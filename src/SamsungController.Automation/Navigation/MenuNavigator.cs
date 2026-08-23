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

        var planner = new NavigationPlanner();
        var candidates = new List<NavigationPlan>();
        NavigationPlanningException? directRouteError = null;
        try
        {
            candidates.Add(planner.Plan(
                _definition,
                state.NodeId,
                targetNodeId,
                includeDraftTransitions));
        }
        catch (NavigationPlanningException exception)
        {
            directRouteError = exception;
        }

        foreach (var anchor in _definition.Anchors.Values.Where(anchor =>
                     anchor.Verified
                     && !anchor.TargetNodeId.Equals(
                         state.NodeId,
                         StringComparison.OrdinalIgnoreCase)))
        {
            NavigationPlan routeFromAnchor;
            try
            {
                routeFromAnchor = planner.Plan(
                    _definition,
                    anchor.TargetNodeId,
                    targetNodeId,
                    includeDraftTransitions);
            }
            catch (NavigationPlanningException)
            {
                continue;
            }

            var script = ResolveAnchorScript(anchor);
            candidates.Add(routeFromAnchor with
            {
                SourceNodeId = state.NodeId,
                SourcePath = state.Path ?? _definition.GetPath(state.NodeId),
                AnchorLeg = new NavigationAnchorLeg(
                    anchor.Id,
                    $"{anchor.Label} · {script.Label}",
                    state.NodeId,
                    state.Path ?? _definition.GetPath(state.NodeId),
                    anchor.TargetNodeId,
                    _definition.GetPath(anchor.TargetNodeId),
                    script.Operations,
                    anchor.Verified)
            });
        }

        if (candidates.Count == 0)
        {
            throw directRouteError ?? new NavigationPlanningException(
                $"No navigation route exists from '{state.Path}' to '{_definition.GetPath(targetNodeId)}'.");
        }

        return candidates
            .OrderBy(GetPlanCost)
            .ThenBy(plan => plan.UsesAnchor)
            .First();
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
            var script = ResolveAnchorScript(anchor);
            await ExecuteOperationsAsync(
                    $"Anchor · {anchor.Label} · {script.Label}",
                    _stateTracker.Current.Path ?? "Unknown",
                    _definition.GetPath(anchor.TargetNodeId),
                    script.Operations,
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

    private ResolvedAnchorScript ResolveAnchorScript(MenuAnchor anchor)
    {
        var state = _stateTracker.Current;
        if (state.NodeId is null
            || state.Confidence is not (MenuStateConfidence.Synchronized or MenuStateConfidence.Probable))
        {
            return new ResolvedAnchorScript("unknown fallback", anchor.Operations);
        }

        if (anchor.ReturnStrategy is not { } strategy)
        {
            return new ResolvedAnchorScript("fallback", anchor.Operations);
        }

        if (state.NodeId.Equals(strategy.MenuRootNodeId, StringComparison.OrdinalIgnoreCase)
            && strategy.AtMenuRoot.Verified)
        {
            return new ResolvedAnchorScript("menu root script", strategy.AtMenuRoot.Operations);
        }

        if (_definition.IsDescendantOf(state.NodeId, strategy.MenuRootNodeId)
            && strategy.BelowMenuRoot.Verified)
        {
            return new ResolvedAnchorScript("deeper menu script", strategy.BelowMenuRoot.Operations);
        }

        return new ResolvedAnchorScript("unverified strategy fallback", anchor.Operations);
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
            if (plan.AnchorLeg is { } anchorLeg)
            {
                var anchor = _definition.GetRequiredAnchor(anchorLeg.AnchorId);
                if (!anchor.Verified
                    || !anchor.TargetNodeId.Equals(
                        anchorLeg.TargetNodeId,
                        StringComparison.OrdinalIgnoreCase)
                    || !anchorLeg.SourceNodeId.Equals(
                        plan.SourceNodeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The navigation plan contains an invalid known-state anchor leg.");
                }

                await ExecuteOperationsAsync(
                        $"Anchor · {anchorLeg.Label}",
                        anchorLeg.SourcePath,
                        anchorLeg.TargetPath,
                        anchorLeg.Operations,
                        cancellationToken)
                    .ConfigureAwait(false);
                _stateTracker.ApplyAnchor(anchor);
            }

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
                var delay = operation.DelayAfter ?? _definition.Timing.GetDelay(operation.Key);
                await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
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

    private long GetPlanCost(NavigationPlan plan)
    {
        const long draftPenalty = 1_000_000;
        var anchorCost = plan.AnchorLeg is null
            ? 0
            : GetOperationsCost(plan.AnchorLeg.Operations);
        return anchorCost + plan.Transitions.Sum(transition =>
            GetOperationsCost(transition.Operations)
            + (transition.Verified ? 0 : draftPenalty));
    }

    private long GetOperationsCost(IReadOnlyList<MenuOperation> operations)
    {
        var commands = operations.Sum(operation => operation.Repeat);
        var delayMilliseconds = operations.Sum(operation =>
            (long)Math.Ceiling(
                (operation.DelayAfter ?? _definition.Timing.GetDelay(operation.Key)).TotalMilliseconds
                * operation.Repeat));
        return commands * 1000L + delayMilliseconds;
    }

    private sealed record ResolvedAnchorScript(
        string Label,
        IReadOnlyList<MenuOperation> Operations);
}
