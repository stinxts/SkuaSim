using Newtonsoft.Json;

namespace SkuaSim.Collector.Models;

/// <summary>
/// Root database containing all passively collected AQW/Skua game metadata.
/// Strictly anonymized: contains zero account names, player names, chat logs, or personal inventory states.
/// </summary>
public sealed class GameDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public long TotalFlushesMerged { get; set; }

    public Dictionary<string, CapturedMap> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CapturedMonster> Monsters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, CapturedItem> Items { get; set; } = new();
    public Dictionary<int, CapturedShop> Shops { get; set; } = new();
    public Dictionary<int, CapturedQuest> Quests { get; set; } = new();

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
}

public sealed class CapturedMapItem
{
    public int MapItemID { get; set; }
    public HashSet<string> CellsSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long TimesRequested { get; set; }
    public HashSet<int> GrantedItemIDs { get; set; } = new();
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
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

    /// <summary>
    /// Maps map name -> set of cells where this monster spawns.
    /// </summary>
    public Dictionary<string, HashSet<string>> MapsSeen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long KillCount { get; set; }
    public CapturedCombatStats Combat { get; set; } = new();
    public Dictionary<string, CapturedAuraStat> Auras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
}

public sealed class CapturedAuraStat
{
    public string Name { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty; // "monster" or "player"
    public string Category { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public float? MinValue { get; set; }
    public float? MaxValue { get; set; }
    public long TimesSeen { get; set; }
    public bool IsCounterAttack { get; set; }
}

public sealed class CapturedDropStat
{
    public int ItemID { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool Temp { get; set; }
    public long DropEvents { get; set; }
    public long TotalQuantity { get; set; }

    /// <summary>
    /// Number of kills recorded for this monster AFTER this drop was first observed,
    /// plus kills in the session where it dropped, used for drop rate estimation.
    /// </summary>
    public long EligibleKills { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
    public double EstimatedDropChance => EligibleKills > 0
        ? Math.Min(1.0, Math.Round((double)DropEvents / EligibleKills, 4))
        : 0.0;
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
    public List<CapturedItemRequirement> Requirements { get; set; } = new();
}

public sealed class CapturedItemRequirement
{
    public int ItemID { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool Temp { get; set; }
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
