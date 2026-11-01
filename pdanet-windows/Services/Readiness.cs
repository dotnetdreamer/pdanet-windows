using System.Net.NetworkInformation;
using PdaNetPortable.Core;
using PdaNetPortable.Models;

namespace PdaNetPortable.Services;

public enum CheckState { Done, Todo, Info, Checking }

public sealed record CheckItem(CheckState State, string Title, string Detail);

/// <summary>A checklist for the Connect page, plus the phone links adb listed in Phone VPN mode.</summary>
public sealed record ReadinessReport(bool Ready, IReadOnlyList<CheckItem> Items, IReadOnlyList<PhoneLink>? Links = null);

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

    /// <summary>
    /// Runs the checks for a mode. Phone VPN also needs the phone link choice, "usb", "wifi" or
    /// "other" with the picked serial.
    /// </summary>
    public static ReadinessReport Check(ConnectionMode mode, string? adbPath, string? link = null, string? phone = null) => mode switch
    {
        ConnectionMode.AdbAgent => CheckPhoneVpn(adbPath, link, phone),
        ConnectionMode.Usb => CheckUsb(),
        _ => CheckWifi(mode),
    };

    private static ReadinessReport CheckPhoneVpn(string? adbPath, string? link, string? phone)
    {
        var (hasAdb, links) = AdbAgent.List(adbPath);
        if (!hasAdb)
            return new(false, new[]
            {
                new CheckItem(CheckState.Todo, "Phone helper is missing", "Set a custom adb program in Settings, Advanced"),
            }, links);
        return PhoneVpn(links, link, phone);
    }

    /// <summary>
    /// The Phone VPN checklist for a link choice and a phone picked from the list, built from
    /// links adb already listed, so the Connect page can answer a new choice at once.
    /// </summary>
    public static ReadinessReport PhoneVpn(IReadOnlyList<PhoneLink> links, string? choice, string? phone)
    {
        var vpn = new CheckItem(CheckState.Info, "Phone VPN is optional", "If a VPN is on in your phone, this PC will use the VPN too");
        var link = AdbAgent.Pick(links, choice, phone);
        if (link == null)
        {
            bool picked = !string.IsNullOrEmpty(phone);
            var todo = choice switch
            {
                "wifi" => new[]
                {
                    new CheckItem(CheckState.Todo, "Turn on Wireless debugging", picked
                        ? "On the phone you picked open Developer options, then Wireless debugging"
                        : "On the phone open Developer options, then Wireless debugging"),
                    new CheckItem(CheckState.Todo, "Use the same WiFi", "The phone and this PC need the same WiFi network"),
                },
                "other" => new[]
                {
                    new CheckItem(CheckState.Todo, "Connect the phone you picked", "Plug the phone back in, or pick another phone"),
                },
                _ => new[]
                {
                    new CheckItem(CheckState.Todo, picked ? "Plug in the phone you picked" : "Plug in your phone",
                        "Use a USB cable that can carry data"),
                    new CheckItem(CheckState.Todo, "Turn on USB debugging", "You will find USB debugging in Developer options on the phone"),
                },
            };
            return new(false, todo.Append(vpn).ToList(), links);
        }

        bool wifi = link.Kind == LinkKind.Wifi;
        var found = $"{link.Label} {Friendly.LinkWords(link.Kind)}";
        return link.State switch
        {
            "device" => new(true, new[] { new CheckItem(CheckState.Done, "Phone ready", found), vpn }, links),
            "unauthorized" => new(false, new[]
            {
                new CheckItem(CheckState.Done, "Phone found", found),
                new CheckItem(CheckState.Todo, "Tap Allow on your phone", wifi
                    ? "Unlock the phone and allow wireless debugging for this PC"
                    : "Unlock the phone and allow USB debugging for this PC"),
                vpn,
            }, links),
            _ => new(false, new[]
            {
                new CheckItem(CheckState.Done, "Phone found", found),
                new CheckItem(CheckState.Todo, "Phone is not responding", wifi
                    ? "Turn Wireless debugging off and on again"
                    : "Unplug the cable and plug the cable back in"),
                vpn,
            }, links),
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
