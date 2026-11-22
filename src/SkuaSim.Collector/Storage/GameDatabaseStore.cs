using System.Text;
using Newtonsoft.Json;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Storage;

/// <summary>
/// Thread-safe and multi-process-safe store that merges incremental deltas from
/// multiple running Skua/VibeSkua clients into a single unified gamedata.json.
/// </summary>
public sealed partial class GameDatabaseStore
{
    private const string MutexName = @"Global\SkuaSim_GameDatabase_Mutex_v1";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc
    };

    public string PrimaryFilePath { get; }
    public IReadOnlyList<string> MirrorFilePaths { get; }

    public GameDatabaseStore(string primaryFilePath, IEnumerable<string>? mirrorFilePaths = null)
    {
        PrimaryFilePath = primaryFilePath;
        MirrorFilePaths = (mirrorFilePaths ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p) && !string.Equals(p, primaryFilePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Supplies offline quest knowledge (Skua's QuestData.json) for the v1 → v2 migration step. Optional.</summary>
    public Func<QuestCatalogHints>? QuestHintsProvider { get; set; }

    /// <summary>Path of the dated pre-migration backup written by the last migration (null when none happened).</summary>
    public string? LastMigrationBackupPath { get; private set; }

    public GameDatabase LoadCurrent()
    {
        return ExecuteWithMutex(() =>
        {
            var db = LoadFromDiskInternal(PrimaryFilePath);
            if (db.SchemaVersion < GameDatabase.CurrentSchemaVersion)
                GameDatabaseMigrations.MigrateToCurrent(db, SafeHints()); // in memory only; the next flush persists it
            return db;
        });
    }

    /// <summary>
    /// Atomically reads the current database on disk, merges the in-memory delta into it,
    /// saves it back atomically, and updates any configured mirror files.
    /// An older file is first backed up (dated copy next to it) and migrated to the current schema.
    /// </summary>
    public GameDatabase FlushDelta(GameDatabase delta)
    {
        return ExecuteWithMutex(() =>
        {
            var current = LoadFromDiskInternal(PrimaryFilePath);
            if (current.SchemaVersion < GameDatabase.CurrentSchemaVersion)
            {
                if (File.Exists(PrimaryFilePath))
                {
                    var backup = Path.Combine(
                        Path.GetDirectoryName(PrimaryFilePath) ?? ".",
                        $"gamedata.v{current.SchemaVersion}-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                    File.Copy(PrimaryFilePath, backup, overwrite: false);
                    LastMigrationBackupPath = backup;
                }
                GameDatabaseMigrations.MigrateToCurrent(current, SafeHints());
            }
            MergeInto(current, delta);
            current.TotalFlushesMerged++;
            current.LastUpdatedUtc = DateTime.UtcNow;

            SaveToDiskAtomic(PrimaryFilePath, current);

            foreach (var mirrorPath in MirrorFilePaths)
            {
                try
                {
                    SaveToDiskAtomic(mirrorPath, current);
                }
                catch
                {
                    // Ignore mirror write errors so primary collection is never disrupted
                }
            }

            return current;
        });
    }

    private QuestCatalogHints? SafeHints()
    {
        try { return QuestHintsProvider?.Invoke(); }
        catch { return null; }
    }

    public static void MergeInto(GameDatabase target, GameDatabase source)
    {
        // 1. Merge Maps
        foreach (var (mapKey, srcMap) in source.Maps)
        {
            var normKey = GameDatabase.NormalizeMapName(string.IsNullOrWhiteSpace(srcMap.Name) ? mapKey : srcMap.Name);
            if (string.IsNullOrEmpty(normKey))
                continue;

            if (!target.Maps.TryGetValue(normKey, out var dstMap))
            {
                dstMap = new CapturedMap { Name = normKey };
                target.Maps[normKey] = dstMap;
            }

            if (!string.IsNullOrWhiteSpace(srcMap.FileName))
                dstMap.FileName = srcMap.FileName;

            dstMap.Visits += srcMap.Visits;
            if (srcMap.LastSeenUtc > dstMap.LastSeenUtc)
                dstMap.LastSeenUtc = srcMap.LastSeenUtc;

            foreach (var (cellName, srcPads) in srcMap.Cells)
            {
                if (string.IsNullOrWhiteSpace(cellName))
                    continue;
                if (!dstMap.Cells.TryGetValue(cellName, out var dstPads))
                {
                    dstPads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dstMap.Cells[cellName] = dstPads;
                }
                foreach (var pad in srcPads)
                {
                    if (!string.IsNullOrWhiteSpace(pad))
                        dstPads.Add(pad);
                }
            }

            foreach (var (monMapId, srcSpawn) in srcMap.Monsters)
            {
                if (!dstMap.Monsters.TryGetValue(monMapId, out var dstSpawn))
                {
                    dstSpawn = new CapturedMapMonsterSpawn { MapID = monMapId };
                    dstMap.Monsters[monMapId] = dstSpawn;
                }

                if (srcSpawn.MonsterID > 0) dstSpawn.MonsterID = srcSpawn.MonsterID;
                if (!string.IsNullOrWhiteSpace(srcSpawn.Name)) dstSpawn.Name = srcSpawn.Name;
                if (!string.IsNullOrWhiteSpace(srcSpawn.Cell)) dstSpawn.Cell = srcSpawn.Cell;
                if (srcSpawn.Level > 0) dstSpawn.Level = srcSpawn.Level;
                if (srcSpawn.MaxHP > 0) dstSpawn.MaxHP = srcSpawn.MaxHP;
                if (!string.IsNullOrWhiteSpace(srcSpawn.Race)) dstSpawn.Race = srcSpawn.Race;
                if (!string.IsNullOrWhiteSpace(srcSpawn.Element)) dstSpawn.Element = srcSpawn.Element;
                if (!string.IsNullOrWhiteSpace(srcSpawn.FileName)) dstSpawn.FileName = srcSpawn.FileName;
                if (srcSpawn.WeaponDps > 0) dstSpawn.WeaponDps = srcSpawn.WeaponDps;
                (dstSpawn.Respawn ??= new()).Merge(srcSpawn.Respawn);
            }

            foreach (var (mapItemId, srcMapItem) in srcMap.MapItems)
            {
                if (!dstMap.MapItems.TryGetValue(mapItemId, out var dstMapItem))
                {
                    dstMapItem = new CapturedMapItem { MapItemID = mapItemId };
                    dstMap.MapItems[mapItemId] = dstMapItem;
                }

                dstMapItem.TimesRequested += srcMapItem.TimesRequested;
                if (srcMapItem.LastSeenUtc > dstMapItem.LastSeenUtc)
                    dstMapItem.LastSeenUtc = srcMapItem.LastSeenUtc;

                foreach (var cell in srcMapItem.CellsSeen)
                    if (!string.IsNullOrWhiteSpace(cell))
                        dstMapItem.CellsSeen.Add(cell);

                foreach (var itemId in srcMapItem.GrantedItemIDs)
                    if (itemId > 0)
                        dstMapItem.GrantedItemIDs.Add(itemId);

                dstMapItem.RequiredQuestIds.UnionWith(srcMapItem.RequiredQuestIds);
                dstMapItem.GrantObservations += srcMapItem.GrantObservations;
                foreach (var (questId, count) in srcMapItem.ActiveQuestCounts)
                    dstMapItem.ActiveQuestCounts[questId] = dstMapItem.ActiveQuestCounts.GetValueOrDefault(questId) + count;
            }

            dstMap.CellNumbers ??= new();
            foreach (var (number, votes) in srcMap.CellNumbers ?? new())
            {
                if (!dstMap.CellNumbers.TryGetValue(number, out var dstVotes))
                    dstMap.CellNumbers[number] = dstVotes = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var (cell, count) in votes)
                    dstVotes[cell] = dstVotes.GetValueOrDefault(cell) + count;
            }

            dstMap.PlayerCountSamples += srcMap.PlayerCountSamples;
            if (srcMap.PeakPlayersObserved > dstMap.PeakPlayersObserved)
            {
                dstMap.PeakPlayersObserved = srcMap.PeakPlayersObserved;
                dstMap.PeakPlayersObservedUtc = srcMap.PeakPlayersObservedUtc;
            }

            if (srcMap.ZoneEvents is { Count: > 0 })
            {
                dstMap.ZoneEvents ??= new Dictionary<string, CapturedZoneEvent>(StringComparer.OrdinalIgnoreCase);
                MergeZoneEvents(dstMap.ZoneEvents, srcMap.ZoneEvents);
            }
        }

        // 2. Merge Monsters
        foreach (var (monKey, srcMon) in source.Monsters)
        {
            var key = string.IsNullOrWhiteSpace(srcMon.Key)
                ? GameDatabase.GetMonsterKey(srcMon.ID, srcMon.Name)
                : srcMon.Key;

            // If we previously only knew this monster by name ("name:foo") and now have its ID ("id:123"),
            // or vice versa, check if a matching monster by ID or Name already exists.
            if (!target.Monsters.TryGetValue(key, out var dstMon))
            {
                if (srcMon.ID > 0 && !string.IsNullOrWhiteSpace(srcMon.Name))
                {
                    var nameFallbackKey = GameDatabase.GetMonsterKey(0, srcMon.Name);
                    if (target.Monsters.TryGetValue(nameFallbackKey, out var existingByName))
                    {
                        target.Monsters.Remove(nameFallbackKey);
                        existingByName.Key = key;
                        existingByName.ID = srcMon.ID;
                        target.Monsters[key] = existingByName;
                        dstMon = existingByName;
                    }
                }
                else if (srcMon.ID == 0 && !string.IsNullOrWhiteSpace(srcMon.Name))
                {
                    dstMon = target.Monsters.Values.FirstOrDefault(m =>
                        string.Equals(m.Name, srcMon.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (dstMon == null)
                {
                    dstMon = new CapturedMonster { Key = key };
                    target.Monsters[key] = dstMon;
                }
            }

            if (srcMon.ID > 0) dstMon.ID = srcMon.ID;
            if (!string.IsNullOrWhiteSpace(srcMon.Name)) dstMon.Name = srcMon.Name;
            if (srcMon.Level > 0) dstMon.Level = srcMon.Level;
            if (srcMon.WeaponDps > 0) dstMon.WeaponDps = srcMon.WeaponDps;
            if (srcMon.MaxHP > 0) dstMon.MaxHP = Math.Max(dstMon.MaxHP, srcMon.MaxHP);
            if (!string.IsNullOrWhiteSpace(srcMon.Race) && srcMon.Race != "None") dstMon.Race = srcMon.Race;
            else if (string.IsNullOrWhiteSpace(dstMon.Race) && !string.IsNullOrWhiteSpace(srcMon.Race)) dstMon.Race = srcMon.Race;
            if (!string.IsNullOrWhiteSpace(srcMon.Element) && srcMon.Element != "None") dstMon.Element = srcMon.Element;
            if (!string.IsNullOrWhiteSpace(srcMon.FileName)) dstMon.FileName = srcMon.FileName;

            dstMon.KillCount += srcMon.KillCount;
            dstMon.TrackedKills += srcMon.TrackedKills;
            foreach (var (questId, kills) in srcMon.KillsWhileQuestActive)
                dstMon.KillsWhileQuestActive[questId] = dstMon.KillsWhileQuestActive.GetValueOrDefault(questId) + kills;
            dstMon.KillsByActiveQuestSet ??= new(StringComparer.Ordinal);
            foreach (var (setKey, kills) in srcMon.KillsByActiveQuestSet ?? new())
                dstMon.KillsByActiveQuestSet[setKey] = dstMon.KillsByActiveQuestSet.GetValueOrDefault(setKey) + kills;
            (dstMon.KillRewards ??= new()).Merge(srcMon.KillRewards);
            (dstMon.Respawn ??= new()).Merge(srcMon.Respawn);
            if (srcMon.OfficialDropsObservedUtc > (dstMon.OfficialDropsObservedUtc ?? DateTime.MinValue))
                dstMon.OfficialDropsObservedUtc = srcMon.OfficialDropsObservedUtc;
            if (srcMon.LastSeenUtc > dstMon.LastSeenUtc)
                dstMon.LastSeenUtc = srcMon.LastSeenUtc;

            foreach (var (mapName, srcCells) in srcMon.MapsSeen)
            {
                if (!dstMon.MapsSeen.TryGetValue(mapName, out var dstCells))
                {
                    dstCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dstMon.MapsSeen[mapName] = dstCells;
                }
                foreach (var c in srcCells)
                    if (!string.IsNullOrWhiteSpace(c))
                        dstCells.Add(c);
            }

            // Merge Combat stats
            var sc = srcMon.Combat;
            var dc = dstMon.Combat;
            dc.AttacksObserved += sc.AttacksObserved;
            dc.Hits += sc.Hits;
            dc.Crits += sc.Crits;
            dc.Dodges += sc.Dodges;
            dc.Misses += sc.Misses;
            dc.TotalDamage += sc.TotalDamage;
            if (sc.MinDamage.HasValue)
                dc.MinDamage = dc.MinDamage.HasValue ? Math.Min(dc.MinDamage.Value, sc.MinDamage.Value) : sc.MinDamage.Value;
            if (sc.MaxDamage.HasValue)
                dc.MaxDamage = dc.MaxDamage.HasValue ? Math.Max(dc.MaxDamage.Value, sc.MaxDamage.Value) : sc.MaxDamage.Value;
            dc.AttackIntervalSamples += sc.AttackIntervalSamples;
            dc.TotalAttackIntervalMs += sc.TotalAttackIntervalMs;
            dc.CounterAttacksObserved += sc.CounterAttacksObserved;
            dc.PlayerDeathsInCell += sc.PlayerDeathsInCell;
            dc.MaxTargetsHit = Math.Max(dc.MaxTargetsHit, sc.MaxTargetsHit);

            // Merge Auras
            foreach (var (auraName, srcAura) in srcMon.Auras)
            {
                if (!dstMon.Auras.TryGetValue(auraName, out var dstAura))
                {
                    dstAura = new CapturedAuraStat { Name = srcAura.Name };
                    dstMon.Auras[auraName] = dstAura;
                }
                if (!string.IsNullOrWhiteSpace(srcAura.CasterType)) dstAura.CasterType = srcAura.CasterType;
                if (!string.IsNullOrWhiteSpace(srcAura.TargetType)) dstAura.TargetType = srcAura.TargetType;
                if (!string.IsNullOrWhiteSpace(srcAura.Category)) dstAura.Category = srcAura.Category;
                if (srcAura.DurationSeconds > 0) dstAura.DurationSeconds = srcAura.DurationSeconds;
                dstAura.TimesSeen += srcAura.TimesSeen;
                dstAura.IsCounterAttack |= srcAura.IsCounterAttack;
                dstAura.IsTaunt |= srcAura.IsTaunt;
                dstAura.IsStacking |= srcAura.IsStacking;
                dstAura.MaxStacksObserved = Math.Max(dstAura.MaxStacksObserved, srcAura.MaxStacksObserved);
                dstAura.IntervalSamples += srcAura.IntervalSamples;
                dstAura.TotalIntervalMs += srcAura.TotalIntervalMs;
                if (srcAura.MinBossHpPercent.HasValue)
                    dstAura.MinBossHpPercent = dstAura.MinBossHpPercent.HasValue ? Math.Min(dstAura.MinBossHpPercent.Value, srcAura.MinBossHpPercent.Value) : srcAura.MinBossHpPercent.Value;
                if (srcAura.MaxBossHpPercent.HasValue)
                    dstAura.MaxBossHpPercent = dstAura.MaxBossHpPercent.HasValue ? Math.Max(dstAura.MaxBossHpPercent.Value, srcAura.MaxBossHpPercent.Value) : srcAura.MaxBossHpPercent.Value;
                if (srcAura.MinValue.HasValue)
                    dstAura.MinValue = dstAura.MinValue.HasValue ? Math.Min(dstAura.MinValue.Value, srcAura.MinValue.Value) : srcAura.MinValue.Value;
                if (srcAura.MaxValue.HasValue)
                    dstAura.MaxValue = dstAura.MaxValue.HasValue ? Math.Max(dstAura.MaxValue.Value, srcAura.MaxValue.Value) : srcAura.MaxValue.Value;
            }

            // Merge SpecialMessages, SkillEvents & ZoneEvents
            foreach (var (msg, count) in srcMon.SpecialMessages)
            {
                dstMon.SpecialMessages[msg] = dstMon.SpecialMessages.GetValueOrDefault(msg) + count;
            }

            if (srcMon.SkillEvents is { Count: > 0 })
            {
                dstMon.SkillEvents ??= new Dictionary<string, CapturedMonsterSkillEvent>(StringComparer.OrdinalIgnoreCase);
                MergeMonsterSkillEvents(dstMon.SkillEvents, srcMon.SkillEvents);
            }

            if (srcMon.ZoneEvents is { Count: > 0 })
            {
                dstMon.ZoneEvents ??= new Dictionary<string, CapturedZoneEvent>(StringComparer.OrdinalIgnoreCase);
                MergeZoneEvents(dstMon.ZoneEvents, srcMon.ZoneEvents);
            }

            // Merge Drops
            foreach (var (itemId, srcDrop) in srcMon.Drops)
            {
                if (!dstMon.Drops.TryGetValue(itemId, out var dstDrop))
                {
                    dstDrop = new CapturedDropStat
                    {
                        ItemID = itemId,
                        ItemName = srcDrop.ItemName,
                        Temp = srcDrop.Temp
                    };
                    dstMon.Drops[itemId] = dstDrop;
                }
                if (!string.IsNullOrWhiteSpace(srcDrop.ItemName))
                    dstDrop.ItemName = srcDrop.ItemName;
                dstDrop.Temp |= srcDrop.Temp;
                dstDrop.DropEvents += srcDrop.DropEvents;
                dstDrop.TotalQuantity += srcDrop.TotalQuantity;
                dstDrop.MeasuredQuantity += srcDrop.MeasuredQuantity;
                dstDrop.QuantitySamples += srcDrop.QuantitySamples;
                dstDrop.TrackedDropEvents += srcDrop.TrackedDropEvents;
                dstDrop.IsOfficialDrop |= srcDrop.IsOfficialDrop;
                dstDrop.QuestGated |= srcDrop.QuestGated;
                dstDrop.RequiredQuestIds.UnionWith(srcDrop.RequiredQuestIds);
                dstDrop.QuestGatedNames.UnionWith(srcDrop.QuestGatedNames);
                dstDrop.QuestObjectives.UnionWith(srcDrop.QuestObjectives);
            }

            // Denominators/bases depend on the merged kill counters (all kills or quest-active kills).
            DropRates.Recompute(dstMon);
        }

        // 3. Merge Items
        foreach (var (itemId, srcItem) in source.Items)
        {
            if (!target.Items.TryGetValue(itemId, out var dstItem))
            {
                dstItem = new CapturedItem { ID = itemId };
                target.Items[itemId] = dstItem;
            }
            if (!string.IsNullOrWhiteSpace(srcItem.Name)) dstItem.Name = srcItem.Name;
            if (!string.IsNullOrWhiteSpace(srcItem.Description)) dstItem.Description = srcItem.Description;
            if (!string.IsNullOrWhiteSpace(srcItem.Category)) dstItem.Category = srcItem.Category;
            if (srcItem.MaxStack > 0) dstItem.MaxStack = Math.Max(dstItem.MaxStack, srcItem.MaxStack);
            dstItem.Temp = srcItem.Temp;
            dstItem.Coins = srcItem.Coins;
            dstItem.Upgrade = srcItem.Upgrade;
            dstItem.House = srcItem.House;
            if (!string.IsNullOrWhiteSpace(srcItem.ItemGroup)) dstItem.ItemGroup = srcItem.ItemGroup;
            if (!string.IsNullOrWhiteSpace(srcItem.FileName)) dstItem.FileName = srcItem.FileName;
            if (!string.IsNullOrWhiteSpace(srcItem.FileLink)) dstItem.FileLink = srcItem.FileLink;
            if (!string.IsNullOrWhiteSpace(srcItem.Meta)) dstItem.Meta = srcItem.Meta;
            if (srcItem.EnhancementPatternID > 0) dstItem.EnhancementPatternID = srcItem.EnhancementPatternID;
            dstItem.RequiredByQuestIds.UnionWith(srcItem.RequiredByQuestIds);
            if (srcItem.LastSeenUtc > dstItem.LastSeenUtc) dstItem.LastSeenUtc = srcItem.LastSeenUtc;
        }

        // 4. Merge Shops
        foreach (var (shopId, srcShop) in source.Shops)
        {
            if (!target.Shops.TryGetValue(shopId, out var dstShop))
            {
                dstShop = new CapturedShop { ID = shopId };
                target.Shops[shopId] = dstShop;
            }
            if (!string.IsNullOrWhiteSpace(srcShop.Name)) dstShop.Name = srcShop.Name;
            foreach (var map in srcShop.MapsSeen)
                if (!string.IsNullOrWhiteSpace(map))
                    dstShop.MapsSeen.Add(map);
            if (srcShop.Items.Count > 0)
                dstShop.Items = srcShop.Items;
            if (srcShop.LastSeenUtc > dstShop.LastSeenUtc)
                dstShop.LastSeenUtc = srcShop.LastSeenUtc;
        }

        // 5. Merge Quests
        foreach (var (questId, srcQuest) in source.Quests)
        {
            if (!target.Quests.TryGetValue(questId, out var dstQuest))
            {
                dstQuest = new CapturedQuest { ID = questId };
                target.Quests[questId] = dstQuest;
            }
            if (!string.IsNullOrWhiteSpace(srcQuest.Name)) dstQuest.Name = srcQuest.Name;
            if (!string.IsNullOrWhiteSpace(srcQuest.Description)) dstQuest.Description = srcQuest.Description;
            if (!string.IsNullOrWhiteSpace(srcQuest.EndText)) dstQuest.EndText = srcQuest.EndText;
            dstQuest.Once = srcQuest.Once;
            dstQuest.Upgrade = srcQuest.Upgrade;
            if (srcQuest.Level > 0) dstQuest.Level = srcQuest.Level;
            if (srcQuest.Slot != 0) dstQuest.Slot = srcQuest.Slot;
            if (srcQuest.Value != 0) dstQuest.Value = srcQuest.Value;
            if (!string.IsNullOrWhiteSpace(srcQuest.Field)) dstQuest.Field = srcQuest.Field;
            if (srcQuest.Index != 0) dstQuest.Index = srcQuest.Index;
            if (srcQuest.Gold > 0) dstQuest.Gold = srcQuest.Gold;
            if (srcQuest.XP > 0) dstQuest.XP = srcQuest.XP;
            if (srcQuest.RequiredClassID > 0) dstQuest.RequiredClassID = srcQuest.RequiredClassID;
            if (srcQuest.RequiredClassPoints > 0) dstQuest.RequiredClassPoints = srcQuest.RequiredClassPoints;
            if (srcQuest.RequiredFactionId > 0) dstQuest.RequiredFactionId = srcQuest.RequiredFactionId;
            if (srcQuest.RequiredFactionRep > 0) dstQuest.RequiredFactionRep = srcQuest.RequiredFactionRep;

            if (srcQuest.AcceptRequirements.Count > 0) dstQuest.AcceptRequirements = srcQuest.AcceptRequirements;
            if (srcQuest.Requirements.Count > 0) dstQuest.Requirements = srcQuest.Requirements;
            // Keep reward lists that carry type/rate (from getQuests) over plain ones (from the Skua Quest model).
            if (srcQuest.Rewards.Count > 0 &&
                (srcQuest.Rewards.Any(r => r.RewardType.HasValue) || !dstQuest.Rewards.Any(r => r.RewardType.HasValue)))
                dstQuest.Rewards = srcQuest.Rewards;

            dstQuest.AcceptedCount += srcQuest.AcceptedCount;
            dstQuest.TurnInCount += srcQuest.TurnInCount;

            foreach (var map in srcQuest.MapsAcceptedOn)
                if (!string.IsNullOrWhiteSpace(map))
                    dstQuest.MapsAcceptedOn.Add(map);

            foreach (var map in srcQuest.MapsTurnedInOn)
                if (!string.IsNullOrWhiteSpace(map))
                    dstQuest.MapsTurnedInOn.Add(map);

            if (srcQuest.LastSeenUtc > dstQuest.LastSeenUtc)
                dstQuest.LastSeenUtc = srcQuest.LastSeenUtc;

            if (srcQuest.ObservedReward is { } srcReward)
            {
                var samples = srcReward.Samples + (dstQuest.ObservedReward?.Samples ?? 0);
                if (dstQuest.ObservedReward == null || srcReward.LastSeenUtc >= dstQuest.ObservedReward.LastSeenUtc)
                    dstQuest.ObservedReward = new CapturedQuestReward
                    {
                        Exp = srcReward.Exp, Gold = srcReward.Gold, Rep = srcReward.Rep, FactionID = srcReward.FactionID,
                        ClassPoints = srcReward.ClassPoints, Coins = srcReward.Coins, LastSeenUtc = srcReward.LastSeenUtc
                    };
                dstQuest.ObservedReward.Samples = samples;
            }
        }

        // 6. Merge Classes & Enhancements
        MergeClassesAndEnhancements(target, source);

        // 7. AQW XP table (level -> XP to next level; fixed game data)
        target.XpToNextLevel ??= new();
        foreach (var (level, toNext) in source.XpToNextLevel ?? new())
            if (level > 0 && toNext > 0)
                target.XpToNextLevel[level] = toNext;

        // 8. Sell prices (latest amount per item, samples summed)
        target.SellPrices ??= new();
        foreach (var (itemId, src) in source.SellPrices ?? new())
        {
            if (itemId <= 0)
                continue;
            if (!target.SellPrices.TryGetValue(itemId, out var dst))
                target.SellPrices[itemId] = dst = new CapturedSellPrice { ItemID = itemId, LastSeenUtc = DateTime.MinValue };
            dst.Samples += src.Samples;
            if (src.LastSeenUtc >= dst.LastSeenUtc)
            {
                dst.Amount = src.Amount;
                dst.Coins = src.Coins;
                dst.Quantity = src.Quantity;
                dst.LastSeenUtc = src.LastSeenUtc;
            }
        }

        // 8b. Collector builds that produced the data (flush counts)
        target.CollectorVersions ??= new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (version, flushes) in source.CollectorVersions ?? new Dictionary<string, long>())
            if (!string.IsNullOrWhiteSpace(version) && flushes > 0)
                target.CollectorVersions[version] = target.CollectorVersions.GetValueOrDefault(version) + flushes;

        // 8c. PvP events and scores per map
        target.Pvp ??= new Dictionary<string, CapturedPvpStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pvpMap, srcPvp) in source.Pvp ?? new Dictionary<string, CapturedPvpStats>())
        {
            if (!target.Pvp.TryGetValue(pvpMap, out var dstPvp))
                target.Pvp[pvpMap] = dstPvp = new CapturedPvpStats();
            dstPvp.Merge(srcPvp);
        }

        // 9. Merge faction names (ID -> name only)
        target.Factions ??= new();
        foreach (var (id, srcFaction) in source.Factions ?? new())
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(srcFaction.Name))
                continue;
            if (!target.Factions.TryGetValue(id, out var dstFaction))
                target.Factions[id] = dstFaction = new CapturedFaction { ID = id };
            dstFaction.Name = srcFaction.Name;
            if (srcFaction.LastSeenUtc > dstFaction.LastSeenUtc)
                dstFaction.LastSeenUtc = srcFaction.LastSeenUtc;
        }
    }

    private static GameDatabase LoadFromDiskInternal(string filePath)
    {
        foreach (var candidate in new[] { filePath, filePath + ".bak" })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var json = File.ReadAllText(candidate, Encoding.UTF8);
                var db = JsonConvert.DeserializeObject<GameDatabase>(json, JsonSettings);
                if (db != null)
                {
                    NormalizeLoadedDatabase(db);
                    return db;
                }
            }
            catch
            {
                // Try backup if primary was corrupted
            }
        }
        return new GameDatabase();
    }

    private static void SaveToDiskAtomic(string filePath, GameDatabase db)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = $"{filePath}.{Environment.ProcessId}.tmp";
        var bakPath = filePath + ".bak";

        var json = JsonConvert.SerializeObject(db, JsonSettings);
        File.WriteAllText(tmpPath, json, new UTF8Encoding(false));

        if (File.Exists(filePath))
        {
            try
            {
                File.Copy(filePath, bakPath, overwrite: true);
            }
            catch
            {
                // Non-fatal if backup fails
            }
        }

        File.Move(tmpPath, filePath, overwrite: true);
    }

    private static T ExecuteWithMutex<T>(Func<T> action)
    {
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(false, MutexName);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            return action();
        }
        finally
        {
            if (acquired && mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
        }
    }
}
