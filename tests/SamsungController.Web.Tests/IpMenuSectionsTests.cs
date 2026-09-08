using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuSectionsTests
{
    [Theory]
    [InlineData("sound")]
    [InlineData("system")]
    public void AllControlsAppearOnceAndLocalDependenciesMoveTogether(string section)
    {
        var groups = IpExpertLayout.ForSection(section);
        var controls = groups.SelectMany(group => group.Controls).ToArray();
        Assert.Equal(IpMenuCatalog.ForSection(section).Select(control => control.Id).Order(), controls.Select(control => control.Id).Order());
        Assert.All(controls, control => Assert.Equal(section, control.Section));
        foreach (var group in groups)
            foreach (var control in group.Controls)
                foreach (var requirement in control.Command.Requirements)
                    foreach (var parent in controls.Where(item => item.Method == requirement.Method && item.Field == requirement.Field))
                        Assert.Contains(parent, group.Controls);
        var moved = IpExpertLayout.Move(null, groups[^1].Id, groups[0].Id, false, section);
        Assert.Equal(groups[^1].Id, moved[0]);
        Assert.Equal(groups.Count, moved.Distinct().Count());
        var normalized = IpExpertLayout.Normalize([groups[^1].Id, "obsolete", groups[^1].Id, null!], section);
        Assert.Equal(moved, normalized);
    }

    [Fact]
    public async Task ThreeLayoutsPersistIndependentlyAndResetOnlyTheChosenSection()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var saved = new Dictionary<string, string[]>();
        foreach (var section in new[] { "expert", "sound", "system" })
        {
            var groups = IpExpertLayout.ForSection(section);
            await fixture.Service.MoveMenuGroupAsync(section, groups[^1].Id, groups[0].Id, false);
            saved[section] = fixture.Service.GetSnapshot().Menu.Preferences.GroupOrder(section).ToArray();
            Assert.Equal(groups[^1].Id, saved[section][0]);
        }
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        await fixture.RestartAsync();
        foreach (var section in saved.Keys) Assert.Equal(saved[section], fixture.Service.GetSnapshot().Menu.Preferences.GroupOrder(section));
        await fixture.Service.ResetMenuLayoutAsync("sound");
        await fixture.RestartAsync();
        var preferences = fixture.Service.GetSnapshot().Menu.Preferences;
        Assert.Equal(saved["expert"], preferences.ExpertGroupOrder);
        Assert.Equal(saved["system"], preferences.SystemGroupOrder);
        Assert.Equal(IpExpertLayout.Normalize(null, "sound"), preferences.SoundGroupOrder);
        Assert.True(preferences.ApplyImmediately);
        Assert.False(preferences.QueryBeforeChange);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("sound")]
    [InlineData("system")]
    [InlineData("white2")]
    public async Task RejectsCrossSectionMovesWithoutChangingPreferences(string section)
    {
        using var fixture = await MenuFixture.CreateAsync();
        var preferences = fixture.Service.GetSnapshot().Menu.Preferences;
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.MoveMenuGroupAsync(section,
            "contrastControl/contrast", "gammaModeControl/gammaMode", false));
        Assert.Equal(preferences, fixture.Service.GetSnapshot().Menu.Preferences);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task SixPeerTabsAndRefreshActionsStayInOnePinnedToolbar()
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture, new LayoutJavaScript());
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        Assert.Equal(new[] { "Picture", "2pt WB", "20pt WB", "Color", "Sound", "System" },
            await renderer.ButtonTextsWithinAsync("nav", "Top-level settings"));
        await renderer.AssertAriaButtonPresentAsync("Expert settings", false);
        var toolbar = await renderer.ButtonTextsWithinAsync("section", "Menu controls and navigation");
        Assert.Contains("Refresh section", toolbar);
        Assert.Contains("Reload all rows", toolbar);
        Assert.Contains("Apply (0)", toolbar);
        Assert.DoesNotContain("Refresh TV state", toolbar);
        foreach (var section in IpMenuCatalog.Sections)
        {
            await renderer.ClickAsync(section.Name);
            await renderer.AssertElementAttributeAsync("button", section.Name, "aria-pressed", "true");
            await renderer.AssertDisabledAsync("Refresh section", false);
            await renderer.AssertDisabledAsync("Reload all rows", IpMenuGrids.ForSection(section.Id) is null);
        }
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("sound")]
    [InlineData("system")]
    public async Task WholeCardReorderKeepsPendingValuesAndRejectsStaleCallbacks(string section)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var js = new LayoutJavaScript();
        await using var services = Services(fixture, js);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync(IpMenuCatalog.Sections.Single(item => item.Id == section).Name);
        await renderer.AssertExpertGroupsDraggableAsync();
        var visible = VisibleOrder(fixture, section).ToArray();
        Assert.True(visible.Length > 1);
        var before = fixture.Service.GetSnapshot().Menu;
        await renderer.Dispatcher.InvokeAsync(async () => Assert.True(await js.Page!.MoveMenuGroupAsync(section, visible[^1], visible[0], false)));
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture, section));
        Assert.Equal(visible[^1], VisibleOrder(fixture, section).First());
        Assert.Equal(before.Pending, fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Equal(before.Readings, fixture.Service.GetSnapshot().Menu.Readings);
        Assert.Equal(before.ValuesRevision, fixture.Service.GetSnapshot().Menu.ValuesRevision);

        await renderer.ClickAsync("Picture");
        var saved = fixture.Service.GetSnapshot().Menu.Preferences;
        await renderer.Dispatcher.InvokeAsync(async () => Assert.False(await js.Page!.MoveMenuGroupAsync(section, visible[0], visible[^1], false)));
        Assert.Equal(saved, fixture.Service.GetSnapshot().Menu.Preferences);
        await renderer.ClickAsync(IpMenuCatalog.Sections.Single(item => item.Id == section).Name);
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture, section));
        await renderer.ClickAsync("Reset layout");
        Assert.Equal(IpExpertLayout.Normalize(null, section), fixture.Service.GetSnapshot().Menu.Preferences.GroupOrder(section));
        Assert.Equal(before.Pending, fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Contains("samsungExpertLayout.detach", js.Calls);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task HeaderRefreshUpdatesTheOpenMenuAndClearsUnsentRawEdits()
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture, new LayoutJavaScript());
        await using var menu = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await using var header = new IpRemotePageTests.IpPageRenderer(services, typeof(MainLayout));
        await menu.StartAsync();
        await header.StartAsync();
        Assert.Equal(new[] { "Home", "Back", "Exit menu", "Refresh TV state" }, await header.ButtonTextsWithinAsync("div", "TV shortcuts"));
        await menu.ChangeAsync("Contrast value", "40");
        await menu.AssertInputValueAsync("Contrast value", "40");
        fixture.Display.Contrast = 42;
        await header.ClickAsync("Refresh TV state");
        await menu.AssertInputValueAsync("Contrast value", "42");
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        fixture.AssertOnlyWhiteBalanceReadWrites();
        await fixture.Service.DisconnectMenuAsync();
        await header.AssertDisabledAsync("Refresh TV state", true);
        await menu.AssertDisabledAsync("Refresh section", true);
        await menu.AssertDisabledAsync("Reload all rows", true);
    }

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Display.Requests.Clear();
        return fixture;
    }
    private static IEnumerable<string> VisibleOrder(MenuFixture fixture, string section) =>
        IpExpertLayout.Ordered(fixture.Service.GetSnapshot().Menu.Preferences.GroupOrder(section), section)
            .Where(group => group.Controls.Any(control => IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, control).Visible)).Select(group => group.Id);
    private static ServiceProvider Services(MenuFixture fixture, IJSRuntime js) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton(js).AddSingleton<NavigationManager>(new IpMenuTests.MenuNavigation()).BuildServiceProvider();
    private sealed class LayoutJavaScript : IJSRuntime
    {
        public DirectMenu? Page { get; private set; }
        public List<string> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "samsungExpertLayout.attach") Page = ((DotNetObjectReference<DirectMenu>)args![1]!).Value;
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
