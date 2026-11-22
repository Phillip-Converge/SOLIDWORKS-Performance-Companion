using SWCompanion.Core.Processes;

namespace SWCompanion.Core.Tests;

public class ProcessClassifierTests
{
    [Fact]
    public void Recognises_SolidWorks_itself_regardless_of_letter_case()
    {
        var p = ProcessClassifier.Classify("sldworks.EXE", null);
        Assert.NotNull(p);
        Assert.Equal(ProcessRole.Main, p.Role);
        Assert.True(p.Protected);
    }

    [Theory]
    [InlineData("FNPLicensingService64.exe")]
    [InlineData("SolidWorksLicensing.exe")]
    [InlineData("lmgrd.exe")]
    [InlineData("EdmServer.exe")]
    [InlineData("ViewServer.exe")]
    public void Licensing_and_PDM_client_are_always_protected(string exe)
    {
        var p = ProcessClassifier.Classify(exe, null);
        Assert.NotNull(p);
        Assert.True(p.Protected);
    }

    [Fact]
    public void Unknown_program_in_a_licensing_folder_is_protected()
    {
        var p = ProcessClassifier.Classify("newtool.exe",
            @"C:\Program Files\Common Files\Macrovision Shared\FlexNet Publisher\newtool.exe");
        Assert.NotNull(p);
        Assert.Equal(ProcessRole.Licensing, p.Role);
        Assert.True(p.Protected);
    }

    [Fact]
    public void Unknown_program_in_a_SolidWorks_folder_is_flagged_for_review_and_protected()
    {
        var p = ProcessClassifier.Classify("mystery.exe", @"C:\Program Files\SOLIDWORKS Corp 2025\Other\mystery.exe");
        Assert.NotNull(p);
        Assert.Equal(ProcessRole.Other, p.Role);
        Assert.True(p.Protected);
    }

    [Fact]
    public void Unrelated_programs_are_ignored()
    {
        Assert.Null(ProcessClassifier.Classify("notepad.exe", @"C:\Windows\System32\notepad.exe"));
        Assert.Null(ProcessClassifier.Classify("chrome.exe", null));
    }

    [Fact]
    public void Every_known_process_has_a_description()
    {
        Assert.All(KnownProcesses.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Scan_runs_without_errors_and_never_includes_unrelated_processes()
    {
        var seen = SolidWorksProcessFinder.Scan();
        Assert.DoesNotContain(seen, s => s.ExeName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase));
    }
}
