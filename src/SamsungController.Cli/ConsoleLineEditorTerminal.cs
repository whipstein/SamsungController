namespace SamsungController.Cli;

internal interface IConsoleKeyReader
{
    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken = default);
}

internal sealed class SystemConsoleKeyReader : IConsoleKeyReader
{
    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                return Console.ReadKey(intercept: true);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }
}

internal sealed class ConsoleLineEditorTerminal : IInteractiveConsoleTerminal
{
    private readonly object _outputSync = new();
    private readonly TextWriter _output;
    private readonly IConsoleKeyReader _keyReader;
    private readonly ConsoleCommandHistory _history;
    private readonly List<char> _buffer = [];

    private string _prompt = string.Empty;
    private int _cursor;
    private int _renderedWidth;
    private bool _editing;

    public ConsoleLineEditorTerminal(
        TextWriter output,
        ConsoleCommandHistory history,
        IConsoleKeyReader? keyReader = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _keyReader = keyReader ?? new SystemConsoleKeyReader();
    }

    public IReadOnlyList<string> History => _history.Snapshot();

    public async ValueTask<string?> ReadLineAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var historyIndex = _history.Snapshot().Count;
        var draft = string.Empty;

        lock (_outputSync)
        {
            _prompt = prompt;
            _buffer.Clear();
            _cursor = 0;
            _renderedWidth = 0;
            _editing = true;
            RenderUnderLock();
        }

        try
        {
            while (true)
            {
                var key = await _keyReader.ReadKeyAsync(cancellationToken).ConfigureAwait(false);
                string? completedLine = null;

                lock (_outputSync)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        completedLine = new string([.. _buffer]);
                        _output.WriteLine();
                        _output.Flush();
                        _editing = false;
                        _renderedWidth = 0;
                    }
                    else
                    {
                        var history = _history.Snapshot();
                        HandleKeyUnderLock(key, history, ref historyIndex, ref draft);
                        RenderUnderLock();
                    }
                }

                if (completedLine is not null)
                {
                    _history.Add(completedLine);
                    return completedLine;
                }
            }
        }
        finally
        {
            lock (_outputSync)
            {
                if (_editing)
                {
                    ClearRenderedLineUnderLock();
                    _output.WriteLine();
                    _output.Flush();
                    _editing = false;
                    _renderedWidth = 0;
                }
            }
        }
    }

    public void WriteLine(string value)
    {
        lock (_outputSync)
        {
            if (!_editing)
            {
                _output.WriteLine(value);
                _output.Flush();
                return;
            }

            ClearRenderedLineUnderLock();
            _output.WriteLine(value);
            _renderedWidth = 0;
            RenderUnderLock();
        }
    }

    public void ClearHistory() => _history.Clear();

    private void HandleKeyUnderLock(
        ConsoleKeyInfo key,
        IReadOnlyList<string> history,
        ref int historyIndex,
        ref string draft)
    {
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            switch (key.Key)
            {
                case ConsoleKey.A:
                    _cursor = 0;
                    return;
                case ConsoleKey.E:
                    _cursor = _buffer.Count;
                    return;
                case ConsoleKey.U:
                    _buffer.Clear();
                    _cursor = 0;
                    historyIndex = history.Count;
                    draft = string.Empty;
                    return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                _cursor = Math.Max(0, _cursor - 1);
                return;
            case ConsoleKey.RightArrow:
                _cursor = Math.Min(_buffer.Count, _cursor + 1);
                return;
            case ConsoleKey.Home:
                _cursor = 0;
                return;
            case ConsoleKey.End:
                _cursor = _buffer.Count;
                return;
            case ConsoleKey.Backspace when _cursor > 0:
                _buffer.RemoveAt(--_cursor);
                return;
            case ConsoleKey.Delete when _cursor < _buffer.Count:
                _buffer.RemoveAt(_cursor);
                return;
            case ConsoleKey.Escape:
                _buffer.Clear();
                _cursor = 0;
                historyIndex = history.Count;
                draft = string.Empty;
                return;
            case ConsoleKey.UpArrow:
                if (history.Count == 0 || historyIndex == 0)
                {
                    return;
                }

                if (historyIndex == history.Count)
                {
                    draft = new string([.. _buffer]);
                }

                ReplaceBufferUnderLock(history[--historyIndex]);
                return;
            case ConsoleKey.DownArrow:
                if (historyIndex >= history.Count)
                {
                    return;
                }

                historyIndex++;
                ReplaceBufferUnderLock(
                    historyIndex == history.Count ? draft : history[historyIndex]);
                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _buffer.Insert(_cursor++, key.KeyChar);
            historyIndex = history.Count;
        }
    }

    private void ReplaceBufferUnderLock(string value)
    {
        _buffer.Clear();
        _buffer.AddRange(value);
        _cursor = _buffer.Count;
    }

    private void RenderUnderLock()
    {
        ClearRenderedLineUnderLock();

        var value = new string([.. _buffer]);
        _output.Write(_prompt);
        _output.Write(value);
        _renderedWidth = _prompt.Length + value.Length;

        _output.Write('\r');
        _output.Write(_prompt);
        if (_cursor > 0)
        {
            _output.Write(value.AsSpan(0, _cursor));
        }

        _output.Flush();
    }

    private void ClearRenderedLineUnderLock()
    {
        if (_renderedWidth == 0)
        {
            _output.Write('\r');
            return;
        }

        _output.Write('\r');
        _output.Write(new string(' ', _renderedWidth));
        _output.Write('\r');
    }
}
