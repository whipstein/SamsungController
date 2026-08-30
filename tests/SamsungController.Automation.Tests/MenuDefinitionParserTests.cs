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
        Assert.Equal("menu", definition.Nodes["picture"].ParentId);
        Assert.Equal(
            ["auto-picture", "picture", "brightness", "reset-picture", "sound-output", "interval", "interval-red", "smart-calibration", "unavailable-feature"],
            definition.Nodes.Values
                .Where(node => node.ParentId == "menu")
                .Select(node => node.Id));
        Assert.Equal(MenuControlType.Selection, definition.Nodes["picture"].ControlType);
        Assert.Equal("Movie", definition.Nodes["picture"].DefaultValue);
        Assert.Equal(
            ["Standard", "Movie", "Filmmaker Mode"],
            definition.Nodes["picture"].SelectionOptions);
        Assert.Equal(MenuControlType.Slider, definition.Nodes["brightness"].ControlType);
        Assert.Equal(0m, definition.Nodes["brightness"].MinimumValue);
        Assert.Equal(100m, definition.Nodes["brightness"].MaximumValue);
        Assert.Equal(MenuControlType.Confirmation, definition.Nodes["reset-picture"].ControlType);
        Assert.Equal(["Reset", "Cancel"], definition.Nodes["reset-picture"].SelectionOptions);
        Assert.Equal(
            MenuControlType.SubmenuSelection,
            definition.Nodes["sound-output"].ControlType);
        Assert.Equal(
            ["TV Speaker", "Receiver", "Bluetooth Speaker"],
            definition.Nodes["sound-output"].SelectionOptions);
        Assert.Equal(MenuControlType.IndexedSelection, definition.Nodes["interval"].ControlType);
        Assert.Equal(["5%", "10%", "15%"], definition.Nodes["interval"].SelectionOptions);
        Assert.Equal(MenuControlType.Action, definition.Nodes["smart-calibration"].ControlType);
        Assert.Null(definition.Nodes["smart-calibration"].DefaultValue);
        Assert.True(definition.Nodes["unavailable-feature"].Disabled);
        var disabledCondition = Assert.Single(definition.Nodes["picture"].DisabledWhen!);
        Assert.Equal("auto-picture", disabledCondition.SettingNodeId);
        Assert.Equal("on", disabledCondition.EqualsValue);
        var hiddenCondition = Assert.Single(definition.Nodes["picture"].HiddenWhen!);
        Assert.Equal("auto-picture", hiddenCondition.SettingNodeId);
        Assert.Equal("on", hiddenCondition.EqualsValue);
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
            "        label: Picture",
            "        label: Picture\n        guessedIndex: 4",
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
    public void ParentFieldsAreRejectedInFavorOfNestedChildren()
    {
        const string yaml =
            """
            version: 1
            id: flat-menu
            name: Flat Menu
            model: Test TV
            nodes:
              - id: settings
                label: Settings
              - id: picture
                label: Picture
                parent: settings
            """;

        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionParser().Parse(yaml));

        Assert.Contains("parent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unknown field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildrenAreOnlyValidForSubmenus()
    {
        const string yaml =
            """
            version: 1
            id: invalid-children
            name: Invalid Children
            model: Test TV
            nodes:
              - id: picture-mode
                label: Picture Mode
                controlType: selection
                defaultValue: Standard
                options: [Standard, Movie]
                children:
                  - id: brightness
                    label: Brightness
            """;

        var exception = Assert.Throws<MenuDefinitionParseException>(
            () => new MenuDefinitionParser().Parse(yaml));

        Assert.Contains("only valid when controlType is submenu", exception.Message, StringComparison.Ordinal);
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
              adjustmentDelay: 60ms
              verified: true
            nodes:
              - id: normal-video
                label: Normal video
            """;

        var definition = new MenuDefinitionParser().Parse(yaml);

        Assert.Equal(
            new MenuTimingProfile(
                175,
                650,
                325,
                true,
                AdjustmentDelayMilliseconds: 60),
            definition.Timing);
    }

    [Fact]
    public void MissingTimingValuesUseReadyToRecordDefaults()
    {
        const string yaml =
            """
            version: 1
            id: default-timing-test
            name: Default Timing Test
            model: Test TV
            nodes:
              - id: normal-video
                label: Normal video
            """;

        var definition = new MenuDefinitionParser().Parse(yaml);

        Assert.Equal(new MenuTimingProfile(150, 800, 300, true), definition.Timing);
        Assert.Equal(75, definition.Timing.GetDelay("KEY_LEFT").TotalMilliseconds);
        Assert.Equal(75, definition.Timing.GetDelay("KEY_RIGHT").TotalMilliseconds);
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
        Assert.Equal(75, definition.Timing.AdjustmentDelayMilliseconds);
        Assert.True(definition.Timing.Verified);

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
            children:
              - id: auto-picture
                label: Auto Picture
                controlType: switch
                defaultValue: off
              - id: picture
                label: Picture
                controlType: selection
                defaultValue: Movie
                options:
                  - Standard
                  - Movie
                  - Filmmaker Mode
                disabledWhen:
                  - setting: auto-picture
                    equals: on
                hiddenWhen:
                  - setting: auto-picture
                    equals: on
              - id: brightness
                label: Brightness
                controlType: slider
                defaultValue: 50
                minimumValue: 0
                maximumValue: 100
              - id: reset-picture
                label: Reset Picture
                controlType: confirmation
                defaultValue: Cancel
                options:
                  - Reset
                  - Cancel
              - id: sound-output
                label: Sound Output
                controlType: submenu-selection
                defaultValue: Receiver
                options:
                  - TV Speaker
                  - Receiver
                  - Bluetooth Speaker
              - id: interval
                label: Interval
                controlType: indexed-selection
                defaultValue: 5%
                options: [5%, 10%, 15%]
              - id: interval-red
                label: Red
                controlType: slider
                defaultValue: 0
                minimumValue: -50
                maximumValue: 50
              - id: smart-calibration
                label: Smart Calibration
                controlType: action
              - id: unavailable-feature
                label: Unavailable Feature
                disabled: true
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
