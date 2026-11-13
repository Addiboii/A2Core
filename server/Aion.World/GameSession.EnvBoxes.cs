using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aion.Protocol.Native;

namespace Aion.World;

// Dungeon and field boxes (user 2026-11-10: "all the instance/dungeon boxes, some give orbs, some give power shards").
//
// Reward boxes: an object whose EnvObjData id has a RewardData row (tools/data/loot.json "npcs": RewardData ID = EnvObjData ID, e.g.
// 1400711 E_CommonRewardBox_001 -> reward_manual_Dungeon_Seal_1400711 = 300 Power Shards) grants its RewardItem/RewardPackage roll
// (ItemProb of 1e8, count min..max, race) when its use completes. OBSERVED wire (3 kinds of objects, rec 20261106-105448 #17549-#17574,
// 20261107-010735 #10937-#10948 and #12324-#12334): 0x363a completed, 0x8d6d finish [0x363b available for a bGetRewardOnce object],
// 0xe33a {object key, its position, the items}, then ~50 ms later 0x561b with increase reason 49. No E_CommonRewardBox use is recorded:
// that it follows the same wire is CANDIDATE.
// Who may take it: bGetRewardOnce (79 objects, e.g. the CommonRewardBoxes and Monolith fragments) - each player once per dungeon copy
// (open world: once per character until the server restarts), the object stays usable for the others (OBSERVED: the fragment's
// 0x363b after its reward). Otherwise an InteractCount-1 object is spent for the whole copy (OBSERVED: no 0x363b after the Drakan
// corpse, the box pile and the orb box); in the open world it comes back after OpenWorldRespawnMs (OURS: no respawn recorded).
// A second use is refused with the long 0x3637 (EnvObj_Fail_CanInteractOnlyOnce, CANDIDATE result; the form is OBSERVED).
//
// Recovery orb boxes: opening one summons recovery orbs around it (OBSERVED rec 105448 #53277-#53343: 0x3638, 0x363a, 0x8d6d, 0x3637,
// no 0x363b, then one 0x3634 per orb: EnvBoxPackets.SummonedEnvObjSpawn, origin = the box). Which orbs: E_All_RecoveryOrbBoxRandom_01
// a random 3 or 4 of HP/MP/stamina/speed (OBSERVED 4 then 3: HP, MP, SPD); the box -> orb link is not in the client data, so the fixed
// boxes follow the client's own test boxes (Test_recoveryOrbBox_01..04 / EventFunc T_TestSummonRecoveryOrb_HP/MP/STA/SPD: one orb of
// that kind within 300; CANDIDATE). The orbs (EnvObjData Usage Overlap, LifeTime 30000, InteractRange 40) are taken by walking into
// them: the client asks with 0x3636 (the 0x3637 answers it), the official answer (OBSERVED #53751-#53783): 0x3638, 0x363a, 0x8d6d,
// 0x8d05 die, 0x3637, the orb's abnormal (EventFunc Event_EnvObj_HP/MP/STA/SPD/OP_Marble: AddAbnormal 19000013/15/16/17/29 for
// 800 / 10000 ms; GameSession.Buffs.cs restores the points), 0x3635 {key, 4, 3}. The rest of the copy sees 0x8d05 and 0x3635 (CANDIDATE).
// An orb nobody takes vanishes after its LifeTime (0x3635 state 1 reason 1, CANDIDATE: none expired in the recordings).
// Map events summon objects too (SummonAroundTarget with an EnvObjData template, e.g. CoastalCave's Basic_RecoveryOrb_01 around a
// spawner): GameSession.MapEvents.cs SummonAround.

/// <summary>A summoned object (a recovery orb) of a map: its key, EnvObjData id, place and end of life.</summary>
public sealed class SummonedEnvObj(int key, int env, Vector3d at, long expiresAt)
{
    public int Key { get; } = key;
    public int Env { get; } = env;
    public Vector3d At { get; } = at;
    public long ExpiresAt { get; } = expiresAt;
}

public sealed partial class MapInstance
{
    /// <summary>Objects summoned on this map (recovery orbs) by key: shared by everyone in the copy.</summary>
    public Dictionary<int, SummonedEnvObj> Summoned { get; } = [];
}

public sealed partial class GameSession
{
    /// <summary>Entity keys of summoned objects: FirstEnvSummonKey .. + EnvSummonKeySpan (clear of objects 910000+ and triggers 950000+).</summary>
    public const int FirstEnvSummonKey = 960_000, EnvSummonKeySpan = 30_000;
    /// <summary>ERelationshipEntity of the recovery orbs (EnvObjData EnvObj_MonsterEnemy; OBSERVED 0x1c in every orb's 0x3634).</summary>
    public const byte OrbRelationship = 28;
    /// <summary>OURS: an open-world box (no copy) that is spent comes back after this long (no official respawn recorded).</summary>
    public const long OpenWorldRespawnMs = 300_000;
    /// <summary>OURS: slack on a summoned object's InteractRange (latency; as WorldObjects.InteractSlack).</summary>
    const double OrbReachSlack = 400;

    static int nextEnvSummonKey;

    static int NextEnvSummonKey() => FirstEnvSummonKey + (int)((uint)Interlocked.Increment(ref nextEnvSummonKey) % EnvSummonKeySpan);

    /// <summary>
    /// A recovery orb box: the orb templates it summons and the radius. Random: a random 3 or 4 of them (OBSERVED 4 and 3).
    /// The fixed boxes' kind is CANDIDATE (the client's test boxes and EventFunc test groups: _01 HP, _02 MP, _03 STA, _04 SPD).
    /// </summary>
    public sealed record OrbBox(string[] Orbs, double Radius, bool Random);

    static readonly string[] AllOrbs = ["Basic_RecoveryOrb_01", "Basic_RecoveryOrb_02", "Basic_RecoveryOrb_03", "Basic_RecoveryOrb_04"];

    static readonly string[] GarrisonOrbs =
    [
        "E_L1_Garrison_H1_FP_01", "Basic_RecoveryOrb_01", "Basic_RecoveryOrb_02", "Basic_RecoveryOrb_03",
        "E_L1_Garrison_H1_FP_01", "Basic_RecoveryOrb_01", "Basic_RecoveryOrb_02", "Basic_RecoveryOrb_03",
    ];

    /// <summary>The recovery orb boxes by EnvObjData id (radius: EventFunc SummonAroundTarget FuncValue2 300).</summary>
    public static readonly IReadOnlyDictionary<int, OrbBox> OrbBoxes = new Dictionary<int, OrbBox>
    {
        [1400603] = new(["Basic_RecoveryOrb_01"], 300, false),   // E_All_RecoveryOrbBox_01 (HP)
        [1400604] = new(["Basic_RecoveryOrb_02"], 300, false),   // E_All_RecoveryOrbBox_02 (MP)
        [1400605] = new(["Basic_RecoveryOrb_03"], 300, false),   // E_All_RecoveryOrbBox_03 (stamina)
        [1400606] = new(["Basic_RecoveryOrb_04"], 300, false),   // E_All_RecoveryOrbBox_04 (speed)
        [1401730] = new(["Basic_RecoveryOrb_04"], 300, false),   // E_PD_DDN_RecoveryOrbBox_04 (Dead Dramata Nest; _04 = speed)
        [1400607] = new(AllOrbs, 300, true),                     // E_All_RecoveryOrbBoxRandom_01 (OBSERVED)
        // Stronghold orb chests (live 2026-11-11: opened, no orbs). EventFunc group L1_Garrison_OrbBox (10270-10277): SummonAroundTarget
        // 110 / 300 of 2 x E_L1_Garrison_H1_FP_01 (flight time), Basic_RecoveryOrb_01 (HP), _02 (MP), _03 (stamina). The L2 and D1 chests
        // have no group of their own: they use the same orbs (CANDIDATE).
        [1200526] = new(GarrisonOrbs, 300, false),               // E_L1_Garrison_OrbBox_01
        [1201460] = new(GarrisonOrbs, 300, false),               // E_L2_Garrison_OrbBox_01 (CANDIDATE)
        [1300639] = new(GarrisonOrbs, 300, false),               // E_D1_Garrison_OrbBox_01 (CANDIDATE)
    };

    /// <summary>
    /// A recovery orb's abnormal and duration (EventFunc Event_EnvObj_*_Marble: HP 19000013, MP 19000015, STA 19000016 for 800 ms,
    /// SPD 19000017 for 10000 ms, OP 19000029 for 800 ms; OBSERVED on the player at each pickup) by the orb's number
    /// (Basic_RecoveryOrb_0N, E_FE_Common_RecoveryOrb_0N, E_Dungeon_D_0026_RecoveryOrb_0N_01).
    /// </summary>
    public static (int Abnormal, int DurationMs)? OrbEffect(int env)
    {
        const string marker = "RecoveryOrb_0";
        if (EnvUsages.Of(env)?.Name is not { } name || name.Contains("Box", StringComparison.OrdinalIgnoreCase)) return null;
        // A stronghold flight-time orb (E_L1_Garrison_H1_FP_01): EventFunc Event_EnvObj_FE_Common_FPRecovery, AddAbnormal 19000028 for 100 ms
        // (the only FP orb event in the client; its link to this orb is CANDIDATE).
        if (name.Contains("_FP_0", StringComparison.OrdinalIgnoreCase)) return (19000028, 100);
        var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + marker.Length >= name.Length) return null;
        return name[at + marker.Length] switch
        {
            '1' => (19000013, 800),
            '2' => (19000015, 800),
            '3' => (19000016, 800),
            '4' => (19000017, 10000),
            '5' => (19000029, 800),
            _ => null,
        };
    }

    /// <summary>Whether an object's use grants its RewardData row: a usable object (not a gather node, portal or cube) used up per use.</summary>
    public static bool HasEnvReward(WorldObject o) =>
        o.Gather is null && !o.Cube && o.Portal is null && o.Pool is null && o.Rift is null && LootTables.Embedded.HasLoot(o.Env)
        && EnvUsages.Of(o.Env) is { Kind: "Normal" or "MapEvent" or "Quest" } u && (u.Count >= 1 || u.RewardOnce);

    /// <summary>A reward or orb box that one use spends for the whole copy (InteractCount 1, not bGetRewardOnce): no 0x363b after it.</summary>
    static bool SpentForCopy(WorldObject o) =>
        (OrbBoxes.ContainsKey(o.Env) || HasEnvReward(o)) && EnvUsages.Of(o.Env) is { Count: 1, RewardOnce: false };

    static bool IsEnvBox(WorldObject o) => OrbBoxes.ContainsKey(o.Env) || HasEnvReward(o);

    // ---------- Who took what ----------

    static readonly ConditionalWeakTable<DungeonCopy, HashSet<string>> copyTaken = new();
    /// <summary>Open-world boxes taken: "map:index:who" -> until when (long.MaxValue: for good).</summary>
    static readonly ConcurrentDictionary<string, long> worldTaken = new();
    /// <summary>Boxes taken in an instance layer without a dungeon copy (cleared with the layer at the next map load).</summary>
    readonly HashSet<string> layerTaken = [];

    string Taker => CharacterId?.ToString() ?? $"key{playerKey}";

    string TakenKey(int index, WorldObject o) =>
        $"{worldMap}:{index}:{(EnvUsages.Of(o.Env)?.RewardOnce == true ? Taker : "*")}";

    bool EnvBoxTaken(int index, WorldObject o, long now)
    {
        // An Empyrean Trace is collected for good once its spawn achievement counts (GameSession.EmpyreanTraces.cs).
        if (TraceCollected(o)) return true;
        var key = TakenKey(index, o);
        if (instanceKey != 0)
            return copy is { } c ? copyTaken.TryGetValue(c, out var set) && set.Contains(key) : layerTaken.Contains(key);
        return worldTaken.TryGetValue(key, out var until) && until > now;
    }

    void TakeEnvBox(int index, WorldObject o, long now)
    {
        var key = TakenKey(index, o);
        if (instanceKey != 0)
        {
            if (copy is { } c) copyTaken.GetOrCreateValue(c).Add(key);
            else layerTaken.Add(key);
            return;
        }
        worldTaken[key] = EnvUsages.Of(o.Env)?.RewardOnce == true ? long.MaxValue : now + OpenWorldRespawnMs;
    }

    /// <summary>A box this player may not open (again): the refusal, else null.</summary>
    byte[]? EnvBoxRefusal(int index, WorldObject o, int key, long now)
    {
        if (!IsEnvBox(o) || !EnvBoxTaken(index, o, now)) return null;
        Note($"InteractEnvObj refused on map {worldMap}: {o.Name} (key {key}) was already opened");
        return EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_CanInteractOnlyOnce, o.Env);
    }

    /// <summary>
    /// A completed use of a box (after the reply): taken, the orbs it summons, its reward (0xe33a, then the items stored and sent as
    /// 0x561b with increase reason 49).
    /// </summary>
    async Task<List<byte[]>> EnvBoxUsedAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        if (!IsEnvBox(o)) return [];
        var replies = new List<byte[]>();
        TakeEnvBox(index, o, now);
        if (OrbBoxes.TryGetValue(o.Env, out var box))
        {
            var orbs = box.Orbs.Select(EnvUsages.IdOf).Where(id => id > 0).ToList();
            if (box.Random)
            {
                var count = Math.Min(orbs.Count, rng.Next(3, 5));
                orbs = orbs.OrderBy(_ => rng.Next()).Take(count).OrderBy(id => id).ToList();
            }
            replies.AddRange(SummonEnvObjects(orbs, o.At, box.Radius, key, now, broadcast: true));
        }
        if (HasEnvReward(o))
        {
            var items = LootTables.Embedded.Roll(o.Env, profileRace, rng);
            if (items.Count > 0)
            {
                replies.Add(EnvBoxPackets.EnvObjReward(key, o.At, items));
                if (Store is { } store && CharacterId is { } character)
                {
                    var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
                    if (granted.Count > 0) replies.AddRange(await InventoryChangesAsync(ct, EnvBoxPackets.IncreaseEnvObjReward, 0));
                }
                Note($"box reward on map {worldMap}: {o.Name} gave {string.Join(", ", items.Select(i => $"{i.ItemId} x{i.Count}"))}");
            }
        }
        return replies;
    }

    // ---------- Summoned objects (recovery orbs) ----------

    readonly Dictionary<int, SummonedEnvObj> ownEnvSummons = [];

    /// <summary>The map's summoned objects (the shared map instance's, else this session's own).</summary>
    Dictionary<int, SummonedEnvObj> EnvSummons => worldPlayer?.Map?.Summoned ?? ownEnvSummons;

    /// <summary>Checks only: the summoned objects of the player's map.</summary>
    public IReadOnlyCollection<SummonedEnvObj> EnvSummonsForChecks => EnvSummons.Values;

    /// <summary>The others on the player's map (a copy: the party).</summary>
    void ToOthersOnMap(IReadOnlyList<byte[]> packets)
    {
        if (packets.Count == 0 || worldPlayer?.Map is not { } map) return;
        foreach (var other in map.Players.Values)
            if (other != worldPlayer) other.Send(packets);
    }

    /// <summary>
    /// Summons objects (EnvObjData ids) around a point within a radius (random spot on the ground, CANDIDATE: the official ones lay
    /// 117-247 from the box with radius 300), each living its LifeTime. The appear packets for this player; with broadcast, also
    /// sent to the others on the map (a map event's go out with its other messages).
    /// </summary>
    List<byte[]> SummonEnvObjects(IReadOnlyList<int> envs, Vector3d centre, double radius, int originKey, long now, bool broadcast)
    {
        var spawned = new List<byte[]>();
        var store = EnvSummons;
        var map = worldPlayer?.Map;
        foreach (var env in envs)
        {
            var angle = rng.NextDouble() * 2 * Math.PI;
            var distance = radius * Math.Sqrt(rng.NextDouble());
            var at = centre with { X = centre.X + Math.Cos(angle) * distance, Y = centre.Y + Math.Sin(angle) * distance };
            var life = EnvUsages.Of(env)?.LifetimeMs ?? 0;
            var key = NextEnvSummonKey();
            store[key] = new SummonedEnvObj(key, env, at, life > 0 ? now + life : long.MaxValue);
            spawned.Add(EnvBoxPackets.SummonedEnvObjSpawn(key, env, at, originKey, now, OrbRelationship));
            if (life > 0) Schedule(now + life, _ => ExpireEnvSummon(store, map, key));
        }
        if (broadcast) ToOthersOnMap(spawned);
        return spawned;
    }

    /// <summary>An untaken summoned object's life ran out: gone for everyone on its map (0x3635 state 1 reason 1, CANDIDATE).</summary>
    List<byte[]> ExpireEnvSummon(Dictionary<int, SummonedEnvObj> store, MapInstance? map, int key)
    {
        if (!store.Remove(key)) return [];
        var gone = new List<byte[]> { GamePackets.EnvObjDespawn(key) };
        if (map is not null)
            foreach (var other in map.Players.Values)
                if (other != worldPlayer) other.Send(gone);
        return worldPlayer?.Map == map ? gone : [];
    }

    /// <summary>The summoned object an InteractEnvObj_RQ names, if any.</summary>
    int? EnvSummonKeyIn(byte[] plain)
    {
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k >= FirstEnvSummonKey && k < FirstEnvSummonKey + EnvSummonKeySpan) return k;
        return null;
    }

    /// <summary>
    /// Walking into a recovery orb (OBSERVED order, rec 105448 #53751-#53783 and #53838-#53872): started, completed, finish, 0x8d05
    /// die, the reply, the orb's abnormal (and the stats it changes), 0x3635 {key, 4, 3}. Gone for the others too.
    /// </summary>
    List<byte[]> PickUpEnvSummon(int key, long now)
    {
        if (!EnvSummons.TryGetValue(key, out var orb))
        {
            Note($"InteractEnvObj refused on map {worldMap}: summoned key {key} is gone");
            return [GamePackets.InteractEnvObjReply(key, 1)];
        }
        var reach = (EnvUsages.Of(orb.Env)?.Range ?? 40) + OrbReachSlack;
        if (position is { } p && Math.Sqrt(Math.Pow(p.X - orb.At.X, 2) + Math.Pow(p.Y - orb.At.Y, 2)) > reach)
            return [EnvBoxPackets.InteractEnvObjRefused(key, EResult.EnvObj_Fail_TooFar, orb.Env)];
        EnvSummons.Remove(key);
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjStarted(key, playerKey), GamePackets.InteractEnvObjCompleted(key, playerKey),
            GamePackets.InteractEnvObjFinishState(key), EnvBoxPackets.EnvObjDie(key), GamePackets.InteractEnvObjReply(key),
        };
        replies.AddRange(ApplyWorldBuffs(WorldBuffs.Overlap(orb.Env), now));   // the orb's abnormal (OrbEffect) and the stats it changes
        replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        ToOthersOnMap([EnvBoxPackets.EnvObjDie(key), GamePackets.EnvObjDespawn(key, 4, 3)]);
        return replies;
    }
}
