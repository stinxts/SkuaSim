using Newtonsoft.Json.Linq;
using Skua.Core.Models.Players;
using Skua.Core.Models.Skills;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

public sealed partial class PacketCollectorEngine
{
    // Strictly ephemeral in-memory player state (NEVER persisted to GameDatabase / disk).
    private int _selfPlayerEntityId;
    private string _currentClassName = string.Empty;
    private int _currentLevel;
    private int _currentMaxHealth;
    private int _weaponEnhPattern;
    private int _weaponEnhLevel;
    private int _classEnhPattern;
    private int _classEnhLevel;
    private int _helmEnhPattern;
    private int _helmEnhLevel;
    private int _capeEnhPattern;
    private int _capeEnhLevel;

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
        int capeEnhLevel = 0)
    {
        lock (_sync)
        {
            if (selfEntityId is > 0)
                _selfPlayerEntityId = selfEntityId.Value;
            if (!string.IsNullOrWhiteSpace(className))
                _currentClassName = className.Trim();
            if (level > 0)
                _currentLevel = Math.Clamp(level, 1, 100);
            if (maxHealth > 0)
                _currentMaxHealth = maxHealth;

            _weaponEnhPattern = Math.Max(0, weaponEnhPattern);
            _weaponEnhLevel = Math.Max(0, weaponEnhLevel);
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
        int capeEnhLevel = 0)
    {
        SetPlayerCombatContext(
            selfEntityId, className, level, maxHealth,
            weaponEnhPattern, weaponEnhLevel,
            classEnhPattern, classEnhLevel,
            helmEnhPattern, helmEnhLevel,
            capeEnhPattern, capeEnhLevel);

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

    internal void HandleSkillActionsPacket(JObject dataObj)
    {
        var actions = dataObj["actions"] as JObject;
        if (actions == null)
            return;

        lock (_sync)
        {
            var className = dataObj["sClass"]?.ToString();
            if (string.IsNullOrWhiteSpace(className))
                className = _currentClassName;
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
        }
    }

    internal void HandleStatUpdatePacket(JObject dataObj)
    {
        var sta = dataObj["sta"] as JObject ?? dataObj;
        if (sta["$STR"] == null && sta["$INT"] == null && sta["$ap"] == null && sta["$sp"] == null)
            return;

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_currentClassName) || _currentLevel <= 0)
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
        }
    }

    internal void HandlePlayerCombatTick(JObject dataObj)
    {
        if (!CollectCombatDetails)
            return;

        lock (_sync)
        {
            if (_selfPlayerEntityId <= 0 || string.IsNullOrWhiteSpace(_currentClassName))
                return;

            var selfRef = $"p:{_selfPlayerEntityId}";
            var cls = GetOrCreateClassDelta(_currentClassName);
            var level = Math.Max(1, _currentLevel);

            if (dataObj["a"] is JArray aArr)
            {
                foreach (var act in aArr.OfType<JObject>())
                    ProcessPlayerCombatEntry(act, null, selfRef, cls, level);
            }

            // Newer AQW combat payloads may place nested "a" under "sarsa"
            if (dataObj["sarsa"] is JArray sarsaArr)
            {
                foreach (var entry in sarsaArr.OfType<JObject>())
                {
                    var caster = entry["cInf"]?.ToString();
                    if (entry["a"] is JArray nested)
                    {
                        foreach (var act in nested.OfType<JObject>())
                            ProcessPlayerCombatEntry(act, caster, selfRef, cls, level);
                    }
                }
            }
        }
    }

    private static void ProcessPlayerCombatEntry(
        JObject act,
        string? fallbackCaster,
        string selfRef,
        CapturedClassProfile cls,
        int level)
    {
        var cInf = act["cInf"]?.ToString() ?? fallbackCaster ?? string.Empty;
        var tInf = act["tInf"]?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cInf) || string.IsNullOrWhiteSpace(tInf))
            return;

        var hitType = (act["type"]?.ToString() ?? "hit").ToLowerInvariant();
        var hp = ParseInt(act["hp"]);
        var now = DateTime.UtcNow;

        // 1. Outgoing attacks from self player to a monster
        if (string.Equals(cInf, selfRef, StringComparison.OrdinalIgnoreCase) &&
            tInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
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
            }
            return;
        }

        // 2. Healing received/cast by self player (AQW sends negative hp or type="heal" targeting player)
        if (string.Equals(tInf, selfRef, StringComparison.OrdinalIgnoreCase) &&
            cInf.StartsWith("p:", StringComparison.OrdinalIgnoreCase) &&
            (hitType == "heal" || hp < 0))
        {
            var healed = Math.Abs(hp);
            if (healed > 0)
            {
                cls.LastSeenUtc = now;
                cls.Combat.HealEvents++;
                cls.Combat.TotalHealingDone += healed;
                cls.Combat.LevelSumForHeal += level;
            }
            return;
        }

        // 3. Incoming monster attacks against self player (for class dodge/mitigation rates)
        if (cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tInf, selfRef, StringComparison.OrdinalIgnoreCase))
        {
            cls.LastSeenUtc = now;
            cls.Combat.AttacksTaken++;
            if (hitType is "d" or "dodge" or "m" or "miss")
                cls.Combat.DodgesTaken++;
            else if (hp > 0)
                cls.Combat.TotalDamageTaken += hp;
        }
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

        if (string.IsNullOrWhiteSpace(cls.InferredStatModel))
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
