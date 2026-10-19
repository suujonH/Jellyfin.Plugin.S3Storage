using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.S3Storage;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;

    public static Plugin Instance { get; private set; } = null!;
    public override Guid Id => Guid.Parse("7637e67a-7fd1-45a2-8b34-e69170cb470f");
    public override string Name => "S3 Storage";
    public override string Description => "S3 存储映射与客户端直连交付。";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "s3storage", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html" },
        new() { Name = "s3storage.js", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.js" }
    ];

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var config = (PluginConfiguration)configuration;
        if (config.PlaybackTokenHours is < 1 or > 168)
            throw new ArgumentException("播放授权有效期应为 1–168 小时。");
        if (config.Enabled)
            foreach (var bucket in config.Buckets) StorageMapping.Validate(bucket);
        base.UpdateConfiguration(configuration);
    }
}
