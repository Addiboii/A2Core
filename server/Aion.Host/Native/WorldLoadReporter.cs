using System.Diagnostics;
using Aion.World;

namespace Aion.Host.Native;

/// <summary>
/// Logs the shared world's load once a minute while anyone plays: how busy the exclusive world turn and the map
/// turns were, how long turns waited, storage calls run outside a turn, and the process CPU.
/// </summary>
public sealed class WorldLoadReporter(SharedWorld world, WorldPresence presence, ILogger<WorldLoadReporter> logger) : BackgroundService
{
    static readonly TimeSpan Period = TimeSpan.FromMinutes(1);
    readonly object healthSync = new();
    (WorldLockStats.Snapshot Locks, double Cpu)? healthBefore;
    object? lastHealth;

    /// <summary>
    /// Server health for the staff dashboard: CPU and lock load over the window since the previous call (at least 2 s;
    /// a sooner call gets the same answer), memory, players, uptime and a pending restart.
    /// </summary>
    public object Health(WorldPresence players, DateTimeOffset? restartAt = null)
    {
        lock (healthSync)
        {
            var locks = world.Lock.Stats.Read();
            var cpu = CpuSeconds();
            if (healthBefore is { } before && locks.Seconds - before.Locks.Seconds < 2 && lastHealth is not null) return lastHealth;
            var w = healthBefore is { } b ? locks.Since(b.Locks) : locks;
            var cpuShare = healthBefore is { } p && w.Seconds > 0 ? (cpu - p.Cpu) / w.Seconds : 0;
            healthBefore = (locks, cpu);
            using var process = Process.GetCurrentProcess();
            return lastHealth = new
            {
                Players = players.All.Count, Connections = players.Connections,
                StartedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()),
                Cores = Environment.ProcessorCount, Cpu = Math.Round(cpuShare, 3),
                MemoryMb = process.WorkingSet64 / 1048576, HeapMb = GC.GetTotalMemory(false) / 1048576, Threads = process.Threads.Count,
                WorldBusy = Math.Round(w.ExclusiveBusy, 4), MapBusy = Math.Round(w.SharedBusy, 4),
                WorldWaitMs = Math.Round(w.AverageExclusiveWaitMs, 2), MapWaitMs = Math.Round(w.AverageSharedWaitMs, 2),
                WindowSeconds = Math.Round(w.Seconds, 1), RestartAt = restartAt,
            };
        }
    }

    /// <summary>The process CPU time in seconds (all cores), for windows like the lock totals.</summary>
    public static double CpuSeconds() => Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var before = world.Lock.Stats.Read();
        var cpuBefore = CpuSeconds();
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = world.Lock.Stats.Read();
            var cpu = CpuSeconds();
            var w = now.Since(before);
            if (w.ExclusiveTurns + w.SharedTurns > 0)
                logger.LogInformation(
                    "World load: {Players} players, CPU {Cpu:P0} of one core, world turn busy {Exclusive:P1} (avg wait {ExclusiveWait:F2} ms), " +
                    "map turns busy {Shared:P1} (avg wait {SharedWait:F2} ms), {Outside} storage calls outside the turn",
                    presence.All.Count, (cpu - cpuBefore) / w.Seconds, w.ExclusiveBusy, w.AverageExclusiveWaitMs, w.SharedBusy, w.AverageSharedWaitMs, w.StoreCallsOutside);
            before = now;
            cpuBefore = cpu;
        }
    }
}
