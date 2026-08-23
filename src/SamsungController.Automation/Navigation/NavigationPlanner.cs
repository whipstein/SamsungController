namespace SamsungController.Automation.Navigation;

public sealed record NavigationAnchorLeg(
    string AnchorId,
    string Label,
    string SourceNodeId,
    string SourcePath,
    string TargetNodeId,
    string TargetPath,
    IReadOnlyList<MenuOperation> Operations,
    bool Verified);

public sealed record NavigationPlan(
    string DefinitionId,
    string SourceNodeId,
    string SourcePath,
    string TargetNodeId,
    string TargetPath,
    IReadOnlyList<MenuTransition> Transitions,
    MenuTimingProfile Timing,
    NavigationAnchorLeg? AnchorLeg = null)
{
    public bool UsesDraftTransitions => Transitions.Any(transition => !transition.Verified);

    public bool UsesAnchor => AnchorLeg is not null;

    public bool IsExecutable => !UsesDraftTransitions && AnchorLeg?.Verified != false;

    public int CommandCount => (AnchorLeg?.Operations.Sum(operation => operation.Repeat) ?? 0)
        + Transitions.Sum(transition =>
            transition.Operations.Sum(operation => operation.Repeat));

    public TimeSpan EstimatedDelay => TimeSpan.FromTicks(
        (AnchorLeg?.Operations.Sum(operation =>
             (operation.DelayAfter ?? Timing.GetDelay(operation.Key)).Ticks
             * operation.Repeat) ?? 0)
        + Transitions.Sum(transition =>
            transition.Operations.Sum(operation =>
                (operation.DelayAfter ?? Timing.GetDelay(operation.Key)).Ticks
                * operation.Repeat)));
}

public sealed class NavigationPlanningException : Exception
{
    public NavigationPlanningException(string message)
        : base(message)
    {
    }
}

public sealed class NavigationPlanner
{
    public NavigationPlan Plan(
        MenuDefinition definition,
        string sourceNodeId,
        string targetNodeId,
        bool includeDraftTransitions = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        definition.GetRequiredNode(sourceNodeId);
        definition.GetRequiredNode(targetNodeId);

        if (sourceNodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return CreatePlan(definition, sourceNodeId, targetNodeId, []);
        }

        var distances = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceNodeId] = 0
        };
        var previous = new Dictionary<string, MenuTransition>(StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<string, long>();
        queue.Enqueue(sourceNodeId, 0);

        while (queue.TryDequeue(out var nodeId, out var distance))
        {
            if (!distances.TryGetValue(nodeId, out var bestDistance)
                || distance != bestDistance)
            {
                continue;
            }

            if (nodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            foreach (var transition in definition.Transitions.Values.Where(transition =>
                         transition.FromNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)
                         && (transition.Verified || includeDraftTransitions)))
            {
                var nextDistance = distance + GetCost(transition, definition.Timing);
                if (distances.TryGetValue(transition.ToNodeId, out var existing)
                    && existing <= nextDistance)
                {
                    continue;
                }

                distances[transition.ToNodeId] = nextDistance;
                previous[transition.ToNodeId] = transition;
                queue.Enqueue(transition.ToNodeId, nextDistance);
            }
        }

        if (!previous.ContainsKey(targetNodeId))
        {
            var qualification = includeDraftTransitions ? "" : " verified";
            throw new NavigationPlanningException(
                $"No{qualification} navigation route exists from '{definition.GetPath(sourceNodeId)}' to '{definition.GetPath(targetNodeId)}'.");
        }

        var reversed = new List<MenuTransition>();
        var current = targetNodeId;
        while (!current.Equals(sourceNodeId, StringComparison.OrdinalIgnoreCase))
        {
            var transition = previous[current];
            reversed.Add(transition);
            current = transition.FromNodeId;
        }

        reversed.Reverse();
        return CreatePlan(definition, sourceNodeId, targetNodeId, reversed);
    }

    private static NavigationPlan CreatePlan(
        MenuDefinition definition,
        string sourceNodeId,
        string targetNodeId,
        IReadOnlyList<MenuTransition> transitions) =>
        new(
            definition.Id,
            sourceNodeId,
            definition.GetPath(sourceNodeId),
            targetNodeId,
            definition.GetPath(targetNodeId),
            transitions,
            definition.Timing);

    private static long GetCost(MenuTransition transition, MenuTimingProfile timing)
    {
        const long draftPenalty = 1_000_000;
        var commands = transition.Operations.Sum(operation => operation.Repeat);
        var delayMilliseconds = transition.Operations.Sum(operation =>
            (long)Math.Ceiling(
                (operation.DelayAfter ?? timing.GetDelay(operation.Key)).TotalMilliseconds
                * operation.Repeat));
        return commands * 1000L
               + delayMilliseconds
               + (transition.Verified ? 0 : draftPenalty);
    }
}
