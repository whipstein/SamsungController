using SamsungController.Cli;

namespace SamsungController.Cli.Tests;

public sealed class MenuSchemaCommandServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.MenuSchemaCliTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReportsValidAndInvalidFilesAndReturnsFailure()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "valid.yaml"),
            """
            version: 1
            id: valid
            name: Valid menu
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
            anchors:
              - id: normal
                label: Normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            """);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "invalid.json"),
            "{ \"version\": 1, \"id\": \"invalid\", \"nodes\": [] }");
        var output = new List<string>();

        var exitCode = await new MenuSchemaCommandService(output.Add)
            .ExecuteAsync(["validate", _directory]);

        Assert.Equal(2, exitCode);
        Assert.Contains(output, line => line.StartsWith("[VALID]", StringComparison.Ordinal));
        Assert.Contains(output, line => line.StartsWith("[INVALID]", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("line 1", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains(
            "1 valid, 1 invalid",
            StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
