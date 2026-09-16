using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuPowerTests
{
    [Fact]
    public async Task StandbyShowsTopWarningWithoutCheckButtonOrLatchedControlLock()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["power"] = "powerOff";
        await fixture.Service.ConnectMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.True(menu.Connected);
        Assert.Contains("TV is off", menu.PowerWarning);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("contrastControl/contrast")));
        await using var services = Services(fixture);
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.AssertTextAsync("no manual check or reset is required");
        Assert.DoesNotContain("Check power", await page.ButtonTextsWithinAsync("section", "Menu controls and navigation"));
        await page.AssertAriaButtonPresentAsync("Verify settings", false);
        await page.ChangeAsync("Contrast value", "44", "onchange");
        await page.ClickAsync("Apply 1 pending");
        await page.AssertInputValueAsync("Contrast value", "45");
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Writes);
        fixture.Values["power"] = "powerOn";
        await page.ChangeAsync("Contrast value", "44", "onchange");
        await page.ClickAsync("Apply 1 pending");
        await page.AssertInputValueAsync("Contrast value", "44");
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerWarning);
        Assert.Null(fixture.Service.GetSnapshot().Menu.ActionWarning);
        Assert.Equal("contrastControl", Assert.Single(fixture.Writes)["method"]!.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandbyRevertsAttemptedRowButPreservesOtherPendingEditsAndResumesWithoutManualCheck(bool queryFirst)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).First();
        fixture.Service.StageMenuValue(row.Id, "11");
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        await fixture.Service.SaveMenuPreferencesAsync(false, queryFirst);
        fixture.Values["power"] = "powerOff";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.ApplyMenuRowAsync(grid.Section, grid.Values[0]));
        Assert.Equal(new[] { "powerControl" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        Assert.Equal("contrastControl/contrast", Assert.Single(fixture.Service.GetSnapshot().Menu.Pending).Key);
        Assert.Null(fixture.Service.MenuControlDisabledReason(row));
        fixture.Values["power"] = "powerOn";
        fixture.Service.StageMenuValue(row.Id, "12");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(12, fixture.Value(row.Id)!.GetValue<int>());
        Assert.Equal(44, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("rejected")]
    public async Task UnreportedPowerDoesNotPreventOtherwiseSupportedSettings(string failure)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() != "powerControl" ? null
            : failure == "unsupported" ? MenuFixture.Reject(request, -32601)
            : failure == "rejected" ? MenuFixture.Reject(request, -32002)
            : ContrastDisplay.Reply(request, failure == "missing" ? new JsonObject() : new JsonObject { ["power"] = "unknown" }));
        await fixture.Service.ApplyMenuAsync();
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerWarning);
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task StandbyDropsQueuedAdjustmentsAndRecallCanBeRetriedWithoutReview()
    {
        using var fixture = await ReadyAsync();
        var saved = await fixture.Service.SaveMenuStateAsync("Baseline");
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        fixture.Values["power"] = "powerOff";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.QueueMenuValueAsync("contrastControl/contrast", "44"));
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.NudgeQueue);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        Assert.Null(fixture.Service.RecallMenuStateDisabledReason(fixture.Service.GetSnapshot().SavedStates.Single()));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.RecallMenuStateAsync(saved, true));
        Assert.Null(fixture.Service.GetSnapshot().StateRecall);
        fixture.Values["power"] = "powerOn";
        await fixture.Service.QueueMenuValueAsync("contrastControl/contrast", "44");
        Assert.Equal(44, fixture.Display.Contrast);
        await fixture.Service.RecallMenuStateAsync(saved, true);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task OffRevertsResetTargetsWithoutMovingSelectorsOrLockingTheGrid(string section)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        fixture.Values["power"] = "powerOff";
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.ResetMenuGridAsync(section));
        Assert.Empty(fixture.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update);
        Assert.Null(fixture.Service.MenuControlDisabledReason(grid.Row(grid.Values[0]).First()));
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
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => operation == "inactive white20"
            ? fixture.Service.RefreshInactiveWhiteBalanceAsync() : fixture.Service.RefreshMenuGridAsync(operation));
        Assert.Contains("TV is off", fixture.Service.GetSnapshot().Menu.PowerWarning);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead);
        fixture.Values["power"] = "powerOn";
        await fixture.Service.RefreshAllMenuSettingsAsync();
        Assert.Null(fixture.Service.GetSnapshot().Menu.PowerWarning);
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Display.Requests.Clear();
        return fixture;
    }
}
