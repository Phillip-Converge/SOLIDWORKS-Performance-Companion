using Microsoft.Win32;
using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Checkup;

/// <summary>One folder SOLIDWORKS's File Locations point to.</summary>
/// <param name="Setting">Which File Location, e.g. "BOM Template Folders".</param>
public sealed record FileLocationEntry(string Setting, string Path, bool Exists, bool IsNetwork);

/// <summary>Facts for the owner's optimization checklist. Every field is read, never changed.</summary>
/// <param name="ExplorerLaunchTo">File Explorer "Open File Explorer to": 1 = This PC (null = Windows default).</param>
/// <param name="ShowRecent">Quick access "Show recently used files": 0 = off (null = Windows default, on).</param>
/// <param name="ShowFrequent">Quick access "Show frequently used folders": 0 = off (null = default, on).</param>
/// <param name="ExtraIndexedFolders">Folders added to Windows Search's index (what SOLIDWORKS calls search paths), with whether each exists.</param>
/// <param name="SolidWorksYear">The SOLIDWORKS version whose settings were read, e.g. "2026".</param>
/// <param name="FileLocations">Every folder in SOLIDWORKS's File Locations.</param>
/// <param name="PdmInstalled">The SOLIDWORKS PDM client is installed.</param>
/// <param name="VaultViews">Names of local vault views on this PC.</param>
public sealed record ChecklistFacts(
    int? ExplorerLaunchTo,
    int? ShowRecent,
    int? ShowFrequent,
    IReadOnlyList<(string Path, bool Exists)> ExtraIndexedFolders,
    string? SolidWorksYear,
    IReadOnlyList<FileLocationEntry> FileLocations,
    bool PdmInstalled,
    IReadOnlyList<string> VaultViews);

/// <summary>Reads the checklist settings. READ ONLY: registry reads and "does this folder exist?" checks only.</summary>
public static class ChecklistReader
{
    public static ChecklistFacts Read()
    {
        int? launchTo = ReadInt(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo");
        int? showRecent = ReadInt(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent");
        int? showFrequent = ReadInt(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowFrequent");

        var (year, locations) = ReadFileLocations();
        var views = SubKeys(Registry.LocalMachine, @"SOFTWARE\SolidWorks\Applications\PDMWorks Enterprise\Databases");
        bool pdm = Exists(Registry.LocalMachine, @"SOFTWARE\SolidWorks\Applications\PDMWorks Enterprise");

        return new ChecklistFacts(launchTo, showRecent, showFrequent, ReadIndexedFolders(), year, locations, pdm, views);
    }

    /// <summary>Folders someone added to Windows Search's index (not Windows' own defaults), from its own list of rules.</summary>
    private static IReadOnlyList<(string, bool)> ReadIndexedFolders()
    {
        var result = new List<(string, bool)>();
        try
        {
            using var rules = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex\WorkingSetRules");
            if (rules is null) return result;
            foreach (var name in rules.GetSubKeyNames())
            {
                using var rule = rules.OpenSubKey(name);
                if (rule?.GetValue("URL") is not string url || !url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) continue;
                if (rule.GetValue("Include") is not int include || include != 1) continue;
                if (rule.GetValue("Default") is int isDefault && isDefault == 1) continue;
                var path = IndexUrlToPath(url);
                if (path.Contains('*')) continue;
                if (path.Contains(@"\Windows\Web\", StringComparison.OrdinalIgnoreCase)) continue; // Windows' own wallpapers
                result.Add((path, PathExists(path)));
            }
        }
        catch
        {
            // couldn't read: report nothing rather than guess
        }
        return result;
    }

    /// <summary>"file:///C:\[guid]\Users\Public\..." -> "C:\Users\Public\..."</summary>
    public static string IndexUrlToPath(string url)
    {
        var path = url["file:///".Length..];
        int open = path.IndexOf("\\[", StringComparison.Ordinal);
        int close = path.IndexOf("]\\", StringComparison.Ordinal);
        if (open >= 0 && close > open) path = path[..open] + path[(close + 1)..];
        return path;
    }

    private static (string?, IReadOnlyList<FileLocationEntry>) ReadFileLocations()
    {
        var list = new List<FileLocationEntry>();
        try
        {
            using var sw = Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks");
            // The newest SOLIDWORKS version that has File Locations stored.
            var year = sw?.GetSubKeyNames()
                .Select(n => System.Text.RegularExpressions.Regex.Match(n, @"^SOLIDWORKS (20\d\d)$"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Where(y => Exists(Registry.CurrentUser, $@"Software\SolidWorks\SOLIDWORKS {y}\ExtReferences"))
                .OrderByDescending(y => y)
                .FirstOrDefault();
            if (year is null) return (null, list);

            using var ext = Registry.CurrentUser.OpenSubKey($@"Software\SolidWorks\SOLIDWORKS {year}\ExtReferences");
            foreach (var name in ext!.GetValueNames())
            {
                if (ext.GetValue(name) is not string value) continue;
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    bool network = raw.StartsWith(@"\\", StringComparison.Ordinal);
                    if (!network && !(raw.Length > 2 && raw[1] == ':' && raw[2] == '\\')) continue; // not a folder
                    var path = raw.Replace("CURRENT_USER", Environment.UserName);
                    list.Add(new FileLocationEntry(name, path, PathExists(path), network));
                }
            }
            return (year, list);
        }
        catch
        {
            return (null, list);
        }
    }

    /// <summary>Does this folder or file exist? Network paths get 1.5 s at most, so an unreachable server can't hold the app up.</summary>
    private static bool PathExists(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return Directory.Exists(path) || File.Exists(path);
        var check = Task.Run(() => Directory.Exists(path) || File.Exists(path));
        return check.Wait(TimeSpan.FromSeconds(1.5)) && check.Result;
    }

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        try { using var k = root.OpenSubKey(path); return k?.GetValue(name) is int i ? i : null; }
        catch { return null; }
    }

    private static IReadOnlyList<string> SubKeys(RegistryKey root, string path)
    {
        try { using var k = root.OpenSubKey(path); return k?.GetSubKeyNames() ?? []; }
        catch { return []; }
    }

    private static bool Exists(RegistryKey root, string path)
    {
        try { using var k = root.OpenSubKey(path); return k is not null; }
        catch { return false; }
    }
}

/// <summary>
/// The owner's SOLIDWORKS and PDM optimization checklist as checkup items. Reports and
/// explains only; nothing is changed (fix buttons come in Phase 4 with backup and undo).
/// Where the app can't read a setting reliably, it says "check by hand" instead of guessing.
/// </summary>
public static class ChecklistEvaluator
{
    public const string Source = "SOLIDWORKS 3D CAD and PDM optimization checklist";

    public static IReadOnlyList<CheckResult> Evaluate(ChecklistFacts f) =>
    [
        ExplorerOpensTo(f),
        QuickAccess(f),
        SearchPaths(f),
        FileLocations(f),
        ExternalReferenceSearch(),
        PdmAddIn(f),
    ];

    private static CheckResult ExplorerOpensTo(ChecklistFacts f)
    {
        const string fix = "File Explorer > View > Options > General tab: set \"Open File Explorer to\" to \"This PC\", then OK.";
        const string plain = Analogies.Lead + "opening Explorer on Home or Quick access is like walking into the office and having " +
                             "someone first gather every file you touched recently, including files in the vault, before you can start.";
        return f.ExplorerLaunchTo == 1
            ? new("explorer-opens", "explorer opens to This PC", CheckStatus.Good, "File Explorer opens to \"This PC\".", "", Source, plain)
            : new("explorer-opens", "explorer opens to This PC", CheckStatus.Look,
                "File Explorer opens to Home (Quick access) instead of \"This PC\", so each time it opens it builds lists of recent " +
                "files and folders, which can include vault files.", fix, Source, plain);
    }

    private static CheckResult QuickAccess(ChecklistFacts f)
    {
        bool recentOff = f.ShowRecent == 0, frequentOff = f.ShowFrequent == 0;
        const string fix = "File Explorer > View > Options > General tab, Privacy: untick \"Show recently used files\" and " +
                           "\"Show frequently used folders\", then OK.";
        const string plain = Analogies.Lead + "Quick access keeps a \"recently handled\" shelf by the door. With PDM, keeping it " +
                             "up to date means checking vault files over and over.";
        if (recentOff && frequentOff)
            return new("quick-access", "quick access history", CheckStatus.Good, "Recent files and frequent folders are turned off in Quick access.", "", Source, plain);
        var on = new List<string>();
        if (!recentOff) on.Add("recently used files");
        if (!frequentOff) on.Add("frequently used folders");
        return new("quick-access", "quick access history", CheckStatus.Look,
            $"Quick access is showing {string.Join(" and ", on)}, which keeps Explorer revisiting recent files, including vault files.",
            fix, Source, plain);
    }

    private static CheckResult SearchPaths(ChecklistFacts f)
    {
        const string fix = "Close SOLIDWORKS. Control Panel > Indexing Options > Modify: untick the extra locations (the lower list " +
                           "shows what's included), then OK and Close. Reopen SOLIDWORKS and check Tools > Options > File Locations > " +
                           "Search Paths is empty. With a PDM vault, PDM records each file's path, so search paths aren't needed.";
        const string plain = Analogies.Lead + "Windows Search keeps a card index of every file in these folders. Keeping cards for " +
                             "folders you don't need (or that no longer exist) is extra filing work going on in the background.";
        var extra = f.ExtraIndexedFolders;
        if (extra.Count == 0)
            return new("search-paths", "search paths (indexing)", CheckStatus.Good,
                "Windows Search isn't indexing any extra folders.", "", Source, plain, "SOLIDWORKS");
        var missing = extra.Where(e => !e.Exists).ToList();
        return new("search-paths", "search paths (indexing)", CheckStatus.Look,
            $"Windows Search is set to index {extra.Count} extra folder{(extra.Count == 1 ? "" : "s")}: " +
            string.Join("; ", extra.Take(4).Select(e => e.Path + (e.Exists ? "" : " (doesn't exist)"))) + (extra.Count > 4 ? "; ..." : "") +
            (missing.Count > 0 ? $" {missing.Count} of them no longer exist." : ""),
            fix, Source, plain, "SOLIDWORKS");
    }

    private static CheckResult FileLocations(ChecklistFacts f)
    {
        const string fix = "In SOLIDWORKS: Tools > Options > System Options > File Locations > Edit All. Make sure every folder " +
                           "exists on this PC; use Find/Replace (bottom left) to swap an old path or year for the current one. " +
                           "Save, then close and reopen SOLIDWORKS.";
        const string plain = Analogies.Lead + "File Locations are SOLIDWORKS's address book for templates, tables and libraries. " +
                             "Addresses that no longer exist send it to an empty lot every time it looks; an old address on a " +
                             "network server can leave it knocking on a door that never answers.";
        if (f.SolidWorksYear is null || f.FileLocations.Count == 0)
            return new("file-locations", "file locations", CheckStatus.Unknown, "SOLIDWORKS's File Locations couldn't be read.", fix, Source, plain, "SOLIDWORKS");

        var missing = f.FileLocations.Where(l => !l.Exists).ToList();
        var oldYear = f.FileLocations.Where(l => System.Text.RegularExpressions.Regex.Match(l.Path, @"SOLIDWORKS (20\d\d)") is { Success: true } m
                                                 && m.Groups[1].Value != f.SolidWorksYear).ToList();
        if (missing.Count == 0 && oldYear.Count == 0)
            return new("file-locations", "file locations", CheckStatus.Good,
                $"All {f.FileLocations.Count} File Locations for SOLIDWORKS {f.SolidWorksYear} point to folders that exist.", "", Source, plain, "SOLIDWORKS");

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            // Name the folder most of the missing paths share, e.g. an old install that was removed.
            var common = missing.GroupBy(m => Root(m.Path)).OrderByDescending(g => g.Count()).First();
            parts.Add($"{missing.Count} of {f.FileLocations.Count} File Locations point to folders that don't exist" +
                      (common.Count() > 1 ? $", {common.Count()} of them under {common.Key}" : $", for example {missing[0].Path} ({missing[0].Setting})") + ".");
        }
        if (oldYear.Count > 0)
            parts.Add($"{oldYear.Count} still point to an older SOLIDWORKS year, for example {oldYear[0].Path}.");
        bool network = missing.Any(m => m.IsNetwork);
        if (network) parts.Add("Some missing ones are on the network, where SOLIDWORKS can wait a long time for a server that doesn't answer.");
        return new("file-locations", "file locations", network ? CheckStatus.Problem : CheckStatus.Look,
            string.Join(" ", parts), fix, Source, plain, "SOLIDWORKS");
    }

    /// <summary>"C:\Program Files\Dassault Systemes\X\SOLIDWORKS\lang\english" -> "C:\Program Files\Dassault Systemes\X".</summary>
    private static string Root(string path)
    {
        // First four parts: drive (or empty, empty, server for \\server\share) plus three folders.
        return string.Join('\\', path.Split('\\').Take(4));
    }

    private static CheckResult ExternalReferenceSearch() =>
        new("ext-ref-search", "external reference search", CheckStatus.Unknown,
            "Check by hand: the app can't yet read this setting reliably.",
            "In SOLIDWORKS: Tools > Options > System Options > External References: untick \"Reference Documents specified in " +
            "File Location\" (under \"Search external references in\").",
            Source,
            Analogies.Lead + "with this ticked, every time SOLIDWORKS opens an assembly it checks your list of reference folders " +
            "for each part before using the copy the assembly already knows about. With PDM, the vault already knows where every file is.",
            "SOLIDWORKS");

    private static CheckResult PdmAddIn(ChecklistFacts f)
    {
        const string steps =
            "In SOLIDWORKS: Tools > SOLIDWORKS PDM > Options: read each box and untick what you don't need, especially the " +
            "\"Automatically update the tree in the extra tree pane\" options. In the vault view: Display > Options > tick " +
            "\"Show bitmap for SOLIDWORKS files\", and leave the Data Card or Version tab selected rather than Preview when browsing.";
        const string plain = Analogies.Lead + "every automatic refresh is a clerk walking to the vault office to ask \"anything " +
                             "new?\". Turning off the refreshes you don't need saves all those trips.";
        if (!f.PdmInstalled)
            return new("pdm-options", "PDM add-in and vault view", CheckStatus.Good, "The SOLIDWORKS PDM client isn't installed, so this doesn't apply.", "", Source, plain, "PDM client");
        if (f.VaultViews.Count == 0)
            return new("pdm-options", "PDM add-in and vault view", CheckStatus.Unknown,
                "There's no vault view on this PC yet, so there's nothing to check.", steps, Source, plain, "PDM client");
        return new("pdm-options", "PDM add-in and vault view", CheckStatus.Unknown,
            $"Check by hand for vault view{(f.VaultViews.Count == 1 ? "" : "s")} {string.Join(", ", f.VaultViews)}: the app can't yet read these settings reliably.",
            steps, Source, plain, "PDM client");
    }
}
