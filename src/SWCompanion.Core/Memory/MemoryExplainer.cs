using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Memory;

/// <summary>
/// Plain-English wording for memory and Windows object limits. Measured facts
/// are stated plainly; general patterns say "usually"; limits are spelled out.
/// </summary>
public static class MemoryExplainer
{
    // Thresholds, in percent.
    public const double RamGettingFull = 80;
    public const double RamShort = 90;
    public const double CommitNearLimit = 90;
    public const double ObjectsWatch = 70;
    public const double ObjectsDanger = 90;

    public static readonly IReadOnlyList<WhyQuestion> Questions =
    [
        new("ram-vs-committed", "\"in ram\" vs \"set aside\"?"),
        new("objects", "what are drawing and window objects?"),
        new("runs-out", "what happens when memory runs out?"),
        new("more-ram", "would more ram help?"),
    ];

    /// <summary>One-line status, most urgent first.</summary>
    public static string Describe(MemorySnapshot m)
    {
        var sw = m.SolidWorks;
        if (sw is not null && sw.GdiPercent >= ObjectsDanger)
            return $"Warning: SOLIDWORKS is close to its limit of drawing objects ({sw.GdiObjects:N0} of {sw.GdiLimit:N0}). Save your work.";
        if (sw is not null && sw.UserPercent >= ObjectsDanger)
            return $"Warning: SOLIDWORKS is close to its limit of window objects ({sw.UserObjects:N0} of {sw.UserLimit:N0}). Save your work.";
        if (m.CommitUsedPercent >= CommitNearLimit)
            return $"Warning: Windows is close to its total memory limit ({Gb(m.CommitUsed)} of {Gb(m.CommitLimit)}). Save your work.";
        if (m.RamUsedPercent >= RamShort)
            return $"Memory is nearly full: {Gb(m.UsedRam)} of {Gb(m.TotalRam)} in use, only {Gb(m.AvailableRam)} free.";
        if (m.RamUsedPercent >= RamGettingFull)
            return $"Memory is getting full: {Gb(m.UsedRam)} of {Gb(m.TotalRam)} in use, {Gb(m.AvailableRam)} free.";

        return sw is null
            ? $"Memory is fine: {Gb(m.AvailableRam)} of {Gb(m.TotalRam)} free. SOLIDWORKS is not running."
            : $"Memory is fine: {Gb(m.AvailableRam)} of {Gb(m.TotalRam)} free. SOLIDWORKS has {Gb((ulong)sw.InRam)} in RAM.";
    }

    /// <summary>A short paragraph: what this means for you right now.</summary>
    public static string WhatIsHappening(MemorySnapshot m)
    {
        var sw = m.SolidWorks;

        if (sw is not null && (sw.GdiPercent >= ObjectsDanger || sw.UserPercent >= ObjectsDanger))
            return "SOLIDWORKS has nearly used up its allowance of the objects Windows uses to draw the screen. When a program runs out, " +
                   "parts of its window and menus can go blank and it usually crashes soon after. Save your work now. Restarting SOLIDWORKS " +
                   "resets the count. Lots of open drawings and documents, and some add-ins, push the count up.";

        if (m.CommitUsedPercent >= CommitNearLimit)
            return "Windows is close to the most memory it can hand out (your RAM plus the page file on disk). If it runs out, programs " +
                   "that ask for more, SOLIDWORKS included, can fail or crash. Save your work, then close documents or programs you don't need.";

        if (m.RamUsedPercent >= RamGettingFull)
            return "Memory is filling up. When RAM is full, Windows makes room by moving parts of programs out to the disk, and reading " +
                   "them back later makes everything feel sluggish. Closing documents you don't need, or other programs, frees memory.";

        if (sw is null)
            return "There's plenty of free memory. Open SOLIDWORKS to see how much it uses.";

        string objects = sw.GdiPercent >= ObjectsWatch || sw.UserPercent >= ObjectsWatch
            ? " Its count of drawing or window objects is higher than usual; it's worth keeping an eye on and saving regularly."
            : "";
        return "There's plenty of free memory, so memory isn't slowing SOLIDWORKS down right now. SOLIDWORKS keeps open models, their " +
               "graphics and undo history in memory, so bigger assemblies and more open documents use more." + objects;
    }

    public static string Answer(string questionId, MemorySnapshot m) => questionId switch
    {
        "ram-vs-committed" => Analogies.With(RamVsCommitted(m), Analogies.RamAndPageFile),
        "objects" => Analogies.With(Objects(m), Analogies.ObjectLimits),
        "runs-out" => Analogies.With(RunsOut(m), Analogies.MemoryLimits),
        "more-ram" => Analogies.With(MoreRam(m), Analogies.MoreRam),
        _ => "",
    };

    private static string RamVsCommitted(MemorySnapshot m)
    {
        string general =
            "\"In RAM\" is the part of SOLIDWORKS's memory sitting in your fast memory chips right now. \"Set aside\" (Windows calls it " +
            "committed) is everything SOLIDWORKS has claimed for its own data, whether it's in RAM at the moment or parked in the page " +
            "file on disk.\n\n" +
            "When SOLIDWORKS is idle or minimised, Windows often moves parts of it out of RAM to give other programs room. That's why " +
            "\"in RAM\" can be much smaller than \"set aside\". It's normal. The cost comes when you switch back: the parked parts are " +
            "read back from disk, which is why SOLIDWORKS can feel slow for a moment after sitting in the background.";

        var sw = m.SolidWorks;
        if (sw is null) return general;
        return general + $"\n\nRight now SOLIDWORKS has {Gb((ulong)sw.InRam)} in RAM and {Gb((ulong)sw.Committed)} set aside. " +
               $"The most it has had in RAM since it started is {Gb((ulong)sw.PeakInRam)}.";
    }

    private static string Objects(MemorySnapshot m)
    {
        string general =
            "Besides memory, Windows gives every program a fixed allowance of two kinds of screen objects.\n\n" +
            "Drawing objects (GDI): the pens, brushes, fonts, icons and images used to paint the screen.\n" +
            "Window objects (USER): the windows, menus, buttons and toolbars themselves.\n\n" +
            "The allowance doesn't grow with more RAM. When a program reaches it, parts of its window and menus can go blank and it " +
            "usually crashes. SOLIDWORKS uses a lot of these, especially with many drawings and documents open, and some add-ins add more. " +
            "Closing documents gives some back; restarting SOLIDWORKS resets the count.";

        var sw = m.SolidWorks;
        if (sw is null) return general;

        string limitNote = sw.GdiLimit != MemorySampler.DefaultHandleLimit || sw.UserLimit != MemorySampler.DefaultHandleLimit
            ? $" On this PC the limits have been changed from Windows' usual {MemorySampler.DefaultHandleLimit:N0}."
            : "";
        return general +
               $"\n\nRight now SOLIDWORKS has {sw.GdiObjects:N0} of {sw.GdiLimit:N0} drawing objects (most so far: {sw.GdiPeak:N0}) " +
               $"and {sw.UserObjects:N0} of {sw.UserLimit:N0} window objects (most so far: {sw.UserPeak:N0})." + limitNote;
    }

    private static string RunsOut(MemorySnapshot m) =>
        "There are three different limits, and they feel different when you hit them.\n\n" +
        "RAM full: Windows moves parts of programs to the disk to make room. Nothing breaks, but everything gets sluggish, " +
        "and switching between programs is slow.\n\n" +
        "Total memory limit (RAM plus page file): programs can no longer get more memory. SOLIDWORKS may show an out-of-memory " +
        "message or crash.\n\n" +
        "Drawing or window object limit: SOLIDWORKS's menus and graphics can go blank or garbled, and it usually crashes, even " +
        "when plenty of RAM is free.\n\n" +
        $"Right now: RAM {m.RamUsedPercent:0}% used, total memory limit {m.CommitUsedPercent:0}% used" +
        (m.SolidWorks is { } sw ? $", drawing objects {sw.GdiPercent:0}%, window objects {sw.UserPercent:0}%." : ".");

    private static string MoreRam(MemorySnapshot m)
    {
        string now = m.RamUsedPercent < RamGettingFull
            ? $"Right now {Gb(m.AvailableRam)} of {Gb(m.TotalRam)} is free, so more RAM wouldn't make any difference at this moment."
            : $"Right now only {Gb(m.AvailableRam)} of {Gb(m.TotalRam)} is free, so more RAM would likely help with what you're doing now.";
        return now + "\n\n" +
               "More RAM helps when memory regularly fills up, usually with very large assemblies, many open documents, or SOLIDWORKS " +
               "running alongside other heavy programs. Watch the RAM bar during your biggest jobs: if it often goes above about 80%, " +
               "more RAM is worth it. If it stays well below that, it won't make SOLIDWORKS faster.\n\n" +
               "More RAM does not raise the drawing or window object limits.";
    }

    /// <summary>
    /// Why "close to the memory limit" can appear with the memory chips only part full, in plain words, with this PC's
    /// numbers. The limit is the memory chips plus the page file (overflow space on the disk).
    /// </summary>
    public static string CommitPlain(MemorySnapshot m)
    {
        ulong pageFile = m.CommitLimit > m.TotalRam ? m.CommitLimit - m.TotalRam : 0;
        return $"Programs have reserved {Gb(m.CommitUsed)} of the {Gb(m.CommitLimit)} Windows can hand out " +
               $"({Gb(m.TotalRam)} of memory chips plus a {Gb(pageFile)} page file on the disk). That's {m.CommitUsedPercent:0}%. " +
               $"The memory chips show only {m.RamUsedPercent:0}% in use because programs reserve memory before they fill it. " +
               "When the reservations reach the limit, Windows refuses programs more memory, and SOLIDWORKS can stop responding or crash, " +
               "even with free memory showing." +
               (m.TopReserved.Count > 0
                   ? " Biggest reservations right now: " + string.Join(", ", m.TopReserved.Take(4).Select(u =>
                       $"{u.Name} {Gb(u.Bytes)}" + (u.Copies > 1 ? $" ({u.Copies} copies)" : ""))) + "."
                   : "");
    }

    /// <summary>"1.4 GB", "820 MB".</summary>
    public static string Gb(ulong bytes) =>
        bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
            : $"{bytes / (1024.0 * 1024):0} MB";
}
