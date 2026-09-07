using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string ContrastTestPath => Path.Combine(_directory, "contrast-test.json");

    public Task PrepareContrastTestAsync() => RunContrastOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingContrastTest();
        if (new[] { profile.Model, profile.Firmware, profile.InputSource, profile.PictureMode, profile.Signal }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Save the model, firmware, input, picture mode, and signal annotations before preparing a write experiment.");
        var (input, mode, video, original) = await ReadContrastCheckpointAsync(profile, "Contrast · prepare (read only)", cancellation).ConfigureAwait(false);
        if (original == 0) throw new InvalidOperationException("Contrast is zero. This first experiment only lowers the value by one; no write is permitted at zero.");
        await SaveContrastTestAsync(new()
        {
            Profile = profile,
            Original = original,
            Target = original - 1,
            ReportedInput = input,
            ReportedPictureMode = mode,
            VideoBaseline = video,
            LastReadback = original
        }).ConfigureAwait(false);
    });

    public Task ApplyContrastTestAsync(Guid testId, bool conditionsConfirmed) => RunContrastOperationAsync(async (profile, cancellation) =>
    {
        var test = RequireContrastTest(testId, profile);
        if (test.Stage != IpRemoteContrastStage.Prepared || test.WriteAttempted)
            throw new InvalidOperationException("Prepare a new baseline before applying a contrast test. A previous write is never replayed.");
        if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the displayed original value, valid one-step target, and unchanged TV/input/signal conditions first.");
        if (DateTimeOffset.UtcNow - test.PreparedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The baseline is over two minutes old. Prepare again; no write was sent.");
        var before = await ReadContrastCheckpointAsync(profile, "Contrast · recheck before write", cancellation).ConfigureAwait(false);
        CheckContext(test, before.Input, before.Mode);
        CheckOtherVideoFields(test, before.Video);
        if (before.Contrast != test.Original)
            throw new InvalidOperationException("Contrast changed since preparation. Prepare again; no write was sent.");

        // Durably remember the original BEFORE the request can leave the app.
        // A crash/cancel after this point is treated as potentially applied.
        test = test with
        {
            WriteAttempted = true,
            Stage = IpRemoteContrastStage.Applying,
            Message = "Sending one contrast change. Keep the display, input, picture mode, and signal unchanged."
        };
        await SaveContrastTestAsync(test).ConfigureAwait(false);
        await WriteContrastOnceAsync(profile, test.Target, "Contrast · lower by one", cancellation).ConfigureAwait(false);
        var after = await ReadContrastCheckpointAsync(profile, "Contrast · changed-value readback", cancellation).ConfigureAwait(false);
        CheckContext(test, after.Input, after.Mode);
        CheckOtherVideoFields(test, after.Video);
        if (after.Contrast != test.Target)
            throw new InvalidOperationException($"Readback did not confirm the target {test.Target}; it reported {after.Contrast}. No retry or automatic restoration was sent.");
        await SaveContrastTestAsync(test with
        {
            Stage = IpRemoteContrastStage.AwaitingVisualCheck,
            ChangeReadbackConfirmed = true,
            LastReadback = after.Contrast,
            Message = $"TV reports contrast {test.Target}. Check the Contrast value on the TV, then select a visual result to restore {test.Original}."
        }).ConfigureAwait(false);
    });

    // A null visual result means explicit recovery, not visual verification.
    public Task RestoreContrastTestAsync(Guid testId, bool? visualConfirmed = null) => RunContrastOperationAsync(async (profile, cancellation) =>
    {
        var test = RequireContrastTest(testId, profile);
        if (!test.RequiresRecovery) throw new InvalidOperationException("There is no unresolved contrast write to restore.");
        if (visualConfirmed is not null && test.Stage != IpRemoteContrastStage.AwaitingVisualCheck)
            throw new InvalidOperationException("Visual confirmation is available only after a successful changed-value readback.");
        test = test with { VisualConfirmed = visualConfirmed ?? test.VisualConfirmed, Stage = IpRemoteContrastStage.Restoring };
        await SaveContrastTestAsync(test).ConfigureAwait(false);
        var before = await ReadContrastCheckpointAsync(profile, "Contrast · recheck before restoration", cancellation).ConfigureAwait(false);
        CheckContext(test, before.Input, before.Mode);
        CheckOtherVideoFields(test, before.Video);
        if (before.Contrast == test.Original)
        {
            await CompleteRestorationAsync(test, before.Contrast).ConfigureAwait(false);
            return;
        }
        if (before.Contrast != test.Target)
            throw new InvalidOperationException($"Contrast is now {before.Contrast}, neither original {test.Original} nor test target {test.Target}. Restore manually in the original context; no value was overwritten.");
        if (test.RestoreAttempted)
            throw new InvalidOperationException($"A restoration was already attempted but contrast is still {before.Contrast}. No repeated write was sent. Restore {test.Original} manually, then check again.");
        test = test with { RestoreAttempted = true, Message = $"Restoring contrast to {test.Original} once, then checking readback." };
        await SaveContrastTestAsync(test).ConfigureAwait(false);
        await WriteContrastOnceAsync(profile, test.Original, "Contrast · restore original", cancellation).ConfigureAwait(false);
        test = test with { RestoreAcknowledged = true };
        await SaveContrastTestAsync(test).ConfigureAwait(false);
        var after = await ReadContrastCheckpointAsync(profile, "Contrast · restoration readback", cancellation).ConfigureAwait(false);
        CheckContext(test, after.Input, after.Mode);
        CheckOtherVideoFields(test, after.Video);
        if (after.Contrast != test.Original)
            throw new InvalidOperationException($"Restoration readback reported {after.Contrast}, expected {test.Original}. Check the TV and restore manually; no repeated write was sent.");
        await CompleteRestorationAsync(test, after.Contrast).ConfigureAwait(false);
    });

    public async Task CloseManuallyRestoredContrastTestAsync(Guid testId)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var test = GetSnapshot().ContrastTest;
            if (test is null || test.Id != testId || !test.RequiresRecovery)
                throw new InvalidOperationException("This contrast recovery is no longer pending.");
            await SaveContrastTestAsync(test with
            {
                Stage = IpRemoteContrastStage.ManuallyClosed,
                ManuallyClosed = true,
                Message = "User reports restoring the original value manually. Closed without a TV request; direct-write verification was NOT awarded."
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private Task CompleteRestorationAsync(IpRemoteContrastTest test, int value)
    {
        var completed = test with { Stage = IpRemoteContrastStage.Completed, RestorationConfirmed = true, LastReadback = value };
        return SaveContrastTestAsync(completed with
        {
            Message = completed.Verified
            ? $"Contrast test passed in this saved context: {test.Original} → {test.Target} → {test.Original}. Change readback, visual confirmation, and restoration readback completed. Other controls remain unverified."
            : $"Original contrast {test.Original} confirmed by readback. The experiment is not verified: changed-value readback, positive visual confirmation, or restoration-command acknowledgment is missing."
        });
    }

    private async Task RunContrastOperationAsync(Func<IpRemoteProfile, CancellationToken, Task> action)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var snapshot = GetSnapshot();
            var profile = snapshot.ActiveProfile ?? throw new InvalidOperationException("Save an IP Remote profile first.");
            if (!snapshot.HasToken || snapshot.AuthorizationRejected)
                throw new InvalidOperationException("Pair explicitly before the contrast experiment. No request was sent.");
            var cancellation = new CancellationTokenSource();
            lock (_sync) _operation = cancellation;
            Update(state => state with { IsBusy = true, StorageWarning = null });
            await action(profile, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            var test = GetSnapshot().ContrastTest;
            var message = error is OperationCanceledException ? "Stopped. No follow-up request was sent."
                : error is IOException or UnauthorizedAccessException or JsonException ? "The private contrast recovery file could not be read or saved. Stop and check the TV; no further request was sent."
                : error.Message;
            if (test is not null && test.Stage != IpRemoteContrastStage.Completed && test.Stage != IpRemoteContrastStage.ManuallyClosed)
            {
                var stopped = test with
                {
                    Stage = test.RequiresRecovery ? IpRemoteContrastStage.RecoveryRequired : IpRemoteContrastStage.Stopped,
                    Message = message + (test.RequiresRecovery ? $" The TV may have changed. Original contrast: {test.Original}. Explicitly check and restore, or restore manually." : " No contrast write was sent.")
                };
                Update(state => state with { ContrastTest = stopped, Status = stopped.Message });
                try { await SaveContrastTestAsync(stopped).ConfigureAwait(false); }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
                { Update(state => state with { StorageWarning = "Recovery status could not be saved. Keep the original value shown here; check/restore the TV manually before closing." }); }
            }
            throw;
        }
        finally
        {
            lock (_sync) { _operation?.Dispose(); _operation = null; }
            Update(state => state with { IsBusy = false });
            _gate.Release();
        }
    }

    private async Task<(string Input, string Mode, JsonObject Video, int Contrast)> ReadContrastCheckpointAsync(
        IpRemoteProfile profile, string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var tv = await _client.ReadAsync(profile.Connection, "getTVStates", cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, tv).ConfigureAwait(false);
        RequireSuccess(tv);
        var input = RequiredText(tv.Result, "inputSource");
        var mode = RequiredText(tv.Result, "pictureMode");
        cancellation.ThrowIfCancellationRequested();
        var video = await _client.ReadAsync(profile.Connection, "getVideoStates", cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, video).ConfigureAwait(false);
        RequireSuccess(video);
        if (video.Result?["contrast"] is not JsonValue field || !field.TryGetValue<int>(out var contrast) || contrast is < 0 or > 100)
            throw new InvalidOperationException("The TV did not report an integer contrast in the protocol envelope range 0–100. No value was inferred or substituted.");
        Update(state => state.ContrastTest is { RequiresRecovery: true } test && test.Profile == profile
            ? state with { ContrastTest = test with { LastReadback = contrast } } : state);
        cancellation.ThrowIfCancellationRequested();
        return (input, mode, (JsonObject)video.Result.DeepClone(), contrast);
    }

    private async Task WriteContrastOnceAsync(IpRemoteProfile profile, int value, string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.WriteContrastAsync(profile.Connection, value, cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, exchange).ConfigureAwait(false);
        RequireSuccess(exchange);
        cancellation.ThrowIfCancellationRequested();
    }

    private static void RequireSuccess(SamsungIpRemoteExchange exchange)
    {
        if (!exchange.IsSuccess) throw new InvalidOperationException($"{exchange.Method}: {exchange.Outcome}. {exchange.Message}");
    }

    private static string RequiredText(JsonObject? result, string field) =>
        result?[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidOperationException($"The TV did not report {field}; this guarded test requires a reported input and picture mode.");

    private static void CheckContext(IpRemoteContrastTest test, string input, string mode)
    {
        if (test.ReportedInput != input || test.ReportedPictureMode != mode)
            throw new InvalidOperationException("The TV-reported input or picture mode changed. Return to the original test conditions before recovery; no further write was sent.");
    }

    private static void CheckOtherVideoFields(IpRemoteContrastTest test, JsonObject video)
    {
        var baseline = (JsonObject)test.VideoBaseline.DeepClone();
        var current = (JsonObject)video.DeepClone();
        baseline.Remove("contrast"); current.Remove("contrast");
        if (!JsonNode.DeepEquals(baseline, current))
            throw new InvalidOperationException("Other reported video fields changed or disappeared. Stop and inspect the TV in the original context; no further write was sent.");
    }

    private IpRemoteContrastTest RequireContrastTest(Guid id, IpRemoteProfile profile)
    {
        var test = GetSnapshot().ContrastTest;
        if (test is null || test.Id != id || test.Profile != profile)
            throw new InvalidOperationException("This test belongs to a different or outdated saved profile. Prepare again, or return to the original profile for recovery.");
        return test;
    }

    private void EnsureNoPendingContrastTest()
    {
        if (GetSnapshot().ContrastTest?.RequiresRecovery == true)
            throw new InvalidOperationException("Resolve the pending contrast experiment first: check and restore the original value, or confirm manual restoration. Its original context must not be replaced.");
    }

    private async Task SaveContrastTestAsync(IpRemoteContrastTest test)
    {
        var temporary = ContrastTestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(test, JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporary);
        File.Move(temporary, ContrastTestPath, overwrite: true);
        Update(state => state with { ContrastTest = test, Status = test.Message });
    }

    private async Task<IpRemoteContrastTest?> LoadContrastTestAsync()
    {
        if (!File.Exists(ContrastTestPath)) return null;
        var test = JsonSerializer.Deserialize<IpRemoteContrastTest>(await File.ReadAllTextAsync(ContrastTestPath).ConfigureAwait(false))
            ?? throw new JsonException("The contrast recovery file is empty.");
        if (test.Profile?.Connection is null || test.Original is < 1 or > 100 || test.Target != test.Original - 1
            || string.IsNullOrWhiteSpace(test.ReportedInput) || string.IsNullOrWhiteSpace(test.ReportedPictureMode) || test.VideoBaseline is null)
            throw new JsonException("The contrast recovery file is invalid. Do not discard it before checking the TV manually.");
        _ = test.Profile.Endpoint;
        return test.RequiresRecovery ? test with
        {
            Stage = IpRemoteContrastStage.RecoveryRequired,
            Message = $"Unresolved experiment from a previous session. Original contrast: {test.Original}. No request was sent on startup. Check the original display/context before explicit recovery."
        }
            : test.Stage == IpRemoteContrastStage.Prepared ? test with { Stage = IpRemoteContrastStage.Stopped, Message = "Previous-session baseline is stale. Prepare again; no request was sent on startup." }
            : test;
    }
}
