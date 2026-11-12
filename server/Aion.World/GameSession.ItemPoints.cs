using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Points from items (migration 079; tools/data/boxes.json uses GetSkillPoint / GetDaevanionPoint). OBSERVED in the community
// recording 2026-11-06 234912 (21:59:04 and 21:59:48): the Wisdom Stone 531900001 answers 0x561b (decrease 14), the use reply,
// then UpdateItemSkillPoint_NT 0x3811 {ESkillPointType u8 1, total u16} (15, then 16: one point each); the Daevanion Crystal
// 531900003 answers 0x561b (decrease 70 kGetDaevanionPoint), the use reply, then UpdateItemDaevanionPoint_NT 0xe22f
// {count 5, {EDaevanionPointType u8, total u16}} (type 1: 15, then 16). The skill points add to what the levels give.
// OURS: no cap (EResult has 0x0338 Over_Already_FixDaevanionPoint; the cap is unknown) but the client's u16; 0xe22f at world
// entry when a character has crystal points (no recording shows the entry message for them).
public sealed partial class GameSession
{
    public const ushort ItemSkillPointNt = 0x3811, ItemDaevanionPointNt = 0xe22f;
    const byte DecreaseDaevanionPoint = 70;   // EDecreaseReason kGetDaevanionPoint (OBSERVED i47691)
    const int PointCategorySkill = 1, PointCategoryDaevanion = 2;
    /// <summary>EDaevanionPointType (usmap of client 2.0.6; the crystals' UseEffectValues name them).</summary>
    static readonly string[] DaevanionPointTypes = ["None", "DaevanionCrystal", "ConquestCrystal", "BattleCrystal", "Season02Crystal01", "Season03Crystal01"];

    int itemSkillPoints;
    readonly int[] daevanionPoints = new int[DaevanionPointTypes.Length];

    /// <summary>Skill points items gave (Wisdom Stones).</summary>
    public int ItemSkillPoints => itemSkillPoints;
    /// <summary>Daevanion points items gave, by EDaevanionPointType (index 1-5).</summary>
    public IReadOnlyList<int> DaevanionPoints => daevanionPoints;

    void LoadItemPoints(IReadOnlyList<ItemPointRecord> rows)
    {
        itemSkillPoints = 0;
        Array.Clear(daevanionPoints);
        foreach (var r in rows)
        {
            if (r.Category == PointCategorySkill && r.Type == SkillPointTypePoint) itemSkillPoints = Math.Max(0, r.Points);
            else if (r.Category == PointCategoryDaevanion && r.Type > 0 && r.Type < daevanionPoints.Length) daevanionPoints[r.Type] = Math.Max(0, r.Points);
        }
    }

    byte[] ItemSkillPointUpdate() => PacketWriter.Opcode(ItemSkillPointNt).U8(SkillPointTypePoint).U16(Math.Min(itemSkillPoints, ushort.MaxValue)).ToArray();

    byte[] ItemDaevanionPointUpdate()
    {
        var w = PacketWriter.Opcode(ItemDaevanionPointNt).Varint((uint)(daevanionPoints.Length - 1));
        for (var type = 1; type < daevanionPoints.Length; type++) w.U8((byte)type).U16(Math.Min(daevanionPoints[type], ushort.MaxValue));
        return w.ToArray();
    }

    /// <summary>A GetSkillPoint item (UseEffectValues [points]): used up, its points added to the item skill points (0x3811).</summary>
    async Task<List<byte[]>?> UseSkillPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, int points, bool reply, CancellationToken ct)
    {
        if (points < 1 || itemSkillPoints + points > ushort.MaxValue) return null;
        var done = await Stored(async () => await store.UsePointItemAsync(character, row.InstanceId, row.Revision, PointCategorySkill, SkillPointTypePoint, points, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        itemSkillPoints += points;
        return [.. await InventoryChangesAsync(ct, 0, DecreaseItemUse), .. Used(row, reply), ItemSkillPointUpdate()];
    }

    /// <summary>A GetDaevanionPoint item (UseEffectValues [EDaevanionPointType name, points]): used up, its points added (0xe22f).</summary>
    async Task<List<byte[]>?> UseDaevanionPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, string kind, int points, bool reply, CancellationToken ct)
    {
        var type = Array.IndexOf(DaevanionPointTypes, kind);
        if (type < 1 || points < 1 || daevanionPoints[type] + points > ushort.MaxValue) return null;
        var done = await Stored(async () => await store.UsePointItemAsync(character, row.InstanceId, row.Revision, PointCategoryDaevanion, type, points, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        daevanionPoints[type] += points;
        return [.. await InventoryChangesAsync(ct, 0, DecreaseDaevanionPoint), .. Used(row, reply), ItemDaevanionPointUpdate()];
    }
}
