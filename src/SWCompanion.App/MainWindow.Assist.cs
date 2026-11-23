using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SWCompanion.Core.Checkup;
using SWCompanion.Core.Cpu;
using SWCompanion.Core.Io;
using SWCompanion.Core.Memory;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// The guide at the top of the window: one friendly answer, the pictures that show why (pipeline, timeline, where
/// the time went), and a step-by-step fix walkthrough with "I did it, check it". Read only: the Companion explains
/// and checks; you make the change.
/// </summary>
public partial class MainWindow
{
    private readonly TimeAccount _account = new();
    private Breakdown? _breakdown;
    private DateTime? _breakdownEnd;
    private FreezeEpisode? _breakdownFreeze;
    private object? _breakdownAction;
    private IReadOnlyList<FixCard> _fixes = [];
    private int _fixIndex;
    private string? _verifyTitle;
    private DateTime _verifyAsked, _lastCheckupAt;
    private bool _checkNow;

    private readonly ChatGuide _chat = new();
    private ChatContext? _chatContext;
    private string? _repliesShown;

    private void SetupAssist()
    {
        SetupHangs();
        SetupLoads();
        SetupAsk();
        SetupPdm();
        SetupSideVitals();
        // Redraw the conversation when it changes, and keep the newest message in view.
        _chat.Changed += ShowChat;
        Layout.ForwardWheel(ChatScroll, PagePerformance);
        // Left as the slim strip last time? Start as the strip again.
        if (App.Settings.StripMode) Loaded += (_, _) => EnterStrip();
    }

    private bool _chatOpen;

    /// <summary>
    /// The strip shows only the latest message (your question with its answer, when you asked something), so it stays
    /// small; "conversation" opens the earlier messages.
    /// </summary>
    private void ShowChat()
    {
        var all = _chat.Messages;
        IEnumerable<ChatMessage> shown = all;
        if (!_chatOpen && all.Count > 0)
        {
            int last = all.Count - 1;
            int from = last > 0 && all[last - 1].From == ChatFrom.You ? last - 1 : last;
            shown = all.Skip(from);
        }
        ChatRows.ItemsSource = shown.Select(ChatRowVm.From).ToList();
        // An AI answer can be long: give it room; otherwise keep the strip short.
        bool answer = all.Count > 0 && all[^1].Title.EndsWith(" says:", StringComparison.Ordinal);
        ChatScroll.MaxHeight = _chatOpen ? 320 : answer ? 240 : 96;
        ChatToggle.Text = _chatOpen ? "hide conversation ‹" : all.Count > 1 ? $"conversation ({all.Count}) ›" : "";
        ChatScroll.ScrollToEnd();
    }

    private void ChatToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _chatOpen = !_chatOpen;
        ShowChat();
    }

    private void Breakdown_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        BreakdownDetail.Visibility = BreakdownDetail.IsVisible ? Visibility.Collapsed : Visibility.Visible;

    private void ChatReply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReplyVm vm || _chatContext is null) return;
        _chat.Reply(DateTime.Now, vm.Reply, _chatContext);
        _repliesShown = null; // the buttons change after an answer
        _log.Write($"guide: you answered \"{vm.Reply.Label}\".");
        if (vm.Reply.Action == ReplyAction.ShowFixes) { ShowPage("fixes"); OpenFix(0); }
    }

    // ---- the slim strip ----

    private StripWindow? _strip;
    private bool _shuttingDown;

    private void StripKey_Click(object sender, RoutedEventArgs e) => EnterStrip();

    /// <summary>Hide the full window and show the slim always-on-top strip. The watching carries on in the background.</summary>
    private void EnterStrip()
    {
        if (_strip is null)
        {
            _strip = new StripWindow();
            _strip.OpenRequested += toFixes => ExitStrip(toFixes);
            // Closing the strip brings the full window back rather than leaving the Companion running unseen.
            _strip.Closing += (_, e) => { if (!_shuttingDown) { e.Cancel = true; ExitStrip(false); } };
            Closed += (_, _) => { _shuttingDown = true; _strip?.Close(); };
        }
        var s = App.Settings;
        if (s.StripLeft is { } l && s.StripTop is { } t &&
            l > SystemParameters.VirtualScreenLeft - 50 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
            t > SystemParameters.VirtualScreenTop - 20 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
        {
            _strip.Left = l; _strip.Top = t;
        }
        else
        {
            // First time: the right-hand edge of where the full window is; drag it wherever you like.
            _strip.Left = Left + ActualWidth - StripWindow.NarrowWidth - 20;
            _strip.Top = Top + 80;
        }
        Hide();
        _strip.Show();
        s.StripMode = true;
        s.Save();
        _log.Write("view: switched to the slim strip.");
    }

    private void ExitStrip(bool toFixes)
    {
        if (_strip is not null)
        {
            App.Settings.StripLeft = _strip.Left;
            App.Settings.StripTop = _strip.Top;
            _strip.Hide();
        }
        App.Settings.StripMode = false;
        App.Settings.Save();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (toFixes) ShowPage("fixes");
        _log.Write("view: back to the full window.");
    }

    /// <summary>Once a second while the strip is up: the light, a short status, three numbers, and the explanation if something is wrong.</summary>
    private void ShowStrip(GuideMessage msg, NowReport report)
    {
        if (_strip is not { IsVisible: true }) return;
        string status = report.Level switch
        {
            NowLevel.NotRunning => "Not open",
            NowLevel.Frozen => msg.Mood == Mood.Bad ? "Looks stuck" : "Still working",
            NowLevel.Busy => "Slowing down",
            _ => "All good",
        };
        var cpu = _lastSmoothed;
        var mem = _lastMemory;
        double disk = _lastIo?.SolidWorks is { } io ? io.ReadBytesPerSec + io.WriteBytesPerSec : 0;
        _strip.Display(new StripState(msg.Mood, status, msg.Title, msg.Body,
            cpu is null ? "-" : $"{cpu.SystemPercent:0}%" + (cpu.SolidWorks is { } sw ? $" (sw {sw.PercentOfPc:0}%)" : ""),
            mem is null ? "-" : $"{mem.UsedRam / 1073741824.0:0} of {mem.TotalRam / 1073741824.0:0} GB",
            $"{IoExplainer.Size((ulong)disk)}/s", _fixes.Count));
    }

    // ---- dismissing a fix ----

    private void FixDismiss_Click(object sender, RoutedEventArgs e)
    {
        var fix = _fixes.ElementAtOrDefault(_fixIndex);
        if (fix is null) return;
        App.Settings.DismissedFixes.Add(fix.Title);
        App.Settings.Save();
        _log.Write($"action: fix dismissed by the user: \"{fix.Title}\" (bring it back from the Fixes page).");
        _fixes = _fixes.Where(f => f.Title != fix.Title).ToList();
        _issueSignature = null;
        if (_fixes.Count == 0) FixClose_Click(sender, e);
        else OpenFix(Math.Min(_fixIndex, _fixes.Count - 1));
    }

    private void DismissedLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int n = App.Settings.DismissedFixes.Count;
        App.Settings.DismissedFixes.Clear();
        App.Settings.Save();
        _issueSignature = null;
        _log.Write($"action: {n} dismissed fix(es) brought back by the user.");
    }

    /// <summary>Once a second, after the right-now report: feed the pictures and decide the guide's message.</summary>
    private void ShowAssist(DateTime now, NowReport report, bool running, IReadOnlyList<Issue> issues)
    {
        var sw = _lastSmoothed?.SolidWorks;
        bool frozen = report.Level == NowLevel.Frozen;
        bool busy = sw is { CoresWorth: >= 0.3 };

        // The second-by-second account of causes (for "where the time went").
        _account.Record(now, report, busy);

        // Where the time went: the freeze in progress, else the most recent freeze or slow action.
        if (frozen && _freeze.UnresponsiveFor(now) is { } so && so.TotalSeconds >= 3)
        {
            _breakdown = _account.For("This freeze so far", now - so, now);
        }
        else
        {
            if (_freeze.LastEpisode is { } ep && !ReferenceEquals(ep, _breakdownFreeze))
            {
                _breakdownFreeze = ep;
                if (_account.For($"The freeze at {ep.Start:HH:mm}", ep.Start, ep.Start + ep.Duration) is { } b) { _breakdown = b; _breakdownEnd = now; }
            }
            if (_addinTracker.Finished.FirstOrDefault() is { Seconds: >= 3 } a && !ReferenceEquals(a, _breakdownAction))
            {
                _breakdownAction = a;
                string title = a.What switch
                {
                    "sheet" => $"Switching to sheet \"{a.Sheet}\"",
                    "open" => $"Opening {a.FileName}",
                    "save" => $"Saving {a.FileName}",
                    "rebuild" => $"Rebuilding {a.FileName}",
                    _ => a.FileName,
                };
                if (_account.For(title, a.Started, a.Started.AddSeconds(a.Seconds!.Value)) is { } b) { _breakdown = b; _breakdownEnd = now; }
            }
        }
        ShowBreakdown();

        // The pipeline.
        var stages = Pipeline.Build(new PipelineInputs(running, report.Level, sw?.CoresWorth, sw?.ThreadsVisible == true ? sw.TopThreadPercent : null,
            _lastIo?.SolidWorks?.ReadBytesPerSec, _lastIo?.SolidWorks?.WriteBytesPerSec,
            _lastIo?.Networks.Sum(n => n.ReceiveBytesPerSec + n.SendBytesPerSec), _vaultTrafficNow.Any(t => t.Requests > 0),
            _defender.CoresNow, report.Causes));
        PipelineStages.ItemsSource = stages.Select((s, i) => StageVm.From(s, i)).ToList();

        // Fixes and the guide's message. A breakdown older than 10 minutes no longer leads the message.
        _fixes = FixCards.From(issues, i => _checks.FirstOrDefault(c => IssueBoard.PlainTitle(c) == i.Title)?.Id);
        ShowTiles();
        var recent = _breakdownEnd is { } end && now - end < TimeSpan.FromMinutes(10) ? _breakdown : null;
        var msg = Guide.Build(running, report, frozen ? _breakdown : recent, _fixes.Count);
        ShowStrip(msg, report);
        // The conversation: a new situation adds a message; the reply buttons fit what was just said.
        _chatContext = new ChatContext(report, frozen ? _breakdown : recent, _fixes, Pipeline.HoldUp(report.Causes));
        // Wait for the first checkup (a few seconds) before speaking, so it doesn't say "nothing to fix" and then "I found 4".
        if (_lastCheckupAt != default || frozen) _chat.Update(now, msg);
        var replies = _chat.Replies(_chatContext);
        string sig = string.Join("|", replies.Select(r => r.Label));
        if (sig != _repliesShown)
        {
            _repliesShown = sig;
            ChatReplies.ItemsSource = replies.Select((r, i) => new ReplyVm(r, i == 0 ? "on" : null)).ToList();
        }
        (GuideBadge.Background, GuideBadgeText.Text) = msg.Mood switch
        {
            Mood.Good => ((Brush)Theme.Ok, "✓"),
            Mood.Working => (Theme.Warn, "…"),
            Mood.Warn => (Theme.Accent, "!"),
            Mood.Bad => (Theme.Alarm, "!"),
            _ => (Theme.Dim, "•"),
        };

        // "I did it, check it": once a fresh checkup has run, say whether the fix took.
        if (_verifyTitle is not null && _lastCheckupAt > _verifyAsked)
        {
            bool stillThere = issues.Any(i => i.Title == _verifyTitle);
            ShowFixResult(stillThere
                ? ("Still showing. Have another look at the steps, or try the next fix.", false)
                : ("✓  Fixed. Nice work, that one's sorted.", true));
            _log.Write($"fix check: \"{_verifyTitle}\" is {(stillThere ? "still showing" : "fixed")}.", now);
            _verifyTitle = null;
        }
    }

    private void ShowBreakdown()
    {
        if (_breakdown is not { } b)
        {
            BreakdownTitle.Text = "Nothing slow yet.";
            BreakdownBar.Children.Clear();
            BreakdownBar.ColumnDefinitions.Clear();
            BreakdownRows.ItemsSource = null;
            BreakdownPanel.Visibility = BreakdownDetail.Visibility = Visibility.Collapsed;
            return;
        }
        BreakdownPanel.Visibility = Visibility.Visible;
        BreakdownTitle.Text = $"{b.Title}: {Core.AddIn.AddinTracker.Clock(b.TotalSeconds)}";
        Brush[] palette = [Theme.Accent, Theme.Warn, Theme.Info, Theme.Ok, Theme.Dim];
        var shares = b.Shares.Take(5).ToList();
        BreakdownBar.Children.Clear();
        BreakdownBar.ColumnDefinitions.Clear();
        for (int i = 0; i < shares.Count; i++)
        {
            BreakdownBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, shares[i].Seconds), GridUnitType.Star) });
            var piece = new Border { Background = palette[i], CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == shares.Count - 1 ? 4 : 0, i == shares.Count - 1 ? 4 : 0, i == 0 ? 4 : 0) };
            Grid.SetColumn(piece, i);
            BreakdownBar.Children.Add(piece);
        }
        BreakdownRows.ItemsSource = shares.Select((s, i) => new ShareRow(palette[i], $"{s.Seconds:0} s", s.What)).ToList();
    }

    // ---- pages ----

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string page }) ShowPage(page);
    }

    /// <summary>Shows one page (performance, fixes, crashes, details, settings) and highlights it in the sidebar.</summary>
    private void ShowPage(string page)
    {
        var pages = new (string Name, ScrollViewer View, Button Nav)[]
        {
            ("performance", PagePerformance, NavPerformance), ("fixes", PageFixes, NavFixes), ("crashes", PageCrashes, NavCrashes), ("pdm", PagePdm, NavPdm),
            ("details", PageDetails, NavDetails), ("settings", PageSettings, NavSettings),
        };
        foreach (var (name, view, nav) in pages)
        {
            bool on = name == page;
            view.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            nav.Tag = on ? "on" : null;
        }
        // Fixes opens straight into the walkthrough: one fix at a time.
        if (page == "fixes" && FixWizard.Visibility != Visibility.Visible && _fixes.Count > 0) OpenFix(0);
    }

    // ---- the basics, live, in the sidebar ----

    private readonly History _cpuPc = new(), _cpuSw = new(), _memPc = new(), _memSw = new(), _gpuPc = new(), _gpuSw = new(), _disk = new(), _net = new();
    private readonly SparkView _cpuChart = new(), _memChart = new(), _gpuChart = new(), _diskChart = new(), _netChart = new();
    private readonly CoreBarsView _coresChart = new();

    private void SetupSideVitals()
    {
        SideCpuChart.Content = _cpuChart;
        SideCoresChart.Content = _coresChart;
        SideMemChart.Content = _memChart;
        SideGpuChart.Content = _gpuChart;
        SideDiskChart.Content = _diskChart;
        SideNetChart.Content = _netChart;
    }

    /// <summary>Once a second: add this second's readings to each chart and update the numbers beside them.</summary>
    private void ShowTiles()
    {
        var cpu = _lastSmoothed;
        var sw = cpu?.SolidWorks;
        _cpuPc.Add(cpu?.SystemPercent ?? 0);
        _cpuSw.Add(sw?.PercentOfPc ?? 0);
        _cpuChart.Show(100, (_cpuPc.Values, Theme.Accent), (_cpuSw.Values, Theme.Info));
        SideCpu.Text = cpu is null ? "-" : $"{cpu.SystemPercent:0}%";
        SideCpu.Foreground = cpu is { SystemPercent: >= 90 } ? Theme.Alarm : Theme.Text;
        SideCpuSub.Text = sw is null ? "SOLIDWORKS isn't open" : $"SW {sw.CoresWorth:0.0} of a core · top thread {(sw.ThreadsVisible ? $"{sw.TopThreadPercent:0}%" : "?")}";

        if (cpu is not null)
        {
            _coresChart.Show(cpu.CorePercent);
            SideCores.Text = $"{cpu.BusyCores} of {cpu.CoreCount} busy";
        }

        if (_lastMemory is { } m)
        {
            double total = m.TotalRam / 1073741824.0, used = m.UsedRam / 1073741824.0, swGb = (m.SolidWorks?.InRam ?? 0) / 1073741824.0;
            _memPc.Add(used);
            _memSw.Add(swGb);
            _memChart.Show(total, (_memPc.Values, Theme.Accent), (_memSw.Values, Theme.Info));
            SideMem.Text = $"{used:0} / {total:0} GB";
            SideMem.Foreground = m.RamUsedPercent >= MemoryExplainer.RamShort ? Theme.Alarm : Theme.Text;
            SideMemSub.Text = m.SolidWorks is null ? "SOLIDWORKS isn't open" : $"SW {swGb:0.0} GB";
        }

        if (_lastGpu is { } g && (g.SolidWorksAdapter ?? g.DedicatedCard ?? g.Adapters.FirstOrDefault()) is { } card)
        {
            _gpuPc.Add(card.BusyPercent);
            _gpuSw.Add(card.SwBusyPercent);
            _gpuChart.Show(100, (_gpuPc.Values, Theme.Accent), (_gpuSw.Values, Theme.Info));
            SideGpu.Text = $"{card.BusyPercent:0}%";
            SideGpuSub.Text = card.Adapter.DedicatedTotal > 0
                ? $"SW {card.SwBusyPercent:0}% · {(card.DedicatedUsed / 1073741824.0):0.0} of {(card.Adapter.DedicatedTotal / 1073741824.0):0} GB"
                : $"SW {card.SwBusyPercent:0}%";
            SideGpuSub.ToolTip = card.Adapter.Name;
        }
        else SideGpu.Text = "-";

        if (_lastIo is { } io)
        {
            double r = io.SolidWorks?.ReadBytesPerSec ?? 0, w = io.SolidWorks?.WriteBytesPerSec ?? 0;
            _disk.Add(r + w);
            _diskChart.Show(Math.Max(_disk.Max, 1_000_000), (_disk.Values, Theme.Info));
            SideDisk.Text = $"{IoExplainer.Size((ulong)(r + w))}/s";
            double net = io.Networks.Sum(n => n.ReceiveBytesPerSec + n.SendBytesPerSec);
            _net.Add(net);
            _netChart.Show(Math.Max(_net.Max, 100_000), (_net.Values, Theme.Accent));
            SideNet.Text = $"{IoExplainer.Size((ulong)net)}/s";
            SideNetSub.Text = _vaultTrafficNow.Any(t => t.Requests > 0) ? "includes vault traffic" : "whole PC";
        }
        NavFixesCount.Text = _fixes.Count.ToString();
        NavFixesBadge.Visibility = _fixes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    // ---- the fix walkthrough ----

    private void GuideAction_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("fixes");
        OpenFix(0);
    }

    private void OpenFix(int index)
    {
        if (_fixes.Count == 0) return;
        _fixIndex = Math.Clamp(index, 0, _fixes.Count - 1);
        FixWizard.Visibility = Visibility.Visible;
        FixResultBox.Visibility = Visibility.Collapsed;
        var fix = _fixes[_fixIndex];
        FixCounter.Text = $"fix {_fixIndex + 1} of {_fixes.Count}";
        FixTitle.Text = fix.Title;
        FixGain.Text = fix.Gain;
        FixSteps.ItemsSource = fix.Steps.Select((s, i) => new StepRow(i + 1, s)).ToList();
        bool checkable = _checks.Any(c => IssueBoard.PlainTitle(c) == fix.Title);
        FixCheck.Content = checkable ? "I did it, check it" : "Done, next fix";
        FixBack.IsEnabled = _fixIndex > 0;
        FixNext.IsEnabled = _fixIndex < _fixes.Count - 1;
        FixWizard.BringIntoView();
    }

    private void FixCheck_Click(object sender, RoutedEventArgs e)
    {
        var fix = _fixes.ElementAtOrDefault(_fixIndex);
        if (fix is null) return;
        if (!_checks.Any(c => IssueBoard.PlainTitle(c) == fix.Title))
        {
            // Not something the checkup can confirm (e.g. a view setting): move on.
            if (_fixIndex < _fixes.Count - 1) OpenFix(_fixIndex + 1);
            else FixClose_Click(sender, e);
            return;
        }
        _verifyTitle = fix.Title;
        _verifyAsked = DateTime.Now;
        _checkNow = true; // the next reading runs the checkup straight away
        ShowFixResult(("Checking...", null));
        _log.Write($"fix check requested by the user: \"{fix.Title}\".");
    }

    private void ShowFixResult((string Text, bool? Ok) r)
    {
        FixResultBox.Visibility = Visibility.Visible;
        FixResult.Text = r.Text;
        var color = r.Ok switch { true => ((SolidColorBrush)Theme.Ok).Color, false => ((SolidColorBrush)Theme.Accent).Color, null => ((SolidColorBrush)Theme.Dim).Color };
        FixResultBox.Background = new SolidColorBrush(color) { Opacity = 0.16 };
        FixResult.Foreground = r.Ok == true ? Theme.Ok : Theme.Text;
    }

    private void FixBack_Click(object sender, RoutedEventArgs e) => OpenFix(_fixIndex - 1);
    private void FixNext_Click(object sender, RoutedEventArgs e) => OpenFix(_fixIndex + 1);

    private void FixClose_Click(object sender, RoutedEventArgs e)
    {
        FixWizard.Visibility = Visibility.Collapsed;
    }

    // ---- what the pictures bind to ----

    private sealed record StepRow(int Number, string Text);
    private sealed record ReplyVm(ChatReply Reply, string? Primary) { public string Label => Reply.Label; }

    /// <summary>A bubble: the Companion on the left (tinted), your answers on the right (accent).</summary>
    private sealed record ChatRowVm(string Title, string Text, HorizontalAlignment Align, Brush Fill, Brush Ink, Brush SubInk,
        Visibility TitleVisibility, double TitleSize, Thickness TextMargin)
    {
        public static ChatRowVm From(ChatMessage m)
        {
            if (m.From == ChatFrom.You)
                return new("", m.Text, HorizontalAlignment.Right, Theme.Accent, Brushes.White, Brushes.White, Visibility.Collapsed, 0, new Thickness(0));
            var tint = (Brush)Application.Current.Resources["Screen"];
            bool big = m.Title.Length > 0;
            return new(m.Title, m.Text, HorizontalAlignment.Left, tint, Theme.Text, (Brush)Application.Current.Resources["Muted"],
                big ? Visibility.Visible : Visibility.Collapsed, 15, new Thickness(0, big && m.Text.Length > 0 ? 4 : 0, 0, 0));
        }
    }
    private sealed record ShareRow(Brush Swatch, string Seconds, string What);

    private sealed record StageVm(string Name, string Detail, Brush Fill, Brush Edge, Thickness EdgeThickness, string Flag,
        Visibility FlagVisibility, Visibility ArrowVisibility, string Tip)
    {
        public static StageVm From(Stage s, int index)
        {
            Color c = s.Health switch
            {
                StageHealth.HoldUp => ((SolidColorBrush)Theme.Alarm).Color,
                StageHealth.Busy => ((SolidColorBrush)Theme.Warn).Color,
                StageHealth.Ok => ((SolidColorBrush)Theme.Ok).Color,
                _ => ((SolidColorBrush)Theme.Dim).Color,
            };
            string tip = s.Health switch
            {
                StageHealth.HoldUp => $"{s.Name} is what's holding things up right now.",
                StageHealth.Busy => $"{s.Name} is busy.",
                StageHealth.Ok => $"{s.Name} is fine.",
                _ => "",
            };
            return new StageVm(s.Name, s.Detail, new SolidColorBrush(c) { Opacity = s.Health == StageHealth.Idle ? 0.06 : 0.13 },
                new SolidColorBrush(c), new Thickness(s.Health == StageHealth.HoldUp ? 2.5 : 1), "HOLDING IT UP",
                s.Health == StageHealth.HoldUp ? Visibility.Visible : Visibility.Collapsed,
                index == 0 ? Visibility.Collapsed : Visibility.Visible, tip);
        }
    }
}
