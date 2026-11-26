using System.Diagnostics;
using System.Runtime.CompilerServices;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Data;
using SkuaSim.Core.Hosting;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;
using SkuaSim.Core.World;

var installation = SkuaInstallation.Detect();
SkuaAssemblyResolver.Register(installation.ProbeDirectories);

// Child-process mode used by the cross-process determinism test.
if (args.Length >= 2 && args[0] == CoreTests.FingerprintArg)
    return CoreTests.PrintDeterminismFingerprint(installation, args[1]);

return CoreTests.RunAll(installation);

internal static class CoreTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception("Assertion failed: " + message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int RunAll(SkuaInstallation installation)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"skuasim_core_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var runner = new SimulationRunner(installation);

            TestEndToEndAndDeterminism(runner, tempDir);
            TestCompileAndInspectScript(runner, tempDir);
            TestSessionPauseResumeAndLiveSnapshot(runner, tempDir);
            TestSpeedPacingAndStopWhilePaused(runner, tempDir);
            TestXpLevelProgression(runner, tempDir);
            TestMaxRealSecondsExcludesCompile(installation, tempDir);
            TestStatProgressionAndEnhancements(runner, tempDir);
            TestClassCombatDodgeCritHealAndDeterminism(runner, tempDir);
            TestCatalogClassCalibration(installation, tempDir);
            TestBoostsEnhancementUnlocksAndLongVirtualDuration(runner, tempDir);
            TestKillQuestHintsOnDemandMonstersAndStallDetector(runner, tempDir);
            TestCrossProcessDeterminism(tempDir);
            TestSchemaV2QuestGatedAndUnknownRateDrops(runner, tempDir);
            TestRealDataRewardsRespawnHouseResetsAndCalendar(runner, tempDir);
            TestBackgroundTaskDeterminism(runner, tempDir);
            TestQuestPetsAndUnlockItems(runner, tempDir);
            TestClassProfilesAndSharedData(tempDir);
            TestPvpMatchMtcidAndTrophies(runner, tempDir);
            TestServerWarnings(runner, tempDir);
            TestInjectedFaults(runner, tempDir);
            TestScenarioAssertions(runner, tempDir);
            TestFrozenDataSnapshots(runner, tempDir);
            TestServerList(runner, tempDir);
            TestQuestGateNamesResolved(runner, tempDir);
            TestDiagnosticGeneratorAndBugReport(runner, tempDir);
            TestFarmTargetsWithCarriedOverQuests(runner, tempDir);
            TestHarvestPlanAndHarvester(runner, tempDir);

            Console.WriteLine("All SkuaSim.Core tests passed successfully!");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void TestEndToEndAndDeterminism(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("1. Testing end-to-end farm + quest + shop script execution & expanded snapshot...");
        var sampleScript = Path.Combine(tempDir, "SampleFarmBot.cs");
        File.WriteAllText(sampleScript, """
        //cs_include Scripts/CoreBots.cs
        using Skua.Core.Interfaces;

        public class SampleFarmBot
        {
            public IScriptInterface Bot => IScriptInterface.Instance;
            public CoreBots Core => CoreBots.Instance;

            public void ScriptMain(IScriptInterface bot)
            {
                Core.SetOptions(disableClassSwap: true);

                // 1. Unbank an item from the scenario bank
                Core.Unbank("Ancient Relic");

                // 2. Complete quest #2 (Chieftain's Head) by farming on northmountain cell r2
                Core.RegisterQuests(2);
                while (!Bot.ShouldExit && Bot.Quests.Completed.Count < 1 && Bot.Player.Gold < 510000) // safety cap only: the quest watcher (a background flow) turns the quest in
                {
                    Core.KillMonster("northmountain", "r2", "Left", "*", log: false);
                }
                Core.CancelRegisteredQuests();

                // 3. Farm a direct drop item (5x Mystic Shard)
                Core.KillMonster("northmountain", "r2", "Left", "*", "Mystic Shard", 5, isTemp: false, log: false);

                Core.SetOptions(false);
            }
        }
        """);

        var scenario = new Scenario
        {
            Name = "SampleFarmBot Verification",
            Seed = 777,
            Player = new PlayerSetup
            {
                Username = "Tester",
                Level = 100,
                Gold = 500_000,
                Class = "ArchPaladin",
                MaxHealth = 10_000,
                DamagePerSecond = 8_000
            },
            Bank = new List<ItemSetup>
            {
                new() { Name = "Ancient Relic", Quantity = 3 }
            }
        };

        var report1 = runner.Run(new SimulationRunOptions
        {
            ScriptPath = sampleScript,
            Scenario = scenario,
            SeedOverride = 777
        });

        Assert(report1.Outcome == SimulationOutcome.Completed, $"Expected Completed, got {report1.Outcome} ({report1.ExceptionText ?? report1.StopReason})");
        Assert(report1.QuestsCompleted >= 1, $"Expected at least 1 quest completed, got {report1.QuestsCompleted}");
        Assert(report1.FinalState.Inventory.Any(i => i.Name == "Ancient Relic" && i.Quantity == 3), "Expected Ancient Relic x3 unbanked into inventory");
        Assert(report1.FinalState.Inventory.Any(i => i.Name == "Mystic Shard" && i.Quantity >= 5), "Expected Mystic Shard x5 farmed into inventory");
        Assert(report1.FinalState.Username == "Tester", "Expected FinalState.Username == Tester");
        Assert(report1.FinalState.MaxHealth == 10_000, "Expected FinalState.MaxHealth == 10000");
        Assert(report1.FinalState.EquippedClass == "ArchPaladin", $"Expected ArchPaladin equipped, got {report1.FinalState.EquippedClass}");
        Assert(report1.FinalState.Kills == report1.Kills && report1.Kills > 0, "Expected FinalState.Kills to match report");
        Assert(report1.FinalState.MapCells.Count > 0 && report1.FinalState.MapMonsters.Count > 0, "Expected map cells and monsters in snapshot");
        Assert(report1.FinalState.QuestDetails.Any(q => q.Id == 2 && q.TurnIns >= 1), "Expected Quest #2 turn-in in QuestDetails");
        Assert(report1.FinalState.MapsVisited.Contains("northmountain", StringComparer.OrdinalIgnoreCase), "Expected northmountain in MapsVisited");
        Console.WriteLine($"   OK: Completed in {report1.VirtualDuration} virtual time ({report1.RealDuration.TotalSeconds:F2}s real), Kills={report1.Kills}, QuestsCompleted={report1.QuestsCompleted}");

        // Export Scenario: the end of run 1 becomes the start of a new run (chained scripts).
        var exported = report1.EndScenario;
        Assert(exported != null && exported.Seed == scenario.Seed && exported.StartDate == report1.StartUtc + TimeSpan.FromSeconds(Math.Floor(report1.VirtualDuration.TotalSeconds)),
            "The exported scenario must start at the end of the run (same seed setting)");
        Assert(exported!.Player.Level == report1.FinalState.Level && exported.Player.Gold == report1.FinalState.Gold &&
               exported.Player.Class == "ArchPaladin" && exported.Location.Map == report1.FinalState.Map &&
               exported.Quests.Completed.SequenceEqual(report1.FinalState.CompletedQuests.OrderBy(id => id)),
            $"The exported scenario must keep level, gold, class, location and completed quests (lv {exported.Player.Level}/{report1.FinalState.Level}, " +
            $"gold {exported.Player.Gold}/{report1.FinalState.Gold}, class {exported.Player.Class}, map {exported.Location.Map}/{report1.FinalState.Map}, " +
            $"completed [{string.Join(",", exported.Quests.Completed)}] vs [{string.Join(",", report1.FinalState.CompletedQuests)}])");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<Scenario>(exported.ToJson(), Scenario.JsonOptions)!;
        var idleScript = Path.Combine(tempDir, "IdleBot.cs");
        File.WriteAllText(idleScript, "using Skua.Core.Interfaces;\npublic class IdleBot { public void ScriptMain(IScriptInterface bot) { bot.Sleep(1000); } }\n");
        var chained = runner.Run(new SimulationRunOptions { ScriptPath = idleScript, Scenario = reloaded });
        Assert(chained.Outcome == SimulationOutcome.Completed && chained.Seed == scenario.Seed &&
               chained.FinalState.Gold == report1.FinalState.Gold && chained.FinalState.Level == report1.FinalState.Level &&
               chained.FinalState.Inventory.Any(i => i.Name == "Ancient Relic" && i.Quantity == 3) &&
               chained.FinalState.Inventory.Any(i => i.Name == "Mystic Shard" && i.Quantity >= 5) &&
               chained.FinalState.CompletedQuests.Count == report1.FinalState.CompletedQuests.Count && chained.FinalState.EquippedClass == "ArchPaladin",
            $"A run from the exported scenario must start with the same character ({chained.Outcome}: {chained.ExceptionText ?? chained.StopReason})");
        Console.WriteLine($"   OK: Exported scenario reloaded (Lv {chained.FinalState.Level}, {chained.FinalState.Inventory.Count} items)");

        Console.WriteLine("2. Testing virtual clock determinism (same seed => identical outcome)...");
        var report2 = runner.Run(new SimulationRunOptions
        {
            ScriptPath = sampleScript,
            Scenario = scenario,
            SeedOverride = 777
        });

        Assert(report1.VirtualDuration == report2.VirtualDuration, $"Virtual duration mismatch: {report1.VirtualDuration} vs {report2.VirtualDuration}");
        Assert(report1.Kills == report2.Kills, $"Kills mismatch: {report1.Kills} vs {report2.Kills}");
        Assert(report1.FinalState.Gold == report2.FinalState.Gold, $"Gold mismatch: {report1.FinalState.Gold} vs {report2.FinalState.Gold}");
        Console.WriteLine($"   OK: Deterministic across runs (VirtualDuration={report2.VirtualDuration}, Kills={report2.Kills}, Gold={report2.FinalState.Gold})");
    }

    private static void TestCompileAndInspectScript(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("3. Testing standalone Compile & InspectScript option discovery...");
        var inspectScript = Path.Combine(tempDir, "InspectableBot.cs");
        File.WriteAllText(inspectScript, """
        using System.Collections.Generic;
        using Skua.Core.Interfaces;
        using Skua.Core.Options;

        public class InspectableBot
        {
            public enum FarmMode { Safe, Fast, Turbo }

            public string OptionsStorage = "InspectStorage";

            public List<IOption> Options = new()
            {
                new Option<bool>("AutoSell", "Auto Sell", "Sell junk", true),
                new Option<FarmMode>("Mode", "Farm Mode", "Execution speed", FarmMode.Fast)
            };

            public List<IOption> ExtraSelect = new()
            {
                new Option<int>("BatchSize", "Batch Size", "Items per batch", 25)
            };

            public void ScriptMain(IScriptInterface bot)
            {
            }
        }
        """);

        var compile = runner.Compile(inspectScript);
        Assert(compile.Success && compile.Main != null, "Expected InspectableBot to compile cleanly");
        Assert(compile.Main!.ScriptMainClasses.Contains("InspectableBot"), "Expected InspectableBot in ScriptMainClasses");

        var inspection = runner.InspectScript(inspectScript);
        Assert(inspection.Success, $"Expected InspectScript to succeed, got: {inspection.ErrorMessage}");
        Assert(inspection.ScriptClass == "InspectableBot", $"Unexpected ScriptClass: {inspection.ScriptClass}");
        Assert(inspection.OptionsStorage == "InspectStorage", $"Unexpected OptionsStorage: {inspection.OptionsStorage}");
        Assert(inspection.Options.Count == 3, $"Expected 3 options, got {inspection.Options.Count}");

        var modeOpt = inspection.Options.FirstOrDefault(o => o.Name == "Mode");
        Assert(modeOpt != null, "Expected 'Mode' option");
        Assert(modeOpt!.ValueType == "Enum", $"Expected Enum type for Mode, got {modeOpt.ValueType}");
        Assert(modeOpt.DefaultValue is string s && s == "Fast", $"Expected Mode default 'Fast' string, got {modeOpt.DefaultValue}");
        Assert(modeOpt.AllowedValues.SequenceEqual(new[] { "Safe", "Fast", "Turbo" }), "Expected FarmMode enum values");

        var batchOpt = inspection.Options.FirstOrDefault(o => o.Name == "BatchSize");
        Assert(batchOpt != null && batchOpt.Category == "ExtraSelect" && batchOpt.DefaultValue is int b && b == 25, "Expected ExtraSelect BatchSize=25");

        var brokenScript = Path.Combine(tempDir, "BrokenBot.cs");
        File.WriteAllText(brokenScript, "public class BrokenBot { public void ScriptMain(IScriptInterface bot) { int x = ; } }");
        var brokenInspection = runner.InspectScript(brokenScript);
        Assert(!brokenInspection.Success && brokenInspection.CompileResult.Errors.Count > 0, "Expected compile errors for BrokenBot");

        // Also test Skua MultiOptions ordering + whitespace section headers (" ")
        var multiScript = Path.Combine(tempDir, "MultiOptionsBot.cs");
        File.WriteAllText(multiScript, """
        using System.Collections.Generic;
        using Skua.Core.Interfaces;
        using Skua.Core.Options;

        public class MultiOptionsBot
        {
            public string OptionsStorage = "MultiTest";
            public string[] MultiOptions = { "Generic", "Select" };

            // Declared intentionally AFTER Select in MultiOptions order to verify MultiOptions ordering
            public List<IOption> Select = new()
            {
                new Option<bool>("1001", "Buy Sword", "Mode [select] only", false)
            };

            public List<IOption> Generic = new()
            {
                new Option<bool>("SkipOptions", "Skip options", "Skip popup", false),
                new Option<string>(" ", "Mode Explanation [all]", "Gets all items", "click here")
            };

            public void ScriptMain(IScriptInterface bot) { }
        }
        """);
        var multiInspection = runner.InspectScript(multiScript);
        Assert(multiInspection.Success, $"MultiOptions inspect failed: {multiInspection.ErrorMessage}");
        Assert(multiInspection.Options.Count == 3, $"Expected 3 multi options (including header), got {multiInspection.Options.Count}");
        Assert(multiInspection.Options[0].Category == "Generic" && multiInspection.Options[0].Name == "SkipOptions",
            $"Expected Generic first per MultiOptions order, got {multiInspection.Options[0].Category}/{multiInspection.Options[0].Name}");
        Assert(multiInspection.Options[1].IsHeader && multiInspection.Options[1].DisplayName == "Mode Explanation [all]",
            "Expected whitespace option to be flagged IsHeader=true");
        Assert(multiInspection.Options[2].Category == "Select" && multiInspection.Options[2].ValueType == "System.Boolean",
            "Expected Select boolean option second");

        Console.WriteLine($"   OK: Discovered {inspection.Options.Count} option(s), MultiOptions ordering & headers verified, compile diagnostics clean");
    }

    private static void TestSessionPauseResumeAndLiveSnapshot(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("4. Testing async SimulationSession Pause/Resume, stall immunity & live incremental trace...");
        var pauseScript = Path.Combine(tempDir, "SessionPauseBot.cs");
        File.WriteAllText(pauseScript, """
        using Skua.Core.Interfaces;

        public class SessionPauseBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("testarena", "r1", "Left");
                bot.Log("step-0");
                for (var i = 1; i <= 5 && !bot.ShouldExit; i++)
                {
                    bot.Sleep(150);
                    bot.Log($"step-{i}");
                }
                bot.Kill.Monster("Boss Guard");
                bot.Drops.Pickup("Guard Token");
            }
        }
        """);

        var scenario = new Scenario
        {
            Name = "Live Session Test",
            Seed = 123,
            Player = new PlayerSetup
            {
                Username = "LiveHero",
                Class = "Legion Revenant",
                DamagePerSecond = 6_000
            },
            Factions = new List<FactionSetup>
            {
                new() { Name = "Doomwood", Rank = 4 }
            },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["testarena"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup>
                    {
                        new()
                        {
                            Name = "Boss Guard",
                            Cell = "r1",
                            MaxHP = 5_000,
                            Level = 50,
                            Drops = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Guard Token"] = 1.0
                            }
                        }
                    }
                }
            },
            Limits = new LimitsSetup
            {
                MaxRealSeconds = 20,
                StallRealSeconds = 2.0
            }
        };

        using var session = runner.Start(
            new SimulationRunOptions { ScriptPath = pauseScript, Scenario = scenario },
            new SimulationSessionOptions { InitialSpeedMultiplier = 4.0 });

        var logCursor = 0;
        long traceCursor = 0;
        var streamedLogs = new List<TraceEntry>();

        var sawStep1 = SpinWaitUntil(() =>
        {
            var batch = session.ReadScriptLogsSince(logCursor, out logCursor);
            streamedLogs.AddRange(batch);
            return streamedLogs.Any(l => l.Text == "step-1") || session.State == SimulationState.Completed;
        }, TimeSpan.FromSeconds(10));

        Assert(sawStep1, "Timed out waiting for step-1 log");
        session.Pause();
        var becamePaused = SpinWaitUntil(() => session.State == SimulationState.Paused && session.IsPaused, TimeSpan.FromSeconds(2));
        Assert(becamePaused, $"Expected session to pause, state was {session.State}");

        var pausedSnap = session.GetLiveSnapshot();
        Assert(pausedSnap.World.Username == "LiveHero", $"Expected LiveHero, got {pausedSnap.World.Username}");
        Assert(pausedSnap.World.EquippedClass == "Legion Revenant", $"Expected Legion Revenant, got {pausedSnap.World.EquippedClass}");
        Assert(pausedSnap.World.CellMonsters.Any(m => m.Name == "Boss Guard"), "Expected Boss Guard in live CellMonsters");
        Assert(pausedSnap.World.Factions.Any(f => f.Name == "Doomwood" && f.Rank == 4), "Expected Doomwood rank 4 in live Factions");
        Assert(pausedSnap.TotalApiCalls > 0 && !string.IsNullOrEmpty(pausedSnap.LastApiCall), "Expected live API telemetry");

        var timelineBatch = session.ReadTimelineSince(traceCursor, out traceCursor, out var dropped);
        Assert(timelineBatch.Count > 0 && !dropped, "Expected live timeline entries without drops");

        // Wait longer than StallRealSeconds (2.0s) while paused; stall detector must remain suspended.
        Thread.Sleep(2300);
        Assert(session.State == SimulationState.Paused, $"Expected session to stay Paused across >2s stall window, state is {session.State}");
        var afterHoldSnap = session.GetLiveSnapshot();
        Assert(afterHoldSnap.VirtualTime == pausedSnap.VirtualTime, $"Expected virtual clock frozen during pause ({pausedSnap.VirtualTime} vs {afterHoldSnap.VirtualTime})");

        session.Resume();
        Assert(session.Completion.Wait(15_000), "Timed out waiting for resumed session to complete");
        var report = session.Completion.GetAwaiter().GetResult();

        Assert(report.Outcome == SimulationOutcome.Completed, $"Expected Completed after resume, got {report.Outcome} ({report.ExceptionText ?? report.StopReason})");
        Assert(report.FinalState.Inventory.Any(i => i.Name == "Guard Token" && i.Quantity >= 1), "Expected Guard Token drop picked up");

        while (true)
        {
            var tail = session.ReadScriptLogsSince(logCursor, out logCursor);
            if (tail.Count == 0)
                break;
            streamedLogs.AddRange(tail);
        }
        Assert(streamedLogs.Count == report.ScriptLogs.Count, $"Expected {report.ScriptLogs.Count} incremental logs, got {streamedLogs.Count}");
        Assert(streamedLogs.Select(l => l.Text).SequenceEqual(report.ScriptLogs.Select(l => l.Text)), "Expected incremental logs to match report logs identically");

        var finalLive = session.GetLiveSnapshot();
        Assert(finalLive.PausedTime >= TimeSpan.FromMilliseconds(1800), $"Expected >=1.8s recorded PausedTime, got {finalLive.PausedTime}");
        Console.WriteLine($"   OK: Paused {finalLive.PausedTime.TotalSeconds:F2}s safely (>2s stall limit), active={report.RealDuration.TotalSeconds:F2}s, logs={streamedLogs.Count}");
    }

    private static void TestSpeedPacingAndStopWhilePaused(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("5. Testing SpeedMultiplier pacing determinism & immediate Stop while StartPaused...");
        var pacedScript = Path.Combine(tempDir, "PacedBot.cs");
        File.WriteAllText(pacedScript, """
        using Skua.Core.Interfaces;

        public class PacedBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                for (var i = 0; i < 4; i++)
                {
                    bot.Sleep(200);
                    bot.Log($"tick-{i}");
                }
            }
        }
        """);

        var scenario = new Scenario { Name = "Pacing Test", Seed = 55 };
        var instantReport = runner.Run(new SimulationRunOptions { ScriptPath = pacedScript, Scenario = scenario });

        using var pacedSession = runner.Start(
            new SimulationRunOptions { ScriptPath = pacedScript, Scenario = scenario },
            new SimulationSessionOptions { InitialSpeedMultiplier = 8.0 });
        Assert(pacedSession.Completion.Wait(10_000), "Timed out waiting for paced run");
        var pacedReport = pacedSession.Completion.GetAwaiter().GetResult();

        Assert(instantReport.VirtualDuration == pacedReport.VirtualDuration, "Expected paced run to have identical virtual duration to instant run");
        Assert(pacedReport.RealDuration >= TimeSpan.FromMilliseconds(55), $"Expected >=55ms paced real duration for 800ms at 8x, got {pacedReport.RealDuration.TotalMilliseconds:F0}ms");

        var loopScript = Path.Combine(tempDir, "LoopBot.cs");
        File.WriteAllText(loopScript, """
        using Skua.Core.Interfaces;

        public class LoopBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                while (!bot.ShouldExit)
                {
                    bot.Sleep(500);
                }
            }
        }
        """);

        using var stopSession = runner.Start(
            new SimulationRunOptions { ScriptPath = loopScript, Scenario = scenario },
            new SimulationSessionOptions { StartPaused = true });

        var paused = SpinWaitUntil(() => stopSession.State == SimulationState.Paused && stopSession.IsPaused, TimeSpan.FromSeconds(5));
        Assert(paused, $"Expected StartPaused session to enter Paused state, got {stopSession.State}");
        Thread.Sleep(150);
        Assert(stopSession.GetLiveSnapshot().VirtualTime == TimeSpan.Zero, "Expected StartPaused virtual time to stay zero before execution");

        var stopSw = Stopwatch.StartNew();
        stopSession.Stop("cancelled while paused");
        Assert(stopSession.Completion.Wait(3_000), "Expected Stop while StartPaused to finish promptly");
        var stopReport = stopSession.Completion.GetAwaiter().GetResult();
        stopSw.Stop();

        Assert(stopReport.Outcome == SimulationOutcome.LimitReached, $"Expected LimitReached on user stop, got {stopReport.Outcome}");
        Assert(stopReport.StopReason == "cancelled while paused", $"Unexpected StopReason: {stopReport.StopReason}");
        Console.WriteLine($"   OK: Paced 8x took {pacedReport.RealDuration.TotalMilliseconds:F0}ms real for {pacedReport.VirtualDuration.TotalMilliseconds:F0}ms virtual; Stop-while-paused unwound in {stopSw.ElapsedMilliseconds}ms");
    }

    private static void TestXpLevelProgression(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("6. Testing XP -> Level progression, MaxHealth scaling & Lv 100 cap...");
        var levelScript = Path.Combine(tempDir, "LevelingBot.cs");
        File.WriteAllText(levelScript, """
        using Skua.Core.Interfaces;

        public class LevelingBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("xpfield", "r1", "Left");
                while (!bot.ShouldExit && bot.Player.Level < 4)
                {
                    bot.Kill.Monster("*");
                }
                bot.Log($"Reached Lv {bot.Player.Level} (XP {bot.Player.XP}/{bot.Player.RequiredXP})");
            }
        }
        """);

        Scenario BuildScenario(int startLevel, int maxHp) => new()
        {
            Name = "XP Progression Test",
            Seed = 99,
            Player = new PlayerSetup
            {
                Username = "LevelTester",
                Level = startLevel,
                MaxHealth = maxHp,
                Class = "Mage",
                DamagePerSecond = 2_000
            },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["xpfield"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup>
                    {
                        new() { Name = "Training Slime", Cell = "r1", MaxHP = 400, Level = 5 }
                    }
                }
            },
            Limits = new LimitsSetup
            {
                MaxVirtualMinutes = 10,
                MaxRealSeconds = 20
            }
        };

        var r1 = runner.Run(new SimulationRunOptions { ScriptPath = levelScript, Scenario = BuildScenario(1, 350) });
        Assert(r1.Outcome == SimulationOutcome.Completed, $"Expected Completed, got {r1.Outcome} ({r1.ExceptionText ?? r1.StopReason})");
        Assert(r1.FinalState.Level >= 4, $"Expected Level >= 4, got {r1.FinalState.Level}");
        Assert(r1.FinalState.MaxHealth == 350 + 35 * (r1.FinalState.Level - 1), $"Expected MaxHealth scaling on level-up, got {r1.FinalState.MaxHealth}");

        // Verify Lv 100 cap does not overflow or exceed 100
        var capScript = Path.Combine(tempDir, "CapBot.cs");
        File.WriteAllText(capScript, """
        using Skua.Core.Interfaces;

        public class CapBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("xpfield", "r1", "Left");
                bot.Kill.Monster("*");
                bot.Kill.Monster("*");
                var ratio = (double)bot.Player.XP / bot.Player.RequiredXP;
                bot.Log($"Lv {bot.Player.Level}, ratio={ratio}");
            }
        }
        """);

        var rCap = runner.Run(new SimulationRunOptions { ScriptPath = capScript, Scenario = BuildScenario(100, 10_000) });
        Assert(rCap.Outcome == SimulationOutcome.Completed, $"Expected Completed for cap check, got {rCap.Outcome}");
        Assert(rCap.FinalState.Level == 100 && rCap.FinalState.MaxHealth == 10_000, $"Expected Level=100 & MaxHealth=10000 at cap, got Lv {rCap.FinalState.Level} HP {rCap.FinalState.MaxHealth}");
        Console.WriteLine($"   OK: Leveled Lv 1 -> Lv {r1.FinalState.Level} in {r1.Kills} kills (MaxHP {r1.FinalState.MaxHealth}); cap at Lv 100 safe");
    }

    private static void TestMaxRealSecondsExcludesCompile(SkuaInstallation installation, string tempDir)
    {
        Console.WriteLine("7. Testing MaxRealSeconds excludes cold Roslyn compile duration...");
        var coldRunner = new SimulationRunner(installation);
        var coreScript = Path.Combine(tempDir, "ColdCompileBot.cs");
        File.WriteAllText(coreScript, """
        //cs_include Scripts/CoreBots.cs
        using Skua.Core.Interfaces;

        public class ColdCompileBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Sleep(250);
                bot.Log("execution completed after cold compile");
            }
        }
        """);

        var shortLimitScenario = new Scenario
        {
            Name = "Short Execution Real Limit",
            Seed = 11,
            Limits = new LimitsSetup
            {
                MaxRealSeconds = 1.5,
                MaxVirtualMinutes = 5,
                StallRealSeconds = 10
            }
        };

        var report = coldRunner.Run(new SimulationRunOptions
        {
            ScriptPath = coreScript,
            Scenario = shortLimitScenario
        });

        Assert(report.CompileDuration > TimeSpan.FromSeconds(1.5), $"Expected cold compile > 1.5s to verify isolation, got {report.CompileDuration.TotalSeconds:F2}s");
        Assert(report.Outcome == SimulationOutcome.Completed, $"Expected Completed despite compile ({report.CompileDuration.TotalSeconds:F2}s) > MaxRealSeconds (1.5s), got {report.Outcome} ({report.StopReason ?? report.ExceptionText})");
        Console.WriteLine($"   OK: Cold compile took {report.CompileDuration.TotalSeconds:F2}s (> 1.5s limit), script still executed and completed cleanly");
    }

    private static void TestStatProgressionAndEnhancements(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("8. Testing AQW stat progression, enhancement patterns, 50% haste cap & enhanceItemShop packet...");
        var statScript = Path.Combine(tempDir, "StatCheckBot.cs");
        File.WriteAllText(statScript, """
        using System.Linq;
        using Skua.Core.Interfaces;
        using Skua.Core.Models.Items;

        public class StatCheckBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                var s = bot.Player.Stats;
                bot.Log($"STATS STR={s.Strength} INT={s.Intellect} END={s.Endurance} DEX={s.Dexterity} WIS={s.Wisdom} LCK={s.Luck} AP={s.AttackPower} SP={s.SpellPower} HP={bot.Player.MaxHealth} Crit={s.CriticalChance:F3} CritMult={s.CriticalMultiplier:F2} Haste={s.Haste:F3} Dodge={s.EvasionChance:F3}");

                // Optionally enhance Default Staff via %xt%zm%enhanceItemShop% packet
                var staff = bot.Inventory.Items.FirstOrDefault(i => i.Name == "Default Staff");
                if (staff != null && bot.Player.Level >= 10)
                {
                    var beforeSp = s.SpellPower;
                    bot.Send.Packet($"%xt%zm%enhanceItemShop%1%{staff.ID}%51001%141%");
                    var after = bot.Player.Stats;
                    bot.Log($"AFTER_ENH BeforeSP={beforeSp} AfterSP={after.SpellPower} EnhPat={staff.EnhancementPatternID} EnhLvl={staff.EnhancementLevel}");
                }
            }
        }
        """);

        // (a) Lv 1 unenhanced Mage
        var lv1 = runner.Run(new SimulationRunOptions
        {
            ScriptPath = statScript,
            Scenario = new Scenario
            {
                Name = "Lv1 Unenhanced Mage",
                Seed = 10,
                Player = new PlayerSetup { Level = 1, Class = "Mage", ClassRank = 1 },
                Inventory = new List<ItemSetup>
                {
                    new() { Name = "Default Staff", Category = "Staff", Equipped = true, EnhancementLevel = 0, EnhancementPatternID = 0 }
                },
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });
        Assert(lv1.Outcome == SimulationOutcome.Completed, $"Lv1 failed: {lv1.ExceptionText ?? lv1.StopReason}");
        Assert(lv1.FinalState.MaxHealth is >= 360 and <= 450, $"Expected realistic Lv1 Mage MaxHP (~370), got {lv1.FinalState.MaxHealth}");
        Assert(lv1.FinalState.Intellect > lv1.FinalState.Strength, "Lv1 Mage should favor INT over STR");

        // (b) Lv 100 Full Wizard Mage vs Lv 100 Full Lucky Mage
        List<ItemSetup> BuildFullSet(int enhPat, int enhLvl, int? procId = null) => new()
        {
            new() { Name = "Arcane Staff", Category = "Staff", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = enhLvl, ProcID = procId },
            new() { Name = "Mage", Category = "Class", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = enhLvl },
            new() { Name = "Wizard Hood", Category = "Helm", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = enhLvl },
            new() { Name = "Spell Cape", Category = "Cape", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = enhLvl }
        };

        var wiz100 = runner.Run(new SimulationRunOptions
        {
            ScriptPath = statScript,
            Scenario = new Scenario
            {
                Name = "Lv100 Wizard Mage",
                Seed = 10,
                Player = new PlayerSetup { Level = 100, Class = "Mage", ClassRank = 10 },
                Inventory = BuildFullSet(6, 100),
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });
        var luck100 = runner.Run(new SimulationRunOptions
        {
            ScriptPath = statScript,
            Scenario = new Scenario
            {
                Name = "Lv100 Lucky Mage",
                Seed = 10,
                Player = new PlayerSetup { Level = 100, Class = "Mage", ClassRank = 10 },
                Inventory = BuildFullSet(9, 100),
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });
        Assert(wiz100.FinalState.Intellect > luck100.FinalState.Intellect && wiz100.FinalState.SpellPower > luck100.FinalState.SpellPower,
            $"Wizard should have higher INT/SP than Lucky (Wiz INT={wiz100.FinalState.Intellect}, Luck INT={luck100.FinalState.Intellect})");
        Assert(luck100.FinalState.Luck > wiz100.FinalState.Luck && luck100.FinalState.CritMultiplier > wiz100.FinalState.CritMultiplier,
            $"Lucky should have higher LCK/CritMult than Wizard (Luck={luck100.FinalState.Luck} CritMult={luck100.FinalState.CritMultiplier:F2})");
        Assert(wiz100.FinalState.MaxHealth > lv1.FinalState.MaxHealth * 5, "Lv 100 Mage MaxHP must scale far above Lv 1");

        // (c) Verify 50% Haste cap under stacked Forge specials (Vim Helm 25 + Avarice Cape 12 + Dauntless Weapon Proc 14)
        var hasteCap = runner.Run(new SimulationRunOptions
        {
            ScriptPath = statScript,
            Scenario = new Scenario
            {
                Name = "Lv100 Forge Haste Cap",
                Seed = 10,
                Player = new PlayerSetup { Level = 100, Class = "Chrono Assassin", ClassRank = 10 },
                Inventory = new List<ItemSetup>
                {
                    new() { Name = "Forge Blade", Category = "Sword", Equipped = true, EnhancementPatternID = 9, EnhancementLevel = 100, ProcID = 14 },
                    new() { Name = "Chrono Assassin", Category = "Class", Equipped = true, EnhancementPatternID = 9, EnhancementLevel = 100 },
                    new() { Name = "Vim Hood", Category = "Helm", Equipped = true, EnhancementPatternID = 25, EnhancementLevel = 100 },
                    new() { Name = "Avarice Cape", Category = "Cape", Equipped = true, EnhancementPatternID = 12, EnhancementLevel = 100 }
                },
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });
        Assert(hasteCap.FinalState.Haste <= 0.5001, $"AQW Haste cap is 50% (0.5), got {hasteCap.FinalState.Haste}");

        // (d) Verify live %xt%zm%enhanceItemShop% boosts stats dynamically
        var enhTest = runner.Run(new SimulationRunOptions
        {
            ScriptPath = statScript,
            Scenario = new Scenario
            {
                Name = "Live Enhance Packet",
                Seed = 10,
                Player = new PlayerSetup { Level = 20, Class = "Mage", ClassRank = 5, Gold = 5000 },
                Inventory = new List<ItemSetup>
                {
                    new() { Name = "Default Staff", Id = 16, Category = "Staff", Equipped = true, EnhancementLevel = 0, EnhancementPatternID = 0 }
                },
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });
        Assert(enhTest.Outcome == SimulationOutcome.Completed, $"Enhance packet run failed: {enhTest.ExceptionText}");
        var afterLine = enhTest.ScriptLogs.FirstOrDefault(l => l.Text.Contains("AFTER_ENH"));
        Assert(afterLine != null && afterLine.Text.Contains("EnhPat=6") && afterLine.Text.Contains("EnhLvl=20"),
            $"Expected EnhPat=6 EnhLvl=20 after enhanceItemShop, got '{afterLine?.Text}'");
        var beforePart = int.Parse(afterLine!.Text.Split("BeforeSP=")[1].Split(' ')[0]);
        var afterPart = int.Parse(afterLine.Text.Split("AfterSP=")[1].Split(' ')[0]);
        Assert(afterPart > beforePart, $"Expected SpellPower increase after Wizard enhance ({beforePart} -> {afterPart})");
        Console.WriteLine($"   OK: Lv1 HP={lv1.FinalState.MaxHealth}, Lv100 Wiz INT={wiz100.FinalState.Intellect}/SP={wiz100.FinalState.SpellPower}, Luck CritMult={luck100.FinalState.CritMultiplier:F2}, HasteCap={hasteCap.FinalState.Haste:F2}");
    }

    private static void TestClassCombatDodgeCritHealAndDeterminism(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("9. Testing class dodge, crit, self-heal, natural XP HP scaling & RNG determinism...");
        var combatScript = Path.Combine(tempDir, "CombatBot.cs");
        File.WriteAllText(combatScript, """
        using Skua.Core.Interfaces;

        public class CombatBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("arena", "r1", "Left");
                for (int i = 0; i < 6 && !bot.ShouldExit; i++)
                    bot.Kill.Monster("*");
            }
        }
        """);

        Scenario BuildCombatScenario(string cls, int level, int enhPat) => new()
        {
            Name = $"{cls} Arena Test",
            Seed = 2026,
            Player = new PlayerSetup { Level = level, Class = cls, ClassRank = 10 },
            Inventory = new List<ItemSetup>
            {
                new() { Name = "Test Blade", Category = "Sword", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = level },
                new() { Name = cls, Category = "Class", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = level },
                new() { Name = "Test Helm", Category = "Helm", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = level },
                new() { Name = "Test Cape", Category = "Cape", Equipped = true, EnhancementPatternID = enhPat, EnhancementLevel = level }
            },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["arena"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup>
                    {
                        new() { Name = "Arena Bruiser", Cell = "r1", MaxHP = 25_000, Level = 30 }
                    }
                }
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 10, MaxRealSeconds = 20 }
        };

        var ynr1 = runner.Run(new SimulationRunOptions { ScriptPath = combatScript, Scenario = BuildCombatScenario("Yami no Ronin", 80, 3) });
        var ynr2 = runner.Run(new SimulationRunOptions { ScriptPath = combatScript, Scenario = BuildCombatScenario("Yami no Ronin", 80, 3) });
        var ap = runner.Run(new SimulationRunOptions { ScriptPath = combatScript, Scenario = BuildCombatScenario("ArchPaladin", 80, 9) });

        Assert(ynr1.Outcome == SimulationOutcome.Completed && ap.Outcome == SimulationOutcome.Completed, "Combat test did not complete");
        Assert(ynr1.FinalState.EvasionChance > 0.5 && ynr1.FinalState.PlayerDodges > 0,
            $"Expected Yami no Ronin high dodge & recorded dodges, got Dodge={ynr1.FinalState.EvasionChance:F2}, Dodges={ynr1.FinalState.PlayerDodges}");
        Assert(ap.FinalState.PlayerCrits > 0 && ap.FinalState.TotalHealingDone > 0,
            $"Expected ArchPaladin crits & healing during combat, got Crits={ap.FinalState.PlayerCrits}, Healed={ap.FinalState.TotalHealingDone}");
        Assert(ynr1.FinalState.PlayerDodges == ynr2.FinalState.PlayerDodges &&
               ynr1.FinalState.PlayerCrits == ynr2.FinalState.PlayerCrits &&
               ynr1.FinalState.TotalDamageDealt == ynr2.FinalState.TotalDamageDealt &&
               ynr1.VirtualDuration == ynr2.VirtualDuration,
            "Same seed must produce identical crits, dodges, damage, and virtual duration");

        // Natural AQW HP progression without hardcoded MaxHealth override
        var lvUpScenario = new Scenario
        {
            Name = "Natural AQW LevelUp",
            Seed = 55,
            Player = new PlayerSetup { Level = 1, Class = "Mage", ClassRank = 1 },
            Inventory = new List<ItemSetup>
            {
                new() { Name = "Default Staff", Category = "Staff", Equipped = true, EnhancementLevel = 0, EnhancementPatternID = 0 }
            },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["arena"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup>
                    {
                        // Level set by the scenario (overrides the captured Frogzard): base XP = 10 x 15 per kill.
                        new() { Name = "Frogzard", Cell = "r1", MaxHP = 60, Level = 15 }
                    }
                }
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 10, MaxRealSeconds = 20 }
        };
        var nat = runner.Run(new SimulationRunOptions { ScriptPath = combatScript, Scenario = lvUpScenario });
        Assert(nat.Outcome == SimulationOutcome.Completed && nat.FinalState.Level > 1 && nat.FinalState.MaxHealth > 400,
            $"Expected realistic Lv1->Lv{nat.FinalState.Level} HP scaling (>400), got MaxHP={nat.FinalState.MaxHealth}");

        Console.WriteLine($"   OK: YnR Dodges={ynr1.FinalState.PlayerDodges}, AP Healed={ap.FinalState.TotalHealingDone}/Crits={ap.FinalState.PlayerCrits}, Nat LvUp 1->{nat.FinalState.Level} HP={nat.FinalState.MaxHealth}");
    }

    private static void TestCatalogClassCalibration(SkuaInstallation installation, string tempDir)
    {
        Console.WriteLine("10. Testing Collector Class & Skill profile loading into SimulationRunner...");
        var dataPath = Path.Combine(tempDir, "custom_gamedata.json");
        File.WriteAllText(dataPath, """
        {
          "SchemaVersion": 1,
          "Classes": {
            "Chrono Testblade": {
              "Name": "Chrono Testblade",
              "InferredStatModel": "Dodge Melee",
              "Skills": [
                { "Slot": 0, "ActID": 1, "SkillID": 1, "Name": "Chrono Slash", "Type": "p", "Damage": 1.0, "CooldownMs": 1500, "Auto": true },
                { "Slot": 1, "ActID": 2, "SkillID": 2, "Name": "Time Stitch", "Type": "h", "Damage": -1.5, "CooldownMs": 6000, "IsHealHint": true }
              ],
              "Combat": {
                "Hits": 80,
                "Crits": 40,
                "TotalDamageDealt": 120000,
                "LevelSumForDamage": 6000,
                "HealEvents": 20,
                "TotalHealingDone": 18000,
                "LevelSumForHeal": 1000,
                "AttacksTaken": 50,
                "DodgesTaken": 30
              }
            }
          }
        }
        """);

        var skillScript = Path.Combine(tempDir, "SkillInspectBot.cs");
        File.WriteAllText(skillScript, """
        using Skua.Core.Interfaces;

        public class SkillInspectBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log($"SKILL0={bot.Player.Skills[0].Name} SKILL1={bot.Player.Skills[1].Name} Dodge={bot.Player.Stats.EvasionChance:F2}");
            }
        }
        """);

        var customRunner = new SimulationRunner(installation);
        var rep = customRunner.Run(new SimulationRunOptions
        {
            ScriptPath = skillScript,
            GameDataPath = dataPath,
            Scenario = new Scenario
            {
                Name = "Catalog Class Calibration",
                Seed = 1,
                Player = new PlayerSetup { Level = 50, Class = "Chrono Testblade", ClassRank = 10 },
                Limits = new LimitsSetup { MaxRealSeconds = 20 }
            }
        });

        Assert(rep.Outcome == SimulationOutcome.Completed, $"Catalog calibration failed: {rep.ExceptionText ?? rep.StopReason}");
        Assert(rep.ScriptLogs.Any(l => l.Text.Contains("SKILL0=Chrono Slash") && l.Text.Contains("SKILL1=Time Stitch")),
            "Expected Bot.Player.Skills to populate from captured Collector sAct skills");
        Assert(rep.FinalState.EvasionChance > 0.35, $"Expected blended Dodge Melee evasion (>0.35), got {rep.FinalState.EvasionChance:F2}");
        Console.WriteLine($"   OK: Custom captured skills & Dodge calibration verified (Evasion={rep.FinalState.EvasionChance:F2})");
    }

    private static void TestBoostsEnhancementUnlocksAndLongVirtualDuration(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("11. Testing Equipment/Consumable Boosts, Awe/Forge Unlock Quests & Unlimited/Multi-Day Virtual Clock...");

        var boostScript = Path.Combine(tempDir, "BoostAndUnlockBot.cs");
        File.WriteAllText(boostScript, """
        using System.Linq;
        using Skua.Core.Interfaces;
        using Skua.Core.Models;
        using Skua.Core.Models.Items;

        public class BoostAndUnlockBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                var grimRank = bot.Reputation.GetRank("Grimskull Trolling");
                var weap = bot.Inventory.Items.FirstOrDefault(i => i.Equipped && i.ItemGroup == "Weapon");
                var armor = bot.Inventory.Items.FirstOrDefault(i => i.Equipped && i.Category == ItemCategory.Armor);
                var helm = bot.Inventory.Items.FirstOrDefault(i => i.Equipped && i.Category == ItemCategory.Helm);
                var xpBoostOn = bot.Boosts.UseExperienceBoost;
                bot.Log($"UNLOCKS GrimRank={grimRank} Weap={weap?.Name} WeapMeta={weap?.Meta} Armor={armor?.Name} Helm={helm?.Name} XpBoost={xpBoostOn}");

                // Advance > 2 days of virtual time to verify multi-day formatting & unlimited mode
                bot.Sleep(2 * 24 * 60 * 60 * 1000 + 3_600_000);
            }
        }
        """);

        var boostedScenario = new Scenario
        {
            Name = "Endgame Boosts + Unlocks + Unlimited Virtual",
            Seed = 7,
            Player = new PlayerSetup { Level = 100, Class = "Void Highlord", ClassRank = 10 },
            Boosts = new BoostsSetup
            {
                XpBoost = true,
                GoldBoost = true,
                ClassPointsBoost = true,
                RepBoost = true,
                ServerBoost = true,
                EquipmentBoosts = new List<GearSlotBoostSetup>
                {
                    new() { Slot = "Weapon", ItemName = "Necrotic Sword of Doom", Meta = "dmgAll:1.51" },
                    new() { Slot = "Armor", ItemName = "Radiant Goddess of War" },
                    new() { Slot = "Helm", ItemName = "Head of the Legion Beast" },
                    new() { Slot = "Cape", ItemName = "Awescended Omni Wings" },
                    new() { Slot = "Pet", ItemName = "Polly Roger" }
                }
            },
            Enhancements = new EnhancementsSetup
            {
                UnlockedAwe = new List<string> { "Spiral Carve", "Awe Blast" },
                UnlockedForge = new List<string> { "Valiance", "Dauntless", "Anima", "Vainglory", "Hearty" }
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 0, MaxRealSeconds = 0 } // 0 = Unlimited virtual & real time!
        };

        // Verify AQW highest-in-category stacking across multi-boost real AQW gear via StatEngine preview
        var preview = StatEngine.PreviewScenarioStats(boostedScenario, GameDataCatalog.Load(null, null));
        Assert(Math.Abs(preview.GearBoosts.DamageAllMultiplier - 1.51) < 0.001, $"Expected NSoD dmgAll 1.51, got {preview.GearBoosts.DamageAllMultiplier}");
        Assert(Math.Abs(preview.GearBoosts.RaceDamageMultipliers["Undead"] - 1.50) < 0.001, $"Expected RGoW Undead 1.50 (overriding HotLB 1.40 & Polly 1.30), got {preview.GearBoosts.RaceDamageMultipliers["Undead"]}");
        Assert(Math.Abs(preview.GearBoosts.XpMultiplier - 1.30) < 0.001, $"Expected HotLB XP 1.30, got {preview.GearBoosts.XpMultiplier}");
        Assert(Math.Abs(preview.GearBoosts.ClassPointsMultiplier - 1.50) < 0.001, $"Expected RGoW/Awescended CP 1.50, got {preview.GearBoosts.ClassPointsMultiplier}");
        Assert(Math.Abs(preview.GearBoosts.RepMultiplier - 1.50) < 0.001, $"Expected RGoW/Awescended Rep 1.50, got {preview.GearBoosts.RepMultiplier}");
        Assert(Math.Abs(preview.GearBoosts.GoldMultiplier - 1.30) < 0.001, $"Expected Gold 1.30, got {preview.GearBoosts.GoldMultiplier}");

        var plainScenario = new Scenario
        {
            Name = "Plain VHL No Boosts",
            Seed = 7,
            Player = new PlayerSetup { Level = 100, Class = "Void Highlord", ClassRank = 10 },
            Limits = new LimitsSetup { MaxVirtualMinutes = 60, MaxRealSeconds = 20 }
        };

        var repBoosted = runner.Run(new SimulationRunOptions { ScriptPath = boostScript, Scenario = boostedScenario });
        var repPlain = runner.Run(new SimulationRunOptions { ScriptPath = boostScript, Scenario = plainScenario });

        Assert(repBoosted.Outcome == SimulationOutcome.Completed, $"Boosted unlimited run failed: {repBoosted.ExceptionText ?? repBoosted.StopReason}");
        Assert(repBoosted.VirtualDuration.TotalDays >= 2.0, $"Expected >2 virtual days in unlimited mode, got {repBoosted.VirtualDuration}");
        Assert(repBoosted.FinalState.CompletedQuests.Contains(2937) &&
               repBoosted.FinalState.CompletedQuests.Contains(8741) &&
               repBoosted.FinalState.CompletedQuests.Contains(9172) &&
               repBoosted.FinalState.CompletedQuests.Contains(9466),
            "Expected Awe (#2937) and Forge (#8741 Valiance, #9172 Dauntless, #9466 Hearty) quests auto-unlocked");
        Assert(repBoosted.ScriptLogs.Any(l =>
                l.Text.Contains("GrimRank=7") &&
                l.Text.Contains("Weap=Necrotic Sword of Doom") &&
                l.Text.Contains("WeapMeta=dmgAll:1.51") &&
                l.Text.Contains("Armor=Radiant Goddess of War") &&
                l.Text.Contains("Helm=Head of the Legion Beast") &&
                l.Text.Contains("XpBoost=True")),
            $"Expected real AQW boosted items & GrimRank=7 in logs, got: {string.Join(" | ", repBoosted.ScriptLogs.Select(l => l.Text))}");
        Assert(repBoosted.FinalState.EffectiveDps > repPlain.FinalState.EffectiveDps * 1.4,
            $"Expected +51% dmgAll & Forge enhancements to raise DPS significantly (Boosted={repBoosted.FinalState.EffectiveDps:F0} vs Plain={repPlain.FinalState.EffectiveDps:F0})");

        // Verify SimClock multi-day formatting
        var clk = new SimClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var formatted2Days = clk.Format(2L * 24 * 60 * 60 * 1000 + 3_600_000);
        Assert(formatted2Days == "2d 01:00:00.000", $"Unexpected multi-day clock format: {formatted2Days}");

        Console.WriteLine($"   OK: Boosts/Unlocks/Unlimited verified (DPS {repPlain.FinalState.EffectiveDps:F0} -> {repBoosted.FinalState.EffectiveDps:F0}, Clock={formatted2Days})");
    }

    private static void TestKillQuestHintsOnDemandMonstersAndStallDetector(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("12. Testing KillQuest AST hints, on-demand monsters on synthesized maps & no-progress (Stalled) detector...");

        // (a) KillQuest(id, map, "name") and KillQuest(id, map, new[] {...}) feed synthesized maps;
        // (b) a monster requested by a runtime-built name is spawned on demand instead of looping forever.
        var hintScript = Path.Combine(tempDir, "KillQuestHintBot.cs");
        File.WriteAllText(hintScript, """
        using System.Linq;
        using Skua.Core.Interfaces;

        public class KillQuestHintBot
        {
            // Stand-ins with CoreStory's signatures: only the literal arguments matter to the hint extractor.
            public void KillQuest(int questId, string mapName, string monsterName) { }
            public void KillQuest(int questId, string mapName, string[] monsterNames) { }

            public void ScriptMain(IScriptInterface bot)
            {
                KillQuest(165, "zzsimlair", "Sim Wyvern");
                KillQuest(166, "zzsimpeak", new[] { "Alpha Beast", "Beta Beast" });

                bot.Map.Join("zzsimlair");
                bot.Log("LAIR " + string.Join(",", bot.Monsters.MapMonsters.Select(m => m.Name).Distinct()));
                bot.Map.Join("zzsimpeak");
                bot.Log("PEAK " + string.Join(",", bot.Monsters.MapMonsters.Select(m => m.Name).Distinct()));

                bot.Map.Join("zzsimvoid");
                var runtimeName = string.Concat("Runtime", " Golem");
                bot.Kill.Monster(runtimeName);
            }
        }
        """);

        var strongPlayer = new PlayerSetup { Username = "Tester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 };
        var hintReport = runner.Run(new SimulationRunOptions
        {
            ScriptPath = hintScript,
            Scenario = new Scenario { Name = "KillQuest hints", Player = strongPlayer, Limits = new LimitsSetup { MaxVirtualMinutes = 30 } },
            SeedOverride = 5
        });
        var logs = string.Join(" | ", hintReport.ScriptLogs.Select(l => l.Text));
        Assert(hintReport.Outcome == SimulationOutcome.Completed, $"Hint run expected Completed, got {hintReport.Outcome} ({hintReport.ExceptionText ?? hintReport.StopReason})");
        Assert(hintReport.ScriptLogs.Any(l => l.Text.StartsWith("LAIR") && l.Text.Contains("Sim Wyvern")), $"Expected 'Sim Wyvern' synthesized from KillQuest(string) hint, logs: {logs}");
        Assert(hintReport.ScriptLogs.Any(l => l.Text.StartsWith("PEAK") && l.Text.Contains("Alpha Beast") && l.Text.Contains("Beta Beast")), $"Expected both monsters from KillQuest(string[]) hint, logs: {logs}");
        Assert(hintReport.KillsByMonster.GetValueOrDefault("Runtime Golem") >= 1, $"Expected the on-demand 'Runtime Golem' to be killed, kills: {string.Join(", ", hintReport.KillsByMonster.Select(k => $"{k.Key}={k.Value}"))}");

        // (c) A tight polling loop that never progresses (the /lair bug shape) must stop as Stalled, deterministically.
        var stallScript = Path.Combine(tempDir, "NoProgressLoopBot.cs");
        File.WriteAllText(stallScript, """
        using Skua.Core.Interfaces;

        public class NoProgressLoopBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzsimloop");
                while (!bot.ShouldExit)
                    bot.TempInv.Contains("Never Drops", 12);
            }
        }
        """);

        var stallScenario = new Scenario
        {
            Name = "No progress loop",
            Player = strongPlayer,
            Limits = new LimitsSetup { MaxVirtualMinutes = 120, MaxNoProgressMinutes = 3 }
        };
        var stall = runner.Run(new SimulationRunOptions { ScriptPath = stallScript, Scenario = stallScenario, SeedOverride = 5 });
        Assert(stall.Outcome == SimulationOutcome.Stalled, $"Expected Stalled, got {stall.Outcome} ({stall.ExceptionText ?? stall.StopReason})");
        Assert(stall.VirtualDuration >= TimeSpan.FromMinutes(3) && stall.VirtualDuration < TimeSpan.FromMinutes(4),
            $"Expected the stall to be detected right after 3 virtual minutes, got {stall.VirtualDuration}");
        Assert(stall.StopReason?.Contains("Contains") == true, $"Expected the repeated TempInv.Contains call to be named, got: {stall.StopReason}");

        var stallAgain = runner.Run(new SimulationRunOptions { ScriptPath = stallScript, Scenario = stallScenario, SeedOverride = 5 });
        Assert(stallAgain.Outcome == SimulationOutcome.Stalled && stallAgain.VirtualDuration == stall.VirtualDuration,
            $"Stall detection must be deterministic: {stall.VirtualDuration} vs {stallAgain.VirtualDuration}");

        // Disabled (0) must fall back to the virtual time limit.
        stallScenario.Limits.MaxNoProgressMinutes = 0;
        stallScenario.Limits.MaxVirtualMinutes = 4;
        var disabled = runner.Run(new SimulationRunOptions { ScriptPath = stallScript, Scenario = stallScenario, SeedOverride = 5 });
        Assert(disabled.Outcome == SimulationOutcome.LimitReached, $"Expected LimitReached with the detector disabled, got {disabled.Outcome}");

        Console.WriteLine($"   OK: KillQuest hints + on-demand monster verified; Stalled after {stall.VirtualDuration} ({stall.TotalApiCalls:N0} calls)");
    }

    private static void TestSchemaV2QuestGatedAndUnknownRateDrops(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("14. Testing schema v2 drops: quest-gated drops need the quest active; unknown rates are synthesized, never 0 %...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_v2_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 2,
          "Maps": { "zzgatemap": { "Name": "zzgatemap", "Cells": { "Enter": ["Spawn"], "r1": ["Left"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990001, "Name": "Gate Beast", "Cell": "r1", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990001": { "Key": "id:990001", "ID": 990001, "Name": "Gate Beast", "Level": 5, "MaxHP": 300, "KillCount": 50,
            "KillsWhileQuestActive": { "802": 10 },
            "Drops": {
              "990101": { "ItemID": 990101, "ItemName": "Gated Fang", "DropEvents": 10, "TrackedDropEvents": 10, "EligibleKills": 10,
                          "QuestGated": true, "RequiredQuestIds": [802], "RateBasis": "quest_active_kills" },
              "990102": { "ItemID": 990102, "ItemName": "Official Pelt", "IsOfficialDrop": true, "DropEvents": 0, "EligibleKills": 50, "RateBasis": "unverified" }
            } } }
        }
        """);

        var script = Path.Combine(tempDir, "GatedDropBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class GatedDropBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzgatemap");
                for (var i = 0; i < 6; i++)
                {
                    bot.Hunt.Monster("Gate Beast");
                    bot.Sleep(13_000); // single spawn: wait for its respawn (no measured respawn here: official ~4 s)
                }
            }
        }
        """);

        Scenario MakeScenario(bool questActive) => new()
        {
            Name = questActive ? "Gated quest active" : "Gated quest inactive",
            Player = new PlayerSetup { Username = "Tester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 },
            Quests = new QuestSetup { Active = questActive ? new List<int> { 802 } : new List<int>() },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };

        var inactive = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(false), GameDataPath = gameDataPath, SeedOverride = 3 });
        var active = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(true), GameDataPath = gameDataPath, SeedOverride = 3 });

        Assert(inactive.Outcome == SimulationOutcome.Completed && active.Outcome == SimulationOutcome.Completed,
            $"Both runs must complete ({inactive.Outcome}: {inactive.StopReason}; {active.Outcome}: {active.StopReason})");
        Assert(inactive.Kills >= 6 && active.Kills >= 6, $"Expected 6 kills per run (inactive={inactive.Kills}, active={active.Kills})");
        Assert(inactive.DropsByItem.GetValueOrDefault("Gated Fang") == 0,
            $"A quest-gated drop must not drop without its quest active (got {inactive.DropsByItem.GetValueOrDefault("Gated Fang")})");
        Assert(active.DropsByItem.GetValueOrDefault("Gated Fang") >= 6,
            $"With the quest active the measured 100 % gated rate must apply (got {active.DropsByItem.GetValueOrDefault("Gated Fang")})");
        Assert(inactive.DropsByItem.GetValueOrDefault("Official Pelt") > 0 &&
               inactive.SynthesizedData.Any(s => s.Key.StartsWith("droprate:gate beast:990102")),
            "An official drop with no measured rate must use a flagged synthesized chance instead of 0 %");
        Console.WriteLine($"   OK: Gated Fang inactive=0 / active={active.DropsByItem["Gated Fang"]}, Official Pelt synthesized={inactive.DropsByItem["Official Pelt"]}");
    }

    /// <summary>
    /// Farm targets with quests carried over from the real game (diagnostic scenarios keep the character's active quests):
    /// an active quest needing some other item must not hide the item the script hunts (a merge ingredient, Grimgaol's
    /// Champion's Seal), and an uncaptured map item gives what the quest the script accepted last needs, not what an
    /// older scenario quest needs.
    /// </summary>
    /// <summary>
    /// skuasim harvest-plan and tools/skua/SkuaSim_Harvester.cs: the scanner reads maps/shops/quests from literal
    /// arguments (room suffixes dropped, string.Join and Cached-Scripts ignored, EnsureComplete's item ID not a quest),
    /// the plan keeps only what the data lacks, and the harvester, run inside the simulator, visits the map's cells,
    /// requests each monster type's drop list once, loads shops and quests, writes no character name and resumes from
    /// its progress file.
    /// </summary>
    private static void TestHarvestPlanAndHarvester(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("28. Testing the harvest plan and the Skua harvester...");
        var dir = Path.Combine(tempDir, "harvest");
        var scripts = Path.Combine(dir, "Scripts");
        Directory.CreateDirectory(Path.Combine(scripts, "Story"));
        Directory.CreateDirectory(Path.Combine(scripts, "Cached-Scripts"));
        File.WriteAllText(Path.Combine(scripts, "Story", "A.cs"), """
        public class A
        {
            public void ScriptMain(object bot)
            {
                Core.Join("ZzHarvestA-1e9");
                Core.KillMonster("zzharvesta", "r1", "Left", "Harvest Slime");
                Core.KillMonster("zzharvesta", "r1", "Left", "Harvest Slime", "Slime Gel", 5, false);
                Core.HuntMonster("zzharvesta", "Harvest Bat", "Bat Fang");
                Core.HuntMonster("zzharvesta", "*", "Anything");
                Core.BuyItem("zzharvesta", 990201, "Harvest Token");
                Bot.Shops.Load(990202);
                Core.EnsureAccept(2);
                Core.RegisterQuests(187, 328);
                Story.KillQuest(2, "zzdone", "Thing");
                var s = string.Join("and", new[] { "x" });
            }
        }
        """);
        File.WriteAllText(Path.Combine(scripts, "B.cs"), """
        public class B { public void M() { Core.HuntMonster("zzharvesta", "Harvest Bat"); Core.EnsureComplete(187, 5); } }
        """);
        File.WriteAllText(Path.Combine(scripts, "Cached-Scripts", "C.cs"), """
        public class C { public void M() { Core.Join("zzcached"); } }
        """);

        var usage = ScriptUsageScanner.Scan(scripts);
        Assert(usage.FilesScanned == 2, $"Cached-Scripts must be skipped (scanned {usage.FilesScanned})");
        Assert(usage.Maps.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "zzdone", "zzharvesta" }),
            $"Maps must be zzdone and zzharvesta, got [{string.Join(", ", usage.Maps.Keys)}]");
        Assert(usage.Maps["zzharvesta"].Count == 2, "zzharvesta is used by two scripts");
        Assert(usage.Shops.Keys.OrderBy(k => k).SequenceEqual(new[] { 990201, 990202 }) && usage.ShopMaps[990201].ContainsKey("zzharvesta"),
            "Shops 990201 (bought on zzharvesta) and 990202 (Shops.Load) must be found");
        Assert(usage.Quests.Keys.OrderBy(k => k).SequenceEqual(new[] { 2, 187, 328 }), $"Quests must be 2, 187, 328, got [{string.Join(", ", usage.Quests.Keys)}]");
        Assert(usage.Farms.Count == 2 && usage.Farms["zzharvesta|Harvest Slime"].Items.ContainsKey("Slime Gel") &&
               usage.Farms["zzharvesta|Harvest Bat"].Items.ContainsKey("Bat Fang"),
            $"Farmed (map, monster, item) must be read from KillMonster/HuntMonster, '*' left out, got [{string.Join(", ", usage.Farms.Keys)}]");

        var known = new SkuaSim.Collector.Models.GameDatabase();
        known.Maps["zzdone"] = new SkuaSim.Collector.Models.CapturedMap
        {
            Name = "zzdone",
            Monsters = { [1] = new SkuaSim.Collector.Models.CapturedMapMonsterSpawn { MapID = 1, MonsterID = 990150, Name = "Thing", Cell = "r1" } }
        };
        known.Monsters["id:990150"] = new SkuaSim.Collector.Models.CapturedMonster { Key = "id:990150", ID = 990150, Name = "Thing", OfficialDropsObservedUtc = new DateTime(2026, 11, 1) };
        known.Quests[328] = new SkuaSim.Collector.Models.CapturedQuest { ID = 328, Name = "Known" };
        var plan = HarvestPlan.Build(usage, known, new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));
        Assert(plan.Stops.Select(s => s.Key).SequenceEqual(new[] { "map:zzharvesta", "shops:1", "quests:1" }),
            $"Plan stops must be map:zzharvesta, shops:1, quests:1, got [{string.Join(", ", plan.Stops.Select(s => s.Key))}]");
        Assert(plan.Stops[0].Shops.SequenceEqual(new[] { 990201 }) && plan.Stops[0].Reasons.Contains("map not captured"),
            "The shop bought on zzharvesta must ride on its map stop");
        Assert(plan.Stops[1].Shops.SequenceEqual(new[] { 990202 }) && plan.Stops[2].Quests.SequenceEqual(new[] { 2, 187 }),
            "Loose shops and uncaptured quests must get map-less stops");
        Assert(HarvestPlan.FromJson(plan.ToJson()).ToJson() == plan.ToJson(), "The plan must round-trip through JSON");
        var planPath = Path.Combine(dir, "plan", HarvestPlan.DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        File.WriteAllText(planPath, plan.ToJson());

        var gameDataPath = Path.Combine(dir, "gamedata_harvest.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 2,
          "Maps": { "zzharvesta": { "Name": "zzharvesta", "Cells": { "Enter": ["Spawn"], "r1": ["Left"], "r2": ["Left"], "Cutscene": ["Spawn"] },
            "Monsters": {
              "1": { "MapID": 1, "MonsterID": 990101, "Name": "Harvest Slime", "Cell": "r1", "Level": 1, "MaxHP": 100 },
              "2": { "MapID": 2, "MonsterID": 990101, "Name": "Harvest Slime", "Cell": "r1", "Level": 1, "MaxHP": 100 },
              "3": { "MapID": 3, "MonsterID": 990102, "Name": "Harvest Bat", "Cell": "r2", "Level": 1, "MaxHP": 100 } } } },
          "Shops": {
            "990201": { "ID": 990201, "Name": "Harvest Shop", "Items": [ { "ShopItemID": 1, "ID": 990301, "Name": "Harvest Token", "Cost": 10 } ] },
            "990202": { "ID": 990202, "Name": "Loose Shop", "Items": [ { "ShopItemID": 2, "ID": 990302, "Name": "Loose Token", "Cost": 10 } ] }
          }
        }
        """);
        var harvester = RepoFile("tools", "skua", "SkuaSim_Harvester.cs");
        SimulationReport Run() => runner.Run(new SimulationRunOptions
        {
            ScriptPath = harvester,
            GameDataPath = gameDataPath,
            Scenario = new Scenario
            {
                Name = "Harvester",
                Player = new PlayerSetup { Username = "SecretHarvester", Level = 100 },
                Options = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["PlanFile"] = System.Text.Json.JsonSerializer.SerializeToElement(planPath),
                    ["CellDwellMs"] = System.Text.Json.JsonSerializer.SerializeToElement(500),
                    ["RequestGapMs"] = System.Text.Json.JsonSerializer.SerializeToElement(500),
                    ["MaxCellsPerMap"] = System.Text.Json.JsonSerializer.SerializeToElement(40)
                },
                Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
            }
        });

        var first = Run();
        Assert(first.Outcome == SimulationOutcome.Completed, $"The harvester must complete ({first.Outcome}: {first.StopReason} {first.ExceptionText})");
        Assert(first.UnimplementedCalls.Count == 0, $"The harvester must only use simulated API members, touched: {string.Join(", ", first.UnimplementedCalls.Keys)}");
        var progressPath = Path.Combine(Path.GetDirectoryName(planPath)!, "harvest_progress.json");
        var progressText = File.ReadAllText(progressPath);
        Assert(!progressText.Contains("SecretHarvester", StringComparison.OrdinalIgnoreCase), "The progress file must never contain the character name");
        var progress = System.Text.Json.JsonDocument.Parse(progressText).RootElement;
        string Status(string key) => progress.GetProperty(key).GetProperty("status").GetString()!;
        string Note(string key) => progress.GetProperty(key).GetProperty("note").GetString()!;
        Assert(Status("map:zzharvesta") == "done" && Status("shops:1") == "done" && Status("quests:1") == "done",
            $"Every stop must be done, progress: {progressText}");
        Assert(Note("map:zzharvesta").Contains("3/3 cell(s), 2 drop list(s) requested") && Note("map:zzharvesta").Contains("1/1 shop(s) loaded"),
            $"The map stop must visit Enter/r1/r2 (not the cutscene) and ask each monster type once: {Note("map:zzharvesta")}");
        Assert(Note("shops:1").Contains("1/1 shop(s) loaded") && Note("quests:1").Contains("2/2 quest(s) loaded"),
            $"Shops and quests must load: {Note("shops:1")} | {Note("quests:1")}");

        var second = Run();
        Assert(second.Outcome == SimulationOutcome.Completed, $"The second run must complete ({second.Outcome})");
        Assert(second.ScriptLogs.Any(l => l.Text.Contains("This client did 0 stop(s)")), "A finished plan must not be visited again");
        Assert(File.ReadAllText(progressPath) == progressText, "Nothing to do must leave the progress file as it was");
        Assert(plan.Stops.All(s => s.Kind == HarvestStop.VisitKind) && plan.Totals.FarmsWaitingForVisit == 2,
            "Farmed monsters on an uncaptured map must wait for a visit, not be sampled");

        // Layer B: once the map and the official drop lists are captured, normal drops are sampled; quest items are not.
        var visited = new SkuaSim.Collector.Models.GameDatabase();
        visited.Maps["zzharvesta"] = new SkuaSim.Collector.Models.CapturedMap
        {
            Name = "zzharvesta",
            Monsters =
            {
                [1] = new SkuaSim.Collector.Models.CapturedMapMonsterSpawn { MapID = 1, MonsterID = 990101, Name = "Harvest Slime", Cell = "r1", Level = 1 },
                [3] = new SkuaSim.Collector.Models.CapturedMapMonsterSpawn { MapID = 3, MonsterID = 990102, Name = "Harvest Bat", Cell = "r2", Level = 1 }
            }
        };
        var listed = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        var slime = new SkuaSim.Collector.Models.CapturedMonster { Key = "id:990101", ID = 990101, Name = "Harvest Slime", OfficialDropsObservedUtc = listed, CreditedKills = 40 };
        slime.Drops[990401] = new SkuaSim.Collector.Models.CapturedDropStat { ItemID = 990401, ItemName = "Slime Gel", IsOfficialDrop = true, CreditedEligibleKills = 40, CreditedDropEvents = 2 };
        var bat = new SkuaSim.Collector.Models.CapturedMonster { Key = "id:990102", ID = 990102, Name = "Harvest Bat", OfficialDropsObservedUtc = listed };
        bat.Drops[990402] = new SkuaSim.Collector.Models.CapturedDropStat { ItemID = 990402, ItemName = "Bat Fang", IsOfficialDrop = true, QuestGated = true };
        visited.Monsters["id:990101"] = slime;
        visited.Monsters["id:990102"] = bat;
        var samplePlan = HarvestPlan.Build(usage, visited, listed);
        var samples = samplePlan.Stops.Where(s => s.Kind == HarvestStop.SampleKind).ToList();
        Assert(samples.Count == 1 && samples[0].Key == "sample:zzharvesta:990101:40" && samples[0].Items.SequenceEqual(new[] { "Slime Gel" }) &&
               samples[0].Kills == 260 && samples[0].Cell == "r1" && samplePlan.Totals.FarmedItemsQuestGated == 1,
            $"Only Slime Gel must be sampled (260 kills to 300, measured count in the key), the quest-gated Bat Fang left out; got " +
            $"[{string.Join(", ", samples.Select(s => $"{s.Key} {string.Join("/", s.Items)} {s.Kills}"))}], quest-gated {samplePlan.Totals.FarmedItemsQuestGated}");
        var samplePlanPath = Path.Combine(dir, "sampleplan", HarvestPlan.DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(samplePlanPath)!);
        File.WriteAllText(samplePlanPath, samplePlan.ToJson());

        var sampled = runner.Run(new SimulationRunOptions
        {
            ScriptPath = harvester,
            GameDataPath = gameDataPath,
            Scenario = new Scenario
            {
                Name = "Harvester sampling",
                Player = new PlayerSetup { Username = "SecretHarvester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 },
                Options = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["SkipOption"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                    ["PlanFile"] = System.Text.Json.JsonSerializer.SerializeToElement(samplePlanPath),
                    ["SampleDrops"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                    ["KillsPerStop"] = System.Text.Json.JsonSerializer.SerializeToElement(5)
                },
                Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
            }
        });
        Assert(sampled.Outcome == SimulationOutcome.Completed, $"Sampling must complete ({sampled.Outcome}: {sampled.StopReason} {sampled.ExceptionText})");
        Assert(sampled.UnimplementedCalls.Count == 0, $"Sampling must only use simulated API members, touched: {string.Join(", ", sampled.UnimplementedCalls.Keys)}");
        var sampleProgressText = File.ReadAllText(Path.Combine(Path.GetDirectoryName(samplePlanPath)!, "harvest_progress.json"));
        var sampleProgress = System.Text.Json.JsonDocument.Parse(sampleProgressText).RootElement;
        var sampleEntry = sampleProgress.GetProperty("sample:zzharvesta:990101:40");
        Assert(sampleEntry.GetProperty("status").GetString() == "done" && sampleEntry.GetProperty("note").GetString()!.Contains("5/5 kill(s) of Harvest Slime"),
            $"The sample stop must kill 5 Harvest Slimes (KillsPerStop): {sampleProgressText}");
        Assert(!sampleProgressText.Contains("\"map:", StringComparison.Ordinal), "Sampling must leave the visit stops alone");
        Assert(sampled.Kills == 5, $"Exactly 5 kills expected, got {sampled.Kills}");
        Assert(sampled.ScriptLogs.Any(l => l.Text.Contains("Skills: ") && l.Text.Contains("CoreBots Solo class")),
            "Sampling must fight with the CBO Solo class through CoreBots.EquipClass (AdvancedSkills.json), never a made-up rotation");

        // --all revisits what is captured; blocked joins are classified by the stored server message; reset / retry.
        visited.Shops[990201] = new SkuaSim.Collector.Models.CapturedShop { ID = 990201, Name = "Harvest Shop" };
        visited.Shops[990202] = new SkuaSim.Collector.Models.CapturedShop { ID = 990202, Name = "Loose Shop" };
        Assert(!HarvestPlan.Build(usage, visited, listed).Stops.Any(s => s.Key == "map:zzharvesta"), "A fully captured map is not visited without --all");
        var revisit = HarvestPlan.Build(usage, visited, listed, revisitAll: true);
        Assert(revisit.Stops.Any(s => s.Key == "map:zzharvesta" && s.Reasons.SequenceEqual(new[] { "1 shop(s) to load" })) &&
               revisit.Stops.Any(s => s.Key == "sample:zzharvesta:990101:40" && s.Kills == (int)HarvestPlan.SampleKillGoal) &&
               revisit.Stops.Where(s => s.Key.StartsWith("quests:")).Sum(s => s.Quests.Count) == 3,
            "--all must revisit captured maps, sample measured monsters again and load every used quest");

        var blockUsage = new ScriptUsage();
        ScriptUsageScanner.ScanSource("Story/Locked1.cs", "class X { void M() { Core.Join(\"zzlocked\"); Core.Join(\"zzlocked2\"); } }", blockUsage);
        ScriptUsageScanner.ScanSource("Story/Locked2.cs", "class X { void M() { Core.Join(\"zzlocked2\"); } }", blockUsage);
        ScriptUsageScanner.ScanSource("Farm/Member.cs", "class X { void M() { Core.Join(\"zzmember\"); } }", blockUsage);
        var blockDb = new SkuaSim.Collector.Models.GameDatabase();
        blockDb.ServerMessages["m1"] = new SkuaSim.Collector.Models.CapturedServerMessage
        {
            Kind = "warning", Text = "\"<Q>\" is an Membership-Only Map.", TargetMaps = { ["zzmember"] = 2 }
        };
        var progressDir = Path.Combine(dir, "progress");
        Directory.CreateDirectory(progressDir);
        var blockProgressPath = Path.Combine(progressDir, HarvestProgress.FileName);
        File.WriteAllText(blockProgressPath, """
        {
          "map:zzlocked": { "status": "failed", "note": "could not join zzlocked (member-only, locked, seasonal or story-gated?)" },
          "map:zzlocked2": { "status": "failed", "note": "could not join zzlocked2 (member-only, locked, seasonal or story-gated?)" },
          "map:zzmember": { "status": "failed", "note": "could not join zzmember (member-only, locked, seasonal or story-gated?)" },
          "sample:zzboss:1:0": { "status": "failed", "note": "too strong: died 5 times (level 30)" },
          "map:zzdone": { "status": "done", "note": "ok" },
          "map:zzbusy": { "status": "claimed", "by": "client-1", "at": "2026-11-25T12:00:00.0000000Z" }
        }
        """);
        var blockProgress = HarvestProgress.Load(blockProgressPath);
        var blockers = HarvestBlockers.Analyze(blockProgress, blockUsage, blockDb);
        Assert(blockers.Maps.Single(m => m.Map == "zzmember").Reason == HarvestBlockers.MemberOnly &&
               blockers.Maps.Single(m => m.Map == "zzlocked").Reason == HarvestBlockers.Locked && blockers.TooStrong.Count == 1,
            $"Joins must be classified (member-only from the server message, locked without one), got " +
            $"[{string.Join(", ", blockers.Maps.Select(m => $"{m.Map}={m.Reason}"))}], too strong {blockers.TooStrong.Count}");
        Assert(blockers.StoryOrder.Count == 1 && blockers.StoryOrder[0].Script == "Story/Locked1.cs" &&
               blockers.StoryOrder[0].Maps.SequenceEqual(new[] { "zzlocked", "zzlocked2" }),
            $"One story script opens both locked maps, got [{string.Join("; ", blockers.StoryOrder.Select(s => $"{s.Script}: {string.Join(",", s.Maps)}"))}]");
        Assert(blockers.ToMarkdown(listed).Contains("1. `Story/Locked1.cs` — opens zzlocked, zzlocked2"), "The report must list the story scripts in order");
        var claimTime = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert(HarvestProgress.ActiveClaims(blockProgress, claimTime.AddMinutes(5)).SequenceEqual(new[] { "map:zzbusy" }) &&
               HarvestProgress.ActiveClaims(blockProgress, claimTime.AddMinutes(30)).Count == 0,
            "A claim is active for 20 minutes (reset/retry refuse while clients run)");
        Assert(HarvestProgress.RemoveFailed(blockProgressPath, claimTime) == 4 &&
               HarvestProgress.Load(blockProgressPath).Select(p => p.Key).OrderBy(k => k).SequenceEqual(new[] { "map:zzbusy", "map:zzdone" }) &&
               File.Exists(Path.Combine(progressDir, "harvest_progress.20261125-120000.json")),
            "--retry-failed must clear the 4 failed stops and keep a dated copy");
        var archived = HarvestProgress.Archive(blockProgressPath, claimTime.AddHours(1));
        Assert(archived != null && !File.Exists(blockProgressPath) && File.Exists(archived) && HarvestProgress.Load(blockProgressPath).Count == 0,
            "--reset must move the progress aside so the next run starts from zero");
        Console.WriteLine($"   OK: {plan.Stops.Count} stops planned from 2 scripts; harvester visited, requested drop lists once per monster type and resumed; " +
                          "sampling plan kept only the normal drop and the sampler killed 5 in a private room");
    }

    private static void TestFarmTargetsWithCarriedOverQuests(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("27. Testing farm targets: carried-over quests do not hide hunted items or steal map items...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_farmtargets.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 2,
          "Maps": { "zzsealmap": { "Name": "zzsealmap", "Cells": { "Enter": ["Spawn"], "r1": ["Left"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990011, "Name": "Seal Beast", "Cell": "r1", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990011": { "Key": "id:990011", "ID": 990011, "Name": "Seal Beast", "Level": 5, "MaxHP": 300 } },
          "Quests": {
            "990501": { "ID": 990501, "Name": "Old Errand", "Requirements": [ { "ItemID": 990601, "ItemName": "Distant Relic", "Quantity": 1, "Temp": false } ] },
            "990502": { "ID": 990502, "Name": "Strike the Rock", "Requirements": [ { "ItemID": 990602, "ItemName": "Rock Struck", "Quantity": 1, "Temp": false } ] }
          }
        }
        """);

        var script = Path.Combine(tempDir, "FarmTargetBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class FarmTargetBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzsealmap");
                bot.Drops.Add("Wanted Seal");
                bot.Hunt.ForItem("Seal Beast", "Wanted Seal", 1);
                bot.Quests.EnsureAccept(990502);
                bot.Map.GetMapItem(777);
                bot.Log(bot.Inventory.Contains("Rock Struck") ? "ROCK=yes" : "ROCK=no");
            }
        }
        """);

        var scenario = new Scenario
        {
            Name = "Carried-over quest",
            Player = new PlayerSetup { Username = "Tester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 },
            Quests = new QuestSetup { Active = new List<int> { 990501 } },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };
        var report = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = scenario, GameDataPath = gameDataPath });

        Assert(report.Outcome == SimulationOutcome.Completed,
            $"The hunt for 'Wanted Seal' must finish although quest 990501 needs another item ({report.Outcome}: {report.StopReason})");
        Assert(report.DropsByItem.GetValueOrDefault("Wanted Seal") >= 1, "'Wanted Seal' must drop (synthesized and flagged)");
        Assert(report.SynthesizedData.Any(s => s.Key.StartsWith("drop:seal beast:") && s.Description.Contains("'Wanted Seal'")),
            "The invented 'Wanted Seal' drop must be flagged");
        var mapItem = report.SynthesizedData.FirstOrDefault(s => s.Key == "mapitem:zzsealmap:777");
        Assert(mapItem != null && mapItem.Description.Contains("'Rock Struck'"),
            $"Map item 777 must be assumed to give the last accepted quest's 'Rock Struck' (got: {mapItem?.Description ?? "no record"})");
        Assert(report.ScriptLogs.Any(l => l.Text.Contains("ROCK=yes")), "The script must own 'Rock Struck' after GetMapItem");
        Console.WriteLine("   OK: Wanted Seal dropped despite the carried-over quest; map item gave Rock Struck");
    }

    /// <summary>
    /// A quest-gated drop known only by quest names (monsterDrops "questGated" with an empty "sReqQuests") is resolved at
    /// load time to every quest with that name, so its rate uses the quest-active kills and it only drops while one of
    /// those quests is active. Duplicate names keep every ID; unknown names are listed, never guessed.
    /// </summary>
    private static string RepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SkuaSim.slnx")))
                return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        throw new FileNotFoundException("SkuaSim.slnx not found above the test binaries.");
    }

    /// <summary>
    /// The Skua diagnostic generator (tools/skua/SkuaSim_Diagnostic.cs), run inside the simulator: the scenario it writes
    /// reproduces the character (stats, items, story slots, dailies via world.getAchievement, reputation, boosts, Forge
    /// unlocks, client options, the script's saved options), never contains the character name or name-like options,
    /// and running it again on its own output gives the same character. Then the bug report is filled from a run.
    /// </summary>
    private static void TestDiagnosticGeneratorAndBugReport(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("26. Testing the diagnostic generator round trip and the bug report...");
        var diagScript = RepoFile("tools", "skua", "SkuaSim_Diagnostic.cs");
        var dir = Path.Combine(tempDir, "diagnostic");
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "Reported.cfg");
        File.WriteAllLines(cfg, new[]
        {
            "Options:player1=SecretAlt", "Options:Leader=SecretLeader", "Options:Mode=SecretName", "Options:RoomNumber=1234",
            "Setup:PrivateRoomNumber=5", "Options:Select Dailies Set=All"
        });

        Dictionary<string, System.Text.Json.JsonElement> DiagOptions(string outDir) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["OutputFolder"] = System.Text.Json.JsonSerializer.SerializeToElement(outDir),
            ["ScriptOptionsFile"] = System.Text.Json.JsonSerializer.SerializeToElement(cfg),
            ["ReportedScript"] = System.Text.Json.JsonSerializer.SerializeToElement("Story/Example.cs")
        };
        var source = new Scenario
        {
            Name = "Diagnostic source",
            StartDate = new DateTime(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc),
            Player = new PlayerSetup
            {
                Username = "SecretName", Level = 77, Xp = 1234, Gold = 4321, Coins = 55, IsMember = false, Class = "Rogue",
                ClassRank = 6, ClassPoints = 70_000, InventorySlots = 120, CompleteEquipment = false
            },
            Location = new LocationSetup { Map = "greenguardwest", Cell = "West3", Pad = "Left" },
            Inventory = new()
            {
                new() { Name = "Rogue", Category = "Class", Equipped = true, EnhancementPatternID = 1, EnhancementLevel = 70 },
                new() { Name = "Zz Diag Blade", Category = "Sword", Equipped = true, EnhancementPatternID = 10, EnhancementLevel = 70, ProcID = 1, Meta = "gold:1.30" },
                new() { Name = "Zz Diag Helm", Category = "Helm", Equipped = true, EnhancementPatternID = 1, EnhancementLevel = 70 },
                new() { Name = "Zz Diag Cape", Category = "Cape", Equipped = true, EnhancementPatternID = 1, EnhancementLevel = 70 },
                new() { Name = "Dragon Scale", Quantity = 7, MaxStack = 10 }
            },
            Bank = new() { new() { Name = "Ancient Relic", Quantity = 3 } },
            Quests = new QuestSetup { Active = new() { 2 }, DailiesCompleted = new() { 187, 328 }, SlotProgress = new() { ["32"] = 5 } },
            Factions = new() { new() { Name = "Good", Id = 2, Rank = 5, TotalRep = 25_000 } },
            Boosts = new BoostsSetup { GoldBoost = true },
            Enhancements = new EnhancementsSetup { UnlockedForge = new() { "Forge (Weapon)" } },
            Options = DiagOptions(Path.Combine(dir, "out1")),
            BotOptions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["PrivateRooms"] = System.Text.Json.JsonSerializer.SerializeToElement(false),
                ["ActionDelay"] = System.Text.Json.JsonSerializer.SerializeToElement(900)
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };

        Scenario RunDiagnostic(Scenario input, string outDir)
        {
            var report = runner.Run(new SimulationRunOptions { ScriptPath = diagScript, Scenario = input });
            Assert(report.Outcome == SimulationOutcome.Completed, $"The diagnostic must complete ({report.Outcome}: {report.StopReason} {report.ExceptionText})");
            Assert(report.UnimplementedCalls.Count == 0, $"The diagnostic must only use simulated API members, touched: {string.Join(", ", report.UnimplementedCalls.Keys)}");
            var files = Directory.GetFiles(outDir, "diagnostic_*.json");
            Assert(files.Length == 1 && Path.GetFileName(files[0]) == "diagnostic_20261120-190000_Example.json",
                $"Expected one diagnostic named after the virtual capture time and the script, got [{string.Join(", ", files.Select(Path.GetFileName))}]");
            var text = File.ReadAllText(files[0]);
            foreach (var secret in new[] { "SecretName", "SecretAlt", "SecretLeader" })
                Assert(!text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"The diagnostic must never contain '{secret}'");
            var loaded = Scenario.Load(files[0]);
            loaded.SourcePath = files[0];
            return loaded;
        }

        var first = RunDiagnostic(source, Path.Combine(dir, "out1"));
        var p = first.Player;
        Assert(p.Username == "Reporter" && p.Level == 77 && p.Xp == 1234 && p.Gold == 4321 && p.Coins == 55 && !p.IsMember &&
               p.Class == "Rogue" && p.ClassRank == 6 && p.ClassPoints == 70_000 && p.InventorySlots == 120 && !p.CompleteEquipment,
            $"Player fields must be captured (got {System.Text.Json.JsonSerializer.Serialize(p)})");
        Assert(first.Location.Map == "greenguardwest" && first.Location.Cell == "West3" && first.Location.Pad == "Left", "Location must be captured");
        Assert(first.StartDate == source.StartDate, $"startDate must be the capture time, got {first.StartDate:o}");
        Assert(first.Inventory.Any(i => i.Name == "Rogue" && i.Equipped && i.EnhancementLevel == 70 && i.EnhancementPatternID == 1) &&
               first.Inventory.Any(i => i.Name == "Zz Diag Blade" && i.Equipped && i.EnhancementPatternID == 10 && i.ProcID == 1 && i.Meta == "gold:1.30") &&
               first.Inventory.Any(i => i.Name == "Dragon Scale" && i.Quantity == 7) && first.Inventory.Count == 5,
            $"Inventory must be captured as it is, got [{string.Join(", ", first.Inventory.Select(i => i.Name))}]");
        Assert(first.Bank.Count == 1 && first.Bank[0].Name == "Ancient Relic" && first.Bank[0].Quantity == 3, "Bank must be captured");
        Assert(first.Quests.Active.SequenceEqual(new[] { 2 }), "Active quests must be captured");
        // AQW keeps one flag per (field, index): every quest sharing the flag of 187 (id0/9) or 328 (id0/0) reads as done.
        var questFlags = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SkuaInstallation.Detect().QuestDataFile)).RootElement
            .EnumerateArray()
            .Where(q => q.TryGetProperty("Field", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String && f.GetString()!.Length > 0)
            .ToDictionary(q => q.GetProperty("ID").GetInt32(), q => (q.GetProperty("Field").GetString(), q.GetProperty("Index").GetInt32()));
        var expectedDailies = questFlags.Where(kv => kv.Value == questFlags[187] || kv.Value == questFlags[328]).Select(kv => kv.Key).Order().ToList();
        Assert(first.Quests.DailiesCompleted.SequenceEqual(expectedDailies) && expectedDailies.Contains(187) && expectedDailies.Contains(328),
            $"Dailies done must come from world.getAchievement: expected [{string.Join(",", expectedDailies)}], got [{string.Join(",", first.Quests.DailiesCompleted)}]");
        Assert(first.Quests.SlotProgress.GetValueOrDefault("32") == 5 && first.Quests.SlotProgress.GetValueOrDefault("21") == 27,
            $"Story slots must be captured (32 = 5, Forge unlock slot 21 = 27), got {System.Text.Json.JsonSerializer.Serialize(first.Quests.SlotProgress)}");
        Assert(first.Enhancements.UnlockedForge.SequenceEqual(new[] { "Forge (Weapon)" }) && first.Enhancements.UnlockedAwe.Count == 0,
            $"Forge unlocks must be derived from the slots, got [{string.Join(", ", first.Enhancements.UnlockedForge)}]");
        Assert(first.Factions.Count == 1 && first.Factions[0].Name == "Good" && first.Factions[0].TotalRep == 25_000, "Reputation must be captured");
        Assert(first.Boosts.GoldBoost && !first.Boosts.XpBoost, "Running boosts must be captured (Boosts.IsBoostActive)");
        Assert(first.BotOptions.TryGetValue("PrivateRooms", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.False &&
               first.BotOptions.TryGetValue("ActionDelay", out var ad) && ad.GetInt32() == 900 && !first.BotOptions.ContainsKey("CustomName"),
            "Client options must be captured (never CustomName)");
        Assert(first.Options.Count == 3 && first.Options["RoomNumber"].GetString() == "1234" &&
               first.Options["Setup:PrivateRoomNumber"].GetString() == "5" && first.Options["Select Dailies Set"].GetString() == "All",
            $"Script options must be copied without name-like keys or the character name, got {System.Text.Json.JsonSerializer.Serialize(first.Options)}");
        Assert(first.Script == "Story/Example.cs", "The reported script must be named in the scenario");

        // Round trip: the diagnostic of the diagnostic's character is the same character.
        var again = Scenario.Load(first.SourcePath!);
        again.Options = DiagOptions(Path.Combine(dir, "out2"));
        again.Player.Username = "SecretName"; // the same real character (its .cfg holds its own name)
        var second = RunDiagnostic(again, Path.Combine(dir, "out2"));
        string State(Scenario s) => System.Text.Json.JsonSerializer.Serialize(new
        {
            s.Player, s.Location, s.Inventory, s.Bank, s.House, s.TempInventory, s.Quests, s.Factions, s.Boosts, s.Enhancements, s.BotOptions, s.Options
        }, Scenario.JsonOptions);
        Assert(State(first) == State(second), "Running the diagnostic on its own output must give the same character");

        // Bug report from a run of a (buggy) script on the diagnostic's character.
        var buggy = Path.Combine(dir, "BuggyScript.cs");
        File.WriteAllText(buggy, """
        using Skua.Core.Interfaces;

        public class BuggyScript
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log("Checking the relic");
                if (bot.Bank.Contains("Ancient Relic", 3))
                    throw new System.InvalidOperationException("relic logic is wrong");
            }
        }
        """);
        var bugRun = runner.Run(new SimulationRunOptions { ScriptPath = buggy, Scenario = first });
        Assert(bugRun.Outcome == SimulationOutcome.ScriptException, $"The buggy script must throw on this character, got {bugRun.Outcome}");
        var md = BugReportWriter.ToMarkdown(first, bugRun, first.SourcePath);
        foreach (var expected in new[] { "# Bug report — `Story/Example.cs`", "## What went wrong", "## Character (from the diagnostic)",
                     "| Level / class | 77 / Rogue (rank 6) |", "| Client options changed from Skua's defaults | PrivateRooms=False, ActionDelay=900 |", "**ScriptException**", "relic logic is wrong", "Checking the relic",
                     "## How much to trust this reproduction", "## Regression test (after the fix)", "skuasim run --scenario" })
            Assert(md.Contains(expected, StringComparison.Ordinal), $"The bug report must contain '{expected}'");
        Assert(!md.Contains("SecretName", StringComparison.OrdinalIgnoreCase), "The bug report must not contain the character name");
        Assert(!bugRun.SynthesizedData.Any(s => s.Key.StartsWith("item:zz diag", StringComparison.Ordinal)),
            "Items given in full by a diagnostic (ID, category, max stack) are real data, never reported as invented");

        // AC items take no bank space (CoreBots banks them with no free slot; seen on a real diagnostic: 187 non-AC items
        // in 210 slots next to 4,074 AC items).
        var bankScript = Path.Combine(dir, "BankSlotsBot.cs");
        File.WriteAllText(bankScript, """
        using Skua.Core.Interfaces;

        public class BankSlotsBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log($"used={bot.Bank.UsedSlots} free={bot.Bank.FreeSlots}");
                bot.Log($"ac={bot.Inventory.ToBank("Zz AC Trinket")} plain={bot.Inventory.ToBank("Zz Plain Rock")}");
            }
        }
        """);
        var bankRun = runner.Run(new SimulationRunOptions
        {
            ScriptPath = bankScript,
            Scenario = new Scenario
            {
                Player = new PlayerSetup { Username = "Tester", BankSlots = 1, CompleteEquipment = false },
                Inventory = new()
                {
                    new() { Name = "Zz AC Trinket", Id = 990401, Category = "Item", MaxStack = 1, Coins = true },
                    new() { Name = "Zz Plain Rock", Id = 990402, Category = "Item", MaxStack = 10 }
                },
                Bank = new()
                {
                    new() { Name = "Zz Banked Rock", Id = 990403, Category = "Item", MaxStack = 10 },
                    new() { Name = "Zz Banked AC", Id = 990404, Category = "Item", MaxStack = 1, Coins = true }
                }
            }
        });
        var bankLogs = string.Join(" | ", bankRun.ScriptLogs.Select(l => l.Text));
        Assert(bankLogs.Contains("used=1 free=0") && bankLogs.Contains("ac=True plain=False"),
            $"AC items must not use bank slots: {bankLogs}");
        Console.WriteLine($"   OK: diagnostic round trip ({first.Inventory.Count} inv / {first.Bank.Count} bank, {first.Quests.SlotProgress.Count} slots, " +
                          $"{first.Quests.DailiesCompleted.Count} dailies, {first.Options.Count} options, 3 private values left out), bug report filled");
    }

    private static void TestQuestGateNamesResolved(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("25. Testing quest-gated drop names resolved to quest IDs...");
        var dir = Path.Combine(tempDir, "gate_names");
        Directory.CreateDirectory(dir);
        var gameDataPath = Path.Combine(dir, "gamedata.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 4,
          "Maps": { "zzgatenames": { "Name": "zzgatenames", "Cells": { "Enter": ["Spawn"], "r1": ["Left"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990301, "Name": "Name Beast", "Cell": "r1", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990301": { "Key": "id:990301", "ID": 990301, "Name": "Name Beast", "Level": 5, "MaxHP": 300, "CreditedKills": 100,
            "CreditedKillsByActiveQuestSet": { "": 50, "990311": 30, "990312": 20 },
            "Drops": { "990321": { "ItemID": 990321, "ItemName": "Named Gate Horn", "IsOfficialDrop": true, "QuestGated": true,
                                   "QuestGatedNames": ["Zz Twin Quest", "Zz Lost Quest"],
                                   "CreditedDropEvents": 50, "CreditedTrackedDropEvents": 50 } } } }
        }
        """);
        var questDataPath = Path.Combine(dir, "QuestData.json");
        File.WriteAllText(questDataPath, """
        [ { "ID": 990311, "Name": "Zz Twin Quest", "Requirements": [], "Rewards": [], "AcceptRequirements": [] },
          { "ID": 990312, "Name": "Zz Twin Quest", "Requirements": [], "Rewards": [], "AcceptRequirements": [] },
          { "ID": 990313, "Name": "Zz Other Quest", "Requirements": [], "Rewards": [], "AcceptRequirements": [] } ]
        """);

        var catalog = GameDataCatalog.Load(gameDataPath, questDataPath);
        var drop = catalog.Captured.Monsters["id:990301"].Drops[990321];
        Assert(drop.RequiredQuestIds.SetEquals(new[] { 990311, 990312 }),
            $"Both quests named 'Zz Twin Quest' must be kept, got [{string.Join(",", drop.RequiredQuestIds.Order())}]");
        Assert(drop.CreditedEligibleKills == 50 && drop.CreditedRateBasis == "quest_active_kills" && drop.EstimatedDropChance == 1.0,
            $"The rate must use the 50 quest-active kills (got {drop.CreditedEligibleKills}, {drop.CreditedRateBasis}, {drop.EstimatedDropChance})");
        Assert(catalog.QuestGateNames.ResolvedDrops == 1 && catalog.QuestGateNames.UnresolvedNames.SequenceEqual(new[] { "Zz Lost Quest" }),
            $"Expected 1 resolved drop and 'Zz Lost Quest' unresolved, got {catalog.QuestGateNames}");
        var onDisk = SkuaSim.Collector.Storage.GameDatabaseStore.ReadSharedFile(gameDataPath);
        Assert(onDisk.Monsters["id:990301"].Drops[990321].RequiredQuestIds.Count == 0, "gamedata.json itself must never change");

        var script = Path.Combine(tempDir, "GateNamesBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class GateNamesBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzgatenames");
                for (var i = 0; i < 4; i++)
                {
                    bot.Hunt.Monster("Name Beast");
                    bot.Sleep(13_000);
                }
            }
        }
        """);
        Scenario MakeScenario(List<int> active) => new()
        {
            Name = "Gate names",
            Player = new PlayerSetup { Username = "Tester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 },
            Quests = new QuestSetup { Active = active },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };
        var inactive = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(new() { 990313 }), GameDataPath = gameDataPath, QuestDataPath = questDataPath });
        var active = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(new() { 990312 }), GameDataPath = gameDataPath, QuestDataPath = questDataPath });
        Assert(inactive.Outcome == SimulationOutcome.Completed && active.Outcome == SimulationOutcome.Completed,
            $"Both runs must complete ({inactive.Outcome}: {inactive.StopReason}; {active.Outcome}: {active.StopReason})");
        Assert(inactive.DropsByItem.GetValueOrDefault("Named Gate Horn") == 0,
            $"Without one of its quests active the drop must not drop (got {inactive.DropsByItem.GetValueOrDefault("Named Gate Horn")})");
        Assert(active.DropsByItem.GetValueOrDefault("Named Gate Horn") >= 4,
            $"With the second 'Zz Twin Quest' active the measured 100 % rate must apply (got {active.DropsByItem.GetValueOrDefault("Named Gate Horn")})");
        Console.WriteLine($"   OK: names -> [990311, 990312], unresolved 'Zz Lost Quest', inactive=0 / active={active.DropsByItem["Named Gate Horn"]}");
    }

    /// <summary>
    /// Frozen data snapshots: "skuasim snapshot create" copies what a run reads; a run with the snapshot gives the same
    /// result as the live data it was made from and keeps giving it after the live file changes; snapshots are never
    /// overwritten, a changed file is reported, and a missing snapshot fails the run instead of using live data.
    /// </summary>
    private static void TestFrozenDataSnapshots(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("23. Testing frozen data snapshots (create, run, live drift, tamper warning, missing snapshot)...");
        var liveDir = Path.Combine(tempDir, "snapshot_live");
        Directory.CreateDirectory(liveDir);
        var gameDataPath = Path.Combine(liveDir, "gamedata.json");
        string GameData(int pelts) => $$"""
        {
          "SchemaVersion": 4,
          "Maps": { "zzsnapmap": { "Name": "zzsnapmap", "Cells": { "Enter": ["Spawn"], "r1": ["Left"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990201, "Name": "Snap Beast", "Cell": "r1", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990201": { "Key": "id:990201", "ID": 990201, "Name": "Snap Beast", "Level": 5, "MaxHP": 300, "CreditedKills": 100,
            "Drops": { "990202": { "ItemID": 990202, "ItemName": "Snap Pelt", "CreditedDropEvents": {{pelts}} } } } }
        }
        """;
        File.WriteAllText(gameDataPath, GameData(50));

        var script = Path.Combine(tempDir, "SnapshotBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class SnapshotBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzsnapmap");
                for (var i = 0; i < 20; i++)
                {
                    bot.Hunt.Monster("Snap Beast");
                    bot.Sleep(13_000);
                }
            }
        }
        """);
        var scenario = new Scenario
        {
            Name = "Snapshot",
            Player = new PlayerSetup { Username = "Tester", Level = 100, MaxHealth = 10_000, DamagePerSecond = 8_000 },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };
        var noQuests = Path.Combine(liveDir, "no_QuestData.json");
        SimulationReport Run(string? snapshot) => runner.Run(new SimulationRunOptions
        {
            ScriptPath = script, Scenario = scenario, SeedOverride = 5, GameDataPath = gameDataPath, QuestDataPath = noQuests, DataSnapshot = snapshot
        });

        var root = DataSnapshot.FolderFor(gameDataPath);
        var snap = DataSnapshot.Create("t1", gameDataPath, null, null, root, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), "test");
        Assert(snap.Directory == Path.Combine(liveDir, "snapshots", "t1") && File.Exists(snap.GameDataPath) &&
               File.Exists(Path.Combine(snap.Directory, DataSnapshot.ManifestFileName)), "The snapshot folder must hold gamedata.json and snapshot.json");
        Assert(snap.Manifest.Monsters == 1 && snap.Manifest.CreditedKills == 100 && snap.Manifest.Files.Count == 1 && snap.Verify().Count == 0,
            $"Manifest must describe the frozen data (monsters={snap.Manifest.Monsters}, files={snap.Manifest.Files.Count}, problems={snap.Verify().Count})");
        Assert(DataSnapshot.ResolveDirectory("t1", root) == snap.Directory && DataSnapshot.ResolveDirectory("nope", root) == null,
            "A snapshot must resolve by name under the snapshots folder");

        var live = Run(null);
        var frozen = Run(snap.Directory);
        Assert(live.Outcome == SimulationOutcome.Completed && frozen.Outcome == SimulationOutcome.Completed,
            $"Both runs must complete ({live.Outcome}: {live.StopReason}; {frozen.Outcome}: {frozen.StopReason})");
        var livePelts = live.DropsByItem.GetValueOrDefault("Snap Pelt");
        Assert(livePelts > 0 && frozen.DropsByItem.GetValueOrDefault("Snap Pelt") == livePelts && frozen.Kills == live.Kills,
            $"The snapshot must give the same run as the live data it froze (live {live.Kills} kills/{livePelts} pelts, snapshot {frozen.Kills}/{frozen.DropsByItem.GetValueOrDefault("Snap Pelt")})");
        Assert(frozen.DataInfo?.StartsWith("snapshot t1 (") == true && live.DataInfo?.StartsWith("live ") == true &&
               frozen.ToMarkdown().Contains("**Game Data:** `snapshot t1"), $"The report must name the data used (got '{frozen.DataInfo}' / '{live.DataInfo}')");

        // The collector keeps writing: the live rate changes, the snapshot run does not.
        File.WriteAllText(gameDataPath, GameData(100));
        var liveAfter = Run(null);
        var frozenAfter = Run(snap.Directory);
        Assert(liveAfter.DropsByItem.GetValueOrDefault("Snap Pelt") > livePelts,
            $"The changed live data (100 % rate) must change the live run (got {liveAfter.DropsByItem.GetValueOrDefault("Snap Pelt")} vs {livePelts})");
        Assert(frozenAfter.DropsByItem.GetValueOrDefault("Snap Pelt") == livePelts && frozenAfter.Warnings.Count == frozen.Warnings.Count,
            "The snapshot run must not change when the live data changes");

        // The scenario can name the snapshot too.
        var viaScenario = runner.Run(new SimulationRunOptions
        {
            ScriptPath = script, SeedOverride = 5, GameDataPath = gameDataPath, QuestDataPath = noQuests,
            Scenario = new Scenario { Name = scenario.Name, Player = scenario.Player, Limits = scenario.Limits, DataSnapshot = snap.Directory }
        });
        Assert(viaScenario.DropsByItem.GetValueOrDefault("Snap Pelt") == livePelts && viaScenario.DataInfo?.StartsWith("snapshot t1") == true,
            "\"dataSnapshot\" in the scenario must select the snapshot");

        var overwriteRefused = false;
        try { DataSnapshot.Create("t1", gameDataPath, null, null, root, DateTime.UtcNow); }
        catch (IOException) { overwriteRefused = true; }
        Assert(overwriteRefused, "An existing snapshot must never be overwritten");
        Assert(!DataSnapshot.IsValidName("..\\x") && !DataSnapshot.IsValidName("a/b") && DataSnapshot.IsValidName("2026-11-24"),
            "Snapshot names must not be paths");

        File.AppendAllText(snap.GameDataPath, " ");
        var tampered = Run(snap.Directory);
        Assert(tampered.Warnings.Any(w => w.Text.Contains("gamedata.json was changed after the snapshot was made")),
            "A snapshot file changed after creation must be reported");

        var missing = Run(Path.Combine(liveDir, "snapshots", "does-not-exist"));
        Assert(missing.Outcome == SimulationOutcome.ScriptException && missing.StopReason?.Contains("not found") == true && missing.Kills == 0,
            $"A missing snapshot must fail the run, never fall back to live data (got {missing.Outcome}: {missing.StopReason})");
        Console.WriteLine($"   OK: live = snapshot ({live.Kills} kills, {livePelts} pelts); live drift {liveAfter.DropsByItem.GetValueOrDefault("Snap Pelt")} pelts, snapshot unchanged; tamper warned; missing fails");
    }

    /// <summary>
    /// Script background work (Task.Run + await Task.Delay with a token, new Random(), Task.WaitAll — the CoreBots
    /// registered-quest pattern) runs one thread at a time in virtual-time order, so the same seed repeats the run
    /// exactly, including the interleaving of main and background log lines.
    /// </summary>
    /// <summary>
    /// PvP NPC match (Bludrut Brawl): "mtcid" moves by the collected cell numbers, the enemy captain's death wins and adds
    /// min(10, 1 + Restorers + 2 x Brawlers) Combat Trophies straight to the inventory (verified on 26 real matches:
    /// 7 without the Restorers, 10 with them), the legacy captured "Combat Trophy" captain drop is not rolled, and killed
    /// NPCs do not respawn during the match.
    /// </summary>
    private static void TestPvpMatchMtcidAndTrophies(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("19. Testing PvP matches (mtcid cell numbers, Bludrut Brawl trophies, no NPC respawn)...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_pvp_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 3,
          "Maps": { "bludrutbrawl": { "Name": "bludrutbrawl",
            "Cells": { "Enter0": ["Spawn"], "Crossupper": [], "Resource1A": [], "Morale1A": [], "Captain1": [] },
            "CellNumbers": { "14": { "Crossupper": 13 }, "18": { "Resource1A": 13 }, "15": { "Morale1A": 20 }, "28": { "Captain1": 19 } },
            "Monsters": {
              "9": { "MapID": 9, "MonsterID": 313, "Name": "(B) Defensive Restorer", "Cell": "Resource1A", "Level": 255, "MaxHP": 5000 },
              "10": { "MapID": 10, "MonsterID": 313, "Name": "(B) Defensive Restorer", "Cell": "Resource1A", "Level": 255, "MaxHP": 5000 },
              "13": { "MapID": 13, "MonsterID": 311, "Name": "(B) Brawler", "Cell": "Morale1A", "Level": 255, "MaxHP": 10000 },
              "16": { "MapID": 16, "MonsterID": 309, "Name": "Team B Captain", "Cell": "Captain1", "Level": 255, "MaxHP": 15000 } } } },
          "Monsters": { "id:309": { "Key": "id:309", "ID": 309, "Name": "Team B Captain", "Level": 255, "MaxHP": 15000, "KillCount": 37,
            "Drops": { "4889": { "ItemID": 4889, "ItemName": "Combat Trophy", "DropEvents": 37, "TrackedDropEvents": 37, "EligibleKills": 37, "RateBasis": "all_kills" } } } },
          "Items": { "4889": { "ID": 4889, "Name": "Combat Trophy", "Category": "Item", "MaxStack": 5000, "Coins": true } }
        }
        """);

        static string Script(bool killAds) => $$"""
        using Skua.Core.Interfaces;

        public class BrawlBot
        {
            private static void Move(IScriptInterface bot, int n) { bot.Send.Packet($"%xt%zm%mtcid%{bot.Map.RoomID}%{n}%"); bot.Sleep(2500); }

            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("bludrutbrawl-999999", "Enter0", "Spawn");
                Move(bot, 99); // unknown number: the character stays
                bot.Log("after99=" + bot.Player.Cell);
                if ({{(killAds ? "true" : "false")}})
                {
                    Move(bot, 14);
                    Move(bot, 18);
                    bot.Kill.Monster(9);
                    bot.Kill.Monster(10);
                    bot.Sleep(10_000);
                    bot.Log("restorersAlive=" + bot.Monsters.MapMonsters.Count(m => m.MapID <= 10 && m.HP > 0));
                }
                Move(bot, 15);
                bot.Kill.Monster(13);
                Move(bot, 28);
                bot.Log("cell=" + bot.Player.Cell);
                bot.Kill.Monster(16);
                bot.Sleep(3000);
                bot.Log("trophies=" + bot.Inventory.GetQuantity("Combat Trophy") + " drops=" + bot.Drops.CurrentDrops.Count());
                // CoreBots.CanBuy reads ACs through Flash; CoreBots.SellItem sells with a raw packet.
                bot.Log("coins=" + bot.Flash.GetGameObject<int>("world.myAvatar.objData.intCoins"));
                bot.Send.Packet($"%xt%zm%sellItem%{bot.Map.RoomID}%4889%1%123%");
                bot.Log("afterSell=" + bot.Inventory.GetQuantity("Combat Trophy"));
            }
        }
        """;

        SimulationReport RunBrawl(bool killAds)
        {
            var path = Path.Combine(tempDir, killAds ? "BrawlAdsBot.cs" : "BrawlBot.cs");
            File.WriteAllText(path, Script(killAds));
            return runner.Run(new SimulationRunOptions
            {
                ScriptPath = path,
                GameDataPath = gameDataPath,
                Scenario = new Scenario
                {
                    Name = "Brawl",
                    Player = new PlayerSetup { Username = "Tester", Level = 100, Coins = 1234, MaxHealth = 100_000, DamagePerSecond = 20_000 },
                    Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
                }
            });
        }

        var noAds = RunBrawl(killAds: false);
        var ads = RunBrawl(killAds: true);
        string Logs(SimulationReport r) => string.Join(" | ", r.ScriptLogs.Select(l => l.Text));
        Assert(noAds.Outcome == SimulationOutcome.Completed && ads.Outcome == SimulationOutcome.Completed,
            $"Both runs must complete ({noAds.Outcome}: {noAds.StopReason}; {ads.Outcome}: {ads.StopReason})");
        Assert(Logs(noAds).Contains("after99=Enter0") && noAds.Warnings.Any(w => w.Text.Contains("mtcid 99")),
            $"An uncollected cell number must not move the character and must warn: {Logs(noAds)}");
        Assert(Logs(noAds).Contains("cell=Captain1"), $"mtcid must move by the collected cell numbers: {Logs(noAds)}");
        Assert(Logs(noAds).Contains("trophies=3 drops=0") && noAds.PvpMatchesWon == 1,
            $"Brawler (2) + win (1) = 3 trophies, added to the inventory, no Combat Trophy drop rolled: {Logs(noAds)}");
        Assert(Logs(ads).Contains("restorersAlive=0") && Logs(ads).Contains("trophies=5 drops=0"),
            $"Killed Restorers stay dead and add 1 each (2 + 2 + 1 = 5): {Logs(ads)}");
        Assert(Logs(noAds).Contains("coins=1234") && Logs(noAds).Contains("afterSell=2"),
            $"Flash intCoins must return the ACs and the sellItem packet must sell: {Logs(noAds)}");
        Console.WriteLine($"   OK: no ads 3 trophies, with 2 Restorers 5 trophies, mtcid moves, unknown number warns, intCoins + sellItem packet");
    }

    /// <summary>
    /// Server warnings (texts from the collector's ServerMessages, 2026-11-24): refused joins (member-only from the
    /// scenario and from collected TargetMaps, seasonal "not available"), refused purchases (gold, AC, full bag) and
    /// refused pickups (item in the bank, full stack) reach the script through ExtensionPacketReceived with the real
    /// packet shapes, the refused action does not happen, and the report lists the messages.
    /// </summary>
    /// <summary>
    /// AQW server list: parsed from a real response of the public API, served by Bot.Servers, named relogins checked
    /// against it (member-only / full for non-members, offline, unknown), the scenario list wins, and the invented
    /// stand-in is reported as synthesized only when a script uses it.
    /// </summary>
    private static void TestServerList(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("24. Testing the AQW server list (servers.json, scenario servers, refused named relogins)...");
        // Real response of https://game.aq.com/game/api/data/servers (2026-11-24, first entries; public data).
        const string apiSample = """
        [{"sName":"Twilly","sIP":"sock7.aq.com","iCount":948,"iMax":1000,"bOnline":1,"iChat":0,"bUpg":0,"sLang":"xx","iPort":5593,"iLevel":0,"accessLevel":0},
         {"sName":"Twig","sIP":"sock8.aq.com","iCount":433,"iMax":1000,"bOnline":1,"iChat":0,"bUpg":0,"sLang":"xx","iPort":5589,"iLevel":0,"accessLevel":0},
         {"sName":"Artix","sIP":"sock7.aq.com","iCount":1501,"iMax":1500,"bOnline":1,"iChat":2,"bUpg":0,"sLang":"it","iPort":5588,"iLevel":0,"accessLevel":0},
         {"sName":"Sir Ver","sIP":"sock7.aq.com","iCount":88,"iMax":1200,"bOnline":1,"iChat":2,"bUpg":1,"sLang":"en","iPort":5589,"iLevel":0,"accessLevel":0},
         {"sName":"Swordhaven (EU)","sIP":"euro.aqw.artix.com","iCount":13,"iMax":1000,"bOnline":1,"iChat":2,"bUpg":0,"sLang":"it","iPort":5588,"iLevel":0,"accessLevel":0}]
        """;
        var parsed = AqwServerList.ParseApi(apiSample, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc));
        Assert(parsed.Servers.Count == 5 && parsed.Servers[3] is { Name: "Sir Ver", MemberOnly: true, Port: 5589, PlayerCount: 88, MaxPlayers: 1200 },
            "The API response must parse into the server list in order");
        Assert(parsed.Servers[2].RefusalFor(isMember: false) != null && parsed.Servers[2].RefusalFor(isMember: true) == null,
            "A full server refuses non-members only (the live list shows members above the cap)");
        var serversPath = Path.Combine(tempDir, "servers_test.json");
        parsed.Save(serversPath);
        Assert(AqwServerList.TryLoad(serversPath)?.Fingerprint() == parsed.Fingerprint(), "servers.json must round-trip");

        var script = Path.Combine(tempDir, "ServerBot.cs");
        File.WriteAllText(script, """
        using System.Linq;
        using Skua.Core.Interfaces;

        public class ServerBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log("servers=" + string.Join(",", bot.Servers.CachedServers.Select(s => s.Name + (s.Upgrade ? "*" : ""))));
                bot.Log("sirver=" + bot.Servers.Relogin("Sir Ver") + " loggedIn=" + bot.Player.LoggedIn);
                bot.Log("artix=" + bot.Servers.Relogin("Artix"));
                bot.Log("nowhere=" + bot.Servers.Relogin("Nowhere"));
                bot.Log("twig=" + bot.Servers.Relogin("Twig") + " loggedIn=" + bot.Player.LoggedIn + " last=" + bot.Servers.LastName);
            }
        }
        """);
        var quietScript = Path.Combine(tempDir, "QuietServerBot.cs");
        File.WriteAllText(quietScript, """
        using Skua.Core.Interfaces;

        public class QuietServerBot
        {
            public void ScriptMain(IScriptInterface bot) => bot.Log("level=" + bot.Player.Level);
        }
        """);

        Scenario NonMember() => new() { Name = "Servers", Player = new PlayerSetup { IsMember = false }, Limits = new LimitsSetup { MaxVirtualMinutes = 5 } };

        var report = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = NonMember(), ServersPath = serversPath });
        var logs = string.Join(" | ", report.ScriptLogs.Select(l => l.Text));
        Assert(report.Outcome == SimulationOutcome.Completed, $"Run must complete ({report.Outcome}: {report.StopReason}) {logs}");
        Assert(logs.Contains("servers=Twilly,Twig,Artix,Sir Ver*,Swordhaven (EU)"), $"Bot.Servers must serve servers.json: {logs}");
        Assert(logs.Contains("sirver=False loggedIn=False") && logs.Contains("artix=False") && logs.Contains("nowhere=False"),
            $"Member-only, full and unknown servers must refuse a non-member and leave them logged out: {logs}");
        Assert(logs.Contains("twig=True loggedIn=True last=Twig"), $"An open server must accept the login: {logs}");
        Assert(!report.SynthesizedData.Any(s => s.Key == "servers") && report.ServersInfo?.Contains("servers_test.json") == true,
            $"A server list from file is not synthesized and the report names it ({report.ServersInfo})");

        var memberScenario = NonMember();
        memberScenario.Player.IsMember = true;
        memberScenario.Servers = new List<ServerSetup>
        {
            new() { Name = "Sir Ver", Ip = "sock7.aq.com", MemberOnly = true, PlayerCount = 10, MaxPlayers = 1200 },
            new() { Name = "Artix", Ip = "sock7.aq.com", PlayerCount = 1501, MaxPlayers = 1500 },
            new() { Name = "Twig", Ip = "sock8.aq.com", Online = false }
        };
        report = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = memberScenario, ServersPath = serversPath });
        logs = string.Join(" | ", report.ScriptLogs.Select(l => l.Text));
        Assert(logs.Contains("servers=Sir Ver*,Artix,Twig") && logs.Contains("sirver=True") && logs.Contains("artix=True") && logs.Contains("twig=False"),
            $"The scenario list must win; members enter member-only and full servers, nobody enters an offline one: {logs}");
        Assert(report.ServersInfo?.StartsWith("scenario") == true, $"Report must name the scenario list ({report.ServersInfo})");

        var missing = Path.Combine(tempDir, "no_servers_here.json");
        report = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = NonMember(), ServersPath = missing });
        Assert(report.SynthesizedData.Any(s => s.Key == "servers"), "Without a server list the stand-in must be reported as synthesized");
        report = runner.Run(new SimulationRunOptions { ScriptPath = quietScript, Scenario = NonMember(), ServersPath = missing });
        Assert(!report.SynthesizedData.Any(s => s.Key == "servers") && report.ServersInfo == null,
            "A script that never uses Bot.Servers must not get a synthesized server list in its report");
        Console.WriteLine("   OK: server list parsed from the API, served, refusals (member-only/full/offline/unknown), scenario override, lazy synthesis");
    }

    private static void TestServerWarnings(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("20. Testing server warnings (refused joins / purchases / pickups -> ExtensionPacketReceived)...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_warnings_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 3,
          "Maps": { "zzgemmap": { "Name": "zzgemmap", "Cells": { "Enter": ["Spawn"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990401, "Name": "Gem Beast", "Cell": "Enter", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990401": { "Key": "id:990401", "ID": 990401, "Name": "Gem Beast", "Level": 5, "MaxHP": 300, "KillCount": 10,
            "Drops": {
              "990501": { "ItemID": 990501, "ItemName": "Banked Token", "DropEvents": 10, "TrackedDropEvents": 10, "EligibleKills": 10, "RateBasis": "all_kills" },
              "990502": { "ItemID": 990502, "ItemName": "Full Gem", "DropEvents": 10, "TrackedDropEvents": 10, "EligibleKills": 10, "RateBasis": "all_kills" } } } },
          "Shops": { "9101": { "ID": 9101, "Name": "Warning Shop", "Items": [
            { "ShopItemID": 1, "ID": 990601, "Name": "Pricey Sword", "Category": "Sword", "Cost": 5000, "Coins": false },
            { "ShopItemID": 2, "ID": 990602, "Name": "AC Helm", "Category": "Helm", "Cost": 500, "Coins": true },
            { "ShopItemID": 3, "ID": 990603, "Name": "Cheap Gem", "Category": "Item", "Cost": 1, "Coins": false, "MaxStack": 10 } ] } },
          "Items": { "990651": { "ID": 990651, "Name": "Mystery Helm", "Category": "Helm", "Coins": true, "MaxStack": 1 } },
          "ServerMessages": { "warning|\"<Q>\" is an Membership-Only Map.": { "Kind": "warning", "Text": "\"<Q>\" is an Membership-Only Map.", "Count": 1,
            "PrecededBy": { "cmd:tfer": 1 }, "TargetMaps": { "zzmembermap": 1 } } }
        }
        """);

        var script = Path.Combine(tempDir, "WarningBot.cs");
        File.WriteAllText(script, """
        using System.Collections.Generic;
        using Skua.Core.Interfaces;

        public class WarningBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                var seen = new List<string>();
                bot.Events.ExtensionPacketReceived += packet =>
                {
                    string type = packet["params"].type;
                    dynamic data = packet["params"].dataObj;
                    if (type == "str")
                        seen.Add((string)data[0] + ":" + (string)data[2]);
                    else if ((string)data.cmd == "buyItem")
                        seen.Add("json:buyItem:" + (int)data.bitSuccess + ":" + (string)(data.strMessage ?? ""));
                };

                bot.Map.Join("lowe");
                bot.Map.Join("zzmembermap");
                bot.Map.Join("walpurgisnacht");
                bot.Log("mapAfterRefusals=" + bot.Map.Name);

                bot.Map.Join("zzgemmap");
                bot.Kill.Monster("Gem Beast");
                bot.Sleep(1000);
                bot.Drops.Pickup("Banked Token");
                bot.Drops.Pickup("Full Gem");
                bot.Log("bankedTokenInInventory=" + bot.Inventory.Contains("Banked Token") + " fullGem=" + bot.Inventory.GetQuantity("Full Gem"));

                bot.Shops.Load(9101);
                bot.Sleep(1000);
                bot.Shops.BuyItem("Pricey Sword");
                bot.Shops.BuyItem("AC Helm");
                bot.Shops.BuyItem("Cheap Gem");
                bot.Log("bought=" + bot.Inventory.Contains("Pricey Sword") + bot.Inventory.Contains("AC Helm") + bot.Inventory.Contains("Cheap Gem"));
                bot.Shops.Load(9999); // never captured
                bot.Sleep(1000);
                bot.Shops.BuyItem("Mystery Helm");
                bot.Shops.BuyItem("Never Seen Relic");
                bot.Log("seen=" + string.Join(" || ", seen));
            }
        }
        """);

        var report = runner.Run(new SimulationRunOptions
        {
            ScriptPath = script,
            GameDataPath = gameDataPath,
            Scenario = new Scenario
            {
                Name = "Server warnings",
                Player = new PlayerSetup { Username = "Tester", Level = 50, Gold = 100, Coins = 0, IsMember = false, InventorySlots = 1,
                    CompleteEquipment = false, MaxHealth = 10_000, DamagePerSecond = 5_000 },
                Inventory = new List<ItemSetup> { new() { Name = "Full Gem", Id = 990502, Quantity = 10, MaxStack = 10 } },
                Bank = new List<ItemSetup> { new() { Name = "Banked Token", Id = 990501, Quantity = 5, MaxStack = 100 } },
                World = new WorldSetup { MemberOnlyMaps = new List<string> { "lowe" }, UnavailableMaps = new List<string> { "walpurgisnacht" } },
                Limits = new LimitsSetup { MaxVirtualMinutes = 10 }
            }
        });

        var logs = string.Join(" | ", report.ScriptLogs.Select(l => l.Text));
        Assert(report.Outcome == SimulationOutcome.Completed, $"Run must complete ({report.Outcome}: {report.StopReason}) {logs}");
        Assert(logs.Contains("mapAfterRefusals=battleon"), $"Refused joins must leave the character where it was: {logs}");
        foreach (var expected in new[]
        {
            "warning:\"lowe\" is an Membership-Only Map.",
            "warning:\"zzmembermap\" is an Membership-Only Map.",
            "warning:\"walpurgisnacht\" is not available.",
            "warning:Item already in your Bank.",
            "warning:Already have a full stack of that item.",
            "json:buyItem:0:Item is not buyable. Not enough Gold",
            "json:buyItem:0:Item is not buyable. Not enough Adventure Coins",
            "json:buyItem:0:Item is not buyable. Bag Item Inventory full",
        })
            Assert(logs.Contains(expected), $"Missing server message '{expected}': {logs}");
        Assert(logs.Contains("bankedTokenInInventory=False fullGem=10") && logs.Contains("bought=FalseFalseFalse"),
            $"Refused pickups/purchases must not change the inventory: {logs}");
        Assert(report.ServerMessages.GetValueOrDefault("Item is not buyable. Not enough Gold") == 1 &&
               report.ToMarkdown().Contains("## Server Warnings & Messages"),
            "The report must list the server messages sent");
        Assert(report.SynthesizedData.Any(s => s.Key == "accost:mystery helm") && report.SynthesizedData.Any(s => s.Key == "currency:never seen relic"),
            "Buying an AC item or an unknown item from an uncaptured shop must flag the unknown price");
        Console.WriteLine($"   OK: {report.ServerMessages.Values.Sum()} server messages (member/seasonal joins, gold/AC/bag purchases, bank/full-stack pickups)");
    }

    /// <summary>
    /// Scenario fault injection: each kind fires on the configured occurrence, refuses the action (or swallows drops),
    /// sends its message, and the same scenario gives the same faults on a second run.
    /// </summary>
    private static void TestInjectedFaults(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("21. Testing scenario fault injection (joinFail, dropDrought, tooSoonAfterCombat, buyDesync, slowDown, disconnect)...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_faults_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 3,
          "Maps": { "zzgemmap": { "Name": "zzgemmap", "Cells": { "Enter": ["Spawn"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990701, "Name": "Shard Beast", "Cell": "Enter", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990701": { "Key": "id:990701", "ID": 990701, "Name": "Shard Beast", "Level": 5, "MaxHP": 300, "KillCount": 10,
            "Drops": { "990801": { "ItemID": 990801, "ItemName": "Gem Shard", "Temp": true, "DropEvents": 10, "TrackedDropEvents": 10, "EligibleKills": 10, "RateBasis": "all_kills" } } } },
          "Shops": { "9201": { "ID": 9201, "Name": "Fault Shop", "Items": [
            { "ShopItemID": 1, "ID": 990901, "Name": "Cheap Gem", "Category": "Item", "Cost": 1, "Coins": false, "MaxStack": 10 } ] } }
        }
        """);

        var script = Path.Combine(tempDir, "FaultBot.cs");
        File.WriteAllText(script, """
        using System.Collections.Generic;
        using Skua.Core.Interfaces;

        public class FaultBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                var seen = new List<string>();
                bot.Events.ExtensionPacketReceived += packet =>
                {
                    string type = packet["params"].type;
                    dynamic data = packet["params"].dataObj;
                    if (type == "str")
                        seen.Add((string)data[2]);
                    else if (data.strMessage != null)
                        seen.Add((string)data.strMessage);
                };

                bot.Map.Join("zzgemmap");
                bot.Log("join1=" + bot.Map.Name);
                bot.Map.Join("zzgemmap");
                bot.Log("join2=" + bot.Map.Name);

                for (var i = 0; i < 3; i++)
                {
                    bot.Kill.Monster("Shard Beast");
                    bot.Sleep(6000);
                }
                bot.Log("shards=" + bot.TempInv.GetQuantity("Gem Shard"));

                bot.Shops.Load(9201);
                bot.Shops.BuyItem("Cheap Gem");
                bot.Log("afterRefusedLoad=" + bot.Inventory.GetQuantity("Cheap Gem"));
                bot.Shops.Load(9201);
                bot.Shops.BuyItem("Cheap Gem");
                bot.Log("afterDesync=" + bot.Inventory.GetQuantity("Cheap Gem"));
                bot.Shops.BuyItem("Cheap Gem");
                bot.Log("afterBuy=" + bot.Inventory.GetQuantity("Cheap Gem"));

                bot.Shops.SellItem("Cheap Gem");
                bot.Log("afterSlowSell=" + bot.Inventory.GetQuantity("Cheap Gem"));
                bot.Shops.SellItem("Cheap Gem");
                bot.Log("afterSell=" + bot.Inventory.GetQuantity("Cheap Gem"));

                bot.Map.Join("battleon");
                bot.Log("loggedInRightAfter=" + bot.Player.LoggedIn);
                bot.Sleep(10_000);
                bot.Log("loggedInLater=" + bot.Player.LoggedIn + " map=" + bot.Map.Name);
                bot.Log("seen=" + string.Join(" || ", seen));
            }
        }
        """);

        SimulationReport Run() => runner.Run(new SimulationRunOptions
        {
            ScriptPath = script,
            GameDataPath = gameDataPath,
            Scenario = new Scenario
            {
                Name = "Faults",
                Player = new PlayerSetup { Username = "Tester", Level = 50, Gold = 1000, CompleteEquipment = false, MaxHealth = 10_000, DamagePerSecond = 5_000 },
                BotOptions = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AutoRelogin"] = System.Text.Json.JsonDocument.Parse("true").RootElement
                },
                Faults = new List<FaultSetup>
                {
                    new() { Kind = "joinFail", Nth = new List<int> { 1 } },
                    new() { Kind = "dropDrought", Nth = new List<int> { 1 }, Kills = 2 },
                    new() { Kind = "tooSoonAfterCombat", Action = "loadShop" },
                    new() { Kind = "buyDesync", Target = "Cheap Gem", Nth = new List<int> { 2 } }, // #1 = the buy without a loaded shop
                    new() { Kind = "slowDown", Action = "sell" },
                    new() { Kind = "disconnect", Action = "join", Nth = new List<int> { 3 } },
                    new() { Kind = "noSuchFault" },
                },
                Limits = new LimitsSetup { MaxVirtualMinutes = 10 }
            }
        });

        var report = Run();
        var logs = string.Join(" | ", report.ScriptLogs.Select(l => l.Text));
        Assert(report.Outcome == SimulationOutcome.Completed, $"Run must complete ({report.Outcome}: {report.StopReason}) {logs}");
        foreach (var expected in new[]
        {
            "join1=battleon", "join2=zzgemmap", "shards=1", "afterRefusedLoad=0", "afterDesync=0", "afterBuy=1",
            "afterSlowSell=1", "afterSell=0", "loggedInRightAfter=False", "loggedInLater=True map=battleon",
            "You cannot do that too soon after combat.", "Item is not buyable. Item Inventory full. Re-login to syncronize your real bag slot amount.",
            "Please slow down. Last action was too soon!",
        })
            Assert(logs.Contains(expected), $"Missing '{expected}': {logs}");
        Assert(report.InjectedFaults.Count == 6 && report.Warnings.Any(w => w.Text.Contains("noSuchFault")),
            $"6 faults must fire and the unknown kind must be reported: {string.Join(" / ", report.InjectedFaults)}");
        var again = Run();
        Assert(again.InjectedFaults.SequenceEqual(report.InjectedFaults), "The same scenario must inject the same faults at the same times");
        Console.WriteLine($"   OK: {report.InjectedFaults.Count} faults fired, script effects as expected, identical on a second run");
    }

    private static void TestScenarioAssertions(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("22. Testing scenario asserts (pass, fail, gained, invalid, compile error, JSON round-trip)...");
        var gameDataPath = Path.Combine(tempDir, "gamedata_asserts_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 3,
          "Maps": { "zzassertmap": { "Name": "zzassertmap", "Cells": { "Enter": ["Spawn"] },
            "Monsters": { "1": { "MapID": 1, "MonsterID": 990711, "Name": "Assert Beast", "Cell": "Enter", "Level": 5, "MaxHP": 300 } } } },
          "Monsters": { "id:990711": { "Key": "id:990711", "ID": 990711, "Name": "Assert Beast", "Level": 5, "MaxHP": 300, "KillCount": 10,
            "Drops": { "990811": { "ItemID": 990811, "ItemName": "Assert Shard", "Temp": true, "DropEvents": 10, "TrackedDropEvents": 10, "EligibleKills": 10, "RateBasis": "all_kills" } } } },
          "Shops": { "9211": { "ID": 9211, "Name": "Assert Shop", "Items": [
            { "ShopItemID": 1, "ID": 990911, "Name": "Assert Gem", "Category": "Item", "Cost": 100, "Coins": false, "MaxStack": 10 } ] } }
        }
        """);

        var script = Path.Combine(tempDir, "AssertBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class AssertBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzassertmap");
                for (var i = 0; i < 3; i++)
                {
                    bot.Kill.Monster("Assert Beast");
                    bot.Sleep(6000);
                }
                bot.Shops.Load(9211);
                bot.Shops.BuyItem("Assert Gem");
                bot.Shops.BuyItem("Assert Gem");
                bot.Log("assert bot done");
            }
        }
        """);

        var scenarioJson = """
        {
          "Name": "Asserts",
          "Script": "AssertBot.cs",
          "Player": { "Username": "Tester", "Level": 50, "Gold": 1000, "CompleteEquipment": false, "MaxHealth": 10000, "DamagePerSecond": 5000 },
          "Limits": { "MaxVirtualMinutes": 10 },
          "Asserts": [
            { "check": "outcome", "is": ["Completed"] },
            { "check": "item", "name": "assert gem", "in": "inventory", "exactly": 2 },
            { "check": "item", "name": "Assert Gem", "gained": true, "min": 2 },
            { "check": "gold", "gained": true, "min": -200, "max": -100 },
            { "check": "kills", "name": "Assert Beast", "exactly": 3 },
            { "check": "deaths" },
            { "check": "drops", "name": "Assert Shard" },
            { "check": "map", "is": ["zzassertmap"] },
            { "check": "mapVisited", "name": "zzassertmap" },
            { "check": "scriptLog", "text": "ASSERT BOT DONE", "exactly": 1 },
            { "check": "questTurnIns", "max": 0 },
            { "check": "virtualMinutes", "max": 10 },
            { "check": "unimplemented" },
            { "check": "level", "min": 60, "description": "expected failure: level 60" },
            { "check": "kills", "max": 2 },
            { "check": "noSuchCheck" },
            { "check": "item" },
            { "check": "level" },
            { "check": "deaths", "gained": true }
          ]
        }
        """;
        var scenarioPath = Path.Combine(tempDir, "asserts_scenario.json");
        File.WriteAllText(scenarioPath, scenarioJson);
        var scenario = Scenario.Load(scenarioPath);
        Assert(scenario.Asserts.Count == 19 && scenario.Script == "AssertBot.cs", "Asserts and script must load from JSON (case-insensitive keys)");
        var roundTrip = JsonSerializerRoundTrip(scenario);
        Assert(roundTrip.Asserts.Count == 19 && roundTrip.Asserts[1].Exactly == 2 && roundTrip.Asserts[0].Is.SequenceEqual(new[] { "Completed" }),
            "Asserts must survive a save/load round-trip (scenario editor)");

        var report = runner.Run(new SimulationRunOptions { ScriptPath = script, GameDataPath = gameDataPath, Scenario = scenario });
        var logs = string.Join(" | ", report.ScriptLogs.Select(l => l.Text));
        Assert(report.Outcome == SimulationOutcome.Completed, $"Run must complete ({report.Outcome}: {report.StopReason}) {logs}");
        Assert(report.Assertions.Count == 19, $"Every assert must produce a result, got {report.Assertions.Count}");
        string Show(AssertionResult a) => $"#{a.Index} {a.Check} {(a.Passed ? "PASS" : "FAIL")} [{a.Description}] expected {a.Expected}, got {a.Actual}";
        var all = string.Join(Environment.NewLine, report.Assertions.Select(Show));
        for (var i = 0; i < 13; i++)
            Assert(report.Assertions[i].Passed, $"Assert #{i + 1} must pass:{Environment.NewLine}{all}");
        for (var i = 13; i < 19; i++)
            Assert(!report.Assertions[i].Passed, $"Assert #{i + 1} must fail:{Environment.NewLine}{all}");
        Assert(report.Assertions[2].Actual.Contains("+2"), $"Gained must show the change: {Show(report.Assertions[2])}");
        Assert(report.Assertions[13].Description == "expected failure: level 60" && report.Assertions[13].Actual == "50", Show(report.Assertions[13]));
        Assert(report.Assertions[14].Actual == "3", $"kills max 2 must report 3: {Show(report.Assertions[14])}");
        foreach (var i in new[] { 15, 16, 17, 18 })
            Assert(report.Assertions[i].Actual.StartsWith("invalid:"), $"Assert #{i + 1} must be reported invalid: {Show(report.Assertions[i])}");
        Assert(report.AssertionsFailed == 6, $"6 asserts must fail, got {report.AssertionsFailed}");
        var markdown = report.ToMarkdown();
        Assert(markdown.Contains("## Scenario Assertions") && markdown.Contains("**FAILED** (13/19 passed)"), "Markdown must show the assertion section and summary");

        // A compile error: only the outcome assert can be evaluated; the rest fail as "did not run".
        var broken = Path.Combine(tempDir, "BrokenAssertBot.cs");
        File.WriteAllText(broken, "public class BrokenAssertBot { public void ScriptMain(Skua.Core.Interfaces.IScriptInterface bot) { this does not compile } }");
        var brokenReport = runner.Run(new SimulationRunOptions
        {
            ScriptPath = broken,
            GameDataPath = gameDataPath,
            Scenario = new Scenario
            {
                Asserts = new List<AssertSetup>
                {
                    new() { Check = "outcome", Is = new List<string> { "CompileError" } },
                    new() { Check = "deaths" }
                }
            }
        });
        Assert(brokenReport.Outcome == SimulationOutcome.CompileError && brokenReport.Assertions.Count == 2
               && brokenReport.Assertions[0].Passed && !brokenReport.Assertions[1].Passed
               && brokenReport.Assertions[1].Actual.Contains("did not run"),
            $"Compile error asserts: {string.Join(" / ", brokenReport.Assertions.Select(Show))}");

        // Deterministic: the same scenario gives the same assertion results.
        var again = runner.Run(new SimulationRunOptions { ScriptPath = script, GameDataPath = gameDataPath, Scenario = Scenario.Load(scenarioPath) });
        Assert(again.Assertions.SequenceEqual(report.Assertions), "The same scenario must give the same assertion results");
        Console.WriteLine($"   OK: 13 asserts passed, 6 expected failures (2 wrong values, 4 invalid), compile-error handling, identical on a second run");
    }

    private static Scenario JsonSerializerRoundTrip(Scenario scenario) =>
        System.Text.Json.JsonSerializer.Deserialize<Scenario>(scenario.ToJson(), Scenario.JsonOptions)!;

    private static void TestBackgroundTaskDeterminism(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("16. Testing background tasks (Task.Run / Task.Delay / Task.WaitAll / new Random) run in a fixed order...");
        var script = Path.Combine(tempDir, "BackgroundBot.cs");
        File.WriteAllText(script, """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Skua.Core.Interfaces;

        public class BackgroundBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                var cts = new CancellationTokenSource();
                var tasks = new List<Task>();
                for (int w = 0; w < 3; w++)
                {
                    int id = w;
                    tasks.Add(Task.Run(async () =>
                    {
                        Random rng = new();
                        try
                        {
                            while (!cts.Token.IsCancellationRequested)
                            {
                                bot.Log($"bg{id} t={bot.Player.Gold} r={rng.Next(1000)}");
                                await Task.Delay(300 + 100 * id, cts.Token);
                            }
                        }
                        catch (OperationCanceledException) { }
                    }, cts.Token));
                }
                for (int i = 0; i < 12; i++)
                {
                    bot.Kill.Monster("Boss Guard");
                    bot.Log($"main kill {i} gold={bot.Player.Gold}");
                }
                cts.Cancel();
                bool stopped = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
                bot.Log($"stopped={stopped} r={new Random().Next(1000)}");
            }
        }
        """);

        Scenario MakeScenario() => new()
        {
            Name = "Background determinism",
            Seed = 4242,
            Player = new PlayerSetup { Username = "Bg", Level = 60, Gold = 1000, Class = "Warrior", MaxHealth = 50_000, DamagePerSecond = 3_000 },
            Location = new LocationSetup { Map = "bgarena", Cell = "r1", Pad = "Left" },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["bgarena"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup> { new() { Name = "Boss Guard", Cell = "r1", MaxHP = 4_000, Level = 40 } }
                }
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30, MaxRealSeconds = 60 }
        };

        string Fingerprint(SimulationReport r) => string.Join("\n", r.ScriptLogs.Select(l => $"{l.VirtualMs} {l.Text}"));
        var a = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario() });
        var b = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario() });

        Assert(a.Outcome == SimulationOutcome.Completed, $"Expected Completed, got {a.Outcome} ({a.ExceptionText ?? a.StopReason})");
        var logs = a.ScriptLogs.Select(l => l.Text).ToList();
        Assert(logs.Count(l => l.StartsWith("main kill")) == 12 && logs.Any(l => l.StartsWith("bg0")) && logs.Any(l => l.StartsWith("bg2")),
            $"Main and background logs expected:\n{string.Join("\n", logs.Take(20))}");
        Assert(logs.Any(l => l.StartsWith("stopped=True")), "Task.WaitAll must see the cancelled background tasks finish (virtual timeout)");
        Assert(!a.Warnings.Any(w => w.Text.Contains("outside the simulator")), "No background task may fall back to free-running");
        Assert(Fingerprint(a) == Fingerprint(b) && a.FinalState.Gold == b.FinalState.Gold && a.VirtualDuration == b.VirtualDuration,
            $"Same seed must repeat the interleaving exactly:\n--- run 1\n{Fingerprint(a)}\n--- run 2\n{Fingerprint(b)}");
        Console.WriteLine($"   OK: {logs.Count} log lines identical in 2 runs ({logs.Count(l => l.StartsWith("bg"))} from background tasks), {a.VirtualDuration} virtual");
    }

    /// <summary>
    /// Quest pets (QuestData accept requirements): quest 5755 "Time for Some Spring Cleaning" needs the Shogun Paragon Pet
    /// in the inventory (the bank does not count — AQW rule), IsAvailable follows Skua's checks, world.getQuestValue
    /// returns story progress, and a checked-but-missing pet is listed in the report.
    /// </summary>
    private static void TestQuestPetsAndUnlockItems(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("17. Testing quest pets / unlock items (inventory rule, IsAvailable, getQuestValue, report list)...");
        var script = Path.Combine(tempDir, "PetQuestBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class PetQuestBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bool owned = bot.Inventory.Contains("Shogun Paragon Pet", 1) || bot.Bank.Contains("Shogun Paragon Pet", 1);
                int slot = bot.Flash.CallGameFunction<int>("world.getQuestValue", 31);
                bool available = bot.Quests.IsAvailable(5755);
                bot.Quests.Accept(5755);
                bot.Log($"owned={owned} slot={slot} available={available} accepted={bot.Quests.IsInProgress(5755)}");
            }
        }
        """);

        SimulationReport Run(string where)
        {
            var scenario = new Scenario
            {
                Name = $"Shogun pet in {where}",
                Seed = 1,
                Player = new PlayerSetup { Username = "Pet", Level = 100, Class = "Warrior" },
                Quests = new QuestSetup { Completed = new List<int> { 793 } }, // Legion story done: quest slot 31 = 5
                Limits = new LimitsSetup { MaxVirtualMinutes = 5, MaxRealSeconds = 60 }
            };
            var pet = new ItemSetup { Name = "Shogun Paragon Pet", Id = 38621, Category = "Pet" };
            if (where == "inventory") scenario.Inventory.Add(pet);
            if (where == "bank") scenario.Bank.Add(pet);
            var report = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = scenario });
            Assert(report.Outcome == SimulationOutcome.Completed, $"{where}: expected Completed, got {report.Outcome} ({report.ExceptionText ?? report.StopReason})");
            return report;
        }
        string Line(SimulationReport r) => r.ScriptLogs.Select(l => l.Text).LastOrDefault(t => t.Contains("owned=")) ?? "";

        var none = Run("none");
        var bank = Run("bank");
        var inv = Run("inventory");
        Assert(Line(none).Contains("owned=False slot=5 available=False accepted=False"), $"No pet: {Line(none)}");
        Assert(Line(bank).Contains("owned=True slot=5 available=False accepted=False"), $"Pet in the bank must not open the quest: {Line(bank)}");
        Assert(Line(inv).Contains("owned=True slot=5 available=True accepted=True"), $"Pet in the inventory opens the quest: {Line(inv)}");
        Assert(none.MissingUnlockItems.Any(m => m.Name == "Shogun Paragon Pet" && m.Opens.Contains("5755")),
            "A checked pet the character does not own must be listed with the quest it opens");
        Assert(!inv.MissingUnlockItems.Any(m => m.Name == "Shogun Paragon Pet"), "An owned pet must not be listed as missing");
        Console.WriteLine($"   OK: none/bank/inventory = {Line(none)} | {Line(bank)} | {Line(inv)}");
    }

    private static void TestRealDataRewardsRespawnHouseResetsAndCalendar(SimulationRunner runner, string tempDir)
    {
        Console.WriteLine("15. Testing real-data consumption (kill rewards, respawn, rep, quest-gated map items, house items, daily reset, AE calendar)...");
        // AQW boosts add up: 1000 rep + server boost = 2000; 1000 XP + XP boost + 50 % gear = 2500; live 210 gold -> 525.
        Assert(Math.Abs(1000 * RewardBoosts.Combine(1.0, false, true) - 2000) < 0.01 &&
               Math.Abs(1000 * RewardBoosts.Combine(1.5, true, false) - 2500) < 0.01 &&
               Math.Abs(210 * RewardBoosts.Combine(1.5, false, true) - 525) < 0.01,
            "Reward boosts must be additive (highest gear bonus + consumable +100 % + server +100 %)");
        var gameDataPath = Path.Combine(tempDir, "gamedata_v3_test.json");
        File.WriteAllText(gameDataPath, """
        {
          "SchemaVersion": 3,
          "Factions": { "990060": { "ID": 990060, "Name": "Test Faction" } },
          "Items": {
            "52566": { "ID": 52566, "Name": "Star of the Sandsea", "Category": "Quest Item", "MaxStack": 1, "Temp": true },
            "990401": { "ID": 990401, "Name": "Choice Gem", "Category": "Item", "MaxStack": 10 }
          },
          "Maps": { "zzrewardmap": { "Name": "zzrewardmap", "Cells": { "Enter": ["Spawn"], "r1": ["Left"], "r2": ["Left"] },
            "Monsters": {
              "1": { "MapID": 1, "MonsterID": 990201, "Name": "Reward Wolf", "Cell": "r1", "Level": 30, "MaxHP": 300,
                     "Respawn": { "Samples": 5, "TotalMs": 12500, "MinMs": 2400, "MaxMs": 2600 } },
              "2": { "MapID": 2, "MonsterID": 990202, "Name": "Plain Imp", "Cell": "r2", "Level": 20, "MaxHP": 300 } },
            "MapItems": { "5044": { "MapItemID": 5044, "GrantedItemIDs": [52566], "RequiredQuestIds": [7277] } } } },
          "Monsters": {
            "id:990201": { "Key": "id:990201", "ID": 990201, "Name": "Reward Wolf", "Level": 30, "MaxHP": 300, "KillCount": 10,
              "KillRewards": { "Samples": 10, "ExpSamples": 10, "TotalBaseExp": 3000, "GoldSamples": 10, "TotalBaseGold": 900,
                               "RepSamples": 10, "TotalBaseRep": 500, "FactionIds": [990060] } },
            "id:990202": { "Key": "id:990202", "ID": 990202, "Name": "Plain Imp", "Level": 20, "MaxHP": 300 } },
          "Quests": { "990301": { "ID": 990301, "Name": "Captured Daily", "Field": "id0", "Slot": -1,
            "Rewards": [ { "ItemID": 990401, "ItemName": "Choice Gem", "Quantity": 1, "RatePercent": 100.0, "RewardType": 0 } ],
            "ObservedReward": { "Samples": 3, "Exp": 0, "Gold": 50, "Rep": 1000, "FactionID": 990060 } } }
        }
        """);

        var script = Path.Combine(tempDir, "RealDataBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class RealDataBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzrewardmap");
                var gold0 = bot.Player.Gold;
                bot.Hunt.Monster("Reward Wolf");
                bot.Log($"WOLF gold+={bot.Player.Gold - gold0}");
                bot.Sleep(1_500);
                bot.Log($"AFTER1500 alive={bot.Monsters.MapMonsters.Exists(m => m.Name == "Reward Wolf" && m.HP > 0)}");
                bot.Sleep(1_500);
                bot.Log($"AFTER3000 alive={bot.Monsters.MapMonsters.Exists(m => m.Name == "Reward Wolf" && m.HP > 0)}");
                var gold1 = bot.Player.Gold;
                bot.Hunt.Monster("Plain Imp");
                bot.Log($"IMP gold+={bot.Player.Gold - gold1}");

                bot.Map.GetMapItem(5044);
                bot.Log($"STAR={bot.Inventory.GetQuantity("Star of the Sandsea") + bot.TempInv.GetQuantity("Star of the Sandsea")}");

                bot.Log($"HOUSEQUEST accept={bot.Quests.EnsureAccept(9555)}");

                bot.Log($"DAILY1 accept={bot.Quests.EnsureAccept(990301)} complete={bot.Quests.EnsureComplete(990301)}");
                bot.Log($"DAILY2 accept={bot.Quests.EnsureAccept(990301)}");
                bot.Sleep(5 * 60_000); // crosses 00:00 server time
                bot.Log($"DAILY3 accept={bot.Quests.EnsureAccept(990301)}");
            }
        }
        """);

        // 2026-11-21 04:57 UTC = 2026-11-20 23:57 US Eastern (EST): the daily reset happens 3 virtual minutes in.
        Scenario MakeScenario(bool withHouseItem, bool questActive, bool calendar) => new()
        {
            Name = "Real data",
            StartDate = new DateTime(2026, 11, 21, 4, 57, 0, DateTimeKind.Utc),
            Player = new PlayerSetup { Username = "Tester", Level = 60, MaxHealth = 10_000, DamagePerSecond = 8_000 },
            House = withHouseItem ? new List<ItemSetup> { new() { Name = "Prime Fiend Shard" } } : new List<ItemSetup>(),
            Quests = new QuestSetup { Active = questActive ? new List<int> { 7277 } : new List<int>() },
            Boosts = new BoostsSetup { AeCalendarServerBoosts = calendar },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };

        // Real calendar excerpt (artix.com/calendar list entry + event page text).
        var calendarPath = Path.Combine(tempDir, "ae_calendar_test.json");
        var cal = AeCalendar.ParseListPage(
            "{title: 'Double Gold Boost 11.20.26',url: 'http://www.artix.com/calendar/double-gold-boost-112026/' + '?d=11%2f20%2f2026+2%3a07%3a00+PM',start: '2026-11-20'}," +
            "{title: 'Members: Free Keys Available',url: 'http://www.artix.com/calendar/members-free-keys/' + '?d=10%2f1%2f2026+9%3a00%3a00+AM',start: '2026-10-01'}," +
            "{title: 'Members: Free Keys Available',url: 'http://www.artix.com/calendar/members-free-keys/' + '?d=11%2f1%2f2026+9%3a00%3a00+AM',start: '2026-11-01'}",
            DateTime.UtcNow);
        Assert(AeCalendar.ApplyDetailPage(cal.Events.Single(e => e.Title.StartsWith("Double Gold")),
                "<title>Double Gold Boost 11.20.26 on Artix Entertainment</title><h1>Double Gold Boost 11.20.26</h1> 72 hour DOUBLE Gold boost on all servers in AQWorlds!"),
            "The boost duration must be read from the event page text after the title");
        Assert(cal.Events.Count(e => e.Title.StartsWith("Members")) == 2, "A URL reused on non-consecutive days must be separate occurrences");
        var goldBoost = cal.Events.Single(e => e.Title.StartsWith("Double Gold"));
        Assert(goldBoost.Kind == AeCalendarKinds.ServerBoost && goldBoost.Gold && !goldBoost.Xp && goldBoost.DurationHours == 72 &&
               goldBoost.StartEastern == new DateTime(2026, 11, 20, 14, 7, 0), $"Double Gold must start 2:07 PM ET and last 72 h ({goldBoost.StartEastern}, {goldBoost.DurationHours} h)");
        Assert(cal.ServerBoostsAt(new DateTime(2026, 11, 21, 4, 57, 0, DateTimeKind.Utc)).Gold &&
               !cal.ServerBoostsAt(new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc)).Gold, "Server boosts must follow the Eastern-time window");
        cal.Save(calendarPath);

        var baseline = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(true, false, false), GameDataPath = gameDataPath, SeedOverride = 5 });
        var questOn = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = MakeScenario(false, true, true), GameDataPath = gameDataPath, CalendarPath = calendarPath, SeedOverride = 5 });
        string Logs(SimulationReport r) => string.Join(" | ", r.ScriptLogs.Select(l => l.Text));
        bool Has(SimulationReport r, string text) => r.ScriptLogs.Any(l => l.Text.Contains(text));

        Assert(baseline.Outcome == SimulationOutcome.Completed && questOn.Outcome == SimulationOutcome.Completed,
            $"Both runs must complete ({baseline.Outcome}: {baseline.StopReason}; {questOn.Outcome}: {questOn.StopReason})");
        Assert(Has(baseline, "WOLF gold+=90") && Has(baseline, "IMP gold+=60"),
            $"Kill gold must use the measured average (90) and the 3 x level rule (60): {Logs(baseline)}");
        Assert(baseline.SynthesizedData.Any(s => s.Key == "killgold:plain imp") && !baseline.SynthesizedData.Any(s => s.Key == "killgold:reward wolf"),
            "Only the rule-based reward may be flagged as synthesized");
        Assert(Has(baseline, "AFTER1500 alive=False") && Has(baseline, "AFTER3000 alive=True"),
            $"The spawn's measured 2.5 s respawn must be used: {Logs(baseline)}");
        Assert(baseline.FinalState.Factions.Any(f => f.Name == "Test Faction" && f.TotalRep == 50 + 1000),
            $"Kill rep (50) and the observed quest rep (1000) must go to the named faction: {string.Join(", ", baseline.FinalState.Factions.Select(f => $"{f.Name}={f.TotalRep}"))}");
        Assert(Has(baseline, "STAR=0") && Has(questOn, "STAR=1"), $"A quest map item needs its quest active: {Logs(baseline)} || {Logs(questOn)}");
        Assert(Has(baseline, "HOUSEQUEST accept=True") && Has(questOn, "HOUSEQUEST accept=False"),
            $"A house-item accept requirement must be satisfied by the house inventory: {Logs(baseline)} || {Logs(questOn)}");
        Assert(baseline.FinalState.Inventory.Any(i => i.Name == "Choice Gem"), "A captured quest's reward (RatePercent 100) must be given");
        Assert(Has(baseline, "DAILY1 accept=True complete=True") && Has(baseline, "DAILY2 accept=False") && Has(baseline, "DAILY3 accept=True") &&
               baseline.ServerResets == 1, $"The daily must reset at 00:00 server time ({baseline.ServerResets} reset(s)): {Logs(baseline)}");
        Assert(Has(questOn, "WOLF gold+=180") && questOn.CalendarBoosts.Any(b => b.Contains("Gold")) && baseline.CalendarBoosts.Count == 0,
            $"The calendar's Double Gold must double kill gold only when enabled: {Logs(questOn)}");
        // Class points per kill = ceil((230 + 25 x rank) x gap ratio): real values from a Shaman rank 1 -> 10 run in
        // icestormarena (gap = character level - monster level).
        int Cp(int rank, int gap) => (int)Math.Ceiling((230 + 25 * rank) * SimWorld.ClassPointsGapRatio(gap) - 1e-9);
        Assert(Cp(4, 10) == 330 && Cp(5, 5) == 355 && Cp(6, -155) == 380 &&     // full value up to 10 levels below
               Cp(3, 15) == 153 && Cp(6, 15) == 190 && Cp(5, 15) == 178 &&     // -10 %/level from 11 levels: 50 % at 15
               Cp(3, 25) == 16 && Cp(2, 50) == 14 && Cp(1, 95) == 13 && Cp(6, 35) == 19, // floor of 5 % (-95 % cap)
            "Class points must follow 230 + 25 x rank with the AQW level-gap penalty, rounded up");
        Assert(!baseline.SynthesizedData.Any(s => s.Key.StartsWith("classpoints:")), "Class points are a verified rule, never synthesized");

        // Boss mechanic: Escherion takes no damage while the Staff of Inversion lives; a fought monster reports state 2.
        var staff = new SimMonster { MapID = 2, Name = "Staff of Inversion", Cell = "Boss", MaxHP = 1000, HP = 1000 };
        var escherion = new SimMonster { MapID = 3, Name = "Escherion", Cell = "Boss", MaxHP = 6000, HP = 6000, Engaged = true };
        Assert(BossMechanics.ProtectorFor("escherion", escherion, new[] { staff, escherion }) == staff && escherion.State == 2,
            "Escherion must be protected by a living Staff of Inversion and report state 2 while fought");
        staff.HP = 0;
        Assert(BossMechanics.ProtectorFor("escherion", escherion, new[] { staff, escherion }) == null &&
               BossMechanics.ProtectorFor("otherMap", escherion, new[] { escherion }) == null, "No protection once the staff is dead or on other maps");

        // Complete equipment (default): empty Weapon/Helm/Cape slots get basic gear enhanced at the character's level;
        // when disabled only a weapon is ensured (AQW has no weaponless state).
        bool HasGear(SimulationReport r, string name) => r.FinalState.Inventory.Any(i => i.Name == name && i.Equipped);
        Assert(HasGear(baseline, "Simulated Weapon") && HasGear(baseline, "Simulated Helm") && HasGear(baseline, "Simulated Cape") &&
               !HasGear(baseline, "Simulated Pet") && baseline.SynthesizedData.Any(s => s.Key == "gear:complete"),
            $"Empty slots must be completed and flagged: {string.Join(", ", baseline.FinalState.Inventory.Where(i => i.Equipped).Select(i => i.Name))}");
        var bareScenario = MakeScenario(true, false, false);
        bareScenario.Player.CompleteEquipment = false;
        var bare = runner.Run(new SimulationRunOptions { ScriptPath = script, Scenario = bareScenario, GameDataPath = gameDataPath, SeedOverride = 5 });
        Assert(HasGear(bare, "Simulated Weapon") && !HasGear(bare, "Simulated Helm") && !HasGear(bare, "Simulated Cape"),
            "With completeEquipment=false only a weapon is ensured");
        Console.WriteLine($"   OK: gold 90/60 (measured/rule), respawn 2.5 s, rep 1050, quest map item gated, house item accepted, daily reset at 00:00 ET, calendar 2x gold, CP rule");
    }

    internal const string FingerprintArg = "--determinism-fingerprint";

    /// <summary>
    /// Runs the same scenario in two separate processes and compares the results. Same-process reruns cannot catch
    /// per-process randomness such as string.GetHashCode(), which used to leak into monster and faction IDs.
    /// </summary>
    private static void TestCrossProcessDeterminism(string tempDir)
    {
        Console.WriteLine("13. Testing determinism across separate processes (same seed => identical IDs and outcome)...");
        var workDir = Path.Combine(tempDir, "fingerprint");
        Directory.CreateDirectory(workDir);

        var first = RunFingerprintChild(workDir);
        var second = RunFingerprintChild(workDir);
        Assert(first == second, $"Cross-process results differ:\n  run 1: {first}\n  run 2: {second}");
        Console.WriteLine($"   OK: Identical in 2 processes ({first})");
    }

    private static string RunFingerprintChild(string workDir)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the test executable.");
        var psi = new ProcessStartInfo(processPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Under "dotnet SkuaSim.Core.Tests.dll" the host is dotnet.exe, so pass the test assembly first.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(CoreTests).Assembly.Location);
        psi.ArgumentList.Add(FingerprintArg);
        psi.ArgumentList.Add(workDir);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the fingerprint child process.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(180_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception("Fingerprint child process timed out.");
        }
        var line = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("FINGERPRINT "));
        Assert(process.ExitCode == 0 && line != null, $"Fingerprint child failed (exit {process.ExitCode}): {stdout}\n{stderrTask.Result}");
        return line!["FINGERPRINT ".Length..];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int PrintDeterminismFingerprint(SkuaInstallation installation, string workDir)
    {
        var script = Path.Combine(workDir, "FingerprintBot.cs");
        File.WriteAllText(script, """
        using Skua.Core.Interfaces;

        public class FingerprintBot
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Map.Join("zzfpmap");
                for (var i = 0; i < 3; i++)
                    bot.Hunt.Monster("Fp Slime");
                bot.Sleep(5_000);
            }
        }
        """);

        var scenario = new Scenario
        {
            Name = "Cross-process fingerprint",
            Player = new PlayerSetup { Username = "Tester", Level = 50, MaxHealth = 5_000, DamagePerSecond = 600 },
            // No Id on the monster or the faction: both IDs are derived from their names.
            Factions = new List<FactionSetup> { new() { Name = "Fp Faction", Rank = 3 } },
            Maps = new Dictionary<string, MapSetup>(StringComparer.OrdinalIgnoreCase)
            {
                ["zzfpmap"] = new MapSetup
                {
                    Cells = new List<string> { "Enter", "r1" },
                    Monsters = new List<MonsterSetup> { new() { Name = "Fp Slime", Cell = "r1", Count = 2, MaxHP = 2_000, Level = 20 } }
                }
            },
            Limits = new LimitsSetup { MaxVirtualMinutes = 30 }
        };

        var report = new SimulationRunner(installation).Run(new SimulationRunOptions { ScriptPath = script, Scenario = scenario, SeedOverride = 99 });
        var fs = report.FinalState;
        Console.WriteLine("FINGERPRINT " + string.Join(" ",
            $"outcome={report.Outcome}",
            $"virtualMs={(long)report.VirtualDuration.TotalMilliseconds}",
            $"kills={report.Kills}",
            $"gold={fs.Gold}",
            $"hp={fs.Health}",
            $"monIds={string.Join(",", fs.MapMonsters.Select(m => $"{m.MapId}:{m.MonId}:{m.Hp}"))}",
            $"factions={string.Join(",", fs.Factions.Select(f => $"{f.Id}:{f.TotalRep}"))}"));
        return report.Outcome == SimulationOutcome.Completed ? 0 : 1;
    }

    private static bool SpinWaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
                return true;
            Thread.Sleep(20);
        }
        return predicate();
    }

    /// <summary>
    /// Phase 3.5 items 16 and the tester-data flow: class combat is blended only from compatible profiles (level band,
    /// patterns, rank group), and other testers' shared exports in data\contrib\ are imported (newest per contributor,
    /// own export and ID-less files rejected) and merged in memory when the catalog loads.
    /// </summary>
    private static void TestClassProfilesAndSharedData(string tempDir)
    {
        Console.WriteLine("18. Testing class combat profiles (level band / gear) and shared tester data (import + merge)...");

        // --- Combat profile matching ---
        var cls = new SkuaSim.Collector.Models.CapturedClassProfile { Name = "Void Highlord" };
        cls.CombatByProfile["L91-100|R10|W9:100|C9:100|H9:100|K9:100"] = new() { Hits = 80, Crits = 20, AttacksTaken = 50, DodgesTaken = 10 };
        cls.CombatByProfile["L91-100|R10|W9:95|C9:100|H9:100|K9:100"] = new() { Hits = 10, Crits = 10 };
        cls.CombatByProfile["L21-30|R5|W6:25|C6:25|H6:25|K6:25"] = new() { Hits = 30, Crits = 1 };
        var exact = ClassCombatProfileMatch.Find(cls, 100, 10, "W9:100|C9:100|H9:100|K9:100");
        Assert(exact != null && exact.Stats.Crits == 20 && exact.Description.EndsWith("(exact)"), $"Exact profile expected, got {exact?.Description}");
        var samePatterns = ClassCombatProfileMatch.Find(cls, 95, 10, "W9:90|C9:90|H9:90|K9:90");
        Assert(samePatterns != null && samePatterns.Stats.Crits == 30 && samePatterns.Stats.Hits == 90,
            $"Same band + patterns should add both endgame profiles, got {samePatterns?.Description}");
        Assert(ClassCombatProfileMatch.Find(cls, 30, 10, "W9:30|C9:30|H9:30|K9:30") == null,
            "An endgame profile must not be blended into a level-30 character");
        Assert(ClassCombatProfileMatch.Find(cls, 100, 5, "W9:100|C9:100|H9:100|K9:100") == null,
            "Rank 5 is not compatible with rank 10 profiles (rank 10 passive)");
        Assert(ClassCombatProfileMatch.Find(cls, 25, 5, "W6:25|C6:25|H6:25|K6:25") is { } low && low.Stats.Hits == 30, "Low-level profile expected");

        // --- Shared tester data ---
        var dataDir = Path.Combine(tempDir, "shared_data");
        Directory.CreateDirectory(dataDir);
        var gameDataPath = Path.Combine(dataDir, "gamedata.json");
        SkuaSim.Collector.Models.GameDatabase MakeDb(long kills, string? contributor, DateTime? exported)
        {
            var db = new SkuaSim.Collector.Models.GameDatabase { ContributorId = contributor, ExportedUtc = exported };
            db.Maps["sharedmap"] = new SkuaSim.Collector.Models.CapturedMap { Name = "sharedmap", Visits = 1 };
            db.Monsters["id:777001"] = new SkuaSim.Collector.Models.CapturedMonster { Key = "id:777001", ID = 777001, Name = "Shared Slime", KillCount = kills, Level = 5 };
            return db;
        }
        void Save(SkuaSim.Collector.Models.GameDatabase db, string path) => SkuaSim.Collector.Storage.GameDatabaseStore.WriteFile(path, db);

        Save(MakeDb(10, null, null), gameDataPath);
        var testerA = Path.Combine(tempDir, "testerA_old.json");
        var testerANew = Path.Combine(tempDir, "testerA_new.json");
        var testerB = Path.Combine(tempDir, "testerB.json");
        var own = Path.Combine(tempDir, "own.json");
        var noId = Path.Combine(tempDir, "noid.json");
        Save(MakeDb(5, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new DateTime(2026, 11, 20)), testerA);
        Save(MakeDb(7, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new DateTime(2026, 11, 22)), testerANew);
        Save(MakeDb(100, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", new DateTime(2026, 11, 21)), testerB);
        Save(MakeDb(1, "cccccccccccccccccccccccccccccccc", new DateTime(2026, 11, 21)), own);
        Save(MakeDb(1, null, null), noId);

        const string ownId = "cccccccccccccccccccccccccccccccc";
        Assert(GameDataContributions.Import(testerA, gameDataPath, ownId).StoredAs != null, "Tester A import failed");
        Assert(GameDataContributions.Import(testerANew, gameDataPath, ownId).StoredAs != null, "Tester A's newer export must replace the older one");
        Assert(GameDataContributions.Import(testerA, gameDataPath, ownId).StoredAs == null, "An older export must not replace a newer one");
        Assert(GameDataContributions.Import(testerB, gameDataPath, ownId).StoredAs != null, "Tester B import failed");
        Assert(GameDataContributions.Import(own, gameDataPath, ownId).StoredAs == null, "This PC's own export must be rejected");
        Assert(GameDataContributions.Import(noId, gameDataPath, ownId).StoredAs == null, "A file without contributor ID must be rejected");

        var catalog = GameDataCatalog.Load(gameDataPath, null);
        Assert(catalog.Contributions.Count == 2 && catalog.Contributions.All(c => c.Status == "merged"),
            $"Expected 2 merged contributions, got {string.Join(", ", catalog.Contributions.Select(c => c.FileName + ":" + c.Status))}");
        Assert(catalog.Captured.Monsters["id:777001"].KillCount == 10 + 7 + 100, $"Kills must add up (10 own + 7 A + 100 B), got {catalog.Captured.Monsters["id:777001"].KillCount}");
        Assert(GameDataCatalog.Load(gameDataPath, null, includeContributions: false).Captured.Monsters["id:777001"].KillCount == 10,
            "Without contributions only the own data is loaded");
        var ownFile = SkuaSim.Collector.Storage.GameDatabaseStore.ReadSharedFile(gameDataPath);
        Assert(ownFile.Monsters["id:777001"].KillCount == 10, "gamedata.json itself must never change");

        // --- AQW Wiki enhancement tables (AqwEnhancementTable) ---
        bool Near(int actual, int expected, int tol = 1) => Math.Abs(actual - expected) <= tol;
        var wizArmor = AqwEnhancementTable.StatsFor(6, 100, EnhancementSlot.Armor);
        Assert(wizArmor is { Int: 75, Wis: 30, End: 15, Lck: 30, Str: 0, Dex: 0 }, $"Wizard armor L100 must be 75/30/15/30 (wiki), got {wizArmor}");
        var wizWeapon = AqwEnhancementTable.StatsFor(6, 100, EnhancementSlot.Weapon);
        Assert(wizWeapon is { Int: 100, Wis: 40, End: 20, Lck: 40 }, $"Wizard weapon L100 must be 100/40/20/40 (Wizard Awe), got {wizWeapon}");
        var fighter60 = AqwEnhancementTable.StatsFor(2, 60, EnhancementSlot.Armor);
        Assert(Near(fighter60.Str, 40) && Near(fighter60.Dex, 12) && Near(fighter60.End, 39), $"Fighter armor L60 ~40/12/39 (wiki), got {fighter60}");
        var wizCape51 = AqwEnhancementTable.StatsFor(6, 51, EnhancementSlot.Cape);
        Assert(Near(wizCape51.Total, 60, 3), $"Wizard cape L51 ~60 points (wiki 30/12/6/12), got {wizCape51.Total}");
        var vim = AqwEnhancementTable.StatsFor(25, 100, EnhancementSlot.Helm);
        Assert(Near(vim.Dex, 198, 3) && Near(vim.End, -137, 3) && Near(vim.Lck, 76, 2), $"Vim helm L100 ~15/198/-137/76 (wiki), got {vim}");
        var forgeCape = AqwEnhancementTable.StatsFor(30, 100, EnhancementSlot.Cape);
        Assert(Near(forgeCape.Str, 30) && Near(forgeCape.Int, 30) && Near(forgeCape.Lck, 61), $"Lament cape L100 ~30/30/61 (wiki), got {forgeCape}");
        var dauntless = AqwEnhancementTable.StatsFor(10, 100, EnhancementSlot.Weapon, procId: 14);
        Assert(dauntless is { Str: 88, Dex: 26, End: 86 }, $"Dauntless weapon L100 must be 88/26/86 (wiki), got {dauntless}");
        Assert(AqwEnhancementTable.StatsFor(6, 1, EnhancementSlot.Weapon) is { Int: 3, Wis: 1, End: 1, Lck: 1 }, "Wizard Awe L1 must be 3/1/1/1");
        Console.WriteLine("   OK: compatible-only combat blending, newest export per contributor, merged in memory, wiki enhancement tables");
    }
}
