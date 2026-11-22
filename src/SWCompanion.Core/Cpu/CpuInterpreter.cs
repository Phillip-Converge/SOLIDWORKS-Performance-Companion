namespace SWCompanion.Core.Cpu;

/// <summary>
/// Turns CPU numbers into one plain-English sentence. These are patterns, not
/// proof: the wording says "usually means" whenever it is an interpretation.
/// </summary>
public static class CpuInterpreter
{
    public static string Describe(CpuSnapshot s)
    {
        var sw = s.SolidWorks;
        string pc = $"PC total {s.SystemPercent:0}%, {s.BusyCores} of {s.CoreCount} cores busy.";

        if (sw is null)
            return s.SystemPercent >= 50
                ? $"SOLIDWORKS is not running, but the PC is busy. {pc}"
                : $"SOLIDWORKS is not running. {pc}";

        if (!sw.ThreadsVisible)
            return $"SOLIDWORKS is using {Cores(sw.CoresWorth)}. Windows won't show its threads, " +
                   "probably because SOLIDWORKS runs as administrator and this app doesn't. " + pc;

        if (sw.CoresWorth < 0.1)
            return s.SystemPercent >= 50
                ? $"SOLIDWORKS is idle, but something else is keeping the PC busy. {pc}"
                : $"SOLIDWORKS is idle. {pc}";

        if (sw.TopThreadPercent >= 85 && sw.CoresWorth < 1.6)
            return $"One core is maxed by SOLIDWORKS and the rest are mostly idle. " +
                   "This pattern usually means single-threaded work such as a rebuild, opening a file or solving mates. " + pc;

        if (sw.CoresWorth >= 2)
            return $"SOLIDWORKS is spreading work over several cores ({Cores(sw.CoresWorth)}). " +
                   "This pattern usually means multi-threaded work such as graphics, drawing views or meshing. " + pc;

        return $"SOLIDWORKS is working lightly ({Cores(sw.CoresWorth)}). {pc}";
    }

    /// <summary>"0.4 of a core", "1.0 core", "3.2 cores".</summary>
    public static string Cores(double coresWorth) => coresWorth switch
    {
        < 0.95 => $"{coresWorth:0.0} of a core",
        < 1.05 => "1.0 core",
        _ => $"{coresWorth:0.0} cores",
    };
}
