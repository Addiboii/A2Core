using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Abyss economy data (tools/data/abyss.json, tools/abyss_data.py).</summary>
public sealed class AbyssTables
{
    public sealed record Rank([property: JsonPropertyName("grade")] int Grade, [property: JsonPropertyName("min_rank_points")] int MinRankPoints);
    public sealed record EntryPool([property: JsonPropertyName("weekly_grant_ms")] long WeeklyGrantMs, [property: JsonPropertyName("pool_cap_ms")] long PoolCapMs,
        [property: JsonPropertyName("reset_weekday")] int ResetWeekday, [property: JsonPropertyName("reset_hour_utc")] int ResetHourUtc);
    public sealed record Items([property: JsonPropertyName("abyss_point")] int AbyssPoint, [property: JsonPropertyName("entry_stone")] int EntryStone);
    public sealed record KillRewards([property: JsonPropertyName("ap_by_grade")] Dictionary<string, int> ApByGrade, [property: JsonPropertyName("ap_default")] int ApDefault,
        [property: JsonPropertyName("rank_points_by_grade")] Dictionary<string, int> RankPointsByGrade, [property: JsonPropertyName("rank_points_default")] int RankPointsDefault);
    public sealed record SeasonWindow([property: JsonPropertyName("from_ms")] long FromMs, [property: JsonPropertyName("to_ms")] long ToMs);
    public sealed record Airspace([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("map_id")] int MapId);
    public sealed record AirspaceBattle([property: JsonPropertyName("id")] int Id, [property: JsonPropertyName("state")] int State,
        [property: JsonPropertyName("airspace_ids")] int[] AirspaceIds);
    public sealed record SiegeSchedule([property: JsonPropertyName("cycle_ms")] long CycleMs, [property: JsonPropertyName("anchor_ms")] long AnchorMs,
        [property: JsonPropertyName("apply_ms")] long ApplyMs, [property: JsonPropertyName("battle_ms")] long BattleMs, [property: JsonPropertyName("end_ms")] long EndMs,
        [property: JsonPropertyName("tick_ms")] long TickMs, [property: JsonPropertyName("presence_points")] int PresencePoints,
        [property: JsonPropertyName("pvp_kill_points")] int PvpKillPoints, [property: JsonPropertyName("pvp_hit_points")] int PvpHitPoints);
    public sealed record SiegeRewards([property: JsonPropertyName("winner_ap")] int WinnerAp, [property: JsonPropertyName("participant_ap")] int ParticipantAp);
    public sealed record ArtifactEntry([property: JsonPropertyName("artifact_id")] int ArtifactId, [property: JsonPropertyName("env_obj_id")] int EnvObjId,
        [property: JsonPropertyName("map_id")] int MapId, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("occupying_race")] int OccupyingRace);
    public sealed record RiftEntry([property: JsonPropertyName("rift_id")] int RiftId, [property: JsonPropertyName("env_obj_id")] int EnvObjId,
        [property: JsonPropertyName("map_id")] int MapId, [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("wave")] int Wave,
        [property: JsonPropertyName("position")] double[] Position);
    sealed record File([property: JsonPropertyName("season_key")] string SeasonKey, [property: JsonPropertyName("season")] SeasonWindow Season,
        [property: JsonPropertyName("maps")] int[] Maps, [property: JsonPropertyName("items")] Items Items, [property: JsonPropertyName("entry")] EntryPool Entry,
        [property: JsonPropertyName("ap_weekly_cap")] long ApWeeklyCap, [property: JsonPropertyName("ranks")] Rank[] Ranks, [property: JsonPropertyName("kill")] KillRewards Kill,
        [property: JsonPropertyName("mission_groups")] string[] MissionGroups, [property: JsonPropertyName("airspaces")] Airspace[]? Airspaces,
        [property: JsonPropertyName("airspace_battles")] AirspaceBattle[]? AirspaceBattles, [property: JsonPropertyName("siege_schedule")] SiegeSchedule? SiegeSchedule,
        [property: JsonPropertyName("siege_rewards")] SiegeRewards? SiegeRewards, [property: JsonPropertyName("artifacts")] ArtifactEntry[]? Artifacts,
        [property: JsonPropertyName("rifts")] RiftEntry[]? Rifts);

    readonly File file;
    readonly HashSet<int> maps;
    AbyssTables(File file)
    {
        this.file = file;
        maps = file.Maps.ToHashSet();
    }

    static readonly Lazy<AbyssTables> embedded = new(() =>
    {
        using var stream = typeof(AbyssTables).Assembly.GetManifestResourceStream("abyss.json") ?? throw new InvalidOperationException("abyss.json is not embedded");
        return new AbyssTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("abyss.json"));
    });
    public static AbyssTables Embedded => embedded.Value;

    public string SeasonKey => file.SeasonKey;
    public EntryPool Entry => file.Entry;
    public Items ItemIds => file.Items;
    public long ApWeeklyCap => file.ApWeeklyCap;
    public KillRewards Kill => file.Kill;
    public bool IsAbyssMap(int mapId) => maps.Contains(mapId);
    public bool InSeason(long nowMs) => file.Season.FromMs <= nowMs && nowMs <= file.Season.ToMs;
    public bool IsAbyssMissionGroup(string group) => file.MissionGroups.Contains(group);
    public int GradeOf(int rankingPoint)
    {
        var grade = 1;
        foreach (var rank in file.Ranks.OrderBy(r => r.Grade))
            if (rankingPoint >= rank.MinRankPoints) grade = rank.Grade;
        return grade;
    }
    public int KillAp(int victimGrade) => file.Kill.ApByGrade.GetValueOrDefault(victimGrade.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Kill.ApDefault);
    public int KillRankPoints(int victimGrade) => file.Kill.RankPointsByGrade.GetValueOrDefault(victimGrade.ToString(System.Globalization.CultureInfo.InvariantCulture), file.Kill.RankPointsDefault);
    public IReadOnlyList<Airspace> Airspaces => file.Airspaces ?? [];
    public IReadOnlyList<AirspaceBattle> AirspaceBattles => file.AirspaceBattles ?? [];
    public Airspace? AirspaceOf(int id) => Airspaces.FirstOrDefault(a => a.Id == id);
    public SiegeSchedule Siege => file.SiegeSchedule ?? DefaultSiege;
    public SiegeRewards SiegePayout => file.SiegeRewards ?? DefaultRewards;
    public IReadOnlyList<ArtifactEntry> Artifacts => file.Artifacts ?? [];
    public IReadOnlyList<RiftEntry> Rifts => file.Rifts ?? [];
    public IReadOnlyList<Airspace> AirspacesOnMap(int mapId) => Airspaces.Where(a => a.MapId == mapId).ToList();

    static SiegeSchedule DefaultSiege => new(604_800_000, 1_784_098_800_000, 86_400_000, 3_600_000, 600_000, 1_000, 1, 100, 5);
    static SiegeRewards DefaultRewards => new(5_000, 1_000);
}

// The Abyss (Reshanta: Dungeon.dat type Abyss, maps 20/22/23 — shared zones). PvP between races, Abyss Points (currency item),
// ranking points/grades, a banked entry-time pool (weekly grant + cap from abyss.json), and active SeasonMission achievements
// (Group_Season_G1_*_Abyss on the Oct 7 Global pin). AbyssPointUpdate_NT 0x3C06, RankingPointUpdate_NT 0x3B78 (type 1 Abyss,
// CANDIDATE), WorldMapAbyssRankerInfo 0x3A17/0x3A18 (empty list). AirspaceBattle (fortress siege) 0x372C–0x373B: weekly wait/apply/battle/end
// schedule, legion apply (persisted), presence + PvP siege scoring, CharAirspacePoint_NT, owner ProgressResult NT, siege AP (online + pending offline).
public sealed partial class GameSession
{
    public const int AbyssPointItemId = 930100003;
    public const int AbyssEntryStoneItemId = 930100019;
    public const ushort AbyssPointUpdateNt = 0x3C06, RankingPointUpdateNt = 0x3B78, WorldMapAbyssRankerInfoRq = 0x3A17, WorldMapAbyssRankerInfoRs = 0x3A18,
        PrevSeasonMissionInfoNt = 0x5682,
        AirspaceBattleRq = 0x372C, AirspaceBattleNt = 0x372D, AirspaceBattleUpdateNt = 0x372E, AirspaceBattleProgressResultNt = 0x372F,
        AirspaceBattleApplyRq = 0x3731, AirspaceBattleApplyRs = 0x3732, AirspaceBattleApplyNt = 0x3733,
        AirspaceBattleApplyCancelRq = 0x3734, AirspaceBattleApplyCancelRs = 0x3735, AirspaceBattleApplyCancelNt = 0x3736,
        AirspaceBattleApplyGuildListNt = 0x3737, CharAirspacePointNt = 0x3738, AirspaceBattleRankingRq = 0x3739, AirspaceBattleRankingRs = 0x373A,
        RiftOptionUpdateRq = 0x3652, RiftOptionUpdateRs = 0x3653, RiftOptionCharUpdateNt = 0x3654;
    const int RankingTypeAbyss = 1;

    static AbyssTables Abyss => AbyssTables.Embedded;

    long entryRemainingMs;
    long apAcc;
    int abyssRankingPoint;
    int abyssRankingGrade = 1;
    long abyssWeeklyResetMs;
    long abyssTickMs;
    long abyssInsideSince;
    long pendingSiegeAp;
    int riftPvpOn;
    bool abyssDirty;
    bool abyssLoaded;
    bool airspaceLoaded;

    public int AbyssRankingGrade => abyssRankingGrade;

    internal void QueueAbyssPointReward(int ap)
    {
        if (ap > 0) pendingRewards.Add([(AbyssPointItemId, ap)]);
    }
    public long AbyssEntryRemainingMs(long now) => EntryRemainingMs(now);

    bool OnAbyssMap => Abyss.IsAbyssMap(worldMap);

    /// <summary>SeasonMission* achievements of the active Global season (listed in the achievement burst).</summary>
    public bool SeasonMissionListed(AchievementTables.Entry entry, long now)
    {
        if (!Abyss.InSeason(now)) return false;
        if (Achievements.GroupOf(entry) is not { } group) return false;
        if (group.Race != 0 && group.Race != profileRace) return false;
        if (group.Category is not ("SeasonMission" or "SeasonMissionExp" or "SeasonMissionUnlock")) return false;
        if (!entry.Group.Contains(Abyss.SeasonKey, StringComparison.Ordinal)) return false;
        if (group.Category == "SeasonMission" && !Abyss.IsAbyssMissionGroup(entry.Group)) return false;
        return true;
    }

    void LoadAbyss(AbyssRecord? stored, long now)
    {
        entryRemainingMs = stored?.EntryRemainingMs ?? Abyss.Entry.WeeklyGrantMs;
        apAcc = stored?.ApAcc ?? 0;
        abyssRankingPoint = stored?.RankingPoint ?? 0;
        abyssRankingGrade = stored?.RankingGrade ?? 1;
        abyssWeeklyResetMs = stored?.WeeklyResetAt.ToUnixTimeMilliseconds() ?? AbyssWeekStart(now);
        pendingSiegeAp = stored?.PendingSiegeAp ?? 0;
        riftPvpOn = stored?.RiftPvpOn ?? 0;
        abyssTickMs = now;
        abyssInsideSince = 0;
        abyssDirty = false;
        abyssLoaded = true;
        ApplyAbyssWeeklyReset(now);
        ApplyPendingSiegeAp(now);
    }

    void ApplyPendingSiegeAp(long now)
    {
        if (pendingSiegeAp <= 0) return;
        var ap = (int)Math.Min(pendingSiegeAp, int.MaxValue);
        pendingSiegeAp = 0;
        abyssDirty = true;
        QueueAbyssPointReward(ap);
        AddAbyssPoints(ap, now, []);
    }

    void EnsureAbyssLoaded(long now)
    {
        if (!abyssLoaded) LoadAbyss(null, now);
    }

    static long AbyssWeekStart(long nowMs) => ContentResetSchedule.WeeklyStartMs(nowMs);

    void ApplyAbyssWeeklyReset(long now)
    {
        var week = AbyssWeekStart(now);
        if (week <= abyssWeeklyResetMs) return;
        entryRemainingMs = Math.Min(Abyss.Entry.PoolCapMs, Abyss.Entry.WeeklyGrantMs);
        apAcc = 0;
        abyssWeeklyResetMs = week;
        abyssDirty = true;
    }

    long EntryRemainingMs(long now)
    {
        ApplyAbyssWeeklyReset(now);
        return entryRemainingMs;
    }

    byte[] AbyssPointUpdate() => PacketWriter.Opcode(AbyssPointUpdateNt).I32((int)Math.Min(apAcc, int.MaxValue)).I32((int)Math.Min(Abyss.ApWeeklyCap, int.MaxValue)).ToArray();
    byte[] RankingPointUpdate() => PacketWriter.Opcode(RankingPointUpdateNt).I32(RankingTypeAbyss).I32(abyssRankingPoint).ToArray();

    byte[] PrevSeasonMissionInfo(long now)
    {
        var missions = Achievements.All.Where(e => SeasonMissionListed(e, now) && e.Type == "SeasonMission").ToList();
        var complete = missions.Count(e => achievements.GetValueOrDefault(e.Id)?.CompletedMs > 0);
        return PacketWriter.Opcode(PrevSeasonMissionInfoNt).I32(complete).I32(missions.Count).ToArray();
    }

    List<byte[]> AbyssAtEntry(long now)
    {
        ApplyAbyssWeeklyReset(now);
        return [PrevSeasonMissionInfo(now), AbyssPointUpdate(), RankingPointUpdate()];
    }

    /// <summary>0x3A17 WorldMapAbyssRankerInfo_RQ: no rankers on the dev server yet.</summary>
    List<byte[]> WorldMapAbyssRankerInfo(byte[] plain)
    {
        if (plain.Length < 2) return [];
        return [PacketWriter.Opcode(WorldMapAbyssRankerInfoRs).U16(0).I32(worldMap).Varint(0).ToArray()];
    }

    /// <summary>A teleport NPC: the race's start point of the map (else its start), as a plain map move.</summary>
    List<byte[]> TeleportToMap(int mapId, long now)
    {
        if (resources is null || reloading || dead || !data.KnownMap(mapId)) return [];
        if (Abyss.IsAbyssMap(mapId) && !OnAbyssMap && EntryRemainingMs(now) <= 0) return [];
        var raceWord = profileRace == 2 ? "Dark" : "Light";
        var points = data.MapPoints(mapId);
        var point = points.Where(p => p.Key.Contains(raceWord, StringComparison.OrdinalIgnoreCase) && p.Key.Contains("StartPoint", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault() ?? data.MapStart(mapId)?.Position;
        return point is { } at ? Teleport(mapId, at, now) : [];
    }

    void AbyssMapEntered(long now)
    {
        if (!OnAbyssMap) { abyssInsideSince = 0; return; }
        if (abyssInsideSince == 0) abyssInsideSince = now;
    }

    void AbyssMapLeft(long now) => abyssInsideSince = 0;

    List<byte[]> AbyssTick(long now)
    {
        ApplyAbyssWeeklyReset(now);
        var replies = new List<byte[]>();
        if (!OnAbyssMap || abyssInsideSince == 0 || entryRemainingMs <= 0) { abyssTickMs = now; return replies; }
        var elapsed = Math.Max(0, (now - abyssTickMs) / 1000);
        if (elapsed <= 0) return replies;
        abyssTickMs = now;
        var spend = Math.Min(entryRemainingMs, elapsed * 1000L);
        if (spend <= 0) return replies;
        entryRemainingMs -= spend;
        abyssDirty = true;
        var seconds = (int)(spend / 1000);
        replies.AddRange(Achieve("CannotIncrease", seconds, e => SeasonMissionListed(e, now), now));
        return replies;
    }

    /// <summary>A cast at a player of the other race on an Abyss map (after duels and arenas said nothing).</summary>
    List<byte[]> FactionHits(int skill, int targetKey, long now, byte actionUid)
    {
        if (worldPlayer?.Map is not { } map || !map.Players.TryGetValue(targetKey, out var victim) || victim.Session.resources is null
            || !MayFactionPvp(victim)) return [];
        var replies = new List<byte[]>();
        var seq = 0;
        var total = 0;
        foreach (var hit in PlayerSkillHits(skill, victim.Session.Defense, pvp: true))
        {
            total += hit.Amount;
            replies.Add(GamePackets.SkillHit(victim.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, actionUid, ++seq));
        }
        if (replies.Count == 0) return [];
        var victimReplies = victim.Session.NpcHitsPlayer(total, now, playerKey);
        victim.Send([.. replies, .. victimReplies]);
        if (!victim.Session.dead) PvpSkillDebuffs(victim, skill, now);
        if (total > 0) replies.AddRange(AccrueFortressPvp(Abyss.Siege.PvpHitPoints, now));
        if (victim.Session.dead) replies.AddRange(FactionKillRewards(victim, now));
        return replies;
    }

    List<byte[]> AbyssKill(WorldPlayer victim, long now)
    {
        var replies = new List<byte[]>();
        replies.AddRange(AccrueFortressPvp(Abyss.Siege.PvpKillPoints, now));
        replies.AddRange(Achieve("KillPC", 1, now: now));
        replies.AddRange(Achieve("KillPCAbyssGrade", 1, e => e.Values.Length == 0 || e.Values.Contains(victim.Session.abyssRankingGrade.ToString(System.Globalization.CultureInfo.InvariantCulture)), now: now));
        if (questLog is not null)
        {
            var changed = questLog.KilledPlayer(level);
            if (changed.Count > 0) pendingQuestChanges.AddRange(changed);
        }
        var ap = Abyss.KillAp(victim.Session.abyssRankingGrade);
        pendingRewards.Add([(AbyssPointItemId, ap)]);
        AddAbyssPoints(ap, now, replies);
        var rankGain = Abyss.KillRankPoints(victim.Session.abyssRankingGrade);
        if (rankGain > 0)
        {
            abyssRankingPoint += rankGain;
            var grade = Abyss.GradeOf(abyssRankingPoint);
            if (grade != abyssRankingGrade) abyssRankingGrade = grade;
            abyssDirty = true;
            replies.Add(RankingPointUpdate());
        }
        return replies;
    }

    void AddAbyssPoints(int ap, long now, List<byte[]> replies)
    {
        if (ap <= 0) return;
        apAcc = Math.Min(Abyss.ApWeeklyCap, apAcc + ap);
        abyssDirty = true;
        replies.Add(AbyssPointUpdate());
        replies.AddRange(Achieve("GetItem", ap, e => e.Items?.Contains(AbyssPointItemId) == true, now));
    }

    List<byte[]> AccrueFortressPvp(int points, long now)
    {
        if (points <= 0 || guild is null || options.World is not { } world || worldPlayer?.Map is not { } map) return [];
        var replies = new List<byte[]>();
        foreach (var airspace in Abyss.AirspacesOnMap(map.MapId))
        {
            if (world.Airspace.AccruePvpAction(world, worldPlayer, airspace.Id, points, now) is not { } nt) continue;
            replies.Add(nt);
            foreach (var player in world.Online.Values)
                if (player.Map?.MapId == map.MapId && player.Key != worldPlayer.Key) player.Send([nt]);
            break;
        }
        return replies;
    }

    AbyssRecord AbyssSnapshot(long now) => new(entryRemainingMs, apAcc, abyssRankingPoint, abyssRankingGrade,
        DateTimeOffset.FromUnixTimeMilliseconds(abyssWeeklyResetMs), DateTimeOffset.FromUnixTimeMilliseconds(now), pendingSiegeAp, riftPvpOn);

    async Task FlushAbyssAsync(long now, CancellationToken ct, bool force = false)
    {
        if (!abyssDirty && !force) return;
        if (Store is not { } store || CharacterId is not { } character) return;
        abyssDirty = false;
        await Stored(async () => await store.SaveAbyssAsync(character, AbyssSnapshot(now), ct) ? [[]] : []);
    }

    readonly List<Aion.Application.Gameplay.QuestRecord> pendingQuestChanges = [];

    async Task EnsureAirspaceLoadedAsync(CancellationToken ct)
    {
        if (airspaceLoaded || options.World is not { } world || Store is not { } store) return;
        if (!world.Airspace.IsLoaded)
        {
            var record = await store.AirspaceWorldAsync(ct);
            world.Airspace.Load(options.WorldId, record);
        }
        airspaceLoaded = true;
    }

    async Task FlushAirspaceAsync(CancellationToken ct)
    {
        if (options.World is not { } world || Store is not { } store) return;
        foreach (var (guildId, ap) in world.Airspace.TakePendingApGrants())
        {
            var online = world.Online.Values.Where(p => p.Session.Guild?.Id == guildId && p.CharacterId is { })
                .Select(p => p.CharacterId!.Value).ToList();
            await store.AddPendingSiegeApForGuildAsync(guildId, ap, online, ct);
        }
        if (!world.Airspace.Dirty) return;
        world.Airspace.ClearDirty();
        await store.SaveAirspaceWorldAsync(world.Airspace.Snapshot(), ct);
    }

    List<byte[]> AirspaceWorldTick(long now)
    {
        if (options.World is not { } world) return [];
        if (!world.Airspace.NeedsTick(now)) return [];
        var packets = world.Airspace.Tick(world, options.WorldId, now).ToList();
        if (packets.Count == 0) return [];
        foreach (var player in world.Online.Values)
            player.Send(packets);
        return [];
    }

    /// <summary>Fortress siege UI packets (AirspaceBattle_*). Called from <see cref="ClientRequest"/>.</summary>
    List<byte[]>? AbyssClientRequest(byte[] plain, long now)
    {
        switch (Opcode(plain))
        {
            case AirspaceBattleRq when plain.Length == 2:
                return [AirspaceBattleSchedule(now)];
            case AirspaceBattleApplyRq when plain.Length >= 10:
            {
                var reader = new PacketReader(plain) { Offset = 2 };
                var battleId = reader.I32();
                var airspaceId = reader.I32();
                return AirspaceBattleApply(battleId, airspaceId, now);
            }
            case AirspaceBattleApplyCancelRq when plain.Length >= 10:
            {
                var reader = new PacketReader(plain) { Offset = 2 };
                var battleId = reader.I32();
                var airspaceId = reader.I32();
                return AirspaceBattleApplyCancel(battleId, airspaceId);
            }
            case AirspaceBattleRankingRq when plain.Length >= 6:
            {
                var airspaceId = BitConverter.ToInt32(plain, 2);
                return [AirspaceBattleRanking(airspaceId, now)];
            }
            case RiftOptionUpdateRq when plain.Length >= 3:
            {
                var on = plain[2] != 0 ? 1 : 0;
                return RiftOptionUpdate(on, now);
            }
            case AbyssArtifactEventInfoRq when plain.Length == 2:
                return [AbyssArtifactEventInfo()];
        }
        return null;
    }

    byte[] AbyssArtifactEventInfo()
    {
        var arts = Abyss.Artifacts;
        var w = PacketWriter.Opcode(AbyssArtifactEventInfoRs).U16(0).Varint(0).Varint(arts.Count);
        foreach (var a in arts)
            w.I32(a.ArtifactId).I32(a.EnvObjId).I32(a.OccupyingRace).I64(0);
        return w.ToArray();
    }

    List<byte[]> RiftOptionUpdate(int on, long now)
    {
        riftPvpOn = on;
        abyssDirty = true;
        var replies = new List<byte[]>
        {
            PacketWriter.Opcode(RiftOptionUpdateRs).U16(0).I32(on).I64(now).I32(0).ToArray(),
            PacketWriter.Opcode(RiftOptionCharUpdateNt).Varint(playerKey).I32(on).ToArray()
        };
        if (options.World is { } world)
            foreach (var player in world.Online.Values)
                if (player.Key != playerKey) player.Send([replies[^1]]);
        return replies;
    }

    byte[] AirspaceBattleSchedule(long now)
    {
        var battles = Abyss.AirspaceBattles;
        var w = PacketWriter.Opcode(AirspaceBattleNt).Varint(battles.Count);
        var siege = options.World?.Airspace;
        foreach (var battle in battles)
        {
            if (siege is not null) siege.WriteBattleSchedule(w, battle.Id, now);
            else
            {
                w.I32(battle.Id).I32(battle.State).I64(0).I64(0).I64(0);
                w.Varint(battle.AirspaceIds.Length);
                foreach (var airspaceId in battle.AirspaceIds)
                {
                    w.I32(airspaceId).I32(0);
                    w.I32(0).I32(0).String("").I32(0).String("");
                }
            }
        }
        return w.ToArray();
    }

    List<byte[]> AirspaceBattleApply(int battleId, int airspaceId, long now)
    {
        var replies = new List<byte[]>();
        var ok = guild is not null && Abyss.AirspaceOf(airspaceId) is not null
            && Abyss.AirspaceBattles.Any(b => b.Id == battleId && b.AirspaceIds.Contains(airspaceId))
            && options.World?.Airspace.RegisterApply(battleId, airspaceId, guild!.Id, now) == true;
        replies.Add(PacketWriter.Opcode(AirspaceBattleApplyRs).U16(ok ? (ushort)0 : (ushort)1).I32(battleId).I32(airspaceId).ToArray());
        if (ok && options.World is { } world)
        {
            replies.Add(world.Airspace.BuildApplyNt(battleId, airspaceId, 1));
            replies.Add(world.Airspace.BuildApplyGuildList(world, battleId, airspaceId, options.WorldId));
            foreach (var player in world.Online.Values)
                if (player != worldPlayer) player.Send(replies);
        }
        return replies;
    }

    List<byte[]> AirspaceBattleApplyCancel(int battleId, int airspaceId)
    {
        var replies = new List<byte[]>();
        var had = guild is not null && options.World?.Airspace.CancelApply(battleId, airspaceId, guild.Id) == true;
        replies.Add(PacketWriter.Opcode(AirspaceBattleApplyCancelRs).U16(had ? (ushort)0 : (ushort)1).I32(battleId).I32(airspaceId).ToArray());
        if (had && options.World is { } world)
        {
            var nt = world.Airspace.BuildApplyCancelNt(battleId, airspaceId);
            replies.Add(nt);
            foreach (var player in world.Online.Values)
                if (player != worldPlayer) player.Send([nt, world.Airspace.BuildApplyGuildList(world, battleId, airspaceId, options.WorldId)]);
        }
        return replies;
    }

    byte[] AirspaceBattleRanking(int airspaceId, long now)
    {
        var w = PacketWriter.Opcode(AirspaceBattleRankingRs);
        if (options.World?.Airspace is { } siege) siege.WriteRanking(w, airspaceId, now);
        else w.U16(0).I32(airspaceId).I32(0).I64(now + 3_600_000).Varint(0);
        return w.ToArray();
    }
}
