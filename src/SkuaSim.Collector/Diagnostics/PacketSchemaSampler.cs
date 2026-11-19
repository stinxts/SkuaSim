using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SkuaSim.Collector.Diagnostics;

/// <summary>
/// Persistent, cross-process-safe diagnostic sampler that accumulates strictly anonymized sample payloads
/// per combat/boss/event packet category into `packet_schemas.json` across sessions (never touching `gamedata.json`).
/// Deduplicates by (MapContext + structural shape) so new maps, Ultra Bosses, and packet variants accumulate cleanly
/// without unbounded file growth. Strips all player usernames, entity/account IDs, chat text, and session tokens.
/// </summary>
public sealed class PacketSchemaSampler
{
    public const int MaxSamplesPerCategory = 12;

    private static readonly Regex PlayerRefRegex = new(@"\bp:(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> RedactedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "strUsername", "uoName", "unm", "strName", "guild", "strGuild", "chat", "sToken", "token",
        "userId", "charId", "intCharID"
    };

    private static readonly HashSet<string> EntityIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "uid", "casterId"
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, List<PacketSchemaSample>> _pendingBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _seenShapesByCategory = new(StringComparer.OrdinalIgnoreCase);
    private int _sessionSamplesCaptured;
    private bool _dirty;

    public bool Enabled { get; set; }

    public int TotalSamplesCaptured
    {
        get
        {
            lock (_sync)
                return _sessionSamplesCaptured;
        }
    }

    public bool HasPendingFlush
    {
        get
        {
            lock (_sync)
                return _dirty;
        }
    }

    public void TryCapture(
        string category,
        string cmd,
        JObject dataObj,
        int selfEntityId,
        string? selfUsername,
        string? mapContext = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(category) || dataObj == null)
            return;

        lock (_sync)
        {
            if (!_seenShapesByCategory.TryGetValue(category, out var seenShapes))
            {
                seenShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _seenShapesByCategory[category] = seenShapes;
            }

            if (seenShapes.Count >= MaxSamplesPerCategory)
                return;

            var playerAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sanitized = (JObject)SanitizeToken(dataObj.DeepClone(), selfEntityId, selfUsername, playerAliasMap, parentKey: null);
            var normalizedMap = (mapContext ?? string.Empty).Trim().ToLowerInvariant();
            var shapeKey = ComputeShapeKey(cmd, normalizedMap, sanitized);

            if (!seenShapes.Add(shapeKey))
                return;

            if (!_pendingBuckets.TryGetValue(category, out var list))
            {
                list = new List<PacketSchemaSample>();
                _pendingBuckets[category] = list;
            }

            list.Add(new PacketSchemaSample
            {
                CapturedUtc = DateTime.UtcNow,
                Command = cmd,
                MapContext = normalizedMap,
                ShapeKey = shapeKey,
                Payload = sanitized
            });
            _sessionSamplesCaptured++;
            _dirty = true;
        }
    }

    public void FlushToFiles(string primarySchemaPath, IEnumerable<string>? mirrorSchemaPaths = null)
    {
        Dictionary<string, List<PacketSchemaSample>> pendingSnapshot;
        lock (_sync)
        {
            if (!_dirty || _pendingBuckets.Count == 0)
                return;

            pendingSnapshot = new Dictionary<string, List<PacketSchemaSample>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, list) in _pendingBuckets)
            {
                if (list.Count > 0)
                    pendingSnapshot[cat] = new List<PacketSchemaSample>(list);
            }
            _pendingBuckets.Clear();
            _dirty = false;
        }

        if (pendingSnapshot.Count == 0)
            return;

        var mutexName = BuildMutexName(primarySchemaPath);
        using var mutex = new Mutex(false, mutexName);
        var lockAcquired = false;

        try
        {
            try
            {
                lockAcquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                lockAcquired = true;
            }

            var existingDoc = LoadExistingDocument(primarySchemaPath);
            var existing = existingDoc.Categories;

            // Retroactively re-sanitize any legacy samples already on disk (e.g. redacting legacy casterId/uid)
            // and index their structural shapes so merging across sessions never duplicates them.
            var existingShapes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, list) in existing)
            {
                var shapeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cleanedList = new List<PacketSchemaSample>(list.Count);
                foreach (var sample in list)
                {
                    if (sample?.Payload == null)
                        continue;

                    var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sample.Payload = (JObject)SanitizeToken(sample.Payload, 0, null, aliasMap, parentKey: null);
                    sample.MapContext = (sample.MapContext ?? string.Empty).Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(sample.ShapeKey))
                        sample.ShapeKey = ComputeShapeKey(sample.Command, sample.MapContext, sample.Payload);

                    if (shapeSet.Add(sample.ShapeKey) && cleanedList.Count < MaxSamplesPerCategory)
                        cleanedList.Add(sample);
                }
                existing[cat] = cleanedList;
                existingShapes[cat] = shapeSet;
            }

            // Merge new session samples into persistent categories across sessions & clients
            foreach (var (cat, samples) in pendingSnapshot)
            {
                if (!existing.TryGetValue(cat, out var dst))
                {
                    dst = new List<PacketSchemaSample>();
                    existing[cat] = dst;
                }
                if (!existingShapes.TryGetValue(cat, out var shapeSet))
                {
                    shapeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    existingShapes[cat] = shapeSet;
                }

                foreach (var s in samples)
                {
                    if (dst.Count >= MaxSamplesPerCategory)
                        break;
                    var key = string.IsNullOrWhiteSpace(s.ShapeKey)
                        ? ComputeShapeKey(s.Command, s.MapContext, s.Payload)
                        : s.ShapeKey;
                    if (shapeSet.Add(key))
                    {
                        s.ShapeKey = key;
                        dst.Add(s);
                    }
                }
            }

            var doc = new PacketSchemaDocument
            {
                Note = "Anonymized AQW packet samples accumulated across sessions for SkuaSim protocol calibration. Contains zero usernames or account/entity IDs.",
                LastUpdatedUtc = DateTime.UtcNow,
                TotalFlushesMerged = existingDoc.TotalFlushesMerged + 1,
                Categories = existing
            };

            var serialized = JsonConvert.SerializeObject(doc, Formatting.Indented);
            WriteAtomic(primarySchemaPath, serialized);
            if (mirrorSchemaPaths != null)
            {
                foreach (var mirror in mirrorSchemaPaths)
                {
                    if (string.IsNullOrWhiteSpace(mirror) ||
                        string.Equals(Path.GetFullPath(mirror), Path.GetFullPath(primarySchemaPath), StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { WriteAtomic(mirror, serialized); } catch { }
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private static PacketSchemaDocument LoadExistingDocument(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                var doc = JsonConvert.DeserializeObject<PacketSchemaDocument>(json);
                if (doc != null)
                {
                    doc.Categories = new Dictionary<string, List<PacketSchemaSample>>(
                        doc.Categories ?? new Dictionary<string, List<PacketSchemaSample>>(),
                        StringComparer.OrdinalIgnoreCase);
                    return doc;
                }
            }
        }
        catch { }

        return new PacketSchemaDocument();
    }

    private static string ComputeShapeKey(string cmd, string mapContext, JObject payload)
    {
        var sb = new StringBuilder();
        sb.Append(cmd.ToLowerInvariant()).Append('|').Append(mapContext).Append('|');

        var topKeys = payload.Properties().Select(p => p.Name).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        sb.Append(string.Join(',', topKeys));

        if (payload["sara"] is JArray saraArr)
        {
            var saraTypes = saraArr.OfType<JObject>()
                .Select(e => e["actionResult"] as JObject ?? e)
                .Select(a => $"{a["type"] ?? a["typ"] ?? "?"}:{(a["cInf"]?.ToString()?.StartsWith("m:", StringComparison.OrdinalIgnoreCase) == true ? "m" : "p")}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            sb.Append("|sara=").Append(string.Join(',', saraTypes));
        }

        if (payload["a"] is JArray aArr)
        {
            var auraShapes = aArr.OfType<JObject>()
                .Select(a => $"{a["cmd"] ?? "act"}:{(a["cInf"]?.ToString()?.StartsWith("m:", StringComparison.OrdinalIgnoreCase) == true ? "m" : "p")}->{(a["tInf"]?.ToString()?.StartsWith("m:", StringComparison.OrdinalIgnoreCase) == true ? "m" : "p")}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            sb.Append("|a=").Append(string.Join(',', auraShapes));
        }

        if (payload["anims"] is JArray animArr)
        {
            var animShapes = animArr.OfType<JObject>()
                .Select(a =>
                {
                    var hasMsg = !string.IsNullOrWhiteSpace(a["msg"]?.ToString());
                    var animStr = a["animStr"]?.ToString() ?? string.Empty;
                    return $"{animStr}{(hasMsg ? "+msg" : string.Empty)}";
                })
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            sb.Append("|anims=").Append(string.Join(',', animShapes));
        }

        if (payload["args"] is JObject argsObj && argsObj["zoneSet"] != null)
        {
            sb.Append("|zone=").Append(argsObj["zoneSet"]?.ToString());
        }

        if (payload["strES"] != null || payload["sES"] != null)
        {
            sb.Append("|es=").Append(payload["strES"] ?? payload["sES"]);
        }

        return sb.ToString();
    }

    private static string BuildMutexName(string filePath)
    {
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "Local\\SkuaSim_Schemas_" + Convert.ToHexString(hash)[..16];
    }

    private static void WriteAtomic(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, filePath, overwrite: true);
    }

    private static JToken SanitizeToken(
        JToken token,
        int selfEntityId,
        string? selfUsername,
        Dictionary<string, string> playerAliasMap,
        string? parentKey)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
            {
                var src = (JObject)token;
                var dst = new JObject();
                var isPlayerDict = string.Equals(parentKey, "p", StringComparison.OrdinalIgnoreCase);

                foreach (var prop in src.Properties())
                {
                    var key = prop.Name;
                    if (isPlayerDict)
                    {
                        key = AnonymizePlayerName(prop.Name, selfUsername, playerAliasMap);
                    }
                    else if (RedactedKeys.Contains(prop.Name))
                    {
                        dst[key] = "<REDACTED>";
                        continue;
                    }
                    else if (EntityIdKeys.Contains(prop.Name))
                    {
                        // Keep small monster map IDs when casterType is explicitly "m"; redact all player entity IDs
                        if (string.Equals(prop.Name, "casterId", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(src["casterType"]?.ToString(), "m", StringComparison.OrdinalIgnoreCase))
                        {
                            dst[key] = prop.Value;
                            continue;
                        }

                        // Already redacted placeholder? Preserve it on re-sanitization passes.
                        if (prop.Value.Type == JTokenType.String)
                        {
                            var strVal = prop.Value.Value<string>() ?? string.Empty;
                            if (strVal.StartsWith('<') && strVal.EndsWith('>'))
                            {
                                dst[key] = strVal;
                                continue;
                            }
                        }

                        var rawId = prop.Value.Type == JTokenType.Integer
                            ? prop.Value.Value<int>()
                            : (int.TryParse(prop.Value.ToString(), out var parsedId) ? parsedId : 0);

                        if (selfEntityId > 0 && rawId == selfEntityId)
                            dst[key] = "<SELF_ID>";
                        else if (rawId > 0)
                        {
                            var aliasKey = "uid:" + rawId;
                            if (!playerAliasMap.TryGetValue(aliasKey, out var alias))
                            {
                                alias = $"<ALLY_{playerAliasMap.Count + 1}_ID>";
                                playerAliasMap[aliasKey] = alias;
                            }
                            dst[key] = alias;
                        }
                        else
                            dst[key] = "<REDACTED_ID>";
                        continue;
                    }

                    dst[key] = SanitizeToken(prop.Value, selfEntityId, selfUsername, playerAliasMap, prop.Name);
                }
                return dst;
            }
            case JTokenType.Array:
            {
                var arr = (JArray)token;
                var dst = new JArray();
                // Keep arrays compact (max 8 items per sample)
                var limit = Math.Min(arr.Count, 8);
                for (var i = 0; i < limit; i++)
                    dst.Add(SanitizeToken(arr[i], selfEntityId, selfUsername, playerAliasMap, parentKey));
                return dst;
            }
            case JTokenType.String:
            {
                var s = token.Value<string>() ?? string.Empty;
                var sanitized = SanitizeStringValue(s, selfEntityId, selfUsername, playerAliasMap);
                return new JValue(sanitized);
            }
            default:
                return token;
        }
    }

    private static string SanitizeStringValue(
        string value,
        int selfEntityId,
        string? selfUsername,
        Dictionary<string, string> playerAliasMap)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = PlayerRefRegex.Replace(value, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var uid))
            {
                if (selfEntityId > 0 && uid == selfEntityId)
                    return "p:<SELF>";
                var key = "uid:" + uid;
                if (!playerAliasMap.TryGetValue(key, out var alias))
                {
                    alias = $"p:<ALLY_{playerAliasMap.Count + 1}>";
                    playerAliasMap[key] = alias;
                }
                return alias;
            }
            return "p:<PLAYER>";
        });

        if (!string.IsNullOrWhiteSpace(selfUsername) &&
            result.Contains(selfUsername, StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(result, Regex.Escape(selfUsername), "<SELF>", RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string AnonymizePlayerName(
        string rawName,
        string? selfUsername,
        Dictionary<string, string> playerAliasMap)
    {
        if (rawName.StartsWith('<') && rawName.EndsWith('>'))
            return rawName;

        if (!string.IsNullOrWhiteSpace(selfUsername) &&
            string.Equals(rawName, selfUsername, StringComparison.OrdinalIgnoreCase))
            return "<SELF>";

        var key = "name:" + rawName;
        if (!playerAliasMap.TryGetValue(key, out var alias))
        {
            alias = $"<ALLY_{playerAliasMap.Count + 1}>";
            playerAliasMap[key] = alias;
        }
        return alias;
    }
}

public sealed class PacketSchemaDocument
{
    public string Note { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public int TotalFlushesMerged { get; set; }
    public Dictionary<string, List<PacketSchemaSample>> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PacketSchemaSample
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string Command { get; set; } = string.Empty;
    public string MapContext { get; set; } = string.Empty;
    public string ShapeKey { get; set; } = string.Empty;
    public JObject Payload { get; set; } = new();
}
