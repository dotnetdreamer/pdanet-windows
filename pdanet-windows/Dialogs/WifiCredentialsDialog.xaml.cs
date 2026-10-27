using System.Windows;
using System.Net.NetworkInformation;
using PdaNetPortable.Models;
using PdaNetPortable.Services;

namespace PdaNetPortable.Dialogs;

public partial class WifiCredentialsDialog : Window
{
    public string? Ssid => string.IsNullOrWhiteSpace(SsidCombo.Text) ? null : SsidCombo.Text.Trim();
    public string? Password => string.IsNullOrEmpty(PwdBox.Password) ? null : PwdBox.Password;

    public WifiCredentialsDialog(ConnectionMode mode, string? presetSsid)
    {
        InitializeComponent();
        InfoText.Text = mode == ConnectionMode.Iphone
            ? "Enter your iPhone Personal Hotspot details. Leave the network connected in iOS settings."
            : "Enter your Android PdaNet+/hotspot details. Keep the hotspot running on the phone.";

        if (!string.IsNullOrEmpty(presetSsid))
        {
            SsidCombo.Items.Add(presetSsid);
            SsidCombo.Text = presetSsid;
            var saved = SecretStore.GetPassword(presetSsid);
            if (!string.IsNullOrEmpty(saved)) PwdBox.Password = saved;
        }

        // Kick off a background scan to populate the dropdown.
        Loaded += (_, _) => RunScan();
    }

    private void OnScan(object sender, RoutedEventArgs e) => RunScan();

    private void RunScan()
    {
        ScanStatus.Text = "Scanning...";
        ScanBtn.IsEnabled = false;
        var current = SsidCombo.Text;
        Task.Run(() =>
        {
            var nets = WifiManager.ScanNetworks();
            Dispatcher.Invoke(() =>
            {
                SsidCombo.Items.Clear();
                foreach (var n in nets) SsidCombo.Items.Add(n);
                if (!string.IsNullOrEmpty(current)) SsidCombo.Text = current;
                ScanStatus.Text = nets.Count > 0 ? $"Found {nets.Count} network(s)" : "No networks found";
                ScanBtn.IsEnabled = true;
            });
        });
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Ssid))
        {
            // Managed Windows setups can block netsh SSID query/scan. If Wi-Fi is already up,
            // allow connecting with the current network and let ConnectionManager handle it.
            var nic = InterfaceDetector.DetectWifiInterface()?.Nic;
            if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            {
                ScanStatus.Text = "Enter a network name or connect Windows to Wi-Fi first";
                return;
            }

            ScanStatus.Text = "Using current Wi-Fi connection";
        }
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
