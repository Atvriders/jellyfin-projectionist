using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using MediaBrowser.Model.Entities;
using Xunit;
using static Jellyfin.Plugin.Projectionist.Tests.HandoffRegistryTests;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>
/// R3-COR-1 / R3-COR-2: the rr-C1 explicit-tracks probe must ask in the shape jellyfin-web's
/// details page really sends: item.MediaSources[0] in the STATIC order and that source's
/// user-specific default tracks (or the preroll's carried-over choice), not the index-less
/// PlaybackInfo response's re-sorted first source and its own track pick.
/// </summary>
public class HandoffTrackPredictionTests
{
    private const string V1440 = "00dab86d1111222233334444555566aa";
    private const string V1080 = "c83c3a641111222233334444555566bb";

    private static readonly HandoffStream Video = new(0, MediaStreamType.Video, "h264", "1080p H264 SDR", null, IsDefault: true);

    /// <summary>A preroll whose one audio track is untagged AAC stereo (jellyfin-web plays audio 1).</summary>
    private static readonly HandoffSource Preroll = new(
        Intro1.ToString("N"),
        1,
        null,
        new[] { Video, new HandoffStream(1, MediaStreamType.Audio, "aac", "AAC - Stereo - Default", null, IsDefault: true) });

    private static string Direct(string sourceId, string psid, int defaultAudio = 1) =>
        "{\"MediaSources\":[{\"Id\":\"" + sourceId + "\",\"Container\":\"mp4\",\"ETag\":\"etag-dp\",\"SupportsDirectPlay\":true,"
        + "\"DefaultAudioStreamIndex\":" + defaultAudio + "}],\"PlaySessionId\":\"" + psid + "\"}";

    private static string Transcode(string sourceId, string psid, string audio) =>
        "{\"MediaSources\":[{\"Id\":\"" + sourceId + "\",\"Container\":\"mkv\",\"ETag\":\"etag\",\"SupportsDirectPlay\":false,"
        + "\"TranscodingUrl\":\"" + TranscodeUrl(psid, audio).Replace($"MediaSourceId={Feature:N}", "MediaSourceId=" + sourceId, StringComparison.Ordinal)
        + "\",\"TranscodingSubProtocol\":\"hls\"}],\"PlaySessionId\":\"" + psid + "\"}";

    private static JsonObject? Body(HandoffHttpRequest r) => r.JsonBody is null ? null : JsonNode.Parse(r.JsonBody)!.AsObject();

    private static bool Names(HandoffHttpRequest r, string sourceId)
        => string.Equals((string?)Body(r)?["MediaSourceId"], sourceId, StringComparison.Ordinal);

    private static int? Audio(HandoffHttpRequest r)
        => Body(r)?["AudioStreamIndex"] is { } a ? (int)a : null;

    private static List<HandoffHttpRequest> PlaybackInfos(Harness h)
        => h.Loopback.Requests.Where(r => r.PathAndQuery.Contains("/PlaybackInfo", StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------ registry (the rr-C1 probe)

    [Fact]
    public async Task TheDetailsPageProbeNamesTheStaticFirstVersionNotTheResponsesFirst()
    {
        // R3-COR-1 ('Golf Versions'): 1440p HEVC mkv + 1080p H.264 mp4. The index-less answer puts
        // the direct-playable 1080p first; the details page plays MediaSources[0] of the item DTO,
        // the widest version, which transcodes.
        var h = new Harness();
        h.Tracks = (_, _, _) => new HandoffTrackContext(
            new HandoffSource(V1440, 1, null, new[] { Video, new HandoffStream(1, MediaStreamType.Audio, "eac3", "EAC3 - 5.1 - Default", null, IsDefault: true) }),
            Preroll,
            true,
            true);
        h.Loopback.PlaybackInfoResponder = r => Names(r, V1440) ? Transcode(V1440, PrewarmPsid, "1") : Direct(V1080, FreshPsid);

        var e = await HotStarted(h);

        var second = PlaybackInfos(h)[1];
        Assert.True(Names(second, V1440));
        Assert.Equal(-1, (int?)Body(second)!["SubtitleStreamIndex"]);
        var up = h.Registry.GetUpcoming(User1, Device, Intro1)!;
        Assert.Equal("Transcode", up.PlayMethod);
        Assert.Null(up.DirectPlay);
        Assert.Equal(PrewarmPsid, e.HotPlaySessionId);
    }

    [Fact]
    public async Task TwoDirectPlayableVersionsPickedDifferentlyByCardAndDetailsAreNotAdvertised()
    {
        var h = new Harness();
        h.Tracks = (_, _, _) => new HandoffTrackContext(new HandoffSource(V1440, 1, null, new[] { Video }), null, true, true);
        h.Loopback.PlaybackInfoResponder = r => Names(r, V1440) ? Direct(V1440, FreshPsid) : Direct(V1080, PrewarmPsid);
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => PlaybackInfos(h).Count == 2, "both prewarms");
        await Task.Delay(50);

        Assert.True(Names(PlaybackInfos(h)[1], V1440));
        Assert.Null(e.Prewarm);
        Assert.Equal("Unknown", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public async Task TheDetailsPageProbeUsesTheUsersDefaultAudioNotTheServersPlayablePick()
    {
        // R3-COR-2 (a) ('Lima Dub'): a:1 AAC fre (not default), a:2 AC3 fre (default); a French
        // user. The index-less answer direct-plays a:1 and reports DefaultAudioStreamIndex=1; the
        // details page's dropdown holds a:2 and that Play transcodes. No carry-over: the preroll's
        // untagged "AAC - Stereo - Default" scores 2 against a:1.
        var h = new Harness();
        h.Tracks = (_, _, _) => new HandoffTrackContext(
            new HandoffSource(
                Feature.ToString("N"),
                2,
                null,
                new[]
                {
                    Video,
                    new HandoffStream(1, MediaStreamType.Audio, "aac", "French - AAC - Stereo", "fre"),
                    new HandoffStream(2, MediaStreamType.Audio, "ac3", "French - Dolby Digital - 5.1 - Default", "fre", IsDefault: true),
                }),
            Preroll,
            true,
            true);
        h.Loopback.PlaybackInfoResponder = r => Audio(r) == 2
            ? Transcode(Feature.ToString("N"), PrewarmPsid, "2")
            : Direct(Feature.ToString("N"), FreshPsid, defaultAudio: 1);

        var e = await HotStarted(h);

        Assert.Equal(2, Audio(PlaybackInfos(h)[1]));
        Assert.Equal("Transcode", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
        Assert.Equal(PrewarmPsid, e.HotPlaySessionId);
    }

    [Theory]
    [InlineData(true, 2, "DirectPlay")]
    [InlineData(false, 1, "Transcode")]
    public async Task ThePrerollsAudioChoiceCarriesOverLikeJellyfinWeb(bool rememberAudio, int expectedAudio, string expectedMethod)
    {
        // 'Mike Two Audio': a:1 AC3 + a:2 AAC, both default-flagged, untagged. The user default is
        // a:1, but after the preroll's "AAC - Stereo - Default" jellyfin-web sends a:2 (codec +1,
        // DisplayTitle +2) when the user remembers audio selections.
        var h = new Harness();
        h.Tracks = (_, _, _) => new HandoffTrackContext(
            new HandoffSource(
                Feature.ToString("N"),
                1,
                null,
                new[]
                {
                    Video,
                    new HandoffStream(1, MediaStreamType.Audio, "ac3", "Dolby Digital - 5.1 - Default", null, IsDefault: true),
                    new HandoffStream(2, MediaStreamType.Audio, "aac", "AAC - Stereo - Default", null, IsDefault: true),
                }),
            Preroll,
            rememberAudio,
            true);
        h.Loopback.PlaybackInfoResponder = r => Audio(r) == 1
            ? Transcode(Feature.ToString("N"), PrewarmPsid, "1")
            : Direct(Feature.ToString("N"), FreshPsid, defaultAudio: 1);
        var e = h.Register();
        h.Registry.OnIntroStreamRequest(e, "Mozilla/5.0 player");
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");

        Assert.Equal(expectedAudio, Audio(PlaybackInfos(h)[1]));
        Assert.Equal(expectedMethod, h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    [Fact]
    public async Task WithoutTheStaticSourcesTheResponsesFirstSourceIsUsed()
    {
        var h = new Harness { Tracks = (_, _, _) => throw new InvalidOperationException("library unavailable") };
        h.Loopback.PlaybackInfoResponse = DirectResponse(PrewarmPsid);
        var e = h.Register();
        h.Registry.OnIntroPlaybackInfo(e, h.IntroRequest());
        await Until(() => e.Prewarm is not null, "prewarm");
        var second = PlaybackInfos(h)[1];
        Assert.True(Names(second, Feature.ToString("N")));
        Assert.Equal(1, Audio(second));
        Assert.Equal("DirectPlay", h.Registry.GetUpcoming(User1, Device, Intro1)!.PlayMethod);
    }

    // ------------------------------------------------------------------ HandoffTracks (jellyfin-web's choice)

    [Fact]
    public void LanguageTaggedTracksDoNotCarryOverFromAnUntaggedPreroll()
    {
        // 'November Eng Two Audio': "English - AAC - Stereo - Default" is not the preroll's
        // DisplayTitle, so a:2 scores 1 (codec) and the default a:1 stays.
        var feature = new HandoffSource(
            Feature.ToString("N"),
            1,
            null,
            new[]
            {
                Video,
                new HandoffStream(1, MediaStreamType.Audio, "ac3", "English - Dolby Digital - 5.1 - Default", "eng", IsDefault: true),
                new HandoffStream(2, MediaStreamType.Audio, "aac", "English - AAC - Stereo - Default", "eng", IsDefault: true),
            });
        var choice = HandoffTracks.PredictDetailsPage(new HandoffTrackContext(feature, Preroll, true, true));
        Assert.Equal(new HandoffTrackChoice(Feature.ToString("N"), 1, -1), choice);
    }

    [Fact]
    public void TheDropdownsFallBackLikeTheDetailsPage()
    {
        // No usable default audio: the first track in sortTracks order (internal, forced, default
        // first, then by index). A default subtitle that is not a subtitle stream: Off.
        var feature = new HandoffSource(
            V1080,
            7,
            3,
            new[]
            {
                Video,
                new HandoffStream(1, MediaStreamType.Audio, "aac", "A", null),
                new HandoffStream(2, MediaStreamType.Audio, "ac3", "B", null, IsDefault: true),
                new HandoffStream(3, MediaStreamType.Audio, "dts", "C", null, IsExternal: true, IsDefault: true),
            });
        Assert.Equal(new HandoffTrackChoice(V1080, 2, -1), HandoffTracks.PredictDetailsPage(new HandoffTrackContext(feature, null, true, true)));

        var noAudio = feature with { Streams = new[] { Video, new HandoffStream(4, MediaStreamType.Subtitle, "srt", "English", "eng") }, DefaultSubtitleStreamIndex = 4 };
        Assert.Equal(new HandoffTrackChoice(V1080, null, 4), HandoffTracks.PredictDetailsPage(new HandoffTrackContext(noAudio, null, true, true)));
    }

    [Fact]
    public void APrerollPlayedWithSubtitlesOffCarriesOffOver()
    {
        var feature = new HandoffSource(
            V1080,
            1,
            2,
            new[]
            {
                Video,
                new HandoffStream(1, MediaStreamType.Audio, "aac", "English - AAC - Stereo", "eng"),
                new HandoffStream(2, MediaStreamType.Subtitle, "srt", "English - SRT", "eng"),
            });
        var preroll = Preroll with { DefaultSubtitleStreamIndex = -1 };
        Assert.Equal(-1, HandoffTracks.PredictDetailsPage(new HandoffTrackContext(feature, preroll, true, true)).SubtitleStreamIndex);
        Assert.Equal(2, HandoffTracks.PredictDetailsPage(new HandoffTrackContext(feature, preroll, true, false)).SubtitleStreamIndex);
        Assert.Equal(2, HandoffTracks.PredictDetailsPage(new HandoffTrackContext(feature, Preroll, true, true)).SubtitleStreamIndex);
    }

    [Fact]
    public void RankIsJellyfinWebsRankStreamType()
    {
        var prev = new HandoffSource(
            "p",
            2,
            null,
            new[]
            {
                Video,
                new HandoffStream(1, MediaStreamType.Audio, "ac3", "English - Dolby Digital - 5.1", "eng"),
                new HandoffStream(2, MediaStreamType.Audio, "aac", "English - AAC - Stereo", "eng"),
            });
        var next = new[]
        {
            Video,
            new HandoffStream(1, MediaStreamType.Audio, "aac", "German - AAC - Stereo", "ger"), // codec 1
            new HandoffStream(2, MediaStreamType.Audio, "opus", "English - Opus - Stereo", "eng"), // position 1 + language 2
            new HandoffStream(3, MediaStreamType.Audio, "aac", "English - AAC - Stereo", "eng"), // 1 + 2 + 2
        };
        Assert.Equal(3, HandoffTracks.Rank(2, prev, next, MediaStreamType.Audio));

        // Without a:3 the position + language match wins; with only a:1 (score 1) nothing does;
        // an index outside the preroll's streams (or -1) carries nothing.
        Assert.Equal(2, HandoffTracks.Rank(2, prev, next.Take(3).ToArray(), MediaStreamType.Audio));
        Assert.Null(HandoffTracks.Rank(2, prev, next.Take(2).ToArray(), MediaStreamType.Audio));
        Assert.Null(HandoffTracks.Rank(9, prev, next, MediaStreamType.Audio));
        Assert.Null(HandoffTracks.Rank(-1, prev, next, MediaStreamType.Audio));
    }
}
