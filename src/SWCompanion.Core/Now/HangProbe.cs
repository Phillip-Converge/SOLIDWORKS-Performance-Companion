using System.Runtime.InteropServices;
using System.Text;

namespace SWCompanion.Core.Now;

/// <summary>A visible top-level window that belongs to SOLIDWORKS.</summary>
/// <param name="IsMain">The main SOLIDWORKS window.</param>
/// <param name="IsDialog">A standard dialog box (a window asking a question or showing a message).</param>
/// <param name="Responding">For dialogs: whether the box itself answers (null if not checked). A box that answers
/// while the main window doesn't is SOLIDWORKS's own "busy running a command" notice, not a question it's waiting on.</param>
public sealed record SwWindow(IntPtr Handle, string Title, string ClassName, bool IsMain, bool IsDialog, bool? Responding = null);

/// <summary>
/// Checks from outside whether SOLIDWORKS is responding, the same way Windows
/// decides a program is "Not Responding". READ ONLY: it sends WM_NULL, the
/// "are you there?" message that does nothing, and lists windows without
/// sending them anything (so it can't get stuck on a frozen window itself).
/// </summary>
public static class HangProbe
{
    /// <summary>
    /// True if the window answered within <paramref name="timeoutMs"/>; false if not;
    /// null if the window is gone. Blocks the calling thread for at most the timeout,
    /// so call it from a background thread.
    /// </summary>
    public static bool? IsResponding(IntPtr window, int timeoutMs = 300)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return null;
        if (IsHungAppWindow(window)) return false; // Windows already considers it hung
        var ok = SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero,
            SmtoAbortIfHung | SmtoBlock, (uint)timeoutMs, out _);
        return ok != IntPtr.Zero;
    }

    /// <summary>Visible top-level windows of a process, the main one first.</summary>
    public static IReadOnlyList<SwWindow> ListWindows(int pid)
    {
        var found = new List<(IntPtr Handle, string Title, string Class, bool Owned, long Area)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint owner);
            if (owner != pid || !IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area == 0) return true;
            found.Add((h, Title(h), ClassOf(h), GetWindow(h, GwOwner) != IntPtr.Zero, area));
            return true;
        }, IntPtr.Zero);

        // Main window: the largest window that isn't owned by another one.
        var main = found.Where(w => !w.Owned).OrderByDescending(w => w.Area).FirstOrDefault();
        return found
            .Select(w => new SwWindow(w.Handle, w.Title, w.Class, w.Handle == main.Handle, w.Class == "#32770"))
            .OrderByDescending(w => w.IsMain)
            .ToList();
    }

    /// <summary>Reads a window's title without sending it a message (safe on frozen windows).</summary>
    private static string Title(IntPtr h)
    {
        var sb = new StringBuilder(256);
        InternalGetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint WmNull = 0x0000;
    private const uint SmtoBlock = 0x0001, SmtoAbortIfHung = 0x0002;
    private const uint GwOwner = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
}
