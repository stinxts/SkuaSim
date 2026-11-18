using Skua.Core.Models;
using Skua.Core.Models.Factions;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public sealed class QuestsApi : ApiBase
{
    private Dictionary<int, QuestData>? _cachedDict;
    private List<QuestData>? _cachedList;

    public QuestsApi(SimContext ctx) : base(ctx) { }

    public int RegisterCompleteInterval { get => World.RegisterCompleteInterval; set => World.RegisterCompleteInterval = value; }

    public List<Quest> Active
    {
        get
        {
            lock (World.Sync)
                return World.ActiveQuests.Select(id => World.GetOrLoadQuest(id)!).ToList();
        }
    }

    public List<Quest> Completed
    {
        get
        {
            lock (World.Sync)
                return World.CompletedQuests.Select(id => World.GetOrLoadQuest(id)!).ToList();
        }
    }

    public List<Quest> Tree
    {
        get
        {
            lock (World.Sync)
                return World.LoadedQuests.Values.ToList();
        }
    }

    public Dictionary<int, QuestData> CachedDictionary
    {
        get => _cachedDict ??= Ctx.Catalog.Quests.ToDictionary(k => k.Key, k => k.Value);
        set => _cachedDict = value;
    }

    public List<QuestData> Cached
    {
        get => _cachedList ??= CachedDictionary.Values.ToList();
        set => _cachedList = value;
    }

    public IEnumerable<int> Registered => World.RegisteredQuests.Keys.ToList();
    public IReadOnlyDictionary<int, int> RegisteredRewards => World.RegisteredQuests;

    public void RegisterQuests(int[] ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
        {
            World.GetOrLoadQuest(id);
            World.AcceptQuest(id, log: false);
        }
        World.RegisterQuests(ids.Select(id => (id, -1)));
    }

    public void RegisterQuests((int, int)[] quests)
    {
        if (quests == null) return;
        foreach (var (id, _) in quests)
        {
            World.GetOrLoadQuest(id);
            World.AcceptQuest(id, log: false);
        }
        World.RegisterQuests(quests);
    }

    public void UnregisterQuests(int[] ids)
    {
        if (ids == null) return;
        lock (World.Sync)
        {
            foreach (var id in ids)
                World.RegisteredQuests.Remove(id);
        }
    }

    public void UnregisterAllQuests()
    {
        lock (World.Sync)
            World.RegisteredQuests.Clear();
    }

    public bool Accept(int id) => World.AcceptQuest(id);
    public void Accept(int[] ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
            World.AcceptQuest(id);
    }

    public bool EnsureAccept(int id) => World.AcceptQuest(id);
    public void EnsureAccept(int[] ids) => Accept(ids);

    public bool CanComplete(int id) => World.CanComplete(id);
    public bool CanCompleteFullCheck(int id) => World.CanComplete(id);

    public bool Complete(int id, int itemId, bool special) => World.CompleteQuest(id, itemId);
    public void Complete(int[] ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
            World.CompleteQuest(id, -1);
    }

    public bool EnsureComplete(int id, int itemId, bool special) => World.CompleteQuest(id, itemId);
    public void EnsureComplete(int[] ids) => Complete(ids);

    public bool IsAvailable(int id)
    {
        var q = World.GetOrLoadQuest(id);
        return q != null && World.IsUnlocked(q) && (!q.Once || !World.HasBeenCompleted(q));
    }

    public bool IsDailyComplete(int id) => World.DailiesCompleted.Contains(id);
    public bool IsDailyComplete(Quest quest) => quest != null && IsDailyComplete(quest.ID);
    public bool IsInProgress(int id) => World.ActiveQuests.Contains(id);
    public bool IsUnlocked(int id) => World.IsUnlocked(World.GetOrLoadQuest(id)!);
    public bool IsUnlocked(Quest quest) => quest != null && World.IsUnlocked(quest);
    public bool HasBeenCompleted(int id) => World.HasBeenCompleted(World.GetOrLoadQuest(id)!);
    public bool HasBeenCompleted(Quest quest) => quest != null && World.HasBeenCompleted(quest);

    public void Load(int[] ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
            World.GetOrLoadQuest(id);
    }

    public Quest? EnsureLoad(int id) => World.GetOrLoadQuest(id);

    public bool TryGetQuest(int id, out Quest? quest)
    {
        quest = World.GetOrLoadQuest(id);
        return quest != null;
    }

    public bool UpdateQuest(int id)
    {
        var q = World.GetOrLoadQuest(id);
        if (q != null && q.Slot >= 0)
        {
            lock (World.Sync)
                World.SlotProgress[q.Slot] = Math.Max(World.SlotProgress.GetValueOrDefault(q.Slot), q.Value);
            return true;
        }
        return false;
    }

    public void UpdateQuest(int value, int slot)
    {
        if (slot >= 0)
        {
            lock (World.Sync)
                World.SlotProgress[slot] = Math.Max(World.SlotProgress.GetValueOrDefault(slot), value);
        }
    }

    public void LoadCachedQuests() { _ = Cached; }

    public List<QuestData> GetCachedQuests(int start, int count) =>
        Cached.Where(q => q.ID >= start && q.ID < start + count).ToList();

    public List<QuestData> GetCachedQuests(int[] ids)
    {
        var set = new HashSet<int>(ids ?? Array.Empty<int>());
        return Cached.Where(q => set.Contains(q.ID)).ToList();
    }
}

public sealed class ShopsApi : ApiBase
{
    public ShopsApi(SimContext ctx) : base(ctx) { }

    public bool IsLoaded => World.CurrentShop != null;
    public int ID => World.CurrentShop?.ID ?? 0;
    public List<ShopItem> Items => World.CurrentShop?.Items ?? new List<ShopItem>();
    public List<ShopInfo> LoadedCache { get => World.ShopCache; set { } }
    public string Name => World.CurrentShop?.Name ?? "";
    public bool ProtectFavorites { get; set; } = true;

    public List<string> GetUnmetPurchaseRequirements(ShopItem item, int quantity)
    {
        var unmet = new List<string>();
        if (item == null)
            return unmet;
        var qty = Math.Max(1, quantity);
        foreach (var req in item.Requirements ?? new List<ItemBase>())
        {
            var have = World.Quantity(req.Temp ? World.TempInventory : World.Inventory, req.Name, req.ID);
            var need = Math.Max(1, req.Quantity) * qty;
            if (have < need)
                unmet.Add($"{req.Name} ({have}/{need})");
        }
        return unmet;
    }

    public void BuyItem(string name, int quantity) => World.Buy(name, null, -1, quantity);
    public void BuyItem(int id, int shopItemId, int quantity) => World.Buy(null, id, shopItemId, quantity);

    public void LoadAndBuyItem(int shopId, string name, int quantity)
    {
        World.LoadShop(shopId);
        World.Buy(name, null, -1, quantity);
    }

    public void LoadAndBuyItem(int shopId, int itemId, int shopItemId, int quantity)
    {
        World.LoadShop(shopId);
        World.Buy(null, itemId, shopItemId, quantity);
    }

    public void EnsureLoadAndBuyItem(int shopId, string itemName, int quantity) => LoadAndBuyItem(shopId, itemName, quantity);
    public void EnsureLoadAndBuyItem(int shopId, int itemId, int shopItemId, int quantity) => LoadAndBuyItem(shopId, itemId, shopItemId, quantity);

    public void Load(int id) => World.LoadShop(id);
    public void LoadArmourCustomizer() { }
    public void LoadHairShop(int id) { }

    public void SellItem(string name, int quantity) => World.Sell(name, null, quantity);
    public void SellItem(int id, int quantity) => World.Sell(null, id, quantity);
}

public sealed class ReputationApi : ApiBase
{
    public ReputationApi(SimContext ctx) : base(ctx) { }

    public List<Faction> FactionList
    {
        get { lock (World.Sync) return World.Factions.ToList(); }
    }

    public int GetRank(string name)
    {
        lock (World.Sync)
        {
            var f = World.Factions.FirstOrDefault(x => SimWorld.Eq(x.Name, name));
            return f?.Rank ?? 10;
        }
    }

    public int GetRank(int id)
    {
        lock (World.Sync)
        {
            var f = World.Factions.FirstOrDefault(x => x.ID == id);
            return f?.Rank ?? 10;
        }
    }

    public bool HasRank(string name, int rank) => GetRank(name) >= rank;
    public bool HasRank(int id, int rank) => GetRank(id) >= rank;
}

public sealed class WaitApi : ApiBase
{
    public WaitApi(SimContext ctx) : base(ctx) { }

    public int WAIT_SLEEP { get; set; } = 250;
    public bool OverrideTimeout { get; set; }
    public int PlayerActionTimeout { get; set; } = 10;
    public int DropActionTimeout { get; set; } = 10;
    public int GameActionTimeout { get; set; } = 10;
    public int ItemActionTimeout { get; set; } = 10;
    public int MapActionTimeout { get; set; } = 20;
    public int MonsterActionTimeout { get; set; } = 10;
    public int QuestActionTimeout { get; set; } = 14;

    public bool For(Func<object> function, object value, int timeout) =>
        ForTrue(() => Equals(function(), value), timeout);

    public ValueTask<bool> ForAsync(Func<object> function, object value, int timeout, CancellationToken token) =>
        new(For(function, value, timeout));

    public bool ForActionCooldown(GameActions action, int timeout) => true;
    public bool ForActionCooldown(string action, int timeout) => true;
    public bool ForBankLoad(int timeout) { World.BankLoaded = true; return true; }
    public bool ForBankToInventory(string name, int timeout) => World.Quantity(World.Inventory, name) > 0;
    public bool ForBankToInventory(int id, int timeout) => World.Quantity(World.Inventory, null, id) > 0;
    public bool ForBankToHouseInventory(string name, int timeout) => true;
    public bool ForBankToHouseInventory(int id, int timeout) => true;
    public bool ForCellChange(string name) => ForTrue(() => SimWorld.Eq(World.Cell, name), 5);
    public bool ForCombatExit(int timeout) => ForTrue(() => !World.InCombat, timeout);
    public bool ForDrop(string name, int timeout) =>
        ForTrue(() => World.DropStack.Any(d => SimWorld.Eq(d.Name, name)) || World.Quantity(World.Inventory, name) > 0 || World.Quantity(World.TempInventory, name) > 0, timeout);
    public bool ForDrop(int id, int timeout) =>
        ForTrue(() => World.DropStack.Any(d => d.ID == id) || World.Quantity(World.Inventory, null, id) > 0 || World.Quantity(World.TempInventory, null, id) > 0, timeout);
    public bool ForFullyRested(int timeout) { World.Rest(); return true; }
    public bool ForInventoryToBank(string name, int timeout) => World.Quantity(World.Bank, name) > 0;
    public bool ForInventoryToBank(int id, int timeout) => World.Quantity(World.Bank, null, id) > 0;
    public bool ForHouseInventoryToBank(string name, int timeout) => true;
    public bool ForHouseInventoryToBank(int id, int timeout) => true;
    public bool ForItemBuy(int timeout) => true;
    public bool ForItemEquip(string name, int timeout) => World.Inventory.Any(i => SimWorld.Eq(i.Name, name) && i.Equipped);
    public bool ForItemEquip(int id, int timeout) => World.Inventory.Any(i => i.ID == id && i.Equipped);
    public bool ForItemSell(int timeout) => true;
    public bool ForMapLoad(string name, int timeout)
    {
        var (norm, _) = SimWorld.ParseMapRequest(name);
        return ForTrue(() => World.MapLoaded && SimWorld.Eq(World.MapName, norm), timeout);
    }
    public bool ForMonsterDeath(int timeout) => ForTrue(() => World.Target is not { Alive: true }, timeout < 0 ? 30 : timeout);
    public bool ForMonsterSpawn(string name, int timeout) => ForTrue(() => World.FindMonster(name) != null, timeout);
    public bool ForMonsterSpawn(int id, int timeout) => ForTrue(() => World.MonstersInCell().Any(m => m.Alive && (m.MapID == id || m.MonID == id)), timeout);
    public bool ForPickup(string name, int timeout)
    {
        World.Pickup(name, null);
        return World.Quantity(World.Inventory, name) > 0 || World.Quantity(World.TempInventory, name) > 0;
    }
    public bool ForPickup(int id, int timeout)
    {
        World.Pickup(null, id);
        return World.Quantity(World.Inventory, null, id) > 0 || World.Quantity(World.TempInventory, null, id) > 0;
    }
    public bool ForPlayerPosition(int x, int y, int timeout) => true;
    public bool ForQuestAccept(int id, int timeout) => World.ActiveQuests.Contains(id);
    public bool ForQuestComplete(int id, int timeout) => !World.ActiveQuests.Contains(id);
    public bool ForQuestLoad(int id, int timeout) => World.GetOrLoadQuest(id) != null;
    public bool ForQuestLoad(int startId, int endId, int timeout) => true;
    public bool ForSkillCooldown(int index, int timeout) => true;

    public bool ForTrue(Func<bool> predicate, int timeout, int sleepOverride = -1) =>
        ForTrue(predicate, null, timeout, sleepOverride);

    public bool ForTrue(Func<bool> predicate, Action? loopFunction, int timeout, int sleepOverride = -1)
    {
        var step = sleepOverride > 0 ? sleepOverride : Math.Max(50, WAIT_SLEEP);
        var maxMs = (timeout <= 0 ? 15 : timeout) * 1000L;
        var deadline = Ctx.Clock.NowMs + maxMs;

        while (!Ctx.StopRequested)
        {
            Ctx.ThrowIfStopping();
            if (predicate())
                return true;
            loopFunction?.Invoke();
            if (predicate())
                return true;
            if (Ctx.Clock.NowMs >= deadline)
                return false;
            Sleep(step);
        }
        return false;
    }

    public bool ForTrue(Func<bool> predicate, Action? loopFunction, int sleepOverride, CancellationToken token) =>
        ForTrue(() => token.IsCancellationRequested || predicate(), loopFunction, 20, sleepOverride);

    public ValueTask<bool> ForTrueAsync(Func<bool> predicate, int timeout, int sleepOverride, CancellationToken token) =>
        new(ForTrue(predicate, null, timeout, sleepOverride));

    public ValueTask<bool> ForTrueAsync(Func<bool> predicate, Action loopFunction, int timeout, int sleepOverride, CancellationToken token) =>
        new(ForTrue(predicate, loopFunction, timeout, sleepOverride));

    public bool IsActionAvailable(GameActions action) => true;
    public bool IsActionAvailable(string action) => true;
}
