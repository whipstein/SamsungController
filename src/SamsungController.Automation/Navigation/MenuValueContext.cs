using System.Globalization;

namespace SamsungController.Automation.Navigation;

// Storage dependencies are distinct from visibility rules. An explicit empty
// context means shared values; null inherits the nearest parent's declaration.
public static class MenuValueContext
{
    public static bool IsEnabled(MenuDefinition definition) => definition.Nodes.Values.Any(node => node.ValueContext is not null);

    public static string CanonicalSource(string source) => source.StartsWith("setting:", StringComparison.OrdinalIgnoreCase)
        ? "setting:" + source[8..]
        : "external:" + (source.StartsWith("external:", StringComparison.OrdinalIgnoreCase) ? source[9..] : source);

    public static IReadOnlyList<string> Sources(MenuDefinition definition, MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.ValueContext is { } declared)
                return declared.Select(CanonicalSource)
                    // A context selector's own value must not be keyed by itself.
                    .Where(source => !source.Equals("setting:" + node.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (current.ParentId is null || !definition.Nodes.TryGetValue(current.ParentId, out current))
                break;
        }
        return [];
    }

    public static string? SourceValue(MenuDefinition definition, string source,
        IReadOnlyDictionary<string, string>? externalValues, IReadOnlyDictionary<string, string>? settingValues)
    {
        var key = CanonicalSource(source);
        if (key.StartsWith("setting:", StringComparison.Ordinal))
        {
            var id = key[8..];
            return settingValues?.GetValueOrDefault(id)
                ?? (definition.Nodes.TryGetValue(id, out var node)
                    ? MenuDefaultValueResolver.Resolve(definition, node, externalValues, settingValues) : null);
        }
        var stateId = key[9..];
        return externalValues?.GetValueOrDefault(stateId)
            ?? definition.ExternalStates.GetValueOrDefault(stateId)?.DefaultValue;
    }

    public static string? ValidateSource(MenuDefinition definition, string source, string? value = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source != source.Trim())
            return "A condition source must be a nonempty ID without surrounding spaces.";
        var key = CanonicalSource(source);
        if (!key.StartsWith("setting:", StringComparison.Ordinal))
        {
            if (!definition.ExternalStates.TryGetValue(key[9..], out var state))
                return $"External state '{key[9..]}' does not exist.";
            return value is not null && !state.Options.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? $"Value '{value}' is not an available option for external state '{state.Id}'." : null;
        }
        if (!definition.Nodes.TryGetValue(key[8..], out var node))
            return $"Menu setting '{key[8..]}' does not exist.";
        if (node.ControlType is not (MenuControlType.Selection or MenuControlType.SubmenuSelection
            or MenuControlType.Switch or MenuControlType.Slider))
            return $"'{node.Id}' is not a supported context setting (selection, submenu-selection, switch, or slider).";
        if (value is null)
            return null;
        var valid = node.ControlType switch
        {
            MenuControlType.Switch => value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase),
            MenuControlType.Slider => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                && number == decimal.Truncate(number) && number >= node.MinimumValue && number <= node.MaximumValue,
            _ => (node.SelectionOptions ?? []).Contains(value, StringComparer.OrdinalIgnoreCase)
        };
        return valid ? null : $"Value '{value}' is not valid for menu setting '{node.Id}'.";
    }

    public static string NormalizeSourceValue(MenuDefinition definition, string source, string value)
    {
        var key = CanonicalSource(source);
        return key.StartsWith("setting:", StringComparison.Ordinal)
            && definition.Nodes.TryGetValue(key[8..], out var node) && node.ControlType == MenuControlType.Slider
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("G29", CultureInfo.InvariantCulture) : value;
    }

    public static IEnumerable<string> SettingDependencies(MenuDefinition definition, MenuNode node) =>
        Sources(definition, node).Concat((node.DefaultValueWhen ?? []).SelectMany(rule => rule.When.Keys).Select(CanonicalSource))
            .Where(source => source.StartsWith("setting:", StringComparison.Ordinal)).Select(source => source[8..]).Distinct(StringComparer.OrdinalIgnoreCase);
}
