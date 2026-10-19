using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.S3Storage.Tests;

/// <summary>
/// An encoder that counts probe requests and answers a few simple members; everything else throws,
/// so a test notices when code starts depending on more than it should.
/// </summary>
internal sealed class FakeMediaEncoder : IMediaEncoder, IDisposable
{
    public int ProbeCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Func<MediaInfoRequest, MediaInfo> Probe { get; set; } = _ => new MediaInfo();

    public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
    {
        ProbeCalls++;
        return Task.FromResult(Probe(request));
    }

    public void Dispose() => Disposed = true;

    public string EncoderPath => "/usr/lib/jellyfin-ffmpeg/ffmpeg";
    public string ProbePath => "/usr/lib/jellyfin-ffmpeg/ffprobe";
    public bool SetFFmpegPath() => true;
    public bool CanEncodeToAudioCodec(string codec) => codec == "aac";
    public bool SupportsEncoder(string encoder) => encoder == "libx264";
    public string GetTimeParameter(long ticks) => "t" + ticks;

    public Version EncoderVersion => throw new NotSupportedException();
    public bool IsPkeyPauseSupported => throw new NotSupportedException();
    public bool IsVaapiDeviceAmd => throw new NotSupportedException();
    public bool IsVaapiDeviceInteliHD => throw new NotSupportedException();
    public bool IsVaapiDeviceInteli965 => throw new NotSupportedException();
    public bool IsVaapiDeviceSupportVulkanDrmModifier => throw new NotSupportedException();
    public bool IsVaapiDeviceSupportVulkanDrmInterop => throw new NotSupportedException();
    public bool IsVideoToolboxAv1DecodeAvailable => throw new NotSupportedException();

    public bool CanEncodeToSubtitleCodec(string codec) => throw new NotSupportedException();
    public bool CanExtractSubtitles(string codec) => throw new NotSupportedException();
    public bool SupportsDecoder(string decoder) => throw new NotSupportedException();
    public bool SupportsHwaccel(string hwaccel) => throw new NotSupportedException();
    public bool SupportsFilter(string filter) => throw new NotSupportedException();
    public bool SupportsFilterWithOption(FilterOptionType option) => throw new NotSupportedException();
    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => throw new NotSupportedException();
    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<string> ExtractVideoImagesOnIntervalAccelerated(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int maxWidth, TimeSpan interval, bool allowHwAccel, bool enableHwEncoding, int? threads, int? qualityScale, ProcessPriorityClass? priority, bool enableKeyFrameOnlyExtraction, EncodingHelper encodingHelper, CancellationToken cancellationToken) => throw new NotSupportedException();
    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => throw new NotSupportedException();
    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => throw new NotSupportedException();
    public string GetExternalSubtitleInputArgument(string inputFile) => throw new NotSupportedException();
    public Task ConvertImage(string inputPath, string outputPath) => throw new NotSupportedException();
    public string EscapeSubtitleFilterPath(string path) => throw new NotSupportedException();
    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => throw new NotSupportedException();
    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => throw new NotSupportedException();
    public string GetInputPathArgument(EncodingJobInfo state) => throw new NotSupportedException();
    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => throw new NotSupportedException();
    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => throw new NotSupportedException();
}
