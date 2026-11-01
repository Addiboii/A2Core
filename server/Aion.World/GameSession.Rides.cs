using Aion.Application.Gameplay;
using Aion.Core.Characters;
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
    long mountSummonUntilMs;
    bool rideContentsUnlocked;
    bool? subzoneAllowsRide;
    /// <summary>EContentsUnlockType::Ride (16): the client blocks CharGetOnRide locally until this is unlocked.</summary>
    public const byte RideContentsUnlock = 16;
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
        foreach (var r in stored.Rides) rides[r.RideId] = new RideEntry(r.RideId, r.Level, r.SoulPoints, r.Owned);
        defaultRide = stored.DefaultRide;
        crystals.Clear();
        foreach (var (type, amount) in stored.Crystals ?? new Dictionary<int, int>()) crystals[type] = amount;
        LoadBoards(stored.Boards);
        mountedRide = null;
        rideContentsUnlocked = false;
        subzoneAllowsRide = null;
    }

    bool OwnsAnyRide() => rides.Values.Any(r => r.Owned);

    /// <summary>Include Ride (16) in 0xe257 / 0xe258 when the player owns a mount or finished the Ride quest gate.</summary>
    public IEnumerable<byte> ExtraContentsUnlocks(IEnumerable<byte> questUnlocks)
    {
        if (rideContentsUnlocked || !OwnsAnyRide()) return [];
        if (questUnlocks.Contains(RideContentsUnlock)) { rideContentsUnlocked = true; return []; }
        return [RideContentsUnlock];
    }

    List<byte[]> UnlockRideContentsIfNeeded()
    {
        if (rideContentsUnlocked || !OwnsAnyRide()) return [];
        rideContentsUnlocked = true;
        return [GamePackets.ContentsUnlockUpdate([RideContentsUnlock])];
    }

    async Task PersistDefaultRideIfUnsetAsync(int rideId, CancellationToken ct)
    {
        if (defaultRide != 0 || rideId == 0 || Store is not { } store || CharacterId is not { } character || !OwnsRide(rideId)) return;
        if (await store.SetDefaultRideAsync(character, rideId, ct)) defaultRide = rideId;
    }

    bool SubzoneAllowsRideNow()
    {
        if (MapId is not { } map || position is not { } at) return true;
        return Subzones.AllowsRide(map, at);
    }

    List<byte[]> DismountIfSubzoneForbidden()
    {
        var allowed = SubzoneAllowsRideNow();
        if (subzoneAllowsRide == allowed) return [];
        subzoneAllowsRide = allowed;
        if (allowed || mountedRide is null) return [];
        mountedRide = null;
        var off = RidePackets.GetOffNotice(playerKey, RidePackets.BySubzone);
        worldPlayer?.Map?.Broadcast(worldPlayer, [off]);
        return [off];
    }

    void Remember(RideRecord r) => rides[r.RideId] = new RideEntry(r.RideId, r.Level, r.SoulPoints, r.Owned);

    byte[] RideInit()
    {
        var owned = rides.Values.Where(r => r.Owned).OrderBy(r => r.RideId).ToList();
        var initDefault = defaultRide;
        if (initDefault == 0 || !owned.Any(r => r.RideId == initDefault))
            initDefault = owned.FirstOrDefault()?.RideId ?? 0;
        return RidePackets.RideInitData(initDefault, owned, crystals, boards.Count > 0 ? boards.Values.ToList() : null);
    }

    /// <summary>
    /// 0x9002/0x9003 move fields while mounting: _move_state is EMoveStateModelType, _move_substate is EMoveStateType.
    /// Stop (0) on the model reads as on-foot idle to the client; use Run while seated even when standing still.
    /// </summary>
    (byte Model, byte Substate) MountMoveAckFields()
    {
        if (moving)
            return (RidePackets.MoveModelRun, sprinting ? RidePackets.MoveTypeSprint : RidePackets.MoveTypeRun);
        return (RidePackets.MoveModelRun, RidePackets.MoveTypeStop);
    }

    List<byte[]> MountAck(int ride)
    {
        mountedRide = ride;
        mountSummonUntilMs = Environment.TickCount64 + 5000;
        var (moveState, moveSubstate) = MountMoveAckFields();
        var notice = RidePackets.GetOnNotice(playerKey, ride, moveState, moveSubstate, isSprinting: sprinting && moving);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        worldPlayer?.Map?.UpdateVisibility(worldPlayer, refresh: true);
        return
        [
            .. InterruptItemCast(),
            RidePackets.GetOnReply(playerKey, 0, ride, moveState, moveSubstate, isSprinting: sprinting && moving),
            notice,
            .. RefreshStats(),
        ];
    }

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
        List<byte[]> Fail() => [RidePackets.OpenBoxReply(1, uid, itemId, select, count, [])];
        if (Store is not { } store || CharacterId is not { } character || row is null || row.Location != "bag"
            || Boxes.Use(row.TemplateId) is not { Effect: "RewardBox", Values: [var group, ..] })
            return Fail();
        count = (int)Math.Min(Math.Clamp(count, 1, Math.Max(1, Boxes.MaxOpen(group))), row.Quantity);
        if (count < 1 || Boxes.Roll(group, select, count, rng) is not { } rewards) return Fail();
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, count, rewards, ct) ? [[]] : []);
        if (done.Count == 0) return Fail();
        var replies = new List<byte[]> { RidePackets.OpenBoxReply(0, uid, itemId, select, count, rewards) };
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseOpenRewardBox, DecreaseOpenRewardBox));
        replies.AddRange(await AutoUseAcquiredItemsAsync(rewards.Select(r => r.Item1), now, ct));
        return replies;
    }

    /// <summary>Item.dat AutoUseWhenAcquire effects the server applies (GetTitle 532560065, GetWing, AscensionGrade 533900001, ...).</summary>
    static bool AutoUsed(ItemUse use) => use is { AutoUse: true, Effect: "GetVehicle" or "GetVehicleSoul" or "GetVehicleSoulCrystal" or "GetWing" or "GetTitle"
        or "GetSkin" or "GetSkinSet" or "GetSocialAction" or "AscensionGrade" } or { Effect: "GetDaevanionPoint" };

    /// <summary>Pet souls and Daevanion crystals apply on acquire (Item.dat AutoUseWhenAcquire; morph crystals drip points).</summary>
    async Task<List<byte[]>> AutoUseAcquiredItemsAsync(IEnumerable<int> templateIds, long now, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        foreach (var tid in templateIds.Distinct())
        {
            if (Boxes.Use(tid) is not { } use) continue;
            if (!AutoUsed(use)) continue;
            foreach (var key in storedItems.Where(s => s.Value.TemplateId == tid && s.Value.Location == "bag").Select(s => s.Key).ToList())
                replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        }
        return replies;
    }

    /// <summary>
    /// A GetWing item [Wing.dat id] (Item.dat, e.g. 532400002 Wing_D_Default_01_b, AutoUseWhenAcquire): used up, the wing registered
    /// and stored, the new WingList (0x5672). Without it the HQ2101050/HQ1101020 reward wing stayed in the bag unusable.
    /// </summary>
    async Task<List<byte[]>?> UseWingItemAsync(INativeCharacterStore store, Guid character, ItemState row, int wing, bool reply, CancellationToken ct)
    {
        if (registeredWings.Contains(wing)) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        await Stored(async () => await store.GrantWingAsync(character, wing, ct) ? [[]] : []);
        registeredWings.Add(wing);
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.Add(GamePackets.WingList(OwnedWings()));
        return replies;
    }

    /// <summary>
    /// An AscensionGrade item ["AscensionGrade_2"] (533900001 Ascension_Grade2_A_c_01_b, AutoUseWhenAcquire): used up; ascends when the grade is
    /// higher. Already ascended (the quest turn-in ascends first) it is only used up, so it no longer sits in the bag unusable.
    /// </summary>
    async Task<List<byte[]>?> UseAscensionItemAsync(INativeCharacterStore store, Guid character, ItemState row, int grade, bool reply, CancellationToken ct)
    {
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        if ((selected?.Ascension ?? 1) < grade && await store.AscendAsync(character, grade, ct)) replies.AddRange(Ascend(grade));
        return replies;
    }

    /// <summary>Auto-use items acquired before their effect was handled (a GetWing reward left in the bag), at world entry.</summary>
    async Task<List<byte[]>> AutoUsePendingItemsAsync(long now, CancellationToken ct) =>
        await AutoUseAcquiredItemsAsync(storedItems.Values.Where(s => s.Location == "bag" && Boxes.Use(s.TemplateId) is { } pending && pending.Effect != "GetDaevanionPoint" && AutoUsed(pending))
            .Select(s => s.TemplateId).ToList(), now, ct);

    // ---------- Item use ----------

    /// <summary>
    /// 0x5612 ItemUse_RQ {i64 uid}. Refused (result 1) for an unknown or cooling item, or an effect
    /// that is not implemented; always answers 0x5613 (never leaves the request unanswered).
    /// </summary>
    async Task<List<byte[]>> UseItemAsync(long uid, long now, CancellationToken ct, bool reply = true)
    {
        storedItems.TryGetValue(uid, out var row);
        var itemId = row?.TemplateId ?? 0;
        var use = row is null ? null : Boxes.Use(row.TemplateId);
        if (use is { Effect: "RewardBox" }) return await OpenBoxAsync(uid, 0, 1, now, ct);
        List<byte[]> Fail() => reply ? [RidePackets.ItemUseReply(1, uid, itemId)] : [];
        if (Store is not { } store || CharacterId is not { } character || row is null || row.Location != "bag" || resources is null || dead)
            return Fail();
        var questAcquire = await TryQuestAcquireFromItemAsync(store, character, row, use, now, reply, ct);
        if (questAcquire.Count > 0) return questAcquire;
        if (use is null)
        {
            LogItemUseRefused(itemId, null, "missing_use_data");
            return Fail();
        }
        var cooldown = Boxes.Cooldown(row.TemplateId);
        if (cooldown.Group != "None" && now < itemCooldowns.GetValueOrDefault(cooldown.Group)) return Fail();
        var replies = use.Effect switch
        {
            "GetVehicle" => await UsePetItemAsync(store, character, row, use, reply, ct),
            "GetVehicleSoul" => await UseSoulItemAsync(store, character, row, use, reply, ct),
            "GetVehicleSoulCrystal" => await UseCrystalItemAsync(store, character, row, use, reply, ct),
            "Fix_VehicleCreatureLevel" => await UseBoardLevelItemAsync(store, character, row, use, reply, ct),
            "Skill" => await UseSkillItemAsync(store, character, row, use, reply, now, ct),
            "Telpo_Home" when itemCast is null => StartItemCast(row, cooldown, reply, now),
            "GetKina" or "GetNonBindingKina" when use.Values is [var amount, ..] && long.TryParse(amount, out var kina) && kina > 0 =>
                await UseKinaItemAsync(store, character, row, kina, reply, ct),
            "GetTitle" when use.Values is [var titleText, ..] && int.TryParse(titleText, out var titleId) => await UseTitleItemAsync(store, character, row, titleId, reply, ct),
            "GetSkin" or "GetSkinSet" when use.Values is [{ Length: > 0 }, ..] => await UseSkinItemAsync(store, character, row, use, reply, ct),
            "GetSocialAction" when use.Values is [var emoteText, ..] && int.TryParse(emoteText, out var emote) => await UseSocialActionItemAsync(store, character, row, emote, reply, ct),
            "GetDaevanionPoint" => await UseDaevanionPointItemAsync(store, character, row, use, reply, ct),
            "GetWing" when use.Values is [var wingText, ..] && int.TryParse(wingText, out var wing) && wing > 0 => await UseWingItemAsync(store, character, row, wing, reply, ct),
            "AscensionGrade" when use.Values is [var gradeText, ..] && int.TryParse(gradeText.Split('_')[^1], out var grade) && grade is >= 2 and <= 6 =>
                await UseAscensionItemAsync(store, character, row, grade, reply, ct),
            "GetQuest" => null,
            _ when AgitDecoTables.Embedded.TryDecoId(row.TemplateId, out var decoId) => await UseAgitDecoItemAsync(store, character, row, decoId, reply, ct),
            _ => null,
        };
        if (replies is null)
        {
            LogItemUseRefused(itemId, use.Effect, Boxes.ImplementsUseEffect(use.Effect) ? "handler_failed" : "unimplemented_use_effect");
            return Fail();
        }
        if (cooldown.Group != "None")
        {
            itemCooldowns[cooldown.Group] = now + cooldown.CooldownMs;
            MarkItemCooldownDirty();
        }
        replies.AddRange(AchieveItemUse(itemId, use.Effect));
        LogItemUse(itemId, use.Effect);
        return replies;
    }

    List<byte[]> Used(ItemState row, bool reply) =>
        reply ? [RidePackets.ItemUseReply(0, WireKey(row), row.TemplateId), RidePackets.ItemUsed(WireKey(row), row.TemplateId)] : [RidePackets.ItemUsed(WireKey(row), row.TemplateId)];

    async Task<List<byte[]>?> UseAgitDecoItemAsync(INativeCharacterStore store, Guid character, ItemState row, int decoId, bool reply, CancellationToken ct)
    {
        if (guild is null) return null;
        var outcome = "none";
        await Stored(async () => { outcome = await store.GrantGuildAgitDecoAsync(character, row.InstanceId, row.Revision, decoId, ct); return []; });
        if (outcome != "ok") return null;
        await ReloadGuildAsync(ct);
        return [.. Used(row, reply), .. await InventoryChangesAsync(ct, 0, 0)];
    }

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
            replies.Add(RidePackets.Acquired(playerKey, rideId, ride.Level, (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0));
            replies.AddRange(extra);
            await PersistDefaultRideIfUnsetAsync(rideId, ct);
        }
        replies.AddRange(UnlockRideContentsIfNeeded());
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse));
        if (!owned)
        {
            replies.AddRange(RefreshStats());
            replies.Add(RideInit());
        }
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
    async Task<List<byte[]>?> UseKinaItemAsync(INativeCharacterStore store, Guid character, ItemState row, long kina, bool reply, CancellationToken ct)
    {
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, [(KinaItemId, kina)], ct) ? [[]] : []);
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
        var abnormal = effects.Any(e => e.Type == "Abnormal_True");
        if (!ItemSkillSupported(effects) || (hp == 0 && mp == 0 && fp == 0 && !abnormal && !effects.Any(e => e.Type is "Dispel_True" or "NoEffect")))
            return null;
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        actionUid = actionUid % 255 + 1;
        var replies = new List<byte[]>();
        if (reply) replies.Add(RidePackets.ItemUseReply(0, WireKey(row), row.TemplateId));
        replies.Add(GamePackets.SkillStart(skillId, actionUid, worldPlayer?.Heading ?? 0, position ?? Vector3d.Zero, playerKey, actorKey: playerKey));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        replies.Add(RidePackets.ItemUsed(WireKey(row), row.TemplateId));
        r.Hp = Math.Min(r.HpMax, r.Hp + hp);
        r.Mp = Math.Min(r.MpMax, r.Mp + mp);
        if (r.FpMax > 0) r.Fp = Math.Min(r.FpMax, r.Fp + fp);
        if (hp > 0 || mp > 0) replies.Add(r.Update());
        if (fp > 0 && r.FpMax > 0) replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Fp, r.Fp)]));
        if (effects.Any(e => e.Type == "Dispel_True")) replies.AddRange(DispelDebuffs(now));
        if (abnormal) replies.AddRange(ApplyItemAbnormals(skillId, effects, now));
        return replies;
    }

    /// <summary>A cast item (return scroll): 0x5617 now, completed on the tick after the cast time unless the player moves.</summary>
    List<byte[]>? StartItemCast(ItemState row, ItemCooldown cooldown, bool reply, long now)
    {
        var key = WireKey(row);
        itemCast = (key, row.TemplateId, now + Math.Max(0, cooldown.CastMs));
        castFrom = position;
        var started = RidePackets.ItemUseStarted(playerKey, key, row.TemplateId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [started]);
        return reply ? [RidePackets.ItemUseReply(0, key, row.TemplateId), started] : [started];
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

    /// <summary>A finished cast: the scroll is used up, 0x5619, then Telpo_Home - bound Kibelisk if any, else race home map.</summary>
    async Task<List<byte[]>> CompleteItemCastAsync(long now, CancellationToken ct)
    {
        if (itemCast is not { } cast || now < cast.DueMs) return [];
        itemCast = null;
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(cast.Key, out var row) || resources is null || dead) return [];
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return [RidePackets.ItemUseInterrupted(playerKey, cast.Key, cast.ItemId, 0)];
        var completed = RidePackets.ItemUseCompleted(playerKey, cast.Key, cast.ItemId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [completed]);
        var (targetMap, home) = TelpoHomeDestination();
        return [completed, RidePackets.ItemUsed(cast.Key, cast.ItemId), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. Teleport(targetMap, home, now)];
    }

    /// <summary>Return scroll destination: latest engraved TeleportArtifact (bind point), else race starter home.</summary>
    (int MapId, Vector3d Position) TelpoHomeDestination()
    {
        for (var i = engraved.Count - 1; i >= 0; i--)
        {
            if (Kibelisks.TryPlacement(engraved[i], out var mapId, out var at))
                return (mapId, at);
        }
        var raceHome = HomeMap;
        var point = data.MapPoints(raceHome).Where(p => p.Key.Contains("home", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault()
            ?? data.MapStart(raceHome)?.Position ?? GamePackets.SpawnPosition;
        return (raceHome, point);
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
        await PersistDefaultRideIfUnsetAsync(rideId, ct);
        return [RidePackets.AcquireReply(playerKey, 0), .. paid, RidePackets.Acquired(playerKey, rideId, ride.Level, (byte)info.Type, board?.Level ?? 1, board?.Exp ?? 0),
            .. extra, .. RefreshStats(), .. UnlockRideContentsIfNeeded()];
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
        if (resources is null || dead || !SubzoneAllowsRideNow())
            return [RidePackets.GetOnReply(playerKey, 1, requested)];
        var ride = requested != 0 ? requested : defaultRide != 0 ? defaultRide : Rides.FirstOrDefault();
        if (!OwnsRide(ride)) return [RidePackets.GetOnReply(playerKey, 1, requested)];
        // Live client retries 0x9001 while WaitGetOnComplete; always re-ack (never toggle dismount on repeat).
        return MountAck(ride);
    }

    /// <summary>0x9004 CharGetOffRide_RQ {bool by swap}.</summary>
    List<byte[]> GetOffRide(bool bySwap)
    {
        if (mountedRide is null) return [RidePackets.GetOffReply(playerKey, 1)];
        mountedRide = null;
        mountSummonUntilMs = 0;
        var off = RidePackets.GetOffNotice(playerKey, RidePackets.ByClient, bySwap);
        worldPlayer?.Map?.Broadcast(worldPlayer, [off]);
        worldPlayer?.Map?.UpdateVisibility(worldPlayer, refresh: true);
        return [RidePackets.GetOffReply(playerKey, 0), off, .. RefreshStats()];
    }

    /// <summary>The server takes the player off its ride (a cast, death): 0x9006 to the player and those nearby.</summary>
    List<byte[]> ForceDismount()
    {
        if (mountedRide is null) return [];
        mountedRide = null;
        mountSummonUntilMs = 0;
        var off = RidePackets.GetOffNotice(playerKey, RidePackets.ByServer);
        worldPlayer?.Map?.Broadcast(worldPlayer, [off]);
        worldPlayer?.Map?.UpdateVisibility(worldPlayer, refresh: true);
        return [off, .. RefreshStats()];
    }

    /// <summary>0x900a CharRideDefaultSet_RQ {i32 ride}: stored, answered 0x900b.</summary>
    async Task<List<byte[]>> SetDefaultRideAsync(int ride, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || ride != 0 && !OwnsRide(ride))
            return [RidePackets.DefaultSetReply(playerKey, 1, ride)];
        var done = await Stored(async () => await store.SetDefaultRideAsync(character, ride, ct) ? [[]] : []);
        if (done.Count == 0) return [RidePackets.DefaultSetReply(playerKey, 1, ride)];
        defaultRide = ride;
        return [RidePackets.DefaultSetReply(playerKey, 0, ride), RidePackets.DefaultSetNotice(playerKey, 0, ride)];
    }

    /// <summary>What a player who starts seeing this one also needs: the ride it sits on.</summary>
    public IEnumerable<byte[]> AppearExtras()
    {
        if (mountedRide is not { } ride) return [];
        var (moveState, moveSubstate) = MountMoveAckFields();
        return [RidePackets.GetOnNotice(playerKey, ride, moveState, moveSubstate, isSprinting: sprinting && moving)];
    }

    // EIncreaseReason / EDecreaseReason (usmap).
    const byte IncreaseItemUse = 9, IncreaseOpenRewardBox = 31, DecreaseItemUse = 14, DecreaseOpenRewardBox = 37;
}
