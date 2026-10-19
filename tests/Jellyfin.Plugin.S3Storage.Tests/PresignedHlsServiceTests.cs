using Jellyfin.Plugin.S3Storage.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class PresignedHlsServiceTests
{
    private static readonly Uri Published = new("https://s3.example/published.m3u8");
    private static readonly StorageBucket Bucket = new() { Bucket = "media", Endpoint = "https://s3.example", KeyPrefix = "encoded", DeliveryMode = DeliveryMode.Presigned };

    // Start of a five-minute window one hour ahead; requests inside one window share their manifests.
    private static readonly DateTimeOffset Window = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() / 300 * 300);

    private static PresignedHlsService Create(Func<StorageBucket, string, string, DateTimeOffset, Task<Uri>> publisher) =>
        new(NullLogger<PresignedHlsService>.Instance) { Publisher = publisher };

    private static Task<Uri> Request(PresignedHlsService service, DateTimeOffset expiresAt, CancellationToken ct = default, string key = "encoded/film/index.m3u8") =>
        service.GetUrlAsync(Bucket, key, "encoded/film", expiresAt, ct);

    [Theory]
    [InlineData("", ".jellyfin-delivery/")]
    [InlineData("/", ".jellyfin-delivery/")]
    [InlineData("encoded", "encoded/.jellyfin-delivery/")]
    [InlineData("/a/b/", "a/b/.jellyfin-delivery/")]
    public void TemporaryManifestsLiveInsideTheInitialPath(string keyPrefix, string expected)
    {
        Assert.Equal(expected, PresignedHlsService.DeliveryPrefix(new StorageBucket { KeyPrefix = keyPrefix }));
    }

    [Fact]
    public async Task CallersInOneWindowShareOnePublication()
    {
        var calls = 0;
        using var service = Create((_, _, _, _) => { calls++; return Task.FromResult(Published); });

        Assert.Equal(Published, await Request(service, Window.AddSeconds(10)));
        Assert.Equal(Published, await Request(service, Window.AddSeconds(299)));
        Assert.Equal(1, calls);

        await Request(service, Window.AddSeconds(300));
        await Request(service, Window.AddSeconds(10), key: "encoded/film/other.m3u8");
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PublicationIsNotCancelledByTheCallerThatStartedIt()
    {
        var calls = 0;
        var gate = new TaskCompletionSource<Uri>();
        using var service = Create((_, _, _, _) => { calls++; return gate.Task; });
        using var first = new CancellationTokenSource();

        var starter = Request(service, Window, first.Token);
        var waiter = Request(service, Window);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        gate.SetResult(Published);

        Assert.Equal(Published, await waiter);
        Assert.Equal(Published, await Request(service, Window));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedPublicationIsRemovedSoItCanBeRetried()
    {
        var calls = 0;
        using var service = Create((_, _, _, _) => ++calls == 1 ? Task.FromException<Uri>(new InvalidDataException("unreachable")) : Task.FromResult(Published));

        await Assert.ThrowsAsync<InvalidDataException>(() => Request(service, Window));

        Assert.Equal(Published, await Request(service, Window));
        Assert.Equal(2, calls);
    }
}
