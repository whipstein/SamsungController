using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData("disabledWhen", "disabled")]
    [InlineData("hiddenWhen", "hidden")]
    public async Task GuidedVerificationPreservesExternalSignalAndRestoresPrerequisiteValues(
        string condition, string behavior)
    {
        var yaml = ExternalStateVerificationMenuYaml.Replace("SIGNAL_CONDITION", condition, StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.RunMenuDefinitionVerificationTestAsync("control:selection-behavior"));
            Assert.Contains("No currently available", error.Message);
            Assert.Empty(GetSentKeys(transport));

            await controller.ConfirmMenuVerificationSignalSetupAsync($"condition:external-{behavior}-behavior", true);
            var externalTest = await controller.RunMenuDefinitionVerificationTestAsync(
                $"condition:external-{behavior}-behavior");
            Assert.Equal("signal-controls", externalTest.TargetNodeId);
            Assert.Equal("settings", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Empty(externalTest.AppliedUpdates);
            Assert.Equal(0, await controller.RestoreMenuDefinitionVerificationTestAsync(externalTest));
            Assert.Equal("YCbCr422", Assert.Single(controller.GetMenuNavigationSnapshot().ExternalStates!).Value);

            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            var test = await controller.RunMenuDefinitionVerificationTestAsync("control:selection-behavior");
            Assert.Equal("hdmi-black-level", test.TargetNodeId);
            Assert.Equal(["master", "hdmi-black-level"], test.AppliedUpdates.Select(update => update.NodeId));
            Assert.Equal("on", controller.GetMenuNavigationSnapshot().ControlValues["master"]);
            Assert.Equal("Low", controller.GetMenuNavigationSnapshot().ControlValues["hdmi-black-level"]);

            Assert.Equal(2, await controller.RestoreMenuDefinitionVerificationTestAsync(test));
            var navigation = controller.GetMenuNavigationSnapshot();
            Assert.Equal("off", navigation.ControlValues["master"]);
            Assert.Equal("Auto", navigation.ControlValues["hdmi-black-level"]);
            Assert.Equal("RGB", Assert.Single(navigation.ExternalStates!).Value);
            Assert.DoesNotContain(navigation.ControlValues.Keys, key => key.StartsWith("external-state:", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("disabledWhen", "disabled")]
    [InlineData("hiddenWhen", "hidden")]
    public async Task ApplyingExpandedValueSnapshotUsesCurrentExternalSignal(
        string condition, string behavior)
    {
        var yaml = ExternalStateVerificationMenuYaml.Replace("SIGNAL_CONDITION", condition, StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            var knownValues = new Dictionary<string, string>
            {
                ["master"] = "on",
                ["hdmi-black-level"] = "Auto",
                ["external-state:pgen-output-format"] = "RGB"
            };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("hdmi-black-level", "Auto", "Low")],
                knownValues,
                returnToNormalVideo: false));
            Assert.Contains(behavior, error.Message);
            Assert.Empty(GetSentKeys(transport));
            Assert.Equal("YCbCr422", Assert.Single(controller.GetMenuNavigationSnapshot().ExternalStates!).Value);

            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            knownValues["external-state:pgen-output-format"] = "YCbCr422";
            await controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("hdmi-black-level", "Auto", "Low")],
                knownValues,
                returnToNormalVideo: false);
            Assert.Equal("Low", controller.GetMenuNavigationSnapshot().ControlValues["hdmi-black-level"]);
            Assert.Equal("RGB", Assert.Single(controller.GetMenuNavigationSnapshot().ExternalStates!).Value);
        }
    }

    [Theory]
    [InlineData("missing-control")]
    [InlineData("external-state:missing-signal")]
    public async Task ExpandedValueSnapshotStillRejectsUnknownControlIds(string unknownId)
    {
        var yaml = ExternalStateVerificationMenuYaml.Replace("SIGNAL_CONDITION", "disabledWhen", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => controller.ApplyMenuControlValuesAsync(
                [new MenuControlValueUpdate("master", "off", "on")],
                new Dictionary<string, string> { [unknownId] = "RGB" },
                returnToNormalVideo: false));
            Assert.Contains(unknownId, error.Message);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    private const string ExternalStateVerificationMenuYaml =
        """
        version: 1
        id: external-state-verification
        name: External State Verification
        model: Test TV
        externalStates:
          - id: pgen-output-format
            label: External HDMI Signal
            defaultValue: RGB
            options: [RGB, YCbCr422, YCbCr444]
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: settings
                label: Settings
                children:
                  - id: signal-controls
                    label: Signal controls
                    SIGNAL_CONDITION:
                      - externalState: pgen-output-format
                        equals: YCbCr422
                      - externalState: pgen-output-format
                        equals: YCbCr444
                    children:
                      - id: master
                        label: Master
                        controlType: switch
                        defaultValue: off
                      - id: hdmi-black-level
                        label: HDMI Black Level
                        controlType: selection
                        defaultValue: Auto
                        options: [Auto, Low, Normal]
                        disabledWhen:
                          - setting: master
                            equals: off
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
            verified: true
            steps:
              - key: KEY_MENU
          - id: open-signal-controls
            from: normal-video
            to: signal-controls
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_ENTER
          - id: open-master
            from: normal-video
            to: master
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_ENTER
              - key: KEY_ENTER
          - id: open-hdmi-black-level
            from: normal-video
            to: hdmi-black-level
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_ENTER
              - key: KEY_DOWN
              - key: KEY_ENTER
        """;
}
