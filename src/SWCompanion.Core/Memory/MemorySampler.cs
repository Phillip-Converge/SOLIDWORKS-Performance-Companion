using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SWCompanion.Core.Memory;

/// <summary>
/// Reads memory use for the PC and SOLIDWORKS. READ ONLY: it only asks Windows
/// for counts. Needs no administrator rights for a SOLIDWORKS started normally.
/// </summary>
public sealed class MemorySampler : IDisposable
{
    /// <summary>Windows' default per-program limit when the registry doesn't say otherwise.</summary>
    public const int DefaultHandleLimit = 10_000;

    private readonly int _gdiLimit;
    private readonly int _userLimit;
    private int? _swPid;
    private Process? _swProcess;

    public MemorySampler()
    {
        (_gdiLimit, _userLimit) = ReadHandleLimits();
    }

    public MemorySnapshot Sample(int? solidWorksPid)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException("Windows did not return memory totals.");

        return new MemorySnapshot(
            status.TotalPhys,
            status.AvailPhys,
            status.TotalPageFile,                       // Windows calls the commit limit "TotalPageFile"
            status.TotalPageFile - status.AvailPageFile,
            SampleSolidWorks(solidWorksPid)) with { TopReserved = Antivirus.ProcessTimes.TopReserved(5) };
    }

    private SolidWorksMemory? SampleSolidWorks(int? pid)
    {
        if (pid != _swPid)
        {
            _swProcess?.Dispose();
            _swProcess = null;
            _swPid = pid;
            if (pid is null) return null;
            try { _swProcess = Process.GetProcessById(pid.Value); }
            catch { return null; }
        }

        if (_swProcess is null) return null;
        try
        {
            // Memory figures come from Windows' process list (no access needed).
            _swProcess.Refresh();
            if (_swProcess.HasExited) return null;

            // Drawing/window object counts need a handle: ask for the lowest level of
            // access Windows offers (query only), never full access.
            var h = OpenProcess(ProcessQueryLimitedInformation, false, _swProcess.Id);
            if (h == IntPtr.Zero) return null;
            try
            {
                return new SolidWorksMemory(
                    _swProcess.WorkingSet64,
                    _swProcess.PrivateMemorySize64,
                    _swProcess.PeakWorkingSet64,
                    GetGuiResources(h, GrGdiObjects),
                    GetGuiResources(h, GrGdiObjectsPeak),
                    GetGuiResources(h, GrUserObjects),
                    GetGuiResources(h, GrUserObjectsPeak),
                    _gdiLimit,
                    _userLimit,
                    _swProcess.HandleCount);
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch
        {
            return null; // ended, or Windows refused
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>This PC's per-program limits for drawing (GDI) and window (USER) objects.</summary>
    public static (int Gdi, int User) ReadHandleLimits()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            return (AsLimit(key?.GetValue("GDIProcessHandleQuota")), AsLimit(key?.GetValue("USERProcessHandleQuota")));
        }
        catch
        {
            return (DefaultHandleLimit, DefaultHandleLimit);
        }
    }

    private static int AsLimit(object? value) =>
        value is int i && i > 0 ? i : DefaultHandleLimit;

    private const int GrGdiObjects = 0, GrUserObjects = 1, GrGdiObjectsPeak = 2, GrUserObjectsPeak = 4;

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    public void Dispose() => _swProcess?.Dispose();
}
