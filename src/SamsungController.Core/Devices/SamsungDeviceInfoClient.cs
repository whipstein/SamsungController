using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungController.Core.Devices;

public sealed record SamsungDeviceInfoRequest
{
    public required string Host { get; init; }

    public bool Secure { get; init; } = true;

    public int? Port { get; init; }

    public bool AllowUntrustedCertificate { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record SamsungDeviceInfoResponse(
    DateTimeOffset Timestamp,
    Uri Endpoint,
    HttpStatusCode StatusCode,
    JsonNode? ParsedPayload,
    string RawContent,
    string? ParseError)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

public interface ISamsungDeviceInfoClient
{
    Task<SamsungDeviceInfoResponse> GetAsync(
        SamsungDeviceInfoRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the Samsung device-information endpoint without sharing certificate
/// policy or mutable HTTP state with the WebSocket transport.
/// </summary>
public sealed class SamsungDeviceInfoClient : ISamsungDeviceInfoClient
{
    private readonly HttpClient? _httpClient;

    public SamsungDeviceInfoClient()
    {
    }

    public SamsungDeviceInfoClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<SamsungDeviceInfoResponse> GetAsync(
        SamsungDeviceInfoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var endpoint = GetEndpoint(request);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpClient? requestClient = null;
        try
        {
            var client = _httpClient;
            if (client is null)
            {
                var handler = new HttpClientHandler();
                if (request.Secure && request.AllowUntrustedCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                requestClient = new HttpClient(handler, disposeHandler: true);
                client = requestClient;
            }

            using var response = await client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token)
                .ConfigureAwait(false);
            var rawContent = await response.Content.ReadAsStringAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            var parsedPayload = TryParsePayload(rawContent, out var parseError);
            return new SamsungDeviceInfoResponse(
                DateTimeOffset.UtcNow,
                endpoint,
                response.StatusCode,
                parsedPayload,
                rawContent,
                parseError);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The TV did not answer {endpoint.AbsoluteUri} within {request.Timeout.TotalSeconds:0.#} seconds.",
                exception);
        }
        finally
        {
            requestClient?.Dispose();
        }
    }

    public static Uri GetEndpoint(SamsungDeviceInfoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        return new UriBuilder(
            request.Secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            request.Host.Trim(),
            request.Port ?? (request.Secure ? 8002 : 8001),
            "/api/v2/").Uri;
    }

    private static JsonNode? TryParsePayload(string content, out string? parseError)
    {
        parseError = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException exception)
        {
            parseError = exception.Message;
            return null;
        }
    }

    private static void Validate(SamsungDeviceInfoRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new ArgumentException("A TV host name or IP address is required.", nameof(request));
        }

        if (request.Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Port must be between 1 and 65535.");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be greater than zero.");
        }
    }
}
