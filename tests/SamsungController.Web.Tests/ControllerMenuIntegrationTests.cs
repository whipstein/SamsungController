using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Automation.Macros;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;
using SamsungController.Core.Protocol;
using SamsungController.Web.Services;
using SamsungController.Web.Components;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.Web.Tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _installedTestMenus = [];

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
    public async Task DiscoversRunnableLocalMenusAndTheirConfigurations()
    {
        Directory.CreateDirectory(_directory);
        var activePath = Path.Combine(_directory, "active.yaml");
        await File.WriteAllTextAsync(activePath, ValidMenuYaml);
        await WriteSettingsAsync(activePath);
        var catalogDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(catalogDirectory);
        var alternatePath = Path.Combine(catalogDirectory, "alternate.yaml");
        await File.WriteAllTextAsync(
            alternatePath,
            """
            version: 1
            id: alternate
            name: Alternate Menu
            model: Test TV 2
            context:
              firmware: "2000"
            configurations:
              - id: standard
                name: Standard
                conditions: Game Mode = Off
              - id: game
                name: Game
                conditions: Game Mode = On
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
            Path.Combine(catalogDirectory, "invalid.yaml"),
            "version: 1\nid: invalid");
        var installedGenericPath = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "menu.example.yaml");
        var userGenericPath = Path.Combine(catalogDirectory, "menu.example.yaml");
        File.Copy(installedGenericPath, userGenericPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var discovered = await controller.DiscoverMenuDefinitionsAsync();

        var active = Assert.Single(discovered, item => item.Id == "test");
        Assert.True(active.IsActive);
        Assert.Equal("Custom file", active.Location);
        var alternate = Assert.Single(discovered, item => item.Id == "alternate");
        Assert.False(alternate.IsActive);
        Assert.Equal("User data", alternate.Location);
        var invalid = Assert.Single(discovered, item => item.Path.EndsWith(
            "invalid.yaml",
            StringComparison.Ordinal));
        Assert.False(invalid.IsValid);
        Assert.Contains("nodes", invalid.Error, StringComparison.Ordinal);
        var genericCopies = discovered
            .Where(item => item.Id == "generic-picture-menu")
            .ToArray();
        Assert.Equal(2, genericCopies.Length);
        Assert.Contains(genericCopies, item =>
            item.Location == "User data" && item.Path == userGenericPath);
        Assert.Contains(genericCopies, item =>
            item.Location == "Installation" && item.Path == installedGenericPath);

        await controller.SetMenuDefinitionAsync(alternate.Path);
        var navigation = controller.GetMenuNavigationSnapshot();
        Assert.Equal("Alternate Menu", navigation.DefinitionName);
        Assert.Equal(["standard", "game"], navigation.Configurations.Select(item => item.Id));
        await controller.SetMenuConfigurationAsync("game");
        Assert.Equal(
            "game",
            controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
    }

    [Fact]
    public async Task DisplayDefinitionResolvesUserMenuAndPreservesVerificationIdentity()
    {
        Directory.CreateDirectory(_directory);
        var menuDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(menuDirectory);
        var menuPath = Path.Combine(menuDirectory, "test.yaml");
        await File.WriteAllTextAsync(menuPath, ValidMenuYaml);
        await WriteSettingsAsync(menuPath);
        var displayDirectory = Path.Combine(_directory, "display-definitions");
        Directory.CreateDirectory(displayDirectory);
        var displayPath = Path.Combine(displayDirectory, "living-room.display.json");
        await File.WriteAllTextAsync(
            displayPath,
            """
            {
              "version": 1,
              "id": "living-room",
              "name": "Living Room TV",
              "connection": {
                "host": "192.0.2.25",
                "secure": true,
                "port": null,
                "allowUntrustedCertificate": true
              },
              "defaultMenu": "default",
              "menus": [
                {
                  "id": "default",
                  "definitionId": "test",
                  "source": "userData",
                  "path": null,
                  "configurationId": null
                }
              ]
            }
            """);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var verificationBefore = controller.GetMenuDefinitionVerificationSnapshot();
        var definitions = await controller.DiscoverDisplayDefinitionsAsync();
        var discovered = Assert.Single(definitions, item => item.Path == displayPath);

        Assert.True(discovered.IsValid);
        Assert.Equal(menuPath, discovered.ResolvedMenuDefinitionPath);
        await controller.SetDisplayDefinitionAsync(displayPath);

        var snapshot = controller.GetSnapshot();
        var verificationAfter = controller.GetMenuDefinitionVerificationSnapshot();
        Assert.Equal("Living Room TV", snapshot.DisplayName);
        Assert.Equal("192.0.2.25", snapshot.Host);
        Assert.Equal(displayPath, snapshot.DisplayDefinitionPath);
        Assert.Equal("Test Menu", controller.GetMenuNavigationSnapshot().DefinitionName);
        Assert.Equal(verificationBefore.CurrentDisplay, verificationAfter.CurrentDisplay);
        Assert.Equal(verificationBefore.RequiredCount, verificationAfter.RequiredCount);
        Assert.Equal(verificationBefore.VerifiedCount, verificationAfter.VerifiedCount);
        Assert.True(Assert.Single(
            await controller.DiscoverDisplayDefinitionsAsync(),
            item => item.Path == displayPath).IsActive);
    }

    [Fact]
    public async Task SavesCurrentDisplayAsUserDefinitionReferencingUserMenu()
    {
        Directory.CreateDirectory(_directory);
        var menuDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(menuDirectory);
        var menuPath = Path.Combine(menuDirectory, "test.yaml");
        await File.WriteAllTextAsync(menuPath, ValidMenuYaml);
        await WriteSettingsAsync(menuPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        var request = new DisplayDefinitionEditRequest(
            "office-tv",
            "Office TV",
            "192.0.2.30",
            true,
            null,
            true,
            menuPath,
            "test",
            null);

        var path = await controller.SaveDisplayDefinitionAsync(request);

        var saved = await new DisplayDefinitionStore().LoadAsync(path);
        Assert.Equal("office-tv", saved.Id);
        var menu = Assert.Single(saved.Menus);
        Assert.Equal("test", menu.DefinitionId);
        Assert.Equal(DisplayMenuDefinitionSource.UserData, menu.Source);
        Assert.False(Path.IsPathRooted(menu.Path));
        var snapshot = controller.GetSnapshot();
        Assert.Equal(path, snapshot.DisplayDefinitionPath);
        Assert.Equal("Office TV", snapshot.DisplayName);
        Assert.Equal("192.0.2.30", snapshot.Host);
    }

    [Fact]
    public async Task SavingDisplayWithInstalledMenuStoresReferenceWithoutCopyingMenu()
    {
        Directory.CreateDirectory(_directory);
        var installedPath = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "menu.example.yaml");
        var installed = await new MenuDefinitionParser().ParseFileAsync(installedPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        var request = new DisplayDefinitionEditRequest(
            "reference-only-tv",
            "Reference-only TV",
            "192.0.2.32",
            true,
            null,
            true,
            installedPath,
            installed.Id,
            installed.ActiveConfigurationId);

        var displayPath = await controller.SaveDisplayDefinitionAsync(request);

        var saved = await new DisplayDefinitionStore().LoadAsync(displayPath);
        var reference = Assert.Single(saved.Menus);
        Assert.Equal(DisplayMenuDefinitionSource.Installation, reference.Source);
        Assert.Null(reference.Path);
        Assert.False(Directory.Exists(Path.Combine(_directory, "menu-definitions")));
    }

    [Fact]
    public async Task UpdatingDisplayDefinitionAddsCurrentMenuWithoutRemovingExistingLinks()
    {
        Directory.CreateDirectory(_directory);
        var menuDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(menuDirectory);
        var defaultMenuPath = Path.Combine(menuDirectory, "test.yaml");
        var alternateMenuPath = Path.Combine(menuDirectory, "alternate.yaml");
        await File.WriteAllTextAsync(defaultMenuPath, ValidMenuYaml);
        await File.WriteAllTextAsync(
            alternateMenuPath,
            ValidMenuYaml
                .Replace("id: test", "id: alternate", StringComparison.Ordinal)
                .Replace("name: Test Menu", "name: Alternate Menu", StringComparison.Ordinal));
        await WriteSettingsAsync(defaultMenuPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var firstRequest = new DisplayDefinitionEditRequest(
            "living-room",
            "Living Room",
            "192.0.2.31",
            true,
            null,
            true,
            defaultMenuPath,
            "test",
            null);
        var path = await controller.SaveDisplayDefinitionAsync(firstRequest);
        await controller.SetMenuDefinitionAsync(alternateMenuPath);
        var secondRequest = firstRequest with
        {
            MenuDefinitionPath = alternateMenuPath,
            MenuDefinitionId = "alternate"
        };

        await controller.SaveDisplayDefinitionAsync(secondRequest);

        var saved = await new DisplayDefinitionStore().LoadAsync(path);
        Assert.Equal(2, saved.Menus.Count);
        Assert.Contains(saved.Menus, menu => menu.DefinitionId == "test");
        var alternate = Assert.Single(
            saved.Menus,
            menu => menu.DefinitionId == "alternate");
        Assert.Equal(alternate.Id, saved.DefaultMenu);
    }

    [Fact]
    public async Task DisplayDefinitionSwitchesBetweenMultipleLinkedMenus()
    {
        Directory.CreateDirectory(_directory);
        var menuDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(menuDirectory);
        var defaultMenuPath = Path.Combine(menuDirectory, "test.yaml");
        var alternateMenuPath = Path.Combine(menuDirectory, "alternate.yaml");
        await File.WriteAllTextAsync(defaultMenuPath, ValidMenuYaml);
        await File.WriteAllTextAsync(
            alternateMenuPath,
            ValidMenuYaml
                .Replace("id: test", "id: alternate", StringComparison.Ordinal)
                .Replace("name: Test Menu", "name: Alternate Menu", StringComparison.Ordinal));
        await WriteSettingsAsync(defaultMenuPath);
        var displayDirectory = Path.Combine(_directory, "display-definitions");
        Directory.CreateDirectory(displayDirectory);
        var displayPath = Path.Combine(displayDirectory, "multi-menu.display.json");
        await File.WriteAllTextAsync(
            displayPath,
            """
            {
              "version": 1,
              "id": "multi-menu",
              "name": "Multi-menu TV",
              "connection": { "host": "192.0.2.40" },
              "defaultMenu": "normal",
              "menus": [
                {
                  "id": "normal",
                  "definitionId": "test",
                  "source": "userData"
                },
                {
                  "id": "alternate",
                  "definitionId": "alternate",
                  "source": "userData"
                }
              ]
            }
            """);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        await controller.SetDisplayDefinitionAsync(displayPath);
        var discovered = Assert.Single(
            await controller.DiscoverDisplayDefinitionsAsync(),
            item => item.Path == displayPath);
        Assert.Equal(2, discovered.Menus.Count);
        Assert.Equal("Test Menu", controller.GetMenuNavigationSnapshot().DefinitionName);

        await controller.SetDisplayMenuReferenceAsync(displayPath, "alternate");

        Assert.Equal("Alternate Menu", controller.GetMenuNavigationSnapshot().DefinitionName);
        Assert.Equal(displayPath, controller.GetSnapshot().DisplayDefinitionPath);
    }

    [Fact]
    public async Task EditingInstalledMenuRequiresAnExplicitCopy()
    {
        Directory.CreateDirectory(_directory);
        var installedPath = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "menu.example.yaml");
        var installedContent = await File.ReadAllTextAsync(installedPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.UpdateMenuNodeAsync(
                "normal-video",
                new MenuNodeEditRequest(
                    "normal-video",
                    "Normal video reference",
                    null,
                    "No TV menu is expected to be visible.")));

        var overridePath = Path.Combine(
            _directory,
            "menu-definitions",
            "generic-picture-menu.yaml");
        Assert.Contains("explicitly create an editable copy", exception.Message);
        Assert.False(File.Exists(overridePath));
        Assert.Equal(installedContent, await File.ReadAllTextAsync(installedPath));
        Assert.Equal(installedPath, controller.GetMenuNavigationSnapshot().DefinitionPath);
    }

    [Fact]
    public async Task ReloadMenuDefinitionReadsExternalChangesFromTheActiveFile()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ValidMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        await File.WriteAllTextAsync(
            definitionPath,
            ValidMenuYaml.Replace("name: Test Menu", "name: Corrected Test Menu", StringComparison.Ordinal));

        await controller.ReloadMenuDefinitionAsync();

        var navigation = controller.GetMenuNavigationSnapshot();
        Assert.Equal("Corrected Test Menu", navigation.DefinitionName);
        Assert.Contains("reloaded", navigation.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoveryShowsInvalidMenuFileWithActionableDiagnostic()
    {
        Directory.CreateDirectory(_directory);
        var catalogDirectory = Path.Combine(_directory, "menu-definitions");
        Directory.CreateDirectory(catalogDirectory);
        var invalidPath = Path.Combine(catalogDirectory, "broken.json");
        await File.WriteAllTextAsync(
            invalidPath,
            "{ \"version\": 1, \"id\": \"broken\", \"nodes\": [] }");
        await using var controller = CreateController();

        var discovered = await controller.DiscoverMenuDefinitionsAsync();

        var invalid = Assert.Single(discovered, item => item.Path == invalidPath);
        Assert.Equal("broken.json", invalid.Name);
        Assert.Equal("User data", invalid.Location);
        Assert.False(invalid.IsValid);
        Assert.False(invalid.IsActive);
        Assert.Contains("line 1", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedAnchorTargetIsReportedAsRecordedDraftRoute()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "draft-anchor-menu.yaml");
        await File.WriteAllTextAsync(
            definitionPath,
            ValidMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal));
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var node = Assert.Single(controller.GetMenuNavigationSnapshot().Nodes);

        Assert.False(node.HasVerifiedRoute);
        Assert.True(node.HasDraftRoute);
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
    public async Task InvalidConfiguredJsonReportsEachNodesSourceLine()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "broken.json");
        await File.WriteAllTextAsync(
            definitionPath,
            """
            {
              "version": 1,
              "id": "broken-json",
              "name": "Broken JSON",
              "model": "Samsung Test TV",
              "nodes": [
                {
                  "id": "normal-video",
                  "label": "Normal video",
                  "children": [
                    {
                      "id": "picture-mode",
                      "label": "Picture Mode",
                      "controlType": "selection",
                      "options": ["Standard", "Movie"]
                    },
                    {
                      "id": "reset-picture",
                      "label": "Reset Picture",
                      "controlType": "confirmation",
                      "options": ["Reset", "Cancel"]
                    }
                  ]
                }
              ]
            }
            """);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();
        var error = controller.GetMenuNavigationSnapshot().Error;

        Assert.Contains("line 12, column 11 · node 'picture-mode'", error, StringComparison.Ordinal);
        Assert.Contains("line 18, column 11 · node 'reset-picture'", error, StringComparison.Ordinal);
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
            "1000"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal("New TV Menu", snapshot.DefinitionName);
        Assert.Equal("1000", snapshot.Context?.Firmware);
        Assert.Contains(snapshot.Nodes, node => node.Id == "normal-video");
        Assert.True(File.Exists(snapshot.DefinitionPath));

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal("new-tv", reparsed.Id);
        Assert.Equal(800, reparsed.Timing.ScreenChangeDelayMilliseconds);
        Assert.False(reparsed.Timing.Verified);
        Assert.True(controller.GetMenuAuthoringSnapshot().Timing.Verified);
        Assert.True(File.Exists(new MenuVerificationStore(_directory).GetPath("new-tv")));
        var defaultConfiguration = Assert.Single(reparsed.Configurations.Values);
        Assert.Equal("default", defaultConfiguration.Id);
        Assert.Equal("Default menu layout.", defaultConfiguration.Conditions);
        Assert.Empty(reparsed.Anchors);
        Assert.Empty(reparsed.Transitions);
    }

    [Fact]
    public async Task InitializationUpgradesLegacyDefaultTimingForImmediateRecording()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "legacy-default-timing.yaml");
        var yaml = ValidMenuYaml.Replace(
            "  defaultDelay: 125ms\n  screenChangeDelay: 600ms",
            "  defaultDelay: 150ms\n  screenChangeDelay: 500ms",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(definitionPath, yaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();

        await controller.InitializeAsync();

        var timing = controller.GetMenuAuthoringSnapshot().Timing;
        Assert.Equal(150, timing.DefaultDelayMilliseconds);
        Assert.Equal(800, timing.ScreenChangeDelayMilliseconds);
        Assert.Equal(300, timing.ReturnDelayMilliseconds);
        Assert.Equal(75, timing.AdjustmentDelayMilliseconds);
        Assert.True(timing.Verified);
        Assert.Equal(3, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
    }

    [Fact]
    public async Task RouteLessDefinitionCanRecordWhenPersistedTimingWasUnverified()
    {
        const string yaml =
            """
            version: 1
            id: route-less
            name: Route-less Menu
            model: Test TV
            timing:
              defaultDelay: 150ms
              screenChangeDelay: 800ms
              returnDelay: 300ms
              verified: false
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: settings
                    label: Settings
            anchors: []
            transitions: []
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var beforeRecording = controller.GetMenuAuthoringSnapshot();
            Assert.True(beforeRecording.Timing.Verified);
            Assert.Equal(3, beforeRecording.TimingValidationPasses);

            controller.StartMenuRecording(new MenuRecordingRequest(
                MenuAuthoringItemKind.Transition,
                string.Empty,
                "Settings",
                "normal-video",
                "settings",
                null,
                null));

            Assert.True(controller.GetMenuAuthoringSnapshot().IsRecording);

            await controller.SendKeyAsync("KEY_MENU");
            await controller.StopAndSaveMenuRecordingAsync();

            var persisted = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            Assert.False(persisted.Timing.Verified);
            Assert.Equal(800, persisted.Timing.ScreenChangeDelayMilliseconds);
        }
    }

    [Fact]
    public async Task RecordedMenuEntryGeneratesAndGroupValidatesTopologyRoutes()
    {
        const string yaml =
            """
            version: 1
            id: generated-coverage
            name: Generated Coverage
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
                        children:
                          - id: picture-mode
                            label: Picture Mode
                            controlType: selection
                            defaultValue: Movie
                            options: [Standard, Movie]
                          - id: expert
                            label: Expert Settings
                            children:
                              - id: brightness
                                label: Brightness
                                controlType: slider
                                defaultValue: 25
                                minimumValue: 0
                                maximumValue: 50
                      - id: sound
                        label: Sound
                        children:
                          - id: sound-output
                            label: Sound Output
                            controlType: selection
                            defaultValue: TV Speaker
                            options: [TV Speaker, Receiver]
            anchors:
              - id: normal-video
                label: Return to video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_EXIT
            transitions: []
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            controller.StartMenuRecording(new MenuRecordingRequest(
                MenuAuthoringItemKind.Transition,
                string.Empty,
                "Settings",
                "normal-video",
                "settings",
                null,
                null));
            await controller.SendKeyAsync("KEY_MENU");
            await controller.StopAndSaveMenuRecordingAsync();

            var generated = controller.GetMenuAuthoringSnapshot();
            Assert.Equal(2, generated.DraftCandidates.Count);
            Assert.All(generated.DraftCandidates, candidate => Assert.True(candidate.GeneratedFromTopology));
            Assert.Equal([4, 2], generated.DraftCandidates
                .Select(candidate => candidate.CoveredRouteCount)
                .OrderDescending());

            var pictureCoverage = Assert.Single(generated.DraftCandidates, candidate =>
                candidate.TargetPath.EndsWith("Brightness", StringComparison.Ordinal));
            for (var pass = 0; pass < 3; pass++)
            {
                await controller.RunMenuAuthoringValidationAsync(
                    pictureCoverage.Kind,
                    pictureCoverage.Id);
                await controller.ConfirmMenuAuthoringValidationAsync(passed: true);
            }

            var remaining = controller.GetMenuAuthoringSnapshot().DraftCandidates;
            Assert.Single(remaining);
            Assert.DoesNotContain(remaining, candidate =>
                candidate.ValidationPasses > 0);
            var persisted = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            var seed = Assert.Single(persisted.Transitions.Values, transition =>
                !transition.GeneratedFromTopology
                && transition.FromNodeId == "normal-video"
                && transition.ToNodeId == "settings");
            Assert.False(seed.Verified);
            var verifiedGroupId = persisted.Transitions[pictureCoverage.Id].ValidationGroupId;
            Assert.All(
                persisted.Transitions.Values.Where(transition =>
                    transition.ValidationGroupId == verifiedGroupId),
                transition => Assert.False(transition.Verified));
            var verification = controller.GetMenuDefinitionVerificationSnapshot();
            Assert.True(verification.Checks.Single(check =>
                check.Kind == MenuVerificationCheckKind.Route
                && check.AuthoringItemId == pictureCoverage.Id).Verified);
        }
    }

    [Fact]
    public async Task GuidedAnchorDefinitionCreatesEntryAndReturnVerificationPlan()
    {
        const string yaml =
            """
            version: 1
            id: guided-anchor
            name: Guided Anchor
            model: Test TV
            nodes:
              - id: tv-interface
                label: TV interface
                children:
                  - id: normal-video
                    label: Normal video
                    children:
                      - id: settings
                        label: Settings
                        children:
                          - id: picture
                            label: Picture
                            children:
                              - id: brightness
                                label: Brightness
                                controlType: slider
                                defaultValue: 25
                                minimumValue: 0
                                maximumValue: 50
                              - id: captions-enabled
                                label: Captions
                                controlType: switch
                                defaultValue: off
                              - id: digital-caption-options
                                label: Digital Caption Options
                                controlType: submenu
                                disabledWhen:
                                  - setting: captions-enabled
                                    equals: off
                                children:
                                  - id: background-color
                                    label: Background Color
                                    controlType: selection
                                    defaultValue: Default
                                    options: [Default, Black]
                                    disabledWhen:
                                      - setting: captions-enabled
                                        equals: off
                          - id: sound
                            label: Sound
            anchors: []
            transitions: []
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.DefineMenuAnchorAsync(
                "settings",
                ["KEY_RETURN"],
                ["KEY_MENU", "KEY_RETURN"]);

            var defined = Assert.IsType<MenuReturnStrategySummary>(
                controller.GetMenuAuthoringSnapshot().ReturnStrategy);
            Assert.Equal("settings", defined.MenuRootNodeId);
            Assert.False(defined.HasEntryRoute);
            Assert.False(defined.AtMenuRoot.Verified);
            Assert.False(defined.BelowMenuRoot.Verified);
            Assert.Empty(controller.GetMenuAuthoringSnapshot().DraftCandidates);

            controller.StartMenuRecording(new MenuRecordingRequest(
                MenuAuthoringItemKind.Transition,
                string.Empty,
                "Settings",
                "normal-video",
                "settings",
                null,
                null));
            await controller.SendKeyAsync("KEY_MENU");
            await controller.StopAndSaveMenuRecordingAsync();

            var planned = Assert.IsType<MenuReturnStrategySummary>(
                controller.GetMenuAuthoringSnapshot().ReturnStrategy);
            Assert.True(planned.HasEntryRoute);
            Assert.Equal("KEY_MENU", planned.EntryScript);
            Assert.Equal(2, controller.GetMenuAuthoringSnapshot().DraftCandidates.Count);
            Assert.Contains(planned.DeepTestNodes, node => node.Id == "brightness");
            Assert.DoesNotContain(planned.DeepTestNodes, node =>
                node.Id is "digital-caption-options" or "background-color");

            transport.SentMessages.Clear();
            await controller.PrepareMenuReturnStrategyTestSourceAsync(
                MenuReturnScriptKind.AtMenuRoot,
                null);
            Assert.Equal(["KEY_MENU"], GetSentKeys(transport));

            transport.SentMessages.Clear();
            await controller.PrepareMenuReturnStrategyTestSourceAsync(
                MenuReturnScriptKind.BelowMenuRoot,
                "picture");
            Assert.Equal(["KEY_MENU", "KEY_ENTER"], GetSentKeys(transport));

            for (var pass = 0; pass < 3; pass++)
            {
                transport.SentMessages.Clear();
                await controller.RunMenuReturnStrategyTestAsync(
                    MenuReturnScriptKind.AtMenuRoot,
                    null);
                Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
                await controller.ConfirmMenuReturnStrategyTestAsync(passed: true);
                var confirmed = controller.GetSnapshot();
                Assert.Equal("Normal video", confirmed.MenuLabel);
                Assert.Equal(MenuStateConfidence.Synchronized, confirmed.MenuConfidence);
            }

            for (var pass = 0; pass < 3; pass++)
            {
                transport.SentMessages.Clear();
                await controller.RunMenuReturnStrategyTestAsync(
                    MenuReturnScriptKind.BelowMenuRoot,
                    "picture");
                Assert.Equal(["KEY_MENU", "KEY_RETURN"], GetSentKeys(transport));
                await controller.ConfirmMenuReturnStrategyTestAsync(passed: true);
                var confirmed = controller.GetSnapshot();
                Assert.Equal("Normal video", confirmed.MenuLabel);
                Assert.Equal(MenuStateConfidence.Synchronized, confirmed.MenuConfidence);
            }

            var persisted = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            var anchor = Assert.Single(persisted.Anchors.Values);
            Assert.False(anchor.Verified);
            Assert.False(anchor.ReturnStrategy!.AtMenuRoot.Verified);
            Assert.False(anchor.ReturnStrategy.BelowMenuRoot.Verified);
            var effective = Assert.IsType<MenuReturnStrategySummary>(
                controller.GetMenuAuthoringSnapshot().ReturnStrategy);
            Assert.True(effective.AtMenuRoot.Verified);
            Assert.True(effective.BelowMenuRoot.Verified);
        }
    }

    [Fact]
    public async Task ExistingDefinitionRequiresConfirmationAndCanBeReplaced()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        var request = new MenuDefinitionCreationRequest(
            "replace-tv",
            "Replacement TV Menu",
            "Samsung Test TV",
            "1000");

        await controller.CreateMenuDefinitionAsync(request);
        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "settings",
            "Settings",
            "tv-interface",
            null));

        var preview = controller.PreviewMenuDefinitionCreation(request);
        Assert.True(preview.FileExists);
        Assert.EndsWith("replace-tv.yaml", preview.Path, StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<IOException>(
            () => controller.CreateMenuDefinitionAsync(request));
        Assert.Contains("Confirm replacement", exception.Message, StringComparison.Ordinal);

        await controller.CreateMenuDefinitionAsync(request, replaceExisting: true);

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.DoesNotContain(snapshot.Nodes, node => node.Id == "settings");
        Assert.Equal(["tv-interface", "normal-video"], snapshot.Nodes.Select(node => node.Id));
        Assert.Contains(
            "profile replaced",
            controller.GetMenuAuthoringSnapshot().Status,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewDefinitionDefaultsItsNameAndIdFromModelAndFirmware()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            string.Empty,
            string.Empty,
            "QN90D",
            "1296"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal("QN90D · firmware 1296", snapshot.DefinitionName);
        Assert.EndsWith(
            "qn90d-1296.yaml",
            snapshot.DefinitionPath,
            StringComparison.Ordinal);

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal("qn90d-1296", reparsed.Id);
        Assert.Equal("QN90D · firmware 1296", reparsed.Name);
    }

    [Fact]
    public async Task NewDefinitionWritesOnlyFirmwareContext()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            string.Empty,
            string.Empty,
            "QN90D",
            "1296"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.EndsWith("qn90d-1296.yaml", snapshot.DefinitionPath, StringComparison.Ordinal);

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal("qn90d-1296", reparsed.Id);
        Assert.Equal("1296", reparsed.Context.Firmware);
        using var json = JsonDocument.Parse(new MenuDefinitionJsonSerializer().Serialize(reparsed));
        Assert.Equal("firmware", Assert.Single(json.RootElement.GetProperty("context").EnumerateObject()).Name);
        var yaml = await File.ReadAllTextAsync(snapshot.DefinitionPath);
        Assert.DoesNotContain("signal:", yaml);
        Assert.DoesNotContain("pictureMode:", yaml);
        Assert.DoesNotContain("input:", yaml);
    }

    [Fact]
    public async Task NewDefinitionCanUseJsonAndSubsequentEditsPreserveJson()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "json-tv",
            "JSON TV",
            "S95F",
            "1296",
            MenuDefinitionFileFormat.Json));
        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "settings",
            "Settings",
            "tv-interface",
            null));

        var path = controller.GetMenuNavigationSnapshot().DefinitionPath;
        var content = await File.ReadAllTextAsync(path);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.EndsWith("json-tv.json", path, StringComparison.Ordinal);
        Assert.StartsWith("{", content.TrimStart(), StringComparison.Ordinal);
        Assert.Equal("Settings", reparsed.Nodes["settings"].Label);
    }

    [Fact]
    public async Task MenuTreeCanBeDefinedAndRenamedBeforeRecordingRoutes()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "tree-first",
            "Tree First Menu",
            "Samsung Test TV",
            "1000"));

        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "settings",
            "Settings",
            "tv-interface",
            "Top-level settings overlay"));
        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "picture",
            "Picture",
            "settings",
            null));
        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "expert",
            "Expert Settings",
            "picture",
            null));
        await controller.CreateMenuNodeAsync(new MenuNodeEditRequest(
            "sound",
            "Sound",
            "settings",
            null));
        await controller.UpdateMenuNodeAsync(
            "picture",
            new MenuNodeEditRequest(
                "picture",
                "Picture controls",
                "settings",
                "The Picture menu is visible"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        var picture = Assert.Single(snapshot.Nodes, node => node.Id == "picture");
        Assert.Equal("Picture controls", picture.Label);
        Assert.Equal("settings", picture.ParentId);
        Assert.Equal("TV interface / Settings / Picture controls", picture.Path);
        Assert.Equal(MenuControlType.Submenu, picture.ControlType);
        Assert.Null(picture.DefaultValue);
        Assert.Empty(picture.SelectionOptions);

        await controller.MoveMenuNodeAsync("sound", -1);

        snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal(
            ["sound", "picture"],
            snapshot.Nodes
                .Where(node => node.ParentId == "settings")
                .Select(node => node.Id));
        Assert.Equal(
            ["tv-interface", "normal-video", "settings", "sound", "picture", "expert"],
            snapshot.Nodes.Select(node => node.Id));

        await controller.DeleteMenuNodeAsync("picture");

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.DoesNotContain("picture", reparsed.Nodes.Keys);
        Assert.DoesNotContain("expert", reparsed.Nodes.Keys);
        Assert.Contains("settings", reparsed.Nodes.Keys);
        Assert.Equal(
            ["tv-interface", "normal-video", "settings", "sound"],
            reparsed.Nodes.Values.Select(node => node.Id));
    }

    [Fact]
    public async Task MenuTopologyOutlineCreatesAnOrderedTreeInOneYamlUpdate()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "outline-tv",
            "Outline TV",
            "Samsung Test TV",
            "1000"));

        const string outline =
            """
            Normal video [normal-video]
            Settings [settings]
              Picture
                Smart Calibration {action}
                Expert Settings
                  Adaptive Picture {switch; default=on}
                  Brightness {slider; default=50; min=0; max=100; disabledWhen=adaptive-picture=on}
                  Unsupported Feature {submenu; disabled=true}
                    Unsupported Detail {slider; default=5; min=0; max=10}
                  Color Tone {selection; default=Warm2; options=Standard|Warm1|Warm2; hiddenWhen=adaptive-picture=on}
                  Contrast
                  Reset Picture {confirmation; default=Cancel; options=Reset|Cancel}
              Sound
                Sound Output {submenu-selection; default=TV Speaker; options=TV Speaker|Receiver|Bluetooth Speaker}
            """;
        var request = new MenuTopologyOutlineRequest("tv-interface", outline);
        var preview = controller.PreviewMenuTopologyOutline(request);

        Assert.Equal(14, preview.OutlineNodeCount);
        Assert.Equal(13, preview.AddedNodeCount);
        Assert.Equal(0, preview.RemovedNodeCount);
        await controller.ApplyMenuTopologyOutlineAsync(request);

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal(
            [
                "tv-interface", "normal-video", "settings", "picture",
                "smart-calibration", "expert-settings", "adaptive-picture", "brightness", "unsupported-feature", "unsupported-detail", "color-tone", "contrast", "reset-picture", "sound", "sound-output"
            ],
            snapshot.Nodes.Select(node => node.Id));
        var smartCalibration = snapshot.Nodes.Single(node => node.Id == "smart-calibration");
        Assert.Equal(MenuControlType.Action, smartCalibration.ControlType);
        Assert.Null(smartCalibration.DefaultValue);
        Assert.Equal("expert-settings", snapshot.Nodes.Single(node => node.Id == "brightness").ParentId);
        var adaptivePicture = snapshot.Nodes.Single(node => node.Id == "adaptive-picture");
        Assert.Equal(MenuControlType.Switch, adaptivePicture.ControlType);
        Assert.Equal("on", adaptivePicture.DefaultValue);
        var brightness = snapshot.Nodes.Single(node => node.Id == "brightness");
        Assert.Equal(MenuControlType.Slider, brightness.ControlType);
        Assert.Equal("50", brightness.DefaultValue);
        Assert.Equal(0m, brightness.MinimumValue);
        Assert.Equal(100m, brightness.MaximumValue);
        Assert.True(brightness.IsDisabledByDefault);
        Assert.Equal("adaptive-picture", Assert.Single(brightness.DisabledWhen).SettingNodeId);
        var unsupportedFeature = snapshot.Nodes.Single(node => node.Id == "unsupported-feature");
        Assert.True(unsupportedFeature.Disabled);
        Assert.True(unsupportedFeature.IsPermanentlyDisabled);
        Assert.True(unsupportedFeature.IsDisabledByDefault);
        var unsupportedDetail = snapshot.Nodes.Single(node => node.Id == "unsupported-detail");
        Assert.False(unsupportedDetail.Disabled);
        Assert.True(unsupportedDetail.IsPermanentlyDisabled);
        Assert.True(unsupportedDetail.IsDisabledByDefault);
        var colorTone = snapshot.Nodes.Single(node => node.Id == "color-tone");
        Assert.Equal(MenuControlType.Selection, colorTone.ControlType);
        Assert.Equal("Warm2", colorTone.DefaultValue);
        Assert.Equal(["Standard", "Warm1", "Warm2"], colorTone.SelectionOptions);
        Assert.True(colorTone.IsHiddenByDefault);
        Assert.Equal("adaptive-picture", Assert.Single(colorTone.HiddenWhen).SettingNodeId);
        var resetPicture = snapshot.Nodes.Single(node => node.Id == "reset-picture");
        Assert.Equal(MenuControlType.Confirmation, resetPicture.ControlType);
        Assert.Equal("Cancel", resetPicture.DefaultValue);
        Assert.Equal(["Reset", "Cancel"], resetPicture.SelectionOptions);
        var soundOutput = snapshot.Nodes.Single(node => node.Id == "sound-output");
        Assert.Equal(MenuControlType.SubmenuSelection, soundOutput.ControlType);
        Assert.Equal("TV Speaker", soundOutput.DefaultValue);
        Assert.Equal(
            ["TV Speaker", "Receiver", "Bluetooth Speaker"],
            soundOutput.SelectionOptions);
        Assert.All(snapshot.Nodes.Where(node => node.Id != "tv-interface"), node =>
            Assert.False(node.HasVerifiedRoute));

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal("Outline TV", reparsed.Name);
        Assert.Single(reparsed.Configurations);
        Assert.Equal("default", reparsed.Configurations.Values.Single().Id);

        var outlineWithoutContrast = string.Join(
            '\n',
            outline.Split('\n').Where(line => !line.Trim().Equals(
                "Contrast",
                StringComparison.Ordinal)));
        var removalRequest = new MenuTopologyOutlineRequest(
            "tv-interface",
            outlineWithoutContrast);
        var removalPreview = controller.PreviewMenuTopologyOutline(removalRequest);

        Assert.Equal(1, removalPreview.RemovedNodeCount);
        Assert.Contains(
            removalPreview.Changes,
            change => change.Contains("Contrast", StringComparison.Ordinal));

        await controller.ApplyMenuTopologyOutlineAsync(removalRequest);
        var afterRemoval = controller.GetMenuNavigationSnapshot();
        Assert.DoesNotContain(afterRemoval.Nodes, node => node.Id == "contrast");
    }

    [Fact]
    public async Task MenuTopologyOutlineFormattingErrorIdentifiesTheLineAndCorrection()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "format-error-tv",
            "Format Error TV",
            "Samsung Test TV",
            "1000"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            controller.PreviewMenuTopologyOutline(new MenuTopologyOutlineRequest(
                "tv-interface",
                "Settings\n   Picture")));

        Assert.Contains("line 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two spaces", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TopologySynchronizationProtectsItemsUsedByRecordedRoutes()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "protected-topology.yaml");
        await File.WriteAllTextAsync(definitionPath, VerifiedRemovalMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            controller.PreviewMenuTopologyOutline(new MenuTopologyOutlineRequest(
                "tv-interface",
                "Normal video [normal-video]",
                KeepUnlistedNodes: false)));

        Assert.Contains("used by recorded routes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Settings overlay", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveMenuConfigurationScopesVerifiedRoutesAndPersistsSelection()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "conditional-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ConditionalMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var standard = controller.GetMenuNavigationSnapshot();
        Assert.Equal("standard", standard.ActiveConfigurationId);
        Assert.True(standard.Nodes.Single(node => node.Id == "picture-clarity").HasVerifiedRoute);

        await controller.SetMenuConfigurationAsync("game-mode");

        var gameMode = controller.GetMenuNavigationSnapshot();
        Assert.Equal("game-mode", gameMode.ActiveConfigurationId);
        Assert.False(gameMode.Nodes.Single(node => node.Id == "picture-clarity").HasVerifiedRoute);
        Assert.Equal(MenuStateConfidence.Unknown, gameMode.State.Confidence);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(_directory, "settings.json")));
        Assert.Equal(
            "game-mode",
            settings.RootElement.GetProperty("MenuConfigurationId").GetString());
    }

    [Fact]
    public async Task FirstConfigurationSafelyScopesExistingLegacyRoutes()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "legacy-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ValidMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuConfigurationAsync(new MenuConfigurationEditRequest(
            "current-layout",
            "Current layout",
            "Game Mode = Off"));

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Equal("current-layout", snapshot.ActiveConfigurationId);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Equal("current-layout", Assert.Single(reparsed.Configurations.Values).Id);
        Assert.Equal("current-layout", Assert.Single(reparsed.Anchors.Values).ConfigurationId);
    }

    [Fact]
    public async Task VerifiedSettingRemovalDeletesItsSubtreeAndRelatedRoutes()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "verified-removal-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, VerifiedRemovalMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.DeleteVerifiedMenuSettingAsync("settings-overlay");

        var snapshot = controller.GetMenuNavigationSnapshot();
        Assert.Contains(snapshot.Nodes, node => node.Id == "normal-video");
        Assert.DoesNotContain(snapshot.Nodes, node => node.Id == "settings-overlay");
        Assert.DoesNotContain(snapshot.Nodes, node => node.Id == "expert-settings");
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(snapshot.DefinitionPath);
        Assert.Equal(2, reparsed.Nodes.Count);
        Assert.Empty(reparsed.Transitions);
        Assert.Null(reparsed.Anchors["normal-video"].ReturnStrategy);
        Assert.Contains(
            "Verified setting removed",
            controller.GetMenuAuthoringSnapshot().Status,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifiedKnownStateSettingCannotBeRemoved()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "known-state-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ValidMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteVerifiedMenuSettingAsync("normal-video"));

        Assert.Contains("known-state anchor", exception.Message, StringComparison.Ordinal);
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
        var timingRoute = Assert.Single(before.TimingTestRoutes);
        Assert.Equal("open-settings", timingRoute.Id);
        Assert.Equal("Normal video", timingRoute.SourcePath);
        Assert.Equal("Settings", timingRoute.TargetPath);
        Assert.True(timingRoute.HasCustomDelays);
        Assert.Equal("open-settings", candidate.Id);
        Assert.Equal("normal-video", candidate.SourceNodeId);
        Assert.Equal("settings", candidate.TargetNodeId);
        Assert.Equal(3, candidate.CommandCount);
        Assert.Collection(
            candidate.ReplaySteps,
            step =>
            {
                Assert.Equal(600, step.EffectiveDelayMilliseconds);
                Assert.False(step.HasCustomDelay);
            },
            step =>
            {
                Assert.Equal(700, step.EffectiveDelayMilliseconds);
                Assert.True(step.HasCustomDelay);
            },
            step => Assert.True(step.HasCustomDelay));

        await controller.DeleteMenuAuthoringDraftAsync(
            MenuAuthoringItemKind.Transition,
            candidate.Id);

        Assert.Empty(controller.GetMenuAuthoringSnapshot().DraftCandidates);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Empty(reparsed.Transitions);
    }

    [Fact]
    public async Task EmptyMenuRecordingCanBeCancelledWithoutChangingYaml()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var definitionPath = controller.GetMenuNavigationSnapshot().DefinitionPath;
            var yamlBefore = await File.ReadAllTextAsync(definitionPath);
            controller.StartMenuRecording(new MenuRecordingRequest(
                MenuAuthoringItemKind.Transition,
                string.Empty,
                "Picture",
                "settings",
                "picture",
                null,
                null));

            Assert.True(controller.GetMenuAuthoringSnapshot().IsRecording);
            Assert.Equal(0, controller.GetMenuAuthoringSnapshot().RecordedCommandCount);

            controller.CancelMenuRecording();

            var cancelled = controller.GetMenuAuthoringSnapshot();
            Assert.False(cancelled.IsRecording);
            Assert.Equal(0, cancelled.RecordedCommandCount);
            Assert.Contains("cancelled", cancelled.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(yamlBefore, await File.ReadAllTextAsync(definitionPath));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task SystemTimingAndPerButtonOverridesArePersistedToDraftYaml()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "timed-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, DraftMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.UpdateMenuAuthoringTimingAsync(
            MenuAuthoringItemKind.Transition,
            "open-settings",
            new MenuTimingProfile(175, 650, 325),
            [
                new MenuAuthoringReplayStepUpdate(1, false, 600),
                new MenuAuthoringReplayStepUpdate(2, false, 700),
                new MenuAuthoringReplayStepUpdate(3, true, 900)
            ]);

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Equal(new MenuTimingProfile(175, 650, 325, false), reparsed.Timing);
        var operations = reparsed.Transitions["open-settings"].Operations;
        Assert.Collection(
            operations,
            operation =>
            {
                Assert.Equal("KEY_MENU", operation.Key);
                Assert.Null(operation.DelayAfter);
            },
            operation =>
            {
                Assert.Equal("KEY_DOWN", operation.Key);
                Assert.Null(operation.DelayAfter);
            },
            operation =>
            {
                Assert.Equal("KEY_DOWN", operation.Key);
                Assert.Equal(TimeSpan.FromMilliseconds(900), operation.DelayAfter);
            });

        var snapshot = controller.GetMenuAuthoringSnapshot();
        Assert.Equal(new MenuTimingProfile(175, 650, 325, false), snapshot.Timing);
        Assert.Equal(0, snapshot.ValidationPasses);
        Assert.Contains("validation restarted", snapshot.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemTimingCanBeSavedWithoutChangingButtonOverrides()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "timing-only.yaml");
        await File.WriteAllTextAsync(definitionPath, DraftMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.UpdateMenuTimingProfileAsync(new MenuTimingProfile(
            225,
            750,
            400,
            AdjustmentDelayMilliseconds: 65));

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Equal(
            new MenuTimingProfile(
                225,
                750,
                400,
                false,
                AdjustmentDelayMilliseconds: 65),
            reparsed.Timing);
        Assert.Equal(TimeSpan.FromMilliseconds(65), reparsed.Timing.GetDelay("KEY_LEFT"));
        Assert.Equal(TimeSpan.FromMilliseconds(65), reparsed.Timing.GetDelay("KEY_RIGHT"));
        Assert.Equal(TimeSpan.FromMilliseconds(700), reparsed.Transitions["open-settings"].Operations[1].DelayAfter);
    }

    [Fact]
    public async Task ChangingSystemTimingInvalidatesItsPersistedVerification()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "verified-timing.yaml");
        var yaml = DraftMenuYaml.Replace(
            "  returnDelay: 300ms",
            "  returnDelay: 300ms\n  verified: true",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(definitionPath, yaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.UpdateMenuTimingProfileAsync(new MenuTimingProfile(125, 600, 300));
        var unchanged = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.False(unchanged.Timing.Verified);
        Assert.True(controller.GetMenuAuthoringSnapshot().Timing.Verified);

        await controller.UpdateMenuTimingProfileAsync(new MenuTimingProfile(
            125,
            600,
            300,
            AdjustmentDelayMilliseconds: 60));
        var changed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.False(changed.Timing.Verified);
        Assert.Equal(60, changed.Timing.AdjustmentDelayMilliseconds);
        Assert.Equal(0, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
    }

    [Fact]
    public async Task ReturnStrategyCanBeInferredAndSavedEntirelyThroughTheService()
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "return-menu.yaml");
        await File.WriteAllTextAsync(definitionPath, ReturnStrategyMenuYaml);
        await WriteSettingsAsync(definitionPath);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        var inferred = Assert.IsType<MenuReturnStrategySummary>(
            controller.GetMenuAuthoringSnapshot().ReturnStrategy);
        Assert.Equal("settings", inferred.MenuRootNodeId);
        Assert.Equal("KEY_RETURN", inferred.AtMenuRoot.Script);
        Assert.True(inferred.AtMenuRoot.Verified);
        Assert.Equal("KEY_MENU, KEY_RETURN", inferred.BelowMenuRoot.Script);
        Assert.False(inferred.BelowMenuRoot.Verified);

        await controller.UpdateMenuReturnStrategyAsync(
            ["KEY_RETURN"],
            ["KEY_MENU", "KEY_RETURN"]);

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        var persisted = Assert.IsType<MenuReturnStrategy>(
            reparsed.Anchors["normal"].ReturnStrategy);
        Assert.Equal("settings", persisted.MenuRootNodeId);
        Assert.False(persisted.AtMenuRoot.Verified);
        Assert.False(persisted.BelowMenuRoot.Verified);
        Assert.True(controller.GetMenuAuthoringSnapshot().ReturnStrategy!.AtMenuRoot.Verified);
        Assert.Equal(
            ["KEY_MENU", "KEY_RETURN"],
            persisted.BelowMenuRoot.Operations.Select(operation => operation.Key));
    }

    [Fact]
    public async Task DraftReplaySendsOnlyTheRecordedCommands()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.RunMenuAuthoringValidationAsync(
                MenuAuthoringItemKind.Transition,
                "open-picture-draft");

            Assert.Equal(["KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task DraftValidationPassesAreRetainedWhenAlternatingCommands()
    {
        var menuYaml = ExplicitValidationMenuYaml
            .Replace(
                """
                    children:
                      - id: picture
                        label: Picture
                """,
                """
                    children:
                      - id: picture
                        label: Picture
                      - id: sound
                        label: Sound
                """,
                StringComparison.Ordinal)
            .Replace(
                """
                  - id: open-picture-draft
                    from: settings
                    to: picture
                    verified: false
                    steps:
                      - key: KEY_DOWN
                      - key: KEY_ENTER
                """,
                """
                  - id: open-picture-draft
                    from: settings
                    to: picture
                    verified: false
                    steps:
                      - key: KEY_DOWN
                      - key: KEY_ENTER
                  - id: open-sound-draft
                    from: settings
                    to: sound
                    verified: false
                    steps:
                      - key: KEY_DOWN
                        repeat: 2
                      - key: KEY_ENTER
                """,
                StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(menuYaml);
        await using (controller)
        {
            await controller.RunMenuAuthoringValidationAsync(
                MenuAuthoringItemKind.Transition,
                "open-picture-draft");
            await controller.ConfirmMenuAuthoringValidationAsync(passed: true);

            await controller.RunMenuAuthoringValidationAsync(
                MenuAuthoringItemKind.Transition,
                "open-sound-draft");
            await controller.ConfirmMenuAuthoringValidationAsync(passed: true);

            await controller.RunMenuAuthoringValidationAsync(
                MenuAuthoringItemKind.Transition,
                "open-picture-draft");
            Assert.Equal(1, controller.GetMenuAuthoringSnapshot().ValidationPasses);
            await controller.ConfirmMenuAuthoringValidationAsync(passed: true);

            var candidates = controller.GetMenuAuthoringSnapshot().DraftCandidates
                .ToDictionary(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, candidates["open-picture-draft"].ValidationPasses);
            Assert.Equal(1, candidates["open-sound-draft"].ValidationPasses);
        }
    }

    [Fact]
    public async Task ThirdDraftApprovalAutomaticallyRunsReturnToVideo()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            for (var pass = 1; pass <= 3; pass++)
            {
                await controller.RunMenuAuthoringValidationAsync(
                    MenuAuthoringItemKind.Transition,
                    "open-picture-draft");
                transport.SentMessages.Clear();

                await controller.ConfirmMenuAuthoringValidationAsync(passed: true);

                if (pass < 3)
                {
                    Assert.Empty(GetSentKeys(transport));
                }
            }

            Assert.Equal(["KEY_EXIT", "KEY_EXIT"], GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Normal video", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, snapshot.MenuConfidence);
        }
    }

    [Fact]
    public async Task FailedAutomaticReturnPreservesTheJustVerifiedTargetState()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            for (var pass = 1; pass <= 3; pass++)
            {
                await controller.RunMenuAuthoringValidationAsync(
                    MenuAuthoringItemKind.Transition,
                    "open-picture-draft");
                if (pass == 3)
                {
                    transport.SendFailure = new IOException("Simulated return failure.");
                    var exception = await Assert.ThrowsAsync<IOException>(() =>
                        controller.ConfirmMenuAuthoringValidationAsync(passed: true));
                    Assert.Contains("return failure", exception.Message, StringComparison.OrdinalIgnoreCase);
                    break;
                }

                await controller.ConfirmMenuAuthoringValidationAsync(passed: true);
            }

            var snapshot = controller.GetSnapshot();
            Assert.Equal("Picture", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, snapshot.MenuConfidence);
            Assert.Contains(
                "current state remains",
                controller.GetMenuAuthoringSnapshot().Status,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TraversalAndRecordedReturnSharePreparationAndThreePassValidation()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            controller.StartMenuRecording(new MenuRecordingRequest(
                MenuAuthoringItemKind.Transition,
                string.Empty,
                "Picture",
                "settings",
                "picture",
                null,
                null,
                RecordReturnToVideo: true));
            await controller.SendKeyAsync("KEY_DOWN");
            await controller.SendKeyAsync("KEY_ENTER");
            controller.BeginReturnToVideoRecording();
            await controller.SendKeyAsync("KEY_HOME");
            await controller.StopAndSaveMenuRecordingAsync();

            var draft = Assert.Single(controller.GetMenuAuthoringSnapshot().DraftCandidates);
            Assert.Equal("open-picture-draft", draft.Id);
            Assert.Equal("settings", draft.SourceNodeId);
            Assert.Equal("picture", draft.TargetNodeId);
            Assert.Equal(2, draft.CommandCount);
            Assert.Equal(1, draft.ReturnCommandCount);

            var beforeValidation = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            var topologyBeforeValidation = await File.ReadAllTextAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            Assert.Equal(2, Assert.Single(beforeValidation.Anchors["normal"].Operations).Repeat);
            Assert.Equal(
                "KEY_HOME",
                Assert.Single(beforeValidation.Transitions["open-picture-draft"]
                    .ReturnToVideoOperations!).Key);

            for (var pass = 1; pass <= 3; pass++)
            {
                transport.SentMessages.Clear();
                await controller.PrepareMenuAuthoringValidationSourceAsync(
                    MenuAuthoringItemKind.Transition,
                    "open-picture-draft");
                Assert.Equal(["KEY_HOME", "KEY_MENU"], GetSentKeys(transport));
                Assert.Equal(
                    pass - 1,
                    controller.GetMenuAuthoringSnapshot().ValidationPasses);

                transport.SentMessages.Clear();
                await controller.RunMenuAuthoringValidationAsync(
                    MenuAuthoringItemKind.Transition,
                    "open-picture-draft");
                Assert.Equal(["KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
                await controller.ConfirmMenuAuthoringValidationAsync(passed: true);

                var confirmed = controller.GetSnapshot();
                Assert.Equal(MenuStateConfidence.Synchronized, confirmed.MenuConfidence);
                if (pass < 3)
                {
                    Assert.Equal("Picture", confirmed.MenuLabel);
                    Assert.Equal(
                        pass,
                        controller.GetMenuAuthoringSnapshot().DraftCandidates
                            .Single(candidate => candidate.Id == "open-picture-draft")
                            .ValidationPasses);

                    transport.SentMessages.Clear();
                    await controller.RunMenuAnchorAsync("normal");
                    Assert.Equal(["KEY_HOME"], GetSentKeys(transport));
                }
                else
                {
                    Assert.Equal("Normal video", confirmed.MenuLabel);
                    Assert.Equal(
                        ["KEY_DOWN", "KEY_ENTER", "KEY_HOME"],
                        GetSentKeys(transport));
                }
            }

            var verified = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            Assert.Equal(
                topologyBeforeValidation,
                await File.ReadAllTextAsync(
                    controller.GetMenuNavigationSnapshot().DefinitionPath));
            var transition = verified.Transitions["open-picture-draft"];
            Assert.False(transition.Verified);
            Assert.Equal(
                "KEY_HOME",
                Assert.Single(transition.ReturnToVideoOperations!).Key);
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.AuthoringItemId == "open-picture-draft").Verified);

            controller.CreateNavigationPlan("picture");
            await controller.ExecuteNavigationPlanAsync();
            transport.SentMessages.Clear();
            await controller.RunMenuAnchorAsync("normal");
            Assert.Equal(["KEY_HOME"], GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task LegacySeparateReturnDraftIsMigratedIntoItsTraversal()
    {
        var legacyYaml = ExplicitValidationMenuYaml.Replace(
            "transitions:",
            """
              - id: return-to-video-replacement
                label: Return to normal video (replacement)
                target: normal-video
                verified: false
                validationSource: picture
                steps:
                  - key: KEY_HOME
            transitions:
            """,
            StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(legacyYaml);
        await using (controller)
        {
            var candidate = Assert.Single(controller.GetMenuAuthoringSnapshot().DraftCandidates);
            Assert.Equal("open-picture-draft", candidate.Id);
            Assert.Equal(1, candidate.ReturnCommandCount);
            Assert.DoesNotContain(
                controller.GetMenuNavigationSnapshot().Anchors,
                anchor => anchor.Id == SamsungControllerService.ReturnToVideoReplacementAnchorId);

            transport.SentMessages.Clear();
            await controller.PrepareMenuAuthoringValidationSourceAsync(
                MenuAuthoringItemKind.Transition,
                "open-picture-draft");

            Assert.Equal(["KEY_HOME", "KEY_MENU"], GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task InstalledMenuReturnVerificationStaysOnVerificationPageAndPersistsBothResults()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal)
            .Replace("- key: KEY_EXIT\n        repeat: 2", "- key: KEY_MENU\n      - key: KEY_RETURN", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var path = controller.GetMenuNavigationSnapshot().DefinitionPath;
            var original = await File.ReadAllTextAsync(path);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services)
            {
                LineLabel = "Anchor · Return to normal video"
            };
            await renderer.StartAsync();

            Assert.DoesNotContain("Add missing definition", renderer.LineText);
            Assert.Contains("Go to matching return test", renderer.LineText);
            Assert.Equal("verification#verification-check-return:default:normal:below-root", renderer.LinkDestination);
            renderer.LineLabel = "Return to video · Below menu root";
            for (var pass = 0; pass < 3; pass++)
            {
                transport.SentMessages.Clear();
                await renderer.ClickAsync("Run test");
                Assert.Equal(["KEY_MENU", "KEY_RETURN"], GetSentKeys(transport));
                await renderer.ClickAsync("Count pass");
            }

            var snapshot = controller.GetMenuDefinitionVerificationSnapshot();
            Assert.True(snapshot.Checks.Single(check => check.Id == "anchor:default:normal").Verified);
            Assert.True(snapshot.Checks.Single(check => check.Id == "return:default:normal:below-root").Verified);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal(path, controller.GetMenuNavigationSnapshot().DefinitionPath);
            Assert.Single(Directory.GetFiles(Path.Combine(_directory, "menu-verifications"), "*.json"));
            Assert.False(Directory.Exists(Path.Combine(_directory, "menu-definitions")));

            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "anchor:default:normal").Verified);
            // Previously saved return evidence can fill the missing anchor result
            // without asking the user to repeat the visual test.
            await controller.RemoveMenuDefinitionVerificationCheckAsync("anchor:default:normal");
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "anchor:default:normal").ExistingEvidenceReady);
            transport.SentMessages.Clear();
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("anchor:default:normal");
            Assert.Empty(GetSentKeys(transport));
            await controller.RunMenuReturnStrategyTestAsync(MenuReturnScriptKind.BelowMenuRoot, "picture");
            await controller.ConfirmMenuReturnStrategyTestAsync(passed: false);
            Assert.False(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "anchor:default:normal").Verified);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task BuildReturnLineTestsInstalledMenuWithoutSavingScriptsOrChangingFallback()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var path = controller.GetMenuNavigationSnapshot().DefinitionPath;
            var original = await File.ReadAllTextAsync(path);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(new NoOpJavaScript()).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services)
            {
                LineLabel = "Return from Settings"
            };
            await renderer.StartAsync(typeof(MenuAuthoringStudio));
            for (var pass = 0; pass < 3; pass++)
            {
                transport.SentMessages.Clear();
                await renderer.ClickAsync("Test line item");
                Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
                await renderer.ClickAsync("Yes — count pass");
            }

            Assert.DoesNotContain("read-only", renderer.Text);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal("KEY_EXIT, KEY_EXIT", controller.GetMenuAuthoringSnapshot().ReturnStrategy!.FallbackScript);
            var anchorCheck = controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "anchor:default:normal");
            Assert.Null(anchorCheck.RelatedReturnCheckId);
            Assert.False(anchorCheck.Verified);
            Assert.Contains(controller.GetMenuAuthoringSnapshot().DraftCandidates, candidate =>
                candidate.Kind == MenuAuthoringItemKind.Anchor && candidate.Id == "normal");
            Assert.Single(Directory.GetFiles(Path.Combine(_directory, "menu-verifications"), "*.json"));
            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "return:default:normal:menu-root").Verified);
        }
    }

    private sealed class NoOpJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    [Fact]
    public async Task TimingPrepareButtonSendsDraftSetupAndShowsResultOnItsOwnLine()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services);
            await renderer.StartAsync();
            await renderer.SelectRouteAsync("open-picture-draft");
            Assert.Contains("START AT · Settings", renderer.TimingText);

            await renderer.ClickAsync("Prepare start");

            Assert.Equal(["KEY_EXIT", "KEY_EXIT", "KEY_MENU"], GetSentKeys(transport));
            Assert.Contains("Start prepared · Settings", renderer.TimingText);
            Assert.Contains("Preparation does not count a pass", renderer.TimingText);
            Assert.Equal(0, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);

            transport.SentMessages.Clear();
            await renderer.ClickAsync("Run test");
            Assert.Equal(["KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            await renderer.ClickAsync("Count pass");
            Assert.Equal(1, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
            Assert.Contains("Pass counted", renderer.TimingText);
        }
    }

    [Fact]
    public async Task TimingPrepareButtonShowsMissingPathErrorOnItsOwnLine()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal);
        var anchorStart = yaml.IndexOf("anchors:", StringComparison.Ordinal);
        var transitionStart = yaml.IndexOf("transitions:", StringComparison.Ordinal);
        yaml = yaml.Remove(anchorStart, transitionStart - anchorStart);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services);
            await renderer.StartAsync();
            await renderer.SelectRouteAsync("open-picture-draft");

            await renderer.ClickAsync("Prepare start");

            Assert.Empty(GetSentKeys(transport));
            Assert.Contains("No defined anchor and route can prepare starting state 'Settings'", renderer.TimingText);
            Assert.Contains("position the TV manually", renderer.TimingText);
            Assert.DoesNotContain("Start prepared", renderer.TimingText);
            Assert.Equal(0, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
        }
    }

    [Fact]
    public async Task TimingPreparationWorksBeforeAnchorAndSourceRouteAreVerified()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var path = controller.GetMenuNavigationSnapshot().DefinitionPath;
            var original = await File.ReadAllTextAsync(path);
            await controller.PrepareMenuTimingProfileTestSourceAsync("open-picture-draft");

            Assert.Equal(["KEY_EXIT", "KEY_EXIT", "KEY_MENU"], GetSentKeys(transport));
            Assert.Equal("settings", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(MenuStateConfidence.Probable, controller.GetSnapshot().MenuConfidence);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.All(controller.GetMenuNavigationSnapshot().Anchors, anchor => Assert.False(anchor.Verified));
            Assert.Equal(0, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);

            transport.SentMessages.Clear();
            await controller.RunMenuTimingProfileTestAsync("open-picture-draft", new MenuTimingProfile(50, 50, 50));
            Assert.Equal(["KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            await controller.ConfirmMenuTimingProfileTestAsync(passed: true);

            transport.SentMessages.Clear();
            await controller.PrepareMenuTimingProfileTestSourceAsync("open-settings");
            Assert.Equal(["KEY_MENU", "KEY_RETURN"], GetSentKeys(transport));
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(1, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
        }
    }

    [Fact]
    public async Task SystemTimingTestSendsOnlyTheSelectedTraversal()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.PrepareMenuTimingProfileTestSourceAsync("open-settings");
            Assert.Equal(["KEY_EXIT", "KEY_EXIT"], GetSentKeys(transport));

            transport.SentMessages.Clear();
            await controller.RunMenuTimingProfileTestAsync(
                "open-settings",
                new MenuTimingProfile(50, 50, 50));

            Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
            var timingCheck = Assert.Single(
                controller.GetMenuDefinitionVerificationSnapshot().Checks,
                check => check.Kind == MenuVerificationCheckKind.Timing);
            Assert.True(timingCheck.AwaitingValidationConfirmation);
            Assert.Equal(0, timingCheck.ValidationPasses);
        }
    }

    [Fact]
    public async Task PassedSystemTimingTestSynchronizesTargetForStateAwareReturn()
    {
        var menuYaml = ExplicitValidationMenuYaml.Replace(
            "- key: KEY_RETURN",
            "- key: KEY_MENU",
            StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(menuYaml);
        await using (controller)
        {
            for (var pass = 1; pass <= 3; pass++)
            {
                await controller.RunMenuTimingProfileTestAsync(
                    "open-settings",
                    new MenuTimingProfile(50, 50, 50));

                await controller.ConfirmMenuTimingProfileTestAsync(passed: true);

                var confirmed = controller.GetSnapshot();
                Assert.Equal("Settings", confirmed.MenuLabel);
                Assert.Equal(MenuStateConfidence.Synchronized, confirmed.MenuConfidence);
                Assert.Equal(pass, controller.GetMenuAuthoringSnapshot().TimingValidationPasses);
            }

            transport.SentMessages.Clear();
            await controller.RunMenuAnchorAsync("normal");

            Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ReturnTestSendsOnlyTheDisplayedScript()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.RunMenuReturnStrategyTestAsync(
                MenuReturnScriptKind.BelowMenuRoot,
                "picture");

            Assert.Equal(["KEY_MENU", "KEY_RETURN"], GetSentKeys(transport));
            var returnCheck = Assert.Single(
                controller.GetMenuDefinitionVerificationSnapshot().Checks,
                check => check.Kind == MenuVerificationCheckKind.ReturnScript
                         && check.ReturnScriptKind == MenuReturnScriptKind.BelowMenuRoot);
            Assert.True(returnCheck.AwaitingValidationConfirmation);
            Assert.Equal("picture", returnCheck.TargetNodeId);
        }
    }

    [Fact]
    public async Task StateSpecificReturnOverrideIsSavedTestedAndVerified()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.UpdateMenuReturnOverrideAsync("picture", ["KEY_EXIT"]);

            var draft = Assert.Single(
                controller.GetMenuAuthoringSnapshot().ReturnStrategy!.NodeOverrides);
            Assert.Equal("picture", draft.NodeId);
            Assert.False(draft.Verified);

            for (var pass = 1; pass <= 3; pass++)
            {
                transport.SentMessages.Clear();
                await controller.RunMenuReturnStrategyTestAsync(
                    MenuReturnScriptKind.NodeOverride,
                    "picture");
                Assert.Equal(["KEY_EXIT"], GetSentKeys(transport));
                await controller.ConfirmMenuReturnStrategyTestAsync(passed: true);
            }

            var verified = Assert.Single(
                controller.GetMenuAuthoringSnapshot().ReturnStrategy!.NodeOverrides);
            Assert.True(verified.Verified);
            var definition = await new MenuDefinitionParser().ParseFileAsync(
                controller.GetMenuNavigationSnapshot().DefinitionPath);
            Assert.False(Assert.Single(
                definition.Anchors["normal"].ReturnStrategy!.NodeOverrides!).Script.Verified);
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.ReturnScriptKind == MenuReturnScriptKind.NodeOverride
                && check.TargetNodeId == "picture").Verified);
        }
    }

    [Fact]
    public async Task ControllerSnapshotExposesTheCurrentMenuLabelWithoutItsFullPath()
    {
        var allVerifiedYaml = ExplicitValidationMenuYaml.Replace(
            "verified: false",
            "verified: true",
            StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(allVerifiedYaml);
        await using (controller)
        {
            await controller.RunMenuAnchorAsync("normal");
            controller.CreateNavigationPlan("picture");
            await controller.ExecuteNavigationPlanAsync();

            var snapshot = controller.GetSnapshot();
            Assert.Equal("Picture", snapshot.MenuLabel);
            Assert.Equal("Settings / Picture", snapshot.MenuPath);
            Assert.Equal(MenuStateConfidence.Probable, snapshot.MenuConfidence);
        }
    }

    [Fact]
    public async Task NavigateToMenuNodePlansAndExecutesItsVerifiedRoute()
    {
        var allVerifiedYaml = ExplicitValidationMenuYaml.Replace(
            "verified: false",
            "verified: true",
            StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(allVerifiedYaml);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("picture");

            Assert.Equal(["KEY_MENU", "KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Picture", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Probable, snapshot.MenuConfidence);
        }
    }

    [Fact]
    public async Task PictureSliderUpdatesNavigateAdjustAndHonorTheExitBehavior()
    {
        const string yaml =
            """
            version: 1
            id: picture-sliders
            name: Picture Sliders
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: settings
                    label: Settings
                    children:
                      - id: brightness
                        label: Brightness
                        controlType: slider
                        defaultValue: 25
                        minimumValue: 0
                        maximumValue: 50
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-brightness
                from: normal-video
                to: brightness
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuSliderValuesAsync(
                [new MenuSliderValueUpdate("brightness", 25, 27)],
                returnToNormalVideo: false);

            Assert.Equal(
                ["KEY_MENU", "KEY_DOWN", "KEY_RIGHT", "KEY_RIGHT"],
                GetSentKeys(transport));
            var stayed = controller.GetSnapshot();
            Assert.Equal("Brightness", stayed.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, stayed.MenuConfidence);

            transport.SentMessages.Clear();
            await controller.ApplyMenuSliderValuesAsync(
                [new MenuSliderValueUpdate("brightness", 27, 26)],
                returnToNormalVideo: true);

            Assert.Equal(["KEY_LEFT", "KEY_RETURN"], GetSentKeys(transport));
            var returned = controller.GetSnapshot();
            Assert.Equal("Normal video", returned.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, returned.MenuConfidence);
        }
    }

    [Fact]
    public async Task DisabledSubmenuStateFlowsToDescendantsAndRoutesReturnWhenEnabled()
    {
        const string yaml =
            """
            version: 1
            id: inherited-disabled-submenu
            name: Inherited Disabled Submenu
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: settings
                    label: Settings
                    children:
                      - id: advanced-enabled
                        label: Advanced Enabled
                        controlType: switch
                        defaultValue: off
                      - id: advanced
                        label: Advanced
                        disabledWhen:
                          - setting: advanced-enabled
                            equals: off
                        children:
                          - id: brightness
                            label: Brightness
                            controlType: slider
                            defaultValue: 50
                            minimumValue: 0
                            maximumValue: 100
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-settings
                from: normal-video
                to: settings
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-advanced-enabled
                from: normal-video
                to: advanced-enabled
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var initial = controller.GetMenuNavigationSnapshot();
            var advanced = initial.Nodes.Single(node => node.Id == "advanced");
            var brightness = initial.Nodes.Single(node => node.Id == "brightness");

            Assert.True(advanced.IsDisabledByDefault);
            Assert.True(brightness.IsDisabledByDefault);
            Assert.Empty(brightness.DisabledWhen);
            Assert.False(brightness.HasVerifiedRoute);
            var unavailable = Assert.Throws<InvalidOperationException>(
                () => controller.CreateNavigationPlan("brightness"));
            Assert.Contains("disabled", unavailable.Message, StringComparison.OrdinalIgnoreCase);

            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("advanced-enabled", "off", "on")],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["advanced-enabled"] = "off",
                    ["brightness"] = "50"
                },
                returnToNormalVideo: false);

            var enabled = controller.GetMenuNavigationSnapshot();
            Assert.True(enabled.Nodes.Single(node => node.Id == "brightness").HasVerifiedRoute);

            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("brightness", "50", "51")],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["advanced-enabled"] = "on",
                    ["brightness"] = "50"
                },
                returnToNormalVideo: false);

            Assert.Contains("KEY_RIGHT", GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task PictureControlsApplyEnablingSwitchBeforeConditionalSlider()
    {
        const string yaml =
            """
            version: 1
            id: conditional-picture-switch
            name: Conditional Picture Switch
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture
                    label: Picture
                    children:
                      - id: twenty-point
                        label: 20 Point
                        children:
                          - id: twenty-point-enabled
                            label: 20 Point
                            controlType: switch
                            defaultValue: off
                          - id: unavailable
                            label: Unavailable
                            disabled: true
                          - id: red
                            label: Red
                            controlType: slider
                            defaultValue: 0
                            minimumValue: -50
                            maximumValue: 50
                            disabledWhen:
                              - setting: twenty-point-enabled
                                equals: off
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-twenty-point
                from: normal-video
                to: twenty-point
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
              - id: open-twenty-point-toggle
                from: normal-video
                to: twenty-point-enabled
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuControlValuesAsync(
                [
                    new MenuControlValueUpdate("red", "0", "2"),
                    new MenuControlValueUpdate("twenty-point-enabled", "off", "on")
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["twenty-point-enabled"] = "off",
                    ["red"] = "0"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                ["KEY_MENU", "KEY_ENTER", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_RIGHT", "KEY_RIGHT"],
                GetSentKeys(transport));
            var result = controller.GetSnapshot();
            Assert.Equal("Red", result.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, result.MenuConfidence);
        }
    }

    [Fact]
    public async Task PictureControlsApplyEnablingSelectionBeforeConditionalSlider()
    {
        const string yaml =
            """
            version: 1
            id: conditional-picture-selection
            name: Conditional Picture Selection
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture
                    label: Picture
                    children:
                      - id: color-space-settings
                        label: Color Space Settings
                        children:
                          - id: color-space
                            label: Color Space
                            controlType: selection
                            defaultValue: Auto
                            options: [Auto, Normal, Native, Custom]
                          - id: red
                            label: Red
                            controlType: slider
                            defaultValue: 50
                            minimumValue: 0
                            maximumValue: 100
                            disabledWhen:
                              - setting: color-space
                                equals: Auto
                              - setting: color-space
                                equals: Normal
                              - setting: color-space
                                equals: Native
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-color-space-settings
                from: normal-video
                to: color-space-settings
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
              - id: open-color-space
                from: normal-video
                to: color-space
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuControlValuesAsync(
                [
                    new MenuControlValueUpdate("red", "50", "52"),
                    new MenuControlValueUpdate("color-space", "Auto", "Custom")
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["color-space"] = "Auto",
                    ["red"] = "50"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                [
                    "KEY_MENU", "KEY_ENTER",
                    "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_DOWN", "KEY_ENTER",
                    "KEY_DOWN", "KEY_RIGHT", "KEY_RIGHT"
                ],
                GetSentKeys(transport));
            var result = controller.GetSnapshot();
            Assert.Equal("Red", result.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, result.MenuConfidence);
        }
    }

    [Fact]
    public async Task SubmenuSelectionChoosesValueAndReturnsToContainingMenu()
    {
        const string yaml =
            """
            version: 1
            id: submenu-selection
            name: Submenu Selection
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: sound-output
                    label: Sound Output
                    controlType: submenu-selection
                    defaultValue: TV Speaker
                    options: [TV Speaker, Receiver, Bluetooth Speaker]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-sound-output
                from: normal-video
                to: sound-output
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var verification = controller.GetMenuControlVerificationSnapshot();
            Assert.Equal(1, verification.RequiredSelectionCount);

            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("sound-output", "TV Speaker", "Bluetooth Speaker")],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sound-output"] = "TV Speaker"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                ["KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_ENTER", "KEY_RETURN"],
                GetSentKeys(transport));
            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("Bluetooth Speaker", navigation.ControlValues["sound-output"]);
            Assert.Equal("sound-output", navigation.State.NodeId);

            verification = await controller.ConfirmMenuSelectionBehaviorAsync("sound-output");
            Assert.True(verification.SelectionsVerified);
        }
    }

    [Fact]
    public async Task IndexedSelectionAppliesEveryFixedRowWithoutExposingSelectorChoice()
    {
        const string yaml =
            """
            version: 1
            id: indexed-selection
            name: Indexed Selection
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: white-balance
                    label: 20 Point White Balance
                    children:
                      - id: interval
                        label: Interval
                        controlType: selection
                        defaultValue: 5%
                        options: [5%, 10%, 15%]
                      - id: red
                        label: Red
                        controlType: slider
                        defaultValue: 0
                        minimumValue: -50
                        maximumValue: 50
                      - id: green
                        label: Green
                        controlType: slider
                        defaultValue: 0
                        minimumValue: -50
                        maximumValue: 50
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-interval
                from: normal-video
                to: interval
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-red
                from: normal-video
                to: red
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
              - id: open-green
                from: normal-video
                to: green
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
                    repeat: 2
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyIndexedMenuControlValuesAsync(
                [
                    new MenuIndexedControlValueUpdate("interval", "5%", "red", "0", "1"),
                    new MenuIndexedControlValueUpdate("interval", "10%", "red", "0", "2"),
                    new MenuIndexedControlValueUpdate("interval", "10%", "green", "0", "-1")
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["interval"] = "5%",
                    ["red"] = "0",
                    ["green"] = "0"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                [
                    "KEY_MENU", "KEY_DOWN", "KEY_RIGHT",
                    "KEY_UP", "KEY_ENTER", "KEY_DOWN", "KEY_ENTER",
                    "KEY_DOWN", "KEY_RIGHT", "KEY_RIGHT",
                    "KEY_DOWN", "KEY_LEFT"
                ],
                GetSentKeys(transport));
            var snapshot = controller.GetMenuNavigationSnapshot();
            Assert.Equal("10%", snapshot.ControlValues["interval"]);
            Assert.Equal("2", snapshot.ControlValues["red"]);
            Assert.Equal("-1", snapshot.ControlValues["green"]);

            var verification = await controller.ConfirmMenuSelectionBehaviorAsync("interval");
            Assert.Contains("interval", verification.ConfirmedSelectionNodeIds);
            Assert.Contains(
                MenuControlType.IndexedSelection,
                verification.VerifiedSelectionControlTypes);
            await controller.SaveMenuControlProfileAsync(
            [
                new MenuControlProfileValue("red", "1", "interval", "5%"),
                new MenuControlProfileValue("red", "2", "interval", "10%"),
                new MenuControlProfileValue("green", "-1", "interval", "10%")
            ]);
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        var profile = reloaded.GetMenuControlProfileSnapshot();
        Assert.Equal("indexed-selection", profile.DefinitionId);
        Assert.Equal(3, profile.Values.Count);
    }

    [Fact]
    public async Task FactoryResetConfirmationRestoresDeclaredControlDefaults()
    {
        const string yaml =
            """
            version: 1
            id: factory-reset
            name: Factory Reset
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture
                    label: Picture
                    children:
                      - id: brightness
                        label: Brightness
                        controlType: slider
                        defaultValue: 25
                        minimumValue: 0
                        maximumValue: 50
                      - id: reset-picture
                        label: Reset Picture
                        controlType: confirmation
                        defaultValue: Reset
                        options: [Reset, Cancel]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-brightness
                from: normal-video
                to: brightness
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-reset-picture
                from: normal-video
                to: reset-picture
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("brightness", "25", "27")],
                new Dictionary<string, string> { ["brightness"] = "25" },
                returnToNormalVideo: true);
            Assert.Equal("27", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            transport.SentMessages.Clear();

            await controller.ResetMenuControlsToFactoryDefaultsAsync("reset-picture", "Reset");

            Assert.Equal(
                ["KEY_MENU", "KEY_DOWN", "KEY_ENTER", "KEY_ENTER", "KEY_RETURN"],
                GetSentKeys(transport));
            var snapshot = controller.GetMenuNavigationSnapshot();
            Assert.Equal("25", snapshot.ControlValues["brightness"]);
            Assert.Equal("normal-video", snapshot.State.NodeId);
        }
    }

    [Fact]
    public async Task MenuControlsRecalculateOffsetsWhenASelectionHidesASibling()
    {
        const string yaml =
            """
            version: 1
            id: hidden-picture-selection
            name: Hidden Picture Selection
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture
                    label: Picture
                    children:
                      - id: picture-mode
                        label: Picture Mode
                        controlType: selection
                        defaultValue: Standard
                        options: [Standard, Movie]
                      - id: dynamic-detail
                        label: Dynamic Detail
                        controlType: slider
                        defaultValue: 0
                        minimumValue: 0
                        maximumValue: 10
                        hiddenWhen:
                          - setting: picture-mode
                            equals: Standard
                      - id: sharpness
                        label: Sharpness
                        controlType: slider
                        defaultValue: 0
                        minimumValue: 0
                        maximumValue: 10
                        disabledWhen:
                          - setting: picture-mode
                            equals: Movie
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-picture
                from: normal-video
                to: picture
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-picture-mode
                from: normal-video
                to: picture-mode
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuControlValuesAsync(
                [
                    new MenuControlValueUpdate("dynamic-detail", "0", "2"),
                    new MenuControlValueUpdate("picture-mode", "Standard", "Movie")
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["picture-mode"] = "Standard",
                    ["dynamic-detail"] = "0",
                    ["sharpness"] = "0"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                [
                    "KEY_MENU", "KEY_ENTER",
                    "KEY_ENTER", "KEY_DOWN", "KEY_ENTER",
                    "KEY_DOWN", "KEY_RIGHT", "KEY_RIGHT"
                ],
                GetSentKeys(transport));

            transport.SentMessages.Clear();
            await controller.ApplyMenuControlValuesAsync(
                [
                    new MenuControlValueUpdate("picture-mode", "Movie", "Standard"),
                    new MenuControlValueUpdate("sharpness", "0", "1")
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["picture-mode"] = "Movie",
                    ["dynamic-detail"] = "2",
                    ["sharpness"] = "0"
                },
                returnToNormalVideo: false);

            Assert.Equal(
                ["KEY_ENTER", "KEY_UP", "KEY_ENTER", "KEY_DOWN", "KEY_RIGHT"],
                GetSentKeys(transport).TakeLast(5));
            var snapshot = controller.GetMenuNavigationSnapshot();
            Assert.Equal("Standard", snapshot.ControlValues["picture-mode"]);
            Assert.True(snapshot.Nodes.Single(node => node.Id == "dynamic-detail").IsHiddenByDefault);
        }
    }

    [Fact]
    public async Task VerifiedMenuNavigationUsesPredictedHiddenSiblingOffsets()
    {
        const string yaml =
            """
            version: 1
            id: hidden-navigation
            name: Hidden Navigation
            model: Test TV
            timing:
              defaultDelay: 50ms
              screenChangeDelay: 50ms
              returnDelay: 50ms
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: settings
                    label: Settings
                    children:
                      - id: game-mode
                        label: Game Mode
                        controlType: switch
                        defaultValue: off
                      - id: optional-tools
                        label: Optional Tools
                        hiddenWhen:
                          - setting: game-mode
                            equals: on
                      - id: sound
                        label: Sound
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-settings
                from: normal-video
                to: settings
                verified: true
                steps:
                  - key: KEY_MENU
              - id: topology-open-settings-to-game-mode
                from: normal-video
                to: game-mode
                verified: true
                generatedFromTopology: true
                topologySeed: open-settings
                validationGroup: topology-open-settings-game-mode
                validationRoute: true
                steps:
                  - key: KEY_MENU
              - id: topology-open-settings-to-optional-tools
                from: normal-video
                to: optional-tools
                verified: true
                generatedFromTopology: true
                topologySeed: open-settings
                validationGroup: topology-open-settings-optional-tools
                validationRoute: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
                  - key: KEY_ENTER
              - id: topology-open-settings-to-sound
                from: normal-video
                to: sound
                verified: true
                generatedFromTopology: true
                topologySeed: open-settings
                validationGroup: topology-open-settings-sound
                validationRoute: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
                    repeat: 2
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("game-mode", "off", "on")],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["game-mode"] = "off"
                },
                returnToNormalVideo: false);

            transport.SentMessages.Clear();
            var plan = controller.CreateNavigationPlan("sound");
            await controller.ExecuteNavigationPlanAsync();

            Assert.True(plan.UsesCalculatedRoute);
            Assert.Equal(["KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            Assert.Equal("on", controller.GetMenuNavigationSnapshot().ControlValues["game-mode"]);
        }
    }

    [Fact]
    public async Task SliderBehaviorVerificationPromotesAfterThreeDifferentSlidersAndPersists()
    {
        const string yaml =
            """
            version: 1
            id: slider-verification
            name: Slider Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: contrast
                    label: Contrast
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: color
                    label: Color
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions: []
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var initial = controller.GetMenuControlVerificationSnapshot();
            Assert.False(initial.SlidersVerified);
            Assert.Equal(0, initial.ConfirmedSliderCount);

            var first = await controller.ConfirmMenuSliderBehaviorAsync("brightness");
            var duplicate = await controller.ConfirmMenuSliderBehaviorAsync("brightness");
            var second = await controller.ConfirmMenuSliderBehaviorAsync("contrast");
            var promoted = await controller.ConfirmMenuSliderBehaviorAsync("color");

            Assert.Equal(1, first.ConfirmedSliderCount);
            Assert.Equal(1, duplicate.ConfirmedSliderCount);
            Assert.Equal(2, second.ConfirmedSliderCount);
            Assert.True(promoted.SlidersVerified);
            Assert.Equal(3, promoted.ConfirmedSliderCount);
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        Assert.True(reloaded.GetMenuControlVerificationSnapshot().SlidersVerified);

        await reloaded.UpdateMenuTimingProfileAsync(new MenuTimingProfile(
            DefaultDelayMilliseconds: 151,
            ScreenChangeDelayMilliseconds: 800,
            ReturnDelayMilliseconds: 300));
        Assert.Equal(0, reloaded.GetMenuControlVerificationSnapshot().ConfirmedSliderCount);

        await reloaded.ConfirmMenuSliderBehaviorAsync("brightness");
        await reloaded.ConfirmMenuSliderBehaviorAsync("contrast");
        await reloaded.ConfirmMenuSliderBehaviorAsync("color");
        Assert.True(reloaded.GetMenuControlVerificationSnapshot().SlidersVerified);

        await reloaded.ResetMenuSliderBehaviorVerificationAsync();
        var reset = reloaded.GetMenuControlVerificationSnapshot();
        Assert.False(reset.SlidersVerified);
        Assert.Equal(0, reset.ConfirmedSliderCount);
    }

    [Fact]
    public async Task NamedTvStatesPersistAndLoadAsPredictionBaselineWithoutSendingKeys()
    {
        const string yaml =
            """
            version: 1
            id: saved-tv-state
            name: Saved TV State
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: contrast-enhancer
                    label: Contrast Enhancer
                    controlType: switch
                    defaultValue: off
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-brightness
                from: normal-video
                to: brightness
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-contrast-enhancer
                from: normal-video
                to: contrast-enhancer
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml);
        string stateId;
        await using (controller)
        {
            var state = await controller.SaveCurrentMenuControlStateAsync(
                "Calibrated night",
                [
                    new MenuControlProfileValue("brightness", "17"),
                    new MenuControlProfileValue("contrast-enhancer", "on")
                ]);
            stateId = state.Id;

            Assert.Empty(GetSentKeys(transport));
            Assert.Equal("17", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("on", controller.GetMenuNavigationSnapshot().ControlValues["contrast-enhancer"]);
            var summary = Assert.Single(controller.GetSavedMenuControlStates());
            Assert.Equal("Calibrated night", summary.Name);
            Assert.Equal(2, summary.ValueCount);

            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("brightness", "17", "18")],
                new Dictionary<string, string>
                {
                    ["brightness"] = "17",
                    ["contrast-enhancer"] = "on"
                },
                returnToNormalVideo: false);
            transport.SentMessages.Clear();

            var loaded = await controller.LoadMenuControlStateAsync(stateId);

            Assert.Equal("Calibrated night", loaded.Name);
            Assert.Equal("17", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(GetSentKeys(transport));
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        Assert.Equal(stateId, Assert.Single(reloaded.GetSavedMenuControlStates()).Id);
        await reloaded.DeleteMenuControlStateAsync(stateId);
        Assert.Empty(reloaded.GetSavedMenuControlStates());
    }

    [Fact]
    public async Task SelectionBehaviorVerificationTracksEachInteractionTypeAndPersists()
    {
        const string yaml =
            """
            version: 1
            id: selection-verification
            name: Selection Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture-mode
                    label: Picture Mode
                    controlType: selection
                    defaultValue: Standard
                    options: [Standard, Movie, Filmmaker Mode]
                  - id: color-tone
                    label: Color Tone
                    controlType: selection
                    defaultValue: Standard
                    options: [Standard, Warm1, Warm2]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-picture-mode
                from: normal-video
                to: picture-mode
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-color-tone
                from: normal-video
                to: color-tone
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var initial = controller.GetMenuControlVerificationSnapshot();
            Assert.False(initial.SelectionsVerified);
            Assert.Equal(0, initial.ConfirmedSelectionCount);
            Assert.Equal(1, initial.RequiredSelectionCount);

            var first = await controller.ConfirmMenuSelectionBehaviorAsync("picture-mode");
            var duplicate = await controller.ConfirmMenuSelectionBehaviorAsync("picture-mode");
            var completed = await controller.ConfirmMenuSelectionBehaviorAsync("color-tone");

            Assert.Equal(1, first.ConfirmedSelectionCount);
            Assert.True(first.SelectionsVerified);
            Assert.Equal(1, duplicate.ConfirmedSelectionCount);
            Assert.True(completed.SelectionsVerified);
            Assert.Equal(1, completed.ConfirmedSelectionCount);
            Assert.Contains(MenuControlType.Selection, completed.VerifiedSelectionControlTypes);

            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            var carried = await controller.CarryForwardExistingMenuVerificationAsync();
            Assert.True(carried.Checks.Single(check =>
                check.Id == "control:selection-behavior").Verified);
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        Assert.True(reloaded.GetMenuControlVerificationSnapshot().SelectionsVerified);

        await reloaded.UpdateMenuTimingProfileAsync(new MenuTimingProfile(
            DefaultDelayMilliseconds: 151,
            ScreenChangeDelayMilliseconds: 800,
            ReturnDelayMilliseconds: 300));
        Assert.Equal(0, reloaded.GetMenuControlVerificationSnapshot().ConfirmedSelectionCount);

        await reloaded.ConfirmMenuSelectionBehaviorAsync("picture-mode");
        await reloaded.ResetMenuSelectionBehaviorVerificationAsync();
        var reset = reloaded.GetMenuControlVerificationSnapshot();
        Assert.False(reset.SelectionsVerified);
        Assert.Equal(0, reset.ConfirmedSelectionCount);
    }

    [Fact]
    public async Task FileVerificationGuidedSliderTestsAutomaticallyUseDistinctRepresentatives()
    {
        const string yaml =
            """
            version: 1
            id: guided-slider-verification
            name: Guided Slider Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: contrast
                    label: Contrast
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: color
                    label: Color
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-brightness
                from: normal-video
                to: brightness
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-contrast
                from: normal-video
                to: contrast
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
              - id: open-color
                from: normal-video
                to: color
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
                    repeat: 2
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var menuPath = controller.GetMenuNavigationSnapshot().DefinitionPath;
            var topologyBeforeVerification = await File.ReadAllTextAsync(menuPath);
            var testedNodeIds = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                var result = await controller.RunMenuDefinitionVerificationTestAsync(
                    "control:slider-behavior");
                testedNodeIds.Add(result.TargetNodeId);
                Assert.Equal(MenuControlType.Slider, result.ControlType);
                Assert.Contains("Changed", result.ActionDescription, StringComparison.Ordinal);

                Assert.Equal(
                    1,
                    await controller.RestoreMenuDefinitionVerificationTestAsync(result));
                var verification = await controller.ConfirmMenuSliderBehaviorAsync(
                    result.TargetNodeId);
                Assert.Equal(index + 1, verification.ConfirmedSliderCount);
            }

            Assert.Equal(3, testedNodeIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(controller.GetMenuControlVerificationSnapshot().SlidersVerified);
            Assert.Equal(3, GetSentKeys(transport).Count(key => key == "KEY_RIGHT"));
            var completed = await controller.ConfirmMenuDefinitionVerificationCheckAsync(
                "control:slider-behavior");
            Assert.True(completed.Checks.Single(check =>
                check.Id == "control:slider-behavior").Verified);
            Assert.Equal(
                topologyBeforeVerification,
                await File.ReadAllTextAsync(menuPath));

            var knownState = await controller.ReturnMenuDefinitionVerificationToKnownStateAsync();

            Assert.Equal("Normal video", knownState);
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal("KEY_RETURN", GetSentKeys(transport).Last());
        }
    }

    [Fact]
    public async Task FileVerificationGuidedConfirmationAlwaysUsesSafeCancelChoice()
    {
        const string yaml =
            """
            version: 1
            id: guided-confirmation-verification
            name: Guided Confirmation Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: pixel-refresh
                    label: Pixel Refresh
                    controlType: confirmation
                    defaultValue: Start Now
                    options: [Start Now, Start After TV Off]
                  - id: discard-changes
                    label: Discard Changes
                    controlType: confirmation
                    defaultValue: Apply
                    options: [Apply, Cancel]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-pixel-refresh
                from: normal-video
                to: pixel-refresh
                verified: true
                steps:
                  - key: KEY_HOME
              - id: open-discard-changes
                from: normal-video
                to: discard-changes
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var result = await controller.RunMenuDefinitionVerificationTestAsync(
                "control:confirmation-behavior");

            Assert.Equal("discard-changes", result.TargetNodeId);
            Assert.Equal(MenuControlType.Confirmation, result.ControlType);
            Assert.Contains("selected Cancel", result.ActionDescription, StringComparison.Ordinal);
            Assert.Equal(
                ["KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_ENTER"],
                GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task FileVerificationDoesNotRequireResetOrDestructiveConfirmations()
    {
        const string yaml =
            """
            version: 1
            id: destructive-confirmations
            name: Destructive Confirmations
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: reset-picture
                    label: Reset Picture
                    controlType: confirmation
                    defaultValue: Reset
                    options: [Reset, Cancel]
                  - id: pixel-refresh
                    label: Pixel Refresh
                    controlType: confirmation
                    defaultValue: Start Now
                    options: [Start Now, Start After TV Off]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-reset-picture
                from: normal-video
                to: reset-picture
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-pixel-refresh
                from: normal-video
                to: pixel-refresh
                verified: true
                steps:
                  - key: KEY_HOME
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var checks = controller.GetMenuDefinitionVerificationSnapshot().Checks;
            Assert.DoesNotContain(
                checks,
                check => check.Kind == MenuVerificationCheckKind.Confirmation);
            Assert.DoesNotContain(
                checks,
                check => check.TargetNodeId is "reset-picture" or "pixel-refresh");
        }
    }

    [Fact]
    public async Task FileVerificationGuidedTestExercisesCalculatedCrossBranchRoute()
    {
        const string yaml =
            """
            version: 1
            id: calculated-navigation-verification
            name: Calculated Navigation Verification
            model: Test TV
            timing:
              defaultDelay: 150ms
              screenChangeDelay: 800ms
              returnDelay: 300ms
              verified: true
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: white-balance
                    label: White Balance
                    children:
                      - id: two-point
                        label: 2 Point
                        children:
                          - id: two-point-red
                            label: Red Gain
                            controlType: action
                      - id: twenty-point
                        label: 20 Point
                        children:
                          - id: twenty-point-red
                            label: Red
                            controlType: action
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: to-two-point-red
                from: normal-video
                to: two-point-red
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
                    repeat: 2
              - id: to-twenty-point-red
                from: normal-video
                to: twenty-point-red
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
                  - key: KEY_DOWN
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var check = Assert.Single(
                controller.GetMenuDefinitionVerificationSnapshot().Checks,
                candidate => candidate.Kind == MenuVerificationCheckKind.CalculatedNavigation);

            var result = await controller.RunMenuDefinitionVerificationTestAsync(check.Id);

            Assert.Equal(check.TargetNodeId, result.TargetNodeId);
            Assert.Contains("without returning to normal video", result.ActionDescription);
            Assert.Contains(
                "row highlighted; do not open or change it",
                result.ActionDescription);
            Assert.Equal(
                [
                    "KEY_RETURN",
                    "KEY_MENU",
                    "KEY_ENTER",
                    "KEY_DOWN",
                    "KEY_ENTER",
                    "KEY_RETURN",
                    "KEY_UP",
                    "KEY_ENTER"
                ],
                GetSentKeys(transport));
            Assert.Equal(
                check.TargetNodeId,
                controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task CalculatedNavigationVerificationSurvivesReconnectAfterConfigurationActivation()
    {
        const string yaml =
            """
            version: 1
            id: configured-calculated-navigation
            name: Configured Calculated Navigation
            model: Test TV
            configurations:
              - id: default
                name: Default
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: white-balance
                    label: White Balance
                    children:
                      - id: two-point
                        label: 2 Point
                        children:
                          - id: two-point-red
                            label: Red Gain
                            controlType: action
                      - id: twenty-point
                        label: 20 Point
                        children:
                          - id: twenty-point-red
                            label: Red
                            controlType: action
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                configuration: default
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: to-two-point-red
                from: normal-video
                to: two-point-red
                configuration: default
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
                    repeat: 2
              - id: to-twenty-point-red
                from: normal-video
                to: twenty-point-red
                configuration: default
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
                  - key: KEY_DOWN
                  - key: KEY_ENTER
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var check = Assert.Single(
                controller.GetMenuDefinitionVerificationSnapshot().Checks,
                candidate => candidate.Kind == MenuVerificationCheckKind.CalculatedNavigation);
            var confirmed = await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            Assert.True(confirmed.Checks.Single(candidate => candidate.Id == check.Id).Verified);
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        var restored = Assert.Single(
            reloaded.GetMenuDefinitionVerificationSnapshot().Checks,
            candidate => candidate.Kind == MenuVerificationCheckKind.CalculatedNavigation);
        Assert.True(restored.Verified);
    }

    [Fact]
    public async Task FileVerificationGuidedSelectionEnablesConditionalPrerequisites()
    {
        const string yaml =
            """
            version: 1
            id: guided-indexed-selection-verification
            name: Guided Indexed Selection Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: white-balance
                    label: White Balance
                    children:
                      - id: twenty-point
                        label: 20 Point
                        children:
                          - id: twenty-point-enabled
                            label: 20 Point
                            controlType: switch
                            defaultValue: off
                          - id: interval
                            label: Interval
                            controlType: selection
                            defaultValue: 5%
                            options: [5%, 10%, 15%]
                            disabledWhen:
                              - setting: twenty-point-enabled
                                equals: off
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-twenty-point
                from: normal-video
                to: twenty-point
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-twenty-point-enabled
                from: normal-video
                to: twenty-point-enabled
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
            """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var result = await controller.RunMenuDefinitionVerificationTestAsync(
                "control:indexed-selection-behavior");

            Assert.Equal("interval", result.TargetNodeId);
            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("on", navigation.ControlValues["twenty-point-enabled"]);
            Assert.Equal("10%", navigation.ControlValues["interval"]);
            Assert.Contains("KEY_ENTER", GetSentKeys(transport));
            Assert.Contains("KEY_DOWN", GetSentKeys(transport));

            Assert.Equal(
                2,
                await controller.RestoreMenuDefinitionVerificationTestAsync(result));
            navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("off", navigation.ControlValues["twenty-point-enabled"]);
            Assert.Equal("5%", navigation.ControlValues["interval"]);
        }
    }

    [Fact]
    public async Task FileVerificationGuidedConditionalTestSetsControllerAndOpensAffectedMenu()
    {
        const string yaml =
            """
            version: 1
            id: guided-condition-verification
            name: Guided Condition Verification
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: settings
                    label: Settings
                    children:
                      - id: master
                        label: Master
                        controlType: switch
                        defaultValue: off
                      - id: autorun
                        label: Autorun
                        controlType: selection
                        defaultValue: Off
                        options: [Off, On]
                        disabledWhen:
                          - setting: master
                            equals: off
                      - id: dependent-row
                        label: Dependent Row
                        controlType: action
                        disabledWhen:
                          - setting: autorun
                            equals: On
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-settings
                from: normal-video
                to: settings
                verified: true
                steps:
                  - key: KEY_MENU
              - id: open-master
                from: normal-video
                to: master
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
              - id: open-autorun
                from: normal-video
                to: autorun
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_ENTER
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var result = await controller.RunMenuDefinitionVerificationTestAsync(
                "condition:disabled-behavior");

            Assert.Equal("dependent-row", result.TargetNodeId);
            Assert.Contains("visible but gray", result.ActionDescription, StringComparison.Ordinal);
            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("on", navigation.ControlValues["master"]);
            Assert.Equal("On", navigation.ControlValues["autorun"]);
            Assert.Equal("settings", navigation.State.NodeId);

            Assert.Equal(
                2,
                await controller.RestoreMenuDefinitionVerificationTestAsync(result));
            navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("off", navigation.ControlValues["master"]);
            Assert.Equal("Off", navigation.ControlValues["autorun"]);
        }
    }

    [Fact]
    public async Task ExternalSignalSelectionImmediatelyControlsMenuAvailability()
    {
        const string yaml =
            """
            version: 1
            id: external-signal-availability
            name: External Signal Availability
            model: Test TV
            externalStates:
              - id: pgen-output-format
                label: PGen output format
                defaultValue: RGB
                options: [RGB, YCbCr422, YCbCr444]
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: hdmi-black-level
                    label: HDMI Black Level
                    controlType: selection
                    defaultValue: Auto
                    options: [Auto, Low, Normal]
                    disabledWhen:
                      - externalState: pgen-output-format
                        equals: YCbCr422
                      - externalState: pgen-output-format
                        equals: YCbCr444
            transitions:
              - id: open-hdmi-black-level
                from: normal-video
                to: hdmi-black-level
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var initialState = Assert.Single(
                controller.GetMenuNavigationSnapshot().ExternalStates!);
            Assert.Equal("RGB", initialState.Value);
            Assert.Equal("hdmi-black-level", controller.CreateNavigationPlan(
                "hdmi-black-level").TargetNodeId);

            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");

            var selectedState = Assert.Single(
                controller.GetMenuNavigationSnapshot().ExternalStates!);
            Assert.Equal("YCbCr422", selectedState.Value);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                controller.CreateNavigationPlan("hdmi-black-level"));
            Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);

            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            Assert.Equal("hdmi-black-level", controller.CreateNavigationPlan(
                "hdmi-black-level").TargetNodeId);
        }
    }

    [Fact]
    public async Task LocalVerificationSidecarPersistsAndOnlyReopensChangedCheck()
    {
        const string yaml =
            """
            version: 1
            id: complete-verification
            name: Complete Verification
            model: S95F
            context:
              firmware: 1296
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: picture-mode
                    label: Picture Mode
                    controlType: selection
                    defaultValue: Standard
                    options: [Standard, Movie]
            anchors:
              - id: normal
                label: Return to normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-picture-mode
                from: normal-video
                to: picture-mode
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        var (controller, _) = await CreateConnectedControllerAsync(yaml);
        await using (controller)
        {
            var sourcePath = controller.GetMenuNavigationSnapshot().DefinitionPath;
            Assert.Equal(yaml, await File.ReadAllTextAsync(sourcePath));
            var initial = controller.GetMenuDefinitionVerificationSnapshot();
            Assert.False(initial.FullyVerified);
            Assert.True(initial.Checks.Single(check => check.Kind == MenuVerificationCheckKind.Timing).Verified);
            Assert.True(initial.Checks.Single(check => check.Kind == MenuVerificationCheckKind.Anchor).Verified);
            Assert.True(initial.Checks.Single(check => check.Kind == MenuVerificationCheckKind.Route).Verified);
            Assert.False(initial.Checks.Single(check => check.Kind == MenuVerificationCheckKind.Display).Verified);
            Assert.False(initial.Checks.Single(check =>
                check.Kind == MenuVerificationCheckKind.Selection).Verified);
            var routeCheck = Assert.Single(
                initial.Checks,
                check => check.Kind == MenuVerificationCheckKind.Route);
            Assert.Equal("open-picture-mode", routeCheck.AuthoringItemId);
            Assert.Equal(
                MenuAuthoringItemKind.Transition,
                routeCheck.AuthoringItemKind);
            Assert.Equal(0, routeCheck.ValidationPasses);
            Assert.Equal(3, routeCheck.RequiredValidationPasses);

            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            var complete = await controller.ConfirmMenuDefinitionVerificationCheckAsync(
                "control:selection-behavior");

            Assert.True(complete.FullyVerified);
            Assert.Equal(complete.RequiredCount, complete.VerifiedCount);
            Assert.Equal(yaml, await File.ReadAllTextAsync(sourcePath));

            var exportPath = Path.Combine(
                _directory,
                "repository-menu-definitions",
                "complete-verification.yaml");
            await controller.ExportMenuStructureAsync(exportPath);
            var exported = await new MenuDefinitionParser().ParseFileAsync(exportPath);
            Assert.Null(exported.Verification);

            await controller.UpdateMenuNodeAsync(
                "picture-mode",
                new MenuNodeEditRequest(
                    "picture-mode",
                    "Picture Mode",
                    "normal-video",
                    null,
                    MenuControlType.Selection,
                    "Standard",
                    SelectionOptions: ["Standard", "Movie", "Filmmaker Mode"]));
            var edited = controller.GetMenuDefinitionVerificationSnapshot();

            Assert.False(edited.FullyVerified);
            var pending = Assert.Single(edited.Checks, check => !check.Verified);
            Assert.Equal("control:selection-behavior", pending.Id);
            Assert.All(
                edited.Checks.Where(check => check.Id != pending.Id),
                check => Assert.True(check.Verified));
        }

        var definitionPath = Path.Combine(_directory, "explicit-validation-menu.yaml");
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Null(reparsed.Verification);
        var sidecarPath = new MenuVerificationStore(_directory).GetPath(
            "complete-verification");
        Assert.True(File.Exists(sidecarPath));

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        var restored = reloaded.GetMenuDefinitionVerificationSnapshot();
        Assert.False(restored.FullyVerified);
        Assert.Equal(
            "control:selection-behavior",
            Assert.Single(restored.Checks, check => !check.Verified).Id);
        Assert.True(restored.Checks.Single(check => check.Id == "display").Verified);
    }

    [Fact]
    public async Task RemovingDisplayVerificationClearsOnlyTheSelectedCheck()
    {
        var (controller, _) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var recorded = await controller.ConfirmMenuDefinitionVerificationCheckAsync(
                "display");
            Assert.True(recorded.Checks.Single(check => check.Id == "display").Verified);
            var otherChecksBefore = recorded.Checks
                .Where(check => check.Id != "display")
                .ToDictionary(check => check.Id, check => check.Verified);

            var updated = await controller.RemoveMenuDefinitionVerificationCheckAsync(
                "display");

            Assert.False(updated.Checks.Single(check => check.Id == "display").Verified);
            Assert.Equal(
                otherChecksBefore,
                updated.Checks
                    .Where(check => check.Id != "display")
                    .ToDictionary(check => check.Id, check => check.Verified));
        }
    }

    [Fact]
    public async Task NavigateBetweenVerifiedDestinationsUsesCalculatedRelativeRoute()
    {
        var allVerifiedYaml = ExplicitValidationMenuYaml.Replace(
            "verified: false",
            "verified: true",
            StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(allVerifiedYaml);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("picture");
            transport.SentMessages.Clear();

            var plan = controller.CreateNavigationPlan("settings");

            Assert.True(plan.UsesCalculatedRoute);
            Assert.False(plan.UsesAnchor);
            Assert.Equal(
                ["KEY_RETURN"],
                plan.CalculatedLeg?.Operations.Select(operation => operation.Key));

            await controller.ExecuteNavigationPlanAsync();

            Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Settings", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Probable, snapshot.MenuConfidence);
        }
    }

    [Fact]
    public async Task ReportingFailedTraversalCapturesDraftAndRestoresKnownState()
    {
        var allVerifiedYaml = ExplicitValidationMenuYaml.Replace(
            "verified: false",
            "verified: true",
            StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(allVerifiedYaml);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("picture");
            var failedPlan = controller.CreateNavigationPlan("settings");
            await controller.ExecuteNavigationPlanAsync();
            transport.SentMessages.Clear();

            var report = await controller.ReportMenuTraversalFailureAsync(failedPlan);

            Assert.Equal("debug-picture-to-settings", report.DraftTransitionId);
            Assert.Equal("Settings / Picture", report.SourcePath);
            Assert.Equal("Settings", report.TargetPath);
            Assert.Equal("Normal video", report.KnownStatePath);
            Assert.Equal(["KEY_EXIT", "KEY_EXIT"], GetSentKeys(transport));

            var candidate = Assert.Single(
                controller.GetMenuAuthoringSnapshot().DraftCandidates,
                item => item.Id == report.DraftTransitionId);
            Assert.Equal("picture", candidate.SourceNodeId);
            Assert.Equal("settings", candidate.TargetNodeId);
            var step = Assert.Single(candidate.ReplaySteps);
            Assert.Equal("KEY_RETURN", step.Key);
            Assert.True(step.HasCustomDelay);

            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("normal-video", navigation.State.NodeId);
            Assert.Equal(MenuStateConfidence.Synchronized, navigation.State.Confidence);
        }
    }

    [Fact]
    public async Task SuccessfulConnectionAssumesNormalVideoWithoutSendingMenuCommands()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(
            clearSentMessages: false);
        await using (controller)
        {
            Assert.Empty(GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Normal video", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Probable, snapshot.MenuConfidence);
            Assert.Contains(
                "assumed",
                controller.GetMenuNavigationSnapshot().State.Reason,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ReloadingTheActiveYamlPreservesTheExpectedMenuState()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var definitionPath = controller.GetMenuNavigationSnapshot().DefinitionPath;
            await controller.SetMenuDefinitionAsync(definitionPath);

            Assert.Empty(GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Normal video", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Probable, snapshot.MenuConfidence);
            Assert.Contains(
                "preserved",
                controller.GetMenuNavigationSnapshot().State.Reason,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MenuNavigationPlansUseVerifiedTransitionsOnly()
    {
        var (controller, _) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.RunMenuAnchorAsync("normal");

            var verifiedPlan = controller.CreateNavigationPlan("settings");
            Assert.True(verifiedPlan.IsExecutable);
            Assert.False(verifiedPlan.UsesDraftTransitions);
            Assert.Single(verifiedPlan.Transitions);

            var exception = Assert.Throws<NavigationPlanningException>(() =>
                controller.CreateNavigationPlan("picture"));
            Assert.Contains("verified navigation route", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LostTvConnectionAutomaticallyDisconnectsTheWebSession()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void HandleChanged()
            {
                if (controller.GetSnapshot().ConnectionState
                    == SamsungConnectionState.Disconnected)
                {
                    disconnected.TrySetResult();
                }
            }

            controller.Changed += HandleChanged;
            try
            {
                transport.LoseConnection();
                await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally
            {
                controller.Changed -= HandleChanged;
            }

            var snapshot = controller.GetSnapshot();
            Assert.Equal(SamsungConnectionState.Disconnected, snapshot.ConnectionState);
            Assert.Contains("closed", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task FailedConnectionRetainsTheErrorAndAllowsAnotherConnectAttempt()
    {
        Directory.CreateDirectory(_directory);
        var transport = new RecordingSamsungTransport
        {
            ConnectFailure = new IOException("Simulated unreachable TV.")
        };
        await using var controller = CreateController(transport);
        await controller.InitializeAsync();

        var exception = await Assert.ThrowsAsync<SamsungConnectionException>(() =>
            controller.ConnectAsync(new TvConnectionRequest(
                "Test TV",
                "192.0.2.10",
                Secure: true,
                Port: null)));

        var snapshot = controller.GetSnapshot();
        Assert.Equal(SamsungConnectionState.Faulted, snapshot.ConnectionState);
        Assert.Equal(exception.Message, snapshot.LastError);
        Assert.False(ConnectionStatePresentation.CanDisconnect(snapshot.ConnectionState));
    }

    [Fact]
    public async Task ConnectionReadinessSettingsPersistAndReachTransport()
    {
        Directory.CreateDirectory(_directory);
        var transport = new RecordingSamsungTransport();
        await using var controller = CreateController(transport);
        await controller.InitializeAsync();

        await controller.ConnectAsync(new TvConnectionRequest(
            "Test TV",
            "192.0.2.10",
            Secure: true,
            Port: null,
            AllowUntrustedCertificate: true,
            KeepAliveIntervalSeconds: 17,
            KeepAliveTimeoutSeconds: 6,
            PostConnectWarmupMilliseconds: 0,
            ReconnectAfterIdleSeconds: 90));

        var snapshot = controller.GetSnapshot();
        Assert.Equal(17, snapshot.KeepAliveIntervalSeconds);
        Assert.Equal(6, snapshot.KeepAliveTimeoutSeconds);
        Assert.Equal(0, snapshot.PostConnectWarmupMilliseconds);
        Assert.Equal(90, snapshot.ReconnectAfterIdleSeconds);
        Assert.Equal(TimeSpan.FromSeconds(17), transport.LastKeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), transport.LastKeepAliveTimeout);

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(_directory, "settings.json")));
        Assert.Equal(
            90,
            settings.RootElement.GetProperty("ReconnectAfterIdleSeconds").GetInt32());
    }

    [Fact]
    public async Task QuickAccessDefaultsToReturnToVideo()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();

        await controller.InitializeAsync();

        var action = Assert.Single(controller.GetQuickAccessActions());
        Assert.Equal("Return to video", action.Label);
        Assert.Equal(QuickAccessActionKind.MenuAnchor, action.Kind);
        Assert.Equal("normal-video", action.Target);
    }

    [Fact]
    public async Task MenuControlBehaviorPreferencesPersistAcrossControllerInstances()
    {
        Directory.CreateDirectory(_directory);
        await using (var controller = CreateController())
        {
            await controller.InitializeAsync();
            Assert.Equal(
                new MenuControlBehaviorPreferences(false, false),
                controller.GetMenuControlBehaviorPreferences());

            await controller.SaveMenuControlBehaviorPreferencesAsync(
                applyImmediately: true,
                returnToNormalVideo: true);

            Assert.Equal(
                new MenuControlBehaviorPreferences(true, true),
                controller.GetMenuControlBehaviorPreferences());
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        Assert.Equal(
            new MenuControlBehaviorPreferences(true, true),
            reloaded.GetMenuControlBehaviorPreferences());
    }

    [Fact]
    public async Task RemovingDefaultQuickAccessActionPersistsAnEmptyList()
    {
        Directory.CreateDirectory(_directory);
        await using (var controller = CreateController())
        {
            await controller.InitializeAsync();
            var action = Assert.Single(controller.GetQuickAccessActions());

            await controller.RemoveQuickAccessActionAsync(action.Id);

            Assert.Empty(controller.GetQuickAccessActions());
        }

        await using var reloaded = CreateController();
        await reloaded.InitializeAsync();
        Assert.Empty(reloaded.GetQuickAccessActions());
    }

    [Fact]
    public async Task QuickAccessRemoteCommandCanBeSavedAndRun()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.AddQuickAccessRemoteKeyAsync(
                "Source",
                "KEY_SOURCE",
                RemoteKeyAction.Click);
            var action = controller.GetQuickAccessActions().Single(candidate =>
                candidate.Kind == QuickAccessActionKind.RemoteKey);

            await controller.RunQuickAccessActionAsync(action.Id);

            Assert.Equal(["KEY_SOURCE"], GetSentKeys(transport));
            var settingsJson = await File.ReadAllTextAsync(
                Path.Combine(_directory, "settings.json"));
            Assert.Contains("Source", settingsJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task QuickAccessMacroMustBeValidAndCanBeRun()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "macros.yaml");
            await File.WriteAllTextAsync(
                macroPath,
                """
                version: 1
                macros:
                  MovieNight:
                    verified: true
                    steps:
                      - key: KEY_HOME
                """);
            await controller.SetMacroFileAsync(macroPath);
            await controller.AddQuickAccessMacroAsync("MovieNight", "Movie night");
            var action = controller.GetQuickAccessActions().Single(candidate =>
                candidate.Kind == QuickAccessActionKind.Macro);

            await controller.RunQuickAccessActionAsync(action.Id);

            Assert.Equal(["KEY_HOME"], GetSentKeys(transport));
            Assert.Equal("Movie night", action.Label);
        }
    }

    [Fact]
    public async Task MacroEditorPersistsIndependentVerificationAndResetsOnlyBehavioralEdits()
    {
        var (controller, _) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "editor-macros.yaml");
            Assert.Empty(await controller.SetMacroFileAsync(macroPath));

            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "First",
                    "First macro",
                    [new KeyStep("KEY_MENU")]));
            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "Second",
                    "Second macro",
                    [new KeyStep("KEY_HOME")]));

            var quickAccessError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.AddQuickAccessMacroAsync("First"));
            Assert.Contains("3/3", quickAccessError.Message, StringComparison.Ordinal);

            await controller.RunMacroAsync("First");
            Assert.Equal(1, (await controller.ConfirmMacroValidationAsync("First", true)).VerificationPasses);
            await controller.RunMacroAsync("Second");
            Assert.Equal(1, (await controller.ConfirmMacroValidationAsync("Second", true)).VerificationPasses);
            await controller.RunMacroAsync("First");
            Assert.Equal(2, (await controller.ConfirmMacroValidationAsync("First", true)).VerificationPasses);
            await controller.RunMacroAsync("First");
            var verified = await controller.ConfirmMacroValidationAsync("First", true);

            Assert.True(verified.Verified);
            Assert.Equal(3, verified.VerificationPasses);
            Assert.Equal(
                1,
                (await controller.LoadMacroDetailsAsync("Second")).VerificationPasses);

            await controller.AddQuickAccessMacroAsync("First");
            await controller.SaveMacroAsync(
                "First",
                new MacroEditRequest(
                    "FirstRenamed",
                    "Metadata-only changes preserve verification",
                    [new KeyStep("KEY_MENU")],
                    ConfirmBeforeRun: true));
            var renamed = await controller.LoadMacroDetailsAsync("FirstRenamed");
            Assert.True(renamed.Verified);
            Assert.True(renamed.ConfirmBeforeRun);
            Assert.Contains(
                controller.GetQuickAccessActions(),
                action => action.Kind == QuickAccessActionKind.Macro
                    && action.Target == "FirstRenamed");

            await controller.SaveMacroAsync(
                "FirstRenamed",
                new MacroEditRequest(
                    "FirstRenamed",
                    "Behavior changed",
                    [new KeyStep("KEY_MENU"), new DelayStep(TimeSpan.FromMilliseconds(50))],
                    ConfirmBeforeRun: true));
            var changed = await controller.LoadMacroDetailsAsync("FirstRenamed");
            Assert.False(changed.Verified);
            Assert.Equal(0, changed.VerificationPasses);
            Assert.True(changed.ConfirmBeforeRun);
            Assert.DoesNotContain(
                controller.GetQuickAccessActions(),
                action => action.Kind == QuickAccessActionKind.Macro);
        }
    }

    [Fact]
    public async Task MacroEditorDeletesUnreferencedMacroAndItsQuickAccessAction()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        var macroPath = Path.Combine(_directory, "delete-macros.yaml");
        await File.WriteAllTextAsync(
            macroPath,
            """
            version: 1
            macros:
              Keep:
                steps:
                  - key: KEY_HOME
              Remove:
                verified: true
                confirmBeforeRun: true
                steps:
                  - key: KEY_MENU
            """);
        await controller.SetMacroFileAsync(macroPath);
        await controller.AddQuickAccessMacroAsync("Remove");

        await controller.DeleteMacroAsync("Remove");

        var remaining = await controller.LoadMacrosAsync();
        Assert.Equal("Keep", Assert.Single(remaining).Name);
        Assert.DoesNotContain(
            controller.GetQuickAccessActions(),
            action => action.Kind == QuickAccessActionKind.Macro);
    }

    [Fact]
    public async Task MacroRenameUpdatesNestedCallsAndDeleteNamesEveryDirectCaller()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        var macroPath = Path.Combine(_directory, "nested-macros.yaml");
        await controller.SetMacroFileAsync(macroPath);
        await controller.SaveMacroAsync(
            null,
            new MacroEditRequest("Child", null, [new KeyStep("KEY_MENU")]));
        await controller.SaveMacroAsync(
            null,
            new MacroEditRequest("Parent", null, [new CallMacroStep("Child")]));
        await controller.SaveMacroAsync(
            null,
            new MacroEditRequest(
                "ParentTwo",
                null,
                [new KeyStep("KEY_HOME"), new CallMacroStep("Child")]));

        await controller.SaveMacroAsync(
            "Child",
            new MacroEditRequest("RenamedChild", null, [new KeyStep("KEY_MENU")]));

        var parent = await controller.LoadMacroDetailsAsync("Parent");
        Assert.Equal(new CallMacroStep("RenamedChild"), Assert.Single(parent.Steps));
        var parentTwo = await controller.LoadMacroDetailsAsync("ParentTwo");
        Assert.Equal(new CallMacroStep("RenamedChild"), parentTwo.Steps[1]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.DeleteMacroAsync("RenamedChild"));
        Assert.Contains("Parent (step 1)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ParentTwo (step 2)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("remove or replace", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await controller.LoadMacroDetailsAsync("RenamedChild"));
    }

    [Fact]
    public async Task MacroCanCallVerifiedMenuDestinationThroughCurrentStatePlanner()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "menu-call-macros.yaml");
            await controller.SetMacroFileAsync(macroPath);
            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "OpenSettingsAndSelect",
                    null,
                    [new MenuStep("settings"), new KeyStep("KEY_ENTER")]));

            await controller.RunMacroAsync("OpenSettingsAndSelect");

            Assert.Equal(["KEY_MENU", "KEY_ENTER"], GetSentKeys(transport));
            Assert.Equal(
                MenuStateConfidence.Unknown,
                controller.GetMenuNavigationSnapshot().State.Confidence);
        }
    }

    [Fact]
    public async Task MacroDeclaredStartRecoversUnknownStateBeforeSavedKeys()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "starting-state-macros.yaml");
            await controller.SetMacroFileAsync(macroPath);
            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "OpenSettings",
                    null,
                    [new KeyStep("KEY_MENU")],
                    "normal-video"));
            await controller.SendKeyAsync("KEY_ENTER");
            Assert.Equal(
                MenuStateConfidence.Unknown,
                controller.GetMenuNavigationSnapshot().State.Confidence);
            transport.SentMessages.Clear();

            await controller.RunMacroAsync("OpenSettings");

            Assert.Equal(["KEY_EXIT", "KEY_EXIT", "KEY_MENU"], GetSentKeys(transport));
            Assert.Equal("settings", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task MacroRecorderCanPrepareSelectedStartWithoutRecordingMacroSteps()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.PrepareMacroRecordingStartAsync("settings");
            await controller.PrepareMacroRecordingStartAsync("settings");

            Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
            Assert.Equal("settings", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task ChangingMacroStartingStateResetsBehavioralVerification()
    {
        var (controller, _) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "starting-state-verification.yaml");
            await controller.SetMacroFileAsync(macroPath);
            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "VolumeCheck",
                    null,
                    [new KeyStep("KEY_VOLUP")],
                    "normal-video"));
            for (var pass = 0; pass < 3; pass++)
            {
                await controller.RunMacroAsync("VolumeCheck");
                await controller.ConfirmMacroValidationAsync("VolumeCheck", true);
            }

            var updated = await controller.SaveMacroAsync(
                "VolumeCheck",
                new MacroEditRequest(
                    "VolumeCheck",
                    null,
                    [new KeyStep("KEY_VOLUP")],
                    "settings"));

            Assert.False(updated.Verified);
            Assert.Equal(0, updated.VerificationPasses);
            Assert.Equal(
                "settings",
                (await controller.LoadMacroDetailsAsync("VolumeCheck")).StartingNodeId);
        }
    }

    [Fact]
    public async Task MacroEditorRejectsStartingStateWithoutVerifiedPreparationRoute()
    {
        var (controller, _) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "invalid-starting-state.yaml");
            await controller.SetMacroFileAsync(macroPath);

            var exception = await Assert.ThrowsAsync<NavigationPlanningException>(() =>
                controller.SaveMacroAsync(
                    null,
                    new MacroEditRequest(
                        "DraftPicture",
                        null,
                        [new KeyStep("KEY_ENTER")],
                        "picture")));

            Assert.Contains("verified anchor", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task MacroRejectsUnverifiedMenuDestinationBeforeSendingEarlierKeys()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            var macroPath = Path.Combine(_directory, "invalid-menu-call-macros.yaml");
            await File.WriteAllTextAsync(
                macroPath,
                """
                version: 1
                macros:
                  Unsafe:
                    steps:
                      - key: KEY_HOME
                      - menu: picture
                """);
            await controller.SetMacroFileAsync(macroPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.RunMacroAsync("Unsafe"));

            Assert.Contains("not verified", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task MacroRejectsMenuCallFromUnknownStateBeforeSendingEarlierKeys()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.SendKeyAsync("KEY_ENTER");
            transport.SentMessages.Clear();
            var macroPath = Path.Combine(_directory, "unknown-state-menu-call.yaml");
            await controller.SetMacroFileAsync(macroPath);
            await controller.SaveMacroAsync(
                null,
                new MacroEditRequest(
                    "NeedsKnownState",
                    null,
                    [new KeyStep("KEY_HOME"), new MenuStep("settings")]));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.RunMacroAsync("NeedsKnownState"));

            Assert.Contains("state is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ReadOnlyResearchQueriesSendKnownApplicationEvents()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.SendQueryAsync(SamsungQuery.EdenApplications);
            await controller.SendQueryAsync(SamsungQuery.InstalledApplications);

            Assert.Equal(
                ["ed.edenApp.get", "ed.installedApp.get"],
                transport.SentMessages.Select(message =>
                {
                    using var document = JsonDocument.Parse(message);
                    Assert.Equal(
                        "ms.channel.emit",
                        document.RootElement.GetProperty("method").GetString());
                    return document.RootElement
                        .GetProperty("params")
                        .GetProperty("event")
                        .GetString()!;
                }));
        }
    }

    [Fact]
    public async Task CancellingActiveMenuUpdateStopsBeforeTheNextRemoteKey()
    {
        const string yaml =
            """
            version: 1
            id: cancellable-picture-update
            name: Cancellable Picture Update
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
              - id: settings
                label: Settings
                children:
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: "50"
                    minimumValue: 0
                    maximumValue: 100
            anchors:
              - id: normal
                label: Normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-brightness
                from: normal-video
                to: brightness
                verified: true
                steps:
                  - key: KEY_MENU
                  - key: KEY_DOWN
                  - key: KEY_ENTER
            """;
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "cancellable-picture-update.yaml");
        await File.WriteAllTextAsync(definitionPath, yaml);
        await WriteSettingsAsync(definitionPath);
        var transport = new RecordingSamsungTransport();
        var delay = new BlockingMenuDelay();
        await using var controller = CreateController(transport, delay);
        await controller.InitializeAsync();
        await controller.ConnectAsync(new TvConnectionRequest(
            "Test TV",
            "192.0.2.10",
            Secure: true,
            Port: null,
            PostConnectWarmupMilliseconds: 0,
            ReconnectAfterIdleSeconds: 0));
        transport.SentMessages.Clear();

        var apply = controller.ApplyMenuControlValuesAsync(
            [new MenuControlValueUpdate("brightness", "50", "55")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["brightness"] = "50"
            },
            returnToNormalVideo: false);
        await delay.FirstDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.CancelNavigation();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
        Assert.Equal(
            MenuStateConfidence.Unknown,
            controller.GetMenuNavigationSnapshot().State.Confidence);
    }

    private async Task<(SamsungControllerService Controller, RecordingSamsungTransport Transport)>
        CreateConnectedControllerAsync(
            string? definitionYaml = null,
            bool clearSentMessages = true,
            bool installedMenu = false)
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = installedMenu
            ? Path.Combine(AppContext.BaseDirectory, "menu-definitions", $"verification-test-{Guid.NewGuid():N}.yaml")
            : Path.Combine(_directory, "explicit-validation-menu.yaml");
        if (installedMenu)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
            _installedTestMenus.Add(definitionPath, definitionYaml ?? ExplicitValidationMenuYaml);
        }
        await File.WriteAllTextAsync(
            definitionPath,
            definitionYaml ?? ExplicitValidationMenuYaml);
        await WriteSettingsAsync(definitionPath);
        var transport = new RecordingSamsungTransport();
        var controller = CreateController(transport);
        await controller.InitializeAsync();
        await controller.ConnectAsync(new TvConnectionRequest(
            "Test TV",
            "192.0.2.10",
            Secure: true,
            Port: null,
            PostConnectWarmupMilliseconds: 0,
            ReconnectAfterIdleSeconds: 0));
        if (clearSentMessages)
        {
            transport.SentMessages.Clear();
        }

        return (controller, transport);
    }

    private static string[] GetSentKeys(RecordingSamsungTransport transport) =>
        transport.SentMessages
            .Select(message =>
            {
                using var document = JsonDocument.Parse(message);
                return document.RootElement
                    .GetProperty("params")
                    .GetProperty("DataOfCmd")
                    .GetString()!;
            })
            .ToArray();

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

    private SamsungControllerService CreateController(RecordingSamsungTransport transport)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SamsungController:ConfigurationDirectory"] = _directory
            })
            .Build();
        return new SamsungControllerService(configuration, transport, ImmediateMenuDelay.Instance);
    }

    private SamsungControllerService CreateController(
        RecordingSamsungTransport transport,
        IMenuDelay menuDelay)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SamsungController:ConfigurationDirectory"] = _directory
            })
            .Build();
        return new SamsungControllerService(configuration, transport, menuDelay);
    }

    private async Task WriteSettingsAsync(string definitionPath)
    {
        var json = JsonSerializer.Serialize(new
        {
            MenuDefinitionPath = definitionPath,
            PostConnectWarmupMilliseconds = 0,
            ReconnectAfterIdleSeconds = 0
        });
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), json);
    }

    public void Dispose()
    {
        var modifiedInstalledMenus = new List<string>();
        foreach (var (path, original) in _installedTestMenus)
        {
            if (!File.Exists(path) || File.ReadAllText(path) != original)
            {
                modifiedInstalledMenus.Add(path);
            }

            File.Delete(path);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        Assert.Empty(modifiedInstalledMenus);
    }

    private const string ValidMenuYaml =
        """
        version: 1
        id: test
        name: Test Menu
        model: Test TV
        timing:
          defaultDelay: 125ms
          screenChangeDelay: 600ms
          returnDelay: 300ms
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
        timing:
          defaultDelay: 125ms
          screenChangeDelay: 600ms
          returnDelay: 300ms
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
              - key: KEY_DOWN
                repeat: 2
                delay: 700ms
        """;

    private const string VerifiedRemovalMenuYaml =
        """
        version: 1
        id: verified-removal-test
        name: Verified Removal Test
        model: Test TV
        nodes:
          - id: tv-interface
            label: TV interface
            children:
              - id: normal-video
                label: Normal video
              - id: settings-overlay
                label: Settings overlay
                children:
                  - id: expert-settings
                    label: Expert settings
        anchors:
          - id: normal-video
            label: Return to normal video
            target: normal-video
            verified: true
            returnStrategy:
              menuRoot: settings-overlay
              atMenuRoot:
                verified: true
                steps:
                  - key: KEY_RETURN
              belowMenuRoot:
                verified: false
                steps:
                  - key: KEY_RETURN
            steps:
              - key: KEY_RETURN
        transitions:
          - id: open-settings
            from: normal-video
            to: settings-overlay
            verified: true
            steps:
              - key: KEY_MENU
          - id: open-expert
            from: settings-overlay
            to: expert-settings
            verified: true
            steps:
              - key: KEY_ENTER
        """;

    private const string ConditionalMenuYaml =
        """
        version: 1
        id: conditional-menu
        name: Conditional Menu
        model: Test TV
        configurations:
          - id: standard
            name: Standard
            conditions: Game Mode = Off
          - id: game-mode
            name: Game Mode
            conditions: Game Mode = On
        nodes:
          - id: normal-video
            label: Normal video
          - id: settings
            label: Settings
            children:
              - id: picture-clarity
                label: Picture Clarity Settings
        anchors:
          - id: normal
            label: Normal video
            target: normal-video
            verified: true
            steps:
              - key: KEY_RETURN
        transitions:
          - id: open-picture-clarity-standard
            from: normal-video
            to: picture-clarity
            configuration: standard
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
                repeat: 4
              - key: KEY_ENTER
        """;

    private const string ReturnStrategyMenuYaml =
        """
        version: 1
        id: return-test
        name: Return Test Menu
        model: Test TV
        nodes:
          - id: normal-video
            label: Normal video
          - id: settings
            label: Settings
        anchors:
          - id: normal
            label: Return to normal video
            target: normal-video
            verified: true
            steps:
              - key: KEY_MENU
                repeat: 2
        transitions:
          - id: open-settings
            from: normal-video
            to: settings
            verified: true
            steps:
              - key: KEY_MENU
          - id: close-settings
            from: settings
            to: normal-video
            verified: true
            steps:
              - key: KEY_RETURN
        """;

    private const string ExplicitValidationMenuYaml =
        """
        version: 1
        id: explicit-validation
        name: Explicit Validation Menu
        model: Test TV
        timing:
          defaultDelay: 50ms
          screenChangeDelay: 50ms
          returnDelay: 50ms
        nodes:
          - id: normal-video
            label: Normal video
          - id: settings
            label: Settings
            children:
              - id: picture
                label: Picture
        anchors:
          - id: normal
            label: Return to normal video
            target: normal-video
            verified: true
            returnStrategy:
              menuRoot: settings
              atMenuRoot:
                verified: true
                steps:
                  - key: KEY_RETURN
              belowMenuRoot:
                verified: false
                steps:
                  - key: KEY_MENU
                  - key: KEY_RETURN
            steps:
              - key: KEY_EXIT
                repeat: 2
        transitions:
          - id: open-settings
            from: normal-video
            to: settings
            verified: true
            steps:
              - key: KEY_MENU
          - id: open-picture-draft
            from: settings
            to: picture
            verified: false
            steps:
              - key: KEY_DOWN
              - key: KEY_ENTER
        """;

    private sealed class ImmediateMenuDelay : IMenuDelay
    {
        public static ImmediateMenuDelay Instance { get; } = new();

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BlockingMenuDelay : IMenuDelay
    {
        public TaskCompletionSource FirstDelayStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            FirstDelayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RecordingSamsungTransport : ISamsungTransport
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        private long _generation;

        public bool IsConnected { get; private set; }

        public long Generation => Interlocked.Read(ref _generation);

        public List<string> SentMessages { get; } = [];

        public Exception? ConnectFailure { get; init; }

        public Exception? SendFailure { get; set; }

        public TimeSpan LastKeepAliveInterval { get; private set; }

        public TimeSpan LastKeepAliveTimeout { get; private set; }

        public Task ConnectAsync(
            Uri endpoint,
            TimeSpan timeout,
            bool allowUntrustedCertificate,
            TimeSpan keepAliveInterval,
            TimeSpan keepAliveTimeout,
            CancellationToken cancellationToken = default)
        {
            if (ConnectFailure is not null)
            {
                return Task.FromException(ConnectFailure);
            }

            LastKeepAliveInterval = keepAliveInterval;
            LastKeepAliveTimeout = keepAliveTimeout;
            IsConnected = true;
            Interlocked.Increment(ref _generation);
            _inbound.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                @event = "ms.channel.connect",
                data = new { token = "test-token" }
            }));
            return Task.CompletedTask;
        }

        public Task SendAsync(
            string rawJson,
            CancellationToken cancellationToken = default)
        {
            SentMessages.Add(rawJson);
            return SendFailure is null
                ? Task.CompletedTask
                : Task.FromException(SendFailure);
        }

        public async IAsyncEnumerable<string> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _inbound.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void LoseConnection()
        {
            IsConnected = false;
            _inbound.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
