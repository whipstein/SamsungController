using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    // Reachable HTTPS and readable cached settings do not mean the screen is on.
    // This is always read-only, independent of the optional value preflight.
    public Task CheckMenuPowerAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        await ReadMenuPowerAsync(profile, cancellation).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Status = menu.PowerWarning ?? "Power read completed. Refresh state after outside changes before adjusting settings." });
    });

    private async Task ReadMenuPowerAsync(IpRemoteProfile profile, CancellationToken cancellation)
    {
        var exchange = await MenuQueryAsync(profile, "powerControl", cancellation).ConfigureAwait(false);
        StoreMenuRead("powerControl", exchange);
        if (IsConnectionFailure(exchange.Outcome) || exchange.Outcome == SamsungIpRemoteOutcome.Canceled) RequireSuccess(exchange);
    }

    private async Task RequireMenuPowerOnAsync(IpRemoteProfile profile, CancellationToken cancellation)
    {
        await ReadMenuPowerAsync(profile, cancellation).ConfigureAwait(false);
        if (GetSnapshot().Menu.PowerWarning is { } reason)
        {
            UpdateMenu(menu => menu with { ActionWarning = reason });
            throw new MenuChangeRejectedException(reason);
        }
    }

    // Only a setting setter with independently confirmed unchanged readback identifies
    // a skippable control. Power-off and selector failures must still stop recall.
    private sealed class MenuChangeRejectedException(string message, string? rejectedControlId = null) : InvalidOperationException(message)
    {
        public string? RejectedControlId { get; } = rejectedControlId;
    }
    private static bool IsMenuRejection(SamsungIpRemoteExchange exchange) => exchange.RpcErrorCode is not null
        && exchange.Outcome is SamsungIpRemoteOutcome.RpcError or SamsungIpRemoteOutcome.Unsupported;

    // Restore only local targets. Never send rollback commands or discard unrelated edits.
    private void RevertMenuTargets(IEnumerable<string> controls, string message)
    {
        var ids = controls.Distinct(StringComparer.Ordinal).ToArray();
        UpdateMenu(menu => menu with { Pending = menu.Pending.Where(pair => !ids.Contains(pair.Key)).ToDictionary(),
            RejectedControls = ids, RejectionRevision = menu.RejectionRevision + 1, ActionWarning = message });
    }
}
