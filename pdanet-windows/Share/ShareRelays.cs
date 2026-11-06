using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdaNetPortable.Models;
using PdaNetPortable.Services;

namespace PdaNetPortable.Share;

/// <summary>
/// Relays (TURN servers) for screen sharing. Some networks keep a PC from a direct link, most
/// often a company network that lets no UDP out. A relay then carries the session, over TLS on
/// port 443 when nothing else gets through. WebRTC still takes a direct path when there is one.
/// The relay passes on encrypted packets it cannot read.
///
/// PdaNet comes with no relay: the person sets one up in Settings, in any of three ways. With a
/// free Cloudflare Realtime TURN key the helper fetches credentials that last a day for each
/// session, so the token never leaves this PC. A web address that hands out a relay with short
/// lived credentials for each session (a coturn REST endpoint, as the Ludo relay is) takes only
/// that address. Any other TURN service, or one of the person's own, takes its addresses, a
/// username and a password. Only the PC whose network blocks direct links needs a relay.
/// </summary>
internal static class ShareRelays
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("pdanet screen sharing relay");

    // turn:host[:port][?transport=udp|tcp], or turns: for TLS. The helper checks them again.
    private static readonly Regex UrlPattern = new(
        @"^turns?:(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:(\d{1,5}))?(\?transport=(udp|tcp))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex KeyIdPattern = new(@"^[A-Za-z0-9_-]{8,128}$", RegexOptions.CultureInvariant);

    /// <summary>The relay settings as the person typed them, with the secrets in the clear.</summary>
    public sealed record Settings(string KeyId, string KeyToken, IReadOnlyList<string> Urls, string Username, string Password, string From)
    {
        public bool HasKey => KeyId.Length > 0 && KeyToken.Length > 0;
        public bool HasOwn => Urls.Count > 0;
        public bool HasFrom => From.Length > 0;
        public bool Any => HasKey || HasOwn || HasFrom;
    }

    public static Settings Read(AppConfig cfg) => new(
        cfg.ShareRelayKeyId ?? "", Unprotect(cfg.ShareRelayKeyToken),
        cfg.ShareRelayUrls ?? new List<string>(), cfg.ShareRelayUsername ?? "", Unprotect(cfg.ShareRelayPassword),
        Unprotect(cfg.ShareRelayFrom));

    public static void Write(AppConfig cfg, Settings s)
    {
        cfg.ShareRelayKeyId = s.KeyId;
        cfg.ShareRelayKeyToken = Protect(s.KeyToken);
        cfg.ShareRelayUrls = s.Urls.ToList();
        cfg.ShareRelayUsername = s.Username;
        cfg.ShareRelayPassword = Protect(s.Password);
        cfg.ShareRelayFrom = Protect(s.From);
    }

    public static bool Configured(AppConfig cfg) => Read(cfg).Any;

    /// <summary>What is wrong with the settings, or null when the helper can use them.</summary>
    public static string? Problem(Settings s)
    {
        if (s.KeyId.Length > 0 != s.KeyToken.Length > 0)
            return "The Cloudflare relay needs both the key ID and the API token";
        if (s.KeyId.Length > 0 && !KeyIdPattern.IsMatch(s.KeyId))
            return "That Cloudflare key ID does not look right, copy the Turn Token ID from the Cloudflare dashboard";
        if (s.HasFrom && !(Uri.TryCreate(s.From, UriKind.Absolute, out var from) && from.Scheme == Uri.UriSchemeHttps && from.Host.Length > 0))
            return "The relay web address must be a whole address that starts with https://";
        foreach (var url in s.Urls)
        {
            var m = UrlPattern.Match(url);
            if (!m.Success)
                return $"{url} is not a relay address. One looks like turns:turn.example.com:443?transport=tcp";
            if (m.Groups[3].Success && int.Parse(m.Groups[3].Value) is < 1 or > 65535)
                return $"{url} has a port that does not exist";
            if (url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase) && m.Groups[5].Value.Equals("udp", StringComparison.OrdinalIgnoreCase))
                return $"{url}: PdaNet takes turns: over TCP only, leave out ?transport=udp";
        }
        if (s.HasOwn && (s.Username.Length == 0 || s.Password.Length == 0))
            return "Your own relay needs a username and a password";
        return null;
    }

    /// <summary>The relay addresses typed one per line, or with spaces or commas between them.</summary>
    public static List<string> SplitUrls(string text) =>
        text.Split(new[] { '\r', '\n', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The Cloudflare key as the helper takes it, or null.</summary>
    public static object? Key(Settings s) => s.HasKey ? new { id = s.KeyId, token = s.KeyToken } : null;

    /// <summary>The person's own relays as the helper takes them, or null.</summary>
    public static object[]? Servers(Settings s) =>
        s.HasOwn ? new object[] { new { urls = s.Urls, username = s.Username, credential = s.Password } } : null;

    /// <summary>The web address that hands out a relay, as the helper takes it, or null.</summary>
    public static string? From(Settings s) => s.HasFrom ? s.From : null;

    private static string Protect(string secret) => secret.Length == 0 ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; } // another Windows user's, or damaged: typed again in Settings
    }

    // ============================ Test ============================

    /// <summary>
    /// Tries the relays the way a session uses them: the helper gets a relay address from each
    /// one, then runs a whole session on this PC that may only go through them. Returns whether a
    /// session works through them, and lines that say what happened.
    /// </summary>
    public static async Task<(bool Ok, string Text)> TestAsync(Settings s, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(new
        {
            op = "test",
            route = ShareSession.BuildRoute(false).Route,
            turn = Servers(s),
            turnKey = Key(s),
            turnFrom = From(s),
        });
        var psi = new ProcessStartInfo(RtcHelper.Extract(), "relaytest")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the screen sharing helper");
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Instance.Debug("[relay test] " + e.Data);
        };
        proc.BeginErrorReadLine();
        // The secrets go on stdin, never on the command line where other programs can read them.
        await proc.StandardInput.WriteAsync(input);
        proc.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        var lines = new List<string>();
        bool ok = false;
        try
        {
            while (await proc.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                string Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool good = e.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                long ms = e.TryGetProperty("ms", out var m) && m.TryGetInt64(out var n) ? n : 0;
                switch (Str("ev"))
                {
                    case "server":
                        var where = Str("host") is { Length: > 0 } h ? $"{h} by {Str("way")}" : Str("url");
                        lines.Add(good ? $"{where}: works, {ms} ms" : $"{where}: {Str("detail")}");
                        break;
                    case "session":
                        lines.Add(good
                            ? "A test session went through the relay. Screen sharing uses it when a direct link fails"
                            : $"The relay gave addresses, but a test session through it did not connect: {Str("detail")}");
                        break;
                    case "key":
                        lines.Add($"Cloudflare relay: {Str("detail")}");
                        break;
                    case "from":
                        lines.Add($"Relay web address: {Str("detail")}");
                        break;
                    case "failed":
                        lines.Add(Str("detail"));
                        break;
                    case "done":
                        ok = good;
                        if (!good && !lines.Any(l => l.StartsWith("The relay gave", StringComparison.Ordinal)))
                            lines.Add("No relay worked from this PC");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lines.Add(ct.IsCancellationRequested ? "Test stopped" : "The test took too long");
        }
        catch (JsonException)
        {
            lines.Add("The screen sharing helper gave an answer PdaNet cannot read");
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { /* already gone */ }
        }
        if (lines.Count == 0) lines.Add("The screen sharing helper stopped without an answer");
        Logger.Instance.Info("Screen sharing relay test: " + string.Join(" | ", lines));
        return (ok, string.Join("\n", lines));
    }
}
