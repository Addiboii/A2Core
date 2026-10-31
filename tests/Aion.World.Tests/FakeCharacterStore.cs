using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.World.Tests;

/// <summary>In-memory store for GameSession integration tests (subset of Native.Checks FakeStore).</summary>
public sealed class FakeCharacterStore : INativeCharacterStore
{
    public Guid AccountId { get; }

    public FakeCharacterStore(Guid? accountId = null) =>
        AccountId = accountId ?? Guid.Parse("0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b");
    public int WorldId { get; } = 1204;
    public int ExtendedCharacterSlots { get; set; }

    public Task<int> ExtendedCharacterSlotsAsync(CancellationToken ct) => Task.FromResult(ExtendedCharacterSlots);

    public List<CharacterState> States { get; } = [];
    public List<QuestRecord> Quests { get; } = [];
    public List<ItemState> Items { get; } = [];
    public List<int> Wings { get; } = [];
    public List<(string Code, long Exp, int ItemCount)> Rewards { get; } = [];
    public List<SkillState> Skills { get; } = [];
    public List<(int Target, int GoodsId, string Period, int Count)> Purchases { get; } = [];
    public List<(int Target, int RecipeId, string Period, int Count)> ConversionCounts { get; } = [];
    public List<AchievementRecord> Achievements { get; } = [];
    public (int Level, long Exp) Progress { get; set; } = (1, 0);
    public Dictionary<int, QuickSlotRecord> QuickSlots { get; } = [];
    public Dictionary<string, JsonElement> Settings { get; } = [];

    public static ItemState BagItem(int template, long quantity, long wire, int? slot = null, JsonElement? upgrades = null)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", slot, 1, empty, upgrades ?? empty, wire);
    }

    public static ItemState StorageItem(int template, long quantity, long wire, int? slot = null) =>
        BagItem(template, quantity, wire, slot) with { Location = "storage" };

    public static ItemState EquippedItem(int template, long wire, int slot)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return new ItemState(Guid.NewGuid(), "b", template, 1, "equipment", slot, 1, empty, empty, wire);
    }

    public static void SeedExpeditionItemLevel(FakeCharacterStore store, int templateId = 110120003, int slots = 2, int level = 55)
    {
        store.Progress = (level, 0);
        for (var slot = 1; slot <= slots; slot++)
            store.Items.Add(EquippedItem(templateId, 4000 + slot, slot));
    }

    bool Consume(Guid item, long revision, int count)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag" && i.Quantity >= count);
        if (index < 0) return false;
        if (Items[index].Quantity == count) Items.RemoveAt(index);
        else Items[index] = Items[index] with { Quantity = Items[index].Quantity - count, Revision = revision + 1 };
        return true;
    }

    public Task<IReadOnlyList<CharacterState>> LobbyAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CharacterState>>(States);
    public Task<CharacterState?> LoadWireAsync(int wireId, CancellationToken ct) =>
        Task.FromResult(States.FirstOrDefault(s => s.WireId == wireId));

    public Task<CharacterState> CreateAsync(CreatePlayer input, CancellationToken ct)
    {
        if (States.Any(s => string.Equals(s.Summary.Name, input.Name, StringComparison.OrdinalIgnoreCase)))
            throw new GameplayException("name_taken");
        var parts = input.ProfileId["dev-v1-".Length..].Split(':');
        var (creation, gender) = (int.Parse(parts[0]), int.Parse(parts[1]));
        var profile = GameData.Embedded.Profile(creation, gender)!;
        var homeMap = profile.Race == 2 ? 1100 : 1000;
        var homeAt = GameData.Embedded.MapStart(homeMap)?.Position ?? GamePackets.SpawnPosition;
        var state = new CharacterState(
            new CharacterSummary(Guid.NewGuid(), AccountId, WorldId, input.Name, 1),
            creation, profile.Race, gender, input.Appearance,
            new CharacterProgress(Progress.Level, Progress.Exp, homeMap, StoredCharacters.PositionJson(homeAt), JsonSerializer.SerializeToElement(new { })),
            1, WireId: 70 + States.Count, VoiceType: input.VoiceType, LobbySlot: input.LobbySlot);
        States.Add(state);
        if (!Items.Any(i => i.Location == "equipment" && i.Slot == 1)
            && GameData.Embedded.StarterWeapon(profile) is { } starterId)
        {
            var wire = Items.Count == 0 ? 6000 : Items.Max(i => i.WireId) + 1;
            Items.Add(EquippedItem(starterId, wire, slot: 1));
        }
        return Task.FromResult(state);
    }

    public Task<bool> SwapLobbySlotsAsync(IReadOnlyList<(long WireId, int Slot)> assignments, int slotCapacity, CancellationToken ct)
    {
        if (assignments.Count == 0 || slotCapacity <= 0
            || assignments.Any(a => a.WireId <= 0 || a.Slot < 0 || a.Slot >= slotCapacity)
            || assignments.Select(a => a.WireId).Distinct().Count() != assignments.Count
            || assignments.Select(a => a.Slot).Distinct().Count() != assignments.Count)
            return Task.FromResult(false);
        var known = States.Select(s => (long)s.WireId).ToList();
        var mapped = new List<(long Wire, int Slot)>();
        foreach (var (clientDbId, slot) in assignments)
        {
            if (!LobbyWireIds.TryResolveWire(clientDbId, known, out var wire)) return Task.FromResult(false);
            mapped.Add((wire, slot));
        }
        var slots = States.Where(s => s.LobbySlot is not null).ToDictionary(s => (long)s.WireId, s => s.LobbySlot!.Value);
        var final = new Dictionary<long, int>(slots);
        foreach (var (wire, slot) in mapped) final[wire] = slot;
        foreach (var state in States)
        {
            if (mapped.Any(m => m.Wire == state.WireId)) continue;
            if (state.LobbySlot is int kept) final[state.WireId] = kept;
        }
        if (final.Values.Distinct().Count() != final.Count) return Task.FromResult(false);
        foreach (var (wire, slot) in mapped)
        {
            var idx = States.FindIndex(s => s.WireId == wire);
            if (idx >= 0) States[idx] = States[idx] with { LobbySlot = slot };
        }
        return Task.FromResult(true);
    }

    public Task<CharacterState> SavePositionAsync(CharacterState state, int mapId, Vector3d position, CancellationToken ct)
    {
        var idx = States.FindIndex(s => s.Summary.CharacterId == state.Summary.CharacterId);
        // Keep level/exp written by SaveProgressAsync; checkpoints only move map/position (matches Postgres split).
        var basis = idx >= 0 ? States[idx].Progress : state.Progress;
        var progress = basis with { MapId = mapId, Position = StoredCharacters.PositionJson(position) };
        var updated = state with { Progress = progress, Revision = state.Revision + 1 };
        if (idx >= 0) States[idx] = updated;
        return Task.FromResult(updated);
    }

    public int LogoutRecordCount { get; private set; }

    public Task<CharacterState?> RecordLoginAsync(CharacterState state, long nowMs, CancellationToken ct)
    {
        var updated = state with { LastLoginUnixMs = nowMs };
        var idx = States.FindIndex(s => s.Summary.CharacterId == state.Summary.CharacterId);
        if (idx >= 0) States[idx] = updated;
        return Task.FromResult<CharacterState?>(updated);
    }

    public Task<CharacterState?> RecordLogoutAsync(CharacterState state, long nowMs, CancellationToken ct)
    {
        LogoutRecordCount++;
        var updated = state with { LastLogoutUnixMs = nowMs };
        var idx = States.FindIndex(s => s.Summary.CharacterId == state.Summary.CharacterId);
        if (idx >= 0) States[idx] = updated;
        return Task.FromResult<CharacterState?>(updated);
    }

    public Task<IReadOnlyList<ItemState>> InventoryAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ItemState>>(Items.ToList());

    public Task<long> AccountQunaBalanceAsync(CancellationToken ct) =>
        Task.FromResult(Items.Where(i => i.TemplateId == PlatformGoodsTables.Embedded.QunaItemId && i.Location is "bag" or "storage").Sum(i => i.Quantity));

    public Task<bool> MoveItemAsync(Guid character, Guid item, long revision, int? equipmentSlot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0) return Task.FromResult(false);
        for (var i = 0; i < Items.Count; i++)
            if (i != index && equipmentSlot is not null && Items[i].Location == "equipment" && Items[i].Slot == equipmentSlot)
                Items[i] = Items[i] with { Location = "bag", Slot = null, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with
        {
            Location = equipmentSlot is null ? "bag" : "equipment",
            Slot = equipmentSlot,
            Revision = revision + 1
        };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<QuestRecord>> QuestsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QuestRecord>>(Quests.ToList());

    public Task<QuestRecord> SaveQuestAsync(Guid character, QuestRecord quest, CancellationToken ct)
    {
        var index = Quests.FindIndex(q => q.Code == quest.Code);
        var saved = quest with { Uid = index >= 0 ? Quests[index].Uid : Quests.Count + 100L };
        if (index >= 0) Quests[index] = saved; else Quests.Add(saved);
        return Task.FromResult(saved);
    }

    public Task<bool> RemoveQuestAsync(Guid character, string code, CancellationToken ct) =>
        Task.FromResult(Quests.RemoveAll(q => q.Code == code) > 0);

    public Task<long?> RewardQuestAsync(Guid character, string code, long experience, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var index = Quests.FindIndex(q => q.Code == code && q.Status == QuestLog.Complete);
        if (index < 0) return Task.FromResult<long?>(null);
        Quests[index] = Quests[index] with { Status = QuestLog.Rewarded };
        Rewards.Add((code, experience, items.Count));
        if (items.Count > 0)
        {
            var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
            foreach (var (template, quantity) in items)
                Items.Add(BagItem(template, quantity, wire++));
        }
        Progress = (Progress.Level, Progress.Exp + experience);
        return Task.FromResult<long?>(Progress.Exp);
    }

    public Task<bool> SaveProgressAsync(Guid character, int level, long experience, CancellationToken ct)
    {
        Progress = (level, experience);
        var idx = States.FindIndex(s => s.Summary.CharacterId == character);
        if (idx >= 0)
            States[idx] = States[idx] with { Progress = States[idx].Progress with { Level = level, Experience = experience } };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<int>> WingsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>(Wings.ToList());

    public SortedSet<int> Titles { get; } = [];
    public Dictionary<int, int> TitleEquips { get; } = [];

    public Task<CharacterExtras> ExtrasAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(CharacterExtras.None with { Titles = Titles.ToList(), TitleEquips = new Dictionary<int, int>(TitleEquips) });

    public Task<bool> GrantTitleAsync(Guid character, int titleId, CancellationToken ct) => Task.FromResult(Titles.Add(titleId));

    public Task<bool> SetTitleAsync(Guid character, int category, int titleId, CancellationToken ct)
    {
        if (titleId == 0) TitleEquips.Remove(category);
        else if (!Titles.Contains(titleId)) return Task.FromResult(false);
        else TitleEquips[category] = titleId;
        return Task.FromResult(true);
    }

    public Task<bool> SoulBindItemAsync(Guid character, Guid item, byte count, CancellationToken ct) =>
        Task.FromResult(false);

    readonly InMemorySocialGraph socialGraph = new();
    CharacterState? StateByName(string name) =>
        States.FirstOrDefault(s => s.Summary.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    CharacterState? StateById(Guid id) => States.FirstOrDefault(s => s.Summary.CharacterId == id);

    public Task<IReadOnlyList<SocialRelation>> SocialAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SocialRelation>>(socialGraph.List(character, StateById));

    public Task<(string Result, SocialRelation? Target)> FriendRequestAsync(Guid character, string targetName, CancellationToken ct) =>
        Task.FromResult(socialGraph.FriendRequest(character, targetName, StateByName));

    public Task<bool> FriendRespondAsync(Guid character, Guid from, bool accept, CancellationToken ct) =>
        Task.FromResult(socialGraph.FriendRespond(character, from, accept));

    public Task<bool> FriendCancelAsync(Guid character, Guid to, CancellationToken ct) =>
        Task.FromResult(socialGraph.FriendCancel(character, to));

    public Task<bool> FriendDeleteAsync(Guid character, Guid friend, CancellationToken ct) =>
        Task.FromResult(socialGraph.FriendDelete(character, friend));

    public Task<bool> FriendMemoAsync(Guid character, Guid friend, string memo, CancellationToken ct) =>
        Task.FromResult(socialGraph.FriendMemo(character, friend, memo));

    public Task<SocialRelation?> BlockAsync(Guid character, string targetName, CancellationToken ct) =>
        Task.FromResult(socialGraph.Block(character, targetName, StateByName));

    public Task<bool> UnblockAsync(Guid character, Guid blocked, CancellationToken ct) =>
        Task.FromResult(socialGraph.Unblock(character, blocked));

    public Task<bool> SocialBlockedAsync(Guid character, Guid other, CancellationToken ct) =>
        Task.FromResult(socialGraph.BlockedBetween(character, other));

    public List<PostRecord> Posts { get; } = [];
    public List<TradeListingRecord> TradeListings { get; } = [];
    public List<TradeDeliveryRecord> TradeDeliveries { get; } = [];
    internal static readonly Dictionary<long, (FakeCharacterStore Store, TradeListingRecord Listing)> BrokerListings = new();

    /// <summary>Simulated world time for economy checks; when unset, uses UTC (Postgres uses DB clock).</summary>
    internal static long EconomyTimeMs { get; set; }

    static long EconomyNowMs() =>
        EconomyTimeMs > 0 ? EconomyTimeMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    internal static readonly Dictionary<Guid, List<PostRecord>> Inboxes = new();
    static long nextPostId = 1;
    static readonly List<PushRewardRecord> PushRewardCatalog = SeedPushRewards();
    static readonly Dictionary<Guid, HashSet<int>> PushRewardsTaken = new();
    static int nextPushRewardId = 7;

    static List<PushRewardRecord> SeedPushRewards()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long Expire(int days) => now + days * 86_400_000L;
        return
        [
            new(1, 1, "AION", "Launch Gift I", "Welcome.", 533460001, 500, now, Expire(70)),
            new(1, 2, "AION", "Launch Gift II", "Welcome.", 532760001, 1, now, Expire(70)),
            new(1, 3, "AION", "Weekly Gift", "Weekly reward.", 531060026, 3, now, Expire(7)),
            new(2, 4, "AION", "Daily Gift I", "Daily reward.", 530116006, 3, now, Expire(7)),
            new(2, 5, "AION", "Daily Gift II", "Daily reward.", 503700031, 1, now, Expire(7)),
            new(2, 6, "AION", "Daily Gift III", "Daily reward.", 530116001, 10, now, Expire(7)),
        ];
    }
    static long nextSaleId = 1000;
    static long nextDeliveryId = 5000;

    public static long KinaIn(FakeCharacterStore store) =>
        store.Items.Where(i => i.TemplateId == GameSession.KinaItemId && i.Location == "bag").Sum(i => i.Quantity);

    static bool TakeKina(FakeCharacterStore store, long amount)
    {
        if (amount <= 0) return true;
        var left = amount;
        foreach (var index in store.Items.Select((item, i) => (item, i)).Where(x => x.item.TemplateId == GameSession.KinaItemId && x.item.Location == "bag").Select(x => x.i).ToList())
        {
            var row = store.Items[index];
            var take = Math.Min(left, row.Quantity);
            if (row.Quantity == take) store.Items.RemoveAt(index);
            else store.Items[index] = row with { Quantity = row.Quantity - take, Revision = row.Revision + 1 };
            left -= take;
            if (left == 0) return true;
        }
        return false;
    }

    static void GrantKina(FakeCharacterStore store, long amount)
    {
        if (amount <= 0) return;
        var stack = store.Items.FirstOrDefault(i => i.TemplateId == GameSession.KinaItemId && i.Location == "bag");
        if (stack is not null)
        {
            var index = store.Items.IndexOf(stack);
            store.Items[index] = stack with { Quantity = stack.Quantity + amount, Revision = stack.Revision + 1 };
        }
        else
            store.Items.Add(BagItem(GameSession.KinaItemId, amount, store.Items.Count == 0 ? 5000 : store.Items.Max(i => i.WireId) + 1));
    }

    public Task<bool> ExchangeAsync(Guid character, IReadOnlyList<(Guid Item, long Count)> gives, INativeCharacterStore otherStore, Guid other,
        IReadOnlyList<(Guid Item, long Count)> receives, long givesKina = 0, long receivesKina = 0, CancellationToken ct = default)
    {
        if (otherStore is not FakeCharacterStore partner) return Task.FromResult(false);
        static bool Move(FakeCharacterStore from, FakeCharacterStore to, IReadOnlyList<(Guid Item, long Count)> list)
        {
            foreach (var (itemId, count) in list)
            {
                var index = from.Items.FindIndex(i => i.InstanceId == itemId);
                if (index < 0 || from.Items[index].Quantity < count) return false;
                var row = from.Items[index];
                if (row.Quantity == count) from.Items.RemoveAt(index);
                else from.Items[index] = row with { Quantity = row.Quantity - count, Revision = row.Revision + 1 };
                var upgrades = row.Upgrades;
                if (upgrades.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(upgrades.GetRawText()) ?? [];
                    dict["enchant_bonus"] = JsonSerializer.SerializeToElement(0);
                    dict["surpass_enchant_bonus"] = JsonSerializer.SerializeToElement(0);
                    upgrades = JsonSerializer.SerializeToElement(dict);
                }
                to.Items.Add(row with { Quantity = count, Revision = row.Revision + 1, Upgrades = upgrades });
            }
            return true;
        }
        if (givesKina > 0 && !TakeKina(this, givesKina)) return Task.FromResult(false);
        if (receivesKina > 0 && !TakeKina(partner, receivesKina)) return Task.FromResult(false);
        if (!Move(this, partner, gives) || !Move(partner, this, receives)) return Task.FromResult(false);
        GrantKina(this, receivesKina);
        GrantKina(partner, givesKina);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<int>> MapFogAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
    public Task<IReadOnlyList<int>> OpenMapFogAsync(Guid character, IReadOnlyList<int> groups, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>(groups.ToList());

    public List<int> TeleportArtifacts { get; } = [];
    public Task<IReadOnlyList<int>> TeleportArtifactsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>(TeleportArtifacts.ToList());
    public Task<bool> EngraveTeleportArtifactAsync(Guid character, int artifactId, CancellationToken ct)
    {
        if (TeleportArtifacts.Contains(artifactId)) return Task.FromResult(false);
        TeleportArtifacts.Add(artifactId);
        return Task.FromResult(true);
    }

    public Task<bool> GrantLootAsync(Guid character, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        foreach (var (template, quantity) in items)
            Items.Add(new ItemState(Guid.NewGuid(), "b", template, quantity, "bag", null, 1, empty, empty, wire++));
        return Task.FromResult(true);
    }

    public Task<bool> EnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int level, int bonus, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0) return Task.FromResult(false);
        foreach (var (costItem, costRev, count) in costs)
        {
            var cIdx = Items.FindIndex(i => i.InstanceId == costItem && i.Revision == costRev);
            if (cIdx < 0 || Items[cIdx].Quantity < count) return Task.FromResult(false);
            if (Items[cIdx].Quantity == count) Items.RemoveAt(cIdx);
            else Items[cIdx] = Items[cIdx] with { Quantity = Items[cIdx].Quantity - count, Revision = Items[cIdx].Revision + 1 };
        }
        Items[index] = Items[index] with
        {
            Upgrades = JsonSerializer.SerializeToElement(new { enchant = level, enchant_bonus = bonus }),
            Revision = revision + 1
        };
        return Task.FromResult(true);
    }

    public Task<bool> ExceedEnchantItemAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int surpassLevel, int surpassBonus, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0) return Task.FromResult(false);
        foreach (var (costItem, costRev, count) in costs)
        {
            var cIdx = Items.FindIndex(i => i.InstanceId == costItem && i.Revision == costRev);
            if (cIdx < 0 || Items[cIdx].Quantity < count) return Task.FromResult(false);
            if (Items[cIdx].Quantity == count) Items.RemoveAt(cIdx);
            else Items[cIdx] = Items[cIdx] with { Quantity = Items[cIdx].Quantity - count, Revision = Items[cIdx].Revision + 1 };
        }
        var row = Items[index];
        var dict = row.Upgrades.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Upgrades.GetRawText()) ?? []
            : [];
        dict["surpass_enchant"] = JsonSerializer.SerializeToElement(surpassLevel);
        dict["surpass_enchant_bonus"] = JsonSerializer.SerializeToElement(surpassBonus);
        Items[index] = row with { Upgrades = JsonSerializer.SerializeToElement(dict), Revision = revision + 1 };
        return Task.FromResult(true);
    }

    public Task<bool> SetRolledStatsAsync(Guid character, Guid item, long revision, JsonElement rolled, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision);
        if (index < 0) return Task.FromResult(false);
        if (Items[index].RolledStats.ValueKind == JsonValueKind.Object && Items[index].RolledStats.EnumerateObject().Any())
            return Task.FromResult(false);
        Items[index] = Items[index] with { RolledStats = rolled, Revision = revision + 1 };
        return Task.FromResult(true);
    }

    public Task<bool> GrantSkinsAsync(Guid character, IReadOnlyList<int> skins, CancellationToken ct)
    {
        if (skins.Count == 0) return Task.FromResult(false);
        return Task.FromResult(true);
    }

    public Task<bool> SkinExtractAsync(Guid character, Guid item, long revision, IReadOnlyList<(Guid Item, long Revision, int Count)> costs,
        int skinId, int extractionUsed, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag");
        if (index < 0) return Task.FromResult(false);
        foreach (var (costItem, costRev, count) in costs)
        {
            var cIdx = Items.FindIndex(i => i.InstanceId == costItem && i.Revision == costRev);
            if (cIdx < 0 || Items[cIdx].Quantity < count) return Task.FromResult(false);
            if (Items[cIdx].Quantity == count) Items.RemoveAt(cIdx);
            else Items[cIdx] = Items[cIdx] with { Quantity = Items[cIdx].Quantity - count, Revision = Items[cIdx].Revision + 1 };
        }
        var row = Items[index];
        var dict = row.Upgrades.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Upgrades.GetRawText()) ?? []
            : [];
        dict["skin_extraction_used"] = JsonSerializer.SerializeToElement(extractionUsed);
        Items[index] = row with { Upgrades = JsonSerializer.SerializeToElement(dict), Revision = revision + 1 };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<QuickSlotRecord>> QuickSlotsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<QuickSlotRecord>>(QuickSlots.Values.OrderBy(s => s.Slot).ToList());

    public Task<bool> SaveQuickSlotAsync(Guid character, int number, QuickSlotRecord? slot, CancellationToken ct)
    {
        if (slot is null) QuickSlots.Remove(number);
        else QuickSlots[number] = slot;
        return Task.FromResult(true);
    }

    public Dictionary<int, int> GodstoneCollection { get; } = [];

    public Task<IReadOnlyDictionary<int, int>> GodstoneCollectionAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<int, int>>(GodstoneCollection);

    public Task<bool> UpsertGodstoneCollectionAsync(Guid character, IReadOnlyList<(int GodstoneId, int Delta)> updates, CancellationToken ct)
    {
        foreach (var (id, delta) in updates)
        {
            GodstoneCollection.TryGetValue(id, out var count);
            count += delta;
            if (count <= 0) GodstoneCollection.Remove(id);
            else GodstoneCollection[id] = count;
        }
        return Task.FromResult(true);
    }

    public DaevanionProgress? Daevanion { get; set; }

    public Task<DaevanionProgress?> DaevanionAsync(Guid character, CancellationToken ct) => Task.FromResult(Daevanion);

    public Task<bool> SaveDaevanionAsync(Guid character, DaevanionProgress progress, CancellationToken ct)
    {
        Daevanion = progress;
        return Task.FromResult(true);
    }

    public Task<bool> SocketGodstoneAsync(Guid character, Guid equip, long equipRevision, Guid material, long materialRevision,
        IReadOnlyList<int> godstones, CancellationToken ct)
    {
        var eIdx = Items.FindIndex(i => i.InstanceId == equip && i.Revision == equipRevision);
        var mIdx = Items.FindIndex(i => i.InstanceId == material && i.Revision == materialRevision);
        if (eIdx < 0 || mIdx < 0 || Items[mIdx].Quantity < 1) return Task.FromResult(false);
        if (Items[mIdx].Quantity == 1) Items.RemoveAt(mIdx);
        else Items[mIdx] = Items[mIdx] with { Quantity = Items[mIdx].Quantity - 1, Revision = Items[mIdx].Revision + 1 };
        Items[eIdx] = Items[eIdx] with
        {
            Upgrades = JsonSerializer.SerializeToElement(new { godstones = godstones.ToArray() }),
            Revision = equipRevision + 1
        };
        return Task.FromResult(true);
    }

    public Task<bool> CombineGodstonesAsync(Guid character, IReadOnlyDictionary<int, int> materials, int rewardId, CancellationToken ct)
    {
        foreach (var (templateId, count) in materials)
        {
            var left = count;
            foreach (var row in Items.Where(i => i.Location == "bag" && i.TemplateId == templateId).ToList())
            {
                if (left <= 0) break;
                var take = (int)Math.Min(left, row.Quantity);
                left -= take;
                var idx = Items.FindIndex(i => i.InstanceId == row.InstanceId);
                if (Items[idx].Quantity == take) Items.RemoveAt(idx);
                else Items[idx] = Items[idx] with { Quantity = Items[idx].Quantity - take, Revision = Items[idx].Revision + 1 };
            }
            if (left > 0) return Task.FromResult(false);
        }
        if (rewardId > 0)
        {
            var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
            Items.Add(BagItem(rewardId, 1, wire));
        }
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<(int Target, int GoodsId, string Period, int Count)>> ShopPurchasesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, int, string, int)>>(Purchases.ToList());

    public Task<IReadOnlyList<(int Target, int RecipeId, string Period, int Count)>> ConversionCountsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, int, string, int)>>(ConversionCounts.ToList());

    public Task<bool> ConversionAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int RecipeId, string Period, int Count, int Limit)> limits, CancellationToken ct)
    {
        foreach (var l in limits)
        {
            var used = ConversionCounts.Where(c => c.Target == l.Target && c.RecipeId == l.RecipeId && c.Period == l.Period).Sum(c => c.Count);
            if (used + l.Count > l.Limit) return Task.FromResult(false);
        }
        if (costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count)))
            return Task.FromResult(false);
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        foreach (var l in limits) ConversionCounts.Add((l.Target, l.RecipeId, l.Period, l.Count));
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        foreach (var (template, quantity) in goods)
            Items.Add(BagItem(template, quantity, wire++));
        return Task.FromResult(true);
    }

    public Task<bool> TakeAchievementRewardAsync(Guid character, int achievementId, int step, IReadOnlyList<(int TemplateId, long Count)> items, CancellationToken ct)
    {
        var index = Achievements.FindIndex(a => a.Id == achievementId);
        if (index >= 0 && Achievements[index].RewardedStep >= step) return Task.FromResult(false);
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        foreach (var (template, quantity) in items) Items.Add(BagItem(template, quantity, wire++));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AchievementRecord>> AchievementsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AchievementRecord>>(Achievements.ToList());

    public Task<bool> SaveAchievementsAsync(Guid character, IReadOnlyList<AchievementRecord> records, CancellationToken ct)
    {
        foreach (var record in records)
        {
            var index = Achievements.FindIndex(a => a.Id == record.Id);
            if (index < 0) Achievements.Add(record);
            else
            {
                var prev = Achievements[index];
                Achievements[index] = record with
                {
                    Count = Math.Max(prev.Count, record.Count),
                    RewardedStep = Math.Max(prev.RewardedStep, record.RewardedStep),
                    CompletedAt = record.CompletedAt ?? prev.CompletedAt,
                };
            }
        }
        return Task.FromResult(true);
    }

    public Task<bool> ShopBuyAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, IReadOnlyList<(int TemplateId, long Count)> goods,
        IReadOnlyList<(int Target, int GoodsId, string Period, int Count, int Limit)> limits, CancellationToken ct)
    {
        foreach (var l in limits)
            if (Purchases.Where(p => p.GoodsId == l.GoodsId && p.Target == l.Target && p.Period == l.Period).Sum(p => p.Count) + l.Count > l.Limit)
                return Task.FromResult(false);
        if (costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count)))
            return Task.FromResult(false);
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        foreach (var l in limits) Purchases.Add((l.Target, l.GoodsId, l.Period, l.Count));
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        foreach (var (template, quantity) in goods)
        {
            var bagRows = Items.Count(i => i.Location == "bag");
            if (bagRows >= GameSession.ShopBagSlotCap && quantity == 1 && template >= 500_000_000)
                Items.Add(StorageItem(template, quantity, wire++));
            else
                Items.Add(BagItem(template, quantity, wire++));
        }
        return Task.FromResult(true);
    }

    public Task<bool> SellToMerchantAsync(Guid character, Guid item, long revision, int count, long kina, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag" && i.Quantity >= count);
        if (index < 0) return Task.FromResult(false);
        var row = Items[index];
        var sold = JsonSerializer.SerializeToElement(new { sold_for = kina, sold_at = 1_000L });
        var wire = Items.Max(i => i.WireId) + 1;
        if (row.Quantity == count)
            Items[index] = row with { Location = "buyback", Slot = null, Upgrades = sold, Revision = revision + 1 };
        else
        {
            Items[index] = row with { Quantity = row.Quantity - count, Revision = revision + 1 };
            Items.Add(row with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "buyback", Slot = null, Upgrades = sold, WireId = wire++ });
        }
        if (kina > 0) Items.Add(BagItem(GameSession.KinaItemId, kina, wire));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ItemState>> BuybackAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ItemState>>(Items.Where(i => i.Location == "buyback").ToList());

    public Task<string> RestoreItemAsync(Guid character, Guid item, IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Location == "buyback");
        if (index < 0 || costs.Any(c => !Items.Any(i => i.InstanceId == c.Item && i.Revision == c.Revision && i.Quantity >= c.Count)))
            return Task.FromResult("failed");
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        index = Items.FindIndex(i => i.InstanceId == item);
        Items[index] = Items[index] with { Location = "bag", Upgrades = JsonSerializer.SerializeToElement(new { }), Revision = Items[index].Revision + 1 };
        return Task.FromResult("ok");
    }

    public Task<bool> ConsumeItemAsync(Guid character, Guid item, long revision, int count, CancellationToken ct) =>
        Task.FromResult(Consume(item, revision, count));

    public Dictionary<int, int> Crystals { get; } = [];

    public List<RideRecord> RideRows { get; } = [];
    public int DefaultRide { get; set; }

    public Task<RideCollection> RidesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(new RideCollection(DefaultRide, RideRows.ToList(), new Dictionary<int, int>(Crystals)));

    public Task<bool> SetDefaultRideAsync(Guid character, int rideId, CancellationToken ct)
    {
        DefaultRide = rideId;
        return Task.FromResult(true);
    }

    public Task<int?> AddRideCrystalsAsync(Guid character, Guid item, long revision, int crystalType, int count, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult<int?>(null);
        return Task.FromResult<int?>(Crystals[crystalType] = Crystals.GetValueOrDefault(crystalType) + count);
    }

    public Task<bool> ConsumeAndGrantAsync(Guid character, Guid item, long revision, int count, IReadOnlyList<(int TemplateId, long Count)> rewards, CancellationToken ct)
    {
        if (!Consume(item, revision, count)) return Task.FromResult(false);
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        foreach (var (template, quantity) in rewards)
            Items.Add(BagItem(template, quantity, wire++));
        return Task.FromResult(true);
    }

    public Task<bool> SortContainerAsync(Guid character, string location, CancellationToken ct)
    {
        var bag = Items.Select((item, index) => (item, index)).Where(x => x.item.Location == location).OrderBy(x => x.item.TemplateId).ThenBy(x => x.item.WireId).ToList();
        for (var n = 0; n < bag.Count; n++)
            Items[bag[n].index] = bag[n].item with { Slot = n, Revision = bag[n].item.Revision + 1 };
        return Task.FromResult(true);
    }

    public Task<bool> MoveBagItemAsync(Guid character, Guid item, long revision, int slot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location == "bag");
        if (index < 0) return Task.FromResult(false);
        var old = Items[index].Slot;
        for (var i = 0; i < Items.Count; i++)
            if (i != index && Items[i].Location == "bag" && Items[i].Slot == slot)
                Items[i] = Items[i] with { Slot = old, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Slot = slot, Revision = revision + 1 };
        return Task.FromResult(true);
    }

    public List<(int TicketId, string Period, int Count)> ExpeditionTickets { get; } = [];

    public Task<IReadOnlyList<(int TicketId, string Period, int Count)>> ExpeditionTicketsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(int, string, int)>>(ExpeditionTickets.ToList());

    public Task<bool> SaveExpeditionTicketsAsync(Guid character, IReadOnlyList<(int TicketId, string Period, int Count)> rows, CancellationToken ct)
    {
        ExpeditionTickets.Clear();
        ExpeditionTickets.AddRange(rows);
        return Task.FromResult(true);
    }

    public AbyssRecord? Abyss { get; set; }
    public Task<AbyssRecord?> AbyssAsync(Guid character, CancellationToken ct) => Task.FromResult(Abyss);
    public Task<bool> SaveAbyssAsync(Guid character, AbyssRecord record, CancellationToken ct)
    {
        Abyss = record;
        return Task.FromResult(true);
    }

    public Dictionary<long, int> PendingSiegeApByGuild { get; } = [];
    public Task AddPendingSiegeApForGuildAsync(long guildId, int ap, IReadOnlyList<Guid> onlineCharacterIds, CancellationToken ct)
    {
        PendingSiegeApByGuild[guildId] = PendingSiegeApByGuild.GetValueOrDefault(guildId) + ap;
        return Task.CompletedTask;
    }

    public AirspaceWorldRecord AirspaceWorld { get; set; } = new([], []);
    public Task<AirspaceWorldRecord> AirspaceWorldAsync(CancellationToken ct) => Task.FromResult(AirspaceWorld);
    public Task SaveAirspaceWorldAsync(AirspaceWorldRecord record, CancellationToken ct)
    {
        AirspaceWorld = record;
        return Task.CompletedTask;
    }

    public WorldResetCursorRecord ResetCursor { get; set; } = new(0, 0);
    public Task<WorldResetCursorRecord> ResetCursorsAsync(CancellationToken ct) => Task.FromResult(ResetCursor);
    public Task SaveResetCursorsAsync(WorldResetCursorRecord record, CancellationToken ct)
    {
        ResetCursor = record;
        return Task.CompletedTask;
    }
    public Task ApplyDailyLegionResetsAsync(DateTime gameDayUtc, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyWeeklyLegionResetsAsync(long weeklyBoundaryMs, CancellationToken ct) => Task.CompletedTask;
    public List<RewardLimitRecord> RewardLimits { get; } = [];
    public Task<IReadOnlyList<RewardLimitRecord>> RewardLimitsAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<RewardLimitRecord>>(RewardLimits.ToList());
    public Task<bool> SaveRewardLimitsAsync(Guid character, IReadOnlyList<RewardLimitRecord> rows, CancellationToken ct)
    {
        RewardLimits.Clear();
        RewardLimits.AddRange(rows);
        return Task.FromResult(true);
    }
    public List<DungeonCooltimeRecord> DungeonCooltimes { get; } = [];
    public Task<IReadOnlyList<DungeonCooltimeRecord>> DungeonCooltimesAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<DungeonCooltimeRecord>>(DungeonCooltimes.ToList());
    public Task<bool> SaveDungeonCooltimesAsync(Guid character, IReadOnlyList<DungeonCooltimeRecord> rows, CancellationToken ct)
    {
        DungeonCooltimes.Clear();
        DungeonCooltimes.AddRange(rows);
        return Task.FromResult(true);
    }

    public GuildView? GuildRow { get; set; }
    public Task<GuildView?> GuildOfAsync(Guid character, CancellationToken ct) => Task.FromResult(GuildRow);
    public Task<IReadOnlyList<GuildView>> SearchGuildsAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GuildView>>(GuildRow is { } g && g.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ? [g] : []);
    public Task<string> CreateGuildAsync(Guid character, string name, int joinRule, string password, int emblem, string intro,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        if (GuildRow is not null) return Task.FromResult("member");
        foreach (var (cost, revision, count) in costs) Consume(cost, revision, count);
        GuildRow = new GuildView(7, name, 1, 0, emblem, joinRule, password, intro, "", DateTimeOffset.UnixEpoch,
            [new GuildMember(character, States.FirstOrDefault()?.Summary.Name ?? "Hero", 1, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        return Task.FromResult("ok");
    }
    readonly List<GuildJoinRequestSent> joinSent = [];
    readonly List<(Guid Applicant, GuildJoinRequestRecv Recv)> joinRecv = [];
    readonly List<GuildStorageItem> guildStorage = [];
    readonly List<GuildStorageHistoryRow> storageHistory = [];
    readonly HashSet<int> ownedDecos = [];
    long storageRevision = 1;
    long storageLastSortMs;
    int storageExtensionCount;
    int todayAttendance;
    long unionId;
    GuildUnionView? unionView;
    readonly List<(int Type, long Amount)> guildWallet = [];
    readonly List<GuildInfoSmall> allies = [];

    public Task<string> JoinGuildAsync(Guid character, long guild, string password, string comment, int maxMembers, CancellationToken ct)
    {
        if (GuildRow?.Members.Any(m => m.CharacterId == character) == true) return Task.FromResult("member");
        joinSent.Add(new GuildJoinRequestSent(guild, $"G{guild}", comment, DateTimeOffset.UtcNow));
        return Task.FromResult("pending");
    }

    public Task<IReadOnlyList<GuildJoinRequestSent>> GuildJoinRequestsSentAsync(Guid character, CancellationToken ct) => Task.FromResult<IReadOnlyList<GuildJoinRequestSent>>(joinSent);
    public Task<IReadOnlyList<GuildJoinRequestRecv>> GuildJoinRequestsRecvAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GuildJoinRequestRecv>>(joinRecv.Select(j => j.Recv).ToList());
    public Task<string> ConfirmGuildJoinAsync(Guid character, string nickname, bool accept, int maxMembers, CancellationToken ct) => Task.FromResult("none");
    public Task<string> CancelGuildJoinRequestAsync(Guid character, long guildId, CancellationToken ct)
    {
        joinSent.RemoveAll(j => j.GuildId == guildId);
        return Task.FromResult("ok");
    }
    public Task<string> SetGuildNoticeAsync(Guid character, string notice, CancellationToken ct)
    {
        if (GuildRow is null) return Task.FromResult("none");
        GuildRow = GuildRow with { Notice = notice };
        return Task.FromResult("ok");
    }
    public Task<string> SetGuildMemberGradeAsync(Guid character, string nickname, int grade, CancellationToken ct) => Task.FromResult("none");
    public Task<string> SetGuildAuthorityAsync(Guid character, IReadOnlyList<(byte Grade, int Bits)> authority, CancellationToken ct) => Task.FromResult("none");
    public Task<string> DelegateGuildMasterAsync(Guid character, string nickname, CancellationToken ct) => Task.FromResult("none");
    public Task<(string Result, int Today, int Yesterday, bool CanToday, bool CanReward)> GuildAttendanceInfoAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(("ok", todayAttendance, 0, true, false));
    public Task<string> MarkGuildAttendanceAsync(Guid character, CancellationToken ct)
    {
        todayAttendance++;
        if (GuildRow is not null) GuildRow = GuildRow with { TodayAttendance = todayAttendance };
        return Task.FromResult("ok");
    }
    public Task<(string Result, int YesterdayCount)> TakeGuildAttendanceRewardAsync(Guid character, CancellationToken ct) => Task.FromResult(("ok", 0));
    public Task<(string Result, long Revision, int ExtensionCount, IReadOnlyList<GuildStorageItem> Items)> GuildStorageInfoAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(("ok", storageRevision, storageExtensionCount, (IReadOnlyList<GuildStorageItem>)guildStorage));
    public Task<string> StoreGuildStorageAsync(Guid character, IReadOnlyList<(Guid Item, long Revision, int Count)> items, CancellationToken ct) => Task.FromResult("none");
    public Task<string> TakeGuildStorageAsync(Guid character, IReadOnlyList<(long ItemKey, int Count)> items, CancellationToken ct) => Task.FromResult("none");
    public Task<(string Result, long LastHistoryMs, IReadOnlyList<GuildStorageHistoryRow> Rows)> GuildStorageHistoryAsync(Guid character, long afterMs, CancellationToken ct) =>
        Task.FromResult(("ok", storageHistory.Count > 0 ? storageHistory[^1].RecordedAtMs : afterMs, (IReadOnlyList<GuildStorageHistoryRow>)storageHistory));
    public Task<(string Result, long LastSortMs)> SortGuildStorageAsync(Guid character, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 }) return Task.FromResult(("authority", 0L));
        guildStorage.Sort((a, b) => a.TemplateId.CompareTo(b.TemplateId));
        for (var i = 0; i < guildStorage.Count; i++) guildStorage[i] = guildStorage[i] with { SlotPos = i };
        storageLastSortMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (GuildRow is not null) GuildRow = GuildRow with { StorageLastSortMs = storageLastSortMs };
        return Task.FromResult(("ok", storageLastSortMs));
    }
    public Task<(string Result, int ExtensionCount)> ExtendGuildStorageAsync(Guid character, int expectedExtension, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult(("authority", storageExtensionCount));
        if (expectedExtension != storageExtensionCount) return Task.FromResult(("count", storageExtensionCount));
        var kina = guildWallet.FirstOrDefault(w => w.Type == GuildOpcodes.WalletKinaType).Amount;
        if (kina < GuildTables.Embedded.StorageExtendKina) return Task.FromResult(("kina", storageExtensionCount));
        SetWallet(GuildOpcodes.WalletKinaType, kina - GuildTables.Embedded.StorageExtendKina);
        storageExtensionCount++;
        if (GuildRow is not null) GuildRow = GuildRow with { StorageExtensionCount = storageExtensionCount };
        return Task.FromResult(("ok", storageExtensionCount));
    }
    public Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> DonateGuildWalletAsync(Guid character, long kina,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        if (GuildRow is null) return Task.FromResult(("none", (IReadOnlyList<(int, long)>)[]));
        foreach (var (cost, revision, count) in costs)
            if (!Consume(cost, revision, count)) return Task.FromResult(("kina", (IReadOnlyList<(int, long)>)guildWallet));
        var have = guildWallet.FirstOrDefault(w => w.Type == GuildOpcodes.WalletKinaType).Amount;
        SetWallet(GuildOpcodes.WalletKinaType, have + kina);
        storageHistory.Add(new GuildStorageHistoryRow("Hero", 3, GameSession.KinaItemId, kina, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return Task.FromResult(("ok", (IReadOnlyList<(int, long)>)guildWallet));
    }
    public Task<(string Result, IReadOnlyList<(int Type, long Amount)> Wallet)> WithdrawGuildWalletAsync(Guid character, long kina, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 }) return Task.FromResult(("authority", (IReadOnlyList<(int, long)>)[]));
        var have = guildWallet.FirstOrDefault(w => w.Type == GuildOpcodes.WalletKinaType).Amount;
        if (have < kina) return Task.FromResult(("kina", (IReadOnlyList<(int, long)>)guildWallet));
        SetWallet(GuildOpcodes.WalletKinaType, have - kina);
        Items.Add(BagItem(GameSession.KinaItemId, kina, 9000 + Items.Count));
        return Task.FromResult(("ok", (IReadOnlyList<(int, long)>)guildWallet));
    }
    void SetWallet(int type, long amount)
    {
        guildWallet.RemoveAll(w => w.Type == type);
        if (amount > 0) guildWallet.Add((type, amount));
        if (GuildRow is not null) GuildRow = GuildRow with { Wallet = guildWallet.ToList() };
    }
    public Task<(string Result, long UnionId, string Name)> CreateGuildUnionAsync(Guid character, string name, int joinRule, string password, int emblem, string intro, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult(("authority", 0L, ""));
        if (unionId != 0) return Task.FromResult(("union", 0L, ""));
        unionId = 42;
        var small = new GuildInfoSmall(GuildRow.Id, GuildRow.Name, GuildRow.Level, GuildRow.Emblem, "Hero", GuildRow.Members.Count, unionId, emblem, name, 0, 0);
        unionView = new GuildUnionView(unionId, name, joinRule, password, emblem, intro, "", GuildRow.Id, [small]);
        GuildRow = GuildRow with { UnionId = unionId, Union = unionView };
        return Task.FromResult(("ok", unionId, name));
    }
    public Task<(string Result, GuildUnionView? Union)> GuildUnionInfoAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(unionView is null ? ("none", null) : ("ok", unionView));
    public Task<string> DisbandGuildUnionAsync(Guid character, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult("authority");
        unionId = 0; unionView = null;
        if (GuildRow is not null) GuildRow = GuildRow with { UnionId = 0, Union = null };
        return Task.FromResult("ok");
    }
    public Task<(string Result, int JoinResult)> JoinGuildUnionAsync(Guid character, long unionId, string password, CancellationToken ct) => Task.FromResult(("none", 0));
    public Task<string> LeaveGuildUnionAsync(Guid character, CancellationToken ct) => Task.FromResult("none");
    public Task<string> RequestGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, int relation, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult("authority");
        allies.Add(new GuildInfoSmall(99, targetName, 1, 0, "Other", 5, 0, 0, "", 0, 0));
        if (GuildRow is not null) GuildRow = GuildRow with { SendAllyRequests = allies.ToList() };
        return Task.FromResult("ok");
    }
    public Task<string> ConfirmGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, bool accept, CancellationToken ct) => Task.FromResult("ok");
    public Task<string> CancelGuildRelationAsync(Guid character, int targetType, string targetName, int serverId, CancellationToken ct) => Task.FromResult("ok");
    public Task<string> GrantGuildAgitDecoAsync(Guid character, Guid item, long revision, int templateId, CancellationToken ct)
    {
        if (!Consume(item, revision, 1)) return Task.FromResult("failed");
        ownedDecos.Add(templateId);
        if (GuildRow is not null) GuildRow = GuildRow with { OwnedAgitDecos = ownedDecos.ToList() };
        return Task.FromResult("ok");
    }
    public Task<string> LeaveGuildAsync(Guid character, CancellationToken ct) => Task.FromResult("none");
    public Task<string> KickGuildMemberAsync(Guid character, string nickname, CancellationToken ct) => Task.FromResult("none");
    public Task<string> DisbandGuildAsync(Guid character, CancellationToken ct) => Task.FromResult("none");
    public Task<int> GuildDonationRemainAsync(Guid character, CancellationToken ct)
    {
        var m = GuildRow?.Members.FirstOrDefault(x => x.CharacterId == character);
        return Task.FromResult(m is null ? 0 : Math.Max(0, GuildTables.Embedded.DonationsPerDay - m.DonationsToday));
    }
    public Task<(string Result, int Remain, int? NewGuildLevel)> DonateGuildAsync(Guid character, int donationLevel,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, CancellationToken ct)
    {
        if (GuildRow is null) return Task.FromResult(("none", 0, (int?)null));
        if (GuildTables.Embedded.Donation(donationLevel) is not { } tier) return Task.FromResult(("level", 0, (int?)null));
        var member = GuildRow.Members.FirstOrDefault(m => m.CharacterId == character);
        if (member is null) return Task.FromResult(("none", 0, (int?)null));
        if (member.DonationsToday >= GuildTables.Embedded.DonationsPerDay) return Task.FromResult(("limit", 0, (int?)null));
        foreach (var (cost, revision, count) in costs)
            if (!Consume(cost, revision, count)) return Task.FromResult(("kina", 0, (int?)null));
        var beforeLevel = GuildTables.Embedded.LevelFromExp(GuildRow.Exp).Level;
        var newExp = GuildRow.Exp + tier.GuildExp;
        var newLevel = GuildTables.Embedded.LevelFromExp(newExp).Level;
        var members = GuildRow.Members.Select(m => m.CharacterId == character
            ? m with { Reputation = m.Reputation + tier.Reputation, DonationReputation = m.DonationReputation + tier.Reputation, DonationsToday = m.DonationsToday + 1 }
            : m).ToList();
        GuildRow = GuildRow with { Exp = newExp, Level = newLevel, Members = members };
        var remain = GuildTables.Embedded.DonationsPerDay - member.DonationsToday - 1;
        return Task.FromResult(("ok", remain, newLevel > beforeLevel ? newLevel : (int?)null));
    }
    public Task<(string Result, int AgitId, long EndMs)> ContractGuildAgitAsync(Guid character, int agitId,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long nowMs, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult(("authority", 0, 0L));
        if (GuildTables.Embedded.Agit(agitId) is null) return Task.FromResult(("agit", 0, 0L));
        foreach (var (cost, revision, count) in costs)
            if (!Consume(cost, revision, count)) return Task.FromResult(("kina", 0, 0L));
        var end = Math.Max(GuildRow.AgitEndMs, nowMs) + GuildTables.Embedded.AgitRentMs;
        GuildRow = GuildRow with { AgitId = agitId, AgitEndMs = end };
        return Task.FromResult(("ok", agitId, end));
    }
    public Task<string> EquipGuildAgitDecoAsync(Guid character, int decoId, int slotId, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 }) return Task.FromResult("authority");
        if (!ownedDecos.Contains(decoId) && GuildRow.OwnedAgitDecos?.Contains(decoId) != true) return Task.FromResult("missing");
        var list = GuildRow.AgitDecos?.ToList() ?? [];
        list.RemoveAll(d => d.DecoId == decoId);
        list.Add(new GuildAgitDeco(decoId, slotId));
        GuildRow = GuildRow with { AgitDecos = list };
        return Task.FromResult("ok");
    }
    public Task<string> UnequipGuildAgitDecoAsync(Guid character, int decoId, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 }) return Task.FromResult("authority");
        if (GuildRow.AgitDecos?.Any(d => d.DecoId == decoId) != true) return Task.FromResult("missing");
        GuildRow = GuildRow with { AgitDecos = GuildRow.AgitDecos!.Where(d => d.DecoId != decoId).ToList() };
        return Task.FromResult("ok");
    }

    public Task<IReadOnlyDictionary<string, JsonElement>> CharacterSettingsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(Settings);

    public Task<bool> SaveCharacterSettingAsync(Guid character, string name, JsonElement value, CancellationToken ct)
    {
        Settings[name] = value.Clone();
        return Task.FromResult(true);
    }

    public Task<bool> MoveItemToContainerAsync(Guid character, Guid item, long revision, string location, int slot, CancellationToken ct)
    {
        var index = Items.FindIndex(i => i.InstanceId == item && i.Revision == revision && i.Location is "bag" or "storage" or "account_storage");
        if (index < 0 || slot < 0) return Task.FromResult(false);
        var (oldLocation, oldSlot) = (Items[index].Location, Items[index].Slot);
        for (var i = 0; i < Items.Count; i++)
            if (i != index && Items[i].Location == location && Items[i].Slot == slot)
                Items[i] = Items[i] with { Location = oldLocation, Slot = oldSlot, Revision = Items[i].Revision + 1 };
        Items[index] = Items[index] with { Location = location, Slot = slot, Revision = revision + 1 };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PostRecord>> PostsAsync(Guid character, CancellationToken ct)
    {
        var inbox = Inboxes.GetValueOrDefault(character) ?? [];
        var local = Posts.Where(p => p.Expire > DateTimeOffset.UtcNow);
        return Task.FromResult<IReadOnlyList<PostRecord>>(local.Concat(inbox).OrderByDescending(p => p.Id).ToList());
    }

    public Task<PostRecord?> SendPostAsync(Guid toCharacter, int dataId, string parameters, IReadOnlyList<(int TemplateId, long Count)> attachments, int tab, CancellationToken ct)
    {
        if (States.All(s => s.Summary.CharacterId != toCharacter)) return Task.FromResult<PostRecord?>(null);
        var post = new PostRecord(nextPostId++, 1, tab, dataId, parameters, false, false, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(TradeTables.Embedded.PlayerMailExpireDays), attachments, 0);
        Posts.Add(post);
        return Task.FromResult<PostRecord?>(post);
    }

    public Task<PostRecord?> SendPlayerPostAsync(Guid fromCharacter, Guid toCharacter, int dataId, string parameters,
        IReadOnlyList<(int TemplateId, long Count)> attachments, long codKina, long mailFeeKina, CancellationToken ct)
    {
        if (!TakeKina(this, mailFeeKina)) return Task.FromResult<PostRecord?>(null);
        foreach (var (template, quantity) in attachments)
        {
            var index = Items.FindIndex(i => i.Location == "bag" && i.TemplateId == template && i.Quantity >= quantity);
            if (index < 0) return Task.FromResult<PostRecord?>(null);
            var row = Items[index];
            if (row.Quantity == quantity) Items.RemoveAt(index);
            else Items[index] = row with { Quantity = row.Quantity - quantity, Revision = row.Revision + 1 };
        }
        var post = new PostRecord(nextPostId++, 1, attachments.Count > 0 ? 2 : 1, dataId, parameters, false, false, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(TradeTables.Embedded.PlayerMailExpireDays), attachments, 0, fromCharacter, codKina);
        if (!Inboxes.TryGetValue(toCharacter, out var box)) Inboxes[toCharacter] = box = [];
        box.Add(post);
        return Task.FromResult<PostRecord?>(post);
    }

    public Task<int> ReturnExpiredPostsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var inbox in Inboxes.Values.ToList())
        {
            for (var i = inbox.Count - 1; i >= 0; i--)
            {
                var post = inbox[i];
                if (post.Taken || post.Expire > now || post.SenderCharacterId is not { } sender) continue;
                if (post.Attachments.Count == 0 && post.CodKina <= 0) { inbox.RemoveAt(i); count++; continue; }
                if (post.CodKina > 0) Items.Add(BagItem(GameSession.KinaItemId, post.CodKina, Items.Count == 0 ? 5000 : Items.Max(x => x.WireId) + 1));
                var returned = post with { Id = nextPostId++, Params = $"return|{post.Params}", SenderCharacterId = null, CodKina = 0, Read = false, Taken = false };
                if (!Inboxes.TryGetValue(sender, out var senderBox)) Inboxes[sender] = senderBox = [];
                senderBox.Add(returned);
                inbox.RemoveAt(i);
                count++;
            }
        }
        return Task.FromResult(count);
    }

    public Task<bool> MarkPostsAsync(Guid character, IReadOnlyList<long> ids, bool delete, CancellationToken ct)
    {
        for (var i = Posts.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Posts[i].Id)) continue;
            if (delete) { if (Posts[i].Taken || Posts[i].Attachments.Count == 0) Posts.RemoveAt(i); }
            else Posts[i] = Posts[i] with { Read = true };
        }
        return Task.FromResult(true);
    }

    public Task<bool> TakePostAsync(Guid character, long id, CancellationToken ct)
    {
        if (Inboxes.TryGetValue(character, out var box))
        {
            var inboxIndex = box.FindIndex(p => p.Id == id && !p.Taken);
            if (inboxIndex >= 0)
            {
                var inboxPost = box[inboxIndex];
                if (inboxPost.CodKina > 0 && !TakeKina(this, inboxPost.CodKina)) return Task.FromResult(false);
                foreach (var (template, quantity) in inboxPost.Attachments)
                {
                    var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
                    Items.Add(BagItem(template, quantity, wire));
                }
                box[inboxIndex] = inboxPost with { Taken = true, Read = true };
                return Task.FromResult(true);
            }
        }
        var index = Posts.FindIndex(p => p.Id == id && !p.Taken);
        if (index < 0) return Task.FromResult(false);
        var post = Posts[index];
        if (post.CodKina > 0 && !TakeKina(this, post.CodKina)) return Task.FromResult(false);
        foreach (var (template, quantity) in post.Attachments)
        {
            var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
            Items.Add(BagItem(template, quantity, wire));
        }
        Posts[index] = post with { Taken = true, Read = true };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PushRewardRecord>> PushRewardsAsync(Guid character, long nowMs, CancellationToken ct)
    {
        if (!PushRewardsTaken.TryGetValue(character, out var taken)) taken = [];
        var list = PushRewardCatalog.Where(r => r.ExpireMs > nowMs && !taken.Contains(r.Id)).ToList();
        return Task.FromResult<IReadOnlyList<PushRewardRecord>>(list);
    }

    public Task<PushRewardRecord?> AddPushRewardAsync(byte type, string sender, string title, string body, int itemTemplateId, long itemCount, long expireMs, CancellationToken ct)
    {
        var sent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = new PushRewardRecord(type, nextPushRewardId++, sender, title, body, itemTemplateId, itemCount, sent, expireMs);
        PushRewardCatalog.Add(row);
        return Task.FromResult<PushRewardRecord?>(row);
    }

    public Task<PushRewardRecord?> TakePushRewardAsync(Guid character, byte type, int id, byte targetSequence, CancellationToken ct)
    {
        if (targetSequence != 1) return Task.FromResult<PushRewardRecord?>(null);
        var reward = PushRewardCatalog.FirstOrDefault(r => r.Id == id && r.Type == type && r.ExpireMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (reward is null) return Task.FromResult<PushRewardRecord?>(null);
        if (!PushRewardsTaken.TryGetValue(character, out var taken)) PushRewardsTaken[character] = taken = [];
        if (!taken.Add(id)) return Task.FromResult<PushRewardRecord?>(null);
        var wire = Items.Count == 0 ? 5000 : Items.Max(i => i.WireId) + 1;
        Items.Add(BagItem(reward.ItemTemplateId, reward.ItemCount, wire));
        return Task.FromResult<PushRewardRecord?>(reward);
    }

    public Task<TradeSummaryRecord> TradeSummaryAsync(Guid character, int tradeServerId, CancellationToken ct)
    {
        var now = EconomyNowMs();
        var active = TradeListings.Count(l => l.SellerCharacterId == character && l.ExpiresAtMs > now);
        var pending = TradeDeliveries.Count(d => d.SellerCharacterId == character && !d.Completed);
        var earnings = TradeDeliveries.Where(d => d.SellerCharacterId == character && !d.Completed).Sum(d => d.Kina);
        return Task.FromResult(new TradeSummaryRecord(active, pending, (int)Math.Min(earnings, int.MaxValue)));
    }

    public Task<(string Result, TradeListingRecord? Listing)> TradeListItemAsync(Guid character, long itemWireKey, long count, long priceKina, int tradeServerId, long nowMs, CancellationToken ct)
    {
        var cfg = TradeTables.Embedded;
        if (TradeListings.Count(l => l.SellerCharacterId == character && l.ExpiresAtMs > nowMs) >= cfg.MaxActiveListings)
            return Task.FromResult<(string, TradeListingRecord?)>(("limit", null));
        var index = Items.FindIndex(i => i.WireId == itemWireKey && i.Location == "bag" && i.Quantity >= count);
        if (index < 0) return Task.FromResult<(string, TradeListingRecord?)>(("item", null));
        var fee = cfg.ListingFee(priceKina);
        if (!TakeKina(this, fee)) return Task.FromResult<(string, TradeListingRecord?)>(("kina", null));
        var row = Items[index];
        Guid escrowId;
        int template = row.TemplateId;
        if (row.Quantity == count)
        {
            Items[index] = row with { Location = "trade_escrow", Slot = null, Revision = row.Revision + 1 };
            escrowId = Items[index].InstanceId;
        }
        else
        {
            Items[index] = row with { Quantity = row.Quantity - count, Revision = row.Revision + 1 };
            var split = row with { InstanceId = Guid.NewGuid(), Quantity = count, Location = "trade_escrow", Slot = null, WireId = Items.Max(i => i.WireId) + 1 };
            Items.Add(split);
            escrowId = split.InstanceId;
        }
        var listing = new TradeListingRecord(nextSaleId++, character, escrowId, itemWireKey, template, count, priceKina, nowMs, nowMs + cfg.ListingDurationMs, tradeServerId);
        TradeListings.Add(listing);
        BrokerListings[listing.SaleId] = (this, listing);
        return Task.FromResult<(string, TradeListingRecord?)>(("ok", listing));
    }

    public Task<(string Result, long DeliveryId)> TradePurchaseAsync(Guid buyerCharacter, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct)
    {
        if (!BrokerListings.TryGetValue(saleId, out var entry)) return Task.FromResult<(string, long)>(("missing", 0L));
        var (sellerStore, listing) = entry;
        if (listing.TemplateId != templateId || listing.Count != count) return Task.FromResult<(string, long)>(("missing", 0L));
        if (listing.ExpiresAtMs <= EconomyNowMs()) return Task.FromResult<(string, long)>(("expired", 0L));
        if (listing.SellerCharacterId == buyerCharacter) return Task.FromResult<(string, long)>(("self", 0L));
        if (!TakeKina(this, listing.PriceKina)) return Task.FromResult<(string, long)>(("kina", 0L));
        var escrowIndex = sellerStore.Items.FindIndex(i => i.InstanceId == listing.ItemInstanceId && i.Location == "trade_escrow");
        if (escrowIndex < 0) return Task.FromResult<(string, long)>(("escrow", 0L));
        var item = sellerStore.Items[escrowIndex];
        sellerStore.Items.RemoveAt(escrowIndex);
        Items.Add(item with { Location = "bag", Slot = null, Revision = item.Revision + 1 });
        BrokerListings.Remove(saleId);
        sellerStore.TradeListings.RemoveAll(l => l.SaleId == saleId);
        var proceeds = TradeTables.Embedded.SellerProceeds(listing.PriceKina);
        var deliveryId = nextDeliveryId++;
        sellerStore.TradeDeliveries.Add(new TradeDeliveryRecord(deliveryId, listing.SellerCharacterId, proceeds, templateId, count, false));
        return Task.FromResult(("ok", deliveryId));
    }

    public Task<(string Result, TradeListingRecord? Listing)> TradeCancelListingAsync(Guid character, long saleId, int templateId, long count, int tradeServerId, CancellationToken ct)
    {
        if (!BrokerListings.TryGetValue(saleId, out var entry) || entry.Listing.SellerCharacterId != character)
            return Task.FromResult<(string, TradeListingRecord?)>(("missing", null));
        var (sellerStore, listing) = entry;
        var escrowIndex = sellerStore.Items.FindIndex(i => i.InstanceId == listing.ItemInstanceId);
        if (escrowIndex >= 0) sellerStore.Items[escrowIndex] = sellerStore.Items[escrowIndex] with { Location = "bag", Revision = sellerStore.Items[escrowIndex].Revision + 1 };
        sellerStore.TradeListings.RemoveAll(l => l.SaleId == saleId);
        BrokerListings.Remove(saleId);
        return Task.FromResult<(string, TradeListingRecord?)>(("ok", listing));
    }

    public Task<IReadOnlyList<TradeDeliveryRecord>> TradeDeliveriesAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TradeDeliveryRecord>>(TradeDeliveries.Where(d => d.SellerCharacterId == character && !d.Completed).ToList());

    public Task<bool> TradeCompleteDeliveryAsync(Guid character, long deliveryId, int tradeServerId, CancellationToken ct)
    {
        var index = TradeDeliveries.FindIndex(d => d.DeliveryId == deliveryId && d.SellerCharacterId == character && !d.Completed);
        if (index < 0) return Task.FromResult(false);
        GrantKina(this, TradeDeliveries[index].Kina);
        TradeDeliveries[index] = TradeDeliveries[index] with { Completed = true };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<SkillState>> SkillsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillState>>(Skills);

    public Task<bool> SetSkillLevelAsync(Guid character, int skillId, int level, int purchased, CancellationToken ct)
    {
        var index = Skills.FindIndex(s => s.SkillId == skillId);
        if (index >= 0) Skills[index] = Skills[index] with { Level = level, Purchased = purchased };
        else Skills.Add(new SkillState(skillId, level, null, purchased));
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
