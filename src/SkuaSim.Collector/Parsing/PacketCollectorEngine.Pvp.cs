using Newtonsoft.Json.Linq;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

/// <summary>
/// PvP (Bludrut Brawl…) from the verified packets "PVPE" { cmd, team, typ, unm, val } and
/// "PVPS" { cmd, pvpScore: [ {k, m, r, v}, {k, m, r, v} ] } (anonymized samples, 2026-11-22). "unm" (a player name)
/// is never read. Called under <c>_sync</c>.
/// </summary>
public sealed partial class PacketCollectorEngine
{
    // RAM only: the last score seen in the current PvP map, to detect the end of a match.
    private string? _pvpLastScore;
    private long _pvpLastScoreTotal = -1;
    private string _pvpMap = string.Empty;

    // RAM only: the match being played, and the last ended one while its grace window is open (items such as Combat
    // Trophies may arrive right after the captain dies or after the character is moved out of the map).
    private CapturedPvpMatch? _pvpMatch;
    private CapturedPvpMatch? _pvpEndedMatch;
    private string _pvpEndedMatchMap = string.Empty;
    private DateTime _pvpEndedMatchUtc;

    /// <summary>Clock for the PvP grace window; replaceable by tests.</summary>
    public Func<DateTime> PvpClockUtc { get; set; } = () => DateTime.UtcNow;

    private CapturedPvpStats PvpStatsFor(string map)
    {
        var key = string.IsNullOrEmpty(map) ? "unknown" : map;
        if (!_delta.Pvp.TryGetValue(key, out var stats))
            _delta.Pvp[key] = stats = new CapturedPvpStats();
        return stats;
    }

    private static void AddToSequence(CapturedPvpStats stats, string entry)
    {
        if (stats.Sequence.Count < CapturedPvpStats.MaxSequence)
            stats.Sequence.Add(entry);
    }

    /// <summary>The match of the current PvP map, opened by its first event or score.</summary>
    private CapturedPvpMatch CurrentPvpMatchLocked()
    {
        if (_pvpMatch == null)
        {
            CommitEndedPvpMatchLocked(); // a new match began: the previous one's grace window closes
            _pvpMatch = new CapturedPvpMatch();
        }
        return _pvpMatch;
    }

    internal void HandlePvpEventPacket(JObject dataObj)
    {
        var team = ParseInt(dataObj["team"], -1);
        var typ = (dataObj["typ"]?.ToString() ?? "").Trim();
        var val = (dataObj["val"]?.ToString() ?? "").Trim();
        if (team < 0 || typ.Length == 0 || typ.Length > 40 || val.Length > 60)
            return;

        if (!string.Equals(_pvpMap, _currentMapName, StringComparison.OrdinalIgnoreCase))
            EndPvpMatchLocked(leaving: true);
        _pvpMap = _currentMapName;

        var stats = PvpStatsFor(_currentMapName);
        var key = $"team{team}:{typ}:{val}";
        stats.Events[key] = stats.Events.GetValueOrDefault(key) + 1;
        var match = CurrentPvpMatchLocked();
        match.Events[key] = match.Events.GetValueOrDefault(key) + 1;
        AddToSequence(stats, "E:" + key);
    }

    internal void HandlePvpScorePacket(JObject dataObj)
    {
        if (dataObj["pvpScore"] is not JArray teams || teams.Count == 0)
            return;

        var parts = new List<string>(teams.Count);
        long total = 0;
        foreach (var t in teams)
        {
            if (t is not JObject o)
                return;
            int k = ParseInt(o["k"]), m = ParseInt(o["m"]), r = ParseInt(o["r"]), v = ParseInt(o["v"]);
            parts.Add($"{k},{m},{r},{v}");
            total += (long)k + m + r + v;
        }
        var score = string.Join('|', parts);

        if (!string.Equals(_pvpMap, _currentMapName, StringComparison.OrdinalIgnoreCase))
            EndPvpMatchLocked(leaving: true); // a score from another map: the previous map's match is over
        _pvpMap = _currentMapName;

        var stats = PvpStatsFor(_currentMapName);
        stats.ScoreUpdates++;
        // Scores only grow during a match; a lower total means a new match began.
        if (_pvpLastScore != null && total < _pvpLastScoreTotal)
            EndPvpMatchLocked(leaving: false);
        if (score != _pvpLastScore)
            AddToSequence(stats, "S:" + score);
        var match = CurrentPvpMatchLocked();
        match.ScoreUpdates++;
        match.FinalScore = score;
        _pvpLastScore = score;
        _pvpLastScoreTotal = total;
    }

    /// <summary>
    /// Credits an item the character received to the PvP match being played, or to the one that ended less than
    /// <see cref="CapturedPvpMatch.GraceSeconds"/> ago. Called under <c>_sync</c>.
    /// </summary>
    private void RecordPvpItemLocked(bool fromDrop, int itemId, string? itemName, long qty)
    {
        if (qty <= 0)
            return;
        CommitEndedPvpMatchIfDueLocked();
        var match = _pvpMatch != null && string.Equals(_pvpMap, _currentMapName, StringComparison.OrdinalIgnoreCase)
            ? _pvpMatch
            : _pvpEndedMatch;
        if (match == null)
            return;

        var key = $"{itemId}:{itemName}";
        var bucket = fromDrop ? match.ItemsDropped : match.ItemsAdded;
        bucket[key] = bucket.GetValueOrDefault(key) + qty;
        var stats = PvpStatsFor(match == _pvpMatch ? _pvpMap : _pvpEndedMatchMap);
        AddToSequence(stats, $"I:{(fromDrop ? "drop" : "add")}:{key}x{qty}");
    }

    /// <summary>Ends the current match (leaving the map, or a new match began). Called under <c>_sync</c>.</summary>
    private void EndPvpMatchLocked(bool leaving = true)
    {
        if (_pvpLastScore != null && _pvpLastScoreTotal > 0)
        {
            var stats = PvpStatsFor(_pvpMap);
            if (stats.FinalScores.Count < CapturedPvpStats.MaxFinalScores)
                stats.FinalScores.Add(_pvpLastScore);
        }
        _pvpLastScore = null;
        _pvpLastScoreTotal = -1;

        if (_pvpMatch != null)
        {
            CommitEndedPvpMatchLocked();
            _pvpMatch.EndedByLeaving = leaving;
            _pvpEndedMatch = _pvpMatch;
            _pvpEndedMatchMap = string.IsNullOrEmpty(_pvpMap) ? "unknown" : _pvpMap;
            _pvpEndedMatchUtc = PvpClockUtc();
            _pvpMatch = null;
        }
    }

    private void CommitEndedPvpMatchIfDueLocked()
    {
        if (_pvpEndedMatch != null && (PvpClockUtc() - _pvpEndedMatchUtc).TotalSeconds > CapturedPvpMatch.GraceSeconds)
            CommitEndedPvpMatchLocked();
    }

    /// <summary>Writes the ended match into the delta (its grace window is over). Called under <c>_sync</c>.</summary>
    private void CommitEndedPvpMatchLocked()
    {
        if (_pvpEndedMatch == null)
            return;
        var stats = PvpStatsFor(_pvpEndedMatchMap);
        if (stats.Matches.Count < CapturedPvpStats.MaxFinalScores)
            stats.Matches.Add(_pvpEndedMatch);
        _pvpEndedMatch = null;
    }
}
