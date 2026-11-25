/*
name: SkuaSim Diagnostic
description: Saves your character's current state as a SkuaSim scenario file (level, class, gear, inventory, bank, house, quest progress, reputation, boosts, client options and the reported script's options) so a script bug can be reproduced offline. Your character name is never written; options that look like account or player names are removed. Attach the file to your bug report.
tags: skuasim, diagnostic, bug report, tool
*/
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Core.Options;

/// <summary>
/// SkuaSim diagnostic generator. Run it in Skua/VibeSkua on the character that hit the bug, standing where the script
/// was started. It only reads (the bank is opened to list it) and writes one JSON file: a SkuaSim scenario that
/// <c>skuasim run --scenario &lt;file&gt;</c> or <c>skuasim bug-report &lt;file&gt;</c> loads as it is.
/// It never sends quest, shop or item actions and never writes the character name, guild, password or server.
/// </summary>
public class SkuaSimDiagnostic
{
    public const string Version = "1";

    public string OptionsStorage = "SkuaSimDiagnostic";
    public bool DontPreconfigure = false;
    public List<IOption> Options = new()
    {
        new Option<string>("ReportedScript", "Reported script",
            "The script that has the bug, relative to the Scripts folder (e.g. Story/Lair.cs). Its saved options are included.", ""),
        new Option<bool>("IncludeScriptOptions", "Include the script's options",
            "Copy the reported script's saved options (.cfg). Options that look like account or player names are removed.", true),
        new Option<bool>("IncludeBank", "Include the bank", "Open the bank and list its items (needed when the script unbanks).", true),
        new Option<string>("OutputFolder", "Output folder", "Where the file is saved (empty = Documents\\SkuaSim Diagnostics).", ""),
        new Option<string>("ScriptOptionsFile", "Script options file",
            "Optional: a .cfg file to read instead of the reported script's own (advanced).", ""),
    };

    private IScriptInterface Bot = null!;

    // Awe and Forge enhancement unlock quests (same table as SkuaSim's EnhancementUnlockCatalog / CoreAdvanced).
    private static readonly (string Name, bool Awe, int QuestId)[] EnhancementUnlocks =
    {
        ("Spiral Carve", true, 2937), ("Awe Blast", true, 2937), ("Health Vamp", true, 2937), ("Mana Vamp", true, 2937), ("Powerword Die", true, 2937),
        ("Forge (Weapon)", false, 8738), ("Lacerate", false, 8739), ("Smite", false, 8740), ("Valiance", false, 8741),
        ("Arcana's Concerto", false, 8742), ("Acheron", false, 8820), ("Elysium", false, 8821), ("Praxis", false, 9171),
        ("Dauntless", false, 9172), ("Ravenous", false, 9560),
        ("Forge (Helm)", false, 8828), ("Vim", false, 8824), ("Examen", false, 8825), ("Anima", false, 8826), ("Pneuma", false, 8827),
        ("Hearty", false, 9466),
        ("Forge (Cape)", false, 8758), ("Absolution", false, 8743), ("Avarice", false, 8745), ("Vainglory", false, 8744),
        ("Penitence", false, 8822), ("Lament", false, 8823)
    };

    // Client options copied to the scenario (Bot.Options). CustomName / CustomGuild are never copied.
    private static readonly string[] ClientOptionNames =
    {
        "AutoRelogin", "AutoReloginAny", "RetryRelogin", "SafeRelogin", "ReloginServer", "PrivateRooms", "SafeTimings", "RestPackets",
        "AggroMonsters", "AggroAllMonsters", "AttackWithoutTarget", "AcceptACDrops", "AcceptAllDrops", "RejectAllDrops",
        "ActionDelay", "HuntDelay", "HuntBuffer", "HuntPriority", "InfiniteRange", "Magnetise", "LagKiller", "SkipCutscenes",
        "JoinMapTries", "QuestAcceptAndCompleteTries", "MaximumTries", "ReloginTries", "UseFunctionBasedSkills"
    };

    // Option keys (after "Category:") that may hold an account or player name: never copied.
    private static readonly Regex PrivateOptionKey = new(
        @"player|account|username|password|login|e-?mail|^(alt|main|leader|follower|member|army|acc|user|char|name)\s*\d*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void ScriptMain(IScriptInterface bot)
    {
        Bot = bot;
        if (!Bot.Player.LoggedIn)
        {
            Bot.Log("[SkuaSim Diagnostic] Log in first: the character must be in game.");
            return;
        }

        var notes = new List<string>();
        var reportedScript = ResolveReportedScript((Bot.Config!.Get<string>("ReportedScript") ?? "").Trim().Replace('\\', '/'), notes);
        var captured = DateTime.UtcNow;
        captured = new DateTime(captured.Ticks - captured.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        if (Bot.Config.Get<bool>("IncludeBank") && !Bot.Bank.Loaded)
        {
            Bot.Bank.Open();
            Bot.Bank.Load(true);
            Bot.Bank.Loaded = true;
            Bot.Sleep(1500);
        }

        var cached = Bot.Quests.Cached;
        if (cached == null || cached.Count == 0)
        {
            Bot.Quests.LoadCachedQuests();
            cached = Bot.Quests.Cached;
        }
        if (cached == null || cached.Count == 0)
            notes.Add("Skua's quest list (QuestData.json) was empty: story progress and dailies were not captured.");
        cached ??= new List<QuestData>();

        // Story progress: AQW stores one value per quest-chain slot.
        var slotProgress = new SortedDictionary<int, int>();
        foreach (var slot in cached.Where(q => q.Slot >= 0).Select(q => q.Slot).Distinct().OrderBy(s => s))
        {
            var value = Bot.Flash.CallGameFunction<int>("world.getQuestValue", slot);
            if (value > 0)
                slotProgress[slot] = value;
        }

        // Daily / weekly / monthly quests already done in this period (the same flag Skua's IsDailyComplete reads).
        var dailiesDone = new SortedSet<int>();
        foreach (var q in cached.Where(q => !string.IsNullOrWhiteSpace(q.Field)))
            if (Bot.Flash.CallGameFunction<bool>("world.getAchievement", q.Field, q.Index))
                dailiesDone.Add(q.ID);

        bool QuestDone(int id)
        {
            var q = cached.FirstOrDefault(x => x.ID == id);
            return q != null && q.Slot >= 0 && slotProgress.TryGetValue(q.Slot, out var v) && v >= q.Value;
        }

        var player = new JObject
        {
            ["username"] = "Reporter",
            ["level"] = Bot.Player.Level,
            ["xp"] = Bot.Player.XP,
            ["gold"] = Bot.Player.Gold,
            ["coins"] = Bot.Player.Coins,
            ["isMember"] = Bot.Player.IsMember,
            ["class"] = Bot.Player.CurrentClass?.Name,
            ["classRank"] = Math.Max(1, Bot.Player.CurrentClassRank),
            ["completeEquipment"] = false,
            ["inventorySlots"] = Bot.Inventory.Slots,
            ["bankSlots"] = Bot.Bank.Slots,
            ["houseSlots"] = Bot.House.Slots
        };
        var cp = Bot.Flash.GetGameObject<int>("world.myAvatar.objData.iCP", 0);
        if (cp > 0 && RankFromClassPoints(cp) == Bot.Player.CurrentClassRank)
            player["classPoints"] = cp;
        else if (cp > 0)
            notes.Add($"Class points ({cp}) did not match class rank {Bot.Player.CurrentClassRank}; left out (the rank is used).");

        var inventory = Items(Bot.Inventory.Items);
        var bank = Bot.Config.Get<bool>("IncludeBank") ? Items(Bot.Bank.Items) : new JArray();
        if (!Bot.Config.Get<bool>("IncludeBank"))
            notes.Add("Bank not included (option off).");
        var house = Items(Bot.House.Items);
        var temp = new JArray(Bot.TempInv.Items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new JObject { ["name"] = i.Name, ["id"] = i.ID, ["quantity"] = i.Quantity, ["temp"] = true }));

        var factions = new JArray(Bot.Reputation.FactionList.OrderBy(f => f.ID)
            .Select(f => new JObject { ["name"] = f.Name, ["id"] = f.ID, ["rank"] = f.Rank, ["totalRep"] = f.TotalRep }));

        var quests = new JObject
        {
            ["completed"] = new JArray(),
            ["active"] = new JArray(Bot.Quests.Active.Select(q => q.ID).Distinct().OrderBy(id => id)),
            ["dailiesCompleted"] = new JArray(dailiesDone),
            ["slotProgress"] = new JObject(slotProgress.Select(kv => new JProperty(kv.Key.ToString(), kv.Value)))
        };

        var awe = EnhancementUnlocks.Where(e => e.Awe && QuestDone(e.QuestId)).Select(e => e.Name).ToList();
        var forge = EnhancementUnlocks.Where(e => !e.Awe && QuestDone(e.QuestId)).Select(e => e.Name).ToList();

        var botOptions = new JObject();
        foreach (var name in ClientOptionNames)
        {
            var prop = typeof(IScriptOption).GetProperty(name);
            if (prop == null)
                continue;
            try
            {
                var value = prop.GetValue(Bot.Options);
                if (value != null)
                    botOptions[name] = value is Enum ? JToken.FromObject(value.ToString()!) : JToken.FromObject(value);
            }
            catch { }
        }

        var scriptOptions = new JObject();
        if (Bot.Config.Get<bool>("IncludeScriptOptions"))
            ReadScriptOptions(reportedScript, scriptOptions, notes);

        var description =
            $"Captured by SkuaSim_Diagnostic.cs v{Version} on {captured:yyyy-MM-dd HH:mm} UTC" +
            $"{(string.IsNullOrEmpty(reportedScript) ? "" : $" for {reportedScript}")}" +
            $" (client {ClientVersion()}). Character name replaced by \"Reporter\"." +
            (notes.Count > 0 ? " Notes: " + string.Join(" ", notes) : "");

        var scenario = new JObject
        {
            ["name"] = $"Diagnostic {captured:yyyy-MM-dd HH:mm} UTC{(string.IsNullOrEmpty(reportedScript) ? "" : $" ({reportedScript})")}",
            ["description"] = description,
            ["startDate"] = captured.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["player"] = player,
            ["location"] = new JObject
            {
                ["map"] = string.IsNullOrWhiteSpace(Bot.Map.Name) ? "battleon" : Bot.Map.Name.ToLowerInvariant(),
                ["cell"] = string.IsNullOrWhiteSpace(Bot.Player.Cell) ? "Enter" : Bot.Player.Cell,
                ["pad"] = string.IsNullOrWhiteSpace(Bot.Player.Pad) ? "Spawn" : Bot.Player.Pad,
                ["loggedIn"] = true
            },
            ["inventory"] = inventory,
            ["bank"] = bank,
            ["house"] = house,
            ["tempInventory"] = temp,
            ["quests"] = quests,
            ["factions"] = factions,
            ["boosts"] = new JObject
            {
                ["xpBoost"] = Bot.Boosts.IsBoostActive(BoostType.Experience),
                ["classPointsBoost"] = Bot.Boosts.IsBoostActive(BoostType.Class),
                ["goldBoost"] = Bot.Boosts.IsBoostActive(BoostType.Gold),
                ["repBoost"] = Bot.Boosts.IsBoostActive(BoostType.Reputation)
            },
            ["enhancements"] = new JObject { ["unlockedAwe"] = new JArray(awe), ["unlockedForge"] = new JArray(forge) },
            ["options"] = scriptOptions,
            ["botOptions"] = botOptions
        };
        if (!string.IsNullOrEmpty(reportedScript))
            scenario["script"] = reportedScript;

        var folder = (Bot.Config.Get<string>("OutputFolder") ?? "").Trim();
        if (folder.Length == 0)
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkuaSim Diagnostics");
        Directory.CreateDirectory(folder);
        var scriptTag = string.IsNullOrEmpty(reportedScript) ? "" : "_" + Regex.Replace(Path.GetFileNameWithoutExtension(reportedScript), @"[^A-Za-z0-9_-]", "");
        var path = Path.Combine(folder, $"diagnostic_{captured:yyyyMMdd-HHmmss}{scriptTag}.json");
        File.WriteAllText(path, scenario.ToString(Formatting.Indented));

        Bot.Log($"[SkuaSim Diagnostic] Saved: {path}");
        Bot.Log($"[SkuaSim Diagnostic] Level {Bot.Player.Level} {Bot.Player.CurrentClass?.Name} (rank {Bot.Player.CurrentClassRank}) in {Bot.Map.Name}: " +
                $"{inventory.Count} inventory, {bank.Count} bank, {house.Count} house items; {slotProgress.Count} story slots, " +
                $"{dailiesDone.Count} dailies done, {factions.Count} factions, {scriptOptions.Count} script options.");
        foreach (var note in notes)
            Bot.Log($"[SkuaSim Diagnostic] Note: {note}");
    }

    /// <summary>
    /// The reported script as a path relative to the Scripts folder. A bare name ("GrimGaol", "grimgaol.cs") or a wrong
    /// folder is looked up by file name (case-insensitive); with one match its real path is used.
    /// </summary>
    private static string ResolveReportedScript(string given, List<string> notes)
    {
        if (given.Length == 0 || Path.IsPathRooted(given))
            return given;
        var scriptsDir = ClientFileSources.SkuaScriptsDIR;
        if (File.Exists(Path.Combine(scriptsDir, given)))
            return given;
        var fileName = Path.GetFileName(given);
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            fileName += ".cs";
        if (!Directory.Exists(scriptsDir))
            return given;
        var matches = Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(scriptsDir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 1)
            return matches[0];
        notes.Add(matches.Count == 0
            ? $"Reported script \"{given}\" was not found in the Scripts folder."
            : $"Reported script \"{given}\" matches {matches.Count} files ({string.Join(", ", matches)}): give its folder.");
        return given;
    }

    private static JArray Items(IEnumerable<InventoryItem> items) => new(items
        .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.ID)
        .Select(i =>
        {
            var o = new JObject { ["name"] = i.Name, ["id"] = i.ID, ["quantity"] = i.Quantity };
            if (!string.IsNullOrWhiteSpace(i.CategoryString)) o["category"] = i.CategoryString;
            if (i.MaxStack > 0) o["maxStack"] = i.MaxStack;
            if (i.Equipped) o["equipped"] = true;
            if (i.Coins) o["coins"] = true;
            if (i.Upgrade) o["member"] = true;
            if (i.EnhancementLevel > 0) o["enhancementLevel"] = i.EnhancementLevel;
            if (i.EnhancementPatternID > 0) o["enhancementPatternID"] = i.EnhancementPatternID;
            if (i.ProcID > 0) o["procID"] = i.ProcID;
            if (!string.IsNullOrWhiteSpace(i.Meta)) o["meta"] = i.Meta;
            return o;
        }));

    /// <summary>Rank of a class from its total CP: rank r (2..10) needs (5·r·(r+1))² points (rank 2 = 900, rank 10 = 302,500).</summary>
    private static int RankFromClassPoints(int cp)
    {
        var rank = 1;
        while (rank < 10 && cp >= (int)Math.Pow(5.0 * (rank + 1) * (rank + 2), 2))
            rank++;
        return rank;
    }

    /// <summary>
    /// Copies the reported script's saved options (Skua's options\&lt;OptionsStorage&gt;.cfg, "Category:Name=value"
    /// lines) as scenario options: "Options:Name" becomes "Name", other categories stay "Category:Name". Keys that may
    /// hold an account or player name, and values equal to the character's name, are left out.
    /// </summary>
    private void ReadScriptOptions(string reportedScript, JObject target, List<string> notes)
    {
        var cfgPath = (Bot.Config!.Get<string>("ScriptOptionsFile") ?? "").Trim();
        if (cfgPath.Length == 0)
        {
            if (reportedScript.Length == 0)
                return;
            var scriptPath = Path.IsPathRooted(reportedScript) ? reportedScript : Path.Combine(ClientFileSources.SkuaScriptsDIR, reportedScript);
            if (!File.Exists(scriptPath))
                return; // already noted by ResolveReportedScript
            var storage = Regex.Match(File.ReadAllText(scriptPath), @"OptionsStorage\s*(?:=|=>)\s*""([^""]+)""");
            if (!storage.Success)
                return; // the script has no saved options
            cfgPath = Path.Combine(ClientFileSources.SkuaOptionsDIR, storage.Groups[1].Value + ".cfg");
        }
        if (!File.Exists(cfgPath))
        {
            notes.Add($"No saved options file ({Path.GetFileName(cfgPath)}): the script's defaults apply.");
            return;
        }

        var self = Bot.Player.Username ?? "";
        var removed = 0;
        foreach (var line in File.ReadAllLines(cfgPath))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            var colon = key.IndexOf(':');
            var category = colon > 0 ? key[..colon] : "";
            var name = colon > 0 ? key[(colon + 1)..] : key;
            if (name.Length == 0)
                continue;
            if (PrivateOptionKey.IsMatch(name) || (self.Length > 0 && string.Equals(value.Trim(), self, StringComparison.OrdinalIgnoreCase)))
            {
                removed++;
                continue;
            }
            target[string.Equals(category, "Options", StringComparison.OrdinalIgnoreCase) || category.Length == 0 ? name : $"{category}:{name}"] = value;
        }
        if (removed > 0)
            notes.Add($"{removed} script option(s) that may hold account or player names were left out.");
    }

    private static string ClientVersion()
    {
        try { return ClientFileSources.DisplayVersion; }
        catch { return "unknown"; }
    }
}
