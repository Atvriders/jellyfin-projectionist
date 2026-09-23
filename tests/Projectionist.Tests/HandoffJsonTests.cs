using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class HandoffJsonTests
{
    // Shape of a real 10.11.11 TranscodingUrl for jellyfin-web (A report, out-hls-cold.json).
    private const string Url1 =
        "/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/master.m3u8?&DeviceId=TW96aWxsYS81LjA%3D&MediaSourceId=a9e1d99df22aee602be916311cddf8bf"
        + "&VideoCodec=av1,h264,vp9&AudioCodec=aac,opus,flac&AudioStreamIndex=1&VideoBitrate=2847857&AudioBitrate=152143&MaxFramerate=30"
        + "&SegmentContainer=mp4&MinSegments=1&BreakOnNonKeyFrames=False&PlaySessionId=e615aeb1fca443b6916a4af3af74cd57&ApiKey=tok"
        + "&TranscodingMaxAudioChannels=2&RequireAvc=false&EnableAudioVbrEncoding=true&Tag=d3100dd9035afff7c9dc0cffd4f298f3"
        + "&SubtitleMethod=Encode&h264-level=40&TranscodeReasons=ContainerBitrateExceedsLimit";

    private static readonly string Url2 = Url1.Replace("e615aeb1fca443b6916a4af3af74cd57", "94aed149c4ca4d9baec6f39698e28b7d");

    [Fact]
    public void RemovePlaySessionIdKeepsEverythingElseVerbatim()
    {
        var stripped = HandoffJson.RemovePlaySessionId(Url1);
        Assert.DoesNotContain("PlaySessionId", stripped);
        Assert.StartsWith("/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/master.m3u8?&DeviceId=TW96aWxsYS81LjA%3D&", stripped);
        Assert.Contains("&BreakOnNonKeyFrames=False&ApiKey=tok&", stripped);
        Assert.Equal(Url1.Length - "&PlaySessionId=e615aeb1fca443b6916a4af3af74cd57".Length, stripped.Length);
        Assert.Equal("/x", HandoffJson.RemovePlaySessionId("/x"));
        Assert.Equal("/x?a=1", HandoffJson.RemovePlaySessionId("/x?playsessionid=2&a=1"));
    }

    [Fact]
    public void UrlsThatDifferOnlyInPlaySessionIdMatch()
        => Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, Url2));

    [Fact]
    public void TranscodeReasonsIsReportingOnlyAndIgnored()
    {
        // Measured on 10.11.11: explicit AudioStreamIndex adds AudioCodecNotSupported, all else equal.
        var withAudioReason = Url2.Replace("TranscodeReasons=ContainerBitrateExceedsLimit", "TranscodeReasons=ContainerBitrateExceedsLimit,AudioCodecNotSupported");
        Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, withAudioReason));
        Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, Url2.Replace("&TranscodeReasons=ContainerBitrateExceedsLimit", string.Empty)));
    }

    [Theory]
    [InlineData("AudioStreamIndex=1", "AudioStreamIndex=2")]
    [InlineData("VideoBitrate=2847857", "VideoBitrate=1847857")]
    [InlineData("ApiKey=tok", "ApiKey=other")]
    [InlineData("&SubtitleMethod=Encode", "&SubtitleStreamIndex=3&SubtitleMethod=Encode")]
    [InlineData("MediaSourceId=a9e1", "MediaSourceId=b9e1")]
    public void AnyOtherDifferenceIsAMismatch(string from, string to)
        => Assert.False(HandoffJson.TranscodingUrlsMatch(Url1, Url2.Replace(from, to)));

    [Fact]
    public void AnInertSubtitleMethodWithoutASubtitleStreamIsIgnored()
    {
        // rr-C1 (a): jellyfin-web's details page sends SubtitleStreamIndex=-1, so its URL carries
        // SubtitleMethod=Encode (no SubtitleStreamIndex: -1 is omitted); a Play without indexes
        // (and the prewarm) carries none. Without a subtitle stream the job is identical.
        var noMethod = Url2.Replace("&SubtitleMethod=Encode", string.Empty);
        Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, noMethod));
        Assert.True(HandoffJson.TranscodingUrlsMatch(noMethod, Url1));
        Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, Url2.Replace("SubtitleMethod=Encode", "SubtitleMethod=External")));
        Assert.True(HandoffJson.TranscodingUrlsMatch(Url1, Url2.Replace("SubtitleMethod=Encode", "SubtitleMethod=Embed&SubtitleCodec=srt")));
    }

    [Theory]
    [InlineData("&SubtitleMethod=Encode", "&SubtitleStreamIndex=3&SubtitleMethod=Encode")]
    [InlineData("&SubtitleMethod=Encode", "&SubtitleStreamIndex=3")]
    [InlineData("SubtitleMethod=Encode", "SubtitleMethod=Hls")]
    [InlineData("SubtitleMethod=Encode", "SubtitleMethod=Drop")]
    public void TheSubtitleMethodStillCountsWithASubtitleStreamOrForHlsAndDrop(string from, string to)
    {
        Assert.False(HandoffJson.TranscodingUrlsMatch(Url1, Url2.Replace(from, to)));
        Assert.False(HandoffJson.TranscodingUrlsMatch(Url1.Replace("&SubtitleMethod=Encode", string.Empty), Url2.Replace(from, to)));
    }

    [Fact]
    public void TheExplicitTracksBodyNamesTheSourceAndItsDefaultTracks()
    {
        var prewarm = HandoffJson.DerivePrewarmBody(Encoding.UTF8.GetBytes("{\"DeviceProfile\":{\"Name\":\"web\"},\"audioStreamIndex\":7,\"MaxStreamingBitrate\":1}"), out _);
        var body = JsonNode.Parse(HandoffJson.DeriveExplicitTracksBody(prewarm, "src1", 1, null))!.AsObject();
        Assert.Equal("src1", (string?)body["MediaSourceId"]);
        Assert.Equal(1, (int?)body["AudioStreamIndex"]);
        Assert.Equal(-1, (int?)body["SubtitleStreamIndex"]);
        Assert.Equal(1, (int?)body["MaxStreamingBitrate"]);
        Assert.NotNull(body["DeviceProfile"]);
        Assert.Single(body, kv => kv.Key.Equals("AudioStreamIndex", System.StringComparison.OrdinalIgnoreCase));

        var noAudio = JsonNode.Parse(HandoffJson.DeriveExplicitTracksBody(prewarm, "src1", null, 4))!.AsObject();
        Assert.False(noAudio.ContainsKey("AudioStreamIndex"));
        Assert.Equal(4, (int?)noAudio["SubtitleStreamIndex"]);
    }

    [Fact]
    public void ParseReadsTheDefaultTrackIndexes()
    {
        var info = HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes(
            "{\"MediaSources\":[{\"Id\":\"a\",\"DefaultAudioStreamIndex\":1,\"DefaultSubtitleStreamIndex\":3}],\"PlaySessionId\":\"p\"}"))!;
        Assert.Equal(1, info.First!.DefaultAudioStreamIndex);
        Assert.Equal(3, info.First.DefaultSubtitleStreamIndex);
        Assert.Null(HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes("{\"MediaSources\":[{\"Id\":\"a\"}]}"))!.First!.DefaultAudioStreamIndex);
    }

    [Fact]
    public void NullUrlsNeverMatch()
    {
        Assert.False(HandoffJson.TranscodingUrlsMatch(null, Url1));
        Assert.False(HandoffJson.TranscodingUrlsMatch(Url1, null));
        Assert.False(HandoffJson.TranscodingUrlsMatch(null, null));
    }

    private static string Response(string psid, string url) =>
        "{\"MediaSources\":[{\"Protocol\":\"File\",\"Id\":\"a9e1d99df22aee602be916311cddf8bf\",\"Path\":\"/m/Caf\\u00e9 \\\"x\\\".mkv\","
        + "\"Container\":\"mkv\",\"ETag\":\"d3100dd9035afff7c9dc0cffd4f298f3\",\"SupportsDirectPlay\":false,\"SupportsTranscoding\":true,"
        + "\"TranscodingUrl\":\"" + url + "\",\"TranscodingSubProtocol\":\"hls\",\"Bitrate\":12000000,\"RunTimeTicks\":2700000000},"
        + "{\"Id\":\"second\",\"SupportsDirectPlay\":false,\"TranscodingUrl\":\"/videos/y/master.m3u8?PlaySessionId=" + psid + "&x=1\"}],"
        + "\"PlaySessionId\":\"" + psid + "\"}";

    [Fact]
    public void RewriteReplacesTopLevelAndEveryTranscodingUrlAndNothingElse()
    {
        const string fresh = "94aed149c4ca4d9baec6f39698e28b7d";
        const string prewarm = "e615aeb1fca443b6916a4af3af74cd57";
        var body = Encoding.UTF8.GetBytes(Response(fresh, Url2));
        var rewritten = HandoffJson.RewritePlaySessionId(body, fresh, prewarm);
        Assert.NotNull(rewritten);

        var expected = Encoding.UTF8.GetBytes(Response(prewarm, Url1));
        Assert.Equal(expected, rewritten);

        var parsed = HandoffJson.ParsePlaybackInfo(rewritten);
        Assert.Equal(prewarm, parsed!.PlaySessionId);
        Assert.All(parsed.MediaSources, s => Assert.Equal(prewarm, HandoffRequestParser.GetQueryValue(s.TranscodingUrl, "PlaySessionId")));

        // Every byte outside the replaced ids is identical.
        var a = Encoding.UTF8.GetString(body).Replace(fresh, "#");
        var b = Encoding.UTF8.GetString(rewritten!).Replace(prewarm, "#");
        Assert.Equal(a, b);
    }

    [Fact]
    public void RewriteRefusesWhenTopLevelSessionIsNotTheFreshOne()
    {
        var body = Encoding.UTF8.GetBytes(Response("94aed149c4ca4d9baec6f39698e28b7d", Url2));
        Assert.Null(HandoffJson.RewritePlaySessionId(body, "ffffffffffffffffffffffffffffffff", "e615aeb1fca443b6916a4af3af74cd57"));
        Assert.Null(HandoffJson.RewritePlaySessionId(body, "94aed149c4ca4d9baec6f39698e28b7d", "bad\"id"));
        Assert.Null(HandoffJson.RewritePlaySessionId(Encoding.UTF8.GetBytes("not json"), "a", "b"));
    }

    [Fact]
    public void ParseReadsTheFieldsTheHandoffNeeds()
    {
        var parsed = HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes(Response("p1", Url1)));
        Assert.NotNull(parsed);
        Assert.Equal("p1", parsed!.PlaySessionId);
        Assert.Equal(2, parsed.MediaSources.Count);
        var s = parsed.First!;
        Assert.Equal("a9e1d99df22aee602be916311cddf8bf", s.Id);
        Assert.False(s.SupportsDirectPlay);
        Assert.Equal("mkv", s.Container);
        Assert.Equal("d3100dd9035afff7c9dc0cffd4f298f3", s.ETag);
        Assert.Equal(Url1, s.TranscodingUrl);
        Assert.Equal("hls", s.TranscodingSubProtocol);
        Assert.Null(parsed.ErrorCode);

        var camel = HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes("{\"mediaSources\":[{\"id\":\"x\",\"supportsDirectPlay\":true,\"container\":\"mp4\"}],\"playSessionId\":\"q\"}"));
        Assert.Equal("q", camel!.PlaySessionId);
        Assert.True(camel.First!.SupportsDirectPlay);
        Assert.Equal("mp4", camel.First.Container);

        Assert.Null(HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes("[1,2]")));
        Assert.Null(HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes("{broken")));
        Assert.Empty(HandoffJson.ParsePlaybackInfo(Encoding.UTF8.GetBytes("{}"))!.MediaSources);
    }

    [Fact]
    public void PrewarmBodyDropsIntroSpecificMembersAndKeepsTheRest()
    {
        const string intro = "{\"UserId\":\"99a5a3e4308f4c18958ae921595b5672\",\"StartTimeTicks\":12345,\"IsPlayback\":true,"
            + "\"AutoOpenLiveStream\":true,\"audioStreamIndex\":1,\"SubtitleStreamIndex\":-1,\"SecondarySubtitleStreamIndex\":2,"
            + "\"MediaSourceId\":\"introsource\",\"LiveStreamId\":\"ls\",\"MaxStreamingBitrate\":3000000,"
            + "\"AlwaysBurnInSubtitleWhenTranscoding\":false,\"EnableDirectStream\":false,\"AllowAudioStreamCopy\":false,"
            + "\"DeviceProfile\":{\"MaxStreamingBitrate\":120000000,\"DirectPlayProfiles\":[{\"Container\":\"mp4\"}]}}";
        var body = HandoffJson.DerivePrewarmBody(Encoding.UTF8.GetBytes(intro), out var hasProfile);
        Assert.True(hasProfile);
        var o = JsonNode.Parse(body)!.AsObject();
        var keys = o.Select(k => k.Key).ToList();
        foreach (var gone in new[] { "audioStreamIndex", "SubtitleStreamIndex", "SecondarySubtitleStreamIndex", "MediaSourceId", "LiveStreamId" })
        {
            Assert.DoesNotContain(gone, keys);
        }

        Assert.Equal(0, (long)o["StartTimeTicks"]!);
        Assert.False((bool)o["AutoOpenLiveStream"]!);
        Assert.Equal("99a5a3e4308f4c18958ae921595b5672", (string)o["UserId"]!);
        Assert.True((bool)o["IsPlayback"]!);
        Assert.Equal(3000000, (int)o["MaxStreamingBitrate"]!);
        Assert.False((bool)o["AlwaysBurnInSubtitleWhenTranscoding"]!);
        Assert.False((bool)o["EnableDirectStream"]!);
        Assert.False((bool)o["AllowAudioStreamCopy"]!);
        Assert.Equal(120000000, (int)o["DeviceProfile"]!["MaxStreamingBitrate"]!);
        Assert.Equal("mp4", (string)o["DeviceProfile"]!["DirectPlayProfiles"]![0]!["Container"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("{not json")]
    public void PrewarmBodyFallsBackToAnEmptyRequest(string intro)
    {
        var body = HandoffJson.DerivePrewarmBody(Encoding.UTF8.GetBytes(intro), out var hasProfile);
        Assert.False(hasProfile);
        Assert.Equal("{\"StartTimeTicks\":0,\"AutoOpenLiveStream\":false}", Encoding.UTF8.GetString(body));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("?", "")]
    [InlineData("?ApiKey=k&audioStreamIndex=1&MaxStreamingBitrate=3000000&MediaSourceId=m&startTimeTicks=5", "?ApiKey=k&MaxStreamingBitrate=3000000")]
    [InlineData("?SubtitleStreamIndex=2", "")]
    [InlineData("?userId=u&api_key=k", "?userId=u&api_key=k")]
    public void PrewarmQueryDropsTheSameMembers(string? query, string expected)
        => Assert.Equal(expected, HandoffJson.DerivePrewarmQuery(query));
}
