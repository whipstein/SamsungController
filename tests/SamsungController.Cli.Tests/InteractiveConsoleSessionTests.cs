using SamsungController.Cli;
using SamsungController.Core.Connection;
using SamsungController.Core.Protocol;

namespace SamsungController.Cli.Tests;

public sealed class InteractiveConsoleSessionTests
{
    [Fact]
    public async Task KeyCommandSendsRequestedActionOnExistingClient()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        var session = CreateSession(
            client,
            "key KEY_RIGHT Press\nkey KEY_RIGHT Release\nexit\n",
            output);

        await session.RunAsync();

        Assert.Equal(
            [
                ("KEY_RIGHT", RemoteKeyAction.Press),
                ("KEY_RIGHT", RemoteKeyAction.Release)
            ],
            client.Keys);
        Assert.Contains("Sent Press KEY_RIGHT.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppsQuerySendsBothKnownReadOnlyQueries()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        var session = CreateSession(client, "query apps\nexit\n", output);

        await session.RunAsync();

        Assert.Equal(
            [SamsungQuery.EdenApplications, SamsungQuery.InstalledApplications],
            client.Queries);
        Assert.Contains("responses will appear asynchronously", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawCommandRequiresExplicitDeveloperMode()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        var session = CreateSession(
            client,
            "raw {\"method\":\"example\"}\nexit\n",
            output,
            allowRaw: false);

        await session.RunAsync();

        Assert.Empty(client.RawMessages);
        Assert.Contains("--allow-raw", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeveloperModeSendsValidJsonObjectUnchanged()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        const string payload = "{\"method\":\"ms.channel.emit\",\"params\":{}}";
        var session = CreateSession(
            client,
            $"raw {payload}\nexit\n",
            output,
            allowRaw: true);

        await session.RunAsync();

        Assert.Equal(payload, Assert.Single(client.RawMessages));
        Assert.Contains("Sent raw JSON.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRawJsonIsRejectedWithoutEndingSession()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        var session = CreateSession(
            client,
            "raw not-json\nstate\nexit\n",
            output,
            allowRaw: true);

        await session.RunAsync();

        Assert.Empty(client.RawMessages);
        Assert.Contains("Command failed:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("State: Connected", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacroCommandUsesConfiguredMacroService()
    {
        var client = new FakeInteractiveSamsungClient();
        var output = new StringWriter();
        var macros = new FakeMacroCommandService();
        var terminal = new TextInteractiveConsoleTerminal(
            new StringReader("macro run TestNavigation\nexit\n"),
            output,
            new ConsoleCommandHistory(path: null));
        var session = new InteractiveConsoleSession(
            client,
            terminal,
            allowRaw: false,
            macros);

        await session.RunAsync();

        Assert.Equal("run TestNavigation", Assert.Single(macros.Arguments));
    }

    private static InteractiveConsoleSession CreateSession(
        FakeInteractiveSamsungClient client,
        string input,
        StringWriter output,
        bool allowRaw = false) =>
        new(client, new StringReader(input), output, allowRaw);

    private sealed class FakeInteractiveSamsungClient : IInteractiveSamsungClient
    {
        public SamsungConnectionState State => SamsungConnectionState.Connected;

        public long ConnectionGeneration => 7;

        public List<(string Key, RemoteKeyAction Action)> Keys { get; } = [];

        public List<SamsungQuery> Queries { get; } = [];

        public List<string> RawMessages { get; } = [];

        public Task SendKeyAsync(
            string key,
            RemoteKeyAction action,
            CancellationToken cancellationToken)
        {
            Keys.Add((key, action));
            return Task.CompletedTask;
        }

        public Task SendQueryAsync(SamsungQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.CompletedTask;
        }

        public Task SendRawAsync(string rawJson, CancellationToken cancellationToken)
        {
            RawMessages.Add(rawJson);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMacroCommandService : IMacroCommandService
    {
        public List<string> Arguments { get; } = [];

        public Task ExecuteAsync(
            string arguments,
            CancellationToken cancellationToken = default)
        {
            Arguments.Add(arguments);
            return Task.CompletedTask;
        }
    }
}
