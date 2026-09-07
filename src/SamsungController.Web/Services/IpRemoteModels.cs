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
    public IpRemotePictureTest? PictureTest { get; init; }
    public IReadOnlyList<IpRemoteControlCapability> ControlCapabilities { get; init; } = [];
    public IpRemotePictureReading? DirectPictureReading { get; init; }
}

public sealed record IpRemotePictureReading(Guid Id, IpRemoteProfile Profile, DateTimeOffset ReadAt,
    string ReportedInput, string ReportedPictureMode, int Value, JsonObject VideoBaseline)
{
    public string Control { get; init; } = "contrast";
}

// Evidence is independent of the latest operation/recovery journal. It is
// endpoint/context-specific, not a model-wide promise or a slider range.
public sealed record IpRemoteControlCapability
{
    public string Control { get; init; } = "contrast";
    public IpRemoteProfile Profile { get; init; } = new();
    public string ReportedInput { get; init; } = "";
    public string ReportedPictureMode { get; init; } = "";
    public Guid EvidenceId { get; init; }
    public DateTimeOffset TestStartedAt { get; init; }
    public int Original { get; init; }
    public int TestedTarget { get; init; }
    public bool ReadVerified { get; init; }
    public bool WriteVerified { get; init; }
    [JsonIgnore] public string Key => JsonSerializer.Serialize(new[] { Control, Profile.ContextKey, ReportedInput, ReportedPictureMode });
    public bool Matches(IpRemoteProfile profile, string input, string mode, string control = "contrast") => Control == control
        && ReadVerified && WriteVerified && Profile.ContextKey == profile.ContextKey
        && ReportedInput == input && ReportedPictureMode == mode;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemotePicturePurpose { Verification, DirectAdjustment }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemotePictureStage { Prepared, Applying, AwaitingVisualCheck, Restoring, RecoveryRequired, Completed, Stopped, ManuallyClosed }

// Private recovery journal; never part of a shared menu or saved calibration.
public sealed record IpRemotePictureTest
{
    // Missing on older private contrast journals: retain their evidence and recovery.
    public string Control { get; init; } = "contrast";
    [JsonIgnore] public string ControlName => SamsungIpRemotePictureControl.Get(Control).Name;
    public IpRemotePicturePurpose Purpose { get; init; } = IpRemotePicturePurpose.Verification;
    public Guid Id { get; init; } = Guid.NewGuid();
    public IpRemoteProfile Profile { get; init; } = new();
    public DateTimeOffset PreparedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Original { get; init; }
    public int Target { get; init; }
    public string ReportedInput { get; init; } = "";
    public string ReportedPictureMode { get; init; } = "";
    public JsonObject VideoBaseline { get; init; } = new();
    public IpRemotePictureStage Stage { get; init; } = IpRemotePictureStage.Prepared;
    public string Message { get; init; } = "Baseline read. No setting has been changed.";
    public bool WriteAttempted { get; init; }
    public bool ChangeReadbackConfirmed { get; init; }
    public bool? VisualConfirmed { get; init; }
    public bool RestoreAttempted { get; init; }
    public bool RestoreAcknowledged { get; init; }
    public bool RestorationConfirmed { get; init; }
    public bool ManuallyClosed { get; init; }
    public bool DirectChangeKept { get; init; }
    public int? LastReadback { get; init; }
    public bool RequiresRecovery => WriteAttempted && !RestorationConfirmed && !ManuallyClosed && !DirectChangeKept;
    public bool Verified => Purpose == IpRemotePicturePurpose.Verification && ChangeReadbackConfirmed
        && VisualConfirmed == true && RestoreAcknowledged && RestorationConfirmed && !ManuallyClosed;
}
