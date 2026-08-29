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

    [Fact]
    public void UsesOnlyRoutesVerifiedForTheActiveMenuConfiguration()
    {
        var definition = new MenuDefinition(
            "conditional",
            "Conditional",
            "TV",
            new MenuDefinitionContext(),
            [new MenuNode("start", "Start"), new MenuNode("target", "Target")],
            [
                new MenuTransition(
                    "standard-route",
                    "start",
                    "target",
                    [new MenuOperation("KEY_DOWN", Repeat: 2)],
                    true,
                    ConfigurationId: "standard"),
                new MenuTransition(
                    "game-route",
                    "start",
                    "target",
                    [new MenuOperation("KEY_DOWN", Repeat: 4)],
                    true,
                    ConfigurationId: "game")
            ],
            [],
            configurations:
            [
                new MenuConfiguration("standard", "Standard"),
                new MenuConfiguration("game", "Game Mode")
            ]);

        var standardPlan = new NavigationPlanner().Plan(
            definition.WithActiveConfiguration("standard"),
            "start",
            "target");
        var gamePlan = new NavigationPlanner().Plan(
            definition.WithActiveConfiguration("game"),
            "start",
            "target");

        Assert.Equal("standard-route", Assert.Single(standardPlan.Transitions).Id);
        Assert.Equal(2, standardPlan.CommandCount);
        Assert.Equal("game-route", Assert.Single(gamePlan.Transitions).Id);
        Assert.Equal(4, gamePlan.CommandCount);
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
