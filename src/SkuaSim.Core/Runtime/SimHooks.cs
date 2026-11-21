namespace SkuaSim.Runtime;

/// <summary>
/// Time source used by the simulator. Implemented by the virtual clock.
/// </summary>
public interface ISimTimeSource
{
    DateTime Now { get; }
    long ElapsedMilliseconds { get; }
    void Sleep(int milliseconds);
    Task Delay(int milliseconds, CancellationToken cancellationToken);
    Task StartFlow(Func<Task?> body, CancellationToken cancellationToken);
    Task<T> StartFlow<T>(Func<Task<T>?> body, CancellationToken cancellationToken);
    int NextScriptRandomSeed();
    /// <summary>Lets virtual time pass (in fixed steps) until <paramref name="done"/> or the virtual timeout; false on timeout.</summary>
    bool PumpUntil(Func<bool> done, long timeoutMs);
}

/// <summary>
/// Static hooks that compiled scripts call instead of real-time APIs. The script compiler rewrites Task.Run,
/// Task.WaitAll/WaitAny, new Random(),
/// Thread.Sleep, Task.Delay, DateTime.Now/UtcNow/Today, DateTimeOffset.Now/UtcNow, Environment.TickCount(64)
/// and Stopwatch to these members, so scripts run on the simulator's virtual clock (fast and deterministic).
/// When no simulation is active, every member falls back to the real system behavior.
/// </summary>
public static class SimHooks
{
    private static volatile ISimTimeSource? _source;

    public static ISimTimeSource? Source
    {
        get => _source;
        set => _source = value;
    }

    public static void Sleep(int millisecondsTimeout)
    {
        var src = _source;
        if (src == null)
        {
            Thread.Sleep(millisecondsTimeout);
            return;
        }
        src.Sleep(Math.Max(0, millisecondsTimeout));
    }

    public static void Sleep(TimeSpan timeout) => Sleep((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));

    public static Task Delay(int millisecondsDelay) => Delay(millisecondsDelay, CancellationToken.None);

    public static Task Delay(int millisecondsDelay, CancellationToken cancellationToken)
    {
        var src = _source;
        return src == null
            ? Task.Delay(millisecondsDelay, cancellationToken)
            : src.Delay(Math.Max(0, millisecondsDelay), cancellationToken);
    }

    public static Task Delay(TimeSpan delay) => Delay((int)Math.Clamp(delay.TotalMilliseconds, 0, int.MaxValue), CancellationToken.None);

    public static Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Delay((int)Math.Clamp(delay.TotalMilliseconds, 0, int.MaxValue), cancellationToken);

    // Task.Run → managed flows (one script thread at a time, in virtual-time order; see SimClock.Flows.cs). Same
    // overload set as Task.Run so the rewritten call binds to the same shape.
    public static Task Run(Action action) => Run(action, CancellationToken.None);

    public static Task Run(Action action, CancellationToken cancellationToken) =>
        _source is { } src
            ? src.StartFlow(() => { action(); return null; }, cancellationToken)
            : Task.Run(action, cancellationToken);

    public static Task Run(Func<Task?> function) => Run(function, CancellationToken.None);

    public static Task Run(Func<Task?> function, CancellationToken cancellationToken) =>
        _source is { } src ? src.StartFlow(function, cancellationToken) : Task.Run(function, cancellationToken);

    public static Task<TResult> Run<TResult>(Func<TResult> function) => Run(function, CancellationToken.None);

    public static Task<TResult> Run<TResult>(Func<TResult> function, CancellationToken cancellationToken) =>
        _source is { } src
            ? src.StartFlow<TResult>(() => Task.FromResult(function()), cancellationToken)
            : Task.Run(function, cancellationToken);

    public static Task<TResult> Run<TResult>(Func<Task<TResult>?> function) => Run(function, CancellationToken.None);

    public static Task<TResult> Run<TResult>(Func<Task<TResult>?> function, CancellationToken cancellationToken) =>
        _source is { } src ? src.StartFlow(function, cancellationToken) : Task.Run(function, cancellationToken);

    // Task.WaitAll / Task.WaitAny: the waiting thread keeps virtual time (and the other flows) moving instead of
    // blocking, and the timeout is virtual. Exceptions and return values come from the real API once it is settled.
    public static void WaitAll(params Task[] tasks) => WaitAll(tasks, Timeout.Infinite, CancellationToken.None);

    public static bool WaitAll(Task[] tasks, int millisecondsTimeout) => WaitAll(tasks, millisecondsTimeout, CancellationToken.None);

    public static bool WaitAll(Task[] tasks, TimeSpan timeout) => WaitAll(tasks, ToTimeoutMs(timeout), CancellationToken.None);

    public static void WaitAll(Task[] tasks, CancellationToken cancellationToken) => WaitAll(tasks, Timeout.Infinite, cancellationToken);

    public static bool WaitAll(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        if (_source is not { } src)
            return Task.WaitAll(tasks, millisecondsTimeout, cancellationToken);
        var settled = src.PumpUntil(() => cancellationToken.IsCancellationRequested || tasks.All(t => t.IsCompleted), TimeoutOrMax(millisecondsTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        return settled && Task.WaitAll(tasks, 0, CancellationToken.None);
    }

    public static int WaitAny(params Task[] tasks) => WaitAny(tasks, Timeout.Infinite, CancellationToken.None);

    public static int WaitAny(Task[] tasks, int millisecondsTimeout) => WaitAny(tasks, millisecondsTimeout, CancellationToken.None);

    public static int WaitAny(Task[] tasks, TimeSpan timeout) => WaitAny(tasks, ToTimeoutMs(timeout), CancellationToken.None);

    public static int WaitAny(Task[] tasks, CancellationToken cancellationToken) => WaitAny(tasks, Timeout.Infinite, cancellationToken);

    public static int WaitAny(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        if (_source is not { } src)
            return Task.WaitAny(tasks, millisecondsTimeout, cancellationToken);
        src.PumpUntil(() => cancellationToken.IsCancellationRequested || tasks.Any(t => t.IsCompleted), TimeoutOrMax(millisecondsTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.WaitAny(tasks, 0, CancellationToken.None);
    }

    private static int ToTimeoutMs(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

    private static long TimeoutOrMax(int millisecondsTimeout) => millisecondsTimeout < 0 ? long.MaxValue : millisecondsTimeout;

    /// <summary>A script's <c>new Random()</c>: seeded from the run seed (creation order), so it repeats with it.</summary>
    public static Random NewRandom() => _source is { } src ? new Random(src.NextScriptRandomSeed()) : new Random();

    public static DateTime Now => _source?.Now ?? DateTime.Now;
    public static DateTime UtcNow => _source != null ? _source.Now.ToUniversalTime() : DateTime.UtcNow;
    public static DateTime Today => Now.Date;
    public static DateTimeOffset OffsetNow => _source != null ? new DateTimeOffset(_source.Now) : DateTimeOffset.Now;
    public static DateTimeOffset OffsetUtcNow => _source != null ? new DateTimeOffset(_source.Now).ToUniversalTime() : DateTimeOffset.UtcNow;
    public static long TickCount64 => _source != null ? 10_000_000 + _source.ElapsedMilliseconds : Environment.TickCount64;
    public static int TickCount => unchecked((int)TickCount64);
}

/// <summary>
/// Drop-in replacement for System.Diagnostics.Stopwatch driven by the virtual clock.
/// </summary>
public sealed class SimStopwatch
{
    public static readonly long Frequency = TimeSpan.TicksPerSecond;
    public static readonly bool IsHighResolution = true;

    private long _startTicks;
    private long _accumulatedTicks;

    public bool IsRunning { get; private set; }

    public TimeSpan Elapsed => new(ElapsedTicks);
    public long ElapsedMilliseconds => ElapsedTicks / TimeSpan.TicksPerMillisecond;
    public long ElapsedTicks => _accumulatedTicks + (IsRunning ? GetTimestamp() - _startTicks : 0);

    public static SimStopwatch StartNew()
    {
        var sw = new SimStopwatch();
        sw.Start();
        return sw;
    }

    public void Start()
    {
        if (IsRunning)
            return;
        _startTicks = GetTimestamp();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        _accumulatedTicks += GetTimestamp() - _startTicks;
        IsRunning = false;
    }

    public void Reset()
    {
        _accumulatedTicks = 0;
        IsRunning = false;
    }

    public void Restart()
    {
        _accumulatedTicks = 0;
        _startTicks = GetTimestamp();
        IsRunning = true;
    }

    public static long GetTimestamp() => SimHooks.TickCount64 * TimeSpan.TicksPerMillisecond;

    public static TimeSpan GetElapsedTime(long startingTimestamp) => new(GetTimestamp() - startingTimestamp);

    public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => new(endingTimestamp - startingTimestamp);

    public override string ToString() => Elapsed.ToString();
}
