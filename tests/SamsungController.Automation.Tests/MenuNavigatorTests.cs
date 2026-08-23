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
