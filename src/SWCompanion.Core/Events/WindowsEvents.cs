using System.Text.RegularExpressions;

namespace SWCompanion.Core.Events;

/// <summary>One record from Windows' own event logs (the ones Event Viewer shows). Level: 1 critical, 2 error, 3 warning, 4 information.</summary>
public sealed record WinEvent(DateTime Time, string Log, string Provider, int Id, int Level, string Message);

/// <summary>
/// A Windows event that matters for SOLIDWORKS, in plain words.
/// </summary>
/// <param name="Kind">"win-hang", "win-crash", "win-gpu", "win-disk", "win-memory", "win-network" or "win-throttle".</param>
/// <param name="Line">One short sentence for the running log.</param>
/// <param name="Detail">The useful facts from the event (program, module, which disk), kept short.</param>
/// <param name="Serious">Worth pulling out into the "came and went" panel on its own, even when SOLIDWORKS didn't freeze.</param>
/// <param name="SolidWorks">The event is about SOLIDWORKS or one of its helper programs.</param>
public sealed record EventFinding(DateTime Time, string Kind, string Line, string Detail, bool Serious, bool SolidWorks, string Source);

/// <summary>
/// Picks out the Windows events that explain slowdowns and freezes (programs hanging or crashing, the graphics driver
/// resetting, disk retries, low memory, network drops) and turns them into plain words. Everything else is ignored.
/// Pure text work, so it can be tested.
/// </summary>
public static class WinEventReader
{
    /// <summary>SOLIDWORKS and its helper programs (and PDM's), by program file name without ".exe".</summary>
    private static readonly HashSet<string> SwPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "sldworks", "sldworks_fs", "sldprocmon", "swvbaserver", "sldexitapp", "swshellfilelaunch", "sldtaskschd",
        "swdocumentmgr", "edrawings", "emodelviewer", "edmserver", "conisioadmin", "viewsetup", "swxjobs",
        "sldworkstaskscheduler", "swscheduler", "sldbgproc",
    };

    private static readonly HashSet<string> GraphicsDrivers = new(StringComparer.OrdinalIgnoreCase)
        { "nvlddmkm", "amdkmdag", "amdwddmg", "igfx", "igfxn", "igfxnd" };

    private static readonly HashSet<string> DiskControllers = new(StringComparer.OrdinalIgnoreCase)
        { "stornvme", "storahci", "iaStorAC", "iaStorVD", "iaStorA", "iaStorAVC", "nvme" };

    /// <summary>Plain words for an event, or null when it doesn't matter for SOLIDWORKS's speed.</summary>
    public static EventFinding? Read(WinEvent e)
    {
        string p = e.Provider;
        string msg = e.Message ?? "";
        string source = $"{e.Log} log, source \"{p}\", event {e.Id}";

        // A program stopped responding and Windows closed it.
        if (p == "Application Hang" && e.Id is 1002)
        {
            string prog = After(msg, "The program ") ?? After(msg, "Program ") ?? "A program";
            bool sw = IsSw(prog);
            return new(e.Time, "win-hang", $"Windows recorded that {Friendly(prog)} stopped responding and was closed.",
                $"Program: {prog}.", Serious: sw, sw, source);
        }

        // A program crashed.
        if (p == "Application Error" && e.Id is 1000)
        {
            string prog = Field(msg, "Faulting application name:") ?? "A program";
            string? module = Field(msg, "Faulting module name:");
            bool sw = IsSw(prog) || (module is not null && IsPdmModule(module) && prog.StartsWith("explorer", StringComparison.OrdinalIgnoreCase));
            return new(e.Time, "win-crash", $"Windows recorded that {Friendly(prog)} crashed" + (module is null ? "." : $" (in {module})."),
                $"Program: {prog}." + (module is null ? "" : $" The part that failed: {module}."), Serious: sw, sw, source);
        }

        // Windows Defender's own log: folder protection blocking a program, files it quarantined, scans starting.
        if (p == "Microsoft-Windows-Windows Defender")
        {
            string? process = Field(msg, "Process Name:");
            string? path = Field(msg, "Path:")?.Replace("file:_", "", StringComparison.OrdinalIgnoreCase);
            bool sw = (process is not null && IsSw(Path.GetFileName(process))) || (path is not null && IsSwFile(path));
            switch (e.Id)
            {
                case 1123 or 1124:
                {
                    string who = process is null ? "a program" : Friendly(Path.GetFileName(process));
                    string where = path is null ? "a protected folder" : $"\"{path}\"";
                    bool audit = e.Id == 1124;
                    return new(e.Time, "win-blocked",
                        audit ? $"Windows Defender's folder protection would have blocked {who} from changing {where} (audit mode, so it was allowed)."
                              : $"Windows Defender's folder protection blocked {who} from changing {where}.",
                        process is null ? "" : $"Program: {process}.", Serious: sw && !audit, sw, source);
                }
                case 1116 or 1117:
                {
                    string? threat = Field(msg, "Name:");
                    string what = path is null ? "a file" : $"\"{path}\"";
                    return new(e.Time, "win-quarantine",
                        e.Id == 1117 ? $"Windows Defender acted on {what} it considers a threat" + (threat is null ? "." : $" ({threat}).")
                                     : $"Windows Defender detected {what} as a threat" + (threat is null ? "." : $" ({threat})."),
                        sw ? "It looks like a SOLIDWORKS file or program." : "", Serious: sw, sw, source);
                }
                case 1000:
                {
                    string kind = Field(msg, "Scan Parameters:") ?? Field(msg, "Scan Type:") ?? "a";
                    return new(e.Time, "win-scan", $"Windows Defender started {kind.ToLowerInvariant()} scan.",
                        "Scans read many files, which can slow opening and saving while they run.", Serious: false, false, source);
                }
            }
            return null;
        }

        // Windows Error Reporting files its reports later, often many at once (e.g. after a restart or when it gets
        // online), so these say what happened EARLIER, not now. A quiet log line with the date it happened, never a hang-up.
        if (p == "Windows Error Reporting" && e.Id is 1001)
        {
            string? when = DumpDate(msg) is { } d ? $" (from {d:d MMM yyyy})" : "";
            if (msg.Contains("Event Name: BlueScreen", StringComparison.OrdinalIgnoreCase))
                return new(e.Time, "win-report", $"Windows filed a report about an earlier blue screen (the whole PC crashed){when}.",
                    "This is a report being sent now, not a new problem.", Serious: false, false, source);
            if (msg.Contains("LiveKernelEvent") && Regex.IsMatch(msg, @"P1:\s*(141|117|1a1|1a8)\b", RegexOptions.IgnoreCase))
                return new(e.Time, "win-report", $"Windows filed a report about an earlier graphics driver reset{when}.",
                    "This is a report being sent now, not a new problem.", Serious: false, false, source);
            return null;
        }

        // The graphics driver stopped responding and Windows restarted it ("TDR"), recorded as it happens.
        if ((p == "Display" && e.Id is 4101) || (GraphicsDrivers.Contains(p) && e.Level is 1 or 2))
        {
            return new(e.Time, "win-gpu", "Windows recorded that the graphics driver stopped responding and was restarted.",
                $"Reported by {p}.", Serious: true, false, source);
        }

        // Disk trouble: retried reads/writes, controller resets, bad blocks, file system errors.
        if ((p.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 7 or 11 or 51 or 52 or 153 or 154) ||
            (DiskControllers.Contains(p) && e.Id is 129 or 11) ||
            (p is "Ntfs" or "Microsoft-Windows-Ntfs" && e.Id is 55 or 98 or 137 or 140))
        {
            string what = e.Id switch
            {
                153 or 154 => "a disk had to retry reading or writing",
                129 => "the disk controller had to be reset because the disk stopped answering",
                7 => "a disk reported a bad block",
                51 => "a disk failed while Windows was swapping memory to it",
                55 or 98 or 137 or 140 => "the file system on a disk reported a problem",
                _ => "a disk reported an error",
            };
            string? which = Regex.Match(msg, @"\\Device\\Harddisk\d+\\DR\d+|\b[A-Z]:", RegexOptions.None) is { Success: true } m ? m.Value : null;
            return new(e.Time, "win-disk", $"Windows recorded that {what}.", which is null ? $"Reported by {p}." : $"Disk: {which}. Reported by {p}.",
                Serious: true, false, source);
        }

        // Windows ran low on memory.
        if (p == "Microsoft-Windows-Resource-Exhaustion-Detector" && e.Id is 2004)
        {
            var top = Regex.Matches(msg, @"([\w\.\-]+\.exe)\s*\(\d+\)\s*consumed\s*([\d,]+)\s*bytes", RegexOptions.IgnoreCase)
                .Select(x => $"{x.Groups[1].Value} ({Gb(x.Groups[2].Value)})").Take(3).ToList();
            return new(e.Time, "win-memory", "Windows recorded that it was running low on memory.",
                top.Count > 0 ? "Biggest users: " + string.Join(", ", top) + "." : "", Serious: true,
                top.Any(t => IsSw(t.Split(' ')[0])), source);
        }

        // A server name couldn't be looked up in time (slows opening files from network folders and vaults).
        if (p == "Microsoft-Windows-DNS-Client" && e.Id is 1014)
        {
            string? name = Regex.Match(msg, @"for the name\s+(\S+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            return new(e.Time, "win-network", "Windows recorded that looking up a server name timed out.",
                name is null ? "" : $"Name: {name}.", Serious: false, false, source);
        }

        // The network connection dropped.
        if (e.Level is 2 or 3 && e.Id is 4 or 27 or 32 &&
            (msg.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || msg.Contains("link is down", StringComparison.OrdinalIgnoreCase)))
        {
            return new(e.Time, "win-network", "Windows recorded that the network connection dropped.", $"Reported by {p}.",
                Serious: true, false, source);
        }

        // The processor was held back by the computer's firmware (heat or power limits).
        if (p == "Microsoft-Windows-Kernel-Processor-Power" && e.Id is 37)
        {
            return new(e.Time, "win-throttle", "Windows recorded that the processor's speed was being limited by the computer's firmware.",
                "Usually heat or a power limit.", Serious: false, false, source);
        }

        return null;
    }

    /// <summary>The event logs and IDs worth watching, as an Event Viewer query (used to filter before reading).</summary>
    public const string ApplicationQuery =
        "*[System[(Provider[@Name='Application Hang'] and EventID=1002) or (Provider[@Name='Application Error'] and EventID=1000) " +
        "or (Provider[@Name='Windows Error Reporting'] and EventID=1001)]]";

    public const string SystemQuery = "*[System[(Level=1 or Level=2 or Level=3) or EventID=37]]";

    /// <summary>
    /// When a reported problem really happened, from the crash file's name: "\Minidump\111926-21578-01.dmp" (month, day,
    /// 2-digit year) or "WATCHDOG-20261119-1234.dmp". Null if there's no such file name.
    /// </summary>
    internal static DateTime? DumpDate(string msg)
    {
        var m = Regex.Match(msg, @"\\Minidump\\(\d{2})(\d{2})(\d{2})-", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int mo) && int.TryParse(m.Groups[2].Value, out int d) && int.TryParse(m.Groups[3].Value, out int y)
            && mo is >= 1 and <= 12 && d is >= 1 and <= 31)
            try { return new DateTime(2000 + y, mo, d); } catch { }
        m = Regex.Match(msg, @"-(20\d{2})(\d{2})(\d{2})-\d+\.dmp", RegexOptions.IgnoreCase);
        if (m.Success)
            try { return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); } catch { }
        return null;
    }

    /// <summary>A SOLIDWORKS file (part, assembly, drawing, template, macro...) or anything in a SOLIDWORKS folder.</summary>
    internal static bool IsSwFile(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path, @"\.(sld\w+|prtdot|asmdot|drwdot|swp|swb|eprt|easm|edrw)\b", RegexOptions.IgnoreCase) ||
        path.Contains("SOLIDWORKS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Defender log to watch, and the events in it that matter.</summary>
    public const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";
    public const string DefenderQuery = "*[System[(EventID=1000 or EventID=1116 or EventID=1117 or EventID=1123 or EventID=1124)]]";

    internal static bool IsSw(string program) => SwPrograms.Contains(Path.GetFileNameWithoutExtension(program.Trim()));

    private static bool IsPdmModule(string module) =>
        module.StartsWith("edm", StringComparison.OrdinalIgnoreCase) || module.StartsWith("conisio", StringComparison.OrdinalIgnoreCase);

    private static string Friendly(string program) =>
        Path.GetFileNameWithoutExtension(program.Trim()).ToLowerInvariant() switch
        {
            "sldworks" => "SOLIDWORKS",
            "explorer" => "File Explorer (where the PDM vault view lives)",
            "edmserver" => "the PDM client service",
            "sldprocmon" => "SOLIDWORKS's process monitor",
            "swshellfilelaunch" => "SOLIDWORKS's file launcher",
            "edrawings" => "eDrawings",
            "" => "a program",
            _ => program.Trim(),
        };

    /// <summary>"The program SLDWORKS.exe version ..." → "SLDWORKS.exe".</summary>
    private static string? After(string msg, string lead)
    {
        int i = msg.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var word = msg[(i + lead.Length)..].TrimStart().Split(' ', '\r', '\n', ',').FirstOrDefault();
        return string.IsNullOrWhiteSpace(word) ? null : word;
    }

    /// <summary>"Faulting application name: SLDWORKS.exe, version: ..." → "SLDWORKS.exe".</summary>
    private static string? Field(string msg, string label)
    {
        int i = msg.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = msg[(i + label.Length)..].TrimStart();
        int end = rest.IndexOfAny([',', '\r', '\n']);
        var value = (end < 0 ? rest : rest[..end]).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string Gb(string bytes) =>
        double.TryParse(bytes.Replace(",", ""), out var b) ? $"{b / 1073741824.0:0.0} GB" : bytes;
}
