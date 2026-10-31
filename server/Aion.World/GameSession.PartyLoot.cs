using Aion.Protocol.Native;

namespace Aion.World;

// Party loot: distribution modes (Manual, Random, Sequence, Leader), special dice/bid rolls for high-grade drops,
// RollDice_RQ (roll / greed) and RollDiceWithdraw_RQ (pass). State in SharedWorld.Social under the world gate.
public sealed partial class GameSession
{
    public const long PartyLootRollMs = 60_000;
    public const byte DropOwnerParty = 1, DropOwnerPlayer = 2;
    const byte DecreaseBid = 15;   // EDecreaseReason (kinah bid escrow)

    /// <summary>Party members on the same map within loot range (same as solo pickup).</summary>
    List<WorldPlayer> PartyLootEligible(Party party)
    {
        if (worldPlayer?.Map is not { } map || position is not { } at) return [];
        return LootRecipients(party).Where(m => m.Map == map && m != worldPlayer
            && (m.Position.X - at.X) * (m.Position.X - at.X) + (m.Position.Y - at.Y) * (m.Position.Y - at.Y) <= LootRange * LootRange).ToList();
    }

    List<byte[]> DropLootParty(Party party, Target target, long now, List<(int ItemId, long Count)> rolled, List<byte[]> replies)
    {
        if (Social is not { } social || worldPlayer is null) return replies;
        var eligible = PartyLootEligible(party);
        if (eligible.Count == 0 && party.Members.Count < 2) return DropLootSolo(target, now, rolled, replies);

        foreach (var (item, count) in rolled)
        {
            var grade = PartyLootGrades.Grade(item);
            var special = grade >= party.SpecialDistributionGrade && party.SpecialDistribution is not PartyOptions.SpecialNormal;
            if (special && party.SpecialDistribution == PartyOptions.SpecialBid)
                replies.AddRange(StartBidSession(party, social, target, item, (int)Math.Min(int.MaxValue, count), now, eligible));
            else if (special)
                replies.AddRange(StartDiceRoll(party, social, target, item, (int)Math.Min(int.MaxValue, count), now, eligible));
            else
                replies.AddRange(PlacePartyDrop(party, target, item, count, now, eligible));
        }
        return replies;
    }

    List<byte[]> DropLootSolo(Target target, long now, List<(int ItemId, long Count)> rolled, List<byte[]> replies)
    {
        var placed = new List<DropItem>();
        foreach (var (item, count) in rolled)
        {
            dropCounter++;
            var key = NextDropKey();
            var uid = ((long)playerKey << 32) | (uint)dropCounter;
            var angle = rng.NextDouble() * 2 * Math.PI;
            var radius = 60 + rng.NextDouble() * 90;
            var at = target.Position with { X = target.Position.X + Math.Cos(angle) * radius, Y = target.Position.Y + Math.Sin(angle) * radius };
            var ground = new GroundDrop(key, uid, item, count, target.Position, at, now + DropLifetimeMs);
            drops[uid] = ground;
            placed.Add(new DropItem(key, target.Position, at, uid, item, (int)Math.Min(int.MaxValue, count), selected?.WireId ?? playerKey,
                now + DropLifetimeMs, (byte)profileRace));
        }
        if (placed.Count > 0)
        {
            replies.Add(LootPackets.DropItems(placed));
            BroadcastDropVisual(placed);
        }
        return replies;
    }

    void ShareGroundDrop(Party party, GroundDrop ground, DropItem wire)
    {
        RegisterGroundDrop(ground);
        // Only party members on the killer's map can see or pick up the drop (avoids ghost uids elsewhere).
        foreach (var m in LootRecipients(party).Where(m => m.Map == worldPlayer?.Map && m != worldPlayer))
            m.Session.RegisterGroundDrop(ground);
        BroadcastDropVisual([wire]);
    }

    void BroadcastDropVisual(IReadOnlyList<DropItem> drops)
    {
        if (drops.Count == 0 || worldPlayer?.Map is not { } map) return;
        map.BroadcastAll([LootPackets.DropItems(drops)], worldPlayer);
    }

    void RemoveSharedDrop(long uid)
    {
        if (Social is not { } social || worldPlayer is null || social.PartyOf(worldPlayer) is not { } party) return;
        foreach (var m in LootRecipients(party).Where(m => m.Map == worldPlayer?.Map))
            if (m != worldPlayer) m.Session.RemoveGroundDrop(uid);
    }

    bool CanPickupDrop(GroundDrop drop)
    {
        if (drop.OwnerType == DropOwnerParty)
            return Social?.PartyOf(worldPlayer!) is { } party && party.Key == drop.Owner && party.Distribution == PartyOptions.DistributionManual;
        if (drop.OwnerType == DropOwnerForce)
            return Social?.ForceOf(worldPlayer!) is { } force && force.Key == drop.Owner && force.Parties.Any(p => p.Distribution == PartyOptions.DistributionManual);
        if (drop.OwnerType == DropOwnerPlayer && drop.Owner != 0)
            return (selected?.WireId ?? playerKey) == drop.Owner;
        return true;
    }

    List<byte[]> PlacePartyDrop(Party party, Target target, int itemId, long count, long now, List<WorldPlayer> eligible)
    {
        var winner = party.Distribution switch
        {
            PartyOptions.DistributionLeader => party.Leader,
            PartyOptions.DistributionRandom => PickRandom([.. LootRecipients(party).Where(m => m.Map == worldPlayer?.Map)], rng),
            PartyOptions.DistributionSequence => NextSequenceMember(party),
            _ => null,
        };
        dropCounter++;
        var key = NextDropKey();
        var uid = ((long)party.Key << 32) | (uint)dropCounter;
        var angle = rng.NextDouble() * 2 * Math.PI;
        var radius = 60 + rng.NextDouble() * 90;
        var at = target.Position with { X = target.Position.X + Math.Cos(angle) * radius, Y = target.Position.Y + Math.Sin(angle) * radius };
        var ownerType = DropOwnerPlayer;
        long owner = selected?.WireId ?? playerKey;
        var inForce = Social?.ForceOf(worldPlayer!) is not null;
        if (party.Distribution == PartyOptions.DistributionManual || winner is null)
        {
            if (inForce && Social!.ForceOf(worldPlayer!) is { } force)
            {
                ownerType = DropOwnerForce;
                owner = force.Key;
            }
            else
            {
                ownerType = DropOwnerParty;
                owner = party.Key;
            }
        }
        else
        {
            owner = winner.Session.selected?.WireId ?? winner.Key;
            NotifyGroupLoot(winner, itemId, party);
        }
        var ground = new GroundDrop(key, uid, itemId, count, target.Position, at, now + DropLifetimeMs, ownerType, owner);
        var drop = new DropItem(key, target.Position, at, uid, itemId, (int)Math.Min(int.MaxValue, count), owner, now + DropLifetimeMs, (byte)profileRace, ownerType);
        ShareGroundDrop(party, ground, drop);
        return [LootPackets.DropItems([drop])];
    }

    static WorldPlayer? PickRandom(List<WorldPlayer> members, Random rng) =>
        members.Count == 0 ? null : members[rng.Next(members.Count)];

    WorldPlayer NextSequenceMember(Party party)
    {
        var onMap = party.Members.Where(m => m.Map == worldPlayer?.Map).ToList();
        if (onMap.Count == 0) return party.Leader;
        var idx = party.SequenceIndex % onMap.Count;
        party.SequenceIndex = (party.SequenceIndex + 1) % onMap.Count;
        return onMap[idx];
    }

    List<byte[]> StartBidSession(Party party, SocialState social, Target target, int itemId, int count, long now, List<WorldPlayer> eligible)
    {
        var uid = social.NextBidUid();
        var bid = new PartyBidSession
        {
            BidUid = uid, PartyKey = party.Key, ItemId = itemId, Count = count, ExpiresAtMs = now + PartyLootRollMs,
        };
        foreach (var m in LootRecipients(party).Where(m => m.Map == worldPlayer?.Map)) bid.Eligible.Add(m.Key);
        social.BidSessions[uid] = bid;
        var begin = DistributionPackets.BeginItemBidding(uid, itemId, count, bid.ExpiresAtMs);
        foreach (var m in LootRecipients(party)) m.Send(begin);
        return [];
    }

    List<byte[]> StartDiceRoll(Party party, SocialState social, Target target, int itemId, int count, long now, List<WorldPlayer> eligible)
    {
        var uid = social.NextDiceUid();
        var roll = new PartyDiceRoll
        {
            DiceUid = uid, PartyKey = party.Key, ItemId = itemId, Count = count, KillerKey = playerKey,
            Center = target.Position, At = target.Position, ExpiresAtMs = now + PartyLootRollMs,
        };
        foreach (var m in LootRecipients(party).Where(m => m.Map == worldPlayer?.Map)) roll.Eligible.Add(m.Key);
        foreach (var key in roll.Eligible) roll.Responses[key] = null;
        social.DiceRolls[uid] = roll;
        var begin = DistributionPackets.BeginRollingDice(uid, itemId, count, roll.ExpiresAtMs);
        foreach (var m in LootRecipients(party)) m.Send(begin);
        return [];
    }

    async Task<List<byte[]>?> PartyLootRequestAsync(byte[] plain, long now, CancellationToken ct)
    {
        var header = Opcode(plain);
        // Live dev+0x157 skill charging reuses 0x381d/0x381f with different bodies (step+skill i32… / empty stop).
        // Party roll withdraw is opcode + i64 dice uid (10 bytes); charging end is 12 bytes.
        if (header == DistributionOpcodes.RollDiceRq && plain.Length != 10) return null;
        if (header == DistributionOpcodes.RollDiceWithdrawRq && plain.Length != 10) return null;
        return header switch
        {
            DistributionOpcodes.RollDiceRq => RollDiceAsync(plain, now),
            DistributionOpcodes.RollDiceWithdrawRq => RollDiceWithdrawAsync(plain, now),
            DistributionOpcodes.BidItemRq => await BidItemAsync(plain, now, ct),
            DistributionOpcodes.BidItemWithdrawRq => await BidItemWithdrawAsync(plain, now, ct),
            _ => null,
        };
    }

    List<byte[]> RollDiceAsync(byte[] plain, long now)
    {
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null) return [];
        var diceUid = BitConverter.ToInt64(plain, 2);
        if (!social.DiceRolls.TryGetValue(diceUid, out var roll) || roll.Resolved || now >= roll.ExpiresAtMs
            || !roll.Eligible.Contains(playerKey)) return [DistributionPackets.RollDiceReply(1, diceUid, 0)];
        if (roll.Responses.TryGetValue(playerKey, out var existing) && existing is not null)
            return [DistributionPackets.RollDiceReply(1, diceUid, 0)];
        var value = rng.Next(1, 101);
        roll.Responses[playerKey] = value;
        var notice = DistributionPackets.RollDiceNotice(roll.ItemId, ServerId, StoredName, value);
        foreach (var m in DistributionMembersOf(roll.PartyKey)) m.Send(notice);
        var replies = new List<byte[]> { DistributionPackets.RollDiceReply(0, diceUid, value) };
        if (AllAnswered(roll)) replies.AddRange(ResolveDiceRoll(social, roll, now));
        return replies;
    }

    List<byte[]> RollDiceWithdrawAsync(byte[] plain, long now)
    {
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null) return [];
        var diceUid = BitConverter.ToInt64(plain, 2);
        if (!social.DiceRolls.TryGetValue(diceUid, out var roll) || roll.Resolved || now >= roll.ExpiresAtMs
            || !roll.Eligible.Contains(playerKey)) return [DistributionPackets.RollDiceWithdrawReply(1, diceUid)];
        if (roll.Responses.TryGetValue(playerKey, out var existing) && existing is not null)
            return [DistributionPackets.RollDiceWithdrawReply(1, diceUid)];
        roll.Responses[playerKey] = -1;
        var pass = DistributionPackets.RollDiceWithdraw(diceUid, roll.ItemId, ServerId, StoredName);
        foreach (var m in DistributionMembersOf(roll.PartyKey)) m.Send(pass);
        var replies = new List<byte[]> { DistributionPackets.RollDiceWithdrawReply(0, diceUid) };
        if (AllAnswered(roll)) replies.AddRange(ResolveDiceRoll(social, roll, now));
        return replies;
    }

    IEnumerable<WorldPlayer> DistributionMembersOf(long partyKey)
    {
        if (Social is not { } social || !social.Parties.TryGetValue(partyKey, out var party)) yield break;
        foreach (var m in LootRecipients(party)) yield return m;
    }

    static bool AllAnswered(PartyDiceRoll roll)
    {
        foreach (var key in roll.Eligible)
            if (roll.Responses.TryGetValue(key, out var v) && v is null) return false;
        return roll.Eligible.Count > 0;
    }

    List<byte[]> ResolveDiceRoll(SocialState social, PartyDiceRoll roll, long now)
    {
        if (roll.Resolved) return [];
        roll.Resolved = true;
        social.DiceRolls.Remove(roll.DiceUid);
        var rolls = roll.Eligible.Select(k => (Key: k, Value: roll.Responses.GetValueOrDefault(k))).Where(r => r.Value is > 0).ToList();
        WorldPlayer? winner = null;
        if (rolls.Count > 0)
        {
            var best = rolls.Max(r => r.Value!.Value);
            var tied = rolls.Where(r => r.Value == best).Select(r => r.Key).ToList();
            var winKey = tied.Count == 1 ? tied[0] : tied[rng.Next(tied.Count)];
            winner = World?.Online.GetValueOrDefault(winKey);
        }
        var packets = new List<byte[]>();
        if (winner is null)
        {
            packets.Add(DistributionPackets.RollDiceResultFail(roll.DiceUid, 1));
            foreach (var m in DistributionMembersOf(roll.PartyKey)) m.Send(packets[0]);
            return packets;
        }
        var result = DistributionPackets.RollDiceResult(roll.DiceUid, roll.ItemId, ServerId, winner.Name);
        foreach (var m in DistributionMembersOf(roll.PartyKey)) m.Send(result);
        packets.Add(result);
        _ = winner.Session.InvokeAsync(async _ =>
        {
            var granted = await winner.Session.Stored(async () =>
                winner.Session.Store is { } store && winner.CharacterId is { } character
                    && await store.GrantLootAsync(character, [(roll.ItemId, roll.Count)], default) ? [[]] : []);
            if (granted.Count == 0) return (false, []);
            var inv = await winner.Session.InventoryChangesAsync(default, IncreaseDropItemGet, 0);
            if (social.Parties.TryGetValue(roll.PartyKey, out var lootParty))
                NotifyGroupLoot(winner, roll.ItemId, lootParty);
            return (true, inv);
        });
        return packets;
    }

    async Task<List<byte[]>> BidItemAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 18 || Social is not { } social || worldPlayer is null) return [];
        var bidUid = BitConverter.ToInt64(plain, 2);
        var price = BitConverter.ToInt64(plain, 10);
        if (!social.BidSessions.TryGetValue(bidUid, out var bid) || bid.Resolved || now >= bid.ExpiresAtMs
            || !bid.Eligible.Contains(playerKey) || price <= bid.HighPrice)
            return [DistributionPackets.BidItemReply(1, bidUid, price)];
        if (Store is not { } store || CharacterId is not { } character || CostStacks(KinaItemId, price) is not { } costs)
            return [DistributionPackets.BidItemReply(2, bidUid, price)];
        var paid = await Stored(async () => await store.ShopBuyAsync(character, costs, [], [], ct) ? [[]] : []);
        if (paid.Count == 0) return [DistributionPackets.BidItemReply(2, bidUid, price)];
        if (bid.HighBidderKey is { } prev && prev != playerKey)
            await RefundBidEscrowAsync(bid, prev, ct);
        bid.HighBidderKey = playerKey;
        bid.HighPrice = price;
        bid.Escrow[playerKey] = price;
        var notice = DistributionPackets.BidItemNotice(bid.ItemId, ServerId, StoredName);
        var win = DistributionPackets.BidItemWin(bidUid, bid.ItemId, price);
        foreach (var m in DistributionMembersOf(bid.PartyKey)) { m.Send(notice); m.Send(win); }
        return [DistributionPackets.BidItemReply(0, bidUid, price), .. await InventoryChangesAsync(ct, 0, DecreaseBid)];
    }

    async Task<List<byte[]>> BidItemWithdrawAsync(byte[] plain, long now, CancellationToken ct)
    {
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null) return [];
        var bidUid = BitConverter.ToInt64(plain, 2);
        if (!social.BidSessions.TryGetValue(bidUid, out var bid) || bid.Resolved || now >= bid.ExpiresAtMs
            || !bid.Eligible.Contains(playerKey) || !bid.Escrow.ContainsKey(playerKey))
            return [DistributionPackets.BidItemWithdrawReply(1, bidUid)];
        await RefundBidEscrowAsync(bid, playerKey, ct);
        if (bid.HighBidderKey == playerKey)
        {
            bid.HighBidderKey = null;
            bid.HighPrice = 0;
        }
        var pass = DistributionPackets.BidItemWithdraw(bidUid, bid.ItemId, ServerId, StoredName);
        foreach (var m in DistributionMembersOf(bid.PartyKey)) m.Send(pass);
        return [DistributionPackets.BidItemWithdrawReply(0, bidUid), .. await InventoryChangesAsync(ct, 0, 0)];
    }

    async Task RefundBidEscrowAsync(PartyBidSession bid, int memberKey, CancellationToken ct)
    {
        if (!bid.Escrow.Remove(memberKey, out var amount) || amount <= 0) return;
        if (World?.Online.GetValueOrDefault(memberKey) is not { } who || who.Session.Store is not { } store
            || who.CharacterId is not { } character) return;
        await who.Session.Stored(async () =>
        {
            await store.GrantLootAsync(character, [(KinaItemId, amount)], ct);
            return [[]];
        });
        await who.Session.InventoryChangesAsync(ct, 0, 0);
    }

    List<byte[]> ResolveBidSession(SocialState social, PartyBidSession bid, long now)
    {
        if (bid.Resolved) return [];
        bid.Resolved = true;
        social.BidSessions.Remove(bid.BidUid);
        var packets = new List<byte[]>();
        if (bid.HighBidderKey is not { } winKey || World?.Online.GetValueOrDefault(winKey) is not { } winner)
        {
            packets.Add(DistributionPackets.BidItemResultFail(bid.BidUid, 1));
            foreach (var m in DistributionMembersOf(bid.PartyKey)) m.Send(packets[0]);
            return packets;
        }
        bid.Escrow.Remove(winKey);
        var participants = bid.Eligible.Count;
        var result = DistributionPackets.BidItemResult(bid.BidUid, bid.HighPrice, bid.ItemId, ServerId, winner.Name, participants, 0);
        foreach (var m in DistributionMembersOf(bid.PartyKey)) m.Send(result);
        packets.Add(result);
        _ = winner.Session.InvokeAsync(async _ =>
        {
            var granted = await winner.Session.Stored(async () =>
                winner.Session.Store is { } store && winner.CharacterId is { } character
                    && await store.GrantLootAsync(character, [(bid.ItemId, bid.Count)], default) ? [[]] : []);
            if (granted.Count == 0) return (false, []);
            var inv = await winner.Session.InventoryChangesAsync(default, IncreaseDropItemGet, 0);
            if (social.Parties.TryGetValue(bid.PartyKey, out var lootParty))
                NotifyGroupLoot(winner, bid.ItemId, lootParty);
            return (true, inv);
        });
        return packets;
    }

    void PartyLootTick(long now)
    {
        if (Social is not { } social || worldPlayer is null) return;
        foreach (var roll in social.DiceRolls.Values.Where(r => !r.Resolved && now >= r.ExpiresAtMs).ToList())
        {
            if (social.Parties.TryGetValue(roll.PartyKey, out var party) && party.Leader != worldPlayer) continue;
            foreach (var key in roll.Eligible.Where(k => roll.Responses.GetValueOrDefault(k) is null))
                roll.Responses[key] = -1;
            ResolveDiceRoll(social, roll, now);
        }
        foreach (var bid in social.BidSessions.Values.Where(b => !b.Resolved && now >= b.ExpiresAtMs).ToList())
        {
            if (social.Parties.TryGetValue(bid.PartyKey, out var party) && party.Leader != worldPlayer) continue;
            ResolveBidSession(social, bid, now);
        }
    }

    List<byte[]> SpecialDistributionWorldEntry()
    {
        if (Social is not { } social || worldPlayer is null || social.PartyOf(worldPlayer) is not { } party) return [];
        var entries = new List<SpecialDistributionEntry>();
        foreach (var roll in social.DiceRolls.Values.Where(r => r.PartyKey == party.Key && r.Eligible.Contains(playerKey) && !r.Resolved))
            entries.Add(new(roll.DiceUid, PartyOptions.SpecialDice, roll.ItemId, roll.Count, roll.ExpiresAtMs, roll.Responses.GetValueOrDefault(playerKey) ?? 0));
        foreach (var bid in social.BidSessions.Values.Where(b => b.PartyKey == party.Key && b.Eligible.Contains(playerKey) && !b.Resolved))
            entries.Add(new(bid.BidUid, PartyOptions.SpecialBid, bid.ItemId, bid.Count, bid.ExpiresAtMs, bid.HighPrice));
        return entries.Count == 0 ? [] : [DistributionPackets.SpecialDistributionList(entries)];
    }

    List<byte[]> PartyInfoUpdate(byte[] plain)
    {
        if (SocialRequests.ParsePartyInfoUpdate(plain) is not { } settings || Social is not { } s || worldPlayer is null
            || s.PartyOf(worldPlayer) is not { } party || party.Leader != worldPlayer)
            return [SocialPackets.PartyInfoUpdateReply(Failed)];
        ApplyPartySettings(party, settings);
        var notice = SocialPackets.PartyInfoUpdateNotice(Party.Limit, party.Distribution, party.SpecialDistribution, party.SpecialDistributionGrade);
        foreach (var m in party.Members) m.Send(notice);
        return [SocialPackets.PartyInfoUpdateReply(0), notice, SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create)];
    }

    static void ApplyPartySettings(Party party, PartySettingsRequest settings)
    {
        party.Distribution = settings.Distribution;
        party.SpecialDistribution = settings.SpecialDistribution;
        party.SpecialDistributionGrade = settings.SpecialDistributionGrade;
        party.JoinType = settings.JoinType;
    }

}
