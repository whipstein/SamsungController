using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed partial class ControllerMenuIntegrationTests
{
    [Fact]
    public async Task OneFileLoadsAllConditionsAndRestoresThemAfterRestartWithoutCommands()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            var verification = controller.GetMenuDefinitionVerificationSnapshot();
            await controller.ImportCalibrationConditionsAsync(CombinedCalibration("21", "44"), asCurrent: true);
            await controller.ImportCalibrationConditionsAsync(CombinedCalibration("22", "45"), asCurrent: false);
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("22", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("44", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("45", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            Assert.Equal("35", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().Values);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().CurrentValues!);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            Assert.Equal("44", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal(verification.Checks.Select(check => (check.Id, check.Verified)),
                controller.GetMenuDefinitionVerificationSnapshot().Checks.Select(check => (check.Id, check.Verified)));
            var exported = controller.ExportCalibrationConditions("All currents", currentValues: true);
            Assert.Equal(2, exported.ConditionValues!.Count);
            Assert.Equal(new[] { "21", "44" }, exported.ConditionValues.Select(set => Assert.Single(set.Values).Value).Order().ToArray());
            Assert.Empty(GetSentKeys(transport));
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal("44", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        Assert.Equal("45", Assert.Single(reopened.GetMenuControlProfileSnapshot().Values).Value);
        await reopened.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
        Assert.Equal("21", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        Assert.Equal("22", Assert.Single(reopened.GetMenuControlProfileSnapshot().Values).Value);
    }

    [Fact]
    public async Task InvalidLaterCombinationRejectsWholeImportAndKeepsExistingValues()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ImportCalibrationConditionsAsync(CombinedCalibration("21", "44"), asCurrent: true);
            await controller.ImportCalibrationConditionsAsync(CombinedCalibration("22", "45"), asCurrent: false);
            foreach (var asCurrent in new[] { true, false })
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    controller.ImportCalibrationConditionsAsync(CombinedCalibration("30", "101"), asCurrent));
                Assert.Contains("Combination 2", error.Message);
                Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
                Assert.Equal("22", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            }
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData("missing", "specify all input conditions")]
    [InlineData("unknown", "not defined")]
    [InlineData("option", "not available")]
    [InlineData("definition", "belongs to menu definition")]
    public async Task CombinedFilesRejectAmbiguousOrUnsupportedConditions(string errorKind, string expected)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            var document = CombinedCalibration("21", "44");
            var conditions = new Dictionary<string, string>(document.ConditionValues![1].Conditions);
            if (errorKind == "missing") conditions.Remove("hdmi-bit-depth");
            if (errorKind == "unknown") conditions.Add("hdmi-input", "HDMI 1");
            if (errorKind == "option") conditions["hdmi-bit-depth"] = "12-bit";
            if (errorKind == "definition") document = document with { DefinitionId = "another-menu" };
            document = document with { ConditionValues = [document.ConditionValues[0], document.ConditionValues[1] with { Conditions = conditions }] };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ImportCalibrationConditionsAsync(document, true));
            Assert.Contains(expected, error.Message);
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().CurrentValues!);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ManualStatesAndLegacyFilesAreBoundToTheSelectedCombination()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml, installedMenu: true);
        await using (controller)
        {
            var eightBit = await controller.SaveCurrentMenuControlStateAsync("Observed", [new("brightness", "20")]);
            await controller.SaveMenuControlProfileAsync([new("brightness", "23")]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Empty(controller.GetSavedMenuControlStates());
            await Assert.ThrowsAsync<KeyNotFoundException>(() => controller.LoadMenuControlStateAsync(eightBit.Id));
            var tenBit = await controller.SaveCurrentMenuControlStateAsync("Observed", [new("brightness", "43")]);
            Assert.NotEqual(eightBit.Id, tenBit.Id);
            var legacy = CombinedCalibration("0", "0") with { Version = 1, ConditionValues = null, Values = [new("brightness", "46")] };
            await controller.ImportCalibrationConditionsAsync(legacy, asCurrent: false);
            Assert.Equal("43", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("46", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            Assert.Equal("20", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("23", Assert.Single(controller.GetMenuControlProfileSnapshot().Values).Value);
            Assert.Equal(eightBit.Id, Assert.Single(controller.GetSavedMenuControlStates()).Id);
            var exported = controller.ExportCalibrationConditions("All targets", currentValues: false);
            Assert.Equal(2, exported.ConditionValues!.Count);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task IndexedCurrentValuesAndTargetsSurviveSignalChangesAndRestart()
    {
        var yaml = ConditionalDefaultsYaml.Replace("          - id: master", """
                  - id: interval
                    label: Interval
                    controlType: indexed-selection
                    defaultValue: 5%
                    options: [5%, 10%]
                  - id: red
                    label: Red
                    controlType: slider
                    defaultValue: 0
                    minimumValue: -50
                    maximumValue: 50
                  - id: master
        """, StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            var baseDocument = CombinedCalibration("21", "44");
            var document = baseDocument with
            {
                ConditionValues = baseDocument.ConditionValues!.Select((set, index) => set with
                {
                    Values = [new("red", index == 0 ? "2" : "7", "interval", "5%"), new("red", index == 0 ? "3" : "8", "interval", "10%")]
                }).ToArray()
            };
            await controller.ImportCalibrationConditionsAsync(document, true);
            await controller.ImportCalibrationConditionsAsync(document, false);
            Assert.Equal(new[] { "2", "3" }, controller.GetMenuControlProfileSnapshot().CurrentValues!.Select(value => value.Value));
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal(new[] { "7", "8" }, controller.GetMenuControlProfileSnapshot().CurrentValues!.Select(value => value.Value));
            Assert.Equal(new[] { "7", "8" }, controller.GetMenuControlProfileSnapshot().Values.Select(value => value.Value));
            Assert.Equal(4, controller.ExportCalibrationConditions("Indexed", true).ConditionValues!.Sum(set => set.Values.Count));
            Assert.Empty(GetSentKeys(transport));
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal(new[] { "7", "8" }, reopened.GetMenuControlProfileSnapshot().CurrentValues!.Select(value => value.Value));
    }

    [Fact]
    public async Task OpenMenuPageShowsMatchingImportedTargetsAndBaselineWarning()
    {
        var yaml = ConditionalDefaultsYaml.Replace("  - id: open-settings\n    from: normal-video\n    to: settings\n    verified: true\n    steps:\n      - key: KEY_MENU\n", "", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            foreach (var check in controller.GetMenuDefinitionVerificationSnapshot().Checks.ToArray())
                await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            await controller.SaveMenuControlBehaviorPreferencesAsync(true, false);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(new NoOpJavaScript()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            var page = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PictureControls>());
            await renderer.Dispatcher.InvokeAsync(() => controller.ImportCalibrationConditionsAsync(CombinedCalibration("21", "44"), true));
            await renderer.Dispatcher.InvokeAsync(() => controller.ImportCalibrationConditionsAsync(CombinedCalibration("22", "45"), false));
            async Task AssertPageAsync(string target, string text) => await renderer.Dispatcher.InvokeAsync(() =>
            {
                var html = page.ToHtmlString();
                var input = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*aria-label=\"Numeric value for Brightness\"[^>]*>").Value;
                Assert.Contains($"value=\"{target}\"", input);
                Assert.Contains(text, html);
                Assert.Contains("Download all targets", html);
            });
            await AssertPageAsync("22", "8-bit");
            await renderer.Dispatcher.InvokeAsync(() => controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit"));
            await AssertPageAsync("45", "10-bit");
            await renderer.Dispatcher.InvokeAsync(() => controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422"));
            await AssertPageAsync("35", "No recorded current values");
            Assert.Empty(GetSentKeys(transport));
        }
    }

    private static MenuControlTargetProfile CombinedCalibration(string eightBit, string tenBit) => new(
        2, "All conditions", "conditional-defaults-test", "Test", "Test TV", null, DateTimeOffset.UtcNow, [],
        [
            new(new Dictionary<string, string> { ["pgen-output-format"] = "RGB", ["hdmi-bit-depth"] = "8-bit" }, [new("brightness", eightBit)]),
            new(new Dictionary<string, string> { ["pgen-output-format"] = "RGB", ["hdmi-bit-depth"] = "10-bit" }, [new("brightness", tenBit)])
        ]);
}
