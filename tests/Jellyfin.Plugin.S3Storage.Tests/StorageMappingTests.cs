using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.S3Storage.Configuration;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class StorageMappingTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "jellyfin-s3-mapping-test");

    [Fact]
    public void MappingUsesDirectoryBoundariesAndMostSpecificMount()
    {
        StorageBucket[] buckets =
        [
            new() { LocalPath = Root, KeyPrefix = "library" },
            new() { LocalPath = Path.Combine(Root, "hls"), KeyPrefix = "encoded" }
        ];
        Assert.Null(StorageMapping.Find(buckets, Root + "-other/movie.mkv"));
        Assert.Null(StorageMapping.Find(buckets, Path.Combine(Root, "..", "movie.mkv")));
        Assert.Equal("library/日本語/movie.mkv", StorageMapping.Find(buckets, Path.Combine(Root, "日本語", "movie.mkv"))!.Key);
        Assert.Equal("encoded/film/master.m3u8", StorageMapping.Find(buckets, Path.Combine(Root, "hls", "film", "master.m3u8"))!.Key);
    }

    [Fact]
    public void TokenHasWorkerCompatiblePayloadAndHmac()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var token = PlaybackToken.Create("film/revision/", DateTimeOffset.FromUnixTimeSeconds(2000000000), secret);
        var parts = token.Split('.');
        var payload = JsonDocument.Parse(Decode(parts[0]));
        Assert.Equal(1, payload.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("film/revision/", payload.RootElement.GetProperty("prefix").GetString());
        Assert.Equal(2000000000, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0])), Decode(parts[1]));
    }

    [Fact]
    public void ObjectUrlEscapesEachSegmentWithoutLosingHierarchy()
    {
        Assert.Equal("movies/a%20b/%23cut%3F.m3u8", StorageMapping.EscapeKey("movies/a b/#cut?.m3u8"));
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
}
