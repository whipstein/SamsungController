using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionVerificationPlannerTests
{
    [Fact]
    public void VerificationIdentityUsesModelAndFirmwareAndRejectsChangedFirmware()
    {
        var original = new MenuDefinition("firmware-test", "Firmware Test", "S95F",
            new MenuDefinitionContext("1296"), [new MenuNode("normal-video", "Normal video")], [], []);
        var plan = MenuDefinitionVerificationPlanner.Create(original);
        Assert.Equal(new MenuVerificationDisplay("S95F", "1296"), plan.Display);
        Assert.Equal("S95F · firmware 1296", MenuDefinitionVerificationPlanner.FormatDisplay(plan.Display));
        var evidence = new MenuVerificationManifest(plan.Display, plan.Checks.Select(check =>
            new MenuVerificationRecord(check.Id, check.Fingerprint, DateTimeOffset.UtcNow)).ToArray());
        var updated = new MenuDefinition(original.Id, original.Name, original.Model,
            new MenuDefinitionContext("1300"), original.Nodes.Values, [], [], verification: evidence);

        var changedPlan = MenuDefinitionVerificationPlanner.Create(updated);

        Assert.Equal("1300", changedPlan.Display.Firmware);
        Assert.All(changedPlan.Checks, check => Assert.False(MenuDefinitionVerificationPlanner.IsCurrent(updated, check)));
    }

    [Fact]
    public void ExternalHiddenLocationCheckTracksNeighborOrderWithoutResettingUnrelatedChecks()
    {
        var nodes = new MenuNode[]
        {
            new("normal-video", "Normal video"),
            new("settings", "Settings", "normal-video"),
            new("missing", "Missing", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off",
                HiddenWhen: [new MenuNodeHiddenCondition("depth", "10-bit", MenuConditionSourceKind.ExternalState)]),
            new("next", "Next", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off"),
            new("last", "Last", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off")
        };
        MenuDefinition Make(IEnumerable<MenuNode> ordered) => new("hidden-position", "Hidden position", "Test TV", new("1"),
            ordered, [], [], externalStates: [new("depth", "Bit depth", "8-bit", ["8-bit", "10-bit"])]);
        var original = MenuDefinitionVerificationPlanner.Create(Make(nodes));
        var reordered = MenuDefinitionVerificationPlanner.Create(Make([nodes[0], nodes[1], nodes[2], nodes[4], nodes[3]]));
        var originalCheck = original.Checks.Single(check => check.Id == "condition:external-hidden-behavior");
        Assert.Contains("next visible control", originalCheck.Description);
        Assert.Contains("previous visible control", originalCheck.Description);
        Assert.NotEqual(originalCheck.Fingerprint, reordered.Checks.Single(check => check.Id == originalCheck.Id).Fingerprint);
        foreach (var unchanged in original.Checks.Where(check => check.Id != originalCheck.Id))
            Assert.Equal(unchanged.Fingerprint, reordered.Checks.Single(check => check.Id == unchanged.Id).Fingerprint);
    }

    [Fact]
    public void ExternalStateRulesUseOneDedicatedGuidedCheck()
    {
        var definition = new MenuDefinition(
            "external-condition",
            "External condition",
            "S95F",
            new MenuDefinitionContext("1296"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode(
                    "hdmi-black-level",
                    "HDMI Black Level",
                    "settings",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Auto",
                    DisabledWhen:
                    [
                        new MenuNodeDisabledCondition(
                            "pgen-output-format",
                            "YCbCr422",
                            MenuConditionSourceKind.ExternalState),
                        new MenuNodeDisabledCondition(
                            "pgen-output-format",
                            "YCbCr444",
                            MenuConditionSourceKind.ExternalState)
                    ],
                    SelectionOptions: ["Auto", "Low", "Normal"])
            ],
            [],
            [],
            externalStates:
            [
                new MenuExternalState(
                    "pgen-output-format",
                    "PGen output format",
                    "RGB",
                    ["RGB", "YCbCr422", "YCbCr444"])
            ]);

        var check = Assert.Single(
            MenuDefinitionVerificationPlanner.Create(definition).Checks,
            candidate => candidate.Id == "condition:external-disabled-behavior");

        Assert.Equal(MenuVerificationCheckKind.ConditionalVisibility, check.Kind);
        Assert.Equal("pgen-output-format", check.ExternalStateId);
        Assert.Contains(check.ExternalStateValue, new[] { "YCbCr422", "YCbCr444" });
        Assert.Equal("hdmi-black-level", check.TargetNodeId);
        Assert.Equal("hdmi-black-level", check.RepresentativeNodeId);
        Assert.Contains("without pressing Enter", check.Description);
        Assert.Contains("app's PGen output format selector", check.Description);
    }

    [Fact]
    public void SelectionEditReopensOnlySharedSelectionVerificationCheck()
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
            editedPlan.Checks.Single(check => check.Id == "control:selection-behavior")));
    }

    [Fact]
    public void PlanCoversRoutesReturnScriptsAndEveryInteractiveControlKind()
    {
        var definition = new MenuDefinition(
            "complete",
            "Complete",
            "S95F",
            new MenuDefinitionContext("1296"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode("brightness", "Brightness", "settings", ControlType: MenuControlType.Slider, DefaultValue: "25", MinimumValue: 0, MaximumValue: 50),
                new MenuNode("mode", "Mode", "settings", ControlType: MenuControlType.Selection, DefaultValue: "Movie", SelectionOptions: ["Standard", "Movie"]),
                new MenuNode("interval", "Interval", "settings", ControlType: MenuControlType.Selection, DefaultValue: "5%", SelectionOptions: ["5%", "10%"]),
                new MenuNode("enhancer", "Enhancer", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off"),
                new MenuNode("apply-changes", "Apply Changes", "settings", ControlType: MenuControlType.Confirmation, DefaultValue: "Cancel", SelectionOptions: ["Apply", "Cancel"]),
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
        Assert.Contains(
            checks,
            check => check.Id == "control:indexed-selection-behavior"
                && check.TargetNodeId == "interval");
        Assert.DoesNotContain(
            checks,
            check => check.Id.StartsWith("control:", StringComparison.Ordinal)
                && check.TargetNodeId == "smart-calibration");
    }

    [Fact]
    public void EquivalentControlsAndConditionsCollapseIntoRepresentativeChecks()
    {
        var nodes = new List<MenuNode>
        {
            new("normal-video", "Normal video"),
            new("settings", "Settings", "normal-video"),
            new("mode", "Mode", "settings", ControlType: MenuControlType.Selection, DefaultValue: "Movie", SelectionOptions: ["Standard", "Movie"]),
            new("enhancer", "Enhancer", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off")
        };
        nodes.AddRange(Enumerable.Range(1, 100).Select(index => new MenuNode(
            $"selection-{index}",
            $"Selection {index}",
            "settings",
            ControlType: MenuControlType.Selection,
            DefaultValue: "A",
            SelectionOptions: ["A", "B"])));
        nodes.AddRange(Enumerable.Range(1, 40).Select(index => new MenuNode(
            $"conditional-{index}",
            $"Conditional {index}",
            "settings",
            ControlType: MenuControlType.Slider,
            DefaultValue: "0",
            DisabledWhen:
            [
                index <= 20
                    ? new MenuNodeDisabledCondition("enhancer", "off")
                    : new MenuNodeDisabledCondition("mode", "Movie")
            ],
            MinimumValue: -10,
            MaximumValue: 10)));
        var definition = new MenuDefinition(
            "representative",
            "Representative",
            "S95F",
            new MenuDefinitionContext("1296"),
            nodes,
            [],
            []);

        var checks = MenuDefinitionVerificationPlanner.Create(definition).Checks;

        Assert.Equal(6, checks.Count);
        var selection = Assert.Single(
            checks,
            check => check.Kind == MenuVerificationCheckKind.Selection);
        Assert.Equal("control:selection-behavior", selection.Id);
        Assert.Contains("101 selection controls", selection.Description);
        var condition = Assert.Single(
            checks,
            check => check.Kind == MenuVerificationCheckKind.ConditionalVisibility);
        Assert.Contains("40 related rows", condition.Description);
        Assert.Contains("2 declared conditional rules", condition.Description);
        Assert.Equal("enhancer", condition.TargetNodeId);
    }

    [Fact]
    public void PlanIncludesOneCalculatedCrossBranchNavigationCheck()
    {
        var definition = CreateCrossBranchDefinition();

        var checks = MenuDefinitionVerificationPlanner.Create(definition).Checks;

        var check = Assert.Single(
            checks,
            candidate => candidate.Kind == MenuVerificationCheckKind.CalculatedNavigation);
        Assert.Equal("navigation:default:calculated-backtracking", check.Id);
        Assert.NotNull(check.SourceNodeId);
        Assert.NotNull(check.TargetNodeId);
        Assert.NotEqual(check.SourceNodeId, check.TargetNodeId);
        Assert.Equal("normal", check.PreparationAnchorId);
        Assert.Contains("without returning to normal video", check.Description);
        Assert.Contains("row highlighted; do not open or change it", check.Description);
    }

    [Fact]
    public void VisualPositionDescriptionDistinguishesOpenSubmenuFromHighlightedRow()
    {
        var definition = CreateCrossBranchDefinition();

        var submenu = MenuDefinitionVerificationPlanner.DescribeVisualPosition(
            definition,
            "white-balance");
        var row = MenuDefinitionVerificationPlanner.DescribeVisualPosition(
            definition,
            "two-point-red");

        Assert.Contains("submenu open", submenu);
        Assert.Contains("child list visible", submenu);
        Assert.Contains("not the White Balance row highlighted", submenu);
        Assert.Contains("row highlighted", row);
        Assert.Contains("do not open or change it", row);
    }

    [Fact]
    public void AdjustmentTimingChangeReopensTimingWithoutReopeningUnrelatedCrossBranchRoute()
    {
        var original = CreateCrossBranchDefinition();
        var edited = new MenuDefinition(
            original.Id,
            original.Name,
            original.Model,
            original.Context,
            original.Nodes.Values,
            original.Transitions.Values,
            original.Anchors.Values,
            original.Timing with { AdjustmentDelayMilliseconds = 60 });

        var originalChecks = MenuDefinitionVerificationPlanner.Create(original).Checks;
        var editedChecks = MenuDefinitionVerificationPlanner.Create(edited).Checks;

        Assert.NotEqual(
            originalChecks.Single(check => check.Id == "timing").Fingerprint,
            editedChecks.Single(check => check.Id == "timing").Fingerprint);
        Assert.Equal(
            originalChecks.Single(check =>
                check.Kind == MenuVerificationCheckKind.CalculatedNavigation).Fingerprint,
            editedChecks.Single(check =>
                check.Kind == MenuVerificationCheckKind.CalculatedNavigation).Fingerprint);
    }

    [Fact]
    public void PermanentlyDisabledBranchUsesOneBehaviorCheckAndNoControlChecks()
    {
        var definition = new MenuDefinition(
            "permanently-disabled",
            "Permanently disabled",
            "S95F",
            new MenuDefinitionContext("1296"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video"),
                new MenuNode("unavailable", "Unavailable", "settings", Disabled: true),
                new MenuNode(
                    "unavailable-slider",
                    "Unavailable slider",
                    "unavailable",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "5",
                    MinimumValue: 0,
                    MaximumValue: 10),
                new MenuNode(
                    "unavailable-mode",
                    "Unavailable mode",
                    "unavailable",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Off",
                    SelectionOptions: ["Off", "On"])
            ],
            [],
            []);

        var checks = MenuDefinitionVerificationPlanner.Create(definition).Checks;

        var disabled = Assert.Single(
            checks,
            check => check.Id == "condition:always-disabled-behavior");
        Assert.Equal(MenuVerificationCheckKind.ConditionalVisibility, disabled.Kind);
        Assert.Equal("settings", disabled.TargetNodeId);
        Assert.Contains("permanently gray", disabled.Description);
        Assert.DoesNotContain(checks, check => check.Kind == MenuVerificationCheckKind.SliderBehavior);
        Assert.DoesNotContain(
            checks,
            check => check.Id == "control:selection-behavior");
    }

    [Fact]
    public void ReconcileInvalidatesVerifiedRouteWhoseRecordedFingerprintChanged()
    {
        var original = new MenuDefinition(
            "route-edit",
            "Route edit",
            "S95F",
            new MenuDefinitionContext("1296"),
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

    private static MenuDefinition CreateCrossBranchDefinition() => new(
        "cross-branch-verification",
        "Cross branch verification",
        "S95F",
        new MenuDefinitionContext("1296"),
        [
            new MenuNode("normal-video", "Normal video"),
            new MenuNode("white-balance", "White Balance", "normal-video"),
            new MenuNode("two-point", "2 Point", "white-balance"),
            new MenuNode(
                "two-point-red",
                "Red Gain",
                "two-point",
                ControlType: MenuControlType.Action),
            new MenuNode("twenty-point", "20 Point", "white-balance"),
            new MenuNode(
                "twenty-point-red",
                "Red",
                "twenty-point",
                ControlType: MenuControlType.Action)
        ],
        [
            new MenuTransition(
                "to-two-point-red",
                "normal-video",
                "two-point-red",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_ENTER", Repeat: 2)
                ],
                true),
            new MenuTransition(
                "to-twenty-point-red",
                "normal-video",
                "twenty-point-red",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_ENTER"),
                    new MenuOperation("KEY_DOWN"),
                    new MenuOperation("KEY_ENTER")
                ],
                true)
        ],
        [
            new MenuAnchor(
                "normal",
                "Return to normal video",
                "normal-video",
                [new MenuOperation("KEY_RETURN")],
                true)
        ],
        new MenuTimingProfile(150, 800, 300, true));

    [Fact]
    public void ReconcileRemovesRouteManifestEvidenceWhenTheRouteNeedsValidation()
    {
        var draft = new MenuDefinition(
            "draft-route",
            "Draft route",
            "S95F",
            new MenuDefinitionContext("1296"),
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
            new MenuDefinitionContext("1296"),
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
