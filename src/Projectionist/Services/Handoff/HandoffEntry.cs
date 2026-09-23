using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>Where the pre-started transcode of an entry is.</summary>
internal enum HandoffHotState
{
    /// <summary>Nothing started (Warm mode, direct play, or not decided yet).</summary>
    None = 0,

    /// <summary>The loopback master/main/segment requests are being made with the player's UA.</summary>
    Starting = 1,

    /// <summary>ffmpeg is running for the prewarm's PlaySessionId; being pinged.</summary>
    Started = 2,

    /// <summary>The pre-start failed; nothing may be handed to the player.</summary>
    Failed = 3,
}

/// <summary>
/// The request the prewarm is derived from: the client's own PlaybackInfo for its first intro.
/// </summary>
internal sealed record HandoffIntroRequest(
    byte[] Body,
    string QueryString,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// One "this user+device will play feature F after intros I1..In" record. Mutable state is
/// guarded by <see cref="Sync"/>; the registry's dictionary is guarded by the registry.
/// </summary>
internal sealed class HandoffEntry
{
    private readonly List<Guid> _introIds;
    private readonly TaskCompletionSource<string> _playerUaSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _dropped;

    public HandoffEntry(
        Guid userId,
        string deviceId,
        Guid featureId,
        string featureName,
        IEnumerable<Guid> introIds,
        string token,
        IPAddress? clientIp,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        bool featureResolvedFirst)
    {
        UserId = userId;
        DeviceId = deviceId;
        FeatureId = featureId;
        FeatureName = featureName;
        _introIds = introIds.Distinct().ToList();
        Token = token;
        ClientIp = clientIp;
        CreatedAt = createdAt;
        LastSeen = createdAt;
        ExpiresAt = expiresAt;
        FeatureResolvedFirst = featureResolvedFirst;
    }

    public object Sync { get; } = new();

    public Guid UserId { get; }

    public string DeviceId { get; }

    public Guid FeatureId { get; }

    public string FeatureName { get; }

    public string Token { get; }

    /// <summary>Effective client address of the /Intros request (after forwarded-headers).</summary>
    public IPAddress? ClientIp { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Last evidence that the device is still in the preroll: its /Intros, an intro PlaybackInfo or
    /// stream request, or a playback start/progress report for one of the intros.
    /// </summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Since when the intro has been reported paused (null = playing / unknown).</summary>
    public DateTimeOffset? PausedSince { get; set; }

    /// <summary>The intro the device last reported as started (null = none reported yet).</summary>
    public Guid? CurrentIntro { get; set; }

    /// <summary>
    /// An intro was reported stopped: the next intro's or the feature's PlaybackInfo is due before
    /// this time, otherwise the play was abandoned (null = no deadline armed).
    /// </summary>
    public DateTimeOffset? NextRequestDeadline { get; set; }

    /// <summary>The armed <see cref="NextRequestDeadline"/> follows a stop before the intro's end (a user stop or a "next" press).</summary>
    public bool StoppedMidway { get; set; }

    /// <summary>When the pre-started transcode began being kept alive (bounds the pinged lifetime).</summary>
    public DateTimeOffset? PrestartedAt { get; set; }

    /// <summary>1 while this entry holds one of the registry's concurrent pre-start slots.</summary>
    public int HotSlot;

    /// <summary>
    /// The device asked for the feature's PlaybackInfo shortly BEFORE /Intros (Kodi resolves the
    /// feature first and plays it from that response), so nothing is pre-started for it.
    /// </summary>
    public bool FeatureResolvedFirst { get; }

    /// <summary>Cancelled when the entry is dropped; stops the prewarm and the ping loop.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public int PrewarmClaimed;

    public HandoffPlaybackInfo? Prewarm { get; set; }

    /// <summary>The prewarm PlaybackInfo response exactly as the server sent it (for the verbatim URL handover).</summary>
    public byte[]? PrewarmBody { get; set; }

    public bool PrewarmHadDeviceProfile { get; set; }

    public string? PlayerUserAgent { get; private set; }

    public HandoffHotState Hot { get; set; }

    public string? HotPlaySessionId { get; set; }

    public string? HotUserAgent { get; set; }

    public bool Consumed { get; set; }

    public bool Dropped
    {
        get => Volatile.Read(ref _dropped);
        set => Volatile.Write(ref _dropped, value);
    }

    public IReadOnlyList<Guid> IntroIds
    {
        get
        {
            lock (Sync)
            {
                return _introIds.ToArray();
            }
        }
    }

    public bool HasIntro(Guid id)
    {
        lock (Sync)
        {
            return _introIds.Contains(id);
        }
    }

    public void AddIntros(IEnumerable<Guid> ids)
    {
        lock (Sync)
        {
            foreach (var id in ids)
            {
                if (!_introIds.Contains(id))
                {
                    _introIds.Add(id);
                }
            }
        }
    }

    /// <summary>Evidence the device is still in the preroll (see <see cref="LastSeen"/>).</summary>
    public void Touch(DateTimeOffset now)
    {
        lock (Sync)
        {
            if (now > LastSeen)
            {
                LastSeen = now;
            }
        }
    }

    /// <summary>First stream/HLS request of an intro from this device: the UA its player uses.</summary>
    public bool TrySetPlayerUserAgent(string userAgent)
    {
        lock (Sync)
        {
            if (PlayerUserAgent is not null)
            {
                return false;
            }

            PlayerUserAgent = userAgent;
        }

        _playerUaSeen.TrySetResult(userAgent);
        return true;
    }

    /// <summary>The player's UA, waiting up to <paramref name="timeout"/> for its first intro stream request.</summary>
    public async Task<string?> WaitForPlayerUserAgentAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        var seen = PlayerUserAgent;
        if (seen is not null)
        {
            return seen;
        }

        var delay = Task.Delay(timeout, time, cancellationToken);
        var done = await Task.WhenAny(_playerUaSeen.Task, delay).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return done == _playerUaSeen.Task ? _playerUaSeen.Task.Result : null;
    }
}
