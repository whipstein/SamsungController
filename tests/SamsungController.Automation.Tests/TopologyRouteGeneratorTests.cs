using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class TopologyRouteGeneratorTests
{
    [Fact]
    public void GeneratesAllDescendantRoutesWithOneCoverageRoutePerTopLevelBranch()
    {
        var generated = TopologyRouteGenerator.Regenerate(CreateDefinition());
        var routes = generated.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology)
            .ToArray();

        Assert.Equal(8, routes.Length);
        Assert.Equal(3, routes.Count(transition => transition.IsValidationRoute));
        Assert.All(routes, transition => Assert.Equal("open-settings", transition.TopologySeedTransitionId));
        Assert.All(routes, transition => Assert.Equal("default", transition.ConfigurationId));
        Assert.All(routes, transition => Assert.False(transition.Verified));

        var picture = Assert.Single(routes, route => route.ToNodeId == "picture");
        Assert.Equal(
            ["KEY_MENU", "KEY_DOWN", "KEY_ENTER"],
            picture.Operations.Select(operation => operation.Key));
        Assert.Equal(1, picture.Operations[1].Repeat);

        var brightness = Assert.Single(routes, route => route.ToNodeId == "brightness");
        Assert.Equal(
            ["KEY_MENU", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN", "KEY_ENTER"],
            brightness.Operations.Select(operation => operation.Key));

        var pictureGroup = routes.Where(route => route.ValidationGroupId == "topology-open-settings-picture").ToArray();
        Assert.Equal(5, pictureGroup.Length);
        Assert.Equal("contrast", Assert.Single(pictureGroup, route => route.IsValidationRoute).ToNodeId);
    }

    [Fact]
    public void PreservesACompletedGroupUntilItsTopologyCommandsChange()
    {
        var initial = TopologyRouteGenerator.Regenerate(CreateDefinition());
        var verifiedTransitions = initial.Transitions.Values.Select(transition =>
            transition.ValidationGroupId == "topology-open-settings-picture"
            || transition.Id == "open-settings"
                ? transition with { Verified = true }
                : transition).ToArray();
        var verified = Copy(initial, initial.Nodes.Values, verifiedTransitions);

        var unchanged = TopologyRouteGenerator.Regenerate(verified);

        Assert.All(
            unchanged.Transitions.Values.Where(transition =>
                transition.ValidationGroupId == "topology-open-settings-picture"),
            transition => Assert.True(transition.Verified));

        var rerecordedSeed = Copy(
            unchanged,
            unchanged.Nodes.Values,
            unchanged.Transitions.Values.Select(transition => transition.Id == "open-settings"
                ? transition with { Verified = false }
                : transition));
        var invalidatedBySeed = TopologyRouteGenerator.Regenerate(rerecordedSeed);

        Assert.All(
            invalidatedBySeed.Transitions.Values.Where(transition =>
                transition.ValidationGroupId == "topology-open-settings-picture"),
            transition => Assert.False(transition.Verified));

        var reorderedNodes = verified.Nodes.Values
            .OrderBy(node => node.Id == "picture" ? 0 : node.Id == "general" ? 1 : 2)
            .ToArray();
        var reordered = Copy(verified, reorderedNodes, verified.Transitions.Values);
        var regenerated = TopologyRouteGenerator.Regenerate(reordered);

        Assert.All(
            regenerated.Transitions.Values.Where(transition =>
                transition.ValidationGroupId == "topology-open-settings-picture"),
            transition => Assert.False(transition.Verified));
    }

    [Fact]
    public void SkipsDisabledBranchesAndDoesNotCountTheirRowsInDirectionalOffsets()
    {
        var definition = new MenuDefinition(
            "disabled-topology",
            "Disabled Topology",
            "Test TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode(
                    "adaptive",
                    "Adaptive",
                    "settings",
                    ControlType: MenuControlType.Switch,
                    DefaultValue: "on"),
                new MenuNode(
                    "advanced",
                    "Advanced",
                    "settings",
                    DisabledWhen: [new MenuNodeDisabledCondition("adaptive", "on")]),
                new MenuNode(
                    "game-options",
                    "Game Options",
                    "settings",
                    HiddenWhen: [new MenuNodeHiddenCondition("adaptive", "on")]),
                new MenuNode("sound", "Sound", "settings"),
                new MenuNode(
                    "sound-output",
                    "Sound Output",
                    "sound",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "TV Speaker",
                    SelectionOptions: ["TV Speaker", "Receiver"])
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [new MenuOperation("KEY_MENU")])
            ],
            []);

        var generated = TopologyRouteGenerator.Regenerate(definition);
        var routes = generated.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology)
            .ToArray();

        Assert.DoesNotContain(routes, transition => transition.ToNodeId == "advanced");
        Assert.DoesNotContain(routes, transition => transition.ToNodeId == "game-options");
        var sound = Assert.Single(routes, transition => transition.ToNodeId == "sound");
        Assert.Equal(["KEY_MENU", "KEY_DOWN", "KEY_ENTER"], sound.Operations.Select(step => step.Key));
        Assert.Equal(1, sound.Operations[1].Repeat);
    }

    [Fact]
    public void PrefersANonConfirmationCoverageTarget()
    {
        var definition = new MenuDefinition(
            "safe-coverage",
            "Safe Coverage",
            "Test TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode("picture", "Picture", "settings"),
                new MenuNode(
                    "brightness",
                    "Brightness",
                    "picture",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "25",
                    MinimumValue: 0,
                    MaximumValue: 50),
                new MenuNode(
                    "reset",
                    "Reset",
                    "picture",
                    ControlType: MenuControlType.Confirmation,
                    DefaultValue: "Cancel",
                    SelectionOptions: ["Reset", "Cancel"])
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [new MenuOperation("KEY_MENU")])
            ],
            []);

        var generated = TopologyRouteGenerator.Regenerate(definition);
        var validationRoute = Assert.Single(generated.Transitions.Values, transition =>
            transition.GeneratedFromTopology && transition.IsValidationRoute);

        Assert.Equal("brightness", validationRoute.ToNodeId);
    }

    [Fact]
    public void AddingOneMenuItemInvalidatesOnlyItsAffectedCoverageBranch()
    {
        var initial = TopologyRouteGenerator.Regenerate(CreateDefinition());
        var fullyVerified = Copy(
            initial,
            initial.Nodes.Values,
            initial.Transitions.Values.Select(transition => transition with { Verified = true }));
        var withNewPictureItem = Copy(
            fullyVerified,
            fullyVerified.Nodes.Values.Append(new MenuNode(
                "color",
                "Color",
                "picture",
                ControlType: MenuControlType.Slider,
                DefaultValue: "25",
                MinimumValue: 0,
                MaximumValue: 50)),
            fullyVerified.Transitions.Values);

        var regenerated = TopologyRouteGenerator.Regenerate(withNewPictureItem);
        var generated = regenerated.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology)
            .ToArray();

        Assert.All(
            generated.Where(transition => transition.ValidationGroupId == "topology-open-settings-picture"),
            transition => Assert.False(transition.Verified));
        Assert.All(
            generated.Where(transition => transition.ValidationGroupId == "topology-open-settings-sound"),
            transition => Assert.True(transition.Verified));
        Assert.Single(generated, transition =>
            transition.ValidationGroupId == "topology-open-settings-picture"
            && transition.IsValidationRoute);
        Assert.DoesNotContain(
            generated.Where(transition => !transition.Verified),
            transition => transition.ValidationGroupId == "topology-open-settings-sound");
    }

    private static MenuDefinition CreateDefinition() => new(
        "topology-test",
        "Topology Test",
        "Test TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("tv-interface", "TV interface"),
            new MenuNode("normal-video", "Normal video", "tv-interface"),
            new MenuNode("settings", "Settings", "normal-video"),
            new MenuNode("general", "General", "settings"),
            new MenuNode("picture", "Picture", "settings"),
            new MenuNode("sound", "Sound", "settings"),
            new MenuNode(
                "picture-mode",
                "Picture Mode",
                "picture",
                ControlType: MenuControlType.Selection,
                DefaultValue: "Movie",
                SelectionOptions: ["Standard", "Movie"]),
            new MenuNode("expert", "Expert Settings", "picture"),
            new MenuNode(
                "brightness",
                "Brightness",
                "expert",
                ControlType: MenuControlType.Slider,
                DefaultValue: "25",
                MinimumValue: 0,
                MaximumValue: 50),
            new MenuNode(
                "contrast",
                "Contrast",
                "expert",
                ControlType: MenuControlType.Slider,
                DefaultValue: "45",
                MinimumValue: 0,
                MaximumValue: 50),
            new MenuNode(
                "sound-output",
                "Sound Output",
                "sound",
                ControlType: MenuControlType.Selection,
                DefaultValue: "TV Speaker",
                SelectionOptions: ["TV Speaker", "Receiver"])
        ],
        [
            new MenuTransition(
                "open-settings",
                "normal-video",
                "settings",
                [new MenuOperation("KEY_MENU")],
                ConfigurationId: "default")
        ],
        [],
        configurations: [new MenuConfiguration("default", "Default")],
        activeConfigurationId: "default");

    private static MenuDefinition Copy(
        MenuDefinition definition,
        IEnumerable<MenuNode> nodes,
        IEnumerable<MenuTransition> transitions) => new(
        definition.Id,
        definition.Name,
        definition.Model,
        definition.Context,
        nodes,
        transitions,
        definition.Anchors.Values,
        definition.Timing,
        definition.Configurations.Values,
        definition.ActiveConfigurationId);
}
