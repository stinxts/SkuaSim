using System.Diagnostics;
using System.Runtime.CompilerServices;
using SkuaSim.Core.Compilation;
using SkuaSim.Core.Hosting;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.Scenarios;

var installation = SkuaInstallation.Detect();
SkuaAssemblyResolver.Register(installation.ProbeDirectories);
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
        Console.WriteLine($"   OK: Discovered {inspection.Options.Count} option(s), storage='{inspection.OptionsStorage}', and compile diagnostics work cleanly");
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
