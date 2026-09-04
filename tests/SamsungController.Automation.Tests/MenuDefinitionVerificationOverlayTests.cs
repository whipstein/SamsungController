using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionVerificationOverlayTests
{
    [Fact]
    public void CreateTopologyRemovesAllDisplayVerificationState()
    {
        var definition = CreateDefinition();

        var topology = MenuDefinitionVerificationOverlay.CreateTopology(definition);

        Assert.Null(topology.Verification);
        Assert.False(topology.Timing.Verified);
        Assert.All(topology.Transitions.Values, transition => Assert.False(transition.Verified));
        Assert.All(topology.Anchors.Values, anchor => Assert.False(anchor.Verified));
        var strategy = Assert.Single(topology.Anchors.Values).ReturnStrategy;
        Assert.NotNull(strategy);
        Assert.False(strategy.AtMenuRoot.Verified);
        Assert.False(strategy.BelowMenuRoot.Verified);
        Assert.False(Assert.Single(strategy.NodeOverrides!).Script.Verified);
    }

    [Fact]
    public void MatchingPersonalManifestIsAppliedOnlyToRuntimeDefinition()
    {
        var topology = MenuDefinitionVerificationOverlay.CreateTopology(CreateDefinition());
        var plan = MenuDefinitionVerificationPlanner.Create(topology);
        var manifest = new MenuVerificationManifest(
            plan.Display,
            plan.Checks.Select(check => new MenuVerificationRecord(
                check.Id,
                check.Fingerprint,
                DateTimeOffset.UtcNow)).ToArray());

        var effective = MenuDefinitionVerificationOverlay.Apply(topology, manifest);

        Assert.False(topology.Timing.Verified);
        Assert.All(topology.Transitions.Values, transition => Assert.False(transition.Verified));
        Assert.All(topology.Anchors.Values, anchor => Assert.False(anchor.Verified));
        Assert.True(effective.Timing.Verified);
        Assert.All(effective.Transitions.Values, transition => Assert.True(transition.Verified));
        Assert.All(effective.Anchors.Values, anchor => Assert.True(anchor.Verified));
        Assert.Same(manifest, effective.Verification);
    }

    [Fact]
    public void ChangedTopologyDoesNotInheritStaleRouteVerification()
    {
        var topology = MenuDefinitionVerificationOverlay.CreateTopology(CreateDefinition());
        var plan = MenuDefinitionVerificationPlanner.Create(topology);
        var manifest = new MenuVerificationManifest(
            plan.Display,
            plan.Checks.Select(check => new MenuVerificationRecord(
                check.Id,
                check.Fingerprint,
                DateTimeOffset.UtcNow)).ToArray());
        var route = Assert.Single(topology.Transitions.Values);
        var changed = new MenuDefinition(
            topology.Id,
            topology.Name,
            topology.Model,
            topology.Context,
            topology.Nodes.Values,
            [route with { Operations = [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_DOWN")] }],
            topology.Anchors.Values,
            topology.Timing,
            topology.Configurations.Values,
            topology.ActiveConfigurationId);

        var effective = MenuDefinitionVerificationOverlay.Apply(changed, manifest);

        Assert.False(Assert.Single(effective.Transitions.Values).Verified);
        Assert.True(effective.Timing.Verified);
    }

    private static MenuDefinition CreateDefinition()
    {
        return new MenuDefinition(
            "overlay-test",
            "Overlay test",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video")
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [new MenuOperation("KEY_MENU")],
                    true)
            ],
            [
                new MenuAnchor(
                    "normal-video",
                    "Return to normal video",
                    "normal-video",
                    [new MenuOperation("KEY_RETURN")],
                    true,
                    ValidationSourceNodeId: "settings",
                    ReturnStrategy: new MenuReturnStrategy(
                        "settings",
                        new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                        new MenuReturnScript(
                            [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_RETURN")],
                            true),
                        [
                            new MenuReturnOverride(
                                "settings",
                                new MenuReturnScript([new MenuOperation("KEY_RETURN")], true))
                        ]))
            ],
            new MenuTimingProfile(150, 800, 300, true));
    }
}
