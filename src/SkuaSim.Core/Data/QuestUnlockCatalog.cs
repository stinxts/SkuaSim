using Skua.Core.Models.Items;

namespace SkuaSim.Core.Data;

/// <summary>A quest that can only be accepted while the character owns a given item (quest "AcceptRequirements").</summary>
public sealed record UnlockedQuest(int Id, string Name, bool MemberOnly, IReadOnlyList<string> Rewards);

/// <summary>
/// An item whose ownership opens quests: quest pets (e.g. "Shogun Paragon Pet" → 5755 Time for Some Spring Cleaning →
/// Legion Token), weapons ("Infernal Caladbolg"), classes, armors, house items… Scripts branch on these with
/// <c>Core.CheckInventory(name)</c>, so owning one can change a script's whole path.
/// </summary>
public sealed class UnlockItem
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public bool IsPet => string.Equals(Category, "Pet", StringComparison.OrdinalIgnoreCase);
    public List<UnlockedQuest> Quests { get; } = new();

    /// <summary>Distinct rewards of the quests this item opens (for reports and the editor).</summary>
    public IEnumerable<string> RewardNames => Quests.SelectMany(q => q.Rewards).Distinct(StringComparer.OrdinalIgnoreCase);

    public string Describe(int maxQuests = 3)
    {
        var quests = string.Join("; ", Quests.Take(maxQuests).Select(q =>
            $"{q.Id} {q.Name}{(q.Rewards.Count > 0 ? " → " + string.Join(", ", q.Rewards.Take(3)) : "")}"));
        return Quests.Count > maxQuests ? $"{quests}; +{Quests.Count - maxQuests} more" : quests;
    }
}

/// <summary>
/// Every item that is an accept requirement of a quest (QuestData.json plus collected quests), built once per catalog.
/// 946 of 10,345 QuestData quests have one (2026-11-22): 317 need a pet (188 different pets).
/// </summary>
public sealed class QuestUnlockCatalog
{
    private readonly Dictionary<int, UnlockItem> _byId = new();
    private readonly Dictionary<string, UnlockItem> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<UnlockItem> Items => _byId.Values;

    public UnlockItem? Find(int id) => id > 0 ? _byId.GetValueOrDefault(id) : null;

    public UnlockItem? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byName.GetValueOrDefault(System.Net.WebUtility.HtmlDecode(name.Trim()));

    public UnlockItem? Find(string? name, int? id) => Find(id ?? 0) ?? Find(name);

    internal static QuestUnlockCatalog Build(GameDataCatalog catalog)
    {
        var result = new QuestUnlockCatalog();
        foreach (var q in catalog.Quests.Values)
        {
            var rewards = (q.Rewards ?? new List<ItemBase>()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => System.Net.WebUtility.HtmlDecode(r.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var req in q.AcceptRequirements ?? new List<ItemBase>())
                // Stackable requirements (currencies such as Legion Token) are resources, not unlocks.
                if (req != null && !req.Temp && req.ID > 0 && req.MaxStack <= 1)
                    result.Add(req.ID, req.Name, req.CategoryString, new UnlockedQuest(q.ID, q.Name ?? "", q.Upgrade, rewards));
        }
        foreach (var cq in catalog.Captured.Quests.Values)
        {
            if (catalog.Quests.ContainsKey(cq.ID))
                continue;
            var rewards = cq.Rewards.Select(r => r.ItemName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var req in cq.AcceptRequirements)
                if (!req.Temp && req.ItemID > 0)
                    result.Add(req.ItemID, req.ItemName, "Item", new UnlockedQuest(cq.ID, cq.Name, cq.Upgrade, rewards));
        }
        return result;
    }

    private void Add(int id, string? name, string? category, UnlockedQuest quest)
    {
        if (!_byId.TryGetValue(id, out var item))
        {
            item = new UnlockItem
            {
                Id = id,
                Name = System.Net.WebUtility.HtmlDecode(name ?? $"Item #{id}").Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "Item" : category
            };
            _byId[id] = item;
            _byName.TryAdd(item.Name, item);
        }
        if (item.Quests.All(q => q.Id != quest.Id))
            item.Quests.Add(quest);
    }
}
