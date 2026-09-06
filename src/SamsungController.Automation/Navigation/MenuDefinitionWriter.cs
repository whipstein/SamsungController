using System.Globalization;
using System.Text;

namespace SamsungController.Automation.Navigation;

public sealed class MenuDefinitionWriter
{
    public string Serialize(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        new MenuDefinitionValidator().ValidateAndThrow(definition);

        var yaml = new StringBuilder();
        yaml.AppendLine("version: 1");
        AppendScalar(yaml, 0, "id", definition.Id);
        AppendScalar(yaml, 0, "name", definition.Name);
        AppendScalar(yaml, 0, "model", definition.Model);
        yaml.AppendLine();
        yaml.AppendLine("context:");
        AppendScalar(yaml, 2, "firmware", definition.Context.Firmware);
        if (definition.Configurations.Count > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("configurations:");
            foreach (var configuration in definition.Configurations.Values)
            {
                AppendListScalar(yaml, 2, "id", configuration.Id);
                AppendScalar(yaml, 4, "name", configuration.Name);
                AppendOptionalScalar(yaml, 4, "conditions", configuration.Conditions);
            }
        }

        if (definition.ExternalStates.Count > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("externalStates:");
            foreach (var state in definition.ExternalStates.Values)
            {
                AppendListScalar(yaml, 2, "id", state.Id);
                AppendScalar(yaml, 4, "label", state.Label);
                AppendScalar(yaml, 4, "defaultValue", state.DefaultValue);
                yaml.AppendLine("    options:");
                foreach (var option in state.Options)
                {
                    yaml.Append("      - ").AppendLine(Quote(option));
                }
            }
        }

        yaml.AppendLine();
        yaml.AppendLine("timing:");
        AppendDuration(yaml, 2, "defaultDelay", definition.Timing.DefaultDelayMilliseconds);
        AppendDuration(yaml, 2, "screenChangeDelay", definition.Timing.ScreenChangeDelayMilliseconds);
        AppendDuration(yaml, 2, "returnDelay", definition.Timing.ReturnDelayMilliseconds);
        AppendDuration(yaml, 2, "adjustmentDelay", definition.Timing.AdjustmentDelayMilliseconds);
        yaml.AppendLine();
        yaml.AppendLine("nodes:");
        var orderedNodes = definition.Nodes.Values.ToArray();
        var childrenByParent = orderedNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.ParentId))
            .GroupBy(node => node.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in orderedNodes.Where(node => string.IsNullOrWhiteSpace(node.ParentId)))
        {
            AppendNode(yaml, node, childrenByParent, 2);
        }

        yaml.AppendLine();
        yaml.AppendLine(definition.Anchors.Count == 0 ? "anchors: []" : "anchors:");
        foreach (var anchor in definition.Anchors.Values)
        {
            AppendListScalar(yaml, 2, "id", anchor.Id);
            AppendScalar(yaml, 4, "label", anchor.Label);
            AppendScalar(yaml, 4, "target", anchor.TargetNodeId);
            AppendOptionalScalar(yaml, 4, "configuration", anchor.ConfigurationId);
            AppendOptionalScalar(yaml, 4, "description", anchor.Description);
            AppendOptionalScalar(yaml, 4, "validationSource", anchor.ValidationSourceNodeId);
            AppendReturnStrategy(yaml, anchor.ReturnStrategy);
            AppendOperations(yaml, anchor.Operations);
        }

        yaml.AppendLine();
        yaml.AppendLine(definition.Transitions.Count == 0 ? "transitions: []" : "transitions:");
        foreach (var transition in definition.Transitions.Values)
        {
            AppendListScalar(yaml, 2, "id", transition.Id);
            AppendScalar(yaml, 4, "from", transition.FromNodeId);
            AppendScalar(yaml, 4, "to", transition.ToNodeId);
            AppendOptionalScalar(yaml, 4, "configuration", transition.ConfigurationId);
            AppendOptionalScalar(yaml, 4, "description", transition.Description);
            if (transition.GeneratedFromTopology)
            {
                AppendBoolean(yaml, 4, "generatedFromTopology", true);
                AppendOptionalScalar(yaml, 4, "topologySeed", transition.TopologySeedTransitionId);
                AppendOptionalScalar(yaml, 4, "validationGroup", transition.ValidationGroupId);
                AppendBoolean(yaml, 4, "validationRoute", transition.IsValidationRoute);
            }
            if (transition.ReturnToVideoOperations is { Count: > 0 } returnOperations)
            {
                AppendOperations(yaml, returnOperations, name: "returnSteps");
            }

            AppendOperations(yaml, transition.Operations);
        }

        return yaml.ToString();
    }

    public string Serialize(
        MenuDefinition definition,
        MenuDefinitionFileFormat format) => format switch
        {
            MenuDefinitionFileFormat.Yaml => Serialize(definition),
            MenuDefinitionFileFormat.Json => new MenuDefinitionJsonSerializer().Serialize(definition),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

    private static string FormatControlType(MenuControlType controlType) => controlType switch
    {
        MenuControlType.Submenu => "submenu",
        MenuControlType.Slider => "slider",
        MenuControlType.Selection => "selection",
        MenuControlType.SubmenuSelection => "submenu-selection",
        MenuControlType.IndexedSelection => "indexed-selection",
        MenuControlType.Switch => "switch",
        MenuControlType.Confirmation => "confirmation",
        MenuControlType.Action => "action",
        _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, null)
    };

    private static void AppendNode(
        StringBuilder yaml,
        MenuNode node,
        IReadOnlyDictionary<string, MenuNode[]> childrenByParent,
        int indentation)
    {
        var fieldIndentation = indentation + 2;
        AppendListScalar(yaml, indentation, "id", node.Id);
        AppendScalar(yaml, fieldIndentation, "label", node.Label);
        AppendOptionalScalar(yaml, fieldIndentation, "description", node.Description);
        AppendScalar(yaml, fieldIndentation, "controlType", FormatControlType(node.ControlType));
        AppendOptionalScalar(yaml, fieldIndentation, "defaultValue", node.DefaultValue);
        if (node.DefaultValueWhen is { Count: > 0 })
        {
            yaml.Append(' ', fieldIndentation).AppendLine("defaultValueWhen:");
            foreach (var rule in node.DefaultValueWhen)
            {
                AppendListScalar(yaml, fieldIndentation + 2, "value", rule.Value);
                yaml.Append(' ', fieldIndentation + 4).AppendLine("when:");
                foreach (var condition in rule.When)
                {
                    AppendScalar(yaml, fieldIndentation + 6, condition.Key, condition.Value);
                }
            }
        }
        AppendOptionalDecimal(yaml, fieldIndentation, "minimumValue", node.MinimumValue);
        AppendOptionalDecimal(yaml, fieldIndentation, "maximumValue", node.MaximumValue);
        if (node.Disabled)
        {
            AppendBoolean(yaml, fieldIndentation, "disabled", true);
        }
        if (node.SelectionOptions is { Count: > 0 })
        {
            yaml.Append(' ', fieldIndentation).AppendLine("options:");
            foreach (var option in node.SelectionOptions)
            {
                yaml.Append(' ', fieldIndentation + 2).Append("- ").AppendLine(Quote(option));
            }
        }

        if (node.DisabledWhen is { Count: > 0 })
        {
            yaml.Append(' ', fieldIndentation).AppendLine("disabledWhen:");
            foreach (var condition in node.DisabledWhen)
            {
                AppendListScalar(
                    yaml,
                    fieldIndentation + 2,
                    condition.SourceKind == MenuConditionSourceKind.ExternalState
                        ? "externalState"
                        : "setting",
                    condition.SourceId);
                AppendScalar(yaml, fieldIndentation + 4, "equals", condition.EqualsValue);
            }
        }

        if (node.HiddenWhen is { Count: > 0 })
        {
            yaml.Append(' ', fieldIndentation).AppendLine("hiddenWhen:");
            foreach (var condition in node.HiddenWhen)
            {
                AppendListScalar(
                    yaml,
                    fieldIndentation + 2,
                    condition.SourceKind == MenuConditionSourceKind.ExternalState
                        ? "externalState"
                        : "setting",
                    condition.SourceId);
                AppendScalar(yaml, fieldIndentation + 4, "equals", condition.EqualsValue);
            }
        }

        if (childrenByParent.TryGetValue(node.Id, out var children))
        {
            yaml.Append(' ', fieldIndentation).AppendLine("children:");
            foreach (var child in children)
            {
                AppendNode(yaml, child, childrenByParent, fieldIndentation + 2);
            }
        }
    }

    private static void AppendOptionalDecimal(
        StringBuilder yaml,
        int indentation,
        string name,
        decimal? value)
    {
        if (value is { } number)
        {
            AppendScalar(yaml, indentation, name, number.ToString("G29", CultureInfo.InvariantCulture));
        }
    }

    public async Task WriteFileAsync(
        string path,
        MenuDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var existingContent = File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)
            : null;
        var format = MenuDefinitionFileFormats.DetectForWrite(fullPath, existingContent);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    Serialize(definition, format),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AppendOperations(
        StringBuilder yaml,
        IReadOnlyList<MenuOperation> operations,
        int indentation = 4,
        string name = "steps")
    {
        yaml.Append(' ', indentation).Append(name).AppendLine(":");
        foreach (var operation in operations)
        {
            AppendListScalar(yaml, indentation + 2, "key", operation.Key);
            if (operation.Action != Core.Protocol.RemoteKeyAction.Click)
            {
                AppendScalar(yaml, indentation + 4, "action", operation.Action.ToString());
            }

            if (operation.Repeat != 1)
            {
                AppendInteger(yaml, indentation + 4, "repeat", operation.Repeat);
            }

            if (operation.DelayAfter is { } delay)
            {
                var milliseconds = delay.TotalMilliseconds.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
                yaml.Append(' ', indentation + 4).Append("delay: ").Append(milliseconds).AppendLine("ms");
            }
        }
    }

    private static void AppendReturnStrategy(
        StringBuilder yaml,
        MenuReturnStrategy? strategy)
    {
        if (strategy is null)
        {
            return;
        }

        yaml.AppendLine("    returnStrategy:");
        AppendScalar(yaml, 6, "menuRoot", strategy.MenuRootNodeId);
        AppendReturnScript(yaml, 6, "atMenuRoot", strategy.AtMenuRoot);
        AppendReturnScript(yaml, 6, "belowMenuRoot", strategy.BelowMenuRoot);
        if (strategy.NodeOverrides is { Count: > 0 })
        {
            yaml.AppendLine("      overrides:");
            foreach (var item in strategy.NodeOverrides)
            {
                AppendListScalar(yaml, 8, "node", item.NodeId);
                AppendOperations(yaml, item.Script.Operations, 10);
            }
        }
    }

    private static void AppendReturnScript(
        StringBuilder yaml,
        int indentation,
        string name,
        MenuReturnScript script)
    {
        yaml.Append(' ', indentation).Append(name).AppendLine(":");
        AppendOperations(yaml, script.Operations, indentation + 2);
    }

    private static void AppendListScalar(
        StringBuilder yaml,
        int indentation,
        string name,
        string value) =>
        yaml.Append(' ', indentation)
            .Append("- ")
            .Append(name)
            .Append(": ")
            .AppendLine(Quote(value));

    private static void AppendScalar(
        StringBuilder yaml,
        int indentation,
        string name,
        string value) =>
        yaml.Append(' ', indentation)
            .Append(name)
            .Append(": ")
            .AppendLine(Quote(value));

    private static void AppendOptionalScalar(
        StringBuilder yaml,
        int indentation,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AppendScalar(yaml, indentation, name, value);
        }
    }

    private static void AppendBoolean(
        StringBuilder yaml,
        int indentation,
        string name,
        bool value) =>
        yaml.Append(' ', indentation)
            .Append(name)
            .Append(": ")
            .AppendLine(value ? "true" : "false");

    private static void AppendInteger(
        StringBuilder yaml,
        int indentation,
        string name,
        int value) =>
        yaml.Append(' ', indentation)
            .Append(name)
            .Append(": ")
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));

    private static void AppendDuration(
        StringBuilder yaml,
        int indentation,
        string name,
        int milliseconds) =>
        yaml.Append(' ', indentation)
            .Append(name)
            .Append(": ")
            .Append(milliseconds.ToString(CultureInfo.InvariantCulture))
            .AppendLine("ms");

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
