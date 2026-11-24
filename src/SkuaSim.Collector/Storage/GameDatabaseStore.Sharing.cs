using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Storage;

/// <summary>
/// Sharing collected data between testers: the collector exports a sanitized copy (<see cref="ExportForSharing"/>),
/// SkuaSim reads it back (<see cref="ReadSharedFile"/>) and merges it in memory with the local data
/// (<see cref="MergeInto"/>; the same merge the collector uses between clients).
/// </summary>
public sealed partial class GameDatabaseStore
{
    /// <summary>File next to gamedata.json holding this install's random contributor ID (see <see cref="GameDatabase.ContributorId"/>).</summary>
    public const string ContributorIdFileName = "contributor_id.txt";

    /// <summary>
    /// Writes <c>skuasim_share_&lt;date&gt;.zip</c> with a sanitized copy of gamedata.json (+ packet_schemas.json when
    /// present) and a manifest. Sanitized: every date is cut to the day (no play times), the migration log (local
    /// file names) is dropped, and a random per-install contributor ID is added so a later export from the same
    /// tester replaces this one instead of counting its kills twice. The file never had account names, inventories,
    /// chat or IPs.
    /// </summary>
    public string ExportForSharing(string exportDirectory)
    {
        Directory.CreateDirectory(exportDirectory);
        var db = LoadCurrent();
        var dataDir = Path.GetDirectoryName(PrimaryFilePath) ?? ".";
        db.ContributorId = GetOrCreateContributorId(dataDir);
        db.ExportedUtc = DateTime.UtcNow.Date;
        db.MigrationLog = new List<string>();

        var zipPath = Path.Combine(exportDirectory, $"skuasim_share_{DateTime.UtcNow:yyyy-MM-dd_HHmmss}.zip");
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "gamedata.json", JsonConvert.SerializeObject(db, SharedJsonSettings));

            var schemas = Path.Combine(dataDir, "packet_schemas.json");
            if (File.Exists(schemas))
            {
                try { WriteEntry(archive, "packet_schemas.json", File.ReadAllText(schemas, Encoding.UTF8)); }
                catch { /* being written by another client: skip it */ }
            }

            var manifest = new
            {
                Kind = "skuasim-shared-data",
                db.SchemaVersion,
                db.ContributorId,
                ExportedUtc = db.ExportedUtc,
                ExportedBy = CollectorInfo.Version,
                db.CollectorVersions,
                Summary = new
                {
                    Maps = db.Maps.Count,
                    Monsters = db.Monsters.Count,
                    Kills = db.Monsters.Values.Sum(m => m.AllKillsObserved),
                    Items = db.Items.Count,
                    Shops = db.Shops.Count,
                    Quests = db.Quests.Count,
                    Classes = db.Classes.Count
                }
            };
            WriteEntry(archive, "manifest.json", JsonConvert.SerializeObject(manifest, SharedJsonSettings));
        }
        return zipPath;
    }

    /// <summary>
    /// Reads a gamedata.json, or a .zip made by <see cref="ExportForSharing"/> / the old "Export .zip", and brings it to
    /// the current schema in memory. Throws <see cref="InvalidDataException"/> when the file is not collector data or
    /// comes from a newer schema than this build understands.
    /// </summary>
    public static GameDatabase ReadSharedFile(string path, QuestCatalogHints? hints = null)
    {
        string json;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("gamedata.json")
                        ?? throw new InvalidDataException("the .zip has no gamedata.json");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        else
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }

        GameDatabase? db;
        try { db = JsonConvert.DeserializeObject<GameDatabase>(json, JsonSettings); }
        catch (JsonException ex) { throw new InvalidDataException($"not a gamedata.json ({ex.Message})"); }
        if (db == null || (db.Maps.Count == 0 && db.Monsters.Count == 0 && db.Items.Count == 0 && db.Quests.Count == 0))
            throw new InvalidDataException("no collected data in the file");
        if (db.SchemaVersion > GameDatabase.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"schema v{db.SchemaVersion} is newer than this SkuaSim build (v{GameDatabase.CurrentSchemaVersion}); update SkuaSim");

        NormalizeLoadedDatabase(db);
        if (db.SchemaVersion < GameDatabase.CurrentSchemaVersion)
            GameDatabaseMigrations.MigrateToCurrent(db, hints);
        return db;
    }

    /// <summary>Writes a database file atomically (temp file + move), as the collector does.</summary>
    public static void WriteFile(string path, GameDatabase db) => SaveToDiskAtomic(path, db);

    /// <summary>The install's random contributor ID, created on first use (a GUID; not derived from anything personal).</summary>
    public static string GetOrCreateContributorId(string dataDirectory)
    {
        var file = Path.Combine(dataDirectory, ContributorIdFileName);
        try
        {
            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (Guid.TryParse(existing, out _))
                    return existing;
            }
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(file, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    private static readonly JsonSerializerSettings SharedJsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        Converters = { new DayOnlyDateTimeConverter() }
    };

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Writes every date as its day only (00:00 UTC), so a shared file does not show when someone played.</summary>
    private sealed class DayOnlyDateTimeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(DateTime) || objectType == typeof(DateTime?);

        public override bool CanRead => false;

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
            throw new NotSupportedException();

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is DateTime dt)
                writer.WriteValue(DateTime.SpecifyKind(dt.ToUniversalTime().Date, DateTimeKind.Utc));
            else
                writer.WriteNull();
        }
    }
}
