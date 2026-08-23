using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class MenuTraversalRecorderTests
{
    [Fact]
    public void TransitionIdentityReusesDraftWithTheSameSourceAndTarget()
    {
        var definition = CreateDefinition([
            new MenuTransition(
                "legacy-open-settings",
                "normal-video",
                "settings",
                [new MenuOperation("KEY_MENU")])
        ]);
        var request = new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "new-transition",
            "Ignored transition intent",
            "normal-video",
            "settings",
            null,
            null);

        var resolved = request.ResolveIdentity(definition);

        Assert.Equal("legacy-open-settings", resolved.ItemId);
        Assert.Equal("Settings", resolved.Label);
    }

    [Fact]
    public void TransitionIdentityIgnoresAnExistingUnrelatedDefaultId()
    {
        var definition = CreateDefinition([
            new MenuTransition(
                "new-transition",
                "settings",
                "normal-video",
                [new MenuOperation("KEY_RETURN")])
        ]);
        var request = new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            string.Empty,
            string.Empty,
            "normal-video",
            "settings",
            null,
            null);

        var resolved = request.ResolveIdentity(definition);

        Assert.Equal("to-settings", resolved.ItemId);
        Assert.Equal("Settings", resolved.Label);
    }

    [Fact]
    public void VerifiedRouteIsProtectedByItsSourceAndTargetInsteadOfItsId()
    {
        var definition = CreateDefinition([
            new MenuTransition(
                "open-settings",
                "normal-video",
                "settings",
                [new MenuOperation("KEY_MENU")],
                Verified: true)
        ]);
        var request = new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "unrelated-id",
            string.Empty,
            "normal-video",
            "settings",
            null,
            null);

        var exception = Assert.Throws<InvalidOperationException>(
            () => request.ResolveIdentity(definition));

        Assert.Contains("already verified", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unrelated-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturesSuccessfulButtonOrderAndCoalescesRepeats()
    {
        var recorder = new MenuTraversalRecorder();
        recorder.Start(new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "open-expert",
            "Open Expert Settings",
            "picture",
            "expert",
            null,
            null),
            new MenuTimingProfile(450, 600, 350));

        recorder.Record("KEY_DOWN", RemoteKeyAction.Click);
        recorder.Record("KEY_DOWN", RemoteKeyAction.Click);
        recorder.Record("KEY_ENTER", RemoteKeyAction.Click);
        recorder.Stop();

        Assert.False(recorder.IsRecording);
        Assert.Collection(
            recorder.Operations,
            operation =>
            {
                Assert.Equal("KEY_DOWN", operation.Key);
                Assert.Equal(2, operation.Repeat);
                Assert.Null(operation.DelayAfter);
            },
            operation =>
            {
                Assert.Equal("KEY_ENTER", operation.Key);
                Assert.Null(operation.DelayAfter);
            });
        Assert.Equal(TimeSpan.FromMilliseconds(450), recorder.Timing.GetDelay("KEY_DOWN"));
        Assert.Equal(TimeSpan.FromMilliseconds(600), recorder.Timing.GetDelay("KEY_ENTER"));
    }

    [Fact]
    public void RecordsSystemTimingAsInheritedInsteadOfEmbeddingOverrides()
    {
        var recorder = new MenuTraversalRecorder();
        recorder.Start(new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "open-picture",
            "Open Picture",
            "normal",
            "picture",
            null,
            null),
            new MenuTimingProfile());

        recorder.Record("KEY_MENU", RemoteKeyAction.Click);
        recorder.Record("KEY_DOWN", RemoteKeyAction.Click);
        recorder.Record("KEY_ENTER", RemoteKeyAction.Click);
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);

        Assert.Collection(
            recorder.Operations,
            operation => Assert.Null(operation.DelayAfter),
            operation => Assert.Null(operation.DelayAfter),
            operation => Assert.Null(operation.DelayAfter),
            operation => Assert.Null(operation.DelayAfter));
    }

    [Fact]
    public void ForwardAndReturnKeysRemainInOneTwoPhaseRecording()
    {
        var recorder = new MenuTraversalRecorder();
        recorder.Start(new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "open-picture",
            "Open Picture",
            "normal-video",
            "settings",
            null,
            null,
            RecordReturnToVideo: true),
            new MenuTimingProfile());

        recorder.Record("KEY_MENU", RemoteKeyAction.Click);
        recorder.BeginReturnToVideo();
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);

        Assert.True(recorder.IsRecordingReturnToVideo);
        Assert.Equal("KEY_MENU", Assert.Single(recorder.ForwardOperations).Key);
        Assert.Equal(
            "KEY_RETURN",
            Assert.Single(recorder.ReturnToVideoOperations).Key);
        Assert.Equal(
            "KEY_RETURN",
            Assert.Single(recorder.Operations).Key);
    }

    [Fact]
    public void UndoRemovesOneButtonPressFromARepeat()
    {
        var recorder = new MenuTraversalRecorder();
        recorder.Start(new MenuRecordingRequest(
            MenuAuthoringItemKind.Anchor,
            "normal",
            "Return to video",
            null,
            "normal-video",
            null,
            null),
            new MenuTimingProfile());
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);

        recorder.UndoLastCommand();

        Assert.Equal(1, Assert.Single(recorder.Operations).Repeat);
    }

    private static MenuDefinition CreateDefinition(IReadOnlyList<MenuTransition> transitions) =>
        new(
            "recording-test",
            "Recording Test",
            "Test TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings")
            ],
            transitions,
            []);
}
