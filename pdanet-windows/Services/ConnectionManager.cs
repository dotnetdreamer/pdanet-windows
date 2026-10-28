using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Models;

namespace PdaNetPortable.Services;

/// <summary>
/// Connection orchestration + state machine + auto-reconnect, ported from
/// connection_manager.py. Per-mode behaviour matches the upstream scripts:
///   USB    = verify the 192.168.49.1:8000 HTTP proxy, front it with the local relay,
///            set the system proxy, then apply carrier bypass.
///   WiFi   = associate with the hotspot, verify internet, apply carrier bypass (no proxy).
///   iPhone = WiFi flow with aggressive bypass.
/// </summary>
public sealed class ConnectionManager
{
    private static readonly Lazy<ConnectionManager> _instance = new(() => new ConnectionManager());
    public static ConnectionManager Instance => _instance.Value;

    private readonly object _gate = new();
    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly Logger _log = Logger.Instance;
    private readonly StatsCollector _stats = StatsCollector.Instance;

    private LocalProxy? _relay;
    private int _forwardPort; // PC port adb forwards to the phone helper (Phone VPN mode)
    private System.Threading.Timer? _monitor;
    private int _reconnectAttempts;
    private volatile bool _busy;
    private bool _systemBypassApplied; // true only when an elevated system-wide bypass is active

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ConnectionMode CurrentMode { get; private set; } = ConnectionMode.Wifi;
    public TetherInterface? CurrentInterface { get; private set; }
    public string? CurrentSsid { get; private set; }
    public bool BypassActive { get; private set; }
    public bool SystemBypassActive => _systemBypassApplied;
    public bool AutoReconnectEnabled { get; set; }
    public string? LastError { get; private set; }

    /// <summary>True when app traffic passes through our relay and the Apps page can record it.</summary>
    public bool WatchingApps => _cfg.WatchApps && _relay is { Running: true };

    /// <summary>True when app traffic passes through our relay, so your block list applies.</summary>
    public bool RelayActive => _relay is { Running: true };

    // Remembered for auto-reconnect.
    private string? _lastSsid;
    private string? _lastPassword;

    public event Action<ConnectionState>? StateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? Tick; // fired ~1/sec while connected (stats refreshed)

    private ConnectionManager()
    {
        AutoReconnectEnabled = _cfg.AutoReconnect;
    }

    public bool IsConnected => State == ConnectionState.Connected;

    private void SetState(ConnectionState next)
    {
        lock (_gate)
        {
            if (State == next) return;
            if (!StateMachine.CanTransition(State, next))
            {
                _log.Warning($"Ignored a status change from {State} to {next}");
                if (next != ConnectionState.Error) return;
            }
            _log.Info($"Status changed from {State} to {next}");
            State = next;
        }
        try { StateChanged?.Invoke(next); } catch { }
    }

    private void RaiseError(string message)
    {
        LastError = message;
        _log.Error(message);
        try { ErrorOccurred?.Invoke(message); } catch { }
    }

    // ---- Public API ----
    public void Connect(ConnectionMode mode, string? ssid = null, string? password = null)
    {
        if (_busy) { _log.Warning("Connect skipped, another action is still running"); return; }
        Task.Run(() => ConnectWorker(mode, ssid, password));
    }

    public void Disconnect()
    {
        if (_busy) { _log.Warning("Disconnect skipped, another action is still running"); return; }
        Task.Run(() => DisconnectWorker(userInitiated: true));
    }

    // ---- Workers ----
    private void ConnectWorker(ConnectionMode mode, string? ssid, string? password)
    {
        _busy = true;
        try
        {
            CurrentMode = mode;
            _lastSsid = ssid;
            _lastPassword = password;
            SetState(ConnectionState.Connecting);

            bool ok = mode switch
            {
                ConnectionMode.Usb => ConnectUsb(),
                ConnectionMode.AdbAgent => ConnectAdbAgent(),
                _ => ConnectWifi(mode, ssid, password), // wifi + iphone
            };

            if (!ok)
            {
                SetState(ConnectionState.Error);
                return;
            }

            // USB or WiFi without the stealth proxy has no relay yet, add one so the Apps page can watch
            // and your block list applies.
            if (mode != ConnectionMode.AdbAgent && _relay == null && (_cfg.WatchApps || TrafficRules.Instance.Any))
                StartWatchRelay();

            _reconnectAttempts = 0;
            _stats.StartSession();
            SetState(ConnectionState.Connected);
            StartMonitor();
            _log.Ok($"Connected with {mode.Display()}");
        }
        catch (Exception e)
        {
            RaiseError($"Connection failed: {e.Message}");
            SetState(ConnectionState.Error);
        }
        finally { _busy = false; }
    }

    private bool ConnectUsb()
    {
        _log.Info("[1/5] Finding the USB connection");
        var iface = InterfaceDetector.DetectUsbInterface();
        if (iface == null)
        {
            RaiseError("No USB tethering found, turn on USB tethering on your phone " +
                       "(Settings, Hotspot and tethering, USB tethering) or PdaNet+ USB mode, and keep mobile data on");
            return false;
        }
        CurrentInterface = iface;
        _stats.SetInterface(iface.Nic);
        _log.Ok($"Found {iface.Alias} ({iface.Description})");

        _log.Info("[2/5] Checking for the PdaNet sharing service");
        bool proxyOk = InterfaceDetector.ProxyReachable(_cfg.ProxyIp, _cfg.ProxyPort, Constants.ProxyCheckTimeoutSeconds * 1000);
        if (proxyOk)
        {
            _log.Ok($"PdaNet sharing service found at {_cfg.ProxyIp}:{_cfg.ProxyPort}");
        }
        else
        {
            // Standard Android USB tethering (RNDIS) provides direct NAT internet with no proxy.
            _log.Info("[3/5] Choosing the best route, using direct USB tethering");
            if (!InterfaceDetector.InternetReachable())
            {
                RaiseError($"The USB connection {iface.Alias} has no internet and the PdaNet sharing service at " +
                           $"{_cfg.ProxyIp}:{_cfg.ProxyPort} is not reachable, turn on mobile data and USB tethering on your phone");
                return false;
            }
            _log.Ok($"Direct USB tethering is working on {iface.Alias}");
        }

        _log.Info("[4/5] Turning on carrier stealth");
        if (!ApplyBypass(iface, usePdaProxy: proxyOk))
            return false;

        _log.Info("[5/5] Checking the internet");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Could not confirm internet yet, it may still work");

        CurrentSsid = null;
        return true;
    }

    private bool ConnectAdbAgent()
    {
        _log.Info("[1/4] Looking for your phone");
        if (!AdbAgent.Init(_cfg.AdbPath, out var err)) { RaiseError(err); return false; }
        _log.Ok($"Phone found, using adb at {AdbAgent.AdbPath}");

        _log.Info("[2/4] Starting the helper on your phone");
        if (!AdbAgent.Deploy(out err)) { RaiseError(err); return false; }
        if (!AdbAgent.StartAgent(_cfg.AgentPort, out err)) { RaiseError(err); return false; }
        _log.Ok($"Phone helper is running on port {_cfg.AgentPort}");

        _log.Info("[3/4] Linking this PC to your phone");
        if (!LinkPhone(out err)) { RaiseError(err); return false; }
        SystemProxy.SetWinInet($"127.0.0.1:{_cfg.LocalProxyPort}");
        _log.Ok(_relay != null
            ? $"Windows proxy points at 127.0.0.1:{_cfg.LocalProxyPort}, linked to the phone helper, app watch is on"
            : $"Windows proxy points at 127.0.0.1:{_cfg.LocalProxyPort}, linked to the phone helper");

        _log.Info("[4/4] Checking the internet");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Could not confirm internet yet, continuing");

        // The agent re-originates on the phone, so the phone/VPN governs the exit (no PC-side TTL needed).
        CurrentInterface = new TetherInterface("adb-agent", "Phone helper over USB", null, null);
        CurrentSsid = null;
        BypassActive = false;
        _stats.SetInterface(null);
        return true;
    }

    private bool ConnectWifi(ConnectionMode mode, string? ssid, string? password)
    {
        _log.Info("[1/4] Finding WiFi on this PC");
        var wifi = InterfaceDetector.DetectWifiInterface();            // .NET-based, no netsh dependency
        var wifiAlias = wifi?.Alias ?? WifiManager.GetWifiInterfaceAlias();
        if (wifiAlias == null)
        {
            RaiseError("No WiFi adapter found on this PC");
            return false;
        }
        bool alreadyUp = wifi?.Nic is { OperationalStatus: OperationalStatus.Up };
        _log.Ok($"WiFi adapter {wifiAlias}");

        _log.Info("[2/4] Joining your hotspot");
        var current = WifiManager.GetCurrentSsid();                    // best-effort; may be blocked on managed PCs
        if (!string.IsNullOrEmpty(ssid) && !string.Equals(current, ssid, StringComparison.Ordinal))
        {
            if (WifiManager.Connect(ssid!, password))
            {
                if (!string.IsNullOrEmpty(password)) SecretStore.SetPassword(ssid!, password!);
                current = ssid;
            }
            else if (alreadyUp)
            {
                _log.Warning($"Could not switch to {ssid} automatically, WiFi control may need admin or Location access, " +
                             "so the current WiFi connection is used instead");
            }
            else
            {
                RaiseError($"Could not join the WiFi network {ssid}, join it from the Windows WiFi menu and press Connect again");
                return false;
            }
        }
        else if (string.IsNullOrEmpty(current) && !alreadyUp)
        {
            RaiseError("This PC is not on any WiFi network, join your phone's hotspot first");
            return false;
        }

        CurrentSsid = current ?? ssid;
        _log.Ok($"Using WiFi {CurrentSsid ?? wifiAlias}");

        var nic = wifi?.Nic ?? InterfaceDetector.FindByAlias(wifiAlias);
        var gw = nic != null ? InterfaceDetector.GetGateway(nic) : null;
        CurrentInterface = new TetherInterface(wifiAlias, nic?.Description ?? "WiFi", gw, nic);
        if (nic != null) _stats.SetInterface(nic);

        _log.Info("[3/4] Checking the internet");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Could not confirm internet yet, continuing");

        _log.Info("[4/4] Turning on carrier stealth");
        if (!ApplyBypass(CurrentInterface, usePdaProxy: false))
            return false;

        return true;
    }

    private bool ApplyBypass(TetherInterface iface, bool usePdaProxy)
    {
        int ttl = Constants.TtlPhoneTraffic;
        _systemBypassApplied = false;

        if (!_cfg.StealthMode)
        {
            // No stealth requested. Still need the proxy data-path for PdaNet USB-proxy mode.
            if (usePdaProxy) StartProxy(usePdaProxy: true, ttl: 128);
            BypassActive = false;
            _log.Info("Carrier stealth is turned off in Settings");
            return true;
        }

        if (!_cfg.UseSystemBypass)
        {
            // DEFAULT PATH — no admin, no UAC. Per-socket TTL on the local proxy's upstream sockets.
            try { StartProxy(usePdaProxy, ttl); }
            catch (Exception e) { RaiseError($"Could not start the stealth proxy on port {_cfg.LocalProxyPort}: {e.Message}"); return false; }
            BypassActive = true;
            _log.Ok($"Carrier stealth is on for apps using 127.0.0.1:{_cfg.LocalProxyPort}, TTL {ttl}");
            return true;
        }

        // OPT-IN PATH — system-wide bypass (global TTL/IPv6/firewall/hosts). Requires admin/UAC.
        if (usePdaProxy) StartProxy(usePdaProxy: true, ttl);   // data path to the phone proxy
        int level = Math.Clamp(_cfg.StealthLevel, 1, 3);
        var argLine = $"--priv apply-bypass --level {level} --ttl {ttl} --iface \"{iface.Alias}\"";
        if (!string.IsNullOrEmpty(iface.Gateway)) argLine += $" --gateway {iface.Gateway}";
        if (usePdaProxy && _cfg.SetWinHttpProxy) argLine += $" --winhttp 127.0.0.1:{_cfg.LocalProxyPort}";

        var res = ElevationHelper.RunPrivileged(argLine);
        if (res == ElevationHelper.ElevationResult.Success)
        {
            _systemBypassApplied = true;
            BypassActive = true;
            _log.Ok($"System wide carrier stealth is on, level {level}, TTL {ttl}");
            return true;
        }

        var why = res == ElevationHelper.ElevationResult.Cancelled
            ? "admin approval was declined" : "an admin step failed";

        if (_cfg.RequireBypass)
        {
            RaiseError($"System wide stealth could not turn on because {why}, and Settings require it");
            RollbackUnprivileged();
            return false;
        }

        // Gracefully fall back to the no-admin TTL proxy so the user still gets stealth without UAC.
        _log.Warning($"System wide stealth is unavailable because {why}, using the stealth proxy instead");
        if (_relay == null)
        {
            try { StartProxy(usePdaProxy, ttl); }
            catch (Exception e) { RaiseError($"Could not start the stealth proxy on port {_cfg.LocalProxyPort}: {e.Message}"); return false; }
        }
        BypassActive = true;
        return true;
    }

    private void StartProxy(bool usePdaProxy, int ttl)
    {
        StartRelay(usePdaProxy
            ? new LocalProxy(_cfg.LocalProxyPort, ttl, _cfg.ProxyIp, _cfg.ProxyPort)
            : new LocalProxy(_cfg.LocalProxyPort, ttl));
        SystemProxy.SetWinInet($"127.0.0.1:{_cfg.LocalProxyPort}");
    }

    /// <summary>Replaces the running relay, so a reconnect does not trip over the old one's port.</summary>
    private void StartRelay(LocalProxy relay)
    {
        _relay?.Stop();
        _relay = relay;
        try { _relay.Start(); }
        catch
        {
            _relay = null;
            throw;
        }
    }

    /// <summary>
    /// A plain relay for USB or WiFi without the stealth proxy, so the Apps page can see which
    /// app sends what. It leaves the TTL at the Windows default, nothing else changes.
    /// </summary>
    private void StartWatchRelay()
    {
        // A proxy you set up yourself stays: the relay would replace it, and disconnecting would clear it.
        var (on, server) = SystemProxy.GetWinInet();
        if (on && !string.IsNullOrEmpty(server) && !server.Contains($"127.0.0.1:{_cfg.LocalProxyPort}"))
        {
            _log.Info($"App watch and blocking are off for this connection, Windows already uses the proxy {server}");
            return;
        }
        try
        {
            StartRelay(new LocalProxy(_cfg.LocalProxyPort, Constants.TtlPhoneTraffic)
            {
                Label = "App watch",
                ApplyTtl = false,
            });
            SystemProxy.SetWinInet($"127.0.0.1:{_cfg.LocalProxyPort}");
        }
        catch (Exception e)
        {
            _log.Warning($"App watch could not start on port {_cfg.LocalProxyPort}, the Apps page stays empty: {e.Message}");
        }
    }

    /// <summary>
    /// Links the PC port Windows uses to the phone helper. With app watch on, a relay sits on that
    /// port and passes everything to the adb link on a private port, so the Apps page can see which
    /// app sent what. Without it, or if the relay cannot start, adb forwards the port straight to
    /// the phone as before.
    /// </summary>
    private bool LinkPhone(out string error)
    {
        _forwardPort = _cfg.LocalProxyPort;
        if (_cfg.WatchApps || TrafficRules.Instance.Any)
        {
            int linkPort = FreeLoopbackPort();
            if (linkPort > 0 && AdbAgent.Forward(linkPort, _cfg.AgentPort, out _))
            {
                try
                {
                    StartRelay(new LocalProxy(_cfg.LocalProxyPort, Constants.TtlPhoneTraffic, "127.0.0.1", linkPort)
                    {
                        Label = "App watch",
                        ApplyTtl = false,
                    });
                    _forwardPort = linkPort;
                    error = "";
                    return true;
                }
                catch (Exception e)
                {
                    _log.Warning($"App watch could not start on port {_cfg.LocalProxyPort}, linking without app watch: {e.Message}");
                    AdbAgent.RemoveForward(linkPort);
                }
            }
        }
        return AdbAgent.Forward(_cfg.LocalProxyPort, _cfg.AgentPort, out error);
    }

    private static int FreeLoopbackPort()
    {
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
        catch { return 0; }
    }

    private void RollbackUnprivileged()
    {
        // Undo the non-privileged steps if bypass failed/declined.
        try { SystemProxy.ClearWinInet(); } catch { }
        try { _relay?.Stop(); } catch { }
        _relay = null;
    }

    private void DisconnectWorker(bool userInitiated)
    {
        _busy = true;
        try
        {
            if (State == ConnectionState.Disconnected) return;
            SetState(ConnectionState.Disconnecting);
            StopMonitor();

            // Tear down a system-wide (elevated) bypass only if one was actually applied.
            if (_systemBypassApplied)
            {
                var argLine = "--priv clear-bypass";
                if (CurrentInterface != null) argLine += $" --iface \"{CurrentInterface.Alias}\"";
                if (CurrentMode == ConnectionMode.Usb && _cfg.SetWinHttpProxy) argLine += " --winhttp";
                ElevationHelper.RunPrivileged(argLine);
                _systemBypassApplied = false;
            }

            // Mode-specific teardown: stop the phone agent + remove the adb forward.
            if (CurrentMode == ConnectionMode.AdbAgent)
                AdbAgent.Stop(_forwardPort > 0 ? _forwardPort : _cfg.LocalProxyPort);

            // Non-privileged teardown (always, no admin): drop the system proxy + stop the relay.
            BypassActive = false;
            SystemProxy.ClearWinInet();
            _relay?.Stop();
            _relay = null;

            _stats.StopSession();
            CurrentInterface = null;
            CurrentSsid = null;
            SetState(ConnectionState.Disconnected);
            _log.Ok("Disconnected");
        }
        catch (Exception e)
        {
            RaiseError($"Problem while disconnecting: {e.Message}");
            SetState(ConnectionState.Disconnected);
        }
        finally { _busy = false; }
    }

    // ---- Monitoring + auto-reconnect ----
    private void StartMonitor()
    {
        StopMonitor();
        _monitor = new System.Threading.Timer(_ => MonitorTick(), null, 1000, 1000);
    }

    private void StopMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
    }

    private void MonitorTick()
    {
        if (State != ConnectionState.Connected) return;
        try
        {
            _stats.Update();
            try { Tick?.Invoke(); } catch { }

            // Interface liveness check (not applicable in ADB-agent mode, which has no local NIC).
            if (CurrentMode != ConnectionMode.AdbAgent)
            {
                var nic = CurrentInterface != null ? InterfaceDetector.FindByAlias(CurrentInterface.Alias) : null;
                bool alive = nic is { OperationalStatus: OperationalStatus.Up };
                if (!alive)
                {
                    _log.Warning("Lost the connection to your phone");
                    if (AutoReconnectEnabled) HandleDisconnectAndReconnect();
                    else Task.Run(() => DisconnectWorker(userInitiated: false));
                }
            }
        }
        catch (Exception e) { _log.Error($"Monitor problem: {e.Message}"); }
    }

    private void HandleDisconnectAndReconnect()
    {
        StopMonitor();
        int maxAttempts = Math.Clamp(_cfg.ReconnectAttempts, 1, 10);
        if (_reconnectAttempts >= maxAttempts)
        {
            RaiseError($"Gave up after {maxAttempts} reconnect tries");
            Task.Run(() => DisconnectWorker(userInitiated: false));
            return;
        }
        _reconnectAttempts++;
        int baseDelay = Math.Clamp(_cfg.ReconnectDelay, 1, 300);
        int delay = Math.Min(baseDelay * (int)Math.Pow(2, _reconnectAttempts - 1), 60); // exp backoff, cap 60s
        _log.Warning($"Reconnecting, try {_reconnectAttempts} of {maxAttempts} in {delay} seconds");

        Task.Run(async () =>
        {
            await Task.Delay(delay * 1000);
            if (State == ConnectionState.Connected) return;
            // Tear down remnants quietly, then retry with remembered params.
            try { if (BypassActive) BypassActive = false; } catch { }
            ConnectWorker(CurrentMode, _lastSsid ?? CurrentSsid,
                _lastPassword ?? (CurrentSsid != null ? SecretStore.GetPassword(CurrentSsid) : null));
        });
    }

    /// <summary>Human-readable stealth status for the GUI.</summary>
    public string StealthStatusString()
    {
        if (CurrentMode == ConnectionMode.AdbAgent) return "VIA PHONE";
        if (!BypassActive) return "DISABLED";
        if (_systemBypassApplied)
        {
            int lvl = Math.Clamp(_cfg.StealthLevel, 1, 3);
            var name = lvl switch { 1 => "BASIC", 2 => "MODERATE", _ => "AGGRESSIVE" };
            return $"L{lvl} {name}";
        }
        return $"TTL {Constants.TtlPhoneTraffic} (PROXY)";
    }
}
