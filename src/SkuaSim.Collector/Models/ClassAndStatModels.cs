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
    /// <summary>Latest snapshot per level, any gear (legacy; one account's gear overwrites another's).</summary>
    public Dictionary<int, CapturedLevelStatSnapshot> StatsByLevel { get; set; } = new();

    /// <summary>
    /// Latest snapshot per exact level, class rank and enhancement signature (additive in v3), key
    /// <see cref="ClassProfileKeys.StatKey"/>, e.g. <c>100|R10|W6:100|C6:100|H6:100|K6:100</c>.
    /// </summary>
    public Dictionary<string, CapturedLevelStatSnapshot> StatsByProfile { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, CapturedClassAuraStat> Auras { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All combat of the class, every level and gear mixed (legacy aggregate; the simulator no longer blends it).</summary>
    public CapturedPlayerCombatStats Combat { get; set; } = new();

    /// <summary>
    /// Combat split by level band (10 levels), class rank and enhancement signature (additive in v3), key
    /// <see cref="ClassProfileKeys.CombatKey"/>, e.g. <c>L91-100|R10|W6:100|C6:100|H6:100|K6:100</c>, so an endgame
    /// character's crit/dodge is never blended into a low-level scenario with the same class.
    /// </summary>
    public Dictionary<string, CapturedPlayerCombatStats> CombatByProfile { get; set; } = new(StringComparer.Ordinal);
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

    public CapturedPlayerCombatStats Clone() => (CapturedPlayerCombatStats)MemberwiseClone();

    /// <summary>Adds another set of counters (MaxSingleHit takes the maximum).</summary>
    public void Add(CapturedPlayerCombatStats o)
    {
        Hits += o.Hits;
        Crits += o.Crits;
        Misses += o.Misses;
        TotalDamageDealt += o.TotalDamageDealt;
        MaxSingleHit = Math.Max(MaxSingleHit, o.MaxSingleHit);
        LevelSumForDamage += o.LevelSumForDamage;
        HealEvents += o.HealEvents;
        TotalHealingDone += o.TotalHealingDone;
        LevelSumForHeal += o.LevelSumForHeal;
        AttacksTaken += o.AttacksTaken;
        DodgesTaken += o.DodgesTaken;
        TotalDamageTaken += o.TotalDamageTaken;
    }

    /// <summary>Adds what changed between two snapshots of a counter set (<paramref name="before"/> → <paramref name="after"/>).</summary>
    public void AddChange(CapturedPlayerCombatStats before, CapturedPlayerCombatStats after)
    {
        Hits += after.Hits - before.Hits;
        Crits += after.Crits - before.Crits;
        Misses += after.Misses - before.Misses;
        TotalDamageDealt += after.TotalDamageDealt - before.TotalDamageDealt;
        if (after.MaxSingleHit > before.MaxSingleHit)
            MaxSingleHit = Math.Max(MaxSingleHit, after.MaxSingleHit);
        LevelSumForDamage += after.LevelSumForDamage - before.LevelSumForDamage;
        HealEvents += after.HealEvents - before.HealEvents;
        TotalHealingDone += after.TotalHealingDone - before.TotalHealingDone;
        LevelSumForHeal += after.LevelSumForHeal - before.LevelSumForHeal;
        AttacksTaken += after.AttacksTaken - before.AttacksTaken;
        DodgesTaken += after.DodgesTaken - before.DodgesTaken;
        TotalDamageTaken += after.TotalDamageTaken - before.TotalDamageTaken;
    }

    [JsonIgnore]
    public bool IsEmpty => Hits + Crits + Misses + HealEvents + AttacksTaken == 0 && TotalDamageTaken == 0;
}

/// <summary>
/// Keys for class stats and combat split by character level, class rank and gear. The enhancement signature is
/// <c>W{pattern}:{level}|C…|H…|K…</c> (weapon, class, helm, cape; 0:0 = not enhanced), the same string the
/// simulator builds for a scenario. Rank 0 = unknown.
/// </summary>
public static class ClassProfileKeys
{
    public static string EnhancementSignature(int wPat, int wLvl, int cPat, int cLvl, int hPat, int hLvl, int kPat, int kLvl) =>
        $"W{wPat}:{wLvl}|C{cPat}:{cLvl}|H{hPat}:{hLvl}|K{kPat}:{kLvl}";

    /// <summary>First level of the 10-level band (1, 11, 21 … 91).</summary>
    public static int BandStart(int level) => (Math.Clamp(level, 1, 100) - 1) / 10 * 10 + 1;

    public static string Band(int level) => $"L{BandStart(level)}-{BandStart(level) + 9}";

    public static string CombatKey(int level, int rank, string signature) => $"{Band(level)}|R{rank}|{signature}";

    public static string StatKey(int level, int rank, string signature) => $"{level}|R{rank}|{signature}";

    /// <summary>The signature's patterns only, without enhancement levels: <c>W6|C6|H6|K6</c>.</summary>
    public static string PatternsOnly(string signature) =>
        string.Join('|', signature.Split('|').Select(part => part.Split(':')[0]));

    /// <summary>Splits a combat key into band, rank and signature; false for a malformed key.</summary>
    public static bool TryParseCombatKey(string key, out string band, out int rank, out string signature)
    {
        band = signature = string.Empty;
        rank = 0;
        var parts = key.Split('|', 3);
        if (parts.Length != 3 || !parts[0].StartsWith('L') || !parts[1].StartsWith('R') || !int.TryParse(parts[1].AsSpan(1), out rank))
            return false;
        band = parts[0];
        signature = parts[2];
        return true;
    }
}

/// <summary>
/// Anonymized enhancement pattern profile keyed by AQW `EnhPatternID`
/// (e.g. 2=Fighter, 3=Thief, 6=Wizard, 9=Lucky, 25=Vim, 26=Examen, 27=Pneuma, 28=Anima).
/// Legacy (2026-11-22): no longer recorded — it needed a "pure set" of gear and caught 1 pattern in 13.7k flushes.
/// Old data is kept and merged; <see cref="CapturedClassProfile.StatsByProfile"/> replaces it.
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
        1 => "Adventurer",
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

/// <summary>
/// Anonymized aura cast by a player class (self buff, party buff, or target debuff).
/// Essential for Phase 4 multi-client Army simulation (LoO, SC, AP, LR party buffs).
/// </summary>
public sealed class CapturedClassAuraStat
{
    public string Name { get; set; } = string.Empty;
    public string TargetScope { get; set; } = string.Empty; // "self", "party", "ally", "monster"
    public string Category { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public bool IsPartyBuff { get; set; }
    public bool IsStacking { get; set; }
    public int MaxStacksObserved { get; set; }
    public float? MinValue { get; set; }
    public float? MaxValue { get; set; }
    public long TimesSeen { get; set; }
}

/// <summary>
/// Captures boss/monster skill animation & message triggers from `ct.anims` (animStr, msg, HP% window, cadence).
/// Used to replay Ultra Boss cues (`Attack2`, `Charge`, `ChargeAttack2`, etc.) in multi-client simulations.
/// </summary>
public sealed class CapturedMonsterSkillEvent
{
    public string Key { get; set; } = string.Empty;
    public string AnimStr { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int CasterMapID { get; set; }
    public long TimesObserved { get; set; }
    public long IntervalSamples { get; set; }
    public long TotalIntervalMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageIntervalMs => IntervalSamples > 0
        ? Math.Round((double)TotalIntervalMs / IntervalSamples, 0)
        : 0.0;

    public double? MinHpPercent { get; set; }
    public double? MaxHpPercent { get; set; }
    public int MaxTargetsHit { get; set; }
    public HashSet<string> AurasInSameTick { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Captures map/boss safe-zone or hazard-zone packets (`cmd == "event"`, `args.zoneSet`: "A", "B", "").
/// </summary>
public sealed class CapturedZoneEvent
{
    public string ZoneSet { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;
    public long TimesObserved { get; set; }
    public long IntervalSamples { get; set; }
    public long TotalIntervalMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageIntervalMs => IntervalSamples > 0
        ? Math.Round((double)TotalIntervalMs / IntervalSamples, 0)
        : 0.0;

    public double? MinBossHpPercent { get; set; }
    public double? MaxBossHpPercent { get; set; }
}

/// <summary>
/// Empirical RNG & cadence metrics for Awe and Forge weapon procs (Spiral Carve, Awe Blast, Valiance, Dauntless, etc.).
/// </summary>
public sealed class CapturedProcStat
{
    public int ProcID { get; set; }
    public string Name { get; set; } = string.Empty;
    public HashSet<string> ObservedAuraNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long PlayerHitsWhileEquipped { get; set; }
    public long ProcTriggersObserved { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double EstimatedTriggerRate => PlayerHitsWhileEquipped > 0
        ? Math.Min(1.0, Math.Round((double)ProcTriggersObserved / PlayerHitsWhileEquipped, 4))
        : 0.0;

    public int DurationSeconds { get; set; }
    public long IntervalSamples { get; set; }
    public long TotalIntervalMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageCooldownMs => IntervalSamples > 0
        ? Math.Round((double)TotalIntervalMs / IntervalSamples, 0)
        : 0.0;

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public static class WeaponProcCatalog
{
    public static string GetProcName(int procId) => procId switch
    {
        1 => "Forge",
        2 => "Spiral Carve",
        3 => "Awe Blast",
        4 => "Health Vamp",
        5 => "Mana Vamp",
        6 => "Powerword Die",
        7 => "Lacerate",
        8 => "Smite",
        9 => "Valiance",
        10 => "Arcana's Concerto",
        11 => "Acheron",
        12 => "Elysium",
        13 => "Praxis",
        14 => "Dauntless",
        15 => "Ravenous",
        _ => procId > 0 ? $"Proc_{procId}" : "None"
    };

    /// <summary>
    /// Resolves a WeaponSpecial ProcID from an observed aura name (or verifies match with equipped ProcID).
    /// </summary>
    public static int ResolveProcIdFromAura(string? auraName, int equippedProcId = 0)
    {
        if (string.IsNullOrWhiteSpace(auraName))
            return 0;

        var n = auraName.Trim();
        if (n.Contains("Spiral Carve", StringComparison.OrdinalIgnoreCase)) return 2;
        if (n.Contains("Awe Blast", StringComparison.OrdinalIgnoreCase)) return 3;
        if (n.Contains("Health Vamp", StringComparison.OrdinalIgnoreCase)) return 4;
        if (n.Contains("Mana Vamp", StringComparison.OrdinalIgnoreCase)) return 5;
        if (n.Contains("Powerword", StringComparison.OrdinalIgnoreCase)) return 6;
        if (n.Equals("Lacerate", StringComparison.OrdinalIgnoreCase)) return 7;
        if (n.Equals("Smite", StringComparison.OrdinalIgnoreCase)) return 8;
        if (n.Equals("Valiance", StringComparison.OrdinalIgnoreCase) || n.Equals("Hero's Valiance", StringComparison.OrdinalIgnoreCase)) return 9;
        if (n.Contains("Arcana", StringComparison.OrdinalIgnoreCase) && n.Contains("Concerto", StringComparison.OrdinalIgnoreCase)) return 10;
        if (n.Equals("Acheron", StringComparison.OrdinalIgnoreCase) || n.Equals("Stygian", StringComparison.OrdinalIgnoreCase)) return 11;
        if (n.Equals("Elysium", StringComparison.OrdinalIgnoreCase)) return 12;
        if (n.Equals("Praxis", StringComparison.OrdinalIgnoreCase)) return 13;
        if (n.Equals("Dauntless", StringComparison.OrdinalIgnoreCase) || n.Equals("Malice", StringComparison.OrdinalIgnoreCase)) return 14;
        if (n.Equals("Ravenous", StringComparison.OrdinalIgnoreCase) || n.Equals("Famine", StringComparison.OrdinalIgnoreCase)) return 15;

        if (equippedProcId > 0)
        {
            var expected = GetProcName(equippedProcId);
            if (!string.IsNullOrEmpty(expected) && n.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return equippedProcId;
        }
        return 0;
    }
}
