using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Automation.Navigation;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Theory]
    [InlineData("RGB", "8-bit")]
    [InlineData("RGB", "10-bit")]
    [InlineData("YCbCr422", "8-bit")]
    [InlineData("YCbCr422", "10-bit")]
    [InlineData("YCbCr444", "8-bit")]
    [InlineData("YCbCr444", "10-bit")]
    public async Task BitDepthAndColorFormatIndependentlySelectGammaVariant(string format, string depth)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(HdmiBitDepthMenuYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ConfirmMenuDefinitionVerificationCheckAsync("display");
            var checks = controller.GetMenuDefinitionVerificationSnapshot().Checks.Select(check => (check.Id, check.VerifiedAtUtc)).ToArray();
            await controller.SetMenuExternalStateAsync("pgen-output-format", format);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", depth);
            Assert.Empty(GetSentKeys(transport));
            Assert.Equal(format, controller.GetMenuNavigationSnapshot().ExternalStates!.Single(state => state.Id == "pgen-output-format").Value);

            var visibleGamma = depth == "8-bit" ? "gamma-8bit" : "gamma-10bit";
            var hiddenGamma = depth == "8-bit" ? "gamma-10bit" : "gamma-8bit";
            Assert.Equal(visibleGamma, controller.CreateNavigationPlan(visibleGamma).TargetNodeId);
            Assert.Contains("hidden", Assert.Throws<InvalidOperationException>(() => controller.CreateNavigationPlan(hiddenGamma)).Message);
            if (format == "RGB")
            {
                Assert.Equal("black-level", controller.CreateNavigationPlan("black-level").TargetNodeId);
            }
            else
            {
                Assert.Contains("disabled", Assert.Throws<InvalidOperationException>(() => controller.CreateNavigationPlan("black-level")).Message);
            }

            // ST.2084 is fixed at 10-bit; exercise another selection instead.
            var testTarget = depth == "8-bit" ? visibleGamma : "picture-mode";
            Assert.Equal(["ST.2084"], controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "gamma-10bit").SelectionOptions);
            var priorValue = controller.GetMenuNavigationSnapshot().ControlValues[testTarget];
            var test = await controller.RunMenuDefinitionVerificationTestAsync("control:selection-behavior");
            Assert.Equal(testTarget, test.TargetNodeId);
            Assert.NotEqual(priorValue, controller.GetMenuNavigationSnapshot().ControlValues[testTarget]);
            Assert.Equal(1, await controller.RestoreMenuDefinitionVerificationTestAsync(test));
            Assert.Equal(priorValue, controller.GetMenuNavigationSnapshot().ControlValues[testTarget]);
            Assert.Equal("ST.2084", controller.GetMenuNavigationSnapshot().ControlValues["gamma-10bit"]);
            var sliderId = depth == "8-bit" ? "bt.1886" : "st.2084";
            var sliderTest = await controller.RunMenuDefinitionVerificationTestAsync("control:slider-behavior");
            Assert.Equal(sliderId, sliderTest.TargetNodeId);
            Assert.Equal("1", controller.GetMenuNavigationSnapshot().ControlValues[sliderId]);
            Assert.Equal(1, await controller.RestoreMenuDefinitionVerificationTestAsync(sliderTest));
            Assert.Equal("0", controller.GetMenuNavigationSnapshot().ControlValues[sliderId]);
            await controller.ReloadMenuDefinitionAsync();
            var externalStates = controller.GetMenuNavigationSnapshot().ExternalStates!;
            Assert.Equal(format, externalStates.Single(state => state.Id == "pgen-output-format").Value);
            Assert.Equal(depth, externalStates.Single(state => state.Id == "hdmi-bit-depth").Value);
            Assert.Equal(checks, controller.GetMenuDefinitionVerificationSnapshot().Checks.Select(check => (check.Id, check.VerifiedAtUtc)));
        }
    }

    [Theory]
    [InlineData(MenuDefinitionFileFormat.Yaml)]
    [InlineData(MenuDefinitionFileFormat.Json)]
    public async Task NewMenusIncludeIndependentHdmiSignalSelectors(MenuDefinitionFileFormat format)
    {
        Directory.CreateDirectory(_directory);
        await using var controller = CreateController();
        await controller.InitializeAsync();
        await controller.CreateMenuDefinitionAsync(new MenuDefinitionCreationRequest("", "", "Test TV", "1234", Format: format));
        var definition = await new MenuDefinitionParser().ParseFileAsync(controller.GetMenuNavigationSnapshot().DefinitionPath);
        Assert.Equal(["RGB", "YCbCr422", "YCbCr444"], definition.ExternalStates["pgen-output-format"].Options);
        Assert.Equal(["8-bit", "10-bit"], definition.ExternalStates["hdmi-bit-depth"].Options);
        Assert.Equal("8-bit", definition.ExternalStates["hdmi-bit-depth"].DefaultValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GammaVariantsKeepTheirCursorSlotsAfterYamlOrJsonRoundTrip(bool json)
    {
        var original = new MenuDefinitionParser().Parse(HdmiBitDepthMenuYaml);
        var definition = json
            ? new MenuDefinitionJsonSerializer().Parse(new MenuDefinitionJsonSerializer().Serialize(original))
            : new MenuDefinitionParser().Parse(new MenuDefinitionWriter().Serialize(original));
        foreach (var format in new[] { "RGB", "YCbCr422", "YCbCr444" })
            foreach (var depth in new[] { "8-bit", "10-bit" })
            {
                var effective = new MenuDefinition(
                    definition.Id, definition.Name, definition.Model, definition.Context,
                    definition.Nodes.Values,
                    definition.Transitions.Values.Where(route => route.Id == "open-settings"),
                    definition.Anchors.Values,
                    externalStates: definition.ExternalStates.Values.Select(state => state with
                    {
                        DefaultValue = state.Id == "hdmi-bit-depth" ? depth : format
                    }));
                var generated = TopologyRouteGenerator.Regenerate(effective);
                var gammaId = depth == "8-bit" ? "gamma-8bit" : "gamma-10bit";
                var sliderId = depth == "8-bit" ? "bt.1886" : "st.2084";
                var gammaRoute = Assert.Single(generated.Transitions.Values, route => route.ToNodeId == gammaId);
                var sliderRoute = Assert.Single(generated.Transitions.Values, route => route.ToNodeId == sliderId);
                Assert.Equal(["KEY_MENU"], gammaRoute.Operations.Select(operation => operation.Key));
                Assert.Equal(["KEY_MENU", "KEY_DOWN"], sliderRoute.Operations.Select(operation => operation.Key));
                Assert.Equal(1, sliderRoute.Operations.Last().Repeat);
                Assert.DoesNotContain(generated.Transitions.Values, route => route.ToNodeId == (depth == "8-bit" ? "gamma-10bit" : "gamma-8bit"));
                Assert.DoesNotContain(generated.Transitions.Values, route => route.ToNodeId == (depth == "8-bit" ? "st.2084" : "bt.1886"));
                var slider = definition.Nodes[sliderId];
                Assert.Equal(-3, slider.MinimumValue);
                Assert.Equal(3, slider.MaximumValue);
                Assert.Equal("0", slider.DefaultValue);
            }
    }

    [Theory]
    [InlineData("Color format")]
    [InlineData("PGen output format")]
    public async Task HeaderClearlyLabelsBothIndependentSignalSelectors(string formatLabel)
    {
        var yaml = HdmiBitDepthMenuYaml.Replace("label: Color format", $"label: {formatLabel}", StringComparison.Ordinal);
        var (controller, _) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(new NoOpJavaScript())
                .AddSingleton<NavigationManager>(new BitDepthNavigationManager()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<MenuKeyLayout>()).ToHtmlString());
            Assert.Contains("<span>Color format</span>", html);
            Assert.Contains("<span>Bit depth</span>", html);
            Assert.Contains("aria-label=\"External HDMI Signal: Color format\"", html);
            Assert.Contains("aria-label=\"External HDMI Signal: Bit depth\"", html);
            Assert.Contains("value=\"8-bit\"", html);
            Assert.Contains("value=\"10-bit\"", html);
        }
    }

    private sealed class BitDepthNavigationManager : NavigationManager
    {
        public BitDepthNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private const string HdmiBitDepthMenuYaml =
        """
        version: 1
        id: hdmi-bit-depth-test
        name: Independent HDMI signal test
        model: Test TV
        externalStates:
          - id: pgen-output-format
            label: Color format
            defaultValue: RGB
            options: [RGB, YCbCr422, YCbCr444]
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
                  - id: gamma-8bit
                    label: Gamma
                    controlType: selection
                    defaultValue: BT.1886
                    options: [BT.1886, "2.2"]
                    hiddenWhen:
                      - externalState: hdmi-bit-depth
                        equals: 10-bit
                  - id: gamma-10bit
                    label: Gamma
                    controlType: selection
                    defaultValue: ST.2084
                    options: [ST.2084]
                    hiddenWhen:
                      - externalState: hdmi-bit-depth
                        equals: 8-bit
                  - id: bt.1886
                    label: BT.1886
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -3
                    maximumValue: 3
                    hiddenWhen:
                      - externalState: hdmi-bit-depth
                        equals: 10-bit
                      - setting: gamma-8bit
                        equals: "2.2"
                  - id: st.2084
                    label: ST.2084
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -3
                    maximumValue: 3
                    hiddenWhen:
                      - externalState: hdmi-bit-depth
                        equals: 8-bit
                  - id: picture-mode
                    label: Picture mode
                    controlType: selection
                    defaultValue: Standard
                    options: [Standard, Movie]
                    hiddenWhen:
                      - externalState: hdmi-bit-depth
                        equals: 8-bit
                  - id: black-level
                    label: Black level
                    controlType: action
                    disabledWhen:
                      - externalState: pgen-output-format
                        equals: YCbCr422
                      - externalState: pgen-output-format
                        equals: YCbCr444
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
          - id: open-gamma-8bit
            from: normal-video
            to: gamma-8bit
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_ENTER
          - id: open-gamma-10bit
            from: normal-video
            to: gamma-10bit
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_ENTER
          - id: open-black-level
            from: normal-video
            to: black-level
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
          - id: open-picture-mode
            from: normal-video
            to: picture-mode
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
          - id: open-bt1886
            from: normal-video
            to: bt.1886
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
          - id: open-st2084
            from: normal-video
            to: st.2084
            verified: true
            steps:
              - key: KEY_MENU
              - key: KEY_DOWN
        """;
}
