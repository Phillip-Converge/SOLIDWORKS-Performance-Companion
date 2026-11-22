namespace SWCompanion.Core.Gpu;

/// <summary>One adapter's readings, averaged since the previous sample.</summary>
/// <param name="BusyPercent">How busy its busiest engine was (the way Task Manager reports it), 0-100.</param>
/// <param name="DedicatedUsed">Video memory in use by all programs.</param>
/// <param name="SharedUsed">Borrowed main memory in use by all programs.</param>
/// <param name="SwBusyPercent">How busy SOLIDWORKS kept this adapter, 0-100.</param>
/// <param name="SwDedicated">Video memory SOLIDWORKS is using on this adapter.</param>
/// <param name="SwShared">Borrowed main memory SOLIDWORKS is using on this adapter.</param>
public sealed record AdapterReading(
    GpuAdapterInfo Adapter,
    double BusyPercent,
    ulong DedicatedUsed,
    ulong SharedUsed,
    double SwBusyPercent,
    ulong SwDedicated,
    ulong SwShared);

/// <summary>Graphics readings for all adapters at one moment.</summary>
/// <param name="SolidWorksRunning">False if SOLIDWORKS isn't open.</param>
/// <param name="WindowsPreference">Windows' per-app graphics setting for SOLIDWORKS:
/// "not set", "let Windows decide", "power saving" or "high performance".</param>
public sealed record GpuSnapshot(
    IReadOnlyList<AdapterReading> Adapters,
    bool SolidWorksRunning,
    string WindowsPreference)
{
    /// <summary>
    /// The adapter SOLIDWORKS draws with: the one holding most of its video memory
    /// (a model's geometry and textures live there), or failing that the one it
    /// keeps busiest. Null if SOLIDWORKS isn't using any yet.
    /// </summary>
    public AdapterReading? SolidWorksAdapter =>
        !SolidWorksRunning ? null
        : Adapters.Where(a => a.SwDedicated + a.SwShared > 0 || a.SwBusyPercent > 0)
                  .OrderByDescending(a => a.Adapter.Kind == GpuKind.Dedicated ? a.SwDedicated : 0)
                  .ThenByDescending(a => a.SwDedicated + a.SwShared)
                  .ThenByDescending(a => a.SwBusyPercent)
                  .FirstOrDefault();

    /// <summary>A dedicated graphics card exists on this PC.</summary>
    public AdapterReading? DedicatedCard => Adapters.FirstOrDefault(a => a.Adapter.Kind == GpuKind.Dedicated);
}
