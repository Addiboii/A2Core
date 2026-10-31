using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>The economy requests of a game connection, each revalidating the world session first (IEconomyStore).</summary>
public sealed partial class ServiceNativeCharacterStore : INativeEconomyStore
{
    IEconomyStore Economy => game as IEconomyStore ?? throw new InvalidOperationException("No economy store.");

    public async Task<IReadOnlyDictionary<int, int>> GodstonesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GodstonesAsync(identity.Account, character, ct);
    }

    public async Task<bool> UseGodstoneItemAsync(Guid character, Guid item, long revision, int godstoneId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.UseGodstoneItemAsync(identity.Account, character, item, revision, godstoneId, ct);
    }

    public async Task<bool> SocketGodstoneAsync(Guid character, Guid item, long revision, int slot, int slots, int godstoneId, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SocketGodstoneAsync(identity.Account, character, item, revision, slot, slots, godstoneId, costs, ct);
    }

    public async Task<bool> CombineGodstonesAsync(Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.CombineGodstonesAsync(identity.Account, character, materials, rewardId, ct);
    }

    public async Task<bool> UpgradeItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, JsonElement upgrades, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.UpgradeItemAsync(identity.Account, character, item, revision, costs, upgrades, ct);
    }

    public async Task<GuildStorageView?> GuildStorageAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildStorageAsync(identity.Account, character, ct);
    }

    public async Task<string> StoreGuildItemAsync(Guid character, Guid item, long revision, long count, int slots, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.StoreGuildItemAsync(identity.Account, character, item, revision, count, slots, authorityBit, ct);
    }

    public async Task<string> TakeGuildItemAsync(Guid character, Guid item, long count, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TakeGuildItemAsync(identity.Account, character, item, count, authorityBit, ct);
    }

    public async Task<string> ExtendGuildStorageAsync(Guid character, int expected, int maxExtensions, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.ExtendGuildStorageAsync(identity.Account, character, expected, maxExtensions, costs, authorityBit, ct);
    }

    public async Task<bool> SortGuildStorageAsync(Guid character, long nowMs, long cooldownMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SortGuildStorageAsync(identity.Account, character, nowMs, cooldownMs, ct);
    }

    public async Task<IReadOnlyList<GuildStorageRecord>> GuildStorageHistoryAsync(Guid character, DateTimeOffset since, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildStorageHistoryAsync(identity.Account, character, since, ct);
    }

    public async Task<long?> GuildWalletAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildWalletAsync(identity.Account, character, ct);
    }

    public async Task<long?> AddGuildWalletAsync(Guid character, long delta, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.AddGuildWalletAsync(identity.Account, character, delta, ct);
    }

    public async Task<(string Result, int Left)> GuildDonateAsync(Guid character, DateOnly day, int perDay, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long essence, long exp, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildDonateAsync(identity.Account, character, day, perDay, costs, essence, exp, ct);
    }

    public async Task<int> GuildDonationsLeftAsync(Guid character, DateOnly day, int perDay, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildDonationsLeftAsync(identity.Account, character, day, perDay, ct);
    }

    public async Task<string> SetGuildTextAsync(Guid character, string field, string value, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SetGuildTextAsync(identity.Account, character, field, value, authorityBit, ct);
    }

    public async Task<string> SetGuildJoinRuleAsync(Guid character, int rule, string password, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SetGuildJoinRuleAsync(identity.Account, character, rule, password, authorityBit, ct);
    }

    public async Task<bool> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SetGuildCommentAsync(identity.Account, character, comment, ct);
    }

    public async Task<string> SetGuildGradeAsync(Guid character, string nickname, int grade, int maxOfficers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SetGuildGradeAsync(identity.Account, character, nickname, grade, maxOfficers, ct);
    }

    public async Task<string> DelegateGuildMasterAsync(Guid character, string nickname, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.DelegateGuildMasterAsync(identity.Account, character, nickname, ct);
    }

    public async Task<string> RequestGuildJoinAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.RequestGuildJoinAsync(identity.Account, character, guild, comment, maxSent, maxReceived, ct);
    }

    public async Task<bool> CancelGuildJoinAsync(Guid character, long guild, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.CancelGuildJoinAsync(identity.Account, character, guild, ct);
    }

    public async Task<IReadOnlyList<GuildJoinRequest>> GuildJoinRequestsAsync(Guid character, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildJoinRequestsAsync(identity.Account, character, authorityBit, ct);
    }

    public async Task<IReadOnlyList<GuildView>> SentGuildJoinRequestsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.SentGuildJoinRequestsAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, Guid? Applicant)> AnswerGuildJoinAsync(Guid character, string nickname, bool accept, int maxMembers, int authorityBit, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.AnswerGuildJoinAsync(identity.Account, character, nickname, accept, maxMembers, authorityBit, ct);
    }

    public async Task<(string Result, int Today, int Yesterday)> GuildAttendanceAsync(Guid character, DateOnly day, bool attend, long expGain, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.GuildAttendanceAsync(identity.Account, character, day, attend, expGain, ct);
    }

    public async Task<TradeListing?> TradeSellAsync(Guid character, Guid item, long revision, long count, long unitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> fee, DateTimeOffset expires, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TradeSellAsync(identity.Account, character, item, revision, count, unitPrice, fee, expires, ct);
    }

    public async Task<TradeListing?> TradeCancelAsync(Guid character, long saleId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TradeCancelAsync(identity.Account, character, saleId, ct);
    }

    public async Task<(string Result, TradeListing? Listing)> TradePurchaseAsync(Guid character, long saleId, long count, long expectedUnitPrice, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TradePurchaseAsync(identity.Account, character, saleId, count, expectedUnitPrice, costs, ct);
    }

    public async Task<TradeListing?> TradeListingAsync(long saleId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TradeListingAsync(identity.Account, saleId, ct);
    }

    public async Task<(IReadOnlyList<TradeListing> Listings, IReadOnlyList<TradeDelivery> Deliveries)> TradeSummaryAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TradeSummaryAsync(identity.Account, character, ct);
    }

    public async Task<long?> CompleteTradeDeliveryAsync(Guid character, long deliveryId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.CompleteTradeDeliveryAsync(identity.Account, character, deliveryId, ct);
    }

    public async Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(Guid character, DateTimeOffset now, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.PushRewardsAsync(identity.Account, character, now, ct);
    }

    public async Task<PushRewardRecord?> TakePushRewardAsync(Guid character, int id, DateTimeOffset now, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await Economy.TakePushRewardAsync(identity.Account, character, id, now, ct);
    }
}
