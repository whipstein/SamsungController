namespace SamsungController.Automation.Navigation;

/// <summary>
/// Keeps persisted menu topology independent from display-specific verification.
/// Runtime navigation receives an effective definition with matching local
/// verification evidence applied in memory.
/// </summary>
public static class MenuDefinitionVerificationOverlay
{
    public static MenuDefinition CreateTopology(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var anchors = definition.Anchors.Values.Select(anchor => anchor with
        {
            Verified = false,
            ReturnStrategy = anchor.ReturnStrategy is null
                ? null
                : anchor.ReturnStrategy with
                {
                    AtMenuRoot = anchor.ReturnStrategy.AtMenuRoot with { Verified = false },
                    BelowMenuRoot = anchor.ReturnStrategy.BelowMenuRoot with { Verified = false },
                    NodeOverrides = (anchor.ReturnStrategy.NodeOverrides ?? [])
                        .Select(item => item with
                        {
                            Script = item.Script with { Verified = false }
                        })
                        .ToArray()
                }
        });
        return new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            definition.Transitions.Values.Select(transition => transition with
            {
                Verified = false
            }),
            anchors,
            definition.Timing with { Verified = false },
            definition.Configurations.Values,
            definition.ActiveConfigurationId,
            externalStates: definition.ExternalStates.Values);
    }

    public static MenuDefinition Apply(
        MenuDefinition topology,
        MenuVerificationManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(topology);
        var cleanTopology = CreateTopology(topology);
        if (manifest is null)
        {
            return cleanTopology;
        }

        var plan = MenuDefinitionVerificationPlanner.Create(cleanTopology);
        var currentChecks = plan.Checks.ToDictionary(
            check => check.Id,
            StringComparer.OrdinalIgnoreCase);
        var verifiedCheckIds = manifest.Checks
            .Where(record => currentChecks.TryGetValue(record.Id, out var check)
                && check.Fingerprint.Equals(
                    record.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verifiedSourceItems = plan.Checks
            .Where(check => verifiedCheckIds.Contains(check.Id)
                && check.SourceItemId is not null)
            .ToArray();
        var verifiedAnchorIds = verifiedSourceItems
            .Where(check => check.Kind == MenuVerificationCheckKind.Anchor)
            .Select(check => check.SourceItemId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var verifiedTransitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedRouteIds = plan.Checks
            .Where(check => check.Kind == MenuVerificationCheckKind.Route
                && check.SourceItemId is not null)
            .Select(check => check.SourceItemId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var transition in cleanTopology.Transitions.Values.Where(transition =>
                     !plannedRouteIds.Contains(transition.Id)
                     && cleanTopology.GetRequiredNode(transition.ToNodeId).ControlType
                     == MenuControlType.Confirmation))
        {
            // Destructive/reset confirmations are intentionally excluded from visual
            // verification. Their topology remains usable by the guarded reset flow.
            verifiedTransitionIds.Add(transition.Id);
        }

        foreach (var check in verifiedSourceItems.Where(check =>
                     check.Kind == MenuVerificationCheckKind.Route))
        {
            var transition = cleanTopology.Transitions.TryGetValue(
                    check.SourceItemId!,
                    out var candidate)
                ? candidate
                : throw new InvalidOperationException(
                    $"Verification route '{check.SourceItemId}' is not present in the menu topology.");
            if (transition.GeneratedFromTopology
                && !string.IsNullOrWhiteSpace(transition.ValidationGroupId))
            {
                foreach (var grouped in cleanTopology.Transitions.Values.Where(candidate =>
                             candidate.GeneratedFromTopology
                             && candidate.ValidationGroupId?.Equals(
                                 transition.ValidationGroupId,
                                 StringComparison.OrdinalIgnoreCase) == true))
                {
                    verifiedTransitionIds.Add(grouped.Id);
                }
            }
            else
            {
                verifiedTransitionIds.Add(transition.Id);
            }
        }

        var anchors = cleanTopology.Anchors.Values.Select(anchor =>
        {
            var configuration = NormalizeConfiguration(anchor.ConfigurationId);
            if (anchor.ReturnStrategy is not { } strategy)
            {
                return anchor with { Verified = verifiedAnchorIds.Contains(anchor.Id) };
            }

            var prefix = $"return:{configuration}:{anchor.Id}:";
            var overrides = (strategy.NodeOverrides ?? []).Select(item => item with
            {
                Script = item.Script with
                {
                    Verified = verifiedCheckIds.Contains($"{prefix}override:{item.NodeId}")
                }
            }).ToArray();
            return anchor with
            {
                Verified = verifiedAnchorIds.Contains(anchor.Id),
                ReturnStrategy = strategy with
                {
                    AtMenuRoot = strategy.AtMenuRoot with
                    {
                        Verified = verifiedCheckIds.Contains($"{prefix}menu-root")
                    },
                    BelowMenuRoot = strategy.BelowMenuRoot with
                    {
                        Verified = verifiedCheckIds.Contains($"{prefix}below-root")
                    },
                    NodeOverrides = overrides
                }
            };
        });
        return new MenuDefinition(
            cleanTopology.Id,
            cleanTopology.Name,
            cleanTopology.Model,
            cleanTopology.Context,
            cleanTopology.Nodes.Values,
            cleanTopology.Transitions.Values.Select(transition => transition with
            {
                Verified = verifiedTransitionIds.Contains(transition.Id)
            }),
            anchors,
            cleanTopology.Timing with { Verified = verifiedCheckIds.Contains("timing") },
            cleanTopology.Configurations.Values,
            cleanTopology.ActiveConfigurationId,
            manifest,
            cleanTopology.ExternalStates.Values);
    }

    public static MenuVerificationManifest? CreateLegacyManifest(
        MenuDefinition definition,
        DateTimeOffset verifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var plan = MenuDefinitionVerificationPlanner.Create(definition);
        var records = plan.Checks
            .Where(check => check.ExistingEvidenceReady)
            .Select(check => new MenuVerificationRecord(
                check.Id,
                check.Fingerprint,
                verifiedAtUtc))
            .ToArray();
        return records.Length == 0
            ? null
            : new MenuVerificationManifest(plan.Display, records);
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}
