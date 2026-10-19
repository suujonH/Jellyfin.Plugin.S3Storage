using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class StrmMetadataCacheTests : IDisposable
{
    private readonly TempDirectory _state = new();
    private readonly TempDirectory _previous = new();
    private readonly PresignedHlsService _hls = new(NullLogger<PresignedHlsService>.Instance);
    private readonly Movie _item = new()
    {
        Id = Guid.NewGuid(), Path = "/library/film.strm", ShortcutPath = "https://example.com/film.mkv",
        DateModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    public void Dispose()
    {
        _hls.Dispose();
        _state.Dispose();
        _previous.Dispose();
    }

    private StrmMetadataCache Cache(TempDirectory root) =>
        new(new TestPaths(root.FullName), new S3MediaStorage(_hls, () => new PluginConfiguration()));

    private MediaSourceInfo[] Sources() =>
        [new() { Id = _item.Id.ToString("N"), Path = _item.ShortcutPath, Protocol = MediaProtocol.Http, RunTimeTicks = 72_000_000_000 }];

    private string DataFile(TempDirectory root) => Path.Combine(root.FullName, "data", "s3-storage", "strm", _item.Id.ToString("N") + ".json");

    private string CacheFile(TempDirectory root) => Path.Combine(root.FullName, "cache", "s3-storage", "strm", _item.Id.ToString("N") + ".json");

    [Fact]
    public void EntriesLiveInTheDataFolder()
    {
        var cache = Cache(_state);

        cache.Write(_item, Sources());

        Assert.True(File.Exists(DataFile(_state)));
        Assert.False(File.Exists(CacheFile(_state)));
        Assert.Equal(72_000_000_000, Assert.Single(cache.Read(_item)!).RunTimeTicks);
    }

    [Fact]
    public void EntriesOfTheFormerCacheFolderAreAdopted()
    {
        // The previous version wrote below the cache folder; produce such a file by writing elsewhere and moving it there.
        Cache(_previous).Write(_item, Sources());
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(_state))!);
        File.Move(DataFile(_previous), CacheFile(_state));

        var cache = Cache(_state);
        var sources = cache.Read(_item);

        Assert.Equal(72_000_000_000, Assert.Single(sources!).RunTimeTicks);
        Assert.False(File.Exists(CacheFile(_state)));
        Assert.True(File.Exists(DataFile(_state)));
        Assert.NotNull(cache.Read(_item));
    }

    [Fact]
    public void FormerEntryDoesNotReplaceANewerOne()
    {
        var cache = Cache(_state);
        cache.Write(_item, Sources());
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(_state))!);
        File.WriteAllText(CacheFile(_state), "{}");

        Assert.NotNull(cache.Read(_item));
        Assert.True(File.Exists(CacheFile(_state)));
    }

    [Fact]
    public void ChangedItemOrCorruptEntryIsAMiss()
    {
        var cache = Cache(_state);
        cache.Write(_item, Sources());

        _item.DateModified = _item.DateModified.AddMinutes(1);
        Assert.Null(cache.Read(_item));

        File.WriteAllText(DataFile(_state), "not json");
        Assert.Null(cache.Read(_item));
        Assert.False(File.Exists(DataFile(_state)));
    }

    [Fact]
    public void NothingCachedIsAMiss()
    {
        Assert.Null(Cache(_state).Read(_item));
    }
}
