using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdaNetPortable.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>
/// In window sheet to pretend the PC is in another city. Picks a place, shows a live clock
/// for it next to the real one, and turns the Windows time zone (and optionally region) on
/// or off. All of it is reversible and needs no admin.
/// </summary>
public partial class LocationSheet : UserControl, ISheet
{
    private readonly Models.AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;

    public event Action? Closed;

    public LocationSheet()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(LocationMask.Presets);
        _view.Filter = o => Match(o as LocationPreset);
        CityList.ItemsSource = _view;

        MatchRegion.IsChecked = _cfg.LocationMatchRegion;
        AutoOnConnect.IsChecked = _cfg.LocationAutoOnConnect;

        var start = LocationMask.Active ? LocationMask.ActiveCity : _cfg.LocationCity;
        CityList.SelectedItem = LocationMask.Find(start) ?? LocationMask.Presets[0];
        CityList.ScrollIntoView(CityList.SelectedItem);

        _clock.Tick += (_, _) => UpdateClocks();
        _clock.Start();

        UpdatePreview();
        UpdateClocks();
        RefreshState();

        // Keep the sheet within the window so the footer buttons never clip on short windows.
        Loaded += (_, _) =>
        {
            var w = Window.GetWindow(this);
            if (w != null) MaxHeight = Math.Max(440, w.ActualHeight - 96);
        };
    }

    public void FocusFirst() => Search.Focus();

    public void OnClosing()
    {
        _clock.Stop();
        SaveChoice();
    }

    private LocationPreset Selected => CityList.SelectedItem as LocationPreset ?? LocationMask.Default;

    private bool Match(LocationPreset? p)
    {
        if (p is null) return false;
        var q = Search.Text?.Trim();
        return string.IsNullOrEmpty(q)
               || p.City.Contains(q, StringComparison.OrdinalIgnoreCase)
               || p.Country.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void OnQuickPick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Content: string city })
        {
            var p = LocationMask.Find(city);
            if (p != null) { Search.Text = ""; CityList.SelectedItem = p; CityList.ScrollIntoView(p); }
        }
    }

    private void OnCitySelected(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
        UpdateClocks();
        if (LocationMask.Active && !_busy) Apply();   // live relocate while on
    }

    private void UpdatePreview()
    {
        var p = Selected;
        MaskCity.Text = p.City;
        MaskZone.Text = $"{p.Country}  ·  {p.TzId}";
        MaskOffset.Text = p.OffsetLabel;
    }

    private void UpdateClocks()
    {
        var p = Selected;
        MaskClock.Text = LocationMask.NowIn(p.TzId).ToString("HH:mm:ss");
        MaskOffset.Text = p.OffsetLabel;

        var realId = LocationMask.RealZoneId;
        RealClock.Text = LocationMask.NowIn(realId).ToString("HH:mm:ss");
        RealZone.Text = LocationMask.OffsetOf(realId);
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (LocationMask.Active) Disable();
        else Apply();
    }

    private void Apply()
    {
        if (_busy) return;
        SaveChoice();
        var p = Selected;
        bool region = MatchRegion.IsChecked == true;
        SetBusy(true);
        Task.Run(() =>
        {
            bool ok = LocationMask.Apply(p, region, out var err);
            Dispatcher.Invoke(() =>
            {
                SetBusy(false);
                if (!ok) System.Windows.MessageBox.Show(Window.GetWindow(this), err, "Location",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshState();
            });
        });
    }

    private void Disable()
    {
        if (_busy) return;
        SetBusy(true);
        Task.Run(() =>
        {
            LocationMask.Restore();
            Dispatcher.Invoke(() => { SetBusy(false); RefreshState(); });
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ToggleBtn.IsEnabled = !busy;
        ToggleBtn.Content = busy ? "Working" : LocationMask.Active ? "Turn off" : "Appear here";
    }

    private void RefreshState()
    {
        bool active = LocationMask.Active;
        var pulse = (Storyboard)Resources["DotPulse"];

        StateText.Text = active ? "ON" : "OFF";
        StateText.Foreground = active ? Ui.Brush("B.Accent") : Ui.Brush("B.Text3");
        StateDot.Fill = active ? Ui.Brush("B.Accent") : Ui.Brush("B.Text3");
        StatePill.BorderBrush = active ? Ui.Brush("B.AccentLine") : Ui.Brush("B.Line2");

        MaskGlow.Opacity = active ? 0.8 : 0;
        MaskCard.Background = active ? Ui.Brush("B.AccentSoft") : Ui.Brush("B.Bg2");
        MaskCard.BorderBrush = active ? Ui.Brush("B.AccentLine") : Ui.Brush("B.Line2");

        ToggleBtn.Content = active ? "Turn off" : "Appear here";
        ToggleBtn.Tag = active ? "" : ""; // cancel glyph when on, globe when off

        if (active) pulse.Begin(StateDot, true);
        else { pulse.Stop(StateDot); StateDot.Opacity = 1; }
    }

    private void SaveChoice()
    {
        _cfg.LocationCity = Selected.City;
        _cfg.LocationMatchRegion = MatchRegion.IsChecked == true;
        _cfg.LocationAutoOnConnect = AutoOnConnect.IsChecked == true;
        ConfigManager.Instance.SaveConfig();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
