using SkuaSim.Collector.Models;

namespace SkuaSim.Core.World;

/// <summary>
/// Picks the collected class combat (crit, dodge, damage, healing) that fits a simulated character (Phase 3.5 item 16).
/// The collector splits each class's combat by 10-level band, class rank and enhancement signature
/// (<see cref="CapturedClassProfile.CombatByProfile"/>). Matching, best first:
/// <list type="number">
/// <item>same band, same rank and the exact signature (patterns and enhancement levels);</item>
/// <item>same band, same enhancement patterns and a compatible rank (1–3, 4–9 or 10: the rank 4 and 10 passives
/// change the stats; an unknown rank counts as compatible) — the matching profiles are added up.</item>
/// </list>
/// Nothing else is blended: the legacy all-gear aggregate (<see cref="CapturedClassProfile.Combat"/>) mixes endgame
/// and low-level characters, so it is not used.
/// </summary>
public static class ClassCombatProfileMatch
{
    public sealed record Match(CapturedPlayerCombatStats Stats, string Description);

    public static Match? Find(CapturedClassProfile? captured, int level, int rank, string signature)
    {
        if (captured?.CombatByProfile is not { Count: > 0 } profiles)
            return null;

        var exactKey = ClassProfileKeys.CombatKey(level, rank, signature);
        if (profiles.TryGetValue(exactKey, out var exact) && !exact.IsEmpty)
            return new Match(exact, $"{exactKey} (exact)");

        var band = ClassProfileKeys.Band(level);
        var patterns = ClassProfileKeys.PatternsOnly(signature);
        var sum = new CapturedPlayerCombatStats();
        var used = 0;
        // Ordinal key order so the sum never depends on dictionary insertion order.
        foreach (var (key, stats) in profiles.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!ClassProfileKeys.TryParseCombatKey(key, out var b, out var r, out var sig) || b != band)
                continue;
            if (ClassProfileKeys.PatternsOnly(sig) != patterns || !RanksCompatible(r, rank))
                continue;
            sum.Add(stats);
            used++;
        }

        return used == 0 || sum.IsEmpty
            ? null
            : new Match(sum, $"{band}|{patterns} ({used} profile{(used == 1 ? "" : "s")}, same patterns)");
    }

    private static bool RanksCompatible(int captured, int wanted) =>
        captured == 0 || RankGroup(captured) == RankGroup(wanted);

    private static int RankGroup(int rank) => rank >= 10 ? 2 : rank >= 4 ? 1 : 0;
}
