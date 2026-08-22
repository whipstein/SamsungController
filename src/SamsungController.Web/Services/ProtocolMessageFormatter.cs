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

        var payload = message.ParsedPayload.DeepClone();
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
