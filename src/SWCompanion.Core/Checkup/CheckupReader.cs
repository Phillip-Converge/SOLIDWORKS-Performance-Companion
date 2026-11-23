using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SWCompanion.Core.Checkup;

/// <summary>
/// Reads the settings the checkup looks at. READ ONLY: it uses Windows' "read"
/// functions and registry reads only, and never runs anything that can change a setting.
/// </summary>
public static class CheckupReader
{
    private const string HandleKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string HandleKeyWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows";

    // Windows' fixed IDs for the processor settings group and its "maximum processor state" setting.
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    public static CheckupFacts Read()
    {
        var (planGuid, planName) = ReadActivePlan();
        var (hasBattery, onBattery, percent) = ReadBattery();
        var (pageAuto, pageText) = ReadPageFileSetting();
        var (drive, free, total) = ReadSystemDrive();

        return new CheckupFacts(
            planName,
            planGuid,
            ReadGuid(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActiveOverlayAcPowerScheme"),
            planGuid is null ? null : ReadPlanValue(planGuid.Value, ac: true),
            planGuid is null ? null : ReadPlanValue(planGuid.Value, ac: false),
            hasBattery,
            onBattery,
            percent,
            ReadInt(HandleKey, "GDIProcessHandleQuota"),
            ReadInt(HandleKey, "USERProcessHandleQuota"),
            ReadInt(HandleKeyWow, "GDIProcessHandleQuota"),
            ReadInt(HandleKeyWow, "USERProcessHandleQuota"),
            pageAuto,
            pageText,
            ReadPageFileUsage(),
            drive, free, total);
    }

    /// <summary>The Windows drive and its free space (a read of the drive's size, nothing else).</summary>
    private static (string?, double?, double?) ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root)) return (null, null, null);
            var d = new DriveInfo(root);
            const double gb = 1024.0 * 1024 * 1024;
            return (root.TrimEnd('\\'), d.AvailableFreeSpace / gb, d.TotalSize / gb);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // ---- power ----

    private static (Guid?, string?) ReadActivePlan()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return (null, null);
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(ptr);
                return (guid, ReadPlanName(guid));
            }
            finally
            {
                LocalFree(ptr);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadPlanName(Guid plan)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref plan, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ReadPlanValue(Guid plan, bool ac)
    {
        try
        {
            var sub = SubProcessor;
            var setting = ProcThrottleMax;
            uint value;
            uint rc = ac
                ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out value)
                : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out value);
            return rc == 0 ? (int)value : null;
        }
        catch
        {
            return null;
        }
    }

    private static (bool HasBattery, bool OnBattery, int? Percent) ReadBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return (false, false, null);
        bool hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255; // 128 = no battery, 255 = unknown
        return (hasBattery, s.ACLineStatus == 0, s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null);
    }

    // ---- page file ----

    private static (bool?, string?) ReadPageFileSetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            if (key?.GetValue("PagingFiles") is not string[] files || files.Length == 0) return (null, null);
            string text = string.Join("; ", files.Where(f => !string.IsNullOrWhiteSpace(f)));
            // "?:\pagefile.sys" = Windows manages it for all drives; "C:\pagefile.sys 0 0" = system-managed size on C.
            bool automatic = files.Any(f => f.StartsWith("?:", StringComparison.Ordinal))
                          || files.All(f => string.IsNullOrWhiteSpace(f) || f.Trim().EndsWith(" 0 0", StringComparison.Ordinal));
            return (automatic, text);
        }
        catch
        {
            return (null, null);
        }
    }

    private static double? ReadPageFileUsage()
    {
        try
        {
            using var counter = new PerformanceCounter("Paging File", "% Usage", "_Total", readOnly: true);
            return counter.NextValue();
        }
        catch
        {
            return null;
        }
    }

    // ---- registry helpers ----

    private static int? ReadInt(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private static Guid? ReadGuid(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is string s && Guid.TryParse(s, out var g) ? g : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Windows power functions (read only) ----

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
