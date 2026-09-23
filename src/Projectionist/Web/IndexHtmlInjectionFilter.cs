using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// Plugged into the ASP.NET Core pipeline via DI (IStartupFilter is auto-discovered).
/// Intercepts responses for Jellyfin's web client index.html and injects our
/// playback hook script tag. This is the same pattern used by Achievement Badges
/// and StarTrack on this server.
/// </summary>
/// <remarks>
/// A startup filter runs outside everything Jellyfin's Startup adds, in particular
/// outside <c>UseResponseCompression</c> and outside <c>app.Map(BaseUrl)</c>. So:
/// <list type="bullet">
/// <item>For the index request we hide Accept-Encoding (plus the conditional and range
/// headers that would get a 304/206 instead of the page) from the inner pipeline, so the
/// page reaches us complete and uncompressed. Rewriting a gzip/br body as text is what
/// blanked the web client in v1.1.1. A body that is still encoded anyway is decoded when
/// we can, and otherwise passed through untouched.</item>
/// <item>The path still carries the BaseUrl, and a reverse proxy may have stripped a prefix
/// we never see, so the script src is relative to the page, like the page's own bundles.</item>
/// </list>
/// </remarks>
public sealed class IndexHtmlInjectionFilter : IStartupFilter
{
    private readonly ILogger<IndexHtmlInjectionFilter> _logger;

    // Request headers the inner pipeline must not see for the index page.
    private static readonly string[] HiddenRequestHeaders =
    {
        "Accept-Encoding", "If-None-Match", "If-Modified-Since", "If-Range", "Range",
    };

    public IndexHtmlInjectionFilter(ILogger<IndexHtmlInjectionFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private async Task InjectAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!IsIndexHtmlRequest(context.Request.Method, context.Request.Path.Value))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var hidden = HideRequestHeaders(context.Request.Headers);
        var originalBody = context.Response.Body;
        using var captured = new MemoryStream();
        context.Response.Body = captured;

        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
            RestoreRequestHeaders(context.Request.Headers, hidden);
        }

        var body = captured.ToArray();
        var response = context.Response;
        var scriptTag = IndexHtmlTransformer.BuildScriptTag(ScriptSrcFor(context.Request.PathBase, context.Request.Path.Value));
        byte[]? modified = null;
        if (response.StatusCode == StatusCodes.Status200OK)
        {
            modified = TransformBody(body, response.ContentType, response.Headers.ContentEncoding.ToString(), scriptTag, out var failure);
            if (failure is not null)
            {
                _logger.LogWarning("[Projectionist] could not inject hook script into {Path}: {Reason}", context.Request.Path, failure);
            }
        }

        if (modified is null)
        {
            await originalBody.WriteAsync(body).ConfigureAwait(false);
            return;
        }

        // The body is now plain UTF-8 and no longer the file the validators describe.
        response.Headers.Remove("Content-Encoding");
        response.Headers.Remove("ETag");
        response.Headers.Remove("Last-Modified");
        response.ContentLength = modified.Length;
        // Force fresh re-fetch so the injection lands in the browser even if it
        // had a cached pre-Projectionist copy.
        response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
        await originalBody.WriteAsync(modified).ConfigureAwait(false);
        _logger.LogInformation("[Projectionist] injected hook script into {Path}", context.Request.Path);
    }

    internal static bool IsIndexHtmlRequest(string method, string? path)
    {
        if (!HttpMethods.IsGet(method)) return false;
        path ??= string.Empty;
        // Match the common entry points for the web client.
        if (path.Equals("/", StringComparison.Ordinal)) return true;
        if (path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/web/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Script src for the page at <paramref name="path"/>. Pages under <c>.../web/</c> get
    /// the relative form; the rare "/" or ".../web" page gets an absolute one built from the
    /// path, which still includes Jellyfin's BaseUrl at this point in the pipeline.
    /// </summary>
    internal static string ScriptSrcFor(PathString pathBase, string? path)
    {
        path ??= string.Empty;
        if (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return IndexHtmlTransformer.RelativeScriptSrc;
        }

        var prefix = pathBase.HasValue ? pathBase.Value!.TrimEnd('/') : string.Empty;
        if (path.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
        {
            prefix += path.Substring(0, path.Length - "/web".Length);
        }

        return $"{prefix}/{IndexHtmlTransformer.Marker}?v={IndexHtmlTransformer.ScriptVersion}";
    }

    internal static List<KeyValuePair<string, StringValues>> HideRequestHeaders(IHeaderDictionary headers)
    {
        var hidden = new List<KeyValuePair<string, StringValues>>();
        foreach (var name in HiddenRequestHeaders)
        {
            if (headers.TryGetValue(name, out var value))
            {
                hidden.Add(new KeyValuePair<string, StringValues>(name, value));
                headers.Remove(name);
            }
        }

        return hidden;
    }

    internal static void RestoreRequestHeaders(IHeaderDictionary headers, List<KeyValuePair<string, StringValues>> hidden)
    {
        foreach (var header in hidden)
        {
            headers[header.Key] = header.Value;
        }
    }

    /// <summary>
    /// The page with the hook injected, as plain UTF-8, or null when <paramref name="body"/> must
    /// be sent unchanged: not HTML, already injected, empty, or encoded in a way we cannot undo
    /// (<paramref name="failure"/> then says why).
    /// </summary>
    internal static byte[]? TransformBody(byte[] body, string? contentType, string? contentEncoding, string scriptTag, out string? failure)
    {
        failure = null;
        if (contentType is null || !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryDecode(body, contentEncoding, out var plain))
        {
            failure = $"unsupported Content-Encoding '{contentEncoding}'";
            return null;
        }

        string html;
        using (var reader = new StreamReader(new MemoryStream(plain), Encoding.UTF8))
        {
            html = reader.ReadToEnd();
        }

        var modified = IndexHtmlTransformer.InjectScriptTag(html, scriptTag);
        if (ReferenceEquals(modified, html))
        {
            return null;
        }

        return Encoding.UTF8.GetBytes(modified);
    }

    /// <summary>
    /// Undoes <paramref name="contentEncoding"/> (gzip, br, deflate; a list is undone right to left).
    /// False for an unknown coding or a corrupt body.
    /// </summary>
    internal static bool TryDecode(byte[] body, string? contentEncoding, out byte[] decoded)
    {
        decoded = body;
        var codings = (contentEncoding ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !c.Equals("identity", StringComparison.OrdinalIgnoreCase))
            .Reverse();
        try
        {
            foreach (var coding in codings)
            {
                using var input = new MemoryStream(decoded);
                using Stream decoder = coding.ToLowerInvariant() switch
                {
                    "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                    _ => Stream.Null,
                };
                if (ReferenceEquals(decoder, Stream.Null))
                {
                    return false;
                }

                using var output = new MemoryStream();
                decoder.CopyTo(output);
                decoded = output.ToArray();
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
