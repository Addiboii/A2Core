namespace Aion.World;

/// <summary>
/// The client's currencies (Item.dat ItemType Currency, each with an ECurrencyCategory) and how this server keeps them, as the
/// official server does (a2core-log/currencies-2026-11-10.md has the full table).
/// OBSERVED (ItemDepotList_RS 0x5611 at every official login, 11 recordings 2026-11-06/07, and ItemModificationList_NT 0x561b):
/// the character currencies are plain bag items (container 1, a slot each): Gold 930100001 (bound Kina), GoldNonbinding 930100031
/// (tradable Kina; also in the warehouses, containers 2 and 15), AbyssPoint, CoinQuest6/7, CoinSeason, CoinEnchant, CoinBattlePass.
/// Never seen as an item: GoldCombined 930100030 ("Kina (All)") - the client adds Gold and GoldNonbinding up itself (CurrencyInfo
/// GoldCombined is the total it shows; every GoldCombined price is paid bound first, then tradable, OBSERVED 20261106-105448
/// #26956: bound paid while 942 tradable were held) - and the GameCash family 9302000xx (Quna and its states, Mileage, Cash), which
/// are account currencies (game.account_currencies, RefreshCurrency_NT). game.grant_item (migration 087) stores them this way
/// whatever grants them; Layout never sends such a row should one exist.
/// </summary>
public static class Currencies
{
    /// <summary>Item.dat Gold (bound Kina), GoldCombined ("Kina (All)", a total only) and GoldNonbinding (tradable Kina).</summary>
    public const int Gold = 930100001, GoldCombined = 930100030, GoldNonbinding = 930100031;

    /// <summary>The account currency items (Item.dat 9302000xx) and the ECurrencyCategory row of game.account_currencies they credit.
    /// GameCash and GameCashNonbinding are the same Quna here (this server keeps all Quna tradable: category 7 is shown as both).</summary>
    static readonly Dictionary<int, int> AccountCategories = new()
    {
        [930200001] = 7,     // GameCash (Quna)
        [930200003] = 6,     // Mileage
        [930200004] = 9,     // Cash
        [930200005] = 95,    // GameCashBinding
        [930200006] = 7,     // GameCashNonbinding
        [930200007] = 103,   // GameCashPending
        [930200008] = 102,   // GameCashActivating
    };

    /// <summary>The account currency category an item of this template credits (null: not an account currency).</summary>
    public static int? AccountCategoryOf(int templateId) => AccountCategories.TryGetValue(templateId, out var category) ? category : null;

    /// <summary>Templates that are never an item stack: Kina (All) and the account currencies.</summary>
    public static bool NeverAnItem(int templateId) => templateId == GoldCombined || AccountCategories.ContainsKey(templateId);

    /// <summary>The template a grant is stored as: Kina (All) lands as bound Kina (Item.dat GoldCombined is NonExchangeable, as Gold).</summary>
    public static int StoredTemplate(int templateId) => templateId == GoldCombined ? Gold : templateId;

    /// <summary>Kina (All), what the client shows as GoldCombined: the bag's bound plus tradable Kina.</summary>
    public static long KinaAll(IEnumerable<(int TemplateId, string Location, long Quantity)> items) =>
        items.Where(i => i.Location == "bag" && i.TemplateId is Gold or GoldNonbinding).Sum(i => i.Quantity);
}
