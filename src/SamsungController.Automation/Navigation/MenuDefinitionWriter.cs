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
        yaml.AppendLine();
        yaml.AppendLine("timing:");
        AppendDuration(yaml, 2, "defaultDelay", definition.Timing.DefaultDelayMilliseconds);
        AppendDuration(yaml, 2, "screenChangeDelay", definition.Timing.ScreenChangeDelayMilliseconds);
        AppendDuration(yaml, 2, "returnDelay", definition.Timing.ReturnDelayMilliseconds);
        yaml.AppendLine();
        yaml.AppendLine("nodes:");
        foreach (var node in definition.Nodes.Values)
        {
            AppendListScalar(yaml, 2, "id", node.Id);
            AppendScalar(yaml, 4, "label", node.Label);
            AppendOptionalScalar(yaml, 4, "parent", node.ParentId);
            AppendOptionalScalar(yaml, 4, "description", node.Description);
        }

        yaml.AppendLine();
        yaml.AppendLine(definition.Anchors.Count == 0 ? "anchors: []" : "anchors:");
        foreach (var anchor in definition.Anchors.Values)
        {
            AppendListScalar(yaml, 2, "id", anchor.Id);
            AppendScalar(yaml, 4, "label", anchor.Label);
            AppendScalar(yaml, 4, "target", anchor.TargetNodeId);
            AppendBoolean(yaml, 4, "verified", anchor.Verified);
            AppendOptionalScalar(yaml, 4, "description", anchor.Description);
            AppendOperations(yaml, anchor.Operations);
        }

        yaml.AppendLine();
        yaml.AppendLine(definition.Transitions.Count == 0 ? "transitions: []" : "transitions:");
        foreach (var transition in definition.Transitions.Values)
        {
            AppendListScalar(yaml, 2, "id", transition.Id);
            AppendScalar(yaml, 4, "from", transition.FromNodeId);
            AppendScalar(yaml, 4, "to", transition.ToNodeId);
            AppendBoolean(yaml, 4, "verified", transition.Verified);
            AppendOptionalScalar(yaml, 4, "description", transition.Description);
            AppendOperations(yaml, transition.Operations);
        }

        return yaml.ToString();
    }

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
        IReadOnlyList<MenuOperation> operations)
    {
        yaml.AppendLine("    steps:");
        foreach (var operation in operations)
        {
            AppendListScalar(yaml, 6, "key", operation.Key);
            if (operation.Action != Core.Protocol.RemoteKeyAction.Click)
            {
                AppendScalar(yaml, 8, "action", operation.Action.ToString());
            }

            if (operation.Repeat != 1)
            {
                AppendInteger(yaml, 8, "repeat", operation.Repeat);
            }

            if (operation.DelayAfter is { } delay)
            {
                var milliseconds = delay.TotalMilliseconds.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
                yaml.Append(' ', 8).Append("delay: ").Append(milliseconds).AppendLine("ms");
            }
        }
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
