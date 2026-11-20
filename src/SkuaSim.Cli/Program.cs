using System.Runtime.CompilerServices;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Data;
using SkuaSim.Core.Hosting;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;

var installation = SkuaInstallation.Detect();
SkuaAssemblyResolver.Register(installation.ProbeDirectories);
return CliEntry.Execute(args, installation);

internal static class CliEntry
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Execute(string[] args, SkuaInstallation installation)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(installation);
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return cmd switch
        {
            "db-stats" or "stats" => RunDbStats(rest, installation),
            "compile" => RunCompile(rest, installation),
            "run" => RunSimulation(rest, installation),
            _ => RunUnknown(cmd, installation)
        };
    }

    private static int RunDbStats(string[] args, SkuaInstallation installation)
    {
        var runner = new SimulationRunner(installation);
        var gameDataPath = GetOption(args, "--gamedata") ?? runner.FindDefaultGameDataPath();
        var questDataPath = GetOption(args, "--questdata") ?? installation.QuestDataFile;

        var catalog = GameDataCatalog.Load(gameDataPath, questDataPath);
        var db = catalog.Captured;
        var totalKills = db.Monsters.Values.Sum(m => m.KillCount);
        var totalDrops = db.Monsters.Values.Sum(m => m.Drops.Values.Sum(d => d.DropEvents));

        Console.WriteLine("=== SkuaSim Environment & Database Summary ===");
        Console.WriteLine($"Client Flavor      : {installation.Flavor}");
        Console.WriteLine($"Assemblies Folder  : {installation.AssembliesDirectory}");
        Console.WriteLine($"Scripts Folder     : {installation.ScriptsDirectory}");
        Console.WriteLine($"Collector DB File  : {catalog.CapturedPath ?? "(none found)"}");
        Console.WriteLine($"QuestData File     : {catalog.QuestDataPath ?? "(none found)"}");
        Console.WriteLine();
        Console.WriteLine("--- Collector Database (gamedata.json) ---");
        Console.WriteLine($"Total Flushes      : {db.TotalFlushesMerged:N0}");
        Console.WriteLine($"Last Updated (UTC) : {db.LastUpdatedUtc:u}");
        Console.WriteLine($"Maps Cataloged     : {db.Maps.Count:N0}");
        Console.WriteLine($"Monsters Cataloged : {db.Monsters.Count:N0} ({totalKills:N0} kills)");
        Console.WriteLine($"Items Cataloged    : {db.Items.Count:N0} ({totalDrops:N0} drop events)");
        Console.WriteLine($"Shops Captured     : {db.Shops.Count:N0}");
        Console.WriteLine($"Quests Captured    : {db.Quests.Count:N0}");
        Console.WriteLine();
        Console.WriteLine("--- Static Game Catalog ---");
        Console.WriteLine($"QuestData Quests   : {catalog.QuestCount:N0}");
        Console.WriteLine($"Total Indexed Items: {catalog.ItemCount:N0}");
        return 0;
    }

    private static int RunCompile(string[] args, SkuaInstallation installation)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: missing script path. Usage: skuasim compile <script.cs>");
            return 1;
        }

        var scriptPath = ResolveScriptPath(args[0], installation);
        var compiler = new ScriptCompiler(installation);
        Console.WriteLine($"Compiling: {scriptPath}");
        var res = compiler.Compile(scriptPath);

        Console.WriteLine($"Elapsed  : {res.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Includes : {res.IncludedFiles.Count} file(s) ({string.Join(", ", res.IncludedFiles.Select(Path.GetFileName))})");
        Console.WriteLine($"Warnings : {res.WarningCount}");

        foreach (var note in res.Notes)
            Console.WriteLine($"Note     : {note}");

        if (!res.Success)
        {
            Console.Error.WriteLine($"FAILED with {res.Errors.Count} error(s):");
            foreach (var err in res.Errors)
                Console.Error.WriteLine($"  {err}");
            return 2;
        }

        var totalRewrites = res.Units.Sum(u => u.TimeHookRewrites);
        Console.WriteLine($"SUCCESS  : Main class [{string.Join(", ", res.Main!.ScriptMainClasses)}], {totalRewrites} virtual-clock hook(s) applied.");
        return 0;
    }

    private static int RunSimulation(string[] args, SkuaInstallation installation)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: missing script path. Usage: skuasim run <script.cs> [--scenario <file.json>] [--seed <int>] [--max-virtual-min <num>] [--max-real-sec <num>] [--max-no-progress-min <num>] [--report <path.md>] [--trace <path.jsonl>]");
            return 1;
        }

        var scriptPath = ResolveScriptPath(args[0], installation);
        var scenarioPath = GetOption(args, "--scenario") ?? GetOption(args, "-s");
        var scenario = !string.IsNullOrEmpty(scenarioPath) ? Scenario.Load(scenarioPath) : new Scenario();

        int? seedOverride = null;
        if (GetOption(args, "--seed") is { } seedStr && int.TryParse(seedStr, out var parsedSeed))
            seedOverride = parsedSeed;

        if (GetOption(args, "--max-virtual-min") is { } vMinStr &&
            double.TryParse(vMinStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vMin))
            scenario.Limits.MaxVirtualMinutes = vMin;

        if (GetOption(args, "--max-real-sec") is { } rSecStr &&
            double.TryParse(rSecStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rSec))
            scenario.Limits.MaxRealSeconds = rSec;

        if (GetOption(args, "--max-no-progress-min") is { } npStr &&
            double.TryParse(npStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var noProgressMin))
            scenario.Limits.MaxNoProgressMinutes = noProgressMin;

        var reportPath = GetOption(args, "--report") ?? GetOption(args, "-o");
        var tracePath = GetOption(args, "--trace");
        var gameDataPath = GetOption(args, "--gamedata");

        var runner = new SimulationRunner(installation);
        var report = runner.Run(new SimulationRunOptions
        {
            ScriptPath = scriptPath,
            Scenario = scenario,
            SeedOverride = seedOverride,
            GameDataPath = gameDataPath,
            TraceOutputPath = tracePath
        });

        var markdown = report.ToMarkdown();
        Console.WriteLine(markdown);

        if (!string.IsNullOrEmpty(reportPath))
        {
            var fullReportPath = Path.GetFullPath(reportPath);
            var dir = Path.GetDirectoryName(fullReportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (fullReportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(fullReportPath, report.ToJson());
            else
                File.WriteAllText(fullReportPath, markdown);
            Console.WriteLine($"Report saved to: {fullReportPath}");
        }

        return report.Outcome switch
        {
            SimulationOutcome.CompileError or SimulationOutcome.ScriptException => 2,
            SimulationOutcome.Stalled => 3,
            _ => 0
        };
    }

    private static string ResolveScriptPath(string input, SkuaInstallation installation)
    {
        if (File.Exists(input))
            return Path.GetFullPath(input);

        var candidate1 = Path.Combine(installation.ScriptsDirectory, input);
        if (File.Exists(candidate1))
            return Path.GetFullPath(candidate1);

        var candidate2 = Path.Combine(installation.UserDataDirectory, input);
        if (File.Exists(candidate2))
            return Path.GetFullPath(candidate2);

        return Path.GetFullPath(input);
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int RunUnknown(string cmd, SkuaInstallation installation)
    {
        Console.Error.WriteLine($"Unknown command: '{cmd}'");
        PrintUsage(installation);
        return 1;
    }

    private static void PrintUsage(SkuaInstallation installation)
    {
        Console.WriteLine("SkuaSim CLI — Offline Test Environment for Skua/VibeSkua Scripts");
        Console.WriteLine($"Detected {installation.Flavor} at: {installation.AssembliesDirectory}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  skuasim db-stats [--gamedata <path>]");
        Console.WriteLine("  skuasim compile <script.cs>");
        Console.WriteLine("  skuasim run <script.cs> [options]");
        Console.WriteLine();
        Console.WriteLine("Run Options:");
        Console.WriteLine("  --scenario, -s <file.json>     Scenario JSON file describing player/world state");
        Console.WriteLine("  --seed <int>                   Random seed for deterministic simulation (default: 42)");
        Console.WriteLine("  --max-virtual-min <minutes>    Maximum simulated virtual time in minutes (default: 360)");
        Console.WriteLine("  --max-real-sec <seconds>       Maximum wall-clock time in seconds (default: 0 = unlimited)");
        Console.WriteLine("  --max-no-progress-min <min>    Stop as Stalled after this many virtual minutes without player");
        Console.WriteLine("                                 progress (default: 60, 0 = disabled)");
        Console.WriteLine("  --report, -o <file.md|.json>   Save the execution report to disk");
        Console.WriteLine("  --trace <file.jsonl>           Stream full API trace to JSONL file");
        Console.WriteLine();
        Console.WriteLine("Exit codes (run): 0 = completed/stopped, 2 = compile error or script exception, 3 = stalled");
    }
}
