using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using G = Aion.Protocol.Native.Generated;

namespace Aion.World;

// ---------- Duty missions and duty scrolls (audit 2026-11-13: quests G4, systems G1; ACTION-PLAN 4.1) ----------
// The 783 daily duty quests: 206 DutyMission (the mission board, AcquireType None) and 577 DutyScroll (a scroll item, UseEffect
// GetDutyQuest [group]). Data: DutyMissions.cs / tools/data/duty.json.
// Messages (0x8d family, catalogue order from QuestList_NT 0x8d09 = 0x3944 + 0x53c5): QuestRollDuty_RQ/RS 0x8d13/0x8d14,
// DutyMissionQuestJournalStatus_RQ/RS/NT 0x8d1a/0x8d1b/0x8d1c, RefreshDutyMissionQuestList_RQ/RS 0x8d1d/0x8d1e,
// DutyMissionQuestAccept_RQ/RS 0x8d1f/0x8d20.
// OBSERVED (s2c only; the recordings hold no request body): the board NT at the world entry before the quest list (rec 20261106-115817
// #173793, then 0x8d09 #173810), and RS {0} + NT as the answer to the board's opening (rec 20261106-145442 #6632/#6635); accepted
// missions are in the quest list with their offered random rewards (#173810, byte-exact in the checks).
// Rules from the client tables: 15 missions a board (GlobalSetting dutymission_quest_journal_acquired_count), 20 duty quests held at
// once (QuestType GetLimitCount, both types count together: GetLimitCountingGroupId 6), 5 missions rewarded a day (DutyMission
// CompleteLimitCount; "Cannot accept more due to daily server completion limit (5/5)"), board refreshes and scroll re-draws cost Kina
// (DutyQuestRedrawCost: 1000, 2500, 6000 ... / 1000, 1900, 3500 ..., 10 at most), the day turns at 16:00 KST (07:00 UTC).
// CANDIDATE: request layouts (the usmap fields: Accept {uid i32, quest id i32}, RollDuty {quest uid i32}), the replies' order, the
// limits' results, which quests a scroll group draws (duty_data.py), the paid chance of each offered item, a re-drawn quest's new uid
// (QuestRemove reason 4 InvalidQuest for the old one), a given-up mission offered again, a rewarded mission kept on the board as 4.
public sealed partial class GameSession
{
    public const ushort QuestRollDutyRq = 0x8d13, DutyJournalRq = 0x8d1a, DutyRefreshRq = 0x8d1d, DutyRefreshRs = 0x8d1e,
        DutyAcceptRq = 0x8d1f, DutyAcceptRs = 0x8d20;

    static DutyTables DutyData => DutyTables.Embedded;
    DutyState duty = new();
    /// <summary>The character's mission board and duty quest data (checks).</summary>
    public DutyState Duty => duty;
    long DutyNow => lastNow != 0 ? lastNow : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A quest on the wire; a held duty quest carries its re-draw count and offered random rewards.</summary>
    QuestWire Wire(QuestRecord q) =>
        q.Status is QuestLog.Incomplete or QuestLog.Complete && duty.Quests.TryGetValue(q.Code, out var extra)
            ? new(q.QuestId, (byte)q.Status, q.Step, q.Goals, (int)q.Uid, extra.Roll, extra.Rewards.Select(r => (r.Item, r.Count, r.Enchant, r.Prob)).ToList())
            : new(q.QuestId, (byte)q.Status, q.Step, q.Goals, (int)q.Uid);

    async Task LoadDutyAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        duty = DutyState.Load(await store.CharacterSettingsAsync(character, ct));
        foreach (var code in duty.Quests.Keys.Where(c => !DutyHeld(c)).ToList()) duty.Quests.Remove(code);
    }

    async Task SaveDutyAsync(CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return;
        var (board, quests) = (duty.BoardJson(), duty.QuestsJson());
        await Stored(async () =>
        {
            await store.SaveCharacterSettingAsync(character, DutyState.DutyBoardSetting, board, ct);
            await store.SaveCharacterSettingAsync(character, DutyState.DutyQuestsSetting, quests, ct);
            return [];
        });
    }

    bool DutyHeld(string code) => questLog?.Get(code) is { Status: QuestLog.Incomplete or QuestLog.Complete };

    /// <summary>Duty quests held now (both types: QuestType GetLimitCountingGroupId 6).</summary>
    int DutyHeldCount() => questLog?.All.Count(q => q.Status is QuestLog.Incomplete or QuestLog.Complete && DutyTables.IsDuty(questLog.Book.Find(q.Code)?.Type)) ?? 0;

    async Task<List<byte[]>?> DutyRequestAsync(byte[] plain, long now, CancellationToken ct) => Opcode(plain) switch
    {
        DutyJournalRq => await DutyJournalAsync(now, ct),
        DutyRefreshRq => await DutyRefreshAsync(now, ct),
        DutyAcceptRq when plain.Length >= 10 => await DutyAcceptAsync(BitConverter.ToInt32(plain, 2), BitConverter.ToInt32(plain, 6), now, ct),
        QuestRollDutyRq when plain.Length >= 6 => await QuestRollDutyAsync(BitConverter.ToInt32(plain, 2), ct),
        _ => null,
    };

    /// <summary>DutyMissionQuestJournalStatus_NT 0x8d1c: counters, the board, expiry, the uids dropped from it.</summary>
    byte[] DutyBoardNotice(IReadOnlyList<int>? deleted = null) => new G.DutyMissionQuestJournalStatusNT
    {
        RefreshedCount = duty.Refreshed,
        DutymissionQuestRewardedCount = duty.Rewarded,
        QuestsList = duty.Board.Select(e => new G.CommonDutyMissionQuest
        {
            Uid = e.Uid, QuestId = e.QuestId, QuestUid = e.QuestUid, PublisherId = 0, Status = e.Status,
            RandomRewardItemsWithProb = e.Rewards.Select(r => new G.CommonRewardItemWithProb { ItemId = r.Item, Count = r.Count, EnchantLevel = r.Enchant, Prob = r.Prob }).ToList(),
        }).ToList(),
        ExpireTime = duty.Expire,
        DeletedQuestUid = [.. deleted ?? []],
        WeeklyExpireTime = duty.WeeklyExpire,
    }.ToArray();

    /// <summary>The board at the world entry, as stored (OBSERVED: an old board is sent unchanged); none before the first board.</summary>
    byte[]? DutyEntryNotice() => duty.Board.Count > 0 ? DutyBoardNotice() : null;

    /// <summary>A new duty day resets the counters and redraws; a board never drawn (or empty and past its 5 s) is drawn.</summary>
    List<int> DutyEnsure(long now)
    {
        if (questLog is null) return [];
        var newDay = duty.StartCycle(DutyData.CycleOf(now));
        return newDay || now >= duty.Expire ? duty.Draw(DutyData, questLog.Book, profileRace, level, DutyHeld, rng, now) : [];
    }

    /// <summary>DutyMissionQuestJournalStatus_RQ (opening the board): RS {0} and the board (OBSERVED pair).</summary>
    async Task<List<byte[]>> DutyJournalAsync(long now, CancellationToken ct)
    {
        if (questLog is null) return [new G.DutyMissionQuestJournalStatusRS { Result = 1 }.ToArray()];
        var deleted = DutyEnsure(now);
        await SaveDutyAsync(ct);
        return [new G.DutyMissionQuestJournalStatusRS { Result = 0 }.ToArray(), DutyBoardNotice(deleted)];
    }

    /// <summary>RefreshDutyMissionQuestList_RQ: the offered missions redrawn for the day's next price (accepted ones stay).</summary>
    async Task<List<byte[]>> DutyRefreshAsync(long now, CancellationToken ct)
    {
        static byte[] Reply(int result) => PacketWriter.Opcode(DutyRefreshRs).U16(result).ToArray();
        if (questLog is null) return [Reply(1)];
        DutyEnsure(now);
        var redraw = DutyData.RedrawOf("DutyMission");
        if (!DutyState.Eligible(DutyData, questLog.Book, profileRace, level).Any()) return [Reply(EResult.DutyMission_NotEnoughQuestPool)];
        if (redraw is null || duty.Refreshed >= Math.Min(redraw.Max, redraw.Costs.Length)) return [Reply(EResult.DutyMission_NotEnoughRefreshCount)];
        var cost = redraw.Costs[duty.Refreshed];
        if (CostStacks((int)cost[0], cost[1]) is not { } stacks) return [Reply(EResult.DutyMission_NotEnoughMoney)];
        return await Stored(async () =>
        {
            if (Store is { } store && CharacterId is { } character)
                foreach (var (item, revision, count) in stacks)
                    if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [Reply(EResult.DutyMission_NotEnoughMoney)];
            duty.Refreshed++;
            var deleted = duty.Draw(DutyData, questLog.Book, profileRace, level, DutyHeld, rng, now);
            await SaveDutyAsync(ct);
            return [.. await InventoryChangesAsync(ct), Reply(0), DutyBoardNotice(deleted)];
        });
    }

    /// <summary>DutyMissionQuestAccept_RQ {board uid, quest id}: the mission joins the quest log with its offered rewards.</summary>
    async Task<List<byte[]>> DutyAcceptAsync(int uid, int questId, long now, CancellationToken ct)
    {
        byte[] Reply(int result, int questUid = 0) => PacketWriter.Opcode(DutyAcceptRs).U16(result).I32(uid).I32(questId).I32(questUid).ToArray();
        if (questLog is null) return [Reply(1)];
        if (duty.Cycle != DutyData.CycleOf(now) || now >= duty.Expire) return [Reply(EResult.DutyMission_HasExpired)];
        var index = duty.Board.FindIndex(e => e.Uid == uid);
        if (index < 0 || duty.Board[index] is not { Status: 1 } entry || questId != 0 && entry.QuestId != questId
            || !questLog.Book.ById.TryGetValue(entry.QuestId, out var code) || questLog.Book.Find(code) is not { } quest)
            return [Reply(1)];
        var rule = DutyData.Rule(quest.Type);
        if (rule is { CompleteLimit: > 0 } && duty.Rewarded >= rule.CompleteLimit) return [Reply(EResult.DutyQuest_Fail_DailyPlayLimit)];
        if (DutyHeldCount() >= (rule?.GetLimit ?? 20)) return [Reply(EResult.DutyQuest_Fail_AcceptLimit)];
        if (level < quest.Level) return [Reply(EResult.Quest_AcceptFail_NotEnoughLevel)];
        if (!QuestBook.RaceMatches(quest, profileRace)) return [Reply(EResult.Quest_AcceptFail_InvalidRace)];
        if (DutyHeld(code)) return [Reply(EResult.Quest_Alreadyaccept)];
        return await Stored(async () =>
        {
            var record = await DutyTakeAsync(code, quest, new DutyQuestExtra(0, null, entry.Rewards), ct);
            duty.Board[index] = entry with { Status = 2, QuestUid = (int)record.Uid };
            await SaveDutyAsync(ct);
            return [Reply(0, (int)record.Uid), GamePackets.QuestAdd(Wire(record)), DutyBoardNotice()];
        });
    }

    /// <summary>
    /// A duty quest joins the log. A rewarded copy (RepeatType Infinitely) leaves first, so the new one gets its own uid: the store's
    /// reward grants are idempotent per quest uid.
    /// </summary>
    async Task<QuestRecord> DutyTakeAsync(string code, QuestDefinition quest, DutyQuestExtra extra, CancellationToken ct)
    {
        if (questLog!.Get(code) is not null)
        {
            if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, code, ct);
            questLog.Remove(code);
        }
        duty.Quests[code] = extra;
        return await SaveQuestAsync(QuestLog.New(code, quest), ct);
    }

    /// <summary>The scroll group's quests the character may draw now (race, level, not held; <paramref name="except"/> left out).</summary>
    List<string> DutyScrollPool(DutyTables.Group group, string? except) =>
        group.Quests.Where(c => c != except && questLog!.Book.Find(c) is { } q && QuestBook.RaceMatches(q, profileRace) && level >= q.Level
                                && q.Steps.Length > 0 && !DutyHeld(c)).ToList();

    /// <summary>
    /// A duty scroll (UseEffect GetDutyQuest [group]): one quest of the group, with its random rewards rolled; the scroll is used up.
    /// Refused (the scroll kept) at 20 duty quests held or when the group has nothing to give.
    /// </summary>
    async Task<List<byte[]>> UseDutyScrollAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, long uid, bool reply, CancellationToken ct)
    {
        List<byte[]> Refuse(int result) => reply ? [RidePackets.ItemUseReply(result, uid, row.TemplateId)] : [];
        if (questLog is null || use.Values is not [var groupName, ..] || !DutyData.Groups.TryGetValue(groupName, out var group)) return Refuse(1);
        if (DutyHeldCount() >= (DutyData.Rule("DutyScroll")?.GetLimit ?? 20)) return Refuse(EResult.DutyQuest_Fail_AcceptLimit);
        var pool = DutyScrollPool(group, null);
        if (pool.Count == 0) return Refuse(EResult.DutyMission_NotEnoughQuestPool);
        var code = pool[rng.Next(pool.Count)];
        var quest = questLog.Book.Find(code)!;
        var done = await Stored(async () =>
        {
            if (!await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct)) return [];
            var record = await DutyTakeAsync(code, quest, new DutyQuestExtra(0, groupName, DutyData.Roll(quest.Rewards?.Random, rng)), ct);
            await SaveDutyAsync(ct);
            return [.. Used(row, reply), .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse), GamePackets.QuestAdd(Wire(record))];
        });
        return done.Count > 0 ? done : Refuse(1);
    }

    /// <summary>
    /// QuestRollDuty_RQ {quest uid}: a scroll quest (DutyScroll bCanReroll) is swapped for another of its group, for the next
    /// re-draw price (DutyScroll_cost, 10 at most); progress starts over, the re-draw count goes with it.
    /// </summary>
    async Task<List<byte[]>> QuestRollDutyAsync(int questUid, CancellationToken ct)
    {
        if (questLog?.ByUid(questUid) is not { } record || questLog.Book.Find(record.Code) is not { } quest)
            return [GamePackets.QuestRollDutyReply(1, new QuestWire(0, 0, 1, new int[6], questUid))];
        byte[] Refuse(int result) => GamePackets.QuestRollDutyReply(result, Wire(record));
        if (record.Status != QuestLog.Incomplete || DutyData.Rule(quest.Type) is not { Reroll: true }) return [Refuse(1)];
        var extra = duty.Quests.GetValueOrDefault(record.Code) ?? new DutyQuestExtra(0, null, []);
        var groupName = extra.Group ?? DutyData.GroupOf(record.Code);
        var redraw = DutyData.RedrawOf(quest.Type);
        if (groupName is null || !DutyData.Groups.TryGetValue(groupName, out var group) || redraw is null) return [Refuse(1)];
        if (extra.Roll >= Math.Min(redraw.Max, redraw.Costs.Length)) return [Refuse(EResult.DutyMission_NotEnoughRefreshCount)];
        var pool = DutyScrollPool(group, record.Code);
        if (pool.Count == 0) return [Refuse(EResult.DutyMission_NotEnoughQuestPool)];
        var cost = redraw.Costs[extra.Roll];
        if (CostStacks((int)cost[0], cost[1]) is not { } stacks) return [Refuse(EResult.DutyMission_NotEnoughMoney)];
        var code = pool[rng.Next(pool.Count)];
        var next = questLog.Book.Find(code)!;
        return await Stored(async () =>
        {
            if (Store is { } store && CharacterId is { } character)
            {
                foreach (var (item, revision, count) in stacks)
                    if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [Refuse(EResult.DutyMission_NotEnoughMoney)];
                await store.RemoveQuestAsync(character, record.Code, ct);
            }
            questLog.Remove(record.Code);
            duty.Quests.Remove(record.Code);
            var added = await DutyTakeAsync(code, next, new DutyQuestExtra(extra.Roll + 1, groupName, DutyData.Roll(next.Rewards?.Random, rng)), ct);
            await SaveDutyAsync(ct);
            return [.. await InventoryChangesAsync(ct), GamePackets.QuestRollDutyReply(0, Wire(added)), GamePackets.QuestRemove([questUid], 4)];
        });
    }

    /// <summary>A duty mission's turn-in past the day's completion limit (5) is refused; 0 when it may be paid.</summary>
    int DutyRewardRefusal(QuestDefinition quest)
    {
        if (DutyData.Rule(quest.Type) is not { CompleteLimit: > 0 } rule) return 0;
        duty.StartCycle(DutyData.CycleOf(DutyNow));
        return duty.Rewarded >= rule.CompleteLimit ? EResult.DutyQuest_Fail_DailyPlayLimit : 0;
    }

    /// <summary>The offered random rewards a duty quest's turn-in pays: the items and their indexes (QuestTakeReward_RS list).</summary>
    (List<(int Item, long Count)> Items, List<int> Indexes) DutyRandomPaid(QuestRecord record)
    {
        if (!duty.Quests.TryGetValue(record.Code, out var extra) || extra.Rewards.Length == 0) return ([], []);
        var indexes = DutyTables.Paid(extra.Rewards, rng);
        return (indexes.Select(i => (extra.Rewards[i].Item, extra.Rewards[i].Count)).ToList(), indexes);
    }

    /// <summary>
    /// After a duty quest's reward: a mission counts for the day (its board entry rewarded, 4); the quest leaves the log so it can be
    /// drawn again (the official list holds no rewarded duty quest: rec 20261106-115817 #173810).
    /// </summary>
    async Task<List<byte[]>> DutyRewardedAsync(QuestRecord record, QuestDefinition quest, CancellationToken ct)
    {
        if (!DutyTables.IsDuty(quest.Type) || questLog is null) return [];
        var mission = quest.Type == "DutyMission";
        if (mission)
        {
            duty.Rewarded++;
            var index = duty.Board.FindIndex(e => e.QuestUid == (int)record.Uid);
            if (index >= 0) duty.Board[index] = duty.Board[index] with { Status = QuestLog.Rewarded };
        }
        duty.Quests.Remove(record.Code);
        if (Store is { } store && CharacterId is { } character) await store.RemoveQuestAsync(character, record.Code, ct);
        questLog.Remove(record.Code);
        await SaveDutyAsync(ct);
        return mission ? [DutyBoardNotice()] : [];
    }

    /// <summary>A duty quest given up: its data goes; a board mission is offered again (status 1).</summary>
    async Task<List<byte[]>> DutyGaveUpAsync(QuestRecord record, CancellationToken ct)
    {
        var index = duty.Board.FindIndex(e => e.QuestUid == (int)record.Uid && e.Status == 2);
        if (!duty.Quests.Remove(record.Code) && index < 0) return [];
        if (index >= 0) duty.Board[index] = duty.Board[index] with { Status = 1, QuestUid = 0 };
        await SaveDutyAsync(ct);
        return index >= 0 ? [DutyBoardNotice()] : [];
    }
}
