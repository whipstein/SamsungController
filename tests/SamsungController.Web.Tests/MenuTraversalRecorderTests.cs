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
            null,
            450));

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
                Assert.Equal(TimeSpan.FromMilliseconds(450), operation.DelayAfter);
            },
            operation =>
            {
                Assert.Equal("KEY_ENTER", operation.Key);
                Assert.Equal(TimeSpan.FromMilliseconds(500), operation.DelayAfter);
            });
    }

    [Fact]
    public void UsesFastDirectionalPacingAndLongerScreenSettling()
    {
        var recorder = new MenuTraversalRecorder();
        recorder.Start(new MenuRecordingRequest(
            MenuAuthoringItemKind.Transition,
            "open-picture",
            "Open Picture",
            "normal",
            "picture",
            null,
            null));

        recorder.Record("KEY_MENU", RemoteKeyAction.Click);
        recorder.Record("KEY_DOWN", RemoteKeyAction.Click);
        recorder.Record("KEY_ENTER", RemoteKeyAction.Click);
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);

        Assert.Collection(
            recorder.Operations,
            operation => Assert.Equal(TimeSpan.FromMilliseconds(500), operation.DelayAfter),
            operation => Assert.Equal(TimeSpan.FromMilliseconds(150), operation.DelayAfter),
            operation => Assert.Equal(TimeSpan.FromMilliseconds(500), operation.DelayAfter),
            operation => Assert.Equal(TimeSpan.FromMilliseconds(300), operation.DelayAfter));
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
            null));
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);
        recorder.Record("KEY_RETURN", RemoteKeyAction.Click);

        recorder.UndoLastCommand();

        Assert.Equal(1, Assert.Single(recorder.Operations).Repeat);
    }
}
