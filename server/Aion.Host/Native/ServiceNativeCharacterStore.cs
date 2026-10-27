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
    IWireCharacterRepository wire, IGameplayStore game, CharacterPersistenceService persistence) : INativeCharacterStore
{
    bool closed;

    public Guid AccountId => identity.Account.AccountId;
    public int WorldId => identity.WorldId;

    async Task ValidateAsync(CancellationToken ct)
    {
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

    public async Task<CharacterState> SavePositionAsync(CharacterState state, int mapId, Vector3d position, CancellationToken ct)
    {
        if (!position.IsFinite || mapId <= 0) throw new ArgumentException("Invalid location");
        for (var attempt = 0; ; attempt++)
        {
            await ValidateAsync(ct);
            var progress = state.Progress with { MapId = mapId, Position = StoredCharacters.PositionJson(position) };
            var revision = await persistence.SaveAsync(identity.Account, state.Summary.CharacterId, state.Revision, progress, ct);
            if (revision is { } saved) return state with { Progress = progress, Revision = saved };
            // Stale revision (another writer, e.g. the staff dashboard): reload and replace only the location.
            if (attempt > 0) throw new InvalidOperationException("Character changed concurrently.");
            state = await persistence.LoadAsync(identity.Account, state.Summary.CharacterId, ct)
                ?? throw new InvalidOperationException("Character is no longer available.");
        }
    }

    public async Task<IReadOnlyList<ItemState>> InventoryAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return (await game.InventoryAsync(identity.Account, character, ct))?.Items ?? [];
    }

    public async Task<bool> MoveItemAsync(Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.MoveItemAsync(identity.Account, character, item, revision, equipmentSlot, ct);
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
        return await game.RewardQuestAsync(identity.Account, character, code, experience, items, ct);
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

    public async Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int level, int bonus, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.EnchantItemAsync(identity.Account, character, item, revision, costs, level, bonus, ct);
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

    public async Task<string> JoinGuildAsync(Guid character, long guild, string password, int maxMembers, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.JoinGuildAsync(identity.Account, character, guild, password, maxMembers, ct);
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
        return await game.GrantLootAsync(identity.Account, character, items, ct);
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

    public async Task<bool> ExchangeAsync(Guid character, IReadOnlyList<(Guid Item, long Count)> gives, INativeCharacterStore otherStore, Guid other,
        IReadOnlyList<(Guid Item, long Count)> receives, CancellationToken ct)
    {
        if (otherStore is not ServiceNativeCharacterStore partner) return false;
        await ValidateAsync(ct);
        await partner.ValidateAsync(ct);
        return await game.ExchangeAsync(identity.Account, character, gives, partner.Account, other, receives, ct);
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
        return await game.SoulBindItemAsync(identity.Account, character, item, count, ct);
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

    public async Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await game.SetSkillLevelAsync(identity.Account, character, skillId, level, ct);
    }

    public async Task<CharacterExtras> ExtrasAsync(Guid character, CancellationToken ct)
    {
        await ValidateAsync(ct);
        return (await game.InventoryAsync(identity.Account, character, ct))?.Extras ?? CharacterExtras.None;
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
        closed = true;
        try { await sessions.CloseAsync(token); }
        catch (Exception error) when (error is InvalidOperationException or Npgsql.NpgsqlException)
        {
            // The credential also expires with its parent browser session.
        }
    }
}
