using System.Runtime.InteropServices;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Io;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class NowEngineTests
{
    private const double MB = 1024 * 1024;

    private static CpuSnapshot Cpu(double cores, double top) =>
        new(10, Enumerable.Repeat(5.0, 20).ToList(), new SolidWorksCpu(cores, cores * 5, top >= 50 ? 1 : 0, top, true));

    private static IoSnapshot Io(double swRead = 0, double netIn = 0) =>
        new([new DiskReading("0 C:", 5, 0.5, swRead, 0)], [new NetworkReading("Wi-Fi", netIn, 0, 1_200_000_000)],
            new SolidWorksIo(swRead, 0, 0, 0, 0, 0));

    private static NowInputs Inputs(bool? responding = true, double frozenSeconds = 0, CpuSnapshot? cpu = null, IoSnapshot? io = null,
                                    IReadOnlyList<SwWindow>? windows = null, IReadOnlyList<CheckResult>? checks = null, OtherPrograms? others = null) =>
        new(true, responding, TimeSpan.FromSeconds(frozenSeconds), cpu ?? Cpu(0, 1), null, null, io ?? Io(), others,
            windows ?? [], checks ?? []);

    [Fact]
    public void Calm_when_responding_and_nothing_stands_out()
    {
        var r = NowEngine.Evaluate(Inputs());
        Assert.Equal(NowLevel.Calm, r.Level);
        Assert.Empty(r.Causes);
    }

    [Fact]
    public void Not_running()
    {
        var r = NowEngine.Evaluate(Inputs() with { SolidWorksRunning = false });
        Assert.Equal(NowLevel.NotRunning, r.Level);
    }

    [Fact]
    public void A_short_pause_is_not_a_freeze()
    {
        Assert.NotEqual(NowLevel.Frozen, NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 1)).Level);
    }

    [Fact]
    public void Frozen_while_computing_on_one_core()
    {
        var r = NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 14, cpu: Cpu(1.0, 98)));
        Assert.Equal(NowLevel.Frozen, r.Level);
        Assert.Equal("SOLIDWORKS has not responded for 14 s.", r.Headline);
        Assert.Equal("Computing on one core", r.Top!.Title);
        Assert.Contains("regenerating drawing views", r.Top.Evidence);
    }

    [Fact]
    public void Frozen_while_loading_over_the_network()
    {
        var r = NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 6, io: Io(swRead: 40 * MB, netIn: 30 * MB)));
        Assert.Equal("Loading files, probably over the network", r.Top!.Title);
    }

    [Fact]
    public void Frozen_with_nothing_busy_means_waiting()
    {
        var r = NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 9));
        Assert.Equal("Waiting, not working", r.Top!.Title);
        Assert.Contains("can't yet see exactly", r.Top.Evidence); // honest about the limit
    }

    [Fact]
    public void SolidWorks_busy_box_during_a_freeze_is_explained_but_never_blamed()
    {
        // SOLIDWORKS's "SOLIDWORKS is busy running a command" box, shown while the main window is frozen.
        var busyBox = new SwWindow(1, "SOLIDWORKS", "#32770", IsMain: false, IsDialog: true, Responding: true);
        var r = NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 20, cpu: Cpu(1.0, 98), windows: [busyBox]));
        Assert.Equal("computing", r.Top!.Kind); // the real cause stays first
        var notice = r.Causes.Single(c => c.Kind == "busy-notice");
        Assert.Contains("Wait while it says WORKING", notice.Evidence);
        Assert.False(notice.Glaring);
    }

    [Fact]
    public void Busy_box_alone_still_points_to_waiting_as_the_cause()
    {
        var busyBox = new SwWindow(1, "SOLIDWORKS", "#32770", IsMain: false, IsDialog: true, Responding: true);
        var r = NowEngine.Evaluate(Inputs(responding: false, frozenSeconds: 20, windows: [busyBox]));
        Assert.Equal("waiting", r.Top!.Kind);
        Assert.Contains(r.Causes, c => c.Kind == "busy-notice");
    }

    [Fact]
    public void A_box_open_while_responding_is_noted_quietly()
    {
        var question = new SwWindow(1, "SOLIDWORKS", "#32770", IsMain: false, IsDialog: true, Responding: true);
        var r = NowEngine.Evaluate(Inputs(windows: [question]));
        Assert.Equal(NowLevel.Calm, r.Level); // not "needs attention"
        Assert.Equal("A SOLIDWORKS box is open", r.Causes.Single().Title);
        Assert.Empty(r.Glaring);
    }

    [Fact]
    public void Glaring_problems_get_a_fix()
    {
        var hog = new OtherPrograms([new ProgramUse("Backup", 77, 3.0, 0)], [], 3.2);
        var r = NowEngine.Evaluate(Inputs(others: hog));
        var g = Assert.Single(r.Glaring);
        Assert.Contains("Close or pause Backup", g.Fix);
    }

    [Fact]
    public void A_red_checkup_item_is_glaring_but_a_yellow_one_is_not()
    {
        var capped = new CheckResult("cpu-cap", "processor speed limit", CheckStatus.Problem, "Capped at 50%.",
            "Control Panel > Power Options > ... set Plugged in to 100%. More text.", "");
        var battery = new CheckResult("battery", "plugged in", CheckStatus.Look, "On battery.", "Plug in.", "");
        var r = NowEngine.Evaluate(Inputs(checks: [capped, battery]));
        Assert.Equal("Also: processor speed limit", Assert.Single(r.Glaring).Title);
        Assert.Equal(NowLevel.Busy, r.Level);
    }

    [Fact]
    public void Names_another_busy_program()
    {
        var others = new OtherPrograms([new ProgramUse("MsMpEng", 99, 2.5, 0)], [], 3);
        var r = NowEngine.Evaluate(Inputs(others: others));
        Assert.Equal(NowLevel.Busy, r.Level);
        Assert.Contains(r.Causes, c => c.Title == "Another program is busy: MsMpEng");
    }

    [Fact]
    public void Setup_problems_are_listed_as_also_but_do_not_count_as_busy()
    {
        var battery = new CheckResult("battery", "plugged in", CheckStatus.Look, "Running on battery (18%).", "Plug in.", "");
        var r = NowEngine.Evaluate(Inputs(checks: [battery]));
        Assert.Equal(NowLevel.Calm, r.Level);
        Assert.Equal("Also: plugged in", r.Causes.Single().Title);
    }
}

public class FreezeTrackerTests
{
    private static NowReport Frozen(string top) =>
        new(NowLevel.Frozen, "frozen", [new Cause("x", top, "")]);

    [Fact]
    public void Records_a_timeline_and_a_summary()
    {
        var t = new FreezeTracker();
        var start = new DateTime(2026, 11, 20, 23, 41, 0);
        t.Observe(false, start);
        for (int s = 2; s <= 18; s += 2)
        {
            t.Observe(false, start.AddSeconds(s));
            t.Record(Frozen("Computing on one core"), start.AddSeconds(s));
        }
        for (int s = 20; s <= 24; s += 2)
        {
            t.Observe(false, start.AddSeconds(s));
            t.Record(Frozen("Loading files"), start.AddSeconds(s));
        }
        Assert.Equal(2, t.CurrentTimeline.Count);

        t.Observe(true, start.AddSeconds(25));
        var e = t.LastEpisode!;
        Assert.Equal(25, e.Duration.TotalSeconds, 3);
        Assert.Equal("Computing on one core", e.TimeByCause[0].Cause);
        Assert.StartsWith("Last freeze: 25 s at 23:41:00: ", e.Summary);
        Assert.Empty(t.CurrentTimeline); // cleared for the next one
    }

    [Fact]
    public void A_blip_shorter_than_the_threshold_is_not_kept()
    {
        var t = new FreezeTracker();
        var start = DateTime.Now;
        t.Observe(false, start);
        t.Observe(true, start.AddSeconds(1));
        Assert.Null(t.LastEpisode);
    }
}

public class HangProbeTests
{
    /// <summary>
    /// Makes a real window on its own thread, checks it answers, then stops its
    /// thread from handling messages (as a frozen program does) and checks the
    /// probe reports it as not responding.
    /// </summary>
    [Fact]
    public void Detects_a_real_frozen_window()
    {
        IntPtr hwnd = IntPtr.Zero;
        var ready = new ManualResetEventSlim();
        var freeze = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            hwnd = CreateWindowEx(0, "STATIC", "freeze test", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ready.Set();
            while (!freeze.IsSet) // handle messages, like a healthy program
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                Thread.Sleep(10);
            }
            // Stop handling messages: "frozen". Plain sleeping, because .NET's normal waits on some
            // threads quietly handle window messages, which would make the window look alive.
            while (!done.IsSet) Thread.Sleep(10);
            DestroyWindow(hwnd);
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        ready.Wait();

        Assert.True(HangProbe.IsResponding(hwnd, 500));
        freeze.Set();
        Thread.Sleep(50);
        Assert.False(HangProbe.IsResponding(hwnd, 300));

        done.Set();
        thread.Join();
        Assert.Null(HangProbe.IsResponding(hwnd)); // window gone
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h,
                                                IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
}
