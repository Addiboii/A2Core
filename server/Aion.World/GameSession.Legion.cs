using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Legion management (feature/systems-2, migration 089): the rest of the 0x8a run (catalogue index + 0x8797, the same base as
// the OBSERVED 0x8a03/0x8a05/0x8a1d/0x8a39/0x8a45). Bodies from the usmap (CANDIDATE where no sample exists; the client's
// requests are encrypted in the recordings), results u16 with the client's EResult Guild_* codes, emblem ids u16.
//   applications (EGuildJoinRule Manual, GlobalSetting guild_join_req_count 3 / guild_join_recv_count 20): GuildJoin_RQ files
//     one (GuildJoin_RS join result 1 Requested), officers online get GuildJoinRequested_NT, GuildRecvJoinRequests lists them,
//     GuildJoinConfirm answers (GuildJoinConfirm_NT to the applicant, GuildJoin_NT to the members), GuildJoinCancel withdraws,
//     GuildSendJoinRequests lists the applicant's own (OBSERVED empty reply 1d8a 0000 00);
//   settings: join rule, emblem (GuildEmblem.dat unlock level), intro (48), notice (160), member grade (GuildData OfficerNum
//     officers), grade authority (EGuildAuthority bits; GuildMemberGradeAuthority.dat defaults), member comment (32), master
//     delegation; each with its _NT to the members online;
//   attendance (once per attendance day, GlobalSetting attendance_reset_time 16:00) and its reward claim, donations (3 a day:
//     OBSERVED remain 3 in GuildInfo_RS); both raise the legion's exp and level (GuildData AccumulateExp) -> GuildLevel_NT.
//     CANDIDATE amounts (no table, no recording): attendance +10 legion exp, donation levels 1/2/3 = 10000/100000/1000000 Kina
//     for +10/+100/+1000 exp; the attendance reward grants nothing (unknown) and is only marked taken.
//   hall (GuildAgitInfo_RS OBSERVED form: no hall), history (empty), storage (empty, 20 slots: the legion warehouse is not built).
public sealed partial class GameSession
{
    public const ushort GuildRecommendedListRq = 0x8a06, GuildRecommendedListRs = 0x8a07, GuildJoinNt = 0x8a11, GuildJoinRequestedNt = 0x8a12,
        GuildJoinConfirmRq = 0x8a13, GuildJoinConfirmRs = 0x8a14, GuildJoinConfirmNt = 0x8a15, GuildJoinCancelRq = 0x8a16, GuildJoinCancelRs = 0x8a17,
        GuildJoinCancelNt = 0x8a18, GuildRecvJoinRequestsRq = 0x8a19, GuildRecvJoinRequestsRs = 0x8a1a, GuildSendJoinRequestsRq = 0x8a1c,
        GuildSendJoinRequestsRs = 0x8a1d, GuildSetJoinRuleRq = 0x8a1e, GuildSetJoinRuleRs = 0x8a1f, GuildSetJoinRuleNt = 0x8a20,
        GuildSetEmblemRq = 0x8a21, GuildSetEmblemRs = 0x8a22, GuildSetEmblemNt = 0x8a23, GuildSetIntroRq = 0x8a24, GuildSetIntroRs = 0x8a25,
        GuildSetIntroNt = 0x8a26, GuildSetNoticeRq = 0x8a27, GuildSetNoticeRs = 0x8a28, GuildSetNoticeNt = 0x8a29, GuildSetGradeRq = 0x8a2a,
        GuildSetGradeRs = 0x8a2b, GuildSetGradeNt = 0x8a2c, GuildSetAuthorityRq = 0x8a2d, GuildSetAuthorityRs = 0x8a2e, GuildSetAuthorityNt = 0x8a2f,
        GuildEditCommentRq = 0x8a30, GuildEditCommentRs = 0x8a31, GuildLevelNt = 0x8a32, GuildDelegateRq = 0x8a36, GuildDelegateRs = 0x8a37,
        GuildDelegateNt = 0x8a38, GuildDonationInfoRq = 0x8a3a, GuildDonationInfoRs = 0x8a3b, GuildDonationRq = 0x8a3c, GuildDonationRs = 0x8a3d,
        GuildAttendanceInfoRq = 0x8a3e, GuildAttendanceInfoRs = 0x8a3f, GuildAttendanceRq = 0x8a40, GuildAttendanceRs = 0x8a41,
        GuildAttendanceRewardRq = 0x8a42, GuildAttendanceRewardRs = 0x8a43, GuildAgitInfoRq = 0x8a44, GuildAgitInfoRs = 0x8a45,
        UpdateGuildWalletNt = 0x8a9f, GuildStorageInfoRq = 0x8aa0, GuildStorageInfoRs = 0x8aa1, GuildHistoryRecordRq = 0x8ab7, GuildHistoryRecordRs = 0x8ab8;
    /// <summary>EGuildAuthority bits (usmap; GuildMemberGradeAuthority.dat Master 8190 = bits 1..12).</summary>
    public const int GuildAuthorityChangeGrade = 1, GuildAuthorityKickout = 2, GuildAuthorityChangeNotice = 3, GuildAuthorityChangeIntro = 4,
        GuildAuthorityChangeEmblem = 5, GuildAuthorityChangeJoinRule = 6, GuildAuthorityJoinApproval = 7;
    /// <summary>CANDIDATE (no table, no recording): legion exp for an attendance; donation levels 1..3 -> (Kina, legion exp).</summary>
    public const long GuildAttendanceExp = 10;
    public static readonly (long Kina, long Exp)[] GuildDonationLevels = [(10_000, 10), (100_000, 100), (1_000_000, 1000)];
    public const int GuildDonationsPerDay = 3;   // OBSERVED: GuildInfo_RS remain donation count 3 for a member who gave nothing
    const byte DecreaseGuildDonation = 0;

    static Systems2Tables Tables2 => Systems2Tables.Embedded;
    static long[] GuildLevelExp => Tables2.GuildLevels.OrderBy(l => l.Level).Select(l => l.AccumulateExp).ToArray();

    /// <summary>The attendance day: days turn at GlobalSetting attendance_reset_time (16:00 UTC).</summary>
    internal static DateOnly GuildDay(long nowMs) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs - Math.Max(0, Tables2.Global("attendance_reset_time"))).UtcDateTime);

    static bool CanTakeAttendanceReward(GuildMemberExtra? mine, DateOnly day) => mine is not null && mine.Attended == day && mine.Rewarded != day;

    static int DonationsLeft(GuildMemberExtra? mine, DateOnly day) => Math.Max(0, GuildDonationsPerDay - (mine?.Donated == day ? mine.Donations : 0));

    bool HasGuildAuthority(int bit)
    {
        if (guild is null) return false;
        var grade = MyGuildGrade();
        if (grade == 0) return true;
        return GuildAuthority().FirstOrDefault(a => a.Grade == grade).Bits is var bits && (bits & (1 << bit)) != 0;
    }

    ILegionStore? LegionStore => Store as ILegionStore;

    async Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(CancellationToken ct) =>
        LegionStore is { } legion && CharacterId is { } character
            ? await StoredValue(() => legion.SentApplicationsAsync(character, ct), (IReadOnlyList<(GuildView, DateTimeOffset)>)[])
            : [];

    /// <summary>Sends packets to every member online, this player included (returned rather than queued).</summary>
    List<byte[]> TellWholeGuild(params byte[][] packets)
    {
        TellGuild(guild, packets);
        return [.. packets];
    }

    /// <summary>The other members online reload the legion (grade, emblem, authority changed) and show their tag again.</summary>
    void RefreshGuildMembers()
    {
        if (guild is null || options.World is not { } world) return;
        foreach (var m in guild.Members.Where(m => m.CharacterId != CharacterId))
            if (world.FindOnline(m.CharacterId) is { } online)
                _ = online.Session.InvokeAsync(async _ => { await online.Session.ReloadGuildAsync(CancellationToken.None); return (true, online.Session.guild is null ? [] : new List<byte[]> { online.Session.GuildDescUpdate() }); });
    }

    /// <summary>GuildJoin_RQ to a Manual legion: an application (null when the legion takes none: the caller answers AutoDeny).</summary>
    async Task<List<byte[]>?> ApplyToGuildAsync(long guildId, string comment, CancellationToken ct)
    {
        if (LegionStore is not { } legion || CharacterId is not { } character) return null;
        var outcome = await StoredValue(() => legion.ApplyGuildAsync(character, guildId, comment, (int)Tables2.Global("guild_join_req_count"), (int)Tables2.Global("guild_join_recv_count"), ct), "none");
        if (outcome == "rule") return null;
        byte[] Reply(int result, byte joinResult) => WriteGuildDesc(PacketWriter.Opcode(GuildJoinRs).U16(result).I64(guildId).U8(joinResult), null, 2).ToArray();
        if (outcome != "ok")
            return [Reply(outcome switch { "member" => EResult.Guild_RequestWithHasGuild, "exists" => EResult.Guild_TooManyRequest, "limit" => EResult.Guild_JoinSendReqFull, _ => EResult.Guild_JoinGuildNotFound }, 0)];
        // The legion's officers online hear of it (GuildJoinRequested_NT).
        var requested = PacketWriter.Opcode(GuildJoinRequestedNt).I32(Profile()?.PcId ?? 1).String(StoredName).String(comment).I64(lastNow).I64(0).Bool(true)
            .I64(0).U8(0).I32(level).I32(0).I32(EquipItemLevel()).I64(CombatPower()).ToArray();
        if (options.World is { } world)
            foreach (var online in world.Online.Values)
                if (online.Session.guild?.Id == guildId && online.Session.HasGuildAuthority(GuildAuthorityJoinApproval)) online.Send(requested);
        return [Reply(0, 1)];
    }

    /// <summary>The legion requests of the 0x8a run; null for an opcode it does not serve, so the main dispatch still gets it
    /// (the run also holds the Pantheon's AgitDeco requests 0x8ab3/0x8ab5, GameSession.Pantheon.cs).</summary>
    async Task<List<byte[]>?> LegionRequestAsync(ushort op, PacketReader r, INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        var legion = LegionStore;
        var now = lastNow;
        switch (op)
        {
            // GuildRecommendedList_RQ {order u8}: legions of this race taking members, fullest first.
            case GuildRecommendedListRq:
            {
                IReadOnlyList<GuildView> found = legion is null ? [] : await StoredValue(() => legion.RecommendedGuildsAsync(character, 20, ct), (IReadOnlyList<GuildView>)[]);
                var sent = await SentApplicationsAsync(ct);
                var w = PacketWriter.Opcode(GuildRecommendedListRs).U16(0).Varint(found.Count);
                foreach (var g in found) WriteGuildSummary(w, g, sent.Any(s => s.Guild.Id == g.Id), sent.FirstOrDefault(s => s.Guild.Id == g.Id).Requested.ToUnixTimeMilliseconds());
                return [w.ToArray()];
            }
            // GuildSendJoinRequests_RQ (no body): this character's applications (OBSERVED empty reply 1d8a 0000 00 at every entry).
            case GuildSendJoinRequestsRq:
            {
                var sent = await SentApplicationsAsync(ct);
                var w = PacketWriter.Opcode(GuildSendJoinRequestsRs).U16(0).Varint(sent.Count);
                foreach (var (g, at) in sent) WriteGuildSummary(w, g, true, at.ToUnixTimeMilliseconds());
                // No unlock update with it (official): chat is in the select's 0xe257 (info-popups 2026-11-11).
                return [w.ToArray()];
            }
            // GuildJoinCancel_RQ {guild id i64, bool}: an application withdrawn; the legion's officers hear GuildJoinCancel_NT.
            case GuildJoinCancelRq:
            {
                var guildId = r.I64();
                var outcome = legion is null ? "none" : await StoredValue(() => legion.CancelApplicationAsync(character, guildId, ct), "none");
                if (outcome == "ok" && options.World is { } world)
                {
                    var notice = PacketWriter.Opcode(GuildJoinCancelNt).I32(Profile()?.PcId ?? 1).String(StoredName).String("").ToArray();
                    foreach (var online in world.Online.Values)
                        if (online.Session.guild?.Id == guildId && online.Session.HasGuildAuthority(GuildAuthorityJoinApproval)) online.Send(notice);
                }
                return [PacketWriter.Opcode(GuildJoinCancelRs).U16(outcome == "ok" ? 0 : EResult.Guild_ConfirmNotFoundRequest).ToArray()];
            }
            // GuildRecvJoinRequests_RQ (no body): the legion's pending applications (GuildJoinRecv).
            case GuildRecvJoinRequestsRq:
            {
                await ReloadGuildAsync(ct);
                var applications = guildExtras?.Applications ?? [];
                var w = PacketWriter.Opcode(GuildRecvJoinRequestsRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0).Varint(applications.Count);
                foreach (var a in applications)
                {
                    var online = options.World?.FindOnline(a.CharacterId)?.Session;
                    w.I32(a.CreationId).String(a.Name).String(a.Comment).I64(a.Requested.ToUnixTimeMilliseconds()).I64(online is null ? a.LastSeen.ToUnixTimeMilliseconds() : 0)
                        .Bool(online is not null).I64(0).I32(online?.level ?? a.Level).I32(0).I32(online?.EquipItemLevel() ?? 0).I64(online?.CombatPower() ?? 0);
                }
                return [w.ToArray()];
            }
            // GuildJoinConfirm_RQ {nickname, accept}: an officer answers an application.
            case GuildJoinConfirmRq:
            {
                var (nickname, accept) = (r.String(), r.Bool());
                byte[] Reply(int result) => PacketWriter.Opcode(GuildJoinConfirmRs).U16(result).String(nickname).Bool(accept).ToArray();
                if (guild is null) return [Reply(EResult.Guild_RequestWithNoGuild)];
                if (!HasGuildAuthority(GuildAuthorityJoinApproval) || legion is null) return [Reply(EResult.Guild_RequestWithNoAuth)];
                var (outcome, applicant) = await StoredValue(() => legion.AnswerApplicationAsync(character, nickname, accept, GuildLevelRow(guild.Level).MaxMembers, ct), ("none", Guid.Empty));
                if (outcome != "ok")
                    return [Reply(outcome switch { "full" => EResult.Guild_ConfirmMemberFull, "member" => EResult.Guild_RequestWithHasGuild, "authority" => EResult.Guild_RequestWithNoAuth, _ => EResult.Guild_ConfirmNotFoundRequest })];
                var guildId = guild.Id;
                if (options.World?.FindOnline(applicant) is { } joined)
                    _ = joined.Session.InvokeAsync(async _ =>
                    {
                        var s = joined.Session;
                        var packets = new List<byte[]> { PacketWriter.Opcode(GuildJoinConfirmNt).I64(guildId).Bool(accept).ToArray() };
                        if (accept && await s.ReloadGuildAsync(CancellationToken.None) is not null) packets.Add(s.GuildDescUpdate());
                        return (true, packets);
                    });
                if (!accept) return [Reply(0)];
                await ReloadGuildAsync(ct);
                return [Reply(0), .. TellWholeGuild(PacketWriter.Opcode(GuildJoinNt).String(nickname).ToArray())];
            }
            // GuildSetJoinRule_RQ {rule u8, password}: GuildSetJoinRule_NT to the members.
            case GuildSetJoinRuleRq:
            {
                var (rule, password) = (r.U8(), r.String());
                byte[] Reply(int result) => PacketWriter.Opcode(GuildSetJoinRuleRs).U16(result).U8(rule).ToArray();
                if (!HasGuildAuthority(GuildAuthorityChangeJoinRule) || legion is null) return [Reply(guild is null ? EResult.Guild_RequestWithNoGuild : EResult.Guild_RequestWithNoAuth)];
                if (rule > 3) return [Reply(EResult.Guild_InvalidJoinRule)];
                if (rule == 3 && (password.Length < Tables2.Global("l10n_guild_password_min_length") || password.Length > Tables2.Global("l10n_guild_password_max_length")))
                    return [Reply(EResult.Guild_PasswordInvalidLength)];
                if (await StoredValue(() => legion.UpdateGuildAsync(character, "join_rule", rule, password, ct), "none") != "ok") return [Reply(EResult.Guild_RequestWithNoAuth)];
                await ReloadGuildAsync(ct);
                return [Reply(0), .. TellWholeGuild(PacketWriter.Opcode(GuildSetJoinRuleNt).U8(rule).String(rule == 3 ? password : "").ToArray())];
            }
            // GuildSetEmblemID_RQ {emblem}: GuildEmblem.dat id unlocked at the legion's level; every member's tag shows it.
            case GuildSetEmblemRq:
            {
                var emblem = r.Remaining >= 4 ? r.I32() : r.U16();
                byte[] Reply(int result) => PacketWriter.Opcode(GuildSetEmblemRs).U16(result).U16(emblem).ToArray();
                if (!HasGuildAuthority(GuildAuthorityChangeEmblem) || legion is null) return [Reply(guild is null ? EResult.Guild_RequestWithNoGuild : EResult.Guild_RequestWithNoAuth)];
                if (Tables2.GuildEmblems.FirstOrDefault(e => e.Id == emblem) is not { } row) return [Reply(EResult.Guild_SetEmblemInvalidID)];
                if (row.UnlockLevel > guild!.Level) return [Reply(EResult.Guild_SetEmblemNotEnoughGrade)];
                if (await StoredValue(() => legion.UpdateGuildAsync(character, "emblem", emblem, "", ct), "none") != "ok") return [Reply(EResult.Guild_RequestWithNoAuth)];
                await ReloadGuildAsync(ct);
                RefreshGuildMembers();
                return [Reply(0), .. TellWholeGuild(PacketWriter.Opcode(GuildSetEmblemNt).U16(emblem).ToArray()), GuildDescUpdate()];
            }
            // GuildSetIntro_RQ / GuildSetNotice_RQ {text}.
            case GuildSetIntroRq or GuildSetNoticeRq:
            {
                var text = r.String();
                var intro = op == GuildSetIntroRq;
                byte[] Reply(int result) => PacketWriter.Opcode(intro ? GuildSetIntroRs : GuildSetNoticeRs).U16(result).String(text).ToArray();
                if (!HasGuildAuthority(intro ? GuildAuthorityChangeIntro : GuildAuthorityChangeNotice) || legion is null)
                    return [Reply(guild is null ? EResult.Guild_RequestWithNoGuild : EResult.Guild_RequestWithNoAuth)];
                if (text.Length > Tables2.Global(intro ? "l10n_guild_intro_max_length" : "l10n_guild_notice_max_length"))
                    return [Reply(intro ? EResult.Guild_IntroInvalidLength : EResult.Guild_SetNoticeInvalidLength)];
                if (await StoredValue(() => legion.UpdateGuildAsync(character, intro ? "intro" : "notice", 0, text, ct), "none") != "ok") return [Reply(EResult.Guild_RequestWithNoAuth)];
                await ReloadGuildAsync(ct);
                return [Reply(0), .. TellWholeGuild(PacketWriter.Opcode(intro ? GuildSetIntroNt : GuildSetNoticeNt).String(text).ToArray())];
            }
            // GuildSetMemberGrade_RQ {nickname, grade u8}: officers at most GuildData OfficerNum; the master's grade moves by delegation only.
            case GuildSetGradeRq:
            {
                var (nickname, grade) = (r.String(), r.U8());
                byte[] Reply(int result) => PacketWriter.Opcode(GuildSetGradeRs).U16(result).ToArray();
                if (!HasGuildAuthority(GuildAuthorityChangeGrade) || legion is null) return [Reply(guild is null ? EResult.Guild_RequestWithNoGuild : EResult.Guild_RequestWithNoAuth)];
                if (grade is 0) return [Reply(EResult.Guild_SetGradeGuildMaster)];
                if (grade is > 2) return [Reply(EResult.Guild_SetGradeInvalidGrade)];
                if (guild!.Members.FirstOrDefault(m => m.Name.Equals(nickname, StringComparison.OrdinalIgnoreCase)) is not { } target) return [Reply(EResult.Guild_SetGradeNicknameNotExists)];
                if (target.Grade == 0) return [Reply(EResult.Guild_SetGradeGuildMaster)];
                var (outcome, who) = await StoredValue(() => legion.SetMemberGradeAsync(character, nickname, grade, GuildLevelRow(guild.Level).Officers, ct), ("none", Guid.Empty));
                if (outcome != "ok") return [Reply(outcome switch { "full" => EResult.Guild_SetGradeOfficerCountFull, "none" => EResult.Guild_SetGradeNotMember, _ => EResult.Guild_RequestWithNoAuth })];
                await ReloadGuildAsync(ct);
                RefreshGuildMembers();
                return [Reply(0), .. TellWholeGuild(PacketWriter.Opcode(GuildSetGradeNt).String(target.Name).U8(grade).ToArray())];
            }
            // GuildSetAuthority_RQ {reset bool, [{grade u8, bits i32}]}: the master's grade authority (reset = the table's).
            case GuildSetAuthorityRq:
            {
                var reset = r.Bool();
                var count = (int)r.Varint();
                var set = new Dictionary<int, int>();
                for (var i = 0; i < count && i < 8; i++) { var grade = r.U8(); var bits = r.I32(); if (grade is 1 or 2) set[grade] = bits & Tables2.DefaultAuthority[0].Bits; }
                byte[] Reply(int result) => PacketWriter.Opcode(GuildSetAuthorityRs).U16(result).ToArray();
                if (guild is null) return [Reply(EResult.Guild_RequestWithNoGuild)];
                if (MyGuildGrade() != 0 || legion is null) return [Reply(EResult.Guild_CannotGrantAuth)];
                if (await StoredValue(() => legion.SetAuthorityAsync(character, reset ? new Dictionary<int, int>() : set, ct), "none") != "ok") return [Reply(EResult.Guild_RequestWithNoAuth)];
                await ReloadGuildAsync(ct);
                RefreshGuildMembers();
                var authority = GuildAuthority();
                var notice = PacketWriter.Opcode(GuildSetAuthorityNt).Varint(authority.Count).Also(w => { foreach (var (g, b) in authority) w.U8(g).I32(b); }).ToArray();
                return [Reply(0), .. TellWholeGuild(notice)];
            }
            // GuildMemberEditComment_RQ {comment} (32 characters).
            case GuildEditCommentRq:
            {
                var comment = r.String();
                byte[] Reply(int result) => PacketWriter.Opcode(GuildEditCommentRs).U16(result).String(comment).ToArray();
                if (guild is null || legion is null) return [Reply(EResult.Guild_RequestWithNoGuild)];
                if (comment.Length > Tables2.Global("l10n_guild_member_comment_max_length")) return [Reply(EResult.Guild_MemberEditCommentInvalidLength)];
                if (await StoredValue(() => legion.SetGuildCommentAsync(character, comment, ct), "none") != "ok") return [Reply(EResult.Guild_RequestWithNoGuild)];
                await ReloadGuildAsync(ct);
                return [Reply(0)];
            }
            // GuildDelegateMaster_RQ {nickname}: the master hands over and becomes an officer.
            case GuildDelegateRq:
            {
                var nickname = r.String();
                byte[] Body(ushort opcode, string newName) => PacketWriter.Opcode(opcode).String(StoredName).U8(1).String(newName).U8(0).ToArray();
                if (guild is null) return [PacketWriter.Opcode(GuildDelegateRs).U16(EResult.Guild_RequestWithNoGuild).String("").U8(0).String("").U8(0).ToArray()];
                if (MyGuildGrade() != 0 || legion is null) return [PacketWriter.Opcode(GuildDelegateRs).U16(EResult.Guild_RequestWithNoAuth).String("").U8(0).String("").U8(0).ToArray()];
                var (outcome, _) = await StoredValue(() => legion.DelegateMasterAsync(character, nickname, ct), ("none", Guid.Empty));
                if (outcome != "ok") return [PacketWriter.Opcode(GuildDelegateRs).U16(EResult.Guild_DelegateGuildMasterNotMember).String("").U8(0).String("").U8(0).ToArray()];
                var newName = guild.Members.FirstOrDefault(m => m.Name.Equals(nickname, StringComparison.OrdinalIgnoreCase))?.Name ?? nickname;
                var rs = PacketWriter.Opcode(GuildDelegateRs).U16(0).String(StoredName).U8(1).String(newName).U8(0).ToArray();
                TellGuild(guild, Body(GuildDelegateNt, newName));
                await ReloadGuildAsync(ct);
                RefreshGuildMembers();
                return [rs, GuildDescUpdate()];
            }
            // GuildDonationInfo_RQ (no body): donations left today.
            case GuildDonationInfoRq:
                await ReloadGuildAsync(ct);
                return [PacketWriter.Opcode(GuildDonationInfoRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0)
                    .U8(DonationsLeft(guildExtras?.Members.FirstOrDefault(m => m.CharacterId == character), GuildDay(now))).ToArray()];
            // GuildDonation_RQ {level u8}: Kina for legion exp (CANDIDATE amounts, see the header).
            case GuildDonationRq:
            {
                var donationLevel = r.U8();
                byte[] Reply(int result, int left) => PacketWriter.Opcode(GuildDonationRs).U16(result).U8(donationLevel).U8(left).ToArray();
                if (guild is null || legion is null) return [Reply(EResult.Guild_RequestWithNoGuild, 0)];
                if (donationLevel is < 1 or > 3) return [Reply(EResult.Guild_DonationInvalidGrade, 0)];
                var (kina, exp) = GuildDonationLevels[donationLevel - 1];
                var day = GuildDay(now);
                if (DonationsLeft(guildExtras?.Members.FirstOrDefault(m => m.CharacterId == character), day) <= 0) return [Reply(EResult.Guild_DonationFinishedToday, 0)];
                if (CostStacks(KinaItemId, kina) is not { } costs) return [Reply(EResult.Guild_DonationNotEnoughMoney, DonationsLeft(guildExtras?.Members.FirstOrDefault(m => m.CharacterId == character), day))];
                var before = guild.Level;
                var (outcome, newLevel, _, left) = await StoredValue(() => legion.GuildDonateAsync(character, day, GuildDonationsPerDay, costs, exp, GuildLevelExp, ct), ("none", 0, 0L, 0));
                if (outcome != "ok") return [Reply(outcome switch { "limit" => EResult.Guild_DonationFinishedToday, "cost" => EResult.Guild_DonationNotEnoughMoney, _ => EResult.Guild_RequestWithNoGuild }, left)];
                await ReloadGuildAsync(ct);
                List<byte[]> replies = [Reply(0, left), .. await InventoryChangesAsync(ct, 0, DecreaseGuildDonation)];
                if (newLevel > before) replies.AddRange(TellWholeGuild(PacketWriter.Opcode(GuildLevelNt).U8(newLevel).ToArray()));
                Note($"legion {guild.Name}: donation level {donationLevel} ({kina} Kina, +{exp} exp)");
                return replies;
            }
            // GuildAttendanceInfo_RQ (no body): today's and yesterday's attendance, whether this member can attend / take the reward.
            case GuildAttendanceInfoRq:
            {
                await ReloadGuildAsync(ct);
                var day = GuildDay(now);
                var mine = guildExtras?.Members.FirstOrDefault(m => m.CharacterId == character);
                return [PacketWriter.Opcode(GuildAttendanceInfoRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0)
                    .U8(Math.Min(255, guildExtras?.Members.Count(m => m.Attended == day) ?? 0)).U8(Math.Min(255, guildExtras?.Members.Count(m => m.Attended == day.AddDays(-1)) ?? 0))
                    .Bool(guild is not null && mine?.Attended != day).Bool(CanTakeAttendanceReward(mine, day)).ToArray()];
            }
            // GuildAttendance_RQ (no body): once a day; legion exp (CANDIDATE amount).
            case GuildAttendanceRq:
            {
                if (guild is null || legion is null) return [PacketWriter.Opcode(GuildAttendanceRs).U16(EResult.Guild_RequestWithNoGuild).ToArray()];
                var before = guild.Level;
                var (outcome, newLevel, _) = await StoredValue(() => legion.GuildAttendAsync(character, GuildDay(now), GuildAttendanceExp, GuildLevelExp, ct), ("none", 0, 0L));
                if (outcome != "ok") return [PacketWriter.Opcode(GuildAttendanceRs).U16(outcome == "done" ? EResult.Guild_AttendanceAlreadyDone : EResult.Guild_RequestWithNoGuild).ToArray()];
                await ReloadGuildAsync(ct);
                List<byte[]> replies = [PacketWriter.Opcode(GuildAttendanceRs).U16(0).ToArray(), .. Achieve("GuildAttendance")];
                if (newLevel > before) replies.AddRange(TellWholeGuild(PacketWriter.Opcode(GuildLevelNt).U8(newLevel).ToArray()));
                return replies;
            }
            // GuildAttendanceReward_RQ (no body): after today's attendance, once (the reward itself is unknown: nothing is granted).
            case GuildAttendanceRewardRq:
            {
                byte[] Reply(int result) => PacketWriter.Opcode(GuildAttendanceRewardRs).U16(result)
                    .U8(Math.Min(255, guildExtras?.Members.Count(m => m.Attended == GuildDay(now).AddDays(-1)) ?? 0)).ToArray();
                if (guild is null || legion is null) return [Reply(EResult.Guild_RequestWithNoGuild)];
                var mine = guildExtras?.Members.FirstOrDefault(m => m.CharacterId == character);
                if (mine?.Attended != GuildDay(now)) return [Reply(EResult.Guild_AttendanceRewardAttendanceFirst)];
                var outcome = await StoredValue(() => legion.GuildAttendanceRewardAsync(character, GuildDay(now), ct), "none");
                await ReloadGuildAsync(ct);
                return [Reply(outcome == "ok" ? 0 : EResult.Guild_AttendanceRewardAlreadyDone)];
            }
            // GuildAgitInfo_RQ (no body): no hall rented (OBSERVED reply 458a 0000 00000000 0000000000000000, #1933 of rec 20261107-210132).
            case GuildAgitInfoRq:
                return [PacketWriter.Opcode(GuildAgitInfoRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0).I32(0).I64(0).ToArray()];
            // GuildHistoryRecord_RQ {last time i64}: no history kept (CANDIDATE empty list).
            case GuildHistoryRecordRq:
                return [PacketWriter.Opcode(GuildHistoryRecordRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0).I64(r.Remaining >= 8 ? r.I64() : 0).Varint(0).ToArray()];
            // GuildStorageInfo_RQ (no body): the legion warehouse is not built: empty, no extensions (CANDIDATE).
            case GuildStorageInfoRq:
                return [PacketWriter.Opcode(GuildStorageInfoRs).U16(guild is null ? EResult.Guild_RequestWithNoGuild : 0).Varint(0).I64(0).U8(0).I32(0).I64(0).ToArray()];
        }
        return null;
    }
}
