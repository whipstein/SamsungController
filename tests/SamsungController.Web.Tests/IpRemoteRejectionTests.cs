using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteRejectionTests
{
    internal static HttpResponseMessage Reject(JsonObject request, int code = -32002) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.ToJsonString(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = "Server error", ["data"] = "Failed" }
        }.ToJsonString())
    };

    [Theory]
    [InlineData(-32002)]
    [InlineData(-32003)]
    [InlineData(-32602)]
    public async Task ExplicitRejectionWithUnchangedReadbackDoesNotLockControlsOrLoseVerification(int code)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(control: "sharpness");
        await fixture.Service.ReadDirectPictureAsync("sharpness");
        fixture.Display.Requests.Clear();
        fixture.Display.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "sharpnessControl" ? Reject(request, code) : null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 74, true));
        Assert.Contains("original 0 is unchanged", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", "sharpnessControl", "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RejectedUnchanged);
        Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Equal(0, fixture.Service.GetSnapshot().DirectPictureReading!.Value);
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        fixture.Display.Override = null;
        await fixture.Service.ReadDirectPictureAsync("sharpness");
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 1, true);
        Assert.Equal(1, fixture.Display.Sharpness);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("mode")]
    [InlineData("other-field")]
    [InlineData("read-failure")]
    [InlineData("cancel")]
    public async Task RejectionNeverClosesRecoveryWithoutMatchingReadback(string problem)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(control: "sharpness");
        await fixture.Service.ReadDirectPictureAsync("sharpness");
        fixture.Display.Requests.Clear();
        var rejected = false;
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "sharpnessControl")
            {
                rejected = true;
                if (problem == "value") fixture.Display.Sharpness = 20;
                if (problem == "mode") fixture.Display.Mode = "Standard";
                if (problem == "other-field") fixture.Display.Contrast = 40;
                if (problem == "cancel") fixture.Service.Cancel();
                return Task.FromResult<HttpResponseMessage?>(Reject(request));
            }
            if (rejected && problem == "read-failure") throw new HttpRequestException("Simulated failure");
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 74, true));
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RejectedUnchanged);
        Assert.Equal(new[] { 74 }, fixture.Display.WritesFor("sharpness"));
        if (problem == "cancel") Assert.Equal(3, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task ConfiguredLimitsBlockBeforeRequestsPersistAndPreserveCapability()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(control: "sharpness");
        var evidence = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { ControlRanges = new(Sharpness: new(0, 20)) });
        await fixture.RestartAsync();
        Assert.Equal(20, fixture.Service.GetSnapshot().ActiveProfile!.RangeFor("sharpness").Maximum);
        Assert.Equal(evidence, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        await fixture.Service.ReadDirectPictureAsync("sharpness");
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 21, true));
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, new Dictionary<string, int> { ["sharpness"] = 21 }, true));
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, new Dictionary<string, int> { ["sharpness"] = 20 }, true);
        Assert.Equal(20, fixture.Display.Sharpness);
    }

    [Fact]
    public async Task BatchStopsOnRejectedUnchangedRowWithoutRestoringOrSendingLaterRows()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await IpRemoteWorkspaceTests.VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "colorControl" ? Reject(request) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id,
            new Dictionary<string, int> { ["contrast"] = 44, ["color"] = 99, ["sharpness"] = 1 }, true));
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Empty(fixture.Display.WritesFor("sharpness"));
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Equal(new[] { IpRemoteBatchStepStage.Confirmed, IpRemoteBatchStepStage.RejectedUnchanged, IpRemoteBatchStepStage.NotSent }, fixture.Service.GetSnapshot().PictureBatch!.Steps.Select(step => step.Stage));
    }

    [Fact]
    public async Task DirectPageSurvivesReportedSharpness74ErrorAndCanApplyCorrectedValue()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(control: "sharpness");
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services);
        await page.StartAsync();
        await page.SelectControlAsync("sharpness");
        await page.ClickAsync("Read direct sharpness");
        await page.ChangeAsync("Target sharpness", "74");
        await page.SetCheckboxAsync("Confirm direct picture conditions", true);
        fixture.Display.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == "sharpnessControl" ? Reject(request) : null);
        await page.ClickAsync("Apply direct sharpness");
        await page.AssertTextAsync("original 0 is unchanged");
        await page.AssertDisabledAsync("Read direct sharpness", false);
        fixture.Display.Override = null;
        await page.ChangeAsync("Target sharpness", "1");
        await page.SetCheckboxAsync("Confirm direct picture conditions", true);
        await page.ClickAsync("Apply direct sharpness");
        Assert.Equal(1, fixture.Display.Sharpness);
    }
}
