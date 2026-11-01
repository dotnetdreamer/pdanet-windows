using System.Windows;
using System.Windows.Controls;
using PdaNetPortable.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PdaNetPortable.Views;

/// <summary>Adds a web app by the web address, or edits or removes a web app.</summary>
public partial class WebAppSheet : UserControl, ISheet
{
    private readonly WebApp? _app;
    private bool _nameTouched, _settingName;

    public event Action? Closed;

    public WebAppSheet(WebApp? app, string? url = null, string? name = null)
    {
        InitializeComponent();
        _app = app;
        Heading.Text = app == null ? "Add a web app" : "Edit web app";
        SaveBtn.Content = app == null ? "Add web app" : "Save";
        RemoveBtn.Visibility = app == null ? Visibility.Collapsed : Visibility.Visible;
        AddressBox.Text = app?.Url ?? url ?? "";
        SetName(app?.Name ?? name ?? "");
        _nameTouched = app != null || !string.IsNullOrEmpty(name);
        ShowIcon();
    }

    public void FocusFirst()
    {
        var box = string.IsNullOrEmpty(AddressBox.Text) ? AddressBox : NameBox;
        box.Focus();
        box.SelectAll();
    }

    public void OnClosing() { }

    private MainWindow? Shell => Window.GetWindow(this) as MainWindow;

    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        ErrorRow.Visibility = Visibility.Collapsed;
        // Until someone types a name, the name follows the address.
        var address = BrowserStore.AddressOf(AddressBox.Text);
        if (!_nameTouched) SetName(address != null ? BrowserStore.NameFor(address) : "");
        ShowIcon();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName) _nameTouched = NameBox.Text.Length > 0;
    }

    private void SetName(string name)
    {
        _settingName = true;
        NameBox.Text = name;
        _settingName = false;
    }

    private void ShowIcon()
    {
        var icon = SiteIcons.For(BrowserStore.AddressOf(AddressBox.Text));
        SiteIcon.Source = icon;
        SiteGlyph.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var address = BrowserStore.AddressOf(AddressBox.Text);
        if (address == null || !BrowserStore.IsWeb(address))
        {
            ErrorText.Text = "Type a web address, for example outlook.office.com";
            ErrorRow.Visibility = Visibility.Visible;
            Ui.Shake(AddressBox);
            AddressBox.Focus();
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = BrowserStore.NameFor(address);

        var store = BrowserStore.Instance;
        if (_app == null)
        {
            store.AddWebApp(name, address);
            Shell?.Toast($"{name} is on your web apps");
        }
        else
        {
            store.UpdateWebApp(_app, name, address);
            Shell?.Toast($"{name} saved");
        }
        Closed?.Invoke();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        BrowserStore.Instance.RemoveWebApp(_app);
        Shell?.Toast($"{_app.Name} removed from your web apps", ToastKind.Info);
        Closed?.Invoke();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
