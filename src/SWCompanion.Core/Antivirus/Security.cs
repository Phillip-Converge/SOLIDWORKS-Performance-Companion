using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Antivirus;

/// <summary>An antivirus product Windows Security Center knows about.</summary>
public sealed record AntivirusProduct(string Name, bool On, bool IsDefender);

/// <summary>A folder SOLIDWORKS needs to write to, and whether Windows lets you write there.</summary>
/// <param name="Blocked">Why writing is blocked, in plain words; null if it isn't.</param>
public sealed record WorkFolder(string What, string Path, bool Exists, string? Blocked);

/// <summary>What Windows lets the Companion see about security that can get in SOLIDWORKS's way. Read without administrator rights.</summary>
/// <param name="Antivirus">Installed antivirus products (null if Windows Security Center couldn't be asked).</param>
/// <param name="FolderProtection">Defender's Controlled folder access: 0 off, 1 on, 2 audit only (logs, doesn't block); null if unknown.</param>
/// <param name="FolderProtectionAllowed">Programs allowed through Controlled folder access.</param>
/// <param name="ProtectedFolders">Extra folders added to Controlled folder access (Windows also protects Documents, Desktop, Pictures and similar).</param>
public sealed record SecurityFacts(
    IReadOnlyList<AntivirusProduct>? Antivirus,
    int? FolderProtection,
    IReadOnlyList<string> FolderProtectionAllowed,
    IReadOnlyList<string> ProtectedFolders,
    IReadOnlyList<WorkFolder> WorkFolders);

/// <summary>
/// Reads the security facts. READ ONLY: asks Windows (Security Center and Defender's settings, the same way Windows
/// Security and Get-MpPreference do) and reads folders' permission lists. Nothing is changed, written or created.
/// </summary>
public static class SecurityReader
{
    public static SecurityFacts Read(string? solidWorksYear)
    {
        var (cfa, allowed, protectedFolders) = ReadFolderProtection();
        return new SecurityFacts(ReadAntivirus(), cfa, allowed, protectedFolders, ReadWorkFolders(solidWorksYear));
    }

    private static IReadOnlyList<AntivirusProduct>? ReadAntivirus()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            var list = new List<AntivirusProduct>();
            foreach (ManagementObject o in search.Get())
            {
                string name = o["displayName"]?.ToString() ?? "";
                uint state = Convert.ToUInt32(o["productState"] ?? 0u);
                // productState: the middle byte says whether real-time protection is on (0x10 = on).
                bool on = ((state >> 8) & 0x10) != 0;
                list.Add(new AntivirusProduct(name, on, name.Contains("Defender", StringComparison.OrdinalIgnoreCase)));
            }
            return list;
        }
        catch { return null; } // Security Center isn't on server versions of Windows
    }

    private static (int?, IReadOnlyList<string>, IReadOnlyList<string>) ReadFolderProtection()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT EnableControlledFolderAccess, ControlledFolderAccessAllowedApplications, ControlledFolderAccessProtectedFolders FROM MSFT_MpPreference");
            foreach (ManagementObject o in search.Get())
            {
                int? mode = o["EnableControlledFolderAccess"] is { } v ? Convert.ToInt32(v) : null;
                return (mode, Strings(o["ControlledFolderAccessAllowedApplications"]), Strings(o["ControlledFolderAccessProtectedFolders"]));
            }
        }
        catch { }
        return (null, [], []);
    }

    private static IReadOnlyList<string> Strings(object? v) => v is string[] a ? a.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() : [];

    /// <summary>The folders SOLIDWORKS writes to while you work: temporary files, its settings, its backups, your documents.</summary>
    private static IReadOnlyList<WorkFolder> ReadWorkFolders(string? year)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folders = new List<(string, string)>
        {
            ("temporary files", Path.GetTempPath()),
            ("SOLIDWORKS settings", Path.Combine(roaming, "SOLIDWORKS")),
            ("SOLIDWORKS local data", Path.Combine(local, "SOLIDWORKS")),
            ("your Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        };
        if (year is not null && ReadBackupFolder(year) is { } backup) folders.Add(("SOLIDWORKS backups and auto-recover", backup));
        return folders.Select(f => Check(f.Item1, f.Item2)).ToList();
    }

    /// <summary>Tools > Options > Backup/Recover folder, if set.</summary>
    private static string? ReadBackupFolder(string year)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\SolidWorks\SOLIDWORKS {year}\Extreme\Backup");
            return key?.GetValue("Backup Directory") as string;
        }
        catch { return null; }
    }

    /// <summary>Can you write here? Read from the folder's permission list only: nothing is written to test it.</summary>
    public static WorkFolder Check(string what, string path)
    {
        try
        {
            var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(path));
            if (!dir.Exists) return new(what, dir.FullName, false, null);
            using var me = WindowsIdentity.GetCurrent();
            var mine = new HashSet<SecurityIdentifier>(me.Groups?.OfType<SecurityIdentifier>() ?? []) { me.User! };
            var rules = dir.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                           .Where(r => mine.Contains((SecurityIdentifier)r.IdentityReference)).ToList();
            const FileSystemRights write = FileSystemRights.CreateFiles | FileSystemRights.WriteData;
            if (rules.Any(r => r.AccessControlType == AccessControlType.Deny && (r.FileSystemRights & write) != 0))
                return new(what, dir.FullName, true, "a permission setting denies you saving files here");
            if (!rules.Any(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & write) != 0))
                return new(what, dir.FullName, true, "you don't have permission to save files here");
            return new(what, dir.FullName, true, null);
        }
        catch (UnauthorizedAccessException) { return new(what, path, true, "Windows won't even let you look at this folder's permissions"); }
        catch { return new(what, path, true, null); } // can't tell: say nothing rather than guess
    }
}

/// <summary>Turns the security facts into checkup items, in plain words.</summary>
public static class SecurityEvaluator
{
    public const string Source = "Microsoft's Windows Security documentation and general SOLIDWORKS support practice";

    public static IReadOnlyList<CheckResult> Evaluate(SecurityFacts f) =>
    [
        OtherAntivirus(f),
        FolderProtection(f),
        WorkFolders(f),
    ];

    private static CheckResult OtherAntivirus(SecurityFacts f)
    {
        const string plain = Analogies.Lead + "antivirus is a security guard who opens every box before it reaches your bench. " +
                             "Two guards checking the same boxes doubles the wait.";
        if (f.Antivirus is null)
            return new("other-antivirus", "antivirus programs", CheckStatus.Unknown, "Windows Security Center couldn't be asked which antivirus is installed.", "", Source, plain);
        var others = f.Antivirus.Where(a => !a.IsDefender).ToList();
        if (others.Count == 0)
            return new("other-antivirus", "antivirus programs", CheckStatus.Good, "Only Windows Defender is installed.", "", Source, plain);
        var on = others.Where(a => a.On).ToList();
        string names = string.Join(", ", others.Select(a => a.Name + (a.On ? "" : " (switched off)")));
        return new("other-antivirus", "antivirus programs", on.Count > 0 ? CheckStatus.Look : CheckStatus.Good,
            $"Antivirus besides Windows Defender: {names}. Antivirus that checks every file as SOLIDWORKS opens and saves it adds a wait to each one, " +
            "and two antivirus programs scanning at once makes it worse.",
            "Ask your IT team whether the antivirus can skip SOLIDWORKS's program folder, its temporary and backup folders, and your project " +
            "or vault folders (each antivirus product has its own exclusion settings). Only one antivirus should do real-time scanning.",
            Source, plain);
    }

    private static CheckResult FolderProtection(SecurityFacts f)
    {
        const string plain = Analogies.Lead + "Controlled folder access is a locked door on folders like Documents: only programs on the guest list " +
                             "may change files inside. If SOLIDWORKS isn't on the list, it can't save there.";
        const string fix = "Windows Security > Virus & threat protection > Manage ransomware protection > Allow an app through Controlled folder " +
                           "access > Add an allowed app > browse to SLDWORKS.exe (in the SOLIDWORKS program folder). Do the same for other " +
                           "SOLIDWORKS programs it blocks (the hang-up's details name them). Needs administrator rights; ask your IT team if it's locked.";
        switch (f.FolderProtection)
        {
            case null:
                return new("folder-protection", "Defender folder protection", CheckStatus.Unknown, "Couldn't read Defender's folder protection setting.", "", Source, plain);
            case 0:
                return new("folder-protection", "Defender folder protection", CheckStatus.Good, "Defender's Controlled folder access is off, so it can't block SOLIDWORKS saving.", "", Source, plain);
        }
        bool allowed = f.FolderProtectionAllowed.Any(a => a.EndsWith("SLDWORKS.exe", StringComparison.OrdinalIgnoreCase));
        string extra = f.ProtectedFolders.Count > 0 ? $" Also protected: {string.Join("; ", f.ProtectedFolders.Take(3))}." : "";
        if (f.FolderProtection == 2)
            return new("folder-protection", "Defender folder protection", CheckStatus.Good,
                "Defender's Controlled folder access is in audit mode: it notes what it would block but doesn't block it." + extra, "", Source, plain);
        return allowed
            ? new("folder-protection", "Defender folder protection", CheckStatus.Good,
                "Defender's Controlled folder access is on, and SOLIDWORKS is on its allowed list." + extra, "", Source, plain)
            : new("folder-protection", "Defender folder protection", CheckStatus.Problem,
                "Defender's Controlled folder access is on and SOLIDWORKS isn't on its allowed list, so SOLIDWORKS can be blocked from saving " +
                "into protected folders such as Documents and Desktop. Saves can fail or files can seem to vanish." + extra, fix, Source, plain);
    }

    private static CheckResult WorkFolders(SecurityFacts f)
    {
        const string plain = Analogies.Lead + "SOLIDWORKS needs a few drawers it can always put things in: scratch paper (temporary files), " +
                             "its own notes (settings) and spare copies (backups). A locked drawer means it stalls or fails when it reaches for one.";
        var blocked = f.WorkFolders.Where(w => w.Blocked is not null).ToList();
        if (blocked.Count == 0)
            return new("work-folders", "folders SOLIDWORKS writes to", CheckStatus.Good,
                "You can save in SOLIDWORKS's working folders (temporary files, settings, backups, Documents).", "", Source, plain, "SOLIDWORKS");
        return new("work-folders", "folders SOLIDWORKS writes to", CheckStatus.Problem,
            "Windows permissions block a folder SOLIDWORKS needs: " +
            string.Join("; ", blocked.Select(b => $"{b.What} ({b.Path}): {b.Blocked}")) +
            ". SOLIDWORKS can fail to save, lose auto-recover copies, or stall when it can't write its temporary files.",
            "Ask your IT team to give your Windows account permission to save in that folder, or point SOLIDWORKS somewhere you can save " +
            "(Tools > Options > System Options > File Locations or Backup/Recover).", Source, plain, "SOLIDWORKS");
    }
}
