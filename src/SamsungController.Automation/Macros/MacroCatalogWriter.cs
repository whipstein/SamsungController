using System.Globalization;
using System.Text;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Macros;

public sealed class MacroCatalogWriter
{
    public string Serialize(MacroCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        new MacroValidator().ValidateAndThrow(catalog);

        var yaml = new StringBuilder();
        yaml.AppendLine("version: 1");
        if (catalog.Variables.Count > 0)
        {
            yaml.AppendLine();
            yaml.AppendLine("variables:");
            foreach (var variable in catalog.Variables.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendScalar(yaml, 2, variable.Key, variable.Value);
            }
        }

        yaml.AppendLine();
        yaml.AppendLine("macros:");
        foreach (var macro in catalog.Macros.Values)
        {
            yaml.Append(' ', 2).Append(Quote(macro.Name)).AppendLine(":");
            if (!string.IsNullOrWhiteSpace(macro.Description))
            {
                AppendScalar(yaml, 4, "description", macro.Description);
            }

            AppendBoolean(yaml, 4, "verified", macro.Verified);
            AppendInteger(yaml, 4, "verificationPasses", macro.VerificationPasses);
            yaml.AppendLine("    steps:");
            foreach (var step in macro.Steps)
            {
                switch (step)
                {
                    case KeyStep key:
                        AppendListScalar(yaml, 6, "key", key.Key);
                        if (key.Action != RemoteKeyAction.Click)
                        {
                            AppendScalar(yaml, 8, "action", key.Action.ToString());
                        }

                        if (key.Repeat != 1)
                        {
                            AppendInteger(yaml, 8, "repeat", key.Repeat);
                        }

                        if (key.Delay is { } keyDelay)
                        {
                            AppendDuration(yaml, 8, "delay", keyDelay);
                        }

                        break;

                    case DelayStep delay:
                        yaml.Append("      - delay: ").AppendLine(FormatDuration(delay.Duration));
                        break;

                    case CallMacroStep call:
                        AppendListScalar(yaml, 6, "call", call.MacroName);
                        if (call.Repeat != 1)
                        {
                            AppendInteger(yaml, 8, "repeat", call.Repeat);
                        }

                        break;

                    case MenuStep menu:
                        AppendListScalar(yaml, 6, "menu", menu.TargetNodeId);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Macro step type '{step.GetType().Name}' is not supported.");
                }
            }
        }

        return yaml.ToString();
    }

    public async Task WriteFileAsync(
        string path,
        MacroCatalog catalog,
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
                    Serialize(catalog),
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

    private static void AppendListScalar(
        StringBuilder yaml,
        int indentation,
        string name,
        string value) =>
        yaml.Append(' ', indentation).Append("- ").Append(name).Append(": ").AppendLine(Quote(value));

    private static void AppendScalar(
        StringBuilder yaml,
        int indentation,
        string name,
        string value) =>
        yaml.Append(' ', indentation).Append(name).Append(": ").AppendLine(Quote(value));

    private static void AppendBoolean(StringBuilder yaml, int indentation, string name, bool value) =>
        yaml.Append(' ', indentation).Append(name).Append(": ").AppendLine(value ? "true" : "false");

    private static void AppendInteger(StringBuilder yaml, int indentation, string name, int value) =>
        yaml.Append(' ', indentation).Append(name).Append(": ")
            .AppendLine(value.ToString(CultureInfo.InvariantCulture));

    private static void AppendDuration(
        StringBuilder yaml,
        int indentation,
        string name,
        TimeSpan value) =>
        yaml.Append(' ', indentation).Append(name).Append(": ").AppendLine(FormatDuration(value));

    private static string FormatDuration(TimeSpan value) =>
        $"{value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms";

    private static string Quote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
