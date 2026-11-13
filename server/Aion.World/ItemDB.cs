using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Aion.World;

/// <summary>A stat line of an item: a fixed value (MainStats) or a random roll (SubStats).</summary>
public sealed record ItemStat(string Name, int Value);
public sealed record ItemRandomStat(string Name, int Min, int Max, int Weight);
/// <summary>What using (double-clicking) the item does: the client's EItemUseEffect and its values.</summary>
public sealed record ItemUseAction(string Effect, IReadOnlyList<string> Values);

/// <summary>One record of gamedata/items/*.xml (tools/item_xml.py from the AION2 client item table).</summary>
public sealed record ItemRecord
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string InternalName { get; init; }
    /// <summary>Equip, Usable, Misc or Currency.</summary>
    public required string Type { get; init; }
    public required string Category { get; init; }
    public required string Grade { get; init; }
    public required string Tier { get; init; }
    public required string Race { get; init; }
    public int Level { get; init; }
    public int PermitLevelMin { get; init; }
    public int PermitLevelMax { get; init; }
    /// <summary>Client MaxStackCount (0 and 1: not stackable).</summary>
    public int MaxStack { get; init; }
    public long SellPrice { get; init; }
    /// <summary>Exchangeable, NonExchangeable, SoulBind, SoulBind_Acquire.</summary>
    public required string Exchange { get; init; }
    public bool CanSell { get; init; }
    public bool CanStorage { get; init; }
    public bool CanGuildStorage { get; init; }
    public bool CanCharacterStorage { get; init; }
    public bool CanDelete { get; init; }
    public bool CanLock { get; init; }
    public bool CanDecompose { get; init; }
    public int MaintainMinutes { get; init; }
    public long DecomposeCost { get; init; }
    public IReadOnlyList<string> DecomposeResults { get; init; } = [];
    public string? EquipCategory { get; init; }
    public string? ArmorType { get; init; }
    public int BasicLevel { get; init; }
    public bool CanEnchant { get; init; }
    public string? EnchantGroup { get; init; }
    public string? ExceedEnchantGroup { get; init; }
    public string? SoulbindCost { get; init; }
    public int SoulbindRandomStats { get; init; }
    public int MagicstoneSlots { get; init; }
    /// <summary>The Enhance Potential (Surpass) group (Item.dat SurpassGroup), null when the item cannot be surpassed.</summary>
    public string? SurpassGroup { get; init; }
    /// <summary>Kina to socket a manastone (Item.dat ImprintMagicStoneCostGold).</summary>
    public long ImprintCost { get; init; }
    public int GodstoneSlots { get; init; }
    public IReadOnlyList<ItemStat> Stats { get; init; } = [];
    public IReadOnlyList<ItemRandomStat> RandomStats { get; init; } = [];
    /// <summary>The appearances (skin names, skins.json keys) the item can give (client ExtractionSkins).</summary>
    public IReadOnlyList<string> Skins { get; init; } = [];
    /// <summary>The chance (1/10000) that extracting the item's appearance gives its skin (Item.dat ExtractionSkinRate).</summary>
    public int SkinExtractionRate { get; init; }
    public string? UsableCategory { get; init; }
    public int MaxUseCount { get; init; }
    public bool AutoUseWhenAcquired { get; init; }
    public int Cooltime { get; init; }
    public string? CooltimeGroup { get; init; }
    public int CastingTime { get; init; }
    public ItemUseAction? Use { get; init; }
    public string? MiscCategory { get; init; }
    /// <summary>A currency's ECurrencyCategory (Gold, GoldNonbinding, AbyssPoint, ...; ItemSortOrder puts currencies in its order).</summary>
    public string? CurrencyCategory { get; init; }
    /// <summary>The file the record came from (weapons, armors, accessories, arcana, consumables, quest_items, materials, currencies).</summary>
    public required string Group { get; init; }

    public bool Tradable => Exchange == "Exchangeable";
    public bool Stackable => MaxStack > 1;
}

/// <summary>
/// The item database (the old official server's ItemDB idea): gamedata/items/*.xml, embedded in the build, validated once
/// against gamedata/items.xsd and turned into an id-keyed dictionary at start-up; lookups never parse or touch storage.
/// </summary>
public sealed class ItemDB
{
    public static readonly string[] Groups = ["weapons", "armors", "accessories", "arcana", "consumables", "quest_items", "materials", "currencies"];

    readonly Dictionary<int, ItemRecord> byId;
    readonly Dictionary<string, List<ItemRecord>> byEffect;
    ItemDB(Dictionary<int, ItemRecord> items)
    {
        byId = items;
        byEffect = items.Values.Where(i => i.Use is not null).GroupBy(i => i.Use!.Effect).ToDictionary(g => g.Key, g => g.ToList());
    }

    static readonly Lazy<ItemDB> embedded = new(() => Load(name => typeof(ItemDB).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing embedded game data {name}")));

    public static ItemDB Embedded => embedded.Value;

    /// <summary>The raw &lt;item&gt; elements of the embedded XML by id (staff item info window; loaded on first use).</summary>
    static readonly Lazy<Dictionary<int, XElement>> raw = new(() =>
    {
        var all = new Dictionary<int, XElement>();
        foreach (var group in Groups)
        {
            using var stream = typeof(ItemDB).Assembly.GetManifestResourceStream($"items/{group}.xml");
            if (stream is null) continue;
            foreach (var e in XDocument.Load(stream).Root!.Elements("item"))
                if (int.TryParse((string?)e.Element("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) all.TryAdd(id, e);
        }
        return all;
    });

    public static XElement? RawOf(int id) => raw.Value.GetValueOrDefault(id);

    public int Count => byId.Count;
    public ItemRecord? Of(int id) => byId.GetValueOrDefault(id);
    public IEnumerable<ItemRecord> All => byId.Values;
    public IReadOnlyList<ItemRecord> WithEffect(string effect) => byEffect.GetValueOrDefault(effect) ?? [];
    public IEnumerable<string> Effects => byEffect.Keys;

    /// <summary>Loads and validates every group file (streams by resource name: items.xsd, items/&lt;group&gt;.xml).</summary>
    public static ItemDB Load(Func<string, Stream> open)
    {
        var schemas = new XmlSchemaSet();
        using (var xsd = open("items.xsd")) schemas.Add(null, XmlReader.Create(xsd));
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        var errors = new List<string>();
        settings.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        var items = new Dictionary<int, ItemRecord>();
        foreach (var group in Groups)
        {
            using var stream = open($"items/{group}.xml");
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (errors.Count > 0) throw new InvalidDataException($"items/{group}.xml does not match items.xsd: {string.Join("; ", errors.Take(5))}");
            foreach (var element in document.Root!.Elements("item"))
            {
                var record = Parse(element, group);
                if (!items.TryAdd(record.Id, record)) throw new InvalidDataException($"item {record.Id} is listed twice (items/{group}.xml)");
            }
        }
        return new ItemDB(items);
    }

    static ItemRecord Parse(XElement e, string group)
    {
        string S(string name) => (string?)e.Element(name) ?? throw new InvalidDataException($"item without {name}");
        string? O(string name) => (string?)e.Element(name);
        int I(string name) => e.Element(name) is { } x ? int.Parse(x.Value, CultureInfo.InvariantCulture) : 0;
        long L(string name) => e.Element(name) is { } x ? long.Parse(x.Value, CultureInfo.InvariantCulture) : 0;
        bool B(string name) => e.Element(name) is { } x && XmlConvert.ToBoolean(x.Value);
        int A(XElement x, string name) => int.Parse((string)x.Attribute(name)!, CultureInfo.InvariantCulture);
        return new ItemRecord
        {
            Id = I("id"), Name = S("name"), InternalName = S("internal_name"), Type = S("type"), Category = S("category"),
            Grade = S("grade"), Tier = S("tier"), Race = S("race"), Level = I("level"), PermitLevelMin = I("permit_level_min"),
            PermitLevelMax = I("permit_level_max"), MaxStack = I("max_stack"), SellPrice = L("sell_price"), Exchange = S("exchange"),
            CanSell = B("can_sell"), CanStorage = B("can_storage"), CanGuildStorage = B("can_guild_storage"),
            CanCharacterStorage = B("can_character_storage"), CanDelete = B("can_delete"), CanLock = B("can_lock"),
            CanDecompose = B("can_decompose"), MaintainMinutes = I("maintain_minutes"), DecomposeCost = L("decompose_cost"),
            DecomposeResults = e.Elements("decompose_result").Select(x => x.Value).ToList(),
            EquipCategory = O("equip_category"), ArmorType = O("armor_type"), BasicLevel = I("basic_level"), CanEnchant = B("can_enchant"),
            EnchantGroup = O("enchant_group"), ExceedEnchantGroup = O("exceed_enchant_group"), SoulbindCost = O("soulbind_cost"),
            SoulbindRandomStats = I("soulbind_random_stats"), MagicstoneSlots = I("magicstone_slots"), SurpassGroup = O("surpass_group"), ImprintCost = L("imprint_cost"), GodstoneSlots = I("godstone_slots"),
            Stats = e.Elements("stat").Select(x => new ItemStat((string)x.Attribute("name")!, A(x, "value"))).ToList(),
            RandomStats = e.Elements("random_stat").Select(x => new ItemRandomStat((string)x.Attribute("name")!, A(x, "min"), A(x, "max"), A(x, "weight"))).ToList(),
            Skins = e.Elements("skin").Select(x => x.Value).ToList(), SkinExtractionRate = I("skin_extraction_rate"),
            UsableCategory = O("usable_category"), MaxUseCount = I("max_use_count"), AutoUseWhenAcquired = B("auto_use_when_acquired"),
            Cooltime = I("cooltime"), CooltimeGroup = O("cooltime_group"), CastingTime = I("casting_time"),
            Use = e.Element("use_action") is { } use ? new ItemUseAction((string)use.Attribute("effect")!, use.Elements("value").Select(v => v.Value).ToList()) : null,
            MiscCategory = O("misc_category"), CurrencyCategory = O("currency_category"), Group = group,
        };
    }
}
