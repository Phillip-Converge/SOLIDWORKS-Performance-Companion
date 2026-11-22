using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SWCompanion.Core.Now;

namespace SWCompanion.App;

/// <summary>What the strip shows, handed over by the main window once a second.</summary>
internal sealed record StripState(Mood Mood, string Status, string? ExplainTitle, string? ExplainBody, string Cpu, string Mem, string Disk,
    int Fixes);

/// <summary>
/// The slim always-on-top strip for the edge of a second monitor. It only shows what the main window decides;
/// the watching keeps running in the main window (hidden while the strip is up).
/// </summary>
public partial class StripWindow : Window
{
    public const double NarrowWidth = 176, WideWidth = 330;

    /// <summary>"open" pressed: back to the full window (and to the fixes, if that's what was pressed).</summary>
    internal event Action<bool>? OpenRequested;

    public StripWindow() => InitializeComponent();

    internal void Display(StripState s)
    {
        var (light, bold) = s.Mood switch
        {
            Mood.Good => (Res("Ok"), false),
            Mood.Working => (Res("Warn"), true),
            Mood.Warn => (Res("Accent"), true),
            Mood.Bad => (Res("Alarm"), true),
            _ => (Res("Dim"), false),
        };
        Light.Fill = light;
        Status.Text = s.Status;

        // Widen with the explanation only when something is wrong; otherwise stay slim.
        bool explain = bold && s.ExplainTitle is not null;
        Explain.Visibility = explain ? Visibility.Visible : Visibility.Collapsed;
        ExplainTitle.Text = s.ExplainTitle ?? "";
        ExplainBody.Text = s.ExplainBody ?? "";
        Card.BorderBrush = explain ? light : Res("Line");
        Card.BorderThickness = new Thickness(explain ? 2 : 1);
        double width = explain ? WideWidth : NarrowWidth;
        if (Math.Abs(Width - width) > 1)
        {
            // Grow towards the middle of the screen so the strip stays on its edge.
            double right = Left + Width;
            var area = SystemParameters.WorkArea;
            bool onRightHalf = Left + Width / 2 > area.Left + area.Width / 2;
            Width = width;
            if (onRightHalf) Left = right - width;
        }

        Cpu.Text = s.Cpu;
        Mem.Text = s.Mem;
        Disk.Text = s.Disk;
        FixesKey.Content = s.Fixes == 1 ? "1 to fix" : $"{s.Fixes} to fix";
        FixesKey.Visibility = s.Fixes > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(false);
    private void Fixes_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(true);

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
