using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Power Shards (the SealStone consumable, Item.dat MiscInfo SealStone -> SealStone.dat SealStone_Default_00): a bag stack
// registered in the HUD auto slot left of B (RegisterSealStoneAutoUse 0x8d4c) while the toggle is on (ActivateSealStone
// 0x8d47) is spent by every damaging cast, Skill.dat / SkillLv.dat SealStoneConsumptionCount per cast, and the cast deals
// the SealStone.dat StatList bonus (EStat SealStoneAddDamage 69, SealStoneAmplifyDamage 534, SealStonePvPIgnoreDefense 559).
// Without enough shards the cast is a plain one. The spent count goes out as the stack's 0x561b change (kItemUse).
// INFERRED: the per-skill counts (absent from the export), the StatList values and when the count is taken (once per cast that
// hits something, as ESealStoneECType ATTACK / HIT suggest).
public sealed partial class GameSession
{
    /// <summary>Power Shard (Bound) 533460001 and the other two Power Shard stacks (034_item_stacks.sql: 100,000 each).</summary>
    public static readonly int[] PowerShardItems = [533460001, 533460004, 533460006];

    /// <summary>INFERRED SealStoneConsumptionCount when the skill export has none: a basic attack 1, any other damaging skill 2.</summary>
    public const int SealStoneBasicCount = 1, SealStoneSkillCount = 2;

    /// <summary>INFERRED SealStone_Default_00 StatList: flat damage per hit, amplify and PvP defense ignored in 1/10000.</summary>
    public const int SealStoneAddDamage = 50, SealStoneAmplifyDamage = 1000, SealStonePvPIgnoreDefense = 1000;

    int sealStonePending;
    long sealStonePendingUid;

    /// <summary>Shards the cast takes (0 for a cast that deals no damage).</summary>
    public int SealStoneConsumption(int skill) => data.Skill(skill) switch
    {
        null or { Damage.Length: 0 } => 0,
        { SealStone: { } count } => Math.Max(0, count),
        var d when SkillCooldown.IsBasicAutoAttack(d) => SealStoneBasicCount,
        _ => SealStoneSkillCount,
    };

    /// <summary>The slotted stack; once it is used up, the next Power Shard stack in the bag (the slot refills itself).</summary>
    ItemState? SealStoneRow()
    {
        if (sealStoneItem == 0) return null;
        if (storedItems.TryGetValue(sealStoneItem, out var row) && row.Location == "bag") return row;
        return storedItems.Values.Where(i => i.Location == "bag" && PowerShardItems.Contains(i.TemplateId))
            .OrderByDescending(i => i.Quantity).FirstOrDefault();
    }

    /// <summary>Takes this cast's shards (stored by <see cref="SealStoneConsumedAsync"/> after the cast) when enough are left.</summary>
    bool TryUseSealStone(int skill)
    {
        var need = SealStoneConsumption(skill);
        if (!sealStoneOn || equippedWeapon is null || need <= 0) return false;
        if (SealStoneRow() is not { } row) return false;
        var uid = WireKey(row);
        if (sealStonePending > 0 && uid != sealStonePendingUid) return false;
        if (row.Quantity - sealStonePending < need) return false;
        sealStonePendingUid = uid;
        sealStonePending += need;
        return true;
    }

    static IReadOnlyList<Hit> SealStoneBoost(IReadOnlyList<Hit> hits) =>
        hits.Select(h => h.Amount > 0 ? h with { Amount = (int)Math.Min(int.MaxValue, (long)h.Amount * (10_000 + SealStoneAmplifyDamage) / 10_000 + SealStoneAddDamage) } : h)
            .ToList();

    static int SealStonePvpDefense(int defense) => (int)((long)defense * (10_000 - SealStonePvPIgnoreDefense) / 10_000);

    /// <summary>
    /// Stores the shards the last casts took: the stack's 0x561b (decrease kItemUse). A used-up slot moves to the next Power
    /// Shard stack, or empties (BroadcastSealStoneChange_NT item 0) when there is none.
    /// </summary>
    async Task<List<byte[]>> SealStoneConsumedAsync(CancellationToken ct)
    {
        if (sealStonePending == 0) return [];
        var (uid, count) = (sealStonePendingUid, sealStonePending);
        sealStonePending = 0;
        if (Store is not { } store || CharacterId is not { } character || !storedItems.TryGetValue(uid, out var row)) return [];
        var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, count, ct) ? [[]] : []);
        if (used.Count == 0) return [];
        var replies = await InventoryChangesAsync(ct, 0, DecreaseItemUse);
        if (uid != sealStoneItem) replies.AddRange(await SealStoneSlotAsync(uid, row.TemplateId, ct));
        else if (!storedItems.ContainsKey(uid) && SealStoneRow() is { } next) replies.AddRange(await SealStoneSlotAsync(WireKey(next), next.TemplateId, ct));
        else if (!storedItems.ContainsKey(uid)) replies.AddRange(SealStoneSlotShown(0));
        return replies;
    }

    async Task<List<byte[]>> SealStoneSlotAsync(long uid, int itemId, CancellationToken ct)
    {
        sealStoneItem = uid;
        await SaveSettingAsync("seal_stone", new SealStoneSetting(sealStoneOn, uid), ct);
        return SealStoneSlotShown(itemId);
    }

    List<byte[]> SealStoneSlotShown(int itemId)
    {
        var change = PlayerPackets.SealstoneChange(playerKey, itemId);
        worldPlayer?.Map?.Broadcast(worldPlayer, [change]);
        return [change];
    }
}
