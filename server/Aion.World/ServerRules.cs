using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>Our own rules (gamedata/server-rules.xml, embedded): values staff may tune, validated once at load.</summary>
public sealed class ServerRules
{
    /// <summary>Chance in percent that a dismantled item also gives its skin (user rule: 25-30 %).</summary>
    public int DismantleSkinChancePercent { get; init; }
    /// <summary>Barrier objects are not placed inside instance copies (user rule: the player can always pass).</summary>
    public bool RemoveInstanceBarriers { get; init; }
    /// <summary>Every subscription benefit free for everyone (the start-up default of the live switch, user rule).</summary>
    public bool SubscriptionFree { get; init; } = true;
    /// <summary>A helper NPC's damage against monsters in percent (PROVISIONAL, user rule: the player does most of the work).</summary>
    public int HelperDamagePercent { get; init; } = 25;
    /// <summary>Skill points / stigma points available at most at a time; the rest stays saved (user rule 2026-11-07).</summary>
    public int SkillPointCap { get; init; } = 1000;
    public int StigmaPointCap { get; init; } = 1000;
    /// <summary>A chasing monster that has not hit its victim for this long gives up and goes home (N4, PROVISIONAL 10 s).</summary>
    public int LeashNoHitMs { get; init; } = 10_000;
    /// <summary>
    /// Chase distance (user rule 2026-11-08): a pulled monster follows its target ChaseMinCm..ChaseMaxCm from where the chase began
    /// (random per chase), ChaseAfterHitCm further once it has landed a hit; then it runs home ReturnSpeedPercent faster and resets.
    /// </summary>
    public int ChaseMinCm { get; init; } = 1000;
    public int ChaseMaxCm { get; init; } = 1500;
    public int ChaseAfterHitCm { get; init; } = 500;
    public int ReturnSpeedPercent { get; init; } = 50;
    /// <summary>A boss gives up after this long without a hit; a boss that lost its target waits this long before it resets.</summary>
    public int BossLeashNoHitMs { get; init; } = 30_000;
    public int BossLostGraceMs { get; init; } = 15_000;
    /// <summary>Headless bot players to run (BotService; 0 = none).</summary>
    public int BotCount { get; init; }
    /// <summary>Boss stun control and stagger (bosses element; PROVISIONAL).</summary>
    public int BossCcWindowMs { get; init; } = 15_000;
    public int BossCcMax { get; init; } = 3;
    public int BossCcImmuneMs { get; init; } = 10_000;
    public int BossStaggerPerCc { get; init; } = 25;
    public int BossStaggerDecayPerS { get; init; } = 5;
    public int BossStaggerMs { get; init; } = 5_000;
    public int BossStaggerImmuneMs { get; init; } = 20_000;
    public int BossStaggerDamagePercent { get; init; } = 50;
    /// <summary>Primary and Empyrean Lord stat conversions (OFFICIAL, client PcStatSecond): what one point of STR, DEX, Justice... gives, by target stat.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string From, double PerPoint)>> PrimaryStats { get; init; } =
        new Dictionary<string, IReadOnlyList<(string, double)>>();
    /// <summary>The running season's tag in the achievement group names (Group_Season_G1_L_Unlock): only its season groups are listed.</summary>
    public string CurrentSeason { get; init; } = "G1";
    /// <summary>Fall damage (PROVISIONAL): no damage up to FallSafeCm, FallPercentPerMetre of max HP per metre above, death from FallDeathCm.</summary>
    public int FallSafeCm { get; init; } = 1000;
    public double FallPercentPerMetre { get; init; } = 2;
    public int FallDeathCm { get; init; } = 6000;
    /// <summary>
    /// What a failed enchant does to items whose client Enchant row has FailPenalty -1 (the Clash Rune; enchant element, user
    /// question 2026-11-11): "safe" never fails, "keep" fails at the table chance but keeps the item and its level, "official"
    /// destroys it as the official server (836 Item_Enchant_Fail_Destruction). Every other item keeps its level on a failure.
    /// </summary>
    public string EnchantBreakable { get; init; } = EnchantOutcome.Safe;
    /// <summary>The Black Cloud market sold for Kina (premium_shop element; the staff panel can change it live, PremiumShop.Current).</summary>
    public PremiumShopRules PremiumShop { get; init; } = PremiumShopRules.Default;
    /// <summary>The Black Cloud Merchants (random_shop element; the staff panel can change it live, RandomShop.Current).</summary>
    public RandomShopRules RandomShop { get; init; } = RandomShopRules.Default;
    /// <summary>
    /// Essence Extraction (gather element): the mastery level at which the first Specialty skill point is given. OFFICIAL is 10
    /// (client GatherMastery: the first BonusSpecializedPoint is on the Level 10 row); 0 or 10 = official. A lower level (user
    /// request 2026-11-12: 6) gives that same point earlier; every later point and the total (14 at 100) stay official.
    /// </summary>
    public int GatherSpecializedFromLevel { get; init; }
    /// <summary>gather skin_bag_percent: the chance (0-100) a won gather adds its node's extra item (GatherSourceMaterial AddMaterialList,
    /// e.g. Poko's Skin Bundle) once. The client table has no chance; 3 official Od gathers gave none (USER 2026-11-12: rare, 5).</summary>
    public int GatherSkinBagPercent { get; init; } = 5;
    /// <summary>monster_ai boss_official: bosses (NpcTactics.IsBoss) fight only by client data and map events, none of our tactics
    /// (user 2026-11-13: "keep the bosses as official").</summary>
    public bool BossOfficialAi { get; init; } = true;
    /// <summary>monster_ai fun_monsters: every other monster fights by our tactics (NpcTactics, OURS; user 2026-11-13: "rest of
    /// monsters sounds fun"); false = as official.</summary>
    public bool FunMonsters { get; init; } = true;
    /// <summary>Spacetime Rifts and hidden cubes (rifts / hidden_cubes elements; RiftRules.cs): the world a rift leads to, the visit time,
    /// the cube respawn.</summary>
    public RiftRules Rifts { get; init; } = RiftRules.Default;

    static readonly Lazy<ServerRules> embedded = new(() =>
    {
        using var stream = typeof(ServerRules).Assembly.GetManifestResourceStream("server-rules.xml")
            ?? throw new InvalidOperationException("Missing embedded game data server-rules.xml");
        return Parse(XDocument.Load(stream));
    });

    public static ServerRules Embedded => embedded.Value;

    public static ServerRules Parse(XDocument document)
    {
        var dismantle = document.Root?.Element("dismantle") ?? throw new InvalidDataException("server-rules.xml: no <dismantle>");
        var chance = int.Parse((string?)dismantle.Attribute("skin_chance_percent") ?? throw new InvalidDataException("server-rules.xml: no skin_chance_percent"),
            CultureInfo.InvariantCulture);
        if (chance is < 0 or > 100) throw new InvalidDataException($"server-rules.xml: skin_chance_percent {chance} is not 0-100");
        var barriers = (string?)document.Root?.Element("instances")?.Attribute("remove_barriers") is { } flag && System.Xml.XmlConvert.ToBoolean(flag);
        var free = (string?)document.Root?.Element("subscription")?.Attribute("free") is not { } f || System.Xml.XmlConvert.ToBoolean(f);
        var helper = int.Parse((string?)document.Root?.Element("helpers")?.Attribute("damage_percent") ?? "25", CultureInfo.InvariantCulture);
        if (helper is < 0 or > 100) throw new InvalidDataException("server-rules.xml: helpers damage_percent must be 0-100");
        var points = document.Root?.Element("points");
        int Cap(string name) => int.Parse((string?)points?.Attribute(name) ?? "1000", CultureInfo.InvariantCulture) is var v && v is >= 0 and <= 30000
            ? v : throw new InvalidDataException($"server-rules.xml: points {name} must be 0-30000");
        int Monsters(string name, int fallback) =>
            int.Parse((string?)document.Root?.Element("monsters")?.Attribute(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) is var v
            && v is >= 0 and <= 100_000 ? v : throw new InvalidDataException($"server-rules.xml: monsters {name} must be 0-100000");
        int Bosses(string name, int fallback) =>
            int.Parse((string?)document.Root?.Element("bosses")?.Attribute(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) is var v
            && v is >= 0 and <= 1_000_000 ? v : throw new InvalidDataException($"server-rules.xml: bosses {name} must be 0-1000000");
        if (Monsters("chase_min_cm", 1000) > Monsters("chase_max_cm", 1500)) throw new InvalidDataException("server-rules.xml: monsters chase_min_cm is above chase_max_cm");
        var primary = (document.Root?.Element("primary_stats")?.Elements("convert") ?? [])
            .Select(c => (From: (string?)c.Attribute("from") ?? "", To: (string?)c.Attribute("to") ?? "",
                          Per: double.Parse((string?)c.Attribute("per_point") ?? "0", CultureInfo.InvariantCulture)))
            .ToList();
        foreach (var c in primary)
            if (StatIds.Of(c.From) is null || StatIds.Of(c.To) is null) throw new InvalidDataException($"server-rules.xml: primary_stats {c.From} -> {c.To}: unknown stat");
        var primaryMap = primary.GroupBy(c => c.To, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(string, double)>)g.Select(c => (c.From, c.Per)).ToList(), StringComparer.OrdinalIgnoreCase);
        var breakable = (string?)document.Root?.Element("enchant")?.Attribute("breakable") ?? EnchantOutcome.Safe;
        if (breakable is not (EnchantOutcome.Safe or EnchantOutcome.Keep or EnchantOutcome.Official))
            throw new InvalidDataException("server-rules.xml: enchant breakable must be safe, keep or official");
        var bots = int.Parse((string?)document.Root?.Element("bots")?.Attribute("count") ?? "0", CultureInfo.InvariantCulture);
        if (bots is < 0 or > 2000) throw new InvalidDataException("server-rules.xml: bots count must be 0-2000");
        var specializedFrom = int.Parse((string?)document.Root?.Element("gather")?.Attribute("specialized_from_level") ?? "0", CultureInfo.InvariantCulture);
        if (specializedFrom is < 0 or > 100) throw new InvalidDataException("server-rules.xml: gather specialized_from_level must be 0-100");
        var skinBag = int.Parse((string?)document.Root?.Element("gather")?.Attribute("skin_bag_percent") ?? "5", CultureInfo.InvariantCulture);
        if (skinBag is < 0 or > 100) throw new InvalidDataException("server-rules.xml: gather skin_bag_percent must be 0-100");
        var monsterAi = document.Root?.Element("monster_ai");
        bool AiFlag(string name) => (string?)monsterAi?.Attribute(name) is not { } v || System.Xml.XmlConvert.ToBoolean(v);
        return new ServerRules { BossOfficialAi = AiFlag("boss_official"), FunMonsters = AiFlag("fun_monsters"), GatherSpecializedFromLevel = specializedFrom, GatherSkinBagPercent = skinBag, EnchantBreakable = breakable, BotCount = bots, PrimaryStats = primaryMap, DismantleSkinChancePercent = chance, RemoveInstanceBarriers = barriers, SubscriptionFree = free, HelperDamagePercent = helper,
            SkillPointCap = Cap("skill_cap"), StigmaPointCap = Cap("stigma_cap"),
            CurrentSeason = (string?)document.Root?.Element("season")?.Attribute("current") ?? "G1",
            LeashNoHitMs = int.Parse((string?)document.Root?.Element("monsters")?.Attribute("leash_no_hit_ms") ?? "10000", CultureInfo.InvariantCulture),
            ChaseMinCm = Monsters("chase_min_cm", 1000), ChaseMaxCm = Monsters("chase_max_cm", 1500),
            ChaseAfterHitCm = Monsters("chase_after_hit_cm", 500), ReturnSpeedPercent = Monsters("return_speed_percent", 50),
            BossLeashNoHitMs = Monsters("boss_leash_no_hit_ms", 30_000), BossLostGraceMs = Monsters("boss_lost_grace_ms", 15_000),
            BossCcWindowMs = Bosses("cc_window_ms", 15_000), BossCcMax = Math.Max(1, Bosses("cc_max", 3)), BossCcImmuneMs = Bosses("cc_immune_ms", 10_000),
            BossStaggerPerCc = Bosses("stagger_per_cc", 25), BossStaggerDecayPerS = Bosses("stagger_decay_per_s", 5), BossStaggerMs = Bosses("stagger_ms", 5_000),
            BossStaggerImmuneMs = Bosses("stagger_immune_ms", 20_000), BossStaggerDamagePercent = Bosses("stagger_damage_percent", 50),
            FallSafeCm = int.Parse((string?)document.Root?.Element("falls")?.Attribute("safe_cm") ?? "1000", CultureInfo.InvariantCulture),
            FallPercentPerMetre = double.Parse((string?)document.Root?.Element("falls")?.Attribute("percent_per_m") ?? "2", CultureInfo.InvariantCulture),
            FallDeathCm = int.Parse((string?)document.Root?.Element("falls")?.Attribute("death_cm") ?? "6000", CultureInfo.InvariantCulture),
            PremiumShop = PremiumShopRules.Parse(document.Root?.Element("premium_shop")),
            RandomShop = RandomShopRules.Parse(document.Root?.Element("random_shop")),
            Rifts = RiftRules.Parse(document.Root?.Element("rifts"), document.Root?.Element("hidden_cubes")) };
    }
}
