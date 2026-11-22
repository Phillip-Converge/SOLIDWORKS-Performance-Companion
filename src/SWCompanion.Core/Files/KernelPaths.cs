using System.Runtime.InteropServices;
using System.Text;

namespace SWCompanion.Core.Files;

/// <summary>
/// Windows' tracing reports paths the way the kernel sees them, e.g.
/// "\Device\HarddiskVolume3\Parts\Housing.SLDPRT" or "\Device\Mup\server\share\x.SLDPRT".
/// This turns them into the normal "C:\Parts\..." and "\\server\share\..." forms. READ ONLY.
/// </summary>
public sealed class KernelPaths
{
    private readonly List<(string Device, string Drive)> _drives;

    public KernelPaths() : this(ReadDriveMap()) { }

    /// <param name="drives">Pairs like ("\Device\HarddiskVolume3", "C:"). Separate so it can be tested.</param>
    public KernelPaths(IEnumerable<(string Device, string Drive)> drives)
    {
        _drives = drives.OrderByDescending(d => d.Device.Length).ToList();
    }

    public string ToNormal(string kernelPath)
    {
        if (string.IsNullOrEmpty(kernelPath)) return kernelPath;

        foreach (var prefix in new[] { @"\Device\Mup\", @"\Device\LanmanRedirector\" })
            if (kernelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Some versions add ";X:0000000000abcdef\" or ";LanmanRedirector\" before the server name.
                var rest = kernelPath[prefix.Length..];
                if (rest.StartsWith(';'))
                {
                    int slash = rest.IndexOf('\\');
                    rest = slash >= 0 ? rest[(slash + 1)..] : rest;
                }
                return @"\\" + rest;
            }

        foreach (var (device, drive) in _drives)
            if (kernelPath.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase) ||
                kernelPath.Equals(device, StringComparison.OrdinalIgnoreCase))
                return drive + kernelPath[device.Length..];

        return kernelPath;
    }

    private static List<(string, string)> ReadDriveMap()
    {
        var map = new List<(string, string)>();
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var sb = new StringBuilder(1024);
            if (QueryDosDevice($"{letter}:", sb, sb.Capacity) > 0)
                map.Add((sb.ToString(), $"{letter}:"));
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
}
