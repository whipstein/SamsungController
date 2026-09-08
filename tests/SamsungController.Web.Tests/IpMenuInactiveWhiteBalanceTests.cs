using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuInactiveWhiteBalanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitRefreshReadsInactiveRowsAndRestoresOffWithoutChangingRgb(bool allSettings)
    {
        using var fixture = await CreateAsync();
        fixture.Override = (request, _) => Task.FromResult(RejectInactiveGetter(fixture, request));
        await fixture.Service.ConnectMenuAsync();
        Assert.Empty(fixture.Writes); // Connect/tab visits do not implicitly enable modes.
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        fixture.Display.Requests.Clear();
        if (allSettings) await fixture.Service.RefreshAllMenuSettingsAsync();
        else await fixture.Service.RefreshInactiveWhiteBalanceAsync();
        AssertComplete(fixture);
        foreach (var row in fixture.GridValues.Values)
            foreach (var field in row.Select(pair => pair.Key).ToArray()) row[field] = 13;
        fixture.Display.Requests.Clear();
        if (allSettings) await fixture.Service.RefreshAllMenuSettingsAsync();
        else await fixture.Service.RefreshInactiveWhiteBalanceAsync();
        AssertComplete(fixture);
    }

    [Fact]
    public async Task GridButtonReadsWhileOffKeepsRowsDisabledAndDoesNotRescanOnTabVisits()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("20-point white balance");
        Assert.Empty(fixture.Writes);
        await renderer.ClickAsync("Reload all rows");
        AssertComplete(fixture);
        await renderer.AssertCheckboxAsync("20-point white balance enabled", false);
        await renderer.AssertTargetAsync("5% Red value", 1);
        await renderer.AssertTextAsync("All 20-point values are loaded");
        var count = fixture.Display.Requests.Count;
        await renderer.ClickAsync("Color");
        await renderer.ClickAsync("20-point white balance");
        Assert.Equal(count, fixture.Display.Requests.Count);
        await renderer.AssertTargetAsync("5% Red value", 1);
        // Turning it Off explicitly after an ordinary Apply must not enable it again.
        await renderer.SetCheckboxAsync("20-point white balance enabled", true);
        await renderer.ClickAsync("Apply 1 pending");
        fixture.Display.Requests.Clear();
        await renderer.SetCheckboxAsync("20-point white balance enabled", false);
        await renderer.ClickAsync("Apply 1 pending");
        Assert.Equal("Off", fixture.Values["WB20PointMode"]!.ToString());
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task AlreadyOnRefreshMovesOnlySelectorsAndLeavesModeOn()
    {
        using var fixture = await CreateAsync();
        fixture.Values["WB20PointMode"] = "On";
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshInactiveWhiteBalanceAsync();
        Assert.All(fixture.Writes, request => Assert.Equal("WB20P.IntervalControl", request["method"]!.ToString()));
        Assert.Equal("On", fixture.Values["WB20PointMode"]!.ToString());
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.Equal(60, fixture.Service.GetSnapshot().Menu.IndexedReadings.Count);
        Assert.Null(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("rejected")]
    public async Task UnknownOrUnsupportedModesAreNotGuessedOrEnabled(string failure)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == "WB20PointModeControl"
            ? failure == "rejected" ? MenuFixture.Reject(request, -32002) : ContrastDisplay.Reply(request, new JsonObject { ["WB20PointMode"] = "Unknown" }) : null);
        await fixture.Service.RefreshInactiveWhiteBalanceAsync();
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("rows")]
    public async Task StopSendsNothingElseAndJournalSurvivesRestartUntilExplicitRestoration(string stopAt)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        var stoppedCount = 0;
        fixture.Override = async (request, _) =>
        {
            if (request["params"]?["WB20PointMode"]?.ToString() == "On")
            {
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(Journal(fixture)))!;
                Assert.True(saved["NeedsRestore"]!.GetValue<bool>());
                Assert.Equal("HDMI4", saved["Input"]!.ToString());
                if (stopAt == "enable")
                {
                    fixture.Values["WB20PointMode"] = "On"; // Delivered despite Stop.
                    stoppedCount = fixture.Display.Requests.Count;
                    fixture.Service.Cancel();
                    return ContrastDisplay.Reply(request, new JsonObject { ["WB20PointMode"] = "On" });
                }
            }
            if (stopAt == "rows" && request["method"]!.ToString() == "WB20P.RedControl")
            {
                stoppedCount = fixture.Display.Requests.Count;
                fixture.Service.Cancel();
            }
            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.Service.RefreshAllMenuSettingsAsync);
        Assert.Equal(stoppedCount, fixture.Display.Requests.Count);
        Assert.Equal("On", fixture.Values["WB20PointMode"]!.ToString());
        Assert.True(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendMenuKeyAsync("return"));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.RefreshAllMenuSettingsAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(ContrastFixture.Profile));
        Assert.Equal(stoppedCount, fixture.Display.Requests.Count);
        fixture.Override = null;
        await fixture.RestartAsync();
        Assert.Equal(stoppedCount, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Restore 20-point white balance");
        await renderer.ClickAsync("Restore 20-point WB to Off");
        Assert.False(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Equal("Off", fixture.Values["WB20PointMode"]!.ToString());
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        fixture.AssertOnlyWhiteBalanceReadWrites();
    }

    [Theory]
    [InlineData("reject enable")]
    [InlineData("false enable echo")]
    [InlineData("reject restore")]
    [InlineData("false restore echo")]
    public async Task FailedModeWritesRequireReviewAndDoNotKeepUncertainGrid(string failure)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) =>
        {
            var target = request["params"]?["WB20PointMode"]?.ToString();
            if (target == (failure.Contains("enable", StringComparison.Ordinal) ? "On" : "Off"))
                return Task.FromResult<HttpResponseMessage?>(failure.StartsWith("reject", StringComparison.Ordinal)
                    ? MenuFixture.Reject(request, -32002) : ContrastDisplay.Reply(request, new JsonObject { ["WB20PointMode"] = target }));
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.RefreshAllMenuSettingsAsync);
        Assert.True(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.DoesNotContain(fixture.Writes, request => request["method"]!.ToString() is "WB20P.RedControl" or "WB20P.GreenControl" or "WB20P.BlueControl");
        if (failure.Contains("enable", StringComparison.Ordinal)) Assert.Single(fixture.Writes);
        fixture.Override = null;
        await fixture.Service.RestoreWhiteBalanceReadAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Equal("Off", fixture.Values["WB20PointMode"]!.ToString());
    }

    [Fact]
    public async Task ChangedContextBlocksRestoreUntilOriginalInputReturns()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB20P.RedControl") fixture.Display.Input = "HDMI2";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.RefreshAllMenuSettingsAsync);
        Assert.True(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Equal("On", fixture.Values["WB20PointMode"]!.ToString());
        fixture.Override = null;
        var writes = fixture.Writes.Count();
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.RestoreWhiteBalanceReadAsync);
        Assert.Equal(writes, fixture.Writes.Count());
        fixture.Display.Input = "HDMI4";
        await fixture.Service.RestoreWhiteBalanceReadAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
        fixture.AssertOnlyWhiteBalanceReadWrites();
    }

    [Fact]
    public async Task ManualRecoveryConfirmationSendsNoCommandAndClearsWhiteBalanceCache()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) =>
        {
            if (request["params"]?["WB20PointMode"]?.ToString() == "On") fixture.Service.Cancel();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.Service.RefreshInactiveWhiteBalanceAsync);
        var count = fixture.Display.Requests.Count;
        await fixture.Service.CloseWhiteBalanceReadReviewAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.False(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
        Assert.DoesNotContain("white20", fixture.Service.GetSnapshot().Menu.SectionsRead.Keys);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        await fixture.RestartAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.WhiteBalanceRead!.NeedsRestore);
    }

    [Fact]
    public async Task RemoteKeysKeepAllGridsAndPendingTargetsWithoutAReadButApplyChecksFreshBaseline()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        await fixture.Service.RefreshAllMenuSettingsAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var before = fixture.Service.GetSnapshot().Menu;
        fixture.Display.Requests.Clear();
        fixture.Display.Contrast = 41; // A real remote change is not assumed or polled.
        foreach (var key in new[] { "menu", "return", "enter" }) await fixture.Service.SendMenuKeyAsync(key);
        Assert.Equal(3, fixture.Display.Requests.Count);
        var after = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(before.Readings, after.Readings);
        Assert.Equal(before.IndexedReadings, after.IndexedReadings);
        Assert.Equal(before.GridsRead, after.GridsRead);
        Assert.Equal(before.SectionsRead, after.SectionsRead);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.ValuesRevision, after.ValuesRevision);
        Assert.Equal(before.SettingsLoadedAt, after.SettingsLoadedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(3, fixture.Writes.Count()); // No stale overwrite.
    }

    private static async Task<MenuFixture> CreateAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        fixture.Values["WB20PointMode"] = "Off";
        fixture.Values["WB20P.Interval"] = "50%";
        var grid = IpMenuGrids.ForSection("white20")!;
        for (var index = 0; index < grid.Values.Count; index++)
            fixture.GridValues["white20/" + grid.Values[index]] = new JsonObject { ["WB20P.Red"] = index + 1, ["WB20P.Green"] = -index, ["WB20P.Blue"] = index - 10 };
        return fixture;
    }

    private static HttpResponseMessage? RejectInactiveGetter(MenuFixture fixture, JsonObject request) =>
        fixture.Values["WB20PointMode"]!.ToString() == "Off" && request["method"]!.ToString().StartsWith("WB20P.", StringComparison.Ordinal)
            ? MenuFixture.Reject(request, -32002) : null;

    private static void AssertComplete(MenuFixture fixture)
    {
        fixture.AssertOnlyWhiteBalanceReadWrites();
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.False(menu.WhiteBalanceRead!.NeedsRestore);
        Assert.Equal("50%", menu.WhiteBalanceRead.OriginalInterval);
        Assert.True(menu.GridsRead.ContainsKey("white20"));
        Assert.Equal(60, menu.IndexedReadings.Count);
        foreach (var control in IpMenuCatalog.IndexedControls.Where(control => control.Section == "white20"))
        {
            Assert.Equal(fixture.GridValues["white20/" + control.IndexValue][control.Field]!.GetValue<int>(), menu.Value(control)!.GetValue<int>());
            Assert.NotNull(fixture.Service.MenuControlDisabledReason(control));
        }
    }

    private static string Journal(MenuFixture fixture) => Path.Combine(Path.GetDirectoryName(fixture.JournalPath)!, "white-balance-read.json");
    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
