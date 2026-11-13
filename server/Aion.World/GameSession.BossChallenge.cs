using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Boss challenge data (tools/data/bosschallenge.json, tools/bosschallenge_data.py).</summary>
public sealed class BossChallengeTables
{
    public sealed record Group([property: JsonPropertyName("n")] string Name, [property: JsonPropertyName("r")] int Race, [property: JsonPropertyName("tier")] int Tier, [property: JsonPropertyName("max")] int MaxLevel)
    {
        public int Id { get; set; }
    }
    public sealed record Stage([property: JsonPropertyName("npc")] int Npc, [property: JsonPropertyName("power")] int Power,
        [property: JsonPropertyName("first")] long[][] First, [property: JsonPropertyName("repeat")] long[][] Repeat);
    sealed record File([property: JsonPropertyName("dungeon")] int Dungeon, [property: JsonPropertyName("map")] int Map, [property: JsonPropertyName("spawn")] double[]? Spawn,
        [property: JsonPropertyName("groups")] Dictionary<string, Group> Groups, [property: JsonPropertyName("contents")] Dictionary<string, Dictionary<string, Stage>> Contents,
        [property: JsonPropertyName("seasons")] Dictionary<string, string[]> Seasons);

    readonly File file;
    readonly Dictionary<int, Group> byId = [];
    BossChallengeTables(File file)
    {
        this.file = file;
        foreach (var (key, g) in file.Groups) { g.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture); byId[g.Id] = g; }
    }

    static readonly Lazy<BossChallengeTables> embedded = new(() =>
    {
        using var stream = typeof(BossChallengeTables).Assembly.GetManifestResourceStream("bosschallenge.json") ?? throw new InvalidOperationException("bosschallenge.json is not embedded");
        return new BossChallengeTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("bosschallenge.json"));
    });
    public static BossChallengeTables Embedded => embedded.Value;

    public int Dungeon => file.Dungeon;
    public int Map => file.Map;
    public double[]? Spawn => file.Spawn;
    public Group? GroupOf(int id) => byId.GetValueOrDefault(id);
    public IEnumerable<Group> Groups => byId.Values;
    public Stage? StageOf(Group group, int level) => file.Contents.GetValueOrDefault(group.Name)?.GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// Boss challenges: a ladder of solo boss fights (26 groups of 10 levels in BossChallengeContents.dat) in the arena dungeon
// 200003. The 0xe2 run: BossChallengeList_NT 0xe237 at world entry (OBSERVED empty, `00`, for a new character),
// BossChallengeDungeonEnter 0xe234/0xe235 (+ 0xe236 notice), UpdateBossChallenge_NT 0xe238 on a clear (usmap bodies;
// Common_BossChallenge = {season key {ESeasonContentsType u8 BossChallenge 3, season group, season no}, boss group, group,
// clear level, best point i64, best point level, best point time, best point update time i64, cumulative point i64}; CANDIDATE).
// Entering (group, level ≤ cleared + 1 and ≤ MaxLevel) opens a private copy with the level's boss at the map's BossSpawn
// point; killing it clears the level: the first-clear reward once, the repeat reward (grade 0) every time, clear points
// 4000 x (tier - 1) + 300 x (level + 1) - seconds (OBSERVED in the official ranking, feature/rank-system) added to the season's
// boss challenge ranking, ClearBossChallenge achievements, and the way out after the dungeon's 30 s hold. The ticket
// Contents_Ticket_BossChallenge (4; MapEntrance Enter_BossChallenge_Step_*: 1, DeductType Clear, checked at the entry) is taken
// on the clear (OBSERVED 0x610c after an official clear, GameSession.Tickets.cs). The grade multipliers are not modelled.
public sealed partial class GameSession
{
    public const ushort BossChallengeEnterRq = 0xe234, BossChallengeEnterRs = 0xe235, BossChallengeEnterNt = 0xe236, BossChallengeListNt = 0xe237, UpdateBossChallengeNt = 0xe238;
    public const int BossChallengeKey = 950_000;
    const byte SeasonContentsBossChallenge = 3;
    static BossChallengeTables BossChallenges => BossChallengeTables.Embedded;

    readonly Dictionary<int, BossChallengeRecord> bossChallenges = [];
    /// <summary>The challenge being fought: group, level, when it began (0: none).</summary>
    (int Group, int Level, long StartMs) activeChallenge;
    bool challengeBossPlaced;

    void LoadBossChallenges(IReadOnlyList<BossChallengeRecord> stored)
    {
        bossChallenges.Clear();
        foreach (var r in stored) bossChallenges[r.GroupId] = r;
    }

    static PacketWriter WriteBossChallenge(PacketWriter w, BossChallengeRecord r) =>
        w.U8(SeasonContentsBossChallenge).I32(0).I32(0).I32(r.GroupId).I32(r.GroupId).I32(r.ClearLevel).I64(r.BestPoint).I32(r.BestLevel).I32(r.BestTimeMs)
            .I64(r.UpdatedAt.ToUnixTimeMilliseconds()).I64(r.CumulativePoint);

    byte[] BossChallengeList()
    {
        var w = PacketWriter.Opcode(BossChallengeListNt).Varint((uint)bossChallenges.Count);
        foreach (var r in bossChallenges.Values) WriteBossChallenge(w, r);
        return w.ToArray();
    }

    /// <summary>0xe234 BossChallengeDungeonEnter_RQ {group, level}: into the arena with the level's boss.</summary>
    List<byte[]> BossChallengeEnter(byte[] plain, long now)
    {
        if (plain.Length < 10) return [];
        var groupId = BitConverter.ToInt32(plain, 2);
        var level = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => PacketWriter.Opcode(BossChallengeEnterRs).U16(result).I32(groupId).I32(level).ToArray();
        var group = BossChallenges.GroupOf(groupId);
        var cleared = bossChallenges.GetValueOrDefault(groupId)?.ClearLevel ?? 0;
        if (group is null || (group.Race != 0 && group.Race != profileRace) || level < 1 || level > group.MaxLevel || level > cleared + 1 || BossChallenges.StageOf(group, level) is null
            || instanceKey != 0 || BossChallenges.Spawn is null)
            return [Reply(1)];
        if (BossChallengeTicket is { } ticket && TicketCount(ticket, now) < 1) return [Reply(EResult.BossChallenge_Fail_NotEnoughTicketCount)];
        var moved = EnterDungeon(BossChallenges.Dungeon, now);
        if (moved.Count == 0) return [Reply(1)];
        activeChallenge = (groupId, level, now);
        challengeBossPlaced = false;
        return [Reply(0), PacketWriter.Opcode(BossChallengeEnterNt).I32(groupId).I32(level).ToArray(), .. moved];
    }

    /// <summary>
    /// Nightmare boss strength per challenge level (PROVISIONAL, tunable). All 10 levels of a group use NPCs with the same template
    /// (e.g. 2980040-2980049: level 45 HeroMonster grade 5 = ~532,000 HP and 5,000-15,000 per hit), so level 1 hit as hard as
    /// level 10 (user 2026-11-08: "too strong for the 1000 combat power"). The level's recommended power (BossChallengeContents
    /// power: 1000 at Pinopi level 1 up to 3000+ in later layers) now scales the boss: at power 1000 half the HP and 40% of the
    /// damage, rising in proportion to the power.
    /// </summary>
    public const double ChallengeHpFactor = 0.5, ChallengeDamageFactor = 0.4, ChallengeBasePower = 1000;

    /// <summary>The level's difficulty against the first level of the first layer (power / 1000, at least 0.5).</summary>
    internal static double ChallengeDifficulty(int power) => Math.Max(0.5, power / ChallengeBasePower);

    /// <summary>After the arena's targets are set: the level's boss at the BossSpawn point (once per challenge).</summary>
    void PlaceChallengeBoss()
    {
        if (activeChallenge.Group == 0 || challengeBossPlaced || worldMap != BossChallenges.Map || targets is null || BossChallenges.Spawn is not { Length: >= 4 } at) return;
        var group = BossChallenges.GroupOf(activeChallenge.Group);
        if (group is null || BossChallenges.StageOf(group, activeChallenge.Level) is not { } stage || data.NpcFromTemplate(stage.Npc, $"BossChallenge_{group.Id}_{activeChallenge.Level}", at[..3], at[3]) is not { } spawn) return;
        var difficulty = ChallengeDifficulty(stage.Power);
        var hp = Math.Max(1, (int)Math.Round(Combat.HpFor(spawn) * ChallengeHpFactor * difficulty));
        targets[BossChallengeKey] = new Target
        {
            Key = BossChallengeKey, Name = spawn.Name, Hp = hp, HpMax = hp, Position = new Vector3d(at[0], at[1], at[2]), NpcId = spawn.NpcId, Level = spawn.Level, Grade = spawn.Grade,
            Direction = at[3], Relationship = 6, Aggressive = true, WalkSpeed = spawn.WalkSpeed, RunSpeed = spawn.RunSpeed, Radius = spawn.Radius, Function = spawn.Function,
            Aggro = spawn.Aggro, Skills = spawn.Skills ?? [], NoRotation = spawn.NoRotation, Air = spawn.Air, AirMonster = spawn.Air && spawn.SubType.EndsWith("Monster", StringComparison.Ordinal), DamageScale = Combat.DamageScale(spawn.SubType, spawn.Grade) * ChallengeDamageFactor * difficulty, Rank = NpcTactics.RankOf(spawn.SubType),
        };
        challengeBossPlaced = true;
    }

    /// <summary>A kill in the arena: the challenge boss clears the level.</summary>
    List<byte[]> BossChallengeKilled(Target target, long now)
    {
        if (activeChallenge.Group == 0 || target.Key != BossChallengeKey || BossChallenges.GroupOf(activeChallenge.Group) is not { } group || BossChallenges.StageOf(group, activeChallenge.Level) is not { } stage) return [];
        var (groupId, level, startMs) = activeChallenge;
        activeChallenge = default;
        var elapsed = (int)Math.Max(0, now - startMs);
        // The official clear points (OBSERVED, GameSession.Ranking.cs BossClearPoints): by the group's tier, the level and the seconds.
        var points = BossClearPoints(group.Tier, level, elapsed / 1000);
        var before = bossChallenges.GetValueOrDefault(groupId) ?? new BossChallengeRecord(groupId, 0, 0, 0, 0, 0, DateTimeOffset.FromUnixTimeMilliseconds(now));
        var first = level > before.ClearLevel;
        var record = before with
        {
            ClearLevel = Math.Max(before.ClearLevel, level),
            BestPoint = Math.Max(before.BestPoint, points), BestLevel = points >= before.BestPoint ? level : before.BestLevel, BestTimeMs = points >= before.BestPoint ? elapsed : before.BestTimeMs,
            CumulativePoint = before.CumulativePoint + points, UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(now),
        };
        bossChallenges[groupId] = record;
        pendingBossChallengeSave = record;
        AddClearPoints(SeasonContentsBossChallenge, points, System.Text.Json.JsonSerializer.Serialize(new
            { boss_group_id = groupId, best_clear_point_level = level, best_clear_point_time = elapsed / 1000, best_clear_point = points }));
        var rewards = (first ? stage.First : []).Concat(stage.Repeat).Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList();
        if (rewards.Count > 0) pendingRewards.Add(rewards);
        var replies = new List<byte[]> { WriteBossChallenge(PacketWriter.Opcode(UpdateBossChallengeNt).U16(0).I64(points).I32(elapsed), record).U8(0).ToArray() };
        if (BossChallengeTicket is { } ticket && UseTicket(ticket, 1, now) is { } used) replies.Add(used);
        replies.AddRange(Achieve("ClearBossChallenge", 1, e => e.Values.Length >= 2 && e.Has(group.Name) && int.TryParse(e.Values[1], out var need) && level >= need, now));
        var hold = MapEventData.DungeonOfMap(worldMap) is { Hold: > 0 } room ? room.Hold * 1000L : 30_000;
        instanceExitAt ??= now + hold;
        return replies;
    }

    BossChallengeRecord? pendingBossChallengeSave;

    /// <summary>The boss challenge's entry ticket (ContentsTicket.dat Contents_Ticket_BossChallenge, id 4).</summary>
    static int? BossChallengeTicket => Tickets.Named("Contents_Ticket_BossChallenge")?.Id;

    async Task FlushBossChallengeAsync(CancellationToken ct)
    {
        if (pendingBossChallengeSave is not { } record || Store is not { } store || CharacterId is not { } character) return;
        pendingBossChallengeSave = null;
        await Stored(async () => await store.SaveBossChallengeAsync(character, record, ct) ? [[]] : []);
    }
}
