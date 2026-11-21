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
            "calendar-update" => RunCalendarUpdate(rest, installation),
            "calendar-show" => RunCalendarShow(rest, installation),
            _ => RunUnknown(cmd, installation)
        };
    }

    private static string CalendarPathFor(string[] args, SkuaInstallation installation) =>
        GetOption(args, "--calendar") ?? new SimulationRunner(installation).FindDefaultCalendarPath();

    /// <summary>Downloads the public AE calendar (artix.com/calendar) into the local snapshot used by the simulator.</summary>
    private static int RunCalendarUpdate(string[] args, SkuaInstallation installation)
    {
        var path = CalendarPathFor(args, installation);
        Console.WriteLine($"Downloading {AeCalendar.SourceUrl} ...");
        try
        {
            var previous = AeCalendar.TryLoad(path);
            var calendar = AeCalendar.DownloadAsync(previous, Console.WriteLine).GetAwaiter().GetResult();
            if (calendar.Events.Count == 0)
            {
                Console.Error.WriteLine("No events found on the calendar page (the page format may have changed). The local file was not changed.");
                return 1;
            }
            calendar.Save(path);
            PrintCalendarSummary(calendar, path);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine($"Calendar update failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunCalendarShow(string[] args, SkuaInstallation installation)
    {
        var path = CalendarPathFor(args, installation);
        var calendar = AeCalendar.TryLoad(path);
        if (calendar == null)
        {
            Console.Error.WriteLine($"No calendar at '{path}'. Run 'skuasim calendar-update' first.");
            return 1;
        }
        PrintCalendarSummary(calendar, path);
        if (GetOption(args, "--date") is { } dateText && DateTime.TryParse(dateText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
        {
            Console.WriteLine();
            Console.WriteLine($"At {at:u} ({AeCalendar.ToEastern(at):yyyy-MM-dd HH:mm} server time): server boosts = {calendar.ServerBoostsAt(at)}");
            foreach (var e in calendar.ActiveAt(at))
                Console.WriteLine($"  - [{e.Kind}] {e.Title} ({e.StartEastern:yyyy-MM-dd HH:mm} ET, {e.DurationHours:0.#} h, {e.DurationSource})");
        }
        return 0;
    }

    private static void PrintCalendarSummary(AeCalendar calendar, string path)
    {
        var coverage = calendar.CoverageEastern;
        Console.WriteLine($"Calendar file : {path}");
        Console.WriteLine($"Fetched (UTC) : {calendar.FetchedUtc:u}  fingerprint {calendar.Fingerprint()}");
        Console.WriteLine($"Coverage (ET) : {coverage?.First:yyyy-MM-dd} .. {coverage?.Last:yyyy-MM-dd}");
        foreach (var g in calendar.Events.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key,-15} {g.Count(),4}");
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
            Console.Error.WriteLine("Error: missing script path. Usage: skuasim run <script.cs> [--scenario <file.json>] [--seed <int|random>] [--max-virtual-min <num>] [--max-real-sec <num>] [--max-no-progress-min <num>] [--start-date <utc|now>] [--ae-calendar] [--report <path.md>] [--trace <path.jsonl>] [--export-scenario <path.json>]");
            return 1;
        }

        var scriptPath = ResolveScriptPath(args[0], installation);
        var scenarioPath = GetOption(args, "--scenario") ?? GetOption(args, "-s");
        var scenario = !string.IsNullOrEmpty(scenarioPath) ? Scenario.Load(scenarioPath) : new Scenario();

        int? seedOverride = null;
        if (GetOption(args, "--seed") is { } seedStr)
        {
            if (string.Equals(seedStr, "random", StringComparison.OrdinalIgnoreCase))
                seedOverride = SimulationRunner.NewRandomSeed(); // printed in the report as "Seed"
            else if (int.TryParse(seedStr, out var parsedSeed))
                seedOverride = parsedSeed;
        }

        if (GetOption(args, "--max-virtual-min") is { } vMinStr &&
            double.TryParse(vMinStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vMin))
            scenario.Limits.MaxVirtualMinutes = vMin;

        if (GetOption(args, "--max-real-sec") is { } rSecStr &&
            double.TryParse(rSecStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rSec))
            scenario.Limits.MaxRealSeconds = rSec;

        if (GetOption(args, "--max-no-progress-min") is { } npStr &&
            double.TryParse(npStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var noProgressMin))
            scenario.Limits.MaxNoProgressMinutes = noProgressMin;

        if (GetOption(args, "--start-date") is { } startText)
        {
            if (string.Equals(startText, "now", StringComparison.OrdinalIgnoreCase))
                scenario.StartAtCurrentTime = true;
            else if (!DateTime.TryParse(startText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var start))
            {
                Console.Error.WriteLine($"Error: invalid --start-date '{startText}' (use e.g. 2026-11-20T19:00:00Z or now).");
                return 1;
            }
            else
            {
                scenario.StartDate = DateTime.SpecifyKind(start, DateTimeKind.Utc);
                scenario.StartAtCurrentTime = false;
            }
        }
        if (args.Any(a => string.Equals(a, "--ae-calendar", StringComparison.OrdinalIgnoreCase)))
            scenario.Boosts.AeCalendarServerBoosts = true;

        var reportPath = GetOption(args, "--report") ?? GetOption(args, "-o");
        var tracePath = GetOption(args, "--trace");
        var gameDataPath = GetOption(args, "--gamedata");
        var calendarPath = GetOption(args, "--calendar");

        var runner = new SimulationRunner(installation);
        var report = runner.Run(new SimulationRunOptions
        {
            ScriptPath = scriptPath,
            Scenario = scenario,
            SeedOverride = seedOverride,
            GameDataPath = gameDataPath,
            CalendarPath = calendarPath,
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

        if (GetOption(args, "--export-scenario") is { } exportPath)
        {
            if (report.EndScenario == null)
                Console.Error.WriteLine("No scenario exported: the run did not start (compile error or stopped before execution).");
            else
            {
                var fullExportPath = Path.GetFullPath(exportPath);
                var exportDir = Path.GetDirectoryName(fullExportPath);
                if (!string.IsNullOrEmpty(exportDir))
                    Directory.CreateDirectory(exportDir);
                File.WriteAllText(fullExportPath, report.EndScenario.ToJson());
                Console.WriteLine($"End-of-run scenario saved to: {fullExportPath}");
            }
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
        Console.WriteLine("  skuasim calendar-update [--calendar <path>]   Download the AE calendar (artix.com/calendar) locally");
        Console.WriteLine("  skuasim calendar-show [--calendar <path>] [--date <utc>]");
        Console.WriteLine();
        Console.WriteLine("Run Options:");
        Console.WriteLine("  --scenario, -s <file.json>     Scenario JSON file describing player/world state");
        Console.WriteLine("  --seed <int|random>            Seed of the random rolls (drops, crits...); same seed = same run (default: 42; random draws one, shown in the report)");
        Console.WriteLine("  --max-virtual-min <minutes>    Maximum simulated virtual time in minutes (default: 360)");
        Console.WriteLine("  --max-real-sec <seconds>       Maximum wall-clock time in seconds (default: 0 = unlimited)");
        Console.WriteLine("  --max-no-progress-min <min>    Stop as Stalled after this many virtual minutes without player");
        Console.WriteLine("                                 progress (default: 60, 0 = disabled)");
        Console.WriteLine("  --start-date <utc|now>         Virtual start time, e.g. 2026-11-20T19:00:00Z, or now for the PC clock (default: 2026-01-01T12:00Z);");
        Console.WriteLine("                                 drives day of week and daily/weekly resets (00:00 US Eastern, weekly on Friday)");
        Console.WriteLine("  --ae-calendar                  Apply the AE calendar's server boosts for the virtual date");
        Console.WriteLine("  --calendar <path>              Calendar file (default: data\\ae_calendar.json next to gamedata.json)");
        Console.WriteLine("  --report, -o <file.md|.json>   Save the execution report to disk");
        Console.WriteLine("  --trace <file.jsonl>           Stream full API trace to JSONL file");
        Console.WriteLine("  --export-scenario <file.json>  Save the character at the end of the run as a new scenario (chain runs)");
        Console.WriteLine();
        Console.WriteLine("Exit codes (run): 0 = completed/stopped, 2 = compile error or script exception, 3 = stalled");
    }
}
