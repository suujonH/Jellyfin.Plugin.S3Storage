using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.S3Storage;

public sealed class WorkerDeployer(IApplicationPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1);
    public static string Fingerprint(StorageBucket bucket) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(bucket)));
    private string StateFile(StorageBucket bucket) => Path.Combine(paths.DataPath, "s3-storage", "deploy", bucket.WorkerName + ".state");
    public bool IsCurrent(StorageBucket bucket) => File.Exists(StateFile(bucket)) && File.ReadAllText(StateFile(bucket)) == Fingerprint(bucket);

    /// <summary>The Wrangler configuration of the Worker for a bucket; secrets are uploaded separately.</summary>
    internal static object WranglerConfig(StorageBucket bucket) => new
    {
        name = bucket.WorkerName,
        main = Path.Combine(bucket.WorkerProjectDirectory, "src", "index.js"),
        compatibility_date = "2026-10-01",
        vars = new { S3_ENDPOINT = bucket.Endpoint, S3_BUCKET = bucket.Bucket, S3_REGION = bucket.Region, ALLOWED_ORIGINS = bucket.AllowedOrigins }
    };

    public async Task DeployAsync(StorageBucket bucket, bool dryRun, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(bucket.WorkerName, "^[a-z0-9][a-z0-9-]{0,62}$") || !Path.IsPathFullyQualified(bucket.WorkerProjectDirectory))
            throw new ArgumentException("请配置有效的 Worker 名称与 Worker 项目绝对路径。");
        StorageMapping.ValidateAllowedOrigins(bucket.AllowedOrigins);
        var wrangler = Path.Combine(bucket.WorkerProjectDirectory, "node_modules", "wrangler", "bin", "wrangler.js");
        if (!File.Exists(wrangler)) throw new ArgumentException("Worker 项目中未安装 Wrangler，请先运行 npm ci。");
        if (!dryRun && (bucket.CloudflareApiToken.Length == 0 || bucket.CloudflareAccountId.Length == 0)) throw new ArgumentException("请配置 Cloudflare Account ID 和 API Token。");
        await _gate.WaitAsync(ct);
        var directory = Path.Combine(paths.DataPath, "s3-storage", "deploy");
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(WranglerConfig(bucket)), ct);
            var args = new List<string> { wrangler, "deploy", "--config", configPath, "--keep-vars" };
            if (dryRun) args.AddRange(["--dry-run", "--outdir", Path.Combine(directory, "dry-run")]);
            var start = ManagedProcess.StartInfo("node", args);
            start.Environment["CLOUDFLARE_API_TOKEN"] = bucket.CloudflareApiToken;
            start.Environment["CLOUDFLARE_ACCOUNT_ID"] = bucket.CloudflareAccountId;
            start.Environment["WRANGLER_SEND_METRICS"] = "false";
            await ManagedProcess.RunAsync(start, null, ct);
            if (dryRun) return;
            var secrets = ManagedProcess.StartInfo("node", [wrangler, "secret", "bulk", "--config", configPath]);
            secrets.Environment["CLOUDFLARE_API_TOKEN"] = bucket.CloudflareApiToken;
            secrets.Environment["CLOUDFLARE_ACCOUNT_ID"] = bucket.CloudflareAccountId;
            secrets.Environment["WRANGLER_SEND_METRICS"] = "false";
            await ManagedProcess.RunAsync(secrets, JsonSerializer.Serialize(new { PLAYBACK_SECRET = bucket.SigningSecret, S3_ACCESS_KEY = bucket.AccessKey, S3_SECRET_KEY = bucket.SecretKey }), ct);
            await File.WriteAllTextAsync(StateFile(bucket), Fingerprint(bucket), ct);
        }
        finally { File.Delete(configPath); _gate.Release(); }
    }
}
