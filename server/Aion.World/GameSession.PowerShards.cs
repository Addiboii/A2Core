using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Power Shards (Item.dat usable_category SealStone, use_action SealStone [SealStone_Lowest_1 | SealStone_High_1 | SealStone_Abyss_1]):
// the client registers one bag stack (RegisterSealStoneAutoUse 0x8d4c) and toggles it (Activate/DeactivateSealStone 0x8d47/0x8d49);
// both were handled, but nothing used the shards up or changed the damage (user 2026-11-08: "equipped, no effect, not consumed").
// The damage is PROVISIONAL (AION-style): while on, each player cast that damages a monster uses ONE shard and raises that cast's
// hits by the shard's amplify plus the flat "Power Shard Damage Bonus" stat (SealStoneAddDamage 69: titles, bracelet enchants, boards).
// OBSERVED (115817, 280 x 0x561b on one 533460001 stack, 0x949 -> 0x832): the official stack shrinks by one per cast, as an
// update with EDecreaseReason 22 kItemModifiedInCombat. The registered shard is named in MyCharAppear (bit 15, every load).
// When the registered stack is used up the next stack of the same shard takes over; with none left the shard switches off.
public sealed partial class GameSession
{
    /// <summary>Damage amplify per shard tier (PROVISIONAL, tunable): Power 5%, Superior 10%, Abyssal 15%.</summary>
    public static double ShardAmplify(string? tier) => tier switch
    {
        "SealStone_High_1" => 0.10,
        "SealStone_Abyss_1" => 0.15,
        _ => 0.05,
    };

    /// <summary>EDecreaseReason kItemModifiedInCombat: the official reason of a shard used by a cast (OBSERVED 0x561b `... 00 16 00`).</summary>
    public const byte DecreaseItemModifiedInCombat = 22;

    /// <summary>Shards the casts used since the last storage write (written after the cast, with the kill work).</summary>
    int pendingShardUses;

    static string? ShardTier(int templateId) =>
        ItemDB.Embedded.Of(templateId) is { Use: { Effect: "SealStone" } action } ? action.Values.FirstOrDefault() ?? "" : null;

    /// <summary>The active shard's amplify, or null when the shard is off, unregistered or used up.</summary>
    double? ActiveShardAmplify()
    {
        if (!sealStoneOn || sealStoneItem == 0 || !storedItems.TryGetValue(sealStoneItem, out var row) || row.Location != "bag") return null;
        if (row.Quantity - pendingShardUses <= 0) return null;
        return ShardTier(row.TemplateId) is { } tier ? ShardAmplify(tier) : null;
    }

    /// <summary>A cast's hits on a monster with the shard: amplify and the flat bonus on every damaging hit.</summary>
    List<Hit> WithShard(List<Hit> hits, double amplify)
    {
        var flat = Math.Max(0, CombatStat("SealStoneAddDamage"));
        return hits.Select(h => h.Amount > 0 ? h with { Amount = (int)Math.Min(int.MaxValue, Math.Round(h.Amount * (1 + amplify)) + flat) } : h).ToList();
    }

    /// <summary>One shard for a cast that dealt damage with the shard on; the storage write follows the cast.</summary>
    void UseShard()
    {
        pendingShardUses++;
        killWorkDue = true;
    }

    /// <summary>Writes the used shards (decrease reason ItemUse), moves to the next stack or switches the shard off.</summary>
    async Task<List<byte[]>> ShardWorkAsync(CancellationToken ct)
    {
        if (pendingShardUses <= 0) return [];
        var count = pendingShardUses;
        pendingShardUses = 0;
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(sealStoneItem, out var row)) return [];
        var templateId = row.TemplateId;
        var take = (int)Math.Min(count, row.Quantity);
        if (take <= 0) return [];
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, take, ct) ? [[]] : []);
        if (used.Count == 0) return [];
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemModifiedInCombat);
        if (storedItems.TryGetValue(sealStoneItem, out var left) && left.Location == "bag" && left.Quantity > 0) return replies;
        // The stack is gone: the next stack of the same shard, else off.
        var next = storedItems.FirstOrDefault(kv => kv.Value.TemplateId == templateId && kv.Value.Location == "bag" && kv.Value.Quantity > 0);
        if (next.Value is not null) replies.AddRange(await RegisterSealStoneAsync(next.Key, ct));
        else
        {
            replies.AddRange(await RegisterSealStoneAsync(0, ct));
            replies.AddRange(await SealStoneToggleAsync(false, ct));
        }
        return replies;
    }

    /// <summary>Using a shard from the bag or a quick slot registers that stack and switches the shard on.</summary>
    async Task<List<byte[]>?> UseShardItemAsync(ItemState row, long uid, bool reply, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        if (sealStoneItem != uid) replies.AddRange(await RegisterSealStoneAsync(uid, ct));
        if (!sealStoneOn) replies.AddRange(await SealStoneToggleAsync(true, ct));
        if (reply) replies.Add(RidePackets.ItemUseReply(RidePackets.ItemUseSuccess, uid, row.TemplateId));
        return replies;
    }
}
