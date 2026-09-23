using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class IndexHtmlInjectionFilterTests
{
    private const string Page =
        "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div id=\"reactRoot\"></div>"
        + "<script defer=\"defer\" src=\"main.jellyfin.bundle.js?4c3e\"></script></body></html>";

    private static readonly string RelativeTag =
        "<script src=\"../Plugins/Projectionist/Hook.js?v=" + IndexHtmlTransformer.ScriptVersion + "\" defer></script>";

    // ---------- request matching / script src ----------

    [Theory]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/web", true)]
    [InlineData("GET", "/web/", true)]
    [InlineData("GET", "/web/index.html", true)]
    [InlineData("GET", "/jellyfin/web/", true)]
    [InlineData("GET", "/jellyfin/web/index.html", true)]
    [InlineData("GET", "/WEB/INDEX.HTML", true)]
    [InlineData("HEAD", "/web/", false)]
    [InlineData("POST", "/web/index.html", false)]
    [InlineData("GET", "/web/config.json", false)]
    [InlineData("GET", "/web/main.jellyfin.bundle.js", false)]
    [InlineData("GET", "/Videos/abc/stream.mp4", false)]
    [InlineData("GET", null, false)]
    public void MatchesOnlyTheWebClientEntryPoints(string method, string? path, bool expected)
    {
        Assert.Equal(expected, IndexHtmlInjectionFilter.IsIndexHtmlRequest(method, path));
    }

    [Theory]
    [InlineData("", "/web/")]
    [InlineData("", "/web/index.html")]
    [InlineData("", "/jellyfin/web/")]
    [InlineData("", "/jellyfin/web/index.html")]
    [InlineData("/base", "/web/")]
    public void PagesUnderWebGetThePageRelativeSrc(string pathBase, string path)
    {
        Assert.Equal("../Plugins/Projectionist/Hook.js?v=" + IndexHtmlTransformer.ScriptVersion,
            IndexHtmlInjectionFilter.ScriptSrcFor(new PathString(pathBase), path));
    }

    [Theory]
    [InlineData("", "/", "/Plugins/Projectionist/Hook.js")]
    [InlineData("", "/web", "/Plugins/Projectionist/Hook.js")]
    [InlineData("", "/jellyfin/web", "/jellyfin/Plugins/Projectionist/Hook.js")]
    [InlineData("/base", "/", "/base/Plugins/Projectionist/Hook.js")]
    public void OtherEntryPointsGetAnAbsoluteSrcIncludingTheBaseUrl(string pathBase, string path, string expected)
    {
        Assert.Equal(expected + "?v=" + IndexHtmlTransformer.ScriptVersion,
            IndexHtmlInjectionFilter.ScriptSrcFor(new PathString(pathBase), path));
    }

    // ---------- body decoding ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("identity")]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData("gzip, br")]
    public void DecodesTheCodingsJellyfinCanProduce(string? coding)
    {
        var plain = Encoding.UTF8.GetBytes(Page);
        Assert.True(IndexHtmlInjectionFilter.TryDecode(Encode(plain, coding), coding, out var decoded));
        Assert.Equal(plain, decoded);
    }

    [Fact]
    public void RefusesUnknownCodingsAndCorruptBodies()
    {
        var plain = Encoding.UTF8.GetBytes(Page);
        Assert.False(IndexHtmlInjectionFilter.TryDecode(plain, "zstd", out _));
        Assert.False(IndexHtmlInjectionFilter.TryDecode(plain, "gzip", out _));
    }

    [Fact]
    public void TransformBodyInjectsIntoAGzipBodyAndReturnsPlainUtf8()
    {
        var gz = Encode(Encoding.UTF8.GetBytes(Page), "gzip");
        var result = IndexHtmlInjectionFilter.TransformBody(gz, "text/html", "gzip", RelativeTag, out var failure);
        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.Equal(IndexHtmlTransformer.InjectScriptTag(Page, RelativeTag), Encoding.UTF8.GetString(result!));
    }

    [Fact]
    public void TransformBodyLeavesNonHtmlAndUndecodableBodiesAlone()
    {
        var plain = Encoding.UTF8.GetBytes(Page);
        Assert.Null(IndexHtmlInjectionFilter.TransformBody(plain, "application/json", null, RelativeTag, out var f1));
        Assert.Null(f1);
        Assert.Null(IndexHtmlInjectionFilter.TransformBody(plain, "text/html", "zstd", RelativeTag, out var f2));
        Assert.Contains("zstd", f2, StringComparison.Ordinal);
    }

    // ---------- the middleware inside a real pipeline with Jellyfin's response compression ----------

    [Theory]
    [InlineData("gzip, deflate, br")]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData(null)]
    [InlineData("identity")]
    public async Task IndexIsServedPlainAndInjectedWhateverTheClientAccepts(string? acceptEncoding)
    {
        var run = await RunAsync("/web/", acceptEncoding);

        Assert.Equal(200, run.Context.Response.StatusCode);
        Assert.False(run.Context.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(IndexHtmlTransformer.InjectScriptTag(Page, RelativeTag), Encoding.UTF8.GetString(run.Body));
        Assert.Equal(run.Body.Length, run.Context.Response.ContentLength);
        Assert.False(run.Context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal("no-cache, no-store, must-revalidate", run.Context.Response.Headers.CacheControl.ToString());
        // The inner pipeline never saw Accept-Encoding; the request gets it back afterwards.
        Assert.False(run.InnerSawAcceptEncoding);
        Assert.Equal(acceptEncoding ?? string.Empty, run.Context.Request.Headers.AcceptEncoding.ToString());
    }

    [Fact]
    public async Task ResponseCompressionIsLiveInThisPipelineForOtherPaths()
    {
        // Control for the test above: the same pipeline does compress a non-index page.
        var run = await RunAsync("/web/other.html", "gzip");

        Assert.True(run.InnerSawAcceptEncoding);
        Assert.Equal("gzip", run.Context.Response.Headers.ContentEncoding.ToString());
        Assert.True(IndexHtmlInjectionFilter.TryDecode(run.Body, "gzip", out var plain));
        Assert.Equal(Page, Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public async Task AStillCompressedIndexIsDecodedBeforeInjecting()
    {
        // Some inner component compresses regardless of the request: decode, inject, send plain.
        var run = await RunAsync("/web/index.html", "gzip", inner: async ctx =>
        {
            var gz = Encode(Encoding.UTF8.GetBytes(Page), "gzip");
            ctx.Response.ContentType = "text/html";
            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentLength = gz.Length;
            await ctx.Response.Body.WriteAsync(gz);
        });

        Assert.False(run.Context.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(IndexHtmlTransformer.InjectScriptTag(Page, RelativeTag), Encoding.UTF8.GetString(run.Body));
        Assert.Equal(run.Body.Length, run.Context.Response.ContentLength);
    }

    [Fact]
    public async Task AnUndecodableIndexIsPassedThroughByteForByte()
    {
        var opaque = new byte[] { 0x28, 0xb5, 0x2f, 0xfd, 1, 2, 3, 4 };
        var run = await RunAsync("/web/", "zstd", inner: async ctx =>
        {
            ctx.Response.ContentType = "text/html";
            ctx.Response.Headers.ContentEncoding = "zstd";
            await ctx.Response.Body.WriteAsync(opaque);
        });

        Assert.Equal(opaque, run.Body);
        Assert.Equal("zstd", run.Context.Response.Headers.ContentEncoding.ToString());
    }

    [Fact]
    public async Task ConditionalHeadersAreHiddenSoTheBrowserCannotKeepAStaleUninjectedCopy()
    {
        var run = await RunAsync("/web/", "gzip", configure: req =>
        {
            req.Headers.IfNoneMatch = "\"v1\"";
            req.Headers.IfModifiedSince = "Tue, 22 Sep 2026 10:00:00 GMT";
        }, inner: async ctx =>
        {
            // What StaticFileMiddleware would do for a matching validator.
            if (ctx.Request.Headers.ContainsKey("If-None-Match") || ctx.Request.Headers.ContainsKey("If-Modified-Since"))
            {
                ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(Page);
        });

        Assert.Equal(200, run.Context.Response.StatusCode);
        Assert.Contains(RelativeTag, Encoding.UTF8.GetString(run.Body), StringComparison.Ordinal);
        Assert.Equal("\"v1\"", run.Context.Request.Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task NonSuccessResponsesAreNotTouched()
    {
        var run = await RunAsync("/web/", null, inner: async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync("<html><body>nope</body></html>");
        });

        Assert.Equal("<html><body>nope</body></html>", Encoding.UTF8.GetString(run.Body));
    }

    [Fact]
    public async Task AnAlreadyInjectedPageIsNotInjectedTwice()
    {
        var injected = IndexHtmlTransformer.InjectScriptTag(Page, RelativeTag);
        var run = await RunAsync("/web/", "br", inner: async ctx =>
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(injected);
        });

        Assert.Equal(injected, Encoding.UTF8.GetString(run.Body));
    }

    [Fact]
    public async Task OtherRequestsPassThroughUntouched()
    {
        var run = await RunAsync("/web/config.json", null, inner: async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"includeCorsCredentials\":false}");
        });

        Assert.Equal("{\"includeCorsCredentials\":false}", Encoding.UTF8.GetString(run.Body));
    }

    // ---------- helpers ----------

    private sealed record Run(HttpContext Context, byte[] Body, bool InnerSawAcceptEncoding);

    private static async Task<Run> RunAsync(
        string path,
        string? acceptEncoding,
        Func<HttpContext, Task>? inner = null,
        Action<HttpRequest>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResponseCompression();
        var provider = services.BuildServiceProvider();

        var sawAcceptEncoding = false;
        var app = new ApplicationBuilder(provider);
        var filter = new IndexHtmlInjectionFilter(NullLogger<IndexHtmlInjectionFilter>.Instance);
        filter.Configure(b =>
        {
            // Jellyfin's own Startup: compression inside, static files after it.
            b.UseResponseCompression();
            b.Run(async ctx =>
            {
                sawAcceptEncoding = ctx.Request.Headers.ContainsKey("Accept-Encoding");
                if (inner is not null)
                {
                    await inner(ctx);
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(Page);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength = bytes.Length;
                ctx.Response.Headers.ETag = "\"v1\"";
                ctx.Response.Headers.CacheControl = "no-cache";
                await ctx.Response.Body.WriteAsync(bytes);
            });
        })(app);
        var pipeline = app.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        if (acceptEncoding is not null)
        {
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        }

        configure?.Invoke(context.Request);
        var output = new MemoryStream();
        context.Response.Body = output;

        await pipeline(context);
        return new Run(context, output.ToArray(), sawAcceptEncoding);
    }

    private static byte[] Encode(byte[] plain, string? codings)
    {
        var data = plain;
        foreach (var coding in (codings ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (coding == "identity")
            {
                continue;
            }

            using var output = new MemoryStream();
            using (Stream encoder = coding switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => throw new ArgumentException("unknown coding " + coding),
            })
            {
                encoder.Write(data);
            }

            data = output.ToArray();
        }

        return data;
    }
}
