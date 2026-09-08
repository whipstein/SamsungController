using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuNumberEntryTests
{
    [Theory]
    [InlineData("white2", false)]
    [InlineData("white2", true)]
    [InlineData("white20", false)]
    [InlineData("white20", true)]
    public async Task RawTextPreservesLeadingMinusAndCommitsNegativeValue(string section, bool immediate)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        foreach (var control in IpMenuCatalog.ForSection("white2").Where(control => control.Parameter.Kind == IpRemoteParameterKind.Integer))
            fixture.Values[control.Field] = 10;
        await fixture.Service.RefreshMenuSectionAsync("white2");
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.SaveMenuPreferencesAsync(immediate);
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync(IpMenuCatalog.Sections.Single(item => item.Id == section).Name);
        fixture.Display.Requests.Clear();
        var grid = IpMenuGrids.ForSection("white20")!;
        var target = section == "white20" ? grid.Row(grid.Values[0]).First()
            : IpMenuCatalog.Get("WB2PointControl/R-Gain");
        var label = target.Name + " value";

        // A native number input sanitizes a lone minus before oninput; text must
        // reach the component unchanged, including on mobile's signed keyboard.
        await renderer.AssertElementAttributeAsync("input", label, "type", "text");
        await renderer.AssertElementAttributeAsync("input", label, "inputmode", "text");
        await renderer.AssertInputValueAsync(label, "10");
        foreach (var draft in new[] { "", "-", "-5", "-50" })
        {
            await renderer.ChangeAsync(label, draft);
            await renderer.AssertInputValueAsync(label, draft);
            await renderer.AssertInputValueAsync(target.Name + " slider", draft is "" or "-" ? "10" : draft);
            Assert.Empty(fixture.Writes);
        }

        await renderer.ChangeAsync(label, "-50", "onchange").WaitAsync(TimeSpan.FromSeconds(10));
        if (!immediate)
        {
            Assert.Empty(fixture.Writes);
            Assert.Equal(-50, fixture.Service.GetSnapshot().Menu.Pending[target.Id].Target.GetValue<int>());
            await renderer.ClickAsync("Apply 1 pending");
        }
        Assert.Equal(-50, fixture.Value(target.Id)!.GetValue<int>());
        await renderer.AssertInputValueAsync(label, "-50");
        Assert.Contains(fixture.Writes, request => request["method"]!.ToString() == target.Method
            && request["params"]![target.Field]!.GetValue<int>() == -50);
    }

    [Theory]
    [InlineData(false, "-")]
    [InlineData(true, "-")]
    [InlineData(false, "-51")]
    [InlineData(true, "-51")]
    [InlineData(false, "-1.5")]
    [InlineData(true, "-1.5")]
    [InlineData(false, " -5 ")]
    [InlineData(true, " -5 ")]
    public async Task InvalidSignedDraftStaysEditableAndSendsNothing(bool immediate, string draft)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.SaveMenuPreferencesAsync(immediate);
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("20-point white balance");
        fixture.Display.Requests.Clear();
        var grid = IpMenuGrids.ForSection("white20")!;
        var target = grid.Row(grid.Values[0]).First();
        await renderer.ChangeAsync(target.Name + " value", draft);
        await renderer.ChangeAsync(target.Name + " value", draft, "onchange");
        await renderer.AssertInputValueAsync(target.Name + " value", draft);
        await renderer.AssertInputValueAsync(target.Name + " slider", "10");
        await renderer.AssertTextAsync("must be an integer from -50 to 50");
        await renderer.AssertElementDisabledAsync("input", target.Name + " value", false);
        Assert.Empty(fixture.Writes);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.Null(fixture.Service.GetSnapshot().Menu.NudgeQueue);
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
