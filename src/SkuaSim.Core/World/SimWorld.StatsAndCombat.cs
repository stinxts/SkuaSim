using Skua.Core.Models.Items;
using Skua.Core.Models.Players;
using Skua.Core.Models.Shops;
using Skua.Core.Models.Skills;

namespace SkuaSim.Core.World;

public sealed partial class SimWorld
{
    private long _lastOutOfCombatRegenCheckMs;

    public ComputedCombatProfile CombatProfile { get; private set; } = null!;
    public PlayerStats Stats => CombatProfile.Stats;
    public SkillInfo[] Skills { get; private set; } = Array.Empty<SkillInfo>();

    public long PlayerAttacks { get; private set; }
    public long PlayerCrits { get; private set; }
    public long PlayerAttacksTaken { get; private set; }
    public long PlayerDodges { get; private set; }
    public long TotalDamageDealt { get; private set; }
    public long TotalHealingDone { get; private set; }

    public bool XpBoostActive { get; set; }
    public bool ClassBoostActive { get; set; }
    public bool GoldBoostActive { get; set; }
    public bool RepBoostActive { get; set; }
    public bool ServerBoostActive { get; set; }
    public int ClassPoints { get; private set; }

    // Boosts add up (see RewardBoosts): highest gear bonus of the type + consumable +100 % + server +100 %.
    public double EffectiveXpMultiplier =>
        RewardBoosts.Combine(CombatProfile?.GearBoosts.XpMultiplier ?? 1.0, XpBoostActive, ServerBoostActive || CalendarBoosts.Xp);
    public double EffectiveClassPointsMultiplier =>
        RewardBoosts.Combine(CombatProfile?.GearBoosts.ClassPointsMultiplier ?? 1.0, ClassBoostActive, ServerBoostActive || CalendarBoosts.ClassPoints);
    public double EffectiveGoldMultiplier =>
        RewardBoosts.Combine(CombatProfile?.GearBoosts.GoldMultiplier ?? 1.0, GoldBoostActive, ServerBoostActive || CalendarBoosts.Gold);
    public double EffectiveRepMultiplier =>
        RewardBoosts.Combine(CombatProfile?.GearBoosts.RepMultiplier ?? 1.0, RepBoostActive, ServerBoostActive || CalendarBoosts.Rep);

    // ---------------------------------------------------------------- AE calendar server boosts

    private Data.ServerBoostState _calendarBoosts;
    private long _calendarValidUntilMs = long.MinValue;
    private bool _calendarCoverageWarned;

    /// <summary>Server-wide boosts the AE calendar lists for the current virtual time (none when not enabled).</summary>
    public Data.ServerBoostState CalendarBoosts
    {
        get
        {
            var cal = _ctx.Calendar;
            if (cal == null)
                return default;
            if (Now < _calendarValidUntilMs)
                return _calendarBoosts;

            var nowUtc = _ctx.Clock.Now;
            var previous = _calendarBoosts;
            _calendarBoosts = cal.ServerBoostsAt(nowUtc);
            var next = cal.NextChangeAfter(nowUtc);
            _calendarValidUntilMs = next is { } n ? (long)Math.Ceiling((n - _ctx.Clock.Epoch).TotalMilliseconds) : long.MaxValue;

            if (!cal.Covers(nowUtc) && !_calendarCoverageWarned)
            {
                _calendarCoverageWarned = true;
                Trace.Warn($"AE calendar has no data for {Data.AeCalendar.ToEastern(nowUtc):yyyy-MM-dd} (server time): no calendar boosts are applied outside its coverage. Update it with 'skuasim calendar-update'.");
            }
            if (_calendarBoosts != previous || !_calendarLogged)
            {
                _calendarLogged = true;
                var titles = string.Join(", ", cal.ActiveAt(nowUtc).Where(e => e.Kind == Data.AeCalendarKinds.ServerBoost).Select(e => e.Title));
                Trace.World($"AE calendar server boosts at {Data.AeCalendar.ToEastern(nowUtc):yyyy-MM-dd HH:mm} server time: {_calendarBoosts}{(titles.Length > 0 ? $" ({titles})" : "")}.");
                CalendarBoostLog.Add($"{Data.AeCalendar.ToEastern(nowUtc):yyyy-MM-dd HH:mm} ET: {_calendarBoosts}{(titles.Length > 0 ? $" ({titles})" : "")}");
            }
            return _calendarBoosts;
        }
    }

    private bool _calendarLogged;

    /// <summary>Calendar boost changes seen during the run (for the report).</summary>
    public List<string> CalendarBoostLog { get; } = new();

    internal static string InferDefaultItemGroup(string? category) =>
        EquipGroup(category) == "weapon" ? "Weapon" : "None";

    public void RecalculateStats(bool healToFullOnIncrease = false)
    {
        var equipped = Inventory.Where(i => i.Equipped).ToList();
        var className = CurrentClass?.Name ?? _ctx.Scenario.Player.Class ?? "Warrior";
        CombatProfile = StatEngine.ComputeProfile(Level, className, Math.Max(1, ClassRank), equipped, _ctx.Catalog);
        Skills = CombatProfile.Skills.ToArray();

        if (!_ctx.Scenario.Player.MaxHealth.HasValue)
        {
            var oldMax = MaxHealth;
            MaxHealth = Math.Max(100, CombatProfile.ComputedMaxHealth);
            if (Alive)
            {
                if (healToFullOnIncrease && MaxHealth > oldMax)
                    Health = MaxHealth;
                else
                    Health = Math.Min(MaxHealth, Math.Max(1, Health));
            }
        }
    }

    internal void ApplyNaturalOutOfCombatRegen()
    {
        if (!Alive || InCombat || Health >= MaxHealth)
        {
            _lastOutOfCombatRegenCheckMs = Now;
            return;
        }

        var baselineMs = Math.Max(InCombatUntilMs, _lastOutOfCombatRegenCheckMs);
        var elapsedSec = (Now - baselineMs) / 1000.0;
        _lastOutOfCombatRegenCheckMs = Now;
        if (elapsedSec <= 0.5)
            return;

        // AQW out-of-combat passive regeneration (~6% MaxHP/s after leaving combat)
        var healed = (int)Math.Round(MaxHealth * 0.06 * Math.Min(30.0, elapsedSec));
        if (healed > 0)
            Health = Math.Min(MaxHealth, Health + healed);
    }

    public void RecordManualSkillUse(int index)
    {
        lock (Sync)
        {
            if (index >= 0 && index < Skills.Length)
                Skills[index].LastUse = Now;
        }
    }

    public bool EnhanceItem(int targetItemId, int shopEnhItemId, int shopId)
    {
        lock (Sync)
        {
            var target = Inventory.FirstOrDefault(i => i.ID == targetItemId);
            if (target == null)
            {
                Trace.World($"Enhance failed: item #{targetItemId} is not in inventory.");
                return false;
            }

            // Look up enhancement in CurrentShop, ShopCache, or captured Shops from Catalog
            ShopItem? enhShopItem = null;
            if (CurrentShop?.ID == shopId)
                enhShopItem = CurrentShop.Items.FirstOrDefault(i => i.ID == shopEnhItemId || i.ShopItemID == shopEnhItemId);
            enhShopItem ??= ShopCache.FirstOrDefault(s => s.ID == shopId)?.Items
                .FirstOrDefault(i => i.ID == shopEnhItemId || i.ShopItemID == shopEnhItemId);

            int patternId = enhShopItem?.EnhancementPatternID ?? 0;
            int procId = enhShopItem?.ProcID ?? 0;
            int enhLvl = enhShopItem?.Level ?? 0;
            int cost = enhShopItem?.Cost ?? 0;
            bool upgrade = enhShopItem?.Upgrade ?? false;
            string enhName = enhShopItem?.Name ?? $"Enhancement #{shopEnhItemId}";

            if (enhShopItem == null && _ctx.Catalog.Captured.Shops.TryGetValue(shopId, out var capShop))
            {
                var csi = capShop.Items.FirstOrDefault(i => i.ID == shopEnhItemId || i.ShopItemID == shopEnhItemId);
                if (csi != null)
                {
                    patternId = csi.EnhancementPatternID;
                    procId = csi.ProcID;
                    enhLvl = csi.Level;
                    cost = csi.Cost;
                    upgrade = csi.Upgrade;
                    enhName = csi.Name;
                }
            }

            if (enhShopItem == null && patternId == 0)
            {
                // Fallback inference by standard AQW enhancement shop IDs when shop wasn't captured
                patternId = InferEnhPatternFromShopId(shopId);
                if (patternId <= 0 && !Rules.LenientShops)
                {
                    Trace.World($"Enhance failed: shop #{shopId} enhancement #{shopEnhItemId} not found.");
                    return false;
                }
                enhLvl = Math.Clamp(Level, 1, 100);
                Trace.Synthesize($"enhance:{shopId}", $"Enhancement #{shopEnhItemId} in shop {shopId} was synthesized at Lv {enhLvl} (Pattern {patternId}).");
            }

            if (patternId <= 0)
                patternId = InferPatternFromEnhancementName(enhName, shopId);

            if (upgrade && !IsMember)
            {
                Trace.World($"Enhance '{target.Name}' failed: member-only enhancement.");
                return false;
            }
            if (enhLvl > Level)
            {
                Trace.World($"Enhance '{target.Name}' failed: requires Lv {enhLvl} (player is Lv {Level}).");
                return false;
            }
            if (cost > 0 && Gold < cost)
            {
                Trace.World($"Enhance '{target.Name}' failed: not enough gold ({cost} needed).");
                return false;
            }

            Gold = Math.Max(0, Gold - cost);
            target.EnhancementPatternID = Math.Max(1, patternId);
            target.EnhancementLevel = Math.Clamp(enhLvl > 0 ? enhLvl : Level, 1, Level);
            if (procId > 0)
                target.ProcID = procId;

            RecalculateStats();
            Trace.World($"Enhanced {target.Name} with {enhName} (Pattern {target.EnhancementPatternID}, Lv {target.EnhancementLevel}, MaxHP {MaxHealth}, DPS {(int)CombatProfile.EffectiveDps}).");
            return true;
        }
    }

    private int RollCombatDamageTick(SimMonster? target, out bool isCrit)
    {
        var profile = CombatProfile;
        var raceMult = profile.GearBoosts.GetRaceMultiplier(target?.Race);

        if (_ctx.Scenario.Player.DamagePerSecond.HasValue)
        {
            var overrideDps = Math.Max(1, _ctx.Scenario.Player.DamagePerSecond.Value);
            isCrit = _ctx.Rng.NextDouble() < profile.Stats.CriticalChance;
            var dmgAllMult = profile.GearBoosts.DamageAllMultiplier;
            return Math.Max(1, (int)Math.Round(overrideDps * dmgAllMult * raceMult * (0.85 + _ctx.Rng.NextDouble() * 0.3)));
        }

        var critChance = profile.Stats.CriticalChance;
        var critMult = Math.Max(1.0, profile.Stats.CriticalMultiplier);
        isCrit = _ctx.Rng.NextDouble() < critChance;

        var expectedCritScale = 1.0 + critChance * (critMult - 1.0);
        var baseTick = (profile.EffectiveDps * raceMult) / Math.Max(1.0, expectedCritScale);
        var variance = baseTick * (0.85 + _ctx.Rng.NextDouble() * 0.30);
        return Math.Max(1, (int)Math.Round(isCrit ? variance * critMult : variance));
    }

    // AQW class rank table = the faction rank table: (5·r·(r+1))² → 900, 3,600, 10,000, 22,500, 44,100, 78,400, 129,600,
    // 202,500, 302,500 (AQW Wiki; the old table here had 9,000 … 108,000 and made rank 9 → 10 take twice as long).
    private static readonly int[] ClassRankThresholds = Enumerable.Range(0, 11).Select(r => RepForRank(r)).ToArray();

    internal void GainClassPoints(int baseCp)
    {
        if (baseCp <= 0 || ClassRank >= 10)
            return;

        var gained = (int)Math.Round(baseCp * EffectiveClassPointsMultiplier);
        if (gained <= 0)
            return;

        if (ClassPoints < ClassRankThresholds[Math.Clamp(ClassRank, 1, 10)])
            ClassPoints = ClassRankThresholds[Math.Clamp(ClassRank, 1, 10)];

        ClassPoints = Math.Min(302500, ClassPoints + gained);
        while (ClassRank < 10 && ClassPoints >= ClassRankThresholds[ClassRank + 1])
        {
            ClassRank++;
            if (CurrentClass != null)
                CurrentClass.Quantity = ClassPoints;
            RecalculateStats();
            Trace.World($"Class rank up! {CurrentClass?.Name ?? "Class"} reached Rank {ClassRank}.");
        }
        if (CurrentClass != null && CurrentClass.Quantity < ClassPoints)
            CurrentClass.Quantity = ClassPoints;
    }

    internal void ApplyScenarioBoostsAndEnhancements()
    {
        var s = _ctx.Scenario;
        XpBoostActive = s.Boosts.XpBoost;
        ClassBoostActive = s.Boosts.ClassPointsBoost;
        GoldBoostActive = s.Boosts.GoldBoost;
        RepBoostActive = s.Boosts.RepBoost;
        ServerBoostActive = s.Boosts.ServerBoost;

        var hasAwe = s.Enhancements.UnlockedAwe.Count > 0;
        var hasForge = s.Enhancements.UnlockedForge.Count > 0;

        void MarkQuestUnlocked(int questId)
        {
            if (questId <= 0)
                return;
            CompletedQuests.Add(questId);
            var q = _ctx.Catalog.FindQuest(questId);
            if (q != null && q.Slot >= 0)
                SlotProgress[q.Slot] = Math.Max(SlotProgress.GetValueOrDefault(q.Slot), q.Value);
        }

        if (hasAwe)
            MarkQuestUnlocked(Scenarios.EnhancementUnlockCatalog.AweUnlockQuestId);

        foreach (var name in s.Enhancements.UnlockedAwe.Concat(s.Enhancements.UnlockedForge))
        {
            var info = Scenarios.EnhancementUnlockCatalog.Find(name);
            if (info == null)
                continue;
            MarkQuestUnlocked(info.QuestId);

            if (!string.IsNullOrWhiteSpace(info.RequiredFaction) && info.RequiredFactionRank > 0)
            {
                var fac = Factions.FirstOrDefault(f => Eq(f.Name, info.RequiredFaction));
                if (fac == null)
                {
                    fac = new Skua.Core.Models.Factions.Faction
                    {
                        ID = StableHash.Of(info.RequiredFaction) % 1000 + 100,
                        Name = info.RequiredFaction
                    };
                    SetFactionRep(fac, RepForRank(info.RequiredFactionRank));
                    Factions.Add(fac);
                }
                else if (fac.Rank < info.RequiredFactionRank)
                {
                    SetFactionRep(fac, Math.Max(fac.TotalRep, RepForRank(info.RequiredFactionRank)));
                }
            }
        }

        // Apply real AQW boosted items & unlocked Awe/Forge starting loadouts to equipped slots
        var hasEnhUnlocks = hasAwe || hasForge;
        // Complete equipment: empty slots get basic gear enhanced at the character's level (scenario items untouched).
        var fill = s.Player.CompleteEquipment;
        var enhanceNew = hasEnhUnlocks || fill;

        var className = CurrentClass?.Name ?? s.Player.Class ?? "Warrior";
        var (basePat, weapPat, procId, helmPat, capePat) = StatEngine.PickStartingEnhancementsForClass(className, _ctx.Catalog, s.Enhancements);
        var enhLvl = Math.Clamp(Level, 1, 100);
        var filled = new List<string>();

        if (CurrentClass != null && enhanceNew && CurrentClass.EnhancementLevel <= 0)
        {
            CurrentClass.EnhancementPatternID = basePat;
            CurrentClass.EnhancementLevel = enhLvl;
            filled.Add($"class '{CurrentClass.Name}' enhanced");
        }

        void EnsureVirtualGearSlot(string slot, string category, int patId, int proc)
        {
            var group = EquipGroup(category);
            var equippedItem = Inventory.FirstOrDefault(i => i.Equipped && EquipGroup(i.CategoryString) == group);
            var slotBoost = s.Boosts.EquipmentBoosts.FirstOrDefault(b => b.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
            var boostMeta = slotBoost != null ? Scenarios.AqwBoostGearCatalog.ResolveMeta(slotBoost) : string.Empty;
            // Pets and cosmetic armors carry no enhancement (the class is the stat "armor" slot), so they are only
            // created for a boost; a weapon always exists in AQW.
            var create = !string.IsNullOrEmpty(boostMeta) || ((hasEnhUnlocks || fill) && slot is not ("Pet" or "Armor")) || slot == "Weapon";

            if (equippedItem == null && create)
            {
                if (slotBoost == null)
                    filled.Add(enhanceNew && patId > 0 && slot != "Pet" ? $"{slot} (Lv {enhLvl} enhanced)" : $"{slot} (unenhanced)");
                var itemName = slotBoost != null
                    ? Scenarios.AqwBoostGearCatalog.ResolveItemName(slotBoost)
                    : $"Simulated {slot}";
                var itemCat = slotBoost != null
                    ? Scenarios.AqwBoostGearCatalog.ResolveCategory(slotBoost, category)
                    : category;
                var catalogMatch = _ctx.Catalog.FindItem(itemName);
                var info = new Data.ItemInfo
                {
                    ID = catalogMatch?.ID ?? SynthesizedItemId("simgear:" + itemName),
                    Name = itemName,
                    MaxStack = 1,
                    Category = itemCat,
                    ItemGroup = slot == "Weapon" ? "Weapon" : "None",
                    Meta = boostMeta,
                    EnhancementPatternID = enhanceNew ? patId : 0,
                    ProcID = hasEnhUnlocks ? proc : 0,
                    Source = catalogMatch != null ? Data.DataSource.Captured : Data.DataSource.Scenario
                };
                equippedItem = NewInventoryItem(info, 1);
                equippedItem.EnhancementPatternID = enhanceNew ? patId : 0;
                equippedItem.EnhancementLevel = enhanceNew && patId > 0 ? enhLvl : 0;
                equippedItem.ProcID = hasEnhUnlocks ? proc : 0;
                equippedItem.Meta = boostMeta;
                Inventory.Add(equippedItem);
                EquipInternal(equippedItem, log: false);
            }
            else if (equippedItem != null)
            {
                if (!string.IsNullOrEmpty(boostMeta))
                    equippedItem.Meta = string.IsNullOrWhiteSpace(equippedItem.Meta) ? boostMeta : $"{equippedItem.Meta},{boostMeta}";
                if (hasEnhUnlocks && equippedItem.EnhancementLevel <= 0 && patId > 0)
                {
                    equippedItem.EnhancementPatternID = patId;
                    equippedItem.EnhancementLevel = enhLvl;
                    if (proc > 0 && equippedItem.ProcID <= 0)
                        equippedItem.ProcID = proc;
                }
            }
        }

        EnsureVirtualGearSlot("Weapon", "Sword", weapPat, procId);
        EnsureVirtualGearSlot("Armor", "Armor", 0, 0);
        EnsureVirtualGearSlot("Helm", "Helm", helmPat, 0);
        EnsureVirtualGearSlot("Cape", "Cape", capePat, 0);
        EnsureVirtualGearSlot("Pet", "Pet", 0, 0);

        if (filled.Count > 0)
            Trace.Synthesize("gear:complete", $"Equipment completed for an ordinary equipped player: {string.Join(", ", filled)} " +
                                              $"(class pattern {basePat}; set player.completeEquipment=false to keep only the scenario's items).");
    }

    private void ApplyCombatSustain(int damageDealt)
    {
        if (!Alive || _ctx.Scenario.Player.RegenPercentPerSecond.HasValue)
            return;

        double healTotal = damageDealt * CombatProfile.LifestealRatio;
        if (CombatProfile.CombatHealPerSecond > 0)
        {
            var healRoll = CombatProfile.CombatHealPerSecond * (0.90 + _ctx.Rng.NextDouble() * 0.20);
            if (_ctx.Rng.NextDouble() < CombatProfile.Stats.CriticalChance)
                healRoll *= Math.Min(2.0, Math.Max(1.2, CombatProfile.Stats.CriticalMultiplier * 0.7));
            healTotal += healRoll;
        }

        var healed = (int)Math.Round(healTotal);
        if (healed > 0)
        {
            var gained = Math.Min(MaxHealth - Health, healed);
            if (gained > 0)
            {
                Health += gained;
                TotalHealingDone += gained;
            }
        }
    }

    private void ApplyMonsterRetaliationDamage(int avgDamage)
    {
        PlayerAttacksTaken++;

        // Evasion roll applies in realistic combat mode
        if (!_ctx.Scenario.Player.DamagePerSecond.HasValue || !_ctx.Scenario.Player.MaxHealth.HasValue)
        {
            if (_ctx.Rng.NextDouble() < CombatProfile.Stats.EvasionChance)
            {
                PlayerDodges++;
                return;
            }
        }

        var rawDealt = (int)(avgDamage * (0.8 + _ctx.Rng.NextDouble() * 0.4));
        int dealt;
        if (!_ctx.Scenario.Player.DamagePerSecond.HasValue || !_ctx.Scenario.Player.MaxHealth.HasValue)
        {
            var mitigated = rawDealt * (1.0 - CombatProfile.DamageReduction) * CombatProfile.IncomingDamageMultiplier;
            dealt = Math.Max(1, (int)Math.Round(mitigated));
        }
        else
        {
            dealt = rawDealt;
        }

        Health -= dealt;
    }

    private static int InferEnhPatternFromShopId(int shopId) => shopId switch
    {
        141 or 768 => 6, // Wizard / Lucky Lv 1-50+ Shops (default to Lucky/Wizard)
        147 or 763 => 9,
        45 => 2, // Fighter
        46 => 3, // Thief
        47 => 7, // Healer
        2142 or 2143 or 2164 => 10, // Forge shops
        _ => 9
    };

    private static int InferPatternFromEnhancementName(string name, int shopId)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("fighter")) return 2;
        if (n.Contains("thief")) return 3;
        if (n.Contains("hybrid")) return 5;
        if (n.Contains("wizard")) return 6;
        if (n.Contains("healer")) return 7;
        if (n.Contains("spellbreaker")) return 8;
        if (n.Contains("lucky") || n.Contains("luck")) return 9;
        if (n.Contains("absolution")) return 11;
        if (n.Contains("avarice")) return 12;
        if (n.Contains("vainglory")) return 24;
        if (n.Contains("vim")) return 25;
        if (n.Contains("examen")) return 26;
        if (n.Contains("pneuma")) return 27;
        if (n.Contains("anima")) return 28;
        if (n.Contains("penitence")) return 29;
        if (n.Contains("lament")) return 30;
        if (n.Contains("hearty")) return 32;
        if (n.Contains("forge")) return 10;
        return InferEnhPatternFromShopId(shopId);
    }
}
