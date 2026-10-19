using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>
/// Keeps ffprobe results of files in a mapping on disk, so refreshing metadata does not read them through the mount again.
/// It lives in the data folder: Jellyfin purges the cache folder periodically.
/// </summary>
public sealed class ProbeCache(IApplicationPaths paths)
{
    private const int CurrentVersion = 1;

    public sealed record Entry(string Path, long Length, long ModifiedTicks, MediaInfo Info, int Version = CurrentVersion);

    /// <summary>Identifies one probe request; the file's size and modification time are checked separately when reading.</summary>
    public static string Key(string fullPath, bool chapters, DlnaProfileType type) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}|{chapters}|{type}")));

    internal string FilePath(string key) => Path.Combine(paths.DataPath, "s3-storage", "probe", key[..2], key + ".json");

    /// <summary>Returns the cached result when it was recorded for exactly this file state; otherwise null.</summary>
    public MediaInfo? Read(string key, string path, long length, long ticks)
    {
        var file = FilePath(key);
        if (!File.Exists(file)) return null;
        Entry? entry;
        try
        {
            // Share delete access so a concurrent Write can replace the file while it is being read.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            entry = JsonSerializer.Deserialize<Entry>(stream, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            Discard(file);
            return null;
        }
        if (entry?.Info is null) { Discard(file); return null; }
        return entry.Version == CurrentVersion && entry.Path == path && entry.Length == length && entry.ModifiedTicks == ticks ? entry.Info : null;
    }

    /// <summary>Stores an entry atomically, so a concurrent reader never sees a partial file.</summary>
    public void Write(string key, Entry entry)
    {
        var file = FilePath(key);
        var content = JsonSerializer.SerializeToUtf8Bytes(entry, JsonDefaults.Options);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            Discard(temporary);
            throw;
        }
    }

    private static void Discard(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
