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

    [Fact]
    public async Task NewDefinitionCanBeCreatedAndPersistedEntirelyThroughTheService()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "new-tv",
            "New TV Menu",
            "Samsung Test TV",
            "1000",
            "SDR",
            "Movie",
            "HDMI 1"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal("New TV Menu", snapshot.DefinitionName);
        Assert.Equal("1000", snapshot.Context?.Firmware);
        Assert.Contains(snapshot.Nodes, node => node.Id == "normal-video");
        Assert.True(File.Exists(snapshot.DefinitionPath));

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal("new-tv", reparsed.Id);
        Assert.Empty(reparsed.Anchors);
        Assert.Empty(reparsed.Transitions);
    }

    [Fact]
    public async Task DraftCanBeDeletedAndYamlIsUpdatedThroughTheService()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "draft-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, DraftMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var before = controller.GetMenuAuthoringSnapshot();
        var candidate = Assert.Single(before.DraftCandidates);
        Assert.Equal("open-settings", candidate.Id);
        Assert.Equal("normal-video", candidate.SourceNodeId);
        Assert.Equal("settings", candidate.TargetNodeId);

        await controller.DeleteMenuAuthoringDraftAsync(
            MenuAuthoringItemKind.Transition,
            candidate.Id);

        Assert.Empty(controller.GetMenuAuthoringSnapshot().DraftCandidates);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Empty(reparsed.Transitions);
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

    private const string DraftMenuYaml =
        """
        version: 1
        id: draft-test
        name: Draft Test Menu
        model: Test TV
        nodes:
          - id: normal-video
            label: Normal video
          - id: settings
            label: Settings
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
            verified: false
            steps:
              - key: KEY_MENU
                delay: 600ms
        """;
}
