using System.Text.Json;
using Aion.Core.Accounts;

namespace Aion.Application.Gameplay;

public sealed record CreatePlayer(Guid RequestId, string ProfileId, string Name, byte[] Appearance,
    int? WorldId = null, int VoiceType = 0, int? LobbySlot = null, int? HomeServerId = null);
public sealed record ItemState(Guid InstanceId, string BuildId, int TemplateId, long Quantity,
    string Location, int? Slot, long Revision, JsonElement RolledStats, JsonElement Upgrades, long WireId = 0)
{
    /// <summary>When the row was created (game.item_instances.created_at): the start of an Item.dat MaintainTime period.</summary>
    public DateTimeOffset? Acquired { get; init; }
}
/// <summary>
/// One row of an item arrangement (sort, store, take out, stack merge): the row ends in Location on Slot (null: no slot) holding
/// Quantity (0: the row goes, merged into another stack). Split: a new row cloned from Item (same build, stats and upgrades) with
/// Quantity on Location/Slot; Item is then also listed as a plain row. Per template the quantities add up to what the rows held.
/// </summary>
public sealed record ItemArrangement(Guid Item, long Revision, string Location, int? Slot, long Quantity, bool Split = false);
/// <summary>A learned skill: the level the client sees, its quick slot, and the level bought with skill points (0: none).</summary>
public sealed record SkillState(int SkillId, int Level, int? Slot, int Purchased = 0);
// Legions: a member (grade EGuildMemberGrade 0 Master, 1 Officer, 2 Member) and a legion with its members.
public sealed record GuildMember(Guid CharacterId, string Name, int Level, int Grade, string Comment, int CreationId, DateTimeOffset Joined);
public sealed record GuildView(long Id, string Name, int Level, long Exp, int Emblem, int JoinRule, string Password, string Intro, string Notice,
    DateTimeOffset Created, IReadOnlyList<GuildMember> Members);
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
/// <summary>
/// A character's Daevanion (migration 083): learned nodes (DaevanionNode ids, the auto-learned Start nodes not stored) and the
/// points items added per EDaevanionPointType (1 DaevanionCrystal .. 5 Season03Crystal01).
/// </summary>
public sealed record DaevanionRecord(IReadOnlyList<int> Nodes, IReadOnlyDictionary<int, int> Bonus)
{
    public static readonly DaevanionRecord None = new([], new Dictionary<int, int>());
}
/// <summary>EquippedWing: the stored wing choice (migration 073; 0 none stored: the default rule applies).</summary>
public sealed record PlayerInventory(IReadOnlyList<ItemState> Items, IReadOnlyList<SkillState> Skills, IReadOnlyList<int>? Wings = null, CharacterExtras? Extras = null,
    int EquippedWing = 0);
// Trusted server calls only: progress changes are not accepted from a player API.
public sealed record AdminCommand(Guid RequestId, Guid CharacterId, string Command,
    string? BuildId = null, int TemplateId = 0, long Quantity = 1, int Level = 1,
    int MapId = 0, double X = 0, double Y = 0, double Z = 0, int CreationId = 0, int Ascension = 0, int[]? Wings = null, bool Staff = false,
    int Category = 0);
// Quest progress (game.character_quests): EQuestStatus 2 Incomplete, 3 Complete, 4 Rewarded.
/// <summary>A ride (pet/mount, VehicleList id): level, soul points, and whether it is acquired (souls collect before).</summary>
public sealed record RideRecord(int RideId, int Level, int SoulPoints, bool Owned = true);
/// <summary>
/// A hotbar slot (QuickSlotInfo): EQuickSlotType (0 skill, 1 item, 2 macro, 3 emote, 4 stigma), the item (uid, template) or skill,
/// the context skills, the macro number, the emote and the toggle flag. A row with no skill and no item is an emptied slot (it keeps
/// a class default off the bar).
/// </summary>
public sealed record QuickSlotRecord(int Slot, int Type, long ItemUid, int ItemId, int SkillId, IReadOnlyList<int>? ContextSkills = null,
    int Macro = 0, int SocialAction = 0, bool Toggle = false)
{
    public bool IsEmpty => ItemId == 0 && SkillId == 0 && SocialAction == 0 && Macro == 0;
}

/// <summary>A creature board: ECreatureType, level, exp and its panels as JSON ({"1": [[slot, grade, stat, value, locked], ...]}).</summary>
public sealed record CreatureBoardRecord(int Type, int Level, long Exp, string Panels);

/// <summary>A character's rides, the default ride (0: none), soul crystals by ESoulCrystalType and creature boards.</summary>
public sealed record RideCollection(int DefaultRide, IReadOnlyList<RideRecord> Rides, IReadOnlyDictionary<int, int>? Crystals = null,
    IReadOnlyList<CreatureBoardRecord>? Boards = null);

/// <summary>
/// A quest row. UpdatedAt: the row's updated_at (null for rows not read from the database); a Rewarded row is not written again,
/// so for it this is when it was rewarded (the Ascension Gauge counts the quests rewarded after its own start, QuestLog.GaugeRecount).
/// </summary>
public sealed record QuestRecord(string Code, int QuestId, long Uid, int Status, int Step, int[] Goals, DateTimeOffset? UpdatedAt = null);
/// <summary>An achievement's state (migration 031): count, when its last step completed, the highest step rewarded.</summary>
public sealed record AchievementRecord(int Id, int Count, DateTimeOffset? CompletedAt, int RewardedStep, DateTimeOffset UpdatedAt);
/// <summary>A boss challenge group's progress (migration 033).</summary>
public sealed record BossChallengeRecord(int GroupId, int ClearLevel, long BestPoint, int BestLevel, int BestTimeMs, long CumulativePoint, DateTimeOffset UpdatedAt);
/// <summary>An entry ticket's count at a time (migration 065); the recharges since then are added on load.</summary>
public sealed record ContentTicketRecord(int TicketId, int Remaining, DateTimeOffset UpdatedAt);
/// <summary>A stigma skill learned with shards and the level it reached (migration 072).</summary>
public sealed record StigmaRecord(int SkillId, int Level);
/// <summary>A part equipped on a mastery skill's specialisation slot (migration 078).</summary>
public sealed record SkillPartRecord(int SkillId, int Slot, int PartId);
/// <summary>A character's stigmas and the shards its GetStigmaShard items gave (on top of Exp.dat's total for its level).</summary>
public sealed record StigmaState(IReadOnlyList<StigmaRecord> Stigmas, int ItemShards)
{
    public static readonly StigmaState Empty = new([], 0);
}
/// <summary>A period collection list's progress (migration 032).</summary>
public sealed record CollectionRecord(int CollectionId, int ListId, int Registered, bool Rewarded);
/// <summary>A mail (migration 030): Post.dat template, EPostType, EPostSaveTab, its message parameters and attachments.</summary>
public sealed record PostRecord(long Id, int Type, int Tab, int DataId, string Params, bool Read, bool Taken, DateTimeOffset Sent, DateTimeOffset Expire,
    IReadOnlyList<(int TemplateId, long Count)> Attachments, int Reason);
// A friend, friend request or block of a character (Kind: friend, sent, received, blocked).
public sealed record SocialRelation(Guid CharacterId, string Name, int Level, int CreationId, int Race, int WireId, string Kind, string Memo = "");
// A class the character can change to: an enabled starter profile of its race and gender.
public sealed record AdminClass(int CreationId, string Name);
// Staff views (gamemaster/admin only). Account names come from the identity subject.
public sealed record AdminProfile(IReadOnlyList<string> Roles, string? BuildId);
/// <summary>An account's staff role and whether the database's permission table (auth.staff_allowed) allows an action.</summary>
public sealed record StaffAccess(string? Role, bool DatabaseAllows)
{
    /// <summary>Allowed only when the C# table and the database both allow it (migration 076).</summary>
    public bool Allows(string action) => DatabaseAllows && Aion.Core.Accounts.StaffPermissions.Allows(Role, action);
}
/// <summary>An audit event for the staff activity log: who did what to which character, with the event's details (JSON).</summary>
public sealed record AdminAuditEntry(long Id, DateTimeOffset At, string Action, Guid? CharacterId, string? CharacterName, string? ActorName, string Details);
public sealed record AdminCharacter(Guid CharacterId, string Name, Guid AccountId, string AccountName,
    int WorldId, int Level, int Race, int Gender, int CreationId, bool AccountDisabled, IReadOnlyList<string>? AccountRoles = null, bool Staff = false);
/// <summary>A catalogue item for the staff dashboard (latest build): grade and level from the item facts.</summary>
public sealed record AdminCatalogItem(int TemplateId, string Name, string? Category, string? Grade, int? Level, string? Race = null);
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
public partial interface IGameplayStore
{
    Task<Guid> CreateAsync(VerifiedAccount account, CreatePlayer input, CancellationToken ct = default);
    /// <param name="adoptAccountStorage">Move the account warehouse rows the account's other characters hold to this one (the playing
    /// character's own load only - the lobby and side reads pass false, or the warehouse jumps to whichever character was read last).</param>
    Task<PlayerInventory?> InventoryAsync(VerifiedAccount account, Guid character, CancellationToken ct = default, bool adoptAccountStorage = true);
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
    Task<bool> GrantTitleAsync(VerifiedAccount account, Guid character, int titleId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The equipped title of a category (1..3) or the shown one (0); 0 clears. The title must be owned.</summary>
    Task<bool> SetTitleAsync(VerifiedAccount account, Guid character, int category, int titleId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The equipped wing (migration 073); 0 clears. A wing must be registered to the character (game.character_wings).</summary>
    Task<bool> SetWingAsync(VerifiedAccount account, Guid character, int wingId, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Skins added to the collection (a count per skin).</summary>
    Task<bool> GrantSkinsAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> skins, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Items (whole stacks or part of one) from the bag to a warehouse ('storage' the account's, 'character_storage'
    /// the character's) or back, all or nothing.</summary>
    Task<bool> TransferItemsAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, long Count)> items, string from, string to,
        CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The account's currencies (ECurrencyCategory -> balance: Quna and its states), through one of its characters.</summary>
    Task<IReadOnlyDictionary<int, long>> AccountCurrenciesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, long>>(new Dictionary<int, long>());
    /// <summary>Adds (or, negative, spends) an account currency; the new balance, null when it would go below zero.</summary>
    Task<long?> AddAccountCurrencyAsync(VerifiedAccount account, Guid character, int category, long delta, CancellationToken ct = default) => Task.FromResult<long?>(null);
    /// <summary>The character's Daevanion nodes and item points.</summary>
    Task<DaevanionRecord> DaevanionAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult(DaevanionRecord.None);
    /// <summary>
    /// Learns <paramref name="add"/> and forgets <paramref name="remove"/> nodes, paying <paramref name="kina"/> (a bag stack,
    /// revision-checked) in the same transaction; false when something changed or is short.
    /// </summary>
    Task<bool> SaveDaevanionNodesAsync(VerifiedAccount account, Guid character, IReadOnlyList<int> add, IReadOnlyList<int> remove,
        (Guid Item, long Revision, long Count)? kina, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Uses up count of a Daevanion point item stack and sets that point type's item points to <paramref name="bonus"/>, atomically.</summary>
    Task<bool> UseDaevanionPointItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int count, int type, int bonus,
        CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The character's Monolith resonance counts (migration 090): Monolith id -> tokens resonated.</summary>
    Task<IReadOnlyDictionary<int, int>> MonolithsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());
    /// <summary>
    /// Uses up the token stacks (revision-checked), moves the Monolith's count from <paramref name="previous"/> to <paramref name="count"/>
    /// (false when it is no longer <paramref name="previous"/>) and grants the rewards, in one transaction.
    /// </summary>
    Task<bool> ResonateMonolithAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int monolith,
        int previous, int count, IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct = default) => Task.FromResult(false);
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
    /// limited goods' count raised within its limit (target 1 character, 2 account; period key).
    /// </summary>
    Task<bool> ShopBuyAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        IReadOnlyList<(int TemplateId, long Count)> goods, IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct = default);
    /// <summary>
    /// A Black Cloud market purchase, all or nothing: the Kina stacks consumed, <paramref name="quna"/> account Quna spent (bound
    /// Quna first), each limited goods' count raised within its limit, the items granted and <paramref name="qunaGranted"/> Quna
    /// credited (a Quna pack), audited with <paramref name="audit"/>. "ok", "money" (not enough Quna), "limit" or "failed".
    /// </summary>
    Task<string> PremiumShopBuyAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long quna,
        IReadOnlyList<(int TemplateId, long Count)> goods, long qunaGranted, long mileage, long mileageGranted, IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits,
        object audit, CancellationToken ct = default) => Task.FromResult("failed");
    /// <summary>The host's field boss kills (game.field_boss_kills): map, WorldMapFieldNamed id, kill and respawn time (Unix ms).</summary>
    Task<IReadOnlyList<(int Map, int Id, long KilledAt, long RespawnAt)>> FieldBossKillsAsync(VerifiedAccount account, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(int, int, long, long)>>([]);
    /// <summary>Keeps a field boss's last kill (the newer kill wins).</summary>
    Task<bool> SaveFieldBossKillAsync(VerifiedAccount account, int map, int id, long killedAt, long respawnAt, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The character's Black Cloud Merchants lists: (wire category, revision, JSON state).</summary>
    Task<IReadOnlyList<(int Category, long Revision, string State)>> RandomShopOffersAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(int, long, string)>>([]);
    /// <summary>
    /// Stores a Black Cloud Merchants list, all or nothing: the row written only at <paramref name="revision"/> (0: not there yet), the
    /// Kina stacks consumed, the items granted, audited (random_shop) when <paramref name="audit"/> is set. "ok", "stale" or "failed".
    /// </summary>
    Task<string> RandomShopSaveAsync(VerifiedAccount account, Guid character, int category, long revision, string state,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods, object? audit, CancellationToken ct = default) =>
        Task.FromResult("failed");
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
    /// <summary>Joins a legion by its rule: "ok", "member", "full", "denied", "password" or "none".</summary>
    Task<string> JoinGuildAsync(VerifiedAccount account, Guid character, long guild, string password, int maxMembers, CancellationToken ct = default);
    /// <summary>Leaves the legion: "ok", "master" (a master disbands instead) or "none".</summary>
    Task<string> LeaveGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>A master or officer removes a lower-grade member by name: "ok", "authority" or "none".</summary>
    Task<string> KickGuildMemberAsync(VerifiedAccount account, Guid character, string nickname, CancellationToken ct = default);
    /// <summary>The master disbands the legion: "ok", "authority" or "none".</summary>
    Task<string> DisbandGuildAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    /// <summary>The account's characters' last renames.</summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastRenamesAsync(VerifiedAccount account, CancellationToken ct = default);
    /// <summary>Renames a character for a Name Change Voucher: "ok", "taken", "invalid", "voucher" (none) or "locked" (168 hours).</summary>
    Task<string> RenameCharacterAsync(VerifiedAccount account, Guid character, string name, CancellationToken ct = default);
    /// <summary>Moves a bag item to a bag slot; an item already there takes the moved item's old slot.</summary>
    Task<bool> MoveBagItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int slot, CancellationToken ct = default);
    /// <summary>Moves a bag or warehouse item to a slot of 'bag', 'storage' or 'character_storage' (the item on that slot takes the old one).</summary>
    Task<bool> MoveItemToContainerAsync(VerifiedAccount account, Guid character, Guid item, long revision, string location, int slot, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>The character's mail, newest first (deleted ones left out).</summary>
    Task<IReadOnlyList<PostRecord>> PostsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PostRecord>>([]);
    /// <summary>Trusted server call (staff): a mail to any character; returns the stored post.</summary>
    Task<PostRecord?> SendPostAsync(VerifiedAccount actor, Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct = default) => Task.FromResult<PostRecord?>(null);
    /// <summary>Marks the character's posts read, or deletes them (a post with attachments not yet taken stays).</summary>
    Task<bool> MarkPostsAsync(VerifiedAccount account, Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Grants a post's attachments once and marks it taken.</summary>
    Task<bool> TakePostAsync(VerifiedAccount account, Guid character, long id, CancellationToken ct = default) => Task.FromResult(false);
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
    Task<IReadOnlyList<BossChallengeRecord>> BossChallengesAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BossChallengeRecord>>([]);
    Task<bool> SaveBossChallengeAsync(VerifiedAccount account, Guid character, BossChallengeRecord record, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<ContentTicketRecord>> ContentTicketsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContentTicketRecord>>([]);
    Task<bool> SaveContentTicketAsync(VerifiedAccount account, Guid character, ContentTicketRecord record, CancellationToken ct = default) => Task.FromResult(false);
    Task<StigmaState> StigmasAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult(StigmaState.Empty);
    /// <summary>Sets a stigma's level when its saved level is still <paramref name="expected"/> (0: not learned yet); false otherwise.</summary>
    Task<bool> SetStigmaLevelAsync(VerifiedAccount account, Guid character, int skillId, int level, int expected, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Consumes one GetStigmaShard item and adds its shards, in one transaction.</summary>
    Task<bool> UseStigmaShardItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int shards, CancellationToken ct = default) => Task.FromResult(false);
    Task<IReadOnlyList<SkillPartRecord>> SkillPartsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SkillPartRecord>>([]);
    /// <summary>Replaces one skill's equipped parts when they still equal <paramref name="expected"/>; false otherwise.</summary>
    Task<bool> ReplaceSkillPartsAsync(VerifiedAccount account, Guid character, int skillId, IReadOnlyList<SkillPartRecord> expected, IReadOnlyList<SkillPartRecord> parts,
        CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>A character id by name (any account), for mail to offline players.</summary>
    Task<Guid?> CharacterByNameAsync(VerifiedAccount account, string name, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
    /// <summary>
    /// Rearranges bag and warehouse rows (ItemSort, ItemStore/TakeOut, MergeStorageItem) all or nothing: every listed row must still
    /// have its revision and lie in the bag or a warehouse, and the quantities of each template must add up as before.
    /// </summary>
    Task<bool> ArrangeItemsAsync(VerifiedAccount account, Guid character, IReadOnlyList<ItemArrangement> items, string reason, CancellationToken ct = default) =>
        Task.FromResult(false);
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
    /// Which of the character's items came from a staff grant (game.item_grants of a game.admin_requests "grant": the staff
    /// item panel, staff rewards, the in-game GM command). Read only; items of another account or character are never listed.
    /// </summary>
    Task<IReadOnlyList<Guid>> StaffGrantedItemsAsync(VerifiedAccount account, Guid character, IReadOnlyList<Guid> items, CancellationToken ct = default);
    /// <summary>Uses up one wing item (GetWing) and registers its wing, in one transaction; false when the item does not match.</summary>
    Task<bool> UseWingItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int wingId, CancellationToken ct = default);
    /// <summary>
    /// Uses up one pet item and acquires its ride; a ride already owned gains a soul point instead.
    /// Null when the item does not match.
    /// </summary>
    Task<RideRecord?> UseRideItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, int rideId, CancellationToken ct = default);
    /// <summary>
    /// Uses up pet items of one ride in one transaction (self-using items gained by the hundred: staff grants, boxes): the first
    /// acquires the ride when not owned, every other one is a soul point. Items that no longer match are skipped; null when none was used.
    /// </summary>
    Task<RideRecord?> UseRideItemsAsync(VerifiedAccount account, Guid character, IReadOnlyList<(Guid Item, long Revision)> items, int rideId, CancellationToken ct = default);
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

    /// <summary>Records a suspected cheat (speed/teleport/skill-rate) in the staff audit log; best-effort, no transaction.</summary>
    Task RecordCheatAsync(Guid character, string kind, object details, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Whether a character already uses this name (names are unique regardless of case).</summary>
    Task<bool> NameTakenAsync(VerifiedAccount account, string name, CancellationToken ct = default);
    Task<bool> UnblockAsync(VerifiedAccount account, Guid character, Guid blocked, CancellationToken ct = default);
    // Player trade: both sides' offered items (instance, count) change hands in one transaction, or nothing
    // does (an item gone, changed, soul-bound, equipped or short). Whole stacks move with their row; part
    // of a stack is split off and granted to the receiver.
    Task<bool> ExchangeAsync(VerifiedAccount first, Guid firstCharacter, IReadOnlyList<(Guid Item, long Count)> firstGives,
        VerifiedAccount second, Guid secondCharacter, IReadOnlyList<(Guid Item, long Count)> secondGives, CancellationToken ct = default);
    // Soul bind of an owned item: upgrades.soul_bind = count, revision bumped.
    Task<bool> SoulBindItemAsync(VerifiedAccount account, Guid character, Guid item, byte count, CancellationToken ct = default, IReadOnlyList<(int Type, int Value)>? stats = null);
    /// <summary>Removes copies of owned skins (skin id -> count; the last copy deletes the row) and grants new ones, in one transaction.</summary>
    Task<bool> CombineSkinsAsync(VerifiedAccount account, Guid character, IReadOnlyDictionary<int, int> used, IReadOnlyList<int> granted, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>Sets one field of an owned item's upgrades (JSON value), revision bumped; false when the item is not the character's.</summary>
    Task<bool> SetItemUpgradeAsync(VerifiedAccount account, Guid character, Guid item, string field, string json, CancellationToken ct = default) =>
        Task.FromResult(false);
    /// <summary>
    /// An enchant attempt, all or nothing: the costs (item, revision, count) are consumed and the item (at its revision)
    /// gets upgrades.enchant = level and upgrades.enchant_bonus = bonus (the failure correction built up so far).
    /// </summary>
    Task<bool> EnchantItemAsync(VerifiedAccount account, Guid character, Guid item, long revision,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, int level, int bonus, CancellationToken ct = default, string field = "enchant");
    Task<JsonElement> AdminAsync(VerifiedAccount actor, AdminCommand command, CancellationToken ct = default);
    /// <summary>The account's own character ascends by play (the HQ1101020 turn-in): the grade rises to <paramref name="grade"/>, never falls.</summary>
    Task<bool> AscendAsync(VerifiedAccount account, Guid character, int grade, CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>
    /// Staff: removes the given counts of a character's items (any location; a count at or above the stack removes the row), in
    /// one transaction, audited as staff_items_deleted and delivered live (pg_notify aion_inventory). The rows removed, or null
    /// when an item is not the character's.
    /// </summary>
    Task<int?> AdminDeleteItemsAsync(VerifiedAccount actor, Guid character, IReadOnlyList<(Guid Item, long Count)> items, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);
    // Throw GameplayException("forbidden") unless the actor holds gamemaster or admin.
    Task<AdminProfile> AdminProfileAsync(VerifiedAccount actor, CancellationToken ct = default);
    /// <summary>The actor's rank and the database's answer for one permission-table action (migration 076).</summary>
    Task<StaffAccess> StaffAccessAsync(VerifiedAccount actor, string action, CancellationToken ct = default) =>
        Task.FromResult(new StaffAccess(null, false));
    /// <summary>The account a character belongs to (staff value checks), or null.</summary>
    Task<Guid?> CharacterAccountAsync(Guid character, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
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
    Task<AdminCatalogPage> AdminCatalogAsync(VerifiedAccount actor, string query, string? category, int offset, int limit, CancellationToken ct = default, string? race = null);
    // Quests of an owned character; null when not owned.
    Task<IReadOnlyList<QuestRecord>?> QuestsAsync(VerifiedAccount account, Guid character, CancellationToken ct = default);
    // Insert or update one quest by code (uid assigned on insert); the stored record.
    Task<QuestRecord> SaveQuestAsync(VerifiedAccount account, Guid character, QuestRecord quest, CancellationToken ct = default);
    Task<bool> RemoveQuestAsync(VerifiedAccount account, Guid character, string code, CancellationToken ct = default);
    // Server-decided reward of a completed quest: marks it rewarded, adds experience and grants the
    // items once (repeating the call for a rewarded quest grants nothing). Returns the new experience.
    Task<long?> RewardQuestAsync(VerifiedAccount account, Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct = default);
}
