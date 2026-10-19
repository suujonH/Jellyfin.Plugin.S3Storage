using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.S3Storage.Tests;

/// <summary>A scratch directory that is removed together with the test that owns it.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Directory.CreateDirectory(FullName);

    public string FullName { get; } = Path.Combine(Path.GetTempPath(), "jellyfin-s3-tests-" + Guid.NewGuid().ToString("N"));

    public string Combine(params string[] parts) => Path.Combine([FullName, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(FullName, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Application paths below a scratch directory; only the folders the plugin uses are distinguished.</summary>
internal sealed class TestPaths(string root) : IApplicationPaths
{
    public string ProgramDataPath => Path.Combine(root, "programdata");
    public string WebPath => Path.Combine(root, "web");
    public string ProgramSystemPath => Path.Combine(root, "system");
    public string DataPath => Path.Combine(root, "data");
    public string ImageCachePath => Path.Combine(root, "cache", "images");
    public string PluginsPath => Path.Combine(root, "plugins");
    public string PluginConfigurationsPath => Path.Combine(root, "plugin-configurations");
    public string LogDirectoryPath => Path.Combine(root, "log");
    public string ConfigurationDirectoryPath => Path.Combine(root, "config");
    public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");
    public string CachePath => Path.Combine(root, "cache");
    public string TempDirectory => Path.Combine(root, "temp");
    public string VirtualDataPath => "%AppDataPath%";
    public string TrickplayPath => Path.Combine(root, "data", "trickplay");
    public string BackupPath => Path.Combine(root, "data", "backups");

    public void MakeSanityCheckOrThrow() => throw new NotSupportedException();

    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false) => throw new NotSupportedException();
}

internal static class Base64Url
{
    public static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
}

internal static class Samples
{
    /// <summary>A probe result with the kinds of members a real one carries (streams, chapters, dates, enums, dictionaries).</summary>
    public static MediaInfo Film() => new()
    {
        Container = "matroska,webm",
        Size = 123456789,
        Bitrate = 8_000_000,
        RunTimeTicks = 72_000_000_000,
        Name = "Film",
        Timestamp = TransportStreamTimestamp.None,
        Formats = ["matroska", "webm"],
        PremiereDate = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        ProductionYear = 2020,
        Artists = ["Artist"],
        Chapters = [new ChapterInfo { Name = "One", StartPositionTicks = 0 }, new ChapterInfo { Name = "Two", StartPositionTicks = 36_000_000_000 }],
        ProviderIds = { ["Imdb"] = "tt0000001" },
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Profile = "High", Width = 1920, Height = 1080, AverageFrameRate = 23.976f, PixelFormat = "yuv420p", BitRate = 7_500_000, IsDefault = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, Language = "eng", SampleRate = 48000, IsDefault = true }
        ]
    };

    public static string Json(object value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
}
