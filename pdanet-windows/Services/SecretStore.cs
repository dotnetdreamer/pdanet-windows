using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdaNetPortable.Core;

namespace PdaNetPortable.Services;

/// <summary>
/// Stores WiFi passwords encrypted at rest with Windows DPAPI (per-user).
/// Windows equivalent of the upstream secret_store.py / keyring usage.
/// Data file holds base64(DPAPI-protected UTF8 password) keyed by SSID.
/// </summary>
public static class SecretStore
{
    private static readonly object _lock = new();

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.WifiNetworks)) return new();
            var json = File.ReadAllText(AppPaths.WifiNetworks);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(Dictionary<string, string> map)
    {
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.WifiNetworks, json);
    }

    public static void SetPassword(string ssid, string password)
    {
        if (string.IsNullOrEmpty(ssid)) return;
        lock (_lock)
        {
            var map = Load();
            try
            {
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(password),
                    null,
                    DataProtectionScope.CurrentUser);
                map[ssid] = Convert.ToBase64String(protectedBytes);
            }
            catch
            {
                // Fallback: store plaintext (best effort on non-DPAPI environments).
                map[ssid] = "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
            }
            Save(map);
        }
    }

    public static string? GetPassword(string ssid)
    {
        lock (_lock)
        {
            var map = Load();
            if (!map.TryGetValue(ssid, out var stored) || string.IsNullOrEmpty(stored))
                return null;
            try
            {
                if (stored.StartsWith("plain:"))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(6)));

                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored),
                    null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }

    public static void Delete(string ssid)
    {
        lock (_lock)
        {
            var map = Load();
            if (map.Remove(ssid)) Save(map);
        }
    }

    public static List<string> ListSsids()
    {
        lock (_lock) return Load().Keys.ToList();
    }
}
