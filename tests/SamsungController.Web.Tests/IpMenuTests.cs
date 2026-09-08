using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuTests
{
    [Fact]
    public async Task ConnectReusesTokenQueriesActualValuesAndDoesNotRequireVerification()
    {
        using var fixture = await MenuFixture.CreateAsync();
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", "getDeviceInformation" }, fixture.Display.Methods);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.Equal(45, fixture.Value("contrastControl/contrast")!.GetValue<int>());
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("contrastControl/contrast")));
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Writes);
        Assert.All(fixture.Display.Requests, request => Assert.Single(request["params"]!.AsObject()));
    }

    [Fact]
    public async Task RefreshUsesOnlyGettersAndUnsupportedFieldsHaveNoInventedDefaults()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) => Task.FromResult(request["method"]!.ToString() == "backlightControl" ? MenuFixture.Reject(request, -32601) : null);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        Assert.Null(fixture.Value("backlightControl/backlight"));
        Assert.Contains("-32601", fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("backlightControl/backlight"))!, StringComparison.Ordinal);
        Assert.Equal(45, fixture.Value("contrastControl/contrast")!.GetValue<int>());
        Assert.Empty(fixture.Writes);
        fixture.Override = null;
        fixture.Values["backlight"] = 32;
        await fixture.Service.RefreshMenuSectionAsync("expert");
        Assert.Equal(32, fixture.Value("backlightControl/backlight")!.GetValue<int>());
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("backlightControl/backlight")));
    }

    [Fact]
    public async Task DocumentedBatchStagesThenWritesAndReadsBackWithoutRemoteKeysOrPassCounts()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Service.StageMenuValue("colorControl/color", "27");
        fixture.Service.StageMenuValue("sharpnessControl/sharpness", "3");
        Assert.Empty(fixture.Writes);
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(new[] { "contrastControl", "colorControl", "sharpnessControl" }, fixture.Writes.Select(request => request["method"]!.ToString()));
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(27, fixture.Display.Color);
        Assert.Equal(3, fixture.Display.Sharpness);
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal("Completed", menu.Update!.Status);
        Assert.All(menu.Update.Steps, step => Assert.Equal("Applied", step.Status));
        Assert.Empty(menu.Pending);
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.DoesNotContain("remoteKeyControl", fixture.Display.Methods);
        Assert.DoesNotContain(ContrastFixture.Token, fixture.Service.ExportReport(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contrastControl/contrast", "51")]
    [InlineData("sharpnessControl/sharpness", "74")]
    [InlineData("colorControl/color", "-1")]
    [InlineData("contrastControl/contrast", "1.5")]
    [InlineData("contrastControl/contrast", "not a number")]
    public async Task InvalidTargetsFailLocallyAndServiceRemainsUsable(string control, string target)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        var count = fixture.Display.Requests.Count;
        Assert.Throws<ArgumentException>(() => fixture.Service.StageMenuValue(control, target));
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(44, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryTransportFailureClearsConnectionRetainsTokenAndConnectCanRetry(bool afterConnection)
    {
        using var fixture = await MenuFixture.CreateAsync();
        if (afterConnection) await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (_, _) => throw new HttpRequestException("simulated offline TV");
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterConnection ? fixture.Service.RefreshMenuSectionAsync("expert") : fixture.Service.ConnectMenuAsync(loadAllSettings: false));
        Assert.False(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.True(fixture.Service.GetSnapshot().HasToken);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
        fixture.Override = null;
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.Empty(fixture.Writes);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("value")]
    public async Task ExternalChangesStopBeforeOverwritingStagedSettings(string change)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        if (change == "input") fixture.Display.Input = "HDMI1";
        else if (change == "mode") fixture.Display.Mode = "Standard";
        else fixture.Display.Contrast = 40;
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(fixture.Writes);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Equal("Not sent", fixture.Service.GetSnapshot().Menu.Update!.Steps[0].Status);
    }

    [Fact]
    public async Task ExplicitRejectionStopsLaterRowsAndCanBeCorrectedWithoutFakeRecovery()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Service.StageMenuValue("colorControl/color", "27");
        fixture.Service.StageMenuValue("sharpnessControl/sharpness", "3");
        fixture.Override = (request, _) => Task.FromResult(request["method"]!.ToString() == "colorControl" ? MenuFixture.Reject(request, -32002) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(new[] { "Applied", "Rejected unchanged", "Pending" }, menu.Update!.Steps.Select(step => step.Status));
        Assert.False(menu.Update.NeedsReview);
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(0, fixture.Display.Sharpness);
        Assert.Equal(2, fixture.Writes.Count());
        fixture.Override = null;
        fixture.Service.StageMenuValue("colorControl/color", "26");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(26, fixture.Display.Color);
        Assert.Equal(3, fixture.Display.Sharpness);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("disconnect")]
    [InlineData("false echo")]
    public async Task AmbiguousWritesPersistOriginalBeforeSendingStopBatchAndNeverReplay(string failure)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Service.StageMenuValue("colorControl/color", "27");
        fixture.Override = async (request, cancellation) =>
        {
            if (request["method"]!.ToString() != "contrastControl") return null;
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!;
            Assert.Equal("Sending", saved["Steps"]![0]!["Status"]!.ToString());
            Assert.Equal(45, saved["Steps"]![0]!["Original"]!.GetValue<int>());
            if (failure == "disconnect") throw new HttpRequestException("simulated write disconnect");
            if (failure == "cancel") { fixture.Service.Cancel(); cancellation.ThrowIfCancellationRequested(); }
            return ContrastDisplay.Reply(request, new JsonObject { ["contrast"] = 44 });
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Service.ApplyMenuAsync);
        Assert.Single(fixture.Writes);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Equal("Uncertain", fixture.Service.GetSnapshot().Menu.Update!.Steps[0].Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(ContrastFixture.Profile));
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        await fixture.Service.CloseMenuUpdateReviewAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Equal(count, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task WhiteBalanceQueriesDoNotSelectIntervalsAndChangedSelectorStopsWrite()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Values["WB20PointMode"] = "On";
        fixture.Values["WB20P.Interval"] = "50%";
        fixture.Values["WB20P.Red"] = -4;
        await fixture.Service.RefreshMenuSectionAsync("white20");
        Assert.Empty(fixture.Writes);
        Assert.Equal(-4, fixture.Value("WB20P.RedControl/WB20P.Red")!.GetValue<int>());
        fixture.Service.StageMenuValue("WB20P.RedControl/WB20P.Red", "-3");
        fixture.Values["WB20P.Interval"] = "55%";
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task TwoPointUpdatesSendAllChannelsPreservingOtherValuesAndNormalizeNumericStrings()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Values["R-Gain"] = "-5";
        fixture.Values["G-Gain"] = 4;
        await fixture.Service.RefreshMenuSectionAsync("white2");
        Assert.Equal(-5, fixture.Value("WB2PointControl/R-Gain")!.GetValue<int>());
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-4");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(-4, fixture.Values["R-Gain"]!.GetValue<int>());
        Assert.Equal(4, fixture.Values["G-Gain"]!.GetValue<int>());
        Assert.Equal(new[] { "R-Gain", "G-Gain", "B-Gain", "R-Offset", "G-Offset", "B-Offset", "AccessToken" }.Order(), fixture.Writes.Single()["params"]!.AsObject().Select(pair => pair.Key).Order());
    }

    [Fact]
    public async Task ModesAreIsolatedFromBatchesAndPreferencesNeverApplyPendingEdits()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        Assert.Throws<InvalidOperationException>(() => fixture.Service.StageMenuValue("pictureModeControl/pictureMode", "Movie"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveMenuPreferencesAsync(true));
        fixture.Service.DiscardMenuChanges();
        await fixture.Service.SaveMenuPreferencesAsync(true);
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
        Assert.Empty(fixture.Writes);
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("pictureModeControl/pictureMode", "Movie");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal("Movie", fixture.Display.Mode);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Readings);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task RemoteSendsOneExplicitKeyAndKeepsReadingsAndPendingEdits()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var before = fixture.Service.GetSnapshot().Menu;
        var count = fixture.Display.Requests.Count;
        await fixture.Service.SendMenuKeyAsync("return");
        Assert.Equal(count + 1, fixture.Display.Requests.Count);
        Assert.Equal("return", fixture.Writes.Single()["params"]!["remoteKey"]!.ToString());
        Assert.Equal(before.Readings, fixture.Service.GetSnapshot().Menu.Readings);
        Assert.Equal(before.Pending, fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Equal(before.ValuesRevision, fixture.Service.GetSnapshot().Menu.ValuesRevision);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        await fixture.Service.SendMenuKeyAsync("power");
        Assert.False(fixture.Service.GetSnapshot().Menu.Connected);
    }

    [Fact]
    public async Task MenuPageQueriesThenStagesAppliesAndHandlesBadInputWithoutVerificationOrPopup()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.RefreshMenuSectionAsync("white2");
        var javascript = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertTargetAsync("Contrast value", 45);
        await renderer.AssertDisabledAsync("Apply 0 pending", true);
        await renderer.ChangeAsync("Contrast value", "74", "onchange");
        await renderer.AssertTextAsync("Contrast must be an integer from 0 to 50");
        Assert.Empty(fixture.Writes);
        await renderer.ChangeAsync("Contrast value", "44", "onchange");
        await renderer.AssertDisabledAsync("Apply 1 pending", false);
        await renderer.ClickAsync("Apply 1 pending");
        Assert.Equal(44, fixture.Display.Contrast);
        await renderer.SetCheckboxAsync("Apply settings immediately", true);
        await renderer.ChangeAsync("Contrast slider", "43");
        Assert.Equal(44, fixture.Display.Contrast); // A drag preview is not a write.
        await renderer.ChangeAsync("Contrast slider", "43", "onchange");
        Assert.Equal(43, fixture.Display.Contrast);
        var writes = fixture.Writes.Count();
        await renderer.ChangeAsync("Contrast slider", "43", "onchange");
        Assert.Equal(writes, fixture.Writes.Count());
        await renderer.AssertDisabledAsync("Apply 0 pending", true);
        await renderer.AssertTextAbsentAsync("No pending settings to apply.");
        await renderer.ClickAsync("2-point white balance");
        await renderer.AssertTextAsync("RGB gains");
        await renderer.AssertTextAsync("RGB offsets");
        await renderer.AssertInputPresentAsync("Confirm direct picture conditions", false);
        Assert.Equal(0, javascript.ConfirmCalls);
    }

    [Fact]
    public async Task CollateralNumericChangeStopsLaterSettingsEvenWhenTargetMatches()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Service.StageMenuValue("sharpnessControl/sharpness", "3");
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "contrastControl") fixture.Display.Color++;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(0, fixture.Display.Sharpness);
        Assert.Single(fixture.Writes);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task ExistingProfileSetupConnectsWithoutPairingAndNavigatesToMenu()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var navigation = new MenuNavigation();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<NavigationManager>(navigation).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DisplaySetup));
        await renderer.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await renderer.AssertDisabledAsync("Connect and open Menu", false);
        await renderer.ClickAsync("Connect and open Menu");
        Assert.EndsWith("/menu", navigation.Uri, StringComparison.Ordinal);
        await renderer.AssertDisabledAsync("Open Menu", false);
        Assert.DoesNotContain("createAccessToken", fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task PrimaryLayoutUsesDirectConnectionAndDoesNotExposeMacrosOrMenuVerification()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).AddSingleton<NavigationManager>(new MenuNavigation()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MainLayout>()).ToHtmlString());
        Assert.Contains("HDMI4", html, StringComparison.Ordinal);
        Assert.Contains("FilmmakerMode", html, StringComparison.Ordinal);
        Assert.Contains(">Disconnect</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Macros", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Build &amp; Verify", html, StringComparison.Ordinal);
        Assert.DoesNotContain("checks needed", html, StringComparison.Ordinal);
        Assert.Contains("href=\"menu\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"remote\"", html, StringComparison.Ordinal);
        Assert.Contains("popovertarget=\"remote-drawer\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Open remote\"", html, StringComparison.Ordinal);
        Assert.Contains("<svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("popover=\"auto\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"remote-drawer-body\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Close remote\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Remote</h1>", html, StringComparison.Ordinal);
        var routes = typeof(DirectMenu).Assembly.GetTypes().SelectMany(type => type.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Select(route => route.Template)).ToArray();
        Assert.Equal(routes.Length, routes.Distinct().Count());
        Assert.DoesNotContain("/macros", routes);
        Assert.DoesNotContain("/menu/build", routes);
        Assert.DoesNotContain("/verification", routes);
        Assert.Contains("/menu", routes);
        Assert.DoesNotContain("/remote", routes);
    }

    [Fact]
    public async Task HiddenRemoteDoesNotQueryAndButtonsSendOnlyTheirExplicitKey()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        var requests = fixture.Display.Requests.Count;
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(Components.Shared.DirectRemote));
        await renderer.StartAsync();
        Assert.Equal(requests, fixture.Display.Requests.Count);
        await renderer.ClickAsync("OK");
        Assert.Single(fixture.Writes);
        Assert.Equal("remoteKeyControl", fixture.Writes.Single()["method"]!.ToString());
        Assert.Equal("enter", fixture.Writes.Single()["params"]!["remoteKey"]!.ToString());
        await fixture.Service.DisconnectMenuAsync();
        await renderer.AssertDisabledAsync("OK", true);
    }

    internal sealed class MenuNavigation : NavigationManager
    {
        public MenuNavigation() => Initialize("http://localhost/", "http://localhost/menu");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
}

internal sealed class MenuFixture(ContrastFixture inner) : IDisposable
{
    public SamsungIpRemoteService Service => inner.Service;
    public ContrastDisplay Display => inner.Display;
    public JsonObject Values { get; } = new();
    public Dictionary<string, JsonObject> GridValues { get; } = new(StringComparer.Ordinal);
    public string JournalPath => Path.Combine(inner.DirectoryPath, "ip-remote", "menu-update.json");
    public Func<JsonObject, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public IEnumerable<JsonObject> Writes => Display.Requests.Where(request => request["params"]!.AsObject().Count > 1);
    public JsonNode? Value(string id) => Service.GetSnapshot().Menu.Value(IpMenuCatalog.Get(id));
    public static async Task<MenuFixture> CreateAsync()
    {
        var fixture = new MenuFixture(await ContrastFixture.CreateAsync());
        foreach (var control in IpMenuCatalog.Controls)
            fixture.Values[control.Field] = control.Parameter.Kind == IpRemoteParameterKind.Integer ? JsonValue.Create(0) : JsonValue.Create(control.Parameter.Choices[0]);
        fixture.Display.Override = fixture.ReplyAsync;
        return fixture;
    }
    private async Task<HttpResponseMessage?> ReplyAsync(JsonObject request, CancellationToken cancellation)
    {
        if (Override is not null && await Override(request, cancellation) is { } overridden) return overridden;
        var method = request["method"]!.ToString();
        if (method is "getVideoStates" or "contrastControl" or "colorControl" or "sharpnessControl") return null;
        if (method == "getDeviceInformation") return ContrastDisplay.Reply(request, new JsonObject { ["modelID"] = "FakeTV", ["FWVersion"] = "1301" });
        if (method == "remoteKeyControl") return ContrastDisplay.Reply(request, new JsonObject());
        if (method == "getTVStates")
            return ContrastDisplay.Reply(request, new JsonObject { ["inputSource"] = Display.Input, ["pictureMode"] = Display.Mode, ["volume"] = 10, ["mute"] = "muteOff", ["pictureSize"] = "16:9", ["soundMode"] = "Standard", ["speakerSelect"] = "Internal" });
        var command = SamsungIpRemoteCommands.Get(method);
        var grid = IpMenuGrids.All.FirstOrDefault(group => group.Fields.Any(field => field + "Control" == method));
        if (grid is not null && GridValues.TryGetValue(grid.Section + "/" + Values[grid.SelectorField], out var row))
        {
            var field = command.Parameters[0].Name;
            if (request["params"]!.AsObject().TryGetPropertyValue(field, out var target)) row[field] = target!.DeepClone();
            return ContrastDisplay.Reply(request, new JsonObject { [field] = row[field]?.DeepClone() });
        }
        foreach (var pair in request["params"]!.AsObject().Where(pair => pair.Key != "AccessToken")) Values[pair.Key] = pair.Value?.DeepClone();
        if (method == "pictureModeControl" && request["params"]!["pictureMode"] is { } mode) Display.Mode = mode.ToString();
        if (method == "inputSourceControl" && request["params"]!["inputSource"] is { } input) Display.Input = input.ToString();
        var result = new JsonObject();
        foreach (var parameter in command.Parameters) result[parameter.Name] = Values[parameter.Name]?.DeepClone();
        return ContrastDisplay.Reply(request, result);
    }
    public static HttpResponseMessage Reject(JsonObject request, int code) => new(HttpStatusCode.OK)
    { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.ToJsonString(), ["error"] = new JsonObject { ["code"] = code, ["message"] = "Rejected by simulated TV" } }.ToJsonString()) };
    public Task RestartAsync() => inner.RestartAsync();
    public void AssertOnlyWhiteBalanceReadWrites()
    {
        Assert.All(Writes, request => Assert.Contains(request["method"]!.ToString(), new[] { "WB20PointModeControl", "WB20P.IntervalControl" }));
        Assert.Equal(new[] { "On", "Off" }, Writes.Where(request => request["method"]!.ToString() == "WB20PointModeControl")
            .Select(request => request["params"]!["WB20PointMode"]!.ToString()));
        Assert.Equal("Off", Values["WB20PointMode"]!.ToString());
    }
    public void Dispose() => inner.Dispose();
}
