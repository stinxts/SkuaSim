using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SkuaSim.Collector.Models;

namespace SkuaSim.Core.Data;

/// <summary>
/// Maps, shops and quests the Skua scripts use, read from literal arguments of the CoreBots/CoreStory calls that name
/// them. Only the syntax tree is parsed (no compile), so the whole Scripts folder scans in seconds.
/// </summary>
public sealed class ScriptUsage
{
    /// <summary>Map name (lowercase, no room suffix) → scripts (paths relative to the Scripts folder) that use it.</summary>
    public Dictionary<string, SortedSet<string>> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shop ID → scripts that load it.</summary>
    public Dictionary<int, SortedSet<string>> Shops { get; } = new();

    /// <summary>Shop ID → map named next to it (BuyItem(map, shopID, ...)) → number of calls.</summary>
    public Dictionary<int, Dictionary<string, int>> ShopMaps { get; } = new();

    /// <summary>Quest ID → scripts that use it.</summary>
    public Dictionary<int, SortedSet<string>> Quests { get; } = new();

    public int FilesScanned { get; set; }
}

public static class ScriptUsageScanner
{
    /// <summary>Folders under Scripts\ that only hold copies of other scripts.</summary>
    private static readonly string[] SkippedFolders = { "Cached-Scripts" };

    private static readonly Regex MapName = new(@"^[a-z0-9_]+$", RegexOptions.CultureInvariant);

    // Calls whose first argument is the map: CoreBots.Join/KillMonster/HuntMonster/HuntMonsterMapID/BuyItem, IMap.Join.
    private static readonly HashSet<string> MapFirst = new(StringComparer.Ordinal)
        { "Join", "KillMonster", "HuntMonster", "HuntMonsterMapID", "KillMonsterMapID", "BuyItem" };

    // CoreStory calls: (int QuestID, string MapName, ...).
    private static readonly HashSet<string> StoryQuestMap = new(StringComparer.Ordinal) { "KillQuest", "MapItemQuest", "BuyQuest" };

    // Calls whose first argument is a quest ID.
    private static readonly HashSet<string> QuestFirst = new(StringComparer.Ordinal)
        { "EnsureAccept", "EnsureComplete", "EnsureCompleteChoose", "EnsureCompleteMulti", "ChainComplete", "ChainQuest", "KillQuest", "MapItemQuest", "BuyQuest" };

    public static ScriptUsage Scan(string scriptsDirectory)
    {
        var usage = new ScriptUsage();
        if (!Directory.Exists(scriptsDirectory))
            return usage;
        var files = Directory.EnumerateFiles(scriptsDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(scriptsDirectory, f).Replace('\\', '/'))
            .Where(rel => !SkippedFolders.Any(s => rel.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(rel => rel, StringComparer.Ordinal);
        foreach (var rel in files)
            ScanSource(rel, File.ReadAllText(Path.Combine(scriptsDirectory, rel)), usage);
        return usage;
    }

    public static void ScanSource(string scriptName, string source, ScriptUsage usage)
    {
        usage.FilesScanned++;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (method, owner) = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => (ma.Name.Identifier.ValueText, LastName(ma.Expression)),
                IdentifierNameSyntax id => (id.Identifier.ValueText, null),
                _ => (null, null)
            };
            if (method == null)
                continue;
            var args = inv.ArgumentList.Arguments;

            if (MapFirst.Contains(method) && args.Count > 0 && owner is not ("string" or "String"))
                AddMap(usage, scriptName, Str(args[0].Expression));
            if (StoryQuestMap.Contains(method))
                AddMap(usage, scriptName, Arg(args, 1, "MapName") is { } m ? Str(m) : null);

            if (method == "BuyItem" && args.Count >= 2 && Int(args[1].Expression) is > 0 and var shopId)
            {
                Add(usage.Shops, shopId, scriptName);
                if (Normalize(Str(args[0].Expression)) is { } shopMap)
                {
                    var maps = usage.ShopMaps.TryGetValue(shopId, out var d) ? d : usage.ShopMaps[shopId] = new(StringComparer.OrdinalIgnoreCase);
                    maps[shopMap] = maps.GetValueOrDefault(shopMap) + 1;
                }
            }
            if (method == "Load" && owner == "Shops" && args.Count == 1 && Int(args[0].Expression) is > 0 and var loaded)
                Add(usage.Shops, loaded, scriptName);

            if (method == "RegisterQuests" || (method is "Load" or "EnsureLoad" && owner == "Quests"))
                foreach (var a in args)
                    AddQuests(usage, scriptName, a.Expression);
            else if (QuestFirst.Contains(method) && args.Count > 0)
                AddQuests(usage, scriptName, Arg(args, 0, "QuestID") ?? Arg(args, 0, "questID"));
        }
    }

    /// <summary>Lowercase map name without a room suffix ("Lair-1e9" → "lair"), or null when it is not a plain map name.</summary>
    public static string? Normalize(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
            return null;
        var name = map.Trim().ToLowerInvariant();
        var dash = name.IndexOf('-');
        if (dash > 0)
            name = name[..dash];
        return MapName.IsMatch(name) ? name : null;
    }

    private static void AddMap(ScriptUsage usage, string script, string? map)
    {
        if (Normalize(map) is { } name)
            Add(usage.Maps, name, script);
    }

    private static void AddQuests(ScriptUsage usage, string script, ExpressionSyntax? expr)
    {
        if (expr == null)
            return;
        IEnumerable<ExpressionSyntax> elements = expr switch
        {
            ImplicitArrayCreationExpressionSyntax a => a.Initializer.Expressions,
            ArrayCreationExpressionSyntax a when a.Initializer != null => a.Initializer.Expressions,
            CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
            _ => new[] { expr }
        };
        foreach (var e in elements)
            if (Int(e) is > 0 and var id)
                Add(usage.Quests, id, script);
    }

    private static void Add<TKey>(Dictionary<TKey, SortedSet<string>> map, TKey key, string script) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(script);
    }

    private static string? LastName(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        PredefinedTypeSyntax p => p.Keyword.ValueText,
        _ => null
    };

    private static ExpressionSyntax? Arg(SeparatedSyntaxList<ArgumentSyntax> args, int index, string name)
    {
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name);
        if (named != null)
            return named.Expression;
        return index < args.Count && args[index].NameColon == null ? args[index].Expression : null;
    }

    private static string? Str(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    private static int? Int(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NumericLiteralExpression) && lit.Token.Value is int v ? v : null;
}

/// <summary>
/// The work list of tools\skua\SkuaSim_Harvester.cs (layer A: visit maps, request official drop lists, load shops and
/// quests so the Collector records them). Each stop is one unit an army client claims; stops are ordered by priority.
/// </summary>
public sealed class HarvestPlan
{
    public const int CurrentVersion = 1;
    public const int QuestsPerStop = 20;
    public const string DefaultFileName = "harvest_plan.json";

    public int Version { get; set; } = CurrentVersion;
    public DateTime CreatedUtc { get; set; }
    public int ScriptsScanned { get; set; }
    public HarvestPlanTotals Totals { get; set; } = new();
    public List<HarvestStop> Stops { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static HarvestPlan FromJson(string json) => JsonSerializer.Deserialize<HarvestPlan>(json, Json) ?? new HarvestPlan();

    /// <summary>Default plan folder, shared with the Skua script's default: Documents\SkuaSim Harvest.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkuaSim Harvest");

    /// <summary>
    /// Builds the plan from what the scripts use minus what the collected data already has. A map is a stop when it was
    /// never captured, when monsters seen on it have no official drop list yet, or when a shop missing from the data is
    /// bought on it. Shops with no known map and missing quests become map-less stops (loaded from wherever the client is).
    /// </summary>
    public static HarvestPlan Build(ScriptUsage usage, GameDatabase db, DateTime createdUtc, int maxStops = 0)
    {
        var plan = new HarvestPlan { CreatedUtc = createdUtc, ScriptsScanned = usage.FilesScanned };
        var monstersById = db.Monsters.Values.Where(m => m.ID > 0).GroupBy(m => m.ID).ToDictionary(g => g.Key, g => g.First());

        var missingShops = usage.Shops.Keys.Where(id => !db.Shops.ContainsKey(id)).OrderBy(id => id).ToList();
        var shopsByMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var looseShops = new List<int>();
        foreach (var id in missingShops)
        {
            var map = usage.ShopMaps.TryGetValue(id, out var maps)
                ? maps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key
                : null;
            if (map == null)
                looseShops.Add(id);
            else
                (shopsByMap.TryGetValue(map, out var l) ? l : shopsByMap[map] = new List<int>()).Add(id);
        }

        var mapStops = new List<HarvestStop>();
        foreach (var name in usage.Maps.Keys.Union(shopsByMap.Keys, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scripts = usage.Maps.TryGetValue(name, out var s) ? s.Count : 0;
            var shops = shopsByMap.TryGetValue(name, out var sh) ? sh : new List<int>();
            var reasons = new List<string>();
            var captured = db.Maps.TryGetValue(name, out var map) ? map : null;
            var noDropList = 0;
            if (captured == null)
                reasons.Add("map not captured");
            else
            {
                noDropList = captured.Monsters.Values.Select(m => m.MonsterID).Distinct()
                    .Count(id => !monstersById.TryGetValue(id, out var mon) || mon.OfficialDropsObservedUtc == null);
                if (noDropList > 0)
                    reasons.Add($"{noDropList} monster(s) without an official drop list");
            }
            if (shops.Count > 0)
                reasons.Add($"{shops.Count} shop(s) not captured");
            if (reasons.Count == 0)
                continue;
            mapStops.Add(new HarvestStop
            {
                Key = "map:" + name,
                Map = name,
                Shops = shops,
                Scripts = scripts,
                Reasons = reasons,
                Priority = scripts * (captured == null ? 3 : 1) + noDropList + 2 * shops.Count
            });
        }

        plan.Stops.AddRange(mapStops.OrderByDescending(x => x.Priority).ThenBy(x => x.Map, StringComparer.Ordinal));

        foreach (var chunk in looseShops.Chunk(QuestsPerStop).Select((ids, i) => (ids, i)))
            plan.Stops.Add(new HarvestStop
            {
                Key = $"shops:{chunk.i + 1}",
                Shops = chunk.ids.ToList(),
                Scripts = chunk.ids.SelectMany(id => usage.Shops[id]).Distinct().Count(),
                Reasons = new List<string> { $"{chunk.ids.Length} shop(s) not captured, map unknown" }
            });

        var missingQuests = usage.Quests.Keys.Where(id => !db.Quests.ContainsKey(id)).OrderBy(id => id).ToList();
        foreach (var chunk in missingQuests.Chunk(QuestsPerStop).Select((ids, i) => (ids, i)))
            plan.Stops.Add(new HarvestStop
            {
                Key = $"quests:{chunk.i + 1}",
                Quests = chunk.ids.ToList(),
                Scripts = chunk.ids.SelectMany(id => usage.Quests[id]).Distinct().Count(),
                Reasons = new List<string> { $"{chunk.ids.Length} quest(s) not captured" }
            });

        if (maxStops > 0 && plan.Stops.Count > maxStops)
            plan.Stops = plan.Stops.Take(maxStops).ToList();

        plan.Totals = new HarvestPlanTotals
        {
            MapsUsed = usage.Maps.Count,
            MapsNotCaptured = mapStops.Count(m => m.Reasons.Contains("map not captured")),
            MapsWithDropGaps = mapStops.Count(m => m.Reasons.Any(r => r.Contains("official drop list", StringComparison.Ordinal))),
            ShopsUsed = usage.Shops.Count,
            ShopsNotCaptured = missingShops.Count,
            QuestsUsed = usage.Quests.Count,
            QuestsNotCaptured = missingQuests.Count
        };
        return plan;
    }
}

public sealed class HarvestPlanTotals
{
    public int MapsUsed { get; set; }
    public int MapsNotCaptured { get; set; }
    public int MapsWithDropGaps { get; set; }
    public int ShopsUsed { get; set; }
    public int ShopsNotCaptured { get; set; }
    public int QuestsUsed { get; set; }
    public int QuestsNotCaptured { get; set; }
}

public sealed class HarvestStop
{
    /// <summary>Stable ID used in the progress file ("map:lair", "shops:1", "quests:3").</summary>
    public string Key { get; set; } = "";

    /// <summary>Map to visit (cells + official drop lists); null = load the shops/quests from wherever the client is.</summary>
    public string? Map { get; set; }

    public List<int> Shops { get; set; } = new();
    public List<int> Quests { get; set; } = new();

    /// <summary>Number of scripts that use this map / these shops or quests.</summary>
    public int Scripts { get; set; }

    public int Priority { get; set; }
    public List<string> Reasons { get; set; } = new();
}
