using System.Text.RegularExpressions;
using PdaNetPortable.Models;

namespace PdaNetPortable.Core;

/// <summary>A connection problem explained in plain words, with steps to fix it.</summary>
public sealed record FriendlyError(string Title, string Summary, IReadOnlyList<string> Steps, string Raw);

/// <summary>
/// Plain language copy for the UI: connect steps, phone setup guides and error explanations.
/// House style for every user facing string: simple words, no dashes, and no full stop at
/// the end of a sentence.
/// </summary>
public static class Friendly
{
    public static string ModeName(ConnectionMode mode) => mode.Display();

    /// <summary>How Phone VPN reaches the phone, as words after a phone name, such as "on the USB cable".</summary>
    public static string LinkWords(LinkKind link) => link switch
    {
        LinkKind.Wifi => "over WiFi",
        LinkKind.Emulator => "on this PC",
        _ => "on the USB cable",
    };

    /// <summary>The steps ConnectionManager logs as "[n/total]" for each mode, in order.</summary>
    public static IReadOnlyList<string> ConnectSteps(ConnectionMode mode) => mode switch
    {
        ConnectionMode.AdbAgent => new[]
        {
            "Looking for your phone",
            "Starting the helper on your phone",
            "Linking this PC to your phone",
            "Checking the internet",
        },
        ConnectionMode.Usb => new[]
        {
            "Finding the USB connection",
            "Checking for the PdaNet sharing service",
            "Choosing the best route",
            "Turning on carrier stealth",
            "Checking the internet",
        },
        _ => new[]
        {
            "Finding WiFi on this PC",
            "Joining your hotspot",
            "Checking the internet",
            "Turning on carrier stealth",
        },
    };

    /// <summary>Step by step phone setup for the "Show me how" guide, Phone VPN follows the phone link.</summary>
    public static IReadOnlyList<string> HowTo(ConnectionMode mode, LinkKind link = LinkKind.Usb) => mode switch
    {
        ConnectionMode.AdbAgent when link == LinkKind.Wifi => new[]
        {
            "On your phone open Settings, then About phone",
            "Tap Build number seven times until the phone says you are a developer",
            "Go back to Settings and open System, then Developer options",
            "Turn on Wireless debugging and keep the phone on the same WiFi as this PC",
            "The first time, press Pair over WiFi and type the code from the phone",
            "Want this PC to use your phone's VPN too? Turn the VPN on first",
        },
        ConnectionMode.AdbAgent => new[]
        {
            "On your phone open Settings, then About phone",
            "Tap Build number seven times until the phone says you are a developer",
            "Go back to Settings and open System, then Developer options",
            "Turn on USB debugging",
            "Plug the phone into this PC and tap Allow when the phone asks",
            "Want this PC to use your phone's VPN too? Turn the VPN on first",
        },
        ConnectionMode.Usb => new[]
        {
            "Plug your phone into this PC with a USB cable",
            "On your phone open Settings, then Network or Connections",
            "Open Hotspot and tethering and turn on USB tethering",
            "Keep mobile data turned on",
        },
        ConnectionMode.Iphone => new[]
        {
            "On your iPhone open Settings, then Personal Hotspot",
            "Turn on Allow Others to Join and note the password",
            "Press Connect here and pick your iPhone from the list",
        },
        _ => new[]
        {
            "On your phone open Settings, then Hotspot and tethering",
            "Turn on WiFi hotspot and note its name and password",
            "Press Connect here and pick your hotspot from the list",
        },
    };

    public static string OnlineSummary(ConnectionMode mode, string? ssid) => mode switch
    {
        ConnectionMode.AdbAgent => "This PC is using your phone's connection, including its VPN",
        ConnectionMode.Usb => "This PC is using your phone's internet over the USB cable",
        ConnectionMode.Iphone => string.IsNullOrEmpty(ssid)
            ? "This PC is using your iPhone's hotspot"
            : $"This PC is using your iPhone's hotspot {ssid}",
        _ => string.IsNullOrEmpty(ssid)
            ? "This PC is using your phone's hotspot"
            : $"This PC is using your phone's hotspot {ssid}",
    };

    /// <summary>
    /// Turns a raw connection error into a title, a short summary and fix steps. In Phone VPN
    /// mode <paramref name="link"/> tells the cable from WiFi when the error text cannot.
    /// </summary>
    public static FriendlyError Explain(string? raw, ConnectionMode mode, LinkKind? link = null)
    {
        var r = Clean(raw);
        var m = (raw ?? "").ToLowerInvariant();
        bool wifi = link == LinkKind.Wifi;

        // Phone VPN messages from AdbAgent.Problem can end with a phone that is ready another way, such
        // as "Galaxy S25 Ultra is ready over WiFi". The fix it panel then shows a button for that way
        // (ConnectView.SuggestFix), so the steps below point at the button. head is the message without
        // that last part, for the checks that must not read the other way's words.
        var parts = r.Split(", ");
        var ready = parts.Skip(1).LastOrDefault(p => p.Contains(" is ready "));
        bool onWifi = ready?.EndsWith("over WiFi") == true, onUsb = ready?.EndsWith("on the USB cable") == true;
        var useOther = onWifi ? "Or press Use WiFi below" : onUsb ? "Or press Use USB cable below" : null;
        var head = string.Join(", ", parts.Where(p => p != ready)).ToLowerInvariant();
        bool mine = m.Contains("phone you picked");

        if (Has(m, "could not save your windows proxy"))
            return new("Your settings could not be saved", "PdaNet saves your Windows proxy settings before connecting, the save failed so nothing changed", new[]
            {
                "Move PdaNet to a folder you can write to, like your Desktop",
                "Press Try again",
            }, r);

        if (Has(m, "not allowed this pc", "unauthorized"))
        {
            bool wireless = head.Contains("wireless");
            return new("Your phone needs to trust this PC", wireless
                ? "Wireless debugging is waiting for your OK on the phone"
                : "USB debugging is waiting for your OK on the phone", Steps(
                "Unlock your phone",
                wireless ? "Look for the Allow wireless debugging message" : "Look for the Allow USB debugging message",
                "Tick Always allow from this computer, then tap Allow",
                useOther,
                "Press Try again"), r);
        }
        if (Has(m, "no phone on the usb cable", "not on the usb cable"))
            return new(parts[0], ready ?? "PdaNet cannot see the phone on the USB cable", new[]
            {
                "Plug the phone into this PC with a cable that can carry data",
                "Turn on USB debugging in Developer options",
                onWifi ? "Or press Use WiFi below" : mine ? "Or press Pick a phone below" : "Or pick another way in step 02",
                "Press Try again",
            }, r);

        if (Has(m, "no phone over wifi", "not over wifi"))
            return new(parts[0], ready ?? "PdaNet cannot see the phone over WiFi", new[]
            {
                "On the phone open Developer options, then turn on Wireless debugging",
                "Keep the phone and this PC on the same WiFi",
                onUsb ? "Or press Use USB cable below" : "First time over WiFi? Press Pair over WiFi below",
                "Press Try again",
            }, r);

        if (Has(m, "phone you picked"))
            return new("The phone you picked is not here", ready ?? "PdaNet cannot see the phone you picked", new[]
            {
                "Plug the phone back in",
                onUsb ? "Or press Use USB cable below" : onWifi ? "Or press Use WiFi below" : "Or press Pick a phone below",
                "Press Try again",
            }, r);

        if (Has(m, "not responding"))
            return new("Your phone is not responding", "The phone is connected but does not answer", Steps(
                "Unlock your phone",
                head.Contains("wifi") ? "Turn Wireless debugging off and on again" : "Unplug the cable and plug the cable back in",
                useOther,
                "Press Try again"), r);

        if (Has(m, "no phone found", "no adb device"))
            return new("We cannot see your phone", "Phone VPN needs a USB cable and USB debugging", new[]
            {
                "Plug your phone into this PC with a cable that can carry data",
                "Turn on USB debugging in Developer options",
                "If your phone asks, tap Allow",
                "Press Try again",
            }, r);

        if (Has(m, "adb helper is missing", "adb not found"))
            return new("The phone helper is missing", "This copy of the app could not prepare its adb tool", new[]
            {
                "Move the app to a folder you can write to, like your Desktop",
                "Or set your own adb program in Settings, Advanced",
                "Press Try again",
            }, r);

        if (Has(m, "helper stopped", "could not copy the helper", "could not start the phone helper",
                "could not link", "could not start adb", "phone helper is missing", "could not unpack"))
            return new("The phone helper could not start", wifi
                ? "Your phone stopped the helper or the WiFi link dropped"
                : "Your phone stopped the helper or the cable connection dropped", new[]
            {
                wifi ? "Check that the phone is still on the same WiFi" : "Unplug the cable and plug the cable back in",
                wifi ? "Check that Wireless debugging is still on" : "Check that USB debugging is still on",
                "Press Try again",
            }, r);

        if (Has(m, "no usb tethering found", "no active usb tethering"))
            return new("USB tethering is off", "This PC cannot see an internet connection from the phone", new[]
            {
                "Plug your phone into this PC with a USB cable",
                "On the phone open Settings, then Hotspot and tethering",
                "Turn on USB tethering",
                "Press Try again",
            }, r);

        if (Has(m, "has no internet"))
            return new("Your phone is not sharing internet", "The cable works but no internet comes through", new[]
            {
                "Turn on mobile data on your phone",
                "Turn USB tethering off and on again",
                "Press Try again",
            }, r);

        if (Has(m, "no wifi adapter", "no wifi interface"))
            return new("This PC has no WiFi", "WiFi modes need a WiFi adapter that is turned on", new[]
            {
                "Turn on WiFi in the Windows quick settings",
                "Or use Phone VPN or USB cable instead",
            }, r);

        if (Has(m, "could not join", "could not connect to wifi"))
            return new("Could not join the hotspot", "Windows could not connect to that network", new[]
            {
                "Check the hotspot name and password",
                "Make sure the hotspot is on and close by",
                "Or join it from the Windows WiFi menu, then press Try again",
            }, r);

        if (Has(m, "not on any wifi", "not connected to any wifi"))
            return new("Join your hotspot first", "This PC is not connected to any WiFi network", new[]
            {
                "Turn on the hotspot on your phone",
                "Join it from the Windows WiFi menu",
                "Press Try again",
            }, r);

        if (Has(m, "system wide stealth could not turn on", "system bypass not applied"))
            return new("Stealth needs admin approval", "Settings require stealth, but Windows did not approve it", new[]
            {
                "Press Try again and choose Yes when Windows asks",
                "Or turn off Only connect with stealth in Settings",
            }, r);

        if (Has(m, "stealth proxy", "local proxy"))
            return new("Another app is using the port", "The stealth proxy could not start on its usual port", new[]
            {
                "Close other proxy or VPN apps on this PC",
                "Or pick another local port in Settings, Advanced",
                "Press Try again",
            }, r);

        if (Has(m, "gave up after", "max reconnect"))
            return new("The connection dropped", "We tried to reconnect a few times without luck", new[]
            {
                "Check the cable or the hotspot on your phone",
                "Press Connect to start again",
            }, r);

        return new("Something went wrong", "The connection could not be completed", new[]
        {
            mode == ConnectionMode.AdbAgent || mode == ConnectionMode.Usb
                ? "Check that your phone is plugged in and unlocked"
                : "Check that your hotspot is on and close by",
            "Press Try again",
            "Still stuck? Open Activity to see which step failed",
        }, r);
    }

    /// <summary>
    /// Brings any text shown in the UI in line with the house style: dash punctuation becomes
    /// a comma, arrows become a real arrow, and full stops that end a sentence are dropped.
    /// Data such as network names is left alone apart from that.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim().Replace('\n', ' ').Replace("\r", "");
        t = t.Replace("Wi-Fi", "WiFi").Replace("wi-fi", "WiFi");
        t = t.Replace(" — ", ", ").Replace(" – ", ", ").Replace(" - ", ", ")
             .Replace("—", ", ").Replace("–", ", ");
        t = t.Replace("->", "→").Replace("<-", "←");
        // A full stop that ends a sentence inside the text becomes a comma.
        t = MidSentenceStop.Replace(t, m => ", " + LowerFirst(m.Groups[1].Value));
        // Trailing full stops and ellipses go away entirely.
        t = TrailingDots.Replace(t, "");
        return t.Trim();
    }

    public static string Adapter(string? alias) =>
        alias is null or "" ? "Unknown" :
        alias == "adb-agent" ? "Phone helper over USB" : Clean(alias);

    public static (string Word, int Bars) Quality(int quality, bool connected)
    {
        if (!connected) return ("Offline", 0);
        if (quality >= Constants.QualityExcellent) return ("Great", 4);
        if (quality >= Constants.QualityGood) return ("Good", 3);
        if (quality >= Constants.QualityFair) return ("Fair", 2);
        return ("Weak", 1);
    }

    public static string SpeedVerdict(double mbps) => mbps switch
    {
        < 1.5 => "Fine for messages and email",
        < 5 => "Good for browsing and music",
        < 15 => "Good for HD video",
        < 50 => "Great for 4K video and video calls",
        _ => "Excellent, faster than most home internet",
    };

    /// <summary>Fix steps, leaving out the ones that do not apply (null).</summary>
    private static string[] Steps(params string?[] steps) => steps.OfType<string>().ToArray();

    private static bool Has(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static string LowerFirst(string word) =>
        word.Length > 1 && char.IsUpper(word[1]) ? word : word.ToLowerInvariant();

    private static readonly Regex MidSentenceStop = new(@"(?<=[a-z0-9\)'""])\.\s+([A-Z][A-Za-z]*)", RegexOptions.Compiled);
    private static readonly Regex TrailingDots = new(@"(\s*(\.\.\.|…|\.))+\s*$", RegexOptions.Compiled);
}
