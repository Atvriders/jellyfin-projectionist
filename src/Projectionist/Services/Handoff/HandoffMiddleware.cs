using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>
/// Request side of the feature handoff. Installed OUTERMOST by FeatureHandoffStartupFilter:
/// before forwarded-headers, auth, response compression and app.Map(BaseUrl). Anything that is
/// not a PlaybackInfo POST (or, while something is pending, a video stream/HLS request) goes
/// straight to the next middleware without touching the body.
///
///   probe     - a loopback probe nonce (plain-http /System/Ping from this host): a 200 answer gets
///               HMAC(secret, nonce | this connection's local ip:port) in a response header.
///   loopback  - a request of ours carrying the per-process secret, on a connection from this
///               host: header stripped, RemoteIpAddress set to the real client's, then passed
///               through untouched.
///   (a)       - PlaybackInfo for a pending intro: body buffered, passed through; afterwards the
///               registry starts the feature's prewarm in the background.
///   stream    - first stream/HLS request of a pending intro: remember the player's User-Agent.
///   (c)       - PlaybackInfo for the pending feature (Hot, adoptable): response captured
///               identity-encoded and possibly rewritten onto the prewarm's PlaySessionId.
///   (d)       - PlaybackInfo for anything else from that device: the pending play is abandoned.
///   cache     - GET of a pre-started (or handed-over) session's master/main/hls1 URL with that
///               session's token: a 200 answer is marked "private, max-age=300" so a browser that
///               prefetched it during the preroll serves the player from its HTTP cache.
///   adopted   - requests carrying a handed-over PlaySessionId are watched briefly (a player that
///               direct-streams it instead gets the unused transcode killed).
/// </summary>
internal sealed class HandoffMiddleware
{
    private const int MaxBufferedBody = 1024 * 1024;

    /// <summary>Cache-Control for a pre-started session's playlists and segments (H).</summary>
    internal const string CacheControl = "private, max-age=300";

    private readonly FeatureHandoffRegistry _registry;
    private readonly HandoffLoopbackClient _loopback;
    private readonly ILogger _logger;

    public HandoffMiddleware(FeatureHandoffRegistry registry, HandoffLoopbackClient loopback, ILogger logger)
    {
        _registry = registry;
        _loopback = loopback;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var request = context.Request;

        // Loopback probe (SEC-2, SEC-RR-1): answered only for a plain-http /System/Ping from this
        // host, with a proof bound to the local endpoint THIS connection arrived on, and only on a
        // 200; the header never reaches the inner pipeline.
        if (request.Headers.TryGetValue(HandoffLoopbackClient.ProbeHeaderName, out var probe))
        {
            request.Headers.Remove(HandoffLoopbackClient.ProbeHeaderName);
            var connection = context.Connection;
            if (!request.IsHttps
                && (request.Path.Value?.EndsWith("/System/Ping", StringComparison.OrdinalIgnoreCase) ?? false)
                && _loopback.IsOwnAddress(connection.RemoteIpAddress, connection.LocalIpAddress))
            {
                var proof = _loopback.ComputeProof(probe.ToString(), connection.LocalIpAddress, connection.LocalPort);
                if (proof is not null)
                {
                    var response = context.Response;
                    response.OnStarting(() =>
                    {
                        if (response.StatusCode == StatusCodes.Status200OK)
                        {
                            response.Headers[HandoffLoopbackClient.ProofHeaderName] = proof;
                        }

                        return Task.CompletedTask;
                    });
                }
            }
        }

        // Loopback marker: only ever honoured with the right secret, over plain http (the
        // loopback client never uses https), on a connection from this host itself, and never
        // left in place.
        if (request.Headers.TryGetValue(HandoffLoopbackClient.HeaderName, out var marker))
        {
            request.Headers.Remove(HandoffLoopbackClient.HeaderName);
            if (_loopback.TryValidateHeader(marker.ToString(), out var clientIp))
            {
                if (!request.IsHttps && _loopback.IsOwnAddress(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress))
                {
                    if (clientIp is not null)
                    {
                        context.Connection.RemoteIpAddress = clientIp;
                    }

                    await next().ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning(
                    "[Projectionist] handoff: ignoring a valid loopback marker from {Ip}, which is not this host over plain http",
                    context.Connection.RemoteIpAddress);
            }
            else
            {
                _logger.LogDebug("[Projectionist] handoff: ignoring a loopback marker with a wrong secret from {Ip}", context.Connection.RemoteIpAddress);
            }
        }

        var isPost = HttpMethods.IsPost(request.Method);
        if (!isPost && !(_registry.HasEntries || _registry.HasAdopted))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var kind = HandoffRequestParser.Classify(request.Path.Value, out var itemId);
        if (kind == HandoffRequestKind.PlaybackInfo && isPost)
        {
            await HandlePlaybackInfoAsync(context, itemId, next).ConfigureAwait(false);
            return;
        }

        if (kind == HandoffRequestKind.VideoStream && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            ObserveStream(context, itemId);
        }

        await next().ConfigureAwait(false);
    }

    private void ObserveStream(HttpContext context, Guid itemId)
    {
        try
        {
            var request = context.Request;
            var ua = request.Headers.UserAgent.ToString();
            var path = request.Path.Value;
            string? token = null;
            var psid = request.Query["PlaySessionId"].ToString();
            if (!string.IsNullOrEmpty(psid))
            {
                token = HandoffRequestParser.GetToken(request.Headers, request.Query);

                // H: the pre-started session's playlists/segments may be cached by the browser, so
                // the player's requests (the same URLs the browser prefetched) never leave it.
                if (HttpMethods.IsGet(request.Method)
                    && HandoffRequestParser.IsCacheableHlsPath(path)
                    && _registry.IsCacheableSession(psid, token, itemId))
                {
                    var response = context.Response;
                    response.OnStarting(() =>
                    {
                        if (response.StatusCode == StatusCodes.Status200OK)
                        {
                            response.Headers.CacheControl = CacheControl;
                            response.Headers.Remove(HeaderNames.Pragma);
                            response.Headers.Remove(HeaderNames.Expires);
                        }

                        return Task.CompletedTask;
                    });
                }

                if (_registry.HasAdopted)
                {
                    _registry.ObserveAdoptedStream(psid, token, ua, HandoffRequestParser.IsHlsStreamPath(path));
                }
            }

            if (!_registry.HasEntries)
            {
                return;
            }

            var entry = _registry.Find(
                HandoffRequestParser.GetDeviceId(request.Headers, request.Query),
                token ?? HandoffRequestParser.GetToken(request.Headers, request.Query));
            if (entry is not null && entry.HasIntro(itemId))
            {
                _registry.OnIntroStreamRequest(entry, ua);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: stream observation failed");
        }
    }

    private async Task HandlePlaybackInfoAsync(HttpContext context, Guid itemId, Func<Task> next)
    {
        HandoffEntry? entry = null;
        FeaturePreloadMode mode = FeaturePreloadMode.Off;
        try
        {
            mode = _registry.Mode;
            if (mode != FeaturePreloadMode.Off)
            {
                // Pre-auth: O(1) and lock-free for requests that match no entry (the token index).
                // Nothing is recorded here; see RecordObservation.
                var request = context.Request;
                entry = _registry.Find(
                    HandoffRequestParser.GetDeviceId(request.Headers, request.Query),
                    HandoffRequestParser.GetToken(request.Headers, request.Query));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: PlaybackInfo inspection failed");
            entry = null;
        }

        if (mode == FeaturePreloadMode.Off)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (entry is null)
        {
            await next().ConfigureAwait(false);
        }
        else if (entry.HasIntro(itemId))
        {
            await HandleIntroPlaybackInfoAsync(context, entry, itemId, next).ConfigureAwait(false);
        }
        else if (itemId == entry.FeatureId)
        {
            await HandleFeaturePlaybackInfoAsync(context, entry, mode, next).ConfigureAwait(false);
        }
        else
        {
            // (d) The device moved on to something else.
            _registry.Drop(entry, "device requested PlaybackInfo for another item");
            await next().ConfigureAwait(false);
        }

        // The feature's own PlaybackInfo after its intros is the normal call, not a sign the
        // client resolves the feature before /Intros; recording it would make replaying the
        // same feature within the window look feature-first and skip every prewarm.
        if (entry is null || itemId != entry.FeatureId || entry.FeatureResolvedFirst)
        {
            RecordObservation(context, itemId);
        }
    }

    /// <summary>
    /// Feature-first rule input, committed only AFTER the inner pipeline (auth included) ran: a
    /// 200 answer to an authenticated request, keyed by the authenticated principal's user id AND
    /// device id claims - never by a raw pre-auth header, so a 401 flood records nothing (SEC-1),
    /// and never by the device id alone, which the client chooses (SEC-RR-2).
    /// </summary>
    private void RecordObservation(HttpContext context, Guid itemId)
    {
        try
        {
            if (context.Response.StatusCode != StatusCodes.Status200OK)
            {
                return;
            }

            var user = context.User;
            var deviceId = user?.FindFirst(FeatureHandoffRegistry.ClaimDeviceId)?.Value;
            if (!string.IsNullOrEmpty(deviceId)
                && Guid.TryParse(user?.FindFirst(FeatureHandoffRegistry.ClaimUserId)?.Value, out var userId)
                && userId != Guid.Empty)
            {
                _registry.ObservePlaybackInfo(userId, deviceId, itemId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: PlaybackInfo observation failed");
        }
    }

    /// <summary>(a) Buffer the intro's body, let it through, then hand a copy to the registry.</summary>
    private async Task HandleIntroPlaybackInfoAsync(HttpContext context, HandoffEntry entry, Guid introId, Func<Task> next)
    {
        HandoffIntroRequest? snapshot = null;
        if (!entry.FeatureResolvedFirst && entry.PrewarmClaimed == 0)
        {
            snapshot = await SnapshotAsync(context.Request).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);

        if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            // The next intro of the chain is starting: the device is still in the preroll, and a
            // late report about the previous intro is stale from now on (rr-C4).
            _registry.OnIntroActivity(entry, introId);
        }

        if (snapshot is not null && context.Response.StatusCode == StatusCodes.Status200OK)
        {
            try
            {
                _registry.OnIntroPlaybackInfo(entry, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] handoff: could not start the prewarm");
            }
        }
    }

    private async Task<HandoffIntroRequest?> SnapshotAsync(HttpRequest request)
    {
        try
        {
            if (request.ContentLength is > MaxBufferedBody)
            {
                return null;
            }

            request.EnableBuffering();
            byte[] body;
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > MaxBufferedBody)
                    {
                        request.Body.Position = 0;
                        return null;
                    }
                }

                body = ms.ToArray();
            }

            request.Body.Position = 0;

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var name in FeatureHandoffRegistry.ForwardedHeaderNames)
            {
                if (request.Headers.TryGetValue(name, out var v) && v.Count > 0 && !string.IsNullOrEmpty(v[0]))
                {
                    headers.Add(new KeyValuePair<string, string>(name, v[0]!));
                }
            }

            return new HandoffIntroRequest(body, request.QueryString.Value ?? string.Empty, headers);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: could not buffer the intro PlaybackInfo body");
            try
            {
                if (request.Body.CanSeek)
                {
                    request.Body.Position = 0;
                }
            }
            catch
            {
            }

            return null;
        }
    }

    /// <summary>(c) The feature's own PlaybackInfo from the device that played the intros.</summary>
    private async Task HandleFeaturePlaybackInfoAsync(HttpContext context, HandoffEntry entry, FeaturePreloadMode mode, Func<Task> next)
    {
        if (mode != FeaturePreloadMode.Hot || !_registry.IsAdoptable(entry))
        {
            _registry.Drop(entry, "feature requested; no prewarmed transcode to hand over");
            await next().ConfigureAwait(false);
            return;
        }

        // The inner pipeline must answer identity-encoded so the JSON can be read and rewritten.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        var response = context.Response;
        var originalBody = response.Body;
        using var captured = new MemoryStream();
        response.Body = captured;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            response.Body = originalBody;
            _registry.Drop(entry, "feature PlaybackInfo threw");
            throw;
        }
        finally
        {
            response.Body = originalBody;
        }

        var bytes = captured.ToArray();
        byte[] output;
        try
        {
            output = _registry.CompleteFeaturePlaybackInfo(
                entry,
                response.StatusCode,
                response.ContentType,
                response.Headers.ContentEncoding.ToString(),
                bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] handoff: feature PlaybackInfo handling failed");
            output = bytes;
        }

        if (!response.HasStarted)
        {
            response.ContentLength = output.Length;
        }

        await originalBody.WriteAsync(output, context.RequestAborted).ConfigureAwait(false);
    }
}
