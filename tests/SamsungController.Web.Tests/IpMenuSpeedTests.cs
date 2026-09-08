using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuSpeedTests
{
    [Theory]
    [InlineData("white20", true)]
    [InlineData("white20", false)]
    [InlineData("color", true)]
    [InlineData("color", false)]
    public async Task SeparateEditsRetainRowAndOnlySelectWhenChangingRows(string section, bool preQuery)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        await fixture.Service.SaveMenuPreferencesAsync(false, preQuery);
        var grid = IpMenuGrids.ForSection(section)!;
        var first = grid.Row(grid.Values[0]).ToArray();
        for (var i = 0; i < 3; i++)
        {
            fixture.Service.StageMenuValue(first[i].Id, "11");
            var start = fixture.Display.Requests.Count;
            await fixture.Service.ApplyMenuAsync();
            Assert.Equal(grid.Values[0], fixture.Values[grid.SelectorField]!.ToString());
            if (i > 0 && !preQuery) Assert.Equal(first[i].Method, fixture.Display.Requests[start]["method"]!.ToString());
        }
        fixture.Service.StageMenuValue(grid.Row(grid.Values[1]).First().Id, "12");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(new[] { grid.Values[0], grid.Values[1] }, fixture.Writes.Where(write => IsWrite(write, grid.SelectorMethod))
            .Select(write => write["params"]![grid.SelectorField]!.ToString()));
        Assert.Equal("Retained", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
        Assert.Equal(grid.Values[1], fixture.Service.GetSnapshot().Menu.SelectorSession!.LastConfirmed);
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Equal("Retained", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings); // Disk journal is not a live selector/value cache.
    }

    [Theory]
    [InlineData("white20", true, 36)]
    [InlineData("white20", false, 21)]
    [InlineData("color", true, 36)]
    [InlineData("color", false, 21)]
    public async Task RgbRequestBudgetWithOptionalPreflight(string section, bool preQuery, int count)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        await fixture.Service.SaveMenuPreferencesAsync(false, preQuery);
        var grid = IpMenuGrids.ForSection(section)!;
        foreach (var control in grid.Row(grid.Values[0])) fixture.Service.StageMenuValue(control.Id, "11");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Single(fixture.Writes, request => IsWrite(request, grid.SelectorMethod));
        Assert.Empty(fixture.Display.Batches);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal(preQuery, update.QueryBeforeChange);
        Assert.True(update.RgbGroups.Single().Verified);
        foreach (var control in grid.Row(grid.Values[0]))
        {
            var writeIndex = fixture.Display.Requests.FindIndex(request => IsWrite(request, control.Method));
            Assert.Contains(fixture.Display.Requests.Skip(writeIndex + 1), request => request["method"]!.ToString() == control.Method && !IsWrite(request, control.Method));
            Assert.Equal(11, fixture.Service.GetSnapshot().Menu.Value(control)!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("contrastControl/contrast", "44")]
    [InlineData("WB2PointControl/B-Gain", "-2")]
    public async Task FastOrdinaryAndTwoPointEditsSendFirstButStillReadBack(string id, string target)
    {
        using var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(false);
        await fixture.Service.RefreshMenuSectionAsync(IpMenuCatalog.Get(id).Section);
        await fixture.Service.SaveMenuPreferencesAsync(false, false);
        fixture.Service.StageMenuValue(id, target);
        fixture.Display.Requests.Clear();
        var control = IpMenuCatalog.Get(id);
        await fixture.Service.ApplyMenuAsync();
        Assert.True(IsWrite(fixture.Display.Requests[0], control.Method));
        Assert.Equal(control.Method == "WB2PointControl" ? 4 : 3, fixture.Display.Requests.Count);
        Assert.Equal(target, fixture.Service.GetSnapshot().Menu.Value(control)!.ToString());
        Assert.Contains(fixture.Display.Methods, method => method == "getTVStates");
        if (control.Method == "WB2PointControl") Assert.Equal(7, fixture.Display.Requests[0]["params"]!.AsObject().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FastModeDoesNotTurnSetterAcknowledgementsIntoVerifiedValues(bool cancel)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        await fixture.Service.SaveMenuPreferencesAsync(false, false);
        var row = IpMenuGrids.ForSection("white20")!.Row("5%").ToArray();
        foreach (var control in row) fixture.Service.StageMenuValue(control.Id, "11");
        fixture.Override = (request, cancellation) =>
        {
            if (!IsWrite(request, row[0].Method)) return Task.FromResult<HttpResponseMessage?>(null);
            if (cancel) { fixture.Service.Cancel(); cancellation.ThrowIfCancellationRequested(); }
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject { [row[0].Field] = 11 }));
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Service.ApplyMenuAsync);
        Assert.Single(fixture.Writes, request => row.Any(control => IsWrite(request, control.Method)));
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.QueryBeforeChange);
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.QueryBeforeChange);
    }

    [Fact]
    public async Task FastModeNeverInventsMissingCachedRgbPeers()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("color");
        fixture.GridValues["color/Red"].Remove("colorSpace.Green");
        await fixture.Service.RefreshMenuGridAsync("color");
        await fixture.Service.SaveMenuPreferencesAsync(false, false);
        fixture.Service.StageMenuValue("colorSpace.RedControl/colorSpace.Red/Red", "11");
        fixture.Display.Requests.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(fixture.Display.Requests);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task QueryPreferenceAndApplyRadiosPersistWithoutSendingCommands()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        fixture.Display.Requests.Clear(); // Initial Expert-tab read is separate from changing local preferences.
        await renderer.AssertClassPresentAsync("direct-menu-toolbar");
        await renderer.AssertRadioAsync("Wait for Apply", true);
        await renderer.AssertRadioAsync("Apply settings immediately", false);
        await renderer.AssertCheckboxAsync("Query before changes", true);
        await renderer.SetCheckboxAsync("Query before changes", false);
        await renderer.ChangeAsync("Apply settings immediately", "immediate", "onchange");
        await renderer.AssertRadioAsync("Wait for Apply", false);
        await renderer.AssertRadioAsync("Apply settings immediately", true);
        await renderer.AssertTextAsync("Cached");
        await renderer.AssertTextAsync("2pt WB");
        await renderer.AssertTextAsync("20pt WB");
        await renderer.AssertElementAttributeAsync("button", "Expert settings", "aria-pressed", "true");
        await renderer.AssertElementAttributeAsync("button", "20-point white balance", "title", "20-point white balance");
        Assert.Empty(fixture.Display.Requests);
        await fixture.RestartAsync();
        Assert.True(fixture.Service.GetSnapshot().Menu.Preferences.ApplyImmediately);
        Assert.False(fixture.Service.GetSnapshot().Menu.Preferences.QueryBeforeChange);
        await fixture.Service.SaveMenuPreferencesAsync(false);
        Assert.False(fixture.Service.GetSnapshot().Menu.Preferences.QueryBeforeChange);
    }

    [Fact]
    public async Task StartupPublishesOnlyContextCheckedGroupsAndHasFewerGlobalQueries()
    {
        using var fixture = await MenuFixture.CreateAsync();
        foreach (var grid in IpMenuGrids.All)
        {
            fixture.Values[grid.ModeField] = grid.RequiredMode;
            fixture.Values[grid.SelectorField] = grid.Values[0];
            foreach (var value in grid.Values) fixture.GridValues[grid.Section + "/" + value] = new JsonObject(grid.Fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(10))));
        }
        var firstRows = 0;
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "WB20P.RedControl" && ++firstRows <= 4)
                Assert.Empty(fixture.Service.GetSnapshot().Menu.IndexedReadings);
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await fixture.Service.ConnectMenuAsync();
        Assert.Equal(78, fixture.Service.GetSnapshot().Menu.IndexedReadings.Count);
        Assert.Equal(17, fixture.Display.Methods.Count(method => method == "getTVStates"));
        Assert.All(fixture.Writes, request => Assert.Contains(IpMenuGrids.All, grid => grid.SelectorMethod == request["method"]!.ToString()));
        Assert.Empty(fixture.Display.Batches);
    }

    private static bool IsWrite(JsonObject request, string method) => request["method"]!.ToString() == method && request["params"]!.AsObject().Count > 1;

    [Fact]
    public async Task ToolbarRemainsAttachedAcrossExpertLayoutAndTabChangesUntilPageDisposal()
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        await fixture.Service.RefreshMenuSectionAsync("expert");
        var javascript = new ToolbarJavaScript();
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(javascript).BuildServiceProvider();
        await using (var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu)))
        {
            await renderer.StartAsync();
            await renderer.ClickAsync("20-point white balance");
            await renderer.ClickAsync("Expert settings");
            Assert.Single(javascript.Calls, call => call == "samsungMenuToolbar.attach");
            Assert.Equal(2, javascript.Calls.Count(call => call == "samsungMenuToolbar.scrollToContent"));
            Assert.DoesNotContain("samsungMenuToolbar.detach", javascript.Calls);
        }
        Assert.Single(javascript.Calls, call => call == "samsungMenuToolbar.detach");
    }

    private sealed class ToolbarJavaScript : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        { Calls.Add(identifier); return ValueTask.FromResult(default(TValue)!); }
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task FastImmediateEditsStillMergeQueuedColorsAndKeepTheLastRow(string section)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync(section);
        await fixture.Service.SaveMenuPreferencesAsync(true, false);
        var grid = IpMenuGrids.ForSection(section)!;
        var row = grid.Row(grid.Values[0]).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Override = async (request, cancellation) =>
        {
            if (IsWrite(request, row[0].Method)) { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
            return null;
        };
        var red = fixture.Service.QueueMenuValueAsync(row[0].Id, "11");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var green = fixture.Service.QueueMenuValueAsync(row[1].Id, "11");
            var latestGreen = fixture.Service.QueueMenuValueAsync(row[1].Id, "13");
            var blue = fixture.Service.QueueMenuValueAsync(row[2].Id, "14");
            Assert.Same(green, latestGreen);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveMenuPreferencesAsync(true, true));
            release.TrySetResult();
            await Task.WhenAll(red, green, blue).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 11, 13, 14 }, grid.Fields.Select(field => fixture.GridValues[section + "/" + grid.Values[0]][field]!.GetValue<int>()));
            Assert.Single(fixture.Writes, request => IsWrite(request, grid.SelectorMethod));
            Assert.Equal(21, fixture.Display.Requests.Count);
            Assert.False(fixture.Service.GetSnapshot().Menu.Update!.QueryBeforeChange);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectorDriftIsRecheckedBeforeWritingUnlessTheUserDisablesPreflight(bool preQuery)
    {
        using var fixture = await IpMenuRgbGroupTests.ReadyAsync("white20");
        await fixture.Service.SaveMenuPreferencesAsync(false, preQuery);
        const string red = "WB20P.RedControl/WB20P.Red/5%";
        fixture.Service.StageMenuValue(red, "11");
        await fixture.Service.ApplyMenuAsync();
        fixture.Values["WB20P.Interval"] = "10%"; // Simulate a selector change; isolation between real clients is not assumed.
        fixture.Service.StageMenuValue(red, "12");
        fixture.Display.Requests.Clear();
        if (preQuery)
        {
            await fixture.Service.ApplyMenuAsync();
            Assert.Single(fixture.Writes, request => IsWrite(request, "WB20P.IntervalControl"));
            Assert.Equal(12, fixture.GridValues["white20/5%"]["WB20P.Red"]!.GetValue<int>());
            Assert.Equal(10, fixture.GridValues["white20/10%"]["WB20P.Red"]!.GetValue<int>());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
            Assert.True(IsWrite(fixture.Display.Requests[0], "WB20P.RedControl"));
            Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
            Assert.Single(fixture.Writes); // A post-write mismatch stops without a blind retry/restore.
        }
    }
}
