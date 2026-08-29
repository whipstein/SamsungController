using SamsungController.Automation.Macros;
using SamsungController.Core.Protocol;

namespace SamsungController.Automation.Tests;

public sealed class MacroCatalogWriterTests
{
    [Fact]
    public void RoundTripsAllStepKindsAndVerificationMetadata()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "BackToVideo",
                [new KeyStep("KEY_RETURN", Repeat: 2, Delay: TimeSpan.FromMilliseconds(175))],
                "Return to video",
                verified: true,
                verificationPasses: 3),
            new MacroDefinition(
                "OpenSettings",
                [
                    new CallMacroStep("BackToVideo"),
                    new DelayStep(TimeSpan.FromMilliseconds(500)),
                    new KeyStep("KEY_MENU", RemoteKeyAction.Press),
                    new MenuStep("picture-brightness")
                ],
                "Open settings",
                verificationPasses: 2)
        ],
        new Dictionary<string, string> { ["legacyDelay"] = "150ms" });

        var yaml = new MacroCatalogWriter().Serialize(catalog);
        var reparsed = new MacroParser().Parse(yaml);

        var back = reparsed.GetRequiredMacro("BackToVideo");
        Assert.True(back.Verified);
        Assert.Equal(3, back.VerificationPasses);
        Assert.Equal(catalog.GetRequiredMacro("BackToVideo").Steps, back.Steps);

        var settings = reparsed.GetRequiredMacro("OpenSettings");
        Assert.False(settings.Verified);
        Assert.Equal(2, settings.VerificationPasses);
        Assert.Equal(catalog.GetRequiredMacro("OpenSettings").Steps, settings.Steps);
        Assert.Equal("150ms", reparsed.Variables["legacyDelay"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ParserRejectsOutOfRangeVerificationPasses(int passes)
    {
        var yaml = $$"""
            version: 1
            macros:
              Test:
                verificationPasses: {{passes}}
                steps:
                  - key: KEY_MENU
            """;

        var exception = Assert.Throws<MacroParseException>(() => new MacroParser().Parse(yaml));

        Assert.Contains("0 through 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriterRejectsOutOfRangeProgrammaticVerificationPasses()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "Test",
                [new KeyStep("KEY_MENU")],
                verificationPasses: 4)
        ]);

        var exception = Assert.Throws<MacroValidationException>(() =>
            new MacroCatalogWriter().Serialize(catalog));

        Assert.Contains("0 through 3", exception.Message, StringComparison.Ordinal);
    }
}
