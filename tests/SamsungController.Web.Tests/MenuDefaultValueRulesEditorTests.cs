using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungController.Automation.Navigation;
using SamsungController.Web.Components;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

#pragma warning disable BL0006
public sealed class MenuDefaultValueRulesEditorTests
{
    [Fact]
    public async Task EditorCombinesPictureModeWithSignalConditions()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new RuleRenderer(services);
        await renderer.StartAsync();
        await renderer.ClickAsync("+ Add default rule");
        await renderer.ChangeAsync("Rule 1: add menu condition", "setting:picture-mode");
        await renderer.ChangeAsync("Rule 1: Picture Mode", "Game");
        await renderer.ChangeAsync("Rule 1: Bit depth", "10-bit");
        Assert.Equal("Game", renderer.Rules[0].When["setting:picture-mode"]);
        Assert.Equal("10-bit", renderer.Rules[0].When["depth"]);
        await renderer.ChangeAsync("Rule 1: Picture Mode", "");
        Assert.DoesNotContain("setting:picture-mode", renderer.Rules[0].When.Keys);
    }

    [Fact]
    public async Task EditorAddsCombinedConditionsReordersAndRemovesRules()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new RuleRenderer(services);
        await renderer.StartAsync();
        await renderer.ClickAsync("+ Add default rule");
        await renderer.ChangeAsync("Rule 1: Bit depth", "10-bit");
        await renderer.ChangeAsync("Rule 1: default value", "Multi word ", input: true);
        Assert.Equal("Multi word ", renderer.Rules[0].Value);
        await renderer.ChangeAsync("Rule 1: default value", "40", input: true);
        var rule = Assert.Single(renderer.Rules);
        Assert.Equal("40", rule.Value);
        Assert.Equal("RGB", rule.When["format"]);
        Assert.Equal("10-bit", rule.When["depth"]);
        await renderer.ClickAsync("+ Add default rule");
        await renderer.ClickAsync("Move default rule 2 up", aria: true);
        Assert.Equal("25", renderer.Rules[0].Value);
        Assert.Equal("40", renderer.Rules[1].Value);
        await renderer.ClickAsync("Remove rule 1");
        Assert.Equal("40", Assert.Single(renderer.Rules).Value);
        await renderer.ChangeAsync("Rule 1: Color format", "");
        Assert.Single(renderer.Rules[0].When);
        await renderer.ChangeAsync("Rule 1: Bit depth", "");
        Assert.Contains("Select at least one condition", renderer.Text);
        await renderer.ClickAsync("Remove rule 1");
        Assert.Empty(renderer.Rules);
    }

    private sealed class RuleRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        private int _root;
        public IReadOnlyList<MenuNodeDefaultValueRule> Rules { get; private set; } = [];
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames => GetCurrentRenderTreeFrames(_root).Array.Take(GetCurrentRenderTreeFrames(_root).Count).ToArray();
        public string Text => FrameText(Frames);
        public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(MenuDefaultValueRulesEditor)));
            await RenderAsync();
        });
        private Task RenderAsync() => RenderRootComponentAsync(_root, ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Rules"] = Rules,
            ["RulesChanged"] = EventCallback.Factory.Create<IReadOnlyList<MenuNodeDefaultValueRule>>(this, async rules =>
            {
                Rules = rules;
                await RenderAsync();
            }),
            ["States"] = new MenuExternalStateSummary[]
            {
                new("format", "Color format", "RGB", "RGB", ["RGB", "YCbCr422"]),
                new("depth", "Bit depth", "8-bit", "8-bit", ["8-bit", "10-bit"]),
                new("setting:picture-mode", "Picture Mode", "Movie", "Movie", ["Movie", "Game"])
            },
            ["FallbackValue"] = "25"
        }));
        public Task ClickAsync(string label, bool aria = false) => DispatchAsync("button", label, "onclick", new MouseEventArgs(), aria);
        public Task ChangeAsync(string label, string value, bool input = false) => DispatchAsync(input ? "input" : "select", label,
            input ? "oninput" : "onchange", new ChangeEventArgs { Value = value }, true);
        private Task DispatchAsync(string tag, string label, string eventName, EventArgs args, bool aria) => Dispatcher.InvokeAsync(async () =>
        {
            var frames = Frames;
            var element = frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == tag)
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(item => aria
                    ? item.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "aria-label" && frame.AttributeValue?.ToString() == label)
                    : FrameText(item).Trim() == label);
            Assert.DoesNotContain(element, frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && frame.AttributeValue is true);
            await DispatchEventAsync(element.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == eventName).AttributeEventHandlerId, null, args);
        });
        private static string FrameText(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => string.Empty
        }));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
#pragma warning restore BL0006
