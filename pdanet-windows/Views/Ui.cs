using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace PdaNetPortable.Views;

/// <summary>Small helpers shared by the views: theme lookups and motion.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Color Color(string key) => (Color)Application.Current.Resources[key];

    public static Color WithAlpha(Color c, byte alpha) => System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B);

    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fade an element in while lifting it slightly, used for page and panel changes.</summary>
    public static void Enter(UIElement el, double rise = 10, int ms = 240)
    {
        var tt = new TranslateTransform(0, rise);
        el.RenderTransform = tt;
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(ms + 40)) { EasingFunction = EaseOut });
    }

    /// <summary>A looping animation with a frame rate cap so idle effects stay cheap.</summary>
    public static DoubleAnimation Loop(double from, double to, double seconds, int fps, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        Timeline.SetDesiredFrameRate(a, fps);
        return a;
    }

    /// <summary>On and off blink for terminal style carets.</summary>
    public static DoubleAnimationUsingKeyFrames Blink()
    {
        var a = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55))));
        Timeline.SetDesiredFrameRate(a, 4);
        return a;
    }

    /// <summary>
    /// Catmull Rom style smoothing turned into Bezier segments, newest sample on the right.
    /// <paramref name="capacity"/> is how many samples span the full width.
    /// </summary>
    public static Geometry Smooth(IReadOnlyList<double> data, int capacity, double w, double h, double max, bool area)
    {
        var g = new StreamGeometry();
        if (data.Count < 2) return g;

        double step = w / (capacity - 1);
        double x0 = w - (data.Count - 1) * step;
        var pts = data.Select((v, i) => new Point(x0 + i * step, h - 2 - Math.Min(1, v / max) * (h - 6))).ToList();

        using (var ctx = g.Open())
        {
            ctx.BeginFigure(area ? new Point(pts[0].X, h) : pts[0], area, area);
            if (area) ctx.LineTo(pts[0], false, false);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = i > 0 ? pts[i - 1] : pts[i];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = i + 2 < pts.Count ? pts[i + 2] : p2;
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, 0, h));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, 0, h));
                ctx.BezierTo(c1, c2, p2, true, true);
            }
            if (area) ctx.LineTo(new Point(pts[^1].X, h), false, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Program icons for the Apps page, read once per program file and shared.</summary>
internal static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var hit)) return hit;

        ImageSource? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    image = source;
                }
            }
        }
        catch { /* no icon, the page shows the first letter instead */ }
        Cache[path] = image;
        return image;
    }
}

/// <summary>One row in a checklist or progress list.</summary>
public sealed record CheckRow(string Glyph, Brush Brush, string Title, Brush TitleBrush, string Detail, bool Spin = false)
{
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One numbered instruction, shown as "01  Do this".</summary>
public sealed record NumberedRow(string No, string Text);

/// <summary>Live numbers handed from the main window to the Connect page once a second.</summary>
public sealed record Telemetry(
    bool Connected,
    Models.ConnectionMode Mode,
    double Down,
    double Up,
    long TotalDown,
    long TotalUp,
    double Uptime,
    double? Latency,
    int Quality);

/// <summary>Pages can refresh themselves when they are shown or when F5 is pressed.</summary>
public interface IPage
{
    void OnShown();
    void Refresh();
}

/// <summary>An in window dialog shown over the page by MainWindow.ShowSheet.</summary>
public interface ISheet
{
    void FocusFirst();
    void OnClosing();
}

/// <summary>
/// Screenshot only helper. When PDANET_UI_DEMO is set (for example "state:connected" or
/// "sheet:wifi"), the window renders that view with sample data and makes no real network
/// changes. It is inert in normal use because the variable is never set.
/// </summary>
public static class Demo
{
    public static string? Spec { get; set; } = Environment.GetEnvironmentVariable("PDANET_UI_DEMO");
    public static bool Active => !string.IsNullOrWhiteSpace(Spec);

    public static string Value(string key)
    {
        foreach (var token in (Spec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', 2);
            if (parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts.Length > 1 ? parts[1] : "";
        }
        return "";
    }
}
