using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkuaSim.Core.Scenarios;

/// <summary>
/// Describes the state of the player who reported a bug: character, location, inventory, bank, quest progress,
/// script options and world tuning. Every field is optional; missing values fall back to sensible defaults.
/// </summary>
public sealed class Scenario
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? Seed { get; set; }

    /// <summary>Default virtual start time (a Thursday) used when <see cref="StartDate"/> is not set.</summary>
    public static readonly DateTime DefaultStartUtc = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Virtual start time of the run (UTC, e.g. "2026-11-20T19:00:00Z"). Drives the day of week, AQW's daily/weekly
    /// resets (00:00 US Eastern; weekly on Friday) and, with <see cref="BoostsSetup.AeCalendarServerBoosts"/>, the AE
    /// calendar's server boosts. Null = 2026-01-01 12:00 UTC.
    /// </summary>
    public DateTime? StartDate { get; set; }

    [JsonIgnore]
    public DateTime EffectiveStartUtc => StartDate is { } d
        ? (d.Kind == DateTimeKind.Utc ? d : d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc))
        : DefaultStartUtc;
    public PlayerSetup Player { get; set; } = new();
    public LocationSetup Location { get; set; } = new();
    public List<ItemSetup> Inventory { get; set; } = new();
    public List<ItemSetup> Bank { get; set; } = new();
    public List<ItemSetup> TempInventory { get; set; } = new();
    public List<ItemSetup> House { get; set; } = new();
    public QuestSetup Quests { get; set; } = new();
    public List<FactionSetup> Factions { get; set; } = new();
    public BoostsSetup Boosts { get; set; } = new();
    public EnhancementsSetup Enhancements { get; set; } = new();

    /// <summary>Script options (Bot.Config), by option name. Values may be strings, numbers or booleans.</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Client options (Bot.Options), e.g. "AutoRelogin": true.</summary>
    public Dictionary<string, JsonElement> BotOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra/override map definitions (used when the collector has no data for a map).</summary>
    public Dictionary<string, MapSetup> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public WorldSetup World { get; set; } = new();
    public LimitsSetup Limits { get; set; } = new();

    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Scenario Load(string path)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException($"Scenario file is empty: {path}");
        scenario.SourcePath = Path.GetFullPath(path);
        scenario.Options = new Dictionary<string, JsonElement>(scenario.Options, StringComparer.OrdinalIgnoreCase);
        scenario.BotOptions = new Dictionary<string, JsonElement>(scenario.BotOptions, StringComparer.OrdinalIgnoreCase);
        scenario.Maps = new Dictionary<string, MapSetup>(scenario.Maps, StringComparer.OrdinalIgnoreCase);
        return scenario;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class PlayerSetup
{
    public string Username { get; set; } = "SimPlayer";
    public int Level { get; set; } = 100;
    public long Gold { get; set; } = 1_000_000;
    public int Coins { get; set; } = 0;
    public bool IsMember { get; set; } = true;
    public int AccessLevel { get; set; } = 0;
    public string Guild { get; set; } = "";
    /// <summary>Name of the class to equip (added to the inventory if missing).</summary>
    public string? Class { get; set; }
    public int ClassRank { get; set; } = 10;
    /// <summary>Optional fixed MaxHealth override. Null computes AQW health from Level, Class, END, and Enhancements.</summary>
    public int? MaxHealth { get; set; }
    /// <summary>Optional fixed DPS override. Null computes AQW DPS from Class, Primary/Secondary Stats, Crit, Haste, and Enhancements.</summary>
    public int? DamagePerSecond { get; set; }
    /// <summary>Optional fixed HP%/s regen override. Null uses class heal skills, weapon lifesteal, and out-of-combat regen.</summary>
    public double? RegenPercentPerSecond { get; set; }
    public bool Invulnerable { get; set; }
    public int InventorySlots { get; set; } = 200;
    public int BankSlots { get; set; } = 600;
    public int HouseSlots { get; set; } = 100;
}

public sealed class LocationSetup
{
    public string Map { get; set; } = "battleon";
    public string Cell { get; set; } = "Enter";
    public string Pad { get; set; } = "Spawn";
    public bool LoggedIn { get; set; } = true;
}

public sealed class ItemSetup
{
    public string? Name { get; set; }
    public int? Id { get; set; }
    public int Quantity { get; set; } = 1;
    public string? Category { get; set; }
    public int? MaxStack { get; set; }
    public bool Equipped { get; set; }
    public bool? Temp { get; set; }
    public bool? Coins { get; set; }
    public bool? Member { get; set; }
    public int? EnhancementLevel { get; set; }
    public int? EnhancementPatternID { get; set; }
    public int? ProcID { get; set; }
}

public sealed class QuestSetup
{
    /// <summary>Quest IDs already completed (one-time/story quests).</summary>
    public List<int> Completed { get; set; } = new();
    /// <summary>Quest IDs accepted at start.</summary>
    public List<int> Active { get; set; } = new();
    /// <summary>Daily/weekly quest IDs already done today.</summary>
    public List<int> DailiesCompleted { get; set; } = new();
    /// <summary>Story progress per quest slot: { "32": 5 } means slot 32 has value 5.</summary>
    public Dictionary<string, int> SlotProgress { get; set; } = new();
}

public sealed class FactionSetup
{
    public string Name { get; set; } = "";
    public int? Id { get; set; }
    public int Rank { get; set; } = 1;
    public int? TotalRep { get; set; }
}

public sealed class MapSetup
{
    public List<string>? Cells { get; set; }
    public List<MonsterSetup> Monsters { get; set; } = new();
    /// <summary>Map item ID -> item name granted by GetMapItem.</summary>
    public Dictionary<string, string> MapItems { get; set; } = new();
}

public sealed class MonsterSetup
{
    public string Name { get; set; } = "";
    public string Cell { get; set; } = "Enter";
    public int? Id { get; set; }
    public int Count { get; set; } = 1;
    public int? MaxHP { get; set; }
    public int? Level { get; set; }
    /// <summary>Item name -> drop chance (0..1).</summary>
    public Dictionary<string, double> Drops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorldSetup
{
    /// <summary>Drop chance used when an item the script is farming has no captured drop data.</summary>
    public double GenericDropChance { get; set; } = 0.5;
    /// <summary>Allow inventing drops for items the script farms but the collector never saw.</summary>
    public bool SynthesizeDrops { get; set; } = true;
    /// <summary>
    /// Forces one respawn delay for every monster. Null (default) uses the collector's measured respawn per spawn /
    /// monster, then the median of measured monsters, then AQW's official ~4 s.
    /// </summary>
    public int? MonsterRespawnSeconds { get; set; }
    public int PlayerRespawnSeconds { get; set; } = 11;
    public int JoinDelayMs { get; set; } = 1500;
    /// <summary>Buying from shops/items the collector never saw succeeds (flagged) instead of failing.</summary>
    public bool LenientShops { get; set; } = true;
    /// <summary>Answer for Yes/No message boxes ("yes" or "no").</summary>
    public string MessageBoxAnswer { get; set; } = "no";
    /// <summary>Generic HP for monsters without captured data: Level * this value (minimum 1000).</summary>
    public int GenericMonsterHpPerLevel { get; set; } = 400;
}

public sealed class LimitsSetup
{
    /// <summary>Maximum virtual simulation duration in minutes. 0 or less means unlimited (runs until script finishes or Stop is clicked).</summary>
    public double MaxVirtualMinutes { get; set; } = 360;
    /// <summary>Maximum wall-clock execution seconds (excluding compilation). 0 or less means unlimited (infinite).</summary>
    public double MaxRealSeconds { get; set; } = 0;
    /// <summary>Stop when no API call happens for this many real seconds (script stuck in pure CPU code).</summary>
    public double StallRealSeconds { get; set; } = 20;
    /// <summary>
    /// Stop with outcome Stalled when the player's progress (level, XP, gold, coins, items, quests, story slots,
    /// reputation) does not change for this many virtual minutes, which almost always means an infinite loop.
    /// Measured in virtual time, so it is deterministic. 0 or less disables it.
    /// </summary>
    public double MaxNoProgressMinutes { get; set; } = 60;
}

public sealed class BoostsSetup
{
    public bool XpBoost { get; set; }
    public bool ClassPointsBoost { get; set; }
    public bool GoldBoost { get; set; }
    public bool RepBoost { get; set; }
    /// <summary>Forces a server-wide double XP/gold/rep/CP for the whole run (independent of the calendar).</summary>
    public bool ServerBoost { get; set; }

    /// <summary>
    /// Apply the server-wide boosts the AE calendar lists for the virtual date (needs a local ae_calendar.json from
    /// "skuasim calendar-update"; dates outside its coverage get no calendar boost and a warning).
    /// </summary>
    public bool AeCalendarServerBoosts { get; set; }
    public List<GearSlotBoostSetup> EquipmentBoosts { get; set; } = new();
}

public sealed class GearSlotBoostSetup
{
    /// <summary>Equipment slot: Weapon, Armor, Helm, Cape, or Pet.</summary>
    public string Slot { get; set; } = "Weapon";
    /// <summary>Real AQW boosted item name (e.g. "Necrotic Sword of Doom", "Radiant Goddess of War", "Head of the Legion Beast").</summary>
    public string? ItemName { get; set; }
    /// <summary>Optional explicit AQW Meta string (auto-resolved from AqwBoostGearCatalog when ItemName matches).</summary>
    public string? Meta { get; set; }
    /// <summary>Legacy single-boost fallback type: None, dmgAll, AllResources, exp, cp, gold, rep, AllRaces, Undead, Chaos, Dragonkin, Human, Elemental, Drakath, Orc.</summary>
    public string BoostType { get; set; } = "None";
    /// <summary>Legacy single-boost percentage (e.g. 51 for +51% dmgAll).</summary>
    public int Percent { get; set; }
}

public sealed class EnhancementsSetup
{
    public List<string> UnlockedAwe { get; set; } = new();
    public List<string> UnlockedForge { get; set; } = new();
}
