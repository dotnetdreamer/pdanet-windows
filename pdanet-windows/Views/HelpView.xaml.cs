using System.Diagnostics;
using System.Windows;
using PdaNetPortable.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>Plain language help: what the app does, common questions and shortcuts.</summary>
public partial class HelpView : UserControl, IPage
{
    private const string ProjectUrl = "https://github.com/pdanet-windows/pdanet-windows";
    private MainWindow? _shell;

    public HelpView()
    {
        InitializeComponent();
        VersionText.Text = $"v{Constants.AppVersion}";
    }

    public void Attach(MainWindow shell) => _shell = shell;

    public void OnShown() { }

    public void Refresh() { }

    private void OnProject(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
        catch { _shell?.Toast("Could not open your browser", ToastKind.Error); }
    }

    private void OnActivity(object sender, RoutedEventArgs e) => _shell?.Navigate(AppPage.Activity);
}
