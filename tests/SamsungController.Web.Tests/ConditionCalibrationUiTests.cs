using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

#pragma warning disable BL0006
public sealed partial class ControllerMenuIntegrationTests
{
    [Fact]
    public async Task CalibrationUploadAndDownloadButtonsHandleAllConditionsWithoutTvCommands()
    {
        var yaml = ConditionalDefaultsYaml.Replace("  - id: open-settings\n    from: normal-video\n    to: settings\n    verified: true\n    steps:\n      - key: KEY_MENU\n", "", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            foreach (var check in controller.GetMenuDefinitionVerificationSnapshot().Checks.ToArray())
                await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            await controller.SaveMenuControlBehaviorPreferencesAsync(true, false);
            var javascript = new CalibrationDownloadJavaScript();
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
            await using var renderer = new CalibrationPageRenderer(services);
            await renderer.StartAsync();
            await renderer.UploadAsync(MenuControlTargetProfileSerializer.Serialize(CombinedCalibration("22", "45")));
            Assert.Equal("22", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().CurrentValues!);
            await renderer.ClickAsync("Enter current settings", contains: true);
            await renderer.UploadAsync(MenuControlTargetProfileSerializer.Serialize(CombinedCalibration("21", "44")));
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("22", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            await renderer.ClickAsync("Download all current settings");
            var currentFile = MenuControlTargetProfileSerializer.Deserialize(javascript.LastJson!);
            Assert.Equal(2, currentFile.ConditionValues!.Count);
            Assert.Equal(new[] { "21", "44" }, currentFile.ConditionValues.Select(set => Assert.Single(set.Values).Value).Order());
            await renderer.Dispatcher.InvokeAsync(() => controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit"));
            Assert.Equal("44", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await renderer.ClickAsync("Adjust TV", contains: true);
            await renderer.ClickAsync("Download all targets");
            var targetFile = MenuControlTargetProfileSerializer.Deserialize(javascript.LastJson!);
            Assert.Equal(2, targetFile.ConditionValues!.Count);
            Assert.Equal("45", targetFile.ConditionValues.Single(set => set.Conditions["hdmi-bit-depth"] == "10-bit").Values.Single(value => value.NodeId == "brightness").Value);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    private sealed class CalibrationPageRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames => GetCurrentRenderTreeFrames(_root).Array.Take(GetCurrentRenderTreeFrames(_root).Count).ToArray();
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(PictureControls)));
            await RenderRootComponentAsync(_root, ParameterView.Empty);
        });
        public Task UploadAsync(string json) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var component = frames.Select((frame, index) => (frame, index)).Single(item =>
                item.frame.FrameType == RenderTreeFrameType.Component && item.frame.ComponentType == typeof(InputFile));
            var callback = frames.Skip(component.index + 1).Take(component.frame.ComponentSubtreeLength - 1)
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "OnChange");
            await Assert.IsType<EventCallback<InputFileChangeEventArgs>>(callback.AttributeValue)
                .InvokeAsync(new InputFileChangeEventArgs([new CalibrationBrowserFile(json)]));
        });
        public Task ClickAsync(string label, bool contains = false) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var button = frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "button")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => contains ? Text(item).Contains(label, StringComparison.Ordinal) : Text(item).Trim() == label);
            Assert.DoesNotContain(button, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(button.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs());
        });
        public Task ChangeAsync(string label, string value) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var element = frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName is "input" or "select")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label));
            await DispatchEventAsync(element.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onchange").AttributeEventHandlerId,
                null, new ChangeEventArgs { Value = value });
        });
        private static string Text(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => string.Empty
        }));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class CalibrationBrowserFile(string json) : IBrowserFile
    {
        public string Name => "all-conditions.samsung-calibration.json";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => Encoding.UTF8.GetByteCount(json);
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private sealed class CalibrationDownloadJavaScript : IJSRuntime
    {
        public string? LastJson { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "samsungController.downloadText") LastJson = Assert.IsType<string>(args![1]);
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
#pragma warning restore BL0006
