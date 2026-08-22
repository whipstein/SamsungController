using SamsungController.Core.Connection;
using SamsungController.Core.Devices;
using SamsungController.Core.Diagnostics;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        try
        {
            return await RunAsync(CliArguments.Parse(args), cancellationSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Command.Equals("help", StringComparison.OrdinalIgnoreCase)
            || arguments.HasFlag("--help"))
        {
            PrintUsage();
            return 0;
        }

        var configurationDirectory = Path.GetFullPath(
            arguments.GetOption("--config-dir")
            ?? ApplicationPaths.GetDefaultConfigurationDirectory());
        var settingsPath = Path.Combine(configurationDirectory, "settings.json");
        var tokenPath = Path.GetFullPath(
            arguments.GetOption("--token-file")
            ?? Path.Combine(configurationDirectory, "tokens.json"));
        var settings = await SamsungCliSettings.LoadAsync(settingsPath, cancellationToken)
            .ConfigureAwait(false);
        var tokenStore = new JsonFileSamsungTokenStore(tokenPath);

        if (arguments.Command.Equals("forget", StringComparison.OrdinalIgnoreCase))
        {
            var hostToForget = ResolveOptionalHost(arguments, settings)
                ?? throw new ArgumentException("No TV host was provided or previously configured.");
            await tokenStore.RemoveAsync(hostToForget, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Forgot the saved pairing token for {hostToForget}.");
            return 0;
        }

        if (arguments.Command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var configuredHost = settings.Host ?? "(not configured)";
            var hasToken = settings.Host is not null
                && await tokenStore.LoadAsync(settings.Host, cancellationToken).ConfigureAwait(false) is not null;
            Console.WriteLine($"TV: {settings.Name}");
            Console.WriteLine($"Host: {configuredHost}");
            Console.WriteLine($"Mode: {(settings.Secure ? "secure (wss)" : "non-secure (ws)")}");
            Console.WriteLine($"Token: {(hasToken ? "saved" : "not saved")}");
            Console.WriteLine($"Configuration: {configurationDirectory}");
            return 0;
        }

        if (!IsConnectionCommand(arguments.Command))
        {
            throw new ArgumentException($"Unknown command: {arguments.Command}");
        }

        var host = ResolveRequiredHost(arguments, settings);
        var sameTv = string.Equals(host, settings.Host, StringComparison.OrdinalIgnoreCase);
        var secure = arguments.HasFlag("--insecure")
            ? false
            : arguments.HasFlag("--secure") || !sameTv || settings.Secure;
        var port = ParseNullableInt(arguments.GetOption("--port"))
            ?? (sameTv ? settings.Port : null);
        var applicationName = arguments.GetOption("--name")
            ?? settings.ApplicationName;
        var pairingTimeoutSeconds = ParseNullableInt(arguments.GetOption("--pairing-timeout")) ?? 90;
        var quiet = arguments.HasFlag("--quiet");
        var logPath = Path.GetFullPath(
            arguments.GetOption("--log")
            ?? CreateSessionLogPath(configurationDirectory, arguments.Command));

        var connectionOptions = new SamsungConnectionOptions
        {
            Host = host,
            ApplicationName = applicationName,
            Secure = secure,
            Port = port,
            AllowUntrustedCertificate = !arguments.HasFlag("--strict-tls"),
            PairingTimeout = TimeSpan.FromSeconds(pairingTimeoutSeconds)
        };

        await using var logger = new NdjsonProtocolLogger(logPath);
        await using var client = new SamsungTvClient(
            new ClientWebSocketSamsungTransport(),
            tokenStore,
            [logger]);

        client.ConnectionStateChanged += (_, eventArgs) =>
        {
            if (!quiet)
            {
                Console.Error.WriteLine($"Connection: {eventArgs.Current}");
                if (eventArgs.Current == SamsungConnectionState.Pairing)
                {
                    Console.Error.WriteLine(
                        "Pairing: select Allow on the TV prompt. If no prompt appears, check "
                        + "Settings > All Settings > Connection > External Device Manager > "
                        + "Device Connect Manager > Access Notification (menu names vary by model).");
                }
            }
        };

        var showRaw = arguments.Command.Equals("listen", StringComparison.OrdinalIgnoreCase);
        client.MessageObserved += (_, eventArgs) =>
        {
            if (quiet)
            {
                return;
            }

            var message = eventArgs.Message;
            if (showRaw)
            {
                Console.WriteLine(
                    $"{message.Timestamp:O} {message.Direction.ToString().ToUpperInvariant()} "
                    + $"gen={message.ConnectionGeneration} {message.RawJson}");
            }
            else
            {
                Console.Error.WriteLine(
                    $"{message.Direction.ToString().ToUpperInvariant()} "
                    + (message.Event ?? "unparsed"));
            }
        };

        if (!quiet)
        {
            Console.Error.WriteLine($"Protocol log: {logPath}");
            if (showRaw)
            {
                Console.Error.WriteLine("Warning: raw pairing traffic can contain the Samsung token.");
            }
        }

        await client.ConnectAsync(connectionOptions, cancellationToken).ConfigureAwait(false);

        var updatedSettings = settings with
        {
            Host = host,
            ApplicationName = applicationName,
            Secure = secure,
            Port = port
        };
        await updatedSettings.SaveAsync(settingsPath, cancellationToken).ConfigureAwait(false);

        if (arguments.Command.Equals("connect", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Connected to {host}; pairing token {(client.Token is null ? "was not returned" : "is saved")}.");
            return 0;
        }

        if (arguments.Command.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            var key = arguments.Positionals.FirstOrDefault()
                ?? throw new ArgumentException("The key command requires a Samsung key, such as KEY_UP.");
            var action = ParseAction(arguments.GetOption("--action"));
            await client.SendKeyAsync(key, action, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Sent {action} {key} to {host}.");
            return 0;
        }

        Console.WriteLine($"Listening to {host}. Press Ctrl+C to stop.");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static string ResolveRequiredHost(CliArguments arguments, SamsungCliSettings settings) =>
        ResolveOptionalHost(arguments, settings)
        ?? throw new ArgumentException(
            "No TV host was provided. Run 'samsungctl connect <TV-IP>' first or use --host.");

    private static string? ResolveOptionalHost(CliArguments arguments, SamsungCliSettings settings)
    {
        var positionalHost = arguments.Command.Equals("connect", StringComparison.OrdinalIgnoreCase)
            ? arguments.Positionals.FirstOrDefault()
            : null;

        return positionalHost
            ?? arguments.GetOption("--host")
            ?? settings.Host;
    }

    private static bool IsConnectionCommand(string command) =>
        command.Equals("connect", StringComparison.OrdinalIgnoreCase)
        || command.Equals("key", StringComparison.OrdinalIgnoreCase)
        || command.Equals("listen", StringComparison.OrdinalIgnoreCase);

    private static RemoteKeyAction ParseAction(string? value)
    {
        if (value is null)
        {
            return RemoteKeyAction.Click;
        }

        return Enum.TryParse<RemoteKeyAction>(value, ignoreCase: true, out var action)
            ? action
            : throw new ArgumentException("Action must be Click, Press, or Release.");
    }

    private static int? ParseNullableInt(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, out var result)
            ? result
            : throw new ArgumentException($"Expected an integer but received '{value}'.");
    }

    private static string CreateSessionLogPath(string configurationDirectory, string command)
    {
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{command.ToLowerInvariant()}.ndjson";
        return Path.Combine(configurationDirectory, "sessions", fileName);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            SamsungController CLI

            Usage:
              samsungctl connect <TV-IP> [options]
              samsungctl key <KEY_NAME> [--action Click|Press|Release] [options]
              samsungctl listen [options]
              samsungctl status [options]
              samsungctl forget [--host <TV-IP>] [options]

            Options:
              --host <TV-IP>             Override the saved TV host
              --name <application-name>  Name shown in the TV pairing prompt
              --insecure                 Use ws:// port 8001 instead of wss:// port 8002
              --secure                   Force secure mode for a previously configured TV
              --port <number>            Override the default Samsung port
              --strict-tls               Require a trusted TLS certificate
              --pairing-timeout <secs>   Pairing prompt timeout (default: 90)
              --log <path>               NDJSON protocol log path
              --token-file <path>        Pairing token store path
              --config-dir <path>        Settings/session directory
              --quiet                    Suppress diagnostic terminal output
              --help                     Show this help

            Examples:
              samsungctl connect 192.168.1.100
              samsungctl key KEY_UP
              samsungctl key KEY_RIGHT --action Press
              samsungctl key KEY_RIGHT --action Release
              samsungctl listen
            """);
    }
}
