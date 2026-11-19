using System.IO.Compression;
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

    public GameDatabase LoadCurrent()
    {
        return ExecuteWithMutex(() => LoadFromDiskInternal(PrimaryFilePath));
    }

    /// <summary>
    /// Atomically reads the current database on disk, merges the in-memory delta into it,
    /// saves it back atomically, and updates any configured mirror files.
    /// </summary>
    public GameDatabase FlushDelta(GameDatabase delta)
    {
        return ExecuteWithMutex(() =>
        {
            var current = LoadFromDiskInternal(PrimaryFilePath);
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

    /// <summary>
    /// Exports a clean, shareable .zip archive containing gamedata.json and a summary manifest.
    /// </summary>
    public string ExportZipPackage(string exportDirectory)
    {
        Directory.CreateDirectory(exportDirectory);
        var db = LoadCurrent();
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
        var zipPath = Path.Combine(exportDirectory, $"skuasim_data_{timestamp}.zip");

        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        var dataEntry = archive.CreateEntry("gamedata.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(dataEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write(JsonConvert.SerializeObject(db, JsonSettings));
        }

        var manifest = new
        {
            ExportedAtUtc = DateTime.UtcNow,
            db.SchemaVersion,
            db.TotalFlushesMerged,
            Summary = new
            {
                MapsCount = db.Maps.Count,
                MonstersCount = db.Monsters.Count,
                TotalMonsterKills = db.Monsters.Values.Sum(m => m.KillCount),
                ItemsCount = db.Items.Count,
                ShopsCount = db.Shops.Count,
                QuestsCount = db.Quests.Count,
                ClassesCount = db.Classes.Count,
                EnhancementsCount = db.Enhancements.Count,
                ProcsCount = db.Procs.Count
            }
        };

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
        {
            writer.Write(JsonConvert.SerializeObject(manifest, JsonSettings));
        }

        return zipPath;
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
            if (srcMon.MaxHP > 0) dstMon.MaxHP = Math.Max(dstMon.MaxHP, srcMon.MaxHP);
            if (!string.IsNullOrWhiteSpace(srcMon.Race) && srcMon.Race != "None") dstMon.Race = srcMon.Race;
            else if (string.IsNullOrWhiteSpace(dstMon.Race) && !string.IsNullOrWhiteSpace(srcMon.Race)) dstMon.Race = srcMon.Race;
            if (!string.IsNullOrWhiteSpace(srcMon.Element) && srcMon.Element != "None") dstMon.Element = srcMon.Element;
            if (!string.IsNullOrWhiteSpace(srcMon.FileName)) dstMon.FileName = srcMon.FileName;

            dstMon.KillCount += srcMon.KillCount;
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
            }

            // Synchronize EligibleKills across all known drops of this monster with total KillCount
            foreach (var drop in dstMon.Drops.Values)
            {
                drop.EligibleKills = Math.Max(dstMon.KillCount, drop.DropEvents);
            }
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
            if (srcQuest.Rewards.Count > 0) dstQuest.Rewards = srcQuest.Rewards;

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
        }

        // 6. Merge Classes & Enhancements
        MergeClassesAndEnhancements(target, source);
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
