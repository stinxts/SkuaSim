using Newtonsoft.Json.Linq;
using SkuaSim.Collector.Diagnostics;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

public sealed partial class PacketCollectorEngine
{
    // Ephemeral interval trackers (never persisted to disk)
    private readonly Dictionary<string, DateTime> _lastAuraAppliedUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSkillAnimUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastZoneEventUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DateTime> _lastProcTriggerUtc = new();

    private int _lastSelfHp = -1;
    private DateTime _lastSelfHpUtc = DateTime.MinValue;

    public PacketSchemaSampler SchemaSampler { get; } = new();

    private static bool IsAuraOnlyAction(JObject act)
    {
        var cmd = act["cmd"]?.ToString();
        if (!string.IsNullOrWhiteSpace(cmd) && cmd.StartsWith("aura", StringComparison.OrdinalIgnoreCase))
            return true;

        var hasAuraPayload = act["aura"] != null || act["auras"] != null;
        var hasDamagePayload = act["hp"] != null || act["d"] != null || act["type"] != null;
        return hasAuraPayload && !hasDamagePayload;
    }

    private static string[] SplitEntityRefs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private bool IsSelfEntityRef(string entityRef)
    {
        if (string.IsNullOrWhiteSpace(entityRef))
            return false;
        if (_selfPlayerEntityId > 0 &&
            string.Equals(entityRef, $"p:{_selfPlayerEntityId}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(_selfUsername) &&
            string.Equals(entityRef, $"p:{_selfUsername}", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private double? TryGetMonsterHpPercent(int monMapId)
    {
        if (monMapId > 0 && _mapMonsters.TryGetValue(monMapId, out var st) && st.MaxHP > 0)
            return Math.Round(Math.Clamp((double)st.CurrentHP * 100.0 / st.MaxHP, 0.0, 100.0), 1);
        return null;
    }

    private double? TryGetActiveBossHpPercent()
    {
        var cellMons = _mapMonsters.Values
            .Where(m => string.Equals(m.Cell, _currentCell, StringComparison.OrdinalIgnoreCase) && m.CurrentHP > 0 && m.MaxHP > 0)
            .OrderByDescending(m => m.MaxHP)
            .FirstOrDefault();
        return cellMons != null
            ? Math.Round(Math.Clamp((double)cellMons.CurrentHP * 100.0 / cellMons.MaxHP, 0.0, 100.0), 1)
            : null;
    }

    private void ProcessAuraObjectEnhanced(
        JObject auraObj,
        string cInf,
        string tInf,
        string? actionCmd,
        DateTime now,
        HashSet<string>? monsterAurasThisTick,
        HashSet<int>? procsRecordedThisTick = null)
    {
        var auraName = auraObj["nam"]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(auraName))
            return;

        // Do not count aura removals ("aura-") as applications
        if (string.Equals(actionCmd, "aura-", StringComparison.OrdinalIgnoreCase))
            return;

        var casterType = cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase)
            ? "monster"
            : cInf.StartsWith("p:", StringComparison.OrdinalIgnoreCase)
                ? "player"
                : string.Empty;

        var targets = SplitEntityRefs(tInf);
        var hasMonsterTarget = targets.Any(t => t.StartsWith("m:", StringComparison.OrdinalIgnoreCase));
        var playerTargets = targets.Where(t => t.StartsWith("p:", StringComparison.OrdinalIgnoreCase)).ToList();
        var isStackingCmd = string.Equals(actionCmd, "aura++", StringComparison.OrdinalIgnoreCase);
        // AQW sends live stack counts in "stk" (and occasionally "stacks")
        var stacks = ParseInt(auraObj["stk"] ?? auraObj["stacks"], 0);
        var dur = ParseInt(auraObj["dur"], 0);
        var cat = auraObj["cat"]?.ToString() ?? string.Empty;
        float? auraVal = null;
        if (auraObj["val"] != null &&
            float.TryParse(auraObj["val"]?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedVal))
        {
            auraVal = parsedVal;
        }

        // 1. Monster-involved aura (either monster cast it, or monster received it, or Counter Attack)
        int involvedMonMapId = 0;
        CapturedMonster? monster = null;
        if (cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase) && int.TryParse(cInf.AsSpan(2), out var cMonId))
        {
            involvedMonMapId = cMonId;
            monster = ResolveMonsterByMapId(cMonId);
        }
        if (monster == null)
        {
            foreach (var t in targets)
            {
                if (t.StartsWith("m:", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.AsSpan(2), out var tMonId))
                {
                    involvedMonMapId = tMonId;
                    monster = ResolveMonsterByMapId(tMonId);
                    if (monster != null)
                        break;
                }
            }
        }
        if (monster == null && auraName.Equals("Counter Attack", StringComparison.OrdinalIgnoreCase))
            monster = GetMonstersInCell(_currentCell).FirstOrDefault();

        if (monster != null)
        {
            if (casterType == "monster")
                monsterAurasThisTick?.Add(auraName);

            if (!monster.Auras.TryGetValue(auraName, out var auraStat))
            {
                auraStat = new CapturedAuraStat { Name = auraName };
                monster.Auras[auraName] = auraStat;
            }

            if (!string.IsNullOrEmpty(casterType))
                auraStat.CasterType = casterType;
            auraStat.TargetType = hasMonsterTarget ? "monster" : "player";
            if (!string.IsNullOrWhiteSpace(cat))
                auraStat.Category = cat;
            if (dur > 0)
                auraStat.DurationSeconds = dur;
            if (auraVal.HasValue)
            {
                auraStat.MinValue = auraStat.MinValue.HasValue ? Math.Min(auraStat.MinValue.Value, auraVal.Value) : auraVal.Value;
                auraStat.MaxValue = auraStat.MaxValue.HasValue ? Math.Max(auraStat.MaxValue.Value, auraVal.Value) : auraVal.Value;
            }

            if (isStackingCmd || stacks > 1)
                auraStat.IsStacking = true;
            if (stacks > auraStat.MaxStacksObserved)
                auraStat.MaxStacksObserved = stacks;

            if (auraName.Equals("Focus", StringComparison.OrdinalIgnoreCase))
                auraStat.IsTaunt = true;

            if (auraName.Equals("Counter Attack", StringComparison.OrdinalIgnoreCase))
            {
                auraStat.IsCounterAttack = true;
                monster.Combat.CounterAttacksObserved++;
            }

            var hpPct = TryGetMonsterHpPercent(involvedMonMapId);
            if (hpPct.HasValue)
            {
                auraStat.MinBossHpPercent = auraStat.MinBossHpPercent.HasValue
                    ? Math.Min(auraStat.MinBossHpPercent.Value, hpPct.Value)
                    : hpPct.Value;
                auraStat.MaxBossHpPercent = auraStat.MaxBossHpPercent.HasValue
                    ? Math.Max(auraStat.MaxBossHpPercent.Value, hpPct.Value)
                    : hpPct.Value;
            }

            var intervalKey = $"mon:{monster.Key}:{auraName}";
            if (_lastAuraAppliedUtc.TryGetValue(intervalKey, out var prevUtc))
            {
                var elapsedMs = (long)(now - prevUtc).TotalMilliseconds;
                if (elapsedMs is >= 500 and <= 120_000)
                {
                    auraStat.IntervalSamples++;
                    auraStat.TotalIntervalMs += elapsedMs;
                }
            }
            _lastAuraAppliedUtc[intervalKey] = now;
            auraStat.TimesSeen++;
        }

        // 2. Player-cast class / party auras (p -> p or p -> m cast by self)
        // Exclude well-known Awe/Forge weapon proc auras (tracked separately in GameDatabase.Procs)
        var isSelfCaster = casterType == "player" && IsSelfEntityRef(cInf);
        var targetsSelf = playerTargets.Any(IsSelfEntityRef);
        var knownWeaponProcId = WeaponProcCatalog.ResolveProcIdFromAura(auraName, 0);

        if (isSelfCaster && knownWeaponProcId == 0 && !IsClassSyncPending() && !string.IsNullOrWhiteSpace(_currentClassName))
        {
            var cls = GetOrCreateClassDelta(_currentClassName);
            if (!cls.Auras.TryGetValue(auraName, out var clsAura))
            {
                clsAura = new CapturedClassAuraStat { Name = auraName };
                cls.Auras[auraName] = clsAura;
            }

            var scope = hasMonsterTarget
                ? "monster"
                : playerTargets.Count > 1
                    ? "party"
                    : targetsSelf
                        ? "self"
                        : "ally";

            clsAura.TargetScope = scope;
            if (scope is "party" or "ally")
                clsAura.IsPartyBuff = true;
            if (!string.IsNullOrWhiteSpace(cat))
                clsAura.Category = cat;
            if (dur > 0)
                clsAura.DurationSeconds = dur;
            if (isStackingCmd || stacks > 1)
                clsAura.IsStacking = true;
            if (stacks > clsAura.MaxStacksObserved)
                clsAura.MaxStacksObserved = stacks;
            if (auraVal.HasValue)
            {
                clsAura.MinValue = clsAura.MinValue.HasValue ? Math.Min(clsAura.MinValue.Value, auraVal.Value) : auraVal.Value;
                clsAura.MaxValue = clsAura.MaxValue.HasValue ? Math.Max(clsAura.MaxValue.Value, auraVal.Value) : auraVal.Value;
            }
            clsAura.TimesSeen++;
        }

        // 3. Awe & Forge weapon proc triggers (when self casts or receives a known proc aura)
        if (isSelfCaster || (casterType == "player" && targetsSelf))
        {
            var matchedProcId = knownWeaponProcId > 0
                ? knownWeaponProcId
                : WeaponProcCatalog.ResolveProcIdFromAura(auraName, _weaponProcId);
            if (matchedProcId > 0)
            {
                if (procsRecordedThisTick == null || procsRecordedThisTick.Add(matchedProcId))
                {
                    RecordProcTriggerDelta(matchedProcId, auraName, dur, now);
                }
            }
        }
    }

    private void RecordProcHitEquipped()
    {
        if (_weaponProcId <= 0)
            return;

        var proc = GetOrCreateProcDelta(_weaponProcId);
        proc.PlayerHitsWhileEquipped++;
        proc.LastSeenUtc = DateTime.UtcNow;
    }

    private void RecordProcTriggerDelta(int procId, string auraName, int durationSec, DateTime now)
    {
        var proc = GetOrCreateProcDelta(procId);
        proc.ProcTriggersObserved++;
        proc.LastSeenUtc = now;
        if (!string.IsNullOrWhiteSpace(auraName))
            proc.ObservedAuraNames.Add(auraName);
        if (durationSec > 0)
            proc.DurationSeconds = durationSec;

        if (_lastProcTriggerUtc.TryGetValue(procId, out var prevUtc))
        {
            var elapsedMs = (long)(now - prevUtc).TotalMilliseconds;
            if (elapsedMs is >= 300 and <= 120_000)
            {
                proc.IntervalSamples++;
                proc.TotalIntervalMs += elapsedMs;
            }
        }
        _lastProcTriggerUtc[procId] = now;
    }

    private CapturedProcStat GetOrCreateProcDelta(int procId)
    {
        if (!_delta.Procs.TryGetValue(procId, out var proc))
        {
            proc = new CapturedProcStat
            {
                ProcID = procId,
                Name = WeaponProcCatalog.GetProcName(procId),
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };
            _delta.Procs[procId] = proc;
        }
        return proc;
    }

    private void HandleCombatAnimations(JArray animsArray, DateTime now, HashSet<string> monsterAurasThisTick)
    {
        foreach (var anim in animsArray.OfType<JObject>())
        {
            var cInf = anim["cInf"]?.ToString() ?? string.Empty;
            var tInf = anim["tInf"]?.ToString() ?? string.Empty;
            var animStr = anim["animStr"]?.ToString()?.Trim() ?? string.Empty;
            var msg = anim["msg"]?.ToString()?.Trim() ?? string.Empty;

            var isMonsterCaster = cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase);
            if (!isMonsterCaster && string.IsNullOrWhiteSpace(msg))
                continue;

            int casterMapId = 0;
            if (isMonsterCaster)
                int.TryParse(cInf.AsSpan(2), out casterMapId);

            var targetMon = ResolveMonsterFromEntityRef(cInf) ?? GetMonstersInCell(_currentCell).FirstOrDefault();
            if (targetMon == null)
                continue;

            if (!string.IsNullOrWhiteSpace(msg))
            {
                targetMon.SpecialMessages[msg] = targetMon.SpecialMessages.GetValueOrDefault(msg) + 1;
                if (msg.Contains("counter attack", StringComparison.OrdinalIgnoreCase))
                    targetMon.Combat.CounterAttacksObserved++;
            }

            if (string.IsNullOrWhiteSpace(animStr) && string.IsNullOrWhiteSpace(msg))
                continue;

            var targets = SplitEntityRefs(tInf);

            // Skip generic trash-mob auto-attack animations ("Attack1,Attack2" / "Attack1")
            // unless accompanied by a boss message, monster aura, or multi-target AoE hit.
            if (string.IsNullOrWhiteSpace(msg) &&
                monsterAurasThisTick.Count == 0 &&
                targets.Length <= 1 &&
                IsGenericMonsterAutoAttackAnim(animStr))
            {
                continue;
            }

            var skillKey = !string.IsNullOrWhiteSpace(animStr) && !string.IsNullOrWhiteSpace(msg)
                ? $"{animStr}|{msg}"
                : !string.IsNullOrWhiteSpace(animStr)
                    ? animStr
                    : msg;

            if (!targetMon.SkillEvents.TryGetValue(skillKey, out var skillEv))
            {
                skillEv = new CapturedMonsterSkillEvent
                {
                    Key = skillKey,
                    AnimStr = animStr,
                    Message = msg,
                    CasterMapID = casterMapId
                };
                targetMon.SkillEvents[skillKey] = skillEv;
            }

            if (casterMapId > 0)
                skillEv.CasterMapID = casterMapId;
            skillEv.TimesObserved++;

            if (targets.Length > skillEv.MaxTargetsHit)
                skillEv.MaxTargetsHit = targets.Length;

            foreach (var a in monsterAurasThisTick)
                skillEv.AurasInSameTick.Add(a);

            var hpPct = TryGetMonsterHpPercent(casterMapId) ?? TryGetActiveBossHpPercent();
            if (hpPct.HasValue)
            {
                skillEv.MinHpPercent = skillEv.MinHpPercent.HasValue ? Math.Min(skillEv.MinHpPercent.Value, hpPct.Value) : hpPct.Value;
                skillEv.MaxHpPercent = skillEv.MaxHpPercent.HasValue ? Math.Max(skillEv.MaxHpPercent.Value, hpPct.Value) : hpPct.Value;
            }

            var intervalKey = $"skill:{targetMon.Key}:{skillKey}";
            if (_lastSkillAnimUtc.TryGetValue(intervalKey, out var prevUtc))
            {
                var elapsedMs = (long)(now - prevUtc).TotalMilliseconds;
                if (elapsedMs is >= 500 and <= 180_000)
                {
                    skillEv.IntervalSamples++;
                    skillEv.TotalIntervalMs += elapsedMs;
                }
            }
            _lastSkillAnimUtc[intervalKey] = now;
        }
    }

    internal void HandleZoneEventPacket(JObject dataObj)
    {
        var args = dataObj["args"] as JObject;
        if (args == null || args["zoneSet"] == null)
            return;

        var now = DateTime.UtcNow;
        var rawZone = args["zoneSet"]?.ToString() ?? string.Empty;
        var zoneKey = string.IsNullOrWhiteSpace(rawZone) ? "(clear)" : rawZone.Trim();
        var bossHpPct = TryGetActiveBossHpPercent();

        if (!string.IsNullOrWhiteSpace(_currentMapName))
        {
            var map = GetOrCreateMap(_currentMapName);
            RecordZoneEventInto(map.ZoneEvents, $"map:{_currentMapName}:{zoneKey}", zoneKey, rawZone.Trim(), _currentCell, bossHpPct, now);
        }

        // Also attach to the highest-HP monster (boss) in the current cell
        var activeBossState = _mapMonsters.Values
            .Where(m => string.Equals(m.Cell, _currentCell, StringComparison.OrdinalIgnoreCase) && m.CurrentHP > 0)
            .OrderByDescending(m => m.MaxHP)
            .FirstOrDefault();
        if (activeBossState != null)
        {
            var mon = ResolveMonsterByMapId(activeBossState.MapID);
            if (mon != null)
                RecordZoneEventInto(mon.ZoneEvents, $"mon:{mon.Key}:{zoneKey}", zoneKey, rawZone.Trim(), _currentCell, bossHpPct, now);
        }

        SchemaSampler.TryCapture("event_zone", "event", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
    }

    private void RecordZoneEventInto(
        Dictionary<string, CapturedZoneEvent> dict,
        string intervalKey,
        string zoneKey,
        string zoneSet,
        string cell,
        double? bossHpPct,
        DateTime now)
    {
        if (!dict.TryGetValue(zoneKey, out var ze))
        {
            ze = new CapturedZoneEvent { ZoneSet = zoneSet, Cell = cell };
            dict[zoneKey] = ze;
        }

        if (!string.IsNullOrWhiteSpace(cell))
            ze.Cell = cell;
        ze.TimesObserved++;

        if (bossHpPct.HasValue)
        {
            ze.MinBossHpPercent = ze.MinBossHpPercent.HasValue ? Math.Min(ze.MinBossHpPercent.Value, bossHpPct.Value) : bossHpPct.Value;
            ze.MaxBossHpPercent = ze.MaxBossHpPercent.HasValue ? Math.Max(ze.MaxBossHpPercent.Value, bossHpPct.Value) : bossHpPct.Value;
        }

        if (_lastZoneEventUtc.TryGetValue(intervalKey, out var prevUtc))
        {
            var elapsedMs = (long)(now - prevUtc).TotalMilliseconds;
            if (elapsedMs is >= 1000 and <= 180_000)
            {
                ze.IntervalSamples++;
                ze.TotalIntervalMs += elapsedMs;
            }
        }
        _lastZoneEventUtc[intervalKey] = now;
    }

    private static bool IsGenericMonsterAutoAttackAnim(string animStr)
    {
        if (string.IsNullOrWhiteSpace(animStr))
            return false;
        return animStr.Equals("Attack1,Attack2", StringComparison.OrdinalIgnoreCase)
            || animStr.Equals("Attack2,Attack1", StringComparison.OrdinalIgnoreCase)
            || animStr.Equals("Attack1", StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessPlayerHpDeltasFromP(
        JObject dataObj,
        CapturedClassProfile cls,
        int level,
        DateTime now,
        bool sawExplicitHeal,
        bool sawExplicitMonsterDmgToSelf,
        bool sawSelfInflictedDmg,
        bool hasSelfSarsaCast,
        bool hasSaraInTick)
    {
        if (string.IsNullOrWhiteSpace(_selfUsername) || dataObj["p"] is not JObject pObj)
            return;

        JObject? selfState = null;
        foreach (var prop in pObj.Properties())
        {
            if (string.Equals(prop.Name, _selfUsername, StringComparison.OrdinalIgnoreCase) && prop.Value is JObject obj)
            {
                selfState = obj;
                break;
            }
        }
        if (selfState == null || selfState["intHP"] == null)
            return;

        var newHp = ParseInt(selfState["intHP"], -1);
        var intState = ParseInt(selfState["intState"], -1);
        if (newHp < 0)
            return;

        var hasCombatActivity = dataObj["sarsa"] != null || dataObj["sara"] != null || dataObj["anims"] != null || dataObj["a"] != null || intState == 2;
        var recentSample = _lastSelfHp > 0 && _lastSelfHpUtc != DateTime.MinValue && (now - _lastSelfHpUtc).TotalSeconds <= 15;

        if (recentSample && hasCombatActivity)
        {
            var delta = newHp - _lastSelfHp;
            if (delta > 0 && !sawExplicitHeal)
            {
                cls.LastSeenUtc = now;
                cls.Combat.HealEvents++;
                cls.Combat.TotalHealingDone += delta;
                cls.Combat.LevelSumForHeal += level;
            }
            else if (delta < 0 &&
                     !sawExplicitMonsterDmgToSelf &&
                     !sawSelfInflictedDmg &&
                     !hasSelfSarsaCast &&
                     !hasSaraInTick)
            {
                // Fallback ONLY when an explicit monster caster exists in this exact tick (never blame a solo mob for player self-damage skills like VHL Shackle!)
                var (attacker, animPlayerTargets) = ResolveAttackingMonsterInTick(dataObj);
                if (attacker != null)
                {
                    var dmg = Math.Abs(delta);
                    cls.LastSeenUtc = now;
                    cls.Combat.AttacksTaken++;
                    cls.Combat.TotalDamageTaken += dmg;

                    attacker.Combat.AttacksObserved++;
                    attacker.Combat.Hits++;
                    attacker.Combat.TotalDamage += dmg;
                    attacker.Combat.MinDamage = attacker.Combat.MinDamage.HasValue
                        ? Math.Min(attacker.Combat.MinDamage.Value, dmg)
                        : dmg;
                    attacker.Combat.MaxDamage = attacker.Combat.MaxDamage.HasValue
                        ? Math.Max(attacker.Combat.MaxDamage.Value, dmg)
                        : dmg;
                    if (animPlayerTargets > attacker.Combat.MaxTargetsHit)
                        attacker.Combat.MaxTargetsHit = animPlayerTargets;
                    RecordMonsterAttackInterval(attacker, dataObj, now);
                }
            }
        }

        _lastSelfHp = newHp == 0 ? -1 : newHp;
        _lastSelfHpUtc = now;
    }

    private (CapturedMonster? Monster, int PlayerTargetCount) ResolveAttackingMonsterInTick(JObject dataObj)
    {
        if (dataObj["anims"] is JArray anims)
        {
            foreach (var anim in anims.OfType<JObject>())
            {
                var mon = ResolveMonsterFromEntityRef(anim["cInf"]?.ToString());
                if (mon != null)
                {
                    var pCount = SplitEntityRefs(anim["tInf"]?.ToString())
                        .Count(t => t.StartsWith("p:", StringComparison.OrdinalIgnoreCase));
                    return (mon, pCount);
                }
            }
        }
        if (dataObj["sara"] is JArray saraArr)
        {
            foreach (var entry in saraArr.OfType<JObject>())
            {
                var act = entry["actionResult"] as JObject ?? entry;
                var mon = ResolveMonsterFromEntityRef(act["cInf"]?.ToString() ?? entry["cInf"]?.ToString());
                if (mon != null)
                {
                    var pCount = SplitEntityRefs(act["tInf"]?.ToString())
                        .Count(t => t.StartsWith("p:", StringComparison.OrdinalIgnoreCase));
                    return (mon, pCount);
                }
            }
        }
        if (dataObj["a"] is JArray aArr)
        {
            foreach (var act in aArr.OfType<JObject>())
            {
                var mon = ResolveMonsterFromEntityRef(act["cInf"]?.ToString());
                if (mon != null)
                {
                    var pCount = SplitEntityRefs(act["tInf"]?.ToString())
                        .Count(t => t.StartsWith("p:", StringComparison.OrdinalIgnoreCase));
                    return (mon, pCount);
                }
            }
        }
        if (dataObj["sarsa"] is JArray sarsa)
        {
            foreach (var s in sarsa.OfType<JObject>())
            {
                var mon = ResolveMonsterFromEntityRef(s["cInf"]?.ToString());
                if (mon != null)
                    return (mon, 1);
            }
        }

        return (null, 0);
    }

    private void RecordMonsterAttackInterval(CapturedMonster mon, JObject dataObj, DateTime now)
    {
        var monMapId = 0;
        if (dataObj["anims"] is JArray anims)
        {
            foreach (var anim in anims.OfType<JObject>())
            {
                var c = anim["cInf"]?.ToString();
                if (c != null && c.StartsWith("m:", StringComparison.OrdinalIgnoreCase) && int.TryParse(c.AsSpan(2), out monMapId))
                    break;
            }
        }

        ActiveMonsterState? activeState = null;
        if (monMapId > 0)
            _mapMonsters.TryGetValue(monMapId, out activeState);
        activeState ??= _mapMonsters.Values.FirstOrDefault(m => string.Equals(m.Key, mon.Key, StringComparison.OrdinalIgnoreCase));
        if (activeState == null)
            return;

        if (activeState.LastAttackUtc != DateTime.MinValue)
        {
            var elapsedMs = (long)(now - activeState.LastAttackUtc).TotalMilliseconds;
            if (elapsedMs is >= 250 and <= 30_000)
            {
                mon.Combat.AttackIntervalSamples++;
                mon.Combat.TotalAttackIntervalMs += elapsedMs;
            }
        }
        if (activeState.LastAttackUtc == DateTime.MinValue || (now - activeState.LastAttackUtc).TotalMilliseconds >= 250)
            activeState.LastAttackUtc = now;
    }

    private void SampleCombatPacketSchema(JObject dataObj)
    {
        if (!SchemaSampler.Enabled)
            return;

        var hasMonAnim = false;
        var hasBossMsg = false;
        var hasMonAttack = false;
        var hasHeal = false;
        var hasMonAura = false;
        var hasMonCastAura = false;
        var hasPlayerAura = false;
        var hasPartyBuff = false;
        var hasSarsa = dataObj["sarsa"] is JArray sa && sa.Count > 0;

        if (dataObj["anims"] is JArray anims)
        {
            foreach (var anim in anims.OfType<JObject>())
            {
                var c = anim["cInf"]?.ToString() ?? string.Empty;
                if (c.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                    hasMonAnim = true;
                if (!string.IsNullOrWhiteSpace(anim["msg"]?.ToString()))
                    hasBossMsg = true;
            }
        }

        void InspectActions(JArray? arr, string? outerCaster)
        {
            if (arr == null) return;
            foreach (var act in arr.OfType<JObject>())
            {
                var c = act["cInf"]?.ToString() ?? outerCaster ?? string.Empty;
                var t = act["tInf"]?.ToString() ?? string.Empty;
                var typ = (act["type"]?.ToString() ?? string.Empty).ToLowerInvariant();
                var hp = ParseInt(act["hp"] ?? act["d"], 0);
                var hasAura = act["aura"] != null || act["auras"] != null;

                if (hasAura)
                {
                    if (c.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMonAura = true;
                        hasMonCastAura = true;
                    }
                    else if (t.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMonAura = true;
                    }
                    else if (c.StartsWith("p:", StringComparison.OrdinalIgnoreCase) && t.StartsWith("p:", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPlayerAura = true;
                        var pTargets = SplitEntityRefs(t);
                        if (pTargets.Length > 1 || pTargets.Any(pt => !IsSelfEntityRef(pt)))
                            hasPartyBuff = true;
                    }
                }
                else
                {
                    if (c.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                        hasMonAttack = true;
                    if (hp < 0 || typ is "heal" or "hot")
                        hasHeal = true;
                }
            }
        }

        InspectActions(dataObj["a"] as JArray, null);
        if (dataObj["sarsa"] is JArray sarsaArr)
        {
            foreach (var entry in sarsaArr.OfType<JObject>())
                InspectActions(entry["a"] as JArray, entry["cInf"]?.ToString());
        }
        if (dataObj["sara"] is JArray saraArr)
        {
            foreach (var entry in saraArr.OfType<JObject>())
            {
                var act = entry["actionResult"] as JObject ?? entry;
                var c = act["cInf"]?.ToString() ?? entry["cInf"]?.ToString() ?? string.Empty;
                var typ = (act["type"]?.ToString() ?? string.Empty).ToLowerInvariant();
                var hp = ParseInt(act["hp"] ?? act["d"], 0);
                if (c.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                    hasMonAttack = true;
                if (hp < 0 || typ is "heal" or "hot")
                    hasHeal = true;
            }
        }

        if (hasMonAttack || (hasMonAnim && dataObj["p"] != null))
            SchemaSampler.TryCapture("ct_monster_attack", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasHeal)
            SchemaSampler.TryCapture("ct_player_heal", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasMonAnim)
            SchemaSampler.TryCapture("ct_monster_anim", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasBossMsg)
            SchemaSampler.TryCapture("ct_boss_msg", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasMonCastAura)
            SchemaSampler.TryCapture("ct_monster_cast_aura", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasMonAura)
            SchemaSampler.TryCapture("ct_aura_monster", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasPlayerAura)
            SchemaSampler.TryCapture("ct_aura_player", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasPartyBuff)
            SchemaSampler.TryCapture("ct_party_buff", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (hasSarsa)
            SchemaSampler.TryCapture("ct_sarsa_sample", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        if (dataObj["p"] is JObject pObj && pObj.Properties().Any(p => p.Value is JObject po && po["intHP"] != null))
            SchemaSampler.TryCapture("ct_player_hp_update", "ct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
    }
}
