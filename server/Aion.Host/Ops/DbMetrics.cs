using System.Diagnostics.Metrics;
using Aion.World;

namespace Aion.Host.Ops;

/// <summary>
/// Database call time and errors for the health page (Stage G), from Npgsql's own metrics (meter "Npgsql", OpenTelemetry
/// names of Npgsql 10): every command of every data source (game store, accounts) is counted without touching the stores.
/// One callback per command into a lock-free histogram; no allocation.
/// </summary>
public sealed class DbMetrics : IDisposable
{
    public static DbMetrics Shared { get; } = new();

    readonly MeterListener listener = new();
    long failed, executing, pending, timeouts;

    /// <summary>Command durations (all data sources).</summary>
    public LatencyStats Operations { get; } = new();
    public long Failed => Interlocked.Read(ref failed);
    public long Executing => Interlocked.Read(ref executing);
    public long PendingConnectionRequests => Interlocked.Read(ref pending);
    public long ConnectionTimeouts => Interlocked.Read(ref timeouts);
    /// <summary>Whether Npgsql published its instruments (false: no database is configured, or a different Npgsql).</summary>
    public bool Seen { get; private set; }

    enum Kind { Duration, Failed, Executing, Pending, Timeouts }
    sealed record Tag(Kind Kind, double ToMicros);

    DbMetrics()
    {
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != "Npgsql") return;
            Kind? kind = instrument.Name switch
            {
                "db.client.operation.duration" => Kind.Duration,
                "db.client.operation.failed" => Kind.Failed,
                "db.client.operation.npgsql.executing" => Kind.Executing,
                "db.client.connection.npgsql.pending_requests" => Kind.Pending,
                "db.client.connection.npgsql.timeouts" => Kind.Timeouts,
                _ => null,
            };
            if (kind is null) return;
            Seen = true;
            l.EnableMeasurementEvents(instrument, new Tag(kind.Value, instrument.Unit == "ms" ? 1000 : 1_000_000));
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, state) => Measure(state, value));
        listener.SetMeasurementEventCallback<float>((_, value, _, state) => Measure(state, value));
        listener.SetMeasurementEventCallback<long>((_, value, _, state) => Measure(state, value));
        listener.SetMeasurementEventCallback<int>((_, value, _, state) => Measure(state, value));
        listener.Start();
    }

    void Measure(object? state, double value)
    {
        if (state is not Tag tag) return;
        switch (tag.Kind)
        {
            case Kind.Duration: Operations.RecordMicros((long)(value * tag.ToMicros)); break;
            case Kind.Failed: Interlocked.Add(ref failed, (long)value); break;
            case Kind.Executing: Interlocked.Add(ref executing, (long)value); break;
            case Kind.Pending: Interlocked.Add(ref pending, (long)value); break;
            case Kind.Timeouts: Interlocked.Add(ref timeouts, (long)value); break;
        }
    }

    public void Dispose() => listener.Dispose();
}
