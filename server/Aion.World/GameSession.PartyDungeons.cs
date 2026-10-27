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
    /// <summary>Player deaths in this copy (the death-limit achievements).</summary>
    public int Deaths { get; set; }
    /// <summary>The final boss's cube reward each player took (player key → items), once per run.</summary>
    public Dictionary<int, List<(int ItemId, long Count)>> CubeTaken { get; } = [];
}

public sealed partial class GameSession
{
    // OBSERVED (HQ001 layer, capture 20261013 #9687-9689, #14161-14167): catalogue 573-584 live at 0x5ec3 + index, not in the 0x54ca run.
    public const ushort DungeonStateNt = 0x6100, DungeonCompleteResultNt = 0x6101, DungeonClearKillNpcNt = 0x6108, DungeonDeathLimitNt = 0x6109, CubeRewardStartNt = 0x56cc, CubeRewardRq = 0x56cd, CubeRewardRs = 0x56ce;
    public const byte DungeonStateProcess = 2, DungeonStateComplete = 3, DungeonStateEnd = 4;
    /// <summary>After the time limit the copy empties this long after the fail message (OURS).</summary>
    public const long DungeonFailExitMs = 5_000;

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
        if (room.QuestGate is { Length: > 0 } gates && questLog is not null)
        {
            var open = gates.Any(g => g.Length >= 2 && g[0].GetString() is { } code && questLog.Get(code) is { } record
                && (record.Status != QuestLog.Incomplete || record.Step == g[1].GetInt32()));
            if (!open) { why = $"quest step {string.Join('/', gates.Select(g => g[0].GetString()))} not reached"; return false; }
        }
        return true;
    }

    /// <summary>The copy a party member would join is full (MapEntrance MaxMember).</summary>
    static bool CopyFull(MapInstance inside, DungeonInfo room) => room.MaxMember > 0 && inside.Players.Count >= room.MaxMember;

    /// <summary>DungeonState_NT 0x6100 {dungeon, EDungeonState u8, start, end, u8 0} (OBSERVED: `60000000 02 [now] 0 00` at the layer entry).</summary>
    static byte[] DungeonState(int dungeonId, byte state, long startMs, long endMs) =>
        PacketWriter.Opcode(DungeonStateNt).I32(dungeonId).U8(state).I64(startMs).I64(endMs).U8(0).ToArray();

    /// <summary>DungeonCompleteResult_NT 0x6101 (OBSERVED `00 60000000 01 00`: u8 0, dungeon, succeeded, no rewards).</summary>
    static byte[] DungeonCompleteResult(int dungeonId, bool succeeded) =>
        PacketWriter.Opcode(DungeonCompleteResultNt).U8(0).I32(dungeonId).U8((byte)(succeeded ? 1 : 0)).U8(0).ToArray();

    /// <summary>After the running state at entry (OBSERVED): DungeonDeathLimit_NT 0x6109 `00000000`, DungeonClearKillNpc_NT 0x6108 `00`.</summary>
    static IEnumerable<byte[]> DungeonLimitsAtEntry() =>
        [PacketWriter.Opcode(DungeonDeathLimitNt).I32(0).ToArray(), PacketWriter.Opcode(DungeonClearKillNpcNt).U8(0).ToArray()];

    /// <summary>At the world entry of an expedition: its state (running until the deadline, or complete until the hold ends).</summary>
    List<byte[]> DungeonStateAtEntry(long now)
    {
        if (copy is not { } c || MapEventData.DungeonOfMap(worldMap) is not { } room) return [];
        // An instance layer (the story): running since the entry, no deadline (OBSERVED at the HQ001 entry).
        if (!Timed(room)) return c.Cleared ? [] : [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, 0), .. DungeonLimitsAtEntry()];
        if (c.Failed) return [];
        if (c.Phase == "Prepare") return [DungeonState(c.DungeonId, DungeonStatePrepare, c.StartedMs, c.ProcessAtMs)];
        if (c.Cleared) return [DungeonState(c.DungeonId, DungeonStateEnd, now, (c.Host.instanceExitAt ?? now + PartyDungeonHoldMs))];   // state 4 carries the hold's end (OBSERVED)
        return [DungeonState(c.DungeonId, DungeonStateProcess, c.StartedMs, c.DeadlineMs), .. DungeonLimitsAtEntry()];
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

    /// <summary>A kill in the host's copy: the cube's final boss arms the cube (OdEnergyCubeRewardStart_NT), a mid boss pays its table to everyone now.</summary>
    void CubeKilled(Target target, long now)
    {
        if (copy is not { } c || c.Host != this || MapEventData.DungeonOfMap(worldMap) is not { Cube: { } cube } room || !c.CubeBossesKilled.Add(target.NpcId)) return;
        if (!cube.TryGetValue(target.NpcId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var table)) return;
        if (target.NpcId == room.CubeBoss)
        {
            mapEventOut.Add(PacketWriter.Opcode(CubeRewardStartNt).Varint(1).I32(target.NpcId).ToArray());
            return;
        }
        foreach (var session in CopyMembers().Prepend(this))
            session.pendingRewards.Add(RollRewards(table, session.Rng));
    }

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
    /// The final boss's cube reward: on the cube object's use, or on OdEnergyCubeReward_RQ {reward count u8}; once per
    /// player per run (RewardOdEnergyCube ConsumeMaxCount 1; the 20 common tickets it consumes are not modelled). The
    /// request gets OdEnergyCubeReward_RS {result, count, [{item, count}]} with the items (the same ones again when the
    /// object use already paid them); result 1 when the boss is still alive or there is no cube here.
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
            items = RollRewards(table, rng);
            c.CubeTaken[playerKey] = items;
            pendingRewards.Add(items);
        }
        var replies = await GrantPendingRewardsAsync(ct);
        if (plain is null) return replies;
        var writer = PacketWriter.Opcode(CubeRewardRs).U16(0).U8(1).Varint((uint)items.Count);
        foreach (var (item, count) in items) writer.I32(item).I32((int)Math.Min(count, int.MaxValue));
        return [.. replies, writer.ToArray()];
    }
}
