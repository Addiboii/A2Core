using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Stored characters of one authenticated game connection (account + world fixed by the
/// world session; nothing here accepts an account from the client). Port of the Python
/// PersistentCharacters/WorldStore pair, without the HTTP bridge.
/// </summary>
public interface INativeCharacterStore : IAsyncDisposable
{
    Guid AccountId { get; }
    int WorldId { get; }
    Task<IReadOnlyList<CharacterState>> LobbyAsync(CancellationToken ct);
    Task<CharacterState?> LoadWireAsync(int wireId, CancellationToken ct);
    /// <summary>Creates and returns the stored state; throws on a rejected name or request.</summary>
    Task<CharacterState> CreateAsync(CreatePlayer input, CancellationToken ct);
    Task<bool> RequestCharacterDeleteAsync(Guid character, DateTimeOffset at, CancellationToken ct) => Task.FromResult(false);
    Task<bool> CancelCharacterDeleteAsync(Guid character, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Completes due deletions; the pending ones with their time.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CharacterDeletionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>());
    Task<string> RenameCharacterAsync(Guid character, string name, CancellationToken ct) => Task.FromResult("invalid");
    Task<GuildView?> GuildOfAsync(Guid character, CancellationToken ct) => Task.FromResult<GuildView?>(null);
    Task<IReadOnlyList<GuildView>> SearchGuildsAsync(string name, CancellationToken ct) => Task.FromResult<IReadOnlyList<GuildView>>([]);
    Task<string> CreateGuildAsync(Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct) => Task.FromResult("failed");
    Task<string> JoinGuildAsync(Guid character, long guild, string password, int maxMembers, CancellationToken ct) => Task.FromResult("none");
    Task<string> LeaveGuildAsync(Guid character, CancellationToken ct) => Task.FromResult("none");
    Task<string> KickGuildMemberAsync(Guid character, string nickname, CancellationToken ct) => Task.FromResult("none");
    Task<string> DisbandGuildAsync(Guid character, CancellationToken ct) => Task.FromResult("none");
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastRenamesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>());
    Task<IReadOnlyList<(int Target, int GoodsId, string Period, int Count)>> ShopPurchasesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, int, string, int)>>([]);
    Task<bool> ShopBuyAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SellToMerchantAsync(Guid character, Guid item, long revision, int count, long kina, CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<ItemState>> BuybackAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemState>>([]);
    Task<string> RestoreItemAsync(Guid character, Guid item, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct) => Task.FromResult("failed");
    Task<bool> MoveBagItemAsync(Guid character, Guid item, long revision, int slot, CancellationToken ct) => Task.FromResult(false);
    Task<bool> MoveItemToContainerAsync(Guid character, Guid item, long revision, string location, int slot, CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<PostRecord>> PostsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<PostRecord>>([]);
    Task<PostRecord?> SendPostAsync(Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct) => Task.FromResult<PostRecord?>(null);
    Task<bool> MarkPostsAsync(Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct) => Task.FromResult(false);
    Task<bool> TakePostAsync(Guid character, long id, CancellationToken ct) => Task.FromResult(false);
    Task<Guid?> CharacterByNameAsync(string name, CancellationToken ct) => Task.FromResult<Guid?>(null);
    Task<IReadOnlyList<CollectionRecord>> CollectionsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<CollectionRecord>>([]);
    Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<BossChallengeRecord>>([]);
    Task<bool> SaveBossChallengeAsync(Guid character, BossChallengeRecord record, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SaveCollectionAsync(Guid character, int collectionId, int listId, int registered, bool rewarded, CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<AchievementRecord>>([]);
    Task<IReadOnlyList<(int Group, int Step)>> GroupRewardsTakenAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<(int, int)>>([]);
    Task<bool> SaveAchievementsAsync(Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct) => Task.FromResult(false);
    Task<bool> TakeAchievementRewardAsync(Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct) => Task.FromResult(false);
    Task<bool> TakeGroupRewardAsync(Guid character, int groupId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SortBagAsync(Guid character, CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<QuickSlotRecord>> QuickSlotsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<QuickSlotRecord>>([]);
    Task<bool> SaveQuickSlotAsync(Guid character, int number, QuickSlotRecord? slot, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Per-character settings by name (emotes, seal stone, auto-use, presets), JSON values.</summary>
    Task<IReadOnlyDictionary<string, JsonElement>> CharacterSettingsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(new Dictionary<string, JsonElement>());
    Task<bool> SaveCharacterSettingAsync(Guid character, string name, JsonElement value, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Grants looted items.</summary>
    Task<bool> GrantLootAsync(Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Stores level and experience (per level).</summary>
    Task<bool> SaveProgressAsync(Guid character, int level, long experience, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Rides (pets/mounts) and the default ride; none by default.</summary>
    Task<RideCollection> RidesAsync(Guid character, CancellationToken ct) => Task.FromResult(new RideCollection(0, []));
    /// <summary>Uses up count of a bag stack and grants the rewards (reward box), atomically.</summary>
    Task<bool> ConsumeAndGrantAsync(Guid character, Guid item, long revision, int count, IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct) =>
        Task.FromResult(false);
    /// <summary>Uses up one pet item: its ride is acquired, or gains a soul point when already owned.</summary>
    Task<RideRecord?> UseRideItemAsync(Guid character, Guid item, long revision, int rideId, CancellationToken ct) => Task.FromResult<RideRecord?>(null);
    Task<bool> SetDefaultRideAsync(Guid character, int rideId, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Uses up one soul item; the ride gains soul points.</summary>
    Task<RideRecord?> AddRideSoulsAsync(Guid character, Guid item, long revision, int rideId, int souls, CancellationToken ct) => Task.FromResult<RideRecord?>(null);
    Task<RideRecord?> AcquireRideWithSoulsAsync(Guid character, int rideId, int cost, int crystalType, CancellationToken ct) => Task.FromResult<RideRecord?>(null);
    Task<RideRecord?> LevelUpRideAsync(Guid character, int rideId, int cost, int crystalType, int maxLevel, CancellationToken ct) => Task.FromResult<RideRecord?>(null);
    /// <summary>Uses up one crystal item; the new crystal amount.</summary>
    Task<int?> AddRideCrystalsAsync(Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct) => Task.FromResult<int?>(null);
    /// <summary>Stores a creature board, paying crystals and Kina atomically.</summary>
    Task<bool> SaveCreatureBoardAsync(Guid character, CreatureBoardRecord board, IReadOnlyDictionary<int, int> crystalCost,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Soul points of rides become crystals of each ride's type.</summary>
    Task<bool> CrystallizeAsync(Guid character, IReadOnlyList<(int RideId, int Points, int CrystalType)> targets, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Uses up count of a bag stack (an item used for its effect).</summary>
    Task<bool> ConsumeItemAsync(Guid character, Guid item, long revision, int count, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Whether a character already uses this name (the name check answers "taken").</summary>
    Task<bool> NameTakenAsync(string name, CancellationToken ct) => Task.FromResult(false);
    /// <summary>
    /// Replaces only the location (map and position, saved together so a teleport to another map
    /// does not store its coordinates under the old map), revision-checked; on a stale revision
    /// reloads once and keeps other edits.
    /// </summary>
    Task<CharacterState> SavePositionAsync(CharacterState state, int mapId, Vector3d position, CancellationToken ct);
    /// <summary>The character's stored items (bag, equipment, storage).</summary>
    Task<IReadOnlyList<ItemState>> InventoryAsync(Guid character, CancellationToken ct);
    /// <summary>Equips a stored item in an equipment slot (null: back to the bag); false when the rules refuse it.</summary>
    Task<bool> MoveItemAsync(Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct);
    Task<IReadOnlyList<SocialRelation>> SocialAsync(Guid character, CancellationToken ct);
    Task<(string Result, SocialRelation? Target)> FriendRequestAsync(Guid character, string targetName, CancellationToken ct);
    Task<bool> FriendRespondAsync(Guid character, Guid from, bool accept, CancellationToken ct);
    Task<bool> FriendCancelAsync(Guid character, Guid to, CancellationToken ct);
    Task<bool> FriendDeleteAsync(Guid character, Guid friend, CancellationToken ct);
    Task<bool> FriendMemoAsync(Guid character, Guid friend, string memo, CancellationToken ct);
    Task<SocialRelation?> BlockAsync(Guid character, string targetName, CancellationToken ct);
    Task<bool> UnblockAsync(Guid character, Guid blocked, CancellationToken ct);
    /// <summary>Player trade with another session's character (both stores validated); all or nothing.</summary>
    Task<bool> ExchangeAsync(Guid character, IReadOnlyList<(Guid Item, long Count)> gives, INativeCharacterStore otherStore, Guid other,
        IReadOnlyList<(Guid Item, long Count)> receives, CancellationToken ct);
    Task<IReadOnlyList<int>> MapFogAsync(Guid character, CancellationToken ct);
    /// <summary>Uncovers world-map fog groups; the ones that were new.</summary>
    Task<IReadOnlyList<int>> OpenMapFogAsync(Guid character, IReadOnlyList<int> groups, CancellationToken ct);
    /// <summary>Kibelisks (TeleportArtifact ids) the character has bound to.</summary>
    Task<IReadOnlyList<int>> TeleportArtifactsAsync(Guid character, CancellationToken ct);
    /// <summary>Records a bound Kibelisk; false when it was already bound.</summary>
    Task<bool> EngraveTeleportArtifactAsync(Guid character, int artifactId, CancellationToken ct);
    /// <summary>The stored skills with their levels (skill levels bought with skill points).</summary>
    Task<IReadOnlyList<SkillState>> SkillsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillState>>([]);
    Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Titles, skins and masteries (read at character selection).</summary>
    Task<CharacterExtras> ExtrasAsync(Guid character, CancellationToken ct) => Task.FromResult(CharacterExtras.None);
    Task<bool> GrantTitleAsync(Guid character, int titleId, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SetTitleAsync(Guid character, int category, int titleId, CancellationToken ct) => Task.FromResult(false);
    Task<bool> GrantSkinsAsync(Guid character, IReadOnlyList<int> skins, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SetSkinEquipsAsync(Guid character, IReadOnlyDictionary<int, int> equips, CancellationToken ct) => Task.FromResult(false);
    Task<bool> SaveMasteryAsync(Guid character, string kind, int category, int level, long exp, CancellationToken ct) => Task.FromResult(false);
    /// <summary>Stores a soul bind (bind count) on an owned item.</summary>
    Task<bool> SoulBindItemAsync(Guid character, Guid item, byte count, CancellationToken ct);
    Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus,
        CancellationToken ct) => Task.FromResult(false);
    Task<IReadOnlyList<QuestRecord>> QuestsAsync(Guid character, CancellationToken ct);
    Task<QuestRecord> SaveQuestAsync(Guid character, QuestRecord quest, CancellationToken ct);
    Task<bool> RemoveQuestAsync(Guid character, string code, CancellationToken ct);
    /// <summary>Pays a Complete quest once (experience and items); the new experience, or null when nothing was paid.</summary>
    Task<long?> RewardQuestAsync(Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct);
    /// <summary>The character's registered wings (Wing.dat ids).</summary>
    Task<IReadOnlyList<int>> WingsAsync(Guid character, CancellationToken ct);
    /// <summary>The character's ascension by play (the HQ1101020 turn-in): the grade only rises. Stores without a record say yes.</summary>
    Task<bool> AscendAsync(Guid character, int grade, CancellationToken ct) => Task.FromResult(true);
}

public static class StoredCharacters
{
    public static Vector3d Position(CharacterState state)
    {
        var p = state.Progress.Position;
        var value = new Vector3d(p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), p.GetProperty("z").GetDouble());
        return value.IsFinite ? value : throw new ArgumentException("Invalid saved position");
    }

    public static JsonElement PositionJson(Vector3d position) =>
        JsonSerializer.SerializeToElement(new { x = position.X, y = position.Y, z = position.Z });

    /// <summary>Name-based UUID (RFC 4122 version 5, SHA-1), as Python's uuid.uuid5.</summary>
    public static Guid Uuid5(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. nsBytes, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)(bytes[6] & 0x0F | 0x50);
        bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
