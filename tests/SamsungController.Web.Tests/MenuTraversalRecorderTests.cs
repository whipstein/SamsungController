using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class MenuTraversalRecorderTests
{
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
}
