namespace SkuaSim.Core.World;

/// <summary>
/// Known AQW boss mechanics that change how a fight works (not just numbers). Kept as a small explicit table; each rule
/// names its source. Phase 4 (aura/boss combat) will extend this.
/// </summary>
public static class BossMechanics
{
    /// <summary>A monster that takes no damage while any of its protectors in the same cell is alive.</summary>
    private sealed record Protection(string Map, string Protected, string Protector, string Source);

    private static readonly Protection[] Protections =
    {
        // Escherion (Lord of Chaos, map "escherion") is invulnerable while the Staff of Inversion lives; CoreBots'
        // KillEscherion switches to the staff (MonMapID 2) when Escherion (MonMapID 3) is in combat.
        new("escherion", "Escherion", "Staff of Inversion", "AQW boss mechanic (CoreBots.KillEscherion)"),
    };

    /// <summary>The living protector that makes <paramref name="target"/> invulnerable right now, or null.</summary>
    public static SimMonster? ProtectorFor(string mapName, SimMonster target, IEnumerable<SimMonster>? mapMonsters)
    {
        if (mapMonsters == null)
            return null;
        foreach (var rule in Protections)
        {
            if (!SimWorld.Eq(rule.Map, mapName) || !SimWorld.Eq(rule.Protected, target.Name))
                continue;
            var protector = mapMonsters.FirstOrDefault(m => m.Alive && SimWorld.Eq(m.Name, rule.Protector) && SimWorld.Eq(m.Cell, target.Cell));
            if (protector != null)
                return protector;
        }
        return null;
    }
}
