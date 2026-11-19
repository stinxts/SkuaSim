using System.Reflection;
using System.Text.Json;
using SkuaSim.Core.Data;
using SkuaSim.Core.Proxies;
using SkuaSim.Core.Scenarios;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Runtime;

/// <summary>
/// Shared state of one simulation run: clock, trace, world, data catalog, proxies and stop control.
/// </summary>
public sealed class SimContext
{
    private long _lastApiActivityTicks = DateTime.UtcNow.Ticks;
    private int _postStopMainThreadCalls;

    public SimContext(Scenario scenario, GameDataCatalog catalog, SimClock clock, SimulationTrace trace, int seed, string skuaFlavor)
    {
        Scenario = scenario;
        Catalog = catalog;
        Clock = clock;
        Trace = trace;
        Seed = seed;
        Rng = new Random(seed);
        SkuaFlavor = skuaFlavor;
        clock.Context = this;
        Proxies = new ProxyHub(this);
        World = new SimWorld(this);
    }

    public Scenario Scenario { get; }
    public GameDataCatalog Catalog { get; }
    public SimClock Clock { get; }
    public SimulationTrace Trace { get; }
    public int Seed { get; }
    public Random Rng { get; }
    public string SkuaFlavor { get; }
    public ProxyHub Proxies { get; }
    public SimWorld World { get; }

    /// <summary>The root IScriptInterface proxy handed to the script.</summary>
    public object? Bot { get; set; }
    public ProxyState? EventsState { get; set; }
    public ProxyState? OptionsState { get; set; }
    public string? LoadedScriptPath { get; set; }
    public Dictionary<string, List<(string Cell, string MonsterName)>> ScriptMonsterHints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, List<string>> ScriptShopHints { get; } = new();

    public volatile bool StopRequested;
    public volatile bool CleanupPhase;
    public string? StopReason { get; private set; }

    public DateTime LastApiActivityUtc => new(Interlocked.Read(ref _lastApiActivityTicks), DateTimeKind.Utc);

    public void NoteApiActivity() => Interlocked.Exchange(ref _lastApiActivityTicks, DateTime.UtcNow.Ticks);

    public void RequestStop(string reason)
    {
        if (StopRequested)
            return;
        StopReason = reason;
        Interlocked.Exchange(ref _postStopMainThreadCalls, 0);
        StopRequested = true;
        Trace.Info("Stop requested: " + reason);
        Clock.NotifyStopRequested();
    }

    /// <summary>
    /// Guards against scripts with loops that ignore Bot.ShouldExit (e.g., CoreStory.KillQuest's
    /// while(CurrentRequirements.Count > 0)) by allowing a small grace window to observe ShouldExit
    /// and then unwinding cleanly via SimulationStopException.
    /// </summary>
    internal void NotePostStopApiCall(string memberName)
    {
        if (!StopRequested || CleanupPhase || !Clock.IsMainThread)
            return;
        if (memberName.EndsWith(".ShouldExit", StringComparison.OrdinalIgnoreCase) ||
            memberName.EndsWith(".ScriptRunning", StringComparison.OrdinalIgnoreCase))
            return;

        if (Interlocked.Increment(ref _postStopMainThreadCalls) >= 40)
            ThrowIfStopping();
    }

    /// <summary>
    /// Throws <see cref="SimulationStopException"/> when the run is stopping (outside the ScriptStopping cleanup phase).
    /// Called from every time-advancing API (Sleep, waits, kills), which every script loop eventually reaches.
    /// </summary>
    public void ThrowIfStopping()
    {
        if (StopRequested && !CleanupPhase)
            throw new SimulationStopException(StopReason ?? "stop requested");
    }

    /// <summary>
    /// Raises an IScriptEvent event (e.g. "MonsterKilled") to every handler the script subscribed.
    /// Handler exceptions are recorded as warnings, like Skua which isolates event handlers.
    /// </summary>
    public List<object?> RaiseEvent(string eventName, params object?[] args)
    {
        var results = new List<object?>();
        if (EventsState == null || !EventsState.Events.TryGetValue(eventName, out var del) || del == null)
            return results;

        Trace.Info($"Event {eventName}({ArgFormatter.FormatArgs(args)}) -> {del.GetInvocationList().Length} handler(s)");
        foreach (var handler in del.GetInvocationList())
        {
            try
            {
                results.Add(handler.DynamicInvoke(args));
            }
            catch (TargetInvocationException tie) when (tie.InnerException is SimulationStopException)
            {
                throw tie.InnerException;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Trace.Warn($"Event handler for {eventName} threw {inner.GetType().Name}: {inner.Message}");
            }
        }
        return results;
    }

    /// <summary>Reads a Bot.Options value (scenario override or simulator default).</summary>
    public T GetBotOption<T>(string name, T fallback)
    {
        if (OptionsState != null && OptionsState.Values.TryGetValue(name, out var value) && value is T typed)
            return typed;
        if (OptionsState?.Handler is IPropertyDefaults defaults && defaults.TryGetDefault(name, out var def) && def is T typedDefault)
            return typedDefault;
        return fallback;
    }

    public static object? ConvertJson(JsonElement element, Type target)
    {
        try
        {
            if (target == typeof(string))
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            if (target.IsEnum)
                return Enum.Parse(target, element.ToString().Replace(' ', '_'), ignoreCase: true);
            return element.Deserialize(target, Scenario.JsonOptions);
        }
        catch
        {
            return ValueConverter.Convert(element.ToString(), target);
        }
    }
}

/// <summary>
/// Lenient value conversion used for option values (strings from scenarios/config to typed values).
/// </summary>
public static class ValueConverter
{
    public static object? Convert(object? value, Type target)
    {
        if (value == null)
            return target.IsValueType ? Activator.CreateInstance(target) : null;
        if (target.IsInstanceOfType(value))
            return value;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (value is JsonElement je)
            return SimContext.ConvertJson(je, underlying);

        var text = value.ToString() ?? "";
        try
        {
            if (underlying == typeof(string))
                return text;
            if (underlying.IsEnum)
            {
                var normalized = string.Join(",", text.Split(',').Select(p => p.Trim().Replace(' ', '_')));
                return Enum.Parse(underlying, normalized, ignoreCase: true);
            }
            if (underlying == typeof(bool))
                return text == "1" || bool.Parse(text);
            return System.Convert.ChangeType(text, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return underlying.IsValueType ? Activator.CreateInstance(underlying) : null;
        }
    }

    public static T Convert<T>(object? value) => (T)Convert(value, typeof(T))!;
}
