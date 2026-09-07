using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteAdvancedCommandTests
{
    [Theory]
    [InlineData("backlightControl", "backlight", "26")]
    [InlineData("colorToneControl", "colorTone", "\"Warm1\"")]
    [InlineData("WB20P.RedControl", "WB20P.Red", "-1")]
    [InlineData("WB20P.IntervalControl", "WB20P.Interval", "\"40%\"")]
    [InlineData("colorSpace.BlueControl", "colorSpace.Blue", "51")]
    [InlineData("colorSpace.ColorControl", "colorSpace.Color", "\"Magenta\"")]
    [InlineData("gamma.BT1886Control", "gamma.BT1886", "-1")]
    [InlineData("AMP.blurReductionControl", "AMP.blurReduction", "2")]
    [InlineData("autoHDRRemasteringControl", "autoHDRRemastering", "\"On\"")]
    [InlineData("WB2PointControl", "R-Gain", "-1")]
    public async Task AdvancedWriteUsesDedicatedGetterDurableBaselineAndIndependentReadback(string method, string field, string value)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = JsonNode.Parse(value) });
        var trial = fixture.Service.GetSnapshot().CommandTrial!;
        Assert.NotNull(trial.BeforeCommand);
        Assert.Empty(fixture.Writes);
        fixture.Intercept = async (request, cancellation) =>
        {
            if (request["params"]!.AsObject().Count > 1)
            {
                var journal = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!["Current"]!;
                Assert.True(journal["WriteAttempted"]!.GetValue<bool>());
                Assert.NotNull(journal["BeforeCommand"]);
                Assert.Equal(trial.Prerequisites.ToJsonString(), journal["Prerequisites"]!.ToJsonString());
            }
            return null;
        };
        await fixture.Service.ExecutePreparedCatalogCommandAsync(trial.Id, true);
        var sent = Assert.Single(fixture.Writes);
        Assert.Equal(method, sent["method"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(value), sent["params"]![field]));
        var result = fixture.Service.GetSnapshot().CommandTrial!;
        Assert.True(result.ReadbackMatches, result.Message);
        Assert.True(result.ChangeObserved);
        Assert.NotNull(result.AfterCommand);
        Assert.False(result.ReadWriteVerified);
        await fixture.Service.ConfirmCatalogCommandAsync(trial.Id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadWriteVerified);
        var count = fixture.Inner.Display.Requests.Count;
        await fixture.Inner.RestartAsync();
        Assert.Equal(count, fixture.Inner.Display.Requests.Count);
        Assert.True(Assert.Single(fixture.Service.GetSnapshot().CommandHistory).ReadWriteVerified);
        Assert.NotNull(fixture.Service.GetSnapshot().CommandTrial!.BeforeCommand);
    }

    [Theory]
    [InlineData("WB20P.RedControl", "WB20P.Red", "WB20PointModeControl", "WB20PointMode", "Off")]
    [InlineData("colorSpace.BlueControl", "colorSpace.Blue", "colorSpaceControl", "colorSpace", "Auto")]
    [InlineData("gamma.BT1886Control", "gamma.BT1886", "gammaModeControl", "gammaMode", "ST.2084")]
    [InlineData("AMP.blurReductionControl", "AMP.blurReduction", "autoMotionPlusControl", "autoMotionPlus", "Off")]
    public async Task WrongModePreventsPreparationWithoutAutomaticallyChangingIt(string method, string field, string dependency, string dependencyField, string wrongValue)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.States[dependency][dependencyField] = wrongValue;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = 1 }));
        Assert.Contains("requires", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().CommandTrial);
    }

    [Theory]
    [InlineData("WB20P.RedControl", "WB20P.Red", "WB20P.IntervalControl", "WB20P.Interval", "40%")]
    [InlineData("colorSpace.BlueControl", "colorSpace.Blue", "colorSpace.ColorControl", "colorSpace.Color", "Green")]
    public async Task SelectorChangingAfterPreparePreventsWrite(string method, string field, string dependency, string dependencyField, string newValue)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = 1 });
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        fixture.States[dependency][dependencyField] = newValue;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecutePreparedCatalogCommandAsync(id, true));
        Assert.Contains("changed", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Writes);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
    }

    [Fact]
    public async Task SelectorChangingDuringWriteCannotCountPassEvenWhenChannelMatches()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("WB20P.RedControl", new() { ["WB20P.Red"] = 1 });
        fixture.Intercept = (request, _) =>
        {
            if (request["params"]!.AsObject().Count > 1) fixture.States["WB20P.IntervalControl"]["WB20P.Interval"] = "40%";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmCatalogCommandAsync(id, true));
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    public async Task MissingOrUnsupportedDedicatedGetterNeverWrites(string outcome)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "backlightControl"
            ? outcome == "missing" ? ContrastDisplay.Reply(request, new JsonObject()) : IpRemoteRejectionTests.Reject(request, -32601) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 }));
        Assert.Empty(fixture.Writes);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
    }

    [Fact]
    public async Task FalseSetterEchoCannotReplaceIndependentGetterAndRejectedWriteDoesNotCrashService()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 });
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]!.AsObject().Count > 1
            ? ContrastDisplay.Reply(request, new JsonObject { ["backlight"] = 26 }) : null);
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        await fixture.Service.ConfirmCatalogCommandAsync(id, false);
        await fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 });
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]!.AsObject().Count > 1 ? IpRemoteRejectionTests.Reject(request) : null);
        id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecutePreparedCatalogCommandAsync(id, true));
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        await fixture.Service.ConfirmCatalogCommandAsync(id, false);
        await fixture.Service.QueryCatalogAsync("backlightControl");
        Assert.True(fixture.Service.GetSnapshot().CatalogQuery!.Exchange.IsSuccess);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"unknown\"")]
    [InlineData("1000")]
    public async Task InvalidOriginalNumericFieldNeverPermitsWrite(string value)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.States["backlightControl"]["backlight"] = JsonNode.Parse(value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 }));
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task NumericStringReadbackCanBeComparedWithoutChangingTypedWrite()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.States["backlightControl"]["backlight"] = "25";
        fixture.Intercept = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != "backlightControl") return Task.FromResult<HttpResponseMessage?>(null);
            if (request["params"]!.AsObject()["backlight"] is { } target)
            {
                Assert.Equal(26, target.GetValue<int>());
                fixture.States["backlightControl"]["backlight"] = "26";
            }
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, fixture.States["backlightControl"].DeepClone()));
        };
        await fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 });
        await fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task NumericAdvancedTestCannotIgnoreAnUnrelatedPictureValueChange()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 });
        fixture.Intercept = (request, _) =>
        {
            if (request["params"]!.AsObject().Count > 1) fixture.Inner.Display.Contrast = 44;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task ExplicitArtModeChangeUsesItsGetterEvenWhenTvPictureModeChanges()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.States["artModeControl"] = new() { ["artMode"] = "artModeOff" };
        fixture.Intercept = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "artModeControl" && request["params"]!.AsObject().Count > 1)
                fixture.Inner.Display.Mode = "Ambient";
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await fixture.Service.PrepareCatalogCommandAsync("artModeControl", new() { ["artMode"] = "artModeOn" });
        await fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.Equal("Ambient", fixture.Service.GetSnapshot().CommandTrial!.AfterTv!["pictureMode"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelAfterWriteDoesNotSendFollowUpReadsOrRestore()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("backlightControl", new() { ["backlight"] = 26 });
        fixture.Intercept = (request, _) =>
        {
            if (request["params"]!.AsObject().Count > 1) fixture.Service.Cancel();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true));
        Assert.Contains("Canceled", error.Message, StringComparison.Ordinal);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        Assert.Same(fixture.Inner.Display.Requests.Last(), Assert.Single(fixture.Writes));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("object")]
    [InlineData("string")]
    public async Task TwoPointPartialWritePreservesOtherChannelsAndSupportsExplicitGetterContainers(string shape)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.WbShape = shape;
        await fixture.Service.PrepareCatalogCommandAsync("WB2PointControl", new() { ["B-Offset"] = -1 });
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        var sent = Assert.Single(fixture.Writes)["params"]!.AsObject();
        Assert.Equal(2, sent.Count); // AccessToken and B-Offset only.
        Assert.Equal(0, fixture.States["WB2PointControl"]["R-Gain"]!.GetValue<int>());
        Assert.Equal(-1, fixture.States["WB2PointControl"]["B-Offset"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnrequestedTwoPointChannelChangeRejectsVerification()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("WB2PointControl", new() { ["R-Gain"] = 1 });
        fixture.Intercept = (request, _) =>
        {
            if (request["params"]!.AsObject().Count > 1) fixture.States["WB2PointControl"]["G-Gain"] = 2;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
    }

    [Theory]
    [InlineData("firstScreenAppControl", "applicationName", "Plex")]
    [InlineData("multiviewControl", "multiviewMode", "example-mode")]
    public async Task AppAndMultiviewTargetsNeedAnActualRecentReadAndNeverSelectOnRead(string method, string field, string value)
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == method
            ? ContrastDisplay.Reply(request, request["params"]!.AsObject().Count > 1 ? new JsonObject() : new JsonArray(value)) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = value }));
        Assert.Empty(fixture.Inner.Display.Requests);
        await fixture.Service.QueryCatalogAsync(method);
        Assert.Empty(fixture.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = "invented" }));
        await fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = value });
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.Single(fixture.Writes);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadWriteVerified);
    }

    [Fact]
    public async Task TwoPointPageCanFillReadingEditAndSendOnlyAfterPreparationAndConsent()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        var js = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpCommands));
        await page.StartAsync();
        await page.SelectAsync("Documented IP command", "WB2PointControl");
        Assert.Empty(fixture.Inner.Display.Requests);
        await page.ClickAsync("Read/list 2-point white balance gains / offsets");
        await page.ClickAsync("Fill fields from this reading (no TV change)");
        Assert.Empty(fixture.Writes);
        await page.ChangeAsync("Command R-Gain", "51");
        await page.AssertTextAsync("R-Gain must be an integer from -50 to 50");
        await page.AssertDisabledAsync("Prepare command (read only)", true);
        await page.ChangeAsync("Command R-Gain", "-1");
        await page.ClickAsync("Prepare command (read only)");
        await page.AssertDisabledAsync("Send prepared command once", true);
        await page.SetCheckboxAsync("Confirm documented command", true);
        await page.ClickAsync("Send prepared command once");
        await page.ClickAsync("Observed expected effect — keep");
        await page.AssertTextAsync("Read/write + user verified");
        Assert.Single(fixture.Writes);
        Assert.Equal(0, js.ConfirmCalls);
    }

    [Fact]
    public async Task DeviceInformationSerialIsRedactedInAllExportRepresentations()
    {
        using var fixture = await AdvancedFixture.CreateAsync();
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "getDeviceInformation"
            ? ContrastDisplay.Reply(request, new JsonObject { ["modelID"] = "Example", ["FWVersion"] = "1296", ["serialNumber"] = "private-display-serial" }) : null);
        await fixture.Service.QueryCatalogAsync("getDeviceInformation");
        Assert.DoesNotContain("private-display-serial", fixture.Service.ExportReport(), StringComparison.Ordinal);
        Assert.Contains("private-display-serial", fixture.Service.ExportReport(redactIdentifiers: false), StringComparison.Ordinal);
    }

    private sealed class AdvancedFixture(ContrastFixture inner) : IDisposable
    {
        public ContrastFixture Inner { get; } = inner;
        public SamsungIpRemoteService Service => Inner.Service;
        public string JournalPath => Path.Combine(Inner.DirectoryPath, "ip-remote", "command-tests.json");
        public IEnumerable<JsonObject> Writes => Inner.Display.Requests.Where(request => request["params"]!.AsObject().Count > 1);
        public Func<JsonObject, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }
        public string WbShape { get; set; } = "flat";
        public Dictionary<string, JsonObject> States { get; } = new()
        {
            ["backlightControl"] = new() { ["backlight"] = 25 },
            ["colorToneControl"] = new() { ["colorTone"] = "Warm2" },
            ["WB20PointModeControl"] = new() { ["WB20PointMode"] = "On" },
            ["WB20P.IntervalControl"] = new() { ["WB20P.Interval"] = "35%" },
            ["WB20P.RedControl"] = new() { ["WB20P.Red"] = 0 },
            ["colorSpaceControl"] = new() { ["colorSpace"] = "Custom" },
            ["colorSpace.ColorControl"] = new() { ["colorSpace.Color"] = "Red" },
            ["colorSpace.BlueControl"] = new() { ["colorSpace.Blue"] = 50 },
            ["gammaModeControl"] = new() { ["gammaMode"] = "BT.1886" },
            ["gamma.BT1886Control"] = new() { ["gamma.BT1886"] = 0 },
            ["autoMotionPlusControl"] = new() { ["autoMotionPlus"] = "Custom" },
            ["AMP.blurReductionControl"] = new() { ["AMP.blurReduction"] = 1 },
            ["autoHDRRemasteringControl"] = new() { ["autoHDRRemastering"] = "Off" },
            ["WB2PointControl"] = new() { ["R-Gain"] = 0, ["G-Gain"] = 0, ["B-Gain"] = 0, ["R-Offset"] = 0, ["G-Offset"] = 0, ["B-Offset"] = 0 }
        };
        public static async Task<AdvancedFixture> CreateAsync()
        {
            var fixture = new AdvancedFixture(await ContrastFixture.CreateAsync());
            fixture.Inner.Display.Override = fixture.RespondAsync;
            return fixture;
        }
        private async Task<HttpResponseMessage?> RespondAsync(JsonObject request, CancellationToken cancellation)
        {
            if (Intercept is not null && await Intercept(request, cancellation) is { } response) return response;
            var method = request["method"]!.GetValue<string>();
            if (!States.TryGetValue(method, out var state)) return null;
            foreach (var parameter in request["params"]!.AsObject().Where(pair => pair.Key != "AccessToken")) state[parameter.Key] = parameter.Value?.DeepClone();
            JsonObject result = (JsonObject)state.DeepClone();
            if (method == "WB2PointControl" && WbShape != "flat")
                result = new() { ["WB2Point"] = WbShape == "object" ? result : JsonValue.Create(result.ToJsonString()) };
            return ContrastDisplay.Reply(request, result);
        }
        public void Dispose() => Inner.Dispose();
    }
}
