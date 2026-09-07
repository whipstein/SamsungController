using System.Diagnostics;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private async Task LoadAllMenuSettingsAsync(IpRemoteProfile profile, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        var original = GetSnapshot().Menu;
        var warnings = new List<string>();
        UpdateMenu(menu => menu with { SettingsLoadedAt = null, LoadWarnings = [] });
        foreach (var section in IpMenuCatalog.Sections)
        {
            UpdateMenu(menu => menu with { Status = "Connecting · reading " + section.Name + "…" });
            await RefreshMenuSectionCoreAsync(profile, section.Id, cancellation, loadingGrid: true, refreshBase: false).ConfigureAwait(false);
        }
        // Include documented read/list methods that do not have an editable
        // Menu card. Never invoke write-only keys, resets or calibration starts.
        foreach (var command in SamsungIpRemoteCommands.All.Where(command => command.CanQuery))
        {
            if (GetSnapshot().Menu.Readings.ContainsKey(command.Method)
                || command.Method is "getTVStates" or "getVideoStates"
                || IpMenuGrids.All.Any(grid => grid.Fields.Any(field => command.Method == field + "Control"))) continue;
            UpdateMenu(menu => menu with { Status = "Connecting · checking " + command.Name + "…" });
            var exchange = await MenuQueryAsync(profile, command.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(command.Method, exchange);
            if (IsConnectionFailure(exchange.Outcome)) RequireSuccess(exchange);
        }
        foreach (var grid in IpMenuGrids.All)
        {
            await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
            if (GetSnapshot().Menu.Input != original.Input || GetSnapshot().Menu.PictureMode != original.PictureMode)
                throw new InvalidOperationException("The input/picture mode changed during connection. Reconnect to load the new context; no further selectors were sent.");
            try
            {
                EnsureMenuWritesAllowed();
                await RefreshMenuGridCoreAsync(profile, grid.Section, cancellation, sectionAlreadyRead: true).ConfigureAwait(false);
                if (!GetSnapshot().Menu.GridsRead.ContainsKey(grid.Section)) warnings.Add(GetSnapshot().Menu.Status);
            }
            catch (InvalidOperationException error) when (GetSnapshot().Menu.Connected)
            {
                warnings.Add(error.Message);
                break; // Do not continue selector moves after an uncertain scan.
            }
        }
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        if (GetSnapshot().Menu.Input != original.Input || GetSnapshot().Menu.PictureMode != original.PictureMode)
            throw new InvalidOperationException("The input/picture mode changed during connection. Settings were invalidated; reconnect to load the new context.");
        var unavailable = GetSnapshot().Menu.Readings.Values.Count(reading => reading.Outcome != SamsungIpRemoteOutcome.Success);
        if (unavailable > 0) warnings.Add($"{unavailable} read methods were unavailable in this display/state. Missing values are not replaced with defaults.");
        var missing = IpMenuCatalog.Controls.Where(control => !control.IsSelector
            && !IpMenuGrids.All.Any(grid => grid.Fields.Contains(control.Field))).Count(control => GetSnapshot().Menu.Value(control) is null);
        missing += IpMenuCatalog.IndexedControls.Count(control => GetSnapshot().Menu.Value(control) is null);
        if (missing > 0) warnings.Add($"{missing} Menu values were not reported, including inactive calibration rows. They remain unavailable, not assumed to be zero.");
        UpdateMenu(menu => menu with
        {
            SettingsLoadedAt = _timeProvider.GetUtcNow(),
            LoadWarnings = warnings,
            Status = $"Connection settings loaded in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s." + (warnings.Count > 0 ? " Some values are unavailable; see connection details." : " All Menu sections are ready.")
        });
    }
}
