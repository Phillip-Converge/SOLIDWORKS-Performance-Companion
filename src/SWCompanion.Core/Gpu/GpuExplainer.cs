using SWCompanion.Core.Explain;
using SWCompanion.Core.Memory;

namespace SWCompanion.Core.Gpu;

/// <summary>
/// Plain-English wording for graphics. Measured facts are stated plainly;
/// general patterns say "usually"; things the app can't see are said out loud.
/// </summary>
public static class GpuExplainer
{
    public const double Busy = 85;
    public const double Quiet = 10;
    public const double VideoMemoryFull = 90;

    public static readonly IReadOnlyList<WhyQuestion> Questions =
    [
        new("which", "which graphics is sw using?"),
        new("what-it-does", "what does the graphics card do?"),
        new("slowing", "is graphics slowing me down?"),
        new("video-memory", "what is video memory?"),
    ];

    /// <summary>One-line status, most important first.</summary>
    public static string Describe(GpuSnapshot g)
    {
        var sw = g.SolidWorksAdapter;
        if (!g.SolidWorksRunning)
        {
            var main = g.DedicatedCard ?? g.Adapters.FirstOrDefault(a => a.Adapter.Kind != GpuKind.Software);
            return main is null
                ? "SOLIDWORKS is not running."
                : $"SOLIDWORKS is not running. {main.Adapter.Name} is {main.BusyPercent:0}% busy.";
        }
        if (sw is null)
            return "SOLIDWORKS hasn't started drawing with a graphics adapter yet.";
        if (sw.Adapter.Kind == GpuKind.Software)
            return "Warning: SOLIDWORKS is drawing without a graphics card, using Windows' software fallback. Graphics will be very slow.";
        if (sw.Adapter.Kind == GpuKind.BuiltIn && g.DedicatedCard is { } card)
            return $"SOLIDWORKS is drawing with the built-in {sw.Adapter.Name}, not the {card.Adapter.Name}.";

        string memory = sw.Adapter.DedicatedTotal > 0
            ? $", using {MemoryExplainer.Gb(sw.SwDedicated)} of its {MemoryExplainer.Gb(sw.Adapter.DedicatedTotal)} video memory"
            : "";
        return $"SOLIDWORKS is drawing with the {sw.Adapter.Name}: {sw.BusyPercent:0}% busy{memory}.";
    }

    /// <summary>A short paragraph: what this means for you right now.</summary>
    public static string WhatIsHappening(GpuSnapshot g)
    {
        var sw = g.SolidWorksAdapter;
        if (!g.SolidWorksRunning)
            return "Open SOLIDWORKS to see which graphics it uses and how hard it works them.";
        if (sw is null)
            return "SOLIDWORKS hasn't put anything on a graphics adapter yet. Open a part, assembly or drawing and this will fill in.";

        if (sw.Adapter.Kind == GpuKind.Software)
            return "Windows is drawing SOLIDWORKS's graphics with the processor instead of a graphics card. That's usually a sign of a " +
                   "missing or broken graphics driver, or SOLIDWORKS's \"software OpenGL\" option being on. Rotating and zooming will be slow. " +
                   "This app only watches; it doesn't change drivers or settings.";

        if (sw.Adapter.Kind == GpuKind.BuiltIn && g.DedicatedCard is not null)
            return "SOLIDWORKS is using the built-in graphics, which share your main memory and are much slower than your dedicated " +
                   "graphics card. This usually happens when Windows or the graphics driver hands SOLIDWORKS to the power-saving graphics. " +
                   "It can be changed in Windows Settings > Display > Graphics, or in the graphics card's own control panel. " +
                   "This app only watches; it doesn't change the setting.";

        if (sw.Adapter.DedicatedTotal > 0 && sw.DedicatedUsed * 100.0 / sw.Adapter.DedicatedTotal >= VideoMemoryFull)
            return "The graphics card's own memory is nearly full. When it fills, models and textures spill into slower main memory " +
                   "and graphics can stutter. Closing documents, or turning off RealView and high-resolution appearances, frees some.";

        if (sw.BusyPercent >= Busy)
            return "The graphics card is working flat out. If SOLIDWORKS feels slow while you rotate, zoom or pan, graphics is the " +
                   "bottleneck right now. RealView, shadows, ambient occlusion and anti-aliasing all add work; turning them off, or " +
                   "simplifying a large assembly's display, usually helps.";

        if (sw.BusyPercent < Quiet)
            return "The graphics card is resting. It only works hard while the picture changes: rotating, zooming, panning, or " +
                   "redrawing after a rebuild. If SOLIDWORKS is slow while the graphics card is this quiet, the wait is somewhere " +
                   "else (the processor, the disk or the network), not graphics.";

        return "The graphics card is drawing what you see and has room to spare, so it isn't what's holding you up.";
    }

    public static string Answer(string questionId, GpuSnapshot g) => questionId switch
    {
        "which" => Analogies.With(Which(g), Analogies.GraphicsCard),
        "what-it-does" => Analogies.With(WhatItDoes(), Analogies.GraphicsCard),
        "slowing" => Analogies.With(Slowing(g), Analogies.GraphicsBottleneck),
        "video-memory" => Analogies.With(VideoMemory(g), Analogies.VideoMemory),
        _ => "",
    };

    private static string Which(GpuSnapshot g)
    {
        var parts = new List<string>();
        var shown = g.Adapters.Where(a => a.Adapter.Kind != GpuKind.Software || a.SwDedicated + a.SwShared > 0).ToList();
        parts.Add("This PC has: " + string.Join("; ", shown.Select(a => $"{a.Adapter.Name} ({KindWords(a.Adapter.Kind)})")) + ".");

        var sw = g.SolidWorksAdapter;
        if (sw is null)
            parts.Add(g.SolidWorksRunning ? "SOLIDWORKS hasn't used any of them yet." : "SOLIDWORKS isn't running.");
        else
        {
            parts.Add($"SOLIDWORKS is drawing with the {sw.Adapter.Name}. The app can tell because that's where SOLIDWORKS keeps " +
                      $"its models for display ({MemoryExplainer.Gb(sw.SwDedicated + sw.SwShared)} right now).");
            var others = g.Adapters.Where(a => a != sw && a.SwDedicated + a.SwShared > 0 && a.Adapter.Kind != GpuKind.Software).ToList();
            if (others.Count > 0)
                parts.Add($"It also has a little on the {string.Join(", ", others.Select(o => o.Adapter.Name))} " +
                          $"({MemoryExplainer.Gb((ulong)others.Sum(o => (long)(o.SwDedicated + o.SwShared)))}). On laptops that's usually normal: " +
                          "the built-in graphics drives the laptop screen and the graphics card hands it the finished picture.");
        }

        parts.Add($"Windows' own per-app graphics setting for SOLIDWORKS (Settings > Display > Graphics): {g.WindowsPreference}." +
                  (g.WindowsPreference == "not set"
                      ? " When it's not set, the graphics driver's own settings decide."
                      : ""));
        parts.Add("What this app can't see: the graphics card maker's own control panel settings (for example the NVIDIA Control " +
                  "Panel's choice for SOLIDWORKS), or whether the driver is one SOLIDWORKS has certified.");
        return string.Join("\n\n", parts);
    }

    private static string WhatItDoes() =>
        "The graphics card draws what you see on screen. In SOLIDWORKS that means turning your model into a picture every time " +
        "it changes: rotating, zooming, panning, selecting and highlighting, and redrawing after a rebuild.\n\n" +
        "Extra effects make that picture more work: RealView, shadows, ambient occlusion, anti-aliasing, and detailed appearances " +
        "and decals. Large assemblies with many components and fine detail are more work too.\n\n" +
        "The graphics card doesn't rebuild features, solve mates or open files. Those are the processor's job. So a faster " +
        "graphics card makes rotating and zooming smoother, but it doesn't make rebuilds quicker.";

    private static string Slowing(GpuSnapshot g)
    {
        var sw = g.SolidWorksAdapter;
        string now = sw is null
            ? "SOLIDWORKS isn't using graphics right now, so graphics isn't slowing anything."
            : sw.BusyPercent >= Busy
                ? $"Right now, maybe: the {sw.Adapter.Name} is {sw.BusyPercent:0}% busy."
                : $"Right now, no: the {sw.Adapter.Name} is only {sw.BusyPercent:0}% busy.";
        return now + "\n\n" +
               "How to tell for yourself: rotate the model and watch this section. If the graphics card jumps to near 100% and the " +
               "movement is jerky, graphics is the limit. If movement is jerky but the graphics card stays low, watch the CPU section: " +
               "one core maxed usually means SOLIDWORKS is busy working something out before it can redraw.";
    }

    private static string VideoMemory(GpuSnapshot g)
    {
        string general =
            "Video memory is fast memory on the graphics card itself. SOLIDWORKS keeps the shapes, edges and textures it is " +
            "displaying there so it can redraw quickly. Built-in graphics don't have their own, so they borrow main memory (\"shared\"), " +
            "which is slower.\n\n" +
            "When video memory fills up, the graphics card also borrows main memory and drawing can stutter.";
        var sw = g.SolidWorksAdapter;
        if (sw is null || sw.Adapter.DedicatedTotal == 0) return general;
        ulong used = Math.Max(sw.DedicatedUsed, sw.SwDedicated);
        return general + $"\n\nRight now the {sw.Adapter.Name} has {MemoryExplainer.Gb(used)} of " +
               $"{MemoryExplainer.Gb(sw.Adapter.DedicatedTotal)} in use, {MemoryExplainer.Gb(sw.SwDedicated)} of it by SOLIDWORKS. " +
               "(Windows counts a program's share slightly differently from the card's total, so the two can disagree a little; " +
               "the app shows the larger.)";
    }

    private static string KindWords(GpuKind kind) => kind switch
    {
        GpuKind.Dedicated => "graphics card",
        GpuKind.BuiltIn => "built-in graphics",
        _ => "Windows' software fallback",
    };
}
