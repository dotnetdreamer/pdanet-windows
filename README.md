# PdaNet Windows (Portable)

PdaNet Windows lets your Windows PC use your Android phone's internet connection. It is a single portable program, so there is nothing to install and you do not need administrator rights to run it.

This is an independent project inspired by PdaNet. It is not made by, or connected to, the makers of PdaNet. Please use it only with your own phone and follow the rules of your mobile plan.

## What it can do

- Share your phone's internet with your PC through a USB cable
- Also work with your phone's WiFi hotspot
- Send your PC's traffic through whatever connection your phone is using, including a VPN that is running on the phone
- Help stop your mobile carrier from noticing that you are sharing (this is often called tethering)
- Guide you with a step by step screen that checks your phone is ready before you connect
- Show live speed, data used, signal strength, and a plain language activity log on a modern dark screen

## What it looks like

Pick how to connect, follow the short checklist so your phone is ready, then press the button

![The PdaNet Windows home screen](docs/screenshots/connect.png)

<table>
  <tr>
    <td width="50%" valign="top">
      <b>You are online</b><br>
      Live download, upload, data used, signal and ping
      <img src="docs/screenshots/online.png" alt="Connected, showing live traffic">
    </td>
    <td width="50%" valign="top">
      <b>It walks you through it</b><br>
      Clear progress, one step at a time
      <img src="docs/screenshots/connecting.png" alt="Connecting, step by step">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Help when something is off</b><br>
      It says what to do in plain words
      <img src="docs/screenshots/fixit.png" alt="A friendly fix it guide">
    </td>
    <td width="50%" valign="top">
      <b>Simple settings</b><br>
      Everything already works out of the box
      <img src="docs/screenshots/settings.png" alt="The settings page">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Answers built in</b><br>
      Plain help for the common questions
      <img src="docs/screenshots/help.png" alt="The help page">
    </td>
    <td width="50%" valign="top">
      <b>A full activity log</b><br>
      For when you want the detail
      <img src="docs/screenshots/activity.png" alt="The activity log">
    </td>
  </tr>
</table>

## What you need

- A Windows 10 or Windows 11 PC, 64 bit
- An Android phone
- A USB cable
- USB debugging turned on in the phone's Developer options (only for the phone VPN mode)

## Getting the app

Download `PdaNetPortable.exe` from the Releases page of this project. Save it anywhere you like, such as your Desktop or a USB stick, and double click to open it. There is no setup step.

## How to use it

Pick how to connect in step 1, follow the short checklist in step 2 so your phone is ready, then press the large round button in step 3. Press it again to disconnect, which returns your PC's network settings to normal.

### Phone VPN mode (recommended)

Use this when you want your PC to go online through the same connection, and the same VPN, that your phone is using. You do not need to switch anything on in the phone's USB menu.

1. On the phone, turn on Developer options and USB debugging
2. Connect the phone to the PC with a USB cable
3. On the phone, tap Allow when it asks whether to trust this computer
4. In the app, choose Phone VPN and press the connect button
5. Open a browser and visit a site that shows your location

If the phone is connected to a VPN, your PC will now appear to be in the same country as that VPN.

### USB mode

Use this to share the phone's internet over the cable.

1. On the phone, turn on USB tethering, found under Settings, then Mobile Hotspot and Tethering
2. In the app, choose USB cable and press the connect button

### WiFi mode

Use this to share the phone's hotspot.

1. On the phone, turn on the WiFi hotspot
2. In the app, choose WiFi hotspot, pick your hotspot or type its name and password, and press the connect button

## Hiding tethering from your carrier

Some mobile plans try to detect when you share your phone's internet with another device. The app can make your PC's traffic look more like normal phone traffic, which helps avoid this. It runs on its own and does not need administrator rights.

This cannot be guaranteed on every network. The strongest option is the phone VPN mode above, because a VPN hides the traffic by encrypting it.

## Settings

Open Settings from the main screen to change things such as

- Whether hiding (stealth) is on
- How many times to retry when the connection drops
- How much data to use before you see a warning
- Which port the app uses on your PC
- A path to your own copy of adb, if you prefer that

## If something does not work

- Phone not found in VPN mode, check that USB debugging is on and that you tapped Allow on the phone
- USB mode says there is no connection, turn on USB tethering on the phone and keep mobile data on
- The browser still shows your real location, some company or school PCs force their own network proxy that overrides the app, in that case open the connection details on the Connect page, copy the proxy address, and set it in your browser
- Nothing seems to happen when you connect, open the Activity page in the app to see which step failed

## Privacy

The app keeps its settings in a folder named data next to the program. WiFi passwords are stored in encrypted form using Windows. The app itself does not send your information anywhere.

## For developers

The app is built with .NET 9 and WPF for Windows. The released program is a single self contained file, so it carries its own copy of .NET, a small Android helper, and Google's adb tool. That is why it needs no installer and no admin rights.

Build it:

```
cd pdanet-windows
dotnet publish -c Release
```

The finished program appears at `pdanet-windows/dist/PdaNetPortable.exe`.

The Android helper is a small Java HTTP proxy in `agent/ProxyMain.java`. A prebuilt copy is already included in the app, so you only need to rebuild it if you change that file. The rebuild uses the Eclipse Java compiler and Google's d8 tool and needs no Android SDK. See `agent/build.bat` for the two tools to download and the steps.

Two built in checks run without opening the window:

```
PdaNetPortable.exe --selftest
PdaNetPortable.exe --selftest-agent
```

## How it works, in short

In the phone VPN mode the app runs a tiny helper on the phone through adb and then points Windows at it. Because that helper runs as a normal program on the phone, your PC's traffic follows the phone's connection, including any VPN. In USB and WiFi modes the app routes traffic through the phone and adjusts it so the connection looks like ordinary phone use.

## License

MIT. See the LICENSE file.

## Thanks

Inspired by the pdanet-linux project and by PdaNet. All trademarks belong to their owners.
