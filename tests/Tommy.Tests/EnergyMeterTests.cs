using Tommy.Core.Telemetry;

namespace Tommy.Tests;

public class EnergyMeterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constant_power_over_an_hour_integrates_to_expected_kwh()
    {
        var meter = new EnergyMeter();
        meter.Sample(100, T0);
        meter.Sample(100, T0.AddHours(1)); // but a 1h gap exceeds MaxGap -> ignored

        // With the gap guard, one long interval is skipped; integrate in small steps instead.
        meter.Reset();
        var t = T0;
        for (var i = 0; i < 3600; i++) // 3600 one-second samples at 100W = 100Wh = 0.1 kWh
        {
            meter.Sample(100, t);
            t = t.AddSeconds(1);
        }
        Assert.Equal(0.1, meter.Kwh, precision: 3);
        Assert.Equal(100.0, meter.Wh, precision: 1);
    }

    [Fact]
    public void Trapezoidal_averages_between_samples()
    {
        var meter = new EnergyMeter();
        meter.Sample(0, T0);
        meter.Sample(200, T0.AddSeconds(2)); // avg 100W over 2s = 100 * (2/3600) / 1000 kWh
        Assert.Equal(100.0 * (2.0 / 3600.0) / 1000.0, meter.Kwh, precision: 9);
    }

    [Fact]
    public void Long_gap_is_not_integrated()
    {
        var meter = new EnergyMeter();
        meter.Sample(100, T0);
        meter.Sample(100, T0.AddMinutes(5)); // gap > MaxGap -> skipped
        Assert.Equal(0, meter.Kwh);
    }

    [Fact]
    public void Pause_starts_a_fresh_interval()
    {
        var meter = new EnergyMeter();
        meter.Sample(100, T0);
        meter.Pause();
        meter.Sample(100, T0.AddSeconds(1)); // first sample after pause -> no integration
        Assert.Equal(0, meter.Kwh);
    }

    [Fact]
    public void Cost_is_kwh_times_rate()
    {
        var meter = new EnergyMeter();
        var t = T0;
        for (var i = 0; i < 3600; i++) { meter.Sample(1000, t); t = t.AddSeconds(1); } // 1 kWh
        Assert.Equal(1.0, meter.Kwh, precision: 3);
        Assert.Equal(0.25, meter.Cost(0.25), precision: 3);
    }

    [Fact]
    public void Negative_power_is_clamped()
    {
        var meter = new EnergyMeter();
        meter.Sample(-50, T0);
        meter.Sample(-50, T0.AddSeconds(1));
        Assert.Equal(0, meter.Kwh);
    }
}
