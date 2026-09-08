using System.Text.Json.Nodes;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string WhiteBalanceReadPath => Path.Combine(_directory, "white-balance-read.json");

    public Task RefreshInactiveWhiteBalanceAsync() => RunMenuOperationAsync((profile, cancellation) =>
        RefreshWhiteBalanceIncludingInactiveCoreAsync(profile, cancellation));

    private async Task RefreshWhiteBalanceIncludingInactiveCoreAsync(IpRemoteProfile profile, CancellationToken cancellation, bool sectionAlreadyRead = false)
    {
        EnsureMenuWritesAllowed();
        if (!sectionAlreadyRead) await RefreshMenuSectionCoreAsync(profile, "white20", cancellation, loadingGrid: true).ConfigureAwait(false);
        if (GetSnapshot().Menu.Readings.GetValueOrDefault("WB20PointModeControl")?.Values?["WB20PointMode"]?.ToString() != "Off")
        {
            // Unsupported/unknown modes remain unavailable. Never guess a mode.
            await RefreshMenuGridCoreAsync(profile, "white20", cancellation, sectionAlreadyRead: true).ConfigureAwait(false);
            return;
        }
        var menu = GetSnapshot().Menu;
        if (string.IsNullOrWhiteSpace(menu.Input) || string.IsNullOrWhiteSpace(menu.PictureMode))
            throw new InvalidOperationException("The TV did not report its input/picture mode. No white-balance mode was changed.");
        var read = new IpMenuWhiteBalanceRead(profile.Endpoint, menu.Input, menu.PictureMode, _timeProvider.GetUtcNow());
        var mode = await ReadWhiteBalanceReadContextAsync(profile, read, cancellation).ConfigureAwait(false);
        if (mode != "Off")
            throw new InvalidOperationException("20-point white-balance mode changed before the read. Refresh again once the TV is stable; no mode was changed.");

        // Persist the original mode before the first write. Stop/errors never
        // run an unconditional finally/rollback against an uncertain context.
        await SaveWhiteBalanceReadAsync(read).ConfigureAwait(false);
        try
        {
            await SendWhiteBalanceReadModeAsync(profile, "On", cancellation).ConfigureAwait(false);
            if (await ReadWhiteBalanceReadContextAsync(profile, read, cancellation).ConfigureAwait(false) != "On")
                throw new InvalidOperationException("The TV did not confirm 20-point white balance On. No interval or RGB command was sent.");
            await RefreshMenuGridCoreAsync(profile, "white20", cancellation, sectionAlreadyRead: true, temporaryWhiteBalanceRead: true).ConfigureAwait(false);
            await RestoreWhiteBalanceReadCoreAsync(profile, GetSnapshot().Menu.WhiteBalanceRead!, cancellation, restoreInterval: false, preserveValues: true).ConfigureAwait(false);
        }
        catch (Exception error) when (IsMenuGridError(error))
        {
            await StopWhiteBalanceReadAsync(error.Message).ConfigureAwait(false);
            throw;
        }
    }

    public Task RestoreWhiteBalanceReadAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed(allowTemporaryWhiteBalanceRead: true);
        var read = GetSnapshot().Menu.WhiteBalanceRead;
        if (read?.NeedsRestore != true) throw new InvalidOperationException("No temporary white-balance mode needs restoration.");
        try { await RestoreWhiteBalanceReadCoreAsync(profile, read, cancellation, restoreInterval: true, preserveValues: false).ConfigureAwait(false); }
        catch (Exception error) when (IsMenuGridError(error))
        {
            await StopWhiteBalanceReadAsync(error.Message).ConfigureAwait(false);
            throw;
        }
    });

    private async Task RestoreWhiteBalanceReadCoreAsync(IpRemoteProfile profile, IpMenuWhiteBalanceRead read, CancellationToken cancellation, bool restoreInterval, bool preserveValues)
    {
        var mode = await ReadWhiteBalanceReadContextAsync(profile, read, cancellation).ConfigureAwait(false);
        if (mode == "On")
        {
            if (restoreInterval && read.OriginalInterval is { } interval)
                await RestoreMenuSelectorAsync(profile, new("white20", read.Endpoint, read.Input, read.PictureMode, interval), cancellation).ConfigureAwait(false);
            await SaveWhiteBalanceReadAsync(read with { Message = "Restoring original 20-point white-balance mode: Off…" }).ConfigureAwait(false);
            await SendWhiteBalanceReadModeAsync(profile, "Off", cancellation).ConfigureAwait(false);
            if (await ReadWhiteBalanceReadContextAsync(profile, read, cancellation, preserveValues).ConfigureAwait(false) != "Off")
                throw new InvalidOperationException("The TV did not confirm 20-point white balance Off. Check the display before continuing.");
        }
        else if (mode == "Off" && preserveValues)
            throw new InvalidOperationException("20-point white balance changed before restoration. Refresh again after reviewing the display; these rows were not retained as a complete read.");
        else if (mode != "Off")
            throw new InvalidOperationException("The TV did not report a known white-balance mode. No restoration command was sent.");
        cancellation.ThrowIfCancellationRequested();
        await SaveWhiteBalanceReadAsync(read with
        {
            NeedsRestore = false,
            Message = preserveValues ? "Read all 20-point values and restored the original interval and mode Off. RGB values were not changed."
                : "Original 20-point mode Off confirmed. Refresh to reread the calibration values."
        }).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Status = GetSnapshot().Menu.WhiteBalanceRead!.Message });
    }

    private async Task<string?> ReadWhiteBalanceReadContextAsync(IpRemoteProfile profile, IpMenuWhiteBalanceRead read, CancellationToken cancellation, bool preserveValues = false)
    {
        if (profile.Endpoint != read.Endpoint) throw new InvalidOperationException("Return to the original display before restoring its white balance.");
        await ReadMenuBaseAsync(profile, cancellation).ConfigureAwait(false);
        var menu = GetSnapshot().Menu;
        if (menu.Input != read.Input || menu.PictureMode != read.PictureMode)
            throw new InvalidOperationException($"White-balance context changed. Return to {read.Input} / {read.PictureMode} before restoring; no further mode commands were sent.");
        var exchange = await MenuQueryAsync(profile, "WB20PointModeControl", cancellation).ConfigureAwait(false);
        RequireSuccess(exchange);
        // Only the verified restore after a complete scan may retain its RGB
        // cache while changing On -> Off. Normal mode changes still invalidate.
        StoreMenuRead("WB20PointModeControl", exchange, preserveGridCache: preserveValues && exchange.Result?["WB20PointMode"]?.ToString() == "Off");
        return exchange.Result?["WB20PointMode"]?.ToString();
    }

    private async Task SendWhiteBalanceReadModeAsync(IpRemoteProfile profile, string mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.ExecuteCommandAsync(profile.Connection, "WB20PointModeControl", new JsonObject { ["WB20PointMode"] = mode }, cancellationToken: cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Calibration read · temporary white-balance mode " + mode, exchange).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        RequireSuccess(exchange);
    }

    private async Task SaveWhiteBalanceReadAsync(IpMenuWhiteBalanceRead read)
    {
        await SaveMenuFileAsync(WhiteBalanceReadPath, read).ConfigureAwait(false);
        UpdateMenu(menu => menu with { WhiteBalanceRead = read });
    }

    private async Task StopWhiteBalanceReadAsync(string message)
    {
        if (GetSnapshot().Menu.WhiteBalanceRead is not { } read) return;
        var stopped = read with { Message = $"Stopped: {message} Original 20-point mode: Off. Check/restore it below. No automatic retry or restoration was sent." };
        UpdateMenu(menu => ClearMenuGridCache(menu, "white20") with { WhiteBalanceRead = stopped });
        try { await SaveWhiteBalanceReadAsync(stopped).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Update(state => state with { StorageWarning = "Could not save the white-balance read result. The original mode was Off; check the display." }); }
    }

    public async Task CloseWhiteBalanceReadReviewAsync()
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var read = GetSnapshot().Menu.WhiteBalanceRead;
            if (read?.NeedsRestore != true) throw new InvalidOperationException("No temporary white-balance read needs review.");
            await SaveWhiteBalanceReadAsync(read with { NeedsRestore = false, Message = "User confirmed manual restoration to Off on the original display/context. No command was sent. Refresh before further edits." }).ConfigureAwait(false);
            var methods = IpMenuCatalog.ForSection("white20").Select(control => control.Method).ToHashSet(StringComparer.Ordinal);
            UpdateMenu(menu => ClearMenuGridCache(menu, "white20") with { Readings = menu.Readings.Where(pair => !methods.Contains(pair.Key)).ToDictionary(), SectionsRead = menu.SectionsRead.Where(pair => pair.Key != "white20").ToDictionary() });
        }
        finally { _gate.Release(); }
    }
}
