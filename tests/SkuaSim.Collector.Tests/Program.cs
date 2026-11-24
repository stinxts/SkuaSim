using System.Reflection;
using Skua.Core.Interfaces;
using SkuaSim.Collector;
using SkuaSim.Collector.Models;
using SkuaSim.Collector.Parsing;
using SkuaSim.Collector.Storage;

var asmDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeSkua", "current", "Assemblies");
AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var name = new AssemblyName(e.Name).Name + ".dll";
    var p = Path.Combine(asmDir, name);
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception($"Assertion failed: {message}");
}

Console.WriteLine("1. Verifying plugin discovery (ISkuaPlugin)...");
var pluginAsm = typeof(SkuaSimCollectorPlugin).Assembly;
var pluginTypes = pluginAsm.DefinedTypes.Where(t => typeof(ISkuaPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract).ToList();
Assert(pluginTypes.Count == 1, $"Expected 1 ISkuaPlugin in assembly, found {pluginTypes.Count}");
var pluginInstance = (ISkuaPlugin)Activator.CreateInstance(pluginTypes[0])!;
Assert(pluginInstance.Name == "SkuaSim Collector", "Plugin name mismatch");
Assert(pluginInstance.Options!.Count == 5, "Expected 5 plugin options");
Console.WriteLine($"   OK: {pluginInstance.Name} ({pluginInstance.Options.Count} options)");

Console.WriteLine("2. Testing packet capture (moveToArea, ct, dropItem, getMapItem, loadShop, getQuests, ccqr)...");
var engine1 = new PacketCollectorEngine();

// Simulate joining ultraezrajal-1234
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "moveToArea",
      "strMapName": "ultraezrajal-1234",
      "strMapFileName": "Map-UltraEzrajal.swf",
      "mondef": [
        {
          "MonID": 4901,
          "strMonName": "Ultra Ezrajal",
          "intLevel": 100,
          "intHP": 15000000,
          "intHPMax": 15000000,
          "strRace": "Chaos",
          "strElement": "Light",
          "strMonFileName": "UltraEzrajal.swf"
        }
      ],
      "monmap": [
        {
          "MonMapID": 1,
          "strFrame": "r2"
        }
      ],
      "monbranch": [
        {
          "MonMapID": 1,
          "MonID": 4901,
          "intHP": 15000000,
          "intHPMax": 15000000
        }
      ]
    }
  }
}
""");

engine1.ProcessClientPacket("%xt%zm%moveToCell%100%r2%Left%");

// Simulate combat tick 1: monster hits player for 4200, crits for 6100, prepares counter attack
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "ct",
      "a": [
        { "cInf": "m:1", "tInf": "p:999", "hp": 4200, "type": "hit" },
        { "cInf": "m:1", "tInf": "p:998", "hp": 6100, "type": "crit" },
        {
          "cInf": "m:1",
          "tInf": "m:1",
          "aura": { "nam": "Counter Attack", "dur": 10, "cat": "buff", "val": 1.0 }
        }
      ],
      "anims": [
        { "cInf": "m:1", "msg": "Ultra Ezrajal prepares a counter attack!" }
      ],
      "m": {
        "1": { "intHP": 5000000 }
      }
    }
  }
}
""");

// Simulate combat tick 2: monster dies (intHP = 0)
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "ct",
      "m": {
        "1": { "intHP": 0 }
      }
    }
  }
}
""");

// Simulate dropItem right after kill
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "dropItem",
      "items": {
        "61890": {
          "ItemID": 61890,
          "sName": "Ezrajal Insignia",
          "sDesc": "Insignia from Ultra Ezrajal.",
          "sType": "Resource",
          "iStk": 100,
          "bTemp": 0,
          "bCoins": 1,
          "bUpg": 0,
          "iQty": 5
        }
      }
    }
  }
}
""");

// Simulate getMapItem + addItems
engine1.ProcessClientPacket("%xt%zm%getMapItem%100%55001%");
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "addItems",
      "items": {
        "77001": {
          "ItemID": 77001,
          "sName": "Ancient Clue",
          "sType": "Quest Item",
          "iStk": 1,
          "bTemp": 1,
          "iQty": 1
        }
      }
    }
  }
}
""");

// Simulate loadShop
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "loadShop",
      "shopinfo": {
        "ShopID": 2031,
        "strName": "Ultra Ezrajal Merge",
        "items": [
          {
            "ShopItemID": 9001,
            "ItemID": 61899,
            "sName": "Empowered Blade",
            "sType": "Sword",
            "iCost": 0,
            "bCoins": 1,
            "iStk": 1,
            "turnin": [
              { "ItemID": 61890, "sName": "Ezrajal Insignia", "iQty": 25 }
            ]
          }
        ]
      }
    }
  }
}
""");

// Simulate getQuests + acceptQuest + ccqr
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "getQuests",
      "quests": {
        "8152": {
          "QuestID": 8152,
          "sName": "Ultra Ezrajal",
          "sDesc": "Defeat Ultra Ezrajal.",
          "bOnce": 0,
          "iLvl": 80,
          "iGold": 250000,
          "iExp": 100000,
          "turnin": [
            { "ItemID": 61891, "iQty": 1 }
          ]
        }
      }
    }
  }
}
""");
engine1.ProcessClientPacket("%xt%zm%acceptQuest%100%8152%");
engine1.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "ccqr",
      "QuestID": 8152,
      "bSuccess": 1
    }
  }
}
""");

// Simulate a second client (Army Client 2) killing Ultra Ezrajal again without dropping Insignia
var engine2 = new PacketCollectorEngine();
engine2.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "moveToArea",
      "strMapName": "ultraezrajal-1234",
      "mondef": [ { "MonID": 4901, "strMonName": "Ultra Ezrajal", "intLevel": 100, "intHPMax": 15000000 } ],
      "monmap": [ { "MonMapID": 1, "strFrame": "r2" } ],
      "monbranch": [ { "MonMapID": 1, "MonID": 4901, "intHP": 15000000, "intHPMax": 15000000 } ]
    }
  }
}
""");
engine2.ProcessExtensionPacketJson("""
{
  "params": {
    "type": "json",
    "dataObj": {
      "cmd": "ct",
      "a": [ { "cInf": "m:1", "tInf": "p:777", "hp": 3500, "type": "hit" } ],
      "m": { "1": { "intHP": 0 } }
    }
  }
}
""");

Console.WriteLine("3. Testing persistence and multi-client merge in GameDatabaseStore...");
var tempDir = Path.Combine(Path.GetTempPath(), $"skuasim_test_{Guid.NewGuid():N}");
var primaryPath = Path.Combine(tempDir, "gamedata.json");
var mirrorPath = Path.Combine(tempDir, "mirror", "gamedata.json");

try
{
    var store = new GameDatabaseStore(primaryPath, new[] { mirrorPath });
    store.FlushDelta(engine1.TakeDeltaSnapshot());
    var merged = store.FlushDelta(engine2.TakeDeltaSnapshot());

    Assert(File.Exists(primaryPath), "Primary gamedata.json not created");
    Assert(File.Exists(mirrorPath), "Mirror gamedata.json not created");
    Assert(merged.TotalFlushesMerged == 2, $"Expected 2 flushes, got {merged.TotalFlushesMerged}");

    var map = merged.Maps["ultraezrajal"];
    Assert(map.Visits == 2, $"Expected 2 map visits, got {map.Visits}");
    Assert(map.Cells.ContainsKey("r2") && map.Cells["r2"].Contains("Left"), "Expected cell r2 with pad Left");
    Assert(map.MapItems[55001].GrantedItemIDs.Contains(77001), "Expected map item 55001 -> item 77001");

    var mon = merged.Monsters["id:4901"];
    Assert(mon.Name == "Ultra Ezrajal", "Monster name mismatch");
    Assert(mon.KillCount == 2, $"Expected 2 kills across both clients, got {mon.KillCount}");
    Assert(mon.Combat.Hits == 2 && mon.Combat.Crits == 1, "Combat hit/crit count mismatch");
    Assert(mon.Combat.MinDamage == 3500 && mon.Combat.MaxDamage == 6100, "Combat min/max damage mismatch");
    Assert(mon.Combat.CounterAttacksObserved >= 1, "Counter-attack not recorded");
    Assert(mon.Auras.ContainsKey("Counter Attack"), "Aura Counter Attack not recorded");
    Assert(mon.Drops[61890].DropEvents == 1 && mon.Drops[61890].TotalQuantity == 5, "Drop count mismatch");
    Assert(mon.Drops[61890].EstimatedDropChance is { } chance61890 && Math.Abs(chance61890 - 0.5) < 0.001, $"Expected 0.5 drop chance (1/2 kills), got {mon.Drops[61890].EstimatedDropChance}");

    Assert(merged.Shops.ContainsKey(2031) && merged.Shops[2031].Items.Count == 1, "Shop 2031 not captured");
    Assert(merged.Quests.ContainsKey(8152) && merged.Quests[8152].AcceptedCount == 1 && merged.Quests[8152].TurnInCount == 1, "Quest 8152 stats mismatch");

    Assert(merged.CollectorVersions.TryGetValue(CollectorInfo.Version, out var flushesByBuild) && flushesByBuild == 2,
        $"Expected 2 flushes recorded for collector {CollectorInfo.Version}");
    Assert(merged.ContributorId == null && merged.ExportedUtc == null, "The collector's own file must not carry a contributor ID");

    // Export for sharing: day-only dates, no migration log, a stable random contributor ID; read back and merge.
    var zipPath = store.ExportForSharing(Path.Combine(tempDir, "exports"));
    Assert(File.Exists(zipPath), "Exported zip not created");
    var shared = GameDatabaseStore.ReadSharedFile(zipPath);
    Assert(Guid.TryParse(shared.ContributorId, out _), "Shared file has no contributor ID");
    Assert(shared.MigrationLog.Count == 0, "Shared file must not carry the migration log");
    Assert(shared.LastUpdatedUtc.TimeOfDay == TimeSpan.Zero && shared.Monsters["id:4901"].LastSeenUtc.TimeOfDay == TimeSpan.Zero,
        "Shared file dates must be cut to the day");
    Assert(shared.Monsters["id:4901"].KillCount == 2, "Shared file lost data");
    var zip2 = store.ExportForSharing(Path.Combine(tempDir, "exports2"));
    Assert(GameDatabaseStore.ReadSharedFile(zip2).ContributorId == shared.ContributorId, "Contributor ID must stay the same per install");
    using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
    {
        var gameJson = new StreamReader(archive.GetEntry("gamedata.json")!.Open()).ReadToEnd();
        Assert(!gameJson.Contains("p:777") && !gameJson.Contains(tempDir.Replace("\\", "\\\\")), "Shared file leaked an entity ref or a local path");
    }
    var combined = new GameDatabase();
    GameDatabaseStore.MergeInto(combined, GameDatabaseStore.ReadSharedFile(primaryPath));
    GameDatabaseStore.MergeInto(combined, shared);
    Assert(combined.Monsters["id:4901"].KillCount == 4 && combined.CollectorVersions[CollectorInfo.Version] == 4,
        "Two testers' files should add up");
    var bogus = Path.Combine(tempDir, "not_gamedata.json");
    File.WriteAllText(bogus, "{\"hello\": 1}");
    var rejected = false;
    try { GameDatabaseStore.ReadSharedFile(bogus); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "A file without collected data must be rejected");

    Console.WriteLine("4. Testing class skills (sAct), stat updates (stu), player combat (ct), enhancements & anonymization...");
    var c1 = new PacketCollectorEngine();
    var c2 = new PacketCollectorEngine();

    c1.SetPlayerCombatContext(
        selfEntityId: 4242,
        className: "Mage",
        level: 10,
        maxHealth: 850,
        weaponEnhPattern: 6, weaponEnhLevel: 10,
        classEnhPattern: 6, classEnhLevel: 10,
        helmEnhPattern: 6, helmEnhLevel: 10,
        capeEnhPattern: 6, capeEnhLevel: 10);

    static string Pext(string dataObj) => "{\"params\":{\"type\":\"json\",\"dataObj\":" + dataObj + "}}";

    c1.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "sAct",
      "actions": {
        "active": [
          { "id": 1, "actID": 1001, "nam": "Auto attack", "damage": 1.0, "cd": 2000, "mp": 0, "typ": "m", "auto": true },
          { "id": 2, "actID": 1002, "nam": "Fireball", "damage": 1.5, "cd": 3000, "mp": 15, "typ": "m", "desc": "Burns target." },
          { "id": 3, "actID": 1003, "nam": "Arcane Mend", "damage": -1.2, "cd": 8000, "mp": 25, "typ": "h", "desc": "Heals caster." }
        ],
        "passive": [
          { "nam": "Runic Mastery", "desc": "Increases INT by 15%." }
        ]
      }
    }
    """));

    c1.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "stu",
      "sta": {
        "$STR": 12, "$INT": 95, "$END": 40, "$DEX": 25, "$WIS": 60, "$LCK": 18,
        "$ap": 48, "$sp": 210, "$tha": 0.14, "$tcr": 0.11, "$scm": 1.65, "$tdo": 0.08
      }
    }
    """));

    // Outgoing player crit, hit, miss; self heal; incoming monster dodge & hit; another player's ignored hit
    c1.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "a": [
        { "cInf": "p:4242", "tInf": "m:1", "type": "crit", "hp": 320 },
        { "cInf": "p:4242", "tInf": "m:1", "type": "hit", "hp": 180 },
        { "cInf": "p:4242", "tInf": "m:1", "type": "miss", "hp": 0 },
        { "cInf": "p:4242", "tInf": "p:4242", "type": "hit", "hp": -190 },
        { "cInf": "m:1", "tInf": "p:4242", "type": "dodge", "hp": 0 },
        { "cInf": "m:1", "tInf": "p:4242", "type": "hit", "hp": 75 },
        { "cInf": "p:99999", "tInf": "m:1", "type": "crit", "hp": 99999 }
      ]
    }
    """));

    // Verify loadShop captures EnhPatternID & ProcID on shop items
    c2.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "loadShop",
      "shopinfo": {
        "ShopID": 141,
        "sName": "Wizard Enhancements",
        "items": [
          { "ShopItemID": 5001, "ItemID": 51001, "sName": "Wizard Weapon Enh Lv 10", "sType": "Enhancement", "iCost": 250, "iLvl": 10, "EnhPatternID": 6, "ProcID": 0, "sMeta": "Weapon" }
        ]
      }
    }
    """));

    c2.SetPlayerCombatContext(selfEntityId: 8888, className: "Mage", level: 10, maxHealth: 850);
    c2.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "a": [
        { "cInf": "p:8888", "tInf": "m:2", "type": "hit", "hp": 100 },
        { "cInf": "m:2", "tInf": "p:8888", "type": "dodge", "hp": 0 }
      ]
    }
    """));

    store.FlushDelta(c1.TakeDeltaSnapshot());
    var finalDb = store.FlushDelta(c2.TakeDeltaSnapshot());

    Assert(finalDb.Classes.TryGetValue("Mage", out var mage), "Mage class profile missing");
    Assert(mage!.Skills.Count == 3, $"Expected 3 captured skills, got {mage.Skills.Count}");
    Assert(mage.Skills[2].IsHealHint, "Expected Arcane Mend to be flagged IsHealHint");
    Assert(mage.Passives.Count == 1, "Expected 1 passive skill");
    Assert(mage.StatsByLevel.TryGetValue(10, out var lv10) && lv10 is { Intellect: 95, SpellPower: 210 }, "Lv 10 Mage stats mismatch");
    Assert(lv10!.EnhancementSignature == "W6:10|C6:10|H6:10|K6:10", $"Unexpected enh signature: {lv10.EnhancementSignature}");

    // Combat sums across c1 + c2 (notice c1 ignored p:99999)
    Assert(mage.Combat.Crits == 1 && mage.Combat.Hits == 2 && mage.Combat.Misses == 1,
        $"Unexpected Mage hits/crits/misses: {mage.Combat.Hits}/{mage.Combat.Crits}/{mage.Combat.Misses}");
    Assert(mage.Combat.TotalDamageDealt == 600, $"Expected 600 total damage (320+180+100), got {mage.Combat.TotalDamageDealt}");
    Assert(mage.Combat.HealEvents == 1 && mage.Combat.TotalHealingDone == 190, "Expected 1 heal for 190");
    Assert(mage.Combat.AttacksTaken == 3 && mage.Combat.DodgesTaken == 2,
        $"Expected 3 incoming attacks & 2 dodges, got {mage.Combat.AttacksTaken}/{mage.Combat.DodgesTaken}");

    // Combat and stats split by level band, rank (0 = unknown here) and enhancement signature (Phase 3.5 item 16)
    Assert(mage.CombatByProfile.TryGetValue("L1-10|R0|W6:10|C6:10|H6:10|K6:10", out var wizGear)
           && wizGear is { Crits: 1, Hits: 1, Misses: 1, HealEvents: 1, AttacksTaken: 2, DodgesTaken: 1, TotalDamageDealt: 500 },
        $"Wizard-gear combat profile mismatch: {string.Join(", ", mage.CombatByProfile.Keys)}");
    Assert(mage.CombatByProfile.TryGetValue("L1-10|R0|W0:0|C0:0|H0:0|K0:0", out var bare)
           && bare is { Hits: 1, Crits: 0, AttacksTaken: 1, DodgesTaken: 1, TotalDamageDealt: 100 },
        "Unenhanced combat profile mismatch (the second client must not mix into the first one's gear)");
    Assert(mage.CombatByProfile.Count == 2, $"Expected 2 combat profiles, got {mage.CombatByProfile.Count}");
    Assert(mage.StatsByProfile.TryGetValue("10|R0|W6:10|C6:10|H6:10|K6:10", out var lv10Profile) && lv10Profile.Intellect == 95,
        "Stat snapshot by profile missing");
    Assert(ClassProfileKeys.Band(91) == "L91-100" && ClassProfileKeys.Band(100) == "L91-100" && ClassProfileKeys.Band(11) == "L11-20",
        "Level band boundaries");
    // The old pure-set enhancement profile is no longer recorded (replaced by StatsByProfile)
    Assert(!finalDb.Enhancements.ContainsKey(6), "Legacy enhancement profile should not be recorded any more");
    Assert(finalDb.Shops.TryGetValue(141, out var enhShop) && enhShop!.Items[0].EnhancementPatternID == 6,
        "Shop 141 EnhancementPatternID not captured");

    var persistedJson = File.ReadAllText(primaryPath);
    // Match the IDs as standalone numbers only (timestamps such as "...:24.4242123Z" contain the digits by chance).
    static bool ContainsId(string json, string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(json, $@"(?<![\d.]){id}(?!\d)");
    Assert(!ContainsId(persistedJson, "4242") && !ContainsId(persistedJson, "8888"), "Persisted JSON must not leak ephemeral entity IDs");

    Console.WriteLine("5. Testing Army/Ultra upgrades (aura origin, party buffs, animStr, event zones, procs, ct.p deltas & schema sampler)...");
    var bossEng = new PacketCollectorEngine();
    bossEng.SchemaSampler.Enabled = true;
    bossEng.SetPlayerCombatContext(
        selfEntityId: 5050,
        className: "Lord of Order",
        level: 100,
        maxHealth: 3200,
        weaponEnhPattern: 9, weaponEnhLevel: 100,
        selfUsername: "SecretHero",
        weaponProcId: 9); // Valiance

    // Join ultra boss room with Boss (MapID 1, 100000 HP)
    bossEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "moveToArea",
      "strMapName": "ultradage-9999",
      "mondef": [
        { "MonID": 9001, "strMonName": "Dage the Dark Lord", "intLevel": 100, "intHPMax": 100000, "strRace": "Undead" }
      ],
      "monmap": [
        { "MonMapID": 1, "MonID": 9001, "strFrame": "Boss" }
      ],
      "monbranch": [
        { "MonMapID": 1, "MonID": 9001, "intHP": 50000, "intHPMax": 100000, "intLevel": 100 }
      ]
    }
    """));
    bossEng.RecordCellChange("ultradage", "Boss", "Left");

    // Establish initial self HP in ct.p, plus player outgoing hit (counts towards Valiance proc hits) and party buff + Valiance proc + Focus taunt
    bossEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "SecretHero": { "intHP": 3200, "intState": 2 }, "AllyTwo": { "intHP": 3000, "intState": 2 } },
      "sarsa": [
        {
          "cInf": "p:5050",
          "a": [
            { "tInf": "m:1", "type": "crit", "hp": 4200 },
            { "tInf": "m:1", "type": "hit", "hp": 1800 }
          ]
        }
      ],
      "a": [
        { "cmd": "aura+", "cInf": "p:5050", "tInf": "p:5050,p:6060", "aura": { "nam": "Order of the Lion", "dur": 10, "cat": "buff" } },
        { "cmd": "aura+", "cInf": "p:5050", "tInf": "p:5050", "aura": { "nam": "Valiance", "dur": 12 } },
        { "cmd": "aura+", "cInf": "p:5050", "tInf": "m:1", "aura": { "nam": "Focus", "dur": 6 } },
        { "cmd": "aura++", "cInf": "m:1", "tInf": "p:5050,p:6060", "aura": { "nam": "Noxious Decay", "dur": 8, "stacks": 2 } }
      ],
      "anims": [
        { "cInf": "m:1", "tInf": "p:5050,p:6060", "animStr": "ChargeAttack2", "msg": "Dage charges a dark cleave!" },
        { "cInf": "m:1", "tInf": "p:5050", "animStr": "Attack2" }
      ]
    }
    """));

    // Next ct tick: monster damages self via ct.p delta (-850 HP) + explicit AoE hit on 3 players; then heal via ct.p delta (+500 HP)
    bossEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "SecretHero": { "intHP": 2350, "intState": 2 } },
      "anims": [
        { "cInf": "m:1", "tInf": "p:5050,p:6060,p:7070", "animStr": "Attack2" }
      ]
    }
    """));
    bossEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "SecretHero": { "intHP": 2850, "intState": 2 } },
      "a": [
        { "cInf": "m:1", "tInf": "p:5050,p:6060,p:7070", "type": "dodge", "hp": 0 }
      ]
    }
    """));

    // Zone events (A, B, and empty string clear)
    bossEng.ProcessExtensionPacketJson(Pext("""{ "cmd": "event", "args": { "zoneSet": "A" } }"""));
    bossEng.ProcessExtensionPacketJson(Pext("""{ "cmd": "event", "args": { "zoneSet": "B" } }"""));
    bossEng.ProcessExtensionPacketJson(Pext("""{ "cmd": "event", "args": { "zoneSet": "" } }"""));

    var bossDb = store.FlushDelta(bossEng.TakeDeltaSnapshot());
    Assert(bossDb.Monsters.TryGetValue("id:9001", out var dage), "Dage boss record missing");
    // Aura-only entries did NOT pollute AttacksObserved; only the 1 ct.p damage hit (-850) + 1 explicit dodge action were counted!
    Assert(dage!.Combat.Hits == 1 && dage.Combat.TotalDamage == 850 && dage.Combat.Dodges == 1,
        $"Expected Dage Hits=1/Dmg=850/Dodges=1, got Hits={dage.Combat.Hits}/Dmg={dage.Combat.TotalDamage}/Dodges={dage.Combat.Dodges}");
    Assert(dage.Combat.MaxTargetsHit == 3, $"Expected AoE MaxTargetsHit=3, got {dage.Combat.MaxTargetsHit}");

    // Auras separated by CasterType + IsTaunt + Stacking + Boss HP%
    Assert(dage.Auras["Focus"].CasterType == "player" && dage.Auras["Focus"].IsTaunt, "Focus should be player-cast taunt");
    Assert(dage.Auras["Noxious Decay"].CasterType == "monster" && dage.Auras["Noxious Decay"].IsStacking && dage.Auras["Noxious Decay"].MaxStacksObserved == 2,
        "Noxious Decay boss aura metadata mismatch");
    Assert(dage.Auras["Noxious Decay"].MinBossHpPercent is >= 49.0 and <= 51.0,
        $"Expected Boss HP% ~50, got {dage.Auras["Noxious Decay"].MinBossHpPercent}");

    // Boss animStr skill events (both with and without msg!)
    Assert(dage.SkillEvents.ContainsKey("Attack2"), "Attack2 animStr without msg must be captured");
    Assert(dage.SkillEvents.TryGetValue("ChargeAttack2|Dage charges a dark cleave!", out var cleave) &&
           cleave!.AurasInSameTick.Contains("Noxious Decay"),
        "ChargeAttack2 skill event with co-occurring aura missing");

    // Zone events captured on both map and boss
    Assert(bossDb.Maps["ultradage"].ZoneEvents.ContainsKey("A") &&
           bossDb.Maps["ultradage"].ZoneEvents.ContainsKey("B") &&
           bossDb.Maps["ultradage"].ZoneEvents.ContainsKey("(clear)"),
        "Map ZoneEvents A/B/(clear) missing");
    Assert(dage.ZoneEvents.ContainsKey("A"), "Boss ZoneEvents missing");

    // Class party buffs + ct.p heal/damage + dodge attribution
    Assert(bossDb.Classes.TryGetValue("Lord of Order", out var loo), "Lord of Order class missing");
    Assert(loo!.Auras.TryGetValue("Order of the Lion", out var lion) && lion!.IsPartyBuff && lion.TargetScope == "party",
        "Party buff aura not captured on class");
    Assert(loo.Combat.HealEvents == 1 && loo.Combat.TotalHealingDone == 500,
        $"Expected ct.p heal delta 500, got {loo.Combat.HealEvents}x {loo.Combat.TotalHealingDone}");
    Assert(loo.Combat.TotalDamageTaken == 850 && loo.Combat.DodgesTaken == 1,
        $"Expected DmgTaken=850 & Dodges=1, got {loo.Combat.TotalDamageTaken} / {loo.Combat.DodgesTaken}");

    // Procs tracking (Valiance ProcID=9: 2 player hits equipped, 1 proc trigger -> 0.5 rate)
    Assert(bossDb.Procs.TryGetValue(9, out var valiance) &&
           valiance!.Name == "Valiance" &&
           valiance.PlayerHitsWhileEquipped == 2 &&
           valiance.ProcTriggersObserved == 1 &&
           Math.Abs(valiance.EstimatedTriggerRate - 0.5) < 0.001,
        $"Unexpected Valiance proc stats: hits={valiance?.PlayerHitsWhileEquipped}, triggers={valiance?.ProcTriggersObserved}");

    // Packet Schema Sampler anonymization & persistence check
    var schemaPath = Path.Combine(tempDir, "packet_schemas.json");
    bossEng.SchemaSampler.FlushToFiles(schemaPath);
    Assert(File.Exists(schemaPath), "packet_schemas.json not written");
    var schemaText = File.ReadAllText(schemaPath);
    Assert(!schemaText.Contains("SecretHero") && !schemaText.Contains("AllyTwo") && !schemaText.Contains("5050") && !schemaText.Contains("6060"),
        "packet_schemas.json must redact all player names and entity IDs");
    Assert(schemaText.Contains("<SELF>") && schemaText.Contains("event_zone") && schemaText.Contains("ct_aura_player"),
        "packet_schemas.json missing expected redacted markers or categories");

    var dbTextAfterBoss = File.ReadAllText(primaryPath);
    Assert(!dbTextAfterBoss.Contains("SecretHero") && !dbTextAfterBoss.Contains("AllyTwo") && !dbTextAfterBoss.Contains("5050"),
        "gamedata.json must never leak player names or UIDs");

    Console.WriteLine("6. Testing live AQW protocol fixes (sara actionResult, typ=d HoT, VHL self-dmg guard, stk stacks, updateClass sync, Attack1,Attack2 filter, casterId redaction)...");
    var liveEng = new PacketCollectorEngine();
    liveEng.SchemaSampler.Enabled = true;
    liveEng.SetPlayerCombatContext(
        selfEntityId: 128880,
        className: "Kathool Adept",
        level: 100,
        maxHealth: 3600,
        weaponEnhPattern: 9, weaponEnhLevel: 100,
        selfUsername: "LiveTester",
        weaponProcId: 3); // Awe Blast

    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "moveToArea",
      "strMapName": "genesisgarden-1",
      "mondef": [
        { "MonID": 5160, "strMonName": "Ancient Creature", "intLevel": 90, "intHPMax": 9000, "strRace": "Chaos" },
        { "MonID": 5163, "strMonName": "Long-eared Beast", "intLevel": 90, "intHPMax": 9000, "strRace": "Chaos" }
      ],
      "monmap": [
        { "MonMapID": 8, "MonID": 5160, "strFrame": "r5" },
        { "MonMapID": 9, "MonID": 5163, "strFrame": "r5" }
      ],
      "monbranch": [
        { "MonMapID": 8, "MonID": 5160, "intHP": 9000, "intHPMax": 9000, "intLevel": 90 },
        { "MonMapID": 9, "MonID": 5163, "intHP": 9000, "intHPMax": 9000, "intLevel": 90 }
      ]
    }
    """));
    liveEng.RecordCellChange("genesisgarden", "r5", "Left");

    // Packet A: Player auto + stacking Madness (stk: 4, casterId: 128880) + Awe Blast proc (both actRef="p3", actID=-1 AND aura "Awe Blast" -> count ONCE!)
    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "LiveTester": { "intHP": 3600, "intState": 2 } },
      "sarsa": [
        { "cInf": "p:128880", "actID": 0, "a": [ { "tInf": "m:9", "type": "crit", "hp": 854, "actRef": "aa" } ] },
        { "cInf": "p:128880", "actID": -1, "a": [ { "tInf": "m:9", "type": "crit", "hp": 1366, "actRef": "p3" } ] }
      ],
      "a": [
        { "cmd": "aura+", "cInf": "p:128880", "tInf": "m:9", "auras": [ { "nam": "Madness", "dur": 10, "stk": 4, "val": 0.8, "casterId": 128880, "casterType": "p" } ] },
        { "cmd": "aura+", "cInf": "p:128880", "tInf": "m:9", "auras": [ { "nam": "Awe Blast", "dur": 10, "casterId": 128880, "casterType": "p" } ] }
      ]
    }
    """));

    // Packet B: Real AQW sara[].actionResult with monster hit (m:9 -> 145), AoE second target (m:9 -> Ally), monster dodge (m:8 -> dodge), HoT (typ="d", hp=-83), and generic "Attack1,Attack2" animStr
    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "LiveTester": { "intHP": 3538 } },
      "sara": [
        { "actionResult": { "tInf": "p:128880", "type": "hit", "hp": 145, "cInf": "m:9" }, "iRes": 1 },
        { "actionResult": { "tInf": "p:777777", "type": "hit", "hp": 130, "cInf": "m:9" }, "iRes": 1 },
        { "actionResult": { "tInf": "p:128880", "type": "dodge", "hp": 0, "cInf": "m:8" }, "iRes": 1 },
        { "actionResult": { "tInf": "p:128880", "typ": "d", "cInf": "p:128880", "hp": -83 }, "iRes": 1 }
      ],
      "anims": [
        { "tInf": "p:128880", "fx": "m", "animStr": "Attack1,Attack2", "cInf": "m:9" },
        { "tInf": "p:128880", "fx": "m", "animStr": "Attack1,Attack2", "cInf": "m:8" }
      ]
    }
    """));

    // Packet C: Immediate class swap to Void Highlord via updateClass (and ignore another player's updateClass!)
    liveEng.ProcessExtensionPacketJson(Pext("""{ "cmd": "updateClass", "uid": 999999, "sClassName": "Necromancer" }"""));
    liveEng.ProcessExtensionPacketJson(Pext("""{ "cmd": "updateClass", "uid": 128880, "sClassName": "Void Highlord", "iCP": 302500 }"""));

    // Packet D: Immediately after class swap, stu + VHL Shackle self-sacrifice (-1018 HP in ct.p with sarsa player skill, NO monster attack!) + Shackled aura
    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "stu",
      "sta": { "$STR": 300, "$INT": 100, "$END": 420, "$DEX": 210, "$WIS": 120, "$LCK": 350, "$ap": 850, "$sp": 200, "$tha": 0.40, "$tcr": 0.48, "$scm": 1.85, "$tdo": 0.21 }
    }
    """));
    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "LiveTester": { "intHP": 5090, "intState": 2 } }
    }
    """));
    liveEng.ProcessExtensionPacketJson(Pext("""
    {
      "cmd": "ct",
      "p": { "LiveTester": { "intHP": 4072, "intState": 2 } },
      "sarsa": [
        { "cInf": "p:128880", "actID": 1, "a": [ { "tInf": "m:9", "type": "crit", "hp": 4200, "actRef": "a2" } ] }
      ],
      "a": [
        { "cmd": "aura+", "cInf": "p:128880", "tInf": "p:128880", "auras": [ { "nam": "Shackled", "dur": 15, "casterId": 128880, "casterType": "p" } ] }
      ]
    }
    """));

    // Verify legacy normalization ("Pattern_1" -> "Adventurer" and pruning "Attack1,Attack2" noise)
    var legacyDelta = new GameDatabase();
    legacyDelta.Enhancements[1] = new CapturedEnhancementProfile { PatternID = 1, Name = "Pattern_1", Observations = 5 };
    legacyDelta.Monsters["id:5160"] = new CapturedMonster
    {
        Key = "id:5160",
        ID = 5160,
        Name = "Ancient Creature",
        SkillEvents = new Dictionary<string, CapturedMonsterSkillEvent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Attack1,Attack2"] = new CapturedMonsterSkillEvent { Key = "Attack1,Attack2", AnimStr = "Attack1,Attack2", MaxTargetsHit = 1, TimesObserved = 10 }
        }
    };
    store.FlushDelta(legacyDelta);
    var liveDb = store.FlushDelta(liveEng.TakeDeltaSnapshot());

    // 1. sara[].actionResult hit + AoE MaxTargetsHit + dodge + HoT
    var beast = liveDb.Monsters["id:5163"];
    var creature = liveDb.Monsters["id:5160"];
    Assert(beast.Combat.Hits == 2 && beast.Combat.MinDamage == 130 && beast.Combat.MaxDamage == 145 && beast.Combat.MaxTargetsHit == 2,
        $"Expected Beast sara Hits=2, 130..145, MaxTargets=2 (no VHL 1018 bleed!), got Hits={beast.Combat.Hits}, {beast.Combat.MinDamage}..{beast.Combat.MaxDamage}, MaxTgt={beast.Combat.MaxTargetsHit}");
    Assert(creature.Combat.Dodges == 1 && creature.Combat.Hits == 0,
        $"Expected Creature sara Dodges=1/Hits=0, got Dodges={creature.Combat.Dodges}/Hits={creature.Combat.Hits}");

    // 2. Generic Attack1,Attack2 NOT recorded (and legacy pruned!)
    Assert(!beast.SkillEvents.ContainsKey("Attack1,Attack2") && !creature.SkillEvents.ContainsKey("Attack1,Attack2"),
        "Generic Attack1,Attack2 auto-attack must not pollute SkillEvents");

    // 3. Stacking "stk": 4 parsed on Madness
    Assert(beast.Auras["Madness"].IsStacking && beast.Auras["Madness"].MaxStacksObserved == 4,
        $"Expected Madness IsStacking=true, MaxStacks=4, got {beast.Auras["Madness"].IsStacking}/{beast.Auras["Madness"].MaxStacksObserved}");

    // 4. Class separation (Kathool Adept vs Void Highlord, zero cross-contamination & Necromancer ignored)
    Assert(!liveDb.Classes.ContainsKey("Necromancer"), "Other player's updateClass must be ignored");
    var ka = liveDb.Classes["Kathool Adept"];
    var vhl = liveDb.Classes["Void Highlord"];
    Assert(ka.Auras.ContainsKey("Madness") && !ka.Auras.ContainsKey("Shackled") && !ka.Auras.ContainsKey("Awe Blast"),
        "Kathool Adept auras must contain Madness only (no Shackled cross-bleed, no weapon proc Awe Blast)");
    Assert(ka.Combat.DodgesTaken == 1 && ka.Combat.TotalDamageTaken == 145 && ka.Combat.HealEvents == 1 && ka.Combat.TotalHealingDone == 83,
        $"Expected Kathool sara Dodges=1/DmgTaken=145/Heals=1x83, got Dodges={ka.Combat.DodgesTaken}/Dmg={ka.Combat.TotalDamageTaken}/Heal={ka.Combat.HealEvents}x{ka.Combat.TotalHealingDone}");
    Assert(vhl.Auras.ContainsKey("Shackled") && !vhl.Auras.ContainsKey("Madness") && vhl.InferredStatModel == "Tank Melee",
        $"Expected Void Highlord to cleanly own Shackled + Tank Melee, got model='{vhl.InferredStatModel}'");
    Assert(vhl.Combat.TotalDamageTaken == 0 && vhl.Combat.AttacksTaken == 0,
        $"VHL Shackle -1018 HP sacrifice must NOT count as incoming monster damage, got {vhl.Combat.TotalDamageTaken}");

    // 5. Awe Blast proc counted ONCE (not double-counted by p3 + aura) and p3 hit excluded from denominator
    Assert(liveDb.Procs.TryGetValue(3, out var aweBlast) && aweBlast!.ProcTriggersObserved == 1 && aweBlast.PlayerHitsWhileEquipped == 2,
        $"Expected Awe Blast Triggers=1, EquippedHits=2 (Kathool aa + VHL a2, excluding p3 itself), got Triggers={aweBlast?.ProcTriggersObserved}, Hits={aweBlast?.PlayerHitsWhileEquipped}");

    // 6. Legacy enhancement Pattern_1 normalized to Adventurer
    Assert(liveDb.Enhancements.TryGetValue(1, out var advEnh) && advEnh!.Name == "Adventurer",
        $"Expected Pattern 1 normalized to 'Adventurer', got '{advEnh?.Name}'");

    // 7. PacketSchemaSampler redacts casterId and uid completely AND merges across sessions + retroactively scrubs legacy files
    var liveSchemaPath = Path.Combine(tempDir, "live_packet_schemas.json");
    // Seed a legacy file containing an unredacted numeric casterId and an earlier map sample to verify cross-session merge + scrub
    File.WriteAllText(liveSchemaPath, """
    {
      "Note": "Legacy",
      "LastUpdatedUtc": "2026-11-18T00:00:00Z",
      "Categories": {
        "ct_aura_monster": [
          {
            "CapturedUtc": "2026-11-18T00:00:00Z",
            "Command": "ct",
            "MapContext": "oldmap",
            "Payload": {
              "cmd": "ct",
              "p": { "<SELF>": { "intHP": 3000 } },
              "a": [ { "cmd": "aura+", "cInf": "p:<SELF>", "tInf": "m:1", "auras": [ { "nam": "Madness", "casterId": 555444, "casterType": "p" } ] } ]
            }
          }
        ]
      }
    }
    """);
    liveEng.SchemaSampler.FlushToFiles(liveSchemaPath);
    var liveSchemaText = File.ReadAllText(liveSchemaPath);
    Assert(!liveSchemaText.Contains("128880") && !liveSchemaText.Contains("777777") && !liveSchemaText.Contains("555444") && !liveSchemaText.Contains("LiveTester"),
        "live_packet_schemas.json must redact new and legacy casterId, uid, and username");
    Assert(liveSchemaText.Contains("<SELF_ID>") && liveSchemaText.Contains("updateClass_sample") && liveSchemaText.Contains("oldmap") && liveSchemaText.Contains("genesisgarden"),
        "live_packet_schemas.json must preserve previous session samples while accumulating new maps/categories");

    Console.WriteLine("7. Testing protocol verification sampling (allowlisted packets, command census, skeletons, privacy)...");
    var protoEng = new PacketCollectorEngine();
    protoEng.SchemaSampler.Enabled = true;
    protoEng.SetPlayerCombatContext(
        selfEntityId: 31337, className: "Rogue", level: 20, maxHealth: 1200,
        weaponEnhPattern: 3, weaponEnhLevel: 20, selfUsername: "ProtoTester", weaponProcId: 0);

    void Send(string json) => protoEng.ProcessExtensionPacketJson(json);
    Send("""
    { "params": { "type": "json", "dataObj": {
        "cmd": "moveToArea", "strMapName": "protomap", "strMapFileName": "proto/town.swf",
        "mondef": [ { "MonID": "60", "strMonName": "Proto Skeleton", "iLvl": 3, "sRace": "Undead", "intHPMax": 800 } ],
        "monBranch": [ { "MonMapID": "1", "MonID": "60", "iLvl": 3, "intHP": 800, "intHPMax": 800 } ],
        "uoBranch": [ { "strUsername": "RoomStranger", "uoName": "roomstranger", "entID": 424242, "intLevel": 77 } ]
    } } }
    """);
    Send("""{ "params": { "type": "json", "dataObj": { "cmd": "addGoldExp", "intGold": 12, "intExp": 340, "iCP": 5, "typ": "m", "id": 1 } } }""");
    Send("""{ "params": { "type": "json", "dataObj": { "cmd": "loadInventoryBig", "items": [ { "sName": "SecretPersonalSword", "ItemID": 999001 } ] } } }""");
    Send("""{ "params": { "type": "json", "dataObj": { "cmd": "mysteryCmd", "someValue": 98765, "label": "SecretValueText", "list": [1, 2, 3] } } }""");
    Send("""{ "params": { "type": "json", "dataObj": { "cmd": "mysteryCmd", "someValue": 1, "label": "x", "list": [] } } }""");
    Send("""{ "params": { "type": "str", "dataObj": [ "respawnMon", "-1", "1" ] } }""");

    var protoSchemaPath = Path.Combine(tempDir, "proto_packet_schemas.json");
    protoEng.SchemaSampler.FlushToFiles(protoSchemaPath);
    var protoText = File.ReadAllText(protoSchemaPath);
    var protoDoc = Newtonsoft.Json.Linq.JObject.Parse(protoText);
    var cats = (Newtonsoft.Json.Linq.JObject)protoDoc["Categories"]!;
    var counts = (Newtonsoft.Json.Linq.JObject)protoDoc["CommandCounts"]!;

    Assert(cats["proto_moveToArea"] != null && protoText.Contains("<OMITTED 1 room player(s)>"), "moveToArea must be sampled with the room player list omitted");
    Assert(protoText.Contains("monBranch") && protoText.Contains("iLvl"), "moveToArea sample must keep the monster keys we need to verify");
    Assert(!protoText.Contains("RoomStranger") && !protoText.Contains("roomstranger") && !protoText.Contains("424242") && !protoText.Contains("ProtoTester") && !protoText.Contains("31337"),
        "Protocol samples must not contain other players, the own username or entity IDs");
    Assert(cats["proto_addGoldExp"] != null && protoText.Contains("340"), "addGoldExp must be sampled with its values");
    Assert(!protoText.Contains("SecretPersonalSword") && !protoText.Contains("999001") && cats["skeleton_loadInventoryBig"] == null,
        "Personal packets (inventory) must never be sampled, not even as skeletons");
    Assert(cats["skeleton_mysteryCmd"] != null && !protoText.Contains("98765") && !protoText.Contains("SecretValueText") && protoText.Contains("<int>"),
        "Unknown commands must be sampled as value-free skeletons");
    Assert(counts.Value<long>("mysteryCmd") == 2 && counts.Value<long>("loadInventoryBig") == 1 && counts.Value<long>("str:respawnMon") == 1,
        $"Command census must count names only (json and str), got: {counts.ToString(Newtonsoft.Json.Formatting.None)}");

    // A second session merges counts instead of overwriting them.
    var protoEng2 = new PacketCollectorEngine();
    protoEng2.SchemaSampler.Enabled = true;
    protoEng2.ProcessExtensionPacketJson("""{ "params": { "type": "str", "dataObj": [ "respawnMon", "-1", "2" ] } }""");
    protoEng2.SchemaSampler.FlushToFiles(protoSchemaPath);
    var mergedCounts = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(protoSchemaPath))["CommandCounts"]!;
    Assert(mergedCounts.Value<long>("str:respawnMon") == 2 && mergedCounts.Value<long>("mysteryCmd") == 2, "Command counts must accumulate across sessions");
    Console.WriteLine($"   OK: {cats.Count} categories, {counts.Count} command names counted, privacy guarantees held");

    // Fixtures below are REAL anonymized AQW packets captured by the protocol sampler (2026-11-20), trimmed for size.
    Console.WriteLine("8. Testing real-packet parsing (monBranch/iLvl/sRace/wDPS, quest rewards, drop attribution, sampler privacy)...");
    var realEng = new PacketCollectorEngine();
    realEng.SchemaSampler.Enabled = true;
    realEng.ProcessExtensionPacketJson("""
    { "params": { "type": "json", "dataObj":
      {"cmd":"moveToArea","areaId":185480,"areaName":"chaoscrypt-95964","strMapName":"chaoscrypt","strMapFileName":"Mirror/town-ChaosCrypt-22Feb18.swf","intType":1,"sExtra":"",
       "mondef":[{"sRace":"Chaos","strMonName":"Chaorrupted Knight","intLevel":4,"strMonFileName":"Guardcorrupt1.swf","strBehave":"walk","MonID":170,"strLinkage":"Guardcorrupt1"},
                 {"sRace":"Chaos","strMonName":"Chaorrupted Armor","intLevel":5,"strMonFileName":"Seppychaos.swf","strBehave":"walk","MonID":171,"strLinkage":"Seppychaos"}],
       "monmap":[{"MonID":170,"intRSS":-1,"bRed":false,"strFrame":"Crypt","MonMapID":1},{"MonID":170,"intRSS":-1,"bRed":false,"strFrame":"Basement","MonMapID":2}],
       "monBranch":[{"intHP":670,"intHPMax":670,"iLvl":4,"wDPS":9,"MonMapID":1,"MonID":170,"intMP":100,"bRed":false,"intState":1,"intMPMax":100},
                    {"intHP":670,"intHPMax":670,"iLvl":4,"wDPS":9,"MonMapID":2,"MonID":170,"intMP":100,"bRed":false,"intState":1,"intMPMax":100}],
       "uoBranch":[]}
    } }
    """);

    // Real getQuests shape: "reward" rows + "oRewards" item definitions (Elders' Blood quest).
    realEng.ProcessExtensionPacketJson("""
    { "params": { "type": "json", "dataObj": { "cmd": "getQuests", "quests": { "802": {
        "QuestID": 802, "sName": "Elders' Blood", "iExp": 0, "iGold": 0, "iLvl": 1, "bOnce": 0,
        "turnin": [ { "ItemID": 5574, "iQty": 8 } ],
        "oItems": { "5574": { "ItemID": 5574, "sName": "Slain Gorillaphant", "bTemp": true, "sType": "Quest Item", "iStk": 57, "sReqQuests": "795,802" } },
        "reward": [ { "ItemID": 5586, "iType": 0, "iQty": 1, "iRate": 100 } ],
        "oRewards": { "itemsS": { "0": { "ItemID": 5586, "sName": "Elders' Blood", "bTemp": false, "sType": "Item", "iStk": 20, "sReqQuests": "" } } }
    } } } } }
    """);

    // (a) A real monster drop right after a kill is credited to the monster.
    realEng.RecordMonsterKilled(1);
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"dropItem","items":{"898":{"ItemID":898,"sName":"Corrupted Armet","sType":"Helm","bTemp":false,"iQty":1,"iStk":1,"sReqQuests":""}}} } }""");
    // (b) The quest reward that arrives right after the turn-in is NOT credited to that same monster.
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"QuestID":802,"msg":"","rewardObj":{"intCoins":0,"intExp":0,"iCP":0,"intGold":0,"typ":"q"},"sName":"Elders' Blood","cmd":"ccqr","bSuccess":1} } }""");
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"dropItem","items":{"5586":{"ItemID":5586,"sName":"Elders' Blood","sType":"Item","bTemp":false,"iQty":1,"iStk":20,"sReqQuests":""}}} } }""");
    // (c) Real addItems carries the player's own inventory state (CharItemID/iQtyNow) — must never reach the samples.
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"msg":"","cmd":"addItems","items":{"8940":{"CharItemID":1483879064,"iQty":1,"bBank":0,"iQtyNow":18}}} } }""");
    // (d) The player's own house must never be sampled; respawnMon (str) is sampled.
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"house","areaName":"house-74055984","areaId":184962,"intType":1,"houseData":{"items":[1,2]},"uoBranch":[]} } }""");
    realEng.ProcessExtensionPacketJson("""{ "params": { "type": "str", "dataObj": [ "respawnMon", "-1", "1" ] } }""");

    var realDelta = realEng.TakeDeltaSnapshot();
    var knight = realDelta.Monsters["id:170"];
    Assert(knight.Level == 4 && knight.Race == "Chaos" && knight.WeaponDps == 9,
        $"monBranch iLvl / mondef sRace / wDPS must be parsed (got Level={knight.Level}, Race={knight.Race}, WeaponDps={knight.WeaponDps})");
    var cryptSpawn = realDelta.Maps["chaoscrypt"].Monsters[1];
    Assert(cryptSpawn.Level == 4 && cryptSpawn.Cell == "Crypt" && cryptSpawn.WeaponDps == 9, "Map spawn must carry level, cell and wDPS");
    Assert(knight.Drops.ContainsKey(898), "A drop right after a kill must be credited to the killed monster");
    Assert(!knight.Drops.ContainsKey(5586) && realEng.SessionQuestRewardsExcluded >= 1,
        "A quest reward arriving right after the turn-in must not be credited to the monster");
    var eldersQuest = realDelta.Quests[802];
    Assert(eldersQuest.Rewards.Count == 1 && eldersQuest.Rewards[0].ItemID == 5586 && eldersQuest.Rewards[0].ItemName == "Elders' Blood" &&
           eldersQuest.Rewards[0].RewardType == 0 && eldersQuest.Rewards[0].RatePercent == 100,
        "getQuests rewards must be parsed with name, type and rate");

    // (e) Without a recent kill, a drop is never credited to "the first monster in the cell" any more.
    var noKillEng = new PacketCollectorEngine();
    noKillEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"chaoscrypt","mondef":[{"sRace":"Chaos","strMonName":"Chaorrupted Knight","intLevel":4,"MonID":170}],"monmap":[{"MonID":170,"strFrame":"Enter","MonMapID":1}],"monBranch":[{"intHP":670,"intHPMax":670,"iLvl":4,"MonMapID":1,"MonID":170}]} } }""");
    noKillEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"dropItem","items":{"24528":{"ItemID":24528,"sName":"Chaos War Medal","sType":"Resource","bTemp":false,"iQty":1,"iStk":1000}}} } }""");
    var noKillDelta = noKillEng.TakeDeltaSnapshot();
    Assert(noKillDelta.Monsters.Values.All(m => m.Drops.Count == 0) && noKillEng.SessionUnattributedDropsSkipped == 1,
        "A drop with no recent kill must not be attributed to any monster");

    var realSchemaPath = Path.Combine(tempDir, "real_packet_schemas.json");
    realEng.SchemaSampler.FlushToFiles(realSchemaPath);
    var realSchemaText = File.ReadAllText(realSchemaPath);
    Assert(!realSchemaText.Contains("1483879064") && !realSchemaText.Contains("\"iQtyNow\": 18"),
        "addItems samples must not keep CharItemID / iQtyNow");
    Assert(!realSchemaText.Contains("74055984") && !realSchemaText.Contains("houseData\": {"),
        "The player's house must never be sampled");
    Assert(realSchemaText.Contains("proto_str_respawnMon"), "respawnMon string packets must be sampled");

    // Retroactive cleanup: a legacy house sample already on disk is dropped on the next flush.
    var legacyDoc = Newtonsoft.Json.Linq.JObject.Parse(realSchemaText);
    ((Newtonsoft.Json.Linq.JObject)legacyDoc["Categories"]!)["proto_moveToArea"] = Newtonsoft.Json.Linq.JArray.Parse("""
    [ { "CapturedUtc": "2026-11-20T00:00:00Z", "Command": "moveToArea", "MapContext": "house", "ShapeKey": "legacy-house",
        "Payload": { "cmd": "moveToArea", "strMapName": "house", "areaName": "house-74055984", "houseData": { "x": 1 } } } ]
    """);
    File.WriteAllText(realSchemaPath, legacyDoc.ToString());
    var cleanupEng = new PacketCollectorEngine();
    cleanupEng.SchemaSampler.Enabled = true;
    cleanupEng.SchemaSampler.CountCommand("ct");
    cleanupEng.SchemaSampler.FlushToFiles(realSchemaPath);
    Assert(!File.ReadAllText(realSchemaPath).Contains("74055984"), "Legacy house samples must be removed retroactively");
    Console.WriteLine($"   OK: Level/Race/wDPS parsed, quest reward excluded ({realEng.SessionQuestRewardsExcluded}), unattributed drop skipped, privacy rules enforced");

    Console.WriteLine("9. Testing schema v2 (migration + backup, quest-gated rates, passive monsterDrops, map-item quests, occupancy)...");
    // (a) v1 -> v2 migration with a dated backup and provable-error cleanup.
    var v2Dir = Path.Combine(tempDir, "v2");
    Directory.CreateDirectory(v2Dir);
    var v2Path = Path.Combine(v2Dir, "gamedata.json");
    var v1Db = new GameDatabase { SchemaVersion = 1 };
    var viking = new CapturedMonster { Key = "id:700", ID = 700, Name = "Skeletal Viking", KillCount = 100 };
    viking.Drops[1] = new CapturedDropStat { ItemID = 1, ItemName = "Impossible Item", DropEvents = 150, EligibleKills = 150 };
    viking.Drops[2] = new CapturedDropStat { ItemID = 2, ItemName = "Fighter Helm", DropEvents = 40, EligibleKills = 100 };
    viking.Drops[3] = new CapturedDropStat { ItemID = 3, ItemName = "Viking Fragment", Temp = true, DropEvents = 2, EligibleKills = 100 };
    viking.Drops[4] = new CapturedDropStat { ItemID = 4, ItemName = "Bone Dust", DropEvents = 25, EligibleKills = 100 };
    viking.Drops[5] = new CapturedDropStat { ItemID = 5, ItemName = "Treasure Chest", DropEvents = 10, EligibleKills = 100 };
    v1Db.Monsters[viking.Key] = viking;
    v1Db.Items[3] = new CapturedItem { ID = 3, Name = "Viking Fragment", Temp = true };
    File.WriteAllText(v2Path, Newtonsoft.Json.JsonConvert.SerializeObject(v1Db));

    var hints = new SkuaSim.Collector.Storage.QuestCatalogHints();
    hints.RewardItemIds.Add(2);          // Fighter Helm: a quest reward — kept (rewards can also be real drops)
    hints.AddRequirement(3, 500);        // temporary + required by quest 500 -> quest-gated (server sets sReqQuests)
    hints.AddRequirement(5, 600);        // permanent + required by quest 600 -> NOT gated (server leaves sReqQuests empty)
    var v2Store = new GameDatabaseStore(v2Path) { QuestHintsProvider = () => hints };
    var migrated = v2Store.FlushDelta(new GameDatabase());
    var mv = migrated.Monsters["id:700"];
    Assert(migrated.SchemaVersion == GameDatabase.CurrentSchemaVersion && migrated.MigrationLog.Count == 2, "Database must be migrated v1->v2->v3 with one log entry per step");
    Assert(v2Store.LastMigrationBackupPath != null && File.Exists(v2Store.LastMigrationBackupPath) &&
           File.ReadAllText(v2Store.LastMigrationBackupPath).Contains("\"SchemaVersion\":1"), "A dated v1 backup must be written before migrating");
    Assert(!mv.Drops.ContainsKey(1), "Impossible drop records (more drops than kills) must be removed");
    Assert(mv.Drops.ContainsKey(2), "Quest rewards that may also be real drops must be kept");
    Assert(mv.Drops[3].QuestGated && mv.Drops[3].RequiredQuestIds.SetEquals(new[] { 500 }) && mv.Drops[3].RateBasis == DropRates.Unverified &&
           mv.Drops[3].EstimatedDropChance == null, "Temporary quest-required drops must become quest-gated with an unverified (null) rate");
    Assert(!mv.Drops[5].QuestGated && mv.Drops[5].RateBasis == DropRates.AllKills, "Permanent quest-required items keep their all-kills rate");
    Assert(mv.Drops[4].RateBasis == DropRates.AllKills && mv.Drops[4].EstimatedDropChance is { } bone && Math.Abs(bone - 0.25) < 0.001, "Normal drops keep their all-kills rate");
    Assert(migrated.Items[3].RequiredByQuestIds.Contains(500), "Items get their requiring quests");
    var backupsBefore = Directory.GetFiles(v2Dir, "gamedata.v1-backup-*.json").Length;
    v2Store.FlushDelta(new GameDatabase());
    Assert(Directory.GetFiles(v2Dir, "gamedata.v1-backup-*.json").Length == backupsBefore, "A v2 file must not be migrated or backed up again");

    // (b) Quest-gated rates are measured only with the quest active (and only once the active list is known).
    var qEng = new PacketCollectorEngine();
    void Join(PacketCollectorEngine e) => e.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"arcangrove","mondef":[{"sRace":"Beast","strMonName":"Gorillaphant","intLevel":10,"MonID":250}],"monmap":[{"MonID":250,"strFrame":"Enter","MonMapID":1}],"monBranch":[{"intHP":900,"intHPMax":900,"iLvl":10,"wDPS":20,"MonMapID":1,"MonID":250}]} } }""");
    Join(qEng);
    qEng.RecordMonsterKilled(1); // active list unknown -> not tracked
    qEng.SyncActiveQuests(new[] { 802 });
    Thread.Sleep(450); // the engine deduplicates kills of the same MonMapID reported within 400 ms
    qEng.RecordMonsterKilled(1);
    qEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"addItems","items":{"5574":{"ItemID":5574,"sName":"Slain Gorillaphant","bTemp":true,"sType":"Quest Item","iStk":57,"iQty":1,"sReqQuests":"795,802"}}} } }""");
    qEng.SyncActiveQuests(Array.Empty<int>());
    Thread.Sleep(450);
    qEng.RecordMonsterKilled(1);
    var qDelta = qEng.TakeDeltaSnapshot();
    var gorilla = qDelta.Monsters["id:250"];
    var slain = gorilla.Drops[5574];
    Assert(gorilla.KillCount == 3 && gorilla.TrackedKills == 2 && gorilla.KillsByActiveQuestSet.GetValueOrDefault("802") == 1 && gorilla.KillsByActiveQuestSet.GetValueOrDefault("") == 1,
        $"Kills must be tracked per active quest set only once the list is known (Kill={gorilla.KillCount}, Tracked={gorilla.TrackedKills}, sets={string.Join(";", gorilla.KillsByActiveQuestSet)})");
    Assert(slain.QuestGated && slain.RequiredQuestIds.SetEquals(new[] { 795, 802 }) && slain.TrackedDropEvents == 1 &&
           slain.RateBasis == DropRates.QuestActiveKills && slain.EstimatedDropChance == 1.0,
        $"A quest-gated drop's rate must use quest-active kills (basis={slain.RateBasis}, chance={slain.EstimatedDropChance})");

    // (c) Passive official drop list (real monsterDrops shape, items keyed by ItemID): official + gated, no invented rate.
    qEng.ProcessExtensionPacketJson("""
    { "params": { "type": "json", "dataObj": {"cmd":"monsterDrops","MonMapID":1,"items":{
        "104953":{"ItemID":104953,"sName":"Pumpkin Rind","sType":"Quest Item","bTemp":true,"iStk":6,"sReqQuests":"10941","questGated":["Five-Star Service"],"questObjective":["Five-Star Service"]},
        "9001":{"ItemID":9001,"sName":"Gorilla Pelt","sType":"Resource","bTemp":false,"iStk":100,"sReqQuests":""}}} } }
    """);
    var official = qEng.TakeDeltaSnapshot().Monsters["id:250"];
    var rind = official.Drops[104953];
    var pelt = official.Drops[9001];
    Assert(rind.IsOfficialDrop && rind.QuestGated && rind.RequiredQuestIds.Contains(10941) && rind.QuestGatedNames.Contains("Five-Star Service") &&
           rind.DropEvents == 0 && rind.EstimatedDropChance == null, "Official quest-gated drops must be recorded without an invented rate");
    Assert(pelt.IsOfficialDrop && !pelt.QuestGated && pelt.EstimatedDropChance == null && official.OfficialDropsObservedUtc != null,
        "Official drops never observed dropping must have a null rate");

    // (d) Quest-gated map items: requiring quests from sReqQuests + active-quest observations.
    var mEng = new PacketCollectorEngine();
    Join(mEng);
    mEng.SyncActiveQuests(new[] { 7277 });
    mEng.ProcessClientPacket("%xt%zm%getMapItem%1%5044%");
    mEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"addItems","items":{"52566":{"ItemID":52566,"sName":"Star of the Sandsea","bTemp":true,"sType":"Quest Item","iStk":1,"iQty":1,"sReqQuests":"7277"}}} } }""");
    // (e) Room occupancy (peak player count; the client exposes no room cap).
    mEng.RecordPlayerCount("arcangrove-12345", 6);
    mEng.RecordPlayerCount("arcangrove-12345", 4);
    var mDelta = mEng.TakeDeltaSnapshot();
    var mapItem = mDelta.Maps["arcangrove"].MapItems[5044];
    Assert(mapItem.GrantedItemIDs.Contains(52566) && mapItem.RequiredQuestIds.Contains(7277) &&
           mapItem.GrantObservations == 1 && mapItem.ActiveQuestCounts.GetValueOrDefault(7277) == 1, "Map items must record their requiring/active quests");
    Assert(mDelta.Maps["arcangrove"].PeakPlayersObserved == 6 && mDelta.Maps["arcangrove"].PlayerCountSamples == 2, "Peak room occupancy must be recorded");

    // (f) Store merge sums the new counters and recomputes the rate basis from the merged totals.
    var mergeStore = new GameDatabaseStore(Path.Combine(v2Dir, "merge.json"));
    mergeStore.FlushDelta(qDelta);
    mergeStore.FlushDelta(mDelta);
    var mergedV2 = mergeStore.FlushDelta(official.Key == "id:250" ? new GameDatabase { Monsters = { [official.Key] = official } } : new GameDatabase());
    var mg = mergedV2.Monsters["id:250"];
    Assert(mergedV2.SchemaVersion == GameDatabase.CurrentSchemaVersion && mg.KillsByActiveQuestSet.GetValueOrDefault("802") == 1 && mg.Drops[5574].RateBasis == DropRates.QuestActiveKills &&
           mg.Drops[104953].IsOfficialDrop && mergedV2.Maps["arcangrove"].PeakPlayersObserved == 6 && mergedV2.Maps["arcangrove"].MapItems[5044].RequiredQuestIds.Contains(7277),
        "Merged database must keep quest-set counters, official flags, map-item quests and peak occupancy");
    Console.WriteLine($"   OK: migrated v1->v2 ({migrated.MigrationLog[0].Split(':').Last().Trim()[..60]}...), gated rates, official drops, map items, peak occupancy");

    Console.WriteLine("10. Testing schema v3 (kill rewards, respawn, quest-set kills, v2->v3 migration)...");
    var v3Eng = new PacketCollectorEngine();
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"chaoscrypt","mondef":[{"sRace":"Chaos","strMonName":"Chaorrupted Wolf","intLevel":12,"MonID":900}],"monmap":[{"MonID":900,"strFrame":"r2","MonMapID":1},{"MonID":900,"strFrame":"r2","MonMapID":2}],"monBranch":[{"intHP":670,"intHPMax":670,"iLvl":12,"wDPS":30,"MonMapID":1,"MonID":900},{"intHP":670,"intHPMax":670,"iLvl":12,"wDPS":30,"MonMapID":2,"MonID":900}]} } }""");

    // (a) Kill rewards from real addGoldExp shapes: totals include the bonus (base = total - bonus); zeros (max level /
    // gold cap) are counted apart; quest rewards (typ "q") are ignored here.
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"bonusGold":18,"intGold":30,"typ":"m","intExp":50,"id":2,"bonusExp":10,"cmd":"addGoldExp"} } }""");
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"intExp":480,"cmd":"addGoldExp","typ":"m","bonusGold":144,"intGold":288,"id":1} } }""");
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"addGoldExp","typ":"m","intExp":0,"intGold":0,"id":1} } }""");
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"typ":"q","intExp":625,"intGold":0,"bonusExp":125,"cmd":"addGoldExp"} } }""");

    // (b) Quest-set kills: an item required by quests 10 and 11 must count a kill with both active only once.
    v3Eng.SyncActiveQuests(new[] { 10, 11 });
    v3Eng.RecordMonsterKilled(1);
    v3Eng.RecordMonsterKilled(2);
    var respawnFrom = DateTime.UtcNow;
    v3Eng.SyncActiveQuests(new[] { 10 });
    Thread.Sleep(450);
    v3Eng.RecordMonsterKilled(1);
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"addItems","items":{"77001":{"ItemID":77001,"sName":"Wolf Fang","bTemp":true,"sType":"Quest Item","iStk":5,"iQty":1,"sReqQuests":"10,11"}}} } }""");

    // (c) Respawn: kill of MonMapID 2 -> ["respawnMon", "-1", "2"] (real str packet layout).
    Thread.Sleep(Math.Max(0, 600 - (int)(DateTime.UtcNow - respawnFrom).TotalMilliseconds));
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "str", "dataObj": [ "respawnMon", "-1", "2" ] } }""");
    v3Eng.ProcessExtensionPacketJson("""{ "params": { "type": "str", "dataObj": [ "respawnMon", "-1", "2" ] } }"""); // duplicate: no pending death
    var v3Delta = v3Eng.TakeDeltaSnapshot();
    var wolf = v3Delta.Monsters["id:900"];
    var kr = wolf.KillRewards;
    Assert(kr.Samples == 3 && kr.ExpSamples == 2 && kr.TotalBaseExp == 40 + 480 && kr.MinBaseExp == 40 && kr.MaxBaseExp == 480 && kr.ZeroExpSamples == 1 &&
           kr.GoldSamples == 2 && kr.TotalBaseGold == 12 + 144 && kr.ZeroGoldSamples == 1 && Math.Abs(kr.AverageBaseGold - 78) < 0.01,
        $"Kill rewards must store base values and skip zeros (exp={kr.TotalBaseExp}/{kr.ExpSamples}, gold={kr.TotalBaseGold}/{kr.GoldSamples})");
    var fang = wolf.Drops[77001];
    Assert(wolf.KillsByActiveQuestSet.GetValueOrDefault("10,11") == 2 && wolf.KillsByActiveQuestSet.GetValueOrDefault("10") == 1 &&
           fang.QuestGated && fang.EligibleKills == 3 && fang.EstimatedDropChance is { } fangRate && Math.Abs(fangRate - 0.3333) < 0.001,
        $"Each kill must count once for an item required by several active quests (eligible={fang.EligibleKills}, chance={fang.EstimatedDropChance})");
    var wolfSpawn = v3Delta.Maps["chaoscrypt"].Monsters[2];
    Assert(wolf.Respawn.Samples == 1 && wolfSpawn.Respawn.Samples == 1 && wolfSpawn.Respawn.MinMs >= 500 && wolfSpawn.Respawn.MinMs < 5000,
        $"A respawn sample must be recorded once per death (monster={wolf.Respawn.Samples}, spawn={wolfSpawn.Respawn.Samples}, ms={wolfSpawn.Respawn.MinMs})");

    // (d) v2 -> v3 migration: dated backup, nothing removed, legacy per-quest counts use the max (not the sum).
    var v3Dir = Path.Combine(tempDir, "v3");
    Directory.CreateDirectory(v3Dir);
    var v3Path = Path.Combine(v3Dir, "gamedata.json");
    var legacyDb = new GameDatabase { SchemaVersion = 2 };
    var mumbler = new CapturedMonster { Key = "id:901", ID = 901, Name = "Mumbler", KillCount = 50, TrackedKills = 20 };
    mumbler.KillsWhileQuestActive[10] = 20;
    mumbler.KillsWhileQuestActive[11] = 20;
    mumbler.Drops[77002] = new CapturedDropStat { ItemID = 77002, ItemName = "Mumble", Temp = true, QuestGated = true, RequiredQuestIds = { 10, 11 },
                                                  DropEvents = 10, TrackedDropEvents = 10, EligibleKills = 40, RateBasis = DropRates.QuestActiveKills };
    legacyDb.Monsters[mumbler.Key] = mumbler;
    File.WriteAllText(v3Path, Newtonsoft.Json.JsonConvert.SerializeObject(legacyDb));
    var v3Store = new GameDatabaseStore(v3Path);
    var mergedV3 = v3Store.FlushDelta(v3Delta);
    var mm = mergedV3.Monsters["id:901"].Drops[77002];
    Assert(mergedV3.SchemaVersion == 3 && mergedV3.MigrationLog.Count == 1 && Directory.GetFiles(v3Dir, "gamedata.v2-backup-*.json").Length == 1,
        "A v2 file must be backed up and migrated to v3");
    Assert(mm.EligibleKills == 20 && mm.EstimatedDropChance is { } mmRate && Math.Abs(mmRate - 0.5) < 0.001,
        $"Legacy per-quest counts must not be summed (eligible={mm.EligibleKills}, chance={mm.EstimatedDropChance})");
    var mergedWolf = mergedV3.Monsters["id:900"];
    Assert(mergedWolf.KillRewards.TotalBaseExp == 520 && mergedWolf.Respawn.Samples == 1 && mergedV3.Maps["chaoscrypt"].Monsters[2].Respawn.Samples == 1 &&
           mergedWolf.KillsByActiveQuestSet.GetValueOrDefault("10,11") == 2, "Merge must keep kill rewards, respawn samples and quest-set kills");
    // (e) Base quest reward from the real ccqr rewardObj (quest 7277) and the faction ID -> name catalog from addFaction
    // (keys verified from the live skeleton; the player's rank/rep must not be stored).
    var rewardEng = new PacketCollectorEngine();
    rewardEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"QuestID":7277,"msg":"","rewardObj":{"FactionID":60,"typ":"q","intCoins":0,"intExp":0,"iCP":0,"iRep":150,"intGold":0},"sName":"Star of the Sandsea","cmd":"ccqr","bSuccess":1} } }""");
    rewardEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"addFaction","faction":{"FactionID":"60","CharFactionID":"123456","iRank":"4","iRepToRank":"12500","sName":"Sandsea","iSpillRep":"1500","iRep":"11500","bitSuccess":"1"}} } }""");
    rewardEng.RecordFactionNames(new[] { (13, (string?)"Oasis") });
    // levelUp (keys from the live skeleton): level -> XP to next level; the player's own "xp" is not stored.
    rewardEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"levelUp","intLevel":12,"intExpToLevel":7740,"xp":31} } }""");
    rewardEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"levelUp","intLevel":100,"intExpToLevel":""} } }""");
    var rewardDelta = rewardEng.TakeDeltaSnapshot();
    var obs = rewardDelta.Quests[7277].ObservedReward;
    Assert(obs != null && obs.Rep == 150 && obs.FactionID == 60 && obs.Gold == 0 && obs.Samples == 1, "ccqr rewardObj must be stored as the quest's base reward");
    Assert(rewardDelta.Factions[60].Name == "Sandsea" && rewardDelta.Factions[13].Name == "Oasis", "Faction names must be cataloged");
    Assert(rewardDelta.XpToNextLevel.Count == 1 && rewardDelta.XpToNextLevel[12] == 7740, "levelUp must record level -> XP to next level (cap level skipped)");
    var factionJson = Newtonsoft.Json.JsonConvert.SerializeObject(rewardDelta.Factions);
    Assert(!factionJson.Contains("11500") && !factionJson.Contains("123456") && !factionJson.Contains("iRank"), "The player's faction rank/rep/CharFactionID must never be stored");
    var rewardMerged = new GameDatabase();
    GameDatabaseStore.MergeInto(rewardMerged, rewardDelta);
    GameDatabaseStore.MergeInto(rewardMerged, rewardDelta);
    Assert(rewardMerged.Quests[7277].ObservedReward!.Samples == 2 && rewardMerged.Quests[7277].ObservedReward!.Rep == 150 && rewardMerged.Factions.Count == 2,
        "Merge must keep the latest base reward, count samples and keep faction names");

    // (e2) Class points per kill (real addGoldExp sample, Arctic Wolf lv 15) and a sale paired with its request
    // (real sellItem response; the client request carries the item ID, CharItemID/iQtyNow are never stored).
    var cpEng = new PacketCollectorEngine();
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"icestormarena","mondef":[{"sRace":"None","strMonName":"Arctic Wolf","intLevel":15,"MonID":3986}],"monmap":[{"MonID":3986,"strFrame":"r6","MonMapID":9}],"monBranch":[{"intHP":1130,"intHPMax":1130,"iLvl":15,"wDPS":17,"MonMapID":9,"MonID":3986}]} } }""");
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"levelUp","intLevel":30,"intExpToLevel":131150,"xp":12} } }""");
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"updateClass","iCP":10000,"sClassCat":"M2","sClassName":"Rogue","uid":1} } }""");
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"bonusCP":0,"intGold":90,"bonusGold":45,"cmd":"addGoldExp","id":9,"typ":"m","intExp":150,"iCP":305} } }""");
    cpEng.ProcessClientPacket("%xt%zm%sellItem%1%12345%1%987654%");
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"bCoins":false,"cmd":"sellItem","intAmount":188,"iQty":1,"iQtyNow":0,"CharItemID":987654} } }""");
    cpEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"bCoins":false,"cmd":"sellItem","intAmount":50,"iQty":1,"iQtyNow":0,"CharItemID":987655} } }"""); // no request: ignored
    var cpDelta = cpEng.TakeDeltaSnapshot();
    var wolfKr = cpDelta.Monsters["id:3986"].KillRewards;
    Assert(wolfKr.CpSamples == 1 && wolfKr.TotalBaseCp == 305 && wolfKr.TotalBaseExp == 150 && wolfKr.TotalBaseGold == 45,
        $"Kill CP must be recorded from iCP - bonusCP (cp={wolfKr.TotalBaseCp}, xp={wolfKr.TotalBaseExp}, gold={wolfKr.TotalBaseGold})");
    Assert(wolfKr.CpByPlayerLevel.Count == 1 && wolfKr.CpByPlayerLevel[30].Samples == 1 && wolfKr.CpByPlayerLevel[30].Average == 305,
        "Kill CP must also be split by the character level (from the last levelUp / live player state)");
    Assert(wolfKr.CpByRankAndLevelGap.Count == 1 && wolfKr.CpByRankAndLevelGap["4|15"].Samples == 1,
        $"Kill CP must be keyed by class rank (10,000 CP = rank 4) and level gap (30 - 15): {string.Join(",", wolfKr.CpByRankAndLevelGap.Keys)}");

    // (e3) Class swap (CoreBots Solo/Farm/Dodge/Boss) and account switch on the same client.
    const string wolfMap = """{ "params": { "type": "json", "dataObj": {"cmd":"moveToArea","strMapName":"icestormarena","mondef":[{"sRace":"None","strMonName":"Arctic Wolf","intLevel":15,"MonID":3986}],"monmap":[{"MonID":3986,"strFrame":"r6","MonMapID":9}],"monBranch":[{"intHP":1130,"intHPMax":1130,"iLvl":15,"wDPS":17,"MonMapID":9,"MonID":3986}]} } }""";
    const string wolfKill = """{ "params": { "type": "json", "dataObj": {"bonusCP":0,"intGold":45,"bonusGold":0,"cmd":"addGoldExp","id":9,"typ":"m","intExp":150,"iCP":255} } }""";
    var swapEng = new PacketCollectorEngine();
    swapEng.ProcessExtensionPacketJson(wolfMap);
    swapEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"levelUp","intLevel":30,"intExpToLevel":131150,"xp":12} } }""");
    swapEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"updateClass","iCP":10000,"sClassCat":"M2","sClassName":"Rogue","uid":1} } }""");
    swapEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"updateClass","iCP":0,"sClassCat":"C1","sClassName":"Mage","uid":1} } }""");
    swapEng.ProcessExtensionPacketJson(wolfKill); // the new class (rank 1) earns the CP
    swapEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"loadInventoryBig"} } }"""); // another account logs in
    swapEng.ProcessExtensionPacketJson(wolfMap);
    swapEng.ProcessExtensionPacketJson(wolfKill); // level/rank of the new account not known yet
    var swapKr = swapEng.TakeDeltaSnapshot().Monsters["id:3986"].KillRewards;
    Assert(swapKr.CpSamples == 2 && swapKr.CpByRankAndLevelGap.Count == 1 && swapKr.CpByRankAndLevelGap["1|15"].Samples == 1 &&
           swapKr.CpByPlayerLevel[30].Samples == 1 && swapEng.CharacterContextResets == 1,
        $"A class swap must switch the rank, and a login must drop the previous account's level/rank: {string.Join(",", swapKr.CpByRankAndLevelGap.Keys)}");
    Assert(PacketCollectorEngine.ClassRankForCp(899) == 1 && PacketCollectorEngine.ClassRankForCp(900) == 2 &&
           PacketCollectorEngine.ClassRankForCp(302_499) == 9 && PacketCollectorEngine.ClassRankForCp(302_500) == 10,
        "Class rank thresholds must follow AQW's table (900 … 302,500)");
    var cpMerged = new CapturedKillRewards();
    cpMerged.Merge(wolfKr);
    cpMerged.Merge(wolfKr);
    Assert(cpMerged.CpByPlayerLevel[30].Samples == 2 && cpMerged.CpByPlayerLevel[30].Max == 305, "Per-level CP must merge");
    Assert(cpDelta.SellPrices.Count == 1 && cpDelta.SellPrices[12345].Amount == 188 && !cpDelta.SellPrices[12345].Coins &&
           !Newtonsoft.Json.JsonConvert.SerializeObject(cpDelta.SellPrices).Contains("987654"),
        "A sale must be paired with its request's item ID, without storing CharItemID");

    // (f) A full sample category still accepts a never-seen key set (e.g. a class-points key in addGoldExp), and the
    // key census records key names per command even when samples are full.
    var keySampler = new SkuaSim.Collector.Diagnostics.PacketSchemaSampler { Enabled = true };
    for (var i = 0; i < SkuaSim.Collector.Diagnostics.PacketSchemaSampler.MaxSamplesPerCategory + 3; i++)
        keySampler.TryCaptureProtocol("addGoldExp", Newtonsoft.Json.Linq.JObject.Parse("""{"cmd":"addGoldExp","typ":"m","intExp":50,"intGold":30,"id":2}"""), true, 0, null, $"map{i}");
    var withCp = Newtonsoft.Json.Linq.JObject.Parse("""{"cmd":"addGoldExp","typ":"m","intExp":50,"intGold":30,"id":2,"iCP":12}""");
    keySampler.TryCaptureProtocol("addGoldExp", withCp, true, 0, null, "map99");
    keySampler.CountKeySet("addGoldExp", withCp);
    var keyPath = Path.Combine(v3Dir, "key_schemas.json");
    keySampler.FlushToFiles(keyPath);
    var keyDoc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(keyPath));
    var gExpSamples = (Newtonsoft.Json.Linq.JArray)keyDoc["Categories"]!["proto_addGoldExp"]!;
    Assert(gExpSamples.Count == SkuaSim.Collector.Diagnostics.PacketSchemaSampler.MaxSamplesPerCategory + 1 && gExpSamples.Any(s => s["Payload"]?["iCP"] != null),
        $"A new key set must be sampled even when the category is full (got {gExpSamples.Count})");
    Assert(keyDoc["CommandCounts"]!.Children<Newtonsoft.Json.Linq.JProperty>().Any(p => p.Name == "keys:addGoldExp:cmd,iCP,id,intExp,intGold,typ"),
        "The key census must record the key names of each command");

    // (g) Cell numbers for "move to cell by number" (CoreBots.PvPMove: %xt%zm%mtcid%room%N%), learned from the cell the
    // character is in right after the request (cell names from the collected bludrutbrawl map). A request that did not
    // move the character is not a vote; the most voted name wins after merging.
    var cellEng = new PacketCollectorEngine();
    cellEng.RecordCellNumber("bludrutbrawl-999999", 5, "Enter0", "Morale0C");
    cellEng.RecordCellNumber("bludrutbrawl", 5, "Enter0", "Morale0C");
    cellEng.RecordCellNumber("bludrutbrawl", 4, "Morale0C", "Morale0B");
    cellEng.RecordCellNumber("bludrutbrawl", 7, "Morale0B", "Morale0B"); // did not move: ignored
    cellEng.RecordCellNumber("bludrutbrawl", 4, "Morale0C", "Morale0A"); // a stray vote
    var cellDelta = cellEng.TakeDeltaSnapshot();
    var brawl = cellDelta.Maps["bludrutbrawl"];
    Assert(brawl.CellForNumber(5) == "Morale0C" && brawl.CellNumbers[5]["Morale0C"] == 2 && !brawl.CellNumbers.ContainsKey(7),
        $"Cell numbers must be voted per map (room suffix dropped) and a request without a move ignored: {Newtonsoft.Json.JsonConvert.SerializeObject(brawl.CellNumbers)}");
    var cellDb = new GameDatabase();
    GameDatabaseStore.MergeInto(cellDb, cellDelta);
    var more = new PacketCollectorEngine();
    more.RecordCellNumber("bludrutbrawl", 4, "Morale0C", "Morale0B");
    GameDatabaseStore.MergeInto(cellDb, more.TakeDeltaSnapshot());
    Assert(cellDb.Maps["bludrutbrawl"].CellForNumber(4) == "Morale0B" && cellDb.Maps["bludrutbrawl"].CellNumbers[4]["Morale0B"] == 2,
        "Merged votes must add up and the most voted cell name wins");
    // The mtcid reply and the PvP event/score packets are sampled for verification (real key sets from the census
    // skeletons: PVPE {cmd,team,typ,unm,val}, PVPS {cmd,pvpScore[{k,m,r,v}]}); the player name in "unm" is redacted.
    var pvpSampler = new SkuaSim.Collector.Diagnostics.PacketSchemaSampler { Enabled = true };
    pvpSampler.TryCaptureStringPacket(Newtonsoft.Json.Linq.JArray.Parse("""["mtcid","-1","5"]"""), 0, null, "bludrutbrawl");
    pvpSampler.TryCaptureProtocol("PVPE", Newtonsoft.Json.Linq.JObject.Parse("""{"cmd":"PVPE","team":0,"typ":"kill","unm":"SomePlayer","val":"3"}"""), false, 0, "SomePlayer", "bludrutbrawl");
    pvpSampler.TryCaptureProtocol("PVPS", Newtonsoft.Json.Linq.JObject.Parse("""{"cmd":"PVPS","pvpScore":[{"k":1,"m":2,"r":3,"v":4},{"k":0,"m":1,"r":0,"v":2}]}"""), false, 0, null, "bludrutbrawl");
    var pvpPath = Path.Combine(v3Dir, "pvp_schemas.json");
    pvpSampler.FlushToFiles(pvpPath);
    var pvpText = File.ReadAllText(pvpPath);
    Assert(pvpText.Contains("proto_str_mtcid") && pvpText.Contains("proto_PVPE") && pvpText.Contains("proto_PVPS") && !pvpText.Contains("SomePlayer"),
        "mtcid, PVPE and PVPS must be sampled, with the player name redacted");

    // (g2) PvP events and scores are recorded per map (real shapes; the real final-looking sample was
    // [{k:10,m:3,r:4,v:1250},{0,0,0,0}]). A lower score total = a new match; leaving the map ends the match.
    var pvpEng = new PacketCollectorEngine();
    static string PvpPacket(string dataObj) => "{\"params\":{\"type\":\"json\",\"dataObj\":" + dataObj + "}}";
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"moveToArea","strMapName":"bludrutbrawl-100001","strMapFileName":"bludrutbrawl.swf"}"""));
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPE","team":0,"typ":"kill","unm":"SomePlayer","val":"Restorer"}"""));
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPS","pvpScore":[{"k":1,"m":0,"r":1,"v":100},{"k":0,"m":0,"r":0,"v":0}]}"""));
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPS","pvpScore":[{"k":10,"m":3,"r":4,"v":1250},{"k":0,"m":0,"r":0,"v":0}]}"""));
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPS","pvpScore":[{"k":0,"m":0,"r":0,"v":0},{"k":1,"m":0,"r":0,"v":50}]}""")); // next match
    pvpEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"moveToArea","strMapName":"battleon-1","strMapFileName":"battleon.swf"}"""));
    var pvpDelta = pvpEng.TakeDeltaSnapshot();
    Assert(pvpDelta.Pvp.TryGetValue("bludrutbrawl", out var brawlPvp), "PvP stats missing for bludrutbrawl");
    Assert(brawlPvp!.Events.GetValueOrDefault("team0:kill:Restorer") == 1 && brawlPvp.ScoreUpdates == 3,
        $"PvP events/score updates mismatch: {Newtonsoft.Json.JsonConvert.SerializeObject(brawlPvp)}");
    Assert(brawlPvp.FinalScores.SequenceEqual(new[] { "10,3,4,1250|0,0,0,0", "0,0,0,0|1,0,0,50" }),
        $"Final scores mismatch: {string.Join(" / ", brawlPvp.FinalScores)}");
    Assert(brawlPvp.Sequence.First() == "E:team0:kill:Restorer" && brawlPvp.Sequence.Count == 4,
        $"PvP sequence mismatch: {string.Join(" ; ", brawlPvp.Sequence)}");
    Assert(!Newtonsoft.Json.JsonConvert.SerializeObject(pvpDelta).Contains("SomePlayer"), "PvP data must not contain player names");
    var pvpDb = new GameDatabase();
    GameDatabaseStore.MergeInto(pvpDb, pvpDelta);
    GameDatabaseStore.MergeInto(pvpDb, pvpDelta);
    Assert(pvpDb.Pvp["bludrutbrawl"].Events["team0:kill:Restorer"] == 2 && pvpDb.Pvp["bludrutbrawl"].FinalScores.Count == 4,
        "Merged PvP stats must add up");

    // (g3) One record per match with the items received in it (to work out the Combat Trophy rule): items during a
    // match go to it; after leaving the map they still count for CapturedPvpMatch.GraceSeconds, then no longer.
    // dropItem/addItems shapes from the real samples (Chaos War Medal dropItem, addItems with iQtyNow).
    var pvpNow = new DateTime(2026, 11, 24, 2, 0, 0, DateTimeKind.Utc);
    var matchEng = new PacketCollectorEngine { PvpClockUtc = () => pvpNow };
    static string TrophyDrop(int dQty) => PvpPacket("{\"cmd\":\"dropItem\",\"items\":{\"4889\":{\"iLvl\":1,\"iStk\":5000,\"ItemID\":4889,\"dID\":4889,\"bCoins\":true,\"iQty\":40,\"dQty\":" + dQty + ",\"sName\":\"Combat Trophy\",\"bTemp\":false,\"sType\":\"Item\",\"sReqQuests\":\"\"}}}");
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"moveToArea","strMapName":"bludrutbrawl-100001","strMapFileName":"bludrutbrawl.swf"}"""));
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPE","team":0,"typ":"kill","unm":"SomePlayer","val":"Captain"}"""));
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPS","pvpScore":[{"k":10,"m":3,"r":4,"v":1250},{"k":0,"m":0,"r":0,"v":0}]}"""));
    matchEng.ProcessExtensionPacketJson(TrophyDrop(3));
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"PVPS","pvpScore":[{"k":0,"m":0,"r":0,"v":0},{"k":0,"m":0,"r":0,"v":10}]}""")); // next match
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"addItems","items":{"4889":{"CharItemID":123,"iQty":2,"bBank":false,"iQtyNow":45}}}"""));
    matchEng.ProcessExtensionPacketJson(PvpPacket("""{"cmd":"moveToArea","strMapName":"battleon-1","strMapFileName":"battleon.swf"}"""));
    pvpNow = pvpNow.AddSeconds(5);
    matchEng.ProcessExtensionPacketJson(TrophyDrop(1)); // within the grace window: still the second match
    var midDelta = matchEng.TakeDeltaSnapshot();
    Assert(midDelta.Pvp["bludrutbrawl"].Matches.Count == 1, "A match must stay open for late items until its grace window ends");
    pvpNow = pvpNow.AddSeconds(CapturedPvpMatch.GraceSeconds + 1);
    matchEng.ProcessExtensionPacketJson(TrophyDrop(7)); // too late: not credited
    var matchDb = new GameDatabase();
    GameDatabaseStore.MergeInto(matchDb, midDelta);
    GameDatabaseStore.MergeInto(matchDb, matchEng.TakeDeltaSnapshot());
    var matches = matchDb.Pvp["bludrutbrawl"].Matches;
    Assert(matches.Count == 2, $"Expected 2 matches: {Newtonsoft.Json.JsonConvert.SerializeObject(matches)}");
    Assert(matches[0].FinalScore == "10,3,4,1250|0,0,0,0" && matches[0].Events.GetValueOrDefault("team0:kill:Captain") == 1
        && matches[0].ItemsDropped.GetValueOrDefault("4889:Combat Trophy") == 3 && matches[0].ItemsAdded.Count == 0 && !matches[0].EndedByLeaving,
        $"First match mismatch: {Newtonsoft.Json.JsonConvert.SerializeObject(matches[0])}");
    Assert(matches[1].ItemsAdded.GetValueOrDefault("4889:Combat Trophy") == 2 && matches[1].ItemsDropped.GetValueOrDefault("4889:Combat Trophy") == 1
        && matches[1].EndedByLeaving,
        $"Second match mismatch (late item within the grace window counts, later ones not): {Newtonsoft.Json.JsonConvert.SerializeObject(matches[1])}");
    Assert(!Newtonsoft.Json.JsonConvert.SerializeObject(matchDb).Contains("SomePlayer"), "PvP match data must not contain player names");

    // (h) Drop size: in dropItem, "dQty" is the amount that dropped; "iQty" can be the stack already owned (real samples
    // carry both keys; the legacy sum of iQty gave ~1,400 Undead Energy per drop). Shape from the real Chaos War Medal
    // dropItem sample, with an owned stack of 1,400 and 2 dropped.
    var qtyEng = new PacketCollectorEngine();
    qtyEng.ProcessExtensionPacketJson(wolfMap);
    qtyEng.RecordMonsterKilled(9);
    qtyEng.ProcessExtensionPacketJson("""{ "params": { "type": "json", "dataObj": {"cmd":"dropItem","items":{"24528":{"iLvl":1,"iStk":1000,"ItemID":24528,"dID":24528,"bCoins":false,"iQty":1400,"dQty":2,"bQuest":1,"sName":"Chaos War Medal","bTemp":false,"sType":"Resource","sReqQuests":""}}} } }""");
    var medal = qtyEng.TakeDeltaSnapshot().Monsters["id:3986"].Drops[24528];
    Assert(medal.DropEvents == 1 && medal.QuantitySamples == 1 && medal.AverageQuantity == 2,
        $"The drop size must come from dQty, not from the owned stack in iQty (samples={medal.QuantitySamples}, avg={medal.AverageQuantity})");

    // (i) Server warnings/messages (collector 1.3.0). Shapes and texts from the CoreBots listeners that react to them
    // (MapIsMemberLocked: str ["warning", room, text]; RelogRequieredListener: JSON strMessage); stored anonymized with
    // the preceding client command; messages after a player-targeted command are never stored.
    var msgEng = new PacketCollectorEngine();
    msgEng.SetPlayerCombatContext(1, "Rogue", 50, selfUsername: "SecretHero");
    static string StrPacket(params string[] parts) => "{\"params\":{\"type\":\"str\",\"dataObj\":" + Newtonsoft.Json.JsonConvert.SerializeObject(parts) + "}}";
    msgEng.ProcessExtensionPacketJson(StrPacket("uotls", "-1", "OtherGuy", "afk:false"));
    msgEng.ProcessClientPacket("%xt%zm%cmd%1%tfer%SecretHero%lair-1%Enter%Spawn%");
    msgEng.ProcessExtensionPacketJson(StrPacket("warning", "-1", "\"lair\" is an Membership-Only Map."));
    msgEng.ProcessClientPacket("%xt%zm%cmd%1%tfer%SecretHero%lair-2%Enter%Spawn%");
    msgEng.ProcessExtensionPacketJson(StrPacket("warning", "-1", "\"lair\" is an Membership-Only Map."));
    msgEng.ProcessClientPacket("%xt%zm%buyItem%1%1770%215%4706%");
    msgEng.ProcessExtensionPacketJson("""{"params":{"type":"json","dataObj":{"cmd":"buyItem","bitSuccess":0,"strMessage":"Item is not buyable. Item Inventory full. Re-login to syncronize your real bag slot amount."}}}""");
    msgEng.ProcessExtensionPacketJson(StrPacket("server", "-1", "SecretHero and OtherGuy: 12 seconds left"));
    msgEng.ProcessClientPacket("%xt%zm%whisper%1%hi there%OtherGuy%");
    msgEng.ProcessExtensionPacketJson(StrPacket("warning", "-1", "Player OtherGuy is busy."));
    msgEng.ProcessClientPacket("%xt%zm%cmd%1%goto%SomeoneElse%");
    msgEng.ProcessExtensionPacketJson(StrPacket("warning", "-1", "Player 'SomeoneElse' could not be found."));
    var msgDelta = msgEng.TakeDeltaSnapshot();
    var msgs = msgDelta.ServerMessages;
    string MsgDump() => Newtonsoft.Json.JsonConvert.SerializeObject(msgs.Values.Select(m => $"{m.Kind}|{m.Text}|{string.Join(",", m.PrecededBy.Select(p => p.Key + "=" + p.Value))}"));
    Assert(msgs.TryGetValue("warning|\"<Q>\" is an Membership-Only Map.", out var memberMsg) && memberMsg.Count == 2 && memberMsg.PrecededBy.GetValueOrDefault("cmd:tfer") == 2
        && memberMsg.TargetMaps.GetValueOrDefault("lair") == 2,
        $"The member-map warning must be stored once with count 2 after cmd:tfer: {MsgDump()}");
    Assert(msgs.TryGetValue("json:buyItem|Item is not buyable. Item Inventory full. Re-login to syncronize your real bag slot amount.", out var buyMsg) && buyMsg.PrecededBy.ContainsKey("buyItem"),
        $"A JSON strMessage must be stored with its command and the preceding client command: {MsgDump()}");
    Assert(msgs.ContainsKey("server|<SELF> and <PLAYER>: # seconds left"), $"Own name, room player names and digits must be masked: {MsgDump()}");
    Assert(msgs.Count == 3 && msgEng.SessionServerMessagesSkipped == 2, $"Messages after whisper / slash commands must be skipped: {MsgDump()}");
    var msgJson = Newtonsoft.Json.JsonConvert.SerializeObject(msgDelta);
    Assert(!msgJson.Contains("SecretHero") && !msgJson.Contains("OtherGuy") && !msgJson.Contains("SomeoneElse"), "Server messages must not contain player names");
    var msgDb = new GameDatabase();
    GameDatabaseStore.MergeInto(msgDb, msgDelta);
    GameDatabaseStore.MergeInto(msgDb, msgDelta);
    Assert(msgDb.ServerMessages["warning|\"<Q>\" is an Membership-Only Map."].Count == 4, "Merged server message counts must add up");

    var mergedTwice = v3Store.FlushDelta(new GameDatabase());
    Assert(mergedTwice.Monsters["id:900"].KillRewards.Samples == 3 && Directory.GetFiles(v3Dir, "gamedata.v2-backup-*.json").Length == 1,
        "A v3 file must not be migrated again and empty flushes must not change counters");
    Console.WriteLine($"   OK: base XP {kr.AverageBaseExp}/kill, base gold {kr.AverageBaseGold}/kill, respawn {wolfSpawn.Respawn.MinMs} ms, quest-set rate {fang.EstimatedDropChance:P1}, v2->v3 migrated");

    Console.WriteLine("   OK: All Collector tests passed successfully!");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}
