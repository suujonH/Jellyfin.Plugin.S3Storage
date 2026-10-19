using System.Text.Json;
using Jellyfin.Plugin.S3Storage.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class StorageDeliveryFilterTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly TempDirectory _elsewhere = new();
    private readonly PresignedHlsService _hls = new(NullLogger<PresignedHlsService>.Instance);

    public void Dispose()
    {
        _hls.Dispose();
        _root.Dispose();
        _elsewhere.Dispose();
    }

    private StorageBucket Bucket(DeliveryMode mode = DeliveryMode.Worker) => new()
    {
        LocalPath = _root.FullName, KeyPrefix = "encoded", DeliveryMode = mode,
        PublicBaseUrl = "https://worker.example", SigningSecret = "0123456789abcdef0123456789abcdef"
    };

    private async Task<(ResultExecutingContext Context, int Continued)> RunAsync(
        IActionResult result, StorageBucket? bucket = null, string method = "GET", bool enabled = true, Action<HttpResponse>? prepare = null)
    {
        var configuration = new PluginConfiguration { Enabled = enabled, PlaybackTokenHours = 24, Buckets = [bucket ?? Bucket()] };
        var storage = new S3MediaStorage(_hls, () => configuration);
        var filter = new StorageDeliveryFilter(storage, NullLogger<StorageDeliveryFilter>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        prepare?.Invoke(http.Response);
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var filters = new List<IFilterMetadata>();
        var context = new ResultExecutingContext(action, filters, result, new object());
        var continued = 0;
        await filter.OnResultExecutionAsync(context, () =>
        {
            continued++;
            return Task.FromResult(new ResultExecutedContext(action, filters, context.Result, new object()));
        });
        return (context, continued);
    }

    private static string CacheControl(ResultExecutingContext context) => context.HttpContext.Response.Headers.CacheControl.ToString();

    private static string TokenPrefix(string url)
    {
        var token = new Uri(url).AbsolutePath.Split('/')[2];
        return JsonDocument.Parse(Base64Url.Decode(token.Split('.')[0])).RootElement.GetProperty("prefix").GetString()!;
    }

    [Fact]
    public async Task MappedFileBecomesARedirectCachedUntilTheLinkHourEnds()
    {
        var (context, continued) = await RunAsync(new PhysicalFileResult(_root.Combine("film.mkv"), "video/mp4"));

        var redirect = Assert.IsType<RedirectResult>(context.Result);
        Assert.StartsWith("https://worker.example/media/", redirect.Url);
        Assert.EndsWith("/encoded/film.mkv", redirect.Url);
        Assert.Equal(1, continued);
        var cacheControl = CacheControl(context);
        Assert.StartsWith("private, max-age=", cacheControl);
        Assert.InRange(int.Parse(cacheControl["private, max-age=".Length..]), 1, 3600);
    }

    [Fact]
    public async Task PlaylistRedirectIsNotCachedAndGrantsItsDirectory()
    {
        var (context, _) = await RunAsync(new PhysicalFileResult(_root.Combine("film", "video", "index.M3U8"), "application/vnd.apple.mpegurl"));

        var redirect = Assert.IsType<RedirectResult>(context.Result);
        Assert.Equal("private, no-store", CacheControl(context));
        Assert.Equal("encoded/film/video/", TokenPrefix(redirect.Url));
    }

    [Fact]
    public async Task HeadRequestsAreRedirectedToo()
    {
        var (context, _) = await RunAsync(new PhysicalFileResult(_root.Combine("film.mkv"), "video/mp4"), method: "HEAD");

        Assert.IsType<RedirectResult>(context.Result);
    }

    [Fact]
    public async Task PublicMappingRedirectsToTheBucketUrl()
    {
        var bucket = Bucket(DeliveryMode.Public);
        bucket.PublicBaseUrl = "https://cdn.example/files";

        var (context, _) = await RunAsync(new PhysicalFileResult(_root.Combine("film.mkv"), "video/mp4"), bucket);

        Assert.Equal("https://cdn.example/files/encoded/film.mkv", Assert.IsType<RedirectResult>(context.Result).Url);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task OnlyGetAndHeadAreRedirected(string method)
    {
        var original = new PhysicalFileResult(_root.Combine("film.mkv"), "video/mp4");

        var (context, continued) = await RunAsync(original, method: method);

        Assert.Same(original, context.Result);
        Assert.Equal(1, continued);
    }

    [Fact]
    public async Task OtherResultsAreLeftAlone()
    {
        IActionResult[] results =
        [
            new NotFoundResult(), new ContentResult { Content = "x" }, new FileContentResult([1, 2, 3], "video/mp4"),
            new FileStreamResult(new MemoryStream([1, 2, 3]), "video/mp4")
        ];
        foreach (var original in results)
        {
            var (context, continued) = await RunAsync(original);

            Assert.Same(original, context.Result);
            Assert.Equal(1, continued);
            Assert.Empty(CacheControl(context));
        }
    }

    [Fact]
    public async Task FilesOutsideAnyMappingStayWithTheServer()
    {
        var original = new PhysicalFileResult(_elsewhere.Combine("film.mkv"), "video/mp4");

        var (context, continued) = await RunAsync(original);

        Assert.Same(original, context.Result);
        Assert.Equal(1, continued);
        Assert.Empty(CacheControl(context));
    }

    [Fact]
    public async Task DisabledPluginAndPrivateMappingsStayWithTheServer()
    {
        var original = new PhysicalFileResult(_root.Combine("film.mkv"), "video/mp4");

        Assert.Same(original, (await RunAsync(original, enabled: false)).Context.Result);
        Assert.Same(original, (await RunAsync(original, Bucket(DeliveryMode.Private))).Context.Result);
    }

    [Fact]
    public async Task StreamedFileIsRedirectedAndItsStreamClosed()
    {
        var path = _root.Combine("clip.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);

        var (context, _) = await RunAsync(new FileStreamResult(stream, "video/mp4"));

        Assert.StartsWith("private, max-age=", CacheControl(context));
        Assert.EndsWith("/encoded/clip.mp4", Assert.IsType<RedirectResult>(context.Result).Url);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task StreamedFileOutsideAMappingKeepsItsStream()
    {
        var path = _elsewhere.Combine("clip.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var original = new FileStreamResult(stream, "video/mp4");

        var (context, continued) = await RunAsync(original);

        Assert.Same(original, context.Result);
        Assert.Equal(1, continued);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task RedirectDropsWhatDescribedTheFileAndReplacesCaching()
    {
        var (context, _) = await RunAsync(new PhysicalFileResult(_root.Combine("poster.jpg"), "image/jpeg"), prepare: response =>
        {
            response.ContentType = "image/jpeg";
            response.Headers.ETag = "\"tag\"";
            response.Headers.LastModified = "Mon, 19 Oct 2026 00:00:00 GMT";
            response.Headers.Age = "4000";
            response.Headers.ContentDisposition = "attachment";
            response.Headers.CacheControl = "public, max-age=31536000, immutable";
            response.Headers.Vary = "Accept";
        });

        var headers = context.HttpContext.Response.Headers;
        Assert.IsType<RedirectResult>(context.Result);
        Assert.False(headers.ContainsKey(HeaderNames.ETag));
        Assert.False(headers.ContainsKey(HeaderNames.LastModified));
        Assert.False(headers.ContainsKey(HeaderNames.Age));
        Assert.False(headers.ContainsKey(HeaderNames.ContentDisposition));
        Assert.Null(context.HttpContext.Response.ContentType);
        Assert.StartsWith("private, max-age=", CacheControl(context));
        Assert.Equal("Accept", headers.Vary.ToString());
    }

    [Fact]
    public async Task FailureToCreateALinkFallsBackToTheServer()
    {
        var path = _root.Combine("clip.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var bucket = Bucket();
        bucket.PublicBaseUrl = "not a url";
        var original = new FileStreamResult(stream, "video/mp4");

        var (context, continued) = await RunAsync(original, bucket);

        Assert.Same(original, context.Result);
        Assert.Equal(1, continued);
        Assert.True(stream.CanRead);
        Assert.Empty(CacheControl(context));
    }
}
