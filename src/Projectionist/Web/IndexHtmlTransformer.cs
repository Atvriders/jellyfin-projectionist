using System;
using System.Net;

namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// Static callback invoked by the FileTransformation plugin whenever Jellyfin's
/// web client requests index.html. We inject a script tag pointing at our
/// embedded playback-hook.js. Also holds the pure HTML transform shared with
/// <see cref="IndexHtmlInjectionFilter"/>.
/// </summary>
public static class IndexHtmlTransformer
{
    /// <summary>Shape FileTransformation hands us. Just `contents`.</summary>
    public sealed class Payload
    {
        public string? contents { get; set; }
    }

    /// <summary>Path of the hook script; its presence in a page means "already injected".</summary>
    internal const string Marker = "Plugins/Projectionist/Hook.js";

    // Use a version query so browsers don't cache stale copies of the hook
    // when we ship updates.
    internal static readonly string ScriptVersion =
        typeof(IndexHtmlTransformer).Module.ModuleVersionId.ToString("N");

    // index.html lives at {BaseUrl}/web/index.html and loads its own bundles
    // relative to itself (it has no <base>), so "../" reaches the server root.
    private static readonly string ScriptTag = BuildScriptTag(RelativeScriptSrc);

    /// <summary>Hook script URL relative to a page served from <c>.../web/</c>.</summary>
    internal static string RelativeScriptSrc => $"../{Marker}?v={ScriptVersion}";

    public static string Transform(Payload payload)
    {
        return InjectScriptTag(payload?.contents ?? string.Empty, ScriptTag);
    }

    /// <summary>The deferred script element for <paramref name="src"/> (attribute-encoded).</summary>
    internal static string BuildScriptTag(string src)
    {
        return $"<script src=\"{WebUtility.HtmlEncode(src)}\" defer></script>";
    }

    /// <summary>
    /// Inserts <paramref name="scriptTag"/> before the last &lt;/body&gt; (appends it when there is
    /// none). Returns <paramref name="html"/> unchanged when it is empty or already references the hook.
    /// </summary>
    internal static string InjectScriptTag(string html, string scriptTag)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html + "\n" + scriptTag;
        }

        return html.Substring(0, idx) + scriptTag + html.Substring(idx);
    }
}
