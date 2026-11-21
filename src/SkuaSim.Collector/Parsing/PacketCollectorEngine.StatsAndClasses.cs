using Newtonsoft.Json.Linq;
using Skua.Core.Models.Players;
using Skua.Core.Models.Skills;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

public sealed partial class PacketCollectorEngine
{
    // Strictly ephemeral in-memory player state (NEVER persisted to GameDatabase / disk).
    private int _selfPlayerEntityId;
    private string _selfUsername = string.Empty;
    private string _currentClassName = string.Empty;
    private int _currentLevel;
    private int _currentMaxHealth;
    private int _weaponEnhPattern;
    private int _weaponEnhLevel;
    private int _weaponProcId;
    private int _classEnhPattern;
    private int _classEnhLevel;
    private int _helmEnhPattern;
    private int _helmEnhLevel;
    private int _capeEnhPattern;
    private int _capeEnhLevel;
    private bool _classSyncPending;
    private DateTime _classSyncPendingSinceUtc = DateTime.MinValue;

    private bool IsClassSyncPending()
    {
        if (!_classSyncPending)
            return false;
        // Safety auto-clear after 2s so a missed updateClass packet never stalls collection
        if ((DateTime.UtcNow - _classSyncPendingSinceUtc).TotalSeconds > 2.0)
        {
            _classSyncPending = false;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Updates ephemeral combat context so outgoing/incoming combat events, stat updates,
    /// and skill definitions can be attributed anonymously to the active class and enhancement setup.
    /// Player IDs and names are NEVER written to the database.
    /// </summary>
    public void SetPlayerCombatContext(
        int? selfEntityId,
        string? className,
        int level,
        int maxHealth = 0,
        int weaponEnhPattern = 0,
        int weaponEnhLevel = 0,
        int classEnhPattern = 0,
        int classEnhLevel = 0,
        int helmEnhPattern = 0,
        int helmEnhLevel = 0,
        int capeEnhPattern = 0,
        int capeEnhLevel = 0,
        string? selfUsername = null,
        int weaponProcId = 0)
    {
        lock (_sync)
        {
            if (selfEntityId is > 0)
                _selfPlayerEntityId = selfEntityId.Value;
            if (!string.IsNullOrWhiteSpace(selfUsername))
            {
                var user = selfUsername.Trim();
                // Another account logged in on this client: the level/class/CP still held belong to the previous one.
                if (!string.IsNullOrEmpty(_selfUsername) && !string.Equals(_selfUsername, user, StringComparison.OrdinalIgnoreCase))
                    ResetCharacterContextLocked();
                _selfUsername = user;
            }
            if (!string.IsNullOrWhiteSpace(className))
            {
                var trimmed = className.Trim();
                if (!string.Equals(_currentClassName, trimmed, StringComparison.OrdinalIgnoreCase))
                    _lastSelfHp = -1;
                _currentClassName = trimmed;
                _classSyncPending = false;
            }
            if (level > 0)
                _currentLevel = Math.Clamp(level, 1, 100);
            if (maxHealth > 0)
                _currentMaxHealth = maxHealth;

            _weaponEnhPattern = Math.Max(0, weaponEnhPattern);
            _weaponEnhLevel = Math.Max(0, weaponEnhLevel);
            _weaponProcId = Math.Max(0, weaponProcId);
            _classEnhPattern = Math.Max(0, classEnhPattern);
            _classEnhLevel = Math.Max(0, classEnhLevel);
            _helmEnhPattern = Math.Max(0, helmEnhPattern);
            _helmEnhLevel = Math.Max(0, helmEnhLevel);
            _capeEnhPattern = Math.Max(0, capeEnhPattern);
            _capeEnhLevel = Math.Max(0, capeEnhLevel);
        }
    }

    /// <summary>
    /// Records live anonymized class skills, primary/secondary stats, and pure-set enhancement
    /// observations directly from Skua's in-game state without saving any personal identifiers.
    /// </summary>
    public void RecordLivePlayerState(
        int? selfEntityId,
        string? className,
        int level,
        int maxHealth,
        PlayerStats? stats,
        IEnumerable<SkillInfo>? skills,
        int weaponEnhPattern = 0,
        int weaponEnhLevel = 0,
        int classEnhPattern = 0,
        int classEnhLevel = 0,
        int helmEnhPattern = 0,
        int helmEnhLevel = 0,
        int capeEnhPattern = 0,
        int capeEnhLevel = 0,
        string? selfUsername = null,
        int weaponProcId = 0)
    {
        SetPlayerCombatContext(
            selfEntityId, className, level, maxHealth,
            weaponEnhPattern, weaponEnhLevel,
            classEnhPattern, classEnhLevel,
            helmEnhPattern, helmEnhLevel,
            capeEnhPattern, capeEnhLevel,
            selfUsername, weaponProcId);

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentClassName))
                return;

            var cls = GetOrCreateClassDelta(_currentClassName);
            var now = DateTime.UtcNow;
            cls.LastSeenUtc = now;

            if (skills != null)
            {
                var capturedSkills = new List<CapturedClassSkill>();
                var slot = 0;
                foreach (var s in skills)
                {
                    if (s == null)
                    {
                        slot++;
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(s.Name))
                    {
                        capturedSkills.Add(BuildSkillModel(
                            slot,
                            s.ActID,
                            s.ID,
                            s.Name,
                            s.Description,
                            s.Icon,
                            s.Type,
                            s.Damage,
                            s.Cooldown,
                            s.MP,
                            s.Range,
                            s.MinTargets,
                            s.MaxTargets,
                            s.Auto));
                    }
                    slot++;
                }

                if (capturedSkills.Count > 0)
                    cls.Skills = capturedSkills;
            }

            if (stats != null && _currentLevel > 0)
            {
                RecordStatSnapshotDelta(
                    cls,
                    _currentLevel,
                    _currentMaxHealth,
                    stats.Strength,
                    stats.Intellect,
                    stats.Endurance,
                    stats.Dexterity,
                    stats.Wisdom,
                    stats.Luck,
                    stats.AttackPower,
                    stats.SpellPower,
                    stats.Haste,
                    stats.CriticalChance,
                    stats.CriticalMultiplier,
                    stats.EvasionChance);
            }
        }
    }

    internal void HandleUpdateClassPacket(JObject dataObj)
    {
        var uid = ParseInt(dataObj["uid"]);
        if (uid > 0 && _selfPlayerEntityId > 0 && uid != _selfPlayerEntityId)
            return;

        var className = dataObj["sClassName"]?.ToString()
                     ?? dataObj["strClassName"]?.ToString()
                     ?? dataObj["sClass"]?.ToString()
                     ?? dataObj["sName"]?.ToString();

        if (!string.IsNullOrWhiteSpace(className))
        {
            var trimmed = className.Trim();
            if (!string.Equals(_currentClassName, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                _currentClassName = trimmed;
                _lastSelfHp = -1;
                _currentClassCp = -1; // another class: its CP total comes with this packet
            }
            _classSyncPending = false;
        }

        // "iCP" is the class's total class points (verified samples: Rogue 195,441; Void Highlord 302,500 = rank 10).
        var classCp = ParseInt(dataObj["iCP"], -1);
        if (classCp >= 0)
            _currentClassCp = classCp;

        SchemaSampler.TryCapture("updateClass_sample", "updateClass", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
    }

    // Total CP of the equipped class (RAM only; -1 = unknown) and a rank hint from the client (Skua CurrentClassRank).
    private int _currentClassCp = -1;
    private int _classRankHint;

    /// <summary>Class rank from the client (used only until a class CP total is known from "updateClass").</summary>
    public void SetClassRankHint(int rank)
    {
        lock (_sync)
            _classRankHint = rank is >= 1 and <= 10 ? rank : 0;
    }

    /// <summary>AQW class rank for a CP total: rank r needs (5·r·(r+1))² CP (900, 3,600 … 302,500 at rank 10).</summary>
    public static int ClassRankForCp(int classCp)
    {
        var rank = 1;
        while (rank < 10)
        {
            var root = 5 * (rank + 1) * (rank + 2);
            if (classCp < root * root)
                break;
            rank++;
        }
        return rank;
    }

    /// <summary>
    /// A (re)login on this client: forget the character level, class, class CP and rank hint (RAM only) so kills made
    /// before the new account's values arrive are not recorded with the previous account's level or rank. Each Skua
    /// client runs its own collector, so several accounts logged in at once never share this state; only an account
    /// switch on the same client can leave it stale. Called under <c>_sync</c>.
    /// </summary>
    private void ResetCharacterContextLocked()
    {
        _currentLevel = 0;
        _currentClassName = string.Empty;
        _currentClassCp = -1;
        _classRankHint = 0;
        _lastSelfHp = -1;
        CharacterContextResets++;
    }

    /// <summary>How many times this session the character context was reset by a (re)login.</summary>
    public int CharacterContextResets { get; private set; }

    private int CurrentClassRank() =>_currentClassCp >= 0 ? ClassRankForCp(_currentClassCp) : _classRankHint;

    internal void HandleEquipItemPacket(JObject dataObj)
    {
        var uid = ParseInt(dataObj["uid"]);
        if (uid > 0 && _selfPlayerEntityId > 0 && uid != _selfPlayerEntityId)
            return;

        var slot = dataObj["strES"]?.ToString() ?? dataObj["sES"]?.ToString() ?? string.Empty;
        var enhPat = ParseInt(dataObj["EnhPatternID"], -1);
        var enhLvl = ParseInt(dataObj["iLvl"], -1);
        var procId = ParseInt(dataObj["ProcID"], -1);

        if (slot.Equals("ar", StringComparison.OrdinalIgnoreCase))
        {
            var name = dataObj["sName"]?.ToString() ?? dataObj["strName"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                _currentClassName = name.Trim();
                _classSyncPending = false;
            }
            else
            {
                // Class swap started; pause class attribution briefly until updateClass / live snapshot provides the new class name
                _classSyncPending = true;
                _classSyncPendingSinceUtc = DateTime.UtcNow;
            }
            _lastSelfHp = -1;
            if (enhPat >= 0) _classEnhPattern = enhPat;
            if (enhLvl > 0) _classEnhLevel = enhLvl;
        }
        else if (slot.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
        {
            if (enhPat >= 0) _weaponEnhPattern = enhPat;
            if (enhLvl > 0) _weaponEnhLevel = enhLvl;
            if (procId >= 0) _weaponProcId = procId;
        }
        else if (slot.Equals("he", StringComparison.OrdinalIgnoreCase))
        {
            if (enhPat >= 0) _helmEnhPattern = enhPat;
            if (enhLvl > 0) _helmEnhLevel = enhLvl;
        }
        else if (slot.Equals("ba", StringComparison.OrdinalIgnoreCase))
        {
            if (enhPat >= 0) _capeEnhPattern = enhPat;
            if (enhLvl > 0) _capeEnhLevel = enhLvl;
        }

        SchemaSampler.TryCapture("equipItem_sample", "equipItem", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
    }

    internal void HandleSkillActionsPacket(JObject dataObj)
    {
        var actions = dataObj["actions"] as JObject;
        if (actions == null)
            return;

        lock (_sync)
        {
            var explicitClass = dataObj["sClass"]?.ToString();
            if (!string.IsNullOrWhiteSpace(explicitClass))
            {
                _currentClassName = explicitClass.Trim();
                _classSyncPending = false;
            }
            else if (IsClassSyncPending())
            {
                return;
            }

            var className = !string.IsNullOrWhiteSpace(explicitClass) ? explicitClass : _currentClassName;
            if (string.IsNullOrWhiteSpace(className))
                return;

            var cls = GetOrCreateClassDelta(className.Trim());
            cls.LastSeenUtc = DateTime.UtcNow;

            if (actions["active"] is JArray activeArr && activeArr.Count > 0)
            {
                var skills = new List<CapturedClassSkill>(activeArr.Count);
                for (var i = 0; i < activeArr.Count; i++)
                {
                    if (activeArr[i] is not JObject so)
                        continue;

                    var nam = so["nam"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(nam))
                        continue;

                    skills.Add(BuildSkillModel(
                        slot: i,
                        actId: ParseInt(so["actID"]),
                        skillId: ParseInt(so["id"]),
                        name: nam,
                        description: so["desc"]?.ToString() ?? string.Empty,
                        icon: so["icon"]?.ToString() ?? string.Empty,
                        type: so["typ"]?.ToString() ?? string.Empty,
                        damage: ParseDoubleSafe(so["damage"], 1.0),
                        cooldownMs: ParseInt(so["cd"]),
                        mpCost: ParseInt(so["mp"]),
                        range: ParseInt(so["range"], 300),
                        minTargets: Math.Max(1, ParseInt(so["tgtMin"], 1)),
                        maxTargets: Math.Max(1, ParseInt(so["tgtMax"], 1)),
                        auto: ParseBool(so["auto"])));
                }

                if (skills.Count > 0)
                    cls.Skills = skills;
            }

            if (actions["passive"] is JArray passiveArr && passiveArr.Count > 0)
            {
                var passives = new List<CapturedClassPassive>(passiveArr.Count);
                for (var i = 0; i < passiveArr.Count; i++)
                {
                    if (passiveArr[i] is not JObject po)
                        continue;

                    var nam = po["nam"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(nam))
                        continue;

                    passives.Add(new CapturedClassPassive
                    {
                        Name = nam,
                        Description = po["desc"]?.ToString() ?? string.Empty,
                        UnlockRank = i == 0 ? 4 : 10
                    });
                }

                if (passives.Count > 0)
                    cls.Passives = passives;
            }

            SchemaSampler.TryCapture("sAct_sample", "sAct", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        }
    }

    internal void HandleStatUpdatePacket(JObject dataObj)
    {
        var sta = dataObj["sta"] as JObject ?? dataObj;
        if (sta["$STR"] == null && sta["$INT"] == null && sta["$ap"] == null && sta["$sp"] == null)
            return;

        lock (_sync)
        {
            var uid = ParseInt(dataObj["uid"]);
            if (uid > 0 && _selfPlayerEntityId > 0 && uid != _selfPlayerEntityId)
                return;

            if (IsClassSyncPending() || string.IsNullOrWhiteSpace(_currentClassName) || _currentLevel <= 0)
                return;

            var cls = GetOrCreateClassDelta(_currentClassName);
            RecordStatSnapshotDelta(
                cls,
                _currentLevel,
                _currentMaxHealth,
                str: ParseInt(sta["$STR"]),
                intl: ParseInt(sta["$INT"]),
                end: ParseInt(sta["$END"]),
                dex: ParseInt(sta["$DEX"]),
                wis: ParseInt(sta["$WIS"]),
                lck: ParseInt(sta["$LCK"]),
                ap: ParseInt(sta["$ap"]),
                sp: ParseInt(sta["$sp"]),
                haste: ParseDoubleSafe(sta["$tha"], 0),
                critChance: ParseDoubleSafe(sta["$tcr"], 0),
                critMult: ParseDoubleSafe(sta["$scm"], 1.5),
                evasionChance: ParseDoubleSafe(sta["$tdo"], 0));

            SchemaSampler.TryCapture("stu_sample", "stu", dataObj, _selfPlayerEntityId, _selfUsername, _currentMapName);
        }
    }

    internal void HandlePlayerCombatTick(JObject dataObj, HashSet<int> procsRecordedThisTick)
    {
        if (!CollectCombatDetails)
            return;

        lock (_sync)
        {
            if (IsClassSyncPending() ||
                (_selfPlayerEntityId <= 0 && string.IsNullOrWhiteSpace(_selfUsername)) ||
                string.IsNullOrWhiteSpace(_currentClassName))
                return;

            var cls = GetOrCreateClassDelta(_currentClassName);
            var level = Math.Max(1, _currentLevel);
            var now = DateTime.UtcNow;
            var sawExplicitHeal = false;
            var sawExplicitMonsterDmgToSelf = false;
            var sawSelfInflictedDmg = false;
            var hasSelfSarsaCast = false;
            var hasSaraInTick = dataObj["sara"] is JArray sArr && sArr.Count > 0;

            if (dataObj["a"] is JArray aArr)
            {
                foreach (var act in aArr.OfType<JObject>())
                    ProcessPlayerCombatEntry(act, null, 0, cls, level, now, procsRecordedThisTick, ref sawExplicitHeal, ref sawExplicitMonsterDmgToSelf, ref sawSelfInflictedDmg);
            }

            // Player skill action results under "sarsa"
            if (dataObj["sarsa"] is JArray sarsaArr)
            {
                foreach (var entry in sarsaArr.OfType<JObject>())
                {
                    var caster = entry["cInf"]?.ToString();
                    var outerActId = ParseInt(entry["actID"], 0);
                    if (!string.IsNullOrWhiteSpace(caster) && IsSelfEntityRef(caster))
                        hasSelfSarsaCast = true;
                    if (entry["a"] is JArray nested)
                    {
                        foreach (var act in nested.OfType<JObject>())
                            ProcessPlayerCombatEntry(act, caster, outerActId, cls, level, now, procsRecordedThisTick, ref sawExplicitHeal, ref sawExplicitMonsterDmgToSelf, ref sawSelfInflictedDmg);
                    }
                }
            }

            // Monster attacks, dodges, misses & DoT/HoT ticks under "sara" (actionResult entries)
            if (dataObj["sara"] is JArray saraArr)
            {
                foreach (var entry in saraArr.OfType<JObject>())
                {
                    var act = entry["actionResult"] as JObject ?? entry;
                    var outerCaster = entry["cInf"]?.ToString();
                    ProcessPlayerCombatEntry(act, outerCaster, 0, cls, level, now, procsRecordedThisTick, ref sawExplicitHeal, ref sawExplicitMonsterDmgToSelf, ref sawSelfInflictedDmg);
                }
            }

            // Fallback / complement using ct.p player HP deltas ONLY when explicit actions did not already cover the tick
            ProcessPlayerHpDeltasFromP(dataObj, cls, level, now, sawExplicitHeal, sawExplicitMonsterDmgToSelf, sawSelfInflictedDmg, hasSelfSarsaCast, hasSaraInTick);
        }
    }

    private void ProcessPlayerCombatEntry(
        JObject act,
        string? fallbackCaster,
        int outerActId,
        CapturedClassProfile cls,
        int level,
        DateTime now,
        HashSet<int> procsRecordedThisTick,
        ref bool sawExplicitHeal,
        ref bool sawExplicitMonsterDmgToSelf,
        ref bool sawSelfInflictedDmg)
    {
        if (IsAuraOnlyAction(act))
            return;

        var cInf = act["cInf"]?.ToString() ?? fallbackCaster ?? string.Empty;
        var tInf = act["tInf"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cInf) || string.IsNullOrWhiteSpace(tInf))
            return;

        var targets = SplitEntityRefs(tInf);
        if (targets.Length == 0)
            return;

        var hp = ParseInt(act["hp"] ?? act["d"]);
        var rawType = act["type"]?.ToString();
        var typ = act["typ"]?.ToString();
        // AQW sends HoT/DoT ticks in sara[].actionResult with "typ": "d" (no "type" field)
        var isDotHotTick = string.IsNullOrEmpty(rawType) && string.Equals(typ, "d", StringComparison.OrdinalIgnoreCase);
        var hitType = isDotHotTick
            ? (hp < 0 ? "hot" : "dot")
            : (rawType ?? "hit").ToLowerInvariant();

        if (hitType == "none" && hp == 0)
            return;

        // 1. Outgoing attacks from self player to one or more monsters
        var monsterTargetsCount = targets.Count(t => t.StartsWith("m:", StringComparison.OrdinalIgnoreCase));
        if (IsSelfEntityRef(cInf) && monsterTargetsCount > 0)
        {
            cls.LastSeenUtc = now;
            if (hitType is "d" or "dodge" or "m" or "miss")
            {
                cls.Combat.Misses++;
            }
            else if (hp > 0)
            {
                if (hitType == "crit")
                    cls.Combat.Crits++;
                else
                    cls.Combat.Hits++;

                cls.Combat.TotalDamageDealt += hp;
                cls.Combat.LevelSumForDamage += level;
                if (hp > cls.Combat.MaxSingleHit)
                    cls.Combat.MaxSingleHit = hp;

                // Weapon procs arrive with actRef="p<procId>" and actID=-1 (do not inflate denominator hits;
                // also catch aura-less procs like Health Vamp/Mana Vamp/Powerword Die when not already logged by aura)
                var procFromActRef = TryParseProcIdFromActRef(act["actRef"]?.ToString());
                var isWeaponProcAction = procFromActRef > 0 || outerActId == -1;
                if (!isWeaponProcAction && !isDotHotTick)
                {
                    RecordProcHitEquipped();
                }
                else if (procFromActRef > 0 && procsRecordedThisTick.Add(procFromActRef))
                {
                    RecordProcTriggerDelta(procFromActRef, WeaponProcCatalog.GetProcName(procFromActRef), 0, now);
                }
            }
            return;
        }

        // 2. Healing received/cast targeting self player (AQW sends negative hp, type="heal"/"hot", or typ="d" with hp < 0)
        var targetsSelf = targets.Any(IsSelfEntityRef);
        if (targetsSelf &&
            cInf.StartsWith("p:", StringComparison.OrdinalIgnoreCase) &&
            (hitType is "heal" or "hot" || hp < 0))
        {
            var healed = Math.Abs(hp);
            if (healed > 0)
            {
                cls.LastSeenUtc = now;
                cls.Combat.HealEvents++;
                cls.Combat.TotalHealingDone += healed;
                cls.Combat.LevelSumForHeal += level;
                sawExplicitHeal = true;
            }
            return;
        }

        // 2b. Self-inflicted damage / self-DoT (e.g. VHL Shackle/Unshackle HP sacrifice) — never attribute to monsters!
        if (targetsSelf && IsSelfEntityRef(cInf) && hp > 0)
        {
            sawSelfInflictedDmg = true;
            return;
        }

        // 3. Incoming monster attacks against self player (supports multi-target comma-separated tInf and sara[].actionResult)
        if (cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase) && targetsSelf)
        {
            cls.LastSeenUtc = now;
            if (isDotHotTick)
            {
                if (hp > 0)
                {
                    cls.Combat.TotalDamageTaken += hp;
                    sawExplicitMonsterDmgToSelf = true;
                }
                return;
            }

            cls.Combat.AttacksTaken++;
            if (hitType is "d" or "dodge" or "m" or "miss")
            {
                cls.Combat.DodgesTaken++;
            }
            else if (hp > 0)
            {
                cls.Combat.TotalDamageTaken += hp;
                sawExplicitMonsterDmgToSelf = true;
            }
        }
    }

    private static int TryParseProcIdFromActRef(string? actRef)
    {
        if (string.IsNullOrWhiteSpace(actRef) || actRef.Length < 2)
            return 0;
        if ((actRef[0] == 'p' || actRef[0] == 'P') &&
            int.TryParse(actRef.AsSpan(1), out var procId) &&
            procId >= 2 && procId <= 20)
        {
            return procId;
        }
        return 0;
    }

    private CapturedClassProfile GetOrCreateClassDelta(string className)
    {
        if (!_delta.Classes.TryGetValue(className, out var cls))
        {
            cls = new CapturedClassProfile
            {
                Name = className,
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };
            _delta.Classes[className] = cls;
        }
        return cls;
    }

    private void RecordStatSnapshotDelta(
        CapturedClassProfile cls,
        int level,
        int maxHealth,
        int str,
        int intl,
        int end,
        int dex,
        int wis,
        int lck,
        int ap,
        int sp,
        double haste,
        double critChance,
        double critMult,
        double evasionChance)
    {
        if (str <= 0 && intl <= 0 && end <= 0 && dex <= 0 && wis <= 0 && lck <= 0 && ap <= 0 && sp <= 0)
            return;

        cls.SamplesObserved++;
        cls.LastSeenUtc = DateTime.UtcNow;

        cls.InferredStatModel = InferStatModel(intl, str, dex, wis, lck, end);

        var sig = $"W{_weaponEnhPattern}:{_weaponEnhLevel}|C{_classEnhPattern}:{_classEnhLevel}|H{_helmEnhPattern}:{_helmEnhLevel}|K{_capeEnhPattern}:{_capeEnhLevel}";
        cls.StatsByLevel[level] = new CapturedLevelStatSnapshot
        {
            Level = level,
            MaxHealth = maxHealth,
            Samples = cls.StatsByLevel.TryGetValue(level, out var prev) ? prev.Samples + 1 : 1,
            Strength = str,
            Intellect = intl,
            Endurance = end,
            Dexterity = dex,
            Wisdom = wis,
            Luck = lck,
            AttackPower = ap,
            SpellPower = sp,
            Haste = haste,
            CriticalChance = critChance,
            CriticalMultiplier = critMult > 0 ? critMult : 1.5,
            EvasionChance = evasionChance,
            EnhancementSignature = sig
        };

        // If at least 2 enhanced gear slots share the same non-zero PatternID at a consistent EnhLevel,
        // record a clean reference snapshot for that enhancement pattern!
        var activeSlots = new[]
        {
            (_weaponEnhPattern, _weaponEnhLevel),
            (_classEnhPattern, _classEnhLevel),
            (_helmEnhPattern, _helmEnhLevel),
            (_capeEnhPattern, _capeEnhLevel)
        }.Where(x => x.Item1 > 0 && x.Item2 > 0).ToList();

        if (activeSlots.Count >= 2 &&
            activeSlots.All(x => x.Item1 == activeSlots[0].Item1 && x.Item2 == activeSlots[0].Item2))
        {
            var patternId = activeSlots[0].Item1;
            var enhLvl = activeSlots[0].Item2;
            if (!_delta.Enhancements.TryGetValue(patternId, out var enh))
            {
                enh = new CapturedEnhancementProfile
                {
                    PatternID = patternId,
                    Name = EnhancementPatternCatalog.GetPatternName(patternId),
                    FirstSeenUtc = DateTime.UtcNow
                };
                _delta.Enhancements[patternId] = enh;
            }
            enh.Observations++;
            enh.LastSeenUtc = DateTime.UtcNow;
            enh.PureSetStatsByLevel[enhLvl] = new CapturedEnhancementLevelObservation
            {
                EnhancementLevel = enhLvl,
                ReferenceClass = cls.Name,
                Samples = enh.PureSetStatsByLevel.TryGetValue(enhLvl, out var prevObs) ? prevObs.Samples + 1 : 1,
                Strength = str,
                Intellect = intl,
                Endurance = end,
                Dexterity = dex,
                Wisdom = wis,
                Luck = lck
            };
        }
    }

    private static string InferStatModel(int intl, int str, int dex, int wis, int lck, int end)
    {
        if (intl > str * 1.4 && intl >= dex)
            return wis >= end ? "Offensive Caster" : "Power Caster";
        if (dex > str * 1.25 && dex > intl)
            return "Dodge Melee";
        if (str > intl * 1.4)
            return end > dex * 1.2 ? "Tank Melee" : "Power Melee";
        if (lck >= str && lck >= intl)
            return "Luck Hybrid";
        return "Full Hybrid";
    }

    private static CapturedClassSkill BuildSkillModel(
        int slot,
        int actId,
        int skillId,
        string name,
        string? description,
        string? icon,
        string? type,
        double damage,
        int cooldownMs,
        int mpCost,
        int range,
        int minTargets,
        int maxTargets,
        bool auto)
    {
        var desc = description ?? string.Empty;
        var typ = type ?? string.Empty;
        var isHeal = damage < 0 ||
                     typ.Contains('h', StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("heal", StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("restore", StringComparison.OrdinalIgnoreCase) ||
                     desc.Contains("HoT", StringComparison.Ordinal);
        var isDotHot = desc.Contains("DoT", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("over time", StringComparison.OrdinalIgnoreCase) ||
                       desc.Contains("HoT", StringComparison.Ordinal);
        var isSelfBuff = typ.Equals("i", StringComparison.OrdinalIgnoreCase) ||
                         (damage == 0 && !isHeal &&
                          (desc.Contains("increase", StringComparison.OrdinalIgnoreCase) ||
                           desc.Contains("haste", StringComparison.OrdinalIgnoreCase) ||
                           desc.Contains("dodge", StringComparison.OrdinalIgnoreCase) ||
                           desc.Contains("crit", StringComparison.OrdinalIgnoreCase)));

        return new CapturedClassSkill
        {
            Slot = slot,
            ActID = actId,
            SkillID = skillId,
            Name = name,
            Description = desc,
            Icon = icon ?? string.Empty,
            Type = typ,
            Damage = damage,
            CooldownMs = Math.Max(0, cooldownMs),
            MpCost = Math.Max(0, mpCost),
            Range = range > 0 ? range : 300,
            MinTargets = Math.Max(1, minTargets),
            MaxTargets = Math.Max(1, maxTargets),
            Auto = auto || slot == 0,
            IsHealHint = isHeal,
            IsDotOrHotHint = isDotHot,
            IsSelfBuffHint = isSelfBuff
        };
    }

    private static double ParseDoubleSafe(JToken? token, double fallback = 0)
    {
        if (token == null || token.Type == JTokenType.Null)
            return fallback;
        if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            return token.Value<double>();
        return double.TryParse(token.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : fallback;
    }
}
