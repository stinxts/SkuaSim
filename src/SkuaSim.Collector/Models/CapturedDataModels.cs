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
    /// </summary>
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>Room player cap read from the game client after joining (null when not observed). Not server-wide iMax.</summary>
    public int? MaxPlayers { get; set; }

    /// <summary>Where <see cref="MaxPlayers"/> came from, e.g. "runtime:world.areaData.intMaxPlayers".</summary>
    public string? MaxPlayersSource { get; set; }

    public DateTime? MaxPlayersObservedUtc { get; set; }
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

    /// <summary>Quest ID -> tracked kills of this monster with that quest active (basis for quest-gated drop rates).</summary>
    public Dictionary<int, long> KillsWhileQuestActive { get; set; } = new();

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
    public long TotalQuantity { get; set; }

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

    public static void Recompute(CapturedMonster monster)
    {
        foreach (var drop in monster.Drops.Values)
        {
            if (drop.QuestGated)
            {
                long eligible = 0;
                foreach (var questId in drop.RequiredQuestIds)
                    eligible += monster.KillsWhileQuestActive.GetValueOrDefault(questId);

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

    public long AcceptedCount { get; set; }
    public long TurnInCount { get; set; }
    public HashSet<string> MapsAcceptedOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MapsTurnedInOn { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
