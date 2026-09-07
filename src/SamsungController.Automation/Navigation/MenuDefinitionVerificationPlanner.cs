using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SamsungController.Automation.Navigation;

public enum MenuVerificationCheckKind
{
    Display,
    Timing,
    Anchor,
    ReturnScript,
    Route,
    CalculatedNavigation,
    SliderBehavior,
    Selection,
    Switch,
    Confirmation,
    ConditionalVisibility
}

public sealed record MenuDefinitionVerificationCheck(
    string Id,
    MenuVerificationCheckKind Kind,
    string Label,
    string Description,
    string Fingerprint,
    string? TargetNodeId = null,
    bool ExistingEvidenceReady = false,
    string? ConfigurationId = null,
    string? SourceItemId = null,
    string? SourceNodeId = null,
    string? PreparationAnchorId = null,
    string? ExternalStateId = null,
    string? ExternalStateValue = null,
    string? RepresentativeNodeId = null);

public sealed record MenuDefinitionVerificationPlan(
    MenuVerificationDisplay Display,
    IReadOnlyList<MenuDefinitionVerificationCheck> Checks);

public static class MenuDefinitionVerificationPlanner
{
    private const string FingerprintVersion = "samsung-menu-verification-v1";

    public static MenuDefinitionVerificationPlan Create(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var display = new MenuVerificationDisplay(
            definition.Model,
            definition.Context.Firmware);
        var checks = new List<MenuDefinitionVerificationCheck>();

        Add(
            checks,
            display,
            "display",
            MenuVerificationCheckKind.Display,
            "Display combination",
            $"Confirm {FormatDisplay(display)} matches the display being tested.",
            CanonicalDisplay(display));
        Add(
            checks,
            display,
            "timing",
            MenuVerificationCheckKind.Timing,
            "System-wide command timing",
            "Verify the Up/Down, Left/Right adjustment, screen-change, and Return waits against the display.",
            $"{definition.Timing.DefaultDelayMilliseconds}|{definition.Timing.ScreenChangeDelayMilliseconds}|{definition.Timing.ReturnDelayMilliseconds}|{definition.Timing.AdjustmentDelayMilliseconds}",
            existingEvidenceReady: definition.Timing.Verified);

        foreach (var anchor in definition.Anchors.Values
                     .OrderBy(item => item.ConfigurationId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            var configuration = NormalizeConfiguration(anchor.ConfigurationId);
            Add(
                checks,
                display,
                $"anchor:{configuration}:{anchor.Id}",
                MenuVerificationCheckKind.Anchor,
                $"Anchor · {anchor.Label}",
                $"Verify the known-state route to {definition.GetPath(anchor.TargetNodeId)}.",
                $"{anchor.Id}|{anchor.TargetNodeId}|{configuration}|{Operations(anchor.Operations)}",
                anchor.TargetNodeId,
                anchor.Verified,
                anchor.ConfigurationId,
                anchor.Id);

            if (anchor.ReturnStrategy is not { } strategy)
            {
                continue;
            }

            var belowRootSourceNodeId = definition.Nodes.Values
                .Where(node => !node.Id.Equals(strategy.MenuRootNodeId, StringComparison.OrdinalIgnoreCase)
                    && definition.IsDescendantOf(node.Id, strategy.MenuRootNodeId))
                .OrderByDescending(node => definition.GetDepth(node.Id))
                .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(node => node.Id)
                .FirstOrDefault() ?? strategy.MenuRootNodeId;
            AddReturnScript(checks, display, definition, anchor, configuration, "menu-root", "At menu root", strategy.MenuRootNodeId, strategy.AtMenuRoot);
            AddReturnScript(checks, display, definition, anchor, configuration, "below-root", "Below menu root", belowRootSourceNodeId, strategy.BelowMenuRoot);
            foreach (var item in strategy.NodeOverrides ?? [])
            {
                AddReturnScript(checks, display, definition, anchor, configuration, $"override:{item.NodeId}", $"Override · {definition.GetRequiredNode(item.NodeId).Label}", item.NodeId, item.Script);
            }
        }

        foreach (var transition in definition.Transitions.Values
                     .Where(item => (!item.GeneratedFromTopology || item.IsValidationRoute)
                         && !IsPermanentlyDisabled(
                             definition,
                             definition.GetRequiredNode(item.ToNodeId))
                         && !IsUnsafeConfirmationTarget(
                             definition.GetRequiredNode(item.ToNodeId)))
                     .OrderBy(item => item.ConfigurationId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => definition.GetDepth(item.ToNodeId))
                     .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            var configuration = NormalizeConfiguration(transition.ConfigurationId);
            var routeIdentity = transition.ValidationGroupId ?? transition.Id;
            Add(
                checks,
                display,
                $"route:{configuration}:{routeIdentity}",
                MenuVerificationCheckKind.Route,
                $"Route · {definition.GetRequiredNode(transition.ToNodeId).Label}",
                $"Verify {definition.GetPath(transition.FromNodeId)} → {definition.GetPath(transition.ToNodeId)}.",
                $"{routeIdentity}|{transition.FromNodeId}|{transition.ToNodeId}|{configuration}|{Operations(transition.Operations)}|{Operations(transition.ReturnToVideoOperations ?? [])}",
                transition.ToNodeId,
                transition.Verified,
                transition.ConfigurationId,
                transition.Id);
        }

        AddCalculatedNavigationCheck(checks, display, definition);

        var sliders = definition.Nodes.Values
            .Where(node => node.ControlType == MenuControlType.Slider
                && !IsPermanentlyDisabled(definition, node))
            .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sliders.Length > 0)
        {
            var representativeCount = Math.Min(3, sliders.Length);
            Add(
                checks,
                display,
                "control:slider-behavior",
                MenuVerificationCheckKind.SliderBehavior,
                "Shared slider behavior",
                $"Verify {representativeCount} representative slider{(representativeCount == 1 ? string.Empty : "s")} move by the expected amount. This covers all {sliders.Length} sliders that use the shared left/right behavior.",
                $"{representativeCount}-distinct-sliders|left-right-increment|v2|{string.Join(";", sliders.Select(ControlShape))}",
                sliders[0].Id);
        }

        AddSharedControlChecks(checks, display, definition);
        AddConditionalChecks(checks, display, definition);

        return new MenuDefinitionVerificationPlan(display, checks);
    }

    public static bool IsCurrent(MenuDefinition definition, MenuDefinitionVerificationCheck check)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(check);
        return definition.Verification?.Checks.Any(record =>
            record.Id.Equals(check.Id, StringComparison.OrdinalIgnoreCase)
            && record.Fingerprint.Equals(check.Fingerprint, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static string FormatDisplay(MenuVerificationDisplay display) =>
        $"{display.Model} · firmware {display.Firmware}";

    private static void AddReturnScript(
        ICollection<MenuDefinitionVerificationCheck> checks,
        MenuVerificationDisplay display,
        MenuDefinition definition,
        MenuAnchor anchor,
        string configuration,
        string suffix,
        string label,
        string targetNodeId,
        MenuReturnScript script)
    {
        Add(
            checks,
            display,
            $"return:{configuration}:{anchor.Id}:{suffix}",
            MenuVerificationCheckKind.ReturnScript,
            $"Return to video · {label}",
            $"Verify the return-to-video script while starting from {definition.GetPath(targetNodeId)}.",
            $"{anchor.Id}|{configuration}|{suffix}|{targetNodeId}|{Operations(script.Operations)}",
            targetNodeId,
            script.Verified,
            anchor.ConfigurationId);
    }

    private static void AddSharedControlChecks(
        ICollection<MenuDefinitionVerificationCheck> checks,
        MenuVerificationDisplay display,
        MenuDefinition definition)
    {
        var supportedTypes = new[]
        {
            MenuControlType.Selection,
            MenuControlType.SubmenuSelection,
            MenuControlType.IndexedSelection,
            MenuControlType.Switch,
            MenuControlType.Confirmation
        };
        foreach (var controlType in supportedTypes)
        {
            var nodes = definition.Nodes.Values
                .Where(node => MenuControlBehaviorClassifier.GetEffectiveControlType(node)
                    == controlType
                    && !IsPermanentlyDisabled(definition, node)
                    && (controlType != MenuControlType.Confirmation
                        || IsSafelyVerifiableConfirmation(node)))
                .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (nodes.Length == 0)
            {
                continue;
            }

            var representative = SelectRepresentative(
                definition,
                nodes,
                preferSafeCancel: controlType == MenuControlType.Confirmation);
            var (id, kind, label, behavior) = controlType switch
            {
                MenuControlType.Selection => (
                    "control:selection-behavior",
                    MenuVerificationCheckKind.Selection,
                    "Shared selection behavior",
                    "open, choose, and option-order behavior"),
                MenuControlType.SubmenuSelection => (
                    "control:submenu-selection-behavior",
                    MenuVerificationCheckKind.Selection,
                    "Shared submenu-selection behavior",
                    "open, choose, and Return behavior"),
                MenuControlType.IndexedSelection => (
                    "control:indexed-selection-behavior",
                    MenuVerificationCheckKind.Selection,
                    "Shared indexed-selection behavior",
                    "fixed selector order and indexed value editing"),
                MenuControlType.Switch => (
                    "control:switch-behavior",
                    MenuVerificationCheckKind.Switch,
                    "Shared switch behavior",
                    "both switch states"),
                MenuControlType.Confirmation => (
                    "control:confirmation-behavior",
                    MenuVerificationCheckKind.Confirmation,
                    "Shared confirmation behavior",
                    "dialog choices and the safe cancel path"),
                _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, null)
            };
            Add(
                checks,
                display,
                id,
                kind,
                label,
                $"Verify the {behavior} using {definition.GetPath(representative.Id)} as the representative. This single check covers all {nodes.Length} {ControlTypeLabel(controlType)} control{(nodes.Length == 1 ? string.Empty : "s")}.",
                $"representative-control-coverage-v2|{controlType}|{string.Join(";", nodes.Select(ControlShape))}",
                representative.Id);
        }
    }

    private static void AddCalculatedNavigationCheck(
        ICollection<MenuDefinitionVerificationCheck> checks,
        MenuVerificationDisplay display,
        MenuDefinition definition)
    {
        if (SelectCalculatedNavigationRepresentative(definition) is not { } representative)
        {
            return;
        }

        var configuration = NormalizeConfiguration(representative.ConfigurationId);
        Add(
            checks,
            display,
            $"navigation:{configuration}:calculated-backtracking",
            MenuVerificationCheckKind.CalculatedNavigation,
            "Calculated cross-branch navigation",
            $"The test first prepares {DescribeVisualPosition(definition, representative.SourceNodeId)} It then navigates directly without returning to normal video. Expected finish: {DescribeVisualPosition(definition, representative.TargetNodeId)}",
            $"calculated-backtracking-v1|{representative.AnchorId}|{representative.SourceNodeId}|{representative.TargetNodeId}|{Operations(representative.Operations)}|{definition.Timing.DefaultDelayMilliseconds}|{definition.Timing.ScreenChangeDelayMilliseconds}|{definition.Timing.ReturnDelayMilliseconds}{AdjustmentTimingShape(representative.Operations, definition.Timing)}",
            representative.TargetNodeId,
            configurationId: representative.ConfigurationId,
            sourceNodeId: representative.SourceNodeId,
            preparationAnchorId: representative.AnchorId);
    }

    private static CalculatedNavigationRepresentative?
        SelectCalculatedNavigationRepresentative(MenuDefinition definition)
    {
        var submenuReturnDelay = TimeSpan.FromMilliseconds(Math.Max(
            definition.Timing.ReturnDelayMilliseconds,
            definition.Timing.ScreenChangeDelayMilliseconds));
        var routes = definition.ApplicableAnchors
            .OrderBy(anchor => anchor.Operations.Sum(operation => operation.Repeat))
            .ThenBy(anchor => anchor.Id, StringComparer.OrdinalIgnoreCase)
            .SelectMany(anchor => definition.ApplicableTransitions
                .Where(transition => transition.FromNodeId.Equals(
                        anchor.TargetNodeId,
                        StringComparison.OrdinalIgnoreCase)
                    && IsSafeCalculatedNavigationTarget(
                        definition,
                        definition.GetRequiredNode(transition.ToNodeId)))
                .Select(transition => new AbsoluteNavigationRoute(
                    anchor.Id,
                    definition.ActiveConfigurationId
                    ?? transition.ConfigurationId
                    ?? anchor.ConfigurationId,
                    transition.ToNodeId,
                    ExpandOperations(transition.Operations))))
            .GroupBy(
                route => $"{route.AnchorId}\u001f{route.TargetNodeId}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(route => route.Operations.Count)
                .First())
            .ToArray();
        if (routes.Length < 2)
        {
            return null;
        }

        foreach (var source in routes
                     .OrderByDescending(route => definition.GetDepth(route.TargetNodeId))
                     .ThenBy(route => route.TargetNodeId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var target in routes
                         .Where(target => target.AnchorId.Equals(
                                 source.AnchorId,
                                 StringComparison.OrdinalIgnoreCase)
                             && !target.TargetNodeId.Equals(
                                 source.TargetNodeId,
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(target => definition.GetDepth(target.TargetNodeId))
                         .ThenBy(target => target.TargetNodeId, StringComparer.OrdinalIgnoreCase))
            {
                var commonPressCount = 0;
                while (commonPressCount < source.Operations.Count
                       && commonPressCount < target.Operations.Count
                       && HasSameCommand(
                           source.Operations[commonPressCount],
                           target.Operations[commonPressCount]))
                {
                    commonPressCount++;
                }

                if (!MenuNavigator.TryCreateRelativeOperations(
                        source.Operations,
                        target.Operations,
                        submenuReturnDelay,
                        commonPressCount,
                        out var relativePresses))
                {
                    continue;
                }

                var operations = CollapseRepeats(relativePresses);
                if (!operations.Any(operation =>
                        operation.Key.Equals(
                            "KEY_RETURN",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return new CalculatedNavigationRepresentative(
                    source.AnchorId,
                    source.ConfigurationId,
                    source.TargetNodeId,
                    target.TargetNodeId,
                    operations);
            }
        }

        return null;
    }

    public static string DescribeVisualPosition(
        MenuDefinition definition,
        string nodeId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var node = definition.GetRequiredNode(nodeId);
        var path = definition.GetPath(node.Id);
        return node.ControlType == MenuControlType.Submenu
            ? $"the {path} submenu open, with its child list visible—not the {node.Label} row highlighted in its parent menu."
            : $"the {path} row highlighted; do not open or change it.";
    }

    private static IReadOnlyList<MenuOperation> ExpandOperations(
        IReadOnlyList<MenuOperation> operations) => operations
        .SelectMany(operation => Enumerable.Range(0, operation.Repeat)
            .Select(_ => operation with { Repeat = 1 }))
        .ToArray();

    private static bool HasSameCommand(MenuOperation left, MenuOperation right) =>
        left.Key.Equals(right.Key, StringComparison.OrdinalIgnoreCase)
        && left.Action == right.Action;

    private static IReadOnlyList<MenuOperation> CollapseRepeats(
        IReadOnlyList<MenuOperation> operations)
    {
        var collapsed = new List<MenuOperation>();
        foreach (var operation in operations)
        {
            if (collapsed.Count > 0
                && collapsed[^1].Key.Equals(
                    operation.Key,
                    StringComparison.OrdinalIgnoreCase)
                && collapsed[^1].Action == operation.Action
                && collapsed[^1].DelayAfter == operation.DelayAfter)
            {
                collapsed[^1] = collapsed[^1] with
                {
                    Repeat = collapsed[^1].Repeat + operation.Repeat
                };
                continue;
            }

            collapsed.Add(operation);
        }

        return collapsed;
    }

    private static bool IsSafeCalculatedNavigationTarget(
        MenuDefinition definition,
        MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Disabled
                || current.DisabledWhen is { Count: > 0 }
                || current.HiddenWhen is { Count: > 0 }
                || IsUnsafeConfirmationTarget(current))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddConditionalChecks(
        ICollection<MenuDefinitionVerificationCheck> checks,
        MenuVerificationDisplay display,
        MenuDefinition definition)
    {
        var groups = definition.Nodes.Values
            .SelectMany(node => CreateConditionalCoverageEntries(node))
            .GroupBy(entry => entry.BehaviorClass, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var entries = group.ToArray();
            var nodes = entries
                .Select(entry => entry.Node)
                .DistinctBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var representative = SelectRepresentative(definition, nodes);
            var external = group.Key.StartsWith("external-", StringComparison.OrdinalIgnoreCase);
            var representativeEntry = entries
                .Where(entry => !external || entry.Node.Id.Equals(
                    representative.Id,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.SourceId, StringComparer.OrdinalIgnoreCase)
                .First();
            var targetNodeId = group.Key.Equals(
                    "always-disabled",
                    StringComparison.OrdinalIgnoreCase)
                ? representative.ParentId ?? representative.Id
                : external
                    ? representative.ParentId ?? representative.Id
                    : representativeEntry.SourceId!;
            var controllerLabel = external
                ? definition.ExternalStates[representativeEntry.SourceId!].Label
                : definition.GetPath(targetNodeId);
            var viewPath = definition.GetPath(representative.ParentId ?? representative.Id);
            var affected = nodes.Length == 1
                ? definition.GetPath(representative.Id)
                : $"{nodes.Length} related rows, including {definition.GetPath(representative.Id)}";
            var distinctRuleCount = nodes
                .Select(node => group.Key.Equals("always-disabled", StringComparison.OrdinalIgnoreCase)
                    ? $"always:{node.Id}"
                    : ConditionPredicate(node))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var (label, behavior) = group.Key switch
            {
                "always-disabled" => ("Permanently disabled rows", "is present but permanently gray and cannot be selected"),
                "disabled" => ("Shared disabled-row behavior", "becomes disabled and remains visible"),
                "hidden" => ("Shared hidden-row behavior", "disappears and is removed from sibling offsets"),
                "external-disabled" => ("External-state disabled rows", "is disabled and remains visible"),
                "external-hidden" => ("External-state hidden rows", "disappears and is removed from sibling offsets"),
                _ => throw new InvalidOperationException($"Unknown conditional behavior class '{group.Key}'.")
            };
            var highlightExternalRow = group.Key.Equals("external-disabled", StringComparison.OrdinalIgnoreCase)
                && representative.ControlType != MenuControlType.Submenu;
            var inspectExternalHiddenRow = group.Key.Equals("external-hidden", StringComparison.OrdinalIgnoreCase);
            var externalPreparation = highlightExternalRow
                ? $"highlight {definition.GetPath(representative.Id)} without pressing Enter on that row or changing its value"
                : inspectExternalHiddenRow
                    ? $"highlight the next visible control after the missing {definition.GetPath(representative.Id)} (or the previous visible control if none follows), without activating it; if only submenus or no rows remain, inspect {viewPath}"
                    : $"open {viewPath}";
            var siblingCoverage = inspectExternalHiddenRow
                ? string.Join(";", definition.Nodes.Values
                    .Where(node => string.Equals(node.ParentId, representative.ParentId, StringComparison.OrdinalIgnoreCase))
                    .Select(node => $"{node.ControlType}:{ConditionShape(definition, node)}"))
                : string.Empty;
            var coverageVersion = inspectExternalHiddenRow
                ? $"representative-external-hidden-location-v5|{representative.Id}|{representative.ParentId}|{siblingCoverage}"
                : external
                ? $"representative-external-condition-coverage-v4|{representative.Id}"
                : "representative-condition-coverage-v3";
            Add(
                checks,
                display,
                $"condition:{group.Key}-behavior",
                MenuVerificationCheckKind.ConditionalVisibility,
                label,
                group.Key.Equals("always-disabled", StringComparison.OrdinalIgnoreCase)
                    ? $"Open {viewPath} and verify the representative {behavior}. This covers {affected}."
                    : external
                        ? $"Set the external equipment and the app's {controllerLabel} selector to {representativeEntry.EqualsValue}, then {externalPreparation} and verify the representative {behavior}. This covers {affected} across {distinctRuleCount} declared conditional rule{(distinctRuleCount == 1 ? string.Empty : "s")}."
                        : $"Change {controllerLabel} and verify the representative {behavior}. This covers {affected} across {distinctRuleCount} declared conditional rule{(distinctRuleCount == 1 ? string.Empty : "s")}.",
                $"{coverageVersion}|{group.Key}|{string.Join(";", nodes.Select(node => ConditionShape(definition, node)))}",
                highlightExternalRow ? representative.Id : targetNodeId,
                externalStateId: external ? representativeEntry.SourceId : null,
                externalStateValue: external ? representativeEntry.EqualsValue : null,
                representativeNodeId: external ? representative.Id : null);
        }
    }

    private static MenuNode SelectRepresentative(
        MenuDefinition definition,
        IEnumerable<MenuNode> nodes,
        bool preferSafeCancel = false) => nodes
        .OrderByDescending(node => preferSafeCancel
            && (node.SelectionOptions ?? []).Contains(
                "Cancel",
                StringComparer.OrdinalIgnoreCase))
        .ThenByDescending(node => definition.ApplicableTransitions.Any(transition =>
            transition.Verified
            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
        .ThenBy(node => (node.DisabledWhen?.Count ?? 0) + (node.HiddenWhen?.Count ?? 0))
        .ThenBy(node => definition.GetDepth(node.Id))
        .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
        .First();

    private static bool IsPermanentlyDisabled(
        MenuDefinition definition,
        MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Disabled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsSafelyVerifiableConfirmation(MenuNode node) =>
        (node.SelectionOptions ?? []).Contains(
            "Cancel",
            StringComparer.OrdinalIgnoreCase)
        && !node.Id.Contains("reset", StringComparison.OrdinalIgnoreCase)
        && !node.Label.Contains("reset", StringComparison.OrdinalIgnoreCase)
        && !(node.SelectionOptions ?? []).Any(option =>
            option.Contains("reset", StringComparison.OrdinalIgnoreCase));

    private static bool IsUnsafeConfirmationTarget(MenuNode node) =>
        MenuControlBehaviorClassifier.GetEffectiveControlType(node)
            == MenuControlType.Confirmation
        && !IsSafelyVerifiableConfirmation(node);

    private static string ControlTypeLabel(MenuControlType controlType) => controlType switch
    {
        MenuControlType.Selection => "selection",
        MenuControlType.SubmenuSelection => "submenu-selection",
        MenuControlType.IndexedSelection => "indexed-selection",
        MenuControlType.Switch => "switch",
        MenuControlType.Confirmation => "confirmation",
        _ => controlType.ToString().ToLowerInvariant()
    };

    private static void Add(
        ICollection<MenuDefinitionVerificationCheck> checks,
        MenuVerificationDisplay display,
        string id,
        MenuVerificationCheckKind kind,
        string label,
        string description,
        string content,
        string? targetNodeId = null,
        bool existingEvidenceReady = false,
        string? configurationId = null,
        string? sourceItemId = null,
        string? sourceNodeId = null,
        string? preparationAnchorId = null,
        string? externalStateId = null,
        string? externalStateValue = null,
        string? representativeNodeId = null)
    {
        var fingerprint = Fingerprint($"{FingerprintVersion}|{CanonicalDisplay(display)}|{id}|{content}");
        checks.Add(new MenuDefinitionVerificationCheck(
            id,
            kind,
            label,
            description,
            fingerprint,
            targetNodeId,
            existingEvidenceReady,
            configurationId,
            sourceItemId,
            sourceNodeId,
            preparationAnchorId,
            externalStateId,
            externalStateValue,
            representativeNodeId));
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string CanonicalDisplay(MenuVerificationDisplay display) =>
        $"{display.Model}|{display.Firmware}";

    private static string ControlShape(MenuNode node) => string.Join(
        "|",
        node.Id,
        node.ControlType,
        node.DefaultValue ?? string.Empty,
        node.MinimumValue?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
        node.MaximumValue?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
        string.Join("\u001e", node.SelectionOptions ?? []))
        + (node.DefaultValueWhen is { Count: > 0 }
            ? "|conditional-defaults:" + string.Join("\u001d", node.DefaultValueWhen.Select(rule =>
                rule.Value + "=" + string.Join("\u001e", rule.When
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}:{pair.Value}"))))
            : string.Empty);

    private static string ConditionShape(MenuDefinition definition, MenuNode node) => string.Join(
        "|",
        node.Id,
        node.Disabled,
        string.Join("\u001e", (node.DisabledWhen ?? []).Select(ConditionShape)),
        string.Join("\u001e", (node.HiddenWhen ?? []).Select(ConditionShape)),
        string.Join(
            "\u001e",
            (node.DisabledWhen ?? []).Concat((node.HiddenWhen ?? []).Select(condition =>
                    new MenuNodeDisabledCondition(
                        condition.SourceId,
                        condition.EqualsValue,
                        condition.SourceKind)))
                .Select(condition => condition.SourceKind == MenuConditionSourceKind.ExternalState
                    ? ExternalStateShape(definition.ExternalStates[condition.SourceId])
                    : ControlShape(definition.GetRequiredNode(condition.SourceId)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(shape => shape, StringComparer.OrdinalIgnoreCase)));

    private static string ConditionShape(MenuNodeDisabledCondition condition) =>
        $"{condition.SourceKind}:{condition.SourceId}={condition.EqualsValue}";

    private static string ConditionShape(MenuNodeHiddenCondition condition) =>
        $"{condition.SourceKind}:{condition.SourceId}={condition.EqualsValue}";

    private static string ExternalStateShape(MenuExternalState state) => string.Join(
        "|",
        state.Id,
        state.Label,
        state.DefaultValue,
        string.Join("\u001e", state.Options));

    private static string ConditionPredicate(MenuNode node) => string.Join(
        "|",
        $"disabled:{string.Join("&", (node.DisabledWhen ?? [])
            .Select(ConditionShape)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}",
        $"hidden:{string.Join("&", (node.HiddenWhen ?? [])
            .Select(ConditionShape)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}");

    private static IEnumerable<ConditionalCoverageEntry> CreateConditionalCoverageEntries(
        MenuNode node)
    {
        if (node.Disabled)
        {
            yield return new ConditionalCoverageEntry(node, "always-disabled");
        }

        foreach (var condition in node.DisabledWhen ?? [])
        {
            yield return new ConditionalCoverageEntry(
                node,
                condition.SourceKind == MenuConditionSourceKind.ExternalState
                    ? "external-disabled"
                    : "disabled",
                condition.SourceId,
                condition.SourceKind,
                condition.EqualsValue);
        }

        foreach (var condition in node.HiddenWhen ?? [])
        {
            yield return new ConditionalCoverageEntry(
                node,
                condition.SourceKind == MenuConditionSourceKind.ExternalState
                    ? "external-hidden"
                    : "hidden",
                condition.SourceId,
                condition.SourceKind,
                condition.EqualsValue);
        }
    }

    private static string Operations(IEnumerable<MenuOperation> operations) => string.Join(
        ";",
        operations.Select(operation => string.Join(
            ",",
            operation.Key,
            operation.Action,
            operation.Repeat.ToString(CultureInfo.InvariantCulture),
            operation.DelayAfter?.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) ?? "system")));

    private static string AdjustmentTimingShape(
        IEnumerable<MenuOperation> operations,
        MenuTimingProfile timing) => operations.Any(operation =>
            operation.Key.Equals("KEY_LEFT", StringComparison.OrdinalIgnoreCase)
            || operation.Key.Equals("KEY_RIGHT", StringComparison.OrdinalIgnoreCase))
        ? $"|adjustment:{timing.AdjustmentDelayMilliseconds}"
        : string.Empty;

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed record ConditionalCoverageEntry(
        MenuNode Node,
        string BehaviorClass,
        string? SourceId = null,
        MenuConditionSourceKind SourceKind = MenuConditionSourceKind.MenuSetting,
        string? EqualsValue = null);

    private sealed record CalculatedNavigationRepresentative(
        string AnchorId,
        string? ConfigurationId,
        string SourceNodeId,
        string TargetNodeId,
        IReadOnlyList<MenuOperation> Operations);

    private sealed record AbsoluteNavigationRoute(
        string AnchorId,
        string? ConfigurationId,
        string TargetNodeId,
        IReadOnlyList<MenuOperation> Operations);
}
