using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungController.Web.Components.Pages;

namespace SamsungController.Web.Tests;

// Exercise the actual rendered timing-row buttons without connecting a browser to a TV.
#pragma warning disable BL0006
internal sealed class VerificationPageTestRenderer(IServiceProvider services)
    : Renderer(services, NullLoggerFactory.Instance)
{
    private int _rootId;
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    private RenderTreeFrame[] TimingFrames
    {
        get
        {
            var current = GetCurrentRenderTreeFrames(_rootId);
            var frames = current.Array.Take(current.Count).ToArray();
            return frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "article")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(article => FrameText(article).Contains("System-wide command timing", StringComparison.Ordinal));
        }
    }

    public string TimingText => FrameText(TimingFrames);

    public Task StartAsync() => Dispatcher.InvokeAsync(async () =>
    {
        _rootId = AssignRootComponentId(InstantiateComponent(typeof(Verification)));
        await RenderRootComponentAsync(_rootId);
    });

    public Task SelectRouteAsync(string routeId) => DispatchAsync("select", null, "onchange",
        new ChangeEventArgs { Value = routeId });

    public Task ClickAsync(string label) => DispatchAsync("button", label, "onclick", new MouseEventArgs());

    private Task DispatchAsync(string tag, string? label, string eventName, EventArgs args) => Dispatcher.InvokeAsync(async () =>
    {
        var frames = TimingFrames;
        var element = frames.Select((frame, index) => (frame, index))
            .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == tag)
            .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
            .Single(item => label is null || FrameText(item).Trim() == label);
        Assert.DoesNotContain(element, frame => frame.FrameType == RenderTreeFrameType.Attribute
            && frame.AttributeName == "disabled" && frame.AttributeValue is true);
        var handler = element.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute
            && frame.AttributeName == eventName).AttributeEventHandlerId;
        await DispatchEventAsync(handler, null, args);
    });

    private static string FrameText(IEnumerable<RenderTreeFrame> frames) => string.Concat(frames.Select(frame => frame.FrameType switch
    {
        RenderTreeFrameType.Text => frame.TextContent,
        RenderTreeFrameType.Markup => System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(frame.MarkupContent, "<[^>]+>", string.Empty)),
        _ => string.Empty
    }));

    protected override void HandleException(Exception exception) =>
        throw new InvalidOperationException("Verification page render failed.", exception);
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
}
#pragma warning restore BL0006
