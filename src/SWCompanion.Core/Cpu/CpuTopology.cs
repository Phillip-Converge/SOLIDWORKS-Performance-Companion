using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SWCompanion.Core.Cpu;

/// <param name="Index">Slot number as shown in the app (00, 01, ...).</param>
/// <param name="PhysicalCore">Which physical core it belongs to. Two slots with the
/// same number are a hyper-threaded pair sharing one physical core.</param>
/// <param name="IsPerformance">True for a performance core, false for an efficiency core.
/// On processors with only one kind of core, every slot is true.</param>
public sealed record LogicalCore(int Index, int PhysicalCore, bool IsPerformance);

/// <summary>
/// What kinds of cores this PC has. READ ONLY: asks Windows once at start-up.
/// </summary>
public sealed class CpuTopology
{
    public string ProcessorName { get; }
    public IReadOnlyList<LogicalCore> Cores { get; }

    /// <summary>True if the processor mixes performance and efficiency cores.</summary>
    public bool IsHybrid { get; }

    public CpuTopology(string processorName, IReadOnlyList<LogicalCore> cores, bool isHybrid)
    {
        ProcessorName = processorName;
        Cores = cores;
        IsHybrid = isHybrid;
    }

    public IReadOnlyList<LogicalCore> PerformanceSlots => Cores.Where(c => c.IsPerformance).ToList();
    public IReadOnlyList<LogicalCore> EfficiencySlots => Cores.Where(c => !c.IsPerformance).ToList();
    public int PerformancePhysicalCount => PerformanceSlots.Select(c => c.PhysicalCore).Distinct().Count();
    public int EfficiencyPhysicalCount => EfficiencySlots.Select(c => c.PhysicalCore).Distinct().Count();

    /// <summary>True if some physical cores appear as two slots (hyper-threading).</summary>
    public bool HasPairs => Cores.GroupBy(c => c.PhysicalCore).Any(g => g.Count() > 1);

    /// <summary>The other slot sharing the same physical core, if any.</summary>
    public LogicalCore? PairOf(int index)
    {
        var me = Cores.FirstOrDefault(c => c.Index == index);
        return me is null ? null : Cores.FirstOrDefault(c => c.PhysicalCore == me.PhysicalCore && c.Index != index);
    }

    /// <summary>"00–11" for a continuous run, otherwise "00, 03, 07".</summary>
    public static string SlotRange(IEnumerable<LogicalCore> slots)
    {
        var idx = slots.Select(s => s.Index).OrderBy(i => i).ToList();
        if (idx.Count == 0) return "none";
        bool continuous = idx.Last() - idx.First() == idx.Count - 1;
        return continuous && idx.Count > 1
            ? $"{idx.First():00}–{idx.Last():00}"
            : string.Join(", ", idx.Select(i => i.ToString("00")));
    }

    // ---- reading from Windows ----

    public static CpuTopology Read()
    {
        string name = ReadProcessorName();
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out int length, IntPtr.Zero, 0);
            if (length > 0)
            {
                var buffer = new byte[length];
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    if (GetSystemCpuSetInformation(pin.AddrOfPinnedObject(), length, out length, IntPtr.Zero, 0))
                        return FromCpuSetBuffer(buffer.AsSpan(0, length), name);
                }
                finally
                {
                    pin.Free();
                }
            }
        }
        catch
        {
            // fall through to the simple version below
        }

        // Windows didn't say: treat every slot as one ordinary core.
        var simple = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(i => new LogicalCore(i, i, true)).ToList();
        return new CpuTopology(name, simple, isHybrid: false);
    }

    /// <summary>Decodes Windows' CPU-set list. Separate from <see cref="Read"/> so it can be tested.</summary>
    public static CpuTopology FromCpuSetBuffer(ReadOnlySpan<byte> buffer, string processorName)
    {
        var raw = new List<(int Index, int Core, int Efficiency)>();
        int offset = 0;
        while (offset + 20 <= buffer.Length)
        {
            int size = BitConverter.ToInt32(buffer.Slice(offset, 4));
            if (size <= 0) break;
            int type = BitConverter.ToInt32(buffer.Slice(offset + 4, 4));
            if (type == 0) // CpuSetInformation
            {
                raw.Add((buffer[offset + 14],   // LogicalProcessorIndex
                         buffer[offset + 15],   // CoreIndex
                         buffer[offset + 18])); // EfficiencyClass (higher = faster)
            }
            offset += size;
        }

        int top = raw.Count == 0 ? 0 : raw.Max(r => r.Efficiency);
        bool hybrid = raw.Select(r => r.Efficiency).Distinct().Count() > 1;
        var cores = raw.OrderBy(r => r.Index)
            .Select(r => new LogicalCore(r.Index, r.Core, !hybrid || r.Efficiency == top))
            .ToList();
        return new CpuTopology(processorName, cores, hybrid);
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            if (!string.IsNullOrEmpty(name))
                return name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ");
        }
        catch
        {
            // not important enough to fail over
        }
        return "your processor";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, int bufferLength, out int returnedLength, IntPtr process, int flags);
}
