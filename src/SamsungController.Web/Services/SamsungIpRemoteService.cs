using System.Reflection;
using System.Text.Json;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

/// <summary>Isolated diagnostics; deliberately has no reference to menu/remote/calibration services.</summary>
public sealed partial class SamsungIpRemoteService : IDisposable
{
    public static string Version { get; } = typeof(SamsungIpRemoteClient).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ISamsungIpRemoteClient _client;
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private IpRemoteSnapshot _snapshot = new();
    private CancellationTokenSource? _operation;

    public SamsungIpRemoteService(IConfiguration configuration, ISamsungIpRemoteClient? client = null, TimeProvider? timeProvider = null)
    {
        var root = configuration["SamsungController:ConfigurationDirectory"] ?? WebApplicationPaths.GetDefaultConfigurationDirectory();
        _directory = Path.Combine(Path.GetFullPath(root), "ip-remote");
        _client = client ?? new SamsungIpRemoteClient(new PrivateIpRemoteTokenStore(_directory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action? Changed;
    public IpRemoteSnapshot GetSnapshot() { lock (_sync) return _snapshot; }
    public string DiagnosticLogPath => Path.Combine(_directory, "diagnostics.ndjson");

    public async Task InitializeAsync()
    {
        if (GetSnapshot().Initialized) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (GetSnapshot().Initialized) return;
            PrivateIpRemoteTokenStore.EnsurePrivateDirectory(_directory);
            var path = Path.Combine(_directory, "profiles.json");
            var saved = File.Exists(path)
                ? JsonSerializer.Deserialize<SavedProfiles>(await File.ReadAllTextAsync(path).ConfigureAwait(false))
                    ?? throw new JsonException("The IP Remote profile file is empty.")
                : new SavedProfiles(null, []);
            if (saved.Profiles is null) throw new JsonException("The IP Remote profile list is missing.");
            foreach (var profile in saved.Profiles)
            {
                if (profile is null || profile.Connection is null || profile.Model is null || profile.Firmware is null
                    || profile.InputSource is null || profile.PictureMode is null || profile.Signal is null)
                    throw new JsonException("An IP Remote profile has invalid fields.");
                _ = profile.Connection.Endpoint;
            }
            var active = saved.Profiles.FirstOrDefault(profile => profile.Endpoint == saved.ActiveEndpoint);
            var hasToken = active is not null && await _client.HasTokenAsync(active.Connection).ConfigureAwait(false);
            var test = await LoadContrastTestAsync().ConfigureAwait(false);
            var capabilities = await LoadControlCapabilitiesAsync().ConfigureAwait(false);
            Update(state => state with
            {
                Initialized = true,
                Profiles = saved.Profiles,
                ActiveProfile = active,
                HasToken = hasToken,
                ContrastTest = test,
                ControlCapabilities = capabilities
            });
            // Upgrade a locally completed test, never a shared diagnostic report.
            // This only saves private evidence; startup sends no TV requests.
            if (test?.Verified == true) await RememberContrastCapabilityAsync(test).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveProfileAsync(IpRemoteProfile profile)
    {
        _ = profile.Endpoint;
        await EnterAsync().ConfigureAwait(false);
        try
        {
            EnsureNoPendingContrastTest();
            var profiles = GetSnapshot().Profiles.Where(item => item.Endpoint != profile.Endpoint).Append(profile).ToArray();
            await SaveProfilesAsync(profiles, profile.Endpoint).ConfigureAwait(false);
            var hasToken = await _client.HasTokenAsync(profile.Connection).ConfigureAwait(false);
            Update(state => state with
            {
                Profiles = profiles,
                ActiveProfile = profile,
                DirectContrastReading = null,
                HasToken = hasToken,
                AuthorizationRejected = state.ActiveProfile?.Endpoint == profile.Endpoint && state.AuthorizationRejected,
                Status = "Profile saved locally. Pairing and reads require an explicit button press."
            });
        }
        finally { _gate.Release(); }
    }

    public async Task SelectProfileAsync(string endpoint)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            EnsureNoPendingContrastTest();
            var profile = GetSnapshot().Profiles.Single(item => item.Endpoint == endpoint);
            await SaveProfilesAsync(GetSnapshot().Profiles, endpoint).ConfigureAwait(false);
            var hasToken = await _client.HasTokenAsync(profile.Connection).ConfigureAwait(false);
            Update(state => state with
            {
                ActiveProfile = profile,
                DirectContrastReading = null,
                HasToken = hasToken,
                AuthorizationRejected = false,
                Status = "Profile selected. Displayed responses are historical; read again for current evidence."
            });
        }
        finally { _gate.Release(); }
    }

    public async Task ForgetTokenAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            EnsureNoPendingContrastTest();
            var profile = GetSnapshot().ActiveProfile ?? throw new InvalidOperationException("Save a profile first.");
            await _client.ForgetTokenAsync(profile.Connection).ConfigureAwait(false);
            Update(state => state with
            {
                HasToken = false,
                DirectContrastReading = null,
                AuthorizationRejected = false,
                Status = "IP Remote token removed locally for this endpoint. The TV and WebSocket token were not changed."
            });
        }
        finally { _gate.Release(); }
    }

    public Task PairAsync(string label = "Pairing") => RunAsync(true, [], label);
    public Task ReadAsync(string method, string label) => RunAsync(false, [method], label);
    public Task ReadBothAsync(string label) => RunAsync(false, SamsungIpRemoteClient.ReadMethods, label);

    public void Cancel()
    {
        lock (_sync) _operation?.Cancel();
    }

    private async Task RunAsync(bool pairing, IReadOnlyList<string> methods, string label)
    {
        if (methods.Any(method => !SamsungIpRemoteClient.ReadMethods.Contains(method, StringComparer.Ordinal)))
            throw new InvalidOperationException("Only the two approved read methods are available.");
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var state = GetSnapshot();
            var profile = state.ActiveProfile ?? throw new InvalidOperationException("Save a profile first.");
            if (!pairing && (!state.HasToken || state.AuthorizationRejected))
                throw new InvalidOperationException("Pair explicitly before querying this endpoint. No request was sent.");
            var cancellation = new CancellationTokenSource();
            lock (_sync) _operation = cancellation;
            Update(current => current with
            {
                IsBusy = true,
                StorageWarning = null,
                DirectContrastReading = null,
                Status = pairing ? "Waiting for IP Remote approval on the TV…" : "Reading a timestamped state snapshot…"
            });
            foreach (var method in pairing ? new[] { "createAccessToken" } : methods)
            {
                if (cancellation.IsCancellationRequested) break;
                var exchange = pairing
                    ? await _client.PairAsync(profile.Connection, cancellation.Token).ConfigureAwait(false)
                    : await _client.ReadAsync(profile.Connection, method, cancellation.Token).ConfigureAwait(false);
                await RecordExchangeAsync(profile, label, exchange, pairing).ConfigureAwait(false);
                if (!exchange.IsSuccess && exchange.Outcome != SamsungIpRemoteOutcome.Unsupported) break;
            }
        }
        finally
        {
            lock (_sync)
            {
                _operation?.Dispose();
                _operation = null;
            }
            Update(state => state with { IsBusy = false });
            _gate.Release();
        }
    }

    public string ExportReport(bool redactIdentifiers = true, bool redactCertificateFingerprints = true)
    {
        var snapshot = GetSnapshot();
        var json = JsonSerializer.Serialize(new
        {
            Format = "SamsungController.IPRemote.Diagnostics.v1",
            Version,
            ExportedAt = DateTimeOffset.UtcNow,
            Safety = "Explicit pairing, two getters, guarded contrast verification, and context-gated direct contrast adjustments. Setter acknowledgments alone are not verification. No other writes, polling, or fallback keys.",
            Context = "Model, firmware, input, picture mode, and signal annotations are user-entered, not TV-reported unless also present in the response.",
            CurrentProfile = snapshot.ActiveProfile,
            snapshot.Observations,
            snapshot.ContrastTest,
            snapshot.ControlCapabilities,
            snapshot.DirectContrastReading,
            Methods = SamsungIpRemoteClient.ReadMethods.Select(method => new
            {
                Method = method,
                LastAttempt = snapshot.Observations.LastOrDefault(item => item.UserEnteredContext.ContextKey == snapshot.ActiveProfile?.ContextKey
                    && item.Exchange.Method == method)?.Exchange,
                WriteCapability = "Not tested; writes disabled for other controls. See ControlCapabilities for context-specific contrast evidence."
            }),
            Unresolved = new[] { "Verify readback against manual TV changes", "Confirm brightness/backlight/shadow-detail mapping", "Advanced white balance and custom color capabilities remain unverified" }
        }, JsonOptions);
        if (redactCertificateFingerprints) json = IpRemoteReportRedactor.RedactCertificateFingerprints(json);
        return ProtocolMessageFormatter.FormatJson(json, revealSensitive: false, revealDeviceIdentifiers: !redactIdentifiers);
    }

    private async Task RecordExchangeAsync(IpRemoteProfile profile, string label, SamsungIpRemoteExchange exchange, bool pairing = false)
    {
        var observation = new IpRemoteObservation(profile, label.Trim(), exchange);
        Update(current => current with
        {
            Observations = current.Observations.Append(observation).TakeLast(100).ToArray(),
            Status = exchange.Message,
            HasToken = exchange.Outcome != SamsungIpRemoteOutcome.NotPaired && (pairing && exchange.IsSuccess || current.HasToken),
            AuthorizationRejected = exchange.Outcome == SamsungIpRemoteOutcome.Unauthorized
                || (!(pairing && exchange.IsSuccess) && current.AuthorizationRejected)
        });
        try
        {
            await File.AppendAllTextAsync(DiagnosticLogPath, JsonSerializer.Serialize(observation) + Environment.NewLine).ConfigureAwait(false);
            RestrictFile(DiagnosticLogPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Update(current => current with { StorageWarning = "The response is visible, but its private diagnostic log could not be saved. Download the report before closing." });
        }
    }

    private async Task EnterAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("An IP Remote action is already running. Cancel it or wait for it to finish.");
    }

    private async Task SaveProfilesAsync(IReadOnlyList<IpRemoteProfile> profiles, string endpoint)
    {
        var path = Path.Combine(_directory, "profiles.json");
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new SavedProfiles(endpoint, profiles), JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporaryPath);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void Update(Func<IpRemoteSnapshot, IpRemoteSnapshot> change)
    {
        lock (_sync) _snapshot = change(_snapshot);
        Changed?.Invoke();
    }

    public void Dispose() { Cancel(); }
    private sealed record SavedProfiles(string? ActiveEndpoint, IReadOnlyList<IpRemoteProfile> Profiles);
}
