using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.MenuDefinitionWriterTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WrittenDefinitionRoundTripsWithoutLosingAuthoringData()
    {
        var definition = new MenuDefinition(
            "test-menu",
            "Owner's Test Menu",
            "Test TV",
            new MenuDefinitionContext("example-fw", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video", "Owner's settings"),
                new MenuNode(
                    "adaptive-picture",
                    "Adaptive Picture",
                    "settings",
                    ControlType: MenuControlType.Switch,
                    DefaultValue: "off"),
                new MenuNode(
                    "brightness",
                    "Brightness",
                    "settings",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "50",
                    DisabledWhen: [new MenuNodeDisabledCondition("adaptive-picture", "on")],
                    MinimumValue: 0,
                    MaximumValue: 100,
                    HiddenWhen: [new MenuNodeHiddenCondition("adaptive-picture", "off")]),
                new MenuNode(
                    "picture-mode",
                    "Picture Mode",
                    "settings",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Movie",
                    SelectionOptions: ["Standard", "Movie", "Filmmaker Mode"]),
                new MenuNode(
                    "reset-picture",
                    "Reset Picture",
                    "settings",
                    ControlType: MenuControlType.Confirmation,
                    DefaultValue: "Cancel",
                    SelectionOptions: ["Reset", "Cancel"]),
                new MenuNode(
                    "sound-output",
                    "Sound Output",
                    "settings",
                    ControlType: MenuControlType.SubmenuSelection,
                    DefaultValue: "Receiver",
                    SelectionOptions: ["TV Speaker", "Receiver", "Bluetooth Speaker"]),
                new MenuNode(
                    "interval",
                    "Interval",
                    "settings",
                    ControlType: MenuControlType.IndexedSelection,
                    DefaultValue: "5%",
                    SelectionOptions: ["5%", "10%", "15%"]),
                new MenuNode(
                    "interval-red",
                    "Red",
                    "settings",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "0",
                    MinimumValue: -50,
                    MaximumValue: 50),
                new MenuNode(
                    "smart-calibration",
                    "Smart Calibration",
                    "settings",
                    ControlType: MenuControlType.Action),
                new MenuNode(
                    "unavailable-feature",
                    "Unavailable Feature",
                    "settings",
                    Disabled: true)
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [
                        new MenuOperation("KEY_DOWN", Repeat: 4, DelayAfter: TimeSpan.FromMilliseconds(250)),
                        new MenuOperation("KEY_ENTER", RemoteKeyAction.Press, DelayAfter: TimeSpan.FromMilliseconds(500))
                    ],
                    Description: "Recorded in the UI",
                    ReturnToVideoOperations:
                    [
                        new MenuOperation("KEY_RETURN", DelayAfter: TimeSpan.FromMilliseconds(325))
                    ],
                    ConfigurationId: "standard")
            ],
            [
                new MenuAnchor(
                    "normal",
                    "Return to video",
                    "normal-video",
                    [new MenuOperation("KEY_RETURN", Repeat: 3, DelayAfter: TimeSpan.FromMilliseconds(300))],
                    true,
                    ReturnStrategy: new MenuReturnStrategy(
                        "settings",
                        new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                        new MenuReturnScript([
                            new MenuOperation("KEY_MENU"),
                            new MenuOperation("KEY_RETURN")
                        ]),
                        [
                            new MenuReturnOverride(
                                "settings",
                                new MenuReturnScript([new MenuOperation("KEY_EXIT")], true))
                        ]),
                    ValidationSourceNodeId: "settings",
                    ConfigurationId: "standard")
            ],
            new MenuTimingProfile(
                175,
                650,
                325,
                true,
                AdjustmentDelayMilliseconds: 60),
            [new MenuConfiguration("standard", "Standard", "Game Mode = Off")]);
        var path = Path.Combine(_directory, "menu.yaml");

        await new MenuDefinitionWriter().WriteFileAsync(path, definition);
        var writtenYaml = await File.ReadAllTextAsync(path);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal(definition.Id, reparsed.Id);
        Assert.Equal(definition.Name, reparsed.Name);
        Assert.Equal(definition.Context, reparsed.Context);
        Assert.Equal(definition.Timing with { Verified = false }, reparsed.Timing);
        var configuration = Assert.Single(reparsed.Configurations.Values);
        Assert.Equal("standard", configuration.Id);
        Assert.Equal("Game Mode = Off", configuration.Conditions);
        Assert.DoesNotContain("verified:", writtenYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("verification:", writtenYaml, StringComparison.Ordinal);
        Assert.Contains("  adjustmentDelay: 60ms", writtenYaml, StringComparison.Ordinal);
        Assert.Contains("    children:", writtenYaml, StringComparison.Ordinal);
        Assert.Contains("        children:", writtenYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("parent:", writtenYaml, StringComparison.Ordinal);
        Assert.Equal("Owner's settings", reparsed.Nodes["settings"].Description);
        Assert.Equal(MenuControlType.Switch, reparsed.Nodes["adaptive-picture"].ControlType);
        Assert.Equal("off", reparsed.Nodes["adaptive-picture"].DefaultValue);
        Assert.Equal(MenuControlType.Slider, reparsed.Nodes["brightness"].ControlType);
        Assert.Equal("50", reparsed.Nodes["brightness"].DefaultValue);
        Assert.Equal(0m, reparsed.Nodes["brightness"].MinimumValue);
        Assert.Equal(100m, reparsed.Nodes["brightness"].MaximumValue);
        var disabledCondition = Assert.Single(reparsed.Nodes["brightness"].DisabledWhen!);
        Assert.Equal("adaptive-picture", disabledCondition.SettingNodeId);
        Assert.Equal("on", disabledCondition.EqualsValue);
        var hiddenCondition = Assert.Single(reparsed.Nodes["brightness"].HiddenWhen!);
        Assert.Equal("adaptive-picture", hiddenCondition.SettingNodeId);
        Assert.Equal("off", hiddenCondition.EqualsValue);
        Assert.Equal(
            ["Standard", "Movie", "Filmmaker Mode"],
            reparsed.Nodes["picture-mode"].SelectionOptions);
        Assert.Equal(MenuControlType.Confirmation, reparsed.Nodes["reset-picture"].ControlType);
        Assert.Equal("Cancel", reparsed.Nodes["reset-picture"].DefaultValue);
        Assert.Equal(["Reset", "Cancel"], reparsed.Nodes["reset-picture"].SelectionOptions);
        Assert.Equal(
            MenuControlType.SubmenuSelection,
            reparsed.Nodes["sound-output"].ControlType);
        Assert.Equal("Receiver", reparsed.Nodes["sound-output"].DefaultValue);
        Assert.Equal(
            ["TV Speaker", "Receiver", "Bluetooth Speaker"],
            reparsed.Nodes["sound-output"].SelectionOptions);
        Assert.Equal(MenuControlType.IndexedSelection, reparsed.Nodes["interval"].ControlType);
        Assert.Equal(["5%", "10%", "15%"], reparsed.Nodes["interval"].SelectionOptions);
        Assert.Equal(MenuControlType.Action, reparsed.Nodes["smart-calibration"].ControlType);
        Assert.Null(reparsed.Nodes["smart-calibration"].DefaultValue);
        Assert.True(reparsed.Nodes["unavailable-feature"].Disabled);
        Assert.Contains("disabled: true", writtenYaml, StringComparison.Ordinal);
        Assert.False(reparsed.Anchors["normal"].Verified);
        Assert.Equal("standard", reparsed.Anchors["normal"].ConfigurationId);
        Assert.Equal("settings", reparsed.Anchors["normal"].ValidationSourceNodeId);
        var returnStrategy = Assert.IsType<MenuReturnStrategy>(
            reparsed.Anchors["normal"].ReturnStrategy);
        Assert.Equal("settings", returnStrategy.MenuRootNodeId);
        Assert.False(returnStrategy.AtMenuRoot.Verified);
        Assert.Equal("KEY_RETURN", Assert.Single(returnStrategy.AtMenuRoot.Operations).Key);
        Assert.False(returnStrategy.BelowMenuRoot.Verified);
        Assert.Equal(
            ["KEY_MENU", "KEY_RETURN"],
            returnStrategy.BelowMenuRoot.Operations.Select(operation => operation.Key));
        var nodeOverride = Assert.Single(returnStrategy.NodeOverrides!);
        Assert.Equal("settings", nodeOverride.NodeId);
        Assert.False(nodeOverride.Script.Verified);
        Assert.Equal("KEY_EXIT", Assert.Single(nodeOverride.Script.Operations).Key);
        var transition = reparsed.Transitions["open-settings"];
        Assert.Equal("standard", transition.ConfigurationId);
        Assert.False(transition.Verified);
        Assert.Equal(4, transition.Operations[0].Repeat);
        Assert.Equal(RemoteKeyAction.Press, transition.Operations[1].Action);
        Assert.Equal(TimeSpan.FromMilliseconds(500), transition.Operations[1].DelayAfter);
        Assert.Equal(
            "KEY_RETURN",
            Assert.Single(transition.ReturnToVideoOperations!).Key);
        Assert.Equal(
            TimeSpan.FromMilliseconds(325),
            Assert.Single(transition.ReturnToVideoOperations!).DelayAfter);
    }

    [Fact]
    public void EmptyAuthoringCollectionsRoundTrip()
    {
        var definition = new MenuDefinition(
            "new-tv",
            "New TV",
            "Samsung TV",
            new MenuDefinitionContext(),
            [new MenuNode("normal-video", "Normal video")],
            [],
            []);

        var yaml = new MenuDefinitionWriter().Serialize(definition);
        var reparsed = new MenuDefinitionParser().Parse(yaml);

        Assert.Empty(reparsed.Anchors);
        Assert.Empty(reparsed.Transitions);
    }

    [Fact]
    public void WriterRejectsChildrenUnderANonSubmenuNode()
    {
        var definition = new MenuDefinition(
            "invalid-tree",
            "Invalid Tree",
            "Samsung TV",
            new MenuDefinitionContext(),
            [
                new MenuNode(
                    "picture-mode",
                    "Picture Mode",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Standard",
                    SelectionOptions: ["Standard", "Movie"]),
                new MenuNode("brightness", "Brightness", "picture-mode")
            ],
            [],
            []);

        var exception = Assert.Throws<MenuDefinitionValidationException>(
            () => new MenuDefinitionWriter().Serialize(definition));

        Assert.Contains("Only a submenu node can contain children", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TopologyGenerationMetadataRoundTrips()
    {
        var definition = new MenuDefinition(
            "generated-routes",
            "Generated Routes",
            "Samsung TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode("picture", "Picture", "settings")
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [new MenuOperation("KEY_MENU")]),
                new MenuTransition(
                    "topology-open-settings-to-picture",
                    "normal-video",
                    "picture",
                    [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_ENTER")],
                    GeneratedFromTopology: true,
                    TopologySeedTransitionId: "open-settings",
                    ValidationGroupId: "topology-open-settings-picture",
                    IsValidationRoute: true)
            ],
            []);

        var yaml = new MenuDefinitionWriter().Serialize(definition);
        var reparsed = new MenuDefinitionParser().Parse(yaml);
        var generated = reparsed.Transitions["topology-open-settings-to-picture"];

        Assert.True(generated.GeneratedFromTopology);
        Assert.Equal("open-settings", generated.TopologySeedTransitionId);
        Assert.Equal("topology-open-settings-picture", generated.ValidationGroupId);
        Assert.True(generated.IsValidationRoute);
    }

    [Fact]
    public void VerificationManifestIsNeverWrittenIntoMenuTopology()
    {
        var display = new MenuVerificationDisplay(
            "S95F",
            "1296",
            "SDR",
            "Filmmaker Mode",
            "Home Theater System");
        var verifiedAt = new DateTimeOffset(2026, 8, 30, 15, 30, 0, TimeSpan.Zero);
        var definition = new MenuDefinition(
            "verified-menu",
            "Verified menu",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Filmmaker Mode", "Home Theater System"),
            [new MenuNode("normal-video", "Normal video")],
            [],
            [],
            verification: new MenuVerificationManifest(
                display,
                [new MenuVerificationRecord("display", new string('a', 64), verifiedAt)]));

        var yaml = new MenuDefinitionWriter().Serialize(definition);
        var reparsed = new MenuDefinitionParser().Parse(yaml);

        Assert.Null(reparsed.Verification);
        Assert.DoesNotContain("verification:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("verifiedAt:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonDefinitionRoundTripsWithTheSameSchemaAndKeepsJsonOnSave()
    {
        var verifiedAt = new DateTimeOffset(2026, 8, 30, 18, 45, 0, TimeSpan.Zero);
        var definition = new MenuDefinition(
            "json-menu",
            "JSON menu",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Filmmaker Mode", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode(
                    "brightness",
                    "Brightness",
                    "settings",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "50",
                    MinimumValue: 0,
                    MaximumValue: 100),
                new MenuNode(
                    "picture-mode",
                    "Picture Mode",
                    "settings",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Filmmaker Mode",
                    SelectionOptions: ["Standard", "Filmmaker Mode"]),
                new MenuNode(
                    "smart-calibration",
                    "Smart Calibration",
                    "settings",
                    ControlType: MenuControlType.Action),
                new MenuNode(
                    "unavailable-feature",
                    "Unavailable Feature",
                    "settings",
                    Disabled: true)
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [new MenuOperation("KEY_MENU", DelayAfter: TimeSpan.FromMilliseconds(800))],
                    true,
                    ConfigurationId: "default")
            ],
            [
                new MenuAnchor(
                    "normal-video-anchor",
                    "Normal video",
                    "normal-video",
                    [new MenuOperation("KEY_RETURN")],
                    true,
                    ReturnStrategy: new MenuReturnStrategy(
                        "settings",
                        new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                        new MenuReturnScript([
                            new MenuOperation("KEY_MENU"),
                            new MenuOperation("KEY_RETURN")
                        ], true)),
                    ValidationSourceNodeId: "settings",
                    ConfigurationId: "default")
            ],
            new MenuTimingProfile(
                150,
                800,
                300,
                true,
                AdjustmentDelayMilliseconds: 65),
            [new MenuConfiguration("default", "Default", "Game Mode = Off")],
            verification: new MenuVerificationManifest(
                new MenuVerificationDisplay(
                    "S95F",
                    "1296",
                    "SDR",
                    "Filmmaker Mode",
                    "HDMI 1"),
                [new MenuVerificationRecord("display", new string('b', 64), verifiedAt)]));
        var path = Path.Combine(_directory, "menu.json");

        await new MenuDefinitionWriter().WriteFileAsync(path, definition);
        var firstContent = await File.ReadAllTextAsync(path);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);
        await new MenuDefinitionWriter().WriteFileAsync(path, reparsed);
        var secondContent = await File.ReadAllTextAsync(path);

        Assert.StartsWith("{", firstContent.TrimStart(), StringComparison.Ordinal);
        Assert.StartsWith("{", secondContent.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"children\"", firstContent, StringComparison.Ordinal);
        Assert.Contains("\"adjustmentDelay\": \"65ms\"", firstContent, StringComparison.Ordinal);
        Assert.DoesNotContain("\"parent\"", firstContent, StringComparison.Ordinal);
        Assert.Equal(definition.Id, reparsed.Id);
        Assert.Equal(definition.Context, reparsed.Context);
        Assert.Equal(definition.Timing with { Verified = false }, reparsed.Timing);
        Assert.Equal(MenuControlType.Slider, reparsed.Nodes["brightness"].ControlType);
        Assert.Equal(100m, reparsed.Nodes["brightness"].MaximumValue);
        Assert.Equal(["Standard", "Filmmaker Mode"], reparsed.Nodes["picture-mode"].SelectionOptions);
        Assert.Equal(MenuControlType.Action, reparsed.Nodes["smart-calibration"].ControlType);
        Assert.True(reparsed.Nodes["unavailable-feature"].Disabled);
        Assert.Contains("\"disabled\": true", firstContent, StringComparison.Ordinal);
        Assert.Equal("KEY_MENU", Assert.Single(reparsed.Transitions.Values).Operations[0].Key);
        Assert.Null(reparsed.Verification);
        Assert.DoesNotContain("\"verification\"", firstContent, StringComparison.Ordinal);
        Assert.DoesNotContain("\"verified\"", firstContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionlessJsonIsDetectedByItsContentAndPreservedOnSave()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "menu-definition");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": 1,
              "id": "detected-json",
              "name": "Detected JSON",
              "model": "Samsung TV",
              "nodes": [{ "id": "normal-video", "label": "Normal video" }],
              "anchors": [],
              "transitions": []
            }
            """);

        var definition = await new MenuDefinitionParser().ParseFileAsync(path);
        await new MenuDefinitionWriter().WriteFileAsync(path, definition);

        Assert.Equal("detected-json", definition.Id);
        Assert.StartsWith(
            "{",
            (await File.ReadAllTextAsync(path)).TrimStart(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonReportsJsonSyntaxFailure()
    {
        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionJsonSerializer().Parse("{ \"version\": 1,"));

        Assert.Contains("Invalid menu definition JSON", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 1, column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSemanticErrorReportsTheUnknownFieldsSourceLine()
    {
        const string json =
            """
            {
              "version": 1,
              "id": "line-diagnostics",
              "name": "Line Diagnostics",
              "model": "Samsung TV",
              "nodes": [
                {
                  "id": "normal-video",
                  "lable": "Normal video"
                }
              ]
            }
            """;

        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionJsonSerializer().Parse(json));

        Assert.Contains("line 9, column 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lable", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
