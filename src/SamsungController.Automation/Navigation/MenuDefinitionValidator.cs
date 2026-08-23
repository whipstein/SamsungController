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
        if (definition.Nodes.Count == 0)
        {
            errors.Add(new MenuDefinitionValidationError(
                "nodes",
                "At least one menu node is required."));
            return errors;
        }

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
        }

        DetectParentCycles(definition, errors);

        foreach (var anchor in definition.Anchors.Values)
        {
            var location = $"anchor '{anchor.Id}'";
            ValidateIdentifier(location, anchor.Id, errors);
            ValidateRequired(location, "label", anchor.Label, errors);
            if (!definition.Nodes.ContainsKey(anchor.TargetNodeId))
            {
                errors.Add(new MenuDefinitionValidationError(
                    location,
                    $"Target node '{anchor.TargetNodeId}' does not exist."));
            }

            ValidateOperations(location, anchor.Operations, errors);
        }

        foreach (var transition in definition.Transitions.Values)
        {
            var location = $"transition '{transition.Id}'";
            ValidateIdentifier(location, transition.Id, errors);
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

            ValidateOperations(location, transition.Operations, errors);
        }

        return errors;
    }

    public void ValidateAndThrow(MenuDefinition definition)
    {
        var errors = Validate(definition);
        if (errors.Count > 0)
        {
            throw new MenuDefinitionValidationException(errors);
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
