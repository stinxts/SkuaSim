using System.Text;
using System.Text.Json;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Runtime;

public enum SimulationOutcome
{
    Completed,
    StoppedByScript,
    LimitReached,
    ScriptException,
    CompileError,
    /// <summary>No player progress for Limits.MaxNoProgressMinutes of virtual time (likely an infinite loop).</summary>
    Stalled
}

public sealed class SimulationReport
{
    public required string ScriptPath { get; init; }
    public string? ScenarioName { get; init; }
    public int Seed { get; init; }

    /// <summary>The character at the end of the run as a new scenario ("Export Scenario"); not part of the report JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Scenarios.Scenario? EndScenario { get; init; }
    public SimulationOutcome Outcome { get; init; }
    public string? StopReason { get; init; }
    public string? ExceptionText { get; init; }
    public TimeSpan CompileDuration { get; init; }
    public TimeSpan RealDuration { get; init; }
    public TimeSpan VirtualDuration { get; init; }
    public long TotalApiCalls { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int PvpMatchesWon { get; init; }
    public int PvpRewardsGiven { get; init; }
    public int DropsPicked { get; init; }
    public int QuestsAccepted { get; init; }
    public int QuestsCompleted { get; init; }
    public List<string> MapsVisited { get; init; } = new();
    public List<string> SynthesizedMaps { get; init; } = new();
    public Dictionary<string, int> KillsByMonster { get; init; } = new();
    public Dictionary<string, int> DropsByItem { get; init; } = new();
    /// <summary>Server warnings/messages sent to the script (real texts): text → count.</summary>
    public Dictionary<string, int> ServerMessages { get; init; } = new();
    /// <summary>Scenario faults that fired, in order ("[time] kind on action #n (target)").</summary>
    public List<string> InjectedFaults { get; init; } = new();
    /// <summary>Scenario asserts checked at the end of the run (empty when the scenario has none).</summary>
    public List<AssertionResult> Assertions { get; init; } = new();
    public int AssertionsFailed => Assertions.Count(a => !a.Passed);
    public Dictionary<int, int> QuestTurnIns { get; init; } = new();
    public WorldSnapshot InitialState { get; init; } = new();
    public WorldSnapshot FinalState { get; init; } = new();
    public List<SynthesizedDataRecord> SynthesizedData { get; init; } = new();

    /// <summary>Items that open quests (quest pets…) the script checked for and the character does not own.</summary>
    public List<World.MissingUnlockItem> MissingUnlockItems { get; init; } = new();
    public Dictionary<string, long> UnimplementedCalls { get; init; } = new();
    public Dictionary<string, long> TopApiCalls { get; init; } = new();
    public List<TraceEntry> ScriptLogs { get; init; } = new();
    public List<TraceEntry> Warnings { get; init; } = new();
    public List<TraceEntry> RecentTimeline { get; init; } = new();
    public List<CompileDiagnostic> CompileErrors { get; init; } = new();
    public string? TraceFilePath { get; init; }

    /// <summary>Virtual start time (scenario "startDate" or the default 2026-01-01 12:00 UTC).</summary>
    public DateTime StartUtc { get; init; }

    /// <summary>True when <see cref="StartUtc"/> was taken from the PC clock (scenario "startAtCurrentTime").</summary>
    public bool StartFromPcClock { get; init; }

    /// <summary>Daily (and weekly/monthly) server resets crossed during the run (00:00 US Eastern).</summary>
    public int ServerResets { get; init; }

    /// <summary>AE calendar file used for server boosts (null when the scenario does not use it).</summary>
    public string? CalendarInfo { get; init; }

    /// <summary>Calendar server-boost states observed during the run.</summary>
    public List<string> CalendarBoosts { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# SkuaSim Execution Report — `{Path.GetFileName(ScriptPath)}`");
        sb.AppendLine();
        sb.AppendLine($"- **Script:** `{ScriptPath}`");
        if (!string.IsNullOrWhiteSpace(ScenarioName))
            sb.AppendLine($"- **Scenario:** `{ScenarioName}`");
        sb.AppendLine($"- **Seed:** `{Seed}`");
        sb.AppendLine($"- **Outcome:** **{Outcome}**{(string.IsNullOrEmpty(StopReason) ? "" : $" ({StopReason})")}");
        if (Assertions.Count > 0)
            sb.AppendLine($"- **Assertions:** **{(AssertionsFailed == 0 ? "PASSED" : "FAILED")}** ({Assertions.Count - AssertionsFailed}/{Assertions.Count} passed)");
        sb.AppendLine($"- **Compile Time:** `{CompileDuration.TotalMilliseconds:F0} ms`");
        sb.AppendLine($"- **Real Time:** `{RealDuration.TotalSeconds:F2} s`");
        sb.AppendLine($"- **Simulated (Virtual) Time:** `{FormatVirtual(VirtualDuration)}`");
        if (StartUtc != default)
        {
            var startEt = Data.AeCalendar.ToEastern(StartUtc);
            sb.AppendLine($"- **Virtual Start:** `{StartUtc:yyyy-MM-dd HH:mm} UTC` ({startEt:ddd yyyy-MM-dd HH:mm} server time){(ServerResets > 0 ? $", {ServerResets} daily reset(s) crossed" : "")}");
            if (StartFromPcClock)
                sb.AppendLine($"  - Taken from the PC clock; reproduce with `--start-date {StartUtc:yyyy-MM-ddTHH:mm:ssZ}` (or `\"startDate\"` in the scenario) and the same seed.");
        }
        if (!string.IsNullOrEmpty(CalendarInfo))
        {
            sb.AppendLine($"- **AE Calendar:** `{CalendarInfo}`");
            foreach (var b in CalendarBoosts)
                sb.AppendLine($"  - Server boosts {b}");
        }
        sb.AppendLine($"- **Total API Calls:** `{TotalApiCalls:N0}`");
        if (!string.IsNullOrEmpty(TraceFilePath))
            sb.AppendLine($"- **Full Trace File:** `{TraceFilePath}`");
        sb.AppendLine();

        if (Assertions.Count > 0)
        {
            sb.AppendLine("## Scenario Assertions");
            sb.AppendLine("| # | Result | Assert | Expected | Actual |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var a in Assertions)
                sb.AppendLine($"| {a.Index} | {(a.Passed ? "PASS" : "**FAIL**")} | {Cell(a.Description)} | {Cell(a.Expected)} | {Cell(a.Actual)} |");
            sb.AppendLine();
        }

        if (CompileErrors.Count > 0)
        {
            sb.AppendLine("## Compile Errors");
            foreach (var err in CompileErrors)
                sb.AppendLine($"- `{err}`");
            sb.AppendLine();
            return sb.ToString();
        }

        if (!string.IsNullOrEmpty(ExceptionText))
        {
            sb.AppendLine("## Unhandled Script Exception");
            sb.AppendLine("```text");
            sb.AppendLine(ExceptionText);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("## Summary Metrics");
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Final Location | `{FinalState.Map}-{FinalState.Room}` (`{FinalState.Cell}` / `{FinalState.Pad}`) |");
        sb.AppendLine($"| Player Level / Gold | Lv. {FinalState.Level} / {FinalState.Gold:N0} Gold |");
        sb.AppendLine($"| Class / HP / Effective DPS | {FinalState.EquippedClass ?? "-"} (Rank {FinalState.ClassRank}) / {FinalState.Health:N0} / {FinalState.MaxHealth:N0} HP / {FinalState.EffectiveDps:N0} DPS |");
        sb.AppendLine($"| Combat (Crits / Dodges / Healed) | {FinalState.PlayerCrits:N0} crits / {FinalState.PlayerDodges:N0} dodges / {FinalState.TotalHealingDone:N0} HP healed |");
        sb.AppendLine($"| Monsters Killed | {Kills:N0} |");
        sb.AppendLine($"| Player Deaths | {Deaths:N0} |");
        if (PvpMatchesWon > 0)
            sb.AppendLine($"| PvP Matches Won / Rewards | {PvpMatchesWon:N0} / {PvpRewardsGiven:N0} |");
        sb.AppendLine($"| Drops Picked Up | {DropsPicked:N0} |");
        sb.AppendLine($"| Quests Accepted / Completed | {QuestsAccepted:N0} / {QuestsCompleted:N0} |");
        sb.AppendLine($"| Maps Visited | {(MapsVisited.Count > 0 ? string.Join(", ", MapsVisited.Select(m => $"`{m}`")) : "_none_")} |");
        sb.AppendLine();

        if (MissingUnlockItems.Count > 0)
        {
            sb.AppendLine("## Paths That Depend on Items the Character Does Not Own");
            sb.AppendLine("> The script checked for these items; each one opens quests (quest pets, weapons, classes…), so owning it would likely change the script's path or rewards. Add one to the scenario (inventory) to simulate that path.");
            sb.AppendLine();
            foreach (var m in MissingUnlockItems)
                sb.AppendLine($"- **{m.Name}** ({m.Category}, #{m.Id}) — checked x{m.Checks}, first at `{FormatVirtual(TimeSpan.FromMilliseconds(m.FirstVirtualMs))}`; opens {m.QuestCount} quest(s): {m.Opens}");
            sb.AppendLine();
        }

        if (SynthesizedData.Count > 0)
        {
            sb.AppendLine("## Synthesized (Invented) Data Warnings");
            sb.AppendLine("> The following data was not present in `gamedata.json` or the scenario and was automatically synthesized:");
            sb.AppendLine();
            foreach (var syn in SynthesizedData)
                sb.AppendLine($"- **[{syn.Key}]** (x{syn.Count}): {syn.Description}");
            sb.AppendLine();
        }

        if (FinalState.Inventory.Count > 0 || FinalState.TempInventory.Count > 0)
        {
            sb.AppendLine("## Final Inventory");
            foreach (var item in FinalState.Inventory)
                sb.AppendLine($"- `{item.Name}` (#{item.Id}) x{item.Quantity}{(item.Equipped ? " **[Equipped]**" : "")}");
            foreach (var item in FinalState.TempInventory)
                sb.AppendLine($"- `{item.Name}` (#{item.Id}) x{item.Quantity} *(Temp)*");
            sb.AppendLine();
        }

        if (KillsByMonster.Count > 0)
        {
            sb.AppendLine("## Monster Kills");
            foreach (var (mon, count) in KillsByMonster.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- `{mon}`: {count:N0}");
            sb.AppendLine();
        }

        if (InjectedFaults.Count > 0)
        {
            sb.AppendLine("## Injected Faults");
            foreach (var line in InjectedFaults)
                sb.AppendLine($"- {line}");
            sb.AppendLine();
        }

        if (ServerMessages.Count > 0)
        {
            sb.AppendLine("## Server Warnings & Messages");
            foreach (var (text, count) in ServerMessages.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- `{text}`: {count:N0}");
            sb.AppendLine();
        }

        if (QuestTurnIns.Count > 0)
        {
            sb.AppendLine("## Completed Quests");
            foreach (var (qid, count) in QuestTurnIns.OrderBy(kv => kv.Key))
                sb.AppendLine($"- Quest `#{qid}`: {count:N0} turn-in(s)");
            sb.AppendLine();
        }

        if (ScriptLogs.Count > 0)
        {
            sb.AppendLine("## Script Logs");
            sb.AppendLine("```text");
            foreach (var log in ScriptLogs.TakeLast(100))
                sb.AppendLine($"[{log.Clock}] {log.Text}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (var w in Warnings.TakeLast(50))
                sb.AppendLine($"- `[{w.Clock}]` {w.Text}");
            sb.AppendLine();
        }

        if (UnimplementedCalls.Count > 0)
        {
            sb.AppendLine("## Unimplemented API Members Touched");
            foreach (var (member, count) in UnimplementedCalls.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- `{member}`: {count:N0} call(s)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace('\n', ' ');

    private static string FormatVirtual(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
}
