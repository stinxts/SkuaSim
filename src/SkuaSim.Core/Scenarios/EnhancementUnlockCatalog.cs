namespace SkuaSim.Core.Scenarios;

public sealed record EnhancementUnlockInfo(
    string Name,
    string Group,      // "Awe", "ForgeWeapon", "ForgeHelm", "ForgeCape"
    string Slot,       // "Weapon", "Helm", "Cape"
    int QuestId,
    int PatternId,
    int ProcId,
    string? RequiredFaction = null,
    int RequiredFactionRank = 0);

/// <summary>
/// Canonical AQW Awe and Forge enhancement unlock catalog matching Skua's CoreAdvanced.cs quest IDs
/// and enhancement pattern / weapon proc IDs.
/// </summary>
public static class EnhancementUnlockCatalog
{
    public const int AweUnlockQuestId = 2937; // "Find the Runes!" (Valencia / museum)

    public static readonly IReadOnlyList<EnhancementUnlockInfo> AweEnhancements = new EnhancementUnlockInfo[]
    {
        new("Spiral Carve", "Awe", "Weapon", AweUnlockQuestId, 9, 2),
        new("Awe Blast", "Awe", "Weapon", AweUnlockQuestId, 9, 3),
        new("Health Vamp", "Awe", "Weapon", AweUnlockQuestId, 9, 4),
        new("Mana Vamp", "Awe", "Weapon", AweUnlockQuestId, 9, 5),
        new("Powerword Die", "Awe", "Weapon", AweUnlockQuestId, 9, 6)
    };

    public static readonly IReadOnlyList<EnhancementUnlockInfo> ForgeEnhancements = new EnhancementUnlockInfo[]
    {
        // Weapon
        new("Forge (Weapon)", "ForgeWeapon", "Weapon", 8738, 10, 1),
        new("Lacerate", "ForgeWeapon", "Weapon", 8739, 10, 7),
        new("Smite", "ForgeWeapon", "Weapon", 8740, 10, 8),
        new("Valiance", "ForgeWeapon", "Weapon", 8741, 10, 9),
        new("Arcana's Concerto", "ForgeWeapon", "Weapon", 8742, 10, 10),
        new("Acheron", "ForgeWeapon", "Weapon", 8820, 10, 11),
        new("Elysium", "ForgeWeapon", "Weapon", 8821, 10, 12),
        new("Praxis", "ForgeWeapon", "Weapon", 9171, 10, 13),
        new("Dauntless", "ForgeWeapon", "Weapon", 9172, 10, 14),
        new("Ravenous", "ForgeWeapon", "Weapon", 9560, 10, 15),

        // Helm
        new("Forge (Helm)", "ForgeHelm", "Helm", 8828, 10, 0),
        new("Vim", "ForgeHelm", "Helm", 8824, 25, 0),
        new("Examen", "ForgeHelm", "Helm", 8825, 26, 0),
        new("Anima", "ForgeHelm", "Helm", 8826, 28, 0),
        new("Pneuma", "ForgeHelm", "Helm", 8827, 27, 0),
        new("Hearty", "ForgeHelm", "Helm", 9466, 32, 0, "Grimskull Trolling", 7),

        // Cape
        new("Forge (Cape)", "ForgeCape", "Cape", 8758, 10, 0),
        new("Absolution", "ForgeCape", "Cape", 8743, 11, 0),
        new("Avarice", "ForgeCape", "Cape", 8745, 12, 0),
        new("Vainglory", "ForgeCape", "Cape", 8744, 24, 0),
        new("Penitence", "ForgeCape", "Cape", 8822, 29, 0),
        new("Lament", "ForgeCape", "Cape", 8823, 30, 0)
    };

    private static readonly Dictionary<string, EnhancementUnlockInfo> ByName =
        AweEnhancements.Concat(ForgeEnhancements)
            .ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public static EnhancementUnlockInfo? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : ByName.GetValueOrDefault(name.Trim());
}
