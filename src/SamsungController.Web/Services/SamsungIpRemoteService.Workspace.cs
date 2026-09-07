using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string PictureBatchPath => Path.Combine(_directory, "picture-batch.json");

    public Task RefreshWorkspaceAsync() => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingPictureTest();
        Update(state => state with { WorkspaceReading = null, DirectPictureReading = null });
        var read = await ReadVideoCheckpointAsync(profile, "Picture workspace · refresh", cancellation).ConfigureAwait(false);
        SetWorkspaceReading(profile, read.Input, read.Mode, read.Video);
    });

    public Task ApplyPictureBatchAsync(Guid readingId, IReadOnlyDictionary<string, int> targets, bool conditionsConfirmed)
    {
        // Freeze the submitted plan before awaiting the operation gate. Editing a
        // caller's dictionary cannot change commands halfway through a batch.
        var requested = targets.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return RunPictureOperationAsync(async (profile, cancellation) =>
        {
            EnsureNoPendingPictureTest();
            var snapshot = GetSnapshot();
            var read = snapshot.WorkspaceReading;
            if (read is null || read.Id != readingId || read.Profile != profile || _timeProvider.GetUtcNow() - read.ReadAt > TimeSpan.FromMinutes(2))
                throw new InvalidOperationException("Refresh the picture workspace first: its reading is missing, stale, or belongs to another profile.");
            if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the unchanged display/input/mode/signal and valid target values before applying.");
            if (requested.Count is < 1 or > 3 || requested.Any(item => !SamsungIpRemotePictureControl.IsSupported(item.Key) || item.Value is < 0 or > 100))
                throw new InvalidOperationException("Only Contrast, Color, and Sharpness integer targets within 0–100 are permitted. The TV's actual range must be checked separately.");
            // Validate the entire plan before the first request, including later rows.
            var steps = new List<IpRemoteBatchStep>();
            foreach (var control in SamsungIpRemotePictureControl.All.Where(item => requested.ContainsKey(item.Id)))
            {
                var original = read.Value(control.Id) ?? throw new InvalidOperationException($"{control.Name} was not reported as a valid integer. No command was sent.");
                if (requested[control.Id] == original) continue;
                var range = profile.RangeFor(control.Id);
                if (!range.Contains(requested[control.Id]))
                    throw new InvalidOperationException($"{control.Name} must be within its configured range {range.Minimum}–{range.Maximum}. No command was sent.");
                if (!snapshot.ControlCapabilities.Any(item => item.Matches(profile, read.ReportedInput, read.ReportedPictureMode, control.Id)))
                    throw new InvalidOperationException($"{control.Name} is not verified in this context. Verify it before including it in a batch; no command was sent.");
                steps.Add(new(control.Id, original, requested[control.Id], Guid.NewGuid()));
            }
            if (steps.Count == 0) throw new InvalidOperationException("There are no changed values to apply. No command was sent.");
            var batch = new IpRemotePictureBatch
            {
                Profile = profile,
                StartedAt = _timeProvider.GetUtcNow(),
                ReportedInput = read.ReportedInput,
                ReportedPictureMode = read.ReportedPictureMode,
                Steps = steps.ToArray()
            };
            var expectedVideo = (JsonObject)read.VideoBaseline.DeepClone();
            Update(state => state with { WorkspaceReading = null, DirectPictureReading = null });
            try
            {
                await SavePictureBatchAsync(batch).ConfigureAwait(false); // All originals durable before any write.
                for (var index = 0; index < batch.Steps.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var step = batch.Steps[index];
                    batch = SetBatchStep(batch, index, IpRemoteBatchStepStage.Checking, $"Checking {step.Control} ({index + 1}/{batch.Steps.Count}).");
                    await SavePictureBatchAsync(batch).ConfigureAwait(false);
                    var before = await ReadVideoCheckpointAsync(profile, $"Batch · recheck {step.Control}", cancellation).ConfigureAwait(false);
                    if (before.Input != read.ReportedInput || before.Mode != read.ReportedPictureMode || !JsonNode.DeepEquals(expectedVideo, before.Video))
                        throw new InvalidOperationException("The TV's input, picture mode, or video values changed outside this batch. Stopped before the next write; refresh and review the remaining targets.");
                    var operation = new IpRemotePictureTest
                    {
                        Id = step.OperationId,
                        Purpose = IpRemotePicturePurpose.DirectAdjustment,
                        Profile = profile,
                        Control = step.Control,
                        Original = step.Original,
                        Target = step.Target,
                        PreparedAt = _timeProvider.GetUtcNow(),
                        ReportedInput = read.ReportedInput,
                        ReportedPictureMode = read.ReportedPictureMode,
                        VideoBaseline = (JsonObject)expectedVideo.DeepClone(),
                        LastReadback = step.Original
                    };
                    batch = SetBatchStep(batch, index, IpRemoteBatchStepStage.Applying, $"Applying {step.Control} ({index + 1}/{batch.Steps.Count}). Stop prevents further requests; it does not undo a delivered write.");
                    await SavePictureBatchAsync(batch).ConfigureAwait(false);
                    // Reuse the proven durable write-once/readback/recovery path.
                    // The outer operation holds the gate for the ENTIRE batch.
                    await ApplyPreparedPictureAsync(operation, cancellation).ConfigureAwait(false);
                    expectedVideo = (JsonObject)GetSnapshot().DirectPictureReading!.VideoBaseline.DeepClone();
                    batch = SetBatchStep(batch, index, IpRemoteBatchStepStage.Confirmed, $"{step.Control} confirmed by readback. Its new value is kept on the TV.");
                    await SavePictureBatchAsync(batch).ConfigureAwait(false);
                }
                batch = batch with { Stage = IpRemoteBatchStage.Completed, Message = $"All {batch.Steps.Count} changes confirmed by readback. New values are kept on the TV." };
                await SavePictureBatchAsync(batch).ConfigureAwait(false);
                SetWorkspaceReading(profile, read.ReportedInput, read.ReportedPictureMode, expectedVideo);
            }
            catch (Exception error) when (error is InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
            {
                batch = ReconcileStoppedBatch(batch, GetSnapshot().PictureTest) with
                {
                    Message = (cancellation.IsCancellationRequested ? "Stopped by user." : error is IOException or UnauthorizedAccessException or JsonException ? "Batch recovery/progress could not be saved." : error.Message)
                        + " Earlier confirmed changes stay on the TV. Resolve any pending recovery, then refresh. No automatic rollback, retry, or resume."
                };
                Update(state => state with { PictureBatch = batch });
                try { await SavePictureBatchAsync(batch).ConfigureAwait(false); }
                catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException)
                { Update(state => state with { StorageWarning = "Batch progress could not be saved. Keep the original values shown here; check the TV before closing." }); }
                throw;
            }
        });
    }

    private void SetWorkspaceReading(IpRemoteProfile profile, string input, string mode, JsonObject video) =>
        Update(state => state with { WorkspaceReading = new(Guid.NewGuid(), profile, _timeProvider.GetUtcNow(), input, mode, (JsonObject)video.DeepClone()) });

    private static IpRemotePictureBatch SetBatchStep(IpRemotePictureBatch batch, int index, IpRemoteBatchStepStage stage, string message) =>
        batch with { Steps = batch.Steps.Select((step, i) => i == index ? step with { Stage = stage } : step).ToArray(), Message = message };

    private static IpRemotePictureBatch ReconcileStoppedBatch(IpRemotePictureBatch batch, IpRemotePictureTest? operation) => batch with
    {
        Stage = IpRemoteBatchStage.Stopped,
        Steps = batch.Steps.Select(step => step.Stage switch
        {
            IpRemoteBatchStepStage.Pending => step with { Stage = IpRemoteBatchStepStage.NotSent },
            IpRemoteBatchStepStage.Checking or IpRemoteBatchStepStage.Applying => step with
            {
                Stage = operation?.Id == step.OperationId && operation.DirectChangeKept ? IpRemoteBatchStepStage.Confirmed
                    : operation?.Id == step.OperationId && operation.RejectedUnchanged ? IpRemoteBatchStepStage.RejectedUnchanged
                    : operation?.Id == step.OperationId && operation.RequiresRecovery ? IpRemoteBatchStepStage.Uncertain : IpRemoteBatchStepStage.NotSent
            },
            _ => step
        }).ToArray(),
        Message = "Batch interrupted in a previous session. Progress is historical; no request was sent or resumed on startup. Resolve pending recovery, then refresh the TV values."
    };

    private async Task SavePictureBatchAsync(IpRemotePictureBatch batch)
    {
        var temporary = PictureBatchPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(batch, JsonOptions)).ConfigureAwait(false);
        RestrictFile(temporary);
        File.Move(temporary, PictureBatchPath, overwrite: true);
        Update(state => state with { PictureBatch = batch, Status = batch.Message });
    }

    private async Task<IpRemotePictureBatch?> LoadPictureBatchAsync(IpRemotePictureTest? operation)
    {
        if (!File.Exists(PictureBatchPath)) return null;
        var batch = JsonSerializer.Deserialize<IpRemotePictureBatch>(await File.ReadAllTextAsync(PictureBatchPath).ConfigureAwait(false))
            ?? throw new JsonException("The picture batch journal is empty.");
        if (batch.Profile?.Connection is null || batch.Id == Guid.Empty || !Enum.IsDefined(batch.Stage) || batch.Steps is null || batch.Steps.Count is < 1 or > 3
            || string.IsNullOrWhiteSpace(batch.ReportedInput) || string.IsNullOrWhiteSpace(batch.ReportedPictureMode)
            || batch.Steps.Any(step => step is null || !SamsungIpRemotePictureControl.IsSupported(step.Control) || step.OperationId == Guid.Empty
                || !Enum.IsDefined(step.Stage) || step.Original is < 0 or > 100 || step.Target is < 0 or > 100 || step.Target == step.Original)
            || batch.Steps.Select(step => step.Control).Distinct().Count() != batch.Steps.Count
            || batch.Steps.Select(step => step.OperationId).Distinct().Count() != batch.Steps.Count
            || (batch.Stage == IpRemoteBatchStage.Completed && batch.Steps.Any(step => step.Stage != IpRemoteBatchStepStage.Confirmed)))
            throw new JsonException("The picture batch journal is invalid. Check the TV before changing or discarding recovery records.");
        _ = batch.Profile.Endpoint;
        return batch.Stage == IpRemoteBatchStage.Running ? ReconcileStoppedBatch(batch, operation) : batch;
    }
}
