using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Cpu;

/// <summary>
/// Longer plain-English explanations of CPU activity, for someone who knows
/// SOLIDWORKS but not computer internals. Measured facts are stated plainly;
/// general patterns say "usually" or "normally"; things the app cannot see are
/// said out loud.
/// </summary>
public static class CpuExplainer
{
    public static readonly IReadOnlyList<WhyQuestion> Questions =
    [
        new("one-core", "why only one core?"),
        new("these-cores", "why these cores?"),
        new("more-cores", "would more cores help?"),
        new("bars", "what do the bars mean?"),
    ];

    /// <summary>A short paragraph: what is going on right now, in everyday words.</summary>
    public static string WhatIsHappening(CpuSnapshot s)
    {
        var sw = s.SolidWorks;

        if (sw is null)
            return "SOLIDWORKS isn't open, so there's nothing of its to explain yet. The bars show what the rest of your PC is doing.";

        if (!sw.ThreadsVisible)
            return "Windows is hiding the details of SOLIDWORKS's work from this app. That usually happens when SOLIDWORKS was started " +
                   "with \"Run as administrator\" and this app wasn't. The total is still correct; the breakdown isn't available.";

        if (sw.CoresWorth < 0.1)
            return s.SystemPercent >= 50
                ? "SOLIDWORKS is waiting for you, but another program is keeping the PC busy. If SOLIDWORKS feels slow right now, " +
                  "that other program is the more likely cause."
                : "SOLIDWORKS is open but waiting for you. It only does real work when you click, rebuild, open or save something. " +
                  "Small blips on the bars are Windows and other programs going about their business.";

        if (sw.TopThreadPercent >= 85 && sw.CoresWorth < 1.6)
            return "SOLIDWORKS is working through a job one step at a time, on a single core. Most modeling work, such as rebuilding " +
                   "features, has to happen in order because each feature is built on the one before it, so it can't be shared out. " +
                   "While this lasts, the speed of that one core decides how long you wait. The other cores sitting idle is normal, not a fault.";

        if (sw.CoresWorth >= 2)
            return "SOLIDWORKS has split this job across several cores at once. Some jobs can be cut into independent pieces, " +
                   "for example drawing several views, preparing graphics or meshing for simulation, so more cores finish them sooner.";

        return "SOLIDWORKS is doing small bits of work, such as responding to the mouse, redrawing the screen or tidying up in the background. " +
               "Nothing at this level should make you wait.";
    }

    /// <summary>The answer to one of <see cref="Questions"/>, using live numbers where they help.</summary>
    public static string Answer(string questionId, CpuSnapshot s, CpuTopology topo) => questionId switch
    {
        "one-core" => Analogies.With(OneCore(s), Analogies.Cores),
        "these-cores" => Analogies.With(TheseCores(s, topo),
            (topo.IsHybrid ? Analogies.PerformanceAndEfficiency : Analogies.Cores) +
            (topo.HasPairs ? " And " + Analogies.HyperThreading : "")),
        "more-cores" => Analogies.With(MoreCores(topo), Analogies.MoreCores),
        "bars" => Analogies.With(Bars(topo), Analogies.Bars),
        _ => "",
    };

    private static string OneCore(CpuSnapshot s)
    {
        string general =
            "Think of a rebuild like a recipe. Each feature (sketch, extrude, fillet...) is built on top of the one before: you can't " +
            "fillet an edge until the extrude that makes it exists. So SOLIDWORKS does most rebuild work one step after another, on one core. " +
            "Extra cores can't speed up a queue where every step waits for the previous one. That's why the speed of a single core matters " +
            "more for everyday modeling than the number of cores.";

        var sw = s.SolidWorks;
        if (sw is null || !sw.ThreadsVisible) return general;

        string now = sw.TopThreadPercent >= 85
            ? $"Right now that's exactly what is happening: one SOLIDWORKS task is using {sw.TopThreadPercent:0}% of a core."
            : sw.BusyThreads >= 2
                ? $"Right now SOLIDWORKS actually has {sw.BusyThreads} tasks busy at once, so this job is one of the kind that can be shared."
                : $"Right now SOLIDWORKS's busiest task is using {sw.TopThreadPercent:0}% of a core, so it isn't held up by a single core.";
        return general + "\n\n" + now;
    }

    private static string TheseCores(CpuSnapshot s, CpuTopology topo)
    {
        var parts = new List<string>
        {
            "SOLIDWORKS doesn't pick cores. Windows does. Many times a second, Windows decides which core runs each piece of work, " +
            "and it often moves work from one core to another. So a busy bar can hop around between readings: the total matters more " +
            "than which bar lights up.",
        };

        if (topo.IsHybrid)
        {
            string pairs = topo.HasPairs
                ? $" Each performance core shows up as two slots side by side (for example {topo.PerformanceSlots[0].Index:00} and {topo.PerformanceSlots[1].Index:00}), " +
                  "because it can juggle two jobs at once (hyper-threading). The two slots share one core, so when both are busy each gets less than a full core."
                : "";
            parts.Add(
                $"Your processor ({topo.ProcessorName}) has two kinds of core. {topo.PerformancePhysicalCount} performance cores, shown as slots " +
                $"{CpuTopology.SlotRange(topo.PerformanceSlots)}, are the fast ones.{pairs} {topo.EfficiencyPhysicalCount} efficiency cores, slots " +
                $"{CpuTopology.SlotRange(topo.EfficiencySlots)}, use less power but are slower. Windows normally puts the program you're using " +
                "on the performance cores and background jobs on the efficiency cores. On battery or in a power-saving mode, Windows may push " +
                "more work onto the efficiency cores, which can make SOLIDWORKS slower.");

            int busyP = topo.PerformanceSlots.Count(c => c.Index < s.CorePercent.Count && s.CorePercent[c.Index] >= CpuSnapshot.BusyCoreThreshold);
            int busyE = topo.EfficiencySlots.Count(c => c.Index < s.CorePercent.Count && s.CorePercent[c.Index] >= CpuSnapshot.BusyCoreThreshold);
            parts.Add($"Right now {busyP} of {topo.PerformanceSlots.Count} performance slots and {busyE} of {topo.EfficiencySlots.Count} efficiency slots are busy.");
        }
        else if (topo.HasPairs)
        {
            parts.Add($"Your processor ({topo.ProcessorName}) has one kind of core. Each one shows up as two slots because it can juggle two jobs at once (hyper-threading).");
        }

        parts.Add("What this app can't see yet: exactly which core SOLIDWORKS's own work is on at each moment. Windows only reveals that through " +
                  "detailed tracing, which is planned for a later step and will need the app to run as administrator.");
        return string.Join("\n\n", parts);
    }

    private static string MoreCores(CpuTopology topo) =>
        "It depends on the job.\n\n" +
        "Usually helped mostly by a faster single core: rebuilding parts and assemblies, solving mates, most everyday modeling.\n\n" +
        "Usually helped by more cores: creating and updating drawing views, rendering, simulation, and some steps of opening large assemblies.\n\n" +
        $"Your PC has {topo.Cores.Count} slots in total. If the sentence above often says \"one core is maxed\" while you wait, a faster processor " +
        "would help you more than one with extra cores. Later versions of this app will keep a history so you can see which kind of wait you have most.";

    private static string Bars(CpuTopology topo)
    {
        string types = topo.IsHybrid
            ? $" The letter under each number shows the kind of core: P = performance (slots {CpuTopology.SlotRange(topo.PerformanceSlots)}), " +
              $"E = efficiency (slots {CpuTopology.SlotRange(topo.EfficiencySlots)}). Pairs that share one performance core are drawn close together."
            : "";
        return
            "Each bar is one slot of your processor, and its height is how busy that slot was over the last second." + types + "\n\n" +
            "Colour: plain bar = less than half busy. Orange = busy (half or more). Red = maxed out (90% or more).\n\n" +
            "The bars show the whole PC, not just SOLIDWORKS. The numbers line above them shows SOLIDWORKS's share.";
    }
}
