using System.Globalization;
using SkuaSim.Core.Scenarios;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Runtime;

/// <summary>Result of one scenario assert (<see cref="Scenario.Asserts"/>), in scenario order.</summary>
public sealed record AssertionResult(int Index, string Check, string Description, bool Passed, string Expected, string Actual);

/// <summary>
/// Checks a scenario's <see cref="Scenario.Asserts"/> against the finished report. Reads the report only (never the
/// world or the clock), so the result is as deterministic as the run. An assert that cannot be evaluated (unknown check,
/// missing name/quest/text, no bounds where they are needed) fails, so a typo never passes silently.
/// </summary>
public static class ScenarioAssertions
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static List<AssertionResult> Evaluate(IReadOnlyList<AssertSetup>? asserts, SimulationReport report)
    {
        var results = new List<AssertionResult>();
        if (asserts == null)
            return results;
        var ran = report.Outcome != SimulationOutcome.CompileError && report.EndScenario != null;
        for (var i = 0; i < asserts.Count; i++)
        {
            var a = asserts[i];
            AssertionResult result;
            try
            {
                result = EvaluateOne(i + 1, a, report, ran);
            }
            catch (InvalidAssert ex)
            {
                result = new AssertionResult(i + 1, a.Check, a.Description ?? a.Check, false, "a valid assert", "invalid: " + ex.Message);
            }
            results.Add(result);
        }
        return results;
    }

    private sealed class InvalidAssert(string message) : Exception(message);

    private static AssertionResult EvaluateOne(int index, AssertSetup a, SimulationReport r, bool ran)
    {
        var check = (a.Check ?? "").Trim();
        if (check.Length == 0)
            throw new InvalidAssert("\"check\" is missing");

        if (check.Equals("outcome", Ci))
        {
            if (a.Is.Count == 0)
                throw new InvalidAssert("outcome needs \"is\" (e.g. [\"Completed\"])");
            foreach (var v in a.Is)
                if (!Enum.TryParse<SimulationOutcome>(v, true, out _))
                    throw new InvalidAssert($"unknown outcome '{v}' (use {string.Join(", ", Enum.GetNames<SimulationOutcome>())})");
            var ok = a.Is.Any(v => string.Equals(v, r.Outcome.ToString(), Ci));
            var actual = r.Outcome + (string.IsNullOrEmpty(r.StopReason) ? "" : $" ({r.StopReason})");
            return new AssertionResult(index, "outcome", a.Description ?? $"outcome is {string.Join(" or ", a.Is)}", ok, string.Join(" | ", a.Is), actual);
        }

        if (!ran)
            return new AssertionResult(index, check, a.Description ?? check, false, "a run", $"the script did not run ({r.Outcome})");

        switch (check.ToLowerInvariant())
        {
            case "item":
            {
                var name = Required(a.Name, "item needs \"name\"");
                var where = (a.In ?? "any").Trim().ToLowerInvariant();
                double Count(WorldSnapshot s)
                {
                    IEnumerable<ItemCount> items = where switch
                    {
                        "any" => s.Inventory.Concat(s.Bank).Concat(s.TempInventory).Concat(s.House),
                        "inventory" => s.Inventory,
                        "bank" => s.Bank,
                        "temp" => s.TempInventory,
                        "house" => s.House,
                        _ => throw new InvalidAssert($"unknown \"in\" '{a.In}' (use any, inventory, bank, temp, house)")
                    };
                    return items.Where(x => string.Equals(x.Name, name, Ci)).Sum(x => (double)x.Quantity);
                }
                var place = where == "any" ? "" : $" in {where}";
                return Numeric(index, a, "item", $"\"{name}\"{place}", Count(r.FinalState), Count(r.InitialState), defaultMin: true);
            }
            case "questturnins":
            {
                var value = a.Quest is { } q ? r.QuestTurnIns.GetValueOrDefault(q) : r.QuestTurnIns.Values.Sum();
                return Numeric(index, a, "questTurnIns", a.Quest is { } qq ? $"quest {qq} turn-ins" : "total quest turn-ins", value, null, defaultMin: true);
            }
            case "questcompleted":
            {
                var q = a.Quest ?? throw new InvalidAssert("questCompleted needs \"quest\"");
                var done = r.FinalState.CompletedQuests.Contains(q) || r.QuestTurnIns.GetValueOrDefault(q) > 0;
                return new AssertionResult(index, "questCompleted", a.Description ?? $"quest {q} completed", done, "completed",
                    done ? "completed" : r.FinalState.ActiveQuests.Contains(q) ? "active, not completed" : "not completed");
            }
            case "level":
                return Numeric(index, a, "level", "level", r.FinalState.Level, r.InitialState.Level, defaultMin: null);
            case "gold":
                return Numeric(index, a, "gold", "gold", r.FinalState.Gold, r.InitialState.Gold, defaultMin: null);
            case "coins":
                return Numeric(index, a, "coins", "AC", r.FinalState.Coins, r.InitialState.Coins, defaultMin: null);
            case "classrank":
                return Numeric(index, a, "classRank", $"class rank ({r.FinalState.EquippedClass ?? "-"})", r.FinalState.ClassRank, r.InitialState.ClassRank, defaultMin: null);
            case "factionrank":
            {
                var name = Required(a.Name, "factionRank needs \"name\"");
                double Rank(WorldSnapshot s) => s.Factions.FirstOrDefault(f => string.Equals(f.Name, name, Ci))?.Rank ?? 1;
                return Numeric(index, a, "factionRank", $"{name} rank", Rank(r.FinalState), Rank(r.InitialState), defaultMin: null);
            }
            case "kills":
                return Numeric(index, a, "kills", a.Name is { } m ? $"\"{m}\" kills" : "kills",
                    a.Name is { } mon ? r.KillsByMonster.Where(k => string.Equals(k.Key, mon, Ci)).Sum(k => k.Value) : r.Kills, null, defaultMin: true);
            case "drops":
                return Numeric(index, a, "drops", a.Name is { } d ? $"\"{d}\" drops picked" : "drops picked",
                    a.Name is { } item ? r.DropsByItem.Where(k => string.Equals(k.Key, item, Ci)).Sum(k => k.Value) : r.DropsPicked, null, defaultMin: true);
            case "deaths":
                return Numeric(index, a, "deaths", "deaths", r.Deaths, null, defaultMin: false);
            case "virtualminutes":
                return Numeric(index, a, "virtualMinutes", "virtual minutes", Math.Round(r.VirtualDuration.TotalMinutes, 2), null, defaultMin: null);
            case "map":
            {
                if (a.Is.Count == 0)
                    throw new InvalidAssert("map needs \"is\" (e.g. [\"battleon\"])");
                var ok = a.Is.Any(v => string.Equals(v, r.FinalState.Map, Ci));
                return new AssertionResult(index, "map", a.Description ?? $"ends in {string.Join(" or ", a.Is)}", ok, string.Join(" | ", a.Is),
                    $"{r.FinalState.Map} ({r.FinalState.Cell})");
            }
            case "mapvisited":
            {
                var name = Required(a.Name, "mapVisited needs \"name\"");
                var ok = r.MapsVisited.Any(m => string.Equals(m, name, Ci));
                return new AssertionResult(index, "mapVisited", a.Description ?? $"visits {name}", ok, "visited", ok ? "visited" : "not visited");
            }
            case "servermessage":
            {
                var text = Required(a.Text, "serverMessage needs \"text\"");
                return Numeric(index, a, "serverMessage", $"server messages containing \"{text}\"",
                    r.ServerMessages.Where(kv => kv.Key.Contains(text, Ci)).Sum(kv => kv.Value), null, defaultMin: true);
            }
            case "scriptlog":
            {
                var text = Required(a.Text, "scriptLog needs \"text\"");
                return Numeric(index, a, "scriptLog", $"script logs containing \"{text}\"", r.ScriptLogs.Count(l => l.Text.Contains(text, Ci)), null, defaultMin: true);
            }
            case "warning":
            {
                var text = Required(a.Text, "warning needs \"text\"");
                return Numeric(index, a, "warning", $"warnings containing \"{text}\"", r.Warnings.Count(l => l.Text.Contains(text, Ci)), null, defaultMin: true);
            }
            case "injectedfaults":
                return Numeric(index, a, "injectedFaults", a.Text is { } f ? $"injected faults matching \"{f}\"" : "injected faults",
                    r.InjectedFaults.Count(x => a.Text == null || x.Contains(a.Text, Ci)), null, defaultMin: true);
            case "synthesized":
            {
                var matches = r.SynthesizedData.Where(s => a.Text == null || s.Key.Contains(a.Text, Ci) || s.Description.Contains(a.Text, Ci)).ToList();
                var result = Numeric(index, a, "synthesized", a.Text is { } t ? $"synthesized data matching \"{t}\"" : "synthesized data records", matches.Count, null, defaultMin: false);
                return result.Passed || matches.Count == 0 ? result : result with { Actual = $"{result.Actual}: {Sample(matches.Select(s => s.Key))}" };
            }
            case "synthesizedmaps":
            {
                var result = Numeric(index, a, "synthesizedMaps", "synthesized maps visited", r.SynthesizedMaps.Count, null, defaultMin: false);
                return result.Passed || r.SynthesizedMaps.Count == 0 ? result : result with { Actual = $"{result.Actual}: {Sample(r.SynthesizedMaps)}" };
            }
            case "unimplemented":
            {
                var members = r.UnimplementedCalls.Keys.Where(k => a.Text == null || k.Contains(a.Text, Ci)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var result = Numeric(index, a, "unimplemented", a.Text is { } t ? $"unimplemented API members matching \"{t}\"" : "unimplemented API members touched", members.Count, null, defaultMin: false);
                return result.Passed || members.Count == 0 ? result : result with { Actual = $"{result.Actual}: {Sample(members)}" };
            }
            default:
                throw new InvalidAssert($"unknown check '{a.Check}'");
        }
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidAssert(message) : value.Trim();

    private static string Sample(IEnumerable<string> values)
    {
        var list = values.ToList();
        return string.Join(", ", list.Take(5)) + (list.Count > 5 ? $" (+{list.Count - 5} more)" : "");
    }

    /// <summary>
    /// Compares a number with min/max/exactly. <paramref name="defaultMin"/>: true = "min 1" when no bound is given,
    /// false = "max 0", null = a bound is required. With <c>gained</c>, the change since the start is compared.
    /// </summary>
    private static AssertionResult Numeric(int index, AssertSetup a, string check, string label, double final, double? initial, bool? defaultMin)
    {
        if (a.Gained && initial == null)
            throw new InvalidAssert($"{check} does not support \"gained\" (it already counts what happened during the run)");
        var value = a.Gained ? final - initial!.Value : final;
        double? min = a.Min, max = a.Max;
        if (a.Exactly is { } ex)
            (min, max) = (ex, ex);
        if (min == null && max == null)
        {
            if (defaultMin == null)
                throw new InvalidAssert($"{check} needs \"min\", \"max\" or \"exactly\"");
            if (defaultMin == true) min = 1; else max = 0;
        }
        if (min > max)
            throw new InvalidAssert($"min {Fmt(min!.Value)} is above max {Fmt(max!.Value)}");

        var expected = a.Exactly is { } e ? $"= {Fmt(e)}"
            : min != null && max != null ? $"{Fmt(min.Value)} .. {Fmt(max.Value)}"
            : min != null ? $">= {Fmt(min.Value)}" : $"<= {Fmt(max!.Value)}";
        var passed = (min == null || value >= min) && (max == null || value <= max);
        var subject = a.Gained ? $"{label} gained" : label;
        var actual = a.Gained ? $"{SignedFmt(value)} ({Fmt(initial!.Value)} → {Fmt(final)})" : Fmt(value);
        return new AssertionResult(index, check, a.Description ?? $"{subject} {expected}", passed, expected, actual);
    }

    private static string Fmt(double v) => v.ToString(v == Math.Floor(v) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture);
    private static string SignedFmt(double v) => (v > 0 ? "+" : "") + Fmt(v);
}
