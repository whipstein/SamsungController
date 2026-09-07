using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpRemoteProfile
{
    public SamsungIpRemoteOptions Connection { get; init; } = new();
    public string Model { get; init; } = "";
    public string Firmware { get; init; } = "";
    public string InputSource { get; init; } = "";
    public string PictureMode { get; init; } = "";
    public string Signal { get; init; } = "";
    [JsonIgnore] public string Endpoint => Connection.Endpoint.AbsoluteUri;
    [JsonIgnore] public string ContextKey => JsonSerializer.Serialize(new[] { Endpoint, Model, Firmware, InputSource, PictureMode, Signal });
}

public sealed record IpRemoteObservation(IpRemoteProfile UserEnteredContext, string Label, SamsungIpRemoteExchange Exchange);

public sealed record IpRemoteSnapshot
{
    public bool Initialized { get; init; }
    public IReadOnlyList<IpRemoteProfile> Profiles { get; init; } = [];
    public IpRemoteProfile? ActiveProfile { get; init; }
    public bool HasToken { get; init; }
    public bool AuthorizationRejected { get; init; }
    public bool IsBusy { get; init; }
    public string Status { get; init; } = "Save an IP Remote profile to begin. No network request has been sent.";
    public string? StorageWarning { get; init; }
    public IReadOnlyList<IpRemoteObservation> Observations { get; init; } = [];
    public IpRemoteContrastTest? ContrastTest { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemoteContrastStage { Prepared, Applying, AwaitingVisualCheck, Restoring, RecoveryRequired, Completed, Stopped, ManuallyClosed }

// Private recovery journal; never part of a shared menu or saved calibration.
public sealed record IpRemoteContrastTest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public IpRemoteProfile Profile { get; init; } = new();
    public DateTimeOffset PreparedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Original { get; init; }
    public int Target { get; init; }
    public string ReportedInput { get; init; } = "";
    public string ReportedPictureMode { get; init; } = "";
    public JsonObject VideoBaseline { get; init; } = new();
    public IpRemoteContrastStage Stage { get; init; } = IpRemoteContrastStage.Prepared;
    public string Message { get; init; } = "Baseline read. No setting has been changed.";
    public bool WriteAttempted { get; init; }
    public bool ChangeReadbackConfirmed { get; init; }
    public bool? VisualConfirmed { get; init; }
    public bool RestoreAttempted { get; init; }
    public bool RestoreAcknowledged { get; init; }
    public bool RestorationConfirmed { get; init; }
    public bool ManuallyClosed { get; init; }
    public int? LastReadback { get; init; }
    public bool RequiresRecovery => WriteAttempted && !RestorationConfirmed && !ManuallyClosed;
    public bool Verified => ChangeReadbackConfirmed && VisualConfirmed == true && RestoreAcknowledged && RestorationConfirmed && !ManuallyClosed;
}
