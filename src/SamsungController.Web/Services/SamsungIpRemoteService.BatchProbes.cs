using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string RgbProbePath => Path.Combine(_directory, "rgb-probe.json");
    private static IpMenuGrid ProbeGrid => IpMenuGrids.ForSection("white20")!;
    public bool CanProbeRgbBatch => GetSnapshot() is { ActiveProfile: { } profile, ReadBatchProbe: { } probe, Menu: { } menu }
        && probe.Endpoint == profile.Endpoint && probe.SessionId == menu.SessionId && probe.Exchange.IsSuccess;

    public Task ProbeReadBatchAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        var exchange = await _client.ProbeReadBatchAsync(profile.Connection, cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, "Batch test · read only", exchange).ConfigureAwait(false);
        Update(state => state with { ReadBatchProbe = new(profile.Endpoint, state.Menu.SessionId, exchange) });
    });

    public Task PrepareRgbProbeAsync(string interval) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        EnsureMenuWritesAllowed();
        if (!ProbeGrid.Values.Contains(interval, StringComparer.Ordinal)) throw new ArgumentException("Choose a documented 20-point percentage.");
        if (GetSnapshot().Menu.Pending.Count > 0) throw new InvalidOperationException("Apply or discard pending Menu edits first.");
        var originalInterval = await ReadMenuGridContextAsync(profile, ProbeGrid, null, cancellation).ConfigureAwait(false);
        var menu = GetSnapshot().Menu;
        var probe = new IpRgbProbe { Endpoint = profile.Endpoint, SessionId = menu.SessionId, Input = menu.Input!, PictureMode = menu.PictureMode!,
            Interval = interval, OriginalInterval = originalInterval, Message = "Preparing the selected percentage. No RGB write has been sent." };
        await SaveRgbProbeAsync(probe).ConfigureAwait(false); // Preserve selector recovery before any move.
        try
        {
            await MoveMenuSelectorAsync(profile, ProbeGrid, ProbeSelector(probe), interval, cancellation).ConfigureAwait(false);
            var values = await ReadRgbProbeValuesAsync(profile, probe, cancellation).ConfigureAwait(false);
            if (values.Values.Any(value => value is null)) throw new InvalidOperationException("All three RGB originals must be reported within -50…50. No defaults or RGB writes were used.");
            menu = GetSnapshot().Menu;
            await SaveRgbProbeAsync(probe with { Stage = IpRgbProbeStage.Prepared, PreparedAt = _timeProvider.GetUtcNow(),
                Originals = values.ToDictionary(pair => pair.Key, pair => pair.Value!.Value),
                Targets = values.ToDictionary(pair => pair.Key, pair => pair.Value == 50 ? 49 : pair.Value!.Value + 1),
                TvBaseline = (JsonObject)menu.Tv.DeepClone(), VideoBaseline = (JsonObject)menu.Video.DeepClone(),
                Message = $"Prepared {interval}. Each channel's test target differs by one step. RGB values are unchanged; the percentage remains selected for your check. Preparation expires in two minutes." }).ConfigureAwait(false);
        }
        catch (Exception error) { await StopRgbProbeAsync(error.Message).ConfigureAwait(false); throw; }
    });

    public Task SendRgbProbeAsync(Guid id, bool singleMethod, bool confirmed) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        var probe = RequireRgbProbe(profile, id);
        if (!confirmed) throw new InvalidOperationException("Confirm the physical display, signal, percentage and small RGB changes before sending.");
        if (probe.Stage != IpRgbProbeStage.Prepared || probe.SessionId != GetSnapshot().Menu.SessionId
            || _timeProvider.GetUtcNow() - probe.PreparedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("This preparation is stale or was already used. Restore/end this test, then prepare again.");
        if (!singleMethod && !CanProbeRgbBatch) throw new InvalidOperationException("Run the read-only batch check successfully on this connection first. The single-method experiment is independent of batch support.");
        try
        {
            var before = await ReadRgbProbeValuesAsync(profile, probe, cancellation).ConfigureAwait(false);
            if (ProbeGrid.Fields.Any(field => before[field] != probe.Originals[field])
                || !JsonNode.DeepEquals(GetSnapshot().Menu.Tv, probe.TvBaseline) || !JsonNode.DeepEquals(GetSnapshot().Menu.Video, probe.VideoBaseline))
                throw new InvalidOperationException("RGB values or reported TV context changed after preparation. Nothing was sent; end this test and prepare again.");
            probe = probe with { Stage = IpRgbProbeStage.Sending, WriteAttempted = true, SingleMethod = singleMethod,
                Message = "The experimental RGB request may be sent. Saved originals are retained; do not change the physical signal." };
            await SaveRgbProbeAsync(probe).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var exchange = await _client.ProbeWhiteBalanceRgbAsync(profile.Connection, probe.Targets["WB20P.Red"], probe.Targets["WB20P.Green"], probe.Targets["WB20P.Blue"], singleMethod, cancellation).ConfigureAwait(false);
            await RecordExchangeAsync(profile, singleMethod ? "RGB test · one method, three fields (experimental)" : "RGB test · three methods, one HTTP batch", exchange).ConfigureAwait(false);
            probe = probe with { Exchange = exchange };
            await SaveRgbProbeAsync(probe).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (IsConnectionFailure(exchange.Outcome) || exchange.Outcome == SamsungIpRemoteOutcome.Canceled) RequireSuccess(exchange);
            // Even a rejected/partial/malformed reply can follow a delivered write.
            // Read each channel independently; never retry or fall back to setters.
            var actual = await ReadRgbProbeValuesAsync(profile, probe, cancellation).ConfigureAwait(false);
            var matched = ProbeGrid.Fields.Count(field => actual[field] == probe.Targets[field]);
            var contextMatches = JsonNode.DeepEquals(GetSnapshot().Menu.Tv, probe.TvBaseline) && JsonNode.DeepEquals(GetSnapshot().Menu.Video, probe.VideoBaseline);
            var result = exchange.IsSuccess && matched == 3 && contextMatches
                ? "All three targets independently confirmed with reported context unchanged. This is evidence for this display/percentage/state only, not atomicity or general support."
                : $"Not verified: {matched}/3 targets matched; RPC outcome {exchange.Outcome}; reported context {(contextMatches ? "unchanged" : "changed")}. Acknowledgment alone is not success, and ignored channels remain visible below.";
            await SaveRgbProbeAsync(probe with { Stage = IpRgbProbeStage.Observed, Readback = actual,
                Message = result + " No fallback was sent. Check the TV, then explicitly restore the originals and selector." }).ConfigureAwait(false);
        }
        catch (Exception error) { await StopRgbProbeAsync(error.Message).ConfigureAwait(false); throw; }
    });

    public Task RestoreRgbProbeAsync(Guid id) => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        var probe = RequireRgbProbe(profile, id);
        try
        {
            await SaveRgbProbeAsync(probe with { Stage = IpRgbProbeStage.Restoring, Message = "Explicit restoration requested. Known single-channel commands are used; no batch retry." }).ConfigureAwait(false);
            await MoveMenuSelectorAsync(profile, ProbeGrid, ProbeSelector(probe), probe.Interval, cancellation).ConfigureAwait(false);
            if (probe.WriteAttempted)
            {
                var actual = await ReadRgbProbeValuesAsync(profile, probe, cancellation).ConfigureAwait(false);
                if (ProbeGrid.Fields.Any(field => actual[field] is null || actual[field] != probe.Originals[field] && actual[field] != probe.Targets[field]))
                    throw new InvalidOperationException("A channel is unknown or differs from both its original and test target. Automatic restoration would overwrite an unexpected change; restore/check the TV manually.");
                foreach (var field in ProbeGrid.Fields)
                {
                    if (actual[field] == probe.Originals[field]) continue;
                    cancellation.ThrowIfCancellationRequested();
                    var exchange = await _client.ExecuteCommandAsync(profile.Connection, field + "Control", new() { [field] = probe.Originals[field] }, cancellationToken: cancellation).ConfigureAwait(false);
                    await RecordExchangeAsync(profile, "RGB test · explicit restore " + field, exchange).ConfigureAwait(false);
                    RequireSuccess(exchange);
                    var after = await ReadRgbProbeValuesAsync(profile, probe, cancellation).ConfigureAwait(false);
                    if (after[field] != probe.Originals[field] || ProbeGrid.Fields.Any(peer => peer != field && after[peer] != actual[peer]))
                        throw new InvalidOperationException("Restoration readback did not match, or another channel changed. Further writes stopped.");
                    actual = after;
                    await SaveRgbProbeAsync(GetSnapshot().RgbProbe! with { RecoveryReadback = actual }).ConfigureAwait(false);
                }
                await SaveRgbProbeAsync(GetSnapshot().RgbProbe! with { RecoveryReadback = actual }).ConfigureAwait(false);
            }
            await RestoreMenuSelectorAsync(profile, ProbeSelector(probe), cancellation).ConfigureAwait(false);
            await SaveRgbProbeAsync(GetSnapshot().RgbProbe! with { Stage = IpRgbProbeStage.Restored,
                Message = probe.WriteAttempted ? "Original RGB values and original percentage were independently confirmed. Test ended; normal Menu behavior is unchanged."
                    : "Original percentage restored. No experimental RGB request was sent. Test ended." }).ConfigureAwait(false);
        }
        catch (Exception error) { await StopRgbProbeAsync(error.Message).ConfigureAwait(false); throw; }
    });

    public async Task CloseRgbProbeManuallyAsync(Guid id, bool confirmed)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var probe = GetSnapshot().RgbProbe;
            if (!confirmed || probe is null || probe.Id != id || !probe.NeedsRecovery) throw new InvalidOperationException("Confirm that the original RGB values and percentage were restored manually.");
            await SaveRgbProbeAsync(probe with { Stage = IpRgbProbeStage.ManuallyClosed, Message = "User confirmed manual restoration. No TV command was sent and no protocol verification was awarded." }).ConfigureAwait(false);
            UpdateMenu(menu => ClearMenuGridCache(menu, "white20"));
        }
        finally { _gate.Release(); }
    }

    private IpRgbProbe RequireRgbProbe(IpRemoteProfile profile, Guid id) => GetSnapshot().RgbProbe is { NeedsRecovery: true } probe && probe.Id == id && probe.Endpoint == profile.Endpoint
        ? probe : throw new InvalidOperationException("This test is not active on the original display. No experimental command was sent.");
    private static IpMenuSelectorSession ProbeSelector(IpRgbProbe probe) => new("white20", probe.Endpoint, probe.Input, probe.PictureMode, probe.OriginalInterval);

    private async Task<Dictionary<string, int?>> ReadRgbProbeValuesAsync(IpRemoteProfile profile, IpRgbProbe probe, CancellationToken cancellation)
    {
        async Task CheckContext()
        {
            if (await ReadMenuGridContextAsync(profile, ProbeGrid, ProbeSelector(probe), cancellation).ConfigureAwait(false) != probe.Interval)
                throw new InvalidOperationException("The TV is not on the prepared percentage. No RGB write was sent to a different row.");
        }
        await CheckContext().ConfigureAwait(false);
        var values = new Dictionary<string, int?>();
        var readings = new Dictionary<string, IpMenuRead>();
        foreach (var control in ProbeGrid.Row(probe.Interval))
        {
            var exchange = await MenuQueryAsync(profile, control.Method, cancellation).ConfigureAwait(false);
            StoreMenuRead(control.Method, exchange);
            if (IsConnectionFailure(exchange.Outcome)) RequireSuccess(exchange);
            var reading = GetSnapshot().Menu.Readings[control.Method];
            values[control.Field] = reading.Outcome == SamsungIpRemoteOutcome.Success && reading.Values?[control.Field] is JsonValue value
                && value.TryGetValue<int>(out var number) && number is >= -50 and <= 50 ? number : null;
            readings[control.Id] = reading;
        }
        await CheckContext().ConfigureAwait(false);
        UpdateMenu(menu => menu with { IndexedReadings = menu.IndexedReadings.Where(pair => !readings.ContainsKey(pair.Key)).Concat(readings).ToDictionary() });
        return values;
    }

    private async Task SaveRgbProbeAsync(IpRgbProbe probe)
    {
        await SaveMenuFileAsync(RgbProbePath, probe).ConfigureAwait(false);
        Update(state => state with { RgbProbe = probe });
    }
    private async Task StopRgbProbeAsync(string message)
    {
        if (GetSnapshot().RgbProbe is not { NeedsRecovery: true } probe) return;
        var stopped = probe with { Stage = IpRgbProbeStage.Stopped, Message = message + " Test stopped. No automatic restore, fallback, retry or resume. Check the saved originals and use explicit recovery." };
        Update(state => state with { RgbProbe = stopped });
        try { await SaveRgbProbeAsync(stopped).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Update(state => state with { StorageWarning = "Could not save the RGB test result. Download the report and preserve its originals before closing." }); }
    }
    private async Task<IpRgbProbe?> LoadRgbProbeAsync()
    {
        if (!File.Exists(RgbProbePath)) return null;
        var probe = JsonSerializer.Deserialize<IpRgbProbe>(await File.ReadAllTextAsync(RgbProbePath).ConfigureAwait(false)) ?? throw new JsonException("Empty RGB test recovery file.");
        if (!Enum.IsDefined(probe.Stage) || !Uri.TryCreate(probe.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
            || string.IsNullOrWhiteSpace(probe.Input) || string.IsNullOrWhiteSpace(probe.PictureMode) || probe.Message is null
            || !ProbeGrid.Values.Contains(probe.Interval) || !ProbeGrid.Values.Contains(probe.OriginalInterval)
            || probe.Originals is null || probe.Targets is null || probe.Readback is null || probe.RecoveryReadback is null || probe.TvBaseline is null || probe.VideoBaseline is null
            || (probe.WriteAttempted || probe.Stage == IpRgbProbeStage.Prepared) && (probe.Originals.Count != 3 || probe.Targets.Count != 3
                || ProbeGrid.Fields.Any(field => !probe.Originals.TryGetValue(field, out var original) || !probe.Targets.TryGetValue(field, out var target)
                    || original is < -50 or > 50 || target is < -50 or > 50 || Math.Abs(target - original) != 1)))
            throw new JsonException("Invalid RGB test recovery file. Preserve it and check the original TV manually.");
        return probe.NeedsRecovery ? probe with { Stage = IpRgbProbeStage.Stopped, Message = "An RGB test was interrupted. Nothing resumed on startup. Reconnect to the original display without changing its signal; restore/end the test here." } : probe;
    }
}
