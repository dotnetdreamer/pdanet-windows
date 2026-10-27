using System.IO;
using System.Text.Json;
using PdaNetPortable.Core;
using PdaNetPortable.Models;

namespace PdaNetPortable.Services;

/// <summary>
/// Settings / profiles / state persistence. Mirrors config_manager.py
/// (config.json, profiles.json, state.json) with atomic writes + validation clamps.
/// </summary>
public sealed class ConfigManager
{
    private static readonly Lazy<ConfigManager> _instance = new(() => new ConfigManager());
    public static ConfigManager Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public AppConfig Config { get; private set; }
    public List<ConnectionProfile> Profiles { get; private set; }
    private Dictionary<string, JsonElement> _state;

    private ConfigManager()
    {
        Config = LoadConfig();
        Profiles = LoadProfiles();
        _state = LoadState();
        Validate(Config);
    }

    // ---- Config ----
    private AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(AppPaths.Config))
            {
                var json = File.ReadAllText(AppPaths.Config);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception e)
        {
            // Back up the corrupted file and fall back to defaults.
            try { File.Move(AppPaths.Config, AppPaths.Config + ".corrupted", true); } catch { }
            Logger.Instance.Error($"Error loading config, using defaults: {e.Message}");
        }
        return new AppConfig();
    }

    public void SaveConfig()
    {
        Validate(Config);
        AtomicWrite(AppPaths.Config, JsonSerializer.Serialize(Config, JsonOpts));
    }

    private static void Validate(AppConfig c)
    {
        c.StealthLevel = Math.Clamp(c.StealthLevel, 1, 3);
        c.ProxyPort = Math.Clamp(c.ProxyPort, 1, 65535);
        c.LocalProxyPort = Math.Clamp(c.LocalProxyPort, 1, 65535);
        c.ReconnectAttempts = Math.Clamp(c.ReconnectAttempts, 1, 10);
        c.ReconnectDelay = Math.Clamp(c.ReconnectDelay, 1, 300);
        c.ConnectionTimeout = Math.Clamp(c.ConnectionTimeout, 5, 300);
        c.StatusUpdateInterval = Math.Clamp(c.StatusUpdateInterval, 100, 10000);
        var levels = new[] { "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" };
        if (!levels.Contains(c.LogLevel.ToUpperInvariant())) c.LogLevel = "INFO";
    }

    // ---- Profiles ----
    private List<ConnectionProfile> LoadProfiles()
    {
        try
        {
            if (File.Exists(AppPaths.Profiles))
            {
                var json = File.ReadAllText(AppPaths.Profiles);
                return JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? new();
            }
        }
        catch (Exception e) { Logger.Instance.Error($"Error loading profiles: {e.Message}"); }
        return new();
    }

    public void SaveProfiles() =>
        AtomicWrite(AppPaths.Profiles, JsonSerializer.Serialize(Profiles, JsonOpts));

    public void AddOrUpdateProfile(ConnectionProfile p)
    {
        var existing = Profiles.FirstOrDefault(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Profiles.Remove(existing);
        if (string.IsNullOrEmpty(p.Created)) p.Created = DateTime.Now.ToString("o");
        Profiles.Add(p);
        SaveProfiles();
    }

    public void DeleteProfile(string name)
    {
        var existing = Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { Profiles.Remove(existing); SaveProfiles(); }
    }

    // ---- State ----
    private Dictionary<string, JsonElement> LoadState()
    {
        try
        {
            if (File.Exists(AppPaths.State))
            {
                var json = File.ReadAllText(AppPaths.State);
                return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void SetState(string key, object value)
    {
        _state[key] = JsonSerializer.SerializeToElement(value);
        AtomicWrite(AppPaths.State, JsonSerializer.Serialize(_state, JsonOpts));
    }

    public T? GetState<T>(string key, T? fallback = default)
    {
        if (_state.TryGetValue(key, out var el))
        {
            try { return el.Deserialize<T>(); } catch { }
        }
        return fallback;
    }

    private static void AtomicWrite(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            Logger.Instance.Error($"Failed to write {Path.GetFileName(path)}: {e.Message}");
        }
    }
}
