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

    internal void HandlePvpEventPacket(JObject dataObj)
    {
        var team = ParseInt(dataObj["team"], -1);
        var typ = (dataObj["typ"]?.ToString() ?? "").Trim();
        var val = (dataObj["val"]?.ToString() ?? "").Trim();
        if (team < 0 || typ.Length == 0 || typ.Length > 40 || val.Length > 60)
            return;

        var stats = PvpStatsFor(_currentMapName);
        var key = $"team{team}:{typ}:{val}";
        stats.Events[key] = stats.Events.GetValueOrDefault(key) + 1;
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
            EndPvpMatchLocked(); // a score from another map: the previous map's match is over
        _pvpMap = _currentMapName;

        var stats = PvpStatsFor(_currentMapName);
        stats.ScoreUpdates++;
        // Scores only grow during a match; a lower total means a new match began.
        if (_pvpLastScore != null && total < _pvpLastScoreTotal && stats.FinalScores.Count < CapturedPvpStats.MaxFinalScores)
            stats.FinalScores.Add(_pvpLastScore);
        if (score != _pvpLastScore)
            AddToSequence(stats, "S:" + score);
        _pvpLastScore = score;
        _pvpLastScoreTotal = total;
    }

    /// <summary>Records the last score as a match result (leaving the PvP map). Called under <c>_sync</c>.</summary>
    private void EndPvpMatchLocked()
    {
        if (_pvpLastScore != null && _pvpLastScoreTotal > 0)
        {
            var stats = PvpStatsFor(_pvpMap);
            if (stats.FinalScores.Count < CapturedPvpStats.MaxFinalScores)
                stats.FinalScores.Add(_pvpLastScore);
        }
        _pvpLastScore = null;
        _pvpLastScoreTotal = -1;
    }
}
