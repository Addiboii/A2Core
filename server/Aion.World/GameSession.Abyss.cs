using Aion.Protocol.Native;

namespace Aion.World;

// The Abyss (Reshanta: Dungeon.dat type Abyss, maps 20/22/23 — shared zones, not copies). In: the NpcTeleport NPCs
// (N_L1_AbyssPortal_01_001 → map 20; the teleport managers back to 1010) at the race's start point of the map
// (map-points Abyss_Reshanta_A_Light_StartPoint / _Dark_). On an Abyss map a cast at a player of the other race does
// PvP damage (Skill.dat PvP rows, the NPC-hit path for HP and death: 0x8d00, the kill 0x8d04 with the killer); the killer's
// KillPC / KillPCAbyssGrade quest objectives and achievements count and 100 Abyss Points arrive (OURS; the official points
// depend on the victim's rank). Dead players revive at their race's respawn point. Ranks, fortresses, artifacts and the
// rift portals are not modelled.
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
        foreach (var hit in Combat.SkillHits(data, skill, className, level, equippedWeapon, victim.Session.Defense, rng, pvp: true))
        {
            total += hit.Amount;
            replies.Add(GamePackets.SkillHit(victim.Key, playerKey, skill, hit.EffectId, hit.Amount, hit.Critical, ++seq));
        }
        if (replies.Count == 0) return [];
        victim.Send([.. replies, .. victim.Session.NpcHitsPlayer(total, now, playerKey)]);
        if (victim.Session.dead) replies.AddRange(AbyssKill(victim, now));
        return replies;
    }

    List<byte[]> AbyssKill(WorldPlayer victim, long now)
    {
        var replies = new List<byte[]>();
        replies.AddRange(Achieve("KillPC", 1, now: now));
        replies.AddRange(Achieve("KillPCAbyssGrade", 1, now: now));
        if (questLog is not null)
        {
            var changed = questLog.KilledPlayer(level);
            if (changed.Count > 0) pendingQuestChanges.AddRange(changed);
        }
        pendingRewards.Add([(AbyssPointItemId, AbyssKillPoints)]);
        return replies;
    }

    readonly List<Aion.Application.Gameplay.QuestRecord> pendingQuestChanges = [];
}
