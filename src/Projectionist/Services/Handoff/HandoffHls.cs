using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>
/// Just enough m3u8 reading to walk Jellyfin's own playlists: master.m3u8 lists "main.m3u8?..."
/// variants, main.m3u8 lists "hls1/main/{n}.{ext}?..." segments (plus an EXT-X-MAP init segment
/// "-1.mp4" for fMP4), all relative. URLs are resolved textually so the query strings stay
/// byte-for-byte what the server wrote (the DeviceId and PlaySessionId in them key the job).
/// </summary>
internal static class HandoffHls
{
    /// <summary>First URI line (not a tag or comment) of a playlist.</summary>
    public static string? FirstUri(string playlist)
    {
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && line[0] != '#')
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Every distinct URI line of a playlist, in order. A master.m3u8 lists one per variant; the
    /// server's Level-5.0 compatibility entrance repeats the same URI, so it collapses here.
    /// </summary>
    public static IReadOnlyList<string> DistinctUris(string playlist)
    {
        var uris = new List<string>();
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && line[0] != '#' && !uris.Contains(line, StringComparer.Ordinal))
            {
                uris.Add(line);
            }
        }

        return uris;
    }

    /// <summary>URI attribute of the first #EXT-X-MAP tag, if any.</summary>
    public static string? MapUri(string playlist)
    {
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var at = line.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            var start = at + 5;
            var end = line.IndexOf('"', start);
            return end > start ? line[start..end] : null;
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="reference"/> against the server-relative
    /// <paramref name="baseUrl"/>; the result is server-relative too (no scheme/host).
    /// </summary>
    public static string Resolve(string baseUrl, string reference)
    {
        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(reference);
            return u.PathAndQuery;
        }

        if (reference.StartsWith('/'))
        {
            return reference;
        }

        var q = baseUrl.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? baseUrl : baseUrl[..q];
        var slash = path.LastIndexOf('/');
        return path[..(slash + 1)] + reference;
    }
}
