using SWCompanion.Core.Files;
using SWCompanion.Core.Explain;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Gpu;
using SWCompanion.Core.Io;
using SWCompanion.Core.Memory;

namespace SWCompanion.Core.Now;

public enum NowLevel { NotRunning, Calm, Busy, Frozen }

/// <param name="Kind">Short internal name, also used to total up a freeze's time by cause.</param>
/// <param name="Title">Plain-English cause, e.g. "Waiting for you: a dialog box is open".</param>
/// <param name="Evidence">The measured facts behind it.</param>
/// <param name="Glaring">True for an obvious, large slowdown the owner should see straight away.</param>
/// <param name="Fix">One plain-English line on what to do about it (shown with glaring causes).</param>
public sealed record Cause(string Kind, string Title, string Evidence, bool Glaring = false, string Fix = "");

/// <summary>Everything the right-now panel looks at, from the other sections.</summary>
public sealed record NowInputs(
    bool SolidWorksRunning,
    bool? Responding,
    TimeSpan UnresponsiveFor,
    CpuSnapshot? Cpu,
    MemorySnapshot? Memory,
    GpuSnapshot? Gpu,
    IoSnapshot? Io,
    OtherPrograms? Others,
    IReadOnlyList<SwWindow> Windows,
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<FileActivity>? ActiveFiles = null,
    VerdictReport? Verdict = null,
    IReadOnlyList<Pdm.ServerTraffic>? VaultTraffic = null,
    // What SOLIDWORKS itself says it is doing, via the add-in (e.g. "Switching to sheet "Sheet3": 20 s so far").
    string? InsideSolidWorks = null,
    // Windows Defender's processor use right now, in cores (watched without administrator rights).
    double? DefenderCores = null);

/// <param name="Headline">One line: the state of SOLIDWORKS right now.</param>
/// <param name="Causes">Most likely cause first. Empty when nothing stands out.</param>
/// <param name="Verdict">During a freeze: is it still making progress? (None otherwise.)</param>
public sealed record NowReport(NowLevel Level, string Headline, IReadOnlyList<Cause> Causes, VerdictReport? Verdict = null)
{
    public Cause? Top => Causes.FirstOrDefault();

    /// <summary>The obvious, large slowdowns to flag loudly, most important first.</summary>
    public IReadOnlyList<Cause> Glaring => Causes.Where(c => c.Glaring).ToList();
}

/// <summary>
/// Combines all the measurements into "what is going on right now", for a quick
/// glance from a second monitor. Patterns are described as likely, not certain;
/// what the app can't see yet is said plainly.
/// </summary>
public static class NowEngine
{
    /// <summary>No answer for this long counts as frozen.</summary>
    public static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(2);

    public static NowReport Evaluate(NowInputs n)
    {
        if (!n.SolidWorksRunning)
            return new NowReport(NowLevel.NotRunning, "SOLIDWORKS isn't open. Start it and the Companion will follow along.", []);

        bool frozen = n.Responding == false && n.UnresponsiveFor >= FrozenAfter;
        var causes = FindCauses(n).Select(c => Flag(c, n, frozen)).ToList();

        if (frozen)
        {
            if (causes.Count == 0 || causes.All(c => c.Kind is "setup" or "other-program" or "busy-notice"))
                causes.Insert(0, Waiting(n));
            // The busy notice explains the popup; it's never the cause, so it goes after the real causes.
            var notice = causes.FirstOrDefault(c => c.Kind == "busy-notice");
            if (notice is not null) { causes.Remove(notice); causes.Insert(Math.Min(causes.Count, causes.FindLastIndex(c => c.Kind != "setup") + 1), notice); }
            return new NowReport(NowLevel.Frozen, $"SOLIDWORKS has not responded for {Seconds(n.UnresponsiveFor)}.", causes, n.Verdict);
        }

        // A box that's simply open (SOLIDWORKS waiting for your answer) is worth noting but isn't "needs attention" on its own.
        var work = causes.Where(c => c.Kind is not ("setup" or "dialog-open") || c.Glaring).ToList();
        if (work.Count > 0)
            return new NowReport(NowLevel.Busy, "SOLIDWORKS is working, but something is slowing it down.", causes);

        return new NowReport(NowLevel.Calm, "All good. SOLIDWORKS is running smoothly.", causes);
    }

    private static List<Cause> FindCauses(NowInputs n)
    {
        var causes = new List<Cause>();
        if (n.InsideSolidWorks is { } inside)
            causes.Add(new Cause("inside", inside,
                "SOLIDWORKS itself reported this (through the Companion's connection to it): it is the action SOLIDWORKS started and hasn't finished yet. " +
                "Section 08 shows more once it finishes."));
        var cpu = n.Cpu?.SolidWorks;
        var sw = n.Io?.SolidWorks;

        // SOLIDWORKS boxes. Two very different situations:
        //  - SOLIDWORKS is responding and a box is open: it's a question waiting for you (maybe hidden behind a window).
        //  - SOLIDWORKS is frozen and a box still answers on its own: that's SOLIDWORKS's "busy running a command"
        //    notice. It is NOT what SOLIDWORKS is waiting on, so it must not be presented as the cause.
        bool frozenNow = n.Responding == false && n.UnresponsiveFor >= FrozenAfter;
        foreach (var d in n.Windows.Where(w => w.IsDialog && !w.IsMain).Take(1))
        {
            string title = string.IsNullOrWhiteSpace(d.Title) ? "untitled" : d.Title;
            if (!frozenNow)
                causes.Add(new Cause("dialog-open", "A SOLIDWORKS box is open",
                    $"\"{title}\" is open. While it's open, SOLIDWORKS waits for your answer and ignores clicks elsewhere, which can look " +
                    "like a freeze if the box is hidden behind another window or on your other screen. Think of it as " + Analogies.OpenDialog));
            else if (d.Responding == true)
                causes.Add(new Cause("busy-notice", "SOLIDWORKS is showing its \"busy running a command\" box",
                    BusyNoticeExplained));
        }

        // From file tracing (step 7), when it's on: the actual files involved.
        var files = n.ActiveFiles ?? [];
        // Defender busy while SOLIDWORKS works (seen from outside, no file tracing needed). Skipped when a file scan is already named.
        bool swWorking = (n.Cpu?.SolidWorks?.CoresWorth ?? 0) >= 0.3 || n.Responding == false;
        if (n.DefenderCores is { } dc && dc >= Antivirus.DefenderExplainer.BusyCores && swWorking &&
            !files.Any(f => f.ScannedByAntivirus && f.AntivirusTime >= FileExplainer.SlowScan))
            causes.Add(new Cause("defender-busy", "Windows Defender is busy",
                $"Defender is using {CpuInterpreter.Cores(dc)} right now while SOLIDWORKS works. It is most likely checking files " +
                "SOLIDWORKS is opening or saving (file tracing, as administrator, shows exactly which). Think of it as a security guard " +
                "opening every box before it reaches your bench."));
        foreach (var f in files.Where(f => f.ScannedByAntivirus && f.AntivirusTime >= FileExplainer.SlowScan)
                               .OrderByDescending(f => f.AntivirusTime).Take(1))
            causes.Add(new Cause("antivirus", $"Windows Defender is scanning {f.Name}",
                FileExplainer.Line(f) + " SOLIDWORKS waits until the scan finishes. Think of it as " +
                "a security guard opening every box before you can use it."));

        if (cpu is { ThreadsVisible: true } && cpu.TopThreadPercent >= 85 && cpu.CoresWorth < 1.6)
            causes.Add(new Cause("computing", "Computing on one core",
                $"SOLIDWORKS's busiest task is using {cpu.TopThreadPercent:0}% of a core and the other cores are mostly idle. " +
                "This usually means work that has to happen step by step: rebuilding, regenerating drawing views, or solving mates. " +
                "Think of it as " + Analogies.OneCoreBusy));
        else if (cpu is not null && cpu.CoresWorth >= 2)
            causes.Add(new Cause("computing-many", "Computing on several cores",
                $"SOLIDWORKS is using {CpuInterpreter.Cores(cpu.CoresWorth)}. Usually graphics preparation, drawing views or meshing."));

        // From vault traffic tracing (step 10b), when it's on: talking to the PDM servers right now.
        foreach (var t in (n.VaultTraffic ?? []).Where(t => t.Resent + t.Retried >= Pdm.VaultTrafficExplainer.TroubleCount).Take(1))
            causes.Add(new Cause("vault-trouble", $"Traffic to the vault's {t.Role} server is being resent or retried",
                Pdm.VaultTrafficExplainer.Line(t) + " Packets are being lost or blocked, so SOLIDWORKS waits for them to be sent again. " +
                "Think of it as a phone line that keeps dropping mid-sentence.",
                Glaring: true, Fix: "Check the Wi-Fi or VPN connection; if it keeps happening, ask IT whether a firewall or security software is interfering with this server."));
        foreach (var t in (n.VaultTraffic ?? []).Where(t => t.Role == "database" &&
                     (t.Requests >= Pdm.VaultTrafficExplainer.ChattyRequests || (t.Requests >= 3 && t.MedianReplyMs >= Pdm.VaultTrafficExplainer.SlowReplyMs))).Take(1))
            causes.Add(new Cause("vault-database", $"Talking to the vault database server ({t.Server})",
                Pdm.VaultTrafficExplainer.Line(t) + " Each request waits for a reply before the next one. Think of it as a string of short " +
                "phone calls to the vault office: the time on the line plus the clerk's lookup, over and over.",
                Glaring: t.MedianReplyMs >= Pdm.VaultTrafficExplainer.VerySlowReplyMs,
                Fix: t.ServerMs > t.FastestReplyMs
                    ? "The server itself is slow to answer: the server's own performance (or what else it's running) is the place to look."
                    : "Most of the wait is the network trip: a closer database, a faster link, or working on the office network instead of a VPN usually helps."));
        foreach (var t in (n.VaultTraffic ?? []).Where(t => t.Role == "archive" && t.BytesIn + t.BytesOut >= Pdm.VaultTrafficExplainer.BigTransferBytes).Take(1))
            causes.Add(new Cause("vault-archive", $"Transferring files with the vault archive server ({t.Server})",
                Pdm.VaultTrafficExplainer.Line(t) + " Files are coming from (or going to) the vault's warehouse; large or many files take longer, " +
                "especially over a slow link."));

        if (sw is not null && sw.ReadBytesPerSec >= IoExplainer.SwBusyBytesPerSec)
        {
            bool network = n.Io!.NetworkReceive >= IoExplainer.NetworkActiveBytesPerSec;
            causes.Add(new Cause("loading", network ? "Loading files, probably over the network" : "Loading files",
                $"SOLIDWORKS is reading {IoExplainer.Rate(sw.ReadBytesPerSec)}" +
                (network ? $" while the network brings in {IoExplainer.Rate(n.Io.NetworkReceive)}, so the files are probably on a server or vault." : ".") +
                " This happens when opening documents, switching to a sheet whose models aren't loaded, or loading components. " +
                "Think of it as " + Analogies.Loading + NamedFiles(files)));
        }
        else if (files.Where(f => f.WaitTime >= FileExplainer.Slow).OrderByDescending(f => f.WaitTime).FirstOrDefault() is { } slowFile)
        {
            // Not reading much right now, but stuck on a file that's slow to open or read.
            causes.Add(new Cause("files",
                $"Waiting on files: {slowFile.Name} from {slowFile.Where}",
                "SOLIDWORKS has spent a long time on these files just now:" + NamedFiles(files)));
        }

        if (sw is not null && sw.WriteBytesPerSec >= IoExplainer.SwBusyBytesPerSec)
            causes.Add(new Cause("saving", "Writing files",
                $"SOLIDWORKS is writing {IoExplainer.Rate(sw.WriteBytesPerSec)}: saving, or its temporary and backup files."));

        if (n.Gpu?.SolidWorksAdapter is { } g && g.SwBusyPercent >= GpuExplainer.Busy)
            causes.Add(new Cause("graphics", "Graphics card working flat out",
                $"The {g.Adapter.Name} is {g.BusyPercent:0}% busy drawing SOLIDWORKS. RealView, shadows and ambient occlusion add to this."));

        if (n.Memory is { } m)
        {
            if (m.SolidWorks is { } ms && (ms.GdiPercent >= MemoryExplainer.ObjectsDanger || ms.UserPercent >= MemoryExplainer.ObjectsDanger))
                causes.Add(new Cause("objects", "Close to the Windows object limit",
                    $"SOLIDWORKS has {ms.GdiObjects:N0} of {ms.GdiLimit:N0} drawing objects and {ms.UserObjects:N0} of {ms.UserLimit:N0} window " +
                    "objects. Near the limit, menus go blank and it usually crashes. Save your work."));
            // Two different limits, explained differently: the memory chips themselves filling up, or Windows running out of
            // memory it can promise to programs (which can happen with the chips only part full).
            if (m.RamUsedPercent >= MemoryExplainer.RamShort)
                causes.Add(new Cause("memory", "The memory chips are nearly full",
                    $"{MemoryExplainer.Gb(m.UsedRam)} of {MemoryExplainer.Gb(m.TotalRam)} of memory (RAM) is in use. Windows is moving parts of " +
                    "programs out to the disk to make room, and reading them back is slow, so everything feels sluggish."));
            else if (m.CommitUsedPercent >= MemoryExplainer.CommitNearLimit)
                causes.Add(new Cause("commit", "Windows is close to its memory limit",
                    MemoryExplainer.CommitPlain(m)));
        }

        if (n.Io?.BusiestDisk is { } disk && disk.BusyPercent >= IoExplainer.DiskBusy && disk.ResponseMs >= IoExplainer.DiskSlowMs)
            causes.Add(new Cause("disk", "The disk is struggling",
                $"Disk {disk.Name} is {disk.BusyPercent:0}% busy and taking {disk.ResponseMs:0} ms per request" +
                (TopIoOther(n) is { } io ? $"; the busiest other program is {io.Name} ({IoExplainer.Rate(io.IoBytesPerSec)})." : ".")));

        if (n.Others is { } o && n.Cpu is { } c && c.CoreCount > 0)
        {
            var top = o.TopCpu.FirstOrDefault();
            double otherPercent = o.OtherCoresTotal / c.CoreCount * 100;
            if (top is not null && (top.CoresWorth >= 1.5 || otherPercent >= 50))
                causes.Add(new Cause("other-program", $"Another program is busy: {top.Name}",
                    $"{top.Name} is using {CpuInterpreter.Cores(top.CoresWorth)}; all other programs together use {otherPercent:0}% of the PC."));
        }

        // Settings from the checkup that slow everything down, shown last as "also".
        // A red checkup item (power saver, capped processor, wrong graphics...) is glaring on its own.
        foreach (var check in n.Checks.Where(ch => ch.Status == CheckStatus.Problem || (ch.Status == CheckStatus.Look && ch.Id is "battery" or "power-plan")))
            causes.Add(new Cause("setup", $"Also: {check.Title}", check.Finding,
                Glaring: check.Status == CheckStatus.Problem,
                Fix: FirstSentence(check.HowToFix)));

        return causes;
    }

    /// <summary>
    /// Marks the causes that are an obvious, large slowdown, and adds a one-line fix.
    /// Kept deliberately strict so the loud alert only appears when it really matters.
    /// </summary>
    private static Cause Flag(Cause c, NowInputs n, bool frozen)
    {
        if (c.Glaring) return c;
        switch (c.Kind)
        {
            case "antivirus" when (n.ActiveFiles ?? []).Any(f => f.AntivirusTime >= TimeSpan.FromSeconds(2)):
                return c with { Glaring = true, Fix = "Ask your IT team whether Windows Defender can skip SOLIDWORKS file types or your project folders." };
            case "objects":
                return c with { Glaring = true, Fix = "Save now, close documents you don't need, then restart SOLIDWORKS to reset the count." };
            case "memory":
                return c with { Glaring = true, Fix = "Save, then close documents and other programs you don't need." };
            case "commit":
                return c with { Glaring = true, Fix = "Save your work. Then let Windows manage the page file size (the hang-up's explanation shows how), or close programs you don't need." };
            case "disk":
                return c with { Glaring = true, Fix = TopIoOther(n) is { } io
                    ? $"Pause or close {io.Name} if you can, then try again." : "Find what else is using the disk (backups, sync, updates) and pause it." };
            case "other-program" when n.Others?.TopCpu.FirstOrDefault() is { CoresWorth: >= 2 } top:
                return c with { Glaring = true, Fix = $"Close or pause {top.Name} while you work in SOLIDWORKS." };
            case "files" when (n.ActiveFiles ?? []).Any(f => f.WaitTime >= TimeSpan.FromSeconds(3)):
                return c with { Glaring = true, Fix = "These files are slow to read. Check the network connection to that location; files opened from your own PC load faster." };
            default:
                return c;
        }
    }

    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? text[..(dot + 1)] : text;
    }

    /// <summary>" Files: Housing.SLDASM from \\server\share: waited 4.2 s, ...; Bracket.SLDPRT ..." (top 3 by wait).</summary>
    private static string NamedFiles(IReadOnlyList<FileActivity> files)
    {
        var top = files.Where(f => f.IsSolidWorksDocument || f.WaitTime >= FileExplainer.Slow)
                       .OrderByDescending(f => f.WaitTime).Take(3).ToList();
        return top.Count == 0 ? "" : " Files: " + string.Join(" ", top.Select(FileExplainer.Line));
    }

    private static ProgramUse? TopIoOther(NowInputs n) =>
        n.Others?.TopIo.FirstOrDefault(p => p.IoBytesPerSec >= IoExplainer.SwBusyBytesPerSec);

    private static Cause Waiting(NowInputs n)
    {
        var cpu = n.Cpu?.SolidWorks;
        var io = n.Io?.SolidWorks;
        string measured = $"SOLIDWORKS is using {(cpu is null ? "almost no" : CpuInterpreter.Cores(cpu.CoresWorth))} processor and reading " +
                          $"{(io is null ? "nothing" : IoExplainer.Rate(io.ReadBytesPerSec))}.";
        return new Cause("waiting", "Waiting, not working",
            $"{measured} It isn't busy, it's waiting for something outside itself: often a reply from a server (vault, licence, a " +
            "network folder), a message box hidden behind another window, or another program. The app can't yet see exactly what " +
            "it's waiting on; the hang detective step will add that. Think of it as " + Analogies.Waiting);
    }

    /// <summary>What the "SOLIDWORKS is busy running a command" box means, and which button to pick.</summary>
    public const string BusyNoticeExplained =
        "This box is SOLIDWORKS saying \"I'm busy\"; it isn't asking you anything it needs to carry on. " +
        "\"Wait for the command to finish\" just closes the box and keeps waiting. \"Close SOLIDWORKS\" ends SOLIDWORKS, " +
        "and anything since your last save is lost. Use the verdict here to choose: Wait while it says WORKING; consider " +
        "Close SOLIDWORKS only once it says STUCK. The moving bar in that box most likely just shows the box is alive, " +
        "rather than how far the command has got.";

    public static string Seconds(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{(int)t.TotalMinutes} min {t.Seconds} s";
}
