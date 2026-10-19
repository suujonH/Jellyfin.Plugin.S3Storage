using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.S3Storage;

public sealed class CachedStrmSourceManager(IMediaSourceManager inner, StrmMetadataCache cache) : IMediaSourceManager
{
    private IReadOnlyList<MediaSourceInfo>? Cached(BaseItem item, User? user)
    {
        if (!cache.Contains(item)) return null;
        var cached = cache.Read(item);
        if (cached is not { Length: > 0 }) return null;
        // Rebuild the accessible version list for this user; only media metadata is cached.
        var sources = inner.GetStaticMediaSources(item, false, user!).Select(current =>
        {
            var saved = cached.FirstOrDefault(s => s.Id == current.Id);
            if (saved is null) return current;
            saved.Path = current.Path;
            saved.Protocol = current.Protocol;
            return saved;
        }).ToArray();
        if (user is not null)
            foreach (var source in sources)
            {
                inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
                source.SupportsTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding);
                source.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
            }
        return sources;
    }
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null) => Cached(item, user) ?? inner.GetStaticMediaSources(item, enablePathSubstitution, user!);
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken ct)
    {
        if (Cached(item, user) is { } cached) return cached;
        var result = await inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct);
        if (cache.Contains(item) && result.Any(s => s.MediaStreams.Any(m => m.Type == MediaStreamType.Video))) cache.Write(item, result);
        return result;
    }
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string sourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken ct)
    {
        var cached = Cached(item, null)?.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        return cached is null ? inner.GetMediaSource(item, sourceId, liveStreamId, enablePathSubstitution, ct) : Task.FromResult(cached);
    }
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => inner.GetMediaStreams(itemId);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => inner.GetMediaAttachments(itemId);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken ct) => inner.OpenLiveStream(request, ct);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken ct) => inner.OpenLiveStreamInternal(request, ct);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken ct) => inner.GetLiveStream(id, ct);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken ct) => inner.GetLiveStreamWithDirectStreamProvider(id, ct);
    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => inner.GetLiveStreamInfoByUniqueId(uniqueId);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken ct) => inner.GetRecordingStreamMediaSources(info, ct);
    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken ct) => inner.GetLiveStreamMediaInfo(id, ct);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    public Task AddMediaInfoWithProbe(MediaSourceInfo source, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken ct) => inner.AddMediaInfoWithProbe(source, isAudio, cacheKey, addProbeDelay, isLiveStream, ct);
}
