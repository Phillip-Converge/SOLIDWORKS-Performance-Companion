using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SWCompanion.Core.Io;

/// <summary>
/// Reads disk and network activity for the PC, and how much SOLIDWORKS reads and
/// writes. READ ONLY, no administrator rights needed. Windows doesn't split
/// SOLIDWORKS's reads into "from disk" and "over the network" without detailed
/// tracing (step 7), so its figures are the two together.
/// </summary>
public sealed class IoSampler
{
    private readonly CounterRates _disk = new("PhysicalDisk",
        "% Idle Time", "Avg. Disk sec/Transfer", "Disk Read Bytes/sec", "Disk Write Bytes/sec");
    private readonly CounterRates _net = new("Network Interface",
        "Bytes Received/sec", "Bytes Sent/sec", "Current Bandwidth");

    private int? _swPid;
    private IoCounters? _swPrevious;
    private long _swPreviousStamp;

    /// <returns>Null on the first call, which only records a starting point.</returns>
    public IoSnapshot? Sample(int? solidWorksPid)
    {
        var disk = _disk.Read();
        var net = _net.Read();
        var sw = SampleSolidWorks(solidWorksPid);

        if (disk["% Idle Time"].Count == 0 && net["Bytes Received/sec"].Count == 0) return null;
        return Build(disk, net, sw);
    }

    /// <summary>Turns counter values into readings. Separate so it can be tested with made-up numbers.</summary>
    public static IoSnapshot Build(
        IReadOnlyDictionary<string, Dictionary<string, double>> disk,
        IReadOnlyDictionary<string, Dictionary<string, double>> net,
        SolidWorksIo? sw)
    {
        var disks = disk["% Idle Time"].Keys
            .Where(name => name != "_Total")
            .Select(name => new DiskReading(
                name,
                Math.Clamp(100 - disk["% Idle Time"][name], 0, 100),
                disk["Avg. Disk sec/Transfer"].GetValueOrDefault(name) * 1000,
                disk["Disk Read Bytes/sec"].GetValueOrDefault(name),
                disk["Disk Write Bytes/sec"].GetValueOrDefault(name)))
            .OrderBy(d => d.Name)
            .ToList();

        var networks = net["Bytes Received/sec"].Keys
            .Select(name => new NetworkReading(
                name,
                net["Bytes Received/sec"][name],
                net["Bytes Sent/sec"].GetValueOrDefault(name),
                net["Current Bandwidth"].GetValueOrDefault(name)))
            .OrderByDescending(n => n.ReceiveBytesPerSec + n.SendBytesPerSec)
            .ToList();

        return new IoSnapshot(disks, networks, sw);
    }

    private SolidWorksIo? SampleSolidWorks(int? pid)
    {
        if (pid != _swPid)
        {
            _swPid = pid;
            _swPrevious = null;
        }
        if (pid is null) return null;

        var now = ReadIoCounters(pid.Value);
        long stamp = Stopwatch.GetTimestamp();
        if (now is null) return null;

        var before = _swPrevious;
        double seconds = (stamp - _swPreviousStamp) / (double)Stopwatch.Frequency;
        _swPrevious = now;
        _swPreviousStamp = stamp;
        if (before is null || seconds <= 0) return new SolidWorksIo(0, 0, 0, 0, now.Value.ReadBytes, now.Value.WriteBytes);

        return new SolidWorksIo(
            (now.Value.ReadBytes - before.Value.ReadBytes) / seconds,
            (now.Value.WriteBytes - before.Value.WriteBytes) / seconds,
            (now.Value.ReadOps - before.Value.ReadOps) / seconds,
            (now.Value.WriteOps - before.Value.WriteOps) / seconds,
            now.Value.ReadBytes,
            now.Value.WriteBytes);
    }

    /// <summary>Asks Windows for a process's running I/O totals, with query-only access.</summary>
    private static IoCounters? ReadIoCounters(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetProcessIoCounters(h, out var c) ? c : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
