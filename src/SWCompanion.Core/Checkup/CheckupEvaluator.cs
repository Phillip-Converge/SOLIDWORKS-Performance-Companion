using SWCompanion.Core.Explain;
using SWCompanion.Core.Gpu;

namespace SWCompanion.Core.Checkup;

/// <summary>
/// Turns checkup facts into plain-English verdicts and hand-fix steps.
/// Phase 1 only reports: nothing here changes a setting.
/// </summary>
public static class CheckupEvaluator
{
    public const string SupportPractice = "General SOLIDWORKS support practice";
    public const string Microsoft = "Microsoft's documented limits for these settings";

    /// <summary>Windows' value when the registry doesn't set one.</summary>
    public const int WindowsDefault = 10_000;
    /// <summary>Recommended values: Microsoft's maximum for each setting.</summary>
    public const int RecommendedGdi = 65_536;
    public const int RecommendedUser = 18_000;
    /// <summary>The Paging File counter should stay below this.</summary>
    public const double PageFileLimit = 75;

    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid ModeBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid ModeBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    public static IReadOnlyList<CheckResult> Evaluate(CheckupFacts f, GpuSnapshot? gpu) =>
    [
        PowerPlan(f) with { Plain = Analogies.Lead + Analogies.PowerPlan },
        ProcessorCap(f) with { Plain = Analogies.Lead + Analogies.ProcessorCap },
        Battery(f) with { Plain = Analogies.Lead + Analogies.Battery },
        HandleLimits(f) with { Plain = Analogies.Lead + Analogies.ObjectLimits + " " + Analogies.Registry },
        PageFile(f) with { Plain = Analogies.Lead + Analogies.PageFileSetting },
        FreeSpace(f),
        Checkup.Requirements.Evaluate(f.Requirements),
        Graphics(gpu) with { Plain = Analogies.Lead + Analogies.GraphicsCard, Group = "SOLIDWORKS" },
    ];

    /// <summary>Free space on the Windows drive: below this is a problem, below <see cref="FreeSpaceLook"/> worth a look.</summary>
    public const double FreeSpaceProblemGb = 10, FreeSpaceLookGb = 20;

    /// <summary>
    /// Free space on the Windows drive. Windows grows the page file there when memory runs short, and SOLIDWORKS writes its
    /// temporary and backup files there; a nearly full drive makes both slow or fail (Microsoft recommends leaving room for
    /// the page file to grow).
    /// </summary>
    private static CheckResult FreeSpace(CheckupFacts f)
    {
        const string id = "free-space", title = "free space on the Windows drive";
        const string plain = Analogies.Lead + "the Windows drive is the PC's workbench. When it's nearly full there's nowhere to " +
                             "spread out: Windows can't grow its memory overflow (the page file), and SOLIDWORKS has no room for its " +
                             "temporary and backup copies while it opens and saves.";
        const string fix =
            "1. Settings > System > Storage: look at what's using the space, and turn on Storage Sense or use \"Cleanup recommendations\". " +
            "2. Disk Cleanup (search for it, run it as administrator) > \"Clean up system files\": old Windows update files are often the biggest. " +
            "3. Empty the Recycle Bin and look through Downloads. " +
            "4. Old SOLIDWORKS download folders (Documents\\SOLIDWORKS Downloads) for versions no longer installed can go; keep the current one, it's needed to repair or change the installation. " +
            "5. With PDM: in the vault view, Tools > Clear Local Cache on folders you're not working in, after checking in your files there. " +
            "Ask your IT team before deleting anything you're unsure about.";
        if (f.SystemDriveFreeGb is not { } free || f.SystemDriveTotalGb is not { } total)
            return new(id, title, CheckStatus.Unknown, "The free space on the Windows drive couldn't be read.", "", SupportPractice, plain);
        string drive = f.SystemDrive ?? "The Windows drive";
        string amount = $"{drive} has {free:0.#} GB free of {total:0} GB";
        if (free < FreeSpaceProblemGb)
            return new(id, title, CheckStatus.Problem,
                $"{amount}. That's very little: Windows can't grow its page file when memory runs short, and saves and updates can fail.",
                fix, SupportPractice, plain);
        if (free < FreeSpaceLookGb)
            return new(id, title, CheckStatus.Look,
                $"{amount}. Keeping at least {FreeSpaceLookGb:0} GB free leaves room for the page file to grow and for SOLIDWORKS's temporary files.",
                fix, SupportPractice, plain);
        return new(id, title, CheckStatus.Good, $"{amount}.", "", SupportPractice, plain);
    }

    private const string PowerFix =
        "Plugged in, open Control Panel > Hardware and Sound > Power Options and choose \"High performance\" " +
        "(click \"Show additional plans\" if it's hidden). On many Windows 11 laptops that list is replaced by " +
        "Settings > System > Power & battery > Power mode: choose \"Best performance\".";

    private static CheckResult PowerPlan(CheckupFacts f)
    {
        const string title = "power plan";
        const string source = "Owner's field experience; Windows power settings";
        if (f.PowerPlanName is null && f.PowerPlanGuid is null)
            return new("power-plan", title, CheckStatus.Unknown, "Windows didn't report the active power plan.", PowerFix, source);

        string name = f.PowerPlanName ?? f.PowerPlanGuid.ToString()!;
        bool fastPlan = f.PowerPlanGuid == HighPerformance || f.PowerPlanGuid == UltimatePerformance
                        || name.Contains("high performance", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("ultimate", StringComparison.OrdinalIgnoreCase);
        if (fastPlan)
            return new("power-plan", title, CheckStatus.Good,
                $"\"{name}\" is active, so Windows lets the processor run at full speed.", "", source);

        if (f.PowerPlanGuid == PowerSaver)
            return new("power-plan", title, CheckStatus.Problem,
                $"\"{name}\" is active. It deliberately slows the processor to save power, which makes SOLIDWORKS noticeably slower.",
                PowerFix, source);

        if (f.PowerModeAc == ModeBestPerformance)
            return new("power-plan", title, CheckStatus.Good,
                $"\"{name}\" is active with Power mode set to \"Best performance\", which lets the processor run at full speed.", "", source);

        string mode = f.PowerModeAc == ModeBestEfficiency ? " with Power mode set to \"Best power efficiency\"" : "";
        return new("power-plan", title, CheckStatus.Look,
            $"\"{name}\"{mode} is active. It lets Windows hold the processor back to save power, so rebuilds and opens can take longer " +
            "than they need to. Many people never change this from the factory setting.",
            PowerFix, source);
    }

    private static CheckResult ProcessorCap(CheckupFacts f)
    {
        const string title = "processor speed limit";
        const string source = "Windows power plan setting \"Maximum processor state\"";
        const string fix =
            "Control Panel > Power Options > \"Change plan settings\" next to the active plan > \"Change advanced power settings\" > " +
            "Processor power management > Maximum processor state: set \"Plugged in\" to 100%.";
        if (f.MaxProcessorAc is null)
            return new("cpu-cap", title, CheckStatus.Unknown, "Windows didn't report the maximum processor state.", fix, source);
        if (f.MaxProcessorAc < 100)
            return new("cpu-cap", title, CheckStatus.Problem,
                $"The active plan caps the processor at {f.MaxProcessorAc}% when plugged in. The processor can never reach full speed, " +
                "and on many processors anything below 100% also switches off turbo boost.", fix, source);
        string battery = f.HasBattery && f.MaxProcessorDc < 100 ? $" On battery it's capped at {f.MaxProcessorDc}%." : "";
        return new("cpu-cap", title, CheckStatus.Good, $"No cap: the processor may run at 100% when plugged in.{battery}", "", source);
    }

    private static CheckResult Battery(CheckupFacts f)
    {
        const string title = "plugged in";
        const string source = "How laptops manage power";
        if (!f.HasBattery)
            return new("battery", title, CheckStatus.Good, "No battery (desktop PC), so this doesn't apply.", "", source);
        if (f.OnBattery)
            return new("battery", title, CheckStatus.Look,
                $"Running on battery{(f.BatteryPercent is { } p ? $" ({p}%)" : "")}. Laptops usually slow the processor and graphics " +
                "card down on battery, whatever the power plan says, so SOLIDWORKS will be slower until you plug in.",
                "Plug in the charger. A laptop's full charger (not a small USB-C one) is usually needed for full speed.", source);
        return new("battery", title, CheckStatus.Good, "Plugged in.", "", source);
    }

    private static CheckResult HandleLimits(CheckupFacts f)
    {
        const string title = "object limits";
        int gdi = f.GdiLimit ?? WindowsDefault, user = f.UserLimit ?? WindowsDefault;
        int gdiWow = f.GdiLimitWow ?? WindowsDefault, userWow = f.UserLimitWow ?? WindowsDefault;
        string source = $"{Microsoft}; {SupportPractice}";

        string found =
            $"Drawing objects (GDI): {gdi:N0}. Window objects (USER): {user:N0}. The recommended values are {RecommendedGdi:N0} and " +
            $"{RecommendedUser:N0}, Microsoft's maximums for these settings.";
        if (gdiWow != gdi || userWow != user)
            found += $" The second registry copy (Wow6432Node) says {gdiWow:N0} and {userWow:N0}. SOLIDWORKS is a 64-bit program, so the " +
                     "first location is the one that applies to it; setting both is the safe habit.";

        const string fix =
            "Try these first: close windows you don't need inside SOLIDWORKS, make sure the graphics card and driver are a " +
            "combination SOLIDWORKS supports, and install the latest SOLIDWORKS service pack. Only if warnings continue: in the " +
            "Registry Editor, under HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Windows (and the same path under " +
            "SOFTWARE\\Wow6432Node), set GDIProcessHandleQuota to 65536 and USERProcessHandleQuota to 18000 (Decimal). Back up the " +
            "registry first. Windows usually needs a restart before the new limits apply. A later version of this app will be able " +
            "to do this for you with an automatic backup and one-click undo.";

        if (gdi >= RecommendedGdi && user >= RecommendedUser)
            return new("handles", title, CheckStatus.Good, found, "", source);
        return new("handles", title, CheckStatus.Look, found, fix, source);
    }

    private static CheckResult PageFile(CheckupFacts f)
    {
        const string title = "page file";
        string source = SupportPractice;
        const string fix =
            "On Windows 10 and later the recommended setting is to let Windows manage the page file: Control Panel > System > Advanced " +
            "system settings > Performance Settings > Advanced > Virtual memory > Change, and tick \"Automatically manage paging file " +
            "size for all drives\". If the Paging File counter stays high, close programs, add RAM, or add a second fast drive for the page file.";

        string usage = f.PageFileUsagePercent is { } u ? $" The Paging File counter reads {u:0}% (it should stay below {PageFileLimit:0}%)." : "";
        if (f.PageFileUsagePercent >= PageFileLimit)
            return new("pagefile", title, CheckStatus.Problem,
                $"The page file is heavily used.{usage} Windows is leaning on the disk for memory, which slows everything down.", fix, source);
        if (f.PageFileAutomatic == false)
            return new("pagefile", title, CheckStatus.Look,
                $"The page file size has been set by hand ({f.PageFileDescription}).{usage} That's fine if it's large enough, but " +
                "Windows-managed is the recommended default.", fix, source);
        if (f.PageFileAutomatic is null)
            return new("pagefile", title, CheckStatus.Unknown, $"Windows didn't report the page file setting.{usage}", fix, source);
        return new("pagefile", title, CheckStatus.Good, $"Windows manages the page file size.{usage}", "", source);
    }

    private static CheckResult Graphics(GpuSnapshot? g)
    {
        const string title = "graphics card in use";
        const string source = "Windows graphics counters";
        const string fix =
            "Windows Settings > System > Display > Graphics: add SLDWORKS.exe and choose \"High performance\". If the graphics card " +
            "maker has a control panel (for example the NVIDIA Control Panel > Manage 3D settings > Program Settings), choose the " +
            "graphics card for SOLIDWORKS there too. Restart SOLIDWORKS afterwards.";
        if (g is null || !g.SolidWorksRunning)
            return new("graphics", title, CheckStatus.Unknown, "Open SOLIDWORKS to check which graphics it draws with.", "", source);
        var sw = g.SolidWorksAdapter;
        if (sw is null)
            return new("graphics", title, CheckStatus.Unknown, "SOLIDWORKS hasn't started drawing yet. Open a document to check.", "", source);
        if (sw.Adapter.Kind == GpuKind.Software)
            return new("graphics", title, CheckStatus.Problem,
                "SOLIDWORKS is drawing without a graphics card (Windows' software fallback). Usually a driver problem or SOLIDWORKS's " +
                "\"software OpenGL\" option being on.",
                "Check the graphics driver is installed and supported, and in SOLIDWORKS untick \"Use software OpenGL\" if it's " +
                "ticked (Tools > Options > System Options, on the Performance or Graphics page depending on version). Restart SOLIDWORKS.", source);
        if (sw.Adapter.Kind == GpuKind.BuiltIn && g.DedicatedCard is { } card)
            return new("graphics", title, CheckStatus.Problem,
                $"SOLIDWORKS is drawing with the built-in {sw.Adapter.Name}, not the {card.Adapter.Name}.", fix, source);
        return new("graphics", title, CheckStatus.Good, $"SOLIDWORKS is drawing with the {sw.Adapter.Name}.", "", source);
    }

    /// <summary>"All 6 checks look good" / "2 things to look at".</summary>
    public static string Summary(IReadOnlyList<CheckResult> results)
    {
        int problems = results.Count(r => r.Status == CheckStatus.Problem);
        int looks = results.Count(r => r.Status == CheckStatus.Look);
        int total = problems + looks;
        return total == 0
            ? $"All {results.Count(r => r.Status == CheckStatus.Good)} checks look good."
            : $"{total} thing{(total == 1 ? "" : "s")} to look at" + (problems > 0 ? $", {problems} likely slowing SOLIDWORKS down." : ".");
    }
}
