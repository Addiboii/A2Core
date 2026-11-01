using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Fortress siege (AirspaceBattle): weekly wait/apply/battle/end schedule, legion signup, live progress and ownership.</summary>
public sealed class AirspaceSiege
{
    /// <summary>EAirspaceBattleState on the Oct 7 pin (OURS until AirspaceBattle.dat export).</summary>
    public const int PhaseWait = 0, PhaseApply = 1, PhaseBattle = 2, PhaseEnd = 3;

    public sealed record GuildDisplay(int ServerId, long GuildId, string Name, int Emblem, string MasterName);

    readonly Dictionary<int, GuildDisplay> owners = [];
    readonly HashSet<(int BattleId, int AirspaceId, long GuildId)> applications = [];
    readonly Dictionary<(int AirspaceId, long GuildId), int> siegePoints = [];
    readonly Dictionary<int, int> lastPhase = [];
    readonly Dictionary<int, int> charPoints = [];
    readonly List<(long GuildId, int Ap)> pendingApGrants = [];
    int loadedWorldId;
    bool loaded;
    public bool IsLoaded => loaded;
    bool dirty;
    long tickAt;
    long rankingVersion;

    public bool NeedsTick(long now) => now >= tickAt;

    public void Load(int worldId, AirspaceWorldRecord? record)
    {
        loadedWorldId = worldId;
        owners.Clear();
        applications.Clear();
        siegePoints.Clear();
        lastPhase.Clear();
        if (record is not null)
        {
            foreach (var o in record.Owners)
                owners[o.AirspaceId] = new GuildDisplay(worldId, o.GuildId, o.Name, o.Emblem, o.MasterName);
            foreach (var a in record.Applications)
                applications.Add((a.BattleId, a.AirspaceId, a.GuildId));
        }
        loaded = true;
        dirty = false;
        tickAt = 0;
    }

    public AirspaceWorldRecord Snapshot()
    {
        var ownerRows = owners.Select(kv => new AirspaceOwnerRecord(kv.Key, kv.Value.GuildId, kv.Value.Name, kv.Value.Emblem, kv.Value.MasterName)).ToList();
        var applyRows = applications.Select(a => new AirspaceApplyRecord(a.BattleId, a.AirspaceId, a.GuildId)).ToList();
        return new AirspaceWorldRecord(ownerRows, applyRows);
    }

    public bool RegisterApply(int battleId, int airspaceId, long guildId, long now)
    {
        if (!InPhase(now, PhaseApply)) return false;
        if (!applications.Add((battleId, airspaceId, guildId))) return false;
        dirty = true;
        return true;
    }

    public bool CancelApply(int battleId, int airspaceId, long guildId)
    {
        if (!applications.Remove((battleId, airspaceId, guildId))) return false;
        dirty = true;
        return true;
    }

    public bool IsApplicant(int battleId, int airspaceId, long guildId) => applications.Contains((battleId, airspaceId, guildId));

    public IReadOnlyList<long> Applicants(int battleId, int airspaceId) =>
        applications.Where(a => a.BattleId == battleId && a.AirspaceId == airspaceId).Select(a => a.GuildId).ToList();

    public GuildDisplay? OwnerOf(int airspaceId) => owners.GetValueOrDefault(airspaceId);

    /// <summary>World tick: advance the schedule, accrue siege points, resolve owners, broadcast NTs.</summary>
    public IReadOnlyList<byte[]> Tick(SharedWorld world, int worldId, long now)
    {
        tickAt = now + AbyssTables.Embedded.Siege.TickMs;
        var packets = new List<byte[]>();
        if (!loaded)
        {
            loadedWorldId = worldId;
            loaded = true;
        }
        foreach (var battle in AbyssTables.Embedded.AirspaceBattles)
        {
            var phase = PhaseAt(now, battle.Id, out var ready, out var battleStart, out var endStart);
            var prev = lastPhase.GetValueOrDefault(battle.Id, -1);
            if (phase == PhaseBattle && prev != PhaseBattle)
            {
                siegePoints.Clear();
                charPoints.Clear();
                rankingVersion++;
                foreach (var airspaceId in battle.AirspaceIds)
                {
                    foreach (var guildId in Applicants(battle.Id, airspaceId))
                        siegePoints[(airspaceId, guildId)] = 0;
                    if (owners.TryGetValue(airspaceId, out var defender))
                        siegePoints[(airspaceId, defender.GuildId)] = siegePoints.GetValueOrDefault((airspaceId, defender.GuildId));
                }
            }
            if (phase == PhaseBattle)
                AccruePresence(world, battle);
            if (phase == PhaseEnd && prev == PhaseBattle)
                packets.AddRange(ResolveBattle(world, worldId, battle, now));
            if (phase != prev)
                packets.Add(BuildBattleUpdate(battle.Id, phase, ready, battleStart, endStart));
            lastPhase[battle.Id] = phase;
        }
        return packets;
    }

    void AccruePresence(SharedWorld world, AbyssTables.AirspaceBattle battle)
    {
        var tickPoints = AbyssTables.Embedded.Siege.PresencePoints;
        if (tickPoints <= 0) return;
        foreach (var airspaceId in battle.AirspaceIds)
        {
            if (AbyssTables.Embedded.AirspaceOf(airspaceId) is not { } airspace) continue;
            var guildsPresent = new HashSet<long>();
            foreach (var player in world.Online.Values)
            {
                if (player.Map?.MapId != airspace.MapId || player.Session.Guild is not { } g) continue;
                if (!IsApplicant(battle.Id, airspaceId, g.Id) && owners.GetValueOrDefault(airspaceId)?.GuildId != g.Id) continue;
                guildsPresent.Add(g.Id);
            }
            foreach (var guildId in guildsPresent)
            {
                var key = (airspaceId, guildId);
                siegePoints[key] = siegePoints.GetValueOrDefault(key) + tickPoints;
            }
        }
        rankingVersion++;
    }

    /// <summary>Fortress battle PvP: guild siege score and CharAirspacePoint_NT for the attacker.</summary>
    public byte[]? AccruePvpAction(SharedWorld world, WorldPlayer attacker, int airspaceId, int points, long now)
    {
        if (points <= 0 || attacker.Session.Guild is not { } guild) return null;
        var tables = AbyssTables.Embedded;
        AbyssTables.AirspaceBattle? battle = null;
        foreach (var b in tables.AirspaceBattles)
        {
            if (!b.AirspaceIds.Contains(airspaceId)) continue;
            if (PhaseAt(now, b.Id, out _, out _, out _) != PhaseBattle) return null;
            if (!IsApplicant(b.Id, airspaceId, guild.Id) && owners.GetValueOrDefault(airspaceId)?.GuildId != guild.Id) return null;
            battle = b;
            break;
        }
        if (battle is null) return null;
        var key = (airspaceId, guild.Id);
        siegePoints[key] = siegePoints.GetValueOrDefault(key) + points;
        charPoints[attacker.Key] = charPoints.GetValueOrDefault(attacker.Key) + points;
        rankingVersion++;
        return BuildCharAirspacePoint(attacker.Key, charPoints[attacker.Key]);
    }

    public int? CharAirspacePoints(int playerKey) => charPoints.GetValueOrDefault(playerKey);

    IEnumerable<byte[]> ResolveBattle(SharedWorld world, int worldId, AbyssTables.AirspaceBattle battle, long now)
    {
        var tables = AbyssTables.Embedded;
        var packets = new List<byte[]>();
        foreach (var airspaceId in battle.AirspaceIds)
        {
            var candidates = siegePoints.Where(kv => kv.Key.AirspaceId == airspaceId).ToList();
            if (candidates.Count == 0)
            {
                foreach (var guildId in Applicants(battle.Id, airspaceId))
                    candidates.Add(new KeyValuePair<(int, long), int>((airspaceId, guildId), 0));
            }
            if (candidates.Count == 0) continue;
            var winnerId = candidates.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.GuildId).First().Key.GuildId;
            var display = DisplayForGuild(winnerId, worldId, world);
            owners[airspaceId] = display;
            dirty = true;
            packets.Add(BuildProgressResult(airspaceId, display, now));
            var rewardedGuilds = new HashSet<long>();
            if (tables.SiegePayout.WinnerAp > 0) { pendingApGrants.Add((winnerId, tables.SiegePayout.WinnerAp)); rewardedGuilds.Add(winnerId); }
            foreach (var guildId in Applicants(battle.Id, airspaceId))
            {
                if (guildId == winnerId || !rewardedGuilds.Add(guildId)) continue;
                if (tables.SiegePayout.ParticipantAp > 0) pendingApGrants.Add((guildId, tables.SiegePayout.ParticipantAp));
            }
            foreach (var player in world.Online.Values)
            {
                if (player.Session.Guild is not { } g) continue;
                var ap = g.Id == winnerId ? tables.SiegePayout.WinnerAp
                    : IsApplicant(battle.Id, airspaceId, g.Id) ? tables.SiegePayout.ParticipantAp : 0;
                if (ap > 0) player.Session.QueueAbyssPointReward(ap);
            }
        }
        applications.RemoveWhere(a => a.BattleId == battle.Id);
        dirty = true;
        siegePoints.Clear();
        charPoints.Clear();
        return packets;
    }

    public IReadOnlyList<(long GuildId, int Ap)> TakePendingApGrants()
    {
        if (pendingApGrants.Count == 0) return [];
        var copy = pendingApGrants.ToList();
        pendingApGrants.Clear();
        return copy;
    }

    GuildDisplay DisplayForGuild(long guildId, int worldId, SharedWorld world)
    {
        if (owners.Values.FirstOrDefault(o => o.GuildId == guildId) is { } cached) return cached;
        foreach (var player in world.Online.Values)
            if (player.Session.Guild?.Id == guildId)
            {
                var g = player.Session.Guild;
                var master = g.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "";
                return new GuildDisplay(worldId, guildId, g.Name, g.Emblem, master);
            }
        return new GuildDisplay(worldId, guildId, $"Legion{guildId}", 0, "");
    }

    public int PhaseAt(long now, int battleId, out long readyTime, out long battleTime, out long endTime)
    {
        var schedule = AbyssTables.Embedded.Siege;
        var cycle = schedule.CycleMs;
        var anchor = schedule.AnchorMs;
        var offset = ((now - anchor) % cycle + cycle) % cycle;
        var waitMs = cycle - schedule.ApplyMs - schedule.BattleMs - schedule.EndMs;
        var cycleStart = now - offset;
        readyTime = cycleStart + waitMs;
        battleTime = readyTime + schedule.ApplyMs;
        endTime = battleTime + schedule.BattleMs;
        if (offset < waitMs) return PhaseWait;
        if (offset < waitMs + schedule.ApplyMs) return PhaseApply;
        if (offset < waitMs + schedule.ApplyMs + schedule.BattleMs) return PhaseBattle;
        if (offset < waitMs + schedule.ApplyMs + schedule.BattleMs + schedule.EndMs) return PhaseEnd;
        return PhaseWait;
    }

    public bool InPhase(long now, int phase) =>
        AbyssTables.Embedded.AirspaceBattles.Any(b => PhaseAt(now, b.Id, out _, out _, out _) == phase);

    public void WriteBattleSchedule(PacketWriter w, int battleId, long now)
    {
        var phase = PhaseAt(now, battleId, out var ready, out var battleStart, out var endStart);
        var battle = AbyssTables.Embedded.AirspaceBattles.First(b => b.Id == battleId);
        w.I32(battle.Id).I32(phase).I64(ready).I64(battleStart).I64(endStart);
        w.Varint(battle.AirspaceIds.Length);
        foreach (var airspaceId in battle.AirspaceIds)
        {
            w.I32(airspaceId).I32(phase == PhaseBattle ? 1 : 0);
            WriteOwnerGuild(w, owners.GetValueOrDefault(airspaceId));
        }
    }

    public void WriteOwnerGuild(PacketWriter w, GuildDisplay? g) =>
        w.I32(g?.ServerId ?? 0).I32((int)(g?.GuildId ?? 0)).String(g?.Name ?? "").I32(g?.Emblem ?? 0).String(g?.MasterName ?? "");

    public byte[] BuildBattleUpdate(int battleId, int phase, long ready, long battleStart, long endStart)
    {
        var battle = AbyssTables.Embedded.AirspaceBattles.First(b => b.Id == battleId);
        var w = PacketWriter.Opcode(GameSession.AirspaceBattleUpdateNt);
        w.I32(battle.Id).I32(phase).I64(ready).I64(battleStart).I64(endStart);
        w.Varint(battle.AirspaceIds.Length);
        foreach (var airspaceId in battle.AirspaceIds)
        {
            w.I32(airspaceId).I32(phase == PhaseBattle ? 1 : 0);
            WriteOwnerGuild(w, owners.GetValueOrDefault(airspaceId));
        }
        return w.ToArray();
    }

    public byte[] BuildProgressResult(int airspaceId, GuildDisplay owner, long now) =>
        PacketWriter.Opcode(GameSession.AirspaceBattleProgressResultNt).I32(airspaceId)
            .Also(w => WriteOwnerGuild(w, owner)).I64(now).ToArray();

    public byte[] BuildApplyNt(int battleId, int airspaceId, int login) =>
        PacketWriter.Opcode(GameSession.AirspaceBattleApplyNt).I32(battleId).I32(airspaceId).I32(login).ToArray();

    public byte[] BuildApplyCancelNt(int battleId, int airspaceId) =>
        PacketWriter.Opcode(GameSession.AirspaceBattleApplyCancelNt).I32(battleId).I32(airspaceId).ToArray();

    public static byte[] BuildCharAirspacePoint(int playerKey, int points) =>
        PacketWriter.Opcode(GameSession.CharAirspacePointNt).Varint(playerKey).I32(points).ToArray();

    public byte[] BuildApplyGuildList(SharedWorld world, int battleId, int airspaceId, int worldId)
    {
        var w = PacketWriter.Opcode(GameSession.AirspaceBattleApplyGuildListNt).I32(battleId).I32(airspaceId);
        var guildIds = Applicants(battleId, airspaceId);
        w.Varint(guildIds.Count);
        foreach (var guildId in guildIds)
            WriteOwnerGuild(w, DisplayForGuild(guildId, worldId, world));
        return w.ToArray();
    }

    public void WriteRanking(PacketWriter w, int airspaceId, long now)
    {
        var rows = siegePoints.Where(kv => kv.Key.AirspaceId == airspaceId).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.GuildId).ToList();
        w.U16(0).I32(airspaceId).I32((int)rankingVersion).I64(now + 3_600_000).Varint(rows.Count);
        var rank = 1;
        foreach (var ((_, guildId), points) in rows)
        {
            var g = owners.Values.FirstOrDefault(o => o.GuildId == guildId) ?? new GuildDisplay(loadedWorldId, guildId, $"Legion{guildId}", 0, "");
            w.I32(rank++).I32(g.ServerId).I32((int)g.GuildId).String(g.Name).I32(g.Emblem).I32(points);
        }
    }

    public bool Dirty => dirty;
    public void ClearDirty() => dirty = false;
}
