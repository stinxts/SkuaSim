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

    Console.WriteLine("   OK: All Collector tests passed successfully!");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}
