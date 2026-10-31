using System.Text.Json;
using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Stored characters of one authenticated game connection, through the same services the
/// /internal/world bridge used. The world session token fixes account and world; every call
/// revalidates it (and its parent browser session), and disposing closes it.
/// </summary>
public sealed class ServiceNativeCharacterStore(string token, WorldIdentity identity, IWorldSessions sessions,
    IWireCharacterRepository wire, IGameplayStore game, CharacterPersistenceService persistence, IAccountQunaBalance accountQuna) : INativeCharacterStore, IGameplayFlushStore, IQuestBatchStore
{
    bool closed;
    (CharacterState State, int MapId, Vector3d Position)? pendingLocation;
    List<ItemState>? itemCache;
    Guid itemCacheCharacter;

    public Guid AccountId => identity.Account.AccountId;
    public int WorldId => identity.WorldId;

    public Task<int> ExtendedCharacterSlotsAsync(CancellationToken ct) =>
        game.ExtendedCharacterSlotsAsync(identity.Account, ct);

    // Every store call but a cached inventory read passes here: any of them may change items (an enchant, a purchase,
    // a sale), so the inventory cache is dropped and the next read loads the stored rows. A stale cache answered an
    // enchant once and then refused every retry on the old revision while the client kept showing +0 (live 2026-10-30).
    async Task ValidateAsync(CancellationToken ct)
    {
        InvalidateItemCache();
        var current = closed ? null : await sessions.ValidateAsync(token, ct);
        if (current is null || current.Account.AccountId != AccountId || current.WorldId != WorldId)
            throw new InvalidOperationException("World session is no longer valid.");
    }

    public async Task<IReadOnlyList<CharacterState>> LobbyAsync(CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await wire.ListWorldAsync(AccountId, WorldId, ct);
    }

    public async Task<CharacterState?> LoadWireAsync(int wireId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return wireId > 0 ? await wire.LoadWireAsync(AccountId, WorldId, wireId, ct) : null;
    }

    public async Task<CharacterState> CreateAsync(CreatePlayer input, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var id = await game.CreateAsync(identity.Account, input with { WorldId = WorldId }, ct);
        var state = await persistence.LoadAsync(identity.Account, id, ct);
        return state?.Summary.WorldId == WorldId ? state : throw new InvalidOperationException("Created character could not be loaded.");
    }

    public async Task<CharacterState?> RecordLoginAsync(CharacterState state, long nowMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await wire.RecordLoginAsync(AccountId, state.Summary.CharacterId, DateTimeOffset.FromUnixTimeMilliseconds(nowMs), ct);
    }

    public async Task<CharacterState?> RecordLogoutAsync(CharacterState state, long nowMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await wire.RecordLogoutAsync(AccountId, state.Summary.CharacterId, DateTimeOffset.FromUnixTimeMilliseconds(nowMs), ct);
    }

    public Task<CharacterState> SavePositionAsync(CharacterState state, int mapId, Vector3d position, CancellationToken ct)
    {
        if (!position.IsFinite || mapId <= 0) throw new ArgumentException("Invalid location");
        var progress = state.Progress with { MapId = mapId, Position = StoredCharacters.PositionJson(position) };
        pendingLocation = (state, mapId, position);
        return Task.FromResult(state with { Progress = progress });
    }

    public async Task<CharacterState?> FlushAsync(CancellationToken ct = default)
    {
        if (closed) return null;
        if (pendingLocation is not { } pending) return null;
        var (state, mapId, position) = pending;
        for (var attempt = 0; ; attempt++)
        {
            await ValidateAsync(ct);
            var positionJson = StoredCharacters.PositionJson(position);
            var revision = await persistence.SavePositionAsync(identity.Account, state.Summary.CharacterId, state.Revision, mapId, positionJson, ct);
            var progress = state.Progress with { MapId = mapId, Position = positionJson };
            if (revision is { } saved)
            {
                pendingLocation = null;
                return state with { Progress = progress, Revision = saved };
            }
            if (attempt > 0) throw new InvalidOperationException("Character changed concurrently.");
            state = await persistence.LoadAsync(identity.Account, state.Summary.CharacterId, ct)
                ?? throw new InvalidOperationException("Character is no longer available.");
            pendingLocation = (state, mapId, position);
        }
    }

    async Task RefreshItemCacheAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        itemCache = (await game.InventoryAsync(identity.Account, character, ct))?.Items?.ToList() ?? [];
        itemCacheCharacter = character;
    }

    void InvalidateItemCache() => itemCache = null;

    public async Task<IReadOnlyList<ItemState>> InventoryAsync(Guid character, CancellationToken ct)
    {
        if (itemCache is not null && itemCacheCharacter == character) return itemCache;
        await RefreshItemCacheAsync(character, ct);
        return itemCache!;
    }

    public Task<long> AccountQunaBalanceAsync(CancellationToken ct) => accountQuna.SumAsync(AccountId, ct);

    public async Task<IReadOnlyList<QuestRecord>> SaveQuestBatchAsync(Guid character, IReadOnlyList<QuestRecord> quests, CancellationToken ct)
    {
        if (quests.Count == 0) return [];
        await ValidateAsync(ct);
        return await game.SaveQuestsAsync(identity.Account, character, quests, ct);
    }

    public async Task<bool> MoveItemAsync(Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var moved = await game.MoveItemAsync(identity.Account, character, item, revision, equipmentSlot, ct);
        if (moved) await RefreshItemCacheAsync(character, ct);
        return moved;
    }

    public async Task<IReadOnlyList<QuestRecord>> QuestsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.QuestsAsync(identity.Account, character, ct) ?? [];
    }

    public async Task<QuestRecord> SaveQuestAsync(Guid character, QuestRecord quest, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveQuestAsync(identity.Account, character, quest, ct);
    }

    public async Task<bool> RemoveQuestAsync(Guid character, string code, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RemoveQuestAsync(identity.Account, character, code, ct);
    }

    public async Task<long?> RewardQuestAsync(Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var paid = await game.RewardQuestAsync(identity.Account, character, code, experience, items, ct);
        if (paid is not null) await RefreshItemCacheAsync(character, ct);
        return paid;
    }

    internal VerifiedAccount Account => identity.Account;

    public async Task<bool> AscendAsync(Guid character, int grade, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AscendAsync(identity.Account, character, grade, ct);
    }

    public async Task<IReadOnlyList<SocialRelation>> SocialAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SocialAsync(identity.Account, character, ct) ?? [];
    }

    public async Task<(string Result, SocialRelation? Target)> FriendRequestAsync(Guid character, string targetName, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.FriendRequestAsync(identity.Account, character, targetName, ct);
    }

    public async Task<bool> FriendRespondAsync(Guid character, Guid from, bool accept, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.FriendRespondAsync(identity.Account, character, from, accept, ct);
    }

    public async Task<bool> FriendCancelAsync(Guid character, Guid to, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.FriendCancelAsync(identity.Account, character, to, ct);
    }

    public async Task<bool> FriendDeleteAsync(Guid character, Guid friend, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.FriendDeleteAsync(identity.Account, character, friend, ct);
    }

    public async Task<bool> FriendMemoAsync(Guid character, Guid friend, string memo, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.FriendMemoAsync(identity.Account, character, friend, memo, ct);
    }

    public async Task<bool> RequestCharacterDeleteAsync(Guid character, DateTimeOffset at, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RequestCharacterDeleteAsync(identity.Account, character, at, ct);
    }

    public async Task<bool> CancelCharacterDeleteAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CancelCharacterDeleteAsync(identity.Account, character, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CharacterDeletionsAsync(CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CharacterDeletionsAsync(identity.Account, ct);
    }

    public async Task<string> RenameCharacterAsync(Guid character, string name, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RenameCharacterAsync(identity.Account, character, name, ct);
    }

    public async Task<bool> SwapLobbySlotsAsync(IReadOnlyList<(long WireId, int Slot)> assignments, int slotCapacity, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SwapLobbySlotsAsync(identity.Account, WorldId, assignments, slotCapacity, ct);
    }

    public async Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int level, int bonus, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.EnchantItemAsync(identity.Account, character, item, revision, costs, level, bonus, ct);
    }

    public async Task<bool> ExceedEnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int surpassLevel, int surpassBonus, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ExceedEnchantItemAsync(identity.Account, character, item, revision, costs, surpassLevel, surpassBonus, ct);
    }

    public async Task<bool> SocketGodstoneAsync(Guid character, Guid equip, long equipRevision, Guid material, long materialRevision,
        IReadOnlyList<int> godstones, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SocketGodstoneAsync(identity.Account, character, equip, equipRevision, material, materialRevision, godstones, ct);
    }

    public async Task<IReadOnlyDictionary<int, int>> GodstoneCollectionAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GodstoneCollectionAsync(identity.Account, character, ct);
    }

    public async Task<bool> UpsertGodstoneCollectionAsync(Guid character, IReadOnlyList<(int GodstoneId, int Delta)> updates, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.UpsertGodstoneCollectionAsync(identity.Account, character, updates, ct);
    }

    public async Task<bool> CombineGodstonesAsync(Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CombineGodstonesAsync(identity.Account, character, materials, rewardId, ct);
    }

    public async Task<GuildView?> GuildOfAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildOfAsync(identity.Account, character, ct);
    }

    public async Task<IReadOnlyList<GuildView>> SearchGuildsAsync(string name, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SearchGuildsAsync(identity.Account, name, ct);
    }

    public async Task<string> CreateGuildAsync(Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CreateGuildAsync(identity.Account, character, name, joinRule, password, emblem, intro, costs, ct);
    }

    public async Task<string> JoinGuildAsync(Guid character, long guild, string password, string comment, int maxMembers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.JoinGuildAsync(identity.Account, character, guild, password, comment, maxMembers, ct);
    }

    public async Task<IReadOnlyList<GuildJoinRequestSent>> GuildJoinRequestsSentAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildJoinRequestsSentAsync(identity.Account, character, ct);
    }

    public async Task<IReadOnlyList<GuildJoinRequestRecv>> GuildJoinRequestsRecvAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildJoinRequestsRecvAsync(identity.Account, character, ct);
    }

    public async Task<string> ConfirmGuildJoinAsync(Guid character, string nickname, bool accept, int maxMembers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConfirmGuildJoinAsync(identity.Account, character, nickname, accept, maxMembers, ct);
    }

    public async Task<string> CancelGuildJoinRequestAsync(Guid character, long guildId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CancelGuildJoinRequestAsync(identity.Account, character, guildId, ct);
    }

    public async Task<string> SetGuildNoticeAsync(Guid character, string notice, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetGuildNoticeAsync(identity.Account, character, notice, ct);
    }

    public async Task<string> SetGuildMemberGradeAsync(Guid character, string nickname, int grade, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetGuildMemberGradeAsync(identity.Account, character, nickname, grade, ct);
    }

    public async Task<string> SetGuildAuthorityAsync(Guid character, IReadOnlyList<(byte Grade, int Bits)> authority, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetGuildAuthorityAsync(identity.Account, character, authority, ct);
    }

    public async Task<string> DelegateGuildMasterAsync(Guid character, string nickname, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DelegateGuildMasterAsync(identity.Account, character, nickname, ct);
    }

    public async Task<(string Result, int Today, int Yesterday, bool CanToday, bool CanReward)> GuildAttendanceInfoAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildAttendanceInfoAsync(identity.Account, character, ct);
    }

    public async Task<string> MarkGuildAttendanceAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarkGuildAttendanceAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, int YesterdayCount)> TakeGuildAttendanceRewardAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakeGuildAttendanceRewardAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, long Revision, int ExtensionCount, IReadOnlyList<GuildStorageItem> Items)> GuildStorageInfoAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildStorageInfoAsync(identity.Account, character, ct);
    }

    public async Task<string> StoreGuildStorageAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.StoreGuildStorageAsync(identity.Account, character, items, ct);
    }

    public async Task<string> TakeGuildStorageAsync(Guid character, IReadOnlyList<(long ItemKey, int Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakeGuildStorageAsync(identity.Account, character, items, ct);
    }

    public async Task<(string Result, long LastHistoryMs, IReadOnlyList<GuildStorageHistoryRow> Rows)> GuildStorageHistoryAsync(Guid character, long afterMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildStorageHistoryAsync(identity.Account, character, afterMs, ct);
    }

    public async Task<(string Result, long LastSortMs)> SortGuildStorageAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SortGuildStorageAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, int ExtensionCount)> ExtendGuildStorageAsync(Guid character, int expectedExtension, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ExtendGuildStorageAsync(identity.Account, character, expectedExtension, ct);
    }

    public async Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> DonateGuildWalletAsync(Guid character, long kina,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DonateGuildWalletAsync(identity.Account, character, kina, costs, ct);
    }

    public async Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> WithdrawGuildWalletAsync(Guid character, long kina, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.WithdrawGuildWalletAsync(identity.Account, character, kina, ct);
    }

    public async Task<(string Result, long UnionId, string Name)> CreateGuildUnionAsync(Guid character, string name, int joinRule, string password, int emblem, string intro, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CreateGuildUnionAsync(identity.Account, character, name, joinRule, password, emblem, intro, ct);
    }

    public async Task<(string Result, GuildUnionView? Union)> GuildUnionInfoAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildUnionInfoAsync(identity.Account, character, ct);
    }

    public async Task<string> DisbandGuildUnionAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DisbandGuildUnionAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, int JoinResult)> JoinGuildUnionAsync(Guid character, long unionId, string password, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.JoinGuildUnionAsync(identity.Account, character, unionId, password, ct);
    }

    public async Task<string> LeaveGuildUnionAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.LeaveGuildUnionAsync(identity.Account, character, ct);
    }

    public async Task<string> RequestGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, int relation, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RequestGuildRelationAsync(identity.Account, character, targetType, targetName, serverId, relation, ct);
    }

    public async Task<string> ConfirmGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, bool accept, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConfirmGuildRelationAsync(identity.Account, character, targetType, targetName, serverId, accept, ct);
    }

    public async Task<string> CancelGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CancelGuildRelationAsync(identity.Account, character, targetType, targetName, serverId, ct);
    }

    public async Task<string> GrantGuildAgitDecoAsync(Guid character, Guid item, long revision, int templateId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GrantGuildAgitDecoAsync(identity.Account, character, item, revision, templateId, ct);
    }

    public async Task<string> LeaveGuildAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.LeaveGuildAsync(identity.Account, character, ct);
    }

    public async Task<string> KickGuildMemberAsync(Guid character, string nickname, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.KickGuildMemberAsync(identity.Account, character, nickname, ct);
    }

    public async Task<string> DisbandGuildAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DisbandGuildAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, int Remain, int? NewGuildLevel)> DonateGuildAsync(Guid character, int donationLevel,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DonateGuildAsync(identity.Account, character, donationLevel, costs, ct);
    }

    public async Task<int> GuildDonationRemainAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GuildDonationRemainAsync(identity.Account, character, ct);
    }

    public async Task<(string Result, int AgitId, long EndMs)> ContractGuildAgitAsync(Guid character, int agitId,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long nowMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ContractGuildAgitAsync(identity.Account, character, agitId, costs, nowMs, ct);
    }

    public async Task<string> EquipGuildAgitDecoAsync(Guid character, int decoId, int slotId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.EquipGuildAgitDecoAsync(identity.Account, character, decoId, slotId, ct);
    }

    public async Task<string> UnequipGuildAgitDecoAsync(Guid character, int decoId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.UnequipGuildAgitDecoAsync(identity.Account, character, decoId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastRenamesAsync(CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.LastRenamesAsync(identity.Account, ct);
    }

    public async Task<IReadOnlyList<(int Target, int GoodsId, string Period, int Count)>> ShopPurchasesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ShopPurchasesAsync(identity.Account, character, ct);
    }

    public async Task<bool> ShopBuyAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ShopBuyAsync(identity.Account, character, costs, goods, limits, ct);
    }

    public async Task<IReadOnlyList<(int Target, int RecipeId, string Period, int Count)>> ConversionCountsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConversionCountsAsync(identity.Account, character, WorldId, ct);
    }

    public async Task<bool> ConversionAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int RecipeId, string Period, int Count, int Limit)> limits, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConversionAsync(identity.Account, character, WorldId, costs, goods, limits, ct);
    }

    public async Task<bool> SellToMerchantAsync(Guid character, Guid item, long revision, int count, long kina, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SellToMerchantAsync(identity.Account, character, item, revision, count, kina, ct);
    }

    public async Task<IReadOnlyList<ItemState>> BuybackAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.BuybackAsync(identity.Account, character, ct);
    }

    public async Task<string> RestoreItemAsync(Guid character, Guid item, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RestoreItemAsync(identity.Account, character, item, costs, ct);
    }

    public async Task<bool> MoveBagItemAsync(Guid character, Guid item, long revision, int slot, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MoveBagItemAsync(identity.Account, character, item, revision, slot, ct);
    }

    public async Task<bool> MoveItemToContainerAsync(Guid character, Guid item, long revision, string location, int slot, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MoveItemToContainerAsync(identity.Account, character, item, revision, location, slot, ct);
    }

    public async Task<IReadOnlyList<PostRecord>> PostsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.PostsAsync(identity.Account, character, ct);
    }

    public async Task<PostRecord?> SendPostAsync(Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SendPostAsync(identity.Account, toCharacter, dataId, parameters, attachments, tab, ct);
    }

    public async Task<bool> MarkPostsAsync(Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MarkPostsAsync(identity.Account, character, ids, delete, ct);
    }

    public async Task<bool> TakePostAsync(Guid character, long id, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakePostAsync(identity.Account, character, id, ct);
    }

    public async Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(Guid character, long nowMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.PushRewardsAsync(identity.Account, character, nowMs, ct);
    }

    public async Task<PushRewardRecord?> AddPushRewardAsync(byte type, string sender, string title, string body, int itemTemplateId, long itemCount, long expireMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AddPushRewardAsync(identity.Account, type, sender, title, body, itemTemplateId, itemCount, expireMs, ct);
    }

    public async Task<PushRewardRecord?> TakePushRewardAsync(Guid character, byte type, int id, byte targetSequence, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakePushRewardAsync(identity.Account, character, type, id, targetSequence, ct);
    }

    public async Task<PostRecord?> SendPlayerPostAsync(Guid fromCharacter, Guid toCharacter, int dataId, string parameters,
        IReadOnlyList<(int TemplateId, long Count)> attachments, long codKina, long mailFeeKina, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SendPlayerPostAsync(identity.Account, fromCharacter, toCharacter, dataId, parameters, attachments, codKina, mailFeeKina, ct);
    }

    public async Task<TradeSummaryRecord> TradeSummaryAsync(Guid character, int tradeServerId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradeSummaryAsync(identity.Account, character, tradeServerId, ct);
    }

    public async Task<(string Result, TradeListingRecord? Listing)> TradeListItemAsync(Guid character, long itemWireKey, long count, long priceKina, int tradeServerId, long nowMs, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradeListItemAsync(identity.Account, character, itemWireKey, count, priceKina, tradeServerId, nowMs, ct);
    }

    public async Task<(string Result, long DeliveryId)> TradePurchaseAsync(Guid buyerCharacter, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradePurchaseAsync(identity.Account, buyerCharacter, saleId, templateId, count, tradeServerId, ct);
    }

    public async Task<(string Result, TradeListingRecord? Listing)> TradeCancelListingAsync(Guid character, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradeCancelListingAsync(identity.Account, character, saleId, templateId, count, tradeServerId, ct);
    }

    public async Task<IReadOnlyList<TradeDeliveryRecord>> TradeDeliveriesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradeDeliveriesAsync(identity.Account, character, ct);
    }

    public async Task<bool> TradeCompleteDeliveryAsync(Guid character, long deliveryId, int tradeServerId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TradeCompleteDeliveryAsync(identity.Account, character, deliveryId, tradeServerId, ct);
    }

    public async Task<Guid?> CharacterByNameAsync(string name, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CharacterByNameAsync(identity.Account, name, ct);
    }

    public async Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.BossChallengesAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveBossChallengeAsync(Guid character, BossChallengeRecord record, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveBossChallengeAsync(identity.Account, character, record, ct);
    }

    public async Task<IReadOnlyList<(int TicketId, string Period, int Count)>> ExpeditionTicketsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ExpeditionTicketsAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveExpeditionTicketsAsync(Guid character, IReadOnlyList<(int TicketId, string Period, int Count)> rows, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveExpeditionTicketsAsync(identity.Account, character, rows, ct);
    }

    public async Task<AbyssRecord?> AbyssAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AbyssAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveAbyssAsync(Guid character, AbyssRecord record, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveAbyssAsync(identity.Account, character, record, ct);
    }

    public Task AddPendingSiegeApForGuildAsync(long guildId, int ap, IReadOnlyList<Guid> onlineCharacterIds, CancellationToken ct) =>
        game.AddPendingSiegeApForGuildAsync(guildId, ap, onlineCharacterIds, ct);

    public Task<AirspaceWorldRecord> AirspaceWorldAsync(CancellationToken ct) =>
        game.AirspaceWorldAsync(WorldId, ct);

    public Task SaveAirspaceWorldAsync(AirspaceWorldRecord record, CancellationToken ct) =>
        game.SaveAirspaceWorldAsync(WorldId, record, ct);

    public Task<WorldResetCursorRecord> ResetCursorsAsync(CancellationToken ct) => game.ResetCursorsAsync(WorldId, ct);
    public Task SaveResetCursorsAsync(WorldResetCursorRecord record, CancellationToken ct) => game.SaveResetCursorsAsync(WorldId, record, ct);
    public Task ApplyDailyLegionResetsAsync(DateTime gameDayUtc, CancellationToken ct) => game.ApplyDailyLegionResetsAsync(gameDayUtc, ct);
    public Task ApplyWeeklyLegionResetsAsync(long weeklyBoundaryMs, CancellationToken ct) => game.ApplyWeeklyLegionResetsAsync(weeklyBoundaryMs, ct);

    public async Task<IReadOnlyList<RewardLimitRecord>> RewardLimitsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RewardLimitsAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveRewardLimitsAsync(Guid character, IReadOnlyList<RewardLimitRecord> rows, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveRewardLimitsAsync(identity.Account, character, rows, ct);
    }

    public async Task<IReadOnlyList<DungeonCooltimeRecord>> DungeonCooltimesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DungeonCooltimesAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveDungeonCooltimesAsync(Guid character, IReadOnlyList<DungeonCooltimeRecord> rows, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveDungeonCooltimesAsync(identity.Account, character, rows, ct);
    }

    public async Task<IReadOnlyList<CollectionRecord>> CollectionsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CollectionsAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveCollectionAsync(Guid character, int collectionId, int listId, int registered, bool rewarded, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveCollectionAsync(identity.Account, character, collectionId, listId, registered, rewarded, ct);
    }

    public async Task<DaevanionProgress?> DaevanionAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.DaevanionAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveDaevanionAsync(Guid character, DaevanionProgress progress, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveDaevanionAsync(identity.Account, character, progress, ct);
    }

    public async Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AchievementsAsync(identity.Account, character, ct);
    }

    public async Task<IReadOnlyList<(int Group, int Step)>> GroupRewardsTakenAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GroupRewardsTakenAsync(identity.Account, character, ct);
    }

    public async Task<bool> SaveAchievementsAsync(Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveAchievementsAsync(identity.Account, character, records, ct);
    }

    public async Task<bool> TakeAchievementRewardAsync(Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakeAchievementRewardAsync(identity.Account, character, achievementId, step, items, ct);
    }

    public async Task<bool> TakeGroupRewardAsync(Guid character, int groupId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TakeGroupRewardAsync(identity.Account, character, groupId, step, items, ct);
    }

    public async Task<bool> SortBagAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SortBagAsync(identity.Account, character, ct);
    }

    public async Task<bool> SortContainerAsync(Guid character, string location, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SortContainerAsync(identity.Account, character, location, ct);
    }

    public async Task<IReadOnlyList<QuickSlotRecord>> QuickSlotsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.QuickSlotsAsync(identity.Account, character, ct) ?? [];
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> CharacterSettingsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CharacterSettingsAsync(identity.Account, character, ct) ?? new Dictionary<string, JsonElement>();
    }

    public async Task<bool> SaveCharacterSettingAsync(Guid character, string name, JsonElement value, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveCharacterSettingAsync(identity.Account, character, name, value, ct);
    }

    public async Task<bool> SaveQuickSlotAsync(Guid character, int number, QuickSlotRecord? slot, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveQuickSlotAsync(identity.Account, character, number, slot, ct);
    }

    public async Task<bool> GrantLootAsync(Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var granted = await game.GrantLootAsync(identity.Account, character, items, ct);
        if (granted) await RefreshItemCacheAsync(character, ct);
        return granted;
    }

    public async Task<bool> SaveProgressAsync(Guid character, int level, long experience, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveProgressAsync(identity.Account, character, level, experience, ct);
    }

    public async Task<RideCollection> RidesAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.RidesAsync(identity.Account, character, ct) ?? new RideCollection(0, []);
    }

    public async Task<bool> ConsumeAndGrantAsync(Guid character, Guid item, long revision, int count, IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConsumeAndGrantAsync(identity.Account, character, item, revision, count, rewards, ct);
    }

    public async Task<RideRecord?> UseRideItemAsync(Guid character, Guid item, long revision, int rideId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.UseRideItemAsync(identity.Account, character, item, revision, rideId, ct);
    }

    public async Task<RideRecord?> AddRideSoulsAsync(Guid character, Guid item, long revision, int rideId, int souls, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AddRideSoulsAsync(identity.Account, character, item, revision, rideId, souls, ct);
    }

    public async Task<RideRecord?> AcquireRideWithSoulsAsync(Guid character, int rideId, int cost, int crystalType, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AcquireRideWithSoulsAsync(identity.Account, character, rideId, cost, crystalType, ct);
    }

    public async Task<RideRecord?> LevelUpRideAsync(Guid character, int rideId, int cost, int crystalType, int maxLevel, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.LevelUpRideAsync(identity.Account, character, rideId, cost, crystalType, maxLevel, ct);
    }

    public async Task<int?> AddRideCrystalsAsync(Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.AddRideCrystalsAsync(identity.Account, character, item, revision, crystalType, count, ct);
    }

    public async Task<bool> SaveCreatureBoardAsync(Guid character, CreatureBoardRecord board, IReadOnlyDictionary<int, int> crystalCost,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveCreatureBoardAsync(identity.Account, character, board, crystalCost, kina, ct);
    }

    public async Task<bool> CrystallizeAsync(Guid character, IReadOnlyList<(int RideId, int Points, int CrystalType)> targets, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.CrystallizeAsync(identity.Account, character, targets, ct);
    }

    public async Task<bool> ConsumeItemAsync(Guid character, Guid item, long revision, int count, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.ConsumeItemAsync(identity.Account, character, item, revision, count, ct);
    }

    public async Task<bool> SetDefaultRideAsync(Guid character, int rideId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetDefaultRideAsync(identity.Account, character, rideId, ct);
    }

    public async Task<bool> NameTakenAsync(string name, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.NameTakenAsync(identity.Account, name, ct);
    }

    public async Task<SocialRelation?> BlockAsync(Guid character, string targetName, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.BlockAsync(identity.Account, character, targetName, ct);
    }

    public async Task<bool> UnblockAsync(Guid character, Guid blocked, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.UnblockAsync(identity.Account, character, blocked, ct);
    }

    public async Task<bool> SocialBlockedAsync(Guid character, Guid other, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SocialBlockedAsync(identity.Account, character, other, ct);
    }

    public async Task<bool> ExchangeAsync(Guid character, IReadOnlyList<(Guid Item, long Count)> gives, INativeCharacterStore otherStore, Guid other,
        IReadOnlyList<(Guid Item, long Count)> receives, long givesKina = 0, long receivesKina = 0, CancellationToken ct = default)
    {
        if (otherStore is not ServiceNativeCharacterStore partner) return false;
        await ValidateAsync(ct);
        await partner.ValidateAsync(ct);
        return await game.ExchangeAsync(identity.Account, character, gives, partner.Account, other, receives, givesKina, receivesKina, ct);
    }

    public async Task<IReadOnlyList<int>> MapFogAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MapFogAsync(identity.Account, character, ct) ?? [];
    }

    public async Task<IReadOnlyList<int>> OpenMapFogAsync(Guid character, IReadOnlyList<int> groups, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.OpenMapFogAsync(identity.Account, character, groups, ct);
    }

    public async Task<IReadOnlyList<int>> TeleportArtifactsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.TeleportArtifactsAsync(identity.Account, character, ct) ?? [];
    }

    public async Task<bool> EngraveTeleportArtifactAsync(Guid character, int artifactId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.EngraveTeleportArtifactAsync(identity.Account, character, artifactId, ct);
    }

    public async Task<bool> SoulBindItemAsync(Guid character, Guid item, byte count, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var bound = await game.SoulBindItemAsync(identity.Account, character, item, count, ct);
        if (bound) await RefreshItemCacheAsync(character, ct);
        return bound;
    }

    public async Task<bool> SetRolledStatsAsync(Guid character, Guid item, long revision, JsonElement rolled, CancellationToken ct)
    {
        await ValidateAsync(ct);
        var ok = await game.SetRolledStatsAsync(identity.Account, character, item, revision, rolled, ct);
        if (ok) await RefreshItemCacheAsync(character, ct);
        return ok;
    }

    public async Task<IReadOnlyList<int>> WingsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return (await game.InventoryAsync(identity.Account, character, ct))?.Wings ?? [];
    }

    public async Task<IReadOnlyList<SkillState>> SkillsAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return (await game.InventoryAsync(identity.Account, character, ct))?.Skills ?? [];
    }

    public async Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, int purchased, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetSkillLevelAsync(identity.Account, character, skillId, level, purchased, ct);
    }

    public async Task<CharacterExtras> ExtrasAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return (await game.InventoryAsync(identity.Account, character, ct))?.Extras ?? CharacterExtras.None;
    }

    public async Task<bool> GrantWingAsync(Guid character, int wingId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GrantWingAsync(identity.Account, character, wingId, ct);
    }

    public async Task<bool> GrantTitleAsync(Guid character, int titleId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GrantTitleAsync(identity.Account, character, titleId, ct);
    }

    public async Task<bool> SetTitleAsync(Guid character, int category, int titleId, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetTitleAsync(identity.Account, character, category, titleId, ct);
    }

    public async Task<bool> GrantSkinsAsync(Guid character, IReadOnlyList<int> skins, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.GrantSkinsAsync(identity.Account, character, skins, ct);
    }

    public async Task<bool> SkinExtractAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int skinId, int extractionUsed, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SkinExtractAsync(identity.Account, character, item, revision, costs, skinId, extractionUsed, ct);
    }

    public async Task<bool> SetSkinEquipsAsync(Guid character, IReadOnlyDictionary<int, int> equips, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetSkinEquipsAsync(identity.Account, character, equips, ct);
    }

    public async Task<bool> SaveMasteryAsync(Guid character, string kind, int category, int level, long exp, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SaveMasteryAsync(identity.Account, character, kind, category, level, exp, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (closed) return;
        try { await FlushAsync(); }
        catch (Exception error) when (error is InvalidOperationException or GameplayException) { }
        closed = true;
        try { await sessions.CloseAsync(token); }
        catch (Exception error) when (error is InvalidOperationException or Npgsql.NpgsqlException)
        {
            // The credential also expires with its parent browser session.
        }
    }
}
