using SWCompanion.Core.Cpu;

namespace SWCompanion.Core.Tests;

public class CpuTopologyTests
{
    /// <summary>Builds Windows' CPU-set list the way Windows lays it out (32 bytes per slot).</summary>
    private static byte[] Buffer(params (int Index, int Core, int Efficiency)[] slots)
    {
        var buf = new byte[slots.Length * 32];
        for (int i = 0; i < slots.Length; i++)
        {
            int o = i * 32;
            BitConverter.GetBytes(32).CopyTo(buf, o);   // Size
            BitConverter.GetBytes(0).CopyTo(buf, o + 4); // Type = CpuSetInformation
            buf[o + 14] = (byte)slots[i].Index;
            buf[o + 15] = (byte)slots[i].Core;
            buf[o + 18] = (byte)slots[i].Efficiency;
        }
        return buf;
    }

    /// <summary>An example hybrid processor (Intel Core i9-13900H): 6 performance cores with two slots each, 8 efficiency cores.</summary>
    private static CpuTopology ExampleLaptop()
    {
        var slots = new List<(int, int, int)>();
        for (int i = 0; i < 12; i++) slots.Add((i, i - i % 2, 1)); // 6 P-cores, 2 slots each
        for (int i = 12; i < 20; i++) slots.Add((i, i, 0));        // 8 E-cores, 1 slot each
        return CpuTopology.FromCpuSetBuffer(Buffer(slots.ToArray()), "Intel Core i9-13900H");
    }

    [Fact]
    public void Decodes_a_hybrid_processor()
    {
        var t = ExampleLaptop();
        Assert.True(t.IsHybrid);
        Assert.Equal(20, t.Cores.Count);
        Assert.Equal(6, t.PerformancePhysicalCount);
        Assert.Equal(8, t.EfficiencyPhysicalCount);
        Assert.Equal("00–11", CpuTopology.SlotRange(t.PerformanceSlots));
        Assert.Equal("12–19", CpuTopology.SlotRange(t.EfficiencySlots));
        Assert.True(t.HasPairs);
        Assert.Equal(5, t.PairOf(4)!.Index);
        Assert.Null(t.PairOf(15));
    }

    [Fact]
    public void A_processor_with_one_kind_of_core_is_not_hybrid()
    {
        var t = CpuTopology.FromCpuSetBuffer(Buffer((0, 0, 0), (1, 0, 0), (2, 2, 0), (3, 2, 0)), "test");
        Assert.False(t.IsHybrid);
        Assert.All(t.Cores, c => Assert.True(c.IsPerformance));
    }

    [Fact]
    public void Reads_this_PC()
    {
        var t = CpuTopology.Read();
        Assert.Equal(Environment.ProcessorCount, t.Cores.Count);
        Assert.False(string.IsNullOrWhiteSpace(t.ProcessorName));
    }
}
