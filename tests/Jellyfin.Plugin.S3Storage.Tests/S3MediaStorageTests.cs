using System.Text.Json;
using Jellyfin.Plugin.S3Storage.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class S3MediaStorageTests : IDisposable
{
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(2000000000);

    private readonly TempDirectory _root = new();
    private readonly PresignedHlsService _hls = new(NullLogger<PresignedHlsService>.Instance);

    public void Dispose()
    {
        _hls.Dispose();
        _root.Dispose();
    }

    private S3MediaStorage Storage(StorageBucket bucket, bool enabled = true)
    {
        var configuration = new PluginConfiguration { Enabled = enabled, Buckets = [bucket] };
        return new(_hls, () => configuration);
    }

    private StorageBucket WorkerBucket(string keyPrefix = "encoded") => new()
    {
        LocalPath = _root.FullName, KeyPrefix = keyPrefix, DeliveryMode = DeliveryMode.Worker,
        PublicBaseUrl = "https://worker.example/", SigningSecret = Secret
    };

    /// <summary>Splits a Worker link /media/{token}/{key} into the prefix granted by its token and the escaped object key.</summary>
    private static (string Prefix, string Key) Decompose(Uri url)
    {
        var segments = url.AbsolutePath.Split('/');
        Assert.Equal(new[] { "", "media" }, segments[..2]);
        var payload = JsonDocument.Parse(Base64Url.Decode(segments[2].Split('.')[0]));
        return (payload.RootElement.GetProperty("prefix").GetString()!, string.Join('/', segments[3..]));
    }

    [Fact]
    public async Task PlaylistGrantCoversItsDirectory()
    {
        var directory = _root.Combine("film", "video", "1080");
        var url = await Storage(WorkerBucket()).GetReadUrlAsync(Path.Combine(directory, "index.m3u8"), Expiry, directory, CancellationToken.None);

        var (prefix, key) = Decompose(url!);
        Assert.Equal("encoded/film/video/1080/", prefix);
        Assert.Equal("encoded/film/video/1080/index.m3u8", key);
    }

    [Fact]
    public async Task PlaylistAtTheMappingRootCoversTheKeyPrefix()
    {
        var url = await Storage(WorkerBucket()).GetReadUrlAsync(_root.Combine("master.m3u8"), Expiry, _root.FullName, CancellationToken.None);

        Assert.Equal("encoded/", Decompose(url!).Prefix);
    }

    [Fact]
    public async Task PlainFileGrantCoversOnlyThatFile()
    {
        var url = await Storage(WorkerBucket()).GetReadUrlAsync(_root.Combine("film", "poster 1.jpg"), Expiry, null, CancellationToken.None);

        var (prefix, key) = Decompose(url!);
        Assert.Equal("encoded/film/poster 1.jpg", prefix);
        Assert.Equal("encoded/film/poster%201.jpg", key);
    }

    [Fact]
    public async Task GrantsAreOnlyIssuedInsideTheMapping()
    {
        var storage = Storage(WorkerBucket());
        var elsewhere = Path.Combine(Path.GetTempPath(), "jellyfin-s3-tests-elsewhere");

        Assert.Null(await storage.GetReadUrlAsync(Path.Combine(elsewhere, "a.mkv"), Expiry, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.GetReadUrlAsync(_root.Combine("a.m3u8"), Expiry, elsewhere, CancellationToken.None));
    }

    [Fact]
    public async Task PublicMappingsLinkStraightToTheObject()
    {
        var bucket = new StorageBucket { LocalPath = _root.FullName, KeyPrefix = "/media/", DeliveryMode = DeliveryMode.Public, PublicBaseUrl = "https://cdn.example/files/" };

        var url = await Storage(bucket).GetReadUrlAsync(_root.Combine("a b", "c.mkv"), Expiry, null, CancellationToken.None);

        Assert.Equal("https://cdn.example/files/media/a%20b/c.mkv", url!.AbsoluteUri);
    }

    [Fact]
    public async Task WithoutAPluginInstanceNothingIsMapped()
    {
        // No test creates the plugin object, so the server's own constructor sees no plugin here.
        var storage = new S3MediaStorage(_hls);
        var path = _root.Combine("a.mkv");

        Assert.False(storage.Configuration.Enabled);
        Assert.Null(storage.Find(path));
        Assert.False(storage.Contains(path));
        Assert.Null(await storage.GetReadUrlAsync(path, Expiry, null, CancellationToken.None));
    }

    [Fact]
    public async Task PrivateAndDisabledMappingsHaveNoLink()
    {
        var path = _root.Combine("a.mkv");
        var privateBucket = new StorageBucket { LocalPath = _root.FullName, DeliveryMode = DeliveryMode.Private };

        Assert.Null(await Storage(privateBucket).GetReadUrlAsync(path, Expiry, null, CancellationToken.None));
        Assert.True(Storage(privateBucket).Contains(path));

        Assert.Null(await Storage(WorkerBucket(), enabled: false).GetReadUrlAsync(path, Expiry, null, CancellationToken.None));
        Assert.False(Storage(WorkerBucket(), enabled: false).Contains(path));
    }
}
