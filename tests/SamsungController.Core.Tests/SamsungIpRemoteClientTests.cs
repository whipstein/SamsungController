using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.Devices;
using SamsungController.Core.IpRemote;

namespace SamsungController.Core.Tests;

public sealed class SamsungIpRemoteClientTests
{
    private static readonly SamsungIpRemoteOptions Options = new() { Host = "192.0.2.10" };
    private const string Token = "private-ip-credential";

    [Fact]
    public async Task PairThenReadUsesSeparateRootEndpointAndJsonRpcTokenParameter()
    {
        var tokens = new MemoryTokens();
        tokens.Values[Options.Host] = "old-websocket-token";
        var handler = new RpcHandler((request, _) => Task.FromResult(Reply(request, request["method"]!.GetValue<string>() == "createAccessToken"
            ? new JsonObject { ["AccessToken"] = Token, ["echo"] = Token }
            : new JsonObject { ["volume"] = 12, ["mute"] = false, ["extra"] = new JsonArray(1, null, "unknown") })));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(tokens, http);
        Assert.False(await client.HasTokenAsync(Options));
        var pairing = await client.PairAsync(Options);
        var read = await client.ReadAsync(Options, "getTVStates");
        Assert.True(pairing.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal(Token, tokens.Values[Options.Endpoint.AbsoluteUri]);
        Assert.Equal("old-websocket-token", tokens.Values[Options.Host]);
        Assert.Null(pairing.Result);
        Assert.Null(handler.Requests[0]["params"]);
        Assert.Equal(Token, handler.Requests[1]["params"]!["AccessToken"]!.GetValue<string>());
        Assert.Equal(1, handler.Requests[0]["id"]!.GetValue<int>());
        Assert.Equal(2, handler.Requests[1]["id"]!.GetValue<int>());
        Assert.All(handler.Endpoints, endpoint => Assert.Equal("https://192.0.2.10:1516/", endpoint.AbsoluteUri));
        Assert.Equal(12, read.Result!["volume"]!.GetValue<int>());
        Assert.False(read.Result["mute"]!.GetValue<bool>());
        Assert.Null(read.Result["extra"]![1]);
        Assert.False(read.Result.ContainsKey("brightness"));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(new[] { pairing, read }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenIsIsolatedByHostAndPortAndReadWithoutOneMakesNoRequest()
    {
        var tokens = PairedTokens();
        var handler = new RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject())));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(tokens, http);
        Assert.Equal(SamsungIpRemoteOutcome.NotPaired, (await client.ReadAsync(Options with { Port = 1515 }, "getTVStates")).Outcome);
        Assert.Equal(SamsungIpRemoteOutcome.NotPaired, (await client.ReadAsync(Options with { Host = "192.0.2.11" }, "getTVStates")).Outcome);
        Assert.True(await client.HasTokenAsync(Options));
        await client.ForgetTokenAsync(Options with { Port = 1515 });
        Assert.True(await client.HasTokenAsync(Options));
        await client.ForgetTokenAsync(Options);
        Assert.False(await client.HasTokenAsync(Options));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PairingAndBothGettersAcceptMatchingTextIdsWithoutExposingTheToken()
    {
        var tokens = new MemoryTokens();
        var handler = new RpcHandler((request, _) => Task.FromResult(JsonResponse(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.ToJsonString(),
            ["result"] = request["method"]!.GetValue<string>() == "createAccessToken"
                ? new JsonObject { ["AccessToken"] = Token, ["echo"] = Token }
                : new JsonObject { ["brightness"] = 20, ["echo"] = Token }
        })));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(tokens, http);
        var exchanges = new[] { await client.PairAsync(Options), await client.ReadAsync(Options, "getTVStates"), await client.ReadAsync(Options, "getVideoStates") };
        Assert.True(await client.HasTokenAsync(Options));
        Assert.Equal(Token, tokens.Values[Options.Endpoint.AbsoluteUri]);
        Assert.All(exchanges, exchange =>
        {
            Assert.True(exchange.IsSuccess, exchange.Message);
            Assert.Equal(exchange.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonNode.Parse(exchange.ResponseJson!)!["id"]!.GetValue<string>());
            Assert.DoesNotContain(Token, JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
        });
        Assert.Equal(20, exchanges[1].Result!["brightness"]!.GetValue<int>());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("\"99\"")]
    [InlineData("\"01\"")]
    [InlineData("\"1.0\"")]
    [InlineData("\" 1 \"")]
    [InlineData("\"private-ip-credential\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("missing")]
    public async Task InvalidPairingIdsStillRejectTokenReplacementAndStayRedacted(string responseId)
    {
        var tokens = PairedTokens();
        var handler = new RpcHandler((_, _) =>
        {
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject { ["AccessToken"] = "replacement-credential" } };
            if (responseId != "missing") reply["id"] = JsonNode.Parse(responseId);
            return Task.FromResult(JsonResponse(reply));
        });
        using var http = new HttpClient(handler);
        var exchange = await new SamsungIpRemoteClient(tokens, http).PairAsync(Options);
        Assert.Equal(SamsungIpRemoteOutcome.ProtocolError, exchange.Outcome);
        Assert.Equal(Token, tokens.Values[Options.Endpoint.AbsoluteUri]);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-credential", JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MatchingTextIdDoesNotTurnAnRpcErrorIntoSuccess()
    {
        using var http = new HttpClient(new RpcHandler((request, _) => Task.FromResult(JsonResponse(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.ToJsonString(),
            ["error"] = new JsonObject { ["code"] = -32010, ["message"] = Token }
        }))));
        var exchange = await new SamsungIpRemoteClient(PairedTokens(), http).ReadAsync(Options, "getTVStates");
        Assert.Equal(SamsungIpRemoteOutcome.Unauthorized, exchange.Outcome);
        Assert.Null(exchange.Result);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(exchange), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setVideoStates")]
    [InlineData("brightnessControl")]
    [InlineData("createAccessToken")]
    [InlineData("getUnknownStates")]
    [InlineData("GETTVSTATES")]
    public async Task ArbitraryMethodsAndWritesAreRejectedBeforeSending(string method)
    {
        var handler = new RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject())));
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(PairedTokens(), http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadAsync(Options, method));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(-32010, SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData(-32001, SamsungIpRemoteOutcome.Unsupported)]
    [InlineData(-32601, SamsungIpRemoteOutcome.Unsupported)]
    [InlineData(-32002, SamsungIpRemoteOutcome.RpcError)]
    public async Task RpcErrorsAreClassifiedAndTokenEchoesAreRedactedWithoutRetry(int code, SamsungIpRemoteOutcome expected)
    {
        var handler = new RpcHandler((request, _) => Task.FromResult(JsonResponse(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = $"bad credential {Token}", ["AccessToken"] = Token }
        })));
        using var http = new HttpClient(handler);
        var tokens = PairedTokens();
        var result = await new SamsungIpRemoteClient(tokens, http).ReadAsync(Options, "getVideoStates");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(code, result.RpcErrorCode);
        Assert.Null(result.Result);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(Token, tokens.Values[Options.Endpoint.AbsoluteUri]);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not-json private-ip-credential")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[],\"error\":{}}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}")]
    public async Task InvalidRepliesNeverInventValues(string body)
    {
        var handler = new RpcHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        var result = await new SamsungIpRemoteClient(PairedTokens(), http).ReadAsync(Options, "getVideoStates");
        Assert.Equal(SamsungIpRemoteOutcome.ProtocolError, result.Outcome);
        Assert.Null(result.Result);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EmptyObjectIsObservedButMissingFieldsStayMissing()
    {
        using var http = new HttpClient(new RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject()))));
        var result = await new SamsungIpRemoteClient(PairedTokens(), http).ReadAsync(Options, "getVideoStates");
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Result!);
    }

    [Theory]
    [InlineData(401, SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData(403, SamsungIpRemoteOutcome.Unauthorized)]
    [InlineData(302, SamsungIpRemoteOutcome.HttpError)]
    [InlineData(500, SamsungIpRemoteOutcome.HttpError)]
    public async Task HttpFailuresRemainFailuresAndRawTextDoesNotLeak(int status, SamsungIpRemoteOutcome expected)
    {
        var handler = new RpcHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Token) }));
        using var http = new HttpClient(handler);
        var result = await new SamsungIpRemoteClient(PairedTokens(), http).ReadAsync(Options, "getTVStates");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.HttpStatus);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PairingDoesNotSaveBadReplyOrExposeUnknownCredentialFields()
    {
        var tokens = PairedTokens();
        using var http = new HttpClient(new RpcHandler((request, _) => Task.FromResult(Reply(request,
            new JsonObject { ["unknownCredential"] = "new-secret", ["echo"] = "new-secret", ["AccessToken"] = "" }))));
        var result = await new SamsungIpRemoteClient(tokens, http).PairAsync(Options);
        Assert.Equal(SamsungIpRemoteOutcome.ProtocolError, result.Outcome);
        Assert.Equal(Token, tokens.Values[Options.Endpoint.AbsoluteUri]);
        Assert.DoesNotContain("new-secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenStorageFailureIsNotPairingSuccess()
    {
        using var http = new HttpClient(new RpcHandler((request, _) => Task.FromResult(Reply(request, new JsonObject { ["AccessToken"] = Token }))));
        var result = await new SamsungIpRemoteClient(new MemoryTokens { FailSave = true }, http).PairAsync(Options);
        Assert.Equal(SamsungIpRemoteOutcome.StorageError, result.Outcome);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutAndUserCancellationHaveDifferentOutcomesAndNoRetries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RpcHandler(async (_, cancellation) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(PairedTokens(), http);
        Assert.Equal(SamsungIpRemoteOutcome.Timeout, (await client.ReadAsync(Options with { RequestTimeout = TimeSpan.FromMilliseconds(30) }, "getTVStates")).Outcome);
        using var cancellation = new CancellationTokenSource();
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = client.ReadAsync(Options, "getVideoStates", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(SamsungIpRemoteOutcome.Canceled, (await pending).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TransportErrorsAndOversizeResponsesAreBounded()
    {
        using var brokenHttp = new HttpClient(new RpcHandler((_, _) => throw new HttpRequestException(Token)));
        var failure = await new SamsungIpRemoteClient(PairedTokens(), brokenHttp).ReadAsync(Options, "getTVStates");
        Assert.Equal(SamsungIpRemoteOutcome.TransportError, failure.Outcome);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(failure), StringComparison.Ordinal);
        using var largeHttp = new HttpClient(new RpcHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(new string('x', 1024 * 1024 + 1)) })));
        var large = await new SamsungIpRemoteClient(PairedTokens(), largeHttp).ReadAsync(Options, "getTVStates");
        Assert.Equal(SamsungIpRemoteOutcome.ProtocolError, large.Outcome);
        Assert.Null(large.ResponseJson);
    }

    [Fact]
    public async Task RequestsAreSerialized()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RpcHandler(async (request, cancellation) =>
        {
            if (request["id"]!.GetValue<long>() == 1)
            {
                firstStarted.SetResult();
                await finishFirst.Task.WaitAsync(cancellation);
            }
            return Reply(request, new JsonObject());
        });
        using var http = new HttpClient(handler);
        var client = new SamsungIpRemoteClient(PairedTokens(), http);
        var first = client.ReadAsync(Options, "getTVStates");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.ReadAsync(Options, "getVideoStates");
        Assert.Single(handler.Requests);
        finishFirst.SetResult();
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.IsSuccess));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void CertificateTrustIsDefaultStrictPinnedAndEndpointScoped()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=test-tv", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        const SslPolicyErrors errors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
        Assert.False(SamsungIpRemoteClient.IsCertificateTrusted(Options, Options.Endpoint, certificate, errors));
        var allowed = Options with { AllowUntrustedCertificate = true };
        Assert.True(SamsungIpRemoteClient.IsCertificateTrusted(allowed, Options.Endpoint, certificate, errors));
        Assert.False(SamsungIpRemoteClient.IsCertificateTrusted(allowed, new Uri("https://192.0.2.11:1516/"), certificate, errors));
        Assert.False(SamsungIpRemoteClient.IsCertificateTrusted(allowed, new Uri("https://192.0.2.10:1515/"), certificate, errors));
        Assert.False(SamsungIpRemoteClient.IsCertificateTrusted(allowed, new Uri("http://192.0.2.10:1516/"), certificate, errors));
        var pinned = Options with { CertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256) };
        Assert.True(SamsungIpRemoteClient.IsCertificateTrusted(pinned, Options.Endpoint, certificate, errors));
        Assert.False(SamsungIpRemoteClient.IsCertificateTrusted(allowed with { CertificateSha256 = new string('0', 64) }, Options.Endpoint, certificate, SslPolicyErrors.None));
    }

    [Theory]
    [InlineData("https://192.0.2.10/path")]
    [InlineData("user:password@host")]
    [InlineData("")]
    public void InvalidAddressesAreRejected(string host) => Assert.Throws<ArgumentException>(() => (Options with { Host = host }).Endpoint);

    [Fact]
    public async Task PrivateStorePersistsSeparateCredentialsAndRestrictsUnixPermissions()
    {
        var directory = Directory.CreateTempSubdirectory("SamsungController-IP-tokens-").FullName;
        try
        {
            var store = new PrivateIpRemoteTokenStore(Path.Combine(directory, "ip-remote"));
            await store.SaveAsync(Options.Endpoint.AbsoluteUri, Token);
            var reloaded = new PrivateIpRemoteTokenStore(Path.Combine(directory, "ip-remote"));
            Assert.Equal(Token, await reloaded.LoadAsync(Options.Endpoint.AbsoluteUri));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(directory, "ip-remote")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "ip-remote", "tokens.json")));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static MemoryTokens PairedTokens()
    {
        var tokens = new MemoryTokens();
        tokens.Values[Options.Endpoint.AbsoluteUri] = Token;
        return tokens;
    }
    private sealed class MemoryTokens : ISamsungTokenStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailSave { get; init; }
        public Task<string?> LoadAsync(string host, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(host));
        public Task SaveAsync(string host, string token, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("cannot save");
            Values[host] = token;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string host, CancellationToken cancellationToken = default) { Values.Remove(host); return Task.CompletedTask; }
    }
    private sealed class RpcHandler(Func<JsonObject, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<JsonObject> Requests { get; } = [];
        public List<Uri> Endpoints { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.Null(request.Headers.Authorization);
            var json = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            Requests.Add(json); Endpoints.Add(request.RequestUri!);
            return await respond(json, cancellationToken);
        }
    }
    private static HttpResponseMessage Reply(JsonObject request, JsonObject result) => JsonResponse(new JsonObject
    { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result });
    private static HttpResponseMessage JsonResponse(JsonObject json) => new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString()) };
}
