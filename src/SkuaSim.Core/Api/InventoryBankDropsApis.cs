using Skua.Core.Models.Items;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public sealed class InventoryApi : ApiBase
{
    public InventoryApi(SimContext ctx) : base(ctx) { }

    public List<InventoryItem> Items
    {
        get { lock (World.Sync) return World.Inventory.ToList(); }
    }

    public int Slots => Ctx.Scenario.Player.InventorySlots;
    public int UsedSlots => Items.Count;
    public int FreeSlots => Math.Max(0, Slots - UsedSlots);
    public bool HasCategories => false;
    public int MiscSlots => Slots;
    public int MiscUsedSlots => UsedSlots;
    public int MiscFreeSlots => FreeSlots;
    public IEnumerable<InventoryItem> FavoritedItems => Items.Where(i => World.Favorites.Contains(i.ID));
    public IEnumerable<InventoryItem> UnfavoritedItems => Items.Where(i => !World.Favorites.Contains(i.ID));

    public bool Contains(string name, int quantity)
    {
        World.NoteItemQuery(name, null, quantity, fromTemp: false);
        return World.Quantity(World.Inventory, name, null) >= quantity;
    }

    public bool Contains(int id, int quantity)
    {
        World.NoteItemQuery(null, id, quantity, fromTemp: false);
        return World.Quantity(World.Inventory, null, id) >= quantity;
    }

    public bool TryGetItem(string name, out InventoryItem? item)
    {
        World.NoteItemQuery(name, null, 1, fromTemp: false);
        lock (World.Sync)
            item = World.Inventory.FirstOrDefault(i => SimWorld.Eq(i.Name, name));
        return item != null;
    }

    public bool TryGetItem(int id, out InventoryItem? item)
    {
        World.NoteItemQuery(null, id, 1, fromTemp: false);
        lock (World.Sync)
            item = World.Inventory.FirstOrDefault(i => i.ID == id);
        return item != null;
    }

    public InventoryItem? GetItem(string name) => TryGetItem(name, out var it) ? it : null;
    public InventoryItem? GetItem(int id) => TryGetItem(id, out var it) ? it : null;

    public int GetQuantity(string name)
    {
        World.NoteItemQuery(name, null, 1, fromTemp: false);
        return World.Quantity(World.Inventory, name, null);
    }

    public int GetQuantity(int id)
    {
        World.NoteItemQuery(null, id, 1, fromTemp: false);
        return World.Quantity(World.Inventory, null, id);
    }

    public bool IsMaxStack(string name) => TryGetItem(name, out var it) && it != null && it.Quantity >= it.MaxStack;
    public bool IsMaxStack(int id) => TryGetItem(id, out var it) && it != null && it.Quantity >= it.MaxStack;

    public bool IsEquipped(string name) => TryGetItem(name, out var it) && it?.Equipped == true;
    public bool IsEquipped(int id) => TryGetItem(id, out var it) && it?.Equipped == true;

    public void EquipItem(int id) => World.Equip(id);
    public void EquipItem(string name)
    {
        if (TryGetItem(name, out var it) && it != null)
            World.Equip(it.ID);
    }
    public void EquipUsableItem(InventoryItem item)
    {
        if (item != null)
            World.Equip(item.ID);
    }

    public bool ToBank(InventoryItem item) => item != null && World.ToBank(item.ID);
    public bool ToBank(string name) => TryGetItem(name, out var it) && it != null && World.ToBank(it.ID);
    public bool ToBank(int id) => World.ToBank(id);
    public bool EnsureToBank(string name) => ToBank(name);
    public bool EnsureToBank(int id) => ToBank(id);

    public string GetPool(ItemBase item) => "Misc";
    public bool HasSpaceFor(ItemBase item, int quantity) => FreeSlots > 0 || (item != null && Contains(item.ID, 1));
    public bool CanBank(InventoryItem item) => item != null && !item.Equipped;
    public bool IsFavorited(int itemId) => World.Favorites.Contains(itemId);
}

public sealed class TempInvApi : ApiBase
{
    public TempInvApi(SimContext ctx) : base(ctx) { }

    public List<ItemBase> Items
    {
        get { lock (World.Sync) return World.TempInventory.ToList(); }
    }

    public bool Contains(string name, int quantity)
    {
        World.NoteItemQuery(name, null, quantity, fromTemp: true);
        return World.Quantity(World.TempInventory, name, null) >= quantity;
    }

    public bool Contains(int id, int quantity)
    {
        World.NoteItemQuery(null, id, quantity, fromTemp: true);
        return World.Quantity(World.TempInventory, null, id) >= quantity;
    }

    public bool TryGetItem(string name, out ItemBase? item)
    {
        World.NoteItemQuery(name, null, 1, fromTemp: true);
        lock (World.Sync)
            item = World.TempInventory.FirstOrDefault(i => SimWorld.Eq(i.Name, name));
        return item != null;
    }

    public bool TryGetItem(int id, out ItemBase? item)
    {
        World.NoteItemQuery(null, id, 1, fromTemp: true);
        lock (World.Sync)
            item = World.TempInventory.FirstOrDefault(i => i.ID == id);
        return item != null;
    }

    public ItemBase? GetItem(string name) => TryGetItem(name, out var it) ? it : null;
    public ItemBase? GetItem(int id) => TryGetItem(id, out var it) ? it : null;

    public int GetQuantity(string name)
    {
        World.NoteItemQuery(name, null, 1, fromTemp: true);
        return World.Quantity(World.TempInventory, name, null);
    }

    public int GetQuantity(int id)
    {
        World.NoteItemQuery(null, id, 1, fromTemp: true);
        return World.Quantity(World.TempInventory, null, id);
    }

    public bool IsMaxStack(string name) => TryGetItem(name, out var it) && it != null && it.Quantity >= it.MaxStack;
    public bool IsMaxStack(int id) => TryGetItem(id, out var it) && it != null && it.Quantity >= it.MaxStack;
}

public sealed class BankApi : ApiBase
{
    public BankApi(SimContext ctx) : base(ctx) { }

    public bool Loaded { get => World.BankLoaded; set => World.BankLoaded = value; }

    public List<InventoryItem> Items
    {
        get { lock (World.Sync) return World.Bank.ToList(); }
    }

    public int Slots => Ctx.Scenario.Player.BankSlots;
    public int UsedSlots => Items.Count;
    public int FreeSlots => Math.Max(0, Slots - UsedSlots);

    public void Open() => World.BankLoaded = true;
    public void Load(bool waitForLoad) => World.BankLoaded = true;

    public bool Contains(string name, int quantity) { World.NoteItemProbe(name, null); return World.Quantity(World.Bank, name, null) >= quantity; }
    public bool Contains(int id, int quantity) { World.NoteItemProbe(null, id); return World.Quantity(World.Bank, null, id) >= quantity; }

    public bool TryGetItem(string name, out InventoryItem? item)
    {
        lock (World.Sync)
            item = World.Bank.FirstOrDefault(i => SimWorld.Eq(i.Name, name));
        return item != null;
    }

    public bool TryGetItem(int id, out InventoryItem? item)
    {
        lock (World.Sync)
            item = World.Bank.FirstOrDefault(i => i.ID == id);
        return item != null;
    }

    public InventoryItem? GetItem(string name) => TryGetItem(name, out var it) ? it : null;
    public InventoryItem? GetItem(int id) => TryGetItem(id, out var it) ? it : null;
    public int GetQuantity(string name) => World.Quantity(World.Bank, name, null);
    public int GetQuantity(int id) => World.Quantity(World.Bank, null, id);
    public bool IsMaxStack(string name) => TryGetItem(name, out var it) && it != null && it.Quantity >= it.MaxStack;
    public bool IsMaxStack(int id) => TryGetItem(id, out var it) && it != null && it.Quantity >= it.MaxStack;

    public bool Swap(string invItem, string bankItem)
    {
        var ok1 = World.Inventory.FirstOrDefault(i => SimWorld.Eq(i.Name, invItem));
        var ok2 = World.Bank.FirstOrDefault(i => SimWorld.Eq(i.Name, bankItem));
        if (ok1 == null || ok2 == null)
            return false;
        return World.ToBank(ok1.ID) && World.ToInventory(ok2.ID);
    }

    public bool Swap(int invItem, int bankItem) => World.ToBank(invItem) && World.ToInventory(bankItem);
    public bool Swap(InventoryItem invItem, InventoryItem bankItem) => invItem != null && bankItem != null && Swap(invItem.ID, bankItem.ID);
    public bool EnsureSwap(string invItem, string bankItem, bool loadBank) => Swap(invItem, bankItem);
    public bool EnsureSwap(int invItem, int bankItem, bool loadBank) => Swap(invItem, bankItem);

    public bool ToInventory(InventoryItem item) => item != null && World.ToInventory(item.ID);
    public bool ToInventory(string name) => TryGetItem(name, out var it) && it != null && World.ToInventory(it.ID);
    public bool ToInventory(int id) => World.ToInventory(id);
    public bool EnsureToInventory(string name, bool loadBank) => ToInventory(name);
    public bool EnsureToInventory(int id, bool loadBank) => ToInventory(id);
}

public sealed class HouseInvApi : ApiBase
{
    public HouseInvApi(SimContext ctx) : base(ctx) { }

    public List<InventoryItem> Items
    {
        get { lock (World.Sync) return World.House.ToList(); }
    }

    public int Slots => Ctx.Scenario.Player.HouseSlots;
    public int UsedSlots => Items.Count;
    public int FreeSlots => Math.Max(0, Slots - UsedSlots);

    public bool Contains(string name, int quantity) { World.NoteItemProbe(name, null); return World.Quantity(World.House, name, null) >= quantity; }
    public bool Contains(int id, int quantity) { World.NoteItemProbe(null, id); return World.Quantity(World.House, null, id) >= quantity; }

    public bool TryGetItem(string name, out InventoryItem? item)
    {
        lock (World.Sync)
            item = World.House.FirstOrDefault(i => SimWorld.Eq(i.Name, name));
        return item != null;
    }

    public bool TryGetItem(int id, out InventoryItem? item)
    {
        lock (World.Sync)
            item = World.House.FirstOrDefault(i => i.ID == id);
        return item != null;
    }

    public InventoryItem? GetItem(string name) => TryGetItem(name, out var it) ? it : null;
    public InventoryItem? GetItem(int id) => TryGetItem(id, out var it) ? it : null;
    public int GetQuantity(string name) => World.Quantity(World.House, name, null);
    public int GetQuantity(int id) => World.Quantity(World.House, null, id);
    public bool IsEquipped(string name) => TryGetItem(name, out var it) && it?.Equipped == true;
    public bool IsEquipped(int id) => TryGetItem(id, out var it) && it?.Equipped == true;
    public bool ToBank(InventoryItem item) => false;
    public bool EnsureToBank(string name) => false;
    public bool EnsureToBank(int id) => false;
}

public sealed class InvHelperApi : ApiBase
{
    public InvHelperApi(SimContext ctx) : base(ctx) { }

    public bool Check(string name, int quantity, bool moveToInventory)
    {
        World.NoteItemQuery(name, null, quantity, fromTemp: false);
        if (World.Quantity(World.TempInventory, name) >= quantity)
            return true;
        if (World.Quantity(World.Inventory, name) >= quantity)
            return true;
        if (World.Quantity(World.House, name) >= quantity)
            return true;
        var inBank = World.Bank.FirstOrDefault(i => SimWorld.Eq(i.Name, name));
        if (inBank != null && inBank.Quantity >= quantity)
        {
            if (moveToInventory)
                World.ToInventory(inBank.ID);
            return true;
        }
        return false;
    }

    public bool Check(int id, int quantity, bool moveToInventory)
    {
        World.NoteItemQuery(null, id, quantity, fromTemp: false);
        if (World.Quantity(World.TempInventory, null, id) >= quantity)
            return true;
        if (World.Quantity(World.Inventory, null, id) >= quantity)
            return true;
        if (World.Quantity(World.House, null, id) >= quantity)
            return true;
        var inBank = World.Bank.FirstOrDefault(i => i.ID == id);
        if (inBank != null && inBank.Quantity >= quantity)
        {
            if (moveToInventory)
                World.ToInventory(inBank.ID);
            return true;
        }
        return false;
    }

    public bool HasAll(IEnumerable<string> itemNames, int quantity, bool moveToInventory) =>
        itemNames.All(n => Check(n, quantity, moveToInventory));

    public bool HasAny(IEnumerable<string> itemNames, int quantity, bool moveToInventory) =>
        itemNames.Any(n => Check(n, quantity, moveToInventory));

    public bool HasAll(IEnumerable<int> itemIds, int quantity, bool moveToInventory) =>
        itemIds.All(id => Check(id, quantity, moveToInventory));

    public bool HasAny(IEnumerable<int> itemIds, int quantity, bool moveToInventory) =>
        itemIds.Any(id => Check(id, quantity, moveToInventory));
}

public sealed class DropsApi : ApiBase
{
    public DropsApi(SimContext ctx) : base(ctx) { }

    public IEnumerable<ItemBase> CurrentDropInfos
    {
        get { lock (World.Sync) return World.DropStack.ToList(); }
    }

    public IEnumerable<string> CurrentDrops
    {
        get { lock (World.Sync) return World.DropStack.Select(d => d.Name).ToList(); }
    }

    public bool Enabled => World.DropsEnabled;
    public int Interval { get => World.DropInterval; set => World.DropInterval = value; }
    public bool RejectElse { get => World.RejectElse; set => World.RejectElse = value; }
    public IEnumerable<string> ToPickup => World.PickupNames.ToList();
    public IEnumerable<int> ToPickupIDs => World.PickupIds.ToList();

    public void Add(string[] names)
    {
        if (names == null) return;
        lock (World.Sync)
        {
            foreach (var n in names.Where(s => !string.IsNullOrWhiteSpace(s)))
                World.PickupNames.Add(n.Trim());
        }
    }

    public void Add(int[] ids)
    {
        if (ids == null) return;
        lock (World.Sync)
        {
            foreach (var id in ids.Where(i => i > 0))
                World.PickupIds.Add(id);
        }
    }

    public void Remove(string[] names)
    {
        if (names == null) return;
        lock (World.Sync)
        {
            foreach (var n in names)
                World.PickupNames.Remove(n);
        }
    }

    public void Remove(int[] ids)
    {
        if (ids == null) return;
        lock (World.Sync)
        {
            foreach (var id in ids)
                World.PickupIds.Remove(id);
        }
    }

    public void Start() => World.StartDrops();
    public void Stop() => World.StopDrops();
    public ValueTask StopAsync() { World.StopDrops(); return ValueTask.CompletedTask; }

    public void Clear()
    {
        lock (World.Sync)
        {
            World.PickupNames.Clear();
            World.PickupIds.Clear();
        }
    }

    public bool Exists(string name) => World.DropStack.Any(d => SimWorld.Eq(d.Name, name));
    public bool Exists(int id) => World.DropStack.Any(d => d.ID == id);

    public void Pickup(string name) => World.Pickup(name, null);
    public void Pickup(int id) => World.Pickup(null, id);
    public void Pickup(string[] names)
    {
        if (names == null) return;
        foreach (var n in names)
            World.Pickup(n, null);
    }
    public void Pickup(int[] ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
            World.Pickup(null, id);
    }

    public void PickupAll(bool skipWait)
    {
        foreach (var d in CurrentDropInfos.ToList())
            World.Pickup(d.Name, d.ID);
    }

    public void RejectAll(bool skipWait)
    {
        lock (World.Sync)
            World.DropStack.Clear();
    }

    public void RejectExcept(string[] names)
    {
        var keep = new HashSet<string>(names ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var d in CurrentDropInfos.Where(d => !keep.Contains(d.Name)).ToList())
            World.Reject(d.Name, d.ID);
    }

    public void RejectExcept(int[] ids)
    {
        var keep = new HashSet<int>(ids ?? Array.Empty<int>());
        foreach (var d in CurrentDropInfos.Where(d => !keep.Contains(d.ID)).ToList())
            World.Reject(d.Name, d.ID);
    }
}
