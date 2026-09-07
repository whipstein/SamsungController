using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 4)]
    public async Task ExternalDisabledVerificationHighlightsNamedRowAndNeverActivatesIt(bool hideColor, int downCount)
    {
        var yaml = ExternalDisabledRowHighlightYaml.Replace("COLOR_VISIBILITY", hideColor
            ? "hiddenWhen: [{ externalState: hdmi-bit-depth, equals: 10-bit }]"
            : "disabled: true", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            var check = Assert.Single(controller.GetMenuDefinitionVerificationSnapshot().Checks,
                check => check.Id == "condition:external-disabled-behavior");
            Assert.Equal("auto-hdr-remastering", check.TargetNodeId);
            Assert.Contains("highlight Normal video / Settings / Expert Settings / Auto HDR Remastering", check.Description);

            var result = await controller.RunMenuDefinitionVerificationTestAsync(check.Id);
            Assert.Equal("auto-hdr-remastering", result.TargetNodeId);
            Assert.Contains("Highlighted", result.ActionDescription);
            Assert.Contains("without pressing Enter", result.ActionDescription);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER" }.Concat(Enumerable.Repeat("KEY_DOWN", downCount)), GetSentKeys(transport));
            Assert.Empty(result.AppliedUpdates);
            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("auto-hdr-remastering", navigation.State.NodeId);
            Assert.Equal("off", navigation.ControlValues["auto-hdr-remastering"]);
            Assert.Contains("disabled", Assert.Throws<InvalidOperationException>(() => controller.CreateNavigationPlan("auto-hdr-remastering")).Message);

            // Repeating the test at the same highlighted row must not send more keys.
            await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            transport.SentMessages.Clear();
            await controller.RunMenuDefinitionVerificationTestAsync(check.Id);
            Assert.Empty(GetSentKeys(transport));
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(candidate => candidate.Id == check.Id).Verified);
        }
    }

    [Theory]
    [InlineData("brightness", "KEY_DOWN", 5)]
    [InlineData("after-hdr", "KEY_UP", 2)]
    public async Task ExternalDisabledVerificationMovesDirectlyFromKnownSibling(string start, string direction, int count)
    {
        var yaml = ExternalDisabledRowHighlightYaml.Replace("COLOR_VISIBILITY", "disabled: true", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            await controller.NavigateToMenuNodeAsync(start);
            transport.SentMessages.Clear();
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-disabled-behavior");
            Assert.Equal(Enumerable.Repeat(direction, count), GetSentKeys(transport));
            Assert.Equal("auto-hdr-remastering", result.TargetNodeId);
            Assert.Equal("auto-hdr-remastering", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task ExternalDisabledVerificationRejectsWrongSignalBeforeSendingKeys()
    {
        var yaml = ExternalDisabledRowHighlightYaml.Replace("COLOR_VISIBILITY", "disabled: true", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.RunMenuDefinitionVerificationTestAsync("condition:external-disabled-behavior"));
            Assert.Contains("Bit depth = 10-bit", error.Message);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ExternalHiddenVerificationHighlightsNeighborWithoutTargetingAbsentRow()
    {
        var yaml = ExternalDisabledRowHighlightYaml.Replace("COLOR_VISIBILITY",
            "hiddenWhen: [{ externalState: hdmi-bit-depth, equals: 10-bit }]", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Equal("color", result.TargetNodeId);
            Assert.Contains("absent", result.ActionDescription);
            Assert.Contains("Tint", result.ActionDescription);
            Assert.Contains("next visible control", result.ActionDescription);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_DOWN" }, GetSentKeys(transport));
            Assert.Equal("tint", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task ExternalDisabledVerificationDoesNotSilentlyResetUnknownStartingPosition()
    {
        var yaml = ExternalDisabledRowHighlightYaml.Replace("COLOR_VISIBILITY", "disabled: true", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("brightness");
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Null(controller.GetMenuNavigationSnapshot().State.NodeId);
            transport.SentMessages.Clear();
            await Assert.ThrowsAsync<NavigationPlanningException>(() =>
                controller.RunMenuDefinitionVerificationTestAsync("condition:external-disabled-behavior"));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    private const string ExternalDisabledRowHighlightYaml = """
        version: 1
        id: external-disabled-row-highlight
        name: External disabled row highlight
        model: Test TV
        externalStates:
          - id: hdmi-bit-depth
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
                      - id: contrast
                        label: Contrast
                        controlType: slider
                        defaultValue: 25
                        minimumValue: 0
                        maximumValue: 50
                      - id: sharpness
                        label: Sharpness
                        controlType: slider
                        defaultValue: 0
                        minimumValue: 0
                        maximumValue: 20
                      - id: color
                        label: Color
                        controlType: slider
                        defaultValue: 25
                        minimumValue: 0
                        maximumValue: 50
                        COLOR_VISIBILITY
                      - id: tint
                        label: Tint
                        controlType: slider
                        defaultValue: 0
                        minimumValue: -15
                        maximumValue: 15
                      - id: auto-hdr-remastering
                        label: Auto HDR Remastering
                        controlType: switch
                        defaultValue: off
                        disabledWhen: [{ externalState: hdmi-bit-depth, equals: 10-bit }]
                      - id: a-other-row
                        label: Another conditional row
                        controlType: switch
                        defaultValue: off
                        disabledWhen: [{ externalState: hdmi-bit-depth, equals: 10-bit }]
                      - id: after-hdr
                        label: Later row
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
          - id: open-auto-hdr
            from: normal-video
            to: auto-hdr-remastering
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }, { key: KEY_DOWN, repeat: 5 }]
          - id: open-after-hdr
            from: normal-video
            to: after-hdr
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }, { key: KEY_DOWN, repeat: 7 }]
        """;
}
