using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Automation.Macros;
using SamsungController.Automation.Navigation;
using SamsungController.Core.Connection;
using SamsungController.Core.Devices;
using SamsungController.Core.Diagnostics;
using SamsungController.Core.Protocol;

namespace SamsungController.Web.Services;

public sealed class SamsungControllerService : IAsyncDisposable
{
    private const int MessageCapacity = 500;
    private const int MacroProgressCapacity = 500;
    private const int DeviceInfoObservationCapacity = 20;
    private const int QuickAccessCapacity = 12;
    private static readonly QuickAccessAction DefaultReturnToVideoAction = new(
        "menuanchor:normal-video",
        "Return to video",
        QuickAccessActionKind.MenuAnchor,
        "normal-video");

    private readonly object _sync = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly SemaphoreSlim _menuDefinitionGate = new(1, 1);
    private readonly string _configurationDirectory;
    private readonly string _settingsPath;
    private readonly string _tokenPath;
    private readonly string _defaultMenuDefinitionPath;
    private readonly JsonFileSamsungTokenStore _tokenStore;
    private readonly NdjsonProtocolLogger _logger;
    private readonly SamsungTvClient _client;
    private readonly ISamsungDeviceInfoClient _deviceInfoClient;
    private readonly IMenuDelay _menuDelay;
    private readonly List<SamsungMessage> _messages = [];
    private readonly List<MacroExecutionProgress> _macroProgress = [];
    private readonly List<DeviceInfoObservation> _deviceInfoObservations = [];
    private readonly MenuTraversalRecorder _menuRecorder = new();

    private SamsungWebSettings _settings = new();
    private CancellationTokenSource? _macroSource;
    private CancellationTokenSource? _navigationSource;
    private MenuDefinition? _menuDefinition;
    private MenuStateTracker? _menuStateTracker;
    private MenuNavigator? _menuNavigator;
    private NavigationPlan? _navigationPlan;
    private bool _initialized;
    private bool _hasToken;
    private int _macroRunning;
    private int _navigationRunning;
    private int _deviceInfoQuerying;
    private string? _activeMacro;
    private string? _lastMacroStatus;
    private string? _navigationStatus;
    private string? _navigationError;
    private NavigationProgress? _navigationProgress;
    private MenuValidationSession? _menuValidation;
    private MenuTimingValidationSession? _menuTimingValidation;
    private MenuReturnValidationSession? _menuReturnValidation;
    private string? _menuAuthoringStatus;
    private string? _menuAuthoringError;
    private string? _lastError;
    private bool _disposed;

    public SamsungControllerService(IConfiguration configuration)
        : this(
            configuration,
            new ClientWebSocketSamsungTransport(),
            SystemMenuDelay.Instance,
            new SamsungDeviceInfoClient())
    {
    }

    internal SamsungControllerService(
        IConfiguration configuration,
        ISamsungTransport transport,
        IMenuDelay menuDelay,
        ISamsungDeviceInfoClient? deviceInfoClient = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(menuDelay);
        _configurationDirectory = Path.GetFullPath(
            configuration["SamsungController:ConfigurationDirectory"]
            ?? WebApplicationPaths.GetDefaultConfigurationDirectory());
        _settingsPath = Path.Combine(_configurationDirectory, "settings.json");
        _tokenPath = Path.Combine(_configurationDirectory, "tokens.json");
        _defaultMenuDefinitionPath = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions",
            "s95f-draft.yaml");
        ProtocolLogPath = Path.Combine(
            _configurationDirectory,
            "sessions",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-web.ndjson");

        _tokenStore = new JsonFileSamsungTokenStore(_tokenPath);
        _logger = new NdjsonProtocolLogger(ProtocolLogPath);
        _client = new SamsungTvClient(
            transport,
            _tokenStore,
            [_logger]);
        _menuDelay = menuDelay;
        _deviceInfoClient = deviceInfoClient ?? new SamsungDeviceInfoClient();
        _client.ConnectionStateChanged += HandleConnectionStateChanged;
        _client.MessageObserved += HandleMessageObserved;
    }

    public event Action? Changed;

    public string ProtocolLogPath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var settings = await SamsungWebSettings.LoadAsync(_settingsPath, cancellationToken)
                .ConfigureAwait(false);
            var hasToken = settings.Host is not null
                && await _tokenStore.LoadAsync(settings.Host, cancellationToken).ConfigureAwait(false) is not null;
            MenuDefinition? menuDefinition = null;
            MenuStateTracker? menuStateTracker = null;
            MenuNavigator? menuNavigator = null;
            string? navigationError = null;
            try
            {
                menuDefinition = await LoadValidatedMenuDefinitionAsync(
                        GetMenuDefinitionPath(settings),
                        cancellationToken)
                    .ConfigureAwait(false);
                menuStateTracker = new MenuStateTracker(menuDefinition);
                menuNavigator = new MenuNavigator(
                    menuDefinition,
                    menuStateTracker,
                    new WebMenuCommandTarget(_client));
                menuStateTracker.Changed += HandleMenuStateChanged;
                menuNavigator.ProgressChanged += HandleNavigationProgress;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                navigationError = exception.Message;
            }

            lock (_sync)
            {
                _settings = settings;
                _hasToken = hasToken;
                _menuDefinition = menuDefinition;
                _menuStateTracker = menuStateTracker;
                _menuNavigator = menuNavigator;
                _navigationError = navigationError;
                _initialized = true;
            }
        }
        finally
        {
            _initializationGate.Release();
        }

        NotifyChanged();
    }

    public ControllerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var macroPath = Path.GetFullPath(
                _settings.MacroFilePath
                ?? Path.Combine(_configurationDirectory, "macros.yaml"));
            var menuState = _menuStateTracker?.Current;
            return new ControllerSnapshot(
                _settings.Name,
                _settings.Host,
                _settings.ApplicationName,
                _settings.Secure,
                _settings.Port,
                _settings.AllowUntrustedCertificate,
                macroPath,
                _client.State,
                _client.ConnectionGeneration,
                _hasToken,
                ProtocolLogPath,
                _lastError,
                Volatile.Read(ref _macroRunning) == 1,
                _activeMacro,
                _lastMacroStatus,
                Volatile.Read(ref _navigationRunning) == 1,
                menuState?.Path,
                menuState?.Confidence ?? MenuStateConfidence.Unknown);
        }
    }

    public DeviceInfoSnapshot GetDeviceInfoSnapshot()
    {
        lock (_sync)
        {
            return new DeviceInfoSnapshot(
                Volatile.Read(ref _deviceInfoQuerying) == 1,
                _deviceInfoObservations.ToArray());
        }
    }

    public IReadOnlyList<QuickAccessAction> GetQuickAccessActions()
    {
        lock (_sync)
        {
            return NormalizeQuickAccess(_settings.QuickAccess).ToArray();
        }
    }

    public IReadOnlyList<SamsungMessage> GetMessages()
    {
        lock (_sync)
        {
            return _messages.ToArray();
        }
    }

    public MacroRunSnapshot GetMacroRunSnapshot()
    {
        lock (_sync)
        {
            return new MacroRunSnapshot(
                Volatile.Read(ref _macroRunning) == 1,
                _activeMacro,
                _lastMacroStatus,
                _macroProgress.ToArray());
        }
    }

    public MenuNavigationSnapshot GetMenuNavigationSnapshot()
    {
        lock (_sync)
        {
            var definition = _menuDefinition;
            var nodes = definition is null
                ? []
                : definition.Nodes.Values.Select(node => new MenuNodeSummary(
                        node.Id,
                        node.Label,
                        definition.GetPath(node.Id),
                        definition.GetDepth(node.Id),
                        node.Description,
                        definition.Transitions.Values.Any(transition =>
                            transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        || definition.Anchors.Values.Any(anchor =>
                            anchor.Verified
                            && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                        definition.Transitions.Values.Any(transition =>
                            !transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))))
                    .ToArray();
            var anchors = definition is null
                ? []
                : definition.Anchors.Values.Select(anchor => new MenuAnchorSummary(
                        anchor.Id,
                        anchor.Label,
                        definition.GetPath(anchor.TargetNodeId),
                        anchor.Description,
                        anchor.Verified,
                        anchor.Operations.Sum(operation => operation.Repeat)))
                    .ToArray();
            return new MenuNavigationSnapshot(
                GetMenuDefinitionPath(_settings),
                definition?.Name,
                definition?.Model,
                definition?.Context,
                nodes,
                anchors,
                _menuStateTracker?.Current ?? new MenuState(
                    null,
                    null,
                    MenuStateConfidence.Unknown,
                    "No valid menu definition is loaded.",
                    DateTimeOffset.UtcNow),
                _navigationPlan,
                Volatile.Read(ref _navigationRunning) == 1,
                _navigationStatus,
                _navigationProgress,
                _navigationError);
        }
    }

    public MenuAuthoringSnapshot GetMenuAuthoringSnapshot()
    {
        lock (_sync)
        {
            var definition = _menuDefinition;
            var candidates = definition is null
                ? []
                : definition.Anchors.Values
                    .Where(anchor => !anchor.Verified)
                    .Select(anchor => new MenuAuthoringCandidateSummary(
                        MenuAuthoringItemKind.Anchor,
                        anchor.Id,
                        anchor.Label,
                        null,
                        anchor.TargetNodeId,
                        null,
                        definition.GetPath(anchor.TargetNodeId),
                        anchor.Operations.Sum(operation => operation.Repeat),
                        GetReplaySteps(anchor.Operations, definition.Timing)))
                    .Concat(definition.Transitions.Values
                        .Where(transition => !transition.Verified)
                        .Select(transition => new MenuAuthoringCandidateSummary(
                            MenuAuthoringItemKind.Transition,
                            transition.Id,
                            definition.GetRequiredNode(transition.ToNodeId).Label,
                            transition.FromNodeId,
                            transition.ToNodeId,
                            definition.GetPath(transition.FromNodeId),
                            definition.GetPath(transition.ToNodeId),
                            transition.Operations.Sum(operation => operation.Repeat),
                            GetReplaySteps(transition.Operations, definition.Timing))))
                    .ToArray();
            var timingTestRoutes = definition is null
                ? []
                : GetTimingTestRoutes(definition);
            var returnStrategy = definition is null
                ? null
                : GetReturnStrategySummary(definition, _menuReturnValidation);
            var request = _menuRecorder.Request;
            var steps = _menuRecorder.Operations
                .Select(operation => new MenuRecordedStepSummary(
                    operation.Key,
                    operation.Action,
                    operation.Repeat,
                    operation.DelayAfter ?? _menuRecorder.Timing.GetDelay(operation.Key)))
                .ToArray();
            return new MenuAuthoringSnapshot(
                _menuRecorder.IsRecording,
                request?.Kind,
                request?.ItemId,
                request?.Label,
                request?.SourceNodeId,
                request?.TargetNodeId,
                steps,
                steps.Sum(step => step.Repeat),
                definition?.Timing ?? new MenuTimingProfile(),
                returnStrategy,
                timingTestRoutes,
                _menuTimingValidation?.TransitionId,
                _menuTimingValidation?.Passes ?? 0,
                MenuTimingValidationSession.RequiredPasses,
                _menuTimingValidation?.AwaitingConfirmation ?? false,
                _menuTimingValidation?.ExpectedTargetPath,
                candidates,
                _menuValidation?.Kind,
                _menuValidation?.ItemId,
                _menuValidation?.Passes ?? 0,
                MenuValidationSession.RequiredPasses,
                _menuValidation?.AwaitingConfirmation ?? false,
                _menuValidation?.ExpectedTargetPath,
                _menuAuthoringStatus,
                _menuAuthoringError);
        }
    }

    public async Task ConnectAsync(
        TvConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoMenuRecording("connect");
        SetLastError(null);

        try
        {
            var settings = GetSettings();
            var options = new SamsungConnectionOptions
            {
                Host = request.Host.Trim(),
                ApplicationName = settings.ApplicationName,
                Secure = request.Secure,
                Port = request.Port,
                AllowUntrustedCertificate = request.AllowUntrustedCertificate,
                AutoReconnect = false,
                PairingTimeout = TimeSpan.FromSeconds(90)
            };
            await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            await UpdateSettingsAsync(
                    current => current with
                    {
                        Host = request.Host.Trim(),
                        Name = string.IsNullOrWhiteSpace(request.DisplayName)
                            ? "Samsung TV"
                            : request.DisplayName.Trim(),
                        Secure = request.Secure,
                        Port = request.Port,
                        AllowUntrustedCertificate = request.AllowUntrustedCertificate
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                _hasToken = _client.Token is not null;
            }

            NotifyChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetLastError(exception.Message);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNoAutomationRunning("disconnect");
        EnsureNoMenuRecording("disconnect");
        await _client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _menuStateTracker?.ReduceConfidence(
            "Connection closed; physical remote use or OSD timeout may change the menu before reconnecting.");
    }

    public async Task SendKeyAsync(
        string key,
        RemoteKeyAction action = RemoteKeyAction.Click,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureNoAutomationRunning("send a manual key");
        await SendTrackedKeyAsync(key, action, cancellationToken).ConfigureAwait(false);
        var recorded = false;
        lock (_sync)
        {
            if (_menuRecorder.IsRecording)
            {
                _menuRecorder.Record(key, action);
                recorded = true;
            }
        }

        if (recorded)
        {
            NotifyChanged();
        }
    }

    public async Task SendRawAsync(
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Raw payload must be a JSON object.", nameof(rawJson));
        }

        EnsureNoAutomationRunning("send a raw request");
        EnsureNoMenuRecording("send a raw request");
        await _client.SendRawAsync(rawJson.Trim(), cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _navigationPlan = null;
        }

        _menuStateTracker?.MarkUnknown(
            "A raw protocol request may have changed the TV menu outside the navigation model.");
    }

    public async Task SendQueryAsync(
        SamsungQuery query,
        CancellationToken cancellationToken = default)
    {
        EnsureNoAutomationRunning("send a research query");
        EnsureNoMenuRecording("send a research query");
        await _client.SendQueryAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfoObservation> ProbeDeviceInfoAsync(
        string? label,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _deviceInfoQuerying, 1) == 1)
        {
            throw new InvalidOperationException("A device-information probe is already running.");
        }

        NotifyChanged();
        try
        {
            SamsungWebSettings settings;
            lock (_sync)
            {
                settings = _settings;
            }

            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                throw new InvalidOperationException("Configure a TV host before probing device information.");
            }

            var observationLabel = string.IsNullOrWhiteSpace(label)
                ? "Unlabeled state"
                : label.Trim();
            var request = new SamsungDeviceInfoRequest
            {
                Host = settings.Host,
                Secure = settings.Secure,
                Port = settings.Port,
                AllowUntrustedCertificate = settings.AllowUntrustedCertificate
            };
            var endpoint = SamsungDeviceInfoClient.GetEndpoint(request);
            await RecordProtocolMessageAsync(
                    CreateDeviceInfoRequestMessage(endpoint, observationLabel),
                    cancellationToken)
                .ConfigureAwait(false);

            DeviceInfoObservation observation;
            try
            {
                var response = await _deviceInfoClient.GetAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                observation = new DeviceInfoObservation(
                    response.Timestamp,
                    observationLabel,
                    response.Endpoint.AbsoluteUri,
                    (int)response.StatusCode,
                    response.IsSuccessStatusCode,
                    response.RawContent,
                    response.ParseError,
                    null);
                await RecordProtocolMessageAsync(
                        new SamsungMessage(
                            response.Timestamp,
                            SamsungMessageDirection.Rx,
                            response.Endpoint.AbsoluteUri,
                            $"HTTP {(int)response.StatusCode} · {observationLabel}",
                            response.ParsedPayload,
                            response.RawContent,
                            response.ParseError,
                            _client.ConnectionGeneration),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                observation = new DeviceInfoObservation(
                    DateTimeOffset.UtcNow,
                    observationLabel,
                    endpoint.AbsoluteUri,
                    null,
                    false,
                    string.Empty,
                    null,
                    exception.Message);
            }

            lock (_sync)
            {
                _deviceInfoObservations.Add(observation);
                if (_deviceInfoObservations.Count > DeviceInfoObservationCapacity)
                {
                    _deviceInfoObservations.RemoveRange(
                        0,
                        _deviceInfoObservations.Count - DeviceInfoObservationCapacity);
                }
            }

            return observation;
        }
        finally
        {
            Interlocked.Exchange(ref _deviceInfoQuerying, 0);
            NotifyChanged();
        }
    }

    public void ClearDeviceInfoObservations()
    {
        lock (_sync)
        {
            _deviceInfoObservations.Clear();
        }

        NotifyChanged();
    }

    public async Task SetMenuDefinitionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change the menu definition");
        EnsureNoMenuRecording("change the menu definition");
        var fullPath = Path.GetFullPath(path.Trim());
        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definition = await LoadValidatedMenuDefinitionAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            await UpdateSettingsAsync(
                    current => current with { MenuDefinitionPath = fullPath },
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(definition);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_sync)
            {
                _navigationError = exception.Message;
            }

            NotifyChanged();
            throw;
        }
        finally
        {
            _menuDefinitionGate.Release();
        }
    }

    public async Task CreateMenuDefinitionAsync(
        MenuDefinitionCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("create a menu definition");
        EnsureNoMenuRecording("create a menu definition");

        var definition = new MenuDefinition(
            request.Id.Trim(),
            request.Name.Trim(),
            request.Model.Trim(),
            new MenuDefinitionContext(
                NormalizeContextValue(request.Firmware),
                NormalizeContextValue(request.Signal),
                NormalizeContextValue(request.PictureMode),
                NormalizeContextValue(request.Input)),
            [
                new MenuNode("tv-interface", "TV interface", Description: "Root for the modeled TV interface."),
                new MenuNode("normal-video", "Normal video", "tv-interface", "No TV menu is expected to be visible.")
            ],
            [],
            []);
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        var path = Path.Combine(
            _configurationDirectory,
            "menu-definitions",
            $"{definition.Id}.yaml");
        if (File.Exists(path))
        {
            throw new IOException(
                $"A menu definition already exists at '{path}'. Load it or choose a different identifier.");
        }

        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new MenuDefinitionWriter()
                .WriteFileAsync(path, definition, cancellationToken)
                .ConfigureAwait(false);
            await UpdateSettingsAsync(
                    current => current with { MenuDefinitionPath = path },
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(definition);
            lock (_sync)
            {
                _menuRecorder.Reset();
                _menuValidation = null;
                _menuAuthoringStatus = "New menu definition created · record an anchor to establish a known starting point";
                _menuAuthoringError = null;
            }
        }
        finally
        {
            _menuDefinitionGate.Release();
        }

        NotifyChanged();
    }

    public void StartMenuRecording(MenuRecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureNoAutomationRunning("start a menu recording");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException("Connect to the TV before recording menu buttons.");
        }

        MenuDefinition definition;
        lock (_sync)
        {
            if (_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException("A menu traversal recording is already active.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException(
                    _navigationError ?? "Create or load a menu definition before recording.");
        }

        var normalized = NormalizeRecordingRequest(request).ResolveIdentity(definition);
        ValidateRecordingRequest(definition, normalized);
        lock (_sync)
        {
            _menuRecorder.Start(normalized, definition.Timing);
            _menuValidation = null;
            _menuAuthoringStatus = "Recording · every successful UI key is being sent to the TV and captured";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public void UndoLastRecordedMenuCommand()
    {
        lock (_sync)
        {
            _menuRecorder.UndoLastCommand();
        }

        NotifyChanged();
    }

    public void ClearRecordedMenuCommands()
    {
        lock (_sync)
        {
            _menuRecorder.ClearCommands();
        }

        NotifyChanged();
    }

    public async Task StopAndSaveMenuRecordingAsync(
        CancellationToken cancellationToken = default)
    {
        MenuDefinition definition;
        MenuRecordingRequest request;
        MenuOperation[] operations;
        lock (_sync)
        {
            if (!_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException("No menu traversal recording is active.");
            }

            if (_menuRecorder.Operations.Count == 0)
            {
                throw new InvalidOperationException("Send at least one successful key before saving the recording.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            request = _menuRecorder.Request!;
            operations = _menuRecorder.Operations.ToArray();
            _menuRecorder.Stop();
        }

        try
        {
            var updated = AddDraftRecording(definition, request, operations);
            await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _menuValidation = new MenuValidationSession(
                    request.Kind,
                    request.ItemId,
                    0,
                    false,
                    updated.GetPath(request.TargetNodeId));
                _menuAuthoringStatus = $"Draft {request.Kind.ToString().ToLowerInvariant()} saved to YAML · ready for validation";
                _menuAuthoringError = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_sync)
            {
                _menuRecorder.Resume();
                _menuAuthoringError = exception.Message;
            }

            throw;
        }
        finally
        {
            NotifyChanged();
        }
    }

    public async Task UpdateMenuTimingProfileAsync(
        MenuTimingProfile timing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timing);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change system menu timing");
        EnsureNoMenuRecording("change system menu timing");

        MenuDefinition definition;
        MenuValidationSession? previousDraftValidation;
        MenuTimingValidationSession? previousTimingValidation;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            previousDraftValidation = _menuValidation;
            previousTimingValidation = _menuTimingValidation;
        }

        var delaysChanged = !definition.Timing.HasSameDelays(timing);
        var normalizedTiming = timing with
        {
            Verified = !delaysChanged && definition.Timing.Verified
        };
        var updated = CopyMenuDefinition(definition, timing: normalizedTiming);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuValidation = delaysChanged ? null : previousDraftValidation;
            _menuTimingValidation = delaysChanged ? null : previousTimingValidation;
            _menuAuthoringStatus = delaysChanged
                ? "System timing saved to YAML · profile validation reset to 0/3"
                : "System timing saved to YAML";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task RunMenuTimingProfileTestAsync(
        string transitionId,
        MenuTimingProfile timing,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionId);
        ArgumentNullException.ThrowIfNull(timing);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("test system menu timing");
        EnsureNoMenuRecording("test system menu timing");

        MenuDefinition definition;
        MenuTimingValidationSession? previousSession;
        MenuValidationSession? previousDraftValidation;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            previousSession = _menuTimingValidation;
            previousDraftValidation = _menuValidation;
            if (previousSession?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm whether the previous system timing test passed before replaying it.");
            }

            if (previousDraftValidation?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm the pending draft replay before testing the system timing profile.");
            }

            if (_menuReturnValidation?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm the pending return-script test before testing the system timing profile.");
            }
        }

        var transition = definition.Transitions.TryGetValue(transitionId, out var candidate)
            ? candidate
            : throw new KeyNotFoundException($"Menu transition '{transitionId}' was not found.");
        var delaysChanged = !definition.Timing.HasSameDelays(timing);
        var normalizedTiming = timing with { Verified = false };
        var updated = CopyMenuDefinition(definition, timing: normalizedTiming);
        new MenuDefinitionValidator().ValidateAndThrow(updated);

        var canContinue = !delaysChanged
                          && previousSession is { AwaitingConfirmation: false }
                          && previousSession.TransitionId.Equals(
                              transitionId,
                              StringComparison.OrdinalIgnoreCase)
                          && previousSession.Timing.HasSameDelays(normalizedTiming)
                          && previousSession.Passes < MenuTimingValidationSession.RequiredPasses;
        var session = canContinue
            ? previousSession! with { Timing = normalizedTiming }
            : new MenuTimingValidationSession(
                transition.Id,
                normalizedTiming,
                0,
                false,
                updated.GetPath(transition.ToNodeId));

        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            _menuTimingValidation = session;
            _menuValidation = delaysChanged ? null : previousDraftValidation;

            _menuAuthoringStatus = canContinue
                ? $"System timing test · pass {session.Passes + 1}/{MenuTimingValidationSession.RequiredPasses}"
                : "System timing saved · starting visual validation at 0/3";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        await ExecuteMenuTimingProfileTestAsync(definition, session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ConfirmMenuTimingProfileTestAsync(
        bool passed,
        CancellationToken cancellationToken = default)
    {
        MenuTimingValidationSession session;
        MenuDefinition definition;
        lock (_sync)
        {
            session = _menuTimingValidation
                ?? throw new InvalidOperationException("No system timing validation is active.");
            if (!session.AwaitingConfirmation)
            {
                throw new InvalidOperationException(
                    "Run the system timing test before confirming its result.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        if (!definition.Timing.HasSameDelays(session.Timing))
        {
            throw new InvalidOperationException(
                "The system timing values changed after this replay. Run the test again before confirming it.");
        }

        if (!passed)
        {
            lock (_sync)
            {
                _menuTimingValidation = session with
                {
                    Passes = 0,
                    AwaitingConfirmation = false
                };
                _menuAuthoringStatus = "System timing test failed · profile validation reset to 0/3";
                _menuAuthoringError = null;
            }

            _menuStateTracker?.MarkUnknown(
                "The user reported that the system timing test did not reach its target.");
            NotifyChanged();
            return;
        }

        var passes = session.Passes + 1;
        if (passes < MenuTimingValidationSession.RequiredPasses)
        {
            lock (_sync)
            {
                _menuTimingValidation = session with
                {
                    Passes = passes,
                    AwaitingConfirmation = false
                };
                _menuAuthoringStatus =
                    $"System timing test passed · {passes}/{MenuTimingValidationSession.RequiredPasses}";
                _menuAuthoringError = null;
            }

            NotifyChanged();
            return;
        }

        var verified = CopyMenuDefinition(
            definition,
            timing: session.Timing with { Verified = true });
        await PersistActiveMenuDefinitionAsync(verified, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuTimingValidation = session with
            {
                Passes = MenuTimingValidationSession.RequiredPasses,
                AwaitingConfirmation = false,
                Timing = session.Timing with { Verified = true }
            };
            _menuAuthoringStatus =
                $"System timing verified · {MenuTimingValidationSession.RequiredPasses}/{MenuTimingValidationSession.RequiredPasses} passes saved to YAML";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task UpdateMenuReturnStrategyAsync(
        IReadOnlyList<string> atMenuRootKeys,
        IReadOnlyList<string> belowMenuRootKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(atMenuRootKeys);
        ArgumentNullException.ThrowIfNull(belowMenuRootKeys);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change return-to-video scripts");
        EnsureNoMenuRecording("change return-to-video scripts");

        MenuDefinition definition;
        MenuValidationSession? previousDraftValidation;
        MenuTimingValidationSession? previousTimingValidation;
        MenuReturnValidationSession? previousReturnValidation;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            previousDraftValidation = _menuValidation;
            previousTimingValidation = _menuTimingValidation;
            previousReturnValidation = _menuReturnValidation;
        }

        var context = GetRequiredReturnStrategyContext(definition);
        var atMenuRoot = UpdateReturnScript(context.Strategy.AtMenuRoot, atMenuRootKeys);
        var belowMenuRoot = UpdateReturnScript(context.Strategy.BelowMenuRoot, belowMenuRootKeys);
        var atMenuRootChanged = atMenuRoot != context.Strategy.AtMenuRoot;
        var belowMenuRootChanged = belowMenuRoot != context.Strategy.BelowMenuRoot;
        var strategy = new MenuReturnStrategy(
            context.Strategy.MenuRootNodeId,
            atMenuRoot,
            belowMenuRoot);
        var anchors = definition.Anchors.Values
            .Select(anchor => anchor.Id.Equals(context.Anchor.Id, StringComparison.OrdinalIgnoreCase)
                ? anchor with { ReturnStrategy = strategy }
                : anchor)
            .ToArray();
        var updated = CopyMenuDefinition(definition, anchors: anchors);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);

        var activeScriptChanged = previousReturnValidation?.Kind switch
        {
            MenuReturnScriptKind.AtMenuRoot => atMenuRootChanged,
            MenuReturnScriptKind.BelowMenuRoot => belowMenuRootChanged,
            _ => false
        };
        lock (_sync)
        {
            _menuValidation = previousDraftValidation;
            _menuTimingValidation = previousTimingValidation;
            _menuReturnValidation = activeScriptChanged ? null : previousReturnValidation;
            _menuAuthoringStatus = atMenuRootChanged || belowMenuRootChanged
                ? "Return-to-video scripts saved to YAML · changed scripts require 3/3 validation"
                : "Return-to-video scripts saved to YAML";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task RunMenuReturnStrategyTestAsync(
        MenuReturnScriptKind kind,
        string? deepStartNodeId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("test a return-to-video script");
        EnsureNoMenuRecording("test a return-to-video script");

        MenuDefinition definition;
        MenuReturnValidationSession? previousSession;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            previousSession = _menuReturnValidation;
            if (previousSession?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm whether the previous return-script test passed before replaying it.");
            }

            if (_menuValidation?.AwaitingConfirmation == true
                || _menuTimingValidation?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm the pending visual test before testing a return-to-video script.");
            }
        }

        var context = GetRequiredReturnStrategyContext(definition);
        var script = kind == MenuReturnScriptKind.AtMenuRoot
            ? context.Strategy.AtMenuRoot
            : context.Strategy.BelowMenuRoot;
        var startNodeId = kind == MenuReturnScriptKind.AtMenuRoot
            ? context.Strategy.MenuRootNodeId
            : NormalizeDeepReturnTestNode(definition, context, deepStartNodeId);
        var signature = GetOperationSignature(script.Operations);
        var canContinue = previousSession is { AwaitingConfirmation: false }
                          && previousSession.Kind == kind
                          && previousSession.StartNodeId.Equals(
                              startNodeId,
                              StringComparison.OrdinalIgnoreCase)
                          && previousSession.ScriptSignature == signature
                          && previousSession.Passes < MenuReturnValidationSession.RequiredPasses;
        var session = canContinue
            ? previousSession!
            : new MenuReturnValidationSession(
                kind,
                startNodeId,
                signature,
                0,
                false,
                definition.GetPath(context.Anchor.TargetNodeId));
        lock (_sync)
        {
            _menuReturnValidation = session;
            _menuAuthoringStatus =
                $"Return script test · pass {session.Passes + 1}/{MenuReturnValidationSession.RequiredPasses}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        await ExecuteMenuReturnStrategyTestAsync(
                definition,
                context,
                script,
                session,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ConfirmMenuReturnStrategyTestAsync(
        bool passed,
        CancellationToken cancellationToken = default)
    {
        MenuReturnValidationSession session;
        MenuDefinition definition;
        MenuValidationSession? draftValidation;
        MenuTimingValidationSession? timingValidation;
        lock (_sync)
        {
            session = _menuReturnValidation
                ?? throw new InvalidOperationException("No return-to-video script validation is active.");
            if (!session.AwaitingConfirmation)
            {
                throw new InvalidOperationException(
                    "Run the return-to-video script before confirming its result.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            draftValidation = _menuValidation;
            timingValidation = _menuTimingValidation;
        }

        var context = GetRequiredReturnStrategyContext(definition);
        var currentScript = session.Kind == MenuReturnScriptKind.AtMenuRoot
            ? context.Strategy.AtMenuRoot
            : context.Strategy.BelowMenuRoot;
        if (GetOperationSignature(currentScript.Operations) != session.ScriptSignature)
        {
            throw new InvalidOperationException(
                "The return script changed after this replay. Run the test again before confirming it.");
        }

        if (!passed)
        {
            var unverified = SetReturnScriptVerified(definition, context, session.Kind, verified: false);
            await PersistActiveMenuDefinitionAsync(unverified, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _menuValidation = draftValidation;
                _menuTimingValidation = timingValidation;
                _menuReturnValidation = session with
                {
                    Passes = 0,
                    AwaitingConfirmation = false
                };
                _menuAuthoringStatus = "Return script test failed · validation reset to 0/3";
                _menuAuthoringError = null;
            }

            _menuStateTracker?.MarkUnknown(
                "The user reported that the return-to-video script did not reach normal video.");
            NotifyChanged();
            return;
        }

        var passes = session.Passes + 1;
        if (passes < MenuReturnValidationSession.RequiredPasses)
        {
            lock (_sync)
            {
                _menuReturnValidation = session with
                {
                    Passes = passes,
                    AwaitingConfirmation = false
                };
                _menuAuthoringStatus =
                    $"Return script test passed · {passes}/{MenuReturnValidationSession.RequiredPasses}";
                _menuAuthoringError = null;
            }

            NotifyChanged();
            return;
        }

        var verified = SetReturnScriptVerified(definition, context, session.Kind, verified: true);
        await PersistActiveMenuDefinitionAsync(verified, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuValidation = draftValidation;
            _menuTimingValidation = timingValidation;
            _menuReturnValidation = session with
            {
                Passes = MenuReturnValidationSession.RequiredPasses,
                AwaitingConfirmation = false
            };
            _menuAuthoringStatus =
                $"Return script verified · {MenuReturnValidationSession.RequiredPasses}/{MenuReturnValidationSession.RequiredPasses} passes saved to YAML";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task UpdateMenuAuthoringTimingAsync(
        MenuAuthoringItemKind kind,
        string itemId,
        MenuTimingProfile timing,
        IReadOnlyList<MenuAuthoringReplayStepUpdate> steps,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(steps);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change draft replay timing");
        EnsureNoMenuRecording("change draft replay timing");

        MenuDefinition definition;
        IReadOnlyList<MenuOperation> operations;
        MenuTimingValidationSession? previousTimingValidation;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            operations = GetDraftOperations(definition, kind, itemId);
            previousTimingValidation = _menuTimingValidation;
        }

        var expanded = ExpandOperations(operations);
        if (steps.Count != expanded.Count)
        {
            throw new InvalidOperationException(
                $"The draft contains {expanded.Count} button presses, but {steps.Count} timing values were supplied. Reopen the timing lab and try again.");
        }

        var tuned = new List<MenuOperation>(expanded.Count);
        for (var index = 0; index < expanded.Count; index++)
        {
            var step = steps[index];
            if (step.Position != index + 1)
            {
                throw new InvalidOperationException("Draft timing positions are out of date. Reopen the timing lab and try again.");
            }

            if (step.UseCustomDelay && step.DelayAfterMilliseconds is < 50 or > 30_000)
            {
                throw new InvalidOperationException(
                    $"Custom wait for press {step.Position} must be between 50 and 30000 milliseconds.");
            }

            tuned.Add(expanded[index] with
            {
                DelayAfter = step.UseCustomDelay
                    ? TimeSpan.FromMilliseconds(step.DelayAfterMilliseconds)
                    : null
            });
        }

        var coalesced = CoalesceOperations(tuned);
        var anchors = definition.Anchors.Values
            .Select(anchor => kind == MenuAuthoringItemKind.Anchor
                              && anchor.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)
                ? anchor with { Operations = coalesced }
                : anchor)
            .ToArray();
        var transitions = definition.Transitions.Values
            .Select(transition => kind == MenuAuthoringItemKind.Transition
                                  && transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)
                ? transition with { Operations = coalesced }
                : transition)
            .ToArray();
        var delaysChanged = !definition.Timing.HasSameDelays(timing);
        var normalizedTiming = timing with
        {
            Verified = !delaysChanged && definition.Timing.Verified
        };
        var updated = CopyMenuDefinition(
            definition,
            transitions,
            anchors,
            normalizedTiming);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);

        var expectedTargetPath = GetAuthoringTargetPath(updated, kind, itemId);
        lock (_sync)
        {
            _menuTimingValidation = delaysChanged ? null : previousTimingValidation;
            _menuValidation = new MenuValidationSession(
                kind,
                itemId.Trim(),
                0,
                false,
                expectedTargetPath);
            _menuAuthoringStatus = "System timing and button overrides saved to YAML · validation restarted at 0/3";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task RunMenuAuthoringValidationAsync(
        MenuAuthoringItemKind kind,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("validate a recorded menu item");
        EnsureNoMenuRecording("validate a recorded menu item");

        MenuDefinition definition;
        MenuValidationSession session;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            session = _menuValidation is { } existing
                      && existing.Kind == kind
                      && existing.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase)
                ? existing
                : new MenuValidationSession(
                    kind,
                    itemId.Trim(),
                    0,
                    false,
                    GetAuthoringTargetPath(definition, kind, itemId));
            if (session.AwaitingConfirmation)
            {
                throw new InvalidOperationException("Confirm whether the previous validation run passed before replaying it.");
            }

            if (_menuTimingValidation?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm the pending system timing test before replaying a draft.");
            }

            if (_menuReturnValidation?.AwaitingConfirmation == true)
            {
                throw new InvalidOperationException(
                    "Confirm the pending return-script test before replaying a draft.");
            }

            _menuValidation = session;
            _menuAuthoringError = null;
        }

        await ExecuteMenuAuthoringValidationAsync(definition, session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PrepareMenuRecordingSourceAsync(
        string sourceNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoMenuRecording("prepare a recording source");
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        definition.GetRequiredNode(sourceNodeId);
        var setup = FindValidationSetup(definition, sourceNodeId);
        await RunNavigationAsync(
                $"Prepare recording source · {definition.GetPath(sourceNodeId)}",
                async (navigator, token) =>
                {
                    await navigator.ExecuteAnchorAsync(setup.Anchor.Id, token)
                        .ConfigureAwait(false);
                    if (setup.Plan.Transitions.Count > 0)
                    {
                        await navigator.ExecutePlanAsync(setup.Plan, token)
                            .ConfigureAwait(false);
                    }
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Source ready · {definition.GetPath(sourceNodeId)}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task DeleteMenuAuthoringDraftAsync(
        MenuAuthoringItemKind kind,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("delete a menu draft");
        EnsureNoMenuRecording("delete a menu draft");
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        if (kind == MenuAuthoringItemKind.Anchor)
        {
            var anchor = definition.GetRequiredAnchor(itemId);
            if (anchor.Verified)
            {
                throw new InvalidOperationException("Verified anchors cannot be deleted from the draft workflow.");
            }
        }
        else
        {
            var transition = definition.Transitions.TryGetValue(itemId, out var existing)
                ? existing
                : throw new KeyNotFoundException($"Menu transition '{itemId}' was not found.");
            if (transition.Verified)
            {
                throw new InvalidOperationException("Verified transitions cannot be deleted from the draft workflow.");
            }
        }

        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            definition.Transitions.Values.Where(transition =>
                kind != MenuAuthoringItemKind.Transition
                || !transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)),
            definition.Anchors.Values.Where(anchor =>
                kind != MenuAuthoringItemKind.Anchor
                || !anchor.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)),
            definition.Timing);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuRecorder.Reset();
            _menuValidation = null;
            _menuAuthoringStatus = $"Draft deleted · {itemId}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task ConfirmMenuAuthoringValidationAsync(
        bool passed,
        CancellationToken cancellationToken = default)
    {
        MenuValidationSession session;
        MenuDefinition definition;
        lock (_sync)
        {
            session = _menuValidation
                ?? throw new InvalidOperationException("No menu validation is active.");
            if (!session.AwaitingConfirmation)
            {
                throw new InvalidOperationException("Run the recorded item before confirming its result.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        if (!passed)
        {
            lock (_sync)
            {
                _menuValidation = session with { Passes = 0, AwaitingConfirmation = false };
                _menuAuthoringStatus = "Validation failed · pass count reset; adjust or replace the draft recording";
                _menuAuthoringError = null;
            }

            _menuStateTracker?.MarkUnknown("The user reported that a recorded menu validation did not reach its target.");
            NotifyChanged();
            return;
        }

        var passes = session.Passes + 1;
        if (passes < MenuValidationSession.RequiredPasses)
        {
            lock (_sync)
            {
                _menuValidation = session with { Passes = passes, AwaitingConfirmation = false };
                _menuAuthoringStatus = $"Validation passed · {passes}/{MenuValidationSession.RequiredPasses}";
                _menuAuthoringError = null;
            }

            NotifyChanged();
            return;
        }

        var verified = SetAuthoringItemVerified(definition, session.Kind, session.ItemId);
        await PersistActiveMenuDefinitionAsync(verified, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuValidation = session with
            {
                Passes = MenuValidationSession.RequiredPasses,
                AwaitingConfirmation = false
            };
            _menuAuthoringStatus = $"Verified · {session.ItemId} passed {MenuValidationSession.RequiredPasses}/{MenuValidationSession.RequiredPasses} and YAML was updated";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public NavigationPlan CreateNavigationPlan(string targetNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        MenuNavigator navigator;
        lock (_sync)
        {
            navigator = _menuNavigator
                ?? throw new InvalidOperationException(
                    _navigationError ?? "No valid menu definition is loaded.");
        }

        try
        {
            var plan = navigator.Plan(targetNodeId, includeDraftTransitions: true);
            lock (_sync)
            {
                _navigationPlan = plan;
                _navigationError = null;
                _navigationStatus = plan.UsesDraftTransitions
                    ? "Draft route previewed · execution blocked"
                    : $"Plan ready · {plan.CommandCount} commands";
                _navigationProgress = null;
            }

            NotifyChanged();
            return plan;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationError = exception.Message;
            }

            NotifyChanged();
            throw;
        }
    }

    public Task RunMenuAnchorAsync(
        string anchorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorId);
        return RunNavigationAsync(
            $"Anchor · {anchorId}",
            (navigator, token) => navigator.ExecuteAnchorAsync(anchorId, token),
            clearPlanOnSuccess: true,
            cancellationToken);
    }

    public Task ExecuteNavigationPlanAsync(CancellationToken cancellationToken = default)
    {
        NavigationPlan plan;
        lock (_sync)
        {
            plan = _navigationPlan
                ?? throw new InvalidOperationException("Create a navigation plan first.");
        }

        return RunNavigationAsync(
            $"Navigate · {plan.TargetPath}",
            (navigator, token) => navigator.ExecutePlanAsync(plan, token),
            clearPlanOnSuccess: true,
            cancellationToken);
    }

    public void CancelNavigation()
    {
        lock (_sync)
        {
            _navigationSource?.Cancel();
        }
    }

    public async Task<IReadOnlyList<MacroSummary>> LoadMacrosAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await LoadValidatedCatalogAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Macros.Values
            .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)
            .Select(macro => new MacroSummary(macro.Name, macro.Description, macro.Steps.Count))
            .ToArray();
    }

    public async Task<IReadOnlyList<MacroSummary>> SetMacroFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path.Trim());
        var catalog = await LoadValidatedCatalogAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        await UpdateSettingsAsync(
                current => current with { MacroFilePath = fullPath },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
        return catalog.Macros.Values
            .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)
            .Select(macro => new MacroSummary(macro.Name, macro.Description, macro.Steps.Count))
            .ToArray();
    }

    public async Task AddQuickAccessRemoteKeyAsync(
        string? label,
        string key,
        RemoteKeyAction action = RemoteKeyAction.Click,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var target = key.Trim();
        var item = new QuickAccessAction(
            CreateQuickAccessId(QuickAccessActionKind.RemoteKey, target, action),
            NormalizeQuickAccessLabel(label, target),
            QuickAccessActionKind.RemoteKey,
            target,
            action);
        await AddOrUpdateQuickAccessAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddQuickAccessMacroAsync(
        string macroName,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var target = macroName.Trim();
        var catalog = await LoadValidatedCatalogAsync(cancellationToken).ConfigureAwait(false);
        catalog.GetRequiredMacro(target);
        var item = new QuickAccessAction(
            CreateQuickAccessId(
                QuickAccessActionKind.Macro,
                target,
                RemoteKeyAction.Click),
            NormalizeQuickAccessLabel(label, target),
            QuickAccessActionKind.Macro,
            target);
        await AddOrUpdateQuickAccessAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveQuickAccessActionAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var existing = GetQuickAccessActions();
        var updated = existing
            .Where(action => !action.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (updated.Length == existing.Count)
        {
            return;
        }

        await UpdateSettingsAsync(
                current => current with { QuickAccess = updated },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
    }

    public async Task RunQuickAccessActionAsync(
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var action = GetQuickAccessActions().FirstOrDefault(candidate =>
            candidate.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Quick-access action '{actionId}' was not found.");
        EnsureNoMenuRecording("run a quick-access action");
        switch (action.Kind)
        {
            case QuickAccessActionKind.RemoteKey:
                await SendKeyAsync(action.Target, action.Action, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case QuickAccessActionKind.Macro:
                await RunMacroAsync(action.Target, cancellationToken).ConfigureAwait(false);
                break;

            case QuickAccessActionKind.MenuAnchor:
                await RunMenuAnchorAsync(action.Target, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException(
                    $"Quick-access action kind '{action.Kind}' is not supported.");
        }
    }

    public async Task RunMacroAsync(
        string macroName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        BeginAutomation(isNavigation: false);

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _macroSource = linkedSource;
            _activeMacro = macroName;
            _lastMacroStatus = "Preparing";
            _macroProgress.Clear();
            _lastError = null;
        }

        NotifyChanged();
        try
        {
            var catalog = await LoadValidatedCatalogAsync(linkedSource.Token).ConfigureAwait(false);
            catalog.GetRequiredMacro(macroName);
            var executor = new MacroExecutor(new WebMacroCommandTarget(this));
            executor.ProgressChanged += HandleMacroProgress;
            lock (_sync)
            {
                _lastMacroStatus = "Running";
            }

            NotifyChanged();
            var result = await executor.ExecuteAsync(catalog, macroName, linkedSource.Token)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _lastMacroStatus =
                    $"Completed · {result.KeysSent} keys · {result.Elapsed.TotalSeconds:0.###}s";
            }
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            lock (_sync)
            {
                _lastMacroStatus = "Cancelled";
            }

            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _lastMacroStatus = "Failed";
                _lastError = exception.Message;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _macroSource = null;
                _activeMacro = null;
            }

            EndAutomation(isNavigation: false);
            NotifyChanged();
        }
    }

    public void CancelMacro()
    {
        lock (_sync)
        {
            _macroSource?.Cancel();
        }
    }

    public void ClearMessages()
    {
        lock (_sync)
        {
            _messages.Clear();
        }

        NotifyChanged();
    }

    private async Task RunNavigationAsync(
        string operationName,
        Func<MenuNavigator, CancellationToken, Task> execute,
        bool clearPlanOnSuccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        BeginAutomation(isNavigation: true);

        MenuNavigator navigator;
        try
        {
            lock (_sync)
            {
                navigator = _menuNavigator
                    ?? throw new InvalidOperationException(
                        _navigationError ?? "No valid menu definition is loaded.");
            }
        }
        catch
        {
            EndAutomation(isNavigation: true);
            throw;
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _navigationSource = linkedSource;
            _navigationStatus = operationName;
            _navigationError = null;
            _navigationProgress = null;
        }

        NotifyChanged();
        try
        {
            await execute(navigator, linkedSource.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _navigationStatus = $"Completed · {operationName}";
                if (clearPlanOnSuccess)
                {
                    _navigationPlan = null;
                }
            }
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            lock (_sync)
            {
                _navigationStatus = $"Cancelled · {operationName}";
                _navigationPlan = null;
            }

            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _navigationStatus = $"Failed · {operationName}";
                _navigationError = exception.Message;
                _navigationPlan = null;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _navigationSource = null;
            }

            EndAutomation(isNavigation: true);
            NotifyChanged();
        }
    }

    private static string NormalizeContextValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "any" : value.Trim();

    private static IReadOnlyList<MenuAuthoringReplayStepSummary> GetReplaySteps(
        IReadOnlyList<MenuOperation> operations,
        MenuTimingProfile timing)
    {
        var result = new List<MenuAuthoringReplayStepSummary>();
        foreach (var operation in operations)
        {
            var systemDelay = timing.GetDelay(operation.Key);
            var effectiveDelay = operation.DelayAfter ?? systemDelay;
            for (var repeat = 0; repeat < operation.Repeat; repeat++)
            {
                result.Add(new MenuAuthoringReplayStepSummary(
                    result.Count + 1,
                    operation.Key,
                    operation.Action,
                    checked((int)effectiveDelay.TotalMilliseconds),
                    checked((int)systemDelay.TotalMilliseconds),
                    operation.DelayAfter is not null));
            }
        }

        return result;
    }

    private static IReadOnlyList<MenuOperation> GetDraftOperations(
        MenuDefinition definition,
        MenuAuthoringItemKind kind,
        string itemId)
    {
        if (kind == MenuAuthoringItemKind.Anchor)
        {
            var anchor = definition.GetRequiredAnchor(itemId);
            if (anchor.Verified)
            {
                throw new InvalidOperationException("Verified anchors cannot be changed in the timing lab.");
            }

            return anchor.Operations;
        }

        var transition = definition.Transitions.TryGetValue(itemId, out var existing)
            ? existing
            : throw new KeyNotFoundException($"Menu transition '{itemId}' was not found.");
        if (transition.Verified)
        {
            throw new InvalidOperationException("Verified transitions cannot be changed in the timing lab.");
        }

        return transition.Operations;
    }

    private static IReadOnlyList<MenuOperation> ExpandOperations(
        IReadOnlyList<MenuOperation> operations) =>
        operations
            .SelectMany(operation => Enumerable.Range(0, operation.Repeat)
                .Select(_ => operation with { Repeat = 1 }))
            .ToArray();

    private static IReadOnlyList<MenuOperation> CoalesceOperations(
        IReadOnlyList<MenuOperation> operations)
    {
        var result = new List<MenuOperation>();
        foreach (var operation in operations)
        {
            if (result.Count > 0)
            {
                var previous = result[^1];
                if (previous.Key.Equals(operation.Key, StringComparison.OrdinalIgnoreCase)
                    && previous.Action == operation.Action
                    && previous.DelayAfter == operation.DelayAfter
                    && previous.Repeat < MenuDefinitionValidator.MaximumRepeat)
                {
                    result[^1] = previous with { Repeat = previous.Repeat + 1 };
                    continue;
                }
            }

            result.Add(operation);
        }

        return result;
    }

    private static MenuDefinition CopyMenuDefinition(
        MenuDefinition definition,
        IEnumerable<MenuTransition>? transitions = null,
        IEnumerable<MenuAnchor>? anchors = null,
        MenuTimingProfile? timing = null) =>
        new(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            transitions ?? definition.Transitions.Values,
            anchors ?? definition.Anchors.Values,
            timing ?? definition.Timing);

    private static MenuRecordingRequest NormalizeRecordingRequest(MenuRecordingRequest request) =>
        request with
        {
            ItemId = request.ItemId.Trim(),
            Label = request.Label.Trim(),
            SourceNodeId = string.IsNullOrWhiteSpace(request.SourceNodeId)
                ? null
                : request.SourceNodeId.Trim(),
            TargetNodeId = request.TargetNodeId.Trim(),
            NewTargetLabel = string.IsNullOrWhiteSpace(request.NewTargetLabel)
                ? null
                : request.NewTargetLabel.Trim(),
            NewTargetParentId = string.IsNullOrWhiteSpace(request.NewTargetParentId)
                ? null
                : request.NewTargetParentId.Trim()
        };

    private static void ValidateRecordingRequest(
        MenuDefinition definition,
        MenuRecordingRequest request)
    {
        if (definition.Anchors.TryGetValue(request.ItemId, out var existingAnchor)
            && (request.Kind != MenuAuthoringItemKind.Anchor || existingAnchor.Verified))
        {
            throw new InvalidOperationException(
                $"A verified anchor or different item named '{request.ItemId}' already exists.");
        }

        if (definition.Transitions.TryGetValue(request.ItemId, out var existingTransition)
            && (request.Kind != MenuAuthoringItemKind.Transition || existingTransition.Verified))
        {
            throw new InvalidOperationException(
                $"A verified transition or different item named '{request.ItemId}' already exists.");
        }

        var nodes = definition.Nodes.Values.ToList();
        if (!definition.Nodes.ContainsKey(request.TargetNodeId))
        {
            if (string.IsNullOrWhiteSpace(request.NewTargetLabel))
            {
                throw new InvalidOperationException(
                    $"Target node '{request.TargetNodeId}' does not exist. Provide a label to create it.");
            }

            nodes.Add(new MenuNode(
                request.TargetNodeId,
                request.NewTargetLabel,
                request.NewTargetParentId));
        }

        var placeholder = new MenuOperation("KEY_RETURN");
        var anchors = definition.Anchors.Values
            .Where(anchor => request.Kind != MenuAuthoringItemKind.Anchor
                             || !anchor.Id.Equals(request.ItemId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var transitions = definition.Transitions.Values
            .Where(transition => request.Kind != MenuAuthoringItemKind.Transition
                                 || !transition.Id.Equals(request.ItemId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (request.Kind == MenuAuthoringItemKind.Anchor)
        {
            anchors.Add(new MenuAnchor(
                request.ItemId,
                request.Label,
                request.TargetNodeId,
                [placeholder]));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.SourceNodeId))
            {
                throw new InvalidOperationException("Choose a source node for a transition recording.");
            }

            transitions.Add(new MenuTransition(
                request.ItemId,
                request.SourceNodeId,
                request.TargetNodeId,
                [placeholder]));
        }

        var candidate = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            nodes,
            transitions,
            anchors,
            definition.Timing);
        new MenuDefinitionValidator().ValidateAndThrow(candidate);
    }

    private static MenuDefinition AddDraftRecording(
        MenuDefinition definition,
        MenuRecordingRequest request,
        IReadOnlyList<MenuOperation> operations)
    {
        ValidateRecordingRequest(definition, request);
        var nodes = definition.Nodes.Values.ToList();
        if (!definition.Nodes.ContainsKey(request.TargetNodeId))
        {
            nodes.Add(new MenuNode(
                request.TargetNodeId,
                request.NewTargetLabel!,
                request.NewTargetParentId,
                "Created by the web menu-authoring recorder."));
        }

        var anchors = definition.Anchors.Values
            .Where(anchor => request.Kind != MenuAuthoringItemKind.Anchor
                             || !anchor.Id.Equals(request.ItemId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var transitions = definition.Transitions.Values
            .Where(transition => request.Kind != MenuAuthoringItemKind.Transition
                                 || !transition.Id.Equals(request.ItemId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var description = "Recorded and saved by the web menu-authoring studio; requires three visual validation passes.";
        if (request.Kind == MenuAuthoringItemKind.Anchor)
        {
            anchors.Add(new MenuAnchor(
                request.ItemId,
                request.Label,
                request.TargetNodeId,
                operations,
                false,
                description));
        }
        else
        {
            transitions.Add(new MenuTransition(
                request.ItemId,
                request.SourceNodeId!,
                request.TargetNodeId,
                operations,
                false,
                description));
        }

        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            nodes,
            transitions,
            anchors,
            definition.Timing);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        return updated;
    }

    private async Task PersistActiveMenuDefinitionAsync(
        MenuDefinition definition,
        CancellationToken cancellationToken)
    {
        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string currentPath;
            lock (_sync)
            {
                currentPath = GetMenuDefinitionPath(_settings);
            }

            var path = currentPath.Equals(
                Path.GetFullPath(_defaultMenuDefinitionPath),
                StringComparison.Ordinal)
                ? GetAvailableUserDefinitionPath(definition.Id)
                : currentPath;
            await new MenuDefinitionWriter()
                .WriteFileAsync(path, definition, cancellationToken)
                .ConfigureAwait(false);
            if (!path.Equals(currentPath, StringComparison.Ordinal))
            {
                await UpdateSettingsAsync(
                        current => current with { MenuDefinitionPath = path },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            InstallMenuDefinition(definition);
        }
        finally
        {
            _menuDefinitionGate.Release();
        }
    }

    private string GetAvailableUserDefinitionPath(string definitionId)
    {
        var directory = Path.Combine(_configurationDirectory, "menu-definitions");
        var preferred = Path.Combine(directory, $"{definitionId}.yaml");
        return !File.Exists(preferred)
            ? preferred
            : Path.Combine(
                directory,
                $"{definitionId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.yaml");
    }

    private static string GetAuthoringTargetPath(
        MenuDefinition definition,
        MenuAuthoringItemKind kind,
        string itemId) =>
        kind == MenuAuthoringItemKind.Anchor
            ? definition.GetPath(definition.GetRequiredAnchor(itemId).TargetNodeId)
            : definition.GetPath(definition.Transitions.TryGetValue(itemId, out var transition)
                ? transition.ToNodeId
                : throw new KeyNotFoundException($"Menu transition '{itemId}' was not found."));

    private static MenuDefinition SetAuthoringItemVerified(
        MenuDefinition definition,
        MenuAuthoringItemKind kind,
        string itemId)
    {
        var anchors = definition.Anchors.Values
            .Select(anchor => kind == MenuAuthoringItemKind.Anchor
                              && anchor.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)
                ? anchor with { Verified = true }
                : anchor)
            .ToArray();
        var transitions = definition.Transitions.Values
            .Select(transition => kind == MenuAuthoringItemKind.Transition
                                  && transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)
                ? transition with { Verified = true }
                : transition)
            .ToArray();
        var found = kind == MenuAuthoringItemKind.Anchor
            ? anchors.Any(anchor => anchor.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            : transitions.Any(transition => transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
        if (!found)
        {
            throw new KeyNotFoundException($"Menu {kind.ToString().ToLowerInvariant()} '{itemId}' was not found.");
        }

        return new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            transitions,
            anchors,
            definition.Timing);
    }

    private async Task ExecuteMenuAuthoringValidationAsync(
        MenuDefinition definition,
        MenuValidationSession session,
        CancellationToken cancellationToken)
    {
        BeginAutomation(isNavigation: true);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _navigationSource = linkedSource;
            _navigationStatus = $"Authoring validation · {session.ItemId}";
            _navigationError = null;
            _navigationProgress = null;
            _menuAuthoringStatus = "Sending the recorded commands only";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        try
        {
            MenuStateTracker tracker;
            lock (_sync)
            {
                tracker = _menuStateTracker
                    ?? throw new InvalidOperationException("No menu state tracker is available.");
            }

            if (session.Kind == MenuAuthoringItemKind.Anchor)
            {
                var anchor = definition.GetRequiredAnchor(session.ItemId);
                if (anchor.Verified)
                {
                    throw new InvalidOperationException($"Anchor '{anchor.Label}' is already verified.");
                }

                await ExecuteAuthoringOperationsAsync(
                        $"Validate anchor · {anchor.Label}",
                        tracker.Current.Path ?? "Unknown",
                        definition.GetPath(anchor.TargetNodeId),
                        anchor.Operations,
                        definition.Timing,
                        linkedSource.Token)
                    .ConfigureAwait(false);
                tracker.ApplyAnchor(anchor);
            }
            else
            {
                var transition = definition.Transitions.TryGetValue(session.ItemId, out var candidate)
                    ? candidate
                    : throw new KeyNotFoundException($"Menu transition '{session.ItemId}' was not found.");
                if (transition.Verified)
                {
                    throw new InvalidOperationException($"Transition '{transition.Id}' is already verified.");
                }

                await ExecuteAuthoringOperationsAsync(
                        $"Validate transition · {transition.Id}",
                        definition.GetPath(transition.FromNodeId),
                        definition.GetPath(transition.ToNodeId),
                        transition.Operations,
                        definition.Timing,
                        linkedSource.Token)
                    .ConfigureAwait(false);
                tracker.ApplyTransition(transition);
            }

            lock (_sync)
            {
                _menuValidation = session with { AwaitingConfirmation = true };
                _navigationStatus = $"Awaiting visual confirmation · {session.ExpectedTargetPath}";
                _menuAuthoringStatus = $"Did the TV reach {session.ExpectedTargetPath}?";
            }
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            _menuStateTracker?.MarkUnknown("Menu authoring validation was cancelled.");
            lock (_sync)
            {
                _menuValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "Validation cancelled";
            }

            throw;
        }
        catch (Exception exception)
        {
            _menuStateTracker?.MarkUnknown("Menu authoring validation did not complete.");
            lock (_sync)
            {
                _menuValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "Validation failed to run";
                _menuAuthoringError = exception.Message;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _navigationSource = null;
            }

            EndAutomation(isNavigation: true);
            NotifyChanged();
        }
    }

    private async Task ExecuteMenuTimingProfileTestAsync(
        MenuDefinition definition,
        MenuTimingValidationSession session,
        CancellationToken cancellationToken)
    {
        BeginAutomation(isNavigation: true);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _navigationSource = linkedSource;
            _navigationStatus = $"System timing test · {session.TransitionId}";
            _navigationError = null;
            _navigationProgress = null;
            _menuAuthoringStatus = "Sending the selected traversal only";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        try
        {
            MenuStateTracker tracker;
            lock (_sync)
            {
                tracker = _menuStateTracker
                    ?? throw new InvalidOperationException("No menu state tracker is available.");
            }

            var transition = definition.Transitions.TryGetValue(session.TransitionId, out var candidate)
                ? candidate
                : throw new KeyNotFoundException(
                    $"Menu transition '{session.TransitionId}' was not found.");
            var systemTimedOperations = transition.Operations
                .Select(operation => operation with { DelayAfter = null })
                .ToArray();
            await ExecuteAuthoringOperationsAsync(
                    $"Test system timing · {transition.Id}",
                    definition.GetPath(transition.FromNodeId),
                    definition.GetPath(transition.ToNodeId),
                    systemTimedOperations,
                    session.Timing,
                    linkedSource.Token)
                .ConfigureAwait(false);
            tracker.ApplyTransition(transition);

            lock (_sync)
            {
                _menuTimingValidation = session with { AwaitingConfirmation = true };
                _navigationStatus =
                    $"Awaiting system timing confirmation · {session.ExpectedTargetPath}";
                _menuAuthoringStatus =
                    $"Did the system timing test reach {session.ExpectedTargetPath}?";
            }
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            _menuStateTracker?.MarkUnknown("System timing validation was cancelled.");
            lock (_sync)
            {
                _menuTimingValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "System timing test cancelled";
            }

            throw;
        }
        catch (Exception exception)
        {
            _menuStateTracker?.MarkUnknown("System timing validation did not complete.");
            lock (_sync)
            {
                _menuTimingValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "System timing test failed to run";
                _menuAuthoringError = exception.Message;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _navigationSource = null;
            }

            EndAutomation(isNavigation: true);
            NotifyChanged();
        }
    }

    private async Task ExecuteMenuReturnStrategyTestAsync(
        MenuDefinition definition,
        ReturnStrategyContext context,
        MenuReturnScript script,
        MenuReturnValidationSession session,
        CancellationToken cancellationToken)
    {
        BeginAutomation(isNavigation: true);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _navigationSource = linkedSource;
            _navigationStatus = $"Return script test · {session.Kind}";
            _navigationError = null;
            _navigationProgress = null;
            _menuAuthoringStatus =
                $"Sending the return script only · expected start: {definition.GetPath(session.StartNodeId)}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        try
        {
            MenuStateTracker tracker;
            lock (_sync)
            {
                tracker = _menuStateTracker
                    ?? throw new InvalidOperationException("No menu state tracker is available.");
            }

            await ExecuteAuthoringOperationsAsync(
                    $"Test return script · {session.Kind}",
                    definition.GetPath(session.StartNodeId),
                    definition.GetPath(context.Anchor.TargetNodeId),
                    script.Operations,
                    definition.Timing,
                    linkedSource.Token)
                .ConfigureAwait(false);
            tracker.ApplyAnchor(context.Anchor);

            lock (_sync)
            {
                _menuReturnValidation = session with { AwaitingConfirmation = true };
                _navigationStatus =
                    $"Awaiting return-script confirmation · {session.ExpectedTargetPath}";
                _menuAuthoringStatus = $"Did the TV return to {session.ExpectedTargetPath}?";
            }
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            _menuStateTracker?.MarkUnknown("Return-script validation was cancelled.");
            lock (_sync)
            {
                _menuReturnValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "Return script test cancelled";
            }

            throw;
        }
        catch (Exception exception)
        {
            _menuStateTracker?.MarkUnknown("Return-script validation did not complete.");
            lock (_sync)
            {
                _menuReturnValidation = session with { AwaitingConfirmation = false };
                _menuAuthoringStatus = "Return script test failed to run";
                _menuAuthoringError = exception.Message;
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _navigationSource = null;
            }

            EndAutomation(isNavigation: true);
            NotifyChanged();
        }
    }

    private static MenuReturnStrategySummary? GetReturnStrategySummary(
        MenuDefinition definition,
        MenuReturnValidationSession? validation)
    {
        var context = TryGetReturnStrategyContext(definition);
        if (context is null)
        {
            return null;
        }

        return new MenuReturnStrategySummary(
            context.Anchor.Id,
            context.Anchor.Label,
            context.Strategy.MenuRootNodeId,
            definition.GetPath(context.Strategy.MenuRootNodeId),
            FormatKeyScript(context.Anchor.Operations),
            new MenuReturnScriptSummary(
                MenuReturnScriptKind.AtMenuRoot,
                "At Settings",
                FormatKeyScript(context.Strategy.AtMenuRoot.Operations),
                context.Strategy.AtMenuRoot.Verified),
            new MenuReturnScriptSummary(
                MenuReturnScriptKind.BelowMenuRoot,
                "Deeper menu",
                FormatKeyScript(context.Strategy.BelowMenuRoot.Operations),
                context.Strategy.BelowMenuRoot.Verified),
            GetDeepReturnTestNodes(definition, context),
            validation?.Kind,
            validation?.Passes ?? 0,
            MenuReturnValidationSession.RequiredPasses,
            validation?.AwaitingConfirmation ?? false,
            validation?.ExpectedTargetPath);
    }

    private static ReturnStrategyContext GetRequiredReturnStrategyContext(
        MenuDefinition definition) =>
        TryGetReturnStrategyContext(definition)
        ?? throw new InvalidOperationException(
            "No return-to-normal-video anchor and Settings route could be identified in this menu definition.");

    private static ReturnStrategyContext? TryGetReturnStrategyContext(
        MenuDefinition definition)
    {
        var anchor = definition.Anchors.TryGetValue("normal-video", out var namedAnchor)
            ? namedAnchor
            : definition.Anchors.Values.FirstOrDefault(candidate =>
                candidate.TargetNodeId.Equals("normal-video", StringComparison.OrdinalIgnoreCase));
        if (anchor is null)
        {
            return null;
        }

        if (anchor.ReturnStrategy is { } existing)
        {
            return new ReturnStrategyContext(anchor, existing);
        }

        var returnTransition = definition.Transitions.Values
            .Where(transition =>
                transition.ToNodeId.Equals(anchor.TargetNodeId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(transition => transition.Verified)
            .ThenBy(transition => definition.GetDepth(transition.FromNodeId))
            .FirstOrDefault(transition => definition.Transitions.Values.Any(open =>
                open.FromNodeId.Equals(anchor.TargetNodeId, StringComparison.OrdinalIgnoreCase)
                && open.ToNodeId.Equals(transition.FromNodeId, StringComparison.OrdinalIgnoreCase)));
        if (returnTransition is null)
        {
            return null;
        }

        return new ReturnStrategyContext(
            anchor,
            new MenuReturnStrategy(
                returnTransition.FromNodeId,
                new MenuReturnScript(returnTransition.Operations, returnTransition.Verified),
                new MenuReturnScript(
                    [new MenuOperation("KEY_MENU"), new MenuOperation("KEY_RETURN")])));
    }

    private static IReadOnlyList<MenuReturnTestNodeSummary> GetDeepReturnTestNodes(
        MenuDefinition definition,
        ReturnStrategyContext context) =>
        definition.Nodes.Values
            .Where(node => definition.IsDescendantOf(
                node.Id,
                context.Strategy.MenuRootNodeId))
            .OrderByDescending(node => definition.GetDepth(node.Id))
            .ThenBy(node => definition.GetPath(node.Id), StringComparer.OrdinalIgnoreCase)
            .Select(node => new MenuReturnTestNodeSummary(node.Id, definition.GetPath(node.Id)))
            .ToArray();

    private static MenuReturnScript UpdateReturnScript(
        MenuReturnScript current,
        IReadOnlyList<string> keys)
    {
        var normalizedKeys = NormalizeReturnScriptKeys(keys);
        var currentKeys = ExpandOperations(current.Operations)
            .Select(operation => operation.Key)
            .ToArray();
        if (currentKeys.SequenceEqual(normalizedKeys, StringComparer.OrdinalIgnoreCase))
        {
            return current;
        }

        var operations = CoalesceOperations(normalizedKeys
            .Select(key => new MenuOperation(key))
            .ToArray());
        return new MenuReturnScript(operations, Verified: false);
    }

    private static string[] NormalizeReturnScriptKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count is < 1 or > MenuDefinitionValidator.MaximumRepeat)
        {
            throw new InvalidOperationException(
                $"A return script must contain between 1 and {MenuDefinitionValidator.MaximumRepeat} button presses.");
        }

        var normalized = new string[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(keys[index]))
            {
                throw new InvalidOperationException(
                    $"Return script button {index + 1} cannot be empty.");
            }

            normalized[index] = keys[index].Trim().ToUpperInvariant();
        }

        return normalized;
    }

    private static string NormalizeDeepReturnTestNode(
        MenuDefinition definition,
        ReturnStrategyContext context,
        string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidOperationException(
                "Choose a deeper menu position for this return-script test.");
        }

        var normalized = nodeId.Trim();
        definition.GetRequiredNode(normalized);
        if (!definition.IsDescendantOf(normalized, context.Strategy.MenuRootNodeId))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(normalized)}' is not below '{definition.GetPath(context.Strategy.MenuRootNodeId)}'.");
        }

        return normalized;
    }

    private static MenuDefinition SetReturnScriptVerified(
        MenuDefinition definition,
        ReturnStrategyContext context,
        MenuReturnScriptKind kind,
        bool verified)
    {
        var strategy = context.Strategy;
        strategy = kind == MenuReturnScriptKind.AtMenuRoot
            ? strategy with { AtMenuRoot = strategy.AtMenuRoot with { Verified = verified } }
            : strategy with { BelowMenuRoot = strategy.BelowMenuRoot with { Verified = verified } };
        var anchors = definition.Anchors.Values
            .Select(anchor => anchor.Id.Equals(context.Anchor.Id, StringComparison.OrdinalIgnoreCase)
                ? anchor with { ReturnStrategy = strategy }
                : anchor)
            .ToArray();
        return CopyMenuDefinition(definition, anchors: anchors);
    }

    private static string FormatKeyScript(IReadOnlyList<MenuOperation> operations) =>
        string.Join(", ", ExpandOperations(operations).Select(operation => operation.Key));

    private static string GetOperationSignature(IReadOnlyList<MenuOperation> operations) =>
        string.Join(
            "|",
            operations.Select(operation =>
                $"{operation.Key.ToUpperInvariant()}:{operation.Action}:{operation.Repeat}:{operation.DelayAfter?.Ticks}"));

    private static IReadOnlyList<MenuTimingTestRouteSummary> GetTimingTestRoutes(
        MenuDefinition definition) =>
        definition.Transitions.Values
            .Where(transition => transition.Operations.Count > 0)
            .OrderBy(transition => definition.GetPath(transition.FromNodeId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(transition => definition.GetPath(transition.ToNodeId), StringComparer.OrdinalIgnoreCase)
            .Select(transition => new MenuTimingTestRouteSummary(
                transition.Id,
                definition.GetPath(transition.FromNodeId),
                definition.GetPath(transition.ToNodeId),
                transition.Operations.Sum(operation => operation.Repeat),
                transition.Operations.Any(operation => operation.DelayAfter is not null)))
            .ToArray();

    private static ValidationSetup FindValidationSetup(
        MenuDefinition definition,
        string sourceNodeId)
    {
        ValidationSetup? best = null;
        foreach (var anchor in definition.Anchors.Values.Where(anchor => anchor.Verified))
        {
            try
            {
                var plan = new NavigationPlanner().Plan(
                    definition,
                    anchor.TargetNodeId,
                    sourceNodeId);
                var candidate = new ValidationSetup(anchor, plan);
                if (best is null
                    || GetSetupCommandCount(candidate) < GetSetupCommandCount(best))
                {
                    best = candidate;
                }
            }
            catch (NavigationPlanningException)
            {
                // Try another verified anchor.
            }
        }

        return best ?? throw new InvalidOperationException(
            $"No verified anchor and route can prepare '{definition.GetPath(sourceNodeId)}'. Validate an anchor and each preceding transition first.");
    }

    private static int GetSetupCommandCount(ValidationSetup setup) =>
        setup.Anchor.Operations.Sum(operation => operation.Repeat) + setup.Plan.CommandCount;

    private async Task ExecuteAuthoringOperationsAsync(
        string phase,
        string sourcePath,
        string targetPath,
        IReadOnlyList<MenuOperation> operations,
        MenuTimingProfile timing,
        CancellationToken cancellationToken)
    {
        var commandCount = operations.Sum(operation => operation.Repeat);
        var commandNumber = 0;
        foreach (var operation in operations)
        {
            for (var repeat = 0; repeat < operation.Repeat; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                commandNumber++;
                lock (_sync)
                {
                    _navigationProgress = new NavigationProgress(
                        DateTimeOffset.UtcNow,
                        phase,
                        sourcePath,
                        targetPath,
                        commandNumber,
                        commandCount,
                        operation.Key,
                        operation.Action);
                    _menuAuthoringStatus = $"Replaying {commandNumber}/{commandCount} · {operation.Key}";
                }

                NotifyChanged();
                await _client.SendKeyAsync(operation.Key, operation.Action, cancellationToken)
                    .ConfigureAwait(false);
                var delay = operation.DelayAfter ?? timing.GetDelay(operation.Key);
                await _menuDelay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendTrackedKeyAsync(
        string key,
        RemoteKeyAction action,
        CancellationToken cancellationToken)
    {
        var normalizedKey = key.Trim();
        await _client.SendKeyAsync(normalizedKey, action, cancellationToken).ConfigureAwait(false);
        MenuStateTracker? tracker;
        lock (_sync)
        {
            _navigationPlan = null;
            tracker = _menuStateTracker;
        }

        tracker?.ObserveCommand(normalizedKey, action);
    }

    private void EnsureNoAutomationRunning(string operation)
    {
        if (Volatile.Read(ref _navigationRunning) == 1)
        {
            throw new InvalidOperationException(
                $"Cancel the active menu navigation before attempting to {operation}.");
        }

        if (Volatile.Read(ref _macroRunning) == 1)
        {
            throw new InvalidOperationException(
                $"Cancel the active macro before attempting to {operation}.");
        }
    }

    private void EnsureNoMenuRecording(string operation)
    {
        lock (_sync)
        {
            if (_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException(
                    $"Stop and save the active menu recording before attempting to {operation}.");
            }
        }
    }

    private void BeginAutomation(bool isNavigation)
    {
        lock (_sync)
        {
            if (_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException(
                    "Stop and save the active menu recording before starting automated commands.");
            }

            if (_navigationRunning == 1)
            {
                throw new InvalidOperationException(
                    "Cancel the active menu navigation before starting another automated operation.");
            }

            if (_macroRunning == 1)
            {
                throw new InvalidOperationException(
                    "Cancel the active macro before starting another automated operation.");
            }

            if (isNavigation)
            {
                _navigationRunning = 1;
            }
            else
            {
                _macroRunning = 1;
            }
        }
    }

    private void EndAutomation(bool isNavigation)
    {
        lock (_sync)
        {
            if (isNavigation)
            {
                _navigationRunning = 0;
            }
            else
            {
                _macroRunning = 0;
            }
        }
    }

    private void InstallMenuDefinition(MenuDefinition definition)
    {
        var tracker = new MenuStateTracker(definition);
        var navigator = new MenuNavigator(
            definition,
            tracker,
            new WebMenuCommandTarget(_client));
        tracker.Changed += HandleMenuStateChanged;
        navigator.ProgressChanged += HandleNavigationProgress;

        MenuStateTracker? previousTracker;
        MenuNavigator? previousNavigator;
        lock (_sync)
        {
            previousTracker = _menuStateTracker;
            previousNavigator = _menuNavigator;
            _menuDefinition = definition;
            _menuStateTracker = tracker;
            _menuNavigator = navigator;
            _navigationPlan = null;
            _navigationProgress = null;
            _navigationStatus = "Menu definition loaded";
            _navigationError = null;
            _menuValidation = null;
            _menuTimingValidation = null;
            _menuReturnValidation = null;
            _menuAuthoringError = null;
        }

        if (previousTracker is not null)
        {
            previousTracker.Changed -= HandleMenuStateChanged;
        }

        if (previousNavigator is not null)
        {
            previousNavigator.ProgressChanged -= HandleNavigationProgress;
        }

        NotifyChanged();
    }

    private SamsungWebSettings GetSettings()
    {
        lock (_sync)
        {
            return _settings;
        }
    }

    private string GetMenuDefinitionPath(SamsungWebSettings settings) =>
        Path.GetFullPath(settings.MenuDefinitionPath ?? _defaultMenuDefinitionPath);

    private async Task<MacroCatalog> LoadValidatedCatalogAsync(
        CancellationToken cancellationToken)
    {
        var path = GetSnapshot().MacroFilePath;
        return await LoadValidatedCatalogAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddOrUpdateQuickAccessAsync(
        QuickAccessAction action,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var current = GetQuickAccessActions().ToList();
        var existingIndex = current.FindIndex(candidate =>
            candidate.Id.Equals(action.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            current[existingIndex] = action;
        }
        else
        {
            if (current.Count >= QuickAccessCapacity)
            {
                throw new InvalidOperationException(
                    $"Quick access supports at most {QuickAccessCapacity} actions. Remove one before adding another.");
            }

            current.Add(action);
        }

        await UpdateSettingsAsync(
                settings => settings with { QuickAccess = current },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
    }

    private static IReadOnlyList<QuickAccessAction> NormalizeQuickAccess(
        IReadOnlyList<QuickAccessAction>? actions)
    {
        if (actions is null)
        {
            return [DefaultReturnToVideoAction];
        }

        var normalized = new List<QuickAccessAction>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (action is null
                || !Enum.IsDefined(action.Kind)
                || string.IsNullOrWhiteSpace(action.Target))
            {
                continue;
            }

            if (action.Kind == QuickAccessActionKind.RemoteKey
                && !Enum.IsDefined(action.Action))
            {
                continue;
            }

            var target = action.Target.Trim();
            var remoteAction = action.Kind == QuickAccessActionKind.RemoteKey
                ? action.Action
                : RemoteKeyAction.Click;
            var id = CreateQuickAccessId(action.Kind, target, remoteAction);
            if (!identifiers.Add(id))
            {
                continue;
            }

            normalized.Add(new QuickAccessAction(
                id,
                NormalizeQuickAccessLabel(action.Label, target),
                action.Kind,
                target,
                remoteAction));
            if (normalized.Count == QuickAccessCapacity)
            {
                break;
            }
        }

        return normalized;
    }

    private static string CreateQuickAccessId(
        QuickAccessActionKind kind,
        string target,
        RemoteKeyAction action) => kind == QuickAccessActionKind.RemoteKey
        ? $"remotekey:{action}:{target}".ToLowerInvariant()
        : $"{kind}:{target}".ToLowerInvariant();

    private static string NormalizeQuickAccessLabel(string? label, string fallback) =>
        string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();

    private static async Task<MacroCatalog> LoadValidatedCatalogAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var catalog = await new MacroParser()
            .ParseFileAsync(path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        new MacroValidator().ValidateAndThrow(catalog);
        return catalog;
    }

    private static async Task<MenuDefinition> LoadValidatedMenuDefinitionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var definition = await new MenuDefinitionParser()
            .ParseFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        return definition;
    }

    private async Task UpdateSettingsAsync(
        Func<SamsungWebSettings, SamsungWebSettings> update,
        CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SamsungWebSettings updated;
            lock (_sync)
            {
                updated = update(_settings);
            }

            await updated.SaveAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _settings = updated;
            }
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    private void HandleConnectionStateChanged(
        object? sender,
        SamsungConnectionStateChangedEventArgs eventArgs)
    {
        if (eventArgs.Error is not null)
        {
            lock (_sync)
            {
                _lastError = eventArgs.Error.Message;
            }
        }

        if (eventArgs.Current is SamsungConnectionState.Disconnected
            or SamsungConnectionState.Faulted)
        {
            _menuStateTracker?.ReduceConfidence(
                "The TV connection ended; predicted menu state is no longer synchronized.");
        }

        NotifyChanged();
    }

    private SamsungMessage CreateDeviceInfoRequestMessage(Uri endpoint, string label)
    {
        var rawJson = JsonSerializer.Serialize(new
        {
            method = "GET",
            endpoint = endpoint.AbsoluteUri,
            label
        });
        return new SamsungMessage(
            DateTimeOffset.UtcNow,
            SamsungMessageDirection.Tx,
            endpoint.AbsoluteUri,
            $"GET /api/v2/ · {label}",
            JsonNode.Parse(rawJson),
            rawJson,
            null,
            _client.ConnectionGeneration);
    }

    private async Task RecordProtocolMessageAsync(
        SamsungMessage message,
        CancellationToken cancellationToken)
    {
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        HandleMessageObserved(this, new SamsungMessageEventArgs(message));
    }

    private void HandleMessageObserved(object? sender, SamsungMessageEventArgs eventArgs)
    {
        lock (_sync)
        {
            _messages.Add(eventArgs.Message);
            if (_messages.Count > MessageCapacity)
            {
                _messages.RemoveRange(0, _messages.Count - MessageCapacity);
            }
        }

        NotifyChanged();
    }

    private void HandleMacroProgress(object? sender, MacroExecutionProgressEventArgs eventArgs)
    {
        lock (_sync)
        {
            _macroProgress.Add(eventArgs.Progress);
            if (_macroProgress.Count > MacroProgressCapacity)
            {
                _macroProgress.RemoveRange(0, _macroProgress.Count - MacroProgressCapacity);
            }
        }

        NotifyChanged();
    }

    private void HandleMenuStateChanged(object? sender, EventArgs eventArgs)
    {
        NotifyChanged();
    }

    private void HandleNavigationProgress(
        object? sender,
        NavigationProgressEventArgs eventArgs)
    {
        lock (_sync)
        {
            _navigationProgress = eventArgs.Progress;
        }

        NotifyChanged();
    }

    private void SetLastError(string? error)
    {
        lock (_sync)
        {
            _lastError = error;
        }

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        var handlers = Changed?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // One disconnected UI circuit cannot interrupt the controller service.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _macroSource?.Cancel();
            _navigationSource?.Cancel();
        }

        if (_menuStateTracker is not null)
        {
            _menuStateTracker.Changed -= HandleMenuStateChanged;
        }

        if (_menuNavigator is not null)
        {
            _menuNavigator.ProgressChanged -= HandleNavigationProgress;
        }

        _client.ConnectionStateChanged -= HandleConnectionStateChanged;
        _client.MessageObserved -= HandleMessageObserved;
        await _client.DisposeAsync().ConfigureAwait(false);
        await _logger.DisposeAsync().ConfigureAwait(false);
        _initializationGate.Dispose();
        _settingsGate.Dispose();
        _menuDefinitionGate.Dispose();
    }

    private sealed class WebMacroCommandTarget(SamsungControllerService controller) : IMacroCommandTarget
    {
        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default) =>
            controller.SendTrackedKeyAsync(key, action, cancellationToken);
    }

    private sealed class WebMenuCommandTarget(SamsungTvClient client) : IMenuCommandTarget
    {
        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default) =>
            client.SendKeyAsync(key, action, cancellationToken);
    }

    private sealed record ValidationSetup(MenuAnchor Anchor, NavigationPlan Plan);

    private sealed record MenuValidationSession(
        MenuAuthoringItemKind Kind,
        string ItemId,
        int Passes,
        bool AwaitingConfirmation,
        string ExpectedTargetPath)
    {
        public const int RequiredPasses = 3;
    }

    private sealed record MenuTimingValidationSession(
        string TransitionId,
        MenuTimingProfile Timing,
        int Passes,
        bool AwaitingConfirmation,
        string ExpectedTargetPath)
    {
        public const int RequiredPasses = 3;
    }

    private sealed record MenuReturnValidationSession(
        MenuReturnScriptKind Kind,
        string StartNodeId,
        string ScriptSignature,
        int Passes,
        bool AwaitingConfirmation,
        string ExpectedTargetPath)
    {
        public const int RequiredPasses = 3;
    }

    private sealed record ReturnStrategyContext(
        MenuAnchor Anchor,
        MenuReturnStrategy Strategy);
}
