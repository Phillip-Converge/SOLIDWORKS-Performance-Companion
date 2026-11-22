using System.Diagnostics;
using System.Runtime.InteropServices;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class LifeSignsTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 10, 0, 0);

    /// <summary>Feeds one reading per second. Each step says what changed in that second.</summary>
    private static LifeSigns Run(params (double MainMs, double CpuMs, ulong IoBytes)[] seconds)
    {
        var life = new LifeSigns();
        TimeSpan main = TimeSpan.Zero, cpu = TimeSpan.Zero;
        ulong io = 0;
        life.Add(new LifeSample(T0, main, cpu, io));
        for (int i = 0; i < seconds.Length; i++)
        {
            main += TimeSpan.FromMilliseconds(seconds[i].MainMs);
            cpu += TimeSpan.FromMilliseconds(seconds[i].CpuMs + seconds[i].MainMs);
            io += seconds[i].IoBytes;
            life.Add(new LifeSample(T0.AddSeconds(i + 1), main, cpu, io));
        }
        return life;
    }

    private static (double, double, ulong)[] Quiet(int n) => Enumerable.Repeat((0.0, 0.0, 0UL), n).ToArray();

    [Fact]
    public void Main_thread_busy_means_working()
    {
        var life = Run((900, 0, 0), (950, 0, 0));
        var v = life.Evaluate(T0.AddSeconds(2), frozen: true);
        Assert.Equal(Verdict.Working, v.Verdict);
        Assert.Equal("", v.Advice);
    }

    [Fact]
    public void Main_thread_waiting_but_data_moving_means_background()
    {
        var life = Run((0, 0, 3_000_000), (0, 5, 2_500_000));
        Assert.Equal(Verdict.Background, life.Evaluate(T0.AddSeconds(2), frozen: true).Verdict);
    }

    [Fact]
    public void Short_silence_is_quiet_long_silence_is_probably_stuck_then_stuck()
    {
        var life = Run([(800, 0, 0), .. Quiet(400)]);
        Assert.Equal(Verdict.Quiet, life.Evaluate(T0.AddSeconds(31), frozen: true).Verdict);
        Assert.Equal(Verdict.ProbablyStuck, life.Evaluate(T0.AddSeconds(1 + 130), frozen: true).Verdict);

        var stuck = life.Evaluate(T0.AddSeconds(1 + 310), frozen: true);
        Assert.Equal(Verdict.Stuck, stuck.Verdict);
        Assert.Equal(T0.AddSeconds(1), stuck.LastSignOfLife);
        Assert.Contains("Task Manager", stuck.Advice);
        Assert.Contains("never ends SOLIDWORKS", stuck.Advice); // the app never does it itself
    }

    [Fact]
    public void Tiny_housekeeping_does_not_count_as_progress()
    {
        // A few milliseconds of CPU and a few bytes now and then: not real work.
        var life = Run([(800, 0, 0), .. Enumerable.Repeat((2.0, 5.0, 512UL), 150)]);
        Assert.Equal(Verdict.ProbablyStuck, life.Evaluate(T0.AddSeconds(151), frozen: true).Verdict);
    }

    [Fact]
    public void Not_frozen_means_no_verdict()
    {
        Assert.Equal(Verdict.None, Run((900, 0, 0)).Evaluate(T0.AddSeconds(1), frozen: false).Verdict);
    }
}

public class LifeSignsSamplerTests
{
    /// <summary>
    /// A real window whose thread first does work (spins), then stops completely.
    /// The sampler must see the main-thread CPU rise, then stop rising.
    /// </summary>
    [Fact]
    public void Sees_a_real_window_thread_work_and_then_stop()
    {
        IntPtr hwnd = IntPtr.Zero;
        var ready = new ManualResetEventSlim();
        var stopWorking = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            hwnd = CreateWindowEx(0, "STATIC", "life test", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ready.Set();
            while (!stopWorking.IsSet) { } // busy: like SOLIDWORKS computing
            while (!done.IsSet) Thread.Sleep(10); // silent: like SOLIDWORKS stuck
            DestroyWindow(hwnd);
        });
        thread.Start();
        ready.Wait();
        int pid = Environment.ProcessId;

        var a = LifeSignsSampler.Sample(pid, hwnd, DateTime.Now)!;
        Thread.Sleep(300);
        var b = LifeSignsSampler.Sample(pid, hwnd, DateTime.Now)!;
        stopWorking.Set();
        Thread.Sleep(100);
        var c = LifeSignsSampler.Sample(pid, hwnd, DateTime.Now)!;
        Thread.Sleep(300);
        var d = LifeSignsSampler.Sample(pid, hwnd, DateTime.Now)!;
        done.Set();
        thread.Join();

        Assert.True(b.MainThreadCpu - a.MainThreadCpu >= TimeSpan.FromMilliseconds(100), "working thread should use CPU");
        Assert.True(d.MainThreadCpu - c.MainThreadCpu < LifeSigns.MainThreadMin, "stopped thread should not");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
