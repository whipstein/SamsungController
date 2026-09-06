using System.Globalization;

namespace SamsungController.Automation.Navigation;

public sealed record MenuDefinitionValidationError(string Location, string Message)
{
    public override string ToString() => $"{Location}: {Message}";
}

public sealed class MenuDefinitionValidationException : Exception
{
    public MenuDefinitionValidationException(IReadOnlyList<MenuDefinitionValidationError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<MenuDefinitionValidationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<MenuDefinitionValidationError> errors) =>
        errors.Count == 0
            ? "Menu definition validation failed."
            : "Menu definition validation failed:" + Environment.NewLine
              + string.Join(Environment.NewLine, errors.Select(error => $"- {error}"));
}

public sealed class MenuDefinitionValidator
{
    public const int MaximumRepeat = 100;
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    public IReadOnlyList<MenuDefinitionValidationError> Validate(MenuDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<MenuDefinitionValidationError>();

        ValidateIdentifier("definition", definition.Id, errors);
        ValidateRequired("definition", "name", definition.Name, errors);
        ValidateRequired("definition", "model", definition.Model, errors);
        ValidateVerification(definition.Verification, errors);
        foreach (var configuration in definition.Configurations.Values)
        {
            var location = $"configuration '{configuration.Id}'";
            ValidateIdentifier(location, configuration.Id, errors);
            ValidateRequired(location, "name", configuration.Name, errors);
        }

        foreach (var state in definition.ExternalStates.Values)
        {
            ValidateExternalState(state, errors);
        }

        if (definition.ActiveConfigurationId is { } activeConfigurationId
            && !definition.Configurations.ContainsKey(activeConfigurationId))
        {
            errors.Add(new MenuDefinitionValidationError(
                "active configuration",
                $"Configuration '{activeConfigurationId}' does not exist."));
        }

        ValidateTiming(definition.Timing, errors);
        if (definition.Nodes.Count == 0)
        {
            errors.Add(new MenuDefinitionValidationError(
                "nodes",
                "At least one menu node is required."));
            return errors;
        }

        var nodesWithChildren = definition.Nodes.Values
            .Where(node => !string.IsNullOrWhiteSpace(node.ParentId))
            .Select(node => node.ParentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes.Values)
        {
            ValidateIdentifier($"node '{node.Id}'", node.Id, errors);
            ValidateRequired($"node '{node.Id}'", "label", node.Label, errors);
            if (!string.IsNullOrWhiteSpace(node.ParentId)
                && !definition.Nodes.ContainsKey(node.ParentId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    $"node '{node.Id}'",
                    $"Parent node '{node.ParentId}' does not exist."));
            }
            else if (node.Id.Equals(node.ParentId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new MenuDefinitionValidationError(
                    $"node '{node.Id}'",
                    "A node cannot be its own parent."));
            }
            if (node.ControlType != MenuControlType.Submenu
                && nodesWithChildren.Contains(node.Id))
            {
                errors.Add(new MenuDefinitionValidationError(
                    $"node '{node.Id}'",
                    "Only a submenu node can contain children."));
            }

            ValidateMenuNodeBehavior(definition, node, errors);
            ValidateDefaultValueRules(definition, node, errors);
        }

        DetectParentCycles(definition, errors);

        foreach (var anchor in definition.Anchors.Values)
        {
            var location = $"anchor '{anchor.Id}'";
            ValidateIdentifier(location, anchor.Id, errors);
            ValidateRequired(location, "label", anchor.Label, errors);
            ValidateConfigurationReference(definition, anchor.ConfigurationId, location, errors);
            if (!definition.Nodes.ContainsKey(anchor.TargetNodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    $"Target node '{anchor.TargetNodeId}' does not exist."));
            }

            if (!string.IsNullOrWhiteSpace(anchor.ValidationSourceNodeId)
                && !definition.Nodes.ContainsKey(anchor.ValidationSourceNodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    $"Validation source node '{anchor.ValidationSourceNodeId}' does not exist."));
            }

            ValidateOperations(location, anchor.Operations, errors);
            ValidateReturnStrategy(definition, anchor, location, errors);
        }

        foreach (var transition in definition.Transitions.Values)
        {
            var location = $"transition '{transition.Id}'";
            ValidateIdentifier(location, transition.Id, errors);
            ValidateConfigurationReference(definition, transition.ConfigurationId, location, errors);
            if (!definition.Nodes.ContainsKey(transition.FromNodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    $"Source node '{transition.FromNodeId}' does not exist."));
            }

            if (!definition.Nodes.ContainsKey(transition.ToNodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    $"Target node '{transition.ToNodeId}' does not exist."));
            }

            if (transition.FromNodeId.Equals(
                    transition.ToNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "Source and target nodes must be different."));
            }

            if (transition.GeneratedFromTopology)
            {
                if (string.IsNullOrWhiteSpace(transition.TopologySeedTransitionId))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        location,
                        "A topology-generated transition must identify its seed transition."));
                }
                else if (!definition.Transitions.TryGetValue(
                             transition.TopologySeedTransitionId,
                             out var seedTransition)
                         || seedTransition.GeneratedFromTopology)
                {
                    errors.Add(new MenuDefinitionValidationError(
                        location,
                        $"Topology seed transition '{transition.TopologySeedTransitionId}' does not exist or is itself generated."));
                }

                if (string.IsNullOrWhiteSpace(transition.ValidationGroupId))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        location,
                        "A topology-generated transition must identify its validation group."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(transition.TopologySeedTransitionId)
                     || !string.IsNullOrWhiteSpace(transition.ValidationGroupId)
                     || transition.IsValidationRoute)
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "Only topology-generated transitions can define topology validation metadata."));
            }

            ValidateOperations(location, transition.Operations, errors);
            if (transition.ReturnToVideoOperations is { } returnOperations)
            {
                ValidateOperations($"{location} returnSteps", returnOperations, errors);
            }
        }

        return errors;
    }

    private static void ValidateDefaultValueRules(
        MenuDefinition definition,
        MenuNode node,
        ICollection<MenuDefinitionValidationError> errors)
    {
        var rules = node.DefaultValueWhen ?? [];
        if (rules.Count > 20)
        {
            errors.Add(new($"node '{node.Id}' defaultValueWhen", "At most 20 conditional defaults are allowed."));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var location = $"node '{node.Id}' defaultValueWhen rule {index + 1}";
            if (rule.When.Count == 0)
            {
                errors.Add(new(location, "'when' must contain at least one external-state condition; use defaultValue for the fallback."));
            }
            if (rule.When.Count > 20)
            {
                errors.Add(new(location, "A conditional default can match at most 20 external states."));
            }
            foreach (var condition in rule.When)
            {
                if (!definition.ExternalStates.TryGetValue(condition.Key, out var state))
                {
                    errors.Add(new(location, $"External state '{condition.Key}' does not exist."));
                }
                else if (!state.Options.Contains(condition.Value, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new(location, $"Value '{condition.Value}' is not an available option for external state '{condition.Key}'."));
                }
            }

            var signature = string.Join("\u001e", rule.When.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}\u001f{pair.Value}"));
            if (!seen.Add(signature))
            {
                errors.Add(new(location, "These conditions duplicate an earlier default rule and would never be used."));
            }

            var valueErrors = new List<MenuDefinitionValidationError>();
            ValidateMenuNodeBehavior(definition, node with { DefaultValue = rule.Value, DefaultValueWhen = [] }, valueErrors);
            foreach (var error in valueErrors)
            {
                errors.Add(new(location, error.Message));
            }
        }
    }

    private static void ValidateMenuNodeBehavior(
        MenuDefinition definition,
        MenuNode node,
        ICollection<MenuDefinitionValidationError> errors)
    {
        var location = $"node '{node.Id}'";
        if (!Enum.IsDefined(node.ControlType))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "Control type must be Submenu, Slider, Selection, SubmenuSelection, IndexedSelection, Switch, Confirmation, or Action."));
        }

        var hasDefaultValue = !string.IsNullOrWhiteSpace(node.DefaultValue);
        var isValuelessControl = node.ControlType is MenuControlType.Submenu
            or MenuControlType.Action;
        if (isValuelessControl && hasDefaultValue)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"A {node.ControlType.ToString().ToLowerInvariant()} cannot have a default value."));
        }
        else if (!isValuelessControl && !hasDefaultValue)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"A {node.ControlType.ToString().ToLowerInvariant()} must define its default value."));
        }

        if (node.ControlType == MenuControlType.Switch
            && hasDefaultValue
            && !node.DefaultValue!.Equals("on", StringComparison.OrdinalIgnoreCase)
            && !node.DefaultValue.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "A switch default value must be 'on' or 'off'."));
        }

        if (node.ControlType == MenuControlType.Slider)
        {
            if (node.MinimumValue is null || node.MaximumValue is null)
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "A slider must define both minimum and maximum values."));
            }
            else if (node.MinimumValue >= node.MaximumValue)
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "A slider minimum value must be less than its maximum value."));
            }

            if (hasDefaultValue)
            {
                if (!decimal.TryParse(
                        node.DefaultValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var sliderDefault))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        location,
                        "A slider default value must be numeric."));
                }
                else if (node.MinimumValue is { } minimum
                         && node.MaximumValue is { } maximum
                         && (sliderDefault < minimum || sliderDefault > maximum))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        location,
                        $"Slider default value '{node.DefaultValue}' must be between {minimum:G29} and {maximum:G29}."));
                }
            }
        }
        else if (node.MinimumValue is not null || node.MaximumValue is not null)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "Only a slider can define minimum and maximum values."));
        }

        var selectionOptions = node.SelectionOptions ?? [];
        var isChoiceControl = node.ControlType is MenuControlType.Selection
            or MenuControlType.SubmenuSelection
            or MenuControlType.IndexedSelection
            or MenuControlType.Confirmation;
        if (isChoiceControl)
        {
            var minimumOptions = node.ControlType == MenuControlType.Confirmation ? 2 : 1;
            if (selectionOptions.Count < minimumOptions)
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    node.ControlType == MenuControlType.Confirmation
                        ? "A confirmation must define at least two available choices."
                        : $"A {FormatChoiceControlName(node.ControlType).ToLowerInvariant()} must define at least one available option."));
            }
            if (hasDefaultValue && !selectionOptions.Any(option => option.Equals(
                    node.DefaultValue,
                    StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    node.ControlType == MenuControlType.Confirmation
                        ? $"Confirmation default value '{node.DefaultValue}' must match an available choice."
                        : $"{FormatChoiceControlName(node.ControlType)} default value '{node.DefaultValue}' must match an available option."));
            }
        }
        else if (selectionOptions.Count > 0)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "Only a selection, submenu selection, indexed selection, or confirmation can define available choices."));
        }

        if (selectionOptions.Count > 100)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "A selection, submenu selection, indexed selection, or confirmation can define at most 100 available choices."));
        }

        if (node.ControlType == MenuControlType.IndexedSelection
            && !HasConsecutiveSlider(definition, node))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "An indexed selection must be followed immediately by at least one slider under the same parent."));
        }

        var uniqueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < selectionOptions.Count; index++)
        {
            var optionLocation = $"{location} option {index + 1}";
            ValidateRequired(optionLocation, "value", selectionOptions[index], errors);
            if (selectionOptions[index].Length > 100)
            {
                errors.Add(new MenuDefinitionValidationError(
                    optionLocation,
                    "A control choice cannot exceed 100 characters."));
            }

            if (!uniqueOptions.Add(selectionOptions[index]))
            {
                errors.Add(new MenuDefinitionValidationError(
                    optionLocation,
                    $"{node.ControlType} option '{selectionOptions[index]}' is duplicated."));
            }
        }

        if (node.Disabled && node.DisabledWhen is { Count: > 0 })
        {
            errors.Add(new MenuDefinitionValidationError(
                $"node '{node.Id}'",
                "A permanently disabled item cannot also define disabledWhen; remove the redundant conditional rule."));
        }

        ValidateValueConditions(
            definition,
            node,
            (node.DisabledWhen ?? []).Select(condition => (
                condition.SourceId,
                condition.EqualsValue,
                condition.SourceKind)),
            "disabledWhen",
            "disabled",
            errors);
        ValidateValueConditions(
            definition,
            node,
            (node.HiddenWhen ?? []).Select(condition => (
                condition.SourceId,
                condition.EqualsValue,
                condition.SourceKind)),
            "hiddenWhen",
            "hidden",
            errors);
    }

    private static void ValidateValueConditions(
        MenuDefinition definition,
        MenuNode node,
        IEnumerable<(
            string SourceId,
            string EqualsValue,
            MenuConditionSourceKind SourceKind)> source,
        string fieldName,
        string behavior,
        ICollection<MenuDefinitionValidationError> errors)
    {
        var conditions = source.ToArray();
        var location = $"node '{node.Id}'";
        if (conditions.Length > 20)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"A menu item can define at most 20 {behavior} conditions."));
        }

        var uniqueConditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in conditions)
        {
            var conditionLocation = $"{location} {fieldName}";
            ValidateIdentifier(conditionLocation, condition.SourceId, errors);
            ValidateRequired(conditionLocation, "equals", condition.EqualsValue, errors);
            if (!uniqueConditions.Add(
                    $"{condition.SourceKind}\u001f{condition.SourceId}\u001f{condition.EqualsValue}"))
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"Condition '{condition.SourceId} = {condition.EqualsValue}' is duplicated."));
            }

            if (condition.SourceKind == MenuConditionSourceKind.ExternalState)
            {
                if (!definition.ExternalStates.TryGetValue(condition.SourceId, out var externalState))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        conditionLocation,
                        $"External state '{condition.SourceId}' does not exist."));
                }
                else if (!externalState.Options.Any(option => option.Equals(
                             condition.EqualsValue,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add(new MenuDefinitionValidationError(
                        conditionLocation,
                        $"Value '{condition.EqualsValue}' is not an available option for external state '{condition.SourceId}'."));
                }

                continue;
            }

            if (!definition.Nodes.TryGetValue(condition.SourceId, out var setting))
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"Setting node '{condition.SourceId}' does not exist."));
            }
            else if (setting.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"A menu item cannot make itself {behavior} based on its own value."));
            }
            else if (setting.ControlType is MenuControlType.Submenu
                     or MenuControlType.Confirmation
                     or MenuControlType.Action)
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"Setting node '{condition.SourceId}' must be a slider, selection, submenu selection, indexed selection, or switch."));
            }
            else if (setting.ControlType is MenuControlType.Selection
                         or MenuControlType.SubmenuSelection
                         or MenuControlType.IndexedSelection
                     && !(setting.SelectionOptions ?? []).Any(option => option.Equals(
                         condition.EqualsValue,
                         StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"Value '{condition.EqualsValue}' is not an available option for selection '{condition.SourceId}'."));
            }
            else if (setting.ControlType == MenuControlType.Switch
                     && !condition.EqualsValue.Equals("on", StringComparison.OrdinalIgnoreCase)
                     && !condition.EqualsValue.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new MenuDefinitionValidationError(
                    conditionLocation,
                    $"Value '{condition.EqualsValue}' for switch '{condition.SourceId}' must be 'on' or 'off'."));
            }
        }
    }

    private static void ValidateExternalState(
        MenuExternalState state,
        ICollection<MenuDefinitionValidationError> errors)
    {
        var location = $"external state '{state.Id}'";
        ValidateIdentifier(location, state.Id, errors);
        ValidateRequired(location, "label", state.Label, errors);
        ValidateRequired(location, "defaultValue", state.DefaultValue, errors);
        if (state.Options.Count == 0)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "An external state must define at least one option."));
            return;
        }

        if (state.Options.Count > 100)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "An external state can define at most 100 options."));
        }

        var uniqueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < state.Options.Count; index++)
        {
            var option = state.Options[index];
            ValidateRequired($"{location} option {index + 1}", "value", option, errors);
            if (!uniqueOptions.Add(option))
            {
                errors.Add(new MenuDefinitionValidationError(
                    $"{location} option {index + 1}",
                    $"External-state option '{option}' is duplicated."));
            }
        }

        if (!state.Options.Any(option => option.Equals(
                state.DefaultValue,
                StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"Default value '{state.DefaultValue}' must match an available option."));
        }
    }

    private static string FormatChoiceControlName(MenuControlType controlType) => controlType switch
    {
        MenuControlType.Selection => "Selection",
        MenuControlType.SubmenuSelection => "Submenu selection",
        MenuControlType.IndexedSelection => "Indexed selection",
        _ => controlType.ToString()
    };

    private static bool HasConsecutiveSlider(MenuDefinition definition, MenuNode selector)
    {
        var nodes = definition.Nodes.Values.ToArray();
        var index = Array.FindIndex(nodes, node => node.Id.Equals(
            selector.Id,
            StringComparison.OrdinalIgnoreCase));
        return index >= 0
               && index + 1 < nodes.Length
               && nodes[index + 1].ParentId?.Equals(
                   selector.ParentId,
                   StringComparison.OrdinalIgnoreCase) == true
               && nodes[index + 1].ControlType == MenuControlType.Slider;
    }

    private static void ValidateConfigurationReference(
        MenuDefinition definition,
        string? configurationId,
        string location,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (!string.IsNullOrWhiteSpace(configurationId)
            && !definition.Configurations.ContainsKey(configurationId))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"Configuration '{configurationId}' does not exist."));
        }
    }

    private static void ValidateTiming(
        MenuTimingProfile timing,
        ICollection<MenuDefinitionValidationError> errors)
    {
        ValidateTimingValue("defaultDelay", timing.DefaultDelayMilliseconds, errors);
        ValidateTimingValue("screenChangeDelay", timing.ScreenChangeDelayMilliseconds, errors);
        ValidateTimingValue("returnDelay", timing.ReturnDelayMilliseconds, errors);
        ValidateTimingValue("adjustmentDelay", timing.AdjustmentDelayMilliseconds, errors);
    }

    private static void ValidateTimingValue(
        string name,
        int milliseconds,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (milliseconds is < 50 or > 30_000)
        {
            errors.Add(new MenuDefinitionValidationError(
                $"timing.{name}",
                "System timing must be between 50 and 30000 milliseconds."));
        }
    }

    private static void ValidateReturnStrategy(
        MenuDefinition definition,
        MenuAnchor anchor,
        string anchorLocation,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (anchor.ReturnStrategy is not { } strategy)
        {
            return;
        }

        var location = $"{anchorLocation} returnStrategy";
        if (!definition.Nodes.ContainsKey(strategy.MenuRootNodeId))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"Menu root node '{strategy.MenuRootNodeId}' does not exist."));
        }
        else if (strategy.MenuRootNodeId.Equals(
                     anchor.TargetNodeId,
                     StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "The menu root and return target must be different nodes."));
        }

        ValidateOperations(
            $"{location} atMenuRoot",
            strategy.AtMenuRoot.Operations,
            errors);
        ValidateOperations(
            $"{location} belowMenuRoot",
            strategy.BelowMenuRoot.Operations,
            errors);

        var overrideNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in strategy.NodeOverrides ?? [])
        {
            var overrideLocation = $"{location} override '{item.NodeId}'";
            if (!overrideNodeIds.Add(item.NodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    overrideLocation,
                    $"Only one return override can be defined for node '{item.NodeId}'."));
            }

            if (!definition.Nodes.ContainsKey(item.NodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    overrideLocation,
                    $"Menu node '{item.NodeId}' does not exist."));
            }

            ValidateOperations(overrideLocation, item.Script.Operations, errors);
        }
    }

    public void ValidateAndThrow(MenuDefinition definition)
    {
        var errors = Validate(definition);
        if (errors.Count > 0)
        {
            throw new MenuDefinitionValidationException(errors);
        }
    }

    private static void ValidateVerification(
        MenuVerificationManifest? verification,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (verification is null)
        {
            return;
        }

        ValidateRequired("verification display", "model", verification.Display.Model, errors);
        ValidateRequired("verification display", "firmware", verification.Display.Firmware, errors);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in verification.Checks)
        {
            var location = $"verification check '{check.Id}'";
            ValidateRequired(location, "id", check.Id, errors);
            if (!ids.Add(check.Id))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "A verification check ID can appear only once."));
            }

            if (check.Fingerprint.Length != 64
                || !check.Fingerprint.All(Uri.IsHexDigit))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "fingerprint must be a 64-character SHA-256 hexadecimal value."));
            }

            if (check.VerifiedAtUtc == default)
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    "verifiedAt must be a valid timestamp."));
            }
        }
    }

    private static void ValidateOperations(
        string location,
        IReadOnlyList<MenuOperation> operations,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (operations.Count == 0)
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "At least one key step is required."));
            return;
        }

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var stepLocation = $"{location}, step {index + 1}";
            ValidateRequired(stepLocation, "key", operation.Key, errors);
            if (!Enum.IsDefined(operation.Action))
            {
                errors.Add(new MenuDefinitionValidationError(
                    stepLocation,
                    "Action must be Click, Press, or Release."));
            }

            if (operation.Repeat is < 1 or > MaximumRepeat)
            {
                errors.Add(new MenuDefinitionValidationError(
                    stepLocation,
                    $"Repeat must be between 1 and {MaximumRepeat}."));
            }

            if (operation.DelayAfter is { } delay
                && (delay <= TimeSpan.Zero || delay > MaximumDelay))
            {
                errors.Add(new MenuDefinitionValidationError(
                    stepLocation,
                    $"Delay must be greater than zero and no more than {MaximumDelay.TotalSeconds:0} seconds."));
            }
        }
    }

    private static void DetectParentCycles(
        MenuDefinition definition,
        ICollection<MenuDefinitionValidationError> errors)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes.Values)
        {
            if (completed.Contains(node.Id))
            {
                continue;
            }

            var path = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var current = node;
            while (true)
            {
                if (positions.TryGetValue(current.Id, out var cycleStart))
                {
                    var cycle = path.Skip(cycleStart).Append(current.Id);
                    errors.Add(new MenuDefinitionValidationError(
                        $"node '{current.Id}'",
                        $"Parent cycle detected: {string.Join(" -> ", cycle)}."));
                    break;
                }

                if (completed.Contains(current.Id))
                {
                    break;
                }

                positions[current.Id] = path.Count;
                path.Add(current.Id);
                if (string.IsNullOrWhiteSpace(current.ParentId)
                    || !definition.Nodes.TryGetValue(current.ParentId, out current))
                {
                    break;
                }
            }

            foreach (var visited in path)
            {
                completed.Add(visited);
            }
        }
    }

    private static void ValidateIdentifier(
        string location,
        string value,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !(char.IsLetter(value[0]) || value[0] == '_')
            || !value.All(character =>
                char.IsLetterOrDigit(character) || character is '_' or '-' or '.'))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                "Identifiers must begin with a letter or underscore and contain only letters, numbers, underscores, periods, or hyphens."));
        }
    }

    private static void ValidateRequired(
        string location,
        string field,
        string value,
        ICollection<MenuDefinitionValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new MenuDefinitionValidationError(
                location,
                $"{field} cannot be empty."));
        }
    }
}
