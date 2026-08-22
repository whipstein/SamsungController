using System.Text.Json;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli;

internal sealed class InteractiveConsoleSession
{
    private readonly IInteractiveSamsungClient _client;
    private readonly IInteractiveConsoleTerminal _terminal;
    private readonly bool _allowRaw;

    public InteractiveConsoleSession(
        IInteractiveSamsungClient client,
        IInteractiveConsoleTerminal terminal,
        bool allowRaw)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _allowRaw = allowRaw;
    }

    public InteractiveConsoleSession(
        IInteractiveSamsungClient client,
        TextReader input,
        TextWriter output,
        bool allowRaw)
        : this(
            client,
            new TextInteractiveConsoleTerminal(
                input,
                output,
                new ConsoleCommandHistory(path: null)),
            allowRaw)
    {
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _terminal.WriteLine("Interactive Samsung console. Type 'help' for commands; 'exit' to quit.");
        if (_allowRaw)
        {
            _terminal.WriteLine(
                "Developer mode: raw JSON sending is enabled. Unknown writes can alter TV behavior.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _terminal.ReadLineAsync("samsungctl> ", cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOfAny([' ', '\t']);
            var verb = separator < 0 ? trimmed : trimmed[..separator];
            var arguments = separator < 0 ? string.Empty : trimmed[(separator + 1)..].TrimStart();

            try
            {
                if (await ExecuteAsync(verb, arguments, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _terminal.WriteLine($"Command failed: {exception.Message}");
            }
        }
    }

    private async Task<bool> ExecuteAsync(
        string verb,
        string arguments,
        CancellationToken cancellationToken)
    {
        switch (verb.ToLowerInvariant())
        {
            case "exit":
            case "quit":
                return true;

            case "help":
                PrintHelp();
                return false;

            case "state":
            case "status":
                _terminal.WriteLine(
                    $"State: {_client.State}; connection generation: {_client.ConnectionGeneration}");
                return false;

            case "history":
                ShowOrClearHistory(arguments);
                return false;

            case "key":
                await SendKeyAsync(arguments, cancellationToken).ConfigureAwait(false);
                return false;

            case "query":
                await SendQueryAsync(arguments, cancellationToken).ConfigureAwait(false);
                return false;

            case "raw":
                await SendRawAsync(arguments, cancellationToken).ConfigureAwait(false);
                return false;

            default:
                _terminal.WriteLine($"Unknown command '{verb}'. Type 'help' for available commands.");
                return false;
        }
    }

    private async Task SendKeyAsync(string arguments, CancellationToken cancellationToken)
    {
        var parts = arguments.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
        {
            _terminal.WriteLine("Usage: key <KEY_NAME> [Click|Press|Release]");
            return;
        }

        var action = RemoteKeyAction.Click;
        if (parts.Length == 2
            && !Enum.TryParse(parts[1], ignoreCase: true, out action))
        {
            _terminal.WriteLine("Action must be Click, Press, or Release.");
            return;
        }

        await _client.SendKeyAsync(parts[0], action, cancellationToken).ConfigureAwait(false);
        _terminal.WriteLine($"Sent {action} {parts[0]}.");
    }

    private async Task SendQueryAsync(string arguments, CancellationToken cancellationToken)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "apps":
                await _client.SendQueryAsync(SamsungQuery.EdenApplications, cancellationToken)
                    .ConfigureAwait(false);
                await _client.SendQueryAsync(SamsungQuery.InstalledApplications, cancellationToken)
                    .ConfigureAwait(false);
                _terminal.WriteLine(
                    "Sent Eden and installed-application queries; responses will appear asynchronously.");
                return;

            case "eden-apps":
                await _client.SendQueryAsync(SamsungQuery.EdenApplications, cancellationToken)
                    .ConfigureAwait(false);
                _terminal.WriteLine("Sent ed.edenApp.get; any response will appear asynchronously.");
                return;

            case "installed-apps":
                await _client.SendQueryAsync(SamsungQuery.InstalledApplications, cancellationToken)
                    .ConfigureAwait(false);
                _terminal.WriteLine("Sent ed.installedApp.get; any response will appear asynchronously.");
                return;

            default:
                _terminal.WriteLine("Usage: query apps|eden-apps|installed-apps");
                return;
        }
    }

    private async Task SendRawAsync(string rawJson, CancellationToken cancellationToken)
    {
        if (!_allowRaw)
        {
            _terminal.WriteLine(
                "Raw sending is disabled. Restart the console with --allow-raw to enable developer mode.");
            return;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _terminal.WriteLine("Usage: raw <JSON object>");
            return;
        }

        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            _terminal.WriteLine("Raw payload must be a JSON object.");
            return;
        }

        await _client.SendRawAsync(rawJson, cancellationToken).ConfigureAwait(false);
        _terminal.WriteLine("Sent raw JSON.");
    }

    private void ShowOrClearHistory(string arguments)
    {
        if (arguments.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _terminal.ClearHistory();
            _terminal.WriteLine("Command history cleared.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            _terminal.WriteLine("Usage: history [clear]");
            return;
        }

        var history = _terminal.History;
        if (history.Count == 0)
        {
            _terminal.WriteLine("Command history is empty.");
            return;
        }

        for (var index = 0; index < history.Count; index++)
        {
            _terminal.WriteLine($"{index + 1,4}  {history[index]}");
        }
    }

    private void PrintHelp()
    {
        _terminal.WriteLine(
            """
            Commands:
              key <KEY_NAME> [Click|Press|Release]
                  Send a Samsung remote key on this connection.
              query apps
                  Request both Eden and installed application lists.
              query eden-apps
                  Send the read-only ed.edenApp.get query.
              query installed-apps
                  Send the read-only ed.installedApp.get query.
              raw <JSON object>
                  Send an arbitrary payload (requires --allow-raw).
              state
                  Show connection state and generation.
              history
                  Show command history. Raw JSON commands are never retained.
              history clear
                  Clear in-memory and persisted command history.
              help
                  Show this command list.
              exit | quit
                  Close the console.
            """);
    }
}
