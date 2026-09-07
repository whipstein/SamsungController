using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

#pragma warning disable BL0006
public sealed class IpRemotePageTests
{
    [Fact]
    public async Task VerifiedDirectControlStagesEditsRequiresConfirmationAndKeepsOrUndoesValue()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Contrast read/write verification saved");
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
        Assert.Empty(fixture.Display.Requests);
        await renderer.ClickAsync("Read direct contrast");
        await renderer.ChangeAsync("Target contrast", "40");
        Assert.Empty(fixture.Display.Writes);
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
        await renderer.SetCheckboxAsync("Confirm direct contrast conditions", true);
        await renderer.AssertDisabledAsync("Apply direct contrast…", false);
        javascript.Confirm = false;
        await renderer.ClickAsync("Apply direct contrast…");
        Assert.Empty(fixture.Display.Writes);
        javascript.Confirm = true;
        await renderer.ClickAsync("Apply direct contrast…");
        Assert.Equal(40, fixture.Display.Contrast);
        await renderer.AssertTextAsync("New value kept on TV");
        await renderer.AssertTextAsync("Contrast read/write verification saved");
        await renderer.AssertDisabledAsync("New display", false);
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
        await renderer.ClickAsync("Undo last direct change…");
        Assert.Equal(45, fixture.Display.Contrast);
        await renderer.AssertTextAsync("Original restored");
        Assert.Equal(new[] { 40, 45 }, fixture.Display.Writes);
        await renderer.ClickAsync("Download diagnostic report");
        Assert.Contains("ControlCapabilities", javascript.Download, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Token, javascript.Download, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedContextCanReadButCannotApplyAndStagingNewTargetClearsConsent()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.ClickAsync("Read direct contrast");
        await renderer.ChangeAsync("Target contrast", "44");
        await renderer.SetCheckboxAsync("Confirm direct contrast conditions", true);
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
        Assert.Empty(fixture.Display.Writes);
        await fixture.VerifyAsync();
        await renderer.ClickAsync("Read direct contrast");
        await renderer.ChangeAsync("Target contrast", "44");
        await renderer.SetCheckboxAsync("Confirm direct contrast conditions", true);
        await renderer.AssertDisabledAsync("Apply direct contrast…", false);
        await renderer.ChangeAsync("Target contrast", "43");
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
        await renderer.SetCheckboxAsync("Confirm direct contrast conditions", true);
        await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.11");
        await renderer.AssertDisabledAsync("Apply direct contrast…", true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuidedContrastPageRequiresConditionsAndDialogThenRestoresAfterVisualChoice(bool visualPass)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await renderer.ClickAsync("Prepare contrast test (read only)");
        await renderer.AssertDisabledAsync("Apply one-step contrast test…", true);
        await renderer.SetCheckboxAsync("Confirm contrast test conditions", true);
        javascript.Confirm = false;
        await renderer.ClickAsync("Apply one-step contrast test…");
        Assert.Empty(fixture.Display.Writes);
        Assert.Equal(2, fixture.Display.Requests.Count);
        javascript.Confirm = true;
        await renderer.ClickAsync("Apply one-step contrast test…");
        await renderer.AssertTextAsync("Contrast restoration pending");
        await renderer.AssertDisabledAsync("New display", true);
        await renderer.AssertDisabledAsync("Save IP Remote profile", true);
        await renderer.AssertTextAsync("Check the actual Contrast number on the TV: is it 44?");
        Assert.Equal(44, fixture.Display.Contrast);
        await renderer.ClickAsync(visualPass ? "Matches — restore original" : "Does not match — restore original");
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.Equal(visualPass, fixture.Service.GetSnapshot().ContrastTest!.Verified);
        await renderer.AssertDisabledAsync("New display", false);
        await renderer.ClickAsync("Download diagnostic report");
        Assert.Equal(visualPass, JsonNode.Parse(javascript.Download)!["ContrastTest"]!["Verified"]!.GetValue<bool>());
        Assert.DoesNotContain(ContrastFixture.Token, javascript.Download, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartShowsRecoveryControlsWithoutSendingAndRequiresRecoveryConfirmation()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PrepareContrastTestAsync();
        await fixture.Service.ApplyContrastTestAsync(fixture.Service.GetSnapshot().ContrastTest!.Id, true);
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        var javascript = new DownloadJavaScript { Confirm = false };
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Unresolved experiment from a previous session");
        await renderer.ClickAsync("Check and restore original…");
        Assert.Equal(count, fixture.Display.Requests.Count);
        javascript.Confirm = true;
        await renderer.ClickAsync("Check and restore original…");
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.False(fixture.Service.GetSnapshot().ContrastTest!.Verified);
    }

    [Fact]
    public async Task PageCanSavePairReadAndDownloadWithoutUsingTheExistingController()
    {
        var directory = Directory.CreateTempSubdirectory("SamsungController-IP-ui-").FullName;
        try
        {
            var fingerprint = new string('A', 64);
            var client = new IpRemoteServiceTests.RecordingClient { ObservedCertificateSha256 = fingerprint };
            using var service = new SamsungIpRemoteService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["SamsungController:ConfigurationDirectory"] = directory }).Build(), client);
            var javascript = new DownloadJavaScript();
            await using var services = new ServiceCollection().AddLogging().AddSingleton(service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
            await using var renderer = new IpPageRenderer(services);
            await renderer.StartAsync();
            await renderer.AssertTextAsync($"v{SamsungIpRemoteService.Version}");
            Assert.Empty(client.Calls);
            await renderer.AssertDisabledAsync("Pair with TV", true);
            await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.10");
            await renderer.ClickAsync("Save IP Remote profile");
            Assert.Empty(client.Calls);
            await renderer.ClickAsync("Pair with TV");
            await renderer.ClickAsync("Read both state queries");
            Assert.Equal(new[] { "createAccessToken", "getTVStates", "getVideoStates" }, client.Calls);
            await renderer.ClickAsync("Download diagnostic report");
            Assert.Contains("SamsungController.IPRemote.Diagnostics.v1", javascript.Download, StringComparison.Ordinal);
            Assert.DoesNotContain("192.0.2.10", javascript.Download, StringComparison.Ordinal);
            Assert.DoesNotContain(fingerprint, javascript.Download, StringComparison.Ordinal);
            Assert.Contains(IpRemoteReportRedactor.CertificateMarker, javascript.Download, StringComparison.Ordinal);
            await renderer.SetCheckboxAsync("Redact certificate SHA-256 fingerprints", false);
            await renderer.ClickAsync("Download diagnostic report");
            Assert.Contains(fingerprint, javascript.Download, StringComparison.Ordinal);
            Assert.DoesNotContain("192.0.2.10", javascript.Download, StringComparison.Ordinal);
            await renderer.SetCheckboxAsync("Redact certificate SHA-256 fingerprints", true);
            await renderer.ClickAsync("Download diagnostic report");
            Assert.DoesNotContain(fingerprint, javascript.Download, StringComparison.Ordinal);
            await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.11");
            await renderer.AssertDisabledAsync("Read both state queries", true);
            await renderer.ClickAsync("Discard unsaved edits");
            await renderer.AssertDisabledAsync("Read both state queries", false);
            await renderer.ClickAsync("New display");
            await renderer.AssertDisabledAsync("Pair again…", true);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealPairingClientWithTextIdEnablesReadsOrShowsTheRejection(bool matchingId)
    {
        var directory = Directory.CreateTempSubdirectory("SamsungController-IP-pairing-ui-").FullName;
        try
        {
            var handler = new TextIdReplyHandler(matchingId);
            using var http = new HttpClient(handler);
            var client = new SamsungIpRemoteClient(new PrivateIpRemoteTokenStore(Path.Combine(directory, "ip-remote")), http);
            using var service = new SamsungIpRemoteService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["SamsungController:ConfigurationDirectory"] = directory }).Build(), client);
            var javascript = new DownloadJavaScript();
            await using var services = new ServiceCollection().AddLogging().AddSingleton(service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
            await using var renderer = new IpPageRenderer(services);
            await renderer.StartAsync();
            await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.10");
            await renderer.ClickAsync("Save IP Remote profile");
            await renderer.ClickAsync("Pair with TV");
            Assert.Equal(matchingId, await client.HasTokenAsync(new SamsungIpRemoteOptions { Host = "192.0.2.10" }));
            await renderer.AssertDisabledAsync("Read both state queries", !matchingId);
            await renderer.AssertTextAsync(matchingId ? "Last pairing attempt: completed" : "Last pairing attempt: failed");
            if (matchingId)
            {
                await renderer.ClickAsync("Read both state queries");
                Assert.Equal(new[] { "createAccessToken", "getTVStates", "getVideoStates" }, handler.Methods);
                Assert.All(service.GetSnapshot().Observations, observation => Assert.True(observation.Exchange.IsSuccess));
            }
            else
            {
                await renderer.AssertTextAsync("Pairing has not saved an IP Remote token.");
                await renderer.AssertDisabledAsync("Pair with TV", false);
                Assert.Single(handler.Methods);
            }
            await renderer.ClickAsync("Download diagnostic report");
            Assert.DoesNotContain("simulated-pairing-credential", javascript.Download, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class TextIdReplyHandler(bool matchingId) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var method = json["method"]!.GetValue<string>();
            Methods.Add(method);
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = matchingId ? json["id"]!.ToJsonString() : "wrong-id",
                ["result"] = method == "createAccessToken" ? new JsonObject { ["AccessToken"] = "simulated-pairing-credential" }
                    : new JsonObject { ["brightness"] = 20 }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }
    }

    private sealed class DownloadJavaScript : IJSRuntime
    {
        public string Download { get; private set; } = "";
        public bool Confirm { get; set; } = true;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "samsungController.downloadText") Download = (string)args![1]!;
            return ValueTask.FromResult(identifier == "confirm" ? (TValue)(object)Confirm : default!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class IpPageRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames => GetCurrentRenderTreeFrames(_root).Array.Take(GetCurrentRenderTreeFrames(_root).Count).ToArray();
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(IpRemote)));
            await RenderRootComponentAsync(_root, ParameterView.Empty);
        });
        private RenderTreeFrame[] Button(string label) => Frames.Select((frame, index) => (frame, index))
            .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "button")
            .Select(item => Frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray()).Single(item => Text(item).Trim() == label);
        public Task ClickAsync(string label) => Dispatcher.InvokeAsync(async () =>
        {
            var button = Button(label);
            Assert.DoesNotContain(button, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(button.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs());
        });
        public Task AssertDisabledAsync(string label, bool expected) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            Button(label).Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true)));
        public Task AssertTextAsync(string expected) => Dispatcher.InvokeAsync(() => Assert.Contains(expected, Text(Frames), StringComparison.Ordinal));
        public Task ChangeAsync(string label, string value) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var input = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "input")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            await DispatchEventAsync(input.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "oninput").AttributeEventHandlerId,
                null, new ChangeEventArgs { Value = value });
        });
        public Task SetCheckboxAsync(string label, bool value) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var input = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "input")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            await DispatchEventAsync(input.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeEventHandlerId,
                null, new ChangeEventArgs { Value = value });
        });
        private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        { RenderTreeFrameType.Text => frame.TextContent, RenderTreeFrameType.Markup => frame.MarkupContent, _ => "" }));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
#pragma warning restore BL0006
