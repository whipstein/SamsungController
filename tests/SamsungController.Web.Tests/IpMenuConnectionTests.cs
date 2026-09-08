using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuConnectionTests
{
    [Fact]
    public async Task ConnectLoadsAllSectionsAndReadableMethodsWithoutEnablingInactiveModes()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["WB20PointMode"] = "Off";
        fixture.Values["colorSpace"] = "Auto";
        await fixture.Service.ConnectMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.True(menu.Connected);
        Assert.NotNull(menu.SettingsLoadedAt);
        Assert.Equal(IpMenuCatalog.Sections.Count, menu.SectionsRead.Count);
        foreach (var command in SamsungIpRemoteCommands.All.Where(command => command.CanQuery))
        {
            if (IpMenuGrids.All.Any(grid => grid.Fields.Any(field => command.Method == field + "Control"))) continue;
            Assert.True(menu.Readings.ContainsKey(command.Method) || fixture.Display.Methods.Contains(command.Method), command.Method);
        }
        Assert.Empty(fixture.Writes);
        Assert.Empty(menu.IndexedReadings);
        Assert.Equal("Off", menu.Readings["WB20PointModeControl"].Values!["WB20PointMode"]!.ToString());
        Assert.Contains(menu.LoadWarnings, warning => warning.Contains("WB20PointMode", StringComparison.Ordinal));
        Assert.Contains(menu.LoadWarnings, warning => warning.Contains("colorSpace", StringComparison.Ordinal));
        Assert.Equal(45, fixture.Value("contrastControl/contrast")!.GetValue<int>());
    }

    [Fact]
    public async Task ConnectPreloadsAllActiveGridCellsRestoresSelectorsAndTabChangesSendNothing()
    {
        using var fixture = await MenuFixture.CreateAsync();
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.RequiredMode;
            fixture.Values[grid.SelectorField] = grid.Values[2];
            for (var index = 0; index < grid.Values.Count; index++)
            {
                var row = new JsonObject();
                foreach (var field in grid.Fields) row[field] = index;
                fixture.GridValues[grid.Section + "/" + grid.Values[index]] = row;
            }
        }
        var original = fixture.GridValues.ToDictionary(pair => pair.Key, pair => pair.Value.ToJsonString());
        await fixture.Service.ConnectMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(78, menu.IndexedReadings.Count);
        foreach (var grid in IpMenuGrids.All)
        {
            Assert.True(menu.GridsRead.ContainsKey(grid.Section));
            Assert.Equal(grid.Values[2], fixture.Values[grid.SelectorField]!.ToString());
            for (var index = 0; index < grid.Values.Count; index++)
                foreach (var control in grid.Row(grid.Values[index])) Assert.Equal(index, menu.Value(control)!.GetValue<int>());
        }
        Assert.All(fixture.Writes, request => Assert.Contains(IpMenuGrids.All, grid => grid.SelectorMethod == request["method"]!.ToString()));
        Assert.All(original, pair => Assert.Equal(pair.Value, fixture.GridValues[pair.Key].ToJsonString()));
        var requests = fixture.Display.Requests.Count;
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("20-point white balance");
        await renderer.AssertTargetAsync("5% Red value", 0);
        await renderer.ClickAsync("Color");
        await renderer.AssertTargetAsync("Red Red value", 0);
        await renderer.ClickAsync("2-point white balance");
        Assert.Equal(requests, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task UnsupportedFieldsDoNotAbortLoadAndListPayloadsAreRetained()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() switch
        {
            "WB2PointControl" => MenuFixture.Reject(request, -32601),
            "USBSourceControl" => new(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject
                { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonArray(new JsonObject { ["deviceName"] = "Example USB" }) }.ToJsonString())
            },
            _ => null
        });
        await fixture.Service.ConnectMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.True(menu.Connected);
        Assert.NotNull(menu.SettingsLoadedAt);
        Assert.Contains(menu.LoadWarnings, warning => warning.Contains("read methods were unavailable", StringComparison.Ordinal));
        Assert.Equal(SamsungIpRemoteOutcome.Unsupported, menu.Readings["WB2PointControl"].Outcome);
        Assert.Null(menu.Value(IpMenuCatalog.Get("WB2PointControl/R-Gain")));
        Assert.IsType<JsonArray>(menu.Readings["USBSourceControl"].Payload);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task StopDuringConnectionDoesNotResumeOnTabVisitOrRestart()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB2PointControl") fixture.Service.Cancel();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ConnectMenuAsync());
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Equal("WB2PointControl", fixture.Display.Methods.Last());
        var requests = fixture.Display.Requests.Count;
        await using (var services = Services(fixture))
        await using (var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu)))
        {
            await renderer.StartAsync();
            await renderer.ClickAsync("20-point white balance");
            Assert.Equal(requests, fixture.Display.Requests.Count);
            await renderer.AssertTextAsync("All-settings load is incomplete");
        }
        await fixture.RestartAsync();
        Assert.Equal(requests, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ContextChangeBeforeGridLoadingStopsBeforeAnySelectorWrite()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["WB20PointMode"] = "On";
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB2PointControl") fixture.Display.Input = "HDMI2";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectMenuAsync());
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("signal only")]
    [InlineData("input")]
    [InlineData("picture mode")]
    public async Task FullRefreshReloadsNewContextWithoutReconnectOrOldDrafts(string change)
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["gammaMode"] = "BT.1886";
        await fixture.Service.ConnectMenuAsync();
        var original = fixture.Service.GetSnapshot().Menu;
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");

        // A changed external signal need not change the reported port or mode.
        if (change == "input") fixture.Display.Input = "HDMI2";
        if (change == "picture mode") fixture.Display.Mode = "Standard";
        fixture.Values["gammaMode"] = "ST.2084";
        fixture.Values["R-Gain"] = 12;
        fixture.Values["backlight"] = 35;
        fixture.Display.Contrast = 40;
        await fixture.Service.RefreshAllMenuSettingsAsync();

        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.True(menu.Connected);
        Assert.Equal(original.SessionId, menu.SessionId);
        Assert.True(menu.ValuesRevision > original.ValuesRevision);
        Assert.NotNull(menu.SettingsLoadedAt);
        Assert.Equal(IpMenuCatalog.Sections.Count, menu.SectionsRead.Count);
        Assert.Empty(menu.Pending);
        Assert.Equal("ST.2084", fixture.Value("gammaModeControl/gammaMode")!.ToString());
        Assert.Equal(12, fixture.Value("WB2PointControl/R-Gain")!.GetValue<int>());
        Assert.Equal(35, fixture.Value("backlightControl/backlight")!.GetValue<int>());
        Assert.Equal(40, fixture.Value("contrastControl/contrast")!.GetValue<int>());
        Assert.Equal(2, fixture.Display.Methods.Count(method => method == "getDeviceInformation"));
        Assert.DoesNotContain("createAccessToken", fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.Contains("TV settings refreshed", menu.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullRefreshReloadsCachedGridValuesAndRestoresSelectors()
    {
        using var fixture = await MenuFixture.CreateAsync();
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.RequiredMode;
            fixture.Values[grid.SelectorField] = grid.Values[3];
            foreach (var value in grid.Values)
                fixture.GridValues[grid.Section + "/" + value] = new JsonObject(grid.Fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(1))));
        }
        await fixture.Service.ConnectMenuAsync();
        foreach (var row in fixture.GridValues.Values)
            foreach (var field in row.Select(pair => pair.Key).ToArray()) row[field] = 9;
        await fixture.Service.RefreshAllMenuSettingsAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(78, menu.IndexedReadings.Count);
        foreach (var grid in IpMenuGrids.All)
        {
            Assert.True(menu.GridsRead.ContainsKey(grid.Section));
            Assert.Equal(grid.Values[3], fixture.Values[grid.SelectorField]!.ToString());
            foreach (var value in grid.Values)
                foreach (var control in grid.Row(value)) Assert.Equal(9, menu.Value(control)!.GetValue<int>());
        }
        Assert.All(fixture.Writes, request => Assert.Contains(IpMenuGrids.All, grid => grid.SelectorMethod == request["method"]!.ToString()));
    }

    [Fact]
    public async Task RefreshButtonRecoversStoppedLoadAndReevaluatesHiddenControls()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == "WB2PointControl" ? MenuFixture.Reject(request, -32601) : null);
        await fixture.Service.ConnectMenuAsync();
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB2PointControl") fixture.Service.Cancel();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RefreshAllMenuSettingsAsync());
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        var stoppedAt = fixture.Display.Requests.Count;
        fixture.Override = null;
        fixture.Values["R-Gain"] = 7;
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("2-point white balance");
        Assert.Equal(stoppedAt, fixture.Display.Requests.Count);
        await renderer.AssertTextAsync("All-settings load is incomplete");
        await renderer.ClickAsync("Refresh TV values");
        await renderer.AssertTargetAsync("R Gain value", 7);
        await renderer.AssertTextAbsentAsync("All-settings load is incomplete");
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Equal(IpMenuCatalog.Sections.Count, fixture.Service.GetSnapshot().Menu.SectionsRead.Count);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ContextChangeDuringExplicitRefreshStopsAndCanBeRetriedInPlace()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Values["WB20PointMode"] = "On";
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB2PointControl") fixture.Display.Input = "HDMI2";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshAllMenuSettingsAsync());
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.Empty(fixture.Writes);
        fixture.Override = null;
        fixture.Values["WB20PointMode"] = "Off";
        await fixture.Service.RefreshAllMenuSettingsAsync();
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Equal("HDMI2", fixture.Service.GetSnapshot().Menu.Input);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task ContextChangingAwayAndBackDuringGridReadCannotMarkInvalidatedLoadComplete()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Values["WB20PointMode"] = "On";
        var originalInput = fixture.Display.Input;
        var changed = false;
        var contextReads = 0;
        fixture.Override = (request, _) =>
        {
            var method = request["method"]!.ToString();
            if (method == "WB20P.RedControl" && !changed) { changed = true; fixture.Display.Input = "HDMI2"; }
            if (method == "getTVStates" && changed && ++contextReads == 2) fixture.Display.Input = originalInput;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshAllMenuSettingsAsync());
        Assert.Equal(originalInput, fixture.Service.GetSnapshot().Menu.Input);
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task RemoteKeyInvalidationOffersFullRefreshWithoutAStaleCompletedLoad()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        await fixture.Service.SendMenuKeyAsync("return");
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        await fixture.Service.RefreshAllMenuSettingsAsync();
        Assert.NotNull(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Single(fixture.Writes);
        Assert.Equal("remoteKeyControl", fixture.Writes.Single()["method"]!.ToString());
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
