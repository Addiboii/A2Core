using Aion.Protocol.Native;

namespace Aion.World;

// Server authority (Stage C, research: a2core-log/authority-2026-11-09.md). The client cannot be changed, so every request is
// checked here and the server's own state decides. Graded response, never an automatic ban:
//   1. log      - Suspect(): an audit.events 'cheat_suspected' row for staff (rate-limited per kind), nothing else changes;
//   2. correct  - the request is refused or corrected (movement reject = rubber-band, skill refusal, item request refused);
//   3. kick     - only for floods far beyond any client (RequestLimiter.FloodKick dropped requests in 10 s, or MalformedKick
//                 requests that broke a handler within a minute): Kick_NT, the connection closes, the character is saved;
//   4. alert    - every kick is recorded with severity "alert" so staff see it at the top of the audit log.
// When a check is unsure (data missing, latency, a state the server does not track) it only logs.
public sealed partial class GameSession
{
    /// <summary>Per-connection request size and rate limits (RequestLimiter).</summary>
    public RequestLimiter Limits { get; } = new();

    /// <summary>
    /// Requests whose handler failed with an exception (opcode, exception, where). The request is answered with nothing and the
    /// session goes on; the fuzz check (tests: AuthorityChecks) keeps this empty for every request opcode. Capped at 100 entries.
    /// </summary>
    public List<string> RequestErrors { get; } = [];
    int malformedInWindow;
    long malformedWindowStart;
    public const int MalformedKick = 200;

    /// <summary>Cheat suspicions counted per kind (staff overview, checks).</summary>
    public Dictionary<string, int> Suspicions { get; } = [];
    readonly Dictionary<string, long> nextSuspectLogMs = [];
    public const long SuspectLogEveryMs = 30_000;

    /// <summary>
    /// Level 1 of the graded response: counted, and recorded for staff (audit 'cheat_suspected', at most once per kind every
    /// 30 s per player). Nothing is refused here; callers decide whether to also correct the request.
    /// </summary>
    internal void Suspect(string kind, object details, long now, string severity = "log")
    {
        Suspicions[kind] = Suspicions.GetValueOrDefault(kind) + 1;
        if (now < nextSuspectLogMs.GetValueOrDefault(kind) && severity != "alert") return;
        nextSuspectLogMs[kind] = now + SuspectLogEveryMs;
        var count = Suspicions[kind];
        var map = worldMap;
        RecordCheatInBackground(kind, new { severity, count, map, details });
    }

    /// <summary>
    /// Writes an audit 'cheat_suspected' row without waiting for it (best-effort). The write goes to the store BEHIND the
    /// connection's <see cref="TurnStore"/>: called from a background task, the wrapper would see this connection's turn held
    /// (by its own loop, on another thread) and release it for the database call and take it again afterwards. The loop then
    /// released the same locks a second time, and the late re-take was never given back: the whole world froze (deploy
    /// 2026-11-09 11:27, bots stuck, no one could enter; the authority checks made these records frequent).
    /// </summary>
    void RecordCheatInBackground(string kind, object details)
    {
        if (Store is not { } wrapped || CharacterId is not { } character) return;
        var store = TurnStore.Unwrap(wrapped);
        _ = Task.Run(async () =>
        {
            try { await store.RecordCheatAsync(character, kind, details); }
            catch { /* best-effort */ }
        });
    }

    /// <summary>Where the server holds the player: the last accepted position report, else the load point (world entry falls
    /// back to the default spawn when the character has none, as LoadComplete does).</summary>
    public Vector3d? ServerPosition => position ?? spawn?.Position ?? (InWorld ? GamePackets.SpawnPosition : null);

    /// <summary>NPC services (shops) answer within this reach of the NPC's body; beyond the log reach the use is recorded.</summary>
    public const double NpcLogReachCm = 1500, NpcRefuseReachCm = 4000;

    /// <summary>
    /// Whether an NPC is out of reach for a service (true: refuse). The player's server position (or the load point) against the
    /// NPC's: beyond NpcLogReachCm logged, beyond NpcRefuseReachCm refused when <paramref name="enforce"/> (a shop used from
    /// across the map); a use that is only logged (enforce false) never refuses.
    /// </summary>
    internal bool NpcOutOfReach(Target npc, string what, long now, bool enforce = true)
    {
        if (IsAdmin || ServerPosition is not { } at) return false;
        var distance = Math.Max(0, at.Distance2d(npc.Position) - npc.Radius);
        if (distance <= NpcLogReachCm) return false;
        var refused = enforce && distance > NpcRefuseReachCm;
        Suspect("npc_reach", new { what, npc = npc.NpcId, distance = (int)distance, refused }, now);
        return refused;
    }

    /// <summary>Whether equipping this item is refused: above the character's level (PermitLevelMin) or the other race's.</summary>
    internal bool EquipRefused(int templateId, long now)
    {
        if (IsAdmin) return false;
        if (data.Item(templateId) is not { } info)
        {
            Suspect("equip_unknown_item", new { item = templateId }, now);
            return false;
        }
        if (info.PermitLevel > level || info.Race != 0 && info.Race != profileRace)
        {
            Suspect("equip_refused", new { item = templateId, permit_level = info.PermitLevel, level, race = info.Race }, now);
            return true;
        }
        return false;
    }

    /// <summary>Level 3: Kick_NT and the connection closes (the listener ends on CloseReason); recorded as a staff alert.</summary>
    List<byte[]> AuthorityKick(string kind, object details, long now)
    {
        Suspect(kind, details, now, severity: "alert");
        CloseReason = "kicked: " + kind;
        return [PacketWriter.Opcode(KickNt).U8(KickByAdmin).String("Too many invalid requests").ToArray()];
    }

    /// <summary>The limits and the safety net around one request: a too large or too fast request is dropped, a handler failure
    /// is recorded instead of ending the connection, a flood of either kicks.</summary>
    async Task<List<byte[]>> GuardedAsync(byte[] plain, long now, Func<Task<List<byte[]>>> handle, CancellationToken ct)
    {
        if (CloseReason is not null) return [];
        var opcode = Opcode(plain);
        var verdict = Limits.Check(opcode, plain.Length, now);
        if (verdict != RequestLimiter.Verdict.Ok)
        {
            if (Limits.Flooding) return AuthorityKick("request_flood", new { opcode = $"0x{opcode:x4}", dropped = Limits.DroppedInWindow }, now);
            if (verdict == RequestLimiter.Verdict.TooLarge) Suspect("request_size", new { opcode = $"0x{opcode:x4}", length = plain.Length }, now);
            else Suspect("request_rate", new { opcode = $"0x{opcode:x4}" }, now);
            return [];
        }
        try
        {
            return await handle();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (RequestErrors.Count < 100)
            {
                var at = error.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Aion.World")) ?? "";
                RequestErrors.Add($"0x{opcode:x4} ({plain.Length} bytes) {error.GetType().Name}: {error.Message} {at}");
            }
            if (now - malformedWindowStart >= 60_000) { malformedWindowStart = now; malformedInWindow = 0; }
            if (++malformedInWindow >= MalformedKick) return AuthorityKick("malformed_flood", new { opcode = $"0x{opcode:x4}", errors = malformedInWindow }, now);
            Suspect("malformed_request", new { opcode = $"0x{opcode:x4}", length = plain.Length, error = error.GetType().Name }, now);
            return [];
        }
    }
}
