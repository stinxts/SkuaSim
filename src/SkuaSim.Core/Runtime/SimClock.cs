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
    private readonly object _gate = new();
    private long _nowMs;
    private int _spinCounter;
    private bool _spinNoted;

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

    public string Format(long? ms = null)
    {
        var t = TimeSpan.FromMilliseconds(ms ?? NowMs);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    public void Sleep(int milliseconds)
    {
        Context?.ThrowIfStopping();
        if (IsMainThread || MainThreadId < 0)
            Advance(milliseconds);
        else
            WaitBackground(milliseconds);
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
        var target = NowMs + Math.Max(0, milliseconds);
        while (Scheduler.TryDequeueDue(target, out var ev))
        {
            SetNow(Math.Max(NowMs, ev!.Due));
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

        SetNow(Math.Max(NowMs, target));
        _spinCounter = 0;
        LastAdvanceRealUtc = DateTime.UtcNow;

        if (MaxVirtualMs > 0 && NowMs >= MaxVirtualMs)
            Context?.RequestStop($"virtual time limit reached ({Format(MaxVirtualMs)})");

        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    /// <summary>
    /// Called on every API call. A script polling the API in a tight loop without sleeping would never let
    /// the world change, so after many calls without time passing the clock nudges forward by 100 ms.
    /// </summary>
    public void NoteApiCall()
    {
        if (!IsMainThread)
            return;
        if (++_spinCounter < 20_000)
            return;
        if (!_spinNoted)
        {
            _spinNoted = true;
            Context?.Trace.Warn("Script is polling the API in a tight loop without sleeping; the clock is auto-advanced by 100 ms every 20,000 calls.");
        }
        Advance(100);
    }

    public void WaitBackground(long milliseconds)
    {
        var wake = NowMs + Math.Max(0, milliseconds);
        var realStart = DateTime.UtcNow;
        lock (_gate)
        {
            while (NowMs < wake && Context?.StopRequested != true)
            {
                Monitor.Wait(_gate, 20);
                var now = DateTime.UtcNow;
                if ((now - LastAdvanceRealUtc).TotalSeconds > 2 && (now - realStart).TotalSeconds > 2)
                    break;
            }
        }
    }

    private void SetNow(long value) => Interlocked.Exchange(ref _nowMs, value);
}
