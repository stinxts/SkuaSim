using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ScriptCompiler> _compilers = new(StringComparer.OrdinalIgnoreCase);

    public SimulationRunner(SkuaInstallation installation)
    {
        _installation = installation;
        SkuaAssemblyResolver.Register(installation.ProbeDirectories);
    }

    public SkuaInstallation Installation => _installation;

    public ScriptCompileResult Compile(string scriptPath, IReadOnlyList<string>? includeRoots = null) =>
        GetCompiler(useVirtualTime: true, includeRoots).Compile(Path.GetFullPath(scriptPath));

    public ScriptInspectionResult InspectScript(string scriptPath, IReadOnlyList<string>? includeRoots = null)
    {
        var scriptFullPath = Path.GetFullPath(scriptPath);
        var compileResult = Compile(scriptFullPath, includeRoots);
        if (!compileResult.Success || compileResult.Main == null)
        {
            return new ScriptInspectionResult
            {
                ScriptPath = scriptFullPath,
                CompileResult = compileResult,
                ErrorMessage = "Compilation failed."
            };
        }

        var loadContext = new SimScriptLoadContext();
        var instProp = typeof(IScriptInterface).GetProperty(nameof(IScriptInterface.Instance), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? dummyBot = null;

        try
        {
            var mainAssembly = LoadUnitsIntoContext(compileResult.Units, compileResult.Main, loadContext);
            var mainInfo = FindScriptMain(mainAssembly);
            if (mainInfo == null)
            {
                return new ScriptInspectionResult
                {
                    ScriptPath = scriptFullPath,
                    CompileResult = compileResult,
                    ErrorMessage = "No class with a ScriptMain(IScriptInterface) method was found in the compiled script."
                };
            }

            if (instProp != null && instProp.GetValue(null) == null)
            {
                var dummyClock = new SimClock(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
                using var dummyTrace = new SimulationTrace(null, () => dummyClock.NowMs, ms => dummyClock.Format(ms));
                var dummyCtx = new SimContext(new Scenario(), GameDataCatalog.Load(null, null), dummyClock, dummyTrace, 0, _installation.Flavor)
                {
                    LoadedScriptPath = scriptFullPath
                };
                dummyBot = BuildBotProxy(dummyCtx);
                instProp.SetValue(null, dummyBot);
            }

            var (scriptType, _) = mainInfo.Value;
            var instance = Activator.CreateInstance(scriptType)
                           ?? throw new InvalidOperationException($"Failed to instantiate script class {scriptType.FullName}.");

            var storage = ExtractOptionsStorage(scriptType, instance);
            var descriptors = new List<ScriptOptionDescriptor>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (memberName, isPrimary, opt) in DiscoverScriptOptions(scriptType, instance))
            {
                var name = opt.Name ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var category = !string.IsNullOrWhiteSpace(opt.Category) &&
                               (isPrimary || !string.Equals(opt.Category, "Options", StringComparison.OrdinalIgnoreCase))
                    ? opt.Category
                    : (isPrimary ? "Options" : memberName);
                if (!seen.Add($"{category}|{name}"))
                    continue;

                descriptors.Add(CreateOptionDescriptor(category, opt));
            }

            return new ScriptInspectionResult
            {
                ScriptPath = scriptFullPath,
                CompileResult = compileResult,
                ScriptClass = scriptType.FullName ?? scriptType.Name,
                OptionsStorage = storage,
                Options = descriptors
            };
        }
        catch (Exception ex)
        {
            return new ScriptInspectionResult
            {
                ScriptPath = scriptFullPath,
                CompileResult = compileResult,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
        finally
        {
            if (dummyBot != null && instProp != null && ReferenceEquals(instProp.GetValue(null), dummyBot))
                instProp.SetValue(null, null);
            try { loadContext.Unload(); } catch { }
        }
    }

    public SimulationReport Run(SimulationRunOptions options)
    {
        using var session = Start(options);
        return session.Completion.GetAwaiter().GetResult();
    }

    public SimulationSession Start(SimulationRunOptions options, SimulationSessionOptions? sessionOptions = null)
    {
        var scriptFullPath = Path.GetFullPath(options.ScriptPath);
        var seed = options.SeedOverride ?? options.Scenario.Seed ?? 42;
        return new SimulationSession(
            scriptFullPath,
            options.Scenario.Name,
            seed,
            sessionOptions ?? new SimulationSessionOptions(),
            session => ExecuteSession(options, scriptFullPath, seed, session));
    }

    private SimulationReport ExecuteSession(SimulationRunOptions options, string scriptFullPath, int seed, SimulationSession session)
    {
        var scenario = options.Scenario;
        var compileResult = Compile(scriptFullPath, options.IncludeRoots);
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
                RealDuration = session.CurrentActiveRealDuration(null),
                CompileErrors = compileResult.Errors
            };
        }

        if (session.StopRequested)
        {
            return new SimulationReport
            {
                ScriptPath = scriptFullPath,
                ScenarioName = scenario.Name,
                Seed = seed,
                Outcome = SimulationOutcome.LimitReached,
                StopReason = session.RequestedStopReason ?? "stopped by user before execution",
                CompileDuration = compileResult.Elapsed,
                RealDuration = session.CurrentActiveRealDuration(null)
            };
        }

        // Load compiled assemblies into an isolated per-run AssemblyLoadContext so static singletons
        // (such as CoreBots.Instance) start completely fresh on every simulation run.
        var loadContext = new SimScriptLoadContext();
        var mainAssembly = LoadUnitsIntoContext(compileResult.Units, compileResult.Main, loadContext);

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
        var instProp = typeof(IScriptInterface).GetProperty(nameof(IScriptInterface.Instance), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        instProp?.SetValue(null, bot);

        ctx.World.InitializeFromScenario();
        var initialSnapshot = ctx.World.Snapshot();

        // Find and instantiate the script class containing ScriptMain(IScriptInterface).
        var (scriptInstance, scriptMainMethod) = CreateScriptInstance(mainAssembly, ctx);

        session.AttachRuntime(ctx, clock, trace);

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
                clock.WaitIfPaused();
                if (!session.StopRequested)
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

        session.MarkExecutionStarted(clock);
        scriptThread.Start();

        var maxRealMs = (long)(Math.Max(1.0, scenario.Limits.MaxRealSeconds) * 1000);
        var stallRealMs = (long)(Math.Max(2.0, scenario.Limits.StallRealSeconds) * 1000);

        while (!scriptDone.Wait(50))
        {
            if (session.StopRequested)
            {
                ctx.RequestStop(session.RequestedStopReason ?? "stopped by user");
                break;
            }

            if (clock.IsPaused)
                continue;

            var activeRealMs = session.ActiveExecutionRealMs(clock);
            if (activeRealMs >= maxRealMs)
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
            if (instProp != null && ReferenceEquals(instProp.GetValue(null), bot))
                instProp.SetValue(null, null);
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
            RealDuration = session.CurrentActiveRealDuration(clock),
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

    private ScriptCompiler GetCompiler(bool useVirtualTime, IReadOnlyList<string>? includeRoots)
    {
        var roots = (includeRoots ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var key = $"{useVirtualTime}|{string.Join(";", roots)}";
        return _compilers.GetOrAdd(key, _ => new ScriptCompiler(_installation, new CompilerSettings
        {
            UseVirtualTime = useVirtualTime,
            IncludeRoots = roots
        }));
    }

    private static Assembly LoadUnitsIntoContext(IReadOnlyList<CompiledUnit> units, CompiledUnit mainUnit, SimScriptLoadContext loadContext)
    {
        Assembly? mainAssembly = null;
        foreach (var unit in units)
        {
            var asm = loadContext.LoadUnit(unit.Image, unit.Pdb);
            if (unit == mainUnit)
                mainAssembly = asm;
        }
        return mainAssembly ?? throw new InvalidOperationException("Failed to load main compiled script assembly.");
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

    private static (Type Type, MethodInfo Method)? FindScriptMain(Assembly assembly)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;
            var method = type.GetMethods(Flags)
                .FirstOrDefault(m => m.Name == "ScriptMain" && m.GetParameters().Length == 1);
            if (method != null)
                return (type, method);
        }

        return null;
    }

    private static (object Instance, MethodInfo Method) CreateScriptInstance(Assembly assembly, SimContext ctx)
    {
        var found = FindScriptMain(assembly)
                    ?? throw new InvalidOperationException("No class with a ScriptMain(IScriptInterface) method was found in the compiled script.");

        var (type, method) = found;
        var instance = Activator.CreateInstance(type)
                       ?? throw new InvalidOperationException($"Failed to instantiate script class {type.FullName}.");

        PopulateScriptConfig(type, instance, ctx);
        return (instance, method);
    }

    private static void PopulateScriptConfig(Type scriptType, object scriptInstance, SimContext ctx)
    {
        var configState = ProxyHub.GetState(ctx.Bot is IScriptInterface b ? b.Config : null);
        if (configState?.Handler is not ConfigApi config)
            return;

        foreach (var (memberName, isPrimary, opt) in DiscoverScriptOptions(scriptType, scriptInstance))
        {
            if (isPrimary)
            {
                if (!config.Options.Contains(opt))
                    config.Options.Add(opt);
            }
            else
            {
                if (!config.MultipleOptions.TryGetValue(memberName, out var list))
                    config.MultipleOptions[memberName] = list = new List<IOption>();
                if (!list.Contains(opt))
                    list.Add(opt);
            }

            if (!config.OptionValues.ContainsKey(opt))
                config.OptionValues[opt] = opt.DefaultValue?.ToString() ?? "";
        }
    }

    private static string? ExtractOptionsStorage(Type scriptType, object scriptInstance)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var field = scriptType.GetField("OptionsStorage", Flags);
        if (field?.FieldType == typeof(string))
            return field.GetValue(field.IsStatic ? null : scriptInstance) as string;

        var prop = scriptType.GetProperty("OptionsStorage", Flags);
        if (prop?.PropertyType == typeof(string) && prop.GetIndexParameters().Length == 0 && prop.GetMethod is { } getter)
            return prop.GetValue(getter.IsStatic ? null : scriptInstance) as string;

        return null;
    }

    private static IEnumerable<(string MemberName, bool IsPrimary, IOption Option)> DiscoverScriptOptions(Type scriptType, object scriptInstance)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var members = new List<(string Name, bool IsPrimary, IEnumerable<IOption> List)>();

        void TryCollectMember(string name, Func<object?> getter)
        {
            try
            {
                if (getter() is IEnumerable<IOption> opts)
                    members.Add((name, string.Equals(name, "Options", StringComparison.OrdinalIgnoreCase), opts));
            }
            catch { }
        }

        foreach (var field in scriptType.GetFields(Flags))
        {
            if (field.Name.Contains('<') || !typeof(IEnumerable<IOption>).IsAssignableFrom(field.FieldType))
                continue;
            TryCollectMember(field.Name, () => field.GetValue(field.IsStatic ? null : scriptInstance));
        }

        foreach (var prop in scriptType.GetProperties(Flags))
        {
            if (prop.GetIndexParameters().Length > 0 || !prop.CanRead || !typeof(IEnumerable<IOption>).IsAssignableFrom(prop.PropertyType))
                continue;
            if (prop.GetMethod is not { } getter)
                continue;
            TryCollectMember(prop.Name, () => prop.GetValue(getter.IsStatic ? null : scriptInstance));
        }

        foreach (var group in members.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var opt in group.List)
            {
                if (opt != null)
                    yield return (group.Name, group.IsPrimary, opt);
            }
        }
    }

    private static ScriptOptionDescriptor CreateOptionDescriptor(string category, IOption opt)
    {
        var type = opt.Type;
        var isEnum = type?.IsEnum == true || opt.DefaultValue is Enum;
        var allowed = type?.IsEnum == true ? (IReadOnlyList<string>)Enum.GetNames(type) : Array.Empty<string>();
        var valueType = isEnum
            ? "Enum"
            : type?.FullName ?? opt.DefaultValue?.GetType().FullName ?? "System.String";

        return new ScriptOptionDescriptor(
            category,
            opt.Name ?? "",
            string.IsNullOrWhiteSpace(opt.DisplayName) ? opt.Name ?? "" : opt.DisplayName,
            opt.Description ?? "",
            valueType,
            ToSafeScalarDefault(opt.DefaultValue, type),
            allowed);
    }

    private static object? ToSafeScalarDefault(object? value, Type? declaredType)
    {
        if (value == null)
            return null;
        if (declaredType?.IsEnum == true || value is Enum)
            return value.ToString();
        return value switch
        {
            bool or int or long or float or double or decimal or string => value,
            _ => value.ToString()
        };
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
