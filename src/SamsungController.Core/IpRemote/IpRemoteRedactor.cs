using System.Text.Json.Nodes;

namespace SamsungController.Core.IpRemote;

public static class IpRemoteRedactor
{
    public const string Redacted = "[redacted]";

    public static JsonNode? Redact(JsonNode? payload, string? knownToken = null, bool pairing = false)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(knownToken)) secrets.Add(knownToken);
        void Collect(JsonNode? node, bool sensitive = false)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var pair in obj) Collect(pair.Value, sensitive || IsSecretKey(pair.Key));
                    break;
                case JsonArray array:
                    foreach (var child in array) Collect(child, sensitive);
                    break;
                case JsonValue value when sensitive && value.TryGetValue<string>(out var text) && text.Length > 0:
                    secrets.Add(text);
                    break;
            }
        }
        Collect(payload);
        string Clean(string text)
        {
            foreach (var secret in secrets.OrderByDescending(value => value.Length))
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
            return text;
        }
        JsonNode? Scrub(JsonNode? node, string? key = null)
        {
            if (key is not null && IsSecretKey(key)) return JsonValue.Create(Redacted);
            return node switch
            {
                JsonObject obj => new JsonObject(obj.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(Clean(pair.Key), Scrub(pair.Value, pair.Key)))),
                JsonArray array => new JsonArray(array.Select(child => Scrub(child)).ToArray()),
                JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(
                    pairing && !(key == "jsonrpc" && text == "2.0") ? Redacted : Clean(text)),
                _ => node?.DeepClone()
            };
        }
        return Scrub(payload);
    }

    private static bool IsSecretKey(string key) => new[] { "token", "authorization", "password", "secret" }
        .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
}
