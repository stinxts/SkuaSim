using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SkuaSim.Core.World;
using SkuaSim.UI.Models;

namespace SkuaSim.UI.ViewModels;

public partial class ClientStateViewModel : ViewModelBase
{
    private List<ItemRowItem> _allInventory = new();

    [ObservableProperty]
    public partial string Username { get; set; } = "-";

    [ObservableProperty]
    public partial string LevelText { get; set; } = "Lv. -";

    [ObservableProperty]
    public partial int Health { get; set; }

    [ObservableProperty]
    public partial int MaxHealth { get; set; } = 100;

    [ObservableProperty]
    public partial string HpText { get; set; } = "0 / 0";

    [ObservableProperty]
    public partial string EquippedClass { get; set; } = "-";

    [ObservableProperty]
    public partial string ClassRankText { get; set; } = "Rank -";

    [ObservableProperty]
    public partial string GoldText { get; set; } = "0";

    [ObservableProperty]
    public partial string CoinsText { get; set; } = "0 AC";

    [ObservableProperty]
    public partial string PlayerStatusBadge { get; set; } = "Idle";

    [ObservableProperty]
    public partial bool IsAlive { get; set; } = true;

    [ObservableProperty]
    public partial bool InCombat { get; set; }

    [ObservableProperty]
    public partial string MapBanner { get; set; } = "(no map loaded)";

    [ObservableProperty]
    public partial string CellPadText { get; set; } = "Cell: -  |  Pad: -";

    [ObservableProperty]
    public partial string MapSource { get; set; } = "-";

    [ObservableProperty]
    public partial string TargetText { get; set; } = "No target";

    [ObservableProperty]
    public partial int Kills { get; set; }

    [ObservableProperty]
    public partial int Deaths { get; set; }

    [ObservableProperty]
    public partial int DropsPicked { get; set; }

    [ObservableProperty]
    public partial string EffectiveDpsText { get; set; } = "0 DPS";

    [ObservableProperty]
    public partial string PowerStatsText { get; set; } = "AP 0  |  SP 0";

    [ObservableProperty]
    public partial string SecondaryStatsText { get; set; } = "Crit 0% (x1.50)  |  Haste 0%  |  Dodge 0%";

    [ObservableProperty]
    public partial string PrimaryStatsText { get; set; } = "STR 0  INT 0  END 0  DEX 0  WIS 0  LUK 0";

    [ObservableProperty]
    public partial string CombatCountersText { get; set; } = "Crits: 0  |  Dodges: 0  |  Healed: 0 HP";

    [ObservableProperty]
    public partial string InventoryFilter { get; set; } = "";

    [ObservableProperty]
    public partial string InventoryHeader { get; set; } = "Inventory (0)";

    [ObservableProperty]
    public partial string BankHeader { get; set; } = "Bank (0)";

    [ObservableProperty]
    public partial string TempHeader { get; set; } = "Temp Inv (0)";

    [ObservableProperty]
    public partial string HouseHeader { get; set; } = "House (0)";

    [ObservableProperty]
    public partial string QuestsHeader { get; set; } = "Quests (0)";

    [ObservableProperty]
    public partial string FactionsHeader { get; set; } = "Factions (0)";

    [ObservableProperty]
    public partial string MapsHeader { get; set; } = "Maps (0)";

    [ObservableProperty]
    public partial string CellMonstersHeader { get; set; } = "Monsters in Cell (0)";

    [ObservableProperty]
    public partial string PendingDropsHeader { get; set; } = "Pending Drops (0)";

    public ObservableCollection<MonsterRowItem> CellMonsters { get; } = new();
    public ObservableCollection<ItemRowItem> PendingDrops { get; } = new();
    public ObservableCollection<ItemRowItem> FilteredInventory { get; } = new();
    public ObservableCollection<ItemRowItem> BankItems { get; } = new();
    public ObservableCollection<ItemRowItem> TempInventoryItems { get; } = new();
    public ObservableCollection<ItemRowItem> HouseItems { get; } = new();
    public ObservableCollection<QuestRowItem> Quests { get; } = new();
    public ObservableCollection<FactionRowItem> Factions { get; } = new();
    public ObservableCollection<string> MapsVisited { get; } = new();

    partial void OnInventoryFilterChanged(string value) => ApplyInventoryFilter();

    public void Reset()
    {
        Username = "-";
        LevelText = "Lv. -";
        Health = 0;
        MaxHealth = 100;
        HpText = "0 / 0";
        EquippedClass = "-";
        ClassRankText = "Rank -";
        GoldText = "0";
        CoinsText = "0 AC";
        PlayerStatusBadge = "Starting";
        IsAlive = true;
        InCombat = false;
        MapBanner = "(no map loaded)";
        CellPadText = "Cell: -  |  Pad: -";
        MapSource = "-";
        TargetText = "No target";
        Kills = 0;
        Deaths = 0;
        DropsPicked = 0;
        EffectiveDpsText = "0 DPS";
        PowerStatsText = "AP 0  |  SP 0";
        SecondaryStatsText = "Crit 0% (x1.50)  |  Haste 0%  |  Dodge 0%";
        PrimaryStatsText = "STR 0  INT 0  END 0  DEX 0  WIS 0  LUK 0";
        CombatCountersText = "Crits: 0  |  Dodges: 0  |  Healed: 0 HP";

        _allInventory = new List<ItemRowItem>();
        CellMonsters.Clear();
        PendingDrops.Clear();
        FilteredInventory.Clear();
        BankItems.Clear();
        TempInventoryItems.Clear();
        HouseItems.Clear();
        Quests.Clear();
        Factions.Clear();
        MapsVisited.Clear();
        UpdateHeaders();
    }

    public void ApplySnapshot(WorldSnapshot world)
    {
        Username = string.IsNullOrWhiteSpace(world.Username) ? "-" : world.Username;
        LevelText = world.Level > 0 ? $"Lv. {world.Level}" : "Lv. -";
        MaxHealth = Math.Max(1, world.MaxHealth);
        Health = Math.Clamp(world.Health, 0, MaxHealth);
        HpText = $"{world.Health:N0} / {world.MaxHealth:N0}";
        EquippedClass = string.IsNullOrWhiteSpace(world.EquippedClass) ? "(none)" : world.EquippedClass;
        ClassRankText = world.ClassRank > 0 ? $"Rank {world.ClassRank}" : "Rank -";
        GoldText = $"{world.Gold:N0} Gold";
        CoinsText = $"{world.Coins:N0} AC";
        IsAlive = world.Alive;
        InCombat = world.InCombat;
        PlayerStatusBadge = !world.LoggedIn
            ? "Offline"
            : !world.Alive
                ? "Dead (Respawning)"
                : world.InCombat
                    ? "In Combat"
                    : "Idle";

        MapBanner = string.IsNullOrWhiteSpace(world.Map)
            ? "(no map loaded)"
            : $"{world.Map}-{world.Room}";
        CellPadText = $"Cell: {(string.IsNullOrWhiteSpace(world.Cell) ? "-" : world.Cell)}   |   Pad: {(string.IsNullOrWhiteSpace(world.Pad) ? "-" : world.Pad)}";
        MapSource = string.IsNullOrWhiteSpace(world.MapSource) ? "-" : world.MapSource;
        TargetText = world.Target != null
            ? $"{world.Target.Name} ({world.Target.Hp:N0}/{world.Target.MaxHp:N0} HP)"
            : "No target";

        Kills = world.Kills;
        Deaths = world.Deaths;
        DropsPicked = world.DropsPicked;

        EffectiveDpsText = $"{world.EffectiveDps:N0} DPS";
        PowerStatsText = $"AP {world.AttackPower:N0}  |  SP {world.SpellPower:N0}";
        SecondaryStatsText = $"Crit {world.CritChance * 100:F1}% (x{world.CritMultiplier:F2})  |  Haste {world.Haste * 100:F1}%  |  Dodge {world.EvasionChance * 100:F1}%";
        PrimaryStatsText = $"STR {world.Strength}  INT {world.Intellect}  END {world.Endurance}  DEX {world.Dexterity}  WIS {world.Wisdom}  LUK {world.Luck}";
        CombatCountersText = $"Crits: {world.PlayerCrits:N0}  |  Dodges: {world.PlayerDodges:N0}  |  Healed: {world.TotalHealingDone:N0} HP";

        var monsters = world.CellMonsters
            .Select(m => new MonsterRowItem(m.MapId, m.Name, m.Level, m.Hp, m.MaxHp, m.Alive, m.IsTarget, m.Synthesized))
            .ToList();
        SyncCollection(CellMonsters, monsters);

        var drops = world.PendingDrops
            .Select(d => new ItemRowItem(d.Id, d.Name, d.Quantity, false, "Drop"))
            .ToList();
        SyncCollection(PendingDrops, drops);

        _allInventory = world.Inventory
            .OrderByDescending(i => i.Equipped)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new ItemRowItem(i.Id, i.Name, i.Quantity, i.Equipped, "Inv"))
            .ToList();
        ApplyInventoryFilter();

        SyncCollection(BankItems, world.Bank
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new ItemRowItem(i.Id, i.Name, i.Quantity, false, "Bank"))
            .ToList());

        SyncCollection(TempInventoryItems, world.TempInventory
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new ItemRowItem(i.Id, i.Name, i.Quantity, false, "Temp"))
            .ToList());

        SyncCollection(HouseItems, world.House
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new ItemRowItem(i.Id, i.Name, i.Quantity, i.Equipped, "House"))
            .ToList());

        var quests = world.QuestDetails
            .OrderByDescending(q => q.Active)
            .ThenByDescending(q => q.CanComplete)
            .ThenBy(q => q.Id)
            .Select(q => new QuestRowItem(q.Id, q.Name, q.Active, q.Completed, q.DailyCompleted, q.CanComplete, q.TurnIns))
            .ToList();
        SyncCollection(Quests, quests);

        var factions = world.Factions
            .OrderByDescending(f => f.Rank)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FactionRowItem(f.Id, f.Name, f.Rank, f.Rep, f.RequiredRep, f.TotalRep))
            .ToList();
        SyncCollection(Factions, factions);

        SyncCollection(MapsVisited, world.MapsVisited);
        UpdateHeaders();
    }

    private void ApplyInventoryFilter()
    {
        var filter = (InventoryFilter ?? "").Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _allInventory
            : _allInventory.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                       i.Id.ToString().Contains(filter, StringComparison.Ordinal)).ToList();
        SyncCollection(FilteredInventory, filtered);
        InventoryHeader = $"Inventory ({_allInventory.Count})";
    }

    private void UpdateHeaders()
    {
        InventoryHeader = $"Inventory ({_allInventory.Count})";
        BankHeader = $"Bank ({BankItems.Count})";
        TempHeader = $"Temp Inv ({TempInventoryItems.Count})";
        HouseHeader = $"House ({HouseItems.Count})";
        QuestsHeader = $"Quests ({Quests.Count})";
        FactionsHeader = $"Factions ({Factions.Count})";
        MapsHeader = $"Maps ({MapsVisited.Count})";
        CellMonstersHeader = $"Monsters in Cell ({CellMonsters.Count})";
        PendingDropsHeader = $"Pending Drops ({PendingDrops.Count})";
    }

    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> next)
    {
        if (target.Count == next.Count && target.SequenceEqual(next))
            return;

        target.Clear();
        foreach (var item in next)
            target.Add(item);
    }
}
