using System.Text.Json.Nodes;

namespace SamsungController.Core.Protocol;

public enum SamsungMessageDirection
{
    Tx,
    Rx
}

public sealed record SamsungMessage(
    DateTimeOffset Timestamp,
    SamsungMessageDirection Direction,
    string Channel,
    string? Event,
    JsonNode? ParsedPayload,
    string RawJson,
    string? ParseError,
    long ConnectionGeneration);

public sealed class SamsungMessageEventArgs(SamsungMessage message) : EventArgs
{
    public SamsungMessage Message { get; } = message;
}
