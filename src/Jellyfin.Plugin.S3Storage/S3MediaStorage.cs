using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Plugin.S3Storage.Configuration;

namespace Jellyfin.Plugin.S3Storage;

public sealed class S3MediaStorage
{
    private static readonly PluginConfiguration Disabled = new();

    private readonly PresignedHlsService _hls;
    private readonly Func<PluginConfiguration> _configuration;

    // This runs for every probe and every file response of the server, so a plugin object that does not exist (yet) must
    // mean "nothing is mapped" rather than an exception.
    public S3MediaStorage(PresignedHlsService hls) : this(hls, static () => Plugin.Instance?.Configuration ?? Disabled) { }

    // The server always reads the live plugin configuration; tests supply their own.
    internal S3MediaStorage(PresignedHlsService hls, Func<PluginConfiguration> configuration)
    {
        _hls = hls;
        _configuration = configuration;
    }

    public PluginConfiguration Configuration => _configuration();

    public StorageMapping? Find(string localPath) => Configuration.Enabled
        ? StorageMapping.Find(Configuration.Buckets, localPath) : null;

    public bool Contains(string localPath) => Find(localPath) is not null;

    public static AmazonS3Client CreateClient(StorageBucket bucket) => new(
        new BasicAWSCredentials(bucket.AccessKey, bucket.SecretKey),
        new AmazonS3Config { ServiceURL = bucket.Endpoint, AuthenticationRegion = bucket.Region, ForcePathStyle = true });

    public async Task<Uri?> GetReadUrlAsync(string localPath, DateTimeOffset expiresAt, string? scopeDirectory, CancellationToken cancellationToken)
    {
        var mapping = Find(localPath);
        if (mapping is null || mapping.Bucket.DeliveryMode == DeliveryMode.Private) return null;
        var bucket = mapping.Bucket;
        if (bucket.DeliveryMode == DeliveryMode.Worker)
        {
            var scope = scopeDirectory is null ? mapping : Find(scopeDirectory);
            if (scope is null || scope.Bucket != bucket) throw new ArgumentException("播放范围必须位于同一个存储映射内。");
            var prefix = scopeDirectory is null ? mapping.Key : scope.Key.Length == 0 ? "" : scope.Key.TrimEnd('/') + "/";
            if (!mapping.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("媒体文件位于播放范围之外。");
            var token = PlaybackToken.Create(prefix, expiresAt, bucket.SigningSecret);
            return new Uri($"{bucket.PublicBaseUrl.TrimEnd('/')}/media/{token}/{StorageMapping.EscapeKey(mapping.Key)}");
        }
        if (bucket.DeliveryMode == DeliveryMode.Public)
            return new Uri($"{bucket.PublicBaseUrl.TrimEnd('/')}/{StorageMapping.EscapeKey(mapping.Key)}");

        if (scopeDirectory is not null && mapping.Key.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var scope = Find(scopeDirectory);
            if (scope is null || scope.Bucket != bucket) throw new ArgumentException("HLS 播放目录必须位于同一存储映射内。");
            return await _hls.GetUrlAsync(bucket, mapping.Key, scope.Key, expiresAt, cancellationToken);
        }

        using var client = CreateClient(bucket);
        return new Uri(await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket.Bucket, Key = mapping.Key, Verb = HttpVerb.GET, Expires = expiresAt.UtcDateTime,
            Protocol = new Uri(bucket.Endpoint).Scheme == "http" ? Protocol.HTTP : Protocol.HTTPS
        }).ConfigureAwait(false));
    }
}
