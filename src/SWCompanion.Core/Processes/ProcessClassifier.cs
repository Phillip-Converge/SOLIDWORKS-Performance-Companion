namespace SWCompanion.Core.Processes;

/// <summary>Decides whether a process is SOLIDWORKS-related, and what it is.</summary>
public static class ProcessClassifier
{
    private static readonly Dictionary<string, KnownProcess> ByName =
        KnownProcesses.All.ToDictionary(p => p.ExeName, StringComparer.OrdinalIgnoreCase);

    // Folders that hold SOLIDWORKS programs. Matched anywhere in the path so that
    // "SOLIDWORKS Corp 2025" and other installed years are caught too.
    private static readonly string[] SolidWorksFolders =
    [
        @"\SOLIDWORKS Corp",
        @"\SolidWorks Shared\",
    ];

    private static readonly string[] LicensingFolders =
    [
        @"\FlexNet Publisher\",
        @"\SolidNetWork License Manager\",
    ];

    /// <summary>
    /// Returns what the process is, or null if it has nothing to do with SOLIDWORKS.
    /// </summary>
    /// <param name="exeName">File name such as "SLDWORKS.exe".</param>
    /// <param name="path">Full path if Windows let us read it, otherwise null.</param>
    public static KnownProcess? Classify(string exeName, string? path)
    {
        if (ByName.TryGetValue(exeName, out var known))
            return known;

        if (path is null)
            return null;

        // Anything licensing-related is protected even if we don't know its name.
        if (LicensingFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return new KnownProcess(exeName, ProcessRole.Licensing,
                "Licensing program (recognised by its folder).", Protected: true);

        if (SolidWorksFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return new KnownProcess(exeName, ProcessRole.Other,
                "In a SOLIDWORKS folder but not yet reviewed. Treated as off-limits until reviewed.",
                Protected: true);

        return null;
    }
}
