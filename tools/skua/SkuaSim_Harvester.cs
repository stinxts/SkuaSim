/*
name: SkuaSim Harvester
description: Visits the maps, shops and quests listed in a SkuaSim harvest plan so the SkuaSim Collector plugin records them (cells, monsters, official drop lists, shops, quests). Run it on every army client at the same time: they share one progress file and split the work. It only fights to open rooms that must be cleared in order (option) and in drop-sampling mode; it never accepts or completes quests and never buys anything. Your character name is never written.
tags: skuasim, collector, data, harvest, army, tool
*/
//cs_include Scripts/CoreBots.cs
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Options;

/// <summary>
/// SkuaSim harvester, layer A (visits). Reads the plan written by <c>skuasim harvest-plan</c> and works through its stops:
/// join the map, walk its cells, request each monster's official drop list (the same request the Weekly/Merge generator
/// scripts send), load the listed shops and quests. The Collector plugin stays passive and records the answers.
/// Every client claims stops from one shared progress file (harvest_progress.json next to the plan), so any number of
/// clients split the plan, and stopping and starting again resumes where it was.
/// </summary>
public class SkuaSimHarvester
{
    public const string Version = "1";

    // Fights use CoreBots exactly like the other scripts: the account's CBO Solo class, its AdvancedSkills.json set and mode.
    private CoreBots Core => CoreBots.Instance;

    public string OptionsStorage = "SkuaSimHarvester";
    // Same pattern as CoreBots: Skua does not open the options window by itself; the script opens it unless "SkipOption" is on.
    public bool DontPreconfigure = true;
    public List<IOption> Options = new()
    {
        new Option<bool>("SkipOption", "Skip this window next time",
            "You will be able to return to this screen via [Scripts] -> [Edit Script Options] if you wish to change anything.", false),
        new Option<string>("PlanFile", "Plan file",
            "harvest_plan.json made by 'skuasim harvest-plan' (empty = %LOCALAPPDATA%\\SkuaSim Harvest\\harvest_plan.json, a folder cloud sync never touches).", ""),
        new Option<int>("MaxStops", "Stops per client", "How many stops this client does before it stops (0 = until the plan is done).", 0),
        new Option<bool>("PrivateRooms", "Private rooms",
            "Join private rooms (map-<number>). Off = public rooms, which also records how full rooms get; a full or failed public join retries in a private room.", false),
        new Option<int>("CellDwellMs", "Time per cell (ms)", "How long to stay in each cell so the monsters load.", 1000),
        new Option<int>("RequestGapMs", "Time between requests (ms)",
            "Pause after each drop-list, shop or quest request, to stay well below AQW's packet spam limit.", 1000),
        new Option<int>("MaxCellsPerMap", "Max cells per map", "Cells visited per map (cutscene and blank cells are skipped).", 40),
        new Option<bool>("ClearLockedRooms", "Clear rooms to move on",
            "When a map refuses a jump ahead (doomvault: \"No skipping ahead\"), kill the monsters of the current room and try again, with the equipped class. Gives up when they are too strong.", true),
        new Option<bool>("SampleDrops", "Sample drop rates (layer B)",
            "Instead of visiting, farm the plan's sample stops: each client kills one monster alone in a private room so the Collector measures its drop rates. Uses the equipped class.", false),
        new Option<int>("KillsPerStop", "Kills per sample stop",
            "Kills per claimed sample stop; the next plan asks for more where the rate is still not measured.", 100),
        new Option<bool>("PickUpDrops", "Pick up drops while sampling",
            "Off = reject drops (the Collector still counts them), so items never reach their max stack.", false),
    };

    private const string ProgressFileName = "harvest_progress.json";
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(20);

    private IScriptInterface Bot = null!;
    private string _progressPath = "";
    // Unique per running script, not per process: VibeSkua 2 runs every client's script inside one process (all clients
    // shared "client-<pid>" and claimed the same stop). Never derived from the character name.
    private string _client = "";
    private readonly Random _rooms = new();
    private Mutex? _lock;

    public void ScriptMain(IScriptInterface bot)
    {
        Bot = bot;
        _client = $"client-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}-{new Random().Next(100_000, 999_999)}";
        if (!Bot.Player.LoggedIn)
        {
            Log("Log in first: the character must be in game.");
            return;
        }
        if (Bot.Config != null && !Bot.Config.Get<bool>("SkipOption"))
            Bot.Config.Configure();

        var planPath =(Bot.Config!.Get<string>("PlanFile") ?? "").Trim();
        if (planPath.Length == 0)
            planPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkuaSim Harvest", "harvest_plan.json");
        if (!File.Exists(planPath))
        {
            Log($"No plan at {planPath}. Make one with: skuasim harvest-plan");
            return;
        }
        var sampling = Bot.Config.Get<bool>("SampleDrops");
        var kind = sampling ? "sample" : "visit";
        var plan = JObject.Parse(File.ReadAllText(planPath));
        var stops = (plan["stops"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(s => ((string?)s["kind"] ?? "visit") == kind).ToList();
        _progressPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, ProgressFileName);
        _lock = new Mutex(false, @"Local\SkuaSimHarvester");

        var maxStops = Bot.Config.Get<int>("MaxStops");
        Log($"v{Version}: {(sampling ? "drop sampling" : "visits")}, {stops.Count} stop(s) in the plan, progress in {_progressPath}.");
        Core.SetOptions(); // loads the account's CBO (classes, use modes) and the usual CoreBots client options (skip cutscenes...)
        var done = 0;
        try
        {
            while (!Bot.ShouldExit && (maxStops <= 0 || done < maxStops))
            {
                JObject? stop;
                try { stop = Claim(stops); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Progress file busy ({ex.Message}); trying again in 5 s.");
                    Bot.Sleep(5000);
                    continue;
                }
                if (stop == null)
                {
                    Log("Nothing left to claim: every stop of this plan is done, failed or being worked on by another client. " +
                        "This is the end of the plan, not an error: run HarvestPlan.bat for the next round (sample stops that still " +
                        "need kills come back with new keys; maps visited since then add new sample targets), then start again.");
                    break;
                }
                var key = (string)stop["key"]!;
                try
                {
                    var (status, note) = sampling ? Sample(stop) : Visit(stop);
                    Finish(key, status, note);
                    Log($"{key}: {status} — {note}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not ThreadInterruptedException)
                {
                    Log($"{key}: failed — {ex.GetType().Name}: {ex.Message}");
                    try { Finish(key, "failed", "error: " + ex.Message); }
                    catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException)
                    {
                        Log($"Could not write the progress file ({ex2.Message}); this stop stays claimed and is retried after 20 min.");
                    }
                }
                done++;
            }
            Release();
            var summary = Read(p => p.Properties().GroupBy(x => (string?)x.Value["status"] ?? "?")
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}").ToList());
            Log($"This client did {done} stop(s). Plan: {stops.Count} stop(s); {string.Join(", ", summary)}.");
        }
        finally
        {
            try { Release(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"Could not release claims: {ex.Message}"); }
            _lock.Dispose();
            _lock = null;
            Core.SetOptions(false);
        }
    }

    private (string Status, string Note) Visit(JObject stop)
    {
        var notes = new List<string>();
        var map = (string?)stop["map"];
        if (!string.IsNullOrEmpty(map))
        {
            if (!JoinMap(map))
                return ("failed", $"could not join {map} (member-only, locked, seasonal or story-gated?)");
            notes.Add(VisitCells());
        }

        var shops = (stop["shops"] as JArray ?? new JArray()).Select(t => (int)t).ToList();
        if (shops.Count > 0)
        {
            var failed = new List<int>();
            foreach (var id in shops)
            {
                if (Bot.ShouldExit)
                    break;
                Bot.Shops.Load(id);
                if (!Bot.Wait.ForTrue(() => Bot.Shops.IsLoaded && Bot.Shops.ID == id, 20))
                    failed.Add(id);
                Gap();
            }
            notes.Add($"{shops.Count - failed.Count}/{shops.Count} shop(s) loaded" + (failed.Count > 0 ? $" (not: {string.Join(", ", failed)})" : ""));
        }

        var quests = (stop["quests"] as JArray ?? new JArray()).Select(t => (int)t).ToList();
        if (quests.Count > 0 && !Bot.ShouldExit)
        {
            Bot.Quests.Load(quests.ToArray());
            Bot.Wait.ForTrue(() => quests.All(id => Bot.Quests.Tree.Any(q => q.ID == id)), 20);
            var loaded = quests.Count(id => Bot.Quests.Tree.Any(q => q.ID == id));
            notes.Add($"{loaded}/{quests.Count} quest(s) loaded");
            Gap();
        }

        if (Bot.ShouldExit)
            return ("stopped", "the script was stopped during this stop");
        return ("done", string.Join("; ", notes));
    }

    /// <summary>
    /// Layer B: kills the stop's monster alone, in a private room, so every kill is this client's own (drops are shared
    /// per kill, so clients in one room would record one kill N times). Gives up when the monster is too strong.
    /// </summary>
    private (string Status, string Note) Sample(JObject stop)
    {
        var map = (string)stop["map"]!;
        var cell = (string?)stop["cell"] ?? "Enter";
        var monster = (string)stop["monster"]!;
        var items = (stop["items"] as JArray ?? new JArray()).Select(t => (string)t!).ToList();
        var target = Math.Min(Math.Max(1, (int?)stop["kills"] ?? 100), Math.Max(1, Bot.Config!.Get<int>("KillsPerStop")));

        var maxed = items.Where(name => Bot.Inventory.Items.Any(i =>
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) && i.MaxStack > 0 && i.Quantity >= i.MaxStack)).ToList();
        if (maxed.Count == items.Count)
            return ("skipped", $"every sampled item is at max stack in the inventory ({string.Join(", ", maxed)}); sample it on another account");

        if (!JoinMap(map, forcePrivate: true))
            return ("failed", $"could not join {map}");
        Bot.Map.Jump(cell, "Left");
        Bot.Wait.ForCellChange(cell);
        if (!string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
            return ("failed", $"could not reach {map}/{cell} (the map may need its rooms cleared in order)");

        var rejectBefore = Bot.Options.RejectAllDrops;
        Bot.Options.RejectAllDrops = !Bot.Config.Get<bool>("PickUpDrops");
        StartClassSkills();
        int kills = 0, deaths = 0, noSpawn = 0;
        string? gaveUp = null;
        try
        {
            while (kills < target && !Bot.ShouldExit)
            {
                if (!Bot.Player.Alive)
                {
                    if (++deaths >= 5) { gaveUp = $"too strong: died {deaths} times (level {Bot.Player.Level})"; break; }
                    if (!RespawnAndResume(cell, "Left")) { gaveUp = "died and could not get back to the cell"; break; }
                }
                if (!string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
                {
                    Bot.Map.Jump(cell, "Left");
                    Bot.Wait.ForCellChange(cell);
                }
                if (!Bot.Skills.TimerRunning)
                    StartClassSkills();
                if (!Bot.Monsters.CurrentAvailableMonsters.Any(m => string.Equals(m.Name, monster, StringComparison.OrdinalIgnoreCase)) &&
                    !Bot.Wait.ForMonsterSpawn(monster, 30))
                {
                    if (++noSpawn >= 3) { gaveUp = "the monster did not spawn in its cell"; break; }
                    continue;
                }
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                Bot.Kill.Monster(monster, limit.Token);
                if (limit.IsCancellationRequested && Bot.Player.Alive)
                {
                    gaveUp = $"too strong: one kill took over 2 minutes (level {Bot.Player.Level})";
                    break;
                }
                if (Bot.Player.Alive)
                    kills++;
            }
        }
        finally
        {
            Bot.Skills.Stop();
            Bot.Options.RejectAllDrops = rejectBefore;
        }

        var note = $"{kills}/{target} kill(s) of {monster} in {map}/{cell}, {deaths} death(s); items: {string.Join(", ", items)}" +
                   (maxed.Count > 0 ? $" (at max stack, not sampled here: {string.Join(", ", maxed)})" : "");
        if (gaveUp != null)
            return ("failed", gaveUp + "; " + note);
        if (Bot.ShouldExit)
            return ("stopped", note);
        return ("done", note);
    }

    private bool JoinMap(string map, bool forcePrivate = false)
    {
        if (string.Equals(Bot.Map.Name, map, StringComparison.OrdinalIgnoreCase) && Bot.Map.Loaded &&
            (!forcePrivate || Bot.Map.PlayerCount <= 1))
            return true;
        var privateFirst = forcePrivate || Bot.Config!.Get<bool>("PrivateRooms");
        for (var attempt = 0; attempt < 2 && !Bot.ShouldExit; attempt++)
        {
            LeaveCombat();
            var room = privateFirst || attempt > 0 ? $"{map}-{_rooms.Next(10_000, 99_999)}" : map;
            Bot.Map.Join(room, "Enter", "Spawn");
            Bot.Wait.ForMapLoad(map, 20);
            if (string.Equals(Bot.Map.Name, map, StringComparison.OrdinalIgnoreCase) && Bot.Map.Loaded)
                return true;
            Bot.Sleep(3000);
        }
        return false;
    }

    /// <summary>Jumps through the map's cells and asks for the official drop list of each monster type met.</summary>
    private string VisitCells()
    {
        var dwell = Math.Max(300, Bot.Config!.Get<int>("CellDwellMs"));
        var maxCells = Math.Max(1, Bot.Config.Get<int>("MaxCellsPerMap"));
        var cells = Bot.Map.Cells.Where(c => !SkipCell(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCells).ToList();
        var asked = new HashSet<int>();
        var visited = 0;
        var refusedInARow = 0;
        int deaths = 0, roomsCleared = 0;
        var clearRooms = Bot.Config.Get<bool>("ClearLockedRooms");
        string? stoppedEarly = null;
        foreach (var cell in cells)
        {
            if (Bot.ShouldExit)
                break;
            Bot.Map.Jump(cell, "Spawn");
            Bot.Sleep(dwell);
            RecoverIfDead();
            if (!string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase) && clearRooms &&
                Bot.Monsters.CurrentAvailableMonsters.Count > 0)
            {
                // Maps like doomvault refuse jumps ahead ("No skipping ahead..."): clear the room we are in, then try again.
                var gaveUp = ClearCurrentCell(ref deaths);
                roomsCleared++;
                if (gaveUp != null)
                {
                    stoppedEarly = $"; stopped clearing rooms: {gaveUp}";
                    break;
                }
                Bot.Map.Jump(cell, "Spawn");
                Bot.Sleep(dwell);
            }
            if (!string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
            {
                if (++refusedInARow >= 3)
                {
                    stoppedEarly = "; stopped: the map refused 3 jumps in a row (rooms must be cleared in order?)";
                    break;
                }
                continue;
            }
            refusedInARow = 0;
            visited++;
            foreach (var mon in Bot.Monsters.CurrentMonsters)
            {
                if (Bot.ShouldExit || mon.MapID <= 0 || !asked.Add(mon.ID))
                    continue;
                // The request the Weekly/Merge generators send; the answer is cell-scoped, so it is asked from the monster's cell.
                Bot.Send.Packet($"%xt%zm%getMonsterDrops%{Bot.Map.RoomID}%{mon.MapID}%");
                Gap();
            }
        }
        return $"{visited}/{cells.Count} cell(s), {asked.Count} drop list(s) requested" +
               (roomsCleared > 0 ? $", {roomsCleared} room(s) cleared to move on, {deaths} death(s)" : "") + stoppedEarly;
    }

    /// <summary>
    /// Kills every monster in the current cell with the equipped class (for maps whose next room opens only when this
    /// one is cleared). Returns why it gave up (too strong), or null.
    /// </summary>
    private string? ClearCurrentCell(ref int deaths)
    {
        var cell = Bot.Player.Cell;
        StartClassSkills();
        try
        {
            for (var kills = 0; kills < 40 && !Bot.ShouldExit; kills++)
            {
                var target = Bot.Monsters.CurrentAvailableMonsters.FirstOrDefault();
                if (target == null)
                    return null;
                if (!Bot.Skills.TimerRunning)
                    StartClassSkills();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                Bot.Kill.Monster(target.Name, limit.Token);
                if (!Bot.Player.Alive)
                {
                    if (++deaths >= 5)
                        return $"too strong: died {deaths} times (level {Bot.Player.Level})";
                    if (!RespawnAndResume(cell, "Spawn"))
                        return "died and could not get back to the room";
                }
                else if (limit.IsCancellationRequested)
                    return $"too strong: one kill took over 2 minutes (level {Bot.Player.Level})";
            }
            return null;
        }
        finally
        {
            Bot.Skills.Stop();
        }
    }

    private static bool SkipCell(string cell) =>
        string.IsNullOrWhiteSpace(cell) || cell.StartsWith("Cut", StringComparison.OrdinalIgnoreCase) ||
        cell.Equals("Blank", StringComparison.OrdinalIgnoreCase) || cell.Equals("Wait", StringComparison.OrdinalIgnoreCase);

    private string? _skillSetLogged;

    /// <summary>
    /// Equips the account's CBO Solo class and starts its AdvancedSkills.json set in the configured use mode, through
    /// CoreBots.EquipClass like every other script (with a generic CBO slot, CoreBots starts the current class's set).
    /// </summary>
    private void StartClassSkills()
    {
        Core.EquipClass(ClassType.Solo);
        if (!Bot.Skills.TimerRunning)
            Bot.Skills.StartAdvanced(Bot.Player.CurrentClass?.Name ?? "generic", false);
        var used = $"{Bot.Player.CurrentClass?.Name ?? "?"} (CoreBots Solo class, AdvancedSkills.json)";
        if (used != _skillSetLogged)
        {
            _skillSetLogged = used;
            Log("Skills: " + used);
        }
    }

    private void LeaveCombat()
    {
        if (!Bot.Player.InCombat)
            return;
        Bot.Map.Jump(Bot.Player.Cell, Bot.Player.Pad);
        Bot.Wait.ForCombatExit(10);
    }

    private void RecoverIfDead()
    {
        if (Bot.Player.Alive)
            return;
        Log("Died while visiting; waiting to respawn.");
        WaitForRespawn();
    }

    /// <summary>
    /// Waits until the character is really alive again (Wait.ForTrue's timeout counts tries, not seconds, and gave up
    /// before AQW's respawn: the loop then went on dead, counting one death per pass), up to 60 s like CoreBots' respawn loop.
    /// </summary>
    private bool WaitForRespawn()
    {
        for (var waited = 0; waited < 60_000 && !Bot.ShouldExit && !Bot.Player.Alive; waited += 500)
            Bot.Sleep(500);
        return Bot.Player.Alive;
    }

    /// <summary>
    /// After a death: wait for the respawn, restart the class skills from scratch through CoreBots (a death left the
    /// skill thread not using skills), and go back to the cell. False when it could not get back.
    /// </summary>
    private bool RespawnAndResume(string cell, string pad)
    {
        if (!WaitForRespawn())
            return false;
        Bot.Skills.Stop();
        StartClassSkills();
        Log($"Respawned; skills restarted ({Bot.Player.CurrentClass?.Name}), back to {cell}.");
        Bot.Map.Jump(cell, pad);
        Bot.Wait.ForCellChange(cell);
        return string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase);
    }

    private void Gap() => Bot.Sleep(Math.Max(300, Bot.Config!.Get<int>("RequestGapMs")));

    // ---- shared progress file (one per plan folder, every client on this PC uses it under one mutex) ----

    /// <summary>The first stop nobody is working on (or whose claim went stale: a client stopped without releasing it).</summary>
    private JObject? Claim(List<JObject> stops) => Update(progress =>
    {
        var now = DateTime.UtcNow;
        foreach (var stop in stops)
        {
            var key = (string?)stop["key"];
            if (string.IsNullOrEmpty(key))
                continue;
            if (progress[key] is JObject entry)
            {
                var status = (string?)entry["status"];
                if (status is "done" or "failed" or "skipped")
                    continue;
                if (status == "claimed" && (string?)entry["by"] != _client &&
                    DateTime.TryParse((string?)entry["at"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) &&
                    now - at < StaleClaim)
                    continue;
            }
            progress[key] = new JObject { ["status"] = "claimed", ["by"] = _client, ["at"] = now.ToString("o") };
            return stop;
        }
        return null;
    });

    private void Finish(string key, string status, string note) => Update<object?>(progress =>
    {
        // A stop cut short by a stop request is released so the next run (or another client) does it again.
        if (status == "stopped")
            progress.Remove(key);
        else
            progress[key] = new JObject { ["status"] = status, ["at"] = DateTime.UtcNow.ToString("o"), ["note"] = note };
        return null;
    });

    /// <summary>Releases this client's unfinished claims (e.g. when the script is stopped).</summary>
    private void Release() => Update<object?>(progress =>
    {
        foreach (var p in progress.Properties().Where(p => (string?)p.Value["status"] == "claimed" && (string?)p.Value["by"] == _client).ToList())
            p.Remove();
        return null;
    });

    private T Read<T>(Func<JObject, T> read) => Update(read, save: false);

    private T Update<T>(Func<JObject, T> change, bool save = true)
    {
        try { _lock?.WaitOne(); }
        catch (AbandonedMutexException) { } // a client died holding it: the mutex is ours now
        try
        {
            // A plan placed in a synced folder (OneDrive, Dropbox) is opened by the sync client right after each write,
            // so a busy file is retried instead of stopping the script.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var progress = File.Exists(_progressPath) ? JObject.Parse(File.ReadAllText(_progressPath)) : new JObject();
                    var result = change(progress);
                    if (save)
                    {
                        var tmp = $"{_progressPath}.{_client}.tmp";
                        File.WriteAllText(tmp, progress.ToString(Formatting.Indented));
                        File.Move(tmp, _progressPath, true);
                    }
                    return result;
                }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
                {
                    Bot.Sleep(500);
                }
            }
        }
        finally
        {
            try { _lock?.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private void Log(string message) => Bot.Log("[SkuaSim Harvester] " + message);
}
