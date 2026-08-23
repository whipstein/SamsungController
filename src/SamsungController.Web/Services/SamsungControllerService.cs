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
    private readonly string _configurationDirectory;
    private readonly string _settingsPath;
    private readonly string _tokenPath;
    private readonly string _defaultMenuDefinitionPath;
    private readonly JsonFileSamsungTokenStore _tokenStore;
    private readonly NdjsonProtocolLogger _logger;
    private readonly SamsungTvClient _client;
    private readonly List<SamsungMessage> _messages = [];
    private readonly List<MacroExecutionProgress> _macroProgress = [];

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

    public async Task ConnectAsync(
        TvConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
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
        var fullPath = Path.GetFullPath(path.Trim());
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

    private void BeginAutomation(bool isNavigation)
    {
        lock (_sync)
        {
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
}
