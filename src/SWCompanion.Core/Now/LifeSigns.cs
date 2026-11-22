using System.Runtime.InteropServices;
using SWCompanion.Core.Explain;
using SWCompanion.Core.Io;

namespace SWCompanion.Core.Now;

/// <summary>Running totals read from Windows at one moment.</summary>
/// <param name="MainThreadCpu">CPU time used so far by the thread that runs SOLIDWORKS's window (null if unknown).</param>
/// <param name="ProcessCpu">CPU time used so far by all of SOLIDWORKS.</param>
/// <param name="IoBytes">Bytes read and written so far by SOLIDWORKS (disk and network together).</param>
public sealed record LifeSample(DateTime Time, TimeSpan? MainThreadCpu, TimeSpan ProcessCpu, ulong IoBytes);

/// <summary>
/// Reads SOLIDWORKS's running totals. READ ONLY: query-level access to the process
/// and to its main thread; nothing is paused or changed.
/// </summary>
public static class LifeSignsSampler
{
    public static LifeSample? Sample(int pid, IntPtr mainWindow, DateTime now)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(process, out _, out _, out long kernel, out long user)) return null;
            ulong io = GetProcessIoCounters(process, out var c) ? c.ReadBytes + c.WriteBytes + c.OtherBytes : 0;
            return new LifeSample(now, MainThreadCpu(mainWindow), TimeSpan.FromTicks(kernel + user), io);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>CPU time of the thread that owns the window (SOLIDWORKS's main thread).</summary>
    private static TimeSpan? MainThreadCpu(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        uint threadId = GetWindowThreadProcessId(window, out _);
        if (threadId == 0) return null;
        var thread = OpenThread(ThreadQueryLimitedInformation, false, threadId);
        if (thread == IntPtr.Zero) return null;
        try
        {
            return GetThreadTimes(thread, out _, out _, out long kernel, out long user) ? TimeSpan.FromTicks(kernel + user) : null;
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ThreadQueryLimitedInformation = 0x0800;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

public enum Verdict
{
    /// <summary>Not frozen, or not enough readings yet.</summary>
    None,
    /// <summary>The main thread is doing work: SOLIDWORKS is computing what you asked for.</summary>
    Working,
    /// <summary>The main thread is waiting, but other parts of SOLIDWORKS are busy or files and data are moving.</summary>
    Background,
    /// <summary>No sign of life recently, but not for long enough to call it stuck.</summary>
    Quiet,
    /// <summary>No sign of life for <see cref="LifeSigns.ProbablyStuckAfter"/>.</summary>
    ProbablyStuck,
    /// <summary>No sign of life for <see cref="LifeSigns.StuckAfter"/>.</summary>
    Stuck,
}

/// <param name="Title">Short verdict, e.g. "Still working: keep waiting".</param>
/// <param name="Evidence">What was measured.</param>
/// <param name="LastSignOfLife">When SOLIDWORKS last did anything measurable.</param>
/// <param name="Advice">What to do now, in plain English (empty while it's still working).</param>
public sealed record VerdictReport(Verdict Verdict, string Title, string Evidence, DateTime? LastSignOfLife, string Advice);

/// <summary>
/// Follows SOLIDWORKS's running totals and answers the question that matters during a
/// freeze: is it still making progress, or has it stopped? The app never ends
/// SOLIDWORKS; this only tells you when ending it is reasonable.
/// </summary>
public sealed class LifeSigns
{
    /// <summary>The main thread used at least this much CPU since the last reading: it's working.</summary>
    public static readonly TimeSpan MainThreadMin = TimeSpan.FromMilliseconds(10);
    /// <summary>All of SOLIDWORKS used at least this much CPU since the last reading: something's busy.</summary>
    public static readonly TimeSpan ProcessMin = TimeSpan.FromMilliseconds(30);
    /// <summary>At least this many bytes read or written since the last reading: data is moving.</summary>
    public const ulong IoMin = 16 * 1024;
    /// <summary>Activity this recent counts as "now".</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ProbablyStuckAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    private LifeSample? _previous;
    private DateTime? _lastMain, _lastCpu, _lastIo, _watchingSince;
    private double _recentIoRate;

    public DateTime? LastMainActivity => _lastMain;

    /// <summary>Last measured activity; if none yet, when watching began (so the silence clock never starts earlier than that).</summary>
    public DateTime? LastSignOfLife => Max(_watchingSince, Max(_lastMain, Max(_lastCpu, _lastIo)));

    /// <summary>Starts fresh (SOLIDWORKS started or restarted).</summary>
    public void Reset()
    {
        _previous = null;
        _lastMain = _lastCpu = _lastIo = _watchingSince = null;
    }

    public void Add(LifeSample s)
    {
        var before = _previous;
        _previous = s;
        if (before is null)
        {
            // The first reading only starts the clock; with nothing to compare, it isn't evidence of work.
            _watchingSince = s.Time;
            return;
        }

        if (s.MainThreadCpu is { } m && before.MainThreadCpu is { } bm && m - bm >= MainThreadMin) _lastMain = s.Time;
        if (s.ProcessCpu - before.ProcessCpu >= ProcessMin) _lastCpu = s.Time;
        ulong io = s.IoBytes >= before.IoBytes ? s.IoBytes - before.IoBytes : 0;
        double seconds = Math.Max(0.001, (s.Time - before.Time).TotalSeconds);
        _recentIoRate = io / seconds;
        if (io >= IoMin) _lastIo = s.Time;
    }

    /// <summary>The verdict for right now. Only meaningful while SOLIDWORKS is not responding.</summary>
    public VerdictReport Evaluate(DateTime now, bool frozen)
    {
        if (!frozen || _previous is null)
            return new VerdictReport(Verdict.None, "", "", LastSignOfLife, "");

        bool main = _lastMain is { } a && now - a <= Recent;
        bool cpu = _lastCpu is { } b && now - b <= Recent;
        bool io = _lastIo is { } c && now - c <= Recent;
        var quietFor = LastSignOfLife is { } last ? now - last : TimeSpan.Zero;

        if (main)
            return new VerdictReport(Verdict.Working, "Still working: keep waiting",
                "SOLIDWORKS's main task is busy right now, working through what you asked for. The window can't redraw until it " +
                "finishes, which is why it says Not Responding. Think of it as " + Analogies.OneCoreBusy,
                LastSignOfLife, "");

        if (cpu || io)
            return new VerdictReport(Verdict.Background, "Still working in the background: keep waiting",
                "The main window is waiting, but other parts of SOLIDWORKS are still " +
                (cpu && io ? "busy and moving data" : cpu ? "busy" : $"moving data ({IoExplainer.Rate(_recentIoRate)})") +
                ", for example loading files or waiting for a server that is still sending replies. " +
                "Think of it as a machinist waiting at the counter while parts are still being brought out.",
                LastSignOfLife, "");

        if (quietFor >= StuckAfter)
            return new VerdictReport(Verdict.Stuck, $"Stuck: no sign of life for {NowEngine.Seconds(quietFor)}",
                "SOLIDWORKS hasn't used the processor, read or written anything, or moved any data in that time. It's very " +
                "unlikely to recover on its own. Think of it as a machinist who has stopped, put the tools down and is staring at the wall.",
                LastSignOfLife, BeforeYouEnd);

        if (quietFor >= ProbablyStuckAfter)
            return new VerdictReport(Verdict.ProbablyStuck, $"Probably stuck: no sign of life for {NowEngine.Seconds(quietFor)}",
                "SOLIDWORKS hasn't done any measurable work in that time. It may still be waiting for a slow reply that never " +
                $"comes. The app will call it stuck at {NowEngine.Seconds(StuckAfter)} of silence.",
                LastSignOfLife, BeforeYouEnd);

        return new VerdictReport(Verdict.Quiet, $"Quiet for {NowEngine.Seconds(quietFor)}: give it a little longer",
            "SOLIDWORKS isn't doing measurable work at this moment. Short pauses are normal while it waits for a reply. " +
            $"If it stays silent for {NowEngine.Seconds(ProbablyStuckAfter)}, the app will say it's probably stuck.",
            LastSignOfLife, "");
    }

    /// <summary>Plain-English steps before ending SOLIDWORKS. The app itself never ends it.</summary>
    public const string BeforeYouEnd =
        "Before you end SOLIDWORKS: (1) Check for a message box hidden behind other windows (the app lists one if it finds it). " +
        "(2) Anything changed since your last save will be lost. If Auto-recover is on (Tools > Options > Backup/Recover), " +
        "SOLIDWORKS may offer recovered documents when it starts again. (3) To end it: if SOLIDWORKS is showing its \"busy " +
        "running a command\" box, choose Close SOLIDWORKS; otherwise open Task Manager (Ctrl+Shift+Esc), Details tab, select " +
        "SLDWORKS.exe, End task. This app never ends SOLIDWORKS for you.";

    /// <summary>Which button to pick in SOLIDWORKS's "busy running a command" box, while it's still making progress.</summary>
    public const string WhileWorking =
        "If SOLIDWORKS shows \"SOLIDWORKS is busy running a command\", choose \"Wait for the command to finish\": it's still " +
        "making progress. That box's moving bar most likely just shows the box is alive, not how far the command has got.";

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
