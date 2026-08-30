namespace SamsungController.Automation.Navigation;

public static class MenuDefinitionVerificationReconciler
{
    public static MenuDefinition Reconcile(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Verification is not { } manifest)
        {
            return definition;
        }

        var plan = MenuDefinitionVerificationPlanner.Create(definition);
        var checks = plan.Checks.ToDictionary(check => check.Id, StringComparer.OrdinalIgnoreCase);
        var staleIds = manifest.Checks
            .Where(record => !checks.TryGetValue(record.Id, out var current)
                || !current.Fingerprint.Equals(record.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (staleIds.Count == 0)
        {
            return definition;
        }

        var validRecords = manifest.Checks
            .Where(record => checks.TryGetValue(record.Id, out var current)
                && current.Fingerprint.Equals(record.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var timing = staleIds.Contains("timing")
            ? definition.Timing with { Verified = false }
            : definition.Timing;
        var anchors = definition.Anchors.Values.Select(anchor =>
        {
            var configuration = NormalizeConfiguration(anchor.ConfigurationId);
            var verified = !staleIds.Contains($"anchor:{configuration}:{anchor.Id}")
                           && anchor.Verified;
            if (anchor.ReturnStrategy is not { } strategy)
            {
                return anchor with { Verified = verified };
            }

            var atMenuRoot = InvalidateScript(
                strategy.AtMenuRoot,
                staleIds.Contains($"return:{configuration}:{anchor.Id}:menu-root"));
            var belowMenuRoot = InvalidateScript(
                strategy.BelowMenuRoot,
                staleIds.Contains($"return:{configuration}:{anchor.Id}:below-root"));
            var overrides = (strategy.NodeOverrides ?? []).Select(item => item with
            {
                Script = InvalidateScript(
                    item.Script,
                    staleIds.Contains($"return:{configuration}:{anchor.Id}:override:{item.NodeId}"))
            }).ToArray();
            return anchor with
            {
                Verified = verified,
                ReturnStrategy = strategy with
                {
                    AtMenuRoot = atMenuRoot,
                    BelowMenuRoot = belowMenuRoot,
                    NodeOverrides = overrides
                }
            };
        }).ToArray();
        var staleValidationGroups = definition.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology && transition.IsValidationRoute)
            .Where(transition => staleIds.Contains(RouteCheckId(transition)))
            .Select(transition => transition.ValidationGroupId)
            .Where(groupId => !string.IsNullOrWhiteSpace(groupId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transitions = definition.Transitions.Values.Select(transition =>
        {
            var isStale = !transition.GeneratedFromTopology
                ? staleIds.Contains(RouteCheckId(transition))
                : transition.ValidationGroupId is { } groupId
                  && staleValidationGroups.Contains(groupId);
            return isStale ? transition with { Verified = false } : transition;
        }).ToArray();

        return new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            transitions,
            anchors,
            timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId,
            new MenuVerificationManifest(manifest.Display, validRecords));
    }

    private static MenuReturnScript InvalidateScript(MenuReturnScript script, bool stale) =>
        stale ? script with { Verified = false } : script;

    private static string RouteCheckId(MenuTransition transition)
    {
        var configuration = NormalizeConfiguration(transition.ConfigurationId);
        var routeIdentity = transition.ValidationGroupId ?? transition.Id;
        return $"route:{configuration}:{routeIdentity}";
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}
