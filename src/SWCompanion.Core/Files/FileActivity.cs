namespace SWCompanion.Core.Files;

public enum FileOp { Open, Read, Write, Close }

/// <summary>One file event from Windows' tracing, already turned into a normal path.</summary>
/// <param name="Path">Normal path, e.g. "C:\Parts\Housing.SLDPRT" or "\\server\share\Housing.SLDPRT".</param>
/// <param name="Bytes">Size of a read or write (0 for open and close).</param>
/// <param name="Duration">How long a read or write took to complete, if known.</param>
public sealed record FileEvent(DateTime Time, int Pid, FileOp Op, string Path, long Bytes, TimeSpan? Duration);

public enum FileLocation { ThisPc, Network }

/// <summary>What SOLIDWORKS (and the antivirus) did with one file recently.</summary>
public sealed class FileActivity
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public FileLocation Location => Path.StartsWith(@"\\", StringComparison.Ordinal) ? FileLocation.Network : FileLocation.ThisPc;

    /// <summary>"this PC" or "\\server\share".</summary>
    public string Where => Location == FileLocation.ThisPc ? "this PC" : ShareOf(Path);

    public bool IsSolidWorksDocument => FileActivityTracker.DocumentExtensions.Contains(System.IO.Path.GetExtension(Path));

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public int Opens { get; set; }

    /// <summary>Total time SOLIDWORKS spent waiting for reads and writes of this file to complete.</summary>
    public TimeSpan WaitTime { get; set; }

    /// <summary>When the antivirus first and last touched this file while SOLIDWORKS was using it (null if never).</summary>
    public DateTime? AntivirusFirst { get; set; }
    public DateTime? AntivirusLast { get; set; }
    public TimeSpan AntivirusTime => AntivirusFirst is { } a && AntivirusLast is { } b ? b - a : TimeSpan.Zero;
    public bool ScannedByAntivirus => AntivirusFirst is not null;

    /// <summary>From first to last activity.</summary>
    public TimeSpan Span => LastSeen - FirstSeen;

    private static string ShareOf(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\');
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : unc;
    }
}

/// <summary>
/// Keeps a rolling picture of the files SOLIDWORKS is using, how long it waits on
/// each, where each lives, and whether the antivirus is scanning them. Fed by the
/// tracing session; kept separate so it can be tested with made-up events.
/// </summary>
public sealed class FileActivityTracker
{
    /// <summary>SOLIDWORKS document and support-file types worth showing by name.</summary>
    public static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sldprt", ".sldasm", ".slddrw", ".sldlfp", ".sldblk", ".sldftp", ".sldmat", ".sldstd", ".slddrt",
        ".prtdot", ".asmdot", ".drwdot", ".x_t", ".x_b", ".step", ".stp", ".igs", ".iges", ".stl", ".sldxml",
        ".sldclr", ".sldsffvt", ".swp", ".swb",
    };

    /// <summary>Files are forgotten this long after their last activity.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(2);

    /// <summary>A file counts as "being read right now" if touched this recently.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, FileActivity> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int? SolidWorksPid { get; set; }
    public HashSet<int> AntivirusPids { get; } = new();

    /// <summary>Called whenever a file goes quiet, with its finished activity (used for the activity log).</summary>
    public event Action<FileActivity>? FileFinished;

    public void Add(FileEvent e)
    {
        lock (_lock)
        {
            bool isSw = e.Pid == SolidWorksPid;
            bool isAv = AntivirusPids.Contains(e.Pid);
            if (!isSw && !isAv) return;
            if (string.IsNullOrEmpty(e.Path) || e.Path.EndsWith('\\')) return; // folders

            if (!_files.TryGetValue(e.Path, out var f))
            {
                if (!isSw) return; // only track antivirus on files SOLIDWORKS is using
                f = new FileActivity { Path = e.Path, FirstSeen = e.Time, LastSeen = e.Time };
                _files[e.Path] = f;
            }

            if (isAv)
            {
                f.AntivirusFirst ??= e.Time;
                f.AntivirusLast = e.Time > (f.AntivirusLast ?? e.Time) ? e.Time : f.AntivirusLast ?? e.Time;
                return;
            }

            if (e.Time > f.LastSeen) f.LastSeen = e.Time;
            switch (e.Op)
            {
                // Opening counts too: an antivirus scan usually happens while the open is held up.
                case FileOp.Open: f.Opens++; f.WaitTime += e.Duration ?? TimeSpan.Zero; break;
                case FileOp.Read: f.BytesRead += e.Bytes; f.WaitTime += e.Duration ?? TimeSpan.Zero; break;
                case FileOp.Write: f.BytesWritten += e.Bytes; f.WaitTime += e.Duration ?? TimeSpan.Zero; break;
            }
        }
    }

    /// <summary>
    /// Forgets files that have been quiet for <see cref="KeepFor"/>, reporting each one
    /// through <see cref="FileFinished"/>. Antivirus activity is only recorded for files
    /// SOLIDWORKS has already opened (the scan happens while SOLIDWORKS's open is held up).
    /// </summary>
    public void Prune(DateTime now)
    {
        List<FileActivity> finished;
        lock (_lock)
        {
            finished = _files.Values.Where(f => now - f.LastSeen > KeepFor).ToList();
            foreach (var f in finished) _files.Remove(f.Path);
        }
        foreach (var f in finished) FileFinished?.Invoke(f);
    }

    /// <summary>Recent files, the ones SOLIDWORKS waited on longest first.</summary>
    public IReadOnlyList<FileActivity> Recent(DateTime now, TimeSpan within)
    {
        lock (_lock)
            return _files.Values.Where(f => now - f.LastSeen <= within)
                .OrderByDescending(f => f.WaitTime + f.AntivirusTime)
                .ThenByDescending(f => f.LastSeen)
                .ToList();
    }

    /// <summary>Files SOLIDWORKS is reading or writing right now.</summary>
    public IReadOnlyList<FileActivity> Active(DateTime now) => Recent(now, ActiveWindow);

    public void Clear()
    {
        lock (_lock) _files.Clear();
    }
}
