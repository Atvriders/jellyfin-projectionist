using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffMiddlewareTests;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>SEC-1..4 and the loopback / Off-mode middleware paths (F2, F8).</summary>
public class HandoffSecurityTests
{
    /// <summary>A logger that keeps every formatted line.</summary>
    internal sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception));
    }

    /// <summary>Response feature whose OnStarting callbacks a test can fire (DefaultHttpContext drops them).</summary>
    internal sealed class StartableResponse : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task StartAsync()
        {
            for (var i = _starting.Count - 1; i >= 0; i--)
            {
                await _starting[i].Callback(_starting[i].State);
            }
        }
    }

    internal static StartableResponse Startable(DefaultHttpContext ctx)
    {
        var feature = new StartableResponse();
        ctx.Features.Set<IHttpResponseFeature>(feature);
        return feature;
    }

    private static DefaultHttpContext Unauthenticated(Guid item, string deviceId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = $"/Items/{item:N}/PlaybackInfo";
        ctx.Request.Headers.Authorization = "MediaBrowser Client=\"x\", DeviceId=\"" + deviceId + "\"";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        ctx.Response.Body = new MemoryStream();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.66");
        return ctx;
    }

    // ------------------------------------------------------------------ SEC-1 / C2 / F1

    [Fact]
    public async Task A401PlaybackInfoRecordsNothing()
    {
        // Inverts the reviewers' probe that pinned the bad behaviour: a request auth rejects must
        // not mark the (victim) device as resolving the feature first.
        var h = new Harness { Mode = FeaturePreloadMode.Warm };
        var (m, _) = Create(h);
        var ctx = Unauthenticated(Feature, Device);
        await m.InvokeAsync(ctx, () => { ctx.Response.StatusCode = 401; return Task.CompletedTask; });

        Assert.Equal(0, h.Registry.ObservedDeviceCount);
        var e = h.Register();
        Assert.False(e.FeatureResolvedFirst);
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => !h.Loopback.Requests.IsEmpty, "the victim's prewarm still runs");
    }

    [Fact]
    public async Task ObservationsAreKeyedByTheAuthenticatedUserAndDeviceNotTheDeviceIdAlone()
    {
        var h = new Harness { Mode = FeaturePreloadMode.Warm };
        var (m, _) = Create(h);

        // 200 but no principal (e.g. an anonymous endpoint): nothing.
        var anon = Unauthenticated(Feature, Device);
        await m.InvokeAsync(anon, () => { anon.Response.StatusCode = 200; return Task.CompletedTask; });
        Assert.Equal(0, h.Registry.ObservedDeviceCount);

        // Real Jellyfin copies the header's DeviceId verbatim into the claim, so an authenticated
        // attacker (another user) can present the victim's device id (SEC-RR-2). Their feature
        // PlaybackInfo is recorded for THEIR user only and never makes the victim's play feature-first.
        var attacker = Guid.Parse("0badc0de0badc0de0badc0de0badc0de");
        var ctx = Unauthenticated(Feature, Device);
        await m.InvokeAsync(ctx, () =>
        {
            ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim("Jellyfin-UserId", attacker.ToString("N")),
                    new System.Security.Claims.Claim("Jellyfin-DeviceId", Device),
                    new System.Security.Claims.Claim("Jellyfin-Token", "attacker-token"),
                },
                "CustomAuthentication"));
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        Assert.Equal(1, h.Registry.ObservedDeviceCount);
        Assert.False(h.Register().FeatureResolvedFirst);
        Assert.True(h.Registry.Register(attacker, Device, Feature, "F", new[] { Intro1 }, "attacker-token", null).FeatureResolvedFirst);

        // A principal without a user id claim records nothing.
        var noUser = Unauthenticated(Other, Device);
        await m.InvokeAsync(noUser, () =>
        {
            noUser.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("Jellyfin-DeviceId", "lonely-device") },
                "CustomAuthentication"));
            noUser.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        Assert.Equal(1, h.Registry.ObservedDeviceCount);

        // The same user on the same device is still feature-first (unchanged rule).
        var own = Unauthenticated(Other, Device);
        await m.InvokeAsync(own, () =>
        {
            own.User = Principal(Device);
            own.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        Assert.True(h.Registry.Register(User1, Device, Other, "O", new[] { Intro1 }, Token, null).FeatureResolvedFirst);
    }

    [Fact]
    public async Task A401FloodLeavesTheObservationMapEmpty()
    {
        var h = new Harness { Mode = FeaturePreloadMode.Hot };
        var (m, _) = Create(h);
        for (var i = 0; i < 2000; i++)
        {
            var ctx = Unauthenticated(Guid.NewGuid(), "d" + i + new string('x', 200));
            await m.InvokeAsync(ctx, () => { ctx.Response.StatusCode = 401; return Task.CompletedTask; });
        }

        Assert.Equal(0, h.Registry.ObservedDeviceCount);
    }

    [Fact]
    public void ObservationsAreCappedPerDeviceAndInTotalAndLongIdsAreIgnored()
    {
        var h = new Harness();
        for (var i = 0; i < 1000; i++)
        {
            h.Registry.ObservePlaybackInfo(User1, "dev-" + i, Guid.NewGuid());
            h.Time.Now += TimeSpan.FromMilliseconds(1);
        }

        Assert.Equal(FeatureHandoffRegistry.MaxObservedDevices, h.Registry.ObservedDeviceCount);

        // Least recently observed devices were evicted, the latest kept.
        h.Registry.ObservePlaybackInfo(User1, "dev-999", Feature);
        Assert.True(h.Registry.Register(User1, "dev-999", Feature, "F", new[] { Intro1 }, "t999", null).FeatureResolvedFirst);
        h.Registry.ObservePlaybackInfo(User1, "dev-0", Feature);
        Assert.Equal(FeatureHandoffRegistry.MaxObservedDevices, h.Registry.ObservedDeviceCount);

        // Per device: only the latest few items are kept.
        var items = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        foreach (var item in items)
        {
            h.Registry.ObservePlaybackInfo(User1, "busy", item);
        }

        Assert.False(h.Registry.Register(User1, "busy", items[0], "F", new[] { Intro1 }, "t1", null).FeatureResolvedFirst);
        Assert.True(h.Registry.Register(User1, "busy", items[^1], "F", new[] { Intro1 }, "t2", null).FeatureResolvedFirst);
        Assert.True(h.Registry.Register(User1, "busy", items[^FeatureHandoffRegistry.MaxObservationsPerDevice], "F", new[] { Intro1 }, "t3", null).FeatureResolvedFirst);
        Assert.False(h.Registry.Register(User1, "busy", items[^(FeatureHandoffRegistry.MaxObservationsPerDevice + 1)], "F", new[] { Intro1 }, "t4", null).FeatureResolvedFirst);

        var h2 = new Harness();
        var longId = new string('d', FeatureHandoffRegistry.MaxDeviceIdLength + 1);
        h2.Registry.ObservePlaybackInfo(User1, longId, Feature);
        Assert.Equal(0, h2.Registry.ObservedDeviceCount);
    }

    [Fact]
    public void ObservationsArePrunedBySweepNotPerRequest()
    {
        var h = new Harness();
        h.Registry.ObservePlaybackInfo(User1, "a", Feature);
        h.Time.Now += FeatureHandoffRegistry.FeatureFirstWindow + TimeSpan.FromSeconds(1);
        h.Registry.ObservePlaybackInfo(User1, "b", Feature);
        Assert.Equal(2, h.Registry.ObservedDeviceCount);
        h.Registry.Sweep();
        Assert.Equal(1, h.Registry.ObservedDeviceCount);
    }

    [Fact]
    public async Task PreAuthRequestsThatMatchNothingNeverWaitOnTheRegistryLock()
    {
        var h = new Harness();
        h.Register(); // HasEntries: the middleware does look requests up
        var (m, _) = Create(h);
        var gate = typeof(FeatureHandoffRegistry)
            .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(h.Registry)!;

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait();
        try
        {
            var work = Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    var pbi = Unauthenticated(Guid.NewGuid(), "d" + i);
                    pbi.Request.Headers.Authorization = "MediaBrowser DeviceId=\"d" + i + "\", Token=\"forged-" + i + "\"";
                    await m.InvokeAsync(pbi, () => { pbi.Response.StatusCode = 401; return Task.CompletedTask; });

                    var seg = Request("GET", $"/Videos/{Feature:N}/hls1/main/0.mp4");
                    seg.Request.Headers.Remove("Authorization");
                    seg.Request.QueryString = new QueryString($"?PlaySessionId=x{i}&ApiKey=forged-{i}");
                    await m.InvokeAsync(seg, () => Task.CompletedTask);
                }
            });
            var finished = await Task.WhenAny(work, Task.Delay(5000));
            Assert.Same(work, finished);
            await work;
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    // ------------------------------------------------------------------ SEC-3

    [Fact]
    public void PendingEntriesAreCappedPerUserOldestFirst()
    {
        var h = new Harness();
        var entries = new List<HandoffEntry>();
        for (var i = 0; i < 5; i++)
        {
            entries.Add(h.Registry.Register(User1, "d" + i, Feature, "F", new[] { Intro1 }, "tok" + i, null));
            h.Time.Now += TimeSpan.FromSeconds(1);
        }

        Assert.True(entries[0].Dropped);
        Assert.True(entries[1].Dropped);
        Assert.All(entries.Skip(2), e => Assert.False(e.Dropped));
        Assert.Null(h.Registry.Find("d0", "tok0"));
        Assert.Same(entries[4], h.Registry.Find("d4", "tok4"));

        // Other users are unaffected.
        var other = h.Registry.Register(Guid.NewGuid(), "x", Feature, "F", new[] { Intro1 }, "tokx", null);
        Assert.False(other.Dropped);
        Assert.False(entries[2].Dropped);
    }

    [Fact]
    public void PendingEntriesAreCappedGlobally()
    {
        var h = new Harness();
        var entries = new List<HandoffEntry>();
        for (var i = 0; i < FeatureHandoffRegistry.MaxEntries + 8; i++)
        {
            entries.Add(h.Registry.Register(Guid.NewGuid(), "d" + i, Feature, "F", new[] { Intro1 }, "tok" + i, null));
            h.Time.Now += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(8, entries.Count(e => e.Dropped));
        Assert.All(entries.Take(8), e => Assert.True(e.Dropped));
        Assert.Equal(FeatureHandoffRegistry.MaxEntries, entries.Count(e => !e.Dropped));
    }

    [Fact]
    public async Task ConcurrentPrestartsAreBoundedAndTheRestGetWarmOnly()
    {
        var h = new Harness();
        var entries = new List<HandoffEntry>();
        for (var i = 0; i < FeatureHandoffRegistry.MaxConcurrentPrestarts + 1; i++)
        {
            var e = h.Registry.Register(Guid.NewGuid(), "d" + i, Feature, "F", new[] { Intro1 }, "tok" + i, IPAddress.Loopback);
            h.Registry.OnIntroStreamRequest(e, "ua");
            h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
            if (i < FeatureHandoffRegistry.MaxConcurrentPrestarts)
            {
                await Until(() => e.Hot == HandoffHotState.Started, "pre-start " + i);
            }

            entries.Add(e);
        }

        // The last one: its prewarm PlaybackInfo and master playlist, then nothing (no slot).
        await Until(() => h.Loopback.Requests.Count == (5 * FeatureHandoffRegistry.MaxConcurrentPrestarts) + 2, "last prewarm");
        await Task.Delay(100);
        Assert.Equal((5 * FeatureHandoffRegistry.MaxConcurrentPrestarts) + 2, h.Loopback.Requests.Count);
        var last = entries[^1];
        Assert.Equal(HandoffHotState.None, last.Hot);
        Assert.Null(last.HotPlaySessionId);
        Assert.False(h.Registry.IsAdoptable(last));
        Assert.Equal(FeatureHandoffRegistry.MaxConcurrentPrestarts, h.Registry.HotSlotsInUse);
        Assert.Equal(FeatureHandoffRegistry.MaxConcurrentPrestarts, entries.Count(e => e.Hot == HandoffHotState.Started));

        // A drop gives its slot back; so does a handover.
        h.Registry.Drop(entries[0], "test");
        Assert.Equal(FeatureHandoffRegistry.MaxConcurrentPrestarts - 1, h.Registry.HotSlotsInUse);
        h.Registry.CompleteFeaturePlaybackInfo(entries[1], 200, "application/json", null, Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid)));
        Assert.True(entries[1].Consumed);
        Assert.Equal(0, h.Registry.HotSlotsInUse);
    }

    // ------------------------------------------------------------------ SEC-4

    [Fact]
    public async Task MismatchLogsNameTheDifferingParametersButNeverTheUrls()
    {
        var log = new ListLogger();
        var h = new Harness(log);
        var e = await HotStarted(h);
        var fresh = Encoding.UTF8.GetBytes(TranscodeResponse(FreshPsid, audio: "2"));
        h.Registry.CompleteFeaturePlaybackInfo(e, 200, "application/json", null, fresh);

        Assert.Contains(log.Lines, l => l.Contains("AudioStreamIndex", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("ApiKey=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log.Lines, l => l.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("/videos/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DifferingParamNamesListsOnlyNames()
    {
        var a = TranscodeUrl(PrewarmPsid);
        var b = TranscodeUrl(FreshPsid, audio: "2").Replace("ApiKey=" + Token, "ApiKey=other", StringComparison.Ordinal) + "&SubtitleStreamIndex=3";
        Assert.Equal(new[] { "AudioStreamIndex", "PlaySessionId", "ApiKey", "SubtitleStreamIndex" }, HandoffJson.DifferingParamNames(a, b));
        Assert.Equal(new[] { "(path)" }, HandoffJson.DifferingParamNames("/a?x=1", "/b?x=1"));
        Assert.Empty(HandoffJson.DifferingParamNames(a, a));
    }

    // ------------------------------------------------------------------ SEC-2: probe + marker

    private sealed class ProbeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

        public ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        public ConcurrentQueue<HttpRequestMessage> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Enqueue(request);
            return Task.FromResult(_answer(request));
        }
    }

    private static HttpResponseMessage Ok(string? proof = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
        if (proof is not null)
        {
            r.Headers.TryAddWithoutValidation(HandoffLoopbackClient.ProofHeaderName, proof);
        }

        return r;
    }

    /// <summary>
    /// What this server's middleware answers to <paramref name="req"/>'s probe when the request
    /// really reached the socket it dialed (the proof is bound to that local endpoint).
    /// </summary>
    private static string? RealAnswer(HandoffLoopbackClient server, HttpRequestMessage req)
    {
        var nonce = req.Headers.TryGetValues(HandoffLoopbackClient.ProbeHeaderName, out var v) ? v.First() : null;
        return server.ComputeProof(nonce, IPAddress.Parse(req.RequestUri!.Host.Trim('[', ']')), req.RequestUri.Port);
    }

    private static HandoffHttpRequest PlaybackInfoRequest() => new(
        HttpMethod.Post,
        $"/Items/{Feature:N}/PlaybackInfo?ApiKey={Token}",
        new List<KeyValuePair<string, string>> { new("Authorization", Auth) },
        Encoding.UTF8.GetBytes("{}"),
        IPAddress.Parse("203.0.113.9"),
        true,
        TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ASquatterThatCannotProveItIsThisServerNeverGetsCredentials()
    {
        HandoffLoopbackClient? client = null;
        var handler = new ProbeHandler(req =>
        {
            if (req.RequestUri!.Host == "127.0.0.1")
            {
                return Ok(); // squatter: 200 to everything, no proof
            }

            // The real server: its middleware answers the nonce for the socket it arrived on.
            return Ok(RealAnswer(client!, req));
        });
        client = new HandoffLoopbackClient(
            NullLogger<HandoffLoopbackClient>.Instance, null, null, null, null, () => new[] { "http://127.0.0.1:18096", "http://192.0.2.10:18096" }, handler);

        var resp = await ((IHandoffLoopback)client).SendAsync(PlaybackInfoRequest(), CancellationToken.None);
        Assert.Equal(200, resp.Status);

        var squatter = handler.Seen.Where(r => r.RequestUri!.Host == "127.0.0.1").ToList();
        var probe = Assert.Single(squatter);
        Assert.Equal("/System/Ping", probe.RequestUri!.AbsolutePath);
        Assert.False(probe.Headers.Contains("Authorization"));
        Assert.False(probe.Headers.Contains(HandoffLoopbackClient.HeaderName));
        Assert.Contains(handler.Seen, r => r.RequestUri!.Host == "192.0.2.10" && r.RequestUri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal) && r.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task AProofFromAnotherSecretOrAReplayedProofIsRejected()
    {
        var other = new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, null, null, "http://unused");
        string? firstNonce = null;
        var handler = new ProbeHandler(req =>
        {
            var nonce = req.Headers.GetValues(HandoffLoopbackClient.ProbeHeaderName).First();
            firstNonce ??= nonce;
            return Ok(RealAnswer(other, req));
        });
        var client = new HandoffLoopbackClient(
            NullLogger<HandoffLoopbackClient>.Instance, null, null, null, null, () => new[] { "http://127.0.0.1:18096" }, handler);

        var resp = await ((IHandoffLoopback)client).SendAsync(PlaybackInfoRequest(), CancellationToken.None);
        Assert.Equal(0, resp.Status);
        Assert.All(handler.Seen, r => Assert.Equal("/System/Ping", r.RequestUri!.AbsolutePath));

        // A proof is bound to its nonce: every probe uses a fresh one.
        var at = IPAddress.Loopback;
        Assert.NotEqual(client.ComputeProof(firstNonce, at, 18096), client.ComputeProof("00112233445566778899aabbccddeeff", at, 18096));
    }

    [Fact]
    public async Task ARelayedProofFromAnotherSocketNeverVerifiesAndNothingIsSent()
    {
        // SEC-RR-1: RequireHttps makes the real http socket answer 307; a local process squatting
        // [::1]:{HttpPort} relays the probe to the real https socket and echoes the proof it gets.
        HandoffLoopbackClient? client = null;
        var handler = new ProbeHandler(req =>
        {
            if (req.RequestUri!.Host == "127.0.0.1")
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            }

            var nonce = req.Headers.GetValues(HandoffLoopbackClient.ProbeHeaderName).First();
            return Ok(client!.ComputeProof(nonce, IPAddress.Loopback, 18552));
        });
        client = new HandoffLoopbackClient(
            NullLogger<HandoffLoopbackClient>.Instance, null, null, null, null, () => new[] { "http://127.0.0.1:18551", "http://[::1]:18551" }, handler);

        var resp = await ((IHandoffLoopback)client).SendAsync(PlaybackInfoRequest(), CancellationToken.None);

        Assert.Equal(0, resp.Status);
        Assert.Equal(2, handler.Seen.Count);
        Assert.All(handler.Seen, r =>
        {
            Assert.Equal("/System/Ping", r.RequestUri!.AbsolutePath);
            Assert.False(r.Headers.Contains("Authorization"));
            Assert.False(r.Headers.Contains(HandoffLoopbackClient.HeaderName));
        });
    }

    /// <summary>An IHostApplicationLifetime whose ApplicationStopping tests fire.</summary>
    internal sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnceTheServerIsStoppingNothingIsSentEvenToAProvenBase(bool provenBefore)
    {
        // SEC-R3-1: Kestrel unbinds during shutdown / restart and anyone may then bind the port; a
        // base proven minutes ago must not receive the next request's credentials.
        HandoffLoopbackClient? client = null;
        var handler = new ProbeHandler(req => Ok(RealAnswer(client!, req)));
        var lifetime = new FakeLifetime();
        client = new HandoffLoopbackClient(
            NullLogger<HandoffLoopbackClient>.Instance, null, null, null, null, () => new[] { "http://127.0.0.1:18096" }, handler, lifetime);
        var loopback = (IHandoffLoopback)client;

        if (provenBefore)
        {
            Assert.Equal(200, (await loopback.SendAsync(PlaybackInfoRequest(), CancellationToken.None)).Status);
        }

        var seen = handler.Seen.Count;
        lifetime.StopApplication();
        Assert.True(client.IsStopping);

        var resp = await loopback.SendAsync(PlaybackInfoRequest(), CancellationToken.None);
        Assert.Equal(0, resp.Status);
        Assert.Equal(seen, handler.Seen.Count);
    }

    [Fact]
    public async Task OnceTheServerIsStoppingNoNewLoopbackConnectionIsOpened()
    {
        // Defence in depth: the SocketsHttpHandler's ConnectCallback refuses too, so a request that
        // passed the first check never opens a connection to whoever now holds the port.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, null, null, null, null, () => Array.Empty<string>(), null);

            await using (await client.ConnectAsync(new DnsEndPoint("127.0.0.1", port), CancellationToken.None))
            {
                using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            client.OnApplicationStopping();
            await Assert.ThrowsAsync<HttpRequestException>(async () => await client.ConnectAsync(new DnsEndPoint("127.0.0.1", port), CancellationToken.None));
            await Task.Delay(100);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ServerShutdownDropsPendingPlaysAndStartsNoNewPrewarm()
    {
        var lifetime = new FakeLifetime();
        var h = new Harness(lifetime: lifetime);
        var e = await HotStarted(h);

        lifetime.StopApplication();
        Assert.True(e.Dropped);
        Assert.True(e.Cancellation.IsCancellationRequested);
        await Until(() => h.Transcode.Killed.Contains(PrewarmPsid), "the pre-started transcode is killed");
        Assert.False(h.Registry.HasEntries);

        // A play that sneaks in during shutdown dials nothing.
        var requests = h.Loopback.Requests.Count;
        var late = h.Register();
        h.Registry.OnIntroPlaybackInfo(late, h.IntroRequest());
        await Task.Delay(100);
        Assert.Equal(requests, h.Loopback.Requests.Count);
    }

    [Fact]
    public async Task OnlyKestrelsListeningSocketsAreEverDialedNeverLoopbackGuesses()
    {
        // IPv4-only bind under RequireHttps: the one http socket redirects; [::1]:{HttpPort} (free,
        // squattable) and GetApiUrlForLocalAccess are never tried, so the handoff is skipped.
        var server = new FakeServer("http://0.0.0.0:18551", "https://0.0.0.0:18552");
        var appHost = DispatchProxy.Create<MediaBrowser.Controller.IServerApplicationHost, AppHostProxy>();
        var handler = new ProbeHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
        var client = new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, appHost, null, null, new FakeServices(server), null, handler);

        var resp = await ((IHandoffLoopback)client).SendAsync(PlaybackInfoRequest(), CancellationToken.None);

        Assert.Equal(0, resp.Status);
        var only = Assert.Single(handler.Seen);
        Assert.Equal("http://127.0.0.1:18551/System/Ping", only.RequestUri!.ToString());
    }

    /// <summary>An IServerApplicationHost whose HttpPort is 18551 and local-access URL [::1] (the old guesses).</summary>
    public class AppHostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_HttpPort" => 18551,
            "get_HttpsPort" => 18552,
            "GetApiUrlForLocalAccess" => "http://[::1]:18551",
            _ => targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null,
        };
    }

    [Fact]
    public async Task KestrelsListeningAddressesAreTheCandidates()
    {
        var server = new FakeServer("http://192.0.2.10:18531", "https://192.0.2.10:18532");
        var services = new FakeServices(server);
        HandoffLoopbackClient? client = null;
        var handler = new ProbeHandler(req =>
        {
            return Ok(RealAnswer(client!, req));
        });
        client = new HandoffLoopbackClient(NullLogger<HandoffLoopbackClient>.Instance, null, null, null, services, null, handler);

        var resp = await ((IHandoffLoopback)client).SendAsync(PlaybackInfoRequest(), CancellationToken.None);
        Assert.Equal(200, resp.Status);
        Assert.Equal("http://192.0.2.10:18531/System/Ping", handler.Seen.First().RequestUri!.ToString());

        // The marker is honoured from the server's own listening address, not from another peer.
        Assert.True(client.IsOwnAddress(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("192.0.2.99")));
        Assert.False(client.IsOwnAddress(IPAddress.Parse("198.51.100.7"), IPAddress.Parse("192.0.2.10")));
    }

    [Theory]
    [InlineData("http://0.0.0.0:8096", "", "http://127.0.0.1:8096")]
    [InlineData("http://[::]:8096", "/jf", "http://[::1]:8096/jf,http://127.0.0.1:8096/jf")]
    [InlineData("http://+:8096", "", "http://127.0.0.1:8096")]
    [InlineData("http://localhost:8096", "", "http://127.0.0.1:8096,http://[::1]:8096")]
    [InlineData("http://192.0.2.10:8096", "/jf", "http://192.0.2.10:8096/jf")]
    [InlineData("http://[2001:db8::5]:8096", "", "http://[2001:db8::5]:8096")]
    [InlineData("https://0.0.0.0:8920", "", "")]
    public void ListeningAddressesBecomeLoopbackCandidates(string address, string baseUrl, string expected)
        => Assert.Equal(
            expected.Length == 0 ? Array.Empty<string>() : expected.Split(','),
            HandoffLoopbackClient.FromServerAddresses(new[] { address }, baseUrl).ToArray());

    private static DefaultHttpContext Probe(string nonce, string path = "/System/Ping")
    {
        var ctx = Request("GET", path);
        ctx.Connection.LocalIpAddress = IPAddress.Loopback;
        ctx.Connection.LocalPort = 8096;
        ctx.Request.Headers[HandoffLoopbackClient.ProbeHeaderName] = nonce;
        return ctx;
    }

    [Fact]
    public async Task TheMiddlewareAnswersAProbeWithAProofBoundToItsOwnSocketAndStripsIt()
    {
        var h = new Harness();
        var (m, loopback) = Create(h);
        const string nonce = "0123456789abcdef0123456789abcdef";
        var ctx = Probe(nonce);
        var response = Startable(ctx);
        var headerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            headerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.ProbeHeaderName);
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        await response.StartAsync();
        Assert.False(headerSeen);
        var proof = ctx.Response.Headers[HandoffLoopbackClient.ProofHeaderName].ToString();
        Assert.Equal(loopback.ComputeProof(nonce, IPAddress.Loopback, 8096), proof);

        // Bound to the socket: the same nonce on another local endpoint gives another proof.
        Assert.NotEqual(proof, loopback.ComputeProof(nonce, IPAddress.Loopback, 8920));
        Assert.NotEqual(proof, loopback.ComputeProof(nonce, IPAddress.IPv6Loopback, 8096));
        Assert.Equal(proof, loopback.ComputeProof(nonce, IPAddress.Loopback.MapToIPv6(), 8096));

        // A malformed nonce gets nothing.
        foreach (var bad in new[] { "short", new string('a', 65), "zz23456789abcdef0123456789abcdef" })
        {
            var c = Probe(bad);
            var r = Startable(c);
            await m.InvokeAsync(c, () => { c.Response.StatusCode = 200; return Task.CompletedTask; });
            await r.StartAsync();
            Assert.False(c.Response.Headers.ContainsKey(HandoffLoopbackClient.ProofHeaderName));
        }
    }

    [Theory]
    [InlineData("remote-peer")]
    [InlineData("https")]
    [InlineData("other-path")]
    [InlineData("redirect")]
    public async Task TheProbeIsOnlyAnsweredForAPlainHttpPingFromThisHostThatSucceeds(string variant)
    {
        // SEC-RR-1: no proof oracle for other peers, over https, on other paths, or on a 307.
        var h = new Harness();
        var (m, _) = Create(h);
        var ctx = Probe("0123456789abcdef0123456789abcdef", variant == "other-path" ? "/Users/Public" : "/System/Ping");
        switch (variant)
        {
            case "remote-peer":
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
                break;
            case "https":
                ctx.Request.Scheme = "https";
                break;
        }

        var response = Startable(ctx);
        var headerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            headerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.ProbeHeaderName);
            ctx.Response.StatusCode = variant == "redirect" ? 307 : 200;
            return Task.CompletedTask;
        });
        await response.StartAsync();
        Assert.False(headerSeen);
        Assert.False(ctx.Response.Headers.ContainsKey(HandoffLoopbackClient.ProofHeaderName));
    }

    [Fact]
    public async Task AValidMarkerOverHttpsIsIgnored()
    {
        var h = new Harness();
        var (m, loopback) = Create(h);
        var ctx = Request("GET", "/System/Endpoint");
        ctx.Request.Scheme = "https";
        ctx.Request.Headers[HandoffLoopbackClient.HeaderName] = loopback.FormatHeader(IPAddress.Parse("8.8.8.8"));
        IPAddress? seen = null;
        await m.InvokeAsync(ctx, () => { seen = ctx.Connection.RemoteIpAddress; return Task.CompletedTask; });
        Assert.Equal(IPAddress.Loopback, seen);
    }

    [Fact]
    public async Task AValidMarkerFromAPeerThatIsNotThisHostIsIgnored()
    {
        var h = new Harness();
        var (m, loopback) = Create(h);
        var ctx = Request("GET", "/System/Endpoint");
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
        ctx.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10");
        ctx.Request.Headers[HandoffLoopbackClient.HeaderName] = loopback.FormatHeader(IPAddress.Parse("8.8.8.8"));
        IPAddress? seen = null;
        var headerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            headerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.HeaderName);
            return Task.CompletedTask;
        });
        Assert.Equal(IPAddress.Parse("198.51.100.7"), seen);
        Assert.False(headerSeen);

        // This host connecting to its own LAN address (peer == local) is honoured.
        var own = Request("GET", "/System/Endpoint");
        own.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        own.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10");
        own.Request.Headers[HandoffLoopbackClient.HeaderName] = loopback.FormatHeader(IPAddress.Parse("203.0.113.50"));
        await m.InvokeAsync(own, () => { seen = own.Connection.RemoteIpAddress; return Task.CompletedTask; });
        Assert.Equal(IPAddress.Parse("203.0.113.50"), seen);
    }

    // ------------------------------------------------------------------ F2: loopback PlaybackInfo bypass

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLoopbackPrewarmPlaybackInfoLeavesTheEntryUntouched(bool forFeature)
    {
        var h = new Harness { Mode = FeaturePreloadMode.Hot };
        var e = h.Register();
        var (m, loopback) = Create(h);
        var item = forFeature ? Feature : Intro1;
        var ctx = Request("POST", $"/Items/{item:N}/PlaybackInfo", "{}");
        ctx.Request.QueryString = new QueryString($"?ApiKey={Token}");
        ctx.Request.Headers[HandoffLoopbackClient.HeaderName] = loopback.FormatHeader(IPAddress.Parse("203.0.113.9"));
        IPAddress? seen = null;
        var markerSeen = true;
        await m.InvokeAsync(ctx, () =>
        {
            seen = ctx.Connection.RemoteIpAddress;
            markerSeen = ctx.Request.Headers.ContainsKey(HandoffLoopbackClient.HeaderName);
            ctx.User = Principal(Device);
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.False(e.Dropped);
        Assert.False(e.Consumed);
        Assert.Equal(0, e.PrewarmClaimed);
        Assert.Equal(0, h.Registry.ObservedDeviceCount);
        Assert.False(markerSeen);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), seen);
        Assert.Empty(h.Loopback.Requests);
    }

    // ------------------------------------------------------------------ F8: Off mode

    [Fact]
    public async Task OffModePlaybackInfoIsNotBufferedLookedUpOrRecorded()
    {
        var h = new Harness { Mode = FeaturePreloadMode.Off };
        var (m, _) = Create(h);
        var ctx = Request("POST", $"/Items/{Feature:N}/PlaybackInfo", "{\"x\":1}");
        var before = ctx.Request.Body;
        Stream? seenBody = null;
        await m.InvokeAsync(ctx, () =>
        {
            seenBody = ctx.Request.Body;
            ctx.User = Principal(Device);
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        Assert.Same(before, seenBody);
        Assert.Equal(0, before.Position);
        Assert.Empty(h.Loopback.Requests);
        Assert.Equal(0, h.Registry.ObservedDeviceCount);
        h.Mode = FeaturePreloadMode.Warm;
        Assert.False(h.Register().FeatureResolvedFirst);
    }

    // ------------------------------------------------------------------ fakes for IServer

    private sealed class FakeServer : IServer
    {
        public FakeServer(params string[] addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var a in addresses)
            {
                feature.Addresses.Add(a);
            }

            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ServerAddressesFeature : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = new List<string>();

        public bool PreferHostingUrls { get; set; }
    }

    private sealed class FakeServices : IServiceProvider
    {
        private readonly IServer _server;

        public FakeServices(IServer server) => _server = server;

        public object? GetService(Type serviceType) => serviceType == typeof(IServer) ? _server : null;
    }
}
