using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffMiddlewareTests;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>
/// Keep-alive driven by what the device reports (C1, COMPAT-2, C3, F3), pre-start failure
/// clean-up (C5, F4), the UA wait (F5), kills off the response path (F6), progressive and
/// multi-variant sessions (F7, COMPAT-1) and post-handover supervision (COMPAT-3). Time is a
/// <see cref="ManualTime"/>, so every Task.Delay the registry makes runs on test time.
/// </summary>
public class HandoffLifecycleTests
{
    private static readonly TimeSpan Tick = FeatureHandoffRegistry.PingInterval + TimeSpan.FromSeconds(1);

    /// <summary>
    /// Advance test time, then let the continuations of the fired timers run: a loop that keeps
    /// going re-arms its delay (the pending-timer count comes back); one that ended does not, so
    /// the wait is bounded.
    /// </summary>
    private static async Task Advance(Harness h, TimeSpan by)
    {
        var before = h.Time.PendingTimers;
        h.Time.Advance(by);
        for (var i = 0; i < 50 && h.Time.PendingTimers < before; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(20);
    }

    private static void Progress(Harness h, Guid item, bool paused = false)
        => h.Registry.OnPlaybackReport(User1, Device, item, HandoffPlaybackReport.Progress, paused, false);

    // ------------------------------------------------------------------ keep-alive (C1 / COMPAT-2 / F3)

    [Fact]
    public async Task PingsWhileTheDeviceIsInThePrerollAndNeverAfterTheHandover()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        await Advance(h, Tick);
        await Until(() => h.Transcode.Pinged.Contains(PrewarmPsid), "ping after 21 s");

        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        Assert.Equal(TranscodeResponse(PrewarmPsid), Encoding.UTF8.GetString(output));
        var pings = h.Transcode.Pinged.Count;
        for (var i = 0; i < 4; i++)
        {
            Progress(h, Intro1);
            await Advance(h, Tick);
        }

        Assert.Equal(pings, h.Transcode.Pinged.Count);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task KeepsPingingWhileTheIntroReportsProgressPastTheIdleTimeout()
    {
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(10).Ticks };
        var e = await HotStarted(h);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Start, false, false);
        for (var i = 0; i < 6; i++)
        {
            Progress(h, Intro1);
            await Advance(h, Tick);
        }

        Assert.False(e.Dropped);
        Assert.True(h.Transcode.Pinged.Count >= 5);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task AnAbandonedPrerollStopsBeingPingedAndIsKilled()
    {
        // Tab closed / network lost: no Stopped report, just silence.
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = await HotStarted(h);
        await Advance(h, Tick);
        await Advance(h, Tick);
        Assert.Equal(2, h.Transcode.Pinged.Count);
        await Advance(h, Tick);
        await Advance(h, Tick); // over 60 s without activity
        Assert.True(e.Dropped);
        Assert.InRange(h.Transcode.Pinged.Count, 2, 3);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");

        var pings = h.Transcode.Pinged.Count;
        await Advance(h, Tick);
        await Advance(h, Tick);
        Assert.Equal(pings, h.Transcode.Pinged.Count);
    }

    [Fact]
    public async Task AnAbandonedPrerollIsKilledThoughJellyfinKeepsReportingProgressForIt()
    {
        // rr-C3: a client that vanishes without a Stopped (Roku powered off, lid closed, half-open
        // socket) still gets IsAutomated Progress from SessionInfo's 1 s timer until the intro's
        // runtime. Those say nothing about the device: killed 60-80 s after its last real report.
        var sessions = DispatchProxy.Create<ISessionManager, SessionEvents>();
        var events = (SessionEvents)(object)sessions;
        var h = new Harness(null, sessions) { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var session = new SessionInfo(null!, null!) { UserId = User1, DeviceId = Device };
        var intro = new Movie { Id = Intro1 };
        var e = await HotStarted(h);
        events.Raise("PlaybackStart", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session });
        var lastReal = h.Time.Now;
        for (var i = 0; i < 8 && !e.Dropped; i++)
        {
            events.Raise("PlaybackProgress", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session, IsAutomated = true });
            h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Progress, false, false, isAutomated: true);
            await Advance(h, Tick);
        }

        Assert.True(e.Dropped);
        Assert.InRange((h.Time.Now - lastReal).TotalSeconds, 60, 90);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task ADevicePausedForOverAMinuteIsNoLongerKeptAlive()
    {
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = await HotStarted(h);
        for (var i = 0; i < 4; i++)
        {
            Progress(h, Intro1, paused: true);
            await Advance(h, Tick);
        }

        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task ThePingedLifetimeIsCapped()
    {
        var h = new Harness { IntroTicks = TimeSpan.FromHours(1).Ticks };
        var e = await HotStarted(h);
        var ticks = 0;
        while (!e.Dropped && ticks < 100)
        {
            Progress(h, Intro1);
            await Advance(h, Tick);
            ticks++;
        }

        Assert.True(e.Dropped);
        Assert.InRange(ticks * Tick.TotalMinutes, FeatureHandoffRegistry.MaxPingedLifetime.TotalMinutes, FeatureHandoffRegistry.MaxPingedLifetime.TotalMinutes + 1);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task UserStoppingThePrerollKillsThePrestartWithinSeconds()
    {
        // ReviewCorrectnessProbeTests.AbandonedHotPrestartLivesUntilExpiry, fixed. A stop before the
        // intro's end gets StoppedMidwayGrace (a "next" press asks for the next item right away).
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = await HotStarted(h);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Start, false, false);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: false);
        Assert.True(e.StoppedMidway);
        Assert.Equal(h.Time.Now + FeatureHandoffRegistry.StoppedMidwayGrace, e.NextRequestDeadline);
        h.Time.Now += FeatureHandoffRegistry.StoppedMidwayGrace - TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.False(e.Dropped);
        h.Time.Now += TimeSpan.FromSeconds(2);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
        Assert.Null(h.Registry.GetUpcoming(User1, Device, null));
    }

    [Fact]
    public async Task ANextPressDuringThePrerollStillGetsTheHandover()
    {
        // jellyfin-web's "next" (onPlaybackChanging) reports the intro stopped midway, then asks for
        // the feature's PlaybackInfo.
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = await HotStarted(h);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Start, false, false);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: false);
        h.Time.Now += TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.True(h.Registry.IsAdoptable(e));
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        Assert.Equal(TranscodeResponse(PrewarmPsid), Encoding.UTF8.GetString(output));
        Assert.True(e.Consumed);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task AnIntroEndingWithoutAFollowUpRequestIsAbandonedAfterTheGrace()
    {
        // EnableNextEpisodeAutoPlay=false: the web never asks for the feature after the last intro.
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = await HotStarted(h);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: true);
        h.Time.Now += FeatureHandoffRegistry.NextRequestGrace - TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.False(e.Dropped);
        h.Time.Now += TimeSpan.FromSeconds(2);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task TheNextIntroOfTheChainCancelsTheGrace()
    {
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = h.Register(Intro1, Intro2);
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Started, "pre-start");

        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Start, false, false);
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: true);

        // The client asks for intro 2 through the middleware.
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Intro2:N}/PlaybackInfo", "{}");
        await m.InvokeAsync(ctx, () => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
        h.Registry.OnPlaybackReport(User1, Device, Intro2, HandoffPlaybackReport.Start, false, false);

        // A late Stopped for intro 1 (reports are queued) is stale and changes nothing.
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: false);

        h.Time.Now += FeatureHandoffRegistry.NextRequestGrace + TimeSpan.FromSeconds(5);
        h.Registry.Sweep();
        Assert.False(e.Dropped);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task ALateStoppedForThePreviousIntroAfterTheNextIntrosPlaybackInfoIsStale()
    {
        // rr-C4 (the reviewers' probe, inverted): a "next" press during intro 1; the server
        // delivers intro 1's Stopped only after intro 2's PlaybackInfo (the Stopped event is queued
        // after user-data saves and event consumers), and intro 2 takes over 5 s to start.
        var h = new Harness { IntroTicks = TimeSpan.FromMinutes(12).Ticks };
        var e = h.Register(Intro1, Intro2);
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Started, "pre-start");
        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Start, false, false);

        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Intro2:N}/PlaybackInfo", "{}");
        await m.InvokeAsync(ctx, () => { ctx.Response.StatusCode = 200; return Task.CompletedTask; });
        Assert.Equal(Intro2, e.CurrentIntro);

        h.Registry.OnPlaybackReport(User1, Device, Intro1, HandoffPlaybackReport.Stopped, false, playedToCompletion: false);
        Assert.Null(e.NextRequestDeadline);
        h.Time.Now += FeatureHandoffRegistry.StoppedMidwayGrace + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.False(e.Dropped);
        Assert.Empty(h.Transcode.Killed);

        // A Stopped for intro 2 itself still counts.
        h.Registry.OnPlaybackReport(User1, Device, Intro2, HandoffPlaybackReport.Stopped, false, playedToCompletion: false);
        h.Time.Now += FeatureHandoffRegistry.StoppedMidwayGrace + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
    }

    [Fact]
    public async Task TheDevicesSessionEndingDropsItsPendingPlay()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Registry.OnDeviceSessionEnded(Guid.NewGuid(), Device); // another user's session: nothing
        Assert.False(e.Dropped);
        h.Registry.OnDeviceSessionEnded(User1, Device);
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task ReportsForOtherItemsDevicesOrUsersChangeNothing()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Registry.OnPlaybackReport(User1, Device, Other, HandoffPlaybackReport.Stopped, false, false);
        h.Registry.OnPlaybackReport(User1, "other-device", Intro1, HandoffPlaybackReport.Stopped, false, false);
        h.Registry.OnPlaybackReport(Guid.NewGuid(), Device, Intro1, HandoffPlaybackReport.Stopped, false, false);
        Assert.False(e.Dropped);
    }

    // ------------------------------------------------------------------ mode switches (C3)

    [Fact]
    public async Task SwitchingToOffDropsEverythingAndStopsAdvertising()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Mode = FeaturePreloadMode.Off;
        Assert.Null(h.Registry.GetUpcoming(User1, Device, Intro1));
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
        Assert.False(h.Registry.HasEntries);
    }

    [Fact]
    public async Task SwitchingFromHotToWarmStopsThePingLoop()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Mode = FeaturePreloadMode.Warm;
        await Advance(h, Tick);
        Assert.True(e.Dropped);
        Assert.Empty(h.Transcode.Pinged);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    // ------------------------------------------------------------------ adoption needs a live job (F3)

    [Fact]
    public async Task AStartedJobThatNoLongerExistsIsNotHandedOver()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Transcode.JobExists = false;
        Assert.False(h.Registry.IsAdoptable(e));

        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        var original = ctx.Response.Body;
        await m.InvokeAsync(ctx, async () =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        });
        Assert.Equal(TranscodeResponse(FreshPsid), Encoding.UTF8.GetString(((MemoryStream)original).ToArray()));
        Assert.True(e.Dropped);
        Assert.False(e.Consumed);
    }

    // ------------------------------------------------------------------ pre-start failures (C5 / F4)

    [Fact]
    public async Task ATransportFailureOnTheSegmentDropsAndKillsTwice()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<HandoffHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loopback.SegmentGate = gate;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Starting, "starting");
        gate.SetResult(new HandoffHttpResponse(0, Array.Empty<byte>(), 0, null));

        await Until(() => e.Dropped, "drop");
        await Until(() => h.Transcode.Killed.Count == 1, "first kill");
        await Task.Delay(50);
        await Advance(h, FeatureHandoffRegistry.RekillDelay + TimeSpan.FromSeconds(1));
        await Until(() => h.Transcode.Killed.Count == 2, "second kill");
        Assert.Equal(0, h.Registry.HotSlotsInUse);
    }

    [Fact]
    public async Task AnUnexpectedExceptionDuringThePrestartDropsAndKillsTwice()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<HandoffHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loopback.SegmentGate = gate;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Starting, "starting");
        gate.SetException(new IOException("connection reset mid-body"));

        await Until(() => e.Dropped, "drop");
        await Until(() => h.Transcode.Killed.Count == 1, "first kill");
        await Task.Delay(50);
        await Advance(h, FeatureHandoffRegistry.RekillDelay + TimeSpan.FromSeconds(1));
        await Until(() => h.Transcode.Killed.Count == 2, "second kill");
    }

    [Fact]
    public async Task DropWhileStartingKillsAgainTenSecondsLaterAndWhileStartedOnlyOnce()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource<HandoffHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Loopback.SegmentGate = gate;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Starting, "starting");
        h.Registry.Drop(e, "test");
        await Until(() => h.Transcode.Killed.Count == 1, "first kill");
        await Task.Delay(50);
        Assert.Single(h.Transcode.Killed);
        await Advance(h, FeatureHandoffRegistry.RekillDelay + TimeSpan.FromSeconds(1));
        await Until(() => h.Transcode.Killed.Count == 2, "second kill");

        var h2 = new Harness();
        var e2 = await HotStarted(h2);
        h2.Registry.Drop(e2, "test");
        await Until(() => h2.Transcode.Killed.Count == 1, "kill");
        await Advance(h2, TimeSpan.FromSeconds(30));
        Assert.Single(h2.Transcode.Killed);
    }

    [Fact]
    public async Task ExpiryDuringTheFreshPlaybackInfoIsNotHandedOver()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        var original = ctx.Response.Body;
        await m.InvokeAsync(ctx, async () =>
        {
            h.Time.Now = e.ExpiresAt + TimeSpan.FromSeconds(1);
            h.Registry.Sweep();
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        });
        Assert.Equal(TranscodeResponse(FreshPsid), Encoding.UTF8.GetString(((MemoryStream)original).ToArray()));
        Assert.False(e.Consumed);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task ANewIntrosReplacingTheEntryMidResponseIsNotHandedOver()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        var original = ctx.Response.Body;
        await m.InvokeAsync(ctx, async () =>
        {
            // Another tab of the same browser starts another movie.
            h.Registry.Register(User1, Device, Other, "Other", new[] { Intro2 }, Token, null);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        });
        Assert.Equal(TranscodeResponse(FreshPsid), Encoding.UTF8.GetString(((MemoryStream)original).ToArray()));
        Assert.True(e.Dropped);
        Assert.False(e.Consumed);
    }

    // ------------------------------------------------------------------ UA wait (F5)

    [Fact]
    public async Task NoPlayerUserAgentAfterTheWaitMeansNoPrestart()
    {
        var h = new Harness();
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        await Task.Delay(50);
        await Advance(h, FeatureHandoffRegistry.PlayerUserAgentWait + TimeSpan.FromSeconds(1));
        await Task.Delay(150);
        Assert.Single(h.Loopback.Requests);
        Assert.Equal(HandoffHotState.None, e.Hot);
        Assert.False(h.Registry.IsAdoptable(e));

        // A UA that arrives in time does start it.
        var h2 = new Harness();
        var e2 = h2.Register();
        h2.Registry.OnIntroPlaybackInfo(e2, h2.IntroRequest());
        await Until(() => e2.Prewarm is not null, "prewarm");
        await Advance(h2, FeatureHandoffRegistry.PlayerUserAgentWait - TimeSpan.FromSeconds(1));
        h2.Registry.OnIntroStreamRequest(e2, "late but in time");
        await Until(() => e2.Hot == HandoffHotState.Started, "pre-start");
        Assert.Equal("late but in time", e2.HotUserAgent);
    }

    // ------------------------------------------------------------------ kills off the response path (F6)

    [Fact]
    public async Task AMismatchReturnsWhileTheKillIsStillBlocked()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Transcode.KillGate = new TaskCompletionSource();
        try
        {
            var fresh = Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid, audio: "2"));
            var output = await Task.Run(() => h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, fresh))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(fresh, output);
            await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill started");
        }
        finally
        {
            h.Transcode.KillGate.TrySetResult();
        }
    }

    [Fact]
    public async Task PlaybackInfoForAnotherItemReturnsWhileTheKillIsStillBlocked()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        h.Transcode.KillGate = new TaskCompletionSource();
        try
        {
            var (m, _) = Create(h);
            var ctx = Request("POST", $"/Items/{Other:N}/PlaybackInfo", "{}");
            var reached = false;
            await Task.Run(() => m.InvokeAsync(ctx, () => { reached = true; return Task.CompletedTask; }))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(reached);
            Assert.True(e.Dropped);
            await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill started");
        }
        finally
        {
            h.Transcode.KillGate.TrySetResult();
        }
    }

    // ------------------------------------------------------------------ progressive (F7) and multi-variant (COMPAT-1)

    [Theory]
    [InlineData("/videos/x/master.m3u8?a=b", "hls", true)]
    [InlineData("/videos/x/master.m3u8?a=b", null, true)]
    [InlineData("/videos/x/master.M3U8", "http", true)]
    [InlineData("/videos/x/stream.mkv?a=b", "HLS", true)]
    [InlineData("/videos/x/stream.mkv?a=b.m3u8", "http", false)]
    [InlineData("/videos/x/stream.ts", null, false)]
    public void IsHlsUrlLooksAtTheSubProtocolAndThePathOnly(string url, string? subProtocol, bool expected)
        => Assert.Equal(expected, FeatureHandoffRegistry.IsHlsUrl(url, subProtocol));

    [Fact]
    public async Task AProgressiveTranscodeIsNotPrestarted()
    {
        var h = new Harness();
        h.Loopback.PlaybackInfoResponse = TranscodeResponse(PrewarmPsid)
            .Replace("master.m3u8", "stream.mkv", StringComparison.Ordinal)
            .Replace("\"TranscodingSubProtocol\":\"hls\"", "\"TranscodingSubProtocol\":\"http\"", StringComparison.Ordinal);
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        await Task.Delay(150);
        Assert.Single(h.Loopback.Requests);
        Assert.Equal(HandoffHotState.None, e.Hot);
        Assert.False(h.Registry.IsAdoptable(e));
    }

    // DynamicHlsHelper's HDR entrances for an HDR10 HEVC copy: a PQ hvc1 variant plus an SDR avc1 one.
    private const string HdrSdrMaster =
        "#EXTM3U\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=21000000,AVERAGE-BANDWIDTH=21000000,VIDEO-RANGE=PQ,CODECS=\"hvc1.2.4.L150,mp4a.40.2\",RESOLUTION=3840x2160,FRAME-RATE=23.976\n"
        + "main.m3u8?DeviceId=dev-1&MediaSourceId=m&VideoCodec=hevc,h264&AudioCodec=aac&AudioStreamIndex=1&VideoBitrate=20000000&PlaySessionId=" + PrewarmPsid + "&ApiKey=tok-1&SegmentContainer=mp4&AllowVideoStreamCopy=true\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=21000000,AVERAGE-BANDWIDTH=21000000,VIDEO-RANGE=SDR,CODECS=\"avc1.640033,mp4a.40.2\",RESOLUTION=3840x2160,FRAME-RATE=23.976\n"
        + "main.m3u8?DeviceId=dev-1&MediaSourceId=m&VideoCodec=h264&AudioCodec=aac&AudioStreamIndex=1&VideoBitrate=20000000&PlaySessionId=" + PrewarmPsid + "&ApiKey=tok-1&SegmentContainer=mp4&AllowVideoStreamCopy=false\n";

    [Fact]
    public async Task AMasterWithSeveralVariantsIsNotPrestartedAndNothingIsRewritten()
    {
        var h = new Harness();
        h.Loopback.MasterPlaylist = HdrSdrMaster;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => h.Loopback.Requests.Count == 2, "prewarm + master");
        await Task.Delay(150);
        Assert.Equal(2, h.Loopback.Requests.Count);
        Assert.Contains("master.m3u8", h.Loopback.Requests.Last().PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(HandoffHotState.None, e.Hot);
        Assert.Null(e.HotPlaySessionId);
        Assert.False(h.Registry.IsAdoptable(e));
        Assert.Equal(0, h.Registry.HotSlotsInUse);
        Assert.Null(h.Registry.GetUpcoming(User1, Device, Intro1)!.Transcode);

        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{}");
        var original = ctx.Response.Body;
        await m.InvokeAsync(ctx, async () =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        });
        Assert.Equal(TranscodeResponse(FreshPsid), Encoding.UTF8.GetString(((MemoryStream)original).ToArray()));
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task TheLevelFiveDuplicateOfTheSameVariantStillCountsAsOne()
    {
        var h = new Harness();
        var uri = "main.m3u8?DeviceId=" + Device + "&PlaySessionId=" + PrewarmPsid;
        h.Loopback.MasterPlaylist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,CODECS=\"hvc1.2.4.L153\"\n" + uri
            + "\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,CODECS=\"hvc1.2.4.L150\"\n" + uri + "\n";
        var e = await HotStarted(h);
        Assert.Equal(HandoffHotState.Started, e.Hot);
        Assert.Equal(new[] { uri }, HandoffHls.DistinctUris(h.Loopback.MasterPlaylist));
    }

    // ------------------------------------------------------------------ post-handover supervision (COMPAT-3)

    private static async Task<HandoffEntry> HandedOver(Harness h)
    {
        var e = await HotStarted(h);
        h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        Assert.True(e.Consumed);
        return e;
    }

    [Fact]
    public async Task ADirectStreamOfTheHandedOverSessionKillsTheUnusedTranscode()
    {
        var h = new Harness();
        await HandedOver(h);
        var (m, _) = Create(h);
        var ctx = Request("GET", $"/Videos/{Feature:N}/stream");
        ctx.Request.Headers.Remove("Authorization");
        ctx.Request.QueryString = new QueryString($"?Static=true&MediaSourceId={Feature:N}&PlaySessionId={PrewarmPsid}&api_key={Token}");
        await m.InvokeAsync(ctx, () => Task.CompletedTask);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task AnHlsRequestFirstMeansTheSessionIsUsedAndKeptEvenLater()
    {
        var h = new Harness();
        await HandedOver(h);
        var (m, _) = Create(h);
        var seg = Request("GET", $"/videos/{Feature:D}/hls1/main/1.mp4");
        seg.Request.Headers.Remove("Authorization");
        seg.Request.QueryString = new QueryString($"?DeviceId={Device}&PlaySessionId={PrewarmPsid}&ApiKey={Token}");
        await m.InvokeAsync(seg, () => Task.CompletedTask);

        // Later static requests (e.g. a download) and the supervision window change nothing.
        var stat = Request("GET", $"/Videos/{Feature:N}/stream.mkv");
        stat.Request.QueryString = new QueryString($"?Static=true&PlaySessionId={PrewarmPsid}&api_key={Token}");
        await m.InvokeAsync(stat, () => Task.CompletedTask);
        h.Time.Now += FeatureHandoffRegistry.AdoptionSupervision + TimeSpan.FromSeconds(5);
        h.Registry.Sweep();
        await Task.Delay(100);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task NoHlsRequestWithinTheWindowKillsTheUnusedTranscode()
    {
        var h = new Harness();
        await HandedOver(h);
        h.Time.Now += FeatureHandoffRegistry.AdoptionSupervision - TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        await Task.Delay(50);
        Assert.Empty(h.Transcode.Killed);
        h.Time.Now += TimeSpan.FromSeconds(2);
        h.Registry.Sweep();
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
        h.Time.Now += TimeSpan.FromSeconds(30);
        h.Registry.Sweep();
        await Task.Delay(50);
        Assert.Single(h.Transcode.Killed);
    }

    [Fact]
    public async Task AnotherTokensRequestWithTheSessionIdDecidesNothing()
    {
        var h = new Harness();
        await HandedOver(h);
        h.Registry.ObserveAdoptedStream(PrewarmPsid, "someone-else", "ua", isHls: false);
        await Task.Delay(50);
        Assert.Empty(h.Transcode.Killed);
    }

    private static (Harness H, SessionEvents Events, SessionInfo Session) WithSessions()
    {
        var sessions = DispatchProxy.Create<ISessionManager, SessionEvents>();
        var h = new Harness(null, sessions);
        return (h, (SessionEvents)(object)sessions, new SessionInfo(null!, null!) { UserId = User1, DeviceId = Device });
    }

    private static PlaybackProgressEventArgs FeatureReport(SessionInfo session, string psid, bool paused = false, bool automated = false)
        => new() { Item = new Movie { Id = Feature }, DeviceId = Device, Session = session, PlaySessionId = psid, IsPaused = paused, IsAutomated = automated };

    [Fact]
    public async Task APlayerReportingTheHandedOverTranscodeKeepsItThoughNoRequestReachesTheServer()
    {
        // rr-C2: hls.js serves master/main/init/first segments from the browser cache and the
        // viewer pauses right away, so no request with the session reaches the server for longer
        // than the supervision window; the player's own reports say it is playing the transcode.
        var (h, events, session) = WithSessions();
        await HandedOver(h);
        session.PlayState.PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode;
        events.Raise("PlaybackStart", FeatureReport(session, PrewarmPsid));
        events.Raise("PlaybackProgress", FeatureReport(session, PrewarmPsid, paused: true));
        h.Time.Now += FeatureHandoffRegistry.AdoptionSupervision + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        h.Time.Now += FeatureHandoffRegistry.AdoptionSupervision;
        h.Registry.Sweep();
        await Task.Delay(100);
        Assert.Empty(h.Transcode.Killed);
    }

    [Theory]
    [InlineData(MediaBrowser.Model.Session.PlayMethod.DirectStream)]
    [InlineData(MediaBrowser.Model.Session.PlayMethod.DirectPlay)]
    public async Task APlayerReportingADirectPlayOfTheHandedOverSessionGetsTheTranscodeKilled(MediaBrowser.Model.Session.PlayMethod method)
    {
        var (h, events, session) = WithSessions();
        await HandedOver(h);
        session.PlayState.PlayMethod = method;
        events.Raise("PlaybackStart", FeatureReport(session, PrewarmPsid));
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task ReportsOfOtherSessionsDevicesOrAutomatedOnesDecideNothing()
    {
        var (h, events, session) = WithSessions();
        await HandedOver(h);
        session.PlayState.PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode;
        events.Raise("PlaybackProgress", FeatureReport(session, FreshPsid));
        events.Raise("PlaybackProgress", FeatureReport(session, PrewarmPsid, automated: true));
        var other = new SessionInfo(null!, null!) { UserId = User1, DeviceId = "other-device" };
        other.PlayState.PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode;
        events.Raise("PlaybackProgress", new PlaybackProgressEventArgs { Item = new Movie { Id = Feature }, DeviceId = "other-device", Session = other, PlaySessionId = PrewarmPsid });
        h.Time.Now += FeatureHandoffRegistry.AdoptionSupervision + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill: nothing used the session");
    }

    // ------------------------------------------------------------------ ISessionManager wiring

    /// <summary>An ISessionManager whose event subscriptions a test can see and raise.</summary>
    public class SessionEvents : DispatchProxy
    {
        public Dictionary<string, Delegate?> Handlers { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                var ev = name[4..];
                Handlers[ev] = Delegate.Combine(Handlers.GetValueOrDefault(ev), (Delegate)args![0]!);
                return null;
            }

            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                var ev = name[7..];
                Handlers[ev] = Delegate.Remove(Handlers.GetValueOrDefault(ev), (Delegate)args![0]!);
                return null;
            }

            throw new NotSupportedException(name);
        }

        public void Raise(string ev, object args) => Handlers.GetValueOrDefault(ev)?.DynamicInvoke(null, args);
    }

    [Fact]
    public async Task TheRegistryFollowsTheSessionManagersPlaybackEventsAndUnsubscribes()
    {
        var sessions = DispatchProxy.Create<ISessionManager, SessionEvents>();
        var events = (SessionEvents)(object)sessions;
        var h = new Harness(null, sessions);
        var e = await HotStarted(h);
        Assert.All(new[] { "PlaybackStart", "PlaybackProgress", "PlaybackStopped", "SessionEnded" }, n => Assert.NotNull(events.Handlers[n]));

        var session = new SessionInfo(null!, null!) { UserId = User1, DeviceId = Device };
        var intro = new Movie { Id = Intro1 };
        events.Raise("PlaybackStart", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session, Users = new List<User>() });
        Assert.Equal(Intro1, e.CurrentIntro);
        events.Raise("PlaybackProgress", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session, IsPaused = true });
        Assert.NotNull(e.PausedSince);
        events.Raise("PlaybackStopped", new PlaybackStopEventArgs { Item = intro, DeviceId = Device, Session = session, PlayedToCompletion = false });
        Assert.True(e.StoppedMidway);
        h.Time.Now += FeatureHandoffRegistry.StoppedMidwayGrace + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");

        var e2 = await HotStarted(h);
        events.Raise("SessionEnded", new SessionEventArgs { SessionInfo = session });
        Assert.True(e2.Dropped);

        h.Registry.Dispose();
        Assert.All(new[] { "PlaybackStart", "PlaybackProgress", "PlaybackStopped", "SessionEnded" }, n => Assert.Null(events.Handlers[n]));
    }

    [Fact]
    public async Task StoppingAShortIntroMidwayIsAStopThoughJellyfinReportsItPlayedToCompletion()
    {
        // Jellyfin marks an item shorter than MinResumeDurationSeconds (300 s) as played once it is
        // stopped past MinResumePct (5 %): a user stopping a 14 s preroll after 5.2 s arrives as
        // PlayedToCompletion=true (seen live: "Stopped at 5157 ms", then the 30 s end-of-intro grace).
        var sessions = DispatchProxy.Create<ISessionManager, SessionEvents>();
        var events = (SessionEvents)(object)sessions;
        var h = new Harness(null, sessions);
        var session = new SessionInfo(null!, null!) { UserId = User1, DeviceId = Device };
        var intro = new Movie { Id = Intro1, RunTimeTicks = TimeSpan.FromSeconds(14).Ticks };
        PlaybackStopEventArgs Stopped(double? atSeconds) => new()
        {
            Item = intro, DeviceId = Device, Session = session, PlayedToCompletion = true,
            PlaybackPositionTicks = atSeconds is null ? null : TimeSpan.FromSeconds(atSeconds.Value).Ticks,
        };

        var e = await HotStarted(h);
        events.Raise("PlaybackStart", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session });
        events.Raise("PlaybackStopped", Stopped(5.157));
        Assert.True(e.StoppedMidway);
        Assert.Equal(h.Time.Now + FeatureHandoffRegistry.StoppedMidwayGrace, e.NextRequestDeadline);
        h.Time.Now += FeatureHandoffRegistry.StoppedMidwayGrace + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");

        // Played out (the player's end position can trail the probed runtime a little): the next
        // intro's or the feature's PlaybackInfo is awaited instead.
        foreach (var end in new double?[] { 13.96, 14.0, null })
        {
            var e2 = await HotStarted(h);
            events.Raise("PlaybackStart", new PlaybackProgressEventArgs { Item = intro, DeviceId = Device, Session = session });
            events.Raise("PlaybackStopped", Stopped(end));
            Assert.False(e2.Dropped, $"stopped at {end}");
            Assert.False(e2.StoppedMidway, $"stopped at {end}");
            Assert.Equal(h.Time.Now + FeatureHandoffRegistry.NextRequestGrace, e2.NextRequestDeadline);
        }

        Assert.True(FeatureHandoffRegistry.PlayedOut(true, TimeSpan.FromSeconds(11.5).Ticks, null), "runtime unknown: trust Jellyfin");
        Assert.False(FeatureHandoffRegistry.PlayedOut(false, TimeSpan.FromSeconds(14).Ticks, TimeSpan.FromSeconds(14).Ticks));
        Assert.False(FeatureHandoffRegistry.PlayedOut(true, TimeSpan.FromSeconds(10.9).Ticks, TimeSpan.FromSeconds(14).Ticks));
        Assert.True(FeatureHandoffRegistry.PlayedOut(true, TimeSpan.FromSeconds(11).Ticks, TimeSpan.FromSeconds(14).Ticks));
    }
}
