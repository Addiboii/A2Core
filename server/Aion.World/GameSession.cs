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
    /// <summary>The race of the world this wire account picked on the login port (<see cref="WorldChoices"/>); null: none known.</summary>
    public Func<string, int?>? WorldRace { get; init; }
    /// <summary>Gateway resume: the player key the client already knows (null: a new key).</summary>
    public int? PlayerKey { get; init; }
    /// <summary>
    /// The host's worlds (four worlds, Stage H): a character belongs to one (its server id on the wire), the lobby lists the
    /// picked world's characters and creates new ones there, and the open-world maps have one copy per world pair. Null: the
    /// single world of before (server id <see cref="WorldId"/>, one copy of every map).
    /// </summary>
    public WorldCatalog? Worlds { get; init; }
    /// <summary>The world this wire account picked on the login port (<see cref="WorldChoices.Server"/>); null: none known.</summary>
    public Func<string, int?>? WorldServer { get; init; }
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
    public const int FpMax = 250_000, FlyDrain = 10_000, FlySprintDrain = 15_000, GlideDrain = 7_500, FpRegenRest = 4_000, FpRegenBattle = 2_000,
        HoverDrain = 7_500;
    /// <summary>
    /// Hovering costs nothing (user rule 2026-11-08): a flight second in which the player moved less than this (cm) drains no FP;
    /// moving flight and gliding drain as above.
    /// </summary>
    public const double FlightIdleCm = 100;

    /// <summary>
    /// The flight gauge's maximum, as the official stat sheet's FPMax (stat 298; OBSERVED 250,000 at the ascension, 610,000-638,793 on
    /// a geared character, rising with equips and soul binds): the base plus every FPMax source - gear and soul-bind rolls
    /// (accessories roll FPMax 21,000-25,000), enchants, buffs, titles and the collections (each owned wing's WingEnchantEffect FPMax: 200,000
    /// the default wing, 20,000 or 50,000 the others; client tables, GearEffects).
    /// 0 without a wing.
    /// </summary>
    int FlightMax() => wingId == 0 ? 0 : Math.Max(1, FlightBase + EquippedItems().Sum(i => i.Stat("FPMax")) + EnchantStat("FPMax") + RideStat("FPMax")
        + BuffStat("FPMax") + BoardStat("FPMax") + TitleStat("FPMax") + ExtraStat("FPMax"));

    /// <summary>
    /// The base under every FPMax source: a fresh ascension owns only the default wing (its owned FPMax 200,000) and the official
    /// gauge is then exactly FpMax (250,000), so the base is FpMax less that wing's FPMax (50,000).
    /// </summary>
    int FlightBase => FpMax - (CollectionTable.Wing(GamePackets.DefaultWing(profileRace))?.Owned.GetValueOrDefault("FPMax") ?? 0);

    /// <summary>Where the player was at the last flight-gauge tick (hover detection).</summary>
    Vector3d? flightTickAt;

    /// <summary>
    /// Hovering refills the gauge only when idle (user 2026-11-08): gathering in the air (Odylle), an interaction, an item cast or a
    /// skill within the last HoverBusyMs keep the flight drain.
    /// </summary>
    public const int HoverBusyMs = 3000;
    long busyUntilMs;
    bool HoverBusy(long now) => pendingUse is not null || itemCast is not null || now < busyUntilMs;

    /// <summary>
    /// The glide mode of the running glide (EGlideStateType: 0 Normal, 1 Boost, 2 Downfall, 3 Normal_Dash - the W dive, 4 Boost_Dash,
    /// 5 DashUp), from the glide start's optional mode byte (mask bit 1). Relayed to others and priced per mode.
    /// </summary>
    byte glideMode;

    /// <summary>The flight drain per second less the buffs' FlyCost (a flight ring's 10000), never below 0.</summary>
    internal int FlightDrain(int perSecond) => Math.Max(0, perSecond - BuffStat("FlyCost"));

    /// <summary>FP per second while gliding, by mode: GlobalSetting fp_point_glide_normal_cost / boost 7500, normal_dash / boost_dash /
    /// dashup 10000 (OFFICIAL client table; the recordings drain 750 per 100 ms gliding, 7500 a second). Downfall (wings folded) is OURS.</summary>
    static int GlideDrainOf(byte mode) => mode switch
    {
        1 => (int)GatherTables.Embedded.Setting("fp_point_glide_boost_cost", GlideDrain),             // Boost
        2 => 0,                                                                                      // Downfall: falling with the wings folded
        3 => (int)GatherTables.Embedded.Setting("fp_point_glide_normal_dash_cost", 10_000),          // Normal_Dash: the W dive
        4 => (int)GatherTables.Embedded.Setting("fp_point_glide_boost_dash_cost", 10_000),           // Boost_Dash
        5 => (int)GatherTables.Embedded.Setting("fp_point_glide_dashup_cost", 10_000),               // DashUp: climbing
        _ => (int)GatherTables.Embedded.Setting("fp_point_glide_normal_cost", GlideDrain),
    };
    /// <summary>
    /// FP a flight start takes at once: GlobalSetting fp_point_fly_cost 2500. OFFICIAL (2026-11-06/07 recordings, 0x8d00 FP of the
    /// flyer): every flight started from the ground or a glide drops the gauge by exactly 2500 with the 0x3744 reply (250000 ->
    /// 247500, 194500 -> 192000), a repeated 0x3744 within the same flight takes nothing; a glide start takes only its first tick.
    /// </summary>
    public static int FlyStartCost => (int)GatherTables.Embedded.Setting("fp_point_fly_cost", 2_500);
    /// <summary>Dodges: CombatDodge (learned) and NormalDodge, its variant (Skill.dat: SP 10000, same 500 ms group cooldown).</summary>
    public const int CombatDodge = 11000100, NormalDodge = 11000101, NormalDodgeSp = 10000;
    /// <summary>
    /// Every class has its own dodge: class prefix * 1,000,000 + 100 (class-skills.json: 11000100 Gladiator ... 19000100, each class
    /// unlocks its own at level 1); the running dash is that id + 1. A Templar's dodge (12000100) was answered as an ordinary skill.
    /// From A2Core PR #4 (Artezianec): live 2026-10-30 a class-16 sprint sent 16000100 and the client dropped the sprint.
    /// </summary>
    public static bool IsCombatDodgeId(int skill) => skill % 1_000_000 == 100 && skill / 1_000_000 is >= 11 and <= 19;
    public static bool IsDodgeId(int skill) => IsCombatDodgeId(skill) || IsCombatDodgeId(skill - 1);
    /// <summary>This character's CombatDodge (the learned class dodge; Gladiator's id when none is learned).</summary>
    int ClassCombatDodge => learned.FirstOrDefault(IsCombatDodgeId) is var id and > 0 ? id : CombatDodge;
    /// <summary>
    /// This character's dodge (Skill.dat: every class has X000100, SP 30,000, and its running variant X000101, SP 10,000, one
    /// cooldown group): the learned X000100, the Gladiator's when none. Before 2026-11-07 only 11000100 was a dodge, so the other
    /// classes' Shift went through the normal cast, the sprint reply never joined it and the client cancelled the sprint.
    /// </summary>
    int ClassDodge => learned.FirstOrDefault(id => id % 1_000_000 == 100 && id / 1_000_000 is >= 11 and <= 19) is var d and not 0 ? d : CombatDodge;
    int ClassNormalDodge => ClassDodge + 1;
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
        /// <summary>Every bar in one message, as the official in-place revive (OBSERVED 2026-11-06 090856 #96734: i32 types 1-6, i64 HP).</summary>
        public byte[] FullUpdate() => GamePackets.CharPoints(key, [(CharPoint.Mp, Mp), (CharPoint.Ap, 0), (CharPoint.Sp, Sp), (CharPoint.Dp, 0), (CharPoint.Op, Op), (CharPoint.Fp, Fp)],
            [(CharPoint.Hp, Hp)]);
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
    /// <summary>
    /// 0x8d02 GlideCooltimeUpdate_NT right after a glide / flight start reply (0x3743 / 0x3744): 2000 ms. OFFICIAL bytes 18 of 18
    /// (e.g. rec 105448 #14430-14431 `4437a1620000` then `028da162d0070000`); we sent it only at world entry, with 0. Not after
    /// every start: never when the previous start was the same kind (24 of 24 glide-after-glide / fly-after-fly), mostly after a
    /// landing or a sprint (CANDIDATE rule: a start of the other kind, or the first since the player was on the ground).
    /// </summary>
    public const int GlideCooltimeMs = 2000;
    int lastAirStart;
    bool landedSinceAirStart = true;

    internal List<byte[]> AirStartReplies(ushort op)
    {
        var reply = op == 0x3706 ? GamePackets.GlideStartReply(playerKey) : GamePackets.FlyStartReply(playerKey);
        var cooldown = landedSinceAirStart || lastAirStart != op;
        if (op == 0x370a && cooldown && resources is not null) resources.Fp = Math.Max(0, resources.Fp - FlyStartCost);
        lastAirStart = op;
        landedSinceAirStart = false;
        return cooldown ? [reply, GamePackets.GlideCooltime(playerKey, GlideCooltimeMs)] : [reply];
    }

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
        registeredWings.UnionWith(WingData.OfRace(profileRace));
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
    /// (10, no reply to the dash request, froze too and was retired 2026-11-11: every cast request is answered once.)
    /// A standing dodge (no sprint) on 6-9 keeps variant 0: that is the reply this client already finishes.
    /// </summary>
    /// <summary>
    /// The dash reply shape (builder DashVariant). Default 7 since 2026-11-06: the official batch (sprint reply, dash start, the
    /// 9-byte dodge result, its abnormal, the SP, one write; recording 145451 shows it for every sprint dash). Variant 0 was the
    /// default before: the dodge got the 11-byte cast reply, which this client reads as a failure, so manual sprints ended at once.
    /// </summary>
    /// Default 8 since 2026-11-07: the same batch one official tick (50 ms) after the sprint request. Official answers 45-85 ms
    /// later; ours went out within 4 ms and the client cancelled every manual-move sprint ~15 ms after it (0x3705, 133 of 133
    /// live, bytes identical to the official Sorcerer's batch), while auto-move sprints without a dash kept going.
    public int DashVariant { get; set; } = 11;
    public const int DashVariantMax = 11, DodgeLateEndMs = 600, DashBatchDelayMs = 50, SprintReplyHoldMs = 80;
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
    bool lobbyLoginSent;
    Vector3d? position, savedPosition;
    long checkpointAt;
    readonly Dictionary<int, long> cooldowns = [];
    Dictionary<int, long> respawnAt = [];
    int actionUid;
    /// <summary>This player's entity key: 1 in a private world, unique per host in the shared one.</summary>
    readonly int playerKey = options.PlayerKey is { } kept ? options.World?.ReserveKey(kept) ?? kept : options.World?.AllocateKey() ?? 1;
    WorldPlayer? worldPlayer;
    public int PlayerKey => playerKey;

    /// <summary>Where a gateway resume would bring this player back: the character and the last map-load ticket (in the world only).</summary>
    public (int WireId, int LoadTicket)? ResumePoint => selected is { } s && resources is not null ? (s.WireId, loadTicket) : null;

    /// <summary>
    /// Gateway resume after a host restart: the client is still in the world (the gateway held it), so the character is selected
    /// again quietly and a map load to its saved place is sent, numbered after the client's last ticket. The client reloads the map
    /// and sends 0x3622, and the normal world entry follows (as after a teleport).
    /// </summary>
    public async Task<List<byte[]>> ResumeAsync(int wireId, int lastLoadTicket, long now, CancellationToken ct)
    {
        var select = PacketWriter.Opcode(0x361f).Varint(wireId).I32(0).Zeros(12).ToArray();
        await SelectAsync(select, now, ct);
        if (selected is null || spawn is not { } at) return [];
        loadTicket = lastLoadTicket + 1;
        Note($"resume: back in map {at.MapId} after a server restart");
        return [GamePackets.PrepareEnterMap(loadTicket, at.MapId, at.Position, GameTime(now))];
    }

    /// <summary>The selected character's wire id (null before a selection).</summary>
    public int? SelectedWireId => selected?.WireId;
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
    /// <summary>Four worlds: the world picked on the login port (its lobby) and the selected character's world.</summary>
    int? pickedServer, homeServerId;

    /// <summary>
    /// This player's server id on the wire (MyCharAppear/CharAppear _server_id, party, friends, kills): the character's world with
    /// four worlds, else the host's world (1204, as before).
    /// </summary>
    public int ServerId => homeServerId ?? options.WorldId;

    /// <summary>The copy of a shared map this player joins: its world pair's on the per-world maps (WorldCatalog.LayerOf); a rift visitor
    /// joins the visited world's copy (GameSession.Rifts.cs, OURS).</summary>
    int MapLayer(int mapId) => instanceKey == 0 && options.Worlds is { } worlds ? worlds.LayerOf(riftVisitWorld ?? ServerId, mapId) : 0;
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

    public async Task<List<byte[]>> HandleAsync(byte[] plain, long now, CancellationToken ct = default) =>
        OldMapFirst(await GuardedAsync(plain, now, () => HandleCheckedAsync(plain, now, ct), ct));

    /// <summary>
    /// A reply that moves the player to another map (0x3621 PrepareEnterMap_NT) carries the last news of the old map's entities
    /// (a kill, HP, a removal, a hit, an abnormal) BEFORE the move, never after: the client applies anything after 0x3621 to the
    /// new map, and our NPC keys start again at FirstNpcKey on every map. Live 2026-11-13 (spirit, Dungeon_D_0018 -> the Ascension
    /// Subspace 200005): the cavern boss died in the tick of the move, its 0x3835/0x8d04/0x8d21 on key 0x0FD0 went out after
    /// 0x3621, Ashar then appeared with the same key 0x0FD0 and the client removed her as the dead boss - HQ2101050 could not be
    /// turned in. Official sends the old map's despawns right around the move (rec 20261107-012837 #6273-6300), nothing of it later.
    /// </summary>
    internal static List<byte[]> OldMapFirst(List<byte[]> replies)
    {
        var move = replies.FindIndex(r => r.Length >= 2 && (r[0] | r[1] << 8) == 0x3621);
        if (move < 0) return replies;
        var late = new List<byte[]>();
        var kept = new List<byte[]>(replies.Count);
        for (var i = 0; i < replies.Count; i++)
            if (i > move && IsOldMapEntityNews(replies[i])) late.Add(replies[i]); else kept.Add(replies[i]);
        if (late.Count == 0) return replies;
        kept.InsertRange(move, late);
        return kept;
    }

    static bool IsOldMapEntityNews(byte[] r) => r.Length >= 2 && (r[0] | r[1] << 8) is 0x3835 or 0x8d04 or 0x8d21 or 0x8d00 or 0x3642
        or 0x3802 or 0x3804 or 0x3806 or 0x3809 or 0x382a or 0x382b or 0x382c;

    async Task<List<byte[]>> HandleCheckedAsync(byte[] plain, long now, CancellationToken ct)
    {
        // A held charge never outlives another action (user rule 2026-11-07, Deadshot): a jump, sprint, dash or another cast (a
        // dodge included) ends it first, with SkillChargingStop_NT, so the client is never left charging. Walking (0x3700) does NOT:
        // official charges keep going while the caster walks slowed (abnormal 100000401, MoveSpeed -3000); ending them on a step
        // left the client holding a charge the server had dropped, repeating its cancel 0x381f, stuck (live trace 20:12, #51375).
        if (charging is not null && plain.Length >= 2 && Opcode(plain) is 0x3702 or 0x3703 or 0x3704 or 0x370e or 0x3800)
        {
            var stopped = InterruptCharge();
            return [.. stopped, .. await HandleCoreAsync(plain, now, ct)];
        }
        return await HandleCoreAsync(plain, now, ct);
    }

    async Task<List<byte[]>> HandleCoreAsync(byte[] plain, long now, CancellationToken ct)
    {
        lastNow = now;
        if (plain.Length < 2) return [];
        var header = Opcode(plain);
        if (plain.Length == 2 && header is 0x383a or 0x383c)
        {
            // Every cast is bracketed by 0x383a/0x383c (u16 result replies). The player enters combat with the accepted cast
            // (EnterCombat: OBSERVED 20261013-144836 #873-#877, 0x383b, 0x3802, 0x3818, 0x3801, then 0x8d21 on) and leaves ~3.5 s
            // after the last action, not on 0x383c. (A2Core PR #4, Artezianec.)
            var replies = new List<byte[]> { GamePackets.RequestAck(header) };
            if (header == 0x383a)
            {
                if (!inCombat)
                {
                    inCombat = true;
                    // Entering combat takes the player off the ride (OBSERVED rec 20261106-115817 #808883 / #1147278: 0x9006 reason 2 with
                    // the own 0x8d21 combat start).
                    replies.AddRange(ForceDismount());
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
            case 0x3622:
                // The bag and equipment before the character's appearance: official sends MyCharAppear (with the Combat Power) before the
                // client's own 0x5610, so the server knows the gear already; ours waited for 0x5610 and sent Combat Power 0 (live
                // 2026-11-09: 0 in the HUD's corner, quest objects refused for combat power).
                if (inventory.Count == 0 && Store is { } entryStore && CharacterId is { } entryCharacter)
                    await Stored(async () =>
                    {
                        Remember(await LoadInventoryAsync(entryStore, entryCharacter, ct));
                        equippedWeapon = WeaponFromInventory();
                        return [];
                    });
                {
                    await LoadFieldBossKillsAsync(now, ct);   // the host's field boss kills before the first map comes up (GameSession.FieldBosses.cs)
                    // Exploration quests a former server left complete but unpaid (status 3) are paid now: their map icons show done.
                    var loaded = LoadComplete(plain, now);
                    if (loaded.Count > 0 && InWorld) loaded.AddRange(await Stored(() => PayFinishedExplorationsAsync(ct)));
                    return loaded;
                }
            case 0x3630:
                // Logout_RQ (in-game Exit). Save first: the client disconnects on the reply.
                await CheckpointAsync(now, force: true, ct);
                return [GamePackets.Logout()];
            case 0x362e: return await BuilderAsync(plain, now, ct);
            case 0x3625 when plain.Length == 2: return await LobbyAsync(now, ct);
            case CharDeleteRq when plain.Length >= 3: return await DeleteCharacterAsync(plain, now, ct);
            case CharDeleteCancelRq when plain.Length >= 3: return await CancelDeleteAsync(plain, ct);
            case CharRenameRq when plain.Length >= 4: return await RenameCharacterAsync(plain, ct);
            case BookmarkRq when plain.Length >= 8 && resources is not null: return await BookmarkAsync(plain, now, ct);
        }
        if (InWorld && ClientRequest(plain, now) is { } clientReplies) return clientReplies;
        if (InWorld && await GuildRequestAsync(plain, ct) is { } guildReplies) return guildReplies;
        if (InWorld && FieldBossRequest(plain, now) is { } fieldBossReplies) return fieldBossReplies;   // GameSession.FieldBosses.cs
        if (InWorld && await Systems2RequestAsync(plain, now, ct) is { } systems2Replies) return systems2Replies;   // GameSession.Systems2.cs
        if (await SocialRequestAsync(plain, now, ct) is { } socialReplies) return socialReplies;
        if (InWorld && await Systems3RequestAsync(plain, now, ct) is { } systems3Replies) return systems3Replies;
        if (await EquipRequestAsync(plain, ct) is { } equipped) return equipped;
        if (InWorld && await DutyRequestAsync(plain, now, ct) is { } dutyReplies) return dutyReplies;   // GameSession.Duty.cs
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
            uncovered = [.. uncovered, .. FallCheck(Opcode(plain), reported, now)];
            TrackAir(plain);
            var entering = MapEventPosition(reported, now);
            entering.AddRange(FestivalPosition(reported, now));   // checkpoints, laps (GameSession.FestivalGames.cs)
            entering.AddRange(InstanceTrigger(reported, now));
            if (worldPlayer?.Map is { } map)
            {
                worldPlayer.Position = reported;
                map.UpdateVisibility(worldPlayer);
                map.Broadcast(worldPlayer, MovementRelay(plain, reported));
            }
            // Glide and flight starts are answered (OBSERVED official: CharGlideStart_RS 0x3743 x21, CharFlyStart_RS 0x3744 x35). This
            // branch returned before the reply cases, so none was ever sent: the client glided anyway but dropped the Dash dive (a new
            // glide start in mode 3 Normal_Dash that waits for its reply) - live 2026-11-08: Dash rolled, then the player fell.
            List<byte[]> started = Opcode(plain) switch
            {
                0x3706 or 0x370a when resources is not null && wingId != 0 => AirStartReplies(Opcode(plain)),
                _ => [],
            };
            return [.. started, .. uncovered, .. entering];
        }
        switch (header)
        {
            case 0x3800:
            {
                if (ThrowBombCast(plain, now) is { } thrown) return thrown;
                var cast = CastReplies(plain, now);
                if (worldPlayer?.Map is { } castMap) castMap.Broadcast(worldPlayer, cast.Where(SharedCastPacket));
                cast.AddRange(await BreakObjectsAsync(now, ct));   // destroyable objects this cast broke (GameSession.Destroy.cs)
                // A kill's quest credit and experience call storage: they follow on the next loop turn (NextTimerAt = 0), so
                // the cast's own replies never wait on the database.
                if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
                return cast;
            }
            case SkillChargingStartRq: return ChargingStart(plain, now);
            case SkillChargingEndRq: return ChargingEnd(plain, now);
            case SkillChargingStopRq: return ChargingStop(now);
            case SkillGroundReadyRq: return GroundReady(plain, now);
            case SkillGroundStartRq: return GroundStart(plain, now);
            case SkillGroundStopRq: return GroundStop();
            case 0x5610: return await InventoryListAsync(ct);
            // WingChange_RQ: packed is_wing_skin, i32 wing (live 2026-10-15, 7 bytes). A registered wing becomes the equipped one
            // (its equipped effect in the stats), a skin the look; both are saved (setting "wing") and restored at login.
            case 0x5674 when plain.Length == 7 && resources is not null:
            {
                var skin = (plain[2] & 1) != 0;
                var wanted = BitConverter.ToInt32(plain, 3);
                if (!OwnedWings().Any(w => w.Id == wanted)) return [GamePackets.WingChangeReply(wanted, skin, 1)];
                if (skin) wingSkin = wanted;
                else if (wingId != 0) wingId = equippedWing = wanted;
                else equippedWing = wanted;
                await SaveSettingAsync("wing", new[] { equippedWing, wingSkin }, ct);
                var notice = GamePackets.WingChangeNotice(playerKey, wanted, skin);
                worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
                return [GamePackets.WingChangeReply(wanted, skin), notice, .. skin ? [] : RefreshStats()];
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
            case CoinShopBuyRq when plain.Length >= 7: return await BuyCoinShopAsync(plain, now, ct);
            case ItemRestoreRq when plain.Length >= 3: return await RestoreItemsAsync(plain, ct);
            case ItemEnchantRq when plain.Length >= 10: return await EnchantAsync(plain, ct);
            // Gathering and timed object interactions take the player off the ride (user 2026-11-12; CANDIDATE: no official mounted
            // gather recorded - the server's own dismount 0x9006 reason 2 as in combat).
            case GatherStartRq when plain.Length >= 3: busyUntilMs = now + HoverBusyMs; return [.. ForceDismount(), .. await GatherStartAsync(plain, now, ct)];
            case TitleChangeRq when plain.Length >= 6: return await TitleChangeAsync(plain, ct);
            case SkillLevelUpRq when plain.Length >= 6: return await SkillLevelUpAsync(plain, ct);
            case SpecializedEquipRq when plain.Length >= 7: return await EquipSkillPartAsync(plain, ct);   // GameSession.Specialization.cs
            case SpecializedUnequipRq when plain.Length >= 6: return await UnequipSkillPartAsync(plain, ct);
            case SpecializedChangeRq when plain.Length >= 10: return await ChangeSkillPartSlotAsync(plain, ct);
            // Daevanion (GameSession.Daevanion.cs): learn a node, forget one, reset a board, learn all / a route.
            case DaevanionOpenNodeRq when plain.Length >= 6: return await DaevanionOpenNodeAsync(plain, ct);
            case DaevanionCloseNodeRq when plain.Length >= 6: return await DaevanionCloseNodeAsync(plain, ct);
            case DaevanionResetBoardRq when plain.Length >= 6: return await DaevanionResetBoardAsync(plain, ct);
            case DaevanionOpenAllNodeRq when plain.Length >= 6: return await DaevanionOpenAllAsync(plain, ct);
            case DaevanionOpenRouteNodeRq when plain.Length >= 7: return await DaevanionOpenRouteAsync(plain, ct);
            // Monoliths (GameSession.EmpyreanTraces.cs): resonate the Empyrean Traces, the account sync.
            case MonolithPackets.MonolithResonateRq when plain.Length >= 10: return await MonolithResonateAsync(plain, ct);
            case MonolithPackets.SyncMonolithAccountAchievementsRq: return SyncMonolithAccountAchievements();
            case TitleEquipRq when plain.Length >= 7: return await TitleEquipAsync(plain, ct);
            case SkinEquipRq when plain.Length >= 3: return await SkinEquipAsync(plain, ct);
            case ItemImprintRq when plain.Length >= 22: return await ImprintAsync(plain, ct);
            case ItemSurpassEnchantRq when plain.Length >= 11: return await SurpassAsync(plain, ct);
            case SkinCombineRq when plain.Length >= 4: return await SkinCombineAsync(plain, ct);
            case SkinExtractionRq when plain.Length >= 3: return await SkinExtractionAsync(plain, ct);
            case ItemExceedEnchantRq when plain.Length >= 11: return await AmplifyAsync(plain, ct);
            // Godstones, succession, soul add (GameSession.Godstones.cs, GameSession.Succession.cs).
            case ItemEnchantGodstoneRq when plain.Length >= 15: return await EnchantGodstoneAsync(plain, ct);
            case CombineGodstoneRq when plain.Length >= 3: return await CombineGodstonesAsync(plain, ct);
            case ItemSuccessionRq when plain.Length >= 18: return await SuccessionAsync(plain, ct);
            case ItemSoulAddRq when plain.Length >= 14: return await SoulAddAsync(plain, ct);
            // The Pantheon's decorations (GameSession.Pantheon.cs).
            case AgitDecoEquipRq when plain.Length >= 7: return await AgitDecoEquipAsync(plain, ct);
            case AgitDecoUnEquipRq when plain.Length >= 6: return await AgitDecoUnEquipAsync(plain, ct);
            case QuestTeleportRq when plain.Length >= 6: return QuestTeleport(plain, now);
            case GatherStopRq when plain.Length >= 3: return GatherStop(plain);
            case GatherSkillAcquireRq when plain.Length >= 3: return await AcquireGatherSkillAsync(plain, ct);
            case GatherSkillUpgradeRq when plain.Length >= 4: return await UpgradeGatherSkillAsync(plain, ct);
            case GatherSkillResetRq: return await ResetGatherSkillsAsync(ct);
            case CraftStartRq when plain.Length >= 7: return await CraftAsync(plain, now, ct);
            case GatherStopRq when plain.Length >= 3: pendingUse = null; return [];
            case ShopBuyRq when plain.Length >= 4: return await BuyGoodsAsync(plain, now, ct);
            case NpShopPurchaseRq when plain.Length >= 10: return await PremiumShopPurchaseAsync(plain, now, ct);
            case RandomShopInfoRq when plain.Length >= 3: return await RandomShopInfoAsync(plain, now, ct);
            case RandomShopPurchaseRq when plain.Length >= 3: return await RandomShopPurchaseAsync(plain, now, ct);
            case RandomShopRefreshRq when plain.Length >= 3: return await RandomShopRefreshAsync(plain, now, ct);
            case RandomShopRefreshCheckRq when plain.Length >= 3: return await RandomShopRefreshCheckAsync(plain, now, ct);
            case NpShopGoodsLimitRq when plain.Length >= 3: return await PremiumGoodsLimitAsync(plain, now, ct);
            case ShopSellRq when plain.Length >= 4: return await SellItemsAsync(plain, ct);
            case ItemRemoveRq when plain.Length >= 4: return await RemoveItemsAsync(plain, ct);
            case QuickSlotContextSkillRq when plain.Length >= 8: return await SetContextSkillAsync(plain, ct);
            case QuickSlotContextSkillResetRq when plain.Length == 2: return await ResetContextSkillsAsync(ct);
            case ItemStoreRq when plain.Length >= 8: return await StoreOrTakeOutAsync(plain, store: true, ct);
            case ItemTakeOutRq when plain.Length >= 8: return await StoreOrTakeOutAsync(plain, store: false, ct);
            case ItemSortRq when plain.Length == 7: return await SortContainerAsync(plain, ct);
            case MergeStorageItemRq when plain.Length >= 9: return await MergeStorageItemAsync(plain, ct);
            case GuildStorageItemSortRq when plain.Length == 6: return GuildStorageItemSort(now);
            case QuickSlotSetRq when plain.Length >= 27: return await SetQuickSlotAsync(plain, ct);
            case QuickSlotResetRq when plain.Length == 3: return await ResetQuickSlotAsync(plain[2], ct);
            case RidePackets.OpenBoxRq when plain.Length == 15: return await OpenBoxRequestAsync(plain, now, ct);
            case RidePackets.ItemUseRq when plain.Length == 10: return await UseItemAsync(BitConverter.ToInt64(plain, 2), now, ct);
            case RidePackets.ItemUseByIdRq when plain.Length == 22:
                return await UseItemByIdAsync(BitConverter.ToInt64(plain, 2), BitConverter.ToInt32(plain, 10), BitConverter.ToInt64(plain, 14), now, ct);
            case RidePackets.AcquireRq when plain.Length == 6: return await AcquireRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.CrystallizeRq when plain.Length >= 3: return await CrystallizeAsync(plain, ct);
            case RidePackets.BoardRollRq when plain.Length >= 5: return await RollBoardAsync(plain, ct);
            case RidePackets.BoardLockRq when plain.Length == 6: return await LockBoardSlotAsync(plain[2], plain[3], plain[4], plain[5] != 0, ct);
            // OBSERVED live 2026-11-06 (36 presses at a death, all unanswered before): 12 bytes, a field mask (01) first, then
            // ERespawnType u8 (3 Item) and the item uid i64. The unmasked 11-byte form and the type alone are taken too.
            case RidePackets.RespawnRq when plain.Length == 12: return await RespawnRequestAsync(plain[3], BitConverter.ToInt64(plain, 4), now, ct);
            case RidePackets.RespawnRq when plain.Length == 11: return await RespawnRequestAsync(plain[2], BitConverter.ToInt64(plain, 3), now, ct);
            case RidePackets.RespawnRq when plain.Length is 3 or 4: return await RespawnRequestAsync(plain[^1], 0, now, ct);
            case RidePackets.UseRebirthRq when plain.Length == 3: return UseRebirth(plain[2] != 0, now);
            case RidePackets.LevelUpRq when plain.Length == 6: return await LevelUpRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case RidePackets.GetOnRq when plain.Length == 6: return GetOnRide(BitConverter.ToInt32(plain, 2));
            case RidePackets.GetOffRq when plain.Length == 3: return GetOffRide(plain[2] != 0);
            case RidePackets.DefaultSetRq when plain.Length == 6: return await SetDefaultRideAsync(BitConverter.ToInt32(plain, 2), ct);
            case 0x562f when plain.Length == 10 && inventory.ContainsKey(BitConverter.ToInt64(plain, 2)):
                return await SoulBindCompleteAsync(BitConverter.ToInt64(plain, 2), ct);
            case 0x3704 when plain.Length == 10: return SprintOn(now);
            // Glide (0x3706) and flight (0x370a) starts, sent once a wing is equipped (live 2026-10-15).
            case 0x3706 or 0x370a when resources is not null && wingId != 0: return AirStartReplies(Opcode(plain));
            case 0x3705 when plain.Length == 10: return SprintOff(now);
            // CharRestStart_RQ / CharRestStop_RQ {key varint, 0 OBSERVED "473700"}: sit down / stand up (dev 0x36A4-0x36A9,
            // usmap layouts; no official reply sample, CANDIDATE). Others see the NT.
            // Inspect a player, emotes, the content sweeps (GameSession.Inspect.cs).
            case InspectPackets.ViewCharRq when plain.Length >= 4 && resources is not null: return ViewChar(plain);
            case InspectPackets.SocialActionPlayRq when plain.Length >= 3 && resources is not null: return SocialActionPlay(plain);
            case InspectPackets.ContentsSweepRq when resources is not null: return ContentsSweep(plain);
            case InspectPackets.BossChallengeSweepRq when resources is not null: return BossChallengeSweep(plain);
            // Per-character settings (GameSession.Settings.cs).
            case SealStoneActivateRq or SealStoneDeactivateRq when resources is not null: return await SealStoneToggleAsync(Opcode(plain) == SealStoneActivateRq, ct);
            case SealStoneRegisterRq when plain.Length >= 10 && resources is not null: return await RegisterSealStoneAsync(BitConverter.ToInt64(plain, 2), ct);
            case PotionAutoUseRq when plain.Length >= 11 && resources is not null: return await SetPotionAutoUseAsync(plain, ct);
            case ItemAutoUseRq when plain.Length >= 7 && resources is not null: return await SetItemAutoUseAsync(plain, ct);
            // The daily dungeon's buff choice (GameSession.DungeonBuffs.cs; CANDIDATE layout {dungeon i32, slot i32}).
            case DailyDungeons.DungeonBuffSelectRq when plain.Length >= 10 && resources is not null: return SelectDungeonBuff(plain, now);
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
                    if (target < 0) return [];   // no such key; the reply's varint could not carry it
                    selectedTarget = target;
                    return targets?.ContainsKey(target) == true ? [GamePackets.SetMainTargetReply(target), GamePackets.TargetUpdate(target)] : [GamePackets.SetMainTargetReply(target)];
                }
                catch (ProtocolException) { return []; }
            // NPC interaction (dialog) and quest requests; layouts from the usmap, never yet seen from the client.
            case 0xe240 when QuestKey(plain, 2) is { } npcKey: return await NpcInteractionAsync(npcKey, now, ct);
            // InteractEnvObj_RQ {target_key, item_key, target_id} (usmap; client encoding unseen, logged).
            case 0x3636:
            {
                var interacted = await InteractEnvObjAsync(plain, now, ct);
                // An interaction that starts (0x3638: chests, nodes, doors) dismounts; walking into an orb (a summoned object) does not.
                return interacted.Any(m => m.Length > 2 && m[0] == 0x38 && m[1] == 0x36) && !interacted.Any(m => m.Length > 1 && m[0] == 0x05 && m[1] == 0x8d)
                    ? [.. ForceDismount(), .. interacted] : interacted;
            }
            case CubeRewardRq: return await CubeRewardAsync(plain, now, ct);
            case PostLoadRq when plain.Length >= 3: return await LoadPostsAsync(plain, ct);
            case PostReadRq: return await MarkPostsAsync(plain, delete: false, ct);
            case PostDeleteRq: return await MarkPostsAsync(plain, delete: true, ct);
            case PostTakeRq when plain.Length >= 11: return await TakePostAsync(plain, ct);
            case ItemDecomposeRq when plain.Length >= 4: return await DecomposeAsync(plain, ct);
            case SlotExtendRq when plain.Length >= 16: return await SlotExtendAsync(plain, ct);
            case TakeAchievementRewardRq: return await TakeAchievementRewardAsync(plain, ct);
            case TakeAchievementGroupRewardRq: return await TakeGroupRewardAsync(plain, ct);
            case TakeAchievementRewardBatchRq: return await TakeRewardBatchAsync(plain, ct);
            case AttendanceRq: return Attendance(now);
            case TakeBattlePassRewardRq: return await TakeBattlePassRewardAsync(plain, ct);
            case MatchingStartRq: return MatchingStart(plain, now);
            case MatchingStartResponseRq: return MatchingStartResponse(plain, now);
            case MatchingCancelRq: return MatchingCancel(now);
            case ArenaDungeonReadyRq: return ArenaReady(plain, now);
            // ArenaRecord_RQ {version u8, EMatchingType u8}: no records yet (OBSERVED official reply "2ce3 0000 01 03 00": result, version 1, type, none).
            case ArenaRecordRq when plain.Length >= 4: return [PacketWriter.Opcode(ArenaRecordRs).U16(0).U8(1).U8(plain[3]).Varint(0).ToArray()];
            case PeriodCollectionListRq: return PeriodCollectionListRequest(plain, now);
            case RegisterPeriodCollectionRq when plain.Length >= 11: return await RegisterCollectionAsync(plain, now, ct);
            case BossChallengeEnterRq: return BossChallengeEnter(plain, now);
            case DungeonEnterRq: return await DungeonEnterAsync(plain, now, ct);
            // The expedition rooms (DungeonRoom* 0x97xx): GameSession.DungeonRooms.cs through Systems3RequestAsync.
            case BattlePassPurchaseRq when plain.Length >= 6: return [PacketWriter.Opcode(BattlePassPurchaseRs).U16(1).I32(BitConverter.ToInt32(plain, 2)).ToArray()];
            case CloseNpcAchievementRq: return CloseNpcAchievement(plain);
            case CloseEnvObjAchievementRq: return CloseEnvObjAchievement(plain);
            case SkillLevelDownRq: return await SkillLevelDownAsync(plain, ct);
            case SkillLevelResetRq: return await SkillLevelResetAsync(plain, ct);
            // InteractEnvObjStop_RQ {key}: a timed interaction cancelled (moved, or the client's cancel).
            case 0x363d when QuestKey(plain, 2) is { } stopKey:
                // OBSERVED (185000 #6763-#6764, #26846-#26847; 162208 #5193): 0x363e {0, key} with 0x3639 reason 0x1906 for the use
                // in progress; with none, 0x363e {0x3813, key 0}.
                if (pendingUse?.Key != stopKey) return [GamePackets.InteractEnvObjStopReply(0, GamePackets.InteractStopNothing)];
                pendingUse = null;
                return [GamePackets.InteractEnvObjStopReply(stopKey), GamePackets.InteractEnvObjInterrupted(stopKey, GamePackets.InterruptStopped, playerKey)];
            case 0xe242 when QuestKey(plain, 2) is { } npcKey:
                // Once the conversation is over on the server's side (the partner removed and a map load pending, as after an
                // NPC's layer entry) the official answer is the failure 0x3e08 with key 0 (OBSERVED #9656); the talk mode ends on it.
                if (reloading || targets?.ContainsKey(npcKey) != true) return [GamePackets.FinishNpcInteractionReply(0, FinishNoConversation)];
                return [GamePackets.FinishNpcInteractionReply(npcKey), .. MapEventTalked(npcKey, null, close: true, now), .. await QuestDialogClosedAsync(npcKey, ct),
                    .. await UnclaimedRewardsAsync(npcKey, ct)];
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
            case 0x4004: return instanceKey != 0 && !reloading ? [PacketWriter.Opcode(0x4005).U16(0).ToArray(), .. LeaveByClient(now)] : [PacketWriter.Opcode(0x4005).U16(1).ToArray()];
            case 0x930d: return instanceKey != 0 && !reloading ? [PacketWriter.Opcode(0x930e).U16(0).ToArray(), .. LeaveByClient(now)] : [PacketWriter.Opcode(0x930e).U16(1).ToArray()];
            // ExitInstanceLayer_RQ at its live opcode 0xe21d (usmap size match, host log 2026-11-05: 2 bytes, unanswered); reply 0xe21e.
            case 0xe21d: return instanceKey != 0 && !reloading ? [PacketWriter.Opcode(0xe21e).U16(0).ToArray(), .. LeaveByClient(now)] : [PacketWriter.Opcode(0xe21e).U16(1).ToArray()];
            // UseTeleportArtifact_RQ {artifact id i32}: travel to a bound Kibelisk. OBSERVED reply (official recording
            // 20261106-115817, 5 uses): 0x9237 {u16 result 0, i32 artifact id}. The official server moves the character on the
            // same map without a load (MyCharAppear_NT); ours uses the map load, which also covers other maps. The official
            // use also lowered one item stack (0x561b, a travel cost): not charged yet.
            case 0x9236 when plain.Length >= 6: return await UseTeleportArtifactAsync(BitConverter.ToInt32(plain, 2), now, ct);
            // QuestTakeReward_RQ {quest uid i32, select item index i32 (-1 none)}: the chosen item of a selectable reward.
            case 0x8d0e when plain.Length >= 6: return await QuestRewardAsync(BitConverter.ToInt32(plain, 2), ct, plain.Length >= 10 ? BitConverter.ToInt32(plain, 6) : -1);
            case 0x8d2b when plain.Length >= 6: return await QuestStepTalkAsync(plain, now, ct);
            // ClientObjectiveComplete_RQ {kind u8, id i32, objective index i32} (11 bytes live; the official reply mirrors it).
            case 0x9230 when plain.Length >= 11:
                return await ClientObjectiveAsync(plain[2], BitConverter.ToInt32(plain, 3), BitConverter.ToInt32(plain, 7), now, ct);
            // CutsceneComplete_RQ {cutscene name, group request key i64}: a cutscene the client was told to play ended (OBSERVED
            // live 2026-10-19 for the lake cave's exit cutscene). It finishes CompleteCutscene objectives and releases a move
            // that waited for its cutscene (a PCTeleport's value 3).
            case 0xe334 when CutsceneComplete(plain) is { } ended: return MapEventCutsceneDone(ended.Name, ended.Key, now);
            // UpdateClientOption_RQ {option data bytes}: the client's settings (key bindings, UI; a zlib blob, ~180 bytes) are
            // kept per character and come back in SelectCharacter_RS (live 2026-11-07: they were dropped, settings reset at relog).
            case 0x8d40: return await SaveClientOptionsAsync(plain, ct);
            // UpdateClientEnvSetting_RQ (dev 0x397D): the RS and the settings echoed as the NT (CANDIDATE; not yet seen from our client).
            // Stored (setting "client_env") and sent back at the next character select: it was echoed but never kept, so auto-potion,
            // invite/trade/duel rejection, auto-loot and the control mode reset at every login (live 2026-11-09).
            case 0x8d42 when plain.Length > 2 && plain.Length <= 258:
                clientEnv = plain[2..];
                await SaveSettingAsync("client_env", Convert.ToBase64String(clientEnv), ct);
                return [GamePackets.ClientEnvSettingReply(), [0x44, 0x8d, .. clientEnv]];
            // MoveToLoginServer_RQ (the character screen's back button; live 2026-10-19 unanswered, the client hung):
            // MoveToLoginServer_RS {result} (usmap; u16 on the wire as every observed result), then the client goes to the
            // login server on its own.
            case 0x3617: return [PacketWriter.Opcode(0x3618).U16(0).ToArray()];
            // OBSERVED (115817, 14 of 14): every CharAllStats_RS is followed by 0x4301 {00} (not named yet).
            case 0x3648: { var (sheet, hpMax) = StatSheet(); SentStatsBaseline(sheet, hpMax); return [GamePackets.CharAllStats(sheet, hpMax), [0x01, 0x43, 0x00]]; }
            case 0x3659:
                // Comm key request (only when Login_RS gave none). No unlock update with it: chat (45) is in the select's 0xe257 as
                // official (it came as an 0xe258 here and at every load, a repeated System_ContentsUnlocked; info-popups 2026-11-11).
                return [GamePackets.CommServerKeyReply()];
            case 0xe34f when Store is null && !lobbyLoginSent:
                // Login_RS resent from the lobby: its handler needs [A+0x428], absent when the
                // instant game-login reply arrives, so the chat server key (Lime+0x80) stayed
                // empty. Resending in the world dropped the connection (research/chat-and-gm.md).
                lobbyLoginSent = true;
                return [GamePackets.LoginCharacters([], $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", "1204",
                    GamePackets.CommServerKey, now, isBornServer: true, chatEnableLevel: 0)];
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
        await CheckpointAsync(now, force: true, ct);
        LeaveWorld();
        if (Store is null) return [GamePackets.LobbyCharacters([])];
        // The plain (uncompressed) full list. Settled live 2026-10-19 from the client recordings of the four research
        // variants: after the plain full list the player picked a character 6 s later; the compressed one made the client
        // drop the connection and log in again; the empty ones showed no characters.
        return await Stored(async () => [GamePackets.LobbyCharacters(await LobbyCharactersAsync(now, ct))]);
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
        summons.Clear();
        respawnAt = [];
        ai = new NpcAi(rng, data);
        invisible = false;
        invincible = default;
        noCool = false;
        sprinting = false;
        Resting = false;
        reloading = false;
        ResetCastTimers();
        ResetBombs();
        ResetChains();
        killWorkDue = false;
        // The instance ends with the character (character switch): no copy, scripts, way out or pending quest credit carry
        // over to the next selection, which would otherwise resume the old run or leave by the previous character's way out.
        instanceKey = 0;
        instanceExitAt = null;
        instanceReturn = null;
        copy = null;
        mapEventsMap = 0;
        mapEventRuns.Clear();
        pendingExit = null;
        clearedEvents.Clear();
        killedNpcs.Clear();
    }

    // ---------- Quests ----------

    /// <summary>Kina is an item (Item.dat Gold).</summary>
    public const int KinaItemId = 930100001;
    /// <summary>Non-binding (tradable) Kina, the dev menu's PlentyNonBindingGold; GetNonBindingKina items grant it. Kina costs
    /// are paid with bound Kina (KinaItemId) first and non-binding Kina for the rest (CostStacks).</summary>
    public const int NonBindingKinaItemId = 930100031;
    /// <summary>Kina (All), Item.dat GoldCombined: bound + non-binding Kina, a total and never a stack of its own (Currencies.cs);
    /// a cost in it is paid like a KinaItemId cost.</summary>
    public const int AnyKinaItemId = Currencies.GoldCombined;
    QuestLog? questLog;
    readonly List<int> killedNpcs = [];
    int selectedTarget;
    long nextDevQuestUid = 1;

    public QuestLog? Quests => questLog;

    /// <summary>A development login (no store) gets an in-memory quest log: quest gates (map events, dungeon entries) can be exercised in checks.</summary>
    public QuestLog DevelopmentQuestLog() => questLog ??= new QuestLog(QuestBook.Embedded, profileRace == 0 ? 1 : profileRace);

    // Wire(QuestRecord): GameSession.Duty.cs (duty quests carry their random rewards).

    /// <summary>An i32 NPC key at the offset, or a varint when the body is not exactly that long (layout unconfirmed).</summary>
    /// <summary>FinishNpcInteraction_RS result when no conversation is open any more (OBSERVED `083e 00000000` after a layer move).</summary>
    const int FinishNoConversation = 0x3e08;

    static int? QuestKey(byte[] plain, int offset)
    {
        // Object and NPC keys are positive: a negative one (an i32 or a varint above 2^31) names nothing and could not even be
        // echoed in a reply (the varint writer refuses it; fuzz 2026-11-09: InteractEnvObj_RQ ended the connection).
        if (plain.Length == offset + 4) return BitConverter.ToInt32(plain, offset) is var raw and >= 0 ? raw : null;
        try { return plain.Length > offset && (int)new PacketReader(plain) { Offset = offset }.Varint() is var key and >= 0 ? key : null; }
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
        // Steps waiting on quests that are already complete move on before the list is sent (a quest stuck by the missing
        // re-check above heals at the next login).
        foreach (var moved in questLog.Recheck(level))
        {
            questLog.TakeMilestones(moved.Code);
            questLog.Set(await store.SaveQuestAsync(character, moved, ct));
        }
        // Collect objectives that missed their items before 2026-11-13 (GameSession.QuestItems.cs) count the bag now.
        foreach (var holding in HeldItemQuests(await store.InventoryAsync(character, ct)))
        {
            questLog.TakeMilestones(holding.Code);
            questLog.Set(await store.SaveQuestAsync(character, holding, ct));
        }
        // The Ascension Gauge's points from the quests rewarded since it started (Quests.cs, GaugeRecount): a gauge stored before
        // 2026-11-12 sat at 0 points; it gets its current total here, so the list below shows it.
        foreach (var gauge in questLog.GaugeRecount(level))
        {
            questLog.TakeMilestones(gauge.Code);
            questLog.Set(await store.SaveQuestAsync(character, gauge, ct));
        }
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

    /// <summary>
    /// Quests the server pays as soon as they complete: Complete "Auto"/"None", and the exploration quests (Quest.dat type
    /// Exploration: 162 Sealed dungeons, 42 strongholds/campsites; Complete "UI", but no turn-in). OBSERVED (capture
    /// 20261106-105448 #78410-78424, EQ2810013 "Fafnite Storage Room", Seal): 0x8d10 {2, step 1, goal 1}, 0x8d10 {3, 1, 1}, then
    /// with no claim the reward follows ~100 ms later, 0x8d07 (its 22,337 exp) and 0x8d0f; the next login lists it at status 4,
    /// as every official list does for finished explorations (EQ2820001 "Graverobber Den", a stronghold). The world map draws
    /// Table_Exploration's CompleteIconType only for a rewarded one: ours stayed at 3 and the map never showed them done.
    /// </summary>
    static bool PaysAtCompletion(QuestDefinition quest) => quest.Complete is "Auto" or "None" || quest.Type == "Exploration";

    /// <summary>
    /// Exploration quests left complete but unpaid by the server before 2026-11-11 (status 3 rows: Axel3 EQ1820001, EQ1810024)
    /// are paid at the map load, as they would have been at completion: the experience, 0x8d0f, the rechecks.
    /// </summary>
    async Task<List<byte[]>> PayFinishedExplorationsAsync(CancellationToken ct)
    {
        var replies = new List<byte[]>();
        if (questLog is null) return replies;
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Complete && questLog.Book.Find(q.Code) is { Type: "Exploration" }).ToList())
            replies.AddRange(await QuestRewardAsync((int)record.Uid, ct));
        return replies;
    }

    /// <summary>Saves and reports changed quests (0x8d10); quests that complete by themselves pay at once.</summary>
    async Task<List<byte[]>> QuestChangesAsync(IEnumerable<QuestRecord> changed, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        var completed = false;
        foreach (var record in changed)
        {
            // The step's goal reached first (the official pattern), then the saved state (the next step or the completion).
            foreach (var milestone in questLog?.TakeMilestones(record.Code) ?? [])
                if (milestone.Uid != 0) replies.Add(GamePackets.QuestProgress(Wire(milestone)));
            var saved = await SaveQuestAsync(record, ct);
            replies.Add(GamePackets.QuestProgress(Wire(saved)));
            if (saved.Status == QuestLog.Complete) completed = true;
            if (saved.Status == QuestLog.Complete && questLog!.Book.Find(saved.Code) is { } auto && PaysAtCompletion(auto))
                replies.AddRange(await QuestRewardAsync((int)saved.Uid, ct, auto.Rewards?.Select is { Length: > 0 } ? 0 : -1));
        }
        // A quest that completed without a reward turn-in (an exploration quest such as EQ1820001 "Ugaros Thieves Campsite",
        // Complete "UI") also moves the quests waiting on it (ClearQuest: HQ1102070 step 7 stayed at 0 for Axel3, live
        // 2026-11-11): before, only a reward turn-in re-checked them.
        if (completed && questLog?.Recheck(level) is { Count: > 0 } waiting) replies.AddRange(await QuestChangesAsync(waiting, ct));
        // A new step may count items already in the bag (GameSession.QuestItems.cs).
        if (HeldItemQuests(storedItems.Values) is { Count: > 0 } holding) replies.AddRange(await QuestChangesAsync(holding, ct));
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
            {
                replies.Add(GamePackets.QuestAdd(Wire(await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct))));
                replies.AddRange(QuestStartCutscene(code));
            }
        }
        if (RunsMapEvents && !reloading && mapEventsMap == worldMap) replies.AddRange(StartEligibleEvents(lastNow));
        return replies;
    }

    /// <summary>
    /// IncompleteQuest (taken, not rewarded), ClearQuestStep (QuestStep id = quest id * 100 + order), SubZone (standing in it),
    /// UnifiedObjectiveScore (the Ascension Gauge's points: AQ1602010 needs 320; Quests.cs), the masteries (the stored gathering or
    /// craft mastery level reaches the value: the 12 GatherCraftMastery quests MQ1902010-060 / MQ2902010-060 at 50); CloseEnvObj never holds.
    /// </summary>
    bool QuestConditionsHold(QuestDefinition quest) => (quest.Conditions ?? []).All(c => c.Type switch
    {
        "IncompleteQuest" => questLog?.Get(c.Value) is { Status: QuestLog.Incomplete or QuestLog.Complete },
        "ClearQuestStep" => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stepId)
            && questLog?.Book.StepOf(stepId) is { } s && questLog.Book.ById.TryGetValue(s.Quest, out var code) && questLog.Get(code) is { } r && (r.Status >= QuestLog.Complete || r.Step > s.Step),
        "SubZone" => position is { } at && Subzones.InSubzone(worldMap, c.Value, at),
        "UnifiedObjectiveScore" => questLog?.ScoreReached(c.Value) == true,
        _ => MasteryLevelOf(c.Type) is { } mastery && int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var needed)
            && mastery >= needed,
    });

    /// <summary>
    /// The level of the mastery an AcquirableConditions type names: ExtractForceMastery the gathering mastery (the one gather mastery
    /// this server keeps, category 0; CANDIDATE: essence extraction read as gathering), the crafts by their table (ConversionMasteries).
    /// </summary>
    int? MasteryLevelOf(string type) =>
        type == "ExtractForceMastery" ? gatherLevel
        : ConversionMasteries.TryGetValue(type, out var table) && CraftCategories.TryGetValue(table, out var category) ? CraftMastery(category).Level : null;

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
    /// Clicking an NPC (0xe240): the dialog opens (0xe241, five minutes). Opening it counts nothing: the quest's AskNpc talk counts
    /// when the client says the talk is over (0x8d2b, QuestStepTalkAsync; CloseNpc "come close" too). OBSERVED
    /// (official 2026-11-06/07): 0 of 28 AskNpc progresses came with the 0xe241; each followed the 0x8d2c reply 0.6-1.7 s later. We
    /// counted at the opening before (106 times in the 2026-11-07..10 traces): the next step, its map event or its NPC moves
    /// started while the conversation was still on screen (user 2026-11-11: "finish the conversation, then the NPC should move").
    /// </summary>
    Task<List<byte[]>> NpcInteractionAsync(int npcKey, long now, CancellationToken ct)
    {
        var replies = new List<byte[]> { GamePackets.StartNpcInteractionReply(npcKey, now + 300_000) };
        if (targets?.GetValueOrDefault(npcKey) is { } npc)
            NpcOutOfReach(npc, "talk", now, enforce: false);   // logged only: scripted dialogs may open from a distance
        return Task.FromResult(replies);
    }

    /// <summary>The dialog closed (0xe242): the quests' CloseNpc objectives for this NPC, after the 0xe243 reply (CANDIDATE fallback: next to it).</summary>
    async Task<List<byte[]>> QuestDialogClosedAsync(int npcKey, CancellationToken ct)
    {
        if (questLog is null || targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        return await Stored(() => QuestChangesAsync(questLog.ClosedDialog(npc.NpcId, level), ct));
    }

    /// <summary>
    /// A finished quest handed in at this NPC is always paid (user rule 2026-11-07): with a bag the client counts as full (the
    /// user carries ~2000 items over the client's own bag size) the client refuses the reward itself and never sends
    /// QuestTakeReward_RQ (0x8d0e: none in the live traces). When the dialog closes (0xe242) with the quest still unpaid, the
    /// server pays it (our bag has no limit, the items always fit). OURS (official overflow behaviour not recorded).
    /// </summary>
    async Task<List<byte[]>> UnclaimedRewardsAsync(int npcKey, CancellationToken ct)
    {
        if (questLog is null || targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        var replies = new List<byte[]>();
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Complete).ToList())
            if (questLog.Book.Find(record.Code) is { Complete: "Npc", FinishNpc: { } finisher } found && finisher == npc.NpcId
                && found.Rewards?.Select is not { Length: > 0 })   // a reward with a choice waits for the reward window
                replies.AddRange(await QuestRewardAsync((int)record.Uid, ct));
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

    /// <summary>Used-up objects on their way out: EnvObjDie at DieAt, the despawn (state 4, reason 3) at GoneAt.</summary>
    readonly List<(int Index, long DieAt, long GoneAt)> objectRemovals = [];

    /// <summary>
    /// When a used-up object dies and despawns, after its use. OBSERVED (our recordings, 40 files): EnvObjDie 0x8d05 about 50 ms
    /// after the use's 0x8d6d (176 of 185), about 650 or 1150 ms for objects with an EnvObjData FinishDuration of 500 or 1000
    /// (9), then the despawn 0x3635 state 4 reason 3 about 50 ms later (188 of the despawns after a use; state 1 reason 1: 6).
    /// The community fork measured the same (FinishDuration + 150 ms, 50 ms with none).
    /// </summary>
    public static (long Die, long Gone) RemovalDelays(int finishMs) => finishMs > 0 ? (finishMs + 150, finishMs + 200) : (50, 100);

    /// <summary>The object dies and despawns for this player on its timing (it stays shown until then).</summary>
    void ScheduleRemoval(int index, long now)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var (die, gone) = RemovalDelays(index < objects.Count ? objects[index].FinishMs : 0);
        objectRemovals.RemoveAll(r => r.Index == index);
        objectRemovals.Add((index, now + die, now + gone));
    }

    /// <summary>The due EnvObjDie and despawns of used-up objects (only those this player still has shown).</summary>
    List<byte[]> ObjectRemovalTick(long now)
    {
        if (objectRemovals.Count == 0) return [];
        var replies = new List<byte[]>();
        for (var i = 0; i < objectRemovals.Count; i++)
        {
            var (index, dieAt, goneAt) = objectRemovals[i];
            var key = WorldObjects.FirstKey + index;
            if (dieAt != long.MinValue && now >= dieAt)
            {
                if (shownObjects.Contains(index)) replies.Add(GamePackets.EnvObjDie(key));
                objectRemovals[i] = (index, long.MinValue, goneAt);
            }
            if (now >= goneAt && shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        objectRemovals.RemoveAll(r => now >= r.GoneAt);
        return replies;
    }
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
        else candidates.UnionWith(WorldObjects.RingsOn(worldMap));   // the open world's flight rings, seen from afar (OURS: RingRange)
        foreach (var i in candidates)
        {
            if (i >= objects.Count) continue;
            var o = objects[i];
            double dx = o.Position[0] - at.X, dy = o.Position[1] - at.Y;
            var range = !instance && o.FlightRing ? WorldObjects.RingRange : WorldObjects.VisibleRange;
            var near = (instance && o.LongRange || dx * dx + dy * dy <= range * range) && !MapEventHidden(i, o)
                && !(instance && RemovedBarrier(o)) && PoolOrRiftShown(i, o, now);   // pools and rifts: only what stands now (GameSession.Rifts.cs)
            if (near && !Gathered(i, now) && !usedObjects.Contains(i) && shownObjects.Add(i))
            {
                replies.Add(o.Pool is null && o.Rift is null ? PlacedSpawn(WorldObjects.FirstKey + i, o, now)   // a trace or bGetRewardOnce box with its spawn achievement (GameSession.EmpyreanTraces.cs)
                    : PoolOrRiftSpawn(i, o, now));
                // A door the map event already opened (ChangeEnvObjState Finish) comes into view open.
                if ((CopyOwner ?? this).finishedObjects.Contains(i)) replies.Add(GamePackets.InteractEnvObjFinishState(WorldObjects.FirstKey + i));
            }
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
    /// Using a quest object (0x3636 with its key): in range, 0x3638 started. OBSERVED (community recordings 2026-11-04 to 06,
    /// reports/movement.md 1-2): an object with an InteractTime (EnvObjData) answers 0x3637 at once (+50 ms) and at the end
    /// 0x363a with 0x8d6d (finish state) when it is used up or 0x363b (available) when it stays; an instant one sends 0x363a,
    /// 0x8d6d, [0x363b], then the 0x3637 reply (capture 20261013-144836 #10090-10093). CANDIDATE: the rule "0x8d6d = used up,
    /// 0x363b = usable again" (8 env ids). Gather nodes keep 0x363a, 0x8d6d, 0x363b, 0x3637 (not recorded; without 0x8d6d/0x363b
    /// the client kept the gather result panel open).
    /// </summary>
    async Task<List<byte[]>> UseObjectAsync(byte[] plain, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        // A summoned object (a recovery orb, walked into): GameSession.EnvBoxes.cs.
        if (EnvSummonKeyIn(plain) is { } summonedKey) return PickUpEnvSummon(summonedKey, now);
        // Entering a trigger actor's volume (the client reports it): started, completed, finish and available states, the
        // reply (OBSERVED for every subspace trigger). The objectives themselves go by the position reports.
        foreach (var key in EnvObjKeys(plain))
            if (key is { } tk && tk - WorldObjects.FirstTriggerKey is var ti && ti >= 0 && ti < WorldObjects.TriggersOn(worldMap).Count)
                return [GamePackets.InteractEnvObjStarted(tk, playerKey), GamePackets.InteractEnvObjCompleted(tk, playerKey),
                    GamePackets.InteractEnvObjFinishState(tk), GamePackets.InteractEnvObjAvailableState(tk), GamePackets.InteractEnvObjReply(tk),
                    .. MapEventEnteredTrigger(WorldObjects.TriggersOn(worldMap)[ti].Name, now)];
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - WorldObjects.FirstKey is var i && i >= 0 && i < objects.Count) { index = i; break; }
        // No such object: refuse it, or the client waits for the reply with the player locked. Every refusal is noted (map, key,
        // object, why): a door that did not open (live 2026-11-06, HQ004) left no trace of the reason before.
        var firstKey = EnvObjKeys(plain).FirstOrDefault(k => k is not null) ?? 0;
        if (index is not { } found)
        {
            Note($"InteractEnvObj refused on map {worldMap}: key {firstKey} is no object here ({Convert.ToHexString(plain)})");
            return [GamePackets.InteractEnvObjReply(firstKey, 1)];
        }
        var o = objects[found];
        var key2 = WorldObjects.FirstKey + found;
        // Doors and barriers (opening them is the way on in instances): never refused as used up or for a few metres of distance.
        var passage = o.Name.Contains("Door", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Barrier", StringComparison.OrdinalIgnoreCase)
            || o.Name.Contains("Gate", StringComparison.OrdinalIgnoreCase);
        if (!passage && (Gathered(found, now) || usedObjects.Contains(found)))
        {
            Note($"InteractEnvObj refused on map {worldMap}: {o.Name} (key {key2}) is used up");
            return [GamePackets.InteractEnvObjReply(firstKey, 1)];
        }
        // A reward or orb box this player (or, spent for the copy, anyone) already opened (GameSession.EnvBoxes.cs).
        if (EnvBoxRefusal(found, o, key2, now) is { } openedBox) return [openedBox];
        // A hidden cube already taken or needing a key, a rift closed, of the other faction, out of level or full (GameSession.Rifts.cs).
        if (await PoolOrRiftRefusalAsync(found, o, key2, now, ct) is { } poolRefusal) return [poolRefusal];
        if (o.FlightRing) return FlyThroughRing(o, key2, now);
        var reach = passage ? Math.Max(o.InteractRange, PassageReach) : o.InteractRange + WorldObjects.InteractSlack;
        if (position is { } at && Math.Sqrt(Math.Pow(at.X - o.Position[0], 2) + Math.Pow(at.Y - o.Position[1], 2)) is var gap && gap > reach)
        {
            Note($"InteractEnvObj refused on map {worldMap}: {o.Name} (key {key2}) is {gap:0} cm away (reach {reach:0})");
            return [GamePackets.InteractEnvObjReply(key2, 1)];
        }
        // Bomb boxes (GameSession.Bombs.cs, OBSERVED): the bomb, the box usable again after its cooldown. Towers are bombed, not used.
        if (IsBombBox(o))
        {
            if (coolingBoxes.Contains(found))
            {
                Note($"InteractEnvObj refused on map {worldMap}: {o.Name} (key {key2}) is cooling down");
                return [GamePackets.InteractEnvObjReply(key2, 1)];
            }
            return TakeBomb(found, key2, now);
        }
        if (IsBombTower(o))
        {
            Note($"InteractEnvObj refused on map {worldMap}: {o.Name} (key {key2}) is destroyed with bombs");
            return [GamePackets.InteractEnvObjReply(key2, 1)];
        }
        // An Empyrean Trace (GameSession.EmpyreanTraces.cs): the reply comes with the start, not after the use (OBSERVED 15 pickups:
        // 0x3637 then 0x3638 together, nothing after 0x363b).
        var replies = IsTrace(o) ? new List<byte[]> { GamePackets.InteractEnvObjReply(key2), GamePackets.InteractEnvObjStarted(key2, playerKey) }
            : new List<byte[]> { GamePackets.InteractEnvObjStarted(key2, playerKey) };
        if (o.InteractTime > 0)
        {
            pendingUse = (found, key2, now + o.InteractTime);
            // OBSERVED (GitHub 83bdcc3): a timed object's reply at the start; a reward box keeps it after the use (its OBSERVED order
            // 0x363a, 0x363b, 0x3637, 0xe33a; GameSession.EnvBoxes.cs) and a trace sent it above.
            if (ReplyAtStart(o)) replies.Add(GamePackets.InteractEnvObjReply(key2));
            return replies;
        }
        replies.AddRange(await CompleteUseAsync(found, ct, now));
        return replies;
    }

    /// <summary>
    /// Flying through a flight ring (EnvObjData Usage FlightRing; the client reports it as an interaction): the reply alone, then the
    /// ring's EventFunc abnormals (WorldBuffs: 19000064 FlySpeed/FlyCost for 3 s and 19000065 Fot 100000 FP for 0.1 s; the Abyss
    /// rings 19000010 / 19000063) with the stat update. The flight points come with the Fot tick 100 ms later (GameSession.Buffs.cs).
    /// OBSERVED rec 20261107-012837 #8706-#8720 (WorldBuffs). The ring stays (no 0x3635) and can be flown through again.
    /// Until 2026-11-12 ours sent 18999040/18999041 (an older client's ids) and filled the gauge at once.
    /// </summary>
    List<byte[]> FlyThroughRing(WorldObject ring, int key, long now)
    {
        // OURS: a pass reported far from the ring is refused (else a client could refill its gauge anywhere). The rings are wide
        // (EnvObjData picking radius up to 1260) and flown at up to 2.5x the flight speed, so the reach is generous.
        if (position is { } at && Math.Sqrt(Math.Pow(at.X - ring.Position[0], 2) + Math.Pow(at.Y - ring.Position[1], 2) + Math.Pow(at.Z - ring.Position[2], 2)) is var gap
            && gap > RingReach)
        {
            Note($"InteractEnvObj refused on map {worldMap}: flight ring {ring.Name} (key {key}) is {gap:0} cm away (reach {RingReach:0})");
            return [GamePackets.InteractEnvObjReply(key, 1)];
        }
        return [GamePackets.InteractEnvObjReply(key), .. ApplyWorldBuffs(WorldBuffs.Ring(ring.Env), now)];
    }

    /// <summary>How far (cm, 3D) from a flight ring a pass is accepted (OURS).</summary>
    public const double RingReach = 5000;

    static CollectionStats CollectionTable => CollectionStats.Embedded;

    /// <summary>
    /// Collection stats: the worn wing's equipped effect and every owned wing's owned effect (client item descriptions), the
    /// PROVISIONAL pet collection (each owned pet by its level) and skin collection (per owned skins).
    /// </summary>
    int CollectionStat(string stat)
    {
        var total = 0;
        if (wingId != 0 && CollectionTable.Wing(wingId) is { } worn) total += worn.Equipped.GetValueOrDefault(stat);
        foreach (var (id, _) in OwnedWings()) total += CollectionTable.Wing(id)?.Owned.GetValueOrDefault(stat) ?? 0;
        foreach (var ride in rides.Values.Where(r => r.Owned)) total += CollectionTable.Pet(ride.Level).GetValueOrDefault(stat);
        if (CountsForWornWeapon(stat)) total += SkinCollectionStats().GetValueOrDefault(stat);
        return total;
    }

    /// <summary>The owned skins' collection points, recounted only when the number of owned skins changes (stats are read per hit).</summary>
    (int Count, IReadOnlyDictionary<string, int> Stats)? skinStatsCache;
    IReadOnlyDictionary<string, int> SkinCollectionStats()
    {
        if (skinStatsCache is { } cached && cached.Count == skins.Count) return cached.Stats;
        var owned = SkinPoints().ToDictionary(p => SkinTables.CategoryNames[p.Key], p => p.Value, StringComparer.OrdinalIgnoreCase);
        var stats = CollectionTable.Skins(owned, Skins.MaxPointsByCategory);
        skinStatsCache = (skins.Count, stats);
        return stats;
    }

    /// <summary>The weapon skin categories: only the worn weapon's type counts (USER 2026-11-08: all weapons collected is 166
    /// points once, not once per weapon type).</summary>
    static readonly HashSet<string> WeaponSkinCategories = new(StringComparer.OrdinalIgnoreCase) { "Greatsword", "Sword", "Dagger", "Bow", "Magicbook", "Orb", "Mace", "Staff" };

    /// <summary>False for another weapon type's skin damage bonus (SwordDamageRatio while a staff is worn): it neither counts nor shows.</summary>
    bool CountsForWornWeapon(string stat) =>
        !(stat.EndsWith("DamageRatio", StringComparison.OrdinalIgnoreCase) && WeaponSkinCategories.Contains(stat[..^"DamageRatio".Length])
          && !string.Equals(data.WeaponType(equippedWeapon) + "DamageRatio", stat, StringComparison.OrdinalIgnoreCase));

    /// <summary>A skin collection Ratio stat as a multiplier (0.01% units: 3450 = +34.5%).</summary>
    double SkinRatio(string stat) => 1 + SkinCollectionStats().GetValueOrDefault(stat) / 10_000.0;

    /// <summary>An equipped item's defense with its slot's skin collection bonus (HelmetDefenseRatio for a helmet, ...).</summary>
    int SlotDefense(ItemInfo item) => item.Stat("ArmorDefense") is var d and not 0 ? (int)Math.Round(d * SkinRatio(item.Category + "DefenseRatio")) : 0;

    /// <summary>The worn weapon's damage with its weapon type's skin collection bonus (SwordDamageRatio for a sword, ...).</summary>
    WeaponStats ScaledWeapon()
    {
        var weapon = data.WeaponStatsOf(equippedWeapon);
        if (data.WeaponType(equippedWeapon) is not { } type) return weapon;
        var scale = SkinRatio(type + "DamageRatio");
        return scale == 1 ? weapon : weapon with { Min = (int)Math.Round(weapon.Min * scale), Max = (int)Math.Round(weapon.Max * scale) };
    }

    IEnumerable<string> CollectionStatNames() =>
        (wingId != 0 && CollectionTable.Wing(wingId) is { } worn ? worn.Equipped.Keys : [])
            .Concat(OwnedWings().SelectMany(w => CollectionTable.Wing(w.Id)?.Owned.Keys ?? []))
            .Concat(rides.Values.Where(r => r.Owned).SelectMany(r => CollectionTable.Pet(r.Level).Keys))
            .Concat(SkinCollectionStats().Keys.Where(CountsForWornWeapon));

    /// <summary>A barrier the server rules keep out of instance copies (gamedata/server-rules.xml instances/remove_barriers).</summary>
    static bool RemovedBarrier(WorldObject o) =>
        ServerRules.Embedded.RemoveInstanceBarriers && o.Name.Contains("Barrier", StringComparison.OrdinalIgnoreCase);

    /// <summary>OURS: doors, barriers and gates can be opened from this far (cm), for latency and the door's own size.</summary>
    public const double PassageReach = 1000;

    /// <summary>
    /// The door being used: its seamless door teleport effect (TeleportEffect.dat id of its EnvObjData sequence, e.g. 40
    /// SeamlessDoorTeleport_heavy_in), sent in the move itself - the client plays the push-open animation and walks through.
    /// </summary>
    int doorEffect;

    /// <summary>Out through an exit door: its sequence, then the Teleport point (MapData point of this map or Teleport.dat) - out of the layer.</summary>
    List<byte[]>? ExitThroughDoor(string point, int effect, long now)
    {
        int map = worldMap;
        Vector3d at;
        if (MapEventData.Points(worldMap).TryGetValue(point, out var p)) at = new Vector3d(p[0], p[1], p[2]);
        else if (MapEventData.Teleport(point) is { At: [var x, var y, var z] } target) { map = target.Map; at = new Vector3d(x, y, z); }
        else { Note($"exit door: no point {point} for map {worldMap}"); return null; }
        // A point on the layer's own map leads out of the layer onto its base map (the layer shares its ground).
        if (map == worldMap && MapEventData.Volume(worldMap) is { } layer) map = layer.Base;
        // Out of the layer: its map events are left after the move (0x922f follows 0x3621, as LeaveInstance).
        List<byte[]> leaving = [];
        if (!IsInstanceMap(map))
        {
            if (instanceKey != 0) leaving = EndInstance();
            instanceExitAt = null; instanceReturn = null;
        }
        Note($"exit door: effect {effect} to {point} (map {map})");
        var moved = effect > 0 ? Teleport(map, at, now, 2, (byte)effect) : Teleport(map, at, now);
        return [.. moved, .. leaving];
    }

    /// <summary>Timed objects answered at the start of the use (not gather nodes, traces or reward boxes).</summary>
    static bool ReplyAtStart(WorldObject o) => o.Gather is null && !IsTrace(o) && !HasEnvReward(o);

    async Task<List<byte[]>> CompleteUseAsync(int index, CancellationToken ct, long now = 0, bool timed = false)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (index >= objects.Count) return [];
        var key = WorldObjects.FirstKey + index;
        // Empyrean Traces and Monoliths (GameSession.EmpyreanTraces.cs): their own order; a Monolith is never spent (its EnvObjData
        // says InteractCount 1 + bDespawn, but it stays where it is: CANDIDATE).
        if (IsTrace(objects[index])) return await CollectTraceAsync(index, objects[index], key, now, ct);
        if (objects[index].Monolith is { Length: > 0 }) return MonolithUsed(objects[index], key);
        // Hidden cubes and Spacetime Rifts (GameSession.Rifts.cs): their own end.
        if (objects[index].Pool is not null || objects[index].Rift is not null) return await PoolOrRiftUsedAsync(index, objects[index], key, now, ct);
        // EnvObjData: a single-use object that despawns (InteractCount 1, bDespawn) stays finished - a door stays open - and is removed
        // after its FinishDuration; it is not made available again (live 2026-11-09: the Prison Compound's small door opened and shut
        // at once because the available state followed the finish, and the player could not pass).
        var envData = EnvUsages.Of(objects[index].Env);
        var spent = objects[index].Gather is null && envData is { SpentAfterUse: true };
        // A gather node sends its own completion (collection count, finish only at the last: CompleteGatherAsync).
        var gatherNode = objects[index].Gather is not null;
        var replies = gatherNode ? new List<byte[]>() : new List<byte[]> { GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key) };
        // A box one use spends for the copy (orb box, Drakan corpse, box pile) is not made available again (OBSERVED: no 0x363b).
        if (!gatherNode && !spent && !SpentForCopy(objects[index])) replies.Add(GamePackets.InteractEnvObjAvailableState(key));
        // A timed use was answered at its start (0x3637 with 0x3638, GitHub 83bdcc3, OBSERVED 2026-11 recordings); an instant one now.
        if (!gatherNode && !(timed && ReplyAtStart(objects[index]))) replies.Add(GamePackets.InteractEnvObjReply(key));
        // Reward boxes (power shards, soulstones ...) and recovery orb boxes: GameSession.EnvBoxes.cs.
        replies.AddRange(await EnvBoxUsedAsync(index, objects[index], key, now, ct));
        // A placed Overlap object walked or flown into (Od bubbles, buff orbs, the arena's orbs...): its EventFunc abnormals (WorldBuffs).
        replies.AddRange(ApplyWorldBuffs(WorldBuffs.Overlap(objects[index].Env), now));
        replies.AddRange(FestivalUsedObject(index, objects[index], now));   // a festival point item (GameSession.FestivalGames.cs)
        if (objects[index].Gather is not null) replies.AddRange(await CompleteGatherAsync(index, objects[index], now, ct));
        CarryBoxUsed(index, objects[index], now, replies);   // GameSession.EnvObjects.cs
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(objects[index].Env, level), ct)));
        // Map event UseEnvObj names the EnvObjData template (E_D0_Layer_2_PrisonDoorSmall_001), the map spawn has its own name
        // (D_Starter_Layer_2_EnvObj_DoorSmall_1): the template name is matched exactly first, the spawn name as before.
        var runs = (CopyOwner ?? this).mapEventRuns;
        var usedName = MapEventObjectName(objects[index]);
        var consumed = runs.Any(r => r.WantsObject(usedName));
        DailyUsedObject(objects[index].Env, now);   // a daily dungeon's orbs and coins (DungeonPoint UseEnvObj)
        replies.AddRange(MapEventUsedObject(usedName, now));
        if (consumed || spent)
        {
            usedObjects.Add(index);
            ScheduleRemoval(index, now);
        }
        // A layer's exit door (World_L_A_Layer_01_ExitDoor: no usage target in our data): out of the layer, back where the player
        // came in (live 2026-11-07: the player could not leave Caelid's office).
        // Only when the door has no MoveMap usage of its own (below): both at once sent two moves (0x3621 back where the player came in,
        // then the door's point) and the client hung in the door sequence until it dropped with GF:1910 (live 2026-11-12 zenyc and
        // 2026-11-13 Axel3, World_L_A_Layer_01 -> Idzone_Dawn_Barracks_Tel_Exit_1).
        if (instanceKey != 0 && objects[index].Name.EndsWith("_ExitDoor", StringComparison.OrdinalIgnoreCase)
            && EnvUsages.Of(objects[index].Env) is not { Kind: "MoveMap", Value: { Length: > 0 } }) replies.AddRange(LeaveInstance(now));
        // A layer's exit door (EnvObjData Usage MoveMap: a Teleport point, e.g. the HQ003 storage's inner door -> World_D_Starter_HQ003_Exit):
        // the client's door sequence (SeamlessDoorTeleport_..._out: the push-open animation), then out to the point. Live 2026-11-09 the door
        // did nothing (no usage in our data); the client table (exported 2026-11-09) names the point and the sequence.
        if (EnvUsages.Of(objects[index].Env) is { Kind: "MoveMap", Value: { Length: > 0 } exitPoint } exitDoor && ExitThroughDoor(exitPoint, exitDoor.Effect, now) is { } exited)
            replies.AddRange(exited);
        // A party dungeon's portal (EnvObjData Usage PartyDungeon): into the expedition.
        // Every entry door, dungeon entrance and party dungeon portal of the client table (EnvObjData Usage EnterInstanceLayer / EnterDungeon /
        // PartyDungeon, value = the dungeon), also those our world data has no portal for.
        var portal = objects[index].Portal is { Length: > 0 } listed ? listed
            : EnvUsages.Of(objects[index].Env) is { Kind: "EnterInstanceLayer" or "EnterDungeon" or "PartyDungeon", Value: { } dungeonText }
              && int.TryParse(dungeonText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dungeonId) && dungeonId > 0
                ? new[] { dungeonId } : null;
        if (portal is { Length: > 0 })
        {
            enteringByDoor = true;
            doorEffect = EnvUsages.Of(objects[index].Env)?.Effect ?? 0;
            try { replies.AddRange(EnterPartyDungeon(portal, now)); }
            finally { enteringByDoor = false; doorEffect = 0; }
        }
        // The expedition's reward cube (Usage OdEnergyCube): the final boss's reward, once per player per run.
        // A layer's exit door (Usage MoveMap: UsageValue names the teleport point outside).
        if (objects[index].MoveTo is { Length: > 0 } point) replies.AddRange(MoveMapDoor(point, now));
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
        var replies = new List<byte[]>();   // the engraved list goes with the select burst (SelectEntryLists)
        var placed = Kibelisks.OnMap(mapId);
        for (var i = 0; i < placed.Count; i++)
        {
            var k = placed[i];
            replies.Add(GamePackets.EnvObjSpawn(Kibelisks.FirstKey + i, k.EnvObj, new Vector3d(k.Position[0], k.Position[1], k.Position[2]), k.Yaw, now, k.UniqueKey, k.Relationship));
        }
        return replies;
    }

    /// <summary>
    /// Candidate object keys in an InteractEnvObj_RQ. The live client (2026-10-15) sends a 01 byte, then
    /// the varint key, i64 0 and the EnvObj id; the earlier guesses (key right after the opcode) are kept after it.
    /// </summary>
    static int?[] EnvObjKeys(byte[] plain) =>
        [plain.Length > 3 && plain[2] == 1 ? QuestKey(plain, 3) : null, QuestKey(plain, 2), plain.Length >= 6 && BitConverter.ToInt32(plain, 2) is var raw and >= 0 ? raw : null];

    /// <summary>The raw body of the last InteractEnvObj_RQ (research log).</summary>
    public byte[]? LastEnvObjRequest { get; private set; }

    /// <summary>Kibelisk travel (0x9236): a bound Kibelisk's map and spot, 200 units in front of it so the character does not
    /// stand inside the object (CANDIDATE spot). Not bound or unknown: the EResult failure in the reply, no move.</summary>
    /// <summary>The scroll a Kibelisk trip uses up: OBSERVED (115817, 5 trips) one Return Scroll (Bound) 530560001 per trip, its count
    /// 4, 3, 2, 1, 0 in the 0x561b ~50 ms after 0x9237. Without one the trip stays free (PROVISIONAL: the official answer then
    /// is not recorded).</summary>
    public const int ReturnScrollItemId = 530560001;

    async Task<List<byte[]>> UseTeleportArtifactAsync(int artifactId, long now, CancellationToken ct)
    {
        var moved = UseTeleportArtifact(artifactId, now);
        if (moved.Count < 2 || Store is not { } store || CharacterId is not { } character) return moved;
        if (storedItems.Values.Where(i => i.TemplateId == ReturnScrollItemId && i.Location == "bag").OrderBy(i => i.Quantity).FirstOrDefault() is not { } scroll)
            return moved;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, scroll.InstanceId, scroll.Revision, 1, ct) ? [[]] : []);
        return used.Count == 0 ? moved : [.. moved, .. await InventoryChangesAsync(ct, 0, DecreaseItemUse)];
    }

    List<byte[]> UseTeleportArtifact(int artifactId, long now)
    {
        static byte[] Reply(int result, int id) => PacketWriter.Opcode(0x9237).U16((ushort)result).I32(id).ToArray();
        if (resources is null || reloading) return [Reply(EResult.TeleportArtifact_Fail_NotFound, artifactId)];
        if (Kibelisks.ByArtifact(artifactId) is not { } target) return [Reply(EResult.TeleportArtifact_Fail_NotFound, artifactId)];
        if (!engraved.Contains(artifactId)) return [Reply(EResult.TeleportArtifact_Fail_NotEngraved, artifactId)];
        var (mapId, k) = target;
        var yaw = k.Yaw * Math.PI / 180;
        var at = new Vector3d(k.Position[0] + 200 * Math.Cos(yaw), k.Position[1] + 200 * Math.Sin(yaw), k.Position[2]);
        // OBSERVED (5 official Kibelisk trips 2026-11-06/07): reason 2 and effect 0, the client's own Kibelisk teleport effect.
        // Ours sent 12 (TeleportEffect.dat Teleport_NoEffect): live 2026-11-08 no animation when teleporting.
        return [Reply(0, artifactId), .. Teleport(mapId, at, now, 2, KibeliskEffect)];
    }

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
        // Anti-cheat (bot finding 2026-11-09): a bind from across the map. Beyond the Kibelisk's reach it is logged, beyond the reach
        // plus the object slack it is refused with EnvObj_Fail_TooFar and nothing is engraved (a player at the stone is never refused).
        if (!IsAdmin && ServerPosition is { } standing
            && standing.Distance2d(new Vector3d(kibelisk.Position[0], kibelisk.Position[1], kibelisk.Position[2])) is var gap && gap > Kibelisks.InteractRange)
        {
            var refused = gap > Kibelisks.InteractRange + WorldObjects.InteractSlack;
            Suspect("kibelisk_reach", new { kibelisk = kibelisk.Name, map = worldMap, distance = (int)gap, refused }, now);
            if (refused)
            {
                Note($"Kibelisk bind refused on map {worldMap}: {kibelisk.Name} (key {objectKey}) is {gap:0} cm away");
                return [GamePackets.InteractEnvObjReply(objectKey, EResult.EnvObj_Fail_TooFar)];
            }
        }
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

    /// <summary>A map event's step-talk reply placed after the leading progress messages (0x922d) of what the talk caused.</summary>
    internal static List<byte[]> StepTalkInOrder(byte[] reply, List<byte[]> caused)
    {
        var at = 0;
        while (at < caused.Count && caused[at].Length >= 2 && (caused[at][0] | caused[at][1] << 8) == 0x922d) at++;
        caused.Insert(at, reply);
        return caused;
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
            // OBSERVED order (HQ001 #21.386, the Brigade 01:53:13.925, FQ_D1 #878.779): the event's progress (the talk objective done,
            // and the next step when nothing waits) goes out first, then the 0x8d2c reply, then the objective's directives (NPC walks).
            return StepTalkInOrder(GamePackets.QuestStepTalkReply(2, eventId, talkKey, index), MapEventTalked(talkKey, index, close: false, now));
        }
        if (questLog is null) return [];
        foreach (var offset in new[] { 2, 3 })
        {
            if (plain.Length < offset + 8 || questLog.ByUid(BitConverter.ToInt32(plain, offset)) is not { } record) continue;
            var npcKey = BitConverter.ToInt32(plain, offset + 4);
            var npcId = targets?.GetValueOrDefault(npcKey)?.NpcId ?? 0;
            // {kind 1, uid, npc key, objective index}: the reply echoes the index (OBSERVED replies `01 0000 uid key 00000000`; the
            // requests in our traces carry index 0 too).
            var objective = plain.Length >= offset + 12 && BitConverter.ToInt32(plain, offset + 8) is >= 0 and < 6 and var i ? i : 0;
            var replies = new List<byte[]> { GamePackets.QuestStepTalkReply(1, (int)record.Uid, npcKey, objective) };
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
        // A quest's CloseNote objective: the note opens with the reply (OBSERVED official DQ2202015: 0x8d15 ShowNote Note_D1_DQ2202015_04,
        // then 0x9231). Without it the step completed but the note never showed (34 quest notes).
        var note = questLog?.ByUid(id) is { } before && questLog.CurrentStep(before) is { } step && index >= 0 && index < step.Objectives.Length
            && step.Objectives[index] is { Type: "CloseNote", Target: { Length: > 0 } name } ? name : null;
        if (questLog?.ClientCompleted(id, index, level) is not { } record) return [GamePackets.ClientObjectiveReply(kind, id, index, 1)];
        var replies = new List<byte[]>();
        if (note is not null) replies.Add(GamePackets.Note(note));
        replies.Add(GamePackets.ClientObjectiveReply(kind, id, index));
        replies.AddRange(await Stored(() => QuestChangesAsync([record], ct)));
        return replies;
    }

    async Task<List<byte[]>> QuestAcceptAsync(int questId, int npcKey, CancellationToken ct)
    {
        // Duty quests come from the mission board and the scrolls only (GameSession.Duty.cs).
        if (questLog is null || !questLog.Book.ById.TryGetValue(questId, out var code) || !questLog.CanAccept(code, level) || DutyTables.IsDuty(questLog.Book.Find(code)?.Type))
            return [GamePackets.QuestAcceptReply(questId, 0, 1)];
        // QuestAdd_NT then QuestAccept_RS {result 0, quest, uid} (OBSERVED 2026-11-04 192703 #35324-35325, 2026-11-06 234912 #38133-38134),
        // then the quest's start cutscene (gamedata/quest-cutscenes.xml).
        return await Stored(async () =>
        {
            var added = Wire(await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct));
            return [GamePackets.QuestAdd(added), GamePackets.QuestAcceptReply(questId, added.Uid), .. QuestStartCutscene(code)];
        });
    }

    async Task<List<byte[]>> QuestGiveUpAsync(int uid, CancellationToken ct)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Incomplete } record) return [GamePackets.QuestGiveUpReply(uid, 1)];
        return await Stored(async () =>
        {
            if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, record.Code, ct);
            questLog.Remove(record.Code);
            return [GamePackets.QuestGiveUpReply(uid), GamePackets.QuestRemove([uid], 5), .. await DutyGaveUpAsync(record, ct)];
        });
    }

    /// <summary>
    /// Pays a Complete quest (official order): experience (0x8d07), the items and Kina (0x561b,
    /// reason 24), the reply (0x8d0f), contents it unlocks (0xe258), then its follow-up quest is
    /// added (0x8d11). Development logins store nothing and pay nothing.
    /// </summary>
    async Task<List<byte[]>> QuestRewardAsync(int uid, CancellationToken ct, int select = -1)
    {
        if (questLog?.ByUid(uid) is not { Status: QuestLog.Complete } record || questLog.Book.Find(record.Code) is not { } quest)
            return [GamePackets.QuestTakeRewardReply(uid, 1)];
        // A selectable reward (rewards.select: HQ1102011/HQ2102020 the first pet, DQ1202072/DQ2202072): exactly one chosen item;
        // a missing or invalid choice is refused (the reward window stays open). Live 2026-11-07: the choice was ignored, no pet.
        var choices = (quest.Rewards?.Select ?? []).Where(i => i.Length == 2).ToList();
        if (choices.Count > 0 && (select < 0 || select >= choices.Count)) return [GamePackets.QuestTakeRewardReply(uid, 1)];
        if (DutyRewardRefusal(quest) is var dutyRefused and > 0) return [GamePackets.QuestTakeRewardReply(uid, dutyRefused)];   // GameSession.Duty.cs
        return await Stored(async () =>
        {
            var unlockedBefore = QuestUnlockTypes().ToHashSet();
            var rewards = quest.Rewards ?? new QuestRewards();
            // The host's base rates (Aion:Rates, OURS) scale the quest's experience and Kina (the table values at 1).
            var rates = options.World?.Rates ?? new WorldRates();
            rewards = rewards with { Exp = WorldRates.Scale(rewards.Exp, rates.Experience), Kina = WorldRates.Scale(rewards.Kina, rates.Kinah) };
            var items = (rewards.Items ?? []).Where(i => i.Length == 2).Select(i => ((int)i[0], i[1])).ToList();
            if (choices.Count > 0) items.Add(((int)choices[select][0], choices[select][1]));
            // Table_QuestClassReward: the class's own items (14 Hero quests' class weapons and armour; audit 2026-11-13 G5: never paid).
            // CANDIDATE order: after the quest's items (no class reward turn-in recorded).
            items.AddRange(rewards.ClassItemsFor(className));
            // A duty quest's offered random rewards (QuestReward RandomReward): the items its chances pay (GameSession.Duty.cs).
            var (randomItems, randomPaid) = DutyRandomPaid(record);
            items.AddRange(randomItems);
            if (rewards.Kina > 0) items.Insert(0, (KinaItemId, rewards.Kina));
            // CollectItem "TRUE" objectives give their items back at the turn-in (G6: the 12 mastery quests' Elevate_* pieces stayed in the
            // bag). Missing ones refuse the reward (CANDIDATE result 1: no official turn-in without them recorded).
            var taken = new List<(Guid Item, long Revision, int Count)>();
            foreach (var (takenItem, takenCount) in QuestBook.TakenAtTurnIn(quest))
                if (CostStacks(takenItem, takenCount) is { } stacks) taken.AddRange(stacks);
                else return [GamePackets.QuestTakeRewardReply(uid, 1)];
            // A full bag keeps the quest unpaid (GameSession.BagRoom.cs; the client's Quest_NotEnoughInventory, CANDIDATE).
            if (!BagRoomFor(items, SlotsFreedBy(taken))) return [GamePackets.QuestTakeRewardReply(uid, EResult.Quest_NotEnoughInventory)];
            var replies = new List<byte[]>();
            if (Store is { } store && CharacterId is { } character)
            {
                foreach (var (stack, revision, count) in taken)
                    if (!await store.ConsumeItemAsync(character, stack, revision, count, ct)) return [GamePackets.QuestTakeRewardReply(uid, 1)];
                if (await store.RewardQuestAsync(character, record.Code, rewards.Exp, items, ct) is not long total)
                    return [GamePackets.QuestTakeRewardReply(uid, 1)];
                // The store added the reward to the level's experience; level-ups follow (ExpUpdate, LevelUpdate, ...).
                replies.AddRange(await GainExperienceAsync(total, rewards.Exp, ExpReasonQuest, ct, questRecheck: false));
                replies.AddRange(await InventoryChangesAsync(ct));
                // Pet items are used as soon as they are acquired (Item.dat AutoUseWhenAcquire): the first pet of HQ1102011 unlocks.
                foreach (var (rewardId, _) in items)
                    if (Boxes.Use(rewardId) is { AutoUse: true, Effect: "GetVehicle" or "GetVehicleSoul" })
                        foreach (var key in storedItems.Where(s => s.Value.TemplateId == rewardId && s.Value.Location == "bag").Select(s => s.Key).ToList())
                            replies.AddRange(await UseItemAsync(key, lastNow, ct, reply: false));
            }
            if (AscensionQuests.Contains(record.Code) && (selected?.Ascension ?? 1) < 2
                && (Store is not { } ascending || CharacterId is not { } ascended || await ascending.AscendAsync(ascended, 2, ct)))
                replies.AddRange([.. Ascend(2), .. await AscensionRewardsAsync(1, 2, ct)]);
            // Stamped as the store stamps updated_at: the Ascension Gauge counts quests rewarded after its start by this time.
            questLog.Set(record with { Status = QuestLog.Rewarded, UpdatedAt = DateTimeOffset.UtcNow });
            replies.Add(GamePackets.QuestTakeRewardReply(uid, 0, randomPaid));
            replies.AddRange(await DutyRewardedAsync(record, quest, ct));
            replies.AddRange(AchieveQuest(record.Code, quest.Type, quest.Exploration, quest.Map));
            var unlocked = QuestUnlockTypes().Where(t => !unlockedBefore.Contains(t)).ToArray();
            if (unlocked.Length > 0) replies.Add(GamePackets.ContentsUnlockUpdate(unlocked));
            // The Ascension Gauge's new total (OBSERVED: 0x8d10 {gauge uid, 2, 1, points} after 0x8d0f, before the next 0x8d11;
            // placed after 0xe258 here, CANDIDATE: no recorded reward unlocked a content and moved the gauge together).
            foreach (var gauge in questLog.GaugeRecount(level))
            {
                questLog.TakeMilestones(gauge.Code);
                replies.Add(GamePackets.QuestProgress(Wire(await SaveQuestAsync(gauge, ct))));
            }
            if (quest.Next is { } next && questLog.CanAccept(next, level))
            {
                replies.Add(GamePackets.QuestAdd(Wire(await SaveQuestAsync(QuestLog.New(next, questLog.Book.Find(next)!), ct))));
                replies.AddRange(QuestStartCutscene(next));
            }
            // Quests waiting on this one (ClearQuest, ClearQuestType, ClearQuestExplorationType objectives) move on.
            replies.AddRange(await QuestChangesAsync(questLog.Recheck(level), ct));
            return replies;
        });
    }

    /// <summary>
    /// The quest whose turn-in ascends the character. OBSERVED in the 2026-10-19 recording at HQ1101020's reward (Ashar): the
    /// flight gauge's maximum (0x8d00 type 13, 250,000), WingChange 0x5676 with the race's default wing, the stats, then the
    /// Subspace's event and quests; the gauge then filled from 40,000 in 40,000 steps (a self skill, 5002), WingAcquire 0x5673 for
    /// the default wing right before the stats (#35172). OURS: filled at once; 0xe295 is not decoded. The Dark chain's counterpart is
    /// HQ2101050 (same Ashar turn-in, then "Spread Your Wings" HQ2101060 in the Subspace, as HQ1101021); only the Light one
    /// ascended, so an Asmodian never got wings.
    /// </summary>
    /// <summary>The selected character's ascension grade (1 before the first ascension).</summary>
    public int Ascension => selected?.Ascension ?? 1;

    public static readonly IReadOnlySet<string> AscensionQuests = new HashSet<string> { "HQ1101020", "HQ2101050" };

    List<byte[]> Ascend(int grade)
    {
        if (selected is { } state) selected = state with { Ascension = grade };
        wingId = equippedWing != 0 && registeredWings.Contains(equippedWing) ? equippedWing : GamePackets.DefaultWing(profileRace);
        if (resources is null) return [];
        resources.FpMax = resources.Fp = FlightMax();
        return [GamePackets.CharPoints(playerKey, [(CharPoint.MaxFp, resources.FpMax), (CharPoint.Fp, resources.Fp)]),
                GamePackets.WingChangeNotice(playerKey, wingId, false), GamePackets.WingAcquire(playerKey, wingId), .. RefreshStats(), GamePackets.GlideCooltime(playerKey)];
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
        if (DashVariant is 7 or 8 or 9 or 11)
        {
            // A dash still held from the previous sprint is that cast's only reply: it leaves now instead of being dropped.
            var earlier = TakePendingSprint();
            if (pendingDashBatch is { } held)
            {
                pendingDashBatch = null;
                earlier.AddRange(held);
                worldPlayer?.Map?.Broadcast(worldPlayer, held.Where(SharedCastPacket));
            }
            pendingSprintReply = reply;
            return earlier;
        }
        return reply;
    }

    /// <summary>
    /// Variant 7 and 9 send the dash in the same write as the held sprint reply. Variant 8 keeps both until the tick
    /// (<see cref="FlushHeldDash"/>), which is when the official server answered.
    /// </summary>
    List<byte[]> DeliverDash(List<byte[]> dash, int variant)
    {
        if (variant is 8 or 11)
        {
            if (variant == 11) sprintDashAt = sprintOnAt;
            // A second dash before the tick joins the held batch: each cast request keeps its one reply.
            pendingDashBatch = [.. pendingDashBatch ?? [], .. dash];
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
        if (now < sprintOnAt + (DashVariant is 8 or 11 ? DashBatchDelayMs : SprintReplyHoldMs)) return [];
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
            var effect = GamePackets.SkillEffect(playerKey, playerKey, ClassDodge, fx.Action, ClassDodge * 100 + DodgeEffectOffset);
            worldPlayer?.Map?.Broadcast(worldPlayer, [effect]);
            replies.Add(effect);
            AddBuff(DodgeAfterAbnormal, DodgeAfterMs, ClassDodge, now, replies);
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

    /// <summary>Variant 11: when the last sprint dash went out (its sprint request time), 0 for none.</summary>
    long sprintDashAt;
    /// <summary>Variant 11: the client's sprint-off this soon after a sprint dash is the dash's, not the player's.</summary>
    public const int SprintDashGraceMs = 400;

    /// <summary>
    /// Variant 11 (default since 2026-11-07, the user's idea): the client drops its sprint ~15 ms after every manual-move sprint dash
    /// (0x3705) while the same sprint without a dash (auto-run) works. The early sprint-off gets its reply (0x3742) but the server
    /// keeps the sprint; when the dash ends the auto-run sprint reply goes out again (0x8d03 + 0x3741, others 0x3721), so the
    /// client switches into the sprint that works. PROVISIONAL (official never needed it).
    /// </summary>
    List<byte[]> SprintOff(long now)
    {
        if (DashVariant == 11 && sprinting && sprintDashAt != 0 && now - sprintDashAt <= SprintDashGraceMs)
        {
            var dashAt = sprintDashAt;
            sprintDashAt = 0;
            var replies = TakePendingSprint();
            if (pendingDashBatch is { } held)
            {
                pendingDashBatch = null;
                replies.AddRange(held);
                worldPlayer?.Map?.Broadcast(worldPlayer, replies.Where(SharedCastPacket));
            }
            replies.Add(GamePackets.SprintOffReply(playerKey));
            Schedule(dashAt + DashBatchDelayMs + DodgeAbnormalOffMs + 10, _ =>
            {
                if (!sprinting || resources is null) return [];
                worldPlayer?.Map?.Broadcast(worldPlayer, [PlayerPackets.SprintOn(playerKey)]);
                return [GamePackets.SprintCooltime(playerKey), GamePackets.SprintOnReply(playerKey)];
            });
            return replies;
        }
        return SprintOff();
    }

    List<byte[]> SprintOff()
    {
        sprinting = false;
        sprintDashAt = 0;
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
            if (invincible.Sp || air is AirState.Fly or AirState.Glide) continue;   // OBSERVED: no SP drain in the air (192703 22:40:35-36)
            // On a mount the sprint costs less with the ride's Vehicle Sprint Cost (mount levels: 10-30, PROVISIONAL as percent).
            resources.Sp = Math.Max(0, resources.Sp - (mountedRide is null ? SprintDrain : (int)Math.Round(SprintDrain * Math.Max(0.1, 1 - Bonus("VehicleSprintCost") / 100.0))));
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
    static List<InventoryItem> Layout(IReadOnlyList<ItemState> items, long owner = 0, GameData? data = null, int bagSlots = int.MaxValue)
    {
        var result = new List<InventoryItem>();
        // Free slots are laid out per container: the bag (1), the account warehouse (2, location 'storage') and the character
        // warehouse (15, location 'character_storage').
        var used = new Dictionary<string, HashSet<int>> { ["bag"] = [], ["storage"] = [], ["character_storage"] = [] };
        // Items that use themselves when gained (ItemRules.cs) sit in container 4 kAutoUseItem until used, never on a bag slot (live
        // 2026-11-11: 100 pets of a staff grant waited past the bag's end, unseen and never used).
        static bool AutoUse(ItemState i) => i.Location == "bag" && ItemRules.IsAutoUse(i.TemplateId);
        foreach (var i in items.Where(i => used.ContainsKey(i.Location) && i.Slot is not null && !AutoUse(i))) used[i.Location].Add(i.Slot!.Value);
        var next = new Dictionary<string, int> { ["bag"] = 0, ["storage"] = 0, ["character_storage"] = 0 };
        // Kina (All) and the account currencies (Quna and its states) are never item stacks on the official server (Currencies.cs);
        // a stored row of one (from before migration 087) is not shown.
        foreach (var item in items.Where(i => (i.Location is "equipment" || used.ContainsKey(i.Location)) && !Currencies.NeverAnItem(i.TemplateId))
                     .OrderBy(i => i.Location == "equipment" ? 0 : 1).ThenBy(i => i.Slot ?? int.MaxValue).ThenBy(i => i.WireId))
        {
            var equipped = item.Location == "equipment";
            var autoUse = AutoUse(item);
            int slot;
            if (equipped) slot = item.Slot ?? 1;
            else if (autoUse) slot = 0;   // OBSERVED: Add to container 4, slot 0
            else if (item.Slot is { } fixedSlot) slot = fixedSlot;
            else
            {
                while (used[item.Location].Contains(next[item.Location])) next[item.Location]++;
                // Never past the bag's last slot (live 2026-11-11: godstones at 46-48 of a 46-slot bag showed as empty spaces): an
                // item without room waits unseen until a slot frees (GameSession.BagRoom.cs).
                if (item.Location == "bag" && next[item.Location] >= bagSlots) continue;
                used[item.Location].Add(slot = next[item.Location]);
            }
            var key = item.WireId > 0 ? item.WireId : BitConverter.ToInt64(item.InstanceId.ToByteArray(), 0) & long.MaxValue;
            var equipment = equipped || item.TemplateId < 500_000_000;
            var container = equipped ? InventoryItem.Equipped : autoUse ? AutoUseContainer : ContainerOf(item.Location);
            // A period item's end (ItemRules.Expiry: FixedLimitTime as OBSERVED, MaintainTime CANDIDATE) and the fixed-expiry flag.
            var (expireAt, expireFixed) = ItemRules.Expiry(item.TemplateId, item.Acquired) is { } end ? end : (0L, false);
            result.Add(new InventoryItem(key, item.TemplateId, item.Quantity, container, slot,
                equipment ? new EquipDetail((data?.ItemLevel(item.TemplateId) ?? 1) + Enchants.Effect(item.TemplateId, EnchantOf(item)).ItemLevel, equipped ? (byte)slot : (byte)0,
                    (byte)EnchantOf(item), SoulBindCount(item) > 0 ? owner : 0, SoulBindCount(item),
                    (byte)EnchantOf(item, "surpass"), EnchantOf(item, "surpass_bonus"), WireStones(item),
                    ItemDB.Embedded.Of(item.TemplateId)?.MagicstoneSlots ?? 1, WireStats(UpgradeList(item, "soul_bind_stats")),
                    WireGodstones(item), WireStats(UpgradeList(item, "soul_add_stats")), WireSubSkills(item)) : null,
                ExpireTime: expireAt, ExpireFixed: expireFixed));
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
    const byte DecreaseSoulBind = 63, UpdateEnchant = 1, UpdateSoulBind = 3, UpdateEquip = 7;   // OBSERVED (see InventoryChangesAsync)

    async Task<List<byte[]>> SoulBindCompleteAsync(long key, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(key, out var row))
            return [GamePackets.SoulBindComplete(key)];
        var count = (byte)Math.Min(255, SoulBindCount(row) + 1);
        var bound = await Stored(async () => await store.SoulBindItemAsync(character, row.InstanceId, count, ct) ? [Array.Empty<byte>()] : []);
        if (bound.Count != 1) return [GamePackets.SoulBindComplete(key, 0, 1)];
        // The bound stats (PROVISIONAL pool, gamedata/provisional/stat-pools.xml), as the official reply lists them.
        var stats = await RollSoulBindStatsAsync(store, character, row, ct);
        var skills = await RollSubSkillsAsync(store, character, row, ct);   // sub-stat skills (GameSession.GearEffects.cs)
        return [GamePackets.SoulBindComplete(key, count, stats: stats, skills: skills), .. await InventoryChangesAsync(ct), .. RefreshStats(), .. Achieve("SoulbindTry")];
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
        // Slot extensions load with the character; before that (or without them) the bag has no limit here (GameSession.BagRoom.cs).
        var bagSlots = BagSlots();
        return Layout(rows, selected?.WireId ?? 0, data, bagSlots > 0 ? bagSlots : int.MaxValue);
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
        // Server authority: the client refuses gear above the character's level or of the other race itself, so such a request is
        // not a normal client's; refused and logged. An item without equip data (Item.dat summary) is only logged.
        if (equip && EquipRefused(row.TemplateId, lastNow))
            return [PacketWriter.Opcode((ushort)(opcode + 1)).U16(1).I64(key).U8(slot).ToArray()];
        var moved = await Stored(async () => await store.MoveItemAsync(character, row.InstanceId, row.Revision, equip ? slot : null, ct) ? [Array.Empty<byte>()] : []);
        // kItem_Equip_Success 0x0430 (OBSERVED 2026-11-06 234912 i20777) / kItem_UnEquip_Success 0x043f (OBSERVED 2026-11-05
        // 204043 i1100); refusals kItem_Equip_Fail 0x0431 / kItem_UnEquip_NotExist 0x5008 (usmap; CANDIDATE choice).
        var reply = PacketWriter.Opcode((ushort)(opcode + 1)).U16(moved.Count == 1 ? (equip ? 0x0430 : 0x043f) : (equip ? 0x0431 : 0x5008)).I64(key).U8(slot);
        if (!equip) reply.I32(BitConverter.ToInt32(plain, 11));
        var replies = new List<byte[]> { reply.ToArray() };
        if (moved.Count == 1)
        {
            var before = VisualItems();
            replies.AddRange(await InventoryChangesAsync(ct, 0, 0, update: UpdateEquip));   // update 7 on both items (OBSERVED 2026-11-06 234912 i20766-20777)
            // The new weapon decides weapon skills and damage; gear changes the stats.
            equippedWeapon = WeaponFromInventory();
            replies.AddRange(RefreshStats());
            var level = EquipItemLevel();
            replies.Add(GamePackets.ItemEquipLevelChange(level, level));
            replies.AddRange(AchieveSet("EquipItemLevel", level));   // the item level achievements follow the gear (also at login)
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
                // Self-using items (ItemRules.IsAutoUse) are never in an official login list: the ones left from before (or past the
                // bag's end) are used now, silently, as the client never saw them. Period items that ended go (RemoveExpiredAsync).
                var items = (await LoadInventoryAsync(store, character, ct)).Where(i => i.Container != AutoUseContainer).ToList();
                Remember(items);
                // The weapon in equip slot 1 decides weapon skills (none equipped: no weapon).
                equippedWeapon = WeaponFromInventory();
                var level = EquipItemLevel();
                // Staff grants of collection items made while offline (or left in the bag from before) are applied now as well.
                return [GamePackets.ItemDepotList(items, slotExtends), GamePackets.ItemEquipLevelChange(level, level),
                    .. await ApplyStaffGrantedItemsAsync(storedItems.Keys.ToList(), ct),
                    .. await AutoUseAcquiredAsync(storedItems.Keys.ToList(), ct), .. await RemoveExpiredAsync(lastNow, ct)];
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

    // ---------- Account currencies (Quna) ----------

    /// <summary>The account currencies last shown (null until the first send in the world).</summary>
    Dictionary<int, long>? shownCurrencies;
    /// <summary>The currency groups RefreshCurrency_NT is sent for (the right group id is a CANDIDATE: 0..2 until one is confirmed).</summary>
    static readonly int[] CurrencyGroups = [0, 1, 2];

    /// <summary>
    /// The account's Quna balances to the client (RefreshCurrency_NT) when they changed or were never sent: GameCash (Quna, all of it
    /// tradable on this server), its tradable and bound parts, activating and pending. Shared by every character of the account.
    /// GameCash also carries the Kina-bought Quna of the free-to-play market (GameSession.PremiumShop.cs, mirror_quna).
    /// </summary>
    public async Task<List<byte[]>> CurrencyChangesAsync(CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character || resources is null) return [];
        var stored = await store.AccountCurrenciesAsync(character, ct);
        storedCurrencies = stored;
        return SendCurrencies(CurrencyBalances(stored));
    }

    const byte AutoUseContainer = 4, DecreaseAutoUsed = 38;   // EItemContainerType kAutoUseItem, the decrease OBSERVED at its use
    /// <summary>Items the client was told are in the auto-use container (their removal says so too).</summary>
    readonly HashSet<long> autoUseHeld = [];

    /// <summary>Changes per ItemModificationList_NT (OURS: the longest official list recorded held 3 changes).</summary>
    public const int ModificationsPerMessage = 200;

    /// <summary>Items that use themselves when gained (ItemRules.cs): shown in the auto-use container, never in the bag.</summary>
    static bool IsAutoUseOnAcquire(int itemId) => ItemRules.IsAutoUse(itemId);

    /// <summary>
    /// The stored items against what the client has: adds, updates and removals (EItemModificationType
    /// 0/1/2) with the given EIncreaseReason / EDecreaseReason (24 kQuestReward was the only one before).
    /// Order: the changes in that order (ItemSort lists them by new slot, OBSERVED). Arranged: items only moved between the bag and
    /// the warehouses (sort, store, take out, merge), so nothing counts as gained (no acquisition achievement, no self-use).
    /// </summary>
    async Task<List<byte[]>> InventoryChangesAsync(CancellationToken ct, byte increase, byte decrease, Func<ItemModification, long>? order = null, bool arranged = false, byte update = 0)
    {
        if (Store is not { } store || CharacterId is not { } character) return [];
        var items = await LoadInventoryAsync(store, character, ct);
        var changes = new List<ItemModification>();
        var gained = new List<(int ItemId, long Count)>();
        foreach (var item in items)
        {
            if (!inventory.TryGetValue(item.Key, out var before))
            {
                // An item that uses itself goes into the auto-use container (OBSERVED: Add, container 4 kAutoUseItem, slot 0, reason 5;
                // removed from container 4 with decrease 38 when used): the client then shows it in the loot pop-up. Layout places it.
                if (item.Container == AutoUseContainer) autoUseHeld.Add(item.Key);
                changes.Add(new ItemModification(0, item, Increase: increase));
                gained.Add((item.ItemId, item.Count));
            }
            else if (before != item)
            {
                changes.Add(new ItemModification(1, item, Increase: (byte)(item.Count > before.Count ? increase : 0), Decrease: (byte)(item.Count < before.Count ? decrease : 0),
                    Update: (byte)(item.Count == before.Count ? update : 0)));
                if (item.Count > before.Count) gained.Add((item.ItemId, item.Count - before.Count));
            }
        }
        if (arranged) gained.Clear();
        var acquired = AchieveItemsGained(gained);
        var gainedKeys = arranged ? new List<long>() : changes.Where(c => c.Type == 0 || c.Increase != 0).Select(c => c.Item.Key).ToList();
        var kept = items.Select(i => i.Key).ToHashSet();
        foreach (var gone in inventory.Values.Where(i => !kept.Contains(i.Key)))
            // Official removal (2026-11-06 #56551): type 2, count 0, no equip detail. With the old count and the detail the client
            // kept dismantled equipment on screen (live 2026-11-06: "cannot extract this gear" on the second try).
            // An auto-opened box leaves with its own decrease (OBSERVED 37 kOpenRewardBox, rec 20261107-010735 #10953).
            changes.Add(autoUseHeld.Remove(gone.Key) || gone.Container == AutoUseContainer
                ? new ItemModification(2, gone with { Count = 0, Detail = null, Container = AutoUseContainer, Slot = 0 },
                    Decrease: decrease == DecreaseOpenRewardBox ? DecreaseOpenRewardBox : DecreaseAutoUsed)
                : new ItemModification(2, gone with { Count = 0, Detail = null }, Decrease: decrease));
        Remember(items);
        if (order is not null) changes = [.. changes.OrderBy(order)];   // stable: equal ranks keep their order
        // A long list goes in parts (hundreds of self-using items used at once): ModificationsPerMessage changes per message.
        var result = changes.Count == 0 ? acquired
            : [.. changes.Chunk(ModificationsPerMessage).Select(part => GamePackets.ItemModificationList(part)), .. acquired];
        // Staff-granted collection items (GameSession.StaffGrants.cs: learned and worn), then the items that use themselves when gained
        // (AutoUseWhenAcquire), whatever brought them.
        if (gainedKeys.Count > 0) result.AddRange(await ApplyStaffGrantedItemsAsync(gainedKeys, ct));
        if (gainedKeys.Count > 0) result.AddRange(await AutoUseAcquiredAsync(gainedKeys, ct));
        result.AddRange(QunaMirrorChanges());   // the Quna shown follows the Kina (free-to-play market)
        // Collect objectives follow the bag (GameSession.QuestItems.cs).
        if (HeldItemQuests(storedItems.Values) is { Count: > 0 } holding) result.AddRange(await QuestChangesAsync(holding, ct));
        return result;
    }

    StarterProfile? Profile() => character is null ? data.Profile(1, 1) : data.Profile(character.CreationId, character.Gender);

    /// <summary>A race's starting map: Poeta (1000) for Light, Ishalgen (1100) for Dark (tools/configure_starters.py).</summary>
    static int HomeMapOf(int race) => race == 2 ? 1100 : 1000;
    int HomeMap => HomeMapOf(Profile()?.Race ?? 1);

    async Task<List<byte[]>> GameLoginAsync(byte[] plain, long now, CancellationToken ct)
    {
        loginIdentity = LoginIdentity(plain);
        worldRace = options.WorldRace?.Invoke(loginIdentity.AccountSid);
        // Four worlds: the picked world names the lobby's characters and the race (one race per world, as official).
        pickedServer = options.Worlds is { } worlds && options.WorldServer?.Invoke(loginIdentity.AccountSid) is { } picked && worlds.Of(picked) is { } world
            ? world.Id : null;
        if (pickedServer is { } pickedId) worldRace = options.Worlds!.Of(pickedId)!.Race;
        if (Store is not null)
            return await Stored(async () =>
            {
                var characters = await LobbyCharactersAsync(now, ct);
                var races = characters.Select(c => c.Race).Distinct().ToList();
                accountRole = await StaffRoleAsync(Store.AccountId, ct);
                // _server_race: the picked world's race (each world holds one race, as an official server), else the race
                // of the account's characters (0 when none, CANDIDATE "not chosen"; a new account then saw its own race's
                // text scrambled).
                return [NativeFraming.CompressedEnvelope(GamePackets.LoginCharacters(characters,
                    $"{loginIdentity.AccountSid}:{loginIdentity.Guid}", Store.WorldId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    GamePackets.CommServerKey, now, serverRace: worldRace ?? (races.Count == 1 ? races[0] : 0), isAdmin: accountRole is not null && lobbyStaff))];
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

    async Task<List<LobbyCharacter>> LobbyCharactersAsync(long now, CancellationToken ct)
    {
        var records = new List<LobbyCharacter>();
        // Due deletions complete first (those characters leave the list); pending ones show their time.
        var deletions = await Store!.CharacterDeletionsAsync(ct);
        var renames = await Store!.LastRenamesAsync(ct);
        lobbyStaff = false;
        foreach (var state in await Store!.LobbyAsync(ct))
        {
            if (state.Staff) lobbyStaff = true;
            if (pickedServer is { } server && options.Worlds is { } worlds)
            {
                if (worlds.HomeOf(state.HomeServerId, state.Race) != server) continue;   // a world lists only its own characters
            }
            else if (worldRace is { } race && state.Race != race) continue;   // a world lists only its own race's characters
            records.Add(await LobbyRecordAsync(Store, state, now, deletions, renames, ct));   // GameSession.Lobby.cs
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

    /// <summary>
    /// A creation of the other race than the account's characters: one race per account on this server (an official
    /// server holds one race; this one holds both), so the other race needs every character of this one deleted first.
    /// With four worlds the rule is per world, as official: a world holds one race (the picked world's), and an account may
    /// play Light on a Light world and Dark on a Dark world.
    /// </summary>
    static bool OtherRace(IReadOnlyList<CharacterState> lobby, int race)
    {
        var races = lobby.Select(s => s.Race).Distinct().ToList();
        return races.Count == 1 && races[0] != race;
    }

    /// <summary>The account's characters on the world a creation goes to (all of them without four worlds).</summary>
    IReadOnlyList<CharacterState> OnCreationWorld(IReadOnlyList<CharacterState> lobby, int race) =>
        options.Worlds is { } worlds ? lobby.Where(s => worlds.HomeOf(s.HomeServerId, s.Race) == CreationWorld(race)).ToList() : lobby;

    /// <summary>Four worlds: where a new character of a race goes, the picked world (else world 1 of the race); null without worlds.</summary>
    int? CreationWorld(int race) => options.Worlds is { } worlds ? pickedServer ?? worlds.First(race).Id : null;

    /// <summary>A creation the worlds refuse: the other race than the picked world's, or a world closed to new characters.</summary>
    int? WorldRefusal(int race) =>
        options.Worlds is { } worlds && CreationWorld(race) is { } id && worlds.Of(id) is { } world
            ? world.Race != race ? GamePackets.CreateInvalidServerRace
              : world.CreationClosed ? EResult.LoginServer_SelectServer_RestrictCreation   // CANDIDATE code for the lobby's refusal
              : null
            : null;

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
            if (WorldRefusal(profile.Race) is { } refusedByWorld) return refusedByWorld;
            var lobby = OnCreationWorld(await store.LobbyAsync(ct), profile.Race);
            if (OtherRace(lobby, profile.Race)) return GamePackets.CreateInvalidServerRace;
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
                if (WorldRefusal(profile.Race) is not null) throw new ArgumentException("The picked world refuses this creation");
                if (OtherRace(OnCreationWorld(await store.LobbyAsync(ct), profile.Race), profile.Race)) throw new ArgumentException("The other race on this server");
                var requestId = StoredCharacters.Uuid5(store.AccountId, "native-create-v1:" + Convert.ToHexStringLower(SHA256.HashData(plain)));
                var state = await store.CreateAsync(new CreatePlayer(requestId, $"dev-v1-{request.CreationId}:{request.Gender}", request.Name,
                    request.CustomizingData, VoiceType: request.VoiceType, LobbySlot: request.SlotNo, HomeServerId: CreationWorld(profile.Race)), ct);
                var stored = StoredProfile(state);
                // Database success precedes the success reply; the record lists the worn starter items as the official Asmodian
                // sample (weapon fx 0, item level 0 there; GameSession.Lobby.cs).
                var (worn, _) = await LobbyEquipsAsync(store, state.Summary.CharacterId, weaponFx: 0, ct);
                return [GamePackets.CharacterCreated(state.Summary.Name, state.Gender, stored.PcId, stored.Race, state.WireId, state.WireId,
                    state.Appearance, state.VoiceType, state.LobbySlot ?? 0, now, equips: worn)];
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
                homeServerId = options.Worlds?.HomeOf(state.HomeServerId, state.Race);
                StaffRole = state.Staff ? accountRole : null;
                experience = state.Progress.Experience;
                registeredWings.Clear();
                registeredWings.UnionWith(await store.WingsAsync(state.Summary.CharacterId, ct));
                var extras = await store.ExtrasAsync(state.Summary.CharacterId, ct);
                LoadSkillLevels(await store.SkillsAsync(state.Summary.CharacterId, ct));
                LoadStigmas(await store.StigmasAsync(state.Summary.CharacterId, ct));   // GameSession.Stigmas.cs
                LoadSkillParts(await store.SkillPartsAsync(state.Summary.CharacterId, ct));   // GameSession.Specialization.cs
                LoadDaevanion(await store.DaevanionAsync(state.Summary.CharacterId, ct));
                LoadMonoliths(await store.MonolithsAsync(state.Summary.CharacterId, ct));
                LoadAchievements(await store.AchievementsAsync(state.Summary.CharacterId, ct), await store.GroupRewardsTakenAsync(state.Summary.CharacterId, ct), now);
                LoadCollections(await store.CollectionsAsync(state.Summary.CharacterId, ct));
                LoadBossChallenges(await store.BossChallengesAsync(state.Summary.CharacterId, ct));
                await LoadSeasonRankingAsync(store, state.Summary.CharacterId, now, ct);   // GameSession.Ranking.cs
                LoadTitles(extras);
                LoadSkins(extras);
                LoadMasteries(extras);
                await LoadRidesAsync(store, state.Summary.CharacterId, ct);
                await LoadQuickSlotsAsync(store, state.Summary.CharacterId, ct);
                LoadSettings(await store.CharacterSettingsAsync(state.Summary.CharacterId, ct));
                await LoadBuybackAsync(store, state.Summary.CharacterId, ct);
                await LoadGuildAsync(store, state.Summary.CharacterId, ct);
                await LoadQuestsAsync(store, state.Summary.CharacterId, state.Race, ct);
                await LoadDutyAsync(store, state.Summary.CharacterId, ct);   // GameSession.Duty.cs
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
                return [GamePackets.SelectCharacter((int)key, state.WireId, playerKey, profile.PcId, state.Appearance, optionData: clientOptions),
                    clientEnv.Length > 0 ? [0x44, 0x8d, .. clientEnv] : GamePackets.ClientEnvSetting(), .. CharacterLists(now), .. LoginAchievements(now), .. MarkEntryLoad(), GamePackets.PrepareEnterMap(loadTicket, savedMap, savedAt, GameTime(now))];
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
        return [GamePackets.SelectCharacter((int)request.Value.Key, 1, 1, p?.PcId ?? 1, chosen?.CustomizingData ?? []),
            GamePackets.ClientEnvSetting(), .. SelectEntryLists(now), DevPresetInfo(p), GamePackets.PrepareEnterMap(1, devMap, devAt, GameTime(now))];
    }

    /// <summary>
    /// The character's lists, sent ONCE after SelectCharacter_RS and before the first PrepareEnterMap_NT, as official (OBSERVED in
    /// every recording: 0x3620, then 0x8d53, 0x5683, 0x610b, 0x8d7e, 0xe256, ... within ~50 ms, never again after a teleport).
    /// Until 2026-11-07 they went with every full map load's LoadComplete (5.6 s after the select, and again on each teleport).
    /// </summary>
    /// <summary>
    /// The login's achievement updates (0x5684) in the select burst, as official (OBSERVED in 11 entries: LoginCharacter 401100200 /
    /// 301100200, EquipItemLevel, GatherMasteryLevel, CraftMasteryLevel, with the login's time); we sent them at the load.
    /// </summary>
    List<byte[]> LoginAchievements(long now) =>
        [.. Achieve("LoginCharacter", now: now), .. AchieveSet("EquipItemLevel", EquipItemLevel(), now: now), .. AchieveSet("GatherMasteryLevel", gatherLevel, now: now)];

    /// <summary>PresetInfo_NT of a development select (no stored character): the profile's default bar in preset 1.</summary>
    byte[] DevPresetInfo(StarterProfile? profile)
    {
        if (profile is not null) (classBar, className) = (profile.DefaultBar().ToList(), profile.Class);
        return PresetInfo();
    }

    /// <summary>The next LoadComplete is the first after a select: it sends the entry's load constants (EntryLoadConstants).</summary>
    bool entryLoadPending;
    IEnumerable<byte[]> MarkEntryLoad() { entryLoadPending = true; return []; }

    List<byte[]> CharacterLists(long now)
    {
        // The skill presets of PresetInfo_NT (select burst) are built from the bar: the class's default bar of the chosen character.
        if (Profile() is { } chosen) (classBar, className) = (chosen.DefaultBar().ToList(), chosen.Class);
        return CharacterListsBurst(now);
    }

    List<byte[]> CharacterListsBurst(long now) =>
    [
        // Titles and skins as the official entry lists them (both empty there: #196 0x5679 `0000`, #203 0x8d5b `000000010000`).
        TitleList(), SkinInfo(), AchievementList(now), BattlePassInfoList(now), PeriodCollectionList(now), BossChallengeList(),
        .. EntryConstants(now, DaevanionPointUpdate()), DaevanionBoardInfo(), .. CraftListsAtEntry(), .. SelectEntryLists(now),
    ];

    /// <summary>
    /// More lists of the select burst (OBSERVED phase in 13 official entries, never again on a later load): the wing list 0x5672,
    /// the buyback list 0xe342, 0x8d58/0x8aaf, the potion and item auto-use lists 0x5648/0x564b, the restore status 0xe343, the
    /// content unlocks 0xe257, the quest list 0x8d09, the engraved Kibelisks 0x9232, the open subzones 0x56ac, the exchange info
    /// 0xe33d and the friend list. Until 2026-11-07 they came with every full map load. PresetInfo_NT (0xe28c, also select
    /// officially) still goes at the load: it is built from the skills the load sets up.
    /// </summary>
    List<byte[]> SelectEntryLists(long now)
    {
        // The equipped wing as the load will choose it (staff and ascended characters fly), so the list already has it.
        var race = profileRace = Profile()?.Race ?? profileRace;
        wingId = IsAdmin || (selected?.Ascension ?? 1) >= 2
            ? equippedWing != 0 && registeredWings.Contains(equippedWing) ? equippedWing : GamePackets.DefaultWing(race) : 0;
        var lists = new List<byte[]> { GamePackets.WingList(OwnedWings()) };
        if (Store is not null) lists.Add(RestorableItems());   // the buyback list
        // 0x8d58 MyPrevSeasonRanking_NT: the ended season's results in its reward period (OBSERVED "00" with none), then 0x8aaf.
        foreach (var empty in GamePackets.EmptyEntryLists()) lists.Add(empty[0] == 0x58 && empty[1] == 0x8d ? MyPrevSeasonRanking() : empty);
        lists.Add(PotionAutoUseList());
        lists.Add(ItemAutoUseList());
        lists.Add(ItemRestoreStatus(now));
        var unlocked = GamePackets.UnlockedContents(level).Concat(QuestUnlockTypes());
        // The wing menu (Wing, 12) is quest-gated (HQ1101020 / HQ2101050); a winged character has it open.
        if (wingId != 0) unlocked = unlocked.Append((byte)12);
        lists.Add(GamePackets.ContentsUnlockList(unlocked.Distinct().Order().ToArray()));
        // Official (2026-11-06: 8 entries of which 5 rewarded for a young character, 4,870 bytes for an older one): the list
        // holds the finished (rewarded, status 4) quests too, so the client knows the chain's earlier steps are done (its quest
        // markers on the map follow from them).
        if (DutyEntryNotice() is { } dutyBoard) lists.Add(dutyBoard);   // the mission board before the quest list (OBSERVED #173793, #173810)
        if (questLog?.All.Select(Wire).ToList() is { Count: > 0 } known) lists.Add(GamePackets.QuestList(known));
        lists.Add(MonolithList());   // MonolithList_NT right after the quest list (OBSERVED 8 entries: 0x8d09, 0xe30b, 0xe324)
        lists.Add(GamePackets.EngravedTeleportArtifacts(engraved));
        lists.Add(GamePackets.OpenSubzoneGroups(fog.ToList()));
        lists.AddRange(SocialWorldEntry());
        return lists;
    }

    /// <summary>
    /// The load burst's skill and bar messages in the official order: the skill list 0x5100, the specialized skills 0x3900, the emote
    /// list 0x9501, the bar 0x5600 (the player's saved slots over the class's default bar, which the presets are built from too).
    /// </summary>
    List<byte[]> SkillsAndBarAtEntry(StarterProfile? profile, IReadOnlyList<(int SkillId, int Level)> kit)
    {
        if (profile is null) return [];
        classBar = profile.DefaultBar().ToList();
        // The learned stigmas follow the kit in the same list (CANDIDATE: no recording has one); the specialisation children in
        // effect follow at their parent's level (OBSERVED 0x5100 #643: 11030340, 11040340; GameSession.Specialization.cs).
        List<(int, int)> skills = [.. WithLevels(kit), .. StigmaSkillList(),
            .. SpecializedChildren().Select(c => (c.Child, Math.Clamp(SkillLevel(ChainRoot(c.Parent)), 1, 255)))];
        return [GamePackets.SkillList(skills, DaevanionSkillBonus, EquipSkillBonus), SpecializedSkillLoad(), SocialActionsAtEntry(), Hotbar(classBar)];
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
        var sharedMap = options.World?.Map(mapId, instanceKey, MapLayer(mapId));
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
            if (instanceKey == 0) PrepareFieldBosses(mapId, targets, respawnAt, FieldBossKills, now);
        }
        MapEventsPrepare();   // spawn entries a map event starts itself stay dormant
        PlaceChallengeBoss();
        equippedWeapon = inventory.Count > 0 ? WeaponFromInventory() : data.StarterWeapon(profile);
        // A map load in the world (teleport, revive) keeps HP/MP (it used to be a full heal); the
        // first entry after a selection starts full.
        // Flight needs a worn wing: the stored choice while owned, else staff and ascended characters (grade 2+) get their
        // race's default wing (GameSession.Wings.cs). Set before the stats: the wing's EquipStatList counts.
        profileRace = profile?.Race ?? 1;
        wingId = WingAtLoad();
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
        // The equipped wing is the saved choice (setting "wing" [wing, wing skin]) when it is still owned (live 2026-11-07: it went
        // back to the default wing at every login and map change).
        wingId = IsAdmin || (selected?.Ascension ?? 1) >= 2
            ? equippedWing != 0 && registeredWings.Contains(equippedWing) ? equippedWing : GamePackets.DefaultWing(race) : 0;
        resources.FpMax = resources.Fp = FlightMax();
        if (fresh) RestoreLogoutState(now);   // the buffs' time left and the FP stored at the logout (GameSession.LogoutState.cs)
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
            // The skills and the bar right after it, before the appearance (OBSERVED in all 16 official load bursts: 0x3623 0x3746
            // 0x5100 0x3900 0x9501 0x5600, then 0xe251 0x400a 0x9000 0xe200 0xffab 0x3633); they came after the dungeon state.
            replies.AddRange(SkillsAndBarAtEntry(profile, kit));
            replies.Add(resources.Maxima());
        }
        replies.Add(GamePackets.MyCharAppear(new MyCharacterAppear
            {
                ToggleFlags = sealStoneOn ? 1 : 0, SealstoneItemId = SealStoneItemId,
                Key = playerKey, Name = CharacterNames.Display(name, StaffRole), Gender = gender, MapId = mapId, Instance = instanceKey, Position = at, PcId = profile?.PcId ?? 1, Race = profile?.Race ?? 1,
                // The character's world and its match server (four worlds; else the host's 1204 for both, as before).
                ServerId = ServerId, MatchServerId = options.Worlds?.MatchOf(ServerId) ?? 0,
                // The own appearance's reason byte: 0 at the world entry, 12 after an instance-layer move in and out (OBSERVED
                // #382, #9680, #14311: the same EAppearReasonType as 0x3647/0x3621). Live 2026-10-19: with 0 in the layer the
                // player kept no controls after Robin ran off.
                AppearReason = instanceMove && TestPiece(LayerPieceAppearance) ? InstanceReason : 0,
                TeleportEffect = instanceMove && TestPiece(LayerPieceAppearance) ? InstanceEffect : (byte)0,
                CharLevel = level, CombatLevel = level, LevelMax = level, Experience = experience, WeaponId = equippedWeapon,
                AbyssRankingPoint = SeasonPointOf(SeasonContentsAbyss),
                Relationship = (byte)((profile?.Race ?? 1) == 2 ? 2 : 1),
                // The real pools (PcStatLevel + gear + buffs); the 1500/200 defaults made the bar read e.g. 4000/1500.
                Hp = resources.Hp, HpMax = resources.HpMax, Mp = resources.Mp, MpMax = resources.MpMax,
                Equips = VisualEquips(all: false),
                CustomizingData = character?.CustomizingData ?? [], VoiceType = character?.VoiceType ?? 0,
                Ascension = selected?.Ascension ?? 1, DbId = selected?.WireId ?? 0,
                // Points spent {skill points, stigma shards} and given by items {Wisdom Stones, shard items} (OBSERVED, community
                // recordings 2026-11-04/05: Exp.dat's total + the item points = the spent points of every fully spent character).
                SpentPoints = [SpentSkillPoints(), StigmaShardsSpent, 0], ItemPoints = [EffectiveSkillPointBonus, ItemStigmaShards, AvailableStigma(1)],
                // The stored gather and craft masteries (they were always the starter's level 1 at the login).
                Gather = (gatherLevel, gatherExp, GatherPointsLeft().Skill, GatherPointsLeft().Specialized),
                Crafts = Enumerable.Range(1, 5).Select(CraftMastery).ToList(),
                WingId = wingId, Fp = resources.Fp, FpMax = resources.FpMax,
                EquipItemLevel = armed, HighestTotalItemLevel = armed, CombatPower = CombatPower(),
                BattleMoveSpeed = BattleMoveSpeedOf(className),
                DaevanionPoints = DaevanionAppearLists(),
            }));
        replies.Add(GamePackets.CharBarrier(playerKey));
        sentStats = null;   // a new appearance: the full sheet once, then only changes
        replies.Add(StatsUpdate(StatSheet().Stats, resources.HpMax));
        if (!shortLoad)
        {
            replies.Add(GamePackets.MentoringRoleUpdate());
            if (wingSkin != 0 && registeredWings.Contains(wingSkin)) replies.Add(GamePackets.WingChangeNotice(playerKey, wingSkin, true));
            replies.Add(RideInit());
            replies.Add(GamePackets.GlideCooltime(playerKey));
        }
        // The class passives (weapon equip permission) right after the appearance, as the official world entry has them.
        replies.InsertRange(replies.FindIndex(b => b[0] == 0x46 && b[1] == 0x36) + 1, PassiveAbnormals(resend: true));
        // (GitHub's entry protection 10002 / 5000 ms is the Dell version's arrival abnormal below: sent once, by the arrival.)
        // The worn gear's passives (godstones, sets) after them (OBSERVED in the own 0x3633 and others' 0x3645; GameSession.GearEffects.cs).
        replies.InsertRange(replies.FindIndex(b => b[0] == 0x46 && b[1] == 0x36) + 1, GearAtLoad());
        // The arrival effect of every load (official: the first 0x382a, right after 0x3646).
        replies.Insert(replies.FindIndex(b => b[0] == 0x46 && b[1] == 0x36) + 1, PlayerPackets.AbnormalAdd(playerKey,
            [new AbnormalWire(++buffUid, ArrivalAbnormal, ArrivalAbnormalMs, now + ArrivalAbnormalMs, CasterKey: playerKey, RangeBase: at)]));
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
            replies.AddRange(GuildAtEntry());
            // The character's lists (achievements, collections, tickets, crafts, ...) go once with the select reply (CharacterLists);
            // the first load after it carries a few more constants before and after the appearance (OBSERVED phases).
            if (entryLoadPending)
            {
                entryLoadPending = false;
                var appearAt = replies.FindIndex(b => b[0] == 0x33 && b[1] == 0x36);
                replies.InsertRange(appearAt < 0 ? replies.Count : appearAt, EntryLoadConstants());
                replies.AddRange(EntryAfterAppearConstants(now));
            }
            if (fresh) elapsedAchieveAt = now + 60_000;
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
        // The CP once more after the load burst (the official world entry has it in 0x3633 only; the client then fell back to its
        // own negative number for the gather/door checks, live 2026-11-08 Axel3), out of the observed order 0x364a, 0x3657.
        replies.Add(CombatPowerUpdate());
        replies.AddRange(DungeonStateAtEntry(now));   // OBSERVED order: 0x4001/0x4002, 0x6100, 0x6109, 0x6108, then 0x922e
        if (!shortLoad)
        {
            if (profile is not null)
            {
                // The skill list, specialisation, emotes and bar went with the load burst (SkillsAndBarAtEntry); the stigma shards
                // left follow when any (GameSession.Stigmas.cs).
                if (stigmas.Count > 0 || ItemStigmaShards > 0) replies.Add(StigmaShardUpdate());
                // The skill extras no official load burst carries (CANDIDATE place, after the bar and the dungeon state as before).
                replies.AddRange(AutoLoadAtEntry());   // charge skills' full charges (OBSERVED 0x3822 after the skill is learned)
                // Points from items (Wisdom Stones, stigma shards): the client learns them from 0x3811 (CANDIDATE at entry).
                if (EffectiveSkillPointBonus > 0) replies.Add(PointsBonus());
                // The used points too: without them the client counted none used until the first level bought, then dropped
                // by everything used at once (live 2026-11-07: available 24, one level bought, -16).
                if (SpentSkillPoints() > 0) replies.Add(SkillPointUpdate());
                if (stigmaPoints[0] > 0) replies.Add(SkillPointBonusUpdate(AvailableStigma(0), SkillPointTypeStigma));
                if (stigmaPoints[1] > 0) replies.Add(SkillPointBonusUpdate(AvailableStigma(1), SkillPointTypeStigmaSuperior));
            }
        }
        if (reloading)
        {
            // A teleport's map load sends no unlock list or update: 0xe257 goes with the select only (it no longer re-locks chat),
            // and the official loads carry no 0xe258 unless something opens. Until 2026-11-11 every load re-sent 0xe258 [45, 43].
            reloading = false;
            if (invisible) replies.Add(GamePackets.InvisibleMode(true));   // a GM stays hidden across the reload
        }
        replies.AddRange(NpcsAtEntry(at));
        replies.AddRange(KibeliskSpawns(mapId, now));
        if (!shortLoad)
        {
        }
        fogCheckAt = 0;
        shownObjects.Clear();
        shownTriggers.Clear();
        usedObjects.Clear();
        layerTaken.Clear();
        objectHits.Clear();
        brokenObjects.Clear();
        breakRemovals.Clear();
        pendingUse = null;
        replies.AddRange(ObjectVisibility(at, now));
        replies.AddRange(MapEventsStart(now));   // the layer's events join (0x922e) and run their first directives
        CharacterName = name;
        EnterSharedWorld(mapId, at);
        arrivalCheckDue = true;
        return replies;
    }

    // ---------- Casts and combat (tools/starter_casts.py) ----------

    public sealed record CastRequest(int SkillId, int ClientActionUid, int ChangeToggle, int TargetKey, double Yaw, Vector3d Location, bool HasToggle = false);

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
            return new CastRequest(BitConverter.ToInt32(body, 4), uid, toggle, (int)targetKey, yaw, new Vector3d(x, y, z), (flags & 8) != 0);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    /// <summary>Latency allowance on top of a skill's Skill.dat use range (OURS, cm): a target running away or a position report
    /// still in flight should not refuse a cast the client drew in range.</summary>
    public const double CastRangeTolerance = 150;

    /// <summary>
    /// Is the cast's target within the skill's Skill.dat NeedSkillUseRange (horizontal, from the caster to the target's edge) and
    /// NeedSkillUseHeightRange (vertical)? Positions are the server's own: the caster's last accepted movement report, an NPC's
    /// server position, another player's shared-world position; never the request's location. A skill without a range, a cast
    /// at the caster itself or at an unknown key, and a caster with no position yet are not checked (CANDIDATE reading of the
    /// fields: horizontal reach to the collision edge and a separate height limit; the client's own check is not recovered).
    /// </summary>
    bool InCastRange(SkillDefinition? definition, int targetKey)
    {
        if (definition is not { UseRange: > 0 } || position is not { } caster || targetKey == playerKey) return true;
        Vector3d at;
        double radius;
        if (targets is not null && targets.TryGetValue(targetKey, out var npc)) (at, radius) = (npc.Position, npc.Radius);
        else if (worldPlayer?.Map is { } map && map.Players.TryGetValue(targetKey, out var other)) (at, radius) = (other.Position, NpcAi.PlayerRadius);
        else return true;
        return WithinCastRange(definition, caster, at, radius);
    }

    /// <summary>The geometry of <see cref="InCastRange"/>: horizontal distance to the target's edge within UseRange, height difference
    /// within UseHeightRange (when set), each plus <see cref="CastRangeTolerance"/>.</summary>
    public static bool WithinCastRange(SkillDefinition definition, Vector3d caster, Vector3d target, double targetRadius = 0)
    {
        if (definition.UseRange <= 0) return true;
        if (caster.Distance2d(target) - targetRadius > definition.UseRange + CastRangeTolerance) return false;
        return definition.UseHeightRange <= 0 || Math.Abs(caster.Z - target.Z) <= definition.UseHeightRange + CastRangeTolerance;
    }
    /// <summary>
    /// OURS: a 0x3800 we cannot parse is still answered once, refused (the 0x3801 of <see cref="GamePackets.CastResult"/>) with the
    /// skill at its fixed place (bytes 4-7 after opcode and mask) when the body holds it, else skill 0. Silence left the client's
    /// cast waiting.
    /// </summary>
    static byte[] UnreadableCastRefusal(byte[] body) => GamePackets.CastResult(body.Length >= 8 ? BitConverter.ToInt32(body, 4) : 0, EResult.Skill_DataError);

    /// <summary>
    /// A refused dodge: its refusal, then the dodge's end (0x3806 with the request's action uid) so the swing the client started on
    /// its own ends too. CANDIDATE: no refused dodge is recorded; 0x3806 is the end the official server sends after every dodge.
    /// </summary>
    List<byte[]> RefusedDodge(byte[] refusal, int skill, CastRequest request) =>
        [refusal, GamePackets.SkillEnd(playerKey, skill, request.ClientActionUid)];

    /// <summary>The EResult of a refused cast (Skill_* codes, as the official refusals carry).</summary>
    int CastRefusal(SkillDefinition? definition, bool known, SkillCost cost, List<string> weapons, int cooldownKey, long now) =>
        definition is null ? EResult.Skill_DataError
        : resources is null || dead ? EResult.Skill_NotLivingCharacter
        : !known ? EResult.Skill_NotMySkill
        : weapons.Count > 0 && !weapons.Contains(data.WeaponType(equippedWeapon) ?? "") ? EResult.Skill_NoWeapon
        : !noCool && now < cooldowns.GetValueOrDefault(cooldownKey) ? EResult.Skill_CoolTime
        : resources.Mp < cost.Mp ? EResult.Skill_NoMP
        : resources.Sp < cost.Sp ? EResult.Skill_NoSP
        : resources.Hp <= cost.Hp ? EResult.Skill_NoHP
        : EResult.Skill_Failed;

    // Skill-rate / animation-hack detection: cast attempts per rolling second. Cooldowns are already enforced (a hack cannot land
    // extra damage); this only records the suspicious input flood for staff. A human mashes well under this; macros/hacks exceed it.
    long castWindowMs;
    int castsThisSecond;
    long nextSkillCheatLogMs;
    public const int MaxCastsPerSecond = 25;

    void TrackCastRate(long now)
    {
        if (now - castWindowMs >= 1000) { castWindowMs = now; castsThisSecond = 0; }
        castsThisSecond++;
        if (castsThisSecond == MaxCastsPerSecond + 1 && now >= nextSkillCheatLogMs && Store is not null && CharacterId is not null)
        {
            nextSkillCheatLogMs = now + 30_000;
            var rate = castsThisSecond; var map = worldMap;
            RecordCheatInBackground("skill_rate", new { casts_per_second = rate, limit = MaxCastsPerSecond, map });
        }
    }

    static readonly Lazy<Dictionary<string, string>> QuestCutscenes = new(() =>
    {
        using var stream = typeof(GameSession).Assembly.GetManifestResourceStream("quest-cutscenes.xml")
            ?? throw new InvalidOperationException("Missing embedded game data quest-cutscenes.xml");
        return System.Xml.Linq.XDocument.Load(stream).Root!.Elements("quest")
            .ToDictionary(e => (string)e.Attribute("code")!, e => (string)e.Attribute("cutscene")!, StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The cutscene a quest plays when it is added (gamedata/quest-cutscenes.xml), as official: no wait.</summary>
    internal static List<byte[]> QuestStartCutsceneForChecks(string code) => QuestStartCutscene(code);
    static List<byte[]> QuestStartCutscene(string code) =>
        QuestCutscenes.Value.TryGetValue(code, out var cutscene) ? [GamePackets.Cutscene(cutscene, wait: false)] : [];

    /// <summary>
    /// PrepareEnterMap's _game_time: OBSERVED official values are small and growing (935,170 at one login, 11,428,267 about
    /// three hours into another session): milliseconds of server time, not Unix time (we sent Unix ms, bytes 13-15 never 0).
    /// </summary>
    static long GameTime(long now) => Math.Max(0, now - Combat.StartedAt);

    List<byte[]> CastReplies(byte[] body, long now)
    {
        // Every cast request gets exactly one reply: one we cannot read is refused (GitHub 61dfa06).
        if (ParseCast(body) is not { } request) return [UnreadableCastRefusal(body)];
        busyUntilMs = now + HoverBusyMs;   // hovering while casting is not a rest (flight time)
        TrackCastRate(now);   // anti-cheat: flag implausible cast rates (macros / animation-cancel hacks)
        var skill = request.SkillId;
        // OBSERVED (official 2026-10-19: 36 dodges, 64 sprint starts; live trace 2026-10-20: the client sends 0x3704 then its
        // CombatDodge request): a dodge requested while the sprint is on starts NormalDodge 11000101 (the running dash, SP 10,000)
        // in place of CombatDodge (SP 30,000). Its replies: 0x3802, the 9-byte 0x3801 naming CombatDodge, abnormal 200 for 300 ms,
        // then 0x8d00 with the SP; 200 ms later 0x3806 ends it and 0x382c removes the abnormal.
        var dodging = skill == ClassDodge || skill == ClassNormalDodge;
        var variant = DashVariant;
        // Variants 6-9 change only the dash that follows a sprint. A standing dodge keeps the reply this client already completes.
        if (dodging && !sprinting && variant >= 6) variant = 0;
        // Variant 6: the sprint stands on its own. A 9-byte refusal (result 1, no skill start) so the client does not read the
        // mask-first reply as a failed dash and drop the sprint. The live question is whether the sprint run then holds on land.
        if (dodging && sprinting && variant == 6)
            return [GamePackets.DodgeResult(ClassDodge, 1)];
        // Variant 10: no reply at all to the dodge the client sends with the sprint (6 refused it and still froze, live 2026-10-20).
        if (dodging && sprinting && variant == 10) return [];
        // Variant 9 starts CombatDodge while sprinting (official starts NormalDodge). The rest of the sprint dashes are NormalDodge.
        // OBSERVED (official 2026-10-19 and 2026-10-20): the server picks the dodge by combat state. All 36 CombatDodge starts came
        // within 8 s of the player's own attack; out of combat the dodge is NormalDodge (10,000 SP, no 0x3804 effect) whether the
        // player sprints, runs or stands (2026-10-20: shift while standing gave NormalDodge with no sprint reply at all).
        var started = skill == ClassDodge && variant != 9 && (sprinting || !inCombat) ? ClassNormalDodge : skill;
        // Recovered Skill.dat cost, cooldown and weapon requirement for any learned skill.
        // NormalDodge is CombatDodge's variant: learned with it, SP 10000, the same group cooldown.
        var normalDodge = started == ClassNormalDodge && learned.Contains(ClassDodge);
        var definition = normalDodge ? data.Skill(ClassDodge) is { } dodge ? dodge with { Cost = dodge.Cost with { Sp = NormalDodgeSp } } : null
            : SkillData(skill);
        // Shared cooldown groups (GameSession.CrowdControl.cs); a specialisation child shares its parent's (GameSession.Specialization.cs).
        var cooldownKey = normalDodge ? ClassDodge : CooldownKeyOf(SpecializedCooldownKey(skill));
        // Stunned, asleep, silenced...: no cast but the escape skills (GameSession.CrowdControl.cs, CANDIDATE refusal code).
        if (!dead && CcBlocksCast(started, now)) return [GamePackets.CastResult(skill, EResult.Skill_Abnormal)];
        var cost = definition?.Cost ?? new SkillCost(0, 0, 0);
        if (Bonus("MPUseDecrease") is > 0 and var cheaper) cost = cost with { Mp = cost.Mp * Math.Max(0, 10_000 - Math.Min(cheaper, 9_000)) / 10_000 };
        var weapons = (definition?.Weapons ?? []).Where(w => w != "None").ToList();
        var accepted = definition is not null && resources is not null && !dead
            && resources.Mp >= cost.Mp && resources.Sp >= cost.Sp && resources.Hp > cost.Hp
            && (learned.Contains(skill) || normalDodge || ChainOffered(skill, now) || ChargeReleasing(skill)
                || StigmaReady(skill)    // a learned stigma in an open stigma slot (GameSession.Stigmas.cs)
                || SpecializedReady(skill, now))   // the specialisation child of a learned skill (GameSession.Specialization.cs)
            && (weapons.Count == 0 || weapons.Contains(data.WeaponType(equippedWeapon) ?? ""))
            && (noCool || now >= cooldowns.GetValueOrDefault(cooldownKey))
            && (Behaviour.ChargesOf(skill) is not { Timed: true } charged || ChargesNow(skill, charged).Count > 0);
        if (!accepted)
        {
            // A sprint dash refused for cost or cooldown stays on the 9-byte form. The mask-first reply drops the sprint.
            if (dodging && sprinting && variant is 7 or 8 or 9 or 11)
                return DeliverDash([GamePackets.DodgeResult(skill, 1)], variant);
            return [GamePackets.CastResult(skill, CastRefusal(definition, learned.Contains(skill) || normalDodge || ChainOffered(skill, now), cost, weapons, cooldownKey, now))];  // must not end another action
        }
        // Server authority (GameSession.SkillChecks.cs): range to the aimed target, the self-centred location; refused before any cost.
        if (!dodging && CheckCastAuthority(ref request, now) is { } refused) return refused;
        var dismount = ForceDismount().Concat(InterruptItemCast()).ToList();
        skillMoveGraceUntil = now + SkillMoveGraceMs;
        // Cooldown reduction from every source (CoolTimeDecrease, 0.01% units: titles up to 3.9%), at most half (OURS).
        var cooldown = noCool ? 0 : (int)Math.Round(definition!.CooldownMs * (1 - Math.Clamp(Bonus("CoolTimeDecrease") / 10_000.0, 0, 0.5)));
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
            if (sprinting && variant is 7 or 8 or 9 or 11)
            {
                // The recorded batch (0x3802, the 9-byte 0x3801 naming CombatDodge, abnormal 200, the SP; the end 200 ms later).
                // Variant 8 also waits out the official tick, so the end stays 200 ms after the batch leaves.
                var delay = variant is 8 or 11 ? DashBatchDelayMs : 0;
                var batchEnd = now + delay + DodgeAbnormalOffMs;
                List<byte[]> dash =
                [
                    .. dismount,
                    GamePackets.SkillStart(started, actionUid, request.Yaw, request.Location, playerKey, actorKey: playerKey, cooldown: cooldown),
                    GamePackets.DodgeResult(ClassDodge),
                    DodgeAbnormalAdd(now, started, actionUid, batchEnd),
                    GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]),
                ];
                if (started == ClassDodge) dodgeEffectAt = (actionUid, now + delay + DodgeEffectMs);
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
                GamePackets.DodgeResult(ClassDodge),
            ];
            if (variant is 2 or >= 4) dodgeReplies.Add(DodgeAbnormalAdd(now, started, actionUid, endAt, end: variant != 2));
            else if (variant == 3) dodgeAbnormalOff = (0, started, actionUid, endAt, true);
            dodgeReplies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Sp, resources!.Sp)]));
            if (variant >= 4 && started == ClassDodge) dodgeEffectAt = (actionUid, now + DodgeEffectMs);
            return dodgeReplies;   // no damage rows: nothing for CombatReplies
        }
        // Official order (2026-11-06): the cast still running ends (0x3806), the start (0x3802), then the 9-byte result (0x3801);
        // this cast's own end follows on its timer (skill-timings.json).
        var replies = new List<byte[]>(dismount);   // a cast takes the player off its ride before the skill starts
        replies.AddRange(StartCastTimer(skill, actionUid, now));
        var charge = UseCharge(skill, now);
        replies.Add(GamePackets.SkillStart(skill, actionUid, request.Yaw, request.Location, request.TargetKey, actorKey: playerKey, attackSpeed: AttackSpeed, cooldown: cooldown,
            chargesLeft: charge?.Left, refillInMs: charge?.RefillIn));
        // Chains (OBSERVED order): this skill's own offer closed, its follow-ups offered, then the result.
        replies.AddRange(ChainReplies(skill, now));
        replies.Add(GamePackets.CastResult(skill));
        replies.Add(resources!.Update());
        replies.AddRange(SupportCast(skill, request, now));   // heals, buffs, cleanses on the caster and allies by step (GameSession.SkillSupport.cs)
        replies.AddRange(CooltimeEffects(skill, now));   // DecreaseCooltime / IncreaseCooltime effects: 0x3847 (official)
        if (!ScheduleProjectiles(skill, actionUid, request, now)) replies.AddRange(CombatReplies(skill, request, now));
        replies.AddRange(PlaceTrap(skill, request, now));   // a trap skill places its trap NPC (player-traps.xml)
        replies.AddRange(SummonCast(skill, request, now));   // a summon skill's NPC, a despawn skill's dismissal (GameSession.Summons.cs)
        return replies;
    }

    /// <summary>Area hits always reach a monster this close to the skill's centre (cm beyond its body), walls or not.</summary>
    public const double AreaAlwaysCm = 300;

    /// <summary>
    /// Whether an area skill centred at <paramref name="centre"/> reaches <paramref name="t"/> on the navmesh: always near the
    /// centre (AreaAlwaysCm beyond its body) or with no floor data at either end; otherwise the floor walk from the centre towards
    /// it (NavMesh.MoveAlong) must reach its body (a monster standing half in a rock or on a mesh edge still counts) or the floor
    /// line of sight must be clear.
    /// </summary>
    internal static bool AreaVisible(NavMesh mesh, Vector3d centre, Target t)
    {
        double dx = t.Position.X - centre.X, dy = t.Position.Y - centre.Y;
        var flat = Math.Sqrt(dx * dx + dy * dy);
        if (flat <= t.Radius + AreaAlwaysCm) return true;
        var reached = mesh.MoveAlong(centre, t.Position);
        double rx = t.Position.X - reached.X, ry = t.Position.Y - reached.Y;
        if (Math.Sqrt(rx * rx + ry * ry) <= t.Radius + 100) return true;
        return mesh.LineOfSight(centre, t.Position);
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
            if (!target.CorpseRemoved && now >= (target.DiedAt > 0 ? target.DiedAt + CorpseMs : due - RespawnMs + CorpseMs))
            {
                target.CorpseRemoved = true;
                replies.Add(GamePackets.Death(target.Key));
            }
            if (now < due) continue;
            // A period field boss stands only at its time and point (GameSession.FieldBosses.cs).
            if (FieldBossHeld(target, now) is { } later) { respawnAt[target.Key] = later; continue; }
            respawnAt.Remove(target.Key);
            // In a scripted layer or dungeon the dead stay dead for the run (official dungeon mobs do not respawn).
            if (RunsMapEvents) continue;
            target.DiedAt = 0;
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
    List<byte[]> CombatReplies(int skill, CastRequest request, long now, int? castUid = null)
    {
        var hitUid = castUid ?? actionUid;   // the hits name their cast's action uid (official 0x3804)
        var replies = RespawnTargets(now);
        // A destroyable object aimed at (EnvObj_Destroy): each damaging cast counts (0x8d93), the last one breaks it (GameSession.Destroy.cs).
        if (data.Skill(skill)?.Damage is { Length: > 0 }) replies.AddRange(ObjectHits(skill, request, (byte)hitUid));
        if (request.TargetKey >= SharedWorld.FirstPlayerKey && request.TargetKey != playerKey)
        {
            var pvp = DuelHits(skill, request.TargetKey, now);
            if (pvp.Count == 0) pvp = ArenaHits(skill, request.TargetKey, now);
            if (pvp.Count == 0) pvp = FactionHits(skill, request.TargetKey, now);
            replies.AddRange(pvp);
        }
        var debuffs = Behaviour.Of(skill) is { Disposition: "Negative", Abnormals.Length: > 0 };
        var mpGained = false;
        if (targets is null || targets.Count == 0 || (data.Skill(skill)?.Damage is not { Length: > 0 } && Guide.Of(skill)?.DamageByLevel is not { Count: > 0 } && !debuffs)) return replies;
        IEnumerable<Target> chosen = targets.TryGetValue(request.TargetKey, out var direct) ? [direct]
            : request.TargetKey == playerKey ? targets.Values.Where(t => t.Position.DistanceSquared(request.Location) <= AreaRange * AreaRange)
            : [];
        // Area skills hit what their text says ("up to 4 enemies within 4m", around the caster, the target, or along a dash path:
        // tools/data/skill-guide.json). Live 2026-11-08: a targeted area skill hit only the aimed monster.
        // The client tables' areas first (SkillEffectFilter per hit step: shape, size, max targets; GameSession.ClientSkills.cs),
        // the guide's text only for skills the tables give no enemy area.
        Dictionary<int, HashSet<int>>? stepsOf = null;
        if (ClientArea(skill, request, targets.GetValueOrDefault(request.TargetKey)) is { Targets.Count: > 0 } clientArea) (chosen, stepsOf) = (clientArea.Targets, clientArea.Steps);
        else if (GuideTargets(skill, request, targets.GetValueOrDefault(request.TargetKey)) is { Count: > 0 } area) chosen = area;
        // Geodata: the monsters the server picks for an area skill must not be behind a wall from the skill's centre (the aimed
        // monster for a target-centred skill, else the caster). The aimed monster always stays (the client checks it against the
        // real walls). Lenient on purpose (user 2026-11-08: a gathered pack must all be hit): a monster near the centre always
        // counts, and one only drops out when the floor really ends before its body (AreaVisible).
        if (ai.Nav is { } sightMesh && position is { } casterAt)
        {
            var centre = (stepsOf is not null ? ClientSkills.Of(skill)?.Area is { Shape: "Circle", NeedTarget: true } : Guide.Of(skill)?.Shape == "target") && direct is not null ? direct.Position : casterAt;
            chosen = chosen.Where(t => t.Key == request.TargetKey || AreaVisible(sightMesh, centre, t)).ToList();
        }
        var dealt = 0;   // the cast's damage (lifesteal)
        var shardAmp = ActiveShardAmplify();   // Power Shard on: more damage, one shard per damaging cast (GameSession.PowerShards.cs)
        var shardHit = false;
        // NPCs the client tables make unhittable (NpcData bCanBeAttacked false / NpcTargetAble Unable: the Drakana floor bombs, boss
        // helpers, lasers; live 2026-11-12 an area skill
        // killed them before they went off) are passed by.
        foreach (var target in chosen.Where(t => t.Attackable).ToList())
        {
            if (target.Dead) continue;
            // An immune NPC (a scripted abnormal) takes the hits for nothing; it still turns on its attacker.
            // Damage at the learned skill level from the client tables (SkillEffectLv), only the steps whose area holds this monster;
            // the guide's level scale only for skills the tables lack.
            var inSteps = stepsOf?.GetValueOrDefault(target.Key);
            var fromClient = ClientSkills.Of(skill) is { HasDamage: true };
            var hits = Combat.SkillHits(data, skill, className, level, equippedWeapon, Math.Max(0, EffectiveDefense(target, now) - PvePierce(skill)), rng,
                    weaponStats: PerfectRoll(PveWeapon(skill, target), Bonus("Perfect")), skillLevel: CombatSkillLevel(skill),
                    steps: inSteps is null ? null : st => inSteps.Contains(st.Index) || st.Filter is null or { Shape: "Self" },
                    rows: fromClient ? RowsFor(target, request.TargetKey, chosen.Count(t => t.Hostile)) : null)
                .Select(h => h with { Amount = TakenAfterMods(target, (int)Math.Round(h.Amount * (fromClient ? 1.0 : GuideLevelScale(skill))), skill, now) }).ToList();
            // A skill whose client/class table carries no damage rows but whose guide (skill-guide.json) states a per-level damage
            // - e.g. Bittercold Wind, a summon skill whose class-skills damage is [] while the guide has 1666.. - uses the guide's
            // level damage as the hit, so guide-only skills deal their described damage instead of nothing (USER 2026-11-08).
            // Not for a skill the client tables describe without damage (a summon/anchor/trap skill: its NPC deals the damage, a buff,
            // a passive): the tables are the truth (user 2026-11-10).
            if (hits.Count == 0 && ClientSkills.Of(skill) is null && Guide.Of(skill) is { DamageByLevel.Count: > 0 } g)
            {
                // Scale by the caster's own attack using the guide ratio %, so a weak caster deals proportionally less instead of
                // the flat reference damage that one-shot low-HP monsters (user 2026-11-08); fall back to the per-level number when
                // the guide has no ratio.
                int baseAmt;
                if (g.RatioPercent > 0)
                {
                    var w = ScaledWeapon();
                    var attack = (w.Min + w.Max) / 2 + data.BaseStats(className, level).GetValueOrDefault("FixingDamage");
                    baseAmt = Math.Max(1, attack * g.RatioPercent / 100);
                }
                else baseAmt = g.DamageByLevel[Math.Clamp(CombatSkillLevel(skill), 1, g.DamageByLevel.Count) - 1];
                var amt = Math.Max(1, (int)Math.Round(baseAmt * (double)Combat.DefenseScale / (Combat.DefenseScale + Math.Max(0, EffectiveDefense(target, now)))));
                hits = [new Hit(TakenAfterMods(target, amt, skill, now), false, 0)];
            }
            hits = hits.Select(h => h with { Amount = PveOut(target, skill, h.Amount, h.Critical) }).ToList();   // every PvE damage stat
            hits = SpecialHits(hits, Bonus("HardHit"), HardHitPve, Bonus("AdditionalHitRate"), PveWeapon(skill));   // Double Chance, extra hits
            hits = hits.Select(h => h with { Amount = BossControl.Vulnerable(target, h.Amount, now) }).ToList();   // a staggered boss takes more
            hits = GuideRepeat(skill, hits);   // "deals damage twice", "strikes 4 times" (skill guide)
            if (target.Immune || (target.HitGauge && KillPending(target))) hits = hits.Select(h => h with { Amount = 0 }).ToList();
            if (shardAmp is { } amp && hits.Any(h => h.Amount > 0)) { hits = WithShard(hits, amp); shardHit = true; }
            var seq = 0;
            foreach (var hit in hits)
            {
                target.Hp = Math.Max(HpFloor(target), target.Hp - (target.HitGauge ? (seq == 0 && hit.Amount > 0 ? 1 : 0) : hit.Amount));
                // The angle on the monster (its facing): back attacks show as back attacks (plotter EAttackAngle).
                var hitAngle = position is { } hitFrom && FacingSide(target.Position, target.Direction, hitFrom) > 0 ? GamePackets.AngleBack : GamePackets.AngleFront;
                replies.Add(GamePackets.SkillHit(target.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq, AttackSpeed, hitAngle,
                    actionUid: hitUid));
            }
            if (debuffs) { replies.AddRange(TargetDebuffs(skill, target, now)); NoteDelayedCasts(skill, target); }   // its debuffs and crowd control on the monster
            // The skill guide's stat debuffs (Defense -30%, Attack -10%, Slow...) and damage over time on the monster it hit.
            GuideDebuff(skill, target, now);
            if (hits.Count > 0) GuideDot(skill, target, hits[0].Amount, hits[0].EffectId, now);
            replies.AddRange(GuideCrowdControl(skill, target, now));   // the guide's stun/root/slow where the client rows are missing
            dealt += hits.Sum(h => h.Amount);
            // A skill that costs no MP restores MP when it hits (PROVISIONAL, mp-gain.xml: official +150 Sorcerer, +180 Ranger per
            // cast), once per cast, as a single MP point.
            // The skill text's "Restores N MP" wins over the class rule, also for skills that cost MP (skill guide).
            if (!mpGained && hits.Any(h => h.Amount > 0) && (data.Skill(skill)?.Cost.Mp == 0 || Guide.Of(skill)?.MpRestore > 0)
                && resources is { } own && own.Mp < own.MpMax)
            {
                mpGained = true;
                own.Mp = Math.Min(own.MpMax, own.Mp + GuideMpRestore(skill));
                replies.Add(GamePackets.CharPoints(playerKey, [(CharPoint.Mp, own.Mp)]));
            }
            // The worn godstones' on-hit skills (GameSession.GearEffects.cs), their damage in this hit's HP and kill.
            var (procs, procDealt) = GearProcs(target, hits, request.Yaw, now);
            replies.AddRange(procs);
            replies.AddRange(AfterNpcDamage(target, skill, hits.Sum(h => h.Amount) + procDealt, now));
        }
        replies.AddRange(GuideLifesteal(skill, dealt));   // "absorbs 10% HP" (skill guide)
        if (shardHit) UseShard();
        return replies;
    }

    /// <summary>After a player's damage to an NPC: its HP, map event HP triggers, and the kill (loot, credit) or its chase.</summary>
    List<byte[]> AfterNpcDamage(Target target, int skill, int amount, long now)
    {
        var replies = new List<byte[]> { GamePackets.NpcHp(target.Key, target.Hp) };
        replies.AddRange(GroggyHit(target, skill, now));   // the skill's GroggyGuardDecreaseEnergy on a guard boss (GameSession.BossClient.cs)
        replies.AddRange(MapEventHp(target, now));
        if (target.Dead)
        {
            respawnAt[target.Key] = now + RespawnMs;  // HP 0 is the kill
            FieldBossKilled(target, now);             // a field boss waits for its schedule (GameSession.FieldBosses.cs)
            killedNpcs.Add(target.NpcId);
            replies.AddRange(AchieveKill(target.NpcId));
            replies.AddRange(BossChallengeKilled(target, now));
            InstanceKill(target, now);
            replies.AddRange(MapEventKilled(target, now));
            killedLevels.Add((target.Level, target.Grade, instanceKey != 0));
            replies.AddRange(DropLoot(target, now));
            // Official order after HP 0: the mob's target cleared, the kill, its 0x8d21 off.
            replies.Add(GamePackets.TargetUpdate(target.Key));
            replies.Add(GamePackets.Kill(target.Key, skill, playerKey, ServerId, StoredName));
            replies.AddRange(ai.Killed(target));
        }
        else replies.AddRange(ai.Engage(targets!, target, now, playerKey, amount));    // a hit monster chases its attacker
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

    /// <summary>
    /// NPCs hit or killed by other NPCs (SetAggroNpc fights): their HP goes to the map events first (Munihelle's Elvida takes most
    /// of her damage from Maard; her HpRemaining steps counted only the player's hits), then the kill sequence without loot or
    /// experience, which the map events count.
    /// </summary>
    List<byte[]> NpcKillsByNpcs(NpcAi fights, long now)
    {
        var replies = new List<byte[]>();
        foreach (var key in fights.TakeNpcHurt())
            if (targets?.GetValueOrDefault(key) is { } hurt) replies.AddRange(MapEventHp(hurt, now, byPlayer: false));
        foreach (var (victim, killer) in fights.TakeNpcKills())
        {
            respawnAt[victim.Key] = now + RespawnMs;
            FieldBossKilled(victim, now);
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
    internal List<byte[]> NpcHitsPlayer(int damage, long now, int killerKey = 0, int skill = 0)
    {
        if (resources is null || dead) return [];
        var replies = new List<byte[]>();
        // The monster skill's abnormals on the player (skill-behaviour.json, the client table: Dungeon_D_0018's 1603840 stuns for
        // 2 s with 12000001). The client plays the stun and blocks input from its own abnormal table. PROVISIONAL: stats none.
        if (skill != 0 && Behaviour.Of(skill) is { Disposition: "Negative", Abnormals.Length: > 0 })
        {
            var speedBefore = MoveSpeedBonus();
            var added = new List<byte[]>();
            // Status Effect Resist (AbnormalResistance, 0.01% units: wings, gear): each monster status effect may be shrugged off.
            var resist = Math.Clamp(Bonus("AbnormalResistance"), 0, 9_000);
            // The monster is the debuff's caster (official 0x382a, rec 115817 #139529) and the source of its damage over time.
            var caster = killerKey != 0 ? (worldPlayer?.Map?.Targets ?? targets)?.GetValueOrDefault(killerKey) : null;
            var knocked = new List<int>();
            foreach (var (cc, duration, abnormal, _) in RollAbnormals(skill))
                if ((resist == 0 || rng.Next(10_000) >= resist) && !ImmuneToAbnormal(abnormal, cc, added))   // the player's immunities (GameSession.SkillSupport.cs)
                {
                    var before = added.Count;
                    AddBuff(abnormal, duration, skill, now, added, caster: caster);
                    if (added.Count > before) knocked.Add(abnormal);
                }
            if (added.Count > 0) { replies.AddRange(added); replies.AddRange(BuffStatsChanged(speedBefore)); }
            knockdowns = caster is null ? [] : knocked.Select(a => (a, caster.Position)).ToList();
        }
        else knockdowns = [];
        if (!inCombat)
        {
            inCombat = true;
            replies.AddRange(ForceDismount());   // hit while riding: off the ride (OBSERVED 0x9006 reason 2 after a 0x3805 hit, rec 115817 #1098129)
            replies.Add(GamePackets.CombatState(playerKey, true));
        }
        combatUntil = now + CombatTimeoutMs;
        damage = DefendHit(damage, killerKey, now, replies);   // shields, immunities, reflect, transfer, conversion (GameSession.SkillSupport.cs)
        if (!invincible.Hp) resources.Hp = Math.Max(0, resources.Hp - damage);
        // Restoration (Regeneration Chance): a chance to recover RestorationHealPercent of max HP the moment a hit lands.
        if (damage > 0 && resources.Hp > 0 && rng.Next(10_000) < Bonus("Restoration"))
            resources.Hp = Math.Min(resources.HpMax, resources.Hp + HealReceived(Math.Max(1, resources.HpMax * RestorationHealPercent / 100)));
        replies.Add(GamePackets.NpcHp(playerKey, resources.Hp));
        // A hit ends a timed object use: 0x363b, then 0x3639 reason 0x1905 (OBSERVED 234912 #10314-#10315, 185000 #5953-#5957).
        if (pendingUse is { } use && WorldObjects.ObjectsOn(worldMap) is var hitObjects && use.Index < hitObjects.Count && hitObjects[use.Index].Gather is null)
        {
            pendingUse = null;
            replies.Add(GamePackets.InteractEnvObjAvailableState(use.Key));
            replies.Add(GamePackets.InteractEnvObjInterrupted(use.Key, GamePackets.InterruptDamaged, playerKey));
        }
        if (resources.Hp == 0) replies.AddRange(Die(killerKey, now, skill));
        else foreach (var (abnormal, from) in knockdowns) replies.AddRange(PlayerKnockdown(abnormal, killerKey, from));
        return replies;
    }

    /// <summary>The knockdowns of the hit being applied (abnormal, the caster's position): their forced move follows the HP update.</summary>
    List<(int Abnormal, Vector3d From)> knockdowns = [];

    /// <summary>
    /// A monster's knockdown abnormal on the player (SkillAbnormalEffect KnockdownInstant [cm]): the player is thrown that far away from
    /// the caster at once, 0x3816 Abnormal_Move_Instant_NT {player, caster, the effect id, goal, type 4, terrain 1, 1}, a sprint ended
    /// first (0x3722 + 0x3742). OFFICIAL: the floor trap 2290014's 16022101 (300 cm), rec 013918 #16788-#16797
    /// "1638 a13f a49702 53c78c09 [goal] 04 01 01" right after the HP update; the goal on the floor (navmesh) CANDIDATE.
    /// </summary>
    List<byte[]> PlayerKnockdown(int abnormalId, int casterKey, Vector3d from)
    {
        if (position is not { } at || AbnormalTable.Embedded.Of(abnormalId) is not { } row) return [];
        foreach (var e in row.Effects)
        {
            if (e.Type != "KnockdownInstant") continue;
            var cm = e.Int(0);
            double dx = at.X - from.X, dy = at.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            var (ux, uy) = length < 1 ? (1.0, 0.0) : (dx / length, dy / length);
            var goal = new Vector3d(at.X + ux * cm, at.Y + uy * cm, at.Z);
            if (ai.Nav?.FloorAt(goal.X, goal.Y, goal.Z, 500) is { } z) goal = goal with { Z = z };
            var replies = new List<byte[]>();
            if (sprinting)
            {
                sprinting = false;
                replies.Add(GamePackets.SprintOffNotice(playerKey));
                replies.Add(GamePackets.SprintOffReply(playerKey));
            }
            replies.Add(new Aion.Protocol.Native.Generated.AbnormalMoveInstantNT { Key = playerKey, CasterKey = casterKey, EffectId = e.Id,
                GoalPos = Aion.Protocol.Native.Generated.CommonVec3.From(goal), MoveType = 4, StartTerrainType = 1, EndTerrainType = 1 }.ToArray());
            return replies;
        }
        return [];
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
    List<byte[]> Die(int killerKey, long now, int killerSkill = 0)
    {
        dead = true;
        diving = false;
        if (copy is { } dungeonCopy) dungeonCopy.Deaths++;
        reviveAt = now + ReviveDelayMs;
        diedAt = position;
        inCombat = false;
        // Reason 00 when nothing killed the player (suffocation), as the 68 recorded deaths without a killer (CANDIDATE for a player).
        // Killed by an NPC: its skill, server id 0 and its NpcData id (OBSERVED 2026-11-06 090856, four deaths); by a player
        // (Abyss) our server id as before (CANDIDATE).
        var npcKiller = killerKey != 0 ? targets?.GetValueOrDefault(killerKey) : null;
        var kill = npcKiller is not null
            ? GamePackets.Kill(playerKey, killerSkill, killerKey, 0, "", dieType: 2, reason: GamePackets.DieByKiller, killerNpcId: npcKiller.NpcId)
            : GamePackets.Kill(playerKey, 0, killerKey, killerKey == 0 ? 0 : ServerId, "", dieType: 2,
                reason: killerKey == 0 ? GamePackets.DieNoKiller : GamePackets.DieByKiller);
        worldPlayer?.Map?.Broadcast(worldPlayer, [kill]);
        return [.. ForceDismount(), .. ClearBuffs(), kill, GamePackets.CombatState(playerKey, false), .. DismissSummons()];
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
            if (RevivesLeft() == 0) return [RidePackets.RespawnReply(1)];   // the run's revives are used up: only out of the dungeon
            if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(itemUid, out var row) || row.Location != "bag" || !IsRebirthItem(row.TemplateId))
                return [RidePackets.RespawnReply(1)];
            var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return [RidePackets.RespawnReply(1)];
            // OBSERVED order (090856 #96729-#96741, three times): 0x8d23, abnormal 10003, 0x8d26 (self and nearby), 0x8d00, then the
            // item change. Not sent: the system skill 8047 (0x3802/0x3806/0x3804) between the bars and the item.
            var revived = Revive(now, inPlace: true);
            var reborn = RidePackets.Reborn(playerKey);
            if (diedAt is not null) worldPlayer?.Map?.Broadcast(worldPlayer, [reborn]);
            return [RidePackets.RespawnReply(0), .. revived.Take(1), reborn, .. revived.Skip(1), .. await InventoryChangesAsync(ct, 0, DecreaseUseRebirthItem)];
        }
        return [RidePackets.RespawnReply(0), .. Revive(now)];
    }

    /// <summary>0x8d24 UseRebirth_RQ {bool}: with a rebirth buff, true revives in place (0x8d26 to those nearby).</summary>
    List<byte[]> UseRebirth(bool use, long now)
    {
        if (!use) return [RidePackets.UseRebirthReply(0, false)];
        if (!dead || !CanRebirth || RevivesLeft() == 0) return [RidePackets.UseRebirthReply(1, false)];
        var reborn = RidePackets.Reborn(playerKey);
        worldPlayer?.Map?.Broadcast(worldPlayer, [reborn]);
        return [RidePackets.UseRebirthReply(0, true), .. UseRebirthBuff(), reborn, .. Revive(now, inPlace: true)];   // CANDIDATE: as the item (0x8d25 path unrecorded)
    }

    const byte DecreaseUseRebirthItem = 85;   // EDecreaseReason kUseRebirthItem

    string ReviveKey => CharacterId?.ToString() ?? playerKey.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The revives this character has left in the current dungeon run (Dungeon.dat RebirthLimitCount: 3 for the party _Hard
    /// dungeons, 2 for five raids), or null without a limit. CANDIDATE: every revive counts (respawn, item, rebirth buff,
    /// the automatic one), per character and run (the client's texts: "Exceeded the number of manual and skill
    /// resurrections allowed"; its HUD also has a party variant, not modelled). Staff revives do not count (OURS).
    /// </summary>
    public int? RevivesLeft() =>
        copy is { } c && MapEventData.DungeonOfMap(worldMap) is { RebirthLimit: > 0 } room
            ? Math.Max(0, room.RebirthLimit - c.Revives.GetValueOrDefault(ReviveKey))
            : null;

    /// <summary>
    /// Revive: full HP/MP, where the player fell without a map load (in place: abnormal 10003, then every bar in one 0x8d00) or
    /// with a map load at the map's respawn point (else its start, else here). In a dungeon whose revives are used up the player
    /// revives outside instead (the dungeon's way out, as leaving). OBSERVED 2026-11-06 090856 (reports/combat.md 7): 10003 for
    /// 3000 ms both ways (#96732, #104115-#104165). At the respawn point (#104115-#104156): the own 0x3647 reason 4 effect 0x4f,
    /// 0x3621 with reason 4 and effect 0x4f, 10003, the HP update; the load then has abnormal 10002 as every load (the
    /// community fork recorded the same order). CANDIDATE: what 10003 does (not in our tables: icon only).
    /// </summary>
    List<byte[]> Revive(long now, bool inPlace = false, bool counted = true)
    {
        dead = false;
        if (resources is null) return [];
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        if (counted && RevivesLeft() is { } left && copy is { } run)
        {
            if (left == 0 && instanceKey != 0) return [resources.Update(), .. LeaveInstance(now)];
            run.Revives[ReviveKey] = run.Revives.GetValueOrDefault(ReviveKey) + 1;
        }
        var revival = new List<byte[]>();
        if (inPlace && diedAt is { } fell)
        {
            position = fell;
            AddBuff(ReviveAbnormal, ReviveAbnormalMs, 0, now, revival);
            return [.. revival.Where(b => b[0] == 0x2a && b[1] == 0x38).Take(1), resources.FullUpdate()];
        }
        // Inside a dungeon: the running stage's revival point, else the dungeon's start (the reload keeps the events).
        var raceWord = profileRace == 2 ? "Dark" : "Light";
        var respawn = DungeonRevivalPoint() ?? data.MapPoints(worldMap).Where(p => p.Key.Contains("respawn", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Contains(raceWord, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault()
            ?? data.MapStart(worldMap)?.Position ?? position ?? spawn?.Position ?? GamePackets.SpawnPosition;
        var load = Teleport(worldMap, respawn, now, ReviveReason, ReviveEffect);
        AddBuff(ReviveAbnormal, ReviveAbnormalMs, 0, now, revival);
        return [PlayerPackets.CharDisappear(playerKey, ReviveReason, ReviveEffect), .. load, .. revival.Where(b => b[0] == 0x2a && b[1] == 0x38).Take(1), resources.Update()];
    }

    /// <summary>The revive's abnormal (OBSERVED 10003 for 3000 ms, caster the player, at the place of the revive).</summary>
    const int ReviveAbnormal = 10003, ReviveAbnormalMs = 3000;
    /// <summary>The respawn-point revive's EAppearReasonType and TeleportEffect id (OBSERVED 090856 #104116, #104118: 4 and 0x4f).</summary>
    const byte ReviveReason = 4, ReviveEffect = 0x4f;

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
    public int Defense => data.BaseStats(className, level).GetValueOrDefault("Defense") + EquippedItems().Sum(SlotDefense) + EnchantStat("ArmorDefense") + BuffStat("Defense")
        + BoardStat("Defense") + TitleStat("Defense") + ExtraStat("Defense");   // + soul binds, manastones and collections (owned wings)

    /// <summary>
    /// Chances to dodge and block a monster hit (OURS, user rule 2026-11-08: wings' and gear's evasion/block count against monsters):
    /// the evasion and block above the level's base (monsters are as accurate as a bare character) on the PvP scale.
    /// </summary>
    internal (double Dodge, double Block, double Taken, int ExtraDefense) PveAvoidance(Target attacker)
    {
        // The monster in front of or behind the player (the player's facing): Front/BackAttackDefense, Decrease Front/BackAttack.
        var side = position is { } me && worldPlayer is { } wp ? FacingSide(me, wp.Heading, attacker.Position) : 0;
        var sideDefense = side > 0 ? Bonus("BackAttackDefense") : side < 0 ? Bonus("FrontAttackDefense") : 0;
        var sideDecrease = side > 0 ? Bonus("DecreaseBackAttack") : side < 0 ? Bonus("DecreaseFrontAttack") : 0;
        var basis = data.BaseStats(className, level);
        double Above(params string[] names) => names.Sum(n => CombatStat(n) - basis.GetValueOrDefault(n));
        var boss = attacker.Rank == NpcRank.Hero;
        // Damage reductions (0.01% units): DecreaseDamage, PvEDecreaseDamage, and against bosses BossNpcDecreaseDamage; boss defense
        // (BossNpcDefense) adds to the defense against Hero monsters (wings' owned and equipped effects, titles, gear).
        // Monster hits count as physical skill hits.
        var decrease = Above("DecreaseDamage", "PvEDecreaseDamage", "DecreasePhysicDamage", "DecreaseSkillDamage", "DecreasePhysicSkillDamage", "DecreaseWeaponDamage")
            + (boss ? Above("BossNpcDecreaseDamage") : 0) + sideDecrease + Above("IronWall");   // Iron Wall: monsters have no Ignore
        var extraDefense = (int)Above("PhysicDamageDefense", "PvEDamageDefense", "SkillDefense", "PhysicSkillDefense") + (boss ? (int)Above("BossNpcDefense") : 0) + sideDefense;
        return (Math.Clamp(Above("Evasion", "PhysicEvasion", "PvEEvasion", "PvEPhysicEvasion", "SkillEvasion", "PhysicSkillEvasion", "ArmorEvasion") / EvaPerPoint, 0, DodgeCap),
                Math.Clamp(Above("Block", "WeaponBlock", "ShieldBlock") / BlockPerPoint, 0, BlockCap),
                Math.Max(0.1, 1 - decrease / PerMille), extraDefense);
    }

    /// <summary>
    /// A hit on a monster with the PvE damage stats (0.01% units for Amplify, flat for Add): AmplifyAllDamage, PvEAmplifyDamage and
    /// PvEAddDamage, and against bosses (Hero) BossNpcAmplifyDamage and BossNpcAddDamage (wings, titles, gear; user 2026-11-08).
    /// </summary>
    int PveOut(Target target, int skill, int amount, bool critical = false)
    {
        if (amount <= 0) return amount;
        var boss = target.Rank == NpcRank.Hero;
        var pm = DamageLine(skill);
        // Where the hit lands on the monster: behind it (over 120 degrees from its facing) or in front (under 60).
        var side = position is { } from ? Math.Abs(GamePackets.PyMod(Math.Atan2(from.Y - target.Position.Y, from.X - target.Position.X) * 180 / Math.PI - target.Direction + 180, 360) - 180) : 90;
        var back = side > 120;
        var front = side < 60;
        // DamageRatio (STR and Destruction, PcStatSecond; CANDIDATE: a damage percentage in 0.01% units like the others).
        var amplify = Bonus("DamageRatio") + Bonus("AmplifyAllDamage") + Bonus("PvEAmplifyDamage") + Bonus("Amplify" + pm + "Damage") + Bonus("AmplifySkillDamage") + Bonus("AmplifyWeaponDamage")
            + Bonus("Amplify" + pm + "SkillDamage") + (boss ? Bonus("BossNpcAmplifyDamage") : 0)
            + (back ? Bonus("AmplifyBackAttack") : 0) + (front ? Bonus("AmplifyFrontAttack") : 0);
        var add = Bonus("PvEAddDamage") + Bonus(pm + "SkillDamage") + Bonus("SkillDamage") + (boss ? Bonus("BossNpcAddDamage") : 0)
            + (back ? Bonus("BackAttackDamage") : 0) + (front ? Bonus("FrontAttackDamage") : 0);
        double raw = amount * Math.Max(0.1, 1 + amplify / PerMille) + add;
        // A critical hit: Critical damage amplify (AmplifyCriticalDamage, Amplify Physic/Magic CriticalDamage) and its flat extra.
        if (critical)
            raw = raw * Math.Max(0.1, 1 + (Bonus("AmplifyCriticalDamage") + Bonus("Amplify" + pm + "CriticalDamage")) / PerMille)
                + Bonus("CriticalAddDamage") + Bonus("Critical" + pm + "AddDamage");
        return Math.Max(1, (int)Math.Round(raw));
    }

    /// <summary>
    /// The player's attack speed on the wire (0x3802 / 0x3804 attack speed; 10,000 = 100%): the base plus CombatSpeed from every
    /// source - the Courage Scroll's StatChange CombatSpeed +1,000 (+10%; Superior +1,500), titles, gear, wings (USER 2026-11-08:
    /// combat speed works like the courage scroll). OBSERVED: an official buffed attack carried 11,000. Clamped 50%..200% (OURS).
    /// </summary>
    int AttackSpeed => Math.Clamp(10_000 + Bonus("CombatSpeed"), 5_000, 20_000);

    /// <summary>
    /// How much a block takes off a hit: the weapon's Decrease Weapon Block (9-33% on weapons) plus a shield's Decrease Shield Block
    /// (25-40%), at most 90%; BlockReduction (50%, OURS) when the gear has neither.
    /// </summary>
    /// <summary>A monster's hit on this player: from behind (the player's facing) or the front; a shield block when a shield blocks.</summary>
    internal (byte Angle, bool Shield) HitSide(Target attacker) =>
        (position is { } me && worldPlayer is { } wp && FacingSide(me, wp.Heading, attacker.Position) > 0 ? GamePackets.AngleBack : GamePackets.AngleFront,
         Bonus("DecreaseShieldBlock") > 0);

    internal double BlockCut() => Bonus("DecreaseWeaponBlock") + Bonus("DecreaseShieldBlock") is > 0 and var cut ? Math.Min(0.9, cut / 10_000.0) : BlockReduction;

    // Special combat stats (user 2026-11-08, from the AION 2 combat notes; chances in 0.01% units like the other percentage stats):
    // Perfect - the hit takes the weapon's maximum damage; Hard Hit (Double Chance) - x2 damage in PvE, x1.5 in PvP; Additional Hit -
    // 1-4 extra small hits of 2.2-5% of the attack; Iron Wall - damage reduction (less the attacker's Ignore Iron Wall); Restoration -
    // a chance to recover part of max HP when hit (less the attacker's Ignore Restoration). Each has its Resist on the defender.
    public const double HardHitPve = 2.0, HardHitPvp = 1.5, ExtraHitMin = 0.022, ExtraHitMax = 0.05;
    /// <summary>Restoration's heal (OURS: the notes say "a portion of HP").</summary>
    public const int RestorationHealPercent = 3;

    /// <summary>The weapon with its maximum as the minimum when Perfect procs (chance in 0.01%, after the defender's Perfect Resist).</summary>
    WeaponStats PerfectRoll(WeaponStats weapon, int chance) => chance > 0 && rng.Next(10_000) < chance ? weapon with { Min = Math.Max(weapon.Min, weapon.Max) } : weapon;

    /// <summary>Hard Hit (each damaging hit, x multiplier) and Additional Hit (1-4 small extra hits) on a cast's hits.</summary>
    List<Hit> SpecialHits(List<Hit> hits, int hardHit, double multiplier, int extraHit, WeaponStats weapon)
    {
        if (hardHit > 0) hits = hits.Select(h => h.Amount > 0 && rng.Next(10_000) < hardHit ? h with { Amount = (int)Math.Round(h.Amount * multiplier) } : h).ToList();
        if (extraHit > 0 && hits.Count > 0 && hits.Any(h => h.Amount > 0) && rng.Next(10_000) < extraHit)
        {
            var attack = (weapon.Min + weapon.Max) / 2.0;
            for (var i = rng.Next(1, 5); i > 0; i--)
                hits.Add(new Hit(Math.Max(1, (int)Math.Round(attack * (ExtraHitMin + rng.NextDouble() * (ExtraHitMax - ExtraHitMin)))), false, hits[0].EffectId));
        }
        return hits;
    }

    /// <summary>A heal the player receives with Increases Incoming Heal (AmplifyHpHealGet, 0.01% units: wings, gear, titles).</summary>
    int HealReceived(int amount) => amount <= 0 ? amount : (int)Math.Round(amount * (1 + Math.Max(0, Bonus("AmplifyHpHealGet")) / 10_000.0));

    /// <summary>A stat from every source above the level's base (gear, enchants, soul binds, manastones, wings, titles, buffs...).</summary>
    int Bonus(string name) => CombatStat(name) - data.BaseStats(className, level).GetValueOrDefault(name);

    /// <summary>Physic or Magic: the damage line of a skill (skill guide damage type; physical by default).</summary>
    static string DamageLine(int skill) => ClientDamageLine(skill);

    /// <summary>
    /// The worn weapon for hits on monsters: its skin-collection damage, and its critical plus every other source's (Critical,
    /// Physic/MagicCritical, SkillCritical; user 2026-11-08: all character boosts count in PvE).
    /// </summary>
    WeaponStats PveWeapon(int skill = 0, Target? target = null)
    {
        var weapon = ScaledWeapon();
        // Behind or in front of the monster: Back/FrontAttackCritical add to the critical (user 2026-11-08).
        var sideCritical = target is not null && position is { } from
            ? FacingSide(target.Position, target.Direction, from) switch { > 0 => Bonus("BackAttackCritical"), < 0 => Bonus("FrontAttackCritical"), _ => 0 } : 0;
        var pm = DamageLine(skill);
        var ownCritical = data.Item(equippedWeapon)?.Stat("Critical") ?? 0;   // already in weapon.Critical (and in the gear sum)
        // The "Attack" of wings, titles, buffs, STR/INT, and the accessories' and soul binds' Attack (WeaponFixingDamage; user 2026-11-08).
        var attack = Bonus("FixingDamage") + Bonus(pm + "Damage") + Bonus("WeaponFixingDamage") + Bonus(pm + "WeaponFixingDamage");
        return weapon with { Min = weapon.Min + attack, Max = weapon.Max + attack,
            Critical = weapon.Critical + Bonus("Critical") - ownCritical + Bonus(pm + "Critical") + Bonus("SkillCritical") + sideCritical };
    }

    /// <summary>
    /// Where an attacker at <paramref name="from"/> stands against someone at <paramref name="at"/> facing <paramref name="facing"/>
    /// (degrees): 1 behind (over 120 degrees off the facing), -1 in front (under 60), 0 at the side.
    /// </summary>
    internal static int FacingSide(Vector3d at, double facing, Vector3d from)
    {
        var off = Math.Abs(GamePackets.PyMod(Math.Atan2(from.Y - at.Y, from.X - at.X) * 180 / Math.PI - facing + 180, 360) - 180);
        return off > 120 ? 1 : off < 60 ? -1 : 0;
    }

    /// <summary>Defense the player's hits ignore on monsters (flat): DefensePierce, Physic/MagicDefensePierce, PvEDefensePierce, IgnoreDefense, PvEIgnoreDefense.</summary>
    int PvePierce(int skill) =>
        Math.Max(0, Bonus("DefensePierce") + Bonus(DamageLine(skill) + "DefensePierce") + Bonus("PvEDefensePierce") + Bonus("IgnoreDefense") + Bonus("PvEIgnoreDefense"));

    /// <summary>The MP pool: MPMax 1500 plus buffs.</summary>
    int MpMax => (int)Math.Round((MpMaxBase + BuffStat("MPMax") + BoardStat("MPMax") + TitleStat("MPMax") + ExtraStat("MPMax")) * (1 + Bonus("MaxMPRatio") / 10_000.0));

    /// <summary>SP maximum: the spawn pool plus SPMax from every source (titles give up to +22,500; only buffs counted before).</summary>
    int SpMaxTotal => GamePackets.SpawnSp + Bonus("SPMax");

    /// <summary>
    /// Soul bind / manastone stats of worn gear, the collection stats, the worn items' other fixed main stats and the stats the
    /// primary and Empyrean Lord stats give (the sources outside the base calculation).
    /// </summary>
    int ExtraStat(string stat) => UpgradeStat(stat) + CollectionStat(stat) + GearMainStat(stat) + DerivedStat(stat) + DaevanionStat(stat) + GearEffectStat(stat)
        + MonolithStat(stat);   // the Monolith levels reached (GameSession.EmpyreanTraces.cs)

    /// <summary>The highest PcStatSecond row (Stat 1..1000); more points count as 1000 (CANDIDATE: the client table ends there).</summary>
    public const int PcStatSecondMaxPoints = 1000;

    /// <summary>
    /// A stat given by the primary stats (STR, DEX, INT, CON, AGI, WIS) and the Empyrean Lord stats (Justice .. Space): the client's
    /// PcStatSecond table (server-rules primary_stats). OFFICIAL: linear in all 1000 rows, and every pure derived value of the 56
    /// official 0x3649 matches it (STR 26 + Destruction 6 -> DamageRatio 320 + gear; DEX 2 + Freedom 34 -> EvasionRatio 360; Death
    /// 26 -> IgnoreRestoration 260; Wisdom 9 -> MPUseIncrease -90; DEX 2 -> 3 raised EvasionRatio, BlockRatio, CriticalResistRatio
    /// by 10 each).
    /// </summary>
    int DerivedStat(string stat) =>
        ServerRules.Embedded.PrimaryStats.TryGetValue(stat, out var from)
            ? (int)Math.Round(from.Sum(f => Math.Clamp(Bonus(f.From), 0, PcStatSecondMaxPoints) * f.PerPoint)) : 0;

    /// <summary>The fixed main stats items.json leaves out (tools/item_data.py keeps 8): the client item table's MainStats.</summary>
    static readonly HashSet<string> ItemInfoStats = new(StringComparer.OrdinalIgnoreCase)
        { "WeaponMinDamage", "WeaponDamage", "WeaponFixingDamage", "WeaponAccuracy", "Critical", "ArmorDefense", "HPMax", "Block" };

    /// <summary>
    /// A worn item's fixed main stat that items.json does not carry (gamedata/items, the client's MainStats): weapons'
    /// DecreaseWeaponBlock and MaxWeaponBlock, shields' DecreaseShieldBlock and MaxShieldBlock, arcana's primary and Empyrean Lord
    /// stats. OFFICIAL: the 0x3649 of a Sorcerer with and without the starter weapon gains/loses 93 DecreaseWeaponBlock 900 and 96
    /// MaxWeaponBlock 100000 with it (rec 010735 #10694 -> #13180).
    /// </summary>
    int GearMainStat(string stat) => ItemInfoStats.Contains(stat) ? 0
        : inventory.Values.Where(i => i.Container == InventoryItem.Equipped)
            .Sum(i => ItemDB.Embedded.Of(i.ItemId)?.Stats.Where(s => string.Equals(s.Name, stat, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value) ?? 0);

    IEnumerable<string> GearMainStatNames() =>
        inventory.Values.Where(i => i.Container == InventoryItem.Equipped)
            .SelectMany(i => ItemDB.Embedded.Of(i.ItemId)?.Stats.Select(s => s.Name) ?? []).Where(n => !ItemInfoStats.Contains(n));

    /// <summary>
    /// The stats every character has on the official sheet whatever the gear (OBSERVED in all 56 official 0x3649, a level-1 Sorcerer
    /// without gear: rec 010735 #10694): BackAttackCritical 100, RestHPRegen 10, RestMPRegen 250, GroundVehicleSpeed 300,
    /// FlyLimitHeight 100000, RestSPRegen 20000, Battle HP/MP/SP regen 5/25/5000, OPMax 100000, OPRegen 20000, Battle/Rest FP regen
    /// 2000/4000, ElementalistDespawnHPRegen 500, CombatFlySpeed -3000. HPMax, MPMax, SPMax, BattleMoveSpeed and DPMax are added apart.
    /// </summary>
    public static readonly IReadOnlyList<(int Type, int Value)> IntrinsicStats =
    [
        (100, 100), (195, 10), (201, 250), (208, 300), (211, 100000), (244, 20000), (275, 5), (276, 25), (277, 5000),
        (295, 100000), (297, 20000), (300, 2000), (301, 4000), (377, 500), (551, -3000),
    ];

    /// <summary>
    /// EStat 206 BattleMoveSpeed by class: OBSERVED -3000 for the Gladiator and the tutorial character, -4000 for the Sorcerer and
    /// the Ranger (0x3649 and 0x3633, 16 recordings). CANDIDATE for the others: the ranged classes (Elementalist) as the Sorcerer
    /// and Ranger, the rest as the Gladiator.
    /// </summary>
    public static int BattleMoveSpeedOf(string? className) => className is "Ranger" or "Sorcerer" or "Elementalist" ? -4000 : -3000;

    /// <summary>
    /// EStat 252 DPMax on the sheet: OBSERVED none at levels 1-9, 100000 at 10 and 18, 150000 at 20 and 45 (CANDIDATE between the
    /// observed levels).
    /// </summary>
    public static int SheetDpMax(int level) => level >= 20 ? 150_000 : level >= 10 ? 100_000 : 0;

    /// <summary>
    /// The stat sheet (0x3649 / 0x364a): the intrinsic stats every official sheet has, HPMax from PcStatLevel with every source,
    /// MPMax, SPMax, DPMax, the weapon's damage/critical/accuracy, the armour's defense and every stat of every source (gear main
    /// and soul-bind stats, primary and Empyrean Lord stats with what PcStatSecond derives from them, titles, rides, buffs...),
    /// in EStat order (OFFICIAL: ascending in 56 of 56 samples).
    /// </summary>
    (IReadOnlyList<(int Type, int Value)> Stats, int HpMax) StatSheet()
    {
        var gear = EquippedItems().ToList();
        var basis = data.BaseStats(className, level);
        var hpMax = Math.Max(1, (basis.GetValueOrDefault("HPMax") is > 0 and var hp ? hp : GamePackets.SpawnHpMax) + gear.Sum(i => i.Stat("HPMax")) + EnchantStat("HPMax") + RideStat("HPMax")
            + BuffStat("HPMax") + BoardStat("HPMax") + TitleStat("HPMax") + ExtraStat("HPMax"));
        hpMax = Math.Max(1, (int)Math.Round(hpMax * (1 + Bonus("MaxHPRatio") / 10_000.0)));   // Max HP % (titles, gear)
        var weapon = data.WeaponStatsOf(equippedWeapon);
        // Until 2026-11-09 the sheet started from the official level-1 tutorial sample (its sword and shield's 89/93/96/97 block
        // stats and Block 200 for every character, BattleMoveSpeed -3000 for every class) and never sent the primary stats' derived
        // values (the Status info window showed them as 0).
        var stats = IntrinsicStats.ToList();
        stats.AddRange([
            (31, weapon.Max + EnchantStat("WeaponDamage")), (33, weapon.Min + EnchantStat("WeaponMinDamage")),
            (128, weapon.Critical + RideStat("Critical") + BuffStat("Critical") + BoardStat("Critical") + TitleStat("Critical") + ExtraStat("Critical")),
            (318, weapon.Accuracy), (193, hpMax), (199, MpMax), (242, SpMaxTotal), (307, ArmorDefense),
            (206, BattleMoveSpeedOf(className)), (252, SheetDpMax(level)),
        ]);
        // OFFICIAL split (byte audit 2026-11-09, 56 official 0x3649): 52 Defense = the level's PcStatLevel Defense with the Defense
        // bonuses (322 for a level-33 character, 54 of 56 samples), 307 ArmorDefense = the armour's own defense (496 there), 19
        // FixingDamage = the level's base attack with its bonuses (46 there, 54 of 56). We sent the total defense as 307 and no 52/19.
        var basis19 = basis.GetValueOrDefault("FixingDamage");
        stats.Add((52, Defense - ArmorDefense));
        stats.Add((19, basis19 + gear.Sum(i => i.Stat("FixingDamage")) + EnchantStat("FixingDamage") + RideStat("FixingDamage") + BuffStat("FixingDamage")
            + BoardStat("FixingDamage") + TitleStat("FixingDamage") + ExtraStat("FixingDamage")));
        // Every other named stat from the sources (gear other than the weapon's own damage stats, enchants, pets, the creature
        // board, buffs, the equipped and owned titles) on its client EStat id: added to the sheet's value, or a new line when the
        // sheet has none (live 2026-11-06: only HPMax, MPMax and Critical were summed, every other bonus was dropped).
        var handled = new HashSet<int> { 31, 33, 128, 318, 193, 199, 242, 307, 298, 52, 19, 252 };
        var handledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ArmorDefense", "Defense", "WeaponDamage", "WeaponMinDamage", "WeaponAccuracy", "Critical", "HPMax", "MPMax", "SPMax", "FPMax", "FixingDamage" };
        // FPMax (298) is the flight gauge's maximum with every source (FlightMax), as on the official sheet.
        if (FlightMax() is > 0 and var flight) stats.Add((298, flight));
        var names = gear.SelectMany(i => i.Stats.Keys)
            .Concat(titleEquips.Where(p => p.Key != 0).SelectMany(p => Titles.Of(p.Value)?.EquipStats.Select(s => s.Stat) ?? []))
            .Concat(titles.SelectMany(id => Titles.Of(id)?.OwnStats.Select(s => s.Stat) ?? []))
            .Concat(rides.Values.Where(r => r.Owned).SelectMany(r => Boxes.RideStats(r.RideId, r.Level).Keys))
            .Concat(buffs.SelectMany(b => b.Stats.Keys))
            .Concat(UpgradeStatNames())
            .Concat(CollectionStatNames())
            .Concat(GearMainStatNames())
            .Concat(DaevanionStatNames())
            .Concat(MonolithStatNames())
            .Concat(GearEffectStatNames())   // equipment set bonuses (GameSession.GearEffects.cs)
            .Concat(ServerRules.Embedded.PrimaryStats.Keys)   // what the primary / Empyrean Lord stats give (0 drops out below)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in names)
        {
            if (handledNames.Contains(name) || StatIds.Of(name) is not int id || handled.Contains(id)) continue;
            var bonus = gear.Sum(i => i.Stat(name)) + EnchantStat(name) + RideStat(name) + BuffStat(name) + BoardStat(name) + TitleStat(name) + ExtraStat(name);
            if (bonus == 0) continue;
            var at = stats.FindIndex(s => s.Type == id);
            if (at >= 0) stats[at] = (id, stats[at].Item2 + bonus);
            else stats.Add((id, bonus));
        }
        // The official sheet lists only non-zero stats (0 zero values in 56 samples, 7,000+ entries), in EStat order.
        return (stats.Where(s => s.Item2 != 0).OrderBy(s => s.Item1).ToList(), hpMax);
    }

    /// <summary>Checks only: the stat sheet (0x3649 list) and the total defense.</summary>
    internal (IReadOnlyList<(int Type, int Value)> Stats, int Defense, int BaseDefense) StatSheetForChecks() =>
        (StatSheet().Stats, Defense, data.BaseStats(className, level).GetValueOrDefault("Defense"));

    /// <summary>The equipped armour's own defense (EStat 307 ArmorDefense on the official sheet): items and their enchant.</summary>
    int ArmorDefense => EquippedItems().Sum(SlotDefense) + EnchantStat("ArmorDefense");

    // ---- Combat Power (the character window's CP, a client EStat) --------------------------------------------------------
    // No official formula is published (AION2 computes CP internally; the client data carries none), so OURS: a weighted sum of
    // the character's final stat sheet, scaled so a geared Lv 45 lands in the live ~290k range (CP tracks ~82x gear score). The
    // field was always sent as 0, which left the Combat Power section empty (user 2026-11-08). Weights and CpScale are tunable;
    // calibrate CpScale against a live number if the magnitude is off.
    /// <summary>CP per point of gear score (equip item level). The live leaderboard sits at ~82x gear score; tunable.</summary>
    public double CpGearFactor = 82.0;

    /// <summary>Combat Power shown on the character window. No official formula is published (AION2 computes CP internally), so
    /// OURS: gear score (equip item level) x CpGearFactor, matching the live leaderboard's ~82x relationship. Bounded so it can
    /// never overflow the client's CP field - an earlier stat-sheet sum overflowed it (flat caps like FlyLimitHeight are 100000).</summary>
    public long CombatPower()
    {
        // OURS, fitted to the official 0x3656 changes in the recordings (2026-11-06): Attack +3 -> +30, Critical +5 -> +35, Critical
        // Resist +5 -> +5, MP +50 -> +12, HP +100 -> +17, Defense +40 with HP +60 -> +19; percentage stats (PvE/PvP damage, defense %)
        // raised the whole number by a few percent. USER 2026-11-08: titles, wings, skins and the rest must raise it, so every
        // source counts (Bonus = gear, soul binds, manastones, titles, wings, collections, buffs).
        var gear = EquipItemLevel() * CpGearFactor;
        var weapon = ScaledWeapon();   // the skin collection's weapon-type damage on top of the weapon
        var skinWeapon = (weapon.Min + weapon.Max) / 2.0 - (data.WeaponStatsOf(equippedWeapon).Min + data.WeaponStatsOf(equippedWeapon).Max) / 2.0;
        var flat = 10.0 * (Bonus("FixingDamage") + Bonus("WeaponFixingDamage") + skinWeapon)
            + 7.0 * Bonus("Critical") + 1.0 * (Bonus("CriticalResist") + Bonus("DefensePierce") / 10.0)
            + 0.5 * (Bonus("Accuracy") + Bonus("Evasion") + Bonus("Block") + Bonus("ArmorEvasion") + Bonus("BlockPierce"))
            + 0.25 * Math.Max(0, Defense - data.BaseStats(className, level).GetValueOrDefault("Defense"))
            + 0.17 * Bonus("HPMax") + 0.24 * Bonus("MPMax")
            + 3.0 * (Bonus("PvEAddDamage") + Bonus("PvPAddDamage") + Bonus("BossNpcAddDamage") + Bonus("CriticalAddDamage"));
        // Percentage stats (0.01% units), weighted 1.5x as fitted (+275 units of PvE/PvP damage and defense % gave +3..7%).
        var percent = Bonus("AmplifyAllDamage") + Bonus("PvEAmplifyDamage") + Bonus("PvPAmplifyDamage") + Bonus("BossNpcAmplifyDamage")
            + Bonus("AmplifyCriticalDamage") + Bonus("DamageRatio") + Bonus("DefenseRatio") + Bonus("MaxHPRatio") + Bonus("DecreaseDamage")
            + Bonus("PvEDecreaseDamage") + Bonus("PvPDecreaseDamage") + Bonus("BossNpcDecreaseDamage") + Bonus("CombatSpeed") + Bonus("CoolTimeDecrease");
        var power = (gear + Math.Max(0, flat)) * (1 + Math.Max(0, percent) * 1.5 / 10_000.0);
        return Math.Clamp((long)Math.Round(power), 0, 2_000_000_000);
    }

    /// <summary>The highest Combat Power this session has sent (0x3656 second field; OBSERVED >= the current one).</summary>
    long highestCombatPower;

    /// <summary>0x3656 CharCombatPowerUpdate_NT {cp i64, highest cp i64}. OBSERVED: the official server sends it right after
    /// every 0x364a CharStatsUpdate (load, equip, buffs, rides). Ours never did, so the client fell back to its own number:
    /// live 2026-11-08 (Axel3) a huge negative CP and "not enough combat power" on every gather spot and door.</summary>
    byte[] CombatPowerUpdate()
    {
        var cp = CombatPower();
        highestCombatPower = Math.Max(highestCombatPower, cp);
        return new Aion.Protocol.Native.Generated.CharCombatPowerUpdateNT { CombatPower = cp, HighestCombatPower = highestCombatPower }.ToArray();
    }

    /// <summary>The stats and HP max the client last received (0x3649 / 0x364a); null sends the whole sheet next.</summary>
    Dictionary<int, int>? sentStats;
    long sentHpMax;

    void SentStatsBaseline(IReadOnlyList<(int Type, int Value)> stats, long hpMax)
    {
        sentStats = [];
        foreach (var (type, value) in stats) sentStats[type] = value;
        sentHpMax = hpMax;
    }

    /// <summary>The stat sheet's HP and MP regeneration out of combat (195, 201) and in combat (275, 276).</summary>
    const int RestHpRegenStat = 195, RestMpRegenStat = 201, BattleHpRegenStat = 275, BattleMpRegenStat = 276;
    /// <summary>
    /// The base of those four (OBSERVED: every official 0x3649 of a character without regeneration gear has 10, 250, 5, 25;
    /// 25 recordings of 2026-10-13 to 11-06; with gear e.g. 60, 255, 35, 35).
    /// </summary>
    public const int RestHpRegenBase = 10, RestMpRegenBase = 250, BattleHpRegenBase = 5, BattleMpRegenBase = 25;

    int RegenBonus(string stat) => Bonus(stat);   // every source above the base (gear, buffs, boards, titles, wings, soul binds)

    /// <summary>
    /// HP regained a second: stat 195 out of combat, 275 in combat. OBSERVED (our recordings 2026-10-13 to 11-06): out of combat
    /// the HP update steps by the character's own 195 every second (10 for 195 = 10, 60 for 60; 30 sitting, OURS factor 3).
    /// CANDIDATE: in combat by 275 (sent in the sheet; the tick regenerates none in combat), the Rest/Battle and plain HPRegen
    /// bonuses as SP's.
    /// </summary>
    int HpRegen(bool battle) => Math.Max(0, (battle ? BattleHpRegenBase : RestHpRegenBase) + RegenBonus(battle ? "BattleHPRegen" : "RestHPRegen") + RegenBonus("HPRegen"));

    /// <summary>MP regained a second: stat 201 out of combat (OBSERVED steps of 250 and 255 for 201 = 250 and 255), 276 in combat.</summary>
    int MpRegen(bool battle) => Math.Max(0, (battle ? BattleMpRegenBase : RestMpRegenBase) + RegenBonus(battle ? "BattleMPRegen" : "RestMPRegen") + RegenBonus("MPRegen"));

    /// <summary>
    /// 0x364a with only what changed since the last one. OBSERVED (byte audit 2026-11-09, 400 official samples): the official
    /// server sends 0-9 changed stats (rarely 32/64), a stat that dropped to 0 as an explicit 0 (25 of 903 entries), and
    /// _stat_hpmax 0 unless the HP max changed (141 of 184). We sent the whole sheet (75-89 stats) and the HP max every time.
    /// </summary>
    byte[] StatsUpdate(IReadOnlyList<(int Type, int Value)> stats, long hpMax)
    {
        if (sentStats is not { } before)
        {
            SentStatsBaseline(stats, hpMax);
            return GamePackets.CharStatsUpdate(playerKey, stats, hpMax);
        }
        var changed = new List<(int Type, int Value)>();
        var now = new HashSet<int>();
        foreach (var (type, value) in stats)
        {
            now.Add(type);
            if (!before.TryGetValue(type, out var old) || old != value) changed.Add((type, value));
        }
        foreach (var type in before.Keys)
            if (!now.Contains(type) && before[type] != 0) changed.Add((type, 0));
        var hpChanged = hpMax != sentHpMax;
        SentStatsBaseline(stats, hpMax);
        return GamePackets.CharStatsUpdate(playerKey, changed, hpChanged ? hpMax : 0);
    }

    /// <summary>Applies the stat sheet to the pools (HP/MP clamped) and returns the updates for the client.</summary>
    List<byte[]> RefreshStats()
    {
        gearState = null;   // the worn gear may have changed: its passives, set bonuses and skill levels again
        if (resources is null) return [];
        var (stats, hpMax) = StatSheet();
        resources.HpMax = hpMax;
        resources.MpMax = MpMax;
        resources.SpMax = SpMaxTotal;
        resources.Hp = Math.Min(resources.Hp, hpMax);
        resources.Mp = Math.Min(resources.Mp, resources.MpMax);
        resources.Sp = Math.Min(resources.Sp, resources.SpMax);
        resources.FpMax = FlightMax();   // gear, soul binds, wings owned (OBSERVED: the official FPMax moved with equips and binds)
        resources.Fp = Math.Min(resources.Fp, resources.FpMax);
        List<byte[]> refreshed = [StatsUpdate(stats, hpMax), CombatPowerUpdate(), resources.Maxima(), .. GearChanges()];
        if (MoveSpeedBonus() is var speed && speed != sentMoveSpeed)
        {
            sentMoveSpeed = speed;
            refreshed.Add(PlayerPackets.MoveSpeedUpdate(playerKey, [(MoveSpeedStat, speed)]));
        }
        return refreshed;
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
    /// <summary>The flight gauge (FP, FP max) for tools and checks.</summary>
    public (int Fp, int FpMax) Flight => resources is null ? (0, 0) : (resources.Fp, resources.FpMax);

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
        RiftTeleporting(mapId);   // a move off the other faction's land ends a rift visit (GameSession.Rifts.cs)
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
            mapEnvState = null;   // a reload into the same map keeps the changed environment; another map starts fresh
            ResetBombs();
            ResetChains();
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
        return [.. ending, GamePackets.PrepareEnterMap(loadTicket, mapId, at, GameTime(now), reason: reason, effect: effect, cutscene: cutscene)];
    }

    /// <summary>The teleport effect of a Kibelisk trip (0x3621 effect byte 0, OBSERVED in all 5 official trips).</summary>
    const byte KibeliskEffect = 0;

    /// <summary>The arrival abnormal after every map load: 10002, 5 s, on the player, caster self, range base the arrival point
    /// (OBSERVED 0x382a mask 0x11 right after 0x3646 in all 60 official loads: world entry, teleports, layer moves).</summary>
    const int ArrivalAbnormal = 10002;
    const int ArrivalAbnormalMs = 5000;

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
        world.Map(mapId, instanceKey, MapLayer(mapId)).Join(worldPlayer);
        if (previous is not null && previous != worldPlayer.Map) world.Drop(previous);
    }

    /// <summary>Leaves the shared map (lobby, disconnect): others see this player disappear. Call under the world gate.</summary>
    public void ExitSharedWorld()
    {
        DismissSummons();   // the others see them go (0x3642 broadcast); the owner's client drops them with the world
        HandOverCopy();
        if (worldPlayer is not null && options.World?.Online.ContainsKey(playerKey) == true) LeaveSocial(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (worldPlayer?.Map is { } map)
        {
            map.Leave(worldPlayer);
            // A private copy nobody is in any more goes away (a solo dungeon left by disconnect, logout or character switch
            // must start fresh next time; before, the same session's key found the old copy with its dead NPCs and state).
            options.World?.Drop(map);
        }
        if (worldPlayer is not null)
        {
            options.World?.Online.Remove(playerKey);
            while (worldPlayer.Outbox.TryDequeue(out _)) { }
        }
    }

    /// <summary>The abyss ranking grade every ordinary official player showed in 0x3645: 19, SeasonRankingGradeInfo
    /// ranking_grade_info_abyss 19 "Trainee", the last grade of ranking_grade_class_abyss_S1 (the unranked; UI_NAMEPLATE_OTHER_RACE_
    /// SEASON_RANKING_NONE "Trainee"). A ranked player shows the grade of its Abyss ranking (GameSession.Ranking.cs).</summary>
    const int OfficialAbyssRankingGrade = 19;

    /// <summary>This player as others see it (0x3645 CharAppear).</summary>
    byte[] OtherAppearance()
    {
        var profile = Profile();
        return PlayerPackets.CharAppear(new OtherCharacter
        {
            Key = playerKey, Name = CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), PcId = profile?.PcId ?? 1, ServerId = ServerId,
            Race = profile?.Race ?? 1, Gender = character?.Gender ?? 1, Position = position ?? worldPlayer?.Position ?? default,
            Direction = worldPlayer?.Heading ?? 0, Hp = resources?.Hp ?? 1, HpMax = resources?.HpMax ?? 1, Mp = resources?.Mp ?? 0, MpMax = resources?.MpMax ?? 0,
            Level = level, WingId = wingId, WingSkinId = wingId != 0 && wingSkin != 0 ? wingSkin : 0, Guild = MyGuildTag(), TitleId = ShownTitle, Equips = VisualEquips(all: true), Abnormals = BuffWires(),
            EquipItemLevel = inventory.Values.Where(i => i.Container == InventoryItem.Equipped).Sum(i => i.Detail?.ItemLevel ?? 0),
            MoveState = (byte)(moving ? 1 : 0), DeadReckoning = moving,
            CustomizingData = character?.CustomizingData ?? [], VoiceType = character?.VoiceType ?? 0, DbId = selected?.WireId ?? playerKey,
            Sprinting = sprinting, ToggleFlags = sealStoneOn ? 1 : 0, SealstoneItemId = SealStoneItemId,
            // OBSERVED (byte audit 2026-11-09): the official appearance names the player's default ride and abyss grade 19 (the
            // unranked; 16 once): the grade of the player's Abyss ranking (feature/rank-system).
            DefaultRideDataId = defaultRide, AbyssRankingGrade = abyssGradeShown,
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

    /// <summary>
    /// The enchant level of each visual slot's equipped item. The client draws the enchant auras (green at +3/+4, then blue,
    /// fire, gold) from the enchant byte of the equip records (OBSERVED: official 0x561c records carry 1..17); before
    /// 2026-11-06 we always sent 0, so no aura showed for the player or for others.
    /// </summary>
    Dictionary<int, byte> VisualEnchants()
    {
        var levels = new Dictionary<int, byte>();
        foreach (var item in inventory.Values.Where(i => i.Container == InventoryItem.Equipped))
            if (VisualSlot.TryGetValue(item.Slot, out var visual) && item.Detail is { EnchantLevel: > 0 } detail) levels[visual] = detail.EnchantLevel;
        return levels;
    }

    /// <summary>0x561c for this player and those who see it: the visual slots whose item changed.</summary>
    byte[]? EquipChange(Dictionary<int, int> before, int equipItemLevel)
    {
        var after = VisualItems();
        var enchants = VisualEnchants();
        var changes = new List<(byte, EquipBroad, EquipBroad)>();
        foreach (var slot in before.Keys.Union(after.Keys).Order())
        {
            int was = before.GetValueOrDefault(slot), now = after.GetValueOrDefault(slot);
            if (was == now) continue;
            EquipBroad Broad(int id, byte enchant) => new(id, enchant, VisualSlot: (byte)slot, Godstones: id == 0 ? [] : [0, 0, 0]);
            changes.Add(((byte)slot, Broad(was, 0), Broad(now, now == 0 ? (byte)0 : enchants.GetValueOrDefault(slot))));
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
    bool SharedCastPacket(byte[] packet) => Opcode(packet) is 0x3802 or 0x3803 or 0x3804 or 0x3806 or 0x3642 or 0x3641 or 0x8d21 or 0x8d04 or 0x382a
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
    /// <summary>The highest point of the fall in progress (gravity started), null when not falling (or the fall glided/flew/swam).</summary>
    double? fallTopZ;
    /// <summary>GravityFallingEffect_NT {key varint, damage varint}: OBSERVED once (115817 #128842: 1738 e545 e802 = 360 damage, then the HP).</summary>
    public const ushort GravityFallingEffectNt = 0x3817;

    /// <summary>
    /// Fall damage (user request 2026-11-07, 2026-11-08): a fall is measured from the client's gravity messages (CharGravityStart
    /// 0x3702 / Update 0x3703: the highest point) to the FIRST solid-ground movement that ends it - the landing. The landing is
    /// any on-ground op (0x3700/0x3701/0x3718/0x370c..0x3711), not only the first three, so the damage lands on impact and is not
    /// delayed until the player happens to walk. Above server-rules.xml falls/safe_cm the player loses percent_per_m of max HP per
    /// metre, a fall of death_cm or more kills (a high fall is fatal).
    /// Gliding, flying, wind paths and water end a fall without damage. PROVISIONAL: the official formula is unknown (one
    /// official fall: 360 damage, about 6-7% of max HP, height unknown).
    /// </summary>
    List<byte[]> FallCheck(ushort op, Vector3d at, long now)
    {
        switch (op)
        {
            case 0x3702 or 0x3703:
                fallTopZ = Math.Max(fallTopZ ?? at.Z, at.Z);
                return [];
            case 0x3700 or 0x3701 or 0x3718 or 0x370c or 0x370d or 0x370e or 0x370f or 0x3710 or 0x3711 when fallTopZ is { } top && air == AirState.Fall:
            {
                fallTopZ = null;
                var rules = ServerRules.Embedded;
                var drop = top - at.Z;
                if (drop <= rules.FallSafeCm || resources is null || dead) return [];
                var damage = drop >= rules.FallDeathCm ? resources.Hp
                    : (int)Math.Ceiling(resources.HpMax * rules.FallPercentPerMetre / 100.0 * (drop - rules.FallSafeCm) / 100.0);
                if (damage <= 0) return [];
                var effect = PacketWriter.Opcode(GravityFallingEffectNt).Varint(playerKey).Varint(damage).ToArray();
                worldPlayer?.Map?.Broadcast(worldPlayer, [effect]);
                return [effect, .. NpcHitsPlayer(damage, now)];
            }
            case 0x3706 or 0x3707 or 0x370a or 0x370b or 0x3712 or 0x3713 or 0x3714 or 0x3715 or 0x3716 or 0x3717:
                fallTopZ = null;   // a glide, flight, wind path or water ends the fall: no damage
                return [];
            default:
                return [];
        }
    }

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
                air = AirState.Ground; landedSinceAirStart = true; break;
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
                // CharGlideStart_RQ: mask | pos | rotate f32 | velocity | [glide mode u8 when mask bit 1] | dead reckoning | time.
                glideMode = (mask & 1) != 0 && plain.Length > 31 ? plain[31] : (byte)0;
                relay = PlayerPackets.GlideStart(playerKey, at, heading, V(19), glideMode);   // others see the dive / boost too
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
    /// <summary>Messages other sessions queued for this player and not yet sent (health page: outbox lengths).</summary>
    public int OutboxLength => worldPlayer?.Outbox.Count ?? 0;

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
        || riftVisit is { } riftEnd && now >= riftEnd.Until && !dead   // a rift visit's end moves the player home (GameSession.Rifts.cs)
        || Social is { } s && worldPlayer is not null && s.DuelOf(worldPlayer) is { } duel && duel.Challenger == worldPlayer && now >= duel.NextMs
        || Social is { } s2 && worldPlayer is not null && ArenaOf(worldPlayer) is { } arena && arena.Owner == worldPlayer && (now >= arena.NextMs || arena.RespawnAt.Values.Any(r => now >= r));

    /// <summary>The time of the last request or tick (for work done from a request without its own clock).</summary>
    long lastNow;
    /// <summary>Set by a map load: the arrival counts like a position report on the next tick.</summary>
    bool arrivalCheckDue;

    public async Task<List<byte[]>> TickAsync(long now, CancellationToken ct = default) => OldMapFirst(await TickCoreAsync(now, ct));

    async Task<List<byte[]>> TickCoreAsync(long now, CancellationToken ct)
    {
        lastNow = now;
        var replies = new List<byte[]>();
        while (posted.TryDequeue(out var item)) replies.AddRange(await item.Run(now));
        replies.AddRange(ObjectRemovalTick(now));   // used-up objects die and despawn on their timing
        replies.AddRange(BreakRemovalTick(now));    // broken objects despawn (GameSession.Destroy.cs)
        replies.AddRange(RunTimers(now));
        replies.AddRange(await RunTimedActionsAsync(now, ct));
        replies.AddRange(await KillWorkAsync(ct));
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
        if (shownCurrencies is null && resources is not null) replies.AddRange(await CurrencyChangesAsync(ct));   // Quna once in the world
        replies.AddRange(FlushHeldDash(now));
        replies.AddRange(SprintTick(now));
        replies.AddRange(DodgeAbnormalTick(now));
        replies.AddRange(await AutoPotionAsync(now, ct));
        replies.AddRange(await AutoUseItemsAsync(now, ct));
        replies.AddRange(await ExpiryTickAsync(now, ct));   // period items that ended (GameSession.ItemStorage.cs)
        replies.AddRange(await RiftTickAsync(now, ct));   // rift notice, the visit's end, the field maps' cubes and rifts (GameSession.Rifts.cs)
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
                    // OFFICIAL (rec 20261107-211036, 0x8d00 FP every 100 ms): flying drains 1,000 per tick (10,000/s); hovering - idle
                    // between gathers and during the three airborne Od gathers, 63.1-94.2 s - drains 750 per tick (HoverDrain 7,500/s) without
                    // a single refill; the gauge refills only after landing (+4,000/s). The 2026-11-08 rule "hovering refills" let a player
                    // stay up for ever (user 2026-11-11) and is gone.
                    var hovering = air == AirState.Fly && position is { } flyAt && flightTickAt is { } before2
                        && Math.Sqrt((flyAt.X - before2.X) * (flyAt.X - before2.X) + (flyAt.Y - before2.Y) * (flyAt.Y - before2.Y) + (flyAt.Z - before2.Z) * (flyAt.Z - before2.Z))
                           < FlightIdleCm * seconds;
                    // A flight ring's FlyCost (19000064 / 19000010: 10000) is FP per second off the flight drain (OBSERVED rec
                    // 20261107-012837: no drain in plain flight, 500 per 100 ms running instead of 1500, while 19000064 lasted).
                    // Hovering likewise (CANDIDATE); gliding is not flight (CANDIDATE: no glide under the buff recorded).
                    if (hovering)
                        resources.Fp = (int)Math.Max(0, fp - seconds * FlightDrain(HoverDrain));
                    else if (air is AirState.Fly or AirState.Glide)
                        resources.Fp = (int)Math.Max(0, fp - seconds * (air == AirState.Glide ? GlideDrainOf(glideMode) : FlightDrain(flySprint ? FlySprintDrain : FlyDrain)));
                    else if (air == AirState.Ground)
                        resources.Fp = (int)Math.Min(resources.FpMax, fp + seconds * (inCombat ? FpRegenBattle : FpRegenRest));
                }
                flightTickAt = position;
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
                // HP recovers by stat 195 a second out of combat (OBSERVED, HpRegen); in combat and while out of breath none (OURS:
                // stat 275 in combat is not settled by our recordings). MP by 201 / 276 (MpRegen). Sitting (CharRestStart)
                // triples both (the recorded +30 steps for 195 = 10).
                var rest = Resting && !inCombat ? RestRegenFactor : 1;
                if (!inCombat && !suffocating) resources.Hp = (int)Math.Min(resources.HpMax, resources.Hp + seconds * rest * HpRegen(battle: false));
                if (suffocating && !invincible.Hp)
                    resources.Hp = (int)Math.Max(0, resources.Hp - seconds * Math.Max(1L, (long)resources.HpMax * SuffocationRatio / 10000));
                resources.Mp = (int)Math.Min(resources.MpMax, resources.Mp + seconds * rest * MpRegen(battle: inCombat));
                if (!sprinting)
                    resources.Sp = (int)Math.Min(resources.SpMax, (resources.Sp + seconds * (inCombat ? SpRegenInCombat + Bonus("BattleSPRegen") : SpRegenOutOfCombat + Bonus("RestSPRegen")) + seconds * Bonus("SPRegen"))
                        * 1.0 + seconds * (inCombat ? SpRegenInCombat : SpRegenOutOfCombat) * Math.Max(0, Bonus("SPRegenRatio")) / 10_000.0);   // + SP regen %
                Pin(resources);
                // Invincible SP is resent each second: flight spends it on the client side.
                if (invincible.Sp || before != (resources.Hp, resources.Mp, resources.Sp)) replies.Add(resources.Update());
                if (resources.Op != op) replies.Add(resources.OpUpdate());
                if (resources.Fp != fp) replies.Add(resources.FpUpdate());
                if (suffocating && resources.Hp == 0) replies.AddRange(Die(0, now));
            }
        }
        if (pendingUse is { } use && now >= use.DueMs)
        {
            pendingUse = null;
            replies.AddRange(await CompleteUseAsync(use.Index, ct, now, timed: true));
        }
        if (dead && now >= reviveAt) replies.AddRange(Revive(now));
        if (itemCast is { } cast && now >= cast.DueMs) replies.AddRange(await CompleteItemCastAsync(now, ct));
        replies.AddRange(BuffTick(now));
        replies.AddRange(await QuickSlotAutoUseAsync(now, ct));
        replies.AddRange(DropTick(now));
        replies.AddRange(MapEventTick(now));
        replies.AddRange(TrapTick(now));
        replies.AddRange(DotTick(now));
        replies.AddRange(PlayerTrapTick(now));
        replies.AddRange(SummonTick(now));
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
        await FlushTicketsAsync(ct);
        await FlushDailyRecordsAsync(ct);
        await FlushFieldBossKillsAsync(ct);
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
                aiMap.Ai.AvoidanceOf ??= (key, attacker) => aiMap.Players.TryGetValue(key, out var p) ? p.Session.PveAvoidance(attacker) : (0, 0, 1, 0);
                aiMap.Ai.BlockCutOf ??= key => aiMap.Players.TryGetValue(key, out var p) ? p.Session.BlockCut() : BlockReduction;
                aiMap.Ai.HitSideOf ??= (key, attacker) => aiMap.Players.TryGetValue(key, out var p) ? p.Session.HitSide(attacker) : (GamePackets.AngleFront, false);
                aiMap.BroadcastAll(aiMap.Ai.Tick(aiMap.Targets, aiMap.NpcVictims(), now, (key, damage) =>
                {
                    if (aiMap.Players.TryGetValue(key, out var victim)) victim.Send(victim.Session.NpcHitsPlayer(damage, now, aiMap.Ai.LastAttacker, aiMap.Ai.LastSkill));
                    else if (aiMap.Players.Values.FirstOrDefault(p => p.Session.OwnsSummon(key)) is { } owner) owner.Send(owner.Session.SummonHit(key, damage, now));
                    return [];
                }));
                aiMap.Ai.HpFloor ??= HpFloor;
                // NPC damage to NPCs counts for the map events' HP objectives too (Maard fighting Elvida in the Asmodian HQ001).
                foreach (var hit in aiMap.Ai.TakeNpcHits()) replies.AddRange(MapEventHp(hit, now));
                aiMap.Ai.TakeCasts();   // boss helpers: solo copies only for now (BossMechanicTick)
                aiMap.Ai.TakeVanished();
                var bossAdds = BossAddsTick(aiMap.Ai, aiMap.Targets, now);
                aiMap.BroadcastAll(bossAdds, worldPlayer);
                replies.AddRange(bossAdds);
                var npcKills = NpcKillsByNpcs(aiMap.Ai, now);
                aiMap.BroadcastAll(npcKills, worldPlayer);
                replies.AddRange(npcKills);
            }
        }
        else if (targets is not null && (ai.DefenseOf ??= _ => Defense) is not null && (ai.AvoidanceOf ??= (_, attacker) => PveAvoidance(attacker)) is not null && (ai.BlockCutOf ??= _ => BlockCut()) is not null
                 && (ai.HitSideOf ??= (_, attacker) => HitSide(attacker)) is not null)
        {
            ai.HpFloor ??= HpFloor;
            // The player and its creature summons are the monsters' victims (a summon hit by a monster takes the hit itself).
            List<(int Key, Vector3d Position)> victims = invisible || dead || FriendlyToAll || position is not { } me ? [] : [(NpcAi.PlayerKey, me), .. SummonVictims()];
            replies.AddRange(Visible(ai.Tick(targets, victims, now, (key, damage) => summons.ContainsKey(key) ? SummonHit(key, damage, now) : NpcHitsPlayer(damage, now, ai.LastAttacker, ai.LastSkill))));
            foreach (var hit in ai.TakeNpcHits()) replies.AddRange(MapEventHp(hit, now));
            foreach (var gone in ai.TakeVanished()) seenNpcs.Remove(gone);   // self-destructed (traps.xml self_destruct)
            replies.AddRange(BossMechanicTick(ai, now));
            replies.AddRange(BossAddsTick(ai, targets, now));   // adds called by boss skills, a reset boss's adds gone (client Spawn effects)
            replies.AddRange(NpcKillsByNpcs(ai, now));
        }
        PartyTick(now);
        DuelTick(now);
        ArenaTick(now);
        replies.AddRange(await Systems2TickAsync(now, ct));   // GameSession.Systems2.cs
        await FlushSeasonPointsAsync(ct);   // GameSession.Ranking.cs
        replies.AddRange(await RankRewardsTickAsync(now, ct));   // GameSession.RankRewards.cs: weekly rewards, rank reward buffs
        if (pendingAchievementPackets.Count > 0) { replies.AddRange(pendingAchievementPackets); pendingAchievementPackets.Clear(); }
        if (worldPlayer is not null)
            while (worldPlayer.Outbox.TryDequeue(out var queued)) replies.Add(queued);
        await CheckpointAsync(now, force: false, ct);
        return replies;
    }

    /// <summary>Saves a selected stored character's position every 30 s when it moved, or now (force).</summary>
    public async Task CheckpointAsync(long now, bool force, CancellationToken ct = default)
    {
        if (force) await SaveLogoutStateAsync(now, ct);   // logout, lobby, disconnect: buffs and FP (GameSession.LogoutState.cs)
        if (force) await SaveLobbyViewAsync(ct);   // the character list's combat power and wing (GameSession.Lobby.cs)
        if (Store is null || selected is null || position is not { } at || at == savedPosition) return;
        if (!force && now < checkpointAt) return;
        checkpointAt = now + CheckpointMs;
        try
        {
            // Inside a dungeon copy the stored spot is its way out (where the player came in: next to the NPC that sent them),
            // so a disconnect, logout or crash brings them back there and the copy starts fresh on the next entry (the user's
            // rule 2026-11-06; the official relog is still to be recorded). Hand-made instance layers keep their exit point.
            var (saveMap, saveAt) = InstanceWayOut() ?? (worldMap, at);
            selected = await Store.SavePositionAsync(selected, saveMap, saveAt, ct);
            savedPosition = at;
            Checkpoints++;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or GameplayException)
        {
            PersistenceErrors.Add(error.GetType().Name);
        }
    }

    /// <summary>The field spot a dungeon copy was entered from, while in one (none for hand-made instance layers).</summary>
    (int Map, Vector3d At)? InstanceWayOut() =>
        instanceKey != 0 && Instances.Of(worldMap) is null && instanceReturn is { } back && !IsInstanceMap(back.Map) ? back : null;

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
        // A length with the top bit set is negative (fuzz 2026-11-09: GetString threw and the connection ended).
        if (length < 0 || (long)at + length + 8 > plain.Length) return null;
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

public sealed partial class GameSession
{
    /// <summary>Checks: the lists the select reply carries (CharacterLists).</summary>
    internal List<byte[]> CharacterListsForChecks(long now) => CharacterLists(now);
}
