using System.Collections.ObjectModel;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Navigation;

public sealed record MenuDefinitionContext(
    string Firmware = "unrecorded",
    string Signal = "any",
    string PictureMode = "any",
    string Input = "any");

public sealed record MenuConfiguration(
    string Id,
    string Name,
    string? Conditions = null);

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

public enum MenuControlType
{
    Submenu,
    Slider,
    Selection,
    Switch,
    Confirmation
}

public sealed record MenuNodeDisabledCondition(
    string SettingNodeId,
    string EqualsValue);

public sealed record MenuNode(
    string Id,
    string Label,
    string? ParentId = null,
    string? Description = null,
    MenuControlType ControlType = MenuControlType.Submenu,
    string? DefaultValue = null,
    IReadOnlyList<MenuNodeDisabledCondition>? DisabledWhen = null,
    IReadOnlyList<string>? SelectionOptions = null,
    decimal? MinimumValue = null,
    decimal? MaximumValue = null);

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
    string? Description = null,
    IReadOnlyList<MenuOperation>? ReturnToVideoOperations = null,
    string? ConfigurationId = null);

public sealed record MenuReturnScript(
    IReadOnlyList<MenuOperation> Operations,
    bool Verified = false);

public sealed record MenuReturnOverride(
    string NodeId,
    MenuReturnScript Script);

public sealed record MenuReturnStrategy(
    string MenuRootNodeId,
    MenuReturnScript AtMenuRoot,
    MenuReturnScript BelowMenuRoot,
    IReadOnlyList<MenuReturnOverride>? NodeOverrides = null);

public sealed record MenuAnchor(
    string Id,
    string Label,
    string TargetNodeId,
    IReadOnlyList<MenuOperation> Operations,
    bool Verified = false,
    string? Description = null,
    MenuReturnStrategy? ReturnStrategy = null,
    string? ValidationSourceNodeId = null,
    string? ConfigurationId = null);

public sealed class MenuDefinition
{
    private readonly IReadOnlyDictionary<string, MenuNode> _nodes;
    private readonly IReadOnlyDictionary<string, MenuTransition> _transitions;
    private readonly IReadOnlyDictionary<string, MenuAnchor> _anchors;
    private readonly IReadOnlyDictionary<string, MenuConfiguration> _configurations;

    public MenuDefinition(
        string id,
        string name,
        string model,
        MenuDefinitionContext context,
        IEnumerable<MenuNode> nodes,
        IEnumerable<MenuTransition> transitions,
        IEnumerable<MenuAnchor> anchors,
        MenuTimingProfile? timing = null,
        IEnumerable<MenuConfiguration>? configurations = null,
        string? activeConfigurationId = null)
    {
        Id = id;
        Name = name;
        Model = model;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Timing = timing ?? new MenuTimingProfile();
        _nodes = ToUniqueDictionary(nodes, node => node.Id, "node");
        _transitions = ToUniqueDictionary(transitions, transition => transition.Id, "transition");
        _anchors = ToUniqueDictionary(anchors, anchor => anchor.Id, "anchor");
        _configurations = ToUniqueDictionary(
            configurations ?? [],
            configuration => configuration.Id,
            "configuration");
        ActiveConfigurationId = string.IsNullOrWhiteSpace(activeConfigurationId)
            ? null
            : activeConfigurationId.Trim();
    }

    public string Id { get; }

    public string Name { get; }

    public string Model { get; }

    public MenuDefinitionContext Context { get; }

    public MenuTimingProfile Timing { get; }

    public IReadOnlyDictionary<string, MenuNode> Nodes => _nodes;

    public IReadOnlyDictionary<string, MenuTransition> Transitions => _transitions;

    public IReadOnlyDictionary<string, MenuAnchor> Anchors => _anchors;

    public IReadOnlyDictionary<string, MenuConfiguration> Configurations => _configurations;

    public string? ActiveConfigurationId { get; }

    public IEnumerable<MenuTransition> ApplicableTransitions => _transitions.Values.Where(
        transition => IsApplicableToActiveConfiguration(transition.ConfigurationId));

    public IEnumerable<MenuAnchor> ApplicableAnchors => _anchors.Values.Where(
        anchor => IsApplicableToActiveConfiguration(anchor.ConfigurationId));

    public bool IsApplicableToActiveConfiguration(string? configurationId) =>
        string.IsNullOrWhiteSpace(configurationId)
        || (!string.IsNullOrWhiteSpace(ActiveConfigurationId)
            && configurationId.Equals(ActiveConfigurationId, StringComparison.OrdinalIgnoreCase));

    public MenuDefinition WithActiveConfiguration(string? configurationId) => new(
        Id,
        Name,
        Model,
        Context,
        Nodes.Values,
        Transitions.Values,
        Anchors.Values,
        Timing,
        Configurations.Values,
        configurationId);

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

    public bool IsDescendantOf(string nodeId, string ancestorNodeId)
    {
        var current = GetRequiredNode(nodeId);
        GetRequiredNode(ancestorNodeId);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(current.ParentId))
        {
            if (!visited.Add(current.Id))
            {
                throw new InvalidOperationException(
                    $"Menu parent cycle encountered while resolving '{nodeId}'.");
            }

            if (current.ParentId.Equals(ancestorNodeId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = GetRequiredNode(current.ParentId);
        }

        return false;
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
