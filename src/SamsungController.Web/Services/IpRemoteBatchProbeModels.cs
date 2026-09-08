using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpReadBatchProbe(string Endpoint, Guid SessionId, SamsungIpRemoteExchange Exchange);
public sealed record IpBatchFailure(string Endpoint, DateTimeOffset Timestamp, string Method, SamsungIpRemoteOutcome Outcome, int? RpcErrorCode);
public enum IpRgbProbeStage { Preparing, Prepared, Sending, Observed, Restoring, Restored, ManuallyClosed, Stopped }
public sealed record IpRgbProbe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public string Endpoint { get; init; } = "";
    public string Input { get; init; } = "";
    public string PictureMode { get; init; } = "";
    public string Interval { get; init; } = "";
    public string OriginalInterval { get; init; } = "";
    public DateTimeOffset PreparedAt { get; init; }
    public JsonObject TvBaseline { get; init; } = new();
    public JsonObject VideoBaseline { get; init; } = new();
    public IReadOnlyDictionary<string, int> Originals { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Targets { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int?> Readback { get; init; } = new Dictionary<string, int?>();
    public IReadOnlyDictionary<string, int?> RecoveryReadback { get; init; } = new Dictionary<string, int?>();
    public IpRgbProbeStage Stage { get; init; } = IpRgbProbeStage.Preparing;
    public bool WriteAttempted { get; init; }
    public bool? SingleMethod { get; init; }
    public SamsungIpRemoteExchange? Exchange { get; init; }
    public string Message { get; init; } = "";
    public bool NeedsRecovery => Stage is not (IpRgbProbeStage.Restored or IpRgbProbeStage.ManuallyClosed);
}
