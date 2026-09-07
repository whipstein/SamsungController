using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData(true, "replacement", "ST.2084", "next", 2)]
    [InlineData(false, "gray-before", "Gray before", "previous", 1)]
    public async Task ExternalHiddenTestHighlightsVisibleLocationWithoutActivatingOrChangingValues(
        bool followingVisible, string expectedNode, string expectedLabel, string direction, int downCount)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HiddenLocationYaml(followingVisible), installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            var before = controller.GetMenuNavigationSnapshot();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Equal("missing", result.TargetNodeId);
            Assert.Contains(expectedLabel, result.ActionDescription);
            Assert.Contains($"{direction} visible control", result.ActionDescription);
            Assert.Contains("BT.1886 is absent", result.ActionDescription);
            Assert.Empty(result.AppliedUpdates);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER" }.Concat(Enumerable.Repeat("KEY_DOWN", downCount)), GetSentKeys(transport));
            var after = controller.GetMenuNavigationSnapshot();
            Assert.Equal(expectedNode, after.State.NodeId);
            Assert.Equal(before.ControlValues, after.ControlValues);
            Assert.Equal(before.ExternalStates, after.ExternalStates);

            // The visible highlight, not the absent node, is the remembered position.
            await controller.ConfirmMenuDefinitionVerificationCheckAsync(result.CheckId);
            transport.SentMessages.Clear();
            await controller.ConfirmMenuVerificationSignalSetupAsync(result.CheckId, true);
            await controller.RunMenuDefinitionVerificationTestAsync(result.CheckId);
            Assert.Empty(GetSentKeys(transport));
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == result.CheckId).Verified);
        }
    }

    [Theory]
    [InlineData("brightness", "KEY_DOWN", 2)]
    [InlineData("tail", "KEY_UP", 1)]
    public async Task ExternalHiddenTestMovesFromKnownSiblingWithoutReopeningMenu(string start, string key, int count)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HiddenLocationYaml(), installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.NavigateToMenuNodeAsync(start);
            transport.SentMessages.Clear();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Equal(Enumerable.Repeat(key, count), GetSentKeys(transport));
            Assert.Equal("replacement", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task ExternalHiddenTestReportsManualInspectionWhenNoVisibleControlRemains()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HiddenLocationYaml(false, allHidden: true), installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Contains("no neighboring control", result.ActionDescription);
            Assert.Equal("expert", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER" }, GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ExternalHiddenTestNeverEntersANeighboringSubmenu()
    {
        var yaml = HiddenLocationYaml().Replace(
            "              - id: replacement\n                label: ST.2084\n                controlType: slider\n                defaultValue: 0\n                minimumValue: -3\n                maximumValue: 3",
            "              - id: replacement\n                label: Neighboring submenu\n                controlType: submenu", StringComparison.Ordinal);
        Assert.NotEqual(HiddenLocationYaml(), yaml);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Contains("Tail", result.ActionDescription);
            Assert.Equal("tail", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_DOWN" }, GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ExternalHiddenTestDistinguishesSameNamedSignalVariants()
    {
        var yaml = HiddenLocationYaml().Replace("label: BT.1886", "label: Gamma", StringComparison.Ordinal)
            .Replace("label: ST.2084", "label: Gamma", StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Contains("Gamma [replacement]", result.ActionDescription);
            Assert.Contains("Gamma [missing] is absent", result.ActionDescription);
            Assert.Equal("replacement", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task ExternalHiddenTestRejectsWrongSignalAndUnknownStartWithoutSendingKeys()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HiddenLocationYaml(), installedMenu: true);
        await using (controller)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior"));
            Assert.Empty(GetSentKeys(transport));
            await controller.NavigateToMenuNodeAsync("brightness");
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            transport.SentMessages.Clear();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            await Assert.ThrowsAsync<NavigationPlanningException>(() => controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior"));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    private static string HiddenLocationYaml(bool followingVisible = true, bool allHidden = false) => HiddenRowPositionYaml
        .Replace("FOLLOWING_VISIBILITY", followingVisible ? "" : "hiddenWhen: [{ setting: master, equals: off }]", StringComparison.Ordinal)
        .Replace("BEFORE_VISIBILITY", allHidden ? "hiddenWhen: [{ setting: master, equals: off }]" : "", StringComparison.Ordinal);

    private const string HiddenRowPositionYaml = """
        version: 1
        id: hidden-row-position
        name: Hidden row position
        model: Test TV
        externalStates:
          - id: depth
            label: Bit depth
            defaultValue: 8-bit
            options: [8-bit, 10-bit]
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: settings
                label: Settings
                children:
                  - id: expert
                    label: Expert Settings
                    children:
                      - id: brightness
                        label: Brightness
                        controlType: slider
                        defaultValue: 25
                        minimumValue: 0
                        maximumValue: 50
                        BEFORE_VISIBILITY
                      - id: gray-before
                        label: Gray before
                        controlType: action
                        disabled: true
                        BEFORE_VISIBILITY
                      - id: missing
                        label: BT.1886
                        controlType: slider
                        defaultValue: 0
                        minimumValue: -3
                        maximumValue: 3
                        hiddenWhen: [{ externalState: depth, equals: 10-bit }]
                      - id: adjacent-hidden
                        label: Another absent row
                        controlType: action
                        hiddenWhen: [{ setting: master, equals: off }]
                      - id: replacement
                        label: ST.2084
                        controlType: slider
                        defaultValue: 0
                        minimumValue: -3
                        maximumValue: 3
                        FOLLOWING_VISIBILITY
                      - id: tail
                        label: Tail
                        controlType: switch
                        defaultValue: off
                        FOLLOWING_VISIBILITY
                  - id: master
                    label: Master
                    controlType: switch
                    defaultValue: off
        anchors:
          - id: normal
            label: Normal video
            target: normal-video
            verified: true
            steps: [{ key: KEY_RETURN }]
        transitions:
          - id: open-expert
            from: normal-video
            to: expert
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }]
          - id: open-brightness
            from: normal-video
            to: brightness
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }]
          - id: open-tail
            from: normal-video
            to: tail
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }, { key: KEY_DOWN, repeat: 3 }]
        """;
}
