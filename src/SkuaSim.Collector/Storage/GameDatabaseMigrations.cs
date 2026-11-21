using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Storage;

/// <summary>
/// Offline quest knowledge used by the v1 → v2 migration (from Skua's QuestData.json and the captured quests):
/// which items are quest rewards and which quests require each item.
/// </summary>
public sealed class QuestCatalogHints
{
    public HashSet<int> RewardItemIds { get; } = new();

    /// <summary>Item ID -> quests that require it (same information as the item's server-side "sReqQuests").</summary>
    public Dictionary<int, HashSet<int>> RequiredByQuests { get; } = new();

    public void AddRequirement(int itemId, int questId)
    {
        if (itemId <= 0 || questId <= 0)
            return;
        if (!RequiredByQuests.TryGetValue(itemId, out var set))
            RequiredByQuests[itemId] = set = new HashSet<int>();
        set.Add(questId);
    }

    /// <summary>Reads Rewards/SimpleRewards/Requirements item IDs from Skua's QuestData.json (missing file = empty hints).</summary>
    public static QuestCatalogHints FromQuestDataFile(string? path)
    {
        var hints = new QuestCatalogHints();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return hints;

        try
        {
            using var reader = new JsonTextReader(new StreamReader(path));
            if (JToken.ReadFrom(reader) is not JArray quests)
                return hints;

            foreach (var quest in quests.OfType<JObject>())
            {
                var questId = quest.Value<int?>("ID") ?? 0;
                foreach (var key in new[] { "Rewards", "SimpleRewards" })
                    if (quest[key] is JArray rewards)
                        foreach (var r in rewards.OfType<JObject>())
                            if ((r.Value<int?>("ItemID") ?? r.Value<int?>("ID") ?? 0) is > 0 and var id)
                                hints.RewardItemIds.Add(id);

                if (quest["Requirements"] is JArray reqs)
                    foreach (var r in reqs.OfType<JObject>())
                        hints.AddRequirement(r.Value<int?>("ItemID") ?? r.Value<int?>("ID") ?? 0, questId);
            }
        }
        catch
        {
            // A corrupt/unreadable QuestData.json only means less precise migration hints.
        }
        return hints;
    }
}

public static class GameDatabaseMigrations
{
    /// <summary>Runs every pending migration in order (v1 → v2 → v3). Returns the log lines added.</summary>
    public static List<string> MigrateToCurrent(GameDatabase db, QuestCatalogHints? externalHints)
    {
        var lines = new List<string>();
        if (db.SchemaVersion < 2)
            lines.Add(MigrateToV2(db, externalHints));
        if (db.SchemaVersion < 3)
            lines.Add(MigrateToV3(db));
        return lines;
    }

    /// <summary>
    /// v2 → v3. Deletes nothing. New fields (kill rewards, respawn, quest-set kills, peak players) start empty. The
    /// per-quest legacy kill counts are frozen and quest-gated rates are recomputed with the maximum of them instead of
    /// the sum (the sum counted a kill once per active required quest). The never-populated room cap fields
    /// (MaxPlayers*) are dropped by the new model.
    /// </summary>
    public static string MigrateToV3(GameDatabase db)
    {
        if (db.SchemaVersion >= 3)
            return "already v3";

        int recomputed = 0;
        foreach (var monster in db.Monsters.Values)
        {
            if (monster.KillsWhileQuestActive.Count == 0)
                continue;
            DropRates.Recompute(monster);
            recomputed++;
        }

        db.SchemaVersion = 3;
        var summary = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z v2->v3: no records removed; recomputed quest-gated rates of " +
                      $"{recomputed} monster(s) with legacy per-quest kill counts (max instead of sum, which double-counted " +
                      "kills made with several required quests active). New: kill rewards, respawn times, quest-set kills, peak players.";
        db.MigrationLog.Add(summary);
        return summary;
    }

    /// <summary>
    /// Migrates a v1 database to v2 in place. The only records deleted are provably wrong ones: drops with more drop
    /// events than the monster has kills (impossible; caused by the old "first monster in the cell" fallback).
    /// <para>
    /// Quest-reward contamination in other v1 drops cannot be separated reliably — many quest rewards are also real
    /// drops (e.g. Shurpu Blaze Token from Xan, ~78 %) — so those records are kept; new captures no longer credit
    /// turn-in rewards to monsters.
    /// </para>
    /// <para>
    /// Temporary items required by a quest become quest-gated with an "unverified" rate until measured with the quest
    /// active (the old all-kills rate was diluted, e.g. 0.04 %). This mirrors the server: live samples showed that
    /// "sReqQuests" is set for temporary quest-required items and empty for permanent ones (Treasure Chest, Chaos War
    /// Medal), so permanent items keep their all-kills rate. Returns a one-line summary that is also logged.
    /// </para>
    /// </summary>
    public static string MigrateToV2(GameDatabase db, QuestCatalogHints? externalHints)
    {
        if (db.SchemaVersion >= 2)
            return "already v2";

        var hints = externalHints ?? new QuestCatalogHints();
        foreach (var quest in db.Quests.Values)
            foreach (var req in quest.Requirements)
                hints.AddRequirement(req.ItemID, quest.ID);

        bool IsTemp(int itemId, CapturedDropStat drop) =>
            drop.Temp || (db.Items.TryGetValue(itemId, out var item) && item.Temp);

        int impossibleRemoved = 0, gatedMarked = 0;
        foreach (var monster in db.Monsters.Values)
        {
            foreach (var (itemId, drop) in monster.Drops.ToList())
            {
                if (drop.DropEvents > monster.KillCount)
                {
                    monster.Drops.Remove(itemId);
                    impossibleRemoved++;
                    continue;
                }

                if (hints.RequiredByQuests.TryGetValue(itemId, out var requiredBy) && IsTemp(itemId, drop))
                {
                    drop.QuestGated = true;
                    drop.RequiredQuestIds.UnionWith(requiredBy);
                    gatedMarked++;
                }
            }
            DropRates.Recompute(monster);
        }

        foreach (var (itemId, requiredBy) in hints.RequiredByQuests)
            if (db.Items.TryGetValue(itemId, out var item) && item.Temp)
                item.RequiredByQuestIds.UnionWith(requiredBy);

        db.SchemaVersion = 2;
        var summary = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z v1->v2: removed {impossibleRemoved} impossible drop record(s) " +
                      $"(more drops than kills); marked {gatedMarked} temporary quest-required drop(s) as quest-gated " +
                      "(rate unverified until measured with the quest active). Other quest-reward contamination was kept (not separable).";
        db.MigrationLog.Add(summary);
        return summary;
    }
}
