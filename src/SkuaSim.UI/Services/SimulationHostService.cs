using System.Runtime.CompilerServices;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;
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

    public bool IsReady => SkuaHostBootstrapper.Installation != null;
    public string? ErrorMessage => SkuaHostBootstrapper.StartupError;
    public string ClientFlavor => SkuaHostBootstrapper.Installation?.Flavor ?? "Not detected";
    public string? ScriptsDirectory => SkuaHostBootstrapper.Installation?.ScriptsDirectory;
    public string? AssembliesDirectory => SkuaHostBootstrapper.Installation?.AssembliesDirectory;
    public SimulationSession? ActiveSession => _activeSession;

    public bool ReconfigureSkua(string assembliesDirectory, string? userDataDirectory = null)
    {
        DisposeActiveSession();
        _runner = null;
        SkuaHostBootstrapper.Initialize(assembliesDirectory, userDataDirectory);
        return IsReady;
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
    public SimulationSession StartSimulation(string scriptPath, string? scenarioPath, int? seedOverride, double speedMultiplier)
    {
        DisposeActiveSession();

        var runner = EnsureRunner();
        var scenario = !string.IsNullOrWhiteSpace(scenarioPath) && File.Exists(scenarioPath)
            ? LoadScenario(scenarioPath)
            : new Scenario { Name = Path.GetFileNameWithoutExtension(scriptPath) };

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
