using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string MenuUpdatePath => Path.Combine(_directory, "menu-update.json");
    private string MenuPreferencesPath => Path.Combine(_directory, "menu-preferences.json");
    private static IpMenuSnapshot ResetMenu(IpMenuSnapshot previous) => new() { Preferences = previous.Preferences, Update = previous.Update, SelectorSession = previous.SelectorSession, WhiteBalanceRead = previous.WhiteBalanceRead };
    private static bool IsConnectionFailure(SamsungIpRemoteOutcome outcome) => outcome is SamsungIpRemoteOutcome.NotPaired or SamsungIpRemoteOutcome.Unauthorized
        or SamsungIpRemoteOutcome.TransportError or SamsungIpRemoteOutcome.CertificateError or SamsungIpRemoteOutcome.Timeout or SamsungIpRemoteOutcome.HttpError;
    private void UpdateMenu(Func<IpMenuSnapshot, IpMenuSnapshot> change) => Update(state => state with { Menu = change(state.Menu) });

    public Task ConnectMenuAsync(bool loadAllSettings = true) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        UpdateMenu(menu => ResetMenu(menu) with { ConnectionLoadAttempted = loadAllSettings, Status = "Connecting and reading TV values…" });
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Connected = true, SessionId = Guid.NewGuid(), Status = "Connected. Current values come from TV queries; documented controls need no verification." });
        // Optional identity: failure of a model-specific getter is not a failure of the two base reads.
        var identity = await MenuQueryAsync(profile, "getDeviceInformation", cancellation).ConfigureAwait(false);
        StoreMenuRead("getDeviceInformation", identity);
        if (IsConnectionFailure(identity.Outcome)) RequireSuccess(identity);
        if (loadAllSettings) await LoadAllMenuSettingsAsync(profile, cancellation).ConfigureAwait(false);
    }, needsConnection: false);

    public async Task DisconnectMenuAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            _client.CloseConnection();
            UpdateMenu(menu => ResetMenu(menu) with { Status = "Disconnected locally. The TV and saved token were not changed." });
        }
        finally { _gate.Release(); }
    }

    public Task RefreshMenuSectionAsync(string section) => RunMenuOperationAsync((profile, cancellation) => RefreshMenuSectionCoreAsync(profile, section, cancellation));

    private async Task RefreshMenuSectionCoreAsync(IpRemoteProfile profile, string section, CancellationToken cancellation, bool loadingGrid = false, bool refreshBase = true)
    {
        if (!IpMenuCatalog.Sections.Any(item => item.Id == section)) throw new ArgumentException("Unknown settings section.");
        var methods = IpMenuCatalog.ForSection(section).Select(control => control.Method).Distinct().ToArray();
        UpdateMenu(menu => menu with
        {
            Readings = menu.Readings.Where(pair => !methods.Contains(pair.Key)
                || (!refreshBase && SamsungIpRemoteCommands.Get(pair.Key).ReadbackMethod is ("getTVStates" or "getVideoStates"))).ToDictionary(),
            SectionsRead = menu.SectionsRead.Where(pair => pair.Key != section).ToDictionary(),
            Status = "Reading " + IpMenuCatalog.Sections.Single(item => item.Id == section).Name + "…"
        });
        if (refreshBase) await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        foreach (var method in methods)
        {
            cancellation.ThrowIfCancellationRequested();
            var command = SamsungIpRemoteCommands.Get(method);
            if (command.ReadbackMethod is "getTVStates" or "getVideoStates") continue;
            // A grid load reads its selector and RGB channels at their actual
            // indexed locations below, not once more for the initial row here.
            if (loadingGrid && IpMenuGrids.ForSection(section) is { } grid
                && (method == grid.SelectorMethod || grid.Fields.Any(field => method == field + "Control"))) continue;
            var exchange = await MenuQueryAsync(profile, method, cancellation).ConfigureAwait(false);
            StoreMenuRead(method, exchange);
            if (IsConnectionFailure(exchange.Outcome) || exchange.Outcome == SamsungIpRemoteOutcome.Canceled) RequireSuccess(exchange);
        }
        UpdateMenu(menu => menu with
        {
            SectionsRead = new Dictionary<string, DateTimeOffset>(menu.SectionsRead) { [section] = _timeProvider.GetUtcNow() },
            Status = "TV values refreshed. Unreported or unavailable controls are not filled with defaults."
        });
    }

    public string? MenuControlDisabledReason(IpMenuControl control)
    {
        var snapshot = GetSnapshot(); var menu = snapshot.Menu;
        if (!menu.Connected) return "Connect to the TV first.";
        if (snapshot.IsBusy) return "A TV request is running.";
        if (menu.Update?.NeedsReview == true) return "Review the interrupted update before applying more changes.";
        if (menu.WhiteBalanceRead?.NeedsRestore == true) return "Restore/check the interrupted 20-point white-balance read before applying changes.";
        if (IpMenuAvailability.For(menu, control) is { Reason: { } unavailable }) return unavailable;
        if (menu.Value(control) is not { } value) return (control.IsIndexed ? menu.IndexedReadings.GetValueOrDefault(control.Id) : menu.Readings.GetValueOrDefault(control.Method)) is { } reading
            ? reading.Outcome == SamsungIpRemoteOutcome.Success ? "Not reported in the TV reply for this display/state." : reading.Message
            : control.IsIndexed ? "Load all rows to read this value from the TV. Enable the required mode first." : "Read this section to get the current TV value.";
        if (string.IsNullOrWhiteSpace(menu.Input) || string.IsNullOrWhiteSpace(menu.PictureMode)) return "The TV did not report its input/picture mode. Refresh before editing settings.";
        if (!UsableOriginal(control.Parameter, value)) return "The returned value does not match the documented control type/range. Inspect diagnostics; no default was substituted.";
        try { _ = MenuPrerequisites(menu, control, forEditing: true); }
        catch (InvalidOperationException error) { return error.Message; }
        return null;
    }

    public void StageMenuValue(string controlId, string text)
    {
        var control = IpMenuCatalog.Get(controlId);
        var target = IpMenuCatalog.ParseTarget(control, text);
        lock (_sync)
        {
            if (MenuControlDisabledReason(control) is { } reason) throw new InvalidOperationException(reason);
            var menu = _snapshot.Menu;
            var pending = new Dictionary<string, IpMenuDraft>(menu.Pending);
            if (control.RequiresSeparateApply && pending.Keys.Any(id => id != controlId)
                || pending.Values.Any(draft => draft.ControlId != controlId && IpMenuCatalog.Get(draft.ControlId).RequiresSeparateApply))
                throw new InvalidOperationException("Apply or discard other pending changes before changing the input, mode, interval or color selector.");
            if (EquivalentCommandValue(menu.Value(control), target)) pending.Remove(controlId);
            else pending[controlId] = new(controlId, target, menu.Value(control)!.DeepClone(), MenuPrerequisites(menu, control, forEditing: true), menu.Input ?? "", menu.PictureMode ?? "");
            _snapshot = _snapshot with { Menu = menu with { Pending = pending } };
        }
        Changed?.Invoke();
    }

    public void DiscardMenuChanges()
    {
        lock (_sync)
        {
            if (_snapshot.IsBusy) throw new InvalidOperationException("Stop or finish the active update first.");
            _snapshot = _snapshot with { Menu = _snapshot.Menu with { Pending = new Dictionary<string, IpMenuDraft>() } };
        }
        Changed?.Invoke();
    }

    public Task ApplyMenuAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed();
        // Keep ordinary edits in insertion order; group indexed edits by section/row so RGB channels share one selection.
        var drafts = GetSnapshot().Menu.Pending.Values.OrderBy(draft => IpMenuCatalog.Get(draft.ControlId).IsIndexed ? 1 : 0)
            .ThenBy(draft => IpMenuCatalog.Get(draft.ControlId).IsIndexed ? IpMenuCatalog.Get(draft.ControlId).Section : "", StringComparer.Ordinal)
            .ThenBy(draft => IpMenuCatalog.Get(draft.ControlId).IsIndexed ? Array.IndexOf(IpMenuGrids.ForSection(IpMenuCatalog.Get(draft.ControlId).Section)!.Values.ToArray(), IpMenuCatalog.Get(draft.ControlId).IndexValue) : 0).ToArray();
        if (drafts.Length == 0) throw new InvalidOperationException("No pending settings to apply.");
        foreach (var draft in drafts) _ = IpMenuCatalog.ParseTarget(IpMenuCatalog.Get(draft.ControlId), draft.Target.ToString());
        var update = new IpMenuUpdate
        {
            Endpoint = profile.Endpoint,
            Input = drafts[0].Input,
            PictureMode = drafts[0].PictureMode,
            StartedAt = _timeProvider.GetUtcNow(),
            Steps = drafts.Select(draft => new IpMenuUpdateStep(draft.ControlId, draft.Original.DeepClone(), draft.Target.DeepClone())).ToArray()
        };
        await SaveMenuUpdateAsync(update).ConfigureAwait(false);
        IpMenuSelectorSession? selectorSession = null;
        for (var index = 0; index < drafts.Length; index++)
        {
            var draft = drafts[index]; var control = IpMenuCatalog.Get(draft.ControlId);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
                if ((GetSnapshot().Menu.Input ?? "") != draft.Input || (GetSnapshot().Menu.PictureMode ?? "") != draft.PictureMode)
                    throw new InvalidOperationException("The TV input or picture mode changed since editing. Refresh and enter the target again.");
                if (control.IsIndexed)
                {
                    var grid = IpMenuGrids.ForSection(control.Section)!;
                    selectorSession ??= await BeginMenuSelectorSessionAsync(profile, grid, cancellation).ConfigureAwait(false);
                    await MoveMenuSelectorAsync(profile, grid, selectorSession, control.IndexValue!, cancellation).ConfigureAwait(false);
                }
                await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
                await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                var before = GetSnapshot().Menu;
                if ((before.Input ?? "") != draft.Input || (before.PictureMode ?? "") != draft.PictureMode
                    || !JsonNode.DeepEquals(MenuPrerequisites(before, control), draft.Prerequisites))
                    throw new InvalidOperationException("The TV input, mode, interval or color changed since editing. Refresh and enter the target again.");
                var original = before.Value(control);
                if (original is null || (!EquivalentCommandValue(original, draft.Original) && !EquivalentCommandValue(original, draft.Target)))
                    throw new InvalidOperationException($"{control.Name} changed on the TV since editing. Refresh before applying; nothing was overwritten.");
                if (EquivalentCommandValue(original, draft.Target))
                {
                    update = MenuStep(update, index, "Already at target");
                    RemoveMenuDraft(draft.ControlId);
                    await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                    await FinishIndexedGroupAsync().ConfigureAwait(false);
                    continue;
                }
                var parameters = MenuWriteParameters(before, control, draft.Target);
                update = MenuStep(update, index, "Sending");
                await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var exchange = await _client.ExecuteCommandAsync(profile.Connection, control.Method, parameters, cancellationToken: cancellation).ConfigureAwait(false);
                await RecordExchangeAsync(profile, "Menu · apply " + control.Name, exchange).ConfigureAwait(false);
                IpMenuSnapshot? after = null;
                string? warning = null;
                if (!exchange.IsSuccess)
                {
                    // A correlated rejection permits a read-only check, never a blind retry or rollback.
                    if (!cancellation.IsCancellationRequested && exchange.Outcome == SamsungIpRemoteOutcome.RpcError && exchange.RpcErrorCode is -32002 or -32003 or -32602)
                    {
                        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
                        await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
                        await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                        var rejected = GetSnapshot().Menu;
                        if (EquivalentCommandValue(rejected.Value(control), draft.Original) && JsonNode.DeepEquals(before.Tv, rejected.Tv)
                            && JsonNode.DeepEquals(before.Video, rejected.Video) && JsonNode.DeepEquals(MenuPrerequisites(rejected, control), draft.Prerequisites)
                            && (control.Method != "WB2PointControl" || CompleteWhiteBalancePeersUnchanged(before, rejected, control.Field)))
                            update = MenuStep(update, index, "Rejected unchanged");
                        // Observed on the display: a WB2Point write can
                        // return -32002 after taking effect. Reuse the independent
                        // read, never the setter reply, and still run every normal
                        // context/other-field check below before accepting it.
                        if (control.Method == "WB2PointControl" && exchange.RpcErrorCode == -32002
                            && EquivalentCommandValue(rejected.Value(control), draft.Target)
                            && CompleteWhiteBalancePeersUnchanged(before, rejected, control.Field))
                        {
                            after = rejected;
                            warning = $"TV returned -32002, but independent readback confirmed {control.Name} = {draft.Target} with the other five white-balance channels and TV context unchanged. No retry was sent; the original error remains in Communication log.";
                        }
                    }
                    if (after is null)
                    {
                        if (update.Steps[index].Status == "Rejected unchanged")
                            throw new InvalidOperationException($"{control.Name}: the TV rejected the requested value {draft.Target} (error {exchange.RpcErrorCode}). Readback confirmed it is still {draft.Original}, with the checked settings/context unchanged. The controls remain available; correct or discard the pending change before applying again.");
                        RequireSuccess(exchange);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (after is null)
                {
                    await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
                    await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
                    await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                    after = GetSnapshot().Menu;
                }
                if (!EquivalentCommandValue(after.Value(control), draft.Target)) throw new InvalidOperationException($"{control.Name}: the TV readback did not match the requested value.");
                if (!control.ChangesContext && ((after.Input ?? "") != draft.Input || (after.PictureMode ?? "") != draft.PictureMode)
                    || !JsonNode.DeepEquals(MenuPrerequisites(after, control), draft.Prerequisites))
                    throw new InvalidOperationException("The TV context/selector changed during the update. Later settings were stopped.");
                if (control.Parameter.Kind == IpRemoteParameterKind.Integer
                    && (ChangedOutsideMenuField(before.Tv, after.Tv, control.Field) || ChangedOutsideMenuField(before.Video, after.Video, control.Field)))
                    throw new InvalidOperationException("Another reported setting changed unexpectedly. Later settings were stopped; check the TV before continuing.");
                if (control.Method == "WB2PointControl" && before.Readings[control.Method].Values!.Any(pair => pair.Key != control.Field && !EquivalentCommandValue(after.Readings[control.Method].Values?[pair.Key], pair.Value)))
                    throw new InvalidOperationException("Another white-balance channel changed unexpectedly. Later settings were stopped.");
                update = MenuStep(update, index, warning is null ? "Applied" : "Applied with TV warning", warning);
                RemoveMenuDraft(draft.ControlId);
                await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                // Input/picture context changes invalidate everything. A local
                // mode/selector only invalidates its own section, not unrelated
                // calibration values we have already read from the same display.
                if (control.RequiresSeparateApply || control.Method is "gammaModeControl" or "autoMotionPlusControl")
                    UpdateMenu(menu => InvalidateMenuControlContext(menu, control));
                await FinishIndexedGroupAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
            {
                if (selectorSession is not null) await StopMenuSelectorSessionAsync(error.Message).ConfigureAwait(false);
                update = MenuStep(update, index, update.Steps[index].Status == "Sending" ? "Uncertain" : update.Steps[index].Status == "Pending" ? "Not sent" : update.Steps[index].Status)
                    with
                { Status = "Stopped", Message = error.Message + " No retry or rollback was sent. Earlier confirmed changes remain on the TV." };
                UpdateMenu(menu => menu with { Update = update, Status = update.Message });
                try { await SaveMenuUpdateAsync(update).ConfigureAwait(false); }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
                { Update(state => state with { StorageWarning = "Could not save the update result. Keep the originals shown here and check the TV." }); }
                throw;
            }
            async Task FinishIndexedGroupAsync()
            {
                if (selectorSession is null || index + 1 < drafts.Length && IpMenuCatalog.Get(drafts[index + 1].ControlId).Section == selectorSession.Section) return;
                await RestoreMenuSelectorAsync(profile, selectorSession, cancellation).ConfigureAwait(false);
                selectorSession = null;
            }
        }
        var warnings = update.Steps.Count(step => step.Warning is not null);
        await SaveMenuUpdateAsync(update with
        {
            Status = warnings == 0 ? "Completed" : "Completed with TV warning",
            Message = warnings == 0 ? "Applied settings and confirmed them by query. No menu navigation or return-to-video keys were needed."
                : $"Applied settings and confirmed them by independent readback, with {warnings} TV warning(s). See the affected rows and Communication log. No command was retried."
        }).ConfigureAwait(false);
    });

    private static JsonObject MenuWriteParameters(IpMenuSnapshot before, IpMenuControl control, JsonNode target)
    {
        var parameters = new JsonObject();
        if (control.Method == "WB2PointControl")
        {
            // Partial requests can apply R-Gain and then fail, or reject later
            // channels without applying them. Send the complete six-field value
            // from this write's fresh preflight, never defaults or pending peers.
            var values = before.Readings.GetValueOrDefault(control.Method)?.Values;
            foreach (var field in control.Command.Parameters)
            {
                if (!UsableOriginal(field, values?[field.Name]))
                    throw new InvalidOperationException($"{control.Name}: a complete 2-point white-balance reading is required; {field.Name} is missing or invalid. Refresh the section. No setting was sent and no default was substituted.");
                parameters[field.Name] = values![field.Name]!.DeepClone();
            }
        }
        parameters[control.Field] = target.DeepClone();
        return parameters;
    }

    private static bool CompleteWhiteBalancePeersUnchanged(IpMenuSnapshot before, IpMenuSnapshot after, string changedField)
    {
        var original = before.Readings.GetValueOrDefault("WB2PointControl")?.Values;
        var actual = after.Readings.GetValueOrDefault("WB2PointControl")?.Values;
        return SamsungIpRemoteCommands.Get("WB2PointControl").Parameters.All(parameter =>
            UsableOriginal(parameter, original?[parameter.Name]) && UsableOriginal(parameter, actual?[parameter.Name])
            && (parameter.Name == changedField || EquivalentCommandValue(actual?[parameter.Name], original?[parameter.Name])));
    }

    private static bool ChangedOutsideMenuField(JsonObject before, JsonObject after, string field) => before.Select(pair => pair.Key).Union(after.Select(pair => pair.Key))
        .Any(key => key != field && !EquivalentCommandValue(before[key], after[key]));

    private static IpMenuUpdate MenuStep(IpMenuUpdate update, int index, string status, string? warning = null) => update with
    { Steps = update.Steps.Select((step, position) => position == index ? step with { Status = status, Warning = warning ?? step.Warning } : step).ToArray() };
    private void RemoveMenuDraft(string id) => UpdateMenu(menu => menu with { Pending = menu.Pending.Where(pair => pair.Key != id).ToDictionary() });

    public async Task CloseMenuUpdateReviewAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var update = GetSnapshot().Menu.Update;
            if (update?.NeedsReview != true) throw new InvalidOperationException("No interrupted menu update needs review.");
            await SaveMenuUpdateAsync(update with
            {
                Steps = update.Steps.Select(step => step.Status is "Sending" or "Uncertain" ? step with { Status = "Checked manually" } : step).ToArray(),
                Status = "Closed",
                Message = "User checked the TV. No command, restoration or verification was sent. Refresh before more edits."
            }).ConfigureAwait(false);
            UpdateMenu(menu => ClearMenuGridCache(menu) with { Readings = new Dictionary<string, IpMenuRead>(), SectionsRead = new Dictionary<string, DateTimeOffset>(), Pending = new Dictionary<string, IpMenuDraft>() });
        }
        finally { _gate.Release(); }
    }

    public async Task SaveMenuPreferencesAsync(bool applyImmediately)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            if (applyImmediately && GetSnapshot().Menu.Pending.Count > 0)
                throw new InvalidOperationException("Apply or discard pending changes before enabling immediate updates.");
            var preferences = GetSnapshot().Menu.Preferences with { ApplyImmediately = applyImmediately };
            await SaveMenuFileAsync(MenuPreferencesPath, preferences).ConfigureAwait(false);
            UpdateMenu(menu => menu with { Preferences = preferences });
        }
        finally { _gate.Release(); }
    }

    public Task SendMenuKeyAsync(string key) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed();
        SamsungIpRemoteCommands.Get("remoteKeyControl").Validate(new() { ["remoteKey"] = key }, false);
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, "remoteKeyControl", new() { ["remoteKey"] = key }, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Remote · " + key, exchange).ConfigureAwait(false);
        RequireSuccess(exchange);
        UpdateMenu(menu => menu with { Connected = key != "power" && menu.Connected, Status = "Sent " + key + ". Loaded settings kept; use Refresh when you want to reread the TV." });
    });

    private void EnsureMenuWritesAllowed(bool allowTemporaryWhiteBalanceRead = false)
    {
        EnsureNoPendingPictureTest(allowTemporaryWhiteBalanceRead);
        if (GetSnapshot().Menu.Update?.NeedsReview == true) throw new InvalidOperationException("Check the interrupted update before sending more commands.");
    }

    private async Task RunMenuOperationAsync(Func<IpRemoteProfile, CancellationToken, Task> action, bool needsConnection = true)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var state = GetSnapshot(); var profile = state.ActiveProfile ?? throw new InvalidOperationException("Choose or save a display first.");
            if (!state.HasToken || state.AuthorizationRejected) throw new InvalidOperationException("Pair with the display first. Its saved token is reused for Connect.");
            if (needsConnection && !state.Menu.Connected) throw new InvalidOperationException("Connect to the display first.");
            var cancellation = new CancellationTokenSource(); lock (_sync) _operation = cancellation;
            Update(snapshot => snapshot with { IsBusy = true });
            await action(profile, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
        { UpdateMenu(menu => menu with { Status = error is OperationCanceledException ? "Stopped. No further command was sent." : error.Message }); throw; }
        finally
        {
            lock (_sync) { _operation?.Dispose(); _operation = null; }
            Update(state => state with { IsBusy = false });
            _gate.Release();
        }
    }

    private static IpMenuSnapshot InvalidateMenuControlContext(IpMenuSnapshot menu, IpMenuControl control)
    {
        if (control.ChangesContext)
            return ClearMenuGridCache(menu) with { Readings = new Dictionary<string, IpMenuRead>(), SectionsRead = new Dictionary<string, DateTimeOffset>(), SettingsLoadedAt = null, LoadWarnings = [], ValuesRevision = menu.ValuesRevision + 1 };

        var methods = IpMenuCatalog.ForSection(control.Section).Select(item => item.Method).ToHashSet(StringComparer.Ordinal);
        return ClearMenuGridCache(menu, control.Section) with
        {
            // Keep the just-confirmed mode itself visible while its dependent
            // values reload. Other sections retain their values and timestamps.
            Readings = menu.Readings.Where(pair => pair.Key == control.Method || !methods.Contains(pair.Key)).ToDictionary(),
            SectionsRead = menu.SectionsRead.Where(pair => pair.Key != control.Section).ToDictionary()
        };
    }

    private async Task<SamsungIpRemoteExchange> MenuQueryAsync(IpRemoteProfile profile, string method, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, method, new(), query: true, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Menu · query " + method, exchange).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (exchange.IsSuccess) UpdateMenu(menu => menu with
        {
            LastContact = _timeProvider.GetUtcNow(),
            TransportStatus = exchange.ServerClosesConnection == true ? "The TV requested Connection: close; the next request needs a new connection."
                : exchange.NewTlsHandshake == false ? "Last request reused the existing TLS connection."
                : exchange.NewTlsHandshake == true ? "Last request established a new TLS connection. Keep-alive is enabled."
                : "HTTPS keep-alive is enabled; transport reuse was not reported."
        });
        return exchange;
    }

    private async Task ReadMenuBaseAsync(IpRemoteProfile profile, CancellationToken cancellation, bool includeVideo = true)
    {
        var tv = await MenuQueryAsync(profile, "getTVStates", cancellation).ConfigureAwait(false); RequireSuccess(tv);
        var video = includeVideo ? await MenuQueryAsync(profile, "getVideoStates", cancellation).ConfigureAwait(false) : null;
        if (video is not null) RequireSuccess(video);
        if (tv.Result is null || includeVideo && video?.Result is null) throw new InvalidOperationException("The TV did not return state objects.");
        var previous = GetSnapshot().Menu;
        var changed = !EquivalentCommandValue(previous.Tv["inputSource"], tv.Result["inputSource"])
            || !EquivalentCommandValue(previous.Tv["pictureMode"], tv.Result["pictureMode"]);
        UpdateMenu(menu => (changed ? ClearMenuGridCache(menu) : menu) with
        {
            Tv = (JsonObject)tv.Result.DeepClone(),
            Video = video?.Result is { } values ? (JsonObject)values.DeepClone() : changed ? new() : menu.Video,
            SettingsLoadedAt = changed ? null : menu.SettingsLoadedAt,
            ValuesRevision = changed ? menu.ValuesRevision + 1 : menu.ValuesRevision,
            Pending = changed ? new Dictionary<string, IpMenuDraft>() : menu.Pending,
            LoadWarnings = changed ? [] : menu.LoadWarnings,
            Readings = changed ? new Dictionary<string, IpMenuRead>() : menu.Readings,
            SectionsRead = changed ? new Dictionary<string, DateTimeOffset>() : menu.SectionsRead
        });
        foreach (var method in IpMenuCatalog.Controls.Select(control => control.Method).Distinct())
        {
            var readback = SamsungIpRemoteCommands.Get(method).ReadbackMethod;
            if (readback == "getTVStates") StoreMenuRead(method, tv);
            else if (readback == "getVideoStates" && video is not null) StoreMenuRead(method, video);
        }
    }

    private void StoreMenuRead(string method, SamsungIpRemoteExchange exchange, bool preserveGridCache = false)
    {
        var fields = exchange.IsSuccess ? CommandFields(SamsungIpRemoteCommands.Get(method), exchange.Result) : null;
        var values = fields is null ? null : (JsonObject)fields.DeepClone();
        var grid = IpMenuGrids.All.FirstOrDefault(item => item.ModeMethod == method);
        if (!preserveGridCache && grid is not null && !EquivalentCommandValue(GetSnapshot().Menu.Readings.GetValueOrDefault(method)?.Values?[grid.ModeField], values?[grid.ModeField]))
            UpdateMenu(menu => ClearMenuGridCache(menu, grid.Section));
        foreach (var control in IpMenuCatalog.Controls.Where(control => control.Method == method && control.Parameter.Kind == IpRemoteParameterKind.Integer))
            if (values?[control.Field] is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                && int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)) values[control.Field] = number;
        UpdateMenu(menu => menu with
        {
            Readings = new Dictionary<string, IpMenuRead>(menu.Readings)
            { [method] = new(_timeProvider.GetUtcNow(), values, exchange.Outcome, exchange.IsSuccess ? "Read from TV" : exchange.Message) { Payload = exchange.Payload?.DeepClone(), RpcErrorCode = exchange.RpcErrorCode } }
        });
    }

    private async Task ReadMenuControlAsync(IpRemoteProfile profile, IpMenuControl control, CancellationToken cancellation)
    {
        if (control.Command.ReadbackMethod is "getTVStates" or "getVideoStates") return;
        var exchange = await MenuQueryAsync(profile, control.Method, cancellation).ConfigureAwait(false);
        StoreMenuRead(control.Method, exchange); RequireSuccess(exchange);
        if (control.IsIndexed)
        {
            await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
            var prerequisites = MenuPrerequisites(GetSnapshot().Menu, control);
            if (prerequisites[IpMenuGrids.ForSection(control.Section)!.SelectorField]?.ToString() != control.IndexValue)
                throw new InvalidOperationException("The interval/color changed while reading. No value was assigned to the wrong row.");
            CacheMenuGridRead(control, GetSnapshot().Menu.Readings[control.Method]);
        }
    }
    private async Task ReadMenuPrerequisitesAsync(IpRemoteProfile profile, IpMenuControl control, CancellationToken cancellation)
    {
        foreach (var requirement in control.Command.Requirements)
        {
            var exchange = await MenuQueryAsync(profile, requirement.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(requirement.Method, exchange); RequireSuccess(exchange);
        }
    }
    private static JsonObject MenuPrerequisites(IpMenuSnapshot menu, IpMenuControl control, bool forEditing = false)
    {
        var result = new JsonObject();
        foreach (var requirement in control.Command.Requirements)
        {
            var value = forEditing && control.IsIndexed && requirement.Field == IpMenuGrids.ForSection(control.Section)!.SelectorField
                ? JsonValue.Create(control.IndexValue) : menu.Readings.GetValueOrDefault(requirement.Method)?.Values?[requirement.Field];
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || !requirement.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Requires {requirement.Field}: {string.Join(" / ", requirement.AllowedValues)}. Change that setting and refresh this section.");
            result[requirement.Field] = value.DeepClone();
        }
        return result;
    }

    private async Task SaveMenuUpdateAsync(IpMenuUpdate update)
    {
        await SaveMenuFileAsync(MenuUpdatePath, update).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Update = update, Status = update.Message.Length > 0 ? update.Message : $"Applying {update.Steps.Count(step => step.Status is "Applied" or "Applied with TV warning" or "Already at target")} / {update.Steps.Count}" });
    }
    private static async Task SaveMenuFileAsync<T>(string path, T data)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data, JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporary); File.Move(temporary, path, overwrite: true);
    }
    private async Task<IpMenuSnapshot> LoadMenuStateAsync()
    {
        var preferences = File.Exists(MenuPreferencesPath) ? JsonSerializer.Deserialize<IpMenuPreferences>(await File.ReadAllTextAsync(MenuPreferencesPath).ConfigureAwait(false)) ?? new() : new();
        preferences = preferences with { ExpertGroupOrder = IpExpertLayout.Normalize(preferences.ExpertGroupOrder) };
        var update = File.Exists(MenuUpdatePath) ? JsonSerializer.Deserialize<IpMenuUpdate>(await File.ReadAllTextAsync(MenuUpdatePath).ConfigureAwait(false)) : null;
        if (update is not null)
        {
            if (update.Steps is null || update.Steps.Count > IpMenuCatalog.AllControls.Count()) throw new JsonException("Invalid direct menu update journal.");
            foreach (var step in update.Steps)
            {
                if (step is null || !IpMenuCatalog.AllControls.Any(control => control.Id == step.ControlId) || step.Target is null || step.Original is null
                    || step.Status is not ("Pending" or "Sending" or "Uncertain" or "Applied" or "Applied with TV warning" or "Already at target" or "Rejected unchanged" or "Not sent" or "Checked manually")
                    || step.Status == "Applied with TV warning" && (string.IsNullOrWhiteSpace(step.Warning) || IpMenuCatalog.Get(step.ControlId).Method != "WB2PointControl"))
                    throw new JsonException("Invalid direct menu update step. Preserve the journal and inspect it before continuing.");
            }
            if (update.Status == "Running") update = update with { Status = "Stopped", Message = "The previous update was interrupted. Nothing was resumed. Read/check the TV before applying more changes." };
        }
        var selector = File.Exists(MenuSelectorPath) ? JsonSerializer.Deserialize<IpMenuSelectorSession>(await File.ReadAllTextAsync(MenuSelectorPath).ConfigureAwait(false)) : null;
        if (selector is not null)
        {
            var grid = IpMenuGrids.ForSection(selector.Section);
            if (grid is null || !grid.Values.Contains(selector.Original) || selector.Message is null) throw new JsonException("Invalid calibration selector journal.");
            if (selector.Status is not ("Restored" or "Stopped")) selector = selector with { Status = "Stopped", Message = "The previous selector operation was interrupted. Nothing was resumed. Load the grid again to read its current values." };
        }
        var whiteBalanceRead = File.Exists(WhiteBalanceReadPath)
            ? JsonSerializer.Deserialize<IpMenuWhiteBalanceRead>(await File.ReadAllTextAsync(WhiteBalanceReadPath).ConfigureAwait(false)) : null;
        if (whiteBalanceRead is not null)
        {
            if (string.IsNullOrWhiteSpace(whiteBalanceRead.Endpoint) || string.IsNullOrWhiteSpace(whiteBalanceRead.Input)
                || string.IsNullOrWhiteSpace(whiteBalanceRead.PictureMode) || whiteBalanceRead.Message is null
                || whiteBalanceRead.OriginalInterval is { } interval && !IpMenuGrids.ForSection("white20")!.Values.Contains(interval))
                throw new JsonException("Invalid temporary white-balance read journal. Preserve it and check the original display.");
            if (whiteBalanceRead.NeedsRestore) whiteBalanceRead = whiteBalanceRead with { Message = "A temporary 20-point read was interrupted. Its original mode was Off. Nothing resumed automatically; restore it below or confirm manual restoration." };
        }
        return new() { Preferences = preferences, Update = update, SelectorSession = selector, WhiteBalanceRead = whiteBalanceRead };
    }
}
