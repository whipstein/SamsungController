using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteCommandServiceTests
{
    [Fact]
    public async Task PreparedCommandHasFreshPreflightDurableOriginalReadbackAndExplicitVisualReview()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        var trial = fixture.Service.GetSnapshot().CommandTrial!;
        Assert.False(trial.RequiresReview);
        Assert.Equal(10, trial.BeforeTv["volume"]!.GetValue<int>());
        fixture.Intercept = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() == "directVolumeControl")
            {
                var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!["Current"]!;
                Assert.True(saved["WriteAttempted"]!.GetValue<bool>());
                Assert.Equal(10, saved["BeforeTv"]!["volume"]!.GetValue<int>());
            }
            return null;
        };
        await fixture.Service.ExecutePreparedCatalogCommandAsync(trial.Id, true);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", "getTVStates", "getVideoStates", "directVolumeControl", "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        Assert.Equal(11, fixture.Volume);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        Assert.Empty(fixture.Service.GetSnapshot().CommandHistory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(ContrastFixture.Profile));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync());
        var count = fixture.Display.Requests.Count;
        await fixture.Service.ConfirmCatalogCommandAsync(trial.Id, true);
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(Assert.Single(fixture.Service.GetSnapshot().CommandHistory).ReadWriteVerified);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        var report = fixture.Service.ExportReport();
        Assert.Contains("CommandHistory", report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Token, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Profile.Connection.Host, report, StringComparison.Ordinal);
        await fixture.Inner.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(Assert.Single(fixture.Service.GetSnapshot().CommandHistory).ReadWriteVerified);
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("old-id")]
    [InlineData("state-drift")]
    public async Task InvalidExecutionStopsBeforeCommand(string problem)
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        if (problem == "state-drift") fixture.Volume = 12;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecutePreparedCatalogCommandAsync(problem == "old-id" ? Guid.NewGuid() : fixture.Service.GetSnapshot().CommandTrial!.Id, problem != "consent"));
        Assert.DoesNotContain("directVolumeControl", fixture.Display.Methods);
    }

    [Theory]
    [InlineData("contrastControl", "{\"contrast\":44}")]
    [InlineData("sharpnessControl", "{\"sharpness\":74}")]
    public async Task CatalogCannotBypassExistingPictureVerificationOrRanges(string method, string json)
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync(method, JsonNode.Parse(json)!.AsObject()));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task FalseAcknowledgmentCannotBeCountedAsVerifiedAndNoAutomaticRestoreOccurs()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        fixture.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "directVolumeControl"
            ? ContrastDisplay.Reply(request, new JsonObject { ["volume"] = 11 }) : null);
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmCatalogCommandAsync(id, true));
        await fixture.Service.ConfirmCatalogCommandAsync(id, false);
        Assert.Single(fixture.Display.Methods, method => method == "directVolumeControl");
        Assert.False(Assert.Single(fixture.Service.GetSnapshot().CommandHistory).ReadWriteVerified);
    }

    [Fact]
    public async Task AlreadyAtTargetDoesNotSendOrManufactureWriteEvidence()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 10 }));
        Assert.All(fixture.Display.Methods, method => Assert.StartsWith("get", method));
        Assert.Null(fixture.Service.GetSnapshot().CommandTrial);
        Assert.Empty(fixture.Service.GetSnapshot().CommandHistory);
    }

    [Fact]
    public async Task MissingOriginalFieldCannotEstablishAWriteEvenIfTargetLaterAppears()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        var applied = false;
        fixture.Intercept = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "directVolumeControl") applied = true;
            if (!applied && request["method"]!.GetValue<string>() == "getTVStates")
                return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject { ["inputSource"] = fixture.Display.Input, ["pictureMode"] = fixture.Display.Mode }));
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        await fixture.Service.ConfirmCatalogCommandAsync(id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ChangeObserved);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadWriteVerified);
    }

    [Fact]
    public async Task ExperimentalNumericPictureTestCannotVerifyIfItChangesOtherFields()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("brightnessControl", new() { ["brightness"] = -1 });
        fixture.Intercept = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != "brightnessControl") return Task.FromResult<HttpResponseMessage?>(null);
            fixture.Display.VideoOverride = new() { ["brightness"] = -1, ["contrast"] = 40, ["color"] = 25, ["sharpness"] = 0 };
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject { ["brightness"] = -1 }));
        };
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmCatalogCommandAsync(id, true));
        Assert.Single(fixture.Display.Methods, method => method == "brightnessControl");
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("reject")]
    [InlineData("cancel")]
    public async Task FailedOrCanceledCommandKeepsReviewAcrossRestartWithoutReplaying(string failure)
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        fixture.Intercept = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != "directVolumeControl") return Task.FromResult<HttpResponseMessage?>(null);
            if (failure == "reject") return Task.FromResult<HttpResponseMessage?>(IpRemoteRejectionTests.Reject(request));
            fixture.Volume = 11;
            if (failure == "cancel") fixture.Service.Cancel();
            throw new HttpRequestException("Simulated lost response");
        };
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecutePreparedCatalogCommandAsync(id, true));
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        var count = fixture.Display.Requests.Count;
        await fixture.Inner.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConfirmCatalogCommandAsync(id, true));
        await fixture.Service.ConfirmCatalogCommandAsync(id, false);
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.RequiresReview);
    }

    [Theory]
    [InlineData("powerControl", "power", "powerOff")]
    [InlineData("powerControl", "power", "reboot")]
    [InlineData("remoteKeyControl", "remoteKey", "cursorUp")]
    public async Task ActionCommandsSkipAutomaticReadbackAndNeverClaimReadWriteVerification(string method, string field, string value)
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync(method, new() { [field] = value });
        fixture.Display.Requests.Clear();
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", method }, fixture.Display.Methods);
        await fixture.Service.ConfirmCatalogCommandAsync(id, true);
        Assert.Null(fixture.Service.GetSnapshot().CommandTrial!.ReadbackMatches);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.ReadWriteVerified);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.VisualConfirmed);
    }

    [Fact]
    public async Task DeviceSelectionRequiresActualFreshDiscoveryWithMatchingTypedIdAndName()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        var parameters = new JsonObject { ["deviceId"] = "usb1", ["deviceName"] = "Example USB" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync("USBSourceControl", parameters));
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.QueryCatalogAsync("USBSourceControl");
        Assert.Single(fixture.Display.Requests);
        Assert.Single(fixture.Display.Requests[0]["params"]!.AsObject());
        Assert.Null(fixture.Service.GetSnapshot().CommandTrial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareCatalogCommandAsync("USBSourceControl", new() { ["deviceId"] = "invented", ["deviceName"] = "Example USB" }));
        await fixture.Service.PrepareCatalogCommandAsync("USBSourceControl", parameters);
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.True(fixture.Service.GetSnapshot().CommandTrial!.Acknowledged);
        Assert.Equal(2, fixture.Display.Methods.Count(method => method == "USBSourceControl"));
    }

    [Fact]
    public async Task MissingJournalStoragePreventsTheWrite()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        Directory.CreateDirectory(fixture.JournalPath + ".tmp");
        var error = await Record.ExceptionAsync(() => fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.DoesNotContain("directVolumeControl", fixture.Display.Methods);
        Assert.False(fixture.Service.GetSnapshot().CommandTrial!.WriteAttempted);
    }

    [Fact]
    public async Task ChangingPictureModeReadsNewContextButNeverUpdatesOrGrantsPictureCapabilities()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Inner.VerifyAsync();
        var capability = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.RefreshWorkspaceAsync();
        await fixture.Service.PrepareCatalogCommandAsync("pictureModeControl", new() { ["pictureMode"] = "Standard" });
        var id = fixture.Service.GetSnapshot().CommandTrial!.Id;
        await fixture.Service.ExecutePreparedCatalogCommandAsync(id, true);
        Assert.Null(fixture.Service.GetSnapshot().WorkspaceReading);
        Assert.Equal(capability, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        await fixture.Service.ConfirmCatalogCommandAsync(id, true);
        await fixture.Service.ReadDirectPictureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 44, true));
    }

    [Fact]
    public async Task PagePreparesChecksSendsReviewsAndRejectsInvalidNumericInputWithoutPopup()
    {
        using var fixture = await CatalogFixture.CreateAsync();
        var js = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpCommands));
        await page.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await page.SelectAsync("Documented IP command", "directVolumeControl");
        await page.ChangeAsync("Command volume", "101");
        await page.AssertDisabledAsync("Prepare command (read only)", true);
        await page.AssertTextAsync("volume must be an integer from 0 to 100");
        await page.ChangeAsync("Command volume", "11");
        await page.ClickAsync("Prepare command (read only)");
        await page.AssertDisabledAsync("Send prepared command once", true);
        await page.SetCheckboxAsync("Confirm documented command", true);
        await page.ClickAsync("Send prepared command once");
        Assert.Equal(11, fixture.Volume);
        await page.AssertTextAsync("Independent readback matches");
        await page.ClickAsync("Observed expected effect — keep");
        await page.AssertTextAsync("Read/write + user verified");
        Assert.Equal(0, js.ConfirmCalls);
        await page.ChangeAsync("Command volume", "12");
        await page.AssertDisabledAsync("Send prepared command once", true);
    }

    [Theory]
    [InlineData(typeof(IpRemote))]
    [InlineData(typeof(IpControls))]
    [InlineData(typeof(IpCommands))]
    public async Task PendingCatalogReviewWithoutPictureJournalDoesNotCrashAnyIpPage(Type pageType)
    {
        using var fixture = await CatalogFixture.CreateAsync();
        await fixture.Service.PrepareCatalogCommandAsync("directVolumeControl", new() { ["volume"] = 11 });
        await fixture.Service.ExecutePreparedCatalogCommandAsync(fixture.Service.GetSnapshot().CommandTrial!.Id, true);
        Assert.Null(fixture.Service.GetSnapshot().PictureTest);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, pageType);
        await page.StartAsync();
        await page.AssertTextAsync(pageType == typeof(IpCommands) ? "Independent readback matches" : "review");
    }

    private sealed class CatalogFixture(ContrastFixture inner) : IDisposable
    {
        public ContrastFixture Inner { get; } = inner;
        public SamsungIpRemoteService Service => Inner.Service;
        public ContrastDisplay Display => Inner.Display;
        public string JournalPath => Path.Combine(Inner.DirectoryPath, "ip-remote", "command-tests.json");
        public int Volume { get; set; } = 10;
        public Func<JsonObject, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }
        public static async Task<CatalogFixture> CreateAsync()
        {
            var fixture = new CatalogFixture(await ContrastFixture.CreateAsync());
            fixture.Display.Override = fixture.RespondAsync;
            return fixture;
        }
        private async Task<HttpResponseMessage?> RespondAsync(JsonObject request, CancellationToken cancellation)
        {
            if (Intercept is not null && await Intercept(request, cancellation) is { } reply) return reply;
            var method = request["method"]!.GetValue<string>();
            if (method == "getTVStates") return ContrastDisplay.Reply(request, new JsonObject { ["inputSource"] = Display.Input, ["pictureMode"] = Display.Mode, ["volume"] = Volume });
            if (method == "directVolumeControl") { Volume = request["params"]!["volume"]!.GetValue<int>(); return ContrastDisplay.Reply(request, new JsonObject { ["volume"] = Volume }); }
            if (method == "pictureModeControl") { Display.Mode = request["params"]!["pictureMode"]!.GetValue<string>(); return ContrastDisplay.Reply(request, new JsonObject { ["pictureMode"] = Display.Mode }); }
            if (method is "powerControl" or "remoteKeyControl") return ContrastDisplay.Reply(request, new JsonObject());
            if (method == "USBSourceControl") return ContrastDisplay.Reply(request, request["params"]!.AsObject().ContainsKey("deviceId")
                ? new JsonObject() : new JsonArray(new JsonObject { ["deviceId"] = "usb1", ["deviceName"] = "Example USB" }));
            return null;
        }
        public void Dispose() => Inner.Dispose();
    }
}
