using SamsungController.Automation.Macros;
using SamsungController.Cli;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli.Tests;

public sealed class MacroCommandServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.MacroCliTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ListsAndValidatesMacroFilesWithoutConnectedTarget()
    {
        var path = await WriteMacroFileAsync();
        var output = new List<string>();
        var service = new MacroCommandService(path, target: null, output.Add);

        await service.ExecuteAsync("list");
        await service.ExecuteAsync("validate");

        Assert.Contains(output, line => line.Contains("TestMacro - Test macro", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("Validated 1 macro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunsNamedMacroAndPrintsOperationProgress()
    {
        var path = await WriteMacroFileAsync();
        var output = new List<string>();
        var target = new RecordingTarget();
        var service = new MacroCommandService(path, target, output.Add);

        await service.ExecuteAsync("run TestMacro");

        Assert.Equal(
            [("KEY_UP", RemoteKeyAction.Click), ("KEY_UP", RemoteKeyAction.Click)],
            target.Commands);
        Assert.Contains(output, line => line.StartsWith("[1/2]", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("Completed TestMacro", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<string> WriteMacroFileAsync()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "macros.yaml");
        await File.WriteAllTextAsync(
            path,
            """
            macros:
              TestMacro:
                description: Test macro
                steps:
                  - key: KEY_UP
                    repeat: 2
            """);
        return path;
    }

    private sealed class RecordingTarget : IMacroCommandTarget
    {
        public List<(string Key, RemoteKeyAction Action)> Commands { get; } = [];

        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((key, action));
            return Task.CompletedTask;
        }
    }
}
