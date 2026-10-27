using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// Bandwidth / latency / quality tracking, ported from stats_collector.py.
/// Uses per-interface byte counters (Windows analogue of /sys/class/net stats) plus ICMP
/// ping for latency, and records sessions to connection_history.json.
/// </summary>
public sealed class StatsCollector
{
    private static readonly Lazy<StatsCollector> _instance = new(() => new StatsCollector());
    public static StatsCollector Instance => _instance.Value;

    private readonly object _lock = new();
    private DateTime? _start;
    private NetworkInterface? _nic;
    public string? InterfaceAlias { get; private set; }

    private long _baseRx, _baseTx;        // cumulative counters captured at session start
    private long _lastRx, _lastTx;
    private DateTime _lastSample;

    public double DownloadRate { get; private set; } // bytes/sec
    public double UploadRate { get; private set; }
    public long TotalDownloaded { get; private set; }
    public long TotalUploaded { get; private set; }

    private readonly Queue<double> _latency = new();
    public double? Latency { get; private set; }
    public double PacketLoss { get; private set; }

    public double UptimeSeconds => _start.HasValue ? (DateTime.Now - _start.Value).TotalSeconds : 0;
    public bool SessionActive => _start.HasValue;

    public void SetInterface(NetworkInterface? nic)
    {
        lock (_lock)
        {
            _nic = nic;
            InterfaceAlias = nic?.Name;
            (_baseRx, _baseTx) = ReadCounters();
            _lastRx = _baseRx;
            _lastTx = _baseTx;
            _lastSample = DateTime.Now;
        }
    }

    public void StartSession()
    {
        lock (_lock)
        {
            _start = DateTime.Now;
            (_baseRx, _baseTx) = ReadCounters();
            _lastRx = _baseRx;
            _lastTx = _baseTx;
            _lastSample = DateTime.Now;
            DownloadRate = UploadRate = 0;
            TotalDownloaded = TotalUploaded = 0;
            _latency.Clear();
            Latency = null;
            PacketLoss = 0;
        }
    }

    public void StopSession()
    {
        SaveSessionToHistory();
        lock (_lock) _start = null;
    }

    public void Update()
    {
        lock (_lock)
        {
            if (_nic == null) return;
            var (rx, tx) = ReadCounters();
            var now = DateTime.Now;
            var dt = (now - _lastSample).TotalSeconds;
            if (dt > 0)
            {
                DownloadRate = Math.Max(0, (rx - _lastRx) / dt);
                UploadRate = Math.Max(0, (tx - _lastTx) / dt);
            }
            TotalDownloaded = Math.Max(0, rx - _baseRx);
            TotalUploaded = Math.Max(0, tx - _baseTx);
            _lastRx = rx;
            _lastTx = tx;
            _lastSample = now;
        }
    }

    private (long rx, long tx) ReadCounters()
    {
        try
        {
            if (_nic == null) return (0, 0);
            var s = _nic.GetIPStatistics();
            return (s.BytesReceived, s.BytesSent);
        }
        catch { return (_lastRx, _lastTx); }
    }

    public double? PingTest(string host = "8.8.8.8")
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(host, 2000);
            if (reply is { Status: IPStatus.Success })
            {
                var ms = reply.RoundtripTime;
                lock (_lock)
                {
                    _latency.Enqueue(ms);
                    while (_latency.Count > 30) _latency.Dequeue();
                    Latency = ms;
                }
                return ms;
            }
        }
        catch { }
        return null;
    }

    public double AverageLatency()
    {
        lock (_lock) return _latency.Count == 0 ? 0 : _latency.Average();
    }

    /// <summary>Connection quality 0-100, ported from get_connection_quality().</summary>
    public int Quality()
    {
        double quality = 100;
        var latency = AverageLatency();
        if (latency > 0)
        {
            if (latency > 200) quality -= 30;
            else if (latency > 100) quality -= 15;
            else if (latency > 50) quality -= 5;
        }
        quality -= PacketLoss * 2;
        return (int)Math.Clamp(quality, 0, 100);
    }

    private void SaveSessionToHistory()
    {
        if (!_start.HasValue) return;
        try
        {
            var session = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTime.Now.ToString("o"),
                ["duration"] = (int)UptimeSeconds,
                ["downloaded"] = TotalDownloaded,
                ["uploaded"] = TotalUploaded,
                ["interface"] = InterfaceAlias,
                ["avg_latency"] = AverageLatency(),
            };

            List<Dictionary<string, object?>> history = new();
            if (File.Exists(AppPaths.ConnectionHistory))
            {
                try
                {
                    history = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(
                        File.ReadAllText(AppPaths.ConnectionHistory)) ?? new();
                }
                catch { history = new(); }
            }
            history.Add(session);
            if (history.Count > 100) history = history.GetRange(history.Count - 100, 100);
            File.WriteAllText(AppPaths.ConnectionHistory,
                JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Logger.Instance.Warning($"Could not save session history: {e.Message}"); }
    }
}
