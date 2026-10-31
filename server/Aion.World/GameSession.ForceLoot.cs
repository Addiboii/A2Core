using Aion.Protocol.Native;

namespace Aion.World;

// Minimal raid/alliance (force) for shared loot across parties.
public sealed partial class GameSession
{
    public const byte DropOwnerForce = 5;

    List<byte[]>? ForceRequest(byte[] plain, long now)
    {
        return Opcode(plain) switch
        {
            SocialOpcodes.ForceCreateRq => ForceCreate(),
            SocialOpcodes.ForceJoinRq => ForceJoin(plain),
            _ => null,
        };
    }

    List<byte[]> ForceCreate()
    {
        if (Social is not { } social || worldPlayer is null || social.PartyOf(worldPlayer) is not { } party
            || party.Leader != worldPlayer || party.ForceKey is not null)
            return [SocialPackets.ForceCreateReply(Failed, 0)];
        var key = social.NextId();
        var force = new Force(key, worldPlayer, party);
        party.ForceKey = key;
        social.Forces[key] = force;
        return [SocialPackets.ForceCreateReply(0, key)];
    }

    List<byte[]> ForceJoin(byte[] plain)
    {
        if (plain.Length < 10 || Social is not { } social || worldPlayer is null || social.PartyOf(worldPlayer) is not { } party
            || party.Leader != worldPlayer || party.ForceKey is not null)
            return [SocialPackets.ForceJoinReply(Failed)];
        var forceKey = BitConverter.ToInt64(plain, 2);
        if (!social.Forces.TryGetValue(forceKey, out var force)) return [SocialPackets.ForceJoinReply(Failed)];
        party.ForceKey = forceKey;
        force.Parties.Add(party);
        return [SocialPackets.ForceJoinReply(0)];
    }

    IEnumerable<WorldPlayer> LootRecipients(Party party)
    {
        if (Social is { } social && social.ForceOf(party.Leader) is { } force)
            return force.AllMembers;
        return party.Members;
    }

    void NotifyGroupLoot(WorldPlayer who, int itemId, Party party)
    {
        var packet = Social?.ForceOf(who) is not null
            ? SocialPackets.ForceMemberItemLooting(who.Name, ServerId, itemId)
            : SocialPackets.PartyMemberItemLooting(who.Name, ServerId, itemId);
        foreach (var m in LootRecipients(party)) m.Send(packet);
    }
}
