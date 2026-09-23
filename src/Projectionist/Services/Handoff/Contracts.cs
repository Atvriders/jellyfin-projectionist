using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

// LOCKED CONTRACT for the "feature handoff" work: shared by the storage warmer,
// the server-side transcode handoff and the web hook. Signatures and wire
// formats here must not change without updating every consumer.
//
// What the preload modes mean:
//   Off  - nothing.
//   Warm - wake the feature's storage while prerolls run (IFeatureWarmer), and
//          let the web hook pre-buffer a direct-play feature in the browser
//          (IFeatureHandoffRegistry resolves the feature's playback decision).
//   Hot  - Warm, plus: start the feature's transcode during the preroll and hand
//          that exact session (its exact TranscodingUrl) to the player when it asks
//          for the feature; browsers also pre-download its first HLS segments.

/// <summary>Resolves the preload mode actually in effect.</summary>
public static class PreloadModes
{
    /// <summary>
    /// The configured <see cref="FeaturePreloadMode"/>, honouring the legacy
    /// <see cref="PluginConfiguration.EnableFeaturePreload"/> flag (true + Off = Warm).
    /// </summary>
    public static FeaturePreloadMode Effective(PluginConfiguration? cfg)
    {
        if (cfg is null)
        {
            return FeaturePreloadMode.Off;
        }

        if (cfg.FeaturePreloadMode == FeaturePreloadMode.Off && cfg.EnableFeaturePreload)
        {
            return FeaturePreloadMode.Warm;
        }

        return cfg.FeaturePreloadMode;
    }
}

/// <summary>Pre-reads a feature's media so its storage is awake before the player needs it.</summary>
public interface IFeatureWarmer
{
    /// <summary>
    /// Fire-and-forget. Must return immediately (no I/O on the caller's thread) and must never throw.
    /// </summary>
    void WarmInBackground(BaseItem feature);
}

/// <summary>Tracks "this user+device will play feature F after intros I1..In".</summary>
public interface IFeatureHandoffRegistry
{
    /// <summary>
    /// Called by PrerollIntroProvider.GetIntros only when it is about to return at least one
    /// preroll and the effective mode is Warm or Hot. <paramref name="httpContext"/> is the
    /// /Intros request (null outside a request). Must not block, do I/O, or throw.
    /// </summary>
    void OnIntrosResolved(HttpContext? httpContext, BaseItem feature, User user, IReadOnlyList<Guid> introItemIds);

    /// <summary>
    /// Backs GET /Plugins/Projectionist/Upcoming. Null when nothing is pending for this
    /// user+device, or when <paramref name="currentItemId"/> is given and is not one of the
    /// pending intros.
    /// </summary>
    UpcomingFeatureInfo? GetUpcoming(Guid userId, string deviceId, Guid? currentItemId);
}

/// <summary>Wire format of GET /Plugins/Projectionist/Upcoming (PascalCase, pinned).</summary>
public sealed class UpcomingFeatureInfo
{
    /// <summary>Feature item id, "N" format (32 lowercase hex).</summary>
    [JsonPropertyName("FeatureId")]
    public string FeatureId { get; set; } = string.Empty;

    /// <summary>Pending intro item ids, "N" format.</summary>
    [JsonPropertyName("IntroIds")]
    public List<string> IntroIds { get; set; } = new();

    /// <summary>"DirectPlay", "Transcode", or "Unknown" (decision not resolved yet; retry later).</summary>
    [JsonPropertyName("PlayMethod")]
    public string PlayMethod { get; set; } = "Unknown";

    /// <summary>Non-null exactly when <see cref="PlayMethod"/> is "DirectPlay".</summary>
    [JsonPropertyName("DirectPlay")]
    public DirectPlayInfo? DirectPlay { get; set; }

    /// <summary>Hint for the browser warmer: stop once this many seconds are buffered.</summary>
    [JsonPropertyName("PrefetchSeconds")]
    public int PrefetchSeconds { get; set; } = 8;

    /// <summary>
    /// Hot mode only: the pre-started HLS session the player will be handed. Null unless
    /// <see cref="PlayMethod"/> is "Transcode" and a pre-start is running for this user+device.
    /// </summary>
    [JsonPropertyName("Transcode")]
    public TranscodePrefetchInfo? Transcode { get; set; }
}

/// <summary>
/// The pre-started session, so a browser can pre-download the first segments into its HTTP
/// cache during the preroll. The server hands the player this exact TranscodingUrl, and marks
/// that session's playlist and segment responses cacheable, so the player's requests (same
/// URLs) are served from the browser cache.
/// </summary>
public sealed class TranscodePrefetchInfo
{
    /// <summary>
    /// Server-relative TranscodingUrl (e.g. "/videos/{id}/master.m3u8?..."), byte-identical to the
    /// TranscodingUrl the player will receive in its feature PlaybackInfo response. The player turns it
    /// into an absolute URL with ApiClient.getUrl(), and hls.js resolves the playlist URIs against it.
    /// </summary>
    [JsonPropertyName("Url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>True once the server's pre-start has produced the first segment (safe to fetch).</summary>
    [JsonPropertyName("Ready")]
    public bool Ready { get; set; }
}

/// <summary>
/// What the web client needs to rebuild the exact Static direct-play URL jellyfin-web will use:
/// Videos/{FeatureId}/stream.{container lowercased}?Static=true&amp;mediaSourceId=..&amp;deviceId=..&amp;ApiKey=..&amp;Tag=..
/// Values are copied verbatim from a PlaybackInfo response computed with the client's own DeviceProfile.
/// </summary>
public sealed class DirectPlayInfo
{
    [JsonPropertyName("Container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("MediaSourceId")]
    public string MediaSourceId { get; set; } = string.Empty;

    [JsonPropertyName("ETag")]
    public string? ETag { get; set; }
}
