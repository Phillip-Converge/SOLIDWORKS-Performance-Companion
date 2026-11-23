using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SWCompanion.Core.Explain;

namespace SWCompanion.Core.Checkup;

/// <summary>The facts needed to compare this PC with SOLIDWORKS's published system requirements. All read, never changed.</summary>
/// <param name="EditionId">Windows' EditionID, e.g. "Professional", "Enterprise", or "Core" for Home.</param>
/// <param name="WindowsBuild">Windows build number: 22000 and above is Windows 11.</param>
/// <param name="InstalledRamGb">Memory installed in the PC, in GB.</param>
/// <param name="SystemDriveIsSsd">True for an SSD, false for a spinning hard disk, null if Windows didn't say.</param>
/// <param name="IsArm">True on an ARM processor (SOLIDWORKS needs Intel or AMD 64-bit).</param>
/// <param name="SolidWorksYear">The newest SOLIDWORKS installed, e.g. 2026, or null.</param>
public sealed record RequirementsFacts(string? EditionId, int? WindowsBuild, double? InstalledRamGb, bool? SystemDriveIsSsd, bool IsArm, int? SolidWorksYear);

/// <summary>
/// Compares this PC with SOLIDWORKS's published system requirements: supported Windows edition and version, memory
/// (16 GB minimum, 32 GB recommended), an SSD, and an Intel/AMD 64-bit processor. Read only.
/// </summary>
public static class Requirements
{
    public const string Source = "SOLIDWORKS system requirements (solidworks.com/support/system-requirements)";
    public const double MinimumRamGb = 16, RecommendedRamGb = 32;
    /// <summary>The first SOLIDWORKS release that no longer supports Windows 10 (support ended with 2025 SP5).</summary>
    public const int FirstYearWithoutWindows10 = 2026;

    // ---- reading ----

    public static RequirementsFacts Read()
    {
        string? edition = null;
        int? build = null;
        try
        {
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            edition = nt?.GetValue("EditionID") as string;
            if (nt?.GetValue("CurrentBuildNumber") is string b && int.TryParse(b, out var n)) build = n;
        }
        catch { }
        build ??= Environment.OSVersion.Version.Build;
        return new RequirementsFacts(edition, build, ReadInstalledRam(), ReadSystemDriveIsSsd(),
            RuntimeInformation.OSArchitecture is Architecture.Arm64 or Architecture.Arm, ReadSolidWorksYear());
    }

    /// <summary>Memory actually fitted (Windows' "installed RAM"), not the slightly smaller amount it can use.</summary>
    private static double? ReadInstalledRam()
    {
        try { return GetPhysicallyInstalledSystemMemory(out ulong kb) ? kb / 1024.0 / 1024.0 : null; }
        catch { return null; }
    }

    /// <summary>Asks Windows' storage information (no administrator needed) what kind of disk holds the Windows drive.</summary>
    private static bool? ReadSystemDriveIsSsd()
    {
        try
        {
            char letter = char.ToUpperInvariant(Environment.GetFolderPath(Environment.SpecialFolder.Windows)[0]);
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            uint? disk = null;
            using (var parts = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'")))
                foreach (ManagementObject p in parts.Get()) { disk = Convert.ToUInt32(p["DiskNumber"]); break; }
            if (disk is null) return null;
            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"));
            foreach (ManagementObject d in disks.Get())
            {
                // MediaType: 3 = hard disk, 4 = SSD, 0 = not said. BusType 17 = NVMe, which is always solid state.
                int media = Convert.ToInt32(d["MediaType"]), bus = Convert.ToInt32(d["BusType"]);
                if (media == 4 || bus == 17) return true;
                if (media == 3) return false;
                return null;
            }
        }
        catch { }
        return null;
    }

    private static int? ReadSolidWorksYear()
    {
        try
        {
            using var sw = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks");
            return sw?.GetSubKeyNames()
                .Select(n => System.Text.RegularExpressions.Regex.Match(n, @"^SOLIDWORKS (20\d\d)$"))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(y => { using var s = sw.OpenSubKey($@"SOLIDWORKS {y}\Setup"); return s?.GetValue("SolidWorks Folder") is string; })
                .OrderByDescending(y => y)
                .Cast<int?>()
                .FirstOrDefault();
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    // ---- judging ----

    /// <summary>Windows Home's EditionIDs all start with "Core" (Core, CoreN, CoreSingleLanguage, CoreCountrySpecific).</summary>
    public static bool IsHome(string? editionId) => editionId?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true;

    public static CheckResult Evaluate(RequirementsFacts? f)
    {
        const string id = "requirements", title = "SOLIDWORKS system requirements";
        const string plain = Analogies.Lead + "SOLIDWORKS publishes the PC setups it tests and supports, like a car maker's " +
                             "list of approved tyres. Off the list it may still run, but if something odd happens, support can ask " +
                             "you to show the problem on a supported setup before they can help.";
        if (f is null)
            return new(id, title, CheckStatus.Unknown, "This PC's details couldn't be read.", "", Source, plain);

        var problems = new List<string>();
        var looks = new List<string>();
        var fixes = new List<string>();
        var good = new List<string>();
        bool win11 = f.WindowsBuild >= 22000;
        string windows = (f.WindowsBuild is null ? "Windows" : win11 ? "Windows 11" : "Windows 10") + (IsHome(f.EditionId) ? " Home" : "");

        if (f.IsArm)
        {
            problems.Add("This PC has an ARM processor; SOLIDWORKS needs an Intel or AMD 64-bit processor.");
            fixes.Add("SOLIDWORKS isn't supported on ARM processors. It needs a PC with an Intel or AMD 64-bit processor.");
        }

        if (f.WindowsBuild is not null && !win11)
        {
            if (f.SolidWorksYear >= FirstYearWithoutWindows10)
            {
                problems.Add($"SOLIDWORKS {f.SolidWorksYear} isn't supported on Windows 10 (support ended with SOLIDWORKS 2025 SP5).");
                fixes.Add("Upgrade this PC to Windows 11 (Settings > Windows Update shows whether it qualifies; your IT team can help).");
            }
            else
            {
                looks.Add("This PC runs Windows 10. SOLIDWORKS 2026 and later need Windows 11, and Microsoft's own support for Windows 10 has ended.");
                fixes.Add("Plan the move to Windows 11 before upgrading SOLIDWORKS to 2026 or later.");
            }
        }

        if (IsHome(f.EditionId))
        {
            looks.Add($"This PC runs {windows}. SOLIDWORKS doesn't support Windows Home editions, so support may ask you to reproduce a problem on Windows Pro or Enterprise first.");
            fixes.Add("Upgrade to Windows Pro: Settings > System > Activation > Upgrade your edition of Windows (it's paid; your files and programs stay).");
        }

        if (f.InstalledRamGb is { } ram)
        {
            string amount = $"{Math.Round(ram):0} GB of memory";
            if (ram < MinimumRamGb - 0.5)
            {
                problems.Add($"This PC has {amount}; SOLIDWORKS needs at least {MinimumRamGb:0} GB ({RecommendedRamGb:0} GB recommended).");
                fixes.Add($"Add memory: {RecommendedRamGb:0} GB is the recommended amount. Your IT team or the PC maker can say which memory fits.");
            }
            else if (ram < RecommendedRamGb - 0.5)
            {
                looks.Add($"This PC has {amount}: enough to run SOLIDWORKS, but {RecommendedRamGb:0} GB is recommended, and large assemblies and drawings need it.");
                fixes.Add($"Consider adding memory to reach {RecommendedRamGb:0} GB, especially for large assemblies.");
            }
            else good.Add(amount);
        }

        if (f.SystemDriveIsSsd == false)
        {
            looks.Add("Windows is on a spinning hard disk. SOLIDWORKS recommends an SSD: opening, saving and starting up are much faster on one.");
            fixes.Add("Move Windows and SOLIDWORKS to an SSD (an IT job). If PDM is used, keep the vault view on the SSD too.");
        }
        else if (f.SystemDriveIsSsd == true) good.Add("an SSD");

        if (f.WindowsBuild is not null && win11 && !IsHome(f.EditionId)) good.Insert(0, windows);
        else if (f.WindowsBuild is not null && win11) good.Insert(0, "Windows 11");

        string fixText = string.Join(" ", fixes.Select((x, i) => $"{i + 1}. {x}"));
        if (problems.Count > 0)
            return new(id, title, CheckStatus.Problem, string.Join(" ", problems.Concat(looks)), fixText, Source, plain);
        if (looks.Count > 0)
            return new(id, title, CheckStatus.Look, string.Join(" ", looks) + (good.Count > 0 ? $" Fine: {string.Join(", ", good)}." : ""), fixText, Source, plain);
        return new(id, title, CheckStatus.Good,
            good.Count > 0 ? $"This PC meets SOLIDWORKS's published requirements: {string.Join(", ", good)}." : "Nothing here falls outside SOLIDWORKS's published requirements.",
            "", Source, plain);
    }
}
