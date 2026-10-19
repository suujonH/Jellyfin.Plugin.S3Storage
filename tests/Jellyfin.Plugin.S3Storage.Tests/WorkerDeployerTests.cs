using System.Text.Json;
using Jellyfin.Plugin.S3Storage.Configuration;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class WorkerDeployerTests : IDisposable
{
    private readonly TempDirectory _state = new();
    private readonly TempDirectory _project = new();

    public void Dispose()
    {
        _state.Dispose();
        _project.Dispose();
    }

    private StorageBucket Bucket(string allowedOrigins) => new()
    {
        WorkerName = "film-delivery", WorkerProjectDirectory = _project.FullName, Endpoint = "https://s3.example", Bucket = "media",
        Region = "auto", AllowedOrigins = allowedOrigins
    };

    [Fact]
    public void WorkerIsConfiguredWithTheBucketsOrigins()
    {
        var config = JsonDocument.Parse(JsonSerializer.Serialize(WorkerDeployer.WranglerConfig(Bucket("https://jellyfin.example.com,http://192.168.1.10:8096")))).RootElement;

        Assert.Equal("film-delivery", config.GetProperty("name").GetString());
        Assert.Equal(Path.Combine(_project.FullName, "src", "index.js"), config.GetProperty("main").GetString());
        var vars = config.GetProperty("vars");
        Assert.Equal("https://jellyfin.example.com,http://192.168.1.10:8096", vars.GetProperty("ALLOWED_ORIGINS").GetString());
        Assert.Equal("https://s3.example", vars.GetProperty("S3_ENDPOINT").GetString());
        Assert.Equal("media", vars.GetProperty("S3_BUCKET").GetString());
        Assert.Equal("auto", vars.GetProperty("S3_REGION").GetString());
    }

    [Fact]
    public void ChangingTheOriginsMakesTheDeploymentOutdated()
    {
        Assert.NotEqual(WorkerDeployer.Fingerprint(Bucket("*")), WorkerDeployer.Fingerprint(Bucket("https://a.example")));
        Assert.Equal(WorkerDeployer.Fingerprint(Bucket("*")), WorkerDeployer.Fingerprint(Bucket("*")));
    }

    [Fact]
    public async Task InvalidOriginsAreNeverDeployed()
    {
        var deployer = new WorkerDeployer(new TestPaths(_state.FullName));

        await Assert.ThrowsAsync<ArgumentException>(() => deployer.DeployAsync(Bucket("https://a.example/path"), dryRun: true, CancellationToken.None));
    }
}
