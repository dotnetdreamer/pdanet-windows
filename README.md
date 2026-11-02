# PdaNet Windows (Portable)

PdaNet Windows lets your Windows PC use your Android phone's internet connection. It is a single portable program, so there is nothing to install and you do not need administrator rights to run it.

This is an independent project inspired by PdaNet. It is not made by, or connected to, the makers of PdaNet. Please use it only with your own phone and follow the rules of your mobile plan.

## What it can do

- Share your phone's internet with your PC through a USB cable
- Also work with your phone's WiFi hotspot
- Send your PC's traffic through whatever connection your phone is using, including a VPN that is running on the phone
- Reach your phone by USB cable or over WiFi, and pick which way when your phone shows up both ways
- Help stop your mobile carrier from noticing that you are sharing (this is often called tethering)
- Guide you with a step by step screen that checks your phone is ready before you connect
- Show live speed, data used, signal strength, and a plain language activity log on a modern dark screen
- Show which apps use the internet, the websites each app visits and the data each app uses, live
- Block an app or a website to save phone data, with no admin
- Make your PC read as another city, time zone and region, with no admin
- Browse the web in the PdaNet browser on the Launchpad, with tabs and your web apps one click away
- Let an AI agent drive that browser for you, through your phone's connection, with a built in MCP server
- See and use another PC, or let someone help on this one, with a ten digit code and nothing to install, straight between the two PCs or through your phone

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
      <b>Settings you understand</b><br>
      Every option has a plain explanation, hover the info dot to read it
      <img src="docs/screenshots/settings.png" alt="The settings page with an explanation open">
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
  <tr>
    <td width="50%" valign="top">
      <b>Cable or WiFi, you pick</b><br>
      See every phone PdaNet can reach and pick one
      <img src="docs/screenshots/phone-pick.png" alt="The list of phones and emulators">
    </td>
    <td width="50%" valign="top">
      <b>Pair over WiFi once</b><br>
      Type the code from the phone, PdaNet fills in the address when the phone shows up
      <img src="docs/screenshots/phone-pair.png" alt="Pairing a phone over WiFi">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>See every app online</b><br>
      The websites and the data of each app, live
      <img src="docs/screenshots/apps.png" alt="The Apps page with apps, websites and lines">
    </td>
    <td width="50%" valign="top">
      <b>Details of every line</b><br>
      Double click a line to see the details
      <img src="docs/screenshots/apps-record.png" alt="The details of one line">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Block an app or a website</b><br>
      Right click a line, no admin needed
      <img src="docs/screenshots/apps-blocked.png" alt="A blocked app on the Apps page">
    </td>
    <td width="50%" valign="top">
      <b>All your blocks</b><br>
      See and remove blocks in one place
      <img src="docs/screenshots/apps-blocked-list.png" alt="The list of blocked apps and websites">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>The Launchpad</b><br>
      Apps running right inside PdaNet, starting with a browser
      <img src="docs/screenshots/launchpad.png" alt="The Launchpad with the PdaNet browser, web apps and recent websites">
    </td>
    <td width="50%" valign="top">
      <b>A separate browser</b><br>
      Tabs, a search box and your web apps on every new tab
      <img src="docs/screenshots/browser.png" alt="The start page of the PdaNet browser">
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <b>Find on page and more</b><br>
      The bar at the bottom shows how the browser goes online
      <img src="docs/screenshots/browser-find.png" alt="A web page in the PdaNet browser with find on page">
    </td>
    <td width="50%" valign="top">
    </td>
  </tr>
</table>

## What you need

- A Windows 10 or Windows 11 PC, 64 bit
- An Android phone
- A USB cable
- USB debugging turned on in the phone's Developer options (only for the phone VPN mode)
- For the phone VPN mode over WiFi, Android 11 or later with Wireless debugging turned on
- For the PdaNet browser, Microsoft Edge WebView2, which comes with Windows 11, when WebView2 is missing the browser offers the free download from Microsoft
- For screen share, PdaNet on both PCs. The N editions of Windows also need the free Media Feature Pack from Microsoft, for the video parts

## Getting the app

Download `PdaNetPortable.exe` from the Releases page of this project. Save it anywhere you like, such as your Desktop or a USB stick, and double click to open it. There is no setup step.

## How to use it

Pick how to connect in step 1, follow the short checklist in step 2 so your phone is ready, then press the large round button in step 3. Press it again to disconnect, which returns your PC's network settings to normal.

<details>
<summary><h3>Phone VPN mode (recommended)</h3></summary>

Use this when you want your PC to go online through the same connection, and the same VPN, that your phone is using. You do not need to switch anything on in the phone's USB menu.

1. On the phone, turn on Developer options and USB debugging
2. Connect the phone to the PC with a USB cable
3. On the phone, tap Allow when it asks whether to trust this computer
4. In the app, choose Phone VPN and press the connect button
5. Open a browser and visit a site that shows your location

If the phone is connected to a VPN, your PC will now appear to be in the same country as that VPN.

Step 2 shows how the app reaches your phone: USB cable, WiFi or Other. USB cable is the default. When your phone is connected both ways at once, the app uses only the way you picked. Other lists every phone and emulator the app can see, for when more than one is connected. A phone picked from that list is remembered, so a second phone never takes its place.

Before it connects, the app saves the proxy settings Windows has for your user account, and disconnecting puts them back. If the app cannot save them, the app changes nothing and does not connect. If the app stops without disconnecting, the next start puts them back.

</details>

<details>
<summary><h3>Phone VPN over WiFi</h3></summary>

Use this to go without the cable. You need Android 11 or later, with the phone and the PC on the same WiFi.

1. On the phone, open Developer options and turn on Wireless debugging
2. In the app, choose Phone VPN, then pick WiFi in step 2
3. The first time, press Pair over WiFi, tap Pair device with pairing code on the phone, and type the code from the phone into the app
4. Press the connect button

After the first time, the app can usually find your phone over WiFi by itself while Wireless debugging is on. If the phone does not show up, press Pair over WiFi, then Paired before?, and type the address from the Wireless debugging screen.

</details>

<details>
<summary><h3>USB mode</h3></summary>

Use this to share the phone's internet over the cable.

1. On the phone, turn on USB tethering, found under Settings, then Mobile Hotspot and Tethering
2. In the app, choose USB cable and press the connect button

</details>

<details>
<summary><h3>WiFi mode</h3></summary>

Use this to share the phone's hotspot.

1. On the phone, turn on the WiFi hotspot
2. In the app, choose WiFi hotspot, pick your hotspot or type its name and password, and press the connect button

</details>

<details>
<summary><h2>Hiding tethering from your carrier</h2></summary>

Some mobile plans try to detect when you share your phone's internet with another device. The app can make your PC's traffic look more like normal phone traffic, which helps avoid this. It runs on its own and does not need administrator rights.

This cannot be guaranteed on every network. The strongest option is the phone VPN mode above, because a VPN hides the traffic by encrypting it.

</details>

<details>
<summary><h2>Pretend you are elsewhere</h2></summary>

The app can set your PC's time zone, and optionally its country and date and number formats, to match another city. It needs no admin, and it is undone the moment you turn it off. Open it from the globe button on the home screen, or from Settings.

This changes what Windows and your apps read, such as the clock and the calendar. For what websites and online services see, which comes from your internet address, use Phone VPN mode with a VPN running on the phone. Together your PC reads as that place both on the desktop and online.

- Pick a city, or search for one
- Watch a live clock for that city next to your real time
- Turn it on, or have it turn on by itself each time you connect
- Turn it off to put your real time zone and region straight back

</details>

<details>
<summary><h2>See which apps use the internet</h2></summary>

The Apps page shows every app on your PC that uses the internet through PdaNet, live. Pick an app to see the websites the app visits, the data the app sends and receives, a chart of the last minute and one line for every connection. Double click a line to see the details.

- Websites with an address that starts with https stay private. PdaNet sees the website name, the time and the amount of data, never the content
- For websites with an address that starts with http, PdaNet shows what the app sent and the answer, with passwords hidden
- Search for an app or a website, show only problems, copy lines or save lines as a file for Excel
- Nothing is saved or sent anywhere unless you save a file yourself

App watch works in every mode. In Phone VPN mode PdaNet adds a small step in front of the phone link, so PdaNet can tell the apps apart. To turn app watch off, open Settings, then Apps. A few apps go online without PdaNet, and those apps do not show up.

<details>
<summary><h3>Block apps and websites</h3></summary>

Right click a line on the Apps page to block the app or the website, for one app or for every app. The details of a line have the same buttons. Blocks need no admin rights and start right away, open connections close at once. Blocked tries show up as red lines. Blocks stay on every time you connect. To see or remove blocks, open Blocked on the Apps page, or Settings.

- In Phone VPN mode blocks work for every app
- In USB and WiFi modes most apps go online through PdaNet, but a few apps, such as Windows Update, go online without PdaNet, and blocks cannot stop those apps without admin rights

</details>

</details>

<details>
<summary><h2>The Launchpad and the PdaNet browser</h2></summary>

The Launchpad holds apps running right inside PdaNet. The first app is the PdaNet browser, a browser with tabs in a separate window, built on Microsoft Edge WebView2.

- Open the Launchpad from the bar on the left, or right click the tray icon and choose Open browser
- Type a web address or a search on the Launchpad and press Enter, the browser opens straight on the page
- Pin the websites you use every day as web apps, with Add web app on the Launchpad or with the star in the address bar. A web app opens in one click from the Launchpad and from every new tab, in the tab where the web app is already open
- As you type, the address bar suggests your web apps and the websites you opened before
- Find on page, zoom, print, downloads, full screen and the search engine are in the menu, with the usual keys such as Ctrl and T for a new tab and Ctrl and F to find
- A new tab can bring back the tabs from last time
- The bar at the bottom of the browser shows how the browser goes online. While PdaNet is connected and app watch is on, the bar says Through PdaNet once PdaNet sees the browser go online. On some company PCs, network settings of the company win over PdaNet, the bar then says Not seen by PdaNet
- Sign ins, history and website data stay in the data folder next to PdaNet, apart from Edge and Chrome. Clear browsing data in the menu removes the sign ins, history and website data

</details>

<details>
<summary><h2>Let an AI agent drive the browser</h2></summary>

PdaNet has a built in MCP (Model Context Protocol) server, so an AI agent can drive the PdaNet browser for you: open pages, read them, fill forms, click buttons and take screenshots. Because it drives the real PdaNet browser, the agent stays signed in to your websites and, while PdaNet is connected, browses through your phone's connection.

Start it from a terminal, or point your MCP client at it:

```
PdaNetPortable.exe --mcp
```

- It talks to the agent over stdio and opens a real browser window you can watch
- The agent sees each page as a list of elements it can click and type into, and acts on them with trusted clicks and keystrokes, so it works on modern websites
- It offers a full set of browser tools: navigate, tabs, snapshot, read, screenshot, click, type, press key, hover, select, scroll, drag, wait, run JavaScript, read the console, and check whether traffic is going through PdaNet
- Add `--hidden` to keep the window off screen for unattended runs, or run `PdaNetPortable.exe --mcp-smoke` for a quick built in self check

For client setup (Claude Desktop, Claude Code, or any MCP client), the full tool list and the safety notes, see [docs/mcp.md](docs/mcp.md).

</details>

<details>
<summary><h2>Share your screen</h2></summary>

Screen share on the Launchpad lets one PC see and use another, to help a friend or to reach your own PC from another room. Both PCs run PdaNet. There is no account, no server to set up and nothing to install.

On the PC that shares its screen

1. Open the Launchpad and press Share my screen
2. Read the ten digit code to the other person, or press Copy and send it in a message
3. With the switch, choose whether they may use your mouse and keyboard. You can change it at any time

On the PC that sees the screen

1. Open the Launchpad, type the code under Connect to a screen and press Connect
2. The other PC's screen opens in a window of its own

While you share, a small bar at the top of your screen says who sees it, with a Control switch and a Stop button. The bar never shows up in the picture. You can also stop from the Launchpad or the tray icon.

In the window with the other PC's screen

- Fit shows the whole screen in the window, Actual size shows it pixel for pixel
- Full screen shows only the other PC's screen, Ctrl+Alt+Break goes back
- The keyboard button sends the keys Windows keeps for itself, such as the Windows key, Alt+Tab and Ctrl+Shift+Esc
- Picture picks Auto, Sharp for text or Smooth for video
- When the other PC has more than one screen, pick the one to see at the top
- Copied text goes both ways while you may use the other PC's mouse and keyboard

The connection

- Direct links the two PCs the shortest way, straight across your network when they share one, else over the internet
- Through your phone sends the screen through the phone PdaNet is connected to, in every mode, Phone VPN included, so it takes the phone's network and VPN. This counts against your mobile data. A still screen sends almost nothing, video and scrolling send the most

Safe by design

- The code works once and only while you share. After five wrong tries the code changes
- Everything between the two PCs is encrypted end to end. The last six digits of the code never leave the two PCs, both prove they know them without sending them
- To find each other, the two PCs leave sealed notes on public relays (Nostr, MQTT and ntfy). The relays never see the code, the screen or anything you type. Without internet, two PCs on the same network find each other there

Light on your PC

- The screen goes from the capture to the encoder inside the graphics chip, without passing through the processor
- Nothing is sent while the screen is still. When it settles, two more pictures make the text sharp
- On a laptop with Intel graphics, sharing a 1080p screen adds about 60 MB to PdaNet and seeing one about 70 MB. Both give the memory back when the session ends

</details>

<details>
<summary><h2>Settings</h2></summary>

Open Settings from the main screen to change things such as

- Whether hiding (stealth) is on
- How many times to retry when the connection drops
- How much data to use before you see a warning
- Whether the Apps page shows the apps that use the internet
- Which port the app uses on your PC
- A path to your own copy of adb, if you prefer that

</details>

<details>
<summary><h2>If something does not work</h2></summary>

- Phone not found in VPN mode, check that USB debugging is on and that you tapped Allow on the phone
- No phone on the USB cable while your phone is on WiFi, press Use WiFi, or pick WiFi in step 2
- USB mode says there is no connection, turn on USB tethering on the phone and keep mobile data on
- The browser still shows your real location, some company or school PCs force their own network proxy that overrides the app, in that case open the connection details on the Connect page, copy the proxy address, and set it in your browser
- Nothing seems to happen when you connect, open the Activity page in the app to see which step failed
- Screen share does not connect, check that both PCs are online. When one of the PCs is on a strict company or school network, try Through your phone
- Without internet, Windows may ask once whether pdanet-rtc may use the network. Allow it on private networks, so two PCs on the same network can find each other

</details>

<details>
<summary><h2>Privacy</h2></summary>

The app keeps its settings in a folder named data next to the program. WiFi passwords are stored in encrypted form using Windows. The app itself does not send your information anywhere. The Apps page keeps the list only while PdaNet runs, unless you save the list to a file.

Screen share sends your screen, and the mouse and keyboard, only to the PC that typed your code, encrypted end to end. The public relays the two PCs meet through carry only sealed notes, and nothing is stored there.

</details>

<details>
<summary><h2>For developers</h2></summary>

The app is built with .NET 9 and WPF for Windows. The released program is a single self contained file, so it carries its own copy of .NET, a small Android helper, and Google's adb tool. That is why it needs no installer and no admin rights.

Build it:

```
cd pdanet-windows
dotnet publish -c Release
```

The finished program appears at `pdanet-windows/dist/PdaNetPortable.exe`.

The Android helper is a small Java HTTP proxy in `agent/ProxyMain.java`. A prebuilt copy is already included in the app, so you only need to rebuild it if you change that file. The rebuild uses the Eclipse Java compiler and Google's d8 tool and needs no Android SDK. See `agent/build.bat` for the two tools to download and the steps.

The screen share helper, `pdanet-rtc.exe`, is built from the Go code in `rtc` with Pion WebRTC. It finds the other PC, agrees on a key from the code with SPAKE2 (RFC 9382) and carries the picture and the input over WebRTC. A prebuilt copy is already included in the app. To rebuild it after changing that code, install Go and run `rtc/build.bat`. The parts it and the app use from others, and their licenses, are listed in [docs/third-party-notices.md](docs/third-party-notices.md).

Three built in checks run without opening the window:

```
PdaNetPortable.exe --selftest
PdaNetPortable.exe --selftest-agent
PdaNetPortable.exe --selftest-monitor
```

The last check tests the Apps page and blocks. The check changes no Windows setting and needs no phone.

Screen share has its own check, which tests the picture from capture to decoding, and a whole session between two helpers on this PC. Add `online` to meet through the public relays, or `memory` to see what each part costs in memory:

```
PdaNetPortable.exe --selftest-share
```

</details>

<details>
<summary><h2>How it works, in short</h2></summary>

In the phone VPN mode the app runs a tiny helper on the phone through adb and then points Windows at it. Because that helper runs as a normal program on the phone, your PC's traffic follows the phone's connection, including any VPN. In USB and WiFi modes the app routes traffic through the phone and adjusts it so the connection looks like ordinary phone use.

Screen share takes the screen with Windows' Desktop Duplication, turns it into H.264 video on the graphics chip and sends it with WebRTC straight to the other PC, where Windows' own decoder shows it. Through your phone, the same traffic goes through the phone's connection, over the cable and the phone helper in Phone VPN mode.

</details>

<details>
<summary><h2>License</h2></summary>

MIT. See the LICENSE file.

</details>

<details>
<summary><h2>Thanks</h2></summary>

Inspired by the pdanet-linux project and by PdaNet. All trademarks belong to their owners.

</details>
