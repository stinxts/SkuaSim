using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkuaSim.Collector.Models;

namespace SkuaSim.Core.Data;

/// <summary>
/// The harvester's shared progress file (harvest_progress.json next to the plan): stop key → { status, by, at, note }.
/// Written by tools\skua\SkuaSim_Harvester.cs; the CLI only archives it, clears failed stops, and reads it for reports.
/// </summary>
public static class HarvestProgress
{
    public const string FileName = "harvest_progress.json";

    /// <summary>A claim younger than this belongs to a client that is (probably) still running (same as the harvester).</summary>
    public static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(20);

    public static string PathFor(string planPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, FileName);

    public static JsonObject Load(string progressPath) =>
        File.Exists(progressPath) ? JsonNode.Parse(File.ReadAllText(progressPath)) as JsonObject ?? new JsonObject() : new JsonObject();

    public static string? Status(JsonNode? entry) => entry?["status"]?.GetValue<string>();
    public static string Note(JsonNode? entry) => entry?["note"]?.GetValue<string>() ?? "";

    /// <summary>Stops claimed less than <see cref="StaleClaim"/> ago: harvester clients that are still working.</summary>
    public static List<string> ActiveClaims(JsonObject progress, DateTime nowUtc) =>
        progress.Where(p => Status(p.Value) == "claimed" &&
                            DateTime.TryParse(p.Value?["at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) &&
                            nowUtc - at.ToUniversalTime() < StaleClaim)
            .Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Moves the progress file aside (harvest_progress.&lt;UTC&gt;.json) so the next run starts from zero. Returns the archive path, or null when there was none.</summary>
    public static string? Archive(string progressPath, DateTime nowUtc)
    {
        if (!File.Exists(progressPath))
            return null;
        var archive = Path.Combine(Path.GetDirectoryName(progressPath)!, $"harvest_progress.{nowUtc:yyyyMMdd-HHmmss}.json");
        File.Move(progressPath, archive);
        return archive;
    }

    /// <summary>Removes the failed stops (after a dated copy) so the harvester tries them again. Returns how many.</summary>
    public static int RemoveFailed(string progressPath, DateTime nowUtc)
    {
        var progress = Load(progressPath);
        var failed = progress.Where(p => Status(p.Value) == "failed").Select(p => p.Key).ToList();
        if (failed.Count == 0)
            return 0;
        File.Copy(progressPath, Path.Combine(Path.GetDirectoryName(progressPath)!, $"harvest_progress.{nowUtc:yyyyMMdd-HHmmss}.json"), true);
        foreach (var key in failed)
            progress.Remove(key);
        File.WriteAllText(progressPath, progress.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return failed.Count;
    }
}

/// <summary>A map the harvester could not join, with the reason the server gave (Collector server messages).</summary>
public sealed record BlockedMap(string Map, string Reason, string Detail, IReadOnlyList<string> Scripts);

public sealed record HarvestFailure(string Key, string Note);

public sealed class HarvestBlockers
{
    public const string ReportFileName = "harvest_blocked.md";
    public const string PlaylistFileName = "harvest_story_playlist.json";

    public const string MemberOnly = "member-only";
    public const string EventLocked = "event-locked";
    public const string Unavailable = "not available (seasonal?)";
    public const string RoomFull = "room full";
    public const string Locked = "locked";

    public List<BlockedMap> Maps { get; } = new();
    public List<HarvestFailure> TooStrong { get; } = new();
    public List<HarvestFailure> Other { get; } = new();

    /// <summary>Story scripts to run (in this order) to unlock the "locked" maps: each pick covers the most maps still locked.</summary>
    public List<(string Script, List<string> Maps)> StoryOrder { get; } = new();

    /// <summary>
    /// Sorts the progress file's failed stops: joins refused (classified by the server message the Collector stored for
    /// that map: member-only, event, seasonal, full; none = story/quest progress or another requirement), monsters too
    /// strong for the account, and other failures. Locked maps get the scripts that use them (Story/ first).
    /// A join refused on a visit stop ("map:lair") or a drop-sampling stop ("sample:lair:123:0") counts once per map.
    /// </summary>
    public static HarvestBlockers Analyze(JsonObject progress, ScriptUsage usage, GameDatabase db)
    {
        var result = new HarvestBlockers();
        var seenMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in progress.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (HarvestProgress.Status(entry) != "failed")
                continue;
            var note = HarvestProgress.Note(entry);
            if (note.StartsWith("could not join", StringComparison.Ordinal) && MapOfStop(key) is { } map)
            {
                if (!seenMaps.Add(map))
                    continue;
                var (reason, detail) = Classify(map, db);
                var users = usage.Maps.TryGetValue(map, out var s) ? s.ToList() : new List<string>();
                var story = users.Where(u => u.StartsWith("Story/", StringComparison.OrdinalIgnoreCase)).ToList();
                result.Maps.Add(new BlockedMap(map, reason, detail, story.Count > 0 ? story : users));
            }
            else if (note.StartsWith("too strong", StringComparison.Ordinal))
                result.TooStrong.Add(new HarvestFailure(key, note));
            else
                result.Other.Add(new HarvestFailure(key, note));
        }

        var open = result.Maps.Where(m => m.Reason == Locked && m.Scripts.Count > 0).Select(m => m.Map).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (open.Count > 0)
        {
            var best = result.Maps.Where(m => open.Contains(m.Map)).SelectMany(m => m.Scripts.Select(s => (Script: s, m.Map)))
                .GroupBy(x => x.Script, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            var maps = best.Select(x => x.Map).OrderBy(m => m, StringComparer.Ordinal).ToList();
            result.StoryOrder.Add((best.Key, maps));
            open.ExceptWith(maps);
        }
        return result;
    }

    /// <summary>The map a stop joins: "map:&lt;map&gt;" or "sample:&lt;map&gt;:&lt;monsterId&gt;:&lt;kills&gt;" (see HarvestPlan); null for other stops.</summary>
    private static string? MapOfStop(string key)
    {
        if (key.StartsWith("map:", StringComparison.Ordinal))
            return key[4..];
        if (key.StartsWith("sample:", StringComparison.Ordinal))
        {
            var parts = key.Split(':');
            return parts.Length >= 4 && parts[1].Length > 0 ? parts[1] : null;
        }
        return null;
    }

    private static (string Reason, string Detail) Classify(string map, GameDatabase db)
    {
        var messages = db.ServerMessages.Values.Where(m => m.TargetMaps.ContainsKey(map)).Select(m => m.Text).ToList();
        string? Find(string text) => messages.FirstOrDefault(m => m.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (Find("Membership-Only") is { } member) return (MemberOnly, member);
        if (Find("locked until event") is { } ev) return (EventLocked, ev);
        if (Find("is not available") is { } na) return (Unavailable, na);
        if (Find("room is full") is { } full) return (RoomFull, full);
        return (Locked, "no server message: story/quest progress, an item or another requirement the account lacks");
    }

    public string ToMarkdown(DateTime createdUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SkuaSim harvest — what the harvester could not do");
        sb.AppendLine($"Made {createdUtc:yyyy-MM-dd HH:mm} UTC by `skuasim harvest-plan` from harvest_progress.json and the Collector's server messages.");
        sb.AppendLine();
        sb.AppendLine("## Story scripts to run (Army Scheduler order)");
        if (StoryOrder.Count == 0)
            sb.AppendLine("None: no map is locked behind story or quest progress.");
        else
        {
            sb.AppendLine($"Run these on the army (e.g. VibeSkua Army Scheduler: load {PlaylistFileName} from this folder as the playlist), " +
                          "then `skuasim harvest-plan --retry-failed` and start the harvester again.");
            sb.AppendLine();
            for (var i = 0; i < StoryOrder.Count; i++)
                sb.AppendLine($"{i + 1}. `{StoryOrder[i].Script}` — opens {string.Join(", ", StoryOrder[i].Maps)}");
        }
        sb.AppendLine();
        sb.AppendLine("## Maps the harvester could not join");
        if (Maps.Count == 0)
            sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Map | Reason | Server message | Scripts that use it |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var m in Maps.OrderBy(m => m.Reason, StringComparer.Ordinal).ThenBy(m => m.Map, StringComparer.Ordinal))
                sb.AppendLine($"| {m.Map} | {m.Reason} | {m.Detail.Replace("|", "/")} | {string.Join(", ", m.Scripts.Take(3))}{(m.Scripts.Count > 3 ? $" (+{m.Scripts.Count - 3})" : "")} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Too strong for the account (for a later group pass)");
        sb.AppendLine(TooStrong.Count == 0 ? "None." : string.Join(Environment.NewLine, TooStrong.Select(f => $"- `{f.Key}`: {f.Note}")));
        sb.AppendLine();
        sb.AppendLine("## Other failures");
        sb.AppendLine(Other.Count == 0 ? "None." : string.Join(Environment.NewLine, Other.Select(f => $"- `{f.Key}`: {f.Note}")));
        return sb.ToString();
    }

    /// <summary>
    /// The story scripts as a Skua script-queue playlist (the format the client's Save Playlist writes and the VibeSkua
    /// Scheduler loads: [{ Path, Id, Name }]), in <see cref="StoryOrder"/>. The Id is derived from the script path, so the same
    /// report always gives the same file. Empty list when nothing is locked behind story progress.
    /// </summary>
    public string ToSkuaPlaylistJson(string scriptsDirectory)
    {
        var entries = StoryOrder.Select(s =>
        {
            var path = Path.GetFullPath(Path.Combine(scriptsDirectory, s.Script.Replace('/', Path.DirectorySeparatorChar)));
            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("skuasim-playlist:" + s.Script.Replace('\\', '/').ToLowerInvariant())));
            return new PlaylistEntry(path, id.ToString(), Path.GetFileName(path));
        }).ToList();
        return JsonSerializer.Serialize(entries, PlaylistJson);
    }

    private sealed record PlaylistEntry(string Path, string Id, string Name);

    private static readonly JsonSerializerOptions PlaylistJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
