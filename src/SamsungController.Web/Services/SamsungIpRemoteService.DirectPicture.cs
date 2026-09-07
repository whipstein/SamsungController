using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string ControlCapabilitiesPath => Path.Combine(_directory, "control-capabilities.json");

    public Task ReadDirectPictureAsync(string control = "contrast") => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingPictureTest();
        var definition = SamsungIpRemotePictureControl.Get(control);
        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        var read = await ReadPictureCheckpointAsync(profile, control, $"Direct {definition.Name} · refresh (read only)", cancellation).ConfigureAwait(false);
        SetDirectPictureReading(profile, control, read.Input, read.Mode, read.Value, read.Video);
    });

    public Task ApplyDirectPictureAsync(Guid readingId, int target, bool conditionsConfirmed) => RunPictureOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingPictureTest();
        var read = GetSnapshot().DirectPictureReading;
        if (read is null || read.Id != readingId || read.Profile != profile || _timeProvider.GetUtcNow() - read.ReadAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Read the direct control again before applying: the reading is missing, stale, or belongs to a different profile.");
        if (!conditionsConfirmed)
            throw new InvalidOperationException("Confirm that the target is valid on this TV and that input, picture mode, and signal conditions are unchanged.");
        var range = profile.RangeFor(read.Control);
        if (!range.Contains(target) || target == read.Value)
            throw new InvalidOperationException($"Choose a different {read.Control} value within the configured range {range.Minimum}–{range.Maximum}. No write was sent.");
        if (!GetSnapshot().ControlCapabilities.Any(item => item.Matches(profile, read.ReportedInput, read.ReportedPictureMode, read.Control)))
            throw new InvalidOperationException($"Direct {read.Control} is locked: complete its guarded verification for this display, firmware, annotated conditions, and reported input/mode first.");

        Update(state => state with { DirectPictureReading = null, WorkspaceReading = null });
        var before = await ReadPictureCheckpointAsync(profile, read.Control, $"Direct {read.Control} · recheck before apply", cancellation).ConfigureAwait(false);
        var adjustment = new IpRemotePictureTest
        {
            Purpose = IpRemotePicturePurpose.DirectAdjustment,
            Control = read.Control,
            Profile = profile,
            PreparedAt = _timeProvider.GetUtcNow(),
            Original = read.Value,
            Target = target,
            ReportedInput = read.ReportedInput,
            ReportedPictureMode = read.ReportedPictureMode,
            VideoBaseline = read.VideoBaseline,
            LastReadback = read.Value
        };
        CheckContext(adjustment, before.Input, before.Mode);
        CheckOtherVideoFields(adjustment, before.Video);
        if (before.Value != read.Value)
            throw new InvalidOperationException($"{adjustment.ControlName} changed from {read.Value} to {before.Value} since the displayed reading. Read again before applying; no write was sent.");
        await ApplyPreparedPictureAsync(adjustment, cancellation).ConfigureAwait(false);
    });

    public Task UndoDirectPictureAsync(Guid operationId, bool conditionsConfirmed)
    {
        if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the original display and conditions before undoing this direct picture adjustment.");
        return RestorePictureTestAsync(operationId, undoConfirmed: true);
    }

    private void SetDirectPictureReading(IpRemoteProfile profile, string control, string input, string mode, int value, JsonObject video) =>
        Update(state => state with { DirectPictureReading = new(Guid.NewGuid(), profile, _timeProvider.GetUtcNow(), input, mode, value, (JsonObject)video.DeepClone()) { Control = control } });

    private async Task RememberPictureCapabilityAsync(IpRemotePictureTest test)
    {
        if (!test.Verified) return;
        var capability = new IpRemoteControlCapability
        {
            Control = test.Control,
            Profile = test.Profile,
            ReportedInput = test.ReportedInput,
            ReportedPictureMode = test.ReportedPictureMode,
            EvidenceId = test.Id,
            TestStartedAt = test.PreparedAt,
            Original = test.Original,
            TestedTarget = test.Target,
            ReadVerified = true,
            WriteVerified = true
        };
        var existing = GetSnapshot().ControlCapabilities;
        if (existing.Any(item => item.Key == capability.Key && item.EvidenceId == capability.EvidenceId)) return;
        var updated = existing.Where(item => item.Key != capability.Key).Append(capability).ToArray();
        try
        {
            var temporary = ControlCapabilitiesPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions)).ConfigureAwait(false);
            RestrictFile(temporary);
            File.Move(temporary, ControlCapabilitiesPath, overwrite: true);
            Update(state => state with { ControlCapabilities = updated });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Update(state => state with { StorageWarning = "The completed picture test is saved, but its separate capability record could not be saved. Direct control remains locked unless earlier evidence exists. Fix private-folder access and restart before beginning another test." });
        }
    }

    private async Task<IReadOnlyList<IpRemoteControlCapability>> LoadControlCapabilitiesAsync()
    {
        if (!File.Exists(ControlCapabilitiesPath)) return [];
        var capabilities = JsonSerializer.Deserialize<IpRemoteControlCapability[]>(await File.ReadAllTextAsync(ControlCapabilitiesPath).ConfigureAwait(false))
            ?? throw new JsonException("The IP Remote capability file is empty.");
        foreach (var item in capabilities)
        {
            if (item?.Profile?.Connection is null || !SamsungIpRemotePictureControl.IsSupported(item.Control) || item.EvidenceId == Guid.Empty
                || !item.ReadVerified || !item.WriteVerified || item.Original is < 0 or > 100 || item.TestedTarget != SamsungIpRemotePictureControl.TestTarget(item.Original)
                || string.IsNullOrWhiteSpace(item.ReportedInput) || string.IsNullOrWhiteSpace(item.ReportedPictureMode))
                throw new JsonException("Invalid private IP Remote capability record. No direct control was enabled.");
            _ = item.Profile.Endpoint;
        }
        if (capabilities.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != capabilities.Length)
            throw new JsonException("Duplicate private IP Remote capability contexts. No direct control was enabled.");
        return capabilities;
    }
}
