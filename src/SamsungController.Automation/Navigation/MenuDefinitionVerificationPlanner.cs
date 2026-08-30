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
                     .Where(item => !item.GeneratedFromTopology || item.IsValidationRoute)
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
            .Where(node => node.ControlType == MenuControlType.Slider)
            .OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sliders.Length > 0)
        {
            Add(
                checks,
                display,
                "control:slider-behavior",
                MenuVerificationCheckKind.SliderBehavior,
                "Shared slider behavior",
                "Verify three representative sliders move by the expected amount and stay synchronized.",
                $"three-distinct-sliders|left-right-increment|v1|{string.Join(";", sliders.Select(ControlShape))}",
                sliders[0].Id);
        }

        foreach (var node in definition.Nodes.Values.OrderBy(node => node.Id, StringComparer.OrdinalIgnoreCase))
        {
            var controlShape = ControlShape(node);
            switch (node.ControlType)
            {
                case MenuControlType.Selection:
                case MenuControlType.SubmenuSelection:
                case MenuControlType.IndexedSelection:
                    Add(checks, display, $"control:selection:{node.Id}", MenuVerificationCheckKind.Selection, $"Selection · {node.Label}", $"Verify every declared option and the exit behavior for {definition.GetPath(node.Id)}.", controlShape, node.Id);
                    break;
                case MenuControlType.Switch:
                    Add(checks, display, $"control:switch:{node.Id}", MenuVerificationCheckKind.Switch, $"Switch · {node.Label}", $"Verify both switch states for {definition.GetPath(node.Id)}.", controlShape, node.Id);
                    break;
                case MenuControlType.Confirmation:
                    Add(checks, display, $"control:confirmation:{node.Id}", MenuVerificationCheckKind.Confirmation, $"Confirmation · {node.Label}", $"Verify the declared choices and safe cancel path for {definition.GetPath(node.Id)}.", controlShape, node.Id);
                    break;
            }

            if (node.DisabledWhen is { Count: > 0 } || node.HiddenWhen is { Count: > 0 })
            {
                Add(checks, display, $"condition:{node.Id}", MenuVerificationCheckKind.ConditionalVisibility, $"Conditions · {node.Label}", $"Verify when {definition.GetPath(node.Id)} is enabled, disabled, visible, or absent.", ConditionShape(definition, node), node.Id);
            }
        }

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
        string.Join("\u001e", (node.DisabledWhen ?? []).Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")),
        string.Join("\u001e", (node.HiddenWhen ?? []).Select(condition => $"{condition.SettingNodeId}={condition.EqualsValue}")),
        string.Join(
            "\u001e",
            (node.DisabledWhen ?? []).Concat((node.HiddenWhen ?? []).Select(condition =>
                    new MenuNodeDisabledCondition(condition.SettingNodeId, condition.EqualsValue)))
                .Select(condition => definition.GetRequiredNode(condition.SettingNodeId))
                .DistinctBy(setting => setting.Id, StringComparer.OrdinalIgnoreCase)
                .Select(ControlShape)));

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
}
