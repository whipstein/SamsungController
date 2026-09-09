using System.Text.Json.Nodes;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    public string? MenuGridResetDisabledReason(string section)
    {
        lock (_sync)
        {
            try { _ = GridResetControls(section); return null; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.Message; }
        }
    }

    /// <summary>Reset only this section's indexed RGB values, respecting Apply mode.</summary>
    public async Task<int> ResetMenuGridAsync(string section)
    {
        var count = 0;
        await RunMenuOperationAsync(async (profile, cancellation) =>
        {
            IpMenuDraft[] drafts;
            bool immediate;
            lock (_sync)
            {
                var controls = GridResetControls(section, ownsOperation: true);
                var nominal = IpMenuGrids.ForSection(section)!.NominalValue;
                var menu = _snapshot.Menu;
                var pending = new Dictionary<string, IpMenuDraft>(menu.Pending);
                // Validate the entire grid before replacing any pending targets.
                foreach (var control in controls)
                {
                    var target = JsonValue.Create(nominal)!;
                    if (EquivalentCommandValue(menu.Value(control), target)) pending.Remove(control.Id);
                    else pending[control.Id] = new(control.Id, target, menu.Value(control)!.DeepClone(),
                        MenuPrerequisites(menu, control, forEditing: true), menu.Input!, menu.PictureMode!);
                }
                cancellation.ThrowIfCancellationRequested();
                drafts = controls.Where(control => pending.ContainsKey(control.Id)).Select(control => pending[control.Id]).ToArray();
                count = drafts.Length;
                immediate = menu.Preferences.ApplyImmediately;
                _snapshot = _snapshot with { Menu = menu with { Pending = pending,
                    Status = count == 0 ? "All RGB values in this section are already nominal. No TV commands were sent."
                        : $"Prepared {count} RGB values for reset to {nominal}. Other sections are unchanged." } };
            }
            Changed?.Invoke();
            if (!immediate || count == 0) return;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                // Never apply unrelated pending edits. Reuse the verified RGB
                // selector/readback path; there is no factory-reset RPC or key.
                await ApplyMenuCoreAsync(profile, cancellation, drafts).ConfigureAwait(false);
            }
            finally
            {
                // An interrupted reset must not leave its unsent values waiting
                // for an accidental later Apply. Delivered changes are not undone.
                var resetIds = drafts.Select(draft => draft.ControlId).ToHashSet(StringComparer.Ordinal);
                UpdateMenu(menu => menu with { Pending = menu.Pending.Where(pair => !resetIds.Contains(pair.Key)).ToDictionary() });
            }
        }).ConfigureAwait(false);
        return count;
    }

    private IpMenuControl[] GridResetControls(string section, bool ownsOperation = false)
    {
        var grid = IpMenuGrids.ForSection(section) ?? throw new ArgumentException("Unknown calibration grid.");
        if (!_snapshot.Menu.Connected) throw new InvalidOperationException("Connect to the display first.");
        if (_menuNudges is not null || _snapshot.IsBusy && !ownsOperation)
            throw new InvalidOperationException("Stop or finish the current TV operation before resetting values.");
        EnsureMenuWritesAllowed();
        var menu = _snapshot.Menu;
        if (menu.Pending.Keys.Any(id => IpMenuCatalog.Get(id).RequiresSeparateApply))
            throw new InvalidOperationException("Apply or discard the pending input, mode, or selector change before resetting RGB values.");
        var controls = grid.Values.SelectMany(grid.Row).ToArray();
        foreach (var control in controls)
            if (MenuValueDisabledReason(menu, control) is { } reason)
                throw new InvalidOperationException($"{control.Name}: {reason} All rows must be loaded and enabled before resetting the section.");
        return controls;
    }
}
