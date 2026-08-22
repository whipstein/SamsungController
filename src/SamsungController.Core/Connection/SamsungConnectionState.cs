namespace SamsungController.Core.Connection;

public enum SamsungConnectionState
{
    Disconnected,
    Connecting,
    Pairing,
    Connected,
    Reconnecting,
    Disconnecting,
    Faulted
}

public sealed class SamsungConnectionStateChangedEventArgs(
    SamsungConnectionState previous,
    SamsungConnectionState current,
    Exception? error = null) : EventArgs
{
    public SamsungConnectionState Previous { get; } = previous;

    public SamsungConnectionState Current { get; } = current;

    public Exception? Error { get; } = error;
}
