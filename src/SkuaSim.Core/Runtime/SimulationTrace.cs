using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkuaSim.Core.Runtime;

public sealed record TraceEntry(
    [property: JsonPropertyName("t")] long VirtualMs,
    [property: JsonPropertyName("time")] string Clock,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("thread")] string Thread,
    [property: JsonPropertyName("text")] string Text);

public sealed class SynthesizedDataRecord
{
    public required string Key { get; init; }
    public required string Description { get; init; }
    public long Count { get; set; }
    public long FirstVirtualMs { get; init; }
}

/// <summary>
/// Records everything that happens during a run: API calls, script logs, world events, warnings,
/// synthesized (invented) data and API members the simulator does not implement yet.
/// The full timeline streams to trace.jsonl; summaries are kept in memory for the report.
/// </summary>
public sealed class SimulationTrace : IDisposable
{
    private const int RecentCapacity = 400;
    private const int ScriptLogCapacity = 50_000;

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly Queue<TraceEntry> _recent = new();
    private readonly Func<long> _clock;
    private readonly Func<long, string> _format;
    private readonly long _maxEntries;
    private long _entries;
    private bool _truncationNoted;

    public SimulationTrace(string? jsonlPath, Func<long> clock, Func<long, string> format, long maxEntries = 500_000)
    {
        _clock = clock;
        _format = format;
        _maxEntries = maxEntries;
        if (!string.IsNullOrEmpty(jsonlPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);
            _writer = new StreamWriter(jsonlPath, append: false) { AutoFlush = false };
        }
    }

    public int MainThreadId { get; set; } = -1;
    public List<TraceEntry> ScriptLog { get; } = new();
    public List<TraceEntry> Warnings { get; } = new();
    public List<TraceEntry> WorldEvents { get; } = new();
    public ConcurrentDictionary<string, SynthesizedDataRecord> Synthesized { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, long> Unimplemented { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, long> CallCounts { get; } = new(StringComparer.Ordinal);
    public long TotalApiCalls;
    public long TotalEntries => Interlocked.Read(ref _entries);

    public IReadOnlyList<TraceEntry> RecentTimeline
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    public void Api(string member, string arguments, string? result)
    {
        var text = result == null ? $"{member}({arguments})" : $"{member}({arguments}) => {result}";
        Write("api", text);
    }

    public void Log(string message)
    {
        var entry = Write("log", message);
        lock (_gate)
        {
            if (ScriptLog.Count < ScriptLogCapacity)
                ScriptLog.Add(entry);
        }
    }

    public void World(string message)
    {
        var entry = Write("world", message);
        lock (_gate)
        {
            if (WorldEvents.Count < ScriptLogCapacity)
                WorldEvents.Add(entry);
        }
    }

    public void Info(string message) => Write("info", message);

    public void Warn(string message)
    {
        var entry = Write("warn", message);
        lock (_gate)
        {
            if (Warnings.Count < 5_000)
                Warnings.Add(entry);
        }
    }

    public void Error(string message) => Warn("ERROR: " + message);

    /// <summary>
    /// Records that the simulator invented a value because no captured data exists. Logged once per key.
    /// </summary>
    public void Synthesize(string key, string description)
    {
        var isNew = false;
        var record = Synthesized.GetOrAdd(key, k =>
        {
            isNew = true;
            return new SynthesizedDataRecord { Key = k, Description = description, FirstVirtualMs = _clock() };
        });
        lock (_gate)
            record.Count++;
        if (isNew)
            Write("synth", description);
    }

    public void NoteUnimplemented(string member) => Unimplemented.AddOrUpdate(member, 1, (_, c) => c + 1);

    public long CountCall(string member)
    {
        Interlocked.Increment(ref TotalApiCalls);
        return CallCounts.AddOrUpdate(member, 1, (_, c) => c + 1);
    }

    private TraceEntry Write(string kind, string text)
    {
        var now = _clock();
        var thread = Environment.CurrentManagedThreadId == MainThreadId ? "main" : $"t{Environment.CurrentManagedThreadId}";
        var entry = new TraceEntry(now, _format(now), kind, thread, text);
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RecentCapacity)
                _recent.Dequeue();

            _entries++;
            if (_writer != null)
            {
                if (_entries <= _maxEntries)
                {
                    _writer.WriteLine(JsonSerializer.Serialize(entry));
                }
                else if (!_truncationNoted)
                {
                    _truncationNoted = true;
                    _writer.WriteLine(JsonSerializer.Serialize(entry with { Kind = "info", Text = $"Trace file truncated after {_maxEntries} entries." }));
                }
            }
        }
        return entry;
    }

    public void Flush()
    {
        lock (_gate)
            _writer?.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
    }
}
