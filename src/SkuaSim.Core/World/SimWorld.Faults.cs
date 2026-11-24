using Newtonsoft.Json.Linq;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.World;

/// <summary>
/// Scenario fault injection (<see cref="Scenario.Faults"/>): deterministic server faults by occurrence of an action and/or
/// virtual time, so a script's recovery paths run in the simulator. Faults never touch <c>ctx.Rng</c>, so the rest of
/// the run's luck is unchanged.
/// </summary>
public sealed partial class SimWorld
{
    // Texts. slowDown / tooSoonAfterCombat: seen by the collector (2026-11-24). The two de-sync texts come from the
    // CoreBots listener that handles them (RelogRequieredListener); the collector has not seen them yet.
    public const string BuyDesyncText = "Item is not buyable. Item Inventory full. Re-login to syncronize your real bag slot amount.";
    public const string QuestDesyncText = "Quest Complete Failed: Missing Required Item";
    public const string SlowDownText = "Please slow down. Last action was too soon!";
    public const string TooSoonAfterCombatText = "You cannot do that too soon after combat.";

    private sealed class FaultState
    {
        public required FaultSetup Setup { get; init; }
        public required string Kind { get; init; }
        public required string Action { get; init; }
        public int Occurrences { get; set; }
        public int Fired { get; set; }
    }

    private List<FaultState>? _faults;
    private int _dropDroughtKillsLeft;

    /// <summary>Faults that fired, in order (report).</summary>
    public List<string> InjectedFaults { get; } = new();

    private static readonly Dictionary<string, string> DefaultFaultAction = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buyDesync"] = "buy", ["questDesync"] = "turnIn", ["joinFail"] = "join", ["dropDrought"] = "kill",
        ["tooSoonAfterCombat"] = "loadShop", ["slowDown"] = "accept", ["disconnect"] = "any",
    };

    private static readonly Dictionary<string, string[]> FaultActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buyDesync"] = new[] { "buy" },
        ["questDesync"] = new[] { "turnIn" },
        ["joinFail"] = new[] { "join" },
        ["dropDrought"] = new[] { "kill" },
        ["tooSoonAfterCombat"] = new[] { "buy", "sell", "turnIn", "accept", "join", "pickup", "loadShop", "any" },
        ["slowDown"] = new[] { "buy", "sell", "turnIn", "accept", "join", "pickup", "loadShop", "any" },
        ["disconnect"] = new[] { "buy", "sell", "turnIn", "accept", "join", "pickup", "loadShop", "kill", "any" },
    };

    private List<FaultState> Faults()
    {
        if (_faults != null)
            return _faults;
        _faults = new List<FaultState>();
        foreach (var f in _ctx.Scenario.Faults ?? new List<FaultSetup>())
        {
            var kind = DefaultFaultAction.Keys.FirstOrDefault(k => Eq(k, f.Kind));
            if (kind == null)
            {
                Trace.Warn($"Scenario fault '{f.Kind}' is unknown (known: {string.Join(", ", DefaultFaultAction.Keys)}); ignored.");
                continue;
            }
            var action = string.IsNullOrWhiteSpace(f.Action) ? DefaultFaultAction[kind] : f.Action.Trim();
            var allowed = FaultActions[kind].FirstOrDefault(a => Eq(a, action));
            if (allowed == null)
            {
                Trace.Warn($"Scenario fault '{kind}' cannot be triggered by '{action}' (allowed: {string.Join(", ", FaultActions[kind])}); ignored.");
                continue;
            }
            _faults.Add(new FaultState { Setup = f, Kind = kind, Action = allowed });
        }
        return _faults;
    }

    /// <summary>
    /// Counts the action for every fault it can trigger and returns the fault that fires now (the first one), or null.
    /// Called before the action happens.
    /// </summary>
    private FaultState? NextFault(string action, string? target)
    {
        var faults = Faults();
        if (faults.Count == 0)
            return null;

        FaultState? firing = null;
        lock (Sync)
        {
            foreach (var f in faults)
            {
                if (!Eq(f.Action, action) && !(Eq(f.Action, "any") && !Eq(action, "kill")))
                    continue;
                if (f.Setup.Target is { Length: > 0 } wanted && !Eq(wanted, target))
                    continue;
                if (f.Setup.AfterMinutes is { } after && Now < after * 60_000)
                    continue;

                f.Occurrences++;
                var limit = f.Setup.Times ?? (f.Setup.Every is > 0 ? int.MaxValue : Math.Max(1, f.Setup.Nth.Count));
                var hit = f.Setup.Nth.Contains(f.Occurrences)
                          || (f.Setup.Every is > 0 and var every && f.Occurrences % every == 0)
                          || (f.Setup.Nth.Count == 0 && f.Setup.Every is not > 0 && f.Occurrences == 1);
                if (firing == null && hit && f.Fired < limit)
                {
                    f.Fired++;
                    firing = f;
                }
            }
            if (firing != null)
            {
                var line = $"[{_ctx.Clock.Format(Now)}] {firing.Kind} on {action} #{firing.Occurrences}{(target != null ? $" ({target})" : "")}";
                InjectedFaults.Add(line);
                Trace.Warn($"Injected fault: {line}.");
            }
        }
        return firing;
    }

    /// <summary>
    /// Applies a fault that refuses the action (all kinds except dropDrought). Returns true when the action must not
    /// happen. <paramref name="sendSpecific"/> sends the action's own refusal packet (buyDesync / questDesync).
    /// </summary>
    private bool ApplyRefusingFault(FaultState fault, Action? sendSpecific = null)
    {
        switch (fault.Kind)
        {
            case "slowDown":
                SendServerString("warning", SlowDownText);
                return true;
            case "tooSoonAfterCombat":
                SendServerString("warning", TooSoonAfterCombatText);
                return true;
            case "disconnect":
                Logout(_ctx.GetBotOption("AutoRelogin", true), _ctx.GetBotOption("ReloginTryDelay", 3000));
                return true;
            case "joinFail":
                return true;
            case "buyDesync":
            case "questDesync":
                sendSpecific?.Invoke();
                return true;
            case "dropDrought":
                _dropDroughtKillsLeft = Math.Max(_dropDroughtKillsLeft, Math.Max(1, fault.Setup.Kills ?? 10));
                Trace.World($"Drop drought: the next {_dropDroughtKillsLeft} kills drop nothing.");
                return false;
        }
        return false;
    }

    /// <summary>Shortcut for hooks: true when an injected fault refuses this action.</summary>
    private bool FaultRefuses(string action, string? target, Action? sendSpecific = null) =>
        NextFault(action, target) is { } fault && ApplyRefusingFault(fault, sendSpecific);

    /// <summary>True while a drop drought swallows this kill's drops (consumes one kill).</summary>
    private bool InDropDrought()
    {
        if (_dropDroughtKillsLeft <= 0)
            return false;
        _dropDroughtKillsLeft--;
        return true;
    }

    private void SendQuestDesync(int questId) =>
        SendServerJson(new JObject { ["cmd"] = "ccqr", ["QuestID"] = questId, ["bSuccess"] = 0, ["strMessage"] = QuestDesyncText }, QuestDesyncText);
}
