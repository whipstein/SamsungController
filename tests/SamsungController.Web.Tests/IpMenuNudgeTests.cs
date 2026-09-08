using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuNudgeTests
{
    private const string Contrast = "contrastControl/contrast";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AbsoluteValuesAndClicksShareOneOrderedQueueWithoutDuplicateTargets()
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuValueAsync(Contrast, "46");
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuValueAsync(Contrast, "40");
        var third = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await fixture.Service.QueueMenuValueAsync(Contrast, "41");
        Assert.Equal(new[] { 46, 40, 41 }, fixture.Service.GetSnapshot().Menu.NudgeQueue!.Values.Select(value => value.Target));
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 46, 40, 41 }, fixture.Writes.Select(write => write["params"]!["contrast"]!.GetValue<int>()));
        Assert.Equal(41, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("50.5")]
    [InlineData("51")]
    [InlineData("-1")]
    public async Task InvalidAbsoluteValuesDoNotAlterTheQueueOrItsNextExpectedValue(string text)
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuValueAsync(Contrast, "46");
        await hold.Entered.Task.WaitAsync(Timeout);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.QueueMenuValueAsync(Contrast, text));
        Assert.Single(fixture.Service.GetSnapshot().Menu.NudgeQueue!.Values);
        var second = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 46, 47 }, fixture.Writes.Select(write => write["params"]!["contrast"]!.GetValue<int>()));
    }

    [Fact]
    public async Task RapidClicksSendEverySuccessiveTargetInOrderAndDoNotInterleaveOtherOperations()
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        var third = fixture.Service.QueueMenuNudgeAsync(Contrast, -1);
        Assert.Equal(new[] { 46, 47, 46 }, fixture.Service.GetSnapshot().Menu.NudgeQueue!.Values.Select(value => value.Target));
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Null(fixture.Service.MenuNudgeDisabledReason(IpMenuCatalog.Get(Contrast)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RefreshMenuSectionAsync("expert"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SendMenuKeyAsync("return"));
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.DisconnectMenuAsync);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.StageMenuValue("pictureModeControl/pictureMode", "Movie"));
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 46, 47, 46 }, fixture.Writes.Select(write => write["params"]!["contrast"]!.GetValue<int>()));
        Assert.Equal(46, fixture.Display.Contrast);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
        Assert.DoesNotContain("remoteKeyControl", fixture.Display.Methods);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("mismatch")]
    [InlineData("context")]
    [InlineData("transport")]
    [InlineData("readback")]
    public async Task FailureDiscardsUnsentClicksAndNeverResumesOnRestart(string failure)
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuValueAsync(Contrast, "40");
        var third = fixture.Service.QueueMenuNudgeAsync("sharpnessControl/sharpness", 1);
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() == "contrastControl")
            {
                if (failure == "transport") throw new HttpRequestException("simulated lost connection");
                if (failure == "context") fixture.Display.Mode = "Standard";
                if (failure == "rejected") return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
                if (failure == "mismatch") return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject()));
            }
            if (failure == "readback" && request["method"]!.ToString() == "getVideoStates")
                return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        hold.AfterRelease = (request, cancellation) => fixture.Override(request, cancellation);
        hold.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(first, second, third).WaitAsync(Timeout));
        await IdleAsync(fixture.Service);
        Assert.True(first.IsFaulted && second.IsFaulted && third.IsFaulted);
        Assert.Single(fixture.Writes);
        Assert.Equal(0, fixture.Display.Sharpness);
        Assert.Equal(failure != "rejected", fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Null(fixture.Service.GetSnapshot().Menu.NudgeQueue);
    }

    [Fact]
    public async Task StopCancelsActiveRequestAndDropsEverythingBehindIt()
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuValueAsync(Contrast, "40");
        var count = fixture.Display.Requests.Count;
        fixture.Service.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(first, second).WaitAsync(Timeout));
        await IdleAsync(fixture.Service);
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Single(fixture.Writes);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Fact]
    public async Task StopBeforeWorkerAcquiresOperationDoesNotSendAnyRequests()
    {
        using var fixture = await ReadyAsync();
        fixture.Display.Requests.Clear();
        var stopped = false;
        void StopOnEnqueue()
        {
            if (!stopped && fixture.Service.GetSnapshot().Menu.NudgeQueue is not null)
            { stopped = true; fixture.Service.Cancel(); }
        }
        fixture.Service.Changed += StopOnEnqueue;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.QueueMenuNudgeAsync(Contrast, 1));
        fixture.Service.Changed -= StopOnEnqueue;
        await IdleAsync(fixture.Service);
        Assert.Empty(fixture.Display.Requests);
    }

    [Fact]
    public async Task UpperLimitIgnoresExtraPlusClicksButAllowsQueuedMinus()
    {
        using var fixture = await ReadyAsync();
        fixture.Display.Contrast = 49;
        await fixture.Service.RefreshMenuSectionAsync("expert");
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        await fixture.Service.QueueMenuNudgeAsync(Contrast, 1); // Already queued 50: no duplicate setter.
        var back = fixture.Service.QueueMenuNudgeAsync(Contrast, -1);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, back).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 50, 49 }, fixture.Writes.Select(write => write["params"]!["contrast"]!.GetValue<int>()));
    }

    [Fact]
    public async Task WhiteBalanceQueuesToMinus50AndPreservesOtherChannels()
    {
        using var fixture = await ReadyAsync();
        fixture.Values["B-Gain"] = -48;
        fixture.Values["R-Gain"] = -6;
        await fixture.Service.RefreshMenuSectionAsync("white2");
        using var hold = HoldFirstWrite(fixture, "WB2PointControl");
        var first = fixture.Service.QueueMenuNudgeAsync("WB2PointControl/B-Gain", -1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuValueAsync("WB2PointControl/B-Gain", "-50");
        await fixture.Service.QueueMenuNudgeAsync("WB2PointControl/B-Gain", -1);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { -49, -50 }, fixture.Writes.Select(write => write["params"]!["B-Gain"]!.GetValue<int>()));
        Assert.All(fixture.Writes, write => { Assert.Equal(7, write["params"]!.AsObject().Count); Assert.Equal(-6, write["params"]!["R-Gain"]!.GetValue<int>()); });
    }

    [Theory]
    [InlineData("white20")]
    [InlineData("color")]
    public async Task IndexedClicksPreserveRowIdentityAndRestoreSelectors(string section)
    {
        using var fixture = await ReadyAsync();
        var grid = IpMenuGrids.ForSection(section)!;
        fixture.Values[grid.ModeField] = grid.RequiredMode;
        fixture.Values[grid.SelectorField] = grid.Values.Last();
        foreach (var value in grid.Values)
            fixture.GridValues[section + "/" + value] = new JsonObject(grid.Fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(10))));
        await fixture.Service.RefreshMenuGridAsync(section);
        fixture.Display.Requests.Clear();
        var control = grid.Row(grid.Values[0]).First();
        using var hold = HoldFirstWrite(fixture, control.Method);
        var first = fixture.Service.QueueMenuNudgeAsync(control.Id, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuNudgeAsync(control.Id, 1);
        var third = fixture.Service.QueueMenuValueAsync(control.Id, "20");
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 11, 12, 20 }, fixture.Writes.Where(write => write["method"]!.ToString() == control.Method).Select(write => write["params"]![control.Field]!.GetValue<int>()));
        Assert.Equal(20, fixture.GridValues[section + "/" + grid.Values[0]][control.Field]!.GetValue<int>());
        Assert.Equal(10, fixture.GridValues[section + "/" + grid.Values[1]][control.Field]!.GetValue<int>());
        Assert.Equal(grid.Values.Last(), fixture.Values[grid.SelectorField]!.ToString());
        Assert.Equal("Restored", fixture.Service.GetSnapshot().Menu.SelectorSession!.Status);
    }

    [Fact]
    public async Task WaitForApplyStillStagesWithoutSendingAndQueueRejectsIneligibleInputs()
    {
        using var fixture = await ReadyAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.QueueMenuNudgeAsync(Contrast, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueMenuNudgeAsync("pictureModeControl/pictureMode", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.QueueMenuValueAsync("pictureModeControl/pictureMode", "Movie"));
        fixture.Service.StageMenuValue(Contrast, "46");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueMenuNudgeAsync(Contrast, 1));
        fixture.Service.DiscardMenuChanges();
        await fixture.Service.SaveMenuPreferencesAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueMenuNudgeAsync(Contrast, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueMenuValueAsync(Contrast, "40"));
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAriaButtonAsync("Increase Contrast");
        await renderer.ClickAriaButtonAsync("Increase Contrast");
        await renderer.AssertTargetAsync("Contrast value", 47);
        Assert.Empty(fixture.Writes);
        Assert.Equal(47, fixture.Service.GetSnapshot().Menu.Pending[Contrast].Target.GetValue<int>());
        await renderer.ChangeAsync("Contrast slider", "40");
        await renderer.ChangeAsync("Contrast slider", "40", "onchange");
        await renderer.ChangeAsync("Contrast value", "38");
        await renderer.ChangeAsync("Contrast value", "38", "onchange");
        await renderer.AssertTargetAsync("Contrast value", 38);
        Assert.Equal(38, fixture.Service.GetSnapshot().Menu.Pending[Contrast].Target.GetValue<int>());
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task BoundedQueueRejectsOverflowAndStopSettlesEveryAcceptedClick()
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var clicks = new List<Task> { fixture.Service.QueueMenuNudgeAsync(Contrast, 1) };
        await hold.Entered.Task.WaitAsync(Timeout);
        for (var i = 1; i < 256; i++) clicks.Add(fixture.Service.QueueMenuNudgeAsync(Contrast, i % 2 == 0 ? 1 : -1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.QueueMenuNudgeAsync(Contrast, 1));
        Assert.Contains("queue is full", error.Message, StringComparison.Ordinal);
        Assert.Equal(256, fixture.Service.GetSnapshot().Menu.NudgeQueue!.Values.Count);
        fixture.Service.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(clicks).WaitAsync(Timeout));
        await IdleAsync(fixture.Service);
        Assert.All(clicks, click => Assert.True(click.IsCompleted));
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task FailureAfterConfirmedClickKeepsThatChangeAndAllowsANewExplicitQueue()
    {
        using var fixture = await ReadyAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        var third = fixture.Service.QueueMenuNudgeAsync(Contrast, 1);
        fixture.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request["method"]!.ToString() == "contrastControl"
            ? MenuFixture.Reject(request, -32002) : null);
        hold.Release.TrySetResult(); // The already-held first write uses its original successful handler.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(first, second, third).WaitAsync(Timeout));
        await IdleAsync(fixture.Service);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsFaulted && third.IsFaulted);
        Assert.Equal(46, fixture.Display.Contrast);
        Assert.Equal(2, fixture.Writes.Count());
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        fixture.Override = null;
        await fixture.Service.QueueMenuNudgeAsync(Contrast, -1).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(45, fixture.Display.Contrast);
        Assert.Equal(3, fixture.Writes.Count());
    }

    [Fact]
    public async Task ConfirmedWhiteBalanceWarningsDoNotBlockTheRemainingClicks()
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.RefreshMenuSectionAsync("white2");
        using var hold = HoldFirstWrite(fixture, "WB2PointControl");
        var first = fixture.Service.QueueMenuNudgeAsync("WB2PointControl/B-Gain", 1);
        await hold.Entered.Task.WaitAsync(Timeout);
        var second = fixture.Service.QueueMenuNudgeAsync("WB2PointControl/B-Gain", 1);
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl" || request["params"]!.AsObject().Count == 1)
                return Task.FromResult<HttpResponseMessage?>(null);
            foreach (var pair in request["params"]!.AsObject().Where(pair => pair.Key != "AccessToken")) fixture.Values[pair.Key] = pair.Value!.DeepClone();
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        hold.AfterRelease = (request, cancellation) => fixture.Override(request, cancellation);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(2, fixture.Values["B-Gain"]!.GetValue<int>());
        Assert.Equal(2, fixture.Writes.Count());
        Assert.Equal("Applied with TV warning", fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Status);
    }

    [Fact]
    public async Task RepeatedUiClicksStayEnabledAndKeepTheSameCardStructureDuringRequests()
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        var structure = await renderer.ControlStructureAsync(Contrast);
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = renderer.ClickAriaButtonAsync("Increase Contrast");
        await hold.Entered.Task.WaitAsync(Timeout);
        await renderer.AssertElementDisabledAsync("button", "Increase Contrast", false);
        await renderer.AssertElementDisabledAsync("input", "Contrast value", false);
        await renderer.AssertElementDisabledAsync("input", "Contrast slider", false);
        var second = renderer.ClickAriaButtonAsync("Increase Contrast");
        await renderer.AssertTargetAsync("Contrast value", 47);
        var third = renderer.ClickAriaButtonAsync("Decrease Contrast");
        await renderer.AssertTargetAsync("Contrast value", 46);
        await renderer.AssertTextAsync("3 adjustment(s) queued / running");
        Assert.Equal(structure, await renderer.ControlStructureAsync(Contrast));
        await renderer.AssertDisabledAsync("Stop", false);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(structure, await renderer.ControlStructureAsync(Contrast));
        await renderer.AssertElementDisabledAsync("input", "Contrast value", false);
        await renderer.AssertTargetAsync("Contrast value", 46);
        Assert.Equal(3, fixture.Writes.Count());
    }

    [Theory]
    [InlineData("Contrast slider", "Contrast value")]
    [InlineData("Contrast value", "Contrast slider")]
    public async Task SliderAndTextEditsStartAndJoinTheQueueAndKeepNewerPreviewsAcrossReplies(string firstInput, string secondInput)
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        var structure = await renderer.ControlStructureAsync(Contrast);
        using var firstHold = HoldFirstWrite(fixture, "contrastControl");
        await renderer.ChangeAsync(firstInput, "46");
        Assert.Empty(fixture.Writes); // A preview is not a committed adjustment.
        var first = renderer.ChangeAsync(firstInput, "46", "onchange");
        await firstHold.Entered.Task.WaitAsync(Timeout);
        await renderer.AssertElementDisabledAsync("input", "Contrast slider", false);
        await renderer.AssertElementDisabledAsync("input", "Contrast value", false);
        await renderer.AssertElementDisabledAsync("button", "Increase Contrast", false);
        await renderer.ChangeAsync(secondInput, "40");
        var second = renderer.ChangeAsync(secondInput, "40", "onchange");
        await renderer.ChangeAsync(firstInput, "38");
        Assert.Equal(new[] { 46, 40 }, fixture.Service.GetSnapshot().Menu.NudgeQueue!.Values.Select(value => value.Target));

        using var secondHold = HoldFirstWrite(fixture, "contrastControl");
        firstHold.Release.TrySetResult();
        await secondHold.Entered.Task.WaitAsync(Timeout);
        await first.WaitAsync(Timeout); // The first readback must not erase the uncommitted 38.
        Assert.Equal(46, fixture.Display.Contrast);
        await renderer.AssertTargetAsync("Contrast value", 38);
        await renderer.AssertTargetAsync("Contrast slider", 38);
        var third = renderer.ChangeAsync(firstInput, "38", "onchange");
        var fourth = renderer.ClickAriaButtonAsync("Decrease Contrast");
        await renderer.AssertTargetAsync("Contrast value", 37);
        Assert.Equal(structure, await renderer.ControlStructureAsync(Contrast));
        secondHold.Release.TrySetResult();
        await Task.WhenAll(second, third, fourth).WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(new[] { 46, 40, 38, 37 }, fixture.Writes.Select(write => write["params"]!["contrast"]!.GetValue<int>()));
        await renderer.AssertTargetAsync("Contrast value", 37);
        Assert.Equal(structure, await renderer.ControlStructureAsync(Contrast));
    }

    [Theory]
    [InlineData("")]
    [InlineData("51")]
    public async Task InProgressTextSurvivesQueueCompletionAndInvalidCommitDoesNotSend(string text)
    {
        using var fixture = await ReadyAsync();
        await using var services = Services(fixture);
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        using var hold = HoldFirstWrite(fixture, "contrastControl");
        var first = renderer.ClickAriaButtonAsync("Increase Contrast");
        await hold.Entered.Task.WaitAsync(Timeout);
        await renderer.ChangeAsync("Contrast value", text);
        hold.Release.TrySetResult();
        await first.WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        await renderer.AssertInputValueAsync("Contrast value", text);
        await renderer.ChangeAsync("Contrast value", text, "onchange");
        await renderer.AssertInputValueAsync("Contrast value", text);
        Assert.Single(fixture.Writes);
        Assert.Null(fixture.Service.GetSnapshot().Menu.NudgeQueue);
        await renderer.AssertElementDisabledAsync("input", "Contrast value", false);
        await renderer.ChangeAsync("Contrast value", "40");
        await renderer.ChangeAsync("Contrast value", "40", "onchange").WaitAsync(Timeout);
        await IdleAsync(fixture.Service);
        Assert.Equal(40, fixture.Display.Contrast);
    }

    private static ServiceProvider Services(MenuFixture fixture) => new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
        .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();

    private static async Task<MenuFixture> ReadyAsync()
    {
        var fixture = await MenuFixture.CreateAsync();
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await fixture.Service.SaveMenuPreferencesAsync(true);
        return fixture;
    }

    private static async Task IdleAsync(SamsungIpRemoteService service)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (!service.GetSnapshot().IsBusy && service.GetSnapshot().Menu.NudgeQueue is null) idle.TrySetResult(); }
        service.Changed += Check;
        try { Check(); await idle.Task.WaitAsync(Timeout); }
        finally { service.Changed -= Check; }
    }

    private static HeldWrite HoldFirstWrite(MenuFixture fixture, string method)
    {
        var hold = new HeldWrite();
        var held = false;
        fixture.Override = async (request, cancellation) =>
        {
            if (!held && request["method"]!.ToString() == method && request["params"]!.AsObject().Count > 1)
            {
                held = true;
                hold.Entered.TrySetResult();
                await hold.Release.Task.WaitAsync(cancellation);
                if (hold.AfterRelease is { } after) return await after(request, cancellation);
            }
            return null;
        };
        return hold;
    }
    private sealed class HeldWrite : IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<JsonObject, CancellationToken, Task<HttpResponseMessage?>>? AfterRelease { get; set; }
        public void Dispose() => Release.TrySetResult();
    }
}
