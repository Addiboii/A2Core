using Aion.Protocol.Native;

namespace Aion.World;

// Experience and levels. Experience is held per level (Exp.dat NeedExp to the next level), as the official
// level-ups show (capture 20261013-144836: ExpUpdate with exp 0 and the reward equal to NeedExp, then
// LevelUpdate). A level-up sends, in the official order, the contents it unlocks (0xe258), 0x8d07 ExpUpdate,
// 0x8d08 LevelUpdate, the skills the class learns (0x5101 AddSkillList, reason LevelUp), and 0x8d91 to those
// nearby; stats follow the new level (PcStatLevel). Kill and quest level-ups do not refill HP/MP on the player.
// Kill experience: OBSERVED none from field kills on the official server (capture 20261030/31); every ExpUpdate
// was a quest reward (reason 2). Scheduled experience events multiply quest rewards (SharedWorld.ExpRate).
public sealed partial class GameSession
{
    long experience;
    public long Experience => experience;
    /// <summary>NPC levels killed since the last experience update (unused while kill exp is off).</summary>
    readonly List<int> killedLevels = [];
    const byte ExpReasonNpc = 1, ExpReasonQuest = 2;   // EExpModifyReason kNpcReward, kQuestReward

    /// <summary>Always zero: official field kills did not grant experience.</summary>
    public long KillExperience(int npcLevel) => 0;

    async Task<List<byte[]>> KillExperienceAsync(CancellationToken ct)
    {
        killedLevels.Clear();
        return [];
    }

    /// <summary>
    /// Applies a new experience total (the current level's experience plus the gain): level-ups while it
    /// reaches NeedExp, stored, and the messages in the official order.
    /// </summary>
    async Task<List<byte[]>> GainExperienceAsync(long total, long gained, byte reason, CancellationToken ct)
    {
        if (reason == ExpReasonQuest && gained > 0)
        {
            var scaled = (long)Math.Round(gained * (options.World?.ExpRate ?? 1));
            total += scaled - gained;
            gained = scaled;
        }
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
            if (selected is { } sel && sel.Summary.CharacterId == character)
                selected = sel with { Progress = sel.Progress with { Level = newLevel, Experience = total } };
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
        LogLevelUp(before, newLevel);
        var daevanionNodesBefore = daevanionNodes.Count;
        EnsureStartNodes();
        RefreshKitSkillLevels();
        replies.AddRange(AchieveSet("PCLevel", level));
        var unlockedList = GamePackets.UnlockedContents(level).Where(t => !unlockedBefore.Contains(t)).ToList();
        if (!unlockedBefore.Contains(DaevanionContentsUnlock) && HasLevelUnlockedClassBoard())
            unlockedList.Add(DaevanionContentsUnlock);
        if (unlockedList.Count > 0)
            replies.Add(GamePackets.ContentsUnlockUpdate(unlockedList.Distinct().Order().ToArray()));
        replies.Add(GamePackets.ExpUpdate(total, gained, reason));
        replies.Add(GamePackets.LevelUpdate(level, level, total));
        if (className is { } cls && Profile() is { } profile)
        {
            var kit = data.SkillListForClient(cls, level, profile);
            learned = kit.Select(k => k.SkillId).ToHashSet();
            var added = kit.Where(k => !skillsBefore.Contains(k.SkillId)).ToList();
            if (added.Count > 0) replies.Add(GamePackets.AddSkillList(playerKey, added));
            replies.AddRange(PassiveAbnormals());
            replies.Add(SkillPointUpdate());
        }
        var broadcast = PlayerPackets.LevelBroadcast(playerKey, level);
        worldPlayer?.Map?.Broadcast(worldPlayer, [broadcast]);
        replies.AddRange(RefreshStats());
        if (daevanionNodes.Count > daevanionNodesBefore)
        {
            replies.Add(DaevanionBoardInfo());
            replies.Add(DaevanionPointUpdate());
        }
        if (reason != ExpReasonQuest && resources is { } r)
        {
            (r.Hp, r.Mp) = (r.HpMax, r.MpMax);
            replies.Add(r.Update());
        }
        return replies;
    }
}
