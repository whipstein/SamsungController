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
    public const string ReturnToVideoReplacementAnchorId = "return-to-video-replacement";

    private const int MessageCapacity = 500;
    private const int MacroProgressCapacity = 500;
    private const int DeviceInfoObservationCapacity = 20;
    private const int QuickAccessCapacity = 12;
    private const string TraversalFailureDescription =
        "Traversal reported failed from the verified menu UI; captured keys and waits require timing or definition validation.";
    private static readonly QuickAccessAction DefaultReturnToVideoAction = new(
        "menuanchor:normal-video",
        "Return to video",
        QuickAccessActionKind.MenuAnchor,
        "normal-video");

    private readonly object _sync = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly SemaphoreSlim _menuDefinitionGate = new(1, 1);
    private readonly SemaphoreSlim _macroCatalogGate = new(1, 1);
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
    private readonly Dictionary<string, int> _menuValidationPasses = new(
        StringComparer.OrdinalIgnoreCase);

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
    private string? _macroValidationCandidate;
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
            "menu.example.yaml");
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
                menuDefinition = ActivateMenuConfiguration(
                    menuDefinition,
                    settings.MenuConfigurationId);
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
            var menuLabel = menuState?.NodeId is { } menuNodeId
                && _menuDefinition?.Nodes.TryGetValue(menuNodeId, out var menuNode) == true
                    ? menuNode.Label
                    : null;
            return new ControllerSnapshot(
                _settings.Name,
                _settings.Host,
                _settings.ApplicationName,
                _settings.Secure,
                _settings.Port,
                _settings.AllowUntrustedCertificate,
                _settings.KeepAliveIntervalSeconds,
                _settings.KeepAliveTimeoutSeconds,
                _settings.PostConnectWarmupMilliseconds,
                _settings.ReconnectAfterIdleSeconds,
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
                menuLabel,
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
                        node.ParentId,
                        definition.ApplicableTransitions.Any(transition =>
                            transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        || definition.ApplicableAnchors.Any(anchor =>
                            anchor.Verified
                            && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                        definition.ApplicableTransitions.Any(transition =>
                            !transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        || definition.ApplicableAnchors.Any(anchor =>
                            !anchor.Verified
                            && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                        node.ControlType,
                        node.DefaultValue,
                        node.DisabledWhen ?? [],
                        IsMenuNodeDisabledByDefault(definition, node),
                        node.SelectionOptions ?? [],
                        node.MinimumValue,
                        node.MaximumValue))
                    .ToArray();
            var anchors = definition is null
                ? []
                : definition.ApplicableAnchors.Select(anchor => new MenuAnchorSummary(
                        anchor.Id,
                        anchor.Label,
                        anchor.TargetNodeId,
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
                definition?.Configurations.Values.Select(configuration =>
                        new MenuConfigurationSummary(
                            configuration.Id,
                            configuration.Name,
                            configuration.Conditions,
                            configuration.Id.Equals(
                                definition.ActiveConfigurationId,
                                StringComparison.OrdinalIgnoreCase)))
                    .ToArray() ?? [],
                definition?.ActiveConfigurationId,
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
                : definition.ApplicableAnchors
                    .Where(anchor => !anchor.Verified)
                    .Select(anchor => new MenuAuthoringCandidateSummary(
                        MenuAuthoringItemKind.Anchor,
                        anchor.Id,
                        anchor.Label,
                        anchor.ValidationSourceNodeId,
                        anchor.TargetNodeId,
                        anchor.ValidationSourceNodeId is null
                            ? null
                            : definition.GetPath(anchor.ValidationSourceNodeId),
                        definition.GetPath(anchor.TargetNodeId),
                        GetMenuValidationPasses(MenuAuthoringItemKind.Anchor, anchor.Id),
                        anchor.Operations.Sum(operation => operation.Repeat),
                        GetReplaySteps(anchor.Operations, definition.Timing),
                        0,
                        []))
                    .Concat(definition.ApplicableTransitions
                        .Where(transition => !transition.Verified)
                        .Select(transition => new MenuAuthoringCandidateSummary(
                            MenuAuthoringItemKind.Transition,
                            transition.Id,
                            definition.GetRequiredNode(transition.ToNodeId).Label,
                            transition.FromNodeId,
                            transition.ToNodeId,
                            definition.GetPath(transition.FromNodeId),
                            definition.GetPath(transition.ToNodeId),
                            GetMenuValidationPasses(
                                MenuAuthoringItemKind.Transition,
                                transition.Id),
                            transition.Operations.Sum(operation => operation.Repeat),
                            GetReplaySteps(transition.Operations, definition.Timing),
                            transition.ReturnToVideoOperations?.Sum(operation => operation.Repeat) ?? 0,
                            GetReplaySteps(
                                transition.ReturnToVideoOperations ?? [],
                                definition.Timing))))
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
                request?.RecordReturnToVideo ?? false,
                _menuRecorder.IsRecordingReturnToVideo,
                _menuRecorder.ForwardOperations.Sum(operation => operation.Repeat),
                _menuRecorder.ReturnToVideoOperations.Sum(operation => operation.Repeat),
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
                PairingTimeout = TimeSpan.FromSeconds(90),
                KeepAliveInterval = TimeSpan.FromSeconds(request.KeepAliveIntervalSeconds),
                KeepAliveTimeout = TimeSpan.FromSeconds(request.KeepAliveTimeoutSeconds),
                PostConnectWarmup = TimeSpan.FromMilliseconds(
                    request.PostConnectWarmupMilliseconds),
                ReconnectAfterIdle = TimeSpan.FromSeconds(request.ReconnectAfterIdleSeconds)
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
                        AllowUntrustedCertificate = request.AllowUntrustedCertificate,
                        KeepAliveIntervalSeconds = request.KeepAliveIntervalSeconds,
                        KeepAliveTimeoutSeconds = request.KeepAliveTimeoutSeconds,
                        PostConnectWarmupMilliseconds = request.PostConnectWarmupMilliseconds,
                        ReconnectAfterIdleSeconds = request.ReconnectAfterIdleSeconds
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                _hasToken = _client.Token is not null;
            }

            await SynchronizeMenuAfterConnectAsync(cancellationToken).ConfigureAwait(false);
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
            var configurationId = ResolveMenuConfigurationId(
                definition,
                GetSettings().MenuConfigurationId);
            await UpdateSettingsAsync(
                    current => current with
                    {
                        MenuDefinitionPath = fullPath,
                        MenuConfigurationId = configurationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(definition.WithActiveConfiguration(configurationId));
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

    public MenuDefinitionCreationPreview PreviewMenuDefinitionCreation(
        MenuDefinitionCreationRequest request)
    {
        var (definition, path) = BuildMenuDefinitionCreation(request);
        return new MenuDefinitionCreationPreview(
            definition.Id,
            definition.Name,
            path,
            File.Exists(path));
    }

    public async Task CreateMenuDefinitionAsync(
        MenuDefinitionCreationRequest request,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("create a menu definition");
        EnsureNoMenuRecording("create a menu definition");

        var (definition, path) = BuildMenuDefinitionCreation(request);
        var replacingExisting = File.Exists(path);
        if (replacingExisting && !replaceExisting)
        {
            throw new IOException(
                $"A menu definition already exists at '{path}'. Confirm replacement or choose a different identifier.");
        }

        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new MenuDefinitionWriter()
                .WriteFileAsync(path, definition, cancellationToken)
                .ConfigureAwait(false);
            await UpdateSettingsAsync(
                    current => current with
                    {
                        MenuDefinitionPath = path,
                        MenuConfigurationId = "default"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(definition);
            lock (_sync)
            {
                _menuRecorder.Reset();
                _menuValidation = null;
                _menuAuthoringStatus = replacingExisting
                    ? "Existing TV profile replaced · describe the active menu configuration next"
                    : "TV profile created · describe the active menu configuration next";
                _menuAuthoringError = null;
            }
        }
        finally
        {
            _menuDefinitionGate.Release();
        }

        NotifyChanged();
    }

    private (MenuDefinition Definition, string Path) BuildMenuDefinitionCreation(
        MenuDefinitionCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model.Trim();
        var firmware = NormalizeContextValue(request.Firmware);
        var signal = NormalizeContextValue(request.Signal);
        var pictureMode = NormalizeContextValue(request.PictureMode);
        var input = NormalizeContextValue(request.Input);
        var definitionName = string.IsNullOrWhiteSpace(request.Name)
            ? $"{model} · firmware {firmware}"
            : request.Name.Trim();
        var definitionId = string.IsNullOrWhiteSpace(request.Id)
            ? CreateMenuDefinitionId(model, firmware, signal, pictureMode, input)
            : request.Id.Trim();
        var definition = new MenuDefinition(
            definitionId,
            definitionName,
            model,
            new MenuDefinitionContext(
                firmware,
                signal,
                pictureMode,
                input),
            [
                new MenuNode("tv-interface", "TV interface", Description: "Root for the modeled TV interface."),
                new MenuNode("normal-video", "Normal video", "tv-interface", "No TV menu is expected to be visible.")
            ],
            [],
            [],
            configurations:
            [
                new MenuConfiguration(
                    "default",
                    "Default",
                    "Record the setting values that affect which menu items are visible.")
            ],
            activeConfigurationId: "default");
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        var path = Path.Combine(
            _configurationDirectory,
            "menu-definitions",
            $"{definition.Id}.yaml");
        return (definition, path);
    }

    private static string CreateMenuDefinitionId(
        string model,
        string firmware,
        string signal,
        string pictureMode,
        string input)
    {
        var fileIdParts = new List<string> { model, firmware };
        AddSpecificDefinitionContext(fileIdParts, signal);
        AddSpecificDefinitionContext(fileIdParts, pictureMode);
        AddSpecificDefinitionContext(fileIdParts, input);
        var normalized = new string(string.Join('-', fileIdParts).Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '-')
            .ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "samsung-tv";
        }

        return char.IsLetter(normalized[0]) || normalized[0] == '_'
            ? normalized
            : $"tv-{normalized}";
    }

    private static void AddSpecificDefinitionContext(ICollection<string> parts, string value)
    {
        if (!value.Equals("any", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("unrecorded", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(value);
        }
    }

    public async Task SetMenuConfigurationAsync(
        string configurationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change the active menu configuration");
        EnsureNoMenuRecording("change the active menu configuration");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var normalizedId = configurationId.Trim();
        var configuration = definition.Configurations.TryGetValue(normalizedId, out var candidate)
            ? candidate
            : throw new InvalidOperationException(
                $"Menu configuration '{normalizedId}' does not exist in this TV definition.");
        if (normalizedId.Equals(definition.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await UpdateSettingsAsync(
                current => current with { MenuConfigurationId = configuration.Id },
                cancellationToken)
            .ConfigureAwait(false);
        InstallMenuDefinition(
            definition.WithActiveConfiguration(configuration.Id),
            preserveValidationProgress: true);
        lock (_sync)
        {
            _navigationStatus = $"Menu configuration selected · {configuration.Name} · position reset to unknown";
            _menuAuthoringStatus = $"Active menu configuration · {configuration.Name}";
        }

        NotifyChanged();
    }

    public async Task CreateMenuConfigurationAsync(
        MenuConfigurationEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("add a menu configuration");
        EnsureNoMenuRecording("add a menu configuration");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var configuration = NormalizeMenuConfigurationRequest(request);
        if (definition.Configurations.ContainsKey(configuration.Id))
        {
            throw new InvalidOperationException(
                $"Menu configuration '{configuration.Id}' already exists.");
        }

        var firstConfiguration = definition.Configurations.Count == 0;
        var configurations = definition.Configurations.Values.Append(configuration).ToArray();
        var transitions = firstConfiguration
            ? definition.Transitions.Values.Select(transition => transition with
            {
                ConfigurationId = configuration.Id
            }).ToArray()
            : definition.Transitions.Values.ToArray();
        var anchors = firstConfiguration
            ? definition.Anchors.Values.Select(anchor => anchor with
            {
                ConfigurationId = configuration.Id
            }).ToArray()
            : definition.Anchors.Values.ToArray();
        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            transitions,
            anchors,
            definition.Timing,
            configurations,
            configuration.Id);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await UpdateSettingsAsync(
                current => current with { MenuConfigurationId = configuration.Id },
                cancellationToken)
            .ConfigureAwait(false);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = firstConfiguration
                ? $"Menu configuration created · {configuration.Name} · existing routes were safely scoped to it"
                : $"Menu configuration created and selected · {configuration.Name} · record routes for this layout";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task UpdateMenuConfigurationAsync(
        string configurationId,
        MenuConfigurationEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("edit a menu configuration");
        EnsureNoMenuRecording("edit a menu configuration");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var existing = definition.Configurations.TryGetValue(configurationId.Trim(), out var candidate)
            ? candidate
            : throw new InvalidOperationException(
                $"Menu configuration '{configurationId.Trim()}' does not exist.");
        var configuration = NormalizeMenuConfigurationRequest(request);
        if (!configuration.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A menu configuration's stable ID cannot be changed after creation.");
        }

        configuration = configuration with { Id = existing.Id };
        var configurations = definition.Configurations.Values.Select(item =>
                item.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)
                    ? configuration
                    : item)
            .ToArray();
        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values,
            definition.Transitions.Values,
            definition.Anchors.Values,
            definition.Timing,
            configurations,
            definition.ActiveConfigurationId);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Menu configuration updated · {configuration.Name}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public MenuTopologyOutlinePreview PreviewMenuTopologyOutline(
        MenuTopologyOutlineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        return MenuTopologyOutlinePlanner.Create(definition, request).Preview;
    }

    public async Task ApplyMenuTopologyOutlineAsync(
        MenuTopologyOutlineRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("apply a menu topology outline");
        EnsureNoMenuRecording("apply a menu topology outline");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var plan = MenuTopologyOutlinePlanner.Create(definition, request);
        if (plan.Preview.HasChanges)
        {
            var updated = CopyMenuDefinition(definition, nodes: plan.Nodes);
            new MenuDefinitionValidator().ValidateAndThrow(updated);
            await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _menuAuthoringStatus = plan.Preview.HasChanges
                ? $"Menu topology saved to YAML · {plan.Preview.AddedNodeCount} added · {plan.Preview.UpdatedNodeCount} updated · {plan.Preview.RemovedNodeCount} removed"
                : "Menu topology already matches the outline · no YAML changes were needed";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task CreateMenuNodeAsync(
        MenuNodeEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("add a menu-tree node");
        EnsureNoMenuRecording("add a menu-tree node");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var node = NormalizeMenuNodeRequest(request);
        if (definition.Nodes.ContainsKey(node.Id))
        {
            throw new InvalidOperationException(
                $"A menu-tree node with ID '{node.Id}' already exists. Choose a different stable ID.");
        }

        var nodes = FlattenMenuNodes(definition.Nodes.Values.Append(node).ToArray());
        var updated = CopyMenuDefinition(definition, nodes: nodes);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Menu-tree node added · {updated.GetPath(node.Id)}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task UpdateMenuNodeAsync(
        string nodeId,
        MenuNodeEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("edit a menu-tree node");
        EnsureNoMenuRecording("edit a menu-tree node");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var existing = definition.GetRequiredNode(nodeId.Trim());
        var node = NormalizeMenuNodeRequest(request);
        if (!node.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A node's stable ID cannot be changed after creation. Its display name can be renamed.");
        }

        node = node with { Id = existing.Id };
        var parentChanged = !string.Equals(
            existing.ParentId,
            node.ParentId,
            StringComparison.OrdinalIgnoreCase);
        var nodes = parentChanged
            ? definition.Nodes.Values
                .Where(candidate => !candidate.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))
                .Append(node)
                .ToArray()
            : definition.Nodes.Values
                .Select(candidate => candidate.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)
                    ? node
                    : candidate)
                .ToArray();
        var updated = CopyMenuDefinition(definition, nodes: FlattenMenuNodes(nodes));
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Menu-tree node updated · {updated.GetPath(existing.Id)}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task MoveMenuNodeAsync(
        string nodeId,
        int direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (direction is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                "Menu nodes can only move one position up (-1) or down (1).");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("reorder the menu tree");
        EnsureNoMenuRecording("reorder the menu tree");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var node = definition.GetRequiredNode(nodeId.Trim());
        var nodes = definition.Nodes.Values.ToList();
        var siblings = nodes
            .Where(candidate => HaveSameParent(candidate, node))
            .ToList();
        var siblingIndex = siblings.FindIndex(candidate =>
            candidate.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
        var destinationIndex = siblingIndex + direction;
        if (destinationIndex < 0 || destinationIndex >= siblings.Count)
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(node.Id)}' is already at the {(direction < 0 ? "top" : "bottom")} of this menu level.");
        }

        var destination = siblings[destinationIndex];
        var nodeIndex = nodes.FindIndex(candidate =>
            candidate.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
        var destinationNodeIndex = nodes.FindIndex(candidate =>
            candidate.Id.Equals(destination.Id, StringComparison.OrdinalIgnoreCase));
        (nodes[nodeIndex], nodes[destinationNodeIndex]) =
            (nodes[destinationNodeIndex], nodes[nodeIndex]);

        var orderedNodes = FlattenMenuNodes(nodes);
        var updated = CopyMenuDefinition(definition, nodes: orderedNodes);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus =
                $"Menu-tree order updated · {updated.GetPath(node.Id)} moved {(direction < 0 ? "up" : "down")}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task DeleteMenuNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("delete a menu-tree branch");
        EnsureNoMenuRecording("delete a menu-tree branch");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var normalizedNodeId = nodeId.Trim();
        var node = definition.GetRequiredNode(normalizedNodeId);
        if (string.IsNullOrWhiteSpace(node.ParentId))
        {
            throw new InvalidOperationException("The top-level menu-tree root cannot be deleted.");
        }

        var removedNodeIds = GetMenuSubtreeNodeIds(definition, normalizedNodeId);
        var referencedTransition = definition.Transitions.Values.FirstOrDefault(transition =>
            removedNodeIds.Contains(transition.FromNodeId)
            || removedNodeIds.Contains(transition.ToNodeId));
        var referencedAnchor = definition.Anchors.Values.FirstOrDefault(anchor =>
            removedNodeIds.Contains(anchor.TargetNodeId));
        var referencedReturnStrategy = definition.Anchors.Values.FirstOrDefault(anchor =>
            anchor.ReturnStrategy is { } strategy
            && (removedNodeIds.Contains(strategy.MenuRootNodeId)
                || (strategy.NodeOverrides ?? []).Any(item => removedNodeIds.Contains(item.NodeId))));
        if (referencedTransition is not null || referencedAnchor is not null || referencedReturnStrategy is not null)
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(normalizedNodeId)}' is already used by a recorded traversal, anchor, or return script. Remove that definition first, then delete this branch.");
        }

        var removedPath = definition.GetPath(normalizedNodeId);
        var updated = CopyMenuDefinition(
            definition,
            nodes: FlattenMenuNodes(definition.Nodes.Values
                .Where(candidate => !removedNodeIds.Contains(candidate.Id))
                .ToArray()));
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Menu-tree branch deleted · {removedPath} · {removedNodeIds.Count} node(s)";
            _menuAuthoringError = null;
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

    public void RemoveRecordedMenuCommand(int index)
    {
        lock (_sync)
        {
            _menuRecorder.RemoveCommand(index);
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

    public void BeginReturnToVideoRecording()
    {
        lock (_sync)
        {
            _menuRecorder.BeginReturnToVideo();
            _menuAuthoringStatus =
                "Recording return to normal video · these keys remain part of the same traversal draft";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public void CancelMenuRecording()
    {
        lock (_sync)
        {
            if (!_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException("No menu traversal recording is active.");
            }

            _menuRecorder.Reset();
            _menuAuthoringStatus = "Recording cancelled · no draft was saved";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task StopAndSaveMenuRecordingAsync(
        CancellationToken cancellationToken = default)
    {
        MenuDefinition definition;
        MenuRecordingRequest request;
        MenuOperation[] operations;
        MenuOperation[] returnOperations;
        lock (_sync)
        {
            if (!_menuRecorder.IsRecording)
            {
                throw new InvalidOperationException("No menu traversal recording is active.");
            }

            if (_menuRecorder.ForwardOperations.Count == 0)
            {
                throw new InvalidOperationException("Send at least one successful key before saving the recording.");
            }

            if (_menuRecorder.Request?.RecordReturnToVideo == true
                && (!_menuRecorder.IsRecordingReturnToVideo
                    || _menuRecorder.ReturnToVideoOperations.Count == 0))
            {
                throw new InvalidOperationException(
                    "Record at least one return-to-video key before saving this combined recording.");
            }

            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            request = _menuRecorder.Request!;
            operations = _menuRecorder.ForwardOperations.ToArray();
            returnOperations = _menuRecorder.ReturnToVideoOperations.ToArray();
            _menuRecorder.Stop();
        }

        try
        {
            var updated = AddDraftRecording(
                definition,
                request,
                operations,
                returnOperations);
            await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                SetMenuValidationPasses(request.Kind, request.ItemId, 0);
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
            if (delaysChanged)
            {
                _menuValidationPasses.Clear();
            }

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
            if (delaysChanged)
            {
                _menuValidationPasses.Clear();
            }

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

        var transition = definition.Transitions.TryGetValue(session.TransitionId, out var candidate)
            ? candidate
            : throw new KeyNotFoundException(
                $"Menu transition '{session.TransitionId}' was not found.");

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

            ConfirmSystemTimingTarget(transition);
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

        ConfirmSystemTimingTarget(transition);
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
            belowMenuRoot,
            context.Strategy.NodeOverrides);
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

    public async Task UpdateMenuReturnOverrideAsync(
        string nodeId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(keys);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change a state-specific return-to-video script");
        EnsureNoMenuRecording("change a state-specific return-to-video script");

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
        var normalizedNodeId = nodeId.Trim();
        definition.GetRequiredNode(normalizedNodeId);
        if (normalizedNodeId.Equals(context.Anchor.TargetNodeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A return override cannot start at the normal-video target. Choose a visible menu state.");
        }

        var overrides = (context.Strategy.NodeOverrides ?? []).ToList();
        var existing = overrides.FirstOrDefault(item =>
            item.NodeId.Equals(normalizedNodeId, StringComparison.OrdinalIgnoreCase));
        var script = UpdateReturnScript(
            existing?.Script ?? new MenuReturnScript(context.Anchor.Operations, Verified: false),
            keys);
        var changed = existing is null || script != existing.Script;
        overrides.RemoveAll(item =>
            item.NodeId.Equals(normalizedNodeId, StringComparison.OrdinalIgnoreCase));
        overrides.Add(new MenuReturnOverride(normalizedNodeId, script));

        var strategy = context.Strategy with { NodeOverrides = overrides };
        var anchors = definition.Anchors.Values
            .Select(anchor => anchor.Id.Equals(context.Anchor.Id, StringComparison.OrdinalIgnoreCase)
                ? anchor with { ReturnStrategy = strategy }
                : anchor)
            .ToArray();
        var updated = CopyMenuDefinition(definition, anchors: anchors);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);

        var activeOverrideChanged = changed
            && previousReturnValidation?.Kind == MenuReturnScriptKind.NodeOverride
            && previousReturnValidation.StartNodeId.Equals(
                normalizedNodeId,
                StringComparison.OrdinalIgnoreCase);
        lock (_sync)
        {
            _menuValidation = previousDraftValidation;
            _menuTimingValidation = previousTimingValidation;
            _menuReturnValidation = activeOverrideChanged ? null : previousReturnValidation;
            _menuAuthoringStatus = changed
                ? $"State-specific return script saved · {definition.GetPath(normalizedNodeId)} · requires 3/3 validation"
                : $"State-specific return script unchanged · {definition.GetPath(normalizedNodeId)}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task DeleteMenuReturnOverrideAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("remove a state-specific return-to-video script");
        EnsureNoMenuRecording("remove a state-specific return-to-video script");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var context = GetRequiredReturnStrategyContext(definition);
        var normalizedNodeId = nodeId.Trim();
        var overrides = (context.Strategy.NodeOverrides ?? [])
            .Where(item => !item.NodeId.Equals(normalizedNodeId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (overrides.Length == (context.Strategy.NodeOverrides?.Count ?? 0))
        {
            throw new KeyNotFoundException(
                $"No state-specific return script exists for '{normalizedNodeId}'.");
        }

        var strategy = context.Strategy with { NodeOverrides = overrides };
        var anchors = definition.Anchors.Values
            .Select(anchor => anchor.Id.Equals(context.Anchor.Id, StringComparison.OrdinalIgnoreCase)
                ? anchor with { ReturnStrategy = strategy }
                : anchor)
            .ToArray();
        await PersistActiveMenuDefinitionAsync(
                CopyMenuDefinition(definition, anchors: anchors),
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"State-specific return script removed · {definition.GetPath(normalizedNodeId)} · default behavior restored";
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
        var startNodeId = kind switch
        {
            MenuReturnScriptKind.AtMenuRoot => context.Strategy.MenuRootNodeId,
            MenuReturnScriptKind.BelowMenuRoot =>
                NormalizeDeepReturnTestNode(definition, context, deepStartNodeId),
            MenuReturnScriptKind.NodeOverride => NormalizeReturnOverrideNode(
                definition,
                context,
                deepStartNodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var script = GetReturnScript(context.Strategy, kind, startNodeId);
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
        var currentScript = GetReturnScript(
            context.Strategy,
            session.Kind,
            session.StartNodeId);
        if (GetOperationSignature(currentScript.Operations) != session.ScriptSignature)
        {
            throw new InvalidOperationException(
                "The return script changed after this replay. Run the test again before confirming it.");
        }

        if (!passed)
        {
            var unverified = SetReturnScriptVerified(
                definition,
                context,
                session.Kind,
                session.StartNodeId,
                verified: false);
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

        var verified = SetReturnScriptVerified(
            definition,
            context,
            session.Kind,
            session.StartNodeId,
            verified: true);
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
            if (delaysChanged)
            {
                _menuValidationPasses.Clear();
            }
            else
            {
                SetMenuValidationPasses(kind, itemId, 0);
            }

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
                ? existing with { Passes = GetMenuValidationPasses(kind, itemId) }
                : new MenuValidationSession(
                    kind,
                    itemId.Trim(),
                    GetMenuValidationPasses(kind, itemId),
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

    public async Task PrepareMenuAuthoringValidationSourceAsync(
        MenuAuthoringItemKind kind,
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoMenuRecording("prepare a validation source");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        if (kind != MenuAuthoringItemKind.Transition
            || !definition.Transitions.TryGetValue(itemId, out var transition)
            || transition.ReturnToVideoOperations is not { Count: > 0 } returnOperations)
        {
            var sourceNodeId = kind == MenuAuthoringItemKind.Anchor
                ? definition.GetRequiredAnchor(itemId).ValidationSourceNodeId
                : definition.Transitions.TryGetValue(itemId, out var sourceTransition)
                    ? sourceTransition.FromNodeId
                    : throw new KeyNotFoundException(
                        $"Menu transition '{itemId}' was not found.");
            if (string.IsNullOrWhiteSpace(sourceNodeId))
            {
                throw new InvalidOperationException(
                    "This validation item does not define a source to prepare.");
            }

            await PrepareMenuRecordingSourceAsync(sourceNodeId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var returnAnchor = FindReturnToVideoAnchor(definition)
            ?? throw new InvalidOperationException(
                "No return-to-normal-video anchor is available for this traversal.");
        NavigationPlan? plan = null;
        try
        {
            plan = new NavigationPlanner().Plan(
                definition,
                returnAnchor.TargetNodeId,
                transition.FromNodeId);
        }
        catch (NavigationPlanningException)
        {
            // The recorded return is still sent. The user can finish positioning
            // the TV manually when the forward source has no verified route yet.
        }

        await RunNavigationAsync(
                $"Prepare integrated traversal · {definition.GetPath(transition.FromNodeId)}",
                async (navigator, token) =>
                {
                    MenuStateTracker tracker;
                    lock (_sync)
                    {
                        tracker = _menuStateTracker
                            ?? throw new InvalidOperationException(
                                "No menu state tracker is available.");
                    }

                    try
                    {
                        await ExecuteAuthoringOperationsAsync(
                                $"Recorded return for {definition.GetPath(transition.ToNodeId)}",
                                definition.GetPath(transition.ToNodeId),
                                definition.GetPath(returnAnchor.TargetNodeId),
                                returnOperations,
                                definition.Timing,
                                token)
                            .ConfigureAwait(false);
                        tracker.ApplyAnchor(returnAnchor with
                        {
                            Operations = returnOperations,
                            Verified = false
                        });
                        if (plan is { Transitions.Count: > 0 })
                        {
                            await navigator.ExecutePlanAsync(plan, token)
                                .ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        tracker.MarkUnknown(
                            "The traversal's recorded return preparation did not complete.");
                        throw;
                    }
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = plan is null
                ? $"Recorded return sent · manually position the TV at {definition.GetPath(transition.FromNodeId)} before replaying"
                : $"Source ready using this traversal's recorded return · {definition.GetPath(transition.FromNodeId)}";
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

        var updated = CopyMenuDefinition(
            definition,
            transitions: definition.Transitions.Values.Where(transition =>
                kind != MenuAuthoringItemKind.Transition
                || !transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)),
            anchors: definition.Anchors.Values.Where(anchor =>
                kind != MenuAuthoringItemKind.Anchor
                || !anchor.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase)));
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

    public async Task DeleteVerifiedMenuSettingAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("remove a verified menu setting");
        EnsureNoMenuRecording("remove a verified menu setting");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var normalizedNodeId = nodeId.Trim();
        definition.GetRequiredNode(normalizedNodeId);
        var isVerifiedSetting = definition.Transitions.Values.Any(transition =>
                transition.Verified
                && transition.ToNodeId.Equals(normalizedNodeId, StringComparison.OrdinalIgnoreCase))
            || definition.Anchors.Values.Any(anchor =>
                anchor.Verified
                && anchor.TargetNodeId.Equals(normalizedNodeId, StringComparison.OrdinalIgnoreCase));
        if (!isVerifiedSetting)
        {
            throw new InvalidOperationException(
                $"Menu setting '{definition.GetPath(normalizedNodeId)}' is not verified.");
        }

        var removedNodeIds = GetMenuSubtreeNodeIds(definition, normalizedNodeId);
        var protectedAnchor = definition.Anchors.Values.FirstOrDefault(anchor =>
            anchor.Verified && removedNodeIds.Contains(anchor.TargetNodeId));
        if (protectedAnchor is not null)
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(normalizedNodeId)}' contains the verified known-state anchor " +
                $"'{protectedAnchor.Label}' and cannot be removed.");
        }

        var removedPath = definition.GetPath(normalizedNodeId);
        var removedTransitionCount = definition.Transitions.Values.Count(transition =>
            removedNodeIds.Contains(transition.FromNodeId)
            || removedNodeIds.Contains(transition.ToNodeId));
        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            definition.Nodes.Values.Where(node => !removedNodeIds.Contains(node.Id)),
            definition.Transitions.Values.Where(transition =>
                !removedNodeIds.Contains(transition.FromNodeId)
                && !removedNodeIds.Contains(transition.ToNodeId)),
            definition.Anchors.Values
                .Where(anchor => !removedNodeIds.Contains(anchor.TargetNodeId))
                .Select(anchor => anchor.ReturnStrategy is not { } strategy
                    ? anchor
                    : removedNodeIds.Contains(strategy.MenuRootNodeId)
                        ? anchor with { ReturnStrategy = null }
                        : anchor with
                        {
                            ReturnStrategy = strategy with
                            {
                                NodeOverrides = (strategy.NodeOverrides ?? [])
                                    .Where(item => !removedNodeIds.Contains(item.NodeId))
                                    .ToArray()
                            }
                        }),
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuRecorder.Reset();
            _menuValidation = null;
            _menuTimingValidation = null;
            _menuReturnValidation = null;
            _menuAuthoringStatus =
                $"Verified setting removed · {removedPath} · {removedNodeIds.Count} settings and {removedTransitionCount} routes deleted";
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
                SetMenuValidationPasses(session.Kind, session.ItemId, 0);
                _menuValidation = session with { Passes = 0, AwaitingConfirmation = false };
                _menuAuthoringStatus = "Validation failed · pass count reset; adjust or replace the draft recording";
                _menuAuthoringError = null;
            }

            _menuStateTracker?.MarkUnknown("The user reported that a recorded menu validation did not reach its target.");
            NotifyChanged();
            return;
        }

        var targetNodeId = GetAuthoringTargetNodeId(definition, session.Kind, session.ItemId);
        var passes = session.Passes + 1;
        if (passes < MenuValidationSession.RequiredPasses)
        {
            lock (_sync)
            {
                SetMenuValidationPasses(session.Kind, session.ItemId, passes);
                _menuValidation = session with { Passes = passes, AwaitingConfirmation = false };
                _menuAuthoringStatus = $"Validation passed · {passes}/{MenuValidationSession.RequiredPasses}";
                _menuAuthoringError = null;
            }

            ConfirmMenuAuthoringTarget(targetNodeId, session.ItemId);
            NotifyChanged();
            return;
        }

        var hasIntegratedReturn = session.Kind == MenuAuthoringItemKind.Transition
            && definition.Transitions.TryGetValue(session.ItemId, out var integratedTransition)
            && integratedTransition.ReturnToVideoOperations is { Count: > 0 };
        var verified = SetAuthoringItemVerified(definition, session.Kind, session.ItemId);
        var returnAnchor = FindReturnToVideoAnchor(verified);
        await PersistActiveMenuDefinitionAsync(verified, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuValidation = session with
            {
                Passes = MenuValidationSession.RequiredPasses,
                AwaitingConfirmation = false
            };
            _menuAuthoringStatus = hasIntegratedReturn
                ? $"Verified traversal and its recorded return · passed {MenuValidationSession.RequiredPasses}/{MenuValidationSession.RequiredPasses} together and saved to YAML"
                : $"Verified · {session.ItemId} passed {MenuValidationSession.RequiredPasses}/{MenuValidationSession.RequiredPasses} and YAML was updated";
            _menuAuthoringError = null;
        }

        ConfirmMenuAuthoringTarget(targetNodeId, session.ItemId);
        NotifyChanged();
        if (returnAnchor is not null
            && !targetNodeId.Equals(
                returnAnchor.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            await RunMenuAnchorAsync(returnAnchor.Id, cancellationToken).ConfigureAwait(false);
        }
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
            var plan = navigator.Plan(targetNodeId, includeDraftTransitions: false);
            lock (_sync)
            {
                _navigationPlan = plan;
                _navigationError = null;
                _navigationStatus = plan.UsesCalculatedRoute
                    ? $"Calculated plan ready from verified paths · {plan.CommandCount} commands"
                    : plan.UsesAnchor
                        ? $"Verified composite plan ready · {plan.CommandCount} commands"
                        : $"Verified plan ready · {plan.CommandCount} commands";
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

    public Task NavigateToMenuNodeAsync(
        string targetNodeId,
        CancellationToken cancellationToken = default)
    {
        var plan = CreateNavigationPlan(targetNodeId);
        return RunNavigationAsync(
            $"Navigate · {plan.TargetPath}",
            (navigator, token) => navigator.ExecutePlanAsync(plan, token),
            clearPlanOnSuccess: true,
            cancellationToken);
    }

    public async Task<MenuTraversalFailureReport> ReportMenuTraversalFailureAsync(
        NavigationPlan failedPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failedPlan);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("report a failed menu traversal");
        EnsureNoMenuRecording("report a failed menu traversal");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "Connect to the TV before reporting a failed traversal so the app can restore normal video.");
        }

        MenuDefinition definition;
        MenuAnchor knownStateAnchor;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            if (!failedPlan.DefinitionId.Equals(
                    definition.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The failed traversal belongs to a different menu definition.");
            }

            definition.GetRequiredNode(failedPlan.SourceNodeId);
            definition.GetRequiredNode(failedPlan.TargetNodeId);
            knownStateAnchor = FindPreferredKnownStateAnchor(definition)
                ?? throw new InvalidOperationException(
                    "No verified known-state anchor is available to restore normal video.");
        }

        var operations = GetNavigationPlanOperations(failedPlan);
        if (operations.Count == 0)
        {
            throw new InvalidOperationException(
                "The traversal did not contain any keys to validate.");
        }

        var transitions = definition.Transitions.Values.ToList();
        var directTransition = failedPlan.AnchorLeg is null
                               && failedPlan.CalculatedLeg is null
                               && failedPlan.Transitions.Count == 1
            ? failedPlan.Transitions[0]
            : null;
        var existingDirect = directTransition is not null
                             && directTransition.FromNodeId.Equals(
                                 failedPlan.SourceNodeId,
                                 StringComparison.OrdinalIgnoreCase)
                             && directTransition.ToNodeId.Equals(
                                 failedPlan.TargetNodeId,
                                 StringComparison.OrdinalIgnoreCase)
                             && definition.Transitions.TryGetValue(
                                 directTransition.Id,
                                 out var storedDirect)
            ? storedDirect
            : null;

        string draftId;
        if (existingDirect is not null)
        {
            draftId = existingDirect.Id;
            transitions.RemoveAll(transition => transition.Id.Equals(
                draftId,
                StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var matchingDrafts = transitions.Where(transition =>
                    !transition.Verified
                    && string.Equals(
                        transition.ConfigurationId,
                        definition.ActiveConfigurationId,
                        StringComparison.OrdinalIgnoreCase)
                    && transition.FromNodeId.Equals(
                        failedPlan.SourceNodeId,
                        StringComparison.OrdinalIgnoreCase)
                    && transition.ToNodeId.Equals(
                        failedPlan.TargetNodeId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingDrafts.Length > 1)
            {
                throw new InvalidOperationException(
                    "More than one diagnostic draft already exists for this traversal. Remove the duplicates in Build & Verify first.");
            }

            draftId = matchingDrafts.FirstOrDefault()?.Id
                ?? CreateTraversalFailureDraftId(
                    definition,
                    failedPlan.SourceNodeId,
                    failedPlan.TargetNodeId);
            transitions.RemoveAll(transition => transition.Id.Equals(
                draftId,
                StringComparison.OrdinalIgnoreCase));
        }

        transitions.Add(new MenuTransition(
            draftId,
            failedPlan.SourceNodeId,
            failedPlan.TargetNodeId,
            operations,
            Verified: false,
            Description: TraversalFailureDescription,
            ConfigurationId: definition.ActiveConfigurationId));
        var updated = CopyMenuDefinition(definition, transitions: transitions);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            SetMenuValidationPasses(
                MenuAuthoringItemKind.Transition,
                draftId,
                0);
            _menuValidation = new MenuValidationSession(
                MenuAuthoringItemKind.Transition,
                draftId,
                0,
                false,
                updated.GetPath(failedPlan.TargetNodeId));
            _menuAuthoringStatus =
                $"Failed traversal captured · {updated.GetPath(failedPlan.SourceNodeId)} → {updated.GetPath(failedPlan.TargetNodeId)} · inspect keys and timing below";
            _menuAuthoringError = null;
            _navigationPlan = null;
        }

        NotifyChanged();
        await RunMenuAnchorAsync(knownStateAnchor.Id, cancellationToken)
            .ConfigureAwait(false);
        return new MenuTraversalFailureReport(
            draftId,
            updated.GetPath(failedPlan.SourceNodeId),
            updated.GetPath(failedPlan.TargetNodeId),
            knownStateAnchor.Label,
            updated.GetPath(knownStateAnchor.TargetNodeId));
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
            .Select(CreateMacroSummary)
            .ToArray();
    }

    public async Task PrepareMacroRecordingStartAsync(
        string startingNodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startingNodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoMenuRecording("prepare a macro starting state");
        ValidateMacroStartState(startingNodeId);
        string targetPath;
        lock (_sync)
        {
            targetPath = _menuDefinition!.GetPath(startingNodeId.Trim());
        }

        await RunNavigationAsync(
                $"Prepare macro start · {targetPath}",
                (navigator, token) => navigator.PrepareStateAsync(startingNodeId.Trim(), token),
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MacroSummary>> SetMacroFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path.Trim());
        var catalog = File.Exists(fullPath)
            ? await LoadValidatedCatalogAsync(fullPath, cancellationToken).ConfigureAwait(false)
            : new MacroCatalog([]);
        await UpdateSettingsAsync(
                current => current with { MacroFilePath = fullPath },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
        return catalog.Macros.Values
            .OrderBy(macro => macro.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CreateMacroSummary)
            .ToArray();
    }

    public async Task<MacroDetails> LoadMacroDetailsAsync(
        string macroName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var macro = (await LoadValidatedCatalogAsync(cancellationToken).ConfigureAwait(false))
            .GetRequiredMacro(macroName.Trim());
        return CreateMacroDetails(macro);
    }

    public async Task<MacroSummary> SaveMacroAsync(
        string? originalName,
        MacroEditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentNullException.ThrowIfNull(request.Steps);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("save a macro");

        await _macroCatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSnapshot().MacroFilePath;
            var catalog = await LoadCatalogForEditingAsync(path, cancellationToken).ConfigureAwait(false);
            var normalizedOriginal = string.IsNullOrWhiteSpace(originalName) ? null : originalName.Trim();
            MacroDefinition? existing = null;
            if (normalizedOriginal is not null)
            {
                existing = catalog.GetRequiredMacro(normalizedOriginal);
            }

            var normalizedName = request.Name.Trim();
            if (catalog.TryGetMacro(normalizedName, out var collision)
                && collision is not null
                && (existing is null
                    || !collision.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Macro '{normalizedName}' already exists.");
            }

            var steps = request.Steps.ToArray();
            var startingNodeId = string.IsNullOrWhiteSpace(request.StartingNodeId)
                ? null
                : request.StartingNodeId.Trim();
            var behaviorUnchanged = existing is not null
                && existing.Steps.SequenceEqual(steps)
                && string.Equals(
                    existing.StartingNodeId,
                    startingNodeId,
                    StringComparison.OrdinalIgnoreCase);
            var updated = new MacroDefinition(
                normalizedName,
                steps,
                string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                verified: behaviorUnchanged && existing!.Verified,
                verificationPasses: behaviorUnchanged ? existing!.VerificationPasses : 0,
                startingNodeId: startingNodeId,
                confirmBeforeRun: request.ConfirmBeforeRun);

            var definitions = new List<MacroDefinition>(catalog.Macros.Count + (existing is null ? 1 : 0));
            foreach (var macro in catalog.Macros.Values)
            {
                if (existing is not null
                    && macro.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                {
                    definitions.Add(updated);
                    continue;
                }

                definitions.Add(existing is not null
                    && !existing.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
                        ? RenameMacroCalls(macro, existing.Name, normalizedName)
                        : macro);
            }

            if (existing is null)
            {
                definitions.Add(updated);
            }

            var savedCatalog = new MacroCatalog(definitions, catalog.Variables);
            ValidateMacroMenuDestinations(savedCatalog);
            await new MacroCatalogWriter().WriteFileAsync(path, savedCatalog, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null && !updated.Verified)
            {
                await RemoveQuickAccessMacroAsync(existing.Name, cancellationToken).ConfigureAwait(false);
            }
            else if (existing is not null
                && !existing.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                await RenameQuickAccessMacroAsync(existing.Name, normalizedName, cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_sync)
            {
                _macroValidationCandidate = null;
            }

            NotifyChanged();
            return CreateMacroSummary(updated);
        }
        finally
        {
            _macroCatalogGate.Release();
        }
    }

    public async Task DeleteMacroAsync(
        string macroName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("delete a macro");

        await _macroCatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSnapshot().MacroFilePath;
            var catalog = await LoadValidatedCatalogAsync(path, cancellationToken).ConfigureAwait(false);
            var existing = catalog.GetRequiredMacro(macroName.Trim());
            var definitions = catalog.Macros.Values
                .Where(macro => !macro.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (definitions.Length == 0)
            {
                throw new InvalidOperationException(
                    "A macro catalog must contain at least one macro. Create its replacement before deleting this one.");
            }

            var callers = catalog.Macros.Values
                .Where(macro => !macro.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(macro => macro.Steps.Select((step, index) => new
                {
                    macro.Name,
                    StepNumber = index + 1,
                    CalledMacro = step is CallMacroStep call ? call.MacroName : null
                }))
                .Where(reference => reference.CalledMacro?.Equals(
                    existing.Name,
                    StringComparison.OrdinalIgnoreCase) == true)
                .Select(reference => $"{reference.Name} (step {reference.StepNumber})")
                .ToArray();
            if (callers.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Cannot delete '{existing.Name}' because it is called by {string.Join(", ", callers)}. " +
                    "Edit those macros and remove or replace the listed call steps first.");
            }

            await new MacroCatalogWriter().WriteFileAsync(
                    path,
                    new MacroCatalog(definitions, catalog.Variables),
                    cancellationToken)
                .ConfigureAwait(false);
            await RemoveQuickAccessMacroAsync(existing.Name, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _macroValidationCandidate = null;
            }

            NotifyChanged();
        }
        finally
        {
            _macroCatalogGate.Release();
        }
    }

    public async Task<MacroSummary> ConfirmMacroValidationAsync(
        string macroName,
        bool passed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroName);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("confirm a macro verification run");
        var normalizedName = macroName.Trim();
        await _macroCatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_macroValidationCandidate is null
                    || !_macroValidationCandidate.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Replay this macro successfully before recording its visual verification result.");
                }
            }

            var path = GetSnapshot().MacroFilePath;
            var catalog = await LoadValidatedCatalogAsync(path, cancellationToken).ConfigureAwait(false);
            var existing = catalog.GetRequiredMacro(normalizedName);
            var passes = passed ? Math.Min(3, existing.VerificationPasses + 1) : 0;
            var updated = new MacroDefinition(
                existing.Name,
                existing.Steps,
                existing.Description,
                verified: passes >= 3,
                verificationPasses: passes,
                startingNodeId: existing.StartingNodeId,
                confirmBeforeRun: existing.ConfirmBeforeRun);
            var definitions = catalog.Macros.Values
                .Select(macro => macro.Name.Equals(existing.Name, StringComparison.OrdinalIgnoreCase)
                    ? updated
                    : macro)
                .ToArray();
            await new MacroCatalogWriter().WriteFileAsync(
                    path,
                    new MacroCatalog(definitions, catalog.Variables),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!passed)
            {
                await RemoveQuickAccessMacroAsync(existing.Name, cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _macroValidationCandidate = null;
                _lastMacroStatus = passed
                    ? passes >= 3
                        ? "Verified · 3/3 visual passes"
                        : $"Visual pass recorded · {passes}/3"
                    : "Visual check failed · verification reset to 0/3";
            }

            NotifyChanged();
            return CreateMacroSummary(updated);
        }
        finally
        {
            _macroCatalogGate.Release();
        }
    }

    public async Task<string> ExportMacroCatalogAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await LoadValidatedCatalogAsync(cancellationToken).ConfigureAwait(false);
        return new MacroCatalogWriter().Serialize(catalog);
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
        var macro = catalog.GetRequiredMacro(target);
        if (!macro.Verified)
        {
            throw new InvalidOperationException(
                $"Macro '{macro.Name}' needs 3/3 visual verification passes before it can be added to quick access.");
        }
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
                var quickCatalog = await LoadValidatedCatalogAsync(cancellationToken)
                    .ConfigureAwait(false);
                var quickMacro = quickCatalog.GetRequiredMacro(action.Target);
                if (!quickMacro.Verified)
                {
                    throw new InvalidOperationException(
                        $"Macro '{quickMacro.Name}' is no longer verified. Replay and confirm it on the Macros page before using quick access.");
                }

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
            _macroValidationCandidate = null;
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
                _macroValidationCandidate = macroName;
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

    private async Task SynchronizeMenuAfterConnectAsync(CancellationToken cancellationToken)
    {
        MenuAnchor? anchor;
        lock (_sync)
        {
            anchor = _menuDefinition?.ApplicableAnchors
                .Where(candidate => candidate.Verified)
                .OrderBy(candidate =>
                    candidate.Id.Equals("normal-video", StringComparison.OrdinalIgnoreCase)
                    || candidate.TargetNodeId.Equals("normal-video", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (anchor is null)
            {
                _navigationStatus = "Connected · no verified known-state anchor is available";
                _navigationError = null;
            }
        }

        if (anchor is null)
        {
            _menuStateTracker?.MarkUnknown(
                "The TV connected, but no verified anchor is available to establish a known menu state.");
            NotifyChanged();
            return;
        }

        try
        {
            await RunMenuAnchorAsync(anchor.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_sync)
            {
                _lastError =
                    $"Connected, but automatic menu synchronization failed: {exception.Message}";
            }

            NotifyChanged();
        }
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

    private async Task ExecuteMenuDestinationWithinMacroAsync(
        string targetNodeId,
        CancellationToken cancellationToken)
    {
        ValidateMacroMenuDestination(targetNodeId, requireKnownState: true);
        MenuNavigator navigator;
        lock (_sync)
        {
            navigator = _menuNavigator
                ?? throw new InvalidOperationException(
                    _navigationError ?? "No valid menu definition is loaded.");
        }

        NavigationPlan plan;
        try
        {
            plan = navigator.Plan(targetNodeId, includeDraftTransitions: false);
            lock (_sync)
            {
                _navigationPlan = plan;
                _navigationStatus = $"Macro menu call · {plan.TargetPath}";
                _navigationError = null;
                _navigationProgress = null;
            }

            NotifyChanged();
            await navigator.ExecutePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Completed macro menu call · {plan.TargetPath}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Cancelled macro menu call · {targetNodeId}";
            }

            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Failed macro menu call · {targetNodeId}";
                _navigationError = exception.Message;
            }

            throw;
        }
        finally
        {
            NotifyChanged();
        }
    }

    private async Task PrepareMenuStartStateWithinMacroAsync(
        string startingNodeId,
        CancellationToken cancellationToken)
    {
        ValidateMacroStartState(startingNodeId);
        MenuNavigator navigator;
        string targetPath;
        lock (_sync)
        {
            navigator = _menuNavigator
                ?? throw new InvalidOperationException(
                    _navigationError ?? "No valid menu definition is loaded.");
            targetPath = _menuDefinition!.GetPath(startingNodeId.Trim());
            _navigationStatus = $"Macro start · preparing {targetPath}";
            _navigationError = null;
            _navigationProgress = null;
        }

        NotifyChanged();
        try
        {
            await navigator.PrepareStateAsync(startingNodeId.Trim(), cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Macro start ready · {targetPath}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Cancelled macro start preparation · {targetPath}";
            }

            throw;
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _navigationPlan = null;
                _navigationStatus = $"Failed macro start preparation · {targetPath}";
                _navigationError = exception.Message;
            }

            throw;
        }
        finally
        {
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

    private static IReadOnlyList<MenuOperation> GetNavigationPlanOperations(
        NavigationPlan plan)
    {
        var operations = new List<MenuOperation>();
        if (plan.AnchorLeg is { } anchorLeg)
        {
            operations.AddRange(anchorLeg.Operations);
        }

        if (plan.CalculatedLeg is { } calculatedLeg)
        {
            operations.AddRange(calculatedLeg.Operations);
        }

        operations.AddRange(plan.Transitions.SelectMany(transition => transition.Operations));
        return CoalesceOperations(ExpandOperations(operations));
    }

    private static MenuAnchor? FindPreferredKnownStateAnchor(
        MenuDefinition definition) => definition.ApplicableAnchors
        .Where(anchor => anchor.Verified)
        .OrderBy(anchor =>
            anchor.Id.Equals("normal-video", StringComparison.OrdinalIgnoreCase)
            || anchor.TargetNodeId.Equals("normal-video", StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
        .ThenBy(anchor => anchor.Label, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    private static string CreateTraversalFailureDraftId(
        MenuDefinition definition,
        string sourceNodeId,
        string targetNodeId)
    {
        var baseId = $"debug-{sourceNodeId}-to-{targetNodeId}";
        if (!definition.Transitions.ContainsKey(baseId)
            && !definition.Anchors.ContainsKey(baseId))
        {
            return baseId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseId}-{suffix}";
            if (!definition.Transitions.ContainsKey(candidate)
                && !definition.Anchors.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

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

    private static HashSet<string> GetMenuSubtreeNodeIds(
        MenuDefinition definition,
        string rootNodeId)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rootNodeId
        };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in definition.Nodes.Values)
            {
                if (node.ParentId is not null
                    && result.Contains(node.ParentId)
                    && result.Add(node.Id))
                {
                    changed = true;
                }
            }
        }

        return result;
    }

    private static bool HaveSameParent(MenuNode left, MenuNode right) =>
        string.Equals(left.ParentId, right.ParentId, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MenuNode> FlattenMenuNodes(
        IReadOnlyList<MenuNode> nodes)
    {
        var result = new List<MenuNode>(nodes.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AppendBranch(MenuNode node)
        {
            if (!visited.Add(node.Id))
            {
                return;
            }

            result.Add(node);
            foreach (var child in nodes.Where(candidate =>
                         string.Equals(candidate.ParentId, node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                AppendBranch(child);
            }
        }

        foreach (var root in nodes.Where(node => string.IsNullOrWhiteSpace(node.ParentId)))
        {
            AppendBranch(root);
        }

        foreach (var node in nodes)
        {
            AppendBranch(node);
        }

        return result;
    }

    private static MenuDefinition CopyMenuDefinition(
        MenuDefinition definition,
        IEnumerable<MenuTransition>? transitions = null,
        IEnumerable<MenuAnchor>? anchors = null,
        MenuTimingProfile? timing = null,
        IEnumerable<MenuNode>? nodes = null) =>
        new(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            nodes ?? definition.Nodes.Values,
            transitions ?? definition.Transitions.Values,
            anchors ?? definition.Anchors.Values,
            timing ?? definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId);

    private static MenuConfiguration NormalizeMenuConfigurationRequest(
        MenuConfigurationEditRequest request) => new(
            request.Id.Trim(),
            request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.Conditions) ? null : request.Conditions.Trim());

    private static bool IsMenuNodeDisabledByDefault(
        MenuDefinition definition,
        MenuNode node) => (node.DisabledWhen ?? []).Any(condition =>
        definition.Nodes.TryGetValue(condition.SettingNodeId, out var setting)
        && setting.DefaultValue?.Equals(
            condition.EqualsValue,
            StringComparison.OrdinalIgnoreCase) == true);

    private static MenuNode NormalizeMenuNodeRequest(MenuNodeEditRequest request) =>
        new(
            request.Id.Trim(),
            request.Label.Trim(),
            string.IsNullOrWhiteSpace(request.ParentId) ? null : request.ParentId.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            request.ControlType,
            string.IsNullOrWhiteSpace(request.DefaultValue) ? null : request.DefaultValue.Trim(),
            request.DisabledWhen?
                .Where(condition => condition is not null)
                .Select(condition => new MenuNodeDisabledCondition(
                    condition.SettingNodeId.Trim(),
                    condition.EqualsValue.Trim()))
                .ToArray() ?? [],
            request.SelectionOptions?
                .Select(option => option.Trim())
                .ToArray() ?? [],
            request.MinimumValue,
            request.MaximumValue);

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
        if (request.RecordReturnToVideo
            && request.Kind != MenuAuthoringItemKind.Transition)
        {
            throw new InvalidOperationException(
                "A return-to-video sequence can be recorded only with a traversal.");
        }

        if (request.RecordReturnToVideo
            && FindReturnToVideoAnchor(definition) is null)
        {
            throw new InvalidOperationException(
                "Record a return-to-normal-video anchor before adding traversal-specific return keys.");
        }

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
                [placeholder],
                ValidationSourceNodeId: request.SourceNodeId,
                ConfigurationId: definition.ActiveConfigurationId));
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
                [placeholder],
                ConfigurationId: definition.ActiveConfigurationId));
        }

        var candidate = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            nodes,
            transitions,
            anchors,
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId);
        new MenuDefinitionValidator().ValidateAndThrow(candidate);
    }

    private static MenuDefinition AddDraftRecording(
        MenuDefinition definition,
        MenuRecordingRequest request,
        IReadOnlyList<MenuOperation> operations,
        IReadOnlyList<MenuOperation> returnOperations)
    {
        definition = MigrateLegacyReturnReplacement(definition);
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
                description,
                ValidationSourceNodeId: request.SourceNodeId,
                ConfigurationId: definition.ActiveConfigurationId));
        }
        else
        {
            transitions.Add(new MenuTransition(
                request.ItemId,
                request.SourceNodeId!,
                request.TargetNodeId,
                operations,
                false,
                description,
                request.RecordReturnToVideo ? returnOperations : null,
                definition.ActiveConfigurationId));
        }

        var updated = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            nodes,
            transitions,
            anchors,
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId);
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

            InstallMenuDefinition(definition, preserveValidationProgress: true);
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
        definition.GetPath(GetAuthoringTargetNodeId(definition, kind, itemId));

    private static string GetAuthoringTargetNodeId(
        MenuDefinition definition,
        MenuAuthoringItemKind kind,
        string itemId) =>
        kind == MenuAuthoringItemKind.Anchor
            ? definition.GetRequiredAnchor(itemId).TargetNodeId
            : definition.Transitions.TryGetValue(itemId, out var transition)
                ? transition.ToNodeId
                : throw new KeyNotFoundException($"Menu transition '{itemId}' was not found.");

    private int GetMenuValidationPasses(
        MenuAuthoringItemKind kind,
        string itemId) =>
        _menuValidationPasses.TryGetValue(
            GetMenuValidationKey(kind, itemId),
            out var passes)
            ? passes
            : 0;

    private void SetMenuValidationPasses(
        MenuAuthoringItemKind kind,
        string itemId,
        int passes)
    {
        var key = GetMenuValidationKey(kind, itemId);
        if (passes <= 0)
        {
            _menuValidationPasses.Remove(key);
            return;
        }

        _menuValidationPasses[key] = passes;
    }

    private void PruneMenuValidationProgress(MenuDefinition definition)
    {
        var draftKeys = definition.Anchors.Values
            .Where(anchor => !anchor.Verified)
            .Select(anchor => GetMenuValidationKey(MenuAuthoringItemKind.Anchor, anchor.Id))
            .Concat(definition.Transitions.Values
                .Where(transition => !transition.Verified)
                .Select(transition => GetMenuValidationKey(
                    MenuAuthoringItemKind.Transition,
                    transition.Id)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _menuValidationPasses.Keys
                     .Where(key => !draftKeys.Contains(key))
                     .ToArray())
        {
            _menuValidationPasses.Remove(key);
        }
    }

    private static string GetMenuValidationKey(
        MenuAuthoringItemKind kind,
        string itemId) =>
        $"{kind}:{itemId.Trim()}";

    private void ConfirmMenuAuthoringTarget(string targetNodeId, string itemId) =>
        _menuStateTracker?.ConfirmNode(
            targetNodeId,
            $"The user confirmed that menu validation '{itemId}' reached its target.");

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

        return CopyMenuDefinition(
            definition,
            transitions: transitions,
            anchors: anchors);
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

    private void ConfirmSystemTimingTarget(MenuTransition transition) =>
        _menuStateTracker?.ConfirmNode(
            transition.ToNodeId,
            $"The user confirmed that system timing test '{transition.Id}' reached its target.");

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
            context.Anchor.TargetNodeId,
            definition.GetPath(context.Anchor.TargetNodeId),
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
            (context.Strategy.NodeOverrides ?? [])
                .OrderBy(item => definition.GetPath(item.NodeId), StringComparer.OrdinalIgnoreCase)
                .Select(item => new MenuReturnOverrideSummary(
                    item.NodeId,
                    definition.GetPath(item.NodeId),
                    FormatKeyScript(item.Script.Operations),
                    item.Script.Verified))
                .ToArray(),
            GetDeepReturnTestNodes(definition, context),
            validation?.Kind,
            validation?.Kind == MenuReturnScriptKind.NodeOverride
                ? validation.StartNodeId
                : null,
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
        var anchor = FindReturnToVideoAnchor(definition);
        if (anchor is null)
        {
            return null;
        }

        if (anchor.ReturnStrategy is { } existing)
        {
            return new ReturnStrategyContext(anchor, existing);
        }

        var returnTransition = definition.ApplicableTransitions
            .Where(transition =>
                transition.ToNodeId.Equals(anchor.TargetNodeId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(transition => transition.Verified)
            .ThenBy(transition => definition.GetDepth(transition.FromNodeId))
            .FirstOrDefault(transition => definition.ApplicableTransitions.Any(open =>
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

    private static MenuAnchor? FindReturnToVideoAnchor(
        MenuDefinition definition,
        string? excludedAnchorId = null)
    {
        if (definition.Anchors.TryGetValue("normal-video", out var namedAnchor)
            && definition.IsApplicableToActiveConfiguration(namedAnchor.ConfigurationId)
            && !namedAnchor.Id.Equals(excludedAnchorId, StringComparison.OrdinalIgnoreCase))
        {
            return namedAnchor;
        }

        return definition.ApplicableAnchors
            .Where(candidate => !candidate.Id.Equals(
                excludedAnchorId,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.TargetNodeId.Equals(
                "normal-video",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Verified)
            .FirstOrDefault();
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

    private static string NormalizeReturnOverrideNode(
        MenuDefinition definition,
        ReturnStrategyContext context,
        string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidOperationException(
                "Choose a menu state for this state-specific return-script test.");
        }

        var normalized = nodeId.Trim();
        definition.GetRequiredNode(normalized);
        if (!(context.Strategy.NodeOverrides ?? []).Any(item =>
                item.NodeId.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"No state-specific return script is defined for '{definition.GetPath(normalized)}'.");
        }

        return normalized;
    }

    private static MenuReturnScript GetReturnScript(
        MenuReturnStrategy strategy,
        MenuReturnScriptKind kind,
        string startNodeId) =>
        kind switch
        {
            MenuReturnScriptKind.AtMenuRoot => strategy.AtMenuRoot,
            MenuReturnScriptKind.BelowMenuRoot => strategy.BelowMenuRoot,
            MenuReturnScriptKind.NodeOverride => (strategy.NodeOverrides ?? [])
                .FirstOrDefault(item => item.NodeId.Equals(
                    startNodeId,
                    StringComparison.OrdinalIgnoreCase))?.Script
                ?? throw new InvalidOperationException(
                    $"No state-specific return script is defined for '{startNodeId}'."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static MenuDefinition SetReturnScriptVerified(
        MenuDefinition definition,
        ReturnStrategyContext context,
        MenuReturnScriptKind kind,
        string startNodeId,
        bool verified)
    {
        var strategy = context.Strategy;
        strategy = kind switch
        {
            MenuReturnScriptKind.AtMenuRoot => strategy with
            {
                AtMenuRoot = strategy.AtMenuRoot with { Verified = verified }
            },
            MenuReturnScriptKind.BelowMenuRoot => strategy with
            {
                BelowMenuRoot = strategy.BelowMenuRoot with { Verified = verified }
            },
            MenuReturnScriptKind.NodeOverride => strategy with
            {
                NodeOverrides = (strategy.NodeOverrides ?? [])
                    .Select(item => item.NodeId.Equals(startNodeId, StringComparison.OrdinalIgnoreCase)
                        ? item with { Script = item.Script with { Verified = verified } }
                        : item)
                    .ToArray()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
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
        definition.ApplicableTransitions
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
        foreach (var anchor in definition.ApplicableAnchors.Where(anchor => anchor.Verified))
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

    private void InstallMenuDefinition(
        MenuDefinition definition,
        bool preserveValidationProgress = false)
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
            if (preserveValidationProgress)
            {
                PruneMenuValidationProgress(definition);
            }
            else
            {
                _menuValidationPasses.Clear();
            }
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

    private static MenuDefinition ActivateMenuConfiguration(
        MenuDefinition definition,
        string? requestedConfigurationId) =>
        definition.WithActiveConfiguration(
            ResolveMenuConfigurationId(definition, requestedConfigurationId));

    private static string? ResolveMenuConfigurationId(
        MenuDefinition definition,
        string? requestedConfigurationId)
    {
        if (!string.IsNullOrWhiteSpace(requestedConfigurationId)
            && definition.Configurations.TryGetValue(requestedConfigurationId, out var requested))
        {
            return requested.Id;
        }

        return definition.Configurations.Values.FirstOrDefault()?.Id;
    }

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

    private static async Task<MacroCatalog> LoadCatalogForEditingAsync(
        string path,
        CancellationToken cancellationToken) =>
        File.Exists(path)
            ? await LoadValidatedCatalogAsync(path, cancellationToken).ConfigureAwait(false)
            : new MacroCatalog([]);

    private static MacroSummary CreateMacroSummary(MacroDefinition macro) =>
        new(
            macro.Name,
            macro.Description,
            macro.Steps.Count,
            macro.Verified,
            macro.VerificationPasses,
            macro.StartingNodeId,
            macro.ConfirmBeforeRun);

    private static MacroDetails CreateMacroDetails(MacroDefinition macro) =>
        new(
            macro.Name,
            macro.Description,
            macro.Steps.ToArray(),
            macro.Verified,
            macro.VerificationPasses,
            macro.StartingNodeId,
            macro.ConfirmBeforeRun);

    private void ValidateMacroMenuDestinations(MacroCatalog catalog)
    {
        foreach (var startingNodeId in catalog.Macros.Values
                     .Select(macro => macro.StartingNodeId)
                     .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateMacroStartState(startingNodeId);
        }

        foreach (var targetNodeId in catalog.Macros.Values
                     .SelectMany(macro => macro.Steps)
                     .OfType<MenuStep>()
                     .Select(step => step.TargetNodeId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateMacroMenuDestination(targetNodeId);
        }
    }

    private void ValidateMacroStartState(string startingNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startingNodeId);
        lock (_sync)
        {
            var navigator = _menuNavigator
                ?? throw new InvalidOperationException(
                    _navigationError ?? "Load a menu definition before assigning a macro starting state.");
            try
            {
                navigator.ValidateStateCanBePrepared(startingNodeId.Trim());
            }
            catch (KeyNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Macro starting state '{startingNodeId}' does not exist in the active menu definition.");
            }
        }
    }

    private void ValidateCurrentMacroMenuState()
    {
        lock (_sync)
        {
            if (_menuStateTracker?.Current.NodeId is null)
            {
                throw new InvalidOperationException(
                    "The expected menu state is unknown, so a macro menu destination cannot be planned before a declared starting state. Run a verified anchor or assign the macro a starting state.");
            }
        }
    }

    private void ValidateMacroMenuDestination(
        string targetNodeId,
        bool requireKnownState = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        lock (_sync)
        {
            var definition = _menuDefinition
                ?? throw new InvalidOperationException(
                    "Load a menu definition before using menu destinations in a macro.");
            if (!definition.Nodes.TryGetValue(targetNodeId.Trim(), out var node))
            {
                throw new InvalidOperationException(
                    $"Menu destination '{targetNodeId}' does not exist in '{definition.Name}'.");
            }

            var verified = definition.ApplicableTransitions.Any(transition =>
                    transition.Verified
                    && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                || definition.ApplicableAnchors.Any(anchor =>
                    anchor.Verified
                    && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
            if (!verified)
            {
                var configuration = definition.ActiveConfigurationId is { } activeId
                    && definition.Configurations.TryGetValue(activeId, out var active)
                        ? $" for menu configuration '{active.Name}'"
                        : string.Empty;
                throw new InvalidOperationException(
                    $"Menu destination '{definition.GetPath(node.Id)}' is not verified{configuration} and cannot be called by a macro.");
            }

            if (requireKnownState && _menuStateTracker?.Current.NodeId is null)
            {
                throw new InvalidOperationException(
                    $"The expected menu state is unknown, so macro destination '{definition.GetPath(node.Id)}' cannot be planned. Run a verified anchor first.");
            }
        }
    }

    private static MacroDefinition RenameMacroCalls(
        MacroDefinition macro,
        string oldName,
        string newName)
    {
        var changed = false;
        var steps = macro.Steps.Select(step =>
        {
            if (step is CallMacroStep call
                && call.MacroName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                return (MacroStep)new CallMacroStep(newName, call.Repeat);
            }

            return step;
        }).ToArray();
        return changed
            ? new MacroDefinition(
                macro.Name,
                steps,
                macro.Description,
                macro.Verified,
                macro.VerificationPasses,
                macro.StartingNodeId,
                macro.ConfirmBeforeRun)
            : macro;
    }

    private async Task RenameQuickAccessMacroAsync(
        string oldName,
        string newName,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var actions = GetQuickAccessActions().Select(action =>
        {
            if (action.Kind != QuickAccessActionKind.Macro
                || !action.Target.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                return action;
            }

            changed = true;
            return action with
            {
                Id = CreateQuickAccessId(QuickAccessActionKind.Macro, newName, RemoteKeyAction.Click),
                Target = newName
            };
        }).ToArray();
        if (changed)
        {
            await UpdateSettingsAsync(
                    settings => settings with { QuickAccess = actions },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RemoveQuickAccessMacroAsync(
        string macroName,
        CancellationToken cancellationToken)
    {
        var existing = GetQuickAccessActions();
        var actions = existing
            .Where(action => action.Kind != QuickAccessActionKind.Macro
                || !action.Target.Equals(macroName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (actions.Length != existing.Count)
        {
            await UpdateSettingsAsync(
                    settings => settings with { QuickAccess = actions },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<MenuDefinition> LoadValidatedMenuDefinitionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var parsed = await new MenuDefinitionParser()
            .ParseFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var definition = MigrateLegacyReturnReplacement(parsed);
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        return definition;
    }

    private static MenuDefinition MigrateLegacyReturnReplacement(
        MenuDefinition definition)
    {
        if (!definition.Anchors.TryGetValue(
                ReturnToVideoReplacementAnchorId,
                out var legacyReplacement)
            || string.IsNullOrWhiteSpace(legacyReplacement.ValidationSourceNodeId))
        {
            return definition;
        }

        var matchingTransitions = definition.Transitions.Values
            .Where(transition => transition.ToNodeId.Equals(
                legacyReplacement.ValidationSourceNodeId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(transition => transition.Verified)
            .ToArray();
        if (matchingTransitions.Length != 1)
        {
            return definition;
        }

        var matchedId = matchingTransitions[0].Id;
        var transitions = definition.Transitions.Values
            .Select(transition => transition.Id.Equals(
                matchedId,
                StringComparison.OrdinalIgnoreCase)
                ? transition with
                {
                    Verified = false,
                    ReturnToVideoOperations = legacyReplacement.Operations
                }
                : transition)
            .ToArray();
        var anchors = definition.Anchors.Values
            .Where(anchor => !anchor.Id.Equals(
                legacyReplacement.Id,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return CopyMenuDefinition(
            definition,
            transitions: transitions,
            anchors: anchors);
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
        if (eventArgs.Current == SamsungConnectionState.Connected)
        {
            lock (_sync)
            {
                _lastError = null;
            }
        }

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
        _macroCatalogGate.Dispose();
    }

    private sealed class WebMacroCommandTarget(SamsungControllerService controller) : IMacroMenuCommandTarget
    {
        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken = default) =>
            controller.SendTrackedKeyAsync(key, action, cancellationToken);

        public void ValidateMenuDestination(string targetNodeId) =>
            controller.ValidateMacroMenuDestination(targetNodeId);

        public void ValidateMenuStartState(string startingNodeId) =>
            controller.ValidateMacroStartState(startingNodeId);

        public void ValidateCurrentMenuState() =>
            controller.ValidateCurrentMacroMenuState();

        public Task PrepareMenuStartStateAsync(
            string startingNodeId,
            CancellationToken cancellationToken = default) =>
            controller.PrepareMenuStartStateWithinMacroAsync(startingNodeId, cancellationToken);

        public Task NavigateToMenuDestinationAsync(
            string targetNodeId,
            CancellationToken cancellationToken = default) =>
            controller.ExecuteMenuDestinationWithinMacroAsync(targetNodeId, cancellationToken);
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
