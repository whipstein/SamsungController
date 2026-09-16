using SamsungController.Core.IpRemote;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    // Reachable HTTPS and readable cached settings do not mean the screen is on.
    // This is always read-only, independent of the optional value preflight.
    public Task CheckMenuPowerAsync() => RunMenuOperationAsync(async (profile, cancellation) =>
    {
        await ReadMenuPowerAsync(profile, cancellation).ConfigureAwait(false);
        UpdateMenu(menu => menu with { Status = menu.PowerDisabledReason ?? "TV is on. Refresh state after outside changes before adjusting settings." });
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
        if (GetSnapshot().Menu.PowerDisabledReason is { } reason) throw new InvalidOperationException(reason);
    }
}
