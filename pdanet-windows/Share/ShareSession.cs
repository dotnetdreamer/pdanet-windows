using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using PdaNetPortable.Models;
using PdaNetPortable.Services;
using Application = System.Windows.Application;
using WinScreen = System.Windows.Forms.Screen;

namespace PdaNetPortable.Share;

internal enum ShareRole { None, Host, Viewer }

/// <summary>Where a session is, in the words the Launchpad shows.</summary>
internal enum SharePhase
{
    Idle,
    Starting,     // the helper starts and reaches the relays
    Waiting,      // the PC that shares shows its code and waits
    Pairing,      // the PC that views found the other PC by the code
    Connecting,   // the code checked out, WebRTC looks for a path between the PCs
    Live,
    Reconnecting, // the path dropped, a new one is being found
}

/// <summary>How the link does, about once a second while live.</summary>
internal sealed class ShareStats
{
    public double Rtt { get; set; }
    public int SendKbps { get; set; }
    public int RecvKbps { get; set; }
    public int TargetKbps { get; set; }
    public double Loss { get; set; }
    public int Fps { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>
/// One screen sharing session, sharing this screen or viewing another, never both. It runs the
/// WebRTC helper, the screen pipeline of its side, remote control and the clipboard, and turns
/// what happens into a phase and a few words the Launchpad, the viewer window and the sharing bar
/// show. Everything public lives on the UI thread.
/// </summary>
internal sealed class ShareSession
{
    public static ShareSession Instance { get; } = new();

    private readonly AppConfig _cfg = ConfigManager.Instance.Config;
    private readonly Logger _log = Logger.Instance;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private RtcHelper? _rtc;
    private HostPipeline? _host;
    private HostInput? _input;
    private ClipboardSync? _clip;
    private DateTime _phaseSince;
    private int _lastFrames;
    private bool _ending;

    public ShareRole Role { get; private set; }
    public SharePhase Phase { get; private set; }
    public string? Code { get; private set; }
    public string? Peer { get; private set; }
    /// <summary>"lan", "internet" or "phone" once live.</summary>
    public string? Path { get; private set; }
    public bool ViaPhone { get; private set; }
    public DateTime? LiveSince { get; private set; }
    public int RelaysUp { get; private set; }
    public int RelaysTotal { get; private set; }
    public ShareStats Stats { get; } = new();
    /// <summary>A short note for the person, such as a wrong code that was tried.</summary>
    public string? Notice { get; private set; }
    public bool AllowControl { get; private set; }
    public bool Active => Role != ShareRole.None;

    // The PC that views: what the other PC shows.
    public ViewerPipeline? Viewer { get; private set; }
    public IReadOnlyList<ScreenInfo> RemoteScreens { get; private set; } = Array.Empty<ScreenInfo>();
    public int RemoteScreen { get; private set; }
    public HostFlags RemoteFlags { get; private set; }
    /// <summary>Degrees the other PC's picture comes turned clockwise, the viewer turns it back.</summary>
    public int RemoteRotation { get; private set; }
    public PointerPicture? RemotePicture { get; private set; }
    public byte RemoteStandard { get; private set; } = 1;
    private readonly Dictionary<uint, PointerPicture> _pictures = new();

    // The newest pointer position, read by the viewer window.
    public ushort PointerX { get; private set; }
    public ushort PointerY { get; private set; }
    public bool PointerVisible { get; private set; }
    private int _pointerQueued;

    /// <summary>Anything above changed. Raised on the UI thread.</summary>
    public event Action? Changed;
    /// <summary>The pointer of the other PC moved or changed shape. Raised on the UI thread.</summary>
    public event Action? PointerChanged;
    /// <summary>The session ended: the words to show, and whether something went wrong.</summary>
    public event Action<string, bool>? Ended;

    private ShareSession()
    {
        _tick.Tick += (_, _) => OnTick();
    }

    // ============================ Starting and stopping ============================

    /// <summary>Shares this screen: makes a code and waits for the other PC.</summary>
    public string? Share(bool viaPhone)
    {
        if (Active) return null;
        var (route, problem) = BuildRoute(viaPhone);
        if (problem != null) return problem;
        Begin(ShareRole.Host, viaPhone);
        AllowControl = _cfg.ShareAllowControl;
        Launch(new { op = "host", name = Environment.MachineName, route });
        return null;
    }

    /// <summary>Views the PC that shows <paramref name="code"/>.</summary>
    public string? Join(string code, bool viaPhone)
    {
        if (Active) return null;
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 10) return "A code has ten digits, like 482 619 3075";
        var (route, problem) = BuildRoute(viaPhone);
        if (problem != null) return problem;
        Begin(ShareRole.Viewer, viaPhone);
        Code = digits;
        Launch(new { op = "join", code = digits, name = Environment.MachineName, route });
        return null;
    }

    private void Begin(ShareRole role, bool viaPhone)
    {
        Role = role;
        ViaPhone = viaPhone;
        Code = Peer = Path = Notice = null;
        LiveSince = null;
        RelaysUp = RelaysTotal = 0;
        RemoteScreens = Array.Empty<ScreenInfo>();
        RemoteFlags = HostFlags.None;
        RemoteRotation = 0;
        RemotePicture = null;
        RemoteStandard = 1;
        _pictures.Clear();
        _ending = false;
        SetPhase(SharePhase.Starting);
    }

    private void Launch(object start)
    {
        try
        {
            _rtc = RtcHelper.Start(start);
        }
        catch (Exception e)
        {
            Finish($"Screen sharing could not start: {e.Message}", error: true);
            return;
        }
        var rtc = _rtc;
        rtc.Event += e => _ui.BeginInvoke(() => { if (_rtc == rtc) OnEvent(e); });
        rtc.Message += OnMessage;
        rtc.Video += f => Viewer?.Push(f.Span);
        rtc.Exited += () => _ui.BeginInvoke(() =>
        {
            if (_rtc == rtc && !_ending) Finish("Screen sharing stopped unexpectedly, try again", error: true);
        });
        _tick.Start();
        _log.Info(Role == ShareRole.Host ? "Screen sharing: making a code" : "Screen sharing: looking for the other PC");
    }

    /// <summary>Ends the session from this PC: the other PC is told.</summary>
    public void Stop()
    {
        if (!Active || _ending) return;
        _rtc?.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Bye));
        Finish(Role == ShareRole.Host ? "You stopped sharing your screen" : "You left the session", error: false);
    }

    /// <summary>Tears everything down and returns to idle. Runs on the UI thread.</summary>
    private void Finish(string text, bool error)
    {
        if (!Active || _ending) return;
        _ending = true;
        _tick.Stop();
        _input?.ReleaseAll();
        _clip?.Dispose();
        _clip = null;
        var rtc = _rtc;
        var host = _host;
        _rtc = null;
        _host = null;
        _input = null;
        // The viewer pipeline holds the pictures the window draws, so it goes on the UI thread.
        Viewer?.Dispose();
        Viewer = null;
        // The helper says goodbye to the other PC, which takes a moment, so it stops in the background.
        Task.Run(() =>
        {
            host?.Dispose();
            rtc?.Dispose();
            ReturnMemorySoon();
        });
        Role = ShareRole.None;
        Code = null;
        LiveSince = null;
        SetPhase(SharePhase.Idle);
        if (error) _log.Warning("Screen sharing: " + text);
        else _log.Info("Screen sharing: " + text);
        Ended?.Invoke(text, error);
    }

    private static int _returnPending;

    /// <summary>
    /// Gives back what a session took, a moment after it ended and its windows closed: a full
    /// collection that compacts frees the pictures the windows and pipelines held, and the
    /// process heap returns the blocks the GPU's and Media Foundation's parts freed. Without it
    /// the app would keep tens of MB until something else made it collect.
    /// </summary>
    public static void ReturnMemorySoon()
    {
        if (Interlocked.Exchange(ref _returnPending, 1) == 1) return;
        Task.Run(async () =>
        {
            await Task.Delay(2000);
            Interlocked.Exchange(ref _returnPending, 0);
            if (Instance.Active) return;
            long before = ShareNative.PrivateWorkingSet();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            HeapCompact(GetProcessHeap(), 0);
            long after = ShareNative.PrivateWorkingSet();
            var gc = GC.GetGCMemoryInfo();
            Logger.Instance.Info($"Screen sharing gave back {(before - after) >> 20} MB: the app now uses {after >> 20} MB, {gc.HeapSizeBytes >> 20} MB of it managed ({gc.TotalCommittedBytes >> 20} MB committed)");
        });
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetProcessHeap();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nuint HeapCompact(IntPtr heap, uint flags);

    // ============================ Control ============================

    /// <summary>Lets the other PC use this PC's mouse and keyboard, or takes that away at once.</summary>
    public void SetAllowControl(bool allow)
    {
        AllowControl = allow;
        _cfg.ShareAllowControl = allow;
        ConfigManager.Instance.SaveConfig();
        if (_input != null) _input.Allowed = allow;
        SendHostState();
        Changed?.Invoke();
    }

    /// <summary>Sends a remote control message from the viewer window.</summary>
    public void Send(byte[] message, bool live = false)
    {
        if (Role == ShareRole.Viewer && Phase == SharePhase.Live)
            _rtc?.Send(live ? RtcHelper.KindLive : RtcHelper.KindCtl, message);
    }

    public void PickRemoteScreen(int index) => Send(MsgWriter.Byte(Msg.PickScreen, (byte)index));

    public void SetRemoteQuality(Quality q) => Send(MsgWriter.Byte(Msg.Quality, (byte)q));

    // ============================ Helper events ============================

    private void OnEvent(JsonElement e)
    {
        string ev = Str(e, "ev");
        switch (ev)
        {
            case "code":
                Code = Str(e, "code");
                Notice = Str(e, "reason") == "tries"
                    ? "Wrong codes were tried, so PdaNet made a new code. Read the new one to the other person"
                    : null;
                SetPhase(SharePhase.Waiting);
                break;
            case "relays":
                RelaysUp = Int(e, "up");
                RelaysTotal = Int(e, "total");
                break;
            case "wrongtry":
                int left = Int(e, "left");
                Notice = $"Someone typed a wrong code. After {left} more, PdaNet makes a new code";
                break;
            case "found":
                SetPhase(SharePhase.Pairing);
                break;
            case "paired":
                Peer = Str(e, "peer") is { Length: > 0 } p ? p : "the other PC";
                Notice = null;
                SetPhase(SharePhase.Connecting);
                break;
            case "connected":
                Path = Str(e, "path");
                OnLive();
                break;
            case "reconnecting":
                if (Phase == SharePhase.Live) SetPhase(SharePhase.Reconnecting);
                break;
            case "ice":
                var state = Str(e, "reason");
                if (Phase == SharePhase.Live && state is "disconnected" or "failed") SetPhase(SharePhase.Reconnecting);
                break;
            case "bitrate":
                _host?.SetBitrate(Int(e, "bps"));
                break;
            case "keyframe":
                _host?.RequestKeyframe();
                break;
            case "stats":
                Stats.Rtt = Dbl(e, "rtt");
                Stats.SendKbps = Int(e, "send");
                Stats.RecvKbps = Int(e, "recv");
                Stats.TargetKbps = Int(e, "target");
                Stats.Loss = Dbl(e, "loss");
                break;
            case "failed":
                Finish(FailureText(Str(e, "reason"), Str(e, "detail")), error: true);
                return;
            case "ended":
                Finish(EndText(Str(e, "reason")), error: Str(e, "reason") == "lost");
                return;
        }
        Changed?.Invoke();
    }

    private void OnLive()
    {
        bool first = LiveSince == null;
        LiveSince ??= DateTime.Now;
        SetPhase(SharePhase.Live);
        if (!first) return;

        if (Role == ShareRole.Host)
        {
            _host = new HostPipeline(_rtc!);
            _input = new HostInput(_host.Screen) { Allowed = AllowControl };
            var host = _host;
            host.Changed += () => _ui.BeginInvoke(() =>
            {
                if (_host != host) return;
                if (_input != null) _input.Screen = host.Screen;
                SendScreens();
                SendHostState();
                _rtc?.Send(RtcHelper.KindCtl, new[] { Msg.Rotation, (byte)host.Rotation, (byte)(host.Rotation >> 8) });
                Changed?.Invoke();
            });
            host.Start();
            SendScreens();
            SendHostState();
            _log.Ok($"Screen sharing: {Peer} sees this screen, {PathWords(Path)}");
        }
        else
        {
            var rtc = _rtc!;
            Viewer = new ViewerPipeline(() => rtc.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Keyframe)));
            Viewer.Failed += why => _ui.BeginInvoke(() => Finish($"This PC cannot show the screen: {why}", error: true));
            var screen = WinScreen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            rtc.Send(RtcHelper.KindCtl, MsgWriter.Hello(screen.Width, screen.Height));
            _log.Ok($"Screen sharing: you see {Peer}, {PathWords(Path)}");
        }

        if (_cfg.ShareClipboard)
        {
            _clip = new ClipboardSync();
            _clip.Copied += text =>
            {
                // The PC that shares only sends its clipboard while the other PC may control it.
                if (Role == ShareRole.Host && !AllowControl) return;
                _rtc?.Send(RtcHelper.KindCtl, MsgWriter.Text(Msg.Clipboard, text));
            };
        }
    }

    // ============================ Messages from the other PC ============================

    /// <summary>Remote control and screen information. Raised on the helper's reader thread.</summary>
    private void OnMessage(byte channel, ReadOnlyMemory<byte> payload)
    {
        var m = payload.Span;
        if (m.IsEmpty) return;
        if (Role == ShareRole.Host)
        {
            switch (m[0])
            {
                case Msg.Move or Msg.Button or Msg.Wheel or Msg.Key or Msg.ReleaseAll:
                    _input?.Handle(m);
                    return;
                case Msg.Hello when m.Length >= 6:
                    _host?.SetViewerSize(MsgWriter.U16(m, 2), MsgWriter.U16(m, 4));
                    return;
                case Msg.Keyframe:
                    _host?.RequestKeyframe();
                    return;
            }
        }

        // The rest changes what the windows show, so it goes to the UI thread.
        if (Role == ShareRole.Viewer && m[0] == Msg.Pointer && m.Length >= 6)
        {
            PointerX = MsgWriter.U16(m, 1);
            PointerY = MsgWriter.U16(m, 3);
            PointerVisible = m[5] != 0;
            // Many moves in a row draw once.
            if (Interlocked.Exchange(ref _pointerQueued, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    _pointerQueued = 0;
                    PointerChanged?.Invoke();
                });
            return;
        }
        var copy = m.ToArray();
        _ui.BeginInvoke(() => OnControl(copy));
    }

    private void OnControl(byte[] m)
    {
        if (!Active || _ending) return;
        switch (m[0])
        {
            case Msg.Clipboard:
                if (Role == ShareRole.Host && !AllowControl) return;
                _clip?.Apply(Encoding.UTF8.GetString(m, 1, m.Length - 1));
                return;
            case Msg.Bye:
                Finish(Role == ShareRole.Host ? $"{Peer ?? "The other PC"} left" : $"{Peer ?? "The other PC"} stopped sharing", error: false);
                return;
        }
        if (Role == ShareRole.Host)
        {
            switch (m[0])
            {
                case Msg.PickScreen when m.Length >= 2:
                    _host?.PickScreen(m[1]);
                    break;
                case Msg.Quality when m.Length >= 2:
                    _host?.SetQuality((Quality)m[1]);
                    break;
            }
            return;
        }

        switch (m[0])
        {
            case Msg.Screens:
                RemoteScreens = MsgWriter.ReadScreens(m, out var current);
                RemoteScreen = current;
                break;
            case Msg.HostState when m.Length >= 2:
                RemoteFlags = (HostFlags)m[1];
                break;
            case Msg.Rotation when m.Length >= 3:
                RemoteRotation = MsgWriter.U16(m, 1) % 360;
                break;
            case Msg.Cursor when m.Length >= 2:
                OnCursor(m);
                PointerChanged?.Invoke();
                return;
        }
        Changed?.Invoke();
    }

    private void OnCursor(byte[] m)
    {
        switch (m[1])
        {
            case CursorShapes.Standard when m.Length >= 3:
                RemoteStandard = m[2];
                RemotePicture = null;
                break;
            case CursorShapes.Picture:
                var p = CursorShapes.ReadPicture(m);
                if (p == null) return;
                if (_pictures.Count > 64) _pictures.Clear();
                _pictures[p.Hash] = p;
                RemotePicture = p;
                RemoteStandard = 0;
                break;
            case CursorShapes.Known when m.Length >= 6:
                if (_pictures.TryGetValue(BitConverter.ToUInt32(m, 2), out var known))
                {
                    RemotePicture = known;
                    RemoteStandard = 0;
                }
                break;
        }
    }

    private void SendScreens()
    {
        if (_host == null) return;
        var list = _host.Screens.Select(s => new ScreenInfo(s.Width, s.Height, s.Primary)).ToList();
        _rtc?.Send(RtcHelper.KindCtl, MsgWriter.Screens(list, _host.ScreenIndex));
    }

    private void SendHostState()
    {
        if (Role != ShareRole.Host) return;
        var flags = (AllowControl ? HostFlags.ControlAllowed : 0) | (_host?.Hidden == true ? HostFlags.Hidden : 0);
        _rtc?.Send(RtcHelper.KindCtl, MsgWriter.Byte(Msg.HostState, (byte)flags));
    }

    // ============================ Timing ============================

    private void OnTick()
    {
        if (!Active) return;
        // A path that is not found in time will not be found: say so instead of waiting forever.
        var stuck = DateTime.Now - _phaseSince;
        if (Phase == SharePhase.Connecting && stuck > TimeSpan.FromSeconds(35))
        {
            Finish(ViaPhone
                ? "The two PCs could not reach each other through your phone. Some mobile networks do not allow direct links, try Direct"
                : "The two PCs could not reach each other. Some company and school networks block direct links, try your phone's hotspot on one PC", error: true);
            return;
        }
        if (Phase == SharePhase.Starting && stuck > TimeSpan.FromSeconds(20))
        {
            Finish("Screen sharing could not reach the internet. Check this PC's connection and try again", error: true);
            return;
        }

        if (Role == ShareRole.Host && _host != null)
        {
            int frames = _host.FramesSent;
            Stats.Fps = frames - _lastFrames;
            _lastFrames = frames;
            Stats.Detail = $"{_host.Size} · {_host.Encoder}";
        }
        else if (Viewer != null)
        {
            int frames = Viewer.FramesShown;
            Stats.Fps = frames - _lastFrames;
            _lastFrames = frames;
            Stats.Detail = Viewer.Decoder;
        }
        Changed?.Invoke();
    }

    private void SetPhase(SharePhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        _phaseSince = DateTime.Now;
        _lastFrames = Role == ShareRole.Host ? _host?.FramesSent ?? 0 : Viewer?.FramesShown ?? 0;
        Changed?.Invoke();
    }

    // ============================ Route ============================

    /// <summary>
    /// How the session reaches the other PC. Direct leaves it to WebRTC, which finds the best path,
    /// on the same network straight across it. Through the phone sends everything through the phone
    /// PdaNet is connected to: on USB tethering or the phone's hotspot from the phone's network on
    /// this PC, in Phone VPN mode through the phone helper. The relays use the proxy Windows uses.
    /// </summary>
    internal static (object Route, string? Problem) BuildRoute(bool viaPhone)
    {
        var cm = ConnectionManager.Instance;
        var cfg = ConfigManager.Instance.Config;
        string? proxy = null;
        var (on, server) = SystemProxy.GetWinInet();
        if (on && !string.IsNullOrWhiteSpace(server)) proxy = ProxyHostPort(server);

        var phoneIps = new List<string>();
        var nic = cm.IsConnected ? cm.CurrentInterface?.Nic : null;
        if (nic != null)
            try
            {
                phoneIps.AddRange(nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()));
            }
            catch { /* the adapter just went away */ }

        if (!viaPhone)
            return (new { mode = "direct", proxy, phoneIps }, null);

        if (!cm.IsConnected)
            return (new { }, "Connect PdaNet to your phone first, then share through the phone");
        if (cm.CurrentMode == ConnectionMode.AdbAgent)
        {
            int port = cm.PhoneLinkPort;
            if (port <= 0) return (new { }, "PdaNet is not linked to the phone helper, disconnect and connect again");
            return (new { mode = "phone", tunnel = $"127.0.0.1:{port}", proxy = $"127.0.0.1:{cfg.LocalProxyPort}", phoneIps }, null);
        }
        if (phoneIps.Count == 0)
            return (new { }, "PdaNet cannot find your phone's network on this PC, reconnect and try again");
        // The phone's network itself carries the relays too, no proxy in between.
        return (new { mode = "phone", bindIp = phoneIps[0], phoneIps }, null);
    }

    /// <summary>"host:port" from a Windows proxy setting, which can also be "http=host:port;https=host:port".</summary>
    private static string? ProxyHostPort(string server)
    {
        var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pick = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                   ?? parts.FirstOrDefault(p => !p.Contains('='))
                   ?? parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
        if (pick == null) return null;
        var value = pick.Contains('=') ? pick[(pick.IndexOf('=') + 1)..] : pick;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        return value.TrimEnd('/');
    }

    // ============================ Words ============================

    public static string PathWords(string? path) => path switch
    {
        "lan" => "straight across your network",
        "phone" => "through your phone",
        _ => "straight over the internet",
    };

    private string FailureText(string reason, string detail) => reason switch
    {
        "wrongcode" => "That code is not right. Check the code with the other person and try again",
        "nohost" => "No PC is sharing with that code right now. Check the code, and that the other PC still shows it",
        "noanswer" => "The other PC did not answer. Ask the other person to share again, then type the new code",
        "badcode" => "A code has ten digits, like 482 619 3075",
        "noroute" => $"PdaNet could not use your phone for screen sharing: {detail}",
        _ => string.IsNullOrEmpty(detail) ? "Screen sharing hit a problem, try again" : $"Screen sharing hit a problem: {detail}",
    };

    private string EndText(string reason) => reason switch
    {
        "peer" => Role == ShareRole.Host ? $"{Peer ?? "The other PC"} left" : $"{Peer ?? "The other PC"} stopped sharing",
        "lost" => "The connection to the other PC was lost and did not come back",
        _ => "Screen sharing ended",
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
