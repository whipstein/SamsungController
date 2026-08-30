using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionVerificationPlannerTests
{
    [Fact]
    public void SelectionEditReopensOnlyAffectedVerificationCheck()
    {
        var original = CreateDefinition(["Standard", "Movie"]);
        var originalPlan = MenuDefinitionVerificationPlanner.Create(original);
        var records = originalPlan.Checks.Select(check => new MenuVerificationRecord(
            check.Id,
            check.Fingerprint,
            DateTimeOffset.UtcNow)).ToArray();
        var verified = CreateDefinition(
            ["Standard", "Movie"],
            new MenuVerificationManifest(originalPlan.Display, records));
        var edited = CreateDefinition(
            ["Standard", "Movie", "Filmmaker Mode"],
            verified.Verification);
        var editedPlan = MenuDefinitionVerificationPlanner.Create(edited);

        Assert.True(MenuDefinitionVerificationPlanner.IsCurrent(
            edited,
            editedPlan.Checks.Single(check => check.Id == "display")));
        Assert.True(MenuDefinitionVerificationPlanner.IsCurrent(
            edited,
            editedPlan.Checks.Single(check => check.Id == "timing")));
        Assert.False(MenuDefinitionVerificationPlanner.IsCurrent(
            edited,
            editedPlan.Checks.Single(check => check.Id == "control:selection:picture-mode")));
    }

    [Fact]
    public void PlanCoversRoutesReturnScriptsAndEveryInteractiveControlKind()
    {
        var definition = new MenuDefinition(
            "complete",
            "Complete",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode("brightness", "Brightness", "settings", ControlType: MenuControlType.Slider, DefaultValue: "25", MinimumValue: 0, MaximumValue: 50),
                new MenuNode("mode", "Mode", "settings", ControlType: MenuControlType.Selection, DefaultValue: "Movie", SelectionOptions: ["Standard", "Movie"]),
                new MenuNode("enhancer", "Enhancer", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off"),
                new MenuNode("reset", "Reset", "settings", ControlType: MenuControlType.Confirmation, DefaultValue: "Cancel", SelectionOptions: ["Reset", "Cancel"]),
                new MenuNode("smart-calibration", "Smart Calibration", "settings", ControlType: MenuControlType.Action),
                new MenuNode("conditional", "Conditional", "settings", DisabledWhen: [new MenuNodeDisabledCondition("enhancer", "off")])
            ],
            [new MenuTransition("open-settings", "normal-video", "settings", [new MenuOperation("KEY_MENU")], true)],
            [new MenuAnchor(
                "normal-video",
                "Return to video",
                "normal-video",
                [new MenuOperation("KEY_RETURN")],
                true,
                ReturnStrategy: new MenuReturnStrategy(
                    "settings",
                    new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                    new MenuReturnScript([new MenuOperation("KEY_MENU"), new MenuOperation("KEY_RETURN")], true)))],
            new MenuTimingProfile(150, 800, 300, true));

        var checks = MenuDefinitionVerificationPlanner.Create(definition).Checks;
        var kinds = checks
            .Select(check => check.Kind)
            .ToHashSet();

        Assert.Contains(MenuVerificationCheckKind.Display, kinds);
        Assert.Contains(MenuVerificationCheckKind.Timing, kinds);
        Assert.Contains(MenuVerificationCheckKind.Anchor, kinds);
        Assert.Contains(MenuVerificationCheckKind.ReturnScript, kinds);
        Assert.Contains(MenuVerificationCheckKind.Route, kinds);
        Assert.Contains(MenuVerificationCheckKind.SliderBehavior, kinds);
        Assert.Contains(MenuVerificationCheckKind.Selection, kinds);
        Assert.Contains(MenuVerificationCheckKind.Switch, kinds);
        Assert.Contains(MenuVerificationCheckKind.Confirmation, kinds);
        Assert.Contains(MenuVerificationCheckKind.ConditionalVisibility, kinds);
        Assert.DoesNotContain(
            checks,
            check => check.Id.StartsWith("control:", StringComparison.Ordinal)
                && check.TargetNodeId == "smart-calibration");
    }

    [Fact]
    public void ReconcileInvalidatesVerifiedRouteWhoseRecordedFingerprintChanged()
    {
        var original = new MenuDefinition(
            "route-edit",
            "Route edit",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video")
            ],
            [new MenuTransition("open-settings", "normal-video", "settings", [new MenuOperation("KEY_MENU")], true)],
            []);
        var plan = MenuDefinitionVerificationPlanner.Create(original);
        var manifest = new MenuVerificationManifest(
            plan.Display,
            plan.Checks.Select(check => new MenuVerificationRecord(
                check.Id,
                check.Fingerprint,
                DateTimeOffset.UtcNow)).ToArray());
        var edited = new MenuDefinition(
            original.Id,
            original.Name,
            original.Model,
            original.Context,
            original.Nodes.Values,
            [new MenuTransition(
                "open-settings",
                "normal-video",
                "settings",
                [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_ENTER")],
                true)],
            [],
            verification: manifest);

        var reconciled = MenuDefinitionVerificationReconciler.Reconcile(edited);

        Assert.False(reconciled.Transitions["open-settings"].Verified);
        Assert.DoesNotContain(
            reconciled.Verification!.Checks,
            record => record.Id.StartsWith("route:", StringComparison.Ordinal));
        Assert.Contains(reconciled.Verification.Checks, record => record.Id == "display");
    }

    [Fact]
    public void ReconcileRemovesRouteManifestEvidenceWhenTheRouteNeedsValidation()
    {
        var draft = new MenuDefinition(
            "draft-route",
            "Draft route",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video")
            ],
            [new MenuTransition(
                "open-settings",
                "normal-video",
                "settings",
                [new MenuOperation("KEY_MENU")],
                false)],
            []);
        var plan = MenuDefinitionVerificationPlanner.Create(draft);
        var withStaleManifest = new MenuDefinition(
            draft.Id,
            draft.Name,
            draft.Model,
            draft.Context,
            draft.Nodes.Values,
            draft.Transitions.Values,
            draft.Anchors.Values,
            verification: new MenuVerificationManifest(
                plan.Display,
                plan.Checks.Select(check => new MenuVerificationRecord(
                    check.Id,
                    check.Fingerprint,
                    DateTimeOffset.UtcNow)).ToArray()));

        var reconciled = MenuDefinitionVerificationReconciler.Reconcile(withStaleManifest);

        Assert.DoesNotContain(
            reconciled.Verification!.Checks,
            record => record.Id.StartsWith("route:", StringComparison.Ordinal));
        Assert.Contains(reconciled.Verification.Checks, record => record.Id == "display");
    }

    private static MenuDefinition CreateDefinition(
        IReadOnlyList<string> options,
        MenuVerificationManifest? verification = null) => new(
            "planner",
            "Planner",
            "S95F",
            new MenuDefinitionContext("1296", "SDR", "Movie", "HDMI 1"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode(
                    "picture-mode",
                    "Picture Mode",
                    "normal-video",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Movie",
                    SelectionOptions: options)
            ],
            [],
            [],
            verification: verification);
}
