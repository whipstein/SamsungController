using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Automation.Navigation;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Fact]
    public async Task OpenMenuPageRefreshesDefaultBasedSliderWhenSignalChanges()
    {
        var yaml = ConditionalDefaultsYaml.Replace("  - id: open-settings\n    from: normal-video\n    to: settings\n    verified: true\n    steps:\n      - key: KEY_MENU\n", "", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            foreach (var check in controller.GetMenuDefinitionVerificationSnapshot().Checks.ToArray())
            {
                await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            }
            Assert.True(controller.GetMenuDefinitionVerificationSnapshot().FullyVerified);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(new NoOpJavaScript()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            var page = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PictureControls>());
            async Task<string> DisplayedBrightnessAsync() => await renderer.Dispatcher.InvokeAsync(() =>
            {
                var input = System.Text.RegularExpressions.Regex.Match(page.ToHtmlString(), "<input[^>]*aria-label=\"Numeric value for Brightness\"[^>]*>").Value;
                Assert.NotEmpty(input);
                return System.Text.RegularExpressions.Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value;
            });
            Assert.Equal("25", await DisplayedBrightnessAsync());
            await renderer.Dispatcher.InvokeAsync(() => controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit"));
            Assert.Equal("40", await DisplayedBrightnessAsync());
            await renderer.Dispatcher.InvokeAsync(() => controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422"));
            Assert.Equal("35", await DisplayedBrightnessAsync());
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task SignalChangesRestoreConditionSpecificPredictionsAndExposeResolvedDefaults()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            var snapshot = controller.GetMenuNavigationSnapshot();
            Assert.Equal("40", snapshot.ControlValues["brightness"]);
            Assert.Equal("on", snapshot.ControlValues["master"]);
            Assert.Equal("High", snapshot.ControlValues["mode"]);
            var node = snapshot.Nodes.Single(node => node.Id == "brightness");
            Assert.Equal("25", node.DefaultValue);
            Assert.Equal("40", node.ResolvedDefaultValue);

            await controller.SaveCurrentMenuControlStateAsync("Entered values", [new("brightness", "40")]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            snapshot = controller.GetMenuNavigationSnapshot();
            Assert.Equal("25", snapshot.ControlValues["brightness"]);
            Assert.Equal("25", snapshot.Nodes.Single(node => node.Id == "brightness").ResolvedDefaultValue);
            Assert.Equal("off", snapshot.ControlValues["master"]);
            Assert.Equal("Low", snapshot.ControlValues["mode"]);
            await controller.ReloadMenuDefinitionAsync();
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("40", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ApplyVerificationAndResetUseTheActiveSignalDefaults()
    {
        var yaml = ConditionalDefaultsYaml.Replace("value: 35", "value: 35.0", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            await controller.ApplyMenuControlValuesAsync([new("brightness", "40", "41")], new Dictionary<string, string>(), false);
            Assert.Single(GetSentKeys(transport), key => key == "KEY_RIGHT");
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("41", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.ResetMenuControlsToFactoryDefaultsAsync("reset", "Reset");
            Assert.Equal("40", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            Assert.Equal("35", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);

            var test = await controller.RunMenuDefinitionVerificationTestAsync("control:slider-behavior");
            Assert.Equal("36", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal(1, await controller.RestoreMenuDefinitionVerificationTestAsync(test));
            await controller.ConfirmMenuDefinitionVerificationCheckAsync(test.CheckId);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        }
    }

    [Fact]
    public async Task InitialLoadUsesRememberedSignalBeforeSeedingDefaultValues()
    {
        var (controller, _) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal("40", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        Assert.Equal("High", reopened.GetMenuNavigationSnapshot().ControlValues["mode"]);
    }

    [Fact]
    public async Task NodeEditsAndOutlineEditsPreserveConditionalRulesUntilExplicitlyRemoved()
    {
        var (controller, _) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml);
        await using (controller)
        {
            await controller.UpdateMenuNodeAsync("brightness", new("brightness", "Brightness renamed", "settings", null,
                MenuControlType.Slider, "25", MinimumValue: 0, MaximumValue: 100));
            Assert.Equal(2, controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").DefaultValueWhen!.Count);
            await controller.ApplyMenuTopologyOutlineAsync(new("settings", "[brightness] Brightness renamed", KeepUnlistedNodes: true));
            Assert.Equal(2, controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").DefaultValueWhen!.Count);
            await controller.UpdateMenuNodeAsync("brightness", new("brightness", "Brightness renamed", "settings", null,
                MenuControlType.Slider, "25", MinimumValue: 0, MaximumValue: 100, DefaultValueWhen: []));
            Assert.Empty(controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").DefaultValueWhen!);
        }
    }

    private const string ConditionalDefaultsYaml = """
        version: 1
        id: conditional-defaults-test
        name: Conditional defaults test
        model: Test TV
        externalStates:
          - id: pgen-output-format
            label: Color format
            defaultValue: RGB
            options: [RGB, YCbCr422]
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
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 100
                    defaultValueWhen:
                      - when: { pgen-output-format: RGB, hdmi-bit-depth: 10-bit }
                        value: 40
                      - when: { hdmi-bit-depth: 10-bit }
                        value: 35
                  - id: master
                    label: Master
                    controlType: switch
                    defaultValue: off
                    defaultValueWhen:
                      - when: { hdmi-bit-depth: 10-bit }
                        value: on
                  - id: mode
                    label: Mode
                    controlType: selection
                    defaultValue: Low
                    options: [Low, High]
                    defaultValueWhen:
                      - when: { hdmi-bit-depth: 10-bit }
                        value: High
                  - id: reset
                    label: Reset
                    controlType: confirmation
                    defaultValue: Cancel
                    options: [Reset, Cancel]
                    defaultValueWhen:
                      - when: { hdmi-bit-depth: 10-bit }
                        value: Reset
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
          - id: open-brightness
            from: normal-video
            to: brightness
            verified: true
            steps:
              - key: KEY_MENU
          - id: open-reset
            from: normal-video
            to: reset
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
                repeat: 3
        """;
}
