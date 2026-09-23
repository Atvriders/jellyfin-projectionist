using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class HandoffRegistryTests
{
    internal static readonly Guid User1 = Guid.Parse("99a5a3e4308f4c18958ae921595b5672");
    internal static readonly Guid Feature = Guid.Parse("a9e1d99df22aee602be916311cddf8bf");
    internal static readonly Guid Intro1 = Guid.Parse("ba2f73e4aca4925d6e616385ee5ab8cb");
    internal static readonly Guid Intro2 = Guid.Parse("11111111222233334444555566667777");
    internal static readonly Guid Other = Guid.Parse("0123456789abcdef0123456789abcdef");
    internal const string Device = "dev-1";
    internal const string Token = "tok-1";
    internal const string PrewarmPsid = "e615aeb1fca443b6916a4af3af74cd57";
    internal const string FreshPsid = "94aed149c4ca4d9baec6f39698e28b7d";

    internal static string TranscodeUrl(string psid, string audio = "1") =>
        $"/videos/{Feature:D}/master.m3u8?&DeviceId={Device}&MediaSourceId={Feature:N}&VideoCodec=h264&AudioStreamIndex={audio}"
        + $"&VideoBitrate=2847857&SegmentContainer=mp4&PlaySessionId={psid}&ApiKey={Token}&Tag=etag";

    internal static string TranscodeResponse(string psid, string audio = "1") =>
        "{\"MediaSources\":[{\"Id\":\"" + Feature.ToString("N") + "\",\"Container\":\"mkv\",\"ETag\":\"etag\",\"SupportsDirectPlay\":false,"
        + "\"TranscodingUrl\":\"" + TranscodeUrl(psid, audio) + "\",\"TranscodingSubProtocol\":\"hls\"}],\"PlaySessionId\":\"" + psid + "\"}";

    internal static string DirectResponse(string psid) =>
        "{\"MediaSources\":[{\"Id\":\"" + Feature.ToString("N") + "\",\"Container\":\"mp4\",\"ETag\":\"etag-dp\",\"SupportsDirectPlay\":true,"
        + "\"DefaultAudioStreamIndex\":1}],"
        + "\"PlaySessionId\":\"" + psid + "\"}";

    internal static readonly byte[] WebIntroBody = Encoding.UTF8.GetBytes(
        "{\"UserId\":\"" + User1.ToString("N") + "\",\"StartTimeTicks\":0,\"IsPlayback\":true,\"AutoOpenLiveStream\":true,"
        + "\"AudioStreamIndex\":1,\"MediaSourceId\":\"" + Intro1.ToString("N") + "\",\"MaxStreamingBitrate\":3000000,"
        + "\"DeviceProfile\":{\"Name\":\"web\"}}");

    /// <summary>
    /// A clock tests drive. Setting <see cref="Now"/> only moves the clock; <see cref="Advance"/>
    /// also fires the timers that fall due (Task.Delay(x, time) and CreateTimer use them), so the
    /// registry's ping loop, UA wait and re-kill delay run on test time, not on real timers.
    /// </summary>
    internal sealed class ManualTime : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = new();

        public DateTimeOffset Now { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        public int PendingTimers
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count(t => t.Due is not null);
                }
            }
        }

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state);
            t.Change(dueTime, period);
            lock (_lock)
            {
                _timers.Add(t);
            }

            return t;
        }

        public void Advance(TimeSpan by)
        {
            var target = Now + by;
            while (true)
            {
                ManualTimer? next;
                lock (_lock)
                {
                    next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                }

                if (next is null)
                {
                    break;
                }

                if (next.Due > Now)
                {
                    Now = next.Due!.Value;
                }

                next.Fire();
            }

            Now = target;
        }

        internal sealed class ManualTimer : ITimer
        {
            private readonly ManualTime _owner;
            private readonly TimerCallback _cb;
            private readonly object? _state;
            private TimeSpan _period;

            public ManualTimer(ManualTime owner, TimerCallback cb, object? state)
            {
                _owner = owner;
                _cb = cb;
                _state = state;
            }

            public DateTimeOffset? Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.Now + dueTime;
                return true;
            }

            public void Fire()
            {
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;
                _cb(_state);
            }

            public void Dispose() => Due = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    internal sealed class FakeTranscode : IHandoffTranscodeControl
    {
        public ConcurrentQueue<string> Killed { get; } = new();

        public ConcurrentQueue<string> Pinged { get; } = new();

        /// <summary>What <see cref="Exists"/> answers (the server still has the job).</summary>
        public bool JobExists { get; set; } = true;

        /// <summary>When set, Kill() returns this task (a kill that blocks until released).</summary>
        public TaskCompletionSource? KillGate { get; set; }

        public bool Exists(string playSessionId) => JobExists;

        public void Ping(string playSessionId) => Pinged.Enqueue(playSessionId);

        public Task Kill(string deviceId, string playSessionId)
        {
            Killed.Enqueue(playSessionId);
            return KillGate?.Task ?? Task.CompletedTask;
        }
    }

    internal sealed class FakeLoopback : IHandoffLoopback
    {
        public ConcurrentQueue<HandoffHttpRequest> Requests { get; } = new();

        public string PlaybackInfoResponse { get; set; } = TranscodeResponse(PrewarmPsid);

        /// <summary>When set, answers PlaybackInfo requests instead of <see cref="PlaybackInfoResponse"/>.</summary>
        public Func<HandoffHttpRequest, string>? PlaybackInfoResponder { get; set; }

        public int PlaybackInfoStatus { get; set; } = 200;

        public string MasterPlaylist { get; set; } =
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000\nmain.m3u8?DeviceId=" + Device + "&PlaySessionId=" + PrewarmPsid + "\n";

        /// <summary>Segment requests wait for this when set (a pre-start held in Starting).</summary>
        public TaskCompletionSource<HandoffHttpResponse>? SegmentGate { get; set; }

        public Task<HandoffHttpResponse> SendAsync(HandoffHttpRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var path = request.PathAndQuery;
            string body;
            var status = 200;
            if (path.Contains("/PlaybackInfo", StringComparison.Ordinal))
            {
                body = PlaybackInfoResponder?.Invoke(request) ?? PlaybackInfoResponse;
                status = PlaybackInfoStatus;
            }
            else if (path.Contains("master.m3u8", StringComparison.Ordinal))
            {
                body = MasterPlaylist;
            }
            else if (path.Contains("main.m3u8", StringComparison.Ordinal))
            {
                body = "#EXTM3U\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?PlaySessionId=" + PrewarmPsid + "\"\n#EXTINF:3,\nhls1/main/0.mp4?PlaySessionId=" + PrewarmPsid + "\n";
            }
            else if (SegmentGate is not null)
            {
                return SegmentGate.Task.WaitAsync(cancellationToken);
            }
            else
            {
                body = new string('x', 1000);
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            return Task.FromResult(new HandoffHttpResponse(status, bytes, bytes.Length, null));
        }
    }

    internal sealed class Harness
    {
        public ManualTime Time { get; } = new();

        public FakeTranscode Transcode { get; } = new();

        public FakeLoopback Loopback { get; } = new();

        public FeaturePreloadMode Mode { get; set; } = FeaturePreloadMode.Hot;

        public long? IntroTicks { get; set; } = TimeSpan.FromSeconds(14).Ticks;

        /// <summary>The item's static sources for the user (null = unknown: the response is used).</summary>
        public Func<Guid, Guid, IReadOnlyList<Guid>, HandoffTrackContext?>? Tracks { get; set; }

        public FeatureHandoffRegistry Registry { get; }

        public Harness(
            Microsoft.Extensions.Logging.ILogger? logger = null,
            MediaBrowser.Controller.Session.ISessionManager? sessions = null,
            Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null)
        {
            Registry = new FeatureHandoffRegistry(
                logger ?? NullLogger.Instance,
                Transcode,
                Loopback,
                _ => IntroTicks,
                Time,
                () => Mode,
                startSweeper: false,
                sessions,
                (u, f, i) => Tracks?.Invoke(u, f, i),
                lifetime);
        }

        public HandoffEntry Register(params Guid[] intros)
            => Registry.Register(User1, Device, Feature, "Feature", intros.Length == 0 ? new[] { Intro1 } : intros, Token, IPAddress.Parse("203.0.113.9"));

        public HandoffIntroRequest IntroRequest(byte[]? body = null) => new(
            body ?? WebIntroBody,
            "?ApiKey=" + Token,
            new List<KeyValuePair<string, string>>
            {
                new("Authorization", "MediaBrowser Client=\"Jellyfin Web\", DeviceId=\"" + Device + "\", Token=\"" + Token + "\""),
                new("User-Agent", "Mozilla/5.0 test"),
            });
    }

    internal static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("timed out waiting for " + what);
    }

    internal static async Task<HandoffEntry> HotStarted(Harness h)
    {
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Hot == HandoffHotState.Started, "pre-start");
        await Until(() => h.Time.PendingTimers > 0, "the ping loop's first delay");
        return e;
    }

    [Fact]
    public void UpcomingIsUnknownUntilThePrewarmResolvesAndFiltersOnCurrentItem()
    {
        var h = new Harness();
        h.Register(Intro1, Intro2);
        var up = h.Registry.GetUpcoming(User1, Device, Intro2);
        Assert.NotNull(up);
        Assert.Equal(Feature.ToString("N"), up!.FeatureId);
        Assert.Equal(new[] { Intro1.ToString("N"), Intro2.ToString("N") }, up.IntroIds);
        Assert.Equal("Unknown", up.PlayMethod);
        Assert.Null(up.DirectPlay);
        Assert.Equal(8, up.PrefetchSeconds);

        Assert.NotNull(h.Registry.GetUpcoming(User1, Device, null));
        Assert.NotNull(h.Registry.GetUpcoming(User1, "DEV-1", null));
        Assert.Null(h.Registry.GetUpcoming(User1, Device, Feature));
        Assert.Null(h.Registry.GetUpcoming(User1, "other-device", null));
        Assert.Null(h.Registry.GetUpcoming(Guid.NewGuid(), Device, null));
    }

    [Fact]
    public async Task ExpiryIsIntroRuntimeTimesOneAndAHalfPlusNinetySeconds()
    {
        var h = new Harness { IntroTicks = TimeSpan.FromSeconds(100).Ticks };
        var e = h.Register();
        var created = h.Time.Now;
        var expected = created + TimeSpan.FromSeconds(150 + 90);
        await Until(() => e.ExpiresAt == expected, "expiry from runtime");

        h.Time.Now = expected - TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.NotNull(h.Registry.GetUpcoming(User1, Device, null));

        h.Time.Now = expected + TimeSpan.FromSeconds(1);
        Assert.Null(h.Registry.GetUpcoming(User1, Device, null));
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        Assert.False(h.Registry.HasEntries);
    }

    [Fact]
    public void UnknownRuntimeKeepsTheProvisionalExpiry()
    {
        var h = new Harness { IntroTicks = null };
        var e = h.Register();
        Assert.Equal(h.Time.Now + FeatureHandoffRegistry.ProvisionalExpiry, e.ExpiresAt);
    }

    [Fact]
    public void FeatureResolvedBeforeIntrosMeansNoPrestart()
    {
        var h = new Harness();
        h.Registry.ObservePlaybackInfo(User1, Device, Feature);
        h.Time.Now += TimeSpan.FromSeconds(30);
        var e = h.Register();
        Assert.True(e.FeatureResolvedFirst);

        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        Assert.Equal(0, e.PrewarmClaimed);
        Assert.Empty(h.Loopback.Requests);
    }

    [Fact]
    public void FeatureResolvedLongBeforeIntrosDoesNotCount()
    {
        var h = new Harness();
        h.Registry.ObservePlaybackInfo(User1, Device, Feature);
        h.Registry.ObservePlaybackInfo(User1, "another-device", Other);
        h.Time.Now += TimeSpan.FromSeconds(61);
        Assert.False(h.Register().FeatureResolvedFirst);

        var h2 = new Harness();
        h2.Registry.ObservePlaybackInfo(User1, Device, Other);
        h2.Registry.ObservePlaybackInfo(User1, "another-device", Feature);
        Assert.False(h2.Register().FeatureResolvedFirst);
    }

    [Fact]
    public void FindNeedsTheTokenSeenAtIntros()
    {
        var h = new Harness();
        var e = h.Register();
        Assert.Same(e, h.Registry.Find(Device, Token));
        Assert.Same(e, h.Registry.Find("DEV-1", Token));
        Assert.Same(e, h.Registry.Find(null, Token));
        Assert.Null(h.Registry.Find(Device, "stolen"));
        Assert.Null(h.Registry.Find(Device, null));
        Assert.Null(h.Registry.Find("other", Token));
    }

    [Fact]
    public void RepeatedIntrosForTheSameFeatureWidenTheEntry()
    {
        var h = new Harness();
        var a = h.Register(Intro1);
        var b = h.Register(Intro2);
        Assert.Same(a, b);
        Assert.True(a.HasIntro(Intro1));
        Assert.True(a.HasIntro(Intro2));
    }

    [Fact]
    public async Task HotPrestartsWithThePlayersUserAgentAndClientIp()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var reqs = h.Loopback.Requests.ToList();
        Assert.Equal(5, reqs.Count);

        var pbi = reqs[0];
        Assert.Equal(HttpMethod.Post, pbi.Method);
        Assert.Equal($"/Items/{Feature:N}/PlaybackInfo?ApiKey={Token}", pbi.PathAndQuery);
        Assert.Contains(pbi.Headers, kv => kv.Key == "Authorization");
        Assert.Equal(IPAddress.Parse("203.0.113.9"), pbi.ClientIp);
        var body = Encoding.UTF8.GetString(pbi.JsonBody!);
        Assert.DoesNotContain("AudioStreamIndex", body);
        Assert.DoesNotContain("MediaSourceId", body);
        Assert.Contains("\"DeviceProfile\"", body);

        Assert.StartsWith($"/videos/{Feature:D}/master.m3u8?", reqs[1].PathAndQuery);
        Assert.Equal($"/videos/{Feature:D}/main.m3u8?DeviceId={Device}&PlaySessionId={PrewarmPsid}", reqs[2].PathAndQuery);
        Assert.Equal($"/videos/{Feature:D}/hls1/main/-1.mp4?PlaySessionId={PrewarmPsid}", reqs[3].PathAndQuery);
        Assert.Equal($"/videos/{Feature:D}/hls1/main/0.mp4?PlaySessionId={PrewarmPsid}", reqs[4].PathAndQuery);
        foreach (var r in reqs.Skip(1))
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("Mozilla/5.0 player", Assert.Single(r.Headers, kv => kv.Key == "User-Agent").Value);
            Assert.Equal(IPAddress.Parse("203.0.113.9"), r.ClientIp);
        }

        Assert.Equal(PrewarmPsid, e.HotPlaySessionId);
        Assert.True(h.Registry.IsAdoptable(e));
        Assert.Equal("Transcode", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public async Task MatchingFeaturePlaybackInfoIsHandedThePrewarmSession()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var fresh = Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid));
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json; charset=utf-8", null, fresh);

        Assert.Equal(TranscodeResponse(PrewarmPsid), Encoding.UTF8.GetString(output));
        Assert.True(e.Consumed);
        Assert.False(h.Registry.HasEntries);
        Assert.Null(h.Registry.GetUpcoming(User1, Device, null));

        // Consumed: dropping or expiring later must not kill the player's job.
        h.Registry.Drop(e, "test");
        await Task.Delay(50);
        Assert.Empty(h.Transcode.Killed);
    }

    [Fact]
    public async Task DifferentTrackOrBitrateIsNotHandedOverAndThePrewarmIsKilled()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var fresh = Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid, audio: "2"));
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, fresh);

        Assert.Equal(fresh, output);
        Assert.False(e.Consumed);
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Theory]
    [InlineData(500, "application/json", null)]
    [InlineData(200, "text/html", null)]
    [InlineData(200, "application/json", "gzip")]
    public async Task UnusableFreshResponsesPassThroughAndKill(int status, string contentType, string? encoding)
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var fresh = Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid));
        Assert.Equal(fresh, h.Registry.CompleteFeaturePlaybackInfo(e, status, contentType, encoding, fresh));
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
    }

    [Fact]
    public async Task AbandonedPrewarmIsKilledAtExpiry()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        await Until(() => e.ExpiresAt < h.Time.Now + TimeSpan.FromMinutes(5), "expiry from runtime");
        h.Time.Now = e.ExpiresAt + TimeSpan.FromSeconds(1);
        h.Registry.Sweep();
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill at expiry");
    }

    [Fact]
    public async Task ANewIntrosForAnotherFeatureReplacesAndKills()
    {
        var h = new Harness();
        var e = await HotStarted(h);
        var other = h.Registry.Register(User1, Device, Other, "Other", new[] { Intro2 }, Token, null);
        Assert.NotSame(e, other);
        Assert.True(e.Dropped);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "kill");
        Assert.Same(other, h.Registry.Find(Device, Token));
    }

    [Fact]
    public async Task WarmModeResolvesThePlaybackDecisionButStartsNothing()
    {
        var h = new Harness { Mode = FeaturePreloadMode.Warm };
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        await Task.Delay(100);
        Assert.Single(h.Loopback.Requests);
        Assert.Equal(HandoffHotState.None, e.Hot);
        Assert.False(h.Registry.IsAdoptable(e));
        Assert.Equal("Transcode", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public async Task DirectPlayFeatureReportsTheStaticUrlInputs()
    {
        var h = new Harness();
        h.Loopback.PlaybackInfoResponse = DirectResponse(PrewarmPsid);
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        var up = h.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.Equal("DirectPlay", up.PlayMethod);
        Assert.Equal("mp4", up.DirectPlay!.Container);
        Assert.Equal(Feature.ToString("N"), up.DirectPlay.MediaSourceId);
        Assert.Equal("etag-dp", up.DirectPlay.ETag);
        await Task.Delay(50);

        // Direct play is only trusted once the explicit-tracks shape (details page) agrees.
        Assert.Equal(2, h.Loopback.Requests.Count);
        var second = System.Text.Json.Nodes.JsonNode.Parse(h.Loopback.Requests.ElementAt(1).JsonBody)!.AsObject();
        Assert.Equal(Feature.ToString("N"), (string?)second["MediaSourceId"]);
        Assert.Equal(1, (int?)second["AudioStreamIndex"]);
        Assert.Equal(-1, (int?)second["SubtitleStreamIndex"]);
        Assert.False(h.Registry.IsAdoptable(e));
    }

    [Fact]
    public async Task ADirectPlayThatOnlyHoldsWithoutExplicitTracksIsNotAdvertisedAndTheTranscodeIsPrestarted()
    {
        // rr-C1 (b): H.264 + a non-default-flagged AC3 in MKV. Without an audio index the server
        // never checks the audio codec (DirectPlay); jellyfin-web's details page sends
        // AudioStreamIndex + SubtitleStreamIndex=-1 + MediaSourceId and gets a transcode.
        var h = new Harness();
        h.Loopback.PlaybackInfoResponder = r =>
            Encoding.UTF8.GetString(r.JsonBody!).Contains("\"AudioStreamIndex\"", StringComparison.Ordinal)
                ? TranscodeResponse(PrewarmPsid)
                : DirectResponse(FreshPsid);
        var e = await HotStarted(h);

        var up = h.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.Equal("Transcode", up.PlayMethod);
        Assert.Null(up.DirectPlay);
        Assert.Equal(PrewarmPsid, e.HotPlaySessionId);
        Assert.True(h.Registry.IsAdoptable(e));

        // The details-page Play's fresh transcode is handed the pre-started session.
        var output = h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        Assert.Equal(TranscodeResponse(PrewarmPsid), Encoding.UTF8.GetString(output));
    }

    [Fact]
    public async Task AFailedExplicitTracksPrewarmAdvertisesNothing()
    {
        var h = new Harness();
        var calls = 0;
        h.Loopback.PlaybackInfoResponder = _ => Interlocked.Increment(ref calls) == 1 ? DirectResponse(PrewarmPsid) : "not json";
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => h.Loopback.Requests.Count == 2, "both prewarms");
        await Task.Delay(50);
        Assert.Null(e.Prewarm);
        Assert.Equal("Unknown", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public async Task DirectPlayWithoutAProfileIsNotTrusted()
    {
        var h = new Harness();
        h.Loopback.PlaybackInfoResponse = DirectResponse(PrewarmPsid);
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest(Encoding.UTF8.GetBytes("{\"UserId\":\"x\"}")));
        await Until(() => e.Prewarm is not null, "prewarm");
        Assert.Equal("Unknown", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public void ThePlayersFirstUserAgentWins()
    {
        var h = new Harness();
        var e = h.Register();
        Assert.True(e.TrySetPlayerUserAgent("first"));
        Assert.False(e.TrySetPlayerUserAgent("second"));
        Assert.Equal("first", e.PlayerUserAgent);
    }

    [Fact]
    public async Task PrewarmRunsOnce()
    {
        var h = new Harness { Mode = FeaturePreloadMode.Warm };
        var e = h.Register(Intro1, Intro2);
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        await Task.Delay(50);
        Assert.Single(h.Loopback.Requests);
    }

    [Fact]
    public void OnIntrosResolvedReadsJellyfinsClaimsAndTheEffectiveClientIp()
    {
        var h = new Harness();
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", User1.ToString("N")),
                new Claim("Jellyfin-DeviceId", Device),
                new Claim("Jellyfin-Token", Token),
            },
            "Custom"));
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.23");
        var movie = new Movie { Id = Feature, Name = "Feature" };
        var user = new User("test", "auth", "reset") { Id = User1 };

        h.Registry.OnIntrosResolved(ctx, movie, user, new[] { Intro1 });
        var e = h.Registry.Find(Device, Token);
        Assert.NotNull(e);
        Assert.Equal(Feature, e!.FeatureId);
        Assert.Equal(IPAddress.Parse("198.51.100.23"), e.ClientIp);

        // Never throws, never records without device/token or when Off.
        h.Registry.OnIntrosResolved(null, movie, user, new[] { Intro1 });
        h.Registry.OnIntrosResolved(new DefaultHttpContext(), movie, user, new[] { Intro1 });
        h.Registry.OnIntrosResolved(ctx, null!, user, new[] { Intro1 });
        var off = new Harness { Mode = FeaturePreloadMode.Off };
        off.Registry.OnIntrosResolved(ctx, movie, user, new[] { Intro1 });
        Assert.False(off.Registry.HasEntries);
    }

    [Fact]
    public async Task FailedPrewarmPlaybackInfoLeavesNothingToAdopt()
    {
        var h = new Harness();
        h.Loopback.PlaybackInfoStatus = 401;
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "ua");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => !h.Loopback.Requests.IsEmpty, "request");
        await Task.Delay(50);
        Assert.Null(e.Prewarm);
        Assert.False(h.Registry.IsAdoptable(e));
        Assert.Equal("Unknown", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }
}
