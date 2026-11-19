using System.Globalization;
using Skua.Core.Models.Items;
using Skua.Core.Models.Players;
using Skua.Core.Models.Skills;
using SkuaSim.Collector.Models;
using SkuaSim.Core.Data;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.World;

public enum AqwStatModel
{
    PowerMelee,
    TankMelee,
    DodgeMelee,
    OffensiveCaster,
    PowerCaster,
    DefensiveCaster,
    FullHybrid,
    LuckHybrid
}

public enum ClassDamageKind
{
    Physical,
    Magic,
    Hybrid
}

public sealed record ClassArchetype(
    AqwStatModel StatModel,
    ClassDamageKind DamageKind,
    double BaseDpsMultiplier,
    double SelfHealCoefficient,
    double PassiveCritBonus,
    double PassiveCritMultBonus,
    double PassiveHasteBonus,
    double PassiveDodgeBonus,
    double DamageReduction);

public sealed class GearBoostProfile
{
    public double DamageAllMultiplier { get; init; } = 1.0;
    public double XpMultiplier { get; init; } = 1.0;
    public double ClassPointsMultiplier { get; init; } = 1.0;
    public double GoldMultiplier { get; init; } = 1.0;
    public double RepMultiplier { get; init; } = 1.0;
    public IReadOnlyDictionary<string, double> RaceDamageMultipliers { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public double GetRaceMultiplier(string? race)
    {
        if (string.IsNullOrWhiteSpace(race))
            return 1.0;
        return RaceDamageMultipliers.TryGetValue(race.Trim(), out var mult) ? Math.Max(1.0, mult) : 1.0;
    }
}

public sealed class ScenarioStatsPreview
{
    public int ComputedMaxHealth { get; init; }
    public double EffectiveDps { get; init; }
    public double CriticalChance { get; init; }
    public double CriticalMultiplier { get; init; } = 1.5;
    public double Haste { get; init; }
    public double EvasionChance { get; init; }
    public string StatModel { get; init; } = "";
    public bool UsesCapturedClassData { get; init; }
    public int CapturedSkillCount { get; init; }
    public long CapturedSamples { get; init; }
    public GearBoostProfile GearBoosts { get; init; } = new();
}

public sealed class ComputedCombatProfile
{
    public required PlayerStats Stats { get; init; }
    public required int ComputedMaxHealth { get; init; }
    public required double EffectiveDps { get; init; }
    public required double CombatHealPerSecond { get; init; }
    public required double LifestealRatio { get; init; }
    public required double DamageReduction { get; init; }
    public required double IncomingDamageMultiplier { get; init; }
    public required AqwStatModel StatModel { get; init; }
    public required ClassDamageKind DamageKind { get; init; }
    public required IReadOnlyList<SkillInfo> Skills { get; init; }
    public string EnhancementSummary { get; init; } = string.Empty;
    public GearBoostProfile GearBoosts { get; init; } = new();
    public bool UsesCapturedClassData { get; init; }
    public int CapturedSkillCount { get; init; }
    public long CapturedSamples { get; init; }
}

/// <summary>
/// Implements AQW stat progression, enhancement pattern distributions (Standard, Awe, Forge),
/// class archetype scaling, dodge/crit/haste formulas, and skill/combat calibration from Collector data.
/// </summary>
public static class StatEngine
{
    // AQW enhancement stat weights per equipment slot
    private const double WeaponSlotWeight = 0.33;
    private const double ClassArmorSlotWeight = 0.25;
    private const double HelmSlotWeight = 0.25;
    private const double CapeSlotWeight = 0.17;

    /// <summary>AQW Wiki level stat pool: 15 + 747 * (Level - 1) / 99 (15 at Lv 1 -> 762 at Lv 100).</summary>
    public static double StatPoolForLevel(int level)
    {
        var lvl = Math.Clamp(level, 1, 100);
        return 15.0 + 747.0 * (lvl - 1) / 99.0;
    }

    /// <summary>AQW Wiki base HP before Endurance: 1640 * ((Level - 1) / 99)^0.66 + 360 (360 at Lv 1 -> 2,000 at Lv 100).</summary>
    public static double BaseHealthForLevel(int level)
    {
        var lvl = Math.Clamp(level, 1, 100);
        var ratio = (lvl - 1) / 99.0;
        return 1640.0 * Math.Pow(ratio, 0.66) + 360.0;
    }

    public static ComputedCombatProfile ComputeProfile(
        int level,
        string? className,
        int classRank,
        IEnumerable<InventoryItem> equippedItems,
        GameDataCatalog catalog)
    {
        var lvl = Math.Clamp(level, 1, 100);
        var rank = Math.Clamp(classRank, 1, 10);
        var normalizedClass = string.IsNullOrWhiteSpace(className) ? "Warrior" : className.Trim();

        var capturedClass = catalog.FindClassProfile(normalizedClass);
        var archetype = ResolveArchetype(normalizedClass, capturedClass);

        InventoryItem? weapon = null, clsItem = null, helm = null, cape = null;
        foreach (var item in equippedItems)
        {
            if (item == null || !item.Equipped)
                continue;
            var group = SimWorld.EquipGroup(item.CategoryString);
            switch (group)
            {
                case "weapon": weapon ??= item; break;
                case "class": clsItem ??= item; break;
                case "helm": helm ??= item; break;
                case "cape": cape ??= item; break;
            }
        }

        // 1. Class intrinsic stat pool by player level
        var pool = StatPoolForLevel(lvl);
        var baseWeights = GetClassModelStatWeights(archetype.StatModel);
        double str = pool * baseWeights.Str;
        double intl = pool * baseWeights.Int;
        double end = pool * baseWeights.End;
        double dex = pool * baseWeights.Dex;
        double wis = pool * baseWeights.Wis;
        double lck = pool * baseWeights.Lck;

        // 2. Enhancement contributions from Weapon, Class/Armor, Helm, Cape
        double maxHpMultiplier = 1.0;
        double healMultiplier = 1.0;
        double forgeOutDamageMultiplier = 1.0;
        double incomingDamageMultiplier = 1.0;
        double bonusCrit = 0.0;
        double bonusCritMult = 0.0;
        double bonusHaste = 0.0;
        double bonusDodge = 0.0;
        double forgeDrBonus = 0.0;
        double procDpsFactor = 1.0;
        double lifestealRatio = 0.0;

        void ApplyEnhancementSlot(InventoryItem? gear, double slotWeight)
        {
            if (gear == null || gear.EnhancementLevel <= 0 || gear.EnhancementPatternID <= 0)
                return;

            var enhLvl = Math.Clamp(gear.EnhancementLevel, 1, lvl);
            var enhPool = StatPoolForLevel(enhLvl) * slotWeight;
            var w = GetEnhancementPatternWeights(gear.EnhancementPatternID);

            str += enhPool * w.Str;
            intl += enhPool * w.Int;
            end += enhPool * w.End;
            dex += enhPool * w.Dex;
            wis += enhPool * w.Wis;
            lck += enhPool * w.Lck;

            // Special Forge helm/cape modifiers matching AQW mechanics
            switch (gear.EnhancementPatternID)
            {
                case 25: // Vim Helm
                    bonusHaste += 0.10;
                    bonusDodge += 0.06;
                    maxHpMultiplier *= 0.85;
                    break;
                case 26: // Examen Helm
                    bonusCrit += 0.06;
                    bonusHaste += 0.06;
                    maxHpMultiplier *= 0.90;
                    break;
                case 27: // Pneuma Helm
                    maxHpMultiplier *= 0.75;
                    break;
                case 28: // Anima Helm
                    maxHpMultiplier *= 0.75;
                    break;
                case 32: // Hearty Helm
                    maxHpMultiplier *= 1.50;
                    break;
                case 11: // Absolution Cape
                    healMultiplier *= 1.50;
                    incomingDamageMultiplier *= 1.15;
                    break;
                case 12: // Avarice Cape
                    bonusHaste += 0.10;
                    incomingDamageMultiplier *= 1.15;
                    break;
                case 24: // Vainglory Cape
                    forgeOutDamageMultiplier *= 1.15;
                    healMultiplier *= 0.50;
                    break;
                case 29: // Penitence Cape
                    forgeDrBonus += 0.25;
                    bonusCritMult -= 0.15;
                    break;
                case 30: // Lament Cape
                    bonusCrit += 0.20;
                    bonusHaste -= 0.05;
                    break;
            }
        }

        ApplyEnhancementSlot(weapon, WeaponSlotWeight);
        ApplyEnhancementSlot(clsItem, ClassArmorSlotWeight);
        ApplyEnhancementSlot(helm, HelmSlotWeight);
        ApplyEnhancementSlot(cape, CapeSlotWeight);

        // Apply Awe / Forge Weapon Proc specials
        if (weapon is { ProcID: > 0 })
        {
            switch (weapon.ProcID)
            {
                case 2: // Spiral Carve
                    bonusCrit += 0.08;
                    procDpsFactor *= 1.12;
                    break;
                case 3: // Awe Blast
                    procDpsFactor *= 1.12;
                    break;
                case 4: // Health Vamp
                    procDpsFactor *= 1.05;
                    lifestealRatio += 0.14;
                    break;
                case 5: // Mana Vamp
                    procDpsFactor *= 1.08;
                    break;
                case 7: // Lacerate
                    procDpsFactor *= 1.10;
                    bonusDodge += 0.05;
                    break;
                case 8: // Smite
                    procDpsFactor *= 1.14;
                    break;
                case 9: // Valiance (+20% core stats proc uptime)
                    str *= 1.20;
                    intl *= 1.20;
                    end *= 1.15;
                    dex *= 1.20;
                    wis *= 1.20;
                    lck *= 1.20;
                    procDpsFactor *= 1.15;
                    break;
                case 10: // Arcana's Concerto
                    procDpsFactor *= 1.22;
                    break;
                case 11: // Acheron
                    procDpsFactor *= 1.24;
                    break;
                case 12: // Elysium
                    procDpsFactor *= 1.25;
                    lifestealRatio += 0.08;
                    break;
                case 13: // Praxis
                    procDpsFactor *= 1.22;
                    break;
                case 14: // Dauntless
                    procDpsFactor *= 1.30;
                    bonusHaste += 0.25;
                    bonusCrit += 0.20;
                    lifestealRatio += 0.16;
                    break;
                case 15: // Ravenous
                    procDpsFactor *= 1.35;
                    break;
            }
        }

        // Round primary stats (minimum 1)
        var iStr = Math.Max(1, (int)Math.Round(str));
        var iInt = Math.Max(1, (int)Math.Round(intl));
        var iEnd = Math.Max(1, (int)Math.Round(end));
        var iDex = Math.Max(1, (int)Math.Round(dex));
        var iWis = Math.Max(1, (int)Math.Round(wis));
        var iLck = Math.Max(1, (int)Math.Round(lck));

        // 3. Secondary Stats derived via AQW Stat Model efficiency scaling
        var normDenom = Math.Max(15.0, pool);
        var rStr = iStr / normDenom;
        var rInt = iInt / normDenom;
        var rDex = iDex / normDenom;
        var rWis = iWis / normDenom;
        var rLck = iLck / normDenom;

        var ap = (int)Math.Round(iStr * (archetype.DamageKind == ClassDamageKind.Magic ? 1.0 : 2.0) + iLck * 0.5 + 8 + lvl * 1.5);
        var sp = (int)Math.Round(iInt * (archetype.DamageKind == ClassDamageKind.Physical ? 1.0 : 2.0) + iLck * 0.5 + 8 + lvl * 1.5);

        // Haste: hard capped at 50% in AQW
        double statHaste = archetype.DamageKind == ClassDamageKind.Magic
            ? rInt * 0.14 + rDex * 0.06 + rLck * 0.04
            : rDex * 0.14 + rInt * 0.05 + rLck * 0.04;
        var haste = (float)Math.Clamp(statHaste + archetype.PassiveHasteBonus + bonusHaste, 0.0, 0.50);

        // Crit Chance
        double statCrit = 0.05 + (archetype.DamageKind == ClassDamageKind.Magic
            ? rWis * 0.12 + rLck * 0.18 + rInt * 0.04
            : rStr * 0.10 + rLck * 0.18 + rDex * 0.04);
        var critChance = (float)Math.Clamp(statCrit + archetype.PassiveCritBonus + bonusCrit, 0.01, 0.95);

        // Crit Multiplier
        double statCritMult = 1.50 + rLck * 1.15;
        var critMult = (float)Math.Clamp(statCritMult + archetype.PassiveCritMultBonus + bonusCritMult, 1.25, 4.50);

        // Evasion/Dodge Chance
        double statDodge = 0.04 + rDex * 0.14 + rWis * 0.08 + rLck * 0.03;
        var evasion = (float)Math.Clamp(statDodge + archetype.PassiveDodgeBonus + bonusDodge, 0.01, 0.90);

        // Blend exact observed Collector dodge/crit rates slightly if captured data exists for this class
        if (capturedClass?.Combat is { } obs)
        {
            if (obs.AttacksTaken >= 20)
                evasion = (float)Math.Clamp(evasion * 0.6 + obs.ObservedDodgeRate * 0.4, 0.01, 0.90);
            if (obs.Hits + obs.Crits >= 20)
                critChance = (float)Math.Clamp(critChance * 0.65 + obs.ObservedCritRate * 0.35, 0.01, 0.95);
        }

        // Max Health from AQW level formula + END * 5
        var computedMaxHp = Math.Max(100, (int)Math.Round((BaseHealthForLevel(lvl) + iEnd * 5.0) * maxHpMultiplier));

        // 4. Primary Offensive Power -> Effective DPS
        double primaryPower = archetype.DamageKind switch
        {
            ClassDamageKind.Physical => ap,
            ClassDamageKind.Magic => sp,
            _ => (ap + sp) * 0.65
        };

        // Rank factor: Rank 1 only has skills 1-2 (~0.68x kit potential); Rank 5 unlocks all passives & ultimate skill (~1.0x)
        var rankScale = rank switch
        {
            1 => 0.68,
            2 => 0.78,
            3 => 0.85,
            4 => 0.93,
            _ => 1.00 + (rank - 5) * 0.01
        };

        var critExpectedMult = 1.0 + critChance * Math.Max(0.0, critMult - 1.0);
        var hasteAttackMult = 1.0 / Math.Max(0.50, 1.0 - haste);

        // Weapon enhancement level penalty if severely under-enhanced vs player level (real AQW early-game feel)
        var enhPenalty = 1.0;
        if (lvl >= 5)
        {
            var avgEnhLvl = new[] { weapon?.EnhancementLevel ?? 0, clsItem?.EnhancementLevel ?? 0 }.Average();
            if (avgEnhLvl <= 0)
                enhPenalty = 0.72; // Totally unenhanced gear past Lv 5 noticeably weakens output
            else if (avgEnhLvl < lvl * 0.6)
                enhPenalty = 0.85;
        }

        // Compute equipped gear boost multipliers (AQW non-stacking rule: highest boost per category wins)
        var gearBoosts = EvaluateGearBoosts(equippedItems);

        // Base weapon-per-second damage scales roughly linearly with AQW PrimaryPower * 0.75 * class multiplier
        var baseAutoSkillDps = Math.Max(8.0, (primaryPower * 0.75 + 10.0) * archetype.BaseDpsMultiplier * rankScale);
        var effectiveDps = baseAutoSkillDps * critExpectedMult * hasteAttackMult * procDpsFactor * forgeOutDamageMultiplier * enhPenalty * gearBoosts.DamageAllMultiplier;

        // If Collector captured real class combat damage for this class, gently calibrate with level-normalized observed DPS
        if (capturedClass?.Combat is { } clsCombat && clsCombat.Hits + clsCombat.Crits >= 10 && clsCombat.AverageDamagePerHit > 0)
        {
            var obsPool = StatPoolForLevel((int)Math.Clamp(Math.Round(clsCombat.AverageDamageSampleLevel), 1, 100));
            var levelScale = pool / Math.Max(15.0, obsPool);
            var obsDpsEstimate = clsCombat.AverageDamagePerHit * 0.8 * levelScale * gearBoosts.DamageAllMultiplier;
            effectiveDps = Math.Clamp(effectiveDps * 0.7 + obsDpsEstimate * 0.3, effectiveDps * 0.4, effectiveDps * 1.8);
        }

        // 5. Combat Healing & Sustainment
        // Rank 1-2 starter classes often haven't unlocked all utility skills unless Rank >= 2
        var healUnlockFactor = rank >= 2 ? 1.0 : 0.65;
        var combatHealPerSec = archetype.SelfHealCoefficient > 0
            ? Math.Max(0.0, (sp * 0.55 + ap * 0.15 + computedMaxHp * 0.015) * archetype.SelfHealCoefficient * healMultiplier * hasteAttackMult * healUnlockFactor)
            : 0.0;

        if (capturedClass?.Combat is { } healObs && healObs.HealEvents >= 5 && healObs.AverageHealPerEvent > 0 && combatHealPerSec > 0)
        {
            var obsPool = StatPoolForLevel((int)Math.Clamp(Math.Round(healObs.AverageHealSampleLevel), 1, 100));
            var obsHealEstimate = (healObs.AverageHealPerEvent / 4.0) * (pool / Math.Max(15.0, obsPool)) * healMultiplier;
            combatHealPerSec = combatHealPerSec * 0.6 + obsHealEstimate * 0.4;
        }

        var totalDr = Math.Clamp(archetype.DamageReduction + forgeDrBonus, 0.0, 0.75);
        var skills = BuildSkills(normalizedClass, rank, archetype, capturedClass);
        var enhSig = $"W{weapon?.EnhancementPatternID ?? 0}:{weapon?.EnhancementLevel ?? 0}|C{clsItem?.EnhancementPatternID ?? 0}:{clsItem?.EnhancementLevel ?? 0}|H{helm?.EnhancementPatternID ?? 0}:{helm?.EnhancementLevel ?? 0}|K{cape?.EnhancementPatternID ?? 0}:{cape?.EnhancementLevel ?? 0}";

        var stats = new PlayerStats
        {
            Strength = iStr,
            Intellect = iInt,
            Endurance = iEnd,
            Dexterity = iDex,
            Wisdom = iWis,
            Luck = iLck,
            AttackPower = ap,
            SpellPower = sp,
            Haste = haste,
            CriticalChance = critChance,
            CriticalMultiplier = critMult,
            EvasionChance = evasion
        };

        return new ComputedCombatProfile
        {
            Stats = stats,
            ComputedMaxHealth = computedMaxHp,
            EffectiveDps = Math.Max(1.0, effectiveDps),
            CombatHealPerSecond = combatHealPerSec,
            LifestealRatio = lifestealRatio,
            DamageReduction = totalDr,
            IncomingDamageMultiplier = incomingDamageMultiplier,
            StatModel = archetype.StatModel,
            DamageKind = archetype.DamageKind,
            Skills = skills,
            EnhancementSummary = enhSig,
            GearBoosts = gearBoosts,
            UsesCapturedClassData = capturedClass != null,
            CapturedSkillCount = capturedClass?.Skills.Count ?? 0,
            CapturedSamples = capturedClass?.SamplesObserved ?? 0
        };
    }

    public static ClassArchetype ResolveArchetype(string className, CapturedClassProfile? captured)
    {
        var n = className.Trim().ToLowerInvariant();

        // Exact/canonical AQW class families
        if (n.Contains("yami no ronin"))
            return new(AqwStatModel.DodgeMelee, ClassDamageKind.Physical, 2.55, 0.06, 0.10, 0.25, 0.20, 0.55, 0.10);
        if (n.Contains("chrono assassin") || n.Contains("horc evader") || n.Contains("great thief"))
            return new(AqwStatModel.DodgeMelee, ClassDamageKind.Physical, 2.00, 0.08, 0.12, 0.20, 0.25, 0.48, 0.10);
        if (n.Contains("void highlord"))
            return new(AqwStatModel.TankMelee, ClassDamageKind.Physical, 3.20, 0.35, 0.18, 0.45, 0.25, 0.08, 0.45);
        if (n.Contains("chaos avenger") || n.Contains("verus doomknight"))
            return new(AqwStatModel.TankMelee, ClassDamageKind.Physical, 3.10, 0.30, 0.16, 0.40, 0.20, 0.05, 0.40);
        if (n.Contains("legion revenant"))
            return new(AqwStatModel.OffensiveCaster, ClassDamageKind.Magic, 2.95, 0.32, 0.15, 0.35, 0.25, 0.12, 0.30);
        if (n.Contains("archmage"))
            return new(AqwStatModel.OffensiveCaster, ClassDamageKind.Magic, 3.30, 0.22, 0.15, 0.40, 0.25, 0.12, 0.25);
        if (n.Contains("archpaladin") || n.Contains("paladin"))
            return new(AqwStatModel.TankMelee, ClassDamageKind.Hybrid, 1.95, 0.48, 0.12, 0.20, 0.12, 0.06, 0.42);
        if (n.Contains("lord of order"))
            return new(AqwStatModel.DefensiveCaster, ClassDamageKind.Hybrid, 1.65, 0.50, 0.15, 0.25, 0.20, 0.10, 0.35);
        if (n.Contains("stonecrusher"))
            return new(AqwStatModel.PowerCaster, ClassDamageKind.Magic, 2.15, 0.40, 0.18, 0.35, 0.22, 0.08, 0.25);
        if (n.Contains("lightcaster"))
            return new(AqwStatModel.PowerCaster, ClassDamageKind.Magic, 2.65, 0.35, 0.15, 0.25, 0.22, 0.10, 0.20);
        if (n.Contains("blaze binder") || n.Contains("pyromancer") || n.Contains("shaman"))
            return new(AqwStatModel.OffensiveCaster, ClassDamageKind.Magic, 1.80, 0.16, 0.10, 0.15, 0.12, 0.06, 0.15);
        if (n.Contains("scarlet sorceress") || n.Contains("blood sorceress") || n.Contains("vampire lord"))
            return new(AqwStatModel.FullHybrid, ClassDamageKind.Magic, 1.85, 0.32, 0.10, 0.20, 0.15, 0.08, 0.18);
        if (n.Contains("archfiend") || n.Contains("master ranger") || n.Contains("eternal inversionist"))
            return new(AqwStatModel.LuckHybrid, ClassDamageKind.Hybrid, 2.05, 0.14, 0.15, 0.30, 0.18, 0.12, 0.12);
        if (n.Contains("oracle"))
            return new(AqwStatModel.DefensiveCaster, ClassDamageKind.Magic, 1.05, 0.42, 0.08, 0.10, 0.08, 0.12, 0.25);
        if (n.Contains("healer") || n.Contains("acolyte") || n.Contains("bard"))
            return new(AqwStatModel.DefensiveCaster, ClassDamageKind.Magic, 0.90, 0.48, 0.05, 0.05, 0.06, 0.05, 0.15);
        if (n.Contains("rogue") || n.Contains("ninja") || n.Contains("pirate") || n.Contains("assassin") || n.Contains("swordmaster"))
            return new(AqwStatModel.DodgeMelee, ClassDamageKind.Physical, 1.05, 0.00, 0.08, 0.15, 0.15, 0.26, 0.05);
        if (n.Contains("mage") || n.Contains("sorcerer") || n.Contains("dark caster") || n.Contains("necromancer"))
            return new(AqwStatModel.OffensiveCaster, ClassDamageKind.Magic, 1.00, n.Contains("necromancer") ? 0.22 : 0.00, 0.05, 0.10, 0.06, 0.05, 0.10);
        if (n.Contains("warrior") || n.Contains("knight") || n.Contains("berserker") || n.Contains("dragonslayer"))
            return new(AqwStatModel.PowerMelee, ClassDamageKind.Physical, 1.00, 0.05, 0.06, 0.10, 0.06, 0.05, 0.18);
        if (n.Contains("no class"))
            return new(AqwStatModel.FullHybrid, ClassDamageKind.Physical, 0.55, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00);

        // If unknown class name, infer from Collector's captured stat model / skill hints
        if (captured != null)
        {
            var hasHeal = captured.Skills.Any(s => s.IsHealHint) || captured.Combat.HealEvents > 0;
            var model = captured.InferredStatModel switch
            {
                "Offensive Caster" => AqwStatModel.OffensiveCaster,
                "Power Caster" => AqwStatModel.PowerCaster,
                "Defensive Caster" => AqwStatModel.DefensiveCaster,
                "Dodge Melee" => AqwStatModel.DodgeMelee,
                "Tank Melee" => AqwStatModel.TankMelee,
                "Power Melee" => AqwStatModel.PowerMelee,
                "Luck Hybrid" => AqwStatModel.LuckHybrid,
                _ => AqwStatModel.FullHybrid
            };
            var kind = model is AqwStatModel.OffensiveCaster or AqwStatModel.PowerCaster or AqwStatModel.DefensiveCaster
                ? ClassDamageKind.Magic
                : model is AqwStatModel.FullHybrid or AqwStatModel.LuckHybrid
                    ? ClassDamageKind.Hybrid
                    : ClassDamageKind.Physical;
            return new(model, kind, 1.25, hasHeal ? 0.25 : 0.05, 0.06, 0.15, 0.08, model == AqwStatModel.DodgeMelee ? 0.28 : 0.06, 0.12);
        }

        return new(AqwStatModel.PowerMelee, ClassDamageKind.Physical, 1.15, 0.05, 0.05, 0.10, 0.05, 0.05, 0.10);
    }

    private static List<SkillInfo> BuildSkills(
        string className,
        int rank,
        ClassArchetype archetype,
        CapturedClassProfile? captured)
    {
        if (captured is { Skills.Count: > 0 })
        {
            var list = new List<SkillInfo>(6);
            for (var i = 0; i < 6; i++)
            {
                var c = captured.Skills.FirstOrDefault(s => s.Slot == i);
                if (c != null)
                {
                    list.Add(new SkillInfo
                    {
                        ID = c.SkillID > 0 ? c.SkillID : i + 1,
                        ActID = c.ActID > 0 ? c.ActID : i + 1,
                        Name = c.Name,
                        Description = c.Description,
                        Icon = c.Icon,
                        Type = c.Type,
                        Damage = c.Damage,
                        Cooldown = c.CooldownMs > 0 ? c.CooldownMs : (i == 0 ? 2000 : 3000 + i * 1500),
                        MP = c.MpCost,
                        Range = c.Range,
                        MinTargets = c.MinTargets,
                        MaxTargets = c.MaxTargets,
                        Auto = c.Auto || i == 0,
                        IsOk = i <= 2 || (i == 3 && rank >= 2) || (i == 4 && rank >= 3) || (i == 5 && rank >= 5),
                        Locked = !((i <= 2) || (i == 3 && rank >= 2) || (i == 4 && rank >= 3) || (i == 5 && rank >= 5))
                    });
                }
                else
                {
                    list.Add(DefaultSkillSlot(i, className, rank, archetype));
                }
            }
            return list;
        }

        return Enumerable.Range(0, 6)
            .Select(i => DefaultSkillSlot(i, className, rank, archetype))
            .ToList();
    }

    private static SkillInfo DefaultSkillSlot(int slot, string className, int rank, ClassArchetype archetype)
    {
        var unlocked = slot switch
        {
            <= 2 => true,
            3 => rank >= 2,
            4 => rank >= 3,
            _ => rank >= 5
        };

        var typ = archetype.DamageKind == ClassDamageKind.Magic ? "m" : "p";
        var (name, dmg, cd, mp) = slot switch
        {
            0 => ("Auto attack", 1.0, 2000, 0),
            1 => ($"{className} Strike", 1.4, 3000, 10),
            2 => (archetype.SelfHealCoefficient >= 0.30 ? "Mend / Sustain" : $"{className} Blast", archetype.SelfHealCoefficient >= 0.30 ? -1.0 : 1.35, 6000, 15),
            3 => ($"{className} Focus", 1.6, 8000, 20),
            4 => ($"{className} Surge", 2.2, 12000, 30),
            _ => ("Potion", 0.0, 30000, 0)
        };

        return new SkillInfo
        {
            ID = slot + 1,
            ActID = slot + 1,
            Name = name,
            Description = "",
            Auto = slot == 0,
            Type = slot == 2 && archetype.SelfHealCoefficient >= 0.30 ? "h" : typ,
            Damage = dmg,
            Cooldown = cd,
            MP = mp,
            Range = archetype.DamageKind == ClassDamageKind.Magic ? 800 : 300,
            MinTargets = 1,
            MaxTargets = archetype.DamageKind == ClassDamageKind.Magic ? 3 : 1,
            IsOk = unlocked,
            Locked = !unlocked
        };
    }

    private readonly record struct StatWeights(double Str, double Int, double End, double Dex, double Wis, double Lck);

    private static StatWeights GetClassModelStatWeights(AqwStatModel model) => model switch
    {
        AqwStatModel.PowerMelee => new(0.32, 0.05, 0.25, 0.18, 0.05, 0.15),
        AqwStatModel.TankMelee => new(0.28, 0.08, 0.34, 0.12, 0.08, 0.10),
        AqwStatModel.DodgeMelee => new(0.22, 0.05, 0.14, 0.36, 0.08, 0.15),
        AqwStatModel.OffensiveCaster => new(0.05, 0.38, 0.14, 0.08, 0.22, 0.13),
        AqwStatModel.PowerCaster => new(0.06, 0.34, 0.18, 0.10, 0.18, 0.14),
        AqwStatModel.DefensiveCaster => new(0.06, 0.28, 0.28, 0.08, 0.20, 0.10),
        AqwStatModel.LuckHybrid => new(0.16, 0.16, 0.14, 0.14, 0.12, 0.28),
        _ => new(0.18, 0.18, 0.20, 0.15, 0.15, 0.14) // FullHybrid
    };

    private static StatWeights GetEnhancementPatternWeights(int patternId) => patternId switch
    {
        2 => new(0.44, 0.00, 0.36, 0.20, 0.00, 0.00), // Fighter
        3 => new(0.34, 0.00, 0.22, 0.44, 0.00, 0.00), // Thief
        5 => new(0.23, 0.23, 0.22, 0.16, 0.16, 0.00), // Hybrid
        6 => new(0.00, 0.46, 0.24, 0.00, 0.30, 0.00), // Wizard
        7 => new(0.00, 0.38, 0.36, 0.00, 0.26, 0.00), // Healer
        8 => new(0.00, 0.36, 0.18, 0.20, 0.00, 0.26), // SpellBreaker
        9 => new(0.14, 0.12, 0.10, 0.10, 0.00, 0.54), // Lucky
        10 => new(0.28, 0.28, 0.14, 0.15, 0.05, 0.10), // Forge base
        11 => new(0.00, 0.40, 0.10, 0.10, 0.25, 0.15), // Absolution Cape
        12 => new(0.15, 0.25, 0.05, 0.25, 0.15, 0.15), // Avarice Cape
        24 => new(0.25, 0.25, 0.15, 0.15, 0.10, 0.10), // Vainglory Cape
        25 => new(0.25, 0.00, 0.00, 0.55, 0.00, 0.20), // Vim Helm
        26 => new(0.00, 0.45, 0.00, 0.15, 0.25, 0.15), // Examen Helm
        27 => new(0.00, 0.65, 0.00, 0.00, 0.25, 0.10), // Pneuma Helm
        28 => new(0.65, 0.00, 0.00, 0.15, 0.00, 0.20), // Anima Helm
        29 => new(0.20, 0.15, 0.40, 0.10, 0.10, 0.05), // Penitence Cape
        30 => new(0.10, 0.10, 0.10, 0.15, 0.15, 0.40), // Lament Cape
        32 => new(0.15, 0.15, 0.55, 0.05, 0.05, 0.05), // Hearty Helm
        _ => new(0.18, 0.18, 0.20, 0.15, 0.15, 0.14)
    };

    private static readonly string[] AllAqwRaces = { "Undead", "Chaos", "Dragonkin", "Human", "Elemental", "Drakath", "Orc" };

    /// <summary>
    /// Converts a user-friendly scenario slot boost into the exact AQW item Meta string
    /// (compatible with CoreBots.GetBoostFloat / CoreAdvanced.HasMinimalBoost).
    /// </summary>
    public static string BuildBoostMeta(string? boostType, int percent)
    {
        if (string.IsNullOrWhiteSpace(boostType) ||
            boostType.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            percent <= 0)
        {
            return string.Empty;
        }

        var factor = (1.0 + percent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        var key = boostType.Trim();

        if (key.Equals("AllResources", StringComparison.OrdinalIgnoreCase))
            return $"exp:{factor},cp:{factor},gold:{factor},rep:{factor}";

        if (key.Equals("AllRaces", StringComparison.OrdinalIgnoreCase))
            return string.Join(",", AllAqwRaces.Select(r => $"{r}:{factor}"));

        return $"{key}:{factor}";
    }

    /// <summary>
    /// Parses AQW item Meta tags across all equipped items using AQW's non-stacking rule
    /// (highest boost per boost type wins; dmgAll multiplies with matching race boost).
    /// </summary>
    public static GearBoostProfile EvaluateGearBoosts(IEnumerable<InventoryItem> equippedItems)
    {
        double dmgAll = 1.0, exp = 1.0, cp = 1.0, gold = 1.0, rep = 1.0;
        var raceMults = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in equippedItems)
        {
            if (item == null || !item.Equipped || string.IsNullOrWhiteSpace(item.Meta))
                continue;

            var parts = item.Meta.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var sep = part.IndexOf(':');
                if (sep <= 0 || sep >= part.Length - 1)
                    continue;

                var tag = part[..sep].Trim();
                if (!double.TryParse(part[(sep + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val) || val <= 1.0)
                    continue;

                if (tag.Equals("dmgAll", StringComparison.OrdinalIgnoreCase))
                    dmgAll = Math.Max(dmgAll, val);
                else if (tag.Equals("exp", StringComparison.OrdinalIgnoreCase))
                    exp = Math.Max(exp, val);
                else if (tag.Equals("cp", StringComparison.OrdinalIgnoreCase))
                    cp = Math.Max(cp, val);
                else if (tag.Equals("gold", StringComparison.OrdinalIgnoreCase))
                    gold = Math.Max(gold, val);
                else if (tag.Equals("rep", StringComparison.OrdinalIgnoreCase))
                    rep = Math.Max(rep, val);
                else if (AllAqwRaces.Any(r => r.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                    raceMults[tag] = Math.Max(raceMults.GetValueOrDefault(tag, 1.0), val);
            }
        }

        return new GearBoostProfile
        {
            DamageAllMultiplier = dmgAll,
            XpMultiplier = exp,
            ClassPointsMultiplier = cp,
            GoldMultiplier = gold,
            RepMultiplier = rep,
            RaceDamageMultipliers = raceMults
        };
    }

    /// <summary>
    /// Selects sensible default starting enhancement patterns &amp; weapon proc from the scenario's
    /// unlocked Awe/Forge checklists for a given class archetype so starting stats match real AQW loadouts.
    /// </summary>
    public static (int BasePattern, int WeaponPattern, int WeaponProcId, int HelmPattern, int CapePattern)
        PickStartingEnhancementsForClass(string? className, GameDataCatalog catalog, EnhancementsSetup? enhSetup)
    {
        var archetype = ResolveArchetype(string.IsNullOrWhiteSpace(className) ? "Warrior" : className, catalog.FindClassProfile(className));
        var isCaster = archetype.DamageKind == ClassDamageKind.Magic ||
                       archetype.StatModel is AqwStatModel.OffensiveCaster or AqwStatModel.PowerCaster or AqwStatModel.DefensiveCaster;
        var basePat = isCaster ? 6 : 9; // Wizard for casters, Lucky for melee/hybrid

        if (enhSetup == null || (enhSetup.UnlockedAwe.Count == 0 && enhSetup.UnlockedForge.Count == 0))
            return (basePat, basePat, 0, basePat, basePat);

        var unlockedForge = new HashSet<string>(enhSetup.UnlockedForge, StringComparer.OrdinalIgnoreCase);
        var unlockedAwe = new HashSet<string>(enhSetup.UnlockedAwe, StringComparer.OrdinalIgnoreCase);

        // Best weapon proc (Forge > Awe)
        int weaponPat = basePat;
        int procId = 0;
        string[] weaponPriority = isCaster
            ? new[] { "Elysium", "Valiance", "Arcana's Concerto", "Acheron", "Dauntless", "Ravenous", "Praxis", "Smite", "Lacerate", "Forge (Weapon)" }
            : new[] { "Ravenous", "Dauntless", "Valiance", "Praxis", "Arcana's Concerto", "Smite", "Lacerate", "Elysium", "Acheron", "Forge (Weapon)" };

        foreach (var wName in weaponPriority)
        {
            if (unlockedForge.Contains(wName) && EnhancementUnlockCatalog.Find(wName) is { } info)
            {
                weaponPat = info.PatternId;
                procId = info.ProcId;
                break;
            }
        }
        if (procId == 0)
        {
            foreach (var aName in new[] { "Spiral Carve", "Awe Blast", "Health Vamp", "Mana Vamp", "Powerword Die" })
            {
                if (unlockedAwe.Contains(aName) && EnhancementUnlockCatalog.Find(aName) is { } info)
                {
                    weaponPat = basePat;
                    procId = info.ProcId;
                    break;
                }
            }
        }

        // Best helm
        int helmPat = basePat;
        string[] helmPriority = isCaster
            ? new[] { "Pneuma", "Examen", "Forge (Helm)", "Vim", "Anima", "Hearty" }
            : archetype.StatModel == AqwStatModel.DodgeMelee
                ? new[] { "Vim", "Anima", "Forge (Helm)", "Examen", "Pneuma", "Hearty" }
                : new[] { "Anima", "Vim", "Forge (Helm)", "Hearty", "Examen", "Pneuma" };

        foreach (var hName in helmPriority)
        {
            if (unlockedForge.Contains(hName) && EnhancementUnlockCatalog.Find(hName) is { } info)
            {
                helmPat = info.PatternId;
                break;
            }
        }

        // Best cape
        int capePat = basePat;
        string[] capePriority = new[] { "Vainglory", "Lament", "Penitence", "Avarice", "Absolution", "Forge (Cape)" };
        foreach (var cName in capePriority)
        {
            if (unlockedForge.Contains(cName) && EnhancementUnlockCatalog.Find(cName) is { } info)
            {
                capePat = info.PatternId;
                break;
            }
        }

        return (basePat, weaponPat, procId, helmPat, capePat);
    }

    /// <summary>
    /// Builds a preview CombatProfile directly from a Scenario + GameDataCatalog for live UI display
    /// before starting a run (matches what SimWorld.InitializeFromScenario equips at t=0).
    /// </summary>
    public static ComputedCombatProfile PreviewScenarioProfile(Scenario scenario, GameDataCatalog catalog)
    {
        var level = Math.Clamp(scenario.Player.Level, 1, 100);
        var className = string.IsNullOrWhiteSpace(scenario.Player.Class) ? "Warrior" : scenario.Player.Class.Trim();
        var rank = Math.Clamp(scenario.Player.ClassRank, 1, 10);

        var hasEnhUnlocks = scenario.Enhancements.UnlockedAwe.Count > 0 || scenario.Enhancements.UnlockedForge.Count > 0;
        var hasGearBoosts = scenario.Boosts.EquipmentBoosts.Any(b => !string.IsNullOrEmpty(AqwBoostGearCatalog.ResolveMeta(b)));
        var (basePat, weapPat, procId, helmPat, capePat) = PickStartingEnhancementsForClass(className, catalog, scenario.Enhancements);

        var equipped = new List<InventoryItem>();
        // Include explicit equipped items from scenario.Inventory first
        foreach (var it in scenario.Inventory.Where(i => i.Equipped))
        {
            equipped.Add(new InventoryItem
            {
                Name = it.Name ?? "Item",
                CategoryString = string.IsNullOrWhiteSpace(it.Category) ? "Item" : it.Category,
                Equipped = true,
                EnhancementPatternID = it.EnhancementPatternID ?? 0,
                EnhancementLevel = it.EnhancementLevel ?? 0,
                ProcID = it.ProcID ?? 0
            });
        }

        var enhLvl = hasEnhUnlocks ? level : (level >= 10 && equipped.Count == 0 ? level : 0);

        void EnsureSlot(string slot, string category, int patId, int proc)
        {
            var group = SimWorld.EquipGroup(category);
            var existing = equipped.FirstOrDefault(i => SimWorld.EquipGroup(i.CategoryString) == group);
            var slotBoost = scenario.Boosts.EquipmentBoosts.FirstOrDefault(b => b.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
            var meta = slotBoost != null ? AqwBoostGearCatalog.ResolveMeta(slotBoost) : "";
            var resolvedName = slot == "Class"
                ? className
                : (slotBoost != null ? AqwBoostGearCatalog.ResolveItemName(slotBoost) : $"Simulated {slot}");
            var resolvedCat = slotBoost != null ? AqwBoostGearCatalog.ResolveCategory(slotBoost, category) : category;

            if (existing == null && (hasEnhUnlocks || !string.IsNullOrEmpty(meta) || slot == "Class"))
            {
                existing = new InventoryItem
                {
                    Name = resolvedName,
                    CategoryString = resolvedCat,
                    ItemGroup = slot == "Weapon" ? "Weapon" : "None",
                    Equipped = true,
                    EnhancementPatternID = enhLvl > 0 ? patId : 0,
                    EnhancementLevel = enhLvl,
                    ProcID = proc,
                    Meta = meta
                };
                equipped.Add(existing);
            }
            else if (existing != null && !string.IsNullOrEmpty(meta) && string.IsNullOrEmpty(existing.Meta))
            {
                existing.Meta = meta;
            }
        }

        EnsureSlot("Class", "Class", basePat, 0);
        if (hasEnhUnlocks || hasGearBoosts)
        {
            EnsureSlot("Weapon", "Sword", weapPat, procId);
            EnsureSlot("Armor", "Armor", 0, 0);
            EnsureSlot("Helm", "Helm", helmPat, 0);
            EnsureSlot("Cape", "Cape", capePat, 0);
            EnsureSlot("Pet", "Pet", 0, 0);
        }

        return ComputeProfile(level, className, rank, equipped, catalog);
    }

    public static ScenarioStatsPreview PreviewScenarioStats(Scenario scenario, GameDataCatalog catalog)
    {
        var profile = PreviewScenarioProfile(scenario, catalog);
        return new ScenarioStatsPreview
        {
            ComputedMaxHealth = profile.ComputedMaxHealth,
            EffectiveDps = profile.EffectiveDps,
            CriticalChance = profile.Stats.CriticalChance,
            CriticalMultiplier = profile.Stats.CriticalMultiplier,
            Haste = profile.Stats.Haste,
            EvasionChance = profile.Stats.EvasionChance,
            StatModel = profile.StatModel.ToString(),
            UsesCapturedClassData = profile.UsesCapturedClassData,
            CapturedSkillCount = profile.CapturedSkillCount,
            CapturedSamples = profile.CapturedSamples,
            GearBoosts = profile.GearBoosts
        };
    }
}
