using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class HandoffMiddlewareTests
{
    internal static readonly string Auth = "MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\", DeviceId=\"" + Device + "\", Version=\"10.11.11\", Token=\"" + Token + "\"";

    internal static (HandoffMiddleware Middleware, HandoffLoopbackClient Loopback) Create(Harness h)
    {
        var loopback = new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, null, null, "http://127.0.0.1:1");
        return (new HandoffMiddleware(h.Registry, loopback, NullLogger.Instance), loopback);
    }

    /// <summary>What Jellyfin's authentication handler leaves on HttpContext.User for a valid token.</summary>
    internal static System.Security.Claims.ClaimsPrincipal Principal(string deviceId, string token = Token)
        => new(new System.Security.Claims.ClaimsIdentity(
            new[]
            {
                new System.Security.Claims.Claim("Jellyfin-UserId", User1.ToString("N")),
                new System.Security.Claims.Claim("Jellyfin-DeviceId", deviceId),
                new System.Security.Claims.Claim("Jellyfin-Token", token),
            },
            "CustomAuthentication"));

    internal static DefaultHttpContext Request(string method, string path, string? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.Authorization = Auth;
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 test";
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
            ctx.Request.ContentType = "application/json";
        }

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact]
    public async Task ValidLoopbackSecretImpersonatesTheClientAndIsStripped()
    {
        var h = new Harness();
        var (m, loopback) = Create(h);
        var ctx = Request("GET", "/System/Endpoint");
        ctx.Request.Headers[HandoffLoopbackClient.HeaderName] = loopback.FormatHeader(IPAddress.Parse("203.0.113.50"));
        IPAddress? seen = null;
        var headerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            headerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.HeaderName);
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("203.0.113.50"), seen);
        Assert.False(headerSeen);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000;203.0.113.50")]
    [InlineData(";203.0.113.50")]
    [InlineData("203.0.113.50")]
    public async Task ForgedLoopbackSecretGetsNoIpRewrite(string forged)
    {
        var h = new Harness();
        var (m, _) = Create(h);
        var ctx = Request("GET", "/System/Endpoint");
        ctx.Request.Headers[HandoffLoopbackClient.HeaderName] = forged;
        IPAddress? seen = null;
        var headerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            headerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.HeaderName);
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Loopback, seen);
        Assert.False(headerSeen);
    }

    [Fact]
    public async Task IntroPlaybackInfoBodyStaysReadableAndStartsThePrewarm()
    {
        var h = new Harness { Mode = Configuration.FeaturePreloadMode.Warm };
        var e = h.Register();
        var (m, _) = Create(h);
        const string body = "{\"UserId\":\"u\",\"MaxStreamingBitrate\":3000000,\"DeviceProfile\":{\"Name\":\"web\"}}";
        var ctx = Request("POST", $"/jf/items/{Intro1:D}/playbackinfo", body);
        string? readByServer = null;
        await m.InvokeAsync(ctx, async () =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            readByServer = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
        });
        Assert.Equal(body, readByServer);
        await Until(() => e.Prewarm is not null, "prewarm");
        var pbi = Assert.Single(h.Loopback.Requests);
        Assert.Contains("\"MaxStreamingBitrate\":3000000", Encoding.UTF8.GetString(pbi.JsonBody!));
        Assert.Contains(pbi.Headers, kv => kv.Key == "Authorization" && kv.Value == Auth);
    }

    [Fact]
    public async Task IntroStreamRequestTeachesThePlayerUserAgent()
    {
        var h = new Harness();
        var e = h.Register();
        var (m, _) = Create(h);
        var ctx = Request("GET", $"/Videos/{Intro1:N}/stream.mp4");
        ctx.Request.Headers.Remove("Authorization");
        ctx.Request.QueryString = new QueryString($"?Static=true&deviceId={Device}&ApiKey={Token}");
        ctx.Request.Headers.UserAgent = "ExoPlayerLib/1.5.1";
        await m.InvokeAsync(ctx, () => Task.CompletedTask);
        Assert.Equal("ExoPlayerLib/1.5.1", e.PlayerUserAgent);

        // Another token for the same device id teaches nothing.
        var h2 = new Harness();
        var e2 = h2.Register();
        var (m2, _) = Create(h2);
        var ctx2 = Request("GET", $"/Videos/{Intro1:N}/stream.mp4");
        ctx2.Request.Headers.Remove("Authorization");
        ctx2.Request.QueryString = new QueryString($"?deviceId={Device}&ApiKey=other");
        await m2.InvokeAsync(ctx2, () => Task.CompletedTask);
        Assert.Null(e2.PlayerUserAgent);
    }

    [Fact]
    public async Task FeaturePlaybackInfoIsHandedThePrewarmSessionIdentityEncoded()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{\"AudioStreamIndex\":1}");
        ctx.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        var original = ctx.Response.Body;
        string? acceptEncodingSeen = "unset";
        await m.InvokeAsync(ctx, async () =>
        {
            acceptEncodingSeen = ctx.Request.Headers.AcceptEncoding.ToString();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        });

        Assert.Equal(string.Empty, acceptEncodingSeen);
        Assert.Same(original, ctx.Response.Body);
        var sent = Encoding.UTF8.GetString(((MemoryStream)original).ToArray());
        Assert.Equal(TranscodeResponse(PrewarmPsid), sent);
        Assert.Equal(Encoding.UTF8.GetByteCount(sent), ctx.Response.ContentLength);
        Assert.True(e.Consumed);
    }

    [Fact]
    public async Task FeaturePlaybackInfoFromAnotherTokenIsUntouched()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        ctx.Request.Headers.Authorization = Auth.Replace(Token, "someone-else");
        ctx.Request.Headers.AcceptEncoding = "gzip";
        var swapped = false;
        var original = ctx.Response.Body;
        await m.InvokeAsync(ctx, () =>
        {
            swapped = !ReferenceEquals(original, ctx.Response.Body) || ctx.Request.Headers.AcceptEncoding.Count == 0;
            return Task.CompletedTask;
        });
        Assert.False(swapped);
        Assert.False(e.Consumed);
        Assert.False(e.Dropped);
    }

    [Fact]
    public async Task PlaybackInfoForSomethingElseAbandonsThePendingPlay()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Other:N}/PlaybackInfo", "{}");
        await m.InvokeAsync(ctx, () => Task.CompletedTask);
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task FeaturePlaybackInfoAfterItsIntrosDoesNotMakeAReplayLookFeatureFirst()
    {
        // Same device plays the same feature again 12 s later (restart, or back-to-back test reps).
        var h = new Harness { Mode = Configuration.FeaturePreloadMode.Warm };
        var first = h.Register();
        var (m, _) = Create(h);
        await m.InvokeAsync(Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}"), () => Task.CompletedTask);
        Assert.True(first.Dropped);
        h.Time.Now += TimeSpan.FromSeconds(12);
        Assert.False(h.Register().FeatureResolvedFirst);

        // A feature PlaybackInfo with nothing pending (a client that resolves the feature first) still
        // counts - once authentication accepted it (the principal's device claim, a 200).
        var h2 = new Harness();
        var (m2, _) = Create(h2);
        var ctx2 = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        await m2.InvokeAsync(ctx2, () =>
        {
            ctx2.User = Principal(Device);
            ctx2.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        h2.Time.Now += TimeSpan.FromSeconds(1);
        Assert.True(h2.Register().FeatureResolvedFirst);
    }

    [Fact]
    public async Task UnrelatedRequestsPassStraightThrough()
    {
        var h = new Harness();
        var (m, _) = Create(h);
        var ctx = Request("GET", $"/Videos/{Intro1:N}/stream.mp4");
        var called = 0;
        await m.InvokeAsync(ctx, () => { called++; return Task.CompletedTask; });
        Assert.Equal(1, called);
        Assert.False(ctx.Request.Body.CanSeek && ctx.Request.Body.Length > 0);

        var post = Request("POST", "/Sessions/Playing", "{\"x\":1}");
        var before = post.Request.Body;
        await m.InvokeAsync(post, () => { called++; return Task.CompletedTask; });
        Assert.Equal(2, called);
        Assert.Same(before, post.Request.Body);
        Assert.Empty(h.Loopback.Requests);
    }
}
