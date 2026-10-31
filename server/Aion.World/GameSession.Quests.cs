using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    // Core quest journal: load/save, auto-acquire, kill batching, NPC talk.
    // World objectives: GameSession.Quests.World.cs. Flow: GameSession.Quests.Flow.cs.

    // ---------- Quests ----------

    /// <summary>Kina is an item (Item.dat Gold).</summary>
    public const int KinaItemId = 930100001;
    QuestLog? questLog;
    readonly List<int> killedNpcs = [];
    int selectedTarget;
    long nextDevQuestUid = 1;

    public QuestLog? Quests => questLog;

    /// <summary>A development login (no store) gets an in-memory quest log: quest gates (map events, dungeon entries) can be exercised in checks.</summary>
    public QuestLog DevelopmentQuestLog() => questLog ??= new QuestLog(QuestBook.Embedded, profileRace == 0 ? 1 : profileRace);

    QuestWire Wire(QuestRecord q, int publisherId = 0)
    {
        if (questLog?.Book.Find(q.Code) is not { } quest)
            return new(q.QuestId, (byte)q.Status, q.Step, q.Goals, (int)q.Uid, publisherId);
        var step = quest.Steps.FirstOrDefault(s => s.Order == q.Step);
        var wireStep = step is null ? q.Step : QuestBook.WireStep(quest, step);
        var publisher = publisherId != 0 ? publisherId : quest.StartNpc ?? 0;
        return new(q.QuestId, (byte)q.Status, wireStep, q.Goals, (int)q.Uid, publisher);
    }

    List<byte[]> QuestAcceptPackets(QuestRecord saved, int acceptNpcKey) =>
    [
        GamePackets.QuestAcceptReply(saved.QuestId, (int)saved.Uid, 0),
        GamePackets.QuestAdd(Wire(saved, acceptNpcKey)),
        GamePackets.QuestProgress(Wire(saved, acceptNpcKey)),
    ];

    /// <summary>An i32 NPC key at the offset, or a varint when the body is not exactly that long (layout unconfirmed).</summary>
    /// <summary>FinishNpcInteraction_RS result when no conversation is open any more (OBSERVED `083e 00000000` after a layer move).</summary>
    const int FinishNoConversation = 0x3e08;

    static int? QuestKey(byte[] plain, int offset)
    {
        if (plain.Length == offset + 4) return BitConverter.ToInt32(plain, offset);
        try { return plain.Length > offset ? (int)new PacketReader(plain) { Offset = offset }.Varint() : null; }
        catch (ProtocolException) { return null; }
    }

    /// <summary>Resolves an NPC key from unrecorded client layouts (leading kind bytes before the key).</summary>
    int? NpcKeyFromBody(byte[] plain)
    {
        foreach (var offset in new[] { 2, 3, 4, 6 })
        {
            if (QuestKey(plain, offset) is { } key && targets?.ContainsKey(key) == true) return key;
        }
        for (var offset = 2; offset + 4 <= plain.Length && offset <= 18; offset++)
            if (BitConverter.ToInt32(plain, offset) is var key && targets?.ContainsKey(key) == true) return key;
        return null;
    }

    /// <summary>
    /// The character's quests. A character without any skips the opening quest (its objectives
    /// are the intro map events) and starts on the quest it leads to, as the official new
    /// character entered the world with HQ1101010 active.
    /// </summary>
    /// <summary>Re-evaluate ClearQuest / PCLevel steps after load or when a dependency quest reaches Complete (self-heals stuck journals).</summary>
    internal async Task<List<byte[]>> QuestRecheckAsync(CancellationToken ct)
    {
        if (questLog is null) return [];
        var changed = questLog.Recheck(level);
        return changed.Count == 0 ? [] : await QuestChangesAsync(changed, ct);
    }

    async Task LoadQuestsAsync(INativeCharacterStore store, Guid character, int race, CancellationToken ct)
    {
        questLog = new QuestLog(QuestBook.Embedded, race);
        questLog.Load(await store.QuestsAsync(character, ct));
        if (questLog.All.Count > 0) return;
        var opening = DevelopmentDefaults.OpeningQuestForRace(race);
        if (questLog.Book.Find(opening) is not { } intro) return;
        questLog.Set(await store.SaveQuestAsync(character, QuestLog.New(opening, intro) with { Status = QuestLog.Rewarded }, ct));
        if (intro.Next is { } next && questLog.Book.Find(next) is { } nextQuest && questLog.CanAccept(next, level))
            questLog.Set(await store.SaveQuestAsync(character, QuestLog.New(next, nextQuest), ct));
    }

    async Task<QuestRecord> SaveQuestAsync(QuestRecord record, CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character) record = await store.SaveQuestAsync(character, record, ct);
        else if (record.Uid == 0) record = record with { Uid = nextDevQuestUid++ };
        questLog?.Set(record);
        return record;
    }

    async Task<IReadOnlyList<QuestRecord>> SaveQuestBatchAsync(IReadOnlyList<QuestRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return [];
        if (records.Count == 1) return [await SaveQuestAsync(records[0], ct)];
        if (Store is { } wrapped && TurnStore.Unwrap(wrapped) is IQuestBatchStore batch && CharacterId is { } character)
        {
            var saved = await batch.SaveQuestBatchAsync(character, records, ct);
            foreach (var record in saved) questLog?.Set(record);
            return saved;
        }
        var list = new List<QuestRecord>(records.Count);
        foreach (var record in records) list.Add(await SaveQuestAsync(record, ct));
        return list;
    }

    /// <summary>Contents unlocked by finished quests (ContentsUnlock quest gates), as unlock type bytes.</summary>
    IEnumerable<byte> QuestUnlockTypes() =>
        (questLog?.UnlockedContents() ?? []).Select(name => BuilderCommands.UnlockTypes.ToList().IndexOf(name)).Where(i => i > 0).Select(i => (byte)i);

    /// <summary>Saves and reports changed quests (0x8d10); quests that complete by themselves pay at once.</summary>
    async Task<List<byte[]>> QuestChangesAsync(IEnumerable<QuestRecord> changed, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        var records = changed.ToList();
        var milestones = new List<QuestRecord>();
        foreach (var record in records)
            foreach (var milestone in questLog?.TakeMilestones(record.Code) ?? [])
            {
                milestones.Add(milestone);
                if (milestone.Uid != 0)
                {
                    Note($"quest transition {record.Code} milestone step={milestone.Step} status={milestone.Status} goals=[{string.Join(',', milestone.Goals)}]");
                    LogQuestEvent("quest_progress", record.Code, milestone.Step, milestone.Status, milestone.Goals);
                    replies.Add(GamePackets.QuestProgress(Wire(milestone)));
                }
            }
        var savedList = records.Count > 1 ? await SaveQuestBatchAsync(records, ct) : records.Count == 1 ? [await SaveQuestAsync(records[0], ct)] : [];
        for (var i = 0; i < savedList.Count; i++)
        {
            var saved = savedList[i];
            Note($"quest transition {saved.Code} saved step={saved.Step} status={saved.Status} goals=[{string.Join(',', saved.Goals)}]");
            LogQuestEvent(saved.Status == QuestLog.Complete ? "quest_complete" : "quest_progress", saved.Code, saved.Step, saved.Status, saved.Goals);
            replies.Add(GamePackets.QuestProgress(Wire(saved)));
            if (saved.Status == QuestLog.Complete && questLog!.Book.Find(saved.Code) is { Complete: "Auto" or "None" })
                replies.AddRange(await QuestRewardAsync((int)saved.Uid, -1, 0, ct));
        }
        replies.AddRange(await AutoAcquireAsync(ct));
        replies.AddRange(ApplyQuestWorldGating(lastNow));
        replies.AddRange(ApplyQuestStepTransitions(milestones, savedList, lastNow));
        if (questLog is not null && savedList.Any(s => s.Status == QuestLog.Complete))
        {
            var rechecked = questLog.Recheck(level);
            if (rechecked.Count > 0)
                replies.AddRange(await QuestChangesAsync(rechecked, ct));
        }
        return replies;
    }

    /// <summary>
    /// Quests that start by themselves (Quest.dat AcquireType Auto, 242 of them): taken when their earlier quests are done, their
    /// map (AcquireMapId, 0 = any) is this one and their conditions hold, in rounds (HQ1101021 opens AQ1602000). Then the map's
    /// events whose start now holds join (Dungeon_0005_MapEvent starts on HQ1101021 step 1). Before 2026-10-19 no Auto quest
    /// was ever taken: the chain ended at the Ascension Subspace. Callers run inside Stored.
    /// </summary>
    async Task<List<byte[]>> AutoAcquireAsync(CancellationToken ct)
    {
        if (questLog is null || resources is null) return [];
        var replies = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            // The earlier quests must be turned in, not only complete (the chain's next starts at the reward: HQ1101021 after Ashar's talk, not at the arrival).
            var codes = questLog.AutoAcquirable(level)
                .Where(code => questLog.Book.Find(code) is { } quest && (quest.Map == 0 || quest.Map == worldMap) && QuestConditionsHold(quest)
                               && (quest.Previous ?? []).Concat(quest.Prerequisites ?? []).All(p => !questLog.Book.Quests.ContainsKey(p) || questLog.Get(p) is { Status: QuestLog.Rewarded })).ToList();
            if (codes.Count == 0) break;
            foreach (var code in codes)
            {
                var saved = await SaveQuestAsync(QuestLog.New(code, questLog.Book.Find(code)!), ct);
                replies.Add(GamePackets.QuestAdd(Wire(saved)));
                replies.Add(GamePackets.QuestProgress(Wire(saved)));
            }
        }
        if (RunsMapEvents && !reloading && mapEventsMap == worldMap) replies.AddRange(StartEligibleEvents(lastNow));
        return replies;
    }

    /// <summary>Env objects closed this session (0xe2ca CloseEnvObjAchievement); gates Exploration acquire.</summary>
    readonly HashSet<string> closedEnvObjectNames = new(StringComparer.OrdinalIgnoreCase);

    internal void MarkClosedEnvObject(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name)) closedEnvObjectNames.Add(name);
    }

    /// <summary>Whether the character may take the quest now, including acquirable conditions.</summary>
    public bool CanAcceptQuest(string code) =>
        questLog?.CanAccept(code, level, QuestConditionsHold) == true;

    /// <summary>First failing gate for accept (race, level, prereqs, acquirable conditions).</summary>
    public string? QuestAcceptBlockReason(string code)
    {
        if (questLog?.Book.Find(code) is not { } quest) return "missing";
        return questLog.AcceptBlockReason(quest, level, QuestConditionsHold);
    }

    /// <summary>IncompleteQuest, ClearQuestStep, SubZone, UnifiedObjectiveScore, CloseEnvObj, gather/craft mastery gates.</summary>
    bool QuestConditionsHold(QuestDefinition quest) => (quest.Conditions ?? []).All(c => QuestConditionHolds(c));

    int MasteryForQuestCondition(string type) => type switch
    {
        "ExtractForceMastery" or "ExtractOdMastery" => GatherMastery.Level,
        "BlacksmithingMastery" => CraftMastery(1).Level,
        "TailoringMastery" => CraftMastery(2).Level,
        "JewelcraftingMastery" => CraftMastery(3).Level,
        "AlchemyMastery" => CraftMastery(4).Level,
        "CookingMastery" => CraftMastery(5).Level,
        _ => 0,
    };

    bool QuestConditionHolds(QuestCondition c) => c.Type switch
    {
        "IncompleteQuest" => questLog?.Get(c.Value) is { Status: QuestLog.Incomplete or QuestLog.Complete },
        "ClearQuestStep" => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stepId)
            && questLog?.Book.StepOf(stepId) is { } s && questLog.Book.ById.TryGetValue(s.Quest, out var code) && questLog.Get(code) is { } r && (r.Status >= QuestLog.Complete || r.Step > s.Step),
        "SubZone" => position is { } at && Subzones.InSubzone(worldMap, c.Value, at),
        "UnifiedObjectiveScore" => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var need)
            && (questLog?.UnifiedObjectiveScore() ?? 0) >= need,
        "CloseEnvObj" => closedEnvObjectNames.Contains(c.Value),
        "ExtractForceMastery" or "ExtractOdMastery" or "BlacksmithingMastery" or "TailoringMastery" or "JewelcraftingMastery" or "AlchemyMastery" or "CookingMastery"
            => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var masteryNeed)
               && MasteryForQuestCondition(c.Type) >= masteryNeed,
        _ => false,
    };

    /// <summary>After gather/craft mastery rises, take Auto-acquire quests whose mastery conditions now hold.</summary>
    async Task<List<byte[]>> QuestMasteryAutoAcquireAsync(CancellationToken ct) =>
        questLog is null || resources is null ? [] : await AutoAcquireAsync(ct);

    internal bool QuestConditionHoldsForTests(QuestCondition condition) => QuestConditionHolds(condition);

    internal void SetQuestLogForTests(QuestLog log) => questLog = log;

    internal void SetMasteryLevelsForTests(int gather, IReadOnlyDictionary<int, int>? craft = null)
    {
        (gatherLevel, gatherExp) = (gather, 0);
        craftMastery.Clear();
        if (craft is not null)
            foreach (var (category, lvl) in craft) craftMastery[category] = (lvl, 0);
    }

    async Task<List<byte[]>> QuestKillsAsync(CancellationToken ct)
    {
        if (killedNpcs.Count == 0 && clearedEvents.Count == 0 && brokenEnvObjCredit.Count == 0) return [];
        var replies = await BrokenEnvObjCreditAsync(ct);
        var npcIds = killedNpcs.ToList();
        killedNpcs.Clear();
        var events = clearedEvents.ToList();
        clearedEvents.Clear();
        if (questLog is not null)
        {
            var changed = new Dictionary<string, QuestRecord>();
            foreach (var npc in npcIds)
                foreach (var record in questLog.Killed(npc, level)) changed[record.Code] = record;
            foreach (var mapEvent in events)
                foreach (var record in questLog.ClearedMapEvent(mapEvent, level)) changed[record.Code] = record;
            if (changed.Count > 0)
                replies.AddRange(await Stored(() => QuestChangesAsync(changed.Values, ct)));
        }
        foreach (var member in NearbyPartyMembers())
            replies.AddRange(await member.Session.ApplyPartyQuestKillCreditAsync(npcIds, ct));
        return replies;
    }

    /// <summary>Kill credit for party members on the same map within PartyExpRange (matches exp share).</summary>
    internal async Task<List<byte[]>> ApplyPartyQuestKillCreditAsync(IReadOnlyList<int> npcIds, CancellationToken ct)
    {
        if (questLog is null || npcIds.Count == 0) return [];
        var changed = new Dictionary<string, QuestRecord>();
        foreach (var npc in npcIds)
            foreach (var record in questLog.Killed(npc, level)) changed[record.Code] = record;
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed.Values, ct));
    }

    /// <summary>
    /// Map events pay ClearMapEvent on <see cref="IMapEventHost.DungeonClear"/> during the same request that finished the script
    /// (step talk, cutscene report, movement into a trigger), not only on the next tick - live sends 0x8d10 in that batch.
    /// </summary>
    async Task<List<byte[]>> WithMapEventClearsFlushedAsync(IEnumerable<byte[]> replies, CancellationToken ct)
    {
        var list = replies.ToList();
        if (clearedEvents.Count > 0 || killedNpcs.Count > 0 || brokenEnvObjCredit.Count > 0)
            list.AddRange(await QuestKillsAsync(ct));
        return list;
    }

    /// <summary>The quest talks open with the NPC whose dialog is open (0xe240 .. 0xe242).</summary>
    (int NpcKey, List<(string Code, int Step)> Talks)? openNpcTalk;
    /// <summary>ClearMapEvent field entry (EnterInstanceLayer): the layer move follows 0xe242 after the entry talk.</summary>
    int? openClearMapEventEntryKey;

    bool IsOpenClearMapEventEntryNpc(Target npc) =>
        questLog is not null && MapEventData.EntryNpc(npc.NpcId) is not null
        && questLog.OpenClearMapEventEntryNpcIds().Contains(npc.NpcId);

    /// <summary>
    /// Clicking an NPC (0xe240): the dialog opens (0xe241, five minutes). The client plays the step's NpcTalk lines itself and
    /// sends FinishNpcInteraction (0xe242) when they end (F on the last line, skip or Esc); AskNpc / CloseNpc count then
    /// (<see cref="NpcInteractionFinishedAsync"/>). Counting on the open moved the step under the playing dialog: when the
    /// talked NPC had no part in the next step (a quest's last talk, a talk before a layer move), the client closed the
    /// interaction at once (0xe242 within 15 ms, live 2026-10-30 HQ2101070 Garlond) and F on the last line did nothing.
    /// </summary>
    Task<List<byte[]>> NpcInteractionAsync(int npcKey, long now, CancellationToken ct)
    {
        var replies = new List<byte[]> { GamePackets.StartNpcInteractionReply(npcKey, now + 300_000) };
        if (targets?.GetValueOrDefault(npcKey) is not { } npc)
        {
            LogNpcTalkRefused(npcKey, 0, null, "unknown_npc_key");
            return Task.FromResult(replies);
        }
        replies.AddRange(MapEventTalked(npcKey, null, close: false, now));
        openNpcTalk = questLog?.OpenTalks(npc.NpcId) is { Count: > 0 } talks ? (npcKey, talks) : null;
        openClearMapEventEntryKey = openNpcTalk is null && IsOpenClearMapEventEntryNpc(npc) ? npcKey : null;
        return Task.FromResult(replies);
    }

    /// <summary>The dialog ended (0xe242): quest AskNpc/CloseNpc and ClearMapEvent entry talks count here.</summary>
    async Task<List<byte[]>> NpcInteractionFinishedAsync(int npcKey, long now, CancellationToken ct)
    {
        if (targets?.GetValueOrDefault(npcKey) is not { } npc) return [];
        var replies = new List<byte[]>();
        if (openNpcTalk is { } open && open.NpcKey == npcKey && questLog is not null)
        {
            openNpcTalk = null;
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Talked(npc.NpcId, level, open.Talks), ct)));
        }
        if (openClearMapEventEntryKey == npcKey)
        {
            openClearMapEventEntryKey = null;
            replies.AddRange(EnterInstanceLayerAfterEntryTalk(npcKey, now));
        }
        return replies;
    }
}
