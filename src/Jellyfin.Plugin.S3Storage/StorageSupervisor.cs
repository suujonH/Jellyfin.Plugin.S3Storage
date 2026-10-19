using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.S3Storage;

public sealed class StorageSupervisor(IApplicationPaths paths, WorkerDeployer deployer, ILogger<StorageSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MountPollInterval = TimeSpan.FromMilliseconds(500);

    // rwxr-x---: owner and group may use the folder, so it can be checked for emptiness and S3FS can mount over it.
    private const UnixFileMode OpenMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private sealed record Mount(string Fingerprint, Process Process, string PasswordFile);
    private readonly Dictionary<string, Mount> _mounts = new();
    private readonly Dictionary<string, string> _attempts = new();
    private readonly ConcurrentDictionary<string, string> _status = new();
    public IReadOnlyDictionary<string, string> Status => _status;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                var config = Plugin.Instance.Configuration;
                var buckets = config.Enabled ? config.Buckets : [];
                foreach (var path in _mounts.Keys.ToArray())
                {
                    var current = buckets.FirstOrDefault(b => b.LocalPath == path && b.MountWithS3fs);
                    if (current is null || _mounts[path].Fingerprint != WorkerDeployer.Fingerprint(current))
                    {
                        try { await UnmountAsync(path, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { _status[path] = ex.Message; logger.LogError(ex, "Unable to unmount S3FS at {Path}", path); }
                    }
                }
                foreach (var bucket in buckets)
                {
                    var fingerprint = WorkerDeployer.Fingerprint(bucket);
                    if (_attempts.GetValueOrDefault(bucket.LocalPath) == fingerprint) continue;
                    _attempts[bucket.LocalPath] = fingerprint;
                    try
                    {
                        if (bucket.MountWithS3fs && !_mounts.ContainsKey(bucket.LocalPath)) await MountBucketAsync(bucket, fingerprint, ct);
                        if (bucket.DeliveryMode == DeliveryMode.Worker && bucket.AutoDeployWorker && !deployer.IsCurrent(bucket)) await deployer.DeployAsync(bucket, false, ct);
                        if (!bucket.MountWithS3fs) _status[bucket.LocalPath] = "使用外部管理的目录";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _status[bucket.LocalPath] = ex.Message;
                        logger.LogError(ex, "S3 storage administration failed for {Name}", bucket.Name);
                    }
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var path in _mounts.Keys.ToArray())
            {
                try { await UnmountAsync(path, timeout.Token); }
                catch (Exception ex) { logger.LogError(ex, "Unable to unmount S3FS during shutdown at {Path}", path); }
            }
        }
    }

    private async Task MountBucketAsync(StorageBucket bucket, string fingerprint, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("S3FS 托管挂载需要 Linux/FUSE。当前系统请使用外部挂载目录。");
        var directory = Path.GetFullPath(bucket.LocalPath);
        await ReleaseExistingMountAsync(directory, ct);
        PrepareMountPoint(directory);
        try
        {
            _mounts.Add(bucket.LocalPath, await StartS3fsAsync(bucket, fingerprint, directory, ct));
            _status[bucket.LocalPath] = "S3FS 已挂载";
        }
        catch
        {
            // Never leave an empty, readable folder behind: a library scan would take it for a library without files.
            ClosePath(directory);
            throw;
        }
    }

    /// <summary>Releases what a previous S3FS (or a crash of it) left mounted on the directory, or refuses when it is not S3FS territory.</summary>
    private static async Task ReleaseExistingMountAsync(string directory, CancellationToken ct)
    {
        var type = MountInfo.FileSystemType(directory);
        if (type is null) return;
        if (!type.StartsWith("fuse", StringComparison.Ordinal)) throw new InvalidOperationException($"目录已被 {type} 挂载");
        // A lazy unmount follows when the first attempt fails because the directory is still in use.
        foreach (var option in new[] { "-u", "-uz" })
        {
            try { await ManagedProcess.RunAsync(ManagedProcess.StartInfo("fusermount3", [option, "--", directory]), null, ct); }
            catch (InvalidOperationException) { }
            if (MountInfo.FileSystemType(directory) is null) return;
        }
        throw new InvalidOperationException("目录仍被挂载占用");
    }

    /// <summary>Makes the directory usable for a mount: it must exist, be empty and be enumerable.</summary>
    [SupportedOSPlatform("linux")]
    private static void PrepareMountPoint(string directory)
    {
        if (Directory.Exists(directory))
        {
            // A failed earlier attempt closed the folder; it has to be opened to look inside.
            File.SetUnixFileMode(directory, OpenMode);
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                File.SetUnixFileMode(directory, UnixFileMode.None);
                throw new ArgumentException("S3FS 挂载目录必须为空或不存在。");
            }
            return;
        }
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, OpenMode);
    }

    [SupportedOSPlatform("linux")]
    private async Task<Mount> StartS3fsAsync(StorageBucket bucket, string fingerprint, string directory, CancellationToken ct)
    {
        var secretDirectory = Path.Combine(paths.DataPath, "s3-storage", "mounts");
        Directory.CreateDirectory(secretDirectory);
        var passwordFile = Path.Combine(secretDirectory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory))) + ".passwd");
        File.WriteAllText(passwordFile, bucket.AccessKey + ":" + bucket.SecretKey);
        File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var bucketPath = bucket.Bucket + (bucket.KeyPrefix.Length == 0 ? "" : ":/" + bucket.KeyPrefix.Trim('/'));
        var arguments = new List<string> { bucketPath, directory, "-f", "-o", "passwd_file=" + passwordFile, "-o", "url=" + bucket.Endpoint, "-o", "use_path_request_style" };
        if (bucket.MountAllowOther) arguments.AddRange(["-o", "allow_other"]);
        var process = new Process { StartInfo = ManagedProcess.StartInfo(bucket.S3fsExecutable, arguments), EnableRaisingEvents = true };

        // Runs on a thread-pool thread, where an exception would end the server.
        void OnExited(object? sender, EventArgs e)
        {
            try
            {
                _status[bucket.LocalPath] = $"S3FS 已退出（{process.ExitCode.ToString(CultureInfo.InvariantCulture)}）。修改配置后可重试。";
                ClosePath(directory);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Unable to handle the exit of S3FS at {Path}", directory); }
        }

        process.Exited += OnExited;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await WaitForMountAsync(directory, process, ct);
            return new Mount(fingerprint, process, passwordFile);
        }
        catch
        {
            // The caller reports the failure; the exit of the process we end here must not overwrite that report.
            process.Exited -= OnExited;
            Kill(process);
            process.Dispose();
            try { File.Delete(passwordFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task WaitForMountAsync(string directory, Process process, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited) throw new InvalidOperationException($"S3FS 启动失败（退出码 {process.ExitCode.ToString(CultureInfo.InvariantCulture)}）");
            if (MountInfo.FileSystemType(directory) is { } type && type.StartsWith("fuse.s3fs", StringComparison.Ordinal)) return;
            if (elapsed.Elapsed >= MountTimeout) throw new TimeoutException("S3FS 30 秒内未完成挂载");
            await Task.Delay(MountPollInterval, ct);
        }
    }

    private async Task UnmountAsync(string path, CancellationToken ct)
    {
        var mount = _mounts[path];
        if (!mount.Process.HasExited)
        {
            await ManagedProcess.RunAsync(ManagedProcess.StartInfo("fusermount3", ["-u", "--", path]), null, ct);
            await mount.Process.WaitForExitAsync(ct);
        }
        mount.Process.Dispose();
        File.Delete(mount.PasswordFile);
        _mounts.Remove(path); _attempts.Remove(path); _status.TryRemove(path, out _);
        ClosePath(path);
    }

    /// <summary>
    /// Takes all access to a directory that is no longer served by a mount. Jellyfin then fails to read it and keeps its library,
    /// instead of finding an empty folder and removing every item below it. Root is not affected by permissions.
    /// </summary>
    private void ClosePath(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var path = Path.GetFullPath(directory);
            // A directory that is still mounted belongs to the mounted file system, not to us.
            if (MountInfo.FileSystemType(path) is null && Directory.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.None);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to close the unmounted directory {Path}", directory); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
}
