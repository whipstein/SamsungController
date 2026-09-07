using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace SamsungController.Web.Services;

public sealed partial class SamsungIpRemoteService
{
    // Local history only: these APIs do not acquire the TV operation gate,
    // send queries, select modes, or read credentials.
    public async Task<IpCommunicationPage> ReadCommunicationLogAsync(IpCommunicationFilter filter, int page = 0, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (page < 0 || pageSize is < 1 or > 200) throw new ArgumentException("Invalid log page.");
        if (!File.Exists(DiagnosticLogPath)) return new([], 0);
        var limit = new FileInfo(DiagnosticLogPath).Length;
        long total = 0;
        var skipped = 0;
        await foreach (var entry in ReadLogEntriesAsync(limit, () => skipped++, cancellationToken))
            if (filter.Matches(entry.Observation)) total++;
        var first = Math.Max(0, total - ((long)page + 1) * pageSize);
        var end = Math.Max(0, total - (long)page * pageSize);
        var rows = new List<IpCommunicationEntry>();
        long match = 0;
        // Two streaming passes keep memory bounded even deep in large logs.
        await foreach (var entry in ReadLogEntriesAsync(limit, () => { }, cancellationToken))
        {
            if (!filter.Matches(entry.Observation)) continue;
            if (match >= first && match < end) rows.Add(entry);
            if (++match >= end) break;
        }
        rows.Reverse();
        return new(rows, total, skipped);
    }

    public async Task<string> ExportCommunicationLogAsync(IpCommunicationFilter filter, bool savedHistory, bool redactIdentifiers = true,
        bool redactSha = true, CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        void Append(IpRemoteObservation observation)
        {
            if (!filter.Matches(observation)) return;
            var safe = IpCommunicationLog.Format(observation, redactIdentifiers, redactSha);
            var compact = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(safe));
            if (output.Length + compact.Length > 20_000_000)
                throw new InvalidOperationException("This export exceeds 20 MB of text. Narrow the filter, or use the private diagnostics.ndjson file for the full archive. No partial download was produced.");
            output.AppendLine(compact);
        }
        if (savedHistory && File.Exists(DiagnosticLogPath))
        {
            var skipped = 0;
            await foreach (var entry in ReadLogEntriesAsync(new FileInfo(DiagnosticLogPath).Length, () => skipped++, cancellationToken)) Append(entry.Observation);
            if (skipped > 0) throw new InvalidOperationException($"{skipped} incomplete or invalid log records were found. Refresh after the active request finishes; the original file is preserved. No incomplete export was downloaded.");
        }
        else if (!savedHistory)
            foreach (var observation in GetSnapshot().Observations) { cancellationToken.ThrowIfCancellationRequested(); Append(observation); }
        return output.ToString();
    }

    private async IAsyncEnumerable<IpCommunicationEntry> ReadLogEntriesAsync(long byteLimit, Action skipped,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(DiagnosticLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var line = new MemoryStream();
        var buffer = new byte[65536];
        long remaining = byteLimit, number = 0;
        var oversized = false;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            remaining -= read;
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                Append(buffer.AsSpan(start, index - start));
                number++;
                IpRemoteObservation? observation = null;
                if (!oversized)
                {
                    try
                    {
                        // JSON permits the trailing CR from Windows newlines.
                        observation = JsonSerializer.Deserialize<IpRemoteObservation>(line.GetBuffer().AsSpan(0, (int)line.Length));
                        if (observation?.Exchange is null || observation.UserEnteredContext?.Connection is null || observation.Label is null
                            || observation.Exchange.Method is null || observation.Exchange.RequestJson is null || observation.Exchange.Message is null) observation = null;
                    }
                    catch (JsonException) { }
                }
                line.SetLength(0); oversized = false; start = index + 1;
                if (observation is null) skipped();
                else yield return new(number, observation);
            }
            Append(buffer.AsSpan(start, read - start));
        }
        if (line.Length > 0 || oversized) skipped(); // Partial append: never accept it as a completed exchange.

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (line.Length + bytes.Length > 16_000_000) oversized = true;
            if (!oversized) line.Write(bytes);
        }
    }
}
