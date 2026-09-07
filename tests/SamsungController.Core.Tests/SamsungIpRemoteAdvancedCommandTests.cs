using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Core.Tests;

public sealed class SamsungIpRemoteAdvancedCommandTests
{
    [Theory]
    [InlineData("WB20P.RedControl", "{\"WB20P.Red\":51}")]
    [InlineData("WB20P.RedControl", "{\"WB20P.Red\":-51}")]
    [InlineData("WB20P.RedControl", "{\"WB20P\":{\"Red\":1}}")]
    [InlineData("WB20P.IntervalControl", "{\"WB20P.Interval\":35}")]
    [InlineData("WB20P.IntervalControl", "{\"WB20P.Interval\":\"33%\"}")]
    [InlineData("WB2PointControl", "{}")]
    [InlineData("WB2PointControl", "{\"R-Gain\":-51}")]
    [InlineData("WB2PointControl", "{\"RGain\":1}")]
    [InlineData("WB2PointControl", "{\"R-Gain\":\"1\"}")]
    [InlineData("WB2PointControl", "{\"WB2Point\":{\"R-Gain\":1}}")]
    [InlineData("backlightControl", "{\"backlight\":51}")]
    [InlineData("tintControl", "{\"tint\":16}")]
    [InlineData("tintControl", "{\"tint\":-16}")]
    [InlineData("gamma.ST2084Control", "{\"gamma.ST2084\":4}")]
    [InlineData("gamma.HLGControl", "{\"gamma.HLG\":-4}")]
    [InlineData("AMP.blurReductionControl", "{\"AMP.blurReduction\":11}")]
    [InlineData("colorSpace.GreenControl", "{\"colorSpace.Green\":101}")]
    [InlineData("colorSpace.ColorAdjustmentPointControl", "{\"colorSpace.ColorAdjustmentPoint\":\"Red\"}")]
    [InlineData("pixelShiftMenuControl", "{\"pixelShiftMenu\":false}")]
    [InlineData("displayRotatorControl", "{\"orientation\":\"Portrait\"}")]
    [InlineData("updateFirmware", "{}")]
    public async Task InvalidAdvancedParametersAreRejectedBeforeTransport(string method, string json)
    {
        var handler = new SamsungIpRemoteClientTests.RpcHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(new SamsungIpRemoteClientTests.MemoryTokens(), http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteCommandAsync(new() { Host = "192.0.2.5" }, method, JsonNode.Parse(json)!.AsObject()));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EveryExposedGetterSendsOnlyTokenAndNeverActsAsSetter()
    {
        var options = new SamsungIpRemoteOptions { Host = "192.0.2.5" };
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens(); tokens.Values[options.Endpoint.AbsoluteUri] = "test-secret";
        var handler = new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject { ["value"] = 0 })));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(tokens, http);
        foreach (var command in SamsungIpRemoteCommands.All.Where(command => command.CanQuery))
        {
            var reply = await client.ExecuteCommandAsync(options, command.Method, new(), query: true);
            Assert.True(reply.IsSuccess, reply.Message);
            Assert.DoesNotContain("acknowledged", reply.Message, StringComparison.Ordinal);
            Assert.Single(handler.Requests.Last()["params"]!.AsObject());
        }
        Assert.DoesNotContain(handler.Requests, request => request["method"]!.GetValue<string>() is "remoteKeyControl" or "volumeUpDnControl" or "channelUpDnControl");
    }

    [Theory]
    [InlineData("firstScreenAppControl", "[\"Plex\",{\"applicationName\":\"YouTube\",\"AccessToken\":\"nested-secret\"}]")]
    [InlineData("multiviewControl", "[]")]
    [InlineData("multiviewControl", "[\"example-mode\"]")]
    public async Task ModeAndAppListsPreserveSanitizedJsonWithoutSelectingAnything(string method, string json)
    {
        var options = new SamsungIpRemoteOptions { Host = "192.0.2.5" };
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens(); tokens.Values[options.Endpoint.AbsoluteUri] = "test-secret";
        var handler = new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(Reply(request, JsonNode.Parse(json)!)));
        using var http = new HttpClient(handler);
        var result = await new SamsungIpRemoteClient(tokens, http).ExecuteCommandAsync(options, method, new(), query: true);
        Assert.True(result.IsSuccess, result.Message);
        Assert.IsType<JsonArray>(result.Payload);
        Assert.Single(Assert.Single(handler.Requests)["params"]!.AsObject());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPointSupportsPartialUpdatesAndDependenciesKeepLiteralCaseAndDots()
    {
        var values = SamsungIpRemoteCommands.Get("WB2PointControl").Validate(new() { ["R-Offset"] = -3 }, false);
        Assert.Single(values);
        Assert.Equal(-3, values["R-Offset"]!.GetValue<int>());
        Assert.Equal(new[] { "WB20PointModeControl", "WB20P.IntervalControl" }, SamsungIpRemoteCommands.Get("WB20P.RedControl").Requirements.Select(item => item.Method));
        Assert.Equal(new[] { "colorSpaceControl", "colorSpace.ColorControl" }, SamsungIpRemoteCommands.Get("colorSpace.BlueControl").Requirements.Select(item => item.Method));
        Assert.Equal("ST.2084", Assert.Single(Assert.Single(SamsungIpRemoteCommands.Get("gamma.ST2084Control").Requirements).AllowedValues));
    }

    private static HttpResponseMessage Reply(JsonObject request, JsonNode result) => new(HttpStatusCode.OK)
    { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString()) };
}
