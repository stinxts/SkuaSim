using Newtonsoft.Json;
using SkuaSim.Collector.Models;
using SkuaSim.Collector.Storage;

namespace SkuaSim.Core.Data;

/// <summary>One tester's shared data file found in <c>data\contrib\</c>.</summary>
public sealed record GameDataContribution(
    string FileName,
    string ContributorId,
    DateTime? ExportedUtc,
    long Kills,
    int Maps,
    string Status);

/// <summary>
/// Data shared by other testers. <c>skuasim import-data</c> checks each file the collector's "Export for Sharing" made
/// and stores it as <c>data\contrib\&lt;contributor&gt;.json</c> (the newest export of each contributor replaces the
/// older one: exports are cumulative, so adding both would count the same kills twice). When a simulation loads
/// gamedata.json, these files are merged into it in memory, in file-name order, with the collector's own merge
/// (kills, drops and samples add up), so the same files always give the same data. gamedata.json itself is never
/// changed.
/// </summary>
public static class GameDataContributions
{
    public const string FolderName = "contrib";

    public static string FolderFor(string gameDataPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameDataPath)) ?? ".", FolderName);

    /// <summary>Merges every contribution next to <paramref name="gameDataPath"/> into <paramref name="target"/>.</summary>
    public static List<GameDataContribution> MergeInto(GameDatabase target, string gameDataPath)
    {
        var used = new List<GameDataContribution>();
        var folder = FolderFor(gameDataPath);
        if (!Directory.Exists(folder))
            return used;

        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            try
            {
                var db = GameDatabaseStore.ReadSharedFile(file);
                var kills = db.Monsters.Values.Sum(m => m.KillCount);
                var maps = db.Maps.Count;
                GameDatabaseStore.MergeInto(target, db);
                used.Add(new GameDataContribution(name, db.ContributorId ?? "", db.ExportedUtc, kills, maps, "merged"));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                used.Add(new GameDataContribution(name, "", null, 0, 0, $"skipped: {ex.Message}"));
            }
        }
        return used;
    }

    public sealed record ImportResult(string Source, string? StoredAs, string Message);

    /// <summary>
    /// Checks a shared file (.zip from "Export for Sharing" or a gamedata.json) and stores it in the contrib folder.
    /// <paramref name="ownContributorId"/> (this PC's collector ID, when known) rejects one's own export, which is
    /// already in gamedata.json.
    /// </summary>
    public static ImportResult Import(string sourcePath, string gameDataPath, string? ownContributorId)
    {
        GameDatabase db;
        try
        {
            db = GameDatabaseStore.ReadSharedFile(sourcePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        {
            return new ImportResult(sourcePath, null, $"rejected: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(db.ContributorId))
            return new ImportResult(sourcePath, null,
                "rejected: no contributor ID (not made by \"Export for Sharing\" of collector 1.1.0 or newer); " +
                "without it a later export from the same tester would be counted twice");
        if (!string.IsNullOrWhiteSpace(ownContributorId) && string.Equals(db.ContributorId, ownContributorId, StringComparison.OrdinalIgnoreCase))
            return new ImportResult(sourcePath, null, "rejected: this is this PC's own export (already in gamedata.json)");

        var folder = FolderFor(gameDataPath);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"{db.ContributorId}.json");
        if (File.Exists(target))
        {
            try
            {
                var existing = GameDatabaseStore.ReadSharedFile(target);
                if (existing.ExportedUtc is { } old && db.ExportedUtc is { } incoming && incoming < old)
                    return new ImportResult(sourcePath, null,
                        $"rejected: an export from {old:yyyy-MM-dd} by the same contributor is already stored (this one is {incoming:yyyy-MM-dd})");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
            {
                // Unreadable stored copy: replace it.
            }
        }

        var json = JsonConvert.SerializeObject(db, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        });
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, target, overwrite: true);

        var kills = db.Monsters.Values.Sum(m => m.KillCount);
        var versions = db.CollectorVersions.Count == 0 ? "collector before 1.1.0" : string.Join(", ", db.CollectorVersions.Keys.OrderBy(v => v, StringComparer.Ordinal));
        return new ImportResult(sourcePath, target,
            $"stored: {db.Maps.Count:N0} maps, {db.Monsters.Count:N0} monsters, {kills:N0} kills, {db.Quests.Count:N0} quests, " +
            $"exported {db.ExportedUtc:yyyy-MM-dd} ({versions})");
    }

    /// <summary>This PC's collector contributor ID (VibeSkua/Skua data folders), or null.</summary>
    public static string? FindOwnContributorId(string userDataDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(userDataDirectory, "SkuaSim", GameDatabaseStore.ContributorIdFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua", "SkuaSim", GameDatabaseStore.ContributorIdFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua", "SkuaSim", GameDatabaseStore.ContributorIdFileName)
        };
        foreach (var file in candidates)
        {
            try
            {
                if (File.Exists(file))
                    return File.ReadAllText(file).Trim();
            }
            catch (IOException) { }
        }
        return null;
    }
}
