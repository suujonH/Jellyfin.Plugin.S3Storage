using Jellyfin.Plugin.S3Storage.Configuration;

namespace Jellyfin.Plugin.S3Storage;

public sealed record StorageMapping(StorageBucket Bucket, string Key)
{
    public static StorageMapping? Find(IEnumerable<StorageBucket> buckets, string localPath)
    {
        if (!Path.IsPathFullyQualified(localPath)) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(localPath);
        foreach (var bucket in buckets.OrderByDescending(b => b.LocalPath.Length))
        {
            if (string.IsNullOrEmpty(bucket.LocalPath)) continue;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bucket.LocalPath));
            if (!fullPath.Equals(root, comparison) && !fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison)) continue;
            var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            var key = string.Join('/', new[] { bucket.KeyPrefix.Trim('/'), relative == "." ? "" : relative }.Where(s => s.Length != 0));
            return new(bucket, key);
        }
        return null;
    }

    public static void Validate(StorageBucket bucket)
    {
        if (!Uri.TryCreate(bucket.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("请输入有效的 S3 HTTP(S) 服务地址。");
        if (string.IsNullOrWhiteSpace(bucket.Bucket) || !Path.IsPathFullyQualified(bucket.LocalPath))
            throw new ArgumentException("存储桶名称和本地绝对路径不能为空。");
        if (string.IsNullOrWhiteSpace(bucket.AccessKey) || string.IsNullOrWhiteSpace(bucket.SecretKey))
            throw new ArgumentException("请配置 S3 Access Key 和 Secret Key。");
        if (bucket.KeyPrefix.Split('/').Any(s => s is "." or ".."))
            throw new ArgumentException("初始路径不能包含 . 或 ..。");
        if (bucket.DeliveryMode is DeliveryMode.Worker or DeliveryMode.Public)
        {
            if (!Uri.TryCreate(bucket.PublicBaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Query.Length != 0 || url.Fragment.Length != 0)
                throw new ArgumentException("请输入不带查询参数的 HTTP(S) 公开地址。");
        }
        if (bucket.DeliveryMode == DeliveryMode.Worker && System.Text.Encoding.UTF8.GetByteCount(bucket.SigningSecret) < 32)
            throw new ArgumentException("Worker 签名密钥至少需要 32 字节。");
        if (bucket.MountWithS3fs && (bucket.AccessKey.Contains(':') || bucket.AccessKey.Contains('\n') || bucket.SecretKey.Contains('\n') || !Path.IsPathFullyQualified(bucket.S3fsExecutable)))
            throw new ArgumentException("S3FS 凭据不能包含换行或 Access Key 冒号，程序路径必须为绝对路径。");
        if (bucket.AutoDeployWorker && (!Path.IsPathFullyQualified(bucket.WorkerProjectDirectory) || bucket.CloudflareAccountId.Length == 0 || bucket.CloudflareApiToken.Length == 0))
            throw new ArgumentException("自动部署需要 Worker 项目绝对路径、Cloudflare Account ID 和 API Token。");
        if (bucket.AutoDeployWorker && !System.Text.RegularExpressions.Regex.IsMatch(bucket.WorkerName, "^[a-z0-9][a-z0-9-]{0,62}$"))
            throw new ArgumentException("Worker 名称只能包含小写字母、数字和短横线，最多 63 个字符。");
        ValidateAllowedOrigins(bucket.AllowedOrigins);
    }

    /// <summary>Accepts <c>*</c>, or comma separated origins exactly as a browser sends them (scheme, host and non-default port, no path).</summary>
    public static void ValidateAllowedOrigins(string? value)
    {
        if (value is not null && (value.Trim() == "*" || value.Split(',').All(item => IsOrigin(item.Trim())))) return;
        throw new ArgumentException("允许的来源应为 *，或用逗号分隔的 http(s) 来源（不带路径），例如 https://jellyfin.example.com。");
    }

    // Browsers compare the Origin header literally, so only the canonical spelling can ever match.
    private static bool IsOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && origin == uri.GetLeftPart(UriPartial.Authority);

    public static string EscapeKey(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
}
