using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    // Optimized application grouping, NOT a JSON-RPC batch. The existing operation
    // gate owns the selector. Each channel still uses its documented setter and
    // independent getter; full TV context is checked at row boundaries.
    private async Task<(IpMenuUpdate Update, int LastIndex)> ApplyMenuRgbSectionAsync(IpRemoteProfile profile, CancellationToken cancellation,
        IpMenuUpdate update, IpMenuDraft[] drafts, int start, MenuNudgeBatch? queuedBatch)
    {
        var first = queuedBatch is null ? drafts[start] : NextMenuNudge(queuedBatch)!.Draft;
        var grid = IpMenuGrids.ForSection(IpMenuCatalog.Get(first.ControlId).Section)!;
        IpMenuSelectorSession? selector = null;
        MenuRgbWorkingGroup? group = null;
        var index = start;
        var activeIndex = start;
        try
        {
            while (true)
            {
                var entry = queuedBatch is null ? null : NextMenuNudge(queuedBatch);
                if (queuedBatch is null ? index >= drafts.Length : entry is null) break;
                var draft = entry?.Draft ?? drafts[index];
                var control = IpMenuCatalog.Get(draft.ControlId);
                if (!control.IsIndexed || control.Section != grid.Section) break;
                activeIndex = index;
                if (entry is not null)
                {
                    update = update with { Steps = [.. update.Steps, new(draft.ControlId, draft.Original.DeepClone(), draft.Target.DeepClone())] };
                    await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                if (group is null || group.Value != control.IndexValue)
                {
                    if (group is not null) await FinishGroupAsync().ConfigureAwait(false);
                    var current = update.QueryBeforeChange
                        ? await ReadMenuGridContextAsync(profile, grid, selector, cancellation).ConfigureAwait(false)
                        : CachedMenuSelector(grid);
                    var context = GetSnapshot().Menu;
                    if (context.Input != draft.Input || context.PictureMode != draft.PictureMode)
                        throw new InvalidOperationException("The TV input or picture mode changed since editing. No RGB value or selector was sent for this row.");
                    selector ??= new(grid.Section, profile.Endpoint, context.Input!, context.PictureMode!, current, LastConfirmed: current);
                    await SaveMenuSelectorSessionAsync(selector).ConfigureAwait(false);
                    // Fast mode must have real, previously queried peers before
                    // any selector/write. Never fill missing values with defaults.
                    var readings = update.QueryBeforeChange ? null : CachedMenuRgbValues(grid, control.IndexValue!);
                    await MoveMenuReadSelectorAsync(profile, grid, selector, current, control.IndexValue!, cancellation).ConfigureAwait(false);
                    if (update.QueryBeforeChange)
                    {
                        readings = await ReadMenuRgbValuesAsync(profile, grid, control.IndexValue!, cancellation).ConfigureAwait(false);
                        await ReadMenuPrerequisitesAsync(profile, control, cancellation).ConfigureAwait(false);
                    }
                    CheckRgbPrerequisites(control, draft);
                    var originals = new JsonObject(readings!.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value.Values![pair.Key]!.DeepClone())));
                    group = new(control.IndexValue!, update.RgbGroups.Count, (JsonObject)context.Tv.DeepClone(), (JsonObject)context.Video.DeepClone(), (JsonObject)originals.DeepClone());
                    update = update with { RgbGroups = [.. update.RgbGroups, new(grid.Section, group.Value, originals)] };
                    await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                    foreach (var rowControl in grid.Row(group.Value)) CacheMenuGridRead(rowControl, readings![rowControl.Field]);
                }
                else if (update.QueryBeforeChange)
                {
                    // Keep unsent targets mergeable during this fresh channel
                    // baseline read. Its compact mode/selector checks prevent
                    // overwriting a stale value or mislabeling another row.
                    await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                }
                var before = GetSnapshot().Menu;
                if (entry is not null)
                {
                    draft = ClaimMenuNudge(queuedBatch!, entry);
                    update = update with { Steps = update.Steps.Select((step, position) => position == index ? step with { Target = draft.Target.DeepClone() } : step).ToArray() };
                }
                CheckRgbPrerequisites(control, draft);
                var original = before.Value(control);
                if (original is null || !EquivalentCommandValue(original, draft.Original) && !EquivalentCommandValue(original, draft.Target))
                    throw new InvalidOperationException($"{control.Name} changed on the TV since editing. Refresh before applying; nothing was overwritten.");
                if (EquivalentCommandValue(original, draft.Target))
                {
                    group.Expected[control.Field] = draft.Target.DeepClone();
                    update = MenuStep(update, index, "Already at target");
                }
                else
                {
                    update = MenuStep(update, index, "Sending") with
                    { RgbGroups = update.RgbGroups.Select((row, position) => position == group.JournalIndex ? row with { WriteAttempted = true } : row).ToArray() };
                    await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    var exchange = await _client.ExecuteCommandAsync(profile.Connection, control.Method, new() { [control.Field] = draft.Target.DeepClone() }, cancellationToken: cancellation).ConfigureAwait(false);
                    await RecordExchangeAsync(profile, "Menu · apply " + control.Name, exchange).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    if (!exchange.IsSuccess)
                    {
                        if (exchange.Outcome == SamsungIpRemoteOutcome.RpcError && exchange.RpcErrorCode is -32002 or -32003 or -32602)
                        {
                            // No retry: only an independently unchanged whole
                            // row/context can resolve a correlated rejection.
                            await FinishGroupAsync().ConfigureAwait(false);
                            update = MenuStep(update, index, "Rejected unchanged");
                            await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                        }
                        RequireSuccess(exchange);
                    }
                    await ReadMenuControlAsync(profile, control, cancellation).ConfigureAwait(false);
                    CheckRgbPrerequisites(control, draft);
                    if (!EquivalentCommandValue(GetSnapshot().Menu.Value(control), draft.Target))
                        throw new InvalidOperationException($"{control.Name}: the TV readback did not match the requested value.");
                    group.Expected[control.Field] = draft.Target.DeepClone();
                    update = MenuStep(update, index, "Applied");
                }
                RemoveMenuDraft(draft.ControlId);
                await SaveMenuUpdateAsync(update).ConfigureAwait(false);
                if (entry is not null) CompleteMenuNudge(queuedBatch!, entry);
                index++;
            }
            if (group is not null) await FinishGroupAsync().ConfigureAwait(false);
            if (selector is not null && group is not null)
                await SaveMenuSelectorSessionAsync(selector with
                {
                    Requested = group.Value, LastConfirmed = group.Value, Status = "Retained",
                    Message = $"Left {group.Value} selected. Further edits reuse it unless the requested row or reported selection changes."
                }).ConfigureAwait(false);
            return (update, index - 1);
        }
        catch (Exception error) when (IsMenuGridError(error))
        {
            if (selector is not null) await StopMenuSelectorSessionAsync(error.Message).ConfigureAwait(false);
            var failedIndex = Math.Min(activeIndex, update.Steps.Count - 1);
            if (failedIndex >= 0)
                update = MenuStep(update, failedIndex, update.Steps[failedIndex].Status == "Sending" ? "Uncertain"
                    : update.Steps[failedIndex].Status == "Pending" ? "Not sent" : update.Steps[failedIndex].Status);
            update = update with { Status = "Stopped", Message = error.Message + " No retry, rollback, or automatic selector restoration was sent. Earlier delivered changes may remain on the TV; check the saved RGB originals." };
            UpdateMenu(menu => ClearMenuGridCache(menu, grid.Section) with { Update = update, Status = update.Message });
            try { await SaveMenuUpdateAsync(update).ConfigureAwait(false); }
            catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
            { Update(state => state with { StorageWarning = "Could not save the RGB update result. Preserve the displayed originals and check the TV before continuing." }); }
            throw;
        }

        async Task FinishGroupAsync()
        {
            var readings = await ReadMenuRgbValuesAsync(profile, grid, group!.Value, cancellation).ConfigureAwait(false);
            if (await ReadMenuGridContextAsync(profile, grid, selector, cancellation).ConfigureAwait(false) != group.Value
                || !JsonNode.DeepEquals(group.Tv, GetSnapshot().Menu.Tv) || !JsonNode.DeepEquals(group.Video, GetSnapshot().Menu.Video))
                throw new InvalidOperationException("The TV context/selector or another reported setting changed during the RGB group. Later settings were stopped.");
            foreach (var field in grid.Fields)
                if (!EquivalentCommandValue(readings[field].Values![field], group.Expected[field]))
                    throw new InvalidOperationException($"Final RGB check for {group.Value}: {field} differs from the expected value, including unchanged peers. Later settings were stopped.");
            foreach (var rowControl in grid.Row(group.Value)) CacheMenuGridRead(rowControl, readings[rowControl.Field]);
            update = update with { RgbGroups = update.RgbGroups.Select((row, position) => position == group.JournalIndex ? row with { Verified = true } : row).ToArray() };
            await SaveMenuUpdateAsync(update).ConfigureAwait(false);
        }
    }

    private void CheckRgbPrerequisites(IpMenuControl control, IpMenuDraft draft)
    {
        var menu = GetSnapshot().Menu;
        if (menu.Input != draft.Input || menu.PictureMode != draft.PictureMode || !JsonNode.DeepEquals(MenuPrerequisites(menu, control), draft.Prerequisites))
            throw new InvalidOperationException("The TV mode, interval or color changed since editing. No further RGB value was sent.");
    }

    private string CachedMenuSelector(IpMenuGrid grid)
    {
        var menu = GetSnapshot().Menu;
        var mode = menu.Readings.GetValueOrDefault(grid.ModeMethod);
        var selected = menu.Readings.GetValueOrDefault(grid.SelectorMethod);
        var value = selected?.Values?[grid.SelectorField]?.ToString();
        if (mode?.Outcome != SamsungIpRemoteOutcome.Success || mode.Values?[grid.ModeField]?.ToString() != grid.RequiredMode
            || selected?.Outcome != SamsungIpRemoteOutcome.Success || value is null || !grid.Values.Contains(value))
            throw new InvalidOperationException("A queried calibration mode and selector are required. Refresh this section; no selector or RGB value was sent.");
        return value;
    }

    private Dictionary<string, IpMenuRead> CachedMenuRgbValues(IpMenuGrid grid, string value)
    {
        var menu = GetSnapshot().Menu;
        var readings = new Dictionary<string, IpMenuRead>();
        foreach (var control in grid.Row(value))
        {
            var reading = menu.IndexedReadings.GetValueOrDefault(control.Id);
            if (reading?.Outcome != SamsungIpRemoteOutcome.Success || !UsableOriginal(control.Parameter, reading.Values?[control.Field]))
                throw new InvalidOperationException($"{value} {control.Name}: a previously queried RGB value is required. Refresh this section; no default was substituted.");
            readings[control.Field] = reading;
        }
        return readings;
    }

    private async Task<Dictionary<string, IpMenuRead>> ReadMenuRgbValuesAsync(IpRemoteProfile profile, IpMenuGrid grid, string value, CancellationToken cancellation)
    {
        var readings = new Dictionary<string, IpMenuRead>();
        foreach (var control in grid.Row(value))
        {
            var exchange = await MenuQueryAsync(profile, control.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(control.Method, exchange); RequireSuccess(exchange);
            var reading = GetSnapshot().Menu.Readings[control.Method];
            if (!UsableOriginal(control.Parameter, reading.Values?[control.Field]))
                throw new InvalidOperationException($"{value} {control.Name}: the TV did not report a usable RGB value. No default was substituted.");
            readings[control.Field] = reading;
        }
        return readings;
    }

    private sealed record MenuRgbWorkingGroup(string Value, int JournalIndex, JsonObject Tv, JsonObject Video, JsonObject Expected);
}
