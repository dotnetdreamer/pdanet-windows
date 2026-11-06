using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// Asks what closing the window does: keep PdaNet running in the tray, or quit it. It says what
/// quitting ends right now, and can keep the pick so the question does not come again.
/// </summary>
public partial class QuitSheet : UserControl, ISheet
{
    /// <summary>Quit rather than keep in the tray, and whether to keep that pick.</summary>
    public event Action<bool, bool>? Picked;
    public event Action? Cancelled;

    /// <param name="ends">What quitting ends, such as "disconnects you from your phone".</param>
    public QuitSheet(IReadOnlyList<string> ends)
    {
        InitializeComponent();
        if (ends.Count == 0) return;
        LossText.Text = "Quitting now " + (ends.Count == 1 ? ends[0] : $"{string.Join(", ", ends.Take(ends.Count - 1))} and {ends[^1]}");
        LossBox.Visibility = Visibility.Visible;
    }

    public void FocusFirst() => TrayBtn.Focus();

    public void OnClosing() { }

    private void OnTray(object sender, RoutedEventArgs e) => Picked?.Invoke(false, Remember.IsChecked == true);

    private void OnQuit(object sender, RoutedEventArgs e) => Picked?.Invoke(true, Remember.IsChecked == true);

    private void OnCancel(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
}
