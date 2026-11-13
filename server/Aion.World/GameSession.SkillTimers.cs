using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Per-skill cast timing from the official recordings of 2026-11-06 (tools/skill_timing.py → tools/data/skill-timings.json):
/// the median time from the caster's SkillStart_NT 0x3802 to its natural SkillEnd 0x3806, and the usual last byte of that
/// 0x3806 (12 on chain/combo skills, 0 on the rest). Loaded once; skills not recorded use the overall median.
/// </summary>
public sealed class SkillTimings
{
    public sealed record Timing([property: JsonPropertyName("end_ms")] int EndMs, [property: JsonPropertyName("end_tail")] byte EndTail,
        [property: JsonPropertyName("samples")] int Samples);

    sealed record TimingFile([property: JsonPropertyName("default_end_ms")] int DefaultEndMs,
        [property: JsonPropertyName("skills")] Dictionary<int, Timing> Skills);

    readonly TimingFile file;
    SkillTimings(TimingFile file) => this.file = file;

    static readonly Lazy<SkillTimings> embedded = new(() =>
    {
        using var stream = typeof(SkillTimings).Assembly.GetManifestResourceStream("skill-timings.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-timings.json");
        return new SkillTimings(JsonSerializer.Deserialize<TimingFile>(stream) ?? throw new InvalidOperationException("Empty skill-timings.json"));
    });

    public static SkillTimings Embedded => embedded.Value;

    /// <summary>When the cast ends (ms after its start) and the end message's last byte.</summary>
    public (int EndMs, byte Tail) Of(int skillId) =>
        file.Skills.TryGetValue(skillId, out var t) ? (t.EndMs, t.EndTail) : (file.DefaultEndMs, (byte)0);

    public bool Recorded(int skillId) => file.Skills.ContainsKey(skillId);
}

// Cast timers. Every official cast ends with 0x3806 SkillEnd (996 of 996 recorded own casts): at the skill's own time when
// nothing follows, or right before the next cast's 0x3802 when the player casts again first (the official order). Before
// 2026-11-06 our normal casts never got an end, so the client fell back on its own timeout between skills (the lag).
// Timers sit in a per-session heap ordered by due time; the connection loop sleeps until the earliest one (NextTimerAt)
// instead of polling, and work that waits on storage after a kill runs right after the cast's replies went out.
public sealed partial class GameSession
{
    readonly PriorityQueue<(long Due, long Token), long> timers = new();
    /// <summary>The player's cast still running (its end not sent): skill, action uid, due, timer token.</summary>
    (int Skill, int Action, long Due, long Token)? runningCast;
    long timerToken;
    /// <summary>Kill follow-ups (quest credit, experience) that touch storage: done after the cast's replies are sent.</summary>
    bool killWorkDue;

    static SkillTimings CastTimings => SkillTimings.Embedded;

    /// <summary>The earliest moment this session has timed work due (the connection loop sleeps until then), or null.</summary>
    public long? NextTimerAt => killWorkDue ? 0
        : (timers.TryPeek(out _, out var due) ? due : (long?)null) is var cast && timedActions.Count > 0 ? Math.Min(cast ?? long.MaxValue, timedActions.Min(a => a.Due))
        : cast;

    /// <summary>Timed work of a skill or object (a projectile landing, a box usable again): run on the tick at or after its time.</summary>
    readonly List<(long Due, Func<long, CancellationToken, Task<List<byte[]>>> Run)> timedActions = [];

    void Schedule(long due, Func<long, CancellationToken, Task<List<byte[]>>> run) => timedActions.Add((due, run));
    void Schedule(long due, Func<long, List<byte[]>> run) => timedActions.Add((due, (now, _) => Task.FromResult(run(now))));

    async Task<List<byte[]>> RunTimedActionsAsync(long now, CancellationToken ct)
    {
        if (timedActions.Count == 0) return [];
        var due = timedActions.Where(a => a.Due <= now).OrderBy(a => a.Due).ToList();
        if (due.Count == 0) return [];
        timedActions.RemoveAll(due.Contains);
        var replies = new List<byte[]>();
        foreach (var action in due) replies.AddRange(await action.Run(now, ct));
        return replies;
    }

    /// <summary>The cast's end is scheduled; a cast still running ends now, before the new start (returned for the reply).</summary>
    List<byte[]> StartCastTimer(int skill, int action, long now)
    {
        var cut = EndRunningCast();
        var (endMs, _) = CastTimings.Of(skill);
        endMs = (int)Math.Round(endMs * 10_000.0 / AttackSpeed);   // a faster attack speed (Courage Scroll, titles...) ends the cast sooner
        var token = ++timerToken;
        runningCast = (skill, action, now + endMs, token);
        timers.Enqueue((now + endMs, token), now + endMs);
        return cut;
    }

    /// <summary>0x3806 for the running cast, if any (also used when a new cast cuts it short).</summary>
    List<byte[]> EndRunningCast()
    {
        if (runningCast is not { } cast) return [];
        runningCast = null;
        return [GamePackets.SkillEnd(playerKey, cast.Skill, cast.Action, CastTimings.Of(cast.Skill).Tail)];
    }

    /// <summary>The timers that are due: the cast ends (to the player and those around).</summary>
    List<byte[]> RunTimers(long now)
    {
        var replies = new List<byte[]>();
        while (timers.TryPeek(out var timer, out var due) && due <= now)
        {
            timers.Dequeue();
            if (runningCast is { } cast && cast.Token == timer.Token)
            {
                var ended = EndRunningCast();
                worldPlayer?.Map?.Broadcast(worldPlayer, ended);
                replies.AddRange(ended);
            }
        }
        return replies;
    }

    /// <summary>Quest credit and experience of the kills a cast made (storage calls), once its replies are out.</summary>
    async Task<List<byte[]>> KillWorkAsync(CancellationToken ct)
    {
        if (!killWorkDue) return [];
        killWorkDue = false;
        return [.. await KillKinaAsync(ct), .. await QuestKillsAsync(ct), .. await KillExperienceAsync(ct), .. await ShardWorkAsync(ct)];
    }

    void ResetCastTimers()
    {
        timers.Clear();
        timedActions.Clear();
        runningCast = null;
    }
}
