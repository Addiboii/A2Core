using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// PROVISIONAL soul bind pools for items without their own random stats (gamedata/provisional/stat-pools.xml); manastones roll
/// the client's ImprintMagicStoneProb table (EnhanceTables).
/// </summary>
public sealed class StatPools
{
    public sealed record Roll(string Stat, int Min, int Max);
    readonly Dictionary<string, List<Roll>> soulbind;
    readonly Dictionary<string, double> gradeFactor;
    StatPools(Dictionary<string, List<Roll>> soulbind, Dictionary<string, double> grades) => (this.soulbind, gradeFactor) = (soulbind, grades);

    static readonly Lazy<StatPools> embedded = new(() =>
    {
        using var stream = typeof(StatPools).Assembly.GetManifestResourceStream("provisional/stat-pools.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/stat-pools.xml");
        return Parse(XDocument.Load(stream));
    });

    public static StatPools Embedded => embedded.Value;

    public static StatPools Parse(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("stat-pools: empty");
        List<Roll> Rolls(XElement e) => e.Elements("stat").Select(s => new Roll((string)s.Attribute("name")!,
            int.Parse((string)s.Attribute("min")!, CultureInfo.InvariantCulture), int.Parse((string)s.Attribute("max")!, CultureInfo.InvariantCulture))).ToList();
        var soul = root.Elements("soulbind").ToDictionary(e => (string)e.Attribute("kind")!, Rolls, StringComparer.OrdinalIgnoreCase);
        var grades = root.Element("grades")?.Attributes().ToDictionary(a => a.Name.LocalName, a => double.Parse(a.Value, CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var roll in soul.Values.SelectMany(r => r))
            if (StatIds.Of(roll.Stat) is null || roll.Min > roll.Max) throw new InvalidDataException($"stat-pools: bad stat {roll.Stat}");
        return new StatPools(soul, grades);
    }

    /// <summary>The gear kind of an item group file: weapon, armor or accessory (arcana counts as accessory).</summary>
    public static string KindOf(string group) => group switch { "weapons" => "weapon", "armors" => "armor", _ => "accessory" };

    /// <summary>count distinct stats for a soul bind of the item (its kind's pool, scaled by its grade).</summary>
    public List<(string Stat, int Value)> SoulBind(ItemRecord item, int count, Random rng)
    {
        // The item's own pool first (Item.dat random stats, gamedata/items <random_stat name min max weight>): count distinct
        // stats picked by weight, each value between min and max (live 2026-11-07: the provisional pool below was used for every
        // item, so a bound item never got its own stats such as STR or Justice).
        if (item.RandomStats.Count > 0)
        {
            var left = item.RandomStats.Where(r => StatIds.Of(r.Name) is not null && r.Max >= r.Min).ToList();
            var rolled = new List<(string, int)>();
            while (rolled.Count < count && left.Count > 0)
            {
                var total = left.Sum(r => Math.Max(1, r.Weight));
                var roll = rng.Next(total);
                var at = 0;
                while (roll >= Math.Max(1, left[at].Weight)) roll -= Math.Max(1, left[at++].Weight);
                var pick = left[at];
                left.RemoveAll(r => string.Equals(r.Name, pick.Name, StringComparison.OrdinalIgnoreCase));
                rolled.Add((pick.Name, rng.Next(pick.Min, pick.Max + 1)));
            }
            return rolled;
        }
        if (!soulbind.TryGetValue(KindOf(item.Group), out var pool) || pool.Count == 0) return [];
        var factor = gradeFactor.GetValueOrDefault(item.Grade, 1);
        var picked = new List<(string, int)>();
        foreach (var roll in pool.OrderBy(_ => rng.Next()).Take(Math.Min(count, pool.Count)))
            picked.Add((roll.Stat, (int)Math.Max(1, Math.Round(rng.Next(roll.Min, roll.Max + 1) * factor))));
        return picked;
    }

    /// <summary>The manastone group an item takes: accessories (and arcana) Magicstone_Group_Acc, weapons and armour _Equip.</summary>
    public static string StoneGroupOf(ItemRecord gear) => KindOf(gear.Group) == "accessory" ? "Magicstone_Group_Acc" : "Magicstone_Group_Equip";

    /// <summary>
    /// One slot's roll for a stone: the client's ImprintMagicStoneProb pool of the stone's ImprintGroupName (its use_action's
    /// second value, e.g. ms_10_common_normal): a stat by AddStatProb weight, then one of its StatInfos by StatProb, with that
    /// row's value. EItemMagicStoneGrade = the StatInfos position + 1 (1 green, 2 blue, 3 the third tier): the slot on the wire
    /// carries no value, so the client reads it back from the same row (CANDIDATE mapping; OBSERVED 115817: stone 531360001
    /// ms_10_common_normal gave grades 1 and 2, the only two tiers with a chance there). Null when the stone has no pool.
    /// </summary>
    public (string Stat, int Value, byte Grade)? Manastone(ItemRecord stone, Random rng)
    {
        if (stone.Use is not { Values.Count: >= 2 } use || EnhanceTables.Embedded.Imprint(use.Values[1]) is not { Count: > 0 } pool) return null;
        var stat = EnhanceTables.Pick(pool, s => s.Weight, rng);
        var grades = stat.Grades.Select((g, i) => (g.Value, g.Chance, Grade: (byte)(i + 1))).ToList();
        if (grades.Count == 0) return null;
        var pick = EnhanceTables.Pick(grades, g => g.Chance, rng);
        return (stat.Stat, pick.Value, pick.Grade);
    }
}

// Soul binding stats and manastones. Soul bind (0x562f -> 0x5630, OBSERVED layout "count, stats [{type u16, value i32}], skills"):
// the item gets its SoulbindRandomStatCount stats from the provisional pool, stored as upgrades.soul_bind_stats. Manastones
// (ItemImprint_RQ 0x5629 {equip uid, stone item id, stone uid} -> 0x562a {result, the echo, slot infos [{stone item id, stat
// i32, EItemMagicStoneGrade u8}]}, usmap; the client sent the request live): the stone is used up and the item's Kina cost
// paid, one stat rolled and stored as upgrades.magic_stones. CANDIDATE: the slot info's stat is the client's EStat id here.
// Both count in the stat sheet while the item is worn (UpgradeStat).
public sealed partial class GameSession
{
    public const ushort ItemImprintRq = 0x5629, ItemImprintRs = 0x562a;

    static List<(string Stat, int Value)> UpgradeList(ItemState row, string field) =>
        row.Upgrades.ValueKind == JsonValueKind.Object && row.Upgrades.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2)
                .Select(e => (e[e.GetArrayLength() >= 3 ? 1 : 0].GetString() ?? "", e[e.GetArrayLength() >= 3 ? 2 : 1].GetInt32())).ToList()
            : [];

    /// <summary>The worn items' soul bind stats and manastone stats for a stat name.</summary>
    int UpgradeStat(string stat) =>
        storedItems.Values.Where(i => i.Location == "equipment")
            .Sum(i => WornUpgrades(i).Where(s => string.Equals(s.Stat, stat, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value));

    IEnumerable<string> UpgradeStatNames() =>
        storedItems.Values.Where(i => i.Location == "equipment").SelectMany(i => WornUpgrades(i).Select(s => s.Stat));

    /// <summary>A worn item's soul bind stats, manastone stats and Enhance Potential stats.</summary>
    static IEnumerable<(string Stat, int Value)> WornUpgrades(ItemState i) =>
        UpgradeList(i, "soul_bind_stats").Concat(UpgradeList(i, "magic_stones")).Concat(SurpassStats(i)).Concat(UpgradeList(i, "soul_add_stats"));

    /// <summary>The soul bind's rolled stats on the wire: {EStat id u16, value i32} each (the OBSERVED form).</summary>
    static IReadOnlyList<(int Type, int Value)> WireStats(IEnumerable<(string Stat, int Value)> stats) =>
        stats.Select(s => (StatIds.Of(s.Stat) ?? 0, s.Value)).Where(s => s.Item1 != 0).ToList();

    /// <summary>Rolls and stores the soul bind stats of a freshly bound item; the stats for the reply (empty when the item has none).</summary>
    async Task<IReadOnlyList<(int Type, int Value)>> RollSoulBindStatsAsync(INativeCharacterStore store, Guid character, ItemState row, CancellationToken ct)
    {
        if (ItemDB.Embedded.Of(row.TemplateId) is not { SoulbindRandomStats: > 0 } item) return [];
        var stats = StatPools.Embedded.SoulBind(item, item.SoulbindRandomStats, rng);
        if (stats.Count == 0) return [];
        var json = JsonSerializer.Serialize(stats.Select(s => new object[] { s.Stat, s.Value }));
        var saved = await Stored(async () => await store.SetItemUpgradeAsync(character, row.InstanceId, "soul_bind_stats", json, ct) ? [[]] : []);
        return saved.Count == 0 ? [] : WireStats(stats);
    }

    async Task<List<byte[]>> ImprintAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 22) return [];
        long equipKey = BitConverter.ToInt64(plain, 2), stoneKey = BitConverter.ToInt64(plain, 14);
        var stoneId = BitConverter.ToInt32(plain, 10);
        List<MagicStoneSlot> slots = [];
        var slotCount = 0;
        // OBSERVED reply (capture 115817): result u16, equip uid, stone id, stone uid, the item's slots (7 bytes each, as in the item record).
        byte[] Reply(int result) =>
            new EquipDetail(0, 0, Stones: slots, StoneSlots: slotCount).WriteStones(PacketWriter.Opcode(ItemImprintRs).U16(result).I64(equipKey).I32(stoneId).I64(stoneKey)).ToArray();
        if (Store is not { } store || CharacterId is not { } character
            || !storedItems.TryGetValue(equipKey, out var gear) || gear.Location is not ("bag" or "equipment")
            || !storedItems.TryGetValue(stoneKey, out var stone) || stone.Location != "bag" || stone.TemplateId != stoneId)
            return [Reply(EResult.Item_Imprint_Fail)];
        if (ItemDB.Embedded.Of(gear.TemplateId) is not { Type: "Equip", MagicstoneSlots: > 0 } gearInfo) return [Reply(EResult.Item_Imprint_NotImprintable)];
        if (ItemDB.Embedded.Of(stone.TemplateId) is not { Use.Effect: "ImprintMagicStone" } stoneInfo
            || stoneInfo.Use.Values.Count >= 3 && !string.Equals(stoneInfo.Use.Values[2], StatPools.StoneGroupOf(gearInfo), StringComparison.OrdinalIgnoreCase))
            return [Reply(EResult.Item_Imprint_InvalidMagicStoneItem)];
        // One engrave fills every slot of the item, each with its own roll (user, official 2026-11-07; the official reply listed
        // 3 slots of one stone id); engraving again replaces them all.
        var rolls = new List<(string Stat, int Value, byte Grade)>();
        for (var n = 0; n < gearInfo.MagicstoneSlots; n++)
            if (StatPools.Embedded.Manastone(stoneInfo, rng) is { } roll) rolls.Add(roll);
        if (rolls.Count == 0) return [Reply(EResult.Item_Imprint_Fail)];
        // The socketing cost (Item.dat ImprintMagicStoneCostGold, Kina).
        var cost = gearInfo.ImprintCost;
        var kina = cost > 0 ? CostStacks(KinaItemId, cost) : [];
        if (kina is null) return [Reply(EResult.Item_Imprint_Fail)];
        var entries = rolls.Select(r => new object[] { stoneId, r.Stat, r.Value, (int)r.Grade }).ToList();
        var done = await Stored(async () =>
        {
            foreach (var (item, revision, count) in kina) if (!await store.ConsumeItemAsync(character, item, revision, count, ct)) return [];
            if (!await store.ConsumeItemAsync(character, stone.InstanceId, stone.Revision, 1, ct)) return [];
            return await store.SetItemUpgradeAsync(character, gear.InstanceId, "magic_stones", JsonSerializer.Serialize(entries), ct) ? [[]] : [];
        });
        if (done.Count == 0) return [Reply(EResult.Item_Imprint_Internal_Error)];
        slots = entries.Select(e => new MagicStoneSlot(Convert.ToInt32(e[0], CultureInfo.InvariantCulture), StatIds.Of((string)e[1]) ?? 0, (byte)(int)e[3])).ToList();
        slotCount = gearInfo.MagicstoneSlots;
        return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. RefreshStats()];
    }

    /// <summary>A socketed stone as stored in upgrades.magic_stones: [stone id, stat, value, colour] (older entries lack the id or colour).</summary>
    public readonly record struct StoredStone(int ItemId, string Stat, int Value, byte Grade);

    /// <summary>The item's socketed stones. Older entries without a colour count as green (Low).</summary>
    static List<StoredStone> StoneSlots(ItemState gear) =>
        gear.Upgrades.ValueKind == JsonValueKind.Object && gear.Upgrades.TryGetProperty("magic_stones", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2).Select(e => e.GetArrayLength() switch
            {
                2 => new StoredStone(0, e[0].GetString() ?? "", e[1].GetInt32(), 1),
                3 => new StoredStone(e[0].GetInt32(), e[1].GetString() ?? "", e[2].GetInt32(), 1),
                _ => new StoredStone(e[0].GetInt32(), e[1].GetString() ?? "", e[2].GetInt32(), (byte)e[3].GetInt32()),
            }).ToList()
            : [];

    /// <summary>The stones on the wire (Common_ItemMagicStoneSlotInfo: stone id, EStat id, colour).</summary>
    static IReadOnlyList<MagicStoneSlot> WireStones(ItemState gear) =>
        StoneSlots(gear).Select(s => new MagicStoneSlot(s.ItemId, StatIds.Of(s.Stat) ?? 0, s.Grade)).ToList();

}
