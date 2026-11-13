using Aion.Application.Gameplay;

namespace Aion.World;

// Quna bag items (live 2026-11-11: "Pending Quna" 535320001 and "Quna" 535300006 sat in the bags - margarita 6,999,994, Axel3 99,999
// of each). Item.dat: Usable, AutoUseWhenAcquire, UseEffect GetGamePendingCash / GetGameCash with UseEffectValues [n] = n per item.
// Quna is the account's (Currencies.cs, migration 087): such a stack is used up whole as soon as it is in the bag (and at login for
// the old ones) and its total credits the account - GetGameCash as GameCash 930200001 (Quna, category 7), GetGamePendingCash as
// GameCashPending 930200007 (Pending Quna, category 103) - through game.grant_item's currency path in the same transaction that
// removes the stack; then the bag change and RefreshCurrency_NT. CANDIDATE: the official messages of this use (none recorded).
public sealed partial class GameSession
{
    /// <summary>The account currency item a Quna bag item credits, and how much per item; null for other items.</summary>
    static (int CurrencyItem, int PerItem)? QunaItemCurrency(int templateId) =>
        ItemDB.Embedded.Of(templateId) is { AutoUseWhenAcquired: true, Use: { } use }
            ? use.Effect switch
            {
                "GetGameCash" => (930200001, PerItem(use.Values)),
                "GetGamePendingCash" => (930200007, PerItem(use.Values)),
                _ => ((int, int)?)null,
            }
            : null;

    static int PerItem(IReadOnlyList<string>? values) => values is [var first, ..] && int.TryParse(first, out var n) && n > 0 ? n : 1;

    async Task<List<byte[]>> AutoUseQunaItemsAsync(List<ItemState> rows, CancellationToken ct)
    {
        if (rows.Count == 0 || Store is not { } store || CharacterId is not { } character) return [];
        var costs = rows.Select(r => (r.InstanceId, r.Revision, (int)Math.Min(int.MaxValue, r.Quantity))).ToList();
        var credit = rows.GroupBy(r => QunaItemCurrency(r.TemplateId)!.Value)
            .Select(g => (g.Key.CurrencyItem, g.Sum(r => r.Quantity) * g.Key.PerItem)).ToList();
        var done = await Stored(async () => await store.ShopBuyAsync(character, costs, credit, [], ct) ? [[]] : []);
        if (done.Count == 0) return [];
        Note($"quna items used: {string.Join(", ", rows.Select(r => $"{r.TemplateId} x{r.Quantity}"))} -> {string.Join(", ", credit.Select(c => $"{c.Item1} +{c.Item2}"))}");
        storedCurrencies = null;
        return [.. await InventoryChangesAsync(ct, 0, DecreaseAutoUsed), .. await CurrencyChangesAsync(ct)];
    }
}
