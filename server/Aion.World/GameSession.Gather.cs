using Aion.Protocol.Native;

namespace Aion.World;

// Gathering. Gather nodes are EnvObjs (EnvObjData Usage GatherSource) placed by MapData, spawned with the quest objects
// (0x3634, tools/world_objects.py "gather": material, count, bonus item, bonus count, mastery, gauge, attempts). The client starts one
// with InteractEnvObj_RQ 0x3636 (the timed interaction already served) or GatherSourceStart_RQ 0x8d30 (0x8d run,
// catalogue index + 0x89d1; which one is not observed). On completion: the material (and, OURS, the bonus item one
// time in five), GatherSourceCompleted_NT 0x8d33, and the node is used up for this player for GatherRespawnMs (OURS).
// Gather mastery (migration 029): a node needs its GatherSource MasteryLevel; each gather pays an eighth of the level's
// GatherMastery need (OURS) as GatherExpUpdate_NT 0x8d35 {exp i64, gained i64}, a level-up GatherLevelUpdate_NT 0x8d36
// {level i32, exp i64, skill points i32, specialized points i32} (usmap fields, CANDIDATE). The gather skill tree
// (MyGatherSkillList_NT, points) is not modelled: the points stay 0.
public sealed partial class GameSession
{
    public const ushort GatherStartRq = 0x8d30, GatherStartRs = 0x8d31, GatherStopRq = 0x8d32, GatherCompletedNt = 0x8d33, GatherExpUpdateNt = 0x8d35,
        GatherLevelUpdateNt = 0x8d36;
    public const long GatherRespawnMs = 60_000;
    const byte IncreaseGatherSource = 41;   // EIncreaseReason kGatherSource
    /// <summary>Gather nodes used up by this player: object index -> when it is back.</summary>
    readonly Dictionary<int, long> gathered = [];
    public Func<int> GatherBonusRoll { get; set; } = () => Random.Shared.Next(5);
    /// <summary>The turn rolls of the gathering mini-game (0..9999).</summary>
    public Func<int> GatherRoll { get; set; } = () => Random.Shared.Next(10_000);
    int gatherLevel = 1;
    long gatherExp;
    /// <summary>Attempts left on a node for this player (object index -> count), while some were used.</summary>
    readonly Dictionary<int, int> gatherAttempts = [];

    public int GatherAttemptsLeft(int index) => gatherAttempts.GetValueOrDefault(index, -1);

    /// <summary>One turn of the mini-game: EGatherTurnResult and the two gauges (percent) before and after it.</summary>
    public readonly record struct GatherTurn(byte Result, int SuccessBefore, int SuccessAfter, int FailBefore, int FailAfter);

    const byte TurnEpicCritical = 1, TurnCritical = 2, TurnBasic = 3, TurnSmall = 4, TurnFailBasic = 5, TurnFailSmall = 6;
    /// <summary>A basic success fills a Beginner node (gauge 360) in one turn, as the official turn did (0 -> 100).</summary>
    const int GatherBasicGain = 360;

    /// <summary>
    /// The turns of one gathering attempt (OURS, after the published rules: a blue success gauge against an orange failure
    /// gauge, three attempts per node; the official turn rules are unrecorded). Each turn succeeds with 65% plus 3 points
    /// per mastery level over the node (35..95%). A success adds a basic 360 gauge units (critical x1.5, epic x2, small
    /// x0.5) towards the node's GatherGauge; a failure adds a third of it (small: a sixth) to the failure gauge. The
    /// attempt ends when either gauge is full, after at most twelve turns.
    /// </summary>
    List<GatherTurn> GatherTurns(int target, int nodeMastery)
    {
        var turns = new List<GatherTurn>();
        var chance = Math.Clamp(6500 + 300 * (gatherLevel - nodeMastery), 3500, 9500);
        int success = 0, fail = 0;
        int Percent(int units) => Math.Min(100, (int)Math.Round(100.0 * units / Math.Max(1, target)));
        while (turns.Count < 12 && success < target && fail < target)
        {
            int before = success, failBefore = fail;
            byte result;
            if (GatherRoll() < chance)
            {
                var kind = GatherRoll();
                result = kind < 200 ? TurnEpicCritical : kind < 1200 ? TurnCritical : kind < 2700 ? TurnSmall : TurnBasic;
                success += result switch { TurnEpicCritical => GatherBasicGain * 2, TurnCritical => GatherBasicGain * 3 / 2, TurnSmall => GatherBasicGain / 2, _ => GatherBasicGain };
            }
            else
            {
                result = GatherRoll() < 3000 ? TurnFailSmall : TurnFailBasic;
                fail += result == TurnFailSmall ? Math.Max(1, target / 6) : Math.Max(1, target / 3);
            }
            turns.Add(new GatherTurn(result, Percent(before), Percent(success), Percent(failBefore), Percent(fail)));
        }
        return turns;
    }

    public (int Level, long Exp) GatherMastery => (gatherLevel, gatherExp);

    byte[] GatherLevelUpdate() => PacketWriter.Opcode(GatherLevelUpdateNt).I32(gatherLevel).I64(gatherExp).I32(0).I32(0).ToArray();

    /// <summary>Mastery exp for a gather (OURS): the level's need over eight; the packets, with the level-up.</summary>
    async Task<List<byte[]>> GatherMasteryExpAsync(CancellationToken ct)
    {
        var need = Crafts.GatherMasteryNeed(gatherLevel);
        if (need <= 0) return [];
        var gained = Math.Max(1, need / 8);
        gatherExp += gained;
        var replies = new List<byte[]> { PacketWriter.Opcode(GatherExpUpdateNt).I64(gatherExp).I64(gained).ToArray() };
        var leveled = false;
        while (need > 0 && gatherExp >= need && gatherLevel < CraftTables.MaxMastery)
        {
            gatherExp -= need;
            gatherLevel++;
            leveled = true;
            need = Crafts.GatherMasteryNeed(gatherLevel);
        }
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { await store.SaveMasteryAsync(character, "gather", 0, gatherLevel, gatherExp, ct); return []; });
        if (leveled)
        {
            replies.Add(GatherLevelUpdate());
            replies.AddRange(await QuestMasteryAutoAcquireAsync(ct));
        }
        return replies;
    }

    /// <summary>
    /// Patch of Oct 28 2026: gathering needs character level 45 and the server pays 1,000 successful gathers per game day.
    /// Stored characters only (development logins keep gathering for tests); <paramref name="take"/> counts the gather against the day.
    /// </summary>
    bool GatherRefused(long now, bool take)
    {
        if (Store is null) return false;
        if (Patch20261028.GatherBlock(level) is not null) return true;
        if (options.World?.GatherLimiter is not { } limiter) return false;
        return take ? !limiter.TryTake(now) : !limiter.HasRoom(now);
    }

    /// <summary>ESlotRestrictIconType sent as 0x3639 _reason when a gather is refused (OBSERVED), or null when allowed.</summary>
    int? GatherFailReason(WorldObject node)
    {
        if (Store is null) return null;
        if (Patch20261028.GatherBlock(level) is not null) return GamePackets.EnvObjGatherFailReason.LackLevel;
        if (node.Gather is { } g && g.Length >= 5 && g[4] > gatherLevel) return GamePackets.EnvObjGatherFailReason.LackMasteryLevel;
        return null;
    }

    List<byte[]> GatherRefusedPackets(int objectKey, int reason) =>
        [GamePackets.InteractEnvObjInterrupted(objectKey, reason, playerKey), GamePackets.InteractEnvObjAvailableState(objectKey)];

    bool Gathered(int index, long now)
    {
        if (!gathered.TryGetValue(index, out var until)) return false;
        if (now < until) return true;
        gathered.Remove(index);
        return false;
    }

    /// <summary>
    /// 0x8d30 GatherSourceStart_RQ {target key}: the attempt's turns are rolled at once and sent in 0x8d31 (GatherTurnResult
    /// {EGatherTurnResult, no extra discovery, no skill effects, success gauge before/after, fail gauge before/after}; an
    /// empty turn list left the result panel waiting). A won attempt starts the node's timed interaction (completion as for
    /// 0x3636: the material, 0x8d33, the node used up); a lost one costs one of the node's attempts (0x8d33 result 1, the node
    /// available again) and the last loss uses the node up.
    /// </summary>
    async Task<List<byte[]>> GatherStartAsync(byte[] plain, long now, CancellationToken ct)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        var objects = WorldObjects.ObjectsOn(worldMap);
        var index = (int)key - WorldObjects.FirstKey;
        byte[] Reply(int result, int attempts, bool won, IReadOnlyList<GatherTurn> turns)
        {
            var w = PacketWriter.Opcode(GatherStartRs).U16(result).Varint(key).I32(attempts).Bool(won).Bool(false).Varint(0)
                .Bool(false).Varint(0).I32(1).Varint(turns.Count);
            foreach (var t in turns) w.U8(t.Result).Bool(false).Varint(0).I32(t.SuccessBefore).I32(t.SuccessAfter).I32(t.FailBefore).I32(t.FailAfter);
            return w.Bool(false).ToArray();
        }
        byte[] GatherCompletedFail(long[] gatherNode) =>
            PacketWriter.Opcode(GatherCompletedNt).U16(1).Varint(key).I32((int)gatherNode[0]).I32(0).I32(0).I32(0).I32(0).ToArray();

        List<byte[]> GatherStartAborted(long[] gatherNode)
        {
            var replies = new List<byte[]> { Reply(1, 0, false, []) };
            replies.Add(GatherCompletedFail(gatherNode));
            return replies;
        }

        if (index < 0 || index >= objects.Count || objects[index].Gather is not { } node)
            return [Reply(1, 0, false, [])];
        if (Gathered(index, now)) return GatherStartAborted(node);
        var keyInt = (int)key;
        if (GatherFailReason(objects[index]) is { } fail)
            return [Reply(1, 0, false, []), ..GatherRefusedPackets(keyInt, fail)];
        if (GatherRefused(now, take: false)) return GatherStartAborted(node);
        // The node's GatherSource MasteryLevel (the fifth gather value) gates it.
        var target = node.Length >= 6 && node[5] > 0 ? (int)node[5] : GatherBasicGain;
        var attempts = gatherAttempts.TryGetValue(index, out var left) ? left : node.Length >= 7 && node[6] > 0 ? (int)node[6] : 3;
        var turns = GatherTurns(target, node.Length >= 5 ? (int)node[4] : 1);
        var won = turns.Count > 0 && turns[^1].SuccessAfter >= 100;
        if (won)
        {
            var replies = await UseObjectAsync(new PacketWriter().U16(0x3636).Varint(key).ToArray(), now, ct);
            var refused = replies.Any(b => b[0] == 0x37 && b[1] == 0x36 && b.Length > 2 && b[2] != 0);
            if (!refused) gatherAttempts.Remove(index);
            return [Reply(refused ? 1 : 0, attempts, !refused, turns), .. replies];
        }
        attempts--;
        var lost = new List<byte[]> { Reply(0, attempts, false, turns), PacketWriter.Opcode(GatherCompletedNt).U16(1).Varint(key).I32((int)node[0]).I32(0).I32(0).I32(0).I32(0).ToArray() };
        if (attempts <= 0)
        {
            gatherAttempts.Remove(index);
            gathered[index] = now + GatherRespawnMs;
            if (shownObjects.Remove(index)) lost.Add(GamePackets.EnvObjDespawn((int)key));
        }
        else
        {
            gatherAttempts[index] = attempts;
            lost.Add(GamePackets.InteractEnvObjAvailableState((int)key));
        }
        return lost;
    }

    /// <summary>0x8d32 GatherSourceStop_RQ {target key}: the gather in progress is cancelled; the node is available again (0x363b).</summary>
    List<byte[]> GatherStop(byte[] plain)
    {
        uint key;
        try { key = new PacketReader(plain) { Offset = 2 }.Varint(); } catch (ProtocolException) { return []; }
        if (pendingUse is { } use && use.Key == (int)key) pendingUse = null;
        return [GamePackets.InteractEnvObjAvailableState((int)key)];
    }

    /// <summary>A gather node's interaction completed: the material, 0x8d33, the node used up (0x3635).</summary>
    async Task<List<byte[]>> CompleteGatherAsync(int index, WorldObject node, long now, CancellationToken ct)
    {
        var g = node.Gather!;
        int key = WorldObjects.FirstKey + index, item = (int)g[0], count = (int)g[1];
        if (GatherRefused(now, take: true))
            return [PacketWriter.Opcode(GatherCompletedNt).U16(1).Varint(key).I32(item).I32(0).I32(0).I32(0).I32(0).ToArray()];
        var bonus = g.Length >= 4 && g[2] > 0 && GatherBonusRoll() == 0 ? ((int)g[2], (int)g[3]) : (0, 0);
        var rewards = new List<(int, long)> { (item, count) };
        if (bonus.Item1 > 0) rewards.Add((bonus.Item1, bonus.Item2));
        var replies = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, rewards, ct) ? [[]] : []);
            if (granted.Count == 0)
                return [PacketWriter.Opcode(GatherCompletedNt).U16(1).Varint(key).I32(item).I32(0).I32(0).I32(0).I32(0).ToArray()];
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseGatherSource, 0));
        }
        replies.Insert(0, PacketWriter.Opcode(GatherCompletedNt).U16(0).Varint(key).I32(item).I32(count).I32(0).I32(bonus.Item1).I32(bonus.Item2).ToArray());
        gathered[index] = now + GatherRespawnMs;
        gatherAttempts.Remove(index);
        if (shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(key));
        replies.AddRange(await GatherMasteryExpAsync(ct));
        replies.AddRange(AchieveGather(gatherLevel));
        return replies;
    }
}
