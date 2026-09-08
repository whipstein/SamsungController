using System.Text.Json.Nodes;

namespace SamsungController.Core.IpRemote;

public sealed partial class SamsungIpRemoteClient
{
    // Deliberately narrow experimental entry points, not an arbitrary RPC editor.
    // Normal Menu commands and their validation remain unchanged.
    public Task<SamsungIpRemoteExchange> ProbeReadBatchAsync(SamsungIpRemoteOptions options, CancellationToken cancellationToken = default) =>
        ExecuteAsync(options, "batch:getTVStates+getVideoStates", false, cancellationToken,
            batch: [new("getTVStates", new()), new("getVideoStates", new())]);

    public Task<SamsungIpRemoteExchange> ProbeWhiteBalanceRgbAsync(SamsungIpRemoteOptions options, int red, int green, int blue, bool singleMethod, CancellationToken cancellationToken = default)
    {
        var values = new[] { red, green, blue };
        if (values.Any(value => value is < -50 or > 50)) throw new ArgumentOutOfRangeException(nameof(red), "All RGB targets must be integers from -50 to 50.");
        var fields = new[] { "WB20P.Red", "WB20P.Green", "WB20P.Blue" };
        if (singleMethod)
            return ExecuteAsync(options, "WB20P.RedControl", false, cancellationToken,
                commandParameters: new JsonObject(fields.Select((field, index) => KeyValuePair.Create<string, JsonNode?>(field, JsonValue.Create(values[index])))),
                catalogCommand: SamsungIpRemoteCommands.Get("WB20P.RedControl"));
        return ExecuteAsync(options, "batch:WB20P.RGB", false, cancellationToken,
            batch: fields.Select((field, index) => new BatchProbeCall(field + "Control", new() { [field] = values[index] })).ToArray());
    }

    private sealed record BatchProbeCall(string Method, JsonObject Parameters);
    private sealed record BatchProbeVerdict(SamsungIpRemoteOutcome Outcome, string Message, JsonObject? Results = null, int? Code = null);

    private static BatchProbeVerdict InspectBatchProbe(JsonNode? parsed, JsonNode? safe, long[] ids, IReadOnlyList<BatchProbeCall> calls)
    {
        static bool Version(JsonObject envelope) => envelope["jsonrpc"] is JsonValue value && value.TryGetValue<string>(out var version) && version == "2.0";
        static int? Code(JsonObject envelope) => envelope["error"] is JsonObject error && error["code"] is JsonValue value && value.TryGetValue<int>(out var code) ? code : null;
        static SamsungIpRemoteOutcome ErrorOutcome(int code) => code == -32010 ? SamsungIpRemoteOutcome.Unauthorized : SamsungIpRemoteOutcome.RpcError;
        static BatchProbeVerdict Invalid() => new(SamsungIpRemoteOutcome.ProtocolError,
            "Batch reply was incomplete, duplicated, malformed, or had unexpected IDs. Batch support was not established; no retry was sent.");
        // A server can reject the whole array with one error and a null ID.
        // This records a rejection, never accepts values or proves batch support.
        if (parsed is JsonObject rejected && Version(rejected) && !rejected.ContainsKey("result") && Code(rejected) is { } rejection
            && (rejected["id"] is null || ids.Any(id => MatchesRequestId(rejected["id"], id))))
            return new(ErrorOutcome(rejection), rejection == -32004 ? "The TV explicitly rejected batch requests (-32004). No sequential fallback was sent."
                : $"The TV rejected the batch envelope ({rejection}). No retry or fallback was sent.", Code: rejection);
        if (parsed is not JsonArray responses || safe is not JsonArray safeResponses || responses.Count != ids.Length) return Invalid();
        var seen = new HashSet<int>();
        var results = new JsonObject();
        var errors = new List<int>();
        for (var position = 0; position < responses.Count; position++)
        {
            if (responses[position] is not JsonObject envelope || safeResponses[position] is not JsonObject safeEnvelope || !Version(envelope)) return Invalid();
            var index = Array.FindIndex(ids, id => MatchesRequestId(envelope["id"], id));
            if (index < 0 || !seen.Add(index) || envelope.ContainsKey("error") == envelope.ContainsKey("result")) return Invalid();
            if (envelope.ContainsKey("error"))
            {
                if (Code(envelope) is not { } error) return Invalid();
                errors.Add(error);
            }
            else
            {
                if (safeEnvelope["result"] is not JsonObject result) return Invalid();
                results[calls[index].Method] = result.DeepClone();
            }
        }
        if (errors.Count > 0)
        {
            var code = errors.Contains(-32010) ? -32010 : errors[0];
            return new(ErrorOutcome(code), $"Received all batch replies, but {errors.Count} member(s) failed ({string.Join(", ", errors)}). A partial batch is not success; no retries were sent.", results, code);
        }
        return new(SamsungIpRemoteOutcome.Success, $"Received {ids.Length} matching batch replies from one HTTP request. This does not prove atomic execution or RGB readback.", results);
    }
}
