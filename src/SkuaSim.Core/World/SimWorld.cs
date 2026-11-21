using System.Reflection;
using Skua.Core.Models.Factions;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Players;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using Skua.Core.Models.Skills;
using SkuaSim.Collector.Models;
using SkuaSim.Core.Data;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.World;

public sealed class SimMonster
{
    public int MapID { get; init; }
    public int MonID { get; init; }
    public string Name { get; init; } = "";
    public string Cell { get; init; } = "Enter";
    public int MaxHP { get; init; }
    public int HP { get; set; }
    public int Level { get; init; }
    public string Race { get; init; } = "None";
    public string FileName { get; init; } = "";
    public CapturedMonster? Data { get; init; }

    /// <summary>The captured spawn this instance comes from (captured maps only): per-spawn respawn and wDPS.</summary>
    public CapturedMapMonsterSpawn? Spawn { get; init; }

    /// <summary>Server-side weapon DPS ("monBranch.wDPS"), 0 when unknown.</summary>
    public int WeaponDps { get; init; }
    public MonsterSetup? Setup { get; init; }
    public bool Synthesized { get; init; }
    public long NextAttackMs { get; set; }
    public bool Alive => HP > 0;

    public Monster ToModel() => new()
    {
        Name = Name,
        ID = MonID,
        Race = Race,
        Cell = Cell,
        MapID = MapID,
        HP = HP,
        MaxHP = MaxHP,
        State = Alive ? 1 : 0,
        FileName = FileName
    };
}

public sealed class SimMap
{
    public string Name { get; init; } = "";
    public string FileName { get; set; } = "";
    public List<string> Cells { get; } = new();
    public Dictionary<string, List<string>> Pads { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SimMonster> Monsters { get; } = new();
    public bool Captured { get; set; }
    public bool FromScenario { get; set; }
    public CapturedMap? Data { get; set; }
    public MapSetup? Setup { get; set; }
    public bool Synthesized => !Captured && !FromScenario;
}

public sealed record WantedItem(ItemInfo Info, int Quantity);

/// <summary>
/// In-memory game state and mechanics. Every public method is thread-safe (guarded by <see cref="Sync"/>).
/// Data comes, in priority order, from the scenario, the collector database and QuestData.json; anything else is
/// synthesized and recorded in the trace so the report shows which results depend on invented data.
/// </summary>
public sealed partial class SimWorld
{
    public const int MaxLevel = 100;
    private const int CombatTickMs = 1000;
    private static readonly FieldInfo? QuestReqsField = typeof(Quest).GetField("_reqs", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? QuestReqCacheField = typeof(Quest).GetField("_reqCache", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly SimContext _ctx;
    private readonly Dictionary<string, (long Time, int Quantity, bool FromTemp, bool FromInventory)> _recentQueries = new(StringComparer.OrdinalIgnoreCase);
    private int _nextCharItemId = 10_000;
    private bool _combatTickerActive;
    private bool _dropTickerActive;
    private bool _questTickerActive;

    public SimWorld(SimContext ctx)
    {
        _ctx = ctx;
        var p = ctx.Scenario.Player;
        Username = p.Username;
        Level = p.Level;
        Gold = p.Gold;
        Coins = p.Coins;
        IsMember = p.IsMember;
        AccessLevel = p.AccessLevel;
        Guild = p.Guild;
        MaxHealth = Math.Max(1, p.MaxHealth ?? 370);
        Health = MaxHealth;
        ClassRank = p.ClassRank;
        LoggedIn = ctx.Scenario.Location.LoggedIn;
        RecalculateStats();
        Health = MaxHealth;
    }

    public object Sync { get; } = new();
    private long Now => _ctx.Clock.NowMs;
    private SimulationTrace Trace => _ctx.Trace;
    private WorldSetup Rules => _ctx.Scenario.World;

    // ---------------------------------------------------------------- Player
    public string Username { get; }
    public int Level { get; set; }
    public int XP { get; set; }
    public long Gold { get; set; }
    public int Coins { get; set; }
    public bool IsMember { get; set; }
    public int AccessLevel { get; set; }
    public string Guild { get; set; }
    public int MaxHealth { get; private set; }
    public int Health { get; set; }
    public int RequiredXP => XpToNextLevel(Level);
    public int ClassRank { get; set; }
    public bool LoggedIn { get; set; }
    public bool Kicked { get; set; }
    public long DeadUntilMs { get; private set; } = -1;
    public long InCombatUntilMs { get; private set; }
    public bool Alive => DeadUntilMs < 0;
    public bool InCombat => Alive && (Now < InCombatUntilMs || (Target?.Alive ?? false));
    public int State => !Alive ? 0 : InCombat ? 2 : 1;
    public SimMonster? Target { get; private set; }
    public int PositionX { get; set; } = 480;
    public int PositionY { get; set; } = 320;

    // ---------------------------------------------------------------- Location
    public SimMap? Map { get; private set; }
    public string MapName { get; private set; } = "";
    public int RoomNumber { get; private set; }
    public string Cell { get; private set; } = "Enter";
    public string Pad { get; private set; } = "Spawn";
    public string LastMap { get; set; } = "";
    public bool MapLoaded { get; private set; }
    public string SpawnCell { get; private set; } = "Enter";
    public string SpawnPad { get; private set; } = "Spawn";
    public HashSet<string> MapsVisited { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SynthesizedMapsVisited { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Items
    public List<InventoryItem> Inventory { get; } = new();
    public List<InventoryItem> Bank { get; } = new();
    public List<InventoryItem> House { get; } = new();
    public List<ItemBase> TempInventory { get; } = new();
    public List<ItemBase> DropStack { get; } = new();
    public HashSet<int> Favorites { get; } = new();
    public bool BankLoaded { get; set; }

    // ---------------------------------------------------------------- Drops
    public HashSet<string> PickupNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> PickupIds { get; } = new();
    public bool DropsEnabled { get; private set; }
    public bool RejectElse { get; set; }
    public int DropInterval { get; set; } = 1000;

    // ---------------------------------------------------------------- Quests
    public Dictionary<int, Quest> LoadedQuests { get; } = new();
    public HashSet<int> ActiveQuests { get; } = new();
    public HashSet<int> CompletedQuests { get; } = new();
    public HashSet<int> DailiesCompleted { get; } = new();
    public Dictionary<int, int> SlotProgress { get; } = new();
    public Dictionary<int, int> RegisteredQuests { get; } = new();
    public int RegisterCompleteInterval { get; set; } = 2000;

    // ---------------------------------------------------------------- Shops / factions
    public ShopInfo? CurrentShop { get; private set; }
    public List<ShopInfo> ShopCache { get; } = new();
    public List<Faction> Factions { get; } = new();

    // ---------------------------------------------------------------- Stats
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int DropsPicked { get; set; }
    public int QuestsAcceptedCount { get; set; }
    public int QuestsCompletedCount { get; set; }
    public int Relogins { get; set; }
    public Dictionary<string, int> KillsByMonster { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> DropsByItem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, int> QuestTurnIns { get; } = new();

    // ================================================================ Initialization

    public void InitializeFromScenario()
    {
        var s = _ctx.Scenario;
        lock (Sync)
        {
            foreach (var item in s.Inventory)
                AddScenarioItem(Inventory, item);
            foreach (var item in s.Bank)
                AddScenarioItem(Bank, item);
            foreach (var item in s.House)
                AddScenarioItem(House, item);
            foreach (var item in s.TempInventory)
            {
                var info = ResolveItem(item.Name, item.Id, true, item.Category);
                info.Temp = true;
                TempInventory.Add(NewItemBase(info, item.Quantity));
            }

            if (!string.IsNullOrWhiteSpace(s.Player.Class) && !Inventory.Any(i => Eq(i.Name, s.Player.Class)))
                AddScenarioItem(Inventory, new ItemSetup { Name = s.Player.Class, Category = "Class", Equipped = true });
            if (!string.IsNullOrWhiteSpace(s.Player.Class))
            {
                var cls = Inventory.First(i => Eq(i.Name, s.Player.Class));
                EquipInternal(cls, log: false);
            }
            if (!Inventory.Any(i => i.Equipped && i.CategoryString == "Class"))
            {
                AddScenarioItem(Inventory, new ItemSetup { Name = "Warrior", Category = "Class", Equipped = true });
                Trace.Synthesize("scenario:class", "No class in the scenario: a default 'Warrior' class was equipped.");
            }

            foreach (var id in s.Quests.Completed)
            {
                CompletedQuests.Add(id);
                var q = _ctx.Catalog.FindQuest(id);
                if (q != null && q.Slot >= 0)
                    SlotProgress[q.Slot] = Math.Max(SlotProgress.GetValueOrDefault(q.Slot), q.Value);
            }
            foreach (var (slot, value) in s.Quests.SlotProgress)
            {
                if (int.TryParse(slot, out var slotId))
                    SlotProgress[slotId] = Math.Max(SlotProgress.GetValueOrDefault(slotId), value);
            }
            foreach (var id in s.Quests.DailiesCompleted)
                DailiesCompleted.Add(id);
            foreach (var id in s.Quests.Active)
            {
                if (GetOrLoadQuest(id) != null)
                    ActiveQuests.Add(id);
            }

            foreach (var f in s.Factions)
            {
                var faction = new Faction { ID = f.Id ?? StableHash.Of(f.Name) % 1000, Name = f.Name };
                SetFactionRep(faction, f.TotalRep ?? RepForRank(f.Rank));
                Factions.Add(faction);
            }

            ApplyScenarioBoostsAndEnhancements();

            RecalculateStats();
            if (!s.Player.MaxHealth.HasValue)
                Health = MaxHealth;
            ScheduleNextServerReset();
        }

        if (LoggedIn)
            Join(s.Location.Map, s.Location.Cell, s.Location.Pad, ignoreCheck: true, raiseEvents: false, delay: false);
    }

    // ================================================================ Server resets

    public int ServerResetsApplied { get; private set; }

    /// <summary>
    /// AQW resets daily quests at 00:00 US Eastern (server time), weekly quests on Friday 00:00 and monthly quests on
    /// the 1st (times as shown by VibeSkua.Manager: "Daily Reset (00:00 EST)", "Weekly Reset (Fri 00:00 EST)").
    /// Quest "Field" decides the period: "id*" daily, "iw*" weekly, "im*" monthly.
    /// </summary>
    private void ScheduleNextServerReset()
    {
        var nowEastern = AeCalendar.ToEastern(_ctx.Clock.Now);
        var nextMidnight = nowEastern.Date.AddDays(1);
        var atMs = (long)Math.Ceiling((AeCalendar.EasternToUtc(nextMidnight) - _ctx.Clock.Epoch).TotalMilliseconds);
        _ctx.Clock.Scheduler.Schedule(Math.Max(Now + 1, atMs), "server-reset", () =>
        {
            lock (Sync)
            {
                ApplyServerReset(nextMidnight);
                ScheduleNextServerReset();
            }
        });
    }

    private void ApplyServerReset(DateTime midnightEastern)
    {
        var weekly = midnightEastern.DayOfWeek == DayOfWeek.Friday;
        var monthly = midnightEastern.Day == 1;
        var cleared = new List<int>();
        foreach (var id in DailiesCompleted.ToList())
        {
            var field = QuestField(id);
            var period = field.StartsWith("iw", StringComparison.OrdinalIgnoreCase) ? 'w'
                : field.StartsWith("im", StringComparison.OrdinalIgnoreCase) ? 'm'
                : 'd';
            if (period == 'd' || (period == 'w' && weekly) || (period == 'm' && monthly))
            {
                DailiesCompleted.Remove(id);
                cleared.Add(id);
            }
        }
        ServerResetsApplied++;
        var kind = monthly && weekly ? "daily + weekly + monthly" : monthly ? "daily + monthly" : weekly ? "daily + weekly" : "daily";
        Trace.World($"Server {kind} reset at {midnightEastern:yyyy-MM-dd ddd} 00:00 server time (US Eastern): " +
                    (cleared.Count == 0 ? "no completed quests to reset." : $"quest(s) {string.Join(", ", cleared.OrderBy(i => i))} available again."));
    }

    private string QuestField(int id) =>
        LoadedQuests.TryGetValue(id, out var q) && !string.IsNullOrWhiteSpace(q.Field) ? q.Field
        : _ctx.Catalog.FindQuest(id)?.Field is { Length: > 0 } f ? f
        : _ctx.Catalog.FindCapturedQuest(id)?.Field ?? "";

    /// <summary>
    /// Total reputation needed for a faction rank: (5·r·(r+1))² — 900, 3,600, 10,000, 22,500, ... 302,500 at rank 10
    /// (rank 10 = 302,500 is confirmed by QuestData "RequiredFactionRep" values, e.g. Ravenous).
    /// </summary>
    internal static int RepForRank(int rank)
    {
        if (rank <= 1)
            return 0;
        rank = Math.Min(rank, MaxFactionRank);
        var root = 5 * rank * (rank + 1);
        return root * root;
    }

    internal const int MaxFactionRank = 10;
    internal const int MaxFactionRep = 302_500;

    internal static int RankForRep(int totalRep)
    {
        var rank = 1;
        while (rank < MaxFactionRank && totalRep >= RepForRank(rank + 1))
            rank++;
        return rank;
    }

    private static void SetFactionRep(Faction faction, int totalRep)
    {
        faction.TotalRep = Math.Clamp(totalRep, 0, MaxFactionRep);
        faction.Rank = RankForRep(faction.TotalRep);
        faction.Rep = faction.TotalRep - RepForRank(faction.Rank);
        faction.RequiredRep = faction.Rank >= MaxFactionRank ? 0 : RepForRank(faction.Rank + 1) - RepForRank(faction.Rank);
    }

    /// <summary>
    /// Adds base reputation (boosts applied here) to a faction, creating it when the player has none yet. The name
    /// comes from the scenario, then the collected faction catalog; an unknown name is synthesized.
    /// </summary>
    internal void GainReputation(int factionId, int baseRep, string source)
    {
        if (baseRep <= 0 || factionId <= 0)
            return;
        var faction = Factions.FirstOrDefault(f => f.ID == factionId);
        if (faction == null)
        {
            var name = _ctx.Catalog.FactionName(factionId);
            if (name != null)
                faction = Factions.FirstOrDefault(f => Eq(f.Name, name));
            if (faction == null)
            {
                if (name == null)
                {
                    name = $"Faction #{factionId}";
                    Trace.Synthesize($"faction:{factionId}", $"Faction {factionId} has no captured name: reputation is tracked as '{name}'.");
                }
                faction = new Faction { ID = factionId, Name = name };
                SetFactionRep(faction, 0);
                Factions.Add(faction);
            }
        }

        var gained = (int)Math.Round(baseRep * EffectiveRepMultiplier);
        var oldRank = faction.Rank;
        SetFactionRep(faction, faction.TotalRep + gained);
        if (faction.Rank > oldRank)
            Trace.World($"Reputation rank up! {faction.Name} reached Rank {faction.Rank}.");
        else
            Trace.World($"+{gained} {faction.Name} reputation ({source}).");
    }

    private void AddScenarioItem(List<InventoryItem> container, ItemSetup setup)
    {
        var info = ResolveItem(setup.Name, setup.Id, setup.Temp, setup.Category);
        if (setup.MaxStack.HasValue)
            info.MaxStack = setup.MaxStack.Value;
        if (setup.Coins.HasValue)
            info.Coins = setup.Coins.Value;
        if (setup.Member.HasValue)
            info.Upgrade = setup.Member.Value;
        var item = NewInventoryItem(info, setup.Quantity);
        if (setup.EnhancementPatternID.HasValue)
            item.EnhancementPatternID = Math.Max(0, setup.EnhancementPatternID.Value);
        if (setup.EnhancementLevel.HasValue)
            item.EnhancementLevel = Math.Max(0, setup.EnhancementLevel.Value);
        if (setup.ProcID.HasValue)
            item.ProcID = Math.Max(0, setup.ProcID.Value);
        container.Add(item);
        if (setup.Equipped && container == Inventory)
            EquipInternal(item, log: false);
    }

    // ================================================================ Items

    /// <summary>
    /// Finds item metadata by name or ID; invents it (flagged) if neither the collector nor QuestData knows the item.
    /// </summary>
    public ItemInfo ResolveItem(string? name, int? id = null, bool? tempHint = null, string? category = null)
    {
        var info = (id is > 0 ? _ctx.Catalog.FindItem(id.Value) : null) ?? _ctx.Catalog.FindItem(name);
        if (info != null)
        {
            if (!string.IsNullOrWhiteSpace(category))
                return new ItemInfo { ID = info.ID, Name = info.Name, MaxStack = info.MaxStack, Temp = tempHint ?? info.Temp, Category = category, Coins = info.Coins, Upgrade = info.Upgrade, ItemGroup = string.IsNullOrWhiteSpace(info.ItemGroup) || info.ItemGroup == "None" ? InferDefaultItemGroup(category) : info.ItemGroup, Meta = info.Meta, EnhancementPatternID = info.EnhancementPatternID, ProcID = info.ProcID, Source = info.Source };
            if (string.IsNullOrWhiteSpace(info.ItemGroup) || info.ItemGroup == "None")
                info.ItemGroup = InferDefaultItemGroup(info.Category);
            return info;
        }

        var finalName = string.IsNullOrWhiteSpace(name) ? $"Item #{id}" : name.Trim();
        var finalId = id is > 0 ? id.Value : SynthesizedItemId(finalName);
        var finalCat = category ?? "Item";
        var synthesized = new ItemInfo
        {
            ID = finalId,
            Name = finalName,
            MaxStack = finalCat is "Class" or "Armor" or "Helm" or "Cape" or "Pet" ? 1 : 1000,
            Temp = tempHint ?? false,
            Category = finalCat,
            ItemGroup = InferDefaultItemGroup(finalCat),
            Source = DataSource.Synthesized
        };
        Trace.Synthesize($"item:{finalName.ToLowerInvariant()}", $"Item '{finalName}' (ID {finalId}) is unknown to the collector and QuestData: metadata invented (temp={synthesized.Temp}, max stack={synthesized.MaxStack}).");
        return synthesized;
    }

    private int SynthesizedItemId(string name)
    {
        var hash = 0;
        foreach (var c in name.ToLowerInvariant())
            hash = unchecked(hash * 31 + c);
        var id = 9_000_000 + Math.Abs(hash % 900_000);
        while (_ctx.Catalog.FindItem(id) is { } other && !Eq(other.Name, name))
            id++;
        return id;
    }

    public InventoryItem NewInventoryItem(ItemInfo info, int quantity) => new()
    {
        ID = info.ID,
        Name = info.Name,
        Quantity = Math.Clamp(quantity, 1, Math.Max(1, info.MaxStack)),
        MaxStack = Math.Max(1, info.MaxStack),
        CategoryString = info.Category,
        Coins = info.Coins,
        Upgrade = info.Upgrade,
        Temp = info.Temp,
        CharItemID = _nextCharItemId++,
        Description = "",
        ItemGroup = string.IsNullOrWhiteSpace(info.ItemGroup) || info.ItemGroup == "None" ? InferDefaultItemGroup(info.Category) : info.ItemGroup,
        Meta = info.Meta ?? "",
        EnhancementPatternID = info.EnhancementPatternID,
        ProcID = info.ProcID,
        EnhancementLevel = info.EnhancementPatternID > 0 ? Math.Clamp(Level, 1, 100) : 0,
        Level = 1
    };

    public static ItemBase NewItemBase(ItemInfo info, int quantity) => new()
    {
        ID = info.ID,
        Name = info.Name,
        Quantity = Math.Max(1, quantity),
        MaxStack = Math.Max(1, info.MaxStack),
        CategoryString = info.Category,
        Coins = info.Coins,
        Upgrade = info.Upgrade,
        Temp = info.Temp,
        Description = "",
        ItemGroup = "None"
    };

    private static ItemInfo InfoOf(ItemBase item) => new()
    {
        ID = item.ID,
        Name = item.Name ?? $"Item #{item.ID}",
        MaxStack = Math.Max(1, item.MaxStack),
        Temp = item.Temp,
        Category = item.CategoryString ?? "Item",
        Coins = item.Coins,
        Upgrade = item.Upgrade,
        Source = DataSource.QuestData
    };

    /// <summary>House furniture/house items (QuestData sType "Floor Item"/"Wall Item"/"House", file under items/house/).</summary>
    public static bool IsHouseItem(ItemBase item) =>
        item.House ||
        (item.CategoryString ?? "") is "Floor Item" or "Wall Item" or "House" ||
        (item.FileLink ?? "").Contains("items/house/", StringComparison.OrdinalIgnoreCase);

    public static bool Eq(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool Matches(ItemBase item, string? name, int? id) =>
        id is > 0 ? item.ID == id : Eq(item.Name, name);

    public int Quantity(IEnumerable<ItemBase> container, string? name, int? id = null)
    {
        lock (Sync)
            return container.Where(i => Matches(i, name, id)).Sum(i => i.Quantity);
    }

    /// <summary>
    /// Remembers which items the script checks (Contains/GetQuantity). The item a kill loop keeps checking is the item
    /// being farmed, which is how drops are synthesized when the collector has no data.
    /// </summary>
    public void NoteItemQuery(string? name, int? id, int quantity, bool fromTemp)
    {
        var key = id is > 0 ? "#" + id : name?.Trim();
        if (string.IsNullOrEmpty(key) || quantity <= 0)
            return;
        lock (Sync)
        {
            _recentQueries.TryGetValue(key, out var prev);
            _recentQueries[key] = (Now, Math.Max(quantity, prev.Time > Now - 20_000 ? prev.Quantity : 0), fromTemp || prev.FromTemp, !fromTemp || prev.FromInventory);
        }
    }

    /// <summary>Items the script is currently trying to obtain and does not have enough of yet.</summary>
    public List<WantedItem> CurrentlyFarmedItems()
    {
        lock (Sync)
        {
            var result = new List<WantedItem>();
            var addedIds = new HashSet<int>();

            // 1. Active quest requirements are always primary farm targets.
            foreach (var qid in ActiveQuests)
            {
                if (!LoadedQuests.TryGetValue(qid, out var quest))
                    continue;
                foreach (var req in quest.Requirements)
                {
                    var info = _ctx.Catalog.FindItem(req.ID) ?? InfoOf(req);
                    var have = Quantity(req.Temp ? TempInventory : Inventory, req.Name, req.ID);
                    if (have < req.Quantity && addedIds.Add(info.ID))
                        result.Add(new WantedItem(info, req.Quantity));
                }
            }

            if (result.Count > 0)
                return result;

            // 2. When no active quest needs items, fall back to direct drop items queried on the current map.
            foreach (var (key, q) in _recentQueries)
            {
                if (Now - q.Time > 8_000)
                    continue;
                int? id = key.StartsWith('#') && int.TryParse(key[1..], out var parsed) ? parsed : null;
                var name = id.HasValue ? null : key;
                var known = (id.HasValue ? _ctx.Catalog.FindItem(id.Value) : null) ?? _ctx.Catalog.FindItem(name);

                var isPickupRegistered = (name != null && PickupNames.Contains(name))
                                         || (id.HasValue && PickupIds.Contains(id.Value))
                                         || (known != null && (PickupNames.Contains(known.Name) || PickupIds.Contains(known.ID)));

                if (!q.FromTemp && !isPickupRegistered)
                    continue;

                var info = known ?? ResolveItem(name, id, !q.FromInventory);
                if (string.Equals(info.Category, "Class", StringComparison.OrdinalIgnoreCase))
                    continue;

                var have = Quantity(info.Temp ? TempInventory : Inventory, info.Name, info.ID);
                if (have < q.Quantity && have < info.MaxStack && addedIds.Add(info.ID))
                    result.Add(new WantedItem(info, q.Quantity));
            }

            return result;
        }
    }

    public bool AddToInventory(ItemInfo info, int quantity, string reason)
    {
        if (quantity <= 0)
            quantity = 1;
        lock (Sync)
        {
            if (info.Temp)
            {
                AddTemp(info, quantity);
                return true;
            }

            var existing = Inventory.FirstOrDefault(i => i.ID == info.ID);
            if (existing != null)
            {
                if (existing.Quantity >= existing.MaxStack)
                {
                    Trace.World($"{info.Name} is at max stack ({existing.MaxStack}); {reason} gave nothing.");
                    return false;
                }
                existing.Quantity = Math.Min(existing.MaxStack, existing.Quantity + quantity);
                Trace.World($"+{quantity} {info.Name} ({reason}) -> {existing.Quantity}");
                return true;
            }

            if (Inventory.Count >= _ctx.Scenario.Player.InventorySlots)
            {
                Trace.Warn($"Inventory full ({Inventory.Count}/{_ctx.Scenario.Player.InventorySlots}): could not add {info.Name} ({reason}).");
                return false;
            }

            var item = NewInventoryItem(info, quantity);
            Inventory.Add(item);
            Trace.World($"+{item.Quantity} {info.Name} ({reason}) -> {item.Quantity}");
            return true;
        }
    }

    public void AddTemp(ItemInfo info, int quantity)
    {
        lock (Sync)
        {
            var existing = TempInventory.FirstOrDefault(i => i.ID == info.ID);
            if (existing != null)
                existing.Quantity = Math.Min(Math.Max(existing.MaxStack, 1), existing.Quantity + quantity);
            else
                TempInventory.Add(NewItemBase(info, Math.Min(quantity, Math.Max(1, info.MaxStack))));
        }
    }

    public bool RemoveItem(string? name, int? id, int quantity, bool temp, string reason)
    {
        lock (Sync)
        {
            if (temp)
            {
                var t = TempInventory.FirstOrDefault(i => Matches(i, name, id));
                if (t == null || t.Quantity < quantity)
                    return false;
                t.Quantity -= quantity;
                if (t.Quantity <= 0)
                    TempInventory.Remove(t);
                return true;
            }

            var inv = Inventory.FirstOrDefault(i => Matches(i, name, id));
            if (inv == null || inv.Quantity < quantity)
                return false;
            inv.Quantity -= quantity;
            if (inv.Quantity <= 0)
            {
                Inventory.Remove(inv);
                if (inv.Equipped)
                    Trace.World($"{inv.Name} was equipped and is now gone.");
            }
            Trace.World($"-{quantity} {inv.Name} ({reason})");
            return true;
        }
    }

    // ================================================================ Equipment

    public static string EquipGroup(string? category) => (category ?? "").Trim() switch
    {
        "Class" => "class",
        "Armor" => "armor",
        "Helm" => "helm",
        "Cape" => "cape",
        "Pet" => "pet",
        "Amulet" or "Necklace" => "amulet",
        "Sword" or "Axe" or "Dagger" or "Gun" or "HandGun" or "Rifle" or "Bow" or "Mace" or "Gauntlet" or "Polearm" or "Staff" or "Wand" or "Whip" => "weapon",
        "Item" => "consumable",
        var other => other.ToLowerInvariant()
    };

    public bool Equip(int id)
    {
        lock (Sync)
        {
            var item = Inventory.FirstOrDefault(i => i.ID == id);
            if (item == null)
            {
                Trace.World($"Equip failed: item #{id} is not in the inventory.");
                return false;
            }
            EquipInternal(item, log: true);
            return true;
        }
    }

    private void EquipInternal(InventoryItem item, bool log)
    {
        var group = EquipGroup(item.CategoryString);
        foreach (var other in Inventory.Where(i => i.Equipped && i != item && EquipGroup(i.CategoryString) == group))
            other.Equipped = false;
        item.Equipped = true;
        if (CombatProfile != null)
            RecalculateStats();
        if (log)
            Trace.World($"Equipped {item.Name} ({item.CategoryString}).");
    }

    public InventoryItem? CurrentClass
    {
        get { lock (Sync) return Inventory.FirstOrDefault(i => i.Equipped && i.CategoryString == "Class"); }
    }

    // ================================================================ Bank

    public bool ToBank(int id)
    {
        lock (Sync)
        {
            var inv = Inventory.FirstOrDefault(i => i.ID == id);
            if (inv == null)
                return false;
            if (inv.Equipped)
            {
                Trace.World($"Cannot bank {inv.Name}: it is equipped.");
                return false;
            }
            var existing = Bank.FirstOrDefault(i => i.ID == id);
            if (existing == null && Bank.Count >= _ctx.Scenario.Player.BankSlots)
            {
                Trace.Warn($"Bank full: could not bank {inv.Name}.");
                return false;
            }
            Inventory.Remove(inv);
            if (existing != null)
                existing.Quantity = Math.Min(existing.MaxStack, existing.Quantity + inv.Quantity);
            else
                Bank.Add(inv);
            Trace.World($"Banked {inv.Name} x{inv.Quantity}.");
            return true;
        }
    }

    public bool ToInventory(int id)
    {
        lock (Sync)
        {
            var bank = Bank.FirstOrDefault(i => i.ID == id);
            if (bank == null)
                return false;
            var existing = Inventory.FirstOrDefault(i => i.ID == id);
            if (existing == null && Inventory.Count >= _ctx.Scenario.Player.InventorySlots)
            {
                Trace.Warn($"Inventory full: could not unbank {bank.Name}.");
                return false;
            }
            Bank.Remove(bank);
            if (existing != null)
                existing.Quantity = Math.Min(existing.MaxStack, existing.Quantity + bank.Quantity);
            else
                Inventory.Add(bank);
            Trace.World($"Unbanked {bank.Name} x{bank.Quantity}.");
            return true;
        }
    }

    // ================================================================ Maps

    public static (string Name, int? Room) ParseMapRequest(string request)
    {
        var trimmed = (request ?? "").Trim().ToLowerInvariant();
        var dash = trimmed.IndexOf('-');
        if (dash <= 0)
            return (trimmed, null);
        var name = trimmed[..dash];
        var roomText = trimmed[(dash + 1)..];
        if (int.TryParse(roomText, out var room))
            return (name, room);
        if (double.TryParse(roomText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var big))
            return (name, (int)Math.Min(big, 999_999));
        return (name, null);
    }

    public void Join(string mapRequest, string? cell, string? pad, bool ignoreCheck, bool raiseEvents = true, bool delay = true)
    {
        var (name, room) = ParseMapRequest(mapRequest);
        if (string.IsNullOrEmpty(name))
            return;
        cell = string.IsNullOrWhiteSpace(cell) ? "Enter" : cell;
        pad = string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad;

        lock (Sync)
        {
            if (!ignoreCheck && MapLoaded && Eq(MapName, name) && (room == null || room == RoomNumber))
            {
                if (!Eq(Cell, cell))
                    JumpInternal(cell, pad, raiseEvents);
                return;
            }
            MapLoaded = false;
            Target = null;
        }

        if (delay && Rules.JoinDelayMs > 0)
            _ctx.Clock.Sleep(Rules.JoinDelayMs);

        string finalCell, finalPad;
        lock (Sync)
        {
            if (!string.IsNullOrEmpty(MapName))
                LastMap = MapName;
            _recentQueries.Clear();
            Map = BuildMap(name);
            MapName = name;
            RoomNumber = room ?? _ctx.Rng.Next(1, 30_000);
            finalCell = Map.Cells.FirstOrDefault(c => Eq(c, cell)) ?? cell;
            if (Map.Synthesized)
            {
                if (!Map.Cells.Contains(finalCell, StringComparer.OrdinalIgnoreCase))
                    Map.Cells.Add(finalCell);
                EnsureSynthesizedMonsterInCell(Map, finalCell);
            }
            else if (!Map.Cells.Contains(finalCell, StringComparer.OrdinalIgnoreCase))
            {
                finalCell = "Enter";
            }
            finalPad = pad;
            Cell = finalCell;
            Pad = finalPad;
            SpawnCell = finalCell;
            SpawnPad = finalPad;
            MapLoaded = true;
            MapsVisited.Add(name);
            if (Map.Synthesized)
                SynthesizedMapsVisited.Add(name);
            Trace.World($"Joined {name}-{RoomNumber} at {finalCell}/{finalPad} ({DescribeMapSource(Map)}, {Map.Monsters.Count} monsters, {Map.Cells.Count} cells)");
        }

        if (raiseEvents)
        {
            _ctx.RaiseEvent("MapChanged", name);
            _ctx.RaiseEvent("CellChanged", name, finalCell, finalPad);
        }
    }

    private static string DescribeMapSource(SimMap map) =>
        map.Captured ? (map.FromScenario ? "captured + scenario" : "captured data") : map.FromScenario ? "scenario" : "NO DATA - synthesized";

    private SimMap BuildMap(string name)
    {
        var map = new SimMap { Name = name };
        var data = _ctx.Catalog.FindMap(name);
        _ctx.Scenario.Maps.TryGetValue(name, out var setup);

        if (data != null)
        {
            map.Captured = true;
            map.Data = data;
            map.FileName = data.FileName;
            foreach (var (cellName, pads) in data.Cells.OrderBy(c => Eq(c.Key, "Enter") ? 0 : 1))
            {
                map.Cells.Add(cellName);
                map.Pads[cellName] = pads.ToList();
            }
            foreach (var spawn in data.Monsters.Values.OrderBy(s => s.MapID))
            {
                var captured = _ctx.Catalog.FindMonster(spawn.MonsterID, spawn.Name);
                var maxHp = spawn.MaxHP > 0 ? spawn.MaxHP : captured?.MaxHP ?? 0;
                var level = spawn.Level > 0 ? spawn.Level : captured?.Level ?? 0;
                if (maxHp <= 0)
                {
                    maxHp = GenericHp(level);
                    Trace.Synthesize($"monsterhp:{spawn.Name.ToLowerInvariant()}", $"Monster '{spawn.Name}' has no captured HP: using {maxHp}.");
                }
                map.Monsters.Add(new SimMonster
                {
                    MapID = spawn.MapID,
                    MonID = spawn.MonsterID,
                    Name = spawn.Name,
                    Cell = string.IsNullOrWhiteSpace(spawn.Cell) ? "Enter" : spawn.Cell,
                    MaxHP = maxHp,
                    HP = maxHp,
                    Level = level,
                    Race = string.IsNullOrWhiteSpace(spawn.Race) ? captured?.Race ?? "None" : spawn.Race,
                    FileName = spawn.FileName,
                    Data = captured,
                    Spawn = spawn,
                    WeaponDps = spawn.WeaponDps > 0 ? spawn.WeaponDps : captured?.WeaponDps ?? 0
                });
            }
        }

        if (setup != null)
        {
            map.FromScenario = true;
            map.Setup = setup;
            foreach (var c in setup.Cells ?? new List<string>())
            {
                if (!map.Cells.Contains(c, StringComparer.OrdinalIgnoreCase))
                    map.Cells.Add(c);
            }
            var nextMapId = map.Monsters.Count == 0 ? 1 : map.Monsters.Max(m => m.MapID) + 1;
            foreach (var ms in setup.Monsters)
            {
                if (!map.Cells.Contains(ms.Cell, StringComparer.OrdinalIgnoreCase))
                    map.Cells.Add(ms.Cell);
                var captured = _ctx.Catalog.FindMonster(ms.Id ?? 0, ms.Name);
                var level = ms.Level ?? captured?.Level ?? 50;
                var hp = ms.MaxHP ?? (captured?.MaxHP > 0 ? captured.MaxHP : GenericHp(level));
                for (var i = 0; i < Math.Max(1, ms.Count); i++)
                {
                    map.Monsters.Add(new SimMonster
                    {
                        MapID = nextMapId++,
                        MonID = ms.Id ?? captured?.ID ?? 90_000 + StableHash.Of(ms.Name) % 9_999,
                        Name = ms.Name,
                        Cell = ms.Cell,
                        MaxHP = hp,
                        HP = hp,
                        Level = level,
                        Race = captured?.Race ?? "None",
                        Data = captured,
                        WeaponDps = captured?.WeaponDps ?? 0,
                        Setup = ms
                    });
                }
            }
        }

        if (!map.Cells.Contains("Enter", StringComparer.OrdinalIgnoreCase))
            map.Cells.Insert(0, "Enter");

        if (map.Synthesized)
        {
            Trace.Synthesize($"map:{name}", $"Map '{name}' has no captured data: cells and monsters are synthesized. Run the collector on this map or describe it in the scenario 'maps' section.");
            if (_ctx.ScriptMonsterHints.TryGetValue(name, out var hints))
            {
                var nextMapId = 1;
                foreach (var (cell, monName) in hints)
                {
                    if (!map.Cells.Contains(cell, StringComparer.OrdinalIgnoreCase))
                        map.Cells.Add(cell);
                    var captured = _ctx.Catalog.FindMonster(0, monName);
                    var level = captured?.Level > 0 ? captured.Level : SynthesizedMonsterLevel();
                    var hp = captured?.MaxHP > 0 ? captured.MaxHP : GenericHp(level);
                    // Spawn 2 instances per hinted cell (like typical AQW rooms) so scripts aren't
                    // forced to wait a full respawn timer after every single kill on synthesized maps.
                    for (var copy = 0; copy < 2; copy++)
                    {
                        map.Monsters.Add(new SimMonster
                        {
                            MapID = nextMapId,
                            MonID = captured?.ID > 0 ? captured.ID : 95_000 + nextMapId,
                            Name = monName,
                            Cell = cell,
                            MaxHP = hp,
                            HP = hp,
                            Level = level,
                            Race = captured?.Race ?? "None",
                            Data = captured,
                            WeaponDps = captured?.WeaponDps ?? 0,
                            Synthesized = true
                        });
                        nextMapId++;
                    }
                }
            }
            EnsureSynthesizedMonsterInCell(map, "Enter");
        }

        return map;
    }

    private void EnsureSynthesizedMonsterInCell(SimMap map, string cell)
    {
        if (!map.Synthesized || string.IsNullOrWhiteSpace(cell))
            return;
        if (map.Monsters.Any(m => Eq(m.Cell, cell)))
            return;

        var nextMapId = map.Monsters.Count == 0 ? 1 : map.Monsters.Max(m => m.MapID) + 1;
        var level = SynthesizedMonsterLevel();
        var hp = GenericHp(level);
        for (var copy = 0; copy < 2; copy++)
        {
            map.Monsters.Add(new SimMonster
            {
                MapID = nextMapId,
                MonID = 99_000 + nextMapId,
                Name = $"Synthesized Monster ({cell})",
                Cell = cell,
                MaxHP = hp,
                HP = hp,
                Level = level,
                Race = "None",
                Synthesized = true
            });
            nextMapId++;
        }
    }

    /// <summary>
    /// On a synthesized (uncaptured) map, spawns a monster the script explicitly asked to fight by name when no
    /// monster with that name exists yet (e.g. names built at runtime that the AST hint extractor cannot see).
    /// Without this, Kill/Hunt loops on a missing monster spin forever. Returns true when something was spawned.
    /// Captured and scenario-defined maps are never touched: a missing monster there is a real script/data issue.
    /// </summary>
    public bool EnsureRequestedMonster(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "*")
            return false;
        name = name.Trim();

        lock (Sync)
        {
            if (Map == null || !Map.Synthesized || Map.Monsters.Any(m => Eq(m.Name, name)))
                return false;

            var cell = string.IsNullOrWhiteSpace(Cell) ? "Enter" : Cell;
            var captured = _ctx.Catalog.FindMonster(0, name);
            var level = captured?.Level > 0 ? captured.Level : SynthesizedMonsterLevel();
            var hp = captured?.MaxHP > 0 ? captured.MaxHP : GenericHp(level);
            var nextMapId = Map.Monsters.Count == 0 ? 1 : Map.Monsters.Max(m => m.MapID) + 1;
            for (var copy = 0; copy < 2; copy++)
            {
                Map.Monsters.Add(new SimMonster
                {
                    MapID = nextMapId,
                    MonID = captured?.ID > 0 ? captured.ID : 95_000 + nextMapId,
                    Name = name,
                    Cell = cell,
                    MaxHP = hp,
                    HP = hp,
                    Level = level,
                    Race = captured?.Race ?? "None",
                    Data = captured,
                    WeaponDps = captured?.WeaponDps ?? 0,
                    Synthesized = true
                });
                nextMapId++;
            }

            // Remember it so re-joining this map spawns it again.
            if (!_ctx.ScriptMonsterHints.TryGetValue(Map.Name, out var hints))
                _ctx.ScriptMonsterHints[Map.Name] = hints = new List<(string Cell, string MonsterName)>();
            if (!hints.Any(h => Eq(h.MonsterName, name)))
                hints.Add((cell, name));

            Trace.Synthesize($"monster:{Map.Name}:{name}",
                $"Monster '{name}' was requested on synthesized map '{Map.Name}' but no hint existed; spawned 2 in cell '{cell}' on demand.");
            return true;
        }
    }

    private int SynthesizedMonsterLevel() => Math.Clamp(Math.Max(5, (int)Math.Round(Level * 0.65)), 5, 35);

    private int GenericHp(int level)
    {
        // Use the scenario's configured per-level HP multiplier when explicitly customized,
        // otherwise scale moderately for uncaptured story/farm monsters (~180 HP/level, min 600).
        var perLevel = Rules.GenericMonsterHpPerLevel == 400 ? 180 : Math.Max(20, Rules.GenericMonsterHpPerLevel);
        return Math.Max(600, Math.Max(1, level) * perLevel);
    }

    public bool Jump(string cell, string pad, bool autoCorrect)
    {
        lock (Sync)
            return JumpInternal(cell, pad, raiseEvents: true, autoCorrect);
    }

    private bool JumpInternal(string cell, string? pad, bool raiseEvents, bool autoCorrect = true)
    {
        if (Map == null || string.IsNullOrWhiteSpace(cell))
            return false;
        var known = Map.Cells.FirstOrDefault(c => Eq(c, cell));
        if (known == null)
        {
            if (Map.Synthesized || Map.FromScenario)
            {
                Map.Cells.Add(cell);
                known = cell;
            }
            else
            {
                Trace.Warn($"Jump to cell '{cell}' which was never captured on map '{MapName}' (known cells: {string.Join(", ", Map.Cells.Take(12))}).");
                known = cell;
            }
        }
        if (Map.Synthesized)
            EnsureSynthesizedMonsterInCell(Map, known);

        var changed = !Eq(Cell, known);
        Cell = known;
        Pad = string.IsNullOrWhiteSpace(pad) ? "Spawn" : pad;
        if (changed && Target != null && !Eq(Target.Cell, Cell))
            Target = null;
        if (raiseEvents)
            _ctx.RaiseEvent("CellChanged", MapName, Cell, Pad);
        return true;
    }

    public void SetSpawnPoint(string? cell, string? pad)
    {
        lock (Sync)
        {
            SpawnCell = string.IsNullOrWhiteSpace(cell) ? Cell : cell;
            SpawnPad = string.IsNullOrWhiteSpace(pad) ? Pad : pad;
        }
    }

    // ================================================================ Progress

    /// <summary>
    /// Fingerprint of everything that counts as player progress: level, XP, gold, coins, class rank, every item
    /// stack (inventory, temp, bank, house), completed quests and dailies, story slot progress and reputation.
    /// Map/cell changes, kills, deaths and quest accepts are deliberately excluded: a loop that only walks around,
    /// dies or re-accepts the same quest is not making progress. Only compared within one run.
    /// </summary>
    public long ProgressSignature()
    {
        lock (Sync)
        {
            unchecked
            {
                long h = 17;
                void Mix(long v) => h = h * 1_000_003 + v;

                Mix(Level); Mix(XP); Mix(Gold); Mix(Coins); Mix(ClassRank);
                foreach (var it in Inventory) { Mix(it.ID); Mix(it.Quantity); }
                Mix(-1);
                foreach (var it in TempInventory) { Mix(it.ID); Mix(it.Quantity); }
                Mix(-2);
                foreach (var it in Bank) { Mix(it.ID); Mix(it.Quantity); }
                Mix(-3);
                foreach (var it in House) { Mix(it.ID); Mix(it.Quantity); }
                Mix(-4);
                Mix(QuestsCompletedCount); Mix(CompletedQuests.Count); Mix(DailiesCompleted.Count);
                foreach (var (slot, value) in SlotProgress) { Mix(slot); Mix(value); }
                foreach (var f in Factions) { Mix(f.ID); Mix(f.TotalRep); }
                return h;
            }
        }
    }

    // ================================================================ Monsters

    public static bool NameMatches(string monsterName, string? query) =>
        query == null || query == "*" || Eq(monsterName, query);

    public List<SimMonster> AllMonsters()
    {
        lock (Sync)
            return Map?.Monsters.ToList() ?? new List<SimMonster>();
    }

    public List<SimMonster> MonstersInCell(string? cell = null)
    {
        lock (Sync)
        {
            var c = cell ?? Cell;
            return Map?.Monsters.Where(m => Eq(m.Cell, c)).ToList() ?? new List<SimMonster>();
        }
    }

    public SimMonster? FindMonster(string? name, int? mapId = null, bool currentCellOnly = true, bool aliveOnly = true)
    {
        lock (Sync)
        {
            if (Map == null)
                return null;
            IEnumerable<SimMonster> q = Map.Monsters;
            if (currentCellOnly)
                q = q.Where(m => Eq(m.Cell, Cell));
            if (aliveOnly)
                q = q.Where(m => m.Alive);
            return mapId is > 0
                ? q.FirstOrDefault(m => m.MapID == mapId)
                : q.FirstOrDefault(m => NameMatches(m.Name, name));
        }
    }

    // ================================================================ Combat

    public bool Attack(SimMonster? monster)
    {
        lock (Sync)
        {
            if (!Alive || !LoggedIn || monster == null || !monster.Alive || !Eq(monster.Cell, Cell))
                return false;
            ApplyNaturalOutOfCombatRegen();
            if (Target != monster)
                Trace.Info($"Attacking {monster.Name} (MapID {monster.MapID}, HP {monster.HP}/{monster.MaxHP})");
            Target = monster;
            InCombatUntilMs = Now + 2000;
            if (!_combatTickerActive)
            {
                _combatTickerActive = true;
                _ctx.Clock.Scheduler.ScheduleRecurring(Now + CombatTickMs, CombatTickMs, "combat", CombatTick);
            }
            return true;
        }
    }

    public void SelectTarget(SimMonster? monster)
    {
        lock (Sync)
            Target = monster;
    }

    public void ClearTarget()
    {
        lock (Sync)
            Target = null;
    }

    public void ExitCombat()
    {
        lock (Sync)
        {
            Target = null;
            InCombatUntilMs = Now;
        }
    }

    private bool CombatTick()
    {
        lock (Sync)
        {
            var target = Target;
            if (!Alive || target == null || !target.Alive || !Eq(target.Cell, Cell))
            {
                if (target != null && !target.Alive)
                    Target = null;
                _combatTickerActive = false;
                return false;
            }

            var damage = RollCombatDamageTick(target, out var isCrit);
            PlayerAttacks++;
            if (isCrit)
                PlayerCrits++;
            TotalDamageDealt += damage;
            target.HP = Math.Max(0, target.HP - damage);
            InCombatUntilMs = Now + 2000;
            ApplyCombatSustain(damage);

            MonsterRetaliates(target);

            if (target.HP <= 0)
            {
                Target = null;
                _combatTickerActive = false;
                OnMonsterDeath(target);
                return false;
            }

            if (!Alive)
            {
                _combatTickerActive = false;
                return false;
            }
            return true;
        }
    }

    private void MonsterRetaliates(SimMonster monster)
    {
        if (_ctx.Scenario.Player.Invulnerable)
            return;

        var combat = monster.Data?.Combat;
        int avgDamage, interval;
        if (combat != null && combat.Hits + combat.Crits > 0)
        {
            avgDamage = (int)Math.Max(1, combat.AverageDamage);
            interval = combat.AverageAttackIntervalMs > 0 ? (int)combat.AverageAttackIntervalMs : 2000;
        }
        else if (_ctx.Catalog.EstimateMonsterDamage(monster.Level, monster.WeaponDps) is { } estimate)
        {
            avgDamage = (int)Math.Max(1, Math.Round(estimate.AverageHit));
            interval = (int)Math.Round(estimate.IntervalMs);
            Trace.Synthesize($"combat:{monster.Name.ToLowerInvariant()}", $"Monster '{monster.Name}' has no captured combat data: estimated {avgDamage} damage every {interval} ms from its wDPS {monster.WeaponDps} and {estimate.BasedOn} measured monster(s) of similar level.");
        }
        else
        {
            avgDamage = Math.Max(15, Math.Max(1, monster.Level) * 5);
            interval = 2000;
            Trace.Synthesize($"combat:{monster.Name.ToLowerInvariant()}", $"Monster '{monster.Name}' has no captured combat data: assuming {avgDamage} damage every {interval} ms.");
        }

        if (Now >= monster.NextAttackMs)
        {
            ApplyMonsterRetaliationDamage(avgDamage);
            monster.NextAttackMs = Now + interval;
        }

        if (_ctx.Scenario.Player.RegenPercentPerSecond is > 0 and var regenPct)
            Health = Math.Min(MaxHealth, Health + (int)(MaxHealth * regenPct / 100.0));
        if (Health <= 0)
            OnPlayerDeath(monster);
    }

    private void OnMonsterDeath(SimMonster monster)
    {
        monster.HP = 0;
        Kills++;
        KillsByMonster[monster.Name] = KillsByMonster.GetValueOrDefault(monster.Name) + 1;

        var (baseXp, baseGold) = KillRewardBase(monster);
        var boostedXp = (int)Math.Round(baseXp * EffectiveXpMultiplier);
        var boostedGold = (int)Math.Round(baseGold * EffectiveGoldMultiplier);
        GainExperience(boostedXp);
        AddGold(boostedGold);
        GainClassPoints(KillClassPointsBase(monster));
        if (monster.Data?.KillRewards is { RepSamples: > 0 } repData && repData.FactionIds.Count == 1)
            GainReputation(repData.FactionIds.First(), (int)Math.Round(repData.AverageBaseRep), $"kill {monster.Name}");
        Trace.World($"Defeated {monster.Name} (MapID {monster.MapID}) in {monster.Cell} (+{boostedXp} xp, +{boostedGold} gold).");

        var respawnMs = ResolveRespawnMs(monster);
        var map = Map;
        _ctx.Clock.Scheduler.Schedule(Now + respawnMs, "respawn:" + monster.Name, () =>
        {
            lock (Sync)
            {
                if (Map == map)
                    monster.HP = monster.MaxHP;
            }
        });

        _ctx.RaiseEvent("MonsterKilled", monster.MapID);
        RollDrops(monster);
    }

    private void OnPlayerDeath(SimMonster killer)
    {
        Health = 0;
        Target = null;
        Deaths++;
        DeadUntilMs = Now + Math.Max(1, Rules.PlayerRespawnSeconds) * 1000L;
        Trace.World($"Player died (killed by {killer.Name} in {Cell}). Respawning in {Rules.PlayerRespawnSeconds}s.");
        _ctx.RaiseEvent("PlayerDeath");

        _ctx.Clock.Scheduler.Schedule(DeadUntilMs, "player-respawn", () =>
        {
            lock (Sync)
            {
                DeadUntilMs = -1;
                Health = MaxHealth;
                Trace.World($"Player respawned at {SpawnCell}/{SpawnPad}.");
                if (!Eq(Cell, SpawnCell))
                    JumpInternal(SpawnCell, SpawnPad, raiseEvents: true);
            }
        });
    }

    /// <summary>
    /// XP needed to reach the next level: the value captured from "levelUp" (intExpToLevel), else AQW's table
    /// (<see cref="AqwXpTable"/>, AQW Wiki, identical to every captured level).
    /// </summary>
    public int XpToNextLevel(int level)
    {
        var lvl = Math.Clamp(level, 1, MaxLevel);
        if (_ctx.Catalog.Captured.XpToNextLevel is { } table && table.TryGetValue(lvl, out var real) && real > 0)
            return real;
        if (AqwXpTable.XpToNext(lvl) is > 0 and var wiki)
            return wiki;
        return RequiredXpForLevel(lvl);
    }

    public static int RequiredXpForLevel(int level)
    {
        var lvl = Math.Clamp(level, 1, MaxLevel);
        return (int)Math.Min(int.MaxValue, (long)lvl * lvl * 25 + lvl * 100);
    }

    /// <summary>AQW gold cap.</summary>
    public const long MaxGold = 100_000_000;

    // Per-kill rule observed by the collector on every measured monster of level 1-90 (schema v3 KillRewards):
    // base XP = 10 x monster level, base gold = 3 x monster level. Used only when the monster has no measurement.
    private const int XpPerMonsterLevel = 10;
    private const int GoldPerMonsterLevel = 3;

    // AQW respawn after the 2020 combat update is "about 3 to 5 seconds" (artix.com/posts/aqw-combat-improvements);
    // only used when the collector has no measurement at all.
    private const long OfficialRespawnFallbackMs = 4_000;

    internal void AddGold(long amount)
    {
        if (amount > 0 && Gold < MaxGold)
            Gold = Math.Min(MaxGold, Gold + amount);
    }

    private int EffectiveMonsterLevel(SimMonster monster, string what)
    {
        if (monster.Level > 0)
            return monster.Level;
        Trace.Synthesize($"monsterlevel:{monster.Name.ToLowerInvariant()}", $"Monster '{monster.Name}' has no captured level: {what} uses the player's level ({Math.Max(1, Level)}).");
        return Math.Max(1, Level);
    }

    /// <summary>
    /// Base (unboosted) XP and gold per kill: the collector's measured averages (addGoldExp) first, then the observed
    /// rule (10 / 3 per monster level), flagged as synthesized. A monster measured only on max-level characters
    /// (XP always 0) or gold-capped ones keeps the rule for that part.
    /// </summary>
    private (int Xp, int Gold) KillRewardBase(SimMonster monster)
    {
        // Scenario > collected data: a scenario that sets a different level describes another monster, so the
        // measured rewards of the captured one do not apply.
        var kr = monster.Setup?.Level is { } setupLevel && monster.Data != null && monster.Data.Level > 0 && setupLevel != monster.Data.Level
            ? null
            : monster.Data?.KillRewards;
        var key = monster.Name.ToLowerInvariant();
        int xp, gold;
        if (kr is { ExpSamples: > 0 })
            xp = (int)Math.Round(kr.AverageBaseExp);
        else
        {
            xp = XpPerMonsterLevel * EffectiveMonsterLevel(monster, "XP per kill");
            Trace.Synthesize($"killxp:{key}", $"Monster '{monster.Name}' has no measured XP per kill: using the observed rule {XpPerMonsterLevel} x level = {xp}.");
        }
        if (kr is { GoldSamples: > 0 })
            gold = (int)Math.Round(kr.AverageBaseGold);
        else
        {
            gold = GoldPerMonsterLevel * EffectiveMonsterLevel(monster, "gold per kill");
            Trace.Synthesize($"killgold:{key}", $"Monster '{monster.Name}' has no measured gold per kill: using the observed rule {GoldPerMonsterLevel} x level = {gold}.");
        }
        return (xp, gold);
    }

    /// <summary>
    /// Base class points per kill: 230 + 25 x class rank (0 at rank 10), times the level-gap ratio
    /// (<see cref="ClassPointsGapRatio"/>), rounded up. Both parts verified against collected kills.
    /// </summary>
    private int KillClassPointsBase(SimMonster monster)
    {
        // AQW rule (AQW Wiki "Experience, Class Points & Reputation"; matches the collected 255-455 values of ranks 1-9):
        // CP per kill = 230 + 25 x class rank, reduced when the monster is below the character's level.
        if (ClassRank >= 10)
            return 0;
        var fullCp = 230 + 25 * Math.Max(1, ClassRank);
        var monsterLevel = monster.Level > 0 ? monster.Level : Level;
        var gap = Level - monsterLevel;
        return (int)Math.Ceiling(fullCp * ClassPointsGapRatio(gap) - 1e-9);
    }

    /// <summary>
    /// Share of the full class points for a monster <paramref name="gap"/> levels below the character: 100 % up to 10
    /// levels, -10 % per level from 11, at least 5 % (-95 % cap). Verified 2026-11-22 on a Shaman rank 1 -> 10 run in
    /// icestormarena: gap 5/10 = 100 %, 15 = 50 %, 20-95 = 5 %; results are rounded up (305 x 50 % = 153, 305 x 5 % = 16).
    /// This is the AQW Wiki rule; the 2018 design note's -5 %/level (zero at 20) does not match the data.
    /// </summary>
    public static double ClassPointsGapRatio(int gap) =>
        gap <= 10 ? 1.0 : Math.Max(0.05, 1.0 - 0.1 * (gap - 10));

    /// <summary>
    /// Respawn delay: scenario "world.monsterRespawnSeconds" (when set) > this spawn's measured average > the
    /// monster's measured average > median of all measured monsters (synthesized) > official 3-5 s (synthesized).
    /// </summary>
    private long ResolveRespawnMs(SimMonster monster)
    {
        if (Rules.MonsterRespawnSeconds is { } forced)
            return Math.Max(1, forced) * 1000L;
        if (monster.Spawn?.Respawn is { Samples: > 0 } spawnRespawn && spawnRespawn.AverageMs > 0)
            return (long)Math.Round(spawnRespawn.AverageMs);
        if (monster.Data?.Respawn is { Samples: > 0 } monsterRespawn && monsterRespawn.AverageMs > 0)
            return (long)Math.Round(monsterRespawn.AverageMs);

        var key = $"respawn:{monster.Name.ToLowerInvariant()}";
        var median = _ctx.Catalog.GlobalRespawnMedianMs;
        if (median > 0)
        {
            Trace.Synthesize(key, $"Monster '{monster.Name}' has no measured respawn: using the median of measured monsters ({median / 1000.0:0.0} s).");
            return (long)Math.Round(median);
        }
        Trace.Synthesize(key, $"Monster '{monster.Name}' has no measured respawn and no respawn data exists: using AQW's official ~{OfficialRespawnFallbackMs / 1000} s.");
        return OfficialRespawnFallbackMs;
    }

    private void GainExperience(int amount)
    {
        if (amount <= 0)
            return;
        if (Level >= MaxLevel)
        {
            Level = MaxLevel;
            XP = 0;
            return;
        }

        long pool = (long)XP + amount;
        while (Level < MaxLevel)
        {
            var needed = XpToNextLevel(Level);
            if (pool < needed)
                break;
            pool -= needed;
            Level++;
            if (_ctx.Scenario.Player.MaxHealth.HasValue)
            {
                MaxHealth += 35;
                if (Alive)
                    Health = MaxHealth;
            }
            else
            {
                RecalculateStats(healToFullOnIncrease: true);
            }
            Trace.World($"Level up! Reached Lv. {Level} (MaxHP {MaxHealth}, DPS {(int)CombatProfile.EffectiveDps}).");
        }

        XP = Level >= MaxLevel ? 0 : (int)Math.Min(int.MaxValue, pool);
    }

    public void Rest()
    {
        lock (Sync)
        {
            if (Alive)
                Health = MaxHealth;
        }
    }

    // ================================================================ Drops

    private void RollDrops(SimMonster monster)
    {
        var dropped = new HashSet<int>();

        if (monster.Setup?.Drops is { Count: > 0 } scenarioDrops)
        {
            foreach (var (itemName, chance) in scenarioDrops)
            {
                var info = ResolveItem(itemName);
                if (_ctx.Rng.NextDouble() < chance)
                {
                    GiveDrop(info, 1, "scenario drop");
                    dropped.Add(info.ID);
                }
            }
        }

        if (monster.Data != null)
        {
            foreach (var drop in monster.Data.Drops.Values)
            {
                if (dropped.Contains(drop.ItemID))
                    continue;

                // Quest-gated items (AQW "sReqQuests") only drop while one of their quests is active.
                if (drop.QuestGated && drop.RequiredQuestIds.Count > 0 && !drop.RequiredQuestIds.Any(ActiveQuests.Contains))
                    continue;

                double chance;
                if (drop.EstimatedDropChance is { } observed)
                {
                    chance = observed;
                }
                else
                {
                    // Known source (observed or official list) without a trustworthy rate: never use 0 %, invent one.
                    chance = Math.Clamp(Rules.GenericDropChance, 0, 1);
                    var why = drop.QuestGated
                        ? "quest-gated and not yet measured with the quest active"
                        : drop.IsOfficialDrop && drop.DropEvents == 0 ? "on the official drop list but never observed dropping" : "rate not measured";
                    Trace.Synthesize($"droprate:{monster.Name.ToLowerInvariant()}:{drop.ItemID}",
                        $"'{drop.ItemName}' is a known drop of '{monster.Name}' ({why}): using an invented {chance:P0} chance.");
                }
                if (_ctx.Rng.NextDouble() >= chance)
                    continue;
                var info = _ctx.Catalog.FindItem(drop.ItemID) ?? new ItemInfo { ID = drop.ItemID, Name = drop.ItemName, Temp = drop.Temp, MaxStack = 1000, Source = DataSource.Captured };
                var qty = drop.DropEvents > 0 ? Math.Max(1, (int)Math.Round((double)drop.TotalQuantity / drop.DropEvents)) : 1;
                GiveDrop(info, qty, "captured drop");
                dropped.Add(drop.ItemID);
            }
        }

        if (!Rules.SynthesizeDrops)
            return;

        foreach (var wanted in CurrentlyFarmedItems())
        {
            var id = wanted.Info.ID;
            if (dropped.Contains(id))
                continue;
            if (monster.Data?.Drops.ContainsKey(id) == true)
                continue;
            if (monster.Setup?.Drops.ContainsKey(wanted.Info.Name) == true)
                continue;
            var sources = _ctx.Catalog.DropSources(id);
            if (sources.Count > 0 && !sources.Any(s => s.ID == monster.MonID || Eq(s.Name, monster.Name)))
            {
                Trace.Synthesize($"nodrop:{monster.Name.ToLowerInvariant()}:{id}",
                    $"'{wanted.Info.Name}' was captured dropping from {string.Join(", ", sources.Select(s => s.Name).Distinct().Take(3))}, not from '{monster.Name}': no drop synthesized.");
                continue;
            }

            var chance = Math.Clamp(Rules.GenericDropChance, 0, 1);
            Trace.Synthesize($"drop:{monster.Name.ToLowerInvariant()}:{id}",
                $"'{wanted.Info.Name}' drops from '{monster.Name}' with an invented {chance:P0} chance (no captured drop data).");
            if (_ctx.Rng.NextDouble() < chance)
            {
                GiveDrop(wanted.Info, 1, "synthesized drop");
                dropped.Add(id);
            }
        }
    }

    private void GiveDrop(ItemInfo info, int quantity, string source)
    {
        DropsByItem[info.Name] = DropsByItem.GetValueOrDefault(info.Name) + quantity;
        if (info.Temp)
        {
            AddTemp(info, quantity);
            var now = TempInventory.FirstOrDefault(i => i.ID == info.ID)?.Quantity ?? quantity;
            Trace.World($"Drop (temp): {info.Name} x{quantity} -> {now} [{source}]");
            _ctx.RaiseEvent("ItemDropped", NewItemBase(info, quantity), true, now);
            return;
        }

        var stacked = DropStack.FirstOrDefault(d => d.ID == info.ID);
        if (stacked != null)
            stacked.Quantity += quantity;
        else
            DropStack.Add(NewItemBase(info, quantity));
        Trace.World($"Drop: {info.Name} x{quantity} waiting for pickup [{source}]");
        _ctx.RaiseEvent("ItemDropped", NewItemBase(info, quantity), false, quantity);

        if (_ctx.GetBotOption("AcceptAllDrops", false))
            Pickup(info.Name, info.ID);
    }

    public bool Pickup(string? name, int? id)
    {
        lock (Sync)
        {
            var drop = DropStack.FirstOrDefault(d => Matches(d, name, id));
            if (drop == null)
                return false;
            if (!AddToInventory(InfoOf(drop), drop.Quantity, "pickup"))
                return false;
            DropStack.Remove(drop);
            DropsPicked++;
            return true;
        }
    }

    public void Reject(string? name, int? id)
    {
        lock (Sync)
        {
            var drop = DropStack.FirstOrDefault(d => Matches(d, name, id));
            if (drop != null)
            {
                DropStack.Remove(drop);
                Trace.World($"Rejected drop {drop.Name}.");
            }
        }
    }

    public void StartDrops()
    {
        lock (Sync)
        {
            DropsEnabled = true;
            if (_dropTickerActive)
                return;
            _dropTickerActive = true;
            _ctx.Clock.Scheduler.ScheduleRecurring(Now + DropInterval, Math.Max(100, DropInterval), "drops", DropTick);
        }
    }

    public void StopDrops()
    {
        lock (Sync)
            DropsEnabled = false;
    }

    private bool DropTick()
    {
        lock (Sync)
        {
            if (!DropsEnabled)
            {
                _dropTickerActive = false;
                return false;
            }
            foreach (var drop in DropStack.ToList())
            {
                if (PickupNames.Contains(drop.Name) || PickupIds.Contains(drop.ID))
                    Pickup(drop.Name, drop.ID);
                else if (RejectElse)
                    Reject(drop.Name, drop.ID);
            }
            return true;
        }
    }

    // ================================================================ Map items

    public void GetMapItem(int mapItemId, int quantity)
    {
        lock (Sync)
        {
            quantity = Math.Max(1, quantity);
            if (Map?.Setup?.MapItems.TryGetValue(mapItemId.ToString(), out var scenarioItem) == true)
            {
                AddToInventory(ResolveItem(scenarioItem), quantity, $"map item {mapItemId}");
                return;
            }

            if (Map?.Data?.MapItems.TryGetValue(mapItemId, out var captured) == true && captured.GrantedItemIDs.Count > 0)
            {
                // The server only grants quest map items while one of the quests that use them is active.
                if (captured.RequiredQuestIds.Count > 0 && !captured.RequiredQuestIds.Any(ActiveQuests.Contains))
                {
                    Trace.World($"GetMapItem({mapItemId}) on '{MapName}' gave nothing: requires one of quest(s) {string.Join(", ", captured.RequiredQuestIds.OrderBy(q => q))} to be active.");
                    return;
                }
                foreach (var itemId in captured.GrantedItemIDs)
                    AddToInventory(ResolveItem(null, itemId), quantity, $"map item {mapItemId}");
                return;
            }

            var wanted = CurrentlyFarmedItems().FirstOrDefault();
            if (wanted != null)
            {
                Trace.Synthesize($"mapitem:{MapName}:{mapItemId}", $"Map item {mapItemId} on '{MapName}' has no captured data: assumed to give '{wanted.Info.Name}' (the item the script is looking for).");
                AddToInventory(wanted.Info, quantity, $"map item {mapItemId}");
                return;
            }

            Trace.Warn($"GetMapItem({mapItemId}) on '{MapName}': unknown map item and no item being farmed; nothing was given.");
        }
    }

    // ================================================================ Quests

    public Quest? GetOrLoadQuest(int id)
    {
        lock (Sync)
        {
            if (LoadedQuests.TryGetValue(id, out var loaded))
                return loaded;

            var data = _ctx.Catalog.FindQuest(id);
            Quest quest;
            if (data != null)
            {
                quest = new Quest
                {
                    ID = data.ID,
                    Name = data.Name,
                    Slot = data.Slot,
                    Value = data.Value,
                    Once = data.Once,
                    Field = data.Field,
                    Index = data.Index,
                    Upgrade = data.Upgrade,
                    Level = data.Level,
                    RequiredClassID = data.RequiredClassID,
                    RequiredClassPoints = data.RequiredClassPoints,
                    RequiredFactionId = data.RequiredFactionId,
                    RequiredFactionRep = data.RequiredFactionRep,
                    Gold = data.Gold,
                    XP = data.XP,
                    AcceptRequirements = data.AcceptRequirements?.ToList() ?? new List<ItemBase>(),
                    Rewards = data.Rewards?.ToList() ?? new List<ItemBase>(),
                    SimpleRewards = data.SimpleRewards?.ToList() ?? new List<SimpleReward>(),
                    Description = "",
                    EndText = ""
                };
                SetRequirements(quest, data.Requirements?.ToList() ?? new List<ItemBase>());
            }
            else if (_ctx.Catalog.FindCapturedQuest(id) is { } cap && (cap.Requirements.Count > 0 || cap.Rewards.Count > 0 || cap.ObservedReward != null))
            {
                static ItemBase ToItem(CapturedItemRequirement r) =>
                    new() { ID = r.ItemID, Name = r.ItemName, Quantity = Math.Max(1, r.Quantity), Temp = r.Temp };
                quest = new Quest
                {
                    ID = cap.ID,
                    Name = string.IsNullOrWhiteSpace(cap.Name) ? $"Quest #{id}" : cap.Name,
                    Slot = cap.Slot,
                    Value = cap.Value,
                    Once = cap.Once,
                    Field = cap.Field,
                    Index = cap.Index,
                    Upgrade = cap.Upgrade,
                    Level = cap.Level,
                    RequiredClassID = cap.RequiredClassID,
                    RequiredClassPoints = cap.RequiredClassPoints,
                    RequiredFactionId = cap.RequiredFactionId,
                    RequiredFactionRep = cap.RequiredFactionRep,
                    Gold = cap.ObservedReward?.Gold ?? cap.Gold,
                    XP = cap.ObservedReward?.Exp ?? cap.XP,
                    AcceptRequirements = cap.AcceptRequirements.Select(ToItem).ToList(),
                    Rewards = cap.Rewards.Select(ToItem).ToList(),
                    SimpleRewards = cap.Rewards.Select(r => new SimpleReward
                    {
                        ID = r.ItemID,
                        Name = r.ItemName,
                        Quantity = Math.Max(1, r.Quantity),
                        Rate = r.RatePercent ?? 100.0,
                        Type = r.RewardType ?? 0
                    }).ToList(),
                    Description = "",
                    EndText = ""
                };
                SetRequirements(quest, cap.Requirements.Select(ToItem).ToList());
                Trace.World($"Quest {id} is not in QuestData.json: using the collector's captured requirements/rewards.");
            }
            else
            {
                quest = new Quest
                {
                    ID = id,
                    Name = $"Quest #{id}",
                    Slot = -1,
                    AcceptRequirements = new List<ItemBase>(),
                    Rewards = new List<ItemBase>(),
                    SimpleRewards = new List<SimpleReward>(),
                    Description = "",
                    EndText = ""
                };
                SetRequirements(quest, new List<ItemBase>());
                Trace.Synthesize($"quest:{id}", $"Quest {id} is not in QuestData.json: simulated with no requirements and no rewards.");
            }

            LoadedQuests[id] = quest;
            return quest;
        }
    }

    private static void SetRequirements(Quest quest, List<ItemBase> requirements)
    {
        QuestReqsField?.SetValue(quest, requirements);
        QuestReqCacheField?.SetValue(quest, requirements);
    }

    public bool IsUnlocked(Quest q)
    {
        lock (Sync)
            return q.Slot < 0 || SlotProgress.GetValueOrDefault(q.Slot) >= q.Value - 1;
    }

    public bool HasBeenCompleted(Quest q)
    {
        lock (Sync)
            return q.Slot >= 0 ? SlotProgress.GetValueOrDefault(q.Slot) >= q.Value : CompletedQuests.Contains(q.ID);
    }

    public bool AcceptQuest(int id, bool log = true)
    {
        lock (Sync)
        {
            var q = GetOrLoadQuest(id)!;
            if (ActiveQuests.Contains(id))
                return true;

            string? reason = null;
            if (!LoggedIn)
                reason = "not logged in";
            else if (q.Upgrade && !IsMember)
                reason = "member-only quest";
            else if (Level < q.Level)
                reason = $"requires level {q.Level}";
            else if (!IsUnlocked(q))
                reason = $"locked (slot {q.Slot} progress {SlotProgress.GetValueOrDefault(q.Slot)} < {q.Value - 1})";
            else if (q.Once && HasBeenCompleted(q))
                reason = "one-time quest already completed";
            else if (DailiesCompleted.Contains(id))
                reason = "daily/weekly quest already completed today";
            else
            {
                // Accept requirements are ownership checks: inventory, bank or the house inventory. House items (e.g.
                // "Prime Fiend Shard" for quest 9555) open quests without being in the house map.
                var missing = q.AcceptRequirements?.FirstOrDefault(r =>
                    Quantity(Inventory, r.Name, r.ID) + Quantity(Bank, r.Name, r.ID) + Quantity(House, r.Name, r.ID) < Math.Max(1, r.Quantity));
                if (missing != null)
                    reason = IsHouseItem(missing)
                        ? $"missing accept requirement '{missing.Name}' (a house item: add it to the scenario's \"house\" list)"
                        : $"missing accept requirement '{missing.Name}'";
            }

            if (reason != null)
            {
                if (log)
                    Trace.World($"Accept quest {id} ({q.Name}) failed: {reason}.");
                return false;
            }

            ActiveQuests.Add(id);
            QuestsAcceptedCount++;
            Trace.World($"Accepted quest {id} ({q.Name}).");
            EnsureQuestTicker();
        }
        _ctx.RaiseEvent("QuestAccepted", id);
        return true;
    }

    private void EnsureQuestTicker()
    {
        if (_questTickerActive)
            return;
        _questTickerActive = true;
        _ctx.Clock.Scheduler.ScheduleRecurring(Now + 700, 700, "quest-watcher", RegisteredQuestTick);
    }

    public bool CanComplete(int id)
    {
        lock (Sync)
        {
            if (!ActiveQuests.Contains(id))
                return false;
            var q = GetOrLoadQuest(id)!;
            return q.Requirements.All(r => Quantity(r.Temp ? TempInventory : Inventory, r.Name, r.ID) >= Math.Max(1, r.Quantity));
        }
    }

    public bool CompleteQuest(int id, int rewardItemId, bool log = true)
    {
        Quest q;
        lock (Sync)
        {
            q = GetOrLoadQuest(id)!;
            if (!CanComplete(id))
            {
                if (log)
                {
                    var reason = !ActiveQuests.Contains(id)
                        ? "quest not accepted"
                        : "missing " + string.Join(", ", q.Requirements
                            .Where(r => Quantity(r.Temp ? TempInventory : Inventory, r.Name, r.ID) < Math.Max(1, r.Quantity))
                            .Select(r => $"{r.Name} {Quantity(r.Temp ? TempInventory : Inventory, r.Name, r.ID)}/{r.Quantity}"));
                    Trace.World($"Complete quest {id} ({q.Name}) failed: {reason}.");
                }
                return false;
            }

            foreach (var req in q.Requirements)
                RemoveItem(req.Name, req.ID, Math.Max(1, req.Quantity), req.Temp, $"quest {id} turn-in");

            foreach (var reward in q.Rewards ?? new List<ItemBase>())
            {
                if (rewardItemId > 0 && reward.ID != rewardItemId && q.Rewards!.Any(r => r.ID == rewardItemId))
                    continue;
                var rate = q.SimpleRewards?.FirstOrDefault(s => s.ID == reward.ID)?.Rate ?? 100.0;
                if (rewardItemId <= 0 && _ctx.Rng.NextDouble() * 100.0 >= rate)
                    continue;
                AddToInventory(_ctx.Catalog.FindItem(reward.ID) ?? InfoOf(reward), Math.Max(1, reward.Quantity), $"quest {id} reward");
            }

            var questGold = (int)Math.Round(q.Gold * EffectiveGoldMultiplier);
            var questXp = (int)Math.Round(q.XP * EffectiveXpMultiplier);
            AddGold(questGold);
            GainExperience(questXp);

            // Reputation and class points are not in QuestData.json: they come from the turn-in rewards the collector
            // observed ("ccqr" rewardObj, base values). Quests never turned in while collecting give none (flagged once).
            if (_ctx.Catalog.FindCapturedQuest(id)?.ObservedReward is { } observed)
            {
                if (observed.Rep > 0)
                    GainReputation(observed.FactionID, observed.Rep, $"quest {id}");
                if (observed.ClassPoints > 0)
                    GainClassPoints(observed.ClassPoints);
            }
            else if (_ctx.Catalog.FindQuest(id) != null)
                Trace.Synthesize($"questrep:{id}", $"Quest {id} ({q.Name}) works normally (requirements, XP, gold and items from QuestData), but it was never turned in while collecting, so a possible faction reputation reward is unknown and none is given. Only matters for reputation farms: turn it in once with the collector running.");
            if (q.Slot >= 0)
                SlotProgress[q.Slot] = Math.Max(SlotProgress.GetValueOrDefault(q.Slot), q.Value);
            if (q.Once || q.Slot >= 0)
                CompletedQuests.Add(id);
            if (!string.IsNullOrWhiteSpace(q.Field))
                DailiesCompleted.Add(id);
            ActiveQuests.Remove(id);
            QuestsCompletedCount++;
            QuestTurnIns[id] = QuestTurnIns.GetValueOrDefault(id) + 1;
            Trace.World($"Completed quest {id} ({q.Name}) (+{questGold} gold, +{questXp} xp).");
        }
        _ctx.RaiseEvent("QuestTurnedIn", id);
        return true;
    }

    public void RegisterQuests(IEnumerable<(int Id, int Reward)> quests)
    {
        lock (Sync)
        {
            foreach (var (id, reward) in quests)
                RegisteredQuests[id] = reward;
            if (RegisteredQuests.Count > 0)
                EnsureQuestTicker();
        }
    }

    private bool RegisteredQuestTick()
    {
        // Like Skua's quest registration: only quests registered with Bot.Quests.RegisterQuests are completed (and
        // re-accepted) automatically. Quests that are merely active wait for the script's own EnsureComplete; completing
        // them here consumed the items the script was about to check (map items, single quest drops) and caused loops.
        List<(int Id, int Reward)> candidates;
        lock (Sync)
        {
            if (RegisteredQuests.Count == 0)
            {
                _questTickerActive = false;
                return false;
            }
            candidates = RegisteredQuests.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        foreach (var (id, reward) in candidates)
        {
            if (!ActiveQuests.Contains(id))
                AcceptQuest(id, log: false);
            if (CanComplete(id))
            {
                CompleteQuest(id, reward);
                AcceptQuest(id, log: false);
            }
        }
        return true;
    }

    // ================================================================ Shops

    public ShopInfo LoadShop(int shopId)
    {
        lock (Sync)
        {
            var cached = ShopCache.FirstOrDefault(s => s.ID == shopId);
            if (cached == null)
            {
                var items = new List<ShopItem>();
                string name;
                if (_ctx.Catalog.Captured.Shops.TryGetValue(shopId, out var data))
                {
                    name = data.Name;
                    foreach (var si in data.Items)
                    {
                        items.Add(new ShopItem
                        {
                            ID = si.ID,
                            Name = si.Name,
                            ShopItemID = si.ShopItemID,
                            Cost = si.Cost,
                            Coins = si.Coins,
                            Upgrade = si.Upgrade,
                            Level = si.Level,
                            MaxStack = Math.Max(1, si.MaxStack),
                            Faction = si.Faction,
                            RequiredReputation = si.RequiredReputation,
                            CategoryString = si.Category,
                            Quantity = 1,
                            TokenID = si.TokenID,
                            TokenQuantity = si.TokenQuantity,
                            TokenName = si.TokenName,
                            EnhancementPatternID = si.EnhancementPatternID,
                            ProcID = si.ProcID,
                            Meta = si.Meta,
                            Requirements = si.Requirements.Select(r => (ItemBase)NewItemBase(_ctx.Catalog.FindItem(r.ItemID) ?? new ItemInfo { ID = r.ItemID, Name = r.ItemName, Temp = r.Temp }, r.Quantity)).ToList()
                        });
                    }
                }
                else
                {
                    name = $"Shop #{shopId}";
                    if (_ctx.ScriptShopHints.TryGetValue(shopId, out var hintedItems))
                    {
                        var nextShopItemId = 1;
                        foreach (var itemName in hintedItems)
                        {
                            var info = ResolveItem(itemName);
                            items.Add(new ShopItem
                            {
                                ID = info.ID,
                                Name = info.Name,
                                ShopItemID = shopId * 100 + nextShopItemId++,
                                Cost = 0,
                                Coins = info.Coins,
                                Upgrade = info.Upgrade,
                                MaxStack = Math.Max(1, info.MaxStack),
                                CategoryString = info.Category,
                                Quantity = 1,
                                Requirements = new List<ItemBase>()
                            });
                        }
                    }
                    Trace.Synthesize($"shop:{shopId}", $"Shop {shopId} has no captured data: synthesized {items.Count} item(s){(Rules.LenientShops ? " (free, no merge requirements checked)" : "")}.");
                }
                cached = new ShopInfo(shopId, name, items);
                ShopCache.Add(cached);
            }
            CurrentShop = cached;
            Trace.World($"Loaded shop {shopId} ({cached.Name}, {cached.Items.Count} items).");
            return cached;
        }
    }

    public bool Buy(string? name, int? itemId, int shopItemId, int quantity)
    {
        int charItemId;
        lock (Sync)
        {
            var shop = CurrentShop;
            var item = shop?.Items.FirstOrDefault(i => shopItemId > 0 ? i.ShopItemID == shopItemId : Matches(i, name, itemId));
            var qty = quantity > 0 ? quantity : 1;

            if (item == null)
            {
                if (shop == null)
                {
                    Trace.World($"BuyItem({name ?? itemId?.ToString()}) failed: no shop loaded.");
                    return false;
                }
                if (!Rules.LenientShops)
                {
                    Trace.World($"BuyItem({name ?? itemId?.ToString()}) failed: item not in shop {shop.ID}.");
                    return false;
                }
                var info = ResolveItem(name, itemId);
                Trace.Synthesize($"buy:{shop.ID}:{info.Name.ToLowerInvariant()}", $"Bought '{info.Name}' from shop {shop.ID} without captured shop data (free, no requirements checked).");
                AddToInventory(info, qty, $"bought from shop {shop.ID}");
                charItemId = Inventory.FirstOrDefault(i => i.ID == info.ID)?.CharItemID ?? 0;
            }
            else
            {
                string? reason = null;
                var totalCost = (long)item.Cost * qty;
                if (item.Upgrade && !IsMember)
                    reason = "member-only item";
                else if (item.Coins ? Coins < totalCost : Gold < totalCost)
                    reason = $"not enough {(item.Coins ? "AC" : "gold")} ({totalCost} needed)";
                else
                {
                    var missing = item.Requirements?.FirstOrDefault(r => Quantity(r.Temp ? TempInventory : Inventory, r.Name, r.ID) < Math.Max(1, r.Quantity) * qty);
                    if (missing != null)
                        reason = $"missing merge requirement '{missing.Name}' x{missing.Quantity * qty}";
                }

                if (reason != null)
                {
                    Trace.World($"Buy '{item.Name}' failed: {reason}.");
                    return false;
                }

                foreach (var req in item.Requirements ?? new List<ItemBase>())
                    RemoveItem(req.Name, req.ID, Math.Max(1, req.Quantity) * qty, req.Temp, $"merge for {item.Name}");
                if (item.Coins)
                    Coins -= (int)totalCost;
                else
                    Gold -= totalCost;
                AddToInventory(_ctx.Catalog.FindItem(item.ID) ?? InfoOf(item), qty, $"bought from shop {shop!.ID}");
                charItemId = Inventory.FirstOrDefault(i => i.ID == item.ID)?.CharItemID ?? 0;
            }
        }
        _ctx.RaiseEvent("ItemBought", charItemId);
        return true;
    }

    // Sell-back rule until the server's sellItem response is verified from collector samples.
    private const double SellBackFraction = 0.25;

    /// <summary>
    /// Gold returned when selling: a share of the item's shop price (captured shops / shops loaded this run). Gold
    /// items only; AC items return nothing until verified. Both are flagged because the rule is not measured yet.
    /// </summary>
    private void SellReturn(InventoryItem item, int quantity)
    {
        // 1. Measured: the server's sellItem amount for this exact item.
        if (_ctx.Catalog.Captured.SellPrices?.TryGetValue(item.ID, out var measured) == true && measured.Samples > 0)
        {
            if (measured.Coins)
                Coins += measured.Amount;
            else
                AddGold(measured.Amount);
            Trace.World($"Sold {quantity}x {item.Name} for {measured.Amount:N0} {(measured.Coins ? "ACs" : "gold")} (measured).");
            return;
        }

        var shopItem = ShopCache.SelectMany(s => s.Items).FirstOrDefault(i => i.ID == item.ID);
        int cost; bool coins;
        if (shopItem != null)
        {
            cost = shopItem.Cost;
            coins = shopItem.Coins;
        }
        else if (_ctx.Catalog.Captured.Shops.Values.SelectMany(s => s.Items).FirstOrDefault(i => i.ID == item.ID) is { } captured)
        {
            cost = captured.Cost;
            coins = captured.Coins;
        }
        else
        {
            Trace.Synthesize($"sell:{item.ID}", $"'{item.Name}' is in no known shop: its sell price is unknown and no gold was given.");
            return;
        }

        if (coins || item.Coins)
        {
            Trace.Synthesize($"sell:{item.ID}", $"'{item.Name}' is an AC item: the AC sell-back is not modeled, nothing was returned.");
            return;
        }
        // 2. Ratio measured on other gold items (sell amount / shop price; 25 % on 20 items, 2026-11-21), else the 25 % rule.
        var (fraction, basedOn) = _ctx.Catalog.GoldSellBackFraction();
        if (basedOn == 0)
            fraction = SellBackFraction;
        // One sale removes the item (or the requested part of its stack). Rounded up: Peasant Rags (2 gold) sells for 1.
        var gold = (long)Math.Ceiling(cost * fraction - 1e-9);
        AddGold(gold);
        if (basedOn > 0)
            Trace.World($"Sold {quantity}x {item.Name} for {gold:N0} gold ({fraction:P0} of its {cost:N0} shop price, measured on {basedOn} item(s)).");
        else
            Trace.Synthesize($"sell:{item.ID}", $"Selling '{item.Name}' returned {fraction:P0} of its {cost:N0} gold shop price (the 25 % rule; no sale measured yet).");
    }

    public bool Sell(string? name, int? id, int quantity)
    {
        InventoryItem? item;
        int sold;
        lock (Sync)
        {
            item = Inventory.FirstOrDefault(i => Matches(i, name, id));
            if (item == null || item.Equipped)
                return false;
            sold = quantity > 0 ? Math.Min(quantity, item.Quantity) : item.Quantity;
            RemoveItem(item.Name, item.ID, sold, false, "sold");
            SellReturn(item, sold);
        }
        _ctx.RaiseEvent("ItemSold", item.CharItemID, sold, Math.Max(0, item.Quantity), 0, item.Coins);
        return true;
    }

    // ================================================================ Session

    public void Logout(bool autoRelogin, int reloginDelayMs)
    {
        lock (Sync)
        {
            LoggedIn = false;
            MapLoaded = false;
            Target = null;
            Trace.World("Logged out.");
        }
        _ctx.RaiseEvent("Logout");
        if (!autoRelogin)
            return;
        _ctx.RaiseEvent("ReloginTriggered", false);
        _ctx.Clock.Scheduler.Schedule(Now + Math.Max(1000, reloginDelayMs), "relogin", () => Relogin());
    }

    public bool Relogin()
    {
        lock (Sync)
        {
            LoggedIn = true;
            Relogins++;
            Trace.World("Logged back in.");
        }
        Join("battleon", "Enter", "Spawn", ignoreCheck: true, raiseEvents: true, delay: false);
        return true;
    }

    public PlayerInfo SelfInfo()
    {
        lock (Sync)
        {
            return new PlayerInfo
            {
                Name = Username,
                HP = Health,
                MaxHP = MaxHealth,
                MP = 100,
                Level = Level,
                Cell = Cell,
                Pad = Pad,
                State = State,
                EntID = 1,
                X = PositionX,
                Y = PositionY,
                Stats = new PlayerStats()
            };
        }
    }

    // ================================================================ Snapshots

    public WorldSnapshot Snapshot()
    {
        lock (Sync)
        {
            var questIds = ActiveQuests
                .Union(CompletedQuests)
                .Union(DailiesCompleted)
                .Union(RegisteredQuests.Keys)
                .Union(QuestTurnIns.Keys)
                .OrderBy(id => id)
                .ToList();

            var questDetails = new List<QuestStatusSnapshot>(questIds.Count);
            foreach (var qid in questIds)
            {
                var quest = LoadedQuests.GetValueOrDefault(qid);
                var data = quest == null ? _ctx.Catalog.FindQuest(qid) : null;
                var name = quest?.Name ?? data?.Name ?? $"Quest #{qid}";
                var active = ActiveQuests.Contains(qid);
                var canComplete = active && quest != null &&
                                  quest.Requirements.All(r => Quantity(r.Temp ? TempInventory : Inventory, r.Name, r.ID) >= Math.Max(1, r.Quantity));
                questDetails.Add(new QuestStatusSnapshot(
                    qid,
                    name,
                    active,
                    CompletedQuests.Contains(qid),
                    DailiesCompleted.Contains(qid),
                    canComplete,
                    QuestTurnIns.GetValueOrDefault(qid)));
            }

            var currentClass = Inventory.FirstOrDefault(i => i.Equipped && i.CategoryString == "Class")?.Name;
            var target = Target;

            return new WorldSnapshot
            {
                Username = Username,
                Map = MapName,
                Room = RoomNumber,
                Cell = Cell,
                Pad = Pad,
                MapLoaded = MapLoaded,
                MapSource = Map == null ? "" : DescribeMapSource(Map),
                Level = Level,
                Gold = Gold,
                Coins = Coins,
                Health = Health,
                MaxHealth = MaxHealth,
                Mana = 100,
                MaxMana = 100,
                Alive = Alive,
                InCombat = InCombat,
                LoggedIn = LoggedIn,
                EquippedClass = currentClass,
                ClassRank = ClassRank,
                Kills = Kills,
                Deaths = Deaths,
                DropsPicked = DropsPicked,
                EffectiveDps = CombatProfile?.EffectiveDps ?? 0,
                PlayerCrits = PlayerCrits,
                PlayerDodges = PlayerDodges,
                TotalDamageDealt = TotalDamageDealt,
                TotalHealingDone = TotalHealingDone,
                Strength = CombatProfile?.Stats.Strength ?? 0,
                Intellect = CombatProfile?.Stats.Intellect ?? 0,
                Endurance = CombatProfile?.Stats.Endurance ?? 0,
                Dexterity = CombatProfile?.Stats.Dexterity ?? 0,
                Wisdom = CombatProfile?.Stats.Wisdom ?? 0,
                Luck = CombatProfile?.Stats.Luck ?? 0,
                AttackPower = CombatProfile?.Stats.AttackPower ?? 0,
                SpellPower = CombatProfile?.Stats.SpellPower ?? 0,
                CritChance = CombatProfile?.Stats.CriticalChance ?? 0,
                CritMultiplier = CombatProfile?.Stats.CriticalMultiplier ?? 1.5f,
                Haste = CombatProfile?.Stats.Haste ?? 0,
                EvasionChance = CombatProfile?.Stats.EvasionChance ?? 0,
                MapCells = Map?.Cells.ToList() ?? new List<string>(),
                Target = target != null ? ToMonsterSnapshot(target, isTarget: true) : null,
                CellMonsters = Map?.Monsters.Where(m => Eq(m.Cell, Cell)).Select(m => ToMonsterSnapshot(m, ReferenceEquals(m, target))).ToList() ?? new List<MonsterSnapshot>(),
                MapMonsters = Map?.Monsters.Select(m => ToMonsterSnapshot(m, ReferenceEquals(m, target))).ToList() ?? new List<MonsterSnapshot>(),
                PendingDrops = DropStack.Select(d => new ItemCount(d.ID, d.Name, d.Quantity, false)).ToList(),
                Inventory = Inventory.Select(i => new ItemCount(i.ID, i.Name, i.Quantity, i.Equipped)).ToList(),
                Bank = Bank.Select(i => new ItemCount(i.ID, i.Name, i.Quantity, false)).ToList(),
                TempInventory = TempInventory.Select(i => new ItemCount(i.ID, i.Name, i.Quantity, false)).ToList(),
                House = House.Select(i => new ItemCount(i.ID, i.Name, i.Quantity, i.Equipped)).ToList(),
                Factions = Factions.Select(f => new FactionSnapshot(f.ID, f.Name, f.Rank, f.Rep, f.RequiredRep, f.TotalRep)).ToList(),
                ActiveQuests = ActiveQuests.OrderBy(i => i).ToList(),
                CompletedQuests = CompletedQuests.OrderBy(i => i).ToList(),
                DailiesCompleted = DailiesCompleted.OrderBy(i => i).ToList(),
                QuestDetails = questDetails,
                MapsVisited = MapsVisited.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
    }

    private static MonsterSnapshot ToMonsterSnapshot(SimMonster m, bool isTarget) =>
        new(m.MapID, m.MonID, m.Name, m.Cell, m.HP, m.MaxHP, m.Level, m.Alive, isTarget, m.Synthesized);
}

public sealed record ItemCount(int Id, string Name, int Quantity, bool Equipped);
public sealed record MonsterSnapshot(int MapId, int MonId, string Name, string Cell, int Hp, int MaxHp, int Level, bool Alive, bool IsTarget, bool Synthesized);
public sealed record FactionSnapshot(int Id, string Name, int Rank, int Rep, int RequiredRep, int TotalRep);
public sealed record QuestStatusSnapshot(int Id, string Name, bool Active, bool Completed, bool DailyCompleted, bool CanComplete, int TurnIns);

public sealed class WorldSnapshot
{
    public string Username { get; init; } = "";
    public string Map { get; init; } = "";
    public int Room { get; init; }
    public string Cell { get; init; } = "";
    public string Pad { get; init; } = "";
    public bool MapLoaded { get; init; }
    public string MapSource { get; init; } = "";
    public int Level { get; init; }
    public long Gold { get; init; }
    public int Coins { get; init; }
    public int Health { get; init; }
    public int MaxHealth { get; init; }
    public int Mana { get; init; } = 100;
    public int MaxMana { get; init; } = 100;
    public bool Alive { get; init; }
    public bool InCombat { get; init; }
    public bool LoggedIn { get; init; }
    public string? EquippedClass { get; init; }
    public int ClassRank { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int DropsPicked { get; init; }
    public double EffectiveDps { get; init; }
    public long PlayerCrits { get; init; }
    public long PlayerDodges { get; init; }
    public long TotalDamageDealt { get; init; }
    public long TotalHealingDone { get; init; }
    public int Strength { get; init; }
    public int Intellect { get; init; }
    public int Endurance { get; init; }
    public int Dexterity { get; init; }
    public int Wisdom { get; init; }
    public int Luck { get; init; }
    public int AttackPower { get; init; }
    public int SpellPower { get; init; }
    public double CritChance { get; init; }
    public double CritMultiplier { get; init; } = 1.5;
    public double Haste { get; init; }
    public double EvasionChance { get; init; }
    public List<string> MapCells { get; init; } = new();
    public MonsterSnapshot? Target { get; init; }
    public List<MonsterSnapshot> CellMonsters { get; init; } = new();
    public List<MonsterSnapshot> MapMonsters { get; init; } = new();
    public List<ItemCount> PendingDrops { get; init; } = new();
    public List<ItemCount> Inventory { get; init; } = new();
    public List<ItemCount> Bank { get; init; } = new();
    public List<ItemCount> TempInventory { get; init; } = new();
    public List<ItemCount> House { get; init; } = new();
    public List<FactionSnapshot> Factions { get; init; } = new();
    public List<int> ActiveQuests { get; init; } = new();
    public List<int> CompletedQuests { get; init; } = new();
    public List<int> DailiesCompleted { get; init; } = new();
    public List<QuestStatusSnapshot> QuestDetails { get; init; } = new();
    public List<string> MapsVisited { get; init; } = new();
}
