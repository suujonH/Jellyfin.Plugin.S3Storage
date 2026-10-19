using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>Short-circuit static STRM delivery after authorization, before the server opens the remote stream.</summary>
public sealed class StrmRedirectFilter(
    ILibraryManager library,
    IUserManager users,
    IMediaSourceManager sources) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!Plugin.Instance.Configuration.Enabled || context.Controller is not (VideosController or AudioController)
            || !context.ActionArguments.TryGetValue("static", out var isStatic) || isStatic is not true
            || !context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid itemId)
        {
            await next();
            return;
        }

        // Everything that is not a STRM shortcut is left to Jellyfin. Other files inside public mappings are handed to their public link
        // by the delivery filter once Jellyfin has produced the response.
        var item = library.GetItemById(itemId);
        if (item is null || !item.IsShortcut) { await next(); return; }

        var principal = context.HttpContext.User;
        var userId = principal.GetUserId();
        // Jellyfin's GetUserById throws for the empty id that API keys and anonymous callers carry.
        var user = userId == Guid.Empty ? null : users.GetUserById(userId);
        // API keys are unrestricted in Jellyfin; any other caller needs a user who can see the item and may play media.
        if (!principal.GetIsApiKey())
        {
            if (user is null) { context.Result = new ForbidResult(); return; }
            if (library.GetItemById<BaseItem>(itemId, user) is null) { context.Result = new NotFoundResult(); return; }
            if (!user.HasPermission(PermissionKind.EnableMediaPlayback)) { context.Result = new ForbidResult(); return; }
        }

        var sourceId = context.ActionArguments.TryGetValue("mediaSourceId", out var requestedSource)
            ? requestedSource as string ?? itemId.ToString("N") : itemId.ToString("N");
        var source = await sources.GetMediaSource(item, sourceId, null!, false, context.HttpContext.RequestAborted);
        // Respect Jellyfin's policy that explicitly routes remote originals through the server.
        if (user?.HasPermission(PermissionKind.ForceRemoteSourceTranscoding) == true) { await next(); return; }
        if (source is not null && source.Protocol == MediaProtocol.Http && source.RequiredHttpHeaders.Count == 0 &&
            StorageMapping.Find(Plugin.Instance.Configuration.Buckets, item.Path) is { Bucket.DeliveryMode: not DeliveryMode.Private })
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.Result = new RedirectResult(source.Path);
            return;
        }
        await next();
    }
}
