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
            Assert.True(page.ButtonDisabled("Open on TV"));
            await page.ConfirmSignalAsync(true);
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
            await controller.ConfirmMenuDefinitionVerificationCheckAsync(checkId);
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
            Assert.True(SignalCheck(controller, checkId).Verified);
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
}
