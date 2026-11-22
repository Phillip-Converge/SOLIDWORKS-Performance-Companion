using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SWCompanion.Core.Crash;

/// <summary>One SOLIDWORKS crash, as SOLIDWORKS itself recorded it.</summary>
/// <param name="Modules">The program modules on the crash's call chain, innermost first (where the error surfaced, then who called it).</param>
/// <param name="LastActions">The last few things SOLIDWORKS logged you doing, e.g. "switched to a part".</param>
public sealed record CrashRecord(
    DateTime When, string Version, string Folder, string? Error, IReadOnlyList<string> Modules,
    IReadOnlyList<string> LastActions, IReadOnlyList<string> AddinCalls, long? MemoryBytes, int? GdiUsed, int? GdiLimit,
    int? SessionSeconds, bool HasDump)
{
    /// <summary>The first module that isn't one of Windows' own basic libraries: the best lead.</summary>
    public string? Lead => Modules.FirstOrDefault(m => !CrashModules.IsWindowsBase(m)) ?? Modules.FirstOrDefault();
}

/// <summary>
/// Reads the crash records SOLIDWORKS keeps in %APPDATA%\SOLIDWORKS\&lt;date&gt;_&lt;version&gt;: its performance log
/// (error, call chain, last actions, memory) and whether a crash dump was written. Read only; the dumps
/// themselves are never opened (they can contain model data).
/// </summary>
public static class CrashReader
{
    private static readonly Regex FolderName = new(@"^(\d{14})_(\d+\.\d+\.\d+\.\d+)$");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SOLIDWORKS");

    public static List<CrashRecord> ReadAll(string? root = null)
    {
        var list = new List<CrashRecord>();
        root ??= DefaultRoot;
        if (!Directory.Exists(root)) return list;
        foreach (var dir in SafeDirs(root))
        {
            var m = FolderName.Match(Path.GetFileName(dir));
            if (!m.Success) continue;
            bool dump = SafeFiles(dir, "*.dmp").Any();
            if (!dump) continue; // a session folder without a dump isn't a crash
            var when = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string log = Path.Combine(dir, "SolidWorksPerformance.log");
            list.Add(Parse(when, m.Groups[2].Value, dir, File.Exists(log) ? SafeRead(log) : null, dump));
        }
        return list.OrderByDescending(c => c.When).ToList();
    }

    /// <summary>Reads one performance log (pure, so it can be tested with made-up text).</summary>
    public static CrashRecord Parse(DateTime when, string version, string folder, string? log, bool hasDump)
    {
        string? error = null;
        var modules = new List<string>();
        var events = new List<string>();
        var addinCalls = new List<string>();
        long? memory = null;
        int? gdiUsed = null, gdiLimit = null, seconds = null;
        bool inCrash = false;

        foreach (var raw in (log ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("<EVENT>", StringComparison.Ordinal) && !inCrash)
                events.Add(line[7..].Trim().Trim('[', ']'));
            else if (line.StartsWith("<PROCMEM>", StringComparison.Ordinal) && !inCrash)
            {
                memory = Number(line, "PrivateBytes") ?? memory;
                gdiUsed = (int?)Number(line, "GDIHandlesUsed") ?? gdiUsed;
                gdiLimit = (int?)Number(line, "GDIHandlesTotal") ?? gdiLimit;
            }
            else if (line.StartsWith("<SWEXH>", StringComparison.Ordinal)) inCrash = true;
            else if (line.StartsWith("<EXCEPTION>", StringComparison.Ordinal))
                error = line[11..].Trim().Trim('"');
            else if (line.StartsWith("<RAWSTACK>", StringComparison.Ordinal))
            {
                modules = line[10..].Trim().TrimEnd(';').Split(',')
                    .Select(f => f.Split(':')[0].Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (line.StartsWith("<AD5>", StringComparison.Ordinal))
                addinCalls = line[5..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(5).ToList();
            else if (line.StartsWith("<RUNTIME>", StringComparison.Ordinal) && int.TryParse(line[9..].Trim(), out var s))
                seconds = s;
        }
        return new CrashRecord(when, version, folder, error, modules, events.TakeLast(3).Select(CrashModules.Action).ToList(),
            addinCalls, memory, gdiUsed, gdiLimit, seconds, hasDump);
    }

    private static long? Number(string line, string name)
    {
        var m = Regex.Match(line, name + @"=(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); } catch (Exception) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); } catch (Exception) { return []; }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            // SOLIDWORKS may still have it open; read it shared.
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// What crash codes and module names mean, in plain words. Only names the Companion is sure about are
/// explained; anything else is shown as is and marked "not in the list yet" (this list is meant to be
/// grown together with someone who knows SOLIDWORKS well).
/// </summary>
public static class CrashModules
{
    private static readonly string[] WindowsBase = ["ntdll", "KERNELBASE", "kernel32", "ucrtbase", "msvcrt", "vcruntime140", "user32"];

    public static bool IsWindowsBase(string module) => WindowsBase.Any(w => module.StartsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain meaning of a module, or null when it isn't in the list yet.</summary>
    public static string? Meaning(string module)
    {
        string m = module.ToLowerInvariant();
        if (m.StartsWith("pskernel")) return "Parasolid, the geometry engine that calculates the 3D shapes of parts";
        if (m.StartsWith("nvogl") || m.StartsWith("nvd3d") || m.StartsWith("nvwgf")) return "the NVIDIA graphics driver";
        if (m.StartsWith("atio6") || m.StartsWith("atig6") || m.StartsWith("amdxx") || m.StartsWith("atiumd")) return "the AMD graphics driver";
        if (m.StartsWith("ig") && (m.Contains("icd") || m.Contains("umd"))) return "the Intel graphics driver";
        if (m is "clr" or "coreclr" or "mscorlib" || m.StartsWith("system.")) return ".NET, the framework most SOLIDWORKS add-ins are written in";
        if (IsWindowsBase(module)) return "one of Windows' own basic libraries (where the error surfaced; rarely the cause itself)";
        if (m.StartsWith("sld") || m.StartsWith("sw")) return "part of SOLIDWORKS itself";
        return null;
    }

    /// <summary>Plain meaning of the error text SOLIDWORKS logged.</summary>
    public static string ErrorMeaning(string? error)
    {
        if (error is null) return "SOLIDWORKS didn't log the error itself.";
        if (error.StartsWith("Access Violation", StringComparison.OrdinalIgnoreCase))
            return "it tried to use a piece of memory it wasn't allowed to (a program error, usually set off by a particular file, feature or driver)";
        string code = Regex.Match(error, @"[0-9a-fA-F]{8}").Value.ToLowerInvariant();
        return code switch
        {
            "e0434352" => "an error inside .NET code, which usually means an add-in (or a .NET part of SOLIDWORKS)",
            "c000000d" => "a program was handed an invalid value",
            "c0000409" => "a safety check stopped it because memory had been overwritten",
            "c00000fd" => "it ran out of room for a very deep chain of steps (stack overflow)",
            "c0000017" or "c000012d" => "it ran out of memory",
            _ => $"error {error}",
        };
    }

    public static string Action(string evt) => evt switch
    {
        "EVT_SWITCH_TO_PART" => "switched to a part",
        "EVT_SWITCH_TO_ASSEMBLY" => "switched to an assembly",
        "EVT_SWITCH_TO_DRAWING" => "switched to a drawing",
        _ => evt.Replace("EVT_", "").Replace('_', ' ').ToLowerInvariant(),
    };
}

/// <summary>How a SOLIDWORKS session ended.</summary>
public enum Ending { Closed, Crashed, EndedWhileFrozen }

/// <summary>
/// Notices SOLIDWORKS ending and works out how: a crash (SOLIDWORKS wrote a crash record for that session:
/// its folder is named after the session's start time), ended while frozen (no crash record, but it wasn't
/// responding just before it disappeared: most likely ended from Task Manager or Windows' "close the program"),
/// or closed normally. Waits a little after it ends, because SOLIDWORKS writes its record on the way out.
/// </summary>
public sealed class CrashWatch
{
    public static readonly TimeSpan WaitForRecord = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SessionMatch = TimeSpan.FromMinutes(2);

    private int? _pid;
    private DateTime? _started;
    private bool _frozen;
    private (DateTime EndedAt, DateTime? Started, bool Frozen)? _pending;

    /// <summary>Every second: which SOLIDWORKS is running (if any), when it started, and whether it's frozen.</summary>
    public void Observe(DateTime now, int? pid, DateTime? started, bool frozen)
    {
        if (pid is not null)
        {
            if (pid != _pid) { _pid = pid; _started = started; }
            _frozen = frozen;
            return;
        }
        if (_pid is not null) _pending = (now, _started, _frozen); // it just disappeared
        _pid = null;
        _started = null;
        _frozen = false;
    }

    /// <summary>Once the wait is over, how it ended (and the crash record, if any). Null while waiting or if nothing ended.</summary>
    public (Ending How, CrashRecord? Record, DateTime EndedAt)? Decide(DateTime now, Func<IReadOnlyList<CrashRecord>> readCrashes)
    {
        if (_pending is not { } p || now - p.EndedAt < WaitForRecord) return null;
        _pending = null;
        var record = p.Started is { } s
            ? readCrashes().FirstOrDefault(c => (c.When - s).Duration() <= SessionMatch)
            : null;
        var how = record is not null ? Ending.Crashed : p.Frozen ? Ending.EndedWhileFrozen : Ending.Closed;
        return (how, record, p.EndedAt);
    }
}

/// <summary>Plain-English crash explanations, patterns across crashes, and the saved report.</summary>
public static class CrashExplainer
{
    /// <summary>"Crashed in Parasolid (the geometry engine...) right after you switched to a part."</summary>
    public static string Summary(CrashRecord c)
    {
        string where = c.Lead is { } lead ? $"in {lead}" + (CrashModules.Meaning(lead) is { } m ? $" ({m})" : " (not in the Companion's list yet)") : "in an unrecorded place";
        string after = c.LastActions.Count > 0 ? $", right after you {c.LastActions[^1]}" : "";
        return $"SOLIDWORKS {VersionName(c.Version)} crashed {where}{after}. In plain words: {CrashModules.ErrorMeaning(c.Error)}.";
    }

    /// <summary>A repeating pattern across crashes (same module two or more times), or null.</summary>
    public static (string Title, string Why, string Try)? Pattern(IReadOnlyList<CrashRecord> crashes)
    {
        var top = crashes.Where(c => c.Lead is not null).GroupBy(c => c.Lead!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (top is null || top.Count() < 2) return null;
        string module = top.Key;
        string meaning = CrashModules.Meaning(module) ?? "a module not in the Companion's list yet";
        var actions = top.SelectMany(c => c.LastActions.TakeLast(1)).GroupBy(a => a).OrderByDescending(g => g.Count()).FirstOrDefault();
        string when = actions is not null && actions.Count() >= 2 ? $" Most of them came right after you {actions.Key}." : "";
        string tip = module.ToLowerInvariant() switch
        {
            // Written as separate sentences: the fix walkthrough shows each one as a numbered step.
            var x when x.StartsWith("pskernel") => "Note which part was open when it crashed (geometry crashes usually follow one model). " +
                                                   "Open that part and run Tools > Evaluate > Check on it. Rebuild it fully with Ctrl+Q. " +
                                                   "If it keeps crashing, send the part and the crash report to SOLIDWORKS support.",
            var x when x.StartsWith("nv") || x.StartsWith("ati") || x.StartsWith("ig") => "Look up your graphics card on the SOLIDWORKS hardware " +
                                                   "certification list. Install the driver version it lists for your SOLIDWORKS release. Restart the PC.",
            "clr" or "mscorlib" or "coreclr" => "Open Tools > Add-Ins and untick the add-ins (both columns). Restart SOLIDWORKS and work as usual. " +
                                                "If the crashes stop, switch the add-ins back on one at a time to find the one causing it.",
            _ => "Open the crashes section (advanced) to see the reports. Send them to SOLIDWORKS support: the same module each time is a strong lead for them.",
        };
        string plainPlace = CrashModules.Meaning(module) is { } known ? $"{module}: {Short(known)}" : module;
        return ($"SOLIDWORKS keeps crashing in the same place ({top.Count()} times, {plainPlace})", $"{Capital(meaning)}.{when}", tip);
    }

    /// <summary>The report the Companion saves: SOLIDWORKS's own record plus what the Companion saw. Local only.</summary>
    public static string Report(CrashRecord? c, string howItEnded, IReadOnlyList<string> companionSaw, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Companion for SOLIDWORKS crash report, {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Contains file and folder names from this PC: share it only with people you'd trust with them.");
        sb.AppendLine();
        sb.AppendLine("HOW IT ENDED");
        sb.AppendLine(howItEnded);
        sb.AppendLine();
        if (c is not null)
        {
            sb.AppendLine("WHAT SOLIDWORKS RECORDED");
            sb.AppendLine(Summary(c));
            sb.AppendLine($"Error: {c.Error ?? "not logged"}");
            sb.AppendLine($"Call chain (innermost first): {(c.Modules.Count == 0 ? "not logged" : string.Join(" > ", c.Modules.Take(8)))}");
            foreach (var m in c.Modules.Take(8).Where(m => CrashModules.Meaning(m) is not null))
                sb.AppendLine($"  {m}: {CrashModules.Meaning(m)}");
            if (c.LastActions.Count > 0) sb.AppendLine($"Last things you did: {string.Join(", then ", c.LastActions)}");
            if (c.AddinCalls.Count > 0) sb.AppendLine($"Recent calls from add-ins or macros into SOLIDWORKS: {string.Join(", ", c.AddinCalls)}");
            if (c.MemoryBytes is { } mem) sb.AppendLine($"SOLIDWORKS memory just before: {mem / 1073741824.0:0.0} GB");
            if (c.GdiUsed is { } g) sb.AppendLine($"Drawing objects (GDI) just before: {g:N0} of {c.GdiLimit:N0}");
            if (c.SessionSeconds is { } s) sb.AppendLine($"Session length: {s / 60} min");
            sb.AppendLine($"SOLIDWORKS's own record: {c.Folder}{(c.HasDump ? " (includes a crash dump for SOLIDWORKS support)" : "")}");
            sb.AppendLine();
        }
        if (companionSaw.Count > 0)
        {
            sb.AppendLine("WHAT THE COMPANION SAW BEFORE IT ENDED");
            foreach (var l in companionSaw) sb.AppendLine("  " + l);
        }
        return sb.ToString();
    }

    public static string VersionName(string fileVersion)
    {
        var p = fileVersion.Split('.');
        return p.Length >= 3 && int.TryParse(p[0], out int major) ? $"{1992 + major} SP{int.Parse(p[1])}.{int.Parse(p[2])}" : fileVersion;
    }

    /// <summary>"Parasolid, the geometry engine that..." -> "Parasolid".</summary>
    private static string Short(string meaning) => meaning.Split(',')[0].Replace("the ", "");

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
