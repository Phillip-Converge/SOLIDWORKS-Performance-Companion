using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SWCompanion.Core.Processes;

/// <summary>One SOLIDWORKS-related process seen running right now.</summary>
public sealed record SeenProcess(
    int Id,
    string ExeName,
    ProcessRole Role,
    string Description,
    bool Protected,
    string? Path);

/// <summary>
/// Lists SOLIDWORKS-related processes. READ ONLY: it only asks Windows for each
/// process's name and file path, using the lowest level of access Windows offers.
/// </summary>
public static class SolidWorksProcessFinder
{
    public static IReadOnlyList<SeenProcess> Scan()
    {
        var found = new List<SeenProcess>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id is 0 or 4) continue; // Windows "Idle" and "System"

                var path = TryGetPath(p.Id);
                var exeName = path is not null ? System.IO.Path.GetFileName(path) : p.ProcessName + ".exe";

                var known = ProcessClassifier.Classify(exeName, path);
                if (known is null) continue;

                found.Add(new SeenProcess(p.Id, exeName, known.Role, known.Description, known.Protected, path));
            }
        }

        return found
            .OrderBy(s => s.Role)
            .ThenBy(s => s.ExeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .ToList();
    }

    /// <summary>Full path of a process's program file, or null if Windows won't say
    /// (common for system services when not running as administrator).</summary>
    private static string? TryGetPath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
