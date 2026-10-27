using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Period item collections (tools/data/collections.json, tools/collection_data.py).</summary>
public sealed class CollectionTables
{
    public sealed record Collection([property: JsonPropertyName("g")] string Group, [property: JsonPropertyName("r")] int Race, [property: JsonPropertyName("t")] int Tab,
        [property: JsonPropertyName("dated")] bool Dated, [property: JsonPropertyName("week")] int Week, [property: JsonPropertyName("kind")] int Kind,
        [property: JsonPropertyName("day")] int Day, [property: JsonPropertyName("level")] int Level)
    {
        public int Id { get; set; }
    }
    public sealed record ListEntry([property: JsonPropertyName("n")] int Target, [property: JsonPropertyName("targets")] int[] Targets,
        [property: JsonPropertyName("rewards")] long[][] Rewards, [property: JsonPropertyName("exp")] int Exp)
    {
        public int Id { get; set; }
    }
    public sealed record Season([property: JsonPropertyName("from")] long FromMs, [property: JsonPropertyName("to")] long ToMs,
        [property: JsonPropertyName("reset_hour_utc")] int ResetHour, [property: JsonPropertyName("daily_lists")] int DailyLists, [property: JsonPropertyName("weekly_lists")] int WeeklyLists);
    sealed record File([property: JsonPropertyName("season")] Season Season, [property: JsonPropertyName("collections")] Dictionary<string, Collection> Collections,
        [property: JsonPropertyName("lists")] Dictionary<string, Dictionary<string, ListEntry>> Lists);

    readonly File file;
    readonly Dictionary<int, Collection> byId = [];
    readonly Dictionary<string, List<ListEntry>> listsByGroup = [];

    CollectionTables(File file)
    {
        this.file = file;
        foreach (var (key, c) in file.Collections) { c.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture); byId[c.Id] = c; }
        foreach (var (group, lists) in file.Lists)
        {
            foreach (var (key, l) in lists) l.Id = int.Parse(key, System.Globalization.CultureInfo.InvariantCulture);
            listsByGroup[group] = lists.Values.OrderBy(l => l.Id).ToList();
        }
    }

    static readonly Lazy<CollectionTables> embedded = new(() =>
    {
        using var stream = typeof(CollectionTables).Assembly.GetManifestResourceStream("collections.json") ?? throw new InvalidOperationException("collections.json is not embedded");
        return new CollectionTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidDataException("collections.json"));
    });
    public static CollectionTables Embedded => embedded.Value;

    public Season Window => file.Season;
    public Collection? Of(int id) => byId.GetValueOrDefault(id);
    public IReadOnlyList<ListEntry> ListsOf(string group) => listsByGroup.GetValueOrDefault(group) ?? [];
    public ListEntry? List(string group, int listId) => ListsOf(group).FirstOrDefault(l => l.Id == listId);

    const long DayMs = 86_400_000L;

    /// <summary>The season day index (0-based) and week index (1-based) of a moment; -1 outside the season.</summary>
    public (int Day, int Week) At(long nowMs)
    {
        if (nowMs < file.Season.FromMs || nowMs > file.Season.ToMs) return (-1, -1);
        var day = (int)((nowMs - file.Season.FromMs) / DayMs);
        return (day, day / 7 + 1);
    }

    /// <summary>The collections open now for a race with their windows: today's daily, this week's weekly, the season-long ones.</summary>
    public List<(Collection Collection, long From, long To)> Open(int race, long nowMs)
    {
        var (day, week) = At(nowMs);
        var open = new List<(Collection, long, long)>();
        if (day < 0) return open;
        var dayStart = file.Season.FromMs + day * DayMs;
        var weekStart = file.Season.FromMs + (week - 1) * 7 * DayMs;
        foreach (var c in byId.Values.Where(c => c.Race == 0 || c.Race == race))
        {
            if (!c.Dated) open.Add((c, file.Season.FromMs, file.Season.ToMs));
            else if (c.Kind == 1 && c.Week == week && c.Day == day % 7 + 1) open.Add((c, dayStart, dayStart + DayMs - 1));
            else if (c.Kind == 2 && c.Week == week) open.Add((c, weekStart, weekStart + 7 * DayMs - 1));
        }
        return open.OrderBy(o => o.Item1.Tab).ToList();
    }

    /// <summary>The lists a collection offers: every list for the season-long ones, ten picked by the collection id for the daily and weekly ones (OURS).</summary>
    public List<ListEntry> Offered(Collection c)
    {
        var all = ListsOf(c.Group);
        if (!c.Dated) return all.ToList();
        var take = c.Kind == 1 ? file.Season.DailyLists : file.Season.WeeklyLists;
        var rng = new Random(c.Id);
        return all.OrderBy(_ => rng.Next()).Take(take).OrderBy(l => l.Id).ToList();
    }
}

// The season's collection book (OBSERVED PeriodCollectionList_NT 0xe256 at the official world entry, 1685 bytes: six tabs,
// {collection i32, tab u8, start i64, end i64, [{collection i32, list i32, registered i32, rewarded bool}]}, the bools
// packed eight to a byte across the message as the PacketWriter does; the daily 5401317 with 10 lists, the weekly
// 5401321 with 10, Craft 5403141 and Boss 5403131 with all 54, tabs 5 and 6 empty). Registering (RegisterPeriodCollection_RQ
// 0xe23d {collection, list, [{uid, item, count, container}]} → 0xe23e; TakePeriodCollectionReward_NT 0xe23f when the list's
// count is reached) consumes the bag items and pays the list's rewards once; PeriodCollectionList_RQ 0xe23b {tab} →
// 0xe23c + the list again. Migration 032. Which ten lists a day offers is the server's choice (OURS: seeded by the id).
public sealed partial class GameSession
{
    public const ushort PeriodCollectionListNt = 0xe256, PeriodCollectionListRq = 0xe23b, PeriodCollectionListRs = 0xe23c,
        RegisterPeriodCollectionRq = 0xe23d, RegisterPeriodCollectionRs = 0xe23e, TakePeriodCollectionRewardNt = 0xe23f;
    const byte DecreaseCollectionRegister = 40, IncreaseCollectionReward = 41;   // EDecreaseReason / EIncreaseReason (CANDIDATE values)
    static CollectionTables Collections => CollectionTables.Embedded;

    /// <summary>(collection, list) → (registered, rewarded).</summary>
    readonly Dictionary<(int Collection, int List), (int Registered, bool Rewarded)> collectionProgress = [];

    void LoadCollections(IReadOnlyList<CollectionRecord> stored)
    {
        collectionProgress.Clear();
        foreach (var r in stored) collectionProgress[(r.CollectionId, r.ListId)] = (r.Registered, r.Rewarded);
    }

    static PacketWriter WriteDelivery(PacketWriter w, int collection, int list, (int Registered, bool Rewarded) state) =>
        w.I32(collection).I32(list).I32(state.Registered).Bool(state.Rewarded);

    byte[] PeriodCollectionList(long now)
    {
        var open = Collections.Open(profileRace, now);
        var w = PacketWriter.Opcode(PeriodCollectionListNt).Varint((uint)open.Count);
        foreach (var (collection, from, to) in open)
        {
            var lists = Collections.Offered(collection);
            w.I32(collection.Id).U8((byte)collection.Tab).I64(from).I64(to).Varint((uint)lists.Count);
            foreach (var list in lists) WriteDelivery(w, collection.Id, list.Id, collectionProgress.GetValueOrDefault((collection.Id, list.Id)));
        }
        return w.ToArray();
    }

    /// <summary>0xe23b PeriodCollectionList_RQ {tab u8}: the book again.</summary>
    List<byte[]> PeriodCollectionListRequest(byte[] plain, long now) =>
        [PacketWriter.Opcode(PeriodCollectionListRs).U16(0).U8(plain.Length > 2 ? plain[2] : (byte)0).ToArray(), PeriodCollectionList(now)];

    /// <summary>0xe23d RegisterPeriodCollection_RQ: the items into the list; the reward when its count is reached.</summary>
    async Task<List<byte[]>> RegisterCollectionAsync(byte[] plain, long now, CancellationToken ct)
    {
        int collectionId, listId;
        var items = new List<(long Uid, int ItemId, int Count, byte Container)>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            collectionId = reader.I32();
            listId = reader.I32();
            var count = reader.Varint();
            for (var i = 0; i < count && i < 32; i++) items.Add(((long)reader.U64(), reader.I32(), reader.I32(), reader.U8()));
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(int result, (int Registered, bool Rewarded) state)
        {
            var w = PacketWriter.Opcode(RegisterPeriodCollectionRs).U16(result);
            WriteDelivery(w, collectionId, listId, state).Varint((uint)items.Count);
            foreach (var (uid, item, count, container) in items) w.I64(uid).I32(item).I32(count).U8(container);
            return w.ToArray();
        }
        var state = collectionProgress.GetValueOrDefault((collectionId, listId));
        var open = Collections.Open(profileRace, now).FirstOrDefault(o => o.Collection.Id == collectionId);
        if (open.Collection is not { } collection || Collections.List(collection.Group, listId) is not { } list || !Collections.Offered(collection).Any(l => l.Id == listId)
            || state.Rewarded || items.Count == 0 || Store is not { } store || CharacterId is not { } character)
            return [Reply(1, state)];
        var room = list.Target - state.Registered;
        var consumed = 0;
        foreach (var (uid, itemId, count, _) in items)
        {
            if (room <= 0) break;
            if (!storedItems.TryGetValue(uid, out var row) || row.Location != "bag" || row.TemplateId != itemId || !list.Targets.Contains(itemId) || count < 1) continue;
            var take = (int)Math.Min(Math.Min(count, room), row.Quantity);
            var ok = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, take, ct) ? [[]] : []);
            if (ok.Count == 0) continue;
            consumed += take;
            room -= take;
            await InventoryChangesAsync(ct, 0, DecreaseCollectionRegister);   // the next stack sees fresh revisions
        }
        if (consumed == 0) return [Reply(1, state)];
        state = (state.Registered + consumed, false);
        var replies = new List<byte[]>();
        if (state.Registered >= list.Target)
        {
            state = (state.Registered, true);
            var rewards = list.Rewards.Where(r => r.Length >= 2).Select(r => ((int)r[0], r[1])).ToList();
            if (rewards.Count > 0) await Stored(async () => await store.GrantLootAsync(character, rewards, ct) ? [[]] : []);
            var nt = PacketWriter.Opcode(TakePeriodCollectionRewardNt).I32(collectionId).I32(listId).I32(list.Exp).Varint((uint)rewards.Count);
            foreach (var (item, count) in rewards) nt.I32(item).I32((int)Math.Min(count, int.MaxValue));
            replies.Add(nt.ToArray());
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseCollectionReward, 0));
        }
        collectionProgress[(collectionId, listId)] = state;
        await Stored(async () => await store.SaveCollectionAsync(character, collectionId, listId, state.Registered, state.Rewarded, ct) ? [[]] : []);
        return [Reply(0, state), .. replies];
    }
}
