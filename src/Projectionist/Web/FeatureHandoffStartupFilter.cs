using System;
using System.Threading;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// Puts <see cref="HandoffMiddleware"/> in front of Jellyfin's whole pipeline (a startup filter
/// runs before Startup.Configure, so it sits outside app.Map(BaseUrl), forwarded-headers,
/// response compression and auth). The middleware and the registry behind it are created on
/// the first request rather than while the pipeline is being built; if that fails, every
/// request just passes through.
/// </summary>
public sealed class FeatureHandoffStartupFilter : IStartupFilter
{
    private readonly ILogger<FeatureHandoffStartupFilter> _logger;

    public FeatureHandoffStartupFilter(ILogger<FeatureHandoffStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            var services = app.ApplicationServices;
            var middleware = new Lazy<HandoffMiddleware?>(
                () =>
                {
                    try
                    {
                        return new HandoffMiddleware(
                            services.GetRequiredService<FeatureHandoffRegistry>(),
                            services.GetRequiredService<HandoffLoopbackClient>(),
                            _logger);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Projectionist] feature handoff middleware unavailable; requests pass through");
                        return null;
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);

            app.Use(async (context, nextMiddleware) =>
            {
                var m = middleware.Value;
                if (m is null)
                {
                    await nextMiddleware().ConfigureAwait(false);
                    return;
                }

                await m.InvokeAsync(context, nextMiddleware).ConfigureAwait(false);
            });
            next(app);
        };
    }
}
