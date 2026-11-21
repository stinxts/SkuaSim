using System.Runtime.CompilerServices;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Data;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;
using SkuaSim.Core.World;
using SkuaSim.UI.Models;

namespace SkuaSim.UI.Services;

/// <summary>
/// UI-facing coordinator for workspace discovery, script compilation, and simulation lifecycle.
/// Methods touching Skua-bound SimulationRunner APIs disable inlining until SkuaHostBootstrapper succeeds.
/// </summary>
public sealed class SimulationHostService : IDisposable
{
    private SimulationRunner? _runner;
    private SimulationSession? _activeSession;
    private GameDataCatalog? _cachedCatalog;

    private static readonly string[] BuiltInCommonClasses =
    {
        "ArchPaladin", "Blaze Binder", "Chaos Avenger", "Dragon of Time", "Healer",
        "Imperial Chunin", "Kathool Adept", "Legion Revenant", "Lord of Order",
        "Mage", "Master Ranger", "Oracle", "Rogue", "Scarlet Sorceress",
        "Shaman", "StoneCrusher", "Void Highlord", "Warrior", "Yami no Ronin"
    };

    public bool IsReady => SkuaHostBootstrapper.Installation != null;
    public string? ErrorMessage => SkuaHostBootstrapper.StartupError;
    public string ClientFlavor => SkuaHostBootstrapper.Installation?.Flavor ?? "Not detected";
    public string? ScriptsDirectory => SkuaHostBootstrapper.Installation?.ScriptsDirectory;
    public string? AssembliesDirectory => SkuaHostBootstrapper.Installation?.AssembliesDirectory;
    public string? UserDataDirectory => SkuaHostBootstrapper.Installation?.UserDataDirectory;
    public SimulationSession? ActiveSession => _activeSession;

    public Dictionary<string, string>? TryLoadSkuaCfgOptions(string? optionsStorage)
    {
        if (string.IsNullOrWhiteSpace(optionsStorage) || string.IsNullOrWhiteSpace(UserDataDirectory))
            return null;

        var cfgPath = Path.Combine(UserDataDirectory, "options", $"{optionsStorage.Trim()}.cfg");
        if (!File.Exists(cfgPath))
            return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var rawLine in File.ReadAllLines(cfgPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                    continue;
                var eq = rawLine.IndexOf('=');
                if (eq <= 0)
                    continue;
                var left = rawLine[..eq].Trim();
                var val = rawLine[(eq + 1)..];
                map[left] = val;

                var colon = left.IndexOf(':');
                if (colon > 0 && colon + 1 < left.Length)
                {
                    var keyOnly = left[(colon + 1)..].Trim();
                    if (!map.ContainsKey(keyOnly))
                        map[keyOnly] = val;
                }
            }
            return map;
        }
        catch
        {
            return null;
        }
    }

    public bool ReconfigureSkua(string assembliesDirectory, string? userDataDirectory = null)
    {
        DisposeActiveSession();
        _runner = null;
        _cachedCatalog = null;
        SkuaHostBootstrapper.Initialize(assembliesDirectory, userDataDirectory);
        return IsReady;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<ClassCatalogOption> DiscoverKnownClasses(bool forceReload = false)
    {
        var list = new List<ClassCatalogOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var catalog = GetOrLoadCatalog(forceReload);
            if (catalog.Captured.Classes is { Count: > 0 })
            {
                foreach (var cls in catalog.Captured.Classes.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(cls.Name) || !seen.Add(cls.Name.Trim()))
                        continue;
                    list.Add(new ClassCatalogOption(
                        cls.Name.Trim(),
                        FromDatabase: true,
                        StatModel: string.IsNullOrWhiteSpace(cls.InferredStatModel) ? "Auto" : cls.InferredStatModel,
                        SkillCount: cls.Skills?.Count ?? 0,
                        Samples: cls.SamplesObserved));
                }
            }
        }
        catch { }

        foreach (var builtIn in BuiltInCommonClasses)
        {
            if (seen.Add(builtIn))
                list.Add(new ClassCatalogOption(builtIn, FromDatabase: false, StatModel: "Archetype", SkillCount: 6, Samples: 0));
        }

        return list;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public ScenarioStatsPreview? PreviewScenarioStats(Scenario scenario)
    {
        if (!IsReady)
            return null;
        try
        {
            var catalog = GetOrLoadCatalog(forceReload: false);
            return StatEngine.PreviewScenarioStats(scenario, catalog);
        }
        catch
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private GameDataCatalog GetOrLoadCatalog(bool forceReload = false)
    {
        if (_cachedCatalog != null && !forceReload)
            return _cachedCatalog;

        string? gameDataPath = null;
        if (IsReady)
        {
            try { gameDataPath = EnsureRunner().FindDefaultGameDataPath(); } catch { }
        }
        gameDataPath ??= FindWorkspaceGameDataPath();

        // QuestData.json is needed for the quest-unlock catalog (quest pets…); missing file = collected quests only.
        var questDataPath = SkuaHostBootstrapper.Installation?.QuestDataFile;
        _cachedCatalog = GameDataCatalog.Load(gameDataPath, questDataPath != null && File.Exists(questDataPath) ? questDataPath : null);
        return _cachedCatalog;
    }

    /// <summary>Every item that opens quests (quest pets first), for the scenario editor.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IReadOnlyList<UnlockItem> GetUnlockItems()
    {
        try
        {
            return GetOrLoadCatalog(forceReload: false).Unlocks.Items
                .OrderByDescending(i => i.IsPet).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<UnlockItem>();
        }
    }

    private static string? FindWorkspaceGameDataPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), "data", "gamedata.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "gamedata.json")
        };
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && cur != null; i++, cur = cur.Parent)
            candidates.Add(Path.Combine(cur.FullName, "data", "gamedata.json"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public Task<IReadOnlyList<ScriptEntry>> DiscoverScriptsAsync() => Task.Run<IReadOnlyList<ScriptEntry>>(() =>
    {
        var list = new List<ScriptEntry>();
        var scriptsDir = ScriptsDirectory;
        if (string.IsNullOrWhiteSpace(scriptsDir) || !Directory.Exists(scriptsDir))
            return list;

        try
        {
            foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(scriptsDir, file);
                if (rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(new ScriptEntry(rel, Path.GetFullPath(file)));
            }
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        return list;
    });

    public IReadOnlyList<ScenarioEntry> DiscoverScenarios()
    {
        var entries = new List<ScenarioEntry>
        {
            new("(Default — empty scenario)", null)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in FindScenarioDirectories())
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var full = Path.GetFullPath(file);
                    if (seen.Add(full))
                        entries.Add(new ScenarioEntry(Path.GetFileName(full), full));
                }
            }
            catch { }
        }

        return entries;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<ScriptCompileResult> CompileAsync(string scriptPath)
    {
        var runner = EnsureRunner();
        return Task.Run(() => runner.Compile(scriptPath));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<ScriptInspectionResult> InspectScriptAsync(string scriptPath)
    {
        var runner = EnsureRunner();
        return Task.Run(() => runner.InspectScript(scriptPath));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SimulationSession StartSimulation(string scriptPath, string? scenarioPath, int? seedOverride, double speedMultiplier)
    {
        var scenario = !string.IsNullOrWhiteSpace(scenarioPath) && File.Exists(scenarioPath)
            ? LoadScenario(scenarioPath)
            : new Scenario { Name = Path.GetFileNameWithoutExtension(scriptPath) };

        return StartSimulation(scriptPath, scenario, seedOverride, speedMultiplier);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SimulationSession StartSimulation(string scriptPath, Scenario scenario, int? seedOverride, double speedMultiplier)
    {
        DisposeActiveSession();

        var runner = EnsureRunner();
        var session = runner.Start(
            new SimulationRunOptions
            {
                ScriptPath = scriptPath,
                Scenario = scenario,
                SeedOverride = seedOverride
            },
            new SimulationSessionOptions
            {
                InitialSpeedMultiplier = speedMultiplier
            });

        _activeSession = session;
        return session;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Scenario LoadScenarioFile(string path) => Scenario.Load(path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Scenario LoadScenario(string path) => Scenario.Load(path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SimulationRunner EnsureRunner()
    {
        if (_runner != null)
            return _runner;

        var installation = SkuaHostBootstrapper.Installation
                           ?? throw new InvalidOperationException(SkuaHostBootstrapper.StartupError ?? "Skua/VibeSkua installation was not found.");
        _runner = new SimulationRunner(installation);
        return _runner;
    }

    public void DisposeActiveSession()
    {
        var session = Interlocked.Exchange(ref _activeSession, null);
        if (session == null)
            return;
        try { session.Dispose(); } catch { }
    }

    public void Dispose() => DisposeActiveSession();

    private static IEnumerable<string> FindScenarioDirectories()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "scenarios"),
            Path.Combine(AppContext.BaseDirectory, "scenarios")
        };

        var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && probed.Add(Path.GetFullPath(dir)))
                yield return dir;
        }

        // Also walk upward from BaseDirectory when launched via bin/Debug/net10.0.
        var cur = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && cur != null; i++, cur = cur.Parent)
        {
            var scenariosDir = Path.Combine(cur.FullName, "scenarios");
            if (Directory.Exists(scenariosDir) && probed.Add(Path.GetFullPath(scenariosDir)))
                yield return scenariosDir;
        }
    }
}
