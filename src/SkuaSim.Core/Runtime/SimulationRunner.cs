using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Skua.Core.Interfaces;
using SkuaSim.Core.Api;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Data;
using SkuaSim.Core.Hosting;
using SkuaSim.Core.Proxies;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.Runtime;

public sealed class SimulationRunOptions
{
    public required string ScriptPath { get; init; }
    public Scenario Scenario { get; init; } = new();
    public int? SeedOverride { get; init; }
    public string? GameDataPath { get; init; }
    public string? QuestDataPath { get; init; }
    public string? TraceOutputPath { get; init; }
    public IReadOnlyList<string> IncludeRoots { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Orchestrates compiling a Skua script, wiring up the simulated Skua API and world,
/// executing ScriptMain on the virtual clock, and generating a SimulationReport.
/// </summary>
public sealed class SimulationRunner
{
    private readonly SkuaInstallation _installation;

    public SimulationRunner(SkuaInstallation installation)
    {
        _installation = installation;
        SkuaAssemblyResolver.Register(installation.ProbeDirectories);
    }

    public SimulationReport Run(SimulationRunOptions options)
    {
        var realSw = Stopwatch.StartNew();
        var scriptFullPath = Path.GetFullPath(options.ScriptPath);
        var scenario = options.Scenario;
        var seed = options.SeedOverride ?? scenario.Seed ?? 42;

        var compiler = new ScriptCompiler(_installation, new CompilerSettings
        {
            UseVirtualTime = true,
            IncludeRoots = options.IncludeRoots
        });

        var compileResult = compiler.Compile(scriptFullPath);
        if (!compileResult.Success || compileResult.Main == null)
        {
            return new SimulationReport
            {
                ScriptPath = scriptFullPath,
                ScenarioName = scenario.Name,
                Seed = seed,
                Outcome = SimulationOutcome.CompileError,
                StopReason = "compilation failed",
                CompileDuration = compileResult.Elapsed,
                RealDuration = realSw.Elapsed,
                CompileErrors = compileResult.Errors
            };
        }

        // Load compiled assemblies into an isolated per-run AssemblyLoadContext so static singletons
        // (such as CoreBots.Instance) start completely fresh on every simulation run.
        var loadContext = new SimScriptLoadContext();
        Assembly? mainAssembly = null;
        foreach (var unit in compileResult.Units)
        {
            var asm = loadContext.LoadUnit(unit.Image, unit.Pdb);
            if (unit == compileResult.Main)
                mainAssembly = asm;
        }

        var gameDataPath = options.GameDataPath ?? FindDefaultGameDataPath();
        var questDataPath = options.QuestDataPath ?? _installation.QuestDataFile;
        var catalog = GameDataCatalog.Load(gameDataPath, questDataPath);

        var clock = new SimClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))
        {
            MaxVirtualMs = (long)(Math.Max(0.1, scenario.Limits.MaxVirtualMinutes) * 60_000)
        };

        using var trace = new SimulationTrace(options.TraceOutputPath, () => clock.NowMs, ms => clock.Format(ms));
        SimServiceProvider.Attach(trace);

        var ctx = new SimContext(scenario, catalog, clock, trace, seed, _installation.Flavor)
        {
            LoadedScriptPath = scriptFullPath
        };

        foreach (var hint in compileResult.Units.SelectMany(u => u.MonsterHints))
        {
            var (normMap, _) = World.SimWorld.ParseMapRequest(hint.Map);
            if (string.IsNullOrEmpty(normMap))
                continue;
            if (!ctx.ScriptMonsterHints.TryGetValue(normMap, out var list))
                ctx.ScriptMonsterHints[normMap] = list = new List<(string Cell, string MonsterName)>();
            foreach (var singleMon in hint.MonsterName.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (singleMon != "*" && !list.Any(x => string.Equals(x.MonsterName, singleMon, StringComparison.OrdinalIgnoreCase)))
                    list.Add((hint.Cell, singleMon));
            }
        }

        foreach (var shopHint in compileResult.Units.SelectMany(u => u.ShopHints))
        {
            if (!ctx.ScriptShopHints.TryGetValue(shopHint.ShopId, out var items))
                ctx.ScriptShopHints[shopHint.ShopId] = items = new List<string>();
            if (!items.Contains(shopHint.ItemName, StringComparer.OrdinalIgnoreCase))
                items.Add(shopHint.ItemName);
        }

        var bot = BuildBotProxy(ctx);
        ctx.Bot = bot;
        typeof(IScriptInterface).GetProperty(nameof(IScriptInterface.Instance), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, bot);

        ctx.World.InitializeFromScenario();
        var initialSnapshot = ctx.World.Snapshot();

        // Find and instantiate the script class containing ScriptMain(IScriptInterface).
        var (scriptInstance, scriptMainMethod) = CreateScriptInstance(mainAssembly!, ctx);

        var outcome = SimulationOutcome.Completed;
        string? exceptionText = null;

        Exception? threadEx = null;
        var scriptDone = new ManualResetEventSlim(false);
        var scriptThread = new Thread(() =>
        {
            clock.MainThreadId = Environment.CurrentManagedThreadId;
            trace.MainThreadId = clock.MainThreadId;
            SkuaSim.Runtime.SimHooks.Source = clock;
            try
            {
                scriptMainMethod.Invoke(scriptInstance, new object?[] { bot });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                threadEx = tie.InnerException;
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
            finally
            {
                scriptDone.Set();
            }
        })
        {
            Name = "SkuaSim Script Thread",
            IsBackground = true
        };

        scriptThread.Start();

        var maxRealMs = (long)(Math.Max(1.0, scenario.Limits.MaxRealSeconds) * 1000);
        var stallRealMs = (long)(Math.Max(2.0, scenario.Limits.StallRealSeconds) * 1000);

        while (!scriptDone.Wait(50))
        {
            if (realSw.ElapsedMilliseconds >= maxRealMs)
            {
                ctx.RequestStop($"real-time limit reached ({scenario.Limits.MaxRealSeconds:F1}s)");
                break;
            }

            var idleMs = (DateTime.UtcNow - ctx.LastApiActivityUtc).TotalMilliseconds;
            if (idleMs >= stallRealMs)
            {
                ctx.RequestStop($"script stalled without calling any Skua API for {scenario.Limits.StallRealSeconds:F1}s");
                break;
            }
        }

        // Give the script thread a short grace window to unwind after StopRequested.
        scriptDone.Wait(1500);

        try
        {
            ctx.CleanupPhase = true;
            ctx.RaiseEvent("ScriptStopping", threadEx is SimulationStopException ? null : threadEx);
        }
        catch { }
        finally
        {
            SkuaSim.Runtime.SimHooks.Source = null;
            try { loadContext.Unload(); } catch { }
        }

        if (threadEx is SimulationStopException stopEx)
        {
            outcome = stopEx.Reason.Contains("script called", StringComparison.OrdinalIgnoreCase)
                ? SimulationOutcome.StoppedByScript
                : SimulationOutcome.LimitReached;
        }
        else if (threadEx != null)
        {
            outcome = SimulationOutcome.ScriptException;
            exceptionText = threadEx.ToString();
            trace.Error($"Unhandled script exception: {threadEx.GetType().Name}: {threadEx.Message}");
        }
        else if (ctx.StopRequested)
        {
            outcome = (ctx.StopReason?.Contains("script called", StringComparison.OrdinalIgnoreCase) == true)
                ? SimulationOutcome.StoppedByScript
                : SimulationOutcome.LimitReached;
        }

        trace.Flush();

        return new SimulationReport
        {
            ScriptPath = scriptFullPath,
            ScenarioName = scenario.Name,
            Seed = seed,
            Outcome = outcome,
            StopReason = ctx.StopReason,
            ExceptionText = exceptionText,
            CompileDuration = compileResult.Elapsed,
            RealDuration = realSw.Elapsed,
            VirtualDuration = TimeSpan.FromMilliseconds(clock.NowMs),
            TotalApiCalls = trace.TotalApiCalls,
            Kills = ctx.World.Kills,
            Deaths = ctx.World.Deaths,
            DropsPicked = ctx.World.DropsPicked,
            QuestsAccepted = ctx.World.QuestsAcceptedCount,
            QuestsCompleted = ctx.World.QuestsCompletedCount,
            MapsVisited = ctx.World.MapsVisited.OrderBy(m => m).ToList(),
            SynthesizedMaps = ctx.World.SynthesizedMapsVisited.OrderBy(m => m).ToList(),
            KillsByMonster = new Dictionary<string, int>(ctx.World.KillsByMonster),
            DropsByItem = new Dictionary<string, int>(ctx.World.DropsByItem),
            QuestTurnIns = new Dictionary<int, int>(ctx.World.QuestTurnIns),
            InitialState = initialSnapshot,
            FinalState = ctx.World.Snapshot(),
            SynthesizedData = trace.Synthesized.Values.OrderBy(s => s.FirstVirtualMs).ToList(),
            UnimplementedCalls = trace.Unimplemented.ToDictionary(k => k.Key, k => k.Value),
            TopApiCalls = trace.CallCounts.OrderByDescending(kv => kv.Value).Take(25).ToDictionary(k => k.Key, k => k.Value),
            ScriptLogs = trace.ScriptLog.ToList(),
            Warnings = trace.Warnings.ToList(),
            RecentTimeline = trace.RecentTimeline.ToList(),
            TraceFilePath = options.TraceOutputPath
        };
    }

    private static IScriptInterface BuildBotProxy(SimContext ctx)
    {
        var hub = ctx.Proxies;
        var botHandler = new BotApi(ctx);
        var killHandler = new KillApi(ctx);
        var aurasHandler = new AurasApi(ctx);
        var configHandler = new ConfigApi(ctx);

        botHandler.Flash = hub.Create<IFlashUtil>(new FlashApi(ctx), "Bot.Flash");
        botHandler.Manager = hub.Create<IScriptStatus>(new ManagerApi(ctx), "Bot.Manager");
        botHandler.Stats = hub.Create<IScriptBotStats>(new StatsApi(ctx), "Bot.Stats");
        botHandler.Self = hub.Create<IScriptSelfAuras>(aurasHandler, "Bot.Self");
        botHandler.Target = hub.Create<IScriptTargetAuras>(aurasHandler, "Bot.Target");
        botHandler.UltraBossHelper = hub.Create<IUltraBossHelper>(new UltraBossHelperApi(ctx), "Bot.UltraBossHelper");
        botHandler.Combat = hub.Create<IScriptCombat>(new CombatApi(ctx), "Bot.Combat");
        botHandler.Kill = hub.Create<IScriptKill>(killHandler, "Bot.Kill");
        botHandler.Hunt = hub.Create<IScriptHunt>(new HuntApi(ctx, killHandler), "Bot.Hunt");
        botHandler.Drops = hub.Create<IScriptDrop>(new DropsApi(ctx), "Bot.Drops");
        botHandler.Events = hub.Create<IScriptEvent>(new EventsApi(ctx), "Bot.Events");
        botHandler.Reputation = hub.Create<IScriptFaction>(new ReputationApi(ctx), "Bot.Reputation");
        botHandler.House = hub.Create<IScriptHouseInv>(new HouseInvApi(ctx), "Bot.House");
        botHandler.Inventory = hub.Create<IScriptInventory>(new InventoryApi(ctx), "Bot.Inventory");
        botHandler.TempInv = hub.Create<IScriptTempInv>(new TempInvApi(ctx), "Bot.TempInv");
        botHandler.Bank = hub.Create<IScriptBank>(new BankApi(ctx), "Bot.Bank");
        botHandler.InvHelper = hub.Create<IScriptInventoryHelper>(new InvHelperApi(ctx), "Bot.InvHelper");
        botHandler.Lite = hub.Create<IScriptLite>(new LiteApi(ctx), "Bot.Lite");
        botHandler.Options = hub.Create<IScriptOption>(new OptionsApi(ctx), "Bot.Options");
        botHandler.Map = hub.Create<IScriptMap>(new MapApi(ctx), "Bot.Map");
        botHandler.Monsters = hub.Create<IScriptMonster>(new MonstersApi(ctx), "Bot.Monsters");
        botHandler.Player = hub.Create<IScriptPlayer>(new PlayerApi(ctx), "Bot.Player");
        botHandler.Quests = hub.Create<IScriptQuest>(new QuestsApi(ctx), "Bot.Quests");
        botHandler.Send = hub.Create<IScriptSend>(new SendApi(ctx), "Bot.Send");
        botHandler.Shops = hub.Create<IScriptShop>(new ShopsApi(ctx), "Bot.Shops");
        botHandler.Skills = hub.Create<IScriptSkill>(new SkillsApi(ctx), "Bot.Skills");
        botHandler.Wait = hub.Create<IScriptWait>(new WaitApi(ctx), "Bot.Wait");
        botHandler.Servers = hub.Create<IScriptServers>(new ServersApi(ctx), "Bot.Servers");
        botHandler.Handlers = hub.Create<IScriptHandlers>(new HandlersApi(ctx), "Bot.Handlers");
        botHandler.Accounts = hub.Create<IScriptAccounts>(new AccountsApi(ctx), "Bot.Accounts");
        botHandler.Config = hub.Create<IScriptOptionContainer>(configHandler, "Bot.Config");

        return hub.Create<IScriptInterface>(botHandler, "Bot");
    }

    private static (object Instance, MethodInfo Method) CreateScriptInstance(Assembly assembly, SimContext ctx)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;
            var method = type.GetMethods(Flags)
                .FirstOrDefault(m => m.Name == "ScriptMain" && m.GetParameters().Length == 1);
            if (method == null)
                continue;

            var instance = Activator.CreateInstance(type)
                           ?? throw new InvalidOperationException($"Failed to instantiate script class {type.FullName}.");

            PopulateScriptConfig(type, instance, ctx);
            return (instance, method);
        }

        throw new InvalidOperationException("No class with a ScriptMain(IScriptInterface) method was found in the compiled script.");
    }

    private static void PopulateScriptConfig(Type scriptType, object scriptInstance, SimContext ctx)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var configState = ProxyHub.GetState(ctx.Bot is IScriptInterface b ? b.Config : null);
        if (configState?.Handler is not ConfigApi config)
            return;

        // Discover Options list on the script class or any included fields (like Core.Options).
        var optionsField = scriptType.GetField("Options", Flags);
        var optionsProp = scriptType.GetProperty("Options", Flags);
        var rawOptions = optionsField?.GetValue(scriptInstance) ?? optionsProp?.GetValue(scriptInstance);
        if (rawOptions is IEnumerable<IOption> opts)
        {
            config.Options.AddRange(opts);
            config.SetDefaults();
        }
    }

    public string? FindDefaultGameDataPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "gamedata.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "gamedata.json"),
            Path.Combine(_installation.UserDataDirectory, "SkuaSim", "gamedata.json")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed class SimScriptLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, Assembly> _units = new(StringComparer.OrdinalIgnoreCase);

        public SimScriptLoadContext() : base("SkuaSimRun", isCollectible: true) { }

        public Assembly LoadUnit(byte[] pe, byte[]? pdb)
        {
            using var peStream = new MemoryStream(pe);
            using var pdbStream = pdb != null ? new MemoryStream(pdb) : null;
            var asm = LoadFromStream(peStream, pdbStream);
            var name = asm.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                _units[name] = asm;
            return asm;
        }

        protected override Assembly? Load(AssemblyName assemblyName) =>
            assemblyName.Name != null && _units.TryGetValue(assemblyName.Name, out var unit) ? unit : null;
    }
}
