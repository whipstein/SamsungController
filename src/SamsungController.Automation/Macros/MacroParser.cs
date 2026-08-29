using System.Globalization;
using System.Text;
using SamsungController.Core.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SamsungController.Automation.Macros;

public sealed class MacroParser
{
    private static readonly HashSet<string> RootFields =
        new(["version", "variables", "macros"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DefinitionFields =
        new(["description", "verified", "verificationPasses", "steps"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KeyFields =
        new(["key", "action", "repeat", "delay"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CallFields =
        new(["call", "repeat"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DelayFields =
        new(["delay"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MenuFields =
        new(["menu"], StringComparer.OrdinalIgnoreCase);

    public MacroCatalog Parse(
        string yaml,
        IReadOnlyDictionary<string, string>? variableOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count != 1)
            {
                throw new MacroParseException("A macro file must contain exactly one YAML document.");
            }

            var root = RequireMapping(stream.Documents[0].RootNode, "document root");
            var rootFields = ReadFields(root, "document root");
            EnsureAllowedFields(rootFields, RootFields, "document root");

            var rawVariables = ReadVariables(rootFields.GetValueOrDefault("variables"));
            if (variableOverrides is not null)
            {
                foreach (var (name, value) in variableOverrides)
                {
                    ValidateVariableName(name);
                    rawVariables[name] = value;
                }
            }

            var resolver = new VariableResolver(rawVariables);
            var variables = resolver.ResolveAll();
            ValidateVersion(rootFields.GetValueOrDefault("version"), resolver);

            if (!rootFields.TryGetValue("macros", out var macrosNode))
            {
                throw new MacroParseException("The document root must contain a 'macros' mapping.");
            }

            var macrosMapping = RequireMapping(macrosNode, "macros");
            var definitions = new List<MacroDefinition>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (nameNode, definitionNode) in macrosMapping.Children)
            {
                var name = resolver.ResolveText(RequireScalar(nameNode, "macro name")).Trim();
                if (name.Length == 0)
                {
                    throw new MacroParseException("Macro names cannot be empty.");
                }

                if (!names.Add(name))
                {
                    throw new MacroParseException(
                        $"Macro names must be unique ignoring case: '{name}'.");
                }

                definitions.Add(ParseDefinition(name, definitionNode, resolver));
            }

            return new MacroCatalog(definitions, variables);
        }
        catch (MacroParseException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new MacroParseException($"Invalid macro YAML: {exception.Message}", exception);
        }
    }

    public async Task<MacroCatalog> ParseFileAsync(
        string path,
        IReadOnlyDictionary<string, string>? variableOverrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Macro file was not found: {fullPath}",
                fullPath);
        }

        var yaml = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return Parse(yaml, variableOverrides);
    }

    private static MacroDefinition ParseDefinition(
        string name,
        YamlNode node,
        VariableResolver resolver)
    {
        if (node is YamlSequenceNode directSteps)
        {
            return new MacroDefinition(name, ParseSteps(name, directSteps, resolver));
        }

        var mapping = RequireMapping(node, $"macro '{name}'");
        var fields = ReadFields(mapping, $"macro '{name}'");
        EnsureAllowedFields(fields, DefinitionFields, $"macro '{name}'");
        if (!fields.TryGetValue("steps", out var stepsNode))
        {
            throw new MacroParseException($"Macro '{name}' must contain a 'steps' sequence.");
        }

        var description = fields.TryGetValue("description", out var descriptionNode)
            ? resolver.ResolveText(RequireScalar(descriptionNode, $"description for macro '{name}'"))
            : null;
        var verified = fields.TryGetValue("verified", out var verifiedNode)
            && ParseBoolean(
                resolver.ResolveText(RequireScalar(verifiedNode, $"verified for macro '{name}'")),
                $"verified for macro '{name}'");
        var verificationPasses = fields.TryGetValue("verificationPasses", out var passesNode)
            ? ParseVerificationPasses(
                resolver.ResolveText(RequireScalar(passesNode, $"verificationPasses for macro '{name}'")),
                name)
            : 0;
        return new MacroDefinition(
            name,
            ParseSteps(name, RequireSequence(stepsNode, $"steps for macro '{name}'"), resolver),
            description,
            verified,
            verificationPasses);
    }

    private static IReadOnlyList<MacroStep> ParseSteps(
        string macroName,
        YamlSequenceNode sequence,
        VariableResolver resolver)
    {
        var steps = new List<MacroStep>(sequence.Children.Count);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var context = $"macro '{macroName}', step {index + 1}";
            var mapping = RequireMapping(sequence.Children[index], context);
            var fields = ReadFields(mapping, context);

            if (fields.ContainsKey("key"))
            {
                EnsureAllowedFields(fields, KeyFields, context);
                var key = resolver.ResolveText(
                    RequireScalar(fields["key"], $"key in {context}")).Trim();
                var action = fields.TryGetValue("action", out var actionNode)
                    ? ParseAction(
                        resolver.ResolveText(
                            RequireScalar(actionNode, $"action in {context}")).Trim(),
                        context)
                    : RemoteKeyAction.Click;
                var repeat = fields.TryGetValue("repeat", out var repeatNode)
                    ? ParseInteger(resolver.ResolveText(RequireScalar(repeatNode, $"repeat in {context}")), context)
                    : 1;
                TimeSpan? delay = fields.TryGetValue("delay", out var delayNode)
                    ? ParseDuration(resolver.ResolveText(RequireScalar(delayNode, $"delay in {context}")), context)
                    : null;
                steps.Add(new KeyStep(key, action, repeat, delay));
                continue;
            }

            if (fields.ContainsKey("call"))
            {
                EnsureAllowedFields(fields, CallFields, context);
                var calledMacro = resolver.ResolveText(
                    RequireScalar(fields["call"], $"call in {context}")).Trim();
                var repeat = fields.TryGetValue("repeat", out var repeatNode)
                    ? ParseInteger(resolver.ResolveText(RequireScalar(repeatNode, $"repeat in {context}")), context)
                    : 1;
                steps.Add(new CallMacroStep(calledMacro, repeat));
                continue;
            }

            if (fields.ContainsKey("delay"))
            {
                EnsureAllowedFields(fields, DelayFields, context);
                var duration = ParseDuration(
                    resolver.ResolveText(RequireScalar(fields["delay"], $"delay in {context}")),
                    context);
                steps.Add(new DelayStep(duration));
                continue;
            }

            if (fields.ContainsKey("menu"))
            {
                EnsureAllowedFields(fields, MenuFields, context);
                var targetNodeId = resolver.ResolveText(
                    RequireScalar(fields["menu"], $"menu destination in {context}")).Trim();
                steps.Add(new MenuStep(targetNodeId));
                continue;
            }

            throw new MacroParseException(
                $"{context} must contain exactly one of 'key', 'call', 'delay', or 'menu'.");
        }

        return steps;
    }

    private static Dictionary<string, string> ReadVariables(YamlNode? node)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
        {
            return variables;
        }

        var mapping = RequireMapping(node, "variables");
        foreach (var (nameNode, valueNode) in mapping.Children)
        {
            var name = RequireScalar(nameNode, "variable name");
            ValidateVariableName(name);
            if (!variables.TryAdd(name, RequireScalar(valueNode, $"variable '{name}'")))
            {
                throw new MacroParseException($"Variable '{name}' is defined more than once.");
            }
        }

        return variables;
    }

    private static void ValidateVersion(YamlNode? node, VariableResolver resolver)
    {
        if (node is null)
        {
            return;
        }

        var value = resolver.ResolveText(RequireScalar(node, "version"));
        if (!value.Equals("1", StringComparison.Ordinal))
        {
            throw new MacroParseException(
                $"Unsupported macro file version '{value}'. This build supports version 1.");
        }
    }

    private static RemoteKeyAction ParseAction(string value, string context) =>
        Enum.TryParse<RemoteKeyAction>(value, ignoreCase: true, out var action)
        && Enum.IsDefined(action)
            ? action
            : throw new MacroParseException(
                $"The action in {context} must be Click, Press, or Release.");

    private static int ParseInteger(string value, string context) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new MacroParseException($"The repeat count in {context} must be an integer.");

    private static bool ParseBoolean(string value, string context) =>
        bool.TryParse(value, out var result)
            ? result
            : throw new MacroParseException($"The value of {context} must be true or false.");

    private static int ParseVerificationPasses(string value, string macroName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            || result is < 0 or > 3)
        {
            throw new MacroParseException(
                $"The verificationPasses for macro '{macroName}' must be an integer from 0 through 3.");
        }

        return result;
    }

    private static TimeSpan ParseDuration(string value, string context)
    {
        var trimmed = value.Trim();
        try
        {
            if (TryParseUnit(trimmed, "ms", TimeSpan.FromMilliseconds, out var duration)
                || TryParseUnit(trimmed, "s", TimeSpan.FromSeconds, out duration)
                || TryParseUnit(trimmed, "m", TimeSpan.FromMinutes, out duration)
                || TryParseUnit(trimmed, "h", TimeSpan.FromHours, out duration))
            {
                return duration;
            }

            if (double.TryParse(
                    trimmed,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var milliseconds))
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out duration))
            {
                return duration;
            }
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentException)
        {
            // Report the same actionable parse error below.
        }

        throw new MacroParseException(
            $"The duration '{value}' in {context} is invalid. Use values such as 150ms, 2s, or 00:00:02.");
    }

    private static bool TryParseUnit(
        string value,
        string suffix,
        Func<double, TimeSpan> factory,
        out TimeSpan duration)
    {
        duration = default;
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var number = value[..^suffix.Length].Trim();
        if (!double.TryParse(
                number,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return false;
        }

        duration = factory(amount);
        return true;
    }

    private static Dictionary<string, YamlNode> ReadFields(
        YamlMappingNode mapping,
        string context)
    {
        var fields = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = RequireScalar(keyNode, $"field name in {context}");
            if (!fields.TryAdd(key, valueNode))
            {
                throw new MacroParseException($"Field '{key}' occurs more than once in {context}.");
            }
        }

        return fields;
    }

    private static void EnsureAllowedFields(
        IReadOnlyDictionary<string, YamlNode> fields,
        IReadOnlySet<string> allowed,
        string context)
    {
        var unknown = fields.Keys.FirstOrDefault(field => !allowed.Contains(field));
        if (unknown is not null)
        {
            throw new MacroParseException($"Unknown field '{unknown}' in {context}.");
        }
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string context) =>
        node as YamlMappingNode
        ?? throw new MacroParseException($"Expected a YAML mapping for {context}.");

    private static YamlSequenceNode RequireSequence(YamlNode node, string context) =>
        node as YamlSequenceNode
        ?? throw new MacroParseException($"Expected a YAML sequence for {context}.");

    private static string RequireScalar(YamlNode node, string context) =>
        node is YamlScalarNode scalar && scalar.Value is not null
            ? scalar.Value
            : throw new MacroParseException($"Expected a scalar value for {context}.");

    private static void ValidateVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !(char.IsLetter(name[0]) || name[0] == '_')
            || name.Any(character =>
                !(char.IsLetterOrDigit(character)
                  || character is '_' or '-' or '.')))
        {
            throw new MacroParseException(
                $"Invalid variable name '{name}'. Use letters, numbers, underscores, periods, or hyphens.");
        }
    }

    private sealed class VariableResolver(IReadOnlyDictionary<string, string> rawVariables)
    {
        private readonly IReadOnlyDictionary<string, string> _rawVariables = rawVariables;
        private readonly Dictionary<string, string> _resolved =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _resolutionPath = [];

        public IReadOnlyDictionary<string, string> ResolveAll()
        {
            foreach (var name in _rawVariables.Keys)
            {
                ResolveVariable(name);
            }

            return new Dictionary<string, string>(_resolved, StringComparer.OrdinalIgnoreCase);
        }

        public string ResolveText(string value)
        {
            var result = new StringBuilder(value.Length);
            var position = 0;
            while (position < value.Length)
            {
                var start = value.IndexOf("${", position, StringComparison.Ordinal);
                if (start < 0)
                {
                    result.Append(value, position, value.Length - position);
                    break;
                }

                result.Append(value, position, start - position);
                var end = value.IndexOf('}', start + 2);
                if (end < 0)
                {
                    throw new MacroParseException(
                        $"Variable expression beginning at '{value[start..]}' is missing a closing brace.");
                }

                var name = value[(start + 2)..end];
                ValidateVariableName(name);
                result.Append(ResolveVariable(name));
                position = end + 1;
            }

            return result.ToString();
        }

        private string ResolveVariable(string name)
        {
            if (_resolved.TryGetValue(name, out var resolved))
            {
                return resolved;
            }

            if (!_rawVariables.TryGetValue(name, out var raw))
            {
                throw new MacroParseException($"Variable '{name}' is not defined.");
            }

            var cycleIndex = _resolutionPath.FindIndex(
                item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cycleIndex >= 0)
            {
                var cycle = _resolutionPath.Skip(cycleIndex).Append(name);
                throw new MacroParseException(
                    $"Variable cycle detected: {string.Join(" -> ", cycle)}.");
            }

            _resolutionPath.Add(name);
            try
            {
                resolved = ResolveText(raw);
                _resolved[name] = resolved;
                return resolved;
            }
            finally
            {
                _resolutionPath.RemoveAt(_resolutionPath.Count - 1);
            }
        }
    }
}
