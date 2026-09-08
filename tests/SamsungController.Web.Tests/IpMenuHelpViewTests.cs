using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Components.Shared;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuHelpViewTests
{
    [Theory]
    [InlineData("expert", "Arrange your controls")]
    [InlineData("sound", "Arrange your controls")]
    [InlineData("system", "Arrange your controls")]
    [InlineData("white2", "preserving untouched channels")]
    [InlineData("white20", "row's pending RGB targets")]
    [InlineData("color", "Choose Custom in Color space")]
    public async Task HelpHasSelectedSectionGuidanceInADismissiblePopover(string section, string guidance)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new HelpNavigation("menu")).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<DirectHelp>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(DirectHelp.Section)] = section }))).ToHtmlString());
        Assert.Contains("id=\"page-help\"", html);
        Assert.Contains("popover=\"auto\"", html);
        Assert.Contains("role=\"dialog\"", html);
        Assert.Contains("aria-labelledby=\"page-help-title\"", html);
        Assert.Contains("popovertargetaction=\"hide\"", html);
        Assert.Contains(guidance, html);
        Assert.Contains("Refresh state", html);
        Assert.Contains("discards unsent edits", html);
        Assert.Contains("Stop sends no automatic restoration", html);
        Assert.DoesNotContain("Refresh TV state", html);
    }

    [Fact]
    public async Task HelpFollowsNavigationBetweenDisplayMenuAndCommunication()
    {
        var navigation = new HelpNavigation("");
        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(navigation).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var page = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DirectHelp>());
        await renderer.Dispatcher.InvokeAsync(() => Assert.Contains("Save, pair, and connect", page.ToHtmlString()));
        await renderer.Dispatcher.InvokeAsync(() => navigation.NavigateTo("menu?test=1#controls"));
        await renderer.Dispatcher.InvokeAsync(() => Assert.Contains("Arrange your controls", page.ToHtmlString()));
        await renderer.Dispatcher.InvokeAsync(() => navigation.NavigateTo("diagnostics"));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            var html = page.ToHtmlString();
            Assert.Contains("Privacy and export", html);
            Assert.Contains("Tokens are always redacted", html);
            Assert.DoesNotContain("Arrange your controls", html);
        });
    }

    [Fact]
    public async Task MenuHasNoDuplicateHeadingsOrUsageBlocksAndKeepsDiscardAccessible()
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture, new ViewJavaScript());
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        foreach (var section in IpMenuCatalog.Sections)
        {
            await page.ClickAsync(section.Name);
            foreach (var text in new[] { "Adjustment details", "After changing HDMI signal", "fixed rows", "20-point RGB adjustments", "Custom color RGB adjustments", "Loading temporarily visits each row", "Limits are from Samsung" })
                await page.AssertTextAbsentAsync(text);
        }
        await page.ClickAsync("Picture");
        await page.ChangeAsync("Contrast value", "40", "onchange");
        await page.AssertDisabledAsync("Discard pending changes", false);
        await page.ClickAsync("Discard pending changes");
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task RememberedSectionLoadsBeforeScrollRestoreAndDoesNotReadTheTV()
    {
        using var fixture = await ReadyAsync();
        var js = new ViewJavaScript { SavedSection = "white20" };
        await using var services = Services(fixture, js);
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.AssertElementAttributeAsync("button", "20-point white balance", "aria-pressed", "true");
        Assert.Equal("white20", Assert.Single(js.Restored));
        await page.ClickAsync("Picture");
        Assert.Equal(new[] { "save", "restore:expert" }, js.ScrollActions.TakeLast(2));
        await page.ChangeAsync("Contrast value", "40", "onchange");
        Assert.Equal(2, js.Restored.Count); // Value updates do not restore/reposition the page.
        await page.ClickAsync("20-point white balance");
        Assert.Equal(new[] { "save", "restore:white20" }, js.ScrollActions.TakeLast(2));
        Assert.Single(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task UnknownRememberedSectionFallsBackToPicture()
    {
        using var fixture = await ReadyAsync();
        var js = new ViewJavaScript { SavedSection = "not-a-section" };
        await using var services = Services(fixture, js);
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.AssertElementAttributeAsync("button", "Picture", "aria-pressed", "true");
        Assert.Equal("expert", Assert.Single(js.Restored));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task GlobalHelpIsNextToRefreshAndAvailableWhileDisconnected()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await using var services = Services(fixture, new ViewJavaScript());
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(MainLayout));
        await page.StartAsync();
        Assert.Equal(new[] { "Home", "Back", "Exit menu", "Refresh state", "Help" }, await page.ButtonTextsWithinAsync("div", "TV shortcuts"));
        await page.AssertDisabledAsync("Refresh state", true);
        await page.AssertTextAbsentAsync("Refresh TV state");
        await using var htmlRenderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await htmlRenderer.Dispatcher.InvokeAsync(async () => (await htmlRenderer.RenderComponentAsync<MainLayout>()).ToHtmlString());
        var helpButton = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*popovertarget=\"page-help\"[^>]*>Help</button>").Value;
        Assert.NotEmpty(helpButton);
        Assert.DoesNotContain("disabled", helpButton);
        Assert.Empty(fixture.Display.Requests);
    }

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Display.Requests.Clear();
        return fixture;
    }
    private static ServiceProvider Services(MenuFixture fixture, IJSRuntime js) => new ServiceCollection().AddLogging()
        .AddSingleton(fixture.Service).AddSingleton(js).AddSingleton<NavigationManager>(new HelpNavigation("menu")).BuildServiceProvider();
    private sealed class HelpNavigation : NavigationManager
    {
        public HelpNavigation(string route) => Initialize("http://localhost/", "http://localhost/" + route);
        protected override void NavigateToCore(string uri, bool forceLoad) { Uri = ToAbsoluteUri(uri).ToString(); NotifyLocationChanged(false); }
    }
    private sealed class ViewJavaScript : IJSRuntime
    {
        public string? SavedSection { get; init; }
        public List<string> ScrollActions { get; } = [];
        public List<string> Restored { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "samsungMenuToolbar.attach") return ValueTask.FromResult((TValue)(object?)SavedSection!);
            if (identifier == "samsungMenuToolbar.savePosition") ScrollActions.Add("save");
            if (identifier == "samsungMenuToolbar.restorePosition") { var section = (string)args![2]!; Restored.Add(section); ScrollActions.Add("restore:" + section); }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
