using System.Collections.ObjectModel;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Navigation;

public sealed record MenuDefinitionContext(
    string Firmware = "unrecorded",
    string Signal = "any",
    string PictureMode = "any",
    string Input = "any");

public sealed record MenuTimingProfile(
    int DefaultDelayMilliseconds = 150,
    int ScreenChangeDelayMilliseconds = 500,
    int ReturnDelayMilliseconds = 300,
    bool Verified = false)
{
    public bool HasSameDelays(MenuTimingProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return DefaultDelayMilliseconds == other.DefaultDelayMilliseconds
               && ScreenChangeDelayMilliseconds == other.ScreenChangeDelayMilliseconds
               && ReturnDelayMilliseconds == other.ReturnDelayMilliseconds;
    }

    public TimeSpan GetDelay(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var milliseconds = key.Trim().ToUpperInvariant() switch
        {
            "KEY_MENU" or "KEY_ENTER" or "KEY_HOME" or "KEY_EXIT" or "KEY_SOURCE" =>
                ScreenChangeDelayMilliseconds,
            "KEY_RETURN" => ReturnDelayMilliseconds,
            _ => DefaultDelayMilliseconds
        };
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

public sealed record MenuNode(
    string Id,
    string Label,
    string? ParentId = null,
    string? Description = null);

public sealed record MenuOperation(
    string Key,
    RemoteKeyAction Action = RemoteKeyAction.Click,
    int Repeat = 1,
    TimeSpan? DelayAfter = null);

public sealed record MenuTransition(
    string Id,
    string FromNodeId,
    string ToNodeId,
    IReadOnlyList<MenuOperation> Operations,
    bool Verified = false,
    string? Description = null);

public sealed record MenuAnchor(
    string Id,
    string Label,
    string TargetNodeId,
    IReadOnlyList<MenuOperation> Operations,
    bool Verified = false,
    string? Description = null);

public sealed class MenuDefinition
{
    private readonly IReadOnlyDictionary<string, MenuNode> _nodes;
    private readonly IReadOnlyDictionary<string, MenuTransition> _transitions;
    private readonly IReadOnlyDictionary<string, MenuAnchor> _anchors;

    public MenuDefinition(
        string id,
        string name,
        string model,
        MenuDefinitionContext context,
        IEnumerable<MenuNode> nodes,
        IEnumerable<MenuTransition> transitions,
        IEnumerable<MenuAnchor> anchors,
        MenuTimingProfile? timing = null)
    {
        Id = id;
        Name = name;
        Model = model;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Timing = timing ?? new MenuTimingProfile();
        _nodes = ToUniqueDictionary(nodes, node => node.Id, "node");
        _transitions = ToUniqueDictionary(transitions, transition => transition.Id, "transition");
        _anchors = ToUniqueDictionary(anchors, anchor => anchor.Id, "anchor");
    }

    public string Id { get; }

    public string Name { get; }

    public string Model { get; }

    public MenuDefinitionContext Context { get; }

    public MenuTimingProfile Timing { get; }

    public IReadOnlyDictionary<string, MenuNode> Nodes => _nodes;

    public IReadOnlyDictionary<string, MenuTransition> Transitions => _transitions;

    public IReadOnlyDictionary<string, MenuAnchor> Anchors => _anchors;

    public MenuNode GetRequiredNode(string nodeId) =>
        _nodes.TryGetValue(nodeId, out var node)
            ? node
            : throw new KeyNotFoundException($"Menu node '{nodeId}' was not found.");

    public MenuAnchor GetRequiredAnchor(string anchorId) =>
        _anchors.TryGetValue(anchorId, out var anchor)
            ? anchor
            : throw new KeyNotFoundException($"Menu anchor '{anchorId}' was not found.");

    public string GetPath(string nodeId)
    {
        var labels = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = GetRequiredNode(nodeId);
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException(
                    $"Menu parent cycle encountered while resolving '{nodeId}'.");
            }

            labels.Push(current.Label);
            if (string.IsNullOrWhiteSpace(current.ParentId))
            {
                break;
            }

            current = GetRequiredNode(current.ParentId);
        }

        return string.Join(" / ", labels);
    }

    public int GetDepth(string nodeId)
    {
        var depth = 0;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = GetRequiredNode(nodeId);
        while (!string.IsNullOrWhiteSpace(current.ParentId))
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException(
                    $"Menu parent cycle encountered while resolving '{nodeId}'.");
            }

            depth++;
            current = GetRequiredNode(current.ParentId);
        }

        return depth;
    }

    private static IReadOnlyDictionary<string, T> ToUniqueDictionary<T>(
        IEnumerable<T> values,
        Func<T, string> getId,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var id = getId(value);
            if (!result.TryAdd(id, value))
            {
                throw new ArgumentException(
                    $"Menu {kind} identifiers must be unique ignoring case: '{id}'.",
                    nameof(values));
            }
        }

        return new ReadOnlyDictionary<string, T>(result);
    }
}
