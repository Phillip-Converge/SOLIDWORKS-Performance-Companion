namespace SWCompanion.Core.Cpu;

/// <summary>CPU readings averaged over the time since the previous sample.</summary>
/// <param name="SystemPercent">Whole PC, 0-100.</param>
/// <param name="CorePercent">Each logical core, 0-100.</param>
/// <param name="SolidWorks">SOLIDWORKS readings, or null if it isn't running or can't be read.</param>
public sealed record CpuSnapshot(
    double SystemPercent,
    IReadOnlyList<double> CorePercent,
    SolidWorksCpu? SolidWorks)
{
    /// <summary>A core counts as "busy" at or above this level.</summary>
    public const double BusyCoreThreshold = 50;

    public int BusyCores => CorePercent.Count(c => c >= BusyCoreThreshold);
    public int CoreCount => CorePercent.Count;
}

/// <param name="CoresWorth">How much CPU SOLIDWORKS used, in whole cores. 1.0 = one core flat out.</param>
/// <param name="PercentOfPc">The same, as a share of the whole PC (0-100).</param>
/// <param name="BusyThreads">SOLIDWORKS threads using at least half a core.</param>
/// <param name="TopThreadPercent">The busiest SOLIDWORKS thread, as % of one core (0-100).</param>
/// <param name="ThreadsVisible">False if Windows refused to show SOLIDWORKS's threads
/// (happens when SOLIDWORKS runs as administrator and this app doesn't).</param>
public sealed record SolidWorksCpu(
    double CoresWorth,
    double PercentOfPc,
    int BusyThreads,
    double TopThreadPercent,
    bool ThreadsVisible);
