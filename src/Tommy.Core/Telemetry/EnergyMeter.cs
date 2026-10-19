namespace Tommy.Core.Telemetry;

/// <summary>
/// Integrates GPU power draw (watts) over wall-clock time into energy (kWh), and
/// turns that into an electricity-cost estimate. Because this is a local model,
/// cost is <b>power</b>, not tokens. GPU-only is a fine v1 — PSU efficiency and
/// CPU/RAM draw aren't modelled, so results are labelled an estimate.
/// </summary>
public sealed class EnergyMeter
{
    // Don't integrate across a long gap (idle between generations, app asleep):
    // a sample after a big gap starts a fresh interval rather than inventing energy.
    private const double MaxGapSeconds = 10.0;

    private DateTimeOffset? _lastAt;
    private double _lastWatts;

    /// <summary>Accumulated energy in kilowatt-hours.</summary>
    public double Kwh { get; private set; }

    /// <summary>Accumulated energy in watt-hours (handy per-message).</summary>
    public double Wh => Kwh * 1000.0;

    /// <summary>Add a power reading; trapezoidal integration against the previous sample.</summary>
    public void Sample(double watts, DateTimeOffset at)
    {
        if (watts < 0) watts = 0;

        if (_lastAt is { } prev)
        {
            var seconds = (at - prev).TotalSeconds;
            if (seconds > 0 && seconds <= MaxGapSeconds)
            {
                var avgWatts = (_lastWatts + watts) / 2.0;
                Kwh += avgWatts * (seconds / 3600.0) / 1000.0;
            }
        }

        _lastAt = at;
        _lastWatts = watts;
    }

    /// <summary>End the current interval so the next sample doesn't integrate across the gap.</summary>
    public void Pause() => _lastAt = null;

    public void Reset()
    {
        Kwh = 0;
        _lastAt = null;
        _lastWatts = 0;
    }

    /// <summary>Seed the accumulated total (e.g. from a reloaded chat) without integrating a gap.</summary>
    public void Restore(double kwh)
    {
        Kwh = kwh > 0 ? kwh : 0;
        _lastAt = null;
        _lastWatts = 0;
    }

    public double Cost(double ratePerKwh) => Kwh * ratePerKwh;
}
