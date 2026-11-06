using System.Security.Cryptography;
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

    // On the PC that shares, what the target comes from: Google Congestion Control's estimate, the
    // rate the best recent burst arrived at, and the queue that does not drain.
    public int GccKbps { get; set; }
    public int CapacityKbps { get; set; }
    public int QueueMs { get; set; }
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
    private int _lastFrames, _ticks;
    private bool _ending;
    // The kinds of address each PC offered for a link, such as "host srflx", see NoPathText.
    private string? _localKinds, _remoteKinds;
    // Easy access: someone here stopped it, which holds until they share again or PdaNet starts
    // again, whatever sessions come between; the share got as far as waiting with its code;
    // PdaNet closes; and the timer that shares again after a session.
    private bool _easyHeld, _reachedWaiting, _closing;
    private readonly DispatcherTimer _easyAgain = new();

    public ShareRole Role { get; private set; }
    public SharePhase Phase { get; private set; }
    public string? Code { get; private set; }
    public string? Peer { get; private set; }
    /// <summary>"lan", "internet", "phone" or "relay" once live.</summary>
    public string? Path { get; private set; }
    /// <summary>The address family and, for a relay, how this PC reaches it, such as "ipv6" or "ipv4 relay udp".</summary>
    public string? PathDetail { get; private set; }
    public bool ViaPhone { get; private set; }
    public DateTime? LiveSince { get; private set; }
    public int RelaysUp { get; private set; }
    public int RelaysTotal { get; private set; }
    public ShareStats Stats { get; } = new();
    /// <summary>A short note for the person, such as a wrong code that was tried.</summary>
    public string? Notice { get; private set; }
    public bool AllowControl { get; private set; }
    public bool Active => Role != ShareRole.None;
    /// <summary>This PC shares with its easy access code, which stays the same.</summary>
    public bool Easy { get; private set; }
    /// <summary>Easy access waits for a PC to connect, the Launchpad keeps Connect to a screen at hand.</summary>
    public bool EasyWaiting => Easy && Role == ShareRole.Host && Phase is SharePhase.Starting or SharePhase.Waiting;

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
    /// <summary>The picture this PC asks the other PC for, kept for the next session too.</summary>
    public Quality ViewQuality { get; private set; }
    public bool ViewGray { get; private set; }

    // The PC that shares: the picture the other PC asked for, kept until the pipeline starts.
    private Quality _hostQuality;
    private bool _hostGray;

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
        _easyAgain.Tick += (_, _) =>
        {
            _easyAgain.Stop();
            StartEasy();
        };
        ConnectionManager.Instance.StateChanged += _ => _ui.BeginInvoke(() => FollowConnection());
    }

    // ============================ Starting and stopping ============================

    /// <summary>Shares this screen: makes a code, or with easy access takes its code, and waits for the other PC.</summary>
    public string? Share(bool viaPhone)
    {
        if (Active) return null;
        var (route, problem) = BuildRoute(viaPhone);
        if (problem != null) return problem;
        _easyHeld = false; // sharing again lifts a Stop
        string? code = _cfg.ShareEasyAccess ? EasyCode() ?? NewEasyCode() : null;
        Begin(ShareRole.Host, viaPhone);
        Easy = code != null;
        AllowControl = _cfg.ShareAllowControl;
        var (turn, turnKey, turnFrom) = Relays();
        // With the easy access code goes how it stands against wrong tries, so a lockout holds.
        var tries = Easy ? _cfg.ShareEasyTries : null;
        Launch(new { op = "host", code, name = Environment.MachineName, route, turn, turnKey, turnFrom, tries });
        return null;
    }

    /// <summary>Views the PC that shows <paramref name="code"/>.</summary>
    public string? Join(string code, bool viaPhone)
    {
        // Easy access pauses while this PC views another one, and comes back after.
        if (EasyWaiting) Finish("Easy access pauses while this PC views another one", error: false, quiet: true);
        if (Active) return null;
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 10) return "A code has ten digits, like 482 619 3075";
        var (route, problem) = BuildRoute(viaPhone);
        if (problem != null) return problem;
        Begin(ShareRole.Viewer, viaPhone);
        Code = digits;
        var (turn, turnKey, turnFrom) = Relays();
        Launch(new { op = "join", code = digits, name = Environment.MachineName, route, turn, turnKey, turnFrom });
        return null;
    }

    /// <summary>The relays set up in Settings as the helper takes them, none when Settings has none it can use: PdaNet comes with none.</summary>
    private (object[]? Servers, object? Key, string? From) Relays()
    {
        var s = ShareRelays.Read(_cfg);
        if (s.Any && ShareRelays.Problem(s) == null) return (ShareRelays.Servers(s), ShareRelays.Key(s), ShareRelays.From(s));
        return (null, null, null);
    }

    private void Begin(ShareRole role, bool viaPhone)
    {
        Role = role;
        ViaPhone = viaPhone;
        ViewQuality = (Quality)Math.Clamp(_cfg.ShareViewQuality, 0, 2);
        ViewGray = _cfg.ShareViewGray;
        (_hostQuality, _hostGray) = (Quality.Auto, false);
        Code = Peer = Path = PathDetail = Notice = null;
        _localKinds = _remoteKinds = null;
        Easy = _reachedWaiting = false;
        _easyAgain.Stop();
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

    /// <summary>
    /// Ends the session from this PC: the other PC is told. Easy access stopped here does not
    /// share again until the person shares again or PdaNet starts again, even after this PC
    /// views another one in between.
    /// </summary>
    public void Stop()
    {
        if (!Active || _ending) return;
        if (Role == ShareRole.Host && Easy) _easyHeld = true;
        _rtc?.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Bye));
        Finish(Role == ShareRole.Host ? "You stopped sharing your screen" : "You left the session", error: false);
    }

    /// <summary>PdaNet closes: the session ends and easy access does not share again.</summary>
    public void Close()
    {
        _closing = true;
        _easyAgain.Stop();
        Stop();
    }

    /// <summary>
    /// Tears everything down and returns to idle. Runs on the UI thread. A quiet end is one PdaNet
    /// makes itself, such as easy access following the connection: nothing is shown for it.
    /// </summary>
    private void Finish(string text, bool error, bool quiet = false)
    {
        if (!Active || _ending) return;
        _ending = true;
        // Easy access shares again once a session is over, unless someone here stopped it. After
        // viewing another PC it comes back, whoever ended that, unless it was stopped before.
        bool again = _cfg.ShareEasyAccess && !_closing && !_easyHeld;
        bool soon = _reachedWaiting || !error; // a share that never got its code waits longer
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
        // A session that went live goes first under Recent sessions. The code goes with it only
        // when the other PC shares with easy access, any other code works once.
        if (LiveSince is { } since)
            try
            {
                bool viewed = Role == ShareRole.Viewer;
                ShareHistory.Add(Peer ?? "the other PC", viewed, viewed && RemoteFlags.HasFlag(HostFlags.EasyAccess) ? Code : null,
                    since, DateTime.Now - since, Path);
            }
            catch (Exception e) { _log.Warning($"Screen sharing could not keep the session under Recent sessions: {e.Message}"); }
        Role = ShareRole.None;
        Code = null;
        LiveSince = null;
        Easy = false;
        SetPhase(SharePhase.Idle);
        if (quiet) _log.Debug("Screen sharing: " + text);
        else if (error) _log.Warning("Screen sharing: " + text);
        else _log.Info("Screen sharing: " + text);
        if (!quiet) Ended?.Invoke(text, error);
        if (again)
        {
            _easyAgain.Interval = TimeSpan.FromSeconds(soon ? 3 : 60);
            _easyAgain.Start();
        }
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

    // ============================ Easy access ============================

    /// <summary>
    /// Turns easy access on or off. On, this PC shares whenever PdaNet runs, with a code that
    /// stays the same. Off, the code is forgotten and a share that waits with it ends; a session
    /// that runs goes on.
    /// </summary>
    public void SetEasyAccess(bool on)
    {
        _cfg.ShareEasyAccess = on;
        if (!on) (_cfg.ShareEasyCode, _cfg.ShareEasyTries) = (null, null);
        ConfigManager.Instance.SaveConfig();
        if (on && EasyCode() == null) NewEasyCode();
        if (on) _easyHeld = false;
        _log.Info(on ? "Easy access is on, this PC shares whenever PdaNet runs" : "Easy access is off, its code is forgotten");
        // A share that waits follows at once: on, it waits with the easy access code; off, it
        // ends, so nobody gets in with that code any more.
        bool waits = Role == ShareRole.Host && Phase is SharePhase.Starting or SharePhase.Waiting;
        if (waits && Easy != on)
            Finish(on ? "Easy access takes over the share" : "Easy access is off", error: false, quiet: true);
        if (on) StartEasy();
        Changed?.Invoke();
    }

    /// <summary>
    /// Shares with the easy access code when easy access is on and no session runs: when PdaNet
    /// starts, and a moment after each session.
    /// </summary>
    public void StartEasy()
    {
        if (!_cfg.ShareEasyAccess || Active || _closing || _easyHeld) return;
        // Through the phone while PdaNet is connected to it, as the Launchpad picks too.
        bool viaPhone = ConnectionManager.Instance.IsConnected;
        var problem = Share(viaPhone);
        if (problem != null && viaPhone) problem = Share(false);
        if (problem != null) _log.Warning("Easy access could not share: " + problem);
    }

    /// <summary>Easy access that waits takes the new way when PdaNet connects to the phone or leaves it.</summary>
    private void FollowConnection()
    {
        if (!EasyWaiting || ViaPhone == ConnectionManager.Instance.IsConnected) return;
        Finish("Easy access follows the new connection", error: false, quiet: true);
        StartEasy();
    }

    /// <summary>The easy access code, or null while there is none.</summary>
    public string? EasyCode()
    {
        if (string.IsNullOrEmpty(_cfg.ShareEasyCode)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(_cfg.ShareEasyCode), EasyEntropy, DataProtectionScope.CurrentUser);
            var code = Encoding.ASCII.GetString(bytes);
            return code.Length == 10 && code.All(char.IsAsciiDigit) ? code : null;
        }
        catch { return null; } // another Windows user's, or damaged
    }

    /// <summary>Makes and keeps a new easy access code, for one that is missing or cannot be read.</summary>
    private string NewEasyCode()
    {
        var code = NewCode();
        _cfg.ShareEasyCode = Protect(code);
        _cfg.ShareEasyTries = null; // a new code starts with no wrong tries against it
        ConfigManager.Instance.SaveConfig();
        return code;
    }

    private static readonly byte[] EasyEntropy = Encoding.ASCII.GetBytes("pdanet easy access code");

    private static string Protect(string code) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.ASCII.GetBytes(code), EasyEntropy, DataProtectionScope.CurrentUser));

    private static string NewCode() =>
        string.Concat(Enumerable.Range(0, 10).Select(_ => (char)('0' + RandomNumberGenerator.GetInt32(10))));

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

    /// <summary>Asks the other PC for a trade of frames for sharpness, and keeps it for the next session.</summary>
    public void SetRemoteQuality(Quality q)
    {
        ViewQuality = q;
        _cfg.ShareViewQuality = (int)q;
        ConfigManager.Instance.SaveConfig();
        Send(MsgWriter.Byte(Msg.Quality, (byte)q));
    }

    /// <summary>Asks the other PC for a black and white picture, or color again, and keeps it for the next session.</summary>
    public void SetRemoteGray(bool on)
    {
        ViewGray = on;
        _cfg.ShareViewGray = on;
        ConfigManager.Instance.SaveConfig();
        Send(MsgWriter.Byte(Msg.Gray, (byte)(on ? 1 : 0)));
    }

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
                _reachedWaiting = true;
                SetPhase(SharePhase.Waiting);
                break;
            case "relays":
                RelaysUp = Int(e, "up");
                RelaysTotal = Int(e, "total");
                break;
            case "wrongtry":
                int left = Int(e, "left");
                Notice = Easy
                    ? $"Someone typed a wrong code. After {left} more, easy access takes no tries for a while"
                    : $"Someone typed a wrong code. After {left} more, PdaNet makes a new code";
                break;
            case "locked":
                var until = DateTime.Now.AddSeconds(Int(e, "wait"));
                Notice = $"Wrong codes were tried, so easy access takes no tries until {until.ToString(until.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm")}";
                _log.Warning("Screen sharing: " + Notice);
                break;
            case "tries":
                // Kept, so a lockout and its doubling outlast a restart of the helper or of PdaNet,
                // and so does each guess an answer let someone test: the helper answers nobody until
                // it hears this is saved. A helper this session let go never hears it.
                if (Easy && e.TryGetProperty("tries", out var t))
                {
                    _cfg.ShareEasyTries = new ShareTries
                    {
                        Wrong = Int(t, "wrong"),
                        Open = Int(t, "open"),
                        Lockouts = Int(t, "lockouts"),
                        Until = t.TryGetProperty("until", out var u) && u.TryGetInt64(out var s) ? s : 0,
                    };
                    if (ConfigManager.Instance.SaveConfig())
                        _rtc?.SendJson(new { op = "kept", seq = e.TryGetProperty("seq", out var q) && q.TryGetInt64(out var n) ? n : 0 });
                    else
                        Notice = "PdaNet cannot save its settings, so easy access lets nobody in until it can";
                }
                break;
            case "found":
                SetPhase(SharePhase.Pairing);
                break;
            case "paired":
                Peer = Str(e, "peer") is { Length: > 0 } p ? p : "the other PC";
                Notice = null;
                SetPhase(SharePhase.Connecting);
                break;
            case "candidates":
                _localKinds = Str(e, "local");
                _remoteKinds = Str(e, "remote");
                break;
            case "relay":
                // The relay brought no servers: the session goes on without them.
                Notice = Str(e, "reason") == "from"
                    ? $"The relay web address in Settings could not be used: {Str(e, "detail")}"
                    : $"The relay in Settings could not be used: {Str(e, "detail")}";
                _log.Warning("Screen sharing: " + Notice);
                break;
            case "connected":
                Path = Str(e, "path");
                PathDetail = Str(e, "detail") is { Length: > 0 } d ? d : null;
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
                Stats.GccKbps = Int(e, "gcc");
                Stats.CapacityKbps = Int(e, "capacity");
                Stats.QueueMs = Int(e, "queue");
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
            _host.SetQuality(_hostQuality);
            _host.SetGray(_hostGray);
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
            _log.Ok($"Screen sharing: {Peer} sees this screen, {PathWords(Path)}{DetailWords(PathDetail)}");
        }
        else
        {
            var rtc = _rtc!;
            Viewer = new ViewerPipeline(() => rtc.Send(RtcHelper.KindCtl, MsgWriter.Bare(Msg.Keyframe)));
            Viewer.Failed += why => _ui.BeginInvoke(() => Finish($"This PC cannot show the screen: {why}", error: true));
            var screen = WinScreen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            rtc.Send(RtcHelper.KindCtl, MsgWriter.Hello(screen.Width, screen.Height));
            // The picture asked for last time, the menu shows it from the start.
            rtc.Send(RtcHelper.KindCtl, MsgWriter.Byte(Msg.Quality, (byte)ViewQuality));
            rtc.Send(RtcHelper.KindCtl, MsgWriter.Byte(Msg.Gray, (byte)(ViewGray ? 1 : 0)));
            _log.Ok($"Screen sharing: you see {Peer}, {PathWords(Path)}{DetailWords(PathDetail)}");
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
                case Msg.Quality when m.Length >= 2 && m[1] <= (byte)Quality.Smooth:
                    _hostQuality = (Quality)m[1];
                    _host?.SetQuality(_hostQuality);
                    break;
                case Msg.Gray when m.Length >= 2:
                    _hostGray = m[1] != 0;
                    _host?.SetGray(_hostGray);
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
        var flags = (AllowControl ? HostFlags.ControlAllowed : 0) | (_host?.Hidden == true ? HostFlags.Hidden : 0) | (Easy ? HostFlags.EasyAccess : 0);
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
            Finish(NoPathText(), error: true);
            return;
        }
        // With a relay up, the helper says by itself when no PC answers the code.
        if (Phase == SharePhase.Starting && RelaysUp == 0 && stuck > TimeSpan.FromSeconds(20))
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
        // Every ten seconds of a live session a line in the log, so a picture that looks wrong can
        // be told apart: a slow link, a link that loses packets, or a screen that never rests.
        if (Phase == SharePhase.Live && ++_ticks % 10 == 0) LogStats();
        Changed?.Invoke();
    }

    private void LogStats()
    {
        var st = Stats;
        string path = $"{PathWords(Path)}{DetailWords(PathDetail)}";
        if (Role == ShareRole.Host && _host?.Describe(10) is { } frames)
            _log.Debug($"Screen sharing: {frames}; link {st.TargetKbps} kbit/s (estimate {st.GccKbps}, best burst {st.CapacityKbps}), "
                       + $"{st.Rtt:0} ms, {st.Loss:0.#} % lost, {st.QueueMs} ms queued, {path}");
        else if (Role == ShareRole.Viewer)
            _log.Debug($"Screen sharing: {Stats.Fps} pictures a second shown, {st.RecvKbps} kbit/s, {st.Rtt:0} ms, {st.Loss:0.#} % lost, {path}");
    }

    private void SetPhase(SharePhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        _ticks = 0;
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
        "relay" => "through a relay server",
        _ => "straight over the internet",
    };

    /// <summary>The address family and the relay's transport, for the log and tooltips: ", over IPv6", ", over IPv4, the relay by TCP".</summary>
    public static string DetailWords(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        var parts = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string text = parts[0] switch { "ipv6" => ", over IPv6", "ipv4" => ", over IPv4", _ => "" };
        if (parts.Length >= 3 && parts[1] == "relay") text += $", the relay by {parts[2].ToUpperInvariant()}";
        return text;
    }

    private string FailureText(string reason, string detail) => reason switch
    {
        "wrongcode" => "That code is not right. Check the code with the other person and try again",
        "nohost" => "No PC is sharing with that code right now. Check the code, and that the other PC still shows it",
        "noanswer" => "The other PC did not answer. Ask the other person to share again, then type the new code",
        "badcode" => "A code has ten digits, like 482 619 3075",
        "noroute" => $"PdaNet could not use your phone for screen sharing: {detail}",
        _ => string.IsNullOrEmpty(detail) ? "Screen sharing hit a problem, try again" : $"Screen sharing hit a problem: {detail}",
    };

    /// <summary>
    /// Why the two PCs found no path, as far as the kinds of address each offered tell. A PC that
    /// was done looking yet found no public address, no "srflx" from the STUN servers, no "public"
    /// one of its own and no "relay" one, most likely sits on a network that blocks UDP. That stays
    /// a likely reason, not a sure one: ICE says what it found, not why. Nor does a public address
    /// tell that the internet gets through to it, a firewall may keep it out. So the words say what
    /// was seen and what may help, not what the network does.
    /// </summary>
    private string NoPathText()
    {
        bool known = _localKinds != null && _remoteKinds != null;
        bool hereRelay = _localKinds?.Contains("relay") == true, thereRelay = _remoteKinds?.Contains("relay") == true;
        bool hereShut = Unreached(_localKinds), thereShut = Unreached(_remoteKinds);
        var who = Peer is null or "the other PC" ? "The other PC" : Peer;
        const string SetUp = "set up a relay in Settings, Screen sharing";
        const string Test = "Press Test in Settings, Screen sharing";
        // A relay is set up here, but this PC got no address on it.
        bool relayDown = !hereRelay && ShareRelays.Configured(_cfg);
        if (ViaPhone)
            return hereShut
                ? "Through your phone this PC found no public address, the mobile network may not allow direct links, try Direct"
                : "The two PCs could not reach each other through your phone. Some mobile networks do not allow direct links, try Direct";
        if (!known)
            return relayDown
                ? $"The two PCs could not reach each other, and the relay set up on this PC did not answer. {Test}"
                : $"The two PCs could not reach each other. Some company and school networks block direct links, on such a PC {SetUp}";
        if (hereShut && relayDown) return $"This PC found no public address, and the relay set up on this PC did not answer. {Test}";
        if (hereShut && thereShut) return $"Neither PC found a public address, both networks may block direct links. On both PCs, {SetUp}";
        if (thereShut) return $"{who} found no public address, its network may block direct links. On that PC, {SetUp}";
        if (hereShut) return $"This PC found no public address, its network may block direct links. On this PC, {SetUp}";
        if (hereRelay || thereRelay) return $"The two PCs could not link up, not even through the relay. {Test}";
        return $"The two PCs could not link up directly, some networks do not allow it. On one of them, {SetUp}";
    }

    /// <summary>A side done looking for addresses that found no public one.</summary>
    private static bool Unreached(string? kinds) =>
        kinds != null && !kinds.Contains("partial") && !kinds.Contains("srflx") && !kinds.Contains("public") && !kinds.Contains("relay");

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
