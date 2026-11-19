using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.UI.Models;

public static class DiagnosticPalette
{
    public static readonly IBrush ErrorFg = SolidColorBrush.Parse("#FCA5A5");
    public static readonly IBrush ErrorBadgeBg = SolidColorBrush.Parse("#7F1D1D");
    public static readonly IBrush ErrorRowBg = SolidColorBrush.Parse("#231015");

    public static readonly IBrush HighWarnFg = SolidColorBrush.Parse("#FDBA74");
    public static readonly IBrush WarnFg = SolidColorBrush.Parse("#FCD34D");
    public static readonly IBrush WarnBadgeBg = SolidColorBrush.Parse("#7C2D12");
    public static readonly IBrush WarnRowBg = SolidColorBrush.Parse("#24190E");

    // Distinct signature Violet / Magenta for Synthesized / Invented data
    public static readonly IBrush SynthFg = SolidColorBrush.Parse("#F0ABFC");
    public static readonly IBrush SynthTitleFg = SolidColorBrush.Parse("#E879F9");
    public static readonly IBrush SynthBadgeBg = SolidColorBrush.Parse("#581C87");
    public static readonly IBrush SynthRowBg = SolidColorBrush.Parse("#241333");

    public static readonly IBrush WorldFg = SolidColorBrush.Parse("#86EFAC");
    public static readonly IBrush WorldBadgeBg = SolidColorBrush.Parse("#14532D");

    public static readonly IBrush ApiFg = SolidColorBrush.Parse("#7DD3FC");
    public static readonly IBrush ApiBadgeBg = SolidColorBrush.Parse("#0C4A6E");

    public static readonly IBrush StubFg = SolidColorBrush.Parse("#94A3B8");
    public static readonly IBrush StubTitleFg = SolidColorBrush.Parse("#67E8F9");
    public static readonly IBrush StubBadgeBg = SolidColorBrush.Parse("#1E293B");
    public static readonly IBrush StubRowBg = SolidColorBrush.Parse("#131E2A");

    public static readonly IBrush LogFg = SolidColorBrush.Parse("#E2E8F0");
    public static readonly IBrush LogBadgeBg = SolidColorBrush.Parse("#1E293B");
    public static readonly IBrush InfoFg = SolidColorBrush.Parse("#94A3B8");
    public static readonly IBrush InfoBadgeBg = SolidColorBrush.Parse("#1E293B");
    public static readonly IBrush TransparentBg = Brushes.Transparent;
}

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

public sealed record DisplayScalePreset(string Label, double ScaleFactor, bool IsAuto)
{
    public override string ToString() => Label;
}

public sealed record VirtualDurationPreset(string Label, double Minutes, bool IsCustom)
{
    public override string ToString() => Label;
}

public sealed record ClassCatalogOption(
    string Name,
    bool FromDatabase,
    string StatModel,
    int SkillCount,
    long Samples)
{
    public string DisplayName => FromDatabase
        ? $"{Name}  [DB · {SkillCount} skills]"
        : $"{Name}  [Archetype]";

    public override string ToString() => DisplayName;
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
    public IBrush BadgeForeground => Synthesized ? DiagnosticPalette.SynthTitleFg : SolidColorBrush.Parse("#F59E0B");
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

public sealed record LogRowItem(string Clock, string Thread, string Text, bool IsWarning = false)
{
    public string FormattedLine => $"[{Clock}] {Text}";

    public string SeverityBadge
    {
        get
        {
            if (IsWarning)
                return IsHighSeverityWarning(Text) ? "ALERT" : "WARN";
            if (ContainsAny(Text, "exception", "crash", "fatal", "error", "failed", "❌", "🛑"))
                return "ERR";
            if (ContainsAny(Text, "synthesiz", "invented"))
                return "SYNTH";
            if (ContainsAny(Text, "warn", "⚠️", "skipped", "not found", "missing"))
                return "WARN";
            if (ContainsAny(Text, "succeeded", "completed", "level up", "rank 10", "✅"))
                return "OK";
            return "LOG";
        }
    }

    public IBrush Foreground => SeverityBadge switch
    {
        "ERR" => DiagnosticPalette.ErrorFg,
        "ALERT" => DiagnosticPalette.HighWarnFg,
        "WARN" => DiagnosticPalette.WarnFg,
        "SYNTH" => DiagnosticPalette.SynthFg,
        "OK" => DiagnosticPalette.WorldFg,
        _ => DiagnosticPalette.LogFg
    };

    public IBrush BadgeBackground => SeverityBadge switch
    {
        "ERR" => DiagnosticPalette.ErrorBadgeBg,
        "ALERT" => DiagnosticPalette.ErrorBadgeBg,
        "WARN" => DiagnosticPalette.WarnBadgeBg,
        "SYNTH" => DiagnosticPalette.SynthBadgeBg,
        "OK" => DiagnosticPalette.WorldBadgeBg,
        _ => DiagnosticPalette.LogBadgeBg
    };

    public IBrush RowBackground => SeverityBadge switch
    {
        "ERR" => DiagnosticPalette.ErrorRowBg,
        "ALERT" => DiagnosticPalette.ErrorRowBg,
        "WARN" => DiagnosticPalette.WarnRowBg,
        "SYNTH" => DiagnosticPalette.SynthRowBg,
        _ => DiagnosticPalette.TransparentBg
    };

    private static bool IsHighSeverityWarning(string text) =>
        ContainsAny(text, "stall", "full", "does not exist", "failed", "threw", "tight loop", "limit");

    private static bool ContainsAny(string text, params string[] tokens)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var t in tokens)
        {
            if (text.Contains(t, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public sealed record CompileErrorRowItem(string File, int Line, int Column, string Id, string Message)
{
    public string FileName => Path.GetFileName(File);
    public string LocationText => Line > 0 ? $"{FileName}({Line},{Column}) [{Id}]" : $"{FileName} [{Id}]";
}

public sealed record DiagnosticRowItem(string Badge, string Title, string Detail, long Count)
{
    public string CountText => Count > 0 ? $"x{Count:N0}" : "";

    public IBrush TitleForeground => Badge.ToUpperInvariant() switch
    {
        "SYNTH" => DiagnosticPalette.SynthTitleFg,
        "WARN" => DiagnosticPalette.WarnFg,
        "ERR" => DiagnosticPalette.ErrorFg,
        "STUB" => DiagnosticPalette.StubTitleFg,
        _ => DiagnosticPalette.ApiFg
    };

    public IBrush BadgeBackground => Badge.ToUpperInvariant() switch
    {
        "SYNTH" => DiagnosticPalette.SynthBadgeBg,
        "WARN" => DiagnosticPalette.WarnBadgeBg,
        "ERR" => DiagnosticPalette.ErrorBadgeBg,
        "STUB" => DiagnosticPalette.StubBadgeBg,
        _ => DiagnosticPalette.ApiBadgeBg
    };

    public IBrush RowBackground => Badge.ToUpperInvariant() switch
    {
        "SYNTH" => DiagnosticPalette.SynthRowBg,
        "WARN" => DiagnosticPalette.WarnRowBg,
        "ERR" => DiagnosticPalette.ErrorRowBg,
        "STUB" => DiagnosticPalette.StubRowBg,
        _ => DiagnosticPalette.TransparentBg
    };
}

public sealed record TraceRowItem(long Seq, string Clock, string Kind, string Thread, string Text)
{
    public string KindTag => Kind.ToUpperInvariant();
    public string FormattedLine => $"[{Clock}] {Text}";

    public IBrush Foreground => Kind.ToLowerInvariant() switch
    {
        "synth" => DiagnosticPalette.SynthFg,
        "warn" => DiagnosticPalette.WarnFg,
        "error" => DiagnosticPalette.ErrorFg,
        "world" => DiagnosticPalette.WorldFg,
        "api" => DiagnosticPalette.ApiFg,
        "info" => DiagnosticPalette.InfoFg,
        _ => DiagnosticPalette.LogFg
    };

    public IBrush BadgeBackground => Kind.ToLowerInvariant() switch
    {
        "synth" => DiagnosticPalette.SynthBadgeBg,
        "warn" => DiagnosticPalette.WarnBadgeBg,
        "error" => DiagnosticPalette.ErrorBadgeBg,
        "world" => DiagnosticPalette.WorldBadgeBg,
        "api" => DiagnosticPalette.ApiBadgeBg,
        "info" => DiagnosticPalette.InfoBadgeBg,
        _ => DiagnosticPalette.LogBadgeBg
    };

    public IBrush RowBackground => Kind.ToLowerInvariant() switch
    {
        "synth" => DiagnosticPalette.SynthRowBg,
        "warn" => DiagnosticPalette.WarnRowBg,
        "error" => DiagnosticPalette.ErrorRowBg,
        _ => DiagnosticPalette.TransparentBg
    };
}

public partial class GearSlotBoostEditorRow : ObservableObject
{
    public string Slot { get; init; } = "Weapon";
    public IReadOnlyList<AqwBoostItemInfo> AvailableItems { get; init; } = Array.Empty<AqwBoostItemInfo>();
    public Action? OnChanged { get; init; }

    [ObservableProperty]
    public partial AqwBoostItemInfo? SelectedItem { get; set; }

    public bool HasBoost => SelectedItem != null && !string.IsNullOrWhiteSpace(SelectedItem.Meta);
    public string BoostSummary => SelectedItem?.Summary ?? "No boost";

    partial void OnSelectedItemChanged(AqwBoostItemInfo? value)
    {
        OnPropertyChanged(nameof(HasBoost));
        OnPropertyChanged(nameof(BoostSummary));
        OnChanged?.Invoke();
    }
}

public partial class EnhancementCheckItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Slot { get; init; } = "";
    public int QuestId { get; init; }
    public Action? OnChanged { get; init; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => OnChanged?.Invoke();
}

public partial class ScriptOptionEditorRow : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string ValueType { get; init; } = "System.String";
    public object? DefaultValue { get; init; }
    public bool IsHeader { get; init; }
    public bool IsBool { get; init; }
    public bool IsChoice { get; init; }
    public bool IsText => !IsHeader && !IsBool && !IsChoice;
    public bool IsEditable => !IsHeader;
    public Action? OnChanged { get; init; }
    public ObservableCollection<string> AllowedValues { get; } = new();

    [ObservableProperty]
    public partial bool BoolValue { get; set; }

    [ObservableProperty]
    public partial string TextValue { get; set; } = "";

    [ObservableProperty]
    public partial string? SelectedChoice { get; set; }

    partial void OnBoolValueChanged(bool value) => OnChanged?.Invoke();
    partial void OnTextValueChanged(string value) => OnChanged?.Invoke();
    partial void OnSelectedChoiceChanged(string? value) => OnChanged?.Invoke();
}

public sealed class ScriptOptionCategoryGroup
{
    public string Category { get; init; } = "Options";
    public string HeaderText => Category.Replace('_', ' ');
    public ObservableCollection<ScriptOptionEditorRow> Options { get; } = new();
}
