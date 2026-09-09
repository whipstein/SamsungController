using System.Globalization;
using System.Text.Json.Nodes;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    public string? MenuRowNudgeDisabledReason(string section, string value, int delta, IReadOnlyDictionary<string, string>? edits = null)
    {
        lock (_sync)
        {
            try { _ = MenuRowNudgeTargets(section, value, delta, edits); return null; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.Message; }
        }
    }

    public Task NudgeMenuRowAsync(string section, string value, int delta, IReadOnlyDictionary<string, string>? edits = null)
    {
        Task completion;
        MenuNudgeSession? start = null;
        lock (_sync)
        {
            var targets = MenuRowNudgeTargets(section, value, delta, edits);
            if (_snapshot.Menu.Preferences.ApplyImmediately)
                (completion, start) = QueueMenuTargetsLocked(targets);
            else
            {
                StageMenuValuesLocked(targets.Select(item => (item.Control, (JsonNode)JsonValue.Create(item.Target)!)).ToArray());
                completion = Task.CompletedTask;
            }
        }
        Changed?.Invoke();
        if (start is not null) _ = RunMenuNudgesAsync(start);
        return completion;
    }

    // Caller holds _sync. Use the latest requested value, never an older reply.
    // Reject the entire click at a boundary rather than altering RGB offsets.
    private (IpMenuControl Control, int Target)[] MenuRowNudgeTargets(string section, string value, int delta, IReadOnlyDictionary<string, string>? edits)
    {
        if (delta is not (-1 or 1)) throw new ArgumentException("A group click must be +1 or -1.");
        var grid = IpMenuGrids.ForSection(section) ?? throw new ArgumentException("Unknown calibration grid.");
        if (!grid.Values.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unknown calibration row.");
        if (_snapshot.Menu.Pending.Keys.Any(id => IpMenuCatalog.Get(id).RequiresSeparateApply))
            throw new InvalidOperationException("Apply or discard the pending mode or selector change before adjusting RGB together.");
        return grid.Row(value).Select(control =>
        {
            if (MenuNudgeDisabledReason(control) is { } reason) throw new InvalidOperationException(reason);
            var original = _snapshot.Menu.Preferences.ApplyImmediately ? MenuNudgeOriginal(control)
                : (_snapshot.Menu.Pending.GetValueOrDefault(control.Id)?.Target ?? _snapshot.Menu.Value(control))!.GetValue<int>();
            if (edits?.TryGetValue(control.Id, out var text) == true)
            {
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out original)
                    || original < control.Parameter.Minimum || original > control.Parameter.Maximum)
                    throw new InvalidOperationException("Finish entering a valid number in each channel before adjusting RGB together.");
            }
            if (delta < 0 && original <= control.Parameter.Minimum || delta > 0 && original >= control.Parameter.Maximum)
                throw new InvalidOperationException($"{control.Name} is at its limit. RGB together preserves the differences between channels.");
            return (control, original + delta);
        }).ToArray();
    }
}
