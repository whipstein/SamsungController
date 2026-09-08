using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Core.IpRemote;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuWhiteBalanceWarningTests
{
    private static readonly string[] Fields = ["R-Gain", "G-Gain", "B-Gain", "R-Offset", "G-Offset", "B-Offset"];

    [Theory]
    [InlineData("R-Gain", "flat")]
    [InlineData("G-Gain", "object")]
    [InlineData("B-Gain", "string")]
    [InlineData("R-Offset", "flat")]
    [InlineData("G-Offset", "object")]
    [InlineData("B-Offset", "string")]
    public async Task ConfirmedFullWriteAcceptsWarningWithoutRetryAndPersistsEvidence(string field, string shape)
    {
        using var fixture = await ReadyAsync(shape);
        var before = Fields.ToDictionary(name => name, name => fixture.Values[name]!.GetValue<int>());
        var target = before[field] - 1;
        fixture.Service.StageMenuValue("WB2PointControl/" + field, target.ToString());
        fixture.Display.Requests.Clear();
        await fixture.Service.ApplyMenuAsync();
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal("Completed with TV warning", menu.Update!.Status);
        Assert.Equal("Applied with TV warning", menu.Update.Steps.Single().Status);
        Assert.Contains("-32002", menu.Update.Steps.Single().Warning, StringComparison.Ordinal);
        Assert.Contains("independent readback confirmed", menu.Update.Steps.Single().Warning, StringComparison.Ordinal);
        Assert.False(menu.Update.NeedsReview);
        Assert.Empty(menu.Pending);
        Assert.Equal(target, fixture.Value("WB2PointControl/" + field)!.GetValue<int>());
        foreach (var other in Fields.Where(name => name != field)) Assert.Equal(before[other], fixture.Values[other]!.GetValue<int>());
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(Fields.Append("AccessToken").Order(), write["params"]!.AsObject().Select(pair => pair.Key).Order());
        foreach (var other in Fields.Where(name => name != field)) Assert.Equal(before[other], write["params"]![other]!.GetValue<int>());
        Assert.Equal(7, fixture.Display.Requests.Count); // Preflight x3, one write, readback x3; no second scan.
        Assert.Contains(fixture.Service.GetSnapshot().Observations, observation => observation.Exchange.Method == "WB2PointControl"
            && observation.Exchange.Outcome == SamsungIpRemoteOutcome.RpcError && observation.Exchange.RpcErrorCode == -32002);
        var warning = menu.Update.Steps.Single().Warning;
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        Assert.Equal(warning, fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Warning);
        Assert.False(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
    }

    [Fact]
    public async Task ConfirmedWarningDoesNotBlockLaterExplicitDraftsOrLosePerRowWarnings()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Service.StageMenuValue("WB2PointControl/G-Gain", "3");
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        await fixture.Service.ApplyMenuAsync();
        Assert.Equal(new[] { "WB2PointControl", "WB2PointControl", "contrastControl" }, fixture.Writes.Select(request => request["method"]!.ToString()));
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal(new[] { "Applied with TV warning", "Applied with TV warning", "Applied" }, update.Steps.Select(step => step.Status));
        Assert.Equal(2, update.Steps.Count(step => step.Warning is not null));
        Assert.False(update.NeedsReview);
        Assert.Equal(-1, fixture.Values["R-Gain"]!.GetValue<int>());
        Assert.Equal(3, fixture.Values["G-Gain"]!.GetValue<int>());
        Assert.Equal(44, fixture.Display.Contrast);
        Assert.Empty(fixture.Service.GetSnapshot().Menu.Pending);
    }

    [Fact]
    public async Task LaterFailureAndRestartPreserveBothConfirmedWarningAndUncertainWrite()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var whiteBalanceHandler = fixture.Override!;
        fixture.Override = (request, cancellation) =>
        {
            if (request["method"]!.ToString() != "contrastControl") return whiteBalanceHandler(request, cancellation);
            fixture.Display.Contrast = 44;
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal("Stopped", update.Status);
        Assert.Equal("Applied with TV warning", update.Steps[0].Status);
        Assert.NotNull(update.Steps[0].Warning);
        Assert.Equal("Uncertain", update.Steps[1].Status);
        Assert.Null(update.Steps[1].Warning);
        Assert.True(update.NeedsReview);
        Assert.Equal(2, fixture.Writes.Count());
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        var reloaded = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal(update.Status, reloaded.Status);
        Assert.Equal(update.Steps.Count, reloaded.Steps.Count);
        for (var i = 0; i < update.Steps.Count; i++)
        {
            Assert.Equal(update.Steps[i].ControlId, reloaded.Steps[i].ControlId);
            Assert.Equal(update.Steps[i].Status, reloaded.Steps[i].Status);
            Assert.Equal(update.Steps[i].Warning, reloaded.Steps[i].Warning);
            Assert.True(JsonNode.DeepEquals(update.Steps[i].Original, reloaded.Steps[i].Original));
            Assert.True(JsonNode.DeepEquals(update.Steps[i].Target, reloaded.Steps[i].Target));
        }
        Assert.True(reloaded.NeedsReview);
    }

    [Fact]
    public async Task PreviouslyUncertainWhiteBalanceIsNotReclassifiedOrResentOnRestart()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Override = (request, _) =>
        {
            if (request["params"]?["R-Gain"] is not { } target) return Task.FromResult<HttpResponseMessage?>(null);
            fixture.Values["R-Gain"] = target.DeepClone();
            fixture.Service.Cancel();
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(-1, fixture.Values["R-Gain"]!.GetValue<int>());
        var count = fixture.Display.Requests.Count;
        await fixture.RestartAsync();
        Assert.Equal(count, fixture.Display.Requests.Count);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal("Uncertain", update.Steps.Single().Status);
        Assert.Null(update.Steps.Single().Warning);
        Assert.True(update.NeedsReview);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("wrong target")]
    [InlineData("other channel")]
    [InlineData("missing after")]
    [InlineData("unusable channel")]
    [InlineData("input")]
    [InlineData("picture mode")]
    [InlineData("ordinary picture value")]
    public async Task MatchingTargetAloneNeverAcceptsUncertainOrChangedContext(string failure)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var written = false;
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl") return Task.FromResult<HttpResponseMessage?>(null);
            if (request["params"]?["R-Gain"] is { } target)
            {
                written = true;
                fixture.Values["R-Gain"] = failure == "wrong target" ? JsonValue.Create(-2) : target.DeepClone();
                if (failure == "other channel") fixture.Values["G-Gain"] = 5;
                if (failure == "input") fixture.Display.Input = "HDMI2";
                if (failure == "picture mode") fixture.Display.Mode = "Standard";
                if (failure == "ordinary picture value") fixture.Display.Color = 26;
                return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            }
            var values = Values(fixture);
            if (failure == "missing after" && written) values.Remove("G-Offset");
            if (failure == "unusable channel" && written) values["G-Offset"] = "unknown";
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, values));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal("Uncertain", update.Steps[0].Status);
        Assert.True(update.NeedsReview);
        Assert.All(update.Steps, step => Assert.Null(step.Warning));
        Assert.Equal("Pending", update.Steps[1].Status);
        Assert.Single(fixture.Writes);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedTargetIsRejectedAndChangedPeerStillRequiresReview(bool changedPeer)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Override = (request, _) =>
        {
            if (request["params"]?["R-Gain"] is null) return Task.FromResult<HttpResponseMessage?>(null);
            if (changedPeer) fixture.Values["G-Gain"] = 5;
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        var update = fixture.Service.GetSnapshot().Menu.Update!;
        Assert.Equal(changedPeer ? "Uncertain" : "Rejected unchanged", update.Steps.Single().Status);
        Assert.Equal(changedPeer, update.NeedsReview);
        Assert.Null(update.Steps.Single().Warning);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData(-32003)]
    [InlineData(-32602)]
    [InlineData(-32010)]
    [InlineData(-32601)]
    public async Task OtherErrorsAreNotReclassifiedEvenWhenTheValueChanged(int code)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        fixture.Override = (request, _) =>
        {
            if (request["params"]?["R-Gain"] is not { } target) return Task.FromResult<HttpResponseMessage?>(null);
            fixture.Values["R-Gain"] = target.DeepClone();
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, code));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Warning);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task OtherMethodsKeepTheirOriginalErrorPolicy()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "contrastControl") return Task.FromResult<HttpResponseMessage?>(null);
            fixture.Display.Contrast = 44;
            return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Warning);
        Assert.Single(fixture.Writes);
    }

    [Theory]
    [InlineData("cancel write")]
    [InlineData("cancel readback")]
    [InlineData("transport")]
    [InlineData("wrong id")]
    [InlineData("failed readback")]
    public async Task InterruptedOrUntrustedRepliesNeverCountAsApplied(string failure)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/R-Gain", "-1");
        var written = false;
        var stopCount = 0;
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl") return Task.FromResult<HttpResponseMessage?>(null);
            if (request["params"]?["R-Gain"] is { } target)
            {
                written = true;
                fixture.Values["R-Gain"] = target.DeepClone();
                stopCount = fixture.Display.Requests.Count;
                if (failure == "transport") throw new HttpRequestException("simulated disconnect after delivery");
                if (failure == "cancel write") fixture.Service.Cancel();
                if (failure == "wrong id") return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
                    { Content = new StringContent("""{"jsonrpc":"2.0","id":"wrong","error":{"code":-32002}}""") });
                return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            }
            if (written && failure is "cancel readback" or "failed readback")
            {
                stopCount = fixture.Display.Requests.Count;
                if (failure == "cancel readback") fixture.Service.Cancel();
                return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await Assert.ThrowsAnyAsync<Exception>(fixture.Service.ApplyMenuAsync);
        Assert.Equal(stopCount, fixture.Display.Requests.Count);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Null(fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Warning);
        Assert.Single(fixture.Writes);
    }

    [Fact]
    public async Task MenuShowsAppliedWarningInsteadOfErrorAndAllowsTheNextEdit()
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("2-point white balance");
        await renderer.ChangeAsync("R Gain value", "-1", "onchange");
        await renderer.ClickAsync("Apply 1 pending");
        await renderer.AssertTextAsync("Applied with TV warning");
        await renderer.AssertTextAsync("independent readback confirmed");
        await renderer.AssertTextAbsentAsync("I checked the TV — close interrupted update");
        await renderer.AssertClassPresentAsync("direct-update-warning");
        await renderer.AssertTargetAsync("R Gain value", -1);
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("WB2PointControl/R-Gain")));
        await renderer.ChangeAsync("R Gain value", "-2", "onchange");
        await renderer.AssertDisabledAsync("Apply 1 pending", false);
        Assert.Single(fixture.Writes);
    }

    private static async Task<MenuFixture> ReadyAsync(string shape = "flat")
    {
        var fixture = await MenuFixture.CreateAsync();
        var originals = new[] { 0, 4, -3, 2, -1, 1 };
        for (var i = 0; i < Fields.Length; i++) fixture.Values[Fields[i]] = originals[i];
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl") return Task.FromResult<HttpResponseMessage?>(null);
            var sent = request["params"]!.AsObject().Where(pair => Fields.Contains(pair.Key)).ToArray();
            if (sent.Length > 0)
            {
                foreach (var pair in sent) fixture.Values[pair.Key] = pair.Value!.DeepClone();
                return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
            }
            var values = Values(fixture);
            if (shape == "string") foreach (var field in Fields) values[field] = values[field]!.ToString();
            var response = shape switch { "object" => new JsonObject { ["WB2Point"] = values }, "string" => new JsonObject { ["WB2Point"] = values.ToJsonString() }, _ => values };
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, response));
        };
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshMenuSectionAsync("white2");
        return fixture;
    }

    private static JsonObject Values(MenuFixture fixture) => new(Fields.Select(field => KeyValuePair.Create(field, fixture.Values[field]?.DeepClone())));
}
