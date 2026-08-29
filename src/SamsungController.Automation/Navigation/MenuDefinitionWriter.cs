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
        AppendScalar(yaml, 2, "signal", definition.Context.Signal);
        AppendScalar(yaml, 2, "pictureMode", definition.Context.PictureMode);
        AppendScalar(yaml, 2, "input", definition.Context.Input);
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

        yaml.AppendLine();
        yaml.AppendLine("timing:");
        AppendDuration(yaml, 2, "defaultDelay", definition.Timing.DefaultDelayMilliseconds);
        AppendDuration(yaml, 2, "screenChangeDelay", definition.Timing.ScreenChangeDelayMilliseconds);
        AppendDuration(yaml, 2, "returnDelay", definition.Timing.ReturnDelayMilliseconds);
        AppendBoolean(yaml, 2, "verified", definition.Timing.Verified);
        yaml.AppendLine();
        yaml.AppendLine("nodes:");
        foreach (var node in definition.Nodes.Values)
        {
            AppendListScalar(yaml, 2, "id", node.Id);
            AppendScalar(yaml, 4, "label", node.Label);
            AppendOptionalScalar(yaml, 4, "parent", node.ParentId);
            AppendOptionalScalar(yaml, 4, "description", node.Description);
            AppendScalar(yaml, 4, "controlType", FormatControlType(node.ControlType));
            AppendOptionalScalar(yaml, 4, "defaultValue", node.DefaultValue);
            if (node.DisabledWhen is { Count: > 0 })
            {
                yaml.AppendLine("    disabledWhen:");
                foreach (var condition in node.DisabledWhen)
                {
                    AppendListScalar(yaml, 6, "setting", condition.SettingNodeId);
                    AppendScalar(yaml, 8, "equals", condition.EqualsValue);
                }
            }
        }

        yaml.AppendLine();
        yaml.AppendLine(definition.Anchors.Count == 0 ? "anchors: []" : "anchors:");
        foreach (var anchor in definition.Anchors.Values)
        {
            AppendListScalar(yaml, 2, "id", anchor.Id);
            AppendScalar(yaml, 4, "label", anchor.Label);
            AppendScalar(yaml, 4, "target", anchor.TargetNodeId);
            AppendOptionalScalar(yaml, 4, "configuration", anchor.ConfigurationId);
            AppendBoolean(yaml, 4, "verified", anchor.Verified);
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
            AppendBoolean(yaml, 4, "verified", transition.Verified);
            AppendOptionalScalar(yaml, 4, "description", transition.Description);
            if (transition.ReturnToVideoOperations is { Count: > 0 } returnOperations)
            {
                AppendOperations(yaml, returnOperations, name: "returnSteps");
            }

            AppendOperations(yaml, transition.Operations);
        }

        return yaml.ToString();
    }

    private static string FormatControlType(MenuControlType controlType) => controlType switch
    {
        MenuControlType.Submenu => "submenu",
        MenuControlType.Slider => "slider",
        MenuControlType.Selection => "selection",
        MenuControlType.Switch => "switch",
        _ => throw new ArgumentOutOfRangeException(nameof(controlType), controlType, null)
    };

    public async Task WriteFileAsync(
        string path,
        MenuDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    Serialize(definition),
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
                AppendBoolean(yaml, 10, "verified", item.Script.Verified);
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
        AppendBoolean(yaml, indentation + 2, "verified", script.Verified);
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
