using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

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
