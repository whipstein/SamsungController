using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Navigation;

public enum MenuStateConfidence
{
    Unknown,
    Low,
    Probable,
    Synchronized
}

public sealed record MenuState(
    string? NodeId,
    string? Path,
    MenuStateConfidence Confidence,
    string Reason,
    DateTimeOffset UpdatedAt);

public sealed class MenuStateTracker
{
    private static readonly HashSet<string> MenuAffectingKeys = new(
    [
        "KEY_UP",
        "KEY_DOWN",
        "KEY_LEFT",
        "KEY_RIGHT",
        "KEY_ENTER",
        "KEY_RETURN",
        "KEY_HOME",
        "KEY_MENU",
        "KEY_EXIT",
        "KEY_SOURCE"
    ],
        StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly MenuDefinition _definition;
    private MenuState _state = new(
        null,
        null,
        MenuStateConfidence.Unknown,
        "No anchor has established the current menu position.",
        DateTimeOffset.UtcNow);

    public MenuStateTracker(MenuDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        new MenuDefinitionValidator().ValidateAndThrow(definition);
    }

    public event EventHandler? Changed;

    public MenuState Current
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public void ApplyAnchor(MenuAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        _definition.GetRequiredNode(anchor.TargetNodeId);
        SetState(new MenuState(
            anchor.TargetNodeId,
            _definition.GetPath(anchor.TargetNodeId),
            anchor.Verified ? MenuStateConfidence.Synchronized : MenuStateConfidence.Probable,
            $"Anchor '{anchor.Label}' completed.",
            DateTimeOffset.UtcNow));
    }

    public void ApplyTransition(MenuTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var current = Current;
        if (!string.Equals(
                current.NodeId,
                transition.FromNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            MarkUnknown(
                $"Transition '{transition.Id}' did not begin at the predicted menu node.");
            return;
        }

        var confidence = current.Confidence switch
        {
            MenuStateConfidence.Synchronized or MenuStateConfidence.Probable =>
                MenuStateConfidence.Probable,
            _ => MenuStateConfidence.Low
        };
        SetState(new MenuState(
            transition.ToNodeId,
            _definition.GetPath(transition.ToNodeId),
            confidence,
            $"Navigation transition '{transition.Id}' completed without a TV-state acknowledgement.",
            DateTimeOffset.UtcNow));
    }

    public void ConfirmNode(string nodeId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _definition.GetRequiredNode(nodeId);
        SetState(new MenuState(
            nodeId,
            _definition.GetPath(nodeId),
            MenuStateConfidence.Synchronized,
            reason,
            DateTimeOffset.UtcNow));
    }

    public void AssumeNode(string nodeId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _definition.GetRequiredNode(nodeId);
        SetState(new MenuState(
            nodeId,
            _definition.GetPath(nodeId),
            MenuStateConfidence.Probable,
            reason,
            DateTimeOffset.UtcNow));
    }

    public void ObserveCommand(string key, RemoteKeyAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!MenuAffectingKeys.Contains(key))
        {
            return;
        }

        var current = Current;
        if (current.NodeId is null)
        {
            return;
        }

        var matches = _definition.ApplicableTransitions.Where(transition =>
                transition.Verified
                && transition.FromNodeId.Equals(current.NodeId, StringComparison.OrdinalIgnoreCase)
                && transition.Operations.Count == 1
                && transition.Operations[0].Repeat == 1
                && transition.Operations[0].Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                && transition.Operations[0].Action == action)
            .ToArray();
        if (matches.Length == 1)
        {
            ApplyTransition(matches[0]);
            return;
        }

        MarkUnknown(matches.Length == 0
            ? $"Command '{key}' has no verified transition from the predicted node."
            : $"Command '{key}' is ambiguous from the predicted node.");
    }

    public void ReduceConfidence(string reason)
    {
        var current = Current;
        if (current.NodeId is null || current.Confidence == MenuStateConfidence.Unknown)
        {
            return;
        }

        SetState(current with
        {
            Confidence = MenuStateConfidence.Low,
            Reason = reason,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public void MarkUnknown(string reason) =>
        SetState(new MenuState(
            null,
            null,
            MenuStateConfidence.Unknown,
            reason,
            DateTimeOffset.UtcNow));

    private void SetState(MenuState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Observers cannot interrupt state tracking.
        }
    }
}
