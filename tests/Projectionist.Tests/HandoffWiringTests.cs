using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Api;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Providers;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Jellyfin.Plugin.Projectionist.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>F9 (server part): /Upcoming, the provider's handoff wiring and the startup filter.</summary>
public class HandoffWiringTests
{
    private sealed class FakeRegistry : IFeatureHandoffRegistry
    {
        public List<(Guid User, string Device, Guid? Current)> Lookups { get; } = new();

        public List<(HttpContext? Context, BaseItem Feature, User User, IReadOnlyList<Guid> Intros)> Resolved { get; } = new();

        public UpcomingFeatureInfo? Answer { get; set; }

        public bool Throw { get; set; }

        public void OnIntrosResolved(HttpContext? httpContext, BaseItem feature, User user, IReadOnlyList<Guid> introItemIds)
        {
            Resolved.Add((httpContext, feature, user, introItemIds));
            if (Throw)
            {
                throw new InvalidOperationException("registry down");
            }
        }

        public UpcomingFeatureInfo? GetUpcoming(Guid userId, string deviceId, Guid? currentItemId)
        {
            Lookups.Add((userId, deviceId, currentItemId));
            return Answer;
        }
    }

    private sealed class FakeWarmer : IFeatureWarmer
    {
        public List<BaseItem> Warmed { get; } = new();

        public bool Throw { get; set; }

        public void WarmInBackground(BaseItem feature)
        {
            Warmed.Add(feature);
            if (Throw)
            {
                throw new InvalidOperationException("warmer down");
            }
        }
    }

    private static UpcomingController Controller(FakeRegistry registry, bool withClaims = true)
    {
        var ctx = new DefaultHttpContext();
        if (withClaims)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim("Jellyfin-UserId", User1.ToString("N")),
                    new Claim("Jellyfin-DeviceId", Device),
                    new Claim("Jellyfin-Token", Token),
                },
                "CustomAuthentication"));
        }

        return new UpcomingController(registry) { ControllerContext = new ControllerContext { HttpContext = ctx } };
    }

    // ------------------------------------------------------------------ UpcomingController

    [Fact]
    public void UpcomingPassesTheCallersClaimsAndCurrentItemToTheRegistry()
    {
        var registry = new FakeRegistry();
        var c = Controller(registry);
        Assert.IsType<NoContentResult>(c.GetUpcoming(Intro1.ToString("D")));
        Assert.Equal((User1, Device, (Guid?)Intro1), Assert.Single(registry.Lookups));
        Assert.Equal("no-store", c.Response.Headers.CacheControl.ToString());

        Assert.IsType<NoContentResult>(c.GetUpcoming(null));
        Assert.Null(registry.Lookups[^1].Current);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UpcomingRejectsABadCurrentItemId(string bad)
    {
        var registry = new FakeRegistry();
        Assert.IsType<BadRequestResult>(Controller(registry).GetUpcoming(bad));
        Assert.Empty(registry.Lookups);
    }

    [Fact]
    public void UpcomingWithoutUserAndDeviceClaimsIsNoContent()
    {
        var registry = new FakeRegistry { Answer = new UpcomingFeatureInfo() };
        Assert.IsType<NoContentResult>(Controller(registry, withClaims: false).GetUpcoming(null));
        Assert.Empty(registry.Lookups);
    }

    [Fact]
    public void UpcomingWritesThePinnedPascalCaseWireFormat()
    {
        var registry = new FakeRegistry
        {
            Answer = new UpcomingFeatureInfo
            {
                FeatureId = Feature.ToString("N"),
                IntroIds = new List<string> { Intro1.ToString("N") },
                PlayMethod = "Transcode",
                Transcode = new TranscodePrefetchInfo { Url = "/videos/x/master.m3u8?a=1&PlaySessionId=p", Ready = true },
            },
        };
        var result = Assert.IsType<ContentResult>(Controller(registry).GetUpcoming(null));
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(
            "{\"FeatureId\":\"" + Feature.ToString("N") + "\",\"IntroIds\":[\"" + Intro1.ToString("N") + "\"],\"PlayMethod\":\"Transcode\","
            + "\"DirectPlay\":null,\"PrefetchSeconds\":8,\"Transcode\":{\"Url\":\"/videos/x/master.m3u8?a=1\\u0026PlaySessionId=p\",\"Ready\":true}}",
            result.Content);

        registry.Answer = new UpcomingFeatureInfo
        {
            FeatureId = "f",
            PlayMethod = "DirectPlay",
            DirectPlay = new DirectPlayInfo { Container = "mp4", MediaSourceId = "m", ETag = null },
        };
        var direct = Assert.IsType<ContentResult>(Controller(registry).GetUpcoming(null));
        Assert.Equal(
            "{\"FeatureId\":\"f\",\"IntroIds\":[],\"PlayMethod\":\"DirectPlay\",\"DirectPlay\":{\"Container\":\"mp4\",\"MediaSourceId\":\"m\",\"ETag\":null},"
            + "\"PrefetchSeconds\":8,\"Transcode\":null}",
            direct.Content);
    }

    // ------------------------------------------------------------------ PrerollIntroProvider wiring

    private static readonly Movie Movie = new() { Id = Feature, Name = "Feature" };
    private static readonly User Viewer = new("test", "auth", "reset") { Id = User1 };

    private static List<IntroInfo> Intros() => new()
    {
        new IntroInfo { Path = "/p/a.mkv", ItemId = Intro1 },
        new IntroInfo { Path = "/p/b.mkv", ItemId = null },
        new IntroInfo { Path = "/p/c.mkv", ItemId = Intro2 },
    };

    [Fact]
    public void ProviderStartsTheWarmAndTheHandoffWhenPrerollsAreReturned()
    {
        var warmer = new FakeWarmer();
        var registry = new FakeRegistry();
        var ctx = new DefaultHttpContext();
        var http = new HttpContextAccessor { HttpContext = ctx };
        PrerollIntroProvider.StartFeatureHandoff(Movie, Viewer, Intros(), new PluginConfiguration { FeaturePreloadMode = FeaturePreloadMode.Hot }, warmer, registry, http, NullLogger.Instance);

        Assert.Same(Movie, Assert.Single(warmer.Warmed));
        var call = Assert.Single(registry.Resolved);
        Assert.Same(ctx, call.Context);
        Assert.Same(Movie, call.Feature);
        Assert.Same(Viewer, call.User);
        Assert.Equal(new[] { Intro1, Intro2 }, call.Intros);
    }

    [Fact]
    public void ProviderDoesNothingWhenOffOrWithoutPrerolls()
    {
        var warmer = new FakeWarmer();
        var registry = new FakeRegistry();
        var http = new HttpContextAccessor();
        PrerollIntroProvider.StartFeatureHandoff(Movie, Viewer, Intros(), new PluginConfiguration { FeaturePreloadMode = FeaturePreloadMode.Off }, warmer, registry, http, NullLogger.Instance);
        PrerollIntroProvider.StartFeatureHandoff(Movie, Viewer, new List<IntroInfo>(), new PluginConfiguration { FeaturePreloadMode = FeaturePreloadMode.Hot }, warmer, registry, http, NullLogger.Instance);
        Assert.Empty(warmer.Warmed);
        Assert.Empty(registry.Resolved);

        // The legacy EnableFeaturePreload flag means Warm.
        PrerollIntroProvider.StartFeatureHandoff(Movie, Viewer, Intros(), new PluginConfiguration { FeaturePreloadMode = FeaturePreloadMode.Off, EnableFeaturePreload = true }, warmer, registry, http, NullLogger.Instance);
        Assert.Single(warmer.Warmed);
        Assert.Single(registry.Resolved);
    }

    [Fact]
    public void ProviderWiringNeverThrowsAndOneFailureDoesNotStopTheOther()
    {
        var warmer = new FakeWarmer { Throw = true };
        var registry = new FakeRegistry { Throw = true };
        PrerollIntroProvider.StartFeatureHandoff(Movie, Viewer, Intros(), new PluginConfiguration { FeaturePreloadMode = FeaturePreloadMode.Warm }, warmer, registry, new HttpContextAccessor(), NullLogger.Instance);
        Assert.Single(warmer.Warmed);
        Assert.Single(registry.Resolved);
    }

    // ------------------------------------------------------------------ FeatureHandoffStartupFilter

    private static (RequestDelegate Pipeline, Func<int> Resolutions, Func<int> InnerCalls) Build(Func<IServiceProvider, FeatureHandoffRegistry> registry)
    {
        var resolutions = 0;
        var inner = 0;
        var services = new ServiceCollection();
        services.AddSingleton(sp =>
        {
            resolutions++;
            return registry(sp);
        });
        services.AddSingleton(_ => new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, null, null, "http://127.0.0.1:1"));
        var provider = services.BuildServiceProvider();

        var filter = new FeatureHandoffStartupFilter(NullLogger<FeatureHandoffStartupFilter>.Instance);
        var app = new ApplicationBuilder(provider);
        filter.Configure(b => b.Run(_ =>
        {
            inner++;
            return Task.CompletedTask;
        }))(app);
        return (app.Build(), () => resolutions, () => inner);
    }

    private static HttpContext PlaybackInfo()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = $"/Items/{Feature:N}/PlaybackInfo";
        ctx.Request.Body = new System.IO.MemoryStream(new byte[] { (byte)'{', (byte)'}' });
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        return ctx;
    }

    [Fact]
    public async Task TheStartupFilterCreatesTheMiddlewareOnTheFirstRequestOnly()
    {
        var (pipeline, resolutions, inner) = Build(_ => new Harness().Registry);
        Assert.Equal(0, resolutions());

        await pipeline(PlaybackInfo());
        await pipeline(PlaybackInfo());
        Assert.Equal(1, resolutions());
        Assert.Equal(2, inner());
    }

    [Fact]
    public async Task TheStartupFilterPassesEverythingThroughWhenTheRegistryCannotBeCreated()
    {
        var (pipeline, resolutions, inner) = Build(_ => throw new InvalidOperationException("no ITranscodeManager"));
        var ctx = PlaybackInfo();
        await pipeline(ctx);
        await pipeline(PlaybackInfo());
        Assert.Equal(2, inner());
        Assert.Equal(1, resolutions());
        Assert.Equal(200, ctx.Response.StatusCode);
    }
}
