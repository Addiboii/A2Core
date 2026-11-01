using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Legions (guilds). Live opcodes: the 0x8a run (GuildOpcodes). Layouts from usmap via tools/packet_structs.py.
public sealed partial class GameSession
{
    public const ushort GuildCreateRq = 0x8a00, GuildCreateRs = 0x8a01, GuildInfoRq = 0x8a02, GuildInfoRs = 0x8a03,
        GuildMemberInfoRq = 0x8a04, GuildMemberInfoRs = 0x8a05, GuildSearchRq = 0x8a08, GuildSearchRs = 0x8a09,
        GuildLeaveRq = 0x8a0a, GuildLeaveRs = 0x8a0b, GuildLeaveNt = 0x8a0c, GuildKickRq = 0x8a0d, GuildKickRs = 0x8a0e,
        GuildJoinRq = 0x8a0f, GuildJoinRs = 0x8a10, GuildDescUpdateNt = 0x8a33, GuildDisbandRq = 0x8a34, GuildDisbandRs = 0x8a35,
        GuildMemberConnectedNt = 0x8a39;
    public const long GuildCreateKina = 100_000;
    const byte DecreaseGuildCreate = 0;
    internal static readonly (byte Grade, int Bits)[] GuildAuthority = [(0, 8190), (1, 1688), (2, 512)];
    static readonly GuildTables Legion = GuildTables.Embedded;

    GuildView? guild;
    public GuildView? Guild => guild;

    static long CharDbId(Guid character) => BitConverter.ToInt64(character.ToByteArray(), 0) & long.MaxValue;

    static PacketWriter WriteGuildDesc(PacketWriter w, GuildView? g, int grade) =>
        w.I64(g?.Id ?? 0).String(g?.Name ?? "").I32(g?.Emblem ?? 0).U8(g is null ? 0 : grade).I32(0).I64(0).I32(0).String("");

    int MyGuildGrade() => guild?.Members.FirstOrDefault(m => m.CharacterId == CharacterId)?.Grade ?? 2;

    (int Level, long NextTotal) GuildLevel(GuildView g) => Legion.LevelFromExp(g.Exp);

    int GuildMaxMembers(GuildView g) => Legion.MaxMembersAtLevel(GuildLevel(g).Level);

    byte[] GuildDescUpdate()
    {
        var packet = WriteGuildDesc(PacketWriter.Opcode(GuildDescUpdateNt).Varint(playerKey), guild, MyGuildGrade()).ToArray();
        worldPlayer?.Map?.Broadcast(worldPlayer, [packet]);
        return packet;
    }

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

    byte[] AgitDecoListNt(GuildView? g)
    {
        var decos = g?.AgitDecos ?? [];
        var w = PacketWriter.Opcode(GuildOpcodes.AgitDecoListNt).Varint(decos.Count);
        foreach (var d in decos) w.I32(d.DecoId).I32(d.SlotId);
        return w.ToArray();
    }

    byte[] GuildInfo(int result)
    {
        var g = guild;
        var (level, nextExp) = g is null ? (0, 0L) : GuildLevel(g);
        var maxMembers = g is null ? Legion.MaxMembers : GuildMaxMembers(g);
        var remain = g?.Members.FirstOrDefault(m => m.CharacterId == CharacterId)?.DonationsToday is { } used
            ? Math.Max(0, Legion.DonationsPerDay - used) : Legion.DonationsPerDay;
        var canAttend = g is not null;
        var w = PacketWriter.Opcode(GuildInfoRs).U16(result).String(g?.Name ?? "").String(g?.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "")
            .U8(g?.Members.Count ?? 0).U8(maxMembers).U8(level).I64(g?.Exp ?? 0).I64(nextExp).I32(g?.Emblem ?? 0)
            .String(g?.Intro ?? "").String(g?.Notice ?? "").U8(g?.JoinRule ?? 0).String(g?.Password ?? "")
            .I32(0).I32(canAttend ? 1 : 0).I32(g?.YesterdayAttendance ?? 0).I32(g?.TodayAttendance ?? 0).I32(remain).I32(g is null ? 0 : 1).I32(g?.AgitId ?? 0).I64(g?.AgitEndMs ?? 0);
        WriteGuildRelationLists(w, g);
        WriteAuthorityList(w, g);
        WriteWalletList(w, g);
        return w.I32(0).ToArray();
    }

    byte[] GuildMembers(int result)
    {
        var online = options.World;
        var members = guild?.Members ?? [];
        var w = PacketWriter.Opcode(GuildMemberInfoRs).U16(result).Varint(members.Count);
        foreach (var m in members)
            w.I64(0).I64(CharDbId(m.CharacterId)).I32(m.CreationId).String(m.Name).I32(m.Level).I32(0).U8(m.Grade).String(m.Comment)
                .I64(0).I64(m.Reputation).I64(m.WeeklyReputation).I64(m.DonationReputation).I64(m.WeeklyDonationReputation)
                .I32(m.CharacterId == CharacterId || online?.FindOnline(m.CharacterId) is not null ? 1 : 0).I64(0).I32(0).I32(0).I32(0).I32(0);
        return w.ToArray();
    }

    List<(Guid Item, long Revision, int Count)>? DonationCosts(int level)
    {
        if (Legion.Donation(level) is not { } tier) return null;
        return CostStacks(KinaItemId, tier.Kina) is { } costs ? costs : null;
    }

    List<(Guid Item, long Revision, int Count)>? AgitRentCosts() =>
        CostStacks(KinaItemId, Legion.AgitRentKina) is { } costs ? costs : null;

    async Task<List<byte[]>?> GuildRequestAsync(byte[] plain, CancellationToken ct)
    {
        var op = Opcode(plain);
        if (op is < GuildCreateRq or > GuildOpcodes.AgitDecoUnEquipRs) return null;
        var r = new PacketReader(plain) { Offset = 2 };
        try
        {
            if (await TryGuildDiplomacyRequestAsync(op, plain, r, ct) is { } diplomacyReplies) return diplomacyReplies;
            if (await TryGuildStorageWalletRequestAsync(op, plain, r, ct) is { } storageExtraReplies) return storageExtraReplies;
            if (await TryGuildOfficerRequestAsync(op, plain, r, ct) is { } officerReplies) return officerReplies;
            if (Store is not { } store || CharacterId is not { } character) return op is GuildInfoRq ? [GuildInfo(1)] : null;
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
                    {
                        var (lv, _) = GuildLevel(g);
                        w.I64(g.Id).String(g.Name).String(g.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "").U8(lv).I64(g.Exp)
                            .U8(g.Members.Count).U8(GuildMaxMembers(g)).I32(g.Emblem).String(g.Intro).U8(g.JoinRule).Bool(false).I64(0).U8(profileRace);
                    }
                    return [w.ToArray()];
                }
                case GuildJoinRq:
                {
                    var (id, _, comment, password) = ((long)r.U64(), r.U8(), r.String(), r.String());
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.JoinGuildAsync(character, id, password, comment, Legion.MaxMembers, ct); return []; });
                    if (outcome == "pending")
                        return [WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(0).I64(id).U8(1), null, 2).ToArray()];
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
                case GuildOpcodes.DonationInfoRq:
                {
                    await ReloadGuildAsync(ct);
                    if (guild is null) return [PacketWriter.Opcode(GuildOpcodes.DonationInfoRs).U16(1).I32(0).ToArray()];
                    var remain = 0;
                    await Stored(async () => { remain = await store.GuildDonationRemainAsync(character, ct); return []; });
                    return [PacketWriter.Opcode(GuildOpcodes.DonationInfoRs).U16(0).I32(remain).ToArray()];
                }
                case GuildOpcodes.DonationRq:
                {
                    var donationLevel = r.I32();
                    if (DonationCosts(donationLevel) is not { } costs) return [PacketWriter.Opcode(GuildOpcodes.DonationRs).U16(1).I32(donationLevel).I32(0).ToArray()];
                    var outcome = ("none", 0, (int?)null);
                    await Stored(async () => { outcome = await store.DonateGuildAsync(character, donationLevel, costs, ct); return []; });
                    if (outcome.Item1 != "ok")
                        return [PacketWriter.Opcode(GuildOpcodes.DonationRs).U16(outcome.Item1 switch { "limit" => 2, "kina" => 3, "level" => 4, _ => 1 }).I32(donationLevel).I32(0).ToArray(),
                            .. await InventoryChangesAsync(ct, 0, DecreaseGuildCreate)];
                    await ReloadGuildAsync(ct);
                    var replies = new List<byte[]> { PacketWriter.Opcode(GuildOpcodes.DonationRs).U16(0).I32(donationLevel).I32(outcome.Item2).ToArray(), GuildInfo(0) };
                    replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseGuildCreate));
                    if (outcome.Item3 is { } newLevel)
                    {
                        var levelNt = PacketWriter.Opcode(GuildOpcodes.LevelNt).I32(newLevel).ToArray();
                        TellGuild(guild, levelNt);
                        replies.Add(levelNt);
                    }
                    return replies;
                }
                case GuildOpcodes.AgitInfoRq:
                {
                    await ReloadGuildAsync(ct);
                    if (guild is null) return [PacketWriter.Opcode(GuildOpcodes.AgitInfoRs).U16(1).I32(0).I64(0).ToArray()];
                    return [PacketWriter.Opcode(GuildOpcodes.AgitInfoRs).U16(0).I32(guild.AgitId).I64(guild.AgitEndMs).ToArray()];
                }
                case GuildOpcodes.AgitContractRq:
                {
                    var agitId = r.I32();
                    var endHint = r.I64();
                    _ = endHint;
                    if (AgitRentCosts() is not { } costs)
                        return [PacketWriter.Opcode(GuildOpcodes.AgitContractRs).U16(3).I32(agitId).I64(0).I32(0).ToArray()];
                    var outcome = ("none", 0, 0L);
                    await Stored(async () => { outcome = await store.ContractGuildAgitAsync(character, agitId, costs, lastNow, ct); return []; });
                    if (outcome.Item1 != "ok")
                        return [PacketWriter.Opcode(GuildOpcodes.AgitContractRs).U16(outcome.Item1 switch { "authority" => 2, "agit" => 4, "kina" => 3, _ => 1 }).I32(agitId).I64(0).I32(0).ToArray()];
                    await ReloadGuildAsync(ct);
                    var infoNt = PacketWriter.Opcode(GuildOpcodes.AgitInfoNt).I32(outcome.Item2).I64(outcome.Item3).ToArray();
                    TellGuild(guild, infoNt);
                    return [PacketWriter.Opcode(GuildOpcodes.AgitContractRs).U16(0).I32(outcome.Item2).I64(outcome.Item3).I32(1).ToArray(), infoNt, GuildInfo(0),
                        .. await InventoryChangesAsync(ct, 0, DecreaseGuildCreate)];
                }
                case GuildOpcodes.AgitDecoEquipRq:
                {
                    var decoId = r.I32(); var slotId = r.I32();
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.EquipGuildAgitDecoAsync(character, decoId, slotId, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.AgitDecoEquipRs).U16(1).I32(decoId).I32(slotId).ToArray()];
                    await ReloadGuildAsync(ct);
                    var nt = AgitDecoListNt(guild);
                    TellGuild(guild, nt);
                    return [PacketWriter.Opcode(GuildOpcodes.AgitDecoEquipRs).U16(0).I32(decoId).I32(slotId).ToArray(), nt];
                }
                case GuildOpcodes.AgitDecoUnEquipRq:
                {
                    var decoId = r.I32();
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.UnequipGuildAgitDecoAsync(character, decoId, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildOpcodes.AgitDecoUnEquipRs).U16(1).I32(decoId).ToArray()];
                    await ReloadGuildAsync(ct);
                    var nt = AgitDecoListNt(guild);
                    TellGuild(guild, nt);
                    return [PacketWriter.Opcode(GuildOpcodes.AgitDecoUnEquipRs).U16(0).I32(decoId).ToArray(), nt];
                }
            }
        }
        catch (ProtocolException) { return []; }
        return null;
    }

    public List<byte[]> GuildRemoved()
    {
        guild = null;
        return resources is null ? [] : [GuildDescUpdate()];
    }

    async Task LoadGuildAsync(INativeCharacterStore store, Guid character, CancellationToken ct) => guild = await store.GuildOfAsync(character, ct);

    List<byte[]> GuildAtEntry()
    {
        if (guild is null) return [];
        TellGuild(guild, PacketWriter.Opcode(GuildMemberConnectedNt).String(StoredName).ToArray());
        return [GuildDescUpdate()];
    }

    /// <summary>0x8aaf AgitDecoList_NT for world entry (replaces the empty list in GamePackets.EmptyEntryLists).</summary>
    internal byte[] AgitDecoListAtEntry() => AgitDecoListNt(guild);
}

static class PacketWriterExtensions
{
    public static PacketWriter Also(this PacketWriter w, Action<PacketWriter> write) { write(w); return w; }
}
