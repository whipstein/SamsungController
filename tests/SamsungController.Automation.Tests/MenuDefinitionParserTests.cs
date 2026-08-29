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
        Assert.Equal(MenuControlType.Selection, definition.Nodes["picture"].ControlType);
        Assert.Equal("Movie", definition.Nodes["picture"].DefaultValue);
        Assert.Equal(
            ["Standard", "Movie", "Filmmaker Mode"],
            definition.Nodes["picture"].SelectionOptions);
        var disabledCondition = Assert.Single(definition.Nodes["picture"].DisabledWhen!);
        Assert.Equal("auto-picture", disabledCondition.SettingNodeId);
        Assert.Equal("on", disabledCondition.EqualsValue);
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
    public void ParsesPersistedSystemTimingVerification()
    {
        const string yaml =
            """
            version: 1
            id: timing-test
            name: Timing Test
            model: Test TV
            timing:
              defaultDelay: 175ms
              screenChangeDelay: 650ms
              returnDelay: 325ms
              verified: true
            nodes:
              - id: normal-video
                label: Normal video
            """;

        var definition = new MenuDefinitionParser().Parse(yaml);

        Assert.Equal(new MenuTimingProfile(175, 650, 325, true), definition.Timing);
    }

    [Fact]
    public async Task BundledGenericDefinitionContainsNoVerifiedRoutes()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "menu.example.yaml");

        var definition = await new MenuDefinitionParser().ParseFileAsync(path);

        Assert.Equal("generic-picture-menu", definition.Id);
        Assert.Equal("unrecorded", definition.Context.Firmware);
        Assert.Equal("unrecorded", definition.Context.Signal);
        Assert.Equal("unrecorded", definition.Context.PictureMode);
        Assert.Equal("unrecorded", definition.Context.Input);
        Assert.Equal(300, definition.Timing.DefaultDelayMilliseconds);
        Assert.Equal(800, definition.Timing.ScreenChangeDelayMilliseconds);
        Assert.Equal(300, definition.Timing.ReturnDelayMilliseconds);
        Assert.False(definition.Timing.Verified);

        var normalVideo = definition.GetRequiredAnchor("normal-video");
        Assert.False(normalVideo.Verified);
        var returnStrategy = Assert.IsType<MenuReturnStrategy>(normalVideo.ReturnStrategy);
        Assert.Equal("settings", returnStrategy.MenuRootNodeId);
        Assert.False(returnStrategy.AtMenuRoot.Verified);
        Assert.Equal("KEY_RETURN", Assert.Single(returnStrategy.AtMenuRoot.Operations).Key);
        Assert.False(returnStrategy.BelowMenuRoot.Verified);
        Assert.Equal("KEY_RETURN", Assert.Single(returnStrategy.BelowMenuRoot.Operations).Key);
        Assert.All(definition.Transitions.Values, transition => Assert.False(transition.Verified));

        Assert.Throws<NavigationPlanningException>(() =>
            new NavigationPlanner().Plan(
                definition,
                "normal-video",
                "expert-settings"));
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
          - id: auto-picture
            label: Auto Picture
            parent: menu
            controlType: switch
            defaultValue: off
          - id: picture
            label: Picture
            parent: menu
            controlType: selection
            defaultValue: Movie
            options:
              - Standard
              - Movie
              - Filmmaker Mode
            disabledWhen:
              - setting: auto-picture
                equals: on
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
