using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SWCompanion.Core.Checkup;

namespace SWCompanion.Core.Antivirus;

/// <summary>
/// Watches Windows Defender's scanning program (MsMpEng.exe) from outside: how much processor it uses,
/// second by second. Works without administrator rights (query-level access only), so the Companion can
/// say "Defender was busy while that opened" even when file tracing is off. Read only.
/// </summary>
public sealed class DefenderSampler
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(20);
    private readonly List<(DateTime Time, double CpuSeconds)> _readings = new();

    /// <summary>Processor use right now, in cores (0.5 = half a core). Null until there are two readings.</summary>
    public double? CoresNow { get; private set; }

    /// <summary>
    /// Takes a reading. Call every second or two. Windows won't let an ordinary program open Defender's process
    /// (it's protected), so this asks Windows for its list of every program's processor time instead, the way
    /// Task Manager does. That needs no administrator rights and opens nothing.
    /// </summary>
    public void Sample(DateTime now)
    {
        if (ProcessTimes.CpuSeconds("MsMpEng.exe") is not { } cpu) { CoresNow = null; return; }
        Add(now, cpu);
    }

    /// <summary>Adds a reading (also used by tests).</summary>
    public void Add(DateTime now, double cumulativeCpuSeconds)
    {
        if (_readings.Count > 0)
        {
            var (t, c) = _readings[^1];
            double secs = (now - t).TotalSeconds;
            CoresNow = secs > 0 && cumulativeCpuSeconds >= c ? (cumulativeCpuSeconds - c) / secs : null;
        }
        _readings.Add((now, cumulativeCpuSeconds));
        while (_readings.Count > 0 && now - _readings[0].Time > KeepFor) _readings.RemoveAt(0);
    }

    /// <summary>How many seconds of processor Defender used between two moments (null if we weren't watching then).</summary>
    public double? CpuSecondsBetween(DateTime from, DateTime to)
    {
        if (_readings.Count < 2 || from < _readings[0].Time.AddSeconds(-3)) return null;
        double At(DateTime when)
        {
            // The last reading at or before the moment (readings are a second or two apart).
            var r = _readings.LastOrDefault(x => x.Time <= when);
            return r == default ? _readings[0].CpuSeconds : r.CpuSeconds;
        }
        return Math.Max(0, At(to.AddSeconds(2)) - At(from));
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
}

/// <summary>
/// Every program's processor time from Windows' own process list (NtQuerySystemInformation, the source Task
/// Manager uses). Works for protected programs like Defender without opening them or administrator rights.
/// </summary>
public static class ProcessTimes
{
    /// <summary>Total processor seconds used so far by all running copies of this program (e.g. "MsMpEng.exe"), or null if none.</summary>
    public static double? CpuSeconds(string imageName)
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 4; attempt++, size *= 2)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size, needed + 65536) / 2; continue; }
                if (status != 0) return null;
                double? total = null;
                long offset = 0;
                while (true)
                {
                    IntPtr entry = buffer + (int)offset;
                    // 64-bit layout: UserTime at 40, KernelTime at 48, ImageName (length at 56, text pointer at 64).
                    long user = Marshal.ReadInt64(entry, 40), kernel = Marshal.ReadInt64(entry, 48);
                    ushort length = (ushort)Marshal.ReadInt16(entry, 56);
                    IntPtr text = Marshal.ReadIntPtr(entry, 64);
                    if (text != IntPtr.Zero && length > 0 &&
                        string.Equals(Marshal.PtrToStringUni(text, length / 2), imageName, StringComparison.OrdinalIgnoreCase))
                        total = (total ?? 0) + (user + kernel) / 1e7;
                    int next = Marshal.ReadInt32(entry, 0);
                    if (next == 0) break;
                    offset += next;
                }
                return total;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    /// <summary>
    /// The programs with the most memory reserved ("commit size"), all copies of each added together, biggest first.
    /// From the same process list, so no administrator rights and nothing is opened.
    /// </summary>
    public static IReadOnlyList<Memory.MemoryUser> TopReserved(int count)
    {
        var totals = new Dictionary<string, (ulong Bytes, int Copies)>(StringComparer.OrdinalIgnoreCase);
        if (!Walk(entry =>
            {
                ushort length = (ushort)Marshal.ReadInt16(entry, 56);
                IntPtr text = Marshal.ReadIntPtr(entry, 64);
                string name = text != IntPtr.Zero && length > 0 ? Marshal.PtrToStringUni(text, length / 2) : "";
                if (name.Length == 0) return; // the idle process
                ulong reserved = (ulong)Marshal.ReadInt64(entry, 184); // 64-bit layout: PagefileUsage (private commit) at 184
                totals[name] = totals.TryGetValue(name, out var t) ? (t.Bytes + reserved, t.Copies + 1) : (reserved, 1);
            })) return [];
        return totals.OrderByDescending(kv => kv.Value.Bytes).Take(count)
                     .Select(kv => new Memory.MemoryUser(kv.Key, kv.Value.Bytes, kv.Value.Copies)).ToList();
    }

    /// <summary>Calls <paramref name="visit"/> with each entry of Windows' process list. False if the list couldn't be read.</summary>
    private static bool Walk(Action<IntPtr> visit)
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 4; attempt++, size *= 2)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out int needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size, needed + 65536) / 2; continue; }
                if (status != 0) return false;
                long offset = 0;
                while (true)
                {
                    IntPtr entry = buffer + (int)offset;
                    visit(entry);
                    int next = Marshal.ReadInt32(entry, 0);
                    if (next == 0) break;
                    offset += next;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return false;
    }

    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
}

/// <summary>Defender's exclusion lists, if Windows lets us see them (normally administrators only).</summary>
public sealed record DefenderExclusions(bool Readable, IReadOnlyList<string> Paths, IReadOnlyList<string> Processes, bool DefenderPresent)
{
    public static DefenderExclusions Read()
    {
        bool present = Process.GetProcessesByName("MsMpEng").Length > 0;
        try
        {
            using var paths = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths");
            using var procs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Processes");
            if (paths is null && procs is null) return new(false, [], [], present);
            return new(true, paths?.GetValueNames() ?? [], procs?.GetValueNames() ?? [], present);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, [], [], present);
        }
    }

    /// <summary>Is this folder (or a folder above it) on the exclusion list?</summary>
    public bool Covers(string folder)
    {
        string f = Normal(folder);
        return Paths.Any(p => f.StartsWith(Normal(p), StringComparison.OrdinalIgnoreCase));
    }

    public bool CoversProcess(string exeName) => Processes.Any(p => Path.GetFileName(p).Equals(exeName, StringComparison.OrdinalIgnoreCase));

    private static string Normal(string p) => Environment.ExpandEnvironmentVariables(p).TrimEnd('\\') + "\\";
}

/// <summary>Plain-English checkup lines and live notes about Defender.</summary>
public static class DefenderExplainer
{
    public const string Source = "Microsoft documentation on Microsoft Defender Antivirus exclusions and general SOLIDWORKS support practice";
    private const string Plain = "Think of it like this: Defender is a security guard who opens every box before it reaches your bench. " +
                                 "SOLIDWORKS files are big boxes, and an assembly can be hundreds of them, so the guard can hold up the line.";
    /// <summary>Defender using at least this much processor while SOLIDWORKS is busy is worth mentioning.</summary>
    public const double BusyCores = 0.5;

    /// <param name="folders">Folders SOLIDWORKS reads a lot ("SOLIDWORKS program folder" -> path, "vault ExampleVault" -> path).</param>
    public static CheckResult Exclusions(DefenderExclusions x, IReadOnlyList<(string What, string Path)> folders)
    {
        if (!x.DefenderPresent)
            return new("defender-exclusions", "Defender exclusions", CheckStatus.Good,
                "Windows Defender isn't running on this PC, so it isn't scanning SOLIDWORKS's files.", "", Source, Plain, "Windows");
        if (!x.Readable)
            return new("defender-exclusions", "Defender exclusions", CheckStatus.Unknown,
                "Windows only shows Defender's exclusion list to administrators, so the Companion can't check it now. " +
                "Run the Companion as administrator (files section) to check it. Defender's live activity is still watched.",
                "", Source, Plain, "Windows");

        var missing = folders.Where(f => !x.Covers(f.Path)).Select(f => f.What).ToList();
        bool exe = x.CoversProcess("SLDWORKS.exe");
        string found = $"Defender has {x.Paths.Count} excluded folder{(x.Paths.Count == 1 ? "" : "s")} and {x.Processes.Count} excluded program{(x.Processes.Count == 1 ? "" : "s")}.";
        if (missing.Count == 0)
            return new("defender-exclusions", "Defender exclusions", CheckStatus.Good,
                $"{found} The folders SOLIDWORKS reads most are excluded.", "", Source, Plain, "Windows");
        return new("defender-exclusions", "Defender exclusions", CheckStatus.Look,
            $"{found} Not excluded: {string.Join(", ", missing)}." + (exe ? " SLDWORKS.exe itself is excluded." : ""),
            "Exclusions are a security decision, so this is one to agree with IT. Microsoft documents how to add folder exclusions " +
            "(Windows Security > Virus & threat protection > Manage settings > Exclusions). Commonly considered: the SOLIDWORKS " +
            "program folder and the PDM local vault folder. Only exclude folders you trust.",
            Source, Plain, "Windows");
    }

    /// <summary>"Windows Defender used 3.1 s of processor while this ran." for a slow action, or null if it was quiet.</summary>
    public static string? DuringAction(double? cpuSeconds, double? scanSeconds, int scannedFiles)
    {
        var parts = new List<string>();
        if (scannedFiles > 0 && scanSeconds is { } s && s >= 0.3)
            parts.Add($"Windows Defender scanned {scannedFiles} file{(scannedFiles == 1 ? "" : "s")} during it, about {s:0.0} s of scanning");
        if (cpuSeconds is { } c && c >= 1)
            parts.Add($"Defender used {c:0.0} s of processor while it ran");
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }
}
