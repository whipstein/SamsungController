using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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
    public async Task VerifiedDirectControlStagesEditsWithoutPopupAndKeepsOrUndoesValue()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Contrast read/write verification saved");
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        Assert.Empty(fixture.Display.Requests);
        await renderer.ClickAsync("Read direct contrast");
        await renderer.ChangeAsync("Target contrast", "40");
        Assert.Empty(fixture.Display.Writes);
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        await renderer.SetCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.AssertDisabledAsync("Apply direct contrast", false);
        javascript.Confirm = false;
        await renderer.ClickAsync("Apply direct contrast");
        Assert.Equal(40, fixture.Display.Contrast);
        await renderer.AssertTextAsync("New value kept on TV");
        await renderer.AssertTextAsync("Contrast read/write verification saved");
        await renderer.AssertDisabledAsync("New display", false);
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        await renderer.ClickAsync("Undo last direct change");
        Assert.Equal(45, fixture.Display.Contrast);
        await renderer.AssertTextAsync("Original restored");
        Assert.Equal(new[] { 40, 45 }, fixture.Display.Writes);
        Assert.Equal(0, javascript.ConfirmCalls);
        await renderer.ClickAsync("Download diagnostic report");
        Assert.Contains("ControlCapabilities", javascript.Download, StringComparison.Ordinal);
        Assert.DoesNotContain(ContrastFixture.Token, javascript.Download, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedContextStaysLockedButVerifiedTargetEditsPreserveConditions()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.ClickAsync("Read direct contrast");
        await renderer.AssertInputPresentAsync("Target contrast", false);
        await renderer.AssertInputPresentAsync("Confirm direct picture conditions", false);
        await renderer.AssertDisabledAsync("Prepare contrast verification (read only)", false);
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        Assert.Empty(fixture.Display.Writes);
        await fixture.VerifyAsync();
        await renderer.ClickAsync("Read direct contrast");
        await renderer.ChangeAsync("Target contrast", "44");
        await renderer.SetCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.AssertDisabledAsync("Apply direct contrast", false);
        await renderer.ChangeAsync("Target contrast", "43");
        await renderer.AssertDisabledAsync("Apply direct contrast", false);
        await renderer.AssertCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.ClickAsync("+");
        await renderer.ClickAsync("−");
        await renderer.AssertDisabledAsync("Apply direct contrast", false);
        await renderer.AssertCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.11");
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuidedContrastPageRequiresConditionsWithoutPopupThenRestoresAfterVisualChoice(bool visualPass)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        var javascript = new DownloadJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        Assert.Empty(fixture.Display.Requests);
        await renderer.ClickAsync("Prepare contrast test (read only)");
        await renderer.AssertDisabledAsync("Apply one-step contrast test", true);
        await renderer.SetCheckboxAsync("Confirm picture test conditions", true);
        javascript.Confirm = false;
        await renderer.ClickAsync("Apply one-step contrast test");
        Assert.Equal(0, javascript.ConfirmCalls);
        await renderer.AssertTextAsync("Contrast restoration pending");
        await renderer.AssertDisabledAsync("New display", true);
        await renderer.AssertDisabledAsync("Save IP Remote profile", true);
        await renderer.AssertTextAsync("Check the actual Contrast number on the TV: is it 44?");
        Assert.Equal(44, fixture.Display.Contrast);
        await renderer.ClickAsync(visualPass ? "Matches — restore original" : "Does not match — restore original");
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(new[] { 44, 45 }, fixture.Display.Writes);
        Assert.Equal(visualPass, fixture.Service.GetSnapshot().PictureTest!.Verified);
        await renderer.AssertDisabledAsync("New display", false);
        await renderer.ClickAsync("Download diagnostic report");
        Assert.Equal(visualPass, JsonNode.Parse(javascript.Download)!["PictureTest"]!["Verified"]!.GetValue<bool>());
        Assert.DoesNotContain(ContrastFixture.Token, javascript.Download, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("color", "Color", 25, 24)]
    [InlineData("sharpness", "Sharpness", 0, 1)]
    public async Task ControlSelectorUsesIndependentGuidedTestAndDirectEditor(string control, string name, int original, int target)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        var javascript = new DownloadJavaScript { Confirm = false };
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service).AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.SelectControlAsync(control);
        Assert.Empty(fixture.Display.Requests);
        await renderer.AssertTextAsync($"No completed {name} verification");
        await renderer.ClickAsync($"Prepare {control} test (read only)");
        await renderer.SetCheckboxAsync("Confirm picture test conditions", true);
        await renderer.ClickAsync($"Apply one-step {control} test");
        await renderer.AssertTextAsync($"Check the actual {name} number on the TV: is it {target}?");
        await renderer.ClickAsync("Matches — restore original");
        await renderer.AssertTextAsync($"{name} read/write verification saved");
        Assert.Equal(new[] { target, original }, fixture.Display.WritesFor(control));
        await renderer.ClickAsync($"Read direct {control}");
        await renderer.SetCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.ChangeAsync($"Target {control}", target.ToString());
        await renderer.AssertCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.ClickAsync($"Apply direct {control}");
        await renderer.ClickAsync("Undo last direct change");
        Assert.Equal(new[] { target, original, target, original }, fixture.Display.WritesFor(control));
        Assert.Equal(0, javascript.ConfirmCalls);
        await renderer.SelectControlAsync("contrast");
        await renderer.AssertTextAsync("Contrast read/write verification saved");
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        await renderer.ClickAsync("Read direct contrast");
        await renderer.AssertCheckboxAsync("Confirm direct picture conditions", false);
        Assert.Equal(2, fixture.Service.GetSnapshot().ControlCapabilities.Count);
    }

    [Theory]
    [InlineData("color", "Color")]
    [InlineData("sharpness", "Sharpness")]
    public async Task UnverifiedDirectControlOffersReadOnlyPreparationAndItsOwnWorkingTest(string control, string name)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        var javascript = new DownloadJavaScript { Confirm = false };
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.SelectControlAsync(control);
        await renderer.ClickAsync($"Read direct {control}");
        await renderer.AssertDisabledAsync($"Apply direct {control}", true);
        await renderer.AssertInputPresentAsync("Confirm direct picture conditions", false);
        await renderer.AssertInputPresentAsync($"Target {control}", false);
        await renderer.AssertTextAsync($"{name} needs its own one-step test");
        await renderer.ClickAsync($"Prepare {control} verification (read only)");
        Assert.Equal(1, javascript.ScrollCalls);
        Assert.All(fixture.Display.Methods, method => Assert.StartsWith("get", method));
        Assert.Equal(control, fixture.Service.GetSnapshot().PictureTest!.Control);
        await renderer.AssertDisabledAsync($"Apply one-step {control} test", true);
        await renderer.SetCheckboxAsync("Confirm picture test conditions", true);
        await renderer.AssertDisabledAsync($"Apply one-step {control} test", false);
        await renderer.AssertDisabledAsync($"Apply direct {control}", true);
        await renderer.ClickAsync($"Apply one-step {control} test");
        await renderer.ClickAsync("Matches — restore original");
        await renderer.ClickAsync($"Read direct {control}");
        await renderer.AssertInputPresentAsync("Confirm direct picture conditions", true);
        await renderer.AssertInputPresentAsync($"Target {control}", true);
        await renderer.SetCheckboxAsync("Confirm direct picture conditions", true);
        await renderer.ChangeAsync($"Target {control}", (fixture.Service.GetSnapshot().DirectPictureReading!.Value + 1).ToString());
        await renderer.AssertDisabledAsync($"Apply direct {control}", false);
        Assert.Equal(0, javascript.ConfirmCalls);
    }

    [Fact]
    public async Task EvidenceFromAnotherReportedModeStillOffersVerificationInsteadOfDirectConsent()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Mode = "Standard";
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.ClickAsync("Read direct contrast");
        await renderer.AssertInputPresentAsync("Confirm direct picture conditions", false);
        await renderer.AssertDisabledAsync("Apply direct contrast", true);
        await renderer.AssertDisabledAsync("Prepare contrast verification (read only)", false);
        await renderer.AssertTextAsync("Direct Contrast is locked because its one-step verification is not complete");
    }

    [Fact]
    public async Task StaticPageCannotOfferAnInteractiveLookingSelectorBeforeHandlersAreReady()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<IpRemote>()).ToHtmlString());
        var select = System.Text.RegularExpressions.Regex.Match(html,
            "<select[^>]*aria-label=\"IP Remote picture control\"[^>]*>");
        Assert.True(select.Success);
        Assert.Contains("disabled", select.Value, StringComparison.Ordinal);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task DuplicateNativeChangeDoesNotClearConditionsForTheSelectedControl()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        await renderer.SelectControlAsync("color", "oninput");
        await renderer.ClickAsync("Prepare color test (read only)");
        await renderer.SetCheckboxAsync("Confirm picture test conditions", true);
        var count = fixture.Display.Requests.Count;
        await renderer.SelectControlAsync("color", "onchange");
        await renderer.AssertCheckboxAsync("Confirm picture test conditions", true);
        await renderer.AssertDisabledAsync("Apply one-step color test", false);
        await renderer.SelectControlAsync("invalid-control", "oninput");
        await renderer.AssertSelectedControlAsync("color");
        await renderer.AssertCheckboxAsync("Confirm picture test conditions", true);
        Assert.Equal(count, fixture.Display.Requests.Count);
    }

    [Theory]
    [InlineData("oninput")]
    [InlineData("onchange")]
    public async Task NativeControlSelectionUpdatesEveryActionAndRemainsSelectedAfterRefresh(string eventName)
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.VerifyAsync();
        fixture.Display.Requests.Clear();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpPageRenderer(services);
        await renderer.StartAsync();
        foreach (var control in new[] { "color", "sharpness", "contrast" })
        {
            var count = fixture.Display.Requests.Count;
            await renderer.SelectControlAsync(control, eventName);
            await renderer.AssertSelectedControlAsync(control);
            await renderer.AssertDisabledAsync($"Read direct {control}", false);
            await renderer.AssertDisabledAsync($"Apply direct {control}", true);
            await renderer.AssertDisabledAsync($"Prepare {control} test (read only)", false);
            Assert.Equal(count, fixture.Display.Requests.Count);
            await renderer.ClickAsync($"Read direct {control}");
            Assert.Equal(control, fixture.Service.GetSnapshot().DirectPictureReading!.Control);
            await renderer.AssertSelectedControlAsync(control);
            await renderer.ClickAsync($"Prepare {control} test (read only)");
            Assert.Equal(control, fixture.Service.GetSnapshot().PictureTest!.Control);
            await renderer.AssertDisabledAsync($"Apply one-step {control} test", true);
            await renderer.AssertSelectedControlAsync(control);
        }
        Assert.All(fixture.Display.Methods, method => Assert.StartsWith("get", method));
    }

    [Fact]
    public async Task RestartShowsRecoveryControlsWithoutSendingAndRequiresRecoveryConfirmation()
    {
        using var fixture = await ContrastFixture.CreateAsync();
        await fixture.Service.PreparePictureTestAsync();
        await fixture.Service.ApplyPictureTestAsync(fixture.Service.GetSnapshot().PictureTest!.Id, true);
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
        Assert.False(fixture.Service.GetSnapshot().PictureTest!.Verified);
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

    internal sealed class DownloadJavaScript : IJSRuntime
    {
        public string Download { get; private set; } = "";
        public bool Confirm { get; set; } = true;
        public int ConfirmCalls { get; private set; }
        public int ScrollCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "confirm") ConfirmCalls++;
            if (identifier == "samsungController.scrollToElement") ScrollCalls++;
            if (identifier == "samsungController.downloadText") Download = (string)args![1]!;
            return ValueTask.FromResult(identifier == "confirm" ? (TValue)(object)Confirm : default!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    internal sealed class IpPageRenderer(IServiceProvider services, Type? componentType = null) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames => GetCurrentRenderTreeFrames(_root).Array.Take(GetCurrentRenderTreeFrames(_root).Count).ToArray();
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            _root = AssignRootComponentId(InstantiateComponent(componentType ?? typeof(IpRemote)));
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
        private RenderTreeFrame[] LabeledElement(string tag, string label) => Frames.Select((frame, index) => (frame, index))
            .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == tag)
            .Select(item => Frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
            .Single(item => item.Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Any(frame => frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
        public Task ClickAriaButtonAsync(string label) => Dispatcher.InvokeAsync(async () =>
        {
            var button = LabeledElement("button", label);
            Assert.DoesNotContain(button, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(button.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs());
        });
        public Task AssertElementDisabledAsync(string tag, string label, bool expected) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            LabeledElement(tag, label).Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true)));
        public Task AssertAriaButtonPresentAsync(string label, bool expected) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            Frames.Select((frame, index) => (frame, index)).Any(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "button"
                && Frames.Skip(item.index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .Any(frame => frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label))));
        public Task<string[]> ControlStructureAsync(string id) => Dispatcher.InvokeAsync(() =>
        {
            var item = Frames.Select((frame, index) => (frame, index)).Single(item => item.frame.FrameType == RenderTreeFrameType.Element
                && item.frame.ElementName == "article" && item.frame.ElementKey?.ToString() == id);
            return Frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).Where(frame => frame.FrameType == RenderTreeFrameType.Element)
                .Select(frame => frame.ElementName).ToArray();
        });
        public Task AssertExpertGroupsDraggableAsync() => Dispatcher.InvokeAsync(() =>
        {
            var groups = Frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "section")
                .Select(item => Frames.Skip(item.index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray())
                .Where(attributes => attributes.Any(frame => frame.AttributeName == "data-expert-group")).ToArray();
            Assert.NotEmpty(groups);
            foreach (var attributes in groups)
            {
                Assert.Contains(attributes, frame => frame.AttributeName == "draggable" && frame.AttributeValue?.ToString() == "true");
                Assert.Contains(attributes, frame => frame.AttributeName == "tabindex" && frame.AttributeValue?.ToString() == "0");
                Assert.Contains(attributes, frame => frame.AttributeName == "aria-keyshortcuts");
            }
        });
        public Task AssertExpertGroupOrderAsync(IEnumerable<string> expected) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            Frames.Where(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "data-expert-group").Select(frame => frame.AttributeValue?.ToString())));
        public Task AssertExpertGroupTextAsync(string id, params string[] expected) => Dispatcher.InvokeAsync(() =>
        {
            var group = Frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "section")
                .Select(item => Frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .Any(frame => frame.AttributeName == "data-expert-group" && frame.AttributeValue?.ToString() == id));
            foreach (var text in expected) Assert.Contains(text, Text(group), StringComparison.Ordinal);
        });
        public Task AssertTextAsync(string expected) => Dispatcher.InvokeAsync(() => Assert.Contains(expected, Text(Frames), StringComparison.Ordinal));
        public Task AssertTextAbsentAsync(string text) => Dispatcher.InvokeAsync(() => Assert.DoesNotContain(text, Text(Frames), StringComparison.Ordinal));
        public Task AssertClassPresentAsync(string name, bool expected = true) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            Frames.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "class"
                && (frame.AttributeValue?.ToString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal)
                || frame.FrameType == RenderTreeFrameType.Markup && frame.MarkupContent.Contains($"class=\"{name}\"", StringComparison.Ordinal))));
        public Task AssertSliderBoundsAsync(string label, string minimum, string maximum) => Dispatcher.InvokeAsync(() =>
        {
            var frames = Frames;
            var group = frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "div"
                    && frames[item.index + 1].AttributeName == "class" && frames[item.index + 1].AttributeValue?.ToString() == "direct-bounded-slider")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label + " slider"));
            Assert.Equal(new[] { "span", "input", "span" }, group.Skip(1).Where(frame => frame.FrameType == RenderTreeFrameType.Element).Select(frame => frame.ElementName));
            Assert.Equal(minimum + maximum, string.Concat(Text(group).Where(character => !char.IsWhiteSpace(character))));
        });
        public Task AssertTargetAsync(string label, int expected) => AssertInputValueAsync(label, expected.ToString());
        public Task AssertInputValueAsync(string label, string expected) => Dispatcher.InvokeAsync(() =>
        {
            var frames = Frames;
            var input = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "input")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            Assert.Equal(expected, input.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "value").AttributeValue?.ToString());
        });
        public Task UploadAsync(IBrowserFile file) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var component = frames.Select((frame, index) => (frame, index)).Single(item => item.frame.FrameType == RenderTreeFrameType.Component && item.frame.ComponentType == typeof(InputFile));
            var callback = (EventCallback<InputFileChangeEventArgs>)frames.Skip(component.index + 1).Take(component.frame.ComponentSubtreeLength - 1)
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "OnChange").AttributeValue;
            await callback.InvokeAsync(new InputFileChangeEventArgs([file]));
        });
        public Task AssertInputPresentAsync(string label, bool expected) => Dispatcher.InvokeAsync(() => Assert.Equal(expected,
            Frames.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label)));
        public Task ChangeAsync(string label, string value, string eventName = "oninput") => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var input = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "input")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            await DispatchEventAsync(input.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == eventName).AttributeEventHandlerId,
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
        public Task AssertCheckboxAsync(string label, bool expected) => Dispatcher.InvokeAsync(() =>
        {
            var frames = Frames;
            var input = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "input")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            Assert.Equal(expected, input.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "checked" && frame.AttributeValue is true));
        });
        public Task AssertRadioAsync(string label, bool expected) => Dispatcher.InvokeAsync(() =>
        {
            var input = LabeledElement("input", label);
            Assert.Contains(input, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "type" && frame.AttributeValue?.ToString() == "radio");
            Assert.Equal(expected, input.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "checked" && frame.AttributeValue is true));
        });
        private RenderTreeFrame[] ControlSelect()
        {
            var frames = Frames;
            return frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "select")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == "IP Remote picture control"));
        }
        public Task AssertSelectedControlAsync(string value) => Dispatcher.InvokeAsync(() => Assert.Equal(value,
            ControlSelect().Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == "value").AttributeValue));
        public Task SelectControlAsync(string value, string eventName = "onchange") => Dispatcher.InvokeAsync(async () =>
        {
            var select = ControlSelect();
            Assert.DoesNotContain(select, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(select.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == eventName).AttributeEventHandlerId,
                new EventFieldInfo { ComponentId = _root, FieldValue = value }, new ChangeEventArgs { Value = value });
        });
        public Task SelectAsync(string label, string value) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var select = frames.Select((frame, index) => (frame, index)).Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "select")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            Assert.DoesNotContain(select, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(select.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeEventHandlerId,
                new EventFieldInfo { ComponentId = _root, FieldValue = value }, new ChangeEventArgs { Value = value });
        });
        private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        { RenderTreeFrameType.Text => frame.TextContent, RenderTreeFrameType.Markup => frame.MarkupContent, _ => "" }));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
#pragma warning restore BL0006
