using Aion.Application.Chat;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>Multi-player social flows on <see cref="SharedWorld"/> with a shared <see cref="FakeStore"/>.</summary>
public static class SocialSharedWorldChecks
{
    static int passed;

    static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("SocialSharedWorld check failed: " + name);
        passed++;
    }

    static void Rejected(Action action, string name)
    {
        try { action(); }
        catch (ChatRejectedException) { passed++; return; }
        throw new InvalidOperationException("Expected rejection: " + name);
    }

    static ushort Op(byte[] p) => (ushort)(p[0] | p[1] << 8);

    public static async Task<int> Run(GameData data, long now, string sessionToken)
    {
        passed = 0;
        var shared = new SharedWorld(data);
        var store = new FakeStore();
        async Task<GameSession> Enter(string name, uint creationId, int seed)
        {
            var s = new GameSession(data, new GameSessionOptions { World = shared, TrainingDummy = true }, new Random(seed)) { Store = store };
            await s.HandleAsync(Strings(0x3613, sessionToken, "", "t", "").Concat(new byte[9]).ToArray(), now);
            await s.HandleAsync(new PacketWriter().U16(0x361b).String(name).U8(1).U32(creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray(), now);
            var wire = store.States[^1].WireId;
            await s.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)wire << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
            await s.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
            return s;
        }

        var aly = await Enter("Aly", 1, 41);
        var bob = await Enter("Bob", 2, 42);
        await aly.TickAsync(now + 10);
        await bob.TickAsync(now + 10);
        var alyDb = store.States.First(s => s.Summary.Name == "Aly").WireId;
        var bobDb = store.States.First(s => s.Summary.Name == "Bob").WireId;

        var ask = await aly.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.FriendRequestRq, 1204, "Bob"), now + 20);
        Check(ask.Any(b => Op(b) == SocialOpcodes.FriendRequestRs), "friend request to Bob");
        Check((await bob.TickAsync(now + 30)).Any(b => Op(b) == SocialOpcodes.FriendRequestNt), "Bob sees friend request notice");
        Check((await bob.HandleAsync(SocialRequests.EncodeDbId(SocialOpcodes.FriendRequestAcceptRq, alyDb), now + 40))
            .Any(b => Op(b) == SocialOpcodes.FriendRequestAcceptRs), "Bob accepts friend request");
        await aly.HandleAsync(SocialRequests.EncodeFriendMemo(bobDb, "party mate"), now + 41);
        Check((await store.SocialAsync(aly.CharacterId!.Value, default))!.Any(r => r.Kind == "friend" && r.Memo == "party mate"), "friend memo on Aly's side");
        await aly.TickAsync(now + 42);
        Check((await bob.TickAsync(now + 43)).Any(b => Op(b) == SocialOpcodes.FriendInfoUpdateNt), "Bob hears Aly online update after friend link");

        Check((await aly.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.FriendBlockRq, 1204, "Bob"), now + 50))
            .Any(b => Op(b) == SocialOpcodes.FriendBlockRs), "Aly blocks Bob");
        Check((await aly.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bob"), now + 60)).Single()[2] != 0,
            "party invite refused while blocked");
        Check((await aly.HandleAsync(SocialRequests.EncodeKey(SocialOpcodes.ExchangeRequestRq, bob.PlayerKey), now + 61)).Single()[2] != 0,
            "trade request refused while blocked");
        Check(await store.UnblockAsync(aly.CharacterId!.Value, bob.CharacterId!.Value, default), "unblock");

        await aly.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionRandom, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), now + 70);
        var partyKey = shared.Social.Parties.Values.Single().Key;
        await aly.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bob"), now + 71);
        await bob.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), now + 72);
        Check(shared.Social.Parties[partyKey].Members.Count == 2, "party of two");
        Check((await aly.HandleAsync(SocialRequests.EncodePartyInfoUpdate(new PartySettingsRequest(
            PartyOptions.DistributionLeader, PartyOptions.JoinApproval, PartyOptions.SpecialDice, 1)), now + 73))
            .Any(b => Op(b) == SocialOpcodes.PartyInfoUpdateRs), "party loot mode update");
        Check(shared.Social.Parties[partyKey].Distribution == PartyOptions.DistributionLeader, "leader loot mode stored");
        await aly.HandleAsync(SocialRequests.EncodeDbId(SocialOpcodes.PartySetLeaderRq, bobDb), now + 74);
        Check(shared.Social.Parties[partyKey].Leader == bob.WorldPlayer, "Bob is party leader");
        await bob.HandleAsync(SocialRequests.EncodeDbId(SocialOpcodes.PartyKickoutRq, alyDb), now + 75);
        Check(!shared.Social.Parties.ContainsKey(partyKey), "kick last member disbands party of two");

        await aly.HandleAsync(SocialRequests.EncodePartyCreate(new PartySettingsRequest(
            PartyOptions.DistributionSequence, PartyOptions.JoinFree, PartyOptions.SpecialNormal, 2)), now + 80);
        partyKey = shared.Social.Parties.Values.Single().Key;
        await aly.HandleAsync(SocialRequests.EncodeServerAndName(SocialOpcodes.PartyInviteRq, 1204, "Bob"), now + 81);
        await bob.HandleAsync(SocialRequests.EncodePartyInviteResponse([new PartyInviteReply(partyKey, PartyInviteAnswer.Accept)]), now + 82);
        bob.ExitSharedWorld();
        Check((await aly.TickAsync(now + 83)).Any(b => Op(b) == SocialOpcodes.PartyDisconnectMemberNt), "offline member notifies party");
        Check(shared.Social.Parties[partyKey].Members.Count == 2, "offline member stays in roster");
        await bob.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now + 84);
        Check((await aly.TickAsync(now + 85)).Any(b => Op(b) == SocialOpcodes.PartyReconnectMemberNt), "reconnect notifies party");
        await aly.HandleAsync(new PacketWriter().U16(SocialOpcodes.PartyLeaveRq).ToArray(), now + 86);
        Check(!shared.Social.Parties.ContainsKey(partyKey), "explicit leave disbands party of two");

        var search = await aly.HandleAsync(new PacketWriter().U16(SocialOpcodes.PartyRecommendRq).I32(0).ToArray(), now + 90);
        Check(search.Any(b => Op(b) == SocialOpcodes.PartyRecommendRs) && search.First(b => Op(b) == SocialOpcodes.PartyRecommendRs).Length > 8,
            "party recommend lists nearby players");

        var relay = new ChatRelay(TimeProvider.System) { WhisperBlocked = (from, to) => store.SocialBlockedAsync(from, to, default).Result };
        var room = new ChatRoomKey("REGION", "1204", "RG");
        var alyChar = aly.CharacterId!.Value;
        var bobChar = bob.CharacterId!.Value;
        var ta = relay.Login(new ChatIdentity("1", "1204", "en_US", null, new ChatCharacter(alyChar, "Aly", "Aly")));
        var tb = relay.Login(new ChatIdentity("2", "1204", "en_US", null, new ChatCharacter(bobChar, "Bob", "Bob")));
        relay.Join(ta, room);
        relay.Join(tb, room);
        relay.Send(tb, room, Msg("Bob", "ping"));
        Check(relay.Send(ta, room, Msg("Aly", "hello region")).Deliveries.Count == 2, "region (zone) chat reaches both");
        Rejected(() => relay.Send(ta, room, Msg("Aly", "say badword here")), "profanity filter");
        await store.BlockAsync(alyChar, "Bob", default);
        Rejected(() => relay.Whisper(ta, new ChatRoomKey("", "", ""), Msg("Aly", "blocked whisper") with { TargetCharacterName = "Bob" }), "block stops whisper");
        await store.UnblockAsync(alyChar, bobChar, default);
        Check(relay.Whisper(ta, new ChatRoomKey("", "", ""), Msg("Aly", "ok") with { TargetCharacterName = "Bob" }).Deliveries.Count == 2,
            "whisper after unblock");

        aly.ExitSharedWorld();
        bob.ExitSharedWorld();
        return passed;
    }

    static ChatMessage Msg(string user, string content) => new(user, "", "NORMAL", content, "{}");

    static byte[] Strings(ushort opcode, params string[] values)
    {
        var w = new PacketWriter().U16(opcode);
        foreach (var v in values) w.String(v);
        return w.ToArray();
    }
}
