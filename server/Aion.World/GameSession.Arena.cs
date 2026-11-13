using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>An arena match (Matching.dat Single 1v1 / Team 5v5 on a MatchingContents arena dungeon): teams, lives, scores.</summary>
public sealed class ArenaMatch(int roomKey, int matchingId, int contentsId, int dungeonId, int mapId, int lives, long playTimeMs)
{
    public const byte Blue = 1, Red = 2;
    public int RoomKey { get; } = roomKey;
    public int MatchingId { get; } = matchingId;
    public int ContentsId { get; } = contentsId;
    public int DungeonId { get; } = dungeonId;
    public int MapId { get; } = mapId;
    public int Lives { get; } = lives;
    public long PlayTimeMs { get; } = playTimeMs;
    /// <summary>MatchingContents RespawnTime (5 s arenas, 10 s battlefields).</summary>
    public long RespawnMs { get; init; } = 5_000;
    public List<WorldPlayer> BlueTeam { get; } = [];
    public List<WorldPlayer> RedTeam { get; } = [];
    public HashSet<int> Accepted { get; } = [];
    public HashSet<int> Ready { get; } = [];
    public Dictionary<int, (int Kills, int Deaths, int Life)> Scores { get; } = [];
    public Dictionary<int, long> RespawnAt { get; } = [];
    /// <summary>0 pending accept, 1 entering, 2 ready wait, 3 battle, 4 done.</summary>
    public int State { get; set; }
    public long NextMs { get; set; }
    public long StartMs { get; set; }
    public IEnumerable<WorldPlayer> Players => BlueTeam.Concat(RedTeam);
    public WorldPlayer Owner => BlueTeam.Count > 0 ? BlueTeam[0] : RedTeam[0];
    public byte TeamOf(WorldPlayer p) => BlueTeam.Contains(p) ? Blue : RedTeam.Contains(p) ? Red : (byte)0;
    public bool Has(WorldPlayer p) => BlueTeam.Contains(p) || RedTeam.Contains(p);
    public bool Enemies(WorldPlayer a, WorldPlayer b) => TeamOf(a) != 0 && TeamOf(b) != 0 && TeamOf(a) != TeamOf(b);
}

public sealed record ArenaQueueEntry(WorldPlayer Player, int MatchingId, long SinceMs);

// Arena (Matching.dat / MatchingContents.dat / RewardArena.dat): Single (1v1, two arenas: Fire Temple 7 / Impetusium 8, 3
// lives, 10 minutes) and Team (5v5, one life). The 0xe2 run (index + 0xdd07): MatchingStateChange_NT 0xe2a8,
// MatchingStart 0xe2a9/0xe2aa, MatchingStartResponse 0xe2ab/0xe2ac, MatchingCancel 0xe2ad/0xe2ae, MatchingRoomInit_NT
// 0xe2b2, ArenaDungeonReady 0xe2b3/0xe2b4/0xe2b5, ArenaDungeonReadyEnd_NT 0xe2b6, ArenaDungeonKill_NT 0xe2be,
// ArenaDungeonScoreUpdate_NT 0xe2bf, ArenaDungeonResult_NT 0xe2c1, ArenaRecord 0xe2c4/0xe2c5 (usmap bodies, CANDIDATE).
// Flow (OURS, from the message names): queue → enough players → PendingReady → everyone accepts → Complete + room info
// → each session moves into the match's copy of the arena map at its team's start point → ready (or 30 s) → battle
// (PvP damage as duels, Skill.dat PvP rows) → a death costs a life, a 5 s respawn at the team point while lives remain →
// the match ends when a team is out of lives or at the time limit (more kills wins, equal is a draw) → result, RewardArena
// (join and win: Abyss Points) → out after 10 s. Ranking points, penalties, schedules and the Strategy modes are not modelled.
public sealed partial class GameSession
{
    // Live 0xe3-block offset +0xa762 (ArenaRanking_NT 0xe32a and ArenaRecord_RS 0xe32c OBSERVED). These were the catalogue index
    // + 0xdd07 before (0x67 lower), which the real client never used: matching, ready, kills, scores, results, records.
    public const ushort MatchingStateChangeNt = 0xe30f, MatchingStartRq = 0xe310, MatchingStartRs = 0xe311, MatchingStartResponseRq = 0xe312, MatchingStartResponseRs = 0xe313,
        MatchingCancelRq = 0xe314, MatchingCancelRs = 0xe315, MatchingRoomInitNt = 0xe319, ArenaDungeonReadyRq = 0xe31a, ArenaDungeonReadyRs = 0xe31b, ArenaDungeonReadyNt = 0xe31c,
        ArenaDungeonReadyEndNt = 0xe31d, ArenaDungeonKillNt = 0xe325, ArenaDungeonScoreUpdateNt = 0xe326, ArenaDungeonResultNt = 0xe328, ArenaRecordRq = 0xe32b, ArenaRecordRs = 0xe32c;
    const byte MatchingStateMatching = 2, MatchingStatePendingReady = 3, MatchingStateComplete = 4;
    public const long ArenaReadyWaitMs = 30_000, ArenaRespawnMs = 5_000, ArenaExitMs = 10_000, ArenaPlayTimeMs = 600_000;
    public const int AbyssPointItemId = 930100003;
    // Table-driven since feature/systems-2 (Systems2Tables): Matching.dat team sizes (Single 1, Team 5, Strategy 10 = the battlefield,
    // GameSession.Battlefield.cs), its MatchingContents pick by Prob (dungeon, map, play time, lives 0 = unlimited, respawn time),
    // RewardArena.dat join/win items, MatchingSchedule windows (group 100: the battlefield).

    /// <summary>Matching.dat: team size per matching id (Single 1, Team 5, Strategy 10); 0 = no such matching (or the hidden party one).</summary>
    internal static int ArenaTeamSize(int matchingId) => Systems2Tables.Embedded.MatchingOf(matchingId) is { Hidden: false } m ? m.TeamSize : 0;

    /// <summary>Matching.dat ContentsInfos: a MatchingContents row by its Prob weights.</summary>
    Systems2Tables.MatchingContents? PickArenaContents(int matchingId)
    {
        if (Systems2Tables.Embedded.MatchingOf(matchingId) is not { Contents.Count: > 0 } m) return null;
        var roll = rng.Next(Math.Max(1, m.Contents.Sum(c => c.Prob)));
        foreach (var c in m.Contents)
        {
            if (roll < c.Prob) return Systems2Tables.Embedded.ContentsOf(c.Id);
            roll -= c.Prob;
        }
        return Systems2Tables.Embedded.ContentsOf(m.Contents[^1].Id);
    }

    /// <summary>RewardArena.dat items of the matching's join (and win) reward, as item ids.</summary>
    internal static List<(int Item, long Count)> ArenaRewardItems(int matchingId, bool won)
    {
        var items = new List<(int, long)>();
        if (Systems2Tables.Embedded.MatchingOf(matchingId) is not { } m) return items;
        foreach (var name in won ? new[] { m.RewardJoin, m.RewardWin } : [m.RewardJoin])
            foreach (var i in Systems2Tables.Embedded.ArenaReward(name))
                if (Systems2Tables.Embedded.ItemId(i.Item) is var id and > 0) items.Add((id, i.Count));
        return items;
    }

    /// <summary>MatchingSchedule.dat: a matching with a schedule group is open only inside its day's windows (UTC, CANDIDATE time zone).</summary>
    internal static bool MatchingOpen(int matchingId, long now)
    {
        if (Systems2Tables.Embedded.MatchingOf(matchingId) is not { ScheduleGroup: > 0 } m) return true;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
        var ms = (long)at.TimeOfDay.TotalMilliseconds;
        return Systems2Tables.Embedded.MatchingSchedules.Any(s => s.Group == m.ScheduleGroup && s.Day == at.DayOfWeek.ToString() && s.Windows.Any(w => ms >= w[0] && ms < w[1]));
    }

    ArenaMatch? ArenaOf(WorldPlayer p) => Social?.Arenas.Values.FirstOrDefault(a => a.Has(p));

    byte[] MatchingState(int matchingId, byte state) => PacketWriter.Opcode(MatchingStateChangeNt).I32(matchingId).U8(state).ToArray();

    /// <summary>0xe2a9 MatchingStart_RQ {matching id}: into the queue; a full set of players opens a match pending everyone's acceptance.</summary>
    List<byte[]> MatchingStart(byte[] plain, long now)
    {
        if (plain.Length < 6) return [];
        var matchingId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(MatchingStartRs).U16(result).I32(matchingId).ToArray();
        var size = ArenaTeamSize(matchingId);
        if (size > 0 && !IsAdmin && !MatchingOpen(matchingId, now)) return [Reply(EResult.ContentsBlock_ArenaMatching)];
        if (size == 0 || Social is not { } s || worldPlayer is null || instanceKey != 0 || ArenaOf(worldPlayer) is not null || s.ArenaQueue.Any(q => q.Player == worldPlayer)) return [Reply(1)];
        s.ArenaQueue.Add(new ArenaQueueEntry(worldPlayer, matchingId, now));
        var replies = new List<byte[]> { Reply(0), MatchingState(matchingId, MatchingStateMatching) };
        var waiting = s.ArenaQueue.Where(q => q.MatchingId == matchingId).OrderBy(q => q.SinceMs).Take(size * 2).ToList();
        if (waiting.Count < size * 2) return replies;
        foreach (var entry in waiting) s.ArenaQueue.Remove(entry);
        if (PickArenaContents(matchingId) is not { } contents)
        {
            foreach (var entry in waiting) s.ArenaQueue.Add(entry);
            return replies;
        }
        var match = new ArenaMatch((int)s.NextId(), matchingId, contents.Id, contents.Dungeon, contents.Map, contents.Lives, contents.PlayMs > 0 ? contents.PlayMs : ArenaPlayTimeMs)
            { NextMs = now + ArenaReadyWaitMs, RespawnMs = contents.RespawnMs > 0 ? contents.RespawnMs : ArenaRespawnMs };
        for (var i = 0; i < waiting.Count; i++) (i % 2 == 0 ? match.BlueTeam : match.RedTeam).Add(waiting[i].Player);
        s.Arenas[match.RoomKey] = match;
        foreach (var p in match.Players)
            if (p == worldPlayer) replies.Add(MatchingState(matchingId, MatchingStatePendingReady)); else p.Send(MatchingState(matchingId, MatchingStatePendingReady));
        return replies;
    }

    /// <summary>0xe2ab MatchingStartResponse_RQ {matching id, accept}: the last acceptance starts the match; a refusal returns the others to the queue.</summary>
    List<byte[]> MatchingStartResponse(byte[] plain, long now)
    {
        if (plain.Length < 7) return [];
        var matchingId = BitConverter.ToInt32(plain, 2);
        var accept = plain[6] != 0;
        byte[] Reply(int result) => PacketWriter.Opcode(MatchingStartResponseRs).U16(result).I32(matchingId).Bool(accept).ToArray();
        if (Social is not { } s || worldPlayer is null || ArenaOf(worldPlayer) is not { State: 0 } match) return [Reply(1)];
        if (!accept)
        {
            s.Arenas.Remove(match.RoomKey);
            foreach (var p in match.Players.Where(p => p != worldPlayer))
            {
                s.ArenaQueue.Add(new ArenaQueueEntry(p, matchingId, now));
                p.Send(MatchingState(matchingId, MatchingStateMatching));
            }
            return [Reply(0)];
        }
        match.Accepted.Add(playerKey);
        if (match.Accepted.Count < match.Players.Count()) return [Reply(0)];
        match.State = 1;
        match.NextMs = now + ArenaReadyWaitMs;
        foreach (var p in match.Players)
        {
            match.Scores[p.Key] = (0, 0, match.Lives);
            var session = p.Session;
            p.Send([MatchingState(matchingId, MatchingStateComplete), MatchingRoomInit(match)]);
            _ = session.InvokeAsync(at => (true, session.EnterArena(match, at)));
        }
        return [Reply(0)];
    }

    byte[] MatchingRoomInit(ArenaMatch match)
    {
        var w = PacketWriter.Opcode(MatchingRoomInitNt).I32(match.RoomKey).I32(match.MatchingId).I32(match.ContentsId).I32(0).I32(match.DungeonId).Varint(2);
        foreach (var (team, members) in new[] { (ArenaMatch.Blue, match.BlueTeam), (ArenaMatch.Red, match.RedTeam) })
        {
            w.U8(team).I64(0).I32(0).Varint((uint)members.Count);
            foreach (var m in members)
            {
                var ms = m.Session;
                w.I64(ms.DbId).I64(ms.DbId).String(ms.StoredName).I32(ms.Profile()?.PcId ?? 1).U8((byte)ms.profileRace).I32(ms.level).I32(0).I32(ms.ServerId).I64(0)
                    .U8((byte)match.MatchingId).I64(0).I32(0).I32(0).I32(0).I32(0).I32(0);
            }
        }
        return w.ToArray();
    }

    (Vector3d Start, Vector3d Respawn)? ArenaPoints(ArenaMatch match, byte team)
    {
        var points = data.MapPoints(match.MapId);
        // Arenas name their sides _A_/_B_ (Fire_Temple_Arena_A_Teleport), battlefields _BLUE_/_RED_ (Battlefield_A_1_BLUE_Teleport).
        string[] sides = team == ArenaMatch.Blue ? ["_A_", "_BLUE_"] : ["_B_", "_RED_"];
        Vector3d? Find(string suffix) => points.FirstOrDefault(p => sides.Any(side => p.Key.Contains(side + suffix, StringComparison.OrdinalIgnoreCase))).Value is { Length: >= 3 } v
            ? new Vector3d(v[0], v[1], v[2]) : null;
        var start = Find("Teleport") ?? Find("Respawn");
        return start is null ? null : (start.Value, Find("Respawn") ?? start.Value);
    }

    /// <summary>On the player's own loop: into the match's copy of the arena map at the team's start point.</summary>
    List<byte[]> EnterArena(ArenaMatch match, long now)
    {
        if (resources is null || worldPlayer is null || reloading || ArenaPoints(match, match.TeamOf(worldPlayer)) is not { } points) return [];
        instanceReturn = (worldMap, position ?? spawn?.Position ?? default);
        instanceKey = match.RoomKey;
        instanceExitAt = null;
        if (dead) { dead = false; (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax); }
        return [PlayerPackets.CharDisappear(playerKey, InstanceReason, InstanceEffect), .. Teleport(match.MapId, points.Start, now, InstanceReason, InstanceEffect)];
    }

    /// <summary>0xe2b3 ArenaDungeonReady_RQ {dbid}: ready; everyone ready (or the wait over) starts the battle.</summary>
    List<byte[]> ArenaReady(byte[] plain, long now)
    {
        if (Social is null || worldPlayer is null || ArenaOf(worldPlayer) is not { State: 1 or 2 } match) return [PacketWriter.Opcode(ArenaDungeonReadyRs).U16(1).ToArray()];
        match.State = 2;
        match.Ready.Add(playerKey);
        var ready = PacketWriter.Opcode(ArenaDungeonReadyNt).Varint((uint)match.Ready.Count);
        foreach (var key in match.Ready) ready.I64(key);
        var notice = ready.ToArray();
        foreach (var p in match.Players.Where(p => p != worldPlayer)) p.Send(notice);
        var replies = new List<byte[]> { PacketWriter.Opcode(ArenaDungeonReadyRs).U16(0).ToArray(), notice };
        if (match.Ready.Count >= match.Players.Count()) StartArenaBattle(match, now);
        return replies;
    }

    void StartArenaBattle(ArenaMatch match, long now)
    {
        match.State = 3;
        match.StartMs = now;
        match.NextMs = now + match.PlayTimeMs;
        var start = PacketWriter.Opcode(ArenaDungeonReadyEndNt).I64(now).ToArray();
        foreach (var p in match.Players) p.Send(start);
    }

    byte[] ArenaScores(ArenaMatch match, ushort opcode = ArenaDungeonScoreUpdateNt)
    {
        var w = PacketWriter.Opcode(opcode).Varint((uint)match.Scores.Count);
        foreach (var p in match.Players)
        {
            var (kills, deaths, life) = match.Scores.GetValueOrDefault(p.Key);
            w.I64(p.Session.DbId).Bool(false).Bool(false).U8((byte)kills).U8((byte)deaths).U8(0).U8((byte)life);
        }
        return w.ToArray();
    }

    /// <summary>A cast at an enemy in a running match (called when no duel claims the target): PvP damage; at 1 HP a death.</summary>
    List<byte[]> ArenaHits(int skill, int targetKey, long now)
    {
        if (worldPlayer is null || ArenaOf(worldPlayer) is not { State: 3 } match) return [];
        var victim = match.Players.FirstOrDefault(p => p.Key == targetKey);
        if (victim is null || !match.Enemies(worldPlayer, victim) || victim.Session.resources is not { } theirs || match.RespawnAt.ContainsKey(victim.Key)) return [];
        var replies = new List<byte[]>();
        var seq = 0;
        foreach (var hit in PvpHits(victim.Session, skill))
        {
            theirs.Hp = Math.Max(1, theirs.Hp - hit.Amount);
            replies.Add(GamePackets.SkillHit(victim.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq, AttackSpeed));
        }
        if (replies.Count == 0) return [];
        victim.Send([.. replies, theirs.Update()]);
        if (theirs.Hp <= 1) ArenaDeath(match, worldPlayer, victim, now);
        return replies;
    }

    void ArenaDeath(ArenaMatch match, WorldPlayer killer, WorldPlayer victim, long now)
    {
        var k = match.Scores.GetValueOrDefault(killer.Key);
        match.Scores[killer.Key] = (k.Kills + 1, k.Deaths, k.Life);
        var v = match.Scores.GetValueOrDefault(victim.Key);
        match.Scores[victim.Key] = (v.Kills, v.Deaths + 1, Math.Max(0, v.Life - 1));
        var packets = new List<byte[]> { PacketWriter.Opcode(ArenaDungeonKillNt).I64(killer.Session.DbId).I64(victim.Session.DbId).ToArray(), ArenaScores(match) };
        foreach (var p in match.Players) p.Send(packets);
        // Lives 0 (MatchingContents Life 0: the battlefield modes) = unlimited respawns; the time limit ends those matches.
        if (match.Lives == 0 || match.Scores[victim.Key].Life > 0) match.RespawnAt[victim.Key] = now + match.RespawnMs;
        var blueOut = match.Lives > 0 && match.BlueTeam.All(p => match.Scores.GetValueOrDefault(p.Key).Life == 0);
        var redOut = match.Lives > 0 && match.RedTeam.All(p => match.Scores.GetValueOrDefault(p.Key).Life == 0);
        if (blueOut || redOut) EndArena(match, blueOut ? ArenaMatch.Red : ArenaMatch.Blue, now);
    }

    /// <summary>On the victim's loop: back at the team's respawn point with full HP.</summary>
    List<byte[]> ArenaRespawn(ArenaMatch match, long now)
    {
        if (resources is null || worldPlayer is null || ArenaPoints(match, match.TeamOf(worldPlayer)) is not { } points) return [];
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        return [resources.Update(), .. Teleport(match.MapId, points.Respawn, now)];
    }

    void EndArena(ArenaMatch match, byte winner, long now)
    {
        if (match.State == 4) return;
        match.State = 4;
        Social?.Arenas.Remove(match.RoomKey);
        // The ratings before the match (each team's mean, the other side's for the Elo change: GameSession.Ranking.cs).
        var type = ArenaKind(match.MatchingId).Season;
        double Mean(IEnumerable<WorldPlayer> team) => team.Select(q => (double)q.Session.ArenaRating(type)).DefaultIfEmpty(ArenaStartRating).Average();
        var (blueRating, redRating) = (Mean(match.BlueTeam), Mean(match.RedTeam));
        foreach (var p in match.Players)
        {
            var won = match.TeamOf(p) == winner;
            if (type != 0) p.Session.ArenaRated(match.MatchingId, winner == 0 ? 0.5 : won ? 1 : 0, match.TeamOf(p) == ArenaMatch.Blue ? redRating : blueRating);
            var w = PacketWriter.Opcode(ArenaDungeonResultNt).U8(winner).I32(won ? 10 : 0).Bool(won).Bool(true).Varint((uint)match.Scores.Count);
            foreach (var q in match.Players)
            {
                var (kills, deaths, life) = match.Scores.GetValueOrDefault(q.Key);
                w.I64(q.Session.DbId).Bool(false).Bool(false).U8((byte)kills).U8((byte)deaths).U8(0).U8((byte)life);
            }
            p.Send(w.ToArray());
            var rewards = ArenaRewardItems(match.MatchingId, won);
            p.Session.pendingRewards.Add(rewards);
            // Abyss Points count towards the week's AbyssPointUpdate_NT (GameSession.Abyss.cs).
            if (rewards.Where(r => r.Item == AbyssPointItemId).Sum(r => r.Count) is var ap and > 0) p.Session.pendingAchievementPackets.AddRange(p.Session.AbyssPointsEarned(ap));
            p.Session.instanceExitAt = now + ArenaExitMs;
            p.Session.pendingAchievementPackets.AddRange(won ? p.Session.Achieve("ArenaWin", 1, e => e.Values.Length == 0 || e.Has(match.MatchingId switch { 1 => "SingleCombat", 2 => "TeamCombat", _ => Systems2Tables.Embedded.ContentsOf(match.ContentsId)?.Type ?? "" }), now) : []);
        }
    }

    /// <summary>The match owner's tick: the ready wait, due respawns, the time limit.</summary>
    void ArenaTick(long now)
    {
        if (Social is null || worldPlayer is null || ArenaOf(worldPlayer) is not { } match || match.Owner != worldPlayer) return;
        foreach (var (key, due) in match.RespawnAt.Where(r => now >= r.Value).ToList())
        {
            match.RespawnAt.Remove(key);
            if (match.Players.FirstOrDefault(p => p.Key == key) is { } p) _ = p.Session.InvokeAsync(at => (true, p.Session.ArenaRespawn(match, at)));
        }
        if (now < match.NextMs) return;
        switch (match.State)
        {
            case 1 or 2: StartArenaBattle(match, now); break;
            case 3:
                var blue = match.BlueTeam.Sum(p => match.Scores.GetValueOrDefault(p.Key).Kills);
                var red = match.RedTeam.Sum(p => match.Scores.GetValueOrDefault(p.Key).Kills);
                EndArena(match, blue > red ? ArenaMatch.Blue : red > blue ? ArenaMatch.Red : (byte)0, now);
                break;
        }
    }

    /// <summary>Leaving the world or the map: out of the queue; a running match counts it as the other side's win.</summary>
    void LeaveArena(long now)
    {
        if (Social is not { } s || worldPlayer is null) return;
        s.ArenaQueue.RemoveAll(q => q.Player == worldPlayer);
        if (ArenaOf(worldPlayer) is { } match && match.State < 4)
        {
            var mine = match.TeamOf(worldPlayer);
            if (match.State == 3) EndArena(match, mine == ArenaMatch.Blue ? ArenaMatch.Red : ArenaMatch.Blue, now);
            else { s.Arenas.Remove(match.RoomKey); foreach (var p in match.Players.Where(p => p != worldPlayer)) p.Session.instanceExitAt = now; }
        }
    }

    List<byte[]> MatchingCancel(long now)
    {
        var removed = Social is { } s && worldPlayer is not null && s.ArenaQueue.RemoveAll(q => q.Player == worldPlayer) > 0;
        return [PacketWriter.Opcode(MatchingCancelRs).U16(removed ? 0 : 1).ToArray()];
    }

    readonly List<byte[]> pendingAchievementPackets = [];

    /// <summary>Checks only: sets the current HP (arena deaths without a hundred casts).</summary>
    public void SetHpForChecks(int hp) { if (resources is not null) resources.Hp = Math.Clamp(hp, 1, resources.HpMax); }
}
