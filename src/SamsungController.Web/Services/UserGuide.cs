using System.Reflection;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SamsungController.Web.Services;

// Only public, build-embedded documentation is exposed. No filesystem path or
// configuration directory supplied by a browser is ever opened.
public static class UserGuide
{
    private static readonly Assembly Assembly = typeof(UserGuide).Assembly;
    private static readonly IReadOnlyDictionary<string, string> Resources = Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("UserGuide/", StringComparison.Ordinal))
        .ToDictionary(name => name[10..].Replace('\\', '/'), name => name, StringComparer.OrdinalIgnoreCase);
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UsePipeTables().UseTaskLists().UseAutoIdentifiers(AutoIdentifierOptions.GitHub).Build();

    public static string? Render(string? documentPath)
    {
        var name = string.IsNullOrEmpty(documentPath) ? "README.md" : documentPath;
        if (!(name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name == "LICENSE") || !Resources.TryGetValue(name, out var resource)) return null;
        using var stream = Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var document = Markdown.Parse(reader.ReadToEnd(), Pipeline);
        foreach (var link in document.Descendants<LinkInline>()) link.Url = RewriteLink(name, link.Url ?? "", link.IsImage);
        return Markdown.ToHtml(document, Pipeline);
    }

    public static string RewriteLink(string documentPath, string url, bool image = false)
    {
        if (url.StartsWith('#')) return url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return !image && absolute.Scheme is "https" or "http" or "mailto" ? url : "#";
        if (url.StartsWith('/') || url.Contains('\\')) return "#";
        var resolved = new Uri(new Uri("https://guide.invalid/" + documentPath), url);
        var name = Uri.UnescapeDataString(resolved.AbsolutePath[1..]);
        if (image) return Resources.ContainsKey(name) && name.StartsWith("docs/images/", StringComparison.Ordinal)
            && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "/guide-assets/" + name : "#";
        if (Resources.ContainsKey(name)) return "/readme/" + name + resolved.Fragment;
        // Other public repository examples are online, never local file reads.
        return "https://github.com/whipstein/SamsungController/blob/main/" + resolved.AbsolutePath[1..] + resolved.Fragment;
    }

    public static byte[]? Image(string? name)
    {
        if (name is null || !name.StartsWith("docs/images/", StringComparison.Ordinal) || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !Resources.TryGetValue(name, out var resource)) return null;
        using var stream = Assembly.GetManifestResourceStream(resource)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
