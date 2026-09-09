using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuGridResetTests
{
    private const string Contrast = "contrastControl/contrast";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("white20", 0, 60)]
    [InlineData("color", 50, 18)]
    public async Task StageResetReplacesOnlyThisGridWithExplicitNominalTargets(string section, int nominal, int count)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        fixture.Service.StageMenuValue(Contrast, "40");
        fixture.Service.StageMenuValue(grid.Row(grid.Values[0]).First().Id, "20");
        Assert.Null(fixture.Service.MenuGridResetDisabledReason(section));
        Assert.Equal(count, await fixture.Service.ResetMenuGridAsync(section));
        Assert.Empty(fixture.Display.Requests);
        var pending = fixture.Service.GetSnapshot().Menu.Pending;
        Assert.Equal(count + 1, pending.Count);
        Assert.Equal(40, pending[Contrast].Target.GetValue<int>());
        Assert.All(grid.Values.SelectMany(grid.Row), control =>
        {
            Assert.Equal(nominal, pending[control.Id].Target.GetValue<int>());
            Assert.Equal(10, pending[control.Id].Original.GetValue<int>());
        });
        fixture.Service.DiscardMenuChanges();
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("white20", 0, 60, true)]
    [InlineData("white20", 0, 60, false)]
    [InlineData("color", 50, 18, true)]
    [InlineData("color", 50, 18, false)]
    public async Task ImmediateResetUsesVerifiedWritesAndNeverAppliesOtherPendingSettings(string section, int nominal, int count, bool queryFirst)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        await fixture.Service.SaveMenuPreferencesAsync(true, queryFirst);
        fixture.Service.StageMenuValue(Contrast, "40");
        Assert.Equal(count, await fixture.Service.ResetMenuGridAsync(section));
        Assert.Equal(Contrast, Assert.Single(fixture.Service.GetSnapshot().Menu.Pending).Key);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(grid.RequiredMode, fixture.Values[grid.ModeField]!.ToString());
        foreach (var value in grid.Values)
            foreach (var field in grid.Fields)
                Assert.Equal(nominal, fixture.GridValues[section + "/" + value][field]!.GetValue<int>());
        Assert.All(fixture.Writes, request => Assert.Contains(request["method"]!.ToString(), grid.Fields.Select(field => field + "Control").Append(grid.SelectorMethod)));
        Assert.Empty(fixture.Display.Batches);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal("Completed", update.Status);
        Assert.Equal(count, update.Steps.Count);
        Assert.Equal(grid.Values.Count, update.RgbGroups.Count);
        Assert.All(update.RgbGroups, group => Assert.True(group.Verified));
        Assert.Equal(grid.Values.Last(), fixture.Values[grid.SelectorField]!.ToString());
    }

    [Theory]
    [InlineData("white20", false)]
    [InlineData("white20", true)]
    [InlineData("color", false)]
    [InlineData("color", true)]
    public async Task AlreadyNominalRemovesStaleGridEditsWithoutSendingAnything(string section, bool immediate)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        foreach (var value in grid.Values)
            foreach (var field in grid.Fields) fixture.GridValues[section + "/" + value][field] = grid.NominalValue;
        await fixture.Service.RefreshMenuGridAsync(section);
        await fixture.Service.SaveMenuPreferencesAsync(immediate);
        fixture.Service.StageMenuValue(grid.Row(grid.Values[0]).First().Id, "20");
        fixture.Service.StageMenuValue(Contrast, "40");
        fixture.Display.Requests.Clear();
        Assert.Equal(0, await fixture.Service.ResetMenuGridAsync(section));
        Assert.Equal(Contrast, Assert.Single(fixture.Service.GetSnapshot().Menu.Pending).Key);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("white20", "missing")]
    [InlineData("color", "missing")]
    [InlineData("white20", "mode")]
    [InlineData("color", "mode")]
    [InlineData("white20", "pending mode")]
    [InlineData("color", "pending mode")]
    public async Task UnavailableGridRejectsAllTargetsWithoutChangingPendingEdits(string section, string problem)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        if (problem == "pending mode")
            fixture.Service.StageMenuValue(grid.ModeMethod + "/" + grid.ModeField, section == "white20" ? "Off" : "Auto");
        else
        {
            if (problem == "mode") fixture.Values[grid.ModeField] = section == "white20" ? "Off" : "Auto";
            else fixture.GridValues[section + "/" + grid.Values.Last()][grid.Fields.Last()] = null;
            await fixture.Service.RefreshMenuGridAsync(section);
            fixture.Service.StageMenuValue(Contrast, "40");
        }
        var before = fixture.Service.GetSnapshot().Menu.Pending;
        fixture.Display.Requests.Clear();
        Assert.NotNull(fixture.Service.MenuGridResetDisabledReason(section));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ResetMenuGridAsync(section));
        Assert.Equal(before, fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Empty(fixture.Display.Requests);
    }

    [Theory]
    [InlineData("white20", false)]
    [InlineData("white20", true)]
    [InlineData("color", false)]
    [InlineData("color", true)]
    public async Task ConfirmationAndCancelSendNothingAndRespectApplyMode(string section, bool immediate)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        await fixture.Service.SaveMenuPreferencesAsync(immediate, false);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.AssertTextAbsentAsync("Reset all");
        await page.ClickAsync(IpMenuCatalog.Sections.Single(item => item.Id == section).Name);
        fixture.Display.Requests.Clear();
        await page.ClickAsync("Reset all");
        await page.AssertTextAsync($"All {grid.Values.Count * 3} RGB values");
        Assert.Empty(fixture.Display.Requests);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        await page.ClickAsync("Cancel reset");
        await page.AssertTextAbsentAsync("RGB values will target");
        Assert.Empty(fixture.Display.Requests);
        await page.ClickAsync("Reset all");
        await page.ClickAsync(immediate ? "Reset now" : "Stage reset");
        await page.AssertTextAsync(immediate ? "confirmed by readback" : "Select Apply to send them");
        if (immediate) Assert.NotEmpty(fixture.Writes);
        else { Assert.Empty(fixture.Display.Requests); Assert.Equal(grid.Values.Count * 3, fixture.Service.GetSnapshot().Menu.Pending.Count); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrFailureStopsTheResetAndClearsOnlyItsUnsentTargets(bool cancel)
    {
        using var fixture = await ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        fixture.Service.StageMenuValue(Contrast, "40");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (request, token) =>
        {
            if (request["method"]!.ToString() == row[1].Method && request["params"]!.AsObject().Count > 1)
            {
                entered.TrySetResult();
                if (cancel) await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
                return MenuFixture.Reject(request, -32002);
            }
            return null;
        };
        var reset = fixture.Service.ResetMenuGridAsync("white20");
        await entered.Task.WaitAsync(Timeout);
        if (cancel) fixture.Service.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => reset.WaitAsync(Timeout));
        Assert.Equal(Contrast, Assert.Single(fixture.Service.GetSnapshot().Menu.Pending).Key);
        Assert.Equal(0, fixture.GridValues["white20/5%"][grid.Fields[0]]!.GetValue<int>());
        Assert.Equal(10, fixture.GridValues["white20/5%"][grid.Fields[2]]!.GetValue<int>());
        Assert.DoesNotContain(fixture.Writes, request => request["method"]!.ToString() == row[2].Method);
        Assert.Equal("Stopped", fixture.Service.GetSnapshot().Menu.Update!.Status);
    }

    private static async Task<MenuFixture> ReadyAsync(string section)
    {
        var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        fixture.Display.Requests.Clear();
        return fixture;
    }
}
