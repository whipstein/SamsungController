using System.Globalization;
using SamsungController.Core.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SamsungController.Automation.Navigation;

public sealed class MenuDefinitionParser
{
    private static readonly HashSet<string> RootFields =
        new(["version", "id", "name", "model", "context", "configurations", "timing", "nodes", "anchors", "transitions"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ContextFields =
        new(["firmware", "signal", "pictureMode", "input"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TimingFields =
        new(["defaultDelay", "screenChangeDelay", "returnDelay", "verified"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConfigurationFields =
        new(["id", "name", "conditions"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NodeFields =
        new(["id", "label", "parent", "description", "controlType", "defaultValue", "minimumValue", "maximumValue", "options", "disabledWhen", "hiddenWhen"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValueConditionFields =
        new(["setting", "equals"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AnchorFields =
        new(["id", "label", "target", "configuration", "verified", "description", "validationSource", "returnStrategy", "steps"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReturnStrategyFields =
        new(["menuRoot", "atMenuRoot", "belowMenuRoot", "overrides"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReturnOverrideFields =
        new(["node", "verified", "steps"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReturnScriptFields =
        new(["verified", "steps"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TransitionFields =
        new(["id", "from", "to", "configuration", "verified", "description", "generatedFromTopology", "topologySeed", "validationGroup", "validationRoute", "returnSteps", "steps"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StepFields =
        new(["key", "action", "repeat", "delay"], StringComparer.OrdinalIgnoreCase);

    public MenuDefinition Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1)
            {
                throw new MenuDefinitionParseException(
                    "A menu definition must contain exactly one YAML document.");
            }

            var root = RequireMapping(stream.Documents[0].RootNode, "document root");
            var fields = ReadFields(root, "document root");
            EnsureAllowedFields(fields, RootFields, "document root");
            ValidateVersion(RequiredScalar(fields, "version", "document root"));

            var context = ParseContext(fields.GetValueOrDefault("context"));
            var configurations = fields.TryGetValue("configurations", out var configurationsNode)
                ? ParseConfigurations(RequireSequence(configurationsNode, "configurations"))
                : [];
            var timing = ParseTiming(fields.GetValueOrDefault("timing"));
            var nodes = ParseNodes(RequiredSequence(fields, "nodes", "document root"));
            var anchors = fields.TryGetValue("anchors", out var anchorsNode)
                ? ParseAnchors(RequireSequence(anchorsNode, "anchors"))
                : [];
            var transitions = fields.TryGetValue("transitions", out var transitionsNode)
                ? ParseTransitions(RequireSequence(transitionsNode, "transitions"))
                : [];

            return new MenuDefinition(
                RequiredScalar(fields, "id", "document root"),
                RequiredScalar(fields, "name", "document root"),
                RequiredScalar(fields, "model", "document root"),
                context,
                nodes,
                transitions,
                anchors,
                timing,
                configurations);
        }
        catch (MenuDefinitionParseException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new MenuDefinitionParseException(
                $"Invalid menu definition YAML: {exception.Message}",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new MenuDefinitionParseException(exception.Message, exception);
        }
    }

    private static MenuTimingProfile ParseTiming(YamlNode? node)
    {
        if (node is null)
        {
            return new MenuTimingProfile();
        }

        var fields = ReadFields(RequireMapping(node, "timing"), "timing");
        EnsureAllowedFields(fields, TimingFields, "timing");
        var defaults = new MenuTimingProfile();
        return new MenuTimingProfile(
            ParseTimingMilliseconds(fields, "defaultDelay", defaults.DefaultDelayMilliseconds),
            ParseTimingMilliseconds(fields, "screenChangeDelay", defaults.ScreenChangeDelayMilliseconds),
            ParseTimingMilliseconds(fields, "returnDelay", defaults.ReturnDelayMilliseconds),
            OptionalBoolean(fields, "verified", "timing", defaults.Verified));
    }

    private static IReadOnlyList<MenuConfiguration> ParseConfigurations(YamlSequenceNode sequence)
    {
        var configurations = new List<MenuConfiguration>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"configuration {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, ConfigurationFields, context);
            configurations.Add(new MenuConfiguration(
                RequiredScalar(fields, "id", context),
                RequiredScalar(fields, "name", context),
                OptionalScalar(fields, "conditions")));
        }

        return configurations;
    }

    private static int ParseTimingMilliseconds(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        int defaultValue) =>
        fields.TryGetValue(name, out var node)
            ? checked((int)ParseDuration(RequireScalar(node, $"{name} in timing"), "timing").TotalMilliseconds)
            : defaultValue;

    public async Task<MenuDefinition> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Menu definition was not found: {fullPath}",
                fullPath);
        }

        var yaml = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Parse(yaml);
    }

    private static MenuDefinitionContext ParseContext(YamlNode? node)
    {
        if (node is null)
        {
            return new MenuDefinitionContext();
        }

        var fields = ReadFields(RequireMapping(node, "context"), "context");
        EnsureAllowedFields(fields, ContextFields, "context");
        return new MenuDefinitionContext(
            OptionalScalar(fields, "firmware") ?? "unrecorded",
            OptionalScalar(fields, "signal") ?? "any",
            OptionalScalar(fields, "pictureMode") ?? "any",
            OptionalScalar(fields, "input") ?? "any");
    }

    private static IReadOnlyList<MenuNode> ParseNodes(YamlSequenceNode sequence)
    {
        var nodes = new List<MenuNode>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"node {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, NodeFields, context);
            nodes.Add(new MenuNode(
                RequiredScalar(fields, "id", context),
                RequiredScalar(fields, "label", context),
                OptionalScalar(fields, "parent"),
                OptionalScalar(fields, "description"),
                ParseControlType(OptionalScalar(fields, "controlType"), context),
                OptionalScalar(fields, "defaultValue"),
                fields.TryGetValue("disabledWhen", out var disabledWhenNode)
                    ? ParseDisabledConditions(
                        RequireSequence(disabledWhenNode, $"disabledWhen in {context}"),
                        context)
                    : [],
                fields.TryGetValue("options", out var optionsNode)
                    ? ParseSelectionOptions(
                        RequireSequence(optionsNode, $"options in {context}"),
                        context)
                    : [],
                OptionalDecimal(fields, "minimumValue", context),
                OptionalDecimal(fields, "maximumValue", context),
                fields.TryGetValue("hiddenWhen", out var hiddenWhenNode)
                    ? ParseHiddenConditions(
                        RequireSequence(hiddenWhenNode, $"hiddenWhen in {context}"),
                        context)
                    : []));
        }

        return nodes;
    }

    private static MenuControlType ParseControlType(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MenuControlType.Submenu;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<MenuControlType>(normalized, ignoreCase: true, out var result)
               && Enum.IsDefined(result)
            ? result
            : throw new MenuDefinitionParseException(
                $"'controlType' in {context} must be submenu, slider, selection, submenu-selection, indexed-selection, switch, or confirmation.");
    }

    private static IReadOnlyList<MenuNodeDisabledCondition> ParseDisabledConditions(
        YamlSequenceNode sequence,
        string nodeContext)
    {
        var conditions = new List<MenuNodeDisabledCondition>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"{nodeContext} disabled condition {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, ValueConditionFields, context);
            conditions.Add(new MenuNodeDisabledCondition(
                RequiredScalar(fields, "setting", context),
                RequiredScalar(fields, "equals", context)));
        }

        return conditions;
    }

    private static IReadOnlyList<MenuNodeHiddenCondition> ParseHiddenConditions(
        YamlSequenceNode sequence,
        string nodeContext)
    {
        var conditions = new List<MenuNodeHiddenCondition>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"{nodeContext} hidden condition {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, ValueConditionFields, context);
            conditions.Add(new MenuNodeHiddenCondition(
                RequiredScalar(fields, "setting", context),
                RequiredScalar(fields, "equals", context)));
        }

        return conditions;
    }

    private static IReadOnlyList<string> ParseSelectionOptions(
        YamlSequenceNode sequence,
        string nodeContext)
    {
        var options = new List<string>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            options.Add(RequireScalar(
                sequence.Children[index],
                $"{nodeContext} option {index + 1}"));
        }

        return options;
    }

    private static decimal? OptionalDecimal(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string context)
    {
        if (!fields.TryGetValue(name, out var node))
        {
            return null;
        }

        var value = RequireScalar(node, $"'{name}' in {context}");
        return decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : throw new MenuDefinitionParseException(
                $"'{name}' in {context} must be a number.");
    }

    private static IReadOnlyList<MenuAnchor> ParseAnchors(YamlSequenceNode sequence)
    {
        var anchors = new List<MenuAnchor>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"anchor {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, AnchorFields, context);
            anchors.Add(new MenuAnchor(
                RequiredScalar(fields, "id", context),
                RequiredScalar(fields, "label", context),
                RequiredScalar(fields, "target", context),
                ParseOperations(RequiredSequence(fields, "steps", context), context),
                OptionalBoolean(fields, "verified", context),
                OptionalScalar(fields, "description"),
                fields.TryGetValue("returnStrategy", out var strategyNode)
                    ? ParseReturnStrategy(strategyNode, context)
                    : null,
                OptionalScalar(fields, "validationSource"),
                OptionalScalar(fields, "configuration")));
        }

        return anchors;
    }

    private static MenuReturnStrategy ParseReturnStrategy(
        YamlNode node,
        string anchorContext)
    {
        var context = $"{anchorContext} returnStrategy";
        var fields = ReadFields(RequireMapping(node, context), context);
        EnsureAllowedFields(fields, ReturnStrategyFields, context);
        return new MenuReturnStrategy(
            RequiredScalar(fields, "menuRoot", context),
            ParseReturnScript(RequiredMapping(fields, "atMenuRoot", context), $"{context} atMenuRoot"),
            ParseReturnScript(RequiredMapping(fields, "belowMenuRoot", context), $"{context} belowMenuRoot"),
            fields.TryGetValue("overrides", out var overridesNode)
                ? ParseReturnOverrides(RequireSequence(overridesNode, $"{context} overrides"), context)
                : []);
    }

    private static IReadOnlyList<MenuReturnOverride> ParseReturnOverrides(
        YamlSequenceNode sequence,
        string strategyContext)
    {
        var overrides = new List<MenuReturnOverride>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"{strategyContext} override {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, ReturnOverrideFields, context);
            overrides.Add(new MenuReturnOverride(
                RequiredScalar(fields, "node", context),
                new MenuReturnScript(
                    ParseOperations(RequiredSequence(fields, "steps", context), context),
                    OptionalBoolean(fields, "verified", context))));
        }

        return overrides;
    }

    private static MenuReturnScript ParseReturnScript(
        YamlMappingNode mapping,
        string context)
    {
        var fields = ReadFields(mapping, context);
        EnsureAllowedFields(fields, ReturnScriptFields, context);
        return new MenuReturnScript(
            ParseOperations(RequiredSequence(fields, "steps", context), context),
            OptionalBoolean(fields, "verified", context));
    }

    private static IReadOnlyList<MenuTransition> ParseTransitions(YamlSequenceNode sequence)
    {
        var transitions = new List<MenuTransition>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"transition {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, TransitionFields, context);
            transitions.Add(new MenuTransition(
                RequiredScalar(fields, "id", context),
                RequiredScalar(fields, "from", context),
                RequiredScalar(fields, "to", context),
                ParseOperations(RequiredSequence(fields, "steps", context), context),
                OptionalBoolean(fields, "verified", context),
                OptionalScalar(fields, "description"),
                fields.TryGetValue("returnSteps", out var returnStepsNode)
                    ? ParseOperations(
                        RequireSequence(returnStepsNode, $"'returnSteps' in {context}"),
                        $"{context}, return-to-video")
                    : null,
                OptionalScalar(fields, "configuration"),
                OptionalBoolean(fields, "generatedFromTopology", context),
                OptionalScalar(fields, "topologySeed"),
                OptionalScalar(fields, "validationGroup"),
                OptionalBoolean(fields, "validationRoute", context)));
        }

        return transitions;
    }

    private static IReadOnlyList<MenuOperation> ParseOperations(
        YamlSequenceNode sequence,
        string ownerContext)
    {
        var operations = new List<MenuOperation>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"{ownerContext}, step {index + 1}";
            var fields = ReadFields(RequireMapping(sequence.Children[index], context), context);
            EnsureAllowedFields(fields, StepFields, context);
            operations.Add(new MenuOperation(
                RequiredScalar(fields, "key", context),
                ParseAction(OptionalScalar(fields, "action") ?? "Click", context),
                ParseInteger(OptionalScalar(fields, "repeat") ?? "1", context),
                fields.TryGetValue("delay", out var delayNode)
                    ? ParseDuration(RequireScalar(delayNode, $"delay in {context}"), context)
                    : null));
        }

        return operations;
    }

    private static RemoteKeyAction ParseAction(string value, string context) =>
        Enum.TryParse<RemoteKeyAction>(value, ignoreCase: true, out var action)
        && Enum.IsDefined(action)
            ? action
            : throw new MenuDefinitionParseException(
                $"The action in {context} must be Click, Press, or Release.");

    private static int ParseInteger(string value, string context) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new MenuDefinitionParseException(
                $"The repeat count in {context} must be an integer.");

    private static TimeSpan ParseDuration(string value, string context)
    {
        var trimmed = value.Trim();
        if (TryParseUnit(trimmed, "ms", TimeSpan.FromMilliseconds, out var duration)
            || TryParseUnit(trimmed, "s", TimeSpan.FromSeconds, out duration))
        {
            return duration;
        }

        throw new MenuDefinitionParseException(
            $"The delay in {context} must use milliseconds (for example 150ms) or seconds (for example 1.5s).");
    }

    private static bool TryParseUnit(
        string value,
        string suffix,
        Func<double, TimeSpan> convert,
        out TimeSpan duration)
    {
        duration = default;
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(
                value[..^suffix.Length],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return false;
        }

        try
        {
            duration = convert(number);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ValidateVersion(string version)
    {
        if (!version.Equals("1", StringComparison.Ordinal))
        {
            throw new MenuDefinitionParseException(
                $"Unsupported menu definition version '{version}'. This build supports version 1.");
        }
    }

    private static bool OptionalBoolean(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string context,
        bool defaultValue = false)
    {
        var value = OptionalScalar(fields, name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new MenuDefinitionParseException(
                $"'{name}' in {context} must be true or false.");
    }

    private static string RequiredScalar(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string context)
    {
        if (!fields.TryGetValue(name, out var node))
        {
            throw new MenuDefinitionParseException(
                $"{context} must contain '{name}'.");
        }

        var value = RequireScalar(node, $"'{name}' in {context}").Trim();
        return value.Length > 0
            ? value
            : throw new MenuDefinitionParseException(
                $"'{name}' in {context} cannot be empty.");
    }

    private static string? OptionalScalar(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name) =>
        fields.TryGetValue(name, out var node)
            ? RequireScalar(node, $"'{name}'").Trim()
            : null;

    private static YamlSequenceNode RequiredSequence(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string context)
    {
        if (!fields.TryGetValue(name, out var node))
        {
            throw new MenuDefinitionParseException(
                $"{context} must contain a '{name}' sequence.");
        }

        return RequireSequence(node, $"'{name}' in {context}");
    }

    private static YamlMappingNode RequiredMapping(
        IReadOnlyDictionary<string, YamlNode> fields,
        string name,
        string context)
    {
        if (!fields.TryGetValue(name, out var node))
        {
            throw new MenuDefinitionParseException(
                $"{context} must contain an '{name}' mapping.");
        }

        return RequireMapping(node, $"'{name}' in {context}");
    }

    private static Dictionary<string, YamlNode> ReadFields(
        YamlMappingNode mapping,
        string context)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = RequireScalar(keyNode, $"field name in {context}");
            if (!result.TryAdd(key, valueNode))
            {
                throw new MenuDefinitionParseException(
                    $"Field '{key}' appears more than once in {context}.");
            }
        }

        return result;
    }

    private static void EnsureAllowedFields(
        IReadOnlyDictionary<string, YamlNode> fields,
        IReadOnlySet<string> allowed,
        string context)
    {
        var unknown = fields.Keys.Where(field => !allowed.Contains(field)).ToArray();
        if (unknown.Length > 0)
        {
            throw new MenuDefinitionParseException(
                $"Unknown field(s) in {context}: {string.Join(", ", unknown)}.");
        }
    }

    private static string RequireScalar(YamlNode node, string context) =>
        node is YamlScalarNode { Value: not null } scalar
            ? scalar.Value
            : throw new MenuDefinitionParseException($"Expected a scalar for {context}.");

    private static YamlMappingNode RequireMapping(YamlNode node, string context) =>
        node as YamlMappingNode
        ?? throw new MenuDefinitionParseException($"Expected a mapping for {context}.");

    private static YamlSequenceNode RequireSequence(YamlNode node, string context) =>
        node as YamlSequenceNode
        ?? throw new MenuDefinitionParseException($"Expected a sequence for {context}.");
}
