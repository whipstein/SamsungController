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
