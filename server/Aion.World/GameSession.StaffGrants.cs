using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Staff grants of collection items (USER 2026-11-13: "make the grant system work as the in-game shop ... wings should go straight
// to the skin area, automatically equipped; wings that are not supposed to be in the inventory"). A wing, skin, skin set, title,
// emote or decoration item that reached the bag through a staff grant (the item panel, staff rewards, the GM command:
// game.admin_requests) is used at once through the same code as its normal use, so it never waits in the bag: the entry is
// learned and stored, the item used up. When the character's slot for it is empty (no wing chosen, no title in that title slot, no
// skin in that skin slot) it is also worn; a chosen one is never replaced. Online the grant arrives as an inventory change
// (InventoryChangesAsync); offline grants, and staff grants left in a bag from before, are applied at the next world entry
// (InventoryListAsync). Idempotent: an applied item is gone, a refused one (an emote already known, the other race) stays.
// Items that use themselves anyway (AutoUseWhenAcquire: most skins and titles) go this way too when staff granted them, so they are
// also worn. Other ways an item arrives (loot, quests, shops, mail) keep the official behaviour (ItemRules.AutoUseEffects).
// CANDIDATE (never recorded): the server-initiated wear itself. The wing is announced with WingChange_NT 0x5676 (the notice of a
// chosen wing), a title with TitleEquipChange_NT 0x5681, skins with ItemEquipChange as after SkinEquip_RS.
public sealed partial class GameSession
{
    bool applyingStaffGrants;

    /// <summary>
    /// Staff-granted collection items among these bag rows (CollectionItems.LearnEffect, the character's race): learned, used up and
    /// worn where the slot is empty. Empty when there are none (one store query when the bag holds candidates).
    /// </summary>
    async Task<List<byte[]>> ApplyStaffGrantedItemsAsync(IEnumerable<long> keys, CancellationToken ct)
    {
        if (applyingStaffGrants || resources is null || dead || Store is not { } store || CharacterId is not { } character) return [];
        var rows = keys.Distinct().Select(k => storedItems.GetValueOrDefault(k)).OfType<ItemState>()
            .Where(r => r.Location == "bag" && CollectionItems.LearnEffect(r.TemplateId) is not null && ItemRules.RaceAllows(r.TemplateId, profileRace)).ToList();
        if (rows.Count == 0) return [];
        applyingStaffGrants = true;
        try
        {
            var staff = (await store.StaffGrantedItemsAsync(character, rows.Select(r => r.InstanceId).ToList(), ct)).ToHashSet();
            rows = rows.Where(r => staff.Contains(r.InstanceId)).ToList();
            if (rows.Count == 0) return [];
            Note($"staff-granted collection items applied: {string.Join(", ", rows.Select(r => $"{r.TemplateId} x{r.Quantity}"))}");
            var replies = new List<byte[]>();
            // Skins and skin sets: all at once (one store call per stack, one SkinUpdate_NT), then the empty skin slots.
            var skinRows = rows.Where(r => CollectionItems.LearnEffect(r.TemplateId) is "GetSkin" or "GetSkinSet").ToList();
            if (skinRows.Count > 0)
            {
                replies.AddRange(await AutoUseSkinItemsAsync(skinRows, ct));
                var learned = skinRows.Where(r => !storedItems.ContainsKey(WireKey(r)))
                    .SelectMany(r => ItemDB.Embedded.Of(r.TemplateId)?.Use is { } use ? SkinsGiven(use.Effect, use.Values) : []).ToList();
                replies.AddRange(await WearGrantedSkinsAsync(learned, ct));
            }
            foreach (var row in rows.Where(r => !skinRows.Contains(r)))
            {
                // The rows reload after every use: the stack as it is now.
                if (storedItems.GetValueOrDefault(WireKey(row)) is not { Location: "bag" } current) continue;
                var use = ItemDB.Embedded.Of(current.TemplateId)?.Use;
                switch (use?.Effect)
                {
                    case "GetWing" when use.Values is [var wingText, ..] && int.TryParse(wingText, out var wing) && wing > 0:
                        replies.AddRange(await AutoUseStackAsync(store, character, current, "GetWing", ct, DecreaseItemUse));
                        if (!storedItems.ContainsKey(WireKey(current))) replies.AddRange(await WearGrantedWingAsync(wing, ct));
                        break;
                    case "GetTitle" when use.Values is [var titleText, ..] && int.TryParse(titleText, out var title):
                        replies.AddRange(await AutoUseStackAsync(store, character, current, "GetTitle", ct, DecreaseItemUse));
                        if (!storedItems.ContainsKey(WireKey(current))) replies.AddRange(await WearGrantedTitleAsync(title, ct));
                        break;
                    case "GetSocialAction" or "GetDeco":
                        // One unit per use, as by hand (an emote or decoration already known is refused and its item stays).
                        for (var n = 0; n < current.Quantity && n < 100; n++)
                        {
                            if (storedItems.GetValueOrDefault(WireKey(current)) is not { Location: "bag" } now) break;
                            var used = use.Effect == "GetDeco"
                                ? await UseDecoItemAsync(store, character, now, new ItemUse(use.Effect, false, use.Values), false, ct)
                                : use.Values is [var emoteText, ..] && int.TryParse(emoteText, out var emote)
                                    ? await UseSocialActionItemAsync(store, character, now, emote, false, ct) : null;
                            if (used is null) break;
                            replies.AddRange(used);
                        }
                        break;
                }
            }
            return replies;
        }
        finally { applyingStaffGrants = false; }
    }

    /// <summary>A granted wing is worn when no wing was chosen (setting "wing" 0: the default wing); a chosen wing stays.</summary>
    async Task<List<byte[]>> WearGrantedWingAsync(int wing, CancellationToken ct)
    {
        if (equippedWing != 0 || !OwnedWings().Any(w => w.Id == wing)) return [];
        equippedWing = wing;
        if (wingId != 0) wingId = wing;
        await SaveSettingAsync("wing", new[] { equippedWing, wingSkin }, ct);
        var notice = GamePackets.WingChangeNotice(playerKey, wing, false);
        worldPlayer?.Map?.Broadcast(worldPlayer, [notice]);
        return [notice, .. RefreshStats()];
    }

    /// <summary>A granted title is worn in its ETitleEquipCategory slot (1-3) when that slot is empty.</summary>
    async Task<List<byte[]>> WearGrantedTitleAsync(int titleId, CancellationToken ct)
    {
        if (Titles.Of(titleId) is not { Category: >= 1 and <= 3 } title || !titles.Contains(titleId) || titleEquips.ContainsKey(title.Category)
            || !await SaveTitleAsync(title.Category, titleId, ct)) return [];
        return [TitleEquipChange(title.Category, titleId), .. RefreshStats()];
    }

    /// <summary>Granted skins are worn in their empty ESkinEquipSlot (CollectionItems.SkinEquipSlots); worn skins stay.</summary>
    async Task<List<byte[]>> WearGrantedSkinsAsync(IReadOnlyList<int> learned, CancellationToken ct)
    {
        var changes = new Dictionary<int, int>();
        foreach (var skin in learned.Distinct().Where(skins.ContainsKey))
            foreach (var slot in CollectionItems.SkinEquipSlots(Skins.Category(skin)))
                if (!skinEquips.ContainsKey(slot) && changes.TryAdd(slot, skin)) break;
        if (changes.Count == 0) return [];
        var before = VisualEquips(true);
        if (Store is { } store && CharacterId is { } character)
        {
            var saved = await Stored(async () => await store.SetSkinEquipsAsync(character, changes, ct) ? [[]] : []);
            if (saved.Count == 0) return [];
        }
        foreach (var (slot, skin) in changes) skinEquips[slot] = skin;
        var after = VisualEquips(true);
        var changed = before.Zip(after).Where(p => p.First.SkinId != p.Second.SkinId).Select(p => (p.First.VisualSlot, p.First, p.Second)).ToList();
        if (changed.Count == 0) return [];
        var shown = PlayerPackets.ItemEquipChange(playerKey, EquipItemLevel(), changed);
        worldPlayer?.Map?.Broadcast(worldPlayer, [shown]);
        return [shown];
    }
}
