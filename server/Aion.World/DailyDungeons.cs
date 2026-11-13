using System.Globalization;
using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Daily dungeons (Dungeon.dat DungeonType Daily, SubType Wave): 900001 DungeonWave_0001 "Daeva Bio-Research Base" (level 30+, the
// one open in this client), 900002 Dungeon_Daily_0001 "Odylium Repository" and 900003 Dungeon_Daily_0002 "Crobakhi's Secret
// Depository" (MapEntrance MinLevel 99 in this client: closed). Solo (MaxMember 1), ticket 101 at the entry (GameSession.Tickets.cs).
// tools/daily_dungeon_data.py -> tools/data/daily-dungeons.json:
// - the run's points (DungeonPoint.dat of the dungeon's DungeonPointGroupName): a kill of a named NPC (KillNpc), of an NPC subtype
//   (KillNpcSubType: DungeonWave_0001's elites 1000), an object used (UseEnvObj: the orbs and coins);
// - the reward (RewardDungeon.dat RewardLight/RewardDark groups): tiers by NeedPoint; the run pays the highest tier its points reach
//   (DungeonWave_0001: 100 points -> 3000 CoinEnchant ... 10000 -> 10000; below the lowest tier nothing, UI_WAVEDUNGEON_RESULT_REWARD_NONE);
// - the time limit and ready time: NOT in the client data (ProcessStateHoldingTime 0). CANDIDATE from the official WaveDungeonInfo_NT
//   0x6103 record of the recording account (capture 20261106-115817 #173809): 900001 90 s / 4170 points, 900002 60 s / 4520 points.
// Messages (usmap catalogue 0x3845-0x3852 + 0x28b7, the run of 0x6100 DungeonState_NT): GroupWaveSpawnStart_NT 0x60fc,
// WaveSpawnBonusDungeonPoint_NT 0x60fd, WaveDungeonCompleteResult_NT 0x6102, WaveDungeonInfo_NT 0x6103 (OBSERVED at every entry),
// DungeonPoint_NT 0x6104, DungeonBuffInfo_NT 0x6105, DungeonBuffSelect_RQ/RS 0x6106/0x6107.
// The buff choice (GameSession.DungeonBuffs.cs): Dungeon.dat DungeonBuffGroupName -> DungeonBuffGroup.dat slots 1-3, each with the
// DungeonBuff.dat rows it may offer (BuffType Abnormal: BuffValue1 the abnormal); only DungeonWave_0001 has a group in this client.

/// <summary>One daily dungeon's run rules.</summary>
public sealed record DailyDungeon(int Id, string Name, int Map, int LimitS, int ReadyS, int HoldS, int WaveId,
    IReadOnlyDictionary<int, int> NpcPoints, IReadOnlyDictionary<int, int> EnvPoints,
    IReadOnlyList<DailyRewardTier> RewardsLight, IReadOnlyList<DailyRewardTier> RewardsDark)
{
    /// <summary>The buff choice: per BuffSlot (1-3) the DungeonBuff rows it may offer (none: the dungeon offers no buff).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<DungeonBuffRow>> BuffSlots { get; init; } = new Dictionary<int, IReadOnlyList<DungeonBuffRow>>();
}

/// <summary>A DungeonBuff.dat row of type Abnormal: its id (DungeonBuffInfo buff_table_id), the abnormal it gives, its grade.</summary>
public sealed record DungeonBuffRow(int Id, int AbnormalId, string Grade);

/// <summary>A RewardDungeon.dat row: the points it needs and its items.</summary>
public sealed record DailyRewardTier(int NeedPoint, int RowId, IReadOnlyList<(int ItemId, long Count)> Items);

/// <summary>A character's best run of a daily dungeon (WaveDungeonInfo_NT): grade, duration in seconds, points.</summary>
public sealed record DailyRecord(int Dungeon, int Grade, int DurationS, int Point);

public sealed class DailyDungeons
{
    public const ushort WaveDungeonCompleteResultNt = 0x6102, WaveDungeonInfoNt = 0x6103, DungeonPointNt = 0x6104,
        DungeonBuffInfoNt = 0x6105, DungeonBuffSelectRq = 0x6106, DungeonBuffSelectRs = 0x6107;

    readonly Dictionary<int, DailyDungeon> byId = [];
    readonly Dictionary<int, DailyDungeon> byMap = [];

    DailyDungeons(JsonElement root)
    {
        foreach (var p in root.GetProperty("dungeons").EnumerateObject())
        {
            var d = p.Value;
            static Dictionary<int, int> Points(JsonElement e) =>
                e.EnumerateObject().ToDictionary(x => int.Parse(x.Name, CultureInfo.InvariantCulture), x => x.Value.GetInt32());
            static List<DailyRewardTier> Tiers(JsonElement e) =>
                e.EnumerateArray().Select(t => new DailyRewardTier(t[0].GetInt32(), t[1].GetInt32(),
                    t[2].EnumerateArray().Select(i => (i[0].GetInt32(), i[1].GetInt64())).ToList())).OrderBy(t => t.NeedPoint).ToList();
            var dungeon = new DailyDungeon(int.Parse(p.Name, CultureInfo.InvariantCulture), d.GetProperty("name").GetString() ?? "", d.GetProperty("map").GetInt32(),
                d.GetProperty("limit_s").GetInt32(), d.GetProperty("ready_s").GetInt32(), d.GetProperty("hold_s").GetInt32(), d.GetProperty("wave_id").GetInt32(),
                Points(d.GetProperty("npc_points")), Points(d.GetProperty("env_points")), Tiers(d.GetProperty("rewards_light")), Tiers(d.GetProperty("rewards_dark")))
            {
                BuffSlots = d.TryGetProperty("buff_slots", out var slots)
                    ? slots.EnumerateObject().ToDictionary(x => int.Parse(x.Name, CultureInfo.InvariantCulture),
                        x => (IReadOnlyList<DungeonBuffRow>)x.Value.EnumerateArray().Select(b => new DungeonBuffRow(b[0].GetInt32(), b[1].GetInt32(), b[2].GetString() ?? "")).ToList())
                    : new Dictionary<int, IReadOnlyList<DungeonBuffRow>>(),
            };
            byId[dungeon.Id] = dungeon;
            byMap.TryAdd(dungeon.Map, dungeon);
        }
    }

    static readonly Lazy<DailyDungeons> embedded = new(() =>
    {
        using var stream = typeof(DailyDungeons).Assembly.GetManifestResourceStream("daily-dungeons.json")
            ?? throw new InvalidOperationException("daily-dungeons.json is not embedded");
        using var json = JsonDocument.Parse(stream);
        return new DailyDungeons(json.RootElement);
    });

    public static DailyDungeons Embedded => embedded.Value;
    public IReadOnlyCollection<DailyDungeon> All => byId.Values;
    public DailyDungeon? Of(int dungeonId) => byId.GetValueOrDefault(dungeonId);
    public DailyDungeon? OfMap(int mapId) => byMap.GetValueOrDefault(mapId);

    /// <summary>The points a kill of this NPC is worth in the dungeon (0: none).</summary>
    public static int KillPoints(DailyDungeon d, int npcId) => d.NpcPoints.GetValueOrDefault(npcId);
    /// <summary>The points using this object (EnvObjData id) is worth in the dungeon.</summary>
    public static int UsePoints(DailyDungeon d, int envId) => d.EnvPoints.GetValueOrDefault(envId);

    /// <summary>The reward of a run with these points for a race (2 = Asmodian): the highest tier reached, or null below the lowest.</summary>
    public static DailyRewardTier? Reward(DailyDungeon d, int race, int points) =>
        (race == 2 ? d.RewardsDark : d.RewardsLight).LastOrDefault(t => points >= t.NeedPoint);

    // ---------- Messages ----------

    /// <summary>
    /// DungeonBuffInfo_NT 0x6105 {dungeon i32, buff infos [{slot i32, buff_table_id i32}]} (usmap fields, plain, the count a varint as
    /// every array; CANDIDATE: never recorded). buff_table_id = the DungeonBuff.dat id the slot offers.
    /// </summary>
    public static byte[] DungeonBuffInfo(int dungeonId, IEnumerable<(int Slot, int BuffId)> offered)
    {
        var list = offered.OrderBy(o => o.Slot).ToList();
        var w = PacketWriter.Opcode(DungeonBuffInfoNt).I32(dungeonId).Varint(list.Count);
        foreach (var (slot, buff) in list) w.I32(slot).I32(buff);
        return w.ToArray();
    }

    /// <summary>DungeonBuffSelect_RS 0x6107 {result, dungeon i32, slot i32} (usmap fields; the result u16 as every observed RS; CANDIDATE).</summary>
    public static byte[] DungeonBuffSelectReply(int result, int dungeonId, int slot) =>
        PacketWriter.Opcode(DungeonBuffSelectRs).U16(result).I32(dungeonId).I32(slot).ToArray();

    /// <summary>DungeonPoint_NT 0x6104 {dungeon i32, point i32} (usmap fields, plain; CANDIDATE: never recorded).</summary>
    public static byte[] DungeonPoint(int dungeonId, int points) => PacketWriter.Opcode(DungeonPointNt).I32(dungeonId).I32(points).ToArray();

    /// <summary>
    /// WaveDungeonInfo_NT 0x6103: the character's records, each {mask u8 | dungeon i32 | bit0 grade u8 | bit1 duration_sec varint |
    /// bit2 point varint}. OBSERVED (capture 20261106-115817 #173809 and the 2026-11-07 entries): `02 07a1bb0d00 01 5a ca20 07a2bb0d00 01
    /// 3c a823` = 900001 grade 1, 90 s, 4170 points; 900002 grade 1, 60 s, 4520 points; `00` for a character with none.
    /// </summary>
    public static byte[] WaveDungeonInfo(IEnumerable<DailyRecord> records)
    {
        var list = records.OrderBy(r => r.Dungeon).ToList();
        var w = PacketWriter.Opcode(WaveDungeonInfoNt).Varint(list.Count);
        foreach (var r in list)
        {
            var mask = (byte)((r.Grade != 0 ? 1 : 0) | (r.DurationS != 0 ? 2 : 0) | (r.Point != 0 ? 4 : 0));
            w.U8(mask).I32(r.Dungeon);
            if ((mask & 1) != 0) w.U8((byte)r.Grade);
            if ((mask & 2) != 0) w.Varint(r.DurationS);
            if ((mask & 4) != 0) w.Varint(r.Point);
        }
        return w.ToArray();
    }

    /// <summary>
    /// WaveDungeonCompleteResult_NT 0x6102 {dungeon, grade u8, duration, total_point, bonus_point, rewards map&lt;item i32, count i64&gt;,
    /// sweep_count} (usmap fields). CANDIDATE form, never recorded: the presence mask of its sibling WaveDungeonInfo_NT (OBSERVED: a mask
    /// byte first, the dungeon always, the optional numbers as varints) and the reward map of DungeonCompleteResult_NT (OBSERVED: count,
    /// then item i32 + count i64). Mask bits: 0 grade, 1 duration (ms), 2 total, 3 bonus, 4 sweep count.
    /// </summary>
    public static byte[] WaveDungeonCompleteResult(int dungeonId, int grade, long durationMs, int total, int bonus, IReadOnlyList<(int ItemId, long Count)> rewards, int sweeps = 0)
    {
        var mask = (byte)((grade != 0 ? 1 : 0) | (durationMs != 0 ? 2 : 0) | (total != 0 ? 4 : 0) | (bonus != 0 ? 8 : 0) | (sweeps != 0 ? 16 : 0));
        var w = PacketWriter.Opcode(WaveDungeonCompleteResultNt).U8(mask).I32(dungeonId);
        if ((mask & 1) != 0) w.U8((byte)grade);
        if ((mask & 2) != 0) w.Varint(durationMs);
        if ((mask & 4) != 0) w.Varint(total);
        if ((mask & 8) != 0) w.Varint(bonus);
        w.Varint(rewards.Count);
        foreach (var (item, count) in rewards) w.I32(item).I64(count);
        if ((mask & 16) != 0) w.Varint(sweeps);
        return w.ToArray();
    }
}
