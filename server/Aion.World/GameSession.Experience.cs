using Aion.Protocol.Native;

namespace Aion.World;

// Experience and levels. Experience is held per level (Exp.dat NeedExp to the next level), as the official
// level-ups show (capture 20261013-144836: ExpUpdate with exp 0 and the reward equal to NeedExp, then
// LevelUpdate). A level-up sends, in the official order, the contents it unlocks (0xe258), 0x8d07 ExpUpdate,
// 0x8d08 LevelUpdate, the skills the class learns (0x5101 AddSkillList, reason LevelUp), and 0x8d91 to those
// nearby only; stats follow the new level (PcStatLevel). OBSERVED (our recordings 2026-10-13 144836 and 2026-10-19
// 231752, 9 level-ups): no 0x8d91 for the own character, and HP is not refilled (it keeps regenerating from where it
// was, e.g. 837 then 867 of 935 after level 5). The community fork found the same.
// Kill experience (ported from the community fork, which measured 1425 kill rewards, ExpUpdate reason 1, in its
// recordings 2026-10-30 to 11-07, own levels 1-45):
// - none below level 10. OBSERVED in ours: 137 deaths at own levels 4-8 (2026-10-13 and 10-19) and no kill reward; the
//   fork's: none at 3-7, the first at 10. Level 9 is unrecorded (CANDIDATE: 10 is the first paying level).
// - none inside an instance (dungeons, quest layers; the fork's ~520 kills). Ours agrees: 63 kills by the recorded
//   character in the dungeon map 900001 (DungeonWave_0001, 2026-11-06 084658), no kill reward.
// - grade 3 pays 390.8 x 1.085^monster level (CANDIDATE: the fork's fit within 0.25 % on 21 levels); grades 1/2/4 pay
//   5/13, 8/13 and 18/13 of it (the fork's OBSERVED ratios). By the gap (own level minus the monster's): full up to +5 and
//   for higher monsters, 0.6 at +7, 0.4 at +8, 0.2 at +9, 0.1 from +10 (the fork's OBSERVED); 0.8 at +6 CANDIDATE.
//   OBSERVED in ours: 300 for each of 4 kills of level-25 monsters (2400252/2400253, grade 3) by a character far above
//   them (2026-11-05 190433): 390.8 x 1.085^25 x 0.1 = 299.8.
// - nearby party members share evenly (the fork's OBSERVED for two; more: CANDIDATE).
public sealed partial class GameSession
{
    long experience;
    public long Experience => experience;
    /// <summary>The first level at which kills pay (CANDIDATE, see above).</summary>
    public const int KillExperienceLevel = 10;
    /// <summary>The grade-3 reward at monster level L: KillExperienceBase x KillExperienceGrowth^L (CANDIDATE fit).</summary>
    public const double KillExperienceBase = 390.8, KillExperienceGrowth = 1.085;
    /// <summary>The monsters killed since the last experience update (cast replies collect them): level, grade, in an instance.</summary>
    readonly List<(int Level, int Grade, bool Instance)> killedLevels = [];
    const byte ExpReasonNpc = 1, ExpReasonQuest = 2;   // EExpModifyReason kNpcReward, kQuestReward

    /// <summary>A grade's share of the grade-3 reward (5 : 8 : 13 : 18 for grades 1-4; grade 5 and up as 4, CANDIDATE).</summary>
    static double GradeFactor(int grade) => grade switch { <= 1 => 5 / 13.0, 2 => 8 / 13.0, 3 => 1, _ => 18 / 13.0 };

    /// <summary>The share by the level gap, own level minus the monster's (+6 CANDIDATE).</summary>
    public static double GapFactor(int gap) => gap switch { <= 5 => 1, 6 => 0.8, 7 => 0.6, 8 => 0.4, 9 => 0.2, _ => 0.1 };

    /// <summary>The experience a kill pays before rates and sharing (the rules above).</summary>
    public static long KillReward(int ownLevel, int npcLevel, int grade, bool instance)
    {
        if (instance || ownLevel < KillExperienceLevel) return 0;
        var mob = Math.Max(1, npcLevel);
        return (long)Math.Round(KillExperienceBase * Math.Pow(KillExperienceGrowth, mob) * GradeFactor(grade) * GapFactor(ownLevel - mob));
    }

    /// <summary>This character's experience for an open-field kill of a monster of this level and grade.</summary>
    public long KillExperience(int npcLevel, int grade = 3) => KillReward(level, npcLevel, grade, false);

    /// <summary>The experience of the kills from the last cast.</summary>
    async Task<List<byte[]>> KillExperienceAsync(CancellationToken ct)
    {
        if (killedLevels.Count == 0) return [];
        // The base rate (Aion:Rates:Experience) and scheduled experience events (SharedWorld.ExpRate) multiply kill experience.
        var gained = WorldRates.Scale(killedLevels.Sum(k => KillReward(level, k.Level, k.Grade, k.Instance)), (options.World?.Rates.Experience ?? 1) * (options.World?.ExpRate ?? 1));
        killedLevels.Clear();
        if (gained <= 0) return [];
        // Party members nearby share it evenly (no party bonus); each applies its share on its own loop.
        var members = NearbyPartyMembers();
        var share = Math.Max(1, gained / (members.Count + 1));
        foreach (var member in members) member.Session.ShareExperience(share, ct);
        return await GainExperienceAsync(experience + share, share, ExpReasonNpc, ct);
    }

    /// <summary>Checks only: experience gained as from kills (level-ups and their quest re-check).</summary>
    internal Task<List<byte[]>> GainExperienceForChecks(long gained, CancellationToken ct) => GainExperienceAsync(experience + gained, gained, ExpReasonNpc, ct);

    /// <summary>
    /// Applies a new experience total (the current level's experience plus the gain): level-ups while it
    /// reaches NeedExp, stored, and the messages in the official order. A level-up re-checks the quests (<paramref name="questRecheck"/>;
    /// a quest reward re-checks them itself after its 0x8d0f).
    /// </summary>
    async Task<List<byte[]>> GainExperienceAsync(long total, long gained, byte reason, CancellationToken ct, bool questRecheck = true)
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
            if (added.Count > 0) replies.Add(GamePackets.AddSkillList(playerKey, added, daevanion: DaevanionSkillBonus, equip: EquipSkillBonus));
            replies.AddRange(PassiveAbnormals());
        }
        worldPlayer?.Map?.Broadcast(worldPlayer, [PlayerPackets.LevelBroadcast(playerKey, level)]);
        replies.AddRange(RefreshStats());
        if (DaevanionBoards(className, before).Count != DaevanionBoards(className, level).Count) replies.Add(DaevanionBoardInfo());
        // No HP/MP refill and no own level broadcast at a level-up (GitHub 2d7b790, as recorded).
        // The new level moves the quests waiting on it (audit 2026-11-13 G1): PCLevel steps (HQ1103010 "reach 45", HQ1103620,
        // HQ2103010, HQ2103600) and the Auto quests with an UnlockLevel above 1 (34, e.g. the Ascension AQ1602010 at 10) were taken
        // only at the next quest event or relog. QuestChangesAsync ends with AutoAcquireAsync. CANDIDATE order: after the level-up
        // messages (no official level-up that moved a quest is recorded).
        if (questRecheck && questLog is { } quests)
            replies.AddRange(await Stored(() => QuestChangesAsync(quests.Recheck(level), ct)));
        return replies;
    }
}
