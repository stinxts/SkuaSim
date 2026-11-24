namespace SkuaSim.Core.World;

/// <summary>
/// PvP maps: moving by cell number (client packet <c>mtcid</c>, used by CoreBots.PvPMove) and NPC matches with their
/// reward (<see cref="PvpMatchRules"/>). The character always plays team 0: scripts join a private room alone, and every
/// collected Bludrut Brawl event so far is team 0.
/// </summary>
public sealed partial class SimWorld
{
    private PvpMatchRules.Rule? _pvpRule;
    private int _pvpPoints;
    private bool _pvpMatchOver;
    private readonly Dictionary<string, int> _pvpKills = new(StringComparer.OrdinalIgnoreCase);

    public int PvpMatchesWon { get; private set; }
    public int PvpRewardsGiven { get; private set; }

    /// <summary>Starts a fresh match when a PvP map is joined (called under <see cref="Sync"/> from Join).</summary>
    private void ResetPvpMatchLocked()
    {
        _pvpRule = PvpMatchRules.For(MapName);
        _pvpPoints = 0;
        _pvpMatchOver = false;
        _pvpKills.Clear();
        if (_pvpRule != null)
            Trace.World($"PvP match started on {MapName}-{RoomNumber} (team 0; reward: {_pvpRule.RewardItem}, {_pvpRule.Source}).");
    }

    /// <summary>True when the item is the reward of the current map's PvP match (never rolled as a monster drop).</summary>
    private bool IsPvpRewardItem(int itemId, string? itemName) =>
        _pvpRule != null && (itemId == _pvpRule.RewardItemId || Eq(itemName, _pvpRule.RewardItem));

    /// <summary>Counts an enemy kill; the enemy captain's death wins the match and grants the reward. Under <see cref="Sync"/>.</summary>
    private void OnPvpMonsterDeathLocked(SimMonster monster)
    {
        if (_pvpRule is not { } rule || _pvpMatchOver)
            return;

        if (rule.PointsPerEnemy.TryGetValue(monster.Name, out var points))
        {
            _pvpPoints += points;
            _pvpKills[monster.Name] = _pvpKills.GetValueOrDefault(monster.Name) + 1;
            return;
        }
        if (!Eq(monster.Name, rule.EnemyCaptain))
            return;

        _pvpMatchOver = true;
        PvpMatchesWon++;
        var reward = Math.Min(rule.MaxReward, rule.WinBonus + _pvpPoints);
        var kills = _pvpKills.Count == 0 ? "captain only" : string.Join(", ", _pvpKills.Select(k => $"{k.Value}x {k.Key}"));
        Trace.World($"PvP match won on {MapName} ({kills}; {_pvpPoints} points): {reward}x {rule.RewardItem}.");
        SendServerString("server", ServerMessageTexts.PvpTeamAVictory);
        // The server adds the reward straight to the inventory ("addItems"), it is not a drop to pick up.
        if (AddToInventory(ResolveItem(rule.RewardItem, rule.RewardItemId), reward, "PvP win"))
            PvpRewardsGiven += reward;
    }

    /// <summary>
    /// Client packet <c>%xt%zm%mtcid%room%n%</c>: move through the door to cell number n. The number → cell name map
    /// comes from the collector (<c>CapturedMap.CellNumbers</c>); an unknown number does not move the character.
    /// </summary>
    public void MoveToCellNumber(int number)
    {
        lock (Sync)
        {
            if (Map == null || !MapLoaded)
                return;
            var cell = Map.Data?.CellForNumber(number);
            if (cell == null)
            {
                Trace.Warn($"mtcid {number} on map '{MapName}': cell number not collected, the character stays in {Cell}. " +
                           "Play this map with the collector to learn its cell numbers.");
                return;
            }
            JumpInternal(cell, "Spawn", raiseEvents: true);
        }
    }
}
