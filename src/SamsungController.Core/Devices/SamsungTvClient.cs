using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SamsungController.Core.Connection;
using SamsungController.Core.Diagnostics;
using SamsungController.Core.Protocol;

namespace SamsungController.Core.Devices;

public sealed class SamsungTvClient : IAsyncDisposable
{
    private readonly ISamsungTransport _transport;
    private readonly ISamsungTokenStore _tokenStore;
    private readonly IReadOnlyList<ISamsungMessageSink> _messageSinks;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Channel<SamsungMessage> _messageChannel = Channel.CreateUnbounded<SamsungMessage>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });
    private readonly ConcurrentDictionary<long, byte> _reconnectSchedules = new();
    private readonly ConcurrentDictionary<long, byte> _authorizedGenerations = new();

    private CancellationTokenSource? _sessionSource;
    private Task? _receiveTask;
    private SamsungConnectionOptions? _options;
    private TaskCompletionSource<string?>? _handshakeSource;
    private int _state = (int)SamsungConnectionState.Disconnected;
    private int _sendPending;
    private long _activeGeneration;
    private long _lastApplicationActivityUnixMilliseconds;
    private bool _disposed;

    public SamsungTvClient(
        ISamsungTransport transport,
        ISamsungTokenStore tokenStore,
        IEnumerable<ISamsungMessageSink>? messageSinks = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _messageSinks = messageSinks?.ToArray() ?? [];
    }

    public event EventHandler<SamsungConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<SamsungMessageEventArgs>? MessageObserved;

    public event EventHandler<SamsungMessageEventArgs>? MessageReceived;

    public SamsungConnectionState State =>
        (SamsungConnectionState)Volatile.Read(ref _state);

    public string? Token { get; private set; }

    public long ConnectionGeneration => Interlocked.Read(ref _activeGeneration);

    public Task ConnectAsync(
        SamsungConnectionOptions options,
        CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(options, forceReconnect: false, cancellationToken);

    private async Task ConnectCoreAsync(
        SamsungConnectionOptions options,
        bool forceReconnect,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceReconnect
                && State == SamsungConnectionState.Connected
                && _transport.IsConnected)
            {
                return;
            }

            _options = options;
            var token = options.Token
                ?? Token
                ?? await _tokenStore.LoadAsync(options.Host, cancellationToken).ConfigureAwait(false);

            await StopCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            SetState(State == SamsungConnectionState.Reconnecting
                ? SamsungConnectionState.Reconnecting
                : SamsungConnectionState.Connecting);

            var endpoint = SamsungProtocol.BuildRemoteControlEndpoint(
                options.Host,
                options.ApplicationName,
                options.Secure,
                options.Port,
                token);

            try
            {
                await _transport.ConnectAsync(
                        endpoint,
                        options.ConnectTimeout,
                        options.AllowUntrustedCertificate,
                        options.KeepAliveInterval,
                        options.KeepAliveTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                var generation = _transport.Generation;
                Interlocked.Exchange(ref _activeGeneration, generation);
                _handshakeSource = new TaskCompletionSource<string?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _sessionSource = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(generation, _sessionSource.Token);
                SetState(SamsungConnectionState.Pairing);

                string? returnedToken;
                try
                {
                    returnedToken = await _handshakeSource.Task
                        .WaitAsync(options.PairingTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw new SamsungPairingTimeoutException(
                        "Timed out waiting for Samsung TV authorization. Approve the TV pairing prompt and try again.",
                        exception);
                }

                if (!_transport.IsConnected)
                {
                    throw new SamsungConnectionException(
                        "The Samsung TV closed the WebSocket immediately after authorization.");
                }

                Token = returnedToken ?? token;
                if (!string.IsNullOrWhiteSpace(returnedToken))
                {
                    await _tokenStore.SaveAsync(options.Host, returnedToken, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (options.PostConnectWarmup > TimeSpan.Zero)
                {
                    SetState(SamsungConnectionState.Warming);
                    await Task.Delay(options.PostConnectWarmup, cancellationToken)
                        .ConfigureAwait(false);
                    if (!_transport.IsConnected)
                    {
                        throw new SamsungConnectionException(
                            "The Samsung TV connection ended while the channel was warming up.");
                    }
                }

                MarkApplicationActivity();
                SetState(SamsungConnectionState.Connected);
            }
            catch (OperationCanceledException)
            {
                SetState(SamsungConnectionState.Disconnected);
                await StopCurrentSessionAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SetState(SamsungConnectionState.Faulted, exception);
                await StopCurrentSessionAsync(CancellationToken.None).ConfigureAwait(false);
                throw exception is SamsungConnectionException
                    ? exception
                    : new SamsungConnectionException(
                        $"Could not connect to Samsung TV at {options.Host}.",
                        exception);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task SendKeyAsync(
        string key,
        RemoteKeyAction action = RemoteKeyAction.Click,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var payload = SamsungProtocol.CreateRemoteKeyPayload(key.Trim(), action);
        await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendQueryAsync(
        SamsungQuery query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = SamsungProtocol.CreateQueryPayload(query);
        await SendRawAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendRawAsync(
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        if (Interlocked.CompareExchange(ref _sendPending, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Another TV command is already waiting for the connection to become ready.");
        }

        var gateAcquired = false;
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EmitAsync(
                        SamsungProtocol.ParseMessage(
                            rawJson,
                            SamsungMessageDirection.Tx,
                            ConnectionGeneration),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _transport.SendAsync(rawJson, cancellationToken).ConfigureAwait(false);
                MarkApplicationActivity();
            }
            catch (Exception exception) when (IsReconnectable(exception))
            {
                if (_options?.AutoReconnect == true)
                {
                    SetState(SamsungConnectionState.Reconnecting, exception);
                    await ConnectCoreAsync(
                            _options with { Token = Token },
                            forceReconnect: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    throw new SamsungConnectionException(
                        "The TV connection was refreshed after the send failed. The command was not retried because delivery could not be confirmed; send it again if the TV did not respond.",
                        exception);
                }
                else
                {
                    SetState(SamsungConnectionState.Disconnected, exception);
                    Interlocked.Increment(ref _activeGeneration);
                    await StopCurrentSessionAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _sendGate.Release();
            }

            Volatile.Write(ref _sendPending, 0);
        }
    }

    public async IAsyncEnumerable<SamsungMessage> Messages(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _messageChannel.Reader
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(SamsungConnectionState.Disconnecting);
            Interlocked.Increment(ref _activeGeneration);
            await StopCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
            SetState(SamsungConnectionState.Disconnected);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(long generation, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var rawJson in _transport.ReceiveAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                var message = SamsungProtocol.ParseMessage(
                    rawJson,
                    SamsungMessageDirection.Rx,
                    generation);
                await EmitAsync(message, cancellationToken).ConfigureAwait(false);
                ProcessHandshakeMessage(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
            _handshakeSource?.TrySetException(exception);
        }
        finally
        {
            var wasAuthorized = _authorizedGenerations.TryRemove(generation, out _);
            if (_handshakeSource?.Task.IsCompleted != true)
            {
                _handshakeSource?.TrySetException(new SamsungConnectionException(
                    "The Samsung TV closed the connection before authorization completed."));
            }

            if (!_disposed
                && !cancellationToken.IsCancellationRequested
                && generation == Interlocked.Read(ref _activeGeneration)
                && wasAuthorized)
            {
                var connectionLoss = failure ?? new SamsungConnectionException(
                    "The Samsung TV closed the control connection.");
                if (_options is { AutoReconnect: true, MaxReconnectAttempts: > 0 })
                {
                    SetState(SamsungConnectionState.Reconnecting, connectionLoss);
                    ScheduleReconnect(generation);
                }
                else
                {
                    SetState(SamsungConnectionState.Disconnected, connectionLoss);
                }
            }
        }
    }

    private void ProcessHandshakeMessage(SamsungMessage message)
    {
        if (message.Event == SamsungProtocol.ChannelUnauthorizedEvent)
        {
            _handshakeSource?.TrySetException(new SamsungAuthenticationException(
                "The Samsung TV rejected authorization. Forget the controller on the TV or remove the saved token and pair again."));
            return;
        }

        if (message.Event == SamsungProtocol.ChannelTimeoutEvent)
        {
            _handshakeSource?.TrySetException(new SamsungPairingTimeoutException(
                "The TV timed out waiting for pairing approval. On the TV, enable Access Notification in Device Connect Manager, remove any denied SamsungController entry from Device List, retry, and select Allow on the prompt.",
                new TimeoutException("Samsung returned ms.channel.timeOut.")));
            return;
        }

        if (message.Event != SamsungProtocol.ChannelConnectEvent)
        {
            return;
        }

        _authorizedGenerations.TryAdd(message.ConnectionGeneration, 0);
        _handshakeSource?.TrySetResult(SamsungProtocol.TryGetToken(message.ParsedPayload));
    }

    private void ScheduleReconnect(long failedGeneration)
    {
        if (!_reconnectSchedules.TryAdd(failedGeneration, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var options = _options;
                if (options is null)
                {
                    return;
                }

                for (var attempt = 1; attempt <= options.MaxReconnectAttempts; attempt++)
                {
                    if (_disposed || State is SamsungConnectionState.Disconnecting or SamsungConnectionState.Disconnected)
                    {
                        return;
                    }

                    var delay = TimeSpan.FromMilliseconds(Math.Min(8000, 250 * Math.Pow(2, attempt - 1)));
                    await Task.Delay(delay).ConfigureAwait(false);

                    try
                    {
                        await ConnectAsync(options with { Token = Token }, CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch (Exception) when (attempt < options.MaxReconnectAttempts)
                    {
                        // Retry below; each failure is surfaced through connection state.
                    }
                    catch (Exception exception)
                    {
                        SetState(SamsungConnectionState.Faulted, exception);
                    }
                }
            }
            finally
            {
                _reconnectSchedules.TryRemove(failedGeneration, out _);
            }
        });
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var connected = State == SamsungConnectionState.Connected && _transport.IsConnected;
        if (connected && !ConnectionIsIdle())
        {
            return;
        }

        if (_options is null)
        {
            throw new InvalidOperationException("ConnectAsync must be called before sending a command.");
        }

        if (State is SamsungConnectionState.Connecting
            or SamsungConnectionState.Pairing
            or SamsungConnectionState.Warming
            or SamsungConnectionState.Reconnecting)
        {
            await ConnectCoreAsync(
                    _options with { Token = Token },
                    forceReconnect: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        SetState(SamsungConnectionState.Reconnecting);
        await ConnectCoreAsync(
                _options with { Token = Token },
                forceReconnect: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool ConnectionIsIdle()
    {
        var threshold = _options?.ReconnectAfterIdle ?? TimeSpan.Zero;
        if (threshold <= TimeSpan.Zero)
        {
            return false;
        }

        var lastActivity = Interlocked.Read(ref _lastApplicationActivityUnixMilliseconds);
        return lastActivity > 0
            && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastActivity
                >= threshold.TotalMilliseconds;
    }

    private void MarkApplicationActivity() => Interlocked.Exchange(
        ref _lastApplicationActivityUnixMilliseconds,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private async ValueTask EmitAsync(
        SamsungMessage message,
        CancellationToken cancellationToken)
    {
        foreach (var sink in _messageSinks)
        {
            await sink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        await _messageChannel.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);

        var eventArgs = new SamsungMessageEventArgs(message);
        InvokeSafely(MessageObserved, eventArgs);
        if (message.Direction == SamsungMessageDirection.Rx)
        {
            InvokeSafely(MessageReceived, eventArgs);
        }
    }

    private void SetState(SamsungConnectionState next, Exception? error = null)
    {
        var previous = (SamsungConnectionState)Interlocked.Exchange(ref _state, (int)next);
        if (previous == next && error is null)
        {
            return;
        }

        try
        {
            ConnectionStateChanged?.Invoke(
                this,
                new SamsungConnectionStateChangedEventArgs(previous, next, error));
        }
        catch
        {
            // Consumer callbacks cannot corrupt protocol state.
        }
    }

    private void InvokeSafely(
        EventHandler<SamsungMessageEventArgs>? handler,
        SamsungMessageEventArgs eventArgs)
    {
        try
        {
            handler?.Invoke(this, eventArgs);
        }
        catch
        {
            // Consumer callbacks cannot interrupt receive or diagnostic capture.
        }
    }

    private async Task StopCurrentSessionAsync(CancellationToken cancellationToken)
    {
        var source = Interlocked.Exchange(ref _sessionSource, null);
        var receiveTask = Interlocked.Exchange(ref _receiveTask, null);
        if (source is not null)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }

        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected after cancelling the session above.
            }
        }

        source?.Dispose();
        _handshakeSource = null;
    }

    private static bool IsReconnectable(Exception exception) =>
        exception is WebSocketException
            or IOException
            or InvalidOperationException;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _messageChannel.Writer.TryComplete();
        await _transport.DisposeAsync().ConfigureAwait(false);
        _connectionGate.Dispose();
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
