using Aion.Protocol.Native;

namespace Aion.World;

// Ascension rewards (client AscensionReward, 90 rows: grade 2..6 x race x class -> exp and items; tools/loot_data.py "ascension").
// Paid when the stored grade rises (the Ashar turn-in to grade 2, an Ascension item to 3..6), once per grade passed: grade 2 has
// none; 3..6 give the race's Ascension wing item and housing decorations (3: a Stigma Shard, 5: a bracelet and an amulet). ExpReward
// is 0 in every row. CANDIDATE delivery (no ascension in any recording): into the bag with the usual item update, by reward mail
// when the bag has no room. Staff SetAscension (admin) pays nothing.
public sealed partial class GameSession
{
    /// <summary>The AscensionReward items for the grades after <paramref name="from"/> up to <paramref name="to"/> (this character's
    /// race and class).</summary>
    public List<(int ItemId, long Count)> AscensionRewardItems(int from, int to)
    {
        var className = EntryClassName;
        var items = new List<(int, long)>();
        for (var grade = Math.Max(2, from + 1); grade <= to; grade++)
            if (LootTables.Embedded.AscensionReward(grade, profileRace, className) is { } row)
                items.AddRange(row.Items.Where(i => i.Length >= 2 && i[1] > 0).Select(i => ((int)i[0], i[1])));
        return items;
    }

    /// <summary>Pays the AscensionReward of the grades after <paramref name="from"/> up to <paramref name="to"/>: the item update
    /// (bag) or a reward mail (RecvPost_NT) when the bag has no room.</summary>
    async Task<List<byte[]>> AscensionRewardsAsync(int from, int to, CancellationToken ct)
    {
        var items = AscensionRewardItems(from, to);
        if (items.Count == 0 || Store is not { } store || CharacterId is not { } character) return [];
        if (BagRoomFor(items))
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count > 0) return await InventoryChangesAsync(ct);
        }
        var post = await StoredValue(() => store.SendPostAsync(character, StaffPostDataId, $"Ascension|Ascension grade {to} reward", items, PostTabReward, ct), null);
        return post is null ? [] : [WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray()];
    }
}
