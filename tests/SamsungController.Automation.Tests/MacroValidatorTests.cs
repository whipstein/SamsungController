using SamsungController.Automation.Macros;

namespace SamsungController.Automation.Tests;

public sealed class MacroValidatorTests
{
    [Fact]
    public void ReportsMissingCallsInvalidValuesAndEmptyMacros()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition("Empty", []),
            new MacroDefinition(
                "Broken",
                [
                    new KeyStep("KEY_UP", Repeat: 0),
                    new DelayStep(TimeSpan.Zero),
                    new CallMacroStep("Missing")
                ])
        ]);

        var errors = new MacroValidator().Validate(catalog);

        Assert.Contains(errors, error => error.Message.Contains("no steps", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Repeat", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Delay", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void DetectsNestedMacroCallCycles()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition("First", [new CallMacroStep("Second")]),
            new MacroDefinition("Second", [new CallMacroStep("Third")]),
            new MacroDefinition("Third", [new CallMacroStep("First")])
        ]);

        var errors = new MacroValidator().Validate(catalog);

        var cycle = Assert.Single(errors, error =>
            error.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("First -> Second -> Third -> First", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMacrosWhoseExpandedPlanIsUnreasonablyLarge()
    {
        var catalog = new MacroCatalog(
        [
            new MacroDefinition(
                "Large",
                [new KeyStep("KEY_DOWN", Repeat: MacroValidator.MaximumRepeat, Delay: TimeSpan.FromMilliseconds(1))]),
            new MacroDefinition(
                "TooLarge",
                [new CallMacroStep("Large", Repeat: 6)])
        ]);

        var errors = new MacroValidator().Validate(catalog);

        Assert.Contains(errors, error =>
            error.MacroName == "TooLarge"
            && error.Message.Contains("10,000", StringComparison.Ordinal));
    }
}
