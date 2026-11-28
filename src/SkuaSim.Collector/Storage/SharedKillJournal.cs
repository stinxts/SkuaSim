using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SkuaSim.Collector.Storage;

/// <summary>
/// One monster death as seen by every collector on this PC that was credited with it. AQW rolls a group kill's drops once
/// and every participant gets them (user, 2026-11-25/27: all monster drops, quest items included; only quest rewards are
/// per player), so an army of N clients sees one sample, not N. The kill is identified by the room instance
/// ("lair-95962", from moveToArea "areaName") and the MonMapID; it stays open to the other clients until the server's
/// "respawnMon" for that MonMapID (the monster cannot die again before it) or <see cref="KillJournalState.JoinWindow"/>.
/// Holds no player or account name: clients are random per-session tokens.
/// </summary>
public sealed class SharedKill
{
    public string Id { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
    public int MonMapId { get; set; }
    public DateTime OpenedUtc { get; set; }
    public bool JoinClosed { get; set; }
    public int MonsterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Clients { get; set; } = new();

    /// <summary>True while every contributing client knew its active quest list (partial knowledge must not bias rates).</summary>
    public bool QuestsKnown { get; set; } = true;

    /// <summary>Union of the contributing clients' active quests (a shared drop is eligible when any of them has the quest).</summary>
    public SortedSet<int> Quests { get; set; } = new();

    /// <summary>Set when any client saw a different monster credited within the ambiguity window: its drops are discarded.</summary>
    public bool Ambiguous { get; set; }

    /// <summary>Union of the drops the clients received from this kill, one per item.</summary>
    public List<SharedDrop> Drops { get; set; } = new();
}

public sealed class SharedDrop
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public bool IsTemp { get; set; }
    public int? MeasuredQuantity { get; set; }
    public int[] RequiredQuestIds { get; set; } = Array.Empty<int>();
}

/// <summary>The journal's content and rules (pure, no I/O): see <see cref="SharedKill"/>.</summary>
public sealed class KillJournalState
{
    /// <summary>
    /// Longest gap between two clients' "addGoldExp" for the same death; a kill is closed earlier by "respawnMon". Clients
    /// on one PC receive the same broadcast within milliseconds; respawns take ~2.5 s on average (collected data).
    /// </summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A kill is counted this long after it opened: past the drop attribution window (5 s) of every client, so the late
    /// drops of all clients are in.
    /// </summary>
    public static readonly TimeSpan CommitDelay = TimeSpan.FromSeconds(6);

    public List<SharedKill> Kills { get; set; } = new();
    public long NextId { get; set; }

    /// <summary>
    /// Adds a client's credited kill: joins the open kill of that room and MonMapID, or opens a new one. A client that
    /// is already in the open kill is credited with a new death (it missed the respawn), so that kill is closed first.
    /// </summary>
    public SharedKill Contribute(string room, int monMapId, string client, DateTime now, int monsterId, string name,
        bool questsKnown, IEnumerable<int> activeQuests)
    {
        var open = Kills.FirstOrDefault(k => !k.JoinClosed && k.MonMapId == monMapId &&
                                             string.Equals(k.Room, room, StringComparison.OrdinalIgnoreCase) &&
                                             now - k.OpenedUtc <= JoinWindow);
        if (open != null && open.Clients.Contains(client))
        {
            open.JoinClosed = true;
            open = null;
        }
        if (open == null)
        {
            foreach (var stale in Kills.Where(k => k.MonMapId == monMapId && string.Equals(k.Room, room, StringComparison.OrdinalIgnoreCase)))
                stale.JoinClosed = true;
            open = new SharedKill
            {
                Id = (++NextId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Room = room,
                MonMapId = monMapId,
                OpenedUtc = now,
                MonsterId = monsterId,
                Name = name
            };
            Kills.Add(open);
        }
        open.Clients.Add(client);
        if (open.MonsterId <= 0 && monsterId > 0) open.MonsterId = monsterId;
        if (string.IsNullOrWhiteSpace(open.Name) && !string.IsNullOrWhiteSpace(name)) open.Name = name;
        open.QuestsKnown &= questsKnown;
        if (questsKnown)
            open.Quests.UnionWith(activeQuests.Where(id => id > 0));
        return open;
    }

    /// <summary>Adds a drop a client received from the kill (once per item). False when the kill was already counted.</summary>
    public bool AddDrop(string killId, SharedDrop drop)
    {
        var kill = Kills.FirstOrDefault(k => k.Id == killId);
        if (kill == null)
            return false;
        var same = kill.Drops.FirstOrDefault(d => d.ItemId == drop.ItemId);
        if (same == null)
            kill.Drops.Add(drop);
        else
        {
            same.RequiredQuestIds = same.RequiredQuestIds.Union(drop.RequiredQuestIds).ToArray();
            same.IsTemp |= drop.IsTemp;
            same.MeasuredQuantity ??= drop.MeasuredQuantity;
        }
        return true;
    }

    public void MarkAmbiguous(string killId)
    {
        var kill = Kills.FirstOrDefault(k => k.Id == killId);
        if (kill != null)
            kill.Ambiguous = true;
    }

    /// <summary>"respawnMon": the monster is back, so the next credit for that MonMapID in the room is a new death.</summary>
    public void CloseJoin(string room, int monMapId)
    {
        foreach (var kill in Kills.Where(k => k.MonMapId == monMapId && string.Equals(k.Room, room, StringComparison.OrdinalIgnoreCase)))
            kill.JoinClosed = true;
    }

    /// <summary>Removes and returns the kills ready to be counted (older than <see cref="CommitDelay"/> at <paramref name="now"/>).</summary>
    public List<SharedKill> TakeDue(DateTime now)
    {
        var due = Kills.Where(k => now - k.OpenedUtc >= CommitDelay).ToList();
        if (due.Count > 0)
            Kills.RemoveAll(due.Contains);
        return due;
    }
}

/// <summary>Atomic access to the journal shared by the collectors of this PC.</summary>
public interface IKillJournal
{
    T Update<T>(Func<KillJournalState, T> action);
}

/// <summary>In-process journal (tests; clients of one process).</summary>
public sealed class InMemoryKillJournal : IKillJournal
{
    private readonly object _sync = new();
    private readonly KillJournalState _state = new();

    public T Update<T>(Func<KillJournalState, T> action)
    {
        lock (_sync)
            return action(_state);
    }

    public int OpenKills
    {
        get { lock (_sync) return _state.Kills.Count; }
    }
}

/// <summary>
/// Journal file next to gamedata.json (<see cref="FileName"/>), shared by every collector on this PC (one process per
/// client in Skua, one process for all tabs in VibeSkua 2) under a named mutex. It only holds open kills (a few seconds
/// each); a client that fails to reach it counts its kill locally, as before.
/// </summary>
public sealed class FileKillJournal : IKillJournal
{
    public const string FileName = "kill_journal.json";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc
    };

    public string FilePath { get; }
    private readonly string _mutexName;

    public FileKillJournal(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToLowerInvariant()));
        _mutexName = "Local\\SkuaSim_KillJournal_" + Convert.ToHexString(hash)[..16];
    }

    public T Update<T>(Func<KillJournalState, T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(LockTimeout); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new TimeoutException("The kill journal is busy.");
        try
        {
            var state = Load();
            var result = action(state);
            Save(state);
            return result;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private KillJournalState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<KillJournalState>(File.ReadAllText(FilePath), JsonSettings) ?? new KillJournalState();
        }
        catch (JsonException)
        {
            // A damaged journal only holds a few seconds of kills: start a new one.
        }
        return new KillJournalState();
    }

    private void Save(KillJournalState state)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(state, JsonSettings));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
