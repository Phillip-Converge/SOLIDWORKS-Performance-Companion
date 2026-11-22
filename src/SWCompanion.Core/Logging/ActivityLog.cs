namespace SWCompanion.Core.Logging;

/// <summary>
/// Appends plain-English, timestamped lines to a daily log file in every approved
/// data folder (see <see cref="AppPaths.DataFolders"/>). Folders are created on the
/// first write. Only ever appends: it never edits or deletes earlier lines or files.
/// </summary>
public sealed class ActivityLog
{
    private readonly IReadOnlyList<string> _folders;
    private readonly object _lock = new();

    public ActivityLog() : this(AppPaths.DataFolders.Select(f => Path.Combine(f, "logs")).ToList()) { }

    /// <param name="folders">Where to write. Separate so tests can use a temporary folder.</param>
    public ActivityLog(IReadOnlyList<string> folders) => _folders = folders;

    /// <summary>The most recent problem writing the log, or null.</summary>
    public string? LastError { get; private set; }

    public static string FileNameFor(DateTime day) => $"activity-{day:yyyy-MM-dd}.log";

    public void Write(string text, DateTime? when = null)
    {
        var t = when ?? DateTime.Now;
        string line = $"{t:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}";
        lock (_lock)
        {
            foreach (var folder in _folders)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    File.AppendAllText(Path.Combine(folder, FileNameFor(t)), line);
                }
                catch (Exception ex)
                {
                    LastError = $"Couldn't write to {folder}: {ex.Message}";
                }
            }
        }
    }
}
