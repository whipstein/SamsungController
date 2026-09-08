using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuLayoutTests
{
    private const string Gamma = "gammaModeControl/gammaMode", Contrast = "contrastControl/contrast", Color = "colorControl/color";

    [Fact]
    public void EveryExpertControlAppearsExactlyOnceAndAllLocalDependenciesStayTogether()
    {
        var grouped = IpExpertLayout.Groups.SelectMany(group => group.Controls).ToArray();
        Assert.Equal(IpMenuCatalog.ForSection("expert").Select(control => control.Id).Order(), grouped.Select(control => control.Id).Order());
        Assert.All(grouped, control => Assert.Equal("expert", control.Section));
        Assert.Equal(4, IpExpertLayout.Groups.Single(group => group.Id == Gamma).Controls.Count);
        Assert.Equal(4, IpExpertLayout.Groups.Single(group => group.Id == "autoMotionPlusControl/autoMotionPlus").Controls.Count);
        foreach (var group in IpExpertLayout.Groups)
            foreach (var control in group.Controls)
                foreach (var requirement in control.Command.Requirements)
                    foreach (var parent in grouped.Where(item => item.Method == requirement.Method && item.Field == requirement.Field))
                        Assert.Contains(parent, group.Controls);
        Assert.Single(IpExpertLayout.Groups.Single(group => group.Id == Contrast).Controls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovesInsertOnEitherSideInBothDirections(bool after)
    {
        var order = IpExpertLayout.Move(null, Gamma, Contrast, after).ToList();
        Assert.Equal(order.IndexOf(Contrast) + (after ? 1 : -1), order.IndexOf(Gamma));
        order = IpExpertLayout.Move(order, Contrast, Gamma, after).ToList();
        Assert.Equal(order.IndexOf(Gamma) + (after ? 1 : -1), order.IndexOf(Contrast));
        Assert.Equal(IpExpertLayout.Groups.Count, order.Count);
        Assert.Equal(order, IpExpertLayout.Move(order, Gamma, Gamma, after));
    }

    [Fact]
    public void NormalizationRetainsSavedOrderDropsStaleDuplicatesAndAppendsNewGroups()
    {
        var order = IpExpertLayout.Normalize([Gamma, "removed-setting", Gamma, Contrast, null!]);
        Assert.Equal(new[] { Gamma, Contrast }, order.Take(2));
        Assert.Equal(IpExpertLayout.Groups.Count, order.Count);
        Assert.Equal(IpExpertLayout.Groups.Select(group => group.Id).Order(), order.Order());
        Assert.Equal(IpExpertLayout.Groups.Select(group => group.Id), IpExpertLayout.Normalize(null));
    }

    [Fact]
    public async Task LayoutPersistsOfflineAcrossRestartAndApplyPreferenceChangesWithoutTvTraffic()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.MoveExpertGroupAsync(Gamma, Contrast, false);
        var order = fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder;
        await fixture.Service.SaveMenuPreferencesAsync(true);
        Assert.Equal(order, fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder);
        await fixture.RestartAsync();
        Assert.Equal(order, fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder);
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
        await fixture.Service.ResetExpertLayoutAsync();
        Assert.Equal(IpExpertLayout.Normalize(null), fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder);
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
        await fixture.RestartAsync();
        Assert.Equal(IpExpertLayout.Normalize(null), fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("gamma.BT1886Control/gamma.BT1886")]
    [InlineData("WB2PointControl/R-Gain")]
    [InlineData("made-up-control")]
    public async Task CannotSplitLinkedGroupsOrMoveControlsFromOtherTabs(string invalid)
    {
        using var fixture = await MenuFixture.CreateAsync();
        var original = fixture.Service.GetSnapshot().Menu.Preferences;
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.MoveExpertGroupAsync(invalid, Contrast, false));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.MoveExpertGroupAsync(Contrast, invalid, true));
        Assert.Equal(original, fixture.Service.GetSnapshot().Menu.Preferences);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task LoadingOlderOrStaleLayoutPreferencesKeepsApplyPreference()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var path = Path.Combine(Path.GetDirectoryName(fixture.JournalPath)!, "menu-preferences.json");
        await File.WriteAllTextAsync(path, """{"ApplyImmediately":true,"ExpertGroupOrder":["removed","gammaModeControl/gammaMode","gammaModeControl/gammaMode"]}""");
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
        Assert.Equal(Gamma, fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder[0]);
        Assert.Equal(IpExpertLayout.Groups.Count, fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder.Count);
        await File.WriteAllTextAsync(path, """{"ApplyImmediately":true}""");
        await fixture.RestartAsync();
        Assert.Equal(IpExpertLayout.Normalize(null), fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder);
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
    }

    [Fact]
    public async Task WholeBoxDragCallbacksReorderRenderedGroupsKeepEditsAndSendNothing()
    {
        using var fixture = await ReadyAsync();
        var js = new LayoutJavaScript();
        await using var services = Services(fixture, js);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertExpertGroupsDraggableAsync();
        await renderer.AssertClassPresentAsync("direct-layout-handlebar", false);
        await renderer.AssertClassPresentAsync("direct-layout-handle", false);
        await renderer.AssertClassPresentAsync("direct-layout-arrows", false);
        await renderer.ChangeAsync("Contrast value", "44", "onchange");
        var before = fixture.Service.GetSnapshot().Menu;
        var requests = fixture.Display.Requests.Count;
        Assert.NotNull(js.Page);
        await renderer.Dispatcher.InvokeAsync(async () => Assert.True(await js.Page!.MoveMenuGroupAsync("expert", Gamma, Contrast, false)));
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
        await renderer.AssertExpertGroupTextAsync(Gamma, "Gamma mode", "BT.1886 adjustment", "ST.2084 adjustment", "HLG adjustment");
        await renderer.AssertTargetAsync("Contrast value", 44);
        await renderer.Dispatcher.InvokeAsync(async () => Assert.True(await js.Page!.MoveMenuGroupAsync("expert", Gamma, Contrast, true)));
        var order = fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder.ToList();
        Assert.Equal(order.IndexOf(Contrast) + 1, order.IndexOf(Gamma));
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
        await renderer.ClickAsync("Reset layout");
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
        await renderer.AssertTextAsync("TV settings and pending edits are unchanged");
        var after = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(before.Readings, after.Readings);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.ValuesRevision, after.ValuesRevision);
        Assert.Equal(before.SectionsRead, after.SectionsRead);
        Assert.Equal(requests, fixture.Display.Requests.Count);
        await renderer.ClickAsync("2-point white balance");
        await renderer.AssertTextAbsentAsync("Reset layout");
        await renderer.ClickAsync("Picture");
        Assert.Equal(2, js.AttachCalls);
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
    }

    [Fact]
    public async Task HiddenGroupKeepsItsPositionWhenItReturns()
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.MoveExpertGroupAsync(Gamma, Contrast, false);
        var gamma = IpExpertLayout.Groups.Single(group => group.Id == Gamma);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(gamma.Controls.Any(control => control.Method == request["method"]!.ToString()) ? MenuFixture.Reject(request, -32601) : null);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await using var services = Services(fixture, new LayoutJavaScript());
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        Assert.DoesNotContain(Gamma, VisibleOrder(fixture));
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
        await fixture.Service.MoveExpertGroupAsync(Color, Contrast, true);
        fixture.Override = null;
        await renderer.ClickAsync("Refresh section");
        var visible = VisibleOrder(fixture).ToList();
        Assert.Equal(visible.IndexOf(Contrast) - 1, visible.IndexOf(Gamma));
        await renderer.AssertExpertGroupOrderAsync(visible);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task FailedSaveRetainsOrderAndShowsInlineError()
    {
        using var fixture = await ReadyAsync();
        var original = fixture.Service.GetSnapshot().Menu.Preferences;
        var js = new LayoutJavaScript();
        await using var services = Services(fixture, js);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        // Occupy the scratch filename with a directory in the temporary fixture.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.JournalPath)!, "menu-preferences.json.tmp"));
        var requests = fixture.Display.Requests.Count;
        await renderer.Dispatcher.InvokeAsync(async () => Assert.False(await js.Page!.MoveMenuGroupAsync("expert", Gamma, Contrast, false)));
        Assert.Equal(original, fixture.Service.GetSnapshot().Menu.Preferences);
        await renderer.AssertTextAsync("Layout was not saved:");
        await renderer.AssertExpertGroupOrderAsync(VisibleOrder(fixture));
        Assert.Equal(requests, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task MissingDragScriptShowsReloadGuidanceWithoutRepeatedAttachAttempts()
    {
        using var fixture = await ReadyAsync();
        var js = new LayoutJavaScript { FailAttach = true };
        await using var services = Services(fixture, js);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Box dragging could not load");
        await renderer.ClickAsync("Reset layout");
        await renderer.AssertTextAsync("Default layout restored");
        Assert.Equal(1, js.AttachCalls);
        Assert.Empty(fixture.Writes);
    }

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        fixture.Values["gammaMode"] = "BT.1886";
        await fixture.Service.ConnectMenuAsync();
        return fixture;
    }
    private static IEnumerable<string> VisibleOrder(MenuFixture fixture) => IpExpertLayout.Ordered(fixture.Service.GetSnapshot().Menu.Preferences.ExpertGroupOrder)
        .Where(group => group.Controls.Any(control => IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, control).Visible)).Select(group => group.Id);
    private static ServiceProvider Services(MenuFixture fixture, IJSRuntime js) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton(js).BuildServiceProvider();
    private sealed class LayoutJavaScript : IJSRuntime
    {
        public DirectMenu? Page { get; private set; }
        public int AttachCalls { get; private set; }
        public bool FailAttach { get; init; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "samsungExpertLayout.attach")
            {
                AttachCalls++;
                Page = ((DotNetObjectReference<DirectMenu>)args![1]!).Value;
                if (FailAttach) throw new JSException("simulated script load failure");
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
