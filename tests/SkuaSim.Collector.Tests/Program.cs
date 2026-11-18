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
Assert(pluginInstance.Options!.Count == 4, "Expected 4 plugin options");
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
    Assert(Math.Abs(mon.Drops[61890].EstimatedDropChance - 0.5) < 0.001, $"Expected 0.5 drop chance (1/2 kills), got {mon.Drops[61890].EstimatedDropChance}");

    Assert(merged.Shops.ContainsKey(2031) && merged.Shops[2031].Items.Count == 1, "Shop 2031 not captured");
    Assert(merged.Quests.ContainsKey(8152) && merged.Quests[8152].AcceptedCount == 1 && merged.Quests[8152].TurnInCount == 1, "Quest 8152 stats mismatch");

    var zipPath = store.ExportZipPackage(Path.Combine(tempDir, "exports"));
    Assert(File.Exists(zipPath), "Exported zip not created");

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

    Assert(finalDb.Enhancements.TryGetValue(6, out var wizEnh) && wizEnh!.Name == "Wizard" && wizEnh.PureSetStatsByLevel.ContainsKey(10),
        "Wizard enhancement profile not recorded");
    Assert(finalDb.Shops.TryGetValue(141, out var enhShop) && enhShop!.Items[0].EnhancementPatternID == 6,
        "Shop 141 EnhancementPatternID not captured");

    var persistedJson = File.ReadAllText(primaryPath);
    Assert(!persistedJson.Contains("4242") && !persistedJson.Contains("8888"), "Persisted JSON must not leak ephemeral entity IDs");

    Console.WriteLine("   OK: All Collector tests passed successfully!");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}
