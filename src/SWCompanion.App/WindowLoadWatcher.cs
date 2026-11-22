using System.Runtime.InteropServices;
using System.Text;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// Notices SOLIDWORKS's windows being created, shown and closed (Windows tells us; nothing is sent to SOLIDWORKS), and
/// while any is still loading, asks it ten times a second whether it answers yet (the same harmless "are you there?"
/// message the freeze check uses). Read only. Must be created on the screen thread, which receives Windows' notices.
/// </summary>
internal sealed class WindowLoadWatcher : IDisposable
{
    private readonly LoadTracker _tracker;
    private readonly WinEventProc _callback; // kept alive while Windows holds on to it
    private IntPtr _hook;
    private int? _pid;
    private IntPtr _main;
    private readonly Timer _probe;
    private int _probing;

    /// <summary>A window or command finished loading. Raised on a background thread.</summary>
    public event Action<LoadTiming>? Ready;

    public WindowLoadWatcher(LoadTracker tracker)
    {
        _tracker = tracker;
        _callback = OnWinEvent;
        _probe = new Timer(_ => Probe(), null, 100, 100);
    }

    /// <summary>Called every second with the running SOLIDWORKS (or null) and its main window.</summary>
    public void Watch(int? pid, IntPtr mainWindow)
    {
        _main = mainWindow;
        if (pid == _pid) return;
        Unhook();
        _pid = pid;
        if (pid is { } id)
            _hook = SetWinEventHook(EventObjectCreate, EventObjectHide, IntPtr.Zero, _callback, (uint)id, 0, WinEventOutOfContext);
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || hwnd == _main) return;
        if (GetAncestor(hwnd, GaRoot) != hwnd) return; // only whole windows, not the buttons inside them
        var now = DateTime.Now;
        switch (ev)
        {
            case EventObjectCreate: _tracker.WindowCreated(now, hwnd); break;
            case EventObjectShow: _tracker.WindowShown(now, hwnd, Title(hwnd)); break;
            case EventObjectDestroy: _tracker.WindowGone(hwnd); break;
        }
    }

    private void Probe()
    {
        if (Interlocked.Exchange(ref _probing, 1) == 1) return; // the previous round is still asking
        try
        {
            var waiting = _tracker.Waiting;
            bool main = _tracker.WaitingOnMain;
            if (waiting.Count == 0 && !main) return;
            var answers = waiting.ToDictionary(h => h, h => HangProbe.IsResponding(h, 80));
            bool? mainAnswers = main && _main != IntPtr.Zero ? HangProbe.IsResponding(_main, 80) : null;
            foreach (var t in _tracker.Check(DateTime.Now, answers, mainAnswers)) Ready?.Invoke(t);
        }
        catch { }
        finally { Volatile.Write(ref _probing, 0); }
    }

    private void Unhook()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose()
    {
        _probe.Dispose();
        Unhook();
    }

    private static string Title(IntPtr h)
    {
        var sb = new StringBuilder(256);
        InternalGetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint EventObjectCreate = 0x8000, EventObjectDestroy = 0x8001, EventObjectShow = 0x8002, EventObjectHide = 0x8003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint GaRoot = 2;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
}
