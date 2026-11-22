using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Storage;

public sealed partial class GameDatabaseStore
{
    private static void NormalizeLoadedDatabase(GameDatabase db)
    {
        db.Classes = new Dictionary<string, CapturedClassProfile>(
            db.Classes ?? new Dictionary<string, CapturedClassProfile>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var cls in db.Classes.Values)
        {
            cls.Auras = new Dictionary<string, CapturedClassAuraStat>(
                cls.Auras ?? new Dictionary<string, CapturedClassAuraStat>(),
                StringComparer.OrdinalIgnoreCase);
            cls.StatsByProfile ??= new Dictionary<string, CapturedLevelStatSnapshot>(StringComparer.Ordinal);
            cls.CombatByProfile ??= new Dictionary<string, CapturedPlayerCombatStats>(StringComparer.Ordinal);

            var procAuraKeys = cls.Auras.Keys
                .Where(k => WeaponProcCatalog.ResolveProcIdFromAura(k, 0) > 0)
                .ToList();
            foreach (var pk in procAuraKeys)
                cls.Auras.Remove(pk);
        }
        db.Enhancements ??= new Dictionary<int, CapturedEnhancementProfile>();
        foreach (var (patternId, enh) in db.Enhancements)
        {
            if (string.IsNullOrWhiteSpace(enh.Name) || enh.Name.StartsWith("Pattern_", StringComparison.OrdinalIgnoreCase))
                enh.Name = EnhancementPatternCatalog.GetPatternName(patternId);
        }
        db.Procs ??= new Dictionary<int, CapturedProcStat>();

        foreach (var map in db.Maps.Values)
        {
            map.ZoneEvents = new Dictionary<string, CapturedZoneEvent>(
                map.ZoneEvents ?? new Dictionary<string, CapturedZoneEvent>(),
                StringComparer.OrdinalIgnoreCase);
        }
        foreach (var mon in db.Monsters.Values)
        {
            mon.SkillEvents = new Dictionary<string, CapturedMonsterSkillEvent>(
                mon.SkillEvents ?? new Dictionary<string, CapturedMonsterSkillEvent>(),
                StringComparer.OrdinalIgnoreCase);

            // Auto-prune legacy generic auto-attack noise ("Attack1,Attack2") without boss message, aura, or AoE
            var noiseKeys = mon.SkillEvents
                .Where(kv =>
                    string.IsNullOrWhiteSpace(kv.Value.Message) &&
                    kv.Value.MaxTargetsHit <= 1 &&
                    (kv.Value.AurasInSameTick == null || kv.Value.AurasInSameTick.Count == 0) &&
                    (kv.Value.AnimStr.Equals("Attack1,Attack2", StringComparison.OrdinalIgnoreCase) ||
                     kv.Value.AnimStr.Equals("Attack2,Attack1", StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var nk in noiseKeys)
                mon.SkillEvents.Remove(nk);

            mon.ZoneEvents = new Dictionary<string, CapturedZoneEvent>(
                mon.ZoneEvents ?? new Dictionary<string, CapturedZoneEvent>(),
                StringComparer.OrdinalIgnoreCase);
        }
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

                dstCls.StatsByProfile ??= new Dictionary<string, CapturedLevelStatSnapshot>(StringComparer.Ordinal);
                if (srcCls.StatsByProfile != null)
                {
                    foreach (var (statKey, srcSnap) in srcCls.StatsByProfile)
                    {
                        // The same level, rank and gear give the same stats: keep the newest values, add the samples.
                        var samples = Math.Max(1, srcSnap.Samples)
                            + (dstCls.StatsByProfile.TryGetValue(statKey, out var old) ? old.Samples : 0);
                        dstCls.StatsByProfile[statKey] = srcSnap;
                        srcSnap.Samples = samples;
                    }
                }

                dstCls.Combat ??= new CapturedPlayerCombatStats();
                if (srcCls.Combat != null)
                    dstCls.Combat.Add(srcCls.Combat);

                dstCls.CombatByProfile ??= new Dictionary<string, CapturedPlayerCombatStats>(StringComparer.Ordinal);
                if (srcCls.CombatByProfile != null)
                {
                    foreach (var (combatKey, srcCombat) in srcCls.CombatByProfile)
                    {
                        if (!dstCls.CombatByProfile.TryGetValue(combatKey, out var dstCombat))
                            dstCls.CombatByProfile[combatKey] = dstCombat = new CapturedPlayerCombatStats();
                        dstCombat.Add(srcCombat);
                    }
                }

                dstCls.Auras ??= new Dictionary<string, CapturedClassAuraStat>(StringComparer.OrdinalIgnoreCase);
                if (srcCls.Auras != null)
                {
                    foreach (var (auraName, srcAura) in srcCls.Auras)
                    {
                        if (!dstCls.Auras.TryGetValue(auraName, out var dstAura))
                        {
                            dstAura = new CapturedClassAuraStat { Name = string.IsNullOrWhiteSpace(srcAura.Name) ? auraName : srcAura.Name };
                            dstCls.Auras[auraName] = dstAura;
                        }
                        if (!string.IsNullOrWhiteSpace(srcAura.TargetScope)) dstAura.TargetScope = srcAura.TargetScope;
                        if (!string.IsNullOrWhiteSpace(srcAura.Category)) dstAura.Category = srcAura.Category;
                        if (srcAura.DurationSeconds > 0) dstAura.DurationSeconds = srcAura.DurationSeconds;
                        dstAura.IsPartyBuff |= srcAura.IsPartyBuff;
                        dstAura.IsStacking |= srcAura.IsStacking;
                        dstAura.MaxStacksObserved = Math.Max(dstAura.MaxStacksObserved, srcAura.MaxStacksObserved);
                        if (srcAura.MinValue.HasValue)
                            dstAura.MinValue = dstAura.MinValue.HasValue ? Math.Min(dstAura.MinValue.Value, srcAura.MinValue.Value) : srcAura.MinValue.Value;
                        if (srcAura.MaxValue.HasValue)
                            dstAura.MaxValue = dstAura.MaxValue.HasValue ? Math.Max(dstAura.MaxValue.Value, srcAura.MaxValue.Value) : srcAura.MaxValue.Value;
                        dstAura.TimesSeen += srcAura.TimesSeen;
                    }
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

                if (!string.IsNullOrWhiteSpace(srcEnh.Name) && !srcEnh.Name.StartsWith("Pattern_", StringComparison.OrdinalIgnoreCase))
                    dstEnh.Name = srcEnh.Name;
                else if (string.IsNullOrWhiteSpace(dstEnh.Name) || dstEnh.Name.StartsWith("Pattern_", StringComparison.OrdinalIgnoreCase))
                    dstEnh.Name = EnhancementPatternCatalog.GetPatternName(patternId);
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

        target.Procs ??= new Dictionary<int, CapturedProcStat>();
        if (source.Procs != null)
        {
            foreach (var (procId, srcProc) in source.Procs)
            {
                if (procId <= 0)
                    continue;
                if (!target.Procs.TryGetValue(procId, out var dstProc))
                {
                    dstProc = new CapturedProcStat
                    {
                        ProcID = procId,
                        Name = !string.IsNullOrWhiteSpace(srcProc.Name) ? srcProc.Name : WeaponProcCatalog.GetProcName(procId),
                        FirstSeenUtc = srcProc.FirstSeenUtc
                    };
                    target.Procs[procId] = dstProc;
                }

                if (!string.IsNullOrWhiteSpace(srcProc.Name)) dstProc.Name = srcProc.Name;
                dstProc.PlayerHitsWhileEquipped += srcProc.PlayerHitsWhileEquipped;
                dstProc.ProcTriggersObserved += srcProc.ProcTriggersObserved;
                if (srcProc.DurationSeconds > 0) dstProc.DurationSeconds = srcProc.DurationSeconds;
                dstProc.IntervalSamples += srcProc.IntervalSamples;
                dstProc.TotalIntervalMs += srcProc.TotalIntervalMs;
                if (srcProc.FirstSeenUtc < dstProc.FirstSeenUtc) dstProc.FirstSeenUtc = srcProc.FirstSeenUtc;
                if (srcProc.LastSeenUtc > dstProc.LastSeenUtc) dstProc.LastSeenUtc = srcProc.LastSeenUtc;
                if (srcProc.ObservedAuraNames != null)
                {
                    foreach (var an in srcProc.ObservedAuraNames)
                        if (!string.IsNullOrWhiteSpace(an))
                            dstProc.ObservedAuraNames.Add(an);
                }
            }
        }
    }

    private static void MergeZoneEvents(Dictionary<string, CapturedZoneEvent> dst, Dictionary<string, CapturedZoneEvent> src)
    {
        foreach (var (key, s) in src)
        {
            if (!dst.TryGetValue(key, out var d))
            {
                d = new CapturedZoneEvent { ZoneSet = s.ZoneSet, Cell = s.Cell };
                dst[key] = d;
            }
            if (!string.IsNullOrWhiteSpace(s.Cell)) d.Cell = s.Cell;
            d.TimesObserved += s.TimesObserved;
            d.IntervalSamples += s.IntervalSamples;
            d.TotalIntervalMs += s.TotalIntervalMs;
            if (s.MinBossHpPercent.HasValue)
                d.MinBossHpPercent = d.MinBossHpPercent.HasValue ? Math.Min(d.MinBossHpPercent.Value, s.MinBossHpPercent.Value) : s.MinBossHpPercent.Value;
            if (s.MaxBossHpPercent.HasValue)
                d.MaxBossHpPercent = d.MaxBossHpPercent.HasValue ? Math.Max(d.MaxBossHpPercent.Value, s.MaxBossHpPercent.Value) : s.MaxBossHpPercent.Value;
        }
    }

    private static void MergeMonsterSkillEvents(Dictionary<string, CapturedMonsterSkillEvent> dst, Dictionary<string, CapturedMonsterSkillEvent> src)
    {
        foreach (var (key, s) in src)
        {
            if (!dst.TryGetValue(key, out var d))
            {
                d = new CapturedMonsterSkillEvent
                {
                    Key = string.IsNullOrWhiteSpace(s.Key) ? key : s.Key,
                    AnimStr = s.AnimStr,
                    Message = s.Message,
                    CasterMapID = s.CasterMapID
                };
                dst[key] = d;
            }
            if (!string.IsNullOrWhiteSpace(s.AnimStr)) d.AnimStr = s.AnimStr;
            if (!string.IsNullOrWhiteSpace(s.Message)) d.Message = s.Message;
            if (s.CasterMapID > 0) d.CasterMapID = s.CasterMapID;
            d.TimesObserved += s.TimesObserved;
            d.IntervalSamples += s.IntervalSamples;
            d.TotalIntervalMs += s.TotalIntervalMs;
            d.MaxTargetsHit = Math.Max(d.MaxTargetsHit, s.MaxTargetsHit);
            if (s.MinHpPercent.HasValue)
                d.MinHpPercent = d.MinHpPercent.HasValue ? Math.Min(d.MinHpPercent.Value, s.MinHpPercent.Value) : s.MinHpPercent.Value;
            if (s.MaxHpPercent.HasValue)
                d.MaxHpPercent = d.MaxHpPercent.HasValue ? Math.Max(d.MaxHpPercent.Value, s.MaxHpPercent.Value) : s.MaxHpPercent.Value;
            if (s.AurasInSameTick != null)
            {
                foreach (var a in s.AurasInSameTick)
                    if (!string.IsNullOrWhiteSpace(a))
                        d.AurasInSameTick.Add(a);
            }
        }
    }
}
