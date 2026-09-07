using Microsoft.Extensions.DependencyInjection;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task VerificationOpensMenuOnlyAfterFinalAcceptedGuidedTest(bool displayConfirmed, bool passed)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(CompletionNavigationMenuYaml, installedMenu: true);
        await using (controller)
        {
            if (displayConfirmed)
                await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = "control:selection-behavior" };
            await page.StartAsync();
            page.Navigation.OnNavigate = () =>
            {
                Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
                Assert.Equal("Standard", controller.GetMenuNavigationSnapshot().ControlValues["picture-mode"]);
                Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
                Assert.Equal("KEY_RETURN", GetSentKeys(transport).Last());
            };

            await page.ClickAsync("Run guided test");
            Assert.Empty(page.Navigation.Destinations);
            Assert.Equal("Movie", controller.GetMenuNavigationSnapshot().ControlValues["picture-mode"]);
            await page.ClickAsync(passed ? "Count pass" : "Failed");

            Assert.Equal("Standard", controller.GetMenuNavigationSnapshot().ControlValues["picture-mode"]);
            Assert.Equal("normal-video", controller.GetMenuNavigationSnapshot().State.NodeId);
            if (displayConfirmed && passed)
            {
                Assert.Equal(["menu"], page.Navigation.Destinations);
                Assert.Equal("http://localhost/controller/menu", page.Navigation.Uri);
                await controller.ReloadMenuDefinitionAsync();
                Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            }
            else
            {
                Assert.False(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
                Assert.Empty(page.Navigation.Destinations);
            }
        }
    }

    [Fact]
    public async Task VerificationOpensMenuWhenDisplayConfirmationIsTheLastCheck()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(CompletionNavigationMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("control:selection-behavior");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = "display" };
            await page.StartAsync();
            Assert.Empty(page.Navigation.Destinations);
            await page.ClickAsync("Confirm this display");
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            Assert.Equal(["menu"], page.Navigation.Destinations);
            Assert.Empty(GetSentKeys(transport)); // Switching app pages does not send TV commands.
        }
    }

    [Fact]
    public async Task VerificationOpensMenuWhenCarryForwardCompletesRemainingChecks()
    {
        var (controller, _) = await CreateConnectedControllerAsync(CompletionNavigationMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await controller.ConfirmMenuSelectionBehaviorAsync("picture-mode");
            Assert.False(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services);
            await page.StartAsync();
            await page.ClickPageButtonAsync("Carry forward existing verified work");
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            Assert.Equal(["menu"], page.Navigation.Destinations);
        }
    }

    [Fact]
    public async Task VerificationOpensMenuAfterThirdPassOfTheFinalRoute()
    {
        var yaml = CompletionNavigationMenuYaml.Replace("to: picture-mode\n    verified: true", "to: picture-mode\n    verified: false", StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("control:selection-behavior");
            var pending = Assert.Single(controller.GetMenuDefinitionVerificationSnapshot().Checks, check => !check.Verified);
            Assert.Equal("route:default:open-picture-mode", pending.Id);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = pending.Id };
            await page.StartAsync();
            for (var pass = 1; pass <= 3; pass++)
            {
                await page.ClickAsync("Prepare start");
                await page.ClickAsync("Run test");
                Assert.Empty(page.Navigation.Destinations);
                await page.ClickAsync("Count pass");
                Assert.Equal(pass == 3, controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
                Assert.Equal(pass == 3 ? 1 : 0, page.Navigation.Destinations.Count);
            }
            Assert.Equal(["menu"], page.Navigation.Destinations);
        }
    }

    [Fact]
    public async Task CompletedVerificationCanBeRevisitedAndResultsRemovedWithoutRedirect()
    {
        var (controller, _) = await CreateConnectedControllerAsync(CompletionNavigationMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("control:selection-behavior");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = "display" };
            await page.StartAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            Assert.Empty(page.Navigation.Destinations);
            await page.ClickPageButtonAsync("Carry forward existing verified work");
            Assert.Empty(page.Navigation.Destinations);
            await page.ClickAsync("Remove validation");
            Assert.False(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            Assert.Empty(page.Navigation.Destinations);
            await page.ClickAsync("Confirm this display");
            Assert.Equal(["menu"], page.Navigation.Destinations);
        }
    }

    [Fact]
    public async Task FailedFinalReturnKeepsVerificationPageVisibleEvenWhenResultWasSaved()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(CompletionNavigationMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var page = new VerificationPageTestRenderer(services) { CheckId = "control:selection-behavior" };
            await page.StartAsync();
            await page.ClickAsync("Run guided test");
            void FailReturnAfterVerificationSaved()
            {
                if (controller.GetMenuDefinitionVerificationSnapshot().FullyVerified)
                    transport.SendFailure = new IOException("Simulated final return failure.");
            }
            controller.Changed += FailReturnAfterVerificationSaved;
            try
            {
                await page.ClickAsync("Count pass");
                Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
                Assert.Contains("Simulated final return failure", page.Text);
                Assert.Empty(page.Navigation.Destinations);
            }
            finally
            {
                controller.Changed -= FailReturnAfterVerificationSaved;
                transport.SendFailure = null;
            }
        }
    }

    private const string CompletionNavigationMenuYaml = """
        version: 1
        id: completion-navigation
        name: Completion navigation test
        model: Test TV
        context:
          firmware: test
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: picture-mode
                label: Picture Mode
                controlType: selection
                defaultValue: Standard
                options: [Standard, Movie]
        anchors:
          - id: normal
            label: Return to normal video
            target: normal-video
            verified: true
            steps:
              - key: KEY_RETURN
        transitions:
          - id: open-picture-mode
            from: normal-video
            to: picture-mode
            verified: true
            steps:
              - key: KEY_MENU
        """;
}
