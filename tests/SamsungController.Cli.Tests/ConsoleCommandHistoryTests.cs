using SamsungController.Cli;

namespace SamsungController.Cli.Tests;

public sealed class ConsoleCommandHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.HistoryTests-{Guid.NewGuid():N}");

    [Fact]
    public void PersistsCommandsAcrossInstancesAndExcludesRawPayloads()
    {
        var path = Path.Combine(_directory, "console-history.txt");
        var history = new ConsoleCommandHistory(path);

        history.Add("key KEY_UP");
        history.Add("raw {\"token\":\"secret\"}");
        history.Add("  RAW {\"method\":\"experimental\"}");
        history.Add("query apps");

        var reloaded = new ConsoleCommandHistory(path);
        Assert.Equal(["key KEY_UP", "query apps"], reloaded.Snapshot());
        Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesCapacityAndClearUpdatesPersistentFile()
    {
        var path = Path.Combine(_directory, "console-history.txt");
        var history = new ConsoleCommandHistory(path, capacity: 2);
        history.Add("key KEY_UP");
        history.Add("key KEY_DOWN");
        history.Add("query apps");

        Assert.Equal(["key KEY_DOWN", "query apps"], history.Snapshot());

        history.Clear();

        Assert.Empty(new ConsoleCommandHistory(path).Snapshot());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
