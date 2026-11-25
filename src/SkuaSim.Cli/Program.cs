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
            "test" => RunTestSuite(rest, installation),
            "calendar-update" => RunCalendarUpdate(rest, installation),
            "calendar-show" => RunCalendarShow(rest, installation),
            "servers-update" => RunServersUpdate(rest, installation),
            "servers-show" => RunServersShow(rest, installation),
            "whatif" => RunWhatIf(rest, installation),
            "import-data" => RunImportData(rest, installation),
            "snapshot" => RunSnapshot(rest, installation),
            "bug-report" => RunBugReport(rest, installation),
            _ => RunUnknown(cmd, installation)
        };
    }

    /// <summary>
    /// "What if the character owned these items?" Runs the script twice with the same seed — the scenario as it is, and
    /// with the --add items in the inventory (quest pets, unlock weapons…) — and compares what each run achieved.
    /// </summary>
    private static int RunWhatIf(string[] args, SkuaInstallation installation)
    {
        var adds = GetOptions(args, "--add");
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal) || adds.Count == 0)
        {
            Console.Error.WriteLine("Usage: skuasim whatif <script.cs> --add \"Item name\" [--add ...] [--scenario <file.json>] [--max-virtual-min <num>] [--seed <int>] [--gamedata <path>] [--report <path.md>]");
            return 1;
        }
        var scriptPath = ResolveScriptPath(args[0], installation);
        var scenarioPath = GetOption(args, "--scenario") ?? GetOption(args, "-s");
        Scenario LoadScenario() => !string.IsNullOrEmpty(scenarioPath) ? Scenario.Load(scenarioPath) : new Scenario();
        var minutes = GetOption(args, "--max-virtual-min") is { } m &&
                      double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vm) ? vm : 60;
        int? seed = GetOption(args, "--seed") is { } s && int.TryParse(s, out var sv) ? sv : null;
        var gameData = GetOption(args, "--gamedata");

        var runner = new SimulationRunner(installation);
        SimulationReport Run(Scenario scenario, string label)
        {
            scenario.Limits.MaxVirtualMinutes = minutes;
            Console.WriteLine($"Running {label}...");
            return runner.Run(new SimulationRunOptions { ScriptPath = scriptPath, Scenario = scenario, SeedOverride = seed, GameDataPath = gameData, DataSnapshot = GetOption(args, "--data") });
        }

        var baseline = Run(LoadScenario(), "the scenario as it is");
        var variantScenario = LoadScenario();
        foreach (var name in adds)
            if (!variantScenario.Inventory.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                variantScenario.Inventory.Add(new ItemSetup { Name = name });
        var variant = Run(variantScenario, "with " + string.Join(", ", adds));

        var markdown = WhatIfMarkdown(Path.GetFileName(scriptPath), adds, baseline, variant);
        Console.WriteLine();
        Console.WriteLine(markdown);
        if ((GetOption(args, "--report") ?? GetOption(args, "-o")) is { } reportPath)
        {
            File.WriteAllText(Path.GetFullPath(reportPath), markdown);
            Console.WriteLine($"Report saved to: {Path.GetFullPath(reportPath)}");
        }
        return baseline.Outcome is SimulationOutcome.CompileError ? 2 : 0;
    }

    private static string WhatIfMarkdown(string script, List<string> adds, SimulationReport a, SimulationReport b)
    {
        static Dictionary<string, long> Owned(SkuaSim.Core.World.WorldSnapshot s) =>
            s.Inventory.Concat(s.Bank).GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (long)g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, long> Gained(SimulationReport r)
        {
            var start = Owned(r.InitialState);
            return Owned(r.FinalState).Select(kv => (kv.Key, Delta: kv.Value - start.GetValueOrDefault(kv.Key)))
                .Where(x => x.Delta != 0).ToDictionary(x => x.Key, x => x.Delta, StringComparer.OrdinalIgnoreCase);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# What if — `{script}` with {string.Join(", ", adds.Select(x => $"**{x}**"))}");
        sb.AppendLine($"Same seed ({a.Seed}) and limits; only the added item(s) differ.");
        sb.AppendLine();
        sb.AppendLine("| | Without | With |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| Outcome | {a.Outcome} | {b.Outcome} |");
        sb.AppendLine($"| Virtual time | {a.VirtualDuration:hh\\:mm\\:ss} | {b.VirtualDuration:hh\\:mm\\:ss} |");
        sb.AppendLine($"| Kills | {a.Kills:N0} | {b.Kills:N0} |");
        sb.AppendLine($"| Quest turn-ins | {a.QuestTurnIns.Values.Sum():N0} | {b.QuestTurnIns.Values.Sum():N0} |");
        sb.AppendLine($"| Gold gained | {a.FinalState.Gold - a.InitialState.Gold:N0} | {b.FinalState.Gold - b.InitialState.Gold:N0} |");

        var quests = a.QuestTurnIns.Keys.Union(b.QuestTurnIns.Keys).OrderBy(q => q)
            .Where(q => a.QuestTurnIns.GetValueOrDefault(q) != b.QuestTurnIns.GetValueOrDefault(q)).ToList();
        foreach (var q in quests)
            sb.AppendLine($"| Quest {q} turn-ins | {a.QuestTurnIns.GetValueOrDefault(q):N0} | {b.QuestTurnIns.GetValueOrDefault(q):N0} |");

        var ga = Gained(a);
        var gb = Gained(b);
        foreach (var item in ga.Keys.Union(gb.Keys, StringComparer.OrdinalIgnoreCase)
                     .Where(k => ga.GetValueOrDefault(k) != gb.GetValueOrDefault(k) && !adds.Contains(k, StringComparer.OrdinalIgnoreCase))
                     .OrderByDescending(k => Math.Abs(gb.GetValueOrDefault(k) - ga.GetValueOrDefault(k))).Take(15))
            sb.AppendLine($"| {item} | {ga.GetValueOrDefault(item):+#,0;-#,0;0} | {gb.GetValueOrDefault(item):+#,0;-#,0;0} |");

        var stillMissing = b.MissingUnlockItems.Where(m => !adds.Contains(m.Name, StringComparer.OrdinalIgnoreCase)).Take(10).ToList();
        if (stillMissing.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Other items the script checked and the character still does not own: " + string.Join(", ", stillMissing.Select(x => x.Name)) + ".");
        }
        return sb.ToString();
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

    private static string ServersPathFor(string[] args, SkuaInstallation installation) =>
        GetOption(args, "--servers") ?? new SimulationRunner(installation).FindDefaultServersPath();

    /// <summary>Downloads AQW's public server list (game.aq.com/game/api/data/servers) into the local file Bot.Servers serves.</summary>
    private static int RunServersUpdate(string[] args, SkuaInstallation installation)
    {
        var path = ServersPathFor(args, installation);
        Console.WriteLine($"Downloading {AqwServerList.SourceUrl} ...");
        try
        {
            var list = AqwServerList.DownloadAsync().GetAwaiter().GetResult();
            if (list.Servers.Count == 0)
            {
                Console.Error.WriteLine("The server list came back empty (the API format may have changed). The local file was not changed.");
                return 1;
            }
            list.Save(path);
            PrintServers(list, path);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Server list update failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunServersShow(string[] args, SkuaInstallation installation)
    {
        var path = ServersPathFor(args, installation);
        var list = AqwServerList.TryLoad(path);
        if (list == null)
        {
            Console.Error.WriteLine($"No server list at '{path}'. Run 'skuasim servers-update' first.");
            return 1;
        }
        PrintServers(list, path);
        return 0;
    }

    private static void PrintServers(AqwServerList list, string path)
    {
        Console.WriteLine($"Server list   : {path}");
        Console.WriteLine($"Fetched (UTC) : {list.FetchedUtc:u}  fingerprint {list.Fingerprint()}");
        foreach (var s in list.Servers)
            Console.WriteLine($"  {s.Name,-20} {s.Ip + ":" + s.Port,-26} {s.PlayerCount,5}/{s.MaxPlayers,-5} {(s.Online ? "online " : "OFFLINE")}" +
                              $"{(s.MemberOnly ? " members-only" : "")}{(s.MaxPlayers > 0 && s.PlayerCount >= s.MaxPlayers ? " FULL" : "")} lang {s.Lang} chat {s.ChatLevel}");
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

    /// <summary>
    /// Stores other testers' shared data ("Export for Sharing" .zip files) in data\contrib\, where every simulation merges
    /// it with gamedata.json in memory. A newer export from the same tester replaces the older one.
    /// </summary>
    private static int RunImportData(string[] args, SkuaInstallation installation)
    {
        var files = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("Usage: skuasim import-data <share.zip|gamedata.json> [more files...] [--gamedata <path>]");
            return 1;
        }

        var runner = new SimulationRunner(installation);
        var gameDataPath = GetOption(args, "--gamedata") ?? runner.FindDefaultGameDataPath();
        if (gameDataPath == null)
        {
            Console.Error.WriteLine("Error: no gamedata.json found (use --gamedata <path>); shared data is stored next to it.");
            return 1;
        }

        var ownId = GameDataContributions.FindOwnContributorId(installation.UserDataDirectory);
        var failures = 0;
        foreach (var file in files)
        {
            var result = File.Exists(file)
                ? GameDataContributions.Import(file, gameDataPath, ownId)
                : new GameDataContributions.ImportResult(file, null, "rejected: file not found");
            Console.WriteLine($"{Path.GetFileName(file)}: {result.Message}");
            if (result.StoredAs == null)
                failures++;
        }
        Console.WriteLine($"Shared data folder: {GameDataContributions.FolderFor(gameDataPath)}");
        return failures == 0 ? 0 : 2;
    }

    private static int RunDbStats(string[] args, SkuaInstallation installation)
    {
        var runner = new SimulationRunner(installation);
        var gameDataPath = GetOption(args, "--gamedata") ?? runner.FindDefaultGameDataPath();
        var questDataPath = GetOption(args, "--questdata") ?? installation.QuestDataFile;

        var catalog = GameDataCatalog.Load(gameDataPath, questDataPath);
        var db = catalog.Captured;
        var totalKills = db.Monsters.Values.Sum(m => m.AllKillsObserved);
        var totalDrops = db.Monsters.Values.Sum(m => m.Drops.Values.Sum(d => d.DropEvents + d.CreditedDropEvents));

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
        Console.WriteLine($"Collector Builds   : {(db.CollectorVersions.Count == 0 ? "(before 1.1.0)" : string.Join(", ", db.CollectorVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} ×{v.Value:N0}")))}");
        Console.WriteLine($"Shared Data        : {(catalog.Contributions.Count == 0 ? "(none)" : $"{catalog.Contributions.Count(c => c.Status == "merged")} file(s) merged")}");
        foreach (var c in catalog.Contributions)
            Console.WriteLine($"  {c.FileName}: {c.Status}" + (c.Status == "merged" ? $", {c.Kills:N0} kills, {c.Maps:N0} maps, exported {c.ExportedUtc:yyyy-MM-dd}" : ""));
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
        const string usage = "Usage: skuasim run [<script.cs>] [--scenario <file.json>] [--seed <int|random>] [--max-virtual-min <num>] [--max-real-sec <num>] [--max-no-progress-min <num>] [--start-date <utc|now>] [--ae-calendar] [--report <path.md>] [--trace <path.jsonl>] [--export-scenario <path.json>]";
        var scenarioPath = GetOption(args, "--scenario") ?? GetOption(args, "-s");
        var scenario = !string.IsNullOrEmpty(scenarioPath) ? Scenario.Load(scenarioPath) : new Scenario();
        var scriptArg = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : scenario.Script;
        if (string.IsNullOrWhiteSpace(scriptArg))
        {
            Console.Error.WriteLine("Error: missing script path (give one, or set \"script\" in the scenario). " + usage);
            return 1;
        }

        var scriptPath = ResolveScriptPath(scriptArg, installation);

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
            DataSnapshot = GetOption(args, "--data"),
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

        return ExitCode(report);
    }

    /// <summary>
    /// Bug report from a diagnostic file (a scenario saved in Skua by tools\skua\SkuaSim_Diagnostic.cs): runs the reported
    /// script on that character and writes the filled bug report template (character, reproduction, data gaps, asserts
    /// to start the regression test from). Default output: next to the diagnostic, &lt;name&gt;.bugreport.md.
    /// </summary>
    private static int RunBugReport(string[] args, SkuaInstallation installation)
    {
        const string usage = "Usage: skuasim bug-report <diagnostic.json> [<script.cs>] [--out <report.md>] [--report <full-report.md>] [--seed <int>] [--max-virtual-min <num>] [--data <snapshot>] [--gamedata <path>]";
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Console.Error.WriteLine(usage);
            return 1;
        }
        var diagnosticPath = Path.GetFullPath(args[0]);
        if (!File.Exists(diagnosticPath))
        {
            Console.Error.WriteLine($"Error: diagnostic file not found: {diagnosticPath}");
            return 1;
        }
        Scenario diagnostic;
        try
        {
            diagnostic = Scenario.Load(diagnosticPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: not a valid scenario/diagnostic file: {ex.Message}");
            return 1;
        }

        var scriptArg = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : diagnostic.Script;
        if (string.IsNullOrWhiteSpace(scriptArg))
        {
            Console.Error.WriteLine("Error: the diagnostic does not name the reported script; give it after the file. " + usage);
            return 1;
        }

        int? seed = GetOption(args, "--seed") is { } seedText && int.TryParse(seedText, out var s) ? s : null;
        if (GetOption(args, "--max-virtual-min") is { } vText &&
            double.TryParse(vText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vMin))
            diagnostic.Limits.MaxVirtualMinutes = vMin;

        var report = new SimulationRunner(installation).Run(new SimulationRunOptions
        {
            ScriptPath = ResolveScriptPath(scriptArg, installation),
            Scenario = diagnostic,
            SeedOverride = seed,
            GameDataPath = GetOption(args, "--gamedata"),
            DataSnapshot = GetOption(args, "--data")
        });

        var outPath = Path.GetFullPath(GetOption(args, "--out") ?? GetOption(args, "-o")
                                       ?? Path.ChangeExtension(diagnosticPath, null) + ".bugreport.md");
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);
        File.WriteAllText(outPath, BugReportWriter.ToMarkdown(diagnostic, report, diagnosticPath));
        if (GetOption(args, "--report") is { } fullPath)
            File.WriteAllText(Path.GetFullPath(fullPath), report.ToMarkdown());

        Console.WriteLine($"Outcome: {report.Outcome}{(string.IsNullOrEmpty(report.StopReason) ? "" : $" ({report.StopReason})")}, " +
                          $"virtual {report.VirtualDuration:hh\\:mm\\:ss}, {report.SynthesizedData.Count} synthesized record(s), {report.SynthesizedMaps.Count} synthesized map(s)");
        Console.WriteLine($"Bug report saved to: {outPath}");
        return ExitCode(report);
    }

    /// <summary>
    /// Exit code of a run. Without scenario asserts: 0 completed/stopped, 2 compile error or script exception, 3 stalled.
    /// With asserts, they decide (the expected outcome is one of them): 0 all passed, 4 one failed; a compile error stays 2.
    /// </summary>
    private static int ExitCode(SimulationReport report)
    {
        if (report.Outcome == SimulationOutcome.CompileError)
            return 2;
        if (report.Assertions.Count > 0)
            return report.AssertionsFailed == 0 ? 0 : 4;
        return report.Outcome switch
        {
            SimulationOutcome.ScriptException => 2,
            SimulationOutcome.Stalled => 3,
            _ => 0
        };
    }

    /// <summary>
    /// Regression suite: runs every scenario (files or folders, *.json) that names its "script", checks its asserts and
    /// prints one line per scenario. Exit code: 0 all passed, 4 a scenario failed, 1 nothing to run.
    /// </summary>
    private static int RunTestSuite(string[] args, SkuaInstallation installation)
    {
        var inputs = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (inputs.Count == 0)
            inputs.Add("scenarios");
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
                files.AddRange(Directory.GetFiles(input, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(input))
                files.Add(input);
            else
            {
                Console.Error.WriteLine($"Not found: {input}");
                return 1;
            }
        }

        var gameDataPath = GetOption(args, "--gamedata");
        var reportDir = GetOption(args, "--report-dir");
        var runner = new SimulationRunner(installation);
        var rows = new List<(string Scenario, string Status, SimulationReport? Report, string Detail)>();
        foreach (var file in files)
        {
            Scenario scenario;
            try { scenario = Scenario.Load(file); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
            {
                rows.Add((Path.GetFileName(file), "SKIP", null, "not a scenario: " + ex.Message));
                continue;
            }
            if (string.IsNullOrWhiteSpace(scenario.Script) || scenario.Asserts.Count == 0)
            {
                rows.Add((Path.GetFileName(file), "SKIP", null, string.IsNullOrWhiteSpace(scenario.Script) ? "no \"script\"" : "no \"asserts\""));
                continue;
            }

            Console.WriteLine($"Running {Path.GetFileName(file)} ({scenario.Script})...");
            var report = runner.Run(new SimulationRunOptions
            {
                ScriptPath = ResolveScriptPath(scenario.Script, installation),
                Scenario = scenario,
                GameDataPath = gameDataPath,
                DataSnapshot = GetOption(args, "--data")
            });
            var passed = ExitCode(report) == 0;
            var failed = report.Assertions.Where(a => !a.Passed).Select(a => $"#{a.Index} {a.Description}: {a.Actual}").ToList();
            var detail = $"{report.Outcome}, {report.VirtualDuration:hh\\:mm\\:ss} virtual, {DataLabel(report)}, {report.Assertions.Count - report.AssertionsFailed}/{report.Assertions.Count} asserts"
                         + (failed.Count > 0 ? " — " + string.Join("; ", failed) : "");
            rows.Add((Path.GetFileName(file), passed ? "PASS" : "FAIL", report, detail));
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {detail}");

            if (!string.IsNullOrEmpty(reportDir))
            {
                Directory.CreateDirectory(reportDir);
                File.WriteAllText(Path.Combine(reportDir, Path.GetFileNameWithoutExtension(file) + ".md"), report.ToMarkdown());
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== SkuaSim scenario tests ===");
        foreach (var (name, status, _, detail) in rows)
            Console.WriteLine($"{status,-4}  {name}: {detail}");
        var ran = rows.Count(r => r.Status != "SKIP");
        var failedCount = rows.Count(r => r.Status == "FAIL");
        Console.WriteLine($"{ran - failedCount}/{ran} passed, {rows.Count - ran} skipped.");
        if (!string.IsNullOrEmpty(reportDir))
            Console.WriteLine($"Reports saved to: {Path.GetFullPath(reportDir)}");
        return ran == 0 ? 1 : failedCount == 0 ? 0 : 4;
    }

    /// <summary>"snapshot 2026-11-24" or "live data" for the one-line test summary.</summary>
    private static string DataLabel(SimulationReport report) =>
        report.DataInfo is { } info && info.StartsWith("snapshot ", StringComparison.Ordinal)
            ? info.Split(" (", 2)[0]
            : "live data";

    /// <summary>
    /// skuasim snapshot create|list: frozen copies of the data a run reads (Data/DataSnapshot.cs), so scenario tests do
    /// not drift while the collector keeps writing gamedata.json.
    /// </summary>
    private static int RunSnapshot(string[] args, SkuaInstallation installation)
    {
        const string usage = "Usage: skuasim snapshot create [<name>] [--note <text>] [--gamedata <path>] [--questdata <path>] [--calendar <path>] [--servers <path>]\n" +
                             "       skuasim snapshot list [--gamedata <path>]";
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var runner = new SimulationRunner(installation);
        var gameDataPath = GetOption(args, "--gamedata") ?? runner.FindDefaultGameDataPath();
        if (gameDataPath == null)
        {
            Console.Error.WriteLine("Error: no gamedata.json found (use --gamedata <path>).");
            return 1;
        }
        var root = DataSnapshot.FolderFor(gameDataPath);

        if (sub == "list")
        {
            var dirs = Directory.Exists(root)
                ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, DataSnapshot.ManifestFileName)))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            Console.WriteLine($"Snapshots in {root}:");
            if (dirs.Count == 0)
                Console.WriteLine("  (none; create one with 'skuasim snapshot create')");
            foreach (var dir in dirs)
            {
                var s = DataSnapshot.Load(dir);
                var problems = s.Verify();
                var m = s.Manifest;
                Console.WriteLine($"  {s.Describe()}: {m.Monsters:N0} monsters, {m.Maps:N0} maps, {m.Quests:N0} quests, " +
                                  $"{m.CreditedKills:N0} credited / {m.LegacyKills:N0} legacy kills" +
                                  (problems.Count == 0 ? ", intact" : " — " + string.Join("; ", problems)));
                if (!string.IsNullOrEmpty(m.Note))
                    Console.WriteLine($"    {m.Note}");
            }
            return 0;
        }

        if (sub != "create")
        {
            Console.Error.WriteLine(usage);
            return 1;
        }

        var name = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : DateTime.UtcNow.ToString("yyyy-MM-dd");
        var questDataPath = GetOption(args, "--questdata") ?? installation.QuestDataFile;
        var calendarPath = GetOption(args, "--calendar") ?? runner.FindDefaultCalendarPath();
        var serversPath = GetOption(args, "--servers") ?? runner.FindDefaultServersPath();
        try
        {
            var snapshot = DataSnapshot.Create(name, gameDataPath, questDataPath, calendarPath, root, DateTime.UtcNow, GetOption(args, "--note"), serversPath);
            var m = snapshot.Manifest;
            Console.WriteLine($"Snapshot {snapshot.Describe()} saved to {snapshot.Directory}");
            foreach (var f in m.Files)
                Console.WriteLine($"  {f.Name,-18} {f.Bytes,12:N0} bytes  {f.Sha256[..12]}");
            if (m.Contributions.Count > 0)
                Console.WriteLine($"  Shared data merged: {string.Join(", ", m.Contributions)}");
            if (!m.Files.Any(f => f.Name == DataSnapshot.QuestDataFileName))
                Console.WriteLine("  Warning: no QuestData.json found; runs with this snapshot have no Skua quest data.");
            if (!m.Files.Any(f => f.Name == AeCalendar.DefaultFileName))
                Console.WriteLine("  Note: no AE calendar (run 'skuasim calendar-update' first if scenarios use calendar boosts).");
            if (!m.Files.Any(f => f.Name == AqwServerList.DefaultFileName))
                Console.WriteLine("  Note: no server list (run 'skuasim servers-update' first; scripts that use Bot.Servers then get a synthesized list).");
            Console.WriteLine($"Use it with \"dataSnapshot\": \"{snapshot.Name}\" in a scenario, or --data {snapshot.Name}.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
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

        // A bare script name ("GrimGaol", as typed in a diagnostic): the one file with that name under Scripts\.
        if (!Path.IsPathRooted(input) && Directory.Exists(installation.ScriptsDirectory))
        {
            var fileName = Path.GetFileName(input);
            if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                fileName += ".cs";
            var matches = Directory.EnumerateFiles(installation.ScriptsDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                Console.WriteLine($"Script '{input}' resolved to {matches[0]}");
                return matches[0];
            }
            if (matches.Count > 1)
                Console.Error.WriteLine($"Script '{input}' matches {matches.Count} files under Scripts\\ ({string.Join(", ", matches.Select(m => Path.GetRelativePath(installation.ScriptsDirectory, m)))}); give its folder.");
        }

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

    /// <summary>Every value of a repeatable flag (e.g. several --add).</summary>
    private static List<string> GetOptions(string[] args, string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                values.Add(args[++i]);
        return values;
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
        Console.WriteLine("  skuasim run [<script.cs>] [options]       The script can come from the scenario's \"script\"");
        Console.WriteLine("  skuasim test [<scenario.json|folder> ...] [--data <snapshot>] [--gamedata <path>] [--report-dir <dir>]   Run every scenario with");
        Console.WriteLine("                                 \"script\" + \"asserts\" (default folder: scenarios) as a regression suite");
        Console.WriteLine("  skuasim calendar-update [--calendar <path>]   Download the AE calendar (artix.com/calendar) locally");
        Console.WriteLine("  skuasim calendar-show [--calendar <path>] [--date <utc>]");
        Console.WriteLine("  skuasim servers-update [--servers <path>]   Download AQW's public server list (game.aq.com) locally for Bot.Servers");
        Console.WriteLine("  skuasim servers-show [--servers <path>]");
        Console.WriteLine("  skuasim whatif <script.cs> --add \"Item\" [--add ...] [--scenario <file.json>] [--max-virtual-min <num>]   Compare a run with and without items (quest pets...)");
        Console.WriteLine("  skuasim import-data <share.zip|gamedata.json> [...] [--gamedata <path>]   Add testers' shared data (data\\contrib\\, merged when simulating)");
        Console.WriteLine("  skuasim snapshot create [<name>] [--note <text>] [--gamedata <path>]   Freeze the current data (gamedata.json + shared");
        Console.WriteLine("                                 data, QuestData.json, AE calendar, server list) in data\\snapshots\\<name> (default name: today's UTC date)");
        Console.WriteLine("  skuasim snapshot list [--gamedata <path>]   List snapshots and check their files");
        Console.WriteLine("  skuasim bug-report <diagnostic.json> [<script.cs>] [--out <file.md>] [--data <snapshot>]   Run a diagnostic made in Skua by");
        Console.WriteLine("                                 tools\\skua\\SkuaSim_Diagnostic.cs and write the filled bug report (default: <diagnostic>.bugreport.md)");
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
        Console.WriteLine("  --data <snapshot>              Read a frozen data snapshot (name or folder) instead of the live data; overrides the");
        Console.WriteLine("                                 scenario's \"dataSnapshot\" (also for test and whatif)");
        Console.WriteLine("  --report, -o <file.md|.json>   Save the execution report to disk");
        Console.WriteLine("  --trace <file.jsonl>           Stream full API trace to JSONL file");
        Console.WriteLine("  --export-scenario <file.json>  Save the character at the end of the run as a new scenario (chain runs)");
        Console.WriteLine();
        Console.WriteLine("Exit codes (run): 0 = completed/stopped, 2 = compile error or script exception, 3 = stalled;");
        Console.WriteLine("  with scenario \"asserts\" they decide: 0 = all passed, 4 = an assert failed (compile error stays 2)");
        Console.WriteLine("Exit codes (test): 0 = all passed, 4 = a scenario failed, 1 = nothing to run");
    }
}
