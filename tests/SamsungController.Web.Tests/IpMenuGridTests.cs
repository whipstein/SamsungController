using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuGridTests
{
    [Theory]
    [InlineData("colorSpaceControl/colorSpace", "Auto", "color")]
    [InlineData("colorSpace.ColorAdjustmentPointControl/colorSpace.ColorAdjustmentPoint", "75%", "color")]
    [InlineData("WB20PointModeControl/WB20PointMode", "Off", "white20")]
    [InlineData("gammaModeControl/gammaMode", "2.2", "expert")]
    [InlineData("autoMotionPlusControl/autoMotionPlus", "Custom", "expert")]
    public async Task LocalModesInvalidateOnlyTheirOwnSection(string controlId, string target, string section)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuSectionAsync("white2");
        await fixture.Service.RefreshMenuGridAsync("white20");
        await fixture.Service.RefreshMenuGridAsync("color");
        var before = fixture.Service.GetSnapshot().Menu;
        fixture.Service.StageMenuValue(controlId, target);
        await fixture.Service.ApplyMenuAsync();
        var after = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(target, after.Value(IpMenuCatalog.Get(controlId))!.ToString());
        Assert.False(after.SectionsRead.ContainsKey(section));
        foreach (var other in new[] { "expert", "white2", "white20", "color" }.Where(item => item != section))
        {
            Assert.Equal(before.SectionsRead[other], after.SectionsRead[other]);
            foreach (var control in IpMenuCatalog.ForSection(other).Where(control => !control.IsIndexed && control.Command.ReadbackMethod is not ("getTVStates" or "getVideoStates")))
                Assert.Equal(before.Readings.GetValueOrDefault(control.Method), after.Readings.GetValueOrDefault(control.Method));
        }
        foreach (var grid in IpMenuGrids.All)
        {
            Assert.Equal(grid.Section != section, after.GridsRead.ContainsKey(grid.Section));
            foreach (var control in grid.Values.SelectMany(grid.Row))
            {
                if (grid.Section == section) Assert.Null(after.Value(control));
                else
                {
                    Assert.Equal(before.IndexedReadings[control.Id], after.IndexedReadings[control.Id]);
                    Assert.Null(fixture.Service.MenuControlDisabledReason(control));
                }
            }
        }
    }

    [Theory]
    [InlineData("pictureModeControl/pictureMode", "Movie")]
    [InlineData("inputSourceControl/inputSource", "HDMI1")]
    public async Task GlobalContextChangesStillInvalidateBothGrids(string controlId, string target)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuGridAsync("white20");
        await fixture.Service.RefreshMenuGridAsync("color");
        fixture.Service.StageMenuValue(controlId, target);
        await fixture.Service.ApplyMenuAsync();
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.GridsRead);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.SectionsRead);
    }

    [Fact]
    public async Task ChoosingCustomColorKeepsWhiteBalanceKnownAndSwitchingBackDoesNotRescan()
    {
        using var fixture = await CreateAsync();
        fixture.Values["colorSpace"] = "Auto";
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuSectionAsync("white2");
        await fixture.Service.RefreshMenuGridAsync("white20");
        await fixture.Service.RefreshMenuGridAsync("color");
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertSliderBoundsAsync("Contrast", "0", "50");
        await renderer.ClickAsync("20-point white balance");
        var before = fixture.Service.GetSnapshot().Menu;
        await renderer.AssertSliderBoundsAsync("5% Red", "-50", "50");
        await renderer.ClickAsync("Color");
        fixture.Display.Requests.Clear();
        await renderer.SelectAsync("Color space", "Custom");
        await renderer.ClickAsync("Apply 1 pending");
        Assert.True(fixture.Service.GetSnapshot().Menu.GridsRead.ContainsKey("color"));
        Assert.DoesNotContain(fixture.Display.Methods, method => method.StartsWith("WB", StringComparison.Ordinal));
        await renderer.AssertSliderBoundsAsync("Red Red", "0", "100");
        var requests = fixture.Display.Requests.Count;
        await renderer.ClickAsync("20-point white balance");
        Assert.Equal(requests, fixture.Display.Requests.Count);
        await renderer.AssertCheckboxAsync("20-point white balance enabled", true);
        foreach (var control in IpMenuCatalog.IndexedControls.Where(control => control.Section == "white20"))
            await renderer.AssertTargetAsync(control.Name + " value", before.Value(control)!.GetValue<int>());
        await renderer.ClickAsync("2-point white balance");
        Assert.Equal(requests, fixture.Display.Requests.Count);
        await renderer.AssertTargetAsync("R Gain value", before.Value(IpMenuCatalog.Get("WB2PointControl/R-Gain"))!.GetValue<int>());
    }

    [Theory]
    [InlineData("white20", 146)]
    [InlineData("color", 59)]
    public async Task GridReadUsesOneRgbQueryPerCellAndSharesContextChecksBetweenRows(string section, int expectedRequests)
    {
        using var fixture = await CreateAsync();
        fixture.Display.Requests.Clear();
        await fixture.Service.RefreshMenuGridAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        // Previously 176 / 67 with full context after every row. Bounded
        // four-row groups retain independent selector checks on every row.
        Assert.Equal(expectedRequests, fixture.Display.Requests.Count);
        foreach (var field in grid.Fields)
            Assert.Equal(grid.Values.Count, fixture.Display.Methods.Count(method => method == field + "Control"));
        Assert.All(fixture.Writes, request => Assert.Equal(grid.SelectorMethod, request["method"]!.ToString()));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("picture")]
    [InlineData("mode")]
    public async Task ContextChangeDuringRgbReadStopsAtTheNextGroupBoundaryWithoutPublishingOrRestoring(string change)
    {
        using var fixture = await CreateAsync();
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB20P.RedControl")
            {
                if (change == "input") fixture.Display.Input = "HDMI2";
                else if (change == "picture") fixture.Display.Mode = "Standard";
                else fixture.Values["WB20PointMode"] = "Off";
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshMenuGridAsync("white20"));
        Assert.Equal(4, fixture.Writes.Count()); // Four read-only rows at most before the next context check.
        Assert.Empty(RgbWrites(fixture));
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.False(fixture.Service.GetSnapshot().Menu.GridsRead.ContainsKey("white20"));
        Assert.Equal("Stopped", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
    }

    [Fact]
    public async Task SelectorAcknowledgmentAloneDoesNotAllowReadingAnIncorrectRow()
    {
        using var fixture = await CreateAsync();
        fixture.Display.Requests.Clear();
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request["method"]!.ToString() == "WB20P.IntervalControl" && request["params"]!.AsObject().Count > 1
                ? new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject { ["WB20P.Interval"] = "5%" } }.ToJsonString()) }
                : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshMenuGridAsync("white20"));
        Assert.DoesNotContain("WB20P.RedControl", fixture.Display.Methods);
        Assert.Single(fixture.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
    }

    [Theory]
    [InlineData("white20", 20, "50%")]
    [InlineData("color", 6, "Blue")]
    public async Task LoadsEveryIndependentRgbRowWithoutChangingValuesAndRestoresSelector(string section, int count, string original)
    {
        using var fixture = await CreateAsync();
        var before = fixture.GridValues.ToDictionary(pair => pair.Key, pair => pair.Value.ToJsonString());
        await fixture.Service.RefreshMenuGridAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        Assert.Equal(count, grid.Values.Count);
        Assert.Equal(original, fixture.Values[grid.SelectorField]!.ToString());
        Assert.Equal("Restored", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
        Assert.Equal(count * 3, fixture.Service.GetSnapshot().Menu.IndexedReadings.Count);
        foreach (var value in grid.Values)
            foreach (var control in grid.Row(value))
            {
                Assert.Equal(fixture.GridValues[section + "/" + value][control.Field]!.ToString(), fixture.Service.GetSnapshot().Menu.Value(control)!.ToString());
                Assert.Null(fixture.Service.MenuControlDisabledReason(control));
            }
        Assert.All(fixture.GridValues, pair => Assert.Equal(before[pair.Key], pair.Value.ToJsonString()));
        Assert.All(fixture.Writes, request => Assert.Equal(grid.SelectorMethod, request["method"]!.ToString()));
        Assert.DoesNotContain("remoteKeyControl", fixture.Display.Methods);
        Assert.True(fixture.Service.GetSnapshot().Menu.GridsRead.ContainsKey(section));
    }

    [Theory]
    [InlineData("white20", "Off")]
    [InlineData("color", "Auto")]
    public async Task DisabledModeNeverGetsEnabledByLoadingGrid(string section, string mode)
    {
        using var fixture = await CreateAsync();
        var grid = IpMenuGrids.ForSection(section)!;
        fixture.Values[grid.ModeField] = mode;
        await fixture.Service.RefreshMenuGridAsync(section);
        Assert.Empty(fixture.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.False(fixture.Service.GetSnapshot().Menu.GridsRead.ContainsKey(section));
        Assert.All(grid.Values.SelectMany(grid.Row), control => Assert.NotNull(fixture.Service.MenuControlDisabledReason(control)));
    }

    [Theory]
    [InlineData("white20", "10%", "90%")]
    [InlineData("color", "Red", "Magenta")]
    public async Task BatchTargetsRowsIndependentlyAndSharesSelectorAcrossRgbChannels(string section, string first, string second)
    {
        using var fixture = await CreateAsync();
        var grid = IpMenuGrids.ForSection(section)!;
        await fixture.Service.RefreshMenuGridAsync(section);
        fixture.Display.Requests.Clear();
        var red = grid.Row(first).First();
        var green = grid.Row(first).Skip(1).First();
        var blue = grid.Row(second).Last();
        var untouched = fixture.GridValues[section + "/" + second][grid.Fields[0]]!.ToString();
        fixture.Service.StageMenuValue(red.Id, "11");
        fixture.Service.StageMenuValue(blue.Id, "12"); // Interleaved edits are grouped by row when applied.
        fixture.Service.StageMenuValue(green.Id, "13");
        Assert.Empty(fixture.Writes);
        Assert.Equal(3, fixture.Service.GetSnapshot().Menu.Pending.Count);
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(11, fixture.GridValues[section + "/" + first][red.Field]!.GetValue<int>());
        Assert.Equal(13, fixture.GridValues[section + "/" + first][green.Field]!.GetValue<int>());
        Assert.Equal(12, fixture.GridValues[section + "/" + second][blue.Field]!.GetValue<int>());
        Assert.Equal(untouched, fixture.GridValues[section + "/" + second][red.Field]!.ToString());
        Assert.Equal(second, fixture.Values[grid.SelectorField]!.ToString());
        Assert.Equal(new[] { first, second }, fixture.Writes.Where(request => request["method"]!.ToString() == grid.SelectorMethod)
            .Select(request => request["params"]![grid.SelectorField]!.ToString()));
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Equal("Completed", fixture.Service.GetSnapshot().Menu.Update!.Status);
        Assert.All(fixture.Service.GetSnapshot().Menu.Update!.Steps, step => Assert.Equal("Applied", step.Status));
        Assert.Equal(11, fixture.Service.GetSnapshot().Menu.Value(red)!.GetValue<int>());
        var requests = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(requests, fixture.Display.Requests.Count);
        Assert.Equal(3, fixture.Service.GetSnapshot().Menu.Update!.Steps.Count);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
    }

    [Fact]
    public async Task ApplyingOnePercentagePreservesOtherRowsAndOrdinaryPendingSettings()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuGridAsync("white20");
        fixture.Display.Requests.Clear();
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row("5%").ToArray();
        var other = grid.Row("95%").Last();
        foreach (var control in row) fixture.Service.StageMenuValue(control.Id, "11");
        fixture.Service.StageMenuValue(row[0].Id, "13"); // Replaces the earlier staged target.
        fixture.Service.StageMenuValue(other.Id, "12");
        fixture.Service.StageMenuValue("contrastControl/contrast", "46");
        var expectedRemaining = fixture.Service.GetSnapshot().Menu.Pending.Where(pair => !row.Any(control => control.Id == pair.Key)).ToDictionary();
        await fixture.Service.ApplyMenuRowAsync("white20", "5%");
        Assert.Equal(new[] { 13, 11, 11 }, row.Select(control => fixture.GridValues["white20/5%"][control.Field]!.GetValue<int>()));
        Assert.Equal(expectedRemaining, fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Equal("5%", fixture.Values[grid.SelectorField]!.ToString());
        Assert.Equal(new[] { "5%" }, fixture.Writes.Where(write => write["method"]!.ToString() == grid.SelectorMethod)
            .Select(write => write["params"]![grid.SelectorField]!.ToString()));
        Assert.Equal(3, RgbWrites(fixture).Count());
        Assert.DoesNotContain(fixture.Writes, write => write["method"]!.ToString() == "contrastControl");
        Assert.Equal(row.Select(control => control.Id), fixture.Service.GetSnapshot().Menu.Update!.Steps.Select(step => step.ControlId));
        var count = fixture.Display.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyMenuRowAsync("white20", "5%"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ApplyMenuRowAsync("white20", "7%"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ApplyMenuRowAsync("expert", "5%"));
        Assert.Equal(count, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task PercentageApplyButtonsOnlyShowInWaitModeAndApplyOnlyTheirOwnBlock()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuGridAsync("white20");
        fixture.Display.Requests.Clear();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("20-point white balance");
        foreach (var value in IpMenuGrids.ForSection("white20")!.Values)
            await renderer.AssertElementDisabledAsync("button", $"Apply {value} white balance", true);
        await renderer.ChangeAsync("5% Red value", "11", "onchange");
        await renderer.ChangeAsync("5% Green value", "12", "onchange");
        await renderer.ChangeAsync("95% Blue value", "13", "onchange");
        await renderer.AssertElementDisabledAsync("button", "Apply 5% white balance", false);
        await renderer.AssertElementDisabledAsync("button", "Apply 95% white balance", false);
        await renderer.ClickAriaButtonAsync("Apply 5% white balance");
        Assert.Equal(11, fixture.GridValues["white20/5%"]["WB20P.Red"]!.GetValue<int>());
        Assert.Equal(12, fixture.GridValues["white20/5%"]["WB20P.Green"]!.GetValue<int>());
        Assert.Equal("95% Blue", IpMenuCatalog.Get(Assert.Single(fixture.Service.GetSnapshot().Menu.Pending).Key).Name);
        await renderer.AssertElementDisabledAsync("button", "Apply 5% white balance", true);
        await renderer.AssertElementDisabledAsync("button", "Apply 95% white balance", false);
        await renderer.ClickAriaButtonAsync("Apply 95% white balance");
        await renderer.SetCheckboxAsync("Apply settings immediately", true);
        foreach (var value in IpMenuGrids.ForSection("white20")!.Values)
            await renderer.AssertAriaButtonPresentAsync($"Apply {value} white balance", false);
        await renderer.SetCheckboxAsync("Apply settings immediately", false);
        await renderer.AssertAriaButtonPresentAsync("Apply 5% white balance", true);
    }

    [Fact]
    public async Task CombinedColorAndWhiteBalanceBatchRetainsBothLastSelectors()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuGridAsync("white20");
        await fixture.Service.RefreshMenuGridAsync("color");
        var wb = IpMenuGrids.ForSection("white20")!.Row("5%").First();
        var color = IpMenuGrids.ForSection("color")!.Row("Yellow").Last();
        fixture.Service.StageMenuValue(wb.Id, "7");
        fixture.Service.StageMenuValue(color.Id, "8");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal("5%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.Equal("Yellow", fixture.Values["colorSpace.Color"]!.ToString());
        Assert.Equal(7, fixture.GridValues["white20/5%"][wb.Field]!.GetValue<int>());
        Assert.Equal(8, fixture.GridValues["color/Yellow"][color.Field]!.GetValue<int>());
    }

    [Theory]
    [InlineData("selector rejection")]
    [InlineData("false selector echo")]
    [InlineData("mode changed")]
    public async Task FailedRowSelectionNeverSendsAnRgbWrite(string failure)
    {
        using var fixture = await CreateAsync();
        var grid = IpMenuGrids.ForSection("white20")!;
        await fixture.Service.RefreshMenuGridAsync(grid.Section);
        fixture.Service.StageMenuValue(grid.Row("10%").First().Id, "11");
        fixture.Display.Requests.Clear();
        if (failure == "mode changed") fixture.Values[grid.ModeField] = "Off";
        else fixture.Override = (request, _) => Task.FromResult(request["method"]!.ToString() == grid.SelectorMethod && request["params"]!.AsObject().Count > 1
            ? failure == "selector rejection" ? MenuFixture.Reject(request, -32002) : ContrastDisplay.Reply(request, new JsonObject { [grid.SelectorField] = "10%" }) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(RgbWrites(fixture));
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Equal("Not sent", fixture.Service.GetSnapshot().Menu.Update!.Steps[0].Status);
    }

    [Fact]
    public async Task StaleRowValueIsReadAgainBeforeOverwriteAndDoesNotOverwriteOtherRows()
    {
        using var fixture = await CreateAsync();
        var grid = IpMenuGrids.ForSection("white20")!;
        await fixture.Service.RefreshMenuGridAsync(grid.Section);
        var cell = grid.Row("15%").First();
        fixture.Service.StageMenuValue(cell.Id, "11");
        fixture.GridValues["white20/15%"][cell.Field] = 6;
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(RgbWrites(fixture));
        Assert.Equal(6, fixture.GridValues["white20/15%"][cell.Field]!.GetValue<int>());
    }

    [Fact]
    public async Task CancelDuringScanNeverRestoresOrResumesAutomatically()
    {
        using var fixture = await CreateAsync();
        fixture.Override = (request, cancellation) =>
        {
            if (request["method"]!.ToString() == "WB20P.IntervalControl" && request["params"]!.AsObject().Count > 1)
            {
                fixture.Service.Cancel();
                cancellation.ThrowIfCancellationRequested();
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.RefreshMenuGridAsync("white20"));
        Assert.Single(fixture.Writes);
        Assert.Empty(RgbWrites(fixture));
        Assert.Equal("Stopped", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        var requests = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(requests, fixture.Display.Requests.Count);
        Assert.Equal("Stopped", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
    }

    [Fact]
    public async Task StopDuringIndexedWriteStopsLaterRowsAndRetainsBothJournals()
    {
        using var fixture = await CreateAsync();
        var grid = IpMenuGrids.ForSection("color")!;
        await fixture.Service.RefreshMenuGridAsync(grid.Section);
        fixture.Service.StageMenuValue(grid.Row("Red").First().Id, "44");
        fixture.Service.StageMenuValue(grid.Row("Magenta").Last().Id, "42");
        fixture.Display.Requests.Clear();
        fixture.Override = (request, cancellation) =>
        {
            if (request["method"]!.ToString() == "colorSpace.RedControl" && request["params"]!.AsObject().Count > 1)
            { fixture.Service.Cancel(); cancellation.ThrowIfCancellationRequested(); }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Service.ApplyMenuAsync);
        Assert.Single(RgbWrites(fixture));
        Assert.Equal(2, fixture.Writes.Count()); // select Red, then its RGB setter. No restore or next row.
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Equal("Stopped", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task ExternalSelectorDriftDuringScanNeverMislabelsValues()
    {
        using var fixture = await CreateAsync();
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB20P.RedControl" && fixture.Values["WB20P.Interval"]!.ToString() == "5%")
                fixture.Values["WB20P.Interval"] = "10%";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshMenuGridAsync("white20"));
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.Empty(RgbWrites(fixture));
    }

    [Fact]
    public async Task MissingRgbFieldIsNotFilledWithAnotherRowOrDefault()
    {
        using var fixture = await CreateAsync();
        fixture.GridValues["color/Cyan"].Remove("colorSpace.Green");
        await fixture.Service.RefreshMenuGridAsync("color");
        var controls = IpMenuGrids.ForSection("color")!.Row("Cyan").ToArray();
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.Value(controls[0]));
        Assert.Null(fixture.Service.GetSnapshot().Menu.Value(controls[1]));
        Assert.NotNull(fixture.Service.MenuControlDisabledReason(controls[1]));
        Assert.Null(fixture.Service.MenuControlDisabledReason(controls[2]));
        Assert.Empty(RgbWrites(fixture));
    }

    [Fact]
    public async Task PageShowsAllRowsWithoutSelectorsAndSupportsStagedAndImmediateEdits()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuGridAsync("white20");
        await fixture.Service.RefreshMenuGridAsync("color");
        fixture.Display.Requests.Clear();
        var javascript = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("20-point white balance");
        await renderer.AssertClassPresentAsync("picture-switch-control");
        await renderer.AssertClassPresentAsync("picture-switch-track");
        await renderer.AssertClassPresentAsync("picture-compact-slider");
        await renderer.AssertClassPresentAsync("picture-compact-value");
        await renderer.AssertClassPresentAsync("direct-slider", false);
        await renderer.AssertInputPresentAsync("20-point white balance enabled", true);
        await renderer.AssertInputPresentAsync("20-point interval", false);
        foreach (var control in IpMenuCatalog.IndexedControls.Where(control => control.Section == "white20"))
            await renderer.AssertInputPresentAsync(control.Name + " value", true);
        await renderer.ChangeAsync("5% Red value", "11", "onchange");
        await renderer.ChangeAsync("95% Blue value", "12", "onchange");
        Assert.Empty(fixture.Writes);
        await renderer.ClickAsync("Apply 2 pending");
        Assert.Equal(11, fixture.GridValues["white20/5%"]["WB20P.Red"]!.GetValue<int>());
        Assert.Equal(12, fixture.GridValues["white20/95%"]["WB20P.Blue"]!.GetValue<int>());
        await renderer.ClickAsync("Color");
        await renderer.AssertInputPresentAsync("Custom color selector", false);
        foreach (var control in IpMenuCatalog.IndexedControls.Where(control => control.Section == "color"))
            await renderer.AssertInputPresentAsync(control.Name + " value", true);
        await renderer.SetCheckboxAsync("Apply settings immediately", true);
        await renderer.ChangeAsync("Magenta Green value", "42", "onchange");
        Assert.Equal(42, fixture.GridValues["color/Magenta"]["colorSpace.Green"]!.GetValue<int>());
        Assert.Equal("Magenta", fixture.Values["colorSpace.Color"]!.ToString());
        await renderer.ClickAsync("20-point white balance");
        await renderer.SetCheckboxAsync("20-point white balance enabled", false);
        Assert.Equal("Off", fixture.Values["WB20PointMode"]!.ToString());
        await renderer.AssertTextAsync("Turn on 20-point white balance above and Apply");
        await renderer.SetCheckboxAsync("20-point white balance enabled", true);
        Assert.Equal("On", fixture.Values["WB20PointMode"]!.ToString());
        Assert.True(fixture.Service.GetSnapshot().Menu.GridsRead.ContainsKey("white20"));
        await renderer.AssertTextAbsentAsync("An IP Remote action is already running");
        Assert.Contains("Read all 20 rows in", fixture.Service.GetSnapshot().Menu.SelectorSession!.Message, StringComparison.Ordinal);
        await renderer.AssertTextAsync("Read all 20 rows in");
        await renderer.AssertTargetAsync("5% Red value", 11);
        Assert.Equal(0, javascript.ConfirmCalls);
    }

    private static IEnumerable<JsonObject> RgbWrites(MenuFixture fixture) => fixture.Writes.Where(request => IpMenuGrids.All.Any(grid => grid.Fields.Any(field => request["method"]!.ToString() == field + "Control")));

    private static async Task<MenuFixture> CreateAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.RequiredMode;
            fixture.Values[grid.SelectorField] = grid.Section == "white20" ? "50%" : "Blue";
            for (var index = 0; index < grid.Values.Count; index++)
            {
                var row = new JsonObject();
                for (var channel = 0; channel < 3; channel++) row[grid.Fields[channel]] = (grid.Section == "white20" ? -30 : 10) + index * 2 + channel;
                fixture.GridValues[grid.Section + "/" + grid.Values[index]] = row;
            }
        }
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        return fixture;
    }
}
