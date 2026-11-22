namespace SWCompanion.Core.Io;

/// <summary>One disk's activity since the previous reading.</summary>
/// <param name="Name">Windows' name, e.g. "0 C:".</param>
/// <param name="BusyPercent">How much of the time the disk was busy, 0-100.</param>
/// <param name="ResponseMs">Average time each read or write took, in milliseconds.</param>
public sealed record DiskReading(string Name, double BusyPercent, double ResponseMs, double ReadBytesPerSec, double WriteBytesPerSec);

/// <summary>One network adapter's traffic since the previous reading.</summary>
/// <param name="SpeedBitsPerSec">The adapter's link speed as Windows reports it (0 if unknown).</param>
public sealed record NetworkReading(string Name, double ReceiveBytesPerSec, double SendBytesPerSec, double SpeedBitsPerSec)
{
    /// <summary>Busiest direction as a share of the link speed, 0-100 (0 if speed unknown).</summary>
    public double UsedPercent => SpeedBitsPerSec <= 0 ? 0
        : Math.Clamp(Math.Max(ReceiveBytesPerSec, SendBytesPerSec) * 8 * 100 / SpeedBitsPerSec, 0, 100);
}

/// <param name="ReadBytesPerSec">Everything SOLIDWORKS read per second: local files, network files and other I/O together.</param>
/// <param name="WriteBytesPerSec">Everything SOLIDWORKS wrote per second.</param>
/// <param name="TotalRead">Read since SOLIDWORKS started.</param>
/// <param name="TotalWritten">Written since SOLIDWORKS started.</param>
public sealed record SolidWorksIo(
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double ReadsPerSec,
    double WritesPerSec,
    ulong TotalRead,
    ulong TotalWritten);

/// <summary>Disk and network readings for one moment.</summary>
public sealed record IoSnapshot(
    IReadOnlyList<DiskReading> Disks,
    IReadOnlyList<NetworkReading> Networks,
    SolidWorksIo? SolidWorks)
{
    public DiskReading? BusiestDisk => Disks.OrderByDescending(d => d.BusyPercent).FirstOrDefault();
    public double NetworkReceive => Networks.Sum(n => n.ReceiveBytesPerSec);
    public double NetworkSend => Networks.Sum(n => n.SendBytesPerSec);
}
