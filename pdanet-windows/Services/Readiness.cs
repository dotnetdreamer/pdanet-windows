using System.Net.NetworkInformation;
using PdaNetPortable.Core;
using PdaNetPortable.Models;

namespace PdaNetPortable.Services;

public enum CheckState { Done, Todo, Info, Checking }

public sealed record CheckItem(CheckState State, string Title, string Detail);

public sealed record ReadinessReport(bool Ready, IReadOnlyList<CheckItem> Items);

/// <summary>
/// Live "is my phone ready?" checks for the Connect page. Strictly read only: nothing here
/// changes network settings. Phone VPN mode asks adb for its device list, the other modes
/// look at this PC's network adapters.
/// </summary>
public static class Readiness
{
    public static ReadinessReport Pending() => new(false, new[]
    {
        new CheckItem(CheckState.Checking, "Checking your setup", "This only takes a moment"),
    });

    public static ReadinessReport Check(ConnectionMode mode, string? adbPath) => mode switch
    {
        ConnectionMode.AdbAgent => CheckPhoneVpn(adbPath),
        ConnectionMode.Usb => CheckUsb(),
        _ => CheckWifi(mode),
    };

    private static ReadinessReport CheckPhoneVpn(string? adbPath)
    {
        var (state, model) = AdbAgent.Probe(adbPath);
        var vpn = new CheckItem(CheckState.Info, "Phone VPN is optional", "If a VPN is on in your phone, this PC will use it too");
        return state switch
        {
            AdbAgent.PhoneState.Ready => new(true, new[]
            {
                new CheckItem(CheckState.Done, "Phone found",
                    string.IsNullOrEmpty(model) ? "Connected by USB" : $"{model}, connected by USB"),
                new CheckItem(CheckState.Done, "USB debugging allowed", "Your phone trusts this PC"),
                vpn,
            }),
            AdbAgent.PhoneState.Unauthorized => new(false, new[]
            {
                new CheckItem(CheckState.Done, "Phone found", "Connected by USB"),
                new CheckItem(CheckState.Todo, "Tap Allow on your phone", "Unlock it and allow USB debugging for this PC"),
                vpn,
            }),
            AdbAgent.PhoneState.Offline => new(false, new[]
            {
                new CheckItem(CheckState.Done, "Phone found", "Connected by USB"),
                new CheckItem(CheckState.Todo, "Phone is not responding", "Unplug the cable and plug it back in"),
                vpn,
            }),
            AdbAgent.PhoneState.NoAdb => new(false, new[]
            {
                new CheckItem(CheckState.Todo, "Phone helper is missing", "Set a custom adb program in Settings, Advanced"),
            }),
            _ => new(false, new[]
            {
                new CheckItem(CheckState.Todo, "Plug in your phone", "Use a USB cable that can carry data"),
                new CheckItem(CheckState.Todo, "Turn on USB debugging", "You will find it in Developer options on the phone"),
                vpn,
            }),
        };
    }

    private static ReadinessReport CheckUsb()
    {
        var iface = InterfaceDetector.DetectUsbInterface();
        var data = new CheckItem(CheckState.Info, "Keep mobile data on", "Your phone shares its mobile data with this PC");
        if (iface != null)
            return new(true, new[]
            {
                new CheckItem(CheckState.Done, "Phone connected by cable", Friendly.Clean(iface.Description)),
                new CheckItem(CheckState.Done, "USB tethering is on", "This PC can see the phone's connection"),
                data,
            });
        return new(false, new[]
        {
            new CheckItem(CheckState.Todo, "Plug in your phone", "Use a USB cable that can carry data"),
            new CheckItem(CheckState.Todo, "Turn on USB tethering", "Settings, Hotspot and tethering, USB tethering"),
            data,
        });
    }

    private static ReadinessReport CheckWifi(ConnectionMode mode)
    {
        bool iphone = mode == ConnectionMode.Iphone;
        var hotspot = new CheckItem(CheckState.Info,
            iphone ? "Turn on Personal Hotspot" : "Turn on your hotspot",
            iphone ? "Settings, Personal Hotspot, Allow Others to Join" : "Settings, Hotspot and tethering, WiFi hotspot");

        bool hasAdapter = NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        if (!hasAdapter)
            return new(false, new[]
            {
                hotspot,
                new CheckItem(CheckState.Todo, "This PC has no WiFi", "Turn WiFi on, or use Phone VPN or USB cable instead"),
            });

        var up = InterfaceDetector.DetectWifiInterface();
        return new(true, new[]
        {
            hotspot,
            up != null
                ? new CheckItem(CheckState.Done, "This PC is on WiFi", "You can switch to your hotspot when you press Connect")
                : new CheckItem(CheckState.Done, "WiFi is ready", "Pick your hotspot when you press Connect"),
        });
    }
}
