using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>What a playback report (ISessionManager event) said about an item.</summary>
internal enum HandoffPlaybackReport
{
    Start = 0,
    Progress = 1,
    Stopped = 2,
}

/// <summary>
/// Server-side feature handoff. While the prerolls of a play play, this:
///   Warm/Hot - resolves the feature's playback decision with the client's own DeviceProfile
///              (a "prewarm" PlaybackInfo made over loopback as that client), which backs
///              GET /Plugins/Projectionist/Upcoming for the web hook's direct-play pre-buffer;
///   Hot      - when that decision is a single-variant HLS transcode/remux, starts ffmpeg for it
///              (loopback master -> main -> init -> first segment with the PLAYER's User-Agent),
///              keeps it alive with ITranscodeManager pings while the device is demonstrably still
///              in the preroll, and when the player then asks for the feature's PlaybackInfo, hands
///              it the prewarm's session - its PlaySessionId and its exact TranscodingUrl - but only
///              if the fresh TranscodingUrl equals the prewarm's modulo PlaySessionId and
///              TranscodeReasons. Browsers can pre-download that session's first segments (the
///              Upcoming Transcode info), which are then marked cacheable.
///
/// Why this works (B report §2, §7c): an HLS job's files are keyed by
/// MD5(MediaPath-UserAgent-DeviceId-PlaySessionId) and every PlaybackInfo mints a new
/// PlaySessionId, so a player only adopts an earlier transcode if it is given that earlier
/// PlaySessionId and requests segments with the same UA and DeviceId. The request-level part
/// lives in <see cref="HandoffMiddleware"/>; this class owns the per-device state, the
/// background work and the decisions.
///
/// Bounds: pending entries are capped per user and globally, at most
/// <see cref="MaxConcurrentPrestarts"/> pre-started transcodes exist at once, a pre-started
/// transcode is only kept alive while the device reports playing an intro (and never beyond
/// <see cref="MaxPingedLifetime"/>), and feature-first observations only come from
/// authenticated 200 responses, in a capped map.
/// </summary>
public sealed class FeatureHandoffRegistry : IFeatureHandoffRegistry, IDisposable
{
    // Claim types of Jellyfin.Api.Constants.InternalClaimTypes (10.11); Jellyfin.Api is not a
    // package the plugin references, so the string values are pinned here.
    internal const string ClaimUserId = "Jellyfin-UserId";
    internal const string ClaimDeviceId = "Jellyfin-DeviceId";
    internal const string ClaimToken = "Jellyfin-Token";

    /// <summary>Longer device ids are not recorded as feature-first observations.</summary>
    internal const int MaxDeviceIdLength = 256;

    /// <summary>Feature-first observations are kept for at most this many devices (least recent evicted).</summary>
    internal const int MaxObservedDevices = 256;

    /// <summary>...and at most this many items per device (oldest evicted).</summary>
    internal const int MaxObservationsPerDevice = 8;

    /// <summary>Pending entries per user; the oldest is dropped beyond this.</summary>
    internal const int MaxEntriesPerUser = 3;

    /// <summary>Pending entries in total; the oldest is dropped beyond this.</summary>
    internal const int MaxEntries = 32;

    /// <summary>Pre-started transcodes that may exist at once; beyond this a play gets Warm-level only.</summary>
    internal const int MaxConcurrentPrestarts = 2;

    /// <summary>A feature PlaybackInfo this long before /Intros means the client resolved the feature first.</summary>
    internal static readonly TimeSpan FeatureFirstWindow = TimeSpan.FromSeconds(60);

    /// <summary>HLS jobs die 60 s after their last activity; ping well inside that.</summary>
    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>How long the pre-start waits for the intro's first stream request to learn the player's UA.</summary>
    internal static readonly TimeSpan PlayerUserAgentWait = TimeSpan.FromSeconds(20);

    /// <summary>No playback report / intro request from the device for this long: the preroll was abandoned.</summary>
    internal static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The intro reported paused for this long: stop keeping the feature's transcode alive.</summary>
    internal static readonly TimeSpan PausedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A pre-started transcode is never kept alive (pinged) longer than this.</summary>
    internal static readonly TimeSpan MaxPingedLifetime = TimeSpan.FromMinutes(20);

    /// <summary>After an intro is reported stopped at its end, the next intro's or the feature's PlaybackInfo is due within this.</summary>
    internal static readonly TimeSpan NextRequestGrace = TimeSpan.FromSeconds(30);

    /// <summary>An intro reported stopped more than this before its runtime was stopped midway, not played out.</summary>
    internal static readonly TimeSpan StoppedBeforeEndSlack = TimeSpan.FromSeconds(3);

    /// <summary>
    /// After an intro is reported stopped midway, the next item's PlaybackInfo is due within this: a
    /// "next" press (jellyfin-web reports the stop just before asking for the next item) still gets
    /// the handover, a user stop is abandoned within seconds (plus the sweep interval).
    /// </summary>
    internal static readonly TimeSpan StoppedMidwayGrace = TimeSpan.FromSeconds(5);

    /// <summary>After a handover, an HLS request carrying the session must arrive within this.</summary>
    internal static readonly TimeSpan AdoptionSupervision = TimeSpan.FromSeconds(30);

    /// <summary>A drop while a segment request may still be in flight kills again after this.</summary>
    internal static readonly TimeSpan RekillDelay = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan ExpiryGrace = TimeSpan.FromSeconds(90);

    /// <summary>Used until (or if never) the intros' runtime is known.</summary>
    internal static readonly TimeSpan ProvisionalExpiry = TimeSpan.FromMinutes(10);

    internal static readonly TimeSpan AdoptedMemory = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlaybackInfoTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Request headers replayed on the prewarm PlaybackInfo (auth + client identity).</summary>
    internal static readonly string[] ForwardedHeaderNames =
    {
        "Authorization",
        "X-Emby-Authorization",
        "X-Emby-Token",
        "X-MediaBrowser-Token",
        "User-Agent",
        "Accept-Language",
    };

    private readonly ILogger _logger;
    private readonly IHandoffTranscodeControl _transcode;
    private readonly IHandoffLoopback _loopback;
    private readonly Func<IReadOnlyList<Guid>, long?> _introRuntimeTicks;
    private readonly TimeProvider _time;
    private readonly Func<FeaturePreloadMode> _mode;
    private readonly ISessionManager? _sessionManager;
    private readonly Func<Guid, Guid, IReadOnlyList<Guid>, HandoffTrackContext?>? _trackContext;
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private volatile bool _stopping;

    // _gate guards _entries (and the writes to _byToken). Only authenticated or token-matched
    // requests and the registry's own background work ever take it.
    private readonly object _gate = new();
    private readonly Dictionary<string, HandoffEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // Lock-free lookups for the pre-auth middleware path: token -> its (few) entries.
    private readonly ConcurrentDictionary<string, HandoffEntry[]> _byToken = new(StringComparer.Ordinal);

    // Feature-first observations (authenticated 200 PlaybackInfo responses only), own lock.
    private readonly object _observedGate = new();
    private readonly Dictionary<string, List<(Guid ItemId, DateTimeOffset At)>> _observed = new(StringComparer.OrdinalIgnoreCase);

    // Handed-over sessions (post-adoption supervision) and sessions whose HLS responses may be cached.
    private readonly ConcurrentDictionary<string, AdoptedSession> _adopted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheableSession> _cacheable = new(StringComparer.Ordinal);

    private readonly ITimer? _sweeper;
    private volatile int _count;
    private volatile int _adoptedCount;
    private int _hotSlotsInUse;
    private bool _disposed;

    public FeatureHandoffRegistry(
        ILogger<FeatureHandoffRegistry> logger,
        ILibraryManager libraryManager,
        ITranscodeManager transcodeManager,
        ISessionManager sessionManager,
        HandoffLoopbackClient loopback,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        IHostApplicationLifetime lifetime)
        : this(
            logger,
            new HandoffTranscodeControl(transcodeManager),
            loopback,
            ids => SumRuntimeTicks(libraryManager, ids),
            TimeProvider.System,
            () => PreloadModes.Effective(Plugin.Instance?.Configuration),
            startSweeper: true,
            sessionManager,
            (userId, featureId, introIds) => HandoffTracks.FromServer(libraryManager, mediaSourceManager, userManager, userId, featureId, introIds),
            lifetime)
    {
    }

    internal FeatureHandoffRegistry(
        ILogger logger,
        IHandoffTranscodeControl transcode,
        IHandoffLoopback loopback,
        Func<IReadOnlyList<Guid>, long?> introRuntimeTicks,
        TimeProvider time,
        Func<FeaturePreloadMode> mode,
        bool startSweeper,
        ISessionManager? sessionManager = null,
        Func<Guid, Guid, IReadOnlyList<Guid>, HandoffTrackContext?>? trackContext = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _logger = logger;
        _trackContext = trackContext;
        _transcode = transcode;
        _loopback = loopback;
        _introRuntimeTicks = introRuntimeTicks;
        _time = time;
        _mode = mode;
        _sessionManager = sessionManager;
        if (sessionManager is not null)
        {
            // The keep-alive of a pre-started transcode follows what the device reports playing.
            sessionManager.PlaybackStart += OnPlaybackStart;
            sessionManager.PlaybackProgress += OnPlaybackProgress;
            sessionManager.PlaybackStopped += OnPlaybackStopped;
            sessionManager.SessionEnded += OnSessionEnded;
        }

        if (startSweeper)
        {
            _sweeper = time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        if (lifetime is not null)
        {
            _stoppingRegistration = lifetime.ApplicationStopping.Register(OnApplicationStopping);
        }
    }

    /// <summary>
    /// ApplicationStopping (SEC-R3-1): every pending play is dropped, which cancels its in-flight
    /// prewarm / pre-start / keep-alive work, and nothing new is tracked, so no background task
    /// carries on dialing the loopback while the listening sockets go away.
    /// </summary>
    internal void OnApplicationStopping()
    {
        _stopping = true;
        List<HandoffEntry> all;
        lock (_gate)
        {
            all = _entries.Values.ToList();
        }

        foreach (var e in all)
        {
            Drop(e, "server shutting down");
        }
    }

    /// <summary>Cheap check for the middleware's fast path.</summary>
    internal bool HasEntries => _count > 0;

    internal bool HasAdopted => _adoptedCount > 0;

    /// <summary>Pre-start slots currently held (tests / diagnostics).</summary>
    internal int HotSlotsInUse => Volatile.Read(ref _hotSlotsInUse);

    /// <summary>Devices with feature-first observations (tests / diagnostics).</summary>
    internal int ObservedDeviceCount
    {
        get
        {
            lock (_observedGate)
            {
                return _observed.Count;
            }
        }
    }

    internal FeaturePreloadMode Mode
    {
        get
        {
            try
            {
                return _mode();
            }
            catch
            {
                return FeaturePreloadMode.Off;
            }
        }
    }

    // ------------------------------------------------------------------ contract

    /// <inheritdoc />
    public void OnIntrosResolved(HttpContext? httpContext, BaseItem feature, User user, IReadOnlyList<Guid> introItemIds)
    {
        try
        {
            if (feature is null || user is null || introItemIds is null || introItemIds.Count == 0)
            {
                return;
            }

            if (Mode == FeaturePreloadMode.Off || _stopping)
            {
                return;
            }

            if (httpContext is null)
            {
                _logger.LogDebug("[Projectionist] handoff: /Intros for {Feature} outside a request; nothing to track", feature.Name);
                return;
            }

            var principal = httpContext.User;
            var deviceId = principal?.FindFirst(ClaimDeviceId)?.Value;
            var token = principal?.FindFirst(ClaimToken)?.Value;
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("[Projectionist] handoff: /Intros for {Feature} has no device id or token claim", feature.Name);
                return;
            }

            Register(
                user.Id,
                deviceId,
                feature.Id,
                feature.Name ?? feature.Id.ToString("N"),
                introItemIds,
                token,
                httpContext.Connection.RemoteIpAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: failed to record pending feature");
        }
    }

    /// <inheritdoc />
    public UpcomingFeatureInfo? GetUpcoming(Guid userId, string deviceId, Guid? currentItemId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var mode = Mode;
        if (mode == FeaturePreloadMode.Off)
        {
            return null;
        }

        HandoffEntry? e;
        lock (_gate)
        {
            _entries.TryGetValue(Key(userId, deviceId), out e);
        }

        if (e is null)
        {
            return null;
        }

        lock (e.Sync)
        {
            if (e.Dropped || _time.GetUtcNow() > e.ExpiresAt)
            {
                return null;
            }
        }

        if (currentItemId.HasValue && !e.HasIntro(currentItemId.Value))
        {
            return null;
        }

        var info = new UpcomingFeatureInfo
        {
            FeatureId = e.FeatureId.ToString("N"),
            IntroIds = e.IntroIds.Select(i => i.ToString("N")).ToList(),
            PlayMethod = "Unknown",
        };

        HandoffPlaybackInfo? prewarm;
        bool hadProfile;
        HandoffHotState hot;
        string? hotPsid;
        bool consumed;
        lock (e.Sync)
        {
            prewarm = e.Prewarm;
            hadProfile = e.PrewarmHadDeviceProfile;
            hot = e.Hot;
            hotPsid = e.HotPlaySessionId;
            consumed = e.Consumed;
        }

        var src = prewarm?.First;
        if (src is not null)
        {
            // Without a DeviceProfile the server skips the whole direct-play decision and reports
            // SupportsDirectPlay=true for anything (C report Q3), so that answer is not trusted.
            if (src.SupportsDirectPlay && hadProfile && !string.IsNullOrEmpty(src.Id) && !string.IsNullOrEmpty(src.Container))
            {
                info.PlayMethod = "DirectPlay";
                info.DirectPlay = new DirectPlayInfo
                {
                    Container = src.Container!,
                    MediaSourceId = src.Id!,
                    ETag = src.ETag,
                };
            }
            else if (!string.IsNullOrEmpty(src.TranscodingUrl))
            {
                info.PlayMethod = "Transcode";

                // H: the exact URL the player will be handed (the prewarm's, verbatim), so a browser
                // can pre-download its playlists and first segments into its HTTP cache.
                if (mode == FeaturePreloadMode.Hot
                    && !consumed
                    && hotPsid is not null
                    && hot is HandoffHotState.Starting or HandoffHotState.Started)
                {
                    info.Transcode = new TranscodePrefetchInfo
                    {
                        Url = src.TranscodingUrl!,
                        Ready = hot == HandoffHotState.Started,
                    };
                }
            }
        }

        return info;
    }

    // ------------------------------------------------------------------ registration

    internal static string Key(Guid userId, string deviceId) => userId.ToString("N") + "\n" + deviceId;

    internal HandoffEntry Register(
        Guid userId,
        string deviceId,
        Guid featureId,
        string featureName,
        IReadOnlyList<Guid> introIds,
        string token,
        IPAddress? clientIp)
    {
        var now = _time.GetUtcNow();
        HandoffEntry entry;
        HandoffEntry? replaced = null;
        var evicted = new List<HandoffEntry>();
        var key = Key(userId, deviceId);
        var featureFirst = WasObserved(userId, deviceId, featureId, now);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing)
                && !existing.Dropped
                && existing.FeatureId == featureId
                && string.Equals(existing.Token, token, StringComparison.Ordinal))
            {
                // Same play asked twice (hook + native, or a retry): widen the intro set, keep any prewarm.
                existing.AddIntros(introIds);
                existing.Touch(now);
                ScheduleExpiry(existing, now);
                _logger.LogDebug("[Projectionist] handoff: /Intros repeated for {Feature} on device {Device}", featureName, deviceId);
                return existing;
            }

            replaced = existing;
            if (replaced is not null)
            {
                RemoveLocked(replaced);
            }

            // SEC-3: bounded per user and globally; the oldest pending play gives way.
            while (_entries.Values.Count(e => e.UserId == userId) >= MaxEntriesPerUser)
            {
                var oldest = _entries.Values.Where(e => e.UserId == userId).OrderBy(e => e.CreatedAt).First();
                RemoveLocked(oldest);
                evicted.Add(oldest);
            }

            while (_entries.Count >= MaxEntries)
            {
                var oldest = _entries.Values.OrderBy(e => e.CreatedAt).First();
                RemoveLocked(oldest);
                evicted.Add(oldest);
            }

            entry = new HandoffEntry(userId, deviceId, featureId, featureName, introIds, token, clientIp, now, now + ProvisionalExpiry, featureFirst);
            _entries[key] = entry;
            _byToken.AddOrUpdate(token, _ => new[] { entry }, (_, list) => list.Append(entry).ToArray());
            _count = _entries.Count;
        }

        if (replaced is not null)
        {
            Drop(replaced, "replaced by a newer /Intros from the same device");
        }

        foreach (var e in evicted)
        {
            Drop(e, "too many pending plays; the oldest gives way");
        }

        ScheduleExpiry(entry, now);
        _logger.LogInformation(
            "[Projectionist] handoff: pending {Feature} ({FeatureId}) after {Count} intro(s) on device {Device}, mode {Mode}{Note}",
            featureName,
            featureId.ToString("N"),
            introIds.Count,
            deviceId,
            Mode,
            entry.FeatureResolvedFirst ? "; feature PlaybackInfo already requested by this device, nothing will be pre-started" : string.Empty);
        return entry;
    }

    /// <summary>Expiry = intro runtime x1.5 + 90 s, computed off the request thread (library lookups may hit the DB).</summary>
    private void ScheduleExpiry(HandoffEntry entry, DateTimeOffset from)
    {
        var ids = entry.IntroIds;
        _ = Task.Run(() =>
        {
            try
            {
                var ticks = _introRuntimeTicks(ids);
                if (ticks is > 0)
                {
                    var expiry = from + TimeSpan.FromTicks((long)(ticks.Value * 1.5)) + ExpiryGrace;
                    lock (entry.Sync)
                    {
                        entry.ExpiresAt = expiry;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Projectionist] handoff: intro runtime lookup failed; keeping provisional expiry");
            }
        });
    }

    private static long? SumRuntimeTicks(ILibraryManager libraryManager, IReadOnlyList<Guid> ids)
    {
        long total = 0;
        var any = false;
        foreach (var id in ids)
        {
            var ticks = libraryManager.GetItemById(id)?.RunTimeTicks;
            if (ticks is > 0)
            {
                total += ticks.Value;
                any = true;
            }
        }

        return any ? total : null;
    }

    // ------------------------------------------------------------------ observations from the middleware

    /// <summary>
    /// An AUTHENTICATED client PlaybackInfo POST answered 200 (the middleware calls this after the
    /// inner pipeline, with the principal's user and device id claims), for the feature-first rule.
    /// Keyed by (user, device) like the entries (SEC-RR-2): the device id claim is whatever the
    /// client put in its Authorization header, so one user's observation must never affect another
    /// user's play. Bounded: long device ids are ignored, at most <see cref="MaxObservedDevices"/>
    /// (user, device) pairs (least recently observed evicted) and
    /// <see cref="MaxObservationsPerDevice"/> items each. Old observations are pruned by
    /// <see cref="Sweep"/>, not here.
    /// </summary>
    internal void ObservePlaybackInfo(Guid userId, string? deviceId, Guid itemId)
    {
        if (userId == Guid.Empty || string.IsNullOrEmpty(deviceId) || deviceId.Length > MaxDeviceIdLength)
        {
            return;
        }

        var key = Key(userId, deviceId);
        var now = _time.GetUtcNow();
        lock (_observedGate)
        {
            if (!_observed.TryGetValue(key, out var list))
            {
                if (_observed.Count >= MaxObservedDevices)
                {
                    string? lru = null;
                    var lruAt = DateTimeOffset.MaxValue;
                    foreach (var (k, v) in _observed)
                    {
                        var last = v.Count == 0 ? DateTimeOffset.MinValue : v[^1].At;
                        if (last < lruAt)
                        {
                            lru = k;
                            lruAt = last;
                        }
                    }

                    if (lru is not null)
                    {
                        _observed.Remove(lru);
                    }
                }

                list = new List<(Guid, DateTimeOffset)>();
                _observed[key] = list;
            }

            list.RemoveAll(o => o.ItemId == itemId);
            list.Add((itemId, now));
            if (list.Count > MaxObservationsPerDevice)
            {
                list.RemoveRange(0, list.Count - MaxObservationsPerDevice);
            }
        }
    }

    private bool WasObserved(Guid userId, string deviceId, Guid itemId, DateTimeOffset now)
    {
        lock (_observedGate)
        {
            return _observed.TryGetValue(Key(userId, deviceId), out var list)
                   && list.Any(o => o.ItemId == itemId && now - o.At <= FeatureFirstWindow);
        }
    }

    private void PruneObserved(DateTimeOffset now)
    {
        lock (_observedGate)
        {
            List<string>? empty = null;
            foreach (var (k, list) in _observed)
            {
                list.RemoveAll(o => now - o.At > FeatureFirstWindow);
                if (list.Count == 0)
                {
                    (empty ??= new List<string>()).Add(k);
                }
            }

            if (empty is not null)
            {
                foreach (var k in empty)
                {
                    _observed.Remove(k);
                }
            }
        }
    }

    /// <summary>
    /// The pending entry of the device a request comes from; the token must match the one seen at
    /// /Intros. O(1) and lock-free (token index), so pre-auth requests that match nothing cost
    /// one dictionary lookup.
    /// </summary>
    internal HandoffEntry? Find(string? deviceId, string? token)
    {
        if (_count == 0 || string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (!_byToken.TryGetValue(token, out var candidates))
        {
            return null;
        }

        foreach (var e in candidates)
        {
            if (!e.Dropped
                && (deviceId is null || string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>First stream/HLS request of an intro: remember the player's User-Agent.</summary>
    internal void OnIntroStreamRequest(HandoffEntry entry, string? userAgent)
    {
        entry.Touch(_time.GetUtcNow());
        if (!string.IsNullOrEmpty(userAgent) && entry.TrySetPlayerUserAgent(userAgent))
        {
            _logger.LogDebug("[Projectionist] handoff: player UA for device {Device}: {UA}", entry.DeviceId, userAgent);
        }
    }

    /// <summary>
    /// A PlaybackInfo for intro <paramref name="introId"/> succeeded: the chain goes on, and that
    /// intro is now the current one, so a report about the previous intro that the server delivers
    /// late (its Stopped is queued after user-data saves and event consumers, rr-C4) is stale.
    /// </summary>
    internal void OnIntroActivity(HandoffEntry entry, Guid introId)
    {
        var now = _time.GetUtcNow();
        lock (entry.Sync)
        {
            entry.LastSeen = now > entry.LastSeen ? now : entry.LastSeen;
            entry.NextRequestDeadline = null;
            if (introId != Guid.Empty && entry.HasIntro(introId))
            {
                entry.CurrentIntro = introId;
            }
        }
    }

    /// <summary>(a) The client's PlaybackInfo for a pending intro succeeded: start the feature's prewarm once.</summary>
    internal void OnIntroPlaybackInfo(HandoffEntry entry, HandoffIntroRequest request)
    {
        if (entry.FeatureResolvedFirst || entry.Dropped || _stopping)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref entry.PrewarmClaimed, 1, 0) != 0)
        {
            return;
        }

        var ct = entry.Cancellation.Token;
        _ = Task.Run(() => RunPrewarmAsync(entry, request, ct), CancellationToken.None);
    }

    /// <summary>
    /// True when a fresh feature PlaybackInfo from this device may be handed the prewarm's session.
    /// A Started pre-start must still have its job on the server (an ffmpeg that crashed or was
    /// killed is not handed over).
    /// </summary>
    internal bool IsAdoptable(HandoffEntry entry)
    {
        string? psid;
        HandoffHotState hot;
        lock (entry.Sync)
        {
            if (entry.Dropped
                || entry.Consumed
                || entry.HotPlaySessionId is null
                || entry.Hot is not (HandoffHotState.Starting or HandoffHotState.Started)
                || entry.Prewarm?.First?.TranscodingUrl is null)
            {
                return false;
            }

            psid = entry.HotPlaySessionId;
            hot = entry.Hot;
        }

        if (hot == HandoffHotState.Started && !_transcode.Exists(psid))
        {
            _logger.LogInformation(
                "[Projectionist] handoff: prewarmed transcode {Psid} of device {Device} no longer exists on the server; not handing it over",
                psid,
                entry.DeviceId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// (c) The fresh feature PlaybackInfo response (identity-encoded) for an adoptable entry.
    /// Returns the bytes to send: handed over onto the prewarm's session (its PlaySessionId and
    /// its exact TranscodingUrl) when the decision is identical, otherwise the fresh bytes
    /// untouched (and the prewarm is killed).
    /// </summary>
    internal byte[] CompleteFeaturePlaybackInfo(HandoffEntry entry, int statusCode, string? contentType, string? contentEncoding, byte[] body)
    {
        try
        {
            if (statusCode != StatusCodes.Status200OK
                || contentType is null
                || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                Drop(entry, $"feature PlaybackInfo returned {statusCode} {contentType} {contentEncoding}".TrimEnd());
                return body;
            }

            var fresh = HandoffJson.ParsePlaybackInfo(body);
            HandoffPlaybackInfo? prewarm;
            byte[]? prewarmBody;
            string? psid;
            lock (entry.Sync)
            {
                prewarm = entry.Prewarm;
                prewarmBody = entry.PrewarmBody;
                psid = entry.HotPlaySessionId;
            }

            var f = fresh?.First;
            var p = prewarm?.First;
            string? mismatch = null;
            if (fresh is null || f is null || p is null || psid is null || fresh.PlaySessionId is null || prewarmBody is null)
            {
                mismatch = "unreadable response";
            }
            else if (fresh.ErrorCode is not null)
            {
                mismatch = "error " + fresh.ErrorCode;
            }
            else if (!string.Equals(f.Id, p.Id, StringComparison.Ordinal))
            {
                mismatch = "another media source was chosen";
            }
            else if (f.SupportsDirectPlay != p.SupportsDirectPlay)
            {
                mismatch = "direct-play decision differs";
            }
            else if (!HandoffJson.TranscodingUrlsMatch(f.TranscodingUrl, p.TranscodingUrl))
            {
                // The URLs carry the user's ApiKey: only the NAMES of the differing parameters are logged.
                mismatch = "transcode parameters differ: "
                    + string.Join(", ", HandoffJson.DifferingParamNames(f.TranscodingUrl, p.TranscodingUrl));
            }

            if (mismatch is not null)
            {
                _logger.LogInformation(
                    "[Projectionist] handoff: not handing prewarmed session {Psid} to device {Device} ({Reason}); fresh response passed through",
                    psid,
                    entry.DeviceId,
                    mismatch);
                Drop(entry, "fresh decision differs from the prewarm");
                return body;
            }

            var handedOver = HandoffJson.HandOverToPrewarm(body, fresh!.PlaySessionId!, psid!, prewarmBody!);
            if (handedOver is null)
            {
                Drop(entry, "could not rewrite the fresh response");
                return body;
            }

            string? ua;
            lock (entry.Sync)
            {
                if (entry.Dropped)
                {
                    // Expired or dropped while the fresh response was computed: the job is being killed.
                    return body;
                }

                entry.Consumed = true;
                ua = entry.HotUserAgent;
            }

            // The ping loop ends by itself at its next tick (Consumed); the job is the player's now.
            Remove(entry);
            ReleaseHotSlot(entry);
            _adopted[psid!] = new AdoptedSession(entry.DeviceId, entry.Token, ua ?? string.Empty, entry.FeatureId, _time.GetUtcNow());
            _adoptedCount = _adopted.Count;

            _logger.LogInformation(
                "[Projectionist] handoff: handed prewarmed transcode {Psid} to device {Device} for {Feature} (fresh session {Fresh} discarded)",
                psid,
                entry.DeviceId,
                entry.FeatureName,
                fresh.PlaySessionId);
            return handedOver;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: feature PlaybackInfo handling failed; passing it through");
            Drop(entry, "exception");
            return body;
        }
    }

    /// <summary>
    /// H: true when a GET for <paramref name="itemId"/> carrying <paramref name="playSessionId"/> and
    /// <paramref name="token"/> belongs to a pre-started (or handed-over) session of that token, so
    /// its playlists/segments may be marked cacheable. Lock-free.
    /// </summary>
    internal bool IsCacheableSession(string? playSessionId, string? token, Guid itemId)
        => !string.IsNullOrEmpty(playSessionId)
           && !string.IsNullOrEmpty(token)
           && _cacheable.TryGetValue(playSessionId, out var c)
           && c.FeatureId == itemId
           && string.Equals(c.Token, token, StringComparison.Ordinal);

    /// <summary>
    /// A feature request carrying a handed-over PlaySessionId (COMPAT-3). The first one decides:
    /// an HLS request means the player uses the session; anything else (a static/direct stream,
    /// e.g. Roku's tryDirect) means the pre-started transcode is unused, so it is killed.
    /// </summary>
    internal void ObserveAdoptedStream(string? playSessionId, string? token, string? userAgent, bool isHls)
    {
        if (string.IsNullOrEmpty(playSessionId) || !_adopted.TryGetValue(playSessionId, out var a))
        {
            return;
        }

        if (!string.IsNullOrEmpty(token) && !string.Equals(a.Token, token, StringComparison.Ordinal))
        {
            return;
        }

        bool log;
        bool kill = false;
        lock (a)
        {
            log = !a.Logged;
            a.Logged = true;
            if (!a.Decided)
            {
                a.Decided = true;
                a.HlsSeen = isHls;
                kill = !isHls;
            }
        }

        if (log && isHls)
        {
            if (string.Equals(a.UserAgent, userAgent, StringComparison.Ordinal))
            {
                _logger.LogInformation("[Projectionist] handoff: player is streaming handed-over session {Psid} with the pre-start's User-Agent", playSessionId);
            }
            else
            {
                _logger.LogWarning(
                    "[Projectionist] handoff: player streams handed-over session {Psid} with a different User-Agent ({UA}); the server will start its own transcode",
                    playSessionId,
                    userAgent);
            }
        }

        if (kill)
        {
            KillAdopted(playSessionId, a, "the player direct-streams the handed-over session instead of using its HLS transcode");
        }
    }

    /// <summary>
    /// rr-C2: the player's own (non-automated) Start/Progress report for a handed-over session.
    /// With PlayMethod Transcode it is playing that HLS session - possibly entirely from the
    /// browser's HTTP cache (the pre-downloaded playlists and first segments), paused before its
    /// first uncached segment, so no request with the PlaySessionId reaches the server for a
    /// while. That decides the supervision the same way an HLS request does: the job is the
    /// player's and is never timer-killed (the server itself keeps it alive from these reports,
    /// TranscodeManager.OnPlaybackProgress). A DirectPlay/DirectStream report of the session
    /// before anything decided means the transcode is unused: killed, like a static first request.
    /// </summary>
    internal void OnAdoptedPlaybackReport(string? playSessionId, string? deviceId, Guid itemId, PlayMethod? playMethod)
    {
        if (string.IsNullOrEmpty(playSessionId)
            || playMethod is null
            || !_adopted.TryGetValue(playSessionId, out var a)
            || !string.Equals(a.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            || (itemId != Guid.Empty && itemId != a.FeatureId))
        {
            return;
        }

        var kill = false;
        lock (a)
        {
            if (a.Decided)
            {
                return;
            }

            a.Decided = true;
            a.HlsSeen = playMethod == PlayMethod.Transcode;
            kill = !a.HlsSeen;
        }

        if (kill)
        {
            KillAdopted(playSessionId, a, "the player reports " + playMethod + " for the handed-over session instead of its HLS transcode");
        }
        else
        {
            _logger.LogInformation("[Projectionist] handoff: player reports playing handed-over session {Psid} as a transcode", playSessionId);
        }
    }

    private void KillAdopted(string psid, AdoptedSession a, string reason)
    {
        _cacheable.TryRemove(psid, out _);
        _logger.LogInformation("[Projectionist] handoff: killing handed-over transcode {Psid} of device {Device} ({Reason})", psid, a.DeviceId, reason);
        var deviceId = a.DeviceId;
        _ = Task.Run(() => KillAsync(deviceId, psid, false));
    }

    // ------------------------------------------------------------------ playback reports (ISessionManager)

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        => OnPlaybackEvent(e, HandoffPlaybackReport.Start, false);

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        => OnPlaybackEvent(e, HandoffPlaybackReport.Progress, false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        => OnPlaybackEvent(e, HandoffPlaybackReport.Stopped, e is not null && PlayedOut(e.PlayedToCompletion, e.PlaybackPositionTicks, e.Item?.RunTimeTicks));

    /// <summary>
    /// Whether a stopped intro was played to its end. Jellyfin's PlayedToCompletion alone does not
    /// say so for prerolls: any item shorter than MinResumeDurationSeconds (300 s by default) that
    /// is stopped past MinResumePct (5 %) is marked played (UserDataManager.UpdatePlayState), so a
    /// user stopping a 14 s preroll after 5 s is reported as PlayedToCompletion=true. A reported
    /// position well before the item's runtime is therefore a stop.
    /// </summary>
    internal static bool PlayedOut(bool playedToCompletion, long? positionTicks, long? runTimeTicks)
        => playedToCompletion
           && !(positionTicks is > 0 && runTimeTicks is > 0 && positionTicks.Value < runTimeTicks.Value - StoppedBeforeEndSlack.Ticks);

    private void OnPlaybackEvent(PlaybackProgressEventArgs e, HandoffPlaybackReport kind, bool playedToCompletion)
    {
        try
        {
            if (e is null || (_count == 0 && _adoptedCount == 0))
            {
                return;
            }

            var deviceId = e.DeviceId ?? e.Session?.DeviceId;
            var itemId = e.Item?.Id ?? e.MediaInfo?.Id ?? Guid.Empty;
            if (_adoptedCount > 0 && kind != HandoffPlaybackReport.Stopped && !e.IsAutomated)
            {
                OnAdoptedPlaybackReport(e.PlaySessionId, deviceId, itemId, e.Session?.PlayState?.PlayMethod);
            }

            if (_count == 0)
            {
                return;
            }

            var userId = e.Session?.UserId ?? e.Users?.FirstOrDefault()?.Id ?? Guid.Empty;
            OnPlaybackReport(userId, deviceId, itemId, kind, e.IsPaused, playedToCompletion, e.IsAutomated);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: playback report handling failed");
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        try
        {
            var session = e?.SessionInfo;
            if (session is not null)
            {
                OnDeviceSessionEnded(session.UserId, session.DeviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: session end handling failed");
        }
    }

    /// <summary>
    /// What the device reports playing. Start/Progress of a pending intro: the device is still in
    /// the preroll (and paused or not). Stopped of the current intro before its end (user stop,
    /// back, error, or a "next" press): the next item's PlaybackInfo is due within
    /// <see cref="StoppedMidwayGrace"/>, otherwise the play is abandoned. Stopped at its end: the
    /// next intro's or the feature's PlaybackInfo is due within <see cref="NextRequestGrace"/>.
    ///
    /// <paramref name="isAutomated"/> Progress is ignored (rr-C3): Jellyfin re-reports the last
    /// state every second on its own (SessionInfo.StartAutomaticProgress) until the position
    /// reaches the item's runtime, whether or not the client is still there, so it says nothing
    /// about the device. Real clients report well inside <see cref="ActivityTimeout"/>
    /// (jellyfin-web every 10 s, Android TV every 3 s, Roku about every 30 s).
    /// </summary>
    internal void OnPlaybackReport(Guid userId, string? deviceId, Guid itemId, HandoffPlaybackReport kind, bool isPaused, bool playedToCompletion, bool isAutomated = false)
    {
        if (_count == 0 || string.IsNullOrEmpty(deviceId) || itemId == Guid.Empty)
        {
            return;
        }

        if (isAutomated && kind == HandoffPlaybackReport.Progress)
        {
            return;
        }

        HandoffEntry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(Key(userId, deviceId), out entry);
        }

        if (entry is null || entry.Dropped || !entry.HasIntro(itemId))
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (entry.Sync)
        {
            // Reports can arrive out of order (the server queues them); one about an intro other
            // than the one last started is stale.
            var stale = entry.CurrentIntro is not null && entry.CurrentIntro != itemId;
            switch (kind)
            {
                case HandoffPlaybackReport.Start:
                    entry.CurrentIntro = itemId;
                    entry.NextRequestDeadline = null;
                    entry.LastSeen = now;
                    entry.PausedSince = isPaused ? now : null;
                    break;
                case HandoffPlaybackReport.Progress when !stale:
                    entry.CurrentIntro ??= itemId;
                    entry.LastSeen = now;
                    entry.PausedSince = isPaused ? entry.PausedSince ?? now : null;
                    break;
                case HandoffPlaybackReport.Stopped when !stale:
                    entry.LastSeen = now;
                    entry.PausedSince = null;
                    entry.StoppedMidway = !playedToCompletion;
                    entry.NextRequestDeadline = now + (playedToCompletion ? NextRequestGrace : StoppedMidwayGrace);
                    break;
            }
        }
    }

    /// <summary>The device's session ended: whatever it had pending is abandoned.</summary>
    internal void OnDeviceSessionEnded(Guid userId, string? deviceId)
    {
        if (_count == 0 || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        HandoffEntry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(Key(userId, deviceId), out entry);
        }

        if (entry is not null)
        {
            Drop(entry, "the device's session ended");
        }
    }

    // ------------------------------------------------------------------ drop / expiry

    /// <summary>Forget the entry and kill its pre-started transcode unless the player adopted it.</summary>
    internal void Drop(HandoffEntry entry, string reason) => Drop(entry, reason, false);

    /// <summary>
    /// <paramref name="rekill"/>: the pre-start did not complete cleanly, so a segment request of
    /// ours may still be in flight on the server; kill again after <see cref="RekillDelay"/>.
    /// </summary>
    private void Drop(HandoffEntry entry, string reason, bool rekill)
    {
        string? killPsid = null;
        string? cachedPsid = null;
        var again = false;
        lock (entry.Sync)
        {
            if (entry.Dropped)
            {
                return;
            }

            entry.Dropped = true;
            if (!entry.Consumed && entry.HotPlaySessionId is not null)
            {
                cachedPsid = entry.HotPlaySessionId;
                if (entry.Hot != HandoffHotState.None)
                {
                    killPsid = entry.HotPlaySessionId;
                    again = rekill || entry.Hot == HandoffHotState.Starting;
                }
            }
        }

        Remove(entry);
        ReleaseHotSlot(entry);
        if (cachedPsid is not null)
        {
            _cacheable.TryRemove(cachedPsid, out _);
        }

        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (killPsid is not null)
        {
            _logger.LogInformation(
                "[Projectionist] handoff: killing prewarmed transcode {Psid} of device {Device} ({Reason})",
                killPsid,
                entry.DeviceId,
                reason);
            // Off the caller's thread: TranscodingJob.Stop() blocks while ffmpeg quits, and the
            // caller may be the feature PlaybackInfo response.
            var deviceId = entry.DeviceId;
            _ = Task.Run(() => KillAsync(deviceId, killPsid, again));
        }
        else
        {
            _logger.LogDebug("[Projectionist] handoff: dropped pending {Feature} for device {Device} ({Reason})", entry.FeatureName, entry.DeviceId, reason);
        }
    }

    private async Task KillAsync(string deviceId, string psid, bool again)
    {
        try
        {
            await _transcode.Kill(deviceId, psid).ConfigureAwait(false);
            if (again)
            {
                // A segment request that was in flight when we gave up may still spawn ffmpeg.
                await Task.Delay(RekillDelay, _time).ConfigureAwait(false);
                await _transcode.Kill(deviceId, psid).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: killing transcode {Psid} failed", psid);
        }
    }

    private void Remove(HandoffEntry entry)
    {
        lock (_gate)
        {
            RemoveLocked(entry);
        }
    }

    private void RemoveLocked(HandoffEntry entry)
    {
        var key = Key(entry.UserId, entry.DeviceId);
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);
        }

        if (_byToken.TryGetValue(entry.Token, out var list) && list.Contains(entry))
        {
            var rest = list.Where(e => !ReferenceEquals(e, entry)).ToArray();
            if (rest.Length == 0)
            {
                _byToken.TryRemove(entry.Token, out _);
            }
            else
            {
                _byToken[entry.Token] = rest;
            }
        }

        _count = _entries.Count;
    }

    private bool TryAcquireHotSlot(HandoffEntry entry)
    {
        while (true)
        {
            var current = Volatile.Read(ref _hotSlotsInUse);
            if (current >= MaxConcurrentPrestarts)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _hotSlotsInUse, current + 1, current) == current)
            {
                Volatile.Write(ref entry.HotSlot, 1);
                return true;
            }
        }
    }

    private void ReleaseHotSlot(HandoffEntry entry)
    {
        if (Interlocked.Exchange(ref entry.HotSlot, 0) == 1)
        {
            Interlocked.Decrement(ref _hotSlotsInUse);
        }
    }

    /// <summary>
    /// Runs every few seconds: drops expired entries, entries whose preroll ended without the
    /// feature being requested, and everything when the mode is Off; forgets old observations;
    /// kills handed-over transcodes no HLS request ever used.
    /// </summary>
    internal void Sweep()
    {
        try
        {
            var now = _time.GetUtcNow();
            var off = Mode == FeaturePreloadMode.Off;
            var drops = new List<(HandoffEntry Entry, string Reason)>();
            lock (_gate)
            {
                foreach (var e in _entries.Values)
                {
                    lock (e.Sync)
                    {
                        if (off)
                        {
                            drops.Add((e, "feature preload was switched off"));
                        }
                        else if (now > e.ExpiresAt)
                        {
                            drops.Add((e, "expired: the feature was never requested"));
                        }
                        else if (e.NextRequestDeadline is { } deadline && now > deadline)
                        {
                            drops.Add((e, e.StoppedMidway ? "the device stopped the preroll" : "the preroll ended and the feature was not requested"));
                        }
                    }
                }
            }

            foreach (var (e, reason) in drops)
            {
                Drop(e, reason);
            }

            PruneObserved(now);

            foreach (var (psid, a) in _adopted)
            {
                bool kill;
                lock (a)
                {
                    kill = !a.Decided && now - a.At > AdoptionSupervision;
                    if (kill)
                    {
                        a.Decided = true;
                    }
                }

                if (kill)
                {
                    KillAdopted(psid, a, "no HLS request used the handed-over session");
                }

                if (now - a.At > AdoptedMemory)
                {
                    _adopted.TryRemove(psid, out _);
                    _cacheable.TryRemove(psid, out _);
                }
            }

            _adoptedCount = _adopted.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: sweep failed");
        }
    }

    // ------------------------------------------------------------------ background work

    private async Task RunPrewarmAsync(HandoffEntry entry, HandoffIntroRequest intro, CancellationToken ct)
    {
        try
        {
            var body = HandoffJson.DerivePrewarmBody(intro.Body, out var hadProfile);
            var query = HandoffJson.DerivePrewarmQuery(intro.QueryString);
            var sw = Stopwatch.StartNew();
            var resp = await _loopback.SendAsync(
                new HandoffHttpRequest(
                    HttpMethod.Post,
                    $"/Items/{entry.FeatureId:N}/PlaybackInfo{query}",
                    intro.Headers,
                    body,
                    entry.ClientIp,
                    CaptureBody: true,
                    PlaybackInfoTimeout),
                ct).ConfigureAwait(false);
            if (resp.Status != StatusCodes.Status200OK)
            {
                _logger.LogWarning(
                    "[Projectionist] handoff: prewarm PlaybackInfo for {Feature} returned {Status}",
                    entry.FeatureName,
                    resp.Status);
                return;
            }

            var info = HandoffJson.ParsePlaybackInfo(resp.Body);
            var src = info?.First;
            if (info is null || src is null || string.IsNullOrEmpty(info.PlaySessionId))
            {
                _logger.LogWarning("[Projectionist] handoff: prewarm PlaybackInfo for {Feature} was unreadable", entry.FeatureName);
                return;
            }

            var infoBody = resp.Body;
            if (hadProfile && src.SupportsDirectPlay && !string.IsNullOrEmpty(src.Id))
            {
                // rr-C1: a direct-play decision made without track indexes only holds for a Play
                // that sends none (cards, home screen). jellyfin-web's details page sends the
                // source and its audio/subtitle indexes, and with an explicit audio index the
                // server also checks that track's codec (e.g. AC3 in a browser): ask again in
                // that shape, and trust DirectPlay only when both agree on the same source.
                // R3-COR-1/2: that shape is predicted from the item's static sources for this
                // user, as the page has them, not from this response (re-sorted, own track pick).
                var details = PredictDetailsPage(entry, src);
                var explicitBody = HandoffJson.DeriveExplicitTracksBody(body, details.MediaSourceId, details.AudioStreamIndex, details.SubtitleStreamIndex);
                var resp2 = await _loopback.SendAsync(
                    new HandoffHttpRequest(
                        HttpMethod.Post,
                        $"/Items/{entry.FeatureId:N}/PlaybackInfo{query}",
                        intro.Headers,
                        explicitBody,
                        entry.ClientIp,
                        CaptureBody: true,
                        PlaybackInfoTimeout),
                    ct).ConfigureAwait(false);
                var info2 = resp2.Status == StatusCodes.Status200OK ? HandoffJson.ParsePlaybackInfo(resp2.Body) : null;
                var src2 = info2?.First;
                if (info2 is null || src2 is null || string.IsNullOrEmpty(info2.PlaySessionId) || !string.Equals(src2.Id, details.MediaSourceId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "[Projectionist] handoff: prewarm PlaybackInfo with explicit tracks for {Feature} returned {Status}; its direct-play decision is not advertised",
                        entry.FeatureName,
                        resp2.Status);
                    return;
                }

                if (!src2.SupportsDirectPlay)
                {
                    _logger.LogInformation(
                        "[Projectionist] handoff: {Feature} direct-plays only when no audio track is named; a Play with explicit tracks (jellyfin-web's details page) does not, so that decision is the prewarm",
                        entry.FeatureName);
                    info = info2;
                    src = src2;
                    infoBody = resp2.Body;
                }
                else if (!string.Equals(src2.Id, src.Id, StringComparison.OrdinalIgnoreCase))
                {
                    // Both direct-play, but a card Play and a details-page Play play different
                    // versions: no one static URL to pre-buffer.
                    _logger.LogInformation(
                        "[Projectionist] handoff: {Feature} direct-plays version {A} from a card but {B} from the details page; its direct-play decision is not advertised",
                        entry.FeatureName,
                        src.Id,
                        src2.Id);
                    return;
                }
            }

            lock (entry.Sync)
            {
                entry.Prewarm = info;
                entry.PrewarmBody = infoBody;
                entry.PrewarmHadDeviceProfile = hadProfile;
            }

            var method = src.SupportsDirectPlay ? "DirectPlay" : src.TranscodingUrl is not null ? "Transcode" : "none";
            _logger.LogInformation(
                "[Projectionist] handoff: prewarm PlaybackInfo for {Feature} on device {Device}: {Method}, PlaySessionId {Psid}, {Ms} ms",
                entry.FeatureName,
                entry.DeviceId,
                method,
                info.PlaySessionId,
                sw.ElapsedMilliseconds);

            if (Mode != FeaturePreloadMode.Hot || src.SupportsDirectPlay || string.IsNullOrEmpty(src.TranscodingUrl))
            {
                return;
            }

            if (!IsHlsUrl(src.TranscodingUrl, src.TranscodingSubProtocol))
            {
                // Progressive transcodes die 10 s after the last reader and restart from byte 0; nothing to keep warm.
                _logger.LogInformation("[Projectionist] handoff: {Feature} transcodes progressively; not pre-starting", entry.FeatureName);
                return;
            }

            var ua = await entry.WaitForPlayerUserAgentAsync(PlayerUserAgentWait, _time, ct).ConfigureAwait(false);
            if (ua is null)
            {
                _logger.LogInformation(
                    "[Projectionist] handoff: no intro stream request seen from device {Device}; not pre-starting {Feature} (the player's User-Agent is unknown)",
                    entry.DeviceId,
                    entry.FeatureName);
                return;
            }

            await PrestartAsync(entry, info.PlaySessionId, src.TranscodingUrl, ua, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dropped/expired/consumed: nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: prewarm for {Feature} failed", entry.FeatureName);
            Drop(entry, "pre-start failed", rekill: true);
        }
    }

    /// <summary>
    /// The details-page Play's MediaSourceId and track indexes: from the item's static sources for
    /// this user (<see cref="HandoffTracks"/>), or, when those cannot be read, the index-less
    /// response's first source and its default tracks.
    /// </summary>
    private HandoffTrackChoice PredictDetailsPage(HandoffEntry entry, HandoffMediaSource src)
    {
        try
        {
            var context = _trackContext?.Invoke(entry.UserId, entry.FeatureId, entry.IntroIds);
            if (context is not null)
            {
                var choice = HandoffTracks.PredictDetailsPage(context);
                _logger.LogDebug(
                    "[Projectionist] handoff: details-page shape of {Feature}: source {Source}, audio {Audio}, subtitles {Subtitle}",
                    entry.FeatureName,
                    choice.MediaSourceId,
                    choice.AudioStreamIndex,
                    choice.SubtitleStreamIndex);
                return choice;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: could not read the static sources of {Feature}", entry.FeatureName);
        }

        return new HandoffTrackChoice(src.Id!, src.DefaultAudioStreamIndex, src.DefaultSubtitleStreamIndex ?? -1);
    }

    internal static bool IsHlsUrl(string url, string? subProtocol)
    {
        if (string.Equals(subProtocol, "hls", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var q = url.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? url : url[..q];
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// master.m3u8 -> its single variant (main.m3u8) -> EXT-X-MAP init segment (fMP4) -> first media
    /// segment, each read to the end, all with the player's UA. ffmpeg starts on the first segment
    /// request (playlists alone start nothing, B report §2b) and then runs on by itself, kept alive
    /// by <see cref="PingLoopAsync"/>.
    /// </summary>
    private async Task PrestartAsync(HandoffEntry entry, string psid, string masterUrl, string userAgent, CancellationToken ct)
    {
        var headers = new[] { new KeyValuePair<string, string>("User-Agent", userAgent) };
        var sw = Stopwatch.StartNew();

        async Task<HandoffHttpResponse> Get(string pathAndQuery, bool capture, TimeSpan timeout)
            => await _loopback.SendAsync(
                new HandoffHttpRequest(HttpMethod.Get, pathAndQuery, headers, null, entry.ClientIp, capture, timeout),
                ct).ConfigureAwait(false);

        // The master playlist starts nothing, so it is read before anything is committed.
        var master = await Get(masterUrl, true, PlaybackInfoTimeout).ConfigureAwait(false);
        var variants = master.Status == 200 ? HandoffHls.DistinctUris(Encoding.UTF8.GetString(master.Body)) : Array.Empty<string>();
        if (variants.Count == 0)
        {
            _logger.LogWarning("[Projectionist] handoff: pre-start master.m3u8 for {Feature} -> {Status}", entry.FeatureName, master.Status);
            Drop(entry, "pre-start failed");
            return;
        }

        if (variants.Count > 1)
        {
            // COMPAT-1: HDR/SDR (or AV1/HEVC/H.264 SDR, or ABR) entrances all map to the same output
            // files, so a player picking another variant than the one started would get the wrong
            // encode. Only single-variant sessions are pre-started.
            _logger.LogInformation(
                "[Projectionist] handoff: {Feature} offers {Count} HLS variants; not pre-starting (the player may pick any of them)",
                entry.FeatureName,
                variants.Count);
            return;
        }

        if (!TryAcquireHotSlot(entry))
        {
            _logger.LogInformation(
                "[Projectionist] handoff: {Max} pre-started transcodes already running; not pre-starting {Feature}",
                MaxConcurrentPrestarts,
                entry.FeatureName);
            return;
        }

        lock (entry.Sync)
        {
            if (entry.Dropped || entry.Consumed)
            {
                ReleaseHotSlot(entry);
                return;
            }

            entry.Hot = HandoffHotState.Starting;
            entry.HotPlaySessionId = psid;
            entry.HotUserAgent = userAgent;
        }

        _cacheable[psid] = new CacheableSession(entry.Token, entry.FeatureId);
        if (entry.Dropped)
        {
            // Dropped between the state change and the cache mark: Drop may have missed the mark.
            _cacheable.TryRemove(psid, out _);
            return;
        }

        var mainUrl = HandoffHls.Resolve(masterUrl, variants[0]);
        var main = await Get(mainUrl, true, PlaybackInfoTimeout).ConfigureAwait(false);
        var mainText = main.Status == 200 ? Encoding.UTF8.GetString(main.Body) : null;
        var init = mainText is null ? null : HandoffHls.MapUri(mainText);
        var first = mainText is null ? null : HandoffHls.FirstUri(mainText);
        if (first is null)
        {
            _logger.LogWarning("[Projectionist] handoff: pre-start main.m3u8 for {Feature} -> {Status}", entry.FeatureName, main.Status);
            Drop(entry, "pre-start failed", rekill: true);
            return;
        }

        var playlistsMs = sw.ElapsedMilliseconds;
        long bytes = 0;
        foreach (var seg in init is null ? new[] { first } : new[] { init, first })
        {
            var r = await Get(HandoffHls.Resolve(mainUrl, seg), false, SegmentTimeout).ConfigureAwait(false);
            if (r.Status != 200)
            {
                // A transport failure (status 0) may leave our request running on the server: re-kill.
                _logger.LogWarning("[Projectionist] handoff: pre-start segment for {Feature} -> {Status}", entry.FeatureName, r.Status);
                Drop(entry, "pre-start failed", rekill: true);
                return;
            }

            bytes += r.Bytes;
        }

        lock (entry.Sync)
        {
            if (entry.Consumed || entry.Dropped)
            {
                return;
            }

            entry.Hot = HandoffHotState.Started;
            entry.PrestartedAt = _time.GetUtcNow();
        }

        _logger.LogInformation(
            "[Projectionist] handoff: pre-started transcode {Psid} for {Feature} (playlists {PlaylistMs} ms, first segment(s) {Bytes} bytes after {Ms} ms)",
            psid,
            entry.FeatureName,
            playlistsMs,
            bytes,
            sw.ElapsedMilliseconds);

        await PingLoopAsync(entry, psid, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-arms the job's 60 s kill timer every <see cref="PingInterval"/> - but only while the
    /// device demonstrably is still in the preroll: it is dropped (and the job killed) once the
    /// device has not been seen for <see cref="ActivityTimeout"/>, has been paused for
    /// <see cref="PausedTimeout"/>, the mode is no longer Hot, or the job has been kept alive for
    /// <see cref="MaxPingedLifetime"/>. Ends at the handover (the player's own requests keep it).
    /// </summary>
    private async Task PingLoopAsync(HandoffEntry entry, string psid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(PingInterval, _time, ct).ConfigureAwait(false);
            string? stop = null;
            lock (entry.Sync)
            {
                if (entry.Consumed || entry.Dropped)
                {
                    return;
                }

                var now = _time.GetUtcNow();
                if (now - entry.LastSeen > ActivityTimeout)
                {
                    stop = "no playback activity from the device for 60 s";
                }
                else if (entry.PausedSince is { } paused && now - paused > PausedTimeout)
                {
                    stop = "the preroll has been paused for over 60 s";
                }
                else if (entry.PrestartedAt is { } started && now - started > MaxPingedLifetime)
                {
                    stop = "kept alive for the maximum time";
                }
            }

            if (stop is null && Mode != FeaturePreloadMode.Hot)
            {
                stop = "feature preload is no longer Hot";
            }

            if (stop is not null)
            {
                Drop(entry, stop);
                return;
            }

            try
            {
                _transcode.Ping(psid);
                _logger.LogDebug("[Projectionist] handoff: pinged prewarmed transcode {Psid}", psid);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Projectionist] handoff: ping of {Psid} failed", psid);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stoppingRegistration.Dispose();
        if (_sessionManager is not null)
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _sessionManager.SessionEnded -= OnSessionEnded;
        }

        _sweeper?.Dispose();
        List<HandoffEntry> all;
        lock (_gate)
        {
            all = _entries.Values.ToList();
        }

        foreach (var e in all)
        {
            Drop(e, "server shutting down");
        }
    }

    /// <summary>A session handed to a player, supervised for <see cref="AdoptionSupervision"/> (guarded by itself).</summary>
    private sealed class AdoptedSession
    {
        public AdoptedSession(string deviceId, string token, string userAgent, Guid featureId, DateTimeOffset at)
        {
            DeviceId = deviceId;
            Token = token;
            UserAgent = userAgent;
            FeatureId = featureId;
            At = at;
        }

        public string DeviceId { get; }

        public string Token { get; }

        public string UserAgent { get; }

        public Guid FeatureId { get; }

        public DateTimeOffset At { get; }

        public bool Logged { get; set; }

        /// <summary>The first request carrying the session, or the player's first Start/Progress report of it, was seen (or the supervision window lapsed).</summary>
        public bool Decided { get; set; }

        public bool HlsSeen { get; set; }
    }

    /// <summary>Whose (token) pre-started session for which feature may be served cacheable.</summary>
    private sealed record CacheableSession(string Token, Guid FeatureId);
}
