using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace SamsungController.Core.Connection;

public sealed class ClientWebSocketSamsungTransport : ISamsungTransport
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private long _generation;
    private bool _disposed;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public long Generation => Interlocked.Read(ref _generation);

    public async Task ConnectAsync(
        Uri endpoint,
        TimeSpan timeout,
        bool allowUntrustedCertificate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        if (allowUntrustedCertificate && endpoint.Scheme == Uri.UriSchemeWss)
        {
            socket.Options.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(endpoint, timeoutSource.Token).ConfigureAwait(false);
            _socket = socket;
            Interlocked.Increment(ref _generation);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Timed out connecting to {endpoint.Host}.", exception);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string rawJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("The Samsung WebSocket is not connected.");
        }

        var bytes = Encoding.UTF8.GetBytes(rawJson);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await socket.SendAsync(
                    bytes.AsMemory(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async IAsyncEnumerable<string> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("The Samsung WebSocket is not connected.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var writer = new ArrayBufferWriter<byte>();

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                writer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        yield break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        throw new WebSocketException(
                            WebSocketError.InvalidMessageType,
                            $"Unexpected Samsung WebSocket message type: {result.MessageType}.");
                    }

                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                yield return Encoding.UTF8.GetString(writer.WrittenSpan);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "SamsungController disconnecting",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The peer is already gone; disposal below completes local cleanup.
        }
        finally
        {
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
