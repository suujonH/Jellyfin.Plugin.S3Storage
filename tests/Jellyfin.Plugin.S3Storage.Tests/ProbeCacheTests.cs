using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dlna;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class ProbeCacheTests : IDisposable
{
    private const string Media = "/library/film.mkv";
    private const long Length = 123456789;
    private const long Ticks = 639000000000000000;

    private readonly TempDirectory _state = new();
    private readonly ProbeCache _cache;

    public ProbeCacheTests() => _cache = new ProbeCache(new TestPaths(_state.FullName));

    public void Dispose() => _state.Dispose();

    private static string Key(bool chapters = false, DlnaProfileType type = DlnaProfileType.Video) => ProbeCache.Key(Media, chapters, type);

    private void Store(string key, string path = Media, long length = Length, long ticks = Ticks, int version = 1) =>
        _cache.Write(key, new ProbeCache.Entry(path, length, ticks, Samples.Film(), version));

    [Fact]
    public void UnchangedFileIsAHit()
    {
        Store(Key());

        var info = _cache.Read(Key(), Media, Length, Ticks);

        Assert.NotNull(info);
        Assert.Equal(Samples.Json(Samples.Film()), Samples.Json(info));
        Assert.NotSame(info, _cache.Read(Key(), Media, Length, Ticks));
    }

    [Fact]
    public void NothingCachedIsAMiss()
    {
        Assert.Null(_cache.Read(Key(), Media, Length, Ticks));
    }

    [Fact]
    public void ChangedSizeOrModificationTimeIsAMiss()
    {
        Store(Key());

        Assert.Null(_cache.Read(Key(), Media, Length + 1, Ticks));
        Assert.Null(_cache.Read(Key(), Media, Length, Ticks + 1));
        Assert.NotNull(_cache.Read(Key(), Media, Length, Ticks));
    }

    [Fact]
    public void ChapterFlagAndMediaTypeAreSeparateEntries()
    {
        Store(Key(chapters: false));

        Assert.Null(_cache.Read(Key(chapters: true), Media, Length, Ticks));
        Assert.Null(_cache.Read(Key(type: DlnaProfileType.Audio), Media, Length, Ticks));
        Assert.NotNull(_cache.Read(Key(chapters: false), Media, Length, Ticks));
    }

    [Fact]
    public void EntryForAnotherPathOrVersionIsAMiss()
    {
        Store(Key(), path: "/library/other.mkv");
        Assert.Null(_cache.Read(Key(), Media, Length, Ticks));

        Store(Key(), version: 2);
        Assert.Null(_cache.Read(Key(), Media, Length, Ticks));
        Assert.True(File.Exists(_cache.FilePath(Key())));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Version\":1,\"Path\":\"/library/film.mkv\",\"Length\":12")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":1,\"Path\":\"/library/film.mkv\",\"Length\":1,\"ModifiedTicks\":2,\"Info\":{\"RunTimeTicks\":\"soon\"}}")]
    public void CorruptFilesAreDeletedAndMiss(string content)
    {
        var file = _cache.FilePath(Key());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);

        Assert.Null(_cache.Read(Key(), Media, Length, Ticks));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void UnreadableFileIsAMissAndIsKept()
    {
        // Exclusive handles are only enforced by Windows; elsewhere the file cannot be made unreadable this way.
        if (!OperatingSystem.IsWindows()) return;
        Store(Key());
        var file = _cache.FilePath(Key());

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(_cache.Read(Key(), Media, Length, Ticks));

        Assert.NotNull(_cache.Read(Key(), Media, Length, Ticks));
    }

    [Fact]
    public void EntriesAreWrittenAtomicallyBelowTheDataFolder()
    {
        var key = Key();
        Store(key);
        Store(key, ticks: Ticks + 5);

        var file = Path.Combine(_state.FullName, "data", "s3-storage", "probe", key[..2], key + ".json");
        Assert.Equal(file, _cache.FilePath(key));
        Assert.Equal(new[] { file }, Directory.GetFiles(Path.GetDirectoryName(file)!));
        Assert.Null(_cache.Read(key, Media, Length, Ticks));
        Assert.NotNull(_cache.Read(key, Media, Length, Ticks + 5));
    }

    [Fact]
    public void KeyIsTheSha256OfPathChaptersAndType()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("/library/film.mkv|True|Audio")));

        Assert.Equal(expected, ProbeCache.Key(Media, true, DlnaProfileType.Audio));
        Assert.Equal(64, expected.Length);
        Assert.NotEqual(ProbeCache.Key(Media, false, DlnaProfileType.Audio), ProbeCache.Key(Media, true, DlnaProfileType.Audio));
        Assert.NotEqual(ProbeCache.Key(Media, true, DlnaProfileType.Video), ProbeCache.Key(Media, true, DlnaProfileType.Audio));
        Assert.NotEqual(ProbeCache.Key("/library/other.mkv", true, DlnaProfileType.Audio), ProbeCache.Key(Media, true, DlnaProfileType.Audio));
    }
}
