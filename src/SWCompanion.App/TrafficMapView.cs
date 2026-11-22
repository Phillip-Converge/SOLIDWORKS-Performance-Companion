using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SWCompanion.Core.Pdm;

namespace SWCompanion.App;

/// <summary>
/// Draws the live traffic map: this PC on the left, the vault's server(s) on the right,
/// a line per role, moving dots for traffic. It only draws what <see cref="TrafficMap"/>
/// decided. To stay light on CPU, the picture is rebuilt only when its shape changes;
/// otherwise just the text is updated, and the dots run at a low frame rate.
/// </summary>
internal sealed class TrafficMapView : Viewbox
{
    private const double W = 900, H = 200, PcLeft = 20, PcRight = 230, ServerLeft = 620, ServerRight = 880;
    private const int DotFrameRate = 15;

    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly Dictionary<string, TextBlock> _labels = new();
    private TextBlock? _pcState;
    private string? _shape;

    public TrafficMapView()
    {
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.DownOnly;
        Child = _canvas;
    }

    public void Show(MapModel m)
    {
        string shape = $"{m.IsExample}|" + string.Join(";", m.Servers.Select(s =>
            s.Name + ":" + string.Join(",", s.Links.Select(l => $"{l.Role}/{l.Health}/{l.Dots}/{Math.Round(l.Thickness)}/{Math.Round(l.Seconds, 1)}"))));
        if (shape != _shape)
        {
            _shape = shape;
            Rebuild(m);
        }
        // Text changes every reading; update it in place without restarting the dots.
        if (_pcState is not null) { _pcState.Text = m.PcState; _pcState.Foreground = StateBrush(m.State); }
        foreach (var s in m.Servers)
            foreach (var l in s.Links)
                if (_labels.TryGetValue(s.Name + "|" + l.Role, out var t)) t.Text = l.Label;
    }

    private void Rebuild(MapModel m)
    {
        _canvas.Children.Clear();
        _labels.Clear();

        // This PC.
        double pcTop = 45, pcHeight = 110;
        AddBox(PcLeft, pcTop, PcRight - PcLeft, pcHeight, "this PC", out var pcBox);
        _pcState = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Width = PcRight - PcLeft - 24, Text = m.PcState, Foreground = StateBrush(m.State) };
        Canvas.SetLeft(_pcState, PcLeft + 12); Canvas.SetTop(_pcState, pcTop + 40);
        _canvas.Children.Add(_pcState);

        if (m.IsExample)
        {
            var tag = new Border { Background = Res("Warn"), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 2),
                Child = new TextBlock { Text = "EXAMPLE: made-up servers", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("TagText") } };
            Canvas.SetLeft(tag, PcLeft); Canvas.SetTop(tag, 6);
            _canvas.Children.Add(tag);
        }

        if (m.Servers.Count == 0) return;

        // Servers stacked on the right; each line runs from the PC to its server.
        double slot = H / m.Servers.Count;
        double pcMid = pcTop + pcHeight / 2;
        for (int i = 0; i < m.Servers.Count; i++)
        {
            var s = m.Servers[i];
            double boxH = Math.Min(110, slot - 20), top = i * slot + (slot - boxH) / 2;
            AddBox(ServerLeft, top, ServerRight - ServerLeft, boxH, s.Name, out _, s.Roles + " server");
            double mid = top + boxH / 2;
            for (int j = 0; j < s.Links.Count; j++)
            {
                var link = s.Links[j];
                double offset = s.Links.Count == 1 ? 0 : (j == 0 ? -16 : 16);
                AddLink(s.Name, link, PcRight, pcMid + offset, ServerLeft, mid + offset);
            }
        }
    }

    private void AddBox(double x, double y, double w, double h, string title, out Border box, string? subtitle = null)
    {
        box = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
            BorderBrush = Res("Text"), Background = Res("Module"),
        };
        Canvas.SetLeft(box, x); Canvas.SetTop(box, y);
        _canvas.Children.Add(box);
        var name = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Res("Text"),
            TextTrimming = TextTrimming.CharacterEllipsis, Width = w - 24 };
        Canvas.SetLeft(name, x + 12); Canvas.SetTop(name, y + 10);
        _canvas.Children.Add(name);
        if (subtitle is not null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, Foreground = Res("Muted") };
            Canvas.SetLeft(sub, x + 12); Canvas.SetTop(sub, y + 32);
            _canvas.Children.Add(sub);
        }
    }

    private void AddLink(string server, MapLink link, double x1, double y1, double x2, double y2)
    {
        var brush = HealthBrush(link.Health);
        var line = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = link.Thickness,
            StrokeDashArray = link.Health == LinkHealth.Trouble ? new DoubleCollection { 3, 2 } : null,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        _canvas.Children.Add(line);

        var label = new TextBlock { Text = link.Label, FontSize = 12, Foreground = Res("Text"), Width = x2 - x1 - 40, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        Canvas.SetLeft(label, x1 + 20); Canvas.SetTop(label, (y1 + y2) / 2 + (y2 >= y1 ? -24 : 6));
        _canvas.Children.Add(label);
        _labels[server + "|" + link.Role] = label;

        // Dots: even ones go out (requests), odd ones come back (replies).
        for (int i = 0; i < link.Dots; i++)
        {
            bool outward = i % 2 == 0;
            // Dark dot with a coloured rim, so it stands out on a line of any colour.
            var dot = new Ellipse { Width = 10, Height = 10, Fill = Res("Text"), Stroke = brush, StrokeThickness = 2 };
            _canvas.Children.Add(dot);
            var duration = TimeSpan.FromSeconds(link.Seconds);
            var begin = TimeSpan.FromSeconds(link.Seconds * i / Math.Max(1, link.Dots));
            Animate(dot, Canvas.LeftProperty, (outward ? x1 : x2) - 5, (outward ? x2 : x1) - 5, duration, begin);
            Animate(dot, Canvas.TopProperty, (outward ? y1 : y2) - 5, (outward ? y2 : y1) - 5, duration, begin);
        }
    }

    private static void Animate(UIElement target, DependencyProperty property, double from, double to, TimeSpan duration, TimeSpan begin)
    {
        var a = new DoubleAnimation(from, to, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, DotFrameRate);
        target.SetValue(property, from);
        target.BeginAnimation(property, a);
    }

    private static Brush HealthBrush(LinkHealth h) => h switch
    {
        LinkHealth.Good => Res("Ok"),
        LinkHealth.Slow => Res("Warn"),
        LinkHealth.VerySlow or LinkHealth.Trouble => Res("Alarm"),
        _ => Res("Dim"),
    };

    private static Brush StateBrush(MapState s) => s switch
    {
        MapState.Responding or MapState.Working => Res("Ok"),
        MapState.Busy or MapState.Quiet => Res("Warn"),
        MapState.Stuck => Res("Alarm"),
        _ => Res("Muted"),
    };

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
