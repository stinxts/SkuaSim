/*
name: SkuaSim Harvester
description: Visits the maps, shops and quests listed in a SkuaSim harvest plan so the SkuaSim Collector plugin records them (cells, monsters, official drop lists, shops, quests). Run it on every army client at the same time: they share one progress file and split the work. It never fights on purpose, never accepts or completes quests and never buys anything. Your character name is never written.
tags: skuasim, collector, data, harvest, army, tool
*/
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

    public string OptionsStorage = "SkuaSimHarvester";
    public bool DontPreconfigure = false;
    public List<IOption> Options = new()
    {
        new Option<string>("PlanFile", "Plan file",
            "harvest_plan.json made by 'skuasim harvest-plan' (empty = Documents\\SkuaSim Harvest\\harvest_plan.json).", ""),
        new Option<int>("MaxStops", "Stops per client", "How many stops this client does before it stops (0 = until the plan is done).", 0),
        new Option<bool>("PrivateRooms", "Private rooms",
            "Join private rooms (map-<number>). Off = public rooms, which also records how full rooms get; a full or failed public join retries in a private room.", false),
        new Option<int>("CellDwellMs", "Time per cell (ms)", "How long to stay in each cell so the monsters load.", 1000),
        new Option<int>("RequestGapMs", "Time between requests (ms)",
            "Pause after each drop-list, shop or quest request, to stay well below AQW's packet spam limit.", 1000),
        new Option<int>("MaxCellsPerMap", "Max cells per map", "Cells visited per map (cutscene and blank cells are skipped).", 40),
    };

    private const string ProgressFileName = "harvest_progress.json";
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(20);

    private IScriptInterface Bot = null!;
    private string _progressPath = "";
    private readonly string _client = $"client-{Environment.ProcessId}";
    private readonly Random _rooms = new();
    private Mutex? _lock;

    public void ScriptMain(IScriptInterface bot)
    {
        Bot = bot;
        if (!Bot.Player.LoggedIn)
        {
            Log("Log in first: the character must be in game.");
            return;
        }

        var planPath = (Bot.Config!.Get<string>("PlanFile") ?? "").Trim();
        if (planPath.Length == 0)
            planPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SkuaSim Harvest", "harvest_plan.json");
        if (!File.Exists(planPath))
        {
            Log($"No plan at {planPath}. Make one with: skuasim harvest-plan");
            return;
        }
        var plan = JObject.Parse(File.ReadAllText(planPath));
        var stops = (plan["stops"] as JArray ?? new JArray()).OfType<JObject>().ToList();
        _progressPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, ProgressFileName);
        _lock = new Mutex(false, @"Local\SkuaSimHarvester");

        var maxStops = Bot.Config.Get<int>("MaxStops");
        Log($"v{Version}: plan with {stops.Count} stop(s), progress in {_progressPath}.");
        var done = 0;
        try
        {
            while (!Bot.ShouldExit && (maxStops <= 0 || done < maxStops))
            {
                var stop = Claim(stops);
                if (stop == null)
                {
                    Log("Nothing left to claim: every stop is done, failed or being visited by another client.");
                    break;
                }
                var key = (string)stop["key"]!;
                try
                {
                    var (status, note) = Visit(stop);
                    Finish(key, status, note);
                    Log($"{key}: {status} — {note}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not ThreadInterruptedException)
                {
                    Finish(key, "failed", "error: " + ex.Message);
                    Log($"{key}: failed — {ex.Message}");
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
            Release();
            _lock.Dispose();
            _lock = null;
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

    private bool JoinMap(string map)
    {
        if (string.Equals(Bot.Map.Name, map, StringComparison.OrdinalIgnoreCase) && Bot.Map.Loaded)
            return true;
        var privateFirst = Bot.Config!.Get<bool>("PrivateRooms");
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
        foreach (var cell in cells)
        {
            if (Bot.ShouldExit)
                break;
            Bot.Map.Jump(cell, "Spawn");
            Bot.Sleep(dwell);
            RecoverIfDead();
            if (!string.Equals(Bot.Player.Cell, cell, StringComparison.OrdinalIgnoreCase))
                continue;
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
        return $"{visited}/{cells.Count} cell(s), {asked.Count} drop list(s) requested";
    }

    private static bool SkipCell(string cell) =>
        string.IsNullOrWhiteSpace(cell) || cell.StartsWith("Cut", StringComparison.OrdinalIgnoreCase) ||
        cell.Equals("Blank", StringComparison.OrdinalIgnoreCase) || cell.Equals("Wait", StringComparison.OrdinalIgnoreCase);

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
        Bot.Wait.ForTrue(() => Bot.Player.Alive, 40);
    }

    private void Gap() => Bot.Sleep(Math.Max(300, Bot.Config!.Get<int>("RequestGapMs")));

    // ---- shared progress file (one per plan folder, every client on this PC uses it under one mutex) ----

    /// <summary>The first stop nobody is working on (or whose claim went stale, or that this client was doing before a restart).</summary>
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
                if (status is "done" or "failed")
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
            var progress = File.Exists(_progressPath) ? JObject.Parse(File.ReadAllText(_progressPath)) : new JObject();
            var result = change(progress);
            if (save)
            {
                var tmp = _progressPath + ".tmp";
                File.WriteAllText(tmp, progress.ToString(Formatting.Indented));
                File.Move(tmp, _progressPath, true);
            }
            return result;
        }
        finally
        {
            try { _lock?.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private void Log(string message) => Bot.Log("[SkuaSim Harvester] " + message);
}
