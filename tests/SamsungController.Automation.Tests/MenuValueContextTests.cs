using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class MenuValueContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedScopesAndExplicitSharedOverrideRoundTrip(bool json)
    {
        var original = new MenuDefinitionParser().Parse(Yaml);
        var definition = json
            ? new MenuDefinitionJsonSerializer().Parse(new MenuDefinitionJsonSerializer().Serialize(original))
            : new MenuDefinitionParser().Parse(new MenuDefinitionWriter().Serialize(original));
        Assert.Empty(new MenuDefinitionValidator().Validate(definition));
        Assert.Null(definition.Nodes["brightness"].ValueContext);
        Assert.Empty(definition.Nodes["shared"].ValueContext!);
        Assert.Equal(new[] { "external:depth", "setting:mode" }, MenuValueContext.Sources(definition, definition.Nodes["brightness"]));
        Assert.Equal(new[] { "external:depth" }, MenuValueContext.Sources(definition, definition.Nodes["mode"]));
        Assert.Empty(MenuValueContext.Sources(definition, definition.Nodes["shared"]));
    }

    [Theory]
    [InlineData("8-bit", "Movie", "25")]
    [InlineData("10-bit", "Movie", "40")]
    [InlineData("8-bit", "Game", "30")]
    [InlineData("10-bit", "Game", "50")]
    public void DefaultsMatchExternalAndMenuSettingsIndependentlyOfStorage(string depth, string mode, string expected)
    {
        var definition = new MenuDefinitionParser().Parse(Yaml);
        Assert.Equal(expected, MenuDefaultValueResolver.Resolve(definition, definition.Nodes["brightness"],
            new Dictionary<string, string> { ["depth"] = depth }, new Dictionary<string, string> { ["mode"] = mode }));
    }

    [Theory]
    [InlineData("valueContext: [external:depth, setting:mode]", "valueContext: [external:missing]", "does not exist")]
    [InlineData("valueContext: [external:depth, setting:mode]", "valueContext: [external:depth, depth]", "unique")]
    [InlineData("valueContext: [external:depth, setting:mode]", "valueContext: [setting:picture]", "not a supported")]
    [InlineData("label: Picture Mode", "label: Picture Mode\n            valueContext: [setting:brightness]", "cycle")]
    [InlineData("when: { setting:mode: Game }", "when: { setting:mode: Unknown }", "not valid")]
    [InlineData("when: { external:depth: 10-bit }", "when: { external:depth: 10-bit, depth: 10-bit }", "duplicated")]
    public void InvalidScopesAndDefaultsExplainTheError(string before, string after, string expected)
    {
        var definition = new MenuDefinitionParser().Parse(Yaml.Replace(before, after, StringComparison.Ordinal));
        Assert.Contains(new MenuDefinitionValidator().Validate(definition), error => error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StorageOnlyChangesDoNotInvalidatePhysicalVerification()
    {
        var original = new MenuDefinitionParser().Parse(Yaml);
        var changed = new MenuDefinitionParser().Parse(Yaml.Replace("valueContext: [external:depth, setting:mode]", "valueContext: []", StringComparison.Ordinal));
        Assert.Equal(MenuDefinitionVerificationPlanner.Create(original).Checks.Select(check => (check.Id, check.Fingerprint)),
            MenuDefinitionVerificationPlanner.Create(changed).Checks.Select(check => (check.Id, check.Fingerprint)));
    }

    private const string Yaml = """
        version: 1
        id: scoped-test
        name: Scoped test
        model: Test TV
        externalStates:
          - id: depth
            label: Bit depth
            defaultValue: 8-bit
            options: [8-bit, 10-bit]
        nodes:
          - id: normal-video
            label: Video
            children:
              - id: picture
                label: Picture
                valueContext: [external:depth, setting:mode]
                children:
                  - id: mode
                    label: Picture Mode
                    controlType: selection
                    defaultValue: Movie
                    options: [Movie, Game]
                  - id: brightness
                    label: Brightness
                    controlType: slider
                    defaultValue: 25
                    minimumValue: 0
                    maximumValue: 100
                    defaultValueWhen:
                      - when: { external:depth: 10-bit, setting:mode: Game }
                        value: 50
                      - when: { setting:mode: Game }
                        value: 30
                      - when: { external:depth: 10-bit }
                        value: 40
                  - id: shared
                    label: Shared switch
                    controlType: switch
                    defaultValue: off
                    valueContext: []
        """;
}
