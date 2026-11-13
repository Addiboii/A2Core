using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// What a character keeps over a logout (community recordings 2026-11-04 to 06, reports/movement.md "What the official re-entry
// restores"). OBSERVED:
// - timed buffs come back with the time they had left, and that time does not run while logged out (234912 #56805: 22101051
//   of 300000 ms with 113585 ms left after at least 14 minutes offline; 084658 #64328: three buffs with 78-84 s of 300 s);
// - the flight gauge comes back as it was (FP 549067 of 650567 at #56821);
// - abnormal 10002 for 5000 ms right after every world-entry 0x3633 (084658 #64332, 090856 #1392, 234912 #56810); it comes
//   with every other map load too (all 61 loads in our recordings; GameSession.cs).
// Stored in the character's settings ("logout_state", as seal_stone and presets), written by the forced checkpoints (logout,
// lobby, disconnect). Not restored (yet): the in-air state (the character comes back in flight with abnormal 100000002 in
// those recordings; the abnormal is ours since GameSession.Flight.cs, but the air state is not saved); HP/MP (not settled: full in every recording). CANDIDATE: what 10002 does
// (its SkillAbnormal row is not in our tables: only the icon shows).
public sealed partial class GameSession
{
    const int EntryProtectionAbnormal = 10002, EntryProtectionMs = 5000;

    sealed record LogoutState(int[][] Buffs, long Fp);

    LogoutState? pendingLogoutState;

    void LoadLogoutState(IReadOnlyDictionary<string, JsonElement> settings)
    {
        try { pendingLogoutState = settings.TryGetValue("logout_state", out var v) ? v.Deserialize<LogoutState>() : null; }
        catch (JsonException) { pendingLogoutState = null; }
    }

    /// <summary>Stores the timed buffs (abnormal, skill, duration, time left) and the flight gauge.</summary>
    async Task SaveLogoutStateAsync(long now, CancellationToken ct)
    {
        if (Store is null || selected is null || resources is null) return;
        var timed = buffs.Where(b => b.DurationMs > 0 && !b.Scripted && b.ExpireAt > now && b.AbnormalId != EntryProtectionAbnormal)
            .Select(b => new[] { b.AbnormalId, b.SkillId, (int)Math.Min(b.DurationMs, int.MaxValue), (int)Math.Min(b.ExpireAt - now, int.MaxValue) }).ToArray();
        await SaveSettingAsync("logout_state", new LogoutState(timed, resources.Fp), ct);
    }

    /// <summary>At the first load after a selection: the stored buffs with their time left, the flight gauge as it was.</summary>
    void RestoreLogoutState(long now)
    {
        if (pendingLogoutState is not { } state || resources is null) return;
        pendingLogoutState = null;
        var ignored = new List<byte[]>();   // BuffsAfterLoad sends them with the load
        foreach (var b in state.Buffs.Where(b => b.Length == 4 && b[0] > 0 && b[2] > 0 && b[3] > 0))
            AddBuff(b[0], b[2], b[1], now, ignored, remainingMs: Math.Min(b[3], b[2]));
        if (resources.FpMax > 0) resources.Fp = (int)Math.Clamp(state.Fp, 0, resources.FpMax);
        RefreshStats();
    }

    /// <summary>The entry protection abnormal after the world-entry appearance (OBSERVED: 10002 for 5000 ms).</summary>
    byte[]? EntryProtection(long now)
    {
        var added = new List<byte[]>();
        return AddBuff(EntryProtectionAbnormal, EntryProtectionMs, 0, now, added) is null ? null : added.FirstOrDefault(b => b[0] == 0x2a && b[1] == 0x38);
    }
}
