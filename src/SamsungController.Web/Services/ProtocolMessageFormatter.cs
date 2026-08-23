using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.Protocol;

namespace SamsungController.Web.Services;

public static class ProtocolMessageFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true
    };

    public static string Format(SamsungMessage message, bool revealSensitive)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ParsedPayload is null)
        {
            return message.RawJson;
        }

        return FormatPayload(message.ParsedPayload, revealSensitive);
    }

    public static string FormatJson(string rawJson, bool revealSensitive)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        try
        {
            var payload = JsonNode.Parse(rawJson);
            return payload is null ? rawJson : FormatPayload(payload, revealSensitive);
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    private static string FormatPayload(JsonNode parsedPayload, bool revealSensitive)
    {
        var payload = parsedPayload.DeepClone();
        if (!revealSensitive)
        {
            RedactTokens(payload);
        }

        return payload.ToJsonString(PrettyJson);
    }

    private static void RedactTokens(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (property.Key.Equals("token", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonObject[property.Key] = "[redacted]";
                    }
                    else
                    {
                        RedactTokens(property.Value);
                    }
                }

                break;

            case JsonArray jsonArray:
                foreach (var child in jsonArray)
                {
                    RedactTokens(child);
                }

                break;
        }
    }
}
