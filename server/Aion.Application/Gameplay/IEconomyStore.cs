using System.Text.Json;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

/// <summary>An item kept in the legion warehouse: the whole stored instance (enchant, godstones, rolled stats) and its slot.</summary>
public sealed record GuildStorageEntry(ItemState Item, int Slot);

/// <summary>The legion warehouse: its items, extensions bought, revision (raised on every change) and last sort.</summary>
public sealed record GuildStorageView(IReadOnlyList<GuildStorageEntry> Items, int Extensions, long Revision, long LastSortMs);

/// <summary>A warehouse history line (EGuildStorageHistoryRecordType 1 store, 2 take out; CANDIDATE values).</summary>
public sealed record GuildStorageRecord(string Name, int Type, int TemplateId, long Count, DateTimeOffset At);

/// <summary>An application to a legion, as its officers see it.</summary>
public sealed record GuildJoinRequest(Guid CharacterId, string Name, int Level, int WireId, string Comment, DateTimeOffset At);

/// <summary>A broker listing. UnitPrice is per item; State: active, sold, cancelled or expired (expiry passed, not yet returned).</summary>
public sealed record TradeListing(long SaleId, Guid Seller, Guid Item, int TemplateId, long Quantity, long UnitPrice, DateTimeOffset Listed, DateTimeOffset Expires, string State);

/// <summary>Kina a seller is owed for a sale (taken with TradeDeliveryComplete).</summary>
public sealed record TradeDelivery(long Id, long Kina, int TemplateId, long Count, DateTimeOffset At);

/// <summary>A gift-box reward (game.push_rewards) and whether this character took it.</summary>
public sealed record PushRewardRecord(int Id, int Type, string Sender, string Title, string Body, int TemplateId, long Count, DateTimeOffset Sent, DateTimeOffset Expire, bool Taken);

/// <summary>
/// Legion grade authority (GuildMemberGradeAuthority.dat AuthorityFlags, tools/data/guild.json grade_authority): bit n is
/// EGuildAuthority n (1 ChangeGrade, 2 Kickout, 3 ChangeNotice, 4 ChangeIntroduce, 5 ChangeEmblem, 6 ChangeJoinrule,
/// 7 JoinRequestApproval, 8 GuildAgitRental, 9 GuildStorageStore, 10 GuildStorageTakeOut, 11 GuildStorageSort,
/// 12 GuildStorageExtend). Index: EGuildMemberGrade 0 Master, 1 Officer, 2 Member.
/// </summary>
public static class GuildAuthority
{
    public const int ChangeGrade = 1, Kickout = 2, ChangeNotice = 3, ChangeIntroduce = 4, ChangeEmblem = 5, ChangeJoinRule = 6,
        JoinRequestApproval = 7, StorageStore = 9, StorageTakeOut = 10, StorageSort = 11, StorageExtend = 12;
    public static readonly int[] Flags = [8190, 1688, 512];
    public static bool Can(int grade, int authority) => grade >= 0 && grade < Flags.Length && (Flags[grade] & (1 << authority)) != 0;
}

/// <summary>
/// Items, economy and legion management (migrations 042-047): godstones, exceed enchanting, the legion warehouse and
/// wallet, officers and applications, the broker and gift-box rewards. Every call checks that the character is the
/// account's; every change of items or Kina is one transaction that either completes or changes nothing. Ported from
/// A2Core PR #4 (Artezianec) with its storage, broker and godstone faults fixed.
/// </summary>
public interface IEconomyStore
{
    // ---- Godstones (game.character_godstones): a collection of stone counts, not bag items ----
    Task<IReadOnlyDictionary<int, int>> GodstonesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Uses up one GetGodstone item (revision-checked) and adds the stone to the collection.</summary>
    Task<bool> UseGodstoneItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int godstoneId, CancellationToken ct = default);
    /// <summary>Takes one stone from the collection into slot <paramref name="slot"/> of an empty slot of a bag or worn item, paying the costs.</summary>
    Task<bool> SocketGodstoneAsync(VerifiedAccount account, Guid character, Guid item, long revision, int slot, int slots, int godstoneId,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    /// <summary>Takes the materials from the collection and adds the reward (0: none), all in one transaction.</summary>
    Task<bool> CombineGodstonesAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct = default);

    /// <summary>Pays the costs and merges <paramref name="upgrades"/> into the item's upgrades (exceed enchanting), revision-checked.</summary>
    Task<bool> UpgradeItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        JsonElement upgrades, CancellationToken ct = default);

    // ---- Legion warehouse and wallet ----
    Task<GuildStorageView?> GuildStorageAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>
    /// Moves count of a bag item into the warehouse (the whole row, or a split part of a stack) on a free slot of
    /// <paramref name="slots"/>: "ok", "none" (no legion, item gone or changed), "bound", "full" or "authority".
    /// </summary>
    Task<string> StoreGuildItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, int slots, int authorityBit, CancellationToken ct = default);
    /// <summary>Moves count of a warehouse item into the bag (locked row, row count checked): "ok", "none" or "authority".</summary>
    Task<string> TakeGuildItemAsync(VerifiedAccount account, Guid character, Guid item, long count, int authorityBit, CancellationToken ct = default);
    /// <summary>Buys the next warehouse extension (expected count = current + 1) for the costs: "ok", "none", "authority", "max".</summary>
    Task<string> ExtendGuildStorageAsync(VerifiedAccount account, Guid character, int expected, int maxExtensions, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int authorityBit, CancellationToken ct = default);
    Task<bool> SortGuildStorageAsync(VerifiedAccount account, Guid character, long nowMs, long cooldownMs, CancellationToken ct = default);
    Task<IReadOnlyList<GuildStorageRecord>> GuildStorageHistoryAsync(VerifiedAccount account, Guid character, DateTimeOffset since, CancellationToken ct = default);
    /// <summary>The legion wallet (EGuildWalletType 1 EssenceGuild, the only type the client knows); null without a legion.</summary>
    Task<long?> GuildWalletAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>
    /// Changes the wallet by <paramref name="delta"/> in one statement (amount = amount + delta, never below 0; the table
    /// has CHECK amount >= 0): the new amount, or null (no legion, or a spend larger than the wallet). Only the master
    /// may spend (delta below 0).
    /// </summary>
    Task<long?> AddGuildWalletAsync(VerifiedAccount account, Guid character, long delta, CancellationToken ct = default);
    /// <summary>
    /// A donation (GuildDonation_RQ) on the game day <paramref name="day"/>: pays the costs, adds the essence to the wallet
    /// and the exp to the legion, at most <paramref name="perDay"/> a day: "ok", "limit", "none" or "money"; the donations left today.
    /// </summary>
    Task<(string Result, int Left)> GuildDonateAsync(VerifiedAccount account, Guid character, DateOnly day, int perDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        long essence, long exp, CancellationToken ct = default);
    Task<int> GuildDonationsLeftAsync(VerifiedAccount account, Guid character, DateOnly day, int perDay, CancellationToken ct = default);

    // ---- Legion officers and applications ----
    /// <summary>A text field (notice, intro) or the join rule: "ok", "none" or "authority".</summary>
    Task<string> SetGuildTextAsync(VerifiedAccount account, Guid character, string field, string value, int authorityBit, CancellationToken ct = default);
    Task<string> SetGuildJoinRuleAsync(VerifiedAccount account, Guid character, int rule, string password, int authorityBit, CancellationToken ct = default);
    /// <summary>A member's own comment.</summary>
    Task<bool> SetGuildCommentAsync(VerifiedAccount account, Guid character, string comment, CancellationToken ct = default);
    /// <summary>Sets a lower member's grade (1 officer, 2 member) within the officer limit: "ok", "none", "authority", "full".</summary>
    Task<string> SetGuildGradeAsync(VerifiedAccount account, Guid character, string nickname, int grade, int maxOfficers, CancellationToken ct = default);
    /// <summary>The master hands the legion to a member and becomes an officer: "ok", "none" or "authority".</summary>
    Task<string> DelegateGuildMasterAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct = default);
    /// <summary>Applies to a legion (Manual join rule): "ok", "member", "requested", "limit", "full_requests", "none".</summary>
    Task<string> RequestGuildJoinAsync(VerifiedAccount account, Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct = default);
    Task<bool> CancelGuildJoinAsync(VerifiedAccount account, Guid character, long guild, CancellationToken ct = default);
    /// <summary>The applications to the character's legion (officers).</summary>
    Task<IReadOnlyList<GuildJoinRequest>> GuildJoinRequestsAsync(VerifiedAccount account, Guid character, int authorityBit, CancellationToken ct = default);
    /// <summary>The legions the character applied to.</summary>
    Task<IReadOnlyList<GuildView>> SentGuildJoinRequestsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Accepts or refuses an application by name: "ok", "none", "authority", "full" or "member" (joined elsewhere meanwhile).</summary>
    Task<(string Result, Guid? Applicant)> AnswerGuildJoinAsync(VerifiedAccount account, Guid character, string nickname, bool accept, int maxMembers, int authorityBit, CancellationToken ct = default);
    /// <summary>Daily attendance on the game day <paramref name="day"/>: "ok", "done" or "none"; the legion's attendance counts today and yesterday.</summary>
    Task<(string Result, int Today, int Yesterday)> GuildAttendanceAsync(VerifiedAccount account, Guid character, DateOnly day, bool attend, long expGain, CancellationToken ct = default);

    // ---- Broker ----
    /// <summary>
    /// Lists count of a bag item at a unit price, paying the listing fee: the bag row (or a split part of the stack) moves
    /// to escrow. Null when the item is gone, changed, bound, equipped or short, or the fee cannot be paid.
    /// </summary>
    Task<TradeListing?> TradeSellAsync(VerifiedAccount account, Guid character, Guid item, long revision, long count, long unitPrice,
        IReadOnlyList<(Guid Item, long Revision, int Count)> fee, DateTimeOffset expires, CancellationToken ct = default);
    /// <summary>Ends one of the character's own unsold listings (active or expired) and returns the item to the bag: the listing, or null.</summary>
    Task<TradeListing?> TradeCancelAsync(VerifiedAccount account, Guid character, long saleId, CancellationToken ct = default);
    /// <summary>
    /// Buys count of an active listing of another character for count x unit price (the costs, computed by the caller from
    /// the stored price): "ok", "none" (gone, sold, cancelled, expired or own), "count" or "money". The seller is owed a delivery.
    /// </summary>
    Task<(string Result, TradeListing? Listing)> TradePurchaseAsync(VerifiedAccount account, Guid character, long saleId, long count, long expectedUnitPrice,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    Task<TradeListing?> TradeListingAsync(VerifiedAccount account, long saleId, CancellationToken ct = default);
    /// <summary>The character's unsold listings (active and expired) and the sales still owed.</summary>
    Task<(IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries)> TradeSummaryAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Pays a delivery's Kina into the bag once: the Kina paid, or null.</summary>
    Task<long?> CompleteTradeDeliveryAsync(VerifiedAccount account, Guid character, long deliveryId, CancellationToken ct = default);

    // ---- Gift box ----
    Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(VerifiedAccount account, Guid character, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Takes an unexpired reward once and grants its item.</summary>
    Task<PushRewardRecord?> TakePushRewardAsync(VerifiedAccount account, Guid character, int id, DateTimeOffset now, CancellationToken ct = default);
}
