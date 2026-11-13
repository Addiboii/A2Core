using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Legions (guilds). Live opcodes: the 0x8a run, catalogue index + 0x8797 (0x8a1d GuildSendJoinRequests_RS, 0x8a03 GuildInfo_RS,
// 0x8a05 GuildMemberInfo_RS, 0x8a39 GuildMemberConnected_NT, 0x8a45 GuildAgitInfo_RS and 0x8aaf AgitDecoList_NT OBSERVED in the
// official recordings of 2026-11-06/07, a character in the 90-member legion "Adventalis"). Results u16 as every observed RS,
// strings varint length, arrays varint count, the emblem id u16 (OBSERVED in GuildInfo_RS and CharGuildDesc). Joining
// (EGuildJoinRule): AutoJoin joins at once, Password with the password, Manual files an application the officers answer
// (GameSession.Legion.cs), AutoDeny refuses. Creation costs GlobalSetting guild_create_gold (100000 Kina); names 1-12.
// Management (settings, grades, authority, attendance, donations, applications): GameSession.Legion.cs.
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
    /// <summary>EGuildUpdateReason (usmap): the GuildLeave_NT reason.</summary>
    const byte GuildReasonDisband = 2, GuildReasonLeave = 3, GuildReasonLeaveForDisband = 4, GuildReasonKicked = 5;

    GuildView? guild;
    /// <summary>The legion's stored extras (authority, wallet, attendance, applications); null outside a legion or without a legion store.</summary>
    GuildExtras? guildExtras;
    public GuildView? Guild => guild;

    static long CharDbId(Guid character) => BitConverter.ToInt64(character.ToByteArray(), 0) & long.MaxValue;

    /// <summary>CharGuildDesc in the official 18-byte tail form (PlayerPackets.WriteGuildDesc; we wrote 22 bytes before 2026-11-09).</summary>
    static PacketWriter WriteGuildDesc(PacketWriter w, GuildView? g, int grade) =>
        PlayerPackets.WriteGuildDesc(w, g is null ? null : new GuildTag(g.Id, g.Name, g.Emblem, (byte)grade));

    /// <summary>This player's guild tag as others see it (0x3645 bit 1); null outside a guild.</summary>
    GuildTag? MyGuildTag() => guild is { } g ? new GuildTag(g.Id, g.Name, g.Emblem, (byte)MyGuildGrade()) : null;

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
            await Stored(async () =>
            {
                guild = await store.GuildOfAsync(character, ct);
                guildExtras = guild is not null && store is ILegionStore legion ? await legion.GuildExtrasAsync(character, ct) : null;
                return [];
            });
        return guild;
    }

    /// <summary>EGuildMemberGrade -> EGuildAuthority bit set: the master's own setting (GuildSetAuthority_RQ) or GuildMemberGradeAuthority.dat.</summary>
    IReadOnlyList<(byte Grade, int Bits)> GuildAuthority() =>
        Systems2Tables.Embedded.DefaultAuthority.Select(d => (d.Grade, d.Grade != 0 && guildExtras?.Authority.TryGetValue(d.Grade, out var bits) == true ? bits : d.Bits)).ToList();

    /// <summary>The legion level's GuildData row (level 1 when unknown).</summary>
    static Systems2Tables.GuildLevel GuildLevelRow(int level) => Systems2Tables.Embedded.GuildLevelOf(Math.Max(1, level));

    /// <summary>
    /// 0x8a03 GuildInfo_RS, OBSERVED layout (#1927 of rec 20261107-210132, 299 bytes, rebuilt here field for field): result u16,
    /// name, master, member count u8, max u8, level u8, exp i64, max exp i64 (GuildData AccumulateExp of the level: 1000 at
    /// level 1 OBSERVED), emblem u16, intro, notice, join rule u8, password, two packed bools (can take the attendance reward,
    /// can attend today: 0x02 OBSERVED), yesterday's and today's attendance u8, donations left u8 (3 OBSERVED), founded i64, hall
    /// i32 + its end i64, nine relation lists and three id lists (empty), grade authority list, wallet list (one EssenceGuild
    /// entry OBSERVED), the owned airspace i32.
    /// </summary>
    byte[] GuildInfo(int result, long now = 0)
    {
        var g = guild;
        var day = GuildDay(now == 0 ? lastNow : now);
        var mine = guildExtras?.Members.FirstOrDefault(m => m.CharacterId == CharacterId);
        var attendance = (g is not null && CanTakeAttendanceReward(mine, day), g is not null && mine?.Attended != day,
            guildExtras?.Members.Count(m => m.Attended == day.AddDays(-1)) ?? 0, guildExtras?.Members.Count(m => m.Attended == day) ?? 0, g is null ? 0 : DonationsLeft(mine, day));
        return GuildInfoBody(result, g, attendance, GuildAuthority(), guildExtras?.Wallet ?? 0);
    }

    /// <summary>The GuildInfo_RS body (see <see cref="GuildInfo"/>): attendance = (can take the reward, can attend, yesterday, today, donations left).</summary>
    internal static byte[] GuildInfoBody(int result, GuildView? g, (bool Reward, bool Attend, int Yesterday, int Today, int Donations) attendance,
        IReadOnlyList<(byte Grade, int Bits)> authority, long wallet)
    {
        var row = GuildLevelRow(g?.Level ?? 1);
        var w = PacketWriter.Opcode(GuildInfoRs).U16(result).String(g?.Name ?? "").String(g?.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "")
            .U8(g?.Members.Count ?? 0).U8(row.MaxMembers).U8(g?.Level ?? 0).I64(g?.Exp ?? 0).I64(row.AccumulateExp)
            .U16(g?.Emblem ?? 0).String(g?.Intro ?? "").String(g?.Notice ?? "").U8(g?.JoinRule ?? 0).String(g?.Password ?? "")
            .Bool(attendance.Reward).Bool(attendance.Attend).U8(Math.Min(255, attendance.Yesterday)).U8(Math.Min(255, attendance.Today)).U8(attendance.Donations)
            .I64(g?.Created.ToUnixTimeMilliseconds() ?? 0).I32(0).I64(0);
        for (var i = 0; i < 12; i++) w.Varint(0);   // allies, hostiles, requests, unions (9 struct arrays, 3 id arrays)
        w.Varint(authority.Count);
        foreach (var (grade, bits) in authority) w.U8(grade).I32(bits);
        return w.Varint(1).U8(1).I64(wallet).I32(0).ToArray();   // wallet: EssenceGuild; the owned airspace
    }

    /// <summary>One GuildMemberInfo entry as the client reads it.</summary>
    internal readonly record struct GuildMemberWire(long Account, long DbId, int PcId, string Name, int Level, byte Grade, string Comment, bool Online,
        long LastLogout, int Subzone, int EquipItemLevel, long CombatPower);

    /// <summary>
    /// 0x8a05 GuildMemberInfo_RS, OBSERVED layout (#1928 of rec 20261107-210132: 90 members, 5994 bytes, parsed to the last byte):
    /// result u16, members varint, each {presence mask u8 (bit 1: a shared message follows), account dbid i64, character dbid
    /// i64, pc id i32, name, level i32, grade u8, [message], online (a packed bool sharing one byte per eight members), last
    /// logout i64 (online members too: the previous session's), subzone i32, equip item level i32, combat power i64}. The usmap's
    /// conqueror level, fame, reputations and today's attendance are absent at their defaults (the other mask bits, never set).
    /// </summary>
    byte[] GuildMembers(int result)
    {
        var online = options.World;
        var wires = new List<GuildMemberWire>();
        foreach (var m in guild?.Members ?? [])
        {
            var session = m.CharacterId == CharacterId ? this : online?.FindOnline(m.CharacterId)?.Session;
            var extra = guildExtras?.Members.FirstOrDefault(x => x.CharacterId == m.CharacterId);
            wires.Add(new GuildMemberWire(0, CharDbId(m.CharacterId), m.CreationId, m.Name, session?.level ?? m.Level, (byte)m.Grade, m.Comment, session is not null,
                extra?.LastSeen.ToUnixTimeMilliseconds() ?? 0, 0, session?.EquipItemLevel() ?? 0, session?.CombatPower() ?? 0));
        }
        return GuildMemberInfoBody(result, wires);
    }

    internal static byte[] GuildMemberInfoBody(int result, IReadOnlyList<GuildMemberWire> members)
    {
        var w = PacketWriter.Opcode(GuildMemberInfoRs).U16(result).Varint(members.Count);
        foreach (var m in members)
        {
            w.U8(m.Comment.Length > 0 ? 2 : 0).I64(m.Account).I64(m.DbId).I32(m.PcId).String(m.Name).I32(m.Level).U8(m.Grade);
            if (m.Comment.Length > 0) w.String(m.Comment);
            w.Bool(m.Online).I64(m.LastLogout).I32(m.Subzone).I32(m.EquipItemLevel).I64(m.CombatPower);
        }
        return w.ToArray();
    }

    /// <summary>Common_GuildInfo (search, recommendations, sent applications): the emblem u16 as in GuildInfo_RS (CANDIDATE: no sample).</summary>
    PacketWriter WriteGuildSummary(PacketWriter w, GuildView g, bool requested = false, long requestedAt = 0) =>
        w.I64(g.Id).String(g.Name).String(g.Members.FirstOrDefault(m => m.Grade == 0)?.Name ?? "").U8(g.Level).I64(g.Exp)
            .U8(g.Members.Count).U8(GuildLevelRow(g.Level).MaxMembers).U16(g.Emblem).String(g.Intro).U8(g.JoinRule).Bool(requested).I64(requestedAt).U8(profileRace);

    /// <summary>GuildCreate_RQ {name, join rule u8, password, emblem, intro}: the emblem u16 (as the client writes it elsewhere) when the
    /// rest then reads as exactly one string, else i32 (the usmap's IntProperty).</summary>
    static (string Name, byte Rule, string Password, int Emblem, string Intro) ReadGuildCreate(byte[] plain)
    {
        var r = new PacketReader(plain) { Offset = 2 };
        var (name, rule, password) = (r.String(), r.U8(), r.String());
        var at = r.Offset;
        try
        {
            var emblem16 = r.U16();
            var intro16 = r.String();
            if (r.End) return (name, rule, password, emblem16, intro16);
        }
        catch (ProtocolException) { }
        r.Offset = at;
        return (name, rule, password, r.I32(), r.String());
    }

    async Task<List<byte[]>?> GuildRequestAsync(byte[] plain, CancellationToken ct)
    {
        var op = Opcode(plain);
        if (op is < GuildCreateRq or > GuildHistoryRecordRs) return null;
        if (Store is not { } store || CharacterId is not { } character) return op is GuildInfoRq ? [GuildInfo(1)] : null;
        var r = new PacketReader(plain) { Offset = 2 };
        try
        {
            switch (op)
            {
                case GuildCreateRq:
                {
                    var (name, rule, password, emblem, intro) = ReadGuildCreate(plain);
                    byte[] Reply(int result) => PacketWriter.Opcode(GuildCreateRs).U16(result).I64(guild?.Id ?? 0).String(name).U8(guild?.Level ?? 0).I64(0)
                        .U8(rule).String(password).U16(emblem).String(intro).Varint(GuildAuthority().Count)
                        .Also(w => { foreach (var (grade, bits) in GuildAuthority()) w.U8(grade).I32(bits); }).ToArray();
                    if (name.Length is < 1 or > 12) return [Reply(EResult.Guild_CreateNameInvalidLength)];
                    if (intro.Length > Systems2Tables.Embedded.Global("l10n_guild_intro_max_length")) return [Reply(EResult.Guild_IntroInvalidLength)];
                    if (CostStacks(KinaItemId, GuildCreateKina) is not { } costs) return [Reply(EResult.Guild_CreateNotEnoughMoney)];
                    var outcome = "failed";
                    await Stored(async () => { outcome = await store.CreateGuildAsync(character, name, rule, password, emblem, intro, costs, ct); return []; });
                    if (outcome != "ok") return [Reply(outcome switch { "taken" => EResult.Guild_CreateExistsName, "member" => EResult.Guild_RequestWithHasGuild, _ => EResult.Fail })];
                    await ReloadGuildAsync(ct);
                    return [Reply(0), .. await InventoryChangesAsync(ct, 0, DecreaseGuildCreate), GuildDescUpdate()];
                }
                case GuildInfoRq:
                    await ReloadGuildAsync(ct);
                    return [GuildInfo(guild is null ? EResult.Guild_RequestWithNoGuild : 0)];
                case GuildMemberInfoRq:
                    await ReloadGuildAsync(ct);
                    return [GuildMembers(guild is null ? EResult.Guild_RequestWithNoGuild : 0)];
                case GuildSearchRq:
                {
                    var name = r.String();
                    IReadOnlyList<GuildView> found = [];
                    await Stored(async () => { found = await store.SearchGuildsAsync(name, ct); return []; });
                    var sent = await SentApplicationsAsync(ct);
                    var w = PacketWriter.Opcode(GuildSearchRs).U16(0).Varint(found.Count);
                    foreach (var g in found)
                        WriteGuildSummary(w, g, sent.Any(s => s.Guild.Id == g.Id), sent.FirstOrDefault(s => s.Guild.Id == g.Id).Requested.ToUnixTimeMilliseconds());
                    return [w.ToArray()];
                }
                case GuildJoinRq:
                {
                    var (id, _, comment, password) = ((long)r.U64(), r.U8(), r.String(), r.String());
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.JoinGuildAsync(character, id, password, GuildMaxMembers, ct); return []; });
                    // A Manual legion takes an application instead (GameSession.Legion.cs); JoinGuildAsync answers "denied" for both refusing rules.
                    if (outcome == "denied" && await ApplyToGuildAsync(id, comment, ct) is { } applied) return applied;
                    if (outcome != "ok")
                        return [WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(outcome switch
                        {
                            "member" => EResult.Guild_RequestWithHasGuild, "full" => EResult.Guild_JoinMemberFull, "password" => EResult.Guild_JoinWrongPassword,
                            "denied" => EResult.Guild_JoinAutoDeny, _ => EResult.Guild_JoinGuildNotFound,
                        }).I64(id).U8(0), null, 2).ToArray()];
                    await ReloadGuildAsync(ct);
                    TellGuild(guild, PacketWriter.Opcode(GuildJoinNt).String(StoredName).ToArray(), PacketWriter.Opcode(GuildMemberConnectedNt).String(StoredName).ToArray());
                    return [WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(0).I64(id).U8(0), guild, 2).ToArray(), GuildDescUpdate(), .. Achieve("GuildJoin")];
                }
                case GuildLeaveRq:
                {
                    var before = guild ?? await ReloadGuildAsync(ct);
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.LeaveGuildAsync(character, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildLeaveRs).U16(outcome == "master" ? EResult.Guild_RequestWithNoAuth : EResult.Guild_RequestWithNoGuild).I64(0).ToArray()];
                    TellGuild(before, PacketWriter.Opcode(GuildLeaveNt).String(StoredName).U8(GuildReasonLeave).I64(0).ToArray());
                    guild = null;
                    guildExtras = null;
                    return [PacketWriter.Opcode(GuildLeaveRs).U16(0).I64(0).ToArray(), GuildDescUpdate()];
                }
                case GuildKickRq:
                {
                    var nickname = r.String();
                    var before = guild ?? await ReloadGuildAsync(ct);
                    if (!HasGuildAuthority(GuildAuthorityKickout)) return [PacketWriter.Opcode(GuildKickRs).U16(EResult.Guild_RequestWithNoAuth).ToArray()];
                    if (nickname.Equals(StoredName, StringComparison.OrdinalIgnoreCase)) return [PacketWriter.Opcode(GuildKickRs).U16(EResult.Guild_KickCannotSelf).ToArray()];
                    if (before?.Members.FirstOrDefault(m => m.Name.Equals(nickname, StringComparison.OrdinalIgnoreCase)) is { Grade: 0 })
                        return [PacketWriter.Opcode(GuildKickRs).U16(EResult.Guild_KickCannotGuildMaster).ToArray()];
                    var outcome = "none";
                    await Stored(async () => { outcome = await store.KickGuildMemberAsync(character, nickname, ct); return []; });
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildKickRs).U16(outcome == "authority" ? EResult.Guild_RequestWithNoAuth : EResult.Guild_KickNotFoundMember).ToArray()];
                    var notice = PacketWriter.Opcode(GuildLeaveNt).String(nickname).U8(GuildReasonKicked).I64(0).ToArray();
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
                    if (outcome != "ok") return [PacketWriter.Opcode(GuildDisbandRs).U16(EResult.Guild_RequestWithNoAuth).ToArray()];
                    TellGuild(before, PacketWriter.Opcode(GuildLeaveNt).String(StoredName).U8(GuildReasonLeaveForDisband).I64(0).ToArray());
                    if (before is not null && options.World is { } world)
                        foreach (var m in before.Members.Where(m => m.CharacterId != character))
                            if (world.FindOnline(m.CharacterId) is { } online)
                                _ = online.Session.InvokeAsync(_ => (true, online.Session.GuildRemoved()));
                    guild = null;
                    guildExtras = null;
                    return [PacketWriter.Opcode(GuildDisbandRs).U16(0).ToArray(), GuildDescUpdate()];
                }
                default:
                    return await LegionRequestAsync(op, r, store, character, ct);
            }
        }
        catch (ProtocolException) { return []; }
    }

    /// <summary>Kicked or disbanded from elsewhere: the legion is gone; the tag clears (on this session's loop).</summary>
    public List<byte[]> GuildRemoved()
    {
        guild = null;
        guildExtras = null;
        return resources is null ? [] : [GuildDescUpdate()];
    }

    /// <summary>World entry: the legion tag and the members' notice (GuildMemberConnected_NT).</summary>
    async Task LoadGuildAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        guild = await store.GuildOfAsync(character, ct);
        guildExtras = guild is not null && store is ILegionStore legion ? await legion.GuildExtrasAsync(character, ct) : null;
        // The week's Abyss Points (GameSession.Abyss.cs), loaded with the legion at the select.
        LoadAbyssWeek(await store.CharacterSettingsAsync(character, ct), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    List<byte[]> GuildAtEntry()
    {
        // The entry's constant AbyssPointUpdate_NT says 0 (EntryConstants, sent after this): a week with points says so again at
        // the next tick.
        if (abyssPointsWeek > 0) pendingAchievementPackets.Add(AbyssPointUpdate());
        if (guild is null) return [];
        TellGuild(guild, PacketWriter.Opcode(GuildMemberConnectedNt).String(StoredName).ToArray());
        return [GuildDescUpdate()];
    }
}

static class PacketWriterExtensions
{
    public static PacketWriter Also(this PacketWriter w, Action<PacketWriter> write) { write(w); return w; }
}
