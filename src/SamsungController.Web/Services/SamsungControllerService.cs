using System.Text.Json;
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
    private readonly List<SamsungMessage> _messages = [];
    private readonly List<MacroExecutionProgress> _macroProgress = [];
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
    private string? _activeMacro;
    private string? _lastMacroStatus;
    private string? _navigationStatus;
    private string? _navigationError;
    private NavigationProgress? _navigationProgress;
    private MenuValidationSession? _menuValidation;
    private string? _menuAuthoringStatus;
    private string? _menuAuthoringError;
    private string? _lastError;
    private bool _disposed;

    public SamsungControllerService(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
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
            new ClientWebSocketSamsungTransport(),
            _tokenStore,
            [_logger]);
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
                        GetFirstDelayMilliseconds(anchor.Operations)))
                    .Concat(definition.Transitions.Values
                        .Where(transition => !transition.Verified)
                        .Select(transition => new MenuAuthoringCandidateSummary(
                            MenuAuthoringItemKind.Transition,
                            transition.Id,
                            transition.Id,
                            transition.FromNodeId,
                            transition.ToNodeId,
                            definition.GetPath(transition.FromNodeId),
                            definition.GetPath(transition.ToNodeId),
                            transition.Operations.Sum(operation => operation.Repeat),
                            GetFirstDelayMilliseconds(transition.Operations))))
                    .ToArray();
            var request = _menuRecorder.Request;
            var steps = _menuRecorder.Operations
                .Select(operation => new MenuRecordedStepSummary(
                    operation.Key,
                    operation.Action,
                    operation.Repeat,
                    operation.DelayAfter ?? TimeSpan.Zero))
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
                        Port = request.Port
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

        var normalized = NormalizeRecordingRequest(request);
        ValidateRecordingRequest(definition, normalized);
        lock (_sync)
        {
            _menuRecorder.Start(normalized);
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

    private static int GetFirstDelayMilliseconds(IReadOnlyList<MenuOperation> operations) =>
        (int)Math.Clamp(
            operations.FirstOrDefault()?.DelayAfter?.TotalMilliseconds ?? 500,
            50,
            30_000);

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

        var placeholder = new MenuOperation(
            "KEY_RETURN",
            DelayAfter: TimeSpan.FromMilliseconds(request.ReplayDelayMilliseconds));
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
            anchors);
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
            anchors);
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
            anchors);
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
            _menuAuthoringStatus = "Preparing the recorded source position";
            _menuAuthoringError = null;
        }

        NotifyChanged();
        try
        {
            MenuStateTracker tracker;
            MenuNavigator navigator;
            lock (_sync)
            {
                tracker = _menuStateTracker
                    ?? throw new InvalidOperationException("No menu state tracker is available.");
                navigator = _menuNavigator
                    ?? throw new InvalidOperationException("No menu navigator is available.");
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

                var setup = FindValidationSetup(definition, transition.FromNodeId);
                await navigator.ExecuteAnchorAsync(setup.Anchor.Id, linkedSource.Token)
                    .ConfigureAwait(false);
                if (setup.Plan.Transitions.Count > 0)
                {
                    await navigator.ExecutePlanAsync(setup.Plan, linkedSource.Token)
                        .ConfigureAwait(false);
                }

                await ExecuteAuthoringOperationsAsync(
                        $"Validate transition · {transition.Id}",
                        definition.GetPath(transition.FromNodeId),
                        definition.GetPath(transition.ToNodeId),
                        transition.Operations,
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
                if (operation.DelayAfter is { } delay)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
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
}
