using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class MenuTopologyIndentGuidePlannerTests
{
    [Fact]
    public void GuidesFollowPopulatedBranchesAndStopAtShallowerItems()
    {
        const string outline =
            "Settings\n"
            + "  Picture\n"
            + "    Expert Settings\n"
            + "      Brightness\n"
            + "\n"
            + "      Contrast\n"
            + "  Sound\n"
            + "Support\n"
            + "\n";

        var depths = MenuTopologyIndentGuidePlanner.CreateLineDepths(outline);

        Assert.Equal([0, 1, 2, 3, 3, 3, 1, 0, 0, 0], depths);
    }
}
