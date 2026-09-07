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
            await renderer.AssertTextAsync("Connection preload did not finish");
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

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
