using System.IO;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

public enum LogLevel { Debug = 0, Info = 1, Ok = 1, Warning = 2, Error = 3, Critical = 4 }

public record LogEntry(string Timestamp, string Level, string Message)
{
    public string Display => $"{Timestamp}  [{Level}]  {Message}";
}

/// <summary>
/// Rotating file logger with an in-memory ring buffer for the GUI log panel.
/// Ported from src/logger.py (same level names incl. OK, same buffer behaviour).
/// </summary>
public sealed class Logger
{
    private static readonly Lazy<Logger> _instance = new(() => new Logger());
    public static Logger Instance => _instance.Value;

    private readonly object _lock = new();
    private readonly List<LogEntry> _buffer = new();
    private readonly string _logFile;
    private LogLevel _minLevel = LogLevel.Info;

    public event Action<LogEntry>? EntryAdded;

    private Logger()
    {
        _logFile = AppPaths.LogFile;
    }

    public void SetLevel(string level)
    {
        _minLevel = level.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "WARNING" or "WARN" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" => LogLevel.Critical,
            _ => LogLevel.Info,
        };
    }

    public void Debug(string m) => Add(LogLevel.Debug, "DEBUG", m);
    public void Info(string m) => Add(LogLevel.Info, "INFO", m);
    public void Ok(string m) => Add(LogLevel.Info, "OK", m);
    public void Warning(string m) => Add(LogLevel.Warning, "WARN", m);
    public void Error(string m) => Add(LogLevel.Error, "ERROR", m);
    public void Critical(string m) => Add(LogLevel.Critical, "CRITICAL", m);

    private void Add(LogLevel level, string label, string message)
    {
        var entry = new LogEntry(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            label,
            message);

        // Always write INFO+ (and OK) to file; respect min level for buffer/GUI.
        WriteToFile(entry);

        if (level < _minLevel) return;

        lock (_lock)
        {
            _buffer.Add(entry);
            if (_buffer.Count > Constants.LogBufferMaxSize)
                _buffer.RemoveAt(0);
        }
        try { EntryAdded?.Invoke(entry); } catch { /* GUI not ready */ }
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 200)
    {
        lock (_lock)
        {
            if (_buffer.Count <= count) return _buffer.ToList();
            return _buffer.GetRange(_buffer.Count - count, count);
        }
    }

    public void ClearBuffer()
    {
        lock (_lock) _buffer.Clear();
    }

    private void WriteToFile(LogEntry e)
    {
        try
        {
            lock (_lock)
            {
                RotateIfNeeded();
                File.AppendAllText(_logFile, $"[{e.Timestamp}] [{e.Level}] :: {e.Message}{Environment.NewLine}");
            }
        }
        catch { /* best effort */ }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logFile);
            if (!fi.Exists || fi.Length < Constants.LogMaxBytes) return;

            // pdanet.log.5 drops off; shift others up by one.
            var oldest = $"{_logFile}.{Constants.LogBackupCount}";
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = Constants.LogBackupCount - 1; i >= 1; i--)
            {
                var src = $"{_logFile}.{i}";
                var dst = $"{_logFile}.{i + 1}";
                if (File.Exists(src)) File.Move(src, dst, true);
            }
            File.Move(_logFile, $"{_logFile}.1", true);
        }
        catch { /* best effort */ }
    }
}
