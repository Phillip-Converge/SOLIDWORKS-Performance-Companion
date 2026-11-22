using System.Windows;
using System.Windows.Media;

namespace SWCompanion.App;

/// <summary>The last minute of a reading, one value a second (oldest first).</summary>
internal sealed class History
{
    public const int Points = 60;
    private readonly Queue<double> _values = new();

    public void Add(double v)
    {
        _values.Enqueue(double.IsFinite(v) ? Math.Max(0, v) : 0);
        while (_values.Count > Points) _values.Dequeue();
    }

    public IReadOnlyList<double> Values => _values.ToList();
    public double Max => _values.Count == 0 ? 0 : _values.Max();
}

/// <summary>
/// A small chart for the sidebar: up to two lines over the last minute (the first one shaded underneath), with a faint
/// half-way line. The scale is fixed (e.g. 0-100 %) or grows to fit the biggest value seen in the minute.
/// </summary>
internal sealed class SparkView : FrameworkElement
{
    private (IReadOnlyList<double> Values, Brush Brush)[] _series = [];
    private double _max = 1;

    public void Show(double max, params (IReadOnlyList<double> Values, Brush Brush)[] series)
    {
        _series = series;
        _max = max > 0 ? max : 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var back = (Brush)Application.Current.Resources["Bg"];
        var line = (Brush)Application.Current.Resources["Line"];
        dc.DrawRoundedRectangle(back, null, new Rect(0, 0, w, h), 4, 4);
        dc.DrawLine(new Pen(line, 1) { DashStyle = DashStyles.Dot }, new Point(0, Math.Round(h / 2) + 0.5), new Point(w, Math.Round(h / 2) + 0.5));

        double step = w / (History.Points - 1);
        for (int s = 0; s < _series.Length; s++)
        {
            var (values, brush) = _series[s];
            if (values.Count < 2) continue;
            // Newest value at the right edge; a part-filled minute starts part way across.
            double x0 = w - (values.Count - 1) * step;
            Point P(int i) => new(x0 + i * step, h - 2 - Math.Min(1, values[i] / _max) * (h - 4));

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(P(0), false, false);
                for (int i = 1; i < values.Count; i++) g.LineTo(P(i), true, false);
            }
            geo.Freeze();

            if (s == 0 && brush is SolidColorBrush sb)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(x0, h), true, true);
                    for (int i = 0; i < values.Count; i++) g.LineTo(P(i), true, false);
                    g.LineTo(new Point(w, h), true, false);
                }
                area.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(0x30, sb.Color.R, sb.Color.G, sb.Color.B));
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, new Pen(brush, 1.5) { LineJoin = PenLineJoin.Round }, geo);
        }
    }
}

/// <summary>One thin bar per processor core, height = how busy it is right now. Busy cores are drawn in the accent colour.</summary>
internal sealed class CoreBarsView : FrameworkElement
{
    private IReadOnlyList<double> _cores = [];

    public void Show(IReadOnlyList<double> cores)
    {
        _cores = cores;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var res = Application.Current.Resources;
        dc.DrawRoundedRectangle((Brush)res["Bg"], null, new Rect(0, 0, w, h), 4, 4);
        int n = _cores.Count;
        if (n == 0) return;
        double gap = n > 24 ? 1 : 2, bw = Math.Max(1, (w - 4 - gap * (n - 1)) / n);
        var busy = (Brush)res["Accent"];
        var quiet = (Brush)res["Dim"];
        for (int i = 0; i < n; i++)
        {
            double v = Math.Clamp(_cores[i] / 100, 0, 1);
            double bh = Math.Max(1.5, v * (h - 4));
            dc.DrawRectangle(_cores[i] >= Core.Cpu.CpuSnapshot.BusyCoreThreshold ? busy : quiet, null,
                new Rect(2 + i * (bw + gap), h - 2 - bh, bw, bh));
        }
    }
}
