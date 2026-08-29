using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using SamsungController.Core.Connection;

namespace SamsungController.Core.Tests.Fakes;

internal sealed class FakeSamsungTransport : ISamsungTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private long _generation;

    public bool IsConnected { get; private set; }

    public long Generation => Interlocked.Read(ref _generation);

    public int ConnectCount { get; private set; }

    public Uri? LastEndpoint { get; private set; }

    public TimeSpan LastKeepAliveInterval { get; private set; }

    public TimeSpan LastKeepAliveTimeout { get; private set; }

    public string? PairingToken { get; set; } = "12345678";

    public string HandshakeEvent { get; set; } = "ms.channel.connect";

    public bool ReturnTokenInClientAttributes { get; set; }

    public bool FailNextSend { get; set; }

    public List<string> SentMessages { get; } = [];

    public Task ConnectAsync(
        Uri endpoint,
        TimeSpan timeout,
        bool allowUntrustedCertificate,
        TimeSpan keepAliveInterval,
        TimeSpan keepAliveTimeout,
        CancellationToken cancellationToken = default)
    {
        LastEndpoint = endpoint;
        LastKeepAliveInterval = keepAliveInterval;
        LastKeepAliveTimeout = keepAliveTimeout;
        ConnectCount++;
        IsConnected = true;
        Interlocked.Increment(ref _generation);

        var response = HandshakeEvent != "ms.channel.connect"
            ? JsonSerializer.Serialize(new { @event = HandshakeEvent })
            : ReturnTokenInClientAttributes
            ? JsonSerializer.Serialize(new
            {
                @event = "ms.channel.connect",
                data = new
                {
                    clients = new[]
                    {
                        new { attributes = new { token = PairingToken } }
                    }
                }
            })
            : JsonSerializer.Serialize(new
            {
                @event = "ms.channel.connect",
                data = new { token = PairingToken }
            });
        _inbound.Writer.TryWrite(response);
        return Task.CompletedTask;
    }

    public Task SendAsync(string rawJson, CancellationToken cancellationToken = default)
    {
        if (FailNextSend)
        {
            FailNextSend = false;
            IsConnected = false;
            throw new WebSocketException("Simulated disconnect.");
        }

        if (!IsConnected)
        {
            throw new InvalidOperationException("Fake transport is disconnected.");
        }

        SentMessages.Add(rawJson);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _inbound.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void EnqueueInbound(string rawJson) => _inbound.Writer.TryWrite(rawJson);

    public void LoseConnection()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
