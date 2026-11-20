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
    public SimulationOutcome Outcome { get; init; }
    public string? StopReason { get; init; }
    public string? ExceptionText { get; init; }
    public TimeSpan CompileDuration { get; init; }
    public TimeSpan RealDuration { get; init; }
    public TimeSpan VirtualDuration { get; init; }
    public long TotalApiCalls { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int DropsPicked { get; init; }
    public int QuestsAccepted { get; init; }
    public int QuestsCompleted { get; init; }
    public List<string> MapsVisited { get; init; } = new();
    public List<string> SynthesizedMaps { get; init; } = new();
    public Dictionary<string, int> KillsByMonster { get; init; } = new();
    public Dictionary<string, int> DropsByItem { get; init; } = new();
    public Dictionary<int, int> QuestTurnIns { get; init; } = new();
    public WorldSnapshot InitialState { get; init; } = new();
    public WorldSnapshot FinalState { get; init; } = new();
    public List<SynthesizedDataRecord> SynthesizedData { get; init; } = new();
    public Dictionary<string, long> UnimplementedCalls { get; init; } = new();
    public Dictionary<string, long> TopApiCalls { get; init; } = new();
    public List<TraceEntry> ScriptLogs { get; init; } = new();
    public List<TraceEntry> Warnings { get; init; } = new();
    public List<TraceEntry> RecentTimeline { get; init; } = new();
    public List<CompileDiagnostic> CompileErrors { get; init; } = new();
    public string? TraceFilePath { get; init; }

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
        sb.AppendLine($"- **Compile Time:** `{CompileDuration.TotalMilliseconds:F0} ms`");
        sb.AppendLine($"- **Real Time:** `{RealDuration.TotalSeconds:F2} s`");
        sb.AppendLine($"- **Simulated (Virtual) Time:** `{FormatVirtual(VirtualDuration)}`");
        sb.AppendLine($"- **Total API Calls:** `{TotalApiCalls:N0}`");
        if (!string.IsNullOrEmpty(TraceFilePath))
            sb.AppendLine($"- **Full Trace File:** `{TraceFilePath}`");
        sb.AppendLine();

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
        sb.AppendLine($"| Drops Picked Up | {DropsPicked:N0} |");
        sb.AppendLine($"| Quests Accepted / Completed | {QuestsAccepted:N0} / {QuestsCompleted:N0} |");
        sb.AppendLine($"| Maps Visited | {(MapsVisited.Count > 0 ? string.Join(", ", MapsVisited.Select(m => $"`{m}`")) : "_none_")} |");
        sb.AppendLine();

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

    private static string FormatVirtual(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
}
