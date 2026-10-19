using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>
/// Answers probe requests for files in a mapping from the persistent <see cref="ProbeCache"/> while the file is unchanged.
/// Every other member, and every other file, is forwarded to the server's own encoder.
/// </summary>
public sealed class ProbeCacheMediaEncoder(IMediaEncoder inner, ProbeCache cache, S3MediaStorage storage, ILogger<ProbeCacheMediaEncoder> logger)
    : IMediaEncoder, IDisposable
{
    public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
    {
        var source = request.MediaSource;
        if (source is not { Protocol: MediaProtocol.File } || string.IsNullOrEmpty(source.Path) || !storage.Contains(source.Path))
            return inner.GetMediaInfo(request, cancellationToken);
        // Disc folders (DVD/BD) are directories, so they are probed every time.
        var file = new FileInfo(source.Path);
        return file.Exists ? ProbeAsync(request, source.Path, file, cancellationToken) : inner.GetMediaInfo(request, cancellationToken);
    }

    private async Task<MediaInfo> ProbeAsync(MediaInfoRequest request, string path, FileInfo file, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var key = ProbeCache.Key(fullPath, request.ExtractChapters, request.MediaType);
        // The state is read before probing: a file that changes meanwhile simply fails the check next time.
        var length = file.Length;
        var ticks = file.LastWriteTimeUtc.Ticks;
        var cached = cache.Read(key, fullPath, length, ticks);
        if (cached is not null)
        {
            logger.LogDebug("探测缓存命中：{Path}", fullPath);
            return cached;
        }

        var result = await inner.GetMediaInfo(request, cancellationToken).ConfigureAwait(false);
        try { cache.Write(key, new ProbeCache.Entry(fullPath, length, ticks, result)); }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to write the probe cache for {Path}", fullPath); }
        return result;
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();

    public string EncoderPath => inner.EncoderPath;
    public string ProbePath => inner.ProbePath;
    public Version EncoderVersion => inner.EncoderVersion;
    public bool IsPkeyPauseSupported => inner.IsPkeyPauseSupported;
    public bool IsVaapiDeviceAmd => inner.IsVaapiDeviceAmd;
    public bool IsVaapiDeviceInteliHD => inner.IsVaapiDeviceInteliHD;
    public bool IsVaapiDeviceInteli965 => inner.IsVaapiDeviceInteli965;
    public bool IsVaapiDeviceSupportVulkanDrmModifier => inner.IsVaapiDeviceSupportVulkanDrmModifier;
    public bool IsVaapiDeviceSupportVulkanDrmInterop => inner.IsVaapiDeviceSupportVulkanDrmInterop;
    public bool IsVideoToolboxAv1DecodeAvailable => inner.IsVideoToolboxAv1DecodeAvailable;

    public bool SupportsEncoder(string encoder) => inner.SupportsEncoder(encoder);
    public bool SupportsDecoder(string decoder) => inner.SupportsDecoder(decoder);
    public bool SupportsHwaccel(string hwaccel) => inner.SupportsHwaccel(hwaccel);
    public bool SupportsFilter(string filter) => inner.SupportsFilter(filter);
    public bool SupportsFilterWithOption(FilterOptionType option) => inner.SupportsFilterWithOption(option);
    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => inner.SupportsBitStreamFilterWithOption(option);

    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken) =>
        inner.ExtractAudioImage(path, imageStreamIndex, cancellationToken);

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken) =>
        inner.ExtractVideoImage(inputFile, container, mediaSource, videoStream, threedFormat, offset, cancellationToken);

    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken) =>
        inner.ExtractVideoImage(inputFile, container, mediaSource, imageStream, imageStreamIndex, targetFormat, cancellationToken);

    public Task<string> ExtractVideoImagesOnIntervalAccelerated(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int maxWidth, TimeSpan interval, bool allowHwAccel, bool enableHwEncoding, int? threads, int? qualityScale, ProcessPriorityClass? priority, bool enableKeyFrameOnlyExtraction, EncodingHelper encodingHelper, CancellationToken cancellationToken) =>
        inner.ExtractVideoImagesOnIntervalAccelerated(inputFile, container, mediaSource, imageStream, maxWidth, interval, allowHwAccel, enableHwEncoding, threads, qualityScale, priority, enableKeyFrameOnlyExtraction, encodingHelper, cancellationToken);

    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => inner.GetInputArgument(inputFile, mediaSource);
    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => inner.GetInputArgument(inputFiles, mediaSource);
    public string GetExternalSubtitleInputArgument(string inputFile) => inner.GetExternalSubtitleInputArgument(inputFile);
    public string GetTimeParameter(long ticks) => inner.GetTimeParameter(ticks);
    public Task ConvertImage(string inputPath, string outputPath) => inner.ConvertImage(inputPath, outputPath);
    public string EscapeSubtitleFilterPath(string path) => inner.EscapeSubtitleFilterPath(path);
    public bool SetFFmpegPath() => inner.SetFFmpegPath();
    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => inner.GetPrimaryPlaylistVobFiles(path, titleNumber);
    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => inner.GetPrimaryPlaylistM2tsFiles(path);
    public string GetInputPathArgument(EncodingJobInfo state) => inner.GetInputPathArgument(state);
    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => inner.GetInputPathArgument(path, mediaSource);
    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => inner.GenerateConcatConfig(source, concatFilePath);

    public bool CanEncodeToAudioCodec(string codec) => inner.CanEncodeToAudioCodec(codec);
    public bool CanEncodeToSubtitleCodec(string codec) => inner.CanEncodeToSubtitleCodec(codec);
    public bool CanExtractSubtitles(string codec) => inner.CanExtractSubtitles(codec);
}
