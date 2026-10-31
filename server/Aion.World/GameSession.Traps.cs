using Aion.Protocol.Native;

namespace Aion.World;

// Player traps (tools/data/pc-traps.json): the Ranger's Explosion Trap 14170000 was accepted, drew its ground circle and did
// nothing (live 2026-10-30: no damage, no stun). The cast places the trap NPC at the cast point, owned by the caster; the
// first enemy inside its trigger radius, or the end of its lifetime, detonates it: every enemy in the blast takes the
// detonation's damage (0x3804) and its stun (0x382a, the NPC AI holds), then the trap leaves (0x3642 flag 7).
partial class GameSession
{
    sealed record PlacedTrap(int Key, string Group, int SkillId, PcTraps.Trap Trap, CastRequest Request, byte ActionUid, long ExpiresAt);

    readonly List<PlacedTrap> traps = [];

    /// <summary>A cast trap skill: the caster's previous trap of the skill leaves and the new one appears at the cast point.</summary>
    List<byte[]> PlaceTrap(int skillId, CastRequest request, byte actionUid, long now)
    {
        if (targets is null || PcTraps.For(skillId) is not { } trap) return [];
        var group = $"trap{skillId}";
        traps.RemoveAll(t => t.Group == group);
        var replies = Dismiss(group);
        var slot = Enumerable.Range(0, SummonSlots).FirstOrDefault(s => !targets.ContainsKey(SummonKey(s)));
        var key = SummonKey(slot);
        var t = new Target
        {
            Key = key, Name = "Ranger_Skill017_Trap", Hp = 1, HpMax = 1, NpcId = trap.Npc, Position = request.Location,
            Level = level, Direction = request.Yaw, Relationship = 22,   // ERelationshipEntity PC_Summon
            Immune = true, Radius = 1, OwnerKey = playerKey,
        };
        targets[key] = t;
        summonKeys[group] = key;
        seenNpcs.Add(key);
        replies.Add(GamePackets.NpcAppear(t.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
        traps.Add(new PlacedTrap(key, group, skillId, trap, request, actionUid, now + trap.LifetimeMs));
        return replies;
    }

    /// <summary>Traps whose trigger radius an enemy entered, or whose lifetime ended, detonate.</summary>
    List<byte[]> TrapTick(long now)
    {
        if (traps.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var placed in traps.ToList())
        {
            if (targets is null || !targets.TryGetValue(placed.Key, out var trap) || trap.Dead)
            {
                traps.Remove(placed);
                continue;
            }
            var r = placed.Trap.TriggerRadius;
            var triggered = targets.Values.Any(t => TrapVictim(t) && t.Position.DistanceSquared(trap.Position) <= (r + t.Radius) * (double)(r + t.Radius));
            if (triggered || now >= placed.ExpiresAt) replies.AddRange(Detonate(placed, trap, now));
        }
        return replies;
    }

    bool TrapVictim(Target t) => !t.Dead && t.OwnerKey == 0 && (t.Hostile || CombatMayHitNeutralForOpenKillObjective(t)) && PlayerMayDamageNpc(t);

    List<byte[]> Detonate(PlacedTrap placed, Target trap, long now)
    {
        traps.Remove(placed);
        var blast = placed.Trap.Blast(SkillAreas.For(placed.SkillId));
        var victims = targets!.Values
            .Where(t => TrapVictim(t) && blast.Contains(trap.Position, trap.Direction, t.Position, t.Radius))
            .OrderBy(t => t.Position.DistanceSquared(trap.Position)).Take(Math.Max(1, blast.Max)).ToList();
        var replies = new List<byte[]>();
        foreach (var victim in victims)
        {
            var hit = PlayerRowHits([placed.Trap.Damage], victim.Defense)[0];
            var questKill = questLog?.OpenKillNpcIds().Contains(victim.NpcId) == true;
            var mapEventKill = mapEventRuns.Any(run => run.OpenKillNpcIds().Contains(victim.NpcId));
            replies.AddRange(ApplyScheduledCombatHit(new ScheduledCombatHit(placed.SkillId, placed.Request, placed.ActionUid, victim.Key, hit, 1, now,
                CombatHitKind.Npc, questKill, mapEventKill), now));
        }
        replies.AddRange(Dismiss(placed.Group));
        return replies;
    }

    /// <summary>Tests: the traps this session has placed and not yet detonated.</summary>
    internal int PlacedTrapCount => traps.Count;

    /// <summary>Tests: cast trap skill <paramref name="skillId"/> at <paramref name="at"/>.</summary>
    internal List<byte[]> PlaceTrapForTest(int skillId, Vector3d at, long now) =>
        PlaceTrap(skillId, new CastRequest(skillId, 0, 0, false, playerKey, 0, at), 1, now);
}
