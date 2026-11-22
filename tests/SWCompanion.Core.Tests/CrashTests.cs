using SWCompanion.Core.Crash;

namespace SWCompanion.Core.Tests;

public class CrashTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);

    // A made-up performance log in the shape SOLIDWORKS writes.
    private const string Log = """
        <EVENT> [EVT_SWITCH_TO_ASSEMBLY]
        <PROCMEM> CMD:DocOpen ATTR:PrivateBytes=2147483648 ATTR:GDIHandlesTotal=16384 ATTR:GDIHandlesUsed=15900
        <EVENT> [EVT_SWITCH_TO_PART]
        <SWEXH>
        <MINIDUMP> "00000000-0000-0000-0000-000000000000"
        <EXCEPTION> "Access Violation at 00007FFC00000000 (virtual address 0), attempt to read from memory"
        <AD5> auExample_c::Release;auExample_c::GetDocuments;auExample_c::Release;
        <RAWSTACK> ntdll:7FFC0:0001,PSKERNEL:7FFC1:0002,PSKERNEL:7FFC2:0003,SLDMODU:5143:0004;
        <UNHANDLED>
        </SWEXH>
        <PROCMEM> CMD:AppFinish ATTR:PrivateBytes=999 ATTR:GDIHandlesUsed=1
        <RUNTIME>1079
        """;

    [Fact]
    public void A_crash_log_is_read_into_plain_facts()
    {
        var c = CrashReader.Parse(T0, "34.3.0.0150", @"C:\Example\crash", Log, hasDump: true);
        Assert.Equal("Access Violation at 00007FFC00000000 (virtual address 0), attempt to read from memory", c.Error);
        Assert.Equal(new[] { "ntdll", "PSKERNEL", "SLDMODU" }, c.Modules);
        Assert.Equal("PSKERNEL", c.Lead); // Windows' own library is skipped as the lead
        Assert.Equal(new[] { "switched to an assembly", "switched to a part" }, c.LastActions);
        Assert.Equal(2147483648, c.MemoryBytes); // the reading before the crash, not the one after
        Assert.Equal((15900, 16384), (c.GdiUsed!.Value, c.GdiLimit!.Value));
        Assert.Equal(1079, c.SessionSeconds);
        Assert.Equal(2, c.AddinCalls.Count);
    }

    [Fact]
    public void The_summary_says_where_and_what_in_plain_words()
    {
        var c = CrashReader.Parse(T0, "34.3.0.0150", "x", Log, true);
        Assert.Equal("SOLIDWORKS 2026 SP3.0 crashed in PSKERNEL (Parasolid, the geometry engine that calculates the 3D shapes of parts), " +
                     "right after you switched to a part. In plain words: it tried to use a piece of memory it wasn't allowed to " +
                     "(a program error, usually set off by a particular file, feature or driver).", CrashExplainer.Summary(c));
    }

    [Fact]
    public void Unknown_modules_are_shown_as_is_and_never_guessed()
    {
        var c = CrashReader.Parse(T0, "34.3.0.0150", "x", "<SWEXH>\n<RAWSTACK> examplemod:1:2;\n", true);
        Assert.Contains("in examplemod (not in the Companion's list yet)", CrashExplainer.Summary(c));
        Assert.Contains("didn't log the error", CrashExplainer.Summary(CrashReader.Parse(T0, "34.3.0.0150", "x", null, true)));
    }

    [Theory]
    [InlineData("Exception e0434352", ".NET")]
    [InlineData("Exception c000000d", "invalid value")]
    [InlineData("Exception c00000fd", "stack overflow")]
    public void Crash_codes_are_explained(string error, string expected) => Assert.Contains(expected, CrashModules.ErrorMeaning(error));

    [Fact]
    public void The_same_module_crashing_twice_is_a_pattern_with_advice()
    {
        var a = CrashReader.Parse(T0, "34.3.0.0150", "a", Log, true);
        var b = CrashReader.Parse(T0.AddDays(3), "34.3.0.0150", "b", Log, true);
        var other = CrashReader.Parse(T0.AddDays(5), "34.3.0.0150", "c", "<SWEXH>\n<RAWSTACK> clr:1:2;\n", true);
        Assert.Null(CrashExplainer.Pattern([a, other]));
        var p = CrashExplainer.Pattern([a, b, other])!.Value;
        Assert.Equal("SOLIDWORKS keeps crashing in the same place (2 times, PSKERNEL: Parasolid)", p.Title);
        Assert.Contains("Most of them came right after you switched to a part", p.Why);
        Assert.Contains("Tools > Evaluate > Check", p.Try);
    }

    [Fact]
    public void Crash_records_are_found_by_folder_and_only_when_there_is_a_dump()
    {
        var root = Path.Combine(Path.GetTempPath(), "swc-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var crash = Directory.CreateDirectory(Path.Combine(root, "20260105090000_34.3.0.0150", "CXPD", "x")).Parent!.Parent!.FullName;
            File.WriteAllText(Path.Combine(crash, "CXPD", "x", "example.dmp"), "not a real dump");
            File.WriteAllText(Path.Combine(crash, "SolidWorksPerformance.log"), Log);
            Directory.CreateDirectory(Path.Combine(root, "20260106090000_34.3.0.0150")); // a session with no dump: not a crash
            Directory.CreateDirectory(Path.Combine(root, "SOLIDWORKS 2026"));             // not a session folder at all

            var all = CrashReader.ReadAll(root);
            var c = Assert.Single(all);
            Assert.Equal(new DateTime(2026, 1, 5, 9, 0, 0), c.When);
            Assert.Equal("PSKERNEL", c.Lead);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Ending_is_told_apart_crash_frozen_or_closed()
    {
        var started = T0;
        var crash = CrashReader.Parse(started.AddSeconds(30), "34.3.0.0150", "x", Log, true); // folder named after the session start

        var w = new CrashWatch();
        w.Observe(T0.AddMinutes(10), 42, started, frozen: false);
        w.Observe(T0.AddMinutes(11), null, null, false);
        Assert.Null(w.Decide(T0.AddMinutes(11).AddSeconds(5), () => [crash])); // still waiting for SOLIDWORKS to write its record
        var d = w.Decide(T0.AddMinutes(11).AddSeconds(16), () => [crash])!.Value;
        Assert.Equal(Ending.Crashed, d.How);
        Assert.Same(crash, d.Record);

        var frozen = new CrashWatch();
        frozen.Observe(T0, 7, started.AddHours(2), frozen: true);
        frozen.Observe(T0.AddSeconds(1), null, null, false);
        Assert.Equal(Ending.EndedWhileFrozen, frozen.Decide(T0.AddSeconds(20), () => [crash])!.Value.How); // the crash belongs to another session

        var closed = new CrashWatch();
        closed.Observe(T0, 9, started.AddHours(3), frozen: false);
        closed.Observe(T0.AddSeconds(1), null, null, false);
        Assert.Equal(Ending.Closed, closed.Decide(T0.AddSeconds(20), () => [])!.Value.How);
        Assert.Null(closed.Decide(T0.AddSeconds(40), () => [])); // told once
    }

    [Fact]
    public void The_report_includes_what_SOLIDWORKS_recorded_and_what_the_Companion_saw()
    {
        var c = CrashReader.Parse(T0, "34.3.0.0150", @"C:\Example\crash", Log, true);
        var text = CrashExplainer.Report(c, "SOLIDWORKS crashed.", ["09:00:01  You switched to sheet \"Sheet3\"."], T0);
        Assert.Contains("Call chain (innermost first): ntdll > PSKERNEL > SLDMODU", text);
        Assert.Contains("Drawing objects (GDI) just before: 15,900 of 16,384", text);
        Assert.Contains("WHAT THE COMPANION SAW BEFORE IT ENDED", text);
        Assert.Contains("share it only with people you'd trust", text);
    }
}
