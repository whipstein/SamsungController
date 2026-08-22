namespace SamsungController.Automation.Macros;

public sealed record MacroValidationError(
    string MacroName,
    int? StepNumber,
    string Message)
{
    public override string ToString() =>
        StepNumber is null
            ? $"{MacroName}: {Message}"
            : $"{MacroName}, step {StepNumber}: {Message}";
}

public sealed class MacroValidationException : Exception
{
    public MacroValidationException(IReadOnlyList<MacroValidationError> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<MacroValidationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<MacroValidationError> errors) =>
        errors.Count == 0
            ? "Macro validation failed."
            : "Macro validation failed:" + Environment.NewLine
              + string.Join(Environment.NewLine, errors.Select(error => $"- {error}"));
}

public sealed class MacroValidator
{
    public const int MaximumRepeat = 1000;
    public const int MaximumExpandedOperations = 10_000;
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromHours(24);

    public IReadOnlyList<MacroValidationError> Validate(MacroCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<MacroValidationError>();

        if (catalog.Macros.Count == 0)
        {
            errors.Add(new MacroValidationError("macros", null, "At least one macro is required."));
            return errors;
        }

        foreach (var macro in catalog.Macros.Values)
        {
            ValidateDefinition(catalog, macro, errors);
        }

        DetectCallCycles(catalog, errors);
        if (!errors.Any(error =>
                error.Message.Contains("does not exist", StringComparison.Ordinal)
                || error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase)))
        {
            ValidateExpandedOperationCounts(catalog, errors);
        }

        return errors;
    }

    public void ValidateAndThrow(MacroCatalog catalog)
    {
        var errors = Validate(catalog);
        if (errors.Count > 0)
        {
            throw new MacroValidationException(errors);
        }
    }

    private static void ValidateDefinition(
        MacroCatalog catalog,
        MacroDefinition macro,
        ICollection<MacroValidationError> errors)
    {
        if (!IsIdentifier(macro.Name))
        {
            errors.Add(new MacroValidationError(
                macro.Name,
                null,
                "Use a name beginning with a letter or underscore and containing only letters, numbers, underscores, periods, or hyphens."));
        }

        if (macro.Steps.Count == 0)
        {
            errors.Add(new MacroValidationError(macro.Name, null, "The macro has no steps."));
        }

        for (var index = 0; index < macro.Steps.Count; index++)
        {
            var stepNumber = index + 1;
            switch (macro.Steps[index])
            {
                case KeyStep key:
                    if (string.IsNullOrWhiteSpace(key.Key))
                    {
                        errors.Add(new MacroValidationError(
                            macro.Name,
                            stepNumber,
                            "The Samsung key cannot be empty."));
                    }

                    if (!Enum.IsDefined(key.Action))
                    {
                        errors.Add(new MacroValidationError(
                            macro.Name,
                            stepNumber,
                            "The action must be Click, Press, or Release."));
                    }

                    ValidateRepeat(macro.Name, stepNumber, key.Repeat, errors);
                    if (key.Delay is { } keyDelay)
                    {
                        ValidateDelay(macro.Name, stepNumber, keyDelay, errors);
                    }

                    break;

                case DelayStep delay:
                    ValidateDelay(macro.Name, stepNumber, delay.Duration, errors);
                    break;

                case CallMacroStep call:
                    ValidateRepeat(macro.Name, stepNumber, call.Repeat, errors);
                    if (string.IsNullOrWhiteSpace(call.MacroName))
                    {
                        errors.Add(new MacroValidationError(
                            macro.Name,
                            stepNumber,
                            "The called macro name cannot be empty."));
                    }
                    else if (!catalog.TryGetMacro(call.MacroName, out _))
                    {
                        errors.Add(new MacroValidationError(
                            macro.Name,
                            stepNumber,
                            $"Called macro '{call.MacroName}' does not exist."));
                    }

                    break;

                default:
                    errors.Add(new MacroValidationError(
                        macro.Name,
                        stepNumber,
                        $"Unsupported step type '{macro.Steps[index].GetType().Name}'."));
                    break;
            }
        }
    }

    private static void ValidateRepeat(
        string macroName,
        int stepNumber,
        int repeat,
        ICollection<MacroValidationError> errors)
    {
        if (repeat is < 1 or > MaximumRepeat)
        {
            errors.Add(new MacroValidationError(
                macroName,
                stepNumber,
                $"Repeat must be between 1 and {MaximumRepeat}."));
        }
    }

    private static void ValidateDelay(
        string macroName,
        int stepNumber,
        TimeSpan delay,
        ICollection<MacroValidationError> errors)
    {
        if (delay <= TimeSpan.Zero || delay > MaximumDelay)
        {
            errors.Add(new MacroValidationError(
                macroName,
                stepNumber,
                $"Delay must be greater than zero and no more than {MaximumDelay.TotalHours:0} hours."));
        }
    }

    private static void DetectCallCycles(
        MacroCatalog catalog,
        ICollection<MacroValidationError> errors)
    {
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var macro in catalog.Macros.Values)
        {
            if (!states.ContainsKey(macro.Name))
            {
                Visit(macro);
            }
        }

        void Visit(MacroDefinition macro)
        {
            states[macro.Name] = 1;
            path.Add(macro.Name);
            for (var index = 0; index < macro.Steps.Count; index++)
            {
                if (macro.Steps[index] is not CallMacroStep call
                    || !catalog.TryGetMacro(call.MacroName, out var called)
                    || called is null)
                {
                    continue;
                }

                if (!states.TryGetValue(called.Name, out var state))
                {
                    Visit(called);
                    continue;
                }

                if (state == 1)
                {
                    var cycleStart = path.FindIndex(
                        name => name.Equals(called.Name, StringComparison.OrdinalIgnoreCase));
                    var cycle = path.Skip(cycleStart).Append(called.Name);
                    errors.Add(new MacroValidationError(
                        macro.Name,
                        index + 1,
                        $"Macro call cycle detected: {string.Join(" -> ", cycle)}."));
                }
            }

            path.RemoveAt(path.Count - 1);
            states[macro.Name] = 2;
        }
    }

    private static void ValidateExpandedOperationCounts(
        MacroCatalog catalog,
        ICollection<MacroValidationError> errors)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var macro in catalog.Macros.Values)
        {
            var count = Count(macro);
            if (count > MaximumExpandedOperations)
            {
                errors.Add(new MacroValidationError(
                    macro.Name,
                    null,
                    $"The expanded macro has more than {MaximumExpandedOperations:N0} key/delay operations."));
            }
        }

        long Count(MacroDefinition macro)
        {
            if (counts.TryGetValue(macro.Name, out var cached))
            {
                return cached;
            }

            long count = 0;
            foreach (var step in macro.Steps)
            {
                var increment = step switch
                {
                    KeyStep key => (long)key.Repeat * (key.Delay is null ? 1 : 2),
                    DelayStep => 1,
                    CallMacroStep call when catalog.TryGetMacro(call.MacroName, out var called)
                        && called is not null => (long)call.Repeat * Count(called),
                    _ => 0
                };
                count = Math.Min(MaximumExpandedOperations + 1L, count + increment);
            }

            counts[macro.Name] = count;
            return count;
        }
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(character =>
            char.IsLetterOrDigit(character)
            || character is '_' or '-' or '.');
}
