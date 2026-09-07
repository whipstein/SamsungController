using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SamsungController.Core.Protocol;

namespace SamsungController.Web.Services;

public static class ProtocolMessageFormatter
{
    private static readonly Regex UuidPattern = new(
        @"(?:uuid:)?\b[0-9a-f]{8}-[0-9a-f]{3,4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MacAddressPattern = new(
        @"(?<![0-9a-f:])(?:[0-9a-f]{2}:){5}[0-9a-f]{2}(?![0-9a-f:])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Pattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex BracketedIpv6Pattern = new(
        @"\[[0-9a-f:]+\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true
    };

    public static string Format(
        SamsungMessage message,
        bool revealSensitive,
        bool revealDeviceIdentifiers = false)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ParsedPayload is null)
        {
            return FormatIdentifierText(message.RawJson, revealDeviceIdentifiers);
        }

        return FormatPayload(message.ParsedPayload, revealSensitive, revealDeviceIdentifiers);
    }

    public static string FormatJson(
        string rawJson,
        bool revealSensitive,
        bool revealDeviceIdentifiers = false)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        try
        {
            var payload = JsonNode.Parse(rawJson);
            return payload is null
                ? FormatIdentifierText(rawJson, revealDeviceIdentifiers)
                : FormatPayload(payload, revealSensitive, revealDeviceIdentifiers);
        }
        catch (JsonException)
        {
            return FormatIdentifierText(rawJson, revealDeviceIdentifiers);
        }
    }

    public static string FormatIdentifierText(string value, bool revealDeviceIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (revealDeviceIdentifiers)
        {
            return value;
        }

        // IPAddress.TryParse also accepts legacy numeric IPv4 forms such as
        // "1296", "2.0", and "3". In diagnostics these are firmware versions,
        // JSON-RPC versions, or request IDs, not evidence of an IP address.
        if (value.Contains(':') && IPAddress.TryParse(value.Trim('[', ']'), out _))
        {
            return "[redacted-ip]";
        }

        var redacted = UuidPattern.Replace(value, "[redacted-uuid]");
        redacted = MacAddressPattern.Replace(redacted, "[redacted-mac]");
        redacted = Ipv4Pattern.Replace(redacted, match => IPAddress.TryParse(match.Value, out _) ? "[redacted-ip]" : match.Value);
        return BracketedIpv6Pattern.Replace(redacted, "[redacted-ip]");
    }

    private static string FormatPayload(
        JsonNode parsedPayload,
        bool revealSensitive,
        bool revealDeviceIdentifiers)
    {
        var payload = parsedPayload.DeepClone();
        if (!revealSensitive)
        {
            RedactTokens(payload);
        }

        if (!revealDeviceIdentifiers)
        {
            RedactDeviceIdentifiers(payload);
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

    private static void RedactDeviceIdentifiers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject.ToArray())
                {
                    if (property.Value is JsonValue value
                        && value.TryGetValue<string>(out var text))
                    {
                        // Diagnostic exports embed the original request/reply
                        // as JSON strings. Inspect those values structurally so
                        // IPv6 addresses are redacted without changing IDs.
                        if (property.Key is "RequestJson" or "ResponseJson")
                        {
                            try
                            {
                                var embedded = JsonNode.Parse(text);
                                RedactDeviceIdentifiers(embedded);
                                jsonObject[property.Key] = embedded?.ToJsonString(PrettyJson) ?? text;
                                continue;
                            }
                            catch (JsonException) { /* Core may have omitted a malformed response. */ }
                        }
                        jsonObject[property.Key] = FormatIdentifierText(
                            text,
                            revealDeviceIdentifiers: false);
                    }
                    else
                    {
                        RedactDeviceIdentifiers(property.Value);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is JsonValue value && value.TryGetValue<string>(out var text))
                        jsonArray[index] = FormatIdentifierText(text, revealDeviceIdentifiers: false);
                    else RedactDeviceIdentifiers(jsonArray[index]);
                }

                break;
        }
    }
}
