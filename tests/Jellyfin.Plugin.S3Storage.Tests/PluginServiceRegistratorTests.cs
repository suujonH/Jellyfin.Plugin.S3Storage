using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class PluginServiceRegistratorTests : IDisposable
{
    private readonly TempDirectory _state = new();

    public void Dispose() => _state.Dispose();

    // The server registers these before plugins get their turn; the registrator replaces both descriptors.
    private IServiceCollection ServerServices(Action<IServiceCollection> registerEncoder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IApplicationPaths>(new TestPaths(_state.FullName));
        services.AddSingleton<IMediaSourceManager>(_ => throw new NotSupportedException());
        registerEncoder(services);
        new PluginServiceRegistrator().RegisterServices(services, null!);
        return services;
    }

    [Fact]
    public void EncoderRegisteredByTypeIsDecoratedAndForwarded()
    {
        var services = ServerServices(s => s.AddSingleton<IMediaEncoder, FakeMediaEncoder>());
        using var provider = services.BuildServiceProvider();

        var encoder = provider.GetRequiredService<IMediaEncoder>();

        Assert.IsType<ProbeCacheMediaEncoder>(encoder);
        Assert.Same(encoder, provider.GetRequiredService<IMediaEncoder>());
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", encoder.EncoderPath);
        Assert.Single(services, d => d.ServiceType == typeof(IMediaEncoder));
        Assert.Single(services, d => d.ServiceType == typeof(IMediaSourceManager));
    }

    [Fact]
    public void EncoderRegisteredByInstanceOrFactoryIsDecoratedToo()
    {
        var instance = new FakeMediaEncoder();
        using var byInstance = ServerServices(s => s.AddSingleton<IMediaEncoder>(instance)).BuildServiceProvider();
        using var byFactory = ServerServices(s => s.AddSingleton<IMediaEncoder>(_ => instance)).BuildServiceProvider();

        foreach (var provider in new[] { byInstance, byFactory })
        {
            var encoder = Assert.IsType<ProbeCacheMediaEncoder>(provider.GetRequiredService<IMediaEncoder>());
            encoder.Dispose();
        }

        Assert.True(instance.Disposed);
    }

    [Fact]
    public void StorageIsAPlainSingletonResolvedWithItsPublicConstructor()
    {
        using var provider = ServerServices(s => s.AddSingleton<IMediaEncoder, FakeMediaEncoder>()).BuildServiceProvider();

        var storage = provider.GetRequiredService<S3MediaStorage>();

        Assert.Same(storage, provider.GetRequiredService<S3MediaStorage>());
        Assert.Same(provider.GetRequiredService<ProbeCache>(), provider.GetRequiredService<ProbeCache>());
        Assert.Same(provider.GetRequiredService<StrmMetadataCache>(), provider.GetRequiredService<StrmMetadataCache>());
    }

    [Fact]
    public void BackgroundServicesCanBeCreated()
    {
        using var provider = ServerServices(s => s.AddSingleton<IMediaEncoder, FakeMediaEncoder>()).BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().Select(service => service.GetType()).ToArray();

        Assert.Contains(typeof(PresignedHlsService), hosted);
        Assert.Contains(typeof(StorageSupervisor), hosted);
    }

    [Fact]
    public void DeliveryFilterRunsInnermostAndStrmFilterAt200()
    {
        using var provider = ServerServices(s => s.AddSingleton<IMediaEncoder, FakeMediaEncoder>()).BuildServiceProvider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<ServiceFilterAttribute>().ToArray();

        Assert.Equal(int.MaxValue - 1000, Assert.Single(filters, f => f.ServiceType == typeof(StorageDeliveryFilter)).Order);
        Assert.Equal(200, Assert.Single(filters, f => f.ServiceType == typeof(StrmRedirectFilter)).Order);
        Assert.Equal(2, filters.Length);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<StorageDeliveryFilter>());
    }
}
