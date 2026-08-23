using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
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
        CreateConnectedControllerAsync(string? definitionYaml = null)
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
            Port: null));
        transport.SentMessages.Clear();
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

        public Task ConnectAsync(
            Uri endpoint,
            TimeSpan timeout,
            bool allowUntrustedCertificate,
            CancellationToken cancellationToken = default)
        {
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
