using SamsungController.Automation.Navigation;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionParserTests
{
    [Fact]
    public void ParsesContextTreeAnchorsAndTransitions()
    {
        var definition = new MenuDefinitionParser().Parse(ValidYaml);

        Assert.Equal("test-menu", definition.Id);
        Assert.Equal("Test TV", definition.Model);
        Assert.Equal("1234.5", definition.Context.Firmware);
        Assert.Equal("Menu / Picture", definition.GetPath("picture"));
        var anchor = definition.GetRequiredAnchor("normal");
        Assert.True(anchor.Verified);
        Assert.Equal(3, Assert.Single(anchor.Operations).Repeat);
        var transition = Assert.Single(definition.Transitions.Values);
        Assert.Equal(RemoteKeyAction.Click, Assert.Single(transition.Operations).Action);
        Assert.Equal(TimeSpan.FromMilliseconds(500), transition.Operations[0].DelayAfter);
    }

    [Fact]
    public void UnknownFieldsAreRejectedInsteadOfIgnored()
    {
        var yaml = ValidYaml.Replace(
            "    label: Picture",
            "    label: Picture\n    guessedIndex: 4",
            StringComparison.Ordinal);

        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionParser().Parse(yaml));

        Assert.Contains("guessedIndex", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidDurationsProduceActionableErrors()
    {
        var yaml = ValidYaml.Replace("500ms", "soon", StringComparison.Ordinal);

        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionParser().Parse(yaml));

        Assert.Contains("milliseconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledS95fDefinitionContainsObservedExpertSettingsRoute()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "s95f-draft.yaml");

        var definition = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal("1296", definition.Context.Firmware);
        Assert.Equal("SDR", definition.Context.Signal);
        Assert.Equal("Filmmaker Mode", definition.Context.PictureMode);
        Assert.Equal("Home Theater System", definition.Context.Input);

        var normalVideo = definition.GetRequiredAnchor("normal-video");
        Assert.True(normalVideo.Verified);
        var reset = Assert.Single(normalVideo.Operations);
        Assert.Equal("KEY_MENU", reset.Key);
        Assert.Equal(2, reset.Repeat);

        var picture = definition.Transitions["open-picture"];
        Assert.True(picture.Verified);
        Assert.Equal("settings-overlay", picture.FromNodeId);
        Assert.Equal("picture", picture.ToNodeId);
        Assert.Collection(
            picture.Operations,
            operation => Assert.Equal("KEY_DOWN", operation.Key),
            operation => Assert.Equal("KEY_ENTER", operation.Key));

        var expertSettings = definition.Transitions["open-expert-settings"];
        Assert.True(expertSettings.Verified);
        Assert.Equal("picture", expertSettings.FromNodeId);
        Assert.Equal("expert-settings", expertSettings.ToNodeId);
        Assert.Equal(4, expertSettings.Operations[0].Repeat);
        Assert.Equal("KEY_ENTER", expertSettings.Operations[1].Key);

        var exit = definition.Transitions["exit-expert-settings"];
        Assert.True(exit.Verified);
        Assert.Equal("normal-video", exit.ToNodeId);
        Assert.Equal(2, Assert.Single(exit.Operations).Repeat);

        var forwardPlan = new NavigationPlanner().Plan(
            definition,
            "normal-video",
            "expert-settings");
        Assert.True(forwardPlan.IsExecutable);
        Assert.Equal(8, forwardPlan.CommandCount);
        var exitPlan = new NavigationPlanner().Plan(
            definition,
            "expert-settings",
            "normal-video");
        Assert.True(exitPlan.IsExecutable);
        Assert.Equal(2, exitPlan.CommandCount);
    }

    private const string ValidYaml =
        """
        version: 1
        id: test-menu
        name: Test Menu
        model: Test TV
        context:
          firmware: 1234.5
          signal: SDR
          pictureMode: Movie
          input: HDMI 1
        nodes:
          - id: normal-video
            label: Normal video
          - id: menu
            label: Menu
          - id: picture
            label: Picture
            parent: menu
        anchors:
          - id: normal
            label: Back to video
            target: normal-video
            verified: true
            steps:
              - key: KEY_RETURN
                repeat: 3
                delay: 150ms
        transitions:
          - id: open-menu
            from: normal-video
            to: menu
            verified: true
            steps:
              - key: KEY_MENU
                delay: 500ms
        """;
}
