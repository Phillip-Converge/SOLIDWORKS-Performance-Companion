using SWCompanion.Contracts;

namespace SWCompanion.Core;

/// <summary>
/// Where logs and reports are written. Always the normal Windows app-data folder; when
/// running from a development copy of the source code, also the project's own data
/// folder (kept out of Git), so the developer can read them easily. Nothing personal
/// is written into the code: both locations are worked out at run time. Folders are
/// only created when something is actually written.
/// </summary>
public static class AppPaths
{
    /// <summary>The name of the solution file that marks a development copy of the source.</summary>
    public const string SolutionFileName = "SWCompanion.slnx";

    /// <summary>The normal Windows place for app data, e.g. C:\Users\name\AppData\Local\SWCompanion.</summary>
    public static string LocalAppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.FolderName);

    /// <summary>The project's data folder if the app is running from a development copy of the source; otherwise null.</summary>
    public static string? ProjectDataFolder { get; } = FindProjectDataFolder(AppContext.BaseDirectory);

    /// <summary>Every folder a log or report is written to.</summary>
    public static IReadOnlyList<string> DataFolders =>
        ProjectDataFolder is null ? [LocalAppDataFolder] : [ProjectDataFolder, LocalAppDataFolder];

    /// <summary>Walks up from <paramref name="start"/> looking for the solution file; returns its "data" folder.</summary>
    public static string? FindProjectDataFolder(string start)
    {
        try
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                    return Path.Combine(dir.FullName, "data");
        }
        catch
        {
            // unreadable folder: just use app data
        }
        return null;
    }
}
