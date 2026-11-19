namespace SkuaSim.Core.Scenarios;

public sealed record AqwBoostItemInfo(
    string Slot,          // "Weapon", "Armor", "Helm", "Cape", "Pet"
    string Name,          // Exact AQW item name (or "(None)")
    string Category,      // AQW ItemCategory string (e.g. "Sword", "Polearm", "Armor", "Helm", "Cape", "Pet")
    string Meta,          // Exact AQW Meta string (e.g. "dmgAll:1.51" or multi-boost "Undead:1.50,Chaos:1.50,...,cp:1.50,rep:1.50,gold:1.30")
    string Summary)       // Concise human-readable boost description
{
    public string DisplayName => string.IsNullOrEmpty(Summary) ? Name : $"{Name}  ({Summary})";
    public override string ToString() => DisplayName;
}

/// <summary>
/// Curated catalog of real AQW boosted equipment per slot, including multi-boost endgame items
/// with AQW Wiki and captured-packet verified percentages and Meta tags compatible with Skua.
/// </summary>
public static class AqwBoostGearCatalog
{
    public const string NoneItemName = "(None)";

    public static readonly IReadOnlyList<AqwBoostItemInfo> Items = new AqwBoostItemInfo[]
    {
        // ---- WEAPONS ----
        new("Weapon", NoneItemName, "Sword", "", "No boost"),
        new("Weapon", "Necrotic Sword of Doom", "Sword", "dmgAll:1.51", "+51% All"),
        new("Weapon", "Empowered Higure", "Sword",
            "dmgAll:1.51,exp:1.35,cp:1.35,gold:1.30,rep:1.30",
            "+51% All · +35% XP/CP · +30% Gold/Rep"),
        new("Weapon", "Exalted Apotheosis", "Sword", "dmgAll:1.51", "+51% All"),
        new("Weapon", "Sin of the Abyss", "Polearm", "dmgAll:1.51", "+51% All"),
        new("Weapon", "Providence", "Polearm", "dmgAll:1.51", "+51% All"),
        new("Weapon", "Malgor's ShadowFlame Blade", "Sword", "dmgAll:1.51", "+51% All"),
        new("Weapon", "Hollowborn Reaper's Scythe", "Polearm", "dmgAll:1.40", "+40% All"),
        new("Weapon", "Awescended Omni Armblades", "Dagger", "dmgAll:1.40", "+40% All"),
        new("Weapon", "Burning Blade of Abezeth", "Sword", "dmgAll:1.30", "+30% All"),
        new("Weapon", "Burning Blade", "Sword", "dmgAll:1.15", "+15% All"),
        new("Weapon", "Awethur's Accoutrements", "Sword",
            "exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+25% XP / CP / Gold / Rep"),
        new("Weapon", "Blade of Awe", "Sword",
            "Undead:1.10,Dragonkin:1.10,Human:1.10,Elemental:1.10,Chaos:1.10",
            "+10% All Tagged Races"),
        new("Weapon", "Blinding Light of Destiny", "Axe", "Undead:1.75", "+75% Undead"),
        new("Weapon", "DragonBlade of Nulgath", "Sword", "Dragonkin:1.75", "+75% Dragonkin"),
        new("Weapon", "Prince Darkon's Poleaxe", "Polearm", "Human:1.75", "+75% Human"),
        new("Weapon", "Empowered Chaos Avenger's Greatsword", "Sword", "Chaos:1.75", "+75% Chaos"),
        new("Weapon", "Headless Pumpkin Blade", "Sword", "Elemental:1.50", "+50% Elemental"),
        new("Weapon", "Chaorrupter Unlocked", "Sword",
            "Chaos:1.50,Drakath:2.20",
            "+50% Chaos · +120% Drakath"),

        // ---- ARMORS ----
        new("Armor", NoneItemName, "Armor", "", "No boost"),
        new("Armor", "Radiant Goddess of War", "Armor",
            "Undead:1.50,Chaos:1.50,Dragonkin:1.50,Human:1.50,Elemental:1.50,cp:1.50,rep:1.50,gold:1.30",
            "+50% Tagged Races · +50% CP/Rep · +30% Gold"),
        new("Armor", "Infernal Flame Pyromancer", "Armor",
            "dmgAll:1.35,exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+35% All · +25% XP/CP/Gold/Rep"),
        new("Armor", "ArchFiend DoomLord", "Armor",
            "dmgAll:1.35,exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+35% All · +25% XP/CP/Gold/Rep"),
        new("Armor", "Empowered Darkon's Debris 1", "Armor",
            "dmgAll:1.35,exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+35% All · +25% XP/CP/Gold/Rep"),
        new("Armor", "Awescended", "Armor",
            "cp:1.50,rep:1.50,gold:1.30",
            "+50% CP/Rep · +30% Gold"),
        new("Armor", "Armor of Awe", "Armor",
            "exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+25% XP / CP / Gold / Rep"),
        new("Armor", "Drago's Royal Guard", "Armor",
            "exp:1.35,cp:1.35",
            "+35% XP / CP"),
        new("Armor", "Sepulchure's DoomKnight Armor", "Armor",
            "Human:1.50",
            "+50% Human"),
        new("Armor", "Fire Champion's Armor", "Armor",
            "Dragonkin:1.50",
            "+50% Dragonkin"),
        new("Armor", "Malgor the ShadowLord", "Armor",
            "Elemental:1.50",
            "+50% Elemental"),
        new("Armor", "Original Drakath Armor", "Armor",
            "Chaos:1.40",
            "+40% Chaos"),
        new("Armor", "Arch DoomKnight", "Armor",
            "Human:1.30,Dragonkin:1.30",
            "+30% Human / Dragonkin"),
        new("Armor", "Celestial Pirate Commander", "Armor",
            "Undead:1.30,Chaos:1.30,Dragonkin:1.30,Human:1.30,Elemental:1.30",
            "+30% All Tagged Races"),

        // ---- HELMS ----
        new("Helm", NoneItemName, "Helm", "", "No boost"),
        new("Helm", "Head of the Legion Beast", "Helm",
            "Human:1.40,Undead:1.40,Dragonkin:1.30,exp:1.30,cp:1.30,gold:1.30,rep:1.30",
            "+40% Human/Undead · +30% Dragon · +30% XP/CP/Gold/Rep"),
        new("Helm", "Awescended Omni Cowl", "Helm",
            "cp:1.50,rep:1.50,gold:1.30",
            "+50% CP/Rep · +30% Gold"),
        new("Helm", "Helm of Awe", "Helm",
            "exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+25% XP / CP / Gold / Rep"),
        new("Helm", "Archmaster of Awe Battle Cowl", "Helm",
            "Undead:1.20,Chaos:1.20,Dragonkin:1.20,Human:1.20,Elemental:1.20",
            "+20% All Tagged Races"),
        new("Helm", "Celestial Pirate Commander's Tricorn", "Helm",
            "Undead:1.30,Chaos:1.30,Dragonkin:1.30,Human:1.30,Elemental:1.30",
            "+30% All Tagged Races"),

        // ---- CAPES ----
        new("Cape", NoneItemName, "Cape", "", "No boost"),
        new("Cape", "Empowered Bonfire Altar", "Cape",
            "dmgAll:1.35,exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+35% All · +25% XP/CP/Gold/Rep"),
        new("Cape", "Awescended Omni Wings", "Cape",
            "cp:1.50,rep:1.50,gold:1.30",
            "+50% CP/Rep · +30% Gold"),
        new("Cape", "Cape of Awe", "Cape",
            "exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+25% XP / CP / Gold / Rep"),
        new("Cape", "Celestial Pirate Commander's Cape", "Cape",
            "Undead:1.30,Chaos:1.30,Dragonkin:1.30,Human:1.30,Elemental:1.30",
            "+30% All Tagged Races"),
        new("Cape", "Archmaster of Awe Battle Wings", "Cape",
            "Undead:1.20,Chaos:1.20,Dragonkin:1.20,Human:1.20,Elemental:1.20",
            "+20% All Tagged Races"),
        new("Cape", "Hollowborn Shadows of the Oversoul", "Cape",
            "Human:1.40",
            "+40% Human"),

        // ---- PETS ----
        new("Pet", NoneItemName, "Pet", "", "No boost"),
        new("Pet", "Polly Roger", "Pet",
            "Undead:1.30,Chaos:1.30,Dragonkin:1.30,Human:1.30,Elemental:1.30",
            "+30% All Tagged Races"),
        new("Pet", "Mecha Binky Companion", "Pet",
            "Undead:1.30,Chaos:1.30,Dragonkin:1.30,Human:1.30,Elemental:1.30",
            "+30% All Tagged Races"),
        new("Pet", "Nightstar Companion", "Pet",
            "exp:1.30,cp:1.30,gold:1.30,rep:1.30",
            "+30% XP / CP / Gold / Rep"),
        new("Pet", "Evolved Void Orb Pet", "Pet",
            "exp:1.25,cp:1.25,gold:1.25,rep:1.25",
            "+25% XP / CP / Gold / Rep"),
        new("Pet", "Lazure's Awakened Skull", "Pet",
            "Human:1.30,gold:1.30",
            "+30% Human · +30% Gold")
    };

    public static IReadOnlyList<AqwBoostItemInfo> GetItemsForSlot(string slot) =>
        Items.Where(i => i.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();

    public static AqwBoostItemInfo? Find(string slot, string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return null;
        return Items.FirstOrDefault(i =>
            i.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase) &&
            i.Name.Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static AqwBoostItemInfo? FindByName(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return null;
        return Items.FirstOrDefault(i => i.Name.Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveMeta(GearSlotBoostSetup setup)
    {
        if (!string.IsNullOrWhiteSpace(setup.Meta))
            return setup.Meta.Trim();

        if (!string.IsNullOrWhiteSpace(setup.ItemName) &&
            !setup.ItemName.Equals(NoneItemName, StringComparison.OrdinalIgnoreCase))
        {
            var catalogItem = Find(setup.Slot, setup.ItemName) ?? FindByName(setup.ItemName);
            if (catalogItem != null)
                return catalogItem.Meta;
        }

        // Legacy fallback when only BoostType + Percent were set
        return World.StatEngine.BuildBoostMeta(setup.BoostType, setup.Percent);
    }

    public static string ResolveItemName(GearSlotBoostSetup setup)
    {
        if (!string.IsNullOrWhiteSpace(setup.ItemName) &&
            !setup.ItemName.Equals(NoneItemName, StringComparison.OrdinalIgnoreCase))
        {
            return setup.ItemName.Trim();
        }
        return !string.IsNullOrWhiteSpace(setup.BoostType) &&
               !setup.BoostType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
               setup.Percent > 0
            ? $"Boosted {setup.Slot} (+{setup.Percent}% {setup.BoostType})"
            : $"Simulated {setup.Slot}";
    }

    public static string ResolveCategory(GearSlotBoostSetup setup, string defaultCategory)
    {
        if (!string.IsNullOrWhiteSpace(setup.ItemName))
        {
            var item = Find(setup.Slot, setup.ItemName) ?? FindByName(setup.ItemName);
            if (item != null)
                return item.Category;
        }
        return defaultCategory;
    }
}
