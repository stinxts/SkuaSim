using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkuaSim.Core.Data;

/// <summary>Kind of an AE calendar entry.</summary>
public static class AeCalendarKinds
{
    /// <summary>Server-wide double XP / gold / reputation / class points (or "Double ALL").</summary>
    public const string ServerBoost = "server_boost";
    /// <summary>Item/resource boosts (e.g. "Legion Token + Rib Boost"): recorded and reported, not simulated.</summary>
    public const string ResourceBoost = "resource_boost";
    public const string Update = "update";
    public const string Event = "event";
}

public sealed class AeCalendarEvent
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Kind { get; set; } = AeCalendarKinds.Event;

    /// <summary>Start in US Eastern server time (the "?d=" value of the calendar link, else midnight of the listed day).</summary>
    public DateTime StartEastern { get; set; }

    /// <summary>Duration in hours (from the event page "NN hour", else derived from the listed days).</summary>
    public double DurationHours { get; set; }

    /// <summary>"page", "listed_days" or "assumed".</summary>
    public string DurationSource { get; set; } = "assumed";

    /// <summary>Days the calendar lists this event on (yyyy-MM-dd).</summary>
    public List<string> ListedDays { get; set; } = new();

    public bool Xp { get; set; }
    public bool Gold { get; set; }
    public bool Rep { get; set; }
    public bool ClassPoints { get; set; }

    [JsonIgnore]
    public DateTime EndEastern => StartEastern.AddHours(DurationHours);
}

public readonly record struct ServerBoostState(bool Xp, bool Gold, bool Rep, bool ClassPoints)
{
    public bool Any => Xp || Gold || Rep || ClassPoints;

    public override string ToString() =>
        !Any ? "none" : string.Join("+", new[] { Xp ? "XP" : null, Gold ? "Gold" : null, Rep ? "Rep" : null, ClassPoints ? "CP" : null }.Where(s => s != null));
}

/// <summary>
/// Local snapshot of the public Artix Entertainment calendar (https://www.artix.com/calendar/), the same source the
/// VibeSkua Calendar tool reads. Server-wide double XP/gold/rep/CP windows drive the simulator's server boosts when a
/// scenario opts in. The simulator only reads the local file, so runs stay offline and deterministic; the file is
/// refreshed explicitly with <c>skuasim calendar-update</c>. All times are US Eastern (AQW server time).
/// </summary>
public sealed class AeCalendar
{
    public const string SourceUrl = "https://www.artix.com/calendar/";
    public const string DefaultFileName = "ae_calendar.json";

    public string Source { get; set; } = SourceUrl;
    public DateTime FetchedUtc { get; set; }
    public List<AeCalendarEvent> Events { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<TimeZoneInfo> EasternZone = new(() =>
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException("US Eastern time zone not found on this system.");
    });

    /// <summary>AQW server time zone (US Eastern, with daylight saving).</summary>
    public static TimeZoneInfo Eastern => EasternZone.Value;

    public static DateTime ToEastern(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);

    public static DateTime EasternToUtc(DateTime eastern) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified), Eastern);

    public (DateTime First, DateTime Last)? CoverageEastern =>
        Events.Count == 0 ? null : (Events.Min(e => e.StartEastern), Events.Max(e => e.EndEastern));

    public bool Covers(DateTime utc) =>
        CoverageEastern is { } c && ToEastern(utc) >= c.First.Date && ToEastern(utc) < c.Last.Date.AddDays(1);

    public IEnumerable<AeCalendarEvent> ActiveAt(DateTime utc)
    {
        var et = ToEastern(utc);
        return Events.Where(e => e.StartEastern <= et && et < e.EndEastern);
    }

    public ServerBoostState ServerBoostsAt(DateTime utc)
    {
        bool xp = false, gold = false, rep = false, cp = false;
        foreach (var e in ActiveAt(utc).Where(e => e.Kind == AeCalendarKinds.ServerBoost))
        {
            xp |= e.Xp; gold |= e.Gold; rep |= e.Rep; cp |= e.ClassPoints;
        }
        return new ServerBoostState(xp, gold, rep, cp);
    }

    /// <summary>Next instant after <paramref name="utc"/> at which any event starts or ends (null when none).</summary>
    public DateTime? NextChangeAfter(DateTime utc)
    {
        var et = ToEastern(utc);
        DateTime? best = null;
        foreach (var e in Events)
        {
            foreach (var t in new[] { e.StartEastern, e.EndEastern })
                if (t > et && (best == null || t < best))
                    best = t;
        }
        return best == null ? null : EasternToUtc(best.Value);
    }

    // ------------------------------------------------------------------ Persistence

    public static AeCalendar? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try { return JsonSerializer.Deserialize<AeCalendar>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Short content fingerprint for reports (events are ordered deterministically).</summary>
    public string Fingerprint()
    {
        var text = string.Join("\n", Events.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Title}|{e.StartEastern:O}|{e.DurationHours:R}")));
        return World.StableHash.Of(text).ToString("X8", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ Parsing

    // Events are embedded in the calendar page as JS objects:
    // {title: 'Double Gold Boost 11.20.26',url: 'http://www.artix.com/calendar/double-gold-boost-112026/' + '?d=11%2f20%2f2026+2%3a07%3a00+PM',start: '2026-11-20'}
    private static readonly Regex EventRegex = new(
        @"\{\s*title:\s*'(?<title>(?:[^'\\]|\\.)*)'\s*,\s*url:\s*'(?<url>[^']*)'(?:\s*\+\s*'(?<query>[^']*)')?\s*,\s*start:\s*'(?<start>\d{4}-\d{2}-\d{2})'",
        RegexOptions.Compiled);

    private static readonly Regex DurationRegex = new(@"(\d{1,3})\s*-?\s*hours?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses the calendar page. Durations are derived from the listed days until <see cref="ApplyDetailPage"/> refines them.</summary>
    public static AeCalendar ParseListPage(string html, DateTime fetchedUtc)
    {
        // Multi-day events are listed once per day with the same URL; a URL reused weeks later (e.g. a monthly
        // "Members: Free Keys Available") is a separate occurrence, so entries are split where the days are not consecutive.
        var raw = new List<(string Slug, string Title, string Url, DateTime Day, DateTime Start)>();
        foreach (Match m in EventRegex.Matches(html))
        {
            var title = Unescape(m.Groups["title"].Value).Trim();
            var url = m.Groups["url"].Value.Trim();
            var dayText = m.Groups["start"].Value;
            var day = DateTime.ParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var start = ParseQueryStart(m.Groups["query"].Value) ?? day;
            raw.Add((string.IsNullOrEmpty(url) ? $"{title}|{dayText}" : url, title, url, day, start));
        }

        var events = new List<AeCalendarEvent>();
        foreach (var group in raw.GroupBy(r => r.Slug, StringComparer.OrdinalIgnoreCase))
        {
            AeCalendarEvent? current = null;
            DateTime lastDay = default;
            foreach (var r in group.OrderBy(r => r.Day).ThenBy(r => r.Start))
            {
                if (current == null || (r.Day - lastDay).TotalDays > 1)
                {
                    current = new AeCalendarEvent { Title = r.Title, Url = r.Url, StartEastern = r.Start };
                    Classify(current);
                    events.Add(current);
                }
                if (r.Start < current.StartEastern)
                    current.StartEastern = r.Start;
                var dayKey = r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!current.ListedDays.Contains(dayKey))
                    current.ListedDays.Add(dayKey);
                lastDay = r.Day;
            }
        }

        foreach (var ev in events)
        {
            ev.ListedDays.Sort(StringComparer.Ordinal);
            if (ev.ListedDays.Count > 1)
            {
                var lastDay = DateTime.ParseExact(ev.ListedDays[^1], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                ev.DurationHours = Math.Max(1, (lastDay.AddDays(1) - ev.StartEastern).TotalHours);
                ev.DurationSource = "listed_days";
            }
            else
            {
                // AQW server boosts are usually 48 h; other single-day entries cover their day.
                ev.DurationHours = ev.Kind == AeCalendarKinds.ServerBoost ? 48 : Math.Max(1, (ev.StartEastern.Date.AddDays(1) - ev.StartEastern).TotalHours);
                ev.DurationSource = "assumed";
            }
        }

        return new AeCalendar
        {
            FetchedUtc = fetchedUtc,
            Events = events.OrderBy(e => e.StartEastern).ThenBy(e => e.Title, StringComparer.Ordinal).ToList()
        };
    }

    /// <summary>Reads the duration ("48 hour DOUBLE Experience ...") from an event detail page.</summary>
    public static bool ApplyDetailPage(AeCalendarEvent ev, string html)
    {
        // The title appears in <head> too; the description ("72 hour DOUBLE Gold boost ...") follows the title in the
        // article body, so check the text right after every occurrence of the title.
        var text = Regex.Replace(Regex.Replace(html, @"<(script|style)\b.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>", " ");
        text = Regex.Replace(System.Net.WebUtility.HtmlDecode(text), @"\s+", " ");
        for (var at = text.IndexOf(ev.Title, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(ev.Title, at + ev.Title.Length, StringComparison.OrdinalIgnoreCase))
        {
            var window = text.Substring(at + ev.Title.Length, Math.Min(160, text.Length - at - ev.Title.Length));
            var m = DurationRegex.Match(window);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var hours) && hours > 0 && hours <= 24 * 31)
            {
                ev.DurationHours = hours;
                ev.DurationSource = "page";
                return true;
            }
        }
        return false;
    }

    internal static void Classify(AeCalendarEvent ev)
    {
        var t = ev.Title;
        bool Has(string pattern) => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase);
        if (Has(@"\bdouble\s+all\b"))
            ev.Xp = ev.Gold = ev.Rep = ev.ClassPoints = true;
        else
        {
            ev.Xp = Has(@"\bdouble\s+(exp|xp|experience)\b");
            ev.Gold = Has(@"\bdouble\s+gold\b");
            ev.Rep = Has(@"\bdouble\s+rep(utation)?\b");
            ev.ClassPoints = Has(@"\bdouble\s+(class\s*points?|cp)\b");
        }
        ev.Kind = ev.Xp || ev.Gold || ev.Rep || ev.ClassPoints ? AeCalendarKinds.ServerBoost
            : Has(@"\bboost\b") ? AeCalendarKinds.ResourceBoost
            : Has(@"\bmid-?week update\b") ? AeCalendarKinds.Update
            : AeCalendarKinds.Event;
    }

    private static DateTime? ParseQueryStart(string query)
    {
        // "?d=11%2f20%2f2026+2%3a07%3a00+PM"
        var idx = query.IndexOf("d=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        var raw = Uri.UnescapeDataString(query[(idx + 2)..].Replace('+', ' ')).Trim();
        return DateTime.TryParse(raw, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static string Unescape(string s)
    {
        try { return Regex.Unescape(s); }
        catch { return s; }
    }

    // ------------------------------------------------------------------ Download (explicit command only)

    /// <summary>
    /// Downloads the public calendar and the detail page of each boost event (for its duration), reusing durations
    /// already known in <paramref name="previous"/>. No personal data is sent.
    /// </summary>
    public static async Task<AeCalendar> DownloadAsync(AeCalendar? previous, Action<string>? log = null, CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SkuaSim-Calendar");
        var html = await http.GetStringAsync(SourceUrl, token).ConfigureAwait(false);
        var calendar = ParseListPage(html, DateTime.UtcNow);
        log?.Invoke($"Calendar page: {calendar.Events.Count} events.");

        var known = previous?.Events
            .Where(e => e.DurationSource == "page" && !string.IsNullOrEmpty(e.Url))
            .GroupBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DurationHours, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        int fetched = 0, reused = 0, failed = 0;
        foreach (var ev in calendar.Events.Where(e => e.Kind is AeCalendarKinds.ServerBoost or AeCalendarKinds.ResourceBoost && !string.IsNullOrEmpty(e.Url)))
        {
            if (known.TryGetValue(ev.Url, out var hours))
            {
                ev.DurationHours = hours;
                ev.DurationSource = "page";
                reused++;
                continue;
            }
            try
            {
                var url = ev.Url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                var page = await http.GetStringAsync(url, token).ConfigureAwait(false);
                if (ApplyDetailPage(ev, page)) fetched++; else failed++;
                await Task.Delay(250, token).ConfigureAwait(false); // be gentle with artix.com
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failed++;
            }
        }
        log?.Invoke($"Boost durations: {fetched} read from event pages, {reused} reused, {failed} kept from the listed days.");
        return calendar;
    }
}
