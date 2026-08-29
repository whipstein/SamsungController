using SamsungController.Automation.Macros;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MacroParserTests
{
    [Fact]
    public void ParsesDirectAndDetailedDefinitionsWithVariables()
    {
        const string yaml =
            """
            version: 1
            variables:
              direction: KEY_DOWN
              repeats: 4
              navigationDelay: 150ms
            macros:
              BackToVideo:
                description: Close open menus
                confirmBeforeRun: true
                steps:
                  - key: KEY_RETURN
                    repeat: 3
              TestNavigation:
                - call: BackToVideo
                - key: ${direction}
                  action: Press
                  repeat: ${repeats}
                  delay: ${navigationDelay}
            """;

        var catalog = new MacroParser().Parse(yaml);

        Assert.Equal(2, catalog.Macros.Count);
        var backToVideo = catalog.GetRequiredMacro("backtovideo");
        Assert.Equal("Close open menus", backToVideo.Description);
        Assert.True(backToVideo.ConfirmBeforeRun);
        Assert.False(catalog.GetRequiredMacro("TestNavigation").ConfirmBeforeRun);
        var steps = catalog.GetRequiredMacro("TestNavigation").Steps;
        Assert.Equal(new CallMacroStep("BackToVideo"), steps[0]);
        Assert.Equal(
            new KeyStep("KEY_DOWN", RemoteKeyAction.Press, 4, TimeSpan.FromMilliseconds(150)),
            steps[1]);
        Assert.Equal("150ms", catalog.Variables["navigationDelay"]);
    }

    [Fact]
    public void CallerCanOverrideVariablesBeforeStepsAreTyped()
    {
        const string yaml =
            """
            variables:
              repeats: 3
            macros:
              Test:
                - key: KEY_UP
                  repeat: ${repeats}
            """;

        var catalog = new MacroParser().Parse(
            yaml,
            new Dictionary<string, string> { ["repeats"] = "7" });

        var key = Assert.IsType<KeyStep>(Assert.Single(catalog.GetRequiredMacro("Test").Steps));
        Assert.Equal(7, key.Repeat);
    }

    [Fact]
    public void ParsesVerifiedMenuDestinationStep()
    {
        const string yaml =
            """
            macros:
              Picture:
                steps:
                  - menu: picture-brightness
            """;

        var catalog = new MacroParser().Parse(yaml);

        Assert.Equal(
            new MenuStep("picture-brightness"),
            Assert.Single(catalog.GetRequiredMacro("Picture").Steps));
    }

    [Fact]
    public void ParsesDeclaredStartingMenuState()
    {
        const string yaml =
            """
            macros:
              Picture:
                start: normal-video
                steps:
                  - key: KEY_MENU
            """;

        var macro = new MacroParser().Parse(yaml).GetRequiredMacro("Picture");

        Assert.Equal("normal-video", macro.StartingNodeId);
    }

    [Fact]
    public void UndefinedVariableProducesActionableParseError()
    {
        const string yaml =
            """
            macros:
              Test:
                - key: ${missingKey}
            """;

        var exception = Assert.Throws<MacroParseException>(() => new MacroParser().Parse(yaml));

        Assert.Contains("missingKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not defined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownStepFieldIsRejectedInsteadOfIgnored()
    {
        const string yaml =
            """
            macros:
              Test:
                - key: KEY_UP
                  typoDelay: 100ms
            """;

        var exception = Assert.Throws<MacroParseException>(() => new MacroParser().Parse(yaml));

        Assert.Contains("typoDelay", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VariableCyclesAreRejected()
    {
        const string yaml =
            """
            variables:
              first: ${second}
              second: ${first}
            macros:
              Test:
                - key: KEY_UP
            """;

        var exception = Assert.Throws<MacroParseException>(() => new MacroParser().Parse(yaml));

        Assert.Contains("first -> second -> first", exception.Message, StringComparison.Ordinal);
    }
}
