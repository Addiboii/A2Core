using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Where an item lives and how it behaves, from the client's Item.dat (gamedata/items, tools/data/item-limits.json) and the
/// official recordings (a2core-log/item-storage-audit-2026-11-11.md has the per-category table). One place for the rules the
/// grant, bag, warehouse and use paths share.
/// <para>OBSERVED (decoded 0x5611 / 0x561b of the 20 official recordings 2026-11-06/07):</para>
/// <list type="bullet">
/// <item>No item with AutoUseWhenAcquire was ever in a login list (bag 1, warehouses 2 / 15, equipment 11). Gained ones come as
/// ItemModificationList_NT Add into container 4 kAutoUseItem, slot 0 (increase 5 loot, 24 quest, 49 object, 72 battle pass), and
/// leave with Remove, container 4, decrease 38 kAutoUseItem (an auto-opened box: decrease 37 kOpenRewardBox, its contents added to
/// the bag in the same list): pet souls 114, titles 8, soul crystals 7 (a stack of 20 and of 50 used whole: one 0x9011 with 20),
/// pets 5, boxes 2, Ascension items 2, the default wing 1. No ItemUse_NT 0x5616 accompanies an auto-use.</item>
/// <item>Currencies in the bag: Gold, GoldNonbinding (also both warehouses), AbyssPoint, CoinQuest6/7, CoinSeason, CoinEnchant,
/// CoinBattlePass - one stack each (Item.dat MaxStackCount 0: no limit); never GoldCombined or a GameCash item (Currencies.cs).</item>
/// <item>A period item (Item.dat FixedLimitTime) carries its end in Common_Item _expire_time with the fixed-expiry flag: ticks
/// 639330336000000000 were sent as 1797404400000 (#2432 of 105448, CoinSeason #192306 of 115817): the ticks read as Korean time
/// (UTC+9).</item>
/// </list>
/// CANDIDATE (never recorded): MaintainTime items' end (acquisition + minutes, not fixed); deleting an expired item (Item.dat
/// DeleteType is DeleteItem for every item, ReplaceItem none) with decrease 29 kPeriodItemExpired; the refusal codes of a full bag
/// on each path (GameSession.BagRoom.cs).
/// </summary>
public static class ItemRules
{
    /// <summary>
    /// The AutoUseWhenAcquire effects this server carries out at once (GameSession.Rides.cs AutoUseAcquiredAsync), so their items
    /// never take a bag slot. Item.dat has 3,396 AutoUseWhenAcquire items in 26 effects; the ones not listed here (BattlePassUnlock 72,
    /// BattlePassExp 42, the craft and gather Elevate masteries 24, Subscribe 7, Skill 3) have no server side yet and stay in the bag.
    /// </summary>
    public static readonly IReadOnlySet<string> AutoUseEffects = new HashSet<string>
    {
        "GetSkin", "GetSkinSet", "GetTitle", "GetVehicle", "GetVehicleSoul", "GetVehicleSoulCrystal", "AscensionGrade", "GetWing",
        "RewardBox", "GetStigmaShard", "GetStigmaShardSuperior", "GetGameCash", "GetGamePendingCash",
        "ExtendInventorySlot", "ExtendStorageSlot", "ExtendCharacterStorageSlot",
    };

    /// <summary>The effect an item uses itself for when gained (Item.dat AutoUseWhenAcquire), when this server carries it out.</summary>
    public static string? AutoUseEffect(int templateId) =>
        ItemDB.Embedded.Of(templateId) is { AutoUseWhenAcquired: true, Use.Effect: var effect } && AutoUseEffects.Contains(effect) ? effect
        : RewardBoxes.Embedded.Use(templateId) is { AutoUse: true, Effect: var boxEffect } && AutoUseEffects.Contains(boxEffect) ? boxEffect
        : null;

    /// <summary>Whether the item uses itself when gained: shown in container 4 kAutoUseItem, never in a bag slot.</summary>
    public static bool IsAutoUse(int templateId) => AutoUseEffect(templateId) is not null;

    /// <summary>Whether a stack of this item takes a bag slot: not an account currency / Kina (All), not a self-using item.</summary>
    public static bool NeedsBagSlot(int templateId) => !Currencies.NeverAnItem(templateId) && !IsAutoUse(templateId);

    /// <summary>
    /// How many of the item one stack holds: Item.dat MaxStackCount, where a currency's 0 means no limit (the official bag keeps one
    /// stack of each currency) and equipment never stacks. game.grant_item uses the same numbers (game.item_stack_max, migrations
    /// 034 and 056).
    /// </summary>
    public static long StackLimit(int templateId) => ItemDB.Embedded.Of(templateId) switch
    {
        null => 1,
        { Type: "Currency" } => long.MaxValue,
        { Type: "Equip" } => 1,
        var item => Math.Max(1, item.MaxStack),
    };

    /// <summary>Whether a character of this race (1 Light, 2 Dark) may use or wear the item (Item.dat ItemRace; All and None: any).</summary>
    public static bool RaceAllows(int templateId, int race) => ItemDB.Embedded.Of(templateId)?.Race switch
    {
        "Light" => race != 2,
        "Dark" => race != 1,
        _ => true,
    };

    /// <summary>Item.dat FixedLimitTime ticks of the period items (tools/item_limits.py).</summary>
    static readonly Lazy<Dictionary<int, long>> fixedLimits = new(() =>
    {
        using var stream = typeof(ItemRules).Assembly.GetManifestResourceStream("item-limits.json")
            ?? throw new InvalidOperationException("Missing embedded game data item-limits.json");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("fixed_limit_ticks").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetInt64());
    });

    /// <summary>The official server reads FixedLimitTime as Korean time (UTC+9), OBSERVED (see the summary).</summary>
    public const long FixedLimitZoneMs = 9 * 3_600_000L;

    /// <summary>The fixed end of a period item (unix ms), null for items without one.</summary>
    public static long? FixedExpiry(int templateId) =>
        fixedLimits.Value.TryGetValue(templateId, out var ticks) ? (ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond - FixedLimitZoneMs : null;

    /// <summary>
    /// When the item ends (unix ms) and whether that end is fixed (the Common_Item fixed-expiry flag): FixedLimitTime, else
    /// MaintainTime minutes after the acquisition (CANDIDATE); null for the items that do not end.
    /// </summary>
    public static (long At, bool Fixed)? Expiry(int templateId, DateTimeOffset? acquired) =>
        FixedExpiry(templateId) is { } at ? (at, true)
        : ItemDB.Embedded.Of(templateId) is { MaintainMinutes: > 0 } item && acquired is { } since ? (since.ToUnixTimeMilliseconds() + item.MaintainMinutes * 60_000L, false)
        : null;

    /// <summary>Whether the item has ended at this time (unix ms).</summary>
    public static bool Expired(int templateId, DateTimeOffset? acquired, long nowMs) => Expiry(templateId, acquired) is { } end && nowMs >= end.At;

    /// <summary>
    /// Whether an item may lie in a location (Item.dat): the account warehouse takes CanStorage items, the character warehouse
    /// CanCharacterStorage ones, the legion warehouse CanGuildStorage ones (not built yet: GameSession.Legion.cs); self-using items and
    /// the account currencies lie in none of them. Unknown items and the bag take anything.
    /// </summary>
    public static bool AllowedIn(int templateId, string location) => location switch
    {
        "storage" => !IsAutoUse(templateId) && !Currencies.NeverAnItem(templateId) && ItemDB.Embedded.Of(templateId) is not { CanStorage: false },
        "character_storage" => !IsAutoUse(templateId) && !Currencies.NeverAnItem(templateId) && ItemDB.Embedded.Of(templateId) is not { CanCharacterStorage: false },
        "guild_storage" => !IsAutoUse(templateId) && !Currencies.NeverAnItem(templateId) && ItemDB.Embedded.Of(templateId) is not { CanGuildStorage: false },
        _ => true,
    };
}
