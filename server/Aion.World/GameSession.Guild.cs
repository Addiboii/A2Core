using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Legions (guilds). Live opcodes: the 0x8a run, catalogue index + 0x8797 (CANDIDATE strong: 0x8a1c/0x8a1d
// GuildSendJoinRequests and 0x8aaf AgitDecoList_NT fit the official world entry). Layouts from the usmap; results
// u16 as every observed RS, strings u8 length, arrays varint count. No client request has been seen: the first body of
// each is logged. Joining is by application (EGuildJoinRule): AutoJoin joins at once, Password with the password;
// Manual and AutoDeny are refused here (officer confirmation is not built). Creation costs 100000 Kina
// (GlobalSetting guild_create_gold); names 1-12 characters.
public sealed partial class GameSession
{
    public const ushort GuildCreateRq = 0x8a00, GuildCreateRs = 0x8a01, GuildInfoRq = 0x8a02, GuildInfoRs = 0x8a03,
        GuildMemberInfoRq = 0x8a04, GuildMemberInfoRs = 0x8a05, GuildSearchRq = 0x8a08, GuildSearchRs = 0x8a09,
        GuildLeaveRq = 0x8a0a, GuildLeaveRs = 0x8a0b, GuildLeaveNt = 0x8a0c, GuildKickRq = 0x8a0d, GuildKickRs = 0x8a0e,
        GuildJoinRq = 0x8a0f, GuildJoinRs = 0x8a10, GuildDescUpdateNt = 0x8a33, GuildDisbandRq = 0x8a34, GuildDisbandRs = 0x8a35,
        GuildMemberConnectedNt = 0x8a39;
    public const long GuildCreateKina = 100_000;
    public const int GuildMaxMembers = 128;   // GuildData MaxMemberNum (every level)
    const byte DecreaseGuildCreate = 0;
    /// <summary>GuildMemberGradeAuthority: Master, Officer, Member bitsets.</summary>
    static readonly (byte Grade, int Bits)[] GuildAuthority = [(0, 8190), (1, 1688), (2, 512)];

    GuildView? guild;
    public GuildView? Guild => guild;

    static long CharDbId(Guid character) => BitConverter.ToInt64(character.ToByteArray(), 0) & long.MaxValue;

    static PacketWriter WriteGuildDesc(PacketWriter w, GuildView? g, int grade) =>
        w.I64(g?.Id ?? 0).String(g?.Name ?? "").I32(g?.Emblem ?? 0).U8(g is null ? 0 : grade).I32(0).I64(0).I32(0).String("");

    int MyGuildGrade() => guild?.Members.FirstOrDefault(m => m.CharacterId == CharacterId)?.Grade ?? 2;

    /// <summary>0x8a33 GuildDescUpdate_NT {key, CharGuildDesc}: this player's legion tag, to itself and those nearby.</summary>
    byte[] GuildDescUpdate()
    {
        var packet = WriteGuildDesc(PacketWriter.Opcode(GuildDescUpdateNt).Varint(playerKey), guild, MyGuildGrade()).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [packet]);
        return packet;
    }

    /// <summary>Sends packets to the legion's other members who are online.</summary>
    void TellGuild(GuildView? g, params byte[][] packets)
    {
        if (g is null || options.World is not { } world) return;
        foreach (var member in g.Members)
            if (member.CharacterId != CharacterId && world.FindOnline(member.CharacterId) is { } online) online.Send(packets);
    }

    async Task<GuildView?> ReloadGuildAsync(CancellationToken ct)
    {
        if (Store is { } store && CharacterId is { } character)
            await Stored(async () => { guild = await store.GuildOfAsync(character, ct); return []; });
        return guild;
    }

    byte[] GuildInfo(int result)
    {
        var g = guild;
        var w = PacketWriter.Opcode(GuildInfoRs).U16(result).String(g?.Name ?? "").String(g?.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "")
            .U8(g?.Members.Count ?? 0).U8(GuildMaxMembers).U8(g?.Level ?? 0).I64(g?.Exp ?? 0).I64(1000).I32(g?.Emblem ?? 0)
            .String(g?.Intro ?? "").String(g?.Notice ?? "").U8(g?.JoinRule ?? 0).String(g?.Password ?? "")
            .Bool(false).Bool(false).U8(0).U8(0).U8(0).I64(g?.Created.ToUnixTimeMilliseconds() ?? 0).I32(0).I64(0);
        for (var i = 0; i < 12; i++) w.Varint(0);   // allies, hostiles, requests, unions (9 struct arrays, 3 id arrays)
        w.Varint(GuildAuthority.Length);
        foreach (var (grade, bits) in GuildAuthority) w.U8(grade).I32(bits);
        return w.Varint(0).I32(0).ToArray();   // wallet, airspace
    }

    byte[] GuildMembers(int result)
    {
        var online = options.World;
        var members = guild?.Members ?? [];
        var w = PacketWriter.Opcode(GuildMemberInfoRs).U16(result).Varint(members.Count);
        foreach (var m in members)
            w.I64(0).I64(CharDbId(m.CharacterId)).I32(m.CreationId).String(m.Name).I32(m.Level).I32(0).U8(m.Grade).String(m.Comment)
                .I64(0).I64(0).I64(0).I64(0).I64(0).Bool(m.CharacterId == CharacterId || online?.FindOnline(m.CharacterId) is not null).I64(0)
                .Bool(false).I32(0).I32(0).I64(0);
        return w.ToArray();
    }

    async Task<List<byte[]>?> GuildRequestAsync(byte[] plain, CancellationToken ct)
    {
        var op = Opcode(plain);
        if (op is < GuildCreateRq or > GuildMemberConnectedNt) return null;
        if (Store is not { } store || CharacterId is not { } character) return op is GuildInfoRq ? [GuildInfo(1)] : null;
        var r = new PacketReader(plain) { Offset = 2 };
        try
        {
            switch (op)
            {
                case GuildCreateRq:
                {
                    var (name, rule, password, emblem, intro) = (r.String(), r.U8(), r.String(), r.I32(), r.String());
                    byte[] Reply(int result) => PacketWriter.Opcode(GuildCreateRs).U16(result).I64(guild?.Id ?? 0).String(name).U8(guild?.Level ?? 0).I64(0)
                        .U8(rule).String(password).I32(emblem).String(intro).Varint(GuildAuthority.Length)
                        .Also(w => { foreach (var (grade, bits) in GuildAuthority) w.U8(grade).I32(bits); }).ToArray();
                    if (CostStacks(KinaItemId, GuildCreateKina) is not { } costs) return [Reply(2)];
                    var outcome = "failed";
                    await Stored(async () => { outcome = await store.CreateGuildAsync(character, name, rule, password, emblem, intro, costs, ct); return []; });
                    if (outcome != "ok") return [Reply(outcome switch { "taken" => 3, "member" => 4, _ => 1 })];
                    await ReloadGuildAsync(ct);
                    return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseGuildCreate), GuildDescUpdate()];
                }
                case GuildInfoRq:
                    await ReloadGuildAsync(ct);
                    return [GuildInfo(guild is null ? 1 : 0)];
                case GuildMemberInfoRq:
                    await ReloadGuildAsync(ct);
                    return [GuildMembers(guild is null ? 1 : 0)];
                case GuildSearchRq:
                {
                    var name = r.String();
                    IReadOnlyList<GuildView> found = [];
                    await Stored(async () => { found = await store.SearchGuildsAsync(name, ct); return []; });
                    var w = PacketWriter.Opcode(GuildSearchRs).U16(0).Varint(found.Count);
                    foreach (var g in found)
                        w.I64(g.Id).String(g.Name).String(g.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "").U8(g.Level).I64(g.Exp)
                            .U8(g.Members.Count).U8(GuildMaxMembers).I32(g.Emblem).String(g.Intro).U8(g.JoinRule).Bool(false).I64(0).U8(profileRace);
                    return [w.ToArray()];
                }
                case GuildJoinRq:
                {
                    var (id, _, _, password) = ((long)r.U64(), r.U8(), r.String(), r.String());
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.JoinGuildAsync(character, id, password, GuildMaxMembers, ct); return []; });
                    if (outcome != "ok")
                        return [WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(outcome switch { "member" => 4, "full" => 5, "password" => 6, "denied" => 7, _ => 1 }).I64(id).U8(0), null, 2).ToArray()];
                    await ReloadGuildAsync(ct);
                    TellGuild(guild, PacketWriter.Opcode(GuildMemberConnectedNt).String(StoredName).ToArray());
                    return [WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(0).I64(id).U8(0), guild, 2).ToArray(), GuildDescUpdate(), .. Achieve("GuildJoin")];
                }
                case GuildLeaveRq:
                {
                    var before = guild ?? await ReloadGuildAsync(ct);
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.LeaveGuildAsync(character, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildLeaveRs).U16(outcome == "master" ? 2 : 1).I64(0).ToArray()];
                    TellGuild(before, PacketWriter.Opcode(GuildLeaveNt).String(StoredName).U8(3).I64(0).ToArray());
                    guild = null;
                    return [PacketWriter.Opcode(GuildLeaveRs).U16(0).I64(0).ToArray(), GuildDescUpdate()];
                }
                case GuildKickRq:
                {
                    var nickname = r.String();
                    var before = guild ?? await ReloadGuildAsync(ct);
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.KickGuildMemberAsync(character, nickname, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildKickRs).U16(outcome == "authority" ? 2 : 1).ToArray()];
                    var notice = PacketWriter.Opcode(GuildLeaveNt).String(nickname).U8(5).I64(0).ToArray();
                    TellGuild(before, notice);
                    if (before?.Members.FirstOrDefault(m => m.Name.Equals(nickname, StringComparison.OrdinalIgnoreCase)) is { } kicked
                        && options.World?.FindOnline(kicked.CharacterId) is { } kickedOnline)
                        _ = kickedOnline.Session.InvokeAsync(_ => (true, kickedOnline.Session.GuildRemoved()));
                    await ReloadGuildAsync(ct);
                    return [PacketWriter.Opcode(GuildKickRs).U16(0).ToArray(), notice];
                }
                case GuildDisbandRq:
                {
                    var before = guild ?? await ReloadGuildAsync(ct);
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.DisbandGuildAsync(character, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildDisbandRs).U16(2).ToArray()];
                    TellGuild(before, PacketWriter.Opcode(GuildLeaveNt).String(StoredName).U8(4).I64(0).ToArray());
                    if (before is not null && options.World is { } world)
                        foreach (var m in before.Members.Where(m => m.CharacterId != character))
                            if (world.FindOnline(m.CharacterId) is { } online)
                                _ = online.Session.InvokeAsync(_ => (true, online.Session.GuildRemoved()));
                    guild = null;
                    return [PacketWriter.Opcode(GuildDisbandRs).U16(0).ToArray(), GuildDescUpdate()];
                }
            }
        }
        catch (ProtocolException) { return []; }
        return null;
    }

    /// <summary>Kicked or disbanded from elsewhere: the legion is gone; the tag clears (on this session's loop).</summary>
    public List<byte[]> GuildRemoved()
    {
        guild = null;
        return resources is null ? [] : [GuildDescUpdate()];
    }

    /// <summary>World entry: the legion tag and the members' notice (GuildMemberConnected_NT).</summary>
    async Task LoadGuildAsync(INativeCharacterStore store, Guid character, CancellationToken ct) => guild = await store.GuildOfAsync(character, ct);

    List<byte[]> GuildAtEntry()
    {
        if (guild is null) return [];
        TellGuild(guild, PacketWriter.Opcode(GuildMemberConnectedNt).String(StoredName).ToArray());
        return [GuildDescUpdate()];
    }
}

static class PacketWriterExtensions
{
    public static PacketWriter Also(this PacketWriter w, Action<PacketWriter> write) { write(w); return w; }
}
