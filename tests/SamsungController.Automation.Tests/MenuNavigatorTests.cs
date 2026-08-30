using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MenuNavigatorTests
{
    [Fact]
    public async Task AnchorAndVerifiedPlanUpdatePredictedState()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, target, delay);

        await navigator.ExecuteAnchorAsync("normal");
        var plan = navigator.Plan("settings");
        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_RETURN", "KEY_RETURN", "KEY_MENU"], target.Keys);
        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)], delay.Delays);
        Assert.Equal("settings", tracker.Current.NodeId);
        Assert.Equal(MenuStateConfidence.Probable, tracker.Current.Confidence);
    }

    [Fact]
    public async Task PrepareStateUsesVerifiedAnchorWhenCurrentStateIsUnknown()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, target, delay);

        navigator.ValidateStateCanBePrepared("settings");
        await navigator.PrepareStateAsync("settings");

        Assert.Equal(["KEY_RETURN", "KEY_RETURN", "KEY_MENU"], target.Keys);
        Assert.Equal("settings", tracker.Current.NodeId);
    }

    [Fact]
    public async Task PrepareStateSendsNothingWhenPredictionAlreadyMatches()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);
        tracker.ConfirmNode("settings", "Visible state confirmed.");

        await navigator.PrepareStateAsync("settings");

        Assert.Empty(target.Keys);
        Assert.Equal("settings", tracker.Current.NodeId);
    }

    [Fact]
    public async Task DraftPlansAreNeverExecuted()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);
        await navigator.ExecuteAnchorAsync("normal");
        var plan = navigator.Plan("picture", includeDraftTransitions: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => navigator.ExecutePlanAsync(plan));

        Assert.Contains("draft", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["KEY_RETURN", "KEY_RETURN"], target.Keys);
    }

    [Fact]
    public async Task PartialFailureMarksStateUnknown()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);
        await navigator.ExecuteAnchorAsync("normal");
        target.Exception = new IOException("send failed");

        await Assert.ThrowsAsync<IOException>(
            () => navigator.ExecutePlanAsync(navigator.Plan("settings")));

        Assert.Equal(MenuStateConfidence.Unknown, tracker.Current.Confidence);
        Assert.Null(tracker.Current.NodeId);
    }

    [Fact]
    public async Task SystemTimingAppliesWhenStepsHaveNoCustomDelay()
    {
        var definition = new MenuDefinition(
            "timed",
            "Timed",
            "TV",
            new MenuDefinitionContext(),
            [new MenuNode("normal", "Normal"), new MenuNode("settings", "Settings")],
            [new MenuTransition(
                "open",
                "normal",
                "settings",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_RIGHT"),
                    new MenuOperation("KEY_DOWN")
                ],
                true)],
            [new MenuAnchor("normal", "Normal", "normal", [new MenuOperation("KEY_RETURN", Repeat: 2)], true)],
            new MenuTimingProfile(125, 650, 325));
        var tracker = new MenuStateTracker(definition);
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, new RecordingTarget(), delay);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("settings"));

        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(325),
                TimeSpan.FromMilliseconds(325),
                TimeSpan.FromMilliseconds(650),
                TimeSpan.FromMilliseconds(75),
                TimeSpan.FromMilliseconds(125)
            ],
            delay.Delays);
    }

    [Fact]
    public async Task ReturnAnchorUsesMenuRootScriptWhenPositionIsKnown()
    {
        var definition = CreateStateAwareReturnDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("settings"));
        target.Keys.Clear();

        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_RETURN"], target.Keys);
        Assert.Equal("normal", tracker.Current.NodeId);
    }

    [Fact]
    public async Task ReturnAnchorUsesDeeperMenuScriptWhenPositionIsKnown()
    {
        var definition = CreateStateAwareReturnDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture"));
        target.Keys.Clear();

        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_MENU", "KEY_RETURN"], target.Keys);
        Assert.Equal("normal", tracker.Current.NodeId);
    }

    [Fact]
    public async Task ReturnAnchorPrefersVerifiedExactNodeOverride()
    {
        var definition = CreateStateAwareReturnDefinition(nodeOverrideVerified: true);
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture"));
        target.Keys.Clear();

        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_EXIT"], target.Keys);
        Assert.Equal("normal", tracker.Current.NodeId);
    }

    [Fact]
    public async Task UnverifiedExactNodeOverrideFallsBackToVerifiedDepthScript()
    {
        var definition = CreateStateAwareReturnDefinition(nodeOverrideVerified: false);
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture"));
        target.Keys.Clear();

        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_MENU", "KEY_RETURN"], target.Keys);
    }

    [Fact]
    public async Task PlanCalculatesRelativeRouteWhenNoDirectTransitionExists()
    {
        var definition = CreateStateAwareReturnDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture", includeDraftTransitions: false));
        target.Keys.Clear();

        var plan = navigator.Plan("settings", includeDraftTransitions: false);

        Assert.True(plan.UsesCalculatedRoute);
        Assert.False(plan.UsesAnchor);
        Assert.Equal("KEY_UP", Assert.Single(plan.CalculatedLeg!.Operations).Key);
        Assert.Equal(1, plan.CommandCount);

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_UP"], target.Keys);
        Assert.Equal("settings", tracker.Current.NodeId);
    }

    [Fact]
    public async Task PlanPrefersShorterDirectRouteOverAnchorRoute()
    {
        var definition = CreateStateAwareReturnDefinition(directReturnRepeat: 1);
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture", includeDraftTransitions: false));
        target.Keys.Clear();

        var plan = navigator.Plan("settings", includeDraftTransitions: false);

        Assert.False(plan.UsesAnchor);
        Assert.Equal("close-picture", Assert.Single(plan.Transitions).Id);

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_UP"], target.Keys);
    }

    [Fact]
    public async Task PlanPrefersShorterCalculatedRouteOverLongDirectRoute()
    {
        var definition = CreateStateAwareReturnDefinition(directReturnRepeat: 4);
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("picture", includeDraftTransitions: false));
        target.Keys.Clear();

        var plan = navigator.Plan("settings", includeDraftTransitions: false);

        Assert.True(plan.UsesCalculatedRoute);
        Assert.False(plan.UsesAnchor);
        Assert.Equal(1, plan.CommandCount);

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_UP"], target.Keys);
    }

    [Fact]
    public async Task PlanCalculatesSingleStepMovesBetweenAbsoluteSiblingRoutes()
    {
        var definition = CreateAbsoluteSiblingDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("brightness", includeDraftTransitions: false));
        target.Keys.Clear();

        var downPlan = navigator.Plan("contrast", includeDraftTransitions: false);
        Assert.True(downPlan.UsesCalculatedRoute);
        Assert.Equal("KEY_DOWN", Assert.Single(downPlan.CalculatedLeg!.Operations).Key);
        await navigator.ExecutePlanAsync(downPlan);

        target.Keys.Clear();
        var upPlan = navigator.Plan("brightness", includeDraftTransitions: false);
        Assert.True(upPlan.UsesCalculatedRoute);
        Assert.Equal("KEY_UP", Assert.Single(upPlan.CalculatedLeg!.Operations).Key);
        await navigator.ExecutePlanAsync(upPlan);

        Assert.Equal(["KEY_UP"], target.Keys);
        Assert.Equal("brightness", tracker.Current.NodeId);
    }

    [Fact]
    public async Task PlanReturnsDirectlyFromSettingToAncestorMenu()
    {
        var definition = CreateAbsoluteSiblingDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, target, delay);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("brightness", includeDraftTransitions: false));
        target.Keys.Clear();
        delay.Delays.Clear();

        var plan = navigator.Plan("settings", includeDraftTransitions: false);

        Assert.True(plan.UsesCalculatedRoute);
        Assert.False(plan.UsesAnchor);
        var operation = Assert.Single(plan.CalculatedLeg!.Operations);
        Assert.Equal("KEY_RETURN", operation.Key);
        Assert.Equal(2, operation.Repeat);
        Assert.Equal(TimeSpan.FromMilliseconds(800), operation.DelayAfter);

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_RETURN", "KEY_RETURN"], target.Keys);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800)],
            delay.Delays);
        Assert.Equal("settings", tracker.Current.NodeId);
    }

    [Fact]
    public async Task CalculatedCrossBranchRouteWaitsForSubmenuReturnToFinish()
    {
        var definition = CreateCrossBranchDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, target, delay);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("two-point-red", includeDraftTransitions: false));
        target.Keys.Clear();
        delay.Delays.Clear();

        var plan = navigator.Plan("twenty-point-red", includeDraftTransitions: false);

        Assert.True(plan.UsesCalculatedRoute);
        Assert.False(plan.UsesAnchor);
        Assert.Collection(
            plan.CalculatedLeg!.Operations,
            operation =>
            {
                Assert.Equal("KEY_RETURN", operation.Key);
                Assert.Equal(TimeSpan.FromMilliseconds(800), operation.DelayAfter);
            },
            operation => Assert.Equal("KEY_DOWN", operation.Key),
            operation => Assert.Equal("KEY_ENTER", operation.Key));

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_RETURN", "KEY_DOWN", "KEY_ENTER"], target.Keys);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(800),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(800)
            ],
            delay.Delays);
        Assert.Equal("twenty-point-red", tracker.Current.NodeId);
    }

    [Fact]
    public async Task CalculatedColorSpaceToWhiteBalanceRouteWaitsAfterEverySubmenuExit()
    {
        var definition = CreateCrossBranchDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var delay = new RecordingDelay();
        var navigator = new MenuNavigator(definition, tracker, target, delay);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("color-red", includeDraftTransitions: false));
        target.Keys.Clear();
        delay.Delays.Clear();

        var plan = navigator.Plan("two-point-red", includeDraftTransitions: false);

        Assert.True(plan.UsesCalculatedRoute);
        Assert.Collection(
            plan.CalculatedLeg!.Operations,
            operation =>
            {
                Assert.Equal("KEY_RETURN", operation.Key);
                Assert.Equal(2, operation.Repeat);
                Assert.Equal(TimeSpan.FromMilliseconds(800), operation.DelayAfter);
            },
            operation => Assert.Equal("KEY_UP", operation.Key),
            operation =>
            {
                Assert.Equal("KEY_ENTER", operation.Key);
                Assert.Equal(2, operation.Repeat);
            });

        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(
            ["KEY_RETURN", "KEY_RETURN", "KEY_UP", "KEY_ENTER", "KEY_ENTER"],
            target.Keys);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(800),
                TimeSpan.FromMilliseconds(800),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(800),
                TimeSpan.FromMilliseconds(800)
            ],
            delay.Delays);
        Assert.Equal("two-point-red", tracker.Current.NodeId);
    }

    [Fact]
    public async Task PlanUsesVerifiedAnchorWhenSourcePathCannotBeSafelyInverted()
    {
        var definition = CreateNonInvertibleRouteDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        await navigator.ExecutePlanAsync(navigator.Plan("source", includeDraftTransitions: false));
        target.Keys.Clear();

        var plan = navigator.Plan("target", includeDraftTransitions: false);

        Assert.True(plan.UsesAnchor);
        Assert.False(plan.UsesCalculatedRoute);
        await navigator.ExecutePlanAsync(plan);

        Assert.Equal(["KEY_EXIT", "KEY_MENU"], target.Keys);
        Assert.Equal("target", tracker.Current.NodeId);
    }

    [Fact]
    public async Task ReturnAnchorUsesFallbackWhenPositionIsUnknownOrScriptIsUnverified()
    {
        var definition = CreateStateAwareReturnDefinition(deeperScriptVerified: false);
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        Assert.Equal(["KEY_MENU", "KEY_MENU"], target.Keys);

        await navigator.ExecutePlanAsync(navigator.Plan("picture"));
        target.Keys.Clear();
        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_MENU", "KEY_MENU"], target.Keys);
    }

    [Fact]
    public async Task ReturnAnchorStillRunsFallbackWhenPredictionIsAlreadyAtTarget()
    {
        var definition = CreateStateAwareReturnDefinition();
        var tracker = new MenuStateTracker(definition);
        var target = new RecordingTarget();
        var navigator = new MenuNavigator(definition, tracker, target);

        await navigator.ExecuteAnchorAsync("normal");
        target.Keys.Clear();
        await navigator.ExecuteAnchorAsync("normal");

        Assert.Equal(["KEY_MENU", "KEY_MENU"], target.Keys);
        Assert.Equal(MenuStateConfidence.Synchronized, tracker.Current.Confidence);
    }

    [Fact]
    public void ManualUnmodeledNavigationInvalidatesPredictionButVolumeDoesNot()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);
        tracker.ApplyAnchor(definition.GetRequiredAnchor("normal"));

        tracker.ObserveCommand("KEY_VOLUP", RemoteKeyAction.Click);
        Assert.Equal(MenuStateConfidence.Synchronized, tracker.Current.Confidence);

        tracker.ObserveCommand("KEY_DOWN", RemoteKeyAction.Click);
        Assert.Equal(MenuStateConfidence.Unknown, tracker.Current.Confidence);
    }

    [Fact]
    public void VisualConfirmationSynchronizesTheConfirmedNode()
    {
        var definition = CreateDefinition();
        var tracker = new MenuStateTracker(definition);

        tracker.ConfirmNode("settings", "The user confirmed the visible menu.");

        Assert.Equal("settings", tracker.Current.NodeId);
        Assert.Equal("Settings", tracker.Current.Path);
        Assert.Equal(MenuStateConfidence.Synchronized, tracker.Current.Confidence);
        Assert.Equal("The user confirmed the visible menu.", tracker.Current.Reason);
    }

    private static MenuDefinition CreateDefinition() => new(
        "test",
        "Test",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("normal", "Normal video"),
            new MenuNode("settings", "Settings"),
            new MenuNode("picture", "Picture", "settings")
        ],
        [
            new MenuTransition(
                "open-settings",
                "normal",
                "settings",
                [new MenuOperation("KEY_MENU", DelayAfter: TimeSpan.FromMilliseconds(20))],
                true),
            new MenuTransition(
                "open-picture-draft",
                "settings",
                "picture",
                [new MenuOperation("KEY_ENTER")])
        ],
        [
            new MenuAnchor(
                "normal",
                "Back to video",
                "normal",
                [new MenuOperation("KEY_RETURN", Repeat: 2, DelayAfter: TimeSpan.FromMilliseconds(10))],
                true)
        ]);

    private static MenuDefinition CreateStateAwareReturnDefinition(
        bool deeperScriptVerified = true,
        int? directReturnRepeat = null,
        bool? nodeOverrideVerified = null) => new(
        "state-aware-return",
        "State-aware return",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("normal", "Normal video"),
            new MenuNode("settings", "Settings"),
            new MenuNode("picture", "Picture", "settings")
        ],
        CreateStateAwareTransitions(directReturnRepeat),
        [
            new MenuAnchor(
                "normal",
                "Back to video",
                "normal",
                [new MenuOperation("KEY_MENU", Repeat: 2)],
                true,
                ReturnStrategy: new MenuReturnStrategy(
                    "settings",
                    new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                    new MenuReturnScript(
                        [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_RETURN")],
                        deeperScriptVerified),
                    nodeOverrideVerified is null
                        ? []
                        : [
                            new MenuReturnOverride(
                                "picture",
                                new MenuReturnScript(
                                    [new MenuOperation("KEY_EXIT")],
                                    nodeOverrideVerified.Value))
                        ]))
        ]);

    private static IReadOnlyList<MenuTransition> CreateStateAwareTransitions(
        int? directReturnRepeat)
    {
        var transitions = new List<MenuTransition>
        {
            new(
                "open-settings",
                "normal",
                "settings",
                [new MenuOperation("KEY_MENU")],
                true),
            new(
                "open-picture",
                "settings",
                "picture",
                [new MenuOperation("KEY_DOWN")],
                true)
        };
        if (directReturnRepeat is { } repeat)
        {
            transitions.Add(new MenuTransition(
                "close-picture",
                "picture",
                "settings",
                [new MenuOperation("KEY_UP", Repeat: repeat)],
                true));
        }

        return transitions;
    }

    private static MenuDefinition CreateAbsoluteSiblingDefinition() => new(
        "absolute-siblings",
        "Absolute siblings",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("normal", "Normal video"),
            new MenuNode("settings", "Settings"),
            new MenuNode("picture", "Picture", "settings"),
            new MenuNode("expert", "Expert Settings", "picture"),
            new MenuNode("brightness", "Brightness", "expert"),
            new MenuNode("contrast", "Contrast", "expert")
        ],
        [
            new MenuTransition(
                "to-settings",
                "normal",
                "settings",
                [new MenuOperation("KEY_MENU")],
                true),
            new MenuTransition(
                "to-brightness",
                "normal",
                "brightness",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_DOWN"),
                    new MenuOperation("KEY_ENTER"),
                    new MenuOperation("KEY_DOWN", Repeat: 4),
                    new MenuOperation("KEY_ENTER")
                ],
                true),
            new MenuTransition(
                "to-contrast",
                "normal",
                "contrast",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_DOWN"),
                    new MenuOperation("KEY_ENTER"),
                    new MenuOperation("KEY_DOWN", Repeat: 4),
                    new MenuOperation("KEY_ENTER"),
                    new MenuOperation("KEY_DOWN")
                ],
                true)
        ],
        [
            new MenuAnchor(
                "normal",
                "Return to normal video",
                "normal",
                [new MenuOperation("KEY_MENU", Repeat: 2)],
                true)
        ]);

    private static MenuDefinition CreateNonInvertibleRouteDefinition() => new(
        "non-invertible",
        "Non-invertible",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("normal", "Normal video"),
            new MenuNode("source", "Source"),
            new MenuNode("target", "Target")
        ],
        [
            new MenuTransition(
                "to-source",
                "normal",
                "source",
                [new MenuOperation("KEY_SOURCE")],
                true),
            new MenuTransition(
                "to-target",
                "normal",
                "target",
                [new MenuOperation("KEY_MENU")],
                true)
        ],
        [
            new MenuAnchor(
                "normal",
                "Return to normal video",
                "normal",
                [new MenuOperation("KEY_EXIT")],
                true)
        ]);

    private static MenuDefinition CreateCrossBranchDefinition() => new(
        "cross-branch",
        "Cross branch",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("normal", "Normal video"),
            new MenuNode("settings", "Settings", "normal"),
            new MenuNode("expert", "Expert Settings", "settings"),
            new MenuNode("white-balance", "White Balance", "expert"),
            new MenuNode("two-point", "2 Point", "white-balance"),
            new MenuNode("two-point-red", "Red Gain", "two-point"),
            new MenuNode("twenty-point", "20 Point", "white-balance"),
            new MenuNode("twenty-point-red", "Red", "twenty-point"),
            new MenuNode("color-space", "Color Space", "expert"),
            new MenuNode("custom-color", "Custom", "color-space"),
            new MenuNode("color-red", "Red", "custom-color")
        ],
        [
            new MenuTransition(
                "to-two-point-red",
                "normal",
                "two-point-red",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_ENTER", Repeat: 3)
                ],
                true),
            new MenuTransition(
                "to-twenty-point-red",
                "normal",
                "twenty-point-red",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_ENTER", Repeat: 2),
                    new MenuOperation("KEY_DOWN"),
                    new MenuOperation("KEY_ENTER")
                ],
                true),
            new MenuTransition(
                "to-color-red",
                "normal",
                "color-red",
                [
                    new MenuOperation("KEY_MENU"),
                    new MenuOperation("KEY_ENTER"),
                    new MenuOperation("KEY_DOWN"),
                    new MenuOperation("KEY_ENTER", Repeat: 2)
                ],
                true)
        ],
        [
            new MenuAnchor(
                "normal",
                "Return to normal video",
                "normal",
                [new MenuOperation("KEY_RETURN")],
                true)
        ],
        new MenuTimingProfile(150, 800, 300, true));

    private sealed class RecordingTarget : IMenuCommandTarget
    {
        public List<string> Keys { get; } = [];

        public Exception? Exception { get; set; }

        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Keys.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDelay : IMenuDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            Delays.Add(duration);
            return Task.CompletedTask;
        }
    }
}
