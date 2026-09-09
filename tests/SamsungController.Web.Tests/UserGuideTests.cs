using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class UserGuideTests
{
    [Fact]
    public void BundledReadmeHasRequirementsPairingImageTablesAndLocalTutorialLinks()
    {
        var html = Assert.IsType<string>(UserGuide.Render(null));
        Assert.Contains("IP Remote must be enabled", html);
        Assert.Contains("Confirm trust and pair", html);
        Assert.Contains("<table>", html);
        Assert.Contains("id=\"where-to-enable-ip-remote\"", html);
        Assert.Contains("href=\"/readme/docs/getting-started.md\"", html);
        Assert.Contains("src=\"/guide-assets/docs/images/samsung-ip-remote-allow.png\"", html);
        Assert.Contains("macos-arm64.dmg", html);
        var tutorial = Assert.IsType<string>(UserGuide.Render("docs/getting-started.md"));
        Assert.Contains("href=\"/readme/README.md#where-to-enable-ip-remote\"", tutorial);
        Assert.Contains("src=\"/guide-assets/docs/images/samsung-ip-remote-allow.png\"", tutorial);
        Assert.NotNull(UserGuide.Render("LICENSE"));
    }

    [Theory]
    [InlineData("../README.md")]
    [InlineData("/etc/passwd")]
    [InlineData("ip-remote/profiles.json")]
    [InlineData("docs/../../ip-remote/tokens.json")]
    [InlineData("docs/images/samsung-ip-remote-allow.png")]
    public void OnlyEmbeddedMarkdownDocumentsCanBeRead(string name) => Assert.Null(UserGuide.Render(name));

    [Fact]
    public void OnlyEmbeddedGuideImagesCanBeDownloaded()
    {
        var bytes = Assert.IsType<byte[]>(UserGuide.Image("docs/images/samsung-ip-remote-allow.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Null(UserGuide.Image("README.md"));
        Assert.Null(UserGuide.Image("docs/images/../../ip-remote/profiles.json"));
        Assert.Null(UserGuide.Image("/etc/passwd"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,test")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//example.com")]
    public void UnsupportedLinkSchemesAreNotRendered(string url) => Assert.Equal("#", UserGuide.RewriteLink("README.md", url));

    [Fact]
    public void OfflineGuideDoesNotHotlinkExternalImages()
    {
        Assert.Equal("#", UserGuide.RewriteLink("README.md", "https://example.com/image.png", image: true));
        Assert.Equal("https://example.com/guide", UserGuide.RewriteLink("README.md", "https://example.com/guide"));
        Assert.Equal("#requirements", UserGuide.RewriteLink("README.md", "#requirements"));
    }

    [Fact]
    public async Task ReadmePageRendersWithoutControllerOrNetworkServices()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Readme>()).ToHtmlString());
        Assert.Contains("guide-markdown", html);
        Assert.Contains("IP Remote must be enabled", html);
        Assert.Contains("Reading this guide sends no TV commands", html);
    }
}
