using Microsoft.Extensions.DependencyInjection;
using SamsungController.Automation.Navigation;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData("selection", "10-bit", "gamma-10bit", "ST.2084", "BT.1886, 2.2")]
    [InlineData("selection", "8-bit", "gamma", "BT.1886, 2.2", "ST.2084")]
    [InlineData("submenu-selection", "10-bit", "gamma-10bit", "ST.2084", "BT.1886, 2.2")]
    [InlineData("indexed-selection", "10-bit", "gamma-10bit", "ST.2084", "BT.1886, 2.2")]
    public async Task HiddenSelectionInspectionOpensMatchingVariantWithoutChoosingAValue(
        string type, string depth, string visible, string expected, string excluded)
    {
        // For 8-bit the representative is after its visible counterpart, with
        // another visible control following: prefer the Gamma variant, not Tail.
        var yaml = GammaOptionsYaml.Replace("controlType: selection", $"controlType: {type}", StringComparison.Ordinal);
        if (depth == "8-bit")
            yaml = yaml.Replace("id: gamma-10bit", "id: a-gamma-10bit", StringComparison.Ordinal);
        if (type == "indexed-selection")
        {
            foreach (var hiddenDepth in new[] { "8-bit", "10-bit" })
            {
                var condition = $"hiddenWhen: [{{ externalState: depth, equals: {hiddenDepth} }}]";
                yaml = yaml.Replace(condition, $"{condition}\n          - id: z-value-{hiddenDepth}\n            label: Indexed adjustment\n            controlType: slider\n            defaultValue: 0\n            minimumValue: -3\n            maximumValue: 3\n            {condition}", StringComparison.Ordinal);
            }
        }
        new MenuDefinitionValidator().ValidateAndThrow(new MenuDefinitionParser().Parse(yaml));
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", depth);
            var before = controller.GetMenuNavigationSnapshot();
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Equal(visible, result.OpenedOptionsNodeId);
            Assert.NotNull(result.OptionsInspectionId);
            Assert.Null(result.ControlType); // Not general selection-behavior evidence.
            Assert.Empty(result.AppliedUpdates);
            Assert.Contains($"contains only: {expected}.", result.ActionDescription);
            Assert.Contains($"choices ({excluded}) must not be offered", result.ActionDescription);
            Assert.Equal(["KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            Assert.Equal(before.ControlValues, controller.GetMenuNavigationSnapshot().ControlValues);
            Assert.Equal(before.ExternalStates, controller.GetMenuNavigationSnapshot().ExternalStates);
            Assert.Equal(MenuStateConfidence.Unknown, controller.GetMenuNavigationSnapshot().State.Confidence);
            transport.SentMessages.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(result.CheckId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SetMenuExternalStateAsync("depth", depth == "10-bit" ? "8-bit" : "10-bit"));
            Assert.Throws<NavigationPlanningException>(() => controller.CreateNavigationPlan("brightness"));
            Assert.Empty(GetSentKeys(transport));

            Assert.Equal(0, await controller.RestoreMenuDefinitionVerificationTestAsync(result));
            Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
            Assert.Equal(visible, controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Null(controller.GetOpenVerificationOptionsTest());
            await controller.RestoreMenuDefinitionVerificationTestAsync(result);
            Assert.Equal(["KEY_RETURN"], GetSentKeys(transport)); // Idempotent dismissal.
            await controller.ReturnMenuDefinitionVerificationToKnownStateAsync();
            Assert.Equal(["KEY_RETURN", "KEY_EXIT"], GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationPageClosesOptionsOnPassAndFailIncludingAfterReopeningPage(bool passed)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(GammaOptionsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            const string checkId = "condition:external-hidden-behavior";
            await using (var firstPage = new VerificationPageTestRenderer(services) { CheckId = checkId })
            {
                await firstPage.StartAsync();
                await firstPage.ConfirmSignalAsync(true);
                await firstPage.ClickAsync("Run guided test");
                Assert.Contains("contains only: ST.2084", firstPage.LineText);
                Assert.Contains("Count pass", firstPage.LineText);
            }
            await using var page = new VerificationPageTestRenderer(services) { CheckId = checkId };
            await page.StartAsync();
            Assert.Contains("Count pass", page.LineText);
            transport.SentMessages.Clear();
            await page.ClickAsync(passed ? "Count pass" : "Failed");
            Assert.Equal(["KEY_RETURN", "KEY_EXIT"], GetSentKeys(transport));
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.Equal(passed, controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == checkId).Verified);
            Assert.Null(controller.GetOpenVerificationOptionsTest());

            if (passed)
            {
                await page.ConfirmSignalAsync(true);
                await page.ClickAsync("Open on TV");
                Assert.NotNull(controller.GetOpenVerificationOptionsTest());
                Assert.Contains("Count pass", page.LineText);
                Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == checkId).Verified);
                transport.SentMessages.Clear();
                await controller.RunMenuAnchorAsync("normal");
                Assert.Equal(["KEY_RETURN", "KEY_EXIT"], GetSentKeys(transport));
            }
        }
    }

    [Fact]
    public async Task ManualReturnDismissesInspectionOnceAndOldTestCannotCloseANewInspection()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(GammaOptionsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var first = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            transport.SentMessages.Clear();
            await controller.SendKeyAsync("KEY_RETURN");
            await controller.RestoreMenuDefinitionVerificationTestAsync(first);
            Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
            Assert.Equal("gamma-10bit", controller.GetMenuNavigationSnapshot().State.NodeId);

            await controller.ConfirmMenuVerificationSignalSetupAsync(first.CheckId, true);
            var second = await controller.RunMenuDefinitionVerificationTestAsync(first.CheckId);
            Assert.NotEqual(first.OptionsInspectionId, second.OptionsInspectionId);
            transport.SentMessages.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RestoreMenuDefinitionVerificationTestAsync(first));
            Assert.Empty(GetSentKeys(transport));
            await controller.RestoreMenuDefinitionVerificationTestAsync(second);
            Assert.Equal(["KEY_RETURN"], GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData("KEY_EXIT")]
    [InlineData("KEY_ENTER")]
    public async Task ManualCorrectionDoesNotReplayAnOutdatedOptionsDismissal(string key)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(GammaOptionsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            transport.SentMessages.Clear();
            await controller.SendKeyAsync(key);
            Assert.Null(controller.GetOpenVerificationOptionsTest());
            await controller.RestoreMenuDefinitionVerificationTestAsync(result);
            Assert.Equal([key], GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData("disabled: true")]
    [InlineData("label: Different selection")]
    public async Task HiddenSelectionInspectionDoesNotOpenDisabledOrUnrelatedChoices(string change)
    {
        var yaml = GammaOptionsYaml.Replace("id: gamma-10bit\n            label: Gamma",
            $"id: gamma-10bit\n            {(change.StartsWith("label:", StringComparison.Ordinal) ? change : $"label: Gamma\n            {change}")}", StringComparison.Ordinal);
        Assert.NotEqual(GammaOptionsYaml, yaml);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("depth", "10-bit");
            await controller.ConfirmMenuVerificationSignalSetupAsync("condition:external-hidden-behavior", true);
            var result = await controller.RunMenuDefinitionVerificationTestAsync("condition:external-hidden-behavior");
            Assert.Null(result.OptionsInspectionId);
            Assert.Equal(["KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_DOWN"], GetSentKeys(transport));
            Assert.Equal("gamma-10bit", controller.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    private const string GammaOptionsYaml = """
        version: 1
        id: gamma-options
        name: Gamma options inspection
        model: Test TV
        context: { firmware: '1' }
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
                  - id: gray
                    label: Gray row
                    controlType: action
                    disabled: true
                  - id: gamma
                    label: Gamma
                    controlType: selection
                    defaultValue: BT.1886
                    options: [BT.1886, '2.2']
                    hiddenWhen: [{ externalState: depth, equals: 10-bit }]
                  - id: gamma-10bit
                    label: Gamma
                    controlType: selection
                    defaultValue: ST.2084
                    options: [ST.2084]
                    hiddenWhen: [{ externalState: depth, equals: 8-bit }]
                  - id: tail
                    label: Tail
                    controlType: switch
                    defaultValue: off
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
        """;
}
