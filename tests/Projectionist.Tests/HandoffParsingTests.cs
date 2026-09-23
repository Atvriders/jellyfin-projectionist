using System;
using System.Net;
using Jellyfin.Plugin.Projectionist.Services.Handoff;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class HandoffParsingTests
{
    private static readonly Guid Id = Guid.Parse("a9e1d99d-f22a-ee60-2be9-16311cddf8bf");

    [Theory]
    [InlineData("/Items/a9e1d99df22aee602be916311cddf8bf/PlaybackInfo")]
    [InlineData("/items/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/playbackinfo")]
    [InlineData("/jellyfin/Items/A9E1D99DF22AEE602BE916311CDDF8BF/PlaybackInfo")]
    [InlineData("/some/base/Items/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/PlaybackInfo/")]
    [InlineData("/Users/99a5a3e4308f4c18958ae921595b5672/Items/a9e1d99df22aee602be916311cddf8bf/PlaybackInfo")]
    public void PlaybackInfoPathsAreRecognised(string path)
    {
        Assert.Equal(HandoffRequestKind.PlaybackInfo, HandoffRequestParser.Classify(path, out var id));
        Assert.Equal(Id, id);
    }

    [Theory]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/stream")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/stream.mp4")]
    [InlineData("/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/master.m3u8")]
    [InlineData("/jf/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/main.m3u8")]
    [InlineData("/videos/a9e1d99d-f22a-ee60-2be9-16311cddf8bf/hls1/main/-1.mp4")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/hls1/main/12.ts")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/hls/abc/stream.m3u8")]
    [InlineData("/videos/Videos/a9e1d99df22aee602be916311cddf8bf/stream.mkv")]
    public void StreamPathsAreRecognised(string path)
    {
        Assert.Equal(HandoffRequestKind.VideoStream, HandoffRequestParser.Classify(path, out var id));
        Assert.Equal(Id, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/web/index.html")]
    [InlineData("/Items/a9e1d99df22aee602be916311cddf8bf")]
    [InlineData("/Items/not-a-guid/PlaybackInfo")]
    [InlineData("/Items/00000000000000000000000000000000/PlaybackInfo")]
    [InlineData("/Other/a9e1d99df22aee602be916311cddf8bf/PlaybackInfo")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/Subtitles/1/Stream.vtt")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/AdditionalParts")]
    [InlineData("/Videos/a9e1d99df22aee602be916311cddf8bf/live.m3u8")]
    [InlineData("/Audio/a9e1d99df22aee602be916311cddf8bf/stream.mp3")]
    public void OtherPathsAreIgnored(string? path)
        => Assert.Equal(HandoffRequestKind.None, HandoffRequestParser.Classify(path, out _));

    [Fact]
    public void AuthorizationHeaderIsParsedLikeJellyfin()
    {
        var parts = HandoffRequestParser.ParseAuthorizationHeader(
            "MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\", DeviceId=\"TW96aWxsYS81LjA%3D\", Version=\"10.11.11\", Token=\"abc123\"");
        Assert.NotNull(parts);
        Assert.Equal("TW96aWxsYS81LjA=", parts!["DeviceId"]);
        Assert.Equal("abc123", parts["token"]);
        Assert.Equal("Jellyfin Web", parts["Client"]);
    }

    [Fact]
    public void LegacyEmbySchemeIsAcceptedAndUnknownSchemesAreNot()
    {
        Assert.Equal("d1", HandoffRequestParser.ParseAuthorizationHeader("Emby DeviceId=\"d1\"")!["DeviceId"]);
        Assert.Null(HandoffRequestParser.ParseAuthorizationHeader("Bearer abc"));
        Assert.Null(HandoffRequestParser.ParseAuthorizationHeader("MediaBrowser"));
    }

    [Fact]
    public void DeviceIdComesFromAuthorizationThenXEmbyAuthorizationThenQuery()
    {
        var h = new HeaderDictionary { ["Authorization"] = "MediaBrowser DeviceId=\"fromAuth\", Token=\"t\"" };
        Assert.Equal("fromAuth", HandoffRequestParser.GetDeviceId(h, Query("deviceId=fromQuery")));

        h = new HeaderDictionary { ["X-Emby-Authorization"] = "MediaBrowser DeviceId=\"fromEmby\"" };
        Assert.Equal("fromEmby", HandoffRequestParser.GetDeviceId(h, Query(string.Empty)));

        Assert.Equal("fromQuery", HandoffRequestParser.GetDeviceId(new HeaderDictionary(), Query("DeviceId=fromQuery&x=1")));
        Assert.Equal("q2", HandoffRequestParser.GetDeviceId(new HeaderDictionary(), Query("deviceid=q2")));
        Assert.Null(HandoffRequestParser.GetDeviceId(new HeaderDictionary(), Query(string.Empty)));
    }

    [Fact]
    public void TokenFollowsJellyfinPrecedence()
    {
        var h = new HeaderDictionary
        {
            ["Authorization"] = "MediaBrowser DeviceId=\"d\", Token=\"auth\"",
            ["X-Emby-Token"] = "emby",
        };
        Assert.Equal("auth", HandoffRequestParser.GetToken(h, Query("ApiKey=q")));

        h = new HeaderDictionary { ["X-Emby-Token"] = "emby", ["X-MediaBrowser-Token"] = "mb" };
        Assert.Equal("emby", HandoffRequestParser.GetToken(h, Query("ApiKey=q")));

        h = new HeaderDictionary { ["X-MediaBrowser-Token"] = "mb" };
        Assert.Equal("mb", HandoffRequestParser.GetToken(h, Query("ApiKey=q")));

        Assert.Equal("q", HandoffRequestParser.GetToken(new HeaderDictionary(), Query("ApiKey=q&api_key=legacy")));
        Assert.Equal("legacy", HandoffRequestParser.GetToken(new HeaderDictionary(), Query("api_key=legacy")));
        Assert.Null(HandoffRequestParser.GetToken(new HeaderDictionary(), Query(string.Empty)));
    }

    [Fact]
    public void QueryValueIsReadCaseInsensitively()
    {
        const string url = "/videos/x/master.m3u8?&DeviceId=abc%3D&PlaySessionId=p1&ApiKey=k";
        Assert.Equal("p1", HandoffRequestParser.GetQueryValue(url, "playsessionid"));
        Assert.Equal("abc=", HandoffRequestParser.GetQueryValue(url, "DeviceId"));
        Assert.Null(HandoffRequestParser.GetQueryValue(url, "Missing"));
        Assert.Null(HandoffRequestParser.GetQueryValue("/no/query", "x"));
    }

    [Fact]
    public void LoopbackHeaderNeedsTheExactSecret()
    {
        const string secret = "00112233445566778899AABBCCDDEEFF";
        Assert.True(HandoffLoopbackClient.TryParseHeader(secret + ";203.0.113.7", secret, out var ip));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ip);

        Assert.True(HandoffLoopbackClient.TryParseHeader(secret + ";2001:db8::5", secret, out ip));
        Assert.Equal(IPAddress.Parse("2001:db8::5"), ip);

        Assert.True(HandoffLoopbackClient.TryParseHeader(secret, secret, out ip));
        Assert.Null(ip);

        Assert.False(HandoffLoopbackClient.TryParseHeader("00112233445566778899AABBCCDDEEFE;203.0.113.7", secret, out ip));
        Assert.Null(ip);
        Assert.False(HandoffLoopbackClient.TryParseHeader("short;203.0.113.7", secret, out _));
        Assert.False(HandoffLoopbackClient.TryParseHeader(string.Empty, secret, out _));
        Assert.False(HandoffLoopbackClient.TryParseHeader(null, secret, out _));
    }

    [Fact]
    public void EachProcessInstanceHasItsOwnSecret()
    {
        using var a = new HandoffLoopbackClient(Microsoft.Extensions.Logging.Abstractions.NullLogger<HandoffLoopbackClient>.Instance, null, null, "http://127.0.0.1:1");
        using var b = new HandoffLoopbackClient(Microsoft.Extensions.Logging.Abstractions.NullLogger<HandoffLoopbackClient>.Instance, null, null, "http://127.0.0.1:1");
        var header = a.FormatHeader(IPAddress.Parse("198.51.100.4"));
        Assert.True(a.TryValidateHeader(header, out var ip));
        Assert.Equal(IPAddress.Parse("198.51.100.4"), ip);
        Assert.False(b.TryValidateHeader(header, out _));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    public void BaseUrlIsNormalised(string? input, string expected)
        => Assert.Equal(expected, HandoffLoopbackClient.NormalizeBaseUrl(input));

    [Fact]
    public void HlsPlaylistsAreWalkedTextually()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000\nmain.m3u8?DeviceId=d%3D&PlaySessionId=p&VideoBitrate=1\n";
        Assert.Equal("main.m3u8?DeviceId=d%3D&PlaySessionId=p&VideoBitrate=1", HandoffHls.FirstUri(master));
        const string main = "#EXTM3U\r\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?DeviceId=d&PlaySessionId=p\"\r\n#EXTINF:3,\r\nhls1/main/0.mp4?DeviceId=d&PlaySessionId=p&runtimeTicks=0\r\n";
        Assert.Equal("hls1/main/-1.mp4?DeviceId=d&PlaySessionId=p", HandoffHls.MapUri(main));
        Assert.Equal("hls1/main/0.mp4?DeviceId=d&PlaySessionId=p&runtimeTicks=0", HandoffHls.FirstUri(main));
        Assert.Null(HandoffHls.MapUri(master));

        Assert.Equal(
            "/videos/x/main.m3u8?DeviceId=d%3D",
            HandoffHls.Resolve("/videos/x/master.m3u8?&DeviceId=d%3D&a=/b", "main.m3u8?DeviceId=d%3D"));
        Assert.Equal("/videos/x/hls1/main/0.mp4?q=1", HandoffHls.Resolve("/videos/x/main.m3u8?q", "hls1/main/0.mp4?q=1"));
        Assert.Equal("/abs/path?q", HandoffHls.Resolve("/videos/x/main.m3u8", "/abs/path?q"));
        Assert.Equal("/videos/y/main.m3u8?a=b", HandoffHls.Resolve("/videos/x/main.m3u8", "http://127.0.0.1:8096/videos/y/main.m3u8?a=b"));
    }

    private static IQueryCollection Query(string q) => new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(q));
}
