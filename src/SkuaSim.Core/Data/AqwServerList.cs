using System.Globalization;
using System.Text.Json;
using SkuaSim.Core.Scenarios;

namespace SkuaSim.Core.Data;

/// <summary>
/// Local copy of AQW's public server list (<see cref="SourceUrl"/>, the list the game client and Skua's server picker
/// read). Bot.Servers serves it, and named relogins are checked against it (offline, full for non-members, member-only).
/// The simulator only reads the local file, so runs stay offline and deterministic; it is refreshed explicitly with
/// <c>skuasim servers-update</c>. Player counts are those of the moment it was fetched.
/// </summary>
public sealed class AqwServerList
{
    public const string SourceUrl = "https://game.aq.com/game/api/data/servers";
    public const string DefaultFileName = "servers.json";

    public string Source { get; set; } = SourceUrl;
    public DateTime FetchedUtc { get; set; }
    public List<ServerSetup> Servers { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Stand-in list for runs without servers.json or a scenario "servers" list: invented, so it is reported as
    /// synthesized as soon as a script reads it or names a server.
    /// </summary>
    public static List<ServerSetup> SynthesizedFallback() => new()
    {
        new ServerSetup { Name = "Twilly", Ip = "sock7.aq.com", Port = 5593, PlayerCount = 400, MaxPlayers = 1000, Lang = "xx" },
        new ServerSetup { Name = "Twig", Ip = "sock8.aq.com", Port = 5589, PlayerCount = 400, MaxPlayers = 1000, Lang = "xx" },
        new ServerSetup { Name = "Artix", Ip = "sock7.aq.com", Port = 5588, PlayerCount = 900, MaxPlayers = 1500, ChatLevel = 2, Lang = "en" },
        new ServerSetup { Name = "Gravelyn", Ip = "sock8.aq.com", Port = 5590, PlayerCount = 300, MaxPlayers = 1500, ChatLevel = 2, Lang = "en" }
    };

    // ------------------------------------------------------------------ Persistence

    public static AqwServerList? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var list = JsonSerializer.Deserialize<AqwServerList>(File.ReadAllText(path), JsonOptions);
            return list is { Servers.Count: > 0 } ? list : null;
        }
        catch { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Short content fingerprint for reports.</summary>
    public string Fingerprint()
    {
        var text = string.Join("\n", Servers.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.Name}|{s.Ip}|{s.Port}|{s.Online}|{s.PlayerCount}|{s.MaxPlayers}|{s.MemberOnly}|{s.ChatLevel}|{s.Lang}|{s.Level}|{s.AccessLevel}")));
        return World.StableHash.Of(text).ToString("X8", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ Parsing

    /// <summary>
    /// Parses the API response: <c>[{"sName":"Twilly","sIP":"sock7.aq.com","iCount":952,"iMax":1000,"bOnline":1,
    /// "iChat":0,"bUpg":0,"sLang":"xx","iPort":5593,"iLevel":0,"accessLevel":0}, ...]</c> (keys as Skua's
    /// <c>Server</c> model maps them). The order of the list is kept: it is the order the client shows.
    /// </summary>
    public static AqwServerList ParseApi(string json, DateTime fetchedUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var servers = new List<ServerSetup>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            var name = Str(e, "sName");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            servers.Add(new ServerSetup
            {
                Name = name.Trim(),
                Ip = Str(e, "sIP") ?? "",
                Port = Int(e, "iPort"),
                Online = Int(e, "bOnline") != 0,
                PlayerCount = Int(e, "iCount"),
                MaxPlayers = Int(e, "iMax"),
                MemberOnly = Int(e, "bUpg") != 0,
                ChatLevel = Int(e, "iChat"),
                Lang = Str(e, "sLang") ?? "",
                Level = Int(e, "iLevel"),
                AccessLevel = Int(e, "accessLevel")
            });
        }
        return new AqwServerList { FetchedUtc = fetchedUtc, Servers = servers };
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    /// <summary>Numbers and flags arrive as numbers, numeric strings or booleans.</summary>
    private static int Int(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            JsonValueKind.True => 1,
            _ => 0
        };
    }

    // ------------------------------------------------------------------ Download (explicit command only)

    /// <summary>Downloads the public server list (one GET, no personal data sent).</summary>
    public static async Task<AqwServerList> DownloadAsync(CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SkuaSim-Servers");
        var json = await http.GetStringAsync(SourceUrl, token).ConfigureAwait(false);
        return ParseApi(json, DateTime.UtcNow);
    }
}
