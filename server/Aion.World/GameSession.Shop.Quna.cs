using Aion.Protocol.Native;

namespace Aion.World;

// Quna (GameCash): bag stacks item 535300006 on 0x5611; account total also via Store.AccountQunaBalanceAsync + HTTP balances.
// Kinah is bag item 930100001 on 0x5611 (GoldCombined). GNB slot wiring for Quna is unverified — see tools/data/currency_hud.json.
// HTTP GET virtual_currency/v2.0/me/balances uses currency_id 109 with currency_group_id 145 (GameCash), 110 with 158 (Mileage).
// RefreshCurrency_NT (0x3bd1) map keys in usmap are ECurrencyCategory (7 GameCash, 6 Mileage), not the HTTP currency_id field.
public sealed partial class GameSession
{
    public const ushort RefreshCurrencyNt = 0x3bd1;

    public const int QunaBmCurrencyGroupId = 145;
    public const int QunaVirtualCurrencyId = 109;
    public const int MileageCurrencyGroupId = 158;
    public const int MileageVirtualCurrencyId = 110;
    public const int GameCashCurrencyCategory = 7;
    public const int MileageCurrencyCategory = 6;
    public const int GoldCombinedCurrencyCategory = 93;

    long accountQunaBalance;

    public long QunaBalance() => Store is not null ? accountQunaBalance : QunaIn(inventory.Values);

    async Task RefreshAccountQunaBalanceAsync(CancellationToken ct)
    {
        if (Store is { } store)
            accountQunaBalance = await store.AccountQunaBalanceAsync(ct);
        else
            accountQunaBalance = QunaIn(inventory.Values);
    }

    static long QunaIn(IEnumerable<InventoryItem> items) =>
        items.Where(i => i.ItemId == PlatformGoodsTables.Embedded.QunaItemId && i.Container == InventoryItem.Bag).Sum(i => i.Count);

    public byte[] RefreshCurrency(int currencyGroupId, params (int MapKey, long Balance)[] entries)
    {
        var w = PacketWriter.Opcode(RefreshCurrencyNt).I32(currencyGroupId).Varint((uint)entries.Length);
        foreach (var (key, balance) in entries) w.I32(key).I64(balance);
        return w.ToArray();
    }

    byte[] RefreshGameCash(long balance) =>
        RefreshCurrency(QunaBmCurrencyGroupId, (GameCashCurrencyCategory, balance));

    byte[] RefreshMileage(long balance = 0) =>
        RefreshCurrency(MileageCurrencyGroupId, (MileageCurrencyCategory, balance));

    public IEnumerable<byte[]> InventoryDepotSyncPackets()
    {
        if (Store is null || CharacterId is null) yield break;
        var level = EquipItemLevel();
        yield return GamePackets.ItemDepotList([.. inventory.Values]);
        yield return GamePackets.ItemEquipLevelChange(level, level);
    }

    public IEnumerable<byte[]> QunaRefreshPackets()
    {
        foreach (var packet in InventoryDepotSyncPackets()) yield return packet;
        yield return RefreshGameCash(QunaBalance());
        yield return RefreshMileage();
    }

    List<byte[]> AppendQunaRefreshIfNeeded(long beforeAccountBalance)
    {
        if (beforeAccountBalance == accountQunaBalance) return [];
        return [RefreshGameCash(accountQunaBalance)];
    }

    public byte[] QunaRefreshNow() => RefreshGameCash(QunaBalance());

    public string WireAccountId => loginIdentity.AccountSid;
}
