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
            new MenuDefinitionContext("example-fw", "SDR", "Movie", "HDMI 1"),
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
                    Description: "Recorded in the UI",
                    ReturnToVideoOperations:
                    [
                        new MenuOperation("KEY_RETURN", DelayAfter: TimeSpan.FromMilliseconds(325))
                    ])
            ],
            [
                new MenuAnchor(
                    "normal",
                    "Return to video",
                    "normal-video",
                    [new MenuOperation("KEY_RETURN", Repeat: 3, DelayAfter: TimeSpan.FromMilliseconds(300))],
                    true,
                    ReturnStrategy: new MenuReturnStrategy(
                        "settings",
                        new MenuReturnScript([new MenuOperation("KEY_RETURN")], true),
                        new MenuReturnScript([
                            new MenuOperation("KEY_MENU"),
                            new MenuOperation("KEY_RETURN")
                        ]),
                        [
                            new MenuReturnOverride(
                                "settings",
                                new MenuReturnScript([new MenuOperation("KEY_EXIT")], true))
                        ]),
                    ValidationSourceNodeId: "settings")
            ],
            new MenuTimingProfile(175, 650, 325, true));
        var path = Path.Combine(_directory, "menu.yaml");

        await new MenuDefinitionWriter().WriteFileAsync(path, definition);
        var reparsed = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal(definition.Id, reparsed.Id);
        Assert.Equal(definition.Name, reparsed.Name);
        Assert.Equal(definition.Context, reparsed.Context);
        Assert.Equal(definition.Timing, reparsed.Timing);
        Assert.Contains("  verified: true", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Equal("Owner's settings", reparsed.Nodes["settings"].Description);
        Assert.True(reparsed.Anchors["normal"].Verified);
        Assert.Equal("settings", reparsed.Anchors["normal"].ValidationSourceNodeId);
        var returnStrategy = Assert.IsType<MenuReturnStrategy>(
            reparsed.Anchors["normal"].ReturnStrategy);
        Assert.Equal("settings", returnStrategy.MenuRootNodeId);
        Assert.True(returnStrategy.AtMenuRoot.Verified);
        Assert.Equal("KEY_RETURN", Assert.Single(returnStrategy.AtMenuRoot.Operations).Key);
        Assert.False(returnStrategy.BelowMenuRoot.Verified);
        Assert.Equal(
            ["KEY_MENU", "KEY_RETURN"],
            returnStrategy.BelowMenuRoot.Operations.Select(operation => operation.Key));
        var nodeOverride = Assert.Single(returnStrategy.NodeOverrides!);
        Assert.Equal("settings", nodeOverride.NodeId);
        Assert.True(nodeOverride.Script.Verified);
        Assert.Equal("KEY_EXIT", Assert.Single(nodeOverride.Script.Operations).Key);
        var transition = reparsed.Transitions["open-settings"];
        Assert.False(transition.Verified);
        Assert.Equal(4, transition.Operations[0].Repeat);
        Assert.Equal(RemoteKeyAction.Press, transition.Operations[1].Action);
        Assert.Equal(TimeSpan.FromMilliseconds(500), transition.Operations[1].DelayAfter);
        Assert.Equal(
            "KEY_RETURN",
            Assert.Single(transition.ReturnToVideoOperations!).Key);
        Assert.Equal(
            TimeSpan.FromMilliseconds(325),
            Assert.Single(transition.ReturnToVideoOperations!).DelayAfter);
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
