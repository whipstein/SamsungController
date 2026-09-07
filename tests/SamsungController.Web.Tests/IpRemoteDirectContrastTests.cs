using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemoteDirectPictureTests
{
    [Fact]
    public async Task DirectApplyKeepsNewValueAndExplicitUndoRestoresWithoutOverwritingVerification()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var evidence = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        fixture.Display.Requests.Clear();
        await fixture.Service.ReadDirectPictureAsync();
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 40, true);
        Assert.Equal(new[] { 40 }, fixture.Display.Writes);
        Assert.Equal(40, fixture.Display.Contrast);
        var operation = fixture.Service.GetSnapshot().PictureTest!;
        Assert.True(operation.DirectChangeKept);
        Assert.False(operation.RequiresRecovery);
        Assert.False(operation.Verified);
        Assert.Equal(IpRemotePicturePurpose.DirectAdjustment, operation.Purpose);
        Assert.Equal(evidence, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        Assert.Equal(40, fixture.Service.GetSnapshot().DirectPictureReading!.Value);
        Assert.Equal(new[] { "getTVStates", "getVideoStates", "getTVStates", "getVideoStates", "contrastControl", "getTVStates", "getVideoStates" }, fixture.Display.Methods);
        await fixture.Service.UndoDirectPictureAsync(operation.Id, true);
        Assert.Equal(new[] { 40, 45 }, fixture.Display.Writes);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RestorationConfirmed);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.DirectChangeKept);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.Equal(evidence, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        Assert.Equal(45, fixture.Service.GetSnapshot().DirectPictureReading!.Value);
        var report = fixture.Service.ExportReport();
        Assert.DoesNotContain(ContrastFixture.Token, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Profile.Connection.Host, report, StringComparison.Ordinal);
        Assert.True(JsonNode.Parse(report)!["ControlCapabilities"]![0]!["WriteVerified"]!.GetValue<bool>());
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Equal(evidence, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        Assert.Null(fixture.Service.GetSnapshot().DirectPictureReading);
    }

    [Fact]
    public async Task SuccessfulAdjustmentSurvivesRestartWithoutRecoveryOrRequestsAndCanStillBeUndone()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 42, true);
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.DirectChangeKept);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        await fixture.Service.UndoDirectPictureAsync(id, true);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Fact]
    public async Task EarlierCompletedJournalIsPromotedWithoutNetworkButFailedVisualTestIsNot()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync(false);
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.ReadDirectPictureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 44, true));
        await fixture.VerifyAsync();
        var oldTest = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath))!.AsObject();
        oldTest.Remove("Purpose"); oldTest.Remove("DirectChangeKept");
        await File.WriteAllTextAsync(fixture.JournalPath, oldTest.ToJsonString());
        File.Delete(Path.Combine(fixture.DirectoryPath, "ip-remote", "control-capabilities.json"));
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Equal(count, fixture.Display.Requests.Count);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("signal")]
    [InlineData("model")]
    [InlineData("annotated-mode")]
    [InlineData("annotated-input")]
    [InlineData("reported-mode")]
    [InlineData("reported-input")]
    [InlineData("host")]
    [InlineData("port")]
    public async Task CapabilityNeverUnlocksAnotherContext(string change)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var profile = ContrastFixture.Profile;
        profile = change switch
        {
            "firmware" => profile with { Firmware = "1302" },
            "signal" => profile with { Signal = "HDR, 10-bit" },
            "model" => profile with { Model = "Another display" },
            "annotated-mode" => profile with { PictureMode = "Standard" },
            "annotated-input" => profile with { InputSource = "Another HDMI source" },
            "host" => profile with { Connection = profile.Connection with { Host = "192.0.2.11" } },
            "port" => profile with { Connection = profile.Connection with { Port = 1515 } },
            _ => profile
        };
        if (change == "reported-mode") fixture.Display.Mode = "Standard";
        if (change == "reported-input") fixture.Display.Input = "HDMI1";
        await new PrivateIpRemoteTokenStore(Path.Combine(fixture.DirectoryPath, "ip-remote")).SaveAsync(profile.Endpoint, ContrastFixture.Token);
        await fixture.Service.SaveProfileAsync(profile);
        await fixture.Service.ReadDirectPictureAsync();
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 44, true));
        Assert.Empty(fixture.Display.Requests);
        Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
    }

    [Fact]
    public async Task CapabilitiesAccumulateForVerifiedContextsInsteadOfReplacingEachOther()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var first = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with { Signal = "10-bit RGB" });
        await fixture.VerifyAsync();
        Assert.Equal(2, fixture.Service.GetSnapshot().ControlCapabilities.Count);
        await fixture.RestartAsync();
        Assert.Contains(first, fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile);
        await fixture.Service.ReadDirectPictureAsync();
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 44, true);
        Assert.Equal(44, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("mode")]
    [InlineData("contrast")]
    [InlineData("color")]
    public async Task FreshPreflightRejectsDriftWithoutLosingHistoricalCapability(string change)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        var id = fixture.Service.GetSnapshot().DirectPictureReading!.Id;
        switch (change)
        {
            case "input": fixture.Display.Input = "HDMI1"; break;
            case "mode": fixture.Display.Mode = "Standard"; break;
            case "contrast": fixture.Display.Contrast = 43; break;
            default: fixture.Display.Color = 26; break;
        }
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(id, 40, true));
        Assert.Empty(fixture.Display.Writes);
        Assert.Null(fixture.Service.GetSnapshot().DirectPictureReading);
        Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
    }

    [Theory]
    [InlineData(44, false)]
    [InlineData(-1, true)]
    [InlineData(101, true)]
    [InlineData(45, true)]
    public async Task ConsentAndTargetValidationPrecedeNetworkRequests(int target, bool confirmed)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, target, confirmed));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task ExpiredOrReplacedReadCannotWrite()
    {
        var clock = new TestClock();
        using var fixture = await ContrastFixture.CreateAsync(clock);
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        var id = fixture.Service.GetSnapshot().DirectPictureReading!.Id;
        clock.Now += TimeSpan.FromMinutes(3);
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(id, 44, true));
        Assert.Empty(fixture.Display.Requests);
        await fixture.Service.ReadDirectPictureAsync();
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(id, 44, true));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task FailedRefreshClearsOldValueRatherThanPresentingItAsCurrent()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        fixture.Display.VideoOverride = new JsonObject();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReadDirectPictureAsync());
        Assert.Null(fixture.Service.GetSnapshot().DirectPictureReading);
        Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
    }

    [Fact]
    public async Task CancelDuringApplyPreservesRecoveryOriginalAndSavedCapabilityAcrossRestart()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var evidence = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.ReadDirectPictureAsync();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Display.Requests.Clear();
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() != "contrastControl") return null;
            fixture.Display.Contrast = 40;
            var journal = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath, cancellation))!;
            Assert.Equal(45, journal["Original"]!.GetValue<int>());
            Assert.Equal("DirectAdjustment", journal["Purpose"]!.GetValue<string>());
            sent.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return null;
        };
        var applying = fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 40, true);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReadDirectPictureAsync());
        fixture.Service.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => applying);
        Assert.Equal(new[] { 40 }, fixture.Display.Writes);
        Assert.Equal(3, fixture.Display.Requests.Count);
        Assert.Null(fixture.Service.GetSnapshot().DirectPictureReading);
        fixture.Display.Override = null;
        await fixture.RestartAsync();
        Assert.Equal(3, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        await fixture.Service.RestorePictureTestAsync(fixture.Service.GetSnapshot().PictureTest!.Id);
        Assert.Equal(new[] { 40, 45 }, fixture.Display.Writes);
        Assert.Equal(evidence, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
    }

    [Fact]
    public async Task UndoRequiresConsentAndRefusesAnUnrelatedNewerValueWithoutOpeningRecovery()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 40, true);
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UndoDirectPictureAsync(id, false));
        Assert.Empty(fixture.Display.Requests);
        fixture.Display.Contrast = 39;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UndoDirectPictureAsync(id, true));
        Assert.Empty(fixture.Display.Writes);
        Assert.Equal(39, fixture.Display.Contrast);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.DirectChangeKept);
    }

    [Fact]
    public async Task CorruptCapabilityFileFailsClosedWithoutOverwritingIt()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var path = Path.Combine(fixture.DirectoryPath, "ip-remote", "control-capabilities.json");
        await File.WriteAllTextAsync(path, "[{}]");
        await Assert.ThrowsAsync<JsonException>(() => fixture.RestartAsync());
        Assert.Empty(fixture.Display.Requests);
        Assert.False(fixture.Service.GetSnapshot().Initialized);
        Assert.Equal("[{}]", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectUndoCannotActOnAnUnrelatedPendingVerification(bool confirmed)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UndoDirectPictureAsync(id, confirmed));
        Assert.Empty(fixture.Display.Requests);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
    }

    [Fact]
    public async Task NewEvidenceAndDirectReadingExportsRedactPinsWithoutChangingPrivateRecords()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var pin = new string('A', 64);
        await fixture.Service.SaveProfileAsync(ContrastFixture.Profile with
        {
            Connection = ContrastFixture.Profile.Connection with { CertificateSha256 = pin }
        });
        await fixture.VerifyAsync();
        await fixture.Service.ReadDirectPictureAsync();
        await fixture.Service.ApplyDirectPictureAsync(fixture.Service.GetSnapshot().DirectPictureReading!.Id, 44, true);
        var report = fixture.Service.ExportReport();
        Assert.DoesNotContain(pin, report, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Token, report, StringComparison.Ordinal);
        var json = JsonNode.Parse(report)!;
        Assert.Equal(IpRemoteReportRedactor.CertificateMarker, json["ControlCapabilities"]![0]!["Profile"]!["Connection"]!["CertificateSha256"]!.GetValue<string>());
        Assert.Equal(IpRemoteReportRedactor.CertificateMarker, json["DirectPictureReading"]!["Profile"]!["Connection"]!["CertificateSha256"]!.GetValue<string>());
        Assert.Contains(pin, fixture.Service.ExportReport(redactCertificateFingerprints: false), StringComparison.Ordinal);
        Assert.Equal(pin, fixture.Service.GetSnapshot().ControlCapabilities.Single().Profile.Connection.CertificateSha256);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(fixture.DirectoryPath, "ip-remote", "control-capabilities.json")));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
