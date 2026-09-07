using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string MenuUpdatePath => Path.Combine(_directory, "menu-update.json");
    private string MenuPreferencesPath => Path.Combine(_directory, "menu-preferences.json");
    private static IpMenuSnapshot ResetMenu(IpMenuSnapshot previous) => new() { Preferences = previous.Preferences, Update = previous.Update };
    private static bool IsConnectionFailure(SamsungIpRemoteOutcome outcome) => outcome is SamsungIpRemoteOutcome.NotPaired or SamsungIpRemoteOutcome.Unauthorized
        or SamsungIpRemoteOutcome.TransportError or SamsungIpRemoteOutcome.CertificateError or SamsungIpRemoteOutcome.Timeout or SamsungIpRemoteOutcome.HttpError;
    private void UpdateMenu(Func<IpMenuSnapshot, IpMenuSnapshot> change) => Update(state => state with { Menu = change(state.Menu) });

    public Task ConnectMenuAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        UpdateMenu(menu => ResetMenu(menu) with { Status = "Connecting and reading TV values…" });
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Connected = true, SessionId = Guid.NewGuid(), Status = "Connected. Current values come from TV queries; documented controls need no verification." });
        // Optional identity: failure of a model-specific getter is not a failure of the two base reads.
        var identity = await MenuQueryAsync(profile, "getDeviceInformation", cancellation).ConfigureAwait(false);
        StoreMenuRead("getDeviceInformation", identity);
        if (IsConnectionFailure(identity.Outcome)) RequireSuccess(identity);
    }, needsConnection: false);

    public async Task DisconnectMenuAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try { UpdateMenu(menu => ResetMenu(menu) with { Status = "Disconnected locally. The TV and saved token were not changed." }); }
        finally { _gate.Release(); }
    }

    public Task RefreshMenuSectionAsync(string section) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        if (!IpMenuCatalog.Sections.Any(item => item.Id == section)) throw new ArgumentException("Unknown settings section.");
        var methods = IpMenuCatalog.ForSection(section).Select(control => control.Method).Distinct().ToArray();
        UpdateMenu(menu => menu with
        {
            Readings = menu.Readings.Where(pair => !methods.Contains(pair.Key)).ToDictionary(),
            SectionsRead = menu.SectionsRead.Where(pair => pair.Key != section).ToDictionary(),
            Status = "Reading " + IpMenuCatalog.Sections.Single(item => item.Id == section).Name + "…"
        });
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        foreach (var method in methods)
        {
            cancellation.ThrowIfCancellationRequested();
            var command = SamsungIpRemoteCommands.Get(method);
            if (command.ReadbackMethod is "getTVStates" or "getVideoStates") continue;
            var exchange = await MenuQueryAsync(profile, method, cancellation).ConfigureAwait(false);
            StoreMenuRead(method, exchange);
            if (IsConnectionFailure(exchange.Outcome) || exchange.Outcome == SamsungIpRemoteOutcome.Canceled) RequireSuccess(exchange);
        }
        UpdateMenu(menu => menu with
        {
            SectionsRead = new Dictionary<string, DateTimeOffset>(menu.SectionsRead) { [section] = _timeProvider.GetUtcNow() },
            Status = "TV values refreshed. Unreported or unavailable controls are not filled with defaults."
        });
    });

    public string? MenuControlDisabledReason(IpMenuControl control)
    {
        var snapshot = GetSnapshot(); var menu = snapshot.Menu;
        if (!menu.Connected) return "Connect to the TV first.";
        if (snapshot.IsBusy) return "A TV request is running.";
        if (menu.Update?.NeedsReview == true) return "Review the interrupted update before applying more changes.";
        if (menu.Value(control) is not { } value) return menu.Readings.GetValueOrDefault(control.Method) is { } reading
            ? reading.Outcome == SamsungIpRemoteOutcome.Success ? "Not reported in the TV reply for this display/state." : reading.Message
            : "Read this section to get the current TV value.";
        if (string.IsNullOrWhiteSpace(menu.Input) || string.IsNullOrWhiteSpace(menu.PictureMode)) return "The TV did not report its input/picture mode. Refresh before editing settings.";
        if (!UsableOriginal(control.Parameter, value)) return "The returned value does not match the documented control type/range. Inspect diagnostics; no default was substituted.";
        try { _ = MenuPrerequisites(menu, control); }
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
            if ((control.ChangesContext || control.IsSelector) && pending.Keys.Any(id => id != controlId)
                || pending.Values.Any(draft => draft.ControlId != controlId && (IpMenuCatalog.Get(draft.ControlId).ChangesContext || IpMenuCatalog.Get(draft.ControlId).IsSelector)))
                throw new InvalidOperationException("Apply or discard other pending changes before changing the input, mode, interval or color selector.");
            if (EquivalentCommandValue(menu.Value(control), target)) pending.Remove(controlId);
            else pending[controlId] = new(controlId, target, menu.Value(control)!.DeepClone(), MenuPrerequisites(menu, control), menu.Input ?? "", menu.PictureMode ?? "");
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
        var drafts = GetSnapshot().Menu.Pending.Values.ToArray();
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
        for (var index = 0; index < drafts.Length; index++)
        {
            var draft = drafts[index]; var control = IpMenuCatalog.Get(draft.ControlId);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
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
                    continue;
                }
                update = MenuStep(update, index, "Sending");
                await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var exchange = await _client.ExecuteCommandAsync(profile.Connection, control.Method, new() { [control.Field] = draft.Target.DeepClone() }, cancellationToken: cancellation).ConfigureAwait(false);
                await RecordExchangeAsync(profile, "Menu · apply " + control.Name, exchange).ConfigureAwait(false);
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
                            && JsonNode.DeepEquals(before.Video, rejected.Video) && JsonNode.DeepEquals(MenuPrerequisites(rejected, control), draft.Prerequisites))
                            update = MenuStep(update, index, "Rejected unchanged");
                    }
                    RequireSuccess(exchange);
                }
                cancellation.ThrowIfCancellationRequested();
                await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
                await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
                await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                var after = GetSnapshot().Menu;
                if (!EquivalentCommandValue(after.Value(control), draft.Target)) throw new InvalidOperationException($"{control.Name}: the TV readback did not match the requested value.");
                if (!control.ChangesContext && ((after.Input ?? "") != draft.Input || (after.PictureMode ?? "") != draft.PictureMode)
                    || !JsonNode.DeepEquals(MenuPrerequisites(after, control), draft.Prerequisites))
                    throw new InvalidOperationException("The TV context/selector changed during the update. Later settings were stopped.");
                if (control.Parameter.Kind == IpRemoteParameterKind.Integer
                    && (ChangedOutsideMenuField(before.Tv, after.Tv, control.Field) || ChangedOutsideMenuField(before.Video, after.Video, control.Field)))
                    throw new InvalidOperationException("Another reported setting changed unexpectedly. Later settings were stopped; check the TV before continuing.");
                if (control.Method == "WB2PointControl" && before.Readings[control.Method].Values!.Any(pair => pair.Key != control.Field && !EquivalentCommandValue(after.Readings[control.Method].Values?[pair.Key], pair.Value)))
                    throw new InvalidOperationException("Another white-balance channel changed unexpectedly. Later settings were stopped.");
                update = MenuStep(update, index, "Applied");
                RemoveMenuDraft(draft.ControlId);
                await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                // A mode/selector can change which values subsequent getters expose. Never carry them into its new context.
                if (control.ChangesContext || control.IsSelector || control.Method is "WB20PointModeControl" or "colorSpaceControl" or "gammaModeControl" or "autoMotionPlusControl")
                    UpdateMenu(menu => menu with { Readings = new Dictionary<string, IpMenuRead>(), SectionsRead = new Dictionary<string, DateTimeOffset>() });
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
            {
                update = MenuStep(update, index, update.Steps[index].Status == "Sending" ? "Uncertain" : update.Steps[index].Status == "Rejected unchanged" ? "Rejected unchanged" : "Not sent")
                    with
                { Status = "Stopped", Message = error.Message + " No retry or rollback was sent. Earlier confirmed changes remain on the TV." };
                UpdateMenu(menu => menu with { Update = update, Status = update.Message });
                try { await SaveMenuUpdateAsync(update).ConfigureAwait(false); }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
                { Update(state => state with { StorageWarning = "Could not save the update result. Keep the originals shown here and check the TV." }); }
                throw;
            }
        }
        await SaveMenuUpdateAsync(update with { Status = "Completed", Message = "Applied settings and confirmed them by query. No menu navigation or return-to-video keys were needed." }).ConfigureAwait(false);
    });

    private static bool ChangedOutsideMenuField(JsonObject before, JsonObject after, string field) => before.Select(pair => pair.Key).Union(after.Select(pair => pair.Key))
        .Any(key => key != field && !EquivalentCommandValue(before[key], after[key]));

    private static IpMenuUpdate MenuStep(IpMenuUpdate update, int index, string status) => update with
    { Steps = update.Steps.Select((step, position) => position == index ? step with { Status = status } : step).ToArray() };
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
            UpdateMenu(menu => menu with { Readings = new Dictionary<string, IpMenuRead>(), SectionsRead = new Dictionary<string, DateTimeOffset>(), Pending = new Dictionary<string, IpMenuDraft>() });
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
            var preferences = new IpMenuPreferences(applyImmediately);
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
        UpdateMenu(menu => menu with { Connected = key != "power" && menu.Connected, Readings = new Dictionary<string, IpMenuRead>(), SectionsRead = new Dictionary<string, DateTimeOffset>() });
        RequireSuccess(exchange);
        UpdateMenu(menu => menu with { Status = "Sent " + key + ". Refresh Menu for current settings." });
    });

    private void EnsureMenuWritesAllowed()
    {
        EnsureNoPendingPictureTest();
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

    private async Task<SamsungIpRemoteExchange> MenuQueryAsync(IpRemoteProfile profile, string method, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, method, new(), query: true, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Menu · query " + method, exchange).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (exchange.IsSuccess) UpdateMenu(menu => menu with { LastContact = _timeProvider.GetUtcNow() });
        return exchange;
    }

    private async Task ReadMenuBaseAsync(IpRemoteProfile profile, CancellationToken cancellation)
    {
        var tv = await MenuQueryAsync(profile, "getTVStates", cancellation).ConfigureAwait(false); RequireSuccess(tv);
        var video = await MenuQueryAsync(profile, "getVideoStates", cancellation).ConfigureAwait(false); RequireSuccess(video);
        if (tv.Result is null || video.Result is null) throw new InvalidOperationException("The TV did not return state objects.");
        var previous = GetSnapshot().Menu;
        var changed = !EquivalentCommandValue(previous.Tv["inputSource"], tv.Result["inputSource"])
            || !EquivalentCommandValue(previous.Tv["pictureMode"], tv.Result["pictureMode"]);
        UpdateMenu(menu => menu with
        {
            Tv = (JsonObject)tv.Result.DeepClone(),
            Video = (JsonObject)video.Result.DeepClone(),
            Readings = changed ? new Dictionary<string, IpMenuRead>() : menu.Readings,
            SectionsRead = changed ? new Dictionary<string, DateTimeOffset>() : menu.SectionsRead
        });
        foreach (var method in IpMenuCatalog.Controls.Select(control => control.Method).Distinct())
        {
            var readback = SamsungIpRemoteCommands.Get(method).ReadbackMethod;
            if (readback is "getTVStates" or "getVideoStates") StoreMenuRead(method, readback == "getTVStates" ? tv : video);
        }
    }

    private void StoreMenuRead(string method, SamsungIpRemoteExchange exchange)
    {
        var fields = exchange.IsSuccess ? CommandFields(SamsungIpRemoteCommands.Get(method), exchange.Result) : null;
        var values = fields is null ? null : (JsonObject)fields.DeepClone();
        foreach (var control in IpMenuCatalog.Controls.Where(control => control.Method == method && control.Parameter.Kind == IpRemoteParameterKind.Integer))
            if (values?[control.Field] is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                && int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)) values[control.Field] = number;
        UpdateMenu(menu => menu with
        {
            Readings = new Dictionary<string, IpMenuRead>(menu.Readings)
            { [method] = new(_timeProvider.GetUtcNow(), values, exchange.Outcome, exchange.IsSuccess ? "Read from TV" : exchange.Message) }
        });
    }

    private async Task ReadMenuControlAsync(IpRemoteProfile profile, IpMenuControl control, CancellationToken cancellation)
    {
        if (control.Command.ReadbackMethod is "getTVStates" or "getVideoStates") return;
        var exchange = await MenuQueryAsync(profile, control.Method, cancellation).ConfigureAwait(false);
        StoreMenuRead(control.Method, exchange); RequireSuccess(exchange);
    }
    private async Task ReadMenuPrerequisitesAsync(IpRemoteProfile profile, IpMenuControl control, CancellationToken cancellation)
    {
        foreach (var requirement in control.Command.Requirements)
        {
            var exchange = await MenuQueryAsync(profile, requirement.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(requirement.Method, exchange); RequireSuccess(exchange);
        }
    }
    private static JsonObject MenuPrerequisites(IpMenuSnapshot menu, IpMenuControl control)
    {
        var result = new JsonObject();
        foreach (var requirement in control.Command.Requirements)
        {
            var value = menu.Readings.GetValueOrDefault(requirement.Method)?.Values?[requirement.Field];
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || !requirement.AllowedValues.Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Requires {requirement.Field}: {string.Join(" / ", requirement.AllowedValues)}. Change that setting and refresh this section.");
            result[requirement.Field] = value.DeepClone();
        }
        return result;
    }

    private async Task SaveMenuUpdateAsync(IpMenuUpdate update)
    {
        await SaveMenuFileAsync(MenuUpdatePath, update).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Update = update, Status = update.Message.Length > 0 ? update.Message : $"Applying {update.Steps.Count(step => step.Status is "Applied" or "Already at target")} / {update.Steps.Count}" });
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
        var update = File.Exists(MenuUpdatePath) ? JsonSerializer.Deserialize<IpMenuUpdate>(await File.ReadAllTextAsync(MenuUpdatePath).ConfigureAwait(false)) : null;
        if (update is not null)
        {
            if (update.Steps is null || update.Steps.Count > IpMenuCatalog.Controls.Count) throw new JsonException("Invalid direct menu update journal.");
            foreach (var step in update.Steps)
            {
                if (step is null || !IpMenuCatalog.Controls.Any(control => control.Id == step.ControlId) || step.Target is null || step.Original is null
                    || step.Status is not ("Pending" or "Sending" or "Uncertain" or "Applied" or "Already at target" or "Rejected unchanged" or "Not sent" or "Checked manually"))
                    throw new JsonException("Invalid direct menu update step. Preserve the journal and inspect it before continuing.");
            }
            if (update.Status == "Running") update = update with { Status = "Stopped", Message = "The previous update was interrupted. Nothing was resumed. Read/check the TV before applying more changes." };
        }
        return new() { Preferences = preferences, Update = update };
    }
}
