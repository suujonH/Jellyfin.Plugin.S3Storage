using System.Globalization;
using Jellyfin.Plugin.S3Storage.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>
/// Replaces a file response with a redirect to the file's public link when the file lies in a mapping that offers one.
/// Authorization has already been decided by Jellyfin (and by other plugins' result filters, which run first and still see the
/// original file response), so this filter only changes how the bytes are delivered.
/// </summary>
public sealed class StorageDeliveryFilter(S3MediaStorage storage, ILogger<StorageDeliveryFilter> logger) : IAsyncResultFilter
{
    // Headers an action sets to describe the file it meant to return. On the redirect they would be misleading, and validators
    // would let a client renew a stale redirect target through a 304 instead of asking for a fresh link.
    private static readonly string[] FileHeaders =
        [HeaderNames.ETag, HeaderNames.LastModified, HeaderNames.Age, HeaderNames.ContentDisposition];

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var configuration = storage.Configuration;
        var http = context.HttpContext;
        if (!configuration.Enabled || http.Request.Method is not ("GET" or "HEAD")) { await next(); return; }

        var path = context.Result switch
        {
            PhysicalFileResult physical => physical.FileName,
            FileStreamResult { FileStream: FileStream stream } => stream.Name,
            _ => null
        };
        if (path is null || storage.Find(path) is not { Bucket.DeliveryMode: not DeliveryMode.Private }) { await next(); return; }

        var (expiresAt, maxAge) = DeliveryWindow.Compute(DateTimeOffset.UtcNow, configuration.PlaybackTokenHours);
        var isPlaylist = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        Uri? url;
        try
        {
            url = await storage.GetReadUrlAsync(path, expiresAt, isPlaylist ? Path.GetDirectoryName(path) : null, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The link is an optimization; the server can still deliver the file itself.
            logger.LogWarning(ex, "Unable to create a public link for {Path}; the server delivers the file itself", path);
            await next();
            return;
        }
        if (url is null) { await next(); return; }

        if (context.Result is FileStreamResult { FileStream: { } file }) await file.DisposeAsync();
        foreach (var header in FileHeaders) http.Response.Headers.Remove(header);
        http.Response.ContentType = null;
        http.Response.Headers.CacheControl = isPlaylist ? "private, no-store" : $"private, max-age={maxAge.ToString(CultureInfo.InvariantCulture)}";
        context.Result = new RedirectResult(url.AbsoluteUri);
        await next();
    }
}
