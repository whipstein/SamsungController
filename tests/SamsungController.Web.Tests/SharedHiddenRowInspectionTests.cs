using Microsoft.Extensions.DependencyInjection;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Fact]
    public async Task ConfirmingGammaStartCorrectsStalePredictionAndCheckboxStaysCheckedThroughoutTest()
    {
        const string checkId = "condition:hidden-behavior";
        var (controller, transport) = await CreateConnectedControllerAsync(SharedHiddenGammaYaml, installedMenu: true);
        await using (controller)
        {
            // A previous test left the app's persisted current value at 2.2.
            // The user has since put the TV back to BT.1886 outside the app.
            await controller.ApplyMenuControlValuesAsync([new("gamma", "BT.1886", "2.2")],
                controller.GetMenuNavigationSnapshot().ControlValues, returnToNormalVideo: false);
            await controller.ReloadMenuDefinitionAsync();
            Assert.Equal("2.2", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            transport.SentMessages.Clear();
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = checkId };
            await page.StartAsync();
            Assert.Contains("TV starting setting: Gamma = BT.1886", page.BoldLineText);
            Assert.Contains("App currently records Gamma = 2.2", page.LineText);
            Assert.True(page.ButtonDisabled("Run guided test"));
            await page.ConfirmSignalAsync(true);
            Assert.Empty(GetSentKeys(transport));
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);

            var renderedChecks = new List<bool>();
            page.OnRendered = () => renderedChecks.Add(page.SignalConfirmationChecked);
            await page.ClickAsync("Run guided test");
            page.OnRendered = null;
            Assert.NotEmpty(renderedChecks);
            Assert.All(renderedChecks, value => Assert.True(value));
            Assert.Equal(["KEY_ENTER", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN"], GetSentKeys(transport));
            Assert.True(page.SignalConfirmationChecked);
            Assert.True(SignalCheck(controller, checkId).SignalSetupConfirmed);

            transport.SentMessages.Clear();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            Assert.Contains("Finish the previous test", error.Message);
            Assert.Empty(GetSentKeys(transport)); // No hidden Up/reset from the temporary 2.2 value.
            Assert.True(page.SignalConfirmationChecked);
            await page.ClickAsync("Failed");
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            Assert.True(page.SignalConfirmationChecked);
            Assert.False(page.ButtonDisabled("Run guided test"));
            await controller.ReloadMenuDefinitionAsync();
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            Assert.False(page.SignalConfirmationChecked); // A reload still requires a fresh physical check.
        }
    }

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
            // Confirmation establishes BT.1886 even when the saved prediction
            // was 2.2; there must be no preliminary Up/reset round trip.
            var expected = new[] { "KEY_ENTER", "KEY_DOWN", "KEY_ENTER", "KEY_DOWN" };
            Assert.Equal(expected, GetSentKeys(transport));
            Assert.Equal("shadow-detail", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal("2.2", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("0", controller.GetMenuNavigationSnapshot().ControlValues["shadow-detail"]);
            Assert.Equal("bt1886", result.TargetNodeId);
            Assert.Contains("Shadow Detail, the next visible control", result.ActionDescription);
            Assert.Contains("BT.1886 is absent", result.ActionDescription);
            Assert.Null(result.OptionsInspectionId);
            Assert.Single(result.AppliedUpdates);
            await controller.RestoreMenuDefinitionVerificationTestAsync(result);
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma"]);
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
