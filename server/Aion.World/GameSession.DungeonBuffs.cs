using Aion.Protocol.Native;

namespace Aion.World;

// The daily dungeon's buff choice (Dungeon.dat DungeonBuffGroupName: DungeonWave_0001 "Daeva Bio-Research Base" only in this
// client; DailyDungeons.cs has the tables and the messages). NEVER RECORDED officially (no 0x6105/0x6107 in any recording, the
// client's requests are encrypted in the captures): everything here is CANDIDATE, built from the client data and its results.
// - The offer: one DungeonBuff row per BuffSlot (1-3) of the group, drawn from the rows that slot lists (DungeonBuffGroup.dat).
//   The draw is fixed per character, dungeon and day (the daily reset 07:00 UTC, the contents tickets' recharge), so a run entered
//   again the same day offers the same three and the next day offers new ones (nothing to store: the draw is seeded).
// - When: the run's Prepare state (EResult DungeonBuff_Fail_NotPreparingState 15612: the choice is only taken while preparing),
//   DungeonBuffInfo_NT 0x6105 right after DungeonState_NT Prepare at the entry. DungeonWave_0001 got a ready time for it
//   (tools/daily_dungeon_data.py READY, 10 s like Dungeon_Daily_0001's).
// - The choice: DungeonBuffSelect_RQ 0x6106 {dungeon i32, slot i32} -> DungeonBuffSelect_RS 0x6107 {result, dungeon, slot}; the slot's
//   abnormal (DungeonBuff BuffValue1) goes on the player for the rest of the run (a scripted buff: it leaves with the map). A second
//   choice replaces the first. Refusals: 15611 NotInDungeonBuffMap (not in a copy of that dungeon, or it has no buff group),
//   15612 NotPreparingState, 15613 InvalidDungeonBuffData (a slot not offered), 15615 InvalidAbnormalData (the abnormal unknown).
public sealed partial class GameSession
{
    /// <summary>The daily reset of the draw (07:00 UTC = 16:00 KST, the contents tickets' DailyRechargeTime).</summary>
    const long BuffDayOffsetMs = 7 * 3600_000L;

    /// <summary>The buffs a character is offered in a dungeon on the day of <paramref name="now"/>: one DungeonBuff row per slot.</summary>
    internal static List<(int Slot, DungeonBuffRow Buff)> DungeonBuffOffer(DailyDungeon d, long characterId, long now)
    {
        var day = Math.Max(0, (now - BuffDayOffsetMs) / 86_400_000L);
        // A fixed mix (HashCode is seeded per process): the same draw for the same character, dungeon and day across restarts.
        var seed = unchecked((int)((((characterId * 1_000_003L) ^ d.Id) * 1_000_033L) ^ day));
        var rng = new Random(seed);
        return d.BuffSlots.OrderBy(s => s.Key).Where(s => s.Value.Count > 0).Select(s => (s.Key, s.Value[rng.Next(s.Value.Count)])).ToList();
    }

    /// <summary>The copy's offer, drawn once for its host at the first look.</summary>
    List<(int Slot, DungeonBuffRow Buff)>? CopyBuffOffer(long now)
    {
        if (copy is not { } c || Dailies.Of(c.DungeonId) is not { BuffSlots.Count: > 0 } d) return null;
        return c.BuffOffer ??= DungeonBuffOffer(d, c.Host.DbId, now);
    }

    /// <summary>At the entry of a dungeon in its Prepare state: the buffs to choose from (DungeonBuffInfo_NT).</summary>
    List<byte[]> DungeonBuffsAtEntry(long now) =>
        copy is { Phase: "Prepare" } c && CopyBuffOffer(now) is { Count: > 0 } offer
            ? [DailyDungeons.DungeonBuffInfo(c.DungeonId, offer.Select(o => (o.Slot, o.Buff.Id)))]
            : [];

    /// <summary>DungeonBuffSelect_RQ 0x6106 {dungeon i32, slot i32}: the slot's buff on the player for the run (see the header).</summary>
    internal List<byte[]> SelectDungeonBuff(byte[] plain, long now)
    {
        int dungeonId = BitConverter.ToInt32(plain, 2), slot = BitConverter.ToInt32(plain, 6);
        byte[] Reply(int result) => DailyDungeons.DungeonBuffSelectReply(result, dungeonId, slot);
        if (copy is not { } c || c.DungeonId != dungeonId || CopyBuffOffer(now) is not { Count: > 0 } offer)
            return [Reply(EResult.DungeonBuff_Fail_NotInDungeonBuffMap)];
        if (c.Phase != "Prepare") return [Reply(EResult.DungeonBuff_Fail_NotPreparingState)];
        if (offer.FirstOrDefault(o => o.Slot == slot) is not { Buff: { } buff }) return [Reply(EResult.DungeonBuff_Fail_InvalidDungeonBuffData)];
        if (AbnormalTable.Embedded.Of(buff.AbnormalId) is null) return [Reply(EResult.DungeonBuff_Fail_InvalidAbnormalData)];
        var replies = new List<byte[]> { Reply(0) };
        if (c.BuffChosen is { } before && before != slot && offer.FirstOrDefault(o => o.Slot == before) is { Buff: { } old })
            replies.AddRange(RemoveScriptedBuffs(old.AbnormalId));
        if (c.BuffChosen != slot) replies.AddRange(AddScriptedBuff(buff.AbnormalId, -1, now));
        c.BuffChosen = slot;
        Note($"dungeon buff: slot {slot} = DungeonBuff {buff.Id} (abnormal {buff.AbnormalId}) in {dungeonId}");
        return replies;
    }
}
