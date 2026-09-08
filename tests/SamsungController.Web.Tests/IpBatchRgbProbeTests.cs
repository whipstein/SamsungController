using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpBatchRgbProbeTests
{
    private static readonly string[] Fields = ["WB20P.Red", "WB20P.Green", "WB20P.Blue"];

    [Fact]
    public async Task ReadBatchOnlySendsOneReadEnvelopeAndDoesNotConsumeItsValuesAsMenuState()
    {
        using var fixture = await CreateAsync();
        var before = fixture.Display.Requests.Count;
        var menu = fixture.Service.GetSnapshot().Menu;
        await fixture.Service.ProbeReadBatchAsync();
        Assert.Equal(before, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Writes);
        Assert.Single(fixture.Display.Batches);
        Assert.True(fixture.Service.CanProbeRgbBatch);
        Assert.Equal(menu, fixture.Service.GetSnapshot().Menu);
        Assert.Equal("query", IpCommunicationLog.Kind(fixture.Service.GetSnapshot().Observations.Last()));
        await fixture.Service.DisconnectMenuAsync();
        await fixture.Service.ConnectMenuAsync(false);
        Assert.False(fixture.Service.CanProbeRgbBatch);
    }

    [Fact]
    public async Task RgbBatchIsOnePostAtPreparedPercentageThenIndependentReadbackAndExplicitRestore()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.ProbeReadBatchAsync();
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var prepared = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Equal("10%", prepared.OriginalInterval);
        Assert.Equal(new[] { -2, 0, 50 }, Fields.Select(field => prepared.Originals[field]));
        Assert.Equal(new[] { -1, 1, 49 }, Fields.Select(field => prepared.Targets[field]));
        Assert.All(fixture.Writes, request => Assert.Equal("WB20P.IntervalControl", request["method"]!.ToString()));
        await fixture.Service.SendRgbProbeAsync(prepared.Id, false, true);
        var observed = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Equal(IpRgbProbeStage.Observed, observed.Stage);
        Assert.Contains("All three targets independently confirmed", observed.Message);
        Assert.Equal(observed.Targets.Values.Select(value => (int?)value), observed.Readback.Values);
        Assert.Equal(2, fixture.Display.Batches.Count);
        var batch = fixture.Display.Batches.Last();
        Assert.Equal(3, batch.Count);
        Assert.DoesNotContain("Interval", batch.ToJsonString());
        Assert.DoesNotContain(fixture.Writes, request => Fields.Any(field => request["method"]!.ToString() == field + "Control"));
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString()); // No hidden restore.
        Assert.True(observed.NeedsRecovery);
        Assert.Equal("write", IpCommunicationLog.Kind(fixture.Service.GetSnapshot().Observations.Single(item => item.Exchange.Method == "batch:WB20P.RGB")));
        await fixture.Service.RestoreRgbProbeAsync(prepared.Id);
        var restored = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Equal(IpRgbProbeStage.Restored, restored.Stage);
        Assert.Equal("10%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.All(Fields, field => Assert.Equal(prepared.Originals[field], fixture.GridValues["white20/50%"][field]!.GetValue<int>()));
        Assert.Equal(3, fixture.Writes.Count(request => Fields.Any(field => request["method"]!.ToString() == field + "Control")));
        Assert.Equal(2, fixture.Display.Batches.Count); // No fallback or batch retry even on restore.
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleMethodDetectsIgnoredFieldsAndCanWorkWithoutBatchSupport(bool acceptAll)
    {
        using var fixture = await CreateAsync();
        fixture.Override = (request, _) =>
        {
            if (!acceptAll || request["method"]!.ToString() != "WB20P.RedControl" || request["params"]!.AsObject().Count != 4)
                return Task.FromResult<HttpResponseMessage?>(null);
            foreach (var field in Fields) fixture.GridValues["white20/50%"][field] = request["params"]![field]!.DeepClone();
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject()));
        };
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var id = fixture.Service.GetSnapshot().RgbProbe!.Id;
        await fixture.Service.SendRgbProbeAsync(id, true, true);
        var probe = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Contains(acceptAll ? "All three targets independently confirmed" : "Not verified: 1/3", probe.Message);
        Assert.Empty(fixture.Display.Batches);
        var write = Assert.Single(fixture.Writes, request => Fields.Any(field => request["method"]!.ToString() == field + "Control"));
        Assert.Equal(4, write["params"]!.AsObject().Count);
        Assert.True(probe.NeedsRecovery);
        var requests = fixture.Display.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendRgbProbeAsync(id, true, true));
        Assert.Equal(requests, fixture.Display.Requests.Count);
        await fixture.Service.RestoreRgbProbeAsync(id);
        Assert.Equal(acceptAll ? 4 : 2, fixture.Writes.Count(request => Fields.Any(field => request["method"]!.ToString() == field + "Control")));
    }

    [Fact]
    public async Task RejectingOneBatchMemberNeverTriggersFallbackAndReadbackReportsPartialChange()
    {
        using var fixture = await CreateAsync();
        AcceptBatches(fixture, failGreen: true);
        await fixture.Service.ProbeReadBatchAsync();
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var id = fixture.Service.GetSnapshot().RgbProbe!.Id;
        await fixture.Service.SendRgbProbeAsync(id, false, true);
        var probe = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Equal(SamsungIpRemoteOutcome.RpcError, probe.Exchange!.Outcome);
        Assert.Contains("Not verified: 2/3", probe.Message);
        Assert.DoesNotContain(fixture.Writes, request => Fields.Any(field => request["method"]!.ToString() == field + "Control"));
        await fixture.Service.RestoreRgbProbeAsync(id);
        Assert.Equal(2, fixture.Writes.Count(request => Fields.Any(field => request["method"]!.ToString() == field + "Control")));
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("id")]
    [InlineData("batch")]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("rgb")]
    [InlineData("selector")]
    public async Task UnsafeOrUnconfirmedPreparationNeverSendsExperimentalRgb(string change)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var id = fixture.Service.GetSnapshot().RgbProbe!.Id;
        switch (change)
        {
            case "input": fixture.Display.Input = "HDMI1"; break;
            case "mode": fixture.Display.Mode = "Standard"; break;
            case "rgb": fixture.GridValues["white20/50%"]["WB20P.Blue"] = 45; break;
            case "selector": fixture.Values["WB20P.Interval"] = "10%"; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendRgbProbeAsync(change == "id" ? Guid.NewGuid() : id, change != "batch", change != "consent"));
        Assert.Empty(fixture.Display.Batches);
        Assert.All(fixture.Writes, request => Assert.Equal("WB20P.IntervalControl", request["method"]!.ToString()));
        Assert.False(fixture.Service.GetSnapshot().RgbProbe!.WriteAttempted);
    }

    [Fact]
    public async Task MissingOriginalStopsWithoutInventingValuesAndPreparedTestLocksOtherWrites()
    {
        using var fixture = await CreateAsync();
        fixture.GridValues["white20/50%"]["WB20P.Green"] = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareRgbProbeAsync("50%"));
        var probe = fixture.Service.GetSnapshot().RgbProbe!;
        Assert.Empty(probe.Originals);
        Assert.Equal(IpRgbProbeStage.Stopped, probe.Stage);
        var before = fixture.Display.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendMenuKeyAsync("enter"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshMenuGridAsync("color"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareRgbProbeAsync("10%"));
        Assert.Equal(before, fixture.Display.Requests.Count);
        await fixture.Service.RestoreRgbProbeAsync(probe.Id);
        Assert.Equal("10%", fixture.Values["WB20P.Interval"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedDeliveredWritePersistsOriginalsNeverResumesAndCanRecoverAfterReconnect(bool cancel)
    {
        using var fixture = await CreateAsync();
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var probe = fixture.Service.GetSnapshot().RgbProbe!;
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB20P.RedControl" || request["params"]!.AsObject().Count != 4)
                return Task.FromResult<HttpResponseMessage?>(null);
            fixture.GridValues["white20/50%"]["WB20P.Red"] = probe.Targets["WB20P.Red"];
            if (cancel) { fixture.Service.Cancel(); throw new OperationCanceledException(); }
            throw new HttpRequestException("Simulated lost reply after delivery");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.SendRgbProbeAsync(probe.Id, true, true));
        Assert.True(fixture.Service.GetSnapshot().RgbProbe!.NeedsRecovery);
        Assert.Equal(4, fixture.Display.Requests.Last()["params"]!.AsObject().Count); // No late read or restore.
        var before = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(before, fixture.Display.Requests.Count);
        Assert.Equal(IpRgbProbeStage.Stopped, fixture.Service.GetSnapshot().RgbProbe!.Stage);
        Assert.Equal(probe.Originals, fixture.Service.GetSnapshot().RgbProbe!.Originals);
        fixture.Override = null;
        var writes = fixture.Writes.Count();
        await fixture.Service.ConnectMenuAsync(); // Default all-settings load must not move selectors.
        Assert.Equal(writes, fixture.Writes.Count());
        await fixture.Service.RestoreRgbProbeAsync(probe.Id);
        Assert.False(fixture.Service.GetSnapshot().RgbProbe!.NeedsRecovery);
        Assert.Equal("10%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.Equal(-2, fixture.GridValues["white20/50%"]["WB20P.Red"]!.GetValue<int>());
    }

    [Fact]
    public async Task RestoreRefusesUnexpectedChangesAndManualCloseSendsNothing()
    {
        using var fixture = await CreateAsync();
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var id = fixture.Service.GetSnapshot().RgbProbe!.Id;
        await fixture.Service.SendRgbProbeAsync(id, true, true);
        fixture.GridValues["white20/50%"]["WB20P.Green"] = 10;
        var writes = fixture.Writes.Count();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RestoreRgbProbeAsync(id));
        Assert.Equal(writes, fixture.Writes.Count());
        await fixture.Service.DisconnectMenuAsync();
        var requests = fixture.Display.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.CloseRgbProbeManuallyAsync(id, false));
        await fixture.Service.CloseRgbProbeManuallyAsync(id, true);
        Assert.Equal(requests, fixture.Display.Requests.Count);
        Assert.Equal(IpRgbProbeStage.ManuallyClosed, fixture.Service.GetSnapshot().RgbProbe!.Stage);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
    }

    [Fact]
    public async Task GuidedPageRequiresConfirmationShowsReadbackExportsAndGatesRemoteUntilRestore()
    {
        using var fixture = await CreateAsync();
        var js = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(BatchRgbTest));
        var before = fixture.Display.Requests.Count;
        await renderer.StartAsync();
        Assert.Equal(before, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Display.Batches);
        await renderer.ClickAsync("Run read-only batch test");
        await renderer.AssertTextAsync("Read batch accepted");
        await renderer.SelectAsync("RGB test percentage", "50%");
        await renderer.ClickAsync("Prepare RGB test");
        await renderer.AssertDisabledAsync("Test RGB batch — one HTTP request", true);
        await renderer.AssertDisabledAsync("Test RGB fields — one method", true);
        await renderer.SetCheckboxAsync("Confirm RGB test conditions", true);
        await renderer.AssertDisabledAsync("Test RGB batch — one HTTP request", false);
        await using var remote = new IpRemotePageTests.IpPageRenderer(services, typeof(Components.Shared.DirectRemote));
        await remote.StartAsync();
        await remote.AssertDisabledAsync("OK", true);
        await renderer.ClickAsync("Test RGB batch — one HTTP request");
        await renderer.AssertTextAsync("All three targets independently confirmed");
        await renderer.AssertCheckboxAsync("Confirm RGB test conditions", true);
        await renderer.AssertDisabledAsync("Test RGB batch — one HTTP request", true);
        await renderer.ClickAsync("Restore originals and original percentage");
        await remote.AssertDisabledAsync("OK", false);
        await renderer.AssertTextAsync("Original RGB values and original percentage were independently confirmed");
        await renderer.ClickAsync("Download batch / RGB report");
        Assert.Contains("Readback", js.Download);
        Assert.Contains("batch:WB20P.RGB", js.Download);
        Assert.DoesNotContain(ContrastFixture.Token, js.Download);
        Assert.DoesNotContain(ContrastFixture.Profile.Connection.Host, js.Download);
        Assert.Equal(0, js.ConfirmCalls);
    }

    private static async Task<MenuFixture> CreateAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        fixture.Values["WB20PointMode"] = "On";
        fixture.Values["WB20P.Interval"] = "10%";
        fixture.GridValues["white20/50%"] = new() { ["WB20P.Red"] = -2, ["WB20P.Green"] = 0, ["WB20P.Blue"] = 50 };
        AcceptBatches(fixture);
        await fixture.Service.ConnectMenuAsync(false);
        return fixture;
    }

    [Fact]
    public async Task RejectedReadBatchLeavesSingleMethodAvailableAndPageErrorsRemainRecoverable()
    {
        using var fixture = await CreateAsync();
        fixture.Display.BatchOverride = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32004,\"message\":\"Not supported\"}}") });
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(BatchRgbTest));
        await renderer.StartAsync();
        await renderer.ClickAsync("Run read-only batch test");
        await renderer.AssertTextAsync("Read batch not verified");
        Assert.Empty(fixture.Writes);
        fixture.Values["WB20PointMode"] = "Off";
        await renderer.ClickAsync("Prepare RGB test");
        await renderer.AssertTextAsync("Requires WB20PointMode: On");
        Assert.Null(fixture.Service.GetSnapshot().RgbProbe);
        Assert.Empty(fixture.Writes);
        fixture.Values["WB20PointMode"] = "On";
        await renderer.ClickAsync("Prepare RGB test");
        await renderer.SetCheckboxAsync("Confirm RGB test conditions", true);
        await renderer.AssertDisabledAsync("Test RGB batch — one HTTP request", true);
        await renderer.AssertDisabledAsync("Test RGB fields — one method", false);
        await renderer.ClickAsync("Test RGB fields — one method");
        await renderer.AssertTextAsync("Not verified: 1/3");
        await renderer.ClickAsync("Restore originals and original percentage");
        Assert.Single(fixture.Display.Batches);
    }

    private static void AcceptBatches(MenuFixture fixture, bool failGreen = false)
    {
        fixture.Display.BatchOverride = (batch, _) =>
        {
            var replies = new JsonArray();
            foreach (var call in batch.Reverse()) // Deliberately reordered; selector cannot be in batch.
            {
                var method = call!["method"]!.ToString();
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = call["id"]!.ToString() };
                if (failGreen && method == "WB20P.GreenControl") reply["error"] = new JsonObject { ["code"] = -32002, ["message"] = "Simulated member rejection" };
                else
                {
                    reply["result"] = new JsonObject();
                    if (method is not ("getTVStates" or "getVideoStates"))
                    {
                        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
                        var field = Assert.Single(Fields, field => method == field + "Control");
                        fixture.GridValues["white20/50%"][field] = call["params"]![field]!.DeepClone();
                    }
                }
                replies.Add(reply);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies.ToJsonString()) });
        };
    }
}
