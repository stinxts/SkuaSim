namespace SkuaSim.Core.World;

/// <summary>Equipment slot an enhancement is applied to.</summary>
public enum EnhancementSlot { Weapon, Armor, Helm, Cape }

/// <summary>Primary stats an enhancement adds (can be negative for some Forge helms).</summary>
public readonly record struct EnhancementStats(int Str, int Int, int Dex, int Wis, int End, int Lck)
{
    public int Total => Str + Int + Dex + Wis + End + Lck;
}

/// <summary>
/// AQW enhancement stats from the AQW Wiki tables (fetched 2026-11-22). Every enhancement gives a total that depends
/// on its level and slot, split by a fixed pattern distribution:
/// <list type="bullet">
/// <item><b>Total by level</b> — weapon totals from the wiki's enhancement shops (listed under Enhancements in
/// <see href="http://aqwwiki.wikidot.com/game-menu">Game Menu</see>): levels 1–50 from the
/// <see href="http://aqwwiki.wikidot.com/wizard-enhancement-shop">Wizard Enhancement Shop</see> weapon rows marked
/// with the legend icon (L1 6, L10 24, L20 40, L30 60, L40 80, L50 97; the unmarked rows give about the previous
/// level's stats), 60–100 from the <see href="http://aqwwiki.wikidot.com/lvl-50-wizard-enh">Lvl 50+ Wizard</see>
/// weapon rows (L60 120 … L100 199): about 2 × level, interpolated in between. The Fighter shop rows match
/// (armor L30 46, L50 75; cape L50 59). The other slots are a fixed share of the
/// weapon: armor and helm 75 % (150 at L100), cape 60 % (120 at L100) — the totals of every
/// <see href="http://aqwwiki.wikidot.com/lvl-50-fighter-enh">Lvl 50+</see> pattern table (e.g. Fighter armor L60 91,
/// L80 122, L100 149; Wizard cape L51 60, L100 120), the Forge helm (152) and Forge cape (121).</item>
/// <item><b>Distribution</b> — the level-100 armor row of each pattern page (Fighter 66 STR / 19 DEX / 64 END; Thief
/// 45/67/37; Hybrid 42 STR 40 INT 30 DEX 37 END; Wizard 75 INT 30 WIS 15 END 30 LCK; Healer 67 INT 22 WIS 60 END;
/// SpellBreaker 60 INT 45 WIS 30 END 15 LCK; Lucky 15 each + 75 LCK; Adventurer, highest listed L45 armor 12/12/12/12
/// with 15 END and 2 LCK), the <see href="http://aqwwiki.wikidot.com/forge-weapon-enhancements">Forge weapon</see>
/// (50 STR 50 INT 100 LCK; Acheron 100 INT 100 LCK; Dauntless 88 STR 26 DEX 86 END; Elysium = Wizard), the
/// <see href="http://aqwwiki.wikidot.com/forge-helmet-enhancements">Forge helms</see> (Anima 204 STR 24 INT 36 DEX
/// 24 WIS −136 END; Pneuma 37/179/36/36/−136; Examen 15 INT 198 WIS −137 END 76 LCK; Vim 15 STR 198 DEX −137 END
/// 76 LCK; Hearty −17 STR −18 others +241 END) and the
/// <see href="http://aqwwiki.wikidot.com/forge-cape-enhancements">Forge capes</see> (all 30 STR 30 INT 61 LCK).
/// Awe enhancements use their base pattern (the Awe pages match the Lvl 50+ rows, e.g. Fighter Awe weapon L100 88/26/86).</item>
/// </list>
/// Each stat is rounded; a stat of the pattern is at least 1 (Wizard Awe L1 = 3/1/1/1).
/// </summary>
public static class AqwEnhancementTable
{
    // Weapon total by enhancement level: levels 1-50 from the Wizard Enhancement Shop weapon rows (wizard-enhancement-shop,
    // member rows: L1 6, L10 24, L20 40, L30 60, L40 80, L50 97), 60-100 from the Lvl 50+ Wizard weapon rows (L60 120 …
    // L100 199) — about 2 x level. Awe rows run a few points higher (L40 84, L80 164) and are not used.
    private static readonly (int Level, double Total)[] WeaponTotals =
    {
        (1, 6), (2, 9), (3, 10), (5, 14), (8, 20), (10, 24), (20, 40), (25, 50), (30, 60), (35, 70), (40, 80), (45, 89), (50, 97),
        (60, 120), (70, 140), (80, 161), (90, 180), (100, 199)
    };

    /// <summary>Total stat points of an enhancement of this level in this slot (before the pattern split).</summary>
    public static double SlotTotal(EnhancementSlot slot, int enhancementLevel)
    {
        var lvl = Math.Clamp(enhancementLevel, 1, 100);
        var weapon = WeaponTotals[^1].Total;
        for (var i = 1; i < WeaponTotals.Length; i++)
        {
            if (lvl > WeaponTotals[i].Level)
                continue;
            var (l0, t0) = WeaponTotals[i - 1];
            var (l1, t1) = WeaponTotals[i];
            weapon = t0 + (t1 - t0) * (lvl - l0) / (double)(l1 - l0);
            break;
        }
        return slot switch
        {
            EnhancementSlot.Weapon => weapon,
            EnhancementSlot.Cape => weapon * 0.60,
            _ => weapon * 0.75
        };
    }

    /// <summary>
    /// Raw distribution of a pattern (wiki row values; only their proportions matter). A weapon's proc can change it:
    /// Acheron (11), Elysium (12) and Dauntless (14) weapons have their own rows on the Forge weapon page.
    /// </summary>
    private static EnhancementStats Distribution(int patternId, EnhancementSlot slot, int procId)
    {
        if (slot == EnhancementSlot.Weapon)
        {
            switch (procId)
            {
                case 11: return new(0, 100, 0, 0, 0, 100);   // Acheron
                case 12: return new(0, 75, 0, 30, 15, 30);   // Elysium (Wizard row)
                case 14: return new(88, 0, 26, 0, 86, 0);    // Dauntless
            }
        }

        return patternId switch
        {
            1 => new(12, 12, 12, 12, 15, 2),     // Adventurer (L45 armor; columns reordered)
            2 => new(66, 0, 19, 0, 64, 0),       // Fighter
            3 => new(45, 0, 67, 0, 37, 0),       // Thief
            5 => new(42, 40, 30, 0, 37, 0),      // Hybrid
            6 => new(0, 75, 0, 30, 15, 30),      // Wizard
            7 => new(0, 67, 0, 22, 60, 0),       // Healer
            8 => new(0, 60, 0, 45, 30, 15),      // SpellBreaker
            9 => new(15, 15, 15, 15, 15, 75),    // Lucky
            25 => new(15, 0, 198, 0, -137, 76),  // Vim helm
            26 => new(0, 15, 0, 198, -137, 76),  // Examen helm
            27 => new(37, 179, 36, 36, -136, 0), // Pneuma helm
            28 => new(204, 24, 36, 24, -136, 0), // Anima helm
            32 => new(-17, -18, -18, -18, 241, -18), // Hearty helm
            // Forge (10) and the Forge capes Absolution 11, Avarice 12, Vainglory 24, Penitence 29, Lament 30
            10 or 11 or 12 or 24 or 29 or 30 => new(50, 50, 0, 0, 0, 100),
            _ => new(0, 0, 0, 0, 0, 0)
        };
    }

    private static bool IsForgeFamily(int patternId) => patternId is 10 or 11 or 12 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 32;

    /// <summary>True when the pattern ID is in the wiki table (unknown IDs add no stats).</summary>
    public static bool IsKnownPattern(int patternId) => Distribution(patternId, EnhancementSlot.Armor, 0).Total != 0;

    /// <summary>Stats an enhancement adds, from the wiki tables.</summary>
    public static EnhancementStats StatsFor(int patternId, int enhancementLevel, EnhancementSlot slot, int procId = 0)
    {
        if (patternId <= 0 || enhancementLevel <= 0)
            return default;
        var dist = Distribution(patternId, slot, procId);
        var weightSum = dist.Total;
        if (weightSum == 0)
            return default;

        var total = SlotTotal(slot, enhancementLevel);
        // Forge enhancements have their own totals on the wiki: weapon 200, helm 152, cape 121 at level 100
        // (vs 199 / 149 / 119 for the normal tables).
        if (IsForgeFamily(patternId) || (slot == EnhancementSlot.Weapon && procId is >= 7 and <= 15))
            total *= slot switch
            {
                EnhancementSlot.Weapon => 200.0 / 199.0,
                EnhancementSlot.Helm => 152.0 / (199.0 * 0.75),
                EnhancementSlot.Cape => 121.0 / (199.0 * 0.60),
                _ => 1.0
            };
        int Part(int weight)
        {
            if (weight == 0)
                return 0;
            var v = (int)Math.Round(total * weight / weightSum, MidpointRounding.AwayFromZero);
            return weight > 0 ? Math.Max(1, v) : Math.Min(-1, v);
        }
        return new(Part(dist.Str), Part(dist.Int), Part(dist.Dex), Part(dist.Wis), Part(dist.End), Part(dist.Lck));
    }
}
