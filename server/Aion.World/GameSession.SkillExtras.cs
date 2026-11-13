using Aion.Protocol.Native;

namespace Aion.World;

// The skill guide's per-hit extras (tools/data/skill-guide.json, user request 2026-11-08 "next in the skill rework"): lifesteal,
// MP restored on hit, multi-hit skills, and the crowd control of the few skills whose client abnormal rows we do not have.
public sealed partial class GameSession
{
    /// <summary>The client's most common crowd-control abnormal (Skill.dat rows, 1054 uses): the stun visual for guide-only stuns.</summary>
    public const int GuideStunAbnormal = 100000311;

    /// <summary>"Deals damage twice" / "strikes 4 times": a skill with one damage row hits that many times.</summary>
    static List<Hit> GuideRepeat(int skill, List<Hit> hits) =>
        Guide.Of(skill) is { Repeat: > 1 } g && hits.Count == 1 ? Enumerable.Repeat(hits[0], g.Repeat).ToList() : hits;

    /// <summary>Crowd control the guide names for a skill without client abnormal rows: stun-type holds the monster, Root/Slow slow it.</summary>
    List<byte[]> GuideCrowdControl(int skill, Target target, long now)
    {
        if (Guide.Of(skill) is not { Effects.Count: > 0 } g || Behaviour.Of(skill)?.Abnormals is { Length: > 0 } || target.Dead) return [];
        var replies = new List<byte[]>();
        foreach (var e in g.Effects)
        {
            if (e.When is not null || rng.Next(100) >= e.Chance) continue;   // "on Block/Parry": not modelled
            var ms = e.DurationMs > 0 ? e.DurationMs : 3000;
            switch (e.Name)
            {
                case "Stun" or "Knockdown" or "Airborne" or "Sleep" or "Fear" or "Frost" or "Stone" or "Paralysis" or "Grab" or "Seal" or "Silence":
                    mapEventNow = now;
                    AddNpcBuff(target, GuideStunAbnormal, ms, hold: true, casterKey: playerKey, skillId: skill);
                    replies.AddRange(mapEventOut);
                    mapEventOut.Clear();
                    break;
                case "Root" or "Bind" or "Snare":
                    target.AddMod("MoveSpeed", -100, now + ms, skill);   // cannot move, still hits what is in reach
                    break;
                case "Slow":
                    target.AddMod("MoveSpeed", -40, now + ms, skill);
                    break;
            }
        }
        return replies;
    }

    /// <summary>"Absorbs N% HP": the caster heals by that share of the damage the cast dealt.</summary>
    List<byte[]> GuideLifesteal(int skill, int dealt)
    {
        if (Guide.Of(skill) is not { LifestealPercent: > 0 } g || dealt <= 0 || resources is not { } r || r.Hp >= r.HpMax || dead) return [];
        r.Hp = Math.Min(r.HpMax, r.Hp + HealReceived(Math.Max(1, dealt * g.LifestealPercent / 100)));
        return [GamePackets.NpcHp(playerKey, r.Hp)];
    }

    /// <summary>"Restores N MP" on hit (the skill text's amount; otherwise the class rule of mp-gain.xml).</summary>
    int GuideMpRestore(int skill) => Guide.Of(skill)?.MpRestore is > 0 and var mp ? mp : MpGain.Embedded.PerCast(className);

    /// <summary>How much the skill's damage grows with its learned level (the guide's own per-level table; 1.0 when unknown).</summary>
    double GuideLevelScale(int skill) => Guide.Of(skill)?.LevelScale(CombatSkillLevel(skill)) ?? 1.0;
}
