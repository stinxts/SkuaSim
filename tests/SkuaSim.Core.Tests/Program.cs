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
                while (!Bot.ShouldExit && Bot.Quests.Completed.Count < 1 && Bot.Player.Gold < 500100)
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
                        new() { Name = "Frogzard", Cell = "r1", MaxHP = 60, Level = 6 }
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
                    bot.Sleep(13_000); // single spawn: wait for the 12 s respawn
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
}
