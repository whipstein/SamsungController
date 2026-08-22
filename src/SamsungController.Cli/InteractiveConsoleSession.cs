using System.Text.Json;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli;

internal sealed class InteractiveConsoleSession(
    IInteractiveSamsungClient client,
    TextReader input,
    TextWriter output,
    bool allowRaw)
{
    private readonly IInteractiveSamsungClient _client =
        client ?? throw new ArgumentNullException(nameof(client));
    private readonly TextReader _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _output.WriteLine("Interactive Samsung console. Type 'help' for commands; 'exit' to quit.");
        if (allowRaw)
        {
            _output.WriteLine(
                "Developer mode: raw JSON sending is enabled. Unknown writes can alter TV behavior.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            _output.Write("samsungctl> ");
            _output.Flush();

            var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                _output.WriteLine($"Command failed: {exception.Message}");
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
                _output.WriteLine(
                    $"State: {_client.State}; connection generation: {_client.ConnectionGeneration}");
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
                _output.WriteLine($"Unknown command '{verb}'. Type 'help' for available commands.");
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
            _output.WriteLine("Usage: key <KEY_NAME> [Click|Press|Release]");
            return;
        }

        var action = RemoteKeyAction.Click;
        if (parts.Length == 2
            && !Enum.TryParse(parts[1], ignoreCase: true, out action))
        {
            _output.WriteLine("Action must be Click, Press, or Release.");
            return;
        }

        await _client.SendKeyAsync(parts[0], action, cancellationToken).ConfigureAwait(false);
        _output.WriteLine($"Sent {action} {parts[0]}.");
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
                _output.WriteLine(
                    "Sent Eden and installed-application queries; responses will appear asynchronously.");
                return;

            case "eden-apps":
                await _client.SendQueryAsync(SamsungQuery.EdenApplications, cancellationToken)
                    .ConfigureAwait(false);
                _output.WriteLine("Sent ed.edenApp.get; any response will appear asynchronously.");
                return;

            case "installed-apps":
                await _client.SendQueryAsync(SamsungQuery.InstalledApplications, cancellationToken)
                    .ConfigureAwait(false);
                _output.WriteLine("Sent ed.installedApp.get; any response will appear asynchronously.");
                return;

            default:
                _output.WriteLine("Usage: query apps|eden-apps|installed-apps");
                return;
        }
    }

    private async Task SendRawAsync(string rawJson, CancellationToken cancellationToken)
    {
        if (!allowRaw)
        {
            _output.WriteLine(
                "Raw sending is disabled. Restart the console with --allow-raw to enable developer mode.");
            return;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _output.WriteLine("Usage: raw <JSON object>");
            return;
        }

        using var document = JsonDocument.Parse(rawJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            _output.WriteLine("Raw payload must be a JSON object.");
            return;
        }

        await _client.SendRawAsync(rawJson, cancellationToken).ConfigureAwait(false);
        _output.WriteLine("Sent raw JSON.");
    }

    private void PrintHelp()
    {
        _output.WriteLine(
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
              help
                  Show this command list.
              exit | quit
                  Close the console.
            """);
    }
}
