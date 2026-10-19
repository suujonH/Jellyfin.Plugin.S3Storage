using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.S3Storage.Configuration;

public enum DeliveryMode { Worker, Presigned, Public, Private }

public sealed class StorageBucket
{
    public string Name { get; set; } = "S3";
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public DeliveryMode DeliveryMode { get; set; } = DeliveryMode.Worker;
    public string PublicBaseUrl { get; set; } = "";
    public string SigningSecret { get; set; } = "";
    public bool MountWithS3fs { get; set; }
    public string S3fsExecutable { get; set; } = "/usr/bin/s3fs";
    public bool MountAllowOther { get; set; }
    public string WorkerProjectDirectory { get; set; } = "";
    public string WorkerName { get; set; } = "jellyfin-s3-delivery";
    public string CloudflareAccountId { get; set; } = "";
    public string CloudflareApiToken { get; set; } = "";
    public bool AutoDeployWorker { get; set; }
    public string AllowedOrigins { get; set; } = "*";
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public int PlaybackTokenHours { get; set; } = 24;
    public StorageBucket[] Buckets { get; set; } = [];
}
