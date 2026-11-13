using Aion.Application.Gameplay;
using Aion.World;

/// <summary>The checks' legion management store (ILegionStore, migration 089) in memory, on the fake store's one legion (GuildRow).</summary>
sealed partial class FakeStore : ILegionStore
{
    public Dictionary<int, int> LegionAuthority { get; } = [];
    public long LegionWallet { get; set; }
    public Dictionary<Guid, GuildMemberExtra> LegionMembers { get; } = [];
    public List<GuildApplication> LegionApplications { get; } = [];
    /// <summary>Other legions (recommendations, applications).</summary>
    public List<GuildView> OtherGuilds { get; } = [];

    /// <summary>Joining another legion (OtherGuilds): AutoJoin is not modelled here; Manual and AutoDeny answer "denied" as the database does.</summary>
    public Task<string> JoinGuildAsync(Guid character, long guild, string password, int maxMembers, CancellationToken ct) =>
        Task.FromResult(OtherGuilds.FirstOrDefault(g => g.Id == guild) is { JoinRule: 1 or 2 } ? "denied" : "none");

    GuildMemberExtra ExtraOf(Guid character) => LegionMembers.GetValueOrDefault(character) ?? new GuildMemberExtra(character, null, null, null, 0, DateTimeOffset.UnixEpoch, 0);

    void SetMembers(Func<GuildMember, GuildMember> change)
    {
        if (GuildRow is { } g) GuildRow = g with { Members = g.Members.Select(change).ToList() };
    }

    public Task<GuildExtras?> GuildExtrasAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(GuildRow is { } g && g.Members.Any(m => m.CharacterId == character)
            ? new GuildExtras(g.Id, new Dictionary<int, int>(LegionAuthority), LegionWallet, g.Members.Select(m => ExtraOf(m.CharacterId)).ToList(),
                LegionApplications.Where(a => a.GuildId == g.Id).ToList())
            : null);

    public Task<IReadOnlyList<GuildView>> RecommendedGuildsAsync(Guid character, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GuildView>>(OtherGuilds.Where(g => g.JoinRule != 1).Take(limit).ToList());

    public Task<IReadOnlyList<(GuildView Guild, DateTimeOffset Requested)>> SentApplicationsAsync(Guid character, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(GuildView, DateTimeOffset)>>(LegionApplications.Where(a => a.CharacterId == character)
            .Select(a => (OtherGuilds.Concat(GuildRow is null ? [] : [GuildRow]).First(g => g.Id == a.GuildId), a.Requested)).ToList());

    public Task<string> ApplyGuildAsync(Guid character, long guild, string comment, int maxSent, int maxReceived, CancellationToken ct)
    {
        if (GuildRow?.Members.Any(m => m.CharacterId == character) == true) return Task.FromResult("member");
        if (OtherGuilds.Concat(GuildRow is null ? [] : [GuildRow]).FirstOrDefault(g => g.Id == guild) is not { } g) return Task.FromResult("none");
        if (g.JoinRule != 2) return Task.FromResult("rule");
        if (LegionApplications.Any(a => a.CharacterId == character && a.GuildId == guild)) return Task.FromResult("exists");
        if (LegionApplications.Count(a => a.CharacterId == character) >= maxSent || LegionApplications.Count(a => a.GuildId == guild) >= maxReceived) return Task.FromResult("limit");
        var state = States.First(s => s.Summary.CharacterId == character);
        LegionApplications.Add(new GuildApplication(character, guild, state.Summary.Name, 1, 1, comment, DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch));
        return Task.FromResult("ok");
    }

    public Task<string> CancelApplicationAsync(Guid character, long guild, CancellationToken ct) =>
        Task.FromResult(LegionApplications.RemoveAll(a => a.CharacterId == character && (a.GuildId == guild || guild == 0)) > 0 ? "ok" : "none");

    public Task<(string Result, Guid Applicant)> AnswerApplicationAsync(Guid officer, string applicant, bool accept, int maxMembers, CancellationToken ct)
    {
        if (GuildRow is not { } g || g.Members.FirstOrDefault(m => m.CharacterId == officer) is not { Grade: <= 1 }) return Task.FromResult(("authority", Guid.Empty));
        if (LegionApplications.FirstOrDefault(a => a.GuildId == g.Id && a.Name.Equals(applicant, StringComparison.OrdinalIgnoreCase)) is not { } a) return Task.FromResult(("none", Guid.Empty));
        LegionApplications.Remove(a);
        if (accept)
        {
            if (g.Members.Count >= maxMembers) return Task.FromResult(("full", a.CharacterId));
            GuildRow = g with { Members = [.. g.Members, new GuildMember(a.CharacterId, a.Name, a.Level, 2, "", a.CreationId, DateTimeOffset.UnixEpoch)] };
            LegionApplications.RemoveAll(x => x.CharacterId == a.CharacterId);
        }
        return Task.FromResult(("ok", a.CharacterId));
    }

    public Task<string> UpdateGuildAsync(Guid character, string field, int number, string text, CancellationToken ct)
    {
        if (GuildRow is not { } g || g.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 }) return Task.FromResult("authority");
        GuildRow = field switch
        {
            "join_rule" => g with { JoinRule = number, Password = number == 3 ? text : "" },
            "emblem" => g with { Emblem = number },
            "intro" => g with { Intro = text },
            "notice" => g with { Notice = text },
            _ => g,
        };
        return Task.FromResult("ok");
    }

    public Task<(string Result, Guid Target)> SetMemberGradeAsync(Guid character, string target, int grade, int maxOfficers, CancellationToken ct)
    {
        if (GuildRow is not { } g || g.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: <= 1 } me) return Task.FromResult(("authority", Guid.Empty));
        if (g.Members.FirstOrDefault(m => m.Name.Equals(target, StringComparison.OrdinalIgnoreCase) && m.Grade > me.Grade) is not { } who) return Task.FromResult(("none", Guid.Empty));
        if (grade <= me.Grade) return Task.FromResult(("authority", who.CharacterId));
        if (grade == 1 && g.Members.Count(m => m.Grade == 1 && m.CharacterId != who.CharacterId) >= maxOfficers) return Task.FromResult(("full", who.CharacterId));
        SetMembers(m => m.CharacterId == who.CharacterId ? m with { Grade = grade } : m);
        return Task.FromResult(("ok", who.CharacterId));
    }

    public Task<string> SetAuthorityAsync(Guid character, IReadOnlyDictionary<int, int> authority, CancellationToken ct)
    {
        if (GuildRow?.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult("authority");
        LegionAuthority.Clear();
        foreach (var (grade, bits) in authority) LegionAuthority[grade] = bits;
        return Task.FromResult("ok");
    }

    public Task<string> SetGuildCommentAsync(Guid character, string comment, CancellationToken ct)
    {
        if (GuildRow?.Members.Any(m => m.CharacterId == character) != true) return Task.FromResult("none");
        SetMembers(m => m.CharacterId == character ? m with { Comment = comment } : m);
        return Task.FromResult("ok");
    }

    public Task<(string Result, Guid Target)> DelegateMasterAsync(Guid character, string target, CancellationToken ct)
    {
        if (GuildRow is not { } g || g.Members.FirstOrDefault(m => m.CharacterId == character) is not { Grade: 0 }) return Task.FromResult(("authority", Guid.Empty));
        if (g.Members.FirstOrDefault(m => m.Name.Equals(target, StringComparison.OrdinalIgnoreCase) && m.CharacterId != character) is not { } who) return Task.FromResult(("none", Guid.Empty));
        SetMembers(m => m.CharacterId == character ? m with { Grade = 1 } : m.CharacterId == who.CharacterId ? m with { Grade = 0 } : m);
        return Task.FromResult(("ok", who.CharacterId));
    }

    (int Level, long Exp) AddLegionExp(long exp, long[] levelExp)
    {
        var total = GuildRow!.Exp + exp;
        var level = 1;
        for (var i = 0; i < levelExp.Length - 1; i++) if (total >= levelExp[i]) level = i + 2;
        GuildRow = GuildRow with { Exp = total, Level = level };
        return (level, total);
    }

    public Task<(string Result, int Level, long Exp)> GuildAttendAsync(Guid character, DateOnly day, long exp, long[] levelExp, CancellationToken ct)
    {
        if (GuildRow?.Members.Any(m => m.CharacterId == character) != true) return Task.FromResult(("none", 0, 0L));
        var mine = ExtraOf(character);
        if (mine.Attended >= day) return Task.FromResult(("done", 0, 0L));
        LegionMembers[character] = mine with { Attended = day };
        var (level, total) = AddLegionExp(exp, levelExp);
        return Task.FromResult(("ok", level, total));
    }

    public Task<string> GuildAttendanceRewardAsync(Guid character, DateOnly day, CancellationToken ct)
    {
        var mine = ExtraOf(character);
        if (mine.Rewarded >= day) return Task.FromResult("done");
        LegionMembers[character] = mine with { Rewarded = day };
        return Task.FromResult("ok");
    }

    public Task<(string Result, int Level, long Exp, int Remaining)> GuildDonateAsync(Guid character, DateOnly day, int maxPerDay,
        IReadOnlyList<(Guid Item, long Revision, int Count)> costs, long exp, long[] levelExp, CancellationToken ct)
    {
        if (GuildRow?.Members.Any(m => m.CharacterId == character) != true) return Task.FromResult(("none", 0, 0L, 0));
        var mine = ExtraOf(character);
        var done = mine.Donated == day ? mine.Donations : 0;
        if (done >= maxPerDay) return Task.FromResult(("limit", 0, 0L, 0));
        foreach (var (item, revision, count) in costs) if (!Consume(item, revision, count)) return Task.FromResult(("cost", 0, 0L, maxPerDay - done));
        LegionMembers[character] = mine with { Donated = day, Donations = done + 1 };
        var (level, total) = AddLegionExp(exp, levelExp);
        return Task.FromResult(("ok", level, total, maxPerDay - done - 1));
    }
}
