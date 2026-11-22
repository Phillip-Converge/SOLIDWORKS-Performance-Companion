using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SWCompanion.Core.Events;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>
/// The "hung up" panel beside the running log. Windows' own event records (program hangs, graphics driver resets,
/// disk errors, low memory, network drops) are written into the log as they happen. Anything that held SOLIDWORKS up,
/// and any serious Windows event, is pulled out of the log: the line stays highlighted, a copy of it slides across
/// into the panel, and clicking it explains what happened and what to do. Read only.
/// </summary>
public partial class MainWindow
{
    /// <summary>How far back to read Windows' logs when the Companion starts.</summary>
    private static readonly TimeSpan EventLookBack = TimeSpan.FromMinutes(30);
    /// <summary>A minor Windows event this close to a freeze is pulled out too.</summary>
    private static readonly TimeSpan NearFreeze = TimeSpan.FromSeconds(15);

    private static readonly SolidColorBrush HangTint = MakeTint();
    private readonly EventWatcher _events = new();
    private readonly List<EventFinding> _winEvents = new();
    private bool _nextLineIsHang;
    private StoryRow? _lastHangRow;

    private static SolidColorBrush MakeTint()
    {
        var c = Theme.Alarm.Color;
        var b = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void SetupHangs()
    {
        Layout.ForwardWheel(HangDetailScroll, PagePerformance);
        LogAndHangs.SizeChanged += (_, e) => { if (e.WidthChanged) ArrangeLogAndHangs(); };
        PagePerformance.SizeChanged += (_, _) => ArrangeLogAndHangs();
        TopStrip.SizeChanged += (_, _) => ArrangeLogAndHangs();
        FreezeBox.SizeChanged += (_, _) => ArrangeLogAndHangs();
        FreezeBox.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(ArrangeLogAndHangs, System.Windows.Threading.DispatcherPriority.Loaded);
        PagePerformance.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(ArrangeLogAndHangs, System.Windows.Threading.DispatcherPriority.Loaded);
        _events.Found += f => Dispatcher.BeginInvoke(() => OnWindowsEvent(f));
        Loaded += async (_, _) =>
        {
            await Task.Run(() => _events.Start(EventLookBack));
            if (_events.Problem is { } p) _log.Write("windows events: " + p);
            else _log.Write("windows events: watching the Application, System and Windows Defender logs (read only).");
            // For checking the slide-in without a real freeze: start with --demo-hang. Labelled "(demo)" and never saved.
            if (Environment.GetCommandLineArgs().Contains("--demo-hang"))
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                OnWindowsEvent(new EventFinding(DateTime.Now, "win-gpu", "(demo) Windows recorded that the graphics driver stopped responding and was restarted.",
                    "This is a pretend event to show how the panel works.", Serious: true, SolidWorks: false, "demo"));
            }
        };
        Closed += (_, _) => _events.Dispose();
    }

    /// <summary>Narrower than this and the panel moves under the log instead of beside it.</summary>
    private const double SideBySideWidth = 720, MinLogHeight = 380, StackedPanelHeight = 380;
    private bool? _hangsStacked;
    private double _logHeight = 460;

    private void ArrangeLogAndHangs()
    {
        // "What's happening" and "hung up" are the heart of the page: they fill the window below the compact strip.
        if (PagePerformance.Content is FrameworkElement page && LogAndHangs.IsVisible)
        {
            double top = LogAndHangs.TranslatePoint(new Point(0, 0), page).Y;
            double fill = Math.Floor(PagePerformance.ViewportHeight - top - 14);
            _logHeight = Math.Max(MinLogHeight, fill);
        }
        bool stacked = LogAndHangs.ActualWidth > 0 && LogAndHangs.ActualWidth < SideBySideWidth;
        if (stacked != _hangsStacked)
        {
            _hangsStacked = stacked;
            var cols = LogAndHangs.ColumnDefinitions;
            cols[1].Width = new GridLength(stacked ? 0 : 14);
            cols[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(CaughtSection, stacked ? 0 : 2);
            Grid.SetRow(CaughtSection, stacked ? 1 : 0);
            CaughtSection.Height = stacked ? StackedPanelHeight : double.NaN;
            CaughtSection.Margin = new Thickness(0, stacked ? 14 : 0, 0, 0);
        }
        double height = stacked ? _logHeight + StackedPanelHeight + 14 : _logHeight;
        if (Math.Abs(LogAndHangs.Height - height) > 1) LogAndHangs.Height = height;
        LogAndHangs.RowDefinitions[0].Height = stacked ? new GridLength(_logHeight) : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>A Windows event that matters: into the log, and pulled out if it's serious or came with a freeze.</summary>
    private void OnWindowsEvent(EventFinding f)
    {
        // Windows often records the same thing several times in a burst: show it once a minute at most.
        if (_winEvents.Any(x => x.Line == f.Line && Math.Abs((x.Time - f.Time).TotalSeconds) < 60))
        {
            _winEvents.Add(f);
            return;
        }
        _winEvents.Add(f);
        if (_winEvents.Count > 300) _winEvents.RemoveAt(0);
        _log.Write($"windows event: {f.Line} {f.Detail} ({f.Source})", f.Time);

        bool nearFreeze = _freeze.UnresponsiveFor(DateTime.Now) > TimeSpan.Zero && DateTime.Now - f.Time < NearFreeze ||
                          _freeze.LastEpisode is { } ep && f.Time >= ep.Start - NearFreeze && f.Time <= ep.Start + ep.Duration + NearFreeze;
        // Late reports about earlier problems are never hang-ups, even if they land during a freeze.
        bool pull = f.Serious || (nearFreeze && f.Kind != "win-report");
        bool already = _catcher.Caught.Any(m => m.Kind == f.Kind && m.Start == f.Time); // e.g. restored from earlier today

        if (!pull || already)
        {
            _story.Note(f.Time, f.Line + (string.IsNullOrEmpty(f.Detail) ? "" : " " + f.Detail), StoryTone.Warn);
            return;
        }
        string title = "Windows: " + f.Line.Replace("Windows recorded that ", "");
        string detail = f.Detail + (nearFreeze ? " It happened around the time SOLIDWORKS wasn't responding." : "");
        _catcher.Report(new Moment(f.Time, TimeSpan.Zero, f.Kind, title, detail.Trim(), LookInto: true));
        ShowCaught();
    }

    /// <summary>A new caught moment: highlight its line in the log, then slide a copy of it into the panel.</summary>
    private void PullOut(Moment m)
    {
        _nextLineIsHang = true;
        _story.Note(m.Start + m.Duration > DateTime.Now ? DateTime.Now : m.Start + m.Duration,
            (m.Kind.StartsWith("win-") ? "" : "Hung up: ") + m.Title, StoryTone.Bad);
        var row = _lastHangRow;
        // After this second's layout (the panel list is rebuilt in the same tick), fly the line across.
        Dispatcher.BeginInvoke(() => SlideIn(row, m.Title), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SlideIn(StoryRow? row, string title)
    {
        ShowCaught();
        CaughtScroll.ScrollToTop();
        // Only animate when the Performance page is on screen; otherwise the list simply has the new item at the top.
        if (!PagePerformance.IsVisible || !SlideLayer.IsVisible) return;
        UpdateLayout();

        // The new item in the panel fades and slides in from the left, timed to land as the flying copy arrives.
        var first = CaughtRows.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        if (first is not null)
        {
            var shift = new TranslateTransform(-40, 0);
            first.RenderTransform = shift;
            first.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(520);
            first.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(300))
                { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // The flying copy starts at the highlighted log line (or the bottom of the log if it's scrolled out of view).
        Point from;
        double width = Math.Max(160, StoryScroll.ActualWidth - 24);
        if (row is not null && StoryRows.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement line && line.IsVisible)
        {
            from = line.TranslatePoint(new Point(0, 0), SlideLayer);
            double top = StoryScroll.TranslatePoint(new Point(0, 0), SlideLayer).Y;
            if (from.Y < top || from.Y > top + StoryScroll.ActualHeight) from = new Point(from.X, top + StoryScroll.ActualHeight - 40);
            width = line.ActualWidth;
        }
        else
        {
            var log = StoryScroll.TranslatePoint(new Point(0, 0), SlideLayer);
            from = new Point(log.X, log.Y + StoryScroll.ActualHeight - 40);
        }
        var to = CaughtScroll.TranslatePoint(new Point(0, 0), SlideLayer);
        double toWidth = Math.Max(120, CaughtScroll.ActualWidth - 8);

        var ghost = new Border
        {
            Background = Theme.Alarm, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Width = width,
            Child = new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.25 },
        };
        Canvas.SetLeft(ghost, from.X);
        Canvas.SetTop(ghost, from.Y);
        SlideLayer.Children.Add(ghost);

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fly = TimeSpan.FromMilliseconds(600);
        // A short pause on the line first, so your eye catches where it came from.
        var start = TimeSpan.FromMilliseconds(150);
        ghost.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from.X, to.X, fly) { BeginTime = start, EasingFunction = ease });
        ghost.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from.Y, to.Y, fly) { BeginTime = start, EasingFunction = ease });
        ghost.BeginAnimation(WidthProperty, new DoubleAnimation(width, toWidth, fly) { BeginTime = start, EasingFunction = ease });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = start + fly };
        fade.Completed += (_, _) => SlideLayer.Children.Remove(ghost);
        ghost.BeginAnimation(OpacityProperty, fade);
    }

    // ---- one hang-up, explained ----

    private void CaughtRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CaughtRow row) return;
        var x = HangExplainer.For(row.Moment, _winEvents);
        HangTitle.Text = x.Title;
        HangWhen.Text = x.When;
        HangWhat.Text = x.WhatHappened;
        HangWhy.Text = x.WhyItMatters;
        HangSaw.ItemsSource = x.WindowsSaw;
        HangSawBox.Visibility = x.WindowsSaw.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HangTry.ItemsSource = x.TryThis.Select((t, i) => new NumberedStep($"{i + 1}.", t)).ToList();
        HangLook.ItemsSource = x.LookFurther;
        var words = x.Words;
        HangWords.ItemsSource = words;
        HangWordsBox.Visibility = words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HangMore.ItemsSource = x.More;
        HangTech.ItemsSource = x.Technical;
        HangMoreKey.Visibility = Visibility.Visible;
        HangMoreBox.Visibility = Visibility.Collapsed;
        HangTechBox.Visibility = Visibility.Collapsed;
        _hangShown = row.Moment;
        HangList.Visibility = Visibility.Collapsed;
        HangDetail.Visibility = Visibility.Visible;
        HangDetailScroll.ScrollToTop();
        _log.Write($"hung up: you opened the explanation for \"{row.Moment.Title}\" ({row.Moment.Start:HH:mm:ss}).");
    }

    private Moment? _hangShown;

    /// <summary>"Explain more": the fuller account, then the option to see exactly what was recorded.</summary>
    private void HangMore_Click(object sender, RoutedEventArgs e)
    {
        HangMoreKey.Visibility = Visibility.Collapsed;
        HangMoreBox.Visibility = Visibility.Visible;
        HangTechKey.Visibility = Visibility.Visible;
        HangMoreBox.BringIntoView();
        if (_hangShown is { } m) _log.Write($"hung up: you asked for more about \"{m.Title}\".");
    }

    private void HangTech_Click(object sender, RoutedEventArgs e)
    {
        HangTechKey.Visibility = Visibility.Collapsed;
        HangTechBox.Visibility = Visibility.Visible;
        HangTechBox.BringIntoView();
        if (_hangShown is { } m) _log.Write($"hung up: you opened the recorded details for \"{m.Title}\".");
    }

    private void HangBack_Click(object sender, RoutedEventArgs e)
    {
        HangDetail.Visibility = Visibility.Collapsed;
        HangList.Visibility = Visibility.Visible;
    }

    private sealed record NumberedStep(string Number, string Text);
}
