using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>The parts of one PlaybackInfo media source the handoff looks at.</summary>
internal sealed record HandoffMediaSource(
    string? Id,
    bool SupportsDirectPlay,
    string? Container,
    string? ETag,
    string? TranscodingUrl,
    string? TranscodingSubProtocol,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null);

/// <summary>The parts of a PlaybackInfoResponse the handoff looks at.</summary>
internal sealed record HandoffPlaybackInfo(
    string? PlaySessionId,
    IReadOnlyList<HandoffMediaSource> MediaSources,
    string? ErrorCode)
{
    public HandoffMediaSource? First => MediaSources.Count > 0 ? MediaSources[0] : null;
}

/// <summary>
/// PlaybackInfo body/response handling for the handoff: deriving the feature's prewarm request
/// from the intro's, reading responses, comparing transcode URLs modulo PlaySessionId, and
/// rewriting a fresh response onto the prewarm's session.
/// </summary>
internal static class HandoffJson
{
    /// <summary>
    /// Body members that describe the intro rather than the feature (a track index or media source
    /// of the intro means nothing for the feature) or that must be forced for a prewarm.
    /// PlaybackInfoDto binding is case-insensitive, so these are removed case-insensitively.
    /// </summary>
    private static readonly string[] StrippedMembers =
    {
        "AudioStreamIndex",
        "SubtitleStreamIndex",
        "SecondarySubtitleStreamIndex",
        "MediaSourceId",
        "LiveStreamId",
        "StartTimeTicks",
        "AutoOpenLiveStream",
    };

    private const string PlaySessionIdParam = "PlaySessionId";
    private const string TranscodeReasonsParam = "TranscodeReasons";
    private const string SubtitleStreamIndexParam = "SubtitleStreamIndex";
    private const string SubtitleMethodParam = "SubtitleMethod";
    private const string SubtitleCodecParam = "SubtitleCodec";

    /// <summary>
    /// Builds the feature's prewarm PlaybackInfo body from the intro's: everything the client sent
    /// is kept (DeviceProfile, MaxStreamingBitrate, AlwaysBurnInSubtitleWhenTranscoding,
    /// Enable*/Allow* flags, UserId, ...) except the intro-specific members, then
    /// StartTimeTicks=0 and AutoOpenLiveStream=false (a prewarm must never open a live stream;
    /// for library files the flag is a no-op anyway). A missing or non-object body becomes "{}".
    /// </summary>
    public static byte[] DerivePrewarmBody(ReadOnlySpan<byte> introBody, out bool hasDeviceProfile)
    {
        hasDeviceProfile = false;
        JsonObject obj;
        try
        {
            obj = introBody.IsEmpty
                ? new JsonObject()
                : JsonNode.Parse(introBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }

        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (StrippedMembers.Any(s => s.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                obj.Remove(key);
            }
            else if (key.Equals("DeviceProfile", StringComparison.OrdinalIgnoreCase)
                && obj[key] is JsonObject)
            {
                hasDeviceProfile = true;
            }
        }

        obj["StartTimeTicks"] = 0;
        obj["AutoOpenLiveStream"] = false;
        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }

    /// <summary>
    /// The intro request's query string minus the same intro-specific (obsolete but still bound)
    /// parameters. Keeps ApiKey/api_key, userId, maxStreamingBitrate etc. Returns "" or "?...".
    /// </summary>
    public static string DerivePrewarmQuery(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString) || queryString == "?")
        {
            return string.Empty;
        }

        var q = queryString[0] == '?' ? queryString[1..] : queryString;
        var kept = q.Split('&').Where(part =>
        {
            if (part.Length == 0)
            {
                return false;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            return !StrippedMembers.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        return kept.Count == 0 ? string.Empty : "?" + string.Join('&', kept);
    }

    /// <summary>Reads a PlaybackInfoResponse (property names matched case-insensitively). Null if it is not one.</summary>
    public static HandoffPlaybackInfo? ParsePlaybackInfo(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var sources = new List<HandoffMediaSource>();
            if (TryGet(root, "MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ms.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    sources.Add(new HandoffMediaSource(
                        GetString(s, "Id"),
                        TryGet(s, "SupportsDirectPlay", out var dp) && dp.ValueKind == JsonValueKind.True,
                        GetString(s, "Container"),
                        GetString(s, "ETag"),
                        GetString(s, "TranscodingUrl"),
                        GetString(s, "TranscodingSubProtocol"),
                        GetInt(s, "DefaultAudioStreamIndex"),
                        GetInt(s, "DefaultSubtitleStreamIndex")));
                }
            }

            return new HandoffPlaybackInfo(GetString(root, "PlaySessionId"), sources, GetString(root, "ErrorCode"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement obj, string name)
        => TryGet(obj, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>
    /// rr-C1: the prewarm body (see <see cref="DerivePrewarmBody"/>) shaped like jellyfin-web's
    /// details-page Play, which always sends the chosen source and the audio/subtitle dropdowns'
    /// values - the source's DefaultAudioStreamIndex and DefaultSubtitleStreamIndex, or -1 (Off)
    /// (itemDetails getPlayOptions, renderAudioSelections/renderSubtitleSelections). The server
    /// applies the indexes only together with MediaSourceId (MediaInfoHelper), and with an explicit
    /// audio index it checks that track's codec for direct play, so this shape can transcode where
    /// the index-less one (card/home-screen Play) direct-plays.
    /// </summary>
    public static byte[] DeriveExplicitTracksBody(ReadOnlySpan<byte> prewarmBody, string mediaSourceId, int? audioStreamIndex, int? subtitleStreamIndex)
    {
        JsonObject obj;
        try
        {
            obj = prewarmBody.IsEmpty
                ? new JsonObject()
                : JsonNode.Parse(prewarmBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }

        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            if (key.Equals("MediaSourceId", StringComparison.OrdinalIgnoreCase)
                || key.Equals("AudioStreamIndex", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SubtitleStreamIndex", StringComparison.OrdinalIgnoreCase))
            {
                obj.Remove(key);
            }
        }

        obj["MediaSourceId"] = mediaSourceId;
        if (audioStreamIndex.HasValue)
        {
            obj["AudioStreamIndex"] = audioStreamIndex.Value;
        }

        obj["SubtitleStreamIndex"] = subtitleStreamIndex ?? -1;
        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }

    /// <summary>
    /// The URL with every PlaySessionId query parameter removed and everything else left exactly
    /// as it was (order, empty parts such as the "?&amp;" Jellyfin emits, encoding).
    /// </summary>
    public static string RemovePlaySessionId(string url) => RemoveParams(url, PlaySessionIdParam);

    private static string RemoveParams(string url, params string[] names)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return url;
        }

        var parts = url[(q + 1)..].Split('&');
        var kept = parts.Where(p =>
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? p : p[..eq];
            return !names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
        });
        return url[..(q + 1)] + string.Join('&', kept);
    }

    /// <summary>
    /// True when two TranscodingUrls are byte-identical apart from PlaySessionId and
    /// TranscodeReasons, i.e. were computed from the same item, source, profile, bitrate, tracks
    /// and token. Only then may the player be handed the prewarm's session: the server does not
    /// check the encode parameters of a segment request against the running job (B report §2b),
    /// so a looser match would silently play the wrong encode.
    ///
    /// TranscodeReasons is excluded because it does not influence the encode and legitimately
    /// differs between an explicit and an implicit default audio track (jellyfin-web's
    /// autoSetNextTracks sends AudioStreamIndex; the prewarm cannot know it would): measured on
    /// 10.11.11 as "VideoCodecNotSupported,AudioCodecNotSupported" vs "VideoCodecNotSupported"
    /// with every other byte equal. In the server it is only parsed into
    /// EncodingJobInfo.TranscodeReasons (MediaBrowser.Controller/MediaEncoding/EncodingJobInfo.cs)
    /// for the session's TranscodingInfo report (TranscodeManager.cs) and copied into variant
    /// playlist URLs (DynamicHlsHelper.cs); no ffmpeg argument is derived from it.
    ///
    /// SubtitleMethod and SubtitleCodec are also excluded when NEITHER URL has a
    /// SubtitleStreamIndex (rr-C1): StreamInfo.ToUrl emits SubtitleMethod whenever the decision had
    /// any subtitle index, -1 (Off, what jellyfin-web's details page sends) included, but only
    /// emits SubtitleStreamIndex for a real track. Without SubtitleStreamIndex the job has no
    /// subtitle stream (EncodingHelper: state.SubtitleStream is null), and every use of the
    /// delivery method in the ffmpeg arguments (EncodingHelper, TranscodeManager) and of the codec
    /// list (GetSubtitleEmbedArguments) is behind a subtitle stream; the playlists only differ for
    /// SubtitleMethod=Hls (DynamicHlsHelper's subtitle group) or Drop, which therefore must still
    /// match.
    /// </summary>
    public static bool TranscodingUrlsMatch(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var ignoreSubtitleMethod = !HasParam(a, SubtitleStreamIndexParam) && !HasParam(b, SubtitleStreamIndexParam)
            && InertSubtitleMethod(a) && InertSubtitleMethod(b);
        var ignored = ignoreSubtitleMethod
            ? new[] { PlaySessionIdParam, TranscodeReasonsParam, SubtitleMethodParam, SubtitleCodecParam }
            : new[] { PlaySessionIdParam, TranscodeReasonsParam };
        return string.Equals(RemoveParams(a, ignored), RemoveParams(b, ignored), StringComparison.Ordinal);
    }

    /// <summary>Every SubtitleMethod value of the URL (if any) is one that changes nothing without a subtitle stream.</summary>
    private static bool InertSubtitleMethod(string url)
        => ParamValues(url, SubtitleMethodParam).All(v =>
            v.Equals("Encode", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Embed", StringComparison.OrdinalIgnoreCase)
            || v.Equals("External", StringComparison.OrdinalIgnoreCase));

    private static bool HasParam(string url, string name) => ParamValues(url, name).Any();

    private static IEnumerable<string> ParamValues(string url, string name)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            yield break;
        }

        foreach (var p in url[(q + 1)..].Split('&'))
        {
            var eq = p.IndexOf('=', StringComparison.Ordinal);
            if ((eq < 0 ? p : p[..eq]).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                yield return eq < 0 ? string.Empty : Uri.UnescapeDataString(p[(eq + 1)..]);
            }
        }
    }

    /// <summary>
    /// Rewrites a PlaybackInfo response from <paramref name="freshPsid"/> to
    /// <paramref name="prewarmPsid"/>: the top-level PlaySessionId and the PlaySessionId inside
    /// every TranscodingUrl. Both are random 32-hex GUIDs that appear nowhere else, so this is a
    /// byte substitution of the fresh id; every other byte of the response is untouched. Null when
    /// the response's top-level PlaySessionId is not <paramref name="freshPsid"/> or either id is
    /// not a plain token.
    /// </summary>
    public static byte[]? RewritePlaySessionId(byte[] json, string freshPsid, string prewarmPsid)
    {
        if (!IsPlainId(freshPsid) || !IsPlainId(prewarmPsid))
        {
            return null;
        }

        var parsed = ParsePlaybackInfo(json);
        if (parsed is null || !string.Equals(parsed.PlaySessionId, freshPsid, StringComparison.Ordinal))
        {
            return null;
        }

        var from = Encoding.ASCII.GetBytes(freshPsid);
        var to = Encoding.ASCII.GetBytes(prewarmPsid);
        var output = new List<byte>(json.Length + 64);
        var span = json.AsSpan();
        while (true)
        {
            var idx = span.IndexOf(from);
            if (idx < 0)
            {
                output.AddRange(span.ToArray());
                break;
            }

            output.AddRange(span[..idx].ToArray());
            output.AddRange(to);
            span = span[(idx + from.Length)..];
        }

        return output.ToArray();
    }

    /// <summary>
    /// The fresh feature PlaybackInfo response handed over onto the prewarm's session: every
    /// PlaySessionId of <paramref name="freshPsid"/> becomes <paramref name="prewarmPsid"/> (as
    /// <see cref="RewritePlaySessionId"/>), and the first media source's TranscodingUrl becomes the
    /// prewarm's VERBATIM - its raw JSON string token copied byte for byte out of
    /// <paramref name="prewarmBody"/>, the server's own encoding of it. Every other byte is the
    /// fresh response's. The player then requests exactly the URLs the pre-start (and a browser
    /// prefetch) already requested, so they can be served from the browser's HTTP cache.
    /// Only call this when <see cref="TranscodingUrlsMatch"/> holds for the two URLs. Null when
    /// either response lacks the token or the result does not read back as intended.
    /// </summary>
    public static byte[]? HandOverToPrewarm(byte[] freshBody, string freshPsid, string prewarmPsid, byte[] prewarmBody)
    {
        var prewarm = ParsePlaybackInfo(prewarmBody);
        var prewarmUrl = prewarm?.First?.TranscodingUrl;
        var prewarmToken = FindFirstTranscodingUrlToken(prewarmBody);
        if (prewarmUrl is null || prewarmToken is null)
        {
            return null;
        }

        var rewritten = RewritePlaySessionId(freshBody, freshPsid, prewarmPsid);
        if (rewritten is null)
        {
            return null;
        }

        var freshToken = FindFirstTranscodingUrlToken(rewritten);
        if (freshToken is null)
        {
            return null;
        }

        var (start, length) = freshToken.Value;
        var (pStart, pLength) = prewarmToken.Value;
        var output = new byte[rewritten.Length - length + pLength];
        rewritten.AsSpan(0, start).CopyTo(output);
        prewarmBody.AsSpan(pStart, pLength).CopyTo(output.AsSpan(start));
        rewritten.AsSpan(start + length).CopyTo(output.AsSpan(start + pLength));

        var check = ParsePlaybackInfo(output);
        return check is not null
               && string.Equals(check.PlaySessionId, prewarmPsid, StringComparison.Ordinal)
               && string.Equals(check.First?.TranscodingUrl, prewarmUrl, StringComparison.Ordinal)
            ? output
            : null;
    }

    /// <summary>
    /// Byte range (start, length) of the raw JSON string token, quotes included, holding
    /// MediaSources[0].TranscodingUrl (names matched case-insensitively). Null when absent.
    /// </summary>
    internal static (int Start, int Length)? FindFirstTranscodingUrlToken(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            // Root members: find MediaSources, skip everything else.
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isSources = reader.GetString()!.Equals("MediaSources", StringComparison.OrdinalIgnoreCase);
                if (!reader.Read())
                {
                    return null;
                }

                if (!isSources || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return null;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isUrl = reader.GetString()!.Equals("TranscodingUrl", StringComparison.OrdinalIgnoreCase);
                    if (!reader.Read())
                    {
                        return null;
                    }

                    if (isUrl)
                    {
                        if (reader.TokenType != JsonTokenType.String || reader.HasValueSequence)
                        {
                            return null;
                        }

                        var start = (int)reader.TokenStartIndex;
                        return (start, reader.ValueSpan.Length + 2);
                    }

                    reader.Skip();
                }

                return null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>
    /// Names of the query parameters whose values differ between two URLs (or that only one has),
    /// for logs: the URLs themselves carry the user's ApiKey and must never be logged.
    /// </summary>
    public static IReadOnlyList<string> DifferingParamNames(string? a, string? b)
    {
        static Dictionary<string, List<string>> Parse(string? url)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var q = url?.IndexOf('?', StringComparison.Ordinal) ?? -1;
            if (url is null || q < 0)
            {
                return map;
            }

            foreach (var part in url[(q + 1)..].Split('&'))
            {
                if (part.Length == 0)
                {
                    continue;
                }

                var eq = part.IndexOf('=', StringComparison.Ordinal);
                var name = eq < 0 ? part : part[..eq];
                if (!map.TryGetValue(name, out var values))
                {
                    map[name] = values = new List<string>();
                }

                values.Add(eq < 0 ? string.Empty : part[(eq + 1)..]);
            }

            return map;
        }

        var pa = Parse(a);
        var pb = Parse(b);
        var names = new List<string>();
        if (a is not null && b is not null)
        {
            var qa = a.IndexOf('?', StringComparison.Ordinal);
            var qb = b.IndexOf('?', StringComparison.Ordinal);
            var pathA = qa < 0 ? a : a[..qa];
            var pathB = qb < 0 ? b : b[..qb];
            if (!string.Equals(pathA, pathB, StringComparison.Ordinal))
            {
                names.Add("(path)");
            }
        }

        foreach (var name in pa.Keys.Concat(pb.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var va = pa.TryGetValue(name, out var x) ? x : null;
            var vb = pb.TryGetValue(name, out var y) ? y : null;
            if (va is null || vb is null || !va.SequenceEqual(vb, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Letters, digits and '-' only (Jellyfin mints Guid "N" strings).</summary>
    public static bool IsPlainId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
