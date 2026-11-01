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
    internal sealed record GroundDrop(int Key, long Uid, int ItemId, long Count, Vector3d Center, Vector3d At, long ExpiresAt,
        byte OwnerType = 2, long Owner = 0);

    readonly Dictionary<long, GroundDrop> drops = [];
    int dropCounter;
    /// <summary>
    /// Drop actor keys: one server-wide sequence in their own block. Drops are shown to the whole map, and trigger actors
    /// (WorldObjects.FirstTriggerKey, up to 93 per map) and the boss challenge use 950,000+, so a per-session counter from
    /// 950,000 gave two players' drops, or a drop and a live trigger actor, the same key on one client.
    /// </summary>
    public const int DropLifetimeMs = 60_000, FirstDropKey = 960_000, DropKeys = 30_000;
    static int dropKeySequence;
    static int NextDropKey() => FirstDropKey + (int)((uint)Interlocked.Increment(ref dropKeySequence) % DropKeys);
    /// <summary>A drop is picked up within this distance (cm), with room for latency.</summary>
    public const double LootRange = 1500;
    const byte IncreaseDropItemGet = 5;   // EIncreaseReason kDropItemGet
    public IReadOnlyCollection<long> GroundDrops => drops.Keys;

    internal void RegisterGroundDrop(GroundDrop drop) => drops[drop.Uid] = drop;
    internal void RemoveGroundDrop(long uid) => drops.Remove(uid);

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
        if (Social is { } social && worldPlayer is not null && social.PartyOf(worldPlayer) is { } party)
        {
            var force = social.ForceOf(worldPlayer);
            if (force is not null && force.Parties.Sum(p => p.Members.Count) > 1)
                return DropLootParty(party, target, now, rolled, []);
            if (party.Members.Count > 1)
                return DropLootParty(party, target, now, rolled, []);
        }
        return DropLootSolo(target, now, rolled, []);
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
                && (position is not { } p || (p.X - drop.At.X) * (p.X - drop.At.X) + (p.Y - drop.At.Y) * (p.Y - drop.At.Y) <= LootRange * LootRange)
                && CanPickupDrop(drop);
            if (ok) taken.Add(drop!);
            else replies.Add(LootPackets.LootReply(1, uid, auto));
        }
        if (taken.Count == 0) return replies;
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, taken.Select(d => (d.ItemId, d.Count)).ToList(), ct) ? [[]] : []);
            if (granted.Count == 0) return [.. replies, .. taken.Select(d => LootPackets.LootReply(1, d.Uid, auto))];
        }
        foreach (var d in taken)
        {
            drops.Remove(d.Uid);
            RemoveSharedDrop(d.Uid);
        }
        replies.AddRange(taken.Select(d => LootPackets.LootReply(0, d.Uid, auto)));
        replies.Add(LootPackets.DropsRemoved(taken.Select(d => (d.Key, d.Uid, (long)(selected?.WireId ?? playerKey), auto)).ToList()));
        var inventory = await InventoryChangesAsync(ct, IncreaseDropItemGet, 0);
        var autoUse = await AutoUseAcquiredItemsAsync(taken.Select(d => d.ItemId), now, ct);
        // Official solo pickup order after 0x5621: 0x561f, 0x561b, then 0x5616 when auto-use applies.
        replies.AddRange(inventory);
        replies.AddRange(autoUse);
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
