using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionSchemaInspectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.SchemaInspectorTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectsEveryYamlAndJsonFileInDirectory()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "valid.yaml"), ValidYaml);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "broken.json"),
            "{ \"version\": 1, \"id\": \"broken\", \"nodes\": [] }");
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "not a definition");

        var results = await new MenuDefinitionSchemaInspector().InspectPathAsync(_directory);

        Assert.Equal(2, results.Count);
        var valid = Assert.Single(results, result => result.IsValid);
        Assert.Equal("test-menu", valid.Id);
        Assert.Equal(3, valid.NodeCount);
        Assert.Equal(1, valid.AnchorCount);
        Assert.Equal(1, valid.ExplicitTransitionCount);
        Assert.Contains(valid.Warnings, warning => warning.Contains(
            "0 stored, 1 calculated",
            StringComparison.Ordinal));
        var invalid = Assert.Single(results, result => !result.IsValid);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Contains(
            "line 1",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReportsGeneratedRouteRefreshWithoutRejectingFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "menu.yaml");
        await File.WriteAllTextAsync(path, ValidYaml);

        var result = await new MenuDefinitionSchemaInspector().InspectFileAsync(path);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.GeneratedTransitionCount);
        Assert.Contains(result.Warnings, warning => warning.Contains(
            "0 stored, 1 calculated",
            StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string ValidYaml = """
        version: 1
        id: test-menu
        name: Test menu
        model: Test TV
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: settings
                label: Settings
                children:
                  - id: picture
                    label: Picture
        anchors:
          - id: normal
            label: Normal video
            target: normal-video
            verified: true
            steps:
              - key: KEY_RETURN
        transitions:
          - id: open-settings
            from: normal-video
            to: settings
            steps:
              - key: KEY_MENU
        """;
}
