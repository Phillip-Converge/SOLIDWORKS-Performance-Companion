using SWCompanion.Core.Explain;
using SWCompanion.Core.Memory;
using SWCompanion.Core.Now;

namespace SWCompanion.Core.Tests;

public class GlossaryTests
{
    [Fact]
    public void FindsTheWordsATextUses()
    {
        var words = Glossary.Find("The graphics driver stopped responding; the processor and memory were fine.").Select(w => w.Word).ToList();
        Assert.Contains("Graphics card (GPU)", words);
        Assert.Contains("Driver", words);
        Assert.Contains("Processor (CPU)", words);
        Assert.Contains("Memory (RAM)", words);
        Assert.Contains("Not Responding", words);
    }

    [Fact]
    public void DoesNotMistakeDriverForDrive()
    {
        var words = Glossary.Find("Install the certified driver.").Select(w => w.Word).ToList();
        Assert.Contains("Driver", words);
        Assert.DoesNotContain("Disk (drive)", words);
    }

    [Fact]
    public void EveryExplanationCarriesItsWords()
    {
        var x = HangExplainer.For(new Moment(DateTime.Now, TimeSpan.Zero, "commit", "Windows is close to its memory limit", "", true), []);
        var words = x.Words.Select(w => w.Word).ToList();
        Assert.Contains("Page file", words);
        Assert.Contains("Memory limit (reserved memory)", words);
    }

    [Fact]
    public void ReservedMemoryIsReadFromTheRightPlaceInWindowsProcessList()
    {
        // This test program's own reserved memory, read two ways, should roughly agree.
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        string name = me.ProcessName + ".exe";
        var all = Antivirus.ProcessTimes.TopReserved(1000);
        var mine = Assert.Single(all, u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase) && u.Copies >= 1);
        double expected = me.PagedMemorySize64; // Windows' "page file usage" = reserved (commit) bytes
        Assert.InRange((double)mine.Bytes / mine.Copies, expected * 0.5, expected * 1.5 * Math.Max(1, mine.Copies));
    }

    [Fact]
    public void TheMemoryLimitIsExplainedWithThisPcsNumbersAndTheBiggestUsers()
    {
        const ulong gb = 1UL << 30;
        var m = new MemorySnapshot(32 * gb, 12 * gb, 40 * gb, 37 * gb, null)
        {
            TopReserved = [new MemoryUser("browser.exe", 9 * gb, 24), new MemoryUser("SLDWORKS.exe", 6 * gb, 1)],
        };
        string text = MemoryExplainer.CommitPlain(m);
        Assert.Contains("37.0 GB of the 40.0 GB", text);
        Assert.Contains("8.0 GB page file", text);
        Assert.Contains("only 63% in use", text);
        Assert.Contains("browser.exe 9.0 GB (24 copies), SLDWORKS.exe 6.0 GB", text);
    }
}
