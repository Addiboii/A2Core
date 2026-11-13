using Aion.Protocol.Native;

namespace Aion.World;

// Monster loot. A kill rolls the monster's reward tables (LootTables); the drops lie on the ground around the
// corpse for the killer (0x561e DropItem_NT, owner Player), who picks them up with 0x5620 ItemLooting_RQ:
// the items are granted (stored), answered 0x5621 ItemLooting_RS per drop, removed with 0x561f
// DropItemRemove_NT, and auto-use items (pet souls) are used at once. Unlooted drops vanish after
// DropLifetimeMs. Layouts checked against the official recordings of 2026-11-06 (see LootPackets).
public sealed partial class GameSession
{
    sealed record GroundDrop(int Key, long Uid, int ItemId, long Count, Vector3d Center, Vector3d At, long ExpiresAt);

    readonly Dictionary<long, GroundDrop> drops = [];
    int dropCounter;
    /// <summary>Official: ownership runs out 2 minutes after the kill.</summary>
    public const int DropLifetimeMs = 120_000;
    /// <summary>A drop is picked up within this distance (cm), with room for latency.</summary>
    public const double LootRange = 1500;
    const byte IncreaseDropItemGet = 5;   // EIncreaseReason kDropItemGet
    // Kill-Kina tunables (USER 2026-11-08). Base Kina per monster level, times the rank multiplier, times the drop rate, with
    // +/-25% jitter; at least KinaMinPerKill. Elites and Heroes/bosses pay much more. PROVISIONAL (no official Kina-by-level table).
    public const int KinaPerLevel = 4;
    public const int KinaEliteMultiplier = 4;
    public const int KinaHeroMultiplier = 10;
    public const long KinaMinPerKill = 2;
    public IReadOnlyCollection<long> GroundDrops => drops.Keys;

    /// <summary>The loot of a killed monster: the drops for the killer (0x561e), or nothing.</summary>
    List<byte[]> DropLoot(Target target, long now)
    {
        // The base rate (Aion:Rates:Drops) and drop events (SharedWorld.DropRate) roll the table more often: x2.5 = two rolls
        // and a third half the time. Kinah in the loot follows Aion:Rates:Kinah.
        var rate = (options.World?.Rates.Drops ?? 1) * (options.World?.DropRate ?? 1);
        var rolls = WorldRates.Rolls(rate, rng);
        var kinahRate = options.World?.Rates.Kinah ?? 1;
        var rolled = new List<(int, long)>();
        // Level-scaled basic drops (RewardData GroupLevelscaleRewardName -> LevelScaleDropGroup) by the killer's level (LootTables.ScaledLevel).
        for (var i = 0; i < rolls; i++) rolled.AddRange(LootTables.Embedded.Roll(target.NpcId, profileRace, rng, level));
        // A dungeon's final boss: its source's extra drops (gamedata/drops, aion2hub.com rates, UNOFFICIAL), once per kill.
        if (instanceKey != 0 && MapEventData.DungeonIdOfMap(worldMap) is > 0 and var dungeonId && MapEventData.DungeonOfMap(worldMap) is { CubeBoss: var boss }
            && boss == target.NpcId)
            rolled.AddRange(DropDB.Embedded.Roll(dungeonId, rng));
        // Kina is not dropped: the official server adds it to the stack at the kill (ItemModificationList_NT update with
        // kItemModifiedInCombat 11, 76 official samples, no ground drop or loot request), so the loot pop-up shows it.
        // Kinah in the loot follows Aion:Rates:Kinah (GitHub 30452be).
        pendingKillKina += WorldRates.Scale(rolled.Where(r => r.Item1 == KinaItemId).Sum(r => r.Item2), kinahRate);
        rolled.RemoveAll(r => r.Item1 == KinaItemId);
        // Base kill Kina by level and rank (USER 2026-11-08: "monsters don't drop kina" - their per-NpcId loot tables rarely
        // carry it). Every killed monster yields some Kina through the same reason-11 stack update + pop-up, scaled by the active
        // drop rate. Not the dummy or a hit-gauge target.
        pendingKillKina += KillKina(target, rate);
        if (pendingKillKina > 0) killWorkDue = true;
        if (rolled.Count == 0) return [];
        var placed = new List<DropItem>();
        foreach (var (item, count) in rolled)
        {
            dropCounter++;
            var key = target.Key;   // official: the drop carries the dead NPC's key
            var uid = ((long)playerKey << 32) | (uint)dropCounter;
            var angle = rng.NextDouble() * 2 * Math.PI;
            // The NPC's drop spread (NpcData DropRadiusMin..Max, cm); 60-150 cm when the data has none (OURS).
            var (radiusMin, radiusMax) = LootTables.Embedded.DropRadius(target.NpcId) ?? (60, 150);
            var radius = radiusMin + rng.NextDouble() * (radiusMax - radiusMin);
            var at = target.Position with { X = target.Position.X + Math.Cos(angle) * radius, Y = target.Position.Y + Math.Sin(angle) * radius };
            var drop = new GroundDrop(key, uid, item, count, target.Position, at, now + DropLifetimeMs);
            drops[uid] = drop;
            placed.Add(new DropItem(key, target.Position, at, uid, item, (int)Math.Min(int.MaxValue, count), selected?.WireId ?? playerKey,
                now + DropLifetimeMs, LootPackets.AnyRace));
        }
        return [.. placed.Select(d => LootPackets.DropItems([d]))];   // one drop per message (OBSERVED, 184 of 184)
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
            else
            {
                replies.Add(LootPackets.LootReply(LootPackets.DropInvalidId, uid, false));
                // A drop that no longer exists (taken, expired) is removed on the client too, with the reply as official (0x5621 always
                // comes with 0x561f, 53 of 53): live 2026-11-07 the client asked for a gone drop again and again (a ghost on the ground).
                if (drop is null || now >= drop.ExpiresAt) { drops.Remove(uid); replies.Add(LootPackets.DropsRemoved([(drop?.Key ?? 0, uid, 0L)])); }
            }
        }
        if (taken.Count == 0) return replies;
        // A full bag leaves the drops on the ground (GameSession.BagRoom.cs; DropItem_NotEnoughInventory, CANDIDATE: not recorded).
        if (!BagRoomFor(taken.Select(d => (d.ItemId, d.Count))))
            return [.. replies, .. taken.Select(d => LootPackets.LootReply(EResult.DropItem_NotEnoughInventory, d.Uid, false))];
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, taken.Select(d => (d.ItemId, d.Count)).ToList(), ct) ? [[]] : []);
            if (granted.Count == 0) return [.. replies, .. taken.Select(d => LootPackets.LootReply(1, d.Uid, false))];
        }
        foreach (var d in taken) drops.Remove(d.Uid);
        // OBSERVED order (2026-11-04 192703 i49919-49952): the bag changes first, then per drop its reply and its removal.
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseDropItemGet, 0));
        foreach (var d in taken)
        {
            replies.Add(LootPackets.LootReply(0, d.Uid, false));   // OBSERVED: the reply's auto flag is 0 in all 33 official (0x561f carries it)
            replies.Add(LootPackets.DropsRemoved([(d.Key, d.Uid, (long)(selected?.WireId ?? playerKey))], auto));
        }
        // Pet souls (and pets) are used as soon as they are acquired (Item.dat AutoUseWhenAcquire).
        foreach (var d in taken)
            if (Boxes.Use(d.ItemId) is { AutoUse: true, Effect: "GetVehicle" or "GetVehicleSoul" })
                foreach (var key in storedItems.Where(s => s.Value.TemplateId == d.ItemId && s.Value.Location == "bag").Select(s => s.Key).ToList())
                    replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        return replies;
    }

    const byte IncreaseItemModifiedInCombat = 11;   // EIncreaseReason kItemModifiedInCombat (OBSERVED with the kill's Kina)
    long pendingKillKina;

    /// <summary>Base Kina a killed monster yields, by its level and rank, scaled by the drop rate, with +/-25% jitter. 0 for the
    /// training dummy and hit-gauge targets (totems). Deterministic given the session's rng (tests seed it).</summary>
    long KillKina(Target target, double rate)
    {
        if (!target.Hostile || target.Key == Combat.TrainingDummyKey || target.HitGauge) return 0;
        var rankMul = target.Rank switch { NpcRank.Hero => KinaHeroMultiplier, NpcRank.Elite => KinaEliteMultiplier, _ => 1 };
        var baseKina = Math.Max(1, target.Level) * (long)KinaPerLevel * rankMul * Math.Max(1.0, rate);
        var jitter = 0.75 + rng.NextDouble() * 0.5;
        return Math.Max(KinaMinPerKill, (long)Math.Round(baseKina * jitter));
    }

    /// <summary>Test seam: the base kill Kina for a target at a drop rate (see KillKina).</summary>
    internal long KillKinaForChecks(Target target, double rate) => KillKina(target, rate);

    /// <summary>The kills' Kina (tradable), granted after the cast (storage call) as one stack update with reason 11, as official.</summary>
    async Task<List<byte[]>> KillKinaAsync(CancellationToken ct)
    {
        if (pendingKillKina <= 0 || Store is not { } store || CharacterId is not { } character) { pendingKillKina = 0; return []; }
        var amount = pendingKillKina;
        pendingKillKina = 0;
        // Paid as tradable Kina (non-binding, usable on the market; USER 2026-11-08), not bound Kina.
        var granted = await Stored(async () => await store.GrantLootAsync(character, [(NonBindingKinaItemId, amount)], ct) ? [[]] : []);
        return granted.Count == 0 ? [] : await InventoryChangesAsync(ct, IncreaseItemModifiedInCombat, 0);
    }

    /// <summary>Drops nobody took vanish (0x561f).</summary>
    List<byte[]> DropTick(long now)
    {
        if (drops.Count == 0) return [];
        var gone = drops.Values.Where(d => now >= d.ExpiresAt).ToList();
        if (gone.Count == 0) return [];
        foreach (var d in gone) drops.Remove(d.Uid);
        return [LootPackets.DropsRemoved(gone.Select(d => (d.Key, d.Uid, 0L)).ToList())];
    }
}
