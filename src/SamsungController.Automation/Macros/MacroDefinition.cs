using System.Collections.ObjectModel;

namespace SamsungController.Automation.Macros;

public sealed record MacroDefinition
{
    public MacroDefinition(
        string name,
        IEnumerable<MacroStep> steps,
        string? description = null,
        bool verified = false,
        int verificationPasses = 0)
    {
        Name = name;
        Steps = Array.AsReadOnly(
            steps?.ToArray() ?? throw new ArgumentNullException(nameof(steps)));
        Description = description;
        VerificationPasses = verified ? Math.Max(3, verificationPasses) : verificationPasses;
        Verified = verified || VerificationPasses >= 3;
    }

    public string Name { get; }

    public IReadOnlyList<MacroStep> Steps { get; }

    public string? Description { get; }

    public bool Verified { get; }

    public int VerificationPasses { get; }
}

public sealed class MacroCatalog
{
    private readonly IReadOnlyDictionary<string, MacroDefinition> _macros;

    public MacroCatalog(
        IEnumerable<MacroDefinition> macros,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(macros);

        var definitions = new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var macro in macros)
        {
            if (!definitions.TryAdd(macro.Name, macro))
            {
                throw new ArgumentException(
                    $"Macro names must be unique ignoring case: '{macro.Name}'.",
                    nameof(macros));
            }
        }

        _macros = new ReadOnlyDictionary<string, MacroDefinition>(definitions);
        Variables = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                variables ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, MacroDefinition> Macros => _macros;

    public IReadOnlyDictionary<string, string> Variables { get; }

    public bool TryGetMacro(string name, out MacroDefinition? macro) =>
        _macros.TryGetValue(name, out macro);

    public MacroDefinition GetRequiredMacro(string name)
    {
        if (TryGetMacro(name, out var macro) && macro is not null)
        {
            return macro;
        }

        throw new KeyNotFoundException($"Macro '{name}' was not found.");
    }
}
