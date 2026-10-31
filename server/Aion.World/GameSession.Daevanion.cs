using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

using DaevanionNode = DaevanionTables.Node;

// Daevanion boards (DaevanionBoard/Node.dat via tools/data/daevanion.json). OBSERVED at world entry for a fresh
// character: DaevanionPointUpdate_NT 0xe22e (five point types, used=0) and DaevanionBoardInfo_NT 0xe226 (empty).
// Opening a node (OBSERVED learn capture): PointUpdate_NT 0xe22e (_used_point_infos per usmap), CharStatsUpdate_NT 0x364a,
// 0x561d, CharCombatPowerUpdate_NT 0x3656 (official; we skip 0x3656 until combat power is tracked live), then
// DaevanionOpenNode_RS 0xe228, AchievementCountUpdate_NT 0x5684 — no OpenNode_NT 0xe229.
public sealed partial class GameSession
{
    public const ushort DaevanionBoardInfoNt = 0xe226, DaevanionOpenNodeRq = 0xe227, DaevanionOpenNodeRs = 0xe228,
        DaevanionOpenNodeNt = 0xe229, DaevanionCloseNodeRq = 0xe22a, DaevanionCloseNodeRs = 0xe22b,
        DaevanionResetBoardRq = 0xe22c, DaevanionResetBoardRs = 0xe22d, DaevanionPointUpdateNt = 0xe22e,
        UpdateItemDaevanionPointNt = 0xe22f, DaevanionOpenAllNodeRq = 0xe2ee, DaevanionOpenAllNodeRs = 0xe2ef,
        DaevanionOpenRouteNodeRq = 0xe2f0, DaevanionOpenRouteNodeRs = 0xe2f1;

    /// <summary>EContentsUnlockType::Daevanion (20): the client keeps board tabs locked until this is in 0xe257/0xe258.</summary>
    public const byte DaevanionContentsUnlock = 20;

    static DaevanionTables Daevanion => DaevanionTables.Embedded;

    readonly HashSet<int> daevanionNodes = [];
    readonly Dictionary<int, int> daevanionUsed = [];
    readonly Dictionary<int, int> daevanionBonus = [];
    bool daevanionContentsUnlocked;

    void LoadDaevanion(DaevanionProgress? stored)
    {
        daevanionContentsUnlocked = false;
        daevanionNodes.Clear();
        daevanionUsed.Clear();
        daevanionBonus.Clear();
        if (stored is not null)
        {
            daevanionNodes.UnionWith(stored.NodeIds);
            foreach (var (type, used) in stored.UsedPoints) if (used > 0) daevanionUsed[type] = used;
            foreach (var (type, bonus) in stored.BonusPoints) if (bonus > 0) daevanionBonus[type] = bonus;
        }
    }

    void EnsureStartNodes()
    {
        if (character is null) return;
        foreach (var boardId in Daevanion.BoardsForCreation(character.CreationId))
        {
            if (level < (Daevanion.BoardOf(boardId)?.NeedLevel ?? 999)) continue;
            daevanionNodes.Add(Daevanion.StartNodeId(boardId));
        }
    }

    /// <summary>
    /// Level-earned Daevanion crystals per point type (client Exp/Daevanion tables: one at NeedLevel, then +1 per level;
    /// matches UI cap e.g. level 16 on a board opening at 12 → 4/4 available when used=0).
    /// </summary>
    int DaevanionLevelEarned(int pointType)
    {
        if (character is null) return 0;
        var earned = 0;
        foreach (var boardId in Daevanion.BoardsForCreation(character.CreationId))
        {
            if (Daevanion.BoardOf(boardId) is not { } board || board.PointType != pointType) continue;
            if (level < board.NeedLevel) continue;
            var tier = level == board.NeedLevel ? 1 : level - board.NeedLevel;
            earned = Math.Max(earned, tier);
        }
        return earned;
    }

    int DaevanionAvailable(int pointType) =>
        DaevanionLevelEarned(pointType) + daevanionBonus.GetValueOrDefault(pointType) - daevanionUsed.GetValueOrDefault(pointType);

    /// <summary>Class boards whose NeedLevel is met (ContentsUnlock.dat also quest-gates Daevanion on HQ1102060 / HQ2102065).</summary>
    bool HasLevelUnlockedClassBoard() =>
        character is not null && Daevanion.BoardsForCreation(character.CreationId)
            .Any(id => level >= (Daevanion.BoardOf(id)?.NeedLevel ?? 999));

    bool DaevanionQuestGateOpen() =>
        questLog?.UnlockedContents().Contains("Daevanion") == true;

    /// <summary>0xe257 extras: a class board tier is level-open (NeedLevel met) but quest bytes were not in the merged list yet.</summary>
    public IEnumerable<byte> DaevanionExtraContentsUnlocks(IEnumerable<byte> questUnlocks)
    {
        if (daevanionContentsUnlocked || !HasLevelUnlockedClassBoard()) return [];
        if (questUnlocks.Contains(DaevanionContentsUnlock)) { daevanionContentsUnlocked = true; return []; }
        return [DaevanionContentsUnlock];
    }

    byte[] DaevanionPointUpdate()
    {
        // DevPacketData_GameServer_DaevanionPointUpdate_NT._used_point_infos (CommonDaevanionPointTypeInfo._point = spent).
        // Official learn capture 100→101→102 is cumulative used on a progressed character, not a wire base offset.
        var w = PacketWriter.Opcode(DaevanionPointUpdateNt).U8(5);
        foreach (var type in Enumerable.Range(1, 5))
            w.U8((byte)type).U16((ushort)daevanionUsed.GetValueOrDefault(type));
        return w.ToArray();
    }

    /// <summary>After opening node(s): one 0x364a with changed wires, 0x561d, then pools updated (no 0x8d00 maxima burst).</summary>
    List<byte[]> DaevanionOpenStatPackets(IEnumerable<DaevanionNode> nodes)
    {
        if (resources is null) return [];
        var opened = nodes.ToList();
        var (stats, hpMax) = StatSheet();
        resources.HpMax = hpMax;
        resources.MpMax = MpMax;
        resources.SpMax = GamePackets.SpawnSp + BuffStat("SPMax");
        resources.ApMax = PlayerApPools.ApMax(data, className, level, Profile()?.Resource);
        resources.Hp = Math.Min(resources.Hp, hpMax);
        resources.Mp = Math.Min(resources.Mp, resources.MpMax);
        resources.Sp = Math.Min(resources.Sp, resources.SpMax);
        resources.Ap = Math.Min(resources.Ap, Math.Max(0, resources.ApMax));
        var byType = stats.ToDictionary(p => p.Type, p => p.Value);
        var changed = opened.SelectMany(n => n.Stats).Select(s => s.Wire).Distinct().OrderBy(w => w)
            .Select(w => (w, byType.GetValueOrDefault(w))).ToList();
        var replies = new List<byte[]>
        {
            changed.Count > 0
                ? GamePackets.CharStatsUpdate(playerKey, changed, hpMax)
                : GamePackets.CharStatsUpdate(playerKey, hpMax),
        };
        var equip = EquipItemLevel();
        replies.Add(GamePackets.ItemEquipLevelChange(equip, equip));
        // CharCombatPowerUpdate_NT 0x3656 follows on official when combat power changes; not sent here (no live CP recompute).
        return replies;
    }

    byte[] DaevanionBoardInfo()
    {
        if (character is null)
            return PacketWriter.Opcode(DaevanionBoardInfoNt).Varint(0).ToArray();
        var boards = Daevanion.BoardsForCreation(character.CreationId)
            .Where(id => level >= (Daevanion.BoardOf(id)?.NeedLevel ?? 999))
            .OrderBy(id => id)
            .ToList();
        var w = PacketWriter.Opcode(DaevanionBoardInfoNt).Varint((uint)boards.Count);
        foreach (var boardId in boards)
        {
            var start = Daevanion.StartNodeId(boardId);
            var nodeIds = daevanionNodes.Where(id => Daevanion.NodeOf(id)?.BoardId == boardId).OrderBy(id => id).ToList();
            if (!nodeIds.Contains(start))
                nodeIds.Insert(0, start);
            w.I32(boardId).Varint((uint)nodeIds.Count);
            foreach (var nodeId in nodeIds) w.I32(nodeId);
        }
        return w.ToArray();
    }

    bool BoardAllowed(int boardId) =>
        character is not null && Daevanion.BoardOf(boardId) is { } board
        && board.Slot == Daevanion.ClassSlot(character.CreationId) && level >= board.NeedLevel;

    bool CanOpenNode(int nodeId, out DaevanionNode node, out int result)
    {
        node = null!;
        result = 1;
        if (Daevanion.NodeOf(nodeId) is not { } n) return false;
        node = n;
        if (daevanionNodes.Contains(nodeId)) return false;
        if (!BoardAllowed(n.BoardId)) return false;
        if (n.Type != 0 && !daevanionNodes.Any(id => Daevanion.NodeOf(id) is { } other && other.BoardId == n.BoardId && Daevanion.IsNeighbor(n, other)))
            return false;
        if (n.Cost <= 0) return true;
        if (Daevanion.BoardOf(n.BoardId) is not { } board) return false;
        if (DaevanionAvailable(board.PointType) < n.Cost) { result = 2; return false; }
        return true;
    }

    async Task<bool> PersistDaevanionAsync(CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return true;
        var progress = new DaevanionProgress(daevanionNodes.ToList(), daevanionUsed, daevanionBonus);
        return await store.SaveDaevanionAsync(character, progress, ct);
    }

    bool ApplyOpenNode(int nodeId, DaevanionNode node)
    {
        if (node.Cost > 0 && Daevanion.BoardOf(node.BoardId) is { } boardMeta)
            daevanionUsed[boardMeta.PointType] = daevanionUsed.GetValueOrDefault(boardMeta.PointType) + node.Cost;
        return daevanionNodes.Add(nodeId);
    }

    void RollbackOpenNode(int nodeId, DaevanionNode node)
    {
        daevanionNodes.Remove(nodeId);
        if (node.Cost > 0 && Daevanion.BoardOf(node.BoardId) is { } boardMeta)
        {
            daevanionUsed[boardMeta.PointType] = Math.Max(0, daevanionUsed.GetValueOrDefault(boardMeta.PointType) - node.Cost);
            if (daevanionUsed[boardMeta.PointType] == 0) daevanionUsed.Remove(boardMeta.PointType);
        }
    }

    byte[] DaevanionBatchOpenReply(ushort opcode, int result, int boardId, IReadOnlyList<int> opened)
    {
        var w = PacketWriter.Opcode(opcode).I32(result).I32(boardId).Varint((uint)opened.Count);
        foreach (var id in opened) w.I32(id);
        return w.ToArray();
    }

    List<int> CollectOpenAll(int boardId)
    {
        var opened = new List<int>();
        while (true)
        {
            var batch = Daevanion.NodesOfBoard(boardId).Where(id => CanOpenNode(id, out _, out _)).OrderBy(id => id).ToList();
            if (batch.Count == 0) break;
            foreach (var id in batch)
            {
                if (!CanOpenNode(id, out var node, out _)) continue;
                ApplyOpenNode(id, node);
                opened.Add(id);
            }
        }
        return opened;
    }

    bool TryOpenRoute(int boardId, IReadOnlyList<int> route, out List<int> opened, out int result)
    {
        opened = [];
        result = 1;
        if (!BoardAllowed(boardId)) return false;
        foreach (var nodeId in route)
        {
            if (Daevanion.NodeOf(nodeId) is not { } node || node.BoardId != boardId)
                return false;
            if (!CanOpenNode(nodeId, out node, out result))
                return false;
            ApplyOpenNode(nodeId, node);
            opened.Add(nodeId);
        }
        result = 0;
        return true;
    }

    async Task<List<byte[]>> FinishBatchOpenAsync(IReadOnlyList<int> opened, CancellationToken ct, bool achieve = true)
    {
        if (opened.Count == 0) return [];
        if (!await PersistDaevanionAsync(ct))
        {
            foreach (var id in opened.OrderByDescending(i => i))
                if (Daevanion.NodeOf(id) is { } node) RollbackOpenNode(id, node);
            return [];
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nodes = opened.Select(id => Daevanion.NodeOf(id)).OfType<DaevanionNode>().ToList();
        return
        [
            DaevanionPointUpdate(),
            .. DaevanionOpenStatPackets(nodes),
            .. achieve ? Achieve("DaevanionNodeActivate", now: now) : [],
        ];
    }

    async Task<List<byte[]>> DaevanionOpenNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var nodeId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(DaevanionOpenNodeRs).U16(result).I32(nodeId).ToArray();
        if (!CanOpenNode(nodeId, out var node, out var fail)) return [Reply(fail)];
        ApplyOpenNode(nodeId, node);
        if (!await PersistDaevanionAsync(ct))
        {
            RollbackOpenNode(nodeId, node);
            return [Reply(1)];
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            DaevanionPointUpdate(),
            .. DaevanionOpenStatPackets([node]),
            Reply(0),
            .. Achieve("DaevanionNodeActivate", now: now),
        ];
    }

    async Task<List<byte[]>> DaevanionOpenAllNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var boardId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result, IReadOnlyList<int> ids) => DaevanionBatchOpenReply(DaevanionOpenAllNodeRs, result, boardId, ids);
        if (!BoardAllowed(boardId)) return [Reply(1, [])];
        var opened = CollectOpenAll(boardId);
        if (opened.Count == 0)
        {
            var pending = Daevanion.NodesOfBoard(boardId).Any(id => !daevanionNodes.Contains(id));
            return [Reply(pending ? 1 : 0, [])];
        }
        var tail = await FinishBatchOpenAsync(opened, ct);
        if (tail.Count == 0) return [Reply(1, [])];
        return [Reply(0, opened), .. tail];
    }

    async Task<List<byte[]>> DaevanionOpenRouteNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        List<int> route;
        int boardId;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            boardId = reader.I32();
            var count = reader.Varint();
            route = [];
            for (var i = 0; i < count && i < 256; i++) route.Add(reader.I32());
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result, IReadOnlyList<int> ids) => DaevanionBatchOpenReply(DaevanionOpenRouteNodeRs, result, boardId, ids);
        if (route.Count == 0) return [Reply(1, [])];
        if (!TryOpenRoute(boardId, route, out var opened, out var fail))
        {
            foreach (var id in opened.OrderByDescending(i => i))
                if (Daevanion.NodeOf(id) is { } node) RollbackOpenNode(id, node);
            return [Reply(fail, [])];
        }
        var tail = await FinishBatchOpenAsync(opened, ct);
        if (tail.Count == 0)
        {
            foreach (var id in opened.OrderByDescending(i => i))
                if (Daevanion.NodeOf(id) is { } node) RollbackOpenNode(id, node);
            return [Reply(1, [])];
        }
        return [Reply(0, opened), .. tail];
    }

    async Task<List<byte[]>> DaevanionCloseNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var nodeId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(DaevanionCloseNodeRs).U16(result).I32(nodeId).ToArray();
        if (Daevanion.NodeOf(nodeId) is not { } node || node.Type == 0 || !daevanionNodes.Remove(nodeId)) return [Reply(1)];
        if (node.Cost > 0 && Daevanion.BoardOf(node.BoardId) is { } board)
        {
            daevanionUsed[board.PointType] = Math.Max(0, daevanionUsed.GetValueOrDefault(board.PointType) - node.Cost);
            if (daevanionUsed[board.PointType] == 0) daevanionUsed.Remove(board.PointType);
        }
        if (!await PersistDaevanionAsync(ct)) return [Reply(1)];
        return [Reply(0), DaevanionBoardInfo(), DaevanionPointUpdate(), .. DaevanionOpenStatPackets([node])];
    }

    async Task<List<byte[]>> DaevanionResetBoardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var boardId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(DaevanionResetBoardRs).U16(result).I32(boardId).ToArray();
        if (!BoardAllowed(boardId)) return [Reply(1)];
        var start = Daevanion.StartNodeId(boardId);
        var removed = new List<DaevanionNode>();
        foreach (var id in daevanionNodes.Where(id => Daevanion.NodeOf(id)?.BoardId == boardId && id != start).ToList())
        {
            if (Daevanion.NodeOf(id) is not { } node) continue;
            removed.Add(node);
            daevanionNodes.Remove(id);
            if (node.Cost > 0 && Daevanion.BoardOf(boardId) is { } board)
            {
                daevanionUsed[board.PointType] = Math.Max(0, daevanionUsed.GetValueOrDefault(board.PointType) - node.Cost);
                if (daevanionUsed[board.PointType] == 0) daevanionUsed.Remove(board.PointType);
            }
        }
        daevanionNodes.Add(start);
        if (!await PersistDaevanionAsync(ct)) return [Reply(1)];
        return [Reply(0), DaevanionBoardInfo(), DaevanionPointUpdate(), .. DaevanionOpenStatPackets(removed)];
    }

    async Task<List<byte[]>?> UseDaevanionPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        (int PointType, int Amount)? grant = Daevanion.ItemGrant(row.TemplateId);
        if (grant is null && use.Values is [var pointText, var amountText, ..]
            && int.TryParse(pointText, out var pointType) && int.TryParse(amountText, out var amount) && amount > 0)
            grant = (pointType, amount);
        if (grant is not { } g) return null;
        var consumed = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (consumed.Count == 0) return null;
        daevanionBonus[g.PointType] = daevanionBonus.GetValueOrDefault(g.PointType) + g.Amount;
        if (!await PersistDaevanionAsync(ct)) return null;
        return
        [
            .. Used(row, reply),
            PacketWriter.Opcode(UpdateItemDaevanionPointNt).U8(1).U8((byte)g.PointType).I32(g.Amount).ToArray(),
            DaevanionPointUpdate(),
        ];
    }

    /// <summary>Summed EStat wire bonuses from stat nodes (values are basis points for % stats).</summary>
    public IReadOnlyDictionary<int, int> DaevanionWireStats()
    {
        var totals = new Dictionary<int, int>();
        foreach (var node in daevanionNodes.Select(Daevanion.NodeOf).OfType<DaevanionNode>())
        foreach (var (wire, value) in node.Stats)
            totals[wire] = totals.GetValueOrDefault(wire) + value;
        return totals;
    }

    public int DaevanionWireStat(int wire) => DaevanionWireStats().GetValueOrDefault(wire);

    public int DaevanionSkillBonus(int skillId) => daevanionNodes.Select(Daevanion.NodeOf).OfType<DaevanionNode>()
        .Count(n => n.Type == 2 && n.SkillId == skillId);

    internal void ReloadDaevanionForTests(DaevanionProgress? progress)
    {
        LoadDaevanion(progress);
        EnsureStartNodes();
    }

    internal byte[] DaevanionPointUpdateWire() => DaevanionPointUpdate();

    internal int DaevanionAvailableForTests(int pointType) => DaevanionAvailable(pointType);

    internal int DaevanionLevelEarnedForTests(int pointType) => DaevanionLevelEarned(pointType);
}
