using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SWCompanion.Core.Processes;

/// <summary>
/// A SOLIDWORKS release and service pack, e.g. "SOLIDWORKS 2026 SP5.2".
/// SOLIDWORKS stamps its program file with a version like 34.5.2.0604:
/// 34 = the release (1992 + 34 = 2026), 5 = service pack 5, 2 = its ".2" update, 0604 = build.
/// </summary>
public sealed record SolidWorksVersion(int Year, int ServicePack, int Update, int Build)
{
    /// <summary>"SOLIDWORKS 2026 SP5.2".</summary>
    public string Name => $"SOLIDWORKS {Year} SP{ServicePack}.{Update}";
    /// <summary>"34.5.2.604", the number support and the knowledge base use.</summary>
    public string Number => $"{Year - 1992}.{ServicePack}.{Update}.{Build}";
    public override string ToString() => Name;

    /// <summary>Reads a file version such as "34.5.2.0604". Null if it doesn't look like SOLIDWORKS's.</summary>
    public static SolidWorksVersion? FromFileVersion(string? text)
    {
        var m = Regex.Match(text ?? "", @"^\s*(\d{2})\.(\d{1,2})\.(\d{1,2})(?:\.(\d+))?");
        if (!m.Success) return null;
        int major = int.Parse(m.Groups[1].Value);
        if (major < 20 || major > 60) return null; // 20 = 2012 ... well beyond today
        return new SolidWorksVersion(1992 + major, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
    }

    /// <summary>Reads names like "SOLIDWORKS PDM Client 2026 SP05.2" from Windows' installed-programs list.</summary>
    public static SolidWorksVersion? FromDisplayName(string? name)
    {
        var m = Regex.Match(name ?? "", @"(20\d\d)\s+SP0*(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return new SolidWorksVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0, 0);
    }

    /// <summary>The version of a SOLIDWORKS program file (the running SLDWORKS.exe, ideally).</summary>
    public static SolidWorksVersion? FromProgram(string? exePath)
    {
        try
        {
            return exePath is not null && File.Exists(exePath)
                ? FromFileVersion(FileVersionInfo.GetVersionInfo(exePath).FileVersion)
                : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>What's installed: SOLIDWORKS itself and, if present, the PDM client. Read only.</summary>
public sealed record InstalledVersions(SolidWorksVersion? SolidWorks, string? SolidWorksPath, SolidWorksVersion? PdmClient)
{
    /// <summary>Plain-English line for the screen.</summary>
    public string Describe(bool running)
    {
        if (SolidWorks is null) return "Couldn't find which SOLIDWORKS version is installed.";
        string text = (running ? "Running " : "Installed: ") + $"{SolidWorks.Name} (version {SolidWorks.Number})";
        if (PdmClient is not null)
        {
            text += $". PDM client: {PdmClient.Name.Replace("SOLIDWORKS ", "")}";
            if (PdmClient.Year != SolidWorks.Year || PdmClient.ServicePack != SolidWorks.ServicePack || PdmClient.Update != SolidWorks.Update)
                text += " (a different release or service pack from SOLIDWORKS; worth checking they were meant to differ)";
        }
        return text + ".";
    }

    /// <summary>
    /// Reads the running program's file if there is one (most accurate), otherwise the newest
    /// install SOLIDWORKS recorded in the registry, plus the PDM client from Windows' program list.
    /// </summary>
    public static InstalledVersions Read(string? runningExePath)
    {
        string? path = runningExePath;
        var sw = SolidWorksVersion.FromProgram(path);
        if (sw is null)
        {
            path = NewestInstallFolder() is { } folder ? Path.Combine(folder, "SLDWORKS.exe") : null;
            sw = SolidWorksVersion.FromProgram(path);
        }
        return new InstalledVersions(sw, path, PdmClientVersion());
    }

    private static string? NewestInstallFolder()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks");
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames()
                         .Where(n => Regex.IsMatch(n, @"^SOLIDWORKS \d{4}$", RegexOptions.IgnoreCase))
                         .OrderByDescending(n => n))
            {
                using var setup = root.OpenSubKey(name + @"\Setup");
                if (setup?.GetValue("SolidWorks Folder") is string folder && Directory.Exists(folder)) return folder;
            }
        }
        catch (Exception) { }
        return null;
    }

    private static SolidWorksVersion? PdmClientVersion()
    {
        foreach (var uninstall in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(uninstall);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(sub);
                    if (item?.GetValue("DisplayName") is string name &&
                        name.StartsWith("SOLIDWORKS PDM Client", StringComparison.OrdinalIgnoreCase) &&
                        SolidWorksVersion.FromDisplayName(name) is { } v)
                        return v;
                }
            }
            catch (Exception) { }
        }
        return null;
    }
}
