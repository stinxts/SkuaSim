using Newtonsoft.Json.Linq;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Quests;
using Skua.Core.Models.Shops;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

/// <summary>
/// Processes game packets and Skua model snapshots into an in-memory delta GameDatabase.
/// Thread-safe via internal lock; designed to be called from a background worker or event handlers.
/// </summary>
public sealed partial class PacketCollectorEngine
{
    private readonly object _sync = new();
    private GameDatabase _delta = new();

    private string _currentMapName = string.Empty;
    private string _currentCell = "Enter";
    private string _currentPad = "Spawn";

    // Active map monsters indexed by MonMapID
    private readonly Dictionary<int, ActiveMonsterState> _mapMonsters = new();

    // Recently killed monster tracking for drop attribution
    private string? _lastKilledMonsterKey;
    private int _lastKilledMonsterId;
    private string _lastKilledMonsterName = string.Empty;
    private DateTime _lastKillTimeUtc = DateTime.MinValue;

    // Recently dropped items via dropItem so addItems right after doesn't double-count
    private readonly Dictionary<int, DateTime> _recentDropItemTimes = new();

    // Recently requested map item via getMapItem so addItems can link GrantedItemIDs
    private int _lastRequestedMapItemId;
    private string _lastRequestedMapItemMap = string.Empty;
    private DateTime _lastRequestedMapItemTimeUtc = DateTime.MinValue;

    // Quest turn-in tracking so quest reward items are not credited to the monster killed just before.
    // Rewards are cached for the whole session (not only the current delta) because flushes reset the delta.
    private const double DropAttributionWindowSeconds = 5.0;
    private const double QuestRewardWindowSeconds = 3.0;
    private readonly Dictionary<int, HashSet<int>> _sessionQuestRewardItems = new();
    private int _lastTurnInQuestId;
    private DateTime _lastTurnInUtc = DateTime.MinValue;

    // Player's active quests (IDs only, kept in RAM): the denominator of quest-gated drop rates.
    private readonly HashSet<int> _activeQuestIds = new();
    private bool _activeQuestsKnown;
    public long SessionQuestRewardsExcluded { get; private set; }
    public long SessionUnattributedDropsSkipped { get; private set; }

    // Total cumulative statistics for this session (for UI/status reporting)
    public long SessionPacketsProcessed { get; private set; }
    public long SessionKillsRecorded { get; private set; }
    public long SessionDropsRecorded { get; private set; }
    public long SessionMapsVisited { get; private set; }

    public bool CollectCombatDetails { get; set; } = true;

    private sealed class ActiveMonsterState
    {
        public int MapID { get; set; }
        public int MonsterID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Cell { get; set; } = string.Empty;
        public int CurrentHP { get; set; }
        public int MaxHP { get; set; }
        public DateTime LastAttackUtc { get; set; } = DateTime.MinValue;
        public DateTime LastDeathCountedUtc { get; set; } = DateTime.MinValue;

        /// <summary>Death time awaiting its "respawnMon" (MinValue when the monster is alive or already measured).</summary>
        public DateTime PendingRespawnSinceUtc { get; set; } = DateTime.MinValue;

        public string Key => GameDatabase.GetMonsterKey(MonsterID, Name);
    }

    /// <summary>
    /// Atomically swaps out the accumulated delta since the last flush and returns it.
    /// </summary>
    public GameDatabase TakeDeltaSnapshot()
    {
        lock (_sync)
        {
            var snapshot = _delta;
            _delta = new GameDatabase();
            return snapshot;
        }
    }

    public bool HasPendingChanges()
    {
        lock (_sync)
        {
            return _delta.Maps.Count > 0
                || _delta.Monsters.Count > 0
                || _delta.Items.Count > 0
                || _delta.Shops.Count > 0
                || _delta.Quests.Count > 0
                || _delta.Classes.Count > 0
                || _delta.Enhancements.Count > 0
                || _delta.Procs.Count > 0
                || _delta.Factions.Count > 0
                || _delta.XpToNextLevel.Count > 0
                || _delta.SellPrices.Count > 0;
        }
    }

    /// <summary>
    /// Processes a Flash "pext" JSON string or serialized JObject.
    /// </summary>
    public void ProcessExtensionPacketJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return;

        JObject root;
        try
        {
            root = JObject.Parse(rawJson);
        }
        catch
        {
            return;
        }

        ProcessExtensionPacketJObject(root);
    }

    /// <summary>JSON server commands parsed by the switch below (anything else is only counted / skeleton-sampled).</summary>
    private static readonly HashSet<string> HandledServerCommands = new(StringComparer.Ordinal)
    {
        "moveToArea", "ct", "event", "updateClass", "equipItem", "sAct", "stu",
        "dropItem", "addItems", "loadShop", "getQuests", "ccqr", "monsterDrops", "addGoldExp", "addFaction", "levelUp", "sellItem"
    };

    private int _lastSellRequestItemId;
    private DateTime _lastSellRequestUtc = DateTime.MinValue;

    /// <summary>
    /// Server "sellItem" (verified keys): { intAmount, bCoins, iQty, CharItemID, iQtyNow }. Paired with the client's
    /// sell request of the last 5 s to know the item; CharItemID / iQtyNow (personal inventory state) are never stored.
    /// </summary>
    private void HandleSellItemResponse(JObject dataObj)
    {
        var itemId = _lastSellRequestItemId;
        var recent = (DateTime.UtcNow - _lastSellRequestUtc).TotalSeconds <= 5.0;
        _lastSellRequestItemId = 0;
        var amount = ParseInt(dataObj["intAmount"], -1);
        if (!recent || itemId <= 0 || amount < 0)
            return;
        if (!_delta.SellPrices.TryGetValue(itemId, out var price))
            _delta.SellPrices[itemId] = price = new CapturedSellPrice { ItemID = itemId };
        price.Amount = amount;
        price.Coins = ParseBool(dataObj["bCoins"], false);
        price.Quantity = ParseInt(dataObj["iQty"], 1);
        price.Samples++;
        price.LastSeenUtc = DateTime.UtcNow;
    }

    public void ProcessExtensionPacketJObject(JObject root)
    {
        var prms = root["params"] as JObject ?? root;
        var type = prms["type"]?.ToString();
        if (string.Equals(type, "str", StringComparison.OrdinalIgnoreCase))
        {
            // String packets: census only (command name, no payload). dataObj is ["cmd", "-1", ...].
            if (prms["dataObj"] is JArray strParts && strParts.Count > 0 && strParts[0]?.Type == JTokenType.String)
            {
                SchemaSampler.CountCommand("str:" + strParts[0]);
                lock (_sync)
                {
                    SchemaSampler.TryCaptureStringPacket(strParts, _selfPlayerEntityId, _selfUsername, _currentMapName);
                    if (string.Equals(strParts[0]!.ToString(), "respawnMon", StringComparison.Ordinal))
                        HandleRespawnMon(strParts, DateTime.UtcNow);
                }
            }
            return;
        }
        if (!string.Equals(type, "json", StringComparison.OrdinalIgnoreCase))
            return;

        if (prms["dataObj"] is not JObject dataObj)
            return;

        var cmd = dataObj["cmd"]?.ToString();
        if (string.IsNullOrEmpty(cmd))
            return;

        lock (_sync)
        {
            SessionPacketsProcessed++;
            SchemaSampler.CountCommand(cmd);
            SchemaSampler.CountKeySet(cmd, dataObj);
            var sampleMap = cmd == "moveToArea"
                ? GameDatabase.NormalizeMapName(dataObj["strMapName"]?.ToString())
                : _currentMapName;
            SchemaSampler.TryCaptureProtocol(cmd, dataObj, HandledServerCommands.Contains(cmd), _selfPlayerEntityId, _selfUsername, sampleMap);

            switch (cmd)
            {
                case "moveToArea":
                    HandleMoveToArea(dataObj);
                    break;
                case "ct":
                    var procsRecordedThisTick = new HashSet<int>();
                    HandleCombatTick(dataObj, procsRecordedThisTick);
                    HandlePlayerCombatTick(dataObj, procsRecordedThisTick);
                    SampleCombatPacketSchema(dataObj);
                    break;
                case "event":
                    HandleZoneEventPacket(dataObj);
                    break;
                case "updateClass":
                    HandleUpdateClassPacket(dataObj);
                    break;
                case "equipItem":
                    HandleEquipItemPacket(dataObj);
                    break;
                case "sAct":
                    HandleSkillActionsPacket(dataObj);
                    break;
                case "stu":
                    HandleStatUpdatePacket(dataObj);
                    break;
                case "dropItem":
                    HandleDropItemPacket(dataObj);
                    break;
                case "addItems":
                    HandleAddItemsPacket(dataObj);
                    break;
                case "loadShop":
                    HandleLoadShopPacket(dataObj);
                    break;
                case "getQuests":
                    HandleGetQuestsPacket(dataObj);
                    break;
                case "ccqr":
                    HandleQuestCompleteResponse(dataObj);
                    break;
                case "monsterDrops":
                    HandleMonsterDropsPacket(dataObj);
                    break;
                case "addGoldExp":
                    HandleAddGoldExpPacket(dataObj);
                    break;
                case "addFaction":
                    HandleAddFactionPacket(dataObj);
                    break;
                case "levelUp":
                    HandleLevelUpPacket(dataObj);
                    break;
                case "sellItem":
                    HandleSellItemResponse(dataObj);
                    break;
            }
        }
    }

    /// <summary>
    /// Processes a client-sent %xt%zm%... packet string.
    /// </summary>
    public void ProcessClientPacket(string packet)
    {
        if (string.IsNullOrWhiteSpace(packet) || !packet.StartsWith("%xt%", StringComparison.Ordinal))
            return;

        // Format: %xt%zm%<cmd>%<areaId>%<arg0>%<arg1>%...
        var parts = packet.Split('%', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return;

        var cmd = parts[2];

        lock (_sync)
        {
            SessionPacketsProcessed++;

            switch (cmd)
            {
                case "moveToCell":
                    if (parts.Length >= 6)
                    {
                        var cell = parts[4];
                        var pad = parts[5];
                        RecordCellVisit(_currentMapName, cell, pad);
                    }
                    break;

                case "getMapItem":
                    if (parts.Length >= 5 && int.TryParse(parts[4], out var mapItemId) && mapItemId > 0)
                    {
                        RecordMapItemRequest(_currentMapName, _currentCell, mapItemId);
                    }
                    break;

                case "acceptQuest":
                    if (parts.Length >= 5 && int.TryParse(parts[4], out var questId) && questId > 0)
                    {
                        RecordQuestAccepted(questId, _currentMapName);
                    }
                    break;

                case "sellItem":
                    // %xt%zm%sellItem%<room>%<ItemID>%<qty>%<CharItemID>% — only the item ID is kept (CharItemID is personal).
                    if (parts.Length >= 5 && int.TryParse(parts[4], out var sellItemId) && sellItemId > 0)
                    {
                        _lastSellRequestItemId = sellItemId;
                        _lastSellRequestUtc = DateTime.UtcNow;
                    }
                    break;

                case "tryQuestComplete":
                    // %xt%zm%tryQuestComplete%<room>%<questId>%<rewardItemId>%... — reward items arrive right after.
                    if (parts.Length >= 5 && int.TryParse(parts[4], out var completingQuestId) && completingQuestId > 0)
                        MarkQuestTurnIn(completingQuestId);
                    break;
            }
        }
    }

    public void RecordMapSnapshot(
        string? mapName,
        string? mapFileName,
        string? currentCell,
        string? currentPad,
        IEnumerable<string>? mapCells,
        IEnumerable<Monster>? mapMonsters)
    {
        var normMap = GameDatabase.NormalizeMapName(mapName);
        if (string.IsNullOrEmpty(normMap))
            return;

        lock (_sync)
        {
            var isNewVisit = !string.Equals(_currentMapName, normMap, StringComparison.OrdinalIgnoreCase);
            _currentMapName = normMap;
            if (!string.IsNullOrWhiteSpace(currentCell))
                _currentCell = currentCell;
            if (!string.IsNullOrWhiteSpace(currentPad))
                _currentPad = currentPad;

            var map = GetOrCreateMap(normMap);
            if (!string.IsNullOrWhiteSpace(mapFileName))
                map.FileName = mapFileName;
            if (isNewVisit)
            {
                map.Visits++;
                SessionMapsVisited++;
            }

            RecordCellVisit(normMap, _currentCell, _currentPad);

            if (mapCells != null)
            {
                foreach (var c in mapCells)
                {
                    if (!string.IsNullOrWhiteSpace(c) && !map.Cells.ContainsKey(c))
                        map.Cells[c] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }

            if (mapMonsters != null)
            {
                foreach (var m in mapMonsters)
                {
                    if (m == null || string.IsNullOrWhiteSpace(m.Name))
                        continue;

                    RegisterMonsterSpawn(
                        normMap,
                        m.MapID,
                        m.ID,
                        m.Name,
                        m.Cell ?? string.Empty,
                        0,
                        m.MaxHP,
                        m.Race ?? string.Empty,
                        string.Empty,
                        m.FileName ?? string.Empty,
                        m.HP);
                }
            }
        }
    }

    public void RecordCellChange(string? mapName, string? cell, string? pad)
    {
        lock (_sync)
        {
            var normMap = GameDatabase.NormalizeMapName(string.IsNullOrWhiteSpace(mapName) ? _currentMapName : mapName);
            RecordCellVisit(normMap, cell, pad);
        }
    }

    public void RecordMonsterKilled(int monMapId)
    {
        lock (_sync)
        {
            RegisterMonsterKillInternal(monMapId);
        }
    }

    public void RecordPlayerDeath()
    {
        lock (_sync)
        {
            var cellMonsters = GetMonstersInCell(_currentCell);
            foreach (var mon in cellMonsters)
            {
                mon.Combat.PlayerDeathsInCell++;
            }
        }
    }

    public void RecordCounterAttack(bool faded)
    {
        if (faded)
            return;

        lock (_sync)
        {
            var cellMonsters = GetMonstersInCell(_currentCell);
            foreach (var mon in cellMonsters)
            {
                mon.Combat.CounterAttacksObserved++;
            }
        }
    }

    public void RecordItemDropped(ItemBase? item, bool addedToInv, int quantityNow)
    {
        if (item == null || item.ID <= 0)
            return;

        lock (_sync)
        {
            UpsertItemFromModel(item);

            // Deduplicate if dropItem JSON packet already recorded this item within the last 1.5s
            var now = DateTime.UtcNow;
            if (_recentDropItemTimes.TryGetValue(item.ID, out var prevTime) && (now - prevTime).TotalMilliseconds < 1500)
                return;

            _recentDropItemTimes[item.ID] = now;
            AttributeDropToMonster(item.ID, item.Name ?? string.Empty, item.Temp, Math.Max(1, item.Quantity));
        }
    }

    public void RecordShopLoaded(ShopInfo? shopInfo)
    {
        if (shopInfo == null || shopInfo.ID <= 0)
            return;

        lock (_sync)
        {
            var shop = GetOrCreateShop(shopInfo.ID);
            if (!string.IsNullOrWhiteSpace(shopInfo.Name))
                shop.Name = shopInfo.Name;
            if (!string.IsNullOrWhiteSpace(_currentMapName))
                shop.MapsSeen.Add(_currentMapName);
            shop.LastSeenUtc = DateTime.UtcNow;

            if (shopInfo.Items is { Count: > 0 })
            {
                var capturedItems = new List<CapturedShopItem>(shopInfo.Items.Count);
                foreach (var si in shopInfo.Items)
                {
                    if (si == null || si.ID <= 0)
                        continue;

                    UpsertItemFromModel(si);

                    var csi = new CapturedShopItem
                    {
                        ShopItemID = si.ShopItemID,
                        ID = si.ID,
                        Name = si.Name ?? string.Empty,
                        Category = si.CategoryString ?? string.Empty,
                        Cost = si.Cost,
                        Coins = si.Coins,
                        Upgrade = si.Upgrade,
                        Level = si.Level,
                        MaxStack = Math.Max(1, si.MaxStack),
                        Faction = si.Faction ?? string.Empty,
                        RequiredReputation = si.RequiredReputation,
                        TokenID = si.TokenID,
                        TokenQuantity = si.TokenQuantity,
                        TokenName = si.TokenName ?? string.Empty,
                        EnhancementPatternID = si.EnhancementPatternID,
                        ProcID = si.ProcID,
                        Meta = si.Meta ?? string.Empty
                    };

                    if (si.Requirements is { Count: > 0 })
                    {
                        foreach (var req in si.Requirements)
                        {
                            if (req == null || req.ID <= 0)
                                continue;
                            UpsertItemFromModel(req);
                            csi.Requirements.Add(new CapturedItemRequirement
                            {
                                ItemID = req.ID,
                                ItemName = req.Name ?? string.Empty,
                                Quantity = Math.Max(1, req.Quantity),
                                Temp = req.Temp
                            });
                        }
                    }

                    capturedItems.Add(csi);
                }

                if (capturedItems.Count > 0)
                    shop.Items = capturedItems;
            }
        }
    }

    public void RecordQuestModel(Quest? quest, bool accepted = false, bool turnedIn = false)
    {
        if (quest == null || quest.ID <= 0)
            return;

        lock (_sync)
        {
            var cq = GetOrCreateQuest(quest.ID);
            if (!string.IsNullOrWhiteSpace(quest.Name)) cq.Name = quest.Name;
            if (!string.IsNullOrWhiteSpace(quest.Description)) cq.Description = quest.Description;
            if (!string.IsNullOrWhiteSpace(quest.EndText)) cq.EndText = quest.EndText;
            cq.Once = quest.Once;
            cq.Upgrade = quest.Upgrade;
            if (quest.Level > 0) cq.Level = quest.Level;
            if (quest.Slot != 0) cq.Slot = quest.Slot;
            if (quest.Value != 0) cq.Value = quest.Value;
            if (!string.IsNullOrWhiteSpace(quest.Field)) cq.Field = quest.Field;
            if (quest.Index != 0) cq.Index = quest.Index;
            if (quest.Gold > 0) cq.Gold = quest.Gold;
            if (quest.XP > 0) cq.XP = quest.XP;
            if (quest.RequiredClassID > 0) cq.RequiredClassID = quest.RequiredClassID;
            if (quest.RequiredClassPoints > 0) cq.RequiredClassPoints = quest.RequiredClassPoints;
            if (quest.RequiredFactionId > 0) cq.RequiredFactionId = quest.RequiredFactionId;
            if (quest.RequiredFactionRep > 0) cq.RequiredFactionRep = quest.RequiredFactionRep;

            if (quest.AcceptRequirements is { Count: > 0 })
            {
                cq.AcceptRequirements = quest.AcceptRequirements
                    .Where(r => r != null && r.ID > 0)
                    .Select(r =>
                    {
                        UpsertItemFromModel(r);
                        return new CapturedItemRequirement
                        {
                            ItemID = r.ID,
                            ItemName = r.Name ?? string.Empty,
                            Quantity = Math.Max(1, r.Quantity),
                            Temp = r.Temp
                        };
                    })
                    .ToList();
            }

            if (quest.Requirements is { Count: > 0 })
            {
                cq.Requirements = quest.Requirements
                    .Where(r => r != null && r.ID > 0)
                    .Select(r =>
                    {
                        UpsertItemFromModel(r);
                        return new CapturedItemRequirement
                        {
                            ItemID = r.ID,
                            ItemName = r.Name ?? string.Empty,
                            Quantity = Math.Max(1, r.Quantity),
                            Temp = r.Temp
                        };
                    })
                    .ToList();
            }

            if (quest.Rewards is { Count: > 0 })
            {
                var modelRewards = quest.Rewards
                    .Where(r => r != null && r.ID > 0)
                    .Select(r =>
                    {
                        UpsertItemFromModel(r);
                        CacheQuestRewardItem(quest.ID, r.ID);
                        return new CapturedItemRequirement
                        {
                            ItemID = r.ID,
                            ItemName = r.Name ?? string.Empty,
                            Quantity = Math.Max(1, r.Quantity),
                            Temp = r.Temp
                        };
                    })
                    .ToList();
                // Do not replace rewards parsed from getQuests (which carry type/rate) with the plain model list.
                if (!cq.Rewards.Any(r => r.RewardType.HasValue))
                    cq.Rewards = modelRewards;
            }

            if (turnedIn)
                MarkQuestTurnIn(quest.ID);

            if (accepted)
            {
                cq.AcceptedCount++;
                if (!string.IsNullOrWhiteSpace(_currentMapName))
                    cq.MapsAcceptedOn.Add(_currentMapName);
            }

            if (turnedIn)
            {
                cq.TurnInCount++;
                if (!string.IsNullOrWhiteSpace(_currentMapName))
                    cq.MapsTurnedInOn.Add(_currentMapName);
            }

            cq.LastSeenUtc = DateTime.UtcNow;
        }
    }

    private void HandleMoveToArea(JObject dataObj)
    {
        var rawMapName = dataObj["strMapName"]?.ToString() ?? dataObj["areaName"]?.ToString();
        var normMap = GameDatabase.NormalizeMapName(rawMapName);
        if (string.IsNullOrEmpty(normMap))
            return;

        _currentMapName = normMap;
        _currentCell = "Enter";
        _currentPad = "Spawn";
        _mapMonsters.Clear();
        _lastSelfHp = -1;

        var map = GetOrCreateMap(normMap);
        map.Visits++;
        SessionMapsVisited++;
        map.LastSeenUtc = DateTime.UtcNow;

        var mapFileName = dataObj["strMapFileName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(mapFileName))
            map.FileName = mapFileName;

        RecordCellVisit(normMap, _currentCell, _currentPad);

        // Real AQW keys (verified from live samples, 2026-11-20): "mondef" { MonID, strMonName, intLevel, sRace,
        // strMonFileName }, "monmap" { MonMapID, MonID, strFrame }, "monBranch" (capital B) { MonMapID, MonID, iLvl,
        // intHP, intHPMax, wDPS }. Lookups are case-insensitive and keep the older guesses as fallbacks.

        // 1. Parse mondef (monster definitions by MonID)
        var monDefs = new Dictionary<int, (string Name, int Level, int MaxHP, string Race, string Element, string FileName)>();
        if (GetArrayIgnoreCase(dataObj, "mondef") is { } monDefArray)
        {
            foreach (var token in monDefArray.OfType<JObject>())
            {
                var monId = ParseInt(token["MonID"]);
                if (monId <= 0)
                    continue;
                var name = token["strMonName"]?.ToString() ?? string.Empty;
                var level = ParseInt(token["intLevel"], ParseInt(token["iLvl"]));
                var maxHp = ParseInt(token["intHPMax"], ParseInt(token["intHP"]));
                var race = token["sRace"]?.ToString() ?? token["strRace"]?.ToString() ?? string.Empty;
                var element = token["strElement"]?.ToString() ?? token["sElmt"]?.ToString() ?? string.Empty;
                var file = token["strMonFileName"]?.ToString() ?? string.Empty;
                monDefs[monId] = (name, level, maxHp, race, element, file);
            }
        }

        // 2. Parse monmap (MonMapID -> strFrame cell)
        var monMapCells = new Dictionary<int, string>();
        if (GetArrayIgnoreCase(dataObj, "monmap") is { } monMapArray)
        {
            foreach (var token in monMapArray.OfType<JObject>())
            {
                var monMapId = ParseInt(token["MonMapID"]);
                var frame = token["strFrame"]?.ToString() ?? string.Empty;
                if (monMapId > 0 && !string.IsNullOrWhiteSpace(frame))
                {
                    monMapCells[monMapId] = frame;
                    if (!map.Cells.ContainsKey(frame))
                        map.Cells[frame] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        // 3. Parse monbranch (MonMapID instances)
        if (GetArrayIgnoreCase(dataObj, "monBranch") is { } monBranchArray)
        {
            foreach (var token in monBranchArray.OfType<JObject>())
            {
                var monMapId = ParseInt(token["MonMapID"]);
                var monId = ParseInt(token["MonID"]);
                if (monMapId <= 0)
                    continue;

                var hp = ParseInt(token["intHP"]);
                var maxHp = ParseInt(token["intHPMax"], hp);
                var level = ParseInt(token["iLvl"], ParseInt(token["intLevel"]));
                var weaponDps = ParseInt(token["wDPS"]);

                monDefs.TryGetValue(monId, out var def);
                monMapCells.TryGetValue(monMapId, out var cell);

                var finalName = !string.IsNullOrWhiteSpace(def.Name)
                    ? def.Name
                    : (token["strMonName"]?.ToString() ?? string.Empty);
                var finalLevel = level > 0 ? level : def.Level;
                var finalMaxHp = maxHp > 0 ? maxHp : def.MaxHP;

                if (!string.IsNullOrWhiteSpace(finalName))
                {
                    RegisterMonsterSpawn(
                        normMap,
                        monMapId,
                        monId,
                        finalName,
                        cell ?? string.Empty,
                        finalLevel,
                        finalMaxHp,
                        def.Race ?? string.Empty,
                        def.Element ?? string.Empty,
                        def.FileName ?? string.Empty,
                        hp > 0 ? hp : finalMaxHp,
                        weaponDps);
                }
            }
        }
    }

    private static JArray? GetArrayIgnoreCase(JObject obj, string key) =>
        obj.GetValue(key, StringComparison.OrdinalIgnoreCase) as JArray;

    private void HandleCombatTick(JObject dataObj, HashSet<int> procsRecordedThisTick)
    {
        var now = DateTime.UtcNow;
        var monsterAurasThisTick = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monsterTargetsThisTick = new Dictionary<int, HashSet<string>>();

        // 1. Direct action array "a"
        if (dataObj["a"] is JArray aArray)
        {
            foreach (var act in aArray.OfType<JObject>())
            {
                ProcessCombatAction(act, null, now, monsterAurasThisTick, monsterTargetsThisTick, procsRecordedThisTick);
            }
        }

        // 2. Skill action results "sarsa"
        if (dataObj["sarsa"] is JArray sarsaArray)
        {
            foreach (var sarsaEntry in sarsaArray.OfType<JObject>())
            {
                var outerCaster = sarsaEntry["cInf"]?.ToString();
                if (sarsaEntry["a"] is JArray innerActions)
                {
                    foreach (var act in innerActions.OfType<JObject>())
                    {
                        ProcessCombatAction(act, outerCaster, now, monsterAurasThisTick, monsterTargetsThisTick, procsRecordedThisTick);
                    }
                }
            }
        }

        // 3. Monster attacks, dodges, misses & DoT/HoT ticks in "sara" (actionResult entries)
        if (dataObj["sara"] is JArray saraArray)
        {
            foreach (var saraEntry in saraArray.OfType<JObject>())
            {
                var actionObj = saraEntry["actionResult"] as JObject ?? saraEntry;
                var outerCaster = saraEntry["cInf"]?.ToString();
                ProcessCombatAction(actionObj, outerCaster, now, monsterAurasThisTick, monsterTargetsThisTick, procsRecordedThisTick);
            }
        }

        // Update AoE MaxTargetsHit across all distinct player targets hit by each monster in this tick
        foreach (var (monMapId, targetSet) in monsterTargetsThisTick)
        {
            if (targetSet.Count > 0)
            {
                var mon = ResolveMonsterByMapId(monMapId);
                if (mon != null && targetSet.Count > mon.Combat.MaxTargetsHit)
                    mon.Combat.MaxTargetsHit = targetSet.Count;
            }
        }

        // 4. Animations / special boss messages "anims" (captures non-generic animStr and msg)
        if (dataObj["anims"] is JArray animsArray)
        {
            HandleCombatAnimations(animsArray, now, monsterAurasThisTick);
        }

        // 5. Monster HP state updates "m"
        if (dataObj["m"] is JObject mObj)
        {
            foreach (var prop in mObj.Properties())
            {
                if (!int.TryParse(prop.Name, out var monMapId) || prop.Value is not JObject monState)
                    continue;

                var newHpToken = monState["intHP"];
                if (newHpToken != null)
                {
                    var newHp = ParseInt(newHpToken, -1);
                    if (newHp == 0)
                    {
                        RegisterMonsterKillInternal(monMapId);
                    }
                    else if (newHp > 0 && _mapMonsters.TryGetValue(monMapId, out var active))
                    {
                        active.CurrentHP = newHp;
                    }
                }
            }
        }
    }

    private void ProcessCombatAction(
        JObject act,
        string? fallbackCaster,
        DateTime now,
        HashSet<string> monsterAurasThisTick,
        Dictionary<int, HashSet<string>> monsterTargetsThisTick,
        HashSet<int> procsRecordedThisTick)
    {
        var cInf = act["cInf"]?.ToString() ?? fallbackCaster ?? string.Empty;
        var tInf = act["tInf"]?.ToString() ?? string.Empty;
        var actionCmd = act["cmd"]?.ToString();

        // Check if this action is a monster death notification (typ == "m" && hp/intState)
        var typ = act["typ"]?.ToString();
        if (string.Equals(typ, "m", StringComparison.OrdinalIgnoreCase))
        {
            var id = ParseInt(act["id"]);
            if (id > 0 && act["hp"] != null && ParseInt(act["cmd"] ?? act["intState"], 0) == 0)
            {
                // Handled via "m" dictionary or MonsterKilledMessage
            }
        }

        if (!CollectCombatDetails)
            return;

        // Monster attacking one or more players (ignore pure aura entries & DoT/HoT ticks)
        var targets = SplitEntityRefs(tInf);
        var playerTargets = targets.Where(t => t.StartsWith("p:", StringComparison.OrdinalIgnoreCase)).ToList();
        var rawType = act["type"]?.ToString();
        var isDotHotTick = string.IsNullOrEmpty(rawType) && string.Equals(typ, "d", StringComparison.OrdinalIgnoreCase);

        if (!IsAuraOnlyAction(act) &&
            !isDotHotTick &&
            cInf.StartsWith("m:", StringComparison.OrdinalIgnoreCase) &&
            playerTargets.Count > 0)
        {
            if (int.TryParse(cInf.AsSpan(2), out var monMapId))
            {
                var mon = ResolveMonsterByMapId(monMapId);
                if (mon != null)
                {
                    var hitType = (rawType ?? "hit").ToLowerInvariant();
                    var hp = ParseInt(act["hp"] ?? act["d"]);

                    // Ignore non-damaging "none" buff/cast markers with 0 hp
                    if (hitType == "none" && hp == 0)
                        goto ProcessAuras;

                    if (!monsterTargetsThisTick.TryGetValue(monMapId, out var tSet))
                    {
                        tSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        monsterTargetsThisTick[monMapId] = tSet;
                    }
                    foreach (var pt in playerTargets)
                        tSet.Add(pt);

                    if (playerTargets.Count > mon.Combat.MaxTargetsHit)
                        mon.Combat.MaxTargetsHit = playerTargets.Count;

                    mon.Combat.AttacksObserved++;
                    switch (hitType)
                    {
                        case "crit":
                            mon.Combat.Crits++;
                            break;
                        case "d":
                        case "dodge":
                            mon.Combat.Dodges++;
                            break;
                        case "m":
                        case "miss":
                            mon.Combat.Misses++;
                            break;
                        default:
                            if (hp > 0)
                                mon.Combat.Hits++;
                            break;
                    }

                    if (hp > 0 && hitType is not ("d" or "dodge" or "m" or "miss"))
                    {
                        mon.Combat.TotalDamage += hp;
                        mon.Combat.MinDamage = mon.Combat.MinDamage.HasValue
                            ? Math.Min(mon.Combat.MinDamage.Value, hp)
                            : hp;
                        mon.Combat.MaxDamage = mon.Combat.MaxDamage.HasValue
                            ? Math.Max(mon.Combat.MaxDamage.Value, hp)
                            : hp;
                    }

                    if (_mapMonsters.TryGetValue(monMapId, out var activeState))
                    {
                        if (activeState.LastAttackUtc != DateTime.MinValue)
                        {
                            var elapsedMs = (long)(now - activeState.LastAttackUtc).TotalMilliseconds;
                            // Ignore multi-target splash hits from the same swing (< 250ms) and out-of-combat gaps (> 30s)
                            if (elapsedMs is >= 250 and <= 30000)
                            {
                                mon.Combat.AttackIntervalSamples++;
                                mon.Combat.TotalAttackIntervalMs += elapsedMs;
                            }
                        }
                        if (activeState.LastAttackUtc == DateTime.MinValue || (now - activeState.LastAttackUtc).TotalMilliseconds >= 250)
                        {
                            activeState.LastAttackUtc = now;
                        }
                    }
                }
            }
        }

        ProcessAuras:
        // Auras applied in this action (monster, player/party, and weapon procs)
        if (act["aura"] is JObject singleAura)
        {
            ProcessAuraObjectEnhanced(singleAura, cInf, tInf, actionCmd, now, monsterAurasThisTick, procsRecordedThisTick);
        }
        if (act["auras"] is JArray aurasArray)
        {
            foreach (var auraObj in aurasArray.OfType<JObject>())
            {
                ProcessAuraObjectEnhanced(auraObj, cInf, tInf, actionCmd, now, monsterAurasThisTick, procsRecordedThisTick);
            }
        }
    }

    private void HandleDropItemPacket(JObject dataObj)
    {
        if (dataObj["items"] is not JObject itemsObj)
            return;

        var now = DateTime.UtcNow;
        foreach (var prop in itemsObj.Properties())
        {
            if (prop.Value is not JObject itemToken)
                continue;

            var item = UpsertItemFromJson(itemToken, prop.Name);
            if (item == null)
                continue;

            var qty = Math.Max(1, ParseInt(itemToken["iQty"], 1));
            _recentDropItemTimes[item.ID] = now;
            AttributeDropToMonster(item.ID, item.Name, item.Temp, qty);
        }
    }

    /// <summary>
    /// Passive capture of the server's official drop list (sent when any tool requests getMonsterDrops, e.g. the
    /// weekly/merge generators). Marks drops as official and quest-gated; never changes observed counts or rates.
    /// Shape: { MonMapID, items: { "&lt;ItemID&gt;": { ItemID, sName, bTemp, sReqQuests, questGated[], questObjective[] } } }
    /// (items may also arrive as an array).
    /// </summary>
    public long SessionOfficialDropListsCaptured { get; private set; }

    private void HandleMonsterDropsPacket(JObject dataObj)
    {
        var monMapId = ParseInt(dataObj["MonMapID"]);
        if (monMapId <= 0 || !_mapMonsters.TryGetValue(monMapId, out var active))
            return;

        IEnumerable<(JObject Item, string? Key)> entries = dataObj["items"] switch
        {
            JObject keyed => keyed.Properties().Where(p => p.Value is JObject).Select(p => ((JObject)p.Value, (string?)p.Name)),
            JArray arr => arr.OfType<JObject>().Select(o => (o, (string?)null)),
            _ => Enumerable.Empty<(JObject, string?)>()
        };

        var mon = GetOrCreateMonster(active.MonsterID, active.Name);
        foreach (var (itemObj, key) in entries)
        {
            var item = UpsertItemFromJson(itemObj, key);
            if (item == null)
                continue;

            if (!mon.Drops.TryGetValue(item.ID, out var drop))
                mon.Drops[item.ID] = drop = new CapturedDropStat { ItemID = item.ID, ItemName = item.Name, Temp = item.Temp };
            if (!string.IsNullOrWhiteSpace(item.Name))
                drop.ItemName = item.Name;
            drop.Temp |= item.Temp;
            drop.IsOfficialDrop = true;

            var gatedNames = ReadStringList(itemObj["questGated"]);
            drop.QuestGatedNames.UnionWith(gatedNames);
            drop.QuestObjectives.UnionWith(ReadStringList(itemObj["questObjective"]));
            if (gatedNames.Count > 0 || item.RequiredByQuestIds.Count > 0)
            {
                drop.QuestGated = true;
                drop.RequiredQuestIds.UnionWith(item.RequiredByQuestIds);
            }
        }

        mon.OfficialDropsObservedUtc = DateTime.UtcNow;
        DropRates.Recompute(mon);
        SessionOfficialDropListsCaptured++;
    }

    private static List<string> ReadStringList(JToken? token) => token switch
    {
        JArray arr => arr.Select(t => t?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        JValue v when !string.IsNullOrWhiteSpace(v.ToString()) => new List<string> { v.ToString() },
        _ => new List<string>()
    };

    private void HandleAddItemsPacket(JObject dataObj)
    {
        if (dataObj["items"] is not JObject itemsObj)
            return;

        var now = DateTime.UtcNow;
        var isRecentMapItemPickup = _lastRequestedMapItemId > 0
            && string.Equals(_lastRequestedMapItemMap, _currentMapName, StringComparison.OrdinalIgnoreCase)
            && (now - _lastRequestedMapItemTimeUtc).TotalSeconds <= 4.0;

        foreach (var prop in itemsObj.Properties())
        {
            if (prop.Value is not JObject itemToken)
                continue;

            var item = UpsertItemFromJson(itemToken, prop.Name);
            if (item == null)
                continue;

            if (isRecentMapItemPickup)
            {
                // The delta may have been flushed between getMapItem and addItems; recreate the entry if needed.
                var map = GetOrCreateMap(_currentMapName);
                if (!map.MapItems.TryGetValue(_lastRequestedMapItemId, out var mapItem))
                    map.MapItems[_lastRequestedMapItemId] = mapItem = new CapturedMapItem { MapItemID = _lastRequestedMapItemId };
                mapItem.GrantedItemIDs.Add(item.ID);
                mapItem.RequiredQuestIds.UnionWith(item.RequiredByQuestIds);
                if (_activeQuestsKnown)
                {
                    mapItem.GrantObservations++;
                    foreach (var questId in _activeQuestIds)
                        mapItem.ActiveQuestCounts[questId] = mapItem.ActiveQuestCounts.GetValueOrDefault(questId) + 1;
                }
                continue;
            }

            // If this item was NOT just announced by dropItem (e.g. temp quest items added directly via addItems on monster kill),
            // attribute it to the monster if a kill just happened or if it's a temporary item in combat.
            if (!_recentDropItemTimes.TryGetValue(item.ID, out var recentDropTime) || (now - recentDropTime).TotalMilliseconds >= 1500)
            {
                if (item.Temp || (now - _lastKillTimeUtc).TotalSeconds <= 3.0)
                {
                    var qty = Math.Max(1, ParseInt(itemToken["iQty"], 1));
                    _recentDropItemTimes[item.ID] = now;
                    AttributeDropToMonster(item.ID, item.Name, item.Temp, qty);
                }
            }
        }
    }

    private void HandleLoadShopPacket(JObject dataObj)
    {
        if (dataObj["shopinfo"] is not JObject shopInfo)
            return;

        var shopId = ParseInt(shopInfo["ShopID"]);
        if (shopId <= 0)
            return;

        var shop = GetOrCreateShop(shopId);
        var shopName = shopInfo["sName"]?.ToString() ?? shopInfo["strName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(shopName))
            shop.Name = shopName;
        if (!string.IsNullOrWhiteSpace(_currentMapName))
            shop.MapsSeen.Add(_currentMapName);
        shop.LastSeenUtc = DateTime.UtcNow;

        if (shopInfo["items"] is JArray itemsArray)
        {
            var list = new List<CapturedShopItem>();
            foreach (var itemObj in itemsArray.OfType<JObject>())
            {
                var capturedItem = UpsertItemFromJson(itemObj, null);
                if (capturedItem == null)
                    continue;

                var csi = new CapturedShopItem
                {
                    ShopItemID = ParseInt(itemObj["ShopItemID"]),
                    ID = capturedItem.ID,
                    Name = capturedItem.Name,
                    Category = capturedItem.Category,
                    Cost = ParseInt(itemObj["iCost"]),
                    Coins = ParseInt(itemObj["bCoins"]) == 1 || ParseBool(itemObj["bCoins"]),
                    Upgrade = ParseInt(itemObj["bUpg"]) == 1 || ParseBool(itemObj["bUpg"]),
                    Level = ParseInt(itemObj["iLvl"]),
                    MaxStack = Math.Max(1, capturedItem.MaxStack),
                    Faction = itemObj["sFaction"]?.ToString() ?? string.Empty,
                    RequiredReputation = ParseInt(itemObj["iReqRep"]),
                    EnhancementPatternID = ParseInt(itemObj["EnhPatternID"]),
                    ProcID = ParseInt(itemObj["ProcID"]),
                    Meta = itemObj["sMeta"]?.ToString() ?? string.Empty
                };

                if (itemObj["turnin"] is JArray turninArr)
                {
                    foreach (var reqObj in turninArr.OfType<JObject>())
                    {
                        var reqItemId = ParseInt(reqObj["ItemID"]);
                        var reqQty = Math.Max(1, ParseInt(reqObj["iQty"], 1));
                        var reqName = reqObj["sName"]?.ToString() ?? string.Empty;
                        if (reqItemId > 0)
                        {
                            csi.Requirements.Add(new CapturedItemRequirement
                            {
                                ItemID = reqItemId,
                                ItemName = reqName,
                                Quantity = reqQty
                            });
                        }
                    }
                }

                list.Add(csi);
            }

            if (list.Count > 0)
                shop.Items = list;
        }
    }

    private void HandleGetQuestsPacket(JObject dataObj)
    {
        if (dataObj["quests"] is not JObject questsObj)
            return;

        foreach (var prop in questsObj.Properties())
        {
            if (prop.Value is not JObject qObj)
                continue;

            var questId = ParseInt(qObj["QuestID"], ParseInt(prop.Name));
            if (questId <= 0)
                continue;

            var cq = GetOrCreateQuest(questId);
            var name = qObj["sName"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) cq.Name = name;
            var desc = qObj["sDesc"]?.ToString();
            if (!string.IsNullOrWhiteSpace(desc)) cq.Description = desc;
            var endText = qObj["sEndText"]?.ToString();
            if (!string.IsNullOrWhiteSpace(endText)) cq.EndText = endText;

            cq.Once = ParseInt(qObj["bOnce"]) == 1 || ParseBool(qObj["bOnce"]);
            cq.Upgrade = ParseInt(qObj["bUpg"]) == 1 || ParseBool(qObj["bUpg"]);
            cq.Level = ParseInt(qObj["iLvl"], cq.Level);
            cq.Slot = ParseInt(qObj["iSlot"], cq.Slot);
            cq.Value = ParseInt(qObj["iValue"], cq.Value);
            cq.Gold = ParseInt(qObj["iGold"], cq.Gold);
            cq.XP = ParseInt(qObj["iExp"], cq.XP);
            cq.RequiredClassID = ParseInt(qObj["iClass"], cq.RequiredClassID);
            cq.RequiredClassPoints = ParseInt(qObj["iReqCP"], cq.RequiredClassPoints);
            cq.RequiredFactionId = ParseInt(qObj["FactionID"], cq.RequiredFactionId);
            cq.RequiredFactionRep = ParseInt(qObj["iReqRep"], cq.RequiredFactionRep);
            var field = qObj["sField"]?.ToString();
            if (!string.IsNullOrWhiteSpace(field)) cq.Field = field;
            cq.Index = ParseInt(qObj["iIndex"], cq.Index);

            // Parse turnin requirements if present
            if (qObj["turnin"] is JArray turninArr)
            {
                var reqs = new List<CapturedItemRequirement>();
                foreach (var rObj in turninArr.OfType<JObject>())
                {
                    var itemId = ParseInt(rObj["ItemID"]);
                    var qty = Math.Max(1, ParseInt(rObj["iQty"], 1));
                    if (itemId > 0)
                    {
                        var itemName = string.Empty;
                        if (qObj["oItems"] is JObject oItems && oItems[itemId.ToString()] is JObject itemDef)
                        {
                            var upserted = UpsertItemFromJson(itemDef, itemId.ToString());
                            itemName = upserted?.Name ?? string.Empty;
                        }
                        reqs.Add(new CapturedItemRequirement
                        {
                            ItemID = itemId,
                            ItemName = itemName,
                            Quantity = qty
                        });
                    }
                }
                if (reqs.Count > 0)
                    cq.Requirements = reqs;
            }

            var rewards = ParseQuestRewards(questId, qObj);
            if (rewards.Count > 0)
                cq.Rewards = rewards;

            cq.LastSeenUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Reads quest rewards from a getQuests entry: "reward" holds one row per reward item
    /// ({ ItemID, iQty, iType, iRate }); "oRewards" groups the item definitions (e.g. "itemsS": { "0": {...} }).
    /// Every reward item is also cached for drop attribution.
    /// </summary>
    private List<CapturedItemRequirement> ParseQuestRewards(int questId, JObject qObj)
    {
        var defsById = new Dictionary<int, CapturedItem>();
        if (qObj["oRewards"] is JObject oRewards)
        {
            foreach (var group in oRewards.Properties().Select(p => p.Value).OfType<JObject>())
            {
                foreach (var itemProp in group.Properties())
                {
                    if (itemProp.Value is not JObject itemDef)
                        continue;
                    var upserted = UpsertItemFromJson(itemDef, null);
                    if (upserted != null)
                        defsById[upserted.ID] = upserted;
                }
            }
        }

        IEnumerable<JObject> rows = qObj["reward"] switch
        {
            JArray arr => arr.OfType<JObject>(),
            JObject single when single["ItemID"] != null => new[] { single },
            JObject keyed => keyed.Properties().Select(p => p.Value).OfType<JObject>(),
            _ => Enumerable.Empty<JObject>()
        };

        var rewards = new List<CapturedItemRequirement>();
        foreach (var row in rows)
        {
            var itemId = ParseInt(row["ItemID"]);
            if (itemId <= 0 || rewards.Any(r => r.ItemID == itemId))
                continue;
            defsById.TryGetValue(itemId, out var def);
            rewards.Add(new CapturedItemRequirement
            {
                ItemID = itemId,
                ItemName = def?.Name ?? string.Empty,
                Quantity = Math.Max(1, ParseInt(row["iQty"], 1)),
                Temp = def?.Temp ?? false,
                RewardType = row["iType"] != null ? ParseInt(row["iType"]) : null,
                RatePercent = row["iRate"] != null ? ParseDoubleSafe(row["iRate"], 100) : null
            });
            CacheQuestRewardItem(questId, itemId);
        }

        // Reward definitions without a matching "reward" row still identify reward items for attribution.
        foreach (var id in defsById.Keys)
            CacheQuestRewardItem(questId, id);

        return rewards;
    }

    private void HandleQuestCompleteResponse(JObject dataObj)
    {
        var questId = ParseInt(dataObj["QuestID"]);
        if (questId <= 0)
            return;

        var success = ParseInt(dataObj["bSuccess"], 1) != 0 && ParseBool(dataObj["bSuccess"], true);
        if (success)
        {
            MarkQuestTurnIn(questId);
            _activeQuestIds.Remove(questId); // a completed quest leaves the active list (re-accepts send acceptQuest again)
            var cq = GetOrCreateQuest(questId);
            cq.TurnInCount++;
            if (!string.IsNullOrWhiteSpace(_currentMapName))
                cq.MapsTurnedInOn.Add(_currentMapName);
            cq.LastSeenUtc = DateTime.UtcNow;

            // Verified "rewardObj" keys: { typ "q", intExp, intGold, iRep, FactionID, iCP, intCoins } (base values).
            if (dataObj["rewardObj"] is JObject reward)
            {
                var r = cq.ObservedReward ??= new CapturedQuestReward();
                r.Samples++;
                r.Exp = ParseInt(reward["intExp"]);
                r.Gold = ParseInt(reward["intGold"]);
                r.Rep = ParseInt(reward["iRep"]);
                r.FactionID = ParseInt(reward["FactionID"]);
                r.ClassPoints = ParseInt(reward["iCP"]);
                r.Coins = ParseInt(reward["intCoins"]);
                r.LastSeenUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// "addFaction" (verified keys): { faction: { FactionID, sName, iRank, iRep, iSpillRep, iRepToRank, CharFactionID } }.
    /// Only the ID → name pair is kept; the player's rank and reputation are never stored.
    /// </summary>
    private void HandleAddFactionPacket(JObject dataObj)
    {
        if (dataObj["faction"] is JObject faction)
            RecordFactionInternal(ParseInt(faction["FactionID"]), faction["sName"]?.ToString());
    }

    /// <summary>
    /// "levelUp" (keys verified from the live skeleton): { intLevel, intExpToLevel, xp }. intExpToLevel is the XP needed
    /// for the next level (sent as a string at the cap); only level -> intExpToLevel is kept, never the player's xp.
    /// </summary>
    private void HandleLevelUpPacket(JObject dataObj)
    {
        var level = ParseInt(dataObj["intLevel"]);
        var toNext = ParseInt(dataObj["intExpToLevel"]);
        if (level > 0 && toNext > 0)
            _delta.XpToNextLevel[level] = toNext;
    }

    /// <summary>Records faction ID → name pairs from the client's faction list (no reputation values).</summary>
    public void RecordFactionNames(IEnumerable<(int Id, string? Name)> factions)
    {
        lock (_sync)
        {
            foreach (var (id, name) in factions)
                RecordFactionInternal(id, name);
        }
    }

    private void RecordFactionInternal(int id, string? name)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(name))
            return;
        if (!_delta.Factions.TryGetValue(id, out var f))
            _delta.Factions[id] = f = new CapturedFaction { ID = id };
        f.Name = name.Trim();
        f.LastSeenUtc = DateTime.UtcNow;
    }

    private void RegisterMonsterSpawn(
        string normMap,
        int monMapId,
        int monId,
        string name,
        string cell,
        int level,
        int maxHp,
        string race,
        string element,
        string fileName,
        int currentHp,
        int weaponDps = 0)
    {
        var map = GetOrCreateMap(normMap);
        if (!string.IsNullOrWhiteSpace(cell) && !map.Cells.ContainsKey(cell))
            map.Cells[cell] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (monMapId > 0)
        {
            if (!map.Monsters.TryGetValue(monMapId, out var spawn))
            {
                spawn = new CapturedMapMonsterSpawn { MapID = monMapId };
                map.Monsters[monMapId] = spawn;
            }
            if (monId > 0) spawn.MonsterID = monId;
            if (!string.IsNullOrWhiteSpace(name)) spawn.Name = name;
            if (!string.IsNullOrWhiteSpace(cell)) spawn.Cell = cell;
            if (level > 0) spawn.Level = level;
            if (weaponDps > 0) spawn.WeaponDps = weaponDps;
            if (maxHp > 0) spawn.MaxHP = maxHp;
            if (!string.IsNullOrWhiteSpace(race)) spawn.Race = race;
            if (!string.IsNullOrWhiteSpace(element)) spawn.Element = element;
            if (!string.IsNullOrWhiteSpace(fileName)) spawn.FileName = fileName;

            if (!_mapMonsters.TryGetValue(monMapId, out var active))
            {
                active = new ActiveMonsterState { MapID = monMapId };
                _mapMonsters[monMapId] = active;
            }
            if (monId > 0) active.MonsterID = monId;
            if (!string.IsNullOrWhiteSpace(name)) active.Name = name;
            if (!string.IsNullOrWhiteSpace(cell)) active.Cell = cell;
            if (maxHp > 0) active.MaxHP = maxHp;
            active.CurrentHP = currentHp > 0 ? currentHp : maxHp;
        }

        var mon = GetOrCreateMonster(monId, name);
        if (level > 0) mon.Level = level;
        if (weaponDps > 0) mon.WeaponDps = weaponDps;
        if (maxHp > 0) mon.MaxHP = Math.Max(mon.MaxHP, maxHp);
        if (!string.IsNullOrWhiteSpace(race) && race != "None") mon.Race = race;
        if (!string.IsNullOrWhiteSpace(element) && element != "None") mon.Element = element;
        if (!string.IsNullOrWhiteSpace(fileName)) mon.FileName = fileName;

        if (!mon.MapsSeen.TryGetValue(normMap, out var cells))
        {
            cells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            mon.MapsSeen[normMap] = cells;
        }
        if (!string.IsNullOrWhiteSpace(cell))
            cells.Add(cell);
    }

    private void RegisterMonsterKillInternal(int monMapId)
    {
        var now = DateTime.UtcNow;
        if (_mapMonsters.TryGetValue(monMapId, out var active))
        {
            // Deduplicate if both "ct.m" and MonsterKilledMessage report the same death within 400ms
            if ((now - active.LastDeathCountedUtc).TotalMilliseconds < 400)
                return;

            active.LastDeathCountedUtc = now;
            active.PendingRespawnSinceUtc = now;
            active.CurrentHP = 0;

            var mon = GetOrCreateMonster(active.MonsterID, active.Name);
            CountKill(mon);

            _lastKilledMonsterKey = mon.Key;
            _lastKilledMonsterId = mon.ID;
            _lastKilledMonsterName = mon.Name;
            _lastKillTimeUtc = now;
            SessionKillsRecorded++;
            return;
        }

        // Fallback if monMapId wasn't in _mapMonsters: attribute to sole monster in current cell
        var cellMonsters = GetMonstersInCell(_currentCell);
        if (cellMonsters.Count == 1)
        {
            var mon = cellMonsters[0];
            CountKill(mon);
            _lastKilledMonsterKey = mon.Key;
            _lastKilledMonsterId = mon.ID;
            _lastKilledMonsterName = mon.Name;
            _lastKillTimeUtc = now;
            SessionKillsRecorded++;
        }
    }

    /// <summary>
    /// Counts a kill. When the active quest list is known, also counts it as a tracked kill under the exact active
    /// quest set: the denominator of quest-gated drop rates (those items can only drop while one of their quests is
    /// active). Keying by the whole set makes each kill count once even when several required quests are active.
    /// </summary>
    private void CountKill(CapturedMonster mon)
    {
        mon.KillCount++;
        if (_activeQuestsKnown)
        {
            mon.TrackedKills++;
            var setKey = DropRates.QuestSetKey(_activeQuestIds);
            mon.KillsByActiveQuestSet[setKey] = mon.KillsByActiveQuestSet.GetValueOrDefault(setKey) + 1;
        }
        DropRates.Recompute(mon);
    }

    /// <summary>
    /// Replaces the known active quest list (from the client's Bot.Quests.Active). Until the first sync the list is
    /// unknown and no quest-gated statistics are recorded, so partial knowledge never biases the rates.
    /// </summary>
    public void SyncActiveQuests(IEnumerable<int> activeQuestIds)
    {
        lock (_sync)
        {
            _activeQuestIds.Clear();
            foreach (var id in activeQuestIds)
                if (id > 0)
                    _activeQuestIds.Add(id);
            _activeQuestsKnown = true;
        }
    }

    public bool ActiveQuestsKnown
    {
        get { lock (_sync) return _activeQuestsKnown; }
    }

    /// <summary>
    /// Records how many players are in the current room of a map (Skua Map.PlayerCount). Only the peak is kept: a lower
    /// bound of the room cap, which the client does not expose. A count, never names.
    /// </summary>
    public void RecordPlayerCount(string? mapName, int playerCount)
    {
        var normMap = GameDatabase.NormalizeMapName(mapName);
        if (string.IsNullOrEmpty(normMap) || playerCount <= 0)
            return;
        lock (_sync)
        {
            var map = GetOrCreateMap(normMap);
            map.PlayerCountSamples++;
            if (playerCount > map.PeakPlayersObserved)
            {
                map.PeakPlayersObserved = playerCount;
                map.PeakPlayersObservedUtc = DateTime.UtcNow;
            }
        }
    }

    // Respawn samples outside this window are discarded (missed packets, map changes, stale deaths).
    private const long MinRespawnSampleMs = 500;
    private const long MaxRespawnSampleMs = 10 * 60 * 1000;

    public long SessionRespawnSamples { get; private set; }

    /// <summary>
    /// String packet ["respawnMon", roomId, MonMapID] (verified from live samples, e.g. ["respawnMon", "-1", "13"]).
    /// The delay since the recorded kill of that MonMapID is a respawn sample for the spawn and for the monster.
    /// </summary>
    private void HandleRespawnMon(JArray parts, DateTime nowUtc)
    {
        if (parts.Count < 3 || !int.TryParse(parts[2]?.ToString(), out var monMapId) ||
            !_mapMonsters.TryGetValue(monMapId, out var active))
            return;

        var since = active.PendingRespawnSinceUtc;
        active.PendingRespawnSinceUtc = DateTime.MinValue;
        if (active.MaxHP > 0)
            active.CurrentHP = active.MaxHP;
        if (since == DateTime.MinValue || string.IsNullOrEmpty(_currentMapName))
            return;

        var ms = (long)(nowUtc - since).TotalMilliseconds;
        if (ms < MinRespawnSampleMs || ms > MaxRespawnSampleMs)
            return;

        var map = GetOrCreateMap(_currentMapName);
        if (!map.Monsters.TryGetValue(monMapId, out var spawn))
        {
            spawn = new CapturedMapMonsterSpawn { MapID = monMapId, MonsterID = active.MonsterID, Name = active.Name, Cell = active.Cell };
            map.Monsters[monMapId] = spawn;
        }
        spawn.Respawn.Add(ms);
        GetOrCreateMonster(active.MonsterID, active.Name).Respawn.Add(ms);
        SessionRespawnSamples++;
    }

    public long SessionKillRewardSamples { get; private set; }

    /// <summary>
    /// "addGoldExp" (verified keys): { typ "m" (monster) | "q" (quest), id = MonMapID for typ "m", intExp, bonusExp,
    /// intGold, bonusGold, iRep, bonusRep, FactionID }. Totals include the bonus, so base = total - bonus. Only monster
    /// kills are recorded here; quest rewards come from getQuests.
    /// </summary>
    private void HandleAddGoldExpPacket(JObject dataObj)
    {
        if (!string.Equals(dataObj["typ"]?.ToString(), "m", StringComparison.OrdinalIgnoreCase))
            return;
        var monMapId = ParseInt(dataObj["id"]);
        if (monMapId <= 0 || !_mapMonsters.TryGetValue(monMapId, out var active))
            return;

        var mon = GetOrCreateMonster(active.MonsterID, active.Name);
        mon.KillRewards.Add(
            ParseInt(dataObj["intExp"]), ParseInt(dataObj["bonusExp"]),
            ParseInt(dataObj["intGold"]), ParseInt(dataObj["bonusGold"]),
            ParseInt(dataObj["iRep"]), ParseInt(dataObj["bonusRep"]),
            ParseInt(dataObj["FactionID"]));
        // Verified 2026-11-21: { ..., "iCP": 305, "bonusCP": 0 } on kills while the class is below rank 10.
        mon.KillRewards.AddClassPoints(ParseInt(dataObj["iCP"]), ParseInt(dataObj["bonusCP"]));
        SessionKillRewardSamples++;
    }

    private void MarkQuestTurnIn(int questId)
    {
        _lastTurnInQuestId = questId;
        _lastTurnInUtc = DateTime.UtcNow;
    }

    private void CacheQuestRewardItem(int questId, int itemId)
    {
        if (questId <= 0 || itemId <= 0)
            return;
        if (!_sessionQuestRewardItems.TryGetValue(questId, out var set))
            _sessionQuestRewardItems[questId] = set = new HashSet<int>();
        set.Add(itemId);
    }

    /// <summary>
    /// True when an item that just arrived belongs to a quest turn-in rather than to a monster kill: a turn-in started
    /// within <see cref="QuestRewardWindowSeconds"/> and the item is one of that quest's known rewards, or the quest's
    /// rewards are unknown this session (then the item is ambiguous and is skipped rather than risk polluting drop rates).
    /// </summary>
    private bool IsLikelyQuestReward(int itemId, DateTime now)
    {
        if (_lastTurnInQuestId <= 0 || (now - _lastTurnInUtc).TotalSeconds > QuestRewardWindowSeconds)
            return false;
        return !_sessionQuestRewardItems.TryGetValue(_lastTurnInQuestId, out var rewards) || rewards.Contains(itemId);
    }

    private void AttributeDropToMonster(int itemId, string itemName, bool isTemp, int quantity)
    {
        CapturedMonster? targetMonster = null;
        var now = DateTime.UtcNow;

        if (IsLikelyQuestReward(itemId, now))
        {
            SessionQuestRewardsExcluded++;
            return;
        }

        // Only credit the monster that was just killed. There is deliberately no "first monster in the cell" fallback:
        // items arriving without a recent kill come from quests, merges, map items or shops, never from that monster.
        if (!string.IsNullOrEmpty(_lastKilledMonsterKey) &&
            (now - _lastKillTimeUtc).TotalSeconds <= DropAttributionWindowSeconds)
        {
            // The delta may have been flushed since the kill; recreate the entry instead of losing the drop.
            targetMonster = _delta.Monsters.TryGetValue(_lastKilledMonsterKey, out var killedMon)
                ? killedMon
                : GetOrCreateMonster(_lastKilledMonsterId, _lastKilledMonsterName);
        }

        if (targetMonster == null)
        {
            SessionUnattributedDropsSkipped++;
            return;
        }

        if (!targetMonster.Drops.TryGetValue(itemId, out var dropStat))
        {
            dropStat = new CapturedDropStat
            {
                ItemID = itemId,
                ItemName = itemName,
                Temp = isTemp
            };
            targetMonster.Drops[itemId] = dropStat;
        }

        if (!string.IsNullOrWhiteSpace(itemName))
            dropStat.ItemName = itemName;
        dropStat.Temp |= isTemp;
        dropStat.DropEvents++;
        if (_activeQuestsKnown)
            dropStat.TrackedDropEvents++;
        dropStat.TotalQuantity += Math.Max(1, quantity);
        if (_delta.Items.TryGetValue(itemId, out var itemInfo) && itemInfo.RequiredByQuestIds.Count > 0)
        {
            dropStat.QuestGated = true;
            dropStat.RequiredQuestIds.UnionWith(itemInfo.RequiredByQuestIds);
        }
        DropRates.Recompute(targetMonster);
        SessionDropsRecorded++;
    }

    private void RecordCellVisit(string normMap, string? cell, string? pad)
    {
        if (!string.IsNullOrWhiteSpace(cell))
            _currentCell = cell;
        if (!string.IsNullOrWhiteSpace(pad))
            _currentPad = pad;

        if (string.IsNullOrEmpty(normMap) || string.IsNullOrWhiteSpace(cell))
            return;

        var map = GetOrCreateMap(normMap);
        if (!map.Cells.TryGetValue(cell, out var pads))
        {
            pads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map.Cells[cell] = pads;
        }
        if (!string.IsNullOrWhiteSpace(pad))
            pads.Add(pad);
    }

    private void RecordMapItemRequest(string normMap, string cell, int mapItemId)
    {
        if (string.IsNullOrEmpty(normMap) || mapItemId <= 0)
            return;

        var map = GetOrCreateMap(normMap);
        if (!map.MapItems.TryGetValue(mapItemId, out var mapItem))
        {
            mapItem = new CapturedMapItem { MapItemID = mapItemId };
            map.MapItems[mapItemId] = mapItem;
        }

        mapItem.TimesRequested++;
        if (!string.IsNullOrWhiteSpace(cell))
            mapItem.CellsSeen.Add(cell);
        mapItem.LastSeenUtc = DateTime.UtcNow;

        _lastRequestedMapItemId = mapItemId;
        _lastRequestedMapItemMap = normMap;
        _lastRequestedMapItemTimeUtc = DateTime.UtcNow;
    }

    private void RecordQuestAccepted(int questId, string normMap)
    {
        if (_activeQuestsKnown)
            _activeQuestIds.Add(questId);
        var cq = GetOrCreateQuest(questId);
        cq.AcceptedCount++;
        if (!string.IsNullOrEmpty(normMap))
            cq.MapsAcceptedOn.Add(normMap);
        cq.LastSeenUtc = DateTime.UtcNow;
    }

    private CapturedItem? UpsertItemFromJson(JObject itemObj, string? fallbackIdKey)
    {
        var itemId = ParseInt(itemObj["ItemID"], ParseInt(fallbackIdKey));
        if (itemId <= 0)
            return null;

        if (!_delta.Items.TryGetValue(itemId, out var item))
        {
            item = new CapturedItem { ID = itemId };
            _delta.Items[itemId] = item;
        }

        var name = itemObj["sName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(name)) item.Name = name.Trim();
        var desc = itemObj["sDesc"]?.ToString();
        if (!string.IsNullOrWhiteSpace(desc)) item.Description = desc;
        var cat = itemObj["sType"]?.ToString();
        if (!string.IsNullOrWhiteSpace(cat)) item.Category = cat;
        var stk = ParseInt(itemObj["iStk"]);
        if (stk > 0) item.MaxStack = Math.Max(item.MaxStack, stk);

        if (itemObj["bTemp"] != null)
            item.Temp = ParseInt(itemObj["bTemp"]) == 1 || ParseBool(itemObj["bTemp"]);
        if (itemObj["bCoins"] != null)
            item.Coins = ParseInt(itemObj["bCoins"]) == 1 || ParseBool(itemObj["bCoins"]);
        if (itemObj["bUpg"] != null)
            item.Upgrade = ParseInt(itemObj["bUpg"]) == 1 || ParseBool(itemObj["bUpg"]);
        if (itemObj["bHouse"] != null)
            item.House = ParseInt(itemObj["bHouse"]) == 1 || ParseBool(itemObj["bHouse"]);

        var group = itemObj["sES"]?.ToString();
        if (!string.IsNullOrWhiteSpace(group)) item.ItemGroup = group;
        var file = itemObj["sFile"]?.ToString();
        if (!string.IsNullOrWhiteSpace(file)) item.FileName = file;
        var link = itemObj["sLink"]?.ToString();
        if (!string.IsNullOrWhiteSpace(link)) item.FileLink = link;
        var meta = itemObj["sMeta"]?.ToString();
        if (!string.IsNullOrWhiteSpace(meta)) item.Meta = meta;
        var enh = ParseInt(itemObj["EnhPatternID"]);
        if (enh > 0) item.EnhancementPatternID = enh;

        // "sReqQuests": comma-separated IDs of the quests that use this item (a quest-gated item when non-empty).
        foreach (var part in (itemObj["sReqQuests"]?.ToString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var reqQuestId) && reqQuestId > 0)
                item.RequiredByQuestIds.Add(reqQuestId);

        item.LastSeenUtc = DateTime.UtcNow;
        return item;
    }

    private CapturedItem UpsertItemFromModel(ItemBase model)
    {
        if (!_delta.Items.TryGetValue(model.ID, out var item))
        {
            item = new CapturedItem { ID = model.ID };
            _delta.Items[model.ID] = item;
        }

        if (!string.IsNullOrWhiteSpace(model.Name)) item.Name = model.Name.Trim();
        if (!string.IsNullOrWhiteSpace(model.Description)) item.Description = model.Description;
        if (!string.IsNullOrWhiteSpace(model.CategoryString)) item.Category = model.CategoryString;
        if (model.MaxStack > 0) item.MaxStack = Math.Max(item.MaxStack, model.MaxStack);
        item.Temp = model.Temp;
        item.Coins = model.Coins;
        item.Upgrade = model.Upgrade;
        item.House = model.House;
        if (!string.IsNullOrWhiteSpace(model.ItemGroup)) item.ItemGroup = model.ItemGroup;
        if (!string.IsNullOrWhiteSpace(model.FileName)) item.FileName = model.FileName;
        if (!string.IsNullOrWhiteSpace(model.FileLink)) item.FileLink = model.FileLink;
        if (!string.IsNullOrWhiteSpace(model.Meta)) item.Meta = model.Meta;
        if (model.EnhancementPatternID > 0) item.EnhancementPatternID = model.EnhancementPatternID;
        item.LastSeenUtc = DateTime.UtcNow;
        return item;
    }

    private CapturedMonster? ResolveMonsterFromEntityRef(string? entRef)
    {
        if (string.IsNullOrWhiteSpace(entRef) || !entRef.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
            return null;
        return int.TryParse(entRef.AsSpan(2), out var monMapId)
            ? ResolveMonsterByMapId(monMapId)
            : null;
    }

    private CapturedMonster? ResolveMonsterByMapId(int monMapId)
    {
        if (_mapMonsters.TryGetValue(monMapId, out var active))
            return GetOrCreateMonster(active.MonsterID, active.Name);

        if (_delta.Maps.TryGetValue(_currentMapName, out var map) &&
            map.Monsters.TryGetValue(monMapId, out var spawn))
        {
            return GetOrCreateMonster(spawn.MonsterID, spawn.Name);
        }

        return null;
    }

    private List<CapturedMonster> GetMonstersInCell(string? cell)
    {
        var result = new List<CapturedMonster>();
        if (string.IsNullOrWhiteSpace(cell))
            return result;

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var active in _mapMonsters.Values)
        {
            if (string.Equals(active.Cell, cell, StringComparison.OrdinalIgnoreCase))
            {
                var mon = GetOrCreateMonster(active.MonsterID, active.Name);
                if (seenKeys.Add(mon.Key))
                    result.Add(mon);
            }
        }
        return result;
    }

    private CapturedMap GetOrCreateMap(string normMap)
    {
        if (!_delta.Maps.TryGetValue(normMap, out var map))
        {
            map = new CapturedMap { Name = normMap };
            _delta.Maps[normMap] = map;
        }
        map.LastSeenUtc = DateTime.UtcNow;
        return map;
    }

    private CapturedMonster GetOrCreateMonster(int monId, string? name)
    {
        var key = GameDatabase.GetMonsterKey(monId, name);
        if (!_delta.Monsters.TryGetValue(key, out var mon))
        {
            mon = new CapturedMonster
            {
                Key = key,
                ID = monId,
                Name = name?.Trim() ?? string.Empty
            };
            _delta.Monsters[key] = mon;
        }
        else
        {
            if (monId > 0) mon.ID = monId;
            if (!string.IsNullOrWhiteSpace(name)) mon.Name = name.Trim();
        }
        mon.LastSeenUtc = DateTime.UtcNow;
        return mon;
    }

    private CapturedShop GetOrCreateShop(int shopId)
    {
        if (!_delta.Shops.TryGetValue(shopId, out var shop))
        {
            shop = new CapturedShop { ID = shopId };
            _delta.Shops[shopId] = shop;
        }
        return shop;
    }

    private CapturedQuest GetOrCreateQuest(int questId)
    {
        if (!_delta.Quests.TryGetValue(questId, out var quest))
        {
            quest = new CapturedQuest { ID = questId };
            _delta.Quests[questId] = quest;
        }
        return quest;
    }

    private static int ParseInt(JToken? token, int fallback = 0)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Integer) return token.Value<int>();
        return int.TryParse(token.ToString(), out var v) ? v : fallback;
    }

    private static int ParseInt(string? str, int fallback = 0)
    {
        return !string.IsNullOrWhiteSpace(str) && int.TryParse(str, out var v) ? v : fallback;
    }

    private static bool ParseBool(JToken? token, bool fallback = false)
    {
        if (token == null) return fallback;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        if (token.Type == JTokenType.Integer) return token.Value<int>() != 0;
        var s = token.ToString();
        if (bool.TryParse(s, out var b)) return b;
        if (int.TryParse(s, out var i)) return i != 0;
        return fallback;
    }
}
