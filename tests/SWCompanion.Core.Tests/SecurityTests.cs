using SWCompanion.Core.Antivirus;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Events;

namespace SWCompanion.Core.Tests;

public class SecurityTests
{
    private static readonly DateTime T = new(2026, 3, 4, 10, 0, 0);
    private const string Defender = "Microsoft-Windows-Windows Defender";

    private static WinEvent D(int id, string msg) => new(T, WinEventReader.DefenderLog, Defender, id, 3, msg);

    [Fact]
    public void FolderProtectionBlockingSolidWorksIsSerious()
    {
        var f = WinEventReader.Read(D(1123,
            "Controlled Folder Access blocked an unauthorized app from making changes.\r\n Detection time: x\r\n Path: C:\\Users\\someone\\Documents\\Bracket.SLDPRT\r\n Process Name: C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\SLDWORKS.exe\r\n"))!;
        Assert.Equal("win-blocked", f.Kind);
        Assert.True(f.Serious);
        Assert.Contains("blocked SOLIDWORKS from changing", f.Line);
        Assert.Contains("Bracket.SLDPRT", f.Line);
    }

    [Fact]
    public void AuditModeIsNotedButNotSerious()
    {
        var f = WinEventReader.Read(D(1124,
            " Path: C:\\Users\\someone\\Documents\\Bracket.SLDPRT\r\n Process Name: C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\SLDWORKS.exe\r\n"))!;
        Assert.False(f.Serious);
        Assert.Contains("would have blocked", f.Line);
    }

    [Fact]
    public void AQuarantinedMacroIsSeriousAndOtherThreatsAreNot()
    {
        var macro = WinEventReader.Read(D(1117, " Name: Trojan:Example\r\n Path: file:_C:\\Macros\\Export.swp\r\n Action: Quarantine\r\n"))!;
        Assert.Equal("win-quarantine", macro.Kind);
        Assert.True(macro.Serious);
        Assert.Contains("C:\\Macros\\Export.swp", macro.Line);
        Assert.Contains("Trojan:Example", macro.Line);

        var other = WinEventReader.Read(D(1116, " Name: Adware:Example\r\n Path: file:_C:\\Downloads\\setup.exe\r\n"))!;
        Assert.False(other.Serious);
    }

    [Fact]
    public void ScansAreQuietLines()
    {
        var f = WinEventReader.Read(D(1000, "Microsoft Defender Antivirus scan has started.\r\n Scan Type: Antimalware\r\n Scan Parameters: Quick Scan\r\n"))!;
        Assert.Equal("win-scan", f.Kind);
        Assert.False(f.Serious);
        Assert.Contains("quick scan", f.Line);
    }

    private static SecurityFacts Facts(IReadOnlyList<AntivirusProduct>? av = null, int? cfa = 0, IReadOnlyList<string>? allowed = null,
        IReadOnlyList<WorkFolder>? folders = null) =>
        new(av ?? [new("Windows Defender", true, true)], cfa, allowed ?? [], [], folders ?? []);

    private static CheckResult Get(SecurityFacts f, string id) => SecurityEvaluator.Evaluate(f).Single(c => c.Id == id);

    [Fact]
    public void AnotherActiveAntivirusIsWorthALook()
    {
        Assert.Equal(CheckStatus.Good, Get(Facts(), "other-antivirus").Status);
        var c = Get(Facts(av: [new("Windows Defender", false, true), new("Example Antivirus", true, false)]), "other-antivirus");
        Assert.Equal(CheckStatus.Look, c.Status);
        Assert.Contains("Example Antivirus", c.Finding);
    }

    [Fact]
    public void FolderProtectionOnWithoutSolidWorksAllowedIsAProblem()
    {
        Assert.Equal(CheckStatus.Good, Get(Facts(cfa: 0), "folder-protection").Status);
        Assert.Equal(CheckStatus.Problem, Get(Facts(cfa: 1), "folder-protection").Status);
        Assert.Equal(CheckStatus.Good, Get(Facts(cfa: 1, allowed: [@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe"]), "folder-protection").Status);
        Assert.Equal(CheckStatus.Good, Get(Facts(cfa: 2), "folder-protection").Status);
        Assert.Equal(CheckStatus.Unknown, Get(Facts(cfa: null), "folder-protection").Status);
    }

    [Fact]
    public void ABlockedWorkingFolderIsNamed()
    {
        var c = Get(Facts(folders: [new("temporary files", @"C:\Temp", true, null), new("SOLIDWORKS settings", @"C:\Settings", true, "you don't have permission to save files here")]), "work-folders");
        Assert.Equal(CheckStatus.Problem, c.Status);
        Assert.Contains(@"SOLIDWORKS settings (C:\Settings)", c.Finding);
        Assert.DoesNotContain(@"C:\Temp", c.Finding);
    }

    [Fact]
    public void YourOwnTempFolderReadsAsWritable()
    {
        var f = SecurityReader.Check("temporary files", Path.GetTempPath());
        Assert.True(f.Exists);
        Assert.Null(f.Blocked);
    }
}
