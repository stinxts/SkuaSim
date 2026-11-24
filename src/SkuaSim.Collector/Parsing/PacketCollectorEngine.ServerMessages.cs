using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkuaSim.Collector.Models;

namespace SkuaSim.Collector.Parsing;

/// <summary>
/// Server warnings and messages: string packets <c>["warning", room, text]</c> / <c>["server", room, text]</c> and the
/// message fields of JSON packets (<c>strMessage</c>, <c>msg</c>), stored as anonymized templates with the client command
/// that preceded them, so the simulator can send the same warnings scripts react to. Called under <c>_sync</c>.
/// </summary>
public sealed partial class PacketCollectorEngine
{
    private static readonly HashSet<string> StringMessageCommands = new(StringComparer.Ordinal) { "warning", "server", "multiLoginWarning" };
    private static readonly string[] JsonMessageFields = { "strMessage", "msg" };

    // Client commands aimed at other players: a message right after them may name that player, so it is never stored.
    private static readonly HashSet<string> PlayerTargetedClientCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "whisper", "message", "gp", "requestFriend", "acceptFriend", "declineFriend", "deleteFriend", "addFriend", "ignoreList"
    };

    private const double PrecedingCommandWindowSeconds = 3.0;
    private const int MaxMessageLength = 300;
    private const int MaxKnownPlayerNames = 5000;

    // RAM only, never persisted: the last client command and the names of players seen in rooms (used to mask names).
    private string _lastClientCommand = string.Empty;
    private DateTime _lastClientCommandUtc = DateTime.MinValue;
    private readonly HashSet<string> _knownPlayerNames = new(StringComparer.OrdinalIgnoreCase);

    public long SessionServerMessagesCaptured { get; private set; }
    public long SessionServerMessagesSkipped { get; private set; }

    /// <summary>Remembers the client command (name only; for <c>cmd</c> the slash command, e.g. <c>cmd:tfer</c>).</summary>
    private void NoteClientCommandLocked(string[] parts)
    {
        var cmd = parts[2];
        if (string.Equals(cmd, "cmd", StringComparison.Ordinal) && parts.Length >= 5 && IsCommandWord(parts[4]))
            cmd = "cmd:" + parts[4];
        _lastClientCommand = cmd;
        _lastClientCommandUtc = DateTime.UtcNow;
    }

    private static bool IsCommandWord(string s) => s.Length is > 0 and <= 24 && s.All(char.IsLetterOrDigit);

    private void NotePlayerNameLocked(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 3 || name.Length > 30 || _knownPlayerNames.Count >= MaxKnownPlayerNames)
            return;
        _knownPlayerNames.Add(name);
    }

    /// <summary>String packet ("warning", "server"…): the text is every string after the room ID.</summary>
    private void HandleStringMessageLocked(JArray strParts)
    {
        var cmd = strParts[0]!.ToString();
        if (string.Equals(cmd, "uotls", StringComparison.Ordinal) && strParts.Count > 2)
        {
            NotePlayerNameLocked(strParts[2]?.ToString());
            return;
        }
        if (!StringMessageCommands.Contains(cmd))
            return;
        var text = string.Join(" | ", strParts.Skip(2).Where(t => t?.Type == JTokenType.String).Select(t => t!.ToString()).Where(s => s.Trim().Length > 0));
        RecordServerMessageLocked(cmd, text);
    }

    /// <summary>JSON packet: any non-empty <c>strMessage</c> / <c>msg</c> field; <c>unm</c> feeds the name mask.</summary>
    private void HandleJsonMessageLocked(string cmd, JObject dataObj)
    {
        if (dataObj["unm"] is JValue { Type: JTokenType.String } unm)
            NotePlayerNameLocked(unm.ToString());
        foreach (var field in JsonMessageFields)
        {
            if (dataObj[field] is JValue { Type: JTokenType.String } v && v.ToString().Trim().Length > 0)
                RecordServerMessageLocked("json:" + cmd, v.ToString());
        }
    }

    private void RecordServerMessageLocked(string kind, string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var recent = (DateTime.UtcNow - _lastClientCommandUtc).TotalSeconds <= PrecedingCommandWindowSeconds;
        var preceding = recent && _lastClientCommand.Length > 0 ? _lastClientCommand : "none";
        if (PlayerTargetedClientCommands.Contains(preceding) ||
            (preceding.StartsWith("cmd:", StringComparison.Ordinal) && !string.Equals(preceding, "cmd:tfer", StringComparison.OrdinalIgnoreCase)))
        {
            SessionServerMessagesSkipped++;
            return;
        }

        var text = AnonymizeServerMessage(rawText, _selfUsername, _knownPlayerNames);
        if (text.Length == 0)
            return;
        var key = kind + "|" + text;
        if (!_delta.ServerMessages.TryGetValue(key, out var msg))
        {
            if (_delta.ServerMessages.Count >= CapturedServerMessage.MaxTemplates)
                return;
            _delta.ServerMessages[key] = msg = new CapturedServerMessage { Kind = kind, Text = text };
        }
        msg.Count++;
        CapturedServerMessage.AddCount(msg.PrecededBy, preceding);
        if (!string.IsNullOrEmpty(_currentMapName))
            CapturedServerMessage.AddCount(msg.Maps, _currentMapName);
        SessionServerMessagesCaptured++;
    }

    private static readonly Regex DoubleQuoted = new("\"[^\"]{0,120}\"", RegexOptions.Compiled);
    private static readonly Regex SingleQuoted = new(@"(?<=^|[\s(\[])'[^']{1,120}'(?=$|[\s.,!?:;)\]])", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Own name → <c>&lt;SELF&gt;</c>, names of players seen in rooms → <c>&lt;PLAYER&gt;</c>, quoted text → <c>&lt;Q&gt;</c>,
    /// digit runs → <c>#</c>; whitespace collapsed, at most 300 characters.
    /// </summary>
    public static string AnonymizeServerMessage(string text, string? selfName, IEnumerable<string>? playerNames)
    {
        var s = text;
        if (!string.IsNullOrWhiteSpace(selfName))
            s = ReplaceName(s, selfName.Trim(), "<SELF>");
        if (playerNames != null)
            foreach (var name in playerNames.OrderByDescending(n => n.Length))
                s = ReplaceName(s, name, "<PLAYER>");
        s = DoubleQuoted.Replace(s, "\"<Q>\"");
        s = SingleQuoted.Replace(s, "'<Q>'");
        s = Digits.Replace(s, "#");
        s = Spaces.Replace(s, " ").Trim();
        return s.Length > MaxMessageLength ? s[..MaxMessageLength] + "…" : s;
    }

    private static string ReplaceName(string s, string name, string replacement) =>
        s.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0
            ? s
            : Regex.Replace(s, @"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])", replacement, RegexOptions.IgnoreCase);
}
