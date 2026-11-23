using System.Windows;
using System.Windows.Controls;
using SWCompanion.Core;
using SWCompanion.Core.Ask;
using SWCompanion.Core.Crash;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// "Send this to the developer": builds a short report about a caught moment, a crash, or a general problem, with private
/// names hidden, and shows it for the user to read and change. They send it from their own e-mail program; the Companion
/// itself never sends anything. Every step is written to the activity log (not the report's text).
/// </summary>
public partial class MainWindow
{
    /// <summary>How far before and after a moment the running log is included in its report.</summary>
    private static readonly TimeSpan ReportLogBefore = TimeSpan.FromMinutes(2), ReportLogAfter = TimeSpan.FromSeconds(30);

    private void HangReport_Click(object sender, RoutedEventArgs e)
    {
        if (_hangShown is not { } m) return;
        var x = HangExplainer.For(m, _winEvents);
        var shown = new List<string> { x.Title, "What happened: " + x.WhatHappened, "Why it matters: " + x.WhyItMatters };
        shown.AddRange(x.TryThis.Select((t, i) => $"Try {i + 1}: {t}"));
        var log = _story.Lines.Where(l => l.Time >= m.Start - ReportLogBefore && l.Time <= m.Start + m.Duration + ReportLogAfter)
                              .Select(l => $"{l.Clock}  {l.Text}").ToList();
        OpenReport($"{m.Title} ({m.Start:d MMM HH:mm})", !x.Specific, shown, x.Technical, log);
    }

    private void CrashReport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CrashRow row) return;
        var c = row.Record;
        bool known = c.Lead is { } lead && CrashModules.Meaning(lead) is not null;
        var recorded = new List<string>
        {
            $"Crashed: {c.When:yyyy-MM-dd HH:mm:ss}",
            "SOLIDWORKS version recorded: " + CrashExplainer.VersionName(c.Version),
            "Error: " + (c.Error ?? "not recorded"),
            "Modules on the crash's call chain (innermost first): " + (c.Modules.Count > 0 ? string.Join(", ", c.Modules) : "not recorded"),
        };
        OpenReport($"SOLIDWORKS crashed{(c.Lead is { } l ? " in " + l : "")} ({c.When:d MMM yyyy})", !known,
            [row.Detail], recorded, []);
    }

    private void GeneralReport_Click(object sender, RoutedEventArgs e) =>
        OpenReport(null, false, [], [], _story.Lines.Select(l => $"{l.Clock}  {l.Text}").ToList());

    private void OpenReport(string? about, bool unexplained, IReadOnlyList<string> shown, IReadOnlyList<string> recorded, IReadOnlyList<string> log)
    {
        var context = new ReportContext(about, unexplained, AppInfo.Version,
            _versions?.Describe(_processes.Any(p => p.Role == Core.Processes.ProcessRole.Main)),
            WindowsVersion(), shown, recorded, log);
        var scrub = ReportScrubber();
        var report = DeveloperReport.Build(context, scrub);
        string what = about ?? "a general problem";
        _log.Write($"report: you opened a report to the developer about \"{scrub.Hide(what)}\" ({scrub.HiddenCount} names hidden). Nothing is sent unless you send it from your own e-mail.");
        new ReportWindow(report, scrub.HiddenCount, _log) { Owner = this }.ShowDialog();
    }

    /// <summary>Hides this PC's name, the user and Windows domain, and the PDM vault and server names, as well as files, folders and addresses.</summary>
    private Scrubber ReportScrubber()
    {
        var other = new List<string>();
        if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) other.Add(Environment.UserDomainName);
        foreach (var v in _vaults)
            other.AddRange(new[] { v.Name, v.DatabaseHost, v.ArchiveServer, v.DatabaseName }.Where(n => !string.IsNullOrWhiteSpace(n))!);
        return new Scrubber(Environment.MachineName, Environment.UserName, other.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string WindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        return $"{(v.Build >= 22000 ? "Windows 11" : "Windows 10")} (build {v.Build}){(Environment.Is64BitOperatingSystem ? ", 64-bit" : "")}";
    }
}

/// <summary>
/// The report, for the user to read and change before sending it from their own e-mail. Nothing leaves the PC from here:
/// "Open in my e-mail" only opens their e-mail program with the report filled in; they press Send there.
/// </summary>
internal sealed class ReportWindow : Window
{
    public ReportWindow(DeveloperReportText report, int hidden, Core.Logging.ActivityLog log)
    {
        Title = "Send this to the developer";
        Width = 760; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var res = Application.Current.Resources;
        Background = (System.Windows.Media.Brush)res["Bg"];
        var text = (System.Windows.Media.Brush)res["Text"];
        var key = (Style)res["KeyButton"];

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 13.5, LineHeight = 19, Foreground = text,
            Text = "This report helps the Companion explain more problems in future versions. Please add a line at the top about what you " +
                   "were doing. Names of files, folders, servers, this PC and you have already been replaced with placeholders" +
                   (hidden > 0 ? $" ({hidden} hidden)" : "") +
                   ". Read it through and change or delete anything you'd rather not share. The Companion doesn't send it: " +
                   "\"Open in my e-mail\" opens your own e-mail program with it filled in, and you press Send there.",
        };
        TextBox Field(string value, bool multi) => new()
        {
            Text = value, AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            FontFamily = multi ? (System.Windows.Media.FontFamily)res["MonoFont"] : FontFamily, FontSize = multi ? 12 : 13, Padding = new Thickness(6),
            Background = (System.Windows.Media.Brush)res["Module"], Foreground = text, BorderBrush = (System.Windows.Media.Brush)res["Line"],
        };
        var subject = Field(report.Subject, false);
        var body = Field(report.Body, true);
        var toLine = new TextBlock { Foreground = text, FontSize = 13, Margin = new Thickness(0, 0, 0, 4), Text = "To: " + AppInfo.Email };
        var subjectBox = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var subjectLabel = new TextBlock { Text = "Subject: ", Foreground = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(subjectLabel, Dock.Left);
        subjectBox.Children.Add(subjectLabel);
        subjectBox.Children.Add(subject);

        var status = new TextBlock { Foreground = (System.Windows.Media.Brush)res["Muted"], FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        Button Key(string label) => new() { Content = label, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Style = key };
        var mail = Key("Open in my e-mail");
        var copy = Key("Copy");
        var save = Key("Save as a file");
        var close = Key("Close");
        close.IsCancel = true;
        close.Margin = new Thickness(0);

        mail.Click += (_, _) =>
        {
            var (link, fits) = DeveloperReport.MailLink(AppInfo.Email, subject.Text, body.Text);
            if (!fits) TrySetClipboard(body.Text);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
                status.Text = fits
                    ? "Your e-mail program should now show the report. Check it and press Send there."
                    : "The report is long, so it's on your clipboard: click into the new e-mail's text and press Ctrl+V, then Send.";
                log.Write($"report: opened your e-mail program with the report ({body.Text.Length:N0} characters{(fits ? "" : ", pasted from the clipboard")}). The Companion didn't send it.");
            }
            catch (Exception ex)
            {
                TrySetClipboard(body.Text);
                status.Text = $"No e-mail program answered, so the report is on your clipboard instead. Paste it into an e-mail to {AppInfo.Email}.";
                log.Write("report: couldn't open an e-mail program (" + ex.Message + "); the report was copied to the clipboard instead.");
            }
        };
        copy.Click += (_, _) =>
        {
            status.Text = TrySetClipboard(subject.Text + Environment.NewLine + Environment.NewLine + body.Text)
                ? $"Copied. Paste it into an e-mail to {AppInfo.Email}."
                : "Couldn't use the clipboard just now (another program may be holding it). Try again.";
            log.Write("report: copied the report to the clipboard.");
        };
        save.Click += (_, _) =>
        {
            try
            {
                var dir = System.IO.Path.Combine(AppPaths.LocalAppDataFolder, "reports");
                System.IO.Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, $"developer-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
                System.IO.File.WriteAllText(file, subject.Text + Environment.NewLine + Environment.NewLine + body.Text);
                status.Text = $"Saved. Attach it to an e-mail to {AppInfo.Email}. The folder is opening now.";
                log.Write($"report: saved the report as {System.IO.Path.GetFileName(file)} in the reports folder.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                status.Text = "Couldn't save the file: " + ex.Message;
                log.Write("report: couldn't save the report (" + ex.Message + ").");
            }
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        buttons.Children.Add(mail);
        buttons.Children.Add(copy);
        buttons.Children.Add(save);

        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(status);
        var top = new StackPanel();
        top.Children.Add(intro);
        top.Children.Add(toLine);
        top.Children.Add(subjectBox);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        // Put the cursor on the note line so the user can start typing straight away.
        Loaded += (_, _) =>
        {
            body.Focus();
            int at = body.Text.IndexOf(DeveloperReport.NotePlaceholder, StringComparison.Ordinal);
            if (at >= 0) body.Select(at, DeveloperReport.NotePlaceholder.Length);
        };
    }

    private static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        return false;
    }
}
