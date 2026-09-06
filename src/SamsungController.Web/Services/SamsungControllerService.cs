using System.Globalization;
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
    private const int SliderVerificationRequiredCount = 3;
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
    private readonly MenuVerificationStore _menuVerificationStore;
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
    private readonly DisplayDefinitionStore _displayDefinitionStore = new();
    private readonly Dictionary<string, int> _menuValidationPasses = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _menuControlValues = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _menuExternalStateValues = new(
        StringComparer.OrdinalIgnoreCase);

    private SamsungWebSettings _settings = new();
    private CancellationTokenSource? _macroSource;
    private CancellationTokenSource? _navigationSource;
    private MenuDefinition? _menuDefinition;
    private MenuDefinition? _menuDefinitionVerificationPlanSource;
    private MenuDefinitionVerificationPlan? _menuDefinitionVerificationPlan;
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
        _menuVerificationStore = new MenuVerificationStore(_configurationDirectory);
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
                menuDefinition = MenuDefinitionVerificationReconciler.Reconcile(
                    ActivateMenuConfiguration(
                        menuDefinition,
                        settings.MenuConfigurationId));
                menuStateTracker = new MenuStateTracker(menuDefinition);
                menuNavigator = new MenuNavigator(
                    menuDefinition,
                    menuStateTracker,
                    new WebMenuCommandTarget(_client),
                    _menuDelay);
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
                ResetMenuControlValues(menuDefinition);
                ResetMenuExternalStateValues(menuDefinition, settings);
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
                menuState?.Confidence ?? MenuStateConfidence.Unknown,
                _settings.DisplayDefinitionPath);
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

    public MenuControlBehaviorPreferences GetMenuControlBehaviorPreferences()
    {
        lock (_sync)
        {
            return new MenuControlBehaviorPreferences(
                _settings.MenuControlApplyImmediately,
                _settings.MenuControlReturnToNormalVideo);
        }
    }

    public async Task SaveMenuControlBehaviorPreferencesAsync(
        bool applyImmediately,
        bool returnToNormalVideo,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await UpdateSettingsAsync(
                current => current with
                {
                    MenuControlApplyImmediately = applyImmediately,
                    MenuControlReturnToNormalVideo = returnToNormalVideo
                },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
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
            var effectiveValues = definition is null
                ? null
                : CreateEffectivePictureControlValues(
                    definition,
                    _menuControlValues,
                    _menuExternalStateValues);
            var routeDefinition = definition is null
                ? null
                : CreateVisibilityAdjustedDefinition(definition, effectiveValues!);
            var nodes = definition is null
                ? []
                : definition.Nodes.Values.Select(node => new MenuNodeSummary(
                        node.Id,
                        node.Label,
                        definition.GetPath(node.Id),
                        definition.GetDepth(node.Id),
                        node.Description,
                        node.ParentId,
                        routeDefinition!.ApplicableTransitions.Any(transition =>
                            transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        || routeDefinition.ApplicableAnchors.Any(anchor =>
                            anchor.Verified
                            && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                        routeDefinition.ApplicableTransitions.Any(transition =>
                            !transition.Verified
                            && transition.ToNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                        || routeDefinition.ApplicableAnchors.Any(anchor =>
                            !anchor.Verified
                            && anchor.TargetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase)),
                        node.ControlType,
                        node.DefaultValue,
                        node.DisabledWhen ?? [],
                        node.Disabled,
                        IsMenuNodePermanentlyDisabled(definition, node),
                        IsMenuNodeDisabledByDefault(definition, node),
                        node.HiddenWhen ?? [],
                        IsMenuNodeHiddenByDefault(definition, node),
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
                _navigationError,
                new Dictionary<string, string>(
                    _menuControlValues,
                    StringComparer.OrdinalIgnoreCase),
                definition?.ExternalStates.Values.Select(state =>
                        new MenuExternalStateSummary(
                            state.Id,
                            state.Label,
                            _menuExternalStateValues.GetValueOrDefault(
                                state.Id,
                                state.DefaultValue),
                            state.DefaultValue,
                            state.Options))
                    .ToArray() ?? []);
        }
    }

    public async Task SetMenuExternalStateAsync(
        string stateId,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change an external menu state");
        EnsureNoMenuRecording("change an external menu state");

        MenuDefinition definition;
        MenuExternalState state;
        IReadOnlyList<MenuExternalStateValue> values;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            state = definition.ExternalStates.TryGetValue(stateId.Trim(), out var candidate)
                ? candidate
                : throw new InvalidOperationException(
                    $"External state '{stateId.Trim()}' is not defined by the active menu.");
            var normalizedValue = state.Options.FirstOrDefault(option => option.Equals(
                    value.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"'{value.Trim()}' is not available for {state.Label}.");
            values = definition.ExternalStates.Values.Select(item =>
                new MenuExternalStateValue(
                    item.Id,
                    item.Id.Equals(state.Id, StringComparison.OrdinalIgnoreCase)
                        ? normalizedValue
                        : _menuExternalStateValues.GetValueOrDefault(
                            item.Id,
                            item.DefaultValue))).ToArray();
        }

        await UpdateSettingsAsync(
                current => current with
                {
                    MenuExternalStateDefinitionId = definition.Id,
                    MenuExternalStateValues = values
                },
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var item in values)
            {
                _menuExternalStateValues[item.Id] = item.Value;
            }

            _navigationPlan = null;
            _navigationStatus = $"External state selected · {state.Label}: {_menuExternalStateValues[state.Id]}";
            if (_menuStateTracker?.Current.NodeId is { } nodeId
                && !nodeId.Equals("normal-video", StringComparison.OrdinalIgnoreCase))
            {
                _menuStateTracker.MarkUnknown(
                    $"{state.Label} changed outside the TV; synchronize menu position before navigating.");
            }
        }

        NotifyChanged();
    }

    public MenuControlVerificationSnapshot GetMenuControlVerificationSnapshot()
    {
        lock (_sync)
        {
            var availableSliderIds = (_menuDefinition?.Nodes.Values ?? [])
                .Where(node => node.ControlType == MenuControlType.Slider
                    && !IsMenuNodePermanentlyDisabled(_menuDefinition!, node))
                .Select(node => node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var savedSliderIds = _settings.Host?.Equals(
                    _settings.SliderVerificationHost,
                    StringComparison.OrdinalIgnoreCase) == true
                ? (_settings.VerifiedSliderNodeIds ?? [])
                    .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            var sliderIds = savedSliderIds
                .Where(availableSliderIds.Contains)
                .ToArray();
            var requiredSliderCount = Math.Min(
                SliderVerificationRequiredCount,
                availableSliderIds.Count);
            var requiredSelectionIds = GetVerifiableSelectionNodeIds(_menuDefinition);
            var savedSelectionIds = _settings.Host?.Equals(
                    _settings.SelectionVerificationHost,
                    StringComparison.OrdinalIgnoreCase) == true
                ? (_settings.VerifiedSelectionNodeIds ?? [])
                    .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            var confirmedSelectionIds = savedSelectionIds
                .Where(nodeId => requiredSelectionIds.Contains(
                    nodeId,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var requiredSelectionTypes = requiredSelectionIds
                .Select(nodeId => MenuControlBehaviorClassifier.GetEffectiveControlType(
                    _menuDefinition!.GetRequiredNode(nodeId)))
                .Distinct()
                .ToArray();
            var verifiedSelectionTypes = confirmedSelectionIds
                .Select(nodeId => MenuControlBehaviorClassifier.GetEffectiveControlType(
                    _menuDefinition!.GetRequiredNode(nodeId)))
                .Distinct()
                .ToArray();
            return new MenuControlVerificationSnapshot(
                sliderIds.Length,
                requiredSliderCount,
                requiredSliderCount > 0 && sliderIds.Length >= requiredSliderCount,
                sliderIds,
                verifiedSelectionTypes.Length,
                requiredSelectionTypes.Length,
                requiredSelectionTypes.Length > 0
                && verifiedSelectionTypes.Length >= requiredSelectionTypes.Length,
                confirmedSelectionIds,
                verifiedSelectionTypes);
        }
    }

    public MenuDefinitionVerificationSnapshot GetMenuDefinitionVerificationSnapshot()
    {
        lock (_sync)
        {
            if (_menuDefinition is not { } definition)
            {
                return new MenuDefinitionVerificationSnapshot(
                    null,
                    null,
                    "No menu definition loaded",
                    null,
                    false,
                    0,
                    0,
                    0,
                    null,
                    []);
            }

            var plan = GetMenuDefinitionVerificationPlan(definition);
            var controlVerification = GetMenuControlVerificationSnapshot();
            var records = (definition.Verification?.Checks ?? [])
                .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var checks = plan.Checks.Select(check =>
            {
                var verified = records.TryGetValue(check.Id, out var record)
                    && record.Fingerprint.Equals(check.Fingerprint, StringComparison.OrdinalIgnoreCase);
                var authoringItemKind = check.Kind switch
                {
                    MenuVerificationCheckKind.Anchor when check.SourceItemId is not null =>
                        MenuAuthoringItemKind.Anchor,
                    MenuVerificationCheckKind.Route when check.SourceItemId is not null =>
                        MenuAuthoringItemKind.Transition,
                    _ => (MenuAuthoringItemKind?)null
                };
                var returnScriptKind = GetVerificationReturnScriptKind(check);
                var returnValidationMatches = returnScriptKind is not null
                    && _menuReturnValidation is { } returnValidation
                    && returnValidation.Kind == returnScriptKind
                    && (returnScriptKind != MenuReturnScriptKind.NodeOverride
                        || check.TargetNodeId?.Equals(
                            returnValidation.StartNodeId,
                            StringComparison.OrdinalIgnoreCase) == true);
                var validationPasses = check.Kind switch
                {
                    MenuVerificationCheckKind.Timing => _menuTimingValidation?.Passes ?? 0,
                    MenuVerificationCheckKind.ReturnScript => returnValidationMatches
                        ? _menuReturnValidation!.Passes
                        : 0,
                    MenuVerificationCheckKind.Anchor or MenuVerificationCheckKind.Route
                        when authoringItemKind is { } itemKind
                             && check.SourceItemId is { } itemId =>
                        GetMenuValidationPasses(itemKind, itemId),
                    _ => 0
                };
                var awaitingValidationConfirmation = check.Kind switch
                {
                    MenuVerificationCheckKind.Timing =>
                        _menuTimingValidation?.AwaitingConfirmation == true,
                    MenuVerificationCheckKind.ReturnScript => returnValidationMatches
                        && _menuReturnValidation!.AwaitingConfirmation,
                    MenuVerificationCheckKind.Anchor or MenuVerificationCheckKind.Route
                        when authoringItemKind is { } itemKind
                             && check.SourceItemId is { } itemId =>
                        _menuValidation is { AwaitingConfirmation: true } activeValidation
                        && activeValidation.Kind == itemKind
                        && activeValidation.ItemId.Equals(
                            itemId,
                            StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                return new MenuDefinitionVerificationCheckSummary(
                    check.Id,
                    check.Kind,
                    check.Label,
                    check.Description,
                    verified,
                    HasExistingVerificationEvidence(definition, check, controlVerification),
                    string.IsNullOrWhiteSpace(check.ConfigurationId)
                    || check.ConfigurationId.Equals(
                        definition.ActiveConfigurationId,
                        StringComparison.OrdinalIgnoreCase),
                    check.TargetNodeId,
                    check.ConfigurationId,
                    authoringItemKind,
                    authoringItemKind is not null ? check.SourceItemId : null,
                    returnScriptKind,
                    validationPasses,
                    MenuValidationSession.RequiredPasses,
                    awaitingValidationConfirmation,
                    verified ? record!.VerifiedAtUtc : null);
            }).ToArray();
            var currentRecordKeys = plan.Checks
                .Select(check => $"{check.Id}\u001f{check.Fingerprint}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var staleCount = (definition.Verification?.Checks ?? []).Count(record =>
                !currentRecordKeys.Contains($"{record.Id}\u001f{record.Fingerprint}"));
            var verifiedCount = checks.Count(check => check.Verified);
            return new MenuDefinitionVerificationSnapshot(
                definition.Name,
                GetMenuDefinitionPath(_settings),
                MenuDefinitionVerificationPlanner.FormatDisplay(plan.Display),
                definition.Verification is null
                    ? null
                    : MenuDefinitionVerificationPlanner.FormatDisplay(definition.Verification.Display),
                checks.Length > 0 && verifiedCount == checks.Length,
                verifiedCount,
                checks.Length,
                staleCount,
                checks.Where(check => check.Verified)
                    .Select(check => check.VerifiedAtUtc)
                    .Max(),
                checks);
        }
    }

    private MenuDefinitionVerificationPlan GetMenuDefinitionVerificationPlan(
        MenuDefinition definition)
    {
        if (!ReferenceEquals(_menuDefinitionVerificationPlanSource, definition)
            || _menuDefinitionVerificationPlan is null)
        {
            _menuDefinitionVerificationPlan = MenuDefinitionVerificationPlanner.Create(definition);
            _menuDefinitionVerificationPlanSource = definition;
        }

        return _menuDefinitionVerificationPlan;
    }

    public async Task<MenuDefinitionVerificationSnapshot> ConfirmMenuDefinitionVerificationCheckAsync(
        string checkId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("record menu-definition verification");
        EnsureNoMenuRecording("record menu-definition verification");

        MenuDefinition definition;
        MenuDefinitionVerificationPlan plan;
        MenuDefinitionVerificationCheck check;
        MenuControlVerificationSnapshot controlVerification;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            plan = GetMenuDefinitionVerificationPlan(definition);
            check = plan.Checks.FirstOrDefault(item => item.Id.Equals(
                        checkId.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Verification check '{checkId.Trim()}' is no longer required by the loaded menu definition.");
            controlVerification = GetMenuControlVerificationSnapshot();
        }

        if (RequiresExistingVerificationEvidence(check.Kind)
            && !HasExistingVerificationEvidence(definition, check, controlVerification))
        {
            throw new InvalidOperationException(
                "Complete this route, anchor, return script, or timing test in Build & Verify before recording it in the menu verification manifest.");
        }

        var updated = AddVerificationRecords(definition, plan, [check]);
        await PersistActiveMenuVerificationAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Display verification recorded · {check.Label}";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        return GetMenuDefinitionVerificationSnapshot();
    }

    public async Task<MenuDefinitionVerificationSnapshot> CarryForwardExistingMenuVerificationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("carry forward menu-definition verification");
        EnsureNoMenuRecording("carry forward menu-definition verification");

        MenuDefinition definition;
        MenuDefinitionVerificationPlan plan;
        MenuControlVerificationSnapshot controlVerification;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            plan = GetMenuDefinitionVerificationPlan(definition);
            controlVerification = GetMenuControlVerificationSnapshot();
        }

        var ready = plan.Checks
            .Where(check => HasExistingVerificationEvidence(definition, check, controlVerification))
            .ToArray();
        if (ready.Length == 0)
        {
            throw new InvalidOperationException(
                "No existing verified routes, timing, sliders, or selections are ready to carry forward.");
        }

        var updated = AddVerificationRecords(definition, plan, ready);
        await PersistActiveMenuVerificationAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuAuthoringStatus = $"Carried {ready.Length} existing verification checks into the local display-verification record";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        return GetMenuDefinitionVerificationSnapshot();
    }

    public async Task<MenuDefinitionVerificationSnapshot> RemoveMenuDefinitionVerificationCheckAsync(
        string checkId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("remove menu-definition verification");
        EnsureNoMenuRecording("remove menu-definition verification");
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        if (definition.Verification is null)
        {
            return GetMenuDefinitionVerificationSnapshot();
        }

        var manifest = definition.Verification with
        {
            Checks = definition.Verification.Checks
                .Where(record => !record.Id.Equals(checkId.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        var updated = CopyMenuDefinition(
            definition,
            verification: manifest,
            replaceVerification: true);
        await PersistActiveMenuVerificationAsync(updated, cancellationToken).ConfigureAwait(false);
        NotifyChanged();
        return GetMenuDefinitionVerificationSnapshot();
    }

    public async Task<MenuDefinitionVerificationTestResult> RunMenuDefinitionVerificationTestAsync(
        string checkId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("run a display-verification test");
        EnsureNoMenuRecording("run a display-verification test");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "Connect to the TV before running an automated verification test.");
        }

        MenuDefinition definition;
        MenuDefinitionVerificationCheck check;
        Dictionary<string, string> effectiveValues;
        MenuControlVerificationSnapshot controlVerification;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            check = GetMenuDefinitionVerificationPlan(definition).Checks
                .FirstOrDefault(item => item.Id.Equals(
                    checkId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Verification check '{checkId.Trim()}' is no longer required by the loaded menu definition.");
            effectiveValues = CreateEffectivePictureControlValues(
                definition,
                _menuControlValues,
                _menuExternalStateValues);
            controlVerification = GetMenuControlVerificationSnapshot();
        }

        if (check.TargetNodeId is null)
        {
            throw new InvalidOperationException(
                $"'{check.Label}' does not have an automated TV target.");
        }

        return check.Kind switch
        {
            MenuVerificationCheckKind.SliderBehavior =>
                await RunAdjustableMenuDefinitionVerificationTestAsync(
                        definition,
                        check,
                        SelectMenuDefinitionVerificationControl(
                            definition,
                            check,
                            effectiveValues,
                            controlVerification.ConfirmedSliderNodeIds),
                        effectiveValues,
                        cancellationToken)
                    .ConfigureAwait(false),
            MenuVerificationCheckKind.Selection
                or MenuVerificationCheckKind.Switch =>
                await RunAdjustableMenuDefinitionVerificationTestAsync(
                        definition,
                        check,
                        SelectMenuDefinitionVerificationControl(
                            definition,
                            check,
                            effectiveValues,
                            []),
                        effectiveValues,
                        cancellationToken)
                    .ConfigureAwait(false),
            MenuVerificationCheckKind.Confirmation =>
                await RunConfirmationMenuDefinitionVerificationTestAsync(
                        definition,
                        check,
                        SelectMenuDefinitionVerificationControl(
                            definition,
                            check,
                            effectiveValues,
                            []),
                        effectiveValues,
                        cancellationToken)
                    .ConfigureAwait(false),
            MenuVerificationCheckKind.ConditionalVisibility =>
                await RunConditionalMenuDefinitionVerificationTestAsync(
                        definition,
                        check,
                        effectiveValues,
                        cancellationToken)
                    .ConfigureAwait(false),
            MenuVerificationCheckKind.CalculatedNavigation =>
                await RunCalculatedNavigationVerificationTestAsync(
                        definition,
                        check,
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"'{check.Label}' is verified through its existing Build & Verify workflow rather than an automated control adjustment.")
        };
    }

    private async Task<MenuDefinitionVerificationTestResult>
        RunCalculatedNavigationVerificationTestAsync(
            MenuDefinition definition,
            MenuDefinitionVerificationCheck check,
            CancellationToken cancellationToken)
    {
        var sourceNodeId = check.SourceNodeId
            ?? throw new InvalidOperationException(
                $"'{check.Label}' does not define its calculated-route starting location.");
        var anchorId = check.PreparationAnchorId
            ?? throw new InvalidOperationException(
                $"'{check.Label}' does not define a known-state preparation anchor.");
        var targetNodeId = check.TargetNodeId!;
        NavigationPlan? calculatedPlan = null;
        await RunNavigationAsync(
                $"Verify calculated navigation · {definition.GetPath(sourceNodeId)} → {definition.GetPath(targetNodeId)}",
                async (navigator, token) =>
                {
                    await navigator.ExecuteAnchorAsync(anchorId, token).ConfigureAwait(false);
                    if (!definition.GetRequiredAnchor(anchorId).TargetNodeId.Equals(
                            sourceNodeId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var sourcePlan = navigator.Plan(
                            sourceNodeId,
                            includeDraftTransitions: false);
                        await navigator.ExecutePlanAsync(sourcePlan, token).ConfigureAwait(false);
                    }

                    calculatedPlan = navigator.Plan(
                        targetNodeId,
                        includeDraftTransitions: false);
                    if (!calculatedPlan.UsesCalculatedRoute
                        || calculatedPlan.UsesAnchor
                        || calculatedPlan.CalculatedLeg?.Operations.Any(operation =>
                            operation.Key.Equals(
                                "KEY_RETURN",
                                StringComparison.OrdinalIgnoreCase)) != true)
                    {
                        throw new InvalidOperationException(
                            "The representative route no longer exercises calculated cross-branch navigation. Reload the menu definition to regenerate verification requirements.");
                    }

                    await navigator.ExecutePlanAsync(calculatedPlan, token).ConfigureAwait(false);
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);

        return new MenuDefinitionVerificationTestResult(
            check.Id,
            targetNodeId,
            definition.GetPath(targetNodeId),
            null,
            $"Starting condition: {MenuDefinitionVerificationPlanner.DescribeVisualPosition(definition, sourceNodeId)} Used {calculatedPlan!.CommandCount} calculated command{(calculatedPlan.CommandCount == 1 ? string.Empty : "s")} without returning to normal video. Expected finish: {MenuDefinitionVerificationPlanner.DescribeVisualPosition(definition, targetNodeId)} Confirm that exact visual state before counting the pass.",
            []);
    }

    public async Task<string> ReturnMenuDefinitionVerificationToKnownStateAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("return display verification to a known state");
        EnsureNoMenuRecording("return display verification to a known state");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "Connect to the TV before returning verification to a known state.");
        }

        MenuDefinition definition;
        MenuAnchor anchor;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            var returnAnchor = FindReturnToVideoAnchor(definition);
            anchor = returnAnchor?.Verified == true
                ? returnAnchor
                : FindPreferredKnownStateAnchor(definition)
                  ?? throw new InvalidOperationException(
                      "No verified known-state anchor is available for verification recovery.");
        }

        await RunMenuAnchorAsync(anchor.Id, cancellationToken).ConfigureAwait(false);
        return definition.GetPath(anchor.TargetNodeId);
    }

    public async Task<int> RestoreMenuDefinitionVerificationTestAsync(
        MenuDefinitionVerificationTestResult test,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(test);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("restore display-verification values");
        EnsureNoMenuRecording("restore display-verification values");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException(
                "Connect to the TV before restoring verification values.");
        }

        MenuDefinition definition;
        Dictionary<string, string> effectiveValues;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            effectiveValues = CreateEffectivePictureControlValues(
                definition,
                _menuControlValues,
                _menuExternalStateValues);
        }

        var restoredCount = 0;
        foreach (var applied in test.AppliedUpdates.Reverse())
        {
            var node = definition.GetRequiredNode(applied.NodeId);
            if (!effectiveValues.TryGetValue(node.Id, out var currentValue))
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(node.Id)}' no longer has a predicted value for restoration.");
            }

            if (!currentValue.Equals(applied.ToValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot safely restore '{definition.GetPath(node.Id)}': its predicted value changed from '{applied.ToValue}' to '{currentValue}' after the test.");
            }

            await ApplyMenuControlValuesAsync(
                    [new MenuControlValueUpdate(
                        node.Id,
                        currentValue,
                        applied.FromValue)],
                    effectiveValues,
                    returnToNormalVideo: false,
                    cancellationToken)
                .ConfigureAwait(false);
            effectiveValues[node.Id] = applied.FromValue;
            restoredCount++;
        }

        return restoredCount;
    }

    public MenuControlProfileSnapshot GetMenuControlProfileSnapshot()
    {
        lock (_sync)
        {
            if (_menuDefinition is null
                || !_menuDefinition.Id.Equals(
                    _settings.MenuControlProfileDefinitionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new MenuControlProfileSnapshot(_menuDefinition?.Id, []);
            }

            return new MenuControlProfileSnapshot(
                _menuDefinition.Id,
                (_settings.MenuControlProfileValues ?? []).ToArray());
        }
    }

    public async Task SaveMenuControlProfileAsync(
        IReadOnlyList<MenuControlProfileValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var normalized = NormalizeMenuControlProfileValues(definition, values);

        await UpdateSettingsAsync(
                current => current with
                {
                    MenuControlProfileDefinitionId = definition.Id,
                    MenuControlProfileValues = normalized
                },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
    }

    public IReadOnlyList<MenuControlProfileValue> ValidateMenuControlProfileValues(
        IReadOnlyList<MenuControlProfileValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            var definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            return NormalizeMenuControlProfileValues(definition, values);
        }
    }

    public IReadOnlyList<SavedMenuControlStateSummary> GetSavedMenuControlStates()
    {
        lock (_sync)
        {
            if (_menuDefinition is null)
            {
                return [];
            }

            return (_settings.SavedMenuControlStates ?? [])
                .Where(state => state.DefinitionId.Equals(
                    _menuDefinition.Id,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(state => state.SavedAtUtc)
                .Select(state => new SavedMenuControlStateSummary(
                    state.Id,
                    state.Name,
                    state.SavedAtUtc,
                    state.Values.Count))
                .ToArray();
        }
    }

    public async Task<SavedMenuControlState> SaveCurrentMenuControlStateAsync(
        string name,
        IReadOnlyList<MenuControlProfileValue> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var normalizedName = name.Trim();
        if (normalizedName.Length > 80)
        {
            throw new InvalidOperationException(
                "A saved TV-state name can contain at most 80 characters.");
        }

        MenuDefinition definition;
        SavedMenuControlState? existing;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            existing = (_settings.SavedMenuControlStates ?? []).FirstOrDefault(state =>
                state.DefinitionId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                && state.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        }

        var normalized = NormalizeMenuControlProfileValues(definition, values);
        var saved = new SavedMenuControlState(
            existing?.Id ?? Guid.NewGuid().ToString("N"),
            normalizedName,
            definition.Id,
            DateTimeOffset.UtcNow,
            normalized);
        await UpdateSettingsAsync(
                current =>
                {
                    var states = (current.SavedMenuControlStates ?? [])
                        .Where(state => !state.Id.Equals(
                            saved.Id,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (states.Count(state => state.DefinitionId.Equals(
                            definition.Id,
                            StringComparison.OrdinalIgnoreCase)) >= 25)
                    {
                        throw new InvalidOperationException(
                            "A menu definition can store at most 25 named TV states. Delete an older state first.");
                    }

                    states.Add(saved);
                    return current with { SavedMenuControlStates = states };
                },
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            SeedMenuControlValuesFromState(definition, saved);
        }

        NotifyChanged();
        return saved;
    }

    public async Task<SavedMenuControlState> LoadMenuControlStateAsync(
        string stateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        MenuDefinition definition;
        SavedMenuControlState state;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            state = (_settings.SavedMenuControlStates ?? []).FirstOrDefault(candidate =>
                    candidate.Id.Equals(stateId.Trim(), StringComparison.OrdinalIgnoreCase)
                    && candidate.DefinitionId.Equals(
                        definition.Id,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException(
                    $"Saved TV state '{stateId.Trim()}' was not found for this menu definition.");
        }

        var normalized = state with
        {
            Values = NormalizeMenuControlProfileValues(definition, state.Values)
        };
        lock (_sync)
        {
            SeedMenuControlValuesFromState(definition, normalized);
        }

        NotifyChanged();
        return normalized;
    }

    public async Task DeleteMenuControlStateAsync(
        string stateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var removed = false;
        await UpdateSettingsAsync(
                current =>
                {
                    var states = (current.SavedMenuControlStates ?? []).ToList();
                    removed = states.RemoveAll(state =>
                        state.Id.Equals(stateId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && state.DefinitionId.Equals(
                            definition.Id,
                            StringComparison.OrdinalIgnoreCase)) > 0;
                    return current with { SavedMenuControlStates = states };
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!removed)
        {
            throw new KeyNotFoundException(
                $"Saved TV state '{stateId.Trim()}' was not found for this menu definition.");
        }

        NotifyChanged();
    }

    public MenuAuthoringSnapshot GetMenuAuthoringSnapshot()
    {
        lock (_sync)
        {
            var definition = _menuDefinition is null
                ? null
                : NormalizeInitialMenuTiming(_menuDefinition);
            var candidates = definition is null
                ? []
                : definition.ApplicableAnchors
                    .Where(anchor => !anchor.Verified && anchor.ReturnStrategy is null)
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
                        .Where(transition => IsAuthoringValidationCandidate(
                            definition,
                            transition))
                        .OrderBy(transition => definition.GetDepth(transition.FromNodeId))
                        .ThenBy(transition => definition.GetPath(transition.ToNodeId), StringComparer.OrdinalIgnoreCase)
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
                                definition.Timing),
                            transition.GeneratedFromTopology,
                            GetCoveredTopologyRouteCount(definition, transition))))
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
                _menuTimingValidation?.Passes
                    ?? (definition?.Timing.Verified == true
                        ? MenuTimingValidationSession.RequiredPasses
                        : 0),
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
                    current =>
                    {
                        var host = request.Host.Trim();
                        var sameControlVerificationHost = current.Host?.Equals(
                            host,
                            StringComparison.OrdinalIgnoreCase) == true;
                        return current with
                        {
                            Host = host,
                            Name = string.IsNullOrWhiteSpace(request.DisplayName)
                                ? "Samsung TV"
                                : request.DisplayName.Trim(),
                            Secure = request.Secure,
                            Port = request.Port,
                            AllowUntrustedCertificate = request.AllowUntrustedCertificate,
                            KeepAliveIntervalSeconds = request.KeepAliveIntervalSeconds,
                            KeepAliveTimeoutSeconds = request.KeepAliveTimeoutSeconds,
                            PostConnectWarmupMilliseconds = request.PostConnectWarmupMilliseconds,
                            ReconnectAfterIdleSeconds = request.ReconnectAfterIdleSeconds,
                            SliderVerificationHost = sameControlVerificationHost
                                ? current.SliderVerificationHost
                                : null,
                            VerifiedSliderNodeIds = sameControlVerificationHost
                                ? current.VerifiedSliderNodeIds
                                : [],
                            SelectionVerificationHost = sameControlVerificationHost
                                ? current.SelectionVerificationHost
                                : null,
                            VerifiedSelectionNodeIds = sameControlVerificationHost
                                ? current.VerifiedSelectionNodeIds
                                : []
                        };
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_sync)
            {
                _hasToken = _client.Token is not null;
            }

            AssumeNormalVideoAfterConnect();
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
            bool preserveMenuState;
            lock (_sync)
            {
                preserveMenuState = _menuDefinition is { } currentDefinition
                    && currentDefinition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        currentDefinition.ActiveConfigurationId,
                        configurationId,
                        StringComparison.OrdinalIgnoreCase)
                    && GetMenuDefinitionPath(_settings).Equals(
                        fullPath,
                        StringComparison.Ordinal);
            }

            await UpdateSettingsAsync(
                    current => current with
                    {
                        MenuDefinitionPath = fullPath,
                        MenuConfigurationId = configurationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var activeDefinition = MenuDefinitionVerificationReconciler.Reconcile(
                definition.WithActiveConfiguration(configurationId));
            InstallMenuDefinition(
                activeDefinition,
                preserveMenuState: preserveMenuState);
            lock (_sync)
            {
                _navigationStatus = "Menu file reloaded · topology routes and validation items regenerated";
                _menuAuthoringStatus = "Menu definition reloaded from disk";
            }

            NotifyChanged();
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

    public async Task ReloadMenuDefinitionAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string path;
        lock (_sync)
        {
            path = GetMenuDefinitionPath(_settings);
        }

        await SetMenuDefinitionAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MenuDefinitionCatalogEntry>>
        DiscoverMenuDefinitionsAsync(
            CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string activePath;
        lock (_sync)
        {
            activePath = GetMenuDefinitionPath(_settings);
        }

        var candidates = new Dictionary<string, MenuDefinitionCandidate>(
            StringComparer.OrdinalIgnoreCase);
        var userDirectory = Path.Combine(_configurationDirectory, "menu-definitions");
        var repositoryDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "menu-definitions");
        var installedDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions");

        AddMenuDefinitionDirectory(
            candidates,
            userDirectory,
            "User data",
            1);
        if (!PathsEqual(repositoryDirectory, installedDirectory))
        {
            AddMenuDefinitionDirectory(
                candidates,
                repositoryDirectory,
                "Repository",
                2);
        }

        AddMenuDefinitionDirectory(
            candidates,
            installedDirectory,
            "Installation",
            3);
        AddMenuDefinitionCandidate(candidates, activePath, "Custom file", 0);

        var discovered = new List<(MenuDefinitionCatalogEntry Entry, int Priority)>();
        var inspector = new MenuDefinitionSchemaInspector();
        foreach (var candidate in candidates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inspection = await inspector
                    .InspectFileAsync(candidate.Path, cancellationToken)
                    .ConfigureAwait(false);
                if (inspection.IsValid)
                {
                    discovered.Add((
                        new MenuDefinitionCatalogEntry(
                            candidate.Path,
                            inspection.Id!,
                            inspection.Name!,
                            inspection.Model!,
                            inspection.Context!,
                            candidate.Location,
                            PathsEqual(candidate.Path, activePath)),
                        candidate.Priority));
                }
                else
                {
                    var fileName = Path.GetFileName(candidate.Path);
                    discovered.Add((
                        new MenuDefinitionCatalogEntry(
                            candidate.Path,
                            Path.GetFileNameWithoutExtension(candidate.Path),
                            fileName,
                            "Invalid menu definition",
                            new MenuDefinitionContext(),
                            candidate.Location,
                            PathsEqual(candidate.Path, activePath),
                            IsValid: false,
                            Error: string.Join(Environment.NewLine, inspection.Diagnostics)),
                        candidate.Priority));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var fileName = Path.GetFileName(candidate.Path);
                discovered.Add((
                    new MenuDefinitionCatalogEntry(
                        candidate.Path,
                        Path.GetFileNameWithoutExtension(candidate.Path),
                        fileName,
                        "Invalid menu definition",
                        new MenuDefinitionContext(),
                        candidate.Location,
                        PathsEqual(candidate.Path, activePath),
                        IsValid: false,
                        Error: exception.Message),
                    candidate.Priority));
            }
        }

        return discovered
            .OrderByDescending(entry => entry.Entry.IsActive)
            .ThenBy(entry => entry.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Entry)
            .ToArray();
    }

    public Task<IReadOnlyList<MenuDefinitionSchemaInspection>>
        InspectMenuDefinitionSchemasAsync(
            string path,
            CancellationToken cancellationToken = default) =>
        new MenuDefinitionSchemaInspector().InspectPathAsync(path, cancellationToken);

    public async Task<IReadOnlyList<DisplayDefinitionCatalogEntry>>
        DiscoverDisplayDefinitionsAsync(
            CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var settings = GetSettings();
        var activePath = settings.DisplayDefinitionPath;
        var candidates = new Dictionary<string, DisplayDefinitionCandidate>(
            StringComparer.OrdinalIgnoreCase);
        var userDirectory = Path.Combine(_configurationDirectory, "display-definitions");
        var repositoryDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "display-definitions");
        var installedDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "display-definitions");
        AddDisplayDefinitionDirectory(candidates, userDirectory, "User data", 1);
        if (!PathsEqual(repositoryDirectory, installedDirectory))
        {
            AddDisplayDefinitionDirectory(candidates, repositoryDirectory, "Repository", 2);
        }

        AddDisplayDefinitionDirectory(candidates, installedDirectory, "Installation", 3);
        AddDisplayDefinitionCandidate(candidates, activePath, "Custom file", 0);

        var menuDefinitions = await DiscoverMenuDefinitionsAsync(cancellationToken)
            .ConfigureAwait(false);
        var discovered = new List<(DisplayDefinitionCatalogEntry Entry, int Priority)>();
        foreach (var candidate in candidates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var definition = await _displayDefinitionStore
                    .LoadAsync(candidate.Path, cancellationToken)
                    .ConfigureAwait(false);
                var resolved = await ResolveDisplayDefinitionAsync(
                        candidate.Path,
                        definition,
                        menuDefinitions,
                        cancellationToken)
                    .ConfigureAwait(false);
                var activeMenu = resolved.ActiveMenu;
                discovered.Add((
                    new DisplayDefinitionCatalogEntry(
                        candidate.Path,
                        definition.Id,
                        definition.Name,
                        definition.Connection.Host,
                        activeMenu.Reference.DefinitionId,
                        activeMenu.CatalogEntry.Name,
                        activeMenu.CatalogEntry.Path,
                        activeMenu.ConfigurationId,
                        activeMenu.Reference.Id,
                        resolved.Menus.Select(menu => new DisplayMenuReferenceSummary(
                                menu.Reference.Id,
                                menu.Reference.DefinitionId,
                                menu.CatalogEntry.Name,
                                menu.CatalogEntry.Path,
                                menu.ConfigurationId,
                                menu.Reference.Id.Equals(
                                    activeMenu.Reference.Id,
                                    StringComparison.OrdinalIgnoreCase)))
                            .ToArray(),
                        candidate.Location,
                        activePath is not null && PathsEqual(candidate.Path, activePath)),
                    candidate.Priority));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                discovered.Add((
                    new DisplayDefinitionCatalogEntry(
                        candidate.Path,
                        Path.GetFileNameWithoutExtension(
                            Path.GetFileNameWithoutExtension(candidate.Path)),
                        Path.GetFileName(candidate.Path),
                        null,
                        "Unavailable",
                        null,
                        null,
                        null,
                        null,
                        [],
                        candidate.Location,
                        activePath is not null && PathsEqual(candidate.Path, activePath),
                        IsValid: false,
                        Error: exception.Message),
                    candidate.Priority));
            }
        }

        return discovered
            .OrderByDescending(entry => entry.Entry.IsActive)
            .ThenBy(entry => entry.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Priority)
            .ThenBy(entry => entry.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Entry)
            .ToArray();
    }

    public DisplayDefinitionSavePreview PreviewDisplayDefinitionSave(
        DisplayDefinitionEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = DisplayDefinitionStore.CreateIdentifier(
            string.IsNullOrWhiteSpace(request.Id) ? request.Name : request.Id);
        var path = Path.Combine(
            _configurationDirectory,
            "display-definitions",
            $"{id}.display.json");
        var activePath = GetSettings().DisplayDefinitionPath;
        return new DisplayDefinitionSavePreview(
            id,
            path,
            File.Exists(path),
            activePath is not null && PathsEqual(path, activePath));
    }

    public async Task<string> SaveDisplayDefinitionAsync(
        DisplayDefinitionEditRequest request,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("save a display definition");
        EnsureNoMenuRecording("save a display definition");
        var preview = PreviewDisplayDefinitionSave(request);
        if (preview.FileExists && !preview.IsActiveUserDefinition && !replaceExisting)
        {
            throw new IOException(
                $"A user display definition already exists at '{preview.Path}'. Confirm replacement or choose a different display ID.");
        }

        var menuPath = Path.GetFullPath(request.MenuDefinitionPath);
        var menuInspection = await new MenuDefinitionSchemaInspector()
            .InspectFileAsync(menuPath, cancellationToken)
            .ConfigureAwait(false);
        if (!menuInspection.IsValid)
        {
            throw new InvalidOperationException(
                "The selected menu definition is invalid:" + Environment.NewLine
                + string.Join(Environment.NewLine, menuInspection.Diagnostics));
        }

        if (!menuInspection.Id!.Equals(
                request.MenuDefinitionId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The selected menu file contains definition ID '{menuInspection.Id}', not '{request.MenuDefinitionId}'.");
        }

        var menuTopology = await LoadMenuTopologyForDisplayReferenceAsync(
                menuPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.MenuConfigurationId)
            && !menuTopology.Configurations.ContainsKey(request.MenuConfigurationId))
        {
            throw new InvalidOperationException(
                $"Menu configuration '{request.MenuConfigurationId}' does not exist in '{menuTopology.Name}'.");
        }

        var source = ClassifyMenuDefinitionSource(menuPath);
        var referencePath = source switch
        {
            DisplayMenuDefinitionSource.UserData => Path.GetRelativePath(
                Path.GetDirectoryName(preview.Path)!,
                menuPath),
            DisplayMenuDefinitionSource.CustomFile => menuPath,
            _ => null
        };
        DisplayDefinitionDocument? existingDefinition = null;
        var selectedDisplayPath = GetSettings().DisplayDefinitionPath;
        if (!string.IsNullOrWhiteSpace(selectedDisplayPath)
            && File.Exists(selectedDisplayPath))
        {
            var selectedDefinition = await _displayDefinitionStore
                .LoadAsync(selectedDisplayPath, cancellationToken)
                .ConfigureAwait(false);
            if (selectedDefinition.Id.Equals(preview.Id, StringComparison.OrdinalIgnoreCase))
            {
                existingDefinition = selectedDefinition;
            }
        }

        if (existingDefinition is null && File.Exists(preview.Path))
        {
            existingDefinition = await _displayDefinitionStore
                .LoadAsync(preview.Path, cancellationToken)
                .ConfigureAwait(false);
        }

        var menus = CreateUpdatedDisplayMenuReferences(
            existingDefinition?.Menus ?? [],
            menuInspection.Id,
            source,
            referencePath,
            request.MenuConfigurationId,
            out var activeMenuReferenceId);
        var document = new DisplayDefinitionDocument
        {
            Id = preview.Id,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? preview.Id
                : request.Name.Trim(),
            Connection = new DisplayConnectionDefinition
            {
                Host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host.Trim(),
                Secure = request.Secure,
                Port = request.Port,
                AllowUntrustedCertificate = request.AllowUntrustedCertificate
            },
            Menus = menus,
            DefaultMenu = activeMenuReferenceId
        };
        await _displayDefinitionStore.SaveAsync(
                preview.Path,
                document,
                cancellationToken)
            .ConfigureAwait(false);
        await UpdateSettingsAsync(
                current => current with
                {
                    DisplayDefinitionPath = preview.Path,
                    Name = document.Name,
                    Host = document.Connection.Host ?? current.Host,
                    Secure = document.Connection.Secure,
                    Port = document.Connection.Port,
                    AllowUntrustedCertificate = document.Connection.AllowUntrustedCertificate
                },
                cancellationToken)
            .ConfigureAwait(false);
        var settings = GetSettings();
        var hasToken = settings.Host is not null
                       && await _tokenStore.LoadAsync(settings.Host, cancellationToken)
                           .ConfigureAwait(false) is not null;
        lock (_sync)
        {
            _hasToken = hasToken;
        }

        NotifyChanged();
        return preview.Path;
    }

    public async Task SetDisplayDefinitionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change the display definition");
        EnsureNoMenuRecording("change the display definition");
        if (ConnectionStatePresentation.CanDisconnect(_client.State))
        {
            throw new InvalidOperationException(
                "Disconnect from the current TV before changing display definitions.");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        var definition = await _displayDefinitionStore
            .LoadAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var menuDefinitions = await DiscoverMenuDefinitionsAsync(cancellationToken)
            .ConfigureAwait(false);
        var resolved = await ResolveDisplayDefinitionAsync(
                fullPath,
                definition,
                menuDefinitions,
                cancellationToken)
            .ConfigureAwait(false);
        await ApplyResolvedDisplayDefinitionAsync(
                fullPath,
                definition,
                resolved.ActiveMenu,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetDisplayMenuReferenceAsync(
        string displayDefinitionPath,
        string menuReferenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayDefinitionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(menuReferenceId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("change the display's menu reference");
        EnsureNoMenuRecording("change the display's menu reference");
        var fullPath = Path.GetFullPath(displayDefinitionPath.Trim());
        var activeDisplayPath = GetSettings().DisplayDefinitionPath;
        if (ConnectionStatePresentation.CanDisconnect(_client.State)
            && (string.IsNullOrWhiteSpace(activeDisplayPath)
                || !PathsEqual(activeDisplayPath, fullPath)))
        {
            throw new InvalidOperationException(
                "Disconnect from the current TV before selecting a menu belonging to another display definition.");
        }

        var definition = await _displayDefinitionStore
            .LoadAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var menuDefinitions = await DiscoverMenuDefinitionsAsync(cancellationToken)
            .ConfigureAwait(false);
        var resolved = await ResolveDisplayDefinitionAsync(
                fullPath,
                definition,
                menuDefinitions,
                cancellationToken)
            .ConfigureAwait(false);
        var menu = resolved.Menus.FirstOrDefault(item => item.Reference.Id.Equals(
                       menuReferenceId.Trim(),
                       StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"Display menu reference '{menuReferenceId.Trim()}' does not exist.");
        await ApplyResolvedDisplayDefinitionAsync(
                fullPath,
                definition,
                menu,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearDisplayDefinitionSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await UpdateSettingsAsync(
                current => current with { DisplayDefinitionPath = null },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
    }

    public async Task<string> ExportMenuStructureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("export the menu structure");
        EnsureNoMenuRecording("export the menu structure");
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A distributable menu structure must use a .yaml, .yml, or .json filename.");
        }

        var topology = CopyMenuDefinition(
            definition,
            verification: null,
            replaceVerification: true);
        await new MenuDefinitionWriter()
            .WriteFileAsync(fullPath, topology, cancellationToken)
            .ConfigureAwait(false);
        return fullPath;
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
            var topology = MenuDefinitionVerificationOverlay.CreateTopology(definition);
            var initialVerification = MenuDefinitionVerificationOverlay.CreateLegacyManifest(
                definition,
                DateTimeOffset.UtcNow);
            await new MenuDefinitionWriter()
                .WriteFileAsync(path, topology, cancellationToken)
                .ConfigureAwait(false);
            if (initialVerification is not null)
            {
                await _menuVerificationStore.SaveAsync(
                        definition.Id,
                        initialVerification,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await UpdateSettingsAsync(
                    current => current with
                    {
                        MenuDefinitionPath = path,
                        MenuConfigurationId = "default"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(MenuDefinitionVerificationOverlay.Apply(
                topology,
                initialVerification));
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
        if (!Enum.IsDefined(request.Format))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Format,
                "Choose YAML or JSON for the menu definition file.");
        }

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
                    "Default menu layout.")
            ],
            activeConfigurationId: "default");
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        var path = Path.Combine(
            _configurationDirectory,
            "menu-definitions",
            $"{definition.Id}{MenuDefinitionFileFormats.Extension(request.Format)}");
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
            configuration.Id,
            definition.Verification,
            definition.ExternalStates.Values);
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
            definition.ActiveConfigurationId,
            definition.Verification,
            definition.ExternalStates.Values);
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
                ? $"Menu topology saved to file · {plan.Preview.AddedNodeCount} added · {plan.Preview.UpdatedNodeCount} updated · {plan.Preview.RemovedNodeCount} removed"
                : "Menu topology already matches the outline · no file changes were needed";
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
                var installed = _menuDefinition ?? updated;
                var coverageRouteCount = request.Kind == MenuAuthoringItemKind.Transition
                    ? installed.ApplicableTransitions.Count(transition =>
                        transition.GeneratedFromTopology
                        && transition.TopologySeedTransitionId?.Equals(
                            request.ItemId,
                            StringComparison.OrdinalIgnoreCase) == true
                        && transition.IsValidationRoute
                        && !transition.Verified)
                    : 0;
                SetMenuValidationPasses(request.Kind, request.ItemId, 0);
                _menuValidation = coverageRouteCount > 0
                    ? null
                    : new MenuValidationSession(
                        request.Kind,
                        request.ItemId,
                        0,
                        false,
                        installed.GetPath(request.TargetNodeId));
                _menuAuthoringStatus = coverageRouteCount > 0
                    ? $"Traversal saved · topology generated {coverageRouteCount} branch coverage test(s)"
                    : $"Draft {request.Kind.ToString().ToLowerInvariant()} saved to file · ready for validation";
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
                ? "System timing saved to file · profile validation reset to 0/3"
                : "System timing saved to file";
            _menuAuthoringError = null;
        }

        if (delaysChanged)
        {
            await ClearMenuControlBehaviorVerificationAsync(cancellationToken)
                .ConfigureAwait(false);
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

        if (delaysChanged)
        {
            await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            InstallMenuDefinition(
                updated,
                preserveValidationProgress: true,
                preserveMenuState: true);
        }
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

        if (delaysChanged)
        {
            await ClearMenuControlBehaviorVerificationAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        NotifyChanged();
        await ExecuteMenuTimingProfileTestAsync(definition, session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PrepareMenuTimingProfileTestSourceAsync(
        string transitionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var transition = definition.Transitions.TryGetValue(
            transitionId.Trim(),
            out var candidate)
            ? candidate
            : throw new KeyNotFoundException(
                $"Menu transition '{transitionId.Trim()}' was not found.");
        await PrepareMenuRecordingSourceAsync(
                transition.FromNodeId,
                cancellationToken)
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
        var timingCheck = MenuDefinitionVerificationPlanner.Create(verified).Checks
            .Single(check => check.Kind == MenuVerificationCheckKind.Timing);
        await PersistVerificationEvidenceAsync(
                verified,
                [timingCheck],
                verified: true,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _menuTimingValidation = session with
            {
                Passes = MenuTimingValidationSession.RequiredPasses,
                AwaitingConfirmation = false,
                Timing = session.Timing with { Verified = true }
            };
            _menuAuthoringStatus =
                $"System timing verified · {MenuTimingValidationSession.RequiredPasses}/{MenuTimingValidationSession.RequiredPasses} passes saved to the personal verification file";
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
                ? anchor with
                {
                    Operations = belowMenuRoot.Operations,
                    Verified = belowMenuRoot.Verified,
                    ReturnStrategy = strategy
                }
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
                ? "Return-to-video scripts saved to file · changed scripts require 3/3 validation"
                : "Return-to-video scripts saved to file";
            _menuAuthoringError = null;
        }

        NotifyChanged();
    }

    public async Task DefineMenuAnchorAsync(
        string menuRootNodeId,
        IReadOnlyList<string> atMenuRootKeys,
        IReadOnlyList<string> belowMenuRootKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuRootNodeId);
        ArgumentNullException.ThrowIfNull(atMenuRootKeys);
        ArgumentNullException.ThrowIfNull(belowMenuRootKeys);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("define the menu anchor");
        EnsureNoMenuRecording("define the menu anchor");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var normalVideo = definition.Nodes.TryGetValue("normal-video", out var namedNormalVideo)
            ? namedNormalVideo
            : throw new InvalidOperationException(
                "The menu definition does not contain the required 'normal-video' starting state.");
        var normalizedRootId = menuRootNodeId.Trim();
        var menuRoot = definition.GetRequiredNode(normalizedRootId);
        if (menuRoot.ParentId?.Equals(normalVideo.Id, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(normalizedRootId)}' is not a top-level menu directly below Normal video.");
        }

        if (menuRoot.ControlType != MenuControlType.Submenu)
        {
            throw new InvalidOperationException("The menu anchor root must be a submenu.");
        }

        var normalizedAtRoot = NormalizeReturnScriptKeys(atMenuRootKeys);
        var normalizedBelowRoot = NormalizeReturnScriptKeys(belowMenuRootKeys);
        var existingAnchor = FindReturnToVideoAnchor(definition);
        var existingStrategy = existingAnchor?.ReturnStrategy;
        var sameRoot = existingStrategy?.MenuRootNodeId.Equals(
            normalizedRootId,
            StringComparison.OrdinalIgnoreCase) == true;
        var atRoot = UpdateReturnScript(
            sameRoot
                ? existingStrategy!.AtMenuRoot
                : new MenuReturnScript(CreateKeyOperations(normalizedAtRoot)),
            normalizedAtRoot);
        var belowRoot = UpdateReturnScript(
            sameRoot
                ? existingStrategy!.BelowMenuRoot
                : new MenuReturnScript(CreateKeyOperations(normalizedBelowRoot)),
            normalizedBelowRoot);
        var strategy = new MenuReturnStrategy(
            normalizedRootId,
            atRoot,
            belowRoot,
            sameRoot ? existingStrategy!.NodeOverrides : []);
        var anchor = existingAnchor is null
            ? new MenuAnchor(
                CreateMenuAnchorId(definition),
                "Return to normal video",
                normalVideo.Id,
                belowRoot.Operations,
                belowRoot.Verified,
                "Defined by the guided menu-anchor workflow.",
                strategy,
                normalizedRootId,
                definition.ActiveConfigurationId)
            : existingAnchor with
            {
                Operations = belowRoot.Operations,
                Verified = belowRoot.Verified,
                ReturnStrategy = strategy,
                ValidationSourceNodeId = normalizedRootId
            };
        var anchors = definition.Anchors.Values
            .Where(candidate => !candidate.Id.Equals(anchor.Id, StringComparison.OrdinalIgnoreCase))
            .Append(anchor)
            .ToArray();
        var transitions = !sameRoot && existingStrategy is not null
            ? definition.Transitions.Values.Where(transition =>
                    transition.GeneratedFromTopology
                    || !transition.FromNodeId.Equals(normalVideo.Id, StringComparison.OrdinalIgnoreCase)
                    || !transition.ToNodeId.Equals(
                        existingStrategy.MenuRootNodeId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : definition.Transitions.Values.ToArray();
        var updated = CopyMenuDefinition(
            definition,
            transitions: transitions,
            anchors: anchors);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        await PersistActiveMenuDefinitionAsync(updated, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _menuReturnValidation = null;
            _menuAuthoringStatus = sameRoot
                ? "Menu anchor saved · changed return scripts require 3/3 validation"
                : $"Menu anchor defined at {definition.GetPath(normalizedRootId)} · record the entry keys next";
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

    public async Task PrepareMenuReturnStrategyTestSourceAsync(
        MenuReturnScriptKind kind,
        string? deepStartNodeId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("prepare an anchor-return verification start");
        EnsureNoMenuRecording("prepare an anchor-return verification start");

        MenuDefinition definition;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
        }

        var context = GetRequiredReturnStrategyContext(definition);
        var targetNodeId = kind switch
        {
            MenuReturnScriptKind.AtMenuRoot => context.Strategy.MenuRootNodeId,
            MenuReturnScriptKind.BelowMenuRoot =>
                NormalizeDeepReturnTestNode(definition, context, deepStartNodeId),
            _ => throw new InvalidOperationException(
                "Only the base-menu and deeper-menu anchor checks have generated preparation routes.")
        };
        var route = definition.ApplicableTransitions
            .Where(transition => transition.FromNodeId.Equals(
                context.Anchor.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
            .Where(transition => transition.ToNodeId.Equals(
                targetNodeId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(transition => transition.Verified)
            .ThenBy(transition => transition.Operations.Sum(operation => operation.Repeat))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No generated route from normal video to '{definition.GetPath(targetNodeId)}' is available yet.");

        await RunNavigationAsync(
                $"Prepare anchor verification · {definition.GetPath(targetNodeId)}",
                async (_, token) =>
                {
                    MenuStateTracker tracker;
                    lock (_sync)
                    {
                        tracker = _menuStateTracker
                            ?? throw new InvalidOperationException(
                                "No menu state tracker is available.");
                    }

                    await ExecuteAuthoringOperationsAsync(
                            "Prepare anchor return line item",
                            definition.GetPath(context.Anchor.TargetNodeId),
                            definition.GetPath(targetNodeId),
                            route.Operations,
                            definition.Timing,
                            token)
                        .ConfigureAwait(false);
                    tracker.ApplyTransition(route);
                },
                clearPlanOnSuccess: true,
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
            var failedReturnCheck = MenuDefinitionVerificationPlanner.Create(unverified).Checks
                .Single(check => check.Kind == MenuVerificationCheckKind.ReturnScript
                    && GetVerificationReturnScriptKind(check) == session.Kind
                    && (session.Kind != MenuReturnScriptKind.NodeOverride
                        || check.TargetNodeId?.Equals(
                            session.StartNodeId,
                            StringComparison.OrdinalIgnoreCase) == true));
            await PersistVerificationEvidenceAsync(
                    unverified,
                    [failedReturnCheck],
                    verified: false,
                    cancellationToken)
                .ConfigureAwait(false);
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

            ConfirmMenuReturnTarget(context.Anchor.TargetNodeId, session.Kind);
            NotifyChanged();
            return;
        }

        var verified = SetReturnScriptVerified(
            definition,
            context,
            session.Kind,
            session.StartNodeId,
            verified: true);
        var returnCheck = MenuDefinitionVerificationPlanner.Create(verified).Checks
            .Single(check => check.Kind == MenuVerificationCheckKind.ReturnScript
                && GetVerificationReturnScriptKind(check) == session.Kind
                && (session.Kind != MenuReturnScriptKind.NodeOverride
                    || check.TargetNodeId?.Equals(
                        session.StartNodeId,
                        StringComparison.OrdinalIgnoreCase) == true));
        await PersistVerificationEvidenceAsync(
                verified,
                [returnCheck],
                verified: true,
                cancellationToken)
            .ConfigureAwait(false);
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
                $"Return script verified · {MenuReturnValidationSession.RequiredPasses}/{MenuReturnValidationSession.RequiredPasses} passes saved to the personal verification file";
            _menuAuthoringError = null;
        }

        ConfirmMenuReturnTarget(context.Anchor.TargetNodeId, session.Kind);
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
            _menuAuthoringStatus = "System timing and button overrides saved to file · validation restarted at 0/3";
            _menuAuthoringError = null;
        }

        if (delaysChanged)
        {
            await ClearMenuControlBehaviorVerificationAsync(cancellationToken)
                .ConfigureAwait(false);
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
            definition.ActiveConfigurationId,
            definition.Verification,
            definition.ExternalStates.Values);
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
        var coveredRouteCount = session.Kind == MenuAuthoringItemKind.Transition
            && definition.Transitions.TryGetValue(session.ItemId, out var topologyTransition)
            && topologyTransition.GeneratedFromTopology
            ? GetCoveredTopologyRouteCount(definition, topologyTransition)
            : 1;
        var verified = SetAuthoringItemVerified(definition, session.Kind, session.ItemId);
        var returnAnchor = FindReturnToVideoAnchor(verified);
        var evidenceCheck = MenuDefinitionVerificationPlanner.Create(verified).Checks
            .Single(check => check.SourceItemId?.Equals(
                    session.ItemId,
                    StringComparison.OrdinalIgnoreCase) == true
                && check.Kind == (session.Kind == MenuAuthoringItemKind.Anchor
                    ? MenuVerificationCheckKind.Anchor
                    : MenuVerificationCheckKind.Route));
        await PersistVerificationEvidenceAsync(
                verified,
                [evidenceCheck],
                verified: true,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _menuValidation = session with
            {
                Passes = MenuValidationSession.RequiredPasses,
                AwaitingConfirmation = false
            };
            _menuAuthoringStatus = coveredRouteCount > 1
                ? $"Topology branch verified · {coveredRouteCount} generated routes promoted together"
                : hasIntegratedReturn
                ? $"Verified traversal and its recorded return · passed {MenuValidationSession.RequiredPasses}/{MenuValidationSession.RequiredPasses} together and saved to personal verification"
                : $"Verified · {session.ItemId} passed {MenuValidationSession.RequiredPasses}/{MenuValidationSession.RequiredPasses} and personal verification was updated";
            _menuAuthoringError = null;
        }

        ConfirmMenuAuthoringTarget(targetNodeId, session.ItemId);
        NotifyChanged();
        if (returnAnchor?.Verified == true
            && !targetNodeId.Equals(
                returnAnchor.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RunMenuAnchorAsync(returnAnchor.Id, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ConfirmMenuAuthoringTarget(targetNodeId, session.ItemId);
                lock (_sync)
                {
                    _menuAuthoringStatus =
                        $"Verification saved · automatic return to normal video failed; current state remains {definition.GetPath(targetNodeId)}";
                }

                NotifyChanged();
                throw;
            }
        }
    }

    public NavigationPlan CreateNavigationPlan(string targetNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetNodeId);
        MenuDefinition definition;
        MenuStateTracker tracker;
        Dictionary<string, string> effectiveValues;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException(
                    _navigationError ?? "No valid menu definition is loaded.");
            tracker = _menuStateTracker
                ?? throw new InvalidOperationException(
                    _navigationError ?? "No menu state tracker is available.");
            effectiveValues = CreateEffectivePictureControlValues(
                definition,
                _menuControlValues,
                _menuExternalStateValues);
        }

        try
        {
            var target = definition.GetRequiredNode(targetNodeId);
            if (IsMenuNodeOrAncestorDisabled(definition, target, effectiveValues))
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(target.Id)}' is disabled by the current predicted menu settings.");
            }

            if (IsMenuNodeOrAncestorHidden(definition, target, effectiveValues))
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(target.Id)}' is hidden by the current predicted menu settings.");
            }

            var effectiveDefinition = CreateVisibilityAdjustedDefinition(
                definition,
                effectiveValues);
            var navigator = new MenuNavigator(
                effectiveDefinition,
                tracker,
                new WebMenuCommandTarget(_client),
                _menuDelay);
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

    public async Task ResetMenuControlsToFactoryDefaultsAsync(
        string confirmationNodeId,
        string confirmationChoice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationChoice);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("reset menu controls to factory defaults");
        EnsureNoMenuRecording("reset menu controls to factory defaults");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException("Connect to the TV before resetting menu controls.");
        }

        MenuDefinition definition;
        MenuStateTracker tracker;
        MenuNode resetNode;
        MenuAnchor returnAnchor;
        Dictionary<string, string> effectiveValues;
        string choice;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            tracker = _menuStateTracker
                ?? throw new InvalidOperationException("No menu state tracker is available.");
            resetNode = definition.GetRequiredNode(confirmationNodeId.Trim());
            if (resetNode.ControlType != MenuControlType.Confirmation)
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(resetNode.Id)}' is not a confirmation control.");
            }

            choice = (resetNode.SelectionOptions ?? []).FirstOrDefault(option => option.Equals(
                         confirmationChoice.Trim(),
                         StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(
                         $"Confirmation '{definition.GetPath(resetNode.Id)}' does not offer '{confirmationChoice.Trim()}'.");
            returnAnchor = FindReturnToVideoAnchor(definition)
                ?? throw new InvalidOperationException(
                    "A return-to-normal-video anchor is required for factory reset synchronization.");
            if (!returnAnchor.Verified)
            {
                throw new InvalidOperationException(
                    "Verify the return-to-normal-video anchor before using factory reset synchronization.");
            }

            effectiveValues = CreateEffectivePictureControlValues(
                definition,
                _menuControlValues,
                _menuExternalStateValues);
        }

        if (IsMenuNodeOrAncestorDisabled(definition, resetNode, effectiveValues))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(resetNode.Id)}' is disabled by the current predicted menu settings.");
        }

        if (IsMenuNodeOrAncestorHidden(definition, resetNode, effectiveValues))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(resetNode.Id)}' is hidden by the current predicted menu settings.");
        }

        var operations = CreateConfirmationOperations(resetNode, choice);
        await RunNavigationAsync(
                $"Factory reset · {definition.GetPath(resetNode.Id)}",
                async (navigator, token) =>
                {
                    await PreparePictureControlAsync(
                            definition,
                            tracker,
                            resetNode,
                            effectiveValues,
                            token)
                        .ConfigureAwait(false);
                    await ExecutePictureControlOperationsAsync(
                            $"Confirm {choice} · {definition.GetPath(resetNode.Id)}",
                            definition.GetPath(resetNode.Id),
                            definition.GetPath(resetNode.Id),
                            operations,
                            definition.Timing,
                            token)
                        .ConfigureAwait(false);
                    tracker.ConfirmNode(
                        resetNode.Id,
                        $"Confirmed '{choice}' for '{definition.GetPath(resetNode.Id)}'.");
                    await navigator.ExecuteAnchorAsync(returnAnchor.Id, token)
                        .ConfigureAwait(false);
                    lock (_sync)
                    {
                        ResetMenuControlValues(definition);
                    }
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ApplyMenuSliderValuesAsync(
        IReadOnlyList<MenuSliderValueUpdate> updates,
        bool returnToNormalVideo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var normalized = updates.Select(update => new MenuControlValueUpdate(
                update.NodeId,
                update.FromValue.ToString("G29", CultureInfo.InvariantCulture),
                update.ToValue.ToString("G29", CultureInfo.InvariantCulture)))
            .ToArray();
        var knownValues = normalized.GroupBy(
                update => update.NodeId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().FromValue,
                StringComparer.OrdinalIgnoreCase);
        return ApplyMenuControlValuesAsync(
            normalized,
            knownValues,
            returnToNormalVideo,
            cancellationToken);
    }

    public async Task ApplyMenuControlValuesAsync(
        IReadOnlyList<MenuControlValueUpdate> updates,
        IReadOnlyDictionary<string, string> knownValues,
        bool returnToNormalVideo,
        CancellationToken cancellationToken = default,
        Action<MenuControlValueUpdate>? valueApplied = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(knownValues);
        if (updates.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one changed menu control to apply.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("apply menu control values");
        EnsureNoMenuRecording("apply menu control values");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException("Connect to the TV before applying menu controls.");
        }

        MenuDefinition definition;
        MenuStateTracker tracker;
        MenuAnchor? returnAnchor;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            tracker = _menuStateTracker
                ?? throw new InvalidOperationException("No menu state tracker is available.");
            returnAnchor = returnToNormalVideo
                ? FindReturnToVideoAnchor(definition)
                : null;
        }

        var normalized = updates.Select(update => ValidatePictureControlUpdate(definition, update))
            .ToArray();
        if (normalized.Select(update => update.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Length)
        {
            throw new InvalidOperationException(
                "Each menu control can appear only once in an apply operation.");
        }

        normalized = OrderPictureControlUpdates(definition, normalized);
        var effectiveValues = CreateEffectivePictureControlValues(
            definition,
            knownValues,
            _menuExternalStateValues);
        foreach (var update in normalized)
        {
            if (effectiveValues.TryGetValue(update.NodeId, out var knownValue)
                && !knownValue.Equals(update.FromValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The predicted value for '{definition.GetPath(update.NodeId)}' changed from '{update.FromValue}' to '{knownValue}'. Refresh the menu controls before applying.");
            }

            effectiveValues[update.NodeId] = update.FromValue;
        }

        if (returnToNormalVideo && returnAnchor?.Verified != true)
        {
            throw new InvalidOperationException(
                "A verified return-to-normal-video anchor is required for the selected exit behavior.");
        }

        var lastTargetNodeId = normalized[^1].NodeId;
        await RunNavigationAsync(
                $"Apply {normalized.Length} menu control{(normalized.Length == 1 ? string.Empty : "s")}",
                async (navigator, token) =>
                {
                    foreach (var update in normalized)
                    {
                        var node = definition.GetRequiredNode(update.NodeId);
                        if (IsMenuNodeOrAncestorDisabled(definition, node, effectiveValues))
                        {
                            throw new InvalidOperationException(
                                $"'{definition.GetPath(node.Id)}' is disabled by the current predicted menu settings.");
                        }

                        if (IsMenuNodeOrAncestorHidden(definition, node, effectiveValues))
                        {
                            throw new InvalidOperationException(
                                $"'{definition.GetPath(node.Id)}' is hidden by the current predicted menu settings.");
                        }

                        await PreparePictureControlAsync(
                                definition,
                                tracker,
                                node,
                                effectiveValues,
                                token)
                            .ConfigureAwait(false);
                        try
                        {
                            await ExecutePictureControlValueChangeAsync(
                                    definition,
                                    update,
                                    token)
                                .ConfigureAwait(false);
                            effectiveValues[update.NodeId] = update.ToValue;
                            lock (_sync)
                            {
                                _menuControlValues[update.NodeId] = update.ToValue;
                            }
                            tracker.ConfirmNode(
                                update.NodeId,
                                $"Menu control '{definition.GetPath(update.NodeId)}' was adjusted to the predicted value '{update.ToValue}'.");
                            valueApplied?.Invoke(update);
                        }
                        catch
                        {
                            tracker.MarkUnknown(
                                $"Menu control adjustment for '{definition.GetPath(update.NodeId)}' did not complete.");
                            throw;
                        }
                    }

                    if (returnAnchor is not null)
                    {
                        try
                        {
                            await navigator.ExecuteAnchorAsync(returnAnchor.Id, token)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            tracker.ConfirmNode(
                                lastTargetNodeId,
                                "Menu control values were applied, but the requested return to normal video failed; the last adjusted control remains the expected state.");
                            throw;
                        }
                    }
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ApplyIndexedMenuControlValuesAsync(
        IReadOnlyList<MenuIndexedControlValueUpdate> updates,
        IReadOnlyDictionary<string, string> knownValues,
        bool returnToNormalVideo,
        CancellationToken cancellationToken = default,
        Action<MenuIndexedControlValueUpdate>? valueApplied = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(knownValues);
        if (updates.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one indexed menu value to apply.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        EnsureNoAutomationRunning("apply indexed menu control values");
        EnsureNoMenuRecording("apply indexed menu control values");
        if (_client.State != SamsungConnectionState.Connected)
        {
            throw new InvalidOperationException("Connect to the TV before applying indexed menu controls.");
        }

        MenuDefinition definition;
        MenuStateTracker tracker;
        MenuAnchor? returnAnchor;
        lock (_sync)
        {
            definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            tracker = _menuStateTracker
                ?? throw new InvalidOperationException("No menu state tracker is available.");
            returnAnchor = returnToNormalVideo
                ? FindReturnToVideoAnchor(definition)
                : null;
        }

        var normalized = updates.Select(update => ValidateIndexedControlUpdate(
                definition,
                update))
            .ToArray();
        if (normalized.Select(update =>
                $"{update.SelectorNodeId}\u001f{update.SelectorValue}\u001f{update.NodeId}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != normalized.Length)
        {
            throw new InvalidOperationException(
                "Each indexed slider can appear only once for each selector value in an apply operation.");
        }

        if (returnToNormalVideo && returnAnchor?.Verified != true)
        {
            throw new InvalidOperationException(
                "A verified return-to-normal-video anchor is required for the selected exit behavior.");
        }

        var effectiveValues = CreateEffectivePictureControlValues(
            definition,
            knownValues,
            _menuExternalStateValues);
        var orderedGroups = normalized
            .GroupBy(update => update.SelectorNodeId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selector = definition.GetRequiredNode(group.Key);
                var optionOrder = (selector.SelectionOptions ?? [])
                    .Select((option, index) => (option, index))
                    .ToDictionary(item => item.option, item => item.index, StringComparer.OrdinalIgnoreCase);
                return new
                {
                    Selector = selector,
                    Rows = group.GroupBy(update => update.SelectorValue, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(row => optionOrder[row.Key])
                        .ToArray()
                };
            })
            .ToArray();
        var lastTargetNodeId = normalized[^1].NodeId;
        await RunNavigationAsync(
                $"Apply {normalized.Length} indexed menu value{(normalized.Length == 1 ? string.Empty : "s")}",
                async (navigator, token) =>
                {
                    foreach (var group in orderedGroups)
                    {
                        var selector = group.Selector;
                        if (IsMenuNodeOrAncestorDisabled(definition, selector, effectiveValues)
                            || IsMenuNodeOrAncestorHidden(definition, selector, effectiveValues))
                        {
                            throw new InvalidOperationException(
                                $"Indexed selector '{definition.GetPath(selector.Id)}' is unavailable under the current predicted settings.");
                        }

                        var currentSelectorValue = effectiveValues[selector.Id];
                        foreach (var row in group.Rows)
                        {
                            if (!currentSelectorValue.Equals(row.Key, StringComparison.OrdinalIgnoreCase))
                            {
                                await PreparePictureControlAsync(
                                        definition,
                                        tracker,
                                        selector,
                                        effectiveValues,
                                        token)
                                    .ConfigureAwait(false);
                                var selectorUpdate = new MenuControlValueUpdate(
                                    selector.Id,
                                    currentSelectorValue,
                                    row.Key);
                                await ExecutePictureControlValueChangeAsync(
                                        definition,
                                        selectorUpdate,
                                        token)
                                    .ConfigureAwait(false);
                                currentSelectorValue = row.Key;
                                effectiveValues[selector.Id] = row.Key;
                                lock (_sync)
                                {
                                    _menuControlValues[selector.Id] = row.Key;
                                }

                                tracker.ConfirmNode(
                                    selector.Id,
                                    $"Indexed selector '{definition.GetPath(selector.Id)}' was set to '{row.Key}'.");
                            }

                            foreach (var update in row)
                            {
                                var node = definition.GetRequiredNode(update.NodeId);
                                effectiveValues[node.Id] = update.FromValue;
                                if (IsMenuNodeOrAncestorDisabled(definition, node, effectiveValues)
                                    || IsMenuNodeOrAncestorHidden(definition, node, effectiveValues))
                                {
                                    throw new InvalidOperationException(
                                        $"Indexed control '{definition.GetPath(node.Id)}' is unavailable under the current predicted settings.");
                                }

                                await PreparePictureControlAsync(
                                        definition,
                                        tracker,
                                        node,
                                        effectiveValues,
                                        token)
                                    .ConfigureAwait(false);
                                await ExecutePictureControlValueChangeAsync(
                                        definition,
                                        new MenuControlValueUpdate(
                                            node.Id,
                                            update.FromValue,
                                            update.ToValue),
                                        token)
                                    .ConfigureAwait(false);
                                effectiveValues[node.Id] = update.ToValue;
                                lock (_sync)
                                {
                                    _menuControlValues[node.Id] = update.ToValue;
                                }

                                tracker.ConfirmNode(
                                    node.Id,
                                    $"Indexed value '{row.Key} / {node.Label}' was adjusted to '{update.ToValue}'.");
                                valueApplied?.Invoke(update);
                                lastTargetNodeId = node.Id;
                            }
                        }
                    }

                    if (returnAnchor is not null)
                    {
                        try
                        {
                            await navigator.ExecuteAnchorAsync(returnAnchor.Id, token)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            tracker.ConfirmNode(
                                lastTargetNodeId,
                                "Indexed values were applied, but the requested return to normal video failed; the last adjusted control remains the expected state.");
                            throw;
                        }
                    }
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MenuControlVerificationSnapshot> ConfirmMenuSliderBehaviorAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalizedNodeId;
        lock (_sync)
        {
            var definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            var node = definition.GetRequiredNode(nodeId.Trim());
            if (node.ControlType != MenuControlType.Slider)
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(node.Id)}' is not defined as a slider.");
            }

            normalizedNodeId = node.Id;
        }

        await UpdateSettingsAsync(
                current =>
                {
                    var ids = current.Host?.Equals(
                            current.SliderVerificationHost,
                            StringComparison.OrdinalIgnoreCase) == true
                        ? (current.VerifiedSliderNodeIds ?? []).ToList()
                        : [];
                    if (!ids.Contains(normalizedNodeId, StringComparer.OrdinalIgnoreCase))
                    {
                        ids.Add(normalizedNodeId);
                    }

                    return current with
                    {
                        SliderVerificationHost = current.Host,
                        VerifiedSliderNodeIds = ids
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
        return GetMenuControlVerificationSnapshot();
    }

    public async Task ResetMenuSliderBehaviorVerificationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ClearMenuSliderBehaviorVerificationAsync(cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    public async Task<MenuControlVerificationSnapshot> ConfirmMenuSelectionBehaviorAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalizedNodeId;
        lock (_sync)
        {
            var definition = _menuDefinition
                ?? throw new InvalidOperationException("No menu definition is loaded.");
            var node = definition.GetRequiredNode(nodeId.Trim());
            if (node.ControlType is not MenuControlType.Selection
                and not MenuControlType.SubmenuSelection
                and not MenuControlType.IndexedSelection)
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(node.Id)}' is not defined as a selection control.");
            }

            normalizedNodeId = node.Id;
        }

        await UpdateSettingsAsync(
                current =>
                {
                    var ids = current.Host?.Equals(
                            current.SelectionVerificationHost,
                            StringComparison.OrdinalIgnoreCase) == true
                        ? (current.VerifiedSelectionNodeIds ?? []).ToList()
                        : [];
                    if (!ids.Contains(normalizedNodeId, StringComparer.OrdinalIgnoreCase))
                    {
                        ids.Add(normalizedNodeId);
                    }

                    return current with
                    {
                        SelectionVerificationHost = current.Host,
                        VerifiedSelectionNodeIds = ids
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
        return GetMenuControlVerificationSnapshot();
    }

    public async Task ResetMenuSelectionBehaviorVerificationAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ClearMenuSelectionBehaviorVerificationAsync(cancellationToken)
            .ConfigureAwait(false);
        NotifyChanged();
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

    private void AssumeNormalVideoAfterConnect()
    {
        MenuStateTracker? tracker;
        string? normalVideoNodeId;
        lock (_sync)
        {
            tracker = _menuStateTracker;
            normalVideoNodeId = _menuDefinition?.Nodes.Keys.FirstOrDefault(nodeId =>
                nodeId.Equals("normal-video", StringComparison.OrdinalIgnoreCase));
            _navigationStatus = normalVideoNodeId is null
                ? "Connected · the menu definition has no normal-video state"
                : "Connected · Normal video assumed · no menu commands sent";
            _navigationError = null;
        }

        if (tracker is null || normalVideoNodeId is null)
        {
            tracker?.MarkUnknown(
                "The TV connected, but the active menu definition has no normal-video state to assume.");
            NotifyChanged();
            return;
        }

        tracker.AssumeNode(
            normalVideoNodeId,
            "Normal video is assumed when the TV first connects; no menu commands were sent.");
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

    private static MenuNode SelectMenuDefinitionVerificationControl(
        MenuDefinition definition,
        MenuDefinitionVerificationCheck check,
        IReadOnlyDictionary<string, string> effectiveValues,
        IReadOnlyList<string> previouslyConfirmedNodeIds)
    {
        var plannedTarget = definition.GetRequiredNode(check.TargetNodeId!);
        var expectedType = MenuControlBehaviorClassifier.GetEffectiveControlType(plannedTarget);
        var effectiveDefinition = CreateVisibilityAdjustedDefinition(
            definition,
            effectiveValues);
        var confirmed = previouslyConfirmedNodeIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var candidates = definition.Nodes.Values
            .Where(node => check.Kind switch
            {
                MenuVerificationCheckKind.SliderBehavior =>
                    node.ControlType == MenuControlType.Slider,
                MenuVerificationCheckKind.Selection =>
                    MenuControlBehaviorClassifier.GetEffectiveControlType(node) == expectedType,
                MenuVerificationCheckKind.Switch =>
                    node.ControlType == MenuControlType.Switch,
                MenuVerificationCheckKind.Confirmation =>
                    node.ControlType == MenuControlType.Confirmation,
                _ => false
            })
            .Where(node => !IsMenuNodePermanentlyDisabled(definition, node)
                && (check.Kind != MenuVerificationCheckKind.Confirmation
                    || (node.SelectionOptions ?? []).Contains(
                        "Cancel",
                        StringComparer.OrdinalIgnoreCase)))
            .Where(node => HasVerifiedPictureControlRoute(effectiveDefinition, node.Id)
                || HasConditionalAncestorWithVerifiedParent(definition, node))
            .Where(node => CanAutomaticallyMakeMenuNodeAvailable(
                definition,
                node,
                effectiveValues))
            .OrderBy(node => confirmed.Contains(node.Id))
            .ThenBy(node => !node.Id.Equals(
                plannedTarget.Id,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(node => definition.GetDepth(node.Id))
            .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No currently available, verified {check.Label.ToLowerInvariant()} representative can be adjusted automatically.");
    }

    private async Task<MenuDefinitionVerificationTestResult>
        RunAdjustableMenuDefinitionVerificationTestAsync(
            MenuDefinition definition,
            MenuDefinitionVerificationCheck check,
            MenuNode node,
            Dictionary<string, string> effectiveValues,
            CancellationToken cancellationToken)
    {
        if (!effectiveValues.TryGetValue(node.Id, out var currentValue))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(node.Id)}' does not have a predicted value for automated verification.");
        }

        var nextValue = SelectAlternateMenuControlValue(node, currentValue);
        var appliedUpdates = await ApplyMenuDefinitionVerificationValueAsync(
                definition,
                node,
                currentValue,
                nextValue,
                effectiveValues,
                cancellationToken)
            .ConfigureAwait(false);
        return new MenuDefinitionVerificationTestResult(
            check.Id,
            node.Id,
            definition.GetPath(node.Id),
            MenuControlBehaviorClassifier.GetEffectiveControlType(node),
            $"Changed {node.Label} from {currentValue} to {nextValue}. Confirm the displayed value and interaction before counting the pass; the prior value will be restored before the menu exits.",
            appliedUpdates);
    }

    private async Task<MenuDefinitionVerificationTestResult>
        RunConfirmationMenuDefinitionVerificationTestAsync(
            MenuDefinition definition,
            MenuDefinitionVerificationCheck check,
            MenuNode node,
            IReadOnlyDictionary<string, string> effectiveValues,
            CancellationToken cancellationToken)
    {
        var safeChoice = (node.SelectionOptions ?? []).FirstOrDefault(option =>
            option.Equals("Cancel", StringComparison.OrdinalIgnoreCase));
        if (safeChoice is null)
        {
            throw new InvalidOperationException(
                $"Confirmation '{definition.GetPath(node.Id)}' does not define a Cancel choice, so it cannot be tested automatically without risking an action.");
        }

        MenuStateTracker tracker;
        lock (_sync)
        {
            tracker = _menuStateTracker
                ?? throw new InvalidOperationException("No menu state tracker is available.");
        }

        var operations = CreateConfirmationOperations(node, safeChoice);
        await RunNavigationAsync(
                $"Verify confirmation · {definition.GetPath(node.Id)}",
                async (_, token) =>
                {
                    await PreparePictureControlAsync(
                            definition,
                            tracker,
                            node,
                            effectiveValues,
                            token)
                        .ConfigureAwait(false);
                    await ExecutePictureControlOperationsAsync(
                            $"Choose safe cancel · {definition.GetPath(node.Id)}",
                            definition.GetPath(node.Id),
                            definition.GetPath(node.Id),
                            operations,
                            definition.Timing,
                            token)
                        .ConfigureAwait(false);
                    tracker.ConfirmNode(
                        node.Id,
                        $"Confirmation '{definition.GetPath(node.Id)}' opened and used its safe Cancel path.");
                },
                clearPlanOnSuccess: true,
                cancellationToken)
            .ConfigureAwait(false);
        return new MenuDefinitionVerificationTestResult(
            check.Id,
            node.Id,
            definition.GetPath(node.Id),
            MenuControlType.Confirmation,
            $"Opened {node.Label} and selected Cancel. Confirm that the dialog opened and closed without applying the action.",
            []);
    }

    private async Task<MenuDefinitionVerificationTestResult>
        RunConditionalMenuDefinitionVerificationTestAsync(
            MenuDefinition definition,
            MenuDefinitionVerificationCheck check,
            Dictionary<string, string> effectiveValues,
            CancellationToken cancellationToken)
    {
        if (check.Id.Equals(
                "condition:always-disabled-behavior",
                StringComparison.OrdinalIgnoreCase))
        {
            var affected = definition.Nodes.Values
                .Where(node => node.Disabled)
                .OrderBy(node => !string.Equals(
                    node.ParentId,
                    check.TargetNodeId,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(node => definition.GetDepth(node.Id))
                .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No permanently disabled representative remains in this verification group.");
            var viewNodeId = affected.ParentId ?? check.TargetNodeId!;
            await NavigateToMenuNodeAsync(viewNodeId, cancellationToken).ConfigureAwait(false);
            return new MenuDefinitionVerificationTestResult(
                check.Id,
                affected.Id,
                definition.GetPath(affected.Id),
                null,
                $"Opened {definition.GetPath(viewNodeId)}. Confirm that {affected.Label} remains visible, gray, and unavailable.",
                []);
        }

        if (!string.IsNullOrWhiteSpace(check.ExternalStateId)
            && !string.IsNullOrWhiteSpace(check.ExternalStateValue))
        {
            var state = definition.ExternalStates.TryGetValue(
                    check.ExternalStateId,
                    out var externalState)
                ? externalState
                : throw new InvalidOperationException(
                    $"External state '{check.ExternalStateId}' is no longer defined.");
            var selectedValue = GetConditionValue(
                definition,
                state.Id,
                MenuConditionSourceKind.ExternalState,
                effectiveValues);
            if (!check.ExternalStateValue.Equals(
                    selectedValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Set the external equipment to {state.Label} = {check.ExternalStateValue}, then select that same value in the persistent app header before running this test.");
            }

            var externalHidden = check.Id.Equals(
                "condition:external-hidden-behavior",
                StringComparison.OrdinalIgnoreCase);
            var affected = definition.Nodes.Values
                .Where(node => externalHidden
                    ? (node.HiddenWhen ?? []).Any(condition =>
                        condition.SourceKind == MenuConditionSourceKind.ExternalState
                        && condition.SourceId.Equals(state.Id, StringComparison.OrdinalIgnoreCase)
                        && condition.EqualsValue.Equals(
                            check.ExternalStateValue,
                            StringComparison.OrdinalIgnoreCase))
                    : (node.DisabledWhen ?? []).Any(condition =>
                        condition.SourceKind == MenuConditionSourceKind.ExternalState
                        && condition.SourceId.Equals(state.Id, StringComparison.OrdinalIgnoreCase)
                        && condition.EqualsValue.Equals(
                            check.ExternalStateValue,
                            StringComparison.OrdinalIgnoreCase)))
                .OrderBy(node => definition.GetDepth(node.Id))
                .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"No representative rule controlled by '{state.Label}' remains in this verification group.");
            var viewNodeId = affected.ParentId ?? affected.Id;
            await NavigateToMenuNodeAsync(viewNodeId, cancellationToken).ConfigureAwait(false);
            var externalExpectedBehavior = externalHidden
                ? "absent"
                : "visible but gray and unavailable";
            return new MenuDefinitionVerificationTestResult(
                check.Id,
                affected.Id,
                definition.GetPath(affected.Id),
                null,
                $"Opened {definition.GetPath(viewNodeId)} with {state.Label} = {selectedValue}. Confirm that {affected.Label} is {externalExpectedBehavior}.",
                []);
        }

        var hidden = check.Id.Equals(
            "condition:hidden-behavior",
            StringComparison.OrdinalIgnoreCase);
        var affectedConditions = definition.Nodes.Values
            .SelectMany(node => hidden
                ? (node.HiddenWhen ?? []).Select(condition => (
                    Node: node,
                    condition.SourceId,
                    condition.SourceKind,
                    condition.EqualsValue))
                : (node.DisabledWhen ?? []).Select(condition => (
                    Node: node,
                    condition.SourceId,
                    condition.SourceKind,
                    condition.EqualsValue)))
            .Where(item => item.SourceKind == MenuConditionSourceKind.MenuSetting
                && item.SourceId.Equals(
                check.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => definition.GetDepth(item.Node.Id))
            .ThenBy(item => item.Node.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var affectedCondition = affectedConditions.FirstOrDefault();
        if (affectedCondition.Node is null)
        {
            throw new InvalidOperationException(
                $"No representative rule controlled by '{check.TargetNodeId}' remains in this verification group.");
        }

        var controller = definition.GetRequiredNode(affectedCondition.SourceId);
        if (!effectiveValues.TryGetValue(controller.Id, out var currentValue))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(controller.Id)}' does not have a predicted value for automated verification.");
        }

        var expectedValue = affectedCondition.EqualsValue;
        var appliedUpdates = new List<MenuControlValueUpdate>();
        if (currentValue.Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            var comparisonValue = SelectAlternateMenuControlValue(controller, currentValue);
            appliedUpdates.AddRange(await ApplyMenuDefinitionVerificationValueAsync(
                    definition,
                    controller,
                    currentValue,
                    comparisonValue,
                    effectiveValues,
                    cancellationToken)
                .ConfigureAwait(false));
            currentValue = comparisonValue;
        }

        appliedUpdates.AddRange(await ApplyMenuDefinitionVerificationValueAsync(
                definition,
                controller,
                currentValue,
                expectedValue,
                effectiveValues,
                cancellationToken)
            .ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(affectedCondition.Node.ParentId))
        {
            await NavigateToMenuNodeAsync(
                    affectedCondition.Node.ParentId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var expectedBehavior = hidden ? "absent" : "visible but gray and unavailable";
        return new MenuDefinitionVerificationTestResult(
            check.Id,
            affectedCondition.Node.Id,
            definition.GetPath(affectedCondition.Node.Id),
            null,
            $"Set {controller.Label} to {expectedValue}. Confirm that {affectedCondition.Node.Label} is {expectedBehavior}; prior values will be restored before the menu exits.",
            CollapseMenuControlUpdates(appliedUpdates));
    }

    private async Task<IReadOnlyList<MenuControlValueUpdate>> ApplyMenuDefinitionVerificationValueAsync(
        MenuDefinition definition,
        MenuNode node,
        string currentValue,
        string nextValue,
        Dictionary<string, string> effectiveValues,
        CancellationToken cancellationToken)
    {
        var updates = CreateMenuAvailabilityUpdates(
                definition,
                node,
                effectiveValues)
            .Append(new MenuControlValueUpdate(node.Id, currentValue, nextValue))
            .ToArray();
        await ApplyMenuControlValuesAsync(
                updates,
                effectiveValues,
                returnToNormalVideo: false,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var update in updates)
        {
            effectiveValues[update.NodeId] = update.ToValue;
        }

        return updates;
    }

    private static IReadOnlyList<MenuControlValueUpdate> CollapseMenuControlUpdates(
        IEnumerable<MenuControlValueUpdate> updates)
    {
        var collapsed = new List<MenuControlValueUpdate>();
        foreach (var update in updates)
        {
            var existingIndex = collapsed.FindIndex(candidate => candidate.NodeId.Equals(
                update.NodeId,
                StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                collapsed.Add(update);
                continue;
            }

            collapsed[existingIndex] = collapsed[existingIndex] with
            {
                ToValue = update.ToValue
            };
        }

        return collapsed
            .Where(update => !update.FromValue.Equals(
                update.ToValue,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static bool CanAutomaticallyMakeMenuNodeAvailable(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        try
        {
            CreateMenuAvailabilityUpdates(definition, node, effectiveValues);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<MenuControlValueUpdate> CreateMenuAvailabilityUpdates(
        MenuDefinition definition,
        MenuNode target,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        var projectedValues = new Dictionary<string, string>(
            effectiveValues,
            StringComparer.OrdinalIgnoreCase);
        var updates = new List<MenuControlValueUpdate>();
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void MakeAvailable(MenuNode node)
        {
            if (completed.Contains(node.Id))
            {
                return;
            }

            if (!visiting.Add(node.Id))
            {
                throw new InvalidOperationException(
                    $"Conditional availability cycle encountered at '{definition.GetPath(node.Id)}'.");
            }

            if (!string.IsNullOrWhiteSpace(node.ParentId))
            {
                MakeAvailable(definition.GetRequiredNode(node.ParentId));
            }

            if (node.Disabled)
            {
                throw new InvalidOperationException(
                    $"'{definition.GetPath(node.Id)}' is permanently disabled.");
            }

            var conditions = (node.DisabledWhen ?? [])
                .Select(condition => (
                    condition.SourceId,
                    condition.SourceKind,
                    condition.EqualsValue))
                .Concat((node.HiddenWhen ?? []).Select(condition => (
                    condition.SourceId,
                    condition.SourceKind,
                    condition.EqualsValue)))
                .GroupBy(
                    condition => (condition.SourceKind, condition.SourceId));
            foreach (var group in conditions)
            {
                var conditionKey = group.Key.SourceKind == MenuConditionSourceKind.ExternalState
                    ? ExternalStateValueKey(group.Key.SourceId)
                    : group.Key.SourceId;
                if (!projectedValues.TryGetValue(conditionKey, out var controllerValue))
                {
                    throw new InvalidOperationException(
                        $"Conditional source '{group.Key.SourceId}' does not have a predicted value.");
                }

                var blockedValues = group
                    .Select(condition => condition.EqualsValue)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!blockedValues.Contains(controllerValue))
                {
                    continue;
                }

                if (group.Key.SourceKind == MenuConditionSourceKind.ExternalState)
                {
                    var state = definition.ExternalStates[group.Key.SourceId];
                    throw new InvalidOperationException(
                        $"'{definition.GetPath(node.Id)}' is unavailable while {state.Label} is {controllerValue}. Change the external equipment, then choose its matching state in the app header.");
                }

                var controller = definition.GetRequiredNode(group.Key.SourceId);
                MakeAvailable(controller);
                controllerValue = projectedValues[controller.Id];
                if (!blockedValues.Contains(controllerValue))
                {
                    continue;
                }

                var availableValue = SelectMenuControlValueOutside(
                    controller,
                    controllerValue,
                    blockedValues);
                var existingIndex = updates.FindIndex(update => update.NodeId.Equals(
                    controller.Id,
                    StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    updates[existingIndex] = updates[existingIndex] with
                    {
                        ToValue = availableValue
                    };
                }
                else
                {
                    updates.Add(new MenuControlValueUpdate(
                        controller.Id,
                        controllerValue,
                        availableValue));
                }

                projectedValues[controller.Id] = availableValue;
            }

            visiting.Remove(node.Id);
            completed.Add(node.Id);
        }

        MakeAvailable(target);
        if (IsMenuNodeOrAncestorDisabled(definition, target, projectedValues)
            || IsMenuNodeOrAncestorHidden(definition, target, projectedValues))
        {
            throw new InvalidOperationException(
                $"No automatic setting combination makes '{definition.GetPath(target.Id)}' available.");
        }

        return updates;
    }

    private static string SelectMenuControlValueOutside(
        MenuNode node,
        string currentValue,
        IReadOnlySet<string> excludedValues)
    {
        IEnumerable<string> candidates = node.ControlType switch
        {
            MenuControlType.Switch => ["off", "on"],
            MenuControlType.Selection
                or MenuControlType.SubmenuSelection
                or MenuControlType.IndexedSelection => node.SelectionOptions ?? [],
            MenuControlType.Slider => CreateAlternateSliderValues(node, currentValue),
            _ => []
        };
        return candidates.FirstOrDefault(candidate =>
                   !candidate.Equals(currentValue, StringComparison.OrdinalIgnoreCase)
                   && !excludedValues.Contains(candidate))
               ?? throw new InvalidOperationException(
                   $"'{node.Label}' has no declared value that makes the dependent menu item available.");
    }

    private static IEnumerable<string> CreateAlternateSliderValues(
        MenuNode node,
        string currentValue)
    {
        var current = decimal.Parse(currentValue, CultureInfo.InvariantCulture);
        if (node.MinimumValue is { } minimum)
        {
            yield return minimum.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (node.MaximumValue is { } maximum)
        {
            yield return maximum.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (node.MaximumValue is null || current + 1 <= node.MaximumValue)
        {
            yield return (current + 1).ToString("G29", CultureInfo.InvariantCulture);
        }

        if (node.MinimumValue is null || current - 1 >= node.MinimumValue)
        {
            yield return (current - 1).ToString("G29", CultureInfo.InvariantCulture);
        }
    }

    private static string SelectAlternateMenuControlValue(
        MenuNode node,
        string currentValue)
    {
        switch (node.ControlType)
        {
            case MenuControlType.Slider:
                var current = decimal.Parse(currentValue, CultureInfo.InvariantCulture);
                if (node.MaximumValue is { } maximum && current + 1 <= maximum)
                {
                    return (current + 1).ToString("G29", CultureInfo.InvariantCulture);
                }

                if (node.MinimumValue is { } minimum && current - 1 >= minimum)
                {
                    return (current - 1).ToString("G29", CultureInfo.InvariantCulture);
                }

                throw new InvalidOperationException(
                    $"Slider '{node.Label}' has no adjacent value available for an automated test.");

            case MenuControlType.Switch:
                return currentValue.Equals("on", StringComparison.OrdinalIgnoreCase)
                    ? "off"
                    : "on";

            case MenuControlType.Selection:
            case MenuControlType.SubmenuSelection:
            case MenuControlType.IndexedSelection:
                var options = node.SelectionOptions ?? [];
                var currentIndex = options.ToList().FindIndex(option => option.Equals(
                    currentValue,
                    StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0 || options.Count < 2)
                {
                    throw new InvalidOperationException(
                        $"Selection '{node.Label}' needs at least two choices for an automated test.");
                }

                return currentIndex + 1 < options.Count
                    ? options[currentIndex + 1]
                    : options[currentIndex - 1];

            default:
                throw new InvalidOperationException(
                    $"'{node.Label}' is not an automatically adjustable verification control.");
        }
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

    private static bool RequiresExistingVerificationEvidence(MenuVerificationCheckKind kind) =>
        kind is MenuVerificationCheckKind.Timing
            or MenuVerificationCheckKind.Anchor
            or MenuVerificationCheckKind.ReturnScript
            or MenuVerificationCheckKind.Route;

    private static bool HasExistingVerificationEvidence(
        MenuDefinition definition,
        MenuDefinitionVerificationCheck check,
        MenuControlVerificationSnapshot controlVerification) => check.Kind switch
        {
            MenuVerificationCheckKind.SliderBehavior => controlVerification.SlidersVerified,
            MenuVerificationCheckKind.Selection => check.TargetNodeId is { } nodeId
                && controlVerification.VerifiedSelectionControlTypes.Contains(
                    MenuControlBehaviorClassifier.GetEffectiveControlType(
                        definition.GetRequiredNode(nodeId))),
            _ => check.ExistingEvidenceReady
        };

    private static MenuReturnScriptKind? GetVerificationReturnScriptKind(
        MenuDefinitionVerificationCheck check)
    {
        if (check.Kind != MenuVerificationCheckKind.ReturnScript)
        {
            return null;
        }

        if (check.Id.EndsWith(":menu-root", StringComparison.OrdinalIgnoreCase))
        {
            return MenuReturnScriptKind.AtMenuRoot;
        }

        if (check.Id.EndsWith(":below-root", StringComparison.OrdinalIgnoreCase))
        {
            return MenuReturnScriptKind.BelowMenuRoot;
        }

        return check.Id.Contains(":override:", StringComparison.OrdinalIgnoreCase)
            ? MenuReturnScriptKind.NodeOverride
            : null;
    }

    private static MenuDefinition AddVerificationRecords(
        MenuDefinition definition,
        MenuDefinitionVerificationPlan plan,
        IReadOnlyList<MenuDefinitionVerificationCheck> verifiedChecks)
    {
        var currentChecks = plan.Checks.ToDictionary(
            check => check.Id,
            StringComparer.OrdinalIgnoreCase);
        var records = (definition.Verification?.Checks ?? [])
            .Where(record => currentChecks.TryGetValue(record.Id, out var current)
                && current.Fingerprint.Equals(record.Fingerprint, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        foreach (var check in verifiedChecks)
        {
            records[check.Id] = new MenuVerificationRecord(check.Id, check.Fingerprint, now);
        }

        var manifest = new MenuVerificationManifest(
            plan.Display,
            records.Values.OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase).ToArray());
        return CopyMenuDefinition(
            definition,
            verification: manifest,
            replaceVerification: true);
    }

    private async Task PersistVerificationEvidenceAsync(
        MenuDefinition definition,
        IReadOnlyList<MenuDefinitionVerificationCheck> checks,
        bool verified,
        CancellationToken cancellationToken)
    {
        if (checks.Count == 0)
        {
            throw new InvalidOperationException(
                "The completed test no longer matches a verification requirement. Reload the menu file and run the regenerated check.");
        }

        var plan = MenuDefinitionVerificationPlanner.Create(definition);
        MenuDefinition updated;
        if (verified)
        {
            updated = AddVerificationRecords(definition, plan, checks);
        }
        else
        {
            var removedIds = checks
                .Select(check => check.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentChecks = plan.Checks.ToDictionary(
                check => check.Id,
                StringComparer.OrdinalIgnoreCase);
            var records = (definition.Verification?.Checks ?? [])
                .Where(record => !removedIds.Contains(record.Id)
                    && currentChecks.TryGetValue(record.Id, out var current)
                    && current.Fingerprint.Equals(
                        record.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            updated = CopyMenuDefinition(
                definition,
                verification: new MenuVerificationManifest(plan.Display, records),
                replaceVerification: true);
        }

        await PersistActiveMenuVerificationAsync(updated, cancellationToken)
            .ConfigureAwait(false);
    }

    private static MenuDefinition CopyMenuDefinition(
        MenuDefinition definition,
        IEnumerable<MenuTransition>? transitions = null,
        IEnumerable<MenuAnchor>? anchors = null,
        MenuTimingProfile? timing = null,
        IEnumerable<MenuNode>? nodes = null,
        MenuVerificationManifest? verification = null,
        bool replaceVerification = false) =>
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
            definition.ActiveConfigurationId,
            replaceVerification ? verification : definition.Verification,
            definition.ExternalStates.Values);

    private static MenuConfiguration NormalizeMenuConfigurationRequest(
        MenuConfigurationEditRequest request) => new(
            request.Id.Trim(),
            request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.Conditions) ? null : request.Conditions.Trim());

    private static bool IsMenuNodeDisabledByDefault(
        MenuDefinition definition,
        MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Disabled
                || (current.DisabledWhen ?? []).Any(condition =>
                    GetConditionValue(
                        definition,
                        condition.SourceId,
                        condition.SourceKind,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))?.Equals(
                        condition.EqualsValue,
                        StringComparison.OrdinalIgnoreCase) == true))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return false;
    }

    private static bool IsMenuNodePermanentlyDisabled(
        MenuDefinition definition,
        MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (current.Disabled)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsMenuNodeHiddenByDefault(
        MenuDefinition definition,
        MenuNode node) => (node.HiddenWhen ?? []).Any(condition =>
        GetConditionValue(
            definition,
            condition.SourceId,
            condition.SourceKind,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))?.Equals(
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
                    condition.SourceId.Trim(),
                    condition.EqualsValue.Trim(),
                    condition.SourceKind))
                .ToArray() ?? [],
            request.SelectionOptions?
                .Select(option => option.Trim())
                .ToArray() ?? [],
            request.MinimumValue,
            request.MaximumValue,
            request.HiddenWhen?
                .Where(condition => condition is not null)
                .Select(condition => new MenuNodeHiddenCondition(
                    condition.SourceId.Trim(),
                    condition.EqualsValue.Trim(),
                    condition.SourceKind))
                .ToArray() ?? [],
            request.Disabled);

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
            definition.ActiveConfigurationId,
            definition.Verification,
            definition.ExternalStates.Values);
        new MenuDefinitionValidator().ValidateAndThrow(candidate);
    }

    private static MenuDefinition AddDraftRecording(
        MenuDefinition definition,
        MenuRecordingRequest request,
        IReadOnlyList<MenuOperation> operations,
        IReadOnlyList<MenuOperation> returnOperations)
    {
        definition = NormalizeInitialMenuTiming(MigrateLegacyReturnReplacement(definition));
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
            definition.ActiveConfigurationId,
            definition.Verification,
            definition.ExternalStates.Values);
        new MenuDefinitionValidator().ValidateAndThrow(updated);
        return updated;
    }

    private async Task PersistActiveMenuDefinitionAsync(
        MenuDefinition definition,
        CancellationToken cancellationToken)
    {
        definition = MenuDefinitionVerificationReconciler.Reconcile(
            TopologyRouteGenerator.Regenerate(definition));
        new MenuDefinitionValidator().ValidateAndThrow(definition);
        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string currentPath;
            lock (_sync)
            {
                currentPath = GetMenuDefinitionPath(_settings);
            }

            if (IsInstalledMenuDefinitionPath(currentPath))
            {
                throw new InvalidOperationException(
                    "The active installation menu is read-only. Use 'Export & use structure' in Build & Verify to explicitly create an editable copy, then repeat the change.");
            }

            var path = currentPath;
            var distributableTopology = CopyMenuDefinition(
                definition,
                verification: null,
                replaceVerification: true);
            await new MenuDefinitionWriter()
                .WriteFileAsync(path, distributableTopology, cancellationToken)
                .ConfigureAwait(false);
            if (definition.Verification is not null)
            {
                await _menuVerificationStore.SaveAsync(
                        definition.Id,
                        definition.Verification,
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

    private async Task PersistActiveMenuVerificationAsync(
        MenuDefinition definition,
        CancellationToken cancellationToken)
    {
        var topology = MenuDefinitionVerificationOverlay.CreateTopology(definition);
        var effective = MenuDefinitionVerificationOverlay.Apply(
            topology,
            definition.Verification);
        var reconciled = MenuDefinitionVerificationReconciler.Reconcile(effective);
        var manifest = reconciled.Verification
            ?? throw new InvalidOperationException(
                "The active menu definition does not contain a display-verification record.");
        await _menuDefinitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _menuVerificationStore.SaveAsync(
                    reconciled.Id,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            InstallMenuDefinition(
                reconciled,
                preserveValidationProgress: true,
                preserveMenuState: true);
        }
        finally
        {
            _menuDefinitionGate.Release();
        }
    }

    private static bool PathsEqual(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)));

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(directory),
            Path.GetFullPath(path));
        return !Path.IsPathRooted(relativePath)
               && !relativePath.Equals("..", StringComparison.Ordinal)
               && !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static bool IsInstalledMenuDefinitionPath(string path) =>
        IsPathInsideDirectory(
            path,
            Path.Combine(AppContext.BaseDirectory, "menu-definitions"));

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

    private void ConfirmMenuReturnTarget(string targetNodeId, MenuReturnScriptKind kind) =>
        _menuStateTracker?.ConfirmNode(
            targetNodeId,
            $"The user confirmed that return-script validation '{kind}' reached normal video.");

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
        var selectedTransition = kind == MenuAuthoringItemKind.Transition
            && definition.Transitions.TryGetValue(itemId, out var candidate)
                ? candidate
                : null;
        var transitions = definition.Transitions.Values.Select(transition =>
        {
            if (selectedTransition is null)
            {
                return transition;
            }

            var selectedItem = transition.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase);
            var coveredByTopologyGroup = selectedTransition.GeneratedFromTopology
                                         && transition.GeneratedFromTopology
                                         && transition.ValidationGroupId?.Equals(
                                             selectedTransition.ValidationGroupId,
                                             StringComparison.OrdinalIgnoreCase) == true;
            var topologySeed = selectedTransition.GeneratedFromTopology
                               && transition.Id.Equals(
                                   selectedTransition.TopologySeedTransitionId,
                                   StringComparison.OrdinalIgnoreCase);
            return selectedItem || coveredByTopologyGroup || topologySeed
                ? transition with { Verified = true }
                : transition;
        }).ToArray();
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

        var entryTransition = definition.ApplicableTransitions
            .Where(transition => !transition.GeneratedFromTopology)
            .Where(transition => transition.FromNodeId.Equals(
                context.Anchor.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
            .Where(transition => transition.ToNodeId.Equals(
                context.Strategy.MenuRootNodeId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(transition => transition.Verified)
            .FirstOrDefault();
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
            validation?.ExpectedTargetPath,
            entryTransition is not null,
            entryTransition?.Id,
            entryTransition is null ? null : FormatKeyScript(entryTransition.Operations),
            entryTransition?.Verified == true);
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
        definition.ApplicableTransitions
            .Where(transition => transition.FromNodeId.Equals(
                context.Anchor.TargetNodeId,
                StringComparison.OrdinalIgnoreCase))
            .Where(transition => definition.IsDescendantOf(
                transition.ToNodeId,
                context.Strategy.MenuRootNodeId))
            .GroupBy(transition => transition.ToNodeId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(transition => transition.IsValidationRoute)
                .ThenByDescending(transition => transition.Verified)
                .ThenBy(transition => transition.Operations.Sum(operation => operation.Repeat))
                .First())
            .OrderByDescending(transition => transition.IsValidationRoute)
            .ThenByDescending(transition =>
                definition.GetRequiredNode(transition.ToNodeId).ControlType
                != MenuControlType.Confirmation)
            .ThenByDescending(transition => definition.GetDepth(transition.ToNodeId))
            .ThenByDescending(transition => transition.Operations.Sum(operation => operation.Repeat))
            .ThenBy(
                transition => definition.GetPath(transition.ToNodeId),
                StringComparer.OrdinalIgnoreCase)
            .Select(transition => new MenuReturnTestNodeSummary(
                transition.ToNodeId,
                definition.GetPath(transition.ToNodeId)))
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

    private static IReadOnlyList<MenuOperation> CreateKeyOperations(
        IReadOnlyList<string> keys) => CoalesceOperations(keys
        .Select(key => new MenuOperation(key))
        .ToArray());

    private static string CreateMenuAnchorId(MenuDefinition definition)
    {
        var configurationSuffix = string.IsNullOrWhiteSpace(definition.ActiveConfigurationId)
            ? null
            : $"-{definition.ActiveConfigurationId}";
        var preferred = definition.Anchors.ContainsKey("normal-video")
            || definition.Transitions.ContainsKey("normal-video")
                ? $"normal-video{configurationSuffix ?? "-anchor"}"
                : "normal-video";
        if (!definition.Anchors.ContainsKey(preferred)
            && !definition.Transitions.ContainsKey(preferred))
        {
            return preferred;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{preferred}-{suffix}";
            if (!definition.Anchors.ContainsKey(candidate)
                && !definition.Transitions.ContainsKey(candidate))
            {
                return candidate;
            }
        }
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
                ? anchor with
                {
                    Operations = strategy.BelowMenuRoot.Operations,
                    Verified = kind == MenuReturnScriptKind.BelowMenuRoot
                        ? verified
                        : anchor.Verified,
                    ReturnStrategy = strategy
                }
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
            .Where(transition => transition.Operations.Count > 0
                && (!transition.GeneratedFromTopology || transition.IsValidationRoute))
            .OrderBy(transition => definition.GetPath(transition.FromNodeId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(transition => definition.GetPath(transition.ToNodeId), StringComparer.OrdinalIgnoreCase)
            .Select(transition => new MenuTimingTestRouteSummary(
                transition.Id,
                definition.GetPath(transition.FromNodeId),
                definition.GetPath(transition.ToNodeId),
                transition.Operations.Sum(operation => operation.Repeat),
                transition.Operations.Any(operation => operation.DelayAfter is not null)))
            .ToArray();

    private static bool IsAuthoringValidationCandidate(
        MenuDefinition definition,
        MenuTransition transition)
    {
        if (transition.Verified)
        {
            return false;
        }

        if (transition.GeneratedFromTopology)
        {
            return transition.IsValidationRoute;
        }

        return !definition.ApplicableTransitions.Any(generated =>
            generated.GeneratedFromTopology
            && !generated.Verified
            && generated.IsValidationRoute
            && generated.TopologySeedTransitionId?.Equals(
                transition.Id,
                StringComparison.OrdinalIgnoreCase) == true);
    }

    private static int GetCoveredTopologyRouteCount(
        MenuDefinition definition,
        MenuTransition transition) => transition.GeneratedFromTopology
        ? definition.ApplicableTransitions.Count(candidate =>
            candidate.GeneratedFromTopology
            && candidate.ValidationGroupId?.Equals(
                transition.ValidationGroupId,
                StringComparison.OrdinalIgnoreCase) == true)
        : 1;

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

    private static MenuControlValueUpdate ValidatePictureControlUpdate(
        MenuDefinition definition,
        MenuControlValueUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.NodeId);
        var nodeId = update.NodeId.Trim();
        var node = definition.GetRequiredNode(nodeId);
        var fromValue = NormalizePictureControlValue(definition, node, update.FromValue);
        var toValue = NormalizePictureControlValue(definition, node, update.ToValue);
        if (node.ControlType == MenuControlType.Slider)
        {
            var fromNumber = decimal.Parse(fromValue, CultureInfo.InvariantCulture);
            var toNumber = decimal.Parse(toValue, CultureInfo.InvariantCulture);
            var commandCount = decimal.Abs(toNumber - fromNumber);
            if (commandCount > MenuDefinitionValidator.MaximumRepeat)
            {
                throw new InvalidOperationException(
                    $"Slider '{definition.GetPath(nodeId)}' requires {commandCount:G29} key presses; the maximum per update is {MenuDefinitionValidator.MaximumRepeat}.");
            }
        }

        return update with
        {
            NodeId = nodeId,
            FromValue = fromValue,
            ToValue = toValue
        };
    }

    private static MenuIndexedControlValueUpdate ValidateIndexedControlUpdate(
        MenuDefinition definition,
        MenuIndexedControlValueUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.SelectorNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.SelectorValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.NodeId);
        var selector = definition.GetRequiredNode(update.SelectorNodeId.Trim());
        if (!IsIndexedSelectionNode(selector))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(selector.Id)}' is not defined as an indexed selection.");
        }

        var selectorValue = NormalizePictureControlValue(
            definition,
            selector,
            update.SelectorValue);
        var node = definition.GetRequiredNode(update.NodeId.Trim());
        if (!GetIndexedSliderNodes(definition, selector).Any(candidate => candidate.Id.Equals(
                node.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{definition.GetPath(node.Id)}' is not a consecutive slider controlled by indexed selection '{definition.GetPath(selector.Id)}'.");
        }

        var normalized = ValidatePictureControlUpdate(
            definition,
            new MenuControlValueUpdate(node.Id, update.FromValue, update.ToValue));
        return update with
        {
            SelectorNodeId = selector.Id,
            SelectorValue = selectorValue,
            NodeId = normalized.NodeId,
            FromValue = normalized.FromValue,
            ToValue = normalized.ToValue
        };
    }

    private static MenuControlProfileValue NormalizeMenuControlProfileValue(
        MenuDefinition definition,
        MenuControlProfileValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Value);
        var hasSelectorId = !string.IsNullOrWhiteSpace(value.SelectorNodeId);
        var hasSelectorValue = !string.IsNullOrWhiteSpace(value.SelectorValue);
        if (hasSelectorId != hasSelectorValue)
        {
            throw new InvalidOperationException(
                "An indexed profile value must define both selector node and selector value.");
        }

        if (hasSelectorId)
        {
            var normalized = ValidateIndexedControlUpdate(
                definition,
                new MenuIndexedControlValueUpdate(
                    value.SelectorNodeId!,
                    value.SelectorValue!,
                    value.NodeId,
                    value.Value,
                    value.Value));
            return new MenuControlProfileValue(
                normalized.NodeId,
                normalized.ToValue,
                normalized.SelectorNodeId,
                normalized.SelectorValue);
        }

        var update = ValidatePictureControlUpdate(
            definition,
            new MenuControlValueUpdate(value.NodeId, value.Value, value.Value));
        return new MenuControlProfileValue(update.NodeId, update.ToValue);
    }

    private static IReadOnlyList<MenuControlProfileValue> NormalizeMenuControlProfileValues(
        MenuDefinition definition,
        IReadOnlyList<MenuControlProfileValue> values)
    {
        if (values.Count > 5000)
        {
            throw new InvalidOperationException(
                "A menu-control value set can store at most 5,000 values.");
        }

        var normalized = values
            .Select(value => NormalizeMenuControlProfileValue(definition, value))
            .ToArray();
        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in normalized)
        {
            var key = $"{value.SelectorNodeId}\u001f{value.SelectorValue}\u001f{value.NodeId}";
            if (!uniqueKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Menu-control value '{key}' is duplicated.");
            }
        }

        return normalized;
    }

    private void SeedMenuControlValuesFromState(
        MenuDefinition definition,
        SavedMenuControlState state)
    {
        ResetMenuControlValues(definition);
        foreach (var value in state.Values.Where(value =>
                     string.IsNullOrWhiteSpace(value.SelectorNodeId)))
        {
            var node = definition.GetRequiredNode(value.NodeId);
            _menuControlValues[node.Id] = NormalizePictureControlValue(
                definition,
                node,
                value.Value);
        }
    }

    private static IReadOnlyList<MenuNode> GetIndexedSliderNodes(
        MenuDefinition definition,
        MenuNode selector)
    {
        if (!IsIndexedSelectionNode(selector))
        {
            return [];
        }

        var nodes = definition.Nodes.Values.ToArray();
        var selectorIndex = Array.FindIndex(nodes, node => node.Id.Equals(
            selector.Id,
            StringComparison.OrdinalIgnoreCase));
        if (selectorIndex < 0)
        {
            return [];
        }

        var sliders = new List<MenuNode>();
        for (var index = selectorIndex + 1; index < nodes.Length; index++)
        {
            var candidate = nodes[index];
            if (candidate.ParentId?.Equals(selector.ParentId, StringComparison.OrdinalIgnoreCase) != true
                || candidate.ControlType != MenuControlType.Slider)
            {
                break;
            }

            sliders.Add(candidate);
        }

        return sliders;
    }

    private static bool IsIndexedSelectionNode(MenuNode selector) =>
        MenuControlBehaviorClassifier.GetEffectiveControlType(selector)
        == MenuControlType.IndexedSelection;

    private static string NormalizePictureControlValue(
        MenuDefinition definition,
        MenuNode node,
        string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        switch (node.ControlType)
        {
            case MenuControlType.Slider:
                if (node.MinimumValue is not { } minimum || node.MaximumValue is not { } maximum)
                {
                    throw new InvalidOperationException(
                        $"Slider '{definition.GetPath(node.Id)}' must define minimum and maximum values.");
                }

                if (!decimal.TryParse(
                        normalized,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var numericValue)
                    || numericValue < minimum
                    || numericValue > maximum)
                {
                    throw new InvalidOperationException(
                        $"Slider '{definition.GetPath(node.Id)}' values must remain between {minimum:G29} and {maximum:G29}.");
                }

                if (decimal.Truncate(numericValue) != numericValue)
                {
                    throw new InvalidOperationException(
                        $"Slider '{definition.GetPath(node.Id)}' currently supports whole-number key steps only.");
                }

                return numericValue.ToString("G29", CultureInfo.InvariantCulture);

            case MenuControlType.Switch:
                if (!normalized.Equals("on", StringComparison.OrdinalIgnoreCase)
                    && !normalized.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Switch '{definition.GetPath(node.Id)}' must be on or off.");
                }

                return normalized.ToLowerInvariant();

            case MenuControlType.Selection:
            case MenuControlType.SubmenuSelection:
            case MenuControlType.IndexedSelection:
                var option = (node.SelectionOptions ?? []).FirstOrDefault(candidate =>
                    candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                return option ?? throw new InvalidOperationException(
                    $"Selection '{definition.GetPath(node.Id)}' does not offer '{normalized}'.");

            default:
                throw new InvalidOperationException(
                    $"'{definition.GetPath(node.Id)}' is not an adjustable slider, switch, selection, or submenu selection.");
        }
    }

    private static Dictionary<string, string> CreateEffectivePictureControlValues(
        MenuDefinition definition,
        IReadOnlyDictionary<string, string> knownValues,
        IReadOnlyDictionary<string, string>? externalStateValues = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes.Values.Where(node =>
                     node.ControlType is MenuControlType.Slider
                         or MenuControlType.Switch
                         or MenuControlType.Selection
                         or MenuControlType.SubmenuSelection
                         or MenuControlType.IndexedSelection
                     && !string.IsNullOrWhiteSpace(node.DefaultValue)))
        {
            values[node.Id] = NormalizePictureControlValue(
                definition,
                node,
                node.DefaultValue!);
        }

        foreach (var (nodeId, value) in knownValues)
        {
            var node = definition.GetRequiredNode(nodeId);
            values[node.Id] = NormalizePictureControlValue(definition, node, value);
        }

        foreach (var state in definition.ExternalStates.Values)
        {
            var value = externalStateValues?.GetValueOrDefault(state.Id)
                ?? state.DefaultValue;
            values[ExternalStateValueKey(state.Id)] = state.Options.First(option =>
                option.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        return values;
    }

    private void ResetMenuControlValues(MenuDefinition? definition)
    {
        _menuControlValues.Clear();
        if (definition is null)
        {
            return;
        }

        foreach (var node in definition.Nodes.Values.Where(node =>
                     node.ControlType is MenuControlType.Slider
                         or MenuControlType.Switch
                         or MenuControlType.Selection
                         or MenuControlType.SubmenuSelection
                         or MenuControlType.IndexedSelection
                     && !string.IsNullOrWhiteSpace(node.DefaultValue)))
        {
            _menuControlValues[node.Id] = NormalizePictureControlValue(
                definition,
                node,
                node.DefaultValue!);
        }
    }

    private void ResetMenuExternalStateValues(
        MenuDefinition? definition,
        SamsungWebSettings settings)
    {
        _menuExternalStateValues.Clear();
        if (definition is null)
        {
            return;
        }

        var persisted = settings.MenuExternalStateDefinitionId?.Equals(
                definition.Id,
                StringComparison.OrdinalIgnoreCase) == true
            ? (settings.MenuExternalStateValues ?? []).ToDictionary(
                item => item.Id,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in definition.ExternalStates.Values)
        {
            var candidate = persisted.GetValueOrDefault(state.Id);
            _menuExternalStateValues[state.Id] = state.Options.FirstOrDefault(option =>
                    option.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                ?? state.DefaultValue;
        }
    }

    private static string ExternalStateValueKey(string stateId) =>
        $"external-state:{stateId}";

    private static string? GetConditionValue(
        MenuDefinition definition,
        string sourceId,
        MenuConditionSourceKind sourceKind,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        var key = sourceKind == MenuConditionSourceKind.ExternalState
            ? ExternalStateValueKey(sourceId)
            : sourceId;
        if (effectiveValues.TryGetValue(key, out var value))
        {
            return value;
        }

        return sourceKind == MenuConditionSourceKind.ExternalState
            ? definition.ExternalStates.GetValueOrDefault(sourceId)?.DefaultValue
            : definition.Nodes.GetValueOrDefault(sourceId)?.DefaultValue;
    }

    private static MenuControlValueUpdate[] OrderPictureControlUpdates(
        MenuDefinition definition,
        IReadOnlyList<MenuControlValueUpdate> updates)
    {
        var byNodeId = updates.ToDictionary(
            update => update.NodeId,
            StringComparer.OrdinalIgnoreCase);
        var result = new List<MenuControlValueUpdate>(updates.Count);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(MenuControlValueUpdate update)
        {
            if (visited.Contains(update.NodeId))
            {
                return;
            }

            if (!visiting.Add(update.NodeId))
            {
                throw new InvalidOperationException(
                    "Picture-control enablement rules contain a dependency cycle.");
            }

            var node = definition.GetRequiredNode(update.NodeId);
            var current = node;
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ancestors.Add(current.Id))
            {
                foreach (var condition in current.DisabledWhen ?? [])
                {
                    if (condition.SourceKind == MenuConditionSourceKind.MenuSetting
                        && byNodeId.TryGetValue(condition.SourceId, out var dependency))
                    {
                        Visit(dependency);
                    }
                }

                foreach (var condition in current.HiddenWhen ?? [])
                {
                    if (condition.SourceKind == MenuConditionSourceKind.MenuSetting
                        && byNodeId.TryGetValue(condition.SourceId, out var dependency))
                    {
                        Visit(dependency);
                    }
                }

                if (string.IsNullOrWhiteSpace(current.ParentId)
                    || !definition.Nodes.TryGetValue(current.ParentId, out current))
                {
                    break;
                }
            }

            visiting.Remove(update.NodeId);
            visited.Add(update.NodeId);
            result.Add(update);
        }

        foreach (var update in updates)
        {
            Visit(update);
        }

        return result.ToArray();
    }

    private static bool IsMenuNodeDisabled(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues) =>
        node.Disabled
        || (node.DisabledWhen ?? []).Any(condition =>
        {
            var value = GetConditionValue(
                definition,
                condition.SourceId,
                condition.SourceKind,
                effectiveValues);
            return value?.Equals(condition.EqualsValue, StringComparison.OrdinalIgnoreCase) == true;
        });

    private static bool IsMenuNodeOrAncestorDisabled(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (IsMenuNodeDisabled(definition, current, effectiveValues))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return false;
    }

    private static bool IsMenuNodeHidden(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues) =>
        (node.HiddenWhen ?? []).Any(condition =>
        {
            var value = GetConditionValue(
                definition,
                condition.SourceId,
                condition.SourceKind,
                effectiveValues);
            return value?.Equals(condition.EqualsValue, StringComparison.OrdinalIgnoreCase) == true;
        });

    private static bool IsMenuNodeOrAncestorHidden(
        MenuDefinition definition,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id))
        {
            if (IsMenuNodeHidden(definition, current, effectiveValues))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return false;
    }

    private static MenuDefinition CreateVisibilityAdjustedDefinition(
        MenuDefinition definition,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        if (!definition.Nodes.Values.Any(node =>
                node.DisabledWhen is { Count: > 0 }
                || node.HiddenWhen is { Count: > 0 }))
        {
            return definition;
        }

        var adjustedNodes = definition.Nodes.Values.Select(node =>
            effectiveValues.TryGetValue(node.Id, out var value)
                ? node with { DefaultValue = value }
                : node).ToArray();
        var adjustedExternalStates = definition.ExternalStates.Values.Select(state =>
            effectiveValues.TryGetValue(ExternalStateValueKey(state.Id), out var value)
                ? state with { DefaultValue = value }
                : state).ToArray();
        var adjusted = new MenuDefinition(
            definition.Id,
            definition.Name,
            definition.Model,
            definition.Context,
            adjustedNodes,
            definition.Transitions.Values,
            definition.Anchors.Values,
            definition.Timing,
            definition.Configurations.Values,
            definition.ActiveConfigurationId,
            definition.Verification,
            adjustedExternalStates);
        var regenerated = TopologyRouteGenerator.Regenerate(adjusted);
        var verifiedGroupIds = definition.Transitions.Values
            .Where(transition => transition.GeneratedFromTopology
                && transition.Verified
                && !string.IsNullOrWhiteSpace(transition.ValidationGroupId))
            .GroupBy(
                transition => transition.ValidationGroupId!,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(transition => transition.Verified)
                && !string.IsNullOrWhiteSpace(group.First().TopologySeedTransitionId)
                && definition.Transitions.TryGetValue(
                    group.First().TopologySeedTransitionId!,
                    out var seed)
                && seed.Verified)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var transitions = regenerated.Transitions.Values.Select(transition =>
            transition.GeneratedFromTopology
            && (transition.ValidationGroupId is not null
                && verifiedGroupIds.Contains(transition.ValidationGroupId)
                || IsNewlyAvailableConditionalRoute(
                    definition,
                    transition,
                    effectiveValues))
                ? transition with { Verified = true }
                : transition).ToArray();
        return new MenuDefinition(
            regenerated.Id,
            regenerated.Name,
            regenerated.Model,
            regenerated.Context,
            regenerated.Nodes.Values,
            transitions,
            regenerated.Anchors.Values,
            regenerated.Timing,
            regenerated.Configurations.Values,
            regenerated.ActiveConfigurationId,
            regenerated.Verification,
            regenerated.ExternalStates.Values);
    }

    private static bool IsNewlyAvailableConditionalRoute(
        MenuDefinition definition,
        MenuTransition transition,
        IReadOnlyDictionary<string, string> effectiveValues)
    {
        if (!transition.GeneratedFromTopology
            || string.IsNullOrWhiteSpace(transition.TopologySeedTransitionId)
            || !definition.Transitions.TryGetValue(
                transition.TopologySeedTransitionId,
                out var seed)
            || !seed.Verified
            || !definition.Nodes.TryGetValue(transition.ToNodeId, out var current))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id)
               && !current.Id.Equals(seed.ToNodeId, StringComparison.OrdinalIgnoreCase))
        {
            if (IsMenuNodeDisabledByDefault(definition, current)
                && !IsMenuNodeOrAncestorDisabled(definition, current, effectiveValues)
                || IsMenuNodeHiddenByDefault(definition, current)
                && !IsMenuNodeOrAncestorHidden(definition, current, effectiveValues))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(current.ParentId)
                || !definition.Nodes.TryGetValue(current.ParentId, out current))
            {
                break;
            }
        }

        return false;
    }

    private static bool HasVerifiedPictureControlRoute(MenuDefinition definition, string nodeId) =>
        definition.ApplicableTransitions.Any(transition =>
            transition.Verified
            && transition.ToNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase))
        || definition.ApplicableAnchors.Any(anchor =>
            anchor.Verified
            && anchor.TargetNodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase));

    private static string[] GetVerifiableSelectionNodeIds(MenuDefinition? definition)
    {
        if (definition is null)
        {
            return [];
        }

        return definition.Nodes.Values
            .Where(node => (node.ControlType is MenuControlType.Selection
                    or MenuControlType.SubmenuSelection
                    or MenuControlType.IndexedSelection)
                && !string.IsNullOrWhiteSpace(node.DefaultValue)
                && node.SelectionOptions is { Count: > 0 }
                && !IsMenuNodePermanentlyDisabled(definition, node))
            .Where(node => HasVerifiedPictureControlRoute(definition, node.Id)
                || HasConditionalAncestorWithVerifiedParent(definition, node))
            .Select(node => node.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasConditionalAncestorWithVerifiedParent(
        MenuDefinition definition,
        MenuNode node)
    {
        var current = node;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Add(current.Id) && !string.IsNullOrWhiteSpace(current.ParentId))
        {
            var parent = definition.GetRequiredNode(current.ParentId);
            if ((current.DisabledWhen is { Count: > 0 }
                 || current.HiddenWhen is { Count: > 0 })
                && parent.ControlType == MenuControlType.Submenu
                && HasVerifiedPictureControlRoute(definition, parent.Id))
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    private async Task PreparePictureControlAsync(
        MenuDefinition definition,
        MenuStateTracker tracker,
        MenuNode node,
        IReadOnlyDictionary<string, string> effectiveValues,
        CancellationToken cancellationToken)
    {
        var effectiveDefinition = CreateVisibilityAdjustedDefinition(
            definition,
            effectiveValues);
        if (HasVerifiedPictureControlRoute(definition, node.Id))
        {
            await PrepareMenuStateForValuesAsync(
                    definition,
                    tracker,
                    node.Id,
                    effectiveValues,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var hasDirectConditionalRoute = (node.DisabledWhen is { Count: > 0 }
                                         || node.HiddenWhen is { Count: > 0 })
                                        && !string.IsNullOrWhiteSpace(node.ParentId)
                                        && HasVerifiedPictureControlRoute(
                                            definition,
                                            node.ParentId);
        if (!hasDirectConditionalRoute)
        {
            if (HasVerifiedPictureControlRoute(effectiveDefinition, node.Id))
            {
                await PrepareMenuStateForValuesAsync(
                        definition,
                        tracker,
                        node.Id,
                        effectiveValues,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException(
                $"Menu control '{definition.GetPath(node.Id)}' does not have a verified navigation route.");
        }

        var parent = definition.GetRequiredNode(node.ParentId!);
        if (parent.ControlType != MenuControlType.Submenu
            || !HasVerifiedPictureControlRoute(definition, parent.Id))
        {
            throw new InvalidOperationException(
                $"Menu control '{definition.GetPath(node.Id)}' needs a verified route to its containing section.");
        }

        var navigableChildren = definition.Nodes.Values.Where(candidate =>
                candidate.ParentId?.Equals(parent.Id, StringComparison.OrdinalIgnoreCase) == true
                && !IsMenuNodeHidden(definition, candidate, effectiveValues))
            .ToArray();
        var childIndex = Array.FindIndex(navigableChildren, candidate =>
            candidate.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
        if (childIndex < 0)
        {
            throw new InvalidOperationException(
                $"Menu control '{definition.GetPath(node.Id)}' is not selectable under the current predicted settings.");
        }

        var currentNodeId = tracker.Current.NodeId;
        var currentChildIndex = string.IsNullOrWhiteSpace(currentNodeId)
            ? -1
            : Array.FindIndex(navigableChildren, candidate => candidate.Id.Equals(
                currentNodeId,
                StringComparison.OrdinalIgnoreCase));
        var offset = childIndex;
        if (currentChildIndex >= 0)
        {
            offset = childIndex - currentChildIndex;
        }
        else
        {
            await PrepareMenuStateForValuesAsync(
                    definition,
                    tracker,
                    parent.Id,
                    effectiveValues,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (offset != 0)
        {
            await ExecutePictureControlOperationsAsync(
                    $"Conditional route · {node.Label}",
                    currentChildIndex >= 0
                        ? definition.GetPath(currentNodeId!)
                        : definition.GetPath(parent.Id),
                    definition.GetPath(node.Id),
                    [new MenuOperation(offset > 0 ? "KEY_DOWN" : "KEY_UP", Repeat: Math.Abs(offset))],
                    definition.Timing,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        tracker.ConfirmNode(
            node.Id,
            $"Reached conditionally enabled control '{definition.GetPath(node.Id)}' from its verified containing section.");
    }

    private async Task PrepareMenuStateForValuesAsync(
        MenuDefinition definition,
        MenuStateTracker tracker,
        string targetNodeId,
        IReadOnlyDictionary<string, string> effectiveValues,
        CancellationToken cancellationToken)
    {
        var effectiveDefinition = CreateVisibilityAdjustedDefinition(
            definition,
            effectiveValues);
        var navigator = new MenuNavigator(
            effectiveDefinition,
            tracker,
            new WebMenuCommandTarget(_client),
            _menuDelay);
        navigator.ProgressChanged += HandleNavigationProgress;
        try
        {
            await navigator.PrepareStateAsync(targetNodeId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            navigator.ProgressChanged -= HandleNavigationProgress;
        }
    }

    private async Task ExecutePictureControlValueChangeAsync(
        MenuDefinition definition,
        MenuControlValueUpdate update,
        CancellationToken cancellationToken)
    {
        if (update.FromValue.Equals(update.ToValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var node = definition.GetRequiredNode(update.NodeId);
        IReadOnlyList<MenuOperation> operations = node.ControlType switch
        {
            MenuControlType.Slider => CreateSliderValueOperations(update),
            MenuControlType.Switch => [new MenuOperation("KEY_ENTER")],
            MenuControlType.Selection => CreateSelectionValueOperations(
                node,
                update,
                returnToContainingMenu: false),
            MenuControlType.SubmenuSelection => CreateSelectionValueOperations(
                node,
                update,
                returnToContainingMenu: true),
            MenuControlType.IndexedSelection => CreateSelectionValueOperations(
                node,
                update,
                returnToContainingMenu: false),
            _ => throw new InvalidOperationException(
                $"'{definition.GetPath(node.Id)}' is not an adjustable menu control.")
        };
        var path = definition.GetPath(node.Id);
        await ExecutePictureControlOperationsAsync(
                $"Adjust {node.ControlType.ToString().ToLowerInvariant()} · {path}",
                path,
                path,
                operations,
                definition.Timing,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<MenuOperation> CreateSliderValueOperations(
        MenuControlValueUpdate update)
    {
        var fromValue = decimal.Parse(update.FromValue, CultureInfo.InvariantCulture);
        var toValue = decimal.Parse(update.ToValue, CultureInfo.InvariantCulture);
        var repeat = decimal.ToInt32(decimal.Abs(toValue - fromValue));
        return repeat == 0
            ? []
            : [new MenuOperation(toValue > fromValue ? "KEY_RIGHT" : "KEY_LEFT", Repeat: repeat)];
    }

    private static IReadOnlyList<MenuOperation> CreateSelectionValueOperations(
        MenuNode node,
        MenuControlValueUpdate update,
        bool returnToContainingMenu)
    {
        var options = node.SelectionOptions ?? [];
        var fromIndex = options.ToList().FindIndex(option =>
            option.Equals(update.FromValue, StringComparison.OrdinalIgnoreCase));
        var toIndex = options.ToList().FindIndex(option =>
            option.Equals(update.ToValue, StringComparison.OrdinalIgnoreCase));
        var difference = toIndex - fromIndex;
        if (difference == 0)
        {
            return [];
        }

        var operations = new List<MenuOperation>
        {
            new MenuOperation("KEY_ENTER"),
            new MenuOperation(difference > 0 ? "KEY_DOWN" : "KEY_UP", Repeat: Math.Abs(difference)),
            new MenuOperation("KEY_ENTER")
        };
        if (returnToContainingMenu)
        {
            operations.Add(new MenuOperation("KEY_RETURN"));
        }

        return operations;
    }

    private static IReadOnlyList<MenuOperation> CreateConfirmationOperations(
        MenuNode node,
        string choice)
    {
        var options = node.SelectionOptions ?? [];
        var fromIndex = options.ToList().FindIndex(option => option.Equals(
            node.DefaultValue,
            StringComparison.OrdinalIgnoreCase));
        var toIndex = options.ToList().FindIndex(option => option.Equals(
            choice,
            StringComparison.OrdinalIgnoreCase));
        if (fromIndex < 0 || toIndex < 0)
        {
            throw new InvalidOperationException(
                $"Confirmation '{node.Label}' has an invalid initial or requested choice.");
        }

        var operations = new List<MenuOperation> { new("KEY_ENTER") };
        var difference = toIndex - fromIndex;
        if (difference != 0)
        {
            operations.Add(new MenuOperation(
                difference > 0 ? "KEY_DOWN" : "KEY_UP",
                Repeat: Math.Abs(difference)));
        }

        operations.Add(new MenuOperation("KEY_ENTER"));
        return operations;
    }

    private async Task ExecutePictureControlOperationsAsync(
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
                }

                NotifyChanged();
                await _client.SendKeyAsync(operation.Key, operation.Action, cancellationToken)
                    .ConfigureAwait(false);
                await _menuDelay.DelayAsync(
                        operation.DelayAfter ?? timing.GetDelay(operation.Key),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

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
        bool preserveValidationProgress = false,
        bool preserveMenuState = false)
    {
        var tracker = new MenuStateTracker(definition);
        var navigator = new MenuNavigator(
            definition,
            tracker,
            new WebMenuCommandTarget(_client),
            _menuDelay);
        tracker.Changed += HandleMenuStateChanged;
        navigator.ProgressChanged += HandleNavigationProgress;

        MenuStateTracker? previousTracker;
        MenuNavigator? previousNavigator;
        MenuState? previousState;
        lock (_sync)
        {
            previousTracker = _menuStateTracker;
            previousNavigator = _menuNavigator;
            previousState = previousTracker?.Current;
            _menuDefinition = definition;
            _menuStateTracker = tracker;
            _menuNavigator = navigator;
            ResetMenuControlValues(definition);
            ResetMenuExternalStateValues(definition, _settings);
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

        if (preserveMenuState
            && previousState?.NodeId is { } nodeId
            && definition.Nodes.ContainsKey(nodeId))
        {
            var reason =
                $"Menu definition reloaded; preserved the prior expected state. {previousState.Reason}";
            switch (previousState.Confidence)
            {
                case MenuStateConfidence.Synchronized:
                    tracker.ConfirmNode(nodeId, reason);
                    break;
                case MenuStateConfidence.Probable:
                    tracker.AssumeNode(nodeId, reason);
                    break;
                case MenuStateConfidence.Low:
                    tracker.AssumeNode(nodeId, reason);
                    tracker.ReduceConfidence(reason);
                    break;
            }
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

    private async Task<ResolvedDisplayDefinition> ResolveDisplayDefinitionAsync(
        string displayDefinitionPath,
        DisplayDefinitionDocument definition,
        IReadOnlyList<MenuDefinitionCatalogEntry> menuDefinitions,
        CancellationToken cancellationToken)
    {
        var resolvedMenus = new List<ResolvedDisplayMenuReference>(definition.Menus.Count);
        foreach (var reference in definition.Menus)
        {
            resolvedMenus.Add(await ResolveDisplayMenuReferenceAsync(
                    displayDefinitionPath,
                    reference,
                    menuDefinitions,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        var defaultMenuId = string.IsNullOrWhiteSpace(definition.DefaultMenu)
            ? definition.Menus[0].Id
            : definition.DefaultMenu;
        var activeMenu = resolvedMenus.First(menu => menu.Reference.Id.Equals(
            defaultMenuId,
            StringComparison.OrdinalIgnoreCase));
        return new ResolvedDisplayDefinition(resolvedMenus, activeMenu);
    }

    private async Task ApplyResolvedDisplayDefinitionAsync(
        string displayDefinitionPath,
        DisplayDefinitionDocument definition,
        ResolvedDisplayMenuReference menu,
        CancellationToken cancellationToken)
    {
        await SetMenuDefinitionAsync(menu.CatalogEntry.Path, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(menu.ConfigurationId))
        {
            await SetMenuConfigurationAsync(menu.ConfigurationId, cancellationToken)
                .ConfigureAwait(false);
        }

        await UpdateSettingsAsync(
                current => current with
                {
                    DisplayDefinitionPath = displayDefinitionPath,
                    Name = definition.Name,
                    Host = definition.Connection.Host ?? current.Host,
                    Secure = definition.Connection.Secure,
                    Port = definition.Connection.Port,
                    AllowUntrustedCertificate = definition.Connection.AllowUntrustedCertificate
                },
                cancellationToken)
            .ConfigureAwait(false);
        var settings = GetSettings();
        var hasToken = settings.Host is not null
                       && await _tokenStore.LoadAsync(settings.Host, cancellationToken)
                           .ConfigureAwait(false) is not null;
        lock (_sync)
        {
            _hasToken = hasToken;
        }

        NotifyChanged();
    }

    private async Task<ResolvedDisplayMenuReference> ResolveDisplayMenuReferenceAsync(
        string displayDefinitionPath,
        DisplayMenuDefinitionReference reference,
        IReadOnlyList<MenuDefinitionCatalogEntry> menuDefinitions,
        CancellationToken cancellationToken)
    {
        MenuDefinitionCatalogEntry? catalogEntry;
        if (!string.IsNullOrWhiteSpace(reference.Path))
        {
            var referencedPath = Path.IsPathRooted(reference.Path)
                ? Path.GetFullPath(reference.Path)
                : Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(displayDefinitionPath)!,
                        reference.Path));
            var inspection = await new MenuDefinitionSchemaInspector()
                .InspectFileAsync(referencedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!inspection.IsValid)
            {
                throw new InvalidOperationException(
                    $"Referenced menu file '{referencedPath}' is invalid:" + Environment.NewLine
                    + string.Join(Environment.NewLine, inspection.Diagnostics));
            }

            if (!inspection.Id!.Equals(
                    reference.DefinitionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Referenced menu file '{referencedPath}' contains definition ID '{inspection.Id}', not '{reference.DefinitionId}'.");
            }

            catalogEntry = menuDefinitions.FirstOrDefault(item => PathsEqual(
                item.Path,
                referencedPath));
            catalogEntry ??= new MenuDefinitionCatalogEntry(
                referencedPath,
                inspection.Id,
                inspection.Name!,
                inspection.Model!,
                inspection.Context!,
                "Custom file",
                false);
        }
        else
        {
            var matches = menuDefinitions
                .Where(item => item.IsValid
                               && item.Id.Equals(
                                   reference.DefinitionId,
                                   StringComparison.OrdinalIgnoreCase))
                .Where(item => reference.Source == DisplayMenuDefinitionSource.Any
                               || ClassifyMenuDefinitionSource(item.Path) == reference.Source)
                .OrderBy(item => GetDisplayMenuSourcePriority(
                    ClassifyMenuDefinitionSource(item.Path)))
                .ThenByDescending(item => item.IsActive)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            catalogEntry = matches.FirstOrDefault();
            if (catalogEntry is null)
            {
                var source = reference.Source == DisplayMenuDefinitionSource.Any
                    ? "any menu-definition catalog"
                    : FormatDisplayMenuSource(reference.Source);
                throw new InvalidOperationException(
                    $"Menu definition '{reference.DefinitionId}' was not found in {source}. Add it to menu-definitions or correct the display reference.");
            }
        }

        var topology = await LoadMenuTopologyForDisplayReferenceAsync(
                catalogEntry.Path,
                cancellationToken)
            .ConfigureAwait(false);
        string? configurationId = null;
        if (!string.IsNullOrWhiteSpace(reference.ConfigurationId))
        {
            configurationId = topology.Configurations.TryGetValue(
                reference.ConfigurationId,
                out var configuration)
                ? configuration.Id
                : throw new InvalidOperationException(
                    $"Menu configuration '{reference.ConfigurationId}' does not exist in referenced menu definition '{topology.Id}'.");
        }
        else
        {
            configurationId = topology.Configurations.Values.FirstOrDefault()?.Id;
        }

        return new ResolvedDisplayMenuReference(reference, catalogEntry, configurationId);
    }

    private static IReadOnlyList<DisplayMenuDefinitionReference>
        CreateUpdatedDisplayMenuReferences(
            IReadOnlyList<DisplayMenuDefinitionReference> existing,
            string definitionId,
            DisplayMenuDefinitionSource source,
            string? path,
            string? configurationId,
            out string activeReferenceId)
    {
        var normalizedConfigurationId = string.IsNullOrWhiteSpace(configurationId)
            ? null
            : configurationId.Trim();
        var matching = existing.FirstOrDefault(item =>
            item.DefinitionId.Equals(definitionId, StringComparison.OrdinalIgnoreCase)
            && item.Source == source
            && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                item.ConfigurationId,
                normalizedConfigurationId,
                StringComparison.OrdinalIgnoreCase));
        if (matching is not null)
        {
            activeReferenceId = matching.Id;
            return existing.ToArray();
        }

        var baseId = DisplayDefinitionStore.CreateIdentifier(
            $"{definitionId}-{normalizedConfigurationId ?? "default"}");
        var usedIds = existing
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        activeReferenceId = baseId;
        for (var suffix = 2; usedIds.Contains(activeReferenceId); suffix++)
        {
            activeReferenceId = $"{baseId}-{suffix}";
        }

        return existing.Append(new DisplayMenuDefinitionReference
        {
            Id = activeReferenceId,
            DefinitionId = definitionId,
            Source = source,
            Path = path,
            ConfigurationId = normalizedConfigurationId
        }).ToArray();
    }

    private static async Task<MenuDefinition> LoadMenuTopologyForDisplayReferenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var parsed = await new MenuDefinitionParser()
            .ParseFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var topology = TopologyRouteGenerator.Regenerate(parsed);
        new MenuDefinitionValidator().ValidateAndThrow(topology);
        return topology;
    }

    private DisplayMenuDefinitionSource ClassifyMenuDefinitionSource(string path)
    {
        if (IsPathInsideDirectory(
                path,
                Path.Combine(_configurationDirectory, "menu-definitions")))
        {
            return DisplayMenuDefinitionSource.UserData;
        }

        var repositoryDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "menu-definitions");
        var installedDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "menu-definitions");
        if (!PathsEqual(repositoryDirectory, installedDirectory)
            && IsPathInsideDirectory(path, repositoryDirectory))
        {
            return DisplayMenuDefinitionSource.Repository;
        }

        return IsPathInsideDirectory(path, installedDirectory)
            ? DisplayMenuDefinitionSource.Installation
            : DisplayMenuDefinitionSource.CustomFile;
    }

    private static int GetDisplayMenuSourcePriority(
        DisplayMenuDefinitionSource source) => source switch
        {
            DisplayMenuDefinitionSource.UserData => 0,
            DisplayMenuDefinitionSource.Repository => 1,
            DisplayMenuDefinitionSource.Installation => 2,
            DisplayMenuDefinitionSource.CustomFile => 3,
            _ => 4
        };

    private static string FormatDisplayMenuSource(
        DisplayMenuDefinitionSource source) => source switch
        {
            DisplayMenuDefinitionSource.UserData => "the user-data menu catalog",
            DisplayMenuDefinitionSource.Repository => "the repository menu catalog",
            DisplayMenuDefinitionSource.Installation => "the installation menu catalog",
            DisplayMenuDefinitionSource.CustomFile => "a custom menu file",
            _ => "any menu-definition catalog"
        };

    private static void AddMenuDefinitionDirectory(
        IDictionary<string, MenuDefinitionCandidate> candidates,
        string directory,
        string location,
        int priority)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            AddMenuDefinitionCandidate(candidates, path, location, priority);
        }
    }

    private static void AddMenuDefinitionCandidate(
        IDictionary<string, MenuDefinitionCandidate> candidates,
        string path,
        string location,
        int priority)
    {
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (!File.Exists(fullPath)
            || !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!candidates.TryGetValue(fullPath, out var existing))
        {
            candidates[fullPath] = new MenuDefinitionCandidate(
                fullPath,
                location,
                priority);
            return;
        }

        if (priority < existing.Priority)
        {
            candidates[fullPath] = existing with { Priority = priority };
        }
    }

    private static void AddDisplayDefinitionDirectory(
        IDictionary<string, DisplayDefinitionCandidate> candidates,
        string directory,
        string location,
        int priority)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "*.display.json",
                     SearchOption.AllDirectories))
        {
            AddDisplayDefinitionCandidate(candidates, path, location, priority);
        }
    }

    private static void AddDisplayDefinitionCandidate(
        IDictionary<string, DisplayDefinitionCandidate> candidates,
        string? path,
        string location,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)
            || !fullPath.EndsWith(".display.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!candidates.ContainsKey(fullPath))
        {
            candidates[fullPath] = new DisplayDefinitionCandidate(
                fullPath,
                location,
                priority);
        }
    }

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

    private async Task<MenuDefinition> LoadValidatedMenuDefinitionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var parsed = await new MenuDefinitionParser()
            .ParseFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var embeddedVerification = parsed.Verification;
        var legacyDefinition = TopologyRouteGenerator.Regenerate(
            MigrateLegacyReturnReplacement(NormalizeInitialMenuTiming(parsed)));
        var topology = TopologyRouteGenerator.Regenerate(
            MenuDefinitionVerificationOverlay.CreateTopology(legacyDefinition));
        var display = MenuDefinitionVerificationPlanner.Create(topology).Display;
        var localVerification = await _menuVerificationStore.LoadAsync(
                topology.Id,
                display,
                cancellationToken)
            .ConfigureAwait(false);
        var migratedVerification = localVerification
            ?? embeddedVerification
            ?? MenuDefinitionVerificationOverlay.CreateLegacyManifest(
                legacyDefinition,
                File.GetLastWriteTimeUtc(path));
        if (localVerification is null && migratedVerification is not null)
        {
            await _menuVerificationStore.SaveAsync(
                    topology.Id,
                    migratedVerification,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var definition = MenuDefinitionVerificationOverlay.Apply(
            topology,
            migratedVerification);

        try
        {
            new MenuDefinitionValidator().ValidateAndThrow(definition);
        }
        catch (MenuDefinitionValidationException exception)
        {
            var source = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (MenuDefinitionFileFormats.DetectForRead(path, source)
                == MenuDefinitionFileFormat.Json)
            {
                throw MenuDefinitionSourceDiagnostics.AddJsonLocations(source, exception);
            }

            throw;
        }

        return definition;
    }

    private static MenuDefinition NormalizeInitialMenuTiming(MenuDefinition definition)
    {
        var timing = definition.Timing;
        var usesLegacyDefaults = timing.DefaultDelayMilliseconds == 150
                                 && timing.ScreenChangeDelayMilliseconds == 500
                                 && timing.ReturnDelayMilliseconds == 300;
        var hasTimingTestRoute = definition.Transitions.Values.Any(
            transition => transition.Operations.Count > 0);
        var normalized = timing with
        {
            ScreenChangeDelayMilliseconds = usesLegacyDefaults
                ? 800
                : timing.ScreenChangeDelayMilliseconds,
            Verified = timing.Verified || usesLegacyDefaults || !hasTimingTestRoute
        };
        if (normalized == timing)
        {
            return definition;
        }

        return CopyMenuDefinition(definition, timing: normalized);
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

    private Task ClearMenuSliderBehaviorVerificationAsync(CancellationToken cancellationToken) =>
        UpdateSettingsAsync(
            current => current with
            {
                SliderVerificationHost = current.Host,
                VerifiedSliderNodeIds = []
            },
            cancellationToken);

    private Task ClearMenuSelectionBehaviorVerificationAsync(
        CancellationToken cancellationToken) =>
        UpdateSettingsAsync(
            current => current with
            {
                SelectionVerificationHost = current.Host,
                VerifiedSelectionNodeIds = []
            },
            cancellationToken);

    private Task ClearMenuControlBehaviorVerificationAsync(
        CancellationToken cancellationToken) =>
        UpdateSettingsAsync(
            current => current with
            {
                SliderVerificationHost = current.Host,
                VerifiedSliderNodeIds = [],
                SelectionVerificationHost = current.Host,
                VerifiedSelectionNodeIds = []
            },
            cancellationToken);

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

    private sealed record MenuDefinitionCandidate(
        string Path,
        string Location,
        int Priority);

    private sealed record DisplayDefinitionCandidate(
        string Path,
        string Location,
        int Priority);

    private sealed record ResolvedDisplayMenuReference(
        DisplayMenuDefinitionReference Reference,
        MenuDefinitionCatalogEntry CatalogEntry,
        string? ConfigurationId);

    private sealed record ResolvedDisplayDefinition(
        IReadOnlyList<ResolvedDisplayMenuReference> Menus,
        ResolvedDisplayMenuReference ActiveMenu);

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
