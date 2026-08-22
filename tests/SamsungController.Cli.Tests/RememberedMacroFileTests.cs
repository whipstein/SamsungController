using SamsungController.Cli;

namespace SamsungController.Cli.Tests;

public sealed class RememberedMacroFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.RememberedMacroTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SuccessfulExplicitMacroFileIsUsedByLaterInvocation()
    {
        Directory.CreateDirectory(_directory);
        var macroFilePath = Path.Combine(_directory, "selected-macros.yaml");
        await File.WriteAllTextAsync(
            macroFilePath,
            """
            macros:
              Remembered:
                - key: KEY_UP
            """);

        var validateResult = await Program.RunAsync(
            CliArguments.Parse(
            [
                "macro",
                "validate",
                "--config-dir",
                _directory,
                "--macro-file",
                macroFilePath
            ]),
            CancellationToken.None);
        var listResult = await Program.RunAsync(
            CliArguments.Parse(["macro", "list", "--config-dir", _directory]),
            CancellationToken.None);
        var settings = await SamsungCliSettings.LoadAsync(
            Path.Combine(_directory, "settings.json"),
            CancellationToken.None);

        Assert.Equal(0, validateResult);
        Assert.Equal(0, listResult);
        Assert.Equal(Path.GetFullPath(macroFilePath), settings.MacroFilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
