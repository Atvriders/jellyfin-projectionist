using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Http;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffMiddlewareTests;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffSecurityTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>
/// H: the player is handed the prewarm's exact TranscodingUrl, /Upcoming tells a browser which
/// URL that is and whether its first segment exists, and that session's playlists/segments are
/// served cacheable so the browser's prefetch satisfies the player's requests.
/// </summary>
public class HandoffPrefetchTests
{
    private const string Fresh = FreshPsid;
    private const string Prewarm = PrewarmPsid;

    // Jellyfin's JSON (System.Text.Json, default encoder) escapes '&' in the URL as \u0026.
    private static string EscapedUrl(string psid, string reasons) =>
        ("/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/master.m3u8?&DeviceId=TW96aWxsYS81LjA%3D&MediaSourceId=a9e1d99df22aee602be916311cddf8bf"
         + "&VideoCodec=hevc,h264&AudioStreamIndex=1&VideoBitrate=2847857&SegmentContainer=mp4&PlaySessionId=" + psid
         + "&ApiKey=tok&Tag=d3100dd9035afff7c9dc0cffd4f298f3&TranscodeReasons=" + reasons)
        .Replace("&", "\\u0026", StringComparison.Ordinal);

    private static string Body(string psid, string reasons, int bitrate) =>
        "{\"MediaSources\":[{\"Id\":\"a9e1d99df22aee602be916311cddf8bf\",\"Bitrate\":" + bitrate + ",\"SupportsDirectPlay\":false,"
        + "\"TranscodingUrl\":\"" + EscapedUrl(psid, reasons) + "\",\"TranscodingSubProtocol\":\"hls\"},"
        + "{\"Id\":\"second\",\"TranscodingUrl\":\"/videos/y/master.m3u8?PlaySessionId=" + psid + "\\u0026x=1\"}],"
        + "\"PlaySessionId\":\"" + psid + "\"}";

    // ------------------------------------------------------------------ (1) the URL, verbatim

    [Fact]
    public void HandOverCopiesThePrewarmsTranscodingUrlTokenVerbatimAndKeepsTheRestFresh()
    {
        var prewarm = Encoding.UTF8.GetBytes(Body(Prewarm, "VideoCodecNotSupported", 111));
        var fresh = Encoding.UTF8.GetBytes(Body(Fresh, "VideoCodecNotSupported,AudioCodecNotSupported", 222));
        var output = HandoffJson.HandOverToPrewarm(fresh, Fresh, Prewarm, prewarm);
        Assert.NotNull(output);

        var expected = Body(Prewarm, "VideoCodecNotSupported", 222);
        Assert.Equal(expected, Encoding.UTF8.GetString(output!));

        var parsed = HandoffJson.ParsePlaybackInfo(output);
        Assert.Equal(Prewarm, parsed!.PlaySessionId);
        Assert.Equal(HandoffJson.ParsePlaybackInfo(prewarm)!.First!.TranscodingUrl, parsed.First!.TranscodingUrl);
        Assert.Contains("TranscodeReasons=VideoCodecNotSupported", parsed.First.TranscodingUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioCodecNotSupported", parsed.First.TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void HandOverRefusesWhatItCannotDoExactly()
    {
        var prewarm = Encoding.UTF8.GetBytes(Body(Prewarm, "A", 1));
        var fresh = Encoding.UTF8.GetBytes(Body(Fresh, "A", 1));
        Assert.Null(HandoffJson.HandOverToPrewarm(fresh, "ffffffffffffffffffffffffffffffff", Prewarm, prewarm));
        Assert.Null(HandoffJson.HandOverToPrewarm(fresh, Fresh, Prewarm, Encoding.UTF8.GetBytes("{\"MediaSources\":[{\"Id\":\"x\"}],\"PlaySessionId\":\"p\"}")));
        Assert.Null(HandoffJson.HandOverToPrewarm(Encoding.UTF8.GetBytes("{\"MediaSources\":[],\"PlaySessionId\":\"" + Fresh + "\"}"), Fresh, Prewarm, prewarm));
        Assert.Null(HandoffJson.HandOverToPrewarm(Encoding.UTF8.GetBytes("not json"), Fresh, Prewarm, prewarm));
    }

    [Fact]
    public void TheTranscodingUrlTokenIsFoundOnlyInTheFirstMediaSource()
    {
        var json = "{\"x\":{\"TranscodingUrl\":\"nested\"},\"mediaSources\":[{\"id\":\"a\",\"transcodingUrl\":\"/v?a=1\\u0026b\"},{\"TranscodingUrl\":\"/second\"}]}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var token = HandoffJson.FindFirstTranscodingUrlToken(bytes);
        Assert.NotNull(token);
        Assert.Equal("\"/v?a=1\\u0026b\"", Encoding.UTF8.GetString(bytes, token!.Value.Start, token.Value.Length));

        Assert.Null(HandoffJson.FindFirstTranscodingUrlToken(Encoding.UTF8.GetBytes("{\"MediaSources\":[{\"Id\":\"a\"},{\"TranscodingUrl\":\"/second\"}]}")));
        Assert.Null(HandoffJson.FindFirstTranscodingUrlToken(Encoding.UTF8.GetBytes("{\"MediaSources\":[{\"TranscodingUrl\":null}]}")));
        Assert.Null(HandoffJson.FindFirstTranscodingUrlToken(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public async Task ThePlayerIsHandedThePrewarmUrlEvenWhenOnlyTranscodeReasonsDiffer()
    {
        var h = new Harness();
        var prewarmUrl = TranscodeUrl(Prewarm) + "&TranscodeReasons=VideoCodecNotSupported";
        h.Loopback.PlaybackInfoResponse = TranscodeResponse(Prewarm).Replace(TranscodeUrl(Prewarm), prewarmUrl, StringComparison.Ordinal);
        var e = await HotStarted(h);

        var freshUrl = TranscodeUrl(Fresh) + "&TranscodeReasons=VideoCodecNotSupported,AudioCodecNotSupported";
        var fresh = TranscodeResponse(Fresh).Replace(TranscodeUrl(Fresh), freshUrl, StringComparison.Ordinal);
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(fresh));

        Assert.True(e.Consumed);
        var handed = HandoffJson.ParsePlaybackInfo(output)!;
        Assert.Equal(Prewarm, handed.PlaySessionId);
        Assert.Equal(prewarmUrl, handed.First!.TranscodingUrl);
        Assert.Equal(TranscodeResponse(Prewarm).Replace(TranscodeUrl(Prewarm), prewarmUrl, StringComparison.Ordinal), Encoding.UTF8.GetString(output));
    }

    // ------------------------------------------------------------------ (2) Upcoming.Transcode

    [Fact]
    public async Task UpcomingNamesThePrestartedSessionAndWhetherItsFirstSegmentExists()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<HandoffHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loopback.SegmentGate = gate;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Starting, "starting");

        var starting = h.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.Equal("Transcode", starting.PlayMethod);
        Assert.Equal(TranscodeUrl(Prewarm), starting.Transcode!.Url);
        Assert.False(starting.Transcode.Ready);

        h.Loopback.SegmentGate = null;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 100));
        gate.SetResult(new HandoffHttpResponse(200, bytes, bytes.Length, null));
        await Until(() => e.Hot == HandoffHotState.Started, "started");
        var ready = h.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.True(ready.Transcode!.Ready);

        // Exactly the URL the player then receives.
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(Fresh)));
        Assert.Equal(ready.Transcode.Url, HandoffJson.ParsePlaybackInfo(output)!.First!.TranscodingUrl);
    }

    [Fact]
    public async Task UpcomingHasNoTranscodeOutsideHotOrWithoutAPrestart()
    {
        var warm = new Harness { Mode = FeaturePreloadMode.Warm };
        var w = warm.Register();
        warm.Registry.OnIntroStreamRequest(w, "ua");
        warm.Registry.OnIntroPlaybackInfo(w, warm.IntroRequest());
        await Until(() => w.Prewarm is not null, "prewarm");
        var up = warm.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.Equal("Transcode", up.PlayMethod);
        Assert.Null(up.Transcode);

        var direct = new Harness();
        direct.Loopback.PlaybackInfoResponse = DirectResponse(Prewarm);
        var d = direct.Register();
        direct.Registry.OnIntroPlaybackInfo(d, direct.IntroRequest());
        await Until(() => d.Prewarm is not null, "prewarm");
        Assert.Null(direct.Registry.GetUpcoming(User1, Device, Intro1)!.Transcode);

        // Hot, but no player UA yet: nothing started, nothing to prefetch.
        var hot = new Harness();
        var x = hot.Register();
        hot.Registry.OnIntroPlaybackInfo(x, hot.IntroRequest());
        await Until(() => x.Prewarm is not null, "prewarm");
        Assert.Null(hot.Registry.GetUpcoming(User1, Device, Intro1)!.Transcode);
    }

    // ------------------------------------------------------------------ (3) cache headers

    private static async Task<DefaultHttpContext> Get(HandoffMiddleware m, string path, string query, int status = 200, string? innerCacheControl = null)
    {
        var ctx = Request("GET", path);
        ctx.Request.Headers.Remove("Authorization");
        ctx.Request.QueryString = new QueryString(query);
        var response = Startable(ctx);
        await m.InvokeAsync(ctx, () =>
        {
            ctx.Response.StatusCode = status;
            if (innerCacheControl is not null)
            {
                ctx.Response.Headers.CacheControl = innerCacheControl;
            }

            return Task.CompletedTask;
        });
        await response.StartAsync();
        return ctx;
    }

    private static string Q(string psid = Prewarm, string token = Token) => $"?DeviceId={Device}&PlaySessionId={psid}&ApiKey={token}";

    [Theory]
    [InlineData("master.m3u8")]
    [InlineData("main.m3u8")]
    [InlineData("hls1/main/-1.mp4")]
    [InlineData("hls1/main/0.mp4")]
    public async Task ThePrestartedSessionsPlaylistsAndSegmentsAreCacheable(string tail)
    {
        var h = new Harness();
        await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = await Get(m, $"/videos/{Feature:D}/{tail}", Q(), innerCacheControl: "no-cache");
        Assert.Equal(HandoffMiddleware.CacheControl, ctx.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task NothingElseGetsCacheHeaders()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var seg = $"/videos/{Feature:D}/hls1/main/0.mp4";

        Assert.False((await Get(m, seg, Q(), status: 404)).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, seg, Q(psid: Fresh))).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, seg, Q(token: "someone-else"))).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, seg, $"?DeviceId={Device}&PlaySessionId={Prewarm}")).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, $"/videos/{Intro1:D}/hls1/main/0.mp4", Q())).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, $"/videos/{Feature:D}/stream.mp4", Q())).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, $"/videos/{Feature:D}/hls/main/0.ts", Q())).Response.Headers.ContainsKey("Cache-Control"));
        Assert.False((await Get(m, $"/Items/{Feature:N}/Images/Primary", Q())).Response.Headers.ContainsKey("Cache-Control"));

        // Dropped: no longer.
        h.Registry.Drop(e, "test");
        Assert.False((await Get(m, seg, Q())).Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task TheHandedOverSessionStaysCacheableUntilForgotten()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(Fresh)));
        Assert.True(e.Consumed);
        var (m, _) = Create(h);
        var seg = $"/videos/{Feature:D}/hls1/main/3.mp4";
        Assert.Equal(HandoffMiddleware.CacheControl, (await Get(m, seg, Q())).Response.Headers.CacheControl.ToString());

        h.Time.Now += FeatureHandoffRegistry.AdoptedMemory + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.False((await Get(m, seg, Q())).Response.Headers.ContainsKey("Cache-Control"));
    }

    [Theory]
    [InlineData("/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/master.m3u8", true, true)]
    [InlineData("/jf/Videos/a9e1d99df22aee602be916311cddf8bf/main.m3u8", true, true)]
    [InlineData("/videos/a9e1d99df22aee602be916311cddf8bf/hls1/main/0.mp4", true, true)]
    [InlineData("/videos/a9e1d99df22aee602be916311cddf8bf/hls/main/0.ts", true, false)]
    [InlineData("/videos/a9e1d99df22aee602be916311cddf8bf/stream", false, false)]
    [InlineData("/videos/a9e1d99df22aee602be916311cddf8bf/stream.mkv", false, false)]
    [InlineData("/Items/a9e1d99df22aee602be916311cddf8bf/PlaybackInfo", false, false)]
    [InlineData("/videos/a9e1d99df22aee602be916311cddf8bf/hls1/", false, false)]
    public void StreamPathShapes(string path, bool hls, bool cacheable)
    {
        Assert.Equal(hls, HandoffRequestParser.IsHlsStreamPath(path));
        Assert.Equal(cacheable, HandoffRequestParser.IsCacheableHlsPath(path));
    }
}
