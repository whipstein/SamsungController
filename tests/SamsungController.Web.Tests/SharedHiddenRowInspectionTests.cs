using Microsoft.Extensions.DependencyInjection;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData("BT.1886")]
    [InlineData("2.2")]
    public async Task SharedHiddenRowTestMovesOneRowPastGammaInsteadOfReopeningExpertSettings(string initialValue)
    {
        var yaml = SharedHiddenGammaYaml.Replace("defaultValue: BT.1886", $"defaultValue: '{initialValue}'", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("gamma");
            transport.SentMessages.Clear();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:hidden-behavior");
            var expected = initialValue == "BT.1886"
                ? new[] { "KEY_ENTER", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN" }
                : new[] { "KEY_ENTER", "KEY_UP", "KEY_ENTER", "KEY_ENTER", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN" };
            Assert.Equal(expected, GetSentKeys(transport));
            Assert.Equal("shadow-detail", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal("2.2", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("0", controller.GetMenuNavigationSnapshot().ControlValues["shadow-detail"]);
            Assert.Equal("bt1886", result.TargetNodeId);
            Assert.Contains("Shadow Detail, the next visible control", result.ActionDescription);
            Assert.Contains("BT.1886 is absent", result.ActionDescription);
            Assert.Null(result.OptionsInspectionId);
            Assert.Equal(initialValue == "BT.1886" ? 1 : 0, result.AppliedUpdates.Count);
            await controller.RestoreMenuDefinitionVerificationTestAsync(result);
            Assert.Equal(initialValue, controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedHiddenRowPageShowsMissingAreaAndRestoresGammaOnPassOrFail(bool passed)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(SharedHiddenGammaYaml, installedMenu: true);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("gamma");
            transport.SentMessages.Clear();
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = "condition:hidden-behavior" };
            await page.StartAsync();
            await page.ConfirmSignalAsync(true);
            await page.ClickAsync("Run guided test");
            Assert.Equal(["KEY_ENTER", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN"], GetSentKeys(transport));
            Assert.Contains("Shadow Detail", page.LineText);
            Assert.Contains("BT.1886 is absent", page.LineText);
            Assert.Equal("shadow-detail", controller.GetMenuNavigationSnapshot().State.NodeId);
            await page.ClickAsync(passed ? "Count pass" : "Failed");
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(passed, SignalCheck(controller, "condition:hidden-behavior").Verified);
        }
    }

    [Theory]
    [InlineData("disabled: true", "shadow-detail", true)]
    [InlineData("hiddenWhen: [{ setting: gamma, equals: '2.2' }]", "gamma", false)]
    public async Task SharedHiddenRowInspectionCountsGrayRowsAndFallsBackWhenNoFollowingRowIsVisible(
        string availability, string expectedNode, bool moveDown)
    {
        var yaml = SharedHiddenGammaYaml.Replace("label: Shadow Detail", $"label: Shadow Detail\n            {availability}", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.NavigateToMenuNodeAsync("gamma");
            transport.SentMessages.Clear();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:hidden-behavior");
            var adjustment = new[] { "KEY_ENTER", "KEY_DOWN", "KEY_ENTER" };
            Assert.Equal(moveDown ? adjustment.Append("KEY_DOWN") : adjustment, GetSentKeys(transport));
            Assert.Equal(expectedNode, controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Contains(moveDown ? "next visible control" : "previous visible control", result.ActionDescription);
            Assert.Null(result.OpenedOptionsNodeId); // No Enter on the neighboring control.
            await controller.RestoreMenuDefinitionVerificationTestAsync(result);
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
        }
    }

    private const string SharedHiddenGammaYaml = """
        version: 1
        id: shared-hidden-gamma
        name: Shared hidden Gamma inspection
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
              - id: expert
                label: Expert Settings
                children:
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 50
                  - id: gamma
                    label: Gamma
                    controlType: selection
                    defaultValue: BT.1886
                    options: [BT.1886, '2.2']
                    hiddenWhen: [{ externalState: depth, equals: 10-bit }]
                  - id: bt1886
                    label: BT.1886
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -3
                    maximumValue: 3
                    hiddenWhen: [{ setting: gamma, equals: '2.2' }, { externalState: depth, equals: 10-bit }]
                  - id: st2084
                    label: ST.2084
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -3
                    maximumValue: 3
                    hiddenWhen: [{ externalState: depth, equals: 8-bit }]
                  - id: shadow-detail
                    label: Shadow Detail
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -5
                    maximumValue: 5
        anchors:
          - id: normal
            label: Normal video
            target: normal-video
            verified: true
            steps: [{ key: KEY_EXIT }]
        transitions:
          - id: open-expert
            from: normal-video
            to: expert
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }]
          - id: open-gamma
            from: normal-video
            to: gamma
            verified: true
            steps: [{ key: KEY_MENU }, { key: KEY_ENTER }, { key: KEY_DOWN }]
        """;
}
