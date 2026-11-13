namespace Aion.World;

/// <summary>
/// The order ItemSort_RQ (0x563e) lays a container out in. OBSERVED (7 official bag sorts, recordings 20261106-105448 #28033/#34900,
/// 115817 #84755/#208101, 145442 #42653 and its two copies; the 0x561b before each 0x563f holds every moved item on its new slot, 44
/// items in #208101): the items go on slots 0, 1, 2, ... ordered by
/// 1. EItemType (Currency, Equip, Usable, Misc, Quest: the usmap enum order);
/// 2. currencies: tradable Kina (GoldNonbinding) first, then ECurrencyCategory order (Gold, AbyssPoint, CoinBattlePass, CoinQuest6,
///    CoinQuest7, CoinEnchant, CoinDungeon1 as recorded); other items: their category in its enum order (EEquipCategory: Greatsword,
///    Sword, ..., Pants, Boots, Cape, Necklace, Bracelet, Amulet; EUsableCategory: None, Potion, Scroll, TeleportScroll, MagicStone,
///    SetupKisk, Gather, Rebirth, SealStone, AddContentsTicketCount, GetArcana; EMiscCategory: Misc, CraftResource,
///    ConversionResource, GatherResource, Ticket, MonolithToken, SkinCombine);
/// 3. EItemGrade, highest first (Special scroll before Common ones, Legend before Rare, Unique before Common);
/// 4. ECoolTimeGroup in enum order (dispel, hp_potion, potion_hp_dot_b, potion_hp_instance_c, potion_mp_dot_b, potion_fp_instance_c;
///    magic_buff_potion before magic_scroll);
/// 5. the first use-effect value when it is a number (the scroll skills 2210103 &lt; 2210104 &lt; 2210105 ...);
/// 6. Item.dat ItemLevel, highest first (two Unique capes: level 41 before 36);
/// 7. the item id (502760004 before 532760001, the kisks, the tickets).
/// The rule set orders all 7 recorded results without a single exception (method: a2core-log/sort-filter-2026-11-13.md).
/// CANDIDATE: stacks of one template (never seen apart in a recorded sort) are kept in acquisition order, the oldest first; items
/// missing from the item table go last.
/// </summary>
public static class ItemSortOrder
{
    // Enum orders from the client's usmap (AION2-5.3.2.0-2.0.6.0), index = the enum value.
    static readonly string[] ItemTypes =
    [
        "None", "Currency", "Equip", "Usable", "Misc", "Quest"
    ];
    static readonly string[] Grades =
    [
        "None", "Common", "Rare", "Legend", "Unique", "Epic", "Mythic", "Special"
    ];
    static readonly string[] EquipCategories =
    [
        "None", "All", "Greatsword", "Sword", "Dagger", "Bow", "Magicbook", "Orb", "Mace", "Staff", "Shield", "Guarder", "Gauntlet", "Reserved1",
        "Reserved2", "Reserved3", "Reserved4", "Weapon", "Helmet", "Shoulder", "Torso", "Pants", "Gloves", "Boots", "Reserved6", "Reserved7", "Belt",
        "Cape", "Reserved8", "Reserved9", "Armor", "Necklace", "BMEarring", "Earring", "BMRing", "Ring", "Bracelet", "Pendant", "Brooch", "Rune",
        "Amulet", "Reserved12", "BMSeal", "Seal", "BMRelic", "Relic", "Badge", "ManaStone", "Accessory", "BMEmblem", "Emblem", "ArcanaGrail",
        "ArcanaParchment", "ArcanaCompass", "ArcanaBell", "ArcanaMirror", "ArcanaLibra", "ArcanaKey", "ArcanaHourglass", "ArcanaDice",
        "ArcanaLantern", "Arcana"
    ];
    static readonly string[] UsableCategories =
    [
        "None", "Potion", "CraftRecipe", "ConversionRecipe", "RewardBox", "BM", "SkillBook", "QuestScroll", "Food", "Drink", "Scroll",
        "TeleportScroll", "MagicStone", "SetupKisk", "GetPoint", "Note", "GetWing", "GetTitle", "Ticket", "Gather", "Rebirth", "SkinGacha",
        "Customize", "GuildFame", "Reputation", "CraftMastery", "GatherMastery", "SealStone", "GetGodstone", "AscensionGrade", "Extension",
        "GetSocialAction", "AddContentsTicketTime", "AddContentsTicketCount", "VehicleSoul", "VehicleSoulCrystal", "Polymorph", "GetArcana",
        "PetPolymorph", "CharacterSlotUnlock", "Quest", "SkinShop", "BattlePass", "GetGameCash", "Deco", "Subscribe", "SkinCombine", "KinaBox",
        "SkinExtraction", "GetTitlePeriod", "Succession", "SoulAdd", "Surpass", "MonolithAllClear", "SealAllClear", "OccupationTerritoryAllClear",
        "MembershipPass", "ClassChange"
    ];
    static readonly string[] MiscCategories =
    [
        "None", "Crystal", "Material", "Misc", "QuestItem", "Customize", "BM", "CraftResource", "ConversionResource", "ConversionResource_Special",
        "GatherResource", "Ticket", "Refund", "Token", "Elevate", "ArcanaMaterial", "CreateArcanaMaterial", "MonolithToken", "Amplify",
        "SkinCombine", "ContentsSweepTicket"
    ];
    static readonly string[] CoolTimeGroups =
    [
        "None", "basic_hp_potion", "common", "dispel", "food", "getskill_scroll", "hp_potion", "magic_buff_potion", "magic_scroll", "mp_dot_potion",
        "mp_potion", "random_box", "speed_1x", "speed_2x", "speed_3x", "teleport_scroll", "trans_scroll", "SetupKisk", "Rebirth", "RewardBox",
        "teleport1", "teleport2", "teleport3", "teleport4", "teleport5", "fp_potion", "potion_hp_dot_a", "potion_hp_dot_b", "potion_hp_instance_c",
        "potion_hp_instance_d", "potion_mp_dot_a", "potion_mp_dot_b", "potion_mp_instance_c", "potion_mp_instance_d", "potion_hp_mp_dot_b",
        "potion_hp_mp_instance_c", "potion_fp_instance_c", "potion_fp_instance_d", "jelly_dp_instance", "crystal_dp_dot",
        "magic_scroll_protectshield", "candy_polymorph", "buff_exp", "buff_utility", "gatcha_godstone", "recipe", "get_point", "get_ticket",
        "get_wing", "food_drink", "CharacterSlotUnlock", "GetArcana", "SelfRebirthItem", "event1", "event2", "event3", "event4", "daevaexpress"
    ];
    static readonly Dictionary<string, int> CurrencyCategories = new()
    {
        ["None"] = 0, ["Gold"] = 1, ["CoinGuild"] = 2, ["AbyssPoint"] = 3, ["CoinBattlePass"] = 4, ["CoinReserved1"] = 5, ["Mileage"] = 6,
        ["GameCash"] = 7, ["Cash"] = 9, ["CoinEvent1"] = 10, ["CoinEvent2"] = 11, ["CoinEvent3"] = 12, ["CoinEvent4"] = 13, ["CoinEvent5"] = 14,
        ["CoinEvent6"] = 15, ["CoinEvent7"] = 16, ["CoinEvent8"] = 17, ["CoinEvent9"] = 18, ["CoinEvent10"] = 19, ["CoinEvent11"] = 20,
        ["CoinEvent12"] = 21, ["CoinEvent13"] = 22, ["CoinEvent14"] = 23, ["CoinEvent15"] = 24, ["CoinEvent16"] = 25, ["CoinEvent17"] = 26,
        ["CoinEvent18"] = 27, ["CoinEvent19"] = 28, ["CoinEvent20"] = 29, ["CoinEvent21"] = 31, ["CoinEvent22"] = 32, ["CoinEvent23"] = 33,
        ["CoinEvent24"] = 34, ["CoinEvent25"] = 35, ["CoinEvent26"] = 36, ["CoinEvent27"] = 37, ["CoinEvent28"] = 38, ["CoinEvent29"] = 39,
        ["CoinEvent30"] = 40, ["CoinEvent31"] = 41, ["CoinEvent32"] = 42, ["CoinEvent33"] = 43, ["CoinEvent34"] = 44, ["CoinEvent35"] = 45,
        ["CoinEvent36"] = 46, ["CoinEvent37"] = 47, ["CoinEvent38"] = 48, ["CoinEvent39"] = 49, ["CoinEvent40"] = 50, ["CoinQuestRefresh"] = 51,
        ["CoinQuestReset"] = 52, ["DivineShard"] = 54, ["CoinQuest1"] = 55, ["CoinQuest2"] = 56, ["CoinQuest3"] = 57, ["CoinQuest4"] = 58,
        ["CoinQuest5"] = 59, ["CoinQuest6"] = 60, ["Medal1"] = 61, ["Medal2"] = 62, ["Medal3"] = 63, ["Medal4"] = 64, ["Medal5"] = 65,
        ["EssenceGuild"] = 66, ["CoinQuest7"] = 67, ["DaevanionPoint"] = 68, ["SkinDyeCommon"] = 69, ["SkinDyePremium"] = 70, ["CoinEnchant"] = 71,
        ["ConvenientCraft"] = 72, ["CoinDungeon1"] = 79, ["SoulCrystalAll"] = 80, ["SoulCrystalIntellect"] = 81, ["SoulCrystalFeral"] = 82,
        ["SoulCrystalNature"] = 83, ["SoulCrystalTrans"] = 84, ["SoulCrystalSpecial"] = 85, ["CoinArena"] = 86, ["CraftFee"] = 87,
        ["DaevanionPointConquest"] = 88, ["DaevanionPointBattle"] = 89, ["Season02Crystal01"] = 90, ["CoinSeason2"] = 91, ["CoinBattlePass2"] = 92,
        ["GoldCombined"] = 93, ["GoldNonbinding"] = 94, ["GameCashBinding"] = 95, ["GameCashNonbinding"] = 96, ["Season03Crystal01"] = 97,
        ["CoinSeason3"] = 98, ["CoinSeason4"] = 99, ["CoinBattlePass3"] = 100, ["GameCashActivating"] = 102, ["GameCashPending"] = 103,
        ["CoinBattlePass4"] = 104, ["CoinSeason5"] = 105, ["CoinOnboarding1"] = 106, ["CoinOnboarding2"] = 107, ["CoinOnboarding3"] = 108,
        ["ExchangeTicket01"] = 109, ["ExchangeTicket02"] = 110, ["ExchangeTicket03"] = 111, ["ExchangeTicket04"] = 112, ["ExchangeTicket05"] = 113,
        ["CoinOnboarding4"] = 114, ["CoinBattlePassEncore1"] = 115, ["CoinTrialDungeon"] = 116,
    };

    static int Ordinal(string[] names, string? name) => name is null ? 0 : Array.IndexOf(names, name) is var i and >= 0 ? i : names.Length;

    /// <summary>The sort key of a template (compare with <see cref="Compare"/>).</summary>
    public readonly record struct Key(int Type, int Group, int Category, int Grade, int CoolTime, long UseValue, int Level, int Id) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            var c = Type.CompareTo(other.Type);
            if (c == 0) c = Group.CompareTo(other.Group);
            if (c == 0) c = Category.CompareTo(other.Category);
            if (c == 0) c = other.Grade.CompareTo(Grade);   // highest grade first
            if (c == 0) c = CoolTime.CompareTo(other.CoolTime);
            if (c == 0) c = UseValue.CompareTo(other.UseValue);
            if (c == 0) c = other.Level.CompareTo(Level);   // highest item level first
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }

    public static Key KeyOf(int templateId)
    {
        if (ItemDB.Embedded.Of(templateId) is not { } item) return new Key(ItemTypes.Length + 1, 0, 0, 0, 0, 0, 0, templateId);
        var type = Ordinal(ItemTypes, item.Type);
        if (item.Type == "Currency")
        {
            var currency = item.CurrencyCategory;
            return new Key(type, currency == "GoldNonbinding" ? 0 : 1, currency is not null && CurrencyCategories.TryGetValue(currency, out var c) ? c : int.MaxValue,
                0, 0, 0, 0, templateId);
        }
        var category = item.Type switch
        {
            "Equip" => Ordinal(EquipCategories, item.EquipCategory),
            "Usable" => Ordinal(UsableCategories, item.UsableCategory),
            "Misc" => Ordinal(MiscCategories, item.MiscCategory),
            _ => 0,
        };
        var useValue = item.Use is { Values: [var first, ..] } && long.TryParse(first, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new Key(type, 0, category, Ordinal(Grades, item.Grade), Ordinal(CoolTimeGroups, item.CooltimeGroup ?? "None"), useValue, item.Level, templateId);
    }

    /// <summary>Two templates in sort order.</summary>
    public static int Compare(int a, int b) => KeyOf(a).CompareTo(KeyOf(b));
}
