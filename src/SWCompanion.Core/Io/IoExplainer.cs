using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Io;

/// <summary>
/// Plain-English wording for disk and network activity. Says plainly that
/// Windows' per-program totals mix disk and network until file tracing (step 7).
/// </summary>
public static class IoExplainer
{
    /// <summary>SOLIDWORKS reading or writing above this is "busy".</summary>
    public const double SwBusyBytesPerSec = 5 * 1024 * 1024;
    /// <summary>A little activity below this counts as quiet.</summary>
    public const double SwQuietBytesPerSec = 256 * 1024;
    public const double DiskBusy = 80;
    public const double DiskSlowMs = 50;
    public const double NetworkActiveBytesPerSec = 1024 * 1024;

    public static readonly IReadOnlyList<WhyQuestion> Questions =
    [
        new("disk-or-network", "reading from disk or network?"),
        new("disk-busy", "what do busy and response time mean?"),
        new("slow-disk", "why would the disk be slow?"),
        new("network-who", "what is using the network?"),
    ];

    public static string Describe(IoSnapshot s)
    {
        var sw = s.SolidWorks;
        string swPart = sw is null
            ? "SOLIDWORKS is not running."
            : sw.ReadBytesPerSec < SwQuietBytesPerSec && sw.WriteBytesPerSec < SwQuietBytesPerSec
                ? "SOLIDWORKS isn't reading or writing much."
                : $"SOLIDWORKS is reading {Rate(sw.ReadBytesPerSec)} and writing {Rate(sw.WriteBytesPerSec)}.";
        var disk = s.BusiestDisk;
        string diskPart = disk is null ? "" : $" Disk {disk.BusyPercent:0}% busy.";
        return $"{swPart}{diskPart} Network: {Rate(s.NetworkReceive)} in, {Rate(s.NetworkSend)} out.";
    }

    public static string WhatIsHappening(IoSnapshot s)
    {
        var sw = s.SolidWorks;
        var disk = s.BusiestDisk;
        bool diskStruggling = disk is not null && disk.BusyPercent >= DiskBusy && disk.ResponseMs >= DiskSlowMs;

        if (diskStruggling)
            return $"The disk is struggling to keep up: busy {disk!.BusyPercent:0}% of the time and taking {disk.ResponseMs:0} ms per request " +
                   "(a healthy SSD usually answers in a few ms). Everything that needs the disk waits in line, SOLIDWORKS included.";

        if (sw is null)
            return "Open SOLIDWORKS to see how much it reads and writes.";

        bool swReading = sw.ReadBytesPerSec >= SwBusyBytesPerSec;
        bool swWriting = sw.WriteBytesPerSec >= SwBusyBytesPerSec;
        bool networkActive = s.NetworkReceive >= NetworkActiveBytesPerSec;

        if (swReading && networkActive)
            return "SOLIDWORKS is reading files and network traffic is up at the same time, so the files are probably coming over the " +
                   "network (a server share or a vault). That's usually slower than reading from your own SSD. Step 7 of this app will " +
                   "name each file and where it comes from.";
        if (swReading)
            return "SOLIDWORKS is reading files. This happens when you open documents, switch to a drawing sheet whose models aren't " +
                   "loaded yet, or load components. Where the files live matters: your own SSD is fastest; a network share is slower.";
        if (swWriting)
            return "SOLIDWORKS is writing: saving, or writing its temporary and backup files. Large saves to a network location take longer.";

        return "SOLIDWORKS isn't reading or writing much, so disk and network aren't what's holding it up right now. " +
               "If it's slow anyway, look at the CPU section.";
    }

    public static string Answer(string questionId, IoSnapshot s) => questionId switch
    {
        "disk-or-network" => Analogies.With(DiskOrNetwork(s), Analogies.DiskOrNetwork),
        "disk-busy" => Analogies.With(DiskBusyAnswer(s), Analogies.DiskBusy),
        "slow-disk" => Analogies.With(SlowDisk(s), Analogies.SlowDisk),
        "network-who" => Analogies.With(NetworkWho(s), Analogies.Network),
        _ => "",
    };

    private static string DiskOrNetwork(IoSnapshot s)
    {
        string now = s.SolidWorks is { } sw
            ? $"\n\nSince it started, SOLIDWORKS has read {Size(sw.TotalRead)} and written {Size(sw.TotalWritten)}."
            : "";
        return "Windows counts everything SOLIDWORKS reads in one total: files on your own disk, files on a network share or vault, " +
               "and a little other housekeeping. Without detailed tracing it can't say which part came from where.\n\n" +
               "A useful clue: if SOLIDWORKS is reading and the network is busy at the same moment, the files are probably coming " +
               "over the network.\n\n" +
               "Step 7 of this app (file tracing, which needs administrator rights) will name each file SOLIDWORKS reads, where it " +
               "comes from, and how long it took." + now;
    }

    private static string DiskBusyAnswer(IoSnapshot s)
    {
        string now = s.BusiestDisk is { } d
            ? $"\n\nRight now disk {d.Name} is {d.BusyPercent:0}% busy, answering in {d.ResponseMs:0.0} ms, reading {Rate(d.ReadBytesPerSec)} " +
              $"and writing {Rate(d.WriteBytesPerSec)}."
            : "";
        return "Busy is how much of the time the disk was working on something. 100% means there was always something waiting.\n\n" +
               "Response time is how long each read or write took. It's the better sign of trouble: a fast SSD can be \"100% busy\" and " +
               "still answer in a millisecond or two, while an old hard drive or an overloaded disk can take 50 ms or more, and " +
               "everything waits." + now;
    }

    private static string SlowDisk(IoSnapshot s) =>
        "Common reasons, most likely first:\n\n" +
        "Antivirus scanning each file as SOLIDWORKS opens it (step 7 will show this per file).\n" +
        "Another program using the disk heavily at the same time: backups, cloud sync (OneDrive, Dropbox), Windows Update, indexing.\n" +
        "Memory running low, so Windows uses the disk as overflow (see the memory and checkup sections).\n" +
        "A nearly full disk, or an old spinning hard drive instead of an SSD.\n\n" +
        (s.BusiestDisk is { } d && d.BusyPercent < DiskBusy
            ? $"Right now the disk looks fine ({d.BusyPercent:0}% busy, {d.ResponseMs:0.0} ms)."
            : "Right now the disk is busy, so one of these is likely.");

    private static string NetworkWho(IoSnapshot s)
    {
        var active = s.Networks.Where(n => n.ReceiveBytesPerSec + n.SendBytesPerSec > 1024).ToList();
        string now = active.Count == 0
            ? "Right now there's almost no network traffic."
            : "Right now: " + string.Join("; ", active.Select(n => $"{n.Name}: {Rate(n.ReceiveBytesPerSec)} in, {Rate(n.SendBytesPerSec)} out"));
        return "Windows reports network traffic per network adapter (Wi-Fi, Ethernet...), not per program, unless detailed tracing " +
               "is on. So these figures are the whole PC: SOLIDWORKS, the vault, browsers, cloud sync, updates and everything else.\n\n" +
               "Step 7 (file tracing) will show which traffic belongs to SOLIDWORKS and which server it's talking to.\n\n" + now;
    }

    /// <summary>"12.4 MB/s", "350 KB/s", "0 KB/s".</summary>
    public static string Rate(double bytesPerSec) =>
        bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024 * 1024):0.0} MB/s" : $"{bytesPerSec / 1024:0} KB/s";

    public static string Size(ulong bytes) =>
        bytes >= 1024UL * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB" : $"{bytes / (1024.0 * 1024):0} MB";
}
