using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private const int MaximumStateBytes = 256 * 1024;
    private static readonly JsonSerializerOptions StateJson = new(JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };
    public string SavedStatesDirectory => Path.Combine(_directory, "saved-states");
    private string StateRecallPath => Path.Combine(_directory, "state-recall.json");
    private bool _recallingState;
    private string SavedStatePath(Guid id) => id == Guid.Empty ? throw new ArgumentException("Choose a saved state.")
        : Path.Combine(SavedStatesDirectory, id.ToString("N") + ".json");

    public string? SaveMenuStateDisabledReason()
    {
        var state = GetSnapshot();
        if (state.IsBusy) return "Stop or finish the current operation first.";
        if (!state.Menu.Connected || state.ActiveProfile is null) return "Connect and read the TV values first.";
        if (state.Menu.Pending.Count > 0) return "Apply or discard pending edits first. Save captures TV readings, not unsent targets.";
        try { EnsureMenuWritesAllowed(); }
        catch (InvalidOperationException error) { return error.Message; }
        return string.IsNullOrWhiteSpace(state.Menu.Input) || string.IsNullOrWhiteSpace(state.Menu.PictureMode)
            ? "Refresh state to read the current input and picture mode." : null;
    }

    public async Task<Guid> SaveMenuStateAsync(string name)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (SaveMenuStateDisabledReason() is { } reason) throw new InvalidOperationException(reason);
                // Prevent concurrent staging while the immutable captured values are written.
                _snapshot = _snapshot with { IsBusy = true };
            }
            var snapshot = GetSnapshot();
            var saved = IpMenuSavedStates.Capture(name, snapshot.ActiveProfile!, snapshot.Menu, _timeProvider.GetUtcNow());
            if (snapshot.SavedStates.Any(item => item.Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase) && item.Context == saved.Context))
                throw new InvalidOperationException("A state with this name already exists for this context. Choose another name, or delete the old state first.");
            PrivateIpRemoteTokenStore.EnsurePrivateDirectory(SavedStatesDirectory);
            await SaveMenuFileAsync(SavedStatePath(saved.Id), saved).ConfigureAwait(false);
            Update(state => state with { SavedStates = state.SavedStates.Append(saved).OrderByDescending(item => item.SavedAt).ToArray() });
            return saved.Id;
        }
        finally { Update(state => state with { IsBusy = false }); _gate.Release(); }
    }

    public async Task DeleteMenuStateAsync(Guid id, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm deleting this saved state first.");
        await EnterAsync().ConfigureAwait(false);
        try
        {
            if (!GetSnapshot().SavedStates.Any(state => state.Id == id)) throw new InvalidOperationException("That saved state is no longer available.");
            File.Delete(SavedStatePath(id)); // Exact GUID filename, never a user-supplied path or name.
            Update(state => state with { SavedStates = state.SavedStates.Where(item => item.Id != id).ToArray() });
        }
        finally { _gate.Release(); }
    }

    public string? RecallMenuStateDisabledReason(IpMenuSavedState saved)
    {
        if (SaveMenuStateDisabledReason() is { } reason) return reason;
        var current = GetSnapshot();
        return saved.Context != IpMenuSavedContext.From(current.ActiveProfile!, current.Menu)
            ? "Select the saved display, input, picture mode and signal context, then Refresh state. Recall will not switch calibration banks for you."
            : null;
    }

    public Task RecallMenuStateAsync(Guid id, bool conditionsConfirmed) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed();
        if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the saved display/input/picture mode and physical HDMI signal before recall.");
        if (GetSnapshot().Menu.Pending.Count > 0) throw new InvalidOperationException("Apply or discard pending edits before recalling a state.");
        // Reload and validate the file before sending anything, not the UI's possibly stale copy.
        var saved = await ReadSavedStateAsync(SavedStatePath(id)).ConfigureAwait(false);
        if (saved.Id != id) throw new InvalidOperationException("The saved state ID does not match its filename.");
        RequireContext();
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        RequireContext();
        var recall = new IpMenuStateRecall(id, saved.Name, saved.Context, _timeProvider.GetUtcNow())
        {
            FinalModes = IpMenuGrids.All.Where(grid => saved.Values.ContainsKey(grid.ModeMethod + "/" + grid.ModeField))
                .ToDictionary(grid => grid.ModeField, grid => saved.Values[grid.ModeMethod + "/" + grid.ModeField]),
            Message = "Recalling saved TV settings…"
        };
        await SaveStateRecallAsync(recall).ConfigureAwait(false); // Durable before any setting or selector write.
        _recallingState = true;
        try
        {
            var controls = saved.Values.Keys.Select(IpMenuCatalog.Get).ToArray();
            foreach (var section in controls.Select(control => control.Section).Distinct())
            {
                await RefreshMenuSectionCoreAsync(profile, section, cancellation, loadingGrid: true).ConfigureAwait(false);
                RequireContext();
            }
            var gridModes = IpMenuGrids.All.Select(grid => grid.ModeMethod).ToHashSet(StringComparer.Ordinal);
            foreach (var control in IpMenuSavedStates.DependencyOrder(controls.Where(control => !control.IsIndexed && !gridModes.Contains(control.Method)
                && !control.Command.Requirements.Any(requirement => gridModes.Contains(requirement.Method)))))
            {
                cancellation.ThrowIfCancellationRequested();
                RequireContext();
                if (MenuValueDisabledReason(GetSnapshot().Menu, control) is { } unavailable)
                { Skip(control, unavailable); continue; }
                await ApplyValuesAsync([control]);
                if (!GetSnapshot().Menu.SectionsRead.ContainsKey(control.Section))
                    await RefreshMenuSectionCoreAsync(profile, control.Section, cancellation, loadingGrid: true).ConfigureAwait(false);
            }
            foreach (var grid in IpMenuGrids.All)
            {
                var modeControl = IpMenuCatalog.Get(grid.ModeMethod + "/" + grid.ModeField);
                var rows = grid.Values.SelectMany(grid.Row).Where(control => saved.Values.ContainsKey(control.Id)).ToArray();
                var auxiliary = controls.Where(control => !control.IsIndexed && control.Command.Requirements.Any(requirement => requirement.Method == grid.ModeMethod)).ToArray();
                var menu = GetSnapshot().Menu;
                var finalMode = saved.Values.GetValueOrDefault(modeControl.Id) ?? menu.Value(modeControl)?.ToString();
                if (rows.Length == 0 && auxiliary.Length == 0 && !saved.Values.ContainsKey(modeControl.Id)) continue;
                var unavailable = MenuValueDisabledReason(menu, modeControl);
                if (unavailable is not null || finalMode is null)
                {
                    foreach (var control in rows.Concat(auxiliary).Append(modeControl).Where(control => saved.Values.ContainsKey(control.Id)))
                        Skip(control, unavailable ?? "Calibration mode not reported.");
                    continue;
                }
                recall = recall with { FinalModes = new(recall.FinalModes) { [grid.ModeField] = finalMode } };
                await SaveStateRecallAsync(recall).ConfigureAwait(false);
                await ApplyTargetAsync(modeControl, rows.Length > 0 ? grid.RequiredMode : finalMode);
                if (!GetSnapshot().Menu.SectionsRead.ContainsKey(grid.Section))
                    await RefreshMenuSectionCoreAsync(profile, grid.Section, cancellation, loadingGrid: true).ConfigureAwait(false);
                foreach (var control in IpMenuSavedStates.DependencyOrder(auxiliary))
                {
                    if (MenuValueDisabledReason(GetSnapshot().Menu, control) is { } reason) { Skip(control, reason); continue; }
                    await ApplyValuesAsync([control]);
                    if (!GetSnapshot().Menu.SectionsRead.ContainsKey(grid.Section))
                        await RefreshMenuSectionCoreAsync(profile, grid.Section, cancellation, loadingGrid: true).ConfigureAwait(false);
                }
                if (rows.Length > 0)
                {
                    // Explicit recall authorizes temporary enablement. Stop never sends an implicit restore.
                    await RefreshMenuGridCoreAsync(profile, grid.Section, cancellation).ConfigureAwait(false);
                    RequireContext();
                    var availableRows = rows.Where(control =>
                    {
                        var reason = grid.Row(control.IndexValue!).Select(peer => MenuValueDisabledReason(GetSnapshot().Menu, peer)).FirstOrDefault(value => value is not null);
                        if (reason is null) return true;
                        Skip(control, "This RGB row is incomplete: " + reason); return false;
                    }).ToArray();
                    await ApplyValuesAsync(availableRows);
                }
                await ApplyTargetAsync(modeControl, finalMode);
                if (saved.Values.ContainsKey(modeControl.Id)) recall = recall with { Confirmed = recall.Confirmed + 1 };
                // Refresh the final mode without losing the final RGB values we just confirmed.
                // A switch back to Off/Auto intentionally invalidates its grid; reload on demand.
                if (!GetSnapshot().Menu.SectionsRead.ContainsKey(grid.Section))
                    await RefreshMenuSectionCoreAsync(profile, grid.Section, cancellation, loadingGrid: true).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            RequireContext();
            recall = recall with { Status = "Completed", Message = $"Recalled ‘{saved.Name}’: {recall.Confirmed} saved values confirmed; {recall.Skipped.Count} unavailable values skipped. Values missing when saved were not changed." };
            await SaveStateRecallAsync(recall).ConfigureAwait(false);
        }
        catch (Exception error) when (IsMenuGridError(error))
        {
            recall = recall with { Status = "Stopped", Message = $"Recall stopped: {error.Message} Earlier confirmed changes remain. Check the TV and the final calibration modes below; no retry, rollback, or automatic resume was sent." };
            Update(state => state with { StateRecall = recall });
            try { await SaveStateRecallAsync(recall).ConfigureAwait(false); }
            catch (Exception storage) when (storage is IOException or UnauthorizedAccessException)
            { Update(state => state with { StorageWarning = "Could not save recall progress. Check the TV before closing." }); }
            throw;
        }
        finally { _recallingState = false; }

        void RequireContext()
        {
            if (saved.Context != IpMenuSavedContext.From(profile, GetSnapshot().Menu))
                throw new InvalidOperationException("The saved display/input/picture mode or signal context does not match. Select the matching context and refresh first; no further settings were sent.");
        }
        void Skip(IpMenuControl control, string reason) => recall = recall with { Skipped = new(recall.Skipped) { [control.Id] = reason } };
        async Task ApplyValuesAsync(IpMenuControl[] values)
        {
            RequireContext();
            var menu = GetSnapshot().Menu;
            var drafts = values.Where(control => !EquivalentCommandValue(menu.Value(control), IpMenuCatalog.ParseTarget(control, saved.Values[control.Id])))
                .Select(control => Draft(control, saved.Values[control.Id], menu)).ToArray();
            if (drafts.Length > 0) await ApplyMenuCoreAsync(profile, cancellation, drafts).ConfigureAwait(false);
            recall = recall with { Confirmed = recall.Confirmed + values.Length };
            await SaveStateRecallAsync(recall).ConfigureAwait(false);
        }
        async Task ApplyTargetAsync(IpMenuControl control, string target)
        {
            cancellation.ThrowIfCancellationRequested(); RequireContext();
            var menu = GetSnapshot().Menu;
            if (EquivalentCommandValue(menu.Value(control), IpMenuCatalog.ParseTarget(control, target))) return;
            if (MenuValueDisabledReason(menu, control) is { } reason) throw new InvalidOperationException(reason);
            await ApplyMenuCoreAsync(profile, cancellation, [Draft(control, target, menu)]).ConfigureAwait(false);
        }
        static IpMenuDraft Draft(IpMenuControl control, string target, IpMenuSnapshot menu) => new(control.Id,
            IpMenuCatalog.ParseTarget(control, target), menu.Value(control)!.DeepClone(), MenuPrerequisites(menu, control, forEditing: true), menu.Input!, menu.PictureMode!);
    });

    public async Task CloseStateRecallReviewAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            if (GetSnapshot().StateRecall is not { NeedsReview: true } recall) return;
            if (GetSnapshot().Menu.Update?.NeedsReview == true)
                throw new InvalidOperationException("Check and close the interrupted Last update below first, then close this recall review.");
            await SaveStateRecallAsync(recall with { Status = "Reviewed", Message = "Recall checked manually. No automatic resume or restoration was sent. Refresh state before further adjustments." }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task SaveStateRecallAsync(IpMenuStateRecall recall)
    {
        await SaveMenuFileAsync(StateRecallPath, recall).ConfigureAwait(false);
        Update(state => state with { StateRecall = recall });
    }

    private static async Task<IpMenuSavedState> ReadSavedStateAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > MaximumStateBytes)
            throw new InvalidOperationException("The saved state is missing, linked, or larger than 256 KiB.");
        var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicates(document.RootElement);
        var saved = JsonSerializer.Deserialize<IpMenuSavedState>(json, StateJson) ?? throw new JsonException("The saved state is empty.");
        IpMenuSavedStates.Validate(saved);
        return saved;

        static void RejectDuplicates(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("The saved state contains a duplicate property.");
                RejectDuplicates(property.Value);
            }
        }
    }

    private async Task LoadSavedStatesAsync()
    {
        var saved = new List<IpMenuSavedState>();
        var invalid = 0;
        if (Directory.Exists(SavedStatesDirectory))
            foreach (var path in Directory.EnumerateFiles(SavedStatesDirectory, "*.json"))
            {
                try
                {
                    var state = await ReadSavedStateAsync(path).ConfigureAwait(false);
                    if (Path.GetFileNameWithoutExtension(path) != state.Id.ToString("N") || saved.Any(item => item.Id == state.Id))
                        throw new InvalidOperationException("Invalid saved-state filename.");
                    saved.Add(state);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { invalid++; }
            }
        var recall = File.Exists(StateRecallPath) ? JsonSerializer.Deserialize<IpMenuStateRecall>(await File.ReadAllTextAsync(StateRecallPath).ConfigureAwait(false), StateJson) : null;
        if (recall?.Status == "Running") recall = recall with { Status = "Stopped", Message = "Recall was interrupted in a previous session. Check the TV and calibration modes. Nothing was resumed on startup." };
        Update(state => state with { SavedStates = saved.OrderByDescending(item => item.SavedAt).ToArray(), StateRecall = recall,
            SavedStatesWarning = invalid == 0 ? null : $"{invalid} invalid saved-state file(s) were ignored. No files were changed; check the saved-states folder." });
    }
}
