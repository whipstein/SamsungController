namespace SamsungController.Cli;

internal interface IInteractiveConsoleTerminal
{
    IReadOnlyList<string> History { get; }

    ValueTask<string?> ReadLineAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    void WriteLine(string value);

    void ClearHistory();
}

internal sealed class TextInteractiveConsoleTerminal(
    TextReader input,
    TextWriter output,
    ConsoleCommandHistory history) : IInteractiveConsoleTerminal
{
    private readonly TextReader _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly ConsoleCommandHistory _history =
        history ?? throw new ArgumentNullException(nameof(history));

    public IReadOnlyList<string> History => _history.Snapshot();

    public async ValueTask<string?> ReadLineAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        _output.Write(prompt);
        _output.Flush();
        var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null)
        {
            _history.Add(line);
        }

        return line;
    }

    public void WriteLine(string value) => _output.WriteLine(value);

    public void ClearHistory() => _history.Clear();
}
