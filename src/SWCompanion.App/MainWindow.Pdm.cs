using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Now;
using SWCompanion.Core.Pdm;
using SWCompanion.Core.Processes;

namespace SWCompanion.App;

/// <summary>
/// The PDM page: SOLIDWORKS PDM in one place, because the vault is a big part of how fast SOLIDWORKS feels. What's
/// installed and running, today's PDM-related hold-ups, the PDM settings on this PC, the vault connection (servers,
/// name checks, port test, live traffic, notes), and a plain guide to how PDM affects speed. Read only.
/// </summary>
public partial class MainWindow
{
    private string? _pdmShown;

    private void SetupPdm()
    {
        PdmGuide.ItemsSource = Core.Pdm.PdmGuide.Points;
        Layout.GentleWheel(PagePdm);
    }

    /// <summary>Refreshes the PDM page (after each checkup, and when something new is caught).</summary>
    private void ShowPdm()
    {
        var pdm = _versions?.PdmClient;
        bool running = _processes.Any(p => p.Role == ProcessRole.Pdm);
        var views = _vaults.Select(v => v.Name).ToList();
        var addin = _checks.FirstOrDefault(c => c.Id == "pdm-options");

        if (pdm is null)
        {
            PdmStatusTitle.Text = "The SOLIDWORKS PDM client isn't installed on this PC.";
            PdmStatusText.Text = "Nothing here applies until it is. The guide at the bottom explains how PDM affects SOLIDWORKS's speed, if you're thinking about it.";
        }
        else
        {
            PdmStatusTitle.Text = $"SOLIDWORKS PDM client {pdm.Name.Replace("SOLIDWORKS ", "")} is installed.";
            PdmStatusText.Text = string.Join(" ", new[]
            {
                views.Count switch
                {
                    0 => "There's no vault view on this PC yet, so there's no vault to check.",
                    1 => $"Vault view: {views[0]}.",
                    _ => $"{views.Count} vault views: {string.Join(", ", views)}.",
                },
                running ? "PDM's background program is running." : "PDM's background program isn't running right now.",
                views.Count > 0 && addin is { Status: not CheckStatus.Good } ? addin.Finding : null,
                "PDM features have had limited testing against real vaults so far, so treat the vault checks as a guide.",
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        // PDM settings: the PDM client checks, and the Windows settings that matter most for the vault view.
        string[] alsoPdm = ["explorer-opens", "quick-access", "search-paths"];
        PdmChecks.ItemsSource = _checks
            .Where(c => !c.Id.Contains(':') && (c.Group == "PDM client" || alsoPdm.Contains(c.Id)))
            // Fine settings get their plain name ("Explorer opens to This PC"); ones to look at get the problem sentence.
            .Select(c => new PdmCheckRow(c.Status is CheckStatus.Good or CheckStatus.Unknown ? Capital(c.Title) : IssueBoard.PlainTitle(c), c.Finding, c.Status == CheckStatus.Good ? "" : c.HowToFix, c.Status switch
            {
                CheckStatus.Good => Theme.Ok,
                CheckStatus.Look => Theme.Warn,
                CheckStatus.Problem => Theme.Alarm,
                _ => (Brush)Theme.Dim,
            }, c.Status == CheckStatus.Good || string.IsNullOrWhiteSpace(c.HowToFix) ? Visibility.Collapsed : Visibility.Visible))
            .ToList();

        // Today's PDM hold-ups, newest first. Only rebuilt when the list changes.
        var holdUps = _catcher.Caught.Where(Core.Pdm.PdmGuide.IsPdm).ToList();
        string sig = string.Join("|", holdUps.Select(m => m.Start.Ticks));
        if (sig == _pdmShown) return;
        _pdmShown = sig;
        PdmHoldUps.ItemsSource = holdUps.Select(m => new PdmHoldUpRow(m.Title,
            m.Duration >= TimeSpan.FromSeconds(1) ? $"{m.Start:HH:mm:ss} · {NowEngine.Seconds(m.Duration)}" : $"{m.Start:HH:mm:ss}",
            m.LookInto ? Theme.Alarm : Theme.Dim, m)).ToList();
        PdmHoldUpsEmpty.Visibility = holdUps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PdmHoldUpsCount.Text = holdUps.Count == 0 ? "" : $"{holdUps.Count} today";
    }

    /// <summary>A PDM hold-up clicked: open its explanation on the Performance page, where the panel lives.</summary>
    private void PdmHoldUp_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PdmHoldUpRow row) return;
        ShowPage("performance");
        ShowHang(row.Moment);
        CaughtSection.BringIntoView();
    }

    private sealed record PdmCheckRow(string Title, string Finding, string Fix, Brush Dot, Visibility FixVisibility);
    private sealed record PdmHoldUpRow(string Title, string When, Brush Edge, Moment Moment);
}
