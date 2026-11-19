using System.Diagnostics;

namespace SkuaSim.Core.Runtime;

/// <summary>
/// Thrown inside the script thread when the simulation is stopping (script Stop(), limits reached, stall).
/// Derives from OperationCanceledException so well-behaved scripts treat it as a cancellation.
/// </summary>
public sealed class SimulationStopException : OperationCanceledException
{
    public SimulationStopException(string reason) : base($"SkuaSim stopped the script: {reason}") => Reason = reason;
    public string Reason { get; }
}

/// <summary>
/// Discrete-event scheduler for world events (monster respawns, combat ticks, auto pickup, handlers...).
/// </summary>
public sealed class SimScheduler
{
    private readonly object _gate = new();
    private readonly PriorityQueue<ScheduledEvent, (long Due, long Seq)> _queue = new();
    private long _seq;

    public sealed class ScheduledEvent
    {
        public required long Due { get; init; }
        public required string Name { get; init; }
        public required Action Action { get; init; }
        public bool Cancelled { get; private set; }
        public void Cancel() => Cancelled = true;
    }

    public ScheduledEvent Schedule(long dueMs, string name, Action action)
    {
        var ev = new ScheduledEvent { Due = dueMs, Name = name, Action = action };
        lock (_gate)
            _queue.Enqueue(ev, (dueMs, _seq++));
        return ev;
    }

    /// <summary>
    /// Runs <paramref name="tick"/> every <paramref name="intervalMs"/> while it returns true.
    /// </summary>
    public void ScheduleRecurring(long firstDueMs, long intervalMs, string name, Func<bool> tick)
    {
        void Run()
        {
            if (tick())
                Schedule(firstDueMs = Math.Max(firstDueMs + intervalMs, firstDueMs), name, Run);
        }
        Schedule(firstDueMs, name, Run);
    }

    public bool TryDequeueDue(long untilMs, out ScheduledEvent? ev)
    {
        lock (_gate)
        {
            while (_queue.TryPeek(out var next, out var priority) && priority.Due <= untilMs)
            {
                _queue.Dequeue();
                if (next.Cancelled)
                    continue;
                ev = next;
                return true;
            }
        }
        ev = null;
        return false;
    }

    public int Count
    {
        get { lock (_gate) return _queue.Count; }
    }
}

/// <summary>
/// Virtual clock. The script's main thread drives time: Sleep advances the clock instantly and processes every
/// world event due in between, so hours of farming run in seconds and the same seed gives the same result.
/// Background threads created by scripts wait until the main thread's clock reaches their wake-up time.
/// </summary>
public sealed class SimClock : SkuaSim.Runtime.ISimTimeSource
{
    private const int PacingSliceMaxMs = 40;
    private const double MinSleepThresholdMs = 12.0;

    private readonly object _gate = new();
    private readonly object _pauseGate = new();
    private long _nowMs;
    private int _spinCounter;
    private bool _spinNoted;
    private volatile bool _paused;
    private bool _stopSignaled;
    private long _pausedStartTicks;
    private long _accumulatedPauseTicks;
    private double _speedMultiplier;
    private double _realDebtMs;

    public SimClock(DateTime epoch) => Epoch = epoch;

    public DateTime Epoch { get; }
    public SimScheduler Scheduler { get; } = new();
    public int MainThreadId { get; set; } = -1;
    public long MaxVirtualMs { get; set; }
    public DateTime LastAdvanceRealUtc { get; private set; } = DateTime.UtcNow;
    internal SimContext? Context { get; set; }

    public long NowMs => Interlocked.Read(ref _nowMs);
    public DateTime Now => Epoch.AddMilliseconds(NowMs);
    public long ElapsedMilliseconds => NowMs;
    public bool IsMainThread => Environment.CurrentManagedThreadId == MainThreadId;
    public bool IsPaused => _paused;

    /// <summary>
    /// Virtual clock pacing multiplier relative to wall-clock time.
    /// Values &lt;= 0 (default) or infinite run at maximum instant speed with zero real delay.
    /// </summary>
    public double SpeedMultiplier
    {
        get { lock (_pauseGate) return _speedMultiplier; }
        set
        {
            var normalized = double.IsNaN(value) || value <= 0 ? 0 : value;
            lock (_pauseGate)
            {
                _speedMultiplier = normalized;
                if (normalized <= 0 || double.IsInfinity(normalized))
                    _realDebtMs = 0;
                Monitor.PulseAll(_pauseGate);
            }
        }
    }

    public long TotalPausedMs
    {
        get
        {
            lock (_pauseGate)
            {
                var ticks = _accumulatedPauseTicks;
                if (_paused && _pausedStartTicks > 0)
                    ticks += Math.Max(0, Stopwatch.GetTimestamp() - _pausedStartTicks);
                return ticks * 1000 / Stopwatch.Frequency;
            }
        }
    }

    public TimeSpan PausedDuration => TimeSpan.FromMilliseconds(TotalPausedMs);

    public string Format(long? ms = null)
    {
        var t = TimeSpan.FromMilliseconds(ms ?? NowMs);
        return t.Days > 0
            ? $"{t.Days}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}"
            : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    public void Pause()
    {
        lock (_pauseGate)
        {
            if (_paused || _stopSignaled)
                return;
            _paused = true;
            _pausedStartTicks = Stopwatch.GetTimestamp();
            Monitor.PulseAll(_pauseGate);
        }
    }

    public void Resume()
    {
        lock (_pauseGate)
        {
            if (!_paused)
                return;
            if (_pausedStartTicks > 0)
                _accumulatedPauseTicks += Math.Max(0, Stopwatch.GetTimestamp() - _pausedStartTicks);
            _pausedStartTicks = 0;
            _paused = false;
            LastAdvanceRealUtc = DateTime.UtcNow;
            Monitor.PulseAll(_pauseGate);
        }
        Context?.NoteApiActivity();
    }

    internal void NotifyStopRequested()
    {
        lock (_pauseGate)
        {
            _stopSignaled = true;
            if (_paused)
            {
                if (_pausedStartTicks > 0)
                    _accumulatedPauseTicks += Math.Max(0, Stopwatch.GetTimestamp() - _pausedStartTicks);
                _pausedStartTicks = 0;
                _paused = false;
            }
            Monitor.PulseAll(_pauseGate);
        }
        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    public void WaitIfPaused()
    {
        if (!_paused)
            return;

        var waited = false;
        lock (_pauseGate)
        {
            while (_paused && !_stopSignaled && Context?.StopRequested != true)
            {
                waited = true;
                Monitor.Wait(_pauseGate);
            }
        }

        if (waited)
        {
            LastAdvanceRealUtc = DateTime.UtcNow;
            Context?.NoteApiActivity();
        }
        Context?.ThrowIfStopping();
    }

    public void Sleep(int milliseconds)
    {
        WaitIfPaused();
        Context?.ThrowIfStopping();
        if (IsMainThread || MainThreadId < 0)
            Advance(milliseconds);
        else
            WaitBackground(milliseconds);
        WaitIfPaused();
        Context?.ThrowIfStopping();
    }

    public Task Delay(int milliseconds, CancellationToken cancellationToken)
    {
        if (IsMainThread)
        {
            Sleep(milliseconds);
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            WaitBackground(milliseconds);
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    /// <summary>
    /// Advances virtual time, running every scheduled world event that becomes due.
    /// </summary>
    public void Advance(long milliseconds)
    {
        WaitIfPaused();
        var prevNow = NowMs;
        var target = prevNow + Math.Max(0, milliseconds);
        while (Scheduler.TryDequeueDue(target, out var ev))
        {
            var eventTime = Math.Max(prevNow, ev!.Due);
            if (eventTime > prevNow)
            {
                PauseAwareRealDelay(eventTime - prevNow);
                prevNow = eventTime;
            }

            WaitIfPaused();
            Context?.ThrowIfStopping();
            SetNow(Math.Max(NowMs, eventTime));
            try
            {
                ev.Action();
            }
            catch (SimulationStopException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Context?.Trace.Warn($"World event '{ev.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (target > prevNow)
            PauseAwareRealDelay(target - prevNow);

        WaitIfPaused();
        SetNow(Math.Max(NowMs, target));
        _spinCounter = 0;
        LastAdvanceRealUtc = DateTime.UtcNow;
        Context?.NoteApiActivity();

        if (MaxVirtualMs > 0 && NowMs >= MaxVirtualMs)
            Context?.RequestStop($"virtual time limit reached ({Format(MaxVirtualMs)})");

        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    /// <summary>
    /// Called on every API call. A script polling the API in a tight loop without sleeping (for example
    /// CoreStory._MonsterHuntBatch waiting on a monster respawn) would otherwise stall virtual time,
    /// so after 500 consecutive calls without time passing the clock nudges forward by 250 ms.
    /// </summary>
    public void NoteApiCall()
    {
        if (!IsMainThread)
            return;
        WaitIfPaused();
        if (++_spinCounter < 500)
            return;
        if (!_spinNoted)
        {
            _spinNoted = true;
            Context?.Trace.Warn("Script is polling the API in a tight loop without sleeping; the clock is auto-advanced by 250 ms every 500 calls.");
        }
        Advance(250);
    }

    public void WaitBackground(long milliseconds)
    {
        var wake = NowMs + Math.Max(0, milliseconds);
        var realStart = DateTime.UtcNow;
        var startPausedMs = TotalPausedMs;
        lock (_gate)
        {
            while (NowMs < wake && Context?.StopRequested != true)
            {
                if (_paused)
                {
                    Monitor.Wait(_gate, 50);
                    continue;
                }
                Monitor.Wait(_gate, 20);
                var now = DateTime.UtcNow;
                var pausedDelta = TotalPausedMs - startPausedMs;
                if (!_paused &&
                    (now - LastAdvanceRealUtc).TotalSeconds > 2 &&
                    (now - realStart).TotalMilliseconds - pausedDelta > 2000)
                    break;
            }
        }
    }

    private void PauseAwareRealDelay(long virtualDeltaMs)
    {
        if (virtualDeltaMs <= 0)
            return;

        lock (_pauseGate)
        {
            if (_speedMultiplier <= 0 || double.IsInfinity(_speedMultiplier))
                return;

            _realDebtMs += virtualDeltaMs / _speedMultiplier;
            while (_realDebtMs >= MinSleepThresholdMs && !_stopSignaled && Context?.StopRequested != true)
            {
                while (_paused && !_stopSignaled && Context?.StopRequested != true)
                    Monitor.Wait(_pauseGate);

                if (_stopSignaled || Context?.StopRequested == true)
                    break;
                if (_speedMultiplier <= 0 || double.IsInfinity(_speedMultiplier))
                {
                    _realDebtMs = 0;
                    break;
                }

                var slice = (int)Math.Min(PacingSliceMaxMs, _realDebtMs);
                if (slice <= 0)
                    break;

                var swBefore = Stopwatch.GetTimestamp();
                Monitor.Wait(_pauseGate, slice);
                var elapsedMs = (Stopwatch.GetTimestamp() - swBefore) * 1000.0 / Stopwatch.Frequency;
                if (!_paused)
                    _realDebtMs = Math.Max(0, _realDebtMs - elapsedMs);

                LastAdvanceRealUtc = DateTime.UtcNow;
                Context?.NoteApiActivity();
            }
        }
    }

    private void SetNow(long value) => Interlocked.Exchange(ref _nowMs, value);
}
