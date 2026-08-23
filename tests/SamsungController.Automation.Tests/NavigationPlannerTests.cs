using SamsungController.Automation.Navigation;

namespace SamsungController.Automation.Tests;

public sealed class NavigationPlannerTests
{
    [Fact]
    public void PrefersVerifiedRouteOverShortDraftRoute()
    {
        var definition = CreateDefinition();

        var plan = new NavigationPlanner().Plan(
            definition,
            "start",
            "target",
            includeDraftTransitions: true);

        Assert.Equal(["start-to-middle", "middle-to-target"], plan.Transitions.Select(item => item.Id));
        Assert.True(plan.IsExecutable);
        Assert.Equal(2, plan.CommandCount);
    }

    [Fact]
    public void DraftRouteCanBePreviewedButIsNotExecutable()
    {
        var definition = CreateDefinition();

        var plan = new NavigationPlanner().Plan(
            definition,
            "target",
            "start",
            includeDraftTransitions: true);

        Assert.True(plan.UsesDraftTransitions);
        Assert.False(plan.IsExecutable);
    }

    [Fact]
    public void MissingVerifiedRouteIsReported()
    {
        var definition = CreateDefinition();

        var exception = Assert.Throws<NavigationPlanningException>(() =>
            new NavigationPlanner().Plan(definition, "target", "start"));

        Assert.Contains("No verified", exception.Message, StringComparison.Ordinal);
    }

    private static MenuDefinition CreateDefinition() => new(
        "test",
        "Test",
        "TV",
        new MenuDefinitionContext(),
        [
            new MenuNode("start", "Start"),
            new MenuNode("middle", "Middle"),
            new MenuNode("target", "Target")
        ],
        [
            new MenuTransition("draft-direct", "start", "target", [new MenuOperation("KEY_MENU")]),
            new MenuTransition("start-to-middle", "start", "middle", [new MenuOperation("KEY_DOWN")], true),
            new MenuTransition("middle-to-target", "middle", "target", [new MenuOperation("KEY_ENTER")], true),
            new MenuTransition("draft-return", "target", "start", [new MenuOperation("KEY_RETURN")])
        ],
        []);
}
