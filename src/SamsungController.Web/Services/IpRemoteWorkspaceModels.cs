using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SamsungController.Web.Services;

public sealed record IpRemoteWorkspaceReading(Guid Id, IpRemoteProfile Profile, DateTimeOffset ReadAt,
    string ReportedInput, string ReportedPictureMode, JsonObject VideoBaseline)
{
    // A missing, incorrectly typed, or out-of-envelope field is unavailable, never zero.
    public int? Value(string control) => VideoBaseline[control] is JsonValue field
        && field.TryGetValue<int>(out var value) && value is >= 0 and <= 100 ? value : null;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemoteBatchStage { Running, Completed, Stopped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemoteBatchStepStage { Pending, Checking, Applying, Confirmed, NotSent, Uncertain, RejectedUnchanged }

public sealed record IpRemoteBatchStep(string Control, int Original, int Target, Guid OperationId,
    IpRemoteBatchStepStage Stage = IpRemoteBatchStepStage.Pending);

// The batch journal retains all originals/progress; the existing single-operation
// journal remains authoritative for a possibly delivered write and its recovery.
public sealed record IpRemotePictureBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public IpRemoteProfile Profile { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; }
    public string ReportedInput { get; init; } = "";
    public string ReportedPictureMode { get; init; } = "";
    public IpRemoteBatchStage Stage { get; init; } = IpRemoteBatchStage.Running;
    public IReadOnlyList<IpRemoteBatchStep> Steps { get; init; } = [];
    public string Message { get; init; } = "Prepared batch. No setting has been changed.";
}
