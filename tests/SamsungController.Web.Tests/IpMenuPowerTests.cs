using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuPowerTests
{
    [Fact]
    public async Task StandbyConnectionStopsBeforeModeOrSelectorWritesAndOffersReadOnlyPowerCheck()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["power"] = "powerOff";
        await fixture.Service.ConnectMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.True(menu.Connected);
        Assert.Contains("TV is off", menu.Status);
        Assert.Empty(menu.SectionsRead);
        Assert.Null(menu.SettingsLoadedAt);
        Assert.Empty(fixture.Writes);
        Assert.Contains("TV is off", fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("contrastControl/contrast")));

        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        Assert.Contains("Check power", await page.ButtonTextsWithinAsync("section", "Menu controls and navigation"));
        await page.AssertAriaButtonPresentAsync("Verify settings", false);
        fixture.Display.Requests.Clear();
        await page.ClickAsync("Check power");
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        fixture.Values["power"] = "powerOn";
        fixture.Display.Requests.Clear();
        await page.ClickAsync("Check power");
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerDisabledReason);
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        await page.ClickAsync("Refresh section");
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("contrastControl/contrast")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PoweringOffAfterConnectBlocksApplyEvenWithoutValuePreflightAndDoesNotCreateReview(bool queryFirst)
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.SaveMenuPreferencesAsync(false, queryFirst);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Values["power"] = "powerOff";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Contains("TV is off", error.Message);
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        Assert.Single(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Equal(45, fixture.Value("contrastControl/contrast")!.GetValue<int>());

        fixture.Values["power"] = "powerOn";
        await fixture.Service.CheckMenuPowerAsync();
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerDisabledReason);
        fixture.Display.Requests.Clear();
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal("powerControl", fixture.Display.Methods.First());
        Assert.Equal("contrastControl", Assert.Single(fixture.Writes)["method"]!.ToString());
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("rejected")]
    public async Task UnconfirmedPowerNeverAssumesOnOrSendsASetting(string failure)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() != "powerControl" ? null
            : failure == "unsupported" ? MenuFixture.Reject(request, -32601)
            : failure == "rejected" ? MenuFixture.Reject(request, -32002)
            : ContrastDisplay.Reply(request, failure == "missing" ? new JsonObject() : new JsonObject { ["power"] = "unknown" }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Contains("power could not be confirmed", error.Message);
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
    }

    [Fact]
    public async Task StandbyStopsQueuedAdjustmentAndRecallWithoutLeavingUncertainJournals()
    {
        using var fixture = await ReadyAsync();
        var saved = await fixture.Service.SaveMenuStateAsync("Baseline");
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        fixture.Values["power"] = "powerOff";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.QueueMenuValueAsync("contrastControl/contrast", "44").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.NudgeQueue);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        Assert.NotNull(fixture.Service.RecallMenuStateDisabledReason(fixture.Service.GetSnapshot().SavedStates.Single()));
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RecallMenuStateAsync(saved, true));
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Null(fixture.Service.GetSnapshot().StateRecall);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task OffBlocksAllRgbChannelsAndImmediateResetBeforeAnySelectorOrValueWrite(string section)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        foreach (var control in grid.Row(grid.Values[0])) fixture.Service.StageMenuValue(control.Id, "11");
        fixture.Values["power"] = "powerOff";
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);

        fixture.Service.DiscardMenuChanges();
        fixture.Values["power"] = "powerOn";
        await fixture.Service.CheckMenuPowerAsync();
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        fixture.Values["power"] = "powerOff";
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResetMenuGridAsync(section));
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    [InlineData("inactive white20")]
    [InlineData("all")]
    public async Task RefreshWhileOffDoesNotMoveSelectorsOrTemporarilyEnableModes(string operation)
    {
        using var fixture = await ReadyAsync();
        fixture.Values["power"] = "powerOff";
        if (operation == "all") await fixture.Service.RefreshAllMenuSettingsAsync();
        else await Assert.ThrowsAsync<InvalidOperationException>(() => operation == "inactive white20"
            ? fixture.Service.RefreshInactiveWhiteBalanceAsync() : fixture.Service.RefreshMenuGridAsync(operation));
        Assert.Contains("TV is off", fixture.Service.GetSnapshot().Menu.PowerDisabledReason);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.SelectorSession);
        Assert.Null(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        fixture.Values["power"] = "powerOn";
        await fixture.Service.RefreshAllMenuSettingsAsync();
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerDisabledReason);
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
    }

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Display.Requests.Clear();
        return fixture;
    }
}
