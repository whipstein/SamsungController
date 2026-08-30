using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefinitionValidatorTests
{
    [Fact]
    public void ReportsMissingReferencesInvalidOperationsAndParentCycles()
    {
        var definition = new MenuDefinition(
            "test",
            "Test",
            "TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("first", "First", "second"),
                new MenuNode("second", "Second", "first")
            ],
            [
                new MenuTransition(
                    "broken",
                    "missing",
                    "first",
                    [new MenuOperation("", Repeat: 0)])
            ],
            [new MenuAnchor("anchor", "Anchor", "missing", [])],
            new MenuTimingProfile(DefaultDelayMilliseconds: 20));

        var errors = new MenuDefinitionValidator().Validate(definition);

        Assert.Contains(errors, error => error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("Source node", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Target node", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("key cannot be empty", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Repeat", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("At least one", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Location == "timing.defaultDelay");
    }

    [Fact]
    public void ValidatesControlDefaultsAndDisabledSettingReferences()
    {
        var definition = new MenuDefinition(
            "controls",
            "Controls",
            "TV",
            new MenuDefinitionContext(),
            [
                new MenuNode("menu", "Menu"),
                new MenuNode(
                    "bad-switch",
                    "Bad switch",
                    "menu",
                    ControlType: MenuControlType.Switch,
                    DefaultValue: "maybe"),
                new MenuNode(
                    "brightness",
                    "Brightness",
                    "menu",
                    ControlType: MenuControlType.Slider,
                    DisabledWhen:
                    [
                        new MenuNodeDisabledCondition("missing", "on"),
                        new MenuNodeDisabledCondition("menu", "open")
                    ]),
                new MenuNode(
                    "picture-mode",
                    "Picture Mode",
                    "menu",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Filmmaker Mode",
                    SelectionOptions: ["Standard", "Movie", "movie"]),
                new MenuNode(
                    "color-tone",
                    "Color Tone",
                    "menu",
                    ControlType: MenuControlType.Selection,
                    DefaultValue: "Warm2"),
                new MenuNode(
                    "dependent",
                    "Dependent",
                    "menu",
                    DisabledWhen: [new MenuNodeDisabledCondition("picture-mode", "Dynamic")],
                    HiddenWhen:
                    [
                        new MenuNodeHiddenCondition("missing-hidden", "on"),
                        new MenuNodeHiddenCondition("menu", "open")
                    ]),
                new MenuNode(
                    "bad-slider-range",
                    "Bad slider range",
                    "menu",
                    ControlType: MenuControlType.Slider,
                    DefaultValue: "20",
                    MinimumValue: 0,
                    MaximumValue: 10),
                new MenuNode(
                    "reset-picture",
                    "Reset Picture",
                    "menu",
                    ControlType: MenuControlType.Confirmation,
                    DefaultValue: "Cancel",
                    SelectionOptions: ["Reset"])
            ],
            [],
            []);

        var errors = new MenuDefinitionValidator().Validate(definition);

        Assert.Contains(errors, error => error.Message.Contains("switch default", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("slider must define", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("must be a slider", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("must match an available option", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("option 'movie' is duplicated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("at least one available option", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("not an available option", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Location.Contains("hiddenWhen", StringComparison.OrdinalIgnoreCase)
            && error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("must be between 0 and 10", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("at least two available choices", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Message.Contains("Confirmation default", StringComparison.OrdinalIgnoreCase));
    }
}
