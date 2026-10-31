using System.Text.Json;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

public sealed record CreatePlayer(Guid RequestId, string ProfileId, string Name, byte[] Appearance,
    int? WorldId = null, int VoiceType = 0, int? LobbySlot = null);
public sealed record ItemState(Guid InstanceId, string BuildId, int TemplateId, long Quantity,
    string Location, int? Slot, long Revision, JsonElement RolledStats, JsonElement Upgrades, long WireId = 0);
/// <summary>A stored skill: <paramref name="Level"/> is the level the client shows; <paramref name="Purchased"/> is the level bought with
/// skill points through SkillLevelUp (0 = none). Only the purchase counts as spent points, never the kit or auto-granted level.</summary>
public sealed record SkillState(int SkillId, int Level, int? Slot, int Purchased = 0);
// Legions: a member (grade EGuildMemberGrade 0 Master, 1 Officer, 2 Member) and a legion with its members.
public sealed record GuildMember(Guid CharacterId, string Name, int Level, int Grade, string Comment, int CreationId, DateTimeOffset Joined,
    long Reputation = 0, long WeeklyReputation = 0, long DonationReputation = 0, long WeeklyDonationReputation = 0, int DonationsToday = 0);
public sealed record GuildAgitDeco(int DecoId, int SlotId);
public sealed record GuildJoinRequestSent(long GuildId, string GuildName, string Comment, DateTimeOffset RequestedAt);
public sealed record GuildJoinRequestRecv(Guid CharacterId, string Name, string Comment, DateTimeOffset RequestedAt, int Level, long Reputation);
public sealed record GuildStorageItem(long ItemKey, int TemplateId, long Quantity, int SlotPos);
public sealed record GuildStorageHistoryRow(string CharName, int RecordType, int TemplateId, long Quantity, long RecordedAtMs);
public sealed record GuildInfoSmall(long GuildId, string Name, int Level, int Emblem, string MasterName, int MemberCount, long UnionId, int UnionEmblem, string UnionName, long RequestedAtMs, int Race);
public sealed record GuildUnionView(long Id, string Name, int JoinRule, string Password, int Emblem, string Intro, string Notice, long OwnerGuildId,
    IReadOnlyList<GuildInfoSmall> MemberGuilds);
public sealed record GuildView(long Id, string Name, int Level, long Exp, int Emblem, int JoinRule, string Password, string Intro, string Notice,
    DateTimeOffset Created, IReadOnlyList<GuildMember> Members, int AgitId = 0, long AgitEndMs = 0, IReadOnlyList<GuildAgitDeco>? AgitDecos = null,
    int TodayAttendance = 0, int YesterdayAttendance = 0, IReadOnlyList<(byte Grade, int Bits)>? Authority = null,
    IReadOnlyList<(int Type, long Amount)>? Wallet = null, IReadOnlyList<int>? OwnedAgitDecos = null,
    long UnionId = 0, GuildUnionView? Union = null, IReadOnlyList<GuildInfoSmall>? AllyGuilds = null, IReadOnlyList<GuildInfoSmall>? HostileGuilds = null,
    IReadOnlyList<GuildInfoSmall>? SendAllyRequests = null, IReadOnlyList<GuildInfoSmall>? RecvAllyRequests = null,
    long StorageLastSortMs = 0, int StorageExtensionCount = 0, int StorageReservedSlots = 0);
// Wings: registered Wing.dat ids (flight needs one equipped).
/// <summary>A craft ('craft', ECraftCategory 1..5) or gather ('gather', category 0) mastery: level and exp towards the next.</summary>
public sealed record MasteryRecord(string Kind, int Category, int Level, long Exp);
/// <summary>
/// Titles, skins and masteries of a character (migration 029): owned Title.dat ids, the equipped title per
/// ETitleEquipCategory (1..3) and the shown one (0), the skin collection (Skin.dat id -> count), the equipped skin per
/// ESkinEquipSlot, and the masteries.
/// </summary>
public sealed record CharacterExtras(IReadOnlyList<int> Titles, IReadOnlyDictionary<int, int> TitleEquips, IReadOnlyDictionary<int, int> Skins,
    IReadOnlyDictionary<int, int> SkinEquips, IReadOnlyList<MasteryRecord> Masteries)
{
    public static readonly CharacterExtras None = new([], new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, int>(), []);
}
public sealed record PlayerInventory(IReadOnlyList<ItemState> Items, IReadOnlyList<SkillState> Skills, IReadOnlyList<int>? Wings = null, CharacterExtras? Extras = null);
// Trusted server calls only: progress changes are not accepted from a player API.
public sealed record AdminCommand(Guid RequestId, Guid CharacterId, string Command,
    string? BuildId = null, int TemplateId = 0, long Quantity = 1, int Level = 1,
    int MapId = 0, double X = 0, double Y = 0, double Z = 0, int CreationId = 0, int Ascension = 0, int[]? Wings = null, bool Staff = false);
// Quest progress (game.character_quests): EQuestStatus 2 Incomplete, 3 Complete, 4 Rewarded.
/// <summary>A ride (pet/mount, VehicleList id): level, soul points, and whether it is acquired (souls collect before).</summary>
public sealed record RideRecord(int RideId, int Level, int SoulPoints, bool Owned = true);
/// <summary>A hotbar slot: EQuickSlotType (0 skill, 1 item, ...), the item (uid, template) or skill.</summary>
public sealed record QuickSlotRecord(int Slot, int Type, long ItemUid, int ItemId, int SkillId, int[]? ContextSkills = null);

/// <summary>A creature board: ECreatureType, level, exp and its panels as JSON ({"1": [[slot, grade, stat, value, locked], ...]}).</summary>
public sealed record CreatureBoardRecord(int Type, int Level, long Exp, string Panels);

/// <summary>A character's rides, the default ride (0: none), soul crystals by ESoulCrystalType and creature boards.</summary>
public sealed record RideCollection(int DefaultRide, IReadOnlyList<RideRecord> Rides, IReadOnlyDictionary<int, int>? Crystals = null,
    IReadOnlyList<CreatureBoardRecord>? Boards = null);

public sealed record QuestRecord(string Code, int QuestId, long Uid, int Status, int Step, int[] Goals);
/// <summary>An achievement's state (migration 031): count, when its last step completed, the highest step rewarded.</summary>
public sealed record AchievementRecord(int Id, int Count, DateTimeOffset? CompletedAt, int RewardedStep, DateTimeOffset UpdatedAt);
/// <summary>A boss challenge group's progress (migration 033).</summary>
public sealed record BossChallengeRecord(int GroupId, int ClearLevel, long BestPoint, int BestLevel, int BestTimeMs, long CumulativePoint, DateTimeOffset UpdatedAt);
/// <summary>Abyss entry pool and ranking (migration 043; 050 pending siege AP and rift PvP flag).</summary>
public sealed record AbyssRecord(long EntryRemainingMs, long ApAcc, int RankingPoint, int RankingGrade, DateTimeOffset WeeklyResetAt, DateTimeOffset UpdatedAt,
    long PendingSiegeAp = 0, int RiftPvpOn = 0);
/// <summary>Fortress owner and siege applicants for a world host (migration 047).</summary>
public sealed record AirspaceOwnerRecord(int AirspaceId, long GuildId, string Name, int Emblem, string MasterName);
public sealed record AirspaceApplyRecord(int BattleId, int AirspaceId, long GuildId);
public sealed record AirspaceWorldRecord(IReadOnlyList<AirspaceOwnerRecord> Owners, IReadOnlyList<AirspaceApplyRecord> Applications);
/// <summary>Last applied daily/weekly boundaries for a world host (migration 080).</summary>
public sealed record WorldResetCursorRecord(long DailyBoundaryMs, long WeeklyBoundaryMs);
/// <summary>ContentsReset reward limit counter (migration 081).</summary>
public sealed record RewardLimitRecord(int LimitType, string Period, int Count);
/// <summary>Instance lockout until Unix ms (migration 082).</summary>
public sealed record DungeonCooltimeRecord(int DungeonId, long UntilMs);
/// <summary>Craft recipe periodic limit (migration 083): target 1 char, 2 account, 3 world.</summary>
public sealed record CraftLimitRecord(byte Target, int RecipeId, string Period, int Count);
/// <summary>A period collection list's progress (migration 032).</summary>
public sealed record CollectionRecord(int CollectionId, int ListId, int Registered, bool Rewarded);
/// <summary>Daevanion board progress (migration 041): activated node ids and point pools.</summary>
public sealed record DaevanionProgress(IReadOnlyList<int> NodeIds, IReadOnlyDictionary<int, int> UsedPoints, IReadOnlyDictionary<int, int> BonusPoints);
/// <summary>A mail (migration 030): Post.dat template, EPostType, EPostSaveTab, its message parameters and attachments.</summary>
public sealed record PostRecord(long Id, int Type, int Tab, int DataId, string Params, bool Read, bool Taken, DateTimeOffset Sent, DateTimeOffset Expire,
    IReadOnlyList<(int TemplateId, long Count)> Attachments, int Reason, Guid? SenderCharacterId = null, long CodKina = 0);
/// <summary>A push reward row (migration 088): one item gift every character can take once while it is active.</summary>
public sealed record PushRewardRecord(byte Type, int Id, string Sender, string Title, string Body, int ItemTemplateId, long ItemCount, long SentMs, long ExpireMs);
/// <summary>Broker listing (migration 051): item held in trade_escrow until sold, cancelled or expired.</summary>
public sealed record TradeListingRecord(long SaleId, Guid SellerCharacterId, Guid ItemInstanceId, long ItemWireKey, int TemplateId, long Count, long PriceKina,
    long ListedAtMs, long ExpiresAtMs, int TradeServerId);
/// <summary>Offline broker payout (migration 052): kina owed to seller after a sale while offline.</summary>
public sealed record TradeDeliveryRecord(long DeliveryId, Guid SellerCharacterId, long Kina, int ItemTemplateId, long ItemCount, bool Completed);
/// <summary>Broker dashboard counts for TradeGetSummary_RS.</summary>
public sealed record TradeSummaryRecord(int ActiveSales, int PendingDeliveries, int PendingEarningsKina);
// A friend, friend request or block of a character (Kind: friend, sent, received, blocked).
public sealed record SocialRelation(Guid CharacterId, string Name, int Level, int CreationId, int Race, int WireId, string Kind, string Memo = "");
// A class the character can change to: an enabled starter profile of its race and gender.
public sealed record AdminClass(int CreationId, string Name);
// Staff views (gamemaster/admin only). Account names come from the identity subject.
public sealed record AdminProfile(IReadOnlyList<string> Roles, string? BuildId);
/// <summary>An audit event for the staff activity log: who did what to which character, with the event's details (JSON).</summary>
public sealed record AdminAuditEntry(long Id, DateTimeOffset At, string Action, Guid? CharacterId, string? CharacterName, string? ActorName, string Details);
public sealed record AdminCharacter(Guid CharacterId, string Name, Guid AccountId, string AccountName,
    int WorldId, int Level, int Race, int Gender, int CreationId, bool AccountDisabled, IReadOnlyList<string>? AccountRoles = null, bool Staff = false);
/// <summary>A catalogue item for the staff dashboard (latest build): grade and level from the item facts.</summary>
public sealed record AdminCatalogItem(int TemplateId, string Name, string? Category, string? Grade, int? Level);
public sealed record AdminCatalogPage(IReadOnlyList<AdminCatalogItem> Items, int Total, IReadOnlyList<string> Categories);
public sealed record AdminCharacterDetail(AdminCharacter Character, PlayerInventory Inventory, AdminAppearance? Appearance = null,
    string? ClassName = null, IReadOnlyList<AdminClass>? Classes = null, int Ascension = 1);
// Decoded customization (Aion.Core CharacterAppearance): table indices and colours the
// staff preview maps to meshes, plus every field for inspection.
public sealed record AdminAppearance(int Version, int? HeadType, int? HairType, int? SkinColor, int? BeardType,
    string? HairColor, string? Hair2Color, string? BeardColor, string? PupilColorLeft, string? PupilColorRight,
    IReadOnlyDictionary<string, int> Fields)
{
    public static AdminAppearance From(Aion.Core.Characters.CharacterAppearance a) => new(a.Version,
        a.TypeIndex("HeadType"), a.TypeIndex("HairType"), a.TypeIndex("SkinColor"), a.TypeIndex("BeardType"),
        a.Color("HairColor"), a.Color("Hair2Color"), a.Color("BeardColor"), a.Color("PupilColor_L"), a.Color("PupilColor_R"),
        a.Fields);
}
public sealed class GameplayException(string code) : Exception(code) { public string Code => Message; }
public interface IGameplayStore
{
    Task<Guid> CreateAsync(VerifiedAccount account, CreatePlayer input, CancellationToken ct = default);
    Task<PlayerInventory?> InventoryAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<bool> MoveItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct = default);
    // World-map fog groups an owned character has uncovered; null when not owned.
    Task<IReadOnlyList<int>?> MapFogAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    // Uncovers fog groups; the ones that were new.
    Task<IReadOnlyList<int>> OpenMapFogAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> groups, CancellationToken ct = default);
    // Kibelisks (teleport artifacts) bound by an owned character; null when not owned.
    Task<IReadOnlyList<int>?> TeleportArtifactsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    // Records a bound Kibelisk; false when already bound or not owned.
    Task<bool> EngraveTeleportArtifactAsync(VerifiedAccount account, Guid character, int artifactId, CancellationToken ct = default);
    /// <summary>A title acquired (false when owned already or the character is not the account's).</summary>
    Task<bool> GrantWingAsync(VerifiedAccount account, Guid character, int wingId, CancellationToken ct = default) => Task.FromResult(false);
    Task<bool> GrantTitleAsync(VerifiedAccount account, Guid character, int titleId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The equipped title of a category (1..3) or the shown one (0); 0 clears. The title must be owned.</summary>
    Task<bool> SetTitleAsync(VerifiedAccount account, Guid character, int category, int titleId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Skins added to the collection (a count per skin).</summary>
    Task<bool> GrantSkinsAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> skins, CancellationToken ct = default) => Task.FromResult(false);
    Task<bool> SkinExtractAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int skinId, int extractionUsed, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Equipped skins by ESkinEquipSlot; 0 clears the slot. Each skin must be owned.</summary>
    Task<bool> SetSkinEquipsAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> equips, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>A learned skill's level (a skill level bought with skill points).</summary>
    Task<bool> SetSkillLevelAsync(VerifiedAccount account, Guid character, int skillId, int level, int purchased, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>A mastery's level and exp.</summary>
    Task<bool> SaveMasteryAsync(VerifiedAccount account, Guid character, string kind, int category, int level, long exp, CancellationToken ct = default) => Task.FromResult(false);
    // Friends, requests and blocks of an owned character; null when not owned.
    Task<IReadOnlyList<SocialRelation>?> SocialAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    // Asks a character of the same world (by name) to be friends. Result: requested, accepted (they had
    // asked first), not_found, self, already_friends, already_requested, blocked, full; with the target.
    Task<(string Result, SocialRelation? Target)> FriendRequestAsync(VerifiedAccount account, Guid character, string targetName, CancellationToken ct = default);
    // Accepts or refuses a received request; true when one was answered.
    Task<bool> FriendRespondAsync(VerifiedAccount account, Guid character, Guid from, bool accept, CancellationToken ct = default);
    Task<bool> FriendCancelAsync(VerifiedAccount account, Guid character, Guid to, CancellationToken ct = default);
    Task<bool> FriendDeleteAsync(VerifiedAccount account, Guid character, Guid friend, CancellationToken ct = default);
    Task<bool> FriendMemoAsync(VerifiedAccount account, Guid character, Guid friend, string memo, CancellationToken ct = default);
    // Blocks a character of the same world by name (ends friendship and requests both ways); the blocked one.
    Task<SocialRelation?> BlockAsync(VerifiedAccount account, Guid character, string targetName, CancellationToken ct = default);
    /// <summary>Requests a character's deletion at a time (now or later); a due deletion completes at once.</summary>
    Task<bool> RequestCharacterDeleteAsync(VerifiedAccount account, Guid character, DateTimeOffset at, CancellationToken ct = default);
    Task<bool> CancelCharacterDeleteAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Completes the account's due deletions; the pending ones by character with their time.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> CharacterDeletionsAsync(VerifiedAccount account, CancellationToken ct = default);
    /// <summary>Goods bought per limit owner: (target 1 character / 2 account, goods, period, count).</summary>
    Task<IReadOnlyList<(int Target, int GoodsId, string Period, int Count)>> ShopPurchasesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>
    /// An NPC shop purchase, all or nothing: the costs (item, revision, count) are consumed, the goods granted, and each
    /// limited goods' count raised within its limit (target 1 character, 2 account, 3 the character's world/server; period key).
    /// </summary>
    Task<bool> ShopBuyAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct = default);
    Task<IReadOnlyList<(int Target, int RecipeId, string Period, int Count)>> ConversionCountsAsync(VerifiedAccount account, Guid character, int worldId, CancellationToken ct = default);
    Task<bool> ConversionAsync(VerifiedAccount account, Guid character, int worldId, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, IReadOnlyList<(int Target, int RecipeId, string Period, int Count, int Limit)> limits, CancellationToken ct = default);
    /// <summary>Sells part or all of a bag stack: Kina granted, the sold units kept for buyback (72 hours, 50 items).</summary>
    Task<bool> SellToMerchantAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count, long kina, CancellationToken ct = default);
    /// <summary>The items waiting for buyback (expired ones are removed first).</summary>
    Task<IReadOnlyList<ItemState>> BuybackAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Buys a sold item back for what it sold for (30 per year): "ok", "limit" or "failed".</summary>
    Task<string> RestoreItemAsync(VerifiedAccount account, Guid character, Guid item, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    /// <summary>The character's legion, with its members.</summary>
    Task<GuildView?> GuildOfAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Legions by name (contains; all when empty), at most 20.</summary>
    Task<IReadOnlyList<GuildView>> SearchGuildsAsync(VerifiedAccount account, string name, CancellationToken ct = default);
    /// <summary>Creates a legion with the character as master, paying the costs: "ok", "taken", "member" or "failed".</summary>
    Task<string> CreateGuildAsync(VerifiedAccount account, Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    /// <summary>Joins a legion by its rule: "ok", "pending", "member", "full", "denied", "password" or "none".</summary>
    Task<string> JoinGuildAsync(VerifiedAccount account, Guid character, long guild, string password, string comment, int maxMembers, CancellationToken ct = default);
    Task<IReadOnlyList<GuildJoinRequestSent>> GuildJoinRequestsSentAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<IReadOnlyList<GuildJoinRequestRecv>> GuildJoinRequestsRecvAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<string> ConfirmGuildJoinAsync(VerifiedAccount account, Guid character, string nickname, bool accept, int maxMembers, CancellationToken ct = default);
    Task<string> CancelGuildJoinRequestAsync(VerifiedAccount account, Guid character, long guildId, CancellationToken ct = default);
    Task<string> SetGuildNoticeAsync(VerifiedAccount account, Guid character, string notice, CancellationToken ct = default);
    Task<string> SetGuildMemberGradeAsync(VerifiedAccount account, Guid character, string nickname, int grade, CancellationToken ct = default);
    Task<string> SetGuildAuthorityAsync(VerifiedAccount account, Guid character, IReadOnlyList<(byte Grade, int Bits)> authority, CancellationToken ct = default);
    Task<string> DelegateGuildMasterAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct = default);
    Task<(string Result, int Today, int Yesterday, bool CanToday, bool CanReward)> GuildAttendanceInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<string> MarkGuildAttendanceAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<(string Result, int YesterdayCount)> TakeGuildAttendanceRewardAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<(string Result, long Revision, int ExtensionCount, IReadOnlyList<GuildStorageItem> Items)> GuildStorageInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<string> StoreGuildStorageAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> items, CancellationToken ct = default);
    Task<string> TakeGuildStorageAsync(VerifiedAccount account, Guid character, IReadOnlyList<(long ItemKey, int Count)> items, CancellationToken ct = default);
    Task<(string Result, long LastHistoryMs, IReadOnlyList<GuildStorageHistoryRow> Rows)> GuildStorageHistoryAsync(VerifiedAccount account, Guid character, long afterMs, CancellationToken ct = default);
    Task<(string Result, long LastSortMs)> SortGuildStorageAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<(string Result, int ExtensionCount)> ExtendGuildStorageAsync(VerifiedAccount account, Guid character, int expectedExtension, CancellationToken ct = default);
    Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> DonateGuildWalletAsync(VerifiedAccount account, Guid character, long kina,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> WithdrawGuildWalletAsync(VerifiedAccount account, Guid character, long kina, CancellationToken ct = default);
    Task<(string Result, long UnionId, string Name)> CreateGuildUnionAsync(VerifiedAccount account, Guid character, string name, int joinRule, string password, int emblem, string intro, CancellationToken ct = default);
    Task<(string Result, GuildUnionView? Union)> GuildUnionInfoAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<string> DisbandGuildUnionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<(string Result, int JoinResult)> JoinGuildUnionAsync(VerifiedAccount account, Guid character, long unionId, string password, CancellationToken ct = default);
    Task<string> LeaveGuildUnionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<string> RequestGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, int relation, CancellationToken ct = default);
    Task<string> ConfirmGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, bool accept, CancellationToken ct = default);
    Task<string> CancelGuildRelationAsync(VerifiedAccount account, Guid character, int targetType, string targetName, int serverId, CancellationToken ct = default);
    Task<string> GrantGuildAgitDecoAsync(VerifiedAccount account, Guid character, Guid item, long revision, int templateId, CancellationToken ct = default);
    /// <summary>Leaves the legion: "ok", "master" (a master disbands instead) or "none".</summary>
    Task<string> LeaveGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>A master or officer removes a lower-grade member by name: "ok", "authority" or "none".</summary>
    Task<string> KickGuildMemberAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct = default);
    /// <summary>The master disbands the legion: "ok", "authority" or "none".</summary>
    Task<string> DisbandGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Legion donation (contribution): "ok", "none", "limit", "kina", "level"; remain count after success.</summary>
    Task<(string Result, int Remain, int? NewGuildLevel)> DonateGuildAsync(VerifiedAccount account, Guid character, int donationLevel,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct = default);
    /// <summary>Remaining donation attempts today for the member.</summary>
    Task<int> GuildDonationRemainAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Rent or extend the legion hall: "ok", "none", "authority", "kina", "agit"; end time ms.</summary>
    Task<(string Result, int AgitId, long EndMs)> ContractGuildAgitAsync(VerifiedAccount account, Guid character, int agitId,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long nowMs, CancellationToken ct = default);
    /// <summary>Equip owned agit decoration: "ok", "none", "slot", "missing".</summary>
    Task<string> EquipGuildAgitDecoAsync(VerifiedAccount account, Guid character, int decoId, int slotId, CancellationToken ct = default);
    /// <summary>Unequip agit decoration: "ok", "none", "missing".</summary>
    Task<string> UnequipGuildAgitDecoAsync(VerifiedAccount account, Guid character, int decoId, CancellationToken ct = default);
    /// <summary>The account's characters' last renames.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastRenamesAsync(VerifiedAccount account, CancellationToken ct = default);
    /// <summary>Renames a character for a Name Change Voucher: "ok", "taken", "invalid", "voucher" (none) or "locked" (168 hours).</summary>
    Task<string> RenameCharacterAsync(VerifiedAccount account, Guid character, string name, CancellationToken ct = default);
    /// <summary>Reassigns lobby slots on a world (wire id, slot); false when not owned or layout invalid.</summary>
    Task<bool> SwapLobbySlotsAsync(VerifiedAccount account, int worldId, IReadOnlyList<(long WireId, int Slot)> assignments, int slotCapacity, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Purchased lobby character slot extensions (auth.accounts.extended_character_slots).</summary>
    Task<int> ExtendedCharacterSlotsAsync(VerifiedAccount account, CancellationToken ct = default) => Task.FromResult(0);
    /// <summary>Moves a bag item to a bag slot; an item already there takes the moved item's old slot.</summary>
    Task<bool> MoveBagItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int slot, CancellationToken ct = default);
    /// <summary>Moves a bag or warehouse item to a slot of 'bag' or 'storage' (the item on that slot takes the old one).</summary>
    Task<bool> MoveItemToContainerAsync(VerifiedAccount account, Guid character, Guid item, long revision, string location, int slot, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The character's mail, newest first (deleted ones left out).</summary>
    Task<IReadOnlyList<PostRecord>> PostsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PostRecord>>([]);
    /// <summary>Trusted server call (staff): a mail to any character; returns the stored post.</summary>
    Task<PostRecord?> SendPostAsync(VerifiedAccount actor, Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct = default) => Task.FromResult<PostRecord?>(null);
    /// <summary>Marks the character's posts read, or deletes them (a post with attachments not yet taken stays).</summary>
    Task<bool> MarkPostsAsync(VerifiedAccount account, Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Grants a post's attachments once and marks it taken (COD kina deducted when set on the post).</summary>
    Task<bool> TakePostAsync(VerifiedAccount account, Guid character, long id, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Player mail: fee in kina from the sender's bag, optional COD for the recipient on take.</summary>
    Task<PostRecord?> SendPlayerPostAsync(VerifiedAccount sender, Guid fromCharacter, Guid toCharacter, int dataId, string parameters,
        IReadOnlyList<(int TemplateId, long Count)> attachments, long codKina, long mailFeeKina, CancellationToken ct = default) => Task.FromResult<PostRecord?>(null);
    /// <summary>Expired posts with untaken attachments return to the sender as new mail.</summary>
    Task<int> ReturnExpiredPostsAsync(CancellationToken ct = default) => Task.FromResult(0);
    /// <summary>Active push rewards this character has not taken yet (server ms for sent/expire).</summary>
    Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(VerifiedAccount account, Guid character, long nowMs, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PushRewardRecord>>([]);
    /// <summary>Trusted staff call: a new global push reward; returns the stored row.</summary>
    Task<PushRewardRecord?> AddPushRewardAsync(VerifiedAccount actor, byte type, string sender, string title, string body, int itemTemplateId, long itemCount,
        long expireMs, CancellationToken ct = default) => Task.FromResult<PushRewardRecord?>(null);
    /// <summary>Grants a push reward once; false when unknown, expired, already taken, or type/id mismatch.</summary>
    Task<PushRewardRecord?> TakePushRewardAsync(VerifiedAccount account, Guid character, byte type, int id, byte targetSequence, CancellationToken ct = default) =>
        Task.FromResult<PushRewardRecord?>(null);
    Task<TradeSummaryRecord> TradeSummaryAsync(VerifiedAccount account, Guid character, int tradeServerId, CancellationToken ct = default) =>
        Task.FromResult(new TradeSummaryRecord(0, 0, 0));
    Task<(string Result, TradeListingRecord? Listing)> TradeListItemAsync(VerifiedAccount account, Guid character, long itemWireKey, long count, long priceKina,
        int tradeServerId, long nowMs, CancellationToken ct = default) => Task.FromResult<(string, TradeListingRecord?)>(("unsupported", null));
    Task<(string Result, long DeliveryId)> TradePurchaseAsync(VerifiedAccount buyer, Guid buyerCharacter, long saleId, int templateId, long count, int tradeServerId,
        CancellationToken ct = default) => Task.FromResult<(string, long)>(("unsupported", 0L));
    Task<(string Result, TradeListingRecord? Listing)> TradeCancelListingAsync(VerifiedAccount account, Guid character, long saleId, int templateId, long count,
        int tradeServerId, CancellationToken ct = default) => Task.FromResult<(string, TradeListingRecord?)>(("unsupported", null));
    Task<IReadOnlyList<TradeDeliveryRecord>> TradeDeliveriesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TradeDeliveryRecord>>([]);
    Task<bool> TradeCompleteDeliveryAsync(VerifiedAccount account, Guid character, long deliveryId, int tradeServerId, CancellationToken ct = default) =>
        Task.FromResult(false);
    Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AchievementRecord>>([]);
    Task<IReadOnlyList<(int Group, int Step)>> GroupRewardsTakenAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(int, int)>>([]);
    /// <summary>Upserts the given states.</summary>
    Task<bool> SaveAchievementsAsync(VerifiedAccount account, Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Grants a step's reward once (rewarded_step rises to the step); false when already rewarded.</summary>
    Task<bool> TakeAchievementRewardAsync(VerifiedAccount account, Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Grants a group reward step once.</summary>
    Task<bool> TakeGroupRewardAsync(VerifiedAccount account, Guid character, int groupId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<CollectionRecord>> CollectionsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionRecord>>([]);
    Task<bool> SaveCollectionAsync(VerifiedAccount account, Guid character, int collectionId, int listId, int registered, bool rewarded, CancellationToken ct = default) => Task.FromResult(false);
    Task<DaevanionProgress?> DaevanionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<DaevanionProgress?>(null);
    Task<bool> SaveDaevanionAsync(VerifiedAccount account, Guid character, DaevanionProgress progress, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BossChallengeRecord>>([]);
    Task<bool> SaveBossChallengeAsync(VerifiedAccount account, Guid character, BossChallengeRecord record, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Expedition contents tickets consumed this period (ticket id, period key, count used).</summary>
    Task<IReadOnlyList<(int TicketId, string Period, int Count)>> ExpeditionTicketsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(int, string, int)>>([]);
    /// <summary>Replaces the character's stored expedition ticket rows (in-memory flush from the world session).</summary>
    Task<bool> SaveExpeditionTicketsAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int TicketId, string Period, int Count)> rows, CancellationToken ct = default) =>
        Task.FromResult(false);
    Task<AbyssRecord?> AbyssAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<AbyssRecord?>(null);
    Task<bool> SaveAbyssAsync(VerifiedAccount account, Guid character, AbyssRecord record, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Credit offline legion members after fortress siege (migration 050 pending_siege_ap).</summary>
    Task AddPendingSiegeApForGuildAsync(long guildId, int ap, IReadOnlyList<Guid> onlineCharacterIds, CancellationToken ct = default) => Task.CompletedTask;
    Task<AirspaceWorldRecord> AirspaceWorldAsync(int worldId, CancellationToken ct = default) =>
        Task.FromResult(new AirspaceWorldRecord([], []));
    Task SaveAirspaceWorldAsync(int worldId, AirspaceWorldRecord record, CancellationToken ct = default) => Task.CompletedTask;
    Task<WorldResetCursorRecord> ResetCursorsAsync(int worldId, CancellationToken ct = default) =>
        Task.FromResult(new WorldResetCursorRecord(0, 0));
    Task SaveResetCursorsAsync(int worldId, WorldResetCursorRecord record, CancellationToken ct = default) => Task.CompletedTask;
    Task ApplyDailyLegionResetsAsync(DateTime gameDayUtc, CancellationToken ct = default) => Task.CompletedTask;
    Task ApplyWeeklyLegionResetsAsync(long weeklyBoundaryMs, CancellationToken ct = default) => Task.CompletedTask;
    Task<IReadOnlyList<RewardLimitRecord>> RewardLimitsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RewardLimitRecord>>([]);
    Task<bool> SaveRewardLimitsAsync(VerifiedAccount account, Guid character, IReadOnlyList<RewardLimitRecord> rows, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<DungeonCooltimeRecord>> DungeonCooltimesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DungeonCooltimeRecord>>([]);
    Task<bool> SaveDungeonCooltimesAsync(VerifiedAccount account, Guid character, IReadOnlyList<DungeonCooltimeRecord> rows, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<CraftLimitRecord>> CraftLimitsAsync(VerifiedAccount account, Guid character, int worldId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CraftLimitRecord>>([]);
    Task<bool> SaveCraftLimitsAsync(VerifiedAccount account, Guid character, int worldId, IReadOnlyList<CraftLimitRecord> rows, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>A character id by name (any account), for mail to offline players.</summary>
    Task<Guid?> CharacterByNameAsync(VerifiedAccount account, string name, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
    /// <summary>Sorts the bag: items ordered by template, then age, on slots 0, 1, 2, ...</summary>
    Task<bool> SortBagAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Sorts a container (bag or storage depot).</summary>
    Task<bool> SortContainerAsync(VerifiedAccount account, Guid character, string location, CancellationToken ct = default);
    Task<IReadOnlyList<QuickSlotRecord>?> QuickSlotsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>Per-character settings by name (game.character_settings): emotes, seal stone, auto-use, presets.</summary>
    Task<IReadOnlyDictionary<string, JsonElement>?> CharacterSettingsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement>?>(new Dictionary<string, JsonElement>());
    Task<bool> SaveCharacterSettingAsync(VerifiedAccount account, Guid character, string name, JsonElement value, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Stores a hotbar slot (replacing it), or clears it when <paramref name="slot"/> is null for that number.</summary>
    Task<bool> SaveQuickSlotAsync(VerifiedAccount account, Guid character, int number, QuickSlotRecord? slot, CancellationToken ct = default);
    /// <summary>Grants looted items (the latest catalogue build; unknown templates are skipped). False when the character is not the account's.</summary>
    Task<bool> GrantLootAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct = default);
    /// <summary>Stores the character's level and experience (held per level).</summary>
    Task<bool> SaveProgressAsync(VerifiedAccount account, Guid character, int level, long experience, CancellationToken ct = default);
    /// <summary>Rides (pets/mounts) the character has acquired, and its default ride.</summary>
    Task<RideCollection?> RidesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>
    /// Uses up <paramref name="count"/> of a bag stack (revision-checked) and grants the rewards in the
    /// same transaction (reward box). False when the item, revision or quantity does not match.
    /// </summary>
    Task<bool> ConsumeAndGrantAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count,
        IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct = default);
    /// <summary>
    /// Uses up one pet item and acquires its ride; a ride already owned gains a soul point instead.
    /// Null when the item does not match.
    /// </summary>
    Task<RideRecord?> UseRideItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int rideId, CancellationToken ct = default);
    /// <summary>Uses up one soul item: the ride gains soul points (owned or not). Null when the item does not match.</summary>
    Task<RideRecord?> AddRideSoulsAsync(VerifiedAccount account, Guid character, Guid item, long revision, int rideId, int souls, CancellationToken ct = default);
    /// <summary>
    /// Acquires a ride for <paramref name="cost"/> soul points (VehicleList NeedSoul); missing points are
    /// paid with crystals of <paramref name="crystalType"/>, then All crystals. Null when it cannot.
    /// </summary>
    Task<RideRecord?> AcquireRideWithSoulsAsync(VerifiedAccount account, Guid character, int rideId, int cost, int crystalType, CancellationToken ct = default);
    /// <summary>Raises an owned ride's level for <paramref name="cost"/> soul points (VehicleLevel NeedSoulCount), crystals as above.</summary>
    Task<RideRecord?> LevelUpRideAsync(VerifiedAccount account, Guid character, int rideId, int cost, int crystalType, int maxLevel, CancellationToken ct = default);
    /// <summary>Uses up one crystal item: crystals of its type. The new amount, or null when the item does not match.</summary>
    Task<int?> AddRideCrystalsAsync(VerifiedAccount account, Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct = default);
    /// <summary>
    /// Stores a creature board, paying crystals (by ESoulCrystalType) and Kina (a bag stack, revision-checked)
    /// in the same transaction; false when something is short or changed.
    /// </summary>
    Task<bool> SaveCreatureBoardAsync(VerifiedAccount account, Guid character, CreatureBoardRecord board, IReadOnlyDictionary<int, int> crystalCost,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct = default);
    /// <summary>Turns soul points of rides into crystals of each ride's type, all or nothing.</summary>
    Task<bool> CrystallizeAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int RideId, int Points, int CrystalType)> targets, CancellationToken ct = default);
    /// <summary>Uses up count of a bag stack (an item used for its effect), revision-checked.</summary>
    Task<bool> ConsumeItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count, CancellationToken ct = default);
    /// <summary>Sets the default ride (an owned one, or 0 to clear).</summary>
    Task<bool> SetDefaultRideAsync(VerifiedAccount account, Guid character, int rideId, CancellationToken ct = default);
    /// <summary>Whether a character already uses this name (names are unique regardless of case).</summary>
    Task<bool> NameTakenAsync(VerifiedAccount account, string name, CancellationToken ct = default);
    Task<bool> UnblockAsync(VerifiedAccount account, Guid character, Guid blocked, CancellationToken ct = default);
    Task<bool> SocialBlockedAsync(VerifiedAccount account, Guid character, Guid other, CancellationToken ct = default);
    /// <summary>Whether either character has blocked the other (chat whispers; no account check).</summary>
    Task<bool> AnyBlockBetweenAsync(Guid character, Guid other, CancellationToken ct = default);
    // Player trade: both sides' offered items (instance, count) change hands in one transaction, or nothing
    // does (an item gone, changed, soul-bound, equipped or short). Whole stacks move with their row; part
    // of a stack is split off and granted to the receiver.
    Task<bool> ExchangeAsync(VerifiedAccount first, Guid firstCharacter, IReadOnlyList<(Guid Item, long Count)> firstGives,
        VerifiedAccount second, Guid secondCharacter, IReadOnlyList<(Guid Item, long Count)> secondGives, long firstGivesKina = 0, long secondGivesKina = 0,
        CancellationToken ct = default);
    // Soul bind of an owned item: upgrades.soul_bind = count, revision bumped.
    Task<bool> SoulBindItemAsync(VerifiedAccount account, Guid character, Guid item, byte count, CancellationToken ct = default);
    /// <summary>
    /// An enchant attempt, all or nothing: the costs (item, revision, count) are consumed and the item (at its revision)
    /// gets upgrades.enchant = level and upgrades.enchant_bonus = bonus (the failure correction built up so far).
    /// </summary>
    Task<bool> EnchantItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus, CancellationToken ct = default);
    /// <summary>Amplify (◆): upgrades.surpass_enchant and upgrades.surpass_enchant_bonus after costs are consumed.</summary>
    Task<bool> ExceedEnchantItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int surpassLevel, int surpassBonus, CancellationToken ct = default);
    Task<bool> SetRolledStatsAsync(VerifiedAccount account, Guid character, Guid item, long revision, JsonElement rolled, CancellationToken ct = default);
    /// <summary>Socket a godstone item into equipment: consumes material, sets upgrades.godstones.</summary>
    Task<bool> SocketGodstoneAsync(VerifiedAccount account, Guid character, Guid equip, long equipRevision, Guid material, long materialRevision,
        IReadOnlyList<int> godstones, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, int>> GodstoneCollectionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    Task<bool> UpsertGodstoneCollectionAsync(VerifiedAccount account, Guid character, IReadOnlyList<(int GodstoneId, int Delta)> updates,
        CancellationToken ct = default);
    Task<bool> CombineGodstonesAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> materials, int rewardId,
        CancellationToken ct = default);
    Task<JsonElement> AdminAsync(VerifiedAccount actor, AdminCommand command, CancellationToken ct = default);
    /// <summary>The account's own character ascends by play (the HQ1101020 turn-in): the grade rises to <paramref name="grade"/>, never falls.</summary>
    Task<bool> AscendAsync(VerifiedAccount account, Guid character, int grade, CancellationToken ct = default) => Task.FromResult(false);
    // Throw GameplayException("forbidden") unless the actor holds gamemaster or admin.
    Task<AdminProfile> AdminProfileAsync(VerifiedAccount actor, CancellationToken ct = default);
    Task<IReadOnlyList<AdminCharacter>> AdminFindCharactersAsync(VerifiedAccount actor, string query, CancellationToken ct = default);
    Task<AdminCharacterDetail?> AdminCharacterAsync(VerifiedAccount actor, Guid character, CancellationToken ct = default);
    // Latest catalogue build: exact template ID or name substring, at most five rows (GM chat command).
    Task<IReadOnlyList<(int TemplateId, string Name)>> AdminSearchItemsAsync(VerifiedAccount actor, string query, CancellationToken ct = default);
    // Latest catalogue build for the dashboard: ID or name substring (empty: all), optional category, paged (limit 1..200).
    /// <summary>Recent audit events, newest first (staff): optionally one action prefix (e.g. "staff_", "gm_") or one character; before: paging by id.</summary>
    Task<IReadOnlyList<AdminAuditEntry>> AdminAuditAsync(VerifiedAccount actor, string? action, Guid? character, long? before, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AdminAuditEntry>>([]);
    /// <summary>Records a staff action taken outside the store (dashboard announcement, kick, teleport); checks the staff role.</summary>
    Task AdminRecordAsync(VerifiedAccount actor, string action, Guid? character, object details, CancellationToken ct = default) => Task.CompletedTask;
    Task<AdminCatalogPage> AdminCatalogAsync(VerifiedAccount actor, string query, string? category, int offset, int limit, CancellationToken ct = default);
    // Quests of an owned character; null when not owned.
    Task<IReadOnlyList<QuestRecord>?> QuestsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    // Insert or update one quest by code (uid assigned on insert); the stored record.
    Task<QuestRecord> SaveQuestAsync(VerifiedAccount account, Guid character, QuestRecord quest, CancellationToken ct = default);
    /// <summary>One transaction for several quest upserts (same semantics as repeated SaveQuestAsync).</summary>
    Task<IReadOnlyList<QuestRecord>> SaveQuestsAsync(VerifiedAccount account, Guid character, IReadOnlyList<QuestRecord> quests, CancellationToken ct = default) =>
        SaveQuestsFallbackAsync(account, character, quests, ct);
    async Task<IReadOnlyList<QuestRecord>> SaveQuestsFallbackAsync(VerifiedAccount account, Guid character, IReadOnlyList<QuestRecord> quests, CancellationToken ct)
    {
        var saved = new List<QuestRecord>(quests.Count);
        foreach (var quest in quests) saved.Add(await SaveQuestAsync(account, character, quest, ct));
        return saved;
    }
    Task<bool> RemoveQuestAsync(VerifiedAccount account, Guid character, string code, CancellationToken ct = default);
    // Server-decided reward of a completed quest: marks it rewarded, adds experience and grants the
    // items once (repeating the call for a rewarded quest grants nothing). Returns the new experience.
    Task<long?> RewardQuestAsync(VerifiedAccount account, Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct = default);
}
