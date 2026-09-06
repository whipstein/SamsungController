using Microsoft.Extensions.DependencyInjection;
using SamsungController.Automation.Navigation;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Fact]
    public async Task EveryDefinedEvidenceCheckHasAnInlineActionEvenWhenHiddenFromBuildList()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(TopologyVerificationMenuYaml, installedMenu: true);
        await using (controller)
        {
            Assert.DoesNotContain(controller.GetMenuAuthoringSnapshot().DraftCandidates,
                candidate => candidate.Id == "open-settings");
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services);
            await renderer.StartAsync();
            foreach (var check in controller.GetMenuDefinitionVerificationSnapshot().Checks.Where(check =>
                         check.Kind is MenuVerificationCheckKind.Timing or MenuVerificationCheckKind.Anchor
                             or MenuVerificationCheckKind.ReturnScript or MenuVerificationCheckKind.Route))
            {
                renderer.CheckId = check.Id;
                Assert.DoesNotContain("Add missing definition", renderer.LineText);
                Assert.Contains(check.RelatedReturnCheckId is not null ? "Go to matching return test" : "Run test", renderer.LineText);
            }

            renderer.CheckId = "route:default:open-settings";
            await renderer.ClickAsync("Prepare start");
            Assert.Equal(["KEY_EXIT", "KEY_EXIT"], GetSentKeys(transport));
            for (var pass = 0; pass < 3; pass++)
            {
                transport.SentMessages.Clear();
                await renderer.ClickAsync("Run test");
                Assert.Equal(["KEY_MENU"], GetSentKeys(transport));
                await renderer.ClickAsync("Count pass");
            }

            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "route:default:open-settings").Verified);
        }
    }

    [Fact]
    public async Task GeneratedBranchVerificationAlsoPersistsCoveredSettingsEntry()
    {
        var (controller, _) = await CreateConnectedControllerAsync(TopologyVerificationMenuYaml, installedMenu: true);
        await using (controller)
        {
            var branch = controller.GetMenuAuthoringSnapshot().DraftCandidates.First(candidate => candidate.GeneratedFromTopology);
            for (var pass = 0; pass < 3; pass++)
            {
                await controller.PrepareMenuAuthoringValidationSourceAsync(branch.Kind, branch.Id);
                await controller.RunMenuAuthoringValidationAsync(branch.Kind, branch.Id);
                await controller.ConfirmMenuAuthoringValidationAsync(true);
            }

            await controller.ReloadMenuDefinitionAsync();
            var checks = controller.GetMenuDefinitionVerificationSnapshot().Checks;
            Assert.True(checks.Single(check => check.AuthoringItemId == branch.Id).Verified);
            Assert.True(checks.Single(check => check.Id == "route:default:open-settings").Verified);
            Assert.DoesNotContain(controller.GetMenuAuthoringSnapshot().DraftCandidates, candidate => candidate.Id == branch.Id);
            await controller.RemoveMenuDefinitionVerificationCheckAsync("route:default:open-settings");
            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "route:default:open-settings").ExistingEvidenceReady);
            await controller.CarryForwardExistingMenuVerificationAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check =>
                check.Id == "route:default:open-settings").Verified);
        }
    }

    [Theory]
    [InlineData("anchor:default:normal")]
    [InlineData("return:default:normal:menu-root")]
    [InlineData("return:default:normal:below-root")]
    [InlineData("return:default:normal:override:picture")]
    [InlineData("timing")]
    public async Task InstalledEvidenceChecksPrepareRunAndSaveWithoutRewritingMenu(string checkId)
    {
        var (controller, _) = await CreateConnectedControllerAsync(TopologyVerificationMenuYaml, installedMenu: true);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services) { CheckId = checkId };
            await renderer.StartAsync();
            for (var pass = 0; pass < 3; pass++)
            {
                await renderer.ClickAsync("Prepare start");
                Assert.DoesNotContain("No verified", renderer.LineText);
                await renderer.ClickAsync("Run test");
                await renderer.ClickAsync("Count pass");
            }

            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == checkId).Verified);
        }
    }

    [Theory]
    [InlineData("slider", "5", "minimumValue: 0\n        maximumValue: 10")]
    [InlineData("switch", "off", "")]
    [InlineData("selection", "Low", "options: [Low, High]")]
    [InlineData("submenu-selection", "Low", "options: [Low, High]")]
    [InlineData("indexed-selection", "5%", "options: [5%, 10%]")]
    [InlineData("confirmation", "Apply", "options: [Apply, Cancel]")]
    public async Task EveryGuidedControlTypeCanBeVerifiedOnAnInstalledMenu(
        string controlType, string defaultValue, string fields)
    {
        var yaml = $$"""
            version: 1
            id: installed-control-audit
            name: Installed Control Audit
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
                children:
                  - id: control
                    label: Test control
                    controlType: {{controlType}}
                    defaultValue: "{{defaultValue}}"
                    {{fields}}
            anchors:
              - id: normal
                label: Normal video
                target: normal-video
                verified: true
                steps:
                  - key: KEY_RETURN
            transitions:
              - id: open-control
                from: normal-video
                to: control
                verified: true
                steps:
                  - key: KEY_MENU
            """;
        if (controlType == "indexed-selection")
        {
            yaml = yaml.Replace("anchors:", "      - id: indexed-value\n        label: Indexed value\n        controlType: slider\n        defaultValue: 0\n        minimumValue: -50\n        maximumValue: 50\nanchors:", StringComparison.Ordinal);
        }

        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            Assert.NotNull(controller.GetMenuNavigationSnapshot().DefinitionName);
            var expectedKind = controlType switch
            {
                "slider" => MenuVerificationCheckKind.SliderBehavior,
                "switch" => MenuVerificationCheckKind.Switch,
                "confirmation" => MenuVerificationCheckKind.Confirmation,
                _ => MenuVerificationCheckKind.Selection
            };
            var check = Assert.Single(controller.GetMenuDefinitionVerificationSnapshot().Checks, check =>
                check.Kind == expectedKind);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services) { CheckId = check.Id };
            await renderer.StartAsync();
            await renderer.ClickAsync("Run guided test");
            await renderer.ClickAsync("Count pass");
            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(item => item.Id == check.Id).Verified);
            if (controlType != "confirmation")
            {
                Assert.Equal(defaultValue, controller.GetMenuNavigationSnapshot().ControlValues["control"]);
            }
            renderer.CheckId = "display";
            await renderer.ClickAsync("Confirm this display");
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(item => item.Id == "display").Verified);
        }
    }

    [Fact]
    public async Task ReturnPreparationSupportsChainedDraftRoutesAndIntegratedReturns()
    {
        var yaml = ExplicitValidationMenuYaml.Replace("verified: true", "verified: false", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.PrepareMenuReturnStrategyTestSourceAsync(MenuReturnScriptKind.BelowMenuRoot, "picture");
            Assert.Equal(["KEY_MENU", "KEY_DOWN", "KEY_ENTER"], GetSentKeys(transport));
            Assert.Equal("picture", controller.GetMenuNavigationSnapshot().State.NodeId);
        }

        var integrated = yaml.Replace("id: open-picture-draft", "id: open-picture-draft\n    returnSteps:\n      - key: KEY_HOME", StringComparison.Ordinal);
        var (integratedController, integratedTransport) = await CreateConnectedControllerAsync(integrated, installedMenu: true);
        await using (integratedController)
        {
            await integratedController.PrepareMenuAuthoringValidationSourceAsync(MenuAuthoringItemKind.Transition, "open-picture-draft");
            Assert.Equal(["KEY_HOME", "KEY_MENU"], GetSentKeys(integratedTransport));
            Assert.Equal("settings", integratedController.GetMenuNavigationSnapshot().State.NodeId);
        }
    }

    [Fact]
    public async Task InactiveLayoutCanBeActivatedAndVerifiedWithoutEditingInstalledMenu()
    {
        var yaml = TopologyVerificationMenuYaml
            .Replace("model: Test TV", "model: Test TV\nconfigurations:\n  - id: standard\n    name: Standard\n  - id: game\n    name: Game", StringComparison.Ordinal)
            .Replace("label: Return to normal video", "label: Return to normal video\n    configuration: game", StringComparison.Ordinal)
            .Replace("id: open-settings", "id: open-settings\n    configuration: game", StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller).BuildServiceProvider();
            await using var renderer = new VerificationPageTestRenderer(services) { CheckId = "route:game:open-settings" };
            await renderer.StartAsync();
            await renderer.ClickAsync("Use game layout");
            Assert.Equal("game", controller.GetMenuNavigationSnapshot().ActiveConfigurationId);
            Assert.DoesNotContain("Add missing definition", renderer.LineText);
            for (var pass = 0; pass < 3; pass++)
            {
                await renderer.ClickAsync("Prepare start");
                await renderer.ClickAsync("Run test");
                await renderer.ClickAsync("Count pass");
            }

            await controller.ReloadMenuDefinitionAsync();
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().Checks.Single(check => check.Id == renderer.CheckId).Verified);
        }
    }

    private const string TopologyVerificationMenuYaml =
        """
        version: 1
        id: topology-verification-audit
        name: Topology Verification Audit
        model: Test TV
        timing:
          defaultDelay: 50ms
          screenChangeDelay: 50ms
          returnDelay: 50ms
          verified: false
        nodes:
          - id: normal-video
            label: Normal video
            children:
              - id: settings
                label: Settings
                children:
                  - id: picture
                    label: Picture
                    children:
                      - id: brightness
                        label: Brightness
                        controlType: action
                  - id: sound
                    label: Sound
                    controlType: action
        anchors:
          - id: normal
            label: Return to normal video
            target: normal-video
            validationSource: settings
            returnStrategy:
              menuRoot: settings
              atMenuRoot:
                steps:
                  - key: KEY_RETURN
              belowMenuRoot:
                steps:
                  - key: KEY_MENU
                  - key: KEY_RETURN
              overrides:
                - node: picture
                  steps:
                    - key: KEY_HOME
            steps:
              - key: KEY_EXIT
                repeat: 2
        transitions:
          - id: open-settings
            from: normal-video
            to: settings
            steps:
              - key: KEY_MENU
        """;
}
