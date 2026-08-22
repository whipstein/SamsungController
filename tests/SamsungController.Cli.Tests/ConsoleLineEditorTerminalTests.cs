using SamsungController.Cli;

namespace SamsungController.Cli.Tests;

public sealed class ConsoleLineEditorTerminalTests
{
    [Fact]
    public async Task UpArrowRecallsMostRecentCommand()
    {
        var keys = new FakeConsoleKeyReader(
            Special(ConsoleKey.UpArrow),
            Special(ConsoleKey.Enter));
        var history = new ConsoleCommandHistory(
            path: null,
            initialEntries: ["key KEY_UP", "query apps"]);
        var terminal = new ConsoleLineEditorTerminal(new StringWriter(), history, keys);

        var line = await terminal.ReadLineAsync("samsungctl> ");

        Assert.Equal("query apps", line);
    }

    [Fact]
    public async Task DownArrowRestoresDraftTypedBeforeHistoryNavigation()
    {
        var keys = new FakeConsoleKeyReader(
            Character('x'),
            Special(ConsoleKey.UpArrow),
            Special(ConsoleKey.DownArrow),
            Special(ConsoleKey.Enter));
        var history = new ConsoleCommandHistory(
            path: null,
            initialEntries: ["key KEY_UP"]);
        var terminal = new ConsoleLineEditorTerminal(new StringWriter(), history, keys);

        var line = await terminal.ReadLineAsync("samsungctl> ");

        Assert.Equal("x", line);
    }

    [Fact]
    public async Task LeftArrowSupportsInsertionWithinCommand()
    {
        var keys = new FakeConsoleKeyReader(
            Character('a'),
            Character('c'),
            Special(ConsoleKey.LeftArrow),
            Character('b'),
            Special(ConsoleKey.Enter));
        var terminal = new ConsoleLineEditorTerminal(
            new StringWriter(),
            new ConsoleCommandHistory(path: null),
            keys);

        var line = await terminal.ReadLineAsync("samsungctl> ");

        Assert.Equal("abc", line);
    }

    private static ConsoleKeyInfo Character(char value) =>
        new(value, ConsoleKey.A, shift: false, alt: false, control: false);

    private static ConsoleKeyInfo Special(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);

    private sealed class FakeConsoleKeyReader(params ConsoleKeyInfo[] keys) : IConsoleKeyReader
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_keys.Dequeue());
    }
}
