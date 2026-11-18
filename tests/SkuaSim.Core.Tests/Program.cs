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
