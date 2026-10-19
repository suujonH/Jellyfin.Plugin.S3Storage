using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class ProbeCacheMediaEncoderTests : IDisposable
{
    private readonly TempDirectory _library = new();
    private readonly TempDirectory _elsewhere = new();
    private readonly TempDirectory _state = new();
    private readonly PresignedHlsService _hls = new(NullLogger<PresignedHlsService>.Instance);
    private readonly FakeMediaEncoder _inner = new() { Probe = _ => Samples.Film() };
    private readonly CapturingLogger<ProbeCacheMediaEncoder> _log = new();
    private readonly ProbeCacheMediaEncoder _encoder;

    public ProbeCacheMediaEncoderTests()
    {
        // A private mapping has no public link but still lives on the mount, so its probes are cached.
        var configuration = new PluginConfiguration
        {
            Enabled = true,
            Buckets = [new StorageBucket { LocalPath = _library.FullName, DeliveryMode = DeliveryMode.Private }]
        };
        _encoder = new ProbeCacheMediaEncoder(_inner, new ProbeCache(new TestPaths(_state.FullName)), new S3MediaStorage(_hls, () => configuration), _log);
    }

    public void Dispose()
    {
        _hls.Dispose();
        _library.Dispose();
        _elsewhere.Dispose();
        _state.Dispose();
    }

    private static MediaInfoRequest Request(string? path, bool chapters = false, DlnaProfileType type = DlnaProfileType.Video, MediaProtocol protocol = MediaProtocol.File) => new()
    {
        ExtractChapters = chapters, MediaType = type, MediaSource = new MediaSourceInfo { Path = path, Protocol = protocol }
    };

    private static string Create(TempDirectory directory, string name, int size = 1000)
    {
        var path = directory.Combine(name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string ProbeFolder => Path.Combine(_state.FullName, "data", "s3-storage", "probe");

    [Fact]
    public async Task UnchangedFileIsProbedOnce()
    {
        var path = Create(_library, "film.mkv");

        var first = await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        var second = await _encoder.GetMediaInfo(Request(path), CancellationToken.None);

        Assert.Equal(1, _inner.ProbeCalls);
        Assert.Equal(Samples.Json(first), Samples.Json(second));
        Assert.Single(Directory.GetFiles(ProbeFolder, "*.json", SearchOption.AllDirectories));
        var hit = Assert.Single(_log.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("探测缓存命中", hit.Message);
    }

    [Fact]
    public async Task ChangedFileIsProbedAgain()
    {
        var path = Create(_library, "film.mkv");
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);

        File.WriteAllBytes(path, new byte[2000]);
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        Assert.Equal(2, _inner.ProbeCalls);

        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        Assert.Equal(3, _inner.ProbeCalls);

        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        Assert.Equal(3, _inner.ProbeCalls);
    }

    [Fact]
    public async Task ChapterFlagAndMediaTypeAreProbedSeparately()
    {
        var path = Create(_library, "film.mkv");

        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path, chapters: true), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path, type: DlnaProfileType.Audio), CancellationToken.None);
        Assert.Equal(3, _inner.ProbeCalls);

        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path, chapters: true), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path, type: DlnaProfileType.Audio), CancellationToken.None);
        Assert.Equal(3, _inner.ProbeCalls);
    }

    [Fact]
    public async Task EquivalentSpellingsOfThePathShareOneEntry()
    {
        var path = Create(_library, "film.mkv");

        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(_library.Combine("sub", "..", "film.mkv")), CancellationToken.None);

        Assert.Equal(1, _inner.ProbeCalls);
    }

    [Fact]
    public async Task FilesOutsideTheMappingAreNeverCached()
    {
        var path = Create(_elsewhere, "film.mkv");

        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);

        Assert.Equal(2, _inner.ProbeCalls);
        Assert.False(Directory.Exists(ProbeFolder));
    }

    [Fact]
    public async Task OnlyRegularLocalFilesAreCached()
    {
        var path = Create(_library, "film.mkv");
        var disc = Directory.CreateDirectory(_library.Combine("disc")).FullName;
        MediaInfoRequest[] requests =
        [
            Request(path, protocol: MediaProtocol.Http),
            Request(null),
            Request(""),
            Request(_library.Combine("missing.mkv")),
            Request(disc),
            new MediaInfoRequest { MediaSource = null!, MediaType = DlnaProfileType.Video }
        ];

        foreach (var request in requests)
        {
            await _encoder.GetMediaInfo(request, CancellationToken.None);
            await _encoder.GetMediaInfo(request, CancellationToken.None);
        }

        Assert.Equal(requests.Length * 2, _inner.ProbeCalls);
        Assert.False(Directory.Exists(ProbeFolder));
    }

    [Fact]
    public async Task FailedProbesAreNotCached()
    {
        var path = Create(_library, "film.mkv");
        _inner.Probe = _ => throw new InvalidOperationException("ffprobe failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _encoder.GetMediaInfo(Request(path), CancellationToken.None));
        _inner.Probe = _ => Samples.Film();
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);

        Assert.Equal(2, _inner.ProbeCalls);
    }

    [Fact]
    public async Task CacheWriteFailureOnlyWarns()
    {
        var path = Create(_library, "film.mkv");
        Directory.CreateDirectory(Path.Combine(_state.FullName, "data"));
        // A file where the cache folder belongs makes every write fail.
        File.WriteAllText(Path.Combine(_state.FullName, "data", "s3-storage"), "in the way");

        var info = await _encoder.GetMediaInfo(Request(path), CancellationToken.None);
        await _encoder.GetMediaInfo(Request(path), CancellationToken.None);

        Assert.Equal(Samples.Json(Samples.Film()), Samples.Json(info));
        Assert.Equal(2, _inner.ProbeCalls);
        var warnings = _log.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, warning =>
        {
            Assert.NotNull(warning.Exception);
            Assert.Contains(path, warning.Message);
        });
    }

    [Fact]
    public void EveryOtherMemberIsForwarded()
    {
        Assert.Equal(_inner.EncoderPath, _encoder.EncoderPath);
        Assert.Equal(_inner.ProbePath, _encoder.ProbePath);
        Assert.True(_encoder.SetFFmpegPath());
        Assert.True(_encoder.CanEncodeToAudioCodec("aac"));
        Assert.False(_encoder.CanEncodeToAudioCodec("opus"));
        Assert.True(_encoder.SupportsEncoder("libx264"));
        Assert.Equal("t5", _encoder.GetTimeParameter(5));
        // The inner encoder decides what is unsupported; the decorator adds no behavior of its own.
        Assert.Throws<NotSupportedException>(() => _encoder.EncoderVersion);
        Assert.Throws<NotSupportedException>(() => _encoder.CanExtractSubtitles("srt"));
        Assert.Throws<NotSupportedException>(() => _encoder.GetInputArgument("a.mkv", new MediaSourceInfo()));

        _encoder.Dispose();
        Assert.True(_inner.Disposed);
    }
}
