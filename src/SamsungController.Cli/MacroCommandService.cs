using SamsungController.Automation.Macros;
using SamsungController.Core.Devices;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli;

internal interface IMacroCommandService
{
    Task ExecuteAsync(string arguments, CancellationToken cancellationToken = default);
}

internal sealed class MacroCommandService : IMacroCommandService
{
    private readonly string _macroFilePath;
    private readonly IMacroCommandTarget? _target;
    private readonly Action<string> _writeLine;
    private readonly MacroParser _parser = new();
    private readonly MacroValidator _validator = new();

    public MacroCommandService(
        string macroFilePath,
        IMacroCommandTarget? target,
        Action<string> writeLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroFilePath);
        _macroFilePath = Path.GetFullPath(macroFilePath);
        _target = target;
        _writeLine = writeLine ?? throw new ArgumentNullException(nameof(writeLine));
    }

    public async Task ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var parts = arguments.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            PrintUsage();
            return;
        }

        if (parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 1)
            {
                PrintUsage();
                return;
            }

            var catalog = await LoadValidatedAsync(cancellationToken).ConfigureAwait(false);
            foreach (var macro in catalog.Macros.Values.OrderBy(
                         macro => macro.Name,
                         StringComparer.OrdinalIgnoreCase))
            {
                _writeLine(
                    macro.Description is null
                        ? macro.Name
                        : $"{macro.Name} - {macro.Description}");
            }

            _writeLine($"{catalog.Macros.Count} macro(s) in {_macroFilePath}.");
            return;
        }

        if (parts[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length != 1)
            {
                PrintUsage();
                return;
            }

            var catalog = await LoadValidatedAsync(cancellationToken).ConfigureAwait(false);
            _writeLine(
                $"Validated {catalog.Macros.Count} macro(s) in {_macroFilePath}.");
            return;
        }

        var macroName = parts[0].Equals("run", StringComparison.OrdinalIgnoreCase)
            ? parts.ElementAtOrDefault(1)
            : parts[0];
        var expectedPartCount = parts[0].Equals("run", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (macroName is null || parts.Length != expectedPartCount)
        {
            PrintUsage();
            return;
        }

        var runCatalog = await LoadValidatedAsync(cancellationToken).ConfigureAwait(false);
        runCatalog.GetRequiredMacro(macroName);
        if (_target is null)
        {
            throw new InvalidOperationException(
                "Running a macro requires an active Samsung TV connection.");
        }

        var executor = new MacroExecutor(_target, validator: _validator);
        executor.ProgressChanged += (_, eventArgs) =>
        {
            var progress = eventArgs.Progress;
            _writeLine(
                $"[{progress.OperationNumber}/{progress.OperationCount}] "
                + $"{progress.SourceMacroName} step {progress.SourceStepNumber}: "
                + progress.Description);
        };

        _writeLine($"Running macro {macroName} from {_macroFilePath}.");
        var result = await executor.ExecuteAsync(runCatalog, macroName, cancellationToken)
            .ConfigureAwait(false);
        _writeLine(
            $"Completed {result.MacroName}: {result.KeysSent} key(s), "
            + $"{result.OperationCount} operation(s), {result.Elapsed.TotalSeconds:0.###}s.");
    }

    private async Task<MacroCatalog> LoadValidatedAsync(CancellationToken cancellationToken)
    {
        var catalog = await _parser.ParseFileAsync(_macroFilePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        _validator.ValidateAndThrow(catalog);
        return catalog;
    }

    private void PrintUsage() =>
        _writeLine("Usage: macro <name> | macro run <name> | macro list | macro validate");
}

internal sealed class SamsungTvMacroCommandTarget(SamsungTvClient client) : IMacroCommandTarget
{
    private readonly SamsungTvClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public Task SendKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken = default) =>
        _client.SendKeyAsync(key, action, cancellationToken);
}
