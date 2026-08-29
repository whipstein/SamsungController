using System.Diagnostics;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Macros;

public interface IMacroCommandTarget
{
    Task SendKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken = default);
}

public interface IMacroMenuCommandTarget : IMacroCommandTarget
{
    void ValidateMenuDestination(string targetNodeId);

    Task NavigateToMenuDestinationAsync(
        string targetNodeId,
        CancellationToken cancellationToken = default);
}

public interface IMacroDelay
{
    Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}

public sealed class SystemMacroDelay : IMacroDelay
{
    public static SystemMacroDelay Instance { get; } = new();

    private SystemMacroDelay()
    {
    }

    public Task DelayAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        Task.Delay(duration, cancellationToken);
}

public enum MacroOperationKind
{
    Key,
    Delay,
    Menu
}

public sealed record MacroExecutionProgress(
    Guid ExecutionId,
    DateTimeOffset Timestamp,
    string RootMacroName,
    string SourceMacroName,
    int SourceStepNumber,
    int NestingDepth,
    int OperationNumber,
    int OperationCount,
    MacroOperationKind Kind,
    string Description,
    string? Key,
    RemoteKeyAction? Action,
    TimeSpan? Delay);

public sealed class MacroExecutionProgressEventArgs(MacroExecutionProgress progress) : EventArgs
{
    public MacroExecutionProgress Progress { get; } =
        progress ?? throw new ArgumentNullException(nameof(progress));
}

public sealed record MacroExecutionResult(
    Guid ExecutionId,
    string MacroName,
    int OperationCount,
    int KeysSent,
    TimeSpan Elapsed);

public sealed class MacroExecutor
{
    private readonly IMacroCommandTarget _target;
    private readonly IMacroDelay _delay;
    private readonly MacroValidator _validator;

    public MacroExecutor(
        IMacroCommandTarget target,
        IMacroDelay? delay = null,
        MacroValidator? validator = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _delay = delay ?? SystemMacroDelay.Instance;
        _validator = validator ?? new MacroValidator();
    }

    public event EventHandler<MacroExecutionProgressEventArgs>? ProgressChanged;

    public async Task<MacroExecutionResult> ExecuteAsync(
        MacroCatalog catalog,
        string macroName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        _validator.ValidateAndThrow(catalog);

        var root = catalog.GetRequiredMacro(macroName);
        var operations = BuildPlan(catalog, root);
        var menuTarget = PrepareMenuTarget(operations);
        var executionId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var keysSent = 0;
        var heldKeys = new HashSet<string>(StringComparer.Ordinal);
        var completed = false;

        try
        {
            for (var index = 0; index < operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = operations[index];
                var keyOperation = operation as KeyOperation;
                var delayOperation = operation as DelayOperation;
                PublishProgress(new MacroExecutionProgress(
                    executionId,
                    DateTimeOffset.UtcNow,
                    root.Name,
                    operation.SourceMacroName,
                    operation.SourceStepNumber,
                    operation.NestingDepth,
                    index + 1,
                    operations.Count,
                    operation.Kind,
                    operation.Description,
                    keyOperation?.Key,
                    keyOperation?.Action,
                    delayOperation?.Duration));

                switch (operation)
                {
                    case KeyOperation key:
                        if (key.Action == RemoteKeyAction.Press)
                        {
                            heldKeys.Add(key.Key);
                        }

                        await _target.SendKeyAsync(key.Key, key.Action, cancellationToken)
                            .ConfigureAwait(false);
                        if (key.Action == RemoteKeyAction.Release)
                        {
                            heldKeys.Remove(key.Key);
                        }

                        keysSent++;
                        break;

                    case DelayOperation delay:
                        await _delay.DelayAsync(delay.Duration, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case MenuOperation menu:
                        await menuTarget!.NavigateToMenuDestinationAsync(
                                menu.TargetNodeId,
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }

            completed = true;
        }
        finally
        {
            if (!completed && heldKeys.Count > 0)
            {
                await ReleaseHeldKeysAsync(heldKeys).ConfigureAwait(false);
            }
        }

        stopwatch.Stop();
        return new MacroExecutionResult(
            executionId,
            root.Name,
            operations.Count,
            keysSent,
            stopwatch.Elapsed);
    }

    private async Task ReleaseHeldKeysAsync(IEnumerable<string> heldKeys)
    {
        using var cleanupSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        foreach (var key in heldKeys)
        {
            try
            {
                await _target.SendKeyAsync(
                        key,
                        RemoteKeyAction.Release,
                        cleanupSource.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Cancellation/failure cleanup is best effort and cannot replace the original error.
            }
        }
    }

    private static IReadOnlyList<MacroOperation> BuildPlan(
        MacroCatalog catalog,
        MacroDefinition root)
    {
        var operations = new List<MacroOperation>();
        Expand(root, nestingDepth: 0);
        return operations;

        void Expand(MacroDefinition macro, int nestingDepth)
        {
            for (var stepIndex = 0; stepIndex < macro.Steps.Count; stepIndex++)
            {
                var stepNumber = stepIndex + 1;
                switch (macro.Steps[stepIndex])
                {
                    case KeyStep key:
                        for (var repetition = 0; repetition < key.Repeat; repetition++)
                        {
                            operations.Add(new KeyOperation(
                                macro.Name,
                                stepNumber,
                                nestingDepth,
                                key.Key.Trim(),
                                key.Action));
                            if (key.Delay is { } keyDelay)
                            {
                                operations.Add(new DelayOperation(
                                    macro.Name,
                                    stepNumber,
                                    nestingDepth,
                                    keyDelay));
                            }
                        }

                        break;

                    case DelayStep delay:
                        operations.Add(new DelayOperation(
                            macro.Name,
                            stepNumber,
                            nestingDepth,
                            delay.Duration));
                        break;

                    case CallMacroStep call:
                        var called = catalog.GetRequiredMacro(call.MacroName);
                        for (var repetition = 0; repetition < call.Repeat; repetition++)
                        {
                            Expand(called, nestingDepth + 1);
                        }

                        break;

                    case MenuStep menu:
                        operations.Add(new MenuOperation(
                            macro.Name,
                            stepNumber,
                            nestingDepth,
                            menu.TargetNodeId.Trim()));
                        break;
                }
            }
        }
    }

    private IMacroMenuCommandTarget? PrepareMenuTarget(
        IReadOnlyList<MacroOperation> operations)
    {
        var menuOperations = operations.OfType<MenuOperation>().ToArray();
        if (menuOperations.Length == 0)
        {
            return null;
        }

        if (_target is not IMacroMenuCommandTarget menuTarget)
        {
            throw new InvalidOperationException(
                "This macro calls a verified menu destination, but the current command target has no menu-navigation context. Run it from the SamsungController web interface.");
        }

        foreach (var targetNodeId in menuOperations
                     .Select(operation => operation.TargetNodeId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            menuTarget.ValidateMenuDestination(targetNodeId);
        }

        return menuTarget;
    }

    private void PublishProgress(MacroExecutionProgress progress)
    {
        try
        {
            ProgressChanged?.Invoke(this, new MacroExecutionProgressEventArgs(progress));
        }
        catch
        {
            // Diagnostic consumers cannot interrupt macro execution.
        }
    }

    private abstract record MacroOperation(
        string SourceMacroName,
        int SourceStepNumber,
        int NestingDepth,
        MacroOperationKind Kind,
        string Description);

    private sealed record KeyOperation(
        string SourceMacroName,
        int SourceStepNumber,
        int NestingDepth,
        string Key,
        RemoteKeyAction Action) : MacroOperation(
            SourceMacroName,
            SourceStepNumber,
            NestingDepth,
            MacroOperationKind.Key,
            $"{Action} {Key}");

    private sealed record DelayOperation(
        string SourceMacroName,
        int SourceStepNumber,
        int NestingDepth,
        TimeSpan Duration) : MacroOperation(
            SourceMacroName,
            SourceStepNumber,
            NestingDepth,
            MacroOperationKind.Delay,
            $"Delay {FormatDuration(Duration)}");

    private sealed record MenuOperation(
        string SourceMacroName,
        int SourceStepNumber,
        int NestingDepth,
        string TargetNodeId) : MacroOperation(
            SourceMacroName,
            SourceStepNumber,
            NestingDepth,
            MacroOperationKind.Menu,
            $"Navigate to verified menu destination {TargetNodeId}");

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMilliseconds < 1000
            ? $"{duration.TotalMilliseconds:0.###}ms"
            : $"{duration.TotalSeconds:0.###}s";
}
