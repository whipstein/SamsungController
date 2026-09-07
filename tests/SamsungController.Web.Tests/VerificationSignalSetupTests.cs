using Microsoft.Extensions.DependencyInjection;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData("condition:external-disabled-behavior")]
    [InlineData("condition:external-hidden-behavior")]
    public async Task SignalDependentPageRequiresBoldPhysicalSetupAndMatchingAppValuesForEveryRun(string checkId)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HdmiBitDepthMenuYaml, installedMenu: true);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = checkId };
            await page.StartAsync();
            var initial = SignalCheck(controller, checkId);
            Assert.Equal(2, initial.SignalRequirements!.Count);
            Assert.Contains("REQUIRED BEFORE RUNNING: Set the physical HDMI source", page.BoldLineText);
            foreach (var requirement in initial.SignalRequirements)
                Assert.Contains($"{requirement.Label}: {requirement.RequiredValue}", page.BoldLineText);
            Assert.True(page.ButtonDisabled("Run guided test"));
            Assert.False(page.SignalConfirmationChecked);
            Assert.Equal(initial.SignalRequirements.Any(item => !item.Matches), page.SignalConfirmationDisabled);

            await MatchSignalRequirementsAsync(controller, checkId);
            Assert.True(page.ButtonDisabled("Run guided test")); // App selectors alone are not physical confirmation.
            Assert.False(page.SignalConfirmationDisabled);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            Assert.Contains("checkbox", error.Message);
            Assert.Empty(GetSentKeys(transport));
            await page.ConfirmSignalAsync(true);
            Assert.True(page.SignalConfirmationChecked);
            Assert.False(page.ButtonDisabled("Run guided test"));
            await page.ConfirmSignalAsync(false);
            Assert.True(page.ButtonDisabled("Run guided test"));
            await page.ConfirmSignalAsync(true);
            Assert.Empty(GetSentKeys(transport)); // Acknowledgement never changes the TV or source.

            await page.ClickAsync("Run guided test");
            Assert.NotEmpty(GetSentKeys(transport));
            Assert.True(page.SignalConfirmationDisabled);
            Assert.Contains("Signal setup confirmed for this run", page.LineText);
            await page.ClickAsync("Failed");
            Assert.True(page.ButtonDisabled("Run guided test"));
            Assert.False(page.SignalConfirmationChecked);
            await page.ConfirmSignalAsync(true);
            await page.ClickAsync("Run guided test");
            await page.ClickAsync("Count pass");
            Assert.True(SignalCheck(controller, checkId).Verified);
            Assert.Empty(SignalCheck(controller, checkId).SignalRequirements!);
            Assert.DoesNotContain("REQUIRED BEFORE RUNNING", page.LineText);
            Assert.False(page.ButtonDisabled("Open on TV"));
            await page.ClickAsync("Open on TV");
            Assert.False(SignalCheck(controller, checkId).SignalSetupConfirmed);
            Assert.True(SignalCheck(controller, checkId).Verified);
            await controller.ReturnMenuDefinitionVerificationToKnownStateAsync();
        }
    }

    [Fact]
    public async Task SignalSetupCannotBeConfirmedForWrongValuesAndAnySignalChangeInvalidatesIt()
    {
        const string checkId = "condition:external-disabled-behavior";
        var (controller, transport) = await CreateConnectedControllerAsync(HdmiBitDepthMenuYaml, installedMenu: true);
        await using (controller)
        {
            var mismatch = Assert.Single(SignalCheck(controller, checkId).SignalRequirements!, item => !item.Matches);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true));
            Assert.Contains(mismatch.RequiredValue, error.Message);
            Assert.Contains(mismatch.SelectedValue, error.Message);
            await MatchSignalRequirementsAsync(controller, checkId);
            await controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true);
            Assert.True(SignalCheck(controller, checkId).SignalSetupConfirmed);
            Assert.False(SignalCheck(controller, "condition:external-hidden-behavior").SignalSetupConfirmed);

            foreach (var signal in controller.GetMenuNavigationSnapshot().ExternalStates!)
            {
                var alternative = signal.Options.First(option => option != signal.Value);
                await controller.SetMenuExternalStateAsync(signal.Id, alternative);
                Assert.False(SignalCheck(controller, checkId).SignalSetupConfirmed);
                await controller.SetMenuExternalStateAsync(signal.Id, signal.Value);
                Assert.False(SignalCheck(controller, checkId).SignalSetupConfirmed); // Changing back does not revive it.
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
                await controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true);
            }
            Assert.Empty(GetSentKeys(transport));
            await controller.RunMenuDefinitionVerificationTestAsync(checkId);
            Assert.False(SignalCheck(controller, checkId).SignalSetupConfirmed);
            transport.SentMessages.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadOrReconnectClearsSignalSetupWithoutRemovingSavedVerification(bool reconnect)
    {
        const string checkId = "condition:external-disabled-behavior";
        var (controller, transport) = await CreateConnectedControllerAsync(HdmiBitDepthMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await MatchSignalRequirementsAsync(controller, checkId);
            await controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true);
            Assert.True(SignalCheck(controller, checkId).SignalSetupConfirmed);
            if (reconnect)
            {
                await controller.DisconnectAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true));
                await controller.ConnectAsync(new TvConnectionRequest("Test TV", "192.0.2.10", true, null,
                    PostConnectWarmupMilliseconds: 0, ReconnectAfterIdleSeconds: 0));
            }
            else
                await controller.ReloadMenuDefinitionAsync();
            Assert.False(SignalCheck(controller, checkId).SignalSetupConfirmed);
            Assert.True(SignalCheck(controller, "display").Verified);
            transport.SentMessages.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task SignalConfirmationIsNotRequiredForAnUnrelatedControlTest()
    {
        var (controller, _) = await CreateConnectedControllerAsync(HdmiBitDepthMenuYaml, installedMenu: true);
        await using (controller)
        {
            const string checkId = "control:selection-behavior";
            Assert.Empty(SignalCheck(controller, checkId).SignalRequirements!);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = checkId };
            await page.StartAsync();
            Assert.DoesNotContain("REQUIRED BEFORE RUNNING", page.LineText);
            Assert.False(page.ButtonDisabled("Run guided test"));
            await page.ClickAsync("Run guided test");
            Assert.Contains("Count pass", page.LineText);
        }
    }

    private static MenuDefinitionVerificationCheckSummary SignalCheck(SamsungControllerService controller, string checkId) =>
        controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == checkId);

    private static async Task MatchSignalRequirementsAsync(SamsungControllerService controller, string checkId)
    {
        foreach (var signal in SignalCheck(controller, checkId).SignalRequirements!)
            await controller.SetMenuExternalStateAsync(signal.StateId, signal.RequiredValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingMenuControlledCheckRequiresItsSignalPrerequisitesButCompletedChecksDoNot(bool disabledEffect)
    {
        var checkId = disabledEffect ? "condition:disabled-behavior" : "condition:hidden-behavior";
        var yaml = HdmiBitDepthMenuYaml.Replace("label: Settings\n        children:",
            "label: Settings\n        disabledWhen: [{ externalState: pgen-output-format, equals: YCbCr422 }, { externalState: pgen-output-format, equals: YCbCr444 }]\n        children:", StringComparison.Ordinal);
        Assert.NotEqual(HdmiBitDepthMenuYaml, yaml);
        if (disabledEffect)
        {
            var changed = yaml.Replace("\n              - setting: gamma-8bit\n                equals: \"2.2\"",
                "\n            disabledWhen: [{ setting: gamma-8bit, equals: '2.2' }]", StringComparison.Ordinal);
            Assert.NotEqual(yaml, changed);
            yaml = changed;
        }
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            // Reproduce the user's situation: both explicitly external tests
            // are complete, but the Gamma-driven conditional test is pending.
            foreach (var id in new[] { "condition:external-disabled-behavior", "condition:external-hidden-behavior" })
                await controller.ConfirmMenuDefinitionVerificationCheckAsync(id);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services);
            await page.StartAsync();
            foreach (var id in new[] { "condition:external-disabled-behavior", "condition:external-hidden-behavior" })
            {
                page.CheckId = id;
                Assert.True(SignalCheck(controller, id).Verified);
                Assert.Empty(SignalCheck(controller, id).SignalRequirements!);
                Assert.DoesNotContain("REQUIRED BEFORE RUNNING", page.LineText);
                Assert.False(page.ButtonDisabled("Open on TV"));
            }
            page.CheckId = checkId;
            Assert.Contains("Bit depth: 8-bit", page.BoldLineText);
            Assert.Contains("Color format: RGB", page.BoldLineText);
            Assert.True(page.ButtonDisabled("Run guided test"));
            Assert.True(page.SignalConfirmationDisabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmMenuVerificationSignalSetupAsync(checkId, true));
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            Assert.True(page.SignalConfirmationDisabled); // HDMI format still mismatches the parent menu.
            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            Assert.False(page.SignalConfirmationDisabled);
            Assert.True(page.ButtonDisabled("Run guided test"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunMenuDefinitionVerificationTestAsync(checkId));
            Assert.Empty(GetSentKeys(transport));
            await page.ConfirmSignalAsync(true);
            await page.ClickAsync("Run guided test");
            Assert.Contains("Set Gamma to 2.2", page.LineText);
            await page.ClickAsync("Count pass");
            Assert.True(SignalCheck(controller, checkId).Verified);
            Assert.Equal("BT.1886", controller.GetMenuNavigationSnapshot().ControlValues["gamma-8bit"]);
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            Assert.DoesNotContain("REQUIRED BEFORE RUNNING", page.LineText);
            await page.ClickAsync("Remove validation");
            Assert.Contains("Bit depth: 8-bit", page.BoldLineText);
            Assert.True(page.ButtonDisabled("Run guided test"));
        }
    }
}
