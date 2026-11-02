using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using PdaNetPortable.Core;
using PdaNetPortable.Services;

namespace PdaNetPortable.Share;

/// <summary>
/// Runs pdanet-rtc.exe, the WebRTC side of screen sharing (rtc/ in the repository, Go and Pion),
/// for one session and talks to it over its stdin and stdout. Each frame is a u32 length, a u8
/// kind and the payload, see rtc/ipc.go. The helper ships inside this exe and is unpacked to the
/// data folder the first time a session starts. It exits when the session ends or when PdaNet
/// closes, since it stops as soon as its stdin closes.
/// </summary>
internal sealed class RtcHelper : IDisposable
{
    public const byte KindJson = 1, KindVideo = 2, KindCtl = 3, KindLive = 4;

    private const string Resource = "PdaNetPortable.rtc.pdanet-rtc.exe";
    private static readonly object ExtractLock = new();
    private static string? _path;

    private readonly Process _proc;
    private readonly Stream _toHelper;
    private readonly Stream _fromHelper;
    private readonly object _writeLock = new();
    private readonly byte[] _header = new byte[5];
    private volatile bool _closed;

    /// <summary>A control message from the helper, {"ev": ...}. Raised on the reader thread.</summary>
    public event Action<JsonElement>? Event;
    /// <summary>A frame of the other PC's screen: u8 flags, u32 RTP timestamp, H.264. Valid during the call only.</summary>
    public event Action<ReadOnlyMemory<byte>>? Video;
    /// <summary>A remote control message from the other PC, with the channel it came on. Valid during the call only.</summary>
    public event Action<byte, ReadOnlyMemory<byte>>? Message;
    /// <summary>The helper stopped.</summary>
    public event Action? Exited;

    private RtcHelper(Process proc)
    {
        _proc = proc;
        _toHelper = proc.StandardInput.BaseStream;
        _fromHelper = proc.StandardOutput.BaseStream;
    }

    /// <summary>Starts a helper and sends the first message, which says what the session is.</summary>
    public static RtcHelper Start(object start)
    {
        var psi = new ProcessStartInfo(Extract())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Extract())!,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the screen sharing helper");
        var helper = new RtcHelper(proc);
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Instance.Debug("[share] " + e.Data);
        };
        proc.BeginErrorReadLine();
        helper.SendJson(start);
        new Thread(helper.ReadLoop) { IsBackground = true, Name = "Share helper" }.Start();
        return helper;
    }

    /// <summary>
    /// Unpacks the helper next to the data folder, as share\pdanet-rtc.exe. The name stays the same
    /// when PdaNet updates, so whatever the person told Windows Firewall about it still holds; a
    /// small file beside it keeps the helper's hash, so a newer PdaNet never runs an older helper.
    /// </summary>
    public static string Extract()
    {
        lock (ExtractLock)
        {
            if (_path != null && File.Exists(_path)) return _path;
            var asm = Assembly.GetExecutingAssembly();
            using var res = asm.GetManifestResourceStream(Resource)
                            ?? throw new InvalidOperationException("The screen sharing helper is missing from this build");
            var hash = Convert.ToHexString(SHA256.HashData(res))[..16].ToLowerInvariant();
            var dir = Path.Combine(AppPaths.DataDir, "share");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "pdanet-rtc.exe");
            var mark = path + ".sha";
            bool current = File.Exists(path) && File.Exists(mark) && new FileInfo(path).Length == res.Length
                           && File.ReadAllText(mark).Trim() == hash;
            if (!current)
            {
                try { Write(res, path); }
                catch (IOException)
                {
                    // Another PdaNet still runs the old one: this one runs its own copy.
                    path = Path.Combine(dir, $"pdanet-rtc-{hash}.exe");
                    if (!File.Exists(path) || new FileInfo(path).Length != res.Length) Write(res, path);
                    return _path = path;
                }
                File.WriteAllText(mark, hash);
            }
            foreach (var old in Directory.GetFiles(dir, "pdanet-rtc-*.exe"))
                try { File.Delete(old); } catch { /* still running somewhere, next time */ }
            return _path = path;
        }
    }

    private static void Write(Stream res, string path)
    {
        res.Position = 0;
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) res.CopyTo(fs);
        File.Move(tmp, path, overwrite: true);
    }

    public void SendJson(object message) => Send(KindJson, JsonSerializer.SerializeToUtf8Bytes(message));

    public void Send(byte kind, ReadOnlySpan<byte> payload) => Send(kind, payload, ReadOnlySpan<byte>.Empty);

    /// <summary>Sends one frame. Writes from several threads are kept whole by a lock.</summary>
    public void Send(byte kind, ReadOnlySpan<byte> head, ReadOnlySpan<byte> body)
    {
        if (_closed) return;
        lock (_writeLock)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_header, (uint)(1 + head.Length + body.Length));
            _header[4] = kind;
            try
            {
                _toHelper.Write(_header);
                if (!head.IsEmpty) _toHelper.Write(head);
                if (!body.IsEmpty) _toHelper.Write(body);
                _toHelper.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _closed = true;
            }
        }
    }

    private void ReadLoop()
    {
        var header = new byte[5];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            while (true)
            {
                if (!ReadExactly(header)) break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length < 1 || length > 32 << 20) break;
                int n = length - 1;
                if (buffer.Length < n)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(n);
                }
                if (!ReadExactly(buffer.AsSpan(0, n))) break;
                var payload = new ReadOnlyMemory<byte>(buffer, 0, n);
                try { Dispatch(header[4], payload); }
                catch (Exception e) { Logger.Instance.Warning($"Screen sharing could not handle a message: {e.Message}"); }

                // One big keyframe should not keep a big buffer for the rest of the session.
                if (buffer.Length > 4 << 20)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _closed = true;
            Exited?.Invoke();
        }
    }

    private void Dispatch(byte kind, ReadOnlyMemory<byte> payload)
    {
        switch (kind)
        {
            case KindJson:
                using (var doc = JsonDocument.Parse(payload))
                    Event?.Invoke(doc.RootElement.Clone());
                break;
            case KindVideo:
                Video?.Invoke(payload);
                break;
            case KindCtl:
            case KindLive:
                Message?.Invoke(kind, payload);
                break;
        }
    }

    private bool ReadExactly(Span<byte> into)
    {
        while (!into.IsEmpty)
        {
            int n = _fromHelper.Read(into);
            if (n <= 0) return false;
            into = into[n..];
        }
        return true;
    }

    /// <summary>Asks the helper to end the session, tell the other PC and exit, then makes sure it is gone.</summary>
    public void Stop()
    {
        if (!_closed) SendJson(new { op = "stop" });
        _closed = true;
        try { _toHelper.Close(); } catch { }
        // The helper says goodbye to the other PC first, which takes a moment.
        if (!_proc.WaitForExit(1500))
            try { _proc.Kill(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _proc.Dispose();
    }
}
