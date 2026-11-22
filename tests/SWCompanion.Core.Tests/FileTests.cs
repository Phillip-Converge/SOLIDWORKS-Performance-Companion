using SWCompanion.Core.Cpu;
using SWCompanion.Core.Files;
using SWCompanion.Core.Logging;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class KernelPathTests
{
    private static readonly KernelPaths Paths = new([
        (@"\Device\HarddiskVolume3", "C:"),
        (@"\Device\HarddiskVolume11", "D:"),
    ]);

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Parts\Housing.SLDPRT", @"C:\Parts\Housing.SLDPRT")]
    [InlineData(@"\Device\HarddiskVolume11\x.SLDASM", @"D:\x.SLDASM")] // not confused with Volume1
    [InlineData(@"\Device\Mup\server\share\Bracket.SLDPRT", @"\\server\share\Bracket.SLDPRT")]
    [InlineData(@"\Device\Mup\;LanmanRedirector\server\share\a.SLDDRW", @"\\server\share\a.SLDDRW")]
    [InlineData(@"\Device\LanmanRedirector\;Z:0000000000012345\server\share\b.SLDPRT", @"\\server\share\b.SLDPRT")]
    public void Turns_kernel_paths_into_normal_ones(string kernel, string expected) =>
        Assert.Equal(expected, Paths.ToNormal(kernel));

    [Fact]
    public void Reads_this_PCs_drive_map()
    {
        var real = new KernelPaths();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows); // e.g. C:\WINDOWS
        Assert.StartsWith(windows[..2], real.ToNormal(windows), StringComparison.OrdinalIgnoreCase); // already normal: unchanged
    }
}

public class FileActivityTrackerTests
{
    private const int Sw = 6096, Defender = 4242, Other = 999;
    private static readonly DateTime T0 = new(2026, 11, 20, 23, 50, 0);

    private static FileActivityTracker Tracker()
    {
        var t = new FileActivityTracker { SolidWorksPid = Sw };
        t.AntivirusPids.Add(Defender);
        return t;
    }

    [Fact]
    public void Adds_up_wait_time_and_bytes_per_file()
    {
        var t = Tracker();
        const string part = @"\\server\share\Housing.SLDPRT";
        t.Add(new FileEvent(T0, Sw, FileOp.Open, part, 0, TimeSpan.FromSeconds(0.9)));
        t.Add(new FileEvent(T0.AddSeconds(1), Sw, FileOp.Read, part, 8 << 20, TimeSpan.FromSeconds(2.1)));
        t.Add(new FileEvent(T0.AddSeconds(3), Sw, FileOp.Read, part, 4 << 20, TimeSpan.FromSeconds(1.2)));

        var f = t.Recent(T0.AddSeconds(4), TimeSpan.FromMinutes(1)).Single();
        Assert.Equal(4.2, f.WaitTime.TotalSeconds, 3);
        Assert.Equal(12 << 20, f.BytesRead);
        Assert.Equal(FileLocation.Network, f.Location);
        Assert.Equal(@"\\server\share", f.Where);
        Assert.True(f.IsSolidWorksDocument);
    }

    [Fact]
    public void Matches_a_Defender_scan_to_the_file_SolidWorks_is_opening()
    {
        var t = Tracker();
        const string asm = @"C:\Jobs\Pump.SLDASM";
        t.Add(new FileEvent(T0, Sw, FileOp.Open, asm, 0, TimeSpan.FromSeconds(0.9)));
        t.Add(new FileEvent(T0.AddMilliseconds(50), Defender, FileOp.Read, asm, 1 << 20, null));
        t.Add(new FileEvent(T0.AddMilliseconds(850), Defender, FileOp.Read, asm, 1 << 20, null));

        var f = t.Recent(T0.AddSeconds(1), TimeSpan.FromMinutes(1)).Single();
        Assert.True(f.ScannedByAntivirus);
        Assert.Equal(0.8, f.AntivirusTime.TotalSeconds, 3);
        Assert.Equal("Pump.SLDASM from this PC: waited 0.9 s, Windows Defender scanned it (0.8 s).", FileExplainer.Line(f));
    }

    [Fact]
    public void Ignores_other_programs_and_scans_of_files_SolidWorks_never_opened()
    {
        var t = Tracker();
        t.Add(new FileEvent(T0, Other, FileOp.Read, @"C:\x.SLDPRT", 100, null));
        t.Add(new FileEvent(T0, Defender, FileOp.Read, @"C:\y.SLDPRT", 100, null));
        Assert.Empty(t.Recent(T0, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Quiet_files_are_reported_once_then_forgotten()
    {
        var t = Tracker();
        var finished = new List<FileActivity>();
        t.FileFinished += finished.Add;
        t.Add(new FileEvent(T0, Sw, FileOp.Open, @"C:\a.SLDPRT", 0, null));
        t.Prune(T0.AddMinutes(1));
        Assert.Empty(finished);
        t.Prune(T0.AddMinutes(3));
        Assert.Single(finished);
        Assert.Empty(t.Recent(T0.AddMinutes(3), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Explains_a_Defender_pattern_and_suggests_IT_not_the_app()
    {
        var t = Tracker();
        t.Add(new FileEvent(T0, Sw, FileOp.Open, @"C:\a.SLDPRT", 0, TimeSpan.FromSeconds(1)));
        t.Add(new FileEvent(T0, Defender, FileOp.Read, @"C:\a.SLDPRT", 1, null));
        t.Add(new FileEvent(T0.AddSeconds(1), Defender, FileOp.Read, @"C:\a.SLDPRT", 1, null));
        var text = FileExplainer.WhatIsHappening(t.Recent(T0.AddSeconds(2), TimeSpan.FromMinutes(1)), tracing: true);
        Assert.Contains("Windows Defender is scanning SOLIDWORKS files", text);
        Assert.Contains("IT team", text);
    }

    [Fact]
    public void Off_explains_why_it_needs_administrator()
    {
        Assert.Contains("administrator", FileExplainer.WhatIsHappening([], tracing: false));
        foreach (var q in FileExplainer.Questions) Assert.Contains("Think of it like this", FileExplainer.Answer(q.Id));
    }
}

public class NowWithFilesTests
{
    [Fact]
    public void A_freeze_names_the_file_being_scanned()
    {
        var t = new FileActivityTracker { SolidWorksPid = 6096 };
        t.AntivirusPids.Add(4242);
        var t0 = DateTime.Now;
        t.Add(new FileEvent(t0, 6096, FileOp.Open, @"\\server\share\Housing.SLDASM", 0, TimeSpan.FromSeconds(3)));
        t.Add(new FileEvent(t0, 4242, FileOp.Read, @"\\server\share\Housing.SLDASM", 1, null));
        t.Add(new FileEvent(t0.AddSeconds(2), 4242, FileOp.Read, @"\\server\share\Housing.SLDASM", 1, null));

        var inputs = new NowInputs(true, false, TimeSpan.FromSeconds(6),
            new CpuSnapshot(5, Enumerable.Repeat(5.0, 20).ToList(), new SolidWorksCpu(0, 0, 0, 1, true)),
            null, null, null, null, [], [], t.Active(t0.AddSeconds(2)));
        var r = NowEngine.Evaluate(inputs);
        Assert.Equal(NowLevel.Frozen, r.Level);
        Assert.Equal("Windows Defender is scanning Housing.SLDASM", r.Top!.Title);
        Assert.Contains(@"\\server\share", r.Top.Evidence);
    }

    [Fact]
    public void A_freeze_on_a_slow_file_names_it_instead_of_generic_waiting()
    {
        var t = new FileActivityTracker { SolidWorksPid = 6096 };
        var t0 = DateTime.Now;
        t.Add(new FileEvent(t0, 6096, FileOp.Read, @"\\server\share\Bracket.SLDPRT", 64 * 1024, TimeSpan.FromSeconds(4)));
        var inputs = new NowInputs(true, false, TimeSpan.FromSeconds(6),
            new CpuSnapshot(5, Enumerable.Repeat(5.0, 20).ToList(), new SolidWorksCpu(0, 0, 0, 1, true)),
            null, null, null, null, [], [], t.Active(t0));
        Assert.Equal(@"Waiting on files: Bracket.SLDPRT from \\server\share", NowEngine.Evaluate(inputs).Top!.Title);
    }
}

public class ActivityLogTests
{
    [Fact]
    public void Appends_the_same_line_to_every_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "swc-log-test-" + Guid.NewGuid().ToString("N"));
        var folders = new[] { Path.Combine(root, "a"), Path.Combine(root, "b") };
        try
        {
            var log = new ActivityLog(folders);
            var when = new DateTime(2026, 11, 20, 23, 55, 1);
            log.Write("freeze: 23 s", when);
            log.Write("file: Housing.SLDPRT", when.AddSeconds(1));
            foreach (var f in folders)
            {
                var lines = File.ReadAllLines(Path.Combine(f, ActivityLog.FileNameFor(when)));
                Assert.Equal(["2026-11-20 23:55:01  freeze: 23 s", "2026-11-20 23:55:02  file: Housing.SLDPRT"], lines);
            }
            Assert.Null(log.LastError);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // the test's own temporary folder only
        }
    }
}
