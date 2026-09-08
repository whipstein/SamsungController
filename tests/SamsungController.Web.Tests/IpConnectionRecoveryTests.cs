using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Shared;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpConnectionRecoveryTests
{
    [Fact]
    public async Task TimedOutBatchIsQuarantinedAndResetReusesTokenWithoutPairingOrLoadingCalibration()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { Connection = ContrastFixture.Profile.Connection with { RequestTimeout = TimeSpan.FromMilliseconds(100) } });
        await fixture.Service.ConnectMenuAsync(false);
        var tokens = Path.Combine(Path.GetDirectoryName(fixture.JournalPath)!, "tokens.json");
        var credentialBefore = await File.ReadAllTextAsync(tokens);
        fixture.Display.BatchOverride = async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("Unreachable simulated timeout");
        };
        await fixture.Service.ProbeReadBatchAsync();
        Assert.Equal(SamsungIpRemoteOutcome.Timeout, fixture.Service.GetSnapshot().ReadBatchProbe!.Exchange.Outcome);
        Assert.False(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.False(fixture.Service.GetSnapshot().AuthorizationRejected);
        Assert.True(fixture.Service.GetSnapshot().HasToken);
        Assert.NotNull(fixture.Service.BatchProbeDisabledReason);
        var before = fixture.Display.Requests.Count;
        await fixture.Service.ResetMenuConnectionAsync();
        Assert.Equal(new[] { "getTVStates", "getVideoStates" }, fixture.Display.Methods.Skip(before));
        Assert.Empty(fixture.Writes);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.Null(fixture.Service.GetSnapshot().Menu.SettingsLoadedAt);
        Assert.Equal(credentialBefore, await File.ReadAllTextAsync(tokens));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ProbeReadBatchAsync);
        Assert.Single(fixture.Display.Batches);
        var history = await File.ReadAllTextAsync(fixture.Service.DiagnosticLogPath);
        before = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(before, fixture.Display.Requests.Count);
        Assert.Equal(history, await File.ReadAllTextAsync(fixture.Service.DiagnosticLogPath));
        Assert.Contains("Timeout", fixture.Service.BatchProbeDisabledReason);
        await fixture.Service.ResetMenuConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ProbeReadBatchAsync);
        Assert.Single(fixture.Display.Batches);
        Assert.Equal(credentialBefore, await File.ReadAllTextAsync(tokens));
        // Changing profile annotations/reconnecting does not erase the endpoint's
        // failed experiment. Other saved displays remain independent.
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { Firmware = "different firmware note" });
        Assert.NotNull(fixture.Service.BatchProbeDisabledReason);
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { Connection = ContrastFixture.Profile.Connection with { Host = "192.0.2.11" } });
        Assert.Null(fixture.Service.BatchProbeDisabledReason);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("unauthorized")]
    [InlineData("transport")]
    [InlineData("second-read-fails")]
    public async Task RecoveryCanRecheckRejectedTokenButOnlyClearsRejectionAfterBothReadsSucceed(string recovery)
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32010));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectMenuAsync(false));
        Assert.True(fixture.Service.GetSnapshot().AuthorizationRejected);
        fixture.Override = (request, _) =>
        {
            if (recovery == "transport") throw new HttpRequestException("Simulated TV still unreachable");
            return Task.FromResult<HttpResponseMessage?>(recovery == "unauthorized" ? MenuFixture.Reject(request, -32010)
                : recovery == "second-read-fails" && request["method"]!.ToString() == "getVideoStates" ? MenuFixture.Reject(request, -32002) : null);
        };
        var before = fixture.Display.Requests.Count;
        if (recovery == "success") await fixture.Service.ResetMenuConnectionAsync();
        else await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ResetMenuConnectionAsync);
        var snapshot = fixture.Service.GetSnapshot();
        Assert.Equal(recovery == "success", snapshot.Menu.Connected);
        Assert.Equal(recovery != "success", snapshot.AuthorizationRejected);
        Assert.True(snapshot.HasToken);
        Assert.False(snapshot.IsBusy);
        Assert.Equal(recovery is "success" or "second-read-fails" ? 2 : 1, fixture.Display.Requests.Count - before);
        Assert.Empty(fixture.Writes);
        Assert.DoesNotContain("createAccessToken", fixture.Display.Methods);
        fixture.Override = null;
        await fixture.Service.ConnectMenuAsync(false); // Normal Connect can also explicitly retry a rejected saved token.
        Assert.False(fixture.Service.GetSnapshot().AuthorizationRejected);
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
    }

    [Fact]
    public async Task ResetKeepsUnfinishedRgbOriginalsAndDoesNotMoveItsSelectorOrResumeTheTest()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["WB20PointMode"] = "On";
        fixture.Values["WB20P.Interval"] = "10%";
        fixture.GridValues["white20/50%"] = new() { ["WB20P.Red"] = 1, ["WB20P.Green"] = 2, ["WB20P.Blue"] = 3 };
        await fixture.Service.ConnectMenuAsync(false);
        await fixture.Service.PrepareRgbProbeAsync("50%");
        var probe = fixture.Service.GetSnapshot().RgbProbe!;
        var writes = fixture.Writes.Count();
        var before = fixture.Display.Requests.Count;
        await fixture.Service.ResetMenuConnectionAsync();
        Assert.Equal(probe, fixture.Service.GetSnapshot().RgbProbe);
        Assert.True(probe.NeedsRecovery);
        Assert.Equal("50%", fixture.Values["WB20P.Interval"]!.ToString());
        Assert.Equal(writes, fixture.Writes.Count());
        Assert.Equal(new[] { "getTVStates", "getVideoStates" }, fixture.Display.Methods.Skip(before));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendMenuKeyAsync("enter"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendRgbProbeAsync(probe.Id, true, true));
        await fixture.Service.RestoreRgbProbeAsync(probe.Id);
        Assert.Equal("10%", fixture.Values["WB20P.Interval"]!.ToString());
    }

    [Fact]
    public async Task ResetWithoutTokenDoesNotAttemptPairingOrAnyRpc()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ForgetTokenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ResetMenuConnectionAsync);
        Assert.Empty(fixture.Display.Requests);
        Assert.Empty(fixture.Display.Batches);
        Assert.False(fixture.Service.GetSnapshot().HasToken);
    }

    [Fact]
    public async Task RecoveryButtonIsExplicitAndShowsFailureOrSuccessWithoutLaunchingOtherCommands()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(ConnectionRecovery));
        await renderer.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await renderer.AssertDisabledAsync("Reset connection (keep pairing)", false);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32010));
        await renderer.ClickAsync("Reset connection (keep pairing)");
        await renderer.AssertTextAsync("-32010");
        await renderer.AssertDisabledAsync("Reset connection (keep pairing)", false);
        fixture.Override = null;
        await renderer.ClickAsync("Reset connection (keep pairing)");
        await renderer.AssertTextAsync("Connection reset succeeded using the saved pairing");
        Assert.Equal(new[] { "getTVStates", "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        Assert.Empty(fixture.Writes);
        Assert.False(fixture.Service.GetSnapshot().AuthorizationRejected);
    }

    [Fact]
    public async Task StopLeavesResetAvailableWithoutOverlappingTheRunningRequest()
    {
        using var fixture = await MenuFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (_, cancellation) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        };
        var running = fixture.Service.ResetMenuConnectionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ResetMenuConnectionAsync);
        fixture.Service.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
        Assert.Single(fixture.Display.Requests);
        fixture.Override = null;
        await fixture.Service.ResetMenuConnectionAsync();
        Assert.True(fixture.Service.GetSnapshot().Menu.Connected);
        Assert.Equal(3, fixture.Display.Requests.Count);
    }
}
