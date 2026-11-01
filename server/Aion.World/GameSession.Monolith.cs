using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    const int MonolithTokenLight = 631900001;
    const int MonolithTokenDark = 631900007;
    const int MonolithScrollReward = 531110002;
    /// <summary>Quest move-point markers and the Monolith they pair with (cm, horizontal).</summary>
    const double MonolithMarkerRange = 5000;

    /// <summary>Per-monolith resonate progress this session (monolith_id → count). CANDIDATE: not persisted yet.</summary>
    readonly Dictionary<int, int> monolithResonateCounts = [];
    /// <summary>Monolith ids the client has been sent MonolithUnlock_NT for.</summary>
    readonly HashSet<int> monolithUnlocked = [];
    /// <summary>Monolith env-object indexes spawned before LoadPost tab 2; flushed with 0x3baa/0x3bdf after.</summary>
    readonly HashSet<int> pendingMonolithNoticeIndexes = [];
    /// <summary>After MonolithList_NT at entry the client accepts Monolith.dat ids in Info/Unlock (LoadPost tab 2 still re-flushes).</summary>
    bool monolithNoticesEnabled;
    /// <summary>Monolith ids auto-resonated this session via proximity fallback (client never sent 0x3bab).</summary>
    readonly HashSet<int> monolithProximityAutoResonated = [];
    /// <summary>When the player entered interact range of a monolith (ms); proximity waits so 0x3bab can win.</summary>
    readonly Dictionary<int, long> monolithProximityInteractDelayStart = [];
    /// <summary>Give MonolithResonate_RQ time to arrive before proximity consumes the token at the stone.</summary>
    const int MonolithProximityInteractDelayMs = 1500;

    static int MonolithTokenForRace(string? race) =>
        string.Equals(race, "Dark", StringComparison.OrdinalIgnoreCase) ? MonolithTokenDark : MonolithTokenLight;

    static int MonolithId(WorldObject monolith) => MonolithTables.Embedded.MonolithIdForEnv(monolith.Env);

    int MonolithResonateCount(int monolithId) => monolithResonateCounts.GetValueOrDefault(monolithId);

    void SetMonolithResonateCount(int monolithId, int count) => monolithResonateCounts[monolithId] = count;

    void ResetMonolithClientState()
    {
        monolithNoticesEnabled = false;
        pendingMonolithNoticeIndexes.Clear();
        monolithProximityAutoResonated.Clear();
        monolithProximityInteractDelayStart.Clear();
    }

    static bool PlayerWithinMonolithInteractRange(Vector3d at, WorldObject monolith)
    {
        var dx = monolith.At.X - at.X;
        var dy = monolith.At.Y - at.Y;
        var range = monolith.InteractRange + WorldObjects.InteractSlack;
        return dx * dx + dy * dy <= range * range;
    }

    /// <summary>Call after MonolithList_NT on world entry so the first 0x3634 can carry 0x3baa/0x3bdf (OBSERVED: before list, wrong env ids).</summary>
    void EnableMonolithNoticesAfterList() => monolithNoticesEnabled = true;

    static bool PlayerWithinMonolithMarkerRange(Vector3d at, WorldObject monolith)
    {
        var dx = monolith.At.X - at.X;
        var dy = monolith.At.Y - at.Y;
        return dx * dx + dy * dy <= MonolithMarkerRange * MonolithMarkerRange;
    }

    List<(int MonolithId, int ResonateCount)> MonolithListEntries()
    {
        var race = profileRace;
        if (race == 0) race = 1;
        var ids = new HashSet<int>(MonolithTables.Embedded.IdsForRace(race));
        ids.UnionWith(monolithResonateCounts.Keys);
        ids.UnionWith(monolithUnlocked);
        return ids.OrderBy(id => id).Select(id => (id, MonolithResonateCount(id))).ToList();
    }

    byte[] MonolithListAtEntry() => GamePackets.MonolithList(MonolithListEntries());

    IEnumerable<byte[]> MonolithEnvObjNotices(int index, WorldObject o, bool resendUnlock = false)
    {
        if (!o.Monolith) yield break;
        if (!monolithNoticesEnabled)
        {
            pendingMonolithNoticeIndexes.Add(index);
            yield break;
        }
        foreach (var packet in MonolithEnvObjWireNotices(index, o, resendUnlock)) yield return packet;
    }

    IEnumerable<byte[]> MonolithEnvObjWireNotices(int index, WorldObject o, bool resendUnlock)
    {
        var monolithId = MonolithId(o);
        var key = WorldObjects.FirstKey + index;
        var count = MonolithResonateCount(monolithId);
        yield return GamePackets.MonolithInfo(monolithId, count, key);
        if (resendUnlock || monolithUnlocked.Add(monolithId))
            yield return GamePackets.MonolithUnlock(monolithId, count);
    }

    List<byte[]> FlushMonolithNoticesAfterLoadPost()
    {
        monolithNoticesEnabled = true;
        var replies = new List<byte[]>();
        var objects = WorldObjects.ObjectsOn(worldMap);
        var indexes = new HashSet<int>(pendingMonolithNoticeIndexes);
        pendingMonolithNoticeIndexes.Clear();
        foreach (var index in shownObjects) indexes.Add(index);
        foreach (var index in indexes.OrderBy(i => i))
        {
            if (index < 0 || index >= objects.Count || !objects[index].Monolith) continue;
            replies.AddRange(MonolithEnvObjWireNotices(index, objects[index], resendUnlock: true));
        }
        return replies;
    }

    bool MonolithNearMovePoint(string movePoint, int map, WorldObject monolith)
    {
        if (QuestTeleportResolver.ForNamedPoint(movePoint, map, data, "monolith-marker") is { } dest)
        {
            var dx = dest.At.X - monolith.At.X;
            var dy = dest.At.Y - monolith.At.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= MonolithMarkerRange;
        }
        // QuestMove_* names are often absent from map-points; using the placed Monolith env obj is enough.
        return true;
    }

    (QuestRecord Record, QuestObjective Objective)? FindMonolithCollectObjective(WorldObject monolith)
    {
        if (questLog is null) return null;
        foreach (var record in questLog.All.Where(q => q.Status == QuestLog.Incomplete))
        {
            if (questLog.CurrentStep(record) is not { } step) continue;
            var book = questLog.Book.Find(record.Code);
            if (book is null) continue;
            for (var i = 0; i < Math.Min(6, step.Objectives.Length); i++)
            {
                var o = step.Objectives[i];
                if (o.Type != "CollectItem" || o.MovePoint is null || o.Item is null || record.Goals[i] >= o.Count) continue;
                var map = o.Map ?? book.Map;
                if (map != worldMap || !MonolithNearMovePoint(o.MovePoint, map, monolith)) continue;
                return (record, o);
            }
        }
        return null;
    }

    bool TryResolveMonolith(int envObjKey, out int index, out WorldObject monolith)
    {
        index = envObjKey - WorldObjects.FirstKey;
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (index < 0 || index >= objects.Count || !objects[index].Monolith)
        {
            monolith = null!;
            return false;
        }
        monolith = objects[index];
        var at = position ?? worldPlayer?.Position ?? default;
        var dx = monolith.At.X - at.X;
        var dy = monolith.At.Y - at.Y;
        var range = monolith.InteractRange + WorldObjects.InteractSlack;
        return dx * dx + dy * dy <= range * range;
    }

    sealed record MonolithResonateResult(bool Ok, int MonolithId, int TokenId, int TokenCount, int RewardId, int PrevCount, int NewCount, List<byte[]> SideEffects);

    /// <summary>
    /// Empyrean Monolith resonate (HQ1102125 step 4, HQ2102105 step 5): consume race Monolith tokens and grant the
    /// CollectItem reward. Used by dedicated MonolithResonate_RQ and the InteractEnvObj fallback.
    /// </summary>
    async Task<MonolithResonateResult> MonolithResonateCoreAsync(WorldObject monolith, int tokenCount, CancellationToken ct)
    {
        var monolithId = MonolithId(monolith);
        var prev = MonolithResonateCount(monolithId);
        if (!monolith.Monolith || tokenCount < 1 || questLog is null || Store is not { } store || CharacterId is not { } character)
            return new(false, monolithId, 0, 0, 0, prev, prev, []);
        if (FindMonolithCollectObjective(monolith) is not { } match)
            return new(false, monolithId, 0, 0, 0, prev, prev, []);
        var book = questLog.Book.Find(match.Record.Code)!;
        var tokenId = MonolithTokenForRace(book.Race);
        var token = storedItems.Values.FirstOrDefault(i => i.Location == "bag" && i.Quantity >= tokenCount && i.TemplateId == tokenId);
        if (token is null)
            return new(false, monolithId, tokenId, tokenCount, 0, prev, prev, []);
        var rewardId = match.Objective.Item!.Value;
        var granted = await Stored(async () =>
        {
            if (!await store.ConsumeItemAsync(character, token.InstanceId, token.Revision, tokenCount, ct)) return [];
            return await store.GrantLootAsync(character, [(rewardId, 1)], ct) ? [[]] : [];
        });
        if (granted.Count == 0)
            return new(false, monolithId, tokenId, tokenCount, rewardId, prev, prev, []);
        var next = prev + 1;
        SetMonolithResonateCount(monolithId, next);
        var replies = await InventoryChangesAsync(ct, IncreaseEnvObjReward, 0);
        var held = inventory.Values.Where(i => i.ItemId == rewardId).Sum(i => i.Count);
        replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Holding(rewardId, held, level), ct)));
        return new(true, monolithId, tokenId, tokenCount, rewardId, prev, next, replies);
    }

    async Task<List<byte[]>> MonolithResonateAsync(WorldObject monolith, CancellationToken ct)
    {
        var result = await MonolithResonateCoreAsync(monolith, 1, ct);
        return result.Ok ? result.SideEffects : [];
    }

    async Task<List<byte[]>> MonolithResonateRequestAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 14 || resources is null)
            return [GamePackets.MonolithResonateReply(1, 0, 0, 0, 0, 0, [])];
        var monolithId = BitConverter.ToInt32(plain, 2);
        var itemCount = BitConverter.ToInt32(plain, 6);
        var envObjKey = BitConverter.ToInt32(plain, 10);
        if (!TryResolveMonolith(envObjKey, out _, out var monolith) || MonolithId(monolith) != monolithId)
            return [GamePackets.MonolithResonateReply(1, monolithId, 0, itemCount, MonolithResonateCount(monolithId), MonolithResonateCount(monolithId), [])];
        var result = await MonolithResonateCoreAsync(monolith, itemCount, ct);
        if (result.Ok) monolithProximityAutoResonated.Add(result.MonolithId);
        var rewards = result.Ok ? [(result.RewardId, 1)] : Array.Empty<(int, int)>();
        return
        [
            GamePackets.MonolithResonateReply(result.Ok ? 0 : 1, result.MonolithId, result.TokenId, result.TokenCount, result.PrevCount, result.NewCount, rewards),
            .. result.SideEffects,
        ];
    }

    Task<List<byte[]>> SyncMonolithAccountAchievementsAsync(byte[] plain) =>
        Task.FromResult<List<byte[]>>([GamePackets.SyncMonolithAccountAchievementsReply()]);

    /// <summary>
    /// When the client never offers MonolithResonate (LoadPost/Info timing or Usage=Monolith UI), complete HQ1102125 step 4 /
    /// HQ2102105 step 5 if the player is at the stone with the race token.
    /// </summary>
    async Task<List<byte[]>> TryMonolithProximityResonateAsync(Vector3d at, long now, CancellationToken ct)
    {
        if (questLog is null || Store is null || CharacterId is null || shownObjects.Count == 0) return [];
        var objects = WorldObjects.ObjectsOn(worldMap);
        var replies = new List<byte[]>();
        foreach (var index in shownObjects)
        {
            if (index >= objects.Count) continue;
            var monolith = objects[index];
            if (!monolith.Monolith || FindMonolithCollectObjective(monolith) is not { Record: var record, Objective: var objective }) continue;
            if (!PlayerWithinMonolithMarkerRange(at, monolith)) continue;
            var monolithId = MonolithId(monolith);
            if (monolithProximityAutoResonated.Contains(monolithId)) continue;
            if (PlayerWithinMonolithInteractRange(at, monolith))
            {
                if (!monolithProximityInteractDelayStart.TryGetValue(monolithId, out var since))
                {
                    monolithProximityInteractDelayStart[monolithId] = now;
                    continue;
                }
                if (now < since + MonolithProximityInteractDelayMs) continue;
            }
            else monolithProximityInteractDelayStart.Remove(monolithId);
            var book = questLog.Book.Find(record.Code)!;
            var tokenId = MonolithTokenForRace(book.Race);
            if (!storedItems.Values.Any(i => i.Location == "bag" && i.Quantity > 0 && i.TemplateId == tokenId)) continue;
            var result = await MonolithResonateCoreAsync(monolith, 1, ct);
            if (!result.Ok) continue;
            monolithProximityAutoResonated.Add(monolithId);
            IReadOnlyList<(int ItemId, int Count)> rewards = [(result.RewardId, 1)];
            replies.Add(GamePackets.MonolithResonateReply(0, result.MonolithId, result.TokenId, result.TokenCount, result.PrevCount, result.NewCount, rewards));
            replies.AddRange(result.SideEffects);
            Note($"MonolithProximityResonate quest={record.Code} step={record.Step} monolith={monolithId} reward={objective.Item}");
        }
        return replies;
    }
}
