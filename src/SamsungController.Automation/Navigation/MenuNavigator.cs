using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Navigation;

public interface IMenuCommandTarget
{
    Task SendKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken = default);
}

public interface IMenuDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

public sealed class SystemMenuDelay : IMenuDelay
{
    public static SystemMenuDelay Instance { get; } = new();

    private SystemMenuDelay()
    {
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default) =>
        Task.Delay(duration, cancellationToken);
}

public sealed record NavigationProgress(
    DateTimeOffset Timestamp,
    string Phase,
    string SourcePath,
    string TargetPath,
    int CommandNumber,
    int CommandCount,
    string Key,
    RemoteKeyAction Action);

public sealed class NavigationProgressEventArgs(NavigationProgress progress) : EventArgs
{
    public NavigationProgress Progress { get; } =
        progress ?? throw new ArgumentNullException(nameof(progress));
}

public sealed class MenuNavigator
{
    private readonly MenuDefinition _definition;
    private readonly MenuStateTracker _stateTracker;
    private readonly IMenuCommandTarget _target;
    private readonly IMenuDelay _delay;

    public MenuNavigator(
        MenuDefinition definition,
        MenuStateTracker stateTracker,
        IMenuCommandTarget target,
        IMenuDelay? delay = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _stateTracker = stateTracker ?? throw new ArgumentNullException(nameof(stateTracker));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _delay = delay ?? SystemMenuDelay.Instance;
        new MenuDefinitionValidator().ValidateAndThrow(definition);
    }

    public event EventHandler<NavigationProgressEventArgs>? ProgressChanged;

    public NavigationPlan Plan(string targetNodeId, bool includeDraftTransitions = true)
    {
        var state = _stateTracker.Current;
        if (state.NodeId is null)
        {
            throw new NavigationPlanningException(
                "Menu position is unknown. Run a verified anchor before planning navigation.");
        }

        return new NavigationPlanner().Plan(
            _definition,
            state.NodeId,
            targetNodeId,
            includeDraftTransitions);
    }

    public async Task ExecuteAnchorAsync(
        string anchorId,
        CancellationToken cancellationToken = default)
    {
        var anchor = _definition.GetRequiredAnchor(anchorId);
        if (!anchor.Verified)
        {
            throw new InvalidOperationException(
                $"Anchor '{anchor.Label}' is still draft and cannot be executed.");
        }

        try
        {
            await ExecuteOperationsAsync(
                    $"Anchor · {anchor.Label}",
                    _stateTracker.Current.Path ?? "Unknown",
                    _definition.GetPath(anchor.TargetNodeId),
                    anchor.Operations,
                    cancellationToken)
                .ConfigureAwait(false);
            _stateTracker.ApplyAnchor(anchor);
        }
        catch
        {
            _stateTracker.MarkUnknown($"Anchor '{anchor.Label}' did not complete.");
            throw;
        }
    }

    public async Task ExecutePlanAsync(
        NavigationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.DefinitionId.Equals(_definition.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The navigation plan belongs to a different menu definition.");
        }

        if (!plan.IsExecutable)
        {
            throw new InvalidOperationException(
                "The navigation plan contains draft transitions and cannot be executed.");
        }

        if (!string.Equals(
                _stateTracker.Current.NodeId,
                plan.SourceNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The predicted menu position changed after this plan was created. Create a new plan.");
        }

        try
        {
            foreach (var transition in plan.Transitions)
            {
                await ExecuteOperationsAsync(
                        $"Transition · {transition.Id}",
                        _definition.GetPath(transition.FromNodeId),
                        _definition.GetPath(transition.ToNodeId),
                        transition.Operations,
                        cancellationToken)
                    .ConfigureAwait(false);
                _stateTracker.ApplyTransition(transition);
            }
        }
        catch
        {
            _stateTracker.MarkUnknown("Navigation did not complete; the TV menu position may be partially advanced.");
            throw;
        }
    }

    private async Task ExecuteOperationsAsync(
        string phase,
        string sourcePath,
        string targetPath,
        IReadOnlyList<MenuOperation> operations,
        CancellationToken cancellationToken)
    {
        var commandCount = operations.Sum(operation => operation.Repeat);
        var commandNumber = 0;
        foreach (var operation in operations)
        {
            for (var repeat = 0; repeat < operation.Repeat; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                commandNumber++;
                PublishProgress(new NavigationProgress(
                    DateTimeOffset.UtcNow,
                    phase,
                    sourcePath,
                    targetPath,
                    commandNumber,
                    commandCount,
                    operation.Key,
                    operation.Action));
                await _target.SendKeyAsync(
                        operation.Key,
                        operation.Action,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (operation.DelayAfter is { } delay)
                {
                    await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void PublishProgress(NavigationProgress progress)
    {
        try
        {
            ProgressChanged?.Invoke(this, new NavigationProgressEventArgs(progress));
        }
        catch
        {
            // Progress observers cannot interrupt navigation.
        }
    }
}
