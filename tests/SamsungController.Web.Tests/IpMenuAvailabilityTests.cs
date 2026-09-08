using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Layout;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuAvailabilityTests
{
    [Theory]
    [InlineData(-32002)]
    [InlineData(-32003)]
    [InlineData(-32601)]
    public async Task RejectedQueryControlsAreHiddenButRefreshCanBringThemBack(int code)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == "localDimmingControl" ? MenuFixture.Reject(request, code) : null);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        var control = IpMenuCatalog.Get("localDimmingControl/localDimming");
        Assert.False(IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, control).Visible);
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertInputPresentAsync(control.Name, false);
        await renderer.AssertTextAsync("controls hidden because their queries were rejected");
        fixture.Override = null;
        await RefreshTvStateAsync(services);
        await renderer.AssertInputPresentAsync(control.Name, true);
        fixture.AssertOnlyWhiteBalanceReadWrites();
    }

    [Fact]
    public async Task UnmetPrerequisitesRemainVisibleGrayAndDisabledEvenWhenTheirGetterFails()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["gammaMode"] = "BT.1886";
        fixture.Values["autoMotionPlus"] = "Off";
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() is "gamma.HLGControl" or "AMP.blurReductionControl" ? MenuFixture.Reject(request, -32002) : null);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        foreach (var id in new[] { "gamma.HLGControl/gamma.HLG", "AMP.blurReductionControl/AMP.blurReduction" })
        {
            var control = IpMenuCatalog.Get(id);
            Assert.True(IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, control).Conditional);
            await renderer.AssertInputPresentAsync(control.Name + " value", true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => fixture.Service.StageMenuValue(id, "0")));
        }
        await renderer.AssertClassPresentAsync("unavailable");
        await renderer.AssertTextAsync("Requires Gamma mode: HLG");
        await renderer.AssertTextAsync("Requires Picture Clarity / Auto Motion Plus: Custom");
        fixture.Override = null;
        fixture.Values["gammaMode"] = "HLG";
        fixture.Values["autoMotionPlus"] = "Custom";
        await RefreshTvStateAsync(services);
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("gamma.HLGControl/gamma.HLG")));
        fixture.AssertOnlyWhiteBalanceReadWrites();
    }

    [Fact]
    public async Task ApplyingCustomMotionEnablesJudderWithoutAnExtraManualRefresh()
    {
        using var fixture = await MenuFixture.CreateAsync();
        fixture.Values["autoMotionPlus"] = "Off";
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        var judder = IpMenuCatalog.Get("AMP.judderReductionControl/AMP.judderReduction");
        Assert.True(IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, judder).Conditional);
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.SelectAsync("Picture Clarity / Auto Motion Plus", "Custom");
        await renderer.ClickAsync("Apply 1 pending");
        Assert.Null(fixture.Service.MenuControlDisabledReason(judder));
        Assert.False(IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, judder).Conditional);
        Assert.Equal("autoMotionPlusControl", Assert.Single(fixture.Writes)["method"]!.ToString());
    }

    [Fact]
    public async Task ARejectedSetterDoesNotHideTheControlOrInvalidateItsQueriedValue()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == "contrastControl" ? MenuFixture.Reject(request, -32002) : null);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.True(IpMenuAvailability.For(fixture.Service.GetSnapshot().Menu, IpMenuCatalog.Get("contrastControl/contrast")).Visible);
        Assert.Equal(45, fixture.Value("contrastControl/contrast")!.GetValue<int>());
    }

    private static async Task RefreshTvStateAsync(ServiceProvider services)
    {
        await using var header = new IpRemotePageTests.IpPageRenderer(services, typeof(MainLayout));
        await header.StartAsync();
        await header.ClickAsync("Refresh TV state");
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<NavigationManager>(new IpMenuTests.MenuNavigation())
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
}
