using SamsungController.Core.Connection;
using SamsungController.Web.Services;

namespace SamsungController.Web.Tests;

public sealed class ConnectionStatePresentationTests
{
    [Theory]
    [InlineData(SamsungConnectionState.Disconnected, false)]
    [InlineData(SamsungConnectionState.Connecting, true)]
    [InlineData(SamsungConnectionState.Pairing, true)]
    [InlineData(SamsungConnectionState.Connected, true)]
    [InlineData(SamsungConnectionState.Reconnecting, true)]
    [InlineData(SamsungConnectionState.Disconnecting, true)]
    [InlineData(SamsungConnectionState.Faulted, false)]
    public void DisconnectIsOfferedOnlyWhileAConnectionIsActive(
        SamsungConnectionState state,
        bool expected)
    {
        Assert.Equal(expected, ConnectionStatePresentation.CanDisconnect(state));
    }
}
