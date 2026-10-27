using System.Collections.Concurrent;
using Aion.Application.Chat;
using Aion.Application.Gameplay;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// The host side of the staff tools: maintenance mode and sanctions cached for login and chat checks (refreshed every
/// 30 s and on every change), the chat log written in batches, scheduled events (timed announcements; experience and
/// drop boosts applied to the shared world), population samples every 5 minutes and the daily cleanup.
/// </summary>
public sealed class ServerControl(IStaffTools tools, IServiceProvider services, ILogger<ServerControl> logger) : BackgroundService, IChatModeration
{
    static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30), SampleEvery = TimeSpan.FromMinutes(5), Tick = TimeSpan.FromSeconds(5);
    volatile MaintenanceState maintenance = new(false, "", null, null);
    volatile IReadOnlyList<Sanction> sanctions = [];
    volatile IReadOnlyList<ScheduledEvent> events = [];
    readonly ConcurrentQueue<ChatLogEntry> chat = new();
    DateTimeOffset refreshedAt = DateTimeOffset.MinValue, sampledAt = DateTimeOffset.MinValue, cleanedAt = DateTimeOffset.MinValue;

    public MaintenanceState Maintenance => maintenance;
    public (double Exp, double Drop) Boosts { get; private set; } = (1, 1);

    /// <summary>Why this account may not enter the game now ("banned …", "maintenance …"), or null.</summary>
    public async Task<string?> LoginBlockedAsync(Guid account, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (sanctions.FirstOrDefault(s => s.AccountId == account && s.Kind == "ban" && s.Active(now)) is { } ban)
            return "banned" + (ban.ExpiresAt is { } until ? $" until {until:u}" : "") + (ban.Reason.Length > 0 ? $": {ban.Reason}" : "");
        if (maintenance.Enabled && !await tools.IsStaffAsync(account, ct)) return "maintenance";
        return null;
    }

    public string? MuteMessage(Guid account)
    {
        var now = DateTimeOffset.UtcNow;
        if (sanctions.FirstOrDefault(s => s.AccountId == account && s.Kind == "mute" && s.Active(now)) is not { } mute) return null;
        var until = mute.ExpiresAt is { } end ? $" for {Remaining(end - now)}" : "";
        return $"You are muted{until}." + (mute.Reason.Length > 0 ? $" Reason: {mute.Reason}" : "");
    }

    static string Remaining(TimeSpan left) => left.TotalHours >= 24 ? $"{left.TotalDays:0} days" : left.TotalMinutes >= 60 ? $"{left.TotalHours:0} hours" : $"{Math.Max(1, left.TotalMinutes):0} minutes";

    public void Logged(ChatLogEntry entry) => chat.Enqueue(entry);

    /// <summary>Reloads maintenance, sanctions and events now (after a staff change).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        maintenance = await tools.MaintenanceAsync(ct);
        sanctions = await tools.ActiveSanctionsAsync(ct);
        events = await tools.LiveEventsAsync(ct);
        refreshedAt = DateTimeOffset.UtcNow;
        ApplyBoosts(refreshedAt);
    }

    /// <summary>Disconnects every session of the account (a ban), with a notice; the number kicked.</summary>
    public int KickAccount(Guid account, string reason)
    {
        var count = 0;
        if (services.GetService<WorldPresence>() is not { } presence) return 0;
        foreach (var entry in presence.All.Where(e => e.AccountId == account))
        {
            _ = entry.Session.InvokeAsync(_ => (true, entry.Session.StaffKick(reason)));
            count++;
        }
        return count;
    }

    /// <summary>Disconnects everyone who is not staff (maintenance), with the maintenance message; the number kicked.</summary>
    public async Task<int> KickPlayersAsync(string message, CancellationToken ct)
    {
        var count = 0;
        if (services.GetService<WorldPresence>() is not { } presence) return 0;
        foreach (var entry in presence.All)
        {
            if (entry.AccountId is { } account && await tools.IsStaffAsync(account, ct)) continue;
            _ = entry.Session.InvokeAsync(_ => (true, entry.Session.StaffKick(message.Length > 0 ? message : "Server maintenance")));
            count++;
        }
        return count;
    }

    void ApplyBoosts(DateTimeOffset now)
    {
        double Rate(string kind) => events.Where(e => e.Kind == kind && e.StartsAt <= now && (e.EndsAt is null || e.EndsAt > now) && e.Multiplier is > 0)
            .Select(e => (double)e.Multiplier!.Value).DefaultIfEmpty(1).Max();
        var boosts = (Rate("exp"), Rate("drop"));
        if (boosts != Boosts) logger.LogInformation("Event boosts now: experience x{Exp}, drops x{Drop}", boosts.Item1, boosts.Item2);
        Boosts = boosts;
        if (services.GetService<SharedWorld>() is { } world)
        {
            world.ExpRate = boosts.Item1;
            world.DropRate = boosts.Item2;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or GameplayException or TimeoutException)
            {
                logger.LogWarning("Server control step failed: {Error}", error.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
        await FlushChatAsync(CancellationToken.None);
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - refreshedAt >= Refresh) await RefreshAsync(ct);
        ApplyBoosts(now);
        await FlushChatAsync(ct);
        await RunAnnouncementsAsync(now, ct);
        if (now - sampledAt >= SampleEvery)
        {
            sampledAt = now;
            var presence = services.GetService<WorldPresence>();
            await tools.SamplePopulationAsync(presence?.All.Count ?? 0, presence?.Connections ?? 0, ct);
        }
        if (now - cleanedAt >= TimeSpan.FromHours(24))
        {
            cleanedAt = now;
            await tools.CleanupAsync(ct);
        }
    }

    async Task FlushChatAsync(CancellationToken ct)
    {
        var batch = new List<ChatLogEntry>();
        while (batch.Count < 500 && chat.TryDequeue(out var line)) batch.Add(line);
        if (batch.Count > 0) await tools.LogChatAsync(batch, ct);
    }

    /// <summary>Announcement events due now: once at the start, then every repeat interval until the end.</summary>
    async Task RunAnnouncementsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var ran = false;
        foreach (var e in events.Where(e => e.Kind == "announce"))
        {
            if (e.StartsAt > now || e.EndsAt is { } end && end <= now) continue;
            var due = e.LastRunAt is not { } last || e.RepeatMinutes is { } every && now >= last.AddMinutes(every);
            if (!due) continue;
            // A one-off announcement more than 10 minutes late (host was down) is skipped.
            if (e.LastRunAt is null && e.RepeatMinutes is null && now - e.StartsAt > TimeSpan.FromMinutes(10))
            {
                await tools.MarkEventRunAsync(e.Id, now, ct);
                ran = true;
                continue;
            }
            await StaffEndpoints.AnnounceAllAsync(services, e.Text, "FFD54A");
            await tools.MarkEventRunAsync(e.Id, now, ct);
            logger.LogInformation("Scheduled announcement {Id} sent", e.Id);
            ran = true;
        }
        if (ran) events = await tools.LiveEventsAsync(ct);
    }
}
