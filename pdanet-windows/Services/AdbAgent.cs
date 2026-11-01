using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;
using PdaNetPortable.Views;

namespace PdaNetPortable.Services;

/// <summary>One way adb reaches a phone, a line of "adb devices -l".</summary>
public sealed record PhoneLink(string Serial, LinkKind Kind, string State, string? Model, string? Name = null)
{
    /// <summary>The phone's own serial number (ro.serialno), the same on the cable and over WiFi, when known.</summary>
    public string? PhoneId { get; init; }

    public bool Ready => State == "device";

    /// <summary>Which phone the link reaches: the phone's serial number when known, else the link's own id.</summary>
    public string Key => PhoneId ?? MdnsSerial ?? Serial;

    // A WiFi link adb found by itself is named "adb-<phone serial>-<random>._adb-tls-connect._tcp".
    private string? MdnsSerial =>
        Serial.StartsWith("adb-") && Serial.IndexOf("._adb", StringComparison.Ordinal) is var end and > 4
        && Serial.LastIndexOf('-', end) is var dash and > 4 ? Serial[4..dash] : null;

    /// <summary>What to call the phone: the name the owner gave the phone, else the model.</summary>
    public string Label => Kind == LinkKind.Emulator ? "Android emulator" : Name ?? Model ?? "Android phone";

    /// <summary>The address without the port for WiFi links, the serial for the others.</summary>
    public string Id =>
        Serial.IndexOf("._adb", StringComparison.Ordinal) is var dot and > 0 ? Serial[..dot]
        : Kind == LinkKind.Wifi && Serial.Contains(':') ? Serial[..Serial.LastIndexOf(':')]
        : Serial;
}

/// <summary>A phone that offers Wireless debugging on this WiFi, a line of "adb mdns services".</summary>
public sealed record WifiService(string Name, bool Pairing, string Address);

/// <summary>
/// Drives the phone-side agent over ADB (the robust "option 2" path).
///
/// Flow: push the embedded dex (ProxyMain) to the device, launch it with `app_process`
/// (runs as the shell user, so the phone's VPN captures it), `adb forward` a PC port to the
/// agent, and point Windows at that port. No native tethering, no mobile-data toggle, no PC
/// admin. The agent re-originates connections on the phone, so tethered PC traffic inherits
/// whatever the phone uses — including an active VPN.
/// </summary>
public static class AdbAgent
{
    private const string DexRemote = "/data/local/tmp/pdanet-agent.dex";
    private const string AgentClass = "ProxyMain";

    public static string? AdbPath { get; private set; }
    /// <summary>The phone Init picked, every adb call names this phone with -s.</summary>
    public static string? Serial { get; private set; }
    /// <summary>The link Init picked, for the connection details.</summary>
    public static PhoneLink? Link { get; private set; }
    /// <summary>What adb listed last, from the Connect page check or from Init.</summary>
    public static IReadOnlyList<PhoneLink> LastLinks { get; private set; } = Array.Empty<PhoneLink>();
    private static Process? _agentProc;
    private static readonly object _extractLock = new();
    // Per link: the name the owner gave the phone in About phone and the phone's serial number, read once.
    private static readonly ConcurrentDictionary<string, (string? Name, string? Id)> _info = new();
    private static readonly HashSet<string> States = new()
        { "device", "unauthorized", "offline", "authorizing", "connecting", "recovery", "rescue", "sideload", "bootloader", "no" };

    /// <summary>
    /// Read only check for the Connect page and the phone list: every link adb lists, the USB
    /// cable first, then WiFi, then emulators. Screenshot demos get sample phones and never run adb.
    /// </summary>
    public static (bool HasAdb, IReadOnlyList<PhoneLink> Links) List(string? configured)
    {
        if (Demo.Active) return (true, LastLinks = SampleLinks(Demo.Value("links")));
        var adb = FindAdb(configured);
        if (adb == null) return (false, LastLinks = Array.Empty<PhoneLink>());

        return (true, LastLinks = Known(adb, Parse(ProcessRunner.Run(adb, "devices -l", 10000).StdOut)));
    }

    /// <summary>Adds the phone's name and serial number to each ready link, asking each phone only once.</summary>
    private static List<PhoneLink> Known(string adb, List<PhoneLink> links) => links
        .Select(l => !l.Ready || l.Kind == LinkKind.Emulator ? l
            : _info.GetOrAdd(l.Serial, s => DeviceInfo(adb, s)) is var (name, id) ? l with { Name = name, PhoneId = id } : l)
        .ToList();

    /// <summary>The name the owner gave the phone, such as Galaxy S25 Ultra, and the phone's serial number.</summary>
    private static (string? Name, string? Id) DeviceInfo(string adb, string serial)
    {
        var r = ProcessRunner.Run(adb,
            $"-s \"{serial}\" shell \"echo N=$(settings get global device_name); echo S=$(getprop ro.serialno)\"", 5000);
        string? Value(string tag)
        {
            var v = r.StdOut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(tag))?[tag.Length..].Trim();
            return v is { Length: > 0 and <= 40 } && v != "null" ? v : null;
        }
        return r.Ok ? (Value("N="), Value("S=")) : (null, null);
    }

    /// <summary>Reads "adb devices -l" lines such as "RFCY10FRKBK  device product:x model:SM_S938B".</summary>
    private static List<PhoneLink> Parse(string output)
    {
        var links = new List<PhoneLink>();
        foreach (var line in output.Split('\n'))
        {
            var tok = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 2 || !States.Contains(tok[1])) continue;
            var model = tok.FirstOrDefault(t => t.StartsWith("model:"))?.Substring(6).Replace('_', ' ');
            links.Add(new PhoneLink(tok[0], KindOf(tok[0]), tok[1], model));
        }
        return links.OrderBy(l => l.Kind).ToList();
    }

    public static LinkKind KindOf(string serial) =>
        serial.StartsWith("emulator-") || serial.StartsWith("127.0.0.1:") || serial.StartsWith("localhost:") ? LinkKind.Emulator
        : serial.Contains(':') || serial.Contains("._adb") ? LinkKind.Wifi
        : LinkKind.Usb;

    /// <summary>
    /// The link a choice points at. "usb" and "wifi" take a link of that kind, "other" a link of
    /// any kind. With a phone picked from the list only that phone counts, so a second phone never
    /// takes over. Without one, "usb" and "wifi" take the first link of that kind, a ready one first.
    /// </summary>
    public static PhoneLink? Pick(IReadOnlyList<PhoneLink> links, string? choice, string? phone)
    {
        LinkKind? kind = choice switch { "wifi" => LinkKind.Wifi, "other" => null, _ => LinkKind.Usb };
        bool picked = !string.IsNullOrEmpty(phone);
        if (kind == null && !picked) return null;
        return links
            .Where(l => (kind == null || l.Kind == kind) && (!picked || l.Key == phone || l.Serial == phone))
            .OrderBy(l => l.Ready ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>What to save for a phone picked from the list: the way to the phone, and the phone itself.</summary>
    public static (string Choice, string Phone) ChoiceFor(PhoneLink link) => link.Kind switch
    {
        LinkKind.Usb => ("usb", link.Key),
        LinkKind.Wifi => ("wifi", link.Key),
        _ => ("other", link.Key),
    };

    /// <summary>How many more ready phones the same way could mean when no phone was picked, such as a second phone on the cable.</summary>
    public static int Rivals(IReadOnlyList<PhoneLink> links, string? choice, string? phone)
    {
        if (!string.IsNullOrEmpty(phone) || choice == "other") return 0;
        var kind = choice == "wifi" ? LinkKind.Wifi : LinkKind.Usb;
        return Math.Max(0, links.Where(l => l.Ready && l.Kind == kind).Select(l => l.Key).Distinct().Count() - 1);
    }

    /// <summary>
    /// A phone that is ready another way than the choice: the picked phone itself first when adb
    /// can tell, then the cable before WiFi. The Connect page offers this phone as its one click
    /// fix and Problem names the same phone, so the fix it steps and the button always agree.
    /// </summary>
    public static PhoneLink? Alternative(IReadOnlyList<PhoneLink> links, string? choice, string? phone)
    {
        var kind = choice == "wifi" ? LinkKind.Wifi : LinkKind.Usb;
        var me = !string.IsNullOrEmpty(phone) ? phone : Pick(links, choice, phone)?.Key;
        return links
            .Where(l => l.Ready && l.Kind != LinkKind.Emulator && (choice == "other" || l.Kind != kind))
            .OrderBy(l => me != null && l.Key == me ? 0 : 1).ThenBy(l => l.Kind)
            .FirstOrDefault();
    }

    /// <summary>
    /// Why a choice cannot be used right now, in plain words, or null when the phone is ready.
    /// Ends with the phone from Alternative when there is one, such as ", Galaxy S25 Ultra is ready over WiFi".
    /// </summary>
    public static string? Problem(IReadOnlyList<PhoneLink> links, string? choice, string? phone)
    {
        var link = Pick(links, choice, phone);
        if (link is { Ready: true }) return null;

        var alt = Alternative(links, choice, phone);
        var elsewhere = alt == null ? "" : $", {alt.Label} is ready {Friendly.LinkWords(alt.Kind)}";
        bool picked = !string.IsNullOrEmpty(phone);
        bool wifi = link?.Kind == LinkKind.Wifi || (link == null && choice == "wifi");
        if (link?.State == "unauthorized")
            return (wifi
                ? "Your phone has not allowed this PC yet, unlock the phone and tap Allow when the phone asks about wireless debugging"
                : "Your phone has not allowed this PC yet, unlock the phone and tap Allow when the phone asks about USB debugging")
                + elsewhere;
        if (link != null)
            return (wifi
                ? "Your phone is not responding over WiFi, turn Wireless debugging off and on again"
                : "Your phone is not responding, unplug the cable and plug the cable back in")
                + elsewhere;

        return choice switch
        {
            "wifi" => (picked ? "The phone you picked is not over WiFi" : "No phone over WiFi")
                      + ", turn on Wireless debugging on the phone",
            "other" => "The phone you picked is not connected, plug the phone back in or pick another phone",
            _ => (picked ? "The phone you picked is not on the USB cable" : "No phone on the USB cable")
                 + ", plug the phone in and turn on USB debugging",
        } + elsewhere;
    }

    /// <summary>The phone serial in a WiFi name such as "adb-RFCY10FRKBK-Qm3x7T", which adb pair reports.</summary>
    public static string? SerialInWifiName(string? name) =>
        name != null && name.StartsWith("adb-") && name.LastIndexOf('-') is var dash and > 4 ? name[4..dash] : null;

    /// <summary>Screenshot only sample phones, for example "usb+wifi", "usbwait" or "none".</summary>
    private static List<PhoneLink> SampleLinks(string spec)
    {
        var links = new List<PhoneLink>();
        foreach (var key in (spec.Length == 0 ? "usb" : spec).Split('+'))
        {
            PhoneLink? link = key switch
            {
                "usb" => new("RFCY10FRKBK", LinkKind.Usb, "device", "SM S938B", "Galaxy S25 Ultra") { PhoneId = "RFCY10FRKBK" },
                "usbwait" => new("RFCY10FRKBK", LinkKind.Usb, "unauthorized", null),
                "wifi" => new("192.168.50.57:44511", LinkKind.Wifi, "device", "SM S938B", "Galaxy S25 Ultra") { PhoneId = "RFCY10FRKBK" },
                "wifiwait" => new("192.168.50.57:44511", LinkKind.Wifi, "unauthorized", null),
                "usb2" => new("R5CY42LQWXM", LinkKind.Usb, "device", "SM A566B", "Galaxy A56 5G") { PhoneId = "R5CY42LQWXM" },
                "emu" => new("emulator-5554", LinkKind.Emulator, "device", "sdk gphone64 x86 64"),
                _ => null,
            };
            if (link != null) links.Add(link);
        }
        return links.OrderBy(l => l.Kind).ToList();
    }

    // ============================ WiFi pairing ============================
    /// <summary>
    /// Phones on this WiFi with Wireless debugging on. A pairing entry shows up only while the
    /// phone shows the Pair device with pairing code box.
    /// </summary>
    public static List<WifiService> WifiServices(string? configured)
    {
        if (Demo.Active)
            return Demo.Value("pair") == "none" ? new List<WifiService>()
                : new List<WifiService>
                {
                    new("adb-RFCY10FRKBK-Qm3x7T", true, "192.168.50.57:37845"),
                    new("adb-RFCY10FRKBK-Qm3x7T", false, "192.168.50.57:44511"),
                };
        var adb = FindAdb(configured);
        if (adb == null) return new();

        var found = new List<WifiService>();
        foreach (var line in ProcessRunner.Run(adb, "mdns services", 8000).StdOut.Split('\n'))
        {
            var tok = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int type = Array.FindIndex(tok, t => t.Contains("_adb-tls-"));
            if (type < 0 || type == tok.Length - 1) continue;
            // Usually "name  type  address", some adb builds join the name and the type with a dot.
            var name = type > 0 ? string.Join(" ", tok[..type]) : tok[0][..Math.Max(0, tok[0].IndexOf("._adb-tls-", StringComparison.Ordinal))];
            found.Add(new(name, tok[type].Contains("pairing"), tok[^1]));
        }
        return found;
    }

    /// <summary>
    /// Pairs this PC with the phone, using the code and the address in the phone's pairing box.
    /// On success returns the phone's name on this WiFi, which the phone's link address shares.
    /// </summary>
    public static (bool Ok, string? Name, string Output) Pair(string? configured, string address, string code)
    {
        // Demo "pairname:none" acts like an adb that does not report which phone it paired.
        if (Demo.Active) return (true, Demo.Value("pairname") == "none" ? null : "adb-RFCY10FRKBK-Qm3x7T", "Successfully paired");
        var adb = FindAdb(configured);
        if (adb == null) return (false, null, "The adb helper is missing");
        var all = ProcessRunner.Run(adb, $"pair {address} {code}", 20000).All;
        if (!all.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)) return (false, null, all);
        var guid = Regex.Match(all, @"guid=([^\]\s]+)");
        return (true, guid.Success ? guid.Groups[1].Value : null, all);
    }

    /// <summary>Links a paired phone over WiFi, at the address on the Wireless debugging screen.</summary>
    public static (bool Ok, string Output) ConnectWifi(string? configured, string address)
    {
        if (Demo.Active) return (true, "connected");
        var adb = FindAdb(configured);
        if (adb == null) return (false, "The adb helper is missing");
        var all = ProcessRunner.Run(adb, $"connect {address}", 15000).All;
        bool ok = all.StartsWith("connected to", StringComparison.OrdinalIgnoreCase)
                  || all.StartsWith("already connected", StringComparison.OrdinalIgnoreCase);
        return (ok, all);
    }

    public static string? FindAdb(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        // Prefer the bundled adb so the app is fully portable (no separate install).
        var bundled = ExtractBundledAdb();
        if (bundled != null) return bundled;

        var onPath = ProcessRunner.Run("where", "adb");
        if (onPath.Ok)
        {
            var hit = onPath.StdOut.Split('\n').Select(s => s.Trim())
                .FirstOrDefault(s => s.EndsWith("adb.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s));
            if (hit != null) return hit;
        }
        foreach (var p in new[]
                 {
                     @"C:\platform-tools\adb.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Android", "Sdk", "platform-tools", "adb.exe"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }

    private static ProcessRunner.Result Adb(string args, int timeout = 30000)
        => ProcessRunner.Run(AdbPath ?? "adb", Target + args, timeout);

    /// <summary>
    /// adb refuses phone commands while more than one device is listed, for example the same
    /// phone over the USB cable and over WiFi debugging, so name the phone Init picked.
    /// </summary>
    private static string Target => Serial == null ? "" : $"-s \"{Serial}\" ";

    /// <summary>Extract the embedded adb.exe + its DLLs to the app data dir and return its path.</summary>
    private static string? ExtractBundledAdb()
    {
        lock (_extractLock) return ExtractBundledAdbLocked();
    }

    private static string? ExtractBundledAdbLocked()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "adb");
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            var files = new[]
            {
                ("PdaNetPortable.adb.adb.exe", "adb.exe"),
                ("PdaNetPortable.adb.AdbWinApi.dll", "AdbWinApi.dll"),
                ("PdaNetPortable.adb.AdbWinUsbApi.dll", "AdbWinUsbApi.dll"),
            };
            foreach (var (res, name) in files)
            {
                var dst = Path.Combine(dir, name);
                if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue; // keep (may be in use)
                using var rs = asm.GetManifestResourceStream(res);
                if (rs == null) return null;
                using var fs = File.Create(dst);
                rs.CopyTo(fs);
            }
            var adb = Path.Combine(dir, "adb.exe");
            return File.Exists(adb) ? adb : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds the phone for the saved choice ("usb", "wifi" or "other", plus a phone picked from
    /// the list) and keeps the link's serial for every later call, so a second link never confuses adb.
    /// </summary>
    public static bool Init(string? configured, string? choice, string? phone, out string error)
    {
        error = "";
        Serial = null;
        Link = null;
        AdbPath = FindAdb(configured);
        if (AdbPath == null)
        {
            error = "The adb helper is missing, set a custom adb program in Settings, Advanced";
            return false;
        }

        var links = Known(AdbPath, Parse(Adb("devices -l").StdOut));
        LastLinks = links;
        var problem = Problem(links, choice, phone);
        if (problem != null)
        {
            error = problem;
            return false;
        }

        Link = Pick(links, choice, phone)!;
        Serial = Link.Serial;
        int rivals = Rivals(links, choice, phone);
        if (rivals > 0)
            Logger.Instance.Warning($"{rivals + 1} phones are {Friendly.LinkWords(Link.Kind)}, PdaNet uses {Link.Label}, " +
                                    "pick a phone under Other in step 02 to choose");
        return true;
    }

    public static bool Deploy(out string error)
    {
        error = "";
        var tmp = Path.Combine(Path.GetTempPath(), "pdanet-agent.dex");
        try
        {
            using var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("PdaNetPortable.proxy.dex");
            if (rs == null) { error = "The phone helper is missing from this app build"; return false; }
            using var fs = File.Create(tmp);
            rs.CopyTo(fs);
        }
        catch (Exception e) { error = "Could not unpack the phone helper: " + e.Message; return false; }

        KillRemote();
        var push = Adb($"push \"{tmp}\" {DexRemote}");
        if (!push.Ok) { error = "Could not copy the helper to your phone: " + push.All; return false; }
        return true;
    }

    public static bool StartAgent(int agentPort, out string error)
    {
        error = "";
        try
        {
            // The agent lives for as long as this adb-shell process does; we kill it on disconnect.
            var psi = new ProcessStartInfo
            {
                FileName = AdbPath ?? "adb",
                Arguments = $"{Target}shell CLASSPATH={DexRemote} app_process /system/bin {AgentClass} {agentPort}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _agentProc = Process.Start(psi);
            if (_agentProc == null) { error = "Could not start adb"; return false; }

            bool listening = false;
            _agentProc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Logger.Instance.Debug("[agent] " + e.Data);
                if (e.Data.Contains("listening")) listening = true;
            };
            _agentProc.ErrorDataReceived += (_, e) => { if (e.Data != null) Logger.Instance.Debug("[agent] " + e.Data); };
            _agentProc.BeginOutputReadLine();
            _agentProc.BeginErrorReadLine();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 7000 && !listening && !_agentProc.HasExited)
                Thread.Sleep(200);

            if (_agentProc.HasExited)
            {
                error = Link?.Kind == LinkKind.Wifi
                    ? "The phone helper stopped right away, check that Wireless debugging is still on"
                    : "The phone helper stopped right away, check that USB debugging is still on";
                return false;
            }
            if (!listening) Logger.Instance.Warning("The phone helper did not confirm it is ready, continuing anyway");
            return true;
        }
        catch (Exception e) { error = "Could not start the phone helper: " + e.Message; return false; }
    }

    public static bool Forward(int localPort, int agentPort, out string error)
    {
        error = "";
        var r = Adb($"forward tcp:{localPort} tcp:{agentPort}");
        if (!r.Ok) { error = "Could not link this PC to the phone: " + r.All; return false; }
        return true;
    }

    /// <summary>Stops the phone helper and removes the adb link on localPort (0 when no link was made).</summary>
    public static void Stop(int localPort)
    {
        try { if (_agentProc is { HasExited: false }) _agentProc.Kill(true); } catch { }
        _agentProc = null;
        if (localPort > 0) RemoveForward(localPort);
        KillRemote();
    }

    public static void RemoveForward(int localPort)
    {
        try { Adb($"forward --remove tcp:{localPort}"); } catch { }
    }

    /// <summary>Kill any lingering agent on the device (parse ps, avoid pkill/awk which vary).</summary>
    private static void KillRemote()
    {
        try
        {
            var ps = Adb("shell ps -A -o PID,ARGS");
            if (!ps.Ok) ps = Adb("shell ps -A");
            foreach (var line in ps.StdOut.Split('\n'))
            {
                if (!line.Contains(AgentClass) || line.Contains("grep")) continue;
                var tok = line.TrimStart().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length > 0 && int.TryParse(tok[0], out var pid))
                    Adb($"shell kill {pid}");
            }
        }
        catch { }
    }
}
