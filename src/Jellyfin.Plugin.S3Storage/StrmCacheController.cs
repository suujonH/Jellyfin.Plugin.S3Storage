using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.S3Storage;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/S3Storage/Metadata")]
public sealed class StrmCacheController(StrmMetadataCache cache, ILibraryManager library) : ControllerBase
{
    [HttpPost("{itemId:guid}")]
    public IActionResult Import(Guid itemId, MediaSourceInfo source)
    {
        var item = library.GetItemById(itemId);
        if (item is null || !cache.Contains(item)) return NotFound();
        if (source.RunTimeTicks is null or <= 0 || !source.MediaStreams.Any(s => s.Type == MediaStreamType.Video && s.Width > 0 && s.Height > 0))
            return BadRequest("需要视频轨道信息和时长。");
        source.Id = item.Id.ToString("N");
        source.Path = item.ShortcutPath;
        source.SupportsProbing = false;
        cache.Write(item, [source]);
        return NoContent();
    }
}
