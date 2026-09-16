using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuRejectionTests
{
    [Theory]
    [InlineData("inputSourceControl/inputSource", -32002)]
    [InlineData("inputSourceControl/inputSource", -32601)]
    [InlineData("contrastControl/contrast", -32002)]
    [InlineData("contrastControl/contrast", -32601)]
    [InlineData("sharpnessControl/sharpness", -32602)]
    [InlineData("brightnessControl/brightness", -32001)]
    [InlineData("WB20PointModeControl/WB20PointMode", -32003)]
    [InlineData("colorSpaceControl/colorSpace", -32601)]
    public async Task ConfirmedRejectionRestoresTargetAndLeavesEveryOtherSectionUsable(string id, int code)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        var control = IpMenuCatalog.Get(id);
        var original = fixture.Value(id)!.ToString();
        var target = control.Parameter.Kind == SamsungController.Core.IpRemote.IpRemoteParameterKind.Integer
            ? (int.Parse(original) == control.Parameter.Maximum ? int.Parse(original) - 1 : int.Parse(original) + 1).ToString()
            : control.Parameter.Choices.First(choice => choice != original);
        var before = fixture.Service.GetSnapshot().Menu;
        fixture.Service.StageMenuValue(id, target);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == control.Method
            && request["params"]?[control.Field] is not null ? MenuFixture.Reject(request, code) : null);
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var after = fixture.Service.GetSnapshot().Menu;
        Assert.Equal(original, after.Value(control)!.ToString());
        Assert.Empty(after.Pending);
        Assert.False(after.Update!.NeedsReview);
        Assert.Contains("rejected", after.ActionWarning);
        Assert.Equal(before.SectionsRead.Count, after.SectionsRead.Count);
        Assert.Equal(before.ValuesRevision, after.ValuesRevision);
        Assert.Single(fixture.Writes); // No rollback or retry.
        Assert.Null(fixture.Service.MenuControlDisabledReason(control));
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("contrastControl/contrast")));
        fixture.Override = null;
        if (control.Field == "brightness") fixture.Override = (request, _) =>
        {
            if (request["params"]?["brightness"] is { } value)
                fixture.Display.VideoOverride = new System.Text.Json.Nodes.JsonObject { ["contrast"] = fixture.Display.Contrast,
                    ["color"] = fixture.Display.Color, ["sharpness"] = fixture.Display.Sharpness, ["brightness"] = value.DeepClone() };
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        fixture.Service.StageMenuValue(id, target);
        await fixture.Service.ApplyMenuAsync();
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Null(fixture.Service.GetSnapshot().Menu.ActionWarning);
        Assert.Equal(target, fixture.Service.GetSnapshot().Menu.Tv[control.Field]?.ToString()
            ?? fixture.Service.GetSnapshot().Menu.Video[control.Field]?.ToString()
            ?? fixture.Value(id)?.ToString());
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task RejectedRgbPreservesLoadedGridAndAllowsNextEdit(string section)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        var row = grid.Row(grid.Values[0]).ToArray();
        fixture.Service.StageMenuValue(row[0].Id, "11");
        fixture.Service.StageMenuValue(row[1].Id, "12");
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]?[row[0].Field] is not null
            ? MenuFixture.Reject(request, -32601) : null);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.False(menu.Update!.NeedsReview);
        Assert.Equal(row[1].Id, Assert.Single(menu.Pending).Key);
        Assert.True(menu.GridsRead.ContainsKey(section));
        Assert.Equal(10, menu.Value(row[0])!.GetValue<int>());
        Assert.All(row, control => Assert.Null(fixture.Service.MenuControlDisabledReason(control)));
        fixture.Override = null;
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(12, fixture.Value(row[1].Id)!.GetValue<int>());
    }

    [Fact]
    public async Task RejectedRecallSkipsTheUnchangedSettingWithoutRequiringReview()
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        var id = await fixture.Service.SaveMenuStateAsync("Baseline");
        fixture.Display.Contrast = 40;
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]?["contrast"] is not null
            ? MenuFixture.Reject(request, -32601) : null);
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal("Completed", fixture.Service.GetSnapshot().StateRecall!.Status);
        Assert.Contains("contrastControl/contrast", fixture.Service.GetSnapshot().StateRecall!.Skipped.Keys);
        Assert.False(fixture.Service.GetSnapshot().StateRecall!.NeedsReview);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Null(fixture.Service.RecallMenuStateDisabledReason(fixture.Service.GetSnapshot().SavedStates.Single()));
        fixture.Override = null;
        await fixture.Service.RecallMenuStateAsync(id, true);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedInputDropdownReturnsToOriginalAndWarningIsInPinnedToolbar(bool immediate)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync();
        await fixture.Service.SaveMenuPreferencesAsync(immediate, false);
        var control = IpMenuCatalog.Get("inputSourceControl/inputSource");
        var original = fixture.Display.Input;
        var target = control.Parameter.Choices.First(choice => choice != original);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["params"]?["inputSource"] is not null
            ? MenuFixture.Reject(request, -32002) : null);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var page = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await page.StartAsync();
        await page.ClickAsync("System");
        await page.SelectAsync(control.Name, target);
        if (!immediate) await page.ClickAsync("Apply 1 pending");
        await page.AssertElementAttributeAsync("select", control.Name, "value", original);
        await page.AssertAriaButtonPresentAsync("Verify settings", false);
        await page.AssertDisabledAsync("Picture", false);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<DirectMenu>()).ToHtmlString());
        Assert.True(html.IndexOf("direct-power-warning", StringComparison.Ordinal) < html.IndexOf("data-control-section", StringComparison.Ordinal));
        Assert.Contains("rejected", html);
        Assert.Equal(original, fixture.Display.Input);
    }
}
