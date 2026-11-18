namespace SkuaSim.UI.Models;

public sealed record ScriptEntry(string DisplayName, string FullPath)
{
    public override string ToString() => DisplayName;
}

public sealed record ScenarioEntry(string DisplayName, string? FilePath)
{
    public override string ToString() => DisplayName;
}

public sealed record SpeedPreset(string Label, double Multiplier)
{
    public override string ToString() => Label;
}

public sealed record MonsterRowItem(
    int MapId,
    string Name,
    int Level,
    int Hp,
    int MaxHp,
    bool Alive,
    bool IsTarget,
    bool Synthesized)
{
    public double HpPercent => MaxHp <= 0 ? 0 : Math.Clamp((double)Hp * 100.0 / MaxHp, 0, 100);
    public string HpText => Alive ? $"{Hp:N0} / {MaxHp:N0}" : "Respawning...";
    public string Badges => (IsTarget ? "[TARGET] " : "") + (Synthesized ? "[SYNTH] " : "") + (Alive ? $"Lv.{Level}" : "DEAD");
}

public sealed record ItemRowItem(int Id, string Name, int Quantity, bool Equipped, string Location)
{
    public string QuantityText => $"x{Quantity:N0}";
    public string TagText => Equipped ? "EQUIPPED" : Location;
}

public sealed record QuestRowItem(int Id, string Name, bool Active, bool Completed, bool DailyCompleted, bool CanComplete, int TurnIns)
{
    public string StatusText
    {
        get
        {
            if (Active && CanComplete) return "Ready to Turn In";
            if (Active) return "Active";
            if (DailyCompleted) return "Daily Done";
            if (Completed) return "Completed";
            return "Registered";
        }
    }

    public string TurnInsText => TurnIns > 0 ? $"{TurnIns} turn-in(s)" : "-";
}

public sealed record FactionRowItem(int Id, string Name, int Rank, int Rep, int RequiredRep, int TotalRep)
{
    public string RankText => $"Rank {Rank}";
    public string RepText => RequiredRep > 0 ? $"{Rep:N0} / {RequiredRep:N0} (Total {TotalRep:N0})" : $"Total {TotalRep:N0}";
}

public sealed record LogRowItem(string Clock, string Thread, string Text)
{
    public string FormattedLine => $"[{Clock}] {Text}";
}

public sealed record CompileErrorRowItem(string File, int Line, int Column, string Id, string Message)
{
    public string FileName => Path.GetFileName(File);
    public string LocationText => Line > 0 ? $"{FileName}({Line},{Column}) [{Id}]" : $"{FileName} [{Id}]";
}

public sealed record DiagnosticRowItem(string Badge, string Title, string Detail, long Count)
{
    public string CountText => Count > 0 ? $"x{Count:N0}" : "";
}
