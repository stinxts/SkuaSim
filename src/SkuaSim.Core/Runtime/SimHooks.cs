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
}

/// <summary>
/// Static hooks that compiled scripts call instead of real-time APIs. The script compiler rewrites
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
