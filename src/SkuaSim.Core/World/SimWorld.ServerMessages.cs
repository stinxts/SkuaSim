using Newtonsoft.Json.Linq;

namespace SkuaSim.Core.World;

/// <summary>
/// Server warnings: actions the server refuses are refused here too, and the script receives the same packet through
/// <c>Bot.Events.ExtensionPacketReceived</c> (CoreBots' MapIsMemberLocked, MapIsNotAvailableListener,
/// RelogRequieredListener… react to them). Texts come from <see cref="ServerMessageTexts"/>.
/// </summary>
public sealed partial class SimWorld
{
    /// <summary>Server messages sent to the script during the run: text → count (report).</summary>
    public Dictionary<string, int> ServerMessagesSent { get; } = new(StringComparer.Ordinal);

    /// <summary>String packet <c>{"params":{"type":"str","dataObj":[cmd, "-1", text]}}</c>.</summary>
    private void SendServerString(string cmd, string text)
    {
        Trace.World($"Server {cmd}: {text}");
        NoteServerMessage(text);
        var packet = new JObject
        {
            ["params"] = new JObject { ["type"] = "str", ["dataObj"] = new JArray(cmd, "-1", text) }
        };
        _ctx.RaiseEvent("ExtensionPacketReceived", packet);
    }

    /// <summary>JSON packet <c>{"params":{"type":"json","dataObj":{...}}}</c>.</summary>
    private void SendServerJson(JObject dataObj, string? messageForReport)
    {
        if (messageForReport != null)
        {
            Trace.World($"Server {dataObj["cmd"]}: {messageForReport}");
            NoteServerMessage(messageForReport);
        }
        var packet = new JObject { ["params"] = new JObject { ["type"] = "json", ["dataObj"] = dataObj } };
        _ctx.RaiseEvent("ExtensionPacketReceived", packet);
    }

    private void NoteServerMessage(string text)
    {
        lock (Sync)
            ServerMessagesSent[text] = ServerMessagesSent.GetValueOrDefault(text) + 1;
    }

    private void SendBuyResult(int itemId, int quantity, int charItemId, string? failure)
    {
        var dataObj = new JObject { ["cmd"] = "buyItem", ["bitSuccess"] = failure == null ? 1 : 0, ["ItemID"] = itemId, ["iQty"] = quantity, ["CharItemID"] = charItemId };
        if (failure != null)
            dataObj["strMessage"] = failure;
        else
            dataObj["bBank"] = false;
        SendServerJson(dataObj, failure);
    }

    /// <summary>Successful accept: { cmd, QuestID, bSuccess: 1, msg: "success" } (the failure text is not verified yet).</summary>
    private void SendAcceptQuestResult(int questId) =>
        SendServerJson(new JObject { ["cmd"] = "acceptQuest", ["QuestID"] = questId, ["bSuccess"] = 1, ["msg"] = ServerMessageTexts.AcceptQuestSuccess }, messageForReport: null);

    // ------------------------------------------------------------------ map access

    private HashSet<string>? _collectedMemberOnlyMaps;

    /// <summary>Member-only maps: the scenario list plus the maps the collector saw refusing a non-member join.</summary>
    private bool IsMemberOnlyMap(string map)
    {
        if (_ctx.Scenario.World.MemberOnlyMaps.Any(m => Eq(m, map)))
            return true;
        _collectedMemberOnlyMaps ??= new HashSet<string>(
            (_ctx.Catalog.Captured.ServerMessages ?? new()).Values
                .Where(m => m.Kind == "warning" && m.Text == ServerMessageTexts.MemberOnlyMapTemplate)
                .SelectMany(m => m.TargetMaps.Keys),
            StringComparer.OrdinalIgnoreCase);
        return _collectedMemberOnlyMaps.Contains(map);
    }

    /// <summary>
    /// The server's answer to a join it refuses, or null when the join is allowed. Seasonal availability changes over
    /// time, so closed seasonal maps come only from the scenario (<c>world.unavailableMaps</c> /
    /// <c>world.eventLockedMaps</c>).
    /// </summary>
    private string? JoinRefusal(string map)
    {
        if (!IsMember && IsMemberOnlyMap(map))
            return ServerMessageTexts.MemberOnlyMap(map);
        if (_ctx.Scenario.World.EventLockedMaps.FirstOrDefault(kv => Eq(kv.Key, map)) is { Key: not null } locked &&
            !House.Any(h => Eq(h.Name, locked.Value)))
            return ServerMessageTexts.MapLockedUntilEvent(map, locked.Value);
        if (_ctx.Scenario.World.UnavailableMaps.Any(m => Eq(m, map)))
            return ServerMessageTexts.MapNotAvailable(map);
        return null;
    }
}
