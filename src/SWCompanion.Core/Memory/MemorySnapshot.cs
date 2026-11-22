namespace SWCompanion.Core.Memory;

/// <summary>Memory readings for the PC and SOLIDWORKS at one moment.</summary>
/// <param name="TotalRam">Physical memory (RAM) installed, in bytes.</param>
/// <param name="AvailableRam">RAM free for programs to use right now.</param>
/// <param name="CommitLimit">Windows' total memory limit: RAM plus the page file on disk.</param>
/// <param name="CommitUsed">How much of that limit programs have set aside.</param>
/// <param name="SolidWorks">SOLIDWORKS readings, or null if it isn't running or can't be read.</param>
public sealed record MemorySnapshot(
    ulong TotalRam,
    ulong AvailableRam,
    ulong CommitLimit,
    ulong CommitUsed,
    SolidWorksMemory? SolidWorks)
{
    public ulong UsedRam => TotalRam - AvailableRam;
    public double RamUsedPercent => TotalRam == 0 ? 0 : UsedRam * 100.0 / TotalRam;
    public double CommitUsedPercent => CommitLimit == 0 ? 0 : CommitUsed * 100.0 / CommitLimit;
    /// <summary>The programs that have reserved the most memory right now (all copies of each added together), biggest first.</summary>
    public IReadOnlyList<MemoryUser> TopReserved { get; init; } = [];
}

/// <summary>One program's reserved memory ("commit size" in Task Manager's Details tab), all its copies together.</summary>
public sealed record MemoryUser(string Name, ulong Bytes, int Copies);

/// <param name="InRam">Working set: SOLIDWORKS memory that is in RAM right now.</param>
/// <param name="Committed">Private bytes: memory SOLIDWORKS has set aside for its own data,
/// whether it is in RAM at the moment or parked on disk.</param>
/// <param name="PeakInRam">The most SOLIDWORKS has had in RAM since it started.</param>
/// <param name="GdiObjects">Drawing objects (pens, fonts, bitmaps...) in use.</param>
/// <param name="GdiPeak">Most drawing objects used at once since SOLIDWORKS started.</param>
/// <param name="UserObjects">Window objects (windows, menus, controls...) in use.</param>
/// <param name="UserPeak">Most window objects used at once since SOLIDWORKS started.</param>
/// <param name="GdiLimit">This PC's per-program limit for drawing objects.</param>
/// <param name="UserLimit">This PC's per-program limit for window objects.</param>
/// <param name="Handles">Other Windows handles (files, threads, events...). No practical limit here.</param>
public sealed record SolidWorksMemory(
    long InRam,
    long Committed,
    long PeakInRam,
    int GdiObjects,
    int GdiPeak,
    int UserObjects,
    int UserPeak,
    int GdiLimit,
    int UserLimit,
    int Handles)
{
    public double GdiPercent => GdiLimit <= 0 ? 0 : GdiObjects * 100.0 / GdiLimit;
    public double UserPercent => UserLimit <= 0 ? 0 : UserObjects * 100.0 / UserLimit;
}
