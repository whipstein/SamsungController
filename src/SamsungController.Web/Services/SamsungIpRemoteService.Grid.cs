using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string MenuSelectorPath => Path.Combine(_directory, "menu-selector.json");

    private static IpMenuSnapshot ClearMenuGridCache(IpMenuSnapshot menu, string? section = null) => menu with
    {
        IndexedReadings = section is null ? new Dictionary<string, IpMenuRead>() : menu.IndexedReadings.Where(pair => IpMenuCatalog.Get(pair.Key).Section != section).ToDictionary(),
        GridsRead = section is null ? new Dictionary<string, DateTimeOffset>() : menu.GridsRead.Where(pair => pair.Key != section).ToDictionary()
    };

    /// <summary>Read every indexed RGB row. Only selectors are moved, never modes or RGB values.</summary>
    public Task RefreshMenuGridAsync(string section) => RunMenuOperationAsync((profile, cancellation) => RefreshMenuGridCoreAsync(profile, section, cancellation));

    private async Task RefreshMenuGridCoreAsync(IpRemoteProfile profile, string section, CancellationToken cancellation, bool sectionAlreadyRead = false, bool temporaryWhiteBalanceRead = false)
    {
        var started = Stopwatch.GetTimestamp();
        EnsureMenuWritesAllowed(temporaryWhiteBalanceRead); // Reading all rows moves a selector; an unresolved RGB write must be handled first.
        var grid = IpMenuGrids.ForSection(section) ?? throw new ArgumentException("Unknown calibration grid.");
        UpdateMenu(menu => ClearMenuGridCache(menu, section));
        if (!sectionAlreadyRead) await RefreshMenuSectionCoreAsync(profile, section, cancellation, loadingGrid: true).ConfigureAwait(false);
        if (GetSnapshot().Menu.Readings.GetValueOrDefault(grid.ModeMethod)?.Values?[grid.ModeField]?.ToString() != grid.RequiredMode)
        {
            UpdateMenu(menu => menu with { Status = $"Set {grid.ModeField} to {grid.RequiredMode} and Apply to load all rows. No mode or RGB value was changed." });
            return;
        }
        IpMenuSelectorSession? session = null;
        try
        {
            session = await BeginMenuSelectorSessionAsync(profile, grid, cancellation).ConfigureAwait(false);
            if (temporaryWhiteBalanceRead)
                await SaveWhiteBalanceReadAsync(GetSnapshot().Menu.WhiteBalanceRead! with { OriginalInterval = session.Original }).ConfigureAwait(false);
            var current = session.Original;
            for (var index = 0; index < grid.Values.Count; index++)
            {
                var value = grid.Values[index];
                UpdateMenu(menu => menu with { Status = $"Reading {value} · row {index + 1} of {grid.Values.Count}. RGB values are unchanged." });
                // The previous row's final context check is this row's preflight.
                // Only selector writes happen here. RGB Apply keeps its full,
                // independent pre/post-write checks in MoveMenuSelectorAsync.
                await MoveMenuReadSelectorAsync(profile, grid, session, current, value, cancellation).ConfigureAwait(false);
                var readings = new Dictionary<string, IpMenuRead>();
                foreach (var control in grid.Row(value))
                {
                    var exchange = await MenuQueryAsync(profile, control.Method, cancellation).ConfigureAwait(false);
                    StoreMenuRead(control.Method, exchange);
                    if (IsConnectionFailure(exchange.Outcome)) RequireSuccess(exchange);
                    readings[control.Id] = GetSnapshot().Menu.Readings[control.Method];
                }
                // Do not label a response as belonging to a row if another controller moved its selector/context.
                current = await ReadMenuGridContextAsync(profile, grid, session, cancellation, readingGrid: true).ConfigureAwait(false);
                if (current != value)
                    throw new InvalidOperationException("The interval/color changed during the read. Load the grid again; no RGB setting was sent.");
                UpdateMenu(menu => menu with { IndexedReadings = menu.IndexedReadings.Concat(readings).ToDictionary() });
            }
            await RestoreMenuSelectorAsync(profile, session, cancellation).ConfigureAwait(false);
            var completed = $"Read all {grid.Values.Count} rows in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s. Original selector {session.Original} restored; RGB values unchanged.";
            await SaveMenuSelectorSessionAsync(GetSnapshot().Menu.SelectorSession! with { Message = completed }).ConfigureAwait(false);
            UpdateMenu(menu => menu with
            {
                GridsRead = new Dictionary<string, DateTimeOffset>(menu.GridsRead) { [section] = _timeProvider.GetUtcNow() },
                Status = completed
            });
        }
        catch (Exception error) when (IsMenuGridError(error))
        {
            UpdateMenu(menu => ClearMenuGridCache(menu, section));
            if (session is not null) await StopMenuSelectorSessionAsync(error.Message).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsMenuGridError(Exception error) => error is InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException;

    private void CacheMenuGridRead(IpMenuControl control, IpMenuRead reading) => UpdateMenu(menu => menu with
    {
        IndexedReadings = new Dictionary<string, IpMenuRead>(menu.IndexedReadings)
        { [control.Id] = reading with { Values = reading.Values is null ? null : (JsonObject)reading.Values.DeepClone() } }
    });

    private async Task<IpMenuSelectorSession> BeginMenuSelectorSessionAsync(IpRemoteProfile profile, IpMenuGrid grid, CancellationToken cancellation)
    {
        var original = await ReadMenuGridContextAsync(profile, grid, null, cancellation).ConfigureAwait(false);
        var menu = GetSnapshot().Menu;
        var session = new IpMenuSelectorSession(grid.Section, profile.Endpoint, menu.Input!, menu.PictureMode!, original, LastConfirmed: original);
        await SaveMenuSelectorSessionAsync(session).ConfigureAwait(false);
        return session;
    }

    private async Task<string> ReadMenuGridContextAsync(IpRemoteProfile profile, IpMenuGrid grid, IpMenuSelectorSession? session, CancellationToken cancellation, bool readingGrid = false)
    {
        // Input/picture mode come from getTVStates. getVideoStates contains
        // ordinary picture values, not the indexed RGB row or its context.
        await ReadMenuBaseAsync(profile, cancellation, includeVideo: !readingGrid).ConfigureAwait(false);
        foreach (var method in new[] { grid.ModeMethod, grid.SelectorMethod })
        {
            var exchange = await MenuQueryAsync(profile, method, cancellation).ConfigureAwait(false);
            StoreMenuRead(method, exchange); RequireSuccess(exchange);
        }
        var menu = GetSnapshot().Menu;
        if (string.IsNullOrWhiteSpace(menu.Input) || string.IsNullOrWhiteSpace(menu.PictureMode))
            throw new InvalidOperationException("The TV did not report its input/picture mode. No selector or RGB value was sent.");
        if (session is not null && (session.Endpoint != profile.Endpoint || menu.Input != session.Input || menu.PictureMode != session.PictureMode))
            throw new InvalidOperationException("Display input/picture mode changed during calibration. No further selector or RGB commands will be sent.");
        if (menu.Readings.GetValueOrDefault(grid.ModeMethod)?.Values?[grid.ModeField]?.ToString() != grid.RequiredMode)
            throw new InvalidOperationException($"Requires {grid.ModeField}: {grid.RequiredMode}. The app will not enable it automatically.");
        var value = menu.Readings.GetValueOrDefault(grid.SelectorMethod)?.Values?[grid.SelectorField]?.ToString();
        if (value is null || !grid.Values.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException("The TV did not report a documented interval/color. No selector value was guessed.");
        return value;
    }

    private async Task MoveMenuReadSelectorAsync(IpRemoteProfile profile, IpMenuGrid grid, IpMenuSelectorSession session, string current, string target, CancellationToken cancellation)
    {
        if (current == target) return;
        await SaveMenuSelectorSessionAsync(session with { Requested = target, LastConfirmed = current, Status = "Sending" }).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, grid.SelectorMethod, new() { [grid.SelectorField] = target }, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Calibration · select " + target, exchange).ConfigureAwait(false);
        RequireSuccess(exchange);
        // A setter acknowledgment is not evidence that the selector changed.
        var selected = await MenuQueryAsync(profile, grid.SelectorMethod, cancellation).ConfigureAwait(false);
        StoreMenuRead(grid.SelectorMethod, selected); RequireSuccess(selected);
        if (selected.Result?[grid.SelectorField]?.ToString() != target)
            throw new InvalidOperationException($"The TV did not select {target}. No RGB command was sent to that row.");
        UpdateMenu(menu => menu with { Readings = menu.Readings.Where(pair => !grid.Fields.Any(field => pair.Key == field + "Control")).ToDictionary() });
        await SaveMenuSelectorSessionAsync(session with { Requested = target, LastConfirmed = target, Status = "Selected" }).ConfigureAwait(false);
    }

    private async Task MoveMenuSelectorAsync(IpRemoteProfile profile, IpMenuGrid grid, IpMenuSelectorSession session, string target, CancellationToken cancellation, bool restoring = false)
    {
        if (!grid.Values.Contains(target, StringComparer.Ordinal)) throw new ArgumentException("Unknown calibration row.");
        var current = await ReadMenuGridContextAsync(profile, grid, session, cancellation).ConfigureAwait(false);
        if (current != target)
        {
            await SaveMenuSelectorSessionAsync(session with { Requested = target, LastConfirmed = current, Status = restoring ? "Restoring" : "Sending" }).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var exchange = await _client.ExecuteCommandAsync(profile.Connection, grid.SelectorMethod, new() { [grid.SelectorField] = target }, cancellationToken: cancellation).ConfigureAwait(false);
            await RecordExchangeAsync(profile, "Calibration · " + (restoring ? "restore " : "select ") + target, exchange).ConfigureAwait(false);
            RequireSuccess(exchange);
            cancellation.ThrowIfCancellationRequested();
            if (await ReadMenuGridContextAsync(profile, grid, session, cancellation).ConfigureAwait(false) != target)
                throw new InvalidOperationException($"The TV did not select {target}. No RGB command was sent to that row.");
            // The shared getters now address a different row; keep only the separately keyed grid cache.
            UpdateMenu(menu => menu with { Readings = menu.Readings.Where(pair => !grid.Fields.Any(field => pair.Key == field + "Control")).ToDictionary() });
        }
        await SaveMenuSelectorSessionAsync(session with
        {
            Requested = target,
            LastConfirmed = target,
            Status = restoring ? "Restored" : "Selected",
            Message = restoring ? $"Original selector {session.Original} restored." : $"Selected {target}; RGB values were not changed by this selector command."
        }).ConfigureAwait(false);
    }

    private Task RestoreMenuSelectorAsync(IpRemoteProfile profile, IpMenuSelectorSession session, CancellationToken cancellation) =>
        MoveMenuSelectorAsync(profile, IpMenuGrids.ForSection(session.Section)!, session, session.Original, cancellation, restoring: true);

    private async Task SaveMenuSelectorSessionAsync(IpMenuSelectorSession session)
    {
        await SaveMenuFileAsync(MenuSelectorPath, session).ConfigureAwait(false);
        UpdateMenu(menu => menu with { SelectorSession = session });
    }

    private async Task StopMenuSelectorSessionAsync(string message)
    {
        if (GetSnapshot().Menu.SelectorSession is not { } session) return;
        var stopped = session with { Status = "Stopped", Message = $"Stopped: {message} Original selector: {session.Original}; last confirmed: {session.LastConfirmed ?? "unknown"}. No automatic restoration/retry was sent. Reload before continuing." };
        UpdateMenu(menu => ClearMenuGridCache(menu, session.Section) with { SelectorSession = stopped });
        try { await SaveMenuSelectorSessionAsync(stopped).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Update(state => state with { StorageWarning = "The selector journal could not be saved. Nothing will resume automatically; read the TV before continuing." }); }
    }
}
