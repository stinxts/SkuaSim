using Newtonsoft.Json;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using SkuaSim.Collector.Models;

namespace SkuaSim.Core.Data;

public enum DataSource { Captured, QuestData, Scenario, Synthesized }

public sealed class ItemInfo
{
    public int ID { get; init; }
    public string Name { get; init; } = "";
    public int MaxStack { get; set; } = 1;
    public bool Temp { get; set; }
    public string Category { get; set; } = "Item";
    public bool Coins { get; set; }
    public bool Upgrade { get; set; }
    public string ItemGroup { get; set; } = "None";
    public string Meta { get; set; } = "";
    public int EnhancementPatternID { get; set; }
    public int ProcID { get; set; }
    public DataSource Source { get; set; }
}

/// <summary>
/// Read-only game knowledge: the collector database (gamedata.json) plus Skua's QuestData.json.
/// </summary>
public sealed class GameDataCatalog
{
    private readonly Dictionary<int, ItemInfo> _itemsById = new();
    private readonly Dictionary<string, ItemInfo> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CapturedMonster>> _monstersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CapturedMonster> _monstersById = new();
    private readonly Dictionary<int, List<CapturedMonster>> _dropSources = new();

    public GameDatabase Captured { get; }
    public string? CapturedPath { get; }
    public IReadOnlyDictionary<int, QuestData> Quests { get; }
    public string? QuestDataPath { get; }

    private GameDataCatalog(GameDatabase captured, string? capturedPath, Dictionary<int, QuestData> quests, string? questDataPath)
    {
        Captured = captured;
        CapturedPath = capturedPath;
        Quests = quests;
        QuestDataPath = questDataPath;
        BuildIndexes();
    }

    public static GameDataCatalog Load(string? gameDataPath, string? questDataPath)
    {
        var captured = new GameDatabase();
        string? usedCaptured = null;
        if (!string.IsNullOrEmpty(gameDataPath) && File.Exists(gameDataPath))
        {
            captured = JsonConvert.DeserializeObject<GameDatabase>(File.ReadAllText(gameDataPath)) ?? new GameDatabase();
            usedCaptured = Path.GetFullPath(gameDataPath);
        }

        var quests = new Dictionary<int, QuestData>();
        string? usedQuests = null;
        if (!string.IsNullOrEmpty(questDataPath) && File.Exists(questDataPath))
        {
            var list = JsonConvert.DeserializeObject<List<QuestData>>(File.ReadAllText(questDataPath)) ?? new List<QuestData>();
            foreach (var q in list)
                quests[q.ID] = q;
            usedQuests = Path.GetFullPath(questDataPath);
        }

        return new GameDataCatalog(captured, usedCaptured, quests, usedQuests);
    }

    private void BuildIndexes()
    {
        foreach (var item in Captured.Items.Values)
        {
            AddItem(new ItemInfo
            {
                ID = item.ID,
                Name = item.Name,
                MaxStack = Math.Max(1, item.MaxStack),
                Temp = item.Temp,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "Item" : item.Category,
                Coins = item.Coins,
                Upgrade = item.Upgrade,
                ItemGroup = string.IsNullOrWhiteSpace(item.ItemGroup) ? "None" : item.ItemGroup,
                Meta = item.Meta ?? "",
                EnhancementPatternID = item.EnhancementPatternID,
                Source = DataSource.Captured
            });
        }

        foreach (var q in Quests.Values)
        {
            foreach (var list in new[] { q.Requirements, q.AcceptRequirements, q.Rewards })
            {
                if (list == null)
                    continue;
                foreach (var it in list)
                    AddItemBase(it, DataSource.QuestData);
            }
        }

        foreach (var shop in Captured.Shops.Values)
        {
            foreach (var si in shop.Items)
            {
                AddItem(new ItemInfo
                {
                    ID = si.ID,
                    Name = si.Name,
                    MaxStack = Math.Max(1, si.MaxStack),
                    Category = string.IsNullOrWhiteSpace(si.Category) ? "Item" : si.Category,
                    Coins = si.Coins,
                    Upgrade = si.Upgrade,
                    Meta = si.Meta ?? "",
                    EnhancementPatternID = si.EnhancementPatternID,
                    ProcID = si.ProcID,
                    Source = DataSource.Captured
                });
            }
        }

        var respawnAverages = new List<double>();
        foreach (var mon in Captured.Monsters.Values)
        {
            if (mon.Respawn is { Samples: > 0 } respawn)
                respawnAverages.Add(respawn.AverageMs);
            var c = mon.Combat;
            if (mon.WeaponDps > 0 && mon.Level > 0 && c != null && c.Hits + c.Crits >= 10 && c.AverageDamage > 0)
                _damageCalibration.Add((mon.Level, c.AverageDamage / mon.WeaponDps, c.AverageAttackIntervalMs));

            if (!string.IsNullOrWhiteSpace(mon.Name))
            {
                if (!_monstersByName.TryGetValue(mon.Name.Trim(), out var list))
                    _monstersByName[mon.Name.Trim()] = list = new List<CapturedMonster>();
                list.Add(mon);
            }
            if (mon.ID > 0)
                _monstersById[mon.ID] = mon;
            foreach (var drop in mon.Drops.Values)
            {
                if (!_dropSources.TryGetValue(drop.ItemID, out var sources))
                    _dropSources[drop.ItemID] = sources = new List<CapturedMonster>();
                sources.Add(mon);
            }
        }
        GlobalRespawnMedianMs = Median(respawnAverages);
    }

    private readonly List<(int Level, double HitPerWeaponDps, double IntervalMs)> _damageCalibration = new();

    /// <summary>Median of the per-monster average respawn delays measured by the collector (0 when none).</summary>
    public double GlobalRespawnMedianMs { get; private set; }

    /// <summary>Number of monsters with measured combat damage and wDPS (the damage calibration set).</summary>
    public int DamageCalibrationCount => _damageCalibration.Count;

    /// <summary>
    /// Estimates average hit damage and attack interval for a monster with no combat data from its wDPS, using
    /// measured monsters of a similar level (observed hit / wDPS ratio is level-dependent: ~2-3 at low levels,
    /// ~0.6-0.9 at 65+). Null when there is no calibration data. Results are estimates (report them as synthesized).
    /// </summary>
    public (double AverageHit, double IntervalMs, int BasedOn)? EstimateMonsterDamage(int level, int weaponDps)
    {
        if (weaponDps <= 0 || level <= 0 || _damageCalibration.Count == 0)
            return null;
        var near = _damageCalibration.Where(p => Math.Abs(p.Level - level) <= 10).ToList();
        if (near.Count < 3)
            near = _damageCalibration.OrderBy(p => Math.Abs(p.Level - level)).ThenBy(p => p.Level).Take(5).ToList();
        var ratio = Median(near.Select(p => p.HitPerWeaponDps).ToList());
        var interval = Median(near.Where(p => p.IntervalMs > 0).Select(p => p.IntervalMs).ToList());
        return (Math.Max(1, ratio * weaponDps), interval > 0 ? interval : 2000, near.Count);
    }

    /// <summary>
    /// Median of (measured sell amount / gold shop price) over sold gold items found in captured shops; (0, 0) when
    /// nothing comparable was measured.
    /// </summary>
    public (double Fraction, int BasedOn) GoldSellBackFraction()
    {
        if (_sellFraction is { } cached)
            return cached;
        var costs = new Dictionary<int, int>();
        foreach (var si in Captured.Shops.Values.SelectMany(s => s.Items))
            if (!si.Coins && si.Cost > 0 && !costs.ContainsKey(si.ID))
                costs[si.ID] = si.Cost;
        var ratios = (Captured.SellPrices ?? new())
            .Values.Where(p => !p.Coins && p.Samples > 0 && costs.ContainsKey(p.ItemID))
            .OrderBy(p => p.ItemID)
            .Select(p => (double)p.Amount / costs[p.ItemID])
            .ToList();
        _sellFraction = ratios.Count == 0 ? (0, 0) : (Median(ratios), ratios.Count);
        return _sellFraction.Value;
    }

    private (double, int)? _sellFraction;

    /// <summary>Faction name by ID from the collected faction catalog (null when unknown).</summary>
    public string? FactionName(int factionId) =>
        factionId > 0 && Captured.Factions != null && Captured.Factions.TryGetValue(factionId, out var f) && !string.IsNullOrWhiteSpace(f.Name)
            ? f.Name
            : null;

    /// <summary>Quest captured by the collector (rewards observed on turn-in, requirements from getQuests).</summary>
    public CapturedQuest? FindCapturedQuest(int id) => Captured.Quests.GetValueOrDefault(id);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    private void AddItemBase(ItemBase it, DataSource source)
    {
        if (it == null || it.ID <= 0 || string.IsNullOrWhiteSpace(it.Name))
            return;
        AddItem(new ItemInfo
        {
            ID = it.ID,
            Name = it.Name.Trim(),
            MaxStack = Math.Max(1, it.MaxStack),
            Temp = it.Temp,
            Category = string.IsNullOrWhiteSpace(it.CategoryString) ? "Item" : it.CategoryString,
            Coins = it.Coins,
            Upgrade = it.Upgrade,
            Source = source
        });
    }

    private void AddItem(ItemInfo info)
    {
        if (info.ID <= 0 || string.IsNullOrWhiteSpace(info.Name))
            return;
        if (_itemsById.TryGetValue(info.ID, out var existing))
        {
            // Captured data wins over QuestData; keep the larger stack size.
            existing.MaxStack = Math.Max(existing.MaxStack, info.MaxStack);
            return;
        }
        _itemsById[info.ID] = info;
        _itemsByName.TryAdd(info.Name.Trim(), info);
    }

    public ItemInfo? FindItem(int id) => _itemsById.GetValueOrDefault(id);

    public ItemInfo? FindItem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _itemsByName.GetValueOrDefault(name.Trim());

    public QuestData? FindQuest(int id) => Quests.GetValueOrDefault(id);

    public CapturedMap? FindMap(string normalizedName) =>
        Captured.Maps.TryGetValue(normalizedName, out var map) ? map : null;

    public CapturedMonster? FindMonster(int monsterId, string? name)
    {
        if (monsterId > 0 && _monstersById.TryGetValue(monsterId, out var byId))
            return byId;
        if (!string.IsNullOrWhiteSpace(name) && _monstersByName.TryGetValue(name.Trim(), out var list))
            return list.OrderByDescending(m => m.KillCount).First();
        return null;
    }

    /// <summary>Captured monsters known to drop the item.</summary>
    public IReadOnlyList<CapturedMonster> DropSources(int itemId) =>
        _dropSources.TryGetValue(itemId, out var list) ? list : Array.Empty<CapturedMonster>();

    public int ItemCount => _itemsById.Count;
    public int QuestCount => Quests.Count;

    public CapturedClassProfile? FindClassProfile(string? className) =>
        !string.IsNullOrWhiteSpace(className) && Captured.Classes != null &&
        Captured.Classes.TryGetValue(className.Trim(), out var cls)
            ? cls
            : null;

    public CapturedEnhancementProfile? FindEnhancementProfile(int patternId) =>
        patternId > 0 && Captured.Enhancements != null &&
        Captured.Enhancements.TryGetValue(patternId, out var enh)
            ? enh
            : null;
}
