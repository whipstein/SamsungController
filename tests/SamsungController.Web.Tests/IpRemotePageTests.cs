using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

#pragma warning disable BL0006
public sealed class IpRemotePageTests
{
    [Fact]
    public async Task PageCanSavePairReadAndDownloadWithoutUsingTheExistingController()
    {
        var directory = Directory.CreateTempSubdirectory("SamsungController-IP-ui-").FullName;
        try
        {
            var client = new IpRemoteServiceTests.RecordingClient();
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
            await renderer.ChangeAsync("TV IP address or hostname", "192.0.2.11");
            await renderer.AssertDisabledAsync("Read both state queries", true);
            await renderer.ClickAsync("Discard unsaved edits");
            await renderer.AssertDisabledAsync("Read both state queries", false);
            await renderer.ClickAsync("New display");
            await renderer.AssertDisabledAsync("Pair again…", true);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class DownloadJavaScript : IJSRuntime
    {
        public string Download { get; private set; } = "";
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "samsungController.downloadText") Download = (string)args![1]!;
            return ValueTask.FromResult(identifier == "confirm" ? (TValue)(object)true : default!);
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
        private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        { RenderTreeFrameType.Text => frame.TextContent, RenderTreeFrameType.Markup => frame.MarkupContent, _ => "" }));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
#pragma warning restore BL0006
