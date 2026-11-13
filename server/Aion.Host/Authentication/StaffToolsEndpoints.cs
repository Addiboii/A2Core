using System.Diagnostics;
using System.Text.RegularExpressions;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.Protocol.Native;
using Aion.World;

namespace Aion.Host.Authentication;

/// <summary>
/// Staff tools: moderation (bans, mutes, the chat log, tickets), maintenance mode, scheduled events and boosts,
/// population history, the economy and anti-cheat overview, bulk rewards, monster control on the live map and
/// database backups. Players open tickets through /api/tickets (and "//report" in chat). Role checks are the store's;
/// actions are audited.
/// </summary>
public static partial class StaffToolsEndpoints
{
    sealed record SanctionRequest(string? Player, string? Kind, string? Reason, double? Hours);
    sealed record TicketUpdate(string? Status, string? Note);
    sealed record NewTicket(string? Category, string? Content, string? Character);
    sealed record MaintenanceRequest(bool Enabled, string? Message, bool Kick = false);
    sealed record EventRequest(string? Kind, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, int? RepeatMinutes, string? Text, decimal? Multiplier);
    sealed record RewardItem(int TemplateId, long Quantity);
    sealed record RewardRequest(string? Target, string? Character, RewardItem[]? Items, long Kina, string? Message);
    sealed record GrantManyRequest(Guid CharacterId, RewardItem[]? Items);

    /// <summary>The most items one multi-grant may carry (the item panel's "select all matching" of a whole category).</summary>
    const int GrantManyLimit = 3000;
    sealed record AreaRequest(double X, double Y, double Radius);
    sealed record SpawnRequest(int NpcId, double X, double Y, double? Z, int Count = 1);

    const int KinaItemId = GameSession.KinaItemId;

    public static void MapStaffToolsEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/admin").RequireRateLimiting("admin");
        staff.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GameplayException error)
            { return error.Code == "forbidden" ? Results.StatusCode(403) : Results.BadRequest(new { error = error.Code }); }
        });

        // ---------- Moderation ----------
        staff.MapGet("/sanctions", async (bool? all, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a ? Results.Ok(await tools.SanctionsAsync(a, all == true, ct)) : Results.Unauthorized());
        staff.MapPost("/sanctions", async (SanctionRequest input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            TimeSpan? duration = input.Hours is { } h ? TimeSpan.FromHours(h) : null;
            var sanction = await tools.SanctionAsync(a, input.Player ?? "", input.Kind ?? "", input.Reason ?? "", duration, ct);
            await control.RefreshAsync(ct);
            var kicked = sanction.Kind == "ban" ? control.KickAccount(sanction.AccountId, sanction.Reason.Length > 0 ? sanction.Reason : "Banned") : 0;
            return Results.Ok(new { sanction, kicked });
        });
        staff.MapDelete("/sanctions/{id:long}", async (long id, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var revoked = await tools.RevokeSanctionAsync(a, id, ct);
            await control.RefreshAsync(ct);
            return Results.Ok(new { revoked });
        });
        staff.MapGet("/chat", async (string? q, string? sender, long? before, int? limit, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a ? Results.Ok(await tools.ChatLogAsync(a, q, sender, before, limit ?? 150, ct)) : Results.Unauthorized());
        staff.MapGet("/tickets", async (string? status, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a ? Results.Ok(await tools.TicketsAsync(a, status ?? "open", ct)) : Results.Unauthorized());
        staff.MapPost("/tickets/{id:long}", async (long id, TicketUpdate input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a ? Results.Ok(new { updated = await tools.UpdateTicketAsync(a, id, input.Status ?? "", input.Note ?? "", ct) }) : Results.Unauthorized());

        // ---------- Maintenance ----------
        staff.MapGet("/maintenance", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if ((await tools.RolesAsync(a, ct)).Count == 0) return Results.StatusCode(403);
            return Results.Ok(await tools.MaintenanceAsync(ct));
        });
        staff.MapPost("/maintenance", async (MaintenanceRequest input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var state = await tools.SetMaintenanceAsync(a, input.Enabled, input.Message ?? "", ct);
            await control.RefreshAsync(ct);
            var kicked = input.Enabled && input.Kick ? await control.KickPlayersAsync(state.Message, ct) : 0;
            return Results.Ok(new { state, kicked });
        });

        // ---------- Black Cloud market (free to play: everything for Kina) ----------
        staff.MapGet("/premium-shop", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if ((await tools.RolesAsync(a, ct)).Count == 0) return Results.StatusCode(403);
            var stored = await tools.PremiumShopSettingsAsync(ct);
            return Results.Ok(PremiumShopView(stored.Json is not null, stored.Since, stored.By));
        });
        staff.MapPost("/premium-shop", async (PremiumShopSettings input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var json = System.Text.Json.JsonSerializer.Serialize(input);
            try { PremiumShopSettings.Parse(json); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or FormatException) { return Results.BadRequest(new { error = error.Message }); }
            await tools.SetPremiumShopSettingsAsync(a, json, ct);
            await control.RefreshAsync(ct);
            var stored = await tools.PremiumShopSettingsAsync(ct);
            return Results.Ok(PremiumShopView(stored.Json is not null, stored.Since, stored.By));
        });
        staff.MapDelete("/premium-shop", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            await tools.SetPremiumShopSettingsAsync(a, null, ct);
            await control.RefreshAsync(ct);
            return Results.Ok(PremiumShopView(false, null, null));
        });

        // ---------- Black Cloud Merchants (the random shop) ----------
        staff.MapGet("/random-shop", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if ((await tools.RolesAsync(a, ct)).Count == 0) return Results.StatusCode(403);
            var stored = await tools.RandomShopSettingsAsync(ct);
            return Results.Ok(RandomShopView(stored.Json is not null, stored.Since, stored.By));
        });
        staff.MapPost("/random-shop", async (RandomShopSettings input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var json = System.Text.Json.JsonSerializer.Serialize(input);
            try { RandomShopSettings.Parse(json); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or FormatException or NullReferenceException) { return Results.BadRequest(new { error = error.Message }); }
            await tools.SetRandomShopSettingsAsync(a, json, ct);
            await control.RefreshAsync(ct);
            var stored = await tools.RandomShopSettingsAsync(ct);
            return Results.Ok(RandomShopView(stored.Json is not null, stored.Since, stored.By));
        });
        staff.MapDelete("/random-shop", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            await tools.SetRandomShopSettingsAsync(a, null, ct);
            await control.RefreshAsync(ct);
            return Results.Ok(RandomShopView(false, null, null));
        });

        // ---------- Events and boosts ----------
        staff.MapGet("/events", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a
                ? Results.Ok(new { events = await tools.EventsAsync(a, ct), boosts = new { exp = control.Boosts.Exp, drop = control.Boosts.Drop } })
                : Results.Unauthorized());
        staff.MapPost("/events", async (EventRequest input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var created = await tools.CreateEventAsync(a, new NewScheduledEvent(input.Kind ?? "", input.StartsAt, input.EndsAt, input.RepeatMinutes, input.Text ?? "", input.Multiplier), ct);
            await control.RefreshAsync(ct);
            return Results.Ok(created);
        });
        staff.MapDelete("/events/{id:long}", async (long id, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, ServerControl control, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var cancelled = await tools.CancelEventAsync(a, id, ct);
            await control.RefreshAsync(ct);
            return Results.Ok(new { cancelled });
        });

        // ---------- Population, economy and anti-cheat ----------
        staff.MapGet("/population", async (double? hours, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await StaffSessions.StaffAsync(ctx, auth, ct) is { } a ? Results.Ok(await tools.PopulationAsync(a, TimeSpan.FromHours(hours ?? 24), ct)) : Results.Unauthorized());
        staff.MapGet("/economy", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var report = await tools.EconomyAsync(a, ct);
            // Live anti-cheat signals: speed-check rejects of players in the world now (read under the world lock).
            var suspects = new List<object>();
            if (services.GetService<WorldPresence>() is { } presence && services.GetService<SharedWorld>() is { } world)
            {
                var views = new List<GameSession.LiveView>();
                await world.Lock.EnterExclusiveAsync(ct);
                try { foreach (var e in presence.All) views.Add(e.Session.Live()); }
                finally { world.Lock.ExitExclusive(); }
                suspects.AddRange(views.Where(v => v.SpeedRejects > 0).OrderByDescending(v => v.SpeedRejects).Take(20)
                    .Select(v => new { v.Name, v.CharacterId, v.Level, v.MapId, v.SpeedRejects }));
            }
            return Results.Ok(new { report, suspects });
        });

        // ---------- Bulk rewards (straight into the bag; online players see them at once) ----------
        staff.MapPost("/rewards", async (RewardRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IStaffTools tools,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var profile = await game.AdminProfileAsync(a, ct);
            if (profile.BuildId is not { } build) return Results.BadRequest(new { error = "no_catalogue" });
            var items = (input.Items ?? []).Where(i => i.Quantity > 0).Select(i => (i.TemplateId, i.Quantity)).ToList();
            if (input.Kina > 0) items.Add((KinaItemId, input.Kina));
            var message = (input.Message ?? "").Trim();
            if (items.Count is 0 or > 10 || items.Any(i => i.Quantity > 100_000_000) || message.Length > 200) return Results.BadRequest(new { error = "invalid_reward" });
            if (items.Any(i => CollectionItems.IsPlaceholder(i.TemplateId))) return Results.BadRequest(new { error = CollectionItems.PlaceholderError });
            var presence = services.GetService<WorldPresence>();
            List<Guid> characters;
            switch (input.Target)
            {
                case "character":
                    var name = (input.Character ?? "").Trim();
                    var found = (await game.AdminFindCharactersAsync(a, name, ct)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.CharacterId.ToString() == name.ToLowerInvariant());
                    if (found is null) return Results.BadRequest(new { error = "unknown_player" });
                    characters = [found.CharacterId];
                    break;
                case "online":
                    characters = presence?.All.Select(e => e.Session.CharacterId).OfType<Guid>().Distinct().ToList() ?? [];
                    break;
                case "all":
                    characters = [.. await tools.AllCharactersAsync(a, ct)];
                    break;
                default:
                    return Results.BadRequest(new { error = "invalid_reward" });
            }
            if (characters.Count * items.Count > 20_000) return Results.BadRequest(new { error = "reward_too_large" });
            var granted = 0;
            var failed = 0;
            foreach (var character in characters)
            {
                foreach (var (template, quantity) in items)
                    // Grants are at most 1,000,000 per request: larger amounts in several.
                    for (var left = quantity; left > 0; left -= 1_000_000)
                    {
                        try
                        {
                            await game.AdminAsync(a, new AdminCommand(Guid.NewGuid(), character, "grant", build, template, Math.Min(left, 1_000_000)), ct);
                            granted++;
                        }
                        // own_account: a character of the actor's own account, without grant.own_account (migration 076).
                        catch (GameplayException error) when (error.Code is "character_unavailable" or "own_account")
                        {
                            failed++;
                            break;
                        }
                    }
                if (message.Length > 0 && presence?.FindCharacter(character) is { } online)
                    _ = online.Session.InvokeAsync(_ => (true, new List<byte[]> { GameSession.Notice(message, 20, "7CE38B") }));
            }
            await game.AdminRecordAsync(a, "staff_reward", null, new { input.Target, input.Character, items = items.Select(i => new[] { (long)i.TemplateId, i.Quantity }), characters = characters.Count, message }, ct);
            return Results.Ok(new { characters = characters.Count, granted, failed });
        });

        // ---------- Multi-grant: many catalogue items to one character in one request ----------
        // The item panel's multi-select (user 2026-11-08: "grant all skins at once, there are hundreds"). One request instead of
        // one per item, so the staff limit (120 requests a minute) is not hit. Every item goes through the same grant command as a
        // single grant (role check, catalogue build, the player in the world receives it at once, its own audit row), plus one
        // summary row. A failed item does not stop the rest; an unavailable character stops it. Collection items are applied in the
        // game as they arrive (GameSession.StaffGrants.cs); placeholder ones (CollectionItems.IsPlaceholder) are refused per item.
        staff.MapPost("/grant-many", async (GrantManyRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            var profile = await game.AdminProfileAsync(a, ct);
            if (profile.BuildId is not { } build) return Results.BadRequest(new { error = "no_catalogue" });
            var items = (input.Items ?? []).Where(i => i.Quantity > 0).GroupBy(i => i.TemplateId).Select(g => (TemplateId: g.Key, Quantity: g.First().Quantity)).ToList();
            if (input.CharacterId == Guid.Empty || items.Count is 0 or > GrantManyLimit || items.Any(i => i.Quantity > 1_000_000))
                return Results.BadRequest(new { error = "invalid_grant" });
            var granted = 0;
            var failed = new List<object>();
            var stopped = false;
            foreach (var (template, quantity) in items)
            {
                // Placeholder collection items (UseEffect None: wings the client cannot learn or show) are refused, the rest go on.
                if (CollectionItems.IsPlaceholder(template)) { failed.Add(new { templateId = template, error = CollectionItems.PlaceholderError }); continue; }
                try
                {
                    await game.AdminAsync(a, new AdminCommand(Guid.NewGuid(), input.CharacterId, "grant", build, template, quantity), ct);
                    granted++;
                }
                catch (GameplayException error) when (error.Code is "forbidden") { throw; }
                catch (GameplayException error)
                {
                    failed.Add(new { templateId = template, error = error.Code });
                    if (error.Code is "character_unavailable" or "unknown_character") { stopped = true; break; }
                }
            }
            await game.AdminRecordAsync(a, "staff_grant_many", input.CharacterId,
                new { items = items.Count, granted, failed = failed.Count, quantity = items.Select(i => i.Quantity).Distinct().Take(3) }, ct);
            return Results.Ok(new { requested = items.Count, granted, failed, stopped });
        });

        // ---------- Monsters on the live map ----------
        staff.MapGet("/npcs", async (string? q, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if ((await tools.RolesAsync(a, ct)).Count == 0) return Results.StatusCode(403);
            var text = (q ?? "").Trim();
            if (text.Length is < 2 or > 64) return Results.Ok(Array.Empty<object>());
            return Results.Ok(GameData.Embedded.NpcsNamed(text).Select(n => new { n.NpcId, n.Name, n.Level, Hostile = n.Relationship is 6 or 11 or >= 16 and <= 21 }));
        });
        staff.MapGet("/maps/{id:int}/monsters", async (int id, double x, double y, double? r, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools,
            IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffSessions.StaffAsync(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            if ((await tools.RolesAsync(a, ct)).Count == 0) return Results.StatusCode(403);
            var result = await OnMapAsync(services, id, map => map.Monsters(x, y, Math.Clamp(r ?? 20000, 100, 60000), 1500), ct);
            return Results.Ok(result ?? []);
        });
        staff.MapPost("/maps/{id:int}/monsters/respawn", async (int id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is not { } a) return Results.Unauthorized();
            var count = await OnMapAsync(services, id, map => (int?)map.RespawnAll(Now()), ct);
            if (count is null) return Results.NotFound(new { error = "map_not_loaded" });
            await game.AdminRecordAsync(a, "staff_monsters_respawn", null, new { map = id, count }, ct);
            return Results.Ok(new { count });
        });
        staff.MapPost("/maps/{id:int}/monsters/clear", async (int id, AreaRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is not { } a) return Results.Unauthorized();
            if (!double.IsFinite(input.X) || !double.IsFinite(input.Y) || input.Radius is < 100 or > 20000) return Results.BadRequest(new { error = "invalid_area" });
            var count = await OnMapAsync(services, id, map => (int?)map.ClearArea(input.X, input.Y, input.Radius, Now(), GameSession.RespawnMs), ct);
            if (count is null) return Results.NotFound(new { error = "map_not_loaded" });
            await game.AdminRecordAsync(a, "staff_monsters_clear", null, new { map = id, input.X, input.Y, input.Radius, count }, ct);
            return Results.Ok(new { count });
        });
        staff.MapPost("/maps/{id:int}/monsters/spawn", async (int id, SpawnRequest input, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is not { } a) return Results.Unauthorized();
            if (input.Count is < 1 or > 20 || !double.IsFinite(input.X) || !double.IsFinite(input.Y)) return Results.BadRequest(new { error = "invalid_spawn" });
            if (GameData.Embedded.NpcPlaced(input.NpcId, id) is not { } template) return Results.BadRequest(new { error = "unknown_npc" });
            var keys = await OnMapAsync(services, id, map =>
            {
                // Height: the given one, else the nearest NPC's (they stand on the ground).
                var z = input.Z ?? map.Targets.Values.Where(t => t.Key != Combat.TrainingDummyKey)
                    .MinBy(t => (t.Position.X - input.X) * (t.Position.X - input.X) + (t.Position.Y - input.Y) * (t.Position.Y - input.Y))?.Position.Z ?? 0;
                return map.Spawn(template, new Vector3d(input.X, input.Y, z), input.Count);
            }, ct);
            if (keys is null) return Results.NotFound(new { error = "map_not_loaded" });
            await game.AdminRecordAsync(a, "staff_monsters_spawn", null, new { map = id, input.NpcId, template.Name, input.X, input.Y, input.Count }, ct);
            return Results.Ok(new { keys, template.Name });
        });
        staff.MapDelete("/maps/{id:int}/monsters/staff", async (int id, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IServiceProvider services, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct) is not { } a) return Results.Unauthorized();
            var count = await OnMapAsync(services, id, map => (int?)map.RemoveStaffSpawns(), ct);
            if (count is null) return Results.NotFound(new { error = "map_not_loaded" });
            await game.AdminRecordAsync(a, "staff_monsters_removed", null, new { map = id, count }, ct);
            return Results.Ok(new { count });
        });

        // ---------- Database backups (admins) ----------
        staff.MapGet("/backups", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, admin: true) is null) return Results.Unauthorized();
            var dir = config["Aion:Backups:Directory"];
            var configured = dir is { Length: > 0 } && config["Aion:Backups:Tool"] is { Length: > 0 };
            // The list also without the Linux backup tool: on Windows the nightly task (deploy/windows/backup.ps1) makes the dumps.
            var files = dir is { Length: > 0 } && Directory.Exists(dir)
                ? new DirectoryInfo(dir!).GetFiles("*.dump").OrderByDescending(f => f.LastWriteTimeUtc).Take(50)
                    .Select(f => new { f.Name, f.Length, At = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) }).ToList<object>()
                : [];
            return Results.Ok(new { configured, files });
        });
        staff.MapPost("/backups", async (HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, admin: true) is not { } a) return Results.Unauthorized();
            if (config["Aion:Backups:Directory"] is not { Length: > 0 } dir || config["Aion:Backups:Tool"] is not { Length: > 0 } tool) return Results.NotFound(new { error = "backups_not_configured" });
            Directory.CreateDirectory(dir);
            var name = $"aion-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.dump";
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3") { RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(tool);
            start.ArgumentList.Add("backup");
            start.ArgumentList.Add(Path.Combine(dir, name));
            if (config["Aion:Backups:PgHome"] is { Length: > 0 } home) start.Environment["AION_PG_HOME"] = home;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("backup tool did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) return Results.Json(new { error = "backup_failed", detail = (await errors).Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() }, statusCode: 500);
            await game.AdminRecordAsync(a, "staff_backup", null, new { name }, ct);
            return Results.Ok(new { name, length = new FileInfo(Path.Combine(dir, name)).Length });
        });
        staff.MapGet("/backups/{name}", async (string name, HttpContext ctx, IAccountAuthentication auth, IGameplayStore game, IConfiguration config, CancellationToken ct) =>
        {
            if (await StaffEndpoints.StaffAsync(ctx, auth, game, ct, admin: true) is not { } a) return Results.Unauthorized();
            if (config["Aion:Backups:Directory"] is not { Length: > 0 } dir || !BackupName().IsMatch(name)) return Results.NotFound();
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return Results.NotFound();
            await game.AdminRecordAsync(a, "staff_backup_downloaded", null, new { name }, ct);
            return Results.File(path, "application/octet-stream", name);
        });
    }

    /// <summary>Players' tickets from the account page (signed in): open one, list their own.</summary>
    public static void MapTicketEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/tickets").RequireRateLimiting("account-entry");
        api.MapPost("", async (NewTicket input, HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
        {
            if (await Signed(ctx, auth, ct) is not { } a) return Results.Unauthorized();
            try { return Results.Ok(new { id = await tools.CreateTicketAsync(a.AccountId, input.Character, input.Category ?? "other", input.Content ?? "", ct) }); }
            catch (GameplayException error) { return Results.BadRequest(new { error = error.Code }); }
        });
        api.MapGet("", async (HttpContext ctx, IAccountAuthentication auth, IStaffTools tools, CancellationToken ct) =>
            await Signed(ctx, auth, ct) is { } a ? Results.Ok((await tools.MyTicketsAsync(a.AccountId, ct)).Select(t => new { t.Id, t.CreatedAt, t.Category, t.Content, t.Status, t.StaffNote, t.HandledAt })) : Results.Unauthorized());
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.dump$")]
    private static partial Regex BackupName();

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Work on a loaded shared map with the world lock held exclusively; null when nobody loaded the map.</summary>
    static async Task<T?> OnMapAsync<T>(IServiceProvider services, int mapId, Func<MapInstance, T> work, CancellationToken ct)
    {
        if (services.GetService<SharedWorld>() is not { } world) return default;
        await world.Lock.EnterExclusiveAsync(ct);
        try { return world.LoadedMap(mapId) is { } map ? work(map) : default; }
        finally { world.Lock.ExitExclusive(); }
    }

    static readonly string[] PremiumCurrencies = ["Kina", "Quna", "Money (KRW)", "Mileage"];
    static readonly string[] LimitTargets = ["", "per character", "per account"];
    static readonly string[] LimitPeriods = ["", " a day", " a week", " a month"];

    /// <summary>The market as the staff panel shows it: the rules in force and every goods with its official and Kina price.</summary>
    static object PremiumShopView(bool custom, DateTimeOffset? since, string? by)
    {
        var rules = PremiumShop.Current;
        var defaults = PremiumShop.Defaults;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tables = PremiumShopTables.Embedded;
        return new
        {
            enabled = rules.Enabled, kinaPerQuna = rules.KinaPerQuna, cashPerQuna = rules.CashPerQuna, mileagePerQuna = rules.MileagePerQuna, mirrorQuna = rules.MirrorQuna,
            sellOffSale = rules.SellOffSale, mirrorMileage = rules.MirrorMileage, qunaPerMileage = rules.QunaPerMileage, randomKinaPerMileage = rules.RandomKinaPerMileage,
            defaults = new { enabled = defaults.Enabled, kinaPerQuna = defaults.KinaPerQuna, qunaPerMileage = defaults.QunaPerMileage, randomKinaPerMileage = defaults.RandomKinaPerMileage },
            custom, since, by,
            // The shop's tabs as the client groups them (BMShopCategory): name and goods per race. Storage (the bought goods'
            // warehouse) and the platform-only goods are not in the client table, so they count 0 here.
            tabs = tables.TabGroups.Select(group => new
            {
                group, name = tables.TabName(group),
                elyos = tables.All.Count(g => tables.TabOf(g, 1) == tables.TabName(group)),
                asmodians = tables.All.Count(g => tables.TabOf(g, 2) == tables.TabName(group)),
            }),
            goods = tables.All.Select(g => new
            {
                id = g.Id, name = g.Name, category = PremiumShopRules.Category(g.CategoryLight != "None" ? g.CategoryLight : g.CategoryDark),
                tab = tables.TabOf(g, g.CategoryLight != "None" ? 1 : 2),
                races = g.Races, item = g.ItemId, count = g.Count,
                currency = PremiumCurrencies[Math.Clamp(g.Currency, 0, 3)], price = g.Price, quna = rules.QunaValue(g), kina = rules.KinaPrice(g),
                ownKina = rules.KinaPrices.TryGetValue(g.Id, out var own) ? own : (long?)null,
                enabled = rules.Sells(g), visible = PremiumShopTables.OnSaleFor(g, 1, now) || PremiumShopTables.OnSaleFor(g, 2, now), onSale = g.OnSale,
                mileage = rules.MileageEarned(g, 1),
                limit = g.LimitTarget > 0 && g.Limit > 0 ? $"{g.Limit} {LimitTargets[Math.Clamp(g.LimitTarget, 0, 2)]}{LimitPeriods[Math.Clamp(g.LimitPeriod, 0, 3)]}" : "",
            }),
        };
    }

    /// <summary>The Black Cloud Merchants as the staff panel shows it: the rules in force, and per shop its pool by kind and grade
    /// with each group's chance of filling one slot and its price range (discount aside).</summary>
    static object RandomShopView(bool custom, DateTimeOffset? since, string? by)
    {
        var rules = RandomShop.Current;
        var tables = RandomShopTables.Embedded;
        return new
        {
            enabled = rules.Enabled, slots = rules.Slots, refreshMinutes = rules.RefreshMinutes,
            discounts = rules.Discounts, kinds = RandomShopTables.KindNames.Select((name, i) => new { name, rules.Kinds[i].Share, rules.Kinds[i].PriceFactor, rules.Kinds[i].Grades }),
            grades = RandomShopTables.GradeNames, custom, since, by,
            shops = tables.Shops.Select(shop =>
            {
                var pool = tables.Pool(shop.Group);
                var kinds = pool.Select(g => g.Kind).Distinct().OrderBy(k => k).ToList();
                var shareSum = kinds.Sum(k => (double)rules.KindOf(k).Share);
                return new
                {
                    id = shop.Id, category = shop.Category, wire = shop.Wire, race = shop.Race, levels = $"{shop.LevelMin}-{shop.LevelMax}", goods = pool.Count,
                    refreshCosts = tables.RefreshCosts(shop.RefreshGroup),
                    groups = pool.GroupBy(g => (g.Kind, g.Grade)).OrderBy(x => x.Key).Select(x =>
                    {
                        var ofKind = pool.Where(g => g.Kind == x.Key.Kind).ToList();
                        var grades = rules.KindOf(x.Key.Kind).Grades;
                        double Weight(int grade) => grades[Math.Clamp(grade, 0, grades.Length - 1)];
                        var kindWeight = ofKind.Sum(g => Weight(g.Grade));
                        var kindChance = kinds.Count == 1 ? 1 : shareSum > 0 ? rules.KindOf(x.Key.Kind).Share / shareSum : 1.0 / kinds.Count;
                        var chance = kindWeight > 0 ? kindChance * x.Count() * Weight(x.Key.Grade) / kindWeight : kindChance * x.Count() / ofKind.Count;
                        return new
                        {
                            kind = RandomShopTables.KindNames[Math.Clamp(x.Key.Kind, 0, 3)], grade = RandomShopTables.GradeNames[Math.Clamp(x.Key.Grade, 0, 4)], count = x.Count(),
                            percent = Math.Round(chance * 100, 1), minPrice = x.Min(g => rules.NormalPrice(g)), maxPrice = x.Max(g => rules.NormalPrice(g)),
                        };
                    }),
                };
            }),
        };
    }

    static async Task<VerifiedAccount?> Signed(HttpContext ctx, IAccountAuthentication auth, CancellationToken ct)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        return await auth.AuthenticateAsync(h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..] : "", ct);
    }
}
