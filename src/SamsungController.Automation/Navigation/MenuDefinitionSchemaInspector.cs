namespace SamsungController.Automation.Navigation;

public sealed record MenuDefinitionSchemaInspection(
    string Path,
    MenuDefinitionFileFormat Format,
    bool IsValid,
    string? Id,
    string? Name,
    string? Model,
    MenuDefinitionContext? Context,
    int NodeCount,
    int AnchorCount,
    int ExplicitTransitionCount,
    int GeneratedTransitionCount,
    int ConfigurationCount,
    int VerificationCheckCount,
    int CurrentVerificationCheckCount,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Parses, regenerates, and validates menu-definition files using the same
/// effective topology that the application will navigate.
/// </summary>
public sealed class MenuDefinitionSchemaInspector
{
    public async Task<IReadOnlyList<MenuDefinitionSchemaInspection>> InspectPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return [await InspectFileAsync(fullPath, cancellationToken).ConfigureAwait(false)];
        }

        if (!Directory.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Menu definition file or directory was not found: {fullPath}",
                fullPath);
        }

        var files = Directory.EnumerateFiles(
                fullPath,
                "*",
                SearchOption.AllDirectories)
            .Where(IsMenuDefinitionFile)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var inspections = new List<MenuDefinitionSchemaInspection>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inspections.Add(await InspectFileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return inspections;
    }

    public async Task<MenuDefinitionSchemaInspection> InspectFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var source = await File.ReadAllTextAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var format = MenuDefinitionFileFormats.DetectForRead(fullPath, source);
        try
        {
            var parsed = format == MenuDefinitionFileFormat.Json
                ? new MenuDefinitionJsonSerializer().Parse(source)
                : new MenuDefinitionParser().Parse(source);
            var effective = TopologyRouteGenerator.Regenerate(parsed);
            try
            {
                new MenuDefinitionValidator().ValidateAndThrow(effective);
            }
            catch (MenuDefinitionValidationException exception)
            {
                throw format == MenuDefinitionFileFormat.Json
                    ? MenuDefinitionSourceDiagnostics.AddJsonLocations(source, exception)
                    : exception;
            }

            var plan = MenuDefinitionVerificationPlanner.Create(effective);
            var currentChecks = plan.Checks.Count(check =>
                check.ExistingEvidenceReady
                || MenuDefinitionVerificationPlanner.IsCurrent(effective, check));
            var warnings = CreateWarnings(parsed, effective);
            return new MenuDefinitionSchemaInspection(
                fullPath,
                format,
                true,
                effective.Id,
                effective.Name,
                effective.Model,
                effective.Context,
                effective.Nodes.Count,
                effective.Anchors.Count,
                effective.Transitions.Values.Count(item => !item.GeneratedFromTopology),
                effective.Transitions.Values.Count(item => item.GeneratedFromTopology),
                effective.Configurations.Count,
                plan.Checks.Count,
                currentChecks,
                [],
                warnings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MenuDefinitionSchemaInspection(
                fullPath,
                format,
                false,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                CreateDiagnostics(exception),
                []);
        }
    }

    private static bool IsMenuDefinitionFile(string path) =>
        Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> CreateDiagnostics(Exception exception) =>
        exception is MenuDefinitionValidationException validation
            ? validation.Errors.Select(error => error.ToString()).ToArray()
            : [exception.Message];

    private static IReadOnlyList<string> CreateWarnings(
        MenuDefinition source,
        MenuDefinition effective)
    {
        var warnings = new List<string>();
        var storedGenerated = source.Transitions.Values
            .Where(item => item.GeneratedFromTopology)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(TransitionShape)
            .ToArray();
        var effectiveGenerated = effective.Transitions.Values
            .Where(item => item.GeneratedFromTopology)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(TransitionShape)
            .ToArray();
        if (!storedGenerated.SequenceEqual(effectiveGenerated, StringComparer.Ordinal))
        {
            warnings.Add(
                $"Generated routes will be refreshed when loaded: {storedGenerated.Length} stored, {effectiveGenerated.Length} calculated.");
        }

        if (effective.Anchors.Count == 0)
        {
            warnings.Add(
                "No anchor is defined. The application cannot establish a known starting location from normal video.");
        }

        if (effective.Transitions.Values.All(item => item.GeneratedFromTopology)
            && effective.Nodes.Count > 1)
        {
            warnings.Add(
                "No explicit seed traversal is defined. Topology routes cannot be calculated until at least one branch traversal is recorded.");
        }

        return warnings;
    }

    private static string TransitionShape(MenuTransition transition) =>
        string.Join(
            '|',
            transition.Id,
            transition.FromNodeId,
            transition.ToNodeId,
            transition.ConfigurationId ?? string.Empty,
            transition.TopologySeedTransitionId ?? string.Empty,
            transition.ValidationGroupId ?? string.Empty,
            transition.IsValidationRoute,
            string.Join(
                ';',
                transition.Operations.Select(operation =>
                    $"{operation.Key},{operation.Action},{operation.Repeat},{operation.DelayAfter?.Ticks ?? 0}")));
}
