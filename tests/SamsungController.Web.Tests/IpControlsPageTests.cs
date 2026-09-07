using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpControlsPageTests
{
    [Fact]
    public async Task AllThreeControlsStageApplyAndStagePreviousWithoutPopupsOrImplicitWrites()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await IpRemoteWorkspaceTests.VerifyAllAsync(fixture);
        var js = new IpRemotePageTests.DownloadJavaScript { Confirm = false };
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpControls));
        await page.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await page.AssertDisabledAsync("Apply 0 pending", true);
        await page.ClickAsync("Refresh TV values");
        await page.AssertTextAsync("3 / 3 controls verified");
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        await page.ChangeAsync("Contrast target", "44");
        await page.ChangeAsync("Color target", "24");
        await page.ChangeAsync("Sharpness target", "1");
        await page.AssertCheckboxAsync("Confirm workspace conditions", true);
        await page.AssertDisabledAsync("Apply 3 pending", false);
        Assert.Equal(2, fixture.Display.Requests.Count);
        await page.ClickAsync("Apply 3 pending");
        await page.AssertTextAsync("All 3 changes confirmed by readback");
        await page.AssertDisabledAsync("Apply 0 pending", true);
        await page.AssertTargetAsync("Contrast target", 44);
        Assert.Equal(1, fixture.Display.Sharpness);
        Assert.Equal(0, js.ConfirmCalls);
        var count = fixture.Display.Requests.Count;
        await page.ClickAsync("Stage previous values");
        Assert.Equal(count, fixture.Display.Requests.Count);
        await page.AssertTargetAsync("Contrast target", 45);
        await page.AssertTargetAsync("Color target", 25);
        await page.AssertTargetAsync("Sharpness target", 0);
        await page.AssertDisabledAsync("Apply 3 pending", true);
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        await page.ClickAsync("Apply 3 pending");
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(25, fixture.Display.Color);
        Assert.Equal(0, fixture.Display.Sharpness);
        Assert.Equal(0, js.ConfirmCalls);
    }

    [Fact]
    public async Task RefreshRetainsDraftInSameContextButDiscardsItWhenReportedModeChanges()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await IpRemoteWorkspaceTests.VerifyAllAsync(fixture);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpControls));
        await page.StartAsync();
        await page.ClickAsync("Refresh TV values");
        await page.ChangeAsync("Contrast target", "40");
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        fixture.Display.Color = 30;
        await page.ClickAsync("Refresh TV values");
        await page.AssertTargetAsync("Contrast target", 40);
        await page.AssertTargetAsync("Color target", 30);
        await page.AssertCheckboxAsync("Confirm workspace conditions", false);
        fixture.Display.Mode = "Standard";
        await page.ClickAsync("Refresh TV values");
        await page.AssertTargetAsync("Contrast target", 45);
        await page.AssertTextAsync("0 / 3 controls verified");
        await page.ChangeAsync("Contrast target", "42"); // Handler also rejects synthetic events on a locked fieldset.
        await page.AssertTargetAsync("Contrast target", 45);
        Assert.Empty(fixture.Display.Writes);
    }

    [Fact]
    public async Task PresetDownloadDistinguishesTvFromDesiredValuesAndUploadOnlyStages()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await IpRemoteWorkspaceTests.VerifyAllAsync(fixture);
        var js = new IpRemotePageTests.DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpControls));
        await page.StartAsync();
        await page.ClickAsync("Refresh TV values");
        await page.ChangeAsync("Color target", "24");
        await page.ClickAsync("Download TV values");
        Assert.Equal(25, JsonNode.Parse(js.Download)!["values"]!["color"]!.GetValue<int>());
        await page.ClickAsync("Download staged values");
        Assert.Equal(24, JsonNode.Parse(js.Download)!["values"]!["color"]!.GetValue<int>());
        var desired = js.Download;
        await page.ClickAsync("Discard pending changes");
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        await page.UploadAsync(new PresetFile(desired));
        await page.AssertTargetAsync("Color target", 24);
        await page.AssertCheckboxAsync("Confirm workspace conditions", false);
        await page.AssertTextAsync("Nothing was sent to the TV");
        Assert.Equal(2, fixture.Display.Requests.Count);
        Assert.Equal(25, fixture.Service.GetSnapshot().WorkspaceReading!.Value("color"));
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        await page.ClickAsync("Apply 1 pending");
        Assert.Equal(new[] { 24 }, fixture.Display.WritesFor("color"));
        Assert.Empty(fixture.Display.Writes);
    }

    [Fact]
    public async Task InvalidOrUnverifiedPresetLeavesEntireDraftUntouched()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpControls));
        await page.StartAsync();
        await page.ClickAsync("Refresh TV values");
        var json = IpRemotePicturePresets.Export(fixture.Service.GetSnapshot().WorkspaceReading!, new Dictionary<string, int> { ["contrast"] = 44, ["color"] = 24 }, "Unverified");
        await page.UploadAsync(new PresetFile(json));
        await page.AssertTextAsync("No values were staged");
        await page.AssertTargetAsync("Contrast target", 45);
        await page.AssertTargetAsync("Color target", 25);
        await page.UploadAsync(new PresetFile("{\nbroken json"));
        await page.AssertTextAsync("JSON error at line 2");
        await page.AssertTargetAsync("Contrast target", 45);
        Assert.Equal(2, fixture.Display.Requests.Count);
    }

    [Fact]
    public async Task StopButtonCancelsBatchAndShowsRecoveryWithoutSendingLaterControls()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await IpRemoteWorkspaceTests.VerifyAllAsync(fixture);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(IpControls));
        await page.StartAsync();
        await page.ClickAsync("Refresh TV values");
        await page.ChangeAsync("Contrast target", "44");
        await page.ChangeAsync("Color target", "24");
        await page.SetCheckboxAsync("Confirm workspace conditions", true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Display.Override = async (request, cancellation) =>
        {
            if (request["method"]!.GetValue<string>() == "contrastControl")
            {
                fixture.Display.Contrast = 44;
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return null;
        };
        var apply = page.ClickAsync("Apply 2 pending");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await page.AssertDisabledAsync("Refresh TV values", true);
            await page.AssertDisabledAsync("Stop", false);
            await page.ClickAsync("Stop");
        }
        finally { fixture.Service.Cancel(); }
        await apply;
        await page.AssertTextAsync("Contrast may have changed");
        await page.AssertTextAsync("Open read-first recovery");
        await page.AssertTextAsync("Not sent");
        await page.AssertDisabledAsync("Stop", true);
        Assert.Equal(new[] { 44 }, fixture.Display.Writes);
        Assert.Empty(fixture.Display.WritesFor("color"));
    }

    private sealed class PresetFile(string content) : IBrowserFile
    {
        public string Name => "preset.json";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => Encoding.UTF8.GetByteCount(content);
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("File too large.");
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }
    }
}
