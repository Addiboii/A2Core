using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    static PacketWriter WriteGuildInfoSmall(PacketWriter w, GuildInfoSmall g) =>
        w.I64(g.GuildId).I32(0).String(g.Name).U8((byte)g.Level).I32(g.Emblem).String(g.MasterName).U8((byte)g.MemberCount)
            .I64(g.UnionId).I32(g.UnionEmblem).String(g.UnionName).I64(g.RequestedAtMs).U8((byte)g.Race);

    static void WriteGuildInfoSmallList(PacketWriter w, IReadOnlyList<GuildInfoSmall>? list)
    {
        var rows = list ?? [];
        w.Varint(rows.Count);
        foreach (var row in rows) WriteGuildInfoSmall(w, row);
    }

    void WriteGuildRelationLists(PacketWriter w, GuildView? g)
    {
        WriteGuildInfoSmallList(w, g?.AllyGuilds);
        WriteGuildInfoSmallList(w, g?.HostileGuilds);
        WriteGuildInfoSmallList(w, g?.SendAllyRequests);
        WriteGuildInfoSmallList(w, g?.RecvAllyRequests);
        for (var i = 0; i < 8; i++) w.Varint(0);
    }

    byte[] UpdateGuildWalletNt(GuildView? g, int reason)
    {
        var wallet = g?.Wallet ?? [];
        var w = PacketWriter.Opcode(GuildOpcodes.UpdateGuildWalletNt).Varint(wallet.Count);
        foreach (var (type, amount) in wallet) w.I32(type).I64(amount);
        return w.I32(reason).ToArray();
    }

    async Task<List<byte[]>?> TryGuildDiplomacyRequestAsync(ushort op, byte[] plain, PacketReader r, CancellationToken ct)
    {
        if (Store is not { } store || CharacterId is not { } character) return null;
        switch (op)
        {
            case GuildOpcodes.UnionCreateRq:
            {
                var (name, rule, password, emblem, intro) = (r.String(), r.U8(), r.String(), r.I32(), r.String());
                var outcome = ("none", 0L, "");
                await Stored(async () => { outcome = await store.CreateGuildUnionAsync(character, name, rule, password, emblem, intro, ct); return []; });
                if (outcome.Item1 != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.UnionCreateRs).U16(outcome.Item1 switch { "taken" => 3, "member" => 4, "union" => 5, _ => 1 })
                        .I64(0).String(name).U8(rule).String(password).I32(emblem).String(intro).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.UnionCreateNt).I64(outcome.Item2).String(outcome.Item3).I32(emblem).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.UnionCreateRs).U16(0).I64(outcome.Item2).String(name).U8(rule).String(password).I32(emblem).String(intro).ToArray(), nt, GuildInfo(0)];
            }
            case GuildOpcodes.UnionInfoRq when plain.Length == 2:
            {
                var outcome = ("none", (GuildUnionView?)null);
                await Stored(async () => { outcome = await store.GuildUnionInfoAsync(character, ct); return []; });
                if (outcome.Item1 != "ok" || outcome.Item2 is not { } u)
                    return [PacketWriter.Opcode(GuildOpcodes.UnionInfoRs).U16(1).I64(0).ToArray()];
                var w = PacketWriter.Opcode(GuildOpcodes.UnionInfoRs).U16(0).I64(u.Id).String(u.Name).U8(1).U8((byte)u.JoinRule).String(u.Password).I32(u.Emblem)
                    .String(u.Intro).String(u.Notice).I64(u.OwnerGuildId);
                w.Varint(u.MemberGuilds.Count);
                foreach (var m in u.MemberGuilds) WriteGuildInfoSmall(w, m);
                for (var i = 0; i < 8; i++) w.Varint(0);
                return [w.ToArray()];
            }
            case GuildOpcodes.UnionDisbandRq when plain.Length == 2:
            {
                var outcome = "none";
                await Stored(async () => { outcome = await store.DisbandGuildUnionAsync(character, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.UnionDisbandRs).U16(2).ToArray()];
                await ReloadGuildAsync(ct);
                var nt = PacketWriter.Opcode(GuildOpcodes.UnionDisbandNt).ToArray();
                TellGuild(guild, nt);
                return [PacketWriter.Opcode(GuildOpcodes.UnionDisbandRs).U16(0).ToArray(), nt, GuildInfo(0)];
            }
            case GuildOpcodes.UnionJoinRq:
            {
                var unionId = r.I64(); var rule = r.U8(); var password = r.String();
                _ = rule;
                var outcome = ("none", 0);
                await Stored(async () => { outcome = await store.JoinGuildUnionAsync(character, unionId, password, ct); return []; });
                if (outcome.Item1 != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.UnionJoinRs).U16(outcome.Item1 switch { "password" => 6, "full" => 5, _ => 1 }).I64(unionId).I32(0).ToArray()];
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.UnionJoinRs).U16(0).I64(unionId).I32(outcome.Item2).ToArray(), GuildInfo(0)];
            }
            case GuildOpcodes.UnionLeaveRq when plain.Length == 2:
            {
                var outcome = "none";
                await Stored(async () => { outcome = await store.LeaveGuildUnionAsync(character, ct); return []; });
                if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.UnionLeaveRs).U16(2).ToArray()];
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.UnionLeaveRs).U16(0).ToArray(), GuildInfo(0)];
            }
            case GuildOpcodes.RelationRq:
            {
                var targetType = r.I32(); var targetName = r.String(); var serverId = r.I32(); var relation = r.I32();
                var outcome = "none";
                await Stored(async () => { outcome = await store.RequestGuildRelationAsync(character, targetType, targetName, serverId, relation, ct); return []; });
                if (outcome != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.RelationRs).U16(outcome switch { "authority" => 2, "target" => 3, "self" => 4, _ => 1 })
                        .I32(targetType).String(targetName).I32(serverId).Varint(0).I32(0).I32(0).ToArray()];
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.RelationRs).U16(0).I32(targetType).String(targetName).I32(serverId).Varint(0).I32(relation).I32(relation).ToArray(), GuildInfo(0)];
            }
            case GuildOpcodes.RelationConfirmRq:
            {
                var targetType = r.I32(); var targetName = r.String(); var serverId = r.I32(); var accept = r.I32() != 0;
                var outcome = "none";
                await Stored(async () => { outcome = await store.ConfirmGuildRelationAsync(character, targetType, targetName, serverId, accept, ct); return []; });
                if (outcome != "ok")
                    return [PacketWriter.Opcode(GuildOpcodes.RelationConfirmRs).U16(2).I32(targetType).String(targetName).I32(serverId).I32(accept ? 1 : 0).ToArray()];
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildOpcodes.RelationConfirmRs).U16(0).I32(targetType).String(targetName).I32(serverId).I32(accept ? 1 : 0).ToArray(), GuildInfo(0)];
            }
            case GuildOpcodes.RelationCancelRq:
            {
                var targetType = r.I32(); var targetName = r.String(); var serverId = r.I32();
                var outcome = "none";
                await Stored(async () => { outcome = await store.CancelGuildRelationAsync(character, targetType, targetName, serverId, ct); return []; });
                var cancelReplies = new List<byte[]> { PacketWriter.Opcode(GuildOpcodes.RelationCancelRs).U16(outcome == "ok" ? 0 : 1).I32(targetType).String(targetName).I32(serverId).ToArray() };
                if (outcome == "ok") cancelReplies.Add(GuildInfo(0));
                return cancelReplies;
            }
        }
        return null;
    }
}
