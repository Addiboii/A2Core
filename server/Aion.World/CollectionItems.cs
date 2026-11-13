namespace Aion.World;

/// <summary>
/// Items that only teach the character a collection entry (USER 2026-11-13: "make the grant system work as the in-game shop ...
/// wings that are not supposed to be in the inventory"): a wing, a skin or skin set, a title, an emote, a Pantheon decoration.
/// A staff grant of one is applied at once instead of lying in the bag (GameSession.StaffGrants.cs), and the placeholder rows of
/// the client's item table, which teach nothing, are refused by the staff grant.
/// <para>Item.dat (client table 2026-11-09, 9,261 items) has these learn effects: GetSkin 1,508 (1,448 AutoUseWhenAcquire),
/// GetTitle 1,464 (1,248), GetSkinSet 119 (all), GetWing 96 (2), GetDeco 159, GetSocialAction 17. The pets (GetVehicle 207) and their
/// souls are all AutoUseWhenAcquire and already used on every path (ItemRules.AutoUseEffects).</para>
/// <para>Placeholders: 78 Usable items have no use action at all (UseEffect None). 58 of them are wings (UsableCategory GetWing:
/// the PartyDungeon / cube `_f` wings, the BMshop, BattlePass, Contents, CLB, Ascension and event wings) with no Wing.dat link; the
/// client cannot learn or show them (live 2026-11-12 zenyc's 69 grey wings, 2026-11-13 sorace's 25). The other 20 (contents
/// tickets, quest scrolls, battle pass unlocks, 4 boxes) are not collection items and stay grantable.</para>
/// </summary>
public static class CollectionItems
{
    /// <summary>The use effects that teach a collection entry and are applied when staff grant the item.</summary>
    public static readonly IReadOnlySet<string> LearnEffects = new HashSet<string>
    {
        "GetWing", "GetSkin", "GetSkinSet", "GetTitle", "GetSocialAction", "GetDeco",
    };

    /// <summary>The usable categories of collection items: a Usable item of one of these without a use action is a placeholder.</summary>
    static readonly IReadOnlySet<string> CollectionCategories = new HashSet<string>
    {
        "GetWing", "GetSkin", "GetSkinSet", "GetTitle", "GetSocialAction", "GetDeco", "GetVehicle",
    };

    /// <summary>The collection effect of an item (one of LearnEffects), or null.</summary>
    public static string? LearnEffect(int templateId) =>
        ItemDB.Embedded.Of(templateId)?.Use is { Effect: var effect } && LearnEffects.Contains(effect) ? effect : null;

    /// <summary>
    /// A collection item without any use action (Item.dat UseEffect None): nothing the client can learn or show; "not usable in this
    /// client" in the staff item database and refused by the staff grant.
    /// </summary>
    public static bool IsPlaceholder(int templateId) =>
        ItemDB.Embedded.Of(templateId) is { Type: "Usable", Use: null, UsableCategory: { } category }
        && CollectionCategories.Contains(category) && RewardBoxes.Embedded.Use(templateId) is null;

    /// <summary>Every placeholder of the item table, by id.</summary>
    public static IReadOnlyList<int> Placeholders => placeholders.Value;
    static readonly Lazy<IReadOnlyList<int>> placeholders = new(() => ItemDB.Embedded.All.Where(i => IsPlaceholder(i.Id)).Select(i => i.Id).Order().ToList());

    /// <summary>The grant error for a placeholder (the staff panel shows its text).</summary>
    public const string PlaceholderError = "not_usable_in_client";

    /// <summary>
    /// The ESkinEquipSlot a skin of this ESkinCategory is worn in (usmap AION2 5.3.2.0: Helmet..Cape 1-7, SwordMain 8, Greatsword 10,
    /// DaggerMain 11, Bow 13, Staff 14, MaceMain 15, Magicbook 17, Orb 18, EarringL 20 / EarringR 21, Necklace 22, EyeWear 23,
    /// MouthWear 24, ShoulderWear 25, Gauntlet 26, HeadWear 27); 0 for none. The earrings fill the left slot first.
    /// </summary>
    public static IReadOnlyList<int> SkinEquipSlots(string category) => category switch
    {
        "Helmet" => [1], "Shoulder" => [2], "Torso" => [3], "Pants" => [4], "Gloves" => [5], "Boots" => [6], "Cape" => [7],
        "Sword" => [8], "Greatsword" => [10], "Dagger" => [11], "Bow" => [13], "Staff" => [14], "Mace" => [15], "Magicbook" => [17], "Orb" => [18],
        "Earring" => [20, 21], "Necklace" => [22], "EyeWear" => [23], "MouthWear" => [24], "ShoulderWear" => [25], "Gauntlet" => [26], "HeadWear" => [27],
        _ => [],
    };
}
