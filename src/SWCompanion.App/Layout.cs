using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SWCompanion.Core;

namespace SWCompanion.App;

/// <summary>
/// Keeps the window calm to read while numbers change live:
///  - every numbered section folds and unfolds by clicking its title, and the Companion
///    remembers which ones you folded (in %LOCALAPPDATA%\SWCompanion\layout.json);
///  - live boxes never shrink back once they've grown, and stop growing at a set height (they
///    scroll inside instead), so the sections below don't jump up and down as text changes.
/// </summary>
internal sealed class Layout
{
    private const double LiveBoxMaxHeight = 380;
    private const double NowOpenHeight = 122, NowFoldedHeight = 88;

    private readonly ScrollViewer _page;
    private readonly HashSet<string> _folded;
    private readonly List<FrameworkElement> _steady = new();
    private readonly string _file = Path.Combine(AppPaths.LocalAppDataFolder, "layout.json");
    private readonly List<(string Id, Border Section)> _sections = new();
    private readonly ScrollViewer _nowScroll;
    private readonly TextBlock _nowChevron;
    private int _detailColumns;

    /// <summary>At least this wide, "right now", the story and the issues sit side by side.</summary>
    public const double WideLive = 1250;
    /// <summary>Narrower than this, the issues go under the story instead of beside it.</summary>
    public const double NarrowLive = 820;
    /// <summary>Height of the vitals, log and issues panels side by side (each scrolls inside).</summary>
    public const double LiveHeight = 540;
    /// <summary>Detail tiles: two columns from this width, three from the next.</summary>
    public const double TwoTiles = 1100, ThreeTiles = 1650;
    /// <summary>Sections that need the full width (the traffic map and the program table).</summary>
    private static readonly HashSet<string> FullWidth = new() { "07", "09" };

    public Layout(Window window, StackPanel sections, ScrollViewer page, ScrollViewer nowScroll, FrameworkElement nowHeader, TextBlock nowChevron)
    {
        _page = page;
        _nowScroll = nowScroll;
        _nowChevron = nowChevron;
        _folded = Load();

        foreach (var section in sections.Children.OfType<Border>())
            MakeFoldable(section);

        ApplyNow();

        foreach (var box in Find<Border>(sections).Where(b => b.Style == (Style)window.FindResource("ScreenPanel")))
            MakeSteady(box);

        // A different window width rewraps text, so let the boxes find their new sizes.
        window.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                foreach (var el in _steady) el.MinHeight = 0;
        };
    }

    /// <summary>
    /// The "right now" panel. In rows (narrow window): fixed height, folding leaves just its headline.
    /// In columns (wide window): it fills its column, so folding isn't needed.
    /// </summary>
    /// <summary>"Right now" sits beside the name in the header now: no fixed height and nothing to fold.</summary>
    private void ApplyNow()
    {
        _nowScroll.Height = double.NaN;
        _nowChevron.Visibility = Visibility.Collapsed;
    }
    // ---- columns on a wide window ----

    /// <summary>
    /// The Details page: its sections laid out as tiles, two or three across when there's room (the traffic map and the
    /// program table stay full width). Re-checked whenever the window changes width.
    /// </summary>
    public void SetupDetailTiles(Window window, ContentControl host, double sidebarWidth)
    {
        var tiles = new Grid();
        var fullWidth = new StackPanel();
        host.Content = new StackPanel { Children = { tiles, fullWidth } };

        void Arrange()
        {
            double width = window.ActualWidth - sidebarWidth;
            int columns = width >= ThreeTilesContent ? 3 : width >= TwoTilesContent ? 2 : 1;
            if (columns == _detailColumns) return;
            _detailColumns = columns;
            foreach (var (_, section) in _sections) (section.Parent as Panel)?.Children.Remove(section);
            tiles.Children.Clear();
            tiles.ColumnDefinitions.Clear();
            var stacks = new List<StackPanel>();
            for (int i = 0; i < columns; i++)
            {
                if (i > 0) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var stack = new StackPanel();
                Grid.SetColumn(stack, i * 2);
                tiles.Children.Add(stack);
                stacks.Add(stack);
            }
            // In order, left to right, then down; the wide sections go full width underneath.
            int next = 0;
            foreach (var (id, section) in _sections)
            {
                if (FullWidth.Contains(id) && columns > 1) fullWidth.Children.Add(section);
                else stacks[next++ % columns].Children.Add(section);
            }
        }

        Arrange();
        window.SizeChanged += (_, e) => { if (e.WidthChanged) Arrange(); };
    }

    /// <summary>Details page width (not window width) at which its sections go two and three across.</summary>
    private const double TwoTilesContent = 900, ThreeTilesContent = 1450;

    // ---- folding ----

    private void MakeFoldable(Border section)
    {
        if (section.Child is not Panel root || root.Children.Count < 2) return;
        var header = root.Children.OfType<DockPanel>().FirstOrDefault();
        var number = header?.Children.OfType<TextBlock>().FirstOrDefault(IsSectionNumber);
        if (header is null || number is null) return;
        string id = number.Text;
        _sections.Add((id, section));

        // Move everything below the title into one body, so folding is one switch and doesn't
        // fight the code that shows and hides individual parts.
        var body = new StackPanel();
        foreach (var child in root.Children.OfType<UIElement>().Where(c => c != header).ToList())
        {
            root.Children.Remove(child);
            DockPanel.SetDock(child, Dock.Top);
            body.Children.Add(child);
        }
        DockPanel.SetDock(body, Dock.Top);
        root.Children.Add(body);

        var chevron = new TextBlock { Style = number.Style, Margin = new Thickness(0, 0, 6, 0), Width = 14 };
        header.Children.Insert(0, chevron);
        DockPanel.SetDock(chevron, Dock.Left);
        header.Cursor = Cursors.Hand;
        header.Background = Brushes.Transparent; // makes the whole title row clickable
        header.ToolTip = "Click to fold or unfold this section";

        void Apply()
        {
            bool folded = _folded.Contains(id);
            body.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = folded ? "▸" : "▾";
            header.Margin = new Thickness(header.Margin.Left, header.Margin.Top, header.Margin.Right, folded ? 0 : 10);
        }
        Apply();
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is not null) return; // a key in the title row
            Toggle(id);
            Apply();
        };
    }

    private static bool IsSectionNumber(TextBlock t) => t.Text.Length == 2 && t.Text.All(char.IsDigit);

    private void Toggle(string id)
    {
        if (!_folded.Remove(id)) _folded.Add(id);
        Save();
    }

    private HashSet<string> Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(_file)) is { } ids) return new HashSet<string>(ids);
        }
        catch (Exception) { /* a damaged file just means the defaults */ }
        // First run: the checkup (long, changes rarely) starts folded; its title row still shows the summary.
        return new HashSet<string> { "01" };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_folded.OrderBy(x => x).ToArray()));
        }
        catch (Exception) { /* not being able to remember folding is never worth an error */ }
    }

    // ---- steady live boxes ----

    private void MakeSteady(Border box)
    {
        if (box.Child is not UIElement content) return;
        box.Child = null;
        var inner = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        box.Child = inner;
        box.MaxHeight = LiveBoxMaxHeight;
        ForwardWheel(inner, _page);
        _steady.Add(box);
        // Grow, never shrink: once a box has needed some height it keeps it.
        box.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height > box.MinHeight) box.MinHeight = Math.Min(e.NewSize.Height, LiveBoxMaxHeight);
        };
    }

    /// <summary>
    /// How far one notch of the mouse wheel moves things, in screen units. Windows' own default is about 48;
    /// the owner found that too jumpy, so the Companion uses half.
    /// </summary>
    internal const double WheelStep = 24;
    internal static double WheelAmount(MouseWheelEventArgs e) => e.Delta / 120.0 * WheelStep;

    private static readonly HashSet<ScrollViewer> Inner = new();

    /// <summary>A box that scrolls inside the page: it scrolls gently, and once it can't go further the wheel scrolls the page.</summary>
    internal static void ForwardWheel(ScrollViewer inner, ScrollViewer? page)
    {
        Inner.Add(inner);
        inner.PreviewMouseWheel += (_, e) =>
        {
            bool atTop = inner.VerticalOffset <= 0, atBottom = inner.VerticalOffset >= inner.ScrollableHeight;
            bool canScroll = inner.ScrollableHeight > 0 && !(e.Delta > 0 && atTop) && !(e.Delta < 0 && atBottom);
            var target = canScroll ? inner : page;
            if (target is null) return;
            target.ScrollToVerticalOffset(target.VerticalOffset - WheelAmount(e));
            e.Handled = true;
        };
    }

    /// <summary>
    /// Gentle scrolling for a top-level scrolling area (the page, the story, the issues). Leaves the wheel alone when
    /// the mouse is over something inside it that scrolls by itself (a box, the program table, an open list).
    /// </summary>
    internal static void GentleWheel(ScrollViewer area)
    {
        area.PreviewMouseWheel += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d is not null && d != area;
                 d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ScrollViewer other && (Inner.Contains(other) || other.ScrollableHeight > 0)) return;
            area.ScrollToVerticalOffset(area.VerticalOffset - WheelAmount(e));
            e.Handled = true;
        };
    }

    // ---- helpers ----

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        // Logical tree: works before the window is shown.
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in Find<T>(child)) yield return deeper;
        }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p is not null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }
}
