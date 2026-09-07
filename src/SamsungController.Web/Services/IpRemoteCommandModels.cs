using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IpRemoteCommandStage { Prepared, Sending, AwaitingReview, Uncertain, Confirmed, Failed, Expired }

public sealed record IpRemoteCommandTrial
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public IpRemoteProfile Profile { get; init; } = new();
    public string Method { get; init; } = "";
    public JsonObject Parameters { get; init; } = new();
    public DateTimeOffset PreparedAt { get; init; }
    public JsonObject BeforeTv { get; init; } = new();
    public JsonObject BeforeVideo { get; init; } = new();
    public JsonObject? AfterTv { get; init; }
    public JsonObject? AfterVideo { get; init; }
    public IpRemoteCommandStage Stage { get; init; }
    public bool WriteAttempted { get; init; }
    public bool Acknowledged { get; init; }
    public bool? ReadbackMatches { get; init; }
    public bool ChangeObserved { get; init; }
    public bool? VisualConfirmed { get; init; }
    public string Message { get; init; } = "Prepared. No command has been sent.";
    public bool RequiresReview => WriteAttempted && Stage is not (IpRemoteCommandStage.Confirmed or IpRemoteCommandStage.Failed);
    public bool ReadWriteVerified => Acknowledged && ReadbackMatches == true && ChangeObserved && VisualConfirmed == true && Stage == IpRemoteCommandStage.Confirmed;
}

public sealed record IpRemoteCatalogQuery(IpRemoteProfile Profile, SamsungIpRemoteExchange Exchange);
