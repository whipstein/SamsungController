using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Text.Json.Nodes;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuRgbGroupTests
{
    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task ThreeColorRequestBudget(string section)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        var row = grid.Row(grid.Values[0]).ToArray();
        for (var i = 0; i < row.Length; i++) fixture.Service.StageMenuValue(row[i].Id, (11 + i).ToString());
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(36, fixture.Display.Requests.Count);
        Assert.Equal(2, fixture.Display.Methods.Count(method => method == "getTVStates"));
        Assert.Equal(2, fixture.Display.Methods.Count(method => method == "getVideoStates"));
        Assert.Equal(3, RgbWrites(fixture, grid).Count());
        Assert.All(RgbWrites(fixture, grid), request => Assert.Equal(2, request["params"]!.AsObject().Count)); // AccessToken + one field, never a multi-field probe.
        Assert.Empty(fixture.Display.Batches);
        Assert.Equal(grid.Values[0], fixture.Values[grid.SelectorField]!.ToString());
        Assert.True(Assert.Single(fixture.Service.GetSnapshot().Menu.Update!.RgbGroups).Verified);
        Assert.Equal(new[] { 11, 12, 13 }, grid.Fields.Select(field => fixture.GridValues[section + "/" + grid.Values[0]][field]!.GetValue<int>()));
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task FullContextIsNotRepeatedBetweenColorWrites(string section)
    {
        using var fixture = await ReadyAsync(section);
        var grid = IpMenuGrids.ForSection(section)!;
        var row = grid.Row(grid.Values[0]).ToArray();
        foreach (var control in row) fixture.Service.StageMenuValue(control.Id, "11");
        await fixture.Service.ApplyMenuAsync();
        var requests = fixture.Display.Requests.ToArray();
        var first = Array.FindIndex(requests, request => IsWrite(request, row[0].Method));
        var last = Array.FindLastIndex(requests, request => IsWrite(request, row[2].Method));
        var between = requests.Skip(first + 1).Take(last - first - 1).ToArray();
        Assert.DoesNotContain(between, request => request["method"]!.ToString() is "getTVStates" or "getVideoStates" or "getDeviceInformation");
        // Channel readbacks and live mode/selector guards still occur between setters.
        Assert.Contains(between, request => request["method"]!.ToString() == row[0].Method && !IsWrite(request, row[0].Method));
        Assert.Contains(between, request => request["method"]!.ToString() == grid.ModeMethod);
        Assert.Contains(between, request => request["method"]!.ToString() == grid.SelectorMethod);
    }

    [Theory]
    [InlineData("acknowledged only")]
    [InlineData("rejected unchanged")]
    [InlineData("mode changed")]
    [InlineData("selector changed")]
    public async Task FailedColorStopsBeforeSendingTheNextColor(string failure)
    {
        using var fixture = await ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        foreach (var control in row) fixture.Service.StageMenuValue(control.Id, "11");
        fixture.Override = (request, _) =>
        {
            if (!IsWrite(request, row[0].Method)) return Task.FromResult<HttpResponseMessage?>(null);
            if (failure == "rejected unchanged") return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            if (failure == "mode changed") fixture.Values[grid.ModeField] = "Off";
            if (failure == "selector changed") fixture.Values[grid.SelectorField] = grid.Values[1];
            return Task.FromResult<HttpResponseMessage?>(failure == "acknowledged only" ? ContrastDisplay.Reply(request, new JsonObject { [row[0].Field] = 11 }) : null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Single(RgbWrites(fixture, grid));
        Assert.Single(fixture.Writes, request => IsWrite(request, grid.SelectorMethod)); // No blind restoration.
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal(failure != "rejected unchanged", update.NeedsReview);
        Assert.Equal(failure == "rejected unchanged" ? "Rejected unchanged" : "Uncertain", update.Steps[0].Status);
        Assert.Equal("Stopped", update.Status);
    }

    [Theory]
    [InlineData("untouched peer")]
    [InlineData("input")]
    [InlineData("picture mode")]
    [InlineData("other video setting")]
    public async Task FinalRowCheckDetectsCollateralChangesAndPreservesAllOriginalsAcrossRestart(string changed)
    {
        using var fixture = await ReadyAsync("color");
        var grid = IpMenuGrids.ForSection("color")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        fixture.Service.StageMenuValue(row[0].Id, "11");
        fixture.Service.StageMenuValue(grid.Row(grid.Values[1]).First().Id, "12");
        fixture.Override = (request, _) =>
        {
            if (IsWrite(request, row[0].Method))
            {
                if (changed == "untouched peer") fixture.GridValues["color/" + grid.Values[0]][row[1].Field] = 42;
                if (changed == "input") fixture.Display.Input = "HDMI2";
                if (changed == "picture mode") fixture.Display.Mode = "Standard";
                if (changed == "other video setting") fixture.Display.Contrast++;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Single(RgbWrites(fixture, grid));
        Assert.Single(fixture.Writes, request => IsWrite(request, grid.SelectorMethod));
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal("Applied", update.Steps[0].Status); // Per-channel acknowledgement is not whole-row verification.
        Assert.True(update.NeedsReview);
        var group = Assert.Single(update.RgbGroups);
        Assert.True(group.WriteAttempted);
        Assert.False(group.Verified);
        Assert.All(grid.Fields, field => Assert.Equal(10, group.Originals[field]!.GetValue<int>()));
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.True(JsonNode.DeepEquals(group.Originals, fixture.Service.GetSnapshot().Menu.Update!.RgbGroups.Single().Originals));

        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.AssertTextAsync("Saved RGB originals");
        await renderer.AssertTextAsync("Final row/context check incomplete");
        await renderer.AssertTextAsync("Original 10");
        await fixture.Service.CloseMenuUpdateReviewAsync();
        Assert.Equal(count, fixture.Display.Requests.Count); // Manual closure never retries or restores.
        var closed = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.False(closed.NeedsReview);
        Assert.True(closed.RgbGroups.Single().ReviewClosed);
        Assert.False(closed.RgbGroups.Single().Verified);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("out of range")]
    public async Task InvalidUntouchedPeerPreventsAllRgbWrites(string failure)
    {
        using var fixture = await ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        fixture.Service.StageMenuValue(row[0].Id, "11");
        fixture.GridValues["white20/" + grid.Values[0]][row[2].Field] = failure == "missing" ? null : JsonValue.Create(999);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Empty(RgbWrites(fixture, grid));
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopBeforeFinalContextCheckDoesNotRestoreOrForgetDeliveredRgb(bool duringPreflight)
    {
        using var fixture = await ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        var row = grid.Row(grid.Values[0]).ToArray();
        fixture.Service.StageMenuValue(row[0].Id, "11");
        var calls = 0;
        var requestCountWhenStopped = 0;
        fixture.Override = (request, cancellation) =>
        {
            if (request["method"]!.ToString() == "getTVStates" && ++calls == (duringPreflight ? 1 : 2))
            {
                requestCountWhenStopped = fixture.Display.Requests.Count;
                fixture.Service.Cancel();
                cancellation.ThrowIfCancellationRequested();
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(requestCountWhenStopped, fixture.Display.Requests.Count);
        Assert.Equal(duringPreflight ? 0 : 1, RgbWrites(fixture, grid).Count());
        Assert.Equal(!duringPreflight, fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        await fixture.RestartAsync();
        Assert.Equal(requestCountWhenStopped, fixture.Display.Requests.Count);
        Assert.Equal(!duringPreflight, fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task AlreadyAtTargetUsesFreshReadbacksWithoutSendingRgb()
    {
        using var fixture = await ReadyAsync("white20");
        var grid = IpMenuGrids.ForSection("white20")!;
        foreach (var control in grid.Row(grid.Values[0]))
        {
            fixture.Service.StageMenuValue(control.Id, "11");
            fixture.GridValues["white20/" + grid.Values[0]][control.Field] = 11;
        }
        await fixture.Service.ApplyMenuAsync();
        Assert.Empty(RgbWrites(fixture, grid));
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.All(update.Steps, step => Assert.Equal("Already at target", step.Status));
        Assert.True(update.RgbGroups.Single().Verified);
        Assert.False(update.RgbGroups.Single().WriteAttempted);
    }

    private static bool IsWrite(JsonObject request, string method) => request["method"]!.ToString() == method && request["params"]!.AsObject().Count > 1;
    private static IEnumerable<JsonObject> RgbWrites(MenuFixture fixture, IpMenuGrid grid) => fixture.Writes.Where(request => grid.Fields.Any(field => IsWrite(request, field + "Control")));

    internal static async Task<MenuFixture> ReadyAsync(string section)
    {
        var fixture = await MenuFixture.CreateAsync();
        var grid = IpMenuGrids.ForSection(section)!;
        fixture.Values[grid.ModeField] = grid.RequiredMode;
        fixture.Values[grid.SelectorField] = grid.Values.Last();
        foreach (var value in grid.Values)
            fixture.GridValues[section + "/" + value] = new JsonObject(grid.Fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(10))));
        await fixture.Service.ConnectMenuAsync(false);
        await fixture.Service.RefreshMenuGridAsync(section);
        fixture.Display.Requests.Clear();
        return fixture;
    }
}
