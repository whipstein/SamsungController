namespace SamsungController.Core.Connection;

public class SamsungConnectionException : Exception
{
    public SamsungConnectionException(string message)
        : base(message)
    {
    }

    public SamsungConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SamsungAuthenticationException : SamsungConnectionException
{
    public SamsungAuthenticationException(string message)
        : base(message)
    {
    }
}

public sealed class SamsungPairingTimeoutException : SamsungConnectionException
{
    public SamsungPairingTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
