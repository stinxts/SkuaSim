namespace SkuaSim.Core.World;

/// <summary>
/// Group PvP maps where a match is played against NPCs (Bludrut Brawl…): who counts for the reward and what a win pays.
/// Kept as a small explicit table like <see cref="BossMechanics"/>; each rule names its source.
/// </summary>
public static class PvpMatchRules
{
    public sealed record Rule(
        string Map,
        string RewardItem,
        int RewardItemId,
        string EnemyCaptain,
        IReadOnlyDictionary<string, int> PointsPerEnemy,
        int WinBonus,
        int MaxReward,
        bool NpcsRespawn,
        string Source);

    private static readonly Rule[] Rules =
    {
        // Bludrut Brawl. AQW Wiki (aqwwiki.wikidot.com/pvp, Bludrut Brawl): Restorer 1, Brawler 2, Captain 0 but an
        // instant win; a win pays 5-10, max 10. Verified 2026-11-23 on 26 collected matches (collector 1.2.0, team 0,
        // reward arrives by "addItems", not as a drop): 3 Brawlers + Captain -> 7 (17 matches), 4 Restorers +
        // 3 Brawlers + Captain -> 10 (9 matches) = min(10, 1 + points); the PVPS "k" score equals the points.
        new("bludrutbrawl", "Combat Trophy", 4889, "Team B Captain",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["(B) Defensive Restorer"] = 1,
                ["(B) Brawler"] = 2,
            },
            WinBonus: 1, MaxReward: 10,
            // No "respawnMon" in 377 collected kills of (B) Restorers/Brawlers/Captain (2026-11-23), while normal maps
            // respawn in ~2.4 s: a killed NPC stays dead for the rest of the match.
            NpcsRespawn: false,
            Source: "AQW Wiki PvP page + 26 collected matches"),
    };

    public static Rule? For(string? mapName) =>
        string.IsNullOrEmpty(mapName) ? null : Rules.FirstOrDefault(r => SimWorld.Eq(r.Map, mapName));
}
