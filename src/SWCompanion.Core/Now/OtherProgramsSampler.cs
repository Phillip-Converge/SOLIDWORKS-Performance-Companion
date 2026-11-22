using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SWCompanion.Core.Now;

/// <param name="CoresWorth">CPU used since the previous reading, in whole cores.</param>
/// <param name="IoBytesPerSec">Everything it read and wrote per second (disk and network together).</param>
public sealed record ProgramUse(string Name, int Pid, double CoresWorth, double IoBytesPerSec);

public sealed record OtherPrograms(IReadOnlyList<ProgramUse> TopCpu, IReadOnlyList<ProgramUse> TopIo, double OtherCoresTotal);

/// <summary>
/// Finds which other programs are busiest, so the app can say "another program is
/// hogging the processor or disk" and name it. READ ONLY, query-level access only.
/// </summary>
public sealed class OtherProgramsSampler
{
    private Dictionary<int, (string Name, long CpuTicks, ulong Io)> _previous = new();
    private long _previousStamp;

    /// <param name="excludePids">Programs to leave out (SOLIDWORKS and this app).</param>
    /// <returns>Null on the first call (nothing to compare with yet).</returns>
    public OtherPrograms? Sample(IReadOnlySet<int> excludePids)
    {
        long stamp = Stopwatch.GetTimestamp();
        double seconds = _previousStamp == 0 ? 0 : (stamp - _previousStamp) / (double)Stopwatch.Frequency;
        _previousStamp = stamp;

        var current = new Dictionary<int, (string, long, ulong)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue;
                var h = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (!GetProcessTimes(h, out _, out _, out long kernel, out long user)) continue;
                    ulong io = GetProcessIoCounters(h, out var c) ? c.ReadBytes + c.WriteBytes + c.OtherBytes : 0;
                    current[p.Id] = (p.ProcessName, kernel + user, io);
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }

        var before = _previous;
        _previous = current;
        if (seconds <= 0) return null;

        var uses = current
            .Where(kv => !excludePids.Contains(kv.Key) && before.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var b = before[kv.Key];
                double cores = Math.Max(0, (kv.Value.Item2 - b.CpuTicks) / 1e7 / seconds); // times are in 100 ns units
                double io = kv.Value.Item3 >= b.Io ? (kv.Value.Item3 - b.Io) / seconds : 0;
                return new ProgramUse(kv.Value.Item1, kv.Key, cores, io);
            })
            .ToList();

        return new OtherPrograms(
            uses.OrderByDescending(u => u.CoresWorth).Take(3).ToList(),
            uses.OrderByDescending(u => u.IoBytesPerSec).Take(3).ToList(),
            uses.Sum(u => u.CoresWorth));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
}
