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
    public const ushort MatchingStateChangeNt = 0xe2a8, MatchingStartRq = 0xe2a9, MatchingStartRs = 0xe2aa, MatchingStartResponseRq = 0xe2ab, MatchingStartResponseRs = 0xe2ac,
        MatchingCancelRq = 0xe2ad, MatchingCancelRs = 0xe2ae, MatchingRoomInitNt = 0xe2b2, ArenaDungeonReadyRq = 0xe2b3, ArenaDungeonReadyRs = 0xe2b4, ArenaDungeonReadyNt = 0xe2b5,
        ArenaDungeonReadyEndNt = 0xe2b6, ArenaDungeonKillNt = 0xe2be, ArenaDungeonScoreUpdateNt = 0xe2bf, ArenaDungeonResultNt = 0xe2c1, ArenaRecordRq = 0xe2c4, ArenaRecordRs = 0xe2c5;
    const byte MatchingStateMatching = 2, MatchingStatePendingReady = 3, MatchingStateComplete = 4;
    public const long ArenaReadyWaitMs = 30_000, ArenaRespawnMs = 5_000, ArenaExitMs = 10_000, ArenaPlayTimeMs = 600_000;
    public const int AbyssPointItemId = 930100003;
    static readonly int[] ArenaDungeons = [7, 8];   // MatchingContents 11/22 (Single) and 33/44 (Team): Fire Temple Arena, Impetusium Arena
    static readonly int[] ArenaMaps = [60, 61];

    /// <summary>Matching.dat: team size per matching id (Single 1, Team 5).</summary>
    static int ArenaTeamSize(int matchingId) => matchingId switch { 1 => 1, 2 => 5, _ => 0 };
    static int ArenaLives(int matchingId) => matchingId == 1 ? 3 : 1;
    static (int Join, int Win) ArenaRewards(int matchingId) => (300, 500);   // RewardArena.dat Reward_Join/Win_Single and _Team: Abyss Points

    ArenaMatch? ArenaOf(WorldPlayer p) => Social?.Arenas.Values.FirstOrDefault(a => a.Has(p));

    byte[] MatchingState(int matchingId, byte state) => PacketWriter.Opcode(MatchingStateChangeNt).I32(matchingId).U8(state).ToArray();

    /// <summary>0xe2a9 MatchingStart_RQ {matching id}: into the queue; a full set of players opens a match pending everyone's acceptance.</summary>
    List<byte[]> MatchingStart(byte[] plain, long now)
    {
        if (plain.Length < 6) return [];
        var matchingId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(int result) => PacketWriter.Opcode(MatchingStartRs).U16(result).I32(matchingId).ToArray();
        var size = ArenaTeamSize(matchingId);
        if (size == 0 || Social is not { } s || worldPlayer is null || instanceKey != 0 || ArenaOf(worldPlayer) is not null || s.ArenaQueue.Any(q => q.Player == worldPlayer)) return [Reply(1)];
        s.ArenaQueue.Add(new ArenaQueueEntry(worldPlayer, matchingId, now));
        var replies = new List<byte[]> { Reply(0), MatchingState(matchingId, MatchingStateMatching) };
        var waiting = s.ArenaQueue.Where(q => q.MatchingId == matchingId).OrderBy(q => q.SinceMs).Take(size * 2).ToList();
        if (waiting.Count < size * 2) return replies;
        foreach (var entry in waiting) s.ArenaQueue.Remove(entry);
        var pick = rng.Next(ArenaDungeons.Length);
        var match = new ArenaMatch((int)s.NextId(), matchingId, (matchingId == 1 ? 11 : 33) * (pick + 1), ArenaDungeons[pick], ArenaMaps[pick], ArenaLives(matchingId), ArenaPlayTimeMs) { NextMs = now + ArenaReadyWaitMs };
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
                w.I64(ms.DbId).I64(ms.DbId).String(ms.StoredName).I32(ms.Profile()?.PcId ?? 1).U8((byte)ms.profileRace).I32(ms.level).I32(0).I32(ServerId).I64(0)
                    .U8((byte)match.MatchingId).I64(0).I32(0).I32(0).I32(0).I32(0).I32(0);
            }
        }
        return w.ToArray();
    }

    (Vector3d Start, Vector3d Respawn)? ArenaPoints(ArenaMatch match, byte team)
    {
        var points = data.MapPoints(match.MapId);
        var side = team == ArenaMatch.Blue ? "_A_" : "_B_";
        Vector3d? Find(string suffix) => points.FirstOrDefault(p => p.Key.Contains(side + suffix, StringComparison.OrdinalIgnoreCase)).Value is { Length: >= 3 } v ? new Vector3d(v[0], v[1], v[2]) : null;
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
        foreach (var hit in Combat.SkillHits(data, skill, className, level, equippedWeapon, victim.Session.Defense, rng, pvp: true))
        {
            theirs.Hp = Math.Max(1, theirs.Hp - hit.Amount);
            replies.Add(GamePackets.SkillHit(victim.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq));
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
        if (match.Scores[victim.Key].Life > 0) match.RespawnAt[victim.Key] = now + ArenaRespawnMs;
        var blueOut = match.BlueTeam.All(p => match.Scores.GetValueOrDefault(p.Key).Life == 0);
        var redOut = match.RedTeam.All(p => match.Scores.GetValueOrDefault(p.Key).Life == 0);
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
        var (join, win) = ArenaRewards(match.MatchingId);
        foreach (var p in match.Players)
        {
            var won = match.TeamOf(p) == winner;
            var w = PacketWriter.Opcode(ArenaDungeonResultNt).U8(winner).I32(won ? 10 : 0).Bool(won).Bool(true).Varint((uint)match.Scores.Count);
            foreach (var q in match.Players)
            {
                var (kills, deaths, life) = match.Scores.GetValueOrDefault(q.Key);
                w.I64(q.Session.DbId).Bool(false).Bool(false).U8((byte)kills).U8((byte)deaths).U8(0).U8((byte)life);
            }
            p.Send(w.ToArray());
            p.Session.pendingRewards.Add([(AbyssPointItemId, join + (won ? win : 0))]);
            p.Session.instanceExitAt = now + ArenaExitMs;
            p.Session.pendingAchievementPackets.AddRange(won ? p.Session.Achieve("ArenaWin", 1, e => e.Values.Length == 0 || e.Has(match.MatchingId == 1 ? "SingleCombat" : "TeamCombat"), now) : []);
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
