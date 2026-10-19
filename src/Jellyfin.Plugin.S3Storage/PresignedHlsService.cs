using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Plugin.S3Storage.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>Publishes temporary, fully signed manifest graphs. Media bytes always travel directly from S3.</summary>
public sealed partial class PresignedHlsService(ILogger<PresignedHlsService> logger) : BackgroundService
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expiry, Task<Uri> Result)> _cache = new();

    // Tests replace the publication; the server always publishes to S3.
    internal Func<StorageBucket, string, string, DateTimeOffset, Task<Uri>>? Publisher { get; init; }

    /// <summary>Folder of the temporary manifests, inside the initial path of the mapping so it stays within what the credentials may use.</summary>
    internal static string DeliveryPrefix(StorageBucket bucket)
    {
        var prefix = bucket.KeyPrefix.Trim('/');
        return prefix.Length == 0 ? ".jellyfin-delivery/" : prefix + "/.jellyfin-delivery/";
    }

    public async Task<Uri> GetUrlAsync(StorageBucket bucket, string key, string scope, DateTimeOffset expiresAt, CancellationToken ct)
    {
        // Reuse manifests for requests in the same five-minute window, without extending the requested grant.
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds() / 300 * 300);
        var cacheKey = WorkerDeployer.Fingerprint(bucket) + ":" + key + ":" + expiry.ToUnixTimeSeconds();
        foreach (var expired in _cache.Where(p => p.Value.Expiry < DateTimeOffset.UtcNow)) _cache.TryRemove(expired.Key, out _);
        var publish = Publisher ?? PublishAsync;
        // The publication is shared by every caller of this window, so it must not end with the request that started it.
        var entry = _cache.GetOrAdd(cacheKey, _ => (expiry, publish(bucket, key, scope.TrimEnd('/') + "/", expiry)));
        try { return await entry.Result.WaitAsync(ct); }
        // A caller that gives up leaves the publication running; only a publication that failed is dropped.
        catch when (entry.Result.IsFaulted || entry.Result.IsCanceled)
        {
            _cache.TryRemove(new KeyValuePair<string, (DateTimeOffset Expiry, Task<Uri> Result)>(cacheKey, entry));
            throw;
        }
    }

    private static async Task<Uri> PublishAsync(StorageBucket bucket, string rootKey, string scope, DateTimeOffset expiry)
    {
        using var timeout = new CancellationTokenSource(PublishTimeout);
        var ct = timeout.Token;
        using var client = S3MediaStorage.CreateClient(bucket);
        var prefix = DeliveryPrefix(bucket) + expiry.ToUnixTimeSeconds() + "/" + Hash(rootKey) + "/";
        var completed = new Dictionary<string, Uri>();
        var visiting = new HashSet<string>();

        async Task<Uri> Sign(string key) => new(await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket.Bucket, Key = key, Verb = HttpVerb.GET, Expires = expiry.UtcDateTime, Protocol = new Uri(bucket.Endpoint).Scheme == "http" ? Protocol.HTTP : Protocol.HTTPS }));
        async Task<Uri> Resource(string key)
        {
            if (!key.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return await Sign(key);
            if (completed.TryGetValue(key, out var existing)) return existing;
            if (!visiting.Add(key)) throw new InvalidDataException("HLS 清单存在循环引用。");
            using var response = await client.GetObjectAsync(bucket.Bucket, key, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var text = await reader.ReadToEndAsync(ct);
            var lines = new List<string>();
            var baseUri = new Uri("https://storage.invalid/" + StorageMapping.EscapeKey(key));
            async Task<string> Rewrite(string reference)
            {
                var resolved = new Uri(baseUri, reference);
                var target = Uri.UnescapeDataString(resolved.AbsolutePath[1..]);
                if (resolved.Host != baseUri.Host || resolved.Scheme != "https" || !target.StartsWith(scope, StringComparison.Ordinal) || target.Split('/').Any(p => p is "." or ".." || p.Contains('\\')))
                    throw new InvalidDataException("HLS 资源超出当前目录。");
                return (await Resource(target)).AbsoluteUri;
            }
            foreach (var original in text.Split('\n'))
            {
                var line = original.TrimEnd('\r');
                if (line.StartsWith('#'))
                {
                    var matches = UriAttribute().Matches(line);
                    foreach (Match match in matches.Cast<Match>().Reverse())
                    {
                        var group = match.Groups[1];
                        line = line[..group.Index] + await Rewrite(group.Value) + line[(group.Index + group.Length)..];
                    }
                }
                else if (line.Trim().Length != 0) line = await Rewrite(line.Trim());
                lines.Add(line);
            }
            var destination = prefix + Hash(key) + ".m3u8";
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Bucket, Key = destination, ContentBody = string.Join('\n', lines), ContentType = "application/vnd.apple.mpegurl", UseChunkEncoding = false }, ct);
            var url = await Sign(destination);
            visiting.Remove(key); completed[key] = url;
            return url;
        }
        return await Resource(rootKey);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var bucket in Plugin.Instance.Configuration.Buckets.Where(b => b.DeliveryMode == DeliveryMode.Presigned))
            {
                try
                {
                    using var client = S3MediaStorage.CreateClient(bucket);
                    var prefix = DeliveryPrefix(bucket);
                    string? continuation = null;
                    do
                    {
                        var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket.Bucket, Prefix = prefix, ContinuationToken = continuation }, stoppingToken);
                        foreach (var item in objects.S3Objects ?? [])
                            if (long.TryParse(item.Key[prefix.Length..].Split('/')[0], out var expiry) && expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                                await client.DeleteObjectAsync(bucket.Bucket, item.Key, stoppingToken);
                        continuation = objects.IsTruncated == true ? objects.NextContinuationToken : null;
                    } while (continuation is not null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Unable to clean expired HLS manifests in {Bucket}", bucket.Name); }
            }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    [GeneratedRegex("\\bURI=\"([^\"]*)\"")]
    private static partial Regex UriAttribute();
}
