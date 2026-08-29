using SamsungController.Core.Connection;

namespace SamsungController.Web.Services;

internal static class ConnectionStatePresentation
{
    public static bool CanDisconnect(SamsungConnectionState state) => state is
        SamsungConnectionState.Connecting or
        SamsungConnectionState.Pairing or
        SamsungConnectionState.Connected or
        SamsungConnectionState.Reconnecting or
        SamsungConnectionState.Disconnecting;
}
