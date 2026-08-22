using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungController.Core.Protocol;

internal static class SamsungProtocol
{
    public const string RemoteControlChannel = "samsung.remote.control";
    public const string ChannelConnectEvent = "ms.channel.connect";
    public const string ChannelTimeoutEvent = "ms.channel.timeOut";
    public const string ChannelUnauthorizedEvent = "ms.channel.unauthorized";

    public static Uri BuildRemoteControlEndpoint(
        string host,
        string applicationName,
        bool secure,
        int? explicitPort,
        string? token)
    {
        var scheme = secure ? Uri.UriSchemeWss : Uri.UriSchemeWs;
        var port = explicitPort ?? (secure ? 8002 : 8001);
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(applicationName));

        var query = $"name={Uri.EscapeDataString(encodedName)}";
        if (!string.IsNullOrWhiteSpace(token))
        {
            query += $"&token={Uri.EscapeDataString(token)}";
        }

        return new UriBuilder(scheme, host, port,
            "/api/v2/channels/samsung.remote.control")
        {
            Query = query
        }.Uri;
    }

    public static string CreateRemoteKeyPayload(string key, RemoteKeyAction action)
    {
        var request = new
        {
            method = "ms.remote.control",
            @params = new
            {
                Cmd = action.ToString(),
                DataOfCmd = key,
                Option = "false",
                TypeOfRemote = "SendRemoteKey"
            }
        };

        return JsonSerializer.Serialize(request);
    }

    public static SamsungMessage ParseMessage(
        string rawJson,
        SamsungMessageDirection direction,
        long generation)
    {
        try
        {
            var payload = JsonNode.Parse(rawJson);
            var eventName = payload?["event"]?.GetValue<string>()
                ?? payload?["method"]?.GetValue<string>();

            return new SamsungMessage(
                DateTimeOffset.UtcNow,
                direction,
                RemoteControlChannel,
                eventName,
                payload,
                rawJson,
                ParseError: null,
                generation);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new SamsungMessage(
                DateTimeOffset.UtcNow,
                direction,
                RemoteControlChannel,
                Event: null,
                ParsedPayload: null,
                rawJson,
                exception.Message,
                generation);
        }
    }

    public static string? TryGetToken(JsonNode? payload)
    {
        var directToken = TryGetString(payload?["data"]?["token"]);
        if (!string.IsNullOrWhiteSpace(directToken))
        {
            return directToken;
        }

        if (payload?["data"]?["clients"] is not JsonArray clients)
        {
            return null;
        }

        foreach (var client in clients)
        {
            var token = TryGetString(client?["attributes"]?["token"]);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonNode? node)
    {
        try
        {
            return node?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
