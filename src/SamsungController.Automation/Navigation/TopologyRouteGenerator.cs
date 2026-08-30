using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Navigation;

public static class TopologyRouteGenerator
{
    public static MenuDefinition Regenerate(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var explicitTransitions = definition.Transitions.Values
            .Where(transition => !transition.GeneratedFromTopology)
            .ToArray();
        var existingGenerated = definition.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology)
            .ToArray();
        var orderedNodes = definition.Nodes.Values.ToArray();
        var generated = new Dictionary<string, MenuTransition>(StringComparer.OrdinalIgnoreCase);
        var usedIds = explicitTransitions
            .Select(transition => transition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in explicitTransitions
                     .Where(seed => CanGenerateFromSeed(definition, orderedNodes, seed))
                     .OrderByDescending(seed => definition.GetDepth(seed.ToNodeId))
                     .ThenBy(seed => seed.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var target in orderedNodes.Where(node =>
                         definition.IsDescendantOf(node.Id, seed.ToNodeId)))
            {
                var routeKey = CreateRouteKey(seed.ConfigurationId, target.Id);
                if (generated.ContainsKey(routeKey)
                    || HasExplicitRouteToTarget(
                        explicitTransitions,
                        target.Id,
                        seed.ConfigurationId))
                {
                    continue;
                }

                if (!TryCreateTopologyOperations(
                        definition,
                        orderedNodes,
                        seed,
                        target.Id,
                        out var operations,
                        out var branchNodeId))
                {
                    continue;
                }

                var groupId = $"topology-{seed.Id}-{branchNodeId}";
                var existing = existingGenerated.FirstOrDefault(candidate =>
                    candidate.TopologySeedTransitionId?.Equals(
                        seed.Id,
                        StringComparison.OrdinalIgnoreCase) == true
                    && candidate.ToNodeId.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
                    && SameConfiguration(candidate.ConfigurationId, seed.ConfigurationId));
                var id = existing?.Id
                    ?? CreateAvailableId($"topology-{seed.Id}-to-{target.Id}", usedIds);
                usedIds.Add(id);
                var effectiveOperations = existing is not null
                                          && HaveSameCommands(existing.Operations, operations)
                    ? existing.Operations
                    : operations;
                generated[routeKey] = new MenuTransition(
                    id,
                    seed.FromNodeId,
                    target.Id,
                    effectiveOperations,
                    false,
                    $"Generated from the ordered menu topology using '{seed.Id}'.",
                    ConfigurationId: seed.ConfigurationId,
                    GeneratedFromTopology: true,
                    TopologySeedTransitionId: seed.Id,
                    ValidationGroupId: groupId);
            }
        }

        var finalizedGenerated = generated.Values
            .GroupBy(
                transition => transition.ValidationGroupId!,
                StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => FinalizeValidationGroup(
                group.ToArray(),
                existingGenerated,
                explicitTransitions))
            .ToArray();
        return new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            explicitTransitions.Concat(finalizedGenerated),
            definition.Anchors.Values,
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId);
    }

    private static bool CanGenerateFromSeed(
        MenuDefinition definition,
        IReadOnlyList<MenuNode> nodes,
        MenuTransition seed) =>
        definition.Nodes.TryGetValue(seed.ToNodeId, out var target)
        && target.ControlType == MenuControlType.Submenu
        && definition.IsDescendantOf(seed.ToNodeId, seed.FromNodeId)
        && nodes.Any(node => node.ParentId?.Equals(
            seed.ToNodeId,
            StringComparison.OrdinalIgnoreCase) == true);

    private static bool HasExplicitRouteToTarget(
        IReadOnlyList<MenuTransition> explicitTransitions,
        string targetNodeId,
        string? configurationId) => explicitTransitions.Any(transition =>
        transition.ToNodeId.Equals(targetNodeId, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(transition.ConfigurationId)
            || SameConfiguration(transition.ConfigurationId, configurationId)));

    private static bool TryCreateTopologyOperations(
        MenuDefinition definition,
        IReadOnlyList<MenuNode> orderedNodes,
        MenuTransition seed,
        string targetNodeId,
        out IReadOnlyList<MenuOperation> operations,
        out string branchNodeId)
    {
        var path = new Stack<MenuNode>();
        var current = definition.GetRequiredNode(targetNodeId);
        while (!current.Id.Equals(seed.ToNodeId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(current.ParentId))
            {
                operations = [];
                branchNodeId = string.Empty;
                return false;
            }

            path.Push(current);
            current = definition.GetRequiredNode(current.ParentId);
        }

        if (path.Count == 0)
        {
            operations = [];
            branchNodeId = string.Empty;
            return false;
        }

        branchNodeId = path.Peek().Id;
        var generated = seed.Operations.ToList();
        var parent = definition.GetRequiredNode(seed.ToNodeId);
        while (path.TryPop(out var child))
        {
            if (parent.ControlType != MenuControlType.Submenu)
            {
                operations = [];
                return false;
            }

            var selectableChildren = orderedNodes.Where(candidate =>
                    candidate.ParentId?.Equals(parent.Id, StringComparison.OrdinalIgnoreCase) == true
                    && !IsDisabledByDefault(definition, candidate))
                .ToArray();
            var childIndex = Array.FindIndex(selectableChildren, candidate =>
                candidate.Id.Equals(child.Id, StringComparison.OrdinalIgnoreCase));
            if (childIndex < 0)
            {
                operations = [];
                return false;
            }

            if (childIndex > 0)
            {
                generated.Add(new MenuOperation("KEY_DOWN", Repeat: childIndex));
            }

            if (child.ControlType == MenuControlType.Submenu)
            {
                generated.Add(new MenuOperation("KEY_ENTER"));
            }
            else if (path.Count > 0)
            {
                operations = [];
                return false;
            }

            parent = child;
        }

        operations = Coalesce(generated);
        return true;
    }

    private static IReadOnlyList<MenuTransition> FinalizeValidationGroup(
        IReadOnlyList<MenuTransition> generated,
        IReadOnlyList<MenuTransition> existingGenerated,
        IReadOnlyList<MenuTransition> explicitTransitions)
    {
        var existingGroup = existingGenerated.Where(candidate =>
                candidate.ValidationGroupId?.Equals(
                    generated[0].ValidationGroupId,
                    StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var unchanged = existingGroup.Length == generated.Count
                        && generated.All(route => existingGroup.Any(existing =>
                            existing.ToNodeId.Equals(route.ToNodeId, StringComparison.OrdinalIgnoreCase)
                            && HaveSameCommands(existing.Operations, route.Operations)));
        var seedVerified = explicitTransitions.Any(transition =>
            transition.Id.Equals(
                generated[0].TopologySeedTransitionId,
                StringComparison.OrdinalIgnoreCase)
            && transition.Verified);
        var verified = unchanged
                       && seedVerified
                       && existingGroup.All(route => route.Verified);
        var previousValidationTarget = unchanged
            ? existingGroup.FirstOrDefault(route => route.IsValidationRoute)?.ToNodeId
            : null;
        var validationTarget = previousValidationTarget
            ?? generated
                .OrderByDescending(route => route.Operations.Sum(operation => operation.Repeat))
                .ThenBy(route => route.ToNodeId, StringComparer.OrdinalIgnoreCase)
                .First()
                .ToNodeId;
        return generated.Select(route => route with
        {
            Verified = verified,
            IsValidationRoute = route.ToNodeId.Equals(
                validationTarget,
                StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }

    private static bool IsDisabledByDefault(MenuDefinition definition, MenuNode node) =>
        (node.DisabledWhen ?? []).Any(condition =>
            definition.Nodes.TryGetValue(condition.SettingNodeId, out var setting)
            && setting.DefaultValue?.Equals(
                condition.EqualsValue,
                StringComparison.OrdinalIgnoreCase) == true);

    private static IReadOnlyList<MenuOperation> Coalesce(IEnumerable<MenuOperation> operations)
    {
        var result = new List<MenuOperation>();
        foreach (var operation in operations)
        {
            if (result.Count > 0)
            {
                var previous = result[^1];
                if (previous.Key.Equals(operation.Key, StringComparison.OrdinalIgnoreCase)
                    && previous.Action == operation.Action
                    && previous.DelayAfter == operation.DelayAfter)
                {
                    result[^1] = previous with { Repeat = previous.Repeat + operation.Repeat };
                    continue;
                }
            }

            result.Add(operation);
        }

        return result;
    }

    private static bool HaveSameCommands(
        IReadOnlyList<MenuOperation> left,
        IReadOnlyList<MenuOperation> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Key.Equals(pair.Second.Key, StringComparison.OrdinalIgnoreCase)
            && pair.First.Action == pair.Second.Action
            && pair.First.Repeat == pair.Second.Repeat);

    private static string CreateRouteKey(string? configurationId, string targetNodeId) =>
        $"{configurationId?.Trim().ToLowerInvariant() ?? "*"}\u001f{targetNodeId.Trim().ToLowerInvariant()}";

    private static bool SameConfiguration(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? null : right.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string CreateAvailableId(string preferred, ISet<string> usedIds)
    {
        if (!usedIds.Contains(preferred))
        {
            return preferred;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{preferred}-{suffix}";
            if (!usedIds.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
