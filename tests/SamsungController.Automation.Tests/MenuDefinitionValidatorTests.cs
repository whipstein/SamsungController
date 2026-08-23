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
}
