using SamsungController.Automation.Navigation;

namespace SamsungController.Cli;

internal sealed class MenuSchemaCommandService
{
    private readonly Action<string> _writeLine;
    private readonly MenuDefinitionSchemaInspector _inspector = new();

    public MenuSchemaCommandService(Action<string> writeLine)
    {
        _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
    }

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count > 0
            && !arguments[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Usage: samsungctl menu validate [file-or-directory]");
        }

        if (arguments.Count > 2)
        {
            throw new ArgumentException(
                "Usage: samsungctl menu validate [file-or-directory]");
        }

        var requestedPath = arguments.Count == 2
            ? arguments[1]
            : Path.Combine(Directory.GetCurrentDirectory(), "menu-definitions");
        var fullPath = Path.GetFullPath(requestedPath);
        var results = await _inspector.InspectPathAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (results.Count == 0)
        {
            _writeLine($"No YAML or JSON menu definitions were found in {fullPath}.");
            return 2;
        }

        foreach (var result in results)
        {
            Print(result);
        }

        var validCount = results.Count(result => result.IsValid);
        var invalidCount = results.Count - validCount;
        _writeLine(
            $"Schema check complete: {validCount} valid, {invalidCount} invalid, {results.Count} total.");
        return invalidCount == 0 ? 0 : 2;
    }

    private void Print(MenuDefinitionSchemaInspection result)
    {
        _writeLine($"[{(result.IsValid ? "VALID" : "INVALID")}] {result.Path}");
        _writeLine($"  Format: {MenuDefinitionFileFormats.Label(result.Format)}");
        if (result.IsValid)
        {
            _writeLine($"  Definition: {result.Name} ({result.Id}) · {result.Model}");
            _writeLine(
                $"  Topology: {result.NodeCount} nodes, {result.AnchorCount} anchors, "
                + $"{result.ExplicitTransitionCount} seed routes, {result.GeneratedTransitionCount} calculated routes, "
                + $"{result.ConfigurationCount} configurations");
            _writeLine(
                $"  File evidence: {result.CurrentVerificationCheckCount}/{result.VerificationCheckCount} planned checks "
                + "(local sidecar not included)");
        }

        foreach (var warning in result.Warnings)
        {
            _writeLine($"  Warning: {warning}");
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            _writeLine($"  Error: {diagnostic}");
        }
    }
}
