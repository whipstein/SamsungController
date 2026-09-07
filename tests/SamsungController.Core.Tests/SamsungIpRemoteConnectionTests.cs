using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Core.Tests;

public sealed class SamsungIpRemoteConnectionTests
{
    [Fact]
    public async Task ReusesTlsConnectionButLoadsCredentialsAndHonorsTimeoutPerRequest()
    {
        await using var tv = new LocalTv();
        var tokens = Tokens(tv);
        using var client = new SamsungIpRemoteClient(tokens);
        var first = await client.ReadAsync(tv.Options, "getTVStates");
        tokens.Values[tv.Options.Endpoint.AbsoluteUri] = "replacement-token";
        var second = await client.ReadAsync(tv.Options with { RequestTimeout = TimeSpan.FromSeconds(3) }, "getVideoStates");
        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        Assert.True(first.NewTlsHandshake);
        Assert.False(second.NewTlsHandshake);
        Assert.False(first.ServerClosesConnection);
        Assert.False(second.ServerClosesConnection);
        Assert.Equal(tv.Pin, first.ObservedCertificateSha256);
        Assert.Equal(tv.Pin, second.ObservedCertificateSha256);
        Assert.Equal(2, tv.Requests.Count);
        Assert.Single(tv.Requests.Select(item => item.Connection).Distinct());
        Assert.Equal("replacement-token", tv.Requests.Last().Request["params"]!["AccessToken"]!.ToString());
    }

    [Fact]
    public async Task ExplicitDisconnectReleasesTlsConnectionWithoutRemovingPairing()
    {
        await using var tv = new LocalTv();
        using var client = new SamsungIpRemoteClient(Tokens(tv));
        Assert.True((await client.ReadAsync(tv.Options, "getTVStates")).IsSuccess);
        client.CloseConnection();
        var next = await client.ReadAsync(tv.Options, "getTVStates");
        Assert.True(next.IsSuccess, next.Message);
        Assert.True(next.NewTlsHandshake);
        Assert.Equal(2, tv.Requests.Select(item => item.Connection).Distinct().Count());
        Assert.All(tv.Requests, item => Assert.Equal("getTVStates", item.Request["method"]!.ToString()));
    }

    [Fact]
    public async Task ServerRequestedCloseIsReportedAndNextRequestReconnectsWithoutPairing()
    {
        await using var tv = new LocalTv { CloseAfterResponse = true };
        using var client = new SamsungIpRemoteClient(Tokens(tv));
        foreach (var method in new[] { "getTVStates", "getVideoStates" })
        {
            var reply = await client.ReadAsync(tv.Options, method);
            Assert.True(reply.IsSuccess, reply.Message);
            Assert.True(reply.NewTlsHandshake);
            Assert.True(reply.ServerClosesConnection);
        }
        Assert.Equal(2, tv.Requests.Select(item => item.Connection).Distinct().Count());
        Assert.Equal(new[] { "getTVStates", "getVideoStates" }, tv.Requests.Select(item => item.Request["method"]!.ToString()));
    }

    [Fact]
    public async Task TrustPolicyAndPinChangesNeverReuseAnAlreadyTrustedConnection()
    {
        await using var tv = new LocalTv();
        using var client = new SamsungIpRemoteClient(Tokens(tv));
        Assert.True((await client.ReadAsync(tv.Options, "getTVStates")).IsSuccess);
        var strict = tv.Options with { AllowUntrustedCertificate = false };
        Assert.Equal(SamsungIpRemoteOutcome.CertificateError, (await client.ReadAsync(strict, "getTVStates")).Outcome);
        Assert.Equal(SamsungIpRemoteOutcome.CertificateError, (await client.ReadAsync(tv.Options with { CertificateSha256 = new string('0', 64) }, "getTVStates")).Outcome);
        var pinned = strict with { CertificateSha256 = tv.Pin };
        Assert.True((await client.ReadAsync(pinned, "getTVStates")).IsSuccess);
        Assert.True((await client.ReadAsync(pinned with { CertificateSha256 = tv.Pin.ToLowerInvariant() }, "getTVStates")).IsSuccess);
        Assert.Equal(3, tv.Requests.Count); // Rejected certificates never receive credentials/RPCs.
        var connections = tv.Requests.Select(item => item.Connection).ToArray();
        Assert.NotEqual(connections[0], connections[1]);
        Assert.Equal(connections[1], connections[2]);
    }

    [Fact]
    public async Task DifferentEndpointsUseIndependentConnectionsAndCredentials()
    {
        await using var first = new LocalTv();
        await using var second = new LocalTv();
        var tokens = Tokens(first);
        tokens.Values[second.Options.Endpoint.AbsoluteUri] = "second-tv-token";
        using var client = new SamsungIpRemoteClient(tokens);
        Assert.True((await client.ReadAsync(first.Options, "getTVStates")).IsSuccess);
        var reply = await client.ReadAsync(second.Options, "getTVStates");
        Assert.True(reply.IsSuccess, reply.Message);
        Assert.Equal(second.Pin, reply.ObservedCertificateSha256);
        Assert.Equal("test-token", Assert.Single(first.Requests).Request["params"]!["AccessToken"]!.ToString());
        Assert.Equal("second-tv-token", Assert.Single(second.Requests).Request["params"]!["AccessToken"]!.ToString());
        Assert.True((await client.ReadAsync(first.Options, "getTVStates")).IsSuccess);
        Assert.Equal(2, first.Requests.Select(item => item.Connection).Distinct().Count());
    }

    [Fact]
    public async Task CancellationAfterAReusedRequestDoesNotRetryAndNextExplicitReadWorks()
    {
        await using var tv = new LocalTv();
        using var client = new SamsungIpRemoteClient(Tokens(tv));
        Assert.True((await client.ReadAsync(tv.Options, "getTVStates")).IsSuccess);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tv.BeforeReply = async (_, cancellation) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        };
        using var cancellation = new CancellationTokenSource();
        var pending = client.ReadAsync(tv.Options, "getVideoStates", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(SamsungIpRemoteOutcome.Canceled, (await pending).Outcome);
        Assert.Equal(2, tv.Requests.Count);
        tv.BeforeReply = null;
        Assert.True((await client.ReadAsync(tv.Options, "getTVStates")).IsSuccess);
        Assert.Equal(3, tv.Requests.Count);
    }

    [Fact]
    public async Task DisposeDoesNotDisposeAnInjectedHttpClientAndRejectsFurtherRpcCalls()
    {
        using var http = new HttpClient(new SamsungIpRemoteClientTests.RpcHandler((request, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject() }.ToJsonString()) })));
        var options = new SamsungIpRemoteOptions { Host = "192.0.2.10" };
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens();
        tokens.Values[options.Endpoint.AbsoluteUri] = "test-token";
        using var first = new SamsungIpRemoteClient(tokens, http);
        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ReadAsync(options, "getTVStates"));
        using var second = new SamsungIpRemoteClient(tokens, http);
        Assert.True((await second.ReadAsync(options, "getTVStates")).IsSuccess);
    }

    private static SamsungIpRemoteClientTests.MemoryTokens Tokens(LocalTv tv)
    {
        var tokens = new SamsungIpRemoteClientTests.MemoryTokens();
        tokens.Values[tv.Options.Endpoint.AbsoluteUri] = "test-token";
        return tokens;
    }

    // Exercise the production HTTP handler/TLS pool, not an injected handler.
    // This fixture listens only on loopback and never contacts a real display.
    private sealed class LocalTv : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly X509Certificate2 _certificate;
        private readonly Task _accept;
        private readonly List<Task> _connections = [];
        public SamsungIpRemoteOptions Options { get; }
        public string Pin => _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public ConcurrentQueue<(int Connection, JsonObject Request)> Requests { get; } = new();
        public Func<JsonObject, CancellationToken, Task>? BeforeReply { get; set; }
        public bool CloseAfterResponse { get; init; }

        public LocalTv()
        {
            using var key = RSA.Create(2048);
            _certificate = new CertificateRequest("CN=test-tv", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            _listener.Start();
            Options = new() { Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, AllowUntrustedCertificate = true, RequestTimeout = TimeSpan.FromSeconds(5) };
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(ServeAsync(socket, _connections.Count));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient socket, int connection)
        {
            using var client = socket;
            await using var stream = new SslStream(client.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { } start)
                {
                    Assert.StartsWith("POST / HTTP/1.1", start, StringComparison.Ordinal);
                    var length = 0;
                    var keepAlive = false;
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                    {
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..], CultureInfo.InvariantCulture);
                        if (header.Equals("Connection: keep-alive", StringComparison.OrdinalIgnoreCase)) keepAlive = true;
                    }
                    Assert.True(keepAlive);
                    Assert.InRange(length, 1, 16384);
                    var buffer = new char[length]; // JSON requests use ASCII (non-ASCII is escaped).
                    var read = await reader.ReadBlockAsync(buffer, _stop.Token);
                    Assert.Equal(length, read);
                    var request = JsonNode.Parse(new string(buffer))!.AsObject();
                    Requests.Enqueue((connection, request));
                    if (BeforeReply is { } before) await before(request, _stop.Token);
                    var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject { ["volume"] = 10 } }.ToJsonString();
                    var close = CloseAfterResponse ? "Connection: close\r\n" : "";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n{close}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}"), _stop.Token);
                    if (CloseAfterResponse) return;
                }
            }
            catch (Exception error) when (error is IOException or AuthenticationException or OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept;
            _listener.Stop();
            await Task.WhenAll(_connections);
            _certificate.Dispose();
            _stop.Dispose();
        }
    }
}
