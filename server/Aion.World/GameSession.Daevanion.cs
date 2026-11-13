using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Daevanion (the "Daevanion" window and the Status info window's Progression tab).
//
// BOARDS. DaevanionBoardInfo_NT 0xe226 = varint board count | {board i32, varint node count, node i32 each}. OFFICIAL (16
// recordings): Start node first (board * 10000 + 113), then learned nodes ascending. Board x5 (Ariel) is in no client table but
// the official level-45 Ranger lists 350113 (4 entries): listed from 45 with its start node only (level CANDIDATE).
//
// NODES: 15 x 15 grid, id = board*10000 + (row-1)*15 + col. Learn (0xe227 {node i32}) when board open, level >= NeedLevel, an
// up/down/left/right neighbour held (start counts), enough points of the board's CostPointType. OFFICIAL check: all 16 recorded
// boards are 4-connected from start; costs sum exactly to the used points (Ranger 31-34 = 396 DaevanionCrystal, 36 = 3 Battle).
// Reply order OBSERVED (rec 115817, 13 opens): 0xe22e used points, [0x364a stats + 0x3656 CP], [0x3813 reason 10], 0xe228 {u16, node}.
//
// POINTS: lists {varint 5, {type u8, points u16}} (OBSERVED u16: 396 = "8c01"; the generated varint codec is wrong). Used 0xe22e,
// item bonus 0xe22f; client adds Exp DaevanionPointMap itself. OFFICIAL: level-45 Ranger bonus 260 used 396 = 136 + 260.
// CANDIDATE: DaevanionMaxCapacity caps the total. Refund/reset/open-all/route: no samples (CANDIDATE); open-all/route opcodes
// 0xe355-0xe358 from the 0xe3 run offset +0xa762.
// REQUESTS: the recordings' client messages are encrypted, so 0xe227 / 0xe22a / 0xe22c / 0xe355 / 0xe357 and their bodies are the
// catalogue's RQ-before-RS order with the RS layouts (CANDIDATE until a live test). Learn-all takes what the points pay for, breadth
// first (CANDIDATE). Forgetting a node / resetting a board costs the nodes' ResetGold Kina (DaevanionNode, GoldCombined).
// ITEMS: GetDaevanionPoint [type, n] adds n per item, Fix_DaevanionPoint [type, n] raises the type to n; reply order OBSERVED
// (rec 115817 #74857-74859): 0x561b, 0x5615/0x5613 772, 0xe22f item points.
// STORE: migration 083 (game.character_daevanion_nodes, game.character_daevanion_points).
public sealed partial class GameSession
{
    public const ushort DaevanionBoardInfoNt = 0xe226, DaevanionOpenNodeRq = 0xe227, DaevanionOpenNodeRs = 0xe228, DaevanionOpenNodeNt = 0xe229,
        DaevanionCloseNodeRq = 0xe22a, DaevanionCloseNodeRs = 0xe22b, DaevanionResetBoardRq = 0xe22c, DaevanionResetBoardRs = 0xe22d,
        DaevanionPointUpdateNt = 0xe22e, UpdateItemDaevanionPointNt = 0xe22f,
        DaevanionOpenAllNodeRq = 0xe355, DaevanionOpenAllNodeRs = 0xe356, DaevanionOpenRouteNodeRq = 0xe357, DaevanionOpenRouteNodeRs = 0xe358;

    const byte SkillLevelDaevanion = 10;          // ESkillAcquireReason (OBSERVED 0x3813 reason 10)
    const byte DecreaseDaevanionRefund = 14;      // EDecreaseReason CANDIDATE

    static DaevanionData Daevanion => DaevanionData.Embedded;

    static readonly Dictionary<string, int> DaevanionClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gladiator"] = 1, ["Templar"] = 2, ["Ranger"] = 3, ["Assassin"] = 4, ["Elementalist"] = 5, ["Sorcerer"] = 6, ["Cleric"] = 7, ["Chanter"] = 8, ["Fighter"] = 9,
    };

    const int ArielBoardLevel = 45;

    public static List<int> DaevanionBoards(string? className, int level)
    {
        if (className is null || !DaevanionClass.TryGetValue(className, out var c)) return [];
        var open = Daevanion.Boards.Where(b => b.Id / 10 == c && level >= b.NeedLevel).Select(b => b.Id).ToList();
        if (level >= ArielBoardLevel) open.Add(c * 10 + 5);
        return open.Order().ToList();
    }

    public static int DaevanionStartNode(int board) => board * 10_000 + 113;

    string? EntryClassName => className ?? (character is { } c ? data.Profile(c.CreationId, c.Gender)?.Class : null);

    readonly HashSet<int> daevanionNodes = [];
    readonly int[] daevanionBonus = new int[DaevanionData.PointTypes + 1];
    Dictionary<string, int>? daevanionStats;

    void LoadDaevanion(DaevanionRecord stored)
    {
        daevanionNodes.Clear();
        foreach (var id in stored.Nodes)
            if (Daevanion.NodeOf(id) is { Type: not "Start" }) daevanionNodes.Add(id);
        Array.Clear(daevanionBonus);
        foreach (var (type, points) in stored.Bonus)
            if (type is >= 1 and <= DaevanionData.PointTypes) daevanionBonus[type] = Math.Max(0, points);
        daevanionStats = null;
    }

    bool DaevanionHeld(int node, IReadOnlyCollection<int> boards) =>
        daevanionNodes.Contains(node) || node % 10_000 == 113 && boards.Contains(node / 10_000);

    public int DaevanionEarned(int type) =>
        type is < 1 or > DaevanionData.PointTypes ? 0
            : Math.Min(Daevanion.MaxPoints(type) is > 0 and var max ? max : int.MaxValue, (type == 1 ? Daevanion.LevelPoints(level) : 0) + daevanionBonus[type]);

    public int DaevanionUsed(int type) =>
        daevanionNodes.Sum(id => Daevanion.NodeOf(id) is { } n && Daevanion.BoardOf(n.Board)?.PointType == type ? n.Cost : 0);

    public int DaevanionLeft(int type) => Math.Max(0, DaevanionEarned(type) - DaevanionUsed(type));

    static PacketWriter DaevanionPointList(PacketWriter w, Func<int, int> points)
    {
        w.Varint(DaevanionData.PointTypes);
        for (var type = 1; type <= DaevanionData.PointTypes; type++) w.U8((byte)type).U16(Math.Clamp(points(type), 0, ushort.MaxValue));
        return w;
    }

    byte[] DaevanionPointUpdate() => DaevanionPointList(PacketWriter.Opcode(DaevanionPointUpdateNt), DaevanionUsed).ToArray();
    byte[] DaevanionItemPointUpdate() => DaevanionPointList(PacketWriter.Opcode(UpdateItemDaevanionPointNt), t => daevanionBonus[t]).ToArray();
    byte[] DaevanionAppearLists() =>
        [.. DaevanionPointList(new PacketWriter(), DaevanionUsed).ToArray(), .. DaevanionPointList(new PacketWriter(), t => daevanionBonus[t]).ToArray()];

    byte[] DaevanionBoardInfo()
    {
        var boards = DaevanionBoards(EntryClassName, level);
        var w = PacketWriter.Opcode(DaevanionBoardInfoNt).Varint(boards.Count);
        foreach (var board in boards)
        {
            var nodes = daevanionNodes.Where(n => n / 10_000 == board).Order().ToList();
            w.I32(board).Varint(nodes.Count + 1).I32(DaevanionStartNode(board));
            foreach (var node in nodes) w.I32(node);
        }
        return w.ToArray();
    }

    internal byte[] DaevanionBoardInfoForChecks() => DaevanionBoardInfo();

    IReadOnlyDictionary<string, int> DaevanionStats()
    {
        if (daevanionStats is { } cached) return cached;
        var sum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in daevanionNodes)
            foreach (var (stat, value) in Daevanion.NodeOf(id)?.Stats ?? [])
                sum[stat] = sum.GetValueOrDefault(stat) + value;
        return daevanionStats = sum;
    }

    int DaevanionStat(string stat) => daevanionNodes.Count == 0 ? 0 : DaevanionStats().GetValueOrDefault(stat);
    IEnumerable<string> DaevanionStatNames() => daevanionNodes.Count == 0 ? [] : DaevanionStats().Keys;

    public int DaevanionSkillBonus(int skillId) =>
        daevanionNodes.Count == 0 ? 0 : daevanionNodes.Sum(id => Daevanion.NodeOf(id) is { Type: "SkillLevel" } n && n.SkillId == skillId ? n.SkillLevels : 0);

    /// <summary>A skill's level in combat: the bought (point) level plus the Daevanion SkillLevel nodes and the worn gear's
    /// sub-stat skill levels (GameSession.GearEffects.cs).</summary>
    int CombatSkillLevel(int skillId) => SkillLevel(skillId) + DaevanionSkillBonus(skillId) + EquipSkillBonus(skillId);

    /// <summary>The Daevanion levels as the 0x3813 / 0x5100 byte.</summary>
    byte DaevanionBonusByte(int skillId) => (byte)Math.Clamp(DaevanionSkillBonus(skillId), 0, 255);

    List<byte[]> DaevanionSkillUpdates(IEnumerable<int> changedNodes) =>
        changedNodes.Select(id => Daevanion.NodeOf(id)).Where(n => n is { Type: "SkillLevel" } && learned.Contains(n.SkillId)).Select(n => n!.SkillId).Distinct()
            .Select(skill => SkillLevelUpdate(skill, SkillLevel(skill), SkillLevelDaevanion, (byte)Math.Min(255, DaevanionSkillBonus(skill)), EquipBonusByte(skill))).ToList();

    List<byte[]> DaevanionChanged(IReadOnlyCollection<int> changed)
    {
        daevanionStats = null;
        var replies = new List<byte[]> { DaevanionPointUpdate() };
        if (changed.Any(id => Daevanion.NodeOf(id)?.Type == "Stat")) replies.AddRange(RefreshStats());
        replies.AddRange(DaevanionSkillUpdates(changed));
        return replies;
    }

    int DaevanionLearnable(int nodeId, IReadOnlyCollection<int> boards, ISet<int> chosen)
    {
        if (Daevanion.NodeOf(nodeId) is not { } node || node.Type is "Start") return EResult.Daevanion_Node_InvalidData;
        if (!boards.Contains(node.Board) || Daevanion.BoardOf(node.Board) is null) return EResult.Daevanion_Node_NotActiveBoard;
        if (daevanionNodes.Contains(nodeId) || chosen.Contains(nodeId)) return EResult.Daevanion_Node_AlreadyOpen;
        if (node.NeedLevel > level) return EResult.Daevanion_Node_NotEnoughCharLevel;
        if (!Daevanion.Neighbours(nodeId).Any(n => DaevanionHeld(n, boards) || chosen.Contains(n))) return EResult.Daevanion_Node_NeighborNodeNotOpen;
        return 0;
    }

    async Task<(int Result, List<byte[]> Replies)> LearnDaevanionAsync(IReadOnlyList<int> nodes, CancellationToken ct)
    {
        if (resources is null || dead || nodes.Count == 0) return (EResult.Daevanion_Node_InvalidData, []);
        var boards = DaevanionBoards(className, level);
        var chosen = new HashSet<int>();
        foreach (var id in nodes)
        {
            var refusal = DaevanionLearnable(id, boards, chosen);
            if (refusal != 0) return (refusal, []);
            chosen.Add(id);
        }
        foreach (var group in nodes.GroupBy(id => Daevanion.BoardOf(id / 10_000)!.PointType))
            if (group.Sum(id => Daevanion.NodeOf(id)!.Cost) > DaevanionLeft(group.Key)) return (EResult.Daevanion_Node_NotEnoughDaevanionPoint, []);
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SaveDaevanionNodesAsync(character, nodes, [], null, ct) ? [[]] : []);
            if (saved.Count == 0) return (EResult.Daevanion_Node_InternalError, []);
        }
        daevanionNodes.UnionWith(nodes);
        return (0, DaevanionChanged(nodes.ToList()));
    }

    async Task<List<byte[]>> DaevanionOpenNodeAsync(byte[] plain, CancellationToken ct)
    {
        var nodeId = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        var (result, replies) = await LearnDaevanionAsync([nodeId], ct);
        return [.. replies, PacketWriter.Opcode(DaevanionOpenNodeRs).U16(result).I32(nodeId).ToArray()];
    }

    /// <summary>
    /// "Learn all" of a board: the learnable nodes outward from the held ones (breadth first, lower ids first) while the board's
    /// points last; a node too dear is skipped (so is what lies behind it). CANDIDATE: no official sample of the order.
    /// </summary>
    (List<int> Nodes, bool Short) DaevanionAllNodes(int board, IReadOnlyCollection<int> boards)
    {
        var order = new List<int>();
        var chosen = new HashSet<int>();
        var budget = DaevanionLeft(Daevanion.BoardOf(board)?.PointType ?? 0);
        var tooDear = false;
        var queue = new Queue<int>(daevanionNodes.Where(n => n / 10_000 == board).Append(DaevanionStartNode(board)));
        while (queue.Count > 0)
            foreach (var next in Daevanion.Neighbours(queue.Dequeue()).Order())
                if (DaevanionLearnable(next, boards, chosen) == 0)
                {
                    var cost = Daevanion.NodeOf(next)!.Cost;
                    if (cost > budget) { tooDear = true; continue; }
                    budget -= cost;
                    chosen.Add(next);
                    order.Add(next);
                    queue.Enqueue(next);
                }
        return (order, tooDear);
    }

    static byte[] DaevanionNodesReply(ushort opcode, int result, int board, IReadOnlyList<int> nodes)
    {
        var w = PacketWriter.Opcode(opcode).U16(result).I32(board).Varint(nodes.Count);
        foreach (var node in nodes) w.I32(node);
        return w.ToArray();
    }

    async Task<List<byte[]>> DaevanionOpenAllAsync(byte[] plain, CancellationToken ct)
    {
        var board = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        var boards = DaevanionBoards(className, level);
        if (!boards.Contains(board) || Daevanion.BoardOf(board) is null)
            return [DaevanionNodesReply(DaevanionOpenAllNodeRs, EResult.Daevanion_Board_NotActiveBoard, board, [])];
        var (nodes, tooDear) = DaevanionAllNodes(board, boards);
        if (nodes.Count == 0)
            return [DaevanionNodesReply(DaevanionOpenAllNodeRs, tooDear ? EResult.Daevanion_Node_NotEnoughDaevanionPoint : EResult.Daevanion_Node_AlreadyOpen, board, [])];
        var (result, replies) = await LearnDaevanionAsync(nodes, ct);
        return [.. replies, DaevanionNodesReply(DaevanionOpenAllNodeRs, result, board, result == 0 ? nodes : [])];
    }

    async Task<List<byte[]>> DaevanionOpenRouteAsync(byte[] plain, CancellationToken ct)
    {
        int board;
        var nodes = new List<int>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            board = reader.I32();
            var count = reader.Varint();
            for (var i = 0; i < count && i < DaevanionData.GridSize * DaevanionData.GridSize; i++) nodes.Add(reader.I32());
        }
        catch (ProtocolException) { return [DaevanionNodesReply(DaevanionOpenRouteNodeRs, EResult.Daevanion_Node_InvalidData, 0, [])]; }
        if (nodes.Any(n => n / 10_000 != board) || nodes.Distinct().Count() != nodes.Count)
            return [DaevanionNodesReply(DaevanionOpenRouteNodeRs, EResult.Daevanion_Node_InvalidData, board, [])];
        var (result, replies) = await LearnDaevanionAsync(nodes, ct);
        return [.. replies, DaevanionNodesReply(DaevanionOpenRouteNodeRs, result, board, result == 0 ? nodes : [])];
    }

    bool DaevanionConnectedWithout(int board, IReadOnlySet<int> removed)
    {
        var keep = daevanionNodes.Where(n => n / 10_000 == board && !removed.Contains(n)).ToHashSet();
        var seen = new HashSet<int> { DaevanionStartNode(board) };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0)
            foreach (var next in Daevanion.Neighbours(queue.Dequeue()))
                if (keep.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        return keep.All(seen.Contains);
    }

    async Task<(bool Paid, List<byte[]> Replies)> RefundDaevanionAsync(IReadOnlyList<int> nodes, CancellationToken ct)
    {
        var gold = nodes.Sum(id => (long)(Daevanion.NodeOf(id)?.ResetGold ?? 0));
        if (Store is { } store && CharacterId is { } character)
        {
            var kina = storedItems.Values.Where(i => i.TemplateId == KinaItemId && i.Location == "bag").OrderByDescending(i => i.Quantity).FirstOrDefault();
            if (gold > 0 && (kina is null || kina.Quantity < gold)) return (false, []);
            var saved = await Stored(async () => await store.SaveDaevanionNodesAsync(character, [], nodes, gold > 0 ? (kina!.InstanceId, kina.Revision, gold) : null, ct) ? [[]] : []);
            if (saved.Count == 0) return (false, []);
        }
        daevanionNodes.ExceptWith(nodes);
        var replies = gold > 0 && Store is not null ? await InventoryChangesAsync(ct, 0, DecreaseDaevanionRefund) : [];
        replies.AddRange(DaevanionChanged(nodes));
        return (true, replies);
    }

    async Task<List<byte[]>> DaevanionCloseNodeAsync(byte[] plain, CancellationToken ct)
    {
        var nodeId = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        byte[] Reply(int result) => PacketWriter.Opcode(DaevanionCloseNodeRs).U16(result).I32(nodeId).ToArray();
        if (resources is null || dead || Daevanion.NodeOf(nodeId) is not { } node || node.Type == "Start") return [Reply(EResult.Daevanion_Node_InvalidData)];
        if (!daevanionNodes.Contains(nodeId)) return [Reply(EResult.Daevanion_Node_NotOpenNode)];
        if (!DaevanionConnectedWithout(node.Board, new HashSet<int> { nodeId })) return [Reply(EResult.Daevanion_Node_NeighborNodeOpened)];
        var (paid, replies) = await RefundDaevanionAsync([nodeId], ct);
        return paid ? [.. replies, Reply(0)] : [Reply(EResult.Daevanion_Board_ResetNotEnoughMoney)];
    }

    async Task<List<byte[]>> DaevanionResetBoardAsync(byte[] plain, CancellationToken ct)
    {
        var board = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        byte[] Reply(int result) => PacketWriter.Opcode(DaevanionResetBoardRs).U16(result).I32(board).ToArray();
        if (resources is null || dead || Daevanion.BoardOf(board) is null) return [Reply(EResult.Daevanion_Board_InvalidData)];
        if (!DaevanionBoards(className, level).Contains(board)) return [Reply(EResult.Daevanion_Board_NotActiveBoard)];
        var nodes = daevanionNodes.Where(n => n / 10_000 == board).Order().ToList();
        if (nodes.Count == 0) return [Reply(EResult.Daevanion_Node_NotOpenNode)];
        var (paid, replies) = await RefundDaevanionAsync(nodes, ct);
        return paid ? [.. replies, DaevanionBoardInfo(), Reply(0)] : [Reply(EResult.Daevanion_Board_ResetNotEnoughMoney)];
    }

    static readonly Dictionary<string, int> DaevanionPointTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DaevanionCrystal"] = 1, ["ConquestCrystal"] = 2, ["BattleCrystal"] = 3, ["Season02Crystal01"] = 4, ["Season03Crystal01"] = 5,
    };

    async Task<(int Result, int Used, List<byte[]>? Replies)> UseDaevanionPointItemsAsync(ItemState row, ItemUse use, int count, CancellationToken ct)
    {
        if (use.Values is not [var typeName, var amountText, ..] || !DaevanionPointTypes.TryGetValue(typeName, out var type)
            || !int.TryParse(amountText, out var amount) || amount < 1 || count < 1 || count > row.Quantity)
            return (1, 0, null);
        var max = Daevanion.MaxPoints(type) is > 0 and var m ? m : ushort.MaxValue;
        var levelPart = type == 1 ? Daevanion.LevelPoints(level) : 0;
        int bonus;
        if (use.Effect == "Fix_DaevanionPoint")
        {
            if (levelPart + daevanionBonus[type] >= amount) return (EResult.Item_Use_Fail_Over_Already_FixDaevanionPoint, 0, null);
            (bonus, count) = (Math.Min(amount, max) - levelPart, 1);
        }
        else
        {
            var room = max - levelPart - daevanionBonus[type];
            if (room < amount) return (EResult.Daevanion_Item_Use_Fail_Over_Maximum_Point, 0, null);
            count = Math.Min(count, room / amount);
            bonus = daevanionBonus[type] + amount * count;
        }
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.UseDaevanionPointItemAsync(character, row.InstanceId, row.Revision, count, type, bonus, ct) ? [[]] : []);
            if (saved.Count == 0) return (1, 0, null);
        }
        daevanionBonus[type] = bonus;
        return (0, count, await InventoryChangesAsync(ct, 0, DecreaseItemUse));
    }

    internal IReadOnlyCollection<int> DaevanionNodesForChecks => daevanionNodes;
    internal void SetLevelForChecks(int value) => level = value;
    internal void SetDaevanionNodesForChecks(params int[] nodes) { daevanionNodes.Clear(); daevanionNodes.UnionWith(nodes); daevanionStats = null; }
    internal int CombatSkillLevelForChecks(int skillId) => CombatSkillLevel(skillId);
    internal byte[] DaevanionAppearListsForChecks() => DaevanionAppearLists();
    internal void SetDaevanionBonusForChecks(int type, int points) => daevanionBonus[type] = points;
}

public sealed partial class GameSession
{
    /// <summary>Checks only: a stat buff (StatChange-like) with these stats; returns its uid for removal.</summary>
    internal int AddStatBuffForChecks(IReadOnlyDictionary<string, int> stats)
    {
        var buff = new Buff { Uid = ++buffUid, AbnormalId = 0, SkillId = 0, Group = 0, DurationMs = 60_000, ExpireAt = long.MaxValue };
        foreach (var (k, v) in stats) buff.Stats[k] = v;
        buffs.Add(buff);
        return buff.Uid;
    }

    internal void RemoveBuffForChecks(int uid) => buffs.RemoveAll(b => b.Uid == uid);
}
