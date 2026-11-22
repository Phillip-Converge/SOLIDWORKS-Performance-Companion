namespace SWCompanion.Core.Checkup;

/// <summary>Raw facts the checkup is based on. Every field is read, never changed.</summary>
/// <param name="PowerPlanName">Name of the active Windows power plan, e.g. "High performance".</param>
/// <param name="PowerPlanGuid">ID of the active power plan.</param>
/// <param name="PowerModeAc">Windows 11 "Power mode" setting while plugged in, as an ID (empty = not used).</param>
/// <param name="MaxProcessorAc">Processor maximum state while plugged in, percent.</param>
/// <param name="MaxProcessorDc">Processor maximum state on battery, percent.</param>
/// <param name="HasBattery">True on laptops.</param>
/// <param name="OnBattery">True if running on battery right now.</param>
/// <param name="BatteryPercent">Charge left, or null if unknown.</param>
/// <param name="GdiLimit">GDIProcessHandleQuota in the main registry location (null = not set, Windows uses 10,000).</param>
/// <param name="UserLimit">USERProcessHandleQuota in the main registry location.</param>
/// <param name="GdiLimitWow">GDIProcessHandleQuota in the Wow6432Node copy.</param>
/// <param name="UserLimitWow">USERProcessHandleQuota in the Wow6432Node copy.</param>
/// <param name="PageFileAutomatic">True if Windows manages the page file size.</param>
/// <param name="PageFileDescription">The page file setting as Windows stores it.</param>
/// <param name="PageFileUsagePercent">The "Paging File % Usage" counter.</param>
public sealed record CheckupFacts(
    string? PowerPlanName,
    Guid? PowerPlanGuid,
    Guid? PowerModeAc,
    int? MaxProcessorAc,
    int? MaxProcessorDc,
    bool HasBattery,
    bool OnBattery,
    int? BatteryPercent,
    int? GdiLimit,
    int? UserLimit,
    int? GdiLimitWow,
    int? UserLimitWow,
    bool? PageFileAutomatic,
    string? PageFileDescription,
    double? PageFileUsagePercent);

public enum CheckStatus { Good, Look, Problem, Unknown }

/// <param name="Id">Internal name.</param>
/// <param name="Title">Short name of the check.</param>
/// <param name="Status">Good (green), Look (yellow, worth checking), Problem (red), Unknown (grey).</param>
/// <param name="Finding">What was found on this PC, in plain English.</param>
/// <param name="HowToFix">Plain-English steps the owner can follow by hand. Empty when nothing needs doing.</param>
/// <param name="Source">Where the advice comes from.</param>
/// <param name="Plain">An everyday comparison explaining what this setting is ("Think of it like this...").</param>
/// <param name="Group">Which part of the checkup it belongs to: "Windows", "SOLIDWORKS" or "PDM client".</param>
public sealed record CheckResult(string Id, string Title, CheckStatus Status, string Finding, string HowToFix, string Source,
                                 string Plain = "", string Group = "Windows");
