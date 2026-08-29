using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using SamsungController.Automation.Macros;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;
using SamsungController.Core.Protocol;
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
    public async Task MenuTreeCanBeDefinedAndRenamedBeforeRecordingRoutes()
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();

        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest(
            "tree-first",
            "Tree First Menu",
            "Samsung Test TV",
            "1000",
            "SDR",
            "Movie",
            "HDMI 1"));

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
        Assert.Equal(new MenuTimingProfile(175, 650, 325), reparsed.Timing);
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
        Assert.Equal(new MenuTimingProfile(175, 650, 325), snapshot.Timing);
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

        await controller.UpdateMenuTimingProfileAsync(new MenuTimingProfile(225, 750, 400));

        var reparsed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.Equal(new MenuTimingProfile(225, 750, 400), reparsed.Timing);
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
        Assert.True(unchanged.Timing.Verified);

        await controller.UpdateMenuTimingProfileAsync(new MenuTimingProfile(225, 750, 400));
        var changed = await new MenuDefinitionParser().ParseFileAsync(definitionPath);
        Assert.False(changed.Timing.Verified);
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
        Assert.True(persisted.AtMenuRoot.Verified);
        Assert.False(persisted.BelowMenuRoot.Verified);
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
                  - id: picture
                    label: Picture
                    parent: settings
                """,
                """
                  - id: picture
                    label: Picture
                    parent: settings
                  - id: sound
                    label: Sound
                    parent: settings
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
            var transition = verified.Transitions["open-picture-draft"];
            Assert.True(transition.Verified);
            Assert.Equal(
                "KEY_HOME",
                Assert.Single(transition.ReturnToVideoOperations!).Key);

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
    public async Task SystemTimingTestSendsOnlyTheSelectedTraversal()
    {
        var (controller, transport) = await CreateConnectedControllerAsync();
        await using (controller)
        {
            await controller.RunMenuTimingProfileTestAsync(
                "open-settings",
                new MenuTimingProfile(50, 50, 50));

            Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
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
                Assert.Equal(MenuStateConfidence.Unknown, controller.GetSnapshot().MenuConfidence);

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
            Assert.True(Assert.Single(
                definition.Anchors["normal"].ReturnStrategy!.NodeOverrides!).Script.Verified);
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
    public async Task SuccessfulConnectionAutomaticallyRunsThePreferredVerifiedAnchor()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(
            clearSentMessages: false);
        await using (controller)
        {
            Assert.Equal(["KEY_EXIT", "KEY_EXIT"], GetSentKeys(transport));
            var snapshot = controller.GetSnapshot();
            Assert.Equal("Normal video", snapshot.MenuLabel);
            Assert.Equal(MenuStateConfidence.Synchronized, snapshot.MenuConfidence);
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

    private async Task<(SamsungControllerService Controller, RecordingSamsungTransport Transport)>
        CreateConnectedControllerAsync(
            string? definitionYaml = null,
            bool clearSentMessages = true)
    {
        Directory.CreateDirectory(_directory);
        var definitionPath = Path.Combine(_directory, "explicit-validation-menu.yaml");
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
          - id: normal-video
            label: Normal video
            parent: tv-interface
          - id: settings-overlay
            label: Settings overlay
            parent: tv-interface
          - id: expert-settings
            label: Expert settings
            parent: settings-overlay
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
          - id: picture
            label: Picture
            parent: settings
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

    private sealed class RecordingSamsungTransport : ISamsungTransport
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        private long _generation;

        public bool IsConnected { get; private set; }

        public long Generation => Interlocked.Read(ref _generation);

        public List<string> SentMessages { get; } = [];

        public Exception? ConnectFailure { get; init; }

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
            return Task.CompletedTask;
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
