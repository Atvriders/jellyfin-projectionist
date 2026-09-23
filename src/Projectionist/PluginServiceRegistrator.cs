using Jellyfin.Plugin.Projectionist.Providers;
using Jellyfin.Plugin.Projectionist.Services;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Jellyfin.Plugin.Projectionist.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Projectionist;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost serverApplicationHost)
    {
        services.AddSingleton<PrerollDiscoveryService>();
        services.AddSingleton<CooldownStore>();
        services.AddSingleton<StatsStore>();
        services.AddSingleton<SessionTracker>();
        services.AddSingleton<HiddenLibraryManager>();
        services.AddSingleton<SeriesPrerollFinder>();
        services.AddSingleton<TrailerFetcher>();
        services.AddSingleton<PrerollSelector>(sp =>
            new PrerollSelector(sp.GetService<CooldownStore>()));
        services.AddSingleton<IIntroProvider, PrerollIntroProvider>();
        services.AddSingleton<IStartupFilter, IndexHtmlInjectionFilter>();

        // Feature handoff: storage warm + transcode pre-start while prerolls play.
        services.AddSingleton<IFeatureWarmer, FeatureWarmer>();
        services.AddSingleton<FeatureHandoffRegistry>();
        services.AddSingleton<IFeatureHandoffRegistry>(sp => sp.GetRequiredService<FeatureHandoffRegistry>());
        services.AddSingleton<IStartupFilter, FeatureHandoffStartupFilter>();
        services.AddSingleton<HandoffLoopbackClient>();
        // (handoff registrations end)
        services.AddHostedService<WebInjector>();
        services.AddHostedService<HideOnStartupService>();
    }
}
