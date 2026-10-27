using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Aion.World;

/// <summary>
/// The shared world's lock: exclusive (world work: entry, social, trades, casts, anything that may touch another map)
/// or shared (map work, together with the map's own <see cref="MapInstance.Lock"/>), so different maps run in
/// parallel while world work runs alone. Writer-preferring (the no-starve readers-writers turnstile): a waiting
/// exclusive turn stops new shared turns. Lock order: the world lock, then one map lock; never the other way round.
/// </summary>
public sealed class WorldLock
{
    readonly SemaphoreSlim turnstile = new(1, 1), roomEmpty = new(1, 1), readerMutex = new(1, 1);
    int readers;

    public WorldLockStats Stats { get; } = new();

    public async Task EnterExclusiveAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await turnstile.WaitAsync(ct);
        // Past the turnstile the acquisition completes (shared turns drain), so it is not cancelled half way.
        await roomEmpty.WaitAsync(CancellationToken.None);
        Stats.Waited(true, started);
    }

    public void ExitExclusive()
    {
        turnstile.Release();
        roomEmpty.Release();
    }

    public async Task EnterSharedAsync(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await turnstile.WaitAsync(ct);
        turnstile.Release();
        await readerMutex.WaitAsync(CancellationToken.None);
        try
        {
            if (++readers == 1) await roomEmpty.WaitAsync(CancellationToken.None);
        }
        finally { readerMutex.Release(); }
        Stats.Waited(false, started);
    }

    public void ExitShared()
    {
        // Held only for the counter: no shared turn is inside while the first one waits for the room.
        readerMutex.Wait();
        try
        {
            if (--readers == 0) roomEmpty.Release();
        }
        finally { readerMutex.Release(); }
    }
}

/// <summary>Waits for and holds of the world lock since startup (thread-safe counters); <see cref="Snapshot.Since"/> gives a window.</summary>
public sealed class WorldLockStats
{
    long exclusiveTurns, sharedTurns, exclusiveWaitTicks, sharedWaitTicks, exclusiveHoldTicks, sharedHoldTicks, outsideCalls;
    readonly long started = Stopwatch.GetTimestamp();

    internal void Waited(bool exclusive, long since)
    {
        var waited = Stopwatch.GetTimestamp() - since;
        Interlocked.Increment(ref exclusive ? ref exclusiveTurns : ref sharedTurns);
        Interlocked.Add(ref exclusive ? ref exclusiveWaitTicks : ref sharedWaitTicks, waited);
    }

    internal void Held(bool exclusive, long ticks) => Interlocked.Add(ref exclusive ? ref exclusiveHoldTicks : ref sharedHoldTicks, ticks);
    internal void Outside() => Interlocked.Increment(ref outsideCalls);

    /// <summary>Totals since startup (milliseconds of waiting and holding, turns, storage calls run outside a turn).</summary>
    public sealed record Snapshot(double Seconds, long ExclusiveTurns, long SharedTurns, double ExclusiveWaitMs, double SharedWaitMs,
        double ExclusiveHoldMs, double SharedHoldMs, long StoreCallsOutside)
    {
        /// <summary>The window from <paramref name="earlier"/> to this one.</summary>
        public Snapshot Since(Snapshot earlier) => new(Seconds - earlier.Seconds, ExclusiveTurns - earlier.ExclusiveTurns, SharedTurns - earlier.SharedTurns,
            ExclusiveWaitMs - earlier.ExclusiveWaitMs, SharedWaitMs - earlier.SharedWaitMs, ExclusiveHoldMs - earlier.ExclusiveHoldMs,
            SharedHoldMs - earlier.SharedHoldMs, StoreCallsOutside - earlier.StoreCallsOutside);

        /// <summary>Share of wall time the world was held exclusively (1 = one core's worth of serial work).</summary>
        public double ExclusiveBusy => Seconds > 0 ? ExclusiveHoldMs / 1000 / Seconds : 0;
        /// <summary>Map work held per second of wall time, summed over maps (above 1: maps ran in parallel).</summary>
        public double SharedBusy => Seconds > 0 ? SharedHoldMs / 1000 / Seconds : 0;
        public double AverageExclusiveWaitMs => ExclusiveTurns > 0 ? ExclusiveWaitMs / ExclusiveTurns : 0;
        public double AverageSharedWaitMs => SharedTurns > 0 ? SharedWaitMs / SharedTurns : 0;
    }

    public Snapshot Read()
    {
        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        return new Snapshot((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency, Interlocked.Read(ref exclusiveTurns), Interlocked.Read(ref sharedTurns),
            Ms(Interlocked.Read(ref exclusiveWaitTicks)), Ms(Interlocked.Read(ref sharedWaitTicks)), Ms(Interlocked.Read(ref exclusiveHoldTicks)),
            Ms(Interlocked.Read(ref sharedHoldTicks)), Interlocked.Read(ref outsideCalls));
    }
}

/// <summary>
/// One game connection's turn in the shared world: exclusive, or shared plus its map's lock. Storage calls made
/// while it is held run outside it (<see cref="OutsideAsync{T}"/>, used by <see cref="TurnStore"/>): the locks are
/// released for the database round trip and taken again in the same mode afterwards, so one player's save no longer
/// stalls everyone. Other connections may run in between; this connection's own state is only touched by its loop.
/// </summary>
public sealed class WorldTurn(SharedWorld world)
{
    MapInstance? map;
    bool exclusive;
    int outside;
    long heldSince;

    /// <summary>Whether this connection holds the world now (not while one of its storage calls runs outside).</summary>
    public bool Held => (exclusive || map is not null) && Volatile.Read(ref outside) == 0;

    public async Task EnterWorldAsync(CancellationToken ct)
    {
        await world.Lock.EnterExclusiveAsync(ct);
        exclusive = true;
        heldSince = Stopwatch.GetTimestamp();
    }

    /// <summary>Shared world, then the map's own lock.</summary>
    public async Task EnterMapAsync(MapInstance on, CancellationToken ct)
    {
        await world.Lock.EnterSharedAsync(ct);
        await on.Lock.WaitAsync(CancellationToken.None);
        map = on;
        heldSince = Stopwatch.GetTimestamp();
    }

    public void Exit()
    {
        Release();
        map = null;
        exclusive = false;
    }

    void Release()
    {
        if (map is null && !exclusive) return;
        world.Lock.Stats.Held(exclusive, Stopwatch.GetTimestamp() - heldSince);
        if (map is { } held)
        {
            held.Lock.Release();
            world.Lock.ExitShared();
        }
        else world.Lock.ExitExclusive();
    }

    async Task ReacquireAsync()
    {
        // Not cancellable: the connection's loop releases the turn it thinks it holds.
        if (map is { } held)
        {
            await world.Lock.EnterSharedAsync(CancellationToken.None);
            await held.Lock.WaitAsync(CancellationToken.None);
        }
        else await world.Lock.EnterExclusiveAsync(CancellationToken.None);
        heldSince = Stopwatch.GetTimestamp();
    }

    /// <summary>Runs a storage call with this turn released (when it is held); the turn is held again when it returns.</summary>
    public async Task<T> OutsideAsync<T>(Func<Task<T>> call)
    {
        if (map is null && !exclusive) return await call();
        if (Interlocked.Increment(ref outside) == 1)
        {
            world.Lock.Stats.Outside();
            Release();
        }
        try { return await call(); }
        finally
        {
            if (Interlocked.Decrement(ref outside) == 0) await ReacquireAsync();
        }
    }
}

/// <summary>A connection's store whose calls run outside its <see cref="WorldTurn"/> (see <see cref="WorldTurn.OutsideAsync{T}"/>).</summary>
public class TurnStore : DispatchProxy
{
    INativeCharacterStore inner = null!;
    WorldTurn turn = null!;
    static readonly MethodInfo OutsideOf = typeof(TurnStore).GetMethod(nameof(OutsideTyped), BindingFlags.NonPublic | BindingFlags.Instance)!;
    static readonly ConcurrentDictionary<Type, MethodInfo> outsideByResult = new();

    public static INativeCharacterStore Wrap(INativeCharacterStore store, WorldTurn turn)
    {
        var proxy = Create<INativeCharacterStore, TurnStore>();
        var self = (TurnStore)(object)proxy;
        self.inner = store is TurnStore wrapped ? wrapped.inner : store;
        self.turn = turn;
        return proxy;
    }

    /// <summary>The store behind a wrapper (the implementation checks its partner's type in trades).</summary>
    public static INativeCharacterStore Unwrap(INativeCharacterStore store) => store is TurnStore wrapped ? wrapped.inner : store;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (args is not null)
            for (var i = 0; i < args.Length; i++)
                if (args[i] is TurnStore wrapped) args[i] = wrapped.inner;
        var result = method.ReturnType;
        if (!turn.Held || !typeof(Task).IsAssignableFrom(result)) return Call(method, args);
        if (!result.IsGenericType) return turn.OutsideAsync(async () => { await (Task)Call(method, args)!; return true; });
        return outsideByResult.GetOrAdd(result.GetGenericArguments()[0], t => OutsideOf.MakeGenericMethod(t)).Invoke(this, [method, args]);
    }

    Task<T> OutsideTyped<T>(MethodInfo method, object?[]? args) => turn.OutsideAsync(() => (Task<T>)Call(method, args)!);

    object? Call(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(inner, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inside)
        {
            ExceptionDispatchInfo.Throw(inside);
            throw;
        }
    }
}
