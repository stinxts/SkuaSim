namespace SkuaSim.Core.Runtime;

/// <summary>
/// Managed background flows: the work a script starts with Task.Run (rewritten to SimHooks.Run). Only one script thread
/// runs at a time: the main thread owns the turn, and a flow runs only when the main thread advances virtual time past
/// the flow's wake-up time (creation time, or the end of its Sleep/Task.Delay), in (time, order) order. A flow hands the
/// turn back when it sleeps, delays or ends. This makes runs with CoreBots background loops (registered-quest watcher,
/// AFK handler…) repeat exactly with the same seed, instead of depending on how the OS schedules threads.
///
/// Fallback (never hangs, but that flow is no longer deterministic, and it is reported): a flow that blocks on something
/// outside the simulator (a lock, network, a task the main thread never lets finish) for 2 s of real time while it holds
/// the turn, or that waits 2 s for a turn the main thread never gives (the main thread blocked on .Wait()/.Result), is
/// detached and runs freely like an ordinary background thread.
/// </summary>
public sealed partial class SimClock
{
    private const double FlowDetachRealSeconds = 2.0;
    private const int FlowSpinLimit = 500;

    internal sealed class ManagedFlow
    {
        public required SimClock Clock { get; init; }
        public required int Id { get; init; }
        public required string Name { get; init; }
        public long WakeMs { get; set; }
        public long Order { get; set; }
        public bool Detached { get; set; }
        public int Spin { get; set; }
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class FlowOrder : IComparer<ManagedFlow>
    {
        public static readonly FlowOrder Instance = new();
        public int Compare(ManagedFlow? x, ManagedFlow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var c = x.WakeMs.CompareTo(y.WakeMs);
            return c != 0 ? c : x.Order.CompareTo(y.Order);
        }
    }

    [ThreadStatic] private static ManagedFlow? t_flow;

    private readonly SortedSet<ManagedFlow> _pendingFlows = new(FlowOrder.Instance);
    private ManagedFlow? _turn; // null = the main thread has the turn
    private long _flowOrder;
    private int _flowIds;
    private int _scriptRandoms;

    /// <summary>Managed flows started this run and how many had to be detached (reported).</summary>
    public int FlowsStarted => Volatile.Read(ref _flowIds);
    public int FlowsDetached { get; private set; }

    internal ManagedFlow? CurrentManagedFlow()
    {
        var f = t_flow;
        return f != null && ReferenceEquals(f.Clock, this) && !f.Detached ? f : null;
    }

    /// <summary>Seed for a script's <c>new Random()</c>: derived from the run seed and the creation order.</summary>
    public int NextScriptRandomSeed()
    {
        var n = Interlocked.Increment(ref _scriptRandoms);
        return unchecked((Context?.Seed ?? 0) * 486_187_739 + n * 16_777_619);
    }

    /// <summary>
    /// Starts <paramref name="body"/> as a managed flow (the script's Task.Run). It first runs at the current virtual
    /// time, after what is already due, when the main thread next advances the clock (any Sleep/Delay/wait).
    /// </summary>
    public Task StartFlow(Func<Task?> body, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartFlowCore(body, cancellationToken, ex => tcs.TrySetException(ex), () => tcs.TrySetCanceled(cancellationToken), t => tcs.TrySetResult());
        return tcs.Task;
    }

    public Task<T> StartFlow<T>(Func<Task<T>?> body, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartFlowCore(() => body(), cancellationToken, ex => tcs.TrySetException(ex), () => tcs.TrySetCanceled(cancellationToken),
            t => tcs.TrySetResult(((Task<T>)t!).Result));
        return tcs.Task;
    }

    /// <summary>The script has ended: pending flows are cancelled and new ones run outside the turn order.</summary>
    public void EndFlows()
    {
        lock (_gate)
        {
            _flowsEnded = true;
            _pendingFlows.Clear();
            _turn = null;
            Monitor.PulseAll(_gate);
        }
    }

    private bool _flowsEnded;

    private void StartFlowCore(Func<Task?> body, CancellationToken ct, Action<Exception> fail, Action cancel, Action<Task?> done)
    {
        if (ct.IsCancellationRequested)
        {
            cancel();
            return;
        }
        if (_flowsEnded)
        {
            Task.Run(async () =>
            {
                var inner = body();
                if (inner != null)
                    await inner.ConfigureAwait(false);
                return inner;
            }).ContinueWith(outer => Complete(outer.IsCompletedSuccessfully ? outer.Result : outer, fail, cancel, done), TaskScheduler.Default);
            return;
        }
        var id = Interlocked.Increment(ref _flowIds);
        var flow = new ManagedFlow { Clock = this, Id = id, Name = $"flow{id}" };
        lock (_gate)
        {
            flow.WakeMs = NowMs;
            flow.Order = ++_flowOrder;
            _pendingFlows.Add(flow);
        }

        var thread = new Thread(() => FlowThreadMain(flow, body, fail, cancel, done))
        {
            IsBackground = true,
            Name = $"SkuaSim {flow.Name}"
        };
        thread.Start();
    }

    private void FlowThreadMain(ManagedFlow flow, Func<Task?> body, Action<Exception> fail, Action cancel, Action<Task?> done)
    {
        t_flow = flow;
        SynchronizationContext.SetSynchronizationContext(new FlowSynchronizationContext(this));
        var handedOver = false;
        try
        {
            WaitForTurn(flow);
            var task = body();
            if (task == null || task.IsCompleted)
            {
                Complete(task, fail, cancel, done);
                return;
            }
            // The flow awaits something that is not a simulator wait (e.g. another flow's task): its continuation is
            // posted back as a new managed flow by FlowSynchronizationContext; this thread's turn ends here.
            handedOver = true;
            task.ContinueWith(t => Complete(t, fail, cancel, done), TaskScheduler.Default);
        }
        catch (SimulationStopException)
        {
            cancel();
        }
        catch (OperationCanceledException)
        {
            cancel();
        }
        catch (Exception ex)
        {
            fail(ex);
        }
        finally
        {
            EndFlowTurn(flow);
            if (!handedOver)
                t_flow = null;
        }
    }

    private static void Complete(Task? task, Action<Exception> fail, Action cancel, Action<Task?> done)
    {
        if (task == null || task.IsCompletedSuccessfully)
            done(task);
        else if (task.IsCanceled)
            cancel();
        else if (task.Exception is { } agg)
            fail(agg.InnerExceptions.Count == 1 ? agg.InnerExceptions[0] : agg);
    }

    /// <summary>Earliest pending flow due at or before <paramref name="untilMs"/> (main thread, inside Advance).</summary>
    private ManagedFlow? PeekDueFlow(long untilMs)
    {
        lock (_gate)
        {
            if (_pendingFlows.Count == 0)
                return null;
            var first = _pendingFlows.Min!;
            return first.WakeMs <= untilMs ? first : null;
        }
    }

    /// <summary>Main thread: give the turn to <paramref name="flow"/> and wait until it sleeps, delays or ends.</summary>
    private void RunFlowTurn(ManagedFlow flow)
    {
        lock (_gate)
        {
            if (!_pendingFlows.Remove(flow) || flow.Detached)
                return;
            flow.LastActivityUtc = DateTime.UtcNow;
            _turn = flow;
            Monitor.PulseAll(_gate);
            while (ReferenceEquals(_turn, flow) && Context?.StopRequested != true && !_stopSignaled)
            {
                Monitor.Wait(_gate, 20);
                if (ReferenceEquals(_turn, flow) && !_paused &&
                    (DateTime.UtcNow - flow.LastActivityUtc).TotalSeconds > FlowDetachRealSeconds)
                {
                    DetachLocked(flow, "it blocked for 2 s of real time on something outside the simulator while running");
                    _turn = null;
                }
            }
            if (ReferenceEquals(_turn, flow))
                _turn = null; // stopping
        }
        LastAdvanceRealUtc = DateTime.UtcNow;
    }

    /// <summary>Flow thread: wait until the main thread gives this flow the turn (or it is detached / stopping).</summary>
    private void WaitForTurn(ManagedFlow flow)
    {
        var waitStart = DateTime.UtcNow;
        lock (_gate)
        {
            while (!ReferenceEquals(_turn, flow) && !flow.Detached)
            {
                if (Context?.StopRequested == true || _stopSignaled || _flowsEnded)
                {
                    _pendingFlows.Remove(flow);
                    throw new SimulationStopException(Context?.StopReason ?? "the script ended");
                }
                Monitor.Wait(_gate, 20);
                // The main thread has the turn but is not advancing the clock (e.g. blocked on task.Wait()): run free.
                var now = DateTime.UtcNow;
                if (_turn == null && !_paused &&
                    (now - waitStart).TotalSeconds > FlowDetachRealSeconds &&
                    (now - LastAdvanceRealUtc).TotalSeconds > FlowDetachRealSeconds &&
                    _pendingFlows.Contains(flow))
                {
                    _pendingFlows.Remove(flow);
                    DetachLocked(flow, "the main thread did not advance the clock for 2 s of real time while it waited for its turn");
                }
            }
            flow.LastActivityUtc = DateTime.UtcNow;
            flow.Spin = 0;
        }
    }

    /// <summary>
    /// Flow thread: sleep <paramref name="milliseconds"/> of virtual time, handing the turn back meanwhile. A cancelled
    /// <paramref name="cancellationToken"/> wakes it at once (like a real Task.Delay), still in turn order.
    /// </summary>
    private void YieldFlow(ManagedFlow flow, long milliseconds, CancellationToken cancellationToken = default)
    {
        WaitIfPaused();
        lock (_gate)
        {
            flow.WakeMs = NowMs + Math.Max(0, milliseconds);
            flow.Order = ++_flowOrder;
            _pendingFlows.Add(flow);
            if (ReferenceEquals(_turn, flow))
                _turn = null;
            Monitor.PulseAll(_gate);
        }
        using (cancellationToken.CanBeCanceled ? cancellationToken.Register(() => WakeFlowNow(flow)) : default)
            WaitForTurn(flow);
        if (flow.Detached)
            WaitBackgroundUnmanaged(Math.Max(0, flow.WakeMs - NowMs));
    }

    private void WakeFlowNow(ManagedFlow flow)
    {
        lock (_gate)
        {
            if (!_pendingFlows.Remove(flow))
                return;
            flow.WakeMs = Math.Min(flow.WakeMs, NowMs);
            flow.Order = ++_flowOrder;
            _pendingFlows.Add(flow);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Task.WaitAll/WaitAny on a script thread: let virtual time pass in 50 ms steps (running the flows being waited for)
    /// until <paramref name="done"/> or <paramref name="timeoutMs"/> of virtual time. Unmanaged threads just poll.
    /// </summary>
    public bool PumpUntil(Func<bool> done, long timeoutMs)
    {
        const int step = 50;
        var start = NowMs;
        while (!done())
        {
            if (NowMs - start >= timeoutMs)
                return false;
            var wait = (int)Math.Min(step, Math.Max(1, timeoutMs - (NowMs - start)));
            if (IsMainThread || CurrentManagedFlow() != null)
                Sleep(wait);
            else
            {
                WaitBackgroundUnmanaged(wait);
                if (Context?.StopRequested == true)
                    return done();
            }
        }
        return true;
    }

    private void EndFlowTurn(ManagedFlow flow)
    {
        lock (_gate)
        {
            _pendingFlows.Remove(flow);
            if (ReferenceEquals(_turn, flow))
                _turn = null;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// A flow polling the API without sleeping would keep the turn forever (time cannot pass while it runs), so after
    /// 500 calls it sleeps 250 ms of virtual time, like the main thread's tight-loop guard.
    /// </summary>
    private void NoteFlowApiCall(ManagedFlow flow)
    {
        flow.LastActivityUtc = DateTime.UtcNow;
        if (++flow.Spin < FlowSpinLimit)
            return;
        flow.Spin = 0;
        YieldFlow(flow, 250);
    }

    private void DetachLocked(ManagedFlow flow, string why)
    {
        if (flow.Detached)
            return;
        flow.Detached = true;
        FlowsDetached++;
        Monitor.PulseAll(_gate);
        Context?.Trace.Warn($"Background task '{flow.Name}' now runs outside the simulator's turn order ({why}); results that depend on it may differ between runs with the same seed.");
    }

    /// <summary>Posts await continuations of managed flows back as new managed flows (same turn order).</summary>
    private sealed class FlowSynchronizationContext : SynchronizationContext
    {
        private readonly SimClock _clock;
        public FlowSynchronizationContext(SimClock clock) => _clock = clock;
        public override void Post(SendOrPostCallback d, object? state) =>
            _clock.StartFlow(() => { d(state); return null; }, CancellationToken.None);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override SynchronizationContext CreateCopy() => this;
    }
}
