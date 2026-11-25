using System.Security.Cryptography;
using System.Text.Json;
using Newtonsoft.Json;
using SkuaSim.Collector.Models;

namespace SkuaSim.Core.Data;

/// <summary>One file of a data snapshot, with its size and SHA-256 (checked on every run).</summary>
public sealed class DataSnapshotFile
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary><c>snapshot.json</c>: what a snapshot holds and where it came from.</summary>
public sealed class DataSnapshotManifest
{
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int SchemaVersion { get; set; }
    public string? Note { get; set; }
    /// <summary>The live files the snapshot was made from (full path, size, SHA-256 at that moment).</summary>
    public List<DataSnapshotFile> Sources { get; set; } = new();
    /// <summary>Shared data files (data\contrib\) already merged into the snapshot's gamedata.json.</summary>
    public List<string> Contributions { get; set; } = new();
    public List<DataSnapshotFile> Files { get; set; } = new();
    public int Monsters { get; set; }
    public int Maps { get; set; }
    public int Items { get; set; }
    public int Quests { get; set; }
    public long CreditedKills { get; set; }
    public long LegacyKills { get; set; }
}

/// <summary>
/// A frozen copy of the data a simulation reads — the collector database with the shared data merged and migrated
/// (exactly what <see cref="GameDataCatalog"/> builds in memory), Skua's QuestData.json, the AE calendar and the AQW
/// server list — stored in
/// <c>data\snapshots\&lt;name&gt;\</c>. Scenarios that name it (<c>"dataSnapshot"</c>) or runs with <c>--data</c> read
/// only these files, so the regression suite gives the same results while the collector keeps writing gamedata.json.
/// A snapshot is never overwritten; make a new one to take in newer data.
/// </summary>
public sealed class DataSnapshot
{
    public const string FolderName = "snapshots";
    public const string ManifestFileName = "snapshot.json";
    public const string GameDataFileName = "gamedata.json";
    public const string QuestDataFileName = "QuestData.json";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private static readonly JsonSerializerSettings GameDataJson = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc
    };

    public string Directory { get; }
    public DataSnapshotManifest Manifest { get; }
    public string Name => Manifest.Name;
    public string GameDataPath => Path.Combine(Directory, GameDataFileName);
    public string QuestDataPath => Path.Combine(Directory, QuestDataFileName);
    /// <summary>Calendar path inside the snapshot (the file may be absent: the run then has no calendar, never the live one).</summary>
    public string CalendarPath => Path.Combine(Directory, AeCalendar.DefaultFileName);
    /// <summary>Server list inside the snapshot (may be absent: the run then uses the synthesized stand-in, never the live one).</summary>
    public string ServersPath => Path.Combine(Directory, AqwServerList.DefaultFileName);

    private DataSnapshot(string directory, DataSnapshotManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    /// <summary>The snapshots folder that belongs to a gamedata.json (<c>data\snapshots\</c>).</summary>
    public static string FolderFor(string gameDataPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameDataPath)) ?? ".", FolderName);

    /// <summary>
    /// The snapshot folder for a name (under <paramref name="snapshotsRoot"/>) or a path to a snapshot folder; null when
    /// neither holds a snapshot.json.
    /// </summary>
    public static string? ResolveDirectory(string nameOrPath, string? snapshotsRoot)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
            return null;
        var candidates = new List<string>();
        if (snapshotsRoot != null && IsValidName(nameOrPath))
            candidates.Add(Path.Combine(snapshotsRoot, nameOrPath));
        candidates.Add(Path.GetFullPath(nameOrPath));
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, ManifestFileName)));
    }

    public static DataSnapshot Load(string directory)
    {
        var full = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(full, ManifestFileName);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<DataSnapshotManifest>(File.ReadAllText(manifestPath), ManifestJson)
                       ?? throw new InvalidDataException($"Empty snapshot manifest: {manifestPath}");
        return new DataSnapshot(full, manifest);
    }

    /// <summary>A name usable as a folder: letters, digits, '-', '_', '.' (not starting with '.').</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name[0] != '.' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Freezes the data a run would read now into <c>&lt;snapshotsRoot&gt;\&lt;name&gt;\</c>. Fails if that folder
    /// already exists (snapshots are never overwritten). <paramref name="createdUtc"/> is metadata only.
    /// </summary>
    public static DataSnapshot Create(string name, string gameDataPath, string? questDataPath, string? calendarPath,
        string snapshotsRoot, DateTime createdUtc, string? note = null, string? serversPath = null)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"Invalid snapshot name '{name}' (use letters, digits, '-', '_' or '.').", nameof(name));
        if (!File.Exists(gameDataPath))
            throw new FileNotFoundException("gamedata.json not found.", gameDataPath);
        var dir = Path.GetFullPath(Path.Combine(snapshotsRoot, name));
        if (System.IO.Directory.Exists(dir))
            throw new IOException($"Snapshot '{name}' already exists at {dir}; snapshots are never overwritten (choose another name).");

        var sources = new List<DataSnapshotFile> { Describe(gameDataPath, Path.GetFullPath(gameDataPath)) };
        // Exactly what a live run would build in memory (contributions merged, migrated, drop rates recomputed).
        var catalog = GameDataCatalog.Load(gameDataPath, questDataPath);

        var tmp = dir + ".tmp";
        if (System.IO.Directory.Exists(tmp))
            System.IO.Directory.Delete(tmp, recursive: true);
        System.IO.Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, GameDataFileName), JsonConvert.SerializeObject(catalog.Captured, GameDataJson));
            if (catalog.QuestDataPath != null)
            {
                sources.Add(Describe(catalog.QuestDataPath, catalog.QuestDataPath));
                File.Copy(catalog.QuestDataPath, Path.Combine(tmp, QuestDataFileName));
            }
            if (!string.IsNullOrEmpty(calendarPath) && File.Exists(calendarPath))
            {
                sources.Add(Describe(calendarPath, Path.GetFullPath(calendarPath)));
                File.Copy(calendarPath, Path.Combine(tmp, AeCalendar.DefaultFileName));
            }
            if (!string.IsNullOrEmpty(serversPath) && File.Exists(serversPath))
            {
                sources.Add(Describe(serversPath, Path.GetFullPath(serversPath)));
                File.Copy(serversPath, Path.Combine(tmp, AqwServerList.DefaultFileName));
            }

            var monsters = catalog.Captured.Monsters.Values;
            var manifest = new DataSnapshotManifest
            {
                Name = name,
                CreatedUtc = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc),
                SchemaVersion = catalog.Captured.SchemaVersion,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
                Sources = sources,
                Contributions = catalog.Contributions.Where(c => c.Status == "merged").Select(c => c.FileName).ToList(),
                Monsters = catalog.Captured.Monsters.Count,
                Maps = catalog.Captured.Maps.Count,
                Items = catalog.Captured.Items.Count,
                Quests = catalog.Quests.Count,
                CreditedKills = monsters.Sum(m => (long)m.CreditedKills),
                LegacyKills = monsters.Sum(m => (long)m.KillCount),
                Files = System.IO.Directory.GetFiles(tmp).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => Describe(f, Path.GetFileName(f))).ToList()
            };
            File.WriteAllText(Path.Combine(tmp, ManifestFileName), System.Text.Json.JsonSerializer.Serialize(manifest, ManifestJson));
            System.IO.Directory.Move(tmp, dir);
        }
        catch
        {
            try { System.IO.Directory.Delete(tmp, recursive: true); } catch { }
            throw;
        }
        return Load(dir);
    }

    /// <summary>Files that are missing or no longer match the manifest (empty = intact).</summary>
    public List<string> Verify()
    {
        var problems = new List<string>();
        foreach (var f in Manifest.Files)
        {
            var path = Path.Combine(Directory, f.Name);
            if (!File.Exists(path))
                problems.Add($"{f.Name} is missing");
            else if (!string.Equals(Sha256Of(path), f.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{f.Name} was changed after the snapshot was made");
        }
        return problems;
    }

    /// <summary>One line for reports: name, creation date and the gamedata fingerprint.</summary>
    public string Describe()
    {
        var game = Manifest.Files.FirstOrDefault(f => f.Name == GameDataFileName);
        return $"{Name} (created {Manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC, schema v{Manifest.SchemaVersion}"
               + (game != null ? $", gamedata {game.Sha256[..12]}" : "") + ")";
    }

    private static DataSnapshotFile Describe(string path, string name) =>
        new() { Name = name, Bytes = new FileInfo(path).Length, Sha256 = Sha256Of(path) };

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
