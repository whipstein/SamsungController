using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed record IpMenuAvailability(bool Visible, bool Conditional, string? Reason)
{
    // Only current-context getter evidence drives visibility. A rejected setter
    // (for example an out-of-range number) never hides the entire control.
    public static IpMenuAvailability For(IpMenuSnapshot menu, IpMenuControl control)
    {
        var reading = control.IsIndexed ? menu.IndexedReadings.GetValueOrDefault(control.Id) : menu.Readings.GetValueOrDefault(control.Method);
        if (reading?.Outcome == SamsungIpRemoteOutcome.Unsupported)
            return new(false, false, $"This query is not supported ({reading.RpcErrorCode}) in the current display/state. Refresh to check again.");
        foreach (var requirement in control.Command.Requirements)
        {
            if (control.IsIndexed && requirement.Field == IpMenuGrids.ForSection(control.Section)!.SelectorField) continue;
            var value = menu.Readings.GetValueOrDefault(requirement.Method)?.Values?[requirement.Field]?.ToString();
            if (value is null || !requirement.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                var name = IpMenuCatalog.Controls.FirstOrDefault(item => item.Method == requirement.Method && item.Field == requirement.Field)?.Name ?? requirement.Field;
                return new(true, true, $"Requires {name}: {string.Join(" / ", requirement.AllowedValues)}. Current: {value ?? "not reported"}. Change the prerequisite, then refresh if needed.");
            }
        }
        if (reading is { Outcome: SamsungIpRemoteOutcome.RpcError, RpcErrorCode: -32002 or -32003 or -32602 })
            return new(false, false, $"Query rejected ({reading.RpcErrorCode}) in the current display/state; not permanently classified as unsupported. Refresh to check again.");
        if (reading is { Outcome: SamsungIpRemoteOutcome.Success } && menu.Value(control) is null)
            return new(false, false, "This value was not included in the TV reply. Refresh to check again.");
        return new(true, false, null);
    }
}
