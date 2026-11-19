using Skua.Core.Interfaces;
using Skua.Core.Models;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public abstract class ApiBase
{
    protected ApiBase(SimContext ctx) => Ctx = ctx;

    protected SimContext Ctx { get; }
    protected SimWorld World => Ctx.World;
    protected IScriptInterface Bot => (IScriptInterface)Ctx.Bot!;
    protected void Sleep(int ms) => Ctx.Clock.Sleep(ms);
}

/// <summary>
/// Root of the simulated API (IScriptInterface). Sub-APIs without a handler are created on demand by the proxy hub.
/// </summary>
public sealed class BotApi : ApiBase
{
    public BotApi(SimContext ctx) : base(ctx)
    {
        Random = new Random(ctx.Seed);
        Version = ctx.SkuaFlavor == "VibeSkua" ? new Version(1, 8, 5, 0) : new Version(1, 4, 4, 4);
    }

    public IFlashUtil Flash { get; set; } = null!;
    public IScriptStatus Manager { get; set; } = null!;
    public IScriptBotStats Stats { get; set; } = null!;
    public IScriptSelfAuras Self { get; set; } = null!;
    public IScriptTargetAuras Target { get; set; } = null!;
    public IUltraBossHelper UltraBossHelper { get; set; } = null!;
    public IScriptCombat Combat { get; set; } = null!;
    public IScriptKill Kill { get; set; } = null!;
    public IScriptHunt Hunt { get; set; } = null!;
    public IScriptDrop Drops { get; set; } = null!;
    public IScriptEvent Events { get; set; } = null!;
    public IScriptFaction Reputation { get; set; } = null!;
    public IScriptHouseInv House { get; set; } = null!;
    public IScriptInventory Inventory { get; set; } = null!;
    public IScriptTempInv TempInv { get; set; } = null!;
    public IScriptBank Bank { get; set; } = null!;
    public IScriptInventoryHelper InvHelper { get; set; } = null!;
    public IScriptLite Lite { get; set; } = null!;
    public IScriptOption Options { get; set; } = null!;
    public IScriptMap Map { get; set; } = null!;
    public IScriptMonster Monsters { get; set; } = null!;
    public IScriptPlayer Player { get; set; } = null!;
    public IScriptQuest Quests { get; set; } = null!;
    public IScriptSend Send { get; set; } = null!;
    public IScriptShop Shops { get; set; } = null!;
    public IScriptSkill Skills { get; set; } = null!;
    public IScriptWait Wait { get; set; } = null!;
    public IScriptServers Servers { get; set; } = null!;
    public IScriptHandlers Handlers { get; set; } = null!;
    public IScriptAccounts Accounts { get; set; } = null!;
    public IScriptBoost Boosts { get; set; } = null!;
    public IScriptOptionContainer Config { get; set; } = null!;

    public bool ShouldExit => Ctx.StopRequested;
    public Version Version { get; }
    public Random Random { get; set; }

    public void SellJunk() => Ctx.Trace.Info("SellJunk() called (no junk configured in the simulator).");

    public new void Sleep(int ms) => Ctx.Clock.Sleep(ms);

    public Task SleepAsync(int ms) => Ctx.Clock.Delay(ms, CancellationToken.None);

    public void Stop(bool runScriptStoppingEvent)
    {
        Ctx.RequestStop("the script called Bot.Stop()");
        Ctx.ThrowIfStopping();
    }

    public Task StopAsync(bool runScriptStoppingEvent)
    {
        Stop(runScriptStoppingEvent);
        return Task.CompletedTask;
    }

    public void Log(string message) => Ctx.Trace.Log(message ?? "");

    public Task Schedule(int delay, Action<IScriptInterface> action)
    {
        var tcs = new TaskCompletionSource();
        Ctx.Clock.Scheduler.Schedule(Ctx.Clock.NowMs + Math.Max(0, delay), "Bot.Schedule", () =>
        {
            try
            {
                action(Bot);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public Task Schedule(int delay, Func<IScriptInterface, Task> function)
    {
        var tcs = new TaskCompletionSource();
        Ctx.Clock.Scheduler.Schedule(Ctx.Clock.NowMs + Math.Max(0, delay), "Bot.Schedule", () =>
        {
            try
            {
                function(Bot).GetAwaiter().GetResult();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        var answer = !yesAndNo || string.Equals(Ctx.Scenario.World.MessageBoxAnswer, "yes", StringComparison.OrdinalIgnoreCase);
        Ctx.Trace.World($"MessageBox [{caption}] {Compact(message)} -> {(yesAndNo ? (answer ? "Yes" : "No") : "OK")}");
        return answer;
    }

    public DialogResult ShowMessageBox(string message, string caption, string[] buttons)
    {
        var index = 0;
        if (buttons is { Length: > 1 })
        {
            var wanted = Ctx.Scenario.World.MessageBoxAnswer;
            var match = Array.FindIndex(buttons, b => string.Equals(b, wanted, StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
                index = match;
        }
        var text = buttons is { Length: > 0 } ? buttons[index] : "OK";
        Ctx.Trace.World($"MessageBox [{caption}] {Compact(message)} -> '{text}'");
        return new DialogResult(text, index);
    }

    private static string Compact(string? message)
    {
        var m = (message ?? "").Replace("\r", " ").Replace("\n", " ");
        return m.Length > 200 ? m[..197] + "..." : m;
    }
}
