using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.S3Storage;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<S3MediaStorage>();
        services.AddSingleton<StrmMetadataCache>();
        services.AddSingleton<ProbeCache>();
        var descriptor = services.Last(d => d.ServiceType == typeof(IMediaSourceManager));
        services.Remove(descriptor);
        services.AddSingleton<IMediaSourceManager>(sp =>
        {
            var inner = (IMediaSourceManager)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            return new CachedStrmSourceManager(inner, sp.GetRequiredService<StrmMetadataCache>());
        });
        var encoderDescriptor = services.Last(d => d.ServiceType == typeof(IMediaEncoder));
        services.Remove(encoderDescriptor);
        services.AddSingleton<IMediaEncoder>(sp =>
        {
            var inner = (IMediaEncoder)(encoderDescriptor.ImplementationInstance ?? encoderDescriptor.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, encoderDescriptor.ImplementationType!));
            return new ProbeCacheMediaEncoder(inner, sp.GetRequiredService<ProbeCache>(), sp.GetRequiredService<S3MediaStorage>(), sp.GetRequiredService<ILogger<ProbeCacheMediaEncoder>>());
        });
        services.AddSingleton<PresignedHlsService>();
        services.AddHostedService(sp => sp.GetRequiredService<PresignedHlsService>());
        services.AddSingleton<WorkerDeployer>();
        services.AddSingleton<StorageSupervisor>();
        services.AddHostedService(sp => sp.GetRequiredService<StorageSupervisor>());
        services.AddScoped<StrmRedirectFilter>();
        services.AddScoped<StorageDeliveryFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<StrmRedirectFilter>(200));
        // Innermost result filter: result filters of other plugins see the original file response first.
        services.Configure<MvcOptions>(options => options.Filters.AddService<StorageDeliveryFilter>(int.MaxValue - 1000));
    }
}
