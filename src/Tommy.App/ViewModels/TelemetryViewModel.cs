using CommunityToolkit.Mvvm.ComponentModel;
using Tommy.Core.Engine;

namespace Tommy.App.ViewModels;

/// <summary>
/// Live hardware readouts from the Strata /metrics feed, formatted for the
/// telemetry panel. Shows what the endpoint exposes and degrades to "—" when the
/// engine is unreachable — graceful by design.
/// </summary>
public partial class TelemetryViewModel : ObservableObject
{
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _state = "—";
    [ObservableProperty] private string? _gpuName;
    [ObservableProperty] private string _gpu = "—";
    [ObservableProperty] private string _vram = "—";
    [ObservableProperty] private string? _cpuName;
    [ObservableProperty] private string _cpu = "—";
    [ObservableProperty] private string _ram = "—";
    [ObservableProperty] private string _vision = "—";

    public void Update(EngineTelemetry t, bool visionOn)
    {
        HasData = true;
        State = t.LiveState;

        var h = t.Hardware;
        GpuName = t.Static.GpuName;
        Gpu = $"{h.GpuUtil:0}% · {h.GpuTempC:0}°C · {h.GpuPowerWatts:0}/{h.GpuPowerLimitWatts:0} W";
        Vram = $"{Gb(h.GpuMemUsedBytes):0.0} / {Gb(h.GpuMemTotalBytes):0.0} GB";

        CpuName = t.Static.CpuName;
        Cpu = $"{h.CpuPercent:0}%";
        Ram = $"{Gb(h.RamUsedBytes):0.0} / {Gb(h.RamTotalBytes):0.0} GB";

        Vision = visionOn ? "on" : "off";
    }

    public void Clear()
    {
        HasData = false;
        State = "unreachable";
    }

    private static double Gb(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;
}
