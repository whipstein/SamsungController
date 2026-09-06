using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuDefaultValueTests
{
    [Theory]
    [InlineData("RGB", "8-bit", "HDMI 1", "25")]
    [InlineData("RGB", "10-bit", "HDMI 1", "40")]
    [InlineData("YCbCr422", "10-bit", "HDMI 1", "35")]
    [InlineData("YCbCr422", "8-bit", "HDMI 2", "25")]
    [InlineData("RGB", "10-bit", "HDMI 2", "45")]
    [InlineData("YCbCr444", "10-bit", "HDMI 2", "35")]
    public void DefaultsRequireAllConditionsAndUseFirstMatchingRule(string format, string depth, string port, string expected)
    {
        var definition = new MenuDefinitionParser().Parse(MenuYaml);
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["format"] = format,
            ["depth"] = depth,
            ["port"] = port
        };
        Assert.Equal(expected, MenuDefaultValueResolver.Resolve(definition, definition.Nodes["brightness"], states));
        Assert.Equal("25", definition.Nodes["brightness"].DefaultValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionalDefaultsRoundTripWithoutChangingTheFallback(bool json)
    {
        var original = new MenuDefinitionParser().Parse(MenuYaml);
        var definition = json
            ? new MenuDefinitionJsonSerializer().Parse(new MenuDefinitionJsonSerializer().Serialize(original))
            : new MenuDefinitionParser().Parse(new MenuDefinitionWriter().Serialize(original));
        Assert.Empty(new MenuDefinitionValidator().Validate(definition));
        Assert.Equal(3, definition.Nodes["brightness"].DefaultValueWhen!.Count);
        Assert.Equal("40", MenuDefaultValueResolver.Resolve(definition, definition.Nodes["brightness"],
            new Dictionary<string, string> { ["depth"] = "10-bit" }));
        Assert.Equal("25", MenuDefaultValueResolver.Resolve(definition, definition.Nodes["brightness"]));
    }

    [Theory]
    [InlineData("value: 45", "value: 101", "between")]
    [InlineData("port: HDMI 2", "missing: HDMI 2", "does not exist")]
    [InlineData("port: HDMI 2", "port: HDMI 3", "not an available option")]
    [InlineData("when: { depth: 10-bit }", "when: {}", "at least one")]
    public void InvalidRulesReportTheControlAndRule(string oldText, string newText, string errorText)
    {
        var definition = new MenuDefinitionParser().Parse(MenuYaml.Replace(oldText, newText, StringComparison.Ordinal));
        var errors = new MenuDefinitionValidator().Validate(definition);
        Assert.Contains(errors, error => error.Location.Contains("brightness", StringComparison.Ordinal)
            && error.Location.Contains("defaultValueWhen rule", StringComparison.Ordinal)
            && error.Message.Contains(errorText, StringComparison.Ordinal));
    }

    [Fact]
    public void EditingConditionalDefaultsInvalidatesOnlyRelatedControlEvidence()
    {
        var original = new MenuDefinitionParser().Parse(MenuYaml);
        var changed = new MenuDefinitionParser().Parse(MenuYaml.Replace("value: 40", "value: 41", StringComparison.Ordinal));
        var before = MenuDefinitionVerificationPlanner.Create(original).Checks.ToDictionary(check => check.Id);
        var after = MenuDefinitionVerificationPlanner.Create(changed).Checks;
        Assert.NotEqual(before["control:slider-behavior"].Fingerprint,
            after.Single(check => check.Id == "control:slider-behavior").Fingerprint);
        Assert.All(after.Where(check => check.Id != "control:slider-behavior"), check =>
            Assert.Equal(before[check.Id].Fingerprint, check.Fingerprint));
    }

    [Fact]
    public void ConditionalDefaultsOnSwitchAndSelectionAreValidatedAgainstTheirValues()
    {
        var definition = new MenuDefinitionParser().Parse(MenuYaml);
        foreach (var (type, defaultValue, value, options) in new[]
                 {
                     (MenuControlType.Switch, "off", "invalid", Array.Empty<string>()),
                     (MenuControlType.Selection, "Low", "Missing", new[] { "Low", "High" })
                 })
        {
            var node = new MenuNode("control", "Control", ControlType: type, DefaultValue: defaultValue,
                SelectionOptions: options, DefaultValueWhen: [new(new Dictionary<string, string> { ["depth"] = "10-bit" }, value)]);
            var variant = new MenuDefinition("test", "Test", "TV", new(), [node], [], [], externalStates: definition.ExternalStates.Values);
            Assert.Contains(new MenuDefinitionValidator().Validate(variant), error => error.Location.Contains("defaultValueWhen", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("8-bit", 1)]
    [InlineData("10-bit", 2)]
    public void GeneratedRoutesUseConditionalDefaultsForVisibility(string depth, int downCount)
    {
        var definition = new MenuDefinition("test", "Test", "TV", new(),
            [
                new("normal", "Normal"),
                new("settings", "Settings", "normal"),
                new("master", "Master", "settings", ControlType: MenuControlType.Switch, DefaultValue: "off",
                    DefaultValueWhen: [new(new Dictionary<string, string> { ["depth"] = "10-bit" }, "on")]),
                new("optional", "Optional", "settings", ControlType: MenuControlType.Action,
                    HiddenWhen: [new("master", "off")]),
                new("target", "Target", "settings", ControlType: MenuControlType.Action)
            ],
            [new("open", "normal", "settings", [new("KEY_MENU")])], [],
            externalStates: [new("depth", "Depth", depth, ["8-bit", "10-bit"])]);
        var route = TopologyRouteGenerator.Regenerate(definition).Transitions.Values.Single(route => route.ToNodeId == "target");
        Assert.Equal(downCount, route.Operations.Single(operation => operation.Key == "KEY_DOWN").Repeat);
    }

    private const string MenuYaml = """
        version: 1
        id: conditional-defaults
        name: Conditional defaults
        model: Test TV
        externalStates:
          - id: format
            label: Color format
            defaultValue: RGB
            options: [RGB, YCbCr422, YCbCr444]
          - id: depth
            label: Bit depth
            defaultValue: 8-bit
            options: [8-bit, 10-bit]
          - id: port
            label: HDMI input
            defaultValue: HDMI 1
            options: [HDMI 1, HDMI 2]
        nodes:
          - id: brightness
            label: Brightness
            controlType: slider
            defaultValue: 25
            minimumValue: 0
            maximumValue: 100
            defaultValueWhen:
              - when: { format: RGB, depth: 10-bit, port: HDMI 2 }
                value: 45
              - when: { format: RGB, depth: 10-bit }
                value: 40
              - when: { depth: 10-bit }
                value: 35
        """;
}
