using Skua.Core.Models;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Skills;
using SkuaSim.Core.Runtime;
using SkuaSim.Core.World;

namespace SkuaSim.Core.Api;

public sealed class CombatApi : ApiBase
{
    public CombatApi(SimContext ctx) : base(ctx) { }

    public bool EnableCounterHandler { get; set; } = true;
    public bool StopAttacking { get; set; }

    public void ApproachTarget() { }

    public bool Attack(int id)
    {
        if (StopAttacking)
            return false;
        var mon = World.FindMonster(null, mapId: id, currentCellOnly: true, aliveOnly: true)
                  ?? World.MonstersInCell().FirstOrDefault(m => m.Alive && (m.MapID == id || m.MonID == id));
        return World.Attack(mon);
    }

    public bool Attack(Monster monster) => monster != null && Attack(monster.MapID > 0 ? monster.MapID : monster.ID);

    public bool Attack(string name)
    {
        if (StopAttacking)
            return false;
        World.EnsureRequestedMonster(name);
        var mon = World.FindMonster(name, currentCellOnly: true, aliveOnly: true);
        return World.Attack(mon);
    }

    public bool SelectMonster(int id)
    {
        var mon = World.MonstersInCell().FirstOrDefault(m => m.MapID == id || m.MonID == id);
        World.SelectTarget(mon);
        return mon != null;
    }

    public bool SelectMonster(Monster monster) => monster != null && SelectMonster(monster.MapID > 0 ? monster.MapID : monster.ID);

    public bool SelectMonster(string name)
    {
        var mon = World.FindMonster(name, currentCellOnly: true, aliveOnly: false);
        World.SelectTarget(mon);
        return mon != null;
    }

    public bool AttackPlayer(string name) => false;

    public void CancelAutoAttack() => World.ClearTarget();

    public void CancelTarget() => World.ClearTarget();

    public void Exit()
    {
        World.ExitCombat();
        if (!SimWorld.Eq(World.Cell, "Wait"))
            Sleep(500);
    }

    public void UntargetSelf() { }
}

public sealed class KillApi : ApiBase
{
    public KillApi(SimContext ctx) : base(ctx) { }

    public void Player(string name) => Sleep(500);

    public void Monster(Monster monster) => Monster(monster, null);

    public void Monster(Monster monster, CancellationToken? token)
    {
        if (monster == null)
            return;
        KillLoop(() => World.MonstersInCell().FirstOrDefault(m => m.Alive && (m.MapID == monster.MapID || SimWorld.Eq(m.Name, monster.Name))), token);
    }

    public void Monster(string name) => Monster(name, null);

    public void Monster(string name, CancellationToken? token)
    {
        World.EnsureRequestedMonster(name);
        KillLoop(() => World.FindMonster(name, currentCellOnly: true, aliveOnly: true), token);
    }

    public void Monster(int id) => Monster(id, null);

    public void Monster(int id, CancellationToken? token)
    {
        KillLoop(() => World.MonstersInCell().FirstOrDefault(m => m.Alive && (m.MapID == id || m.MonID == id)), token);
    }

    public void ForItem(string name, string item, int quantity, bool tempItem) =>
        ForItems(new[] { name }, new[] { item }, new[] { quantity }, new[] { tempItem });

    public void ForItem(IEnumerable<string> names, string item, int quantity, bool tempItem) =>
        ForItems(names, new[] { item }, new[] { quantity }, new[] { tempItem });

    public void ForItems(string name, IEnumerable<string> items, IEnumerable<int> quantities, bool tempItems) =>
        ForItems(new[] { name }, items, quantities, tempItems);

    public void ForItems(string name, IEnumerable<string> items, IEnumerable<int> quantities, IEnumerable<bool> tempItems) =>
        ForItems(new[] { name }, items, quantities, tempItems);

    public void ForItems(IEnumerable<string> names, IEnumerable<string> items, IEnumerable<int> quantities, bool tempItems)
    {
        var itemList = items.ToList();
        ForItems(names, itemList, quantities, Enumerable.Repeat(tempItems, itemList.Count));
    }

    public void ForItems(IEnumerable<string> names, IEnumerable<string> items, IEnumerable<int> quantities, IEnumerable<bool> tempItems)
    {
        var nameList = names.ToList();
        var itemList = items.ToList();
        var qtyList = quantities.ToList();
        var tempList = tempItems.ToList();

        for (var i = 0; i < itemList.Count; i++)
        {
            var it = itemList[i];
            var q = i < qtyList.Count ? qtyList[i] : 1;
            var t = i < tempList.Count && tempList[i];
            World.NoteItemQuery(it, null, q, t);
            if (!t)
                World.PickupNames.Add(it);
        }

        bool HasAllRequired()
        {
            for (var i = 0; i < itemList.Count; i++)
            {
                var it = itemList[i];
                var q = i < qtyList.Count ? qtyList[i] : 1;
                var t = i < tempList.Count && tempList[i];
                World.NoteItemQuery(it, null, q, t);
                if (!t)
                    World.Pickup(it, null);
                var have = World.Quantity(t ? World.TempInventory : World.Inventory, it);
                if (have < q)
                    return false;
            }
            return true;
        }

        foreach (var n in nameList)
            World.EnsureRequestedMonster(n);

        var missingWarned = false;
        while (!Ctx.StopRequested && !HasAllRequired())
        {
            Ctx.ThrowIfStopping();
            var target = World.MonstersInCell().FirstOrDefault(m => m.Alive && nameList.Any(n => SimWorld.NameMatches(m.Name, n)));
            if (target == null)
            {
                var anywhere = World.AllMonsters().FirstOrDefault(m => nameList.Any(n => SimWorld.NameMatches(m.Name, n)));
                if (anywhere == null)
                {
                    if (!missingWarned)
                    {
                        missingWarned = true;
                        Ctx.Trace.Warn($"Kill.ForItems: monster [{string.Join(", ", nameList)}] does not exist on map '{World.MapName}'.");
                    }
                    return;
                }
                Sleep(500);
                continue;
            }
            KillOne(target, null);
        }
    }

    private void KillLoop(Func<SimMonster?> selector, CancellationToken? token)
    {
        var target = selector();
        if (target == null)
        {
            Sleep(250);
            return;
        }
        KillOne(target, token);
    }

    internal void KillOne(SimMonster target, CancellationToken? token)
    {
        while (!Ctx.StopRequested && token?.IsCancellationRequested != true && target.Alive)
        {
            Ctx.ThrowIfStopping();
            if (!World.Alive)
            {
                Sleep(500);
                continue;
            }
            if (!SimWorld.Eq(World.Cell, target.Cell))
                World.Jump(target.Cell, "Left", autoCorrect: true);

            World.Attack(target);
            Sleep(500);
        }
    }
}

public sealed class HuntApi : ApiBase
{
    private readonly KillApi _kill;

    public HuntApi(SimContext ctx, KillApi kill) : base(ctx) => _kill = kill;

    public void Monster(string name) => Monster(name, null);

    public void Monster(string name, CancellationToken? token)
    {
        World.EnsureRequestedMonster(name);
        var mon = FindAndJump(m => SimWorld.NameMatches(m.Name, name));
        if (mon != null)
            _kill.KillOne(mon, token);
        else
            Sleep(250);
    }

    public void Monster(int id) => Monster(id, null);

    public void Monster(int id, CancellationToken? token)
    {
        var mon = FindAndJump(m => m.MonID == id || m.MapID == id);
        if (mon != null)
            _kill.KillOne(mon, token);
        else
            Sleep(250);
    }

    public void Monster(Monster monster) => Monster(monster, null);

    public void Monster(Monster monster, CancellationToken? token)
    {
        if (monster == null)
            return;
        Monster(monster.MapID > 0 ? monster.MapID : monster.ID, token);
    }

    public void ForItem(string name, string item, int quantity, bool tempItem) =>
        ForItems(new[] { name }, new[] { item }, new[] { quantity }, new[] { tempItem });

    public void ForItem(IEnumerable<string> names, string item, int quantity, bool tempItem) =>
        ForItems(names, new[] { item }, new[] { quantity }, new[] { tempItem });

    public void ForItems(string name, IEnumerable<string> items, IEnumerable<int> quantities, bool tempItems) =>
        ForItems(new[] { name }, items, quantities, tempItems);

    public void ForItems(string name, IEnumerable<string> items, IEnumerable<int> quantities, IEnumerable<bool> tempItems) =>
        ForItems(new[] { name }, items, quantities, tempItems);

    public void ForItems(IEnumerable<string> names, IEnumerable<string> items, IEnumerable<int> quantities, bool tempItems)
    {
        var itemList = items.ToList();
        ForItems(names, itemList, quantities, Enumerable.Repeat(tempItems, itemList.Count));
    }

    public void ForItems(IEnumerable<string> names, IEnumerable<string> items, IEnumerable<int> quantities, IEnumerable<bool> tempItems)
    {
        var nameList = names.ToList();
        var itemList = items.ToList();
        var qtyList = quantities.ToList();
        var tempList = tempItems.ToList();

        for (var i = 0; i < itemList.Count; i++)
        {
            var it = itemList[i];
            var q = i < qtyList.Count ? qtyList[i] : 1;
            var t = i < tempList.Count && tempList[i];
            World.NoteItemQuery(it, null, q, t);
            if (!t)
                World.PickupNames.Add(it);
        }

        bool HasAllRequired()
        {
            for (var i = 0; i < itemList.Count; i++)
            {
                var it = itemList[i];
                var q = i < qtyList.Count ? qtyList[i] : 1;
                var t = i < tempList.Count && tempList[i];
                World.NoteItemQuery(it, null, q, t);
                if (!t)
                    World.Pickup(it, null);
                var have = World.Quantity(t ? World.TempInventory : World.Inventory, it);
                if (have < q)
                    return false;
            }
            return true;
        }

        foreach (var n in nameList)
            World.EnsureRequestedMonster(n);

        while (!Ctx.StopRequested && !HasAllRequired())
        {
            Ctx.ThrowIfStopping();
            var mon = FindAndJump(m => nameList.Any(n => SimWorld.NameMatches(m.Name, n)));
            if (mon == null)
            {
                if (!World.AllMonsters().Any(m => nameList.Any(n => SimWorld.NameMatches(m.Name, n))))
                {
                    Ctx.Trace.Warn($"Hunt.ForItems: monster [{string.Join(", ", nameList)}] does not exist on map '{World.MapName}'.");
                    return;
                }
                Sleep(500);
                continue;
            }
            _kill.KillOne(mon, null);
        }
    }

    public void WithPriority(string name, HuntPriorities priority) => Monster(name, null);
    public void WithPriority(string name, HuntPriorities priority, CancellationToken? token) => Monster(name, token);
    public void WithPriority(Monster monster, HuntPriorities priority) => Monster(monster, null);
    public void WithPriority(Monster monster, HuntPriorities priority, CancellationToken? token) => Monster(monster, token);
    public void WithPriority(int id, HuntPriorities priority) => Monster(id, null);
    public void WithPriority(int id, HuntPriorities priority, CancellationToken? token) => Monster(id, token);

    private SimMonster? FindAndJump(Func<SimMonster, bool> predicate)
    {
        var all = World.AllMonsters().Where(predicate).ToList();
        var target = all.FirstOrDefault(m => m.Alive && SimWorld.Eq(m.Cell, World.Cell))
                     ?? all.FirstOrDefault(m => m.Alive);
        if (target != null && !SimWorld.Eq(World.Cell, target.Cell))
            World.Jump(target.Cell, "Left", autoCorrect: true);
        return target;
    }
}

public sealed class SkillsApi : ApiBase
{
    public SkillsApi(SimContext ctx) : base(ctx) { }

    public int SkillTimeout { get; set; } = -1;
    public int SkillInterval { get; set; } = 100;
    public bool TimerRunning { get; private set; } = true;
    public SkillUseMode SkillUseMode { get; set; } = SkillUseMode.UseIfAvailable;

    public bool CanUseSkill(int index)
    {
        if (!World.Alive || index is < 0 or > 5)
            return false;
        var skills = World.Skills;
        if (skills.Length > index)
            return !skills[index].Locked && skills[index].IsOk;
        return true;
    }

    public bool UseSkill(int index)
    {
        if (!CanUseSkill(index))
            return false;
        World.RecordManualSkillUse(index);
        if (World.Target is { Alive: true } target)
            World.Attack(target);
        return true;
    }

    public void Resume() => TimerRunning = true;
    public void Pause() => TimerRunning = false;
    public void LoadAdvanced(string skills, int skillTimeout, SkillUseMode skillMode) => TimerRunning = true;
    public void LoadAdvanced(string className, bool autoEquip, ClassUseMode useMode)
    {
        if (autoEquip && !string.IsNullOrWhiteSpace(className))
        {
            var item = World.Inventory.FirstOrDefault(i => SimWorld.Eq(i.Name, className));
            if (item != null)
                World.Equip(item.ID);
        }
        TimerRunning = true;
    }
    public void LoadAdvanced(string className, string mode, bool autoEquip) => LoadAdvanced(className, autoEquip, ClassUseMode.Base);
    // Skua: StartAdvanced = LoadAdvanced + Start (the skill thread). CoreBots.EquipClass uses the class form.
    public void StartAdvanced(string skills, int skillTimeout, SkillUseMode skillMode) => LoadAdvanced(skills, skillTimeout, skillMode);
    public void StartAdvanced(string className, bool autoEquip, ClassUseMode useMode) => LoadAdvanced(className, autoEquip, useMode);
    public void StartAdvanced(string className, string mode, bool autoEquip) => LoadAdvanced(className, mode, autoEquip);
    public void Start() => TimerRunning = true;
    public void Stop() => TimerRunning = false;
    public void UseBaseProvider() { }
}
