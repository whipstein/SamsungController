using SamsungController.Automation.Macros;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MacroExecutorTests
{
    [Fact]
    public async Task ExecutesRequiredNavigationMacroWithNestingRepeatsAndDelays()
    {
        const string yaml =
            """
            variables:
              navigationDelay: 150ms
            macros:
              BackToVideo:
                - key: KEY_RETURN
                  repeat: 3
              TestNavigation:
                - call: BackToVideo
                - delay: 500ms
                - key: KEY_MENU
                - delay: 500ms
                - key: KEY_DOWN
                  repeat: 4
                  delay: ${navigationDelay}
                - key: KEY_ENTER
                  action: Press
            """;
        var catalog = new MacroParser().Parse(yaml);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var progress = new List<MacroExecutionProgress>();
        var executor = new MacroExecutor(target, delay);
        executor.ProgressChanged += (_, eventArgs) => progress.Add(eventArgs.Progress);

        var result = await executor.ExecuteAsync(catalog, "testnavigation");

        Assert.Equal(
            [
                ("KEY_RETURN", RemoteKeyAction.Click),
                ("KEY_RETURN", RemoteKeyAction.Click),
                ("KEY_RETURN", RemoteKeyAction.Click),
                ("KEY_MENU", RemoteKeyAction.Click),
                ("KEY_DOWN", RemoteKeyAction.Click),
                ("KEY_DOWN", RemoteKeyAction.Click),
                ("KEY_DOWN", RemoteKeyAction.Click),
                ("KEY_DOWN", RemoteKeyAction.Click),
                ("KEY_ENTER", RemoteKeyAction.Press)
            ],
            target.Commands);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(150)
            ],
            delay.Durations);
        Assert.Equal(15, result.OperationCount);
        Assert.Equal(9, result.KeysSent);
        Assert.Equal(15, progress.Count);
        Assert.All(progress.Take(3), item => Assert.Equal("BackToVideo", item.SourceMacroName));
        Assert.Equal(Enumerable.Range(1, 15), progress.Select(item => item.OperationNumber));
    }

    [Fact]
    public async Task CancellationInterruptsAnActiveDelayBeforeLaterKeys()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "Cancelable",
                [new DelayStep(TimeSpan.FromSeconds(10)), new KeyStep("KEY_MENU")])
        ]);
        var target = new RecordingTarget();
        var delay = new BlockingDelay();
        var executor = new MacroExecutor(target, delay);
        using var cancellationSource = new CancellationTokenSource();

        var execution = executor.ExecuteAsync(
            catalog,
            "Cancelable",
            cancellationSource.Token);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(target.Commands);
    }

    [Fact]
    public async Task CancellationBestEffortReleasesAKeyPressedByTheMacro()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "LongPress",
                [
                    new KeyStep("KEY_RIGHT", RemoteKeyAction.Press),
                    new DelayStep(TimeSpan.FromSeconds(10)),
                    new KeyStep("KEY_RIGHT", RemoteKeyAction.Release)
                ])
        ]);
        var target = new RecordingTarget();
        var delay = new BlockingDelay();
        var executor = new MacroExecutor(target, delay);
        using var cancellationSource = new CancellationTokenSource();

        var execution = executor.ExecuteAsync(catalog, "LongPress", cancellationSource.Token);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(
            [
                ("KEY_RIGHT", RemoteKeyAction.Press),
                ("KEY_RIGHT", RemoteKeyAction.Release)
            ],
            target.Commands);
    }

    [Fact]
    public async Task MenuDestinationsArePreflightedBeforeAnyKeyIsSent()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "WebOnly",
                [new KeyStep("KEY_HOME"), new MenuStep("settings")])
        ]);
        var target = new RecordingTarget();
        var executor = new MacroExecutor(target, new RecordingDelay());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(catalog, "WebOnly"));

        Assert.Contains("web interface", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(target.Commands);
    }

    [Fact]
    public async Task ExecutesMenuDestinationInMacroOperationOrder()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "Picture",
                [
                    new KeyStep("KEY_HOME"),
                    new MenuStep("picture-brightness"),
                    new KeyStep("KEY_ENTER")
                ])
        ]);
        var target = new RecordingMenuTarget();
        var progress = new List<MacroExecutionProgress>();
        var executor = new MacroExecutor(target, new RecordingDelay());
        executor.ProgressChanged += (_, eventArgs) => progress.Add(eventArgs.Progress);

        var result = await executor.ExecuteAsync(catalog, "Picture");

        Assert.Equal(
            ["key:KEY_HOME", "menu:picture-brightness", "key:KEY_ENTER"],
            target.Operations);
        Assert.Equal(["picture-brightness"], target.ValidatedDestinations);
        Assert.Equal(3, result.OperationCount);
        Assert.Equal(2, result.KeysSent);
        Assert.Equal(MacroOperationKind.Menu, progress[1].Kind);
    }

    private sealed class RecordingTarget : IMacroCommandTarget
    {
        public List<(string Key, RemoteKeyAction Action)> Commands { get; } = [];

        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add((key, action));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMenuTarget : IMacroMenuCommandTarget
    {
        public List<string> Operations { get; } = [];

        public List<string> ValidatedDestinations { get; } = [];

        public void ValidateMenuDestination(string targetNodeId) =>
            ValidatedDestinations.Add(targetNodeId);

        public Task NavigateToMenuDestinationAsync(
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"menu:{targetNodeId}");
            return Task.CompletedTask;
        }

        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"key:{key}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDelay : IMacroDelay
    {
        public List<TimeSpan> Durations { get; } = [];

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Durations.Add(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : IMacroDelay
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
