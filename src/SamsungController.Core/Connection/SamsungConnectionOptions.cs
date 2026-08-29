namespace SamsungController.Core.Connection;

public sealed record SamsungConnectionOptions
{
    public required string Host { get; init; }

    public string ApplicationName { get; init; } = "SamsungController";

    public bool Secure { get; init; } = true;

    public int? Port { get; init; }

    public string? Token { get; init; }

    public bool AllowUntrustedCertificate { get; init; } = true;

    public bool AutoReconnect { get; init; } = true;

    public int MaxReconnectAttempts { get; init; } = 5;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PostConnectWarmup { get; init; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan ReconnectAfterIdle { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("A TV host name or IP address is required.", nameof(Host));
        }

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new ArgumentException("An application name is required.", nameof(ApplicationName));
        }

        if (Port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }

        if (PairingTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PairingTimeout));
        }

        if (KeepAliveInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval));
        }

        if (KeepAliveTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveTimeout));
        }

        if (KeepAliveInterval == TimeSpan.Zero && KeepAliveTimeout > TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A keep-alive interval is required when a keep-alive timeout is enabled.",
                nameof(KeepAliveInterval));
        }

        if (PostConnectWarmup < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PostConnectWarmup));
        }

        if (ReconnectAfterIdle < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectAfterIdle));
        }

        if (MaxReconnectAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts));
        }
    }
}
