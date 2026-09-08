using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Core.Tests;

public sealed class SamsungIpRemoteBatchProbeTests
{
    private static readonly SamsungIpRemoteOptions Options = new() { Host = "192.0.2.10" };
    private const string Token = "batch-private-credential";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadBatchIsOnePostWithTwoUniqueIdsAndAcceptsReorderedReplies(bool textIds)
    {
        using var handler = new ProbeHandler(request => new JsonArray(request.AsArray().Reverse().Select(call =>
            (JsonNode)Reply(call!, textIds, new() { ["method"] = call!["method"]!.DeepClone(), ["echo"] = Token })).ToArray()));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var result = await client.ProbeReadBatchAsync(Options);
        Assert.True(result.IsSuccess, result.Message);
        var batch = Assert.Single(handler.Requests).AsArray();
        Assert.Equal(new[] { "getTVStates", "getVideoStates" }, batch.Select(call => call!["method"]!.ToString()));
        Assert.Equal(2, batch.Select(call => call!["id"]!.ToString()).Distinct().Count());
        Assert.All(batch, call => Assert.Single(call!["params"]!.AsObject()));
        Assert.Equal("getTVStates", result.Result!["getTVStates"]!["method"]!.ToString());
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("wrong-id")]
    [InlineData("wrong-version")]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("array-result")]
    [InlineData("single-success")]
    public async Task IncompleteOrMalformedRepliesNeverEstablishSupportOrRetry(string flaw)
    {
        using var handler = new ProbeHandler(request =>
        {
            var batch = new JsonArray(request.AsArray().Select(call => (JsonNode)Reply(call!)).ToArray());
            switch (flaw)
            {
                case "missing": batch.RemoveAt(1); break;
                case "duplicate": batch[1] = batch[0]!.DeepClone(); break;
                case "extra": batch.Add(batch[0]!.DeepClone()); break;
                case "wrong-id": batch[1]!["id"] = 999; break;
                case "wrong-version": batch[1]!["jsonrpc"] = "1.0"; break;
                case "both": batch[1]!["error"] = new JsonObject { ["code"] = -1 }; break;
                case "neither": batch[1]!.AsObject().Remove("result"); break;
                case "array-result": batch[1]!["result"] = new JsonArray(); break;
                case "single-success": return batch[0]!.DeepClone();
            }
            return batch;
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var result = await client.ProbeReadBatchAsync(Options);
        Assert.Equal(SamsungIpRemoteOutcome.ProtocolError, result.Outcome);
        Assert.Null(result.Result);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(-32004, false, SamsungIpRemoteOutcome.RpcError)]
    [InlineData(-32010, false, SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData(-32002, true, SamsungIpRemoteOutcome.RpcError)]
    [InlineData(-32010, true, SamsungIpRemoteOutcome.Unauthorized)]
    public async Task WholeOrPartialErrorsAreReportedAndSecretsAreRedacted(int code, bool partial, SamsungIpRemoteOutcome outcome)
    {
        using var handler = new ProbeHandler(request =>
        {
            var error = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = partial ? request[1]!["id"]!.DeepClone() : null,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = Token, ["AccessToken"] = "echo-secret" } };
            return partial ? new JsonArray(Reply(request[0]!), error) : error;
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var result = await client.ProbeReadBatchAsync(Options);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.RpcErrorCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
        Assert.DoesNotContain("echo-secret", JsonSerializer.Serialize(result));
        if (partial) Assert.Single(result.Result!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RgbProbeSendsOneEnvelopeWithAllTargetsAndNoSelector(bool singleMethod)
    {
        using var handler = new ProbeHandler(request => request is JsonArray batch
            ? new JsonArray(batch.Select(call => (JsonNode)Reply(call!)).ToArray()) : Reply(request));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var result = await client.ProbeWhiteBalanceRgbAsync(Options, -50, 0, 50, singleMethod);
        Assert.True(result.IsSuccess, result.Message);
        var request = Assert.Single(handler.Requests);
        if (singleMethod)
        {
            Assert.Equal("WB20P.RedControl", request["method"]!.ToString());
            Assert.Equal(4, request["params"]!.AsObject().Count);
            Assert.Equal(-50, request["params"]!["WB20P.Red"]!.GetValue<int>());
            Assert.Equal(0, request["params"]!["WB20P.Green"]!.GetValue<int>());
            Assert.Equal(50, request["params"]!["WB20P.Blue"]!.GetValue<int>());
        }
        else
        {
            var batch = request.AsArray();
            Assert.Equal(new[] { "WB20P.RedControl", "WB20P.GreenControl", "WB20P.BlueControl" }, batch.Select(call => call!["method"]!.ToString()));
            Assert.Equal(new[] { -50, 0, 50 }, batch.Select(call => call!["params"]!.AsObject().Single(pair => pair.Key != "AccessToken").Value!.GetValue<int>()));
        }
        Assert.DoesNotContain("Interval", request.ToJsonString());
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(-51, 0, 0)]
    [InlineData(0, 51, 0)]
    [InlineData(0, 0, -51)]
    public async Task OutOfRangeIsRejectedBeforeTransportForBothExperiments(int red, int green, int blue)
    {
        using var handler = new ProbeHandler(request => Reply(request));
        using var http = new HttpClient(handler);
        using var client = Client(http);
        foreach (var single in new[] { false, true })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ProbeWhiteBalanceRgbAsync(Options, red, green, blue, single));
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteCommandAsync(Options, "WB20P.RedControl", new() { ["WB20P.Red"] = 1, ["WB20P.Green"] = 2 }));
        Assert.Empty(handler.Requests); // Ordinary commands still reject experimental extra fields.
    }

    private static SamsungIpRemoteClient Client(HttpClient http)
    {
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens();
        tokens.Values[Options.Endpoint.AbsoluteUri] = Token;
        return new(tokens, http);
    }
    private static JsonObject Reply(JsonNode call, bool textId = false, JsonObject? result = null) => new()
    { ["jsonrpc"] = "2.0", ["id"] = textId ? JsonValue.Create(call["id"]!.ToString()) : call["id"]!.DeepClone(), ["result"] = result ?? new() };
    private sealed class ProbeHandler(Func<JsonNode, JsonNode> respond) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string? RawReply { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Options.Endpoint, request.RequestUri);
            Assert.Contains("keep-alive", request.Headers.Connection);
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(json);
            return new(Status) { Content = new StringContent(RawReply ?? respond(json).ToJsonString()) };
        }
    }

    [Theory]
    [InlineData("malformed", SamsungIpRemoteOutcome.ProtocolError)]
    [InlineData("oversized", SamsungIpRemoteOutcome.ProtocolError)]
    [InlineData("unauthorized", SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData("http-error", SamsungIpRemoteOutcome.HttpError)]
    [InlineData("transport", SamsungIpRemoteOutcome.TransportError)]
    public async Task FailedBatchTransportDoesNotRetryOrLeakRawCredentials(string failure, SamsungIpRemoteOutcome outcome)
    {
        using var handler = new ProbeHandler(_ => throw new HttpRequestException("Simulated transport failure"))
        {
            Status = failure == "unauthorized" ? HttpStatusCode.Unauthorized : failure == "http-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            RawReply = failure == "transport" ? null : failure == "oversized" ? new string('x', 1024 * 1024 + 1) : "not JSON " + Token
        };
        using var http = new HttpClient(handler);
        using var client = Client(http);
        var result = await client.ProbeReadBatchAsync(Options);
        Assert.Equal(outcome, result.Outcome);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }
}
