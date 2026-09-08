using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpCommunicationFilter(string Search = "", bool ErrorsOnly = false, string Kind = "all")
{
    public bool Matches(IpRemoteObservation observation) => (!ErrorsOnly || !observation.Exchange.IsSuccess)
        && (Kind == "all" || Kind == IpCommunicationLog.Kind(observation))
        && (string.IsNullOrWhiteSpace(Search) || new[] { observation.UserEnteredContext.Model, observation.Exchange.Method, observation.Label, observation.Exchange.Message,
            observation.Exchange.RequestJson, observation.Exchange.ResponseJson ?? "", observation.Exchange.RpcErrorCode?.ToString() ?? "" }
            .Any(text => text.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)));
}

public sealed record IpCommunicationEntry(long Number, IpRemoteObservation Observation);
public sealed record IpCommunicationPage(IReadOnlyList<IpCommunicationEntry> Entries, long Total, int SkippedLines = 0);

public static class IpCommunicationLog
{
    public static string Kind(IpRemoteObservation observation)
    {
        if (observation.Exchange.Method == "createAccessToken") return "pairing";
        try
        {
            var parsed = JsonNode.Parse(observation.Exchange.RequestJson);
            var calls = parsed is JsonArray batch ? batch.AsEnumerable() : [parsed];
            return calls.Any(call => call is JsonObject rpc && rpc["params"] is JsonObject parameters
                && parameters.Any(pair => !pair.Key.Equals("AccessToken", StringComparison.OrdinalIgnoreCase))) ? "write" : "query";
        }
        catch (JsonException) { return "other"; }
    }

    public static string Format(IpRemoteObservation observation, bool redactIdentifiers = true, bool redactSha = true)
    {
        // Expand embedded JSON before collecting secrets so tokens and echoes
        // are scrubbed together, including old/private log records.
        var node = JsonSerializer.SerializeToNode(observation)!.AsObject();
        var exchange = node["Exchange"]!.AsObject();
        foreach (var field in new[] { "RequestJson", "ResponseJson" })
        {
            if (exchange[field] is not JsonValue value) continue;
            try { exchange[field] = JsonNode.Parse(value.GetValue<string>()); }
            catch (JsonException) { exchange[field] = "[non-JSON payload omitted]"; }
        }
        node = IpRemoteRedactor.Redact(node)!.AsObject();
        exchange = node["Exchange"]!.AsObject();
        foreach (var field in new[] { "RequestJson", "ResponseJson" })
            if (exchange[field] is JsonObject or JsonArray) exchange[field] = exchange[field]!.ToJsonString(new() { WriteIndented = true });
        var json = node.ToJsonString();
        if (redactSha) json = IpRemoteReportRedactor.RedactCertificateFingerprints(json);
        return ProtocolMessageFormatter.FormatJson(json, revealSensitive: false, revealDeviceIdentifiers: !redactIdentifiers);
    }

    public static IpRemoteObservation Safe(IpRemoteObservation observation, bool redactIdentifiers = true, bool redactSha = true) =>
        JsonSerializer.Deserialize<IpRemoteObservation>(Format(observation, redactIdentifiers, redactSha))!;
}
