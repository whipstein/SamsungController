using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    private string ControlCapabilitiesPath => Path.Combine(_directory, "control-capabilities.json");

    public Task ReadDirectContrastAsync() => RunContrastOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingContrastTest();
        Update(state => state with { DirectContrastReading = null });
        var read = await ReadContrastCheckpointAsync(profile, "Direct contrast · refresh (read only)", cancellation).ConfigureAwait(false);
        SetDirectContrastReading(profile, read.Input, read.Mode, read.Contrast, read.Video);
    });

    public Task ApplyDirectContrastAsync(Guid readingId, int target, bool conditionsConfirmed) => RunContrastOperationAsync(async (profile, cancellation) =>
    {
        EnsureNoPendingContrastTest();
        var read = GetSnapshot().DirectContrastReading;
        if (read is null || read.Id != readingId || read.Profile != profile || _timeProvider.GetUtcNow() - read.ReadAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Read direct contrast again before applying: the reading is missing, stale, or belongs to a different profile.");
        if (!conditionsConfirmed)
            throw new InvalidOperationException("Confirm that the target is valid on this TV and that input, picture mode, and signal conditions are unchanged.");
        if (target is < 0 or > 100 || target == read.Value)
            throw new InvalidOperationException("Choose a different integer contrast within the protocol envelope 0–100 and this TV's actual range. No write was sent.");
        if (!GetSnapshot().ControlCapabilities.Any(item => item.Matches(profile, read.ReportedInput, read.ReportedPictureMode)))
            throw new InvalidOperationException("Direct contrast is locked: complete the guarded contrast verification for this display, firmware, annotated conditions, and reported input/mode first.");

        Update(state => state with { DirectContrastReading = null });
        var before = await ReadContrastCheckpointAsync(profile, "Direct contrast · recheck before apply", cancellation).ConfigureAwait(false);
        var adjustment = new IpRemoteContrastTest
        {
            Purpose = IpRemoteContrastPurpose.DirectAdjustment,
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
        if (before.Contrast != read.Value)
            throw new InvalidOperationException($"Contrast changed from {read.Value} to {before.Contrast} since the displayed reading. Read again before applying; no write was sent.");
        await ApplyPreparedContrastAsync(adjustment, cancellation).ConfigureAwait(false);
    });

    public Task UndoDirectContrastAsync(Guid operationId, bool conditionsConfirmed)
    {
        if (!conditionsConfirmed) throw new InvalidOperationException("Confirm the original display and conditions before undoing this direct contrast adjustment.");
        return RestoreContrastTestAsync(operationId, undoConfirmed: true);
    }

    private void SetDirectContrastReading(IpRemoteProfile profile, string input, string mode, int value, JsonObject video) =>
        Update(state => state with { DirectContrastReading = new(Guid.NewGuid(), profile, _timeProvider.GetUtcNow(), input, mode, value, (JsonObject)video.DeepClone()) });

    private async Task RememberContrastCapabilityAsync(IpRemoteContrastTest test)
    {
        if (!test.Verified) return;
        var capability = new IpRemoteControlCapability
        {
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
            Update(state => state with { StorageWarning = "The completed contrast test is saved, but its separate capability record could not be saved. Direct control remains locked unless earlier evidence exists. Fix private-folder access and restart before beginning another test." });
        }
    }

    private async Task<IReadOnlyList<IpRemoteControlCapability>> LoadControlCapabilitiesAsync()
    {
        if (!File.Exists(ControlCapabilitiesPath)) return [];
        var capabilities = JsonSerializer.Deserialize<IpRemoteControlCapability[]>(await File.ReadAllTextAsync(ControlCapabilitiesPath).ConfigureAwait(false))
            ?? throw new JsonException("The IP Remote capability file is empty.");
        foreach (var item in capabilities)
        {
            if (item?.Profile?.Connection is null || item.Control != "contrast" || item.EvidenceId == Guid.Empty
                || !item.ReadVerified || !item.WriteVerified || item.Original is < 1 or > 100 || item.TestedTarget != item.Original - 1
                || string.IsNullOrWhiteSpace(item.ReportedInput) || string.IsNullOrWhiteSpace(item.ReportedPictureMode))
                throw new JsonException("Invalid private IP Remote capability record. No direct control was enabled.");
            _ = item.Profile.Endpoint;
        }
        if (capabilities.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != capabilities.Length)
            throw new JsonException("Duplicate private IP Remote capability contexts. No direct control was enabled.");
        return capabilities;
    }
}
