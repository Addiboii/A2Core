using Aion.Protocol.Native;

namespace Aion.World;

// Monster loot. A kill rolls the monster's reward tables (LootTables); the drops lie on the ground around the
// corpse for the killer (0x561e DropItem_NT, owner Player), who picks them up with 0x5620 ItemLooting_RQ:
// the items are granted (stored), answered 0x5621 ItemLooting_RS per drop, removed with 0x561f
// DropItemRemove_NT, and auto-use items (pet souls) are used at once. Unlooted drops vanish after
// DropLifetimeMs. Opcodes: catalogue 340-343 + 0x54ca (the 0x56 run, OBSERVED around 0x5610/0x5612/0x5646);
// layouts are the usmap structs, no official sample (CANDIDATE).
public sealed partial class GameSession
{
    sealed record GroundDrop(int Key, long Uid, int ItemId, long Count, Vector3d Center, Vector3d At, long ExpiresAt);

    readonly Dictionary<long, GroundDrop> drops = [];
    int dropCounter;
    public const int DropLifetimeMs = 60_000, FirstDropKey = 950_000;
    /// <summary>A drop is picked up within this distance (cm), with room for latency.</summary>
    public const double LootRange = 1500;
    const byte IncreaseDropItemGet = 5;   // EIncreaseReason kDropItemGet
    public IReadOnlyCollection<long> GroundDrops => drops.Keys;

    /// <summary>The loot of a killed monster: the drops for the killer (0x561e), or nothing.</summary>
    List<byte[]> DropLoot(Target target, long now)
    {
        // Drop events (SharedWorld.DropRate) roll the table more often: x2.5 = two rolls and a third half the time.
        var rate = options.World?.DropRate ?? 1;
        var fraction = rate - Math.Floor(rate);
        var rolls = (int)Math.Floor(rate) + (fraction > 0 && rng.NextDouble() < fraction ? 1 : 0);
        var rolled = new List<(int, long)>();
        for (var i = 0; i < rolls; i++) rolled.AddRange(LootTables.Embedded.Roll(target.NpcId, profileRace, rng));
        if (rolled.Count == 0) return [];
        var placed = new List<DropItem>();
        foreach (var (item, count) in rolled)
        {
            dropCounter++;
            var key = FirstDropKey + dropCounter % 40_000;
            var uid = ((long)playerKey << 32) | (uint)dropCounter;
            var angle = rng.NextDouble() * 2 * Math.PI;
            var radius = 60 + rng.NextDouble() * 90;
            var at = target.Position with { X = target.Position.X + Math.Cos(angle) * radius, Y = target.Position.Y + Math.Sin(angle) * radius };
            var drop = new GroundDrop(key, uid, item, count, target.Position, at, now + DropLifetimeMs);
            drops[uid] = drop;
            placed.Add(new DropItem(key, target.Position, at, uid, item, (int)Math.Min(int.MaxValue, count), selected?.WireId ?? playerKey,
                now + DropLifetimeMs, (byte)profileRace));
        }
        return [LootPackets.DropItems(placed)];
    }

    /// <summary>0x5620 ItemLooting_RQ {array of drop uids (i64), auto looting bool}.</summary>
    async Task<List<byte[]>> LootAsync(byte[] plain, long now, CancellationToken ct)
    {
        var uids = new List<long>();
        bool auto;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 64; i++) uids.Add((long)reader.U64());
            auto = reader.U8() != 0;
        }
        catch (ProtocolException) { return []; }
        var replies = new List<byte[]>();
        var taken = new List<GroundDrop>();
        foreach (var uid in uids.Distinct())
        {
            var ok = drops.TryGetValue(uid, out var drop) && now < drop.ExpiresAt && !dead
                && (position is not { } p || (p.X - drop.At.X) * (p.X - drop.At.X) + (p.Y - drop.At.Y) * (p.Y - drop.At.Y) <= LootRange * LootRange);
            if (ok) taken.Add(drop!);
            else replies.Add(LootPackets.LootReply(1, uid, auto));
        }
        if (taken.Count == 0) return replies;
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, taken.Select(d => (d.ItemId, d.Count)).ToList(), ct) ? [[]] : []);
            if (granted.Count == 0) return [.. replies, .. taken.Select(d => LootPackets.LootReply(1, d.Uid, auto))];
        }
        foreach (var d in taken) drops.Remove(d.Uid);
        replies.AddRange(taken.Select(d => LootPackets.LootReply(0, d.Uid, auto)));
        replies.Add(LootPackets.DropsRemoved(taken.Select(d => (d.Key, d.Uid, (long)(selected?.WireId ?? playerKey), auto)).ToList()));
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseDropItemGet, 0));
        // Pet souls (and pets) are used as soon as they are acquired (Item.dat AutoUseWhenAcquire).
        foreach (var d in taken)
            if (Boxes.Use(d.ItemId) is { AutoUse: true, Effect: "GetVehicle" or "GetVehicleSoul" })
                foreach (var key in storedItems.Where(s => s.Value.TemplateId == d.ItemId && s.Value.Location == "bag").Select(s => s.Key).ToList())
                    replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        return replies;
    }

    /// <summary>Drops nobody took vanish (0x561f).</summary>
    List<byte[]> DropTick(long now)
    {
        if (drops.Count == 0) return [];
        var gone = drops.Values.Where(d => now >= d.ExpiresAt).ToList();
        if (gone.Count == 0) return [];
        foreach (var d in gone) drops.Remove(d.Uid);
        return [LootPackets.DropsRemoved(gone.Select(d => (d.Key, d.Uid, 0L, false)).ToList())];
    }
}
