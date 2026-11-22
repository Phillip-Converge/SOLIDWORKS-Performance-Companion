using SWCompanion.Core.Checkup;

namespace SWCompanion.Core.Tests;

public class CheckupTests
{
    private static readonly Guid OemHighPerf = new("d08ddd22-1f01-4dcb-ad9b-81ea669270f5");
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>An example laptop: High performance plan, on battery, raised drawing-object limit.</summary>
    private static CheckupFacts Laptop(
        string plan = "High performance", Guid? guid = null, Guid? mode = null, int maxAc = 100,
        bool onBattery = true, int gdi = 16384, int user = 10000, double pageUse = 2, bool pageAuto = true) =>
        new(plan, guid ?? OemHighPerf, mode, maxAc, 100, HasBattery: true, OnBattery: onBattery, BatteryPercent: 22,
            gdi, user, GdiLimitWow: 10000, UserLimitWow: 10000, pageAuto, "?:\\pagefile.sys", pageUse);

    private static CheckResult Get(CheckupFacts f, string id) => CheckupEvaluator.Evaluate(f, null).Single(r => r.Id == id);

    [Fact]
    public void Example_laptop_on_battery_with_a_raised_object_limit()
    {
        var results = CheckupEvaluator.Evaluate(Laptop(), null);
        Assert.Equal(CheckStatus.Good, results.Single(r => r.Id == "power-plan").Status); // OEM copy of High performance
        Assert.Equal(CheckStatus.Good, results.Single(r => r.Id == "cpu-cap").Status);
        Assert.Equal(CheckStatus.Look, results.Single(r => r.Id == "battery").Status);
        Assert.Equal(CheckStatus.Look, results.Single(r => r.Id == "handles").Status);
        Assert.Equal(CheckStatus.Good, results.Single(r => r.Id == "pagefile").Status);
        Assert.Equal(CheckStatus.Unknown, results.Single(r => r.Id == "graphics").Status); // no graphics reading given
        Assert.Equal("2 things to look at.", CheckupEvaluator.Summary(results));
    }

    [Fact]
    public void Balanced_plan_is_flagged_unless_power_mode_is_best_performance()
    {
        Assert.Equal(CheckStatus.Look, Get(Laptop("Balanced", Balanced), "power-plan").Status);
        Assert.Contains("High performance", Get(Laptop("Balanced", Balanced), "power-plan").HowToFix);
        var bestPerf = Laptop("Balanced", Balanced, mode: new Guid("ded574b5-45a0-4f42-8737-46345c09c238"));
        Assert.Equal(CheckStatus.Good, Get(bestPerf, "power-plan").Status);
    }

    [Fact]
    public void Power_saver_is_a_problem()
    {
        var f = Laptop("Power saver", new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"));
        Assert.Equal(CheckStatus.Problem, Get(f, "power-plan").Status);
    }

    [Fact]
    public void Processor_capped_below_100_is_a_problem()
    {
        var r = Get(Laptop(maxAc: 99), "cpu-cap");
        Assert.Equal(CheckStatus.Problem, r.Status);
        Assert.Contains("99%", r.Finding);
    }

    [Fact]
    public void Handle_limits_cite_Microsoft_and_mention_the_second_copy()
    {
        var r = Get(Laptop(), "handles");
        Assert.Contains("16,384", r.Finding);
        Assert.Contains("Wow6432Node", r.Finding);
        Assert.Contains("Microsoft", r.Source);
        Assert.Contains("65536", r.HowToFix);
        Assert.Contains("Back up the registry", r.HowToFix);
        Assert.Equal(CheckStatus.Good, Get(Laptop(gdi: 65536, user: 18000), "handles").Status);
    }

    [Fact]
    public void Busy_page_file_is_a_problem()
    {
        Assert.Equal(CheckStatus.Problem, Get(Laptop(pageUse: 80), "pagefile").Status);
        Assert.Equal(CheckStatus.Look, Get(Laptop(pageAuto: false), "pagefile").Status);
    }

    [Fact]
    public void Plugged_in_is_good()
    {
        Assert.Equal(CheckStatus.Good, Get(Laptop(onBattery: false), "battery").Status);
    }

    [Fact]
    public void Reads_this_PC_without_errors()
    {
        var f = CheckupReader.Read();
        Assert.NotNull(f.PowerPlanName);
        Assert.InRange(f.MaxProcessorAc ?? 100, 0, 100);
        Assert.All(CheckupEvaluator.Evaluate(f, null), r => Assert.False(string.IsNullOrWhiteSpace(r.Finding)));
    }
}
