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
                    SelectionOptions: ["TV Speaker", "Receiver", "Bluetooth Speaker"])
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
            new MenuTimingProfile(175, 650, 325, true),
            [new MenuConfiguration("standard", "Standard", "Game Mode = Off")]);
        var path = Path.Combine(_directory, "menu.yaml");

        await new MenuDefinitionWriter().WriteFileAsync(path, definition);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal(definition.Id, reparsed.Id);
        Assert.Equal(definition.Name, reparsed.Name);
        Assert.Equal(definition.Context, reparsed.Context);
        Assert.Equal(definition.Timing, reparsed.Timing);
        var configuration = Assert.Single(reparsed.Configurations.Values);
        Assert.Equal("standard", configuration.Id);
        Assert.Equal("Game Mode = Off", configuration.Conditions);
        Assert.Contains("  verified: true", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
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
        Assert.True(reparsed.Anchors["normal"].Verified);
        Assert.Equal("standard", reparsed.Anchors["normal"].ConfigurationId);
        Assert.Equal("settings", reparsed.Anchors["normal"].ValidationSourceNodeId);
        var returnStrategy = Assert.IsType<MenuReturnStrategy>(
            reparsed.Anchors["normal"].ReturnStrategy);
        Assert.Equal("settings", returnStrategy.MenuRootNodeId);
        Assert.True(returnStrategy.AtMenuRoot.Verified);
        Assert.Equal("KEY_RETURN", Assert.Single(returnStrategy.AtMenuRoot.Operations).Key);
        Assert.False(returnStrategy.BelowMenuRoot.Verified);
        Assert.Equal(
            ["KEY_MENU", "KEY_RETURN"],
            returnStrategy.BelowMenuRoot.Operations.Select(operation => operation.Key));
        var nodeOverride = Assert.Single(returnStrategy.NodeOverrides!);
        Assert.Equal("settings", nodeOverride.NodeId);
        Assert.True(nodeOverride.Script.Verified);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
