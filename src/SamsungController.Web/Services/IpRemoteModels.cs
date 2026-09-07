using System.Text.Json;
using System.Text.Json.Serialization;
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
}
