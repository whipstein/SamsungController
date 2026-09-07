using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungController.Web.Components.Pages;

namespace SamsungController.Web.Tests;

// Exercise actual verification-row buttons without connecting a browser to a TV.
#pragma warning disable BL0006
internal sealed class VerificationPageTestRenderer(IServiceProvider services)
    : Renderer(services, NullLoggerFactory.Instance)
{
    private int _rootId;
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    public string LineLabel { get; set; } = "System-wide command timing";
    public string? CheckId { get; set; }
    public Action? OnRendered { get; set; }
    private RenderTreeFrame[] Frames
    {
        get
        {
            var current = GetCurrentRenderTreeFrames(_rootId);
            return current.Array.Take(current.Count).ToArray();
        }
    }
    private RenderTreeFrame[] LineFrames
    {
        get
        {
            var frames = Frames;
            return frames.Select((frame, index) => (frame, index))
                .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "article")
                .Select(item => frames.Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
                .Single(article => CheckId is not null
                    ? article.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute
                        && frame.AttributeName == "id" && frame.AttributeValue?.ToString() == $"verification-check-{CheckId}")
                    : article.Select((frame, index) => (frame, index))
                    .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "strong")
                    .Any(item => FrameText(article.Skip(item.index).Take(item.frame.ElementSubtreeLength)).Trim() == LineLabel));
        }
    }

    public string TimingText => LineText;
    public string LineText => FrameText(LineFrames);
    public string Text => FrameText(Frames);
    public string BoldLineText => string.Join(" ", LineFrames.Select((frame, index) => (frame, index))
        .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == "strong")
        .Select(item => FrameText(LineFrames.Skip(item.index).Take(item.frame.ElementSubtreeLength)))
        .Concat(LineFrames.Where(frame => frame.FrameType == RenderTreeFrameType.Markup)
            .SelectMany(frame => System.Text.RegularExpressions.Regex.Matches(frame.MarkupContent, "<strong>(.*?)</strong>")
                .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)))));

    private RenderTreeFrame[] FindElement(string tag, string? label, bool wholePage = false) =>
        (wholePage ? Frames : LineFrames).Select((frame, index) => (frame, index))
            .Where(item => item.frame.FrameType == RenderTreeFrameType.Element && item.frame.ElementName == tag)
            .Select(item => (wholePage ? Frames : LineFrames).Skip(item.index).Take(item.frame.ElementSubtreeLength).ToArray())
            .Single(item => label is null || FrameText(item).Trim().StartsWith(label, StringComparison.Ordinal));

    public bool ButtonDisabled(string label) => HasTrueAttribute(FindElement("button", label), "disabled");
    public bool SignalConfirmationDisabled => HasTrueAttribute(FindElement("input", null), "disabled");
    public bool SignalConfirmationChecked => HasTrueAttribute(FindElement("input", null), "checked");
    private static bool HasTrueAttribute(IEnumerable<RenderTreeFrame> frames, string name) => frames.Any(frame =>
        frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name && frame.AttributeValue is true);

    public Task StartAsync(Type? componentType = null) => Dispatcher.InvokeAsync(async () =>
    {
        _rootId = AssignRootComponentId(InstantiateComponent(componentType ?? typeof(Verification)));
        await RenderRootComponentAsync(_rootId);
    });

    public Task SelectRouteAsync(string routeId) => DispatchAsync("select", null, "onchange",
        new ChangeEventArgs { Value = routeId });

    public Task ClickAsync(string label) => DispatchAsync("button", label, "onclick", new MouseEventArgs());
    public Task ConfirmSignalAsync(bool confirmed) => DispatchAsync("input", null, "onchange", new ChangeEventArgs { Value = confirmed });
    public Task ClickPageButtonAsync(string label) => DispatchAsync("button", label, "onclick", new MouseEventArgs(), wholePage: true);

    public string? LinkDestination => LineFrames.FirstOrDefault(frame => frame.FrameType == RenderTreeFrameType.Attribute
        && frame.AttributeName == "href").AttributeValue?.ToString();

    private Task DispatchAsync(string tag, string? label, string eventName, EventArgs args, bool wholePage = false) => Dispatcher.InvokeAsync(async () =>
    {
        var element = FindElement(tag, label, wholePage);
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
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        OnRendered?.Invoke();
        return Task.CompletedTask;
    }
}
#pragma warning restore BL0006
