using Aion.Protocol.Native;

namespace Aion.World;

// Expeditions (Dungeon.dat type Party) beyond the scripts: one DungeonCopy per copy (shared by the party through the
// MapInstance), its host (the session running the scripts; handed over when that player leaves), the time limit
// (ProcessStateHoldingTime), the entry rules (MapEntrance.dat: level, equipment item level, member cap), the reward
// cube (RewardOdEnergyCube.dat: the final boss's RewardCommon at the cube object, mid-boss rewards on their kill) and
// the dungeon state messages. Opcodes: the 0x54ca run (index + 0x54ca, anchored by ItemUse 0x5612, TitleList 0x5679,
// OpenSubzoneGroupIDs 0x56ac and AddSubzoneGroupID 0x56ad in the official recording): OdEnergyCubeRewardStart_NT 0x56cc,
// OdEnergyCubeReward_RQ/RS 0x56cd/0x56ce. The dungeon state family is OBSERVED at 0x6100-0x610b (catalogue 573-584 at
// 0x5ec3 + index): DungeonState_NT 0x6100, DungeonCompleteResult_NT 0x6101, DungeonClearKillNpc_NT 0x6108, DungeonDeathLimit_NT 0x6109.
// Their bodies are the usmap fields (CANDIDATE: none observed). Not modelled: entry tickets, the weekly currency
// limit, the cube's ceiling, DungeonCoolTimes (every party dungeon's Cooltime is 0), DeathLimit (0 everywhere).

/// <summary>The shared state of one dungeon copy.</summary>
public sealed class DungeonCopy(GameSession host, int dungeonId, long now)
{
    public GameSession Host { get; set; } = host;
    public int DungeonId { get; } = dungeonId;
    public long StartedMs { get; } = now;
    /// <summary>When the run fails for time (0: no limit).</summary>
    public long DeadlineMs { get; set; }
    public bool Cleared { get; set; }
    public bool Failed { get; set; }
    /// <summary>"Prepare" while a field event's ready time runs (its scripts wait for DungeonState Process), else "Process".</summary>
    public string Phase { get; set; } = "Process";
    public long ProcessAtMs { get; set; }
    /// <summary>Reward-cube bosses killed in this copy (NpcData ids).</summary>
    public HashSet<int> CubeBossesKilled { get; } = [];
    /// <summary>Player deaths in this copy (the death-limit achievements; 0x6109 at entry).</summary>
    public int Deaths { get; set; }
    /// <summary>When the clear happened and when its Complete state (Dungeon.dat hold) ends; End (4) was sent.</summary>
    public long ClearedMs { get; set; }
    public long CompleteEndsMs { get; set; }
    public bool EndSent { get; set; }
    /// <summary>Revives each character used in this run (character id, or player key for a development login), for
    /// Dungeon.dat RebirthLimitCount.</summary>
    public Dictionary<string, int> Revives { get; } = [];
    /// <summary>Treasure boxes opened in this run: (character key as for <see cref="Revives"/>, object index).</summary>
    public HashSet<(string Who, int Index)> ChestsOpened { get; } = [];
    /// <summary>The final boss's cube reward each player took (player key → items), once per run.</summary>
    public Dictionary<int, List<(int ItemId, long Count)>> CubeTaken { get; } = [];
}

public sealed partial class GameSession
{
    // OBSERVED (HQ001 layer, capture 20261013 #9687-9689, #14161-14167): catalogue 573-584 live at 0x5ec3 + index, not in the 0x54ca run.
    public const ushort DungeonStateNt = 0x6100, DungeonCompleteResultNt = 0x6101, DungeonClearKillNpcNt = 0x6108, DungeonDeathLimitNt = 0x6109;
    /// <summary>
    /// The reward cube: OdEnergyCubeRewardStart_NT 0x5713, OdEnergyCubeReward_RQ 0x5714, OdEnergyCubeReward_RS 0x5715,
    /// PartyDungeonRewardMiddleBossKill_NT 0x572e. OBSERVED 0x5713/0x5715/0x572e in the community recordings 2026-11-04 to 06
    /// (reports/dungeons.md 4: six clears, twelve mid-boss kills; none of the old 0x56cc-0x56ce, 0x47 lower); 0x5714 CANDIDATE
    /// (between them by position).
    /// </summary>
    public const ushort CubeRewardStartNt = 0x5713, CubeRewardRq = 0x5714, CubeRewardRs = 0x5715, MiddleBossKillNt = 0x572e;
    public const byte DungeonStateProcess = 2, DungeonStateComplete = 3, DungeonStateEnd = 4;
    /// <summary>After the time limit the copy empties this long after the fail message (OURS).</summary>
    public const long DungeonFailExitMs = 5_000;
    /// <summary>How long the End state (4) runs after the Complete state's hold (OBSERVED 600 s: layer 108, sealed 310027).</summary>
    public const long DungeonEndStateMs = 600_000;

    DungeonCopy? copy;
    readonly List<List<(int ItemId, long Count)>> pendingRewards = [];

    public DungeonCopy? Copy => copy;
    Random Rng => rng;

    /// <summary>At a map load: the copy's shared state (the first one in hosts it; a solo copy keeps its own across reloads).</summary>
    void JoinCopy(MapInstance? sharedMap, int mapId, long now)
    {
        if (instanceKey == 0 || MapEventData.DungeonOfMap(mapId) is not { } room) { copy = null; return; }
        var id = MapEventData.DungeonIdOfMap(mapId);
        DungeonCopy Fresh()
        {
            var limit = room.Limit > 0 ? room.Limit : room.Progress;
            var fresh = new DungeonCopy(this, id, now) { DeadlineMs = limit > 0 && Timed(room) ? now + (room.Ready + limit) * 1000L : 0 };
            if (room.Ready > 0) { fresh.Phase = "Prepare"; fresh.ProcessAtMs = now + room.Ready * 1000L; }
            return fresh;
        }
        if (sharedMap is not null) copy = sharedMap.Dungeon ??= Fresh();
        else if (copy is null || copy.DungeonId != id) copy = Fresh();
    }

    /// <summary>The dungeon types with a run timer and the dungeon state messages (Dungeon.dat ProcessStateHoldingTime).</summary>
    static bool Timed(DungeonInfo room) => room.Type is "Party" or "Seal" or "Awaken" or "Daily" or "BossChallenge" or "Raid" or "FieldEvent";
    public const byte DungeonStatePrepare = 1;

    string IMapEventHost.DungeonStateName => copy?.Phase ?? "Process";

    /// <summary>The host's tick: a field event's ready time ends, the event phase begins and its scripts start.</summary>
    List<byte[]> DungeonPhaseTick(long now)
    {
        if (copy is not { Phase: "Prepare" } c || c.Host != this || now < c.ProcessAtMs) return [];
        c.Phase = "Process";
        var replies = new List<byte[]> { DungeonState(c.DungeonId, DungeonStateProcess, now, c.DeadlineMs) };
        replies.AddRange(StartEligibleEvents(now));
        return replies;
    }

    /// <summary>
    /// MapEntrance.dat: the character's level and equipment item level, and the quest step the entry needs (one of
    /// QuestStepIdList active, or that quest done); staff and development logins (no stored character) pass.
    /// </summary>
    bool MeetsEntry(DungeonInfo room, out string why)
    {
        why = "";
        if (!Timed(room) || Store is null) return true;
        if (room.MinLevel > 0 && level < room.MinLevel) { why = $"level {level} < {room.MinLevel}"; return false; }
        if (room.MaxLevel > 0 && level > room.MaxLevel) { why = $"level {level} > {room.MaxLevel}"; return false; }
        if (room.ItemLevel > 0 && EquipItemLevel() < room.ItemLevel) { why = $"item level {EquipItemLevel()} < {room.ItemLevel}"; return false; }
        // GameSession.EntryGate.cs: the gate quest at its step or finished, or a quest step that needs this room (A2Core PR #4).
        if (room.QuestGate is { Length: > 0 } gates && !QuestGateOpen(room))
        { why = $"quest step {string.Join('/', gates.Select(g => g[0].GetString()))} not reached"; return false; }
        return true;
    }

    /// <summary>The copy a party member would join is full (MapEntrance MaxMember).</summary>
    static bool CopyFull(MapInstance inside, DungeonInfo room) => room.MaxMember > 0 && inside.Players.Count >= room.MaxMember;

    /// <summary>DungeonState_NT 0x6100 {dungeon i32, EDungeonState u8, start i64, end i64}: 23 bytes with the opcode (OBSERVED:
    /// all 33 instances in the community recordings of 2026-11-04 to 06 and the 4 in ours; no trailing byte).</summary>
    internal static byte[] DungeonState(int dungeonId, byte state, long startMs, long endMs) =>
        PacketWriter.Opcode(DungeonStateNt).I32(dungeonId).U8(state).I64(startMs).I64(endMs).ToArray();

    /// <summary>
    /// DungeonCompleteResult_NT 0x6101: u8 0, dungeon, succeeded, then the clear rewards {item i32, count i64} (OBSERVED
    /// `00 60000000 01 00` without; with them 2026-11-06 234912 #44952 for 310027: its reward_light by item id, ascending).
    /// </summary>
    internal static byte[] DungeonCompleteResult(int dungeonId, bool succeeded, IReadOnlyList<(int ItemId, long Count)>? rewards = null)
    {
        var w = PacketWriter.Opcode(DungeonCompleteResultNt).U8(0).I32(dungeonId).U8((byte)(succeeded ? 1 : 0)).Varint((uint)(rewards?.Count ?? 0));
        foreach (var (item, count) in (rewards ?? []).OrderBy(r => r.ItemId)) w.I32(item).I64(count);
        return w.ToArray();
    }

    /// <summary>
    /// After the running state at entry (OBSERVED): DungeonDeathLimit_NT 0x6109 with the copy's deaths (`00000000` at a first
    /// entry, `05000000` after a wipe: 2026-11-06 090856 #104160), DungeonClearKillNpc_NT 0x6108 `00`.
    /// </summary>
    internal static IEnumerable<byte[]> DungeonLimitsAtEntry(int deaths = 0) =>
        [PacketWriter.Opcode(DungeonDeathLimitNt).I32(deaths).ToArray(), PacketWriter.Opcode(DungeonClearKillNpcNt).U8(0).ToArray()];

    /// <summary>The host's tick: the Complete state's hold ended, End (4) runs 600 s from there (OBSERVED 234912 #45201).</summary>
    void DungeonEndTick(long now)
    {
        if (copy is not { Cleared: true, EndSent: false } c || c.Host != this || now < c.CompleteEndsMs) return;
        c.EndSent = true;
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, c.CompleteEndsMs, c.CompleteEndsMs + DungeonEndStateMs));
    }

    /// <summary>At the world entry of an expedition: its state (running until the deadline, or complete until the hold ends).</summary>
    List<byte[]> DungeonStateAtEntry(long now)
    {
        if (copy is not { } c || MapEventData.DungeonOfMap(worldMap) is not { } room) return [];
        // An instance layer (the story): running since the entry, no deadline (OBSERVED at the HQ001 entry).
        if (!Timed(room)) return c.Cleared ? [] : [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, 0), .. DungeonLimitsAtEntry(c.Deaths)];
        if (c.Failed) return [];
        if (c.Phase == "Prepare") return [DungeonState(c.DungeonId, DungeonStatePrepare, c.StartedMs, c.ProcessAtMs)];
        // After the clear: Complete (3) for the hold, then End (4) for 600 s (OBSERVED 2026-11-06 234912 #44953, #45201).
        if (c.Cleared) return [c.EndSent ? DungeonState(c.DungeonId, DungeonStateEnd, c.CompleteEndsMs, c.CompleteEndsMs + DungeonEndStateMs)
            : DungeonState(c.DungeonId, DungeonStateComplete, c.ClearedMs, c.CompleteEndsMs)];
        return [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, c.DeadlineMs), .. DungeonLimitsAtEntry(c.Deaths)];
    }

    /// <summary>The host's tick: past the deadline the run fails and the copy empties.</summary>
    void DungeonTimeLimit(long now)
    {
        if (copy is not { } c || c.Host != this || c.DeadlineMs == 0 || c.Cleared || c.Failed || now < c.DeadlineMs) return;
        c.Failed = true;
        Note($"dungeon {c.DungeonId}: the time limit passed");
        mapEventOut.Add(DungeonState(c.DungeonId, DungeonStateEnd, c.StartedMs, now));
        mapEventOut.Add(DungeonCompleteResult(c.DungeonId, false));
        foreach (var session in CopyMembers().Prepend(this)) session.instanceExitAt ??= now + DungeonFailExitMs;
    }

    /// <summary>
    /// The host leaves its copy with others still inside: the scripts and their state move to one of them, who hosts
    /// from now on (its own tick drives them). Nothing to do when this player does not host.
    /// </summary>
    void HandOverCopy()
    {
        if (copy is not { } c || c.Host != this) return;
        var next = CopyMembers().FirstOrDefault(s => !s.reloading) ?? CopyMembers().FirstOrDefault();
        if (next is null) return;
        foreach (var run in mapEventRuns) run.Host = next;
        next.mapEventRuns.AddRange(mapEventRuns);
        next.dormantSpawners.UnionWith(dormantSpawners);
        next.startedSpawners.UnionWith(startedSpawners);
        next.removedObjects.UnionWith(removedObjects);
        next.pendingSpawns.AddRange(pendingSpawns);
        foreach (var (target, buffs) in npcBuffs) next.npcBuffs[target] = buffs;
        next.pendingExit ??= pendingExit;
        next.mapEventsMap = worldMap;
        next.mapEventNow = Math.Max(next.mapEventNow, mapEventNow);
        next.mapEventOut.AddRange(mapEventOut);
        mapEventRuns.Clear();
        dormantSpawners.Clear();
        startedSpawners.Clear();
        removedObjects.Clear();
        pendingSpawns.Clear();
        npcBuffs.Clear();
        mapEventOut.Clear();
        pendingExit = null;
        c.Host = next;
    }

    // ---------- The reward cube ----------

    /// <summary>
    /// A kill in the host's copy (OBSERVED, reports/dungeons.md 5): a mid boss sends 0x572e {npc id} and pays nothing then
    /// (2026-11-05 204306 #11315, #20965); the final boss arms the cube with 0x5713 listing the killed mid bosses, never the
    /// final one (084658 #61800 [2310104, 2310108]).
    /// </summary>
    void CubeKilled(Target target, long now)
    {
        if (copy is not { } c || c.Host != this || MapEventData.DungeonOfMap(worldMap) is not { Cube: { } cube } room || !c.CubeBossesKilled.Add(target.NpcId)) return;
        if (!cube.ContainsKey(target.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture))) return;
        if (target.NpcId != room.CubeBoss)
        {
            mapEventOut.Add(PacketWriter.Opcode(MiddleBossKillNt).I32(target.NpcId).ToArray());
            return;
        }
        var mids = KilledMidBosses(c, room);
        var start = PacketWriter.Opcode(CubeRewardStartNt).Varint((uint)mids.Count);
        foreach (var id in mids) start.I32(id);
        mapEventOut.Add(start.ToArray());
    }

    /// <summary>The mid bosses (cube tables other than the final boss's) killed in this copy, by id.</summary>
    static List<int> KilledMidBosses(DungeonCopy c, DungeonInfo room) =>
        (room.Cube ?? []).Keys.Select(k => int.Parse(k, System.Globalization.CultureInfo.InvariantCulture))
            .Where(id => id != room.CubeBoss && c.CubeBossesKilled.Contains(id)).Order().ToList();

    /// <summary>Each table entry is an independent chance of 1e8 with a count range (as LootTables).</summary>
    static List<(int ItemId, long Count)> RollRewards(long[][] table, Random rng)
    {
        var items = new List<(int, long)>();
        foreach (var entry in table)
        {
            if (entry.Length < 4 || rng.NextInt64(100_000_000) >= entry[1]) continue;
            items.Add(((int)entry[0], entry[2] + rng.NextInt64(entry[3] - entry[2] + 1)));
        }
        return items;
    }

    async Task<List<byte[]>> GrantPendingRewardsAsync(CancellationToken ct)
    {
        var batches = pendingRewards.ToList();
        pendingRewards.Clear();
        var items = batches.SelectMany(b => b).ToList();
        if (items.Count == 0) return [];
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count == 0) return [];
            return await InventoryChangesAsync(ct, IncreaseDropItemGet, 0);
        }
        return [];
    }

    /// <summary>
    /// The cube reward: on the cube object's use, or on OdEnergyCubeReward_RQ {reward count u8}; once per player per run
    /// (RewardOdEnergyCube ConsumeMaxCount 1; the 20 common tickets it consumes are not modelled). It pays the final boss's
    /// table and every killed mid boss's table in one, then OdEnergyCubeReward_RS {result, count, [{item, count}]}, after
    /// the object use as well (OBSERVED 2026-11-06 084658 #62929: KraoCave_Easy 3 x 25000 kina; 090856 #142335-#142341). The
    /// same items again when it already paid; result 1 when the boss is still alive or there is no cube here.
    /// </summary>
    async Task<List<byte[]>> CubeRewardAsync(byte[]? plain, long now, CancellationToken ct)
    {
        var c = copy;
        var room = MapEventData.DungeonOfMap(worldMap);
        var table = room?.Cube is { } cube && room.CubeBoss != 0 && c is not null && c.CubeBossesKilled.Contains(room.CubeBoss)
            ? cube.GetValueOrDefault(room.CubeBoss.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
        if (table is null || c is null)
            return plain is null ? [] : [PacketWriter.Opcode(CubeRewardRs).U16(1).U8(0).Varint(0).ToArray()];
        if (!c.CubeTaken.TryGetValue(playerKey, out var items))
        {
            if (!PayCubeTicket(room!, now)) return plain is null ? [] : [PacketWriter.Opcode(CubeRewardRs).U16(1).U8(0).Varint(0).ToArray()];
            items = RollRewards(table, rng);
            foreach (var mid in KilledMidBosses(c, room!))
                if (room!.Cube!.GetValueOrDefault(mid.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } midTable)
                    items.AddRange(RollRewards(midTable, rng));
            items = items.GroupBy(i => i.ItemId).Select(g => (g.Key, g.Sum(i => i.Count))).ToList();
            c.CubeTaken[playerKey] = items;
            pendingRewards.Add(items);
        }
        var replies = ContentTicketUpdates();   // the energy first (OBSERVED 0x610c before the items: 090856 #142335-#142341)
        replies.AddRange(await GrantPendingRewardsAsync(ct));
        var writer = PacketWriter.Opcode(CubeRewardRs).U16(0).U8(1).Varint((uint)items.Count);
        foreach (var (item, count) in items) writer.I32(item).I32((int)Math.Min(count, int.MaxValue));
        return [.. replies, writer.ToArray()];
    }
}
