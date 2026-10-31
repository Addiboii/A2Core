using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed record GameSessionOptions
{
    /// <summary>Level of development characters; a selected stored character uses its own.</summary>
    public int CharacterLevel { get; init; } = 1;
    /// <summary>Live progression cap when constructing sessions without injected <see cref="GameData.WithLevelCap"/>.</summary>
    public int LevelCap { get; init; } = Aion.Core.ProgressionOptions.DefaultLevelCap;
    /// <summary>The login port's '$' placeholder row, when the development launch seeds one.</summary>
    public LoginCharacterRow? IntroCharacter { get; init; }
    public int WorldId { get; init; } = DevelopmentDefaults.WorldId;
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
    /// <summary>The race of the world this wire account picked on the login port (<see cref="WorldChoices"/>); null: none known.</summary>
    public Func<string, int?>? WorldRace { get; init; }
    /// <summary>Overrides Resurrect.dat auto-rez delay (development / tests use a short wait).</summary>
    public int? AutoResurrectMsOverride { get; init; }
    /// <summary>When true (default), companion/combat map-event AskNpc objectives can complete by proximity or lock-on when the client sends no talk UI.</summary>
    public bool MapEventAskNpcProximityFallback { get; init; } = true;
    /// <summary>Pass Shop / BM premium currency for this connection; null uses the host default from <see cref="GameSession.ConfigureShopOptions"/>.</summary>
    public ShopOptions? Shop { get; init; }
}

/// <summary>
/// One game connection after the handshake: decrypted client bodies in, plaintext server bodies
/// out, plus timer ticks. Port of tools/game_server.py game_followup_replies and its helpers
/// (casts, combat, NPC AI, checkpoints). With a store the account's stored characters are used
/// (authenticated launch); without one the development flow creates an unsaved character.
/// </summary>
public sealed partial class GameSession(GameData data, GameSessionOptions options, Random rng)
{
    readonly ShopOptions shopOptions = options.Shop ?? HostDefaultShopOptions;

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
    public const int FpMax = 250_000, FlyDrainPerTick = 1000, FlySprintDrainPerTick = 1500, GlideDrainPerTick = 750, FpRegenRest = 4_000, FpRegenBattle = 2_000;
    /// <summary>Dodges: CombatDodge (learned) and NormalDodge, its variant (Skill.dat: SP 10000, same 500 ms group cooldown).</summary>
    public const int CombatDodge = 11000100, NormalDodge = 11000101, NormalDodgeSp = 10000;
    /// <summary>
    /// Every class has its own dodge: class prefix * 1,000,000 + 100 (Skill.dat: 11000100 Gladiator ... 16000100, 18000100, all
    /// "Dodge", SP 30000, own group cooldown); the running dash is that id + 1. Live 2026-10-30: a class-16 character's sprint sent
    /// 16000100, which the 11000100-only check answered as an ordinary skill, and the client dropped the sprint 14 ms later.
    /// </summary>
    public static bool IsCombatDodgeId(int skill) => skill % 1_000_000 == 100 && skill / 1_000_000 is >= 11 and <= 19;
    public static bool IsDodgeId(int skill) => IsCombatDodgeId(skill) || IsCombatDodgeId(skill - 1);
    /// <summary>This character's CombatDodge (the learned class dodge; Gladiator's id when none is learned).</summary>
    int ClassCombatDodge => learned.FirstOrDefault(IsCombatDodgeId) is var id and > 0 ? id : CombatDodge;
    int ClassNormalDodge => ClassCombatDodge + 1;
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
        public int Ap, ApMax;
        public int Sp = GamePackets.SpawnSp, SpMax = GamePackets.SpawnSp;
        /// <summary>The Diving Gauge (OP): official stat sheet OPMax 100000, OPRegen 20000 (0x3649).</summary>
        public int Op = OpMax;
        /// <summary>The player's bars as the official server sends them (0x8d00; never 0x364c).</summary>
        public byte[] Update()
        {
            var ints = new List<(CharPoint, int)> { (CharPoint.Mp, Mp), (CharPoint.Sp, Sp) };
            if (ApMax > 0) ints.Add((CharPoint.Ap, Ap));
            return GamePackets.CharPoints(key, ints, [(CharPoint.Hp, Hp)]);
        }

        public byte[] ApUpdate() => ApMax <= 0 ? [] : GamePackets.CharPoints(key, [(CharPoint.Ap, Ap)]);
        public byte[] SpUpdate() => GamePackets.CharPoints(key, [(CharPoint.Sp, Sp)]);
        public byte[] OpUpdate() => GamePackets.CharPoints(key, [(CharPoint.Op, Op)]);
        public byte[] FpUpdate() => GamePackets.CharPoints(key, [(CharPoint.MaxFp, FpMax), (CharPoint.Fp, Fp)]);
        /// <summary>The world-entry maxima, as the official burst before 0x3633 (OP max 100000 observed).</summary>
        public int Fp, FpMax;
        public byte[] Maxima()
        {
            var ints = new List<(CharPoint, int)> { (CharPoint.Mp, Mp), (CharPoint.MaxMp, MpMax), (CharPoint.MaxSp, SpMax), (CharPoint.MaxOp, OpMax), (CharPoint.Fp, Fp), (CharPoint.MaxFp, FpMax) };
            if (ApMax > 0) { ints.Add((CharPoint.Ap, Ap)); ints.Add((CharPoint.MaxAp, ApMax)); }
            return GamePackets.CharPoints(key, ints, [(CharPoint.Hp, Hp), (CharPoint.MaxHp, HpMax)]);
        }
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
    /// <summary>Light (1) or Dark (2) for chat and social packets.</summary>
    public int ProfileRace => profileRace;
    long sprintTickAt, fpTickAt;
    /// <summary>The dodge in progress: its abnormal's uid (0: none), the skill and action uid of the 0x3806 (End: sent), and when they end.</summary>
    (int Uid, int Skill, int Action, long At, bool End)? dodgeAbnormalOff;
    /// <summary>NPC skill damage and debuffs are ignored until this time (dodge abnormal 200, DodgeAbnormalMs).</summary>
    long dodgeInvulnUntil;
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
    int autoAttackSkill;
    byte autoAttackActionUid;
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
    // The picked world's race (1 Light, 2 Dark): the lobby's _server_race and the only race it lists.
    int? worldRace;
    /// <summary>
    /// The selected character's staff role ("admin", "gamemaster"), else null: the account's role when the character is
    /// flagged staff (CharacterState.Staff, set by hand; a new character is a player even on a staff account).
    /// </summary>
    public string? StaffRole { get; private set; }
    /// <summary>Whether the character in play is staff (name tag, builder commands, the default wing, entry overrides).</summary>
    public bool IsAdmin => StaffRole is not null;
    /// <summary>Whether the account holds a staff role (staff-style names at creation and rename).</summary>
    public bool StaffAccount => accountRole is not null;
    /// <summary>The Login_RS _is_admin flag as sent (account role and a staff character on the account).</summary>
    public bool LobbyAdmin => accountRole is not null && lobbyStaff;
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
            // Every cast is bracketed by 0x383a/0x383c (u16 result replies). The player enters combat with the accepted cast
            // (EnterCombat) and leaves ~3.5 s after the last action, not on 0x383c.
            var replies = new List<byte[]> { GamePackets.RequestAck(header) };
            if (header == 0x383a && inCombat) combatUntil = now + CombatTimeoutMs;
            return replies;
        }
        if (header == 0x3602 && plain.Length >= 10) return [GamePackets.Ping(BitConverter.ToUInt64(plain, 2), now)];
        lastActivityMs = now;
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
            case 0x3622: return await LoadCompleteAsync(plain, now, ct);
            case 0x3630:
                // Logout_RQ (in-game Exit). Save first: the client disconnects on the reply.
                await FlushDeferredPersistenceAsync(now, ct);
                await CheckpointAsync(now, force: true, ct);
                return [GamePackets.Logout()];
            case 0x362e: return await BuilderAsync(plain, now, ct);
            case 0x3625 when plain.Length == 2: return await LobbyAsync(now, ct);
            case CharDeleteRq when plain.Length >= 3: return await DeleteCharacterAsync(plain, now, ct);
            case CharDeleteCancelRq when plain.Length >= 3: return await CancelDeleteAsync(plain, ct);
            case CharRenameRq when plain.Length >= 4: return await RenameCharacterAsync(plain, ct);
            case CharSlotSwapRq when plain.Length >= 4: return await SwapLobbySlotsAsync(plain, now, ct);
            case CharCustomizingListRq when plain.Length == 2: return await CharCustomizingListAsync(ct);
        }
        if (InWorld && ClientRequest(plain, now) is { } clientReplies) return clientReplies;
        if (InWorld && Opcode(plain) is var skinBlockOp && skinBlockOp >= 0x8d62 && skinBlockOp <= 0x8d67 && (skinBlockOp - 0x8d60) % 2 == 0)
            return SkinBlockRefusal(plain);
        if (InWorld && await GuildRequestAsync(plain, ct) is { } guildReplies) return guildReplies;
        if (InWorld && await PartyLootRequestAsync(plain, now, ct) is { } lootRollReplies) return lootRollReplies;
        if (InWorld && ForceRequest(plain, now) is { } forceReplies) return forceReplies;
        if (await SocialRequestAsync(plain, now, ct) is { } socialReplies) return socialReplies;
        if (InWorld && await TradeRequestAsync(plain, now, ct) is { Count: > 0 } tradeReplies) return tradeReplies;
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
            var wasAir = air;
            TrackAir(plain);
            TrackFallStart(wasAir, air, reported);
            uncovered = [.. uncovered, .. FallLandingReplies(wasAir, air, reported, now)];
            var entering = MapEventPosition(reported, now);
            entering.AddRange(InstanceTrigger(reported, Opcode(plain), now));
            if (worldPlayer?.Map is { } map)
            {
                worldPlayer.Position = reported;
                if (worldPlayer.NeedsVisibilityRefresh(reported, now)) map.UpdateVisibility(worldPlayer);
                map.Broadcast(worldPlayer, MovementRelay(plain, reported));
            }
            var abandonedHeld = AbandonQueuedHeldCast(now);
            return await WithMapEventClearsFlushedAsync([.. abandonedHeld, .. uncovered, .. entering], ct);
        }
        switch (header)
        {
            case 0x3800:
            {
                var cast = CastReplies(plain, now);
                LogSkillCastFromReplies(plain, cast);
                if (worldPlayer?.Map is { } castMap) castMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                cast.AddRange(await SealStoneConsumedAsync(ct));
                cast.AddRange(await QuestKillsAsync(ct));
                cast.AddRange(await KillExperienceAsync(ct));
                return await WithMapEventClearsFlushedAsync(cast, ct);
            }
            case SkillGroundReadyRq or SkillGroundStartRq or SkillGroundStopRq:
                return await GroundSkillAsync(header, plain, now, ct);
            // SkillChargingStart_RQ (dev 0x36C3 + 0x157): same wire as 0x3800; answered 0x381b then combat packets.
            case SkillChargingStartRq:
            {
                var cast = await HandleSkillChargingStartAsync(plain, now, ct);
                if (worldPlayer?.Map is { } chargeMap) chargeMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                return await WithMapEventClearsFlushedAsync(cast, ct);
            }
            case SkillChargingEndRq:
            {
                var cast = await HandleSkillChargingEndAsync(plain, now, ct);
                if (worldPlayer?.Map is { } releaseMap) releaseMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                return await WithMapEventClearsFlushedAsync(cast, ct);
            }
            case SkillChargingStopRq when plain.Length == 2:
                return HandleSkillChargingStop(plain);
            case BasicAtkRq:
            {
                var cast = HandleBasicAtkRequest(plain, now);
                if (worldPlayer?.Map is { } basicMap) basicMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                cast.AddRange(await SealStoneConsumedAsync(ct));
                return await WithMapEventClearsFlushedAsync(cast, ct);
            }
            case BasicAtkToggleOnRq when plain.Length == 2:
                return HandleBasicAtkToggleOn(now);
            case BasicAtkToggleOffRq when plain.Length == 2:
                return HandleBasicAtkToggleOff();
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
            case CoinShopOpenRq when plain.Length >= 6: return await OpenCoinShopAsync(plain, now, ct);
            case CoinShopBuyRq when plain.Length >= 6: return await BuyCoinShopAsync(plain, now, ct);
            case GuildShopBuyRq when plain.Length >= 4: return await BuyGuildShopAsync(plain, now, ct);
            case RandomShopInfoRq when plain.Length >= 6: return await RandomShopInfoAsync(plain, now, ct);
            case RandomShopPurchaseRq when plain.Length >= 10: return await RandomShopPurchaseAsync(plain, now, ct);
            case RandomShopRefreshRq when plain.Length >= 6: return await RandomShopRefreshAsync(plain, now, ct);
            case RandomShopRefreshCheckRq when plain.Length >= 6: return await RandomShopRefreshCheckAsync(plain, ct);
            case NPShopGameMoneyLimitRq when plain.Length >= 3: return await NPShopGameMoneyLimitAsync(plain, now, ct);
            case NPShopPurchaseRq when plain.Length >= 10: return await NPShopPurchaseAsync(plain, now, ct);
            case TrustLimitInfoRq: return TrustLimitInfo(now);
            case ItemRestoreRq when plain.Length >= 3: return await RestoreItemsAsync(plain, ct);
            case ItemEnchantRq when plain.Length >= 10: return await EnchantAsync(plain, ct);
            case ItemExceedEnchantRq when plain.Length >= 10: return await ExceedEnchantAsync(plain, ct);
            case ItemEnchantGodstoneRq when plain.Length >= 15: return await EnchantGodstoneAsync(plain, ct);
            case CombineGodstoneRq when plain.Length >= 3: return await CombineGodstoneAsync(plain, ct);
            case GatherStartRq when plain.Length >= 3: return await GatherStartAsync(plain, now, ct);
            case TitleChangeRq when plain.Length >= 6: return await TitleChangeAsync(plain, ct);
            case SkillLevelUpRq: return await SkillLevelUpAsync(plain, ct);
            case TitleEquipRq when plain.Length >= 7: return await TitleEquipAsync(plain, ct);
            case SkinEquipRq when plain.Length >= 3: return await SkinEquipAsync(plain, ct);
            case SkinExtractionRq when plain.Length >= 3: return await SkinExtractionAsync(plain, ct);
            case GatherStopRq when plain.Length >= 3: return GatherStop(plain);
            case CraftStartRq when plain.Length >= 7: return await CraftAsync(plain, ct);
            case ConversionStartRq: return await ConversionStartAsync(plain, ct);
            case ConversionLimitListRq when plain.Length >= 2: return await ConversionLimitListAsync(plain, ct);
            case ShopBuyRq when plain.Length >= 4: return await BuyGoodsAsync(plain, now, ct);
            case ShopSellRq when plain.Length >= 4: return await SellItemsAsync(plain, ct);
            case ItemRemoveRq when plain.Length >= 4: return await RemoveItemsAsync(plain, ct);
            case ItemSortRq when plain.Length == 7: return await SortBagAsync(plain, ct);
            case QuickSlotSetRq when plain.Length >= 27: return await SetQuickSlotAsync(plain, ct);
            case QuickSlotContextSkillRq when plain.Length >= 8: return await UpdateQuickSlotContextAsync(plain, ct);
            case SkillCustomizingUpdateRq: return await UpdateSkillCustomizingAsync(plain, ct);
            case QuickSlotResetRq when plain.Length == 3: return await ResetQuickSlotAsync(plain[2], ct);
            case QuickSlotContextSkillResetRq when plain.Length == 2: return await ResetQuickSlotContextAsync(ct);
            case RidePackets.OpenBoxRq when plain.Length == 15: return await OpenBoxRequestAsync(plain, now, ct);
            case RidePackets.ItemUseRq when plain.Length == 10: return await UseItemAsync(BitConverter.ToInt64(plain, 2), now, ct);
            case RidePackets.AcquireRq when plain.Length == 6: return await AcquireRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.CrystallizeRq when plain.Length >= 3: return await CrystallizeAsync(plain, ct);
            case RidePackets.BoardRollRq when plain.Length >= 5: return await RollBoardAsync(plain, ct);
            case RidePackets.BoardLockRq when plain.Length == 6: return await LockBoardSlotAsync(plain[2], plain[3], plain[4], plain[5] != 0, ct);
            case RidePackets.RespawnRq when plain.Length >= 3:
                return await RespawnRequestAsync(plain[2], plain.Length >= 11 ? BitConverter.ToInt64(plain, 3) : 0, now, ct);
            case RidePackets.UseRebirthRq when plain.Length == 3: return UseRebirth(plain[2] != 0, now);
            case RidePackets.LevelUpRq when plain.Length == 6: return await LevelUpRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.GetOnRq when plain.Length == 6: return GetOnRide(BitConverter.ToInt32(plain, 2));
            case RidePackets.GetOffRq when plain.Length == 3: return GetOffRide(plain[2] != 0);
            case RidePackets.DefaultSetRq when plain.Length == 6: return await SetDefaultRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case 0x562f when plain.Length == 10 && inventory.ContainsKey(BitConverter.ToInt64(plain, 2)):
                return await SoulBindCompleteAsync(BitConverter.ToInt64(plain, 2), ct);
            case 0x3704 when plain.Length == 10: return SprintOn(now);
            // Glide (0x3706) and flight (0x370a) starts, sent once a wing is equipped (live 2026-10-15).
            case 0x3706 when resources is not null && wingId != 0:
                if (!CanSpendFlightFp) return [GamePackets.GlideStartReply(playerKey, 1)];
                air = AirState.Glide;
                ResetFpTick(now);
                return [GamePackets.GlideStartReply(playerKey)];
            case 0x370a when resources is not null && wingId != 0:
                if (!CanSpendFlightFp) return [GamePackets.FlyStartReply(playerKey, 1)];
                air = AirState.Fly;
                flySprint = plain.Length > 2 && (plain[2] & 1) != 0;
                ResetFpTick(now);
                return FlyStart();
            case 0x3708 when plain.Length >= 11: return GlideBuoyancyOn(plain);
            case 0x3709 when plain.Length >= 10: return GlideBuoyancyOff(plain);
            case 0x3719 when plain.Length >= 27: return RequestAirPath(plain);
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
                    var replies = new List<byte[]> { GamePackets.SetMainTargetReply(target), GamePackets.TargetUpdate(playerKey, target) };
                    if (targets?.ContainsKey(target) == true) replies.Add(GamePackets.TargetUpdate(target));
                    return replies;
                }
                catch (ProtocolException) { return []; }
            // NPC interaction (dialog) and quest requests; layouts from the usmap, never yet seen from the client.
            case 0xe240 when NpcKeyFromBody(plain) is { } npcKey:
                return await WithMapEventClearsFlushedAsync(await NpcInteractionAsync(npcKey, now, ct), ct);
            case 0xe240:
                LogNpcTalkRefused(0, 0, null, "unparsed_body");
                return [];
            // InteractEnvObj_RQ {target_key, item_key, target_id} (usmap; client encoding unseen, logged).
            case 0x3636:
                return await WithMapEventClearsFlushedAsync(await InteractEnvObjAsync(plain, now, ct), ct);
            case CubeRewardRq: return await CubeRewardAsync(plain, now, ct);
            case PostLoadRq when plain.Length >= 3: return await LoadPostsAsync(plain, ct);
            case PushRewardInfoRq when plain.Length == 2: return await RetrievePushRewardsAsync(now, ct);
            case TakePushRewardRq when plain.Length >= 8: return await TakePushRewardAsync(plain, ct);
            case PostReadRq: return await MarkPostsAsync(plain, delete: false, ct);
            case PostDeleteRq: return await MarkPostsAsync(plain, delete: true, ct);
            case PostTakeRq when plain.Length >= 11: return await TakePostAsync(plain, ct);
            case ItemDecomposeRq when plain.Length >= 3: return await DecomposeAsync(plain, ct);
            case TakeAchievementRewardRq: return await TakeAchievementRewardAsync(plain, now, ct);
            case TakeAchievementGroupRewardRq: return await TakeGroupRewardAsync(plain, ct);
            case TakeAchievementRewardBatchRq: return await TakeRewardBatchAsync(plain, now, ct);
            case AttendanceRq: return Attendance(now);
            case TakeBattlePassRewardRq or LegacyTakeBattlePassRewardRq: return await TakeBattlePassRewardAsync(plain, now, ct);
            case MatchingStartRq: return MatchingStart(plain, now);
            case MatchingStartResponseRq: return MatchingStartResponse(plain, now);
            case MatchingCancelRq: return MatchingCancel(now);
            case ArenaDungeonReadyRq: return ArenaReady(plain, now);
            case ArenaRecordRq when plain.Length >= 4: return [PacketWriter.Opcode(ArenaRecordRs).U16(0).I32(plain[2]).U8(plain[3]).Varint(0).ToArray()];
            case PeriodCollectionListRq: return PeriodCollectionListRequest(plain, now);
            case RegisterPeriodCollectionRq when plain.Length >= 11: return await RegisterCollectionAsync(plain, now, ct);
            case DaevanionOpenNodeRq when plain.Length >= 6: return await DaevanionOpenNodeAsync(plain, ct);
            case DaevanionCloseNodeRq when plain.Length >= 6: return await DaevanionCloseNodeAsync(plain, ct);
            case DaevanionResetBoardRq when plain.Length >= 6: return await DaevanionResetBoardAsync(plain, ct);
            case DaevanionOpenAllNodeRq when plain.Length >= 6: return await DaevanionOpenAllNodeAsync(plain, ct);
            case DaevanionOpenRouteNodeRq when plain.Length >= 6: return await DaevanionOpenRouteNodeAsync(plain, ct);
            case BossChallengeEnterRq: return BossChallengeEnter(plain, now);
            case PartyDungeonEnterRq: return PartyDungeonEnter(now);
            case DungeonRoomListRq: return DungeonRoomList(plain);
            case DungeonRoomCreateRq: return DungeonRoomCreate(plain, now);
            case DungeonRoomJoinRq: return DungeonRoomJoin(plain, now);
            case DungeonRoomApplyResponseRq: return DungeonRoomApplyResponse(plain, now);
            case DungeonRoomInviteRq: return DungeonRoomInvite(plain, now);
            case DungeonRoomInviteResponseRq: return DungeonRoomInviteResponse(plain, now);
            case DungeonRoomInstantJoinRq: return DungeonRoomInstantJoin(plain, now);
            case DungeonRoomReadyRq: return DungeonRoomReady(plain);
            case DungeonRoomEditSettingRq: return DungeonRoomEditSetting(plain);
            case DungeonRoomLeaveRq: return DungeonRoomLeave();
            case DungeonRoomKickMemberRq: return DungeonRoomKick(plain);
            case DungeonRoomStartRq: return DungeonRoomStart(now);
            case DungeonRoomRetryRq: return DungeonRoomRetry(now);
            case DungeonRoomRetryResponseRq: return DungeonRoomRetryResponse(plain, now);
            case BattlePassPurchaseRq or LegacyBattlePassPurchaseRq: return await BattlePassPurchaseAsync(plain, now, ct);
            case CloseNpcAchievementRq: return CloseNpcAchievement(plain);
            case CloseEnvObjAchievementRq: return CloseEnvObjAchievement(plain);
            case SkillLevelDownRq: return await SkillLevelDownAsync(plain, ct);
            case SkillLevelResetRq: return await SkillLevelResetAsync(plain, ct);
            // InteractEnvObjStop_RQ {key}: a timed interaction cancelled (moved, or the client's cancel).
            case 0x363d when QuestKey(plain, 2) is { } stopKey:
                if (pendingUse?.Key == stopKey) pendingUse = null;
                return [GamePackets.InteractEnvObjStopReply(stopKey)];
            case 0xe242 when NpcKeyFromBody(plain) is { } npcKey:
                // Once the conversation is over on the server's side (the partner removed and a map load pending, as after an
                // NPC's layer entry) the official answer is the failure 0x3e08 with key 0 (OBSERVED #9656); the talk mode ends on it.
                if (reloading || targets?.ContainsKey(npcKey) != true) return [GamePackets.FinishNpcInteractionReply(0, FinishNoConversation)];
                return await WithMapEventClearsFlushedAsync(
                    [GamePackets.FinishNpcInteractionReply(npcKey), .. MapEventTalked(npcKey, null, close: true, now), .. await NpcInteractionFinishedAsync(npcKey, now, ct)], ct);
            // NpcConversation_RQ {npc key, func value}: the dialog choice of an EnterInstanceLayer NPC (OBSERVED reply 0x4007 before the move).
            case 0x4006 when plain.Length >= 7: return NpcConversation(plain, now);
            case 0xe244 when NpcKeyFromBody(plain) is { } npcKey: return [GamePackets.KeepAliveNpcInteractionReply(npcKey, now + 300_000)];
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
            // QuestTeleport_RQ {quest uid i32} (live 2026-10-30: 0x8d16 31000000 on the journal's "Go to the quest area?" confirm).
            case 0x8d16 when plain.Length >= 6: return QuestTeleport(BitConverter.ToInt32(plain, 2), now);
            case 0x8d0e when plain.Length >= 6:
                return await QuestRewardAsync(BitConverter.ToInt32(plain, 2),
                    plain.Length >= 10 ? BitConverter.ToInt32(plain, 6) : -1, now, ct);
            case 0x8d2b when plain.Length >= 6: return await QuestStepTalkAsync(plain, now, ct);
            // ClientObjectiveComplete_RQ {kind u8, id i32, objective index i32} (11 bytes live; the official reply mirrors it).
            case 0x9230 when plain.Length >= 11:
                return await ClientObjectiveAsync(plain[2], BitConverter.ToInt32(plain, 3), BitConverter.ToInt32(plain, 7), now, ct);
            // UseTeleportArtifact_RQ {i32 artifact} (usmap; live 2026-10-30 `36929B091000` from the world map's teleport confirm).
            case 0x9236 when plain.Length >= 6: return await UseTeleportArtifactAsync(BitConverter.ToInt32(plain, 2), now, ct);
            // CutsceneComplete_RQ {cutscene name, group request key i64}: a cutscene the client was told to play ended (OBSERVED
            // live 2026-10-19 for the lake cave's exit cutscene). It finishes CompleteCutscene objectives and releases a move
            // that waited for its cutscene (a PCTeleport's value 3).
            case 0xe334 when CutsceneComplete(plain) is { } ended:
                return await WithMapEventClearsFlushedAsync(MapEventCutsceneDone(ended.Name, ended.Key, now), ct);
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
                return [GamePackets.LoginCharacters([], $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", DevelopmentDefaults.WorldIdString,
                    GamePackets.CommServerKey, now, isBornServer: true, chatEnableLevel: 0)];
            case 0xe34f when Store is not null && !lobbyLoginSent:
                lobbyLoginSent = true;
                return await Stored(async () =>
                {
                    await EnsureExtendedCharacterSlotsAsync(ct);
                    return
                    [
                        GamePackets.LoginCharacters([], $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", Store.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            GamePackets.CommServerKey, now, isBornServer: true, chatEnableLevel: 0, extendedSlotCount: ExtendedCharacterSlotCount),
                        GamePackets.ExtendedCharacterSlot(ExtendedCharacterSlotCount),
                    ];
                });
        }
        if (GamePackets.WorldEntryQueryReplies.TryGetValue(header, out var reply))
            return [reply];
        return [];
    }

    /// <summary>
    /// 0x3625 Lobby_RQ (back to character selection): save, leave the world, answer 0x3626 with
    /// the account's characters as stored now (class, level and ascension changes show).
    /// Development logins store nothing and get an empty list (the client offers creation).
    /// </summary>
    async Task<List<byte[]>> LobbyAsync(long now, CancellationToken ct)
    {
        await FlushDeferredPersistenceAsync(now, ct);
        await CheckpointAsync(now, force: true, ct);
        LeaveWorld();
        if (Store is null) return [GamePackets.LobbyCharacters([])];
        // The plain (uncompressed) full list. Settled live 2026-10-19 from the client recordings of the four research
        // variants: after the plain full list the player picked a character 6 s later; the compressed one made the client
        // drop the connection and log in again; the empty ones showed no characters.
        return await Stored(async () =>
        {
            await EnsureExtendedCharacterSlotsAsync(ct);
            return LobbyCharacterReplies(await LobbyCharactersAsync(ct));
        });
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
        ClearChainState();
        skillCustomizing.Clear();
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


    // ---------- Sprint (Shift): 0x3704 on / 0x3705 off; SP drains while it lasts ----------

    List<byte[]> SprintOn(long now)
    {
        if (resources is null) return [];
        sprinting = resources.Sp > 0;
        sprintTickAt = now + SprintTickMs;
        ResetFpTick(now);
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
        dodgeInvulnUntil = now + DodgeAbnormalMs;
        var replies = new List<byte[]>();
        var buff = AddBuff(DodgeAbnormal, DodgeAbnormalMs, skill, now, replies, path: SelfAbnormalPath.Dodge);
        dodgeAbnormalOff = (buff?.Uid ?? 0, skill, action, endAt, end);
        return replies.Count > 0 ? replies[0]
            : PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, DodgeAbnormal, DodgeAbnormalMs, now + DodgeAbnormalMs,
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
            var effect = GamePackets.SkillEffect(playerKey, playerKey, ClassCombatDodge, fx.Action, ClassCombatDodge * 100 + DodgeEffectOffset);
            worldPlayer?.Map?.Broadcast(worldPlayer, [effect]);
            replies.Add(effect);
            AddBuff(DodgeAfterAbnormal, DodgeAfterMs, ClassCombatDodge, now, replies, path: SelfAbnormalPath.Dodge);
            replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources.Sp)]));
        }
        if (dodgeAbnormalOff is { } off && now >= off.At)
        {
            dodgeAbnormalOff = null;
            var ended = new List<byte[]>();
            if (off.End) ended.Add(GamePackets.SkillEnd(playerKey, off.Skill, off.Action));
            if (off.Uid != 0)
            {
                if (buffs.FirstOrDefault(b => b.Uid == off.Uid) is { } dodgeBuff) ended.AddRange(RemoveBuffs([dodgeBuff], DodgeAbnormalOffReason));
                else ended.Add(PlayerPackets.AbnormalRemove(playerKey, [(off.Uid, DodgeAbnormalOffReason)]));
            }
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

    void ResetFpTick(long now) => fpTickAt = now + SprintTickMs;

    /// <summary>Flight FP drain every 100 ms while flying or gliding (OBSERVED: 1000 / 1500 / 750 per tick).</summary>
    List<byte[]> FlightFpTick(long now)
    {
        if (resources is null || dead || resources.FpMax <= 0 || air is not (AirState.Fly or AirState.Glide)) return [];
        if (fpTickAt <= 0) ResetFpTick(now);
        var replies = new List<byte[]>();
        var drained = false;
        for (var ticks = 0; ticks < 200 && now >= fpTickAt; ticks++)
        {
            fpTickAt += SprintTickMs;
            if (invincible.Sp) continue;
            var drain = air == AirState.Glide ? GlideDrainPerTick : flySprint ? FlySprintDrainPerTick : FlyDrainPerTick;
            resources.Fp = Math.Max(0, resources.Fp - drain);
            drained = true;
            if (resources.Fp == 0) break;
        }
        if (drained) replies.AddRange(FpWireUpdates());
        if (resources.Fp == 0) replies.AddRange(EndFlightFromEmptyFp());
        return replies;
    }

    /// <summary>0x8d00 FP + MaxFP and the flight gauge stats (EStat FlyCost 247 current, FPMax 298).</summary>
    List<byte[]> FpWireUpdates()
    {
        if (resources is null || resources.FpMax <= 0) return [];
        return [resources.FpUpdate(), FpStatsUpdate()];
    }

    /// <summary>Partial 0x364a with FlyCost (247) and FPMax (298) for the arc gauge.</summary>
    byte[] FpStatsUpdate() => GamePackets.CharStatsUpdate(playerKey, [(247, resources!.Fp), (298, resources.FpMax)], resources.HpMax);

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
    /// Unassigned rows keep the slot the client already shows them in (<paramref name="shown"/>), then a
    /// hinted slot (<paramref name="hints"/>), so one move changes only the moved items, as the official.
    /// </summary>
    internal static List<InventoryItem> Layout(IReadOnlyList<ItemState> items, long owner = 0, GameData? data = null,
        IReadOnlyDictionary<long, InventoryItem>? shown = null, IReadOnlyDictionary<long, int>? hints = null)
    {
        var result = new List<InventoryItem>();
        // Free slots are laid out per container: the bag (1) and the warehouse (2, location 'storage').
        var used = new Dictionary<string, HashSet<int>> { ["bag"] = [], ["storage"] = [] };
        foreach (var i in items.Where(i => i.Location is "bag" or "storage" && i.Slot is not null)) used[i.Location].Add(i.Slot!.Value);
        var kept = new Dictionary<long, int>();
        foreach (var i in items.Where(i => i.Location is "bag" or "storage" && i.Slot is null))
        {
            var container = i.Location == "storage" ? InventoryItem.Storage : InventoryItem.Bag;
            if (shown?.GetValueOrDefault(WireKey(i)) is { } was && was.Container == container && used[i.Location].Add(was.Slot))
                kept[WireKey(i)] = was.Slot;
        }
        foreach (var i in items.Where(i => i.Location is "bag" or "storage" && i.Slot is null && !kept.ContainsKey(WireKey(i))))
            if (hints?.GetValueOrDefault(WireKey(i), -1) is >= 0 and var hint && used[i.Location].Add(hint))
                kept[WireKey(i)] = hint;
        var next = new Dictionary<string, int> { ["bag"] = 0, ["storage"] = 0 };
        foreach (var item in items.Where(i => i.Location is "bag" or "equipment" or "storage")
                     .OrderBy(i => i.Location == "equipment" ? 0 : 1).ThenBy(i => i.Slot ?? int.MaxValue).ThenBy(i => i.WireId))
        {
            var equipped = item.Location == "equipment";
            int slot;
            if (equipped) slot = item.Slot ?? 1;
            else if (item.Slot is { } fixedSlot) slot = fixedSlot;
            else if (kept.TryGetValue(WireKey(item), out var keptSlot)) slot = keptSlot;
            else
            {
                while (used[item.Location].Contains(next[item.Location])) next[item.Location]++;
                used[item.Location].Add(slot = next[item.Location]);
            }
            var key = item.WireId > 0 ? item.WireId : BitConverter.ToInt64(item.InstanceId.ToByteArray(), 0) & long.MaxValue;
            var equipment = equipped || data?.Item(item.TemplateId) is not null;
            var container = equipped ? InventoryItem.Equipped : item.Location == "storage" ? InventoryItem.Storage : InventoryItem.Bag;
            result.Add(new InventoryItem(key, item.TemplateId, item.Quantity, container, slot,
                equipment ? new EquipDetail((data?.ItemLevel(item.TemplateId) ?? 1) + Enchants.Effect(item.TemplateId, EnchantOf(item)).ItemLevel, equipped ? (byte)slot : (byte)0,
                    (byte)EnchantOf(item), SoulBindCount(item) > 0 ? owner : 0, SoulBindCount(item),
                    (byte)EnchantOf(item, "surpass_enchant"), EnchantOf(item, "enchant_bonus"), EnchantOf(item, "surpass_enchant_bonus"), GodstonesOf(item),
                    RolledWireStats(item.RolledStats),
                    (byte)Math.Clamp(SkinExtractionUsed(item), 0, 255)) : null));
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

    async Task<List<InventoryItem>> LoadInventoryAsync(INativeCharacterStore store, Guid character, CancellationToken ct, IReadOnlyDictionary<long, int>? hints = null)
    {
        var rows = await store.InventoryAsync(character, ct);
        storedItems.Clear();
        foreach (var row in rows) storedItems[WireKey(row)] = row;
        await EnsureRolledStatsAsync(store, character, ct);
        var result = Layout(storedItems.Values.ToList(), selected?.WireId ?? 0, data, inventory, hints);
        await RefreshAccountQunaBalanceAsync(ct);
        return result;
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
            // A swap puts the old piece in the bag slot the new one left.
            Dictionary<long, int>? hints = null;
            if (equip && inventory.GetValueOrDefault(key) is { Container: InventoryItem.Bag } from
                && inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Equipped && i.Slot == slot && i.Key != key) is { } old)
                hints = new() { [old.Key] = from.Slot };
            replies.AddRange(await InventoryChangesAsync(ct, 24, 0, hints));
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
                equippedWeapon = WeaponFromInventory();
                var level = EquipItemLevel();
                return [.. InventoryDepotSyncPackets(), QunaRefreshNow()];
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
    async Task<List<byte[]>> InventoryChangesAsync(CancellationToken ct, byte increase, byte decrease, IReadOnlyDictionary<long, int>? hints = null)
    {
        if (Store is not { } store || CharacterId is not { } character) return [];
        var beforeQuna = QunaBalance();
        var items = await LoadInventoryAsync(store, character, ct, hints);
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
        await RefreshAccountQunaBalanceAsync(ct);
        var quest = await SyncQuestCollectItemsAsync(ct);
        var qunaRefresh = AppendQunaRefreshIfNeeded(beforeQuna);
        if (changes.Count == 0)
            return qunaRefresh.Count == 0
                ? (quest.Count == 0 ? acquired : [.. acquired, .. quest])
                : [.. acquired, .. quest, .. qunaRefresh];
        return [GamePackets.ItemModificationList(changes), .. acquired, .. quest, .. qunaRefresh];
    }

    List<byte[]> UnlockUpdateOnce()
    {
        if (unlockUpdateSent) return [];
        unlockUpdateSent = true;
        return [GamePackets.ContentsUnlockUpdate()];
    }

    StarterProfile? Profile() => character is null ? data.Profile(1, 1) : data.Profile(character.CreationId, character.Gender);

    /// <summary>A race's starting map: Poeta (1000) for Light, Ishalgen (1100) for Dark (tools/configure_starters.py).</summary>
    static int HomeMapOf(int race) => race == 2 ? 1100 : 1000;
    int HomeMap => HomeMapOf(Profile()?.Race ?? 1);

    async Task<List<byte[]>> GameLoginAsync(byte[] plain, long now, CancellationToken ct)
    {
        loginIdentity = LoginIdentity(plain);
        worldRace = options.WorldRace?.Invoke(loginIdentity.AccountSid);
        if (Store is not null)
            return await Stored(async () =>
            {
                await EnsureExtendedCharacterSlotsAsync(ct);
                var characters = await LobbyCharactersAsync(ct);
                var races = characters.Select(c => c.Race).Distinct().ToList();
                accountRole = await StaffRoleAsync(Store.AccountId, ct);
                // _server_race: the picked world's race (each world holds one race, as an official server), else the race
                // of the account's characters (0 when none, CANDIDATE "not chosen"; a new account then saw its own race's
                // text scrambled).
                return
                [
                    NativeFraming.CompressedEnvelope(GamePackets.LoginCharacters(characters,
                        $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", Store.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        GamePackets.CommServerKey, now, serverRace: worldRace ?? (races.Count == 1 ? races[0] : 0), isAdmin: LobbyAdmin,
                        extendedSlotCount: ExtendedCharacterSlotCount)),
                    GamePackets.ExtendedCharacterSlot(ExtendedCharacterSlotCount),
                ];
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
        var lobbyStates = new List<CharacterState>();
        foreach (var state in await Store!.LobbyAsync(ct))
        {
            if (state.Staff) lobbyStaff = true;
            if (worldRace is { } race && state.Race != race) continue;
            lobbyStates.Add(state);
        }
        foreach (var (state, slotNo) in LobbySlotLayout(lobbyStates, LobbySlotCapacity))
        {
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
                SlotNo = slotNo,
                CustomizingData = state.Appearance,
                LogoutTime = state.LobbyLogoutWireMs, RegTime = state.RegisteredUnixMs,
                PendingDeleteTime = deletions.TryGetValue(state.Summary.CharacterId, out var deleteAt) ? deleteAt.ToUnixTimeMilliseconds() : 0,
                LastNicknameChangeTime = renames.TryGetValue(state.Summary.CharacterId, out var renamedAt) ? renamedAt.ToUnixTimeMilliseconds() : 0,
            });
        }
        // Stable list order (creation / wire id): slot moves update _slot_no only. Re-sorting by slot cleared pedestal
        // models on the client while labels still tracked SlotNo (live 2026-10-31 after 0xe336).
        return records.OrderBy(c => c.RegTime).ThenBy(c => c.Key).ToList();
    }

    StarterProfile StoredProfile(CharacterState state)
    {
        var profile = data.Profile(state.CreationId, state.Gender);
        return profile is not null && profile.Race == state.Race ? profile : throw new ArgumentException("Persisted character profile is unsupported");
    }

    /// <summary>Short label for persistence failures (native listener logs these on store refusals).</summary>
    internal static string PersistenceErrorLabel(Exception error)
    {
        if (error is DbException && error.GetType().Name == "PostgresException")
        {
            var sqlState = error.GetType().GetProperty("SqlState")?.GetValue(error) as string;
            var messageText = error.GetType().GetProperty("MessageText")?.GetValue(error) as string;
            if (sqlState is not null)
                return $"PostgresException {sqlState} {messageText ?? error.Message}";
        }
        return error.GetType().Name;
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
                                          or System.Text.Json.JsonException or KeyNotFoundException or DbException)
        {
            PersistenceErrors.Add(PersistenceErrorLabel(error));
            return [];
        }
    }

    /// <summary>
    /// A creation of the other race than the account's characters: one race per account on this server (an official
    /// server holds one race; this one holds both), so the other race needs every character of this one deleted first.
    /// </summary>
    static bool OtherRace(IReadOnlyList<CharacterState> lobby, int race)
    {
        var races = lobby.Select(s => s.Race).Distinct().ToList();
        return races.Count == 1 && races[0] != race;
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
            if (OtherRace(lobby, profile.Race)) return GamePackets.CreateInvalidServerRace;
            await EnsureExtendedCharacterSlotsAsync(ct);
            if (refused.SlotNo >= LobbySlotCapacity || lobby.Any(s => s.LobbySlot == refused.SlotNo)) return GamePackets.CreateInvalidSlot;
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
                if (OtherRace(await store.LobbyAsync(ct), profile.Race)) throw new ArgumentException("The other race on this server");
                await EnsureExtendedCharacterSlotsAsync(ct);
                if (request.SlotNo >= LobbySlotCapacity) throw new ArgumentException("Lobby slot locked");
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
                LoadAchievements(await store.AchievementsAsync(state.Summary.CharacterId, ct), await store.GroupRewardsTakenAsync(state.Summary.CharacterId, ct), now);
                LoadCollections(await store.CollectionsAsync(state.Summary.CharacterId, ct));
                LoadBossChallenges(await store.BossChallengesAsync(state.Summary.CharacterId, ct));
                LoadExpeditionTickets(await store.ExpeditionTicketsAsync(state.Summary.CharacterId, ct));
                LoadRewardLimits(await store.RewardLimitsAsync(state.Summary.CharacterId, ct));
                LoadDungeonCooltimes(await store.DungeonCooltimesAsync(state.Summary.CharacterId, ct));
                LoadAbyss(await store.AbyssAsync(state.Summary.CharacterId, ct), now);
                await EnsureAirspaceLoadedAsync(ct);
                await EnsureContentResetsLoadedAsync(ct);
                LoadTitles(extras);
                LoadSkins(extras);
                LoadMasteries(extras);
                await LoadRidesAsync(store, state.Summary.CharacterId, ct);
                await LoadQuickSlotsAsync(store, state.Summary.CharacterId, ct);
                var settings = await store.CharacterSettingsAsync(state.Summary.CharacterId, ct);
                LoadSettings(settings);
                LoadBuffSettings(settings, now);
                LoadSkillCustomizing(settings);
                await LoadBuybackAsync(store, state.Summary.CharacterId, ct);
                await LoadGuildAsync(store, state.Summary.CharacterId, ct);
                await LoadGodstoneCollectionAsync(store, state.Summary.CharacterId, ct);
                await LoadQuestsAsync(store, state.Summary.CharacterId, state.Race, ct);
                engraved.Clear();
                engraved.AddRange(await store.TeleportArtifactsAsync(state.Summary.CharacterId, ct));
                fog.Clear();
                fog.UnionWith(await store.MapFogAsync(state.Summary.CharacterId, ct));
                await LoadSocialAsync(ct);
                character = new Choice(state.Summary.Name, state.Gender, state.CreationId, state.Appearance, state.VoiceType);
                level = data.ClampLevel(state.Progress.Level);
                LoadDaevanion(await store.DaevanionAsync(state.Summary.CharacterId, ct));
                var daevanionNodesBefore = daevanionNodes.Count;
                EnsureStartNodes();
                if (daevanionNodes.Count > daevanionNodesBefore)
                {
                    var progress = new DaevanionProgress(daevanionNodes.ToList(), daevanionUsed, daevanionBonus);
                    await store.SaveDaevanionAsync(state.Summary.CharacterId, progress, ct);
                }
                className = profile.Class;
                RefreshKitSkillLevels();
                LoadSkillLevels(await store.SkillsAsync(state.Summary.CharacterId, ct));
                var savedMap = state.Progress.MapId.Value;
                // Logged out inside an instance layer: back at its exit on the field map (the copy is gone).
                if (Instances.Of(savedMap) is { } left)
                    (savedMap, savedAt) = (left.Exit.Map, new Vector3d(left.Exit.Position[0], left.Exit.Position[1], left.Exit.Position[2]));
                // A generated dungeon or story layer (its copy is gone too): a layer puts the player back on its base map at the
                // same spot, any other dungeon at the race's starting point (live 2026-10-18: a relog from the HQ001 layer).
                else if (IsInstanceMap(savedMap))
                {
                    var home = HomeMapOf(state.Race);
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
                return [GamePackets.SelectCharacter((int)key, state.WireId, playerKey, profile.PcId, state.Appearance, now, skillCustomizing: SkillCustomizingForSelect(profile.Class, level)),
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

    async Task<List<byte[]>> LoadCompleteAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var ticket = BitConverter.ToInt32(plain, 2);
        var name = character?.Name ?? options.IntroCharacter?.Name ?? "$local";
        var gender = character?.Gender ?? 1;
        var profile = data.Profile(character?.CreationId ?? 1, gender);
        // Skills follow the character level (tools/class_skills.py), not a grant list.
        var kit = profile is null ? [] : data.SkillListForClient(profile.Class, level, profile);
        className = profile?.Class;
        RefreshKitSkillLevels();
        kitListBaselines = kit.ToDictionary(k => k.SkillId, k => k.Level);
        if (profile is not null)
            kit = data.WithChainVariantsOnHotbar(WithLevels(kit), profile, MasteryLevel);
        learned = kit.Select(k => k.SkillId).ToHashSet();
        var (mapId, at) = spawn ?? (GamePackets.SpawnMapId, GamePackets.SpawnPosition);
        if (!IsInstanceMap(mapId) && instanceKey != 0) instanceKey = 0;
        EnsureAbyssLoaded(now);
        var previousMap = worldMap;
        worldMap = mapId;
        if (Abyss.IsAbyssMap(previousMap) && !OnAbyssMap) AbyssMapLeft(now);
        if (OnAbyssMap) AbyssMapEntered(now);
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
            if (instanceKey != 0 && MapEventTables.Embedded.DungeonOfMap(mapId) is { } soloRoom)
                Patch20261028.ScaleBossHp(targets.Values, MapEventTables.Embedded.DungeonIdOfMap(mapId), soloRoom);
        }
        LogWorldEntities();
        MapEventsPrepare();   // spawn entries a map event starts itself stay dormant
        PlaceChallengeBoss();
        if (Store is { } invStore && CharacterId is { } invChar)
            Remember(await LoadInventoryAsync(invStore, invChar, ct));
        equippedWeapon = Store is null ? data.StarterWeapon(profile) : WeaponFromInventory();
        // A map load in the world (teleport, revive) keeps HP/MP (it used to be a full heal); the
        // first entry after a selection starts full.
        var fresh = resources is null;
        resources = fresh ? new Resources(playerKey) : resources!;
        RefreshStats();
        if (fresh) (resources.Hp, resources.Mp, resources.Ap) = (resources.HpMax, resources.MpMax, resources.ApMax);
        resourceTickMs = now;
        ResetFpTick(now);
        var armed = EquipItemLevel();
        mountedRide = null;   // a map load (teleport, revive) leaves the ride
        ResetMovementCheck(at, now);
        position = savedPosition = at;
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
        RecomputeCombatPower();
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
                CombatPower = combatPower,
                SubzoneId = Subzones.SubzoneIdAt(mapId, at),
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
        // Respawn protection (10002) and class passives right after the own appearance (0x3633 / 0x3645).
        var appearAt = replies.FindIndex(b => b[0] == 0x46 && b[1] == 0x36) + 1;
        replies.InsertRange(appearAt, [.. RespawnLoadProtectAfterAppear(now), .. PassiveAbnormals(resend: true)]);
        replies.InsertRange(0, sameLevelRemovals);   // the same level's old entities go before the load reply (OBSERVED)
        sameLevelRemovals = [];
        if (shortLoad)
        {
            // After 0x364a the official layer entry sends one point update (#9684 `01 01 03 a0860100`: type 3, 100000).
            replies.Add(GamePackets.CharPoints(playerKey, [((CharPoint)3, 100000)]));
            // The short load omits 0x8d09 QuestList; without progress the tracker stays blank on instance re-entry (HQ2101030 step 3).
            if (questLog is not null)
                replies.AddRange(await QuestRecheckAsync(ct));
            if (questLog?.All.Where(q => q.Status == QuestLog.Incomplete).ToList() is { Count: > 0 } tracking)
                foreach (var q in tracking) replies.Add(GamePackets.QuestProgress(Wire(q)));
        }
        else
        {
            replies.AddRange(BuffsAfterLoad());
            if (Store is not null) replies.Add(RestorableItems());   // the buyback list (official: 0xe342 at world entry)
            replies.AddRange(GuildAtEntry());
            // Titles and skins as the official entry lists them (both empty there: #196 0x5679 `0000`, #203 0x8d5b `000000010000`).
            replies.Add(TitleList());
            replies.Add(SkinInfo());
            replies.Add(SeasonSchedule(now));
            replies.Add(AchievementList(now));
            replies.Add(BattlePassInfoList(now));
            replies.AddRange(InventoryDepotSyncPackets());
            replies.Add(PeriodCollectionList(now));
            replies.Add(BossChallengeList());
            replies.Add(BookmarkedContentList());
            replies.Add(PartyDungeonChallengeList());
            replies.Add(ContentsTicketsList(now));
            replies.Add(DungeonCoolTimesList(now));
            replies.Add(GodstoneList());
            replies.Add(DaevanionPointUpdate());
            replies.Add(DaevanionBoardInfo());
            replies.AddRange(AbyssAtEntry(now));
            replies.AddRange(EntryConstants(now, AccountContentsInfoList(now), SubscribeList(now)));
            replies.AddRange(AchieveSet("EquipItemLevel", EquipItemLevel(), now: now));
            if (fresh) { replies.AddRange(Achieve("LoginCharacter", now: now)); elapsedAchieveAt = now + 60_000; }
            replies.AddRange(ApplyLazyPeriodicResets(now, notifyClient: true));
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
                var leveledKit = WithLevels(kit);
                var skillList = GamePackets.SkillList(leveledKit, SkillWireOriginalLevels(leveledKit));
                replies.Add(skillList);
                LogSkillPointsAtEntry();
                replies.Add(SkillPointUpdate());
                var actives = learned.Where(id => id >= 11_000_000 && !IsDodgeId(id) && data.Skill(id)?.Type == "Active").Order().ToList();
                replies.Add(GamePackets.SkillOptionSlots(actives, SkillOptionMap()));
                replies.Add([0x58, 0x8d, 0x00]);
                replies.Add(AgitDecoListAtEntry());
                replies.AddRange(SettingsAtEntry(now));
                // The player's saved hotbar, else the class's default bar for its level.
                var hotbar = Hotbar(profile.DefaultBar());
                replies.Add(hotbar);
                LogSkillBarAtEntry(skillList, hotbar);
            }
            var questUnlocks = QuestUnlockTypes().ToList();
            var extras = ExtraContentsUnlocks(questUnlocks).Concat(DaevanionExtraContentsUnlocks(questUnlocks));
            var unlockTypes = GamePackets.WorldEntryContentsUnlocks(level, questUnlocks, extras);
            // The wing menu (Wing, 12) is quest-gated (HQ1101020 / HQ2101050); a winged character has it open.
            if (wingId != 0) unlockTypes = unlockTypes.Append((byte)12).Distinct().Order().ToArray();
            if (unlockTypes.Contains(RideContentsUnlock)) rideContentsUnlocked = true;
            if (unlockTypes.Contains(DaevanionContentsUnlock)) daevanionContentsUnlocked = true;
            replies.Add(GamePackets.ContentsUnlockList(unlockTypes));
            // Rewarded quests go in too (EQuestStatus 4): the client builds its acquirable list (Quest_AddedToAcquirableList,
            // NPC markers, Alt+1) from Quest.dat PreQuest/NextQuestName against this list. Without them it took the opening
            // HQ1100010 as never done and offered it, and never offered the next NPC-acquired story quest (HQ1101030 after HQ1101021).
            if (questLog is not null)
                replies.AddRange(await QuestRecheckAsync(ct));
            if (questLog?.All.Select(Wire).ToList() is { Count: > 0 } open)
                replies.Add(GamePackets.QuestList(open));
        }
        if (reloading)
        {
            // A teleport's map load: 0xe257 just re-locked chat (45) and the client sends no new
            // world-entry queries, so the unlock update (0xe258) follows directly (live: chat stayed
            // closed after MoveMap without it). The short load sent no 0xe257, so nothing to undo.
            if (!shortLoad) { replies.Add(GamePackets.ContentsUnlockUpdate()); unlockUpdateSent = true; }
            reloading = false;
            reloadStartedMs = 0;
            if (invisible) replies.Add(GamePackets.InvisibleMode(true));   // a GM stays hidden across the reload
        }
        replies.AddRange(EnsureAliveAfterLoad());
        replies.AddRange(ApplyQuestWorldGating(now));
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
        ResetEnvObjHits();
        pendingUse = null;
        replies.AddRange(ObjectVisibility(at, now));
        replies.AddRange(MapEventsStart(now, resendExistingJoins: shortLoad));   // the layer's events join (0x922e) and run their first directives
        replies.AddRange(ApplyQuestWorldGating(now));
        CharacterName = name;
        EnterSharedWorld(mapId, at);
        subzoneAllowsRide = SubzoneAllowsRideNow();
        arrivalCheckDue = true;
        if (fresh && selected is { } loginWho && Store is { } loginStore)
            selected = await loginStore.RecordLoginAsync(loginWho, now, ct) ?? loginWho;
        if (fresh) replies.AddRange(await AutoUsePendingItemsAsync(now, ct));
        return replies;
    }

    // ---------- Casts and combat (tools/starter_casts.py) ----------

    public sealed record CastRequest(int SkillId, int ClientActionUid, int ChangeToggle, bool HasChangeToggle, int TargetKey, double Yaw, Vector3d Location, int ClientActId = 0, bool HasClientActionUid = false);

    /// <summary>0x3800 cast request (writer RVA 0x8f7db28): u16 presence mask, skill, byte 2, target varint, yaw + location, optionals.</summary>
    public static CastRequest? ParseCast(byte[] body)
    {
        if (body.Length < 26 || !IsSkillStartWireOpcode(Opcode(body)) || body[8] != 2) return null;
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
            var hasToggle = (flags & 8) != 0;
            int toggle = hasToggle ? reader.U8() : 0;
            int uid = (flags & 16) != 0 ? reader.U8() : 0;
            int actId = (flags & 32) != 0 ? (int)reader.Varint() : 0;     // client act ID (the animation the client plays)
            foreach (var bit in new[] { 64, 128 })
                if ((flags & bit) != 0 && !float.IsFinite(reader.F32())) return null;
            if ((flags & 256) != 0) reader.U64();       // client time
            if (!reader.End) return null;
            return new CastRequest(BitConverter.ToInt32(body, 4), uid, toggle, hasToggle, (int)targetKey, yaw, new Vector3d(x, y, z), actId, (flags & 16) != 0);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    static List<byte[]> CastMalformedReply(byte[] body)
    {
        if (body.Length < 9 || !IsSkillStartWireOpcode(Opcode(body))) return [];
        var skill = BitConverter.ToInt32(body, 4);
        return [GamePackets.CastReply(false, skill, 0)];
    }

    /// <summary>
    /// 0x3801 for a cast: the pre-#140 flags-first form for every skill. The official 9-byte form (GamePackets.CastResult)
    /// left this client unable to cast again (live 2026-10-30: Ranger 13:35 one shot, Summoner 13:47 summon then no basic
    /// attack request for minutes), even with the 0x3806 end; the old form kept combat working. OBSERVED official 2026-10-30: flags byte 0, result u16, skill (01 38 00 00 00 a0 ed d5 00), no uid. 0x3806 is still sent.
    /// </summary>
    /// <summary>0x3801 refusal for cast-not-ready; result 1 keeps attacking (#158). Official 0x1813 stopped targeting on this client.</summary>
    internal const ushort CastNotReadyResult = 1;

    int ChainRoot(int skill)
    {
        for (var i = 0; i < 8 && data.Skill(skill) is { ChainPrev: > 0 } step; i++) skill = step.ChainPrev;
        return skill;
    }

    // Level/specialization variants (Ranger 14020010, 14021230) chain from a variant root; the learned family skill
    // (last four digits zeroed, 14020000) unlocks them.
    bool ChainRootLearned(int skill)
    {
        var root = ChainRoot(skill);
        return learned.Contains(root) || learned.Contains(root / 10_000 * 10_000);
    }

    static int SkillFamilyId(int skillId) => skillId / 10_000 * 10_000;

    bool SkillFamilyLearned(int skillId) => learned.Contains(skillId) || learned.Contains(SkillFamilyId(skillId));

    SkillDefinition? ResolveSkillDefinition(int skillId) => data.Skill(skillId) ?? data.Skill(SkillFamilyId(skillId));

    static byte[] CastReplyFor(CastRequest request, bool accepted) =>
        GamePackets.CastReply(accepted, request.SkillId, (byte)request.ChangeToggle, request.HasChangeToggle, request.ClientActionUid, includeUid: false,
            refusal: CastNotReadyResult);

    List<byte[]> StopAutoAttackReplies(CastRequest request, int skill)
    {
        var replies = new List<byte[]> { CastReplyFor(request, true) };
        if (autoAttackActionUid != 0)
        {
            replies.Add(GamePackets.SkillEnd(playerKey, autoAttackSkill != 0 ? autoAttackSkill : skill, autoAttackActionUid));
            autoAttackActionUid = 0;
        }
        return replies;
    }

    List<byte[]> CastReplies(byte[] body, long now)
    {
        if (ParseCast(body) is not { } request) return CastMalformedReply(body);
        NoteHeldCastWireReceived(now);
        var skill = request.SkillId;
        request = WithCastLocation(request);
        // OBSERVED (official 2026-10-19: 36 dodges, 64 sprint starts; live trace 2026-10-20: the client sends 0x3704 then its
        // CombatDodge request): a dodge requested while the sprint is on starts NormalDodge 11000101 (the running dash, SP 10,000)
        // in place of CombatDodge (SP 30,000). Its replies: 0x3802, the 9-byte 0x3801 naming CombatDodge, abnormal 200 for 300 ms,
        // then 0x8d00 with the SP; 200 ms later 0x3806 ends it and 0x382c removes the abnormal.
        var combatDodge = ClassCombatDodge;
        var dodging = IsDodgeId(skill);
        if (dodging) combatDodge = IsCombatDodgeId(skill) ? skill : skill - 1;
        var variant = DashVariant;
        // Variants 6-9 change only the dash that follows a sprint. A standing dodge keeps the reply this client already completes.
        if (dodging && !sprinting && variant >= 6) variant = 0;
        // Variant 6: the sprint stands on its own. A 9-byte refusal (result 1, no skill start) so the client does not read the
        // mask-first reply as a failed dash and drop the sprint. The live question is whether the sprint run then holds on land.
        if (dodging && sprinting && variant == 6)
            return [GamePackets.DodgeResult(combatDodge, 1)];
        // Variant 10: no reply at all to the dodge the client sends with the sprint (6 refused it and still froze, live 2026-10-20).
        if (dodging && sprinting && variant == 10) return [];
        // Variant 9 starts CombatDodge while sprinting (official starts NormalDodge). The rest of the sprint dashes are NormalDodge.
        // OBSERVED (official 2026-10-19 and 2026-10-20): the server picks the dodge by combat state. All 36 CombatDodge starts came
        // within 8 s of the player's own attack; out of combat the dodge is NormalDodge (10,000 SP, no 0x3804 effect) whether the
        // player sprints, runs or stands (2026-10-20: shift while standing gave NormalDodge with no sprint reply at all).
        var started = skill == combatDodge && variant != 9 && (sprinting || !inCombat) ? combatDodge + 1 : skill;
        // Recovered Skill.dat cost, cooldown and weapon requirement for any learned skill.
        // NormalDodge is CombatDodge's variant: learned with it, SP 10000, the same group cooldown.
        var normalDodge = dodging && started == combatDodge + 1 && learned.Contains(combatDodge);
        var definition = normalDodge ? data.Skill(combatDodge) is { } dodge ? dodge with { Cost = dodge.Cost with { Sp = NormalDodgeSp } } : null
            : ResolveSkillDefinition(skill);
        var cooldownKey = data.CooldownGroup(normalDodge ? combatDodge : (data.Skill(skill) is not null ? skill : SkillFamilyId(skill)));
        var cost = definition?.Cost ?? new SkillCost(0, 0, 0);
        if (BuffStat("MPUseDecrease") is > 0 and var cheaper) cost = cost with { Mp = cost.Mp * Math.Max(0, 10_000 - cheaper) / 10_000 };
        var weapons = (definition?.Weapons ?? []).Where(w => w != "None").ToList();
        var chainSkill = definition is { ChainPrev: > 0 };
        // A step's parent may itself be a chain-only step (Ranger 14040000 <- 14030000 <- 14020000): the learned root decides.
        var chainAllowed = !chainSkill || (ChainReady(skill, now) && ChainRootLearned(skill));
        var cooldownReady = SkillCooldown.CooldownReady(now, cooldowns, cooldownKey, noCool);
        var meetsCastRules = definition is not null && resources is not null && !dead && chainAllowed && !CombatCastBlocked(skill)
            && resources.Mp >= cost.Mp && resources.Sp >= cost.Sp && resources.Hp > cost.Hp
            && (resources.ApMax <= 0 || resources.Ap >= cost.Ap)
            && (SkillFamilyLearned(skill) || normalDodge || chainSkill || CarriedSkill == skill)
            && (weapons.Count == 0 || weapons.Contains(data.WeaponType(equippedWeapon) ?? ""))
            && UseConditionMet(definition, now, request.TargetKey, skill);
        var basicAuto = definition is not null && SkillCooldown.IsBasicAutoAttack(definition);
        var toggleOff = request.HasChangeToggle && request.ChangeToggle is 0 or 2;
        var toggleOn = request.HasChangeToggle && request.ChangeToggle == 1;
        if (basicAuto && toggleOff)
            return StopAutoAttackReplies(request, skill);
        if (basicAuto && toggleOn)
        {
            autoAttackSkill = skill;
            selectedTarget = request.TargetKey;
        }
        // Minimum gap between two accepted non-dodge casts. CheckToleranceMs (200) equals ProjectileRecastMs (200), so a
        // zero-cooldown projectile (Tempest Shot) or a chain step was re-accepted on every client resend (~17 ms, live
        // 2026-10-30 16:08: Tempest Shot x6 in 70 ms, Snipe->Rapid Fire->Spiral Arrow->Snipe in 33 ms). Each accept is a new
        // SkillStart with projectiles/FX, which flooded the client (stuck FX meshes, then UE "Maximum number of UObjects").
        // OBSERVED official: Tempest Shot re-accepted 229 ms apart at the least.
        var castGapOk = noCool || dodging || now - lastAcceptedCastMs >= SkillCooldown.MinCastGapMs;
        var accepted = meetsCastRules && cooldownReady && castGapOk;
        if (!accepted)
        {
            if (!dodging && meetsCastRules && IsExactHeldCastDuplicate(request, body, now))
                return [CastReplyFor(request, false)];
            var queued = MaybeQueueEarlyChainCast(body, request, chainSkill, chainAllowed, castGapOk, nextCastAcceptAtMs);
            if (!releasingSkillCharge && HasDeferredQueuedCast(now)) return queued;
            if (queued.Count > 0) return queued;
            LogSkillCastRefusal(request, skill, definition, meetsCastRules, chainAllowed, cooldownReady, castGapOk, cost, weapons);
            // A sprint dash refused for cost or cooldown stays on the 9-byte form. The mask-first reply drops the sprint.
            if (dodging && sprinting && variant is 7 or 8 or 9)
                return DeliverDash([GamePackets.DodgeResult(skill, 1)], variant);
            // Right-click auto-attack-on-target: acknowledge toggle while the swing is still on lockout (no SkillStart).
            if (basicAuto && toggleOn && meetsCastRules && !cooldownReady)
                return [CastReplyFor(request, true)];
            // Held-key duplicate 0x3800 (~17 ms): 0x3801 failure (no second 0x3802). Playtest 2026-10-31 #262 silent drop hid reason.
            return [CastReplyFor(request, false)];  // must not end another action
        }
        var dismount = ForceDismount().Concat(InterruptItemCast()).ToList();
        // The bomb thrown at a tower ends with the hit that breaks it; any other cast of it uses a charge now.
        if (DestructibleEnvObjIndex(request.TargetKey) is null) dismount.AddRange(CarrySkillStarted(skill));
        if (!dodging) lastAcceptedCastMs = now;
        ScheduleNextCastAccept(now, definition!, skill, HeldCastWireKeyOf(request, body));
        DropQueuedHeldCastSilently();
        skillMoveGraceUntil = now + SkillMoveGraceMs;
        var cooldown = noCool ? 0 : SkillCooldown.Reduced(SkillCooldown.EffectiveMs(definition), CoolTimeDecrease);
        var lockout = PlayerCastLockoutMs(definition!, skill);
        if (definition!.CooldownMs > 0 && lockout == definition.CooldownMs) lockout = cooldown;
        if (!noCool && lockout > 0) cooldowns[cooldownKey] = now + lockout;
        // Builder SetInvincible: the flagged resources do not decrease.
        if (!invincible.Mp) resources!.Mp -= cost.Mp;
        if (!invincible.Sp) resources!.Sp -= cost.Sp;
        if (!invincible.Hp) resources!.Hp -= cost.Hp;
        if (resources!.ApMax > 0 && cost.Ap > 0) resources.Ap = Math.Max(0, resources.Ap - cost.Ap);
        actionUid = actionUid % 255 + 1;
        if (basicAuto)
        {
            autoAttackActionUid = (byte)actionUid;
            autoAttackSkill = skill;
        }
        if (dodging)
        {
            // Server i-frames for every accepted dodge (variant 0 sends no abnormal 200 on the wire; the client still dashes ~300 ms).
            dodgeInvulnUntil = now + DodgeAbnormalMs;
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
                    GamePackets.DodgeResult(combatDodge),
                    DodgeAbnormalAdd(now, started, actionUid, batchEnd),
                    GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]),
                ];
                if (started == combatDodge) dodgeEffectAt = (actionUid, now + delay + DodgeEffectMs);
                return DeliverDash(dash, variant);
            }
            if (variant == 0)
            {
                List<byte[]> bisect =
                [
                    CastReplyFor(request, true),
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
                GamePackets.DodgeResult(combatDodge),
            ];
            if (variant is 2 or >= 4) dodgeReplies.Add(DodgeAbnormalAdd(now, started, actionUid, endAt, end: variant != 2));
            else if (variant == 3) dodgeAbnormalOff = (0, started, actionUid, endAt, true);
            dodgeReplies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]));
            if (variant >= 4 && started == combatDodge) dodgeEffectAt = (actionUid, now + DodgeEffectMs);
            return dodgeReplies;   // no damage rows: nothing for CombatReplies
        }
        // Official order (2026-10-30): 0x3802 start, 0x3818/0x3819 chain, the 0x3801 accept, 0x8d21 when it starts combat,
        // then the 0x3803 arrows.
        var replies = new List<byte[]> { CastStartPacket(skill, (byte)actionUid, request, cooldown) };
        replies.AddRange(ChainCasterAbnormalReplies(skill, now));
        replies.AddRange(ChainCastReplies(skill, now));
        replies.AddRange(ApplySkillCondGrant(skill, definition!, now, request));
        replies.Add(GamePackets.RequestAck(0x383a));
        replies.Add(CastReplyFor(request, true));
        replies.AddRange(EnterCombat(now));
        replies.Add(resources!.Update());
        replies.InsertRange(0, dismount);   // a cast takes the player off its ride before the skill starts
        replies.AddRange(ScheduleCombatHits(skill, request, now, (byte)actionUid));
        replies.AddRange(CasterMoveReplies(skill, request, definition!, now));
        replies.AddRange(AbnormalOnlyCastReplies(skill, request, definition!, now));
        replies.AddRange(SkillEffectReplies(skill, request, definition!, now));
        ScheduleSkillEnd(skill, (byte)actionUid, definition!, now);
        replies.AddRange(SummonFromSkill(skill, request, now));
        replies.AddRange(JointStrike(definition!, request, now));
        replies.AddRange(DismissFromSkill(skill));
        replies.AddRange(PlaceTrap(skill, request, (byte)actionUid, now));
        replies.AddRange(PlaceZone(skill, request, (byte)actionUid, now));
        return replies;
    }

    /// <summary>
    /// An accepted cast starts combat: 0x8d21 on right after its 0x3801 (OBSERVED official 2026-10-30, 21 8d df4b 00 01 after
    /// the first Snipe's 0x3801); every cast and every 0x383a bracket in combat holds it for <see cref="CombatTimeoutMs"/>.
    /// </summary>
    List<byte[]> EnterCombat(long now)
    {
        combatUntil = now + CombatTimeoutMs;
        if (inCombat) return [];
        inCombat = true;
        return [GamePackets.CombatState(playerKey, true), .. MapEventCombatStarted(now)];
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
            if (victim.OwnerKey != 0) { replies.AddRange(fights.Killed(victim)); replies.AddRange(SummonDied(victim)); continue; }
            if (killer.OwnerKey == playerKey) { replies.AddRange(PlayerKilledNpc(victim, 0, killer.Key, now)); continue; }
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
    internal List<byte[]> NpcHitsPlayer(int damage, long now, int killerKey = 0, int killerSkill = 0)
    {
        if (resources is null || dead) return [];
        if (killerSkill > 0) lastNpcHitSkill = killerSkill;
        damage = DungeonBossDamage(damage, killerKey);
        var replies = new List<byte[]>();
        if (!inCombat)
        {
            inCombat = true;
            replies.Add(GamePackets.CombatState(playerKey, true));
        }
        combatUntil = now + CombatTimeoutMs;
        replies.AddRange(PlayerDamageReplies(damage, killerKey, now));
        return replies;
    }

    // ---------- Stats (death: GameSession.Death.cs) ----------

    /// <summary>MPMax of the official 0x3649 sample (level 1); CANDIDATE: the same for every class and level.</summary>
    public const int MpMaxBase = 1500;

    IEnumerable<ItemInfo> EquippedItems() =>
        inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Select(i => data.Item(i.ItemId)).OfType<ItemInfo>();

    int EquipItemLevel() => inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Sum(i => i.Detail?.ItemLevel ?? 0);

    /// <summary>The weapon in equip slot 1; development logins (no store) keep the class starter weapon.</summary>
    int? WeaponFromInventory() =>
        inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Equipped && i.Slot == 1)?.ItemId
        ?? (Store is null ? data.StarterWeapon(Profile()) : null);

    /// <summary>Defense against NPC hits: PcStatLevel Defense plus equipped items' defense stats.</summary>
    public int Defense => data.BaseStats(className, level).GetValueOrDefault("Defense") + EquipStat("ArmorDefense") + EquipStat("Defense")
        + EnchantStat("ArmorDefense") + EnchantStat("Defense") + ExceedStat("ArmorDefense") + ExceedStat("Defense") + BuffStat("Defense")
        + BoardStat("Defense") + TitleStat("Defense") + SetStat("Defense") + DaevanionWireStat(52);

    /// <summary>The MP pool: MPMax 1500 plus buffs.</summary>
    int MpMax => MpMaxBase + BuffStat("MPMax") + BoardStat("MPMax") + TitleStat("MPMax") + DaevanionWireStat(199);

    /// <summary>
    /// The stat sheet (0x3649 / 0x364a): PcStatLevel bases plus gear, buffs, titles, Daevanion and the official sample defaults where still unknown.
    /// </summary>
    (IReadOnlyList<(int Type, int Value)> Stats, int HpMax) StatSheet()
    {
        var basis = data.BaseStats(className, level);
        var daevanion = DaevanionWireStats();
        int D(int wire) => daevanion.GetValueOrDefault(wire);
        var hpMax = Math.Max(1, (basis.GetValueOrDefault("HPMax") is > 0 and var hp ? hp : GamePackets.SpawnHpMax) + EquipStat("HPMax") + EnchantStat("HPMax") + ExceedStat("HPMax") + RideStat("HPMax")
            + BuffStat("HPMax") + BoardStat("HPMax") + TitleStat("HPMax") + SetStat("HPMax") + D(193));
        var weapon = data.WeaponStatsOf(equippedWeapon);
        int WeaponStat(string name, int fallback) => Math.Max(fallback, EquipStat(name));
        var byType = GamePackets.DefaultStats.Select(s => s.Type switch
        {
            31 => (Type: s.Type, Value: WeaponStat("WeaponDamage", weapon.Max) + TitleStat("FixingDamage") + EnchantStat("WeaponDamage") + ExceedStat("WeaponDamage") + D(31)),
            33 => (Type: s.Type, Value: WeaponStat("WeaponMinDamage", weapon.Min) + TitleStat("FixingDamage") + EnchantStat("WeaponMinDamage") + ExceedStat("WeaponMinDamage") + D(33)),
            128 => (Type: s.Type, Value: WeaponStat("Critical", weapon.Critical) + RideStat("Critical") + BuffStat("Critical") + BoardStat("Critical") + TitleStat("Critical") + SetStat("Critical") + D(128)),
            318 => (Type: s.Type, Value: WeaponStat("WeaponAccuracy", weapon.Accuracy) + EquipStat("Accuracy") + TitleStat("Accuracy") + D(318)),
            193 => (Type: s.Type, Value: hpMax), 199 => (Type: s.Type, Value: MpMax), 242 => (Type: s.Type, Value: GamePackets.SpawnSp + BuffStat("SPMax")),
            307 => (Type: s.Type, Value: Defense),
            _ => (Type: s.Type, Value: s.Value + D(s.Type)),
        }).ToDictionary(p => p.Type, p => p.Value);
        foreach (var (wire, value) in daevanion)
            if (!byType.ContainsKey(wire)) byType[wire] = value;
        foreach (var (wire, value) in ExtraSheetStats())
            byType[wire] = value;
        if (wingId != 0 && resources is { FpMax: > 0 })
        {
            byType[247] = resources.Fp;   // FlyCost: current flight time (usmap; pairs with FPMax on the gauge)
            byType[298] = resources.FpMax;
        }
        var stats = byType.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)).ToList();
        return (stats, hpMax);
    }

    int StatWireValue(int wireType)
    {
        foreach (var (type, value) in StatSheet().Stats)
            if (type == wireType) return value;
        return 0;
    }

    /// <summary>Applies the stat sheet to the pools (HP/MP clamped) and returns the updates for the client.</summary>
    internal List<byte[]> RefreshStats()
    {
        if (resources is null) return [];
        var (stats, hpMax) = StatSheet();
        resources.HpMax = hpMax;
        resources.MpMax = MpMax;
        resources.SpMax = GamePackets.SpawnSp + BuffStat("SPMax");
        resources.ApMax = PlayerApPools.ApMax(data, className, level, Profile()?.Resource);
        resources.Hp = Math.Min(resources.Hp, hpMax);
        resources.Mp = Math.Min(resources.Mp, resources.MpMax);
        resources.Sp = Math.Min(resources.Sp, resources.SpMax);
        resources.Ap = Math.Min(resources.Ap, Math.Max(0, resources.ApMax));
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

    public (int Hp, int HpMax, int Mp, int MpMax, int Sp, int SpMax, int Fp, int FpMax) Resource =>
        resources is { } r ? (r.Hp, r.HpMax, r.Mp, r.MpMax, r.Sp, r.SpMax, r.Fp, r.FpMax) : default;

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
        if (mapId != worldMap) LogTeleport(mapId, at, "teleport");
        BroadcastWarpLeave(effect, cutscene.Length > 0);
        // An instance layer or dungeon is a private copy (the entry set it; a staff move gets one too); elsewhere the shared map.
        var previousInstance = instanceKey;
        instanceKey = !IsInstanceMap(mapId) ? 0 : instanceKey != 0 ? instanceKey : playerKey;
        if (!IsInstanceMap(mapId)) { instanceExitAt = null; instanceReturn = null; clearExit = null; ResetWalkInLayerState(); }
        else if (mapId != worldMap) ResetWalkInLayerState();
        moveReason = reason;
        moveEffect = effect;
        diving = false;
        surfaceZ = null;
        var ending = new List<byte[]>();
        if (mapId != worldMap || instanceKey != previousInstance) ending.AddRange(DropCarried(ExpireEventFunction));
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
            DismissAllSummons();
            targets = null;  // the new map's NPCs appear on 0x3622
            respawnAt = [];
            // Hits still due land on the old map's NPCs: the next map reuses their keys (2000 onward), so they would hit (and kill) its NPCs.
            pendingCombatHits.Clear();
            pendingProjectiles.Clear();
            if (options.World is not null) ai = new NpcAi(rng, data);
        }
        spawn = (mapId, at);
        position = at;
        reloading = true;
        NoteReloadStarted(now);
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
        if (arriving)
        {
            TellFriends(true);
            PartyReconnectIfNeeded();
        }
        var previous = worldPlayer.Map;
        world.Map(mapId, instanceKey).Join(worldPlayer);
        if (previous is not null && previous != worldPlayer.Map) world.Drop(previous);
    }

    /// <summary>Leaves the shared map (lobby, disconnect): others see this player disappear. Call under the world gate.</summary>
    public void ExitSharedWorld()
    {
        DismissAllSummons();
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
    internal byte[] OtherAppearance()
    {
        var profile = Profile();
        return PlayerPackets.CharAppear(new OtherCharacter
        {
            Key = playerKey, Name = CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), PcId = profile?.PcId ?? 1,
            Race = profile?.Race ?? 1, Gender = character?.Gender ?? 1, Position = position ?? worldPlayer?.Position ?? default,
            Direction = worldPlayer?.Heading ?? 0, Hp = resources?.Hp ?? 1, HpMax = resources?.HpMax ?? 1, Mp = resources?.Mp ?? 0, MpMax = resources?.MpMax ?? 0,
            Level = level, WingId = wingId, TitleId = ShownTitle, Equips = VisualEquips(all: true), Abnormals = BuffWires(),
            EquipItemLevel = inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Sum(i => i.Detail?.ItemLevel ?? 0),
            MoveState = mountedRide is not null ? RidePackets.MoveModelRun : (byte)(moving ? RidePackets.MoveModelRun : RidePackets.MoveModelStop),
            MoveSubstate = mountedRide is not null ? MountMoveAckFields().Substate : (byte)0,
            DeadReckoning = moving,
            CustomizingData = character?.CustomizingData ?? [], VoiceType = character?.VoiceType ?? 0, DbId = selected?.WireId ?? playerKey,
            Sprinting = sprinting, ToggleFlags = sealStoneOn ? 1 : 0, SealstoneItemId = SealStoneItemId,
            RideDataId = mountedRide ?? 0, DefaultRideDataId = defaultRide,
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
            EquipBroad Broad(int id)
            {
                if (id == 0) return new EquipBroad(0, VisualSlot: (byte)slot, Godstones: []);
                var equipSlot = VisualSlot.FirstOrDefault(p => p.Value == slot).Key;
                var gs = equipSlot == 0 ? [0, 0, 0] : GodstonesInSlot(equipSlot).ToArray();
                return new EquipBroad(id, VisualSlot: (byte)slot, Godstones: gs);
            }
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

    /// <summary>Time of the last accepted non-dodge cast (see SkillCooldown.MinCastGapMs).</summary>
    long lastAcceptedCastMs = long.MinValue / 2;

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
        var wasAir = air;
        switch (Opcode(plain))
        {
            case 0x370a when CanSpendFlightFp: air = AirState.Fly; flySprint = plain.Length > 2 && (plain[2] & 1) != 0; break;
            case 0x370b when CanSpendFlightFp: air = AirState.Fly; break;
            case 0x3706 or 0x3707 when CanSpendFlightFp: air = AirState.Glide; break;
            case 0x3702 or 0x3703: air = AirState.Fall; break;
            case 0x3712 or 0x3713: air = AirState.WindPath; break;
            case 0x3700 or 0x3701 or 0x3718 or 0x370c or 0x370d or 0x370e or 0x370f or 0x3710 or 0x3711 or 0x3714 or 0x3715 or 0x3716 or 0x3717:
                air = AirState.Ground; break;
        }
        if (air is AirState.Fly or AirState.Glide && wasAir is not (AirState.Fly or AirState.Glide))
            ResetFpTick(lastNow);
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
        || Social is { } s2 && worldPlayer is not null && ArenaOf(worldPlayer) is { } arena && arena.Owner == worldPlayer && (now >= arena.NextMs || arena.RespawnAt.Values.Any(r => now >= r))
        || Social is { } s3 && worldPlayer is not null && s3.PartyOf(worldPlayer) is { } lootParty && lootParty.Leader == worldPlayer
            && (s3.DiceRolls.Values.Any(r => !r.Resolved && r.PartyKey == lootParty.Key && now >= r.ExpiresAtMs)
                || s3.BidSessions.Values.Any(b => !b.Resolved && b.PartyKey == lootParty.Key && now >= b.ExpiresAtMs))
        || options.World?.Airspace.NeedsTick(now) == true
        || options.World?.Resets.NeedsTick(now) == true
        || pendingCombatHits.Exists(h => now >= h.DueMs);

    /// <summary>The time of the last request or tick (for work done from a request without its own clock).</summary>
    long lastNow;
    /// <summary>Set by a map load: the arrival counts like a position report on the next tick.</summary>
    bool arrivalCheckDue;

    public async Task<List<byte[]>> TickAsync(long now, CancellationToken ct = default)
    {
        lastNow = now;
        var replies = new List<byte[]>();
        while (posted.TryDequeue(out var item)) replies.AddRange(await item.Run(now));
        replies.AddRange(IdleReturnTick(now));
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
        replies.AddRange(FlightFpTick(now));
        replies.AddRange(SprintTick(now));
        replies.AddRange(DodgeAbnormalTick(now));
        replies.AddRange(SkillEndTick(now));
        replies.AddRange(SummonExpiryTick(now));
        replies.AddRange(ElementalActivateTick(now));
        replies.AddRange(DrainQueuedHeldCast(now));
        replies.AddRange(SkillChargeTimeoutTick(now));
        replies.AddRange(ChainTickReplies(now));
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
                if (resources.FpMax > 0 && !dead && air == AirState.Ground)
                    resources.Fp = (int)Math.Min(resources.FpMax, fp + seconds * (inCombat ? FpRegenBattle : FpRegenRest));
                var suffocating = underwater && resources.Op == 0;
                if (suffocating) replies.AddRange(EnsureOxygenDeficiency(now));
                else if (resources.Op > 0) replies.AddRange(ClearOxygenDeficiencyIfPresent());
                // Minutes in a movement state for the ElapsedTime achievements naming it ("Glide for 10 minutes" = step 10;
                // Flying, Swimming likewise): the seconds accrue per state and count a minute at a time. WindPath and
                // springboard states are not tracked.
                if (!dead && (air == AirState.Glide ? "Glide" : air == AirState.Fly ? "Flying" : surfaceZ is not null ? "Swimming" : null) is { } moveState)
                {
                    var accrued = elapsedStateSeconds.GetValueOrDefault(moveState) + seconds;
                    for (; accrued >= 60; accrued -= 60) replies.AddRange(Achieve("ElapsedTime", 1, e => e.Has(moveState), now: now));
                    elapsedStateSeconds[moveState] = accrued;
                }
                var rest = Resting && !inCombat ? RestRegenFactor : 1;
                if (!inCombat)
                {
                    var hpRegen = StatWireValue(195) + BuffStat("HPRegen");
                    if (hpRegen > 0) resources.Hp = (int)Math.Min(resources.HpMax, resources.Hp + seconds * rest * hpRegen);
                    var mpRegen = StatWireValue(201) + BuffStat("MPRegen");
                    resources.Mp = (int)Math.Min(resources.MpMax, resources.Mp + seconds * rest * Math.Max(0, mpRegen));
                }
                if (!sprinting)
                    resources.Sp = (int)Math.Min(resources.SpMax, resources.Sp + seconds * (inCombat ? SpRegenInCombat : SpRegenOutOfCombat));
                if (resources.ApMax > 0)
                {
                    var apRegen = PlayerApPools.ApRegen(data, className);
                    if (apRegen > 0)
                        resources.Ap = (int)Math.Min(resources.ApMax, resources.Ap + seconds * apRegen);
                }
                Pin(resources);
                // Invincible SP is resent each second: flight spends it on the client side.
                var apBefore = resources.Ap;
                if (invincible.Sp || before != (resources.Hp, resources.Mp, resources.Sp)) replies.Add(resources.Update());
                else if (resources.ApMax > 0 && apBefore != resources.Ap) replies.Add(resources.ApUpdate());
                if (resources.Op != op) replies.Add(resources.OpUpdate());
                if (resources.Fp != fp) replies.AddRange(FpWireUpdates());
                if (resources.Fp == 0 && fp > 0 && air is AirState.Fly or AirState.Glide)
                    replies.AddRange(EndFlightFromEmptyFp());
                if (suffocating && resources.Hp == 0) replies.AddRange(Die(0, now));
            }
        }
        if (pendingUse is { } use && now >= use.DueMs)
        {
            pendingUse = null;
            replies.AddRange(await WithMapEventClearsFlushedAsync(await CompleteUseAsync(use.Index, ct, now), ct));
        }
        if (dead && now >= reviveAt) replies.AddRange(AutoRevive(now));
        WarnStuckReload(now);
        if (itemCast is { } cast && now >= cast.DueMs) replies.AddRange(await CompleteItemCastAsync(now, ct));
        replies.AddRange(ReleasePendingProjectiles(now));
        replies.AddRange(ResolvePendingCasterMoves(now));
        replies.AddRange(ResolvePendingSkillAbnormals(now));
        replies.AddRange(ResolvePendingSkillEffects(now));
        replies.AddRange(ResolvePendingMoveStops(now));
        replies.AddRange(ResolvePendingCombatHits(now));
        replies.AddRange(TrapTick(now));
        replies.AddRange(ZoneTick(now));
        replies.AddRange(await QuestKillsAsync(ct));
        replies.AddRange(await KillExperienceAsync(ct));
        replies.AddRange(BuffTick(now));
        replies.AddRange(DropTick(now));
        replies.AddRange(MapEventTick(now));
        replies.AddRange(InstanceTick(now));
        replies.AddRange(AbyssTick(now));
        replies.AddRange(AirspaceWorldTick(now));
        replies.AddRange(ContentResetWorldTick(now));
        await FlushContentResetWorkAsync(ct);
        // Map events cleared by a talk or a timer pay their quest objective here (kills pay on the cast).
        if (clearedEvents.Count > 0) replies.AddRange(await QuestKillsAsync(ct));
        if (pendingRewards.Count > 0) replies.AddRange(await GrantPendingRewardsAsync(now, ct));
        if (pendingQuestChanges.Count > 0)
        {
            var changed = pendingQuestChanges.ToList();
            pendingQuestChanges.Clear();
            replies.AddRange(await Stored(() => QuestChangesAsync(changed, ct)));
        }
        await FlushAchievementsAsync(now, ct);
        await FlushAbyssAsync(now, ct);
        await FlushAirspaceAsync(ct);
        await FlushBossChallengeAsync(ct);
        await FlushExpeditionTicketsAsync(ct);
        await FlushRewardLimitsAsync(ct);
        await FlushDungeonCooltimesAsync(ct);
        await FlushBuffSettingsAsync(now, ct);
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
                SyncSharedMapAiKillPreserve(aiMap);
                aiMap.Ai.DefenseOf ??= key => aiMap.Players.TryGetValue(key, out var p) ? p.Session.Defense : 0;
                aiMap.BroadcastAll(aiMap.Ai.Tick(aiMap.Targets, aiMap.NpcVictims(), now, (key, damage) =>
                {
                    if (aiMap.Players.TryGetValue(key, out var victim)) victim.Send(victim.Session.NpcHitsPlayer(damage, now, aiMap.Ai.LastAttacker, aiMap.Ai.LastHitSkill));
                    return [];
                }));
                var npcKills = NpcKillsByNpcs(aiMap.Ai, now);
                aiMap.BroadcastAll(npcKills, worldPlayer);
                replies.AddRange(npcKills);
            }
        }
        else if (targets is not null && (ai.DefenseOf ??= _ => Defense) is not null)
        {
            ai.SkipLeashHealNpcIds = OpenKillNpcIdsForSharedAi();
            if (ai.SkipLeashHealNpcIds.Count == 0) ai.SkipLeashHealNpcIds = null;
            replies.AddRange(Visible(ai.Tick(targets, invisible || dead || FriendlyToAll ? null : position, now, damage => NpcHitsPlayer(damage, now, ai.LastAttacker, ai.LastHitSkill))));
            replies.AddRange(NpcKillsByNpcs(ai, now));
        }
        PartyTick(now);
        StaggerGaugeCombat.StaggerTick(targets, now);
        ClearPlayerStaggerIfExpired(now);
        PartyLootTick(now);
        DuelTick(now);
        ArenaTick(now);
        if (pendingAchievementPackets.Count > 0) { replies.AddRange(pendingAchievementPackets); pendingAchievementPackets.Clear(); }
        if (worldPlayer is not null)
            while (worldPlayer.Outbox.TryDequeue(out var queued)) replies.Add(queued);
        await CheckpointAsync(now, force: false, ct);
        return replies;
    }

    /// <summary>Writes debounced session state before logout or disconnect (position is saved separately).</summary>
    public async Task FlushDeferredPersistenceAsync(long now, CancellationToken ct)
    {
        if (pendingQuestChanges.Count > 0)
        {
            var changed = pendingQuestChanges.ToList();
            pendingQuestChanges.Clear();
            await Stored(() => QuestChangesAsync(changed, ct));
        }
        await FlushAchievementsAsync(now, ct, force: true);
        await FlushAbyssAsync(now, ct, force: true);
        await FlushAirspaceAsync(ct);
        await FlushBossChallengeAsync(ct);
        await FlushExpeditionTicketsAsync(ct);
        await FlushRewardLimitsAsync(ct);
        await FlushDungeonCooltimesAsync(ct);
        await FlushContentResetWorkAsync(ct);
        await FlushBuffSettingsAsync(now, ct, force: true);
        if (InWorld && Store is { } logoutStore && selected is { } logoutWho)
            selected = await logoutStore.RecordLogoutAsync(logoutWho, now, ct) ?? logoutWho;
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
            if (TurnStore.Unwrap(Store) is IGameplayFlushStore flush && await flush.FlushAsync(ct) is { } flushed)
                selected = flushed;
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
        return (DevelopmentDefaults.AccountSid, DevelopmentDefaults.NullGuid);
    }
}
