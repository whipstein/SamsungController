using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SamsungController.Automation.Navigation;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class ControllerMenuIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.Web.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializationLoadsConfiguredMenuDefinitionIntoSnapshot()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ValidMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var snapshot = controller.GetMenuNavigationSnapshot();

        Assert.Equal("Test Menu", snapshot.DefinitionName);
        Assert.Equal("Test TV", snapshot.Model);
        Assert.Equal(MenuStateConfidence.Unknown, snapshot.State.Confidence);
        Assert.Equal("Normal video", Assert.Single(snapshot.Nodes).Path);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public async Task InvalidConfiguredDefinitionIsVisibleWithoutBreakingTheWebSession()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "broken.yaml");
        await File.WriteAllTextAsync(definitionPath, "version: 1\nid: broken");
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var snapshot = controller.GetMenuNavigationSnapshot();

        Assert.Null(snapshot.DefinitionName);
        Assert.Empty(snapshot.Nodes);
        Assert.Contains("must contain", snapshot.Error, StringComparison.Ordinal);
    }

    private SamsungControllerService CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SamsungController:ConfigurationDirectory"] = _directory
            })
            .Build();
        return new SamsungControllerService(configuration);
    }

    private async Task WriteSettingsAsync(string definitionPath)
    {
        var json = JsonSerializer.Serialize(new { MenuDefinitionPath = definitionPath });
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string ValidMenuYaml =
        """
        version: 1
        id: test
        name: Test Menu
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
        """;
}
