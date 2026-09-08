using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    // Retain the private journal location so an interrupted contrast write from
    // the previous preview can still be recovered. Control now identifies its field.
    private string PictureTestPath => Path.Combine(_directory, "contrast-test.json");

    public Task PreparePictureTestAsync(string control = "contrast") => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingPictureTest();
        var definition = SamsungIpRemotePictureControl.Get(control);
        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        if (new[] { profile.Model, profile.Firmware, profile.InputSource, profile.PictureMode, profile.Signal }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Save the model, firmware, input, picture mode, and signal annotations before preparing a write experiment.");
        var (input, mode, video, original) = await ReadPictureCheckpointAsync(profile, control, $"{definition.Name} · prepare (read only)", cancellation).ConfigureAwait(false);
        var range = profile.RangeFor(control);
        if (!range.Contains(original) || !range.Contains(SamsungIpRemotePictureControl.TestTarget(original)))
            throw new InvalidOperationException($"The original/test target falls outside the configured {definition.Name} range {range.Minimum}–{range.Maximum}. Correct the range before testing.");
        await SavePictureTestAsync(new()
        {
            Profile = profile,
            Control = control,
            PreparedAt = _timeProvider.GetUtcNow(),
            Original = original,
            Target = SamsungIpRemotePictureControl.TestTarget(original),
            ReportedInput = input,
            ReportedPictureMode = mode,
            VideoBaseline = video,
            LastReadback = original
        }).ConfigureAwait(false);
    });

    public Task ApplyPictureTestAsync(Guid testId, bool conditionsConfirmed) => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        if (GetSnapshot().CommandTrial?.RequiresReview == true) throw new InvalidOperationException("Review the pending command on IP Commands before picture adjustments.");
        var test = RequirePictureTest(testId, profile);
        if (test.Purpose != IpRemotePicturePurpose.Verification || test.Stage != IpRemotePictureStage.Prepared || test.WriteAttempted)
            throw new InvalidOperationException("Prepare a new baseline before applying a picture test. A previous write is never replayed.");
        if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the displayed original value, valid one-step target, and unchanged TV/input/signal conditions first.");
        if (_timeProvider.GetUtcNow() - test.PreparedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The baseline is over two minutes old. Prepare again; no write was sent.");
        var before = await ReadPictureCheckpointAsync(profile, test.Control, $"{test.ControlName} · recheck before write", cancellation).ConfigureAwait(false);
        CheckContext(test, before.Input, before.Mode);
        CheckOtherVideoFields(test, before.Video);
        if (before.Value != test.Original)
            throw new InvalidOperationException($"{test.ControlName} changed since preparation. Prepare again; no write was sent.");

        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        await ApplyPreparedPictureAsync(test, cancellation).ConfigureAwait(false);
    });

    private async Task ApplyPreparedPictureAsync(IpRemotePictureTest test, CancellationToken cancellation)
    {
        var profile = test.Profile;
        var direct = test.Purpose == IpRemotePicturePurpose.DirectAdjustment;

        // Durably remember the original BEFORE the request can leave the app.
        // A crash/cancel after this point is treated as potentially applied.
        test = test with
        {
            WriteAttempted = true,
            Stage = IpRemotePictureStage.Applying,
            Message = $"Sending one {test.ControlName} change. Keep the display, input, picture mode, and signal unchanged."
        };
        await SavePictureTestAsync(test).ConfigureAwait(false);
        try
        {
            await WritePictureOnceAsync(profile, test.Control, test.Target, $"{test.ControlName} · {(direct ? "apply target" : "one-step test")}", cancellation).ConfigureAwait(false);
        }
        catch (PictureCommandRejectedException rejection) when (!cancellation.IsCancellationRequested)
        {
            // Only an explicit, correlated RPC failure permits this read-only check.
            // Never follow cancellation/timeouts with requests or assume an error means no change.
            var unchanged = await ReadPictureCheckpointAsync(profile, test.Control, $"{test.ControlName} · rejected target check (read only)", cancellation).ConfigureAwait(false);
            CheckContext(test, unchanged.Input, unchanged.Mode);
            CheckOtherVideoFields(test, unchanged.Video);
            if (unchanged.Value != test.Original)
                throw new InvalidOperationException($"The TV rejected {test.Target}, but its value changed to {unchanged.Value}. Check recovery; no retry or restoring write was sent.");
            var message = $"The TV rejected {test.ControlName} {test.Target} ({rejection.Code}). Readback confirms the original {test.Original} is unchanged. Choose a value within the TV's range or check whether this setting is available. No retry or restoring write was sent.";
            await SavePictureTestAsync(test with { Stage = IpRemotePictureStage.Stopped, RejectedUnchanged = true, LastReadback = test.Original, Message = message }).ConfigureAwait(false);
            if (direct) SetDirectPictureReading(profile, test.Control, unchanged.Input, unchanged.Mode, unchanged.Value, unchanged.Video);
            throw new InvalidOperationException(message);
        }
        var after = await ReadPictureCheckpointAsync(profile, test.Control, $"{test.ControlName} · changed-value readback", cancellation).ConfigureAwait(false);
        CheckContext(test, after.Input, after.Mode);
        CheckOtherVideoFields(test, after.Video);
        if (after.Value != test.Target)
            throw new InvalidOperationException($"Readback did not confirm the target {test.Target}; it reported {after.Value}. No retry or automatic restoration was sent.");
        await SavePictureTestAsync(test with
        {
            Stage = direct ? IpRemotePictureStage.Completed : IpRemotePictureStage.AwaitingVisualCheck,
            ChangeReadbackConfirmed = true,
            DirectChangeKept = direct,
            LastReadback = after.Value,
            Message = direct ? $"Direct {test.ControlName} applied: {test.Original} → {test.Target}, confirmed by independent readback. The new value is kept on the TV. Undo is explicit; no new verification was awarded."
                : $"TV reports {test.ControlName} {test.Target}. Check the {test.ControlName} value on the TV, then select a visual result to restore {test.Original}."
        }).ConfigureAwait(false);
        if (direct) SetDirectPictureReading(profile, test.Control, after.Input, after.Mode, after.Value, after.Video);
    }

    // A null visual result means explicit recovery, not visual verification.
    public Task RestorePictureTestAsync(Guid testId, bool? visualConfirmed = null, bool undoConfirmed = false) => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        var test = RequirePictureTest(testId, profile);
        var undo = test.Purpose == IpRemotePicturePurpose.DirectAdjustment && test.DirectChangeKept;
        if (undoConfirmed && !undo) throw new InvalidOperationException("There is no kept direct adjustment to undo. Use the guided test or recovery controls for other pending operations.");
        if (!test.RequiresRecovery && !undo) throw new InvalidOperationException("There is no unresolved picture write or retained direct adjustment to restore.");
        if (undo && !undoConfirmed) throw new InvalidOperationException("Confirm the original display and conditions before undoing this direct picture adjustment.");
        if (visualConfirmed is not null && test.Stage != IpRemotePictureStage.AwaitingVisualCheck)
            throw new InvalidOperationException("Visual confirmation is available only after a successful changed-value readback.");
        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        // A failed preflight for an explicit Undo does not turn a previously
        // successful kept adjustment into an unresolved write.
        test = test with { VisualConfirmed = visualConfirmed ?? test.VisualConfirmed, Stage = IpRemotePictureStage.Restoring };
        if (!undo) await SavePictureTestAsync(test).ConfigureAwait(false);
        var before = await ReadPictureCheckpointAsync(profile, test.Control, $"{test.ControlName} · recheck before restoration", cancellation).ConfigureAwait(false);
        CheckContext(test, before.Input, before.Mode);
        CheckOtherVideoFields(test, before.Video);
        if (before.Value == test.Original)
        {
            await CompleteRestorationAsync(test, before.Value).ConfigureAwait(false);
            return;
        }
        if (before.Value != test.Target)
            throw new InvalidOperationException($"{test.ControlName} is now {before.Value}, neither original {test.Original} nor test target {test.Target}. Restore manually in the original context; no value was overwritten.");
        if (test.RestoreAttempted)
            throw new InvalidOperationException($"A restoration was already attempted but {test.ControlName} is still {before.Value}. No repeated write was sent. Restore {test.Original} manually, then check again.");
        test = test with { DirectChangeKept = false, RestoreAttempted = true, Message = $"Restoring {test.ControlName} to {test.Original} once, then checking readback." };
        await SavePictureTestAsync(test).ConfigureAwait(false);
        await WritePictureOnceAsync(profile, test.Control, test.Original, $"{test.ControlName} · restore original", cancellation).ConfigureAwait(false);
        test = test with { RestoreAcknowledged = true };
        await SavePictureTestAsync(test).ConfigureAwait(false);
        var after = await ReadPictureCheckpointAsync(profile, test.Control, $"{test.ControlName} · restoration readback", cancellation).ConfigureAwait(false);
        CheckContext(test, after.Input, after.Mode);
        CheckOtherVideoFields(test, after.Video);
        if (after.Value != test.Original)
            throw new InvalidOperationException($"Restoration readback reported {after.Value}, expected {test.Original}. Check the TV and restore manually; no repeated write was sent.");
        await CompleteRestorationAsync(test, after.Value).ConfigureAwait(false);
    });

    public async Task CloseManuallyRestoredPictureTestAsync(Guid testId)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var test = GetSnapshot().PictureTest;
            if (test is null || test.Id != testId || !test.RequiresRecovery)
                throw new InvalidOperationException("This picture recovery is no longer pending.");
            await SavePictureTestAsync(test with
            {
                Stage = IpRemotePictureStage.ManuallyClosed,
                ManuallyClosed = true,
                Message = "User reports restoring the original value manually. Closed without a TV request; direct-write verification was NOT awarded."
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task CompleteRestorationAsync(IpRemotePictureTest test, int value)
    {
        var completed = test with { Stage = IpRemotePictureStage.Completed, DirectChangeKept = false, RestorationConfirmed = true, LastReadback = value };
        await SavePictureTestAsync(completed with
        {
            Message = test.Purpose == IpRemotePicturePurpose.DirectAdjustment ? $"Direct {test.ControlName} restored to {test.Original}, confirmed by readback. Saved capability evidence is unchanged."
            : completed.Verified
            ? $"{test.ControlName} test passed in this saved context: {test.Original} → {test.Target} → {test.Original}. Change readback, visual confirmation, and restoration readback completed. Other controls keep their own verification status."
            : $"Original {test.ControlName} {test.Original} confirmed by readback. The experiment is not verified: changed-value readback, positive visual confirmation, or restoration-command acknowledgment is missing."
        }).ConfigureAwait(false);
        if (test.Purpose == IpRemotePicturePurpose.DirectAdjustment)
        {
            var video = (JsonObject)test.VideoBaseline.DeepClone();
            video[test.Control] = value;
            SetDirectPictureReading(test.Profile, test.Control, test.ReportedInput, test.ReportedPictureMode, value, video);
        }
    }

    private async Task RunPictureOperationAsync(Func<IpRemoteProfile, CancellationToken, Task> action)
    {
        await EnterAsync().ConfigureAwait(false);
        try
        {
            var snapshot = GetSnapshot();
            var profile = snapshot.ActiveProfile ?? throw new InvalidOperationException("Save an IP Remote profile first.");
            if (!snapshot.HasToken || snapshot.AuthorizationRejected)
                throw new InvalidOperationException("Pair explicitly before the picture experiment. No request was sent.");
            var cancellation = new CancellationTokenSource();
            lock (_sync) _operation = cancellation;
            Update(state => state with { IsBusy = true, StorageWarning = null });
            await action(profile, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            var test = GetSnapshot().PictureTest;
            var message = error is OperationCanceledException ? "Stopped. No follow-up request was sent."
                : error is IOException or UnauthorizedAccessException or JsonException ? "The private picture recovery file could not be read or saved. Stop and check the TV; no further request was sent."
                : error.Message;
            if (test is not null && !test.RejectedUnchanged && test.Stage != IpRemotePictureStage.Completed && test.Stage != IpRemotePictureStage.ManuallyClosed)
            {
                var stopped = test with
                {
                    Stage = test.RequiresRecovery ? IpRemotePictureStage.RecoveryRequired : IpRemotePictureStage.Stopped,
                    Message = message + (test.RequiresRecovery ? $" The TV may have changed. Original {test.ControlName}: {test.Original}. Explicitly check and restore, or restore manually." : " No picture write was sent.")
                };
                Update(state => state with { PictureTest = stopped, Status = stopped.Message });
                try { await SavePictureTestAsync(stopped).ConfigureAwait(false); }
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

    private async Task<(string Input, string Mode, JsonObject Video, int Value)> ReadPictureCheckpointAsync(
        IpRemoteProfile profile, string control, string label, CancellationToken cancellation)
    {
        var definition = SamsungIpRemotePictureControl.Get(control);
        var checkpoint = await ReadVideoCheckpointAsync(profile, label, cancellation).ConfigureAwait(false);
        if (checkpoint.Video[control] is not JsonValue field || !field.TryGetValue<int>(out var value) || value is < 0 or > 100)
            throw new InvalidOperationException($"The TV did not report an integer {definition.Name} in the protocol envelope range 0–100. No value was inferred or substituted.");
        Update(state => state.PictureTest is { RequiresRecovery: true } test && test.Profile == profile && test.Control == control
            ? state with { PictureTest = test with { LastReadback = value } } : state);
        cancellation.ThrowIfCancellationRequested();
        return (checkpoint.Input, checkpoint.Mode, checkpoint.Video, value);
    }

    private async Task<(string Input, string Mode, JsonObject Video)> ReadVideoCheckpointAsync(
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
        if (video.Result is null) throw new InvalidOperationException("The TV did not return video state. No values were inferred.");
        cancellation.ThrowIfCancellationRequested();
        return (input, mode, (JsonObject)video.Result.DeepClone());
    }

    private async Task WritePictureOnceAsync(IpRemoteProfile profile, string control, int value, string label, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var exchange = await _client.WritePictureControlAsync(profile.Connection, control, value, cancellation).ConfigureAwait(false);
        await RecordExchangeAsync(profile, label, exchange).ConfigureAwait(false);
        if (exchange.Outcome == SamsungIpRemoteOutcome.RpcError && exchange.RpcErrorCode is -32002 or -32003 or -32602)
            throw new PictureCommandRejectedException(exchange.RpcErrorCode.Value);
        RequireSuccess(exchange);
        cancellation.ThrowIfCancellationRequested();
    }

    private static void RequireSuccess(SamsungIpRemoteExchange exchange)
    {
        if (!exchange.IsSuccess) throw new InvalidOperationException($"{exchange.Method}: {exchange.Outcome}. {exchange.Message}");
    }

    private sealed class PictureCommandRejectedException(int code) : InvalidOperationException($"The TV rejected the command ({code}).")
    { public int Code { get; } = code; }

    private static string RequiredText(JsonObject? result, string field) =>
        result?[field] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidOperationException($"The TV did not report {field}; this guarded test requires a reported input and picture mode.");

    private static void CheckContext(IpRemotePictureTest test, string input, string mode)
    {
        if (test.ReportedInput != input || test.ReportedPictureMode != mode)
            throw new InvalidOperationException("The TV-reported input or picture mode changed. Return to the original test conditions before recovery; no further write was sent.");
    }

    private static void CheckOtherVideoFields(IpRemotePictureTest test, JsonObject video)
    {
        var baseline = (JsonObject)test.VideoBaseline.DeepClone();
        var current = (JsonObject)video.DeepClone();
        baseline.Remove(test.Control); current.Remove(test.Control);
        if (!JsonNode.DeepEquals(baseline, current))
            throw new InvalidOperationException("Other reported video fields changed or disappeared. Stop and inspect the TV in the original context; no further write was sent.");
    }

    private IpRemotePictureTest RequirePictureTest(Guid id, IpRemoteProfile profile)
    {
        var test = GetSnapshot().PictureTest;
        if (test is null || test.Id != id || test.Profile != profile)
            throw new InvalidOperationException("This test belongs to a different or outdated saved profile. Prepare again, or return to the original profile for recovery.");
        return test;
    }

    private void EnsureNoPendingPictureTest(bool allowTemporaryWhiteBalanceRead = false)
    {
        if (GetSnapshot().RgbProbe?.NeedsRecovery == true)
            throw new InvalidOperationException("Restore/end the pending RGB experiment on Batch / RGB test before sending other commands or changing displays.");
        if (!allowTemporaryWhiteBalanceRead && GetSnapshot().Menu.WhiteBalanceRead?.NeedsRestore == true)
            throw new InvalidOperationException("Restore/check the interrupted 20-point white-balance read on Menu before sending more commands or changing displays.");
        if (GetSnapshot().Menu.Update?.NeedsReview == true)
            throw new InvalidOperationException("Check the interrupted update on the Menu page before changing display context or sending more commands.");
        if (GetSnapshot().CommandTrial?.RequiresReview == true)
            throw new InvalidOperationException("Review the pending IP command first. Check/handle its effect in Communication log recovery before starting another write or replacing its display context.");
        if (GetSnapshot().PictureTest?.RequiresRecovery == true)
            throw new InvalidOperationException("Resolve the pending picture operation first: check and restore the original value, or confirm manual restoration. Its original context must not be replaced.");
    }

    private async Task SavePictureTestAsync(IpRemotePictureTest test)
    {
        var temporary = PictureTestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(test, JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporary);
        File.Move(temporary, PictureTestPath, overwrite: true);
        Update(state => state with { PictureTest = test, Status = test.Message });
        await RememberPictureCapabilityAsync(test).ConfigureAwait(false);
    }

    private async Task<IpRemotePictureTest?> LoadPictureTestAsync()
    {
        if (!File.Exists(PictureTestPath)) return null;
        var test = JsonSerializer.Deserialize<IpRemotePictureTest>(await File.ReadAllTextAsync(PictureTestPath).ConfigureAwait(false))
            ?? throw new JsonException("The picture recovery file is empty.");
        if (test.Profile?.Connection is null || !SamsungIpRemotePictureControl.IsSupported(test.Control) || !Enum.IsDefined(test.Purpose) || !Enum.IsDefined(test.Stage)
            || test.Original is < 0 or > 100 || test.Target is < 0 or > 100 || test.Target == test.Original
            || (test.Purpose == IpRemotePicturePurpose.Verification && test.Target != SamsungIpRemotePictureControl.TestTarget(test.Original))
            || (test.DirectChangeKept && (test.Purpose != IpRemotePicturePurpose.DirectAdjustment || !test.WriteAttempted || !test.ChangeReadbackConfirmed || test.Stage != IpRemotePictureStage.Completed))
            || (test.RejectedUnchanged && (!test.WriteAttempted || test.ChangeReadbackConfirmed || test.DirectChangeKept || test.Stage != IpRemotePictureStage.Stopped || test.LastReadback != test.Original))
            || string.IsNullOrWhiteSpace(test.ReportedInput) || string.IsNullOrWhiteSpace(test.ReportedPictureMode) || test.VideoBaseline is null)
            throw new JsonException("The picture recovery file is invalid. Do not discard it before checking the TV manually.");
        _ = test.Profile.Endpoint;
        return test.RequiresRecovery ? test with
        {
            Stage = IpRemotePictureStage.RecoveryRequired,
            Message = $"Unresolved experiment from a previous session. Original {test.ControlName}: {test.Original}. No request was sent on startup. Check the original display/context before explicit recovery."
        }
            : test.Stage == IpRemotePictureStage.Prepared ? test with { Stage = IpRemotePictureStage.Stopped, Message = "Previous-session baseline is stale. Prepare again; no request was sent on startup." }
            : test;
    }
}
