using System.Net.NetworkInformation;
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
    private System.Threading.Timer? _monitor;
    private int _reconnectAttempts;
    private volatile bool _busy;
    private bool _systemBypassApplied; // true only when an elevated system-wide bypass is active

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ConnectionMode CurrentMode { get; private set; } = ConnectionMode.Wifi;
    public TetherInterface? CurrentInterface { get; private set; }
    public string? CurrentSsid { get; private set; }
    public bool BypassActive { get; private set; }
    public bool AutoReconnectEnabled { get; set; }
    public string? LastError { get; private set; }

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
                _log.Warning($"Ignoring invalid transition {State} -> {next}");
                if (next != ConnectionState.Error) return;
            }
            _log.Info($"State changed: {State} -> {next}");
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
        if (_busy) { _log.Warning("Connect ignored: operation already in progress"); return; }
        Task.Run(() => ConnectWorker(mode, ssid, password));
    }

    public void Disconnect()
    {
        if (_busy) { _log.Warning("Disconnect ignored: operation already in progress"); return; }
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

            _reconnectAttempts = 0;
            _stats.StartSession();
            SetState(ConnectionState.Connected);
            StartMonitor();
            _log.Ok($"{Constants.AppName}: {mode.Display()} connection established");
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
        _log.Info("[1/5] Detecting USB tethering interface...");
        var iface = InterfaceDetector.DetectUsbInterface();
        if (iface == null)
        {
            RaiseError("No active USB tethering interface. On the phone, turn on PdaNet+ 'Activate USB Mode' " +
                       "(or Settings > Connections > Mobile Hotspot and Tethering > USB tethering), keep mobile data on, then retry.");
            return false;
        }
        CurrentInterface = iface;
        _stats.SetInterface(iface.Nic);
        _log.Ok($"Interface: {iface.Alias} ({iface.Description})");

        _log.Info("[2/5] Checking PdaNet proxy availability...");
        bool proxyOk = InterfaceDetector.ProxyReachable(_cfg.ProxyIp, _cfg.ProxyPort, Constants.ProxyCheckTimeoutSeconds * 1000);
        if (proxyOk)
        {
            _log.Ok($"PdaNet proxy reachable at {_cfg.ProxyIp}:{_cfg.ProxyPort}");
        }
        else
        {
            // Standard Android USB tethering (RNDIS) provides direct NAT internet with no proxy.
            _log.Info("[3/5] PdaNet proxy not found; using direct USB tethering (no proxy)...");
            if (!InterfaceDetector.InternetReachable())
            {
                RaiseError($"USB interface '{iface.Alias}' is up but has no internet, and the PdaNet proxy " +
                           $"({_cfg.ProxyIp}:{_cfg.ProxyPort}) is not reachable. Turn on PdaNet+ USB mode (or USB tethering) with mobile data enabled.");
                return false;
            }
            _log.Ok($"Direct USB tethering active on {iface.Alias}");
        }

        _log.Info("[4/5] Applying carrier bypass...");
        if (!ApplyBypass(iface, usePdaProxy: proxyOk))
            return false;

        _log.Info("[5/5] Verifying connectivity...");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Connectivity check did not confirm internet (may still work)");

        CurrentSsid = null;
        return true;
    }

    private bool ConnectAdbAgent()
    {
        _log.Info("[1/4] Checking ADB and device...");
        if (!AdbAgent.Init(_cfg.AdbPath, out var err)) { RaiseError(err); return false; }
        _log.Ok($"ADB ready ({AdbAgent.AdbPath})");

        _log.Info("[2/4] Deploying + starting phone agent...");
        if (!AdbAgent.Deploy(out err)) { RaiseError(err); return false; }
        if (!AdbAgent.StartAgent(_cfg.AgentPort, out err)) { RaiseError(err); return false; }
        _log.Ok($"Agent running on phone port {_cfg.AgentPort}");

        _log.Info("[3/4] Forwarding PC port + setting system proxy...");
        if (!AdbAgent.Forward(_cfg.LocalProxyPort, _cfg.AgentPort, out err)) { RaiseError(err); return false; }
        SystemProxy.SetWinInet($"127.0.0.1:{_cfg.LocalProxyPort}");
        _log.Ok($"System proxy -> 127.0.0.1:{_cfg.LocalProxyPort} -> (adb) -> phone agent");

        _log.Info("[4/4] Verifying connectivity...");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Connectivity not confirmed yet (continuing)");

        // The agent re-originates on the phone, so the phone/VPN governs the exit (no PC-side TTL needed).
        CurrentInterface = new TetherInterface("adb-agent", "ADB app_process agent (VPN pass-through)", null, null);
        CurrentSsid = null;
        BypassActive = false;
        _stats.SetInterface(null);
        return true;
    }

    private bool ConnectWifi(ConnectionMode mode, string? ssid, string? password)
    {
        _log.Info("[1/4] Detecting WiFi interface...");
        var wifi = InterfaceDetector.DetectWifiInterface();            // .NET-based, no netsh dependency
        var wifiAlias = wifi?.Alias ?? WifiManager.GetWifiInterfaceAlias();
        if (wifiAlias == null)
        {
            RaiseError("No WiFi interface found.");
            return false;
        }
        bool alreadyUp = wifi?.Nic is { OperationalStatus: OperationalStatus.Up };
        _log.Ok($"WiFi interface: {wifiAlias}");

        _log.Info("[2/4] Connecting to hotspot...");
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
                _log.Warning($"Could not switch to '{ssid}' automatically (WiFi control may need admin/Location). " +
                             "Using the current WiFi connection instead.");
            }
            else
            {
                RaiseError($"Could not connect to WiFi '{ssid}'. Connect Windows to the hotspot from the taskbar, " +
                           "then press CONNECT with WiFi mode selected.");
                return false;
            }
        }
        else if (string.IsNullOrEmpty(current) && !alreadyUp)
        {
            RaiseError("Not connected to any WiFi network. Connect to the phone hotspot first, then choose WiFi mode.");
            return false;
        }

        CurrentSsid = current ?? ssid;
        _log.Ok($"Using WiFi: {CurrentSsid ?? wifiAlias}");

        var nic = wifi?.Nic ?? InterfaceDetector.FindByAlias(wifiAlias);
        var gw = nic != null ? InterfaceDetector.GetGateway(nic) : null;
        CurrentInterface = new TetherInterface(wifiAlias, nic?.Description ?? "WiFi", gw, nic);
        if (nic != null) _stats.SetInterface(nic);

        _log.Info("[3/4] Verifying internet connectivity...");
        if (!InterfaceDetector.InternetReachable())
            _log.Warning("Cannot confirm internet yet (continuing)");

        _log.Info("[4/4] Applying carrier bypass...");
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
            _log.Info("Stealth mode disabled; TTL not normalized");
            return true;
        }

        if (!_cfg.UseSystemBypass)
        {
            // DEFAULT PATH — no admin, no UAC. Per-socket TTL on the local proxy's upstream sockets.
            try { StartProxy(usePdaProxy, ttl); }
            catch (Exception e) { RaiseError($"Failed to start local proxy: {e.Message}"); return false; }
            BypassActive = true;
            _log.Ok($"Stealth active (no admin): outbound TTL={ttl} on traffic via 127.0.0.1:{_cfg.LocalProxyPort}");
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
            _log.Ok($"System-wide carrier bypass active (level {level}, TTL {ttl})");
            return true;
        }

        var why = res == ElevationHelper.ElevationResult.Cancelled
            ? "administrator approval was declined" : "a privileged step failed";

        if (_cfg.RequireBypass)
        {
            RaiseError($"System bypass not applied ({why}); it is marked required in settings.");
            RollbackUnprivileged();
            return false;
        }

        // Gracefully fall back to the no-admin TTL proxy so the user still gets stealth without UAC.
        _log.Warning($"System bypass unavailable ({why}); falling back to the no-admin TTL proxy.");
        if (_relay == null)
        {
            try { StartProxy(usePdaProxy, ttl); }
            catch (Exception e) { RaiseError($"Failed to start local proxy: {e.Message}"); return false; }
        }
        BypassActive = true;
        return true;
    }

    private void StartProxy(bool usePdaProxy, int ttl)
    {
        _relay = usePdaProxy
            ? new LocalProxy(_cfg.LocalProxyPort, ttl, _cfg.ProxyIp, _cfg.ProxyPort)
            : new LocalProxy(_cfg.LocalProxyPort, ttl);
        _relay.Start();
        SystemProxy.SetWinInet($"127.0.0.1:{_cfg.LocalProxyPort}");
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
                AdbAgent.Stop(_cfg.LocalProxyPort);

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
            RaiseError($"Disconnect error: {e.Message}");
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
                    _log.Warning("Tether interface lost");
                    if (AutoReconnectEnabled) HandleDisconnectAndReconnect();
                    else Task.Run(() => DisconnectWorker(userInitiated: false));
                }
            }
        }
        catch (Exception e) { _log.Error($"Monitoring error: {e.Message}"); }
    }

    private void HandleDisconnectAndReconnect()
    {
        StopMonitor();
        int maxAttempts = Math.Clamp(_cfg.ReconnectAttempts, 1, 10);
        if (_reconnectAttempts >= maxAttempts)
        {
            RaiseError($"Max reconnect attempts ({maxAttempts}) reached");
            Task.Run(() => DisconnectWorker(userInitiated: false));
            return;
        }
        _reconnectAttempts++;
        int baseDelay = Math.Clamp(_cfg.ReconnectDelay, 1, 300);
        int delay = Math.Min(baseDelay * (int)Math.Pow(2, _reconnectAttempts - 1), 60); // exp backoff, cap 60s
        _log.Warning($"Reconnect attempt {_reconnectAttempts}/{maxAttempts} in {delay}s");

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
