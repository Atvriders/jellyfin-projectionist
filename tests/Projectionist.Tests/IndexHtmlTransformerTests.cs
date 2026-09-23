using System;
using System.IO;
using Jellyfin.Plugin.Projectionist.Web;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class IndexHtmlTransformerTests
{
    private const string Tag = "<script src=\"x/Plugins/Projectionist/Hook.js\" defer></script>";

    [Fact]
    public void InsertsBeforeTheLastClosingBody()
    {
        var html = "<html><body><p>a</body>x</p></BODY></html>";
        Assert.Equal("<html><body><p>a</body>x</p>" + Tag + "</BODY></html>", IndexHtmlTransformer.InjectScriptTag(html, Tag));
    }

    [Fact]
    public void AppendsWhenThereIsNoClosingBody()
    {
        Assert.Equal("<p>a</p>\n" + Tag, IndexHtmlTransformer.InjectScriptTag("<p>a</p>", Tag));
    }

    [Fact]
    public void LeavesAnAlreadyInjectedPageAlone()
    {
        var html = "<body><script src=\"../Plugins/Projectionist/Hook.js?v=1\" defer></script></body>";
        Assert.Same(html, IndexHtmlTransformer.InjectScriptTag(html, Tag));
    }

    [Fact]
    public void LeavesAnEmptyPageAlone()
    {
        Assert.Equal(string.Empty, IndexHtmlTransformer.InjectScriptTag(string.Empty, Tag));
    }

    [Fact]
    public void FileTransformationCallbackUsesThePageRelativeSrc()
    {
        var result = IndexHtmlTransformer.Transform(new IndexHtmlTransformer.Payload { contents = "<html><body></body></html>" });
        var expected = "<script src=\"../Plugins/Projectionist/Hook.js?v=" + IndexHtmlTransformer.ScriptVersion + "\" defer></script></body>";
        Assert.EndsWith(expected + "</html>", result, System.StringComparison.Ordinal);
    }

    [Fact]
    public void FileTransformationCallbackToleratesMissingContents()
    {
        Assert.Equal(string.Empty, IndexHtmlTransformer.Transform(new IndexHtmlTransformer.Payload()));
        Assert.Equal(string.Empty, IndexHtmlTransformer.Transform(null!));
    }

    [Fact]
    public void InjectedHookIsTheSourceFileTheNodeTestsExercise()
    {
        // The tag points at Plugins/Projectionist/Hook.js, which serves this embedded resource;
        // tests/js runs against src/Projectionist/Web/playback-hook.js. They must be the same bytes.
        var asm = typeof(IndexHtmlTransformer).Assembly;
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.Projectionist.Web.playback-hook.js");
        Assert.NotNull(stream);
        using var embedded = new MemoryStream();
        stream!.CopyTo(embedded);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Projectionist.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var source = File.ReadAllBytes(Path.Combine(dir!.FullName, "src", "Projectionist", "Web", "playback-hook.js"));
        Assert.Equal(source, embedded.ToArray());

        // The helpers the node tests read are only published when a test asks for them first.
        var script = System.Text.Encoding.UTF8.GetString(source);
        Assert.Contains("if (window.__projectionistExposeInternals === true) {", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptTagIsAttributeEncoded()
    {
        Assert.Equal("<script src=\"/a&quot;&gt;b/Plugins/Projectionist/Hook.js\" defer></script>",
            IndexHtmlTransformer.BuildScriptTag("/a\">b/Plugins/Projectionist/Hook.js"));
    }
}
