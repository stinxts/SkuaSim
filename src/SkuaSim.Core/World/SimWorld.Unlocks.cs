using SkuaSim.Core.Data;

namespace SkuaSim.Core.World;

/// <summary>An unlock item (quest pet, weapon, class…) the script asked about but the character does not own.</summary>
public sealed record MissingUnlockItem(int Id, string Name, string Category, int Checks, long FirstVirtualMs, string Opens, int QuestCount);

public sealed partial class SimWorld
{
    private readonly Dictionary<int, (UnlockItem Item, int Checks, long FirstMs)> _missingUnlocks = new();

    /// <summary>
    /// Called when the script checks for an item (inventory/bank/house Contains, TryGetItem, GetQuantity, or a quest
    /// accept that fails on it). If it is an item that opens quests and the character owns none, the script is most
    /// likely skipping a path (e.g. CoreLegion's Legion Token farm skips every Paragon Pet it does not find), so it is
    /// listed in the report's "Paths that depend on items the character does not own".
    /// </summary>
    internal void NoteItemProbe(string? name, int? id)
    {
        var unlock = _ctx.Catalog.Unlocks.Find(name, id);
        if (unlock == null)
            return;
        lock (Sync)
        {
            if (Quantity(Inventory, unlock.Name, unlock.Id) + Quantity(Bank, unlock.Name, unlock.Id) + Quantity(House, unlock.Name, unlock.Id) > 0)
                return;
            _missingUnlocks[unlock.Id] = _missingUnlocks.TryGetValue(unlock.Id, out var seen)
                ? (seen.Item, seen.Checks + 1, seen.FirstMs)
                : (unlock, 1, Now);
        }
    }

    /// <summary>
    /// Bot.Quests.IsAvailable as Skua implements it (auqw/Skua ScriptQuest.IsAvailable): not done today, unlocked,
    /// member if the quest needs it, level, required class points and faction rep, and every accept requirement in the
    /// inventory (by name). It does not look at turn-in requirements, and a one-time quest already done still counts
    /// as available there (that is HasBeenCompleted's job).
    /// </summary>
    public bool IsQuestAvailable(Skua.Core.Models.Quests.Quest q)
    {
        lock (Sync)
        {
            if (DailiesCompleted.Contains(q.ID) || !IsUnlocked(q) || (q.Upgrade && !IsMember) || Level < q.Level)
                return false;
            if (q.RequiredClassID > 0 && ClassPointsFor(q.RequiredClassID) < q.RequiredClassPoints)
                return false;
            if (q.RequiredFactionId > 1 && (Factions.FirstOrDefault(f => f.ID == q.RequiredFactionId)?.TotalRep ?? 0) < q.RequiredFactionRep)
                return false;
            var available = true;
            foreach (var r in q.AcceptRequirements ?? new List<Skua.Core.Models.Items.ItemBase>())
            {
                if (r == null || Quantity(Inventory, r.Name, null) >= Math.Max(1, r.Quantity))
                    continue;
                NoteItemProbe(r.Name, r.ID);
                available = false;
            }
            return available;
        }
    }

    /// <summary>Class points of a class by item ID: the equipped class uses the run's CP (at least its rank's threshold).</summary>
    private int ClassPointsFor(int classItemId)
    {
        if (CurrentClass is { } cls && cls.ID == classItemId)
            return Math.Max(ClassPoints, Math.Max(cls.Quantity, RepForRank(Math.Clamp(ClassRank, 1, 10))));
        return Inventory.Concat(Bank).FirstOrDefault(i => i.ID == classItemId)?.Quantity ?? 0;
    }

    /// <summary>Unlock items the script checked and the character does not own, most checked first.</summary>
    public List<MissingUnlockItem> MissingUnlockItems()
    {
        lock (Sync)
            return _missingUnlocks.Values
                .OrderByDescending(v => v.Item.IsPet)
                .ThenByDescending(v => v.Checks).ThenBy(v => v.FirstMs).ThenBy(v => v.Item.Id)
                .Select(v => new MissingUnlockItem(v.Item.Id, v.Item.Name, v.Item.Category, v.Checks, v.FirstMs, v.Item.Describe(), v.Item.Quests.Count))
                .ToList();
    }
}
