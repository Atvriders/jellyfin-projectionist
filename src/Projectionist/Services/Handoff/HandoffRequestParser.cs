using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>What kind of request the handoff middleware is looking at.</summary>
internal enum HandoffRequestKind
{
    None = 0,

    /// <summary>POST .../Items/{id}/PlaybackInfo.</summary>
    PlaybackInfo = 1,

    /// <summary>GET/HEAD .../Videos/{id}/stream*, master.m3u8, main.m3u8, hls1/..., hls/....</summary>
    VideoStream = 2,
}

/// <summary>
/// Pre-auth request parsing for the handoff middleware. The middleware runs outside
/// app.Map(BaseUrl) and before authentication, so everything here works on the raw
/// request: the path may carry a BaseUrl prefix, segment casing varies (/Items vs
/// /items, /Videos vs /videos), ids arrive in "N" or "D" format, and the device id /
/// token have to be dug out of the headers or query the same way Jellyfin's
/// AuthorizationContext does it (Jellyfin.Server.Implementations/Security/AuthorizationContext.cs).
/// </summary>
internal static class HandoffRequestParser
{
    private static readonly string[] TokenHeaders = { "X-Emby-Token", "X-MediaBrowser-Token" };

    /// <summary>
    /// Classifies a request path. Only the two shapes the handoff cares about are recognised;
    /// everything else is <see cref="HandoffRequestKind.None"/>. <paramref name="itemId"/> is the
    /// {id} segment. The method check is the caller's job (PlaybackInfo = POST, streams = GET/HEAD).
    /// </summary>
    public static HandoffRequestKind Classify(string? path, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(path) || path.Length < 10)
        {
            return HandoffRequestKind.None;
        }

        // UsePathTrim in the server strips a trailing slash later on; do the same here.
        var trimmed = path.AsSpan().TrimEnd('/');

        // .../Items/{id}/PlaybackInfo
        if (trimmed.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[..^"/PlaybackInfo".Length];
            var slash = rest.LastIndexOf('/');
            if (slash < 0)
            {
                return HandoffRequestKind.None;
            }

            var idPart = rest[(slash + 1)..];
            var before = rest[..slash];
            if (before.EndsWith("/Items", StringComparison.OrdinalIgnoreCase) && TryParseId(idPart, out itemId))
            {
                return HandoffRequestKind.PlaybackInfo;
            }

            return HandoffRequestKind.None;
        }

        // .../Videos/{id}/<tail>
        var videos = trimmed.LastIndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        while (videos >= 0)
        {
            var after = trimmed[(videos + "/Videos/".Length)..];
            var slash = after.IndexOf('/');
            if (slash > 0 && TryParseId(after[..slash], out itemId) && IsStreamTail(after[(slash + 1)..]))
            {
                return HandoffRequestKind.VideoStream;
            }

            if (videos == 0)
            {
                break;
            }

            videos = trimmed[..videos].LastIndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        }

        itemId = Guid.Empty;
        return HandoffRequestKind.None;
    }

    /// <summary>
    /// For a <see cref="HandoffRequestKind.VideoStream"/> path: true when it is one of the HLS
    /// shapes a pre-started session serves (master.m3u8, main.m3u8, hls1/..., legacy hls/...),
    /// false for stream / stream.{container} (static or progressive).
    /// </summary>
    public static bool IsHlsStreamPath(string? path)
        => StreamTail(path) is { } tail
           && (tail.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
               || tail.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase)
               || tail.StartsWith("hls1/", StringComparison.OrdinalIgnoreCase)
               || tail.StartsWith("hls/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The paths a pre-started session's playlists and segments are served under, which the
    /// handoff may mark cacheable: master.m3u8, main.m3u8 and hls1/... (not the legacy hls/).
    /// </summary>
    public static bool IsCacheableHlsPath(string? path)
        => StreamTail(path) is { } tail
           && (tail.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
               || tail.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase)
               || (tail.StartsWith("hls1/", StringComparison.OrdinalIgnoreCase) && tail.Length > 5));

    /// <summary>The part after .../Videos/{id}/ of a VideoStream path; null for any other path.</summary>
    private static string? StreamTail(string? path)
    {
        if (Classify(path, out var id) != HandoffRequestKind.VideoStream)
        {
            return null;
        }

        var trimmed = path!.AsSpan().TrimEnd('/');
        var at = trimmed.LastIndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var after = trimmed[(at + "/Videos/".Length)..];
            var slash = after.IndexOf('/');
            if (slash > 0 && TryParseId(after[..slash], out var candidate) && candidate == id && IsStreamTail(after[(slash + 1)..]))
            {
                return after[(slash + 1)..].ToString();
            }

            if (at == 0)
            {
                break;
            }

            at = trimmed[..at].LastIndexOf("/Videos/", StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static bool IsStreamTail(ReadOnlySpan<char> tail)
    {
        if (tail.Equals("stream", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // stream.{container}
        if (tail.StartsWith("stream.", StringComparison.OrdinalIgnoreCase) && tail.IndexOf('/') < 0)
        {
            return true;
        }

        // hls1/{playlistId}/{segmentId}.{container} and the legacy hls/{playlistId}/...
        return tail.StartsWith("hls1/", StringComparison.OrdinalIgnoreCase)
            || tail.StartsWith("hls/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accepts every format ASP.NET's Guid route binding accepts ("N", "D", "B", "P").</summary>
    public static bool TryParseId(ReadOnlySpan<char> value, out Guid id)
        => Guid.TryParse(value, out id) && id != Guid.Empty;

    /// <summary>
    /// Parses a "MediaBrowser k=\"v\", ..." (or legacy "Emby ...") header into its parts,
    /// URL-decoding values exactly like AuthorizationContext.GetParts. Null when the scheme is
    /// not one Jellyfin accepts.
    /// </summary>
    public static Dictionary<string, string>? ParseAuthorizationHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var span = header.AsSpan().Trim();
        var firstSpace = span.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var scheme = span[..firstSpace];
        if (!scheme.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("Emby", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        span = span[(firstSpace + 1)..];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var escaped = false;
        var start = 0;
        var key = string.Empty;
        int i;
        for (i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '"' || c == ',')
            {
                escaped = (!escaped) == (c == '"');
                if (c == ',' && !escaped)
                {
                    if (start < i)
                    {
                        result[key] = WebUtility.UrlDecode(span[start..i].Trim().Trim('"').ToString());
                        key = string.Empty;
                    }

                    start = i + 1;
                }
            }
            else if (!escaped && c == '=')
            {
                key = span[start..i].Trim().ToString();
                start = i + 1;
            }
        }

        if (start < i)
        {
            result[key] = WebUtility.UrlDecode(span[start..i].Trim().Trim('"').ToString());
        }

        return result;
    }

    private static Dictionary<string, string>? GetAuthParts(IHeaderDictionary headers)
    {
        var auth = headers.Authorization;
        if (StringValues.IsNullOrEmpty(auth))
        {
            auth = headers["X-Emby-Authorization"];
        }

        return StringValues.IsNullOrEmpty(auth) ? null : ParseAuthorizationHeader(auth[0]);
    }

    /// <summary>
    /// Device id as the client states it: the auth header's DeviceId, else the deviceId query
    /// parameter (stream/HLS URLs carry it there). Null when the request names none, in which case
    /// Jellyfin would fall back to the token's device; callers then match on the token alone.
    /// </summary>
    public static string? GetDeviceId(IHeaderDictionary headers, IQueryCollection query)
    {
        var parts = GetAuthParts(headers);
        if (parts is not null && parts.TryGetValue("DeviceId", out var fromHeader) && !string.IsNullOrWhiteSpace(fromHeader))
        {
            return fromHeader;
        }

        // IQueryCollection is case-insensitive: covers deviceId and DeviceId.
        var q = query["deviceId"];
        return StringValues.IsNullOrEmpty(q) || string.IsNullOrWhiteSpace(q[0]) ? null : q[0];
    }

    /// <summary>
    /// Access token in AuthorizationContext's precedence order: header Token=, X-Emby-Token,
    /// X-MediaBrowser-Token, ApiKey query, api_key query.
    /// </summary>
    public static string? GetToken(IHeaderDictionary headers, IQueryCollection query)
    {
        var parts = GetAuthParts(headers);
        if (parts is not null && parts.TryGetValue("Token", out var t) && !string.IsNullOrEmpty(t))
        {
            return t;
        }

        foreach (var name in TokenHeaders)
        {
            var h = headers[name];
            if (!StringValues.IsNullOrEmpty(h) && !string.IsNullOrEmpty(h[0]))
            {
                return h[0];
            }
        }

        var q = query["ApiKey"];
        if (!StringValues.IsNullOrEmpty(q) && !string.IsNullOrEmpty(q[0]))
        {
            return q[0];
        }

        q = query["api_key"];
        return StringValues.IsNullOrEmpty(q) || string.IsNullOrEmpty(q[0]) ? null : q[0];
    }

    /// <summary>Value of one query parameter (case-insensitive name) from a raw URL; null when absent.</summary>
    public static string? GetQueryValue(string? url, string name)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        foreach (var part in url.AsSpan(q + 1).ToString().Split('&'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? part : part[..eq];
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return null;
    }
}
