using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Chain skills (Skill.dat ChainSkillPrevSkillId): casting a skill offers its chain steps for their ChainSkillAvailableTime
/// (0x3818 ChainSkillActiveList_NT); a step is castable only while offered, and casting it or the window closing withdraws it
/// (0x3819 ChainSkillDeactiveList_NT). OBSERVED in the 2.0.6 recording 20261013-144836: the cast of 11020000 offered 11030000
/// (#875), the cast of 11030000 offered 11040000 then withdrew itself (#956, #957, in that order, before the 0x3801), and
/// 11040000, never cast, was withdrawn 3046 ms later (#1142; its window is 3000 ms). Idea and opcodes from A2Core PR #4
/// (Artezianec), GameSession.SkillChains.cs; the chain data comes from the client tables only (tools/class_skills.py), none of
/// the PR's improvised damage rows or effect ids.
/// </summary>
partial class GameSession
{
    /// <summary>OURS: the window of a chain step whose ChainSkillAvailableTime is 0 (every step of the 2.0.6 tables has one).</summary>
    public const int DefaultChainWindowMs = 3000;

    /// <summary>The chain steps on offer and when each window closes.</summary>
    readonly Dictionary<int, long> activeChains = [];

    /// <summary>Whether <paramref name="skill"/> is a chain step on offer now (it is then castable without being learned).</summary>
    bool ChainStepReady(int skill, long now) => activeChains.TryGetValue(skill, out var until) && now <= until;

    /// <summary>The chain prompts of an accepted cast: its steps offered (0x3818), then itself withdrawn if it was a step (0x3819).</summary>
    List<byte[]> ChainCastReplies(int skill, SkillDefinition definition, long now)
    {
        var replies = new List<byte[]>();
        var offered = new List<int>();
        foreach (var next in definition.ChainNext ?? [])
        {
            if (data.Skill(next) is not { } step) continue;
            // ChainSkillActivateRate in 1/10000 (CANDIDATE reading; every 2.0.6 step has 10000).
            if (step.ChainRate is > 0 and < 10_000 && rng.Next(10_000) >= step.ChainRate) continue;
            activeChains[next] = now + (step.ChainWindowMs > 0 ? step.ChainWindowMs : DefaultChainWindowMs);
            offered.Add(next);
        }
        if (offered.Count > 0) replies.Add(GamePackets.ChainSkillActiveList(skill, offered));
        if (activeChains.Remove(skill)) replies.Add(GamePackets.ChainSkillDeactiveList([skill]));
        return replies;
    }

    /// <summary>Chain steps whose window closed: withdrawn together (0x3819).</summary>
    List<byte[]> ChainTick(long now)
    {
        if (activeChains.Count == 0) return [];
        var expired = activeChains.Where(c => now > c.Value).Select(c => c.Key).Order().ToList();
        if (expired.Count == 0) return [];
        foreach (var skill in expired) activeChains.Remove(skill);
        return [GamePackets.ChainSkillDeactiveList(expired)];
    }
}
