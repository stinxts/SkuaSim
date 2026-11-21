using System.Globalization;
using System.Text.Json;
using Skua.Core.Models.Items;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.World;

public sealed partial class SimWorld
{
    /// <summary>
    /// The character as it is now, as a new scenario: level, XP, gold, ACs, class rank/CP, every inventory, bank,
    /// house and temp item (with enhancements and what is equipped), reputation, quest progress and location, starting
    /// at the current virtual time. Settings that are not character state (boosts, unlocks, options, maps, world rules,
    /// limits) are copied from the run's scenario. Lets runs be chained: level up, then run a story with that character.
    /// </summary>
    public Scenario ExportScenario(string? scriptName = null)
    {
        lock (Sync)
        {
            var source = _ctx.Scenario;
            var s = JsonSerializer.Deserialize<Scenario>(source.ToJson(), Scenario.JsonOptions) ?? new Scenario();
            s.Options = new Dictionary<string, JsonElement>(s.Options, StringComparer.OrdinalIgnoreCase);
            s.BotOptions = new Dictionary<string, JsonElement>(s.BotOptions, StringComparer.OrdinalIgnoreCase);
            s.Maps = new Dictionary<string, MapSetup>(s.Maps, StringComparer.OrdinalIgnoreCase);

            var baseName = string.IsNullOrWhiteSpace(source.Name) ? "Scenario" : source.Name;
            s.Name = string.IsNullOrWhiteSpace(scriptName) ? $"{baseName} (exported)" : $"{baseName} (after {scriptName})";
            var endUtc = _ctx.Clock.Now;
            s.Description = $"Exported by SkuaSim at the end of a run{(string.IsNullOrWhiteSpace(scriptName) ? "" : $" of {scriptName}")} " +
                            $"(virtual time {endUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC). Based on: {baseName}.";
            s.StartDate = new DateTime(endUtc.Ticks - endUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            s.StartAtCurrentTime = false;

            var p = s.Player;
            p.Level = Level;
            p.Xp = XP;
            p.Gold = Gold;
            p.Coins = Coins;
            p.IsMember = IsMember;
            p.Class = CurrentClass?.Name ?? p.Class;
            p.ClassRank = ClassRank;
            p.ClassPoints = ClassPoints > 0 ? ClassPoints : null;
            // Fixed stat overrides of the source stay as they were (null = computed by StatEngine).

            // Everything is exported as it is, including the basic gear made by "Complete Equipment" and enhancements
            // made at the starting level: the script did not re-enhance, so the next run must not either.
            s.Inventory = Inventory.Select(ToSetup).ToList();
            s.Bank = Bank.Select(ToSetup).ToList();
            s.House = House.Select(ToSetup).ToList();
            s.TempInventory = TempInventory.Select(t => new ItemSetup { Name = t.Name, Id = t.ID, Quantity = t.Quantity, Temp = true }).ToList();

            s.Factions = Factions
                .Select(f => new FactionSetup { Name = f.Name, Id = f.ID, Rank = f.Rank, TotalRep = f.TotalRep })
                .ToList();

            s.Quests = new QuestSetup
            {
                Completed = CompletedQuests.OrderBy(id => id).ToList(),
                Active = ActiveQuests.OrderBy(id => id).ToList(),
                DailiesCompleted = DailiesCompleted.OrderBy(id => id).ToList(),
                SlotProgress = SlotProgress.OrderBy(kv => kv.Key)
                    .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value)
            };

            s.Location = new LocationSetup
            {
                Map = string.IsNullOrWhiteSpace(MapName) ? source.Location.Map : MapName,
                Cell = Cell,
                Pad = Pad,
                LoggedIn = true
            };
            s.SourcePath = null;
            return s;
        }
    }

    private static ItemSetup ToSetup(InventoryItem i) => new()
    {
        Name = i.Name,
        Id = i.ID,
        Quantity = i.Quantity,
        Category = string.IsNullOrWhiteSpace(i.CategoryString) ? null : i.CategoryString,
        MaxStack = i.MaxStack > 0 ? i.MaxStack : null,
        Equipped = i.Equipped,
        Coins = i.Coins ? true : null,
        Member = i.Upgrade ? true : null,
        EnhancementLevel = i.EnhancementLevel > 0 ? i.EnhancementLevel : null,
        EnhancementPatternID = i.EnhancementPatternID > 0 ? i.EnhancementPatternID : null,
        ProcID = i.ProcID > 0 ? i.ProcID : null
    };
}
