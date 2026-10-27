using System.Collections.Concurrent;
using System.Security.Cryptography;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed record GameSessionOptions
{
    /// <summary>Level of development characters; a selected stored character uses its own.</summary>
    public int CharacterLevel { get; init; } = 1;
    /// <summary>The login port's '$' placeholder row, when the development launch seeds one.</summary>
    public LoginCharacterRow? IntroCharacter { get; init; }
    public int WorldId { get; init; } = 1204;
    /// <summary>Stored accounts: the account's staff role, if any; a character carries it only when flagged staff.</summary>
    public Func<Guid, CancellationToken, Task<string?>>? StaffRole { get; init; }
    /// <summary>Builder (GM) commands from the console (0x362e); null leaves them disabled.</summary>
    public BuilderCommands? Builder { get; init; }
    /// <summary>Extracted ground grids by map (NPCs walk on the landscape); null: slope heights.</summary>
    public TerrainStore? Terrain { get; init; }
    /// <summary>Extracted navigation meshes by map (NPC paths and floors); null: straight lines.</summary>
    public NavMeshStore? Nav { get; init; }
    /// <summary>
    /// The world shared with the host's other connections (multiplayer): unique player keys, shared
    /// NPCs, players seeing each other. Null: a private world per connection (player key 1).
    /// </summary>
    public SharedWorld? World { get; init; }
    /// <summary>Development logins: the map characters enter (at its start point) instead of the intro map (load tests on a big map).</summary>
    public int? DevelopmentMap { get; init; }
    /// <summary>The training dummy (key 1000) next to the spawn point: off since 2026-10-18 (it was the first combat target; the checks still use it).</summary>
    public bool TrainingDummy { get; init; }
}

/// <summary>
/// One game connection after the handshake: decrypted client bodies in, plaintext server bodies
/// out, plus timer ticks. Port of tools/game_server.py game_followup_replies and its helpers
/// (casts, combat, NPC AI, checkpoints). With a store the account's stored characters are used
/// (authenticated launch); without one the development flow creates an unsaved character.
/// </summary>
public sealed partial class GameSession(GameData data, GameSessionOptions options, Random rng)
{
    public const long CombatTimeoutMs = 3500;   // official 0x8d21 leave ~3.3-3.6 s after the last action
    public const long CheckpointMs = 30000;
    public const double AreaRange = 400;        // "within 4m" of the area skills' text
    /// <summary>Monster respawn after the kill (OURS; the official delay is unknown) and corpse removal (OBSERVED ~4050 ms after 0x8d04).</summary>
    public const long RespawnMs = 30_000, CorpseMs = 4_000;
    /// <summary>Official SP (stamina): sprint drains 500 every 100 ms; regen per second 20000 out of combat, 5000 in combat, none while sprinting.</summary>
    public const int SprintDrain = 500, SprintTickMs = 100, SpRegenOutOfCombat = 20000, SpRegenInCombat = 5000;

    /// <summary>
    /// The Diving Gauge: OPMax and OPRegen (per second) are the official stat sheet's; the drain while diving is 200 every 100 ms
    /// (OBSERVED in the 2026-10-19 recording: 99800 -> 55400 in 22 s, then +20000 a second back to 100000): 50 s of breath.
    /// At 0 the player takes the official Oxygen Deficiency (abnormal 3000: Dot_TargetMaxHP every 1000 ms, 500/10000 = 5% of max HP).
    /// </summary>
    public const int OpMax = 100000, OpRegen = 20000, DiveDrain = 2000, SuffocationRatio = 500;
    /// <summary>
    /// The flight gauge (FP), OBSERVED in the 2026-10-19 recording: a fresh ascension's maximum 250,000; flight drains 1000 every
    /// 100 ms (1500 when the flight started running, 750 while gliding); on the ground it refills 4000 a second (2000 in combat,
    /// the stat sheet's RestFPRegen/BattleFPRegen); at 0 the server starts the fall itself (0x371e to the flyer).
    /// </summary>
    public const int FpMax = 250_000, FlyDrain = 10_000, FlySprintDrain = 15_000, GlideDrain = 7_500, FpRegenRest = 4_000, FpRegenBattle = 2_000;
    /// <summary>Dodges: CombatDodge (learned) and NormalDodge, its variant (Skill.dat: SP 10000, same 500 ms group cooldown).</summary>
    public const int CombatDodge = 11000100, NormalDodge = 11000101, NormalDodgeSp = 10000;
    /// <summary>
    /// The dodge's abnormal (OBSERVED at every official dodge and sprint dash 2026-10-19): id 200 for 300 ms on the dodger, removed
    /// with reason 7 about 200 ms later (0x382c), together with the skill's end (0x3806).
    /// </summary>
    public const int DodgeAbnormal = 200, DodgeAbnormalMs = 300, DodgeAbnormalOffMs = 200;
    /// <summary>
    /// The standing dodge's effect (OBSERVED 50-60 ms after all 36 official CombatDodge starts, never after the sprint's NormalDodge):
    /// 0x3804 SkillEffect, abnormal 3015 for 3000 ms carrying the skill (0x382a; 0x382b update when it is still on; 0x382c reason 1 at
    /// its end), then 0x8d00 with the SP. Live 2026-10-20 the client stayed in the dodge without it.
    /// </summary>
    public const int DodgeEffectMs = 50, DodgeAfterAbnormal = 3015, DodgeAfterMs = 3000, DodgeEffectOffset = 13;
    /// <summary>
    /// A dodge's 0x3802 (OBSERVED, all official dodges): the target is the dodger and the "location" a short direction vector, 100 cm
    /// along the movement for the sprint dash (3 samples; the standing dodge's vector was 94-107 cm with a z part). The client's
    /// request carries its target NPC and world position instead; echoing those froze the client after the dash (live 2026-10-20).
    /// </summary>
    public const int DashLength = 100;
    /// <summary>
    /// The dodge's end relative to the client's own dash movement (0x370e): OBSERVED the official 0x3806 reached the client 186 ms
    /// after it sent the move start (its dash lasts about 250 ms and it resumes running right after the end). Live 2026-10-20 our
    /// client started its move only about 100 ms after the skill start arrived, so an end timed from the start came ~110 ms into
    /// the dash and the client never left it (frozen, the sprint still draining). The end now waits for the move start too.
    /// </summary>
    public const int DodgeEndAfterMoveMs = 175;
    public const byte DodgeAbnormalOffReason = 7;

    sealed record Choice(string Name, int Gender, int CreationId, byte[] CustomizingData, int VoiceType);

    sealed class Resources(int key)
    {
        public int Hp = GamePackets.SpawnHp, HpMax = GamePackets.SpawnHpMax, Mp = MpMaxBase, MpMax = MpMaxBase;
        public int Sp = GamePackets.SpawnSp, SpMax = GamePackets.SpawnSp;
        /// <summary>The Diving Gauge (OP): official stat sheet OPMax 100000, OPRegen 20000 (0x3649).</summary>
        public int Op = OpMax;
        /// <summary>The player's bars as the official server sends them (0x8d00; never 0x364c).</summary>
        public byte[] Update() => GamePackets.CharPoints(key, [(CharPoint.Mp, Mp), (CharPoint.Sp, Sp)], [(CharPoint.Hp, Hp)]);
        public byte[] SpUpdate() => GamePackets.CharPoints(key, [(CharPoint.Sp, Sp)]);
        public byte[] OpUpdate() => GamePackets.CharPoints(key, [(CharPoint.Op, Op)]);
        public byte[] FpUpdate() => GamePackets.CharPoints(key, [(CharPoint.Fp, Fp)]);
        /// <summary>The world-entry maxima, as the official burst before 0x3633 (OP max 100000 observed).</summary>
        public int Fp, FpMax;
        public byte[] Maxima() => GamePackets.CharPoints(key, [(CharPoint.Mp, Mp), (CharPoint.MaxMp, MpMax), (CharPoint.MaxSp, SpMax), (CharPoint.MaxOp, OpMax), (CharPoint.Fp, Fp), (CharPoint.MaxFp, FpMax)],
            [(CharPoint.Hp, Hp), (CharPoint.MaxHp, HpMax)]);
    }

    NpcAi ai = new(rng, data);
    Task<Terrain?>? terrainLoad;
    Task<NavMesh?>? navLoad;
    bool invisible;
    bool reloading;   // a teleport's map load (0x3621 in the world) is pending
    bool sprinting;
    /// <summary>Sitting (CharRestStart_RQ 0x3747 until CharRestStop_RQ 0x374a, a move or combat); recovery is <see cref="RestRegenFactor"/> times faster.</summary>
    public bool Resting { get; private set; }
    public const int RestRegenFactor = 3;
    /// <summary>Where the player is from the movement requests: flying (0x370a/b), gliding (0x3706/7), in a jump or fall (0x3702/3), else on the ground.</summary>
    /// <summary>WindPath: riding a wind stream (0x3712/0x3713), which spends no flight points (OURS, as the earlier game's
    /// streams; no official FP during a ride is recorded) and recovers none.</summary>
    enum AirState { Ground, Fly, Glide, Fall, WindPath }
    AirState air;
    /// <summary>The flight started running (0x370a mask bit 0): the faster drain.</summary>
    bool flySprint;
    /// <summary>Under water: a dive request (0x3716/0x3717) or a swim below the surface; a land move ends it.</summary>
    bool diving;
    /// <summary>The water surface of the current swim: the highest swim position since the player entered the water.</summary>
    double? surfaceZ;
    /// <summary>
    /// OURS: how far below the surface a swimmer counts as diving. Live 2026-10-19 the client reported the lake cave's
    /// underwater swim with the plain swim requests (0x3714 at z 1874.95 sinking, under a surface of 2687.5; no dive request
    /// in the trace), and a surface swim holds z exactly.
    /// </summary>
    public const double DiveDepth = 100;
    int wingId;
    /// <summary>Registered wings: stored ones (loaded at selection) plus AcquireWingAll.</summary>
    readonly SortedSet<int> registeredWings = [];
    public IReadOnlyCollection<int> RegisteredWings => registeredWings;

    List<(int Id, byte Enchant)> OwnedWings() =>
        registeredWings.Concat(wingId != 0 ? [wingId] : []).Distinct().Order().Select(id => (id, (byte)0)).ToList();

    /// <summary>Builder AcquireWingAll: every wing of the character's race, sent as the new WingList (0x5672).</summary>
    public List<byte[]> AcquireAllWings()
    {
        if (resources is null) return [];
        registeredWings.UnionWith(GamePackets.Wings(profileRace));
        return [GamePackets.WingList(OwnedWings())];
    }
    int profileRace = 1;
    long sprintTickAt;
    /// <summary>The dodge in progress: its abnormal's uid (0: none), the skill and action uid of the 0x3806 (End: sent), and when they end.</summary>
    (int Uid, int Skill, int Action, long At, bool End)? dodgeAbnormalOff;
    /// <summary>
    /// Which reply the sprint's dash gets (builder DashVariant, for live tests without a deploy each; the trace shows the shape):
    /// 0 the pre-recording shape (0x3801 with mask, the full 0x8d00, 0x3802: this client ends the dash by itself but drops the sprint
    /// 12-75 ms after its start with 0x3705, every time in 119 live sprints), 1 the recorded shape without extras (0x3802, the 9-byte
    /// 0x3801, 0x8d00 with the SP), 2 = 1 + abnormal 200 and its 0x382c, 3 = 1 + the 0x3806 end, 4 all of it as recorded (the end
    /// 200 ms after the start; with this the client kept the sprint but never left the dash, live 2026-10-20), 5 = 4 with the end
    /// 600 ms after the start, past the client's own dash.
    /// 6 answers the sprint and refuses the dash (9-byte 0x3801 result 1, no 0x3802). 7 holds the sprint reply and sends it in the
    /// same write as the recorded dash (official: one batch). 8 is 7, held until <see cref="DashBatchDelayMs"/> after the sprint
    /// request (official answers 45-85 ms later, one tick). 9 is 7 but starts CombatDodge instead of NormalDodge.
    /// Variants 1-5 sent the sprint reply on its own, a frame before the dash, and the client never left the dash.
    /// 10 answers the sprint and sends nothing for the dash request.
    /// A standing dodge (no sprint) on 6-10 keeps variant 0: that is the reply this client already finishes.
    /// </summary>
    public int DashVariant { get; set; }
    public const int DashVariantMax = 10, DodgeLateEndMs = 600, DashBatchDelayMs = 50, SprintReplyHoldMs = 80;
    /// <summary>Sprint-on reply (and, for variant 8, the dash) waiting to share one write. Official sends them at one timestamp.</summary>
    List<byte[]>? pendingSprintReply, pendingDashBatch;
    long sprintOnAt;
    /// <summary>The standing dodge's pending effect: the start's action uid and when it is due.</summary>
    (int Action, long At)? dodgeEffectAt;
    /// <summary>Seconds spent gliding, flying or swimming since the last whole minute counted (ElapsedTime achievements).</summary>
    readonly Dictionary<string, long> elapsedStateSeconds = new();
    int level = data.ClampLevel(options.CharacterLevel);
    (string AccountSid, string Guid) loginIdentity = ("0", "0");
    Choice? character;
    CharacterState? selected;
    (int MapId, Vector3d Position)? spawn;
    HashSet<int> learned = [];
    string? className;
    Dictionary<int, Target>? targets;
    int? equippedWeapon;
    Resources? resources;
    long resourceTickMs;
    bool inCombat;
    long combatUntil;
    int worldMap;
    (bool Hp, bool Mp, bool Sp) invincible;
    bool noCool;
    bool unlockUpdateSent, lobbyLoginSent;
    Vector3d? position, savedPosition;
    long checkpointAt;
    readonly Dictionary<int, long> cooldowns = [];
    Dictionary<int, long> respawnAt = [];
    int actionUid;
    /// <summary>This player's entity key: 1 in a private world, unique per host in the shared one.</summary>
    readonly int playerKey = options.World?.AllocateKey() ?? 1;
    WorldPlayer? worldPlayer;
    public int PlayerKey => playerKey;
    public WorldPlayer? WorldPlayer => worldPlayer;

    readonly ConcurrentQueue<(Func<long, Task<List<byte[]>>> Run, Action Cancel)> posted = new();

    public INativeCharacterStore? Store { get; set; }
    /// <summary>The character in the world (after 0x3622), else null.</summary>
    public string? CharacterName { get; private set; }
    public List<string> PersistenceErrors { get; } = [];
    public List<int> SelectKeys { get; } = [];
    public int Checkpoints { get; private set; }
    public IReadOnlyDictionary<int, Target> Targets => targets ?? [];
    public Vector3d? Position => position;
    public int Level => level;
    public string? ClassName => className;
    public int? EquippedWeapon => equippedWeapon;
    /// <summary>The account's staff role read at game login ("admin", "gamemaster"), else null.</summary>
    string? accountRole;
    /// <summary>Whether any of the account's characters is flagged staff (the Login_RS "is admin" flag).</summary>
    bool lobbyStaff;
    /// <summary>
    /// The selected character's staff role ("admin", "gamemaster"), else null: the account's role when the character is
    /// flagged staff (CharacterState.Staff, set by hand; a new character is a player even on a staff account).
    /// </summary>
    public string? StaffRole { get; private set; }
    /// <summary>Whether the character in play is staff (name tag, builder commands, the default wing, entry overrides).</summary>
    public bool IsAdmin => StaffRole is not null;
    /// <summary>Whether the account holds a staff role (staff-style names at creation and rename).</summary>
    public bool StaffAccount => accountRole is not null;
    /// <summary>After world entry (0x3622), until the connection ends.</summary>
    public bool InWorld => resources is not null;
    public int? MapId => InWorld ? worldMap : null;
    /// <summary>The selected stored character, when the account is authenticated.</summary>
    public Guid? CharacterId => selected?.Summary.CharacterId;

    static ushort Opcode(byte[] body) => body.Length >= 2 ? (ushort)(body[0] | body[1] << 8) : (ushort)0;

    public async Task<List<byte[]>> HandleAsync(byte[] plain, long now, CancellationToken ct = default)
    {
        lastNow = now;
        if (plain.Length < 2) return [];
        var header = Opcode(plain);
        if (plain.Length == 2 && header is 0x383a or 0x383c)
        {
            // Every cast is bracketed by 0x383a/0x383c (u16 result replies). The player enters
            // combat once and leaves ~3.5 s after the last action, not on 0x383c.
            var replies = new List<byte[]> { GamePackets.RequestAck(header) };
            if (header == 0x383a)
            {
                if (!inCombat)
                {
                    inCombat = true;
                    replies.Add(GamePackets.CombatState(playerKey, true));
                    replies.AddRange(MapEventCombatStarted(now));
                }
                combatUntil = now + CombatTimeoutMs;
            }
            return replies;
        }
        if (header == 0x3602 && plain.Length >= 10) return [GamePackets.Ping(BitConverter.ToUInt64(plain, 2), now)];
        switch (header)
        {
            case 0x3613: return await GameLoginAsync(plain, now, ct);
            case 0x3619:
            {
                var name = NameRequest(plain);
                if (name.Length == 0) return [];
                // The client's EResult codes (only 0 has been observed; the codes are the usmap enum's): a staff-style
                // name is a banned word for players, a name in use a duplicate.
                var result = CharacterNames.IsStaffStyle(name) && !StaffAccount ? GamePackets.NameBannedWord : GamePackets.ResultOk;
                if (result == GamePackets.ResultOk && Store is { } names && (await Stored(async () => await names.NameTakenAsync(name, ct) ? [[]] : [])).Count > 0)
                    result = GamePackets.NameDuplicate;
                return [GamePackets.NameCheck(name, result)];
            }
            case 0x361b: return await CreateAsync(plain, now, ct);
            case 0x361f: return await SelectAsync(plain, now, ct);
            case 0x3622: return LoadComplete(plain, now);
            case 0x3630:
                // Logout_RQ (in-game Exit). Save first: the client disconnects on the reply.
                await CheckpointAsync(now, force: true, ct);
                return [GamePackets.Logout()];
            case 0x362e: return await BuilderAsync(plain, now, ct);
            case 0x3625 when plain.Length == 2: return await LobbyAsync(now, ct);
            case CharDeleteRq when plain.Length >= 3: return await DeleteCharacterAsync(plain, now, ct);
            case CharDeleteCancelRq when plain.Length >= 3: return await CancelDeleteAsync(plain, ct);
            case CharRenameRq when plain.Length >= 4: return await RenameCharacterAsync(plain, ct);
        }
        if (InWorld && ClientRequest(plain, now) is { } clientReplies) return clientReplies;
        if (InWorld && await GuildRequestAsync(plain, ct) is { } guildReplies) return guildReplies;
        if (await SocialRequestAsync(plain, now, ct) is { } socialReplies) return socialReplies;
        if (await EquipRequestAsync(plain, ct) is { } equipped) return equipped;
        if (PositionReport(plain) is { } reported)
        {
            // Too fast: rejected (the client goes back), not stored, not shown to others.
            if (CheckMovement(plain, reported, now) is { } rejected) return rejected;
            position = reported;
            // The dodge's end waits for the client's dash movement (see DodgeEndAfterMoveMs).
            if (Opcode(plain) == 0x370e && dodgeAbnormalOff is { } dodging) dodgeAbnormalOff = dodging with { At = Math.Max(dodging.At, now + DodgeEndAfterMoveMs) };
            // A stop (0x3718) keeps the state: a diver who stops is still under water.
            switch (Opcode(plain))
            {
                case 0x3714 or 0x3715:
                    surfaceZ = Math.Max(surfaceZ ?? reported.Z, reported.Z);
                    diving = reported.Z < surfaceZ - DiveDepth;
                    break;
                case 0x3716 or 0x3717: diving = true; break;
                case 0x3718: break;
                default: diving = false; surfaceZ = null; break;
            }
            var uncovered = await UncoverFogAsync(reported, now, ct);
            if (itemCast is not null && castFrom is { } from && from.DistanceSquared(reported) > CastMoveTolerance * CastMoveTolerance)
                uncovered = [.. InterruptItemCast(), .. uncovered];
            uncovered = [.. uncovered, .. NpcVisibility(reported)];
            TrackAir(plain);
            var entering = MapEventPosition(reported, now);
            entering.AddRange(InstanceTrigger(reported, now));
            if (worldPlayer?.Map is { } map)
            {
                worldPlayer.Position = reported;
                map.UpdateVisibility(worldPlayer);
                map.Broadcast(worldPlayer, MovementRelay(plain, reported));
            }
            return [.. uncovered, .. entering];
        }
        switch (header)
        {
            case 0x3800:
            {
                var cast = CastReplies(plain, now);
                if (worldPlayer?.Map is { } castMap) castMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                cast.AddRange(await QuestKillsAsync(ct));
                cast.AddRange(await KillExperienceAsync(ct));
                return cast;
            }
            case 0x5610: return await InventoryListAsync(ct);
            // WingChange_RQ: packed is_wing_skin, i32 wing (live 2026-10-15, 7 bytes). A registered
            // wing becomes the equipped one for this connection (the equipped wing is not stored yet).
            case 0x5674 when plain.Length == 7 && resources is not null:
            {
                var skin = (plain[2] & 1) != 0;
                var wanted = BitConverter.ToInt32(plain, 3);
                if (!OwnedWings().Any(w => w.Id == wanted)) return [GamePackets.WingChangeReply(wanted, skin, 1)];
                if (!skin) wingId = wanted;
                return [GamePackets.WingChangeReply(wanted, skin), GamePackets.WingChangeNotice(playerKey, wanted, skin)];
            }
            // Soul binding before equipping: start (0x562b uid, cost uid, cost id), cancel and
            // complete (0x562d / 0x562f uid). Only items the client was sent can be bound.
            case 0x562b when plain.Length == 22 && inventory.ContainsKey(BitConverter.ToInt64(plain, 2)):
                return [GamePackets.SoulBindStart(BitConverter.ToInt64(plain, 2), BitConverter.ToInt64(plain, 10), BitConverter.ToInt32(plain, 18))];
            case 0x562d when plain.Length == 10: return [GamePackets.SoulBindCancel(BitConverter.ToInt64(plain, 2))];
            case LootPackets.LootRq when plain.Length >= 4: return await LootAsync(plain, now, ct);
            case ItemMoveRq when plain.Length == 19: return await MoveItemInBagAsync(plain, ct);
            case ShopOpenRq when plain.Length >= 3: return await OpenShopAsync(plain, now, ct);
            case ItemRestoreRq when plain.Length >= 3: return await RestoreItemsAsync(plain, ct);
            case ItemEnchantRq when plain.Length >= 10: return await EnchantAsync(plain, ct);
            case GatherStartRq when plain.Length >= 3: return await GatherStartAsync(plain, now, ct);
            case TitleChangeRq when plain.Length >= 6: return await TitleChangeAsync(plain, ct);
            case SkillLevelUpRq when plain.Length >= 6: return await SkillLevelUpAsync(plain, ct);
            case TitleEquipRq when plain.Length >= 7: return await TitleEquipAsync(plain, ct);
            case SkinEquipRq when plain.Length >= 3: return await SkinEquipAsync(plain, ct);
            case GatherStopRq when plain.Length >= 3: return GatherStop(plain);
            case CraftStartRq when plain.Length >= 7: return await CraftAsync(plain, ct);
            case GatherStopRq when plain.Length >= 3: pendingUse = null; return [];
            case ShopBuyRq when plain.Length >= 4: return await BuyGoodsAsync(plain, now, ct);
            case ShopSellRq when plain.Length >= 4: return await SellItemsAsync(plain, ct);
            case ItemRemoveRq when plain.Length >= 4: return await RemoveItemsAsync(plain, ct);
            case ItemSortRq when plain.Length == 7: return await SortBagAsync(plain, ct);
            case QuickSlotSetRq when plain.Length >= 27: return await SetQuickSlotAsync(plain, ct);
            case QuickSlotResetRq when plain.Length == 3: return await ResetQuickSlotAsync(plain[2], ct);
            case RidePackets.OpenBoxRq when plain.Length == 15: return await OpenBoxRequestAsync(plain, now, ct);
            case RidePackets.ItemUseRq when plain.Length == 10: return await UseItemAsync(BitConverter.ToInt64(plain, 2), now, ct);
            case RidePackets.AcquireRq when plain.Length == 6: return await AcquireRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.CrystallizeRq when plain.Length >= 3: return await CrystallizeAsync(plain, ct);
            case RidePackets.BoardRollRq when plain.Length >= 5: return await RollBoardAsync(plain, ct);
            case RidePackets.BoardLockRq when plain.Length == 6: return await LockBoardSlotAsync(plain[2], plain[3], plain[4], plain[5] != 0, ct);
            case RidePackets.RespawnRq when plain.Length == 11: return await RespawnRequestAsync(plain[2], BitConverter.ToInt64(plain, 3), now, ct);
            case RidePackets.UseRebirthRq when plain.Length == 3: return UseRebirth(plain[2] != 0, now);
            case RidePackets.LevelUpRq when plain.Length == 6: return await LevelUpRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.GetOnRq when plain.Length == 6: return GetOnRide(BitConverter.ToInt32(plain, 2));
            case RidePackets.GetOffRq when plain.Length == 3: return GetOffRide(plain[2] != 0);
            case RidePackets.DefaultSetRq when plain.Length == 6: return await SetDefaultRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case 0x562f when plain.Length == 10 && inventory.ContainsKey(BitConverter.ToInt64(plain, 2)):
                return await SoulBindCompleteAsync(BitConverter.ToInt64(plain, 2), ct);
            case 0x3704 when plain.Length == 10: return SprintOn(now);
            // Glide (0x3706) and flight (0x370a) starts, sent once a wing is equipped (live 2026-10-15).
            case 0x3706 when resources is not null && wingId != 0: return [GamePackets.GlideStartReply(playerKey)];
            case 0x370a when resources is not null && wingId != 0: return [GamePackets.FlyStartReply(playerKey)];
            case 0x3705 when plain.Length == 10: return SprintOff();
            // CharRestStart_RQ / CharRestStop_RQ {key varint, 0 OBSERVED "473700"}: sit down / stand up (dev 0x36A4-0x36A9,
            // usmap layouts; no official reply sample, CANDIDATE). Others see the NT.
            // Per-character settings (GameSession.Settings.cs).
            case SealStoneActivateRq or SealStoneDeactivateRq when resources is not null: return await SealStoneToggleAsync(Opcode(plain) == SealStoneActivateRq, ct);
            case SealStoneRegisterRq when plain.Length >= 10 && resources is not null: return await RegisterSealStoneAsync(BitConverter.ToInt64(plain, 2), ct);
            case PotionAutoUseRq when plain.Length >= 11 && resources is not null: return await SetPotionAutoUseAsync(plain, ct);
            case ItemAutoUseRq when plain.Length >= 7 && resources is not null: return await SetItemAutoUseAsync(plain, ct);
            case PresetSetRq when plain.Length >= 5 && resources is not null: return await PresetSetAsync(plain[2], plain[3], plain[4] != 0, ct);
            case PresetListSetRq when plain.Length >= 3 && resources is not null: return await PresetListSetAsync(plain, ct);
            case 0x3747 or 0x374a when plain.Length >= 3 && resources is not null:
            {
                var start = Opcode(plain) == 0x3747;
                if (start && dead) return [GamePackets.RestReply(true, playerKey, 1)];
                Resting = start;
                worldPlayer?.Map?.Broadcast(worldPlayer, [GamePackets.RestNotice(start, playerKey)]);
                return [GamePackets.RestReply(start, playerKey)];
            }
            case 0x3833 when plain.Length >= 4 && plain[2] == 0:
                // SetMainTarget_RQ: 00 + target varint; answered 0x3834 (official).
                try
                {
                    var target = (int)new PacketReader(plain) { Offset = 3 }.Varint();
                    selectedTarget = target;
                    return targets?.ContainsKey(target) == true ? [GamePackets.SetMainTargetReply(target), GamePackets.TargetUpdate(target)] : [GamePackets.SetMainTargetReply(target)];
                }
                catch (ProtocolException) { return []; }
            // NPC interaction (dialog) and quest requests; layouts from the usmap, never yet seen from the client.
            case 0xe240 when QuestKey(plain, 2) is { } npcKey: return await NpcInteractionAsync(npcKey, now, ct);
            // InteractEnvObj_RQ {target_key, item_key, target_id} (usmap; client encoding unseen, logged).
            case 0x3636: return await InteractEnvObjAsync(plain, now, ct);
            case CubeRewardRq: return await CubeRewardAsync(plain, now, ct);
            case PostLoadRq when plain.Length >= 3: return await LoadPostsAsync(plain, ct);
            case PostReadRq: return await MarkPostsAsync(plain, delete: false, ct);
            case PostDeleteRq: return await MarkPostsAsync(plain, delete: true, ct);
            case PostTakeRq when plain.Length >= 11: return await TakePostAsync(plain, ct);
            case ItemDecomposeRq when plain.Length >= 4: return await DecomposeAsync(plain, ct);
            case TakeAchievementRewardRq: return await TakeAchievementRewardAsync(plain, ct);
            case TakeAchievementGroupRewardRq: return await TakeGroupRewardAsync(plain, ct);
            case TakeAchievementRewardBatchRq: return await TakeRewardBatchAsync(plain, ct);
            case AttendanceRq: return Attendance(now);
            case TakeBattlePassRewardRq: return TakeBattlePassReward(plain, now);
            case MatchingStartRq: return MatchingStart(plain, now);
            case MatchingStartResponseRq: return MatchingStartResponse(plain, now);
            case MatchingCancelRq: return MatchingCancel(now);
            case ArenaDungeonReadyRq: return ArenaReady(plain, now);
            case ArenaRecordRq when plain.Length >= 4: return [PacketWriter.Opcode(ArenaRecordRs).U16(0).I32(plain[2]).U8(plain[3]).Varint(0).ToArray()];
            case PeriodCollectionListRq: return PeriodCollectionListRequest(plain, now);
            case RegisterPeriodCollectionRq when plain.Length >= 11: return await RegisterCollectionAsync(plain, now, ct);
            case BossChallengeEnterRq: return BossChallengeEnter(plain, now);
            case BattlePassPurchaseRq when plain.Length >= 6: return [PacketWriter.Opcode(BattlePassPurchaseRs).U16(1).I32(BitConverter.ToInt32(plain, 2)).ToArray()];
            case CloseNpcAchievementRq: return CloseNpcAchievement(plain);
            case CloseEnvObjAchievementRq: return CloseEnvObjAchievement(plain);
            case SkillLevelDownRq: return await SkillLevelDownAsync(plain, ct);
            case SkillLevelResetRq: return await SkillLevelResetAsync(plain, ct);
            // InteractEnvObjStop_RQ {key}: a timed interaction cancelled (moved, or the client's cancel).
            case 0x363d when QuestKey(plain, 2) is { } stopKey:
                if (pendingUse?.Key == stopKey) pendingUse = null;
                return [GamePackets.InteractEnvObjStopReply(stopKey)];
            case 0xe242 when QuestKey(plain, 2) is { } npcKey:
                // Once the conversation is over on the server's side (the partner removed and a map load pending, as after an
                // NPC's layer entry) the official answer is the failure 0x3e08 with key 0 (OBSERVED #9656); the talk mode ends on it.
                if (reloading || targets?.ContainsKey(npcKey) != true) return [GamePackets.FinishNpcInteractionReply(0, FinishNoConversation)];
                return [GamePackets.FinishNpcInteractionReply(npcKey), .. MapEventTalked(npcKey, null, close: true, now)];
            // NpcConversation_RQ {npc key, func value}: the dialog choice of an EnterInstanceLayer NPC (OBSERVED reply 0x4007 before the move).
            case 0x4006 when plain.Length >= 7: return NpcConversation(plain, now);
            case 0xe244 when QuestKey(plain, 2) is { } npcKey: return [GamePackets.KeepAliveNpcInteractionReply(npcKey, now + 300_000)];
            // Unrecorded client layouts (the recordings hold the server's side only; live 2026-10-18 NpcConversation_RQ carried a
            // byte before its key): the quest id or uid is the first i32 in the body that names one, wherever it sits.
            case 0x8d0a when plain.Length >= 6:
                return await QuestAcceptAsync(FirstInt32(plain, id => questLog?.Book.ById.ContainsKey(id) == true) ?? BitConverter.ToInt32(plain, 2), plain.Length >= 10 ? BitConverter.ToInt32(plain, 6) : 0, ct);
            case 0x8d0c when plain.Length >= 6:
                return await QuestGiveUpAsync(FirstInt32(plain, uid => questLog?.ByUid(uid) is not null) ?? BitConverter.ToInt32(plain, 2), ct);
            // MapExit_RQ 0x4004 (the leave button of a layer or dungeon) and ExitInstanceLayer_RQ 0x930d: bodies unrecorded, ignored;
            // the reply is {u16 result} (CANDIDATE) then the way out as the script's MapExit sends it.
            case 0x4004: return instanceKey != 0 && !reloading ? [PacketWriter.Opcode(0x4005).U16(0).ToArray(), .. LeaveInstance(now)] : [PacketWriter.Opcode(0x4005).U16(1).ToArray()];
            case 0x930d: return instanceKey != 0 && !reloading ? [PacketWriter.Opcode(0x930e).U16(0).ToArray(), .. LeaveInstance(now)] : [PacketWriter.Opcode(0x930e).U16(1).ToArray()];
            case 0x8d0e when plain.Length >= 6: return await QuestRewardAsync(BitConverter.ToInt32(plain, 2), ct);
            case 0x8d2b when plain.Length >= 6: return await QuestStepTalkAsync(plain, now, ct);
            // ClientObjectiveComplete_RQ {kind u8, id i32, objective index i32} (11 bytes live; the official reply mirrors it).
            case 0x9230 when plain.Length >= 11:
                return await ClientObjectiveAsync(plain[2], BitConverter.ToInt32(plain, 3), BitConverter.ToInt32(plain, 7), now, ct);
            // CutsceneComplete_RQ {cutscene name, group request key i64}: a cutscene the client was told to play ended (OBSERVED
            // live 2026-10-19 for the lake cave's exit cutscene). It finishes CompleteCutscene objectives and releases a move
            // that waited for its cutscene (a PCTeleport's value 3).
            case 0xe334 when CutsceneComplete(plain) is { } ended: return MapEventCutsceneDone(ended.Name, ended.Key, now);
            case 0x8d40: return [GamePackets.ClientOptionReply()];
            // UpdateClientEnvSetting_RQ (dev 0x397D): the RS and the settings echoed as the NT (CANDIDATE; not yet seen from our client).
            case 0x8d42 when plain.Length > 2: return [GamePackets.ClientEnvSettingReply(), [0x44, 0x8d, .. plain.AsSpan(2)]];
            // MoveToLoginServer_RQ (the character screen's back button; live 2026-10-19 unanswered, the client hung):
            // MoveToLoginServer_RS {result} (usmap; u16 on the wire as every observed result), then the client goes to the
            // login server on its own.
            case 0x3617: return [PacketWriter.Opcode(0x3618).U16(0).ToArray()];
            case 0x3648: { var (sheet, hpMax) = StatSheet(); return [GamePackets.CharAllStats(sheet, hpMax)]; }
            case 0x3659:
                // Comm key request (only when Login_RS gave none), then the chat unlock update.
                return [GamePackets.CommServerKeyReply(), .. UnlockUpdateOnce()];
            case 0xe34f when Store is null && !lobbyLoginSent:
                // Login_RS resent from the lobby: its handler needs [A+0x428], absent when the
                // instant game-login reply arrives, so the chat server key (Lime+0x80) stayed
                // empty. Resending in the world dropped the connection (research/chat-and-gm.md).
                lobbyLoginSent = true;
                return [GamePackets.LoginCharacters([], $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", "1204",
                    GamePackets.CommServerKey, now, isBornServer: true, chatEnableLevel: 0)];
        }
        if (GamePackets.WorldEntryQueryReplies.TryGetValue(header, out var reply))
            return header == 0x8a1c ? [reply, .. UnlockUpdateOnce()] : [reply];
        return [];
    }

    /// <summary>
    /// 0x3625 Lobby_RQ (back to character selection): save, leave the world, answer 0x3626 with
    /// the account's characters as stored now (class, level and ascension changes show).
    /// Development logins store nothing and get an empty list (the client offers creation).
    /// </summary>
    async Task<List<byte[]>> LobbyAsync(long now, CancellationToken ct)
    {
        await CheckpointAsync(now, force: true, ct);
        LeaveWorld();
        if (Store is null) return [GamePackets.LobbyCharacters([])];
        // The plain (uncompressed) full list. Settled live 2026-10-19 from the client recordings of the four research
        // variants: after the plain full list the player picked a character 6 s later; the compressed one made the client
        // drop the connection and log in again; the empty ones showed no characters.
        return await Stored(async () => [GamePackets.LobbyCharacters(await LobbyCharactersAsync(ct))]);
    }

    /// <summary>World state ends with the character; the next selection starts it again.</summary>
    void LeaveWorld()
    {
        ExitSharedWorld();
        resources = null;
        targets = null;
        selected = null;
        StaffRole = null;
        spawn = null;
        position = savedPosition = null;
        CharacterName = null;
        inCombat = false;
        cooldowns.Clear();
        respawnAt = [];
        ai = new NpcAi(rng, data);
        invisible = false;
        invincible = default;
        noCool = false;
        sprinting = false;
        Resting = false;
        unlockUpdateSent = false;  // the next world entry re-locks chat in 0xe257 and needs 0xe258 again
        reloading = false;
    }

    // ---------- Quests ----------

    /// <summary>Kina is an item (Item.dat Gold).</summary>
    public const int KinaItemId = 930100001;
    QuestLog? questLog;
    readonly List<int> killedNpcs = [];
    int selectedTarget;
    long nextDevQuestUid = 1;

    public QuestLog? Quests => questLog;

    /// <summary>A development login (no store) gets an in-memory quest log: quest gates (map events, dungeon entries) can be exercised in checks.</summary>
    public QuestLog DevelopmentQuestLog() => questLog ??= new QuestLog(QuestBook.Embedded, profileRace == 0 ? 1 : profileRace);

    static QuestWire Wire(QuestRecord q) => new(q.QuestId, (byte)q.Status, q.Step, q.Goals, (int)q.Uid);

    /// <summary>An i32 NPC key at the offset, or a varint when the body is not exactly that long (layout unconfirmed).</summary>
    /// <summary>FinishNpcInteraction_RS result when no conversation is open any more (OBSERVED `083e 00000000` after a layer move).</summary>
    const int FinishNoConversation = 0x3e08;

    static int? QuestKey(byte[] plain, int offset)
    {
        if (plain.Length == offset + 4) return BitConverter.ToInt32(plain, offset);
        try { return plain.Length > offset ? (int)new PacketReader(plain) { Offset = offset }.Varint() : null; }
        catch (ProtocolException) { return null; }
    }

    /// <summary>
    /// The character's quests. A character without any skips the opening quest (its objectives
    /// are the intro map events) and starts on the quest it leads to, as the official new
    /// character entered the world with HQ1101010 active.
    /// </summary>
    async Task LoadQuestsAsync(INativeCharacterStore store, Guid character, int race, CancellationToken ct)
    {
        questLog = new QuestLog(QuestBook.Embedded, race);
        questLog.Load(await store.QuestsAsync(character, ct));
        if (questLog.All.Count > 0) return;
        var opening = race == 2 ? "HQ2100010" : "HQ1100010";
        if (questLog.Book.Find(opening) is not { } intro) return;
        questLog.Set(await store.SaveQuestAsync(character, QuestLog.New(opening, intro) with { Status = QuestLog.Rewarded }, ct));
        if (intro.Next is { } next && questLog.CanAccept(next, level))
            questLog.Set(await store.SaveQuestAsync(character, QuestLog.New(next, questLog.Book.Find(next)!), ct));
    }

    async Task<QuestRecord> SaveQuestAsync(QuestRecord record, CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character) record = await store.SaveQuestAsync(character, record, ct);
        else if (record.Uid == 0) record = record with { Uid = nextDevQuestUid++ };
        questLog?.Set(record);
        return record;
    }

    /// <summary>Contents unlocked by finished quests (ContentsUnlock quest gates), as unlock type bytes.</summary>
    IEnumerable<byte> QuestUnlockTypes() =>
        (questLog?.UnlockedContents() ?? []).Select(name => BuilderCommands.UnlockTypes.ToList().IndexOf(name)).Where(i => i > 0).Select(i => (byte)i);

    /// <summary>Saves and reports changed quests (0x8d10); quests that complete by themselves pay at once.</summary>
    async Task<List<byte[]>> QuestChangesAsync(IEnumerable<QuestRecord> changed, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        foreach (var record in changed)
        {
            // The step's goal reached first (the official pattern), then the saved state (the next step or the completion).
            foreach (var milestone in questLog?.TakeMilestones(record.Code) ?? [])
                if (milestone.Uid != 0) replies.Add(GamePackets.QuestProgress(Wire(milestone)));
            var saved = await SaveQuestAsync(record, ct);
            replies.Add(GamePackets.QuestProgress(Wire(saved)));
            if (saved.Status == QuestLog.Complete && questLog!.Book.Find(saved.Code) is { Complete: "Auto" or "None" })
                replies.AddRange(await QuestRewardAsync((int)saved.Uid, ct));
        }
        replies.AddRange(await AutoAcquireAsync(ct));
        return replies;
    }

    /// <summary>
    /// Quests that start by themselves (Quest.dat AcquireType Auto, 242 of them): taken when their earlier quests are done, their
    /// map (AcquireMapId, 0 = any) is this one and their conditions hold, in rounds (HQ1101021 opens AQ1602000). Then the map's
    /// events whose start now holds join (Dungeon_0005_MapEvent starts on HQ1101021 step 1). Before 2026-10-19 no Auto quest
    /// was ever taken: the chain ended at the Ascension Subspace. Callers run inside Stored.
    /// </summary>
    async Task<List<byte[]>> AutoAcquireAsync(CancellationToken ct)
    {
        if (questLog is null || resources is null) return [];
        var replies = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            // The earlier quests must be turned in, not only complete (the chain's next starts at the reward: HQ1101021 after Ashar's talk, not at the arrival).
            var codes = questLog.AutoAcquirable(level)
                .Where(code => questLog.Book.Find(code) is { } quest && (quest.Map == 0 || quest.Map == worldMap) && QuestConditionsHold(quest)
                               && (quest.Previous ?? []).Concat(quest.Prerequisites ?? []).All(p => !questLog.Book.Quests.ContainsKey(p) || questLog.Get(p) is { Status: QuestLog.Rewarded })).ToList();
            if (codes.Count == 0) break;
            foreach (var code in codes)
                replies.Add(GamePackets.QuestAdd(Wire(await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct))));
        }
        if (RunsMapEvents && !reloading && mapEventsMap == worldMap) replies.AddRange(StartEligibleEvents(lastNow));
        return replies;
    }

    /// <summary>IncompleteQuest (taken, not rewarded), ClearQuestStep (QuestStep id = quest id * 100 + order), SubZone (standing in it); the rest never hold (CloseEnvObj, the masteries, UnifiedObjectiveScore).</summary>
    bool QuestConditionsHold(QuestDefinition quest) => (quest.Conditions ?? []).All(c => c.Type switch
    {
        "IncompleteQuest" => questLog?.Get(c.Value) is { Status: QuestLog.Incomplete or QuestLog.Complete },
        "ClearQuestStep" => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stepId)
            && questLog?.Book.StepOf(stepId) is { } s && questLog.Book.ById.TryGetValue(s.Quest, out var code) && questLog.Get(code) is { } r && (r.Status >= QuestLog.Complete || r.Step > s.Step),
        "SubZone" => position is { } at && Subzones.InSubzone(worldMap, c.Value, at),
        _ => false,
    });

    async Task<List<byte[]>> QuestKillsAsync(CancellationToken ct)
    {
        if (questLog is null || killedNpcs.Count == 0 && clearedEvents.Count == 0) { killedNpcs.Clear(); clearedEvents.Clear(); return []; }
        var changed = new Dictionary<string, QuestRecord>();
        foreach (var npc in killedNpcs)
            foreach (var record in questLog.Killed(npc, level)) changed[record.Code] = record;
        killedNpcs.Clear();
        foreach (var mapEvent in clearedEvents)
            foreach (var record in questLog.ClearedMapEvent(mapEvent, level)) changed[record.Code] = record;
        clearedEvents.Clear();
        return await Stored(() => QuestChangesAsync(changed.Values, ct));
    }

    /// <summary>
    /// Clicking an NPC (0xe240): the dialog opens (0xe241, five minutes). Talking counts for
    /// AskNpc objectives here when the client sends no separate step talk (0x8d2b).
    /// </summary>
    async Task<List<byte[]>> NpcInteractionAsync(int npcKey, long now, CancellationToken ct)
    {
        var replies = new List<byte[]> { GamePackets.StartNpcInteractionReply(npcKey, now + 300_000) };
        if (questLog is null || targets?.GetValueOrDefault(npcKey) is not { } npc) return replies;
        replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Talked(npc.NpcId, level), ct)));
        return replies;
    }

    // ---------- World-map fog (the clouds on the M map) ----------

    /// <summary>Uncovered fog groups (stored; per connection on development logins).</summary>
    readonly SortedSet<int> fog = [];
    long fogCheckAt;
    public IReadOnlyCollection<int> Fog => fog;
    public const long FogCheckMs = 500;

    /// <summary>Entering a subzone uncovers its fog group: stored, then 0x56ad. Checked at most every FogCheckMs.</summary>
    async Task<List<byte[]>> UncoverFogAsync(Vector3d at, long now, CancellationToken ct)
    {
        if (resources is null || now < fogCheckAt) return [];
        fogCheckAt = now + FogCheckMs;
        var replies = ObjectVisibility(at, now);
        replies.AddRange(await AreaQuestsAsync(at, ct));
        if (Subzones.GroupAt(worldMap, at) is { } group && !fog.Contains(group)) replies.AddRange(await OpenFogAsync([group], ct));
        return replies;
    }

    // ---------- Quest objects and trigger areas ----------

    /// <summary>Quest objects this client has been sent (index on the map).</summary>
    readonly HashSet<int> shownObjects = [];
    /// <summary>Trigger actors spawned for this player (index in the map's trigger list).</summary>
    readonly HashSet<int> shownTriggers = [];
    /// <summary>A map event's instance (dungeon, layer): long-range objects and every trigger actor are sent at entry (OBSERVED).</summary>
    bool InstanceMap => instanceKey != 0 && MapEventData.Serves(worldMap);
    /// <summary>
    /// Objects a map event's UseEnvObj objective consumed in this layer: gone for this player (OBSERVED 2026-10-19: each phoenix egg of
    /// HQ002 despawned 100 ms after its use, 0x3635 state 4 reason 3, and never came back; before this they could be used without end).
    /// </summary>
    readonly HashSet<int> usedObjects = [];
    /// <summary>A timed interaction in progress: object index, key, when it completes.</summary>
    (int Index, int Key, long DueMs)? pendingUse;

    /// <summary>Spawns quest objects within WorldObjects.VisibleRange (0x3634) and despawns those left behind (0x3635).</summary>
    List<byte[]> ObjectVisibility(Vector3d at, long now)
    {
        var replies = new List<byte[]>();
        var objects = WorldObjects.ObjectsOn(worldMap);
        // The objects shown (to despawn) and those placed in range (SpatialGrid), in index order.
        var candidates = new SortedSet<int>(shownObjects);
        candidates.UnionWith(SpatialGrid.Objects(worldMap).Near(at.X, at.Y, WorldObjects.VisibleRange));
        var instance = InstanceMap;
        if (instance) candidates.UnionWith(WorldObjects.LongRangeOn(worldMap));
        foreach (var i in candidates)
        {
            if (i >= objects.Count) continue;
            var o = objects[i];
            double dx = o.Position[0] - at.X, dy = o.Position[1] - at.Y;
            var near = (instance && o.LongRange || dx * dx + dy * dy <= WorldObjects.VisibleRange * WorldObjects.VisibleRange) && !MapEventHidden(i, o);
            if (near && !Gathered(i, now) && !usedObjects.Contains(i) && shownObjects.Add(i))
                replies.Add(GamePackets.EnvObjSpawn(WorldObjects.FirstKey + i, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship));
            else if (!near && shownObjects.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
        }
        // Trigger actors: all of an instance map's at once (official), elsewhere within TriggerRange.
        var triggers = WorldObjects.TriggersOn(worldMap);
        for (var i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            if (t.Env == 0) continue;
            double dx = t.Position[0] - at.X, dy = t.Position[1] - at.Y;
            var near = instance || dx * dx + dy * dy <= WorldObjects.TriggerRange * WorldObjects.TriggerRange;
            if (near && shownTriggers.Add(i))
                replies.Add(GamePackets.TriggerSpawn(WorldObjects.FirstTriggerKey + i, t.Env, t.At, t.Direction, now, t.Id));
            else if (!near && shownTriggers.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstTriggerKey + i));
        }
        return replies;
    }

    /// <summary>EnterVolumePC (trigger area, widened by the objective's value) and EnterSubZone objectives of active quests.</summary>
    async Task<List<byte[]>> AreaQuestsAsync(Vector3d at, CancellationToken ct)
    {
        if (questLog is null || !questLog.OpenAreaObjectives().Any()) return [];
        var triggers = WorldObjects.TriggersOn(worldMap);
        bool Inside(QuestObjective o) => o.Target is { } name && (o.Type == "EnterSubZone"
            ? Subzones.InSubzone(worldMap, name, at)
            : triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Contains(at)));
        var changed = questLog.Entered(Inside, level);
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed, ct));
    }

    /// <summary>
    /// Using a quest object (0x3636 with its key): in range, 0x3638 started; after its InteractTime
    /// (EnvObjData; 0 at once) 0x363a completed, 0x8d6d finish state, 0x363b available state, then the 0x3637 reply
    /// (the official order of the player's own interaction, capture 20261013-144836 #10090-10093) and UseEnvObj quest
    /// progress. Without 0x8d6d/0x363b the client kept the interaction (the gather result panel) open.
    /// </summary>
    async Task<List<byte[]>> UseObjectAsync(byte[] plain, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        // Entering a trigger actor's volume (the client reports it): started, completed, finish and available states, the
        // reply (OBSERVED for every subspace trigger). The objectives themselves go by the position reports.
        foreach (var key in EnvObjKeys(plain))
            if (key is { } tk && tk - WorldObjects.FirstTriggerKey is var ti && ti >= 0 && ti < WorldObjects.TriggersOn(worldMap).Count)
                return [GamePackets.InteractEnvObjStarted(tk, playerKey), GamePackets.InteractEnvObjCompleted(tk, playerKey),
                    GamePackets.InteractEnvObjFinishState(tk), GamePackets.InteractEnvObjAvailableState(tk), GamePackets.InteractEnvObjReply(tk)];
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - WorldObjects.FirstKey is var i && i >= 0 && i < objects.Count) { index = i; break; }
        // No such object: refuse it, or the client waits for the reply with the player locked.
        if (index is not { } found || Gathered(found, now) || usedObjects.Contains(found)) return [GamePackets.InteractEnvObjReply(EnvObjKeys(plain).FirstOrDefault(k => k is not null) ?? 0, 1)];
        var o = objects[found];
        var key2 = WorldObjects.FirstKey + found;
        if (o.FlightRing) return FlyThroughRing(key2, now);
        if (position is { } at && Math.Sqrt(Math.Pow(at.X - o.Position[0], 2) + Math.Pow(at.Y - o.Position[1], 2)) > o.InteractRange + WorldObjects.InteractSlack)
            return [GamePackets.InteractEnvObjReply(key2, 1)];
        var replies = new List<byte[]> { GamePackets.InteractEnvObjStarted(key2, playerKey) };
        if (o.InteractTime > 0)
        {
            pendingUse = (found, key2, now + o.InteractTime);
            return replies;
        }
        replies.AddRange(await CompleteUseAsync(found, ct, now));
        return replies;
    }

    /// <summary>
    /// Flying through a flight ring (EnvObjData Usage FlightRing; the client reports it as an interaction): the reply alone, the
    /// two abnormals the official sent (18999040 for 3 s, 18999041 for 0.1 s; not in the extracted tables) and the flight points
    /// refilled (OBSERVED capture 20261019-231752 #37632-#37639: 183000 back to 250000 within 2.6 s; ours at once).
    /// </summary>
    List<byte[]> FlyThroughRing(int key, long now)
    {
        var replies = new List<byte[]> { GamePackets.InteractEnvObjReply(key) };
        var at = position ?? Vector3d.Zero;
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal, 3000, now + 3000, CasterKey: playerKey, RangeBase: at)]));
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal + 1, 100, now + 100, CasterKey: playerKey, RangeBase: at)]));
        if (resources is { FpMax: > 0 } r && r.Fp < r.FpMax)
        {
            r.Fp = r.FpMax;
            replies.Add(r.FpUpdate());
        }
        return replies;
    }

    public const int FlightRingAbnormal = 18999040;

    async Task<List<byte[]>> CompleteUseAsync(int index, CancellationToken ct, long now = 0)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (index >= objects.Count) return [];
        var key = WorldObjects.FirstKey + index;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key),
            GamePackets.InteractEnvObjReply(key),
        };
        if (objects[index].Gather is not null) replies.AddRange(await CompleteGatherAsync(index, objects[index], now, ct));
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(objects[index].Env, level), ct)));
        var consumed = (CopyOwner ?? this).mapEventRuns.Any(r => r.WantsObject(objects[index].Name));
        replies.AddRange(MapEventUsedObject(objects[index].Name, now));
        if (consumed)
        {
            usedObjects.Add(index);
            shownObjects.Remove(index);
            replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        // A party dungeon's portal (EnvObjData Usage PartyDungeon): into the expedition.
        if (objects[index].Portal is { Length: > 0 } portal) replies.AddRange(EnterPartyDungeon(portal, now));
        // The expedition's reward cube (Usage OdEnergyCube): the final boss's reward, once per player per run.
        if (objects[index].Cube) replies.AddRange(await CubeRewardAsync(null, now, ct));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(objects[index].Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(objects[index].Env) == true, now));
        return replies;
    }

    async Task<List<byte[]>> OpenFogAsync(IReadOnlyList<int> groups, CancellationToken ct)
    {
        var added = groups.Where(g => !fog.Contains(g)).Distinct().Order().ToList();
        if (Store is { } store && CharacterId is { } character && added.Count > 0)
        {
            var stored = await Stored(async () =>
            {
                var opened = await store.OpenMapFogAsync(character, added, ct);
                return opened.Select(g => BitConverter.GetBytes(g)).ToList();
            });
            added = stored.Select(b => BitConverter.ToInt32(b)).ToList();
        }
        fog.UnionWith(added);
        return [.. added.Select(GamePackets.AddSubzoneGroup), .. added.Count > 0 ? Achieve("EnterSubzone", added.Count) : []];
    }

    /// <summary>GM RevealMap: every fog group uncovered (stored), sent as the full list.</summary>
    public async Task<List<byte[]>> RevealMapAsync(CancellationToken ct)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        await OpenFogAsync(Subzones.AllGroups, ct);
        return [GamePackets.OpenSubzoneGroups(fog.ToList())];
    }

    // ---------- Kibelisks (teleport artifacts) ----------

    /// <summary>TeleportArtifact ids the character has bound to (stored; per connection on development logins).</summary>
    readonly List<int> engraved = [];
    public IReadOnlyList<int> Engraved => engraved;

    /// <summary>World entry: the engraved list (0x9232) and the map's Kibelisks (0x3634).</summary>
    List<byte[]> KibeliskSpawns(int mapId, long now)
    {
        var replies = new List<byte[]> { GamePackets.EngravedTeleportArtifacts(engraved) };
        var placed = Kibelisks.OnMap(mapId);
        for (var i = 0; i < placed.Count; i++)
        {
            var k = placed[i];
            replies.Add(GamePackets.EnvObjSpawn(Kibelisks.FirstKey + i, k.EnvObj, new Vector3d(k.Position[0], k.Position[1], k.Position[2]), k.Yaw, now, k.UniqueKey));
        }
        return replies;
    }

    /// <summary>
    /// Candidate object keys in an InteractEnvObj_RQ. The live client (2026-10-15) sends a 01 byte, then
    /// the varint key, i64 0 and the EnvObj id; the earlier guesses (key right after the opcode) are kept after it.
    /// </summary>
    static int?[] EnvObjKeys(byte[] plain) =>
        [plain.Length > 3 && plain[2] == 1 ? QuestKey(plain, 3) : null, QuestKey(plain, 2), plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : null];

    /// <summary>The raw body of the last InteractEnvObj_RQ (research log).</summary>
    public byte[]? LastEnvObjRequest { get; private set; }

    /// <summary>
    /// Binding to a Kibelisk (official order): 0x3638 started, 0x363a completed, 0x3637 reply, then
    /// 0x9233 when newly bound and the quest progress (EngraveTeleportArtifact). The object is the
    /// one whose key the request carries (varint or i32 after the opcode).
    /// </summary>
    async Task<List<byte[]>> InteractEnvObjAsync(byte[] plain, long now, CancellationToken ct)
    {
        LastEnvObjRequest = plain;
        var placed = Kibelisks.OnMap(worldMap);
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - Kibelisks.FirstKey is var i && i >= 0 && i < placed.Count) { index = i; break; }
        if (index is not { } found) return await UseObjectAsync(plain, now, ct);
        var kibelisk = placed[found];
        var objectKey = Kibelisks.FirstKey + found;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjStarted(objectKey, playerKey), GamePackets.InteractEnvObjCompleted(objectKey, playerKey), GamePackets.InteractEnvObjReply(objectKey),
        };
        var added = !engraved.Contains(kibelisk.ArtifactId);
        if (added && Store is { } store && CharacterId is { } character)
            added = (await Stored(async () => await store.EngraveTeleportArtifactAsync(character, kibelisk.ArtifactId, ct) ? [Array.Empty<byte>()] : [])).Count == 1;
        if (added)
        {
            engraved.Add(kibelisk.ArtifactId);
            replies.Add(GamePackets.EngravedTeleportArtifactAdd(kibelisk.ArtifactId));
            replies.AddRange(Achieve("EngraveTeleportArtifact", 1, e => e.Has(kibelisk.Artifact)));
        }
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Engraved(kibelisk.Artifact, level), ct)));
        return replies;
    }

    /// <summary>QuestStepNpcConversation_RQ: finds the quest by uid (after an optional kind byte) and counts the talk.</summary>
    /// <summary>The first i32 in the body (any offset after the opcode) that satisfies the test; the canonical offset 2 wins when it does.</summary>
    static int? FirstInt32(byte[] plain, Func<int, bool> fits)
    {
        for (var offset = 2; offset + 4 <= plain.Length; offset++)
            if (BitConverter.ToInt32(plain, offset) is var value && fits(value)) return value;
        return null;
    }

    async Task<List<byte[]>> QuestStepTalkAsync(byte[] plain, long now, CancellationToken ct)
    {
        // Kind 2: a map event's talk objective {kind, map event id, npc key, objective index} (reply OBSERVED: 02 0000 id key index).
        // The request is unrecorded: when the fixed offsets do not name a running event and an NPC here, the first i32s that do are taken.
        if (plain.Length >= 15 && (plain[2] == 2 || plain[3] == 2))
        {
            int eventId = BitConverter.ToInt32(plain, 3), talkKey = BitConverter.ToInt32(plain, 7), index = BitConverter.ToInt32(plain, 11);
            var runs = (CopyOwner ?? this).mapEventRuns;
            if (!runs.Any(r => r.Id == eventId) || targets?.ContainsKey(talkKey) != true)
            {
                eventId = FirstInt32(plain, id => runs.Any(r => r.Id == id)) ?? eventId;
                talkKey = FirstInt32(plain, key => key > 0 && targets?.ContainsKey(key) == true) ?? talkKey;
                index = plain[^4..] is var tail && BitConverter.ToInt32(tail, 0) is >= 0 and < 6 and var last ? last : 0;
            }
            return [GamePackets.QuestStepTalkReply(2, eventId, talkKey, index), .. MapEventTalked(talkKey, index, close: false, now)];
        }
        if (questLog is null) return [];
        foreach (var offset in new[] { 2, 3 })
        {
            if (plain.Length < offset + 8 || questLog.ByUid(BitConverter.ToInt32(plain, offset)) is not { } record) continue;
            var npcKey = BitConverter.ToInt32(plain, offset + 4);
            var npcId = targets?.GetValueOrDefault(npcKey)?.NpcId ?? 0;
            var replies = new List<byte[]> { GamePackets.QuestStepTalkReply(1, (int)record.Uid, npcKey) };
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Talked(npcId, level).Where(r => r.Code == record.Code), ct)));
            return replies;
        }
        return [];
    }

    /// <summary>
    /// The client finished an objective by itself (cutscene talk, note, fake item): kind 1 names a quest uid, kind 2 a
    /// map event. The official reply (0x9231) came before the quest's progress (0x8d10).
    /// </summary>
    async Task<List<byte[]>> ClientObjectiveAsync(byte kind, int id, int index, long now, CancellationToken ct)
    {
        if (kind == 2) return MapEventClientObjective(id, index, now);
        if (questLog?.ClientCompleted(id, index, level) is not { } record) return [GamePackets.ClientObjectiveReply(kind, id, index, 1)];
        var replies = new List<byte[]> { GamePackets.ClientObjectiveReply(kind, id, index) };
        replies.AddRange(await Stored(() => QuestChangesAsync([record], ct)));
        return replies;
    }

    async Task<List<byte[]>> QuestAcceptAsync(int questId, int npcKey, CancellationToken ct)
    {
        if (questLog is null || !questLog.Book.ById.TryGetValue(questId, out var code) || !questLog.CanAccept(code, level))
            return [GamePackets.QuestAcceptReply(questId, 0, 1)];
        return await Stored(async () => [GamePackets.QuestAdd(Wire(await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct)))]);
    }

    async Task<List<byte[]>> QuestGiveUpAsync(int uid, CancellationToken ct)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Incomplete } record) return [GamePackets.QuestGiveUpReply(uid, 1)];
        return await Stored(async () =>
        {
            if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, record.Code, ct);
            questLog.Remove(record.Code);
            return [GamePackets.QuestGiveUpReply(uid), GamePackets.QuestRemove([uid], 5)];
        });
    }

    /// <summary>
    /// Pays a Complete quest (official order): experience (0x8d07), the items and Kina (0x561b,
    /// reason 24), the reply (0x8d0f), contents it unlocks (0xe258), then its follow-up quest is
    /// added (0x8d11). Development logins store nothing and pay nothing.
    /// </summary>
    async Task<List<byte[]>> QuestRewardAsync(int uid, CancellationToken ct)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Complete } record || questLog.Book.Find(record.Code) is not { } quest)
            return [GamePackets.QuestTakeRewardReply(uid, 1)];
        return await Stored(async () =>
        {
            var unlockedBefore = QuestUnlockTypes().ToHashSet();
            var rewards = quest.Rewards ?? new QuestRewards();
            var items = (rewards.Items ?? []).Where(i => i.Length == 2).Select(i => ((int)i[0], i[1])).ToList();
            if (rewards.Kina > 0) items.Insert(0, (KinaItemId, rewards.Kina));
            var replies = new List<byte[]>();
            if (Store is { } store && CharacterId is { } character)
            {
                if (await store.RewardQuestAsync(character, record.Code, rewards.Exp, items, ct) is not long total)
                    return [GamePackets.QuestTakeRewardReply(uid, 1)];
                // The store added the reward to the level's experience; level-ups follow (ExpUpdate, LevelUpdate, ...).
                replies.AddRange(await GainExperienceAsync(total, rewards.Exp, ExpReasonQuest, ct));
                replies.AddRange(await InventoryChangesAsync(ct));
            }
            if (record.Code == AscensionQuest && (selected?.Ascension ?? 1) < 2
                && (Store is not { } ascending || CharacterId is not { } ascended || await ascending.AscendAsync(ascended, 2, ct)))
                replies.AddRange(Ascend(2));
            questLog.Set(record with { Status = QuestLog.Rewarded });
            replies.Add(GamePackets.QuestTakeRewardReply(uid));
            replies.AddRange(AchieveQuest(record.Code, quest.Type));
            var unlocked = QuestUnlockTypes().Where(t => !unlockedBefore.Contains(t)).ToArray();
            if (unlocked.Length > 0) replies.Add(GamePackets.ContentsUnlockUpdate(unlocked));
            if (quest.Next is { } next && questLog.CanAccept(next, level))
                replies.Add(GamePackets.QuestAdd(Wire(await SaveQuestAsync(QuestLog.New(next, questLog.Book.Find(next)!), ct))));
            // Quests waiting on this one (ClearQuest, ClearQuestType objectives) move on.
            replies.AddRange(await QuestChangesAsync(questLog.Recheck(level), ct));
            return replies;
        });
    }

    /// <summary>
    /// The quest whose turn-in ascends the character. OBSERVED in the 2026-10-19 recording at HQ1101020's reward (Ashar): the
    /// flight gauge's maximum (0x8d00 type 13, 250,000), WingChange 0x5676 with the race's default wing, the stats, then the
    /// Subspace's event and quests; the gauge then filled from 40,000 in 40,000 steps (a self skill, 5002). OURS: filled at once,
    /// the wing list resent (0x5672; the official 0x5673/0xe295 notices are not decoded).
    /// </summary>
    public const string AscensionQuest = "HQ1101020";

    List<byte[]> Ascend(int grade)
    {
        if (selected is { } state) selected = state with { Ascension = grade };
        wingId = GamePackets.DefaultWing(profileRace);
        if (resources is null) return [];
        resources.FpMax = resources.Fp = FpMax;
        return [GamePackets.CharPoints(playerKey, [(CharPoint.MaxFp, resources.FpMax), (CharPoint.Fp, resources.Fp)]),
                GamePackets.WingChangeNotice(playerKey, wingId, false), GamePackets.WingList(OwnedWings()), GamePackets.GlideCooltime(playerKey)];
    }

    /// <summary>Builder QuestAdd / QuestComplete / QuestCompleteStep / QuestDelete for a quest code or numeric id.</summary>
    public async Task<(string Message, List<byte[]> Packets)> QuestCommandAsync(string command, string quest, CancellationToken ct)
    {
        if (questLog is null) return ("Quests need a character in the world.", []);
        var code = questLog.Book.Find(quest) is not null ? quest
            : int.TryParse(quest, out var id) && questLog.Book.ById.TryGetValue(id, out var byId) ? byId : null;
        if (code is null) return ($"No quest {quest}.", []);
        var title = questLog.Book.Find(code)!.Title ?? code;
        switch (command)
        {
            case "QuestAdd":
                if (questLog.Get(code) is not null) return ($"{code} is already in the journal.", []);
                var added = await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct);
                return ($"Added {code} \"{title}\".", [GamePackets.QuestAdd(Wire(added))]);
            case "QuestCompleteStep":
                return questLog.CompleteStep(code, level) is { } stepped
                    ? ($"{code}: step done.", await QuestChangesAsync([stepped], ct)) : ($"{code} is not in progress.", []);
            case "QuestComplete":
                return questLog.CompleteAll(code) is { } done
                    ? ($"{code} complete; take the reward at the NPC or with QuestReward.", await QuestChangesAsync([done], ct)) : ($"{code} is not in progress.", []);
            case "QuestReward":
                return questLog.Get(code) is { Status: QuestLog.Complete } complete
                    ? ($"{code} rewarded.", await QuestRewardAsync((int)complete.Uid, ct)) : ($"{code} is not complete.", []);
            case "QuestDelete":
                if (questLog.Get(code) is not { } existing) return ($"{code} is not in the journal.", []);
                if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, code, ct);
                questLog.Remove(code);
                return ($"Removed {code}.", [GamePackets.QuestRemove([(int)existing.Uid], 1)]);
        }
        return ("Unknown quest command.", []);
    }

    // ---------- Sprint (Shift): 0x3704 on / 0x3705 off; SP drains while it lasts ----------

    List<byte[]> SprintOn(long now)
    {
        if (resources is null) return [];
        sprinting = resources.Sp > 0;
        sprintTickAt = now + SprintTickMs;
        sprintOnAt = now;
        worldPlayer?.Map?.Broadcast(worldPlayer, [PlayerPackets.SprintOn(playerKey)]);
        // The client follows the sprint start with its own CombatDodge request (live 2026-10-20, 4 ms later), which CastReplies
        // turns into the running dash (NormalDodge) while the sprint is on, as the official server did.
        // Official answers the sprint and the dodge in one batch (the 2026-10-19 recording: one timestamp, 45-85 ms after the
        // burst). Writing the sprint reply on its own let this client enter the sprint a frame before the dash, and it then
        // never left the dash (live 2026-10-20, variants 1-5). Variants 7-9 hold the reply for that batch.
        List<byte[]> reply = [GamePackets.SprintCooltime(playerKey), GamePackets.SprintOnReply(playerKey)];
        if (DashVariant is 7 or 8 or 9)
        {
            pendingSprintReply = reply;
            pendingDashBatch = null;
            return [];
        }
        return reply;
    }

    /// <summary>
    /// Variant 7 and 9 send the dash in the same write as the held sprint reply. Variant 8 keeps both until the tick
    /// (<see cref="FlushHeldDash"/>), which is when the official server answered.
    /// </summary>
    List<byte[]> DeliverDash(List<byte[]> dash, int variant)
    {
        if (variant == 8)
        {
            pendingDashBatch = dash;
            return [];
        }
        var replies = TakePendingSprint();
        replies.AddRange(dash);
        return replies;
    }

    /// <summary>The held sprint reply, if it has not been sent. Cleared either way.</summary>
    List<byte[]> TakePendingSprint()
    {
        var held = pendingSprintReply;
        pendingSprintReply = null;
        return held ?? [];
    }

    /// <summary>
    /// Sends a sprint reply that no dodge joined (a swim sprint: official answers 0x8d03 + 0x3741 only), and variant 8's
    /// combined batch once the tick delay has passed. The dash packets are broadcast here because the cast handler
    /// already returned nothing.
    /// </summary>
    List<byte[]> FlushHeldDash(long now)
    {
        if (pendingSprintReply is null && pendingDashBatch is null) return [];
        if (now < sprintOnAt + (DashVariant == 8 ? DashBatchDelayMs : SprintReplyHoldMs)) return [];
        var replies = TakePendingSprint();
        if (pendingDashBatch is { } dash)
        {
            pendingDashBatch = null;
            replies.AddRange(dash);
            worldPlayer?.Map?.Broadcast(worldPlayer, replies.Where(SharedCastPacket));
        }
        return replies;
    }

    /// <summary>The dodge abnormal on this player (0x382a), scheduled for its removal with the skill's end.</summary>
    byte[] DodgeAbnormalAdd(long now, int skill, int action, long endAt, bool end = true)
    {
        var uid = ++buffUid;
        dodgeAbnormalOff = (uid, skill, action, endAt, end);
        return PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(uid, DodgeAbnormal, DodgeAbnormalMs, now + DodgeAbnormalMs,
            CasterKey: playerKey, Level: 1, RangeBase: position ?? Vector3d.Zero)]);
    }

    /// <summary>
    /// The standing dodge's effect at +50 ms (0x3804, the 3 s abnormal 3015, the SP), then the dodge's end (0x3806) and its abnormal's
    /// removal (0x382c reason 7) at +200 ms, as recorded.
    /// </summary>
    List<byte[]> DodgeAbnormalTick(long now)
    {
        var replies = new List<byte[]>();
        if (dodgeEffectAt is { } fx && now >= fx.At && resources is not null)
        {
            dodgeEffectAt = null;
            var effect = GamePackets.SkillEffect(playerKey, playerKey, CombatDodge, fx.Action, CombatDodge * 100 + DodgeEffectOffset);
            worldPlayer?.Map?.Broadcast(worldPlayer, [effect]);
            replies.Add(effect);
            AddBuff(DodgeAfterAbnormal, DodgeAfterMs, CombatDodge, now, replies);
            replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources.Sp)]));
        }
        if (dodgeAbnormalOff is { } off && now >= off.At)
        {
            dodgeAbnormalOff = null;
            var ended = new List<byte[]>();
            if (off.End) ended.Add(GamePackets.SkillEnd(playerKey, off.Skill, off.Action));
            if (off.Uid != 0) ended.Add(PlayerPackets.AbnormalRemove(playerKey, [(off.Uid, DodgeAbnormalOffReason)]));
            worldPlayer?.Map?.Broadcast(worldPlayer, ended);
            replies.AddRange(ended);
        }
        return replies;
    }

    List<byte[]> SprintOff()
    {
        sprinting = false;
        // A sprint the client released before the held reply went out still has to be seen, or the off notice has nothing to end.
        var replies = TakePendingSprint();
        if (pendingDashBatch is { } dash)
        {
            pendingDashBatch = null;
            replies.AddRange(dash);
            worldPlayer?.Map?.Broadcast(worldPlayer, replies.Where(SharedCastPacket));
        }
        worldPlayer?.Map?.Broadcast(worldPlayer, [GamePackets.SprintOffNotice(playerKey)]);
        replies.Add(GamePackets.SprintOffNotice(playerKey));
        replies.Add(GamePackets.SprintOffReply(playerKey));
        return replies;
    }

    /// <summary>Sprint drain on the 100 ms ticks; at 0 SP the server ends the sprint (0x3722 alone, official).</summary>
    List<byte[]> SprintTick(long now)
    {
        if (!sprinting || resources is null || now < sprintTickAt) return [];
        var replies = new List<byte[]>();
        var drained = false;
        while (sprinting && now >= sprintTickAt)
        {
            sprintTickAt += SprintTickMs;
            if (invincible.Sp) continue;
            resources.Sp = Math.Max(0, resources.Sp - SprintDrain);
            drained = true;
            if (resources.Sp == 0) sprinting = false;
        }
        if (drained) replies.Add(resources.SpUpdate());
        if (!sprinting)
        {
            replies.Add(GamePackets.SprintOffNotice(playerKey));
            worldPlayer?.Map?.Broadcast(worldPlayer, [GamePackets.SprintOffNotice(playerKey)]);
        }
        return replies;
    }

    // ---------- Inventory (0x5610 → 0x5611; changes as 0x561b) ----------

    /// <summary>Items as last sent to the client, by wire key (for 0x561b differences).</summary>
    readonly Dictionary<long, InventoryItem> inventory = [];
    /// <summary>The wire keys of the stored items this session knows (checks).</summary>
    public IReadOnlyCollection<long> StoredItemKeys => storedItems.Keys;

    /// <summary>
    /// Stored items on the wire: equipped (container 11, slot = equip slot) first, then the bag
    /// (container 1) in creation order on the free slots. Equipment (templates below 500000000,
    /// or anything equipped) carries an equip detail with its Item.dat ItemLevel.
    /// </summary>
    static List<InventoryItem> Layout(IReadOnlyList<ItemState> items, long owner = 0, GameData? data = null)
    {
        var result = new List<InventoryItem>();
        // Free slots are laid out per container: the bag (1) and the warehouse (2, location 'storage').
        var used = new Dictionary<string, HashSet<int>> { ["bag"] = [], ["storage"] = [] };
        foreach (var i in items.Where(i => i.Location is "bag" or "storage" && i.Slot is not null)) used[i.Location].Add(i.Slot!.Value);
        var next = new Dictionary<string, int> { ["bag"] = 0, ["storage"] = 0 };
        foreach (var item in items.Where(i => i.Location is "bag" or "equipment" or "storage")
                     .OrderBy(i => i.Location == "equipment" ? 0 : 1).ThenBy(i => i.Slot ?? int.MaxValue).ThenBy(i => i.WireId))
        {
            var equipped = item.Location == "equipment";
            int slot;
            if (equipped) slot = item.Slot ?? 1;
            else if (item.Slot is { } fixedSlot) slot = fixedSlot;
            else
            {
                while (used[item.Location].Contains(next[item.Location])) next[item.Location]++;
                used[item.Location].Add(slot = next[item.Location]);
            }
            var key = item.WireId > 0 ? item.WireId : BitConverter.ToInt64(item.InstanceId.ToByteArray(), 0) & long.MaxValue;
            var equipment = equipped || item.TemplateId < 500_000_000;
            var container = equipped ? InventoryItem.Equipped : item.Location == "storage" ? InventoryItem.Storage : InventoryItem.Bag;
            result.Add(new InventoryItem(key, item.TemplateId, item.Quantity, container, slot,
                equipment ? new EquipDetail((data?.ItemLevel(item.TemplateId) ?? 1) + Enchants.Effect(item.TemplateId, EnchantOf(item)).ItemLevel, equipped ? (byte)slot : (byte)0,
                    (byte)EnchantOf(item), SoulBindCount(item) > 0 ? owner : 0, SoulBindCount(item)) : null));
        }
        return result;
    }

    /// <summary>Stored soul binds (upgrades.soul_bind = bind count).</summary>
    static byte SoulBindCount(ItemState item) =>
        item.Upgrades.ValueKind == System.Text.Json.JsonValueKind.Object && item.Upgrades.TryGetProperty("soul_bind", out var count)
        && count.TryGetByte(out var value) ? value : (byte)0;

    /// <summary>
    /// 0x562f ItemSoulBindComplete_RQ: the item is bound to this character (stored), answered
    /// 0x5630 with the new bind count and the item update (0x561b) carrying the bind.
    /// </summary>
    async Task<List<byte[]>> SoulBindCompleteAsync(long key, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row))
            return [GamePackets.SoulBindComplete(key)];
        var count = (byte)Math.Min(255, SoulBindCount(row) + 1);
        var bound = await Stored(async () => await store.SoulBindItemAsync(character, row.InstanceId, count, ct) ? [Array.Empty<byte>()] : []);
        if (bound.Count != 1) return [GamePackets.SoulBindComplete(key, 0, 1)];
        return [GamePackets.SoulBindComplete(key, count), .. await InventoryChangesAsync(ct), .. Achieve("SoulbindTry")];
    }

    void Remember(IEnumerable<InventoryItem> items)
    {
        inventory.Clear();
        foreach (var item in items) inventory[item.Key] = item;
    }

    /// <summary>Stored rows by wire key (the item_key the client's equip requests carry).</summary>
    readonly Dictionary<long, ItemState> storedItems = [];

    static long WireKey(ItemState item) => item.WireId > 0 ? item.WireId : BitConverter.ToInt64(item.InstanceId.ToByteArray(), 0) & long.MaxValue;

    async Task<List<InventoryItem>> LoadInventoryAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        var rows = await store.InventoryAsync(character, ct);
        storedItems.Clear();
        foreach (var row in rows) storedItems[WireKey(row)] = row;
        return Layout(rows, selected?.WireId ?? 0, data);
    }

    /// <summary>The opcode of the last equip/unequip request recognised by shape (research log).</summary>
    public int? EquipOpcode { get; private set; }

    /// <summary>
    /// ItemEquip_RQ {i64 item_key, u8 EEquipSlot} (11 bytes) and ItemUnEquip_RQ {item_key, slot,
    /// i32 slot_pos} (15 bytes), recognised by shape because their opcodes are outside the
    /// anchored blocks: the key must be a stored item of this character. Stored through the
    /// equipment rules, answered on the next opcode (RQ/RS pairs are adjacent) with result,
    /// key, slot (and slot_pos), then the item changes (0x561b) and the equip level (0x561d).
    /// </summary>
    const int EquipOpcodeLive = 0xe247, UnequipOpcodeLive = 0xe249;   // live 2026-10-15

    async Task<List<byte[]>?> EquipRequestAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length is not (11 or 15) || Opcode(plain) is not (EquipOpcodeLive or UnequipOpcodeLive)
            || Store is not { } store || CharacterId is not { } character || resources is null) return null;
        var key = BitConverter.ToInt64(plain, 2);
        var slot = plain[10];
        if (!storedItems.TryGetValue(key, out var row) || slot is < 1 or > 50) return null;
        var opcode = Opcode(plain);
        EquipOpcode = opcode;
        var equip = plain.Length == 11;
        var moved = await Stored(async () => await store.MoveItemAsync(character, row.InstanceId, row.Revision, equip ? slot : null, ct) ? [Array.Empty<byte>()] : []);
        var reply = PacketWriter.Opcode((ushort)(opcode + 1)).U16(moved.Count == 1 ? 0 : 1).I64(key).U8(slot);
        if (!equip) reply.I32(BitConverter.ToInt32(plain, 11));
        var replies = new List<byte[]> { reply.ToArray() };
        if (moved.Count == 1)
        {
            var before = VisualItems();
            replies.AddRange(await InventoryChangesAsync(ct));
            // The new weapon decides weapon skills and damage; gear changes the stats.
            equippedWeapon = WeaponFromInventory();
            replies.AddRange(RefreshStats());
            var level = EquipItemLevel();
            replies.Add(GamePackets.ItemEquipLevelChange(level, level));
            // Also to the wearer: without it the own model kept its old look (live 2026-10-15).
            if (EquipChange(before, level) is { } change)
            {
                replies.Add(change);
                worldPlayer?.Map?.Broadcast(worldPlayer, [change]);
            }
        }
        return replies;
    }

    /// <summary>0x5610: the stored inventory (account logins) or the starter weapon (development), then the equip level.</summary>
    async Task<List<byte[]>> InventoryListAsync(CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character)
            return await Stored(async () =>
            {
                var items = await LoadInventoryAsync(store, character, ct);
                Remember(items);
                // The weapon in equip slot 1 decides weapon skills (none equipped: no weapon).
                equippedWeapon = WeaponFromInventory();
                var level = EquipItemLevel();
                return [GamePackets.ItemDepotList(items), GamePackets.ItemEquipLevelChange(level, level)];
            });
        var weapon = data.StarterWeapon(Profile());
        if (weapon is null) return [];
        equippedWeapon = weapon;
        var starter = new InventoryItem(1, weapon.Value, 1, InventoryItem.Equipped, 1, new EquipDetail(1, 1));
        Remember([starter]);
        return [GamePackets.ItemDepotList([starter]), GamePackets.ItemEquipLevelChange(1, 1)];
    }

    /// <summary>
    /// After a stored change (a GM grant): reload the items and send what differs from the client's
    /// view as 0x561b (type 0 new, type 1 changed; increase reason 24 as the official grants).
    /// </summary>
    public async Task<List<byte[]>> InventoryChangesAsync(CancellationToken ct) => await InventoryChangesAsync(ct, 24, 0);

    /// <summary>
    /// The stored items against what the client has: adds, updates and removals (EItemModificationType
    /// 0/1/2) with the given EIncreaseReason / EDecreaseReason (24 kQuestReward was the only one before).
    /// </summary>
    async Task<List<byte[]>> InventoryChangesAsync(CancellationToken ct, byte increase, byte decrease)
    {
        if (Store is not { } store || CharacterId is not { } character) return [];
        var items = await LoadInventoryAsync(store, character, ct);
        var changes = new List<ItemModification>();
        var gained = new List<(int ItemId, long Count)>();
        foreach (var item in items)
        {
            if (!inventory.TryGetValue(item.Key, out var before)) { changes.Add(new ItemModification(0, item, Increase: increase)); gained.Add((item.ItemId, item.Count)); }
            else if (before != item)
            {
                changes.Add(new ItemModification(1, item, Increase: (byte)(item.Count > before.Count ? increase : 0), Decrease: (byte)(item.Count < before.Count ? decrease : 0)));
                if (item.Count > before.Count) gained.Add((item.ItemId, item.Count - before.Count));
            }
        }
        var acquired = AchieveItemsGained(gained);
        var kept = items.Select(i => i.Key).ToHashSet();
        foreach (var gone in inventory.Values.Where(i => !kept.Contains(i.Key)))
            changes.Add(new ItemModification(2, gone, Decrease: decrease));
        Remember(items);
        return changes.Count == 0 ? acquired : [GamePackets.ItemModificationList(changes), .. acquired];
    }

    List<byte[]> UnlockUpdateOnce()
    {
        if (unlockUpdateSent) return [];
        unlockUpdateSent = true;
        return [GamePackets.ContentsUnlockUpdate()];
    }

    StarterProfile? Profile() => character is null ? data.Profile(1, 1) : data.Profile(character.CreationId, character.Gender);

    async Task<List<byte[]>> GameLoginAsync(byte[] plain, long now, CancellationToken ct)
    {
        loginIdentity = LoginIdentity(plain);
        if (Store is not null)
            return await Stored(async () =>
            {
                var characters = await LobbyCharactersAsync(ct);
                var races = characters.Select(c => c.Race).Distinct().ToList();
                accountRole = await StaffRoleAsync(Store.AccountId, ct);
                return [NativeFraming.CompressedEnvelope(GamePackets.LoginCharacters(characters,
                    $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", Store.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    GamePackets.CommServerKey, now, serverRace: races.Count == 1 ? races[0] : 0, isAdmin: accountRole is not null && lobbyStaff))];
            });
        // A '$' placeholder with the spawn position loads by itself; the origin record leaves
        // the client on the create screen.
        if (options.IntroCharacter is { } intro)
            return [NativeFraming.CompressedEnvelope(GamePackets.GameLogin(intro.Name, GamePackets.SpawnPosition, now,
                worldId: intro.ServerId, accountSid: loginIdentity.AccountSid, guid: loginIdentity.Guid, nameCode: (int)intro.Code,
                race: 1, gender: 2, detailFlag: 1, equipLevel: 12, level: level, zoneId: 3000))];
        return [NativeFraming.CompressedEnvelope(GamePackets.GameLogin("$local", Vector3d.Zero, now, worldId: options.WorldId,
            accountSid: loginIdentity.AccountSid, guid: loginIdentity.Guid, level: level, zoneId: 3000))];
    }

    /// <summary>Role lookup at login; a failed lookup means no staff role rather than a failed login.</summary>
    async Task<string?> StaffRoleAsync(Guid account, CancellationToken ct)
    {
        if (options.StaffRole is not { } lookup) return null;
        try { return await lookup(account, ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PersistenceErrors.Add(error.GetType().Name);
            return null;
        }
    }

    async Task<List<LobbyCharacter>> LobbyCharactersAsync(CancellationToken ct)
    {
        var records = new List<LobbyCharacter>();
        // Due deletions complete first (those characters leave the list); pending ones show their time.
        var deletions = await Store!.CharacterDeletionsAsync(ct);
        var renames = await Store!.LastRenamesAsync(ct);
        lobbyStaff = false;
        foreach (var state in await Store!.LobbyAsync(ct))
        {
            if (state.Staff) lobbyStaff = true;
            var profile = StoredProfile(state);
            // The stored equipment as others see it (a new character wears its starter weapon,
            // as in the official records of level-one starter characters).
            var worn = (await Store.InventoryAsync(state.Summary.CharacterId, ct)).Where(i => i.Location == "equipment" && i.Slot is { } s && VisualSlot.ContainsKey(s))
                .OrderBy(i => VisualSlot[i.Slot!.Value]).ToList();
            var equips = worn.Select(i => new EquipBroad(i.TemplateId, 0, (byte)VisualSlot[i.Slot!.Value])).ToList();
            records.Add(new LobbyCharacter
            {
                // Zone 3009 follows the official records of level-one starter characters.
                Key = state.WireId, DbId = state.WireId, Nickname = state.Summary.Name, PcId = profile.PcId,
                Race = state.Race, Gender = state.Gender, Level = state.Progress.Level, CombatLevel = state.Progress.Level,
                Experience = state.Progress.Experience, EquipItemLevel = worn.Sum(i => data.ItemLevel(i.TemplateId)), ZoneId = 3009,
                Equips = equips,
                Position = StoredCharacters.Position(state), VoiceType = state.VoiceType,
                SlotNo = state.LobbySlot ?? throw new ArgumentException("Character has no assigned lobby slot"),
                CustomizingData = state.Appearance,
                PendingDeleteTime = deletions.TryGetValue(state.Summary.CharacterId, out var deleteAt) ? deleteAt.ToUnixTimeMilliseconds() : 0,
                LastNicknameChangeTime = renames.TryGetValue(state.Summary.CharacterId, out var renamedAt) ? renamedAt.ToUnixTimeMilliseconds() : 0,
            });
        }
        return records;
    }

    StarterProfile StoredProfile(CharacterState state)
    {
        var profile = data.Profile(state.CreationId, state.Gender);
        return profile is not null && profile.Race == state.Race ? profile : throw new ArgumentException("Persisted character profile is unsupported");
    }

    /// <summary>A storage-backed handler; failures send nothing rather than a guess.</summary>
    async Task<List<byte[]>> Stored(Func<Task<List<byte[]>>> action)
    {
        try
        {
            return await action();
        }
        // A database error (constraint, connection) refuses the request instead of ending the connection.
        catch (Exception error) when (error is ArgumentException or ProtocolException or InvalidOperationException or GameplayException
                                          or System.Text.Json.JsonException or KeyNotFoundException or System.Data.Common.DbException)
        {
            PersistenceErrors.Add(error.GetType().Name);
            return [];
        }
    }

    /// <summary>Why a creation was refused, as the client's EResult (checked in the client's own order: name, class, race, slot).</summary>
    async Task<int> CreateRefusalAsync(INativeCharacterStore store, CreateRequestData refused, byte[] plain, CancellationToken ct)
    {
        try
        {
            if (CreateRequest(plain, strict: true) is null) return GamePackets.CreateInvalidCustomizing;
            if (CharacterNames.IsStaffStyle(refused.Name) && !StaffAccount) return GamePackets.CreateInvalidName;
            if (await store.NameTakenAsync(refused.Name, ct)) return GamePackets.CreateDuplicateName;
            var profile = data.Profile(refused.CreationId, refused.Gender);
            if (profile is null) return GamePackets.CreateInvalidPcData;
            var lobby = await store.LobbyAsync(ct);
            var races = lobby.Select(s => s.Race).Distinct().ToList();
            if (races.Count == 1 && races[0] != profile.Race) return GamePackets.CreateInvalidServerRace;
            if (lobby.Any(s => s.LobbySlot == refused.SlotNo)) return GamePackets.CreateInvalidSlot;
            return GamePackets.CreateErrorInternal;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or GameplayException or System.Data.Common.DbException)
        {
            return GamePackets.CreateErrorInternal;
        }
    }

    async Task<List<byte[]>> CreateAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (Store is { } store)
        {
            var created = await Stored(async () =>
            {
                // Strict complete parsing: unknown tails must not create a character.
                var request = CreateRequest(plain, strict: true) ?? throw new ArgumentException("Unsupported character creation layout");
                var profile = data.Profile(request.CreationId, request.Gender) ?? throw new ArgumentException("Unknown creation profile");
                // One race per account per server, as on the official servers.
                var races = (await store.LobbyAsync(ct)).Select(s => s.Race).Distinct().ToList();
                if (races.Count == 1 && races[0] != profile.Race) throw new ArgumentException("Account already chose the other race on this server");
                var requestId = StoredCharacters.Uuid5(store.AccountId, "native-create-v1:" + Convert.ToHexStringLower(SHA256.HashData(plain)));
                var state = await store.CreateAsync(new CreatePlayer(requestId, $"dev-v1-{request.CreationId}:{request.Gender}", request.Name,
                    request.CustomizingData, VoiceType: request.VoiceType, LobbySlot: request.SlotNo), ct);
                var stored = StoredProfile(state);
                // Database success precedes the success reply.
                return [GamePackets.CharacterCreated(state.Summary.Name, state.Gender, stored.PcId, stored.Race, state.WireId, state.WireId,
                    state.Appearance, state.VoiceType, state.LobbySlot ?? 0, now)];
            });
            if (created.Count > 0) return created;
            // A refused creation is answered with the client's EResult code for its cause (the usmap enum; the client's
            // lobby message follows it): a name in use, a staff-style name, the account's other race, an unknown class,
            // a taken slot, a layout the server does not read; anything else is an internal error.
            if (CreateRequest(plain, strict: false) is not { } refused) return [];
            var reason = await CreateRefusalAsync(store, refused, plain, ct);
            return [GamePackets.CharacterCreated(refused.Name, refused.Gender, 1, 1, 0, 0, refused.CustomizingData, refused.VoiceType, refused.SlotNo, now, result: reason)];
        }
        var parsed = CreateRequest(plain, strict: false);
        if (parsed is null) return [];
        character = new Choice(parsed.Name, parsed.Gender, parsed.CreationId, parsed.CustomizingData, parsed.VoiceType);
        var p = data.Profile(parsed.CreationId, parsed.Gender);
        return [GamePackets.CharacterCreated(parsed.Name, parsed.Gender, p?.PcId ?? 1, p?.Race ?? 1, 1, 0, parsed.CustomizingData,
            parsed.VoiceType, parsed.SlotNo, now, level)];
    }

    async Task<List<byte[]>> SelectAsync(byte[] plain, long now, CancellationToken ct)
    {
        var request = SelectRequest(plain);
        resources = null;   // a selected character enters the world with full pools
        if (Store is { } store)
        {
            if (request is not null) SelectKeys.Add((int)request.Value.Key);
            return await Stored(async () =>
            {
                if (request is null || plain.Length != 2 + NativeVarint.Encode(request.Value.Key).Length + 16)
                    throw new ArgumentException("Unsupported character selection layout");
                var key = request.Value.Key;
                // Dev runs: after a 0x361c record with key 1 the client selected 65536.
                // CANDIDATE: the key arrives shifted left 16 bits.
                var candidates = new List<uint> { key };
                if (key != 0 && (key & 0xFFFF) == 0) candidates.Add(key >> 16);
                CharacterState? state = null;
                foreach (var wire in candidates.Where(c => c is > 0 and <= int.MaxValue))
                    if ((state = await store.LoadWireAsync((int)wire, ct)) is not null) break;
                if (state is null) throw new ArgumentException("No stored character for the selection key");
                if (!candidates.Contains((uint)state.WireId)) throw new ArgumentException("Character identity mismatch");
                var profile = StoredProfile(state);
                var savedAt = StoredCharacters.Position(state);
                if (state.Progress.MapId is not > 0) throw new ArgumentException("Saved character has no valid map");
                selected = state;
                StaffRole = state.Staff ? accountRole : null;
                experience = state.Progress.Experience;
                registeredWings.Clear();
                registeredWings.UnionWith(await store.WingsAsync(state.Summary.CharacterId, ct));
                var extras = await store.ExtrasAsync(state.Summary.CharacterId, ct);
                LoadSkillLevels(await store.SkillsAsync(state.Summary.CharacterId, ct));
                LoadAchievements(await store.AchievementsAsync(state.Summary.CharacterId, ct), await store.GroupRewardsTakenAsync(state.Summary.CharacterId, ct), now);
                LoadCollections(await store.CollectionsAsync(state.Summary.CharacterId, ct));
                LoadBossChallenges(await store.BossChallengesAsync(state.Summary.CharacterId, ct));
                LoadTitles(extras);
                LoadSkins(extras);
                LoadMasteries(extras);
                await LoadRidesAsync(store, state.Summary.CharacterId, ct);
                await LoadQuickSlotsAsync(store, state.Summary.CharacterId, ct);
                LoadSettings(await store.CharacterSettingsAsync(state.Summary.CharacterId, ct));
                await LoadBuybackAsync(store, state.Summary.CharacterId, ct);
                await LoadGuildAsync(store, state.Summary.CharacterId, ct);
                await LoadQuestsAsync(store, state.Summary.CharacterId, state.Race, ct);
                engraved.Clear();
                engraved.AddRange(await store.TeleportArtifactsAsync(state.Summary.CharacterId, ct));
                fog.Clear();
                fog.UnionWith(await store.MapFogAsync(state.Summary.CharacterId, ct));
                await LoadSocialAsync(ct);
                character = new Choice(state.Summary.Name, state.Gender, state.CreationId, state.Appearance, state.VoiceType);
                level = data.ClampLevel(state.Progress.Level);
                var savedMap = state.Progress.MapId.Value;
                // Logged out inside an instance layer: back at its exit on the field map (the copy is gone).
                if (Instances.Of(savedMap) is { } left)
                    (savedMap, savedAt) = (left.Exit.Map, new Vector3d(left.Exit.Position[0], left.Exit.Position[1], left.Exit.Position[2]));
                // A generated dungeon or story layer (its copy is gone too): a layer puts the player back on its base map at the
                // same spot, any other dungeon at the race's starting point (live 2026-10-18: a relog from the HQ001 layer).
                else if (IsInstanceMap(savedMap))
                {
                    var home = state.Race == 2 ? 1100 : 1000;
                    var dungeonMap = savedMap;
                    (savedMap, savedAt) = MapEventData.Volume(savedMap) is { } overlay ? (overlay.Base, savedAt)
                        : data.MapStart(home) is { } start ? (home, start.Position) : (GamePackets.SpawnMapId, GamePackets.SpawnPosition);
                    // A solo dungeon a quest still sends the player into (an event started by an open quest step): a fresh copy
                    // from its start, the field spot kept as the way out (live 2026-10-25: a relog in the Ascension Subspace left
                    // the player at the base, the lake cave that leads there already cleared and AQ1602000 open for good).
                    profileRace = state.Race == 2 ? 2 : 1;
                    if (QuestDungeonResume(dungeonMap, now) is { } resume)
                    {
                        instanceReturn = (savedMap, savedAt);
                        (savedMap, savedAt) = (dungeonMap, resume);
                        Note($"login: back into dungeon map {dungeonMap} (its quest step is open)");
                    }
                }
                instanceKey = IsInstanceMap(savedMap) ? playerKey : 0;
                spawn = (savedMap, savedAt);
                loadTicket = 1;
                return [GamePackets.SelectCharacter((int)key, state.WireId, playerKey, profile.PcId, state.Appearance, now),
                    GamePackets.ClientEnvSetting(), GamePackets.PrepareEnterMap(loadTicket, savedMap, savedAt, now)];
            });
        }
        if (request is null) return [];
        var chosen = character;
        var p = chosen is null ? data.Profile(1, 1) : data.Profile(chosen.CreationId, chosen.Gender);
        var (devMap, devAt) = (GamePackets.SpawnMapId, GamePackets.SpawnPosition);
        if (options.DevelopmentMap is { } wanted && data.MapStart(wanted) is { } start)
        {
            (devMap, devAt) = (wanted, start.Position);
            spawn = (devMap, devAt);
        }
        return [GamePackets.SelectCharacter((int)request.Value.Key, 1, 1, p?.PcId ?? 1, chosen?.CustomizingData ?? [], now),
            GamePackets.ClientEnvSetting(), GamePackets.PrepareEnterMap(1, devMap, devAt, now)];
    }

    List<byte[]> LoadComplete(byte[] plain, long now)
    {
        if (plain.Length < 6) return [];
        var ticket = BitConverter.ToInt32(plain, 2);
        var name = character?.Name ?? options.IntroCharacter?.Name ?? "$local";
        var gender = character?.Gender ?? 1;
        var profile = data.Profile(character?.CreationId ?? 1, gender);
        // Skills follow the character level (tools/class_skills.py), not a grant list.
        var kit = profile is null ? [] : data.LearnedSkills(profile.Class, level);
        learned = kit.Select(k => k.SkillId).ToHashSet();
        className = profile?.Class;
        var (mapId, at) = spawn ?? (GamePackets.SpawnMapId, GamePackets.SpawnPosition);
        worldMap = mapId;
        // Map geometry loads off the game loop; NPCs use it from the tick it is ready.
        var geometry = data.GeometryMap(mapId);
        // A shared map's NPC AI gets its geometry once, from the first player who enters it.
        var sharedMap = options.World?.Map(mapId, instanceKey);
        JoinCopy(sharedMap, mapId, now);
        if (sharedMap is not null) ai = sharedMap.Ai;
        if (sharedMap is null || !sharedMap.GeometryApplied)
        {
            if (sharedMap is not null) sharedMap.GeometryApplied = true;
            ai.Terrain = null;
            ai.Nav = null;
            terrainLoad = options.Terrain?.LoadAsync(geometry);
            navLoad = options.Nav?.LoadAsync(geometry);
            ApplyGeometry();
        }
        else
        {
            terrainLoad = null;
            navLoad = null;
        }
        if (sharedMap is not null)
        {
            if (options.TrainingDummy) sharedMap.Targets.TryAdd(Combat.TrainingDummyKey, Combat.TrainingDummy(at));
            targets = sharedMap.Targets;
            respawnAt = sharedMap.RespawnAt;
        }
        else if (targets is null)
        {
            targets = [];
            if (options.TrainingDummy) targets[Combat.TrainingDummyKey] = Combat.TrainingDummy(at);
            foreach (var (key, target) in Combat.MapTargets(data, mapId)) targets[key] = target;
        }
        MapEventsPrepare();   // spawn entries a map event starts itself stay dormant
        PlaceChallengeBoss();
        equippedWeapon = inventory.Count > 0 ? WeaponFromInventory() : data.StarterWeapon(profile);
        // A map load in the world (teleport, revive) keeps HP/MP (it used to be a full heal); the
        // first entry after a selection starts full.
        var fresh = resources is null;
        resources = fresh ? new Resources(playerKey) : resources!;
        RefreshStats();
        if (fresh) (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        resourceTickMs = now;
        var armed = EquipItemLevel();
        mountedRide = null;   // a map load (teleport, revive) leaves the ride
        ResetMovementCheck(at, now);
        air = AirState.Ground;
        flySprint = false;
        // Flight needs an equipped wing: staff and ascended characters (grade 2+) get their race's default wing and the
        // flight gauge (OBSERVED 250,000 at the ascension; drained and refilled on the ticks).
        var race = profileRace = profile?.Race ?? 1;
        wingId = IsAdmin || (selected?.Ascension ?? 1) >= 2 ? GamePackets.DefaultWing(race) : 0;
        resources.FpMax = resources.Fp = wingId != 0 ? FpMax : 0;
        // An instance-layer move in or out (the level stays loaded): the official load is the short list, not the world-entry
        // set (OBSERVED entry #9676-9702: 0x3623, 0x3633, 0x3646, 0x382a, 0x364a, 0x8d00, 0x4001, 0x4002, 0x6100.., 0x922e, spawns;
        // exit #14305-14345: the same with 0x3722 and 0x3742 after 0x4002 and no dungeon state). Live 2026-10-19: with the
        // full set the player had no controls in the layer (three byte-level fixes before this one did not free them).
        var instanceMove = pendingInstanceMove;
        pendingInstanceMove = false;
        var shortLoad = instanceMove && sameLevelMove && TestPiece(LayerPieceShortLoad);   // a dungeon on another level is a full load (unrecorded)
        sameLevelMove = false;
        var replies = new List<byte[]>
        {
            // OBSERVED (capture 20261013 #371, #9676, #14305): the ticket is 0 in every load reply whatever 0x3621 carried;
            // the i32 after the position is 4 at the instance-layer entry and 1 at the world entry and the way back
            // (CANDIDATE: the map's kind; 2 came from an earlier sample and works at the world entry).
            GamePackets.LoadComplete(0, at, frame: IsInstanceMap(mapId) ? 4 : 2, charKey: playerKey),
        };
        if (!shortLoad)
        {
            // Official order: the last ground height and the resource maxima before the appear.
            replies.Add(GamePackets.LastOnGroundZ(playerKey, (float)at.Z));
            replies.Add(resources.Maxima());
        }
        replies.Add(GamePackets.MyCharAppear(new MyCharacterAppear
            {
                ToggleFlags = sealStoneOn ? 1 : 0,
                Key = playerKey, Name = CharacterNames.Display(name, StaffRole), Gender = gender, MapId = mapId, Position = at, PcId = profile?.PcId ?? 1, Race = profile?.Race ?? 1,
                // The own appearance's reason byte: 0 at the world entry, 12 after an instance-layer move in and out (OBSERVED
                // #382, #9680, #14311: the same EAppearReasonType as 0x3647/0x3621). Live 2026-10-19: with 0 in the layer the
                // player kept no controls after Robin ran off.
                AppearReason = instanceMove && TestPiece(LayerPieceAppearance) ? InstanceReason : 0,
                TeleportEffect = instanceMove && TestPiece(LayerPieceAppearance) ? InstanceEffect : (byte)0,
                CharLevel = level, CombatLevel = level, LevelMax = level, Experience = experience, WeaponId = equippedWeapon,
                Relationship = (byte)((profile?.Race ?? 1) == 2 ? 2 : 1),
                // The real pools (PcStatLevel + gear + buffs); the 1500/200 defaults made the bar read e.g. 4000/1500.
                Hp = resources.Hp, HpMax = resources.HpMax, Mp = resources.Mp, MpMax = resources.MpMax,
                Equips = VisualEquips(all: false),
                CustomizingData = character?.CustomizingData ?? [], VoiceType = character?.VoiceType ?? 0,
                Ascension = selected?.Ascension ?? 1, DbId = selected?.WireId ?? 0,
                WingId = wingId, Fp = resources.Fp, FpMax = resources.FpMax,
                EquipItemLevel = armed, HighestTotalItemLevel = armed,
            }));
        replies.Add(GamePackets.CharBarrier(playerKey));
        replies.Add(GamePackets.CharStatsUpdate(playerKey, StatSheet().Stats, resources.HpMax));
        if (!shortLoad)
        {
            replies.Add(GamePackets.MentoringRoleUpdate());
            replies.Add(GamePackets.WingList(OwnedWings()));
            replies.Add(RideInit());
            replies.Add(GamePackets.GlideCooltime(playerKey));
        }
        // The class passives (weapon equip permission) right after the appearance, as the official world entry has them.
        replies.InsertRange(replies.FindIndex(b => b[0] == 0x46 && b[1] == 0x36) + 1, PassiveAbnormals(resend: true));
        replies.InsertRange(0, sameLevelRemovals);   // the same level's old entities go before the load reply (OBSERVED)
        sameLevelRemovals = [];
        if (shortLoad)
        {
            // After 0x364a the official layer entry sends one point update (#9684 `01 01 03 a0860100`: type 3, 100000).
            replies.Add(GamePackets.CharPoints(playerKey, [((CharPoint)3, 100000)]));
        }
        else
        {
            replies.AddRange(BuffsAfterLoad());
            if (Store is not null) replies.Add(RestorableItems());   // the buyback list (official: 0xe342 at world entry)
            replies.AddRange(GuildAtEntry());
            // Titles and skins as the official entry lists them (both empty there: #196 0x5679 `0000`, #203 0x8d5b `000000010000`).
            replies.Add(TitleList());
            replies.Add(SkinInfo());
            replies.Add(AchievementList(now));
            replies.Add(BattlePassInfoList(now));
            replies.Add(PeriodCollectionList(now));
            replies.Add(BossChallengeList());
            replies.AddRange(EntryConstants(now));
            replies.AddRange(AchieveSet("EquipItemLevel", EquipItemLevel(), now: now));
            if (fresh) { replies.AddRange(Achieve("LoginCharacter", now: now)); elapsedAchieveAt = now + 60_000; }
            replies.AddRange(CraftListsAtEntry());
        }
        if (!instanceMove && !shortLoad) replies.AddRange(MapEnvironment(mapId));   // every full map load (OBSERVED at the official world entry, map 1000 state 0)
        if (instanceMove && TestPiece(LayerPieceEnvironment))
        {
            replies.AddRange(MapEnvironment(mapId));   // after an instance-layer move, in and out (official)
            // The way back to the base map carries two empty notifications after the environment (OBSERVED #14318-14319).
            if (!IsInstanceMap(mapId)) replies.AddRange([PacketWriter.Opcode(0x3722).ToArray(), PacketWriter.Opcode(0x3742).ToArray()]);
        }
        moveReason = 0;
        moveEffect = 0;
        replies.AddRange(DungeonStateAtEntry(now));   // OBSERVED order: 0x4001/0x4002, 0x6100, 0x6109, 0x6108, then 0x922e
        if (!shortLoad)
        {
            if (profile is not null)
            {
                // Owned skills before the hotbar (0x5100 then 0x5600; not the usmap ordinals).
                replies.Add(GamePackets.SkillList(WithLevels(kit)));
                replies.Add(GamePackets.SkillOptionSlots(learned.Where(id => id >= 11_000_000 && id != CombatDodge && data.Skill(id)?.Type == "Active").Order().ToList()));
                replies.AddRange(GamePackets.EmptyEntryLists());
                replies.AddRange(SettingsAtEntry(now));
                // The player's saved hotbar, else the class's default bar for its level.
                replies.Add(Hotbar(profile.DefaultBar()));
            }
            var unlocked = GamePackets.UnlockedContents(level).Concat(QuestUnlockTypes()).Except(GamePackets.LaterUnlockedContents);
            // The wing menu (Wing, 12) is quest-gated (HQ1101020); a winged character has it open.
            if (wingId != 0) unlocked = unlocked.Append((byte)12);
            replies.Add(GamePackets.ContentsUnlockList(unlocked.Distinct().Order().ToArray()));
            if (questLog?.All.Where(q => q.Status < QuestLog.Rewarded).Select(Wire).ToList() is { Count: > 0 } open)
                replies.Add(GamePackets.QuestList(open));
        }
        if (reloading)
        {
            // A teleport's map load: 0xe257 just re-locked chat (45) and the client sends no new
            // world-entry queries, so the unlock update (0xe258) follows directly (live: chat stayed
            // closed after MoveMap without it). The short load sent no 0xe257, so nothing to undo.
            if (!shortLoad) { replies.Add(GamePackets.ContentsUnlockUpdate()); unlockUpdateSent = true; }
            reloading = false;
            if (invisible) replies.Add(GamePackets.InvisibleMode(true));   // a GM stays hidden across the reload
        }
        replies.AddRange(NpcsAtEntry(at));
        replies.AddRange(KibeliskSpawns(mapId, now));
        if (!shortLoad)
        {
            replies.Add(GamePackets.OpenSubzoneGroups(fog.ToList()));
            replies.AddRange(SocialWorldEntry());
        }
        fogCheckAt = 0;
        shownObjects.Clear();
        shownTriggers.Clear();
        usedObjects.Clear();
        pendingUse = null;
        replies.AddRange(ObjectVisibility(at, now));
        replies.AddRange(MapEventsStart(now));   // the layer's events join (0x922e) and run their first directives
        CharacterName = name;
        EnterSharedWorld(mapId, at);
        arrivalCheckDue = true;
        return replies;
    }

    // ---------- Casts and combat (tools/starter_casts.py) ----------

    public sealed record CastRequest(int SkillId, int ClientActionUid, int ChangeToggle, int TargetKey, double Yaw, Vector3d Location);

    /// <summary>0x3800 cast request (writer RVA 0x8f7db28): u16 presence mask, skill, byte 2, target varint, yaw + location, optionals.</summary>
    public static CastRequest? ParseCast(byte[] body)
    {
        if (body.Length < 26 || body[0] != 0x00 || body[1] != 0x38 || body[8] != 2) return null;
        var flags = BitConverter.ToUInt16(body, 2);
        if ((flags & ~0x1ff) != 0) return null;
        var reader = new PacketReader(body) { Offset = 9 };
        try
        {
            var targetKey = reader.Varint();
            float yaw = reader.F32(), x = reader.F32(), y = reader.F32(), z = reader.F32();
            if (!float.IsFinite(yaw) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || targetKey < 1) return null;
            if ((flags & 1) != 0) reader.U8();          // caster direction
            if ((flags & 6) != 0) reader.U8();          // auto-use/reserved share one packed bool byte
            int toggle = (flags & 8) != 0 ? reader.U8() : 0;
            int uid = (flags & 16) != 0 ? reader.U8() : 0;
            if ((flags & 32) != 0) reader.Varint();     // client act ID
            foreach (var bit in new[] { 64, 128 })
                if ((flags & bit) != 0 && !float.IsFinite(reader.F32())) return null;
            if ((flags & 256) != 0) reader.U64();       // client time
            if (!reader.End) return null;
            return new CastRequest(BitConverter.ToInt32(body, 4), uid, toggle, (int)targetKey, yaw, new Vector3d(x, y, z));
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    List<byte[]> CastReplies(byte[] body, long now)
    {
        if (ParseCast(body) is not { } request) return [];
        var skill = request.SkillId;
        // OBSERVED (official 2026-10-19: 36 dodges, 64 sprint starts; live trace 2026-10-20: the client sends 0x3704 then its
        // CombatDodge request): a dodge requested while the sprint is on starts NormalDodge 11000101 (the running dash, SP 10,000)
        // in place of CombatDodge (SP 30,000). Its replies: 0x3802, the 9-byte 0x3801 naming CombatDodge, abnormal 200 for 300 ms,
        // then 0x8d00 with the SP; 200 ms later 0x3806 ends it and 0x382c removes the abnormal.
        var dodging = skill is CombatDodge or NormalDodge;
        var variant = DashVariant;
        // Variants 6-9 change only the dash that follows a sprint. A standing dodge keeps the reply this client already completes.
        if (dodging && !sprinting && variant >= 6) variant = 0;
        // Variant 6: the sprint stands on its own. A 9-byte refusal (result 1, no skill start) so the client does not read the
        // mask-first reply as a failed dash and drop the sprint. The live question is whether the sprint run then holds on land.
        if (dodging && sprinting && variant == 6)
            return [GamePackets.DodgeResult(CombatDodge, 1)];
        // Variant 10: no reply at all to the dodge the client sends with the sprint (6 refused it and still froze, live 2026-10-20).
        if (dodging && sprinting && variant == 10) return [];
        // Variant 9 starts CombatDodge while sprinting (official starts NormalDodge). The rest of the sprint dashes are NormalDodge.
        // OBSERVED (official 2026-10-19 and 2026-10-20): the server picks the dodge by combat state. All 36 CombatDodge starts came
        // within 8 s of the player's own attack; out of combat the dodge is NormalDodge (10,000 SP, no 0x3804 effect) whether the
        // player sprints, runs or stands (2026-10-20: shift while standing gave NormalDodge with no sprint reply at all).
        var started = skill == CombatDodge && variant != 9 && (sprinting || !inCombat) ? NormalDodge : skill;
        // Recovered Skill.dat cost, cooldown and weapon requirement for any learned skill.
        // NormalDodge is CombatDodge's variant: learned with it, SP 10000, the same group cooldown.
        var normalDodge = started == NormalDodge && learned.Contains(CombatDodge);
        var definition = normalDodge ? data.Skill(CombatDodge) is { } dodge ? dodge with { Cost = dodge.Cost with { Sp = NormalDodgeSp } } : null
            : data.Skill(skill);
        var cooldownKey = normalDodge ? CombatDodge : skill;
        var cost = definition?.Cost ?? new SkillCost(0, 0, 0);
        if (BuffStat("MPUseDecrease") is > 0 and var cheaper) cost = cost with { Mp = cost.Mp * Math.Max(0, 10_000 - cheaper) / 10_000 };
        var weapons = (definition?.Weapons ?? []).Where(w => w != "None").ToList();
        var accepted = definition is not null && resources is not null && !dead
            && resources.Mp >= cost.Mp && resources.Sp >= cost.Sp && resources.Hp > cost.Hp
            && (learned.Contains(skill) || normalDodge)
            && (weapons.Count == 0 || weapons.Contains(data.WeaponType(equippedWeapon) ?? ""))
            && (noCool || now >= cooldowns.GetValueOrDefault(cooldownKey));
        if (!accepted)
        {
            // A sprint dash refused for cost or cooldown stays on the 9-byte form. The mask-first reply drops the sprint.
            if (dodging && sprinting && variant is 7 or 8 or 9)
                return DeliverDash([GamePackets.DodgeResult(skill, 1)], variant);
            return [GamePackets.CastReply(false, skill, request.ClientActionUid)];  // must not end another action
        }
        var dismount = ForceDismount().Concat(InterruptItemCast()).ToList();
        skillMoveGraceUntil = now + SkillMoveGraceMs;
        var cooldown = noCool ? 0 : definition!.CooldownMs;
        cooldowns[cooldownKey] = now + cooldown;
        // Builder SetInvincible: the flagged resources do not decrease.
        if (!invincible.Mp) resources!.Mp -= cost.Mp;
        if (!invincible.Sp) resources!.Sp -= cost.Sp;
        if (!invincible.Hp) resources!.Hp -= cost.Hp;
        actionUid = actionUid % 255 + 1;
        if (dodging)
        {
            // The request's location is already the dash vector (about 100 cm along the movement input, OBSERVED in the client's own
            // 0x3800 and 0x370e); the official 0x3802 echoes it with the yaw and the dodger as target.
            // BISECT (2026-10-20): the recorded extras (the 9-byte 0x3801, abnormal 200 and its 0x382c, the 0x3806 end, the standing
            // dodge's 0x3804 + abnormal 3015) match the official bytes, yet with them this client never left its dash (five live tests),
            // while the pre-recording reply shape (variant 0) let it end the dash by itself after ~260 ms as the official client does;
            // with variant 0 the client drops the sprint itself 12-75 ms after its start (119 live sprints). The variants (DashVariant)
            // return the extras one at a time, switched in-game by a builder.
            if (sprinting && variant is 7 or 8 or 9)
            {
                // The recorded batch (0x3802, the 9-byte 0x3801 naming CombatDodge, abnormal 200, the SP; the end 200 ms later).
                // Variant 8 also waits out the official tick, so the end stays 200 ms after the batch leaves.
                var delay = variant == 8 ? DashBatchDelayMs : 0;
                var batchEnd = now + delay + DodgeAbnormalOffMs;
                List<byte[]> dash =
                [
                    .. dismount,
                    GamePackets.SkillStart(started, actionUid, request.Yaw, request.Location, playerKey, actorKey: playerKey, cooldown: cooldown),
                    GamePackets.DodgeResult(CombatDodge),
                    DodgeAbnormalAdd(now, started, actionUid, batchEnd),
                    GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]),
                ];
                if (started == CombatDodge) dodgeEffectAt = (actionUid, now + delay + DodgeEffectMs);
                return DeliverDash(dash, variant);
            }
            if (variant == 0)
            {
                List<byte[]> bisect =
                [
                    GamePackets.CastReply(true, skill, request.ClientActionUid),
                    .. dismount,
                    resources!.Update(),
                    GamePackets.SkillStart(started, actionUid, request.Yaw, request.Location, playerKey, actorKey: playerKey, cooldown: cooldown),
                ];
                return bisect;
            }
            var endAt = now + (variant == 5 ? DodgeLateEndMs : DodgeAbnormalOffMs);
            List<byte[]> dodgeReplies =
            [
                .. dismount,
                GamePackets.SkillStart(started, actionUid, request.Yaw, request.Location, playerKey, actorKey: playerKey, cooldown: cooldown),
                GamePackets.DodgeResult(CombatDodge),
            ];
            if (variant is 2 or >= 4) dodgeReplies.Add(DodgeAbnormalAdd(now, started, actionUid, endAt, end: variant != 2));
            else if (variant == 3) dodgeAbnormalOff = (0, started, actionUid, endAt, true);
            dodgeReplies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]));
            if (variant >= 4 && started == CombatDodge) dodgeEffectAt = (actionUid, now + DodgeEffectMs);
            return dodgeReplies;   // no damage rows: nothing for CombatReplies
        }
        var replies = new List<byte[]>
        {
            GamePackets.CastReply(true, skill, request.ClientActionUid),
            resources!.Update(),
            GamePackets.SkillStart(skill, actionUid, request.Yaw, request.Location, request.TargetKey, actorKey: playerKey, cooldown: cooldown),
        };
        replies.InsertRange(1, dismount);   // a cast takes the player off its ride before the skill starts
        replies.AddRange(CombatReplies(skill, request, now));
        return replies;
    }

    /// <summary>Corpses: removed (0x3642 flag 3) CorpseMs after the kill; at the respawn a fresh 0x3641 at full HP.</summary>
    List<byte[]> RespawnTargets(long now)
    {
        if (respawnAt.Count == 0) return [];
        var replies = new List<byte[]>();
        // Only the dead have a respawn time: no walk over a big map's thousands of NPCs every tick.
        foreach (var key in respawnAt.Keys.Order().ToList())
        {
            var due = respawnAt[key];
            if (!Targets.TryGetValue(key, out var target) || !target.Dead) continue;
            if (!target.CorpseRemoved && now >= due - RespawnMs + CorpseMs)
            {
                target.CorpseRemoved = true;
                replies.Add(GamePackets.Death(target.Key));
            }
            if (now < due) continue;
            respawnAt.Remove(target.Key);
            // In a scripted layer or dungeon the dead stay dead for the run (official dungeon mobs do not respawn).
            if (RunsMapEvents) continue;
            target.Hp = target.HpMax;
            target.CorpseRemoved = false;
            replies.Add(GamePackets.NpcAppear(target.Appearance()));
        }
        return replies;
    }

    /// <summary>
    /// Damage from an accepted cast: the target key selects a target; a self-centred cast
    /// (key 1) hits monsters within AreaRange of the cast location. 0x3804 hits, then 0x8d00 HP.
    /// </summary>
    List<byte[]> CombatReplies(int skill, CastRequest request, long now)
    {
        var replies = RespawnTargets(now);
        if (request.TargetKey >= SharedWorld.FirstPlayerKey && request.TargetKey != playerKey)
        {
            var pvp = DuelHits(skill, request.TargetKey, now);
            if (pvp.Count == 0) pvp = ArenaHits(skill, request.TargetKey, now);
            if (pvp.Count == 0) pvp = FactionHits(skill, request.TargetKey, now);
            replies.AddRange(pvp);
        }
        if (targets is null || targets.Count == 0 || data.Skill(skill)?.Damage is not { Length: > 0 }) return replies;
        IEnumerable<Target> chosen = targets.TryGetValue(request.TargetKey, out var direct) ? [direct]
            : request.TargetKey == playerKey ? targets.Values.Where(t => t.Position.DistanceSquared(request.Location) <= AreaRange * AreaRange)
            : [];
        foreach (var target in chosen.Where(t => t.Hostile).ToList())
        {
            if (target.Dead) continue;
            // An immune NPC (a scripted abnormal) takes the hits for nothing; it still turns on its attacker.
            var hits = Combat.SkillHits(data, skill, className, level, equippedWeapon, target.Defense, rng);
            if (target.Immune) hits = hits.Select(h => h with { Amount = 0 }).ToList();
            var seq = 0;
            foreach (var hit in hits)
            {
                target.Hp = Math.Max(0, target.Hp - hit.Amount);
                replies.Add(GamePackets.SkillHit(target.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq));
            }
            replies.Add(GamePackets.NpcHp(target.Key, target.Hp));
            replies.AddRange(MapEventHp(target, now));
            if (target.Dead)
            {
                respawnAt[target.Key] = now + RespawnMs;  // HP 0 is the kill
                killedNpcs.Add(target.NpcId);
                replies.AddRange(AchieveKill(target.NpcId));
                replies.AddRange(BossChallengeKilled(target, now));
                InstanceKill(target, now);
                replies.AddRange(MapEventKilled(target, now));
                killedLevels.Add(target.Level);
                replies.AddRange(DropLoot(target, now));
                // Official order after HP 0: the mob's target cleared, the kill, its 0x8d21 off.
                replies.Add(GamePackets.TargetUpdate(target.Key));
                replies.Add(GamePackets.Kill(target.Key, skill, playerKey, ServerId, StoredName));
                replies.AddRange(ai.Killed(target));
            }
            else replies.AddRange(ai.Engage(targets, target, now, playerKey, hits.Sum(h => h.Amount)));    // a hit monster chases its attacker
        }
        return replies;
    }

    // ---------- Work from other threads (chat commands) ----------

    /// <summary>
    /// Runs <paramref name="action"/> on this connection's loop at its next tick (session state
    /// is single-threaded); the packets it returns are sent to this client.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<long, (T Result, List<byte[]> Packets)> action) =>
        InvokeAsync(now => Task.FromResult(action(now)));

    /// <summary>As above, for work that awaits (storage calls) while it holds the loop.</summary>
    public Task<T> InvokeAsync<T>(Func<long, Task<(T Result, List<byte[]> Packets)>> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        posted.Enqueue((async now =>
        {
            try
            {
                var (result, packets) = await action(now);
                done.SetResult(result);
                return packets;
            }
            catch (Exception error)
            {
                done.SetException(error);
                return [];
            }
        }, () => done.TrySetCanceled()));
        return done.Task;
    }

    /// <summary>Fails pending invocations when the connection ends.</summary>
    public void Abandon()
    {
        while (posted.TryDequeue(out var item)) item.Cancel();
    }

    /// <summary>Hands finished terrain/navmesh loads to the NPC AI (never waits); a failed load leaves NPCs on the slope model.</summary>
    void ApplyGeometry()
    {
        if (terrainLoad is { IsCompleted: true } terrain)
        {
            ai.Terrain = terrain.IsCompletedSuccessfully ? terrain.Result : null;
            if (terrain.IsFaulted) PersistenceErrors.Add("TerrainLoad");
            terrainLoad = null;
        }
        if (navLoad is { IsCompleted: true } nav)
        {
            ai.Nav = nav.IsCompletedSuccessfully ? nav.Result : null;
            if (nav.IsFaulted) PersistenceErrors.Add("NavMeshLoad");
            navLoad = null;
        }
    }

    /// <summary>NPCs killed by other NPCs (SetAggroNpc fights): the kill sequence without loot or experience; the map events count it.</summary>
    List<byte[]> NpcKillsByNpcs(NpcAi fights, long now)
    {
        var replies = new List<byte[]>();
        foreach (var (victim, killer) in fights.TakeNpcKills())
        {
            respawnAt[victim.Key] = now + RespawnMs;
            replies.Add(GamePackets.TargetUpdate(victim.Key));
            replies.Add(GamePackets.Kill(victim.Key, 0, killer.Key, 0, ""));
            replies.AddRange(fights.Killed(victim));
            replies.AddRange(MapEventKilled(victim, now));
        }
        return replies;
    }

    /// <summary>
    /// An NPC hit lands on the player: combat starts, HP drops (official 0x8d00 for the player's key).
    /// At HP 0 the player dies (see Die); invincible HP holds.
    /// </summary>
    internal List<byte[]> NpcHitsPlayer(int damage, long now, int killerKey = 0)
    {
        if (resources is null || dead) return [];
        var replies = new List<byte[]>();
        if (!inCombat)
        {
            inCombat = true;
            replies.Add(GamePackets.CombatState(playerKey, true));
        }
        combatUntil = now + CombatTimeoutMs;
        if (!invincible.Hp) resources.Hp = Math.Max(0, resources.Hp - damage);
        replies.Add(GamePackets.NpcHp(playerKey, resources.Hp));
        if (resources.Hp == 0) replies.AddRange(Die(killerKey, now));
        return replies;
    }

    // ---------- Death ----------

    bool dead;
    long reviveAt;
    public bool Dead => dead;
    /// <summary>OURS: a dead player who sends no respawn request is revived at the map's respawn point after this long.</summary>
    public const int ReviveDelayMs = 60_000;
    Vector3d? diedAt;

    /// <summary>
    /// The player died: NPCs drop it, casts are refused, and the kill (0x8d04, the layout of the
    /// official NPC kills; CANDIDATE for a player victim, usmap CharDie_NT has the same leading
    /// fields) goes to the player and those nearby. The client's respawn request is not known yet
    /// (it is logged as an unanswered opcode), so the server revives the player itself.
    /// </summary>
    List<byte[]> Die(int killerKey, long now)
    {
        dead = true;
        diving = false;
        if (copy is { } dungeonCopy) dungeonCopy.Deaths++;
        reviveAt = now + ReviveDelayMs;
        diedAt = position;
        inCombat = false;
        var kill = GamePackets.Kill(playerKey, 0, killerKey, ServerId, "", dieType: 2);
        worldPlayer?.Map?.Broadcast(worldPlayer, [kill]);
        return [.. ForceDismount(), .. ClearBuffs(), kill, GamePackets.CombatState(playerKey, false)];
    }

    /// <summary>
    /// 0x8d22 CharRespawn_RQ {ERespawnType u8, item uid i64}: Item (3) with a resurrection item revives in
    /// place and uses one up; otherwise the respawn point. Answered 0x8d23.
    /// </summary>
    async Task<List<byte[]>> RespawnRequestAsync(byte type, long itemUid, long now, CancellationToken ct)
    {
        if (!dead) return [RidePackets.RespawnReply(1)];
        if (type == RidePackets.RespawnItem)
        {
            if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(itemUid, out var row) || row.Location != "bag" || !IsRebirthItem(row.TemplateId))
                return [RidePackets.RespawnReply(1)];
            var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return [RidePackets.RespawnReply(1)];
            return [RidePackets.RespawnReply(0), .. await InventoryChangesAsync(ct, 0, DecreaseUseRebirthItem), .. Revive(now, inPlace: true)];
        }
        return [RidePackets.RespawnReply(0), .. Revive(now)];
    }

    /// <summary>0x8d24 UseRebirth_RQ {bool}: with a rebirth buff, true revives in place (0x8d26 to those nearby).</summary>
    List<byte[]> UseRebirth(bool use, long now)
    {
        if (!use) return [RidePackets.UseRebirthReply(0, false)];
        if (!dead || !CanRebirth) return [RidePackets.UseRebirthReply(1, false)];
        var reborn = RidePackets.Reborn(playerKey);
        worldPlayer?.Map?.Broadcast(worldPlayer, [reborn]);
        return [RidePackets.UseRebirthReply(0, true), .. UseRebirthBuff(), reborn, .. Revive(now, inPlace: true)];
    }

    const byte DecreaseUseRebirthItem = 85;   // EDecreaseReason kUseRebirthItem

    /// <summary>Revive: full HP/MP and a map load where the player fell (in place) or at the map's respawn point (else its start, else here).</summary>
    List<byte[]> Revive(long now, bool inPlace = false)
    {
        dead = false;
        if (resources is null) return [];
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        if (inPlace && diedAt is { } fell) return [resources.Update(), .. Teleport(worldMap, fell, now)];
        // Inside a dungeon: the running stage's revival point, else the dungeon's start (the reload keeps the events).
        var raceWord = profileRace == 2 ? "Dark" : "Light";
        var respawn = DungeonRevivalPoint() ?? data.MapPoints(worldMap).Where(p => p.Key.Contains("respawn", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Contains(raceWord, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault()
            ?? data.MapStart(worldMap)?.Position ?? position ?? spawn?.Position ?? GamePackets.SpawnPosition;
        return [resources.Update(), .. Teleport(worldMap, respawn, now)];
    }

    // ---------- Stats ----------

    /// <summary>MPMax of the official 0x3649 sample (level 1); CANDIDATE: the same for every class and level.</summary>
    public const int MpMaxBase = 1500;

    IEnumerable<ItemInfo> EquippedItems() =>
        inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Select(i => data.Item(i.ItemId)).OfType<ItemInfo>();

    int EquipItemLevel() => inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Sum(i => i.Detail?.ItemLevel ?? 0);

    /// <summary>The weapon in equip slot 1; development logins (no store) keep the class starter weapon.</summary>
    int? WeaponFromInventory() =>
        inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Equipped && i.Slot == 1)?.ItemId ?? (Store is null ? data.StarterWeapon(Profile()) : null);

    /// <summary>Defense against NPC hits: PcStatLevel Defense plus the equipped items' ArmorDefense.</summary>
    public int Defense => data.BaseStats(className, level).GetValueOrDefault("Defense") + EquippedItems().Sum(i => i.Stat("ArmorDefense")) + EnchantStat("ArmorDefense") + BuffStat("Defense")
        + BoardStat("Defense") + TitleStat("Defense");

    /// <summary>The MP pool: MPMax 1500 plus buffs.</summary>
    int MpMax => MpMaxBase + BuffStat("MPMax") + BoardStat("MPMax") + TitleStat("MPMax");

    /// <summary>
    /// The stat sheet (0x3649 / 0x364a): the official sample's values, with HPMax from PcStatLevel
    /// plus equipped HPMax, MPMax 1500, the weapon's damage/critical/accuracy and the armour's defense.
    /// </summary>
    (IReadOnlyList<(int Type, int Value)> Stats, int HpMax) StatSheet()
    {
        var gear = EquippedItems().ToList();
        var basis = data.BaseStats(className, level);
        var hpMax = Math.Max(1, (basis.GetValueOrDefault("HPMax") is > 0 and var hp ? hp : GamePackets.SpawnHpMax) + gear.Sum(i => i.Stat("HPMax")) + EnchantStat("HPMax") + RideStat("HPMax")
            + BuffStat("HPMax") + BoardStat("HPMax") + TitleStat("HPMax"));
        var weapon = data.WeaponStatsOf(equippedWeapon);
        var stats = GamePackets.DefaultStats.Select(s => s.Type switch
        {
            31 => (s.Type, weapon.Max + EnchantStat("WeaponDamage")), 33 => (s.Type, weapon.Min + EnchantStat("WeaponMinDamage")),
            128 => (s.Type, weapon.Critical + RideStat("Critical") + BuffStat("Critical") + BoardStat("Critical") + TitleStat("Critical")),
            318 => (s.Type, weapon.Accuracy), 193 => (s.Type, hpMax), 199 => (s.Type, MpMax), 242 => (s.Type, GamePackets.SpawnSp + BuffStat("SPMax")),
            307 => (s.Type, Defense),
            _ => s,
        }).ToList();
        return (stats, hpMax);
    }

    /// <summary>Applies the stat sheet to the pools (HP/MP clamped) and returns the updates for the client.</summary>
    List<byte[]> RefreshStats()
    {
        if (resources is null) return [];
        var (stats, hpMax) = StatSheet();
        resources.HpMax = hpMax;
        resources.MpMax = MpMax;
        resources.SpMax = GamePackets.SpawnSp + BuffStat("SPMax");
        resources.Hp = Math.Min(resources.Hp, hpMax);
        resources.Mp = Math.Min(resources.Mp, resources.MpMax);
        resources.Sp = Math.Min(resources.Sp, resources.SpMax);
        return [GamePackets.CharStatsUpdate(playerKey, stats, hpMax), resources.Maxima()];
    }

    // ---------- Builder command effects (call on this session's loop, in the world) ----------

    /// <summary>Builder SetInvisible: 0x364d to the client; NPCs stop seeing the player.</summary>
    public List<byte[]> SetInvisible(bool on)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        invisible = on;
        if (worldPlayer?.Map is { } map)
        {
            worldPlayer.Hidden = on;
            map.UpdateVisibility(worldPlayer);
        }
        return [GamePackets.InvisibleMode(on)];
    }

    public bool Invisible => invisible;

    async Task<List<byte[]>> BuilderAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (GamePackets.BuilderCmdRequest(plain) is not { } commands) return [];
        if (options.Builder is not { } builder) return [GamePackets.BuilderCmdResponse(1, commands, "Builder commands are disabled on this server.")];
        var replies = new List<byte[]>();
        var messages = new List<string>();
        var result = 0;
        foreach (var line in commands)
        {
            var outcome = await builder.RunAsync(this, line, now, ct);
            replies.AddRange(outcome.Packets);
            messages.Add(outcome.Message);
            if (result == 0) result = outcome.Result;
        }
        replies.Add(GamePackets.BuilderCmdResponse(result, commands, string.Join('\n', messages)));
        return replies;
    }

    /// <summary>Builder Hp/Mp/Sp: sets one resource, clamped to 0..max.</summary>
    public List<byte[]> SetResource(char kind, long value)
    {
        var r = resources ?? throw new InvalidOperationException("Not in the world.");
        switch (kind)
        {
            case 'H': r.Hp = (int)Math.Clamp(value, 0, r.HpMax); break;
            case 'M': r.Mp = (int)Math.Clamp(value, 0, r.MpMax); break;
            case 'S': r.Sp = (int)Math.Clamp(value, 0, r.SpMax); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return [r.Update()];
    }

    public (int Hp, int HpMax, int Mp, int MpMax, int Sp, int SpMax) Resource =>
        resources is { } r ? (r.Hp, r.HpMax, r.Mp, r.MpMax, r.Sp, r.SpMax) : default;

    /// <summary>Builder SetInvincible: flagged resources stay full (null leaves a flag as is).</summary>
    public List<byte[]> SetInvincible(bool? hp, bool? mp, bool? sp)
    {
        var r = resources ?? throw new InvalidOperationException("Not in the world.");
        invincible = (hp ?? invincible.Hp, mp ?? invincible.Mp, sp ?? invincible.Sp);
        Pin(r);
        return [r.Update()];
    }

    public (bool Hp, bool Mp, bool Sp) Invincible => invincible;

    /// <summary>What the staff overview shows of a player in the world (read under the world gate).</summary>
    public sealed record LiveView(string Name, Guid? CharacterId, string? StaffRole, int Level, string? ClassName,
        int? MapId, string? Area, Vector3d? Position, int Hp, int HpMax, int Mp, int MpMax, bool Invisible,
        string? PartyLeader, int PartySize, double Heading = 0, bool Dead = false, int SpeedRejects = 0);

    public LiveView Live()
    {
        var r = Resource;
        var party = worldPlayer is null ? null : Social?.PartyOf(worldPlayer);
        return new LiveView(StoredName, CharacterId, StaffRole, level, Profile()?.Class, MapId,
            MapId is { } map && position is { } at ? Subzones.AreaAt(map, at) : null, position,
            r.Hp, r.HpMax, r.Mp, r.MpMax, invisible, party?.Leader.Name, party?.Members.Count ?? 0, worldPlayer?.Heading ?? 0, dead, SpeedRejects);
    }

    /// <summary>Builder NoCool: casts ignore cooldowns.</summary>
    public bool NoCool
    {
        get => noCool;
        set
        {
            noCool = value;
            if (value) cooldowns.Clear();
        }
    }

    void Pin(Resources r)
    {
        if (invincible.Hp) r.Hp = r.HpMax;
        if (invincible.Mp) r.Mp = r.MpMax;
        if (invincible.Sp) r.Sp = r.SpMax;
    }

    /// <summary>
    /// Builder Teleport: map load (0x3621) to the position; the client then sends 0x3622 and
    /// gets the normal world entry. CANDIDATE: 0x3621 has only been used after selection.
    /// </summary>
    public List<byte[]> Teleport(int mapId, Vector3d at, long now, byte reason = 0, byte effect = 0, string cutscene = "")
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        BroadcastWarpLeave(effect, cutscene.Length > 0);
        // An instance layer or dungeon is a private copy (the entry set it; a staff move gets one too); elsewhere the shared map.
        instanceKey = !IsInstanceMap(mapId) ? 0 : instanceKey != 0 ? instanceKey : playerKey;
        if (!IsInstanceMap(mapId)) { instanceExitAt = null; instanceReturn = null; }
        moveReason = reason;
        moveEffect = effect;
        diving = false;
        surfaceZ = null;
        var ending = new List<byte[]>();
        if (mapId != worldMap)
        {
            HandOverCopy();   // a party copy's scripts stay with the players still inside
            if (!IsInstanceMap(mapId)) copy = null;
            activeChallenge = default;
            ending.AddRange(RemoveScriptedBuffs(null));   // a map event's buffs end with its map
            mapEventRuns.Clear();
            mapEventsMap = 0;
            pendingExit = null;
            // A move between maps of one level (an instance layer in or out): the client keeps the entities it has, so the
            // load removes them first (OBSERVED at the HQ001 entry: 0x3635 per object and 0x3642 flag 1 for Robin before 0x3623,
            // 0x3647 reason 1 for the rest after the burst). Live 2026-10-18: without it the base map's Robin stood in the layer.
            sameLevelMove = data.GeometryMap(mapId) == data.GeometryMap(worldMap);
            sameLevelRemovals = sameLevelMove ? SameLevelRemovals() : [];
            targets = null;  // the new map's NPCs appear on 0x3622
            respawnAt = [];
            if (options.World is not null) ai = new NpcAi(rng, data);
        }
        spawn = (mapId, at);
        position = at;
        reloading = true;
        // The official numbers the map loads of a connection 1, 2, 3 (capture 20261013: #244 world entry 1, #9655 the layer 2,
        // #14301 back 3); ours sent 1 every time. Live 2026-10-19: after a seamless layer move (reason 12) the client never
        // resumed play (no UpdateVisibleLen 0x3651, no movement) while a plain teleport worked: a repeated ticket is a suspect.
        loadTicket++;
        return [.. ending, GamePackets.PrepareEnterMap(loadTicket, mapId, at, now, reason: reason, effect: effect, cutscene: cutscene)];
    }

    /// <summary>The reason of the pending map load (12: an instance-layer move, followed by the map environment).</summary>
    byte moveReason;

    /// <summary>The map-load ticket of 0x3621, counted per connection from 1 at the world entry (OBSERVED).</summary>
    int loadTicket = 1;

    /// <summary>The teleport effect of the pending map load (0x41 with instance-layer moves); the own appearance repeats it.</summary>
    byte moveEffect;

    /// <summary>Removals of the entities shown on the map just left, sent at the next load when the level stays the same.</summary>
    List<byte[]> sameLevelRemovals = [];

    /// <summary>The pending map load keeps the level (an instance layer over the map just left, or the way back).</summary>
    bool sameLevelMove;

    List<byte[]> SameLevelRemovals()
    {
        var gone = new List<byte[]>();
        foreach (var i in shownObjects) gone.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
        foreach (var i in shownTriggers) gone.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstTriggerKey + i));
        foreach (var key in seenNpcs) gone.Add(GamePackets.Death(key, 1));
        if (worldPlayer is { } me) foreach (var key in me.Seeing) gone.Add(PlayerPackets.CharDisappear(key, 1));
        return gone;
    }

    // Messages other sessions queue for this player (hits, movement, chat) must not wait for this connection's next 50 ms
    // tick: the host loop also waits on this signal and ticks at once, so a PvP hit reaches the victim as fast as the
    // attacker's own reply.
    readonly SemaphoreSlim outboxWake = new(0, 1);

    /// <summary>Completes when another session queued messages for this player (one signal, however many messages).</summary>
    public Task OutboxWaitAsync(CancellationToken ct) => outboxWake.WaitAsync(ct);

    internal void WakeOutbox()
    {
        if (outboxWake.CurrentCount > 0) return;
        try { outboxWake.Release(); } catch (SemaphoreFullException) { }
    }

    // ---------- Shared world (multiplayer) ----------

    /// <summary>World entry with a shared world: join the map; others in range see this player appear.</summary>
    void EnterSharedWorld(int mapId, Vector3d at)
    {
        if (options.World is not { } world) return;
        worldPlayer ??= new WorldPlayer(this, playerKey) { Appear = OtherAppearance };
        worldPlayer.Position = at;
        worldPlayer.Hidden = invisible;
        worldPlayer.CharacterId = CharacterId;
        worldPlayer.Name = CharacterName ?? "";
        worldPlayer.Level = level;
        var arriving = !world.Online.ContainsKey(playerKey);
        world.Online[playerKey] = worldPlayer;
        if (arriving) TellFriends(true);
        var previous = worldPlayer.Map;
        world.Map(mapId, instanceKey).Join(worldPlayer);
        if (previous is not null && previous != worldPlayer.Map) world.Drop(previous);
    }

    /// <summary>Leaves the shared map (lobby, disconnect): others see this player disappear. Call under the world gate.</summary>
    public void ExitSharedWorld()
    {
        HandOverCopy();
        if (worldPlayer is not null && options.World?.Online.ContainsKey(playerKey) == true) LeaveSocial(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (worldPlayer?.Map is { } map) map.Leave(worldPlayer);
        if (worldPlayer is not null)
        {
            options.World?.Online.Remove(playerKey);
            while (worldPlayer.Outbox.TryDequeue(out _)) { }
        }
    }

    /// <summary>This player as others see it (0x3645 CharAppear).</summary>
    byte[] OtherAppearance()
    {
        var profile = Profile();
        return PlayerPackets.CharAppear(new OtherCharacter
        {
            Key = playerKey, Name = CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), PcId = profile?.PcId ?? 1,
            Race = profile?.Race ?? 1, Gender = character?.Gender ?? 1, Position = position ?? worldPlayer?.Position ?? default,
            Direction = worldPlayer?.Heading ?? 0, Hp = resources?.Hp ?? 1, HpMax = resources?.HpMax ?? 1, Mp = resources?.Mp ?? 0, MpMax = resources?.MpMax ?? 0,
            Level = level, WingId = wingId, TitleId = ShownTitle, Equips = VisualEquips(all: true), Abnormals = BuffWires(),
            EquipItemLevel = inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Sum(i => i.Detail?.ItemLevel ?? 0),
            MoveState = (byte)(moving ? 1 : 0), DeadReckoning = moving,
            CustomizingData = character?.CustomizingData ?? [], VoiceType = character?.VoiceType ?? 0, DbId = selected?.WireId ?? playerKey,
            Sprinting = sprinting, ToggleFlags = sealStoneOn ? 1 : 0, SealstoneItemId = SealStoneItemId,
        });
    }

    // EEquipSlot (stored equip slot) -> EVisualEquipSlot (what others see): weapon, armour, cape, jewellery.
    static readonly Dictionary<int, int> VisualSlot = new() { [1] = 1, [3] = 2, [4] = 3, [5] = 4, [6] = 5, [7] = 6, [8] = 7, [19] = 8, [10] = 9, [11] = 10, [12] = 11 };

    /// <summary>Equipped items by visual slot (the client's EquipBroad list for other players).</summary>
    Dictionary<int, int> VisualItems()
    {
        var items = new Dictionary<int, int>();
        foreach (var item in inventory.Values.Where(i => i.Container == InventoryItem.Equipped))
            if (VisualSlot.TryGetValue(item.Slot, out var visual)) items[visual] = item.ItemId;
        if (!items.ContainsKey(1) && Store is null && equippedWeapon is { } weapon) items[1] = weapon;
        return items;
    }

    /// <summary>0x561c for this player and those who see it: the visual slots whose item changed.</summary>
    byte[]? EquipChange(Dictionary<int, int> before, int equipItemLevel)
    {
        var after = VisualItems();
        var changes = new List<(byte, EquipBroad, EquipBroad)>();
        foreach (var slot in before.Keys.Union(after.Keys).Order())
        {
            int was = before.GetValueOrDefault(slot), now = after.GetValueOrDefault(slot);
            if (was == now) continue;
            EquipBroad Broad(int id) => new(id, VisualSlot: (byte)slot, Godstones: id == 0 ? [] : [0, 0, 0]);
            changes.Add(((byte)slot, Broad(was), Broad(now)));
        }
        return changes.Count > 0 ? PlayerPackets.ItemEquipChange(playerKey, equipItemLevel, changes) : null;
    }

    bool moving;
    (Vector3d At, long Ms)? lastMove;

    /// <summary>
    /// Running or walking as others should see it: the client sends no walk flag, so the speed
    /// between this player's ground updates decides (walk below RunThreshold cm/s; unknown: run).
    /// </summary>
    bool Running(Vector3d at)
    {
        var now = Environment.TickCount64;
        var running = true;
        if (lastMove is { } last && now - last.Ms is > 50 and < 2000)
        {
            var dx = at.X - last.At.X;
            var dy = at.Y - last.At.Y;
            running = Math.Sqrt(dx * dx + dy * dy) * 1000.0 / (now - last.Ms) >= RunThreshold;
        }
        lastMove = (at, now);
        return running;
    }

    /// <summary>Between the official walk (~200 cm/s) and run (~600 cm/s) speeds. CANDIDATE.</summary>
    public const double RunThreshold = 400;

    /// <summary>Casts other players see: the skill start and its effects, not the caster's own replies and bars.</summary>
    bool SharedCastPacket(byte[] packet) => Opcode(packet) is 0x3802 or 0x3804 or 0x3642 or 0x3641 or 0x8d21 or 0x8d04 or 0x382a
        || Opcode(packet) == 0x3835 && QuestKey(packet, 2) != playerKey
        || Opcode(packet) == 0x8d00 && QuestKey(packet, 2) != playerKey;   // NPC HP, not this player's bars

    /// <summary>
    /// What other players receive when this one moves: the client request mapped to the official
    /// broadcast (layouts from the client traces and the official capture, see PlayerPackets).
    /// 0x3700 start -> 0x371b, 0x3701 update -> 0x371c, 0x3718 stop -> 0x371a, 0x3702/0x3703 jump and
    /// fall -> 0x371e/0x371f, 0x3706/0x3707 glide -> 0x3723/0x3724, 0x370a/0x370b flight -> 0x3728/0x3729.
    /// Directions: the requests carry f32 degrees (move direction u16 on the ground).
    /// </summary>
    /// <summary>The last position sent in a parkour/rolling broadcast: the offsets of the next update are from it.</summary>
    Vector3d? offsetFrom;

    /// <summary>The air state a movement request implies (the flight gauge drains in the air and refills on the ground).</summary>
    void TrackAir(byte[] plain)
    {
        switch (Opcode(plain))
        {
            case 0x370a: air = AirState.Fly; flySprint = plain.Length > 2 && (plain[2] & 1) != 0; break;
            case 0x370b: air = AirState.Fly; break;
            case 0x3706 or 0x3707: air = AirState.Glide; break;
            case 0x3702 or 0x3703: air = AirState.Fall; break;
            case 0x3712 or 0x3713: air = AirState.WindPath; break;
            case 0x3700 or 0x3701 or 0x3718 or 0x370c or 0x370d or 0x370e or 0x370f or 0x3710 or 0x3711 or 0x3714 or 0x3715 or 0x3716 or 0x3717:
                air = AirState.Ground; break;
        }
        Resting = false;   // any movement stands the player up
    }

    List<byte[]> MovementRelay(byte[] plain, Vector3d at)
    {
        float F(int offset) => plain.Length >= offset + 4 ? BitConverter.ToSingle(plain, offset) : 0f;
        Vector3d V(int offset) => new(F(offset), F(offset + 4), F(offset + 8));
        static double Degrees(int u16) => u16 * 360.0 / 65536.0;
        var mask = plain.Length > 2 ? plain[2] : 0;
        byte[]? relay = null;
        double heading;
        switch (Opcode(plain))
        {
            case 0x3700 or 0x3701 when plain.Length >= 21:
                heading = F(17);
                relay = Opcode(plain) == 0x3700
                    ? PlayerPackets.MoveStart(playerKey, at, Degrees(BitConverter.ToUInt16(plain, 15)), heading, Running(at))
                    : PlayerPackets.MoveSnapshot(playerKey, at, Degrees(BitConverter.ToUInt16(plain, 15)), heading, Running(at));
                moving = true;
                break;
            case 0x3718 when plain.Length >= 20:
                heading = F(16);
                relay = PlayerPackets.MoveStop(playerKey, at, heading);
                moving = false;
                break;
            case 0x3702:
            {
                var o = 3 + ((mask & 1) != 0 ? 1 : 0) + 12;
                heading = F(o);
                relay = PlayerPackets.GravityStart(playerKey, at, heading, V(o + 4), (mask & 1) != 0 ? plain[3] : (byte)0, (mask & 4) != 0);
                break;
            }
            case 0x3703:
                heading = F(15);
                relay = PlayerPackets.GravitySnapshot(playerKey, at, heading, V(19));
                break;
            case 0x3706:
                heading = F(15);
                relay = PlayerPackets.GlideStart(playerKey, at, heading, V(19));
                break;
            case 0x3707:
                heading = F(14);
                relay = PlayerPackets.GlideSnapshot(playerKey, at, heading, V(18));
                break;
            // Swimming and diving (requests shaped like the glide ones: start = mask | pos | rotate f32 | velocity | mode u8 | time;
            // update = pos | rotate | velocity | ...): relayed as the usmap's 0x3736/0x3737/0x3739/0x373a (CANDIDATE, no official
            // sample). Nothing was relayed before: other players could not see a swimmer move.
            case 0x3714 when plain.Length >= 31:
                heading = F(15);
                relay = PlayerPackets.SwimStart(playerKey, at, heading, V(19), plain.Length > 31 ? plain[31] : (byte)0, sprinting);
                break;
            case 0x3715 when plain.Length >= 30:
                heading = F(14);
                relay = PlayerPackets.SwimSnapshot(playerKey, at, heading, V(18), sprinting);
                break;
            case 0x3716 when plain.Length >= 31:
                heading = F(15);
                relay = PlayerPackets.DiveStart(playerKey, at, heading, V(19), plain.Length > 31 ? plain[31] : (byte)0);
                break;
            case 0x3717 when plain.Length >= 30:
                heading = F(14);
                relay = PlayerPackets.DiveSnapshot(playerKey, at, heading, V(18));
                break;
            case 0x370a:
                heading = F(27);
                relay = PlayerPackets.FlyStart(playerKey, at, V(15), heading);
                break;
            case 0x370b:
                heading = F(26);
                relay = PlayerPackets.FlySnapshot(playerKey, at, V(14), heading);
                break;
            // Parkour (0x370c start: mask | pos | n points | move dir | rot | mode | [variation]; 0x370d update: pos |
            // move dir | rot | dr), forward-attack moves (0x370e/0x370f) and rolling (0x3710/0x3711), as broadcast officially.
            case 0x370c when plain.Length >= 16:
            {
                var n = plain[15];
                var o = 16 + n * 12;
                if (plain.Length < o + 7) return [];
                var points = Enumerable.Range(0, n).Select(i => V(16 + i * 12)).ToList();
                heading = F(o + 2);
                relay = PlayerPackets.ParkourStart(playerKey, at, points, BitConverter.ToUInt16(plain, o), heading, plain[o + 6],
                    (mask & 2) != 0 && plain.Length > o + 7 ? plain[o + 7] : null);
                offsetFrom = at;
                break;
            }
            case 0x370d or 0x3711 when plain.Length >= 21:
            {
                heading = F(16);
                static sbyte S(double v) => (sbyte)Math.Clamp(Math.Round(v), -127, 127);
                var d = at - (offsetFrom ?? at);
                var moveDir = Degrees(BitConverter.ToUInt16(plain, 14));
                relay = Opcode(plain) == 0x370d
                    ? PlayerPackets.ParkourOffset(playerKey, S(d.X), S(d.Y), S(d.Z), moveDir, heading)
                    : PlayerPackets.RollingOffset(playerKey, S(d.X), S(d.Y), S(d.Z), moveDir, heading, plain[20] != 0);
                offsetFrom = at;
                break;
            }
            case 0x3710 when plain.Length >= 21:
                heading = F(16);
                relay = PlayerPackets.RollingStart(playerKey, at, Degrees(BitConverter.ToUInt16(plain, 14)), heading, plain[20] != 0);
                offsetFrom = at;
                break;
            case 0x370e when plain.Length >= 53:
                heading = F(32);
                relay = PlayerPackets.ForwardAttackMoveStart(playerKey, BitConverter.ToInt32(plain, 3), plain[7], at, V(20), heading,
                    BitConverter.ToInt32(plain, 36) is var target && target != 0 ? target : playerKey, V(40), plain[52] != 0);
                break;
            case 0x370f when plain.Length >= 31:
                heading = F(27);
                relay = PlayerPackets.ForwardAttackMoveSnapshot(playerKey, at, V(15), heading, (mask & 2) != 0);
                break;
            // Wind stream ride: 0x3712 mask | pos | velocity | rotate f32 | path group i64 | node u16 | offset varint | [dr] | time;
            // 0x3713 pos | velocity | rotate f32 | node u16 | offset varint | dr | time (live 2026-10-24, 50 and 42 bytes).
            case 0x3712 when plain.Length >= 41:
                heading = F(27);
                relay = PlayerPackets.WindPathStart(playerKey, at, V(15), heading, BitConverter.ToInt64(plain, 31), BitConverter.ToUInt16(plain, 39));
                break;
            case 0x3713 when plain.Length >= 32:
                heading = F(26);
                relay = PlayerPackets.WindPathSnapshot(playerKey, at, V(14), heading, BitConverter.ToUInt16(plain, 30));
                break;
            default:
                return [];
        }
        if (worldPlayer is not null && double.IsFinite(heading)) worldPlayer.Heading = heading;
        return relay is null ? [] : [relay];
    }

    // ---------- Ticks ----------

    /// <summary>Timer-driven messages: regeneration, respawns, leaving combat, NPC movement; then the checkpoint.</summary>
    /// <summary>The shared map this player is in (changed only by this connection's own loop).</summary>
    public MapInstance? SharedMap => worldPlayer?.Map;

    /// <summary>
    /// Requests that are map work (see <see cref="SharedWorld"/>): position reports, pings and the cast brackets touch
    /// only this player, its map and the players on it, so they run with the map's lock instead of the whole world.
    /// </summary>
    public static bool IsMapRequest(byte[] plain) =>
        plain.Length >= 2 && (Opcode(plain) is 0x3602 || plain.Length == 2 && Opcode(plain) is 0x383a or 0x383c || PositionReport(plain) is not null);

    /// <summary>
    /// Whether this tick has world work due (call holding at least the map's turn): GM/chat work posted from other
    /// threads, completions that may teleport or reward (object use, item cast, revive, instance exit), and a duel's
    /// timers (they change the shared duel state). Otherwise the tick is map work: regeneration, buffs, NPC AI,
    /// respawns, visibility, party status (read-only), saves.
    /// </summary>
    public bool NeedsWorldTick(long now) =>
        !posted.IsEmpty
        || pendingUse is { } use && now >= use.DueMs
        || dead && now >= reviveAt
        || itemCast is { } cast && now >= cast.DueMs
        || instanceExitAt is { } exit && now >= exit
        || Social is { } s && worldPlayer is not null && s.DuelOf(worldPlayer) is { } duel && duel.Challenger == worldPlayer && now >= duel.NextMs
        || Social is { } s2 && worldPlayer is not null && ArenaOf(worldPlayer) is { } arena && arena.Owner == worldPlayer && (now >= arena.NextMs || arena.RespawnAt.Values.Any(r => now >= r));

    /// <summary>The time of the last request or tick (for work done from a request without its own clock).</summary>
    long lastNow;
    /// <summary>Set by a map load: the arrival counts like a position report on the next tick.</summary>
    bool arrivalCheckDue;

    public async Task<List<byte[]>> TickAsync(long now, CancellationToken ct = default)
    {
        lastNow = now;
        var replies = new List<byte[]>();
        while (posted.TryDequeue(out var item)) replies.AddRange(await item.Run(now));
        if (arrivalCheckDue && !reloading && position is { } arrivedAt)
        {
            // The arrival is a position too: event triggers, the area objectives (an EnterSubZone at the map's start: live
            // 2026-10-19 HQ1101020 completed 65 s after the arrival, at the player's first move), fog, and the quests that
            // start by themselves here.
            arrivalCheckDue = false;
            replies.AddRange(MapEventPosition(arrivedAt, now));
            fogCheckAt = 0;
            replies.AddRange(await UncoverFogAsync(arrivedAt, now, ct));
            replies.AddRange(await Stored(() => AutoAcquireAsync(ct)));
        }
        ApplyGeometry();
        replies.AddRange(FlushHeldDash(now));
        replies.AddRange(SprintTick(now));
        replies.AddRange(DodgeAbnormalTick(now));
        replies.AddRange(await AutoPotionAsync(now, ct));
        if (resources is not null)
        {
            var seconds = Math.Max(0, (now - resourceTickMs) / 1000);
            if (seconds > 0)
            {
                resourceTickMs += seconds * 1000;
                var before = (resources.Hp, resources.Mp, resources.Sp);
                var op = resources.Op;
                var underwater = diving && !dead;
                resources.Op = underwater ? (int)Math.Max(0, op - seconds * DiveDrain) : (int)Math.Min(OpMax, op + seconds * OpRegen);
                var fp = resources.Fp;
                if (resources.FpMax > 0 && !dead)
                {
                    if (air is AirState.Fly or AirState.Glide)
                        resources.Fp = (int)Math.Max(0, fp - seconds * (air == AirState.Glide ? GlideDrain : flySprint ? FlySprintDrain : FlyDrain));
                    else if (air == AirState.Ground)
                        resources.Fp = (int)Math.Min(resources.FpMax, fp + seconds * (inCombat ? FpRegenBattle : FpRegenRest));
                }
                // Minutes in a movement state for the ElapsedTime achievements naming it ("Glide for 10 minutes" = step 10;
                // Flying, Swimming likewise): the seconds accrue per state and count a minute at a time. WindPath and
                // springboard states are not tracked.
                if (!dead && (air == AirState.Glide ? "Glide" : air == AirState.Fly ? "Flying" : surfaceZ is not null ? "Swimming" : null) is { } moveState)
                {
                    var accrued = elapsedStateSeconds.GetValueOrDefault(moveState) + seconds;
                    for (; accrued >= 60; accrued -= 60) replies.AddRange(Achieve("ElapsedTime", 1, e => e.Has(moveState), now: now));
                    elapsedStateSeconds[moveState] = accrued;
                }
                var suffocating = underwater && op == 0;   // from the second after the gauge ran out
                // OURS: HP recovers 2%/s out of combat (no official rate recovered); not while out of breath. Sitting
                // (CharRestStart) triples HP and MP recovery (OURS too).
                var rest = Resting && !inCombat ? RestRegenFactor : 1;
                if (!inCombat && !suffocating) resources.Hp = (int)Math.Min(resources.HpMax, resources.Hp + seconds * rest * (Math.Max(1, resources.HpMax / 50) + BuffStat("HPRegen")));
                if (suffocating && !invincible.Hp)
                    resources.Hp = (int)Math.Max(0, resources.Hp - seconds * Math.Max(1L, (long)resources.HpMax * SuffocationRatio / 10000));
                resources.Mp = (int)Math.Min(resources.MpMax, resources.Mp + seconds * rest * (25 + BuffStat("MPRegen")));
                if (!sprinting)
                    resources.Sp = (int)Math.Min(resources.SpMax, resources.Sp + seconds * (inCombat ? SpRegenInCombat : SpRegenOutOfCombat));
                Pin(resources);
                // Invincible SP is resent each second: flight spends it on the client side.
                if (invincible.Sp || before != (resources.Hp, resources.Mp, resources.Sp)) replies.Add(resources.Update());
                if (resources.Op != op) replies.Add(resources.OpUpdate());
                if (resources.Fp != fp) replies.Add(resources.FpUpdate());
                if (resources.Fp == 0 && fp > 0 && air is AirState.Fly or AirState.Glide && position is { } here)
                {
                    // The gauge ran out: the official server starts the flyer's fall itself (0x371e with gravity mode 2, no jump;
                    // recording 2026-10-19 at FP 0, twice: after the flight and after the glide that followed).
                    air = AirState.Fall;
                    replies.Add(PlayerPackets.GravityStart(playerKey, here, worldPlayer?.Heading ?? 0, Vector3d.Zero, 2, jumpUp: false));
                }
                if (suffocating && resources.Hp == 0) replies.AddRange(Die(0, now));
            }
        }
        if (pendingUse is { } use && now >= use.DueMs)
        {
            pendingUse = null;
            replies.AddRange(await CompleteUseAsync(use.Index, ct, now));
        }
        if (dead && now >= reviveAt) replies.AddRange(Revive(now));
        if (itemCast is { } cast && now >= cast.DueMs) replies.AddRange(await CompleteItemCastAsync(now, ct));
        replies.AddRange(BuffTick(now));
        replies.AddRange(DropTick(now));
        replies.AddRange(MapEventTick(now));
        replies.AddRange(InstanceTick(now));
        // Map events cleared by a talk or a timer pay their quest objective here (kills pay on the cast).
        if (clearedEvents.Count > 0) replies.AddRange(await QuestKillsAsync(ct));
        if (pendingRewards.Count > 0) replies.AddRange(await GrantPendingRewardsAsync(ct));
        if (pendingQuestChanges.Count > 0)
        {
            var changed = pendingQuestChanges.ToList();
            pendingQuestChanges.Clear();
            replies.AddRange(await Stored(() => QuestChangesAsync(changed, ct)));
        }
        await FlushAchievementsAsync(now, ct);
        await FlushBossChallengeAsync(ct);
        // Play time: a minute at a time to the ElapsedTime achievements without a movement state (steps are minutes:
        // "Accumulate 3 hours of playtime" = 180). The ones naming states (Glide, Flying, Swimming) count their own minutes below.
        if (resources is not null && elapsedAchieveAt != 0 && now >= elapsedAchieveAt)
        {
            elapsedAchieveAt = now + 60_000;
            replies.AddRange(Achieve("ElapsedTime", 1, e => e.Values.Length == 0, now: now));
        }
        var respawned = RespawnTargets(now);
        worldPlayer?.Map?.BroadcastAll(respawned, worldPlayer);
        replies.AddRange(Visible(respawned));
        replies.AddRange(NpcVisibilityTick(now));
        if (inCombat && now >= combatUntil)
        {
            inCombat = false;
            replies.Add(GamePackets.CombatState(playerKey, false));
        }
        // An invisible GM is not seen: NPCs neither aggro nor keep chasing.
        if (worldPlayer?.Map is { } aiMap)
        {
            // One AI tick per map, by whichever player's loop ticks first; everyone on the map gets it.
            if (now >= aiMap.AiTickAt)
            {
                aiMap.AiTickAt = now + 50;
                aiMap.Ai.DefenseOf ??= key => aiMap.Players.TryGetValue(key, out var p) ? p.Session.Defense : 0;
                aiMap.BroadcastAll(aiMap.Ai.Tick(aiMap.Targets, aiMap.NpcVictims(), now, (key, damage) =>
                {
                    if (aiMap.Players.TryGetValue(key, out var victim)) victim.Send(victim.Session.NpcHitsPlayer(damage, now, aiMap.Ai.LastAttacker));
                    return [];
                }));
                var npcKills = NpcKillsByNpcs(aiMap.Ai, now);
                aiMap.BroadcastAll(npcKills, worldPlayer);
                replies.AddRange(npcKills);
            }
        }
        else if (targets is not null && (ai.DefenseOf ??= _ => Defense) is not null)
        {
            replies.AddRange(Visible(ai.Tick(targets, invisible || dead || FriendlyToAll ? null : position, now, damage => NpcHitsPlayer(damage, now, ai.LastAttacker))));
            replies.AddRange(NpcKillsByNpcs(ai, now));
        }
        PartyTick(now);
        DuelTick(now);
        ArenaTick(now);
        if (pendingAchievementPackets.Count > 0) { replies.AddRange(pendingAchievementPackets); pendingAchievementPackets.Clear(); }
        if (worldPlayer is not null)
            while (worldPlayer.Outbox.TryDequeue(out var queued)) replies.Add(queued);
        await CheckpointAsync(now, force: false, ct);
        return replies;
    }

    /// <summary>Saves a selected stored character's position every 30 s when it moved, or now (force).</summary>
    public async Task CheckpointAsync(long now, bool force, CancellationToken ct = default)
    {
        if (Store is null || selected is null || position is not { } at || at == savedPosition) return;
        if (!force && now < checkpointAt) return;
        checkpointAt = now + CheckpointMs;
        try
        {
            selected = await Store.SavePositionAsync(selected, worldMap, at, ct);
            savedPosition = at;
            Checkpoints++;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or GameplayException)
        {
            PersistenceErrors.Add(error.GetType().Name);
        }
    }

    // ---------- Request parsers ----------

    /// <summary>
    /// Client position from movement messages: 0x3700/0x3701 (x y z at byte 3), 0x3718 (byte 4),
    /// the jump/fall series 0x3702/0x3703 and the dash movement 0x370f (byte 3; 0x3702 has one more
    /// byte when its flag bit 0 is set), as in the live client traces of 2026-10-14.
    /// </summary>
    public static Vector3d? PositionReport(byte[] body)
    {
        if (body.Length < 3) return null;
        int offset = Opcode(body) switch
        {
            0x3700 or 0x3701 or 0x3703 or 0x370f or 0x370c => 3,
            // Swimming and diving (CharSwimming[Dive]Start_RQ: mask, _pos; the updates: _pos first; live samples 2026-10-19).
            0x3714 or 0x3716 => 3,
            0x3715 or 0x3717 => 2,
            // Parkour update and rolling: no mask, _pos first; forward-attack start: mask, skill, state, _pos.
            0x370d or 0x3710 or 0x3711 => 2,
            0x370e => 8,
            // Glide/flight starts carry a flag byte before _pos; their updates start with it (live samples 2026-10-15).
            0x3706 or 0x370a => 3,
            0x3707 or 0x370b => 2,
            0x3702 => (body[2] & 1) != 0 ? 4 : 3,
            0x3718 => 4,
            // Wind stream ride (dev 0x366f CharTrailWindPathStart_RQ: trail mode, _pos, ...; 0x3670 the update: _pos first;
            // live 2026-10-24 trace 232135: entered from a flight, 109 updates over 15 s, left gliding).
            0x3712 => 3,
            0x3713 => 2,
            _ => -1,
        };
        if (offset < 0 || body.Length < offset + 12) return null;
        var p = new Vector3d(BitConverter.ToSingle(body, offset), BitConverter.ToSingle(body, offset + 4), BitConverter.ToSingle(body, offset + 8));
        return p.IsFinite && Math.Abs(p.X) < 1e7 && Math.Abs(p.Y) < 1e7 && Math.Abs(p.Z) < 1e7 ? p : null;
    }

    /// <summary>0xe334: varint length | UTF-8 cutscene name | i64 group request key. Null when it does not parse.</summary>
    public static (string Name, long Key)? CutsceneComplete(byte[] plain)
    {
        int at = 2, length = 0, shift = 0;
        while (true)
        {
            if (at >= plain.Length || shift > 28) return null;
            var b = plain[at++];
            length |= (b & 0x7f) << shift;
            if (b < 0x80) break;
            shift += 7;
        }
        if (at + length + 8 > plain.Length) return null;
        return (System.Text.Encoding.UTF8.GetString(plain, at, length), BitConverter.ToInt64(plain, at + length));
    }

    /// <summary>0x3619: u8 length | name | u8 1. Empty when it does not parse.</summary>
    public static string NameRequest(byte[] body)
    {
        if (body.Length < 3 || body.Length < 3 + body[2]) return "";
        try { return new System.Text.UTF8Encoding(false, true).GetString(body, 3, body[2]); }
        catch (ArgumentException) { return ""; }
    }

    public sealed record CreateRequestData(string Name, int Gender, int CreationId, byte[] CustomizingData, int VoiceType, int SlotNo);

    /// <summary>0x361b CharCreate_RQ: nickname, gender, i32 creation ID, customizing bytes, voice, slot, skip tutorial.</summary>
    public static CreateRequestData? CreateRequest(byte[] body, bool strict)
    {
        if (body.Length < 4 || Opcode(body) != 0x361b) return null;
        try
        {
            var reader = new PacketReader(body) { Offset = 2 };
            var name = reader.String();
            int gender = reader.U8(), creation = (int)reader.U32();
            var customizing = reader.Bytes();
            int voice = reader.U8(), slot = reader.U8(), skip = reader.U8();
            if (strict && (!reader.End || skip > 1)) return null;
            return new CreateRequestData(name, gender, creation, customizing, voice, slot);
        }
        catch (Exception error) when (error is ProtocolException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>0x361f SelectCharacter_RQ: varint key, i32 map, Vec3 (map/position are not authoritative).</summary>
    public static (uint Key, int MapId, Vector3d Position)? SelectRequest(byte[] body)
    {
        if (body.Length < 6) return null;
        try
        {
            var reader = new PacketReader(body) { Offset = 2 };
            return (reader.Varint(), reader.I32(), reader.Vec3());
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>"account:guid" from a 0x3613 body (u8 length + string after the opcode); placeholders when absent.</summary>
    public static (string AccountSid, string Guid) LoginIdentity(byte[] body)
    {
        var payload = body.AsSpan(Math.Min(3, body.Length));
        var end = payload.IndexOf((byte)0);
        if (end > 0)
        {
            try
            {
                var parts = new System.Text.UTF8Encoding(false, true).GetString(payload[..end]).Split(':', 2);
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 && parts[0].All(char.IsAscii) && parts[1].All(char.IsAscii))
                    return (parts[0], parts[1]);
            }
            catch (ArgumentException) { }
        }
        return ("1000001", "00000000-0000-0000-0000-000000000000");
    }
}
