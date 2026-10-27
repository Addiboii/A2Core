using Aion.Protocol.Native;

namespace Aion.World;

// Experience and levels. Experience is held per level (Exp.dat NeedExp to the next level), as the official
// level-ups show (capture 20261013-144836: ExpUpdate with exp 0 and the reward equal to NeedExp, then
// LevelUpdate). A level-up sends, in the official order, the contents it unlocks (0xe258), 0x8d07 ExpUpdate,
// 0x8d08 LevelUpdate, the skills the class learns (0x5101 AddSkillList, reason LevelUp), and 0x8d91 to those
// nearby; stats follow the new level (PcStatLevel) and the pools refill.
// Kill experience (OURS: no table gives it and the official kills sent none, the recorded character being
// above those mobs): NeedExp at the mob's level / KillsPerLevel, scaled by the level gap.
public sealed partial class GameSession
{
    long experience;
    public long Experience => experience;
    /// <summary>OURS: a mob of the player's level gives 1/KillsPerLevel of that level's NeedExp.</summary>
    public const int KillsPerLevel = 25;
    /// <summary>NPC levels killed since the last experience update (cast replies collect them).</summary>
    readonly List<int> killedLevels = [];
    const byte ExpReasonNpc = 1, ExpReasonQuest = 2;   // EExpModifyReason kNpcReward, kQuestReward

    /// <summary>Experience for killing a mob: its level's share, less for weaker mobs (none 10+ levels below), more for stronger ones.</summary>
    public long KillExperience(int npcLevel)
    {
        var mob = data.ClampLevel(Math.Max(1, npcLevel));
        var gap = mob - level;
        var factor = gap >= 0 ? 1 + Math.Min(gap, 5) * 0.1 : Math.Max(0, 1 + gap * 0.1);
        return (long)Math.Round(data.NeedExp(mob) / (double)KillsPerLevel * factor);
    }

    /// <summary>The experience of the kills from the last cast.</summary>
    async Task<List<byte[]>> KillExperienceAsync(CancellationToken ct)
    {
        if (killedLevels.Count == 0) return [];
        // Scheduled experience events multiply kill experience (SharedWorld.ExpRate).
        var gained = (long)Math.Round(killedLevels.Sum(KillExperience) * (options.World?.ExpRate ?? 1));
        killedLevels.Clear();
        if (gained <= 0) return [];
        // Party members nearby share it evenly (OURS: no party bonus); each applies its share on its own loop.
        var members = NearbyPartyMembers();
        var share = Math.Max(1, gained / (members.Count + 1));
        foreach (var member in members) member.Session.ShareExperience(share, ct);
        return await GainExperienceAsync(experience + share, share, ExpReasonNpc, ct);
    }

    /// <summary>
    /// Applies a new experience total (the current level's experience plus the gain): level-ups while it
    /// reaches NeedExp, stored, and the messages in the official order.
    /// </summary>
    async Task<List<byte[]>> GainExperienceAsync(long total, long gained, byte reason, CancellationToken ct)
    {
        var before = level;
        var newLevel = level;
        while (newLevel < data.MaxLevel && data.NeedExp(newLevel) is > 0 and var need && total >= need)
        {
            total -= need;
            newLevel++;
        }
        if (newLevel >= data.MaxLevel) total = Math.Min(total, Math.Max(0, data.NeedExp(data.MaxLevel) - 1));
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SaveProgressAsync(character, newLevel, total, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
        }
        experience = total;
        var replies = new List<byte[]>();
        if (newLevel == before)
        {
            replies.Add(GamePackets.ExpUpdate(total, gained, reason));
            return replies;
        }
        var unlockedBefore = GamePackets.UnlockedContents(before).ToHashSet();
        var skillsBefore = learned.ToHashSet();
        level = newLevel;
        replies.AddRange(AchieveSet("PCLevel", level));
        var unlocked = GamePackets.UnlockedContents(level).Where(t => !unlockedBefore.Contains(t)).ToArray();
        if (unlocked.Length > 0) replies.Add(GamePackets.ContentsUnlockUpdate(unlocked));
        replies.Add(GamePackets.ExpUpdate(total, gained, reason));
        replies.Add(GamePackets.LevelUpdate(level, level, total));
        if (className is { } cls)
        {
            var kit = data.LearnedSkills(cls, level);
            learned = kit.Select(k => k.SkillId).ToHashSet();
            var added = kit.Where(k => !skillsBefore.Contains(k.SkillId)).ToList();
            if (added.Count > 0) replies.Add(GamePackets.AddSkillList(playerKey, added));
            replies.AddRange(PassiveAbnormals());
        }
        var broadcast = PlayerPackets.LevelBroadcast(playerKey, level);
        worldPlayer?.Map?.Broadcast(worldPlayer, [broadcast]);
        replies.Add(broadcast);
        replies.AddRange(RefreshStats());
        if (resources is { } r)
        {
            (r.Hp, r.Mp) = (r.HpMax, r.MpMax);
            replies.Add(r.Update());
        }
        return replies;
    }
}
