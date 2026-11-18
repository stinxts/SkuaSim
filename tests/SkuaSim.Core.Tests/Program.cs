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
        Console.WriteLine("1. Testing end-to-end farm + quest + shop script execution to Completion...");
        var tempDir = Path.Combine(Path.GetTempPath(), $"skuasim_core_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
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

            var runner = new SimulationRunner(installation);
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

            Console.WriteLine("3. All SkuaSim.Core tests passed successfully!");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
