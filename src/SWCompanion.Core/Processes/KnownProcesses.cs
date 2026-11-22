namespace SWCompanion.Core.Processes;

/// <summary>What kind of program a process is, as far as SW Companion cares.</summary>
public enum ProcessRole
{
    Main,       // SOLIDWORKS itself
    Helper,     // SOLIDWORKS helper programs
    Pdm,        // SOLIDWORKS PDM client
    Licensing,  // licensing: never touched
    Other,      // in a SOLIDWORKS folder but not yet reviewed with the owner
}

/// <param name="ExeName">File name, e.g. "SLDWORKS.exe".</param>
/// <param name="Role">What kind of program it is.</param>
/// <param name="Description">Plain-English description. Where Dassault does not
/// document the job, the text says so instead of guessing.</param>
/// <param name="Protected">True for the processes the safety rules say must never be stopped.</param>
public sealed record KnownProcess(string ExeName, ProcessRole Role, string Description, bool Protected);

/// <summary>
/// The SOLIDWORKS-related processes SW Companion recognises. Descriptions are
/// based on each program's own Windows file description (set by the vendor).
/// This list is reviewed and approved by the owner. Being on it does NOT mean a
/// process may be stopped: nothing may be stopped in Phase 1.
/// </summary>
public static class KnownProcesses
{
    public static readonly IReadOnlyList<KnownProcess> All =
    [
        // SOLIDWORKS itself
        new("SLDWORKS.exe", ProcessRole.Main, "SOLIDWORKS itself.", Protected: true),

        // Helpers
        new("sldworks_fs.exe", ProcessRole.Helper,
            "SOLIDWORKS background helper. Dassault labels it only \"SldWorks_fs\"; its exact job is not documented. It can stay running after SOLIDWORKS closes.",
            Protected: false),
        new("sldProcMon.exe", ProcessRole.Helper,
            "SOLIDWORKS Resource Monitor. Watches system resources such as memory while SOLIDWORKS runs.",
            Protected: false),
        new("sldsurfacing.exe", ProcessRole.Helper,
            "SOLIDWORKS helper Dassault labels \"solidsurfacing application\". Its exact job is not documented.",
            Protected: false),
        new("swCefSubProc.exe", ProcessRole.Helper,
            "Built-in web browser (Chromium) behind the Task Pane, Welcome dialog and other web panels. Several copies are normal.",
            Protected: false),
        new("sldbgproc.exe", ProcessRole.Helper,
            "SOLIDWORKS background process Dassault labels \"sldbgproc\". Its exact job is not documented.",
            Protected: false),
        new("swxdesktophelper.exe", ProcessRole.Helper, "SOLIDWORKS Desktop Helper.", Protected: false),
        new("swScheduler.exe", ProcessRole.Helper, "SOLIDWORKS Task Scheduler.", Protected: false),
        new("sldBenchmark.exe", ProcessRole.Helper, "SOLIDWORKS Performance Test.", Protected: false),

        // PDM client: never stopped (safety rule 1)
        new("EdmServer.exe", ProcessRole.Pdm, "SOLIDWORKS PDM client background process.", Protected: true),
        new("ViewServer.exe", ProcessRole.Pdm, "SOLIDWORKS PDM client process (Dassault labels it only \"SOLIDWORKS PDM\").", Protected: true),
        new("AddInSrv.exe", ProcessRole.Pdm, "SOLIDWORKS PDM client process (Dassault labels it only \"SOLIDWORKS PDM\").", Protected: true),
        new("TaskExecutor.exe", ProcessRole.Pdm, "SOLIDWORKS PDM task runner.", Protected: true),

        // Licensing: never stopped (safety rule 1)
        new("FNPLicensingService64.exe", ProcessRole.Licensing, "FlexNet Licensing Service (licensing).", Protected: true),
        new("FNPLicensingService.exe", ProcessRole.Licensing, "FlexNet Licensing Service (licensing).", Protected: true),
        new("SolidWorksLicensing.exe", ProcessRole.Licensing, "SolidWorks Licensing Service (licensing).", Protected: true),
        new("lmgrd.exe", ProcessRole.Licensing, "SolidNetWork License Manager (licensing).", Protected: true),
        new("sw_d.exe", ProcessRole.Licensing, "SolidNetWork License Manager vendor daemon (licensing).", Protected: true),
    ];
}
