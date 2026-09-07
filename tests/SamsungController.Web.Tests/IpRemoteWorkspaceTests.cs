using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteWorkspaceTests
{
    internal static async Task VerifyAllAsync(ContrastFixture fixture)
    {
        foreach (var control in SamsungIpRemotePictureControl.All) await fixture.VerifyAsync(control: control.Id);
        fixture.Display.Requests.Clear();
    }
    private static Dictionary<string, int> Targets() => new() { ["contrast"] = 44, ["color"] = 24, ["sharpness"] = 1 };
    private static string BatchPath(ContrastFixture fixture) => Path.Combine(fixture.DirectoryPath, "ip-remote", "picture-batch.json");

    [Fact]
    public async Task OneRefreshReadsAllControlsWithoutInventingMissingValues()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.RefreshWorkspaceAsync();
        var read = fixture.Service.GetSnapshot().WorkspaceReading!;
        Assert.Equal(new[] { "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        Assert.Equal(new int?[] { 45, 25, 0 }, SamsungIpRemotePictureControl.All.Select(control => read.Value(control.Id)));
        fixture.Display.VideoOverride = new() { ["contrast"] = 45, ["color"] = "25", ["sharpness"] = 101 };
        await fixture.Service.RefreshWorkspaceAsync();
        read = fixture.Service.GetSnapshot().WorkspaceReading!;
        Assert.Null(read.Value("color"));
        Assert.Null(read.Value("sharpness"));
        Assert.Null(read.Value("missing"));
    }

    [Fact]
    public async Task BatchUsesVerifiedSettersInOrderWithIndependentPreflightAndReadbackAndDurableOriginals()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Requests.Clear();
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>().EndsWith("Control", StringComparison.Ordinal))
            {
                var saved = JsonSerializer.Deserialize<IpRemotePictureBatch>(await File.ReadAllTextAsync(BatchPath(fixture), cancellation))!;
                Assert.Equal(new[] { 45, 25, 0 }, saved.Steps.Select(step => step.Original));
                var operation = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!;
                Assert.True(operation["WriteAttempted"]!.GetValue<bool>());
                Assert.Equal(saved.Steps.Single(step => step.Stage == IpRemoteBatchStepStage.Applying).OperationId,
                    operation["Id"]!.GetValue<Guid>());
            }
            return null;
        };
        await fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true);
        Assert.Equal(SamsungIpRemotePictureControl.All.SelectMany(control => new[] { "getTVStates", "getVideoStates", control.Method, "getTVStates", "getVideoStates" }), fixture.Display.Methods);
        var snapshot = fixture.Service.GetSnapshot();
        Assert.Equal(IpRemoteBatchStage.Completed, snapshot.PictureBatch!.Stage);
        Assert.All(snapshot.PictureBatch.Steps, step => Assert.Equal(IpRemoteBatchStepStage.Confirmed, step.Stage));
        Assert.Equal(new[] { 45, 25, 0 }, snapshot.PictureBatch.Steps.Select(step => step.Original));
        Assert.Equal(new int?[] { 44, 24, 1 }, SamsungIpRemotePictureControl.All.Select(control => snapshot.WorkspaceReading!.Value(control.Id)));
        Assert.Equal(3, snapshot.ControlCapabilities.Count);
        Assert.True(snapshot.PictureTest!.DirectChangeKept);
        var journal = await File.ReadAllTextAsync(BatchPath(fixture));
        Assert.DoesNotContain(ContrastFixture.Token, journal, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(BatchPath(fixture)));
        var report = fixture.Service.ExportReport();
        Assert.Contains("PictureBatch", report, StringComparison.Ordinal);
        Assert.Contains("WorkspaceReading", report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Token, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Profile.Connection.Host, report, StringComparison.Ordinal);
        fixture.Display.Requests.Clear();
        await fixture.RestartAsync();
        Assert.Empty(fixture.Display.Requests);
        Assert.Null(fixture.Service.GetSnapshot().WorkspaceReading);
        Assert.Equal(IpRemoteBatchStage.Completed, fixture.Service.GetSnapshot().PictureBatch!.Stage);
    }

    [Fact]
    public async Task UnchangedRowsAreSkippedAndSingleChangedControlCanBeApplied()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(control: "color");
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Requests.Clear();
        await fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id,
            new Dictionary<string, int> { ["contrast"] = 45, ["color"] = 24, ["sharpness"] = 0 }, true);
        Assert.Equal("color", Assert.Single(fixture.Service.GetSnapshot().PictureBatch!.Steps).Control);
        Assert.Equal(new[] { 24 }, fixture.Display.WritesFor("color"));
        Assert.Empty(fixture.Display.WritesFor("contrast"));
        Assert.Empty(fixture.Display.WritesFor("sharpness"));
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("unsupported")]
    [InlineData("range")]
    [InlineData("consent")]
    [InlineData("stale")]
    [InlineData("reading")]
    [InlineData("unchanged")]
    [InlineData("missing")]
    public async Task InvalidPlanIsRejectedBeforeAnyRequestIncludingInvalidLaterRows(string problem)
    {
        var time = new WorkspaceClock();
        using var fixture = await ContrastFixture.CreateAsync(time);
        if (problem == "unverified") await fixture.VerifyAsync();
        else await VerifyAllAsync(fixture);
        if (problem == "missing") fixture.Display.VideoOverride = new() { ["contrast"] = 45, ["color"] = 25 };
        await fixture.Service.RefreshWorkspaceAsync();
        var read = fixture.Service.GetSnapshot().WorkspaceReading!;
        var targets = Targets();
        if (problem == "unsupported") targets["brightness"] = 1;
        if (problem == "range") targets["sharpness"] = 101;
        if (problem == "unchanged") targets = new() { ["contrast"] = 45 };
        if (problem == "stale") time.Now += TimeSpan.FromMinutes(3);
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(
            problem == "reading" ? Guid.NewGuid() : read.Id, targets, problem != "consent"));
        Assert.Empty(fixture.Display.Requests);
        Assert.Null(fixture.Service.GetSnapshot().PictureBatch);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("value")]
    public async Task ExternalDriftBetweenRowsKeepsFirstChangeAndDoesNotSendLaterWrites(string drift)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        var tvReads = 0;
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "getTVStates" && ++tvReads == 3)
            {
                if (drift == "input") fixture.Display.Input = "HDMI2";
                if (drift == "mode") fixture.Display.Mode = "Standard";
                if (drift == "value") fixture.Display.Color = 30;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Empty(fixture.Display.WritesFor("color"));
        Assert.Empty(fixture.Display.WritesFor("sharpness"));
        Assert.Equal(new[] { IpRemoteBatchStepStage.Confirmed, IpRemoteBatchStepStage.NotSent, IpRemoteBatchStepStage.NotSent }, fixture.Service.GetSnapshot().PictureBatch!.Steps.Select(step => step.Stage));
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Null(fixture.Service.GetSnapshot().WorkspaceReading);
    }

    [Fact]
    public async Task SetterSuccessWithoutActualChangeStopsTheBatchAndRequiresReadFirstRecovery()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request["method"]!.GetValue<string>() == "colorControl" ? ContrastDisplay.Reply(request, new JsonObject { ["color"] = 24 }) : null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Contains("Readback did not confirm", error.Message, StringComparison.Ordinal);
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Empty(fixture.Display.WritesFor("sharpness"));
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        fixture.Display.Override = null;
        await fixture.Service.RestorePictureTestAsync(fixture.Service.GetSnapshot().PictureTest!.Id);
        Assert.Single(fixture.Display.WritesFor("color")); // Already original: recovery reads but does not rewrite it.
        Assert.Equal(44, fixture.Display.Contrast);
    }

    [Fact]
    public async Task AmbiguousSecondWriteStopsThirdAndRestartRecoveryRestoresOnlyTheUncertainControl()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != "colorControl") return Task.FromResult<HttpResponseMessage?>(null);
            fixture.Display.Color = 24;
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Equal(new[] { IpRemoteBatchStepStage.Confirmed, IpRemoteBatchStepStage.Uncertain, IpRemoteBatchStepStage.NotSent }, fixture.Service.GetSnapshot().PictureBatch!.Steps.Select(step => step.Stage));
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Empty(fixture.Display.WritesFor("sharpness"));
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        fixture.Display.Override = null;
        await fixture.Service.RestorePictureTestAsync(fixture.Service.GetSnapshot().PictureTest!.Id);
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(new[] { 24, 25 }, fixture.Display.WritesFor("color"));
        Assert.Equal(3, fixture.Service.GetSnapshot().ControlCapabilities.Count);
    }

    [Fact]
    public async Task StopDuringDeliveredWriteDoesNotRetryContinueOrAutomaticallyUndo()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "contrastControl")
            { fixture.Display.Contrast = 44; fixture.Service.Cancel(); return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject { ["contrast"] = 44 })); }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Equal(new[] { 44 }, fixture.Display.Writes);
        Assert.Empty(fixture.Display.WritesFor("color"));
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Equal(IpRemoteBatchStepStage.Uncertain, fixture.Service.GetSnapshot().PictureBatch!.Steps[0].Stage);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
    }

    [Fact]
    public async Task BatchGateCoversAllRowsAndSubmittedTargetsCannotChangeMidBatch()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() == "contrastControl")
            { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
            return null;
        };
        var targets = Targets();
        var batch = fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, targets, true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshWorkspaceAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(ContrastFixture.Profile));
            targets["color"] = 100;
        }
        finally { release.TrySetResult(); }
        await batch;
        Assert.Equal(24, fixture.Display.Color);
        Assert.Equal(1, fixture.Display.Sharpness);
    }

    [Fact]
    public async Task FailureToSaveOriginalsPreventsAllTvRequests()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Requests.Clear();
        Directory.CreateDirectory(BatchPath(fixture) + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Empty(fixture.Display.Requests);
        Assert.NotNull(fixture.Service.GetSnapshot().StorageWarning);
    }

    [Fact]
    public async Task ProgressStorageFailureAfterReadbackKeepsConfirmedResultAndStopsNextRow()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await VerifyAllAsync(fixture);
        await fixture.Service.RefreshWorkspaceAsync();
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() == "getVideoStates" && fixture.Display.Contrast == 44)
                Directory.CreateDirectory(BatchPath(fixture) + ".tmp");
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ApplyPictureBatchAsync(fixture.Service.GetSnapshot().WorkspaceReading!.Id, Targets(), true));
        Assert.Equal(IpRemoteBatchStepStage.Confirmed, fixture.Service.GetSnapshot().PictureBatch!.Steps[0].Stage);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Empty(fixture.Display.WritesFor("color"));
        fixture.Display.Requests.Clear();
        await fixture.RestartAsync();
        Assert.Empty(fixture.Display.Requests);
        Assert.Equal(IpRemoteBatchStage.Stopped, fixture.Service.GetSnapshot().PictureBatch!.Stage);
        Assert.Equal(new[] { IpRemoteBatchStepStage.Confirmed, IpRemoteBatchStepStage.NotSent, IpRemoteBatchStepStage.NotSent }, fixture.Service.GetSnapshot().PictureBatch!.Steps.Select(step => step.Stage));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("direct")]
    [InlineData("profile")]
    [InlineData("prepare")]
    public async Task OtherOperationsInvalidateWorkspaceReadings(string operation)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.RefreshWorkspaceAsync();
        switch (operation)
        {
            case "read": await fixture.Service.ReadBothAsync("test"); break;
            case "direct": await fixture.Service.ReadDirectPictureAsync("color"); break;
            case "profile": await fixture.Service.SaveProfileAsync(ContrastFixture.Profile); break;
            case "prepare": await fixture.Service.PreparePictureTestAsync("color"); break;
        }
        Assert.Null(fixture.Service.GetSnapshot().WorkspaceReading);
    }

    private sealed class WorkspaceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
