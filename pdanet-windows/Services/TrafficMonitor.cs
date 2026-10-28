using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Interop;

namespace PdaNetPortable.Services;

/// <summary>Where a connection stands, used for colours and the problems filter.</summary>
public enum FlowOutcome { Open, Done, Problem, Failed, Blocked }

/// <summary>A site one app talked to, with the bytes moved both ways.</summary>
public sealed class HostTraffic
{
    private long _bytes;

    internal HostTraffic(string name) => Name = name;

    public string Name { get; }
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Connections { get; internal set; } // changed under the monitor lock

    internal void Add(long n) => Interlocked.Add(ref _bytes, n);
}

/// <summary>Totals for one site across the apps being shown.</summary>
public sealed record SiteTotal(string Host, long Bytes, int Connections);

/// <summary>Everything the Apps page needs for one refresh.</summary>
public sealed record TrafficSnapshot(
    IReadOnlyList<AppTraffic> Apps,
    IReadOnlyList<TrafficFlow> NewFlows,
    long OldestId,
    long BytesUp,
    long BytesDown,
    double RateUp,
    double RateDown,
    int Sites);

/// <summary>Who owns a connection: the program file, its friendly name and publisher.</summary>
internal sealed record AppIdentity(string Key, string Name, string Exe, string? Path, string? Publisher, bool IsSelf)
{
    public static readonly AppIdentity Unknown = new("?", "Unknown app", "", null, null, false);

    // Windows programs whose own description is not helpful to most people.
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["svchost.exe"] = "Windows services",
        ["backgroundTaskHost.exe"] = "Windows background tasks",
        ["taskhostw.exe"] = "Windows scheduled tasks",
        ["RuntimeBroker.exe"] = "Windows app helper",
        ["SearchHost.exe"] = "Windows Search",
        ["WidgetService.exe"] = "Windows Widgets",
    };

    public static AppIdentity Of(int pid)
    {
        if (pid <= 0) return Unknown;
        if (pid == Environment.ProcessId) return Self();

        string? path = null;
        try { path = NativeMethods.ProcessImagePath(pid); } catch { }

        string exe;
        if (path != null) exe = System.IO.Path.GetFileName(path);
        else
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                exe = p.ProcessName + ".exe";
            }
            catch { return Unknown; } // the app already closed
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(exe);
        string? publisher = null;
        if (path != null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = FirstText(info.FileDescription, info.ProductName) ?? name;
                publisher = FirstText(info.CompanyName);
            }
            catch { }
        }
        if (KnownNames.TryGetValue(exe, out var known)) name = known;
        return new((path ?? exe).ToLowerInvariant(), Friendly.Clean(name), exe, path, publisher, false);
    }

    public static AppIdentity Self() => new("self", "PdaNet Windows",
        System.IO.Path.GetFileName(Environment.ProcessPath) ?? "PdaNetPortable.exe", Environment.ProcessPath, null, true);

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>An app on this PC that went online through PdaNet, with live totals.</summary>
public sealed class AppTraffic
{
    private long _up, _down, _lastUp, _lastDown, _lastSeenTicks;
    private int _connections, _open, _problems, _blocked;
    private readonly SortedSet<int> _pids = new();

    // Changed only under the monitor lock.
    internal readonly Dictionary<string, HostTraffic> Hosts = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<double> HistoryUp = new();
    internal readonly List<double> HistoryDown = new();

    internal AppTraffic(AppIdentity who, DateTime now)
    {
        Key = who.Key;
        Name = who.Name;
        Exe = who.Exe;
        Path = who.Path;
        Publisher = who.Publisher;
        IsSelf = who.IsSelf;
        FirstSeen = now;
        _lastSeenTicks = now.Ticks;
    }

    public string Key { get; }
    public string Name { get; }
    public string Exe { get; }
    public string? Path { get; }
    public string? Publisher { get; }
    public bool IsSelf { get; }
    public DateTime FirstSeen { get; }
    public DateTime LastSeen => new(Interlocked.Read(ref _lastSeenTicks));
    public long BytesUp => Interlocked.Read(ref _up);
    public long BytesDown => Interlocked.Read(ref _down);
    public long BytesTotal => BytesUp + BytesDown;
    public int Connections => Volatile.Read(ref _connections);
    public int OpenConnections => Volatile.Read(ref _open);
    public int Problems => Volatile.Read(ref _problems);
    public int BlockedCount => Volatile.Read(ref _blocked);
    public int Sites { get; private set; }
    public double RateUp { get; private set; }
    public double RateDown { get; private set; }

    /// <summary>Process ids seen for this app, like "4120, 9876".</summary>
    public string Pids { get; private set; } = "";

    internal HostTraffic Begin(int pid, string host, DateTime now)
    {
        Interlocked.Increment(ref _connections);
        Interlocked.Increment(ref _open);
        Interlocked.Exchange(ref _lastSeenTicks, now.Ticks);
        if (pid > 0 && _pids.Add(pid))
            Pids = string.Join(", ", _pids.Take(4)) + (_pids.Count > 4 ? $" and {_pids.Count - 4} more" : "");
        if (!Hosts.TryGetValue(host, out var h))
        {
            h = new HostTraffic(host);
            Hosts[host] = h;
            Sites = Hosts.Count;
        }
        h.Connections++;
        return h;
    }

    internal void Finish(FlowOutcome outcome)
    {
        Interlocked.Decrement(ref _open);
        if (outcome is FlowOutcome.Failed or FlowOutcome.Problem) Interlocked.Increment(ref _problems);
        else if (outcome == FlowOutcome.Blocked) Interlocked.Increment(ref _blocked);
    }

    internal void AddUp(long n) => Interlocked.Add(ref _up, n);
    internal void AddDown(long n) => Interlocked.Add(ref _down, n);

    internal void Sample(double seconds)
    {
        long up = BytesUp, down = BytesDown;
        RateUp = Math.Max(0, (up - _lastUp) / seconds);
        RateDown = Math.Max(0, (down - _lastDown) / seconds);
        _lastUp = up;
        _lastDown = down;
        TrafficMonitor.Push(HistoryUp, RateUp);
        TrafficMonitor.Push(HistoryDown, RateDown);
    }

    /// <summary>Starts the next rate sample from the current totals (used after seeding sample data).</summary>
    internal void Rebase()
    {
        _lastUp = BytesUp;
        _lastDown = BytesDown;
    }
}

/// <summary>
/// One connection an app made through the local relay. For secure (HTTPS) connections the
/// relay only ever sees the site name, the port, the timing and the size, because the content
/// is encrypted between the app and the site. For plain HTTP it also sees the request and
/// response heads, which are kept with passwords and cookies hidden.
/// </summary>
public sealed class TrafficFlow
{
    private readonly HostTraffic _host;
    private long _up, _down, _endedTicks;
    private int _finished;
    private volatile string? _responseHead, _error, _blocked;
    private volatile int _status;

    internal TrafficFlow(long id, AppTraffic app, HostTraffic host, int pid, bool secure, string method, int port,
        string path, string requestHead, DateTime started)
    {
        Id = id;
        App = app;
        _host = host;
        Pid = pid;
        Secure = secure;
        Method = method.ToUpperInvariant();
        Port = port;
        Path = path;
        RequestHead = requestHead;
        Started = started;
    }

    public long Id { get; }
    public AppTraffic App { get; }
    public int Pid { get; }
    public bool Secure { get; }
    public string Method { get; }
    public string Host => _host.Name;
    public int Port { get; }
    public string Path { get; }
    public string RequestHead { get; }
    public string? ResponseHead => _responseHead;
    public int Status => _status;
    public string? Error => _error;

    /// <summary>Why PdaNet refused or cut this connection, when one of your rules did.</summary>
    public string? BlockedBy => _blocked;

    public DateTime Started { get; }
    public bool IsOpen => Interlocked.Read(ref _endedTicks) == 0;
    public long BytesUp => Interlocked.Read(ref _up);
    public long BytesDown => Interlocked.Read(ref _down);
    public string? ContentType => HttpHead.Header(_responseHead, "Content-Type");
    public string Reason => HttpHead.ReasonPhrase(_responseHead);

    public DateTime? Ended
    {
        get
        {
            long t = Interlocked.Read(ref _endedTicks);
            return t == 0 ? null : new DateTime(t);
        }
    }

    public TimeSpan Duration => (Ended ?? DateTime.Now) - Started;

    public FlowOutcome Outcome =>
        _blocked != null ? FlowOutcome.Blocked
        : _error != null ? FlowOutcome.Failed
        : _status >= 400 ? FlowOutcome.Problem
        : IsOpen ? FlowOutcome.Open : FlowOutcome.Done;

    public bool IsProblem => Outcome is FlowOutcome.Failed or FlowOutcome.Problem;

    /// <summary>The address as a browser would show it.</summary>
    public string Url
    {
        get
        {
            bool defaultPort = Secure ? Port == 443 : Port == 80;
            var port = defaultPort ? "" : $":{Port}";
            return Secure ? $"https://{Host}{port}" : $"http://{Host}{port}{Path}";
        }
    }

    internal void AddUp(long n)
    {
        Interlocked.Add(ref _up, n);
        App.AddUp(n);
        _host.Add(n);
        TrafficMonitor.Instance.CountTotal(n, 0);
    }

    internal void AddDown(long n)
    {
        Interlocked.Add(ref _down, n);
        App.AddDown(n);
        _host.Add(n);
        TrafficMonitor.Instance.CountTotal(0, n);
    }

    internal void Respond(string head)
    {
        _status = HttpHead.StatusCode(head);
        _responseHead = HttpHead.Redact(head);
    }

    internal void Fail(string reason) => _error ??= reason;

    /// <summary>Set by the relay: closes both sides of this connection.</summary>
    internal Action? Abort { get; set; }

    internal void Block(string reason) => _blocked ??= reason;

    /// <summary>Closes the connection because a new rule covers it. False when it already ended.</summary>
    internal bool Cut(string reason)
    {
        var abort = Abort;
        if (!IsOpen || abort == null) return false;
        _blocked ??= reason;
        try { abort(); } catch { /* already closing */ }
        return true;
    }

    internal void Finish(DateTime when)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        Abort = null;
        // Closed before anything came back: the site or the phone never answered. Set before the
        // line reads as closed, so the Apps page never sees it closed without its final result.
        if (_blocked == null && _error == null && _status == 0 && BytesDown == 0) _error = "No answer from the website";
        Interlocked.Exchange(ref _endedTicks, when.Ticks);
        App.Finish(Outcome);
    }
}

/// <summary>
/// Records which apps on this PC go online through the local relay and what they send, for the
/// Apps page. The owning app of each connection comes from the Windows TCP table, which needs no
/// admin rights. Everything stays in memory on this PC: nothing is written to disk unless you
/// save it yourself, and nothing is sent anywhere.
/// </summary>
public sealed class TrafficMonitor
{
    private static readonly Lazy<TrafficMonitor> _instance = new(() => new TrafficMonitor());
    public static TrafficMonitor Instance => _instance.Value;

    public const int MaxFlows = 4000;
    public const int HistoryPoints = 60;

    private readonly object _lock = new();
    private readonly List<TrafficFlow> _flows = new();
    private readonly HashSet<TrafficFlow> _open = new(); // still open, even once trimmed from the list
    private readonly Dictionary<string, AppTraffic> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _historyUp = new(), _historyDown = new();
    private long _nextId, _up, _down, _lastUp, _lastDown, _lastSample;
    private double _rateUp, _rateDown;
    private System.Threading.Timer? _sampler, _demoTicker;

    // Owner lookups: one TCP table read serves a burst of new connections.
    private readonly object _ownerLock = new();
    private Dictionary<int, int> _owners = new();
    private int _ownersPort;
    private long _ownersAt;
    private readonly Dictionary<int, (AppIdentity Who, long Until)> _identities = new();

    private TrafficMonitor() { }

    /// <summary>Follows the Watch app traffic setting.</summary>
    public bool Enabled => ConfigManager.Instance.Config.WatchApps;

    public long BytesUp => Interlocked.Read(ref _up);
    public long BytesDown => Interlocked.Read(ref _down);

    /// <summary>
    /// Which app is on the other end of a connection into the relay. Call after the request head
    /// has been read, so the lookups never hold up new connections.
    /// </summary>
    internal (int Pid, AppIdentity Who) Identify(EndPoint? client, int relayPort)
    {
        int pid = client is IPEndPoint ep ? FindOwner(ep.Port, relayPort) : 0;
        return (pid, Identify(pid));
    }

    /// <summary>Starts recording a connection that reached the relay. Returns null when watching is off.</summary>
    internal TrafficFlow? Begin(int pid, AppIdentity who, bool secure, string method, string host, int port,
        string path, string head)
    {
        if (!Enabled) return null;
        var now = DateTime.Now;
        lock (_lock)
        {
            var app = AppFor(who, now);
            var h = app.Begin(pid, host, now);
            var flow = new TrafficFlow(++_nextId, app, h, pid, secure, method, port, path, HttpHead.Redact(head), now);
            _flows.Add(flow);
            _open.Add(flow);
            if (_flows.Count > MaxFlows + 250) _flows.RemoveRange(0, _flows.Count - MaxFlows);
            StartSampler();
            return flow;
        }
    }

    public void End(TrafficFlow? flow)
    {
        if (flow == null) return;
        flow.Finish(DateTime.Now);
        lock (_lock) _open.Remove(flow);
    }

    /// <summary>Closes the open connections a new rule covers. Returns how many were cut.</summary>
    public int Cut(BlockRule rule)
    {
        List<TrafficFlow> hit;
        lock (_lock) hit = _open.Where(f => rule.Matches(f.App.Exe, f.Host)).ToList();
        int cut = 0;
        foreach (var f in hit)
            if (f.Cut($"Stopped when you blocked {rule.Label}")) cut++;
        return cut;
    }

    /// <summary>All apps, plus the connections that started after <paramref name="afterId"/>.</summary>
    public TrafficSnapshot Snapshot(long afterId)
    {
        lock (_lock)
        {
            int lo = 0, hi = _flows.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_flows[mid].Id <= afterId) lo = mid + 1;
                else hi = mid;
            }
            return new TrafficSnapshot(
                _apps.Values.ToList(),
                _flows.GetRange(lo, _flows.Count - lo),
                _flows.Count > 0 ? _flows[0].Id : _nextId + 1,
                BytesUp, BytesDown, _rateUp, _rateDown,
                _apps.Values.SelectMany(a => a.Hosts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    /// <summary>Bytes per second over the last minute, oldest first. Null app means all apps.</summary>
    public double[] History(AppTraffic? app, bool down)
    {
        lock (_lock)
        {
            if (app == null) return (down ? _historyDown : _historyUp).ToArray();
            return (down ? app.HistoryDown : app.HistoryUp).ToArray();
        }
    }

    /// <summary>The sites that moved the most data. Null app means all apps.</summary>
    public List<SiteTotal> TopSites(AppTraffic? app, int count)
    {
        lock (_lock)
        {
            var hosts = app != null ? app.Hosts.Values : _apps.Values.SelectMany(a => a.Hosts.Values);
            return hosts.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SiteTotal(g.Key, g.Sum(h => h.Bytes), g.Sum(h => h.Connections)))
                .OrderByDescending(s => s.Bytes).ThenByDescending(s => s.Connections)
                .Take(count).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _flows.Clear(); // _open stays: those connections are still live and a new rule must reach them
            _apps.Clear();
            _historyUp.Clear();
            _historyDown.Clear();
            Interlocked.Exchange(ref _up, 0);
            Interlocked.Exchange(ref _down, 0);
            _lastUp = _lastDown = 0;
            _rateUp = _rateDown = 0;
        }
    }

    internal void CountTotal(long up, long down)
    {
        if (up != 0) Interlocked.Add(ref _up, up);
        if (down != 0) Interlocked.Add(ref _down, down);
    }

    internal static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryPoints) list.RemoveAt(0);
    }

    private AppTraffic AppFor(AppIdentity who, DateTime now)
    {
        if (!_apps.TryGetValue(who.Key, out var app))
        {
            app = new AppTraffic(who, now);
            _apps[who.Key] = app;
        }
        return app;
    }

    private int FindOwner(int clientPort, int relayPort)
    {
        lock (_ownerLock)
        {
            long now = Environment.TickCount64;
            if (_ownersPort != relayPort || now - _ownersAt > 250 || !_owners.ContainsKey(clientPort))
            {
                try { _owners = NativeMethods.TcpOwnersByLocalPort(relayPort); }
                catch { _owners = new(); }
                _ownersPort = relayPort;
                _ownersAt = now;
            }
            return _owners.TryGetValue(clientPort, out var pid) ? pid : 0;
        }
    }

    // Names are cached briefly per process id, Windows reuses an id only after a while.
    private AppIdentity Identify(int pid)
    {
        long now = Environment.TickCount64;
        lock (_identities)
            if (_identities.TryGetValue(pid, out var hit) && hit.Until > now) return hit.Who;
        var who = AppIdentity.Of(pid);
        lock (_identities) _identities[pid] = (who, now + 30_000);
        return who;
    }

    private void StartSampler()
    {
        if (_sampler != null) return;
        _lastSample = Stopwatch.GetTimestamp();
        _sampler = new System.Threading.Timer(_ => Sample(), null, 1000, 1000);
    }

    private void Sample()
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = Math.Max(0.2, (now - _lastSample) / (double)Stopwatch.Frequency);
            _lastSample = now;
            foreach (var app in _apps.Values) app.Sample(seconds);
            long up = BytesUp, down = BytesDown;
            _rateUp = Math.Max(0, (up - _lastUp) / seconds);
            _rateDown = Math.Max(0, (down - _lastDown) / seconds);
            _lastUp = up;
            _lastDown = down;
            Push(_historyUp, _rateUp);
            Push(_historyDown, _rateDown);
        }
    }

    // ======================= Screenshot only demo =======================
    /// <summary>Fills the Apps page with sample apps and keeps a few of them busy, for screenshots.</summary>
    public void SeedDemo()
    {
        static string Under(Environment.SpecialFolder root, string rel) =>
            System.IO.Path.Combine(Environment.GetFolderPath(root), rel);
        static AppIdentity Who(string exe, string name, string? path, string publisher) =>
            new((path ?? exe).ToLowerInvariant(), name, exe, path, publisher, false);

        var edge = Who("msedge.exe", "Microsoft Edge",
            Under(Environment.SpecialFolder.ProgramFilesX86, @"Microsoft\Edge\Application\msedge.exe"), "Microsoft Corporation");
        var spotify = Who("Spotify.exe", "Spotify", Under(Environment.SpecialFolder.ApplicationData, @"Spotify\Spotify.exe"), "Spotify Ltd");
        var teams = Who("ms-teams.exe", "Microsoft Teams", null, "Microsoft Corporation");
        var onedrive = Who("OneDrive.exe", "Microsoft OneDrive",
            Under(Environment.SpecialFolder.LocalApplicationData, @"Microsoft\OneDrive\OneDrive.exe"), "Microsoft Corporation");
        var code = Who("Code.exe", "Visual Studio Code",
            Under(Environment.SpecialFolder.LocalApplicationData, @"Programs\Microsoft VS Code\Code.exe"), "Microsoft Corporation");
        var svc = Who("svchost.exe", "Windows services", Under(Environment.SpecialFolder.System, "svchost.exe"), "Microsoft Corporation");
        var curl = Who("curl.exe", "curl", Under(Environment.SpecialFolder.System, "curl.exe"), "curl");
        var self = AppIdentity.Self();

        const long K = 1024, M = 1024 * 1024;
        // App, pid, secure, method, host, port, path, sent, received, seconds ago, seconds taken (or -1 while open),
        // status, content type, error
        var rows = new (AppIdentity App, int Pid, bool Secure, string Method, string Host, int Port, string Path, long Up,
            long Down, int Ago, double Took, int Status, string? Type, string? Error)[]
        {
            (svc, 1948, false, "GET", "ctldl.windowsupdate.com", 80, "/msdownload/update/v3/static/trustedr/en/authrootstl.cab",
                412, 18 * K + 210, 1210, 0.41, 200, "application/vnd.ms-cab-compressed", null),
            (svc, 1948, true, "CONNECT", "settings-win.data.microsoft.com", 443, "", 6 * K, 14 * K, 1195, 1.2, 200, null, null),
            (edge, 9120, true, "CONNECT", "www.bing.com", 443, "", 38 * K, 1 * M + 90 * K, 1170, 6.8, 200, null, null),
            (edge, 9120, true, "CONNECT", "ntp.msn.com", 443, "", 61 * K, 3 * M + 410 * K, 1166, 12.4, 200, null, null),
            (spotify, 7712, true, "CONNECT", "apresolve.spotify.com", 443, "", 2 * K, 4 * K + 90, 1120, 0.38, 200, null, null),
            (spotify, 7712, true, "CONNECT", "spclient.wg.spotify.com", 443, "", 210 * K, 3 * M + 120 * K, 1118, -1, 200, null, null),
            (spotify, 7712, true, "CONNECT", "audio-ak-spotify-com.akamaized.net", 443, "", 96 * K, 84 * M, 1100, -1, 200, null, null),
            (teams, 11024, true, "CONNECT", "teams.microsoft.com", 443, "", 940 * K, 12 * M + 300 * K, 1040, 18.0, 200, null, null),
            (teams, 11024, true, "CONNECT", "statics.teams.cdn.office.net", 443, "", 120 * K, 27 * M + 640 * K, 1036, 9.6, 200, null, null),
            (teams, 11024, true, "CONNECT", "pub-ent-euno-08-t.trouter.teams.microsoft.com", 443, "", 88 * K, 412 * K, 1030, -1, 200, null, null),
            (code, 15520, true, "CONNECT", "marketplace.visualstudio.com", 443, "", 160 * K, 8 * M + 600 * K, 960, 21.5, 200, null, null),
            (code, 15520, true, "CONNECT", "update.code.visualstudio.com", 443, "", 9 * K, 210 * K, 955, 0.9, 200, null, null),
            (svc, 1948, true, "CONNECT", "fe3cr.delivery.mp.microsoft.com", 443, "", 0, 0, 900, 15.0, 0, null, "The website did not answer in time"),
            (onedrive, 6604, true, "CONNECT", "api.onedrive.com", 443, "", 48 * K, 310 * K, 870, 2.2, 200, null, null),
            (onedrive, 6604, true, "CONNECT", "my.microsoftpersonalcontent.com", 443, "", 64 * M, 1 * M + 200 * K, 860, 110.0, 200, null, null),
            (edge, 9120, true, "CONNECT", "github.com", 443, "", 220 * K, 6 * M + 210 * K, 700, 41.0, 200, null, null),
            (edge, 9120, true, "CONNECT", "avatars.githubusercontent.com", 443, "", 31 * K, 2 * M + 400 * K, 698, 3.3, 200, null, null),
            (edge, 9120, false, "GET", "neverssl.com", 80, "/", 702, 4 * K + 120, 640, 0.31, 200, "text/html", null),
            (edge, 9120, false, "GET", "neverssl.com", 80, "/favicon.ico", 640, 512, 639, 0.12, 404, "text/html", null),
            (curl, 13380, false, "POST", "httpbin.org", 80, "/post", 1 * K + 30, 2 * K + 410, 520, 0.84, 200, "application/json", null),
            (curl, 13380, false, "GET", "httpbin.org", 80, "/status/503", 210, 180, 510, 0.66, 503, "text/html", null),
            (curl, 13380, false, "GET", "intranet.local", 80, "/", 0, 0, 505, 0.02, 0, null, "Could not find this website"),
            (self, Environment.ProcessId, true, "CONNECT", "api.ipify.org", 443, "", 3 * K, 6 * K, 420, 0.5, 200, null, null),
            (self, Environment.ProcessId, true, "CONNECT", "speed.cloudflare.com", 443, "", 160 * K, 23 * M + 860 * K, 410, 7.9, 200, null, null),
            (edge, 9120, true, "CONNECT", "www.youtube.com", 443, "", 410 * K, 18 * M + 300 * K, 300, -1, 200, null, null),
            (edge, 9120, true, "CONNECT", "rr4---sn-5hne6nzk.googlevideo.com", 443, "", 3 * M, 402 * M, 296, -1, 200, null, null),
            (edge, 9120, true, "CONNECT", "fonts.gstatic.com", 443, "", 12 * K, 640 * K, 120, 1.4, 200, null, null),
            (code, 15520, true, "CONNECT", "vscode-sync.trafficmanager.net", 443, "", 22 * K, 96 * K, 60, 1.1, 200, null, null),
        };

        // Live rates (received, sent per second) for the connections that stay open.
        var live = new Dictionary<string, (double Down, double Up)>
        {
            ["rr4---sn-5hne6nzk.googlevideo.com"] = (3.2 * M, 60 * K),
            ["audio-ak-spotify-com.akamaized.net"] = (40 * K, 2 * K),
            ["pub-ent-euno-08-t.trouter.teams.microsoft.com"] = (3 * K, 1 * K),
            ["www.youtube.com"] = (60 * K, 8 * K),
            ["spclient.wg.spotify.com"] = (4 * K, 1 * K),
        };

        // Two rules, so the page shows what blocking looks like: OneDrive was blocked part way
        // through a big upload, and Windows telemetry is blocked for every app. Kept in memory only.
        var oneDriveRule = new BlockRule
        {
            Kind = BlockKind.App, Exe = onedrive.Exe, AppName = onedrive.Name, AppPath = onedrive.Path,
            Created = DateTime.Now.AddSeconds(-750), _hits = 4,
        };
        var telemetryRule = new BlockRule
        {
            Kind = BlockKind.Site, Site = "events.data.microsoft.com", Subdomains = true,
            Created = DateTime.Now.AddDays(-2), _hits = 2,
        };
        TrafficRules.Instance.UseInMemory(new[] { oneDriveRule, telemetryRule });
        var refused = new (AppIdentity App, int Pid, string Host, int Ago, BlockRule Rule)[]
        {
            (svc, 1948, "v10.events.data.microsoft.com", 1150, telemetryRule),
            (onedrive, 6604, "api.onedrive.com", 700, oneDriveRule),
            (onedrive, 6604, "my.microsoftpersonalcontent.com", 610, oneDriveRule),
            (svc, 1948, "v20.events.data.microsoft.com", 330, telemetryRule),
            (onedrive, 6604, "api.onedrive.com", 240, oneDriveRule),
            (onedrive, 6604, "my.microsoftpersonalcontent.com", 90, oneDriveRule),
        };
        // Everything in time order, so ids grow with time like they do for real connections.
        var events = rows.Select(r => (Row: r, Rule: r.Host == "my.microsoftpersonalcontent.com" ? oneDriveRule : null))
            .Concat(refused.Select(b => (Row: (App: b.App, Pid: b.Pid, Secure: true, Method: "CONNECT", Host: b.Host,
                Port: 443, Path: "", Up: 0L, Down: 0L, Ago: b.Ago, Took: 0.004, Status: 0, Type: (string?)null,
                Error: (string?)null), Rule: (BlockRule?)b.Rule)))
            .OrderByDescending(x => x.Row.Ago)
            .ToList();

        var now = DateTime.Now;
        var open = new List<(TrafficFlow Flow, double Down, double Up)>();
        lock (_lock)
        {
            foreach (var (r, rule) in events)
            {
                var started = now.AddSeconds(-r.Ago);
                var app = AppFor(r.App, started);
                var host = app.Begin(r.Pid, r.Host, started);
                var head = r.Secure ? DemoConnect(r.Host, r.Port) : DemoRequest(r.Method, r.Host, r.Path);
                var flow = new TrafficFlow(++_nextId, app, host, r.Pid, r.Secure, r.Method, r.Port, r.Path,
                    HttpHead.Redact(head), started);
                if (r.Up > 0) flow.AddUp(r.Up);
                if (r.Down > 0) flow.AddDown(r.Down);
                if (r.Error != null) flow.Fail(r.Error);
                else if (r.Status > 0)
                    flow.Respond(r.Secure ? "HTTP/1.1 200 Connection established" : DemoResponse(r.Status, r.Type, r.Down));
                if (rule != null) flow.Block(r.Down > 0 ? $"Stopped when you blocked {rule.Label}" : rule.Reason);
                if (r.Took >= 0) flow.Finish(started.AddSeconds(r.Took));
                else if (live.TryGetValue(r.Host, out var rate)) open.Add((flow, rate.Down, rate.Up));
                _flows.Add(flow);
            }

            // A minute of history so the charts show an established trace straight away.
            for (int i = 0; i < HistoryPoints; i++)
            {
                double wave = (Math.Sin(i / 4.0) + 1) / 2;
                double totalDown = 0, totalUp = 0;
                foreach (var app in _apps.Values)
                {
                    var (down, up) = DemoRate(app, open, wave, i);
                    Push(app.HistoryDown, down);
                    Push(app.HistoryUp, up);
                    totalDown += down;
                    totalUp += up;
                }
                Push(_historyDown, totalDown);
                Push(_historyUp, totalUp);
            }
            foreach (var app in _apps.Values) app.Rebase();
            _lastUp = BytesUp;
            _lastDown = BytesDown;
            StartSampler();
        }

        // Keep the open connections moving so the live view looks alive. Half a second out of step
        // with the sampler, so every sample sees exactly one tick.
        int tick = HistoryPoints;
        _demoTicker ??= new System.Threading.Timer(_ =>
        {
            tick++;
            double wave = (Math.Sin(tick / 4.0) + 1) / 2;
            foreach (var (flow, down, up) in open)
            {
                flow.AddDown((long)(down * (0.55 + wave * 0.9)));
                flow.AddUp((long)(up * (0.6 + wave * 0.8)));
            }
        }, null, 500, 1000);
    }

    private static (double Down, double Up) DemoRate(AppTraffic app, List<(TrafficFlow Flow, double Down, double Up)> open,
        double wave, int i)
    {
        if (TrafficRules.Instance.AppRule(app.Exe) != null) return (0, 0); // blocked apps stay quiet
        double down = 0, up = 0;
        foreach (var (flow, d, u) in open)
        {
            if (!ReferenceEquals(flow.App, app)) continue;
            down += d * (0.55 + wave * 0.9);
            up += u * (0.6 + wave * 0.8);
        }
        // A little background chatter for apps without a busy connection.
        if (down == 0 && i % 9 == 0) down = 18 * 1024;
        return (down, up);
    }

    private static string DemoConnect(string host, int port) =>
        $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: keep-alive\r\n" +
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0\r\n\r\n";

    private static string DemoRequest(string method, string host, string path) =>
        $"{method} http://{host}{path} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
        "Accept: text/html,application/xhtml+xml,*/*\r\nAccept-Encoding: gzip, deflate\r\nCookie: session=8f2c41d0\r\n\r\n";

    private static string DemoResponse(int status, string? type, long length)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", 503 => "Service Unavailable", _ => "OK" };
        return $"HTTP/1.1 {status} {reason}\r\nDate: {DateTime.UtcNow:R}\r\nContent-Type: {type ?? "text/html"}\r\n" +
               $"Content-Length: {Math.Max(0, length - 220)}\r\nConnection: close\r\nSet-Cookie: id=a3fWa; Max-Age=2592000\r\n\r\n";
    }
}

/// <summary>Small helpers for HTTP/1.x message heads as they pass through the relay.</summary>
internal static class HttpHead
{
    private const int MaxStored = 8 * 1024;

    private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    };

    /// <summary>The head as text with cookies and credentials replaced, for showing and saving.</summary>
    public static string Redact(string head)
    {
        var sb = new StringBuilder(Math.Min(head.Length, MaxStored));
        var lines = head.Split('\n');
        for (int i = 0; i < lines.Length && sb.Length < MaxStored; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            int colon = i > 0 ? line.IndexOf(':') : -1;
            if (colon > 0 && Secret.Contains(line[..colon].Trim()))
                line = line[..colon] + ": [hidden by PdaNet]";
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    public static string? Header(string? head, string name)
    {
        if (string.IsNullOrEmpty(head)) return null;
        foreach (var raw in head.Split('\n').Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (IsNamed(line, name)) return line[(name.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>"HTTP/1.1 404 Not Found" gives 404, anything else gives 0.</summary>
    public static int StatusCode(string? head)
    {
        if (string.IsNullOrEmpty(head) || !head.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) return 0;
        var parts = FirstLine(head).Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 0;
    }

    public static string ReasonPhrase(string? head)
    {
        if (string.IsNullOrEmpty(head)) return "";
        var parts = FirstLine(head).Split(' ', 3);
        return parts.Length > 2 ? parts[2].Trim() : "";
    }

    /// <summary>
    /// Asks the far end to close after one response, so every plain HTTP request travels on its
    /// own connection. That keeps each request on its own line in the Apps page, and stops a
    /// browser from reusing a connection to one site for a request to another.
    /// </summary>
    public static string CloseAfterResponse(string head)
    {
        if (Header(head, "Upgrade") != null) return head; // websockets keep their connection
        // So does Windows sign in to intranet sites, which needs its challenge and answer on one connection.
        var auth = Header(head, "Authorization");
        if (auth != null && (auth.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase)
                             || auth.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase)))
            return head;
        var lines = head.Split("\r\n");
        var sb = new StringBuilder(head.Length + 24);
        sb.Append(lines[0]).Append("\r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || IsNamed(line, "Connection") || IsNamed(line, "Proxy-Connection") || IsNamed(line, "Keep-Alive"))
                continue;
            sb.Append(line).Append("\r\n");
        }
        return sb.Append("Connection: close\r\n\r\n").ToString();
    }

    /// <summary>A plain words reason for a connection that could not be made.</summary>
    public static string Reason(Exception e) => e switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }
            => "Could not find this website",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "The website refused to connect",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "The website did not answer in time",
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable }
            => "Could not reach this website",
        OperationCanceledException => "Stopped when PdaNet disconnected",
        _ => "Could not reach this website",
    };

    private static string FirstLine(string head)
    {
        int end = head.IndexOf('\n');
        return (end >= 0 ? head[..end] : head).TrimEnd('\r');
    }

    private static bool IsNamed(string line, string name) =>
        line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Collects the first bytes of a response as they are relayed, until its head is complete.
/// It only keeps a copy, the data itself is never held back.
/// </summary>
internal sealed class HeadSniffer
{
    private const int Limit = 16 * 1024;
    private readonly byte[] _buf = new byte[Limit];
    private int _len;

    /// <summary>Feeds a relayed chunk. Returns true when sniffing is over, with the head if one was found.</summary>
    public bool Feed(ReadOnlySpan<byte> chunk, out string? head)
    {
        head = null;
        int take = Math.Min(chunk.Length, Limit - _len);
        chunk[..take].CopyTo(_buf.AsSpan(_len));
        _len += take;

        while (true)
        {
            int end = _buf.AsSpan(0, _len).IndexOf("\r\n\r\n"u8);
            if (end < 0) return _len >= Limit;

            var found = Encoding.Latin1.GetString(_buf, 0, end);
            int code = HttpHead.StatusCode(found);
            if (code is >= 100 and < 200 && code != 101)
            {
                // An interim answer such as 100 Continue, the real one follows.
                int used = end + 4;
                Buffer.BlockCopy(_buf, used, _buf, 0, _len - used);
                _len -= used;
                continue;
            }
            head = found;
            return true;
        }
    }
}
