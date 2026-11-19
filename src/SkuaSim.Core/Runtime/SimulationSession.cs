using System.Diagnostics;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Runtime;

public enum SimulationState
{
    Compiling,
    Running,
    Paused,
    Stopping,
    Completed
}

public sealed class SimulationSessionOptions
{
    /// <summary>
    /// Initial virtual clock speed relative to wall-clock time. Values &lt;= 0 run at instant maximum speed.
    /// </summary>
    public double InitialSpeedMultiplier { get; init; }
    public bool StartPaused { get; init; }
}

public sealed class SimulationLiveSnapshot
{
    public SimulationState State { get; init; }
    public SimulationOutcome? Outcome { get; init; }
    public TimeSpan VirtualTime { get; init; }
    public TimeSpan ActiveRealTime { get; init; }
    public TimeSpan PausedTime { get; init; }
    public double SpeedMultiplier { get; init; }
    public bool IsPaused { get; init; }
    public long TotalApiCalls { get; init; }
    public string? LastApiCall { get; init; }
    public int LogCount { get; init; }
    public int WarningCount { get; init; }
    public int SynthesizedCount { get; init; }
    public int UnimplementedCount { get; init; }
    public string? StopReason { get; init; }
    public WorldSnapshot World { get; init; } = new();
}

public sealed record ScriptOptionDescriptor(
    string Category,
    string Name,
    string DisplayName,
    string Description,
    string ValueType,
    object? DefaultValue,
    IReadOnlyList<string> AllowedValues,
    bool IsHeader = false);

public sealed class ScriptInspectionResult
{
    public required string ScriptPath { get; init; }
    public required ScriptCompileResult CompileResult { get; init; }
    public bool Success => CompileResult.Success && ScriptClass != null;
    public string? ScriptClass { get; init; }
    public string? OptionsStorage { get; init; }
    public IReadOnlyList<ScriptOptionDescriptor> Options { get; init; } = Array.Empty<ScriptOptionDescriptor>();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Controls an active or completed simulation run, supporting pause/resume, live speed adjustment,
/// non-blocking snapshots, and incremental log/warning/timeline streaming for UI frontends.
/// </summary>
public sealed class SimulationSession : IDisposable
{
    private readonly object _stateGate = new();
    private readonly TaskCompletionSource<SimulationReport> _completionTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _realSw = Stopwatch.StartNew();

    private SimulationState _state = SimulationState.Compiling;
    private bool _pausedRequested;
    private bool _stopRequested;
    private string? _stopReason;
    private double _speedMultiplier;
    private SimClock? _clock;
    private SimContext? _ctx;
    private SimulationTrace? _trace;
    private SimulationReport? _report;
    private Stopwatch? _executionSw;
    private long _pausedAtExecutionStartMs;
    private TimeSpan _finalActiveRealTime;
    private TimeSpan _finalPausedTime;
    private bool _disposed;

    internal SimulationSession(
        string scriptPath,
        string? scenarioName,
        int seed,
        SimulationSessionOptions sessionOptions,
        Func<SimulationSession, SimulationReport> coordinator)
    {
        ScriptPath = Path.GetFullPath(scriptPath);
        ScenarioName = scenarioName;
        Seed = seed;
        _pausedRequested = sessionOptions.StartPaused;
        _speedMultiplier = NormalizeSpeed(sessionOptions.InitialSpeedMultiplier);

        var thread = new Thread(() =>
        {
            try
            {
                var report = coordinator(this);
                Complete(report);
            }
            catch (Exception ex)
            {
                var fallback = new SimulationReport
                {
                    ScriptPath = ScriptPath,
                    ScenarioName = ScenarioName,
                    Seed = Seed,
                    Outcome = SimulationOutcome.ScriptException,
                    StopReason = "simulator coordinator error",
                    ExceptionText = ex.ToString(),
                    RealDuration = _realSw.Elapsed
                };
                Complete(fallback);
            }
        })
        {
            Name = "SkuaSim Session Coordinator",
            IsBackground = true
        };
        thread.Start();
    }

    public string ScriptPath { get; }
    public string? ScenarioName { get; }
    public int Seed { get; }
    public Task<SimulationReport> Completion => _completionTcs.Task;

    public event Action<SimulationState>? StateChanged;
    public event Action<SimulationReport>? Completed;

    public SimulationState State
    {
        get { lock (_stateGate) return _state; }
    }

    public bool IsPaused
    {
        get { lock (_stateGate) return _clock?.IsPaused ?? _pausedRequested; }
    }

    public SimulationReport? Report
    {
        get { lock (_stateGate) return _report; }
    }

    public double SpeedMultiplier
    {
        get { lock (_stateGate) return _clock?.SpeedMultiplier ?? _speedMultiplier; }
        set
        {
            var normalized = NormalizeSpeed(value);
            lock (_stateGate)
            {
                _speedMultiplier = normalized;
                if (_clock != null)
                    _clock.SpeedMultiplier = normalized;
            }
        }
    }

    internal bool StopRequested
    {
        get { lock (_stateGate) return _stopRequested; }
    }

    internal string? RequestedStopReason
    {
        get { lock (_stateGate) return _stopReason; }
    }

    internal long TotalRealElapsedMs => _realSw.ElapsedMilliseconds;

    public void Pause()
    {
        bool stateChanged = false;
        SimulationState newState;
        lock (_stateGate)
        {
            if (_state is SimulationState.Completed or SimulationState.Stopping)
                return;
            _pausedRequested = true;
            if (_clock != null)
            {
                _clock.Pause();
                if (_state == SimulationState.Running)
                {
                    _state = SimulationState.Paused;
                    stateChanged = true;
                }
            }
            newState = _state;
        }

        if (stateChanged)
            StateChanged?.Invoke(newState);
    }

    public void Resume()
    {
        bool stateChanged = false;
        SimulationState newState;
        lock (_stateGate)
        {
            if (_state is SimulationState.Completed or SimulationState.Stopping)
                return;
            _pausedRequested = false;
            if (_clock != null)
            {
                _clock.Resume();
                if (_state == SimulationState.Paused)
                {
                    _state = SimulationState.Running;
                    stateChanged = true;
                }
            }
            newState = _state;
        }

        if (stateChanged)
            StateChanged?.Invoke(newState);
    }

    public void Stop(string reason = "stopped by user")
    {
        SimContext? ctx;
        SimClock? clock;
        bool stateChanged = false;
        SimulationState newState;
        lock (_stateGate)
        {
            if (_state == SimulationState.Completed)
                return;
            if (!_stopRequested)
            {
                _stopRequested = true;
                _stopReason = string.IsNullOrWhiteSpace(reason) ? "stopped by user" : reason;
            }
            if (_state is SimulationState.Running or SimulationState.Paused)
            {
                _state = SimulationState.Stopping;
                stateChanged = true;
            }
            ctx = _ctx;
            clock = _clock;
            newState = _state;
        }

        ctx?.RequestStop(_stopReason ?? "stopped by user");
        clock?.NotifyStopRequested();
        if (stateChanged)
            StateChanged?.Invoke(newState);
    }

    public SimulationLiveSnapshot GetLiveSnapshot()
    {
        SimulationState state;
        SimClock? clock;
        SimContext? ctx;
        SimulationTrace? trace;
        SimulationReport? report;
        double speed;
        bool pausedReq;
        string? stopReason;
        long totalRealMs;
        TimeSpan finalActiveReal;
        TimeSpan finalPaused;

        lock (_stateGate)
        {
            state = _state;
            clock = _clock;
            ctx = _ctx;
            trace = _trace;
            report = _report;
            speed = _speedMultiplier;
            pausedReq = _pausedRequested;
            stopReason = _stopReason;
            totalRealMs = _realSw.ElapsedMilliseconds;
            finalActiveReal = _finalActiveRealTime;
            finalPaused = _finalPausedTime;
        }

        if (report != null)
        {
            return new SimulationLiveSnapshot
            {
                State = SimulationState.Completed,
                Outcome = report.Outcome,
                VirtualTime = report.VirtualDuration,
                ActiveRealTime = finalActiveReal > TimeSpan.Zero ? finalActiveReal : report.RealDuration,
                PausedTime = finalPaused,
                SpeedMultiplier = clock?.SpeedMultiplier ?? speed,
                IsPaused = false,
                TotalApiCalls = report.TotalApiCalls,
                LastApiCall = trace?.LastApiCall,
                LogCount = report.ScriptLogs.Count,
                WarningCount = report.Warnings.Count,
                SynthesizedCount = report.SynthesizedData.Count,
                UnimplementedCount = report.UnimplementedCalls.Count,
                StopReason = report.StopReason,
                World = report.FinalState
            };
        }

        if (ctx != null && clock != null && trace != null)
        {
            var pausedMs = clock.TotalPausedMs;
            var activeRealMs = Math.Max(0, totalRealMs - pausedMs);
            return new SimulationLiveSnapshot
            {
                State = clock.IsPaused && state == SimulationState.Running ? SimulationState.Paused : state,
                VirtualTime = TimeSpan.FromMilliseconds(clock.NowMs),
                ActiveRealTime = TimeSpan.FromMilliseconds(activeRealMs),
                PausedTime = TimeSpan.FromMilliseconds(pausedMs),
                SpeedMultiplier = clock.SpeedMultiplier,
                IsPaused = clock.IsPaused,
                TotalApiCalls = Interlocked.Read(ref trace.TotalApiCalls),
                LastApiCall = trace.LastApiCall,
                LogCount = trace.ScriptLogCount,
                WarningCount = trace.WarningCount,
                SynthesizedCount = trace.Synthesized.Count,
                UnimplementedCount = trace.Unimplemented.Count,
                StopReason = ctx.StopReason ?? stopReason,
                World = ctx.World.Snapshot()
            };
        }

        return new SimulationLiveSnapshot
        {
            State = state,
            ActiveRealTime = TimeSpan.FromMilliseconds(totalRealMs),
            PausedTime = TimeSpan.Zero,
            SpeedMultiplier = speed,
            IsPaused = pausedReq,
            StopReason = stopReason,
            World = new WorldSnapshot()
        };
    }

    public IReadOnlyList<TraceEntry> ReadScriptLogsSince(int startIndex, out int nextIndex, int maxCount = 500)
    {
        var trace = GetTrace();
        if (trace == null)
        {
            nextIndex = Math.Max(0, startIndex);
            return Array.Empty<TraceEntry>();
        }
        return trace.ReadScriptLogsSince(startIndex, out nextIndex, maxCount);
    }

    public IReadOnlyList<TraceEntry> ReadWarningsSince(int startIndex, out int nextIndex, int maxCount = 500)
    {
        var trace = GetTrace();
        if (trace == null)
        {
            nextIndex = Math.Max(0, startIndex);
            return Array.Empty<TraceEntry>();
        }
        return trace.ReadWarningsSince(startIndex, out nextIndex, maxCount);
    }

    public IReadOnlyList<IndexedTraceEntry> ReadTimelineSince(long afterSeq, out long latestSeq, out bool droppedEntries, int maxCount = 1000)
    {
        var trace = GetTrace();
        if (trace == null)
        {
            latestSeq = Math.Max(0, afterSeq);
            droppedEntries = false;
            return Array.Empty<IndexedTraceEntry>();
        }
        return trace.ReadTimelineSince(afterSeq, out latestSeq, out droppedEntries, maxCount);
    }

    public IReadOnlyList<SynthesizedDataRecord> GetSynthesizedSnapshot() =>
        GetTrace()?.GetSynthesizedSnapshot() ?? Array.Empty<SynthesizedDataRecord>();

    public IReadOnlyDictionary<string, long> GetUnimplementedSnapshot() =>
        GetTrace()?.GetUnimplementedSnapshot() ?? new Dictionary<string, long>();

    internal void AttachRuntime(SimContext ctx, SimClock clock, SimulationTrace trace)
    {
        SimulationState newState;
        bool stopAlreadyRequested;
        string? stopReason;
        bool startPaused;
        double speed;

        lock (_stateGate)
        {
            _ctx = ctx;
            _clock = clock;
            _trace = trace;
            speed = _speedMultiplier;
            startPaused = _pausedRequested;
            stopAlreadyRequested = _stopRequested;
            stopReason = _stopReason;
        }

        clock.SpeedMultiplier = speed;
        if (stopAlreadyRequested)
            ctx.RequestStop(stopReason ?? "stopped by user");
        else if (startPaused)
            clock.Pause();

        lock (_stateGate)
        {
            _state = stopAlreadyRequested
                ? SimulationState.Stopping
                : clock.IsPaused ? SimulationState.Paused : SimulationState.Running;
            newState = _state;
        }

        StateChanged?.Invoke(newState);
    }

    internal void MarkExecutionStarted(SimClock clock)
    {
        lock (_stateGate)
        {
            _pausedAtExecutionStartMs = clock.TotalPausedMs;
            _executionSw = Stopwatch.StartNew();
        }
    }

    internal long ActiveExecutionRealMs(SimClock clock)
    {
        Stopwatch? execSw;
        long pausedBaseline;
        lock (_stateGate)
        {
            execSw = _executionSw;
            pausedBaseline = _pausedAtExecutionStartMs;
        }
        if (execSw == null)
            return 0;

        var pausedDuringExecution = Math.Max(0, clock.TotalPausedMs - pausedBaseline);
        return Math.Max(0, execSw.ElapsedMilliseconds - pausedDuringExecution);
    }

    internal TimeSpan CurrentActiveRealDuration(SimClock? clock)
    {
        var pausedMs = clock?.TotalPausedMs ?? 0;
        var activeMs = Math.Max(0, _realSw.ElapsedMilliseconds - pausedMs);
        return TimeSpan.FromMilliseconds(activeMs);
    }

    private void Complete(SimulationReport report)
    {
        lock (_stateGate)
        {
            _finalPausedTime = _clock?.PausedDuration ?? TimeSpan.Zero;
            _finalActiveRealTime = report.RealDuration;
            _report = report;
            _state = SimulationState.Completed;
        }

        _completionTcs.TrySetResult(report);
        StateChanged?.Invoke(SimulationState.Completed);
        Completed?.Invoke(report);
    }

    private SimulationTrace? GetTrace()
    {
        lock (_stateGate) return _trace;
    }

    private static double NormalizeSpeed(double value) =>
        double.IsNaN(value) || value <= 0 ? 0 : value;

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        if (State != SimulationState.Completed)
        {
            Stop("session disposed");
            try
            {
                _completionTcs.Task.Wait(2500);
            }
            catch { }
        }
    }
}
