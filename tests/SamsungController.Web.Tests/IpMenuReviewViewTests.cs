using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuReviewViewTests
{
    [Theory]
    [InlineData("contrastControl/contrast", "44")]
    [InlineData("sharpnessControl/sharpness", "5")]
    [InlineData("brightnessControl/brightness", "2")]
    public async Task RejectedWriteShowsReviewInPinnedToolbarAndClearsWithoutRetry(string controlId, string target)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        var control = IpMenuCatalog.Get(controlId);
        await using var services = Services(fixture);
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.AssertAriaButtonPresentAsync("Verify settings", false);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == control.Method
            && request["params"]?[control.Field] is not null ? MenuFixture.Reject(request, -32601) : null);
        await page.ChangeAsync(control.Name + " value", target, "onchange");
        fixture.Display.Requests.Clear();
        await page.ClickAsync("Apply 1 pending");
        Assert.Single(fixture.Writes);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        await page.AssertInputPresentAsync(control.Name + " value", true);
        Assert.Contains("Verify settings", await page.ButtonTextsWithinAsync("section", "Menu controls and navigation"));
        await page.AssertElementAttributeAsync("button", "Verify settings", "popovertarget", "verify-menu-settings");
        await page.AssertTextAsync("Clearing the review sends no commands");

        await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<DirectMenu>()).ToHtmlString());
            Assert.Contains("popover=\"auto\"", html);
            Assert.Contains("aria-labelledby=\"verify-menu-settings-title\"", html);
            Assert.True(html.IndexOf("id=\"verify-menu-settings\"", StringComparison.Ordinal) < html.IndexOf("data-control-section=", StringComparison.Ordinal));
            Assert.DoesNotContain("aria-label=\"Last settings update\"", html); // No recovery action buried below the controls.
        }
        fixture.Display.Requests.Clear();
        await page.ClickAsync("I checked the TV — close interrupted update");
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        await page.AssertAriaButtonPresentAsync("Verify settings", false);
        await page.AssertTextAsync("Review cleared. Select Refresh state before further adjustments.");
        Assert.Empty(fixture.Display.Requests);
        await fixture.RestartAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task SendingDoesNotFlashAReviewButtonOrMoveThePinnedControls()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (request, token) =>
        {
            if (request["params"]?["contrast"] is not null)
            {
                entered.TrySetResult();
                await finish.Task.WaitAsync(token);
            }
            return null;
        };
        var apply = fixture.Service.ApplyMenuAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview); // Sending is not yet a failure.
            await using var services = Services(fixture);
            await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
            await page.StartAsync();
            await page.AssertAriaButtonPresentAsync("Verify settings", false);
        }
        finally { finish.TrySetResult(); await apply.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [Fact]
    public async Task PendingRecallOpensCombinedRecallReviewFromTopToolbar()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        var snapshot = fixture.Service.GetSnapshot();
        var recall = new IpMenuStateRecall(Guid.NewGuid(), "Interrupted recall", IpMenuSavedContext.From(snapshot.ActiveProfile!, snapshot.Menu), DateTimeOffset.UtcNow)
        { Status = "Stopped" };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(fixture.JournalPath)!, "state-recall.json"), JsonSerializer.Serialize(recall));
        await fixture.RestartAsync();
        fixture.Display.Requests.Clear();
        await using var services = Services(fixture);
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        Assert.Contains("Verify settings", await page.ButtonTextsWithinAsync("section", "Menu controls and navigation"));
        await page.AssertElementAttributeAsync("button", "Verify settings", "popovertarget", "recall-settings");
        Assert.Empty(fixture.Display.Requests);
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
