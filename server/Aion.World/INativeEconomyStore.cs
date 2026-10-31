using System.Text.Json;
using Aion.Application.Gameplay;

namespace Aion.World;

/// <summary>
/// The economy part of a connection's store (Aion.Application IEconomyStore without the account, which the world session
/// fixes): godstones, exceed enchanting, the legion warehouse, wallet and officers, the broker and the gift box. A store
/// without it (development logins) answers these requests with a failure result.
/// </summary>
public interface INativeEconomyStore
{
    Task<IReadOnlyDictionary<int, int>> GodstonesAsync(Guid character, CancellationToken ct);
    Task<bool> UseGodstoneItemAsync(Guid character, Guid item, long revision, int godstoneId, CancellationToken ct);
    Task<bool> SocketGodstoneAsync(Guid character, Guid item, long revision, int slot, int slots, int godstoneId, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct);
    Task<bool> CombineGodstonesAsync(Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct);
    Task<bool> UpgradeItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, JsonElement upgrades, CancellationToken ct);
    Task<GuildStorageView?> GuildStorageAsync(Guid character, CancellationToken ct);
    Task<string> StoreGuildItemAsync(Guid character, Guid item, long revision, long count, int slots, int authorityBit, CancellationToken ct);
    Task<string> TakeGuildItemAsync(Guid character, Guid item, long count, int authorityBit, CancellationToken ct);
    Task<string> ExtendGuildStorageAsync(Guid character, int expected, int maxExtensions, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int authorityBit, CancellationToken ct);
    Task<bool> SortGuildStorageAsync(Guid character, long nowMs, long cooldownMs, CancellationToken ct);
    Task<IReadOnlyList<GuildStorageRecord>> GuildStorageHistoryAsync(Guid character, DateTimeOffset since, CancellationToken ct);
    Task<long?> GuildWalletAsync(Guid character, CancellationToken ct);
    Task<long?> AddGuildWalletAsync(Guid character, long delta, CancellationToken ct);
    Task<(string Result, int Left)> GuildDonateAsync(Guid character, DateOnly day, int perDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long essence, long exp, CancellationToken ct);
    Task<int> GuildDonationsLeftAsync(Guid character, DateOnly day, int perDay, CancellationToken ct);
    Task<string> SetGuildTextAsync(Guid character, string field, string value, int authorityBit, CancellationToken ct);
    Task<string> SetGuildJoinRuleAsync(Guid character, int rule, string password, int authorityBit, CancellationToken ct);
    Task<bool> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct);
    Task<string> SetGuildGradeAsync(Guid character, string nickname, int grade, int maxOfficers, CancellationToken ct);
    Task<string> DelegateGuildMasterAsync(Guid character, string nickname, CancellationToken ct);
    Task<string> RequestGuildJoinAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct);
    Task<bool> CancelGuildJoinAsync(Guid character, long guild, CancellationToken ct);
    Task<IReadOnlyList<GuildJoinRequest>> GuildJoinRequestsAsync(Guid character, int authorityBit, CancellationToken ct);
    Task<IReadOnlyList<GuildView>> SentGuildJoinRequestsAsync(Guid character, CancellationToken ct);
    Task<(string Result, Guid? Applicant)> AnswerGuildJoinAsync(Guid character, string nickname, bool accept, int maxMembers, int authorityBit, CancellationToken ct);
    Task<(string Result, int Today, int Yesterday)> GuildAttendanceAsync(Guid character, DateOnly day, bool attend, long expGain, CancellationToken ct);
    Task<TradeListing?> TradeSellAsync(Guid character, Guid item, long revision, long count, long unitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> fee, DateTimeOffset expires, CancellationToken ct);
    Task<TradeListing?> TradeCancelAsync(Guid character, long saleId, CancellationToken ct);
    Task<(string Result, TradeListing? Listing)> TradePurchaseAsync(Guid character, long saleId, long count, long expectedUnitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct);
    Task<TradeListing?> TradeListingAsync(long saleId, CancellationToken ct);
    Task<(IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries)> TradeSummaryAsync(Guid character, CancellationToken ct);
    Task<long?> CompleteTradeDeliveryAsync(Guid character, long deliveryId, CancellationToken ct);
    Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(Guid character, DateTimeOffset now, CancellationToken ct);
    Task<PushRewardRecord?> TakePushRewardAsync(Guid character, int id, DateTimeOffset now, CancellationToken ct);
}
