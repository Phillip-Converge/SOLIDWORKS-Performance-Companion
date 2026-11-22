using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SWCompanion.Core.Cpu;

/// <summary>
/// Measures CPU use for the whole PC, each core, and SOLIDWORKS's threads.
/// READ ONLY: it only reads Windows' running CPU-time counters and compares
/// each reading with the previous one. Needs no administrator rights.
/// Call <see cref="Sample"/> on a regular interval (about once a second).
/// </summary>
public sealed class CpuSampler : IDisposable
{
    private readonly int _coreCount = Environment.ProcessorCount;
    private CoreTimes[]? _prevCores;

    private int? _swPid;
    private Process? _swProcess;
    private TimeSpan _prevSwCpu;
    private Dictionary<int, TimeSpan> _prevThreadCpu = new();
    private long _prevStamp;

    /// <summary>
    /// Takes a reading. Returns null on the first call (there is nothing to
    /// compare with yet) and after SOLIDWORKS changes.
    /// </summary>
    /// <param name="solidWorksPid">Process ID of SOLIDWORKS, or null if not running.</param>
    public CpuSnapshot? Sample(int? solidWorksPid)
    {
        long stamp = Stopwatch.GetTimestamp();
        double elapsedSeconds = _prevStamp == 0 ? 0 : (stamp - _prevStamp) / (double)Stopwatch.Frequency;
        _prevStamp = stamp;

        var cores = ReadCoreTimes();
        var corePercent = _prevCores is null ? null : CoreUsage(_prevCores, cores);
        _prevCores = cores;

        var sw = SampleSolidWorks(solidWorksPid, elapsedSeconds);

        if (corePercent is null || elapsedSeconds <= 0) return null;
        return new CpuSnapshot(corePercent.Average(), corePercent, sw);
    }

    private SolidWorksCpu? SampleSolidWorks(int? pid, double elapsedSeconds)
    {
        if (pid != _swPid)
        {
            // SOLIDWORKS started, stopped or restarted: start fresh.
            _swProcess?.Dispose();
            _swProcess = null;
            _prevThreadCpu = new();
            _swPid = pid;
            if (pid is null) return null;
            try
            {
                _swProcess = Process.GetProcessById(pid.Value);
                _prevSwCpu = _swProcess.TotalProcessorTime;
            }
            catch
            {
                _swProcess = null;
            }
            ReadThreads(); // prime per-thread readings
            return null;
        }

        if (_swProcess is null || elapsedSeconds <= 0) return null;

        TimeSpan cpu;
        try
        {
            _swProcess.Refresh();
            if (_swProcess.HasExited) return null;
            cpu = _swProcess.TotalProcessorTime;
        }
        catch
        {
            return null;
        }

        double coresWorth = Math.Max(0, (cpu - _prevSwCpu).TotalSeconds / elapsedSeconds);
        _prevSwCpu = cpu;

        var (busyThreads, topThread, visible) = ThreadUsage(elapsedSeconds);
        return new SolidWorksCpu(
            coresWorth,
            Math.Min(100, coresWorth / _coreCount * 100),
            busyThreads,
            topThread,
            visible);
    }

    private (int Busy, double Top, bool Visible) ThreadUsage(double elapsedSeconds)
    {
        var previous = _prevThreadCpu;
        var current = ReadThreads();
        if (current is null) return (0, 0, false);

        int busy = 0;
        double top = 0;
        foreach (var (id, time) in current)
        {
            if (!previous.TryGetValue(id, out var before)) continue; // new thread, no baseline yet
            double pct = Math.Clamp((time - before).TotalSeconds / elapsedSeconds * 100, 0, 100);
            if (pct >= 50) busy++;
            if (pct > top) top = pct;
        }
        return (busy, top, true);
    }

    /// <summary>Reads CPU time used so far by each SOLIDWORKS thread. Null if Windows refuses.</summary>
    private Dictionary<int, TimeSpan>? ReadThreads()
    {
        if (_swProcess is null) return null;
        var result = new Dictionary<int, TimeSpan>();
        int refused = 0, total = 0;
        try
        {
            foreach (ProcessThread t in _swProcess.Threads)
            {
                total++;
                try { result[t.Id] = t.TotalProcessorTime; }
                catch { refused++; } // thread ended, or no permission
                finally { t.Dispose(); }
            }
        }
        catch
        {
            return null;
        }

        if (total > 0 && refused == total) return null;
        _prevThreadCpu = result;
        return result;
    }

    // ---- whole PC and per core ----

    private static double[] CoreUsage(CoreTimes[] before, CoreTimes[] after)
    {
        var pct = new double[after.Length];
        for (int i = 0; i < after.Length && i < before.Length; i++)
        {
            long total = (after[i].Kernel + after[i].User) - (before[i].Kernel + before[i].User);
            long idle = after[i].Idle - before[i].Idle;
            pct[i] = total <= 0 ? 0 : Math.Clamp((total - idle) * 100.0 / total, 0, 100);
        }
        return pct;
    }

    private CoreTimes[] ReadCoreTimes()
    {
        int size = Marshal.SizeOf<SystemProcessorPerformanceInformation>();
        var buffer = new SystemProcessorPerformanceInformation[_coreCount];
        int status = NtQuerySystemInformation(SystemProcessorPerformanceInformationClass, buffer, size * _coreCount, out _);
        if (status != 0)
            throw new InvalidOperationException($"Windows did not return per-core CPU times (status 0x{status:X8}).");

        return buffer.Select(b => new CoreTimes(b.IdleTime, b.KernelTime, b.UserTime)).ToArray();
    }

    private readonly record struct CoreTimes(long Idle, long Kernel, long User);

    private const int SystemProcessorPerformanceInformationClass = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessorPerformanceInformation
    {
        public long IdleTime;   // included in KernelTime
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int infoClass,
        [Out] SystemProcessorPerformanceInformation[] info,
        int length,
        out int returnLength);

    public void Dispose() => _swProcess?.Dispose();
}
