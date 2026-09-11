using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class CompactLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LayoutToggleIsPresentationOnlyAndIndependentOfTheme(bool connected)
    {
        using var fixture = await MenuFixture.CreateAsync();
        if (connected)
        {
            await fixture.Service.ConnectMenuAsync();
            fixture.Service.StageMenuValue("contrastControl/contrast", "31");
        }
        var before = fixture.Service.GetSnapshot().Menu;
        fixture.Display.Requests.Clear();
        var js = new LayoutJs();
        await using var services = Services(fixture, js);
        await using var header = new IpRemotePageTests.IpPageRenderer(services, typeof(MainLayout));
        await header.StartAsync();
        await header.AssertDisabledAsync("Switch between Standard and Compact layout", false);
        await header.ClickAsync("Switch between Standard and Compact layout");
        Assert.Equal("samsungLayout.toggle", js.Calls[^1]);
        Assert.DoesNotContain("samsungTheme.toggle", js.Calls);
        await header.ClickAsync("Switch between light and dark mode");
        Assert.Equal("samsungTheme.toggle", js.Calls[^1]);
        await header.ClickAsync("Switch between Standard and Compact layout");
        Assert.Equal(2, js.Calls.Count(call => call == "samsungLayout.toggle"));
        var after = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.Preferences, after.Preferences);
        Assert.Equal(before.Readings, after.Readings);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task MissingLayoutScriptReportsReloadAndLeavesButtonUsable()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await using var services = Services(fixture, new LayoutJs { Fail = true });
        await using var header = new IpRemotePageTests.IpPageRenderer(services, typeof(MainLayout));
        await header.StartAsync();
        await header.ClickAsync("Switch between Standard and Compact layout");
        await header.AssertTextAsync("Refresh the webpage and try again");
        await header.AssertDisabledAsync("Switch between Standard and Compact layout", false);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("white20", 20)]
    [InlineData("color", 6)]
    public async Task DenseRowsReuseAllOriginalAccessibleInputsWithReadbackHints(string section, int count)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        // Avoid racing the initial Picture tab's asynchronous first-render read.
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await using var services = Services(fixture, new LayoutJs());
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.ClickAsync(IpMenuCatalog.Sections.Single(item => item.Id == section).Name);
        var grid = IpMenuGrids.ForSection(section)!;
        Assert.Equal(count, grid.Values.Count);
        await page.AssertClassPresentAsync("compact-indexed-head");
        foreach (var control in grid.Values.SelectMany(grid.Row))
        {
            await page.AssertElementAttributeAsync("input", control.Name + " value", "type", "text");
            await page.AssertElementAttributeAsync("input", control.Name + " value", "aria-invalid", "false");
            await page.AssertInputPresentAsync(control.Name + " slider", true); // Standard still has its sliders.
            await page.AssertAriaButtonPresentAsync("Increase " + control.Name, true);
            await page.AssertAriaButtonPresentAsync("Decrease " + control.Name, true);
        }
        fixture.Display.Requests.Clear();
        var first = grid.Row(grid.Values[0]).First();
        await page.ChangeAsync(first.Name + " value", "-999");
        await page.AssertElementAttributeAsync("input", first.Name + " value", "aria-invalid", "true");
        await page.ChangeAsync(first.Name + " value", "10");
        await page.AssertElementAttributeAsync("input", first.Name + " value", "aria-invalid", "false");
        Assert.Empty(fixture.Display.Requests);
    }

    private static ServiceProvider Services(MenuFixture fixture, IJSRuntime js) => new ServiceCollection().AddLogging()
        .AddSingleton(fixture.Service).AddSingleton(js).AddSingleton<NavigationManager>(new IpMenuTests.MenuNavigation()).BuildServiceProvider();
    private sealed class LayoutJs : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public bool Fail { get; init; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Calls.Add(identifier);
            if (Fail && identifier == "samsungLayout.toggle") throw new JSException("Missing script");
            return ValueTask.FromResult(default(T)!);
        }
    }
}
