using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using FontWeights = System.Windows.FontWeights;
using TextWrapping = System.Windows.TextWrapping;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// A small info dot that explains one setting in plain words. Hover it to read a short card:
/// a heading, a full explanation, and an optional tip. Screen readers get the same text
/// through the name and help text, so the meaning is never hidden behind a hover.
/// </summary>
public partial class InfoTip : UserControl
{
    public InfoTip()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnChanged));
    public static readonly DependencyProperty TipProperty = DependencyProperty.Register(
        nameof(Tip), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnChanged));

    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string Body { get => (string)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public string Tip { get => (string)GetValue(TipProperty); set => SetValue(TipProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((InfoTip)d).Rebuild();

    private void Rebuild()
    {
        var panel = new StackPanel { MaxWidth = 338 };
        if (!string.IsNullOrEmpty(Heading))
            panel.Children.Add(new TextBlock
            {
                Text = Heading,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                Foreground = Ui.Brush("B.Accent"),
                Margin = new Thickness(0, 0, 0, 5),
            });
        if (!string.IsNullOrEmpty(Body))
            panel.Children.Add(new TextBlock
            {
                Text = Body,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 17.5,
                Foreground = Ui.Brush("B.Text2"),
            });
        if (!string.IsNullOrEmpty(Tip))
        {
            var tip = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Ui.Brush("B.Text3"),
            };
            tip.Inlines.Add(new Run("›  ") { Foreground = Ui.Brush("B.Accent"), FontWeight = FontWeights.Bold });
            tip.Inlines.Add(new Run(Tip));
            panel.Children.Add(tip);
        }

        Icon.ToolTip = panel;
        AutomationProperties.SetName(this, string.IsNullOrEmpty(Heading) ? "More information" : $"About {Heading}");
        AutomationProperties.SetHelpText(this, string.IsNullOrEmpty(Tip) ? Body : $"{Body}. {Tip}");
    }
}
