using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Storage;

public sealed partial class GameDatabaseStore
{
    private static void NormalizeLoadedDatabase(GameDatabase db)
    {
        db.Classes = new Dictionary<string, CapturedClassProfile>(
            db.Classes ?? new Dictionary<string, CapturedClassProfile>(),
            StringComparer.OrdinalIgnoreCase);
        db.Enhancements ??= new Dictionary<int, CapturedEnhancementProfile>();
    }

    private static void MergeClassesAndEnhancements(GameDatabase target, GameDatabase source)
    {
        target.Classes ??= new Dictionary<string, CapturedClassProfile>(StringComparer.OrdinalIgnoreCase);
        target.Enhancements ??= new Dictionary<int, CapturedEnhancementProfile>();

        if (source.Classes != null)
        {
            foreach (var (className, srcCls) in source.Classes)
            {
                var key = string.IsNullOrWhiteSpace(srcCls.Name) ? className : srcCls.Name.Trim();
                if (string.IsNullOrEmpty(key))
                    continue;

                if (!target.Classes.TryGetValue(key, out var dstCls))
                {
                    dstCls = new CapturedClassProfile
                    {
                        Name = key,
                        FirstSeenUtc = srcCls.FirstSeenUtc
                    };
                    target.Classes[key] = dstCls;
                }

                dstCls.SamplesObserved += srcCls.SamplesObserved;
                if (!string.IsNullOrWhiteSpace(srcCls.InferredStatModel))
                    dstCls.InferredStatModel = srcCls.InferredStatModel;
                if (srcCls.FirstSeenUtc < dstCls.FirstSeenUtc)
                    dstCls.FirstSeenUtc = srcCls.FirstSeenUtc;
                if (srcCls.LastSeenUtc > dstCls.LastSeenUtc)
                    dstCls.LastSeenUtc = srcCls.LastSeenUtc;

                if (srcCls.Skills is { Count: > 0 })
                    dstCls.Skills = srcCls.Skills;
                if (srcCls.Passives is { Count: > 0 })
                    dstCls.Passives = srcCls.Passives;

                dstCls.StatsByLevel ??= new Dictionary<int, CapturedLevelStatSnapshot>();
                if (srcCls.StatsByLevel != null)
                {
                    foreach (var (lvl, srcSnap) in srcCls.StatsByLevel)
                    {
                        if (!dstCls.StatsByLevel.TryGetValue(lvl, out var dstSnap))
                        {
                            dstCls.StatsByLevel[lvl] = srcSnap;
                        }
                        else
                        {
                            dstSnap.Samples += Math.Max(1, srcSnap.Samples);
                            if (srcSnap.MaxHealth > 0) dstSnap.MaxHealth = srcSnap.MaxHealth;
                            dstSnap.Strength = srcSnap.Strength;
                            dstSnap.Intellect = srcSnap.Intellect;
                            dstSnap.Endurance = srcSnap.Endurance;
                            dstSnap.Dexterity = srcSnap.Dexterity;
                            dstSnap.Wisdom = srcSnap.Wisdom;
                            dstSnap.Luck = srcSnap.Luck;
                            dstSnap.AttackPower = srcSnap.AttackPower;
                            dstSnap.SpellPower = srcSnap.SpellPower;
                            dstSnap.Haste = srcSnap.Haste;
                            dstSnap.CriticalChance = srcSnap.CriticalChance;
                            dstSnap.CriticalMultiplier = srcSnap.CriticalMultiplier;
                            dstSnap.EvasionChance = srcSnap.EvasionChance;
                            if (!string.IsNullOrWhiteSpace(srcSnap.EnhancementSignature))
                                dstSnap.EnhancementSignature = srcSnap.EnhancementSignature;
                        }
                    }
                }

                dstCls.Combat ??= new CapturedPlayerCombatStats();
                var sc = srcCls.Combat;
                if (sc != null)
                {
                    dstCls.Combat.Hits += sc.Hits;
                    dstCls.Combat.Crits += sc.Crits;
                    dstCls.Combat.Misses += sc.Misses;
                    dstCls.Combat.TotalDamageDealt += sc.TotalDamageDealt;
                    dstCls.Combat.MaxSingleHit = Math.Max(dstCls.Combat.MaxSingleHit, sc.MaxSingleHit);
                    dstCls.Combat.LevelSumForDamage += sc.LevelSumForDamage;
                    dstCls.Combat.HealEvents += sc.HealEvents;
                    dstCls.Combat.TotalHealingDone += sc.TotalHealingDone;
                    dstCls.Combat.LevelSumForHeal += sc.LevelSumForHeal;
                    dstCls.Combat.AttacksTaken += sc.AttacksTaken;
                    dstCls.Combat.DodgesTaken += sc.DodgesTaken;
                    dstCls.Combat.TotalDamageTaken += sc.TotalDamageTaken;
                }
            }
        }

        if (source.Enhancements != null)
        {
            foreach (var (patternId, srcEnh) in source.Enhancements)
            {
                if (patternId <= 0)
                    continue;

                if (!target.Enhancements.TryGetValue(patternId, out var dstEnh))
                {
                    dstEnh = new CapturedEnhancementProfile
                    {
                        PatternID = patternId,
                        Name = !string.IsNullOrWhiteSpace(srcEnh.Name)
                            ? srcEnh.Name
                            : EnhancementPatternCatalog.GetPatternName(patternId),
                        FirstSeenUtc = srcEnh.FirstSeenUtc
                    };
                    target.Enhancements[patternId] = dstEnh;
                }

                if (!string.IsNullOrWhiteSpace(srcEnh.Name))
                    dstEnh.Name = srcEnh.Name;
                dstEnh.Observations += srcEnh.Observations;
                if (srcEnh.FirstSeenUtc < dstEnh.FirstSeenUtc)
                    dstEnh.FirstSeenUtc = srcEnh.FirstSeenUtc;
                if (srcEnh.LastSeenUtc > dstEnh.LastSeenUtc)
                    dstEnh.LastSeenUtc = srcEnh.LastSeenUtc;

                dstEnh.PureSetStatsByLevel ??= new Dictionary<int, CapturedEnhancementLevelObservation>();
                if (srcEnh.PureSetStatsByLevel != null)
                {
                    foreach (var (lvl, srcObs) in srcEnh.PureSetStatsByLevel)
                    {
                        if (!dstEnh.PureSetStatsByLevel.TryGetValue(lvl, out var dstObs))
                        {
                            dstEnh.PureSetStatsByLevel[lvl] = srcObs;
                        }
                        else
                        {
                            dstObs.Samples += Math.Max(1, srcObs.Samples);
                            if (!string.IsNullOrWhiteSpace(srcObs.ReferenceClass))
                                dstObs.ReferenceClass = srcObs.ReferenceClass;
                            dstObs.Strength = srcObs.Strength;
                            dstObs.Intellect = srcObs.Intellect;
                            dstObs.Endurance = srcObs.Endurance;
                            dstObs.Dexterity = srcObs.Dexterity;
                            dstObs.Wisdom = srcObs.Wisdom;
                            dstObs.Luck = srcObs.Luck;
                        }
                    }
                }
            }
        }
    }
}
