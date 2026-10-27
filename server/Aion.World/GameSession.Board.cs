using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Creature boards (one per ECreatureType 2..6). Exp comes from acquiring and levelling rides of that type
// (VehicleLevel GetCreatureExp); VehicleCreatureLevel NeedExp levels the board, and each level opens a slot
// (VehicleCreatureSlot SlotOpenLevel) in every panel. Rolling (0x9017) rerolls a panel's chosen unlocked
// slots for SoulCrystalCount crystals (the board's crystal type, else All) and GoldCount Kina, plus the lock
// cost by the number of locked slots (CANDIDATE: charged on rolls); the grade follows the board level's odds
// and the option its group's weights. Locking (0x9019) is free. Panel 1's stats (HPMax, MPMax, Defense,
// Critical) join the stat sheet; the client shows all of them from the board. All CANDIDATE except the
// world-entry form, which rebuilds the official sample.
public sealed partial class GameSession
{
    readonly Dictionary<byte, CreatureBoard> boards = [];
    public IReadOnlyDictionary<byte, CreatureBoard> Boards => boards;
    const byte SelectedPanel = 1;
    static readonly Dictionary<string, int> CreatureTypeNames = new() { ["Intellect"] = 2, ["Feral"] = 3, ["Nature"] = 4, ["Trans"] = 5, ["Special"] = 6 };

    void LoadBoards(IReadOnlyList<CreatureBoardRecord>? stored)
    {
        boards.Clear();
        for (byte type = 2; type <= 6; type++) boards[type] = CreatureBoard.Fresh(type);
        foreach (var b in stored ?? [])
            if (b.Type is >= 2 and <= 6) boards[(byte)b.Type] = FromJson((byte)b.Type, b.Level, b.Exp, b.Panels);
    }

    static CreatureBoard FromJson(byte type, int level, long exp, string json)
    {
        var panels = new Dictionary<byte, IReadOnlyList<BoardSlot>>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var panel in doc.RootElement.EnumerateObject())
                panels[byte.Parse(panel.Name)] = panel.Value.EnumerateArray()
                    .Select(s => new BoardSlot((byte)s[0].GetInt32(), (byte)s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt64(), s[4].GetInt32() == 1)).ToList();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or IndexOutOfRangeException) { }
        var fresh = CreatureBoard.Fresh(type);
        return new CreatureBoard(type, level, exp, panels.Count > 0 ? panels : fresh.Panels);
    }

    static CreatureBoardRecord ToRecord(CreatureBoard b) => new(b.Type, b.Level, b.Exp,
        JsonSerializer.Serialize(b.Panels.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.Select(s => new long[] { s.Index, s.Grade, s.StatType, s.Value, s.Locked ? 1 : 0 }).ToList())));

    /// <summary>A stat of the boards' selected panels.</summary>
    int BoardStat(string stat) => boards.Values.Sum(b => b.Panels.GetValueOrDefault(SelectedPanel)?.Sum(s =>
        s.StatType != 0 && StatName(s.StatType) == stat ? (int)s.Value : 0) ?? 0);

    static readonly Dictionary<int, string> statNames = new();
    static string? StatName(int statId)
    {
        lock (statNames)
        {
            if (statNames.Count == 0)
                for (byte type = 2; type <= 6; type++)
                    foreach (var group in Boxes.CreatureSlots(type)?.Groups ?? [])
                        foreach (var o in Boxes.CreatureOptions(group)) statNames[o.StatId] = o.Stat;
            return statNames.GetValueOrDefault(statId);
        }
    }

    /// <summary>A board with every slot its level opens (new empty slots appended to each panel).</summary>
    static CreatureBoard WithOpenSlots(CreatureBoard b)
    {
        var open = Boxes.CreatureSlots(b.Type)?.Open.Count(level => level <= b.Level) ?? 1;
        return b with { Panels = b.Panels.ToDictionary(p => p.Key, p => (IReadOnlyList<BoardSlot>)p.Value.Concat(
            Enumerable.Range(p.Value.Count, Math.Max(0, open - p.Value.Count)).Select(i => new BoardSlot((byte)i, 0, 0, 0))).ToList()) };
    }

    /// <summary>Board exp for a ride of a type: level-ups open slots; stored; 0x901c when the level changed.</summary>
    async Task<List<byte[]>> AddBoardExpAsync(int type, int exp, CancellationToken ct)
    {
        if (exp <= 0 || !boards.TryGetValue((byte)type, out var board)) return [];
        var level = board.Level;
        var total = board.Exp + exp;
        while (level < Boxes.CreatureMaxLevel(type) && Boxes.CreatureLevel(type, level) is { NeedExp: > 0 } row && total >= row.NeedExp)
        {
            total -= row.NeedExp;
            level++;
        }
        var updated = WithOpenSlots(board with { Level = level, Exp = total });
        return await SaveBoardAsync(updated, new Dictionary<int, int>(), null, ct) ? updated.Level != board.Level ? [RidePackets.BoardInfo(updated)] : [] : [];
    }

    async Task<bool> SaveBoardAsync(CreatureBoard board, IReadOnlyDictionary<int, int> crystalCost, (Guid Item, long Revision, long Count)? kina, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return false;
        var done = await Stored(async () => await store.SaveCreatureBoardAsync(character, ToRecord(board), crystalCost, kina, ct) ? [[]] : []);
        if (done.Count == 0) return false;
        boards[board.Type] = board;
        return true;
    }

    /// <summary>0x9017 CreatureBoardStatSlotRoll_RQ {type u8, panel u8, slot list (varint count + u8), auto bool}.</summary>
    async Task<List<byte[]>> RollBoardAsync(byte[] plain, CancellationToken ct)
    {
        byte type, panel;
        var targets = new List<byte>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            type = reader.U8(); panel = reader.U8();
            var count = reader.Varint();
            for (var i = 0; i < count && i < 32; i++) targets.Add(reader.U8());
        }
        catch (ProtocolException) { return [RidePackets.BoardRollReply(1, 0, 0, [], 0, 0)]; }
        if (!boards.TryGetValue(type, out var board) || Boxes.CreatureSlots(type) is not { } rule || !board.Panels.TryGetValue(panel, out var slots))
            return [RidePackets.BoardRollReply(1, type, panel, [], 0, 0)];
        List<byte[]> Fail() => [RidePackets.BoardRollReply(1, type, panel, [], board.Level, board.Exp)];
        var chosen = slots.Where(s => targets.Contains(s.Index) && !s.Locked).ToList();
        if (chosen.Count == 0) return Fail();
        // Cost: the roll plus the lock cost for the locked slots of the panel.
        var locked = slots.Count(s => s.Locked);
        var crystalsNeeded = rule.Count + (locked > 0 && locked <= rule.Lock.Length ? rule.Lock[locked - 1] : 0);
        var gold = (long)rule.Gold + (locked > 0 && locked <= rule.LockGold.Length ? rule.LockGold[locked - 1] : 0);
        var cost = new Dictionary<int, int>();
        var rest = crystalsNeeded;
        foreach (var crystal in rule.Crystals.Where(c => c > 0).Distinct())
        {
            var take = Math.Min(crystals.GetValueOrDefault(crystal), rest);
            if (take > 0) cost[crystal] = take;
            rest -= take;
        }
        var kina = storedItems.Values.Where(i => i.TemplateId == KinaItemId && i.Location == "bag").OrderByDescending(i => i.Quantity).FirstOrDefault();
        if (rest > 0 || gold > 0 && (kina is null || kina.Quantity < gold)) return Fail();
        var odds = Boxes.CreatureLevel(type, board.Level)?.Odds ?? [10_000, 0, 0, 0, 0, 0];
        var rolled = new List<BoardSlot>();
        foreach (var slot in chosen)
        {
            var group = slot.Index < rule.Groups.Length ? rule.Groups[slot.Index] : rule.Groups.LastOrDefault();
            var grade = Pick(odds.Select((weight, i) => (i + 1, weight)).ToList());
            var options = Boxes.CreatureOptions(group ?? "").Where(o => o.Grade == grade).ToList();
            if (options.Count == 0) options = Boxes.CreatureOptions(group ?? "").Where(o => o.Grade == 1).ToList();
            if (options.Count == 0) return Fail();
            var option = options[Pick(options.Select((o, i) => (i, o.Prob)).ToList())];
            rolled.Add(slot with { Grade = (byte)option.Grade, StatType = option.StatId, Value = rng.Next(option.Min, Math.Max(option.Min, option.Max) + 1) });
        }
        var updated = board with { Panels = board.Panels.ToDictionary(p => p.Key, p => p.Key != panel ? p.Value
            : (IReadOnlyList<BoardSlot>)p.Value.Select(s => rolled.FirstOrDefault(r => r.Index == s.Index) ?? s).ToList()) };
        if (!await SaveBoardAsync(updated, cost, gold > 0 ? (kina!.InstanceId, kina.Revision, gold) : null, ct)) return Fail();
        foreach (var (crystal, amount) in cost) crystals[crystal] -= amount;
        var replies = new List<byte[]> { RidePackets.BoardRollReply(0, type, panel, rolled, updated.Level, updated.Exp) };
        if (cost.Count > 0) replies.Add(RidePackets.CrystalsSpent(cost));
        replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseCreatureBoardRoll));
        replies.AddRange(RefreshStats());
        return replies;
    }

    int Pick(IReadOnlyList<(int Value, int Weight)> weighted)
    {
        var total = weighted.Sum(w => Math.Max(0, w.Weight));
        if (total <= 0) return weighted.Count > 0 ? weighted[0].Value : 0;
        var roll = rng.Next(total);
        foreach (var (value, weight) in weighted)
            if ((roll -= Math.Max(0, weight)) < 0) return value;
        return weighted[^1].Value;
    }

    /// <summary>0x9019 CreatureBoardStatSlotLock_RQ {type u8, panel u8, slot u8, lock bool}: stored, answered 0x901a.</summary>
    async Task<List<byte[]>> LockBoardSlotAsync(byte type, byte panel, byte index, bool locked, CancellationToken ct)
    {
        if (!boards.TryGetValue(type, out var board) || !board.Panels.TryGetValue(panel, out var slots) || slots.All(s => s.Index != index))
            return [RidePackets.BoardLockReply(1, type, panel, index, locked)];
        var updated = board with { Panels = board.Panels.ToDictionary(p => p.Key, p => p.Key != panel ? p.Value
            : (IReadOnlyList<BoardSlot>)p.Value.Select(s => s.Index == index ? s with { Locked = locked } : s).ToList()) };
        return await SaveBoardAsync(updated, new Dictionary<int, int>(), null, ct)
            ? [RidePackets.BoardLockReply(0, type, panel, index, locked)] : [RidePackets.BoardLockReply(1, type, panel, index, locked)];
    }

    /// <summary>A growth voucher (Item.dat Fix_VehicleCreatureLevel [type, level]): the board reaches that level.</summary>
    async Task<List<byte[]>?> UseBoardLevelItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        if (use.Values is not [var name, var value, ..] || !CreatureTypeNames.TryGetValue(name, out var type) || !int.TryParse(value, out var level)
            || !boards.TryGetValue((byte)type, out var board)) return null;
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (used.Count == 0) return null;
        var updated = WithOpenSlots(board with { Level = Math.Clamp(Math.Max(board.Level, level), 1, Boxes.CreatureMaxLevel(type)), Exp = level > board.Level ? 0 : board.Exp });
        await SaveBoardAsync(updated, new Dictionary<int, int>(), null, ct);
        return [.. Used(row, reply), RidePackets.BoardInfo(updated), .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse)];
    }

    const byte DecreaseCreatureBoardRoll = 78;   // EDecreaseReason kCreatureBoardStatGacha
}
