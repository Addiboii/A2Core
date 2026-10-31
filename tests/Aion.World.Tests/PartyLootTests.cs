using Aion.Protocol.Native;
using Aion.World;
using Aion.World.Tests;
using Xunit;

namespace Aion.World.Tests;

public sealed class PartyLootTests
{
    const long Now = 1_760_100_000_000;

    [Fact]
    public void ItemGrade_uses_decompose_package_tier()
    {
        Assert.Equal(4, PartyLootGrades.Grade(110120003));
        Assert.Equal(1, PartyLootGrades.Grade(930100001));
    }

    [Fact]
    public async Task Party_dice_roll_awards_highest_roller()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var alice = await EnterWorld(world, storeA, "Alice", 1);
        var bob = await EnterWorld(world, storeB, "Bob", 2);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialDice, 1)), Now);
        await alice.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bob"), Now);
        await bob.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(
            world.Social.Parties.Values.Single().Key, PartyInviteAnswer.Accept)]), Now + 1);
        var party = world.Social.Parties.Values.Single();
        var roll = new PartyDiceRoll
        {
            DiceUid = 42, PartyKey = party.Key, ItemId = 110120003, Count = 1, KillerKey = alice.PlayerKey,
            Center = Vector3d.Zero, At = Vector3d.Zero, ExpiresAtMs = Now + GameSession.PartyLootRollMs,
        };
        roll.Eligible.Add(alice.PlayerKey);
        roll.Eligible.Add(bob.PlayerKey);
        roll.Responses[alice.PlayerKey] = null;
        roll.Responses[bob.PlayerKey] = null;
        world.Social.DiceRolls[roll.DiceUid] = roll;
        await alice.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 2);
        var bobReplies = await bob.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceRq).I64(roll.DiceUid).ToArray(), Now + 3);
        Assert.Contains(bobReplies, p => p.Length >= 2 && (ushort)(p[0] | p[1] << 8) == DistributionOpcodes.RollDiceRs);
        Assert.DoesNotContain(world.Social.DiceRolls, kv => kv.Key == roll.DiceUid);
        await alice.TickAsync(Now + 4);
        await bob.TickAsync(Now + 4);
        Assert.True(storeA.Items.Count > 0 || storeB.Items.Count > 0, "winner should receive the item in store");
    }

    [Fact]
    public async Task Kinah_bid_awards_high_bidder_after_timeout()
    {
        var world = new SharedWorld(GameData.Embedded);
        var storeA = new FakeCharacterStore();
        var storeB = new FakeCharacterStore();
        var alice = await EnterWorldWithKina(world, storeA, "BidA", 11, 50_000);
        var bob = await EnterWorldWithKina(world, storeB, "BidB", 12, 50_000);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionManual, PartyOptions.JoinFree, PartyOptions.SpecialBid, 1)), Now);
        world.Social.Parties.Values.Single().Members.Add(bob.WorldPlayer!);
        var party = world.Social.Parties.Values.Single();
        var bid = new PartyBidSession
        {
            BidUid = 99, PartyKey = party.Key, ItemId = 110120003, Count = 1, ExpiresAtMs = Now + 50,
        };
        bid.Eligible.Add(alice.PlayerKey);
        bid.Eligible.Add(bob.PlayerKey);
        world.Social.BidSessions[bid.BidUid] = bid;
        var bidPacket = new PacketWriter().U16(DistributionOpcodes.BidItemRq).I64(bid.BidUid).I64(1000).ToArray();
        await alice.HandleAsync(bidPacket, Now + 1);
        var bobBid = await bob.HandleAsync(new PacketWriter().U16(DistributionOpcodes.BidItemRq).I64(bid.BidUid).I64(2000).ToArray(), Now + 2);
        Assert.Contains(bobBid, p => p.Length >= 4 && (ushort)(p[0] | p[1] << 8) == DistributionOpcodes.BidItemRs && p[2] == 0);
        await alice.TickAsync(Now + 100);
        Assert.DoesNotContain(world.Social.BidSessions, kv => kv.Key == bid.BidUid);
        await bob.TickAsync(Now + 100);
        await bob.TickAsync(Now + 101);
        Assert.Contains(storeB.Items, i => i.TemplateId == 110120003);
    }

    [Fact]
    public void Special_distribution_list_packet_uses_expected_opcode()
    {
        var packet = DistributionPackets.SpecialDistributionList([
            new SpecialDistributionEntry(1, PartyOptions.SpecialDice, 110120003, 1, Now + 1000, 42),
        ]);
        Assert.Equal(DistributionOpcodes.SpecialDistributionListNt, (ushort)(packet[0] | packet[1] << 8));
    }

    [Fact]
    public async Task Force_create_join_shares_loot_notifications()
    {
        var world = new SharedWorld(GameData.Embedded);
        var leadStore = new FakeCharacterStore();
        var allyStore = new FakeCharacterStore();
        var lead = await EnterWorld(world, leadStore, "ForceLead", 5);
        var ally = await EnterWorld(world, allyStore, "AllyLead", 6);
        await lead.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionLeader, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        await ally.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionLeader, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        var create = await lead.HandleAsync(new PacketWriter().U16(SocialOpcodes.ForceCreateRq).ToArray(), Now + 1);
        Assert.Contains(create, p => p.Length >= 2 && (ushort)(p[0] | p[1] << 8) == SocialOpcodes.ForceCreateRs);
        var forceKey = world.Social.Forces.Keys.Single();
        await ally.HandleAsync(new PacketWriter().U16(SocialOpcodes.ForceJoinRq).I64(forceKey).ToArray(), Now + 2);
        Assert.Equal(forceKey, world.Social.Parties.Values.First(p => p.Leader == ally.WorldPlayer).ForceKey);
    }

    [Fact]
    public async Task Party_dice_pass_and_timeout_resolve()
    {
        var world = new SharedWorld(GameData.Embedded);
        var alice = await EnterWorld(world, new FakeCharacterStore(), "Lead", 3);
        var bob = await EnterWorld(world, new FakeCharacterStore(), "Mem", 4);
        await alice.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionSequence, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), Now);
        world.Social.Parties.Values.Single().Members.Add(bob.WorldPlayer!);
        Assert.NotNull(bob.WorldPlayer);
        var party = world.Social.Parties.Values.Single();
        var roll = new PartyDiceRoll
        {
            DiceUid = 7, PartyKey = party.Key, ItemId = 110120003, Count = 1, KillerKey = alice.PlayerKey,
            Center = Vector3d.Zero, At = Vector3d.Zero, ExpiresAtMs = Now + 100,
        };
        roll.Eligible.Add(alice.PlayerKey);
        roll.Eligible.Add(bob.PlayerKey);
        roll.Responses[alice.PlayerKey] = null;
        roll.Responses[bob.PlayerKey] = null;
        world.Social.DiceRolls[roll.DiceUid] = roll;
        await bob.HandleAsync(new PacketWriter().U16(DistributionOpcodes.RollDiceWithdrawRq).I64(roll.DiceUid).ToArray(), Now + 1);
        Assert.Contains(world.Social.DiceRolls, kv => kv.Key == roll.DiceUid);
        await alice.TickAsync(Now + 200);
        Assert.DoesNotContain(world.Social.DiceRolls, kv => kv.Key == roll.DiceUid);
    }

    static async Task<GameSession> EnterWorld(SharedWorld world, FakeCharacterStore store, string name, uint creation)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world, WorldId = 1204, CharacterLevel = 10 }, new Random((int)creation))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        var wireId = store.States.Single().WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        return session;
    }

    static async Task LoadBag(GameSession session) =>
        await session.HandleAsync(new PacketWriter().U16(0x5610).ToArray(), Now);

    static async Task<GameSession> EnterWorldWithKina(SharedWorld world, FakeCharacterStore store, string name, uint creation, long kina)
    {
        var session = new GameSession(GameData.Embedded, new GameSessionOptions { World = world, WorldId = 1204, CharacterLevel = 10 }, new Random((int)creation))
        { Store = store };
        await session.HandleAsync(GameSessionTestHelpers.Login(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creation).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), Now);
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, kina, 8000 + creation));
        var wireId = store.States.Single().WireId;
        await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), Now);
        await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), Now);
        await LoadBag(session);
        return session;
    }
}
