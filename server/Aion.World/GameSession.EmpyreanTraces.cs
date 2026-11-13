using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Empyrean Traces (user 2026-11-11: "the collection system and the usage of the system; they don't spawn on the map").
//
// WHAT THEY ARE (client tables, tools/monolith_data.py -> tools/data/monoliths.json): EnvObjData Usage Normal objects with
// bGetRewardOnce and a RewardData row reward_manual_Monolith_<id>: Verteron E_L1_Verteron_fragment_Ground/Air/Water (559 on World_L_A
// 1010), Eltnen E_L2_Godfragment_* (140 on 1011), Altgard E_D1_GodFragment_01_* (559 on 1110), Morheim E_D2_GodFragment_* (140 on 1111),
// Lower/Middle Reshanta E_AR1/AR2_Fragment_Ground/Air (200 on 20, 100 on 22). tools/world_objects.py kept only Quest-usage objects,
// so none spawned; since 2026-11-11 they and the 12 Monoliths are in world-objects.json (appended per map: older indexes unchanged).
// Every placed trace has its own UseEnvObjSpawnerName achievement (Achievement.dat Group_Unlock_MonolithFragment_<region>_NN,
// ObjValues = the spawn name, one step of count 1): that achievement is the "collected" mark, for good and per character.
//
// COLLECTING, OBSERVED (13 recordings, 15 pickups by the recorded Asmodian, rec 20261106-105448 #17034-#17575, 115817 #131060-#131248,
// 145442 #21556-#21914): the reply 0x3637 {0, key} and 0x3638 started at once; InteractTime 2500 ms later 0x363a completed, 0x8d6d
// finish, 0x363b available (the trace stays for the others: other players' 0x3638/0x363a on the same keys); then 0x5684 the region
// count (UseEnvObj achievement 420100700 "Collect Empyrean Trace: Altgard" / 320100700 Verteron / x20100900 Reshanta, and the battle
// pass missions "Retrieve Empyrean Trace" 9x0110900 ...), 0xe33a the reward at the trace (Altgard/Verteron 3 tokens, Eltnen/Morheim
// and Reshanta 1: RewardItem reward_manual_Monolith_<env>; OBSERVED 631900007 x3), ~50 ms later 0x561b reason 49, then 0x5684 the
// spawn's own achievement (count 1, complete). No respawn: a trace is collected once per character (bGetRewardOnce; the 0x3634 of a
// collected trace is unchanged, OBSERVED: the client greys it out from the achievement). Flying (Air, VisibleRange MidRange) and
// water variants differ only in where they are placed. A second use (not seen: the client does not offer it) is refused as other
// once-only objects (EnvObj_Fail_CanInteractOnlyOnce, CANDIDATE).
//
// THE USAGE: MONOLITHS (Monolith.dat). The items ("Empyrean Trace: Altgard" = Monolith_token_D_s_01a_b ...) are resonated with
// the region's Monolith (EnvObjData Usage Monolith: E_D1_GodMonolith_01, E_L1_Verteron_MonoLith, E_AR1_L_GodMonolith_13 ...). A
// Monolith's resonance count is the tokens put in so far; MonolithLevel.dat NeedTokenCount is the count that reaches each level
// (Altgard/Verteron 30 levels up to 560, Eltnen/Morheim 8 up to 45, Reshanta 20 up to 300), and each level's RewardMonolith row
// gives permanent stats (Life, Destiny, Time, Space, Justice, Destruction, Freedom, Death, Illusion, Wisdom, SPMax, PvP stats) and
// items (Amulet Enhance Scrolls, Amplify fragments, Daevanion points, chest keys, Monolith titles). OBSERVED: MonolithList_NT 0xe30b
// at login (count 560 = Altgard finished). CANDIDATE (no recording): using a Monolith (5 s) answers MonolithInfo_NT 0xe30c, the
// window's MonolithResonate_RQ 0xe30d {monolith, item count, object key} -> RS 0xe30e, the rewards of every level passed at once,
// one token = one count; MonolithUnlock_NT / the unlock quests (HQ1102125 / HQ2102105) are not gated; SyncMonolithAccountAchievements
// (another server's traces, 0xe369/0xe36a) answers "nothing to sync". Express vouchers (UseEffect MonolithAllClear [monolith, map])
// collect every uncollected trace of the map at once (CANDIDATE: no recording; same achievements and tokens as one by one).
// STORE: migration 090 game.character_monoliths.

/// <summary>The Monoliths, their levels and the trace / Monolith objects (embedded monoliths.json, tools/monolith_data.py).</summary>
public sealed class MonolithTables
{
    public sealed record Level(int Number, int Need, IReadOnlyList<(int Item, int Count, int Enchant)> Items, IReadOnlyDictionary<string, int> Stats);
    public sealed record Monolith(int Id, string Name, int Race, int Token, string? UnlockQuest, int UnlockStep, IReadOnlyList<string> Groups, IReadOnlyList<Level> Levels)
    {
        /// <summary>The count that reaches the last level.</summary>
        public int MaxCount => Levels.Count == 0 ? 0 : Levels.Max(l => l.Need);
        /// <summary>The level a resonance count reaches.</summary>
        public int LevelAt(int count) => Levels.Where(l => l.Need <= count).Select(l => l.Number).DefaultIfEmpty(1).Max();
    }

    public IReadOnlyDictionary<int, Monolith> Monoliths { get; }
    /// <summary>Monolith object (EnvObjData id) -> the Monoliths it resonates with.</summary>
    public IReadOnlyDictionary<int, int[]> Objects { get; }
    /// <summary>Trace EnvObjData ids -> template names.</summary>
    public IReadOnlyDictionary<int, string> Traces { get; }
    /// <summary>Express voucher item -> (monolith, map).</summary>
    public IReadOnlyDictionary<int, (int Monolith, int Map)> Vouchers { get; }

    MonolithTables(JsonElement root)
    {
        var monoliths = new Dictionary<int, Monolith>();
        foreach (var m in root.GetProperty("monoliths").EnumerateObject())
        {
            var v = m.Value;
            var levels = v.GetProperty("levels").EnumerateArray().Select(l => new Level(l[0].GetInt32(), l[1].GetInt32(),
                l[2].EnumerateArray().Select(i => (i[0].GetInt32(), i[1].GetInt32(), i[2].GetInt32())).ToList(),
                l[3].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase))).OrderBy(l => l.Number).ToList();
            var id = int.Parse(m.Name, System.Globalization.CultureInfo.InvariantCulture);
            monoliths[id] = new Monolith(id, v.GetProperty("name").GetString() ?? "", v.GetProperty("race").GetInt32(), v.GetProperty("token").GetInt32(),
                v.GetProperty("unlock_quest").GetString(), v.GetProperty("unlock_step").GetInt32(),
                v.GetProperty("groups").EnumerateArray().Select(g => g.GetString() ?? "").ToList(), levels);
        }
        Monoliths = monoliths;
        Objects = root.GetProperty("objects").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
            p => p.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Traces = root.GetProperty("traces").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetString() ?? "");
        Vouchers = root.GetProperty("vouchers").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
            p => (p.Value[0].GetInt32(), p.Value[1].GetInt32()));
    }

    static readonly Lazy<MonolithTables> embedded = new(() =>
    {
        using var stream = typeof(MonolithTables).Assembly.GetManifestResourceStream("monoliths.json") ?? throw new InvalidOperationException("monoliths.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new MonolithTables(doc.RootElement);
    });
    public static MonolithTables Embedded => embedded.Value;

    public Monolith? Of(int id) => Monoliths.GetValueOrDefault(id);
    public IEnumerable<Monolith> OfRace(int race) => Monoliths.Values.Where(m => m.Race == race).OrderBy(m => m.Id);
}

public sealed partial class GameSession
{
    static MonolithTables MonolithData => MonolithTables.Embedded;

    /// <summary>Resonance count per Monolith (migration 090).</summary>
    readonly Dictionary<int, int> monolithCounts = [];
    Dictionary<string, int>? monolithStats;

    public int MonolithCount(int id) => monolithCounts.GetValueOrDefault(id);

    void LoadMonoliths(IReadOnlyDictionary<int, int> stored)
    {
        monolithCounts.Clear();
        foreach (var (id, count) in stored)
            if (MonolithData.Of(id) is not null && count > 0) monolithCounts[id] = count;
        monolithStats = null;
    }

    /// <summary>An Empyrean Trace: a placed object with its spawn's achievement (world-objects.json "achievement").</summary>
    static bool IsTrace(WorldObject o) => o.Achievement != 0 && MonolithData.Traces.ContainsKey(o.Env);

    /// <summary>A trace this character has collected for good: its spawn achievement counted.</summary>
    bool TraceCollected(WorldObject o) => o.Achievement != 0 && (achievements.GetValueOrDefault(o.Achievement)?.Count ?? 0) > 0;

    /// <summary>
    /// The 0x3634 of a placed object: one with a spawn achievement (<see cref="SpawnAchievement"/>) in the trace form (mask 0x0c, the
    /// achievement after the phase), the rest in the placed form (mask 4).
    /// </summary>
    static byte[] PlacedSpawn(int key, WorldObject o, long now) => SpawnAchievement(o) is not 0 and var achievement
        ? MonolithPackets.TraceSpawn(key, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship, achievement)
        : GamePackets.EnvObjSpawn(key, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship);

    /// <summary>Checks only: a placed object's 0x3634.</summary>
    internal static byte[] PlacedSpawnForChecks(int key, WorldObject o, long now) => PlacedSpawn(key, o, now);

    /// <summary>
    /// The achievement a placed object's 0x3634 carries (_achievement_id_from_spawn_unit): a trace's (world-objects.json), else for a
    /// bGetRewardOnce object its spawn's UseEnvObjSpawnerName achievement (the first by id, as world_objects.py picks a trace's), else 0.
    /// OBSERVED: in all 20 recordings every bGetRewardOnce object's spawn has mask 0x0c with that achievement (87: 86 traces and the
    /// Quest object E_D1_HQ_GodFragment_02_001, rec 20261106-115118 #31926, achievement 100050001, which world-objects.json does not
    /// mark) and no other object's spawn (1 859) has it. The client draws a bGetRewardOnce object's map/minimap marker (UIIcon GetRewardOnce:
    /// Minimap_Monster, the white dot) and greys it out from that achievement; without it the stronghold Supply Chests
    /// (E_L1_H1_TargetBox, achievement 110010050, count 5), Stronghold Chests (Envobj_L1_H1_BalaurBox_1..3), the stronghold reward
    /// chest and the dungeon reward boxes (E_CommonRewardBox_*) had no marker (live 2026-11-11, map 153001). Those spawns are in no
    /// recording: their form is CANDIDATE by the same rule.
    /// </summary>
    internal static int SpawnAchievement(WorldObject o) =>
        o.Achievement != 0 ? o.Achievement
        : EnvUsages.Of(o.Env) is { RewardOnce: true } ? SpawnerAchievements.Value.GetValueOrDefault(o.Name) : 0;

    /// <summary>Spawn name -> its first UseEnvObjSpawnerName achievement (case-insensitive, as the client compares spawn names).</summary>
    static readonly Lazy<Dictionary<string, int>> SpawnerAchievements = new(() =>
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Achievements.OfType("UseEnvObjSpawnerName").OrderBy(e => e.Id))
            if (entry.Values is [{ Length: > 0 } name, ..]) byName.TryAdd(name, entry.Id);
        return byName;
    });

    /// <summary>
    /// A trace collected (its 2.5 s use done), in the OBSERVED order: 0x363a, 0x8d6d, 0x363b, the region / battle pass counts
    /// (UseEnvObj), 0xe33a with the tokens, the items (0x561b reason 49), the spawn's own achievement (UseEnvObjSpawnerName).
    /// </summary>
    async Task<List<byte[]>> CollectTraceAsync(int index, WorldObject o, int key, long now, CancellationToken ct)
    {
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key),
        };
        TakeEnvBox(index, o, now);
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        var items = LootTables.Embedded.Roll(o.Env, profileRace, rng);
        if (items.Count > 0)
        {
            replies.Add(EnvBoxPackets.EnvObjReward(key, o.At, items));
            if (Store is { } store && CharacterId is { } character)
            {
                var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
                if (granted.Count > 0) replies.AddRange(await InventoryChangesAsync(ct, EnvBoxPackets.IncreaseEnvObjReward, 0));
            }
        }
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
        Note($"Empyrean Trace collected on map {worldMap}: {o.Name} (achievement {o.Achievement}) gave {string.Join(", ", items.Select(i => $"{i.ItemId} x{i.Count}"))}");
        return replies;
    }

    /// <summary>The Monolith an object resonates with for this character: its race's, else the first.</summary>
    static MonolithTables.Monolith? MonolithOfObject(WorldObject o, int race) =>
        (o.Monolith ?? MonolithData.Objects.GetValueOrDefault(o.Env) ?? []).Select(MonolithData.Of).OfType<MonolithTables.Monolith>()
            .OrderBy(m => m.Race == race ? 0 : 1).FirstOrDefault();

    /// <summary>A Monolith used (5 s): the usual completion, then MonolithInfo_NT so the resonance window opens (CANDIDATE). It stays.</summary>
    List<byte[]> MonolithUsed(WorldObject o, int key)
    {
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key),
            GamePackets.InteractEnvObjReply(key),
        };
        if (MonolithOfObject(o, profileRace) is { } monolith) replies.Add(MonolithPackets.MonolithInfo(monolith.Id, MonolithCount(monolith.Id), key));
        return replies;
    }

    /// <summary>MonolithList_NT at the select: every Monolith of the race with its count (OBSERVED).</summary>
    byte[] MonolithList() => MonolithPackets.MonolithList(MonolithData.OfRace(profileRace).Select(m => (m.Id, MonolithCount(m.Id))).ToList());

    /// <summary>
    /// 0xe30d MonolithResonate_RQ {monolith i32, item count i32, object key i32}: puts that many of the Monolith's tokens in (at most
    /// what the last level needs), stores the count, grants the items of every level passed and refreshes the stats (CANDIDATE wire).
    /// </summary>
    async Task<List<byte[]>> MonolithResonateAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 10) return [];
        var id = BitConverter.ToInt32(plain, 2);
        var asked = BitConverter.ToInt32(plain, 6);
        var monolith = MonolithData.Of(id);
        var previous = MonolithCount(id);
        byte[] Fail(int result) => MonolithPackets.ResonateReply(result, id, monolith?.Token ?? 0, 0, previous, previous, []);
        if (monolith is null || monolith.Race != profileRace) return [Fail(EResult.Monolith_Fail_NotUnlock)];
        if (asked <= 0) return [Fail(EResult.MonolithResonate_Fail_InvalidItemCount)];
        if (previous >= monolith.MaxCount) return [Fail(EResult.MonolithResonate_Fail_MaxLevel)];
        var used = Math.Min(asked, monolith.MaxCount - previous);
        if (CostStacks(monolith.Token, used) is not { } costs) return [Fail(EResult.MonolithResonate_Fail_NotEnoughCountItem)];
        var count = previous + used;
        var passed = monolith.Levels.Where(l => l.Need > previous && l.Need <= count).ToList();
        var rewards = passed.SelectMany(l => l.Items).GroupBy(i => (i.Item, i.Enchant)).Select(g => (g.Key.Item, g.Key.Enchant, (long)g.Sum(i => i.Count))).ToList();
        if (!BagRoomFor(rewards.Select(r => (r.Item, r.Item3)), SlotsFreedBy(costs))) return [Fail(EResult.Item_Common_Fail_NotEnoughSlotCountContainer)];   // GameSession.BagRoom.cs
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.ResonateMonolithAsync(character, costs, id, previous, count,
                rewards.Select(r => (r.Item, r.Item3)).ToList(), ct) ? [[]] : []);
            if (saved.Count == 0) return [Fail(EResult.MonolithResonate_Fail_NotEnoughCountItem)];
        }
        monolithCounts[id] = count;
        monolithStats = null;
        var replies = Store is null ? new List<byte[]>() : await InventoryChangesAsync(ct, IncreaseMonolithReward, DecreaseItemUse);
        replies.Add(MonolithPackets.ResonateReply(0, id, monolith.Token, used, previous, count, rewards));
        if (passed.Any(l => l.Stats.Count > 0)) replies.AddRange(RefreshStats());
        Note($"Monolith {monolith.Name}: resonated {used} ({previous} -> {count}, level {monolith.LevelAt(previous)} -> {monolith.LevelAt(count)})");
        return replies;
    }

    /// <summary>EIncreaseReason of the resonance rewards (CANDIDATE: the objects' reward reason 49); the tokens go with the item-use decrease.</summary>
    const byte IncreaseMonolithReward = 49;

    /// <summary>0xe369 SyncMonolithAccountAchievements_RQ {object key}: no other server's traces to merge (CANDIDATE: "already up to date").</summary>
    static List<byte[]> SyncMonolithAccountAchievements() =>
        [MonolithPackets.SyncReply(EResult.SyncMonolithAccountAchievements_Fail_NotFoundMatchingAchievement, [])];

    /// <summary>The stats of the Monolith levels reached (RewardMonolith Stat rewards), summed.</summary>
    IReadOnlyDictionary<string, int> MonolithStats()
    {
        if (monolithStats is { } cached) return cached;
        var sum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, count) in monolithCounts)
            foreach (var level in MonolithData.Of(id)?.Levels.Where(l => l.Need <= count) ?? [])
                foreach (var (stat, value) in level.Stats) sum[stat] = sum.GetValueOrDefault(stat) + value;
        return monolithStats = sum;
    }

    int MonolithStat(string stat) => monolithCounts.Count == 0 ? 0 : MonolithStats().GetValueOrDefault(stat);
    IEnumerable<string> MonolithStatNames() => monolithCounts.Count == 0 ? [] : MonolithStats().Keys;

    /// <summary>
    /// An express voucher (UseEffect MonolithAllClear [monolith, map]): every trace of that Monolith's groups on the map this character
    /// has not collected, at once: their spawn achievements, the region counts and their tokens (CANDIDATE: no recording). Refused
    /// when nothing is left ("Has no effect if all Empyrean Traces in the region have already been obtained").
    /// </summary>
    async Task<List<byte[]>?> UseMonolithVoucherAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, long now, CancellationToken ct)
    {
        var (monolithId, map) = MonolithData.Vouchers.TryGetValue(row.TemplateId, out var v) ? v
            : (MonolithData.Monoliths.Values.FirstOrDefault(m => use.Values is [var name, ..] && m.Name == name)?.Id ?? 0,
               use.Values is [_, var mapText, ..] && int.TryParse(mapText, out var m2) ? m2 : 0);
        if (MonolithData.Of(monolithId) is not { } monolith || monolith.Race != profileRace) return null;
        var groups = monolith.Groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = WorldObjects.ObjectsOn(map).Where(o => IsTrace(o) && !TraceCollected(o)
            && Achievements.Of(o.Achievement) is { } entry && groups.Contains(entry.Group)).ToList();
        if (left.Count == 0) return null;
        var rewards = left.SelectMany(o => LootTables.Embedded.Roll(o.Env, profileRace, rng)).GroupBy(i => i.ItemId)
            .Select(g => (g.Key, g.Sum(i => i.Count))).ToList();
        var done = await Stored(async () => await store.ConsumeAndGrantAsync(character, row.InstanceId, row.Revision, 1, rewards, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        var replies = Used(row, reply);
        replies.AddRange(await InventoryChangesAsync(ct, EnvBoxPackets.IncreaseEnvObjReward, DecreaseItemUse));
        foreach (var env in left.Select(o => o.Env).Distinct())
        {
            var n = left.Count(o => o.Env == env);
            replies.AddRange(Achieve("UseEnvObj", n, e => e.Envs?.Contains(env) == true, now));
        }
        var ids = left.Select(o => o.Achievement).ToHashSet();
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => ids.Contains(e.Id), now));
        Note($"Monolith voucher {row.TemplateId}: {left.Count} traces of {monolith.Name} on map {map} collected");
        return replies;
    }

    /// <summary>Checks only: a Monolith's count as stored.</summary>
    public void SetMonolithForChecks(int id, int count) { monolithCounts[id] = count; monolithStats = null; }
    public int MonolithStatForChecks(string stat) => MonolithStat(stat);
    /// <summary>Checks only: the character's race (1 Elyos, 2 Asmodian).</summary>
    public int RaceForChecks => profileRace;
}
