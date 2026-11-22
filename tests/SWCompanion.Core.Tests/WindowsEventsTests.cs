using SWCompanion.Core.Events;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class WindowsEventsTests
{
    private static readonly DateTime T = new(2026, 3, 4, 10, 0, 0);

    private static WinEvent E(string provider, int id, string msg, int level = 2, string log = "System") => new(T, log, provider, id, level, msg);

    [Fact]
    public void SolidWorksHangIsSeriousAndNamedPlainly()
    {
        var f = WinEventReader.Read(E("Application Hang", 1002,
            "The program SLDWORKS.exe version 34.5.2.0 stopped interacting with Windows and was closed.", log: "Application"))!;
        Assert.Equal("win-hang", f.Kind);
        Assert.True(f.Serious);
        Assert.True(f.SolidWorks);
        Assert.Contains("SOLIDWORKS stopped responding", f.Line);
    }

    [Fact]
    public void OtherProgramHangIsLoggedButNotSerious()
    {
        var f = WinEventReader.Read(E("Application Hang", 1002, "The program notepad.exe version 1.0 stopped interacting with Windows.", log: "Application"))!;
        Assert.False(f.Serious);
        Assert.Contains("notepad.exe", f.Line);
    }

    [Fact]
    public void CrashNamesTheFailingPart()
    {
        var f = WinEventReader.Read(E("Application Error", 1000,
            "Faulting application name: SLDWORKS.exe, version: 34.5.2.0\r\nFaulting module name: example.dll, version: 1.0", log: "Application"))!;
        Assert.Equal("win-crash", f.Kind);
        Assert.True(f.Serious);
        Assert.Contains("example.dll", f.Line);
    }

    [Fact]
    public void PdmCrashInsideExplorerCountsAsSolidWorks()
    {
        var f = WinEventReader.Read(E("Application Error", 1000,
            "Faulting application name: explorer.exe, version: 10.0\r\nFaulting module name: EdmShellExt.dll, version: 1.0", log: "Application"))!;
        Assert.True(f.SolidWorks);
        Assert.Contains("File Explorer", f.Line);
    }

    [Theory]
    [InlineData("Display", 4101, "Display driver nvlddmkm stopped responding and has successfully recovered.", 3)]
    [InlineData("nvlddmkm", 153, "The description for Event ID 153 from source nvlddmkm cannot be found.", 2)]
    public void GraphicsDriverResets(string provider, int id, string msg, int level)
    {
        var f = WinEventReader.Read(E(provider, id, msg, level))!;
        Assert.Equal("win-gpu", f.Kind);
        Assert.True(f.Serious);
    }

    [Fact]
    public void LateCrashReportsAreQuietAndDatedNotNewProblems()
    {
        var gpu = WinEventReader.Read(E("Windows Error Reporting", 1001,
            @"Fault bucket , type 0 Event Name: LiveKernelEvent P1: 141 P2: ... Attached files: \\?\C:\WINDOWS\LiveKernelReports\WATCHDOG-20261119-1530.dmp", 4, "Application"))!;
        Assert.Equal("win-report", gpu.Kind);
        Assert.False(gpu.Serious);
        Assert.Contains("earlier graphics driver reset (from 19 Nov 2026)", gpu.Line);

        var blue = WinEventReader.Read(E("Windows Error Reporting", 1001,
            @"Fault bucket , type 0 Event Name: BlueScreen P1: 9f Attached files: \\?\C:\WINDOWS\Minidump\071426-19500-01.dmp", 4, "Application"))!;
        Assert.False(blue.Serious);
        Assert.Contains("earlier blue screen", blue.Line);
        Assert.Contains("14 Jul 2026", blue.Line);
    }

    [Fact]
    public void DiskRetryAndControllerReset()
    {
        Assert.Equal("win-disk", WinEventReader.Read(E("disk", 153, @"The IO operation at logical block address 0x1 for Disk 0 (PDO name: \Device\00000035) was retried.", 3))!.Kind);
        var reset = WinEventReader.Read(E("stornvme", 129, @"Reset to device, \Device\RaidPort0, was issued.", 3))!;
        Assert.Contains("controller had to be reset", reset.Line);
    }

    [Fact]
    public void LowMemoryListsTheBiggestUsers()
    {
        var f = WinEventReader.Read(E("Microsoft-Windows-Resource-Exhaustion-Detector", 2004,
            "Windows successfully diagnosed a low virtual memory condition. The following programs consumed the most virtual memory: " +
            "SLDWORKS.exe (1234) consumed 21474836480 bytes, chrome.exe (55) consumed 1073741824 bytes.", 3))!;
        Assert.Equal("win-memory", f.Kind);
        Assert.Contains("SLDWORKS.exe (20.0 GB)", f.Detail);
        Assert.True(f.SolidWorks);
    }

    [Fact]
    public void NetworkDropAndSlowNameLookup()
    {
        Assert.Equal("win-network", WinEventReader.Read(E("e1dexpress", 27, "Intel(R) Ethernet Connection: Network link is disconnected.", 3))!.Kind);
        var dns = WinEventReader.Read(E("Microsoft-Windows-DNS-Client", 1014, "Name resolution for the name server01.example.com timed out after none of the configured DNS servers responded.", 3))!;
        Assert.False(dns.Serious);
        Assert.Contains("server01.example.com", dns.Detail);
    }

    [Fact]
    public void UnrelatedEventsAreIgnored()
    {
        Assert.Null(WinEventReader.Read(E("Service Control Manager", 7036, "The Print Spooler service entered the running state.", 4)));
        Assert.Null(WinEventReader.Read(E("Windows Error Reporting", 1001, "Fault bucket , type 0 Event Name: APPCRASH", 4, "Application")));
    }

    [Fact]
    public void FreezeExplanationListsWindowsEventsNearbyFirst()
    {
        var freeze = new Moment(T, TimeSpan.FromSeconds(12), "freeze", "SOLIDWORKS stopped responding for 12 s", "", true);
        var gpu = WinEventReader.Read(E("Display", 4101, "Display driver stopped responding and has successfully recovered.", 3))! with { Time = T.AddSeconds(5) };
        var far = gpu with { Time = T.AddMinutes(10) };
        var x = HangExplainer.For(freeze, [gpu, far]);
        Assert.Single(x.WindowsSaw);
        Assert.Contains("graphics driver", x.WindowsSaw[0]);
        Assert.StartsWith("Start with what Windows recorded", x.TryThis[0]);
        Assert.Contains("lasted 12 s", x.When);
    }

    [Theory]
    [InlineData("freeze")] [InlineData("box")] [InlineData("slow-open")] [InlineData("slow-sheet")] [InlineData("slow-save")]
    [InlineData("slow-rebuild")] [InlineData("antivirus")] [InlineData("win-hang")] [InlineData("win-crash")] [InlineData("win-gpu")]
    [InlineData("win-disk")] [InlineData("win-memory")] [InlineData("win-network")] [InlineData("win-throttle")] [InlineData("something-new")]
    public void EveryKindGetsAnExplanationAndSomethingToTry(string kind)
    {
        var x = HangExplainer.For(new Moment(T, TimeSpan.Zero, kind, "Title", "Detail.", true), []);
        Assert.False(string.IsNullOrWhiteSpace(x.WhatHappened));
        Assert.False(string.IsNullOrWhiteSpace(x.WhyItMatters));
        Assert.NotEmpty(x.TryThis);
        Assert.NotEmpty(x.LookFurther);
    }
}
