using Newtonsoft.Json;

namespace SkuaSim.Collector.Models;

/// <summary>
/// Anonymized class profile captured from AQW skill packets (`sAct`), stat snapshots (`stu`/stats),
/// and combat tick (`ct`) events. Contains zero player or account identifiers.
/// </summary>
public sealed class CapturedClassProfile
{
    public string Name { get; set; } = "";
    public int SamplesObserved { get; set; }
    public string InferredStatModel { get; set; } = "";
    public List<CapturedClassSkill> Skills { get; set; } = new();
    public List<CapturedClassPassive> Passives { get; set; } = new();
    public Dictionary<int, CapturedLevelStatSnapshot> StatsByLevel { get; set; } = new();
    public CapturedPlayerCombatStats Combat { get; set; } = new();
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedClassSkill
{
    public int Slot { get; set; }
    public int ActID { get; set; }
    public int SkillID { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Type { get; set; } = "";
    public double Damage { get; set; } = 1.0;
    public int CooldownMs { get; set; }
    public int MpCost { get; set; }
    public int Range { get; set; } = 300;
    public int MinTargets { get; set; } = 1;
    public int MaxTargets { get; set; } = 1;
    public bool Auto { get; set; }
    public bool IsHealHint { get; set; }
    public bool IsSelfBuffHint { get; set; }
    public bool IsDotOrHotHint { get; set; }
}

public sealed class CapturedClassPassive
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int UnlockRank { get; set; }
}

/// <summary>
/// Anonymized stat snapshot observed for a class at a specific player level
/// and anonymous enhancement signature (e.g. "W9:100|C9:100|H9:100|K9:100").
/// </summary>
public sealed class CapturedLevelStatSnapshot
{
    public int Level { get; set; }
    public int MaxHealth { get; set; }
    public int Samples { get; set; }
    public int Strength { get; set; }
    public int Intellect { get; set; }
    public int Endurance { get; set; }
    public int Dexterity { get; set; }
    public int Wisdom { get; set; }
    public int Luck { get; set; }
    public int AttackPower { get; set; }
    public int SpellPower { get; set; }
    public double Haste { get; set; }
    public double CriticalChance { get; set; }
    public double CriticalMultiplier { get; set; }
    public double EvasionChance { get; set; }
    public string EnhancementSignature { get; set; } = "";
}

/// <summary>
/// Cumulative anonymized combat performance observed for a class across real battles.
/// Stores level sums alongside event counts so the simulator can normalize DPS/heal metrics across levels.
/// </summary>
public sealed class CapturedPlayerCombatStats
{
    public long Hits { get; set; }
    public long Crits { get; set; }
    public long Misses { get; set; }
    public long TotalDamageDealt { get; set; }
    public long MaxSingleHit { get; set; }
    public long LevelSumForDamage { get; set; }
    public long HealEvents { get; set; }
    public long TotalHealingDone { get; set; }
    public long LevelSumForHeal { get; set; }
    public long AttacksTaken { get; set; }
    public long DodgesTaken { get; set; }
    public long TotalDamageTaken { get; set; }

    [JsonIgnore]
    public double ObservedCritRate => Hits + Crits > 0 ? (double)Crits / (Hits + Crits) : 0;

    [JsonIgnore]
    public double ObservedDodgeRate => AttacksTaken > 0 ? (double)DodgesTaken / AttacksTaken : 0;

    [JsonIgnore]
    public double AverageDamagePerHit => Hits + Crits > 0 ? (double)TotalDamageDealt / (Hits + Crits) : 0;

    [JsonIgnore]
    public double AverageDamageSampleLevel => Hits + Crits > 0 ? (double)LevelSumForDamage / (Hits + Crits) : 0;

    [JsonIgnore]
    public double AverageHealPerEvent => HealEvents > 0 ? (double)TotalHealingDone / HealEvents : 0;

    [JsonIgnore]
    public double AverageHealSampleLevel => HealEvents > 0 ? (double)LevelSumForHeal / HealEvents : 0;
}

/// <summary>
/// Anonymized enhancement pattern profile keyed by AQW `EnhPatternID`
/// (e.g. 2=Fighter, 3=Thief, 6=Wizard, 9=Lucky, 25=Vim, 26=Examen, 27=Pneuma, 28=Anima).
/// </summary>
public sealed class CapturedEnhancementProfile
{
    public int PatternID { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<int, CapturedEnhancementLevelObservation> PureSetStatsByLevel { get; set; } = new();
    public long Observations { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedEnhancementLevelObservation
{
    public int EnhancementLevel { get; set; }
    public string ReferenceClass { get; set; } = "";
    public int Samples { get; set; }
    public int Strength { get; set; }
    public int Intellect { get; set; }
    public int Endurance { get; set; }
    public int Dexterity { get; set; }
    public int Wisdom { get; set; }
    public int Luck { get; set; }
}

public static class EnhancementPatternCatalog
{
    public static string GetPatternName(int patternId) => patternId switch
    {
        2 => "Fighter",
        3 => "Thief",
        5 => "Hybrid",
        6 => "Wizard",
        7 => "Healer",
        8 => "SpellBreaker",
        9 => "Lucky",
        10 => "Forge",
        11 => "Absolution",
        12 => "Avarice",
        24 => "Vainglory",
        25 => "Vim",
        26 => "Examen",
        27 => "Pneuma",
        28 => "Anima",
        29 => "Penitence",
        30 => "Lament",
        32 => "Hearty",
        _ => patternId > 0 ? $"Pattern_{patternId}" : "None"
    };
}
