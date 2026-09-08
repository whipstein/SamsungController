using System.Diagnostics;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    /// <summary>Explicitly reload every setting for the current signal, without reconnecting or reusing cached values.</summary>
    public Task RefreshAllMenuSettingsAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed();
        // Bit depth/HDR may change without a different reported HDMI port or
        // picture-mode name. Never carry values or unsent edits across this reload.
        UpdateMenu(menu => ClearMenuGridCache(menu) with
        {
            Readings = new Dictionary<string, IpMenuRead>(),
            SectionsRead = new Dictionary<string, DateTimeOffset>(),
            Pending = new Dictionary<string, IpMenuDraft>(),
            SettingsLoadedAt = null,
            LoadWarnings = [],
            ConnectionLoadAttempted = true,
            ValuesRevision = menu.ValuesRevision + 1,
            Status = "Refreshing all TV values for the current signal…"
        });
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        await LoadAllMenuSettingsAsync(profile, cancellation, refreshing: true).ConfigureAwait(false);
    });

    private async Task LoadAllMenuSettingsAsync(IpRemoteProfile profile, CancellationToken cancellation, bool refreshing = false)
    {
        var started = Stopwatch.GetTimestamp();
        var original = GetSnapshot().Menu;
        var warnings = new List<string>();
        UpdateMenu(menu => menu with { SettingsLoadedAt = null, LoadWarnings = [] });
        foreach (var section in IpMenuCatalog.Sections)
        {
            UpdateMenu(menu => menu with { Status = (refreshing ? "Refreshing" : "Connecting") + " · reading " + section.Name + "…" });
            await RefreshMenuSectionCoreAsync(profile, section.Id, cancellation, loadingGrid: true, refreshBase: false).ConfigureAwait(false);
        }
        // Include documented read/list methods that do not have an editable
        // Menu card. Never invoke write-only keys, resets or calibration starts.
        foreach (var command in SamsungIpRemoteCommands.All.Where(command => command.CanQuery))
        {
            if (GetSnapshot().Menu.Readings.ContainsKey(command.Method)
                || command.Method is "getTVStates" or "getVideoStates"
                || IpMenuGrids.All.Any(grid => grid.Fields.Any(field => command.Method == field + "Control"))) continue;
            UpdateMenu(menu => menu with { Status = (refreshing ? "Refreshing" : "Connecting") + " · checking " + command.Name + "…" });
            var exchange = await MenuQueryAsync(profile, command.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(command.Method, exchange);
            if (IsConnectionFailure(exchange.Outcome)) RequireSuccess(exchange);
        }
        foreach (var grid in IpMenuGrids.All)
        {
            await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
            RequireLoadContext();
            try
            {
                EnsureMenuWritesAllowed();
                if (refreshing && grid.Section == "white20")
                    await RefreshWhiteBalanceIncludingInactiveCoreAsync(profile, cancellation, sectionAlreadyRead: true).ConfigureAwait(false);
                else
                    await RefreshMenuGridCoreAsync(profile, grid.Section, cancellation, sectionAlreadyRead: true).ConfigureAwait(false);
                if (!GetSnapshot().Menu.GridsRead.ContainsKey(grid.Section)) warnings.Add(GetSnapshot().Menu.Status);
            }
            catch (InvalidOperationException error) when (GetSnapshot().Menu.Connected)
            {
                if (GetSnapshot().Menu.WhiteBalanceRead?.NeedsRestore == true) throw;
                warnings.Add(error.Message);
                break; // Do not continue selector moves after an uncertain scan.
            }
        }
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        RequireLoadContext();
        var unavailable = GetSnapshot().Menu.Readings.Values.Count(reading => reading.Outcome != SamsungIpRemoteOutcome.Success);
        if (unavailable > 0) warnings.Add($"{unavailable} read methods were unavailable in this display/state. Missing values are not replaced with defaults.");
        var missing = IpMenuCatalog.Controls.Where(control => !control.IsSelector
            && !IpMenuGrids.All.Any(grid => grid.Fields.Contains(control.Field))).Count(control => GetSnapshot().Menu.Value(control) is null);
        missing += IpMenuCatalog.IndexedControls.Count(control => GetSnapshot().Menu.Value(control) is null);
        if (missing > 0)
        {
            var menu = GetSnapshot().Menu;
            var hidden = IpMenuCatalog.AllControls.Count(control => !control.IsSelector
                && (control.IsIndexed || !IpMenuGrids.All.Any(grid => grid.Fields.Contains(control.Field)))
                && !IpMenuAvailability.For(menu, control).Visible);
            warnings.Add($"{missing} Menu values were not reported. {hidden} rejected/absent controls are hidden; controls with unmet prerequisites remain gray and disabled. Open the communication log for each response.");
        }
        UpdateMenu(menu => menu with
        {
            SettingsLoadedAt = _timeProvider.GetUtcNow(),
            LoadWarnings = warnings,
            Status = $"{(refreshing ? "TV settings refreshed" : "Connection settings loaded")} in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s." + (warnings.Count > 0 ? " Some values are unavailable; see settings-load details." : " All Menu sections are ready.")
        });

        void RequireLoadContext()
        {
            var current = GetSnapshot().Menu;
            if (current.ValuesRevision != original.ValuesRevision || current.Input != original.Input || current.PictureMode != original.PictureMode)
                throw new InvalidOperationException("The TV input/picture mode changed during loading. Wait for the signal to settle, then select Refresh TV values to reload all settings. No further selectors were sent; reconnecting is not required.");
        }
    }
}
