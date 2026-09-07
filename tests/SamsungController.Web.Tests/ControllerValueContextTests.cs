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
    private static string ValueContextsYaml => ConditionalDefaultsYaml
        .Replace("label: Settings", "label: Settings\n        valueContext: [external:hdmi-bit-depth, setting:mode]", StringComparison.Ordinal)
        .Replace("label: Master", "label: Master\n            valueContext: []", StringComparison.Ordinal)
        .Replace("pgen-output-format: RGB, hdmi-bit-depth: 10-bit", "setting:mode: High, hdmi-bit-depth: 10-bit", StringComparison.Ordinal);

    private static MenuControlConditionValues ScopedValues(string depth, string mode, string brightness) =>
        new(new Dictionary<string, string> { ["external:hdmi-bit-depth"] = depth, ["setting:mode"] = mode }, [new("brightness", brightness)]);

    private static MenuControlTargetProfile ScopedCalibration(string eight = "21", string ten = "44") =>
        new(3, "All contexts", "conditional-defaults-test", "Test", "Test TV", null, DateTimeOffset.UtcNow, [],
        [
            ScopedValues("8-bit", "Low", eight),
            ScopedValues("8-bit", "High", "31"),
            ScopedValues("10-bit", "High", ten),
            new(new Dictionary<string, string>(), [new("master", "on")])
        ]);

    [Fact]
    public async Task NodeAndOutlineEditsPreserveScopesAndSupportExplicitSharedAndInheritance()
    {
        var (controller, _) = await CreateConnectedControllerAsync(ValueContextsYaml);
        await using (controller)
        {
            await controller.UpdateMenuNodeAsync("settings", new("settings", "Picture", "normal-video", null));
            Assert.Equal(2, controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "settings").ValueContext!.Count);
            await controller.ApplyMenuTopologyOutlineAsync(new("settings", "[master] Shared switch", KeepUnlistedNodes: true));
            Assert.Empty(controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "master").ValueContext!);
            await controller.UpdateMenuNodeAsync("brightness", new("brightness", "Brightness", "settings", null,
                MenuControlType.Slider, "25", MinimumValue: 0, MaximumValue: 100, ValueContext: []));
            Assert.Empty(controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").EffectiveValueContext!);
            await controller.UpdateMenuNodeAsync("brightness", new("brightness", "Brightness", "settings", null,
                MenuControlType.Slider, "25", MinimumValue: 0, MaximumValue: 100, InheritValueContext: true));
            Assert.Null(controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").ValueContext);
            Assert.Equal(2, controller.GetMenuNavigationSnapshot().Nodes.Single(node => node.Id == "brightness").EffectiveValueContext!.Count);
        }
    }

    [Fact]
    public async Task MenuDependentDefaultsAlsoRefreshBeforeOptingIntoScopedStorage()
    {
        var yaml = ConditionalDefaultsYaml.Replace("pgen-output-format: RGB, hdmi-bit-depth: 10-bit", "setting:mode: High, hdmi-bit-depth: 10-bit", StringComparison.Ordinal) + """

          - id: open-mode
            from: normal-video
            to: mode
            verified: true
            steps:
              - key: KEY_MENU
        """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("40", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyMenuControlValuesAsync(
                [new("mode", "High", "Low"), new("brightness", "40", "41")], new Dictionary<string, string>(), false));
            Assert.Empty(GetSentKeys(transport));
            await controller.ApplyMenuControlValuesAsync([new("mode", "High", "Low")], new Dictionary<string, string>(), false);
            Assert.Equal("35", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal("35", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
    }

    [Fact]
    public async Task ScopedCurrentsAndTargetsFollowDepthAndModeButNotColorFormatAndSurviveRestart()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ValueContextsYaml, installedMenu: true);
        await using (controller)
        {
            var checks = controller.GetMenuDefinitionVerificationSnapshot().Checks.Select(check => (check.Id, check.Verified)).ToArray();
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration(), true);
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration("22", "45"), false);
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Contains(controller.GetMenuControlProfileSnapshot().Values, value => value.NodeId == "brightness" && value.Value == "22");
            await controller.SetRecordedMenuContextAsync("mode", "High");
            Assert.Equal("31", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("44", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("on", controller.GetMenuNavigationSnapshot().ControlValues["master"]);
            await controller.SetRecordedMenuContextAsync("mode", "Low");
            Assert.Equal("35", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.DoesNotContain(controller.GetMenuControlProfileSnapshot().CurrentValues!, value => value.NodeId == "brightness");
            await controller.SetRecordedMenuContextAsync("mode", "High");
            var exported = controller.ExportCalibrationConditions("All contexts", true);
            Assert.Equal(3, exported.Version);
            Assert.Contains(exported.ConditionValues!, set => set.Conditions.Count == 0 && set.Values.Any(value => value.NodeId == "master"));
            Assert.Contains(exported.ConditionValues!, set => set.Conditions.Count == 1 && set.Values.Any(value => value.NodeId == "mode"));
            Assert.DoesNotContain(exported.ConditionValues!, set => set.Conditions.Keys.Any(key => key.Contains("pgen", StringComparison.Ordinal)));
            var roundtrip = MenuControlTargetProfileSerializer.Deserialize(MenuControlTargetProfileSerializer.Serialize(exported));
            await controller.ImportCalibrationConditionsAsync(roundtrip, true);
            Assert.Equal(checks, controller.GetMenuDefinitionVerificationSnapshot().Checks.Select(check => (check.Id, check.Verified)));
            Assert.Empty(GetSentKeys(transport));
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal("44", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        await reopened.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
        Assert.Equal("High", reopened.GetMenuNavigationSnapshot().ControlValues["mode"]);
        Assert.Equal("31", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        await reopened.SetRecordedMenuContextAsync("mode", "Low");
        Assert.Equal("21", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
    }

    [Fact]
    public async Task MixedContextBatchIsRejectedBeforeKeysAndSingleSelectorLoadsNewBaseline()
    {
        var yaml = ValueContextsYaml + """

          - id: open-mode
            from: normal-video
            to: mode
            verified: true
            steps:
              - key: KEY_MENU
        """;
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration(), true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyMenuControlValuesAsync(
                [new("mode", "Low", "High"), new("brightness", "21", "22")], new Dictionary<string, string>(), false));
            Assert.Contains("separately", error.Message);
            Assert.Empty(GetSentKeys(transport));
            await controller.ApplyMenuControlValuesAsync([new("mode", "Low", "High")], new Dictionary<string, string>(), false);
            Assert.Equal("31", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal(new[] { "KEY_MENU", "KEY_ENTER", "KEY_DOWN", "KEY_ENTER" }, GetSentKeys(transport));
            await controller.SetRecordedMenuContextAsync("mode", "Low");
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        }
    }

    [Fact]
    public async Task ManualValuesUseSelectedContextAndCannotSilentlyRelabelAMode()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ValueContextsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.SaveCurrentMenuControlStateAsync("Current", [new("brightness", "23"), new("master", "on")]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.SaveCurrentMenuControlStateAsync("Wrong context",
                [new("mode", "High"), new("brightness", "23")]));
            await controller.SetRecordedMenuContextAsync("mode", "High");
            await controller.SaveCurrentMenuControlStateAsync("Current", [new("brightness", "33"), new("master", "off")]);
            Assert.Equal("High", controller.GetMenuNavigationSnapshot().ControlValues["mode"]);
            Assert.Equal("33", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetRecordedMenuContextAsync("mode", "Low");
            Assert.Equal("23", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Equal("off", controller.GetMenuNavigationSnapshot().ControlValues["master"]);
            Assert.Single(controller.GetSavedMenuControlStates());
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("option")]
    [InlineData("bounds")]
    [InlineData("alias")]
    public async Task InvalidScopedImportIsAtomic(string kind)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ValueContextsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration(), true);
            var document = ScopedCalibration("29");
            var invalid = document.ConditionValues![1];
            var conditions = new Dictionary<string, string>(invalid.Conditions);
            if (kind == "missing") conditions.Remove("setting:mode");
            if (kind == "extra") conditions.Add("external:pgen-output-format", "RGB");
            if (kind == "option") conditions["setting:mode"] = "Unknown";
            if (kind == "alias") conditions.Add("hdmi-bit-depth", "8-bit");
            invalid = invalid with { Conditions = conditions, Values = [new("brightness", kind == "bounds" ? "101" : "31")] };
            document = document with { ConditionValues = [document.ConditionValues[0], invalid] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ImportCalibrationConditionsAsync(document, true));
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task CollapsingOldColorBanksRequiresReviewOnlyWhenValuesConflictOrContextIsMissing()
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml);
        await using (controller)
        {
            await controller.SaveCurrentMenuControlStateAsync("RGB", [new("brightness", "21"), new("mode", "Low")]);
            await controller.SaveMenuControlProfileAsync([new("brightness", "22")]);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            await controller.SaveCurrentMenuControlStateAsync("YCbCr", [new("brightness", "28"), new("mode", "Low")]);
            await controller.SaveMenuControlProfileAsync([new("brightness", "29")]);
            await File.WriteAllTextAsync(controller.GetMenuNavigationSnapshot().DefinitionPath!, ValueContextsYaml);
            await controller.ReloadMenuDefinitionAsync();
            var conflicts = controller.GetMenuControlProfileSnapshot().ContextConflicts!;
            Assert.Equal(2, conflicts.Count(conflict => conflict.Value.NodeId == "brightness"));
            Assert.DoesNotContain(controller.GetMenuControlProfileSnapshot().CurrentValues!, value => value.NodeId == "brightness");
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ApplyMenuControlValuesAsync(
                [new("brightness", "25", "26")], new Dictionary<string, string>(), false));
            await controller.ResolveMenuValueContextConflictAsync(new("brightness", "28"), false);
            await controller.ResolveMenuValueContextConflictAsync(new("brightness", "29"), true);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().ContextConflicts!);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "RGB");
            Assert.Equal("28", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Contains(controller.GetMenuControlProfileSnapshot().Values, value => value.NodeId == "brightness" && value.Value == "29");
            await controller.SaveMenuControlProfileAsync([]);
            Assert.Empty(controller.GetMenuControlProfileSnapshot().Values);
            Assert.DoesNotContain(controller.GetMenuControlProfileSnapshot().ContextConflicts!, conflict => conflict.IsTarget);
            // Returning to the old rules proves neither archived RGB nor YCbCr bank was overwritten.
            await File.WriteAllTextAsync(controller.GetMenuNavigationSnapshot().DefinitionPath!, ConditionalDefaultsYaml);
            await controller.ReloadMenuDefinitionAsync();
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldValuesAreReusedOnlyWhenTheRequiredModeIsKnown(bool includesMode)
    {
        var (controller, transport) = await CreateConnectedControllerAsync(ConditionalDefaultsYaml);
        await using (controller)
        {
            await controller.SaveCurrentMenuControlStateAsync("Old", includesMode
                ? [new("brightness", "21"), new("mode", "Low")] : [new("brightness", "21")]);
            await File.WriteAllTextAsync(controller.GetMenuNavigationSnapshot().DefinitionPath!, ValueContextsYaml);
            await controller.ReloadMenuDefinitionAsync();
            if (includesMode)
            {
                Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
                Assert.Empty(controller.GetMenuControlProfileSnapshot().ContextConflicts!);
            }
            else
            {
                Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
                Assert.Contains(controller.GetMenuControlProfileSnapshot().ContextConflicts!, conflict => conflict.Value.NodeId == "brightness");
            }
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task IndexedCellsFollowTheirOwnScopeAndMixedIndexedBatchIsRejected()
    {
        var yaml = ValueContextsYaml.Replace("          - id: master", """
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
            var document = ScopedCalibration() with
            {
                ConditionValues =
                [
                    ScopedValues("8-bit", "Low", "0") with { Values = [new("red", "2", "interval", "5%"), new("red", "3", "interval", "10%")] },
                    ScopedValues("8-bit", "High", "0") with { Values = [new("red", "7", "interval", "5%"), new("red", "8", "interval", "10%")] }
                ]
            };
            await controller.ImportCalibrationConditionsAsync(document, true);
            await controller.ImportCalibrationConditionsAsync(document, false);
            await controller.SetMenuExternalStateAsync("pgen-output-format", "YCbCr422");
            Assert.Equal(new[] { "2", "3" }, controller.GetMenuControlProfileSnapshot().CurrentValues!.Where(value => value.SelectorNodeId is not null).Select(value => value.Value));
            Assert.Throws<InvalidOperationException>(() => controller.ValidateMenuValueContextBatch([new("mode", "Low", "High")], [new("interval", "5%", "red", "2", "4")]));
            await controller.SetRecordedMenuContextAsync("mode", "High");
            Assert.Equal(new[] { "7", "8" }, controller.GetMenuControlProfileSnapshot().CurrentValues!.Where(value => value.SelectorNodeId is not null).Select(value => value.Value));
            var exported = controller.ExportCalibrationConditions("Grid", true);
            Assert.Equal(4, exported.ConditionValues!.Sum(set => set.Values.Count(value => value.SelectorNodeId is not null)));
            Assert.Empty(GetSentKeys(transport));
        }
    }

    [Fact]
    public async Task ScopedResetPersistsDefaultsAndDoesNotOverwriteOtherContexts()
    {
        var (controller, _) = await CreateConnectedControllerAsync(ValueContextsYaml, installedMenu: true);
        await using (controller)
        {
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration(), true);
            await controller.ResetMenuControlsToFactoryDefaultsAsync("reset", "Reset");
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "10-bit");
            Assert.Equal("44", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await controller.SetMenuExternalStateAsync("hdmi-bit-depth", "8-bit");
            Assert.Equal("25", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
        }
        await using var reopened = CreateController();
        await reopened.InitializeAsync();
        Assert.Equal("25", reopened.GetMenuNavigationSnapshot().ControlValues["brightness"]);
    }

    [Fact]
    public async Task ScopedMenuUiShowsActivePictureModeAndCommandFreeContextControls()
    {
        var yaml = ValueContextsYaml.Replace("  - id: open-settings\n    from: normal-video\n    to: settings\n    verified: true\n    steps:\n      - key: KEY_MENU\n", "", StringComparison.Ordinal);
        var (controller, transport) = await CreateConnectedControllerAsync(yaml, installedMenu: true);
        await using (controller)
        {
            foreach (var check in controller.GetMenuDefinitionVerificationSnapshot().Checks)
                await controller.ConfirmMenuDefinitionVerificationCheckAsync(check.Id);
            await controller.ImportCalibrationConditionsAsync(ScopedCalibration(), true);
            await using var services = new ServiceCollection().AddLogging().AddSingleton(controller)
                .AddSingleton<IJSRuntime>(new NoOpJavaScript()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
            var page = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<PictureControls>());
            await renderer.Dispatcher.InvokeAsync(() => controller.SetRecordedMenuContextAsync("mode", "High"));
            var html = await renderer.Dispatcher.InvokeAsync(page.ToHtmlString);
            Assert.Contains("Mode: High", html);
            Assert.Contains("saved value groups", html);
            await using var interactive = new CalibrationPageRenderer(services);
            await interactive.StartAsync();
            await interactive.ClickAsync("Enter current settings", contains: true);
            await interactive.ChangeAsync("Actual TV context: Mode", "Low");
            Assert.Equal("21", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            await interactive.ChangeAsync("Actual TV context: Mode", "High");
            Assert.Equal("31", controller.GetMenuNavigationSnapshot().ControlValues["brightness"]);
            Assert.Empty(GetSentKeys(transport));
        }
    }
}
