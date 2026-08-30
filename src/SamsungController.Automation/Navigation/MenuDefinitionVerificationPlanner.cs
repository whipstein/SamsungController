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
    string? SourceItemId = null);

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
            definition.Context.Firmware,
            definition.Context.Signal,
            definition.Context.PictureMode,
            definition.Context.Input);
        var checks = new List<MenuDefinitionVerificationCheck>();

        Add(
            checks,
            display,
            "display",
            MenuVerificationCheckKind.Display,
            "Display combination",
            $"Confirm {FormatDisplay(display)} is the display and viewing context being tested.",
            CanonicalDisplay(display));
        Add(
            checks,
            display,
            "timing",
            MenuVerificationCheckKind.Timing,
            "System-wide command timing",
            "Verify the default, screen-change, and return waits against the display.",
            $"{definition.Timing.DefaultDelayMilliseconds}|{definition.Timing.ScreenChangeDelayMilliseconds}|{definition.Timing.ReturnDelayMilliseconds}",
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
        $"{display.Model} · firmware {display.Firmware} · {display.Signal} · {display.PictureMode} · {display.Input}";

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
                    && !IsPermanentlyDisabled(definition, node))
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
            var nodes = group
                .Select(entry => entry.Node)
                .DistinctBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var representative = SelectRepresentative(definition, nodes);
            var controllerNodeId = group.Key.Equals(
                    "always-disabled",
                    StringComparison.OrdinalIgnoreCase)
                ? representative.ParentId ?? representative.Id
                : nodes
                    .SelectMany(node => group.Key.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                        ? (node.DisabledWhen ?? []).Select(condition => condition.SettingNodeId)
                        : (node.HiddenWhen ?? []).Select(condition => condition.SettingNodeId))
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .First();
            var controller = definition.GetRequiredNode(controllerNodeId);
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
                _ => throw new InvalidOperationException($"Unknown conditional behavior class '{group.Key}'.")
            };
            Add(
                checks,
                display,
                $"condition:{group.Key}-behavior",
                MenuVerificationCheckKind.ConditionalVisibility,
                label,
                group.Key.Equals("always-disabled", StringComparison.OrdinalIgnoreCase)
                    ? $"Open {definition.GetPath(controller.Id)} and verify the representative {behavior}. This covers {affected}."
                    : $"Change {definition.GetPath(controller.Id)} and verify the representative {behavior}. This covers {affected} across {distinctRuleCount} declared conditional rule{(distinctRuleCount == 1 ? string.Empty : "s")}.",
                $"representative-condition-coverage-v3|{group.Key}|{string.Join(";", nodes.Select(node => ConditionShape(definition, node)))}",
                controller.Id);
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
        string? sourceItemId = null)
    {
        var fingerprint = Fingerprint($"{FingerprintVersion}|{CanonicalDisplay(display)}|{id}|{content}");
        checks.Add(new MenuDefinitionVerificationCheck(id, kind, label, description, fingerprint, targetNodeId, existingEvidenceReady, configurationId, sourceItemId));
    }

    private static string NormalizeConfiguration(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private static string CanonicalDisplay(MenuVerificationDisplay display) =>
        $"{display.Model}|{display.Firmware}|{display.Signal}|{display.PictureMode}|{display.Input}";

    private static string ControlShape(MenuNode node) => string.Join(
        "|",
        node.Id,
        node.ControlType,
        node.DefaultValue ?? string.Empty,
        node.MinimumValue?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
        node.MaximumValue?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
        string.Join("\u001e", node.SelectionOptions ?? []));

    private static string ConditionShape(MenuDefinition definition, MenuNode node) => string.Join(
        "|",
        node.Id,
        node.Disabled,
        string.Join("\u001e", (node.DisabledWhen ?? []).Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")),
        string.Join("\u001e", (node.HiddenWhen ?? []).Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")),
        string.Join(
            "\u001e",
            (node.DisabledWhen ?? []).Concat((node.HiddenWhen ?? []).Select(condition =>
                    new MenuNodeDisabledCondition(condition.SettingNodeId, condition.EqualsValue)))
                .Select(condition => definition.GetRequiredNode(condition.SettingNodeId))
                .DistinctBy(setting => setting.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(setting => setting.Id, StringComparer.OrdinalIgnoreCase)
                .Select(ControlShape)));

    private static string ConditionPredicate(MenuNode node) => string.Join(
        "|",
        $"disabled:{string.Join("&", (node.DisabledWhen ?? [])
            .Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}",
        $"hidden:{string.Join("&", (node.HiddenWhen ?? [])
            .Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}");

    private static IEnumerable<ConditionalCoverageEntry> CreateConditionalCoverageEntries(
        MenuNode node)
    {
        if (node.Disabled)
        {
            yield return new ConditionalCoverageEntry(node, "always-disabled");
        }
        else if (node.DisabledWhen is { Count: > 0 })
        {
            yield return new ConditionalCoverageEntry(node, "disabled");
        }

        if (node.HiddenWhen is { Count: > 0 })
        {
            yield return new ConditionalCoverageEntry(node, "hidden");
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

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private sealed record ConditionalCoverageEntry(
        MenuNode Node,
        string BehaviorClass);
}
