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

    public async Task PrepareStateAsync(
        string targetNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        _definition.GetRequiredNode(targetNodeId);
        var state = _stateTracker.Current;
        if (state.NodeId is not null)
        {
            if (state.NodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ExecutePlanAsync(
                    Plan(targetNodeId, includeDraftTransitions: false),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var preparation = FindUnknownStatePreparation(targetNodeId);
        await ExecuteAnchorAsync(preparation.Anchor.Id, cancellationToken).ConfigureAwait(false);
        if (!preparation.Anchor.TargetNodeId.Equals(
                targetNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            await ExecutePlanAsync(preparation.Route, cancellationToken).ConfigureAwait(false);
        }
    }

    public void ValidateStateCanBePrepared(string targetNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        _definition.GetRequiredNode(targetNodeId);
        _ = FindUnknownStatePreparation(targetNodeId);
    }

    // Explicit test setup may exercise recorded drafts. Normal navigation still
    // requires verified anchors and routes; preparation never grants verification.
    public async Task PrepareValidationSourceAsync(
        string sourceNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeId);
        _definition.GetRequiredNode(sourceNodeId);
        var preparation = FindUnknownStatePreparation(sourceNodeId, includeDrafts: true);
        var script = ResolveAnchorScript(preparation.Anchor, includeDraftScripts: true);
        try
        {
            await ExecuteOperationsAsync(
                    $"Prepare test start · {preparation.Anchor.Label} · {script.Label}",
                    _stateTracker.Current.Path ?? "Unknown",
                    _definition.GetPath(preparation.Anchor.TargetNodeId),
                    script.Operations,
                    cancellationToken)
                .ConfigureAwait(false);
            _stateTracker.AssumeNode(preparation.Anchor.TargetNodeId,
                "Test preparation sent the defined anchor; visually check the TV before testing.");
            foreach (var transition in preparation.Route.Transitions)
            {
                await ExecuteOperationsAsync(
                        $"Prepare test start · {transition.Id}",
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
            _stateTracker.MarkUnknown("Test preparation did not complete; manually check the TV position.");
            throw;
        }
    }

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

        foreach (var anchor in _definition.ApplicableAnchors.Where(anchor =>
                     anchor.Verified
                     && !anchor.TargetNodeId.Equals(
                         state.NodeId,
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (TryCreateCalculatedPlan(
                    planner,
                    anchor,
                    state,
                    targetNodeId,
                    includeDraftTransitions,
                    out var calculatedPlan))
            {
                candidates.Add(calculatedPlan);
            }

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
        if (!_definition.IsApplicableToActiveConfiguration(anchor.ConfigurationId))
        {
            throw new InvalidOperationException(
                $"Anchor '{anchor.Label}' is not valid for the active menu configuration.");
        }

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

    private UnknownStatePreparation FindUnknownStatePreparation(string targetNodeId, bool includeDrafts = false)
    {
        var planner = new NavigationPlanner();
        var candidates = new List<UnknownStatePreparation>();
        foreach (var anchor in _definition.ApplicableAnchors.Where(anchor => anchor.Verified || includeDrafts))
        {
            try
            {
                var route = planner.Plan(
                    _definition,
                    anchor.TargetNodeId,
                    targetNodeId,
                    includeDraftTransitions: includeDrafts);
                candidates.Add(new UnknownStatePreparation(anchor, route));
            }
            catch (NavigationPlanningException)
            {
                // This anchor cannot establish the requested state through the allowed routes.
            }
        }

        return candidates
            .OrderBy(candidate => !candidate.Anchor.Verified || candidate.Route.UsesDraftTransitions)
            .ThenBy(candidate =>
                GetOperationsCost(candidate.Anchor.Operations)
                + GetPlanCost(candidate.Route))
            .ThenBy(candidate => candidate.Anchor.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new NavigationPlanningException(includeDrafts
                ? $"No defined anchor and route can prepare starting state '{_definition.GetPath(targetNodeId)}'. Define the missing path in Build & Verify, or position the TV manually and select Run test."
                : $"No verified anchor can prepare starting state '{_definition.GetPath(targetNodeId)}'.");
    }

    private ResolvedAnchorScript ResolveAnchorScript(MenuAnchor anchor, bool includeDraftScripts = false)
    {
        var state = _stateTracker.Current;
        if (state.NodeId is null
            || state.Confidence is not (MenuStateConfidence.Synchronized or MenuStateConfidence.Probable))
        {
            return new ResolvedAnchorScript("unknown fallback", anchor.Operations);
        }

        var nodeOverride = anchor.ReturnStrategy?.NodeOverrides?.FirstOrDefault(item =>
            item.NodeId.Equals(state.NodeId, StringComparison.OrdinalIgnoreCase));
        if (nodeOverride is not null && (nodeOverride.Script.Verified || includeDraftScripts))
        {
            return new ResolvedAnchorScript(
                $"{_definition.GetPath(nodeOverride.NodeId)} override",
                nodeOverride.Script.Operations);
        }

        var integratedReturn = _definition.ApplicableTransitions
            .Where(transition => transition.ToNodeId.Equals(
                    state.NodeId,
                    StringComparison.OrdinalIgnoreCase)
                && (includeDraftScripts || transition.Verified
                    || state.Confidence == MenuStateConfidence.Synchronized)
                && transition.ReturnToVideoOperations is { Count: > 0 })
            .OrderBy(transition => transition.ReturnToVideoOperations!.Sum(
                operation => operation.Repeat))
            .ThenBy(transition => transition.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (integratedReturn is not null)
        {
            var scriptKind = integratedReturn.Verified
                ? "recorded return"
                : "visually confirmed draft return";
            return new ResolvedAnchorScript(
                $"{_definition.GetPath(integratedReturn.ToNodeId)} {scriptKind}",
                integratedReturn.ReturnToVideoOperations!);
        }

        if (anchor.ReturnStrategy is not { } strategy)
        {
            return new ResolvedAnchorScript("fallback", anchor.Operations);
        }

        if (state.NodeId.Equals(strategy.MenuRootNodeId, StringComparison.OrdinalIgnoreCase)
            && (strategy.AtMenuRoot.Verified || includeDraftScripts))
        {
            return new ResolvedAnchorScript("menu root script", strategy.AtMenuRoot.Operations);
        }

        if (_definition.IsDescendantOf(state.NodeId, strategy.MenuRootNodeId)
            && (strategy.BelowMenuRoot.Verified || includeDraftScripts))
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
                "The navigation plan contains draft, unverified, or incompatible legs and cannot be executed.");
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

            if (plan.CalculatedLeg is { } calculatedLeg)
            {
                if (!calculatedLeg.BasedOnVerifiedRoutes
                    || !calculatedLeg.SourceNodeId.Equals(
                        plan.SourceNodeId,
                        StringComparison.OrdinalIgnoreCase)
                    || !calculatedLeg.TargetNodeId.Equals(
                        plan.TargetNodeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The navigation plan contains an invalid calculated route leg.");
                }

                await ExecuteOperationsAsync(
                        $"Calculated · {calculatedLeg.Label}",
                        calculatedLeg.SourcePath,
                        calculatedLeg.TargetPath,
                        calculatedLeg.Operations,
                        cancellationToken)
                    .ConfigureAwait(false);
                _stateTracker.ApplyTransition(new MenuTransition(
                    $"calculated-{calculatedLeg.SourceNodeId}-to-{calculatedLeg.TargetNodeId}",
                    calculatedLeg.SourceNodeId,
                    calculatedLeg.TargetNodeId,
                    calculatedLeg.Operations,
                    Verified: true,
                    Description: calculatedLeg.Basis));
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
        var calculatedCost = plan.CalculatedLeg is null
            ? 0
            : GetOperationsCost(plan.CalculatedLeg.Operations);
        return anchorCost + calculatedCost + plan.Transitions.Sum(transition =>
            GetOperationsCost(transition.Operations)
            + (transition.Verified ? 0 : draftPenalty));
    }

    private bool TryCreateCalculatedPlan(
        NavigationPlanner planner,
        MenuAnchor anchor,
        MenuState state,
        string targetNodeId,
        bool includeDraftTransitions,
        out NavigationPlan plan)
    {
        plan = null!;
        NavigationPlan sourceRoute;
        NavigationPlan targetRoute;
        try
        {
            sourceRoute = planner.Plan(
                _definition,
                anchor.TargetNodeId,
                state.NodeId!,
                includeDraftTransitions);
            targetRoute = planner.Plan(
                _definition,
                anchor.TargetNodeId,
                targetNodeId,
                includeDraftTransitions);
        }
        catch (NavigationPlanningException)
        {
            return false;
        }

        if (!sourceRoute.IsExecutable || !targetRoute.IsExecutable)
        {
            return false;
        }

        var sourcePresses = ExpandOperations(sourceRoute.Transitions);
        var targetPresses = ExpandOperations(targetRoute.Transitions);
        var commonPressCount = 0;
        while (commonPressCount < sourcePresses.Count
               && commonPressCount < targetPresses.Count
               && HasSameCommand(
                   sourcePresses[commonPressCount],
                   targetPresses[commonPressCount]))
        {
            commonPressCount++;
        }

        var usedAncestorReturnRoute = false;
        var submenuReturnDelay = TimeSpan.FromMilliseconds(Math.Max(
            _definition.Timing.ReturnDelayMilliseconds,
            _definition.Timing.ScreenChangeDelayMilliseconds));
        List<MenuOperation> relativePresses;
        if (_definition.IsDescendantOf(state.NodeId!, targetNodeId)
            && commonPressCount == targetPresses.Count
            && TryCreateAncestorReturnPresses(
                sourcePresses,
                commonPressCount,
                submenuReturnDelay,
                out var ancestorReturnPresses))
        {
            usedAncestorReturnRoute = true;
            relativePresses = ancestorReturnPresses;
        }
        else
        {
            if (!TryCreateRelativeOperations(
                    sourcePresses,
                    targetPresses,
                    submenuReturnDelay,
                    commonPressCount,
                    out relativePresses))
            {
                return false;
            }
        }

        var operations = CollapseRepeats(relativePresses);
        if (operations.Count == 0)
        {
            return false;
        }

        var sourcePath = state.Path ?? _definition.GetPath(state.NodeId!);
        var targetPath = _definition.GetPath(targetNodeId);
        plan = new NavigationPlan(
            _definition.Id,
            state.NodeId!,
            sourcePath,
            targetNodeId,
            targetPath,
            [],
            _definition.Timing,
            CalculatedLeg: new NavigationCalculatedLeg(
                $"{sourcePath} → {targetPath}",
                state.NodeId!,
                sourcePath,
                targetNodeId,
                targetPath,
                operations,
                usedAncestorReturnRoute
                    ? $"Calculated from verified menu levels via {anchor.Label}; {relativePresses.Count} level returns with {submenuReturnDelay.TotalMilliseconds:0}ms waits."
                    : $"Calculated from two verified routes from {anchor.Label}; {commonPressCount} shared commands, returning directly through exited submenu levels.",
                BasedOnVerifiedRoutes: true));
        return true;
    }

    private static IReadOnlyList<MenuOperation> ExpandOperations(
        IReadOnlyList<MenuTransition> transitions) => transitions
        .SelectMany(transition => transition.Operations)
        .SelectMany(operation => Enumerable.Range(0, operation.Repeat)
            .Select(_ => operation with { Repeat = 1 }))
        .ToArray();

    private static bool HasSameCommand(MenuOperation left, MenuOperation right) =>
        left.Key.Equals(right.Key, StringComparison.OrdinalIgnoreCase)
        && left.Action == right.Action;

    internal static bool TryCreateRelativeOperations(
        IReadOnlyList<MenuOperation> sourcePresses,
        IReadOnlyList<MenuOperation> targetPresses,
        TimeSpan submenuReturnDelay,
        int commonPressCount,
        out List<MenuOperation> relativePresses)
    {
        relativePresses = [];
        var firstUnsharedEntry = sourcePresses.Count;
        for (var index = commonPressCount; index < sourcePresses.Count; index++)
        {
            if (sourcePresses[index].Key.Trim().Equals("KEY_ENTER", StringComparison.OrdinalIgnoreCase)
                && sourcePresses[index].Action == RemoteKeyAction.Click)
            {
                firstUnsharedEntry = index;
                break;
            }
        }

        for (var index = sourcePresses.Count - 1; index >= commonPressCount; index--)
        {
            if (!TryInvert(
                    sourcePresses[index],
                    submenuReturnDelay,
                    out var inverse))
            {
                relativePresses = [];
                return false;
            }

            // Return leaves a submenu with its entry row highlighted in the
            // parent. Rewinding rows inside that submenu first is unnecessary.
            // Keep every level exit and the directional offset on the shared
            // level; still reject unsupported/non-click operations above.
            if (index > firstUnsharedEntry && inverse.Key != "KEY_RETURN")
            {
                continue;
            }

            AddAndCancelDirectionalOpposites(relativePresses, inverse);
        }

        for (var index = commonPressCount; index < targetPresses.Count; index++)
        {
            AddAndCancelDirectionalOpposites(relativePresses, targetPresses[index]);
        }

        return relativePresses.Count > 0;
    }

    private static bool TryInvert(
        MenuOperation operation,
        TimeSpan submenuReturnDelay,
        out MenuOperation inverse)
    {
        var inverseKey = operation.Action == RemoteKeyAction.Click
            ? operation.Key.Trim().ToUpperInvariant() switch
            {
                "KEY_UP" => "KEY_DOWN",
                "KEY_DOWN" => "KEY_UP",
                "KEY_LEFT" => "KEY_RIGHT",
                "KEY_RIGHT" => "KEY_LEFT",
                "KEY_ENTER" => "KEY_RETURN",
                _ => null
            }
            : null;
        inverse = new MenuOperation(
            inverseKey ?? operation.Key,
            RemoteKeyAction.Click,
            DelayAfter: inverseKey == "KEY_RETURN" ? submenuReturnDelay : null);
        return inverseKey is not null;
    }

    private static bool TryCreateAncestorReturnPresses(
        IReadOnlyList<MenuOperation> sourcePresses,
        int commonPressCount,
        TimeSpan returnDelay,
        out List<MenuOperation> returnPresses)
    {
        returnPresses = [];
        for (var index = sourcePresses.Count - 1; index >= commonPressCount; index--)
        {
            var operation = sourcePresses[index];
            if (operation.Action != RemoteKeyAction.Click)
            {
                return false;
            }

            switch (operation.Key.Trim().ToUpperInvariant())
            {
                case "KEY_ENTER":
                    returnPresses.Add(new MenuOperation(
                        "KEY_RETURN",
                        DelayAfter: returnDelay));
                    break;
                case "KEY_UP":
                case "KEY_DOWN":
                case "KEY_LEFT":
                case "KEY_RIGHT":
                    break;
                default:
                    return false;
            }
        }

        return returnPresses.Count > 0;
    }

    private static void AddAndCancelDirectionalOpposites(
        List<MenuOperation> operations,
        MenuOperation operation)
    {
        if (operations.Count > 0
            && AreOppositeDirectionalCommands(operations[^1], operation))
        {
            operations.RemoveAt(operations.Count - 1);
            return;
        }

        operations.Add(operation);
    }

    private static bool AreOppositeDirectionalCommands(
        MenuOperation left,
        MenuOperation right)
    {
        if (left.Action != RemoteKeyAction.Click || right.Action != RemoteKeyAction.Click)
        {
            return false;
        }

        var leftKey = left.Key.Trim().ToUpperInvariant();
        var rightKey = right.Key.Trim().ToUpperInvariant();
        return (leftKey, rightKey) is
            ("KEY_UP", "KEY_DOWN") or
            ("KEY_DOWN", "KEY_UP") or
            ("KEY_LEFT", "KEY_RIGHT") or
            ("KEY_RIGHT", "KEY_LEFT");
    }

    private static IReadOnlyList<MenuOperation> CollapseRepeats(
        IReadOnlyList<MenuOperation> presses)
    {
        var operations = new List<MenuOperation>();
        foreach (var press in presses)
        {
            if (operations.Count > 0
                && HasSameCommand(operations[^1], press)
                && operations[^1].DelayAfter == press.DelayAfter)
            {
                operations[^1] = operations[^1] with
                {
                    Repeat = operations[^1].Repeat + 1
                };
                continue;
            }

            operations.Add(press);
        }

        return operations;
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

    private sealed record UnknownStatePreparation(
        MenuAnchor Anchor,
        NavigationPlan Route);
}
