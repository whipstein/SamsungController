using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyingPictureAndGeneralSettingsStaysInsideMenuBetweenChanges(bool returnToVideo)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(CrossSectionBatchMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("color");
            transport.SentMessages.Clear();
            var completed = new List<string>();
            await controller.ApplyMenuControlValuesAsync(
                [new("color", "25", "26"), new("autorun-hub", "off", "on"), new("autorun-mirroring", "on", "off")],
                controller.GetMenuNavigationSnapshot().ControlValues,
                returnToVideo,
                valueApplied: update => completed.Add(update.NodeId));

            var expected = new List<string>
            {
                "KEY_RIGHT", // Color adjustment completes inside Expert Settings.
                "KEY_RETURN", "KEY_RETURN", // Expert -> Picture -> Settings, keeping Picture highlighted.
                "KEY_DOWN", "KEY_DOWN", "KEY_DOWN", "KEY_ENTER", // Across to General & Privacy.
                "KEY_DOWN", "KEY_ENTER", // Start Screen Options.
                "KEY_ENTER", // Autorun Hub off -> on.
                "KEY_DOWN", "KEY_ENTER" // Next setting, then Autorun Mirroring on -> off.
            };
            if (returnToVideo)
                expected.AddRange(["KEY_MENU", "KEY_MENU"]);
            Assert.Equal(expected, GetSentKeys(transport));
            Assert.Equal(["color", "autorun-hub", "autorun-mirroring"], completed);
            Assert.Equal(returnToVideo ? "normal-video" : "autorun-mirroring",
                controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal("26", controller.GetMenuNavigationSnapshot().ControlValues["color"]);
            Assert.Equal("on", controller.GetMenuNavigationSnapshot().ControlValues["autorun-hub"]);
            Assert.Equal("off", controller.GetMenuNavigationSnapshot().ControlValues["autorun-mirroring"]);
        }
    }

    private const string CrossSectionBatchMenuYaml = """
        version: 1
        id: cross-section-batch
        name: Cross section batch
        model: Test TV
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: settings
                label: Settings
                children:
                  - id: all-settings
                    label: All Settings
                  - id: picture
                    label: Picture
                    children:
                      - id: mode
                        label: Picture Mode
                      - id: eyecomfort
                        label: EyeComfort
                      - id: calibration
                        label: Calibration
                      - id: panel-care
                        label: Panel Care
                      - id: expert
                        label: Expert Settings
                        children:
                          - id: brightness
                            label: Brightness
                          - id: contrast
                            label: Contrast
                          - id: sharpness
                            label: Sharpness
                          - id: color
                            label: Color
                            controlType: slider
                            minimumValue: 0
                            maximumValue: 50
                            defaultValue: 25
                  - id: sound
                    label: Sound
                  - id: connection
                    label: Connection
                  - id: general
                    label: General & Privacy
                    children:
                      - id: accessibility
                        label: Accessibility
                      - id: start-screen
                        label: Start Screen Options
                        children:
                          - id: autorun-hub
                            label: Autorun Hub
                            controlType: switch
                            defaultValue: off
                          - id: autorun-mirroring
                            label: Autorun Mirroring
                            controlType: switch
                            defaultValue: on
        anchors:
          - id: normal
            label: Return to normal video
            target: normal-video
            verified: true
            steps: [{ key: KEY_MENU, repeat: 2 }]
        transitions:
          - id: to-color
            from: normal-video
            to: color
            verified: true
            steps:
              - { key: KEY_MENU }
              - { key: KEY_DOWN }
              - { key: KEY_ENTER }
              - { key: KEY_DOWN, repeat: 4 }
              - { key: KEY_ENTER }
              - { key: KEY_DOWN, repeat: 3 }
          - id: to-autorun-hub
            from: normal-video
            to: autorun-hub
            verified: true
            steps:
              - { key: KEY_MENU }
              - { key: KEY_DOWN, repeat: 4 }
              - { key: KEY_ENTER }
              - { key: KEY_DOWN }
              - { key: KEY_ENTER }
          - id: to-autorun-mirroring
            from: normal-video
            to: autorun-mirroring
            verified: true
            steps:
              - { key: KEY_MENU }
              - { key: KEY_DOWN, repeat: 4 }
              - { key: KEY_ENTER }
              - { key: KEY_DOWN }
              - { key: KEY_ENTER }
              - { key: KEY_DOWN }
        """;
}
