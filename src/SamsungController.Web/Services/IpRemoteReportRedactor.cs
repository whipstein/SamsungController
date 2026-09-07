using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungController.Web.Services;

/// <summary>Export-only redaction; saved TLS trust and local diagnostics remain intact.</summary>
public static class IpRemoteReportRedactor
{
    public const string CertificateMarker = "[redacted-sha256]";
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static string RedactCertificateFingerprints(string json)
    {
        var payload = JsonNode.Parse(json);
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scrub(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var child in array) Scrub(child);
            }
            else if (node is JsonObject obj)
            {
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (IsCertificateField(property.Key) && !string.IsNullOrEmpty(text))
                        {
                            var normalized = text.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
                            if (normalized.Length == 64 && normalized.All(Uri.IsHexDigit))
                            {
                                fingerprints.Add(text);
                                fingerprints.Add(normalized);
                                fingerprints.Add(string.Join(":", Enumerable.Range(0, 32).Select(index => normalized.Substring(index * 2, 2))));
                                fingerprints.Add(string.Join(" ", Enumerable.Range(0, 32).Select(index => normalized.Substring(index * 2, 2))));
                            }
                            obj[property.Key] = CertificateMarker;
                        }
                        else if (property.Key is "RequestJson" or "ResponseJson")
                        {
                            try
                            {
                                var embedded = JsonNode.Parse(text);
                                Scrub(embedded);
                                obj[property.Key] = embedded?.ToJsonString(PrettyJson) ?? text;
                            }
                            catch (JsonException) { /* Core may have omitted a malformed response. */ }
                        }
                    }
                    else Scrub(property.Value);
                }
            }
        }
        Scrub(payload);
        var result = payload?.ToJsonString(PrettyJson) ?? "null";
        // Mask echoes and annotations too, including case/colon variations.
        // Replacing the JSON-encoded string content preserves valid outer JSON.
        foreach (var fingerprint in fingerprints.OrderByDescending(item => item.Length))
        {
            var encoded = JsonSerializer.Serialize(fingerprint);
            result = result.Replace(encoded[1..^1], CertificateMarker, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static bool IsCertificateField(string name) => name.Equals("CertificateSha256", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NormalizedCertificatePin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ObservedCertificateSha256", StringComparison.OrdinalIgnoreCase);
}
