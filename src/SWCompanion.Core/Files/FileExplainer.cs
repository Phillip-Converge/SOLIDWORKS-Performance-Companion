using SWCompanion.Core.Explain;
using SWCompanion.Core.Io;

namespace SWCompanion.Core.Files;

/// <summary>Plain-English wording for the files SOLIDWORKS is using.</summary>
public static class FileExplainer
{
    /// <summary>A file SOLIDWORKS waited on at least this long is worth pointing out.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SlowScan = TimeSpan.FromMilliseconds(500);

    public static readonly IReadOnlyList<WhyQuestion> Questions =
    [
        new("what-is-tracing", "what is file tracing?"),
        new("why-slow", "why would one file be slow?"),
        new("antivirus", "what does the antivirus have to do with it?"),
        new("network-files", "why are network files slower?"),
    ];

    /// <summary>"Housing.SLDPRT from \\server\share: waited 4.2 s, read 12 MB, Defender scanned it for 0.8 s."</summary>
    public static string Line(FileActivity f)
    {
        var parts = new List<string>();
        if (f.WaitTime >= TimeSpan.FromMilliseconds(50)) parts.Add($"waited {Secs(f.WaitTime)}");
        if (f.BytesRead > 0) parts.Add($"read {IoExplainer.Size((ulong)f.BytesRead)}");
        if (f.BytesWritten > 0) parts.Add($"wrote {IoExplainer.Size((ulong)f.BytesWritten)}");
        if (f.ScannedByAntivirus) parts.Add($"Windows Defender scanned it ({Secs(f.AntivirusTime)})");
        return $"{f.Name} from {f.Where}" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + ".";
    }

    /// <summary>One sentence for the top of the section.</summary>
    public static string Describe(IReadOnlyList<FileActivity> active, IReadOnlyList<FileActivity> recent, bool tracing)
    {
        if (!tracing) return "File tracing is off.";
        var docs = active.Where(f => f.IsSolidWorksDocument).ToList();
        if (docs.Count > 0)
            return $"SOLIDWORKS is using {docs.Count} document{(docs.Count == 1 ? "" : "s")} right now: " +
                   string.Join(", ", docs.Take(3).Select(d => d.Name)) + (docs.Count > 3 ? "..." : "") + ".";
        if (active.Count > 0) return $"SOLIDWORKS is using {active.Count} file{(active.Count == 1 ? "" : "s")} (none are SOLIDWORKS documents).";
        var slow = recent.FirstOrDefault(f => f.WaitTime >= Slow);
        return slow is null
            ? "SOLIDWORKS isn't reading any files right now."
            : $"Quiet now. Slowest file in the last 2 minutes: {Line(slow)}";
    }

    /// <summary>What this means: picks out the most important pattern.</summary>
    public static string WhatIsHappening(IReadOnlyList<FileActivity> recent, bool tracing)
    {
        if (!tracing)
            return "Turn on file tracing to see each file SOLIDWORKS opens, how long it waited for it, whether it came from this PC " +
                   "or a server, and whether Windows Defender held it up. Windows only allows this when the Companion runs as administrator.";

        var scanned = recent.Where(f => f.ScannedByAntivirus && f.AntivirusTime >= SlowScan).ToList();
        if (scanned.Count > 0)
            return $"Windows Defender is scanning SOLIDWORKS files as they open ({scanned.Count} recently, " +
                   $"{Secs(new TimeSpan(scanned.Sum(f => f.AntivirusTime.Ticks)))} in total). Each scan holds up the open until it's done. " +
                   "If this happens a lot, your IT team can ask Defender to skip SOLIDWORKS's own file types and folders.";

        var network = recent.Where(f => f.Location == FileLocation.Network && f.WaitTime >= Slow).ToList();
        if (network.Count > 0)
            return $"{network.Count} file{(network.Count == 1 ? "" : "s")} from a network location took over a second each " +
                   $"(slowest: {Line(network.OrderByDescending(f => f.WaitTime).First())}) Reading over the network is usually slower " +
                   "than from your own SSD; large assemblies with many network parts add up.";

        var slow = recent.Where(f => f.WaitTime >= Slow).ToList();
        if (slow.Count > 0)
            return $"{slow.Count} file{(slow.Count == 1 ? "" : "s")} took over a second to read (slowest: {Line(slow.First())}) " +
                   "Large files, a busy disk or a scan can all cause this.";

        return recent.Count == 0
            ? "Nothing to report: SOLIDWORKS hasn't opened files since tracing started."
            : "Files are opening quickly. Nothing here is slowing SOLIDWORKS down.";
    }

    public static string Answer(string id) => id switch
    {
        "what-is-tracing" => Analogies.With(
            "Windows keeps a running record of every file each program opens, reads and writes, and how long each step took. " +
            "The Companion listens to that record for SOLIDWORKS and Windows Defender only, and turns it into the list above. " +
            "It only listens; it doesn't change or slow down files. Windows only lets administrators listen, which is why the " +
            "Companion has to run as administrator for this section.",
            "a security camera over the parts counter. It doesn't hand out parts or get in the way; it just records who " +
            "collected what, and how long they stood waiting."),
        "why-slow" => Analogies.With(
            "Most often: the file is large; it's on a server rather than this PC; Windows Defender is scanning it; the disk is busy " +
            "with something else; or it's from an older SOLIDWORKS version and has to be converted as it opens. The list shows the " +
            "first three directly. Old-version conversion will come from the add-in step.",
            Analogies.Loading),
        "antivirus" => Analogies.With(
            "Windows Defender checks each file when a program opens it. Until the check finishes, SOLIDWORKS waits. For one part " +
            "that's a blink; for an assembly with hundreds of parts, it adds up. Companies often ask Defender to skip SOLIDWORKS " +
            "file types or project folders they trust. That's a decision for your IT team, and this app never changes antivirus settings.",
            Analogies.SlowDisk),
        "network-files" => Analogies.With(
            "Every read from a server travels over the network and back. A local SSD answers in a fraction of a millisecond; a " +
            "server usually takes several milliseconds per request, more over Wi-Fi or a VPN. Opening an assembly makes thousands " +
            "of small requests, so the difference multiplies.",
            Analogies.DiskOrNetwork),
        _ => "",
    };

    public static string Secs(TimeSpan t) => t.TotalSeconds >= 10 ? $"{t.TotalSeconds:0} s" : $"{t.TotalSeconds:0.0} s";
}
