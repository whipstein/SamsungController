using System.Net;
using System.Text.Json.Nodes;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpRemotePictureControlTests
{
    [Theory]
    [InlineData("contrast", 45, 44)]
    [InlineData("color", 25, 24)]
    [InlineData("sharpness", 0, 1)]
    public async Task EachControlVerifiesRestoresAndEnablesOnlyItsOwnDirectWrites(string control, int original, int target)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync(control);
        var prepared = fixture.Service.GetSnapshot().PictureTest!;
        Assert.Equal(control, prepared.Control);
        Assert.Equal(original, prepared.Original);
        Assert.Equal(target, prepared.Target);
        Assert.All(fixture.Display.Methods, method => Assert.StartsWith("get", method));
        await fixture.Service.ApplyPictureTestAsync(prepared.Id, true);
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Equal(new[] { target }, fixture.Display.WritesFor(control));
        await fixture.Service.RestorePictureTestAsync(prepared.Id, true);
        var capability = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Equal(control, capability.Control);
        Assert.Equal(new[] { target, original }, fixture.Display.WritesFor(control));

        fixture.Display.Requests.Clear();
        await fixture.RestartAsync();
        Assert.Empty(fixture.Display.Requests);
        Assert.Equal(control, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities).Control);
        await fixture.Service.ReadDirectPictureAsync(control);
        var read = fixture.Service.GetSnapshot().DirectPictureReading!;
        Assert.Equal(control, read.Control);
        await fixture.Service.ApplyDirectPictureAsync(read.Id, target, true);
        var adjustment = fixture.Service.GetSnapshot().PictureTest!;
        Assert.True(adjustment.DirectChangeKept);
        Assert.Equal(control, adjustment.Control);
        await fixture.Service.UndoDirectPictureAsync(adjustment.Id, true);
        Assert.Equal(new[] { target, original }, fixture.Display.WritesFor(control));
        Assert.Equal(capability, Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities));
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(0, fixture.Display.Sharpness);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("sharpness")]
    public async Task ContrastEvidenceCannotUnlockAnotherControlButIndependentEvidenceAccumulates(string control)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var contrastEvidence = Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.ReadDirectPictureAsync(control);
        var read = fixture.Service.GetSnapshot().DirectPictureReading!;
        var count = fixture.Display.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyDirectPictureAsync(read.Id, read.Value + 1, true));
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Empty(fixture.Display.WritesFor(control));
        await fixture.VerifyAsync(control: control);
        await fixture.RestartAsync();
        Assert.Equal(2, fixture.Service.GetSnapshot().ControlCapabilities.Count);
        Assert.Contains(contrastEvidence, fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Contains(fixture.Service.GetSnapshot().ControlCapabilities, item => item.Control == control);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("sharpness")]
    public async Task MissingFieldNeverSubstitutesContrastAndOtherFieldDriftBlocksWrite(string control)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        fixture.Display.VideoOverride = new JsonObject { ["contrast"] = 45 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync(control));
        Assert.Null(fixture.Service.GetSnapshot().PictureTest);
        fixture.Display.VideoOverride = null;
        await fixture.Service.PreparePictureTestAsync(control);
        fixture.Display.Contrast = 44;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(fixture.Service.GetSnapshot().PictureTest!.Id, true));
        Assert.Empty(fixture.Display.WritesFor(control));
    }

    [Theory]
    [InlineData("color")]
    [InlineData("sharpness")]
    public async Task AmbiguousWriteLocksOtherControlsAndRecoversTheRightFieldAfterRestart(string control)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync(control);
        var test = fixture.Service.GetSnapshot().PictureTest!;
        fixture.Display.Override = (request, _) =>
        {
            if (request["method"]!.GetValue<string>() != control + "Control") return Task.FromResult<HttpResponseMessage?>(null);
            if (control == "color") fixture.Display.Color = test.Target;
            else fixture.Display.Sharpness = test.Target;
            throw new HttpRequestException("Simulated lost acknowledgment after the TV changed");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(test.Id, true));
        fixture.Display.Override = null;
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Equal(control, fixture.Service.GetSnapshot().PictureTest!.Control);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync("contrast"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReadDirectPictureAsync("contrast"));
        Assert.Equal(count, fixture.Display.Requests.Count);
        await fixture.Service.RestorePictureTestAsync(test.Id);
        Assert.Equal(new[] { test.Target, test.Original }, fixture.Display.WritesFor(control));
        Assert.Empty(fixture.Display.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(0, fixture.Display.Sharpness);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("sharpness")]
    public async Task FalseSetterSuccessDoesNotVerifyOrAllowAnotherWrite(string control)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync(control);
        var test = fixture.Service.GetSnapshot().PictureTest!;
        fixture.Display.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.GetValue<string>() == control + "Control"
            ? ContrastDisplay.Reply(request, new JsonObject { [control] = test.Target }) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApplyPictureTestAsync(test.Id, true));
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.ChangeReadbackConfirmed);
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
        await fixture.Service.RestorePictureTestAsync(test.Id);
        Assert.Single(fixture.Display.WritesFor(control)); // Already original: no redundant restoration.
        Assert.Empty(fixture.Service.GetSnapshot().ControlCapabilities);
    }

    [Theory]
    [InlineData("brightness")]
    [InlineData("AccessToken")]
    public async Task UnsupportedControlCannotPrepareOrReadForDirectUse(string control)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreparePictureTestAsync(control));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ReadDirectPictureAsync(control));
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task LegacyJournalWithoutControlRetainsContrastVerificationAndRecovery()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath))!.AsObject();
        json.Remove("Control");
        await File.WriteAllTextAsync(fixture.JournalPath, json.ToJsonString());
        File.Delete(Path.Combine(fixture.DirectoryPath, "ip-remote", "control-capabilities.json"));
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.Verified);
        Assert.Equal("contrast", Assert.Single(fixture.Service.GetSnapshot().ControlCapabilities).Control);
        await fixture.Service.PreparePictureTestAsync();
        var id = fixture.Service.GetSnapshot().PictureTest!.Id;
        await fixture.Service.ApplyPictureTestAsync(id, true);
        json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.JournalPath))!.AsObject();
        json.Remove("Control");
        await File.WriteAllTextAsync(fixture.JournalPath, json.ToJsonString());
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().PictureTest!.RequiresRecovery);
        Assert.Equal("contrast", fixture.Service.GetSnapshot().PictureTest!.Control);
        await fixture.Service.RestorePictureTestAsync(id);
        Assert.Equal(45, fixture.Display.Contrast);
    }
}
