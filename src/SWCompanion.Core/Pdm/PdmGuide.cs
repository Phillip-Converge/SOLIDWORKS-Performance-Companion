using SWCompanion.Core.Now;

namespace SWCompanion.Core.Pdm;

/// <summary>One plain explanation for the PDM page.</summary>
public sealed record PdmPoint(string Title, string Text);

/// <summary>
/// The PDM page's plain-English guide to how SOLIDWORKS PDM affects speed, and which caught moments are PDM-related.
/// Advice follows general SOLIDWORKS PDM support practice and Microsoft's documentation. Pure text, tested.
/// </summary>
public static class PdmGuide
{
    public static readonly IReadOnlyList<PdmPoint> Points =
    [
        new("Two servers do the work",
            "A PDM vault has a database server (SQL Server), which answers questions like \"what's the latest version of this file?\", and an archive server, which stores the files themselves. Almost everything you do in the vault talks to one or both, so the further away they are (another site, a VPN), the longer each step waits."),
        new("Files come to your PC first",
            "Before SOLIDWORKS can open a vault file, PDM copies it from the archive server into a local cache on your PC (\"get\"). The first open of a big assembly can be slow because hundreds of files are copied; later opens are quicker unless newer versions need fetching."),
        new("Every reference is checked",
            "Opening an assembly or drawing makes PDM check each file it uses against the vault. Hundreds of parts means hundreds of checks, each a small trip to the database server."),
        new("Check out and check in",
            "Each check-out, check-in, and change of workflow state updates the database and moves files to or from the archive. Large assemblies, many references, and a slow connection all make these take longer."),
        new("The vault view lives in File Explorer",
            "Browsing the vault asks the database about every folder and file you look at. File Explorer set to open on Home or Quick access, which builds lists of recent files (including vault files), adds lookups every time it opens."),
        new("Antivirus and indexing",
            "Windows Defender checking every file in the vault view and the local cache, and Windows Search indexing the vault view, add work to each file PDM handles. Leaving the vault out of indexing, and antivirus exclusions for the vault view and cache, are decisions for your IT team."),
        new("The PDM add-in inside SOLIDWORKS",
            "The add-in adds its own checks when you open and save. It's needed when you work with vault files; if you spend a session only on local files, switching it off (Tools > Add-Ins) removes those steps."),
    ];

    /// <summary>A caught moment that's about PDM: vault waits, PDM programs hanging or crashing, slow PDM windows.</summary>
    public static bool IsPdm(Moment m)
    {
        if (m.Kind.StartsWith("vault-", StringComparison.Ordinal)) return true;
        string text = m.Title + " " + m.Detail;
        return text.Contains("vault", StringComparison.OrdinalIgnoreCase) || text.Contains("PDM", StringComparison.Ordinal) ||
               text.Contains("EdmServer", StringComparison.OrdinalIgnoreCase) || text.Contains("ConisioAdmin", StringComparison.OrdinalIgnoreCase) ||
               (m.Kind == "slow-load" && PdmWindows.Any(w => m.Title.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Titles of PDM's own windows, so a slow one counts as a PDM hold-up.</summary>
    private static readonly string[] PdmWindows = ["Check In", "Check Out", "Get Latest", "Undo Check Out", "Change State", "Data Card", "Copy Tree"];
}
