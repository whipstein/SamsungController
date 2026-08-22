using System.Text.Json;
using System.Text.Json.Serialization;
using SamsungController.Core.Protocol;

namespace SamsungController.Core.Diagnostics;

public sealed class NdjsonProtocolLogger : ISamsungMessageSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StreamWriter _writer;
    private bool _disposed;

    public NdjsonProtocolLogger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous));
    }

    public async ValueTask WriteAsync(
        SamsungMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        var record = new
        {
            timestamp = message.Timestamp,
            direction = message.Direction.ToString().ToUpperInvariant(),
            channel = message.Channel,
            eventName = message.Event,
            parsedPayload = message.ParsedPayload,
            rawJson = message.RawJson,
            parseError = message.ParseError,
            connectionGeneration = message.ConnectionGeneration
        };

        var line = JsonSerializer.Serialize(record, SerializerOptions);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
