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

    private int RollCombatDamageTick(out bool isCrit)
    {
        if (_ctx.Scenario.Player.DamagePerSecond.HasValue)
        {
            var overrideDps = Math.Max(1, _ctx.Scenario.Player.DamagePerSecond.Value);
            isCrit = _ctx.Rng.NextDouble() < CombatProfile.Stats.CriticalChance;
            return Math.Max(1, (int)(overrideDps * (0.85 + _ctx.Rng.NextDouble() * 0.3)));
        }

        var critChance = CombatProfile.Stats.CriticalChance;
        var critMult = Math.Max(1.0, CombatProfile.Stats.CriticalMultiplier);
        isCrit = _ctx.Rng.NextDouble() < critChance;

        var expectedCritScale = 1.0 + critChance * (critMult - 1.0);
        var baseTick = CombatProfile.EffectiveDps / Math.Max(1.0, expectedCritScale);
        var variance = baseTick * (0.85 + _ctx.Rng.NextDouble() * 0.30);
        return Math.Max(1, (int)Math.Round(isCrit ? variance * critMult : variance));
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
