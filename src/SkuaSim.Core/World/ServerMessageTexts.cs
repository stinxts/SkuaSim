namespace SkuaSim.Core.World;

/// <summary>
/// Server warnings and messages with their real wording, as captured by the collector (1.3.0, 2026-11-24,
/// <c>GameDatabase.ServerMessages</c>). Only texts seen in collected data are sent; the part the collector masks as
/// <c>&lt;Q&gt;</c> (quoted text) is filled with the requested map name. Packet shapes: string packets
/// <c>["warning"|"server", "-1", text]</c> (CoreBots reads the text at index 2) and JSON packets verified in
/// <c>packet_schemas.json</c> (<c>buyItem</c> { cmd, bitSuccess, strMessage, ItemID, iQty, CharItemID },
/// <c>acceptQuest</c> { cmd, QuestID, bSuccess, msg }).
/// </summary>
public static class ServerMessageTexts
{
    /// <summary>Collector template of the member-only join warning (its TargetMaps list the member-only maps).</summary>
    public const string MemberOnlyMapTemplate = "\"<Q>\" is an Membership-Only Map.";

    public static string MemberOnlyMap(string map) => $"\"{map}\" is an Membership-Only Map.";
    public static string MapNotAvailable(string map) => $"\"{map}\" is not available.";

    public static string MapLockedUntilEvent(string map, string houseItem) =>
        $"\"{map}\" map is locked until event begins. Get '{houseItem}' house item from /BaseCamp to unlock.";

    public const string BuyNotEnoughGold = "Item is not buyable. Not enough Gold";
    public const string BuyNotEnoughCoins = "Item is not buyable. Not enough Adventure Coins";
    public const string BuyInventoryFull = "Item is not buyable. Bag Item Inventory full";

    public const string PickupInBank = "Item already in your Bank.";
    public const string PickupFullStack = "Already have a full stack of that item.";

    /// <summary>Sent when a PvP match is won by team 0 ("Team A"; bludrutbrawl and dagepvp).</summary>
    public const string PvpTeamAVictory = "The battle has been decided! Team A is victorious!";

    public const string AcceptQuestSuccess = "success";
}
