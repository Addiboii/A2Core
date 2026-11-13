using Aion.Protocol.Native;

namespace Aion.World;

// The Abyss (Reshanta: Dungeon.dat type Abyss, maps 20/22/23 — shared zones, not copies). In: the NpcTeleport NPCs
// (N_L1_AbyssPortal_01_001 → map 20; the teleport managers back to 1010) at the race's start point of the map
// (map-points Abyss_Reshanta_A_Light_StartPoint / _Dark_). On an Abyss map a cast at a player of the other race does
// PvP damage (Skill.dat PvP rows, the NPC-hit path for HP and death: 0x8d00, the kill 0x8d04 with the killer); the killer's
// KillPC / KillPCAbyssGrade quest objectives and achievements count and 100 Abyss Points arrive (OURS; the official points
// depend on the victim's rank). Dead players revive at their race's respawn point. Ranks, fortresses, artifacts and the
// rift portals are not modelled.
// Abyss Points of the week (feature/systems-2): AbyssPointUpdate_NT 0xffab {ap_acc i64, ap_acc_max i64} is OBSERVED at every
// official world entry as 0 / 3,000,000; every Abyss Point earned here (Abyss kills, arena and battlefield rewards) adds to the
// week's count and sends it again, up to 3,000,000 (CANDIDATE: further points still arrive, only the count stops). The week
// turns with the season missions (GlobalSetting season_mission_reset_day 3 = Wednesday, season_mission_reset_time 16:00 KST = 07:00 UTC,
// the KST reading as the shops' OBSERVED weekly reset 2026-11-11 07:00 UTC and the season end, fix/rank-rewards;
// CANDIDATE that the Abyss count shares it); kept as character setting "abyss_point_week" {week, acc}.
// WorldMapAbyssRankerInfo_RQ 0x8ddc (catalogue 1035 + 0x89d1, the 0x8d run that ends before the party run's 0x9200) -> RS 0x8ddd
// {result, map, rankers}: the high-ranked players fallen on the map lately (feature/rank-system, GameSession.Ranking.cs). AbyssArtifactTeleport_RQ 0xe309 {artifact} (1435 + 0xdd6e, the
// run of the answered 0xe306) -> RS 0xe30a {result, artifact}: refused Telpo_InvalidAbyssEventState, as no artifact war runs
// (the AbyssArtifactEvent schedule and occupation are not modelled). All CANDIDATE.
public sealed partial class GameSession
{
    public const int AbyssKillPoints = 100;

    bool OnAbyssMap => MapEventData.DungeonOfMap(worldMap) is { Type: "Abyss" };

    /// <summary>A teleport NPC: the race's start point of the map (else its start), as a plain map move.</summary>
    List<byte[]> TeleportToMap(int mapId, long now)
    {
        if (resources is null || reloading || dead || !data.KnownMap(mapId)) return [];
        var raceWord = profileRace == 2 ? "Dark" : "Light";
        var points = data.MapPoints(mapId);
        var point = points.Where(p => p.Key.Contains(raceWord, StringComparison.OrdinalIgnoreCase) && p.Key.Contains("StartPoint", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Vector3d?)new Vector3d(p.Value[0], p.Value[1], p.Value[2])).FirstOrDefault() ?? data.MapStart(mapId)?.Position;
        return point is { } at ? Teleport(mapId, at, now) : [];
    }

    /// <summary>A cast at a player of the other race on an Abyss map (after duels and arenas said nothing).</summary>
    List<byte[]> FactionHits(int skill, int targetKey, long now)
    {
        if (!OnAbyssMap || worldPlayer?.Map is not { } map || !map.Players.TryGetValue(targetKey, out var victim) || victim.Session.profileRace == profileRace
            || victim.Session.resources is null || victim.Session.dead || victim.Hidden) return [];
        var replies = new List<byte[]>();
        var seq = 0;
        var total = 0;
        foreach (var hit in PvpHits(victim.Session, skill))
        {
            total += hit.Amount;
            replies.Add(GamePackets.SkillHit(victim.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq, AttackSpeed));
        }
        if (replies.Count == 0) return [];
        victim.Send([.. replies, .. victim.Session.NpcHitsPlayer(total, now, playerKey)]);
        if (victim.Session.dead)
        {
            victim.Session.AbyssDied(now);   // the victim's Abyss death count, a fallen ranker on the world map (GameSession.Ranking.cs)
            replies.AddRange(AbyssKill(victim, now));
        }
        return replies;
    }

    List<byte[]> AbyssKill(WorldPlayer victim, long now)
    {
        var replies = new List<byte[]>();
        replies.AddRange(Achieve("KillPC", 1, now: now));
        replies.AddRange(Achieve("KillPCAbyssGrade", 1, now: now));
        if (questLog is not null)
        {
            // The victim's map and Abyss grade decide which KillPC / KillPCAbyssGrade objectives count (Quests.cs KilledPlayer).
            var changed = questLog.KilledPlayer(level, worldMap, victim.Session.AbyssGradeShown);
            if (changed.Count > 0) pendingQuestChanges.AddRange(changed);
        }
        pendingRewards.Add([(AbyssPointItemId, AbyssKillPoints)]);
        AbyssRanking(0, 1, 0);   // the kill counts in the Abyss ranking's extra JSON (kill_count)
        replies.AddRange(AbyssPointsEarned(AbyssKillPoints));
        return replies;
    }

    readonly List<Aion.Application.Gameplay.QuestRecord> pendingQuestChanges = [];

    public const ushort AbyssPointUpdateNt = 0xffab, WorldMapAbyssRankerInfoRq = 0x8ddc, WorldMapAbyssRankerInfoRs = 0x8ddd,
        AbyssArtifactTeleportRq = 0xe309, AbyssArtifactTeleportRs = 0xe30a;
    /// <summary>OBSERVED _ap_acc_max of every official entry.</summary>
    public const long AbyssPointWeekMax = 3_000_000;
    const string AbyssWeekSetting = "abyss_point_week";
    long abyssPointsWeek;
    DateOnly abyssWeek;
    bool abyssWeekDirty;

    /// <summary>The Abyss week's first day: the Wednesday (season_mission_reset_day 3) whose 16:00 KST (07:00 UTC) last passed.</summary>
    internal static DateOnly AbyssWeekOf(long nowMs)
    {
        var shifted = DateTimeOffset.FromUnixTimeMilliseconds(nowMs + RankTables.KstMs - Systems2Tables.Embedded.Global("season_mission_reset_time")).UtcDateTime.Date;
        var back = ((int)shifted.DayOfWeek - (int)Systems2Tables.Embedded.Global("season_mission_reset_day") + 7) % 7;
        return DateOnly.FromDateTime(shifted.AddDays(-back));
    }

    byte[] AbyssPointUpdate() => PacketWriter.Opcode(AbyssPointUpdateNt).I64(abyssPointsWeek).I64(AbyssPointWeekMax).ToArray();

    /// <summary>Abyss Points earned: the week's count (and AbyssPointUpdate_NT); saved at this session's next tick.</summary>
    public List<byte[]> AbyssPointsEarned(long amount)
    {
        if (amount <= 0) return [];
        var week = AbyssWeekOf(lastNow == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : lastNow);
        if (week != abyssWeek) (abyssWeek, abyssPointsWeek) = (week, 0);
        abyssPointsWeek = Math.Min(AbyssPointWeekMax, abyssPointsWeek + amount);
        abyssWeekDirty = true;
        AbyssRanking(amount, 0, 0);   // the season's Abyss ranking point (GameSession.Ranking.cs; RankingPointUpdate_NT at the tick)
        return [AbyssPointUpdate()];
    }

    /// <summary>World entry: the stored week's count (a past week counts 0).</summary>
    void LoadAbyssWeek(IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, long now)
    {
        abyssWeek = AbyssWeekOf(now);
        abyssPointsWeek = 0;
        if (settings.TryGetValue(AbyssWeekSetting, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Object
            && v.TryGetProperty("week", out var w) && DateOnly.TryParse(w.GetString(), out var stored) && stored == abyssWeek
            && v.TryGetProperty("acc", out var acc) && acc.TryGetInt64(out var points))
            abyssPointsWeek = Math.Clamp(points, 0, AbyssPointWeekMax);
    }

    async Task SaveAbyssWeekAsync(CancellationToken ct)
    {
        if (!abyssWeekDirty || Store is not { } store || CharacterId is not { } character) return;
        abyssWeekDirty = false;
        var value = System.Text.Json.JsonSerializer.SerializeToElement(new { week = abyssWeek.ToString("yyyy-MM-dd"), acc = abyssPointsWeek });
        await Stored(async () => { await store.SaveCharacterSettingAsync(character, AbyssWeekSetting, value, ct); return []; });
    }

    List<byte[]>? AbyssRequest(byte[] plain, long now) => Opcode(plain) switch
    {
        WorldMapAbyssRankerInfoRq => [AbyssRankerInfo(now)],
        AbyssArtifactTeleportRq when plain.Length >= 6 => [PacketWriter.Opcode(AbyssArtifactTeleportRs)
            .U16(Systems2Tables.Embedded.AbyssArtifacts.Any(a => a.Id == BitConverter.ToInt32(plain, 2)) ? EResult.Telpo_InvalidAbyssEventState : EResult.Telpo_InvalidAbyssEvent)
            .I32(BitConverter.ToInt32(plain, 2)).ToArray()],
        _ => null,
    };
}
