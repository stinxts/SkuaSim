using Newtonsoft.Json;

namespace SkuaSim.Collector.Models;

/// <summary>
/// Root database containing all passively collected AQW/Skua game metadata.
/// Strictly anonymized: contains zero account names, player names, chat logs, or personal inventory states.
/// </summary>
public sealed class GameDatabase
{
    /// <summary>
    /// v2 (2026-11-20): quest-gated drop precision (RequiredQuestIds, TrackedDropEvents, KillsWhileQuestActive,
    /// RateBasis), passive official drops (IsOfficialDrop), room MaxPlayers, quest-gated map items, quest reward
    /// types, monster level/WeaponDps. Files written before v2 deserialize with SchemaVersion = 1 and are migrated
    /// (with a dated backup) by GameDatabaseStore on the next flush.
    /// <para>
    /// v3 (2026-11-21): base XP/gold/reputation per kill (<see cref="CapturedKillRewards"/>, from "addGoldExp"),
    /// respawn times (<see cref="CapturedRespawnStats"/>, kill → "respawnMon"), kills keyed by the exact active quest
    /// set (<see cref="CapturedMonster.KillsByActiveQuestSet"/>, fixes double counting when several required quests
    /// are active), and room occupancy (<see cref="CapturedMap.PeakPlayersObserved"/>) replacing the unreachable
    /// client-side room cap. Additive v3 fields (no migration needed, older files load them empty): quest
    /// <see cref="CapturedQuest.ObservedReward"/> and the faction ID → name catalog (<see cref="Factions"/>).
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public long TotalFlushesMerged { get; set; }

    /// <summary>Human-readable record of schema migrations applied to this file.</summary>
    public List<string> MigrationLog { get; set; } = new();

    public Dictionary<string, CapturedMap> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CapturedMonster> Monsters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, CapturedItem> Items { get; set; } = new();
    public Dictionary<int, CapturedShop> Shops { get; set; } = new();
    public Dictionary<int, CapturedQuest> Quests { get; set; } = new();
    public Dictionary<string, CapturedClassProfile> Classes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, CapturedEnhancementProfile> Enhancements { get; set; } = new();
    public Dictionary<int, CapturedProcStat> Procs { get; set; } = new();

    /// <summary>
    /// AQW XP table: level -> XP needed to reach the next level, from "levelUp" { intLevel, intExpToLevel } (game data;
    /// the player's own XP is not stored). Additive in v3.
    /// </summary>
    public Dictionary<int, int> XpToNextLevel { get; set; } = new();

    /// <summary>
    /// Gold/ACs returned when selling an item: server "sellItem" { intAmount, bCoins, iQty } paired with the client's
    /// preceding "sellItem" request (item ID). Item ID -> last observed amount. Additive in v3.
    /// </summary>
    public Dictionary<int, CapturedSellPrice> SellPrices { get; set; } = new();

    /// <summary>Faction ID -> name (additive in v3; no player reputation is stored).</summary>
    public Dictionary<int, CapturedFaction> Factions { get; set; } = new();

    /// <summary>
    /// Collector build -> number of flushes it merged into this file (additive in v3), e.g. <c>"1.1.0+19e923d": 812</c>.
    /// Files from before 1.1.0 have none.
    /// </summary>
    public Dictionary<string, long> CollectorVersions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>PvP events and scores per map ("PVPE"/"PVPS"; additive in v3). Key "unknown" when the map was not known yet.</summary>
    public Dictionary<string, CapturedPvpStats> Pvp { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set only in a file exported for sharing: a random ID made once per collector install (not derived from the
    /// account, the PC or the user), so SkuaSim can replace a tester's older export with the newer one instead of
    /// counting the same kills twice. Never present in the collector's own gamedata.json.
    /// </summary>
    public string? ContributorId { get; set; }

    /// <summary>Set only in a file exported for sharing: when it was exported (UTC, whole day).</summary>
    public DateTime? ExportedUtc { get; set; }

    public static string NormalizeMapName(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return string.Empty;
        var trimmed = mapName.Trim();
        var dashIdx = trimmed.IndexOf('-');
        if (dashIdx > 0)
            trimmed = trimmed[..dashIdx];
        return trimmed.ToLowerInvariant();
    }

    public static string GetMonsterKey(int monsterId, string? monsterName)
    {
        if (monsterId > 0)
            return $"id:{monsterId}";
        var norm = (monsterName ?? "unknown").Trim().ToLowerInvariant();
        return $"name:{norm}";
    }
}

public sealed class CapturedMap
{
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Visits { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Maps cell name (e.g. "Enter", "r2") to the set of pads observed in that cell (e.g. "Spawn", "Left").
    /// </summary>
    public Dictionary<string, HashSet<string>> Cells { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monster spawns indexed by their per-map instance ID (MonMapID).
    /// </summary>
    public Dictionary<int, CapturedMapMonsterSpawn> Monsters { get; set; } = new();

    /// <summary>
    /// Map items requested via getMapItem on this map, indexed by MapItemID.
    /// </summary>
    public Dictionary<int, CapturedMapItem> MapItems { get; set; } = new();

    /// <summary>
    /// Zone events (cmd="event", args.zoneSet) observed on this map (e.g. UltraDage, DarkCarnax).
    /// </summary>
    public Dictionary<string, CapturedZoneEvent> ZoneEvents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Highest player count seen in a room of this map (Skua Map.PlayerCount, sampled on join and periodically).
    /// A lower bound of the room cap, which the client does not expose (the "world.areaData.intMaxPlayers" probe
    /// was missing in every room). 0 = never sampled.
    /// </summary>
    public int PeakPlayersObserved { get; set; }

    public DateTime? PeakPlayersObservedUtc { get; set; }

    /// <summary>Number of player-count samples taken on this map.</summary>
    public long PlayerCountSamples { get; set; }

    /// <summary>
    /// Cell number → cell name, learned from the client's "move to cell by number" request (%xt%zm%mtcid%room%N%,
    /// used by CoreBots.PvPMove in Bludrut Brawl) paired with the cell the character is in right after it.
    /// Each number keeps the votes per cell name; the most voted name is the answer.
    /// </summary>
    public Dictionary<int, Dictionary<string, long>> CellNumbers { get; set; } = new();

    /// <summary>The most voted cell name for a cell number (null when unknown).</summary>
    public string? CellForNumber(int number) =>
        CellNumbers.TryGetValue(number, out var votes) && votes.Count > 0
            ? votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key
            : null;
}

public sealed class CapturedMapMonsterSpawn
{
    public int MapID { get; set; }
    public int MonsterID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;
    public int Level { get; set; }
    public int MaxHP { get; set; }
    public string Race { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Server-side monster weapon DPS from moveToArea "monBranch[].wDPS" (0 when unknown).</summary>
    public int WeaponDps { get; set; }

    /// <summary>Respawn delay of this spawn (kill → "respawnMon" with this MonMapID).</summary>
    public CapturedRespawnStats Respawn { get; set; } = new();
}

/// <summary>Respawn delay samples: time from a recorded kill to the server's "respawnMon" for the same MonMapID.</summary>
public sealed class CapturedRespawnStats
{
    public long Samples { get; set; }
    public long TotalMs { get; set; }
    public long? MinMs { get; set; }
    public long? MaxMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageMs => Samples > 0 ? Math.Round((double)TotalMs / Samples, 0) : 0.0;

    public void Add(long ms)
    {
        Samples++;
        TotalMs += ms;
        MinMs = MinMs.HasValue ? Math.Min(MinMs.Value, ms) : ms;
        MaxMs = MaxMs.HasValue ? Math.Max(MaxMs.Value, ms) : ms;
    }

    public void Merge(CapturedRespawnStats? other)
    {
        if (other == null || other.Samples <= 0)
            return;
        Samples += other.Samples;
        TotalMs += other.TotalMs;
        if (other.MinMs.HasValue) MinMs = MinMs.HasValue ? Math.Min(MinMs.Value, other.MinMs.Value) : other.MinMs;
        if (other.MaxMs.HasValue) MaxMs = MaxMs.HasValue ? Math.Max(MaxMs.Value, other.MaxMs.Value) : other.MaxMs;
    }
}

/// <summary>
/// Rewards per monster kill from "addGoldExp" (typ "m"). Values are stored as base values: the packet's intExp/intGold/iRep
/// already include boosts and membership bonus, which arrive separately as bonusExp/bonusGold/bonusRep
/// (e.g. intGold 2500 with bonusGold 1500 = base 1000). A 0 means the reward was not granted (e.g. a max-level
/// character gets intExp 0, a gold-capped one intGold 0): those are counted apart and never averaged in.
/// </summary>
public sealed class CapturedKillRewards
{
    public long Samples { get; set; }

    public long ExpSamples { get; set; }
    public long TotalBaseExp { get; set; }
    public int? MinBaseExp { get; set; }
    public int? MaxBaseExp { get; set; }
    public long ZeroExpSamples { get; set; }

    public long GoldSamples { get; set; }
    public long TotalBaseGold { get; set; }
    public int? MinBaseGold { get; set; }
    public int? MaxBaseGold { get; set; }
    public long ZeroGoldSamples { get; set; }

    public long RepSamples { get; set; }
    public long TotalBaseRep { get; set; }
    public HashSet<int> FactionIds { get; set; } = new();

    /// <summary>Class points per kill ("iCP" minus "bonusCP"; only sent while the class is below rank 10).</summary>
    public long CpSamples { get; set; }
    public long TotalBaseCp { get; set; }
    public int? MinBaseCp { get; set; }
    public int? MaxBaseCp { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageBaseCp => CpSamples > 0 ? Math.Round((double)TotalBaseCp / CpSamples, 1) : 0.0;

    /// <summary>
    /// Base class points per kill split by the killer's character level (a number only, no identity), to test whether
    /// CP depends on the character level rather than the monster level (observed: same 104-455 range on monsters of
    /// level 15-255 while an account leveled 11 -> 40).
    /// </summary>
    public Dictionary<int, CapturedCpStat> CpByPlayerLevel { get; set; } = new();

    /// <summary>
    /// Base CP per kill keyed by "class rank|level gap" (gap = character level - monster level), e.g. "4|0" or "9|25".
    /// AQW: CP = 230 + 25 x class rank, reduced when the monster is far below the character (the exact penalty curve
    /// is measured here: sources disagree between -5 %/level and -10 %/level from 11 levels).
    /// </summary>
    public Dictionary<string, CapturedCpStat> CpByRankAndLevelGap { get; set; } = new(StringComparer.Ordinal);

    public void AddClassPointsContext(int baseCp, int classRank, int levelGap)
    {
        if (baseCp <= 0 || classRank <= 0)
            return;
        var key = $"{classRank}|{levelGap}";
        if (!CpByRankAndLevelGap.TryGetValue(key, out var stat))
            CpByRankAndLevelGap[key] = stat = new CapturedCpStat();
        stat.Add(baseCp);
    }

    public void AddClassPoints(int totalCp, int bonusCp, int playerLevel = 0)
    {
        if (totalCp <= 0)
            return;
        var cp = Math.Max(0, totalCp - Math.Max(0, bonusCp));
        CpSamples++;
        TotalBaseCp += cp;
        MinBaseCp = MinBaseCp.HasValue ? Math.Min(MinBaseCp.Value, cp) : cp;
        MaxBaseCp = MaxBaseCp.HasValue ? Math.Max(MaxBaseCp.Value, cp) : cp;
        if (playerLevel > 0)
        {
            if (!CpByPlayerLevel.TryGetValue(playerLevel, out var stat))
                CpByPlayerLevel[playerLevel] = stat = new CapturedCpStat();
            stat.Add(cp);
        }
    }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageBaseExp => ExpSamples > 0 ? Math.Round((double)TotalBaseExp / ExpSamples, 1) : 0.0;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageBaseGold => GoldSamples > 0 ? Math.Round((double)TotalBaseGold / GoldSamples, 1) : 0.0;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageBaseRep => RepSamples > 0 ? Math.Round((double)TotalBaseRep / RepSamples, 1) : 0.0;

    public void Add(int totalExp, int bonusExp, int totalGold, int bonusGold, int totalRep, int bonusRep, int factionId)
    {
        Samples++;
        if (totalExp > 0)
        {
            var exp = Math.Max(0, totalExp - Math.Max(0, bonusExp));
            ExpSamples++;
            TotalBaseExp += exp;
            MinBaseExp = MinBaseExp.HasValue ? Math.Min(MinBaseExp.Value, exp) : exp;
            MaxBaseExp = MaxBaseExp.HasValue ? Math.Max(MaxBaseExp.Value, exp) : exp;
        }
        else
            ZeroExpSamples++;

        if (totalGold > 0)
        {
            var gold = Math.Max(0, totalGold - Math.Max(0, bonusGold));
            GoldSamples++;
            TotalBaseGold += gold;
            MinBaseGold = MinBaseGold.HasValue ? Math.Min(MinBaseGold.Value, gold) : gold;
            MaxBaseGold = MaxBaseGold.HasValue ? Math.Max(MaxBaseGold.Value, gold) : gold;
        }
        else
            ZeroGoldSamples++;

        if (totalRep > 0)
        {
            RepSamples++;
            TotalBaseRep += Math.Max(0, totalRep - Math.Max(0, bonusRep));
            if (factionId > 0)
                FactionIds.Add(factionId);
        }
    }

    public void Merge(CapturedKillRewards? other)
    {
        if (other == null || other.Samples <= 0)
            return;
        Samples += other.Samples;
        ExpSamples += other.ExpSamples;
        TotalBaseExp += other.TotalBaseExp;
        if (other.MinBaseExp.HasValue) MinBaseExp = MinBaseExp.HasValue ? Math.Min(MinBaseExp.Value, other.MinBaseExp.Value) : other.MinBaseExp;
        if (other.MaxBaseExp.HasValue) MaxBaseExp = MaxBaseExp.HasValue ? Math.Max(MaxBaseExp.Value, other.MaxBaseExp.Value) : other.MaxBaseExp;
        ZeroExpSamples += other.ZeroExpSamples;
        GoldSamples += other.GoldSamples;
        TotalBaseGold += other.TotalBaseGold;
        if (other.MinBaseGold.HasValue) MinBaseGold = MinBaseGold.HasValue ? Math.Min(MinBaseGold.Value, other.MinBaseGold.Value) : other.MinBaseGold;
        if (other.MaxBaseGold.HasValue) MaxBaseGold = MaxBaseGold.HasValue ? Math.Max(MaxBaseGold.Value, other.MaxBaseGold.Value) : other.MaxBaseGold;
        ZeroGoldSamples += other.ZeroGoldSamples;
        RepSamples += other.RepSamples;
        TotalBaseRep += other.TotalBaseRep;
        FactionIds.UnionWith(other.FactionIds);
        CpSamples += other.CpSamples;
        TotalBaseCp += other.TotalBaseCp;
        if (other.MinBaseCp.HasValue) MinBaseCp = MinBaseCp.HasValue ? Math.Min(MinBaseCp.Value, other.MinBaseCp.Value) : other.MinBaseCp;
        if (other.MaxBaseCp.HasValue) MaxBaseCp = MaxBaseCp.HasValue ? Math.Max(MaxBaseCp.Value, other.MaxBaseCp.Value) : other.MaxBaseCp;
        CpByPlayerLevel ??= new();
        foreach (var (level, stat) in other.CpByPlayerLevel ?? new())
        {
            if (!CpByPlayerLevel.TryGetValue(level, out var dst))
                CpByPlayerLevel[level] = dst = new CapturedCpStat();
            dst.Merge(stat);
        }
        CpByRankAndLevelGap ??= new(StringComparer.Ordinal);
        foreach (var (key, stat) in other.CpByRankAndLevelGap ?? new())
        {
            if (!CpByRankAndLevelGap.TryGetValue(key, out var dst))
                CpByRankAndLevelGap[key] = dst = new CapturedCpStat();
            dst.Merge(stat);
        }
    }
}

public sealed class CapturedCpStat
{
    public long Samples { get; set; }
    public long Total { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double Average => Samples > 0 ? Math.Round((double)Total / Samples, 1) : 0.0;

    public void Add(int cp)
    {
        Samples++;
        Total += cp;
        Min = Min.HasValue ? Math.Min(Min.Value, cp) : cp;
        Max = Max.HasValue ? Math.Max(Max.Value, cp) : cp;
    }

    public void Merge(CapturedCpStat? other)
    {
        if (other == null || other.Samples <= 0)
            return;
        Samples += other.Samples;
        Total += other.Total;
        if (other.Min.HasValue) Min = Min.HasValue ? Math.Min(Min.Value, other.Min.Value) : other.Min;
        if (other.Max.HasValue) Max = Max.HasValue ? Math.Max(Max.Value, other.Max.Value) : other.Max;
    }
}

public sealed class CapturedMapItem
{
    public int MapItemID { get; set; }
    public HashSet<string> CellsSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long TimesRequested { get; set; }
    public HashSet<int> GrantedItemIDs { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Quests that use the granted items (from the items' "sReqQuests"): the map item only works while one is active.</summary>
    public HashSet<int> RequiredQuestIds { get; set; } = new();

    /// <summary>Successful grants observed while the active quest list was known.</summary>
    public long GrantObservations { get; set; }

    /// <summary>Quest ID -> how many of those successful grants happened with that quest active.</summary>
    public Dictionary<int, long> ActiveQuestCounts { get; set; } = new();
}

public sealed class CapturedMonster
{
    public string Key { get; set; } = string.Empty;
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public int MaxHP { get; set; }
    public string Race { get; set; } = string.Empty;
    public string Element { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Server-side monster weapon DPS from moveToArea "monBranch[].wDPS" (0 when unknown).</summary>
    public int WeaponDps { get; set; }

    /// <summary>
    /// Maps map name -> set of cells where this monster spawns.
    /// </summary>
    public Dictionary<string, HashSet<string>> MapsSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long KillCount { get; set; }

    /// <summary>Kills recorded while the player's active quest list was known (v2+ collectors only).</summary>
    public long TrackedKills { get; set; }

    /// <summary>
    /// Legacy (v2 collectors, frozen since v3): quest ID -> tracked kills with that quest active. Summing it over an
    /// item's quests double-counts kills made with several of them active, so <see cref="DropRates"/> only uses the
    /// maximum of these legacy counts (a lower bound) on top of <see cref="KillsByActiveQuestSet"/>.
    /// </summary>
    public Dictionary<int, long> KillsWhileQuestActive { get; set; } = new();

    /// <summary>
    /// Tracked kills keyed by the exact set of active quest IDs at kill time ("" = none active; ascending IDs joined
    /// with ","). A quest-gated drop's denominator is the sum over sets containing any of its required quests, so each
    /// kill counts once.
    /// </summary>
    public Dictionary<string, long> KillsByActiveQuestSet { get; set; } = new(StringComparer.Ordinal);

    /// <summary>XP/gold/reputation per kill (from "addGoldExp" typ "m"), as base values without boosts.</summary>
    public CapturedKillRewards KillRewards { get; set; } = new();

    /// <summary>Respawn delay across all spawns of this monster (per-spawn values are on the map's spawn entry).</summary>
    public CapturedRespawnStats Respawn { get; set; } = new();

    /// <summary>Last time an official "monsterDrops" list was observed for this monster.</summary>
    public DateTime? OfficialDropsObservedUtc { get; set; }

    public CapturedCombatStats Combat { get; set; } = new();
    public Dictionary<string, CapturedAuraStat> Auras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CapturedMonsterSkillEvent> SkillEvents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CapturedZoneEvent> ZoneEvents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> SpecialMessages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, CapturedDropStat> Drops { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedCombatStats
{
    public long AttacksObserved { get; set; }
    public long Hits { get; set; }
    public long Crits { get; set; }
    public long Dodges { get; set; }
    public long Misses { get; set; }
    public long TotalDamage { get; set; }
    public int? MinDamage { get; set; }
    public int? MaxDamage { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageDamage => (Hits + Crits) > 0
        ? Math.Round((double)TotalDamage / (Hits + Crits), 1)
        : 0.0;

    public long AttackIntervalSamples { get; set; }
    public long TotalAttackIntervalMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageAttackIntervalMs => AttackIntervalSamples > 0
        ? Math.Round((double)TotalAttackIntervalMs / AttackIntervalSamples, 0)
        : 0.0;

    public long CounterAttacksObserved { get; set; }
    public long PlayerDeathsInCell { get; set; }
    public int MaxTargetsHit { get; set; }
}

public sealed class CapturedAuraStat
{
    public string Name { get; set; } = string.Empty;
    public string CasterType { get; set; } = string.Empty; // "monster" or "player"
    public string TargetType { get; set; } = string.Empty; // "monster" or "player"
    public string Category { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public float? MinValue { get; set; }
    public float? MaxValue { get; set; }
    public long TimesSeen { get; set; }
    public bool IsCounterAttack { get; set; }
    public bool IsTaunt { get; set; }
    public bool IsStacking { get; set; }
    public int MaxStacksObserved { get; set; }
    public long IntervalSamples { get; set; }
    public long TotalIntervalMs { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double AverageIntervalMs => IntervalSamples > 0
        ? Math.Round((double)TotalIntervalMs / IntervalSamples, 0)
        : 0.0;

    public double? MinBossHpPercent { get; set; }
    public double? MaxBossHpPercent { get; set; }
}

public sealed class CapturedDropStat
{
    public int ItemID { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool Temp { get; set; }
    public long DropEvents { get; set; }

    /// <summary>
    /// Legacy, unreliable: sums the packet's "iQty", which for a stackable item the character already owns is the stack
    /// total, not the amount dropped (Undead Energy averaged ~1,400 per drop). Kept for history; never use it as a
    /// drop size — use <see cref="MeasuredQuantity"/> / <see cref="QuantitySamples"/>.
    /// </summary>
    public long TotalQuantity { get; set; }

    /// <summary>Sum of the amounts actually dropped ("dQty" in dropItem; "iQty" of an addItems entry that has "iQtyNow").</summary>
    public long MeasuredQuantity { get; set; }

    /// <summary>Drops whose amount was measured (denominator of the average drop size).</summary>
    public long QuantitySamples { get; set; }

    /// <summary>Average amount per drop when measured, else null (unknown: the simulator uses 1).</summary>
    public double? AverageQuantity => QuantitySamples > 0 ? (double)MeasuredQuantity / QuantitySamples : null;

    /// <summary>
    /// Kills that could have produced this drop (denominator of the rate), recomputed by <see cref="DropRates"/>:
    /// all kills for normal drops, kills with a required quest active for quest-gated drops.
    /// </summary>
    public long EligibleKills { get; set; }

    /// <summary>Confirmed by the server's official "monsterDrops" list.</summary>
    public bool IsOfficialDrop { get; set; }

    /// <summary>Only drops while one of <see cref="RequiredQuestIds"/> is active (item "sReqQuests" / monsterDrops "questGated").</summary>
    public bool QuestGated { get; set; }

    /// <summary>Quest IDs that use this item (from the item's "sReqQuests").</summary>
    public HashSet<int> RequiredQuestIds { get; set; } = new();

    /// <summary>Quest names from monsterDrops "questGated" (the server sends names, not IDs).</summary>
    public HashSet<string> QuestGatedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quest names from monsterDrops "questObjective".</summary>
    public HashSet<string> QuestObjectives { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drop events recorded while the active quest list was known (numerator for quest-gated rates).</summary>
    public long TrackedDropEvents { get; set; }

    /// <summary>
    /// "all_kills" (normal drop), "quest_active_kills" (quest-gated, measured with the quest active) or
    /// "unverified" (quest-gated but not yet measured with the quest active, or never observed dropping).
    /// </summary>
    public string RateBasis { get; set; } = DropRates.AllKills;

    /// <summary>Observed drop chance, or null when there is no trustworthy measurement yet (simulators must not use 0).</summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double? EstimatedDropChance => RateBasis switch
    {
        DropRates.QuestActiveKills when EligibleKills > 0 && TrackedDropEvents > 0 =>
            Math.Min(1.0, Math.Round((double)TrackedDropEvents / EligibleKills, 4)),
        DropRates.AllKills when EligibleKills > 0 && DropEvents > 0 =>
            Math.Min(1.0, Math.Round((double)DropEvents / EligibleKills, 4)),
        _ => null
    };
}

/// <summary>Single place where drop-rate denominators and bases are derived (engine deltas and store merges).</summary>
public static class DropRates
{
    public const string AllKills = "all_kills";
    public const string QuestActiveKills = "quest_active_kills";
    public const string Unverified = "unverified";

    /// <summary>Key of <see cref="CapturedMonster.KillsByActiveQuestSet"/>: ascending distinct positive IDs joined with ",".</summary>
    public static string QuestSetKey(IEnumerable<int> questIds) =>
        string.Join(",", questIds.Where(id => id > 0).Distinct().OrderBy(id => id));

    /// <summary>
    /// Kills that could have produced a quest-gated drop: v3 kills whose active quest set contains any required quest
    /// (each kill once) plus, for v2 legacy counts, the largest per-quest count (a lower bound of their union).
    /// </summary>
    public static long QuestActiveEligibleKills(CapturedMonster monster, IReadOnlyCollection<int> requiredQuestIds)
    {
        if (requiredQuestIds.Count == 0)
            return 0;

        long eligible = 0;
        foreach (var (setKey, kills) in monster.KillsByActiveQuestSet)
        {
            if (kills <= 0 || setKey.Length == 0)
                continue;
            foreach (var part in setKey.Split(','))
            {
                if (int.TryParse(part, out var id) && requiredQuestIds.Contains(id))
                {
                    eligible += kills;
                    break;
                }
            }
        }

        long legacy = 0;
        foreach (var questId in requiredQuestIds)
            legacy = Math.Max(legacy, monster.KillsWhileQuestActive.GetValueOrDefault(questId));
        return eligible + legacy;
    }

    public static void Recompute(CapturedMonster monster)
    {
        foreach (var drop in monster.Drops.Values)
        {
            if (drop.QuestGated)
            {
                var eligible = QuestActiveEligibleKills(monster, drop.RequiredQuestIds);

                if (eligible > 0 && drop.TrackedDropEvents > 0)
                {
                    drop.EligibleKills = Math.Max(eligible, drop.TrackedDropEvents);
                    drop.RateBasis = QuestActiveKills;
                }
                else
                {
                    drop.EligibleKills = eligible;
                    drop.RateBasis = Unverified;
                }
            }
            else
            {
                drop.EligibleKills = Math.Max(monster.KillCount, drop.DropEvents);
                drop.RateBasis = drop.DropEvents > 0 ? AllKills : Unverified;
            }
        }
    }
}

public sealed class CapturedItem
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int MaxStack { get; set; } = 1;
    public bool Temp { get; set; }
    public bool Coins { get; set; }
    public bool Upgrade { get; set; }
    public bool House { get; set; }
    public string ItemGroup { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileLink { get; set; } = string.Empty;
    public string Meta { get; set; } = string.Empty;
    public int EnhancementPatternID { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Quest IDs that use this item (AQW "sReqQuests"); a non-empty set marks a quest-gated item.</summary>
    public HashSet<int> RequiredByQuestIds { get; set; } = new();
}

public sealed class CapturedShop
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public HashSet<string> MapsSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CapturedShopItem> Items { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedShopItem
{
    public int ShopItemID { get; set; }
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Cost { get; set; }
    public bool Coins { get; set; }
    public bool Upgrade { get; set; }
    public int Level { get; set; }
    public int MaxStack { get; set; } = 1;
    public string Faction { get; set; } = string.Empty;
    public int RequiredReputation { get; set; }
    public int TokenID { get; set; }
    public double TokenQuantity { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public int EnhancementPatternID { get; set; }
    public int ProcID { get; set; }
    public string Meta { get; set; } = string.Empty;
    public List<CapturedItemRequirement> Requirements { get; set; } = new();
}

public sealed class CapturedItemRequirement
{
    public int ItemID { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool Temp { get; set; }

    /// <summary>Quest rewards only: raw AQW reward type from getQuests "reward[].iType" (meaning to be mapped).</summary>
    public int? RewardType { get; set; }

    /// <summary>Quest rewards only: drop chance in percent from getQuests "reward[].iRate" (100 = guaranteed).</summary>
    public double? RatePercent { get; set; }
}

/// <summary>Base quest turn-in reward from "ccqr" "rewardObj" (last observed values; they are fixed per quest).</summary>
public sealed class CapturedQuestReward
{
    public long Samples { get; set; }
    public int Exp { get; set; }
    public int Gold { get; set; }
    public int Rep { get; set; }
    public int FactionID { get; set; }
    public int ClassPoints { get; set; }
    public int Coins { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedSellPrice
{
    public int ItemID { get; set; }
    /// <summary>Amount returned per sale ("intAmount"), gold unless <see cref="Coins"/>.</summary>
    public int Amount { get; set; }
    public bool Coins { get; set; }
    public int Quantity { get; set; }
    public long Samples { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Faction catalog entry (game data only): ID and name from "addFaction" or the client's faction list. The player's
/// own rank/reputation is never stored.
/// </summary>
public sealed class CapturedFaction
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CapturedQuest
{
    public int ID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EndText { get; set; } = string.Empty;
    public bool Once { get; set; }
    public bool Upgrade { get; set; }
    public int Level { get; set; }
    public int Slot { get; set; }
    public int Value { get; set; }
    public string Field { get; set; } = string.Empty;
    public int Index { get; set; }
    public int Gold { get; set; }
    public int XP { get; set; }
    public int RequiredClassID { get; set; }
    public int RequiredClassPoints { get; set; }
    public int RequiredFactionId { get; set; }
    public int RequiredFactionRep { get; set; }

    public List<CapturedItemRequirement> AcceptRequirements { get; set; } = new();
    public List<CapturedItemRequirement> Requirements { get; set; } = new();
    public List<CapturedItemRequirement> Rewards { get; set; } = new();

    /// <summary>
    /// Base turn-in reward observed from the server ("ccqr" "rewardObj": intExp, intGold, iRep, FactionID, iCP,
    /// intCoins). These are base values without boosts (verified: ccqr intGold 1000 vs addGoldExp intGold 2500 with
    /// bonusGold 1500) and are not reduced at level cap. Null until a turn-in is observed. QuestData.json has no
    /// reputation or class points, so this is the only source for them.
    /// </summary>
    public CapturedQuestReward? ObservedReward { get; set; }

    public long AcceptedCount { get; set; }
    public long TurnInCount { get; set; }
    public HashSet<string> MapsAcceptedOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MapsTurnedInOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// PvP match data for one map (additive in v3), from the server packets "PVPE" { team, typ, val, unm } (one event, e.g.
/// team 0 killed a "Restorer"; the player name is never read) and "PVPS" { pvpScore: [ {k, m, r, v} per team ] }
/// (score update; the meaning of k/m/r/v is still being worked out, hence the ordered sequence). Numbers only, no
/// names and no times.
/// </summary>
public sealed class CapturedPvpMatch
{
    /// <summary>Items received up to this long after the match ended (leaving the map or the next match) still count.</summary>
    public const int GraceSeconds = 20;

    /// <summary>Last score of the match, <c>k,m,r,v|k,m,r,v</c> (team 0 | team 1); empty if no score arrived.</summary>
    public string FinalScore { get; set; } = string.Empty;

    public int ScoreUpdates { get; set; }

    /// <summary>Events of this match keyed <c>team{n}:{typ}:{val}</c>.</summary>
    public Dictionary<string, long> Events { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Amounts announced by "dropItem" (<c>dQty</c>, else <c>iQty</c>), keyed <c>{ItemID}:{name}</c>.</summary>
    public Dictionary<string, long> ItemsDropped { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Amounts added by "addItems" (<c>iQty</c>), keyed <c>{ItemID}:{name}</c>. Kept apart from <see cref="ItemsDropped"/>
    /// because a picked-up drop may show up in both.
    /// </summary>
    public Dictionary<string, long> ItemsAdded { get; set; } = new(StringComparer.Ordinal);

    /// <summary>True when the match ended because the character left the map (false: the next match began).</summary>
    public bool EndedByLeaving { get; set; }
}

public sealed class CapturedPvpStats
{
    public const int MaxSequence = 2000;
    public const int MaxFinalScores = 500;

    /// <summary>Event counts keyed <c>team{n}:{typ}:{val}</c>, e.g. <c>team0:kill:Restorer</c>.</summary>
    public Dictionary<string, long> Events { get; set; } = new(StringComparer.Ordinal);

    public long ScoreUpdates { get; set; }

    /// <summary>
    /// The last score of each match: <c>k,m,r,v|k,m,r,v</c> (team 0 | team 1). A match ends when the scores drop back
    /// (a new match started) or when the character leaves the map.
    /// </summary>
    public List<string> FinalScores { get; set; } = new();

    /// <summary>
    /// Events and score changes in the order they arrived: <c>E:team0:kill:Restorer</c>, <c>S:10,3,4,1250|0,0,0,0</c>;
    /// <c>---</c> separates flushes/sessions. Kept up to <see cref="MaxSequence"/> entries.
    /// </summary>
    public List<string> Sequence { get; set; } = new();

    /// <summary>
    /// One record per match (additive, collector 1.2.0): final score, events and the items the character received during
    /// the match or within <see cref="CapturedPvpMatch.GraceSeconds"/> after it ended, to work out the trophy rule.
    /// </summary>
    public List<CapturedPvpMatch> Matches { get; set; } = new();

    public void Merge(CapturedPvpStats? o)
    {
        if (o == null)
            return;
        foreach (var (key, count) in o.Events)
            Events[key] = Events.GetValueOrDefault(key) + count;
        ScoreUpdates += o.ScoreUpdates;
        foreach (var s in o.FinalScores)
            if (FinalScores.Count < MaxFinalScores)
                FinalScores.Add(s);
        foreach (var m in o.Matches ?? new())
            if (Matches.Count < MaxFinalScores)
                Matches.Add(m);
        if (o.Sequence.Count > 0 && Sequence.Count < MaxSequence)
        {
            if (Sequence.Count > 0)
                Sequence.Add("---");
            Sequence.AddRange(o.Sequence.Take(Math.Max(0, MaxSequence - Sequence.Count)));
        }
    }
}
