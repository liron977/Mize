using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Mize.Reservations.Core.Telemetry;

namespace Mize.Reservations.Contracts;

/// <summary>
/// A meter factory owned by one test. Every <see cref="ReservationMetrics"/> built from it gets its own meter instance
/// (with this factory as its scope), so a <see cref="MeterProbe"/> sees only that test's measurements even while other tests
/// run in parallel. Framework types only.
/// </summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        lock (_meters) _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        lock (_meters) foreach (var m in _meters) m.Dispose();
    }
}

/// <summary>Collects what the instruments of one <see cref="TestMeterFactory"/> record.</summary>
public sealed class MeterProbe : IDisposable
{
    public sealed record Sample(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Sample> _samples = new();

    public TestMeterFactory Factory { get; } = new();

    public MeterProbe()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, Factory)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        _listener.Start();
    }

    public ReservationMetrics NewMetrics() => new(Factory);

    /// <summary>Asks observable instruments (gauges) to report now.</summary>
    public void ObserveGauges() => _listener.RecordObservableInstruments();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags) dict[tag.Key] = tag.Value;
        _samples.Enqueue(new Sample(instrument.Name, value, dict));
    }

    private IEnumerable<Sample> Matching(string instrument, (string Key, string Value)[] tags) =>
        _samples.Where(s => s.Instrument == instrument
                            && tags.All(t => s.Tags.TryGetValue(t.Key, out var v) && Equals(v?.ToString(), t.Value)));

    /// <summary>Sum of the recorded values (counters) with the given tags.</summary>
    public double Sum(string instrument, params (string Key, string Value)[] tags) => Matching(instrument, tags).Sum(s => s.Value);

    /// <summary>How many measurements were recorded (for a histogram: how many observations).</summary>
    public int Count(string instrument, params (string Key, string Value)[] tags) => Matching(instrument, tags).Count();

    public IReadOnlyList<double> Values(string instrument, params (string Key, string Value)[] tags) =>
        Matching(instrument, tags).Select(s => s.Value).ToList();

    public void Dispose()
    {
        _listener.Dispose();
        Factory.Dispose();
    }
}
