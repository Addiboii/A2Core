using Aion.Protocol.Native;

namespace Aion.World;

// Player damage zones (tools/data/pc-zones.json): the Sorcerer's Bittercold Wind 15280000 was accepted and did no damage (live
// 2026-10-30): the skill has no Damage row, its Spawn effect only places an invisible summon, and PcSummons ignores summons
// without attacks. The cast places that marker at the target; each tick of its life every enemy in the area takes the zone's
// damage (0x3804), then the marker leaves (0x3642 flag 7). Its kills are the caster's.
partial class GameSession
{
    sealed record PlacedZone(int Key, string Group, int SkillId, PcZones.Zone Zone, CastRequest Request, byte ActionUid, int TicksLeft, long NextTickAt);

    readonly List<PlacedZone> zones = [];

    /// <summary>A cast zone skill: the caster's previous zone of the skill leaves and the new one appears at the target.</summary>
    List<byte[]> PlaceZone(int skillId, CastRequest request, byte actionUid, long now)
    {
        if (targets is null || PcZones.For(skillId) is not { } zone) return [];
        var group = $"zone{skillId}";
        zones.RemoveAll(z => z.Group == group);
        var replies = Dismiss(group);
        var slot = Enumerable.Range(1, SummonSlots).Select(i => (lastSummonSlot + i) % SummonSlots).FirstOrDefault(s => !targets.ContainsKey(SummonKey(s)));
        lastSummonSlot = slot;
        var key = SummonKey(slot);
        var centre = request.TargetKey != playerKey && targets.TryGetValue(request.TargetKey, out var locked) ? locked.Position : request.Location;
        var t = new Target
        {
            Key = key, Name = zone.Name, Hp = 1, HpMax = 1, NpcId = zone.Npc, Position = centre,
            Level = level, Direction = request.Yaw, Relationship = 22,   // ERelationshipEntity PC_Summon
            Immune = true, Radius = 1, OwnerKey = playerKey,
        };
        targets[key] = t;
        summonKeys[group] = key;
        seenNpcs.Add(key);
        replies.Add(GamePackets.NpcAppear(t.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
        zones.Add(new PlacedZone(key, group, skillId, zone, request, actionUid, zone.Ticks, now + NpcAi.HitDelayMs));
        return replies;
    }

    /// <summary>Zones whose next tick is due hurt the enemies in their area; a zone with no ticks left leaves.</summary>
    List<byte[]> ZoneTick(long now)
    {
        if (zones.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var placed in zones.ToList())
        {
            if (targets is null || !targets.TryGetValue(placed.Key, out var marker) || marker.Dead)
            {
                zones.Remove(placed);
                continue;
            }
            var current = placed;
            while (current.TicksLeft > 0 && now >= current.NextTickAt)
            {
                replies.AddRange(ZoneDamage(current, marker, current.NextTickAt));
                current = current with { TicksLeft = current.TicksLeft - 1, NextTickAt = current.NextTickAt + Math.Max(1, current.Zone.TickMs) };
            }
            zones[zones.IndexOf(placed)] = current;
            if (current.TicksLeft > 0) continue;
            zones.Remove(current);
            replies.AddRange(Dismiss(current.Group));
        }
        return replies;
    }

    List<byte[]> ZoneDamage(PlacedZone placed, Target marker, long at)
    {
        var area = placed.Zone.Blast(SkillAreas.For(placed.SkillId));
        var victims = targets!.Values
            .Where(t => TrapVictim(t) && area.Contains(marker.Position, marker.Direction, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(marker.Position)).Take(Math.Max(1, area.Max)).ToList();
        var replies = new List<byte[]>();
        foreach (var victim in victims)
        {
            var hit = PlayerRowHits([placed.Zone.Damage], victim.Defense)[0];
            var questKill = questLog?.OpenKillNpcIds().Contains(victim.NpcId) == true;
            var mapEventKill = mapEventRuns.Any(run => run.OpenKillNpcIds().Contains(victim.NpcId));
            replies.AddRange(ApplyScheduledCombatHit(new ScheduledCombatHit(placed.SkillId, placed.Request, placed.ActionUid, victim.Key, hit, 1, at,
                CombatHitKind.Npc, questKill, mapEventKill), at));
        }
        return replies;
    }

    /// <summary>Tests: the zones this session has placed and not yet ended.</summary>
    internal int PlacedZoneCount => zones.Count;

    /// <summary>Tests: cast zone skill <paramref name="skillId"/> on <paramref name="targetKey"/> (0: at <paramref name="at"/>).</summary>
    internal List<byte[]> PlaceZoneForTest(int skillId, int targetKey, Vector3d at, long now) =>
        PlaceZone(skillId, new CastRequest(skillId, 0, 0, false, targetKey == 0 ? playerKey : targetKey, 0, at), 1, now);
}
