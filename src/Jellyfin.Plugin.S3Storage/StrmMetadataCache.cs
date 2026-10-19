using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.S3Storage;

public sealed class StrmMetadataCache(IApplicationPaths paths, S3MediaStorage storage)
{
    private sealed record Entry(string Path, string Target, DateTime Modified, MediaSourceInfo[] Sources);
    public bool Contains(BaseItem item) => item is Video && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && storage.Contains(item.Path) && !string.IsNullOrWhiteSpace(item.ShortcutPath);

    // The data folder survives: Jellyfin purges the cache folder periodically, which would discard what the initial probe found.
    private string FilePath(Guid itemId) => Path.Combine(paths.DataPath, "s3-storage", "strm", itemId.ToString("N") + ".json");
    private string LegacyFilePath(Guid itemId) => Path.Combine(paths.CachePath, "s3-storage", "strm", itemId.ToString("N") + ".json");

    public MediaSourceInfo[]? Read(BaseItem item)
    {
        var path = FilePath(item.Id);
        if (!File.Exists(path) && !TryMoveLegacy(LegacyFilePath(item.Id), path)) return null;
        try
        {
            var cached = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
            return cached is not null && cached.Path == item.Path && cached.Target == item.ShortcutPath && cached.Modified == item.DateModified ? cached.Sources : null;
        }
        catch (JsonException) { File.Delete(path); return null; }
    }

    public void Write(BaseItem item, IReadOnlyList<MediaSourceInfo> sources)
    {
        var path = FilePath(item.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Entry(item.Path, item.ShortcutPath, item.DateModified, sources.Where(s => Guid.TryParse(s.Id, out var id) && id == item.Id).ToArray())));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Adopts an entry written to the former cache location by an earlier version.</summary>
    private static bool TryMoveLegacy(string legacy, string path)
    {
        if (!File.Exists(legacy)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(legacy, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A concurrent reader may have moved it already.
            return File.Exists(path);
        }
    }
}
