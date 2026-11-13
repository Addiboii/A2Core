using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Item use, reward boxes (chests) and rides (pets/mounts). Data: RewardBoxes (tools/data/boxes.json);
// wire: RidePackets. Chests: OpenRewardBox_RQ (or ItemUse_RQ on a chest) rolls the box, the store uses
// the box up and grants the rewards in one transaction, then pet items (AutoUseWhenAcquire) are used at
// once. Items (ItemUse_RQ): pet items acquire their ride (or a soul point when owned), soul items add
// soul points, potions heal (item skills: HpHeal_Item, HpHealRatio_Item, MpHeal_Item, FpHeal_Item), the
// return scroll casts and teleports home, Kina chests pay out. Rides: acquired with souls (0x9007),
// levelled with souls (0x9013), mounted (0x9001/0x9004); every acquired ride adds its VehicleStat stats.
public sealed partial class GameSession
{
    readonly Dictionary<int, RideEntry> rides = [];
    internal bool OwnsRideForChecks(int rideId) => rides.TryGetValue(rideId, out var r) && r.Owned;
    /// <summary>Soul crystals by ESoulCrystalType (6 = All).</summary>
    readonly Dictionary<int, int> crystals = [];
    public IReadOnlyDictionary<int, int> Crystals => crystals;
    static readonly Dictionary<string, int> CrystalTypes = new()
    {
        ["SoulCrystalFeral"] = 1, ["SoulCrystalIntellect"] = 2, ["SoulCrystalNature"] = 3, ["SoulCrystalTrans"] = 4, ["SoulCrystalSpecial"] = 5, ["SoulCrystalAll"] = 6,
    };
    public const int AllCrystals = 6;
    int defaultRide;
    int? mountedRide;
    /// <summary>Acquired rides (soul points also collect for rides not acquired yet).</summary>
    public IReadOnlyCollection<int> Rides => rides.Values.Where(r => r.Owned).Select(r => r.RideId).ToList();
    public int? MountedRide => mountedRide;
    public IReadOnlyDictionary<int, RideEntry> RideStates => rides;
    static RewardBoxes Boxes => RewardBoxes.Embedded;
    /// <summary>Item cooldowns by Item.dat CoolTimeGroup (ready time).</summary>
    readonly Dictionary<string, long> itemCooldowns = [];
    /// <summary>A cast item in progress (return scroll): key, item, when it completes.</summary>
    (long Key, int ItemId, long DueMs)? itemCast;
    Vector3d? castFrom;
    /// <summary>Moving farther than this from where a cast item started cancels it (cm).</summary>
    public const double CastMoveTolerance = 100;

    bool OwnsRide(int ride) => rides.TryGetValue(ride, out var r) && r.Owned;

    async Task LoadRidesAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        var stored = await store.RidesAsync(character, ct);
        rides.Clear();
        foreach (var r in stored.Rides) rides[r.RideId] = new RideEntry(r.RideId, RideLevel(r.RideId, r.Level), r.SoulPoints, r.Owned);
        defaultRide = stored.DefaultRide;
        crystals.Clear();
        foreach (var (type, amount) in stored.Crystals ?? new Dictionary<int, int>()) crystals[type] = amount;
        LoadBoards(stored.Boards);
        mountedRide = null;
    }

    void Remember(RideRecord r) => rides[r.RideId] = new RideEntry(r.RideId, RideLevel(r.RideId, r.Level), r.SoulPoints, r.Owned);

    /// <summary>
    /// A ride's level as shown and used. The 7 Special creatures (VehicleList CreatureType Special, e.g. BlackDragon_01) have no stat
    /// group (StatGroupName None) and their own soul crystal: levelling them gives nothing, so they count as the top VehicleLevel
    /// from their acquisition (USER RULE 2026-11-12, "the 7 special pets should just be max level when acquired"; OURS - the official
    /// level of a Special pet is unrecorded). The stored level is left as it is.
    /// </summary>
    static int RideLevel(int rideId, int stored) => Boxes.Ride(rideId) is { Type: SpecialCreatureType } ? Math.Max(stored, Boxes.RideMaxLevel) : stored;
    const int SpecialCreatureType = 6;   // ECreatureType Special (GameSession.Board.cs CreatureTypeNames)

    byte[] RideInit() => RidePackets.RideInitData(defaultRide, rides.Values.ToList(), crystals, boards.Count > 0 ? boards.Values.ToList() : null);

    /// <summary>The ride's board after a ride gained a level (1 = acquired): exp added, the acquire/level message's board part.</summary>
    async Task<(List<byte[]> Extra, CreatureBoard? Board)> RideBoardAsync(RideInfo info, int rideLevel, CancellationToken ct)
    {
        var extra = await AddBoardExpAsync(info.Type, Boxes.RideBoardExp(rideLevel), ct);
        return (extra, boards.GetValueOrDefault((byte)info.Type));
    }

    /// <summary>A stat summed over the acquired rides at their levels (VehicleStat; CANDIDATE: every acquired ride counts).</summary>
    int RideStat(string stat) => rides.Values.Where(r => r.Owned).Sum(r => Boxes.RideStats(r.RideId, r.Level).GetValueOrDefault(stat));

    // ---------- Chests ----------

    /// <summary>0x5646 OpenRewardBox_RQ {i64 box uid, u8 select index, i32 open count}.</summary>
    Task<List<byte[]>> OpenBoxRequestAsync(byte[] plain, long now, CancellationToken ct) =>
        OpenBoxAsync(BitConverter.ToInt64(plain, 2), plain[10], BitConverter.ToInt32(plain, 11), now, ct);

    async Task<List<byte[]>> OpenBoxAsync(long uid, byte select, int count, long now, CancellationToken ct)
    {
        storedItems.TryGetValue(uid, out var row);
        var itemId = row?.TemplateId ?? 0;
        // EResult Item_OpenRewardBox_*: 861 success (OFFICIAL 3 of 3 replies, rec 20261107-011529), 864 fail (we sent 0 / 1).
        List<byte[]> Fail() => [RidePackets.OpenBoxReply(row is null ? EResult.Item_OpenRewardBox_InvalidItem : EResult.Item_OpenRewardBox_Fail, uid, itemId, select, count, [])];
        if (Store is not { } store || CharacterId is not { } character || row is null || row.Location != "bag"
            || Boxes.Use(row.TemplateId) is not { Effect: "RewardBox", Values: [var group, ..] })
            return Fail();
        // A box of the other faction (Item.dat ItemRace) is not opened (CANDIDATE code: the client refuses its use itself).
        if (!ItemRules.RaceAllows(row.TemplateId, profileRace))
            return [RidePackets.OpenBoxReply(EResult.Item_Common_Fail_InvalidRace, uid, itemId, select, count, [])];
        count = (int)Math.Min(Math.Clamp(count, 1, Math.Max(1, Boxes.MaxOpen(group, profileRace))), row.Quantity);
        // Only the opener's faction (FactionRewards.cs): the race / class rows of the box, other-faction items swapped or left out.
        if (count < 1 || Boxes.Roll(group, select, count, rng, profileRace, EntryClassName) is not { } rewards) return Fail();
        // Room for the contents (GameSession.BagRoom.cs; the box's slot frees when all of it is opened).
        if (!BagRoomFor(rewards, count >= row.Quantity && ItemRules.NeedsBagSlot(row.TemplateId) ? 1 : 0))
            return [RidePackets.OpenBoxReply(EResult.Item_RewardBox_NotEnoughSlot, uid, itemId, select, count, [])];
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, count, rewards, ct) ? [[]] : []);
        if (done.Count == 0) return Fail();
        var replies = new List<byte[]> { RidePackets.OpenBoxReply(EResult.Item_OpenRewardBox_Success, uid, itemId, select, count, rewards) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseOpenRewardBox, DecreaseOpenRewardBox));
        // Pet items are used as soon as they are acquired (Item.dat AutoUseWhenAcquire).
        foreach (var (rewardId, _) in rewards)
            if (Boxes.Use(rewardId) is { AutoUse: true, Effect: "GetVehicle" })
                foreach (var key in storedItems.Where(s => s.Value.TemplateId == rewardId && s.Value.Location == "bag").Select(s => s.Key).ToList())
                    replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        return replies;
    }

    // ---------- Item use ----------

    /// <summary>
    /// 0x5612 ItemUse_RQ {i64 uid}. Refused (result 1) for an unknown or cooling item, or an effect
    /// that is not implemented (skins, titles, tickets, ...).
    /// </summary>
    async Task<List<byte[]>> UseItemAsync(long uid, long now, CancellationToken ct, bool reply = true)
    {
        storedItems.TryGetValue(uid, out var row);
        var itemId = row?.TemplateId ?? 0;
        // The server's own summary (boxes.json) first; any other use action comes from the item XML (GetWing, ...).
        var use = row is null ? null : Boxes.Use(row.TemplateId)
            ?? (ItemDB.Embedded.Of(row.TemplateId) is { Use: { } action } record ? new ItemUse(action.Effect, record.AutoUseWhenAcquired, action.Values) : null);
        if (use is { Effect: "RewardBox" }) return await OpenBoxAsync(uid, 0, 1, now, ct);
        List<byte[]> Fail() => reply ? [RidePackets.ItemUseReply(1, uid, itemId)] : [];
        if (Store is not { } store || CharacterId is not { } character || row is null || row.Location != "bag" || use is null || resources is null || dead)
            return Fail();
        // Item.dat ItemRace and the period items (ItemRules.cs): the client's own results (CANDIDATE: never recorded).
        if (!ItemRules.RaceAllows(row.TemplateId, profileRace)) return reply ? [RidePackets.ItemUseReply(EResult.Item_Use_FailRace, uid, itemId)] : [];
        if (ItemRules.Expired(row.TemplateId, row.Acquired, now)) return reply ? [RidePackets.ItemUseReply(EResult.Item_Common_Fail_ExpiredPeriodItem, uid, itemId)] : [];
        var cooldown = ItemCooldownOf(row.TemplateId);
        if (cooldown.Group != "None" && now < itemCooldowns.GetValueOrDefault(cooldown.Group)) return Fail();
        // Duty scrolls (GetDutyQuest [group]): a duty quest of the group (GameSession.Duty.cs).
        if (use.Effect == "GetDutyQuest")
        {
            var given = await UseDutyScrollAsync(store, character, row, use, uid, reply, ct);
            if (given.Any(b => b.Length >= 2 && (b[0] | b[1] << 8) == 0x8d11))
            {
                if (cooldown.Group != "None") itemCooldowns[cooldown.Group] = now + cooldown.CooldownMs;
                given.AddRange(AchieveItemUse(itemId, use.Effect));
            }
            return given;
        }
        // Daevanion crystals: the official order is the item update (0x561b), ItemUse_RS 772, then 0xe22f with the item points.
        if (use.Effect is "GetDaevanionPoint" or "Fix_DaevanionPoint")
        {
            var (result, _, changes) = await UseDaevanionPointItemsAsync(row, use, 1, ct);
            if (changes is null) return reply ? [RidePackets.ItemUseReply(result == 0 ? 1 : result, uid, itemId)] : [];
            if (cooldown.Group != "None") itemCooldowns[cooldown.Group] = now + cooldown.CooldownMs;
            return [.. changes, .. (reply ? new List<byte[]> { RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, WireKey(row), row.TemplateId) } : []),
                DaevanionItemPointUpdate(), .. AchieveItemUse(itemId, use.Effect)];
        }
        var replies = use.Effect switch
        {
            "SealStone" => await UseShardItemAsync(row, uid, reply, ct),
            "GetVehicle" => await UsePetItemAsync(store, character, row, use, reply, ct),
            "GetVehicleSoul" => await UseSoulItemAsync(store, character, row, use, reply, ct),
            "GetVehicleSoulCrystal" => await UseCrystalItemAsync(store, character, row, use, reply, ct),
            "Fix_VehicleCreatureLevel" => await UseBoardLevelItemAsync(store, character, row, use, reply, ct),
            "Skill" => await UseSkillItemAsync(store, character, row, use, reply, now, ct),
            "Telpo_Home" when itemCast is null => StartItemCast(row, cooldown, reply, now),
            // Wings (GetWing [wing id]): a cast (Wing_Get_Casting, 5 s in the item data; OURS: instant, USER 2026-11-08), then the wing
            // joins the character's wings.
            "GetWing" when itemCast is null && use.Values is [var wingText, ..] && int.TryParse(wingText, out var wing) && wing > 0
                && !OwnedWings().Any(w => w.Id == wing) => StartItemCast(row, cooldown with { CastMs = Math.Min(cooldown.CastMs, WingCastMs) }, reply, now),
            "GetKina" or "GetNonBindingKina" when use.Values is [var amount, ..] && long.TryParse(amount, out var kina) && kina > 0 =>
                await UseKinaItemAsync(store, character, row, use.Effect == "GetNonBindingKina" ? NonBindingKinaItemId : KinaItemId, kina, reply, ct),
            "GetTitle" when use.Values is [var titleText, ..] && int.TryParse(titleText, out var titleId) => await UseTitleItemAsync(store, character, row, titleId, reply, ct),
            "GetSkin" or "GetSkinSet" when use.Values is [{ Length: > 0 }, ..] => await UseSkinItemAsync(store, character, row, use, reply, ct),
            "GetSocialAction" when use.Values is [var emoteText, ..] && int.TryParse(emoteText, out var emote) => await UseSocialActionItemAsync(store, character, row, emote, reply, ct),
            "GetGodstone" => await UseGodstoneItemAsync(store, character, row, use, reply, ct),
            "GetDeco" => await UseDecoItemAsync(store, character, row, use, reply, ct),
            // Empyrean Trace express vouchers (GameSession.EmpyreanTraces.cs).
            "MonolithAllClear" => await UseMonolithVoucherAsync(store, character, row, use, reply, now, ct),
            // Official order (11 of 13 recorded uses): the item update (0x561b), ItemUse_RS, then the points (0x3811).
            "GetStigmaShard" or "GetStigmaShardSuperior" or "Fix_StigmaSkillPoint" => await UseStigmaItemsAsync(store, character, row, use, 1, ct) is { Count: > 0 } shard
                ? [.. shard[..^1], .. (reply ? new List<byte[]> { RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, WireKey(row), row.TemplateId) } : []), shard[^1]] : null,
            "AscensionGrade" when use.Values is [var gradeText, ..] && int.TryParse(gradeText.Split('_')[^1], out var grade) && grade is >= 2 and <= 6
                => await UseAscensionItemAsync(store, character, row, grade, reply, ct),
            // Wisdom Stones: the official order is the item update, ItemUse_RS 772, then 0x3811 with the stones' points in total.
            "GetSkillPoint" => await UseSkillPointItemsAsync(store, character, row, use, 1, ct) is { } stone
                ? [.. stone, .. (reply ? new List<byte[]> { RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, WireKey(row), row.TemplateId) } : []),
                    PointsBonus()] : null,
            _ => null,
        };
        if (replies is null) return Fail();
        if (cooldown.Group != "None") itemCooldowns[cooldown.Group] = now + cooldown.CooldownMs;
        replies.AddRange(AchieveItemUse(itemId, use.Effect));
        return replies;
    }

    /// <summary>
    /// 0x5614 ItemUseByItemID_RQ {uid i64, item id i32, use count i64}: the client's "use N" of one stack (live: 1000 Kina
    /// pouches 503700067). Kina items pay count x amount in one transaction; reward boxes open count at once (their own
    /// 0x5647 and limit). Counts are clamped to the stack. Other effects are refused (result 1) rather than used N times.
    /// Reply order as OBSERVED: the item changes (0x561b), then 0x5615 with the count actually used.
    /// </summary>
    async Task<List<byte[]>> UseItemByIdAsync(long uid, int itemId, long count, long now, CancellationToken ct)
    {
        if (!storedItems.TryGetValue(uid, out var row) || row.TemplateId != itemId)
            row = storedItems.Values.FirstOrDefault(r => r.TemplateId == itemId && r.Location == "bag");
        var key = row is null ? uid : WireKey(row);
        List<byte[]> Fail() => [RidePackets.ItemUseByIdReply(1, key, itemId, 0)];
        var use = row is null ? null : Boxes.Use(row.TemplateId)
            ?? (ItemDB.Embedded.Of(row.TemplateId) is { Use: { } action } record ? new ItemUse(action.Effect, record.AutoUseWhenAcquired, action.Values) : null);
        if (Store is not { } store || CharacterId is not { } character || row is null || row.Location != "bag" || dead || use is null || count < 1)
            return Fail();
        if (!ItemRules.RaceAllows(row.TemplateId, profileRace)) return [RidePackets.ItemUseByIdReply(EResult.Item_Use_FailRace, key, itemId, 0)];
        if (ItemRules.Expired(row.TemplateId, row.Acquired, now)) return [RidePackets.ItemUseByIdReply(EResult.Item_Common_Fail_ExpiredPeriodItem, key, itemId, 0)];
        var used = (int)Math.Min(Math.Min(count, row.Quantity), int.MaxValue);
        if (use.Effect is "GetStigmaShard" or "GetStigmaShardSuperior" or "Fix_StigmaSkillPoint")
            return await UseStigmaItemsAsync(store, character, row, use, use.Effect == "Fix_StigmaSkillPoint" ? 1 : used, ct) is { } shards
                ? [.. shards, RidePackets.ItemUseByIdReply(RidePackets.ItemUseSuccess, key, itemId, use.Effect == "Fix_StigmaSkillPoint" ? 1 : used)] : Fail();
        // A stack of Daevanion crystals at once (OBSERVED rec 115817 #74857-74859: 23 used, 0x561b, 0x5615 772, 0xe22f).
        if (use.Effect is "GetDaevanionPoint" or "Fix_DaevanionPoint")
        {
            var (result, usedNow, changes) = await UseDaevanionPointItemsAsync(row, use, used, ct);
            return changes is null ? [RidePackets.ItemUseByIdReply(result == 0 ? 1 : result, key, itemId, 0)]
                : [.. changes, RidePackets.ItemUseByIdReply(RidePackets.ItemUseSuccess, key, itemId, usedNow), DaevanionItemPointUpdate()];
        }
        // A stack of Wisdom Stones at once: all their points, one 0x3811.
        if (use.Effect == "GetSkillPoint")
            return await UseSkillPointItemsAsync(store, character, row, use, used, ct) is { } stones
                ? [.. stones, RidePackets.ItemUseByIdReply(RidePackets.ItemUseSuccess, key, itemId, used), PointsBonus()] : Fail();
        if (use.Effect == "RewardBox")
            return [.. await OpenBoxAsync(key, 0, used, now, ct), RidePackets.ItemUseByIdReply(RidePackets.ItemUseSuccess, key, itemId, used)];
        if (use is not { Effect: "GetKina" or "GetNonBindingKina", Values: [var amount, ..] } || !long.TryParse(amount, out var kina) || kina < 1
            || kina > long.MaxValue / used)
            return Fail();
        var cooldown = Boxes.Cooldown(row.TemplateId);
        if (cooldown.Group != "None" && now < itemCooldowns.GetValueOrDefault(cooldown.Group)) return Fail();
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, used, [(use.Effect == "GetNonBindingKina" ? NonBindingKinaItemId : KinaItemId, kina * used)], ct) ? [[]] : []);
        if (done.Count == 0) return Fail();
        if (cooldown.Group != "None") itemCooldowns[cooldown.Group] = now + cooldown.CooldownMs;
        var replies = await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse);
        replies.Add(RidePackets.ItemUseByIdReply(RidePackets.ItemUseSuccess, key, itemId, used));
        replies.AddRange(Achieve("UseItem", used, e => e.Items?.Contains(itemId) == true));
        replies.AddRange(Achieve("UseUsableItemType", used, e => e.Has(use.Effect)));
        return replies;
    }

    List<byte[]> Used(ItemState row, bool reply) =>
        reply ? [RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, WireKey(row), row.TemplateId), RidePackets.ItemUsed(WireKey(row), row.TemplateId)] : [RidePackets.ItemUsed(WireKey(row), row.TemplateId)];

    /// <summary>A pet item: the ride is acquired (0x9009), or one soul point when already owned (0x900d).</summary>
    async Task<List<byte[]>?> UsePetItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var value, ..] || !int.TryParse(value, out var rideId) || Boxes.Ride(rideId) is not { } info) return null;
        var owned = OwnsRide(rideId);
        RideRecord? ride = null;
        await Stored(async () => (ride = await store.UseRideItemAsync(character, row.InstanceId, row.Revision, rideId, ct)) is null ? [] : [[]]);
        if (ride is null) return null;
        Remember(ride);
        var replies = Used(row, reply);
        if (owned) replies.Add(RidePackets.SoulPoints([(rideId, 1)]));
        else
        {
            var (extra, board) = await RideBoardAsync(info, 1, ct);
            replies.Add(RidePackets.Acquired(playerKey, rideId, RideLevel(rideId, ride.Level), (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0));
            replies.AddRange(extra);
        }
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse));
        if (!owned) replies.AddRange(RefreshStats());
        return replies;
    }

    /// <summary>A soul item [ride, count]: soul points for the ride, owned or not (0x900d).</summary>
    async Task<List<byte[]>?> UseSoulItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var value, var amount, ..] || !int.TryParse(value, out var rideId) || !int.TryParse(amount, out var souls) || souls < 1
            || Boxes.Ride(rideId) is null) return null;
        RideRecord? ride = null;
        await Stored(async () => (ride = await store.AddRideSoulsAsync(character, row.InstanceId, row.Revision, rideId, souls, ct)) is null ? [] : [[]]);
        if (ride is null) return null;
        Remember(ride);
        var replies = Used(row, reply);
        replies.Add(RidePackets.SoulPoints([(rideId, souls)]));
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse));
        return replies;
    }

    /// <summary>A crystal item [ESoulCrystalType name, count]: crystals of that type (0x9011 reason Item).</summary>
    async Task<List<byte[]>?> UseCrystalItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var name, var amount, ..] || !CrystalTypes.TryGetValue(name, out var type) || !int.TryParse(amount, out var count) || count < 1) return null;
        int? total = null;
        await Stored(async () => (total = await store.AddRideCrystalsAsync(character, row.InstanceId, row.Revision, type, count, ct)) is null ? [] : [[]]);
        if (total is not { } now) return null;
        crystals[type] = now;
        return [.. Used(row, reply), RidePackets.CrystalsGained(new Dictionary<int, int> { [type] = count }, RidePackets.ItemReason),
            .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse)];
    }

    /// <summary>
    /// 0x900f RideSoulCrystallize_RQ {array {ride i32, points i32}}: soul points become crystals of each
    /// ride's ESoulCrystalType, one for one (CANDIDATE rate).
    /// </summary>
    async Task<List<byte[]>> CrystallizeAsync(byte[] plain, CancellationToken ct)
    {
        var targets = new List<(int RideId, int Points, int CrystalType)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 256; i++) targets.Add(((int)reader.U32(), (int)reader.U32(), 0));
        }
        catch (ProtocolException) { return [RidePackets.CrystallizeReply(1, [])]; }
        targets = targets.Select(x => x with { CrystalType = Boxes.Ride(x.RideId)?.Crystal ?? 0 }).ToList();
        if (Store is not { } store || CharacterId is not { } character || targets.Count == 0
            || targets.Any(x => x.Points < 1 || x.CrystalType < 1 || !rides.TryGetValue(x.RideId, out var r) || r.SoulPoints < x.Points)
            || targets.GroupBy(x => x.RideId).Any(g => g.Count() > 1))
            return [RidePackets.CrystallizeReply(1, [])];
        var done = await Stored(async () => await store.CrystallizeAsync(character, targets, ct) ? [[]] : []);
        if (done.Count == 0) return [RidePackets.CrystallizeReply(1, [])];
        foreach (var (ride, points, type) in targets)
        {
            rides[ride] = rides[ride] with { SoulPoints = rides[ride].SoulPoints - points };
            crystals[type] = crystals.GetValueOrDefault(type) + points;
        }
        var pairs = targets.Select(x => (x.RideId, x.Points)).ToList();
        return [RidePackets.CrystallizeReply(0, pairs), RidePackets.SoulPointsSpent(pairs),
            RidePackets.CrystalsGained(targets.GroupBy(x => x.CrystalType).ToDictionary(g => g.Key, g => g.Sum(x => x.Points)), RidePackets.CrystallizeReason)];
    }

    /// <summary>Whether souls plus crystals of the ride's type (then All) cover a cost.</summary>
    bool CanPay(int rideId, int cost) =>
        (rides.TryGetValue(rideId, out var r) ? r.SoulPoints : 0) + crystals.GetValueOrDefault(Boxes.Ride(rideId)?.Crystal ?? 0) + crystals.GetValueOrDefault(AllCrystals) >= cost;

    /// <summary>Mirrors the store's payment: souls first, then crystals of the type, then All; the messages for what was spent.</summary>
    List<byte[]> Paid(int rideId, int soulsBefore, int cost)
    {
        var replies = new List<byte[]>();
        var fromSouls = Math.Min(soulsBefore, cost);
        if (fromSouls > 0) replies.Add(RidePackets.SoulPointsSpent([(rideId, fromSouls)]));
        var rest = cost - fromSouls;
        var spent = new Dictionary<int, int>();
        foreach (var type in new[] { Boxes.Ride(rideId)?.Crystal ?? 0, AllCrystals }.Where(t => t > 0).Distinct())
        {
            var take = Math.Min(crystals.GetValueOrDefault(type), rest);
            if (take <= 0) continue;
            crystals[type] -= take;
            spent[type] = take;
            rest -= take;
        }
        if (spent.Count > 0) replies.Add(RidePackets.CrystalsSpent(spent));
        return replies;
    }

    /// <summary>A Kina chest [amount]: the Kina currency item is granted.</summary>
    async Task<List<byte[]>?> UseKinaItemAsync(INativeCharacterStore store, Guid character, ItemState row, int kinaItem, long kina, bool reply, CancellationToken ct)
    {
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, [(kinaItem, kina)], ct) ? [[]] : []);
        if (done.Count == 0) return null;
        return [.. Used(row, reply), .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse)];
    }

    /// <summary>
    /// An item skill (potions, food, drinks, scrolls): instant HP/MP/FP heals now; Abnormal_True
    /// effects (heal over time, stat buffs) go through ApplyItemAbnormals. The official order: skill
    /// start on the player (0x3802), the item count (0x561b), ItemUse_NT (0x5616), then the effect.
    /// </summary>
    async Task<List<byte[]>?> UseSkillItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct)
    {
        if (use.Values is not [var value, ..] || !int.TryParse(value, out var skillId) || resources is not { } r) return null;
        var effects = Boxes.SkillEffects(skillId);
        int Roll(SkillEffectRow e) => rng.Next(Math.Min(e.Value(0), e.Value(1)), Math.Max(e.Value(0), e.Value(1)) + 1);
        int hp = 0, mp = 0, fp = 0;
        foreach (var e in effects)
            switch (e.Type)
            {
                case "HpHeal_Item": hp += Roll(e); break;
                // CANDIDATE: values 0/1 are the share of max HP in 1/10000.
                case "HpHealRatio_Item": hp += (int)((long)r.HpMax * Roll(e) / 10_000); break;
                case "MpHeal_Item": mp += Roll(e); break;
                case "FpHeal_Item": fp += Roll(e); break;
            }
        // Potion strength from every source (titles, gear, wings): HpPotionRate / MpPotionRate in 0.01% units, Hp/MpPotionRegen flat.
        if (hp > 0) hp = HealReceived((int)Math.Round(hp * (1 + Bonus("HpPotionRate") / 10_000.0)) + Bonus("HpPotionRegen"));
        if (mp > 0) mp = (int)Math.Round(mp * (1 + Bonus("MpPotionRate") / 10_000.0)) + Bonus("MpPotionRegen");
        var abnormal = effects.Any(e => e.Type == "Abnormal_True");
        if (hp == 0 && mp == 0 && fp == 0 && !(abnormal && ItemAbnormalsSupported(effects))) return null;
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        actionUid = actionUid % 255 + 1;
        var replies = new List<byte[]>();
        if (reply) replies.Add(RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, WireKey(row), row.TemplateId));
        replies.Add(GamePackets.SkillStart(skillId, actionUid, worldPlayer?.Heading ?? 0, position ?? Vector3d.Zero, playerKey, actorKey: playerKey));
        // OBSERVED (012837): the item skill's end (0x3806) right after its start, before the item count and ItemUse_NT (20 of 21).
        replies.Add(GamePackets.SkillEnd(playerKey, skillId, actionUid));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.Add(RidePackets.ItemUsed(WireKey(row), row.TemplateId));
        r.Hp = Math.Min(r.HpMax, r.Hp + hp);
        r.Mp = Math.Min(r.MpMax, r.Mp + mp);
        if (r.FpMax > 0) r.Fp = Math.Min(r.FpMax, r.Fp + fp);
        if (hp > 0 || mp > 0) replies.Add(r.Update());
        if (fp > 0 && r.FpMax > 0) replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Fp, r.Fp)]));
        if (abnormal) replies.AddRange(ApplyItemAbnormals(skillId, effects, now));
        return replies;
    }

    /// <summary>Learning a wing from its item takes this long at most (OURS, USER 2026-11-08: instant; the item data says 5 s).</summary>
    public const int WingCastMs = 0;

    /// <summary>A cast item (return scroll): 0x5617 now, completed on the tick after the cast time unless the player moves.</summary>
    List<byte[]>? StartItemCast(ItemState row, ItemCooldown cooldown, bool reply, long now)
    {
        var key = WireKey(row);
        itemCast = (key, row.TemplateId, now + Math.Max(0, cooldown.CastMs));
        castFrom = position;
        var started = RidePackets.ItemUseStarted(playerKey, key, row.TemplateId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [started]);
        return reply ? [RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, key, row.TemplateId), started] : [started];
    }

    /// <summary>Moving (or casting) cancels a cast item: 0x5618 to the player and those nearby.</summary>
    List<byte[]> InterruptItemCast()
    {
        if (itemCast is not { } cast) return [];
        itemCast = null;
        var stopped = RidePackets.ItemUseInterrupted(playerKey, cast.Key, cast.ItemId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [stopped]);
        return [stopped];
    }

    /// <summary>
    /// An Ascension item (AscensionGrade [AscensionGrade_N], the quest rewards 533900001-005 "Ascension: Daeva" and on): used up,
    /// the character's ascension raised to N when lower (stored), the ascension updates as at the quest's turn-in (Ascend).
    /// </summary>
    async Task<List<byte[]>?> UseAscensionItemAsync(INativeCharacterStore store, Guid character, ItemState row, int grade, bool reply, CancellationToken ct)
    {
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        var before = selected?.Ascension ?? 1;
        if (before < grade && await store.AscendAsync(character, grade, ct))
            replies.AddRange([.. Ascend(grade), .. await AscensionRewardsAsync(before, grade, ct)]);
        return replies;
    }

    bool autoUsing;

    /// <summary>
    /// Bag items that use themselves on acquisition (Item.dat AutoUseWhenAcquire with an effect this server carries out,
    /// ItemRules.AutoUseEffects): used now, for every way an item arrives (quest rewards, loot, boxes, mail, staff grants) and at
    /// login for what was left before (live 2026-11-06: Ascension: Daeva stayed in the bag). Skins all at once
    /// (AutoUseSkinItemsAsync), Quna onto the account (AutoUseQunaItemsAsync), the pets of a ride in one transaction
    /// (AutoUsePetItemsAsync), every other stack whole (AutoUseStackAsync, GameSession.ItemStorage.cs).
    /// </summary>
    async Task<List<byte[]>> AutoUseAcquiredAsync(IEnumerable<long> keys, CancellationToken ct)
    {
        if (autoUsing || resources is null || dead || Store is not { } store || CharacterId is not { } character) return [];
        autoUsing = true;
        try
        {
            var rows = keys.Distinct().Select(k => storedItems.GetValueOrDefault(k)).OfType<ItemState>()
                .Where(r => r.Location == "bag" && ItemRules.IsAutoUse(r.TemplateId)).ToList();
            if (rows.Count == 0) return [];
            var replies = new List<byte[]>();
            replies.AddRange(await AutoUseSkinItemsAsync(rows.Where(r => IsAutoSkinItem(r.TemplateId)).ToList(), ct));
            replies.AddRange(await AutoUseQunaItemsAsync(rows.Where(r => QunaItemCurrency(r.TemplateId) is not null).ToList(), ct));
            foreach (var pets in rows.Where(r => ItemRules.AutoUseEffect(r.TemplateId) == "GetVehicle").GroupBy(r => r.TemplateId))
                replies.AddRange(await AutoUsePetItemsAsync(store, character, pets.Select(r => storedItems.GetValueOrDefault(WireKey(r))).OfType<ItemState>().ToList(), ct));
            foreach (var row in rows)
            {
                var effect = ItemRules.AutoUseEffect(row.TemplateId)!;
                if (effect is "GetVehicle" or "GetSkin" or "GetSkinSet" or "GetGameCash" or "GetGamePendingCash") continue;
                // The rows reload after every use: the stack as it is now.
                if (storedItems.GetValueOrDefault(WireKey(row)) is { Location: "bag" } current)
                    replies.AddRange(await AutoUseStackAsync(store, character, current, effect, ct));
            }
            return replies;
        }
        finally { autoUsing = false; }
    }

    /// <summary>An item's cooldown and cast time: the boxes.json summary, else the item XML (Cooltime, CoolTimeGroup, CastingTime).</summary>
    static ItemCooldown ItemCooldownOf(int itemId) =>
        Boxes.Cooldown(itemId) is var known && (known.Group != "None" || known.CastMs > 0 || known.CooldownMs > 0) ? known
        : ItemDB.Embedded.Of(itemId) is { } item ? new ItemCooldown(item.Cooltime, item.CooltimeGroup ?? "None", item.CastingTime) : new ItemCooldown(0, "None", 0);

    /// <summary>A finished cast: a wing item registers its wing; a return scroll is used up, 0x5619, then the map's home point.</summary>
    async Task<List<byte[]>> CompleteItemCastAsync(long now, CancellationToken ct)
    {
        if (itemCast is not { } cast || now < cast.DueMs) return [];
        itemCast = null;
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(cast.Key, out var row) || resources is null || dead) return [];
        if (ItemDB.Embedded.Of(row.TemplateId)?.Use is { Effect: "GetWing", Values: [var wingText, ..] } && int.TryParse(wingText, out var wingId))
        {
            var learned = await Stored(async () => await store.UseWingItemAsync(character, row.InstanceId, row.Revision, wingId, ct) ? [[]] : []);
            if (learned.Count == 0) return [RidePackets.ItemUseInterrupted(playerKey, cast.Key, cast.ItemId, 0)];
            registeredWings.Add(wingId);
            var wingStats = RefreshStats();   // its owned effect: flight time (FPMax) and the other stats
            var finished = RidePackets.ItemUseCompleted(playerKey, cast.Key, cast.ItemId);
            worldPlayer?.Map?.Broadcast(worldPlayer, [finished]);
            return [finished, RidePackets.ItemUsed(cast.Key, cast.ItemId), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse), GamePackets.WingList(OwnedWings()), .. wingStats];
        }
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return [RidePackets.ItemUseInterrupted(playerKey, cast.Key, cast.ItemId, 0)];
        var completed = RidePackets.ItemUseCompleted(playerKey, cast.Key, cast.ItemId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [completed]);
        var home = data.MapPoints(worldMap).Where(p => p.Key.Contains("home", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault()
            ?? data.MapStart(worldMap)?.Position ?? position ?? GamePackets.SpawnPosition;
        return [completed, RidePackets.ItemUsed(cast.Key, cast.ItemId), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. Teleport(worldMap, home, now)];
    }

    // ---------- Rides ----------

    /// <summary>0x9007 CharRideAcquire_RQ {i32 ride}: a ride with enough soul points (VehicleList NeedSoul) is acquired.</summary>
    async Task<List<byte[]>> AcquireRideAsync(int rideId, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || Boxes.Ride(rideId) is not { } info || info.NeedSoul < 1
            || OwnsRide(rideId) || !CanPay(rideId, info.NeedSoul))
            return [RidePackets.AcquireReply(playerKey, 1)];
        var soulsBefore = rides.TryGetValue(rideId, out var entry) ? entry.SoulPoints : 0;
        RideRecord? ride = null;
        await Stored(async () => (ride = await store.AcquireRideWithSoulsAsync(character, rideId, info.NeedSoul, info.Crystal, ct)) is null ? [] : [[]]);
        if (ride is null) return [RidePackets.AcquireReply(playerKey, 1)];
        var paid = Paid(rideId, soulsBefore, info.NeedSoul);
        Remember(ride);
        var (extra, board) = await RideBoardAsync(info, 1, ct);
        return [RidePackets.AcquireReply(playerKey, 0), .. paid, RidePackets.Acquired(playerKey, rideId, RideLevel(rideId, ride.Level), (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0),
            .. extra, .. RefreshStats()];
    }

    /// <summary>0x9013 RideLevelUp_RQ {i32 ride}: an acquired ride levels up for VehicleLevel NeedSoulCount soul points.</summary>
    async Task<List<byte[]>> LevelUpRideAsync(int rideId, CancellationToken ct)
    {
        var info = Boxes.Ride(rideId);
        rides.TryGetValue(rideId, out var entry);
        List<byte[]> Fail() => [RidePackets.LevelUpReply(1, rideId, entry?.Level ?? 0, (byte)(info?.Type ?? 0))];
        var cost = entry is null ? 0 : Boxes.RideLevelCost(entry.Level);
        if (Store is not { } store || CharacterId is not { } character || info is null || entry is not { Owned: true } || cost < 1 || !CanPay(rideId, cost))
            return Fail();
        RideRecord? ride = null;
        await Stored(async () => (ride = await store.LevelUpRideAsync(character, rideId, cost, info.Crystal, Boxes.RideMaxLevel, ct)) is null ? [] : [[]]);
        if (ride is null) return Fail();
        var paid = Paid(rideId, entry.SoulPoints, cost);
        Remember(ride);
        var (extra, board) = await RideBoardAsync(info, ride.Level, ct);
        return [RidePackets.LevelUpReply(0, rideId, ride.Level, (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0), .. paid, .. extra, .. RefreshStats()];
    }

    /// <summary>0x9001 CharGetOnRide_RQ {i32 ride} (0: the default ride): an owned ride is mounted and shown to others.</summary>
    List<byte[]> GetOnRide(int requested)
    {
        var ride = requested != 0 ? requested : defaultRide != 0 ? defaultRide : Rides.FirstOrDefault();
        if (resources is null || dead || !OwnsRide(ride))
            return [RidePackets.GetOnReply(playerKey, dead ? EResult.CharGetOnRide_Fail_Dead : !OwnsRide(ride) ? EResult.CharGetOnRide_Fail_NotExist : EResult.CharGetOnRide_Fail, requested)];
        mountedRide = ride;
        worldPlayer?.Map?.Broadcast(worldPlayer, [RidePackets.GetOnNotice(playerKey, ride, sprinting: sprinting)]);
        return [.. InterruptItemCast(), RidePackets.GetOnReply(playerKey, EResult.CharGetOnRide_Success, ride, sprinting: sprinting)];
    }

    /// <summary>0x9004 CharGetOffRide_RQ {bool by swap}.</summary>
    List<byte[]> GetOffRide(bool bySwap)
    {
        // OBSERVED (official): CharGetOffRide_Success 15160 (383b), reason 1.
        if (mountedRide is null) return [RidePackets.GetOffReply(playerKey, EResult.CharGetOffRide_Fail)];
        mountedRide = null;
        worldPlayer?.Map?.Broadcast(worldPlayer, [RidePackets.GetOffNotice(playerKey, RidePackets.ByClient, bySwap)]);
        return [RidePackets.GetOffReply(playerKey, EResult.CharGetOffRide_Success)];
    }

    /// <summary>The server takes the player off its ride (a cast, death): 0x9006 to the player and those nearby.</summary>
    List<byte[]> ForceDismount()
    {
        if (mountedRide is null) return [];
        mountedRide = null;
        var off = RidePackets.GetOffNotice(playerKey, RidePackets.ByServer);
        worldPlayer?.Map?.Broadcast(worldPlayer, [off]);
        return [off];
    }

    /// <summary>0x900a CharRideDefaultSet_RQ {i32 ride}: stored, answered 0x900b.</summary>
    async Task<List<byte[]>> SetDefaultRideAsync(int ride, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || ride != 0 && !OwnsRide(ride))
            return [RidePackets.DefaultSetReply(playerKey, 1, ride)];
        var done = await Stored(async () => await store.SetDefaultRideAsync(character, ride, ct) ? [[]] : []);
        if (done.Count == 0) return [RidePackets.DefaultSetReply(playerKey, 1, ride)];
        defaultRide = ride;
        return [RidePackets.DefaultSetReply(playerKey, 0, ride)];
    }

    /// <summary>What a player who starts seeing this one also needs: the ride it sits on.</summary>
    public IEnumerable<byte[]> AppearExtras() => mountedRide is { } ride ? [RidePackets.GetOnNotice(playerKey, ride)] : [];

    // EIncreaseReason / EDecreaseReason (usmap).
    const byte IncreaseItemUse = 9, IncreaseOpenRewardBox = 31, DecreaseItemUse = 14, DecreaseOpenRewardBox = 37;
}
