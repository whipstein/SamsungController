using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SamsungController.MenuDefinitionWriterTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task WrittenDefinitionRoundTripsWithoutLosingAuthoringData()
    {
        var definition = new MenuDefinition(
            "test-menu",
            "Owner's Test Menu",
            "Test TV",
            new MenuDefinitionContext("1296", "SDR", "Filmmaker Mode", "Home Theater System"),
            [
                new MenuNode("normal-video", "Normal video"),
                new MenuNode("settings", "Settings", "normal-video", "Owner's settings")
            ],
            [
                new MenuTransition(
                    "open-settings",
                    "normal-video",
                    "settings",
                    [
                        new MenuOperation("KEY_DOWN", Repeat: 4, DelayAfter: TimeSpan.FromMilliseconds(250)),
                        new MenuOperation("KEY_ENTER", RemoteKeyAction.Press, DelayAfter: TimeSpan.FromMilliseconds(500))
                    ],
                    Description: "Recorded in the UI")
            ],
            [
                new MenuAnchor(
                    "normal",
                    "Return to video",
                    "normal-video",
                    [new MenuOperation("KEY_RETURN", Repeat: 3, DelayAfter: TimeSpan.FromMilliseconds(300))],
                    true)
            ]);
        var path = Path.Combine(_directory, "menu.yaml");

        await new MenuDefinitionWriter().WriteFileAsync(path, definition);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal(definition.Id, reparsed.Id);
        Assert.Equal(definition.Name, reparsed.Name);
        Assert.Equal(definition.Context, reparsed.Context);
        Assert.Equal("Owner's settings", reparsed.Nodes["settings"].Description);
        Assert.True(reparsed.Anchors["normal"].Verified);
        var transition = reparsed.Transitions["open-settings"];
        Assert.False(transition.Verified);
        Assert.Equal(4, transition.Operations[0].Repeat);
        Assert.Equal(RemoteKeyAction.Press, transition.Operations[1].Action);
        Assert.Equal(TimeSpan.FromMilliseconds(500), transition.Operations[1].DelayAfter);
    }

    [Fact]
    public void EmptyAuthoringCollectionsRoundTrip()
    {
        var definition = new MenuDefinition(
            "new-tv",
            "New TV",
            "Samsung TV",
            new MenuDefinitionContext(),
            [new MenuNode("normal-video", "Normal video")],
            [],
            []);

        var yaml = new MenuDefinitionWriter().Serialize(definition);
        var reparsed = new MenuDefinitionParser().Parse(yaml);

        Assert.Empty(reparsed.Anchors);
        Assert.Empty(reparsed.Transitions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
