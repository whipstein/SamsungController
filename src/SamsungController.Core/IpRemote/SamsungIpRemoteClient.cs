using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.Devices;

namespace SamsungController.Core.IpRemote;

/// <summary>A separate HTTPS client with two getters and explicitly allowlisted picture writes.</summary>
public sealed partial class SamsungIpRemoteClient(ISamsungTokenStore tokenStore, HttpClient? httpClient = null) : ISamsungIpRemoteClient, IDisposable
{
    public static IReadOnlyList<string> ReadMethods { get; } = Array.AsReadOnly(new[] { "getTVStates", "getVideoStates" });
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _transportLock = new();
    private OwnedTransport? _transport;
    private bool _disposed;
    private long _requestId;
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public async Task<bool> HasTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await tokenStore.LoadAsync(options.Endpoint.AbsoluteUri, cancellationToken).ConfigureAwait(false));

    public async Task ForgetTokenAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await tokenStore.RemoveAsync(options.Endpoint.AbsoluteUri, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<SamsungIpRemoteExchange> PairAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) =>
        ExecuteAsync(options, "createAccessToken", pairing: true, cancellationToken);

    public Task<SamsungIpRemoteExchange> ReadAsync(SamsungIpRemoteOptions options, string method, CancellationToken cancellationToken = default)
    {
        if (!ReadMethods.Contains(method, StringComparer.Ordinal))
            throw new InvalidOperationException("IP Remote reads allow only getTVStates and getVideoStates. Pairing and guarded picture writes are separate explicit actions; arbitrary methods are disabled.");
        return ExecuteAsync(options, method, pairing: false, cancellationToken);
    }

    public Task<SamsungIpRemoteExchange> WritePictureControlAsync(SamsungIpRemoteOptions options, string control, int value, CancellationToken cancellationToken = default)
    {
        // Historical protocol envelope bounds only, NOT a claim about a modern
        // display's range. The guided workflow additionally requires a confirmed
        // baseline, one-step test, visual check, and restoration before direct use.
        var definition = SamsungIpRemotePictureControl.Get(control);
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "The value must be an integer from 0 to 100; the display's actual range must be checked separately.");
        return ExecuteAsync(options, definition.Method, pairing: false, cancellationToken, definition, value);
    }

    public Task<SamsungIpRemoteExchange> ExecuteCommandAsync(SamsungIpRemoteOptions options, string method, JsonObject parameters, bool query = false, CancellationToken cancellationToken = default)
    {
        var command = SamsungIpRemoteCommands.Get(method);
        var values = command.Validate(parameters, query);
        return ExecuteAsync(options, method, false, cancellationToken, commandParameters: values, catalogCommand: command);
    }

    private async Task<SamsungIpRemoteExchange> ExecuteAsync(SamsungIpRemoteOptions options, string method, bool pairing, CancellationToken cancellationToken, SamsungIpRemotePictureControl? control = null, int? requestedValue = null, JsonObject? commandParameters = null, SamsungIpRemoteCommand? catalogCommand = null, IReadOnlyList<BatchProbeCall>? batch = null)
    {
        var endpoint = options.Endpoint;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _requestId);
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        var batchIds = batch?.Select((_, index) => index == 0 ? id : Interlocked.Increment(ref _requestId)).ToArray();
        JsonNode wireRequest = batch is null ? request : new JsonArray(batch.Select((call, index) => (JsonNode)new JsonObject
            { ["jsonrpc"] = "2.0", ["id"] = batchIds![index], ["method"] = call.Method }).ToArray());
        string? responseJson = null;
        int? httpStatus = null;
        string? token = null;
        JsonNode? resultPayload = null;
        var accessingCredentials = false;
        OwnedTransport? transport = null;
        long initialHandshakes = 0;
        bool? serverClosesConnection = null;
        // A batch is an isolated experiment; never return its socket to normal
        // operation. Also discard a failed/ambiguous transport, without replaying
        // the RPC or touching the saved credential.
        var discardTransport = batch is not null;
        SamsungIpRemoteExchange Complete(SamsungIpRemoteOutcome outcome, string message, JsonObject? result = null, int? code = null)
        {
            discardTransport |= outcome is SamsungIpRemoteOutcome.Timeout or SamsungIpRemoteOutcome.Canceled
                or SamsungIpRemoteOutcome.TransportError or SamsungIpRemoteOutcome.CertificateError
                or SamsungIpRemoteOutcome.ProtocolError or SamsungIpRemoteOutcome.HttpError or SamsungIpRemoteOutcome.Unauthorized;
            return new(started, id, method, endpoint.AbsoluteUri, outcome, message,
                IpRemoteRedactor.Redact(wireRequest, token)!.ToJsonString(PrettyJson), responseJson, result,
                httpStatus, code, timer.ElapsedMilliseconds, transport?.Fingerprint)
            {
                Payload = resultPayload?.DeepClone(),
                NewTlsHandshake = transport is null ? null : transport.Handshakes != initialHandshakes,
                ServerClosesConnection = serverClosesConnection
            };
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(pairing ? options.PairingTimeout : options.RequestTimeout);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!pairing)
            {
                accessingCredentials = true;
                token = await tokenStore.LoadAsync(endpoint.AbsoluteUri, timeout.Token).ConfigureAwait(false);
                accessingCredentials = false;
                if (string.IsNullOrWhiteSpace(token))
                    return Complete(SamsungIpRemoteOutcome.NotPaired, "No separate IP Remote token is saved for this host and port. Pair explicitly first.");
                request["params"] = new JsonObject { ["AccessToken"] = token };
                if (control is not null) request["params"]![control.Id] = requestedValue!.Value;
                if (commandParameters is not null)
                    foreach (var item in commandParameters) request["params"]![item.Key] = item.Value?.DeepClone();
                if (batch is not null)
                    for (var index = 0; index < batch.Count; index++)
                    {
                        var parameters = (JsonObject)batch[index].Parameters.DeepClone();
                        parameters["AccessToken"] = token;
                        wireRequest[index]!["params"] = parameters;
                    }
            }

            var client = httpClient;
            if (client is null)
            {
                if (batch is not null) CloseConnection();
                transport = GetTransport(options);
                initialHandshakes = transport.Handshakes;
                transport.RejectedCertificate = false;
                client = transport.Client;
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.Connection.Add("keep-alive");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new StringContent(wireRequest.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            serverClosesConnection = response.Headers.ConnectionClose == true;
            var body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            JsonObject? envelope = null;
            JsonObject? safeEnvelope = null;
            JsonNode? parsed = null;
            JsonNode? safeParsed = null;
            try
            {
                parsed = JsonNode.Parse(body);
                envelope = parsed as JsonObject;
                // Pairing text is always masked, even an unexpected reply, so a
                // newly issued credential cannot escape via an error/echo field.
                safeParsed = IpRemoteRedactor.Redact(parsed, token, pairing);
                safeEnvelope = safeParsed as JsonObject;
                // Preserve only a response ID that we can prove is our own
                // public request ID. All other pairing strings remain masked.
                if (safeEnvelope is not null && MatchesRequestId(envelope?["id"], id))
                    safeEnvelope["id"] = envelope!["id"]!.DeepClone();
                responseJson = (batch is not null ? safeParsed : safeEnvelope)?.ToJsonString(PrettyJson) ?? "[Response is not a JSON object; content omitted for credential safety.]";
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                responseJson = "[Malformed response omitted for credential safety.]";
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Complete(SamsungIpRemoteOutcome.Unauthorized, "The TV rejected IP Remote authorization. No automatic re-pairing or retries were attempted.");
            if (!response.IsSuccessStatusCode)
                return Complete(SamsungIpRemoteOutcome.HttpError, $"The TV returned HTTP {httpStatus}. Redirects are not followed.");
            if (batch is not null)
            {
                resultPayload = safeParsed?.DeepClone();
                var verdict = InspectBatchProbe(parsed, safeParsed, batchIds!, batch);
                return Complete(verdict.Outcome, verdict.Message, verdict.Results, verdict.Code);
            }
            if (envelope is null || safeEnvelope is null
                || envelope["jsonrpc"] is not JsonValue version || !version.TryGetValue<string>(out var protocol) || protocol != "2.0"
                || !MatchesRequestId(envelope["id"], id))
                return Complete(SamsungIpRemoteOutcome.ProtocolError, "The response was not a matching JSON-RPC 2.0 reply. Its ID must equal the request ID as a number or the same decimal text. No values or credentials were accepted.");
            if (envelope.ContainsKey("error"))
            {
                if (envelope.ContainsKey("result"))
                    return Complete(SamsungIpRemoteOutcome.ProtocolError, "The reply contains both result and error; it cannot confirm success.");
                var code = envelope["error"] is JsonObject error && error["code"] is JsonValue number && number.TryGetValue<int>(out var errorCode)
                    ? (int?)errorCode : null;
                var outcome = code switch
                {
                    -32010 => SamsungIpRemoteOutcome.Unauthorized,
                    -32001 or -32601 => SamsungIpRemoteOutcome.Unsupported,
                    _ => SamsungIpRemoteOutcome.RpcError
                };
                return Complete(outcome, $"The TV returned a JSON-RPC error{(code is null ? string.Empty : $" ({code})")}. Support remains context-specific; no retries or fallback keys were sent.", code: code);
            }
            if (catalogCommand?.AllowsArrayResult == true && safeEnvelope["result"] is JsonArray list)
            {
                if (catalogCommand.DeviceList && list.Any(item => item is not JsonObject))
                    return Complete(SamsungIpRemoteOutcome.ProtocolError, "The device list contains invalid entries. No device was selected.");
                resultPayload = list.DeepClone();
                return Complete(SamsungIpRemoteOutcome.Success, $"Received {list.Count} list entries. A list or setter response is not verification of selection.");
            }
            if (envelope["result"] is not JsonObject result || safeEnvelope["result"] is not JsonObject safeResult)
                return Complete(SamsungIpRemoteOutcome.ProtocolError, "The reply has no object-valued result. Missing or unexpected values were not replaced with defaults.");
            if (pairing)
            {
                if (result["AccessToken"] is not JsonValue value || !value.TryGetValue<string>(out var accessToken)
                    || string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 16384)
                    return Complete(SamsungIpRemoteOutcome.ProtocolError, "Pairing did not return a usable AccessToken. No credential was saved.");
                accessingCredentials = true;
                await tokenStore.SaveAsync(endpoint.AbsoluteUri, accessToken, timeout.Token).ConfigureAwait(false);
                accessingCredentials = false;
                return Complete(SamsungIpRemoteOutcome.Success, "Pairing completed: separate IP Remote token saved. You can now read the current state; the state queries still need to be tested.");
            }
            if (catalogCommand is not null) resultPayload = safeResult.DeepClone();
            return Complete(SamsungIpRemoteOutcome.Success,
                control is not null ? $"{control.Name} command acknowledged. This alone does not confirm a change; independent readback and visual confirmation are required."
                    : catalogCommand is { IsReadOnly: false } && commandParameters?.Count > 0 ? $"{catalogCommand.Name} acknowledged. Check independent readback and the actual display; an acknowledgment is not verification."
                    : $"Received {safeResult.Count} result fields. This is a timestamped response, not a verified control mapping or live subscription.",
                (JsonObject)safeResult.DeepClone());
        }
        catch (OperationCanceledException)
        {
            return Complete(cancellationToken.IsCancellationRequested ? SamsungIpRemoteOutcome.Canceled : SamsungIpRemoteOutcome.Timeout,
                cancellationToken.IsCancellationRequested ? "Request canceled. No follow-up request was sent."
                    : "The TV did not complete the request before the timeout. Check IP Remote, the TV approval dialog, and LAN/VPN access; retry only when ready.");
        }
        catch (HttpRequestException)
        {
            return Complete(transport?.RejectedCertificate == true ? SamsungIpRemoteOutcome.CertificateError : SamsungIpRemoteOutcome.TransportError,
                transport?.RejectedCertificate == true ? "The TV certificate was not trusted. Verify its SHA-256 fingerprint and pin it, or explicitly allow an untrusted certificate for this endpoint only."
                    : "The HTTPS request failed. Check the IP Remote port, whether the TV is on, and local-network/VPN access.");
        }
        catch (ResponseTooLargeException)
        {
            return Complete(SamsungIpRemoteOutcome.ProtocolError, "The TV response exceeded the 1 MiB diagnostic limit; content was not retained.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Complete(accessingCredentials ? SamsungIpRemoteOutcome.StorageError : SamsungIpRemoteOutcome.TransportError,
                accessingCredentials ? "Could not read or save the private IP Remote credential file. No automatic retry was attempted."
                    : "The HTTPS response stream ended unexpectedly. No automatic retry was attempted.");
        }
        finally
        {
            if (discardTransport) CloseConnection();
            _gate.Release();
        }
    }

    // Reuse TCP/TLS connections, but never reuse a permissive connection after
    // changing endpoint, certificate pin, or trust policy. Credentials are still
    // loaded and attached separately to each RPC, not kept in default headers.
    private OwnedTransport GetTransport(SamsungIpRemoteOptions options)
    {
        lock (_transportLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = new TransportKey(options.Endpoint, options.AllowUntrustedCertificate, options.NormalizedCertificatePin);
            if (_transport?.Key != key)
            {
                _transport?.Dispose();
                _transport = new OwnedTransport(key, options);
            }
            return _transport;
        }
    }

    public void Dispose()
    {
        lock (_transportLock)
        {
            _disposed = true;
            _transport?.Dispose();
            _transport = null;
            // An injected HttpClient belongs to its caller.
        }
    }

    public void CloseConnection()
    {
        lock (_transportLock)
        {
            _transport?.Dispose();
            _transport = null;
        }
    }

    private sealed record TransportKey(Uri Endpoint, bool AllowUntrusted, string? Pin);

    private sealed class OwnedTransport : IDisposable
    {
        public TransportKey Key { get; }
        public HttpClient Client { get; }
        public string? Fingerprint { get; private set; }
        public bool RejectedCertificate { get; set; }
        public long Handshakes { get; private set; }

        public OwnedTransport(TransportKey key, SamsungIpRemoteOptions options)
        {
            Key = key;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = 1,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                        try
                        {
                            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                        }
                        catch (Exception error) when (error is SocketException or PlatformNotSupportedException) { /* Use OS keepalive defaults. */ }
                        await socket.ConnectAsync(context.DnsEndPoint, cancellation).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                Handshakes++;
                using var peer = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                Fingerprint = peer?.GetCertHashString(HashAlgorithmName.SHA256);
                // This transport serves only its exact endpoint/trust key;
                // redirects are disabled and callers cannot supply other URLs.
                var trusted = IsCertificateTrusted(options, key.Endpoint, peer, errors);
                RejectedCertificate |= !trusted;
                return trusted;
            };
            Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public void Dispose() => Client.Dispose();
    }

    private static bool MatchesRequestId(JsonNode? node, long expected)
    {
        if (node is not JsonValue value) return false;
        // Observed Samsung pairing replies stringify the numeric request ID.
        // Accept that representation without accepting a different, missing,
        // fractional, padded, or otherwise coerced ID (including on reads/errors).
        return value.TryGetValue<long>(out var number) && number == expected
            || value.TryGetValue<string>(out var text) && text == expected.ToString(CultureInfo.InvariantCulture);
    }

    public static bool IsCertificateTrusted(SamsungIpRemoteOptions options, Uri requestEndpoint, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || requestEndpoint.Scheme != Uri.UriSchemeHttps
            || !requestEndpoint.Authority.Equals(options.Endpoint.Authority, StringComparison.OrdinalIgnoreCase)) return false;
        if (options.NormalizedCertificatePin is { } pin)
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pin), certificate.GetCertHash(HashAlgorithmName.SHA256));
        return errors == SslPolicyErrors.None || options.AllowUntrustedCertificate;
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new ResponseTooLargeException();
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > MaximumResponseBytes) throw new ResponseTooLargeException();
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class ResponseTooLargeException : Exception;
}
