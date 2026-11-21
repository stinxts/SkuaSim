namespace SkuaSim.Core.World;

/// <summary>
/// AQW XP needed to go from a level to the next (index = current level, 1..99), from the AQW Wiki
/// (https://aqwwiki.wikidot.com/exp-class-points-rep). Cross-checked 2026-11-22 against the 33 levels the collector
/// captured from the server's "levelUp" packet (11-40, 79, 80): all identical. Collected values still take precedence.
/// </summary>
public static class AqwXpTable
{
    private static readonly int[] ToNext =
    {
        0,
        65, 100, 300, 600, 1000, 1500, 2000, 2550, 3200, 3850,                                         // 1-10
        4600, 5450, 6450, 7700, 9200, 12900, 15150, 17650, 20650, 24150,                               // 11-20
        28150, 32650, 35000, 37650, 43650, 51650, 63150, 79650, 101650, 131150,                        // 21-30
        170650, 223150, 290650, 312500, 331250, 350000, 368750, 387500, 406250, 425000,                // 31-40
        443750, 462500, 481250, 500000, 518750, 537500, 556250, 575000, 598750, 633760,                // 41-50
        664670, 710130, 759000, 785000, 815000, 840000, 870000, 910000, 990000, 1080000,               // 51-60
        1180000, 1290000, 1410000, 1540000, 1680000, 1830000, 1990000, 2160000, 2340000, 2530000,      // 61-70
        2730000, 2940000, 3160000, 3390000, 3630000, 3880000, 4140000, 4410000, 4690000, 4980000,      // 71-80
        5280000, 5590000, 5910000, 6240000, 6580000, 7580000, 8580000, 9580000, 10580000, 11580000,    // 81-90
        13080000, 14580000, 16080000, 17580000, 19080000, 20580000, 22080000, 23580000, 25080000       // 91-99
    };

    /// <summary>XP to reach the next level, or 0 at/after the cap.</summary>
    public static int XpToNext(int level) => level >= 1 && level < ToNext.Length ? ToNext[level] : 0;
}
