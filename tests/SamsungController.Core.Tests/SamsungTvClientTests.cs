using System.Text;
using System.Text.Json;
using SamsungController.Core.Connection;
using SamsungController.Core.Devices;
using SamsungController.Core.Protocol;
using SamsungController.Core.Tests.Fakes;

namespace SamsungController.Core.Tests;

public sealed class SamsungTvClientTests
{
    [Fact]
    public async Task ConnectExtractsAndPersistsDirectToken()
    {
        var transport = new FakeSamsungTransport { PairingToken = "direct-token" };
        var tokens = new InMemoryTokenStore();
        await using var client = new SamsungTvClient(transport, tokens);

        await client.ConnectAsync(CreateOptions());

        Assert.Equal(SamsungConnectionState.Connected, client.State);
        Assert.Equal("direct-token", client.Token);
        Assert.Equal("direct-token", await tokens.LoadAsync("192.0.2.10"));
        Assert.Equal("wss", transport.LastEndpoint?.Scheme);
        Assert.Equal(8002, transport.LastEndpoint?.Port);
        Assert.Contains("name=", transport.LastEndpoint?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectExtractsTokenFromClientAttributes()
    {
        var transport = new FakeSamsungTransport
        {
            PairingToken = "attribute-token",
            ReturnTokenInClientAttributes = true
        };
        var tokens = new InMemoryTokenStore();
        await using var client = new SamsungTvClient(transport, tokens);

        await client.ConnectAsync(CreateOptions());

        Assert.Equal("attribute-token", client.Token);
        Assert.Equal("attribute-token", await tokens.LoadAsync("192.0.2.10"));
    }

    [Theory]
    [InlineData(RemoteKeyAction.Click, "Click")]
    [InlineData(RemoteKeyAction.Press, "Press")]
    [InlineData(RemoteKeyAction.Release, "Release")]
    public async Task SendKeyCreatesExpectedSamsungPayload(
        RemoteKeyAction action,
        string expectedCommand)
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions());

        await client.SendKeyAsync("KEY_WHATEVER", action);

        var rawJson = Assert.Single(transport.SentMessages);
        using var json = JsonDocument.Parse(rawJson);
        Assert.Equal("ms.remote.control", json.RootElement.GetProperty("method").GetString());
        var parameters = json.RootElement.GetProperty("params");
        Assert.Equal(expectedCommand, parameters.GetProperty("Cmd").GetString());
        Assert.Equal("KEY_WHATEVER", parameters.GetProperty("DataOfCmd").GetString());
        Assert.Equal("false", parameters.GetProperty("Option").GetString());
        Assert.Equal("SendRemoteKey", parameters.GetProperty("TypeOfRemote").GetString());
    }

    [Fact]
    public async Task UnknownInboundMessageIsPreserved()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions());
        var received = new TaskCompletionSource<SamsungMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, eventArgs) =>
        {
            if (eventArgs.Message.Event == "com.samsung.unknown")
            {
                received.TrySetResult(eventArgs.Message);
            }
        };
        const string rawJson = "{\"event\":\"com.samsung.unknown\",\"data\":{\"answer\":42}}";

        transport.EnqueueInbound(rawJson);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(rawJson, message.RawJson);
        Assert.Equal(42, message.ParsedPayload?["data"]?["answer"]?.GetValue<int>());
        Assert.Null(message.ParseError);
    }

    [Fact]
    public async Task SendFailureReconnectsWithoutRetryingAmbiguousCommand()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions());
        transport.FailNextSend = true;

        var exception = await Assert.ThrowsAsync<SamsungConnectionException>(
            () => client.SendKeyAsync("KEY_UP"));

        Assert.Equal(2, transport.ConnectCount);
        Assert.Empty(transport.SentMessages);
        Assert.Contains("was not retried", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SamsungConnectionState.Connected, client.State);
        Assert.Equal(2, client.ConnectionGeneration);
    }

    [Fact]
    public async Task ConnectPassesPingPongSettingsAndReportsWarmupState()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        var states = new List<SamsungConnectionState>();
        client.ConnectionStateChanged += (_, eventArgs) => states.Add(eventArgs.Current);

        await client.ConnectAsync(CreateOptions() with
        {
            KeepAliveInterval = TimeSpan.FromSeconds(17),
            KeepAliveTimeout = TimeSpan.FromSeconds(6),
            PostConnectWarmup = TimeSpan.FromMilliseconds(10)
        });

        Assert.Equal(TimeSpan.FromSeconds(17), transport.LastKeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), transport.LastKeepAliveTimeout);
        Assert.Contains(SamsungConnectionState.Warming, states);
        Assert.Equal(SamsungConnectionState.Connected, states[^1]);
    }

    [Fact]
    public async Task FirstCommandAfterIdleReconnectsThenSendsExactlyOnce()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions() with
        {
            ReconnectAfterIdle = TimeSpan.FromMilliseconds(1)
        });
        await Task.Delay(10);

        await client.SendKeyAsync("KEY_MENU");

        Assert.Equal(2, transport.ConnectCount);
        Assert.Single(transport.SentMessages);
    }

    [Fact]
    public async Task OnlyOneCommandCanWaitForConnectionWarmup()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        var warming = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, eventArgs) =>
        {
            if (eventArgs.Current == SamsungConnectionState.Warming)
            {
                warming.TrySetResult();
            }
        };
        var connect = client.ConnectAsync(CreateOptions() with
        {
            PostConnectWarmup = TimeSpan.FromMilliseconds(50)
        });
        await warming.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var first = client.SendKeyAsync("KEY_MENU");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendKeyAsync("KEY_DOWN"));
        await connect;
        await first;

        Assert.Contains("already waiting", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(transport.SentMessages);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task UnexpectedConnectionLossDisconnectsWhenReconnectIsDisabled()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions() with { AutoReconnect = false });
        var disconnected = new TaskCompletionSource<SamsungConnectionStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, eventArgs) =>
        {
            if (eventArgs.Current == SamsungConnectionState.Disconnected)
            {
                disconnected.TrySetResult(eventArgs);
            }
        };

        transport.LoseConnection();

        var stateChange = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SamsungConnectionState.Disconnected, client.State);
        Assert.IsType<SamsungConnectionException>(stateChange.Error);
        Assert.Contains("closed", stateChange.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task SendFailureDisconnectsWhenReconnectIsDisabled()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions() with { AutoReconnect = false });
        transport.FailNextSend = true;

        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(
            () => client.SendKeyAsync("KEY_UP"));

        Assert.Equal(SamsungConnectionState.Disconnected, client.State);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task ApplicationNameIsBase64EncodedInEndpoint()
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());

        await client.ConnectAsync(CreateOptions() with { ApplicationName = "Samsung Controller" });

        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes("Samsung Controller"));
        Assert.Contains(
            $"name={Uri.EscapeDataString(encodedName)}",
            transport.LastEndpoint?.Query,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TvPairingTimeoutIsReportedWithoutReconnect()
    {
        var transport = new FakeSamsungTransport
        {
            HandshakeEvent = "ms.channel.timeOut"
        };
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        var states = new List<SamsungConnectionState>();
        client.ConnectionStateChanged += (_, eventArgs) => states.Add(eventArgs.Current);

        var exception = await Assert.ThrowsAsync<SamsungPairingTimeoutException>(
            () => client.ConnectAsync(CreateOptions() with { MaxReconnectAttempts = 3 }));

        Assert.Contains("Access Notification", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, transport.ConnectCount);
        Assert.DoesNotContain(SamsungConnectionState.Reconnecting, states);
        Assert.Equal(SamsungConnectionState.Faulted, client.State);
    }

    [Theory]
    [InlineData(SamsungQuery.EdenApplications, "ed.edenApp.get")]
    [InlineData(SamsungQuery.InstalledApplications, "ed.installedApp.get")]
    public async Task SendQueryCreatesReadOnlyChannelEmitPayload(
        SamsungQuery query,
        string expectedEvent)
    {
        var transport = new FakeSamsungTransport();
        await using var client = new SamsungTvClient(transport, new InMemoryTokenStore());
        await client.ConnectAsync(CreateOptions());

        await client.SendQueryAsync(query);

        var rawJson = Assert.Single(transport.SentMessages);
        using var json = JsonDocument.Parse(rawJson);
        Assert.Equal("ms.channel.emit", json.RootElement.GetProperty("method").GetString());
        var parameters = json.RootElement.GetProperty("params");
        Assert.Equal(string.Empty, parameters.GetProperty("data").GetString());
        Assert.Equal(expectedEvent, parameters.GetProperty("event").GetString());
        Assert.Equal("host", parameters.GetProperty("to").GetString());
    }

    private static SamsungConnectionOptions CreateOptions() => new()
    {
        Host = "192.0.2.10",
        PairingTimeout = TimeSpan.FromSeconds(2),
        MaxReconnectAttempts = 0,
        PostConnectWarmup = TimeSpan.Zero,
        ReconnectAfterIdle = TimeSpan.Zero
    };
}
