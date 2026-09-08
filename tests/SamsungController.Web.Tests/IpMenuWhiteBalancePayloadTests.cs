using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using SamsungController.Web.Components.Pages;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class IpMenuWhiteBalancePayloadTests
{
    private static readonly string[] Fields = ["R-Gain", "G-Gain", "B-Gain", "R-Offset", "G-Offset", "B-Offset"];

    [Theory]
    [InlineData("R-Gain", "flat")]
    [InlineData("G-Gain", "object")]
    [InlineData("B-Gain", "string")]
    [InlineData("R-Offset", "flat")]
    [InlineData("G-Offset", "object")]
    [InlineData("B-Offset", "string")]
    public async Task EveryChannelWorksWithASequentialSixFieldSetter(string field, string shape)
    {
        using var fixture = await ReadyAsync(shape);
        var before = Values(fixture);
        var target = before[field]!.GetValue<int>() + 1;
        fixture.Service.StageMenuValue("WB2PointControl/" + field, target.ToString());
        fixture.Display.Requests.Clear();
        await fixture.Service.ApplyMenuAsync();
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(Fields.Append("AccessToken").Order(), write["params"]!.AsObject().Select(pair => pair.Key).Order());
        foreach (var name in Fields)
        {
            var expected = name == field ? target : before[name]!.GetValue<int>();
            Assert.Equal(expected, write["params"]![name]!.GetValue<int>());
            Assert.Equal(expected, fixture.Value("WB2PointControl/" + name)!.GetValue<int>());
        }
        Assert.Equal(7, fixture.Display.Requests.Count);
        var menu = fixture.Service.GetSnapshot().Menu;
        Assert.Equal("Completed", menu.Update!.Status);
        Assert.Equal("Applied", menu.Update.Steps.Single().Status);
        Assert.Null(menu.Update.Steps.Single().Warning);
        Assert.False(menu.Update.NeedsReview);
        Assert.Empty(menu.Pending);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("out of range")]
    [InlineData("fraction")]
    public async Task IncompleteFreshPreflightNeverSendsOrUsesCachedPeers(string failure)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/B-Gain", "2");
        var before = Values(fixture);
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl") return Task.FromResult<HttpResponseMessage?>(null);
            var values = Values(fixture);
            switch (failure)
            {
                case "missing": values.Remove("G-Offset"); break;
                case "null": values["G-Offset"] = null; break;
                case "unknown": values["G-Offset"] = "unknown"; break;
                case "out of range": values["G-Offset"] = 51; break;
                case "fraction": values["G-Offset"] = 1.5; break;
            }
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, values));
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Contains("G-Offset is missing or invalid", error.Message, StringComparison.Ordinal);
        Assert.Contains("No setting was sent", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Writes);
        Assert.True(JsonNode.DeepEquals(before, Values(fixture)));
        var snapshot = fixture.Service.GetSnapshot();
        Assert.Equal("Not sent", snapshot.Menu.Update!.Steps.Single().Status);
        Assert.False(snapshot.Menu.Update.NeedsReview);
        Assert.False(snapshot.IsBusy);
        Assert.True(snapshot.Menu.Connected);
    }

    [Fact]
    public async Task PreservedPeersComeFromFreshPreflightNotTheEditingCache()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/B-Gain", "2");
        fixture.Values["R-Gain"] = -6; // Another controller changed this since the editor loaded.
        await fixture.Service.ApplyMenuAsync();
        var write = Assert.Single(fixture.Writes);
        Assert.Equal(-6, write["params"]!["R-Gain"]!.GetValue<int>());
        Assert.Equal(-6, fixture.Values["R-Gain"]!.GetValue<int>());
        Assert.Equal(2, fixture.Values["B-Gain"]!.GetValue<int>());
        Assert.Equal("Applied", fixture.Service.GetSnapshot().Menu.Update!.Steps.Single().Status);
    }

    [Fact]
    public async Task PendingPeersWaitForTheirOwnStepAndEarlierAppliedChannelsArePreserved()
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/B-Gain", "2");
        fixture.Service.StageMenuValue("WB2PointControl/R-Offset", "3");
        await fixture.Service.ApplyMenuAsync();
        var writes = fixture.Writes.ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(2, writes[0]["params"]!["B-Gain"]!.GetValue<int>());
        Assert.Equal(2, writes[0]["params"]!["R-Offset"]!.GetValue<int>());
        Assert.Equal(2, writes[1]["params"]!["B-Gain"]!.GetValue<int>());
        Assert.Equal(3, writes[1]["params"]!["R-Offset"]!.GetValue<int>());
        Assert.All(writes, write => Assert.Equal(7, write["params"]!.AsObject().Count));
        Assert.All(fixture.Service.GetSnapshot().Menu.Update!.Steps, step => Assert.Equal("Applied", step.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgmentStillRequiresUnchangedAndPresentPeers(bool missing)
    {
        using var fixture = await ReadyAsync();
        fixture.Service.StageMenuValue("WB2PointControl/B-Gain", "2");
        fixture.Service.StageMenuValue("contrastControl/contrast", "44");
        var handler = fixture.Override!;
        var written = false;
        fixture.Override = async (request, cancellation) =>
        {
            var response = await handler(request, cancellation);
            if (request["method"]!.ToString() != "WB2PointControl") return response;
            if (request["params"]!.AsObject().Count > 1)
            {
                written = true;
                fixture.Values["G-Offset"] = 5;
            }
            else if (written && missing)
            {
                response?.Dispose();
                var values = Values(fixture);
                values.Remove("G-Offset");
                return ContrastDisplay.Reply(request, values);
            }
            return response;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Service.ApplyMenuAsync);
        Assert.Equal("Uncertain", fixture.Service.GetSnapshot().Menu.Update!.Steps[0].Status);
        Assert.Equal("Pending", fixture.Service.GetSnapshot().Menu.Update!.Steps[1].Status);
        Assert.True(fixture.Service.GetSnapshot().Menu.Update!.NeedsReview);
        Assert.Single(fixture.Writes);
        Assert.Equal(45, fixture.Display.Contrast);
    }

    [Fact]
    public async Task RejectedUnchangedBlueGainDoesNotFreezeThePageOrRequireRecovery()
    {
        using var fixture = await ReadyAsync();
        await fixture.Service.RefreshMenuSectionAsync("expert");
        var handler = fixture.Override!;
        fixture.Override = (request, cancellation) => request["method"]!.ToString() == "WB2PointControl" && request["params"]!.AsObject().Count > 1
            ? Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002)) : handler(request, cancellation);
        await using var services = new ServiceCollection().AddLogging().AddSingleton(fixture.Service)
            .AddSingleton<IJSRuntime>(new IpRemotePageTests.DownloadJavaScript()).BuildServiceProvider();
        await using var renderer = new IpRemotePageTests.IpPageRenderer(services, typeof(DirectMenu));
        await renderer.StartAsync();
        await renderer.ClickAsync("2-point white balance");
        await renderer.ChangeAsync("B Gain value", "2", "onchange");
        await renderer.ClickAsync("Apply 1 pending");
        await renderer.AssertTextAsync("B Gain: the TV rejected the requested value 2");
        await renderer.AssertTextAsync("The controls remain available");
        await renderer.AssertTextAbsentAsync("I checked the TV — close interrupted update");
        await renderer.AssertDisabledAsync("Refresh section", false);
        await renderer.AssertDisabledAsync("Picture", false);
        Assert.False(fixture.Service.GetSnapshot().IsBusy);
        Assert.Null(fixture.Service.MenuControlDisabledReason(IpMenuCatalog.Get("WB2PointControl/G-Gain")));
        await renderer.ClickAsync("Discard pending changes");
        await renderer.ChangeAsync("G Gain value", "5", "onchange");
        await renderer.AssertDisabledAsync("Apply 1 pending", false);
        Assert.Single(fixture.Writes);
    }

    private static async Task<MenuFixture> ReadyAsync(string shape = "flat")
    {
        var fixture = await MenuFixture.CreateAsync();
        var originals = new[] { -6, 4, 0, 2, -1, 1 };
        for (var i = 0; i < Fields.Length; i++) fixture.Values[Fields[i]] = originals[i];
        fixture.Override = (request, _) =>
        {
            if (request["method"]!.ToString() != "WB2PointControl") return Task.FromResult<HttpResponseMessage?>(null);
            var parameters = request["params"]!.AsObject();
            if (parameters.Count > 1)
            {
                // Models the suspected failure: earlier channels may be applied
                // before a missing later field returns an error. Partial B-Gain
                // cannot pass the first field. A full payload succeeds for all.
                foreach (var field in Fields)
                {
                    if (parameters[field] is not { } value) return Task.FromResult<HttpResponseMessage?>(MenuFixture.Reject(request, -32002));
                    fixture.Values[field] = value.DeepClone();
                }
                return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, new JsonObject()));
            }
            var values = Values(fixture);
            if (shape == "string") foreach (var field in Fields) values[field] = values[field]!.ToString();
            var result = shape switch { "object" => new JsonObject { ["WB2Point"] = values }, "string" => new JsonObject { ["WB2Point"] = values.ToJsonString() }, _ => values };
            return Task.FromResult<HttpResponseMessage?>(ContrastDisplay.Reply(request, result));
        };
        await fixture.Service.ConnectMenuAsync(loadAllSettings: false);
        await fixture.Service.RefreshMenuSectionAsync("white2");
        return fixture;
    }

    private static JsonObject Values(MenuFixture fixture) => new(Fields.Select(field => KeyValuePair.Create(field, fixture.Values[field]?.DeepClone())));
}
