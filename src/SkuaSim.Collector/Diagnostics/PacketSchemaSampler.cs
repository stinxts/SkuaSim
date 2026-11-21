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
        "userId", "charId", "intCharID",
        // Personal inventory state carried by addItems/getDrop (per-character item record ID, owned quantity, bank flag)
        "CharItemID", "iQtyNow", "bBank",
        // The player's own house layout
        "houseData"
    };

    /// <summary>String (str) server packets sampled for protocol verification. Their arguments are numbers/cells only.</summary>
    public static readonly IReadOnlySet<string> ProtocolStringCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "respawnMon", "resTimed"
    };

    private static readonly HashSet<string> EntityIdKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "uid", "casterId"
    };

    /// <summary>Unknown commands only get a few value-free skeleton samples each.</summary>
    public const int MaxSkeletonSamplesPerCommand = 3;

    /// <summary>
    /// Protocol packets sampled in full (anonymized) to verify the real key names the parsers rely on
    /// (Phase 3.5 Step 1). They describe the world (maps, monsters, rewards, quests, drops), never the player's own
    /// inventory/bank/social data.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtocolCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "moveToArea", "addGoldExp", "dropItem", "addItems", "ccqr", "getQuests", "monsterDrops",
        // Monster respawn/update candidates (exact name to be confirmed by the command census)
        "mtls", "respawnMon",
        // Sell-back gold (CharItemID / iQtyNow are redacted)
        "sellItem",
        // XP table (intLevel, intExpToLevel)
        "levelUp"
    };

    /// <summary>
    /// Commands whose name suggests personal or social data. They are counted by the census but never sampled,
    /// not even as value-free skeletons.
    /// </summary>
    private static readonly Regex PersonalCommandRegex = new(
        "inv|bank|house|friend|chat|whisper|message|msg|guild|mail|login|user|party|ignore|char|account|password|report|trade",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Keys removed from moveToArea before sampling: the other players present in the room.</summary>
    private static readonly string[] RoomPlayerKeys = { "uoBranch", "uoBranchs", "players" };

    private readonly object _sync = new();
    private readonly Dictionary<string, List<PacketSchemaSample>> _pendingBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _seenShapesByCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _seenKeySetsByCategory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map-independent key signature of a payload: sorted top-level keys, plus the keys of nested objects whose keys are
    /// names (not IDs), e.g. "cmd,id,intExp,intGold,typ" or "QuestID,rewardObj{FactionID,iCP,iRep},...". Key names only.
    /// </summary>
    public static string KeySignature(JObject payload)
    {
        var parts = new List<string>();
        foreach (var prop in payload.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (prop.Value is JObject nested && nested.Properties().Any() && nested.Properties().All(p => !char.IsDigit(p.Name[0])))
                parts.Add($"{prop.Name}{{{string.Join(",", nested.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}}}");
            else
                parts.Add(prop.Name);
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// Counts the key signature of a protocol packet in the command census ("keys:addGoldExp:bonusGold,cmd,..."): key
    /// names and counts only, never values. Reveals new fields (e.g. class points) even when samples are full.
    /// </summary>
    public void CountKeySet(string cmd, JObject dataObj)
    {
        if (Enabled && dataObj != null && cmd != "moveToArea" && (ProtocolCommands.Contains(cmd) || KeyCensusExtraCommands.Contains(cmd)))
            CountCommand($"keys:{cmd}:{KeySignature(dataObj)}");
    }

    /// <summary>Non-sampled commands whose key names are counted too (class points / selling / factions).</summary>
    private static readonly HashSet<string> KeyCensusExtraCommands = new(StringComparer.Ordinal)
    {
        "updateClass", "addFaction", "stu"
    };
    private readonly Dictionary<string, long> _pendingCommandCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _commandsSeenThisSession = new(StringComparer.Ordinal);
    private long _countsSinceFlush;
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
        CaptureCore(category, cmd, dataObj, selfEntityId, selfUsername, mapContext, MaxSamplesPerCategory, skeletonOnly: false);
    }

    /// <summary>
    /// Counts one server command by name only (no payload). Persisted as <see cref="PacketSchemaDocument.CommandCounts"/>
    /// so unhandled packets (e.g. the monster respawn packet) can be discovered without recording their content.
    /// </summary>
    public void CountCommand(string commandName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(commandName))
            return;

        lock (_sync)
        {
            _pendingCommandCounts[commandName] = _pendingCommandCounts.GetValueOrDefault(commandName) + 1;
            // Mark dirty on a new command name or periodically, not on every packet, to avoid rewriting the file constantly.
            if (_commandsSeenThisSession.Add(commandName) || ++_countsSinceFlush >= 500)
                _dirty = true;
        }
    }

    /// <summary>
    /// Samples a JSON server packet for protocol verification: allowlisted world packets in full (anonymized),
    /// any other non-personal command as a value-free skeleton (key names only). Personal commands are never sampled.
    /// </summary>
    public void TryCaptureProtocol(string cmd, JObject dataObj, bool handledByCollector, int selfEntityId, string? selfUsername, string? mapContext)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(cmd) || dataObj == null)
            return;

        if (ProtocolCommands.Contains(cmd))
        {
            var payload = dataObj;
            if (cmd == "moveToArea")
            {
                // Never sample the player's own house (layout + owner-specific area name).
                if (IsHousePayload(dataObj, mapContext))
                    return;
                payload = (JObject)dataObj.DeepClone();
                foreach (var key in RoomPlayerKeys)
                {
                    if (payload[key] is JArray players)
                        payload[key] = $"<OMITTED {players.Count} room player(s)>";
                    else if (payload[key] != null)
                        payload[key] = "<OMITTED room players>";
                }
            }
            CaptureCore("proto_" + cmd, cmd, payload, selfEntityId, selfUsername, mapContext, MaxSamplesPerCategory, skeletonOnly: false);
            return;
        }

        if (handledByCollector || PersonalCommandRegex.IsMatch(cmd))
            return;

        CaptureCore("skeleton_" + cmd, cmd, dataObj, selfEntityId, selfUsername, mapContext: null, MaxSkeletonSamplesPerCommand, skeletonOnly: true);
    }

    /// <summary>
    /// Samples an allowlisted string (str) server packet, e.g. ["respawnMon", "-1", "3"], as
    /// <c>{ "cmd": ..., "args": [...] }</c>. Other str packets (chat, warnings) are never sampled.
    /// </summary>
    public void TryCaptureStringPacket(JArray parts, int selfEntityId, string? selfUsername, string? mapContext)
    {
        if (!Enabled || parts == null || parts.Count == 0 || parts[0]?.Type != JTokenType.String)
            return;
        var cmd = parts[0]!.ToString();
        if (!ProtocolStringCommands.Contains(cmd))
            return;

        var payload = new JObject
        {
            ["cmd"] = cmd,
            ["args"] = new JArray(parts.Skip(1).Select(p => p.DeepClone()))
        };
        CaptureCore("proto_str_" + cmd, cmd, payload, selfEntityId, selfUsername, mapContext, MaxSamplesPerCategory, skeletonOnly: false);
    }

    private static bool IsHousePayload(JObject payload, string? mapContext) =>
        string.Equals((mapContext ?? string.Empty).Trim(), "house", StringComparison.OrdinalIgnoreCase) ||
        payload["houseData"] != null ||
        string.Equals(payload["strMapName"]?.ToString(), "house", StringComparison.OrdinalIgnoreCase);

    private void CaptureCore(
        string category,
        string cmd,
        JObject dataObj,
        int selfEntityId,
        string? selfUsername,
        string? mapContext,
        int maxSamples,
        bool skeletonOnly)
    {
        lock (_sync)
        {
            if (!_seenShapesByCategory.TryGetValue(category, out var seenShapes))
            {
                seenShapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _seenShapesByCategory[category] = seenShapes;
            }

            if (!_seenKeySetsByCategory.TryGetValue(category, out var seenKeySets))
            {
                seenKeySets = new HashSet<string>(StringComparer.Ordinal);
                _seenKeySetsByCategory[category] = seenKeySets;
            }

            // Full categories still accept a sample with a never-seen key set (e.g. a new reward key), up to 2x the cap,
            // so a new protocol field is never hidden behind per-map samples.
            var newKeySet = !seenKeySets.Contains(KeySignature(dataObj));
            if (seenShapes.Count >= maxSamples && (!newKeySet || seenShapes.Count >= maxSamples * 2))
                return;

            var playerAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sanitized = (JObject)SanitizeToken(dataObj.DeepClone(), selfEntityId, selfUsername, playerAliasMap, parentKey: null);
            if (skeletonOnly)
                sanitized = (JObject)ToSkeleton(sanitized);
            var normalizedMap = (mapContext ?? string.Empty).Trim().ToLowerInvariant();
            var shapeKey = ComputeShapeKey(cmd, normalizedMap, sanitized);

            if (!seenShapes.Add(shapeKey))
                return;
            seenKeySets.Add(KeySignature(dataObj));

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
        Dictionary<string, long> pendingCounts;
        lock (_sync)
        {
            if (!_dirty || (_pendingBuckets.Count == 0 && _pendingCommandCounts.Count == 0))
                return;

            pendingSnapshot = new Dictionary<string, List<PacketSchemaSample>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, list) in _pendingBuckets)
            {
                if (list.Count > 0)
                    pendingSnapshot[cat] = new List<PacketSchemaSample>(list);
            }
            _pendingBuckets.Clear();
            pendingCounts = new Dictionary<string, long>(_pendingCommandCounts, StringComparer.Ordinal);
            _pendingCommandCounts.Clear();
            _countsSinceFlush = 0;
            _dirty = false;
        }

        if (pendingSnapshot.Count == 0 && pendingCounts.Count == 0)
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
            var existingKeySets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cat, list) in existing)
            {
                var shapeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cleanedList = new List<PacketSchemaSample>(list.Count);
                foreach (var sample in list)
                {
                    if (sample?.Payload == null)
                        continue;
                    // Retroactively drop any sample of the player's own house (older builds sampled it).
                    if (IsHousePayload(sample.Payload, sample.MapContext))
                        continue;

                    var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sample.Payload = (JObject)SanitizeToken(sample.Payload, 0, null, aliasMap, parentKey: null);
                    sample.MapContext = (sample.MapContext ?? string.Empty).Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(sample.ShapeKey))
                        sample.ShapeKey = ComputeShapeKey(sample.Command, sample.MapContext, sample.Payload);

                    var keySet = KeySignature(sample.Payload);
                    var keySets = existingKeySets.TryGetValue(cat, out var ks) ? ks : existingKeySets[cat] = new HashSet<string>(StringComparer.Ordinal);
                    if (shapeSet.Add(sample.ShapeKey) &&
                        (cleanedList.Count < MaxSamplesFor(cat) || (!keySets.Contains(keySet) && cleanedList.Count < MaxSamplesFor(cat) * 2)))
                    {
                        cleanedList.Add(sample);
                        keySets.Add(keySet);
                    }
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

                var keySets = existingKeySets.TryGetValue(cat, out var ks) ? ks : existingKeySets[cat] = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in samples)
                {
                    var keySet = KeySignature(s.Payload);
                    var fits = dst.Count < MaxSamplesFor(cat) || (!keySets.Contains(keySet) && dst.Count < MaxSamplesFor(cat) * 2);
                    if (!fits)
                        continue;
                    var key = string.IsNullOrWhiteSpace(s.ShapeKey)
                        ? ComputeShapeKey(s.Command, s.MapContext, s.Payload)
                        : s.ShapeKey;
                    if (shapeSet.Add(key))
                    {
                        s.ShapeKey = key;
                        dst.Add(s);
                        keySets.Add(keySet);
                    }
                }
            }

            var commandCounts = new Dictionary<string, long>(existingDoc.CommandCounts ?? new Dictionary<string, long>(), StringComparer.Ordinal);
            foreach (var (name, count) in pendingCounts)
                commandCounts[name] = commandCounts.GetValueOrDefault(name) + count;

            var doc = new PacketSchemaDocument
            {
                Note = "Anonymized AQW packet samples accumulated across sessions for SkuaSim protocol calibration. Contains zero usernames or account/entity IDs. 'skeleton_*' samples keep key names only; CommandCounts holds command names and counts only.",
                LastUpdatedUtc = DateTime.UtcNow,
                TotalFlushesMerged = existingDoc.TotalFlushesMerged + 1,
                CommandCounts = commandCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
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

    private static int MaxSamplesFor(string category) =>
        category.StartsWith("skeleton_", StringComparison.OrdinalIgnoreCase) ? MaxSkeletonSamplesPerCommand : MaxSamplesPerCategory;

    /// <summary>Replaces every leaf value with its type ("&lt;int&gt;", "&lt;str&gt;", ...); keeps at most 2 array items.</summary>
    private static JToken ToSkeleton(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
            {
                var dst = new JObject();
                foreach (var prop in ((JObject)token).Properties())
                    dst[prop.Name] = ToSkeleton(prop.Value);
                return dst;
            }
            case JTokenType.Array:
            {
                var dst = new JArray();
                foreach (var item in ((JArray)token).Take(2))
                    dst.Add(ToSkeleton(item));
                return dst;
            }
            case JTokenType.Integer: return new JValue("<int>");
            case JTokenType.Float: return new JValue("<float>");
            case JTokenType.Boolean: return new JValue("<bool>");
            case JTokenType.Null: return new JValue("<null>");
            default: return new JValue("<str>");
        }
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
    /// <summary>Server command name -> times seen (names and counts only, no payloads).</summary>
    public Dictionary<string, long> CommandCounts { get; set; } = new(StringComparer.Ordinal);
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
