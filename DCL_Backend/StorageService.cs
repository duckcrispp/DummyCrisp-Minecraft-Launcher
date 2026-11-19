using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace DCL_Launcher;

public sealed class StorageService
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string RootDir { get; }
    public string DclDir { get; }
    public string ConfigDir { get; }
    public string CacheDir { get; }
    public string ManifestsCacheDir { get; }
    public string LogsDir { get; }
    public string MinecraftDir { get; }
    public string VersionsDir { get; }
    public string LibrariesDir { get; }
    public string AssetsDir { get; }
    public string ModsDir { get; }
    public string ResourcePacksDir { get; }
    public string ShaderPacksDir { get; }
    public string SavesDir { get; }
    public string RuntimeJavaDir { get; }

    public string SettingsPath { get; }
    public string AccountsPath { get; }
    public string FavoritesPath { get; }

    private readonly object _logLock = new();
    private readonly ConcurrentDictionary<string, (string GameDir, string? McRoot)> _externalInstancePaths = new(StringComparer.OrdinalIgnoreCase);
    public string SessionLogPath { get; }

    public StorageService(string? baseDir = null)
    {
        RootDir = string.IsNullOrWhiteSpace(baseDir)
            ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : baseDir;

        DclDir = Path.Combine(RootDir, "DCL");
        ConfigDir = Path.Combine(DclDir, "config");
        CacheDir = Path.Combine(DclDir, "cache");
        ManifestsCacheDir = Path.Combine(CacheDir, "manifests");
        LogsDir = Path.Combine(DclDir, "logs");
        RuntimeJavaDir = Path.Combine(DclDir, "runtime");

        MinecraftDir = Path.Combine(RootDir, ".minecraft");
        VersionsDir = Path.Combine(MinecraftDir, "versions");
        LibrariesDir = Path.Combine(MinecraftDir, "libraries");
        AssetsDir = Path.Combine(MinecraftDir, "assets");
        ModsDir = Path.Combine(MinecraftDir, "mods");
        ResourcePacksDir = Path.Combine(MinecraftDir, "resourcepacks");
        ShaderPacksDir = Path.Combine(MinecraftDir, "shaderpacks");
        SavesDir = Path.Combine(MinecraftDir, "saves");

        SettingsPath = Path.Combine(ConfigDir, "settings.json");
        AccountsPath = Path.Combine(ConfigDir, "accounts.dat");
        FavoritesPath = Path.Combine(ConfigDir, "instances.json");
        SessionLogPath = Path.Combine(LogsDir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}.log");

        EnsureDirectories();
    }

    public void SetExternalInstancePaths(IEnumerable<InstanceMeta> metas)
    {
        _externalInstancePaths.Clear();
        foreach (var m in metas)
        {
            if (!string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.ExternalPath))
                _externalInstancePaths[m.Id] = (m.ExternalPath, m.ExternalMinecraftDir);
        }
    }

    public void RegisterExternalInstancePath(string instanceId, string gameDir, string? mcRoot)
    {
        if (!string.IsNullOrWhiteSpace(instanceId) && !string.IsNullOrWhiteSpace(gameDir))
            _externalInstancePaths[instanceId] = (gameDir, mcRoot);
    }

    public void UnregisterExternalInstancePath(string instanceId)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
            _externalInstancePaths.TryRemove(instanceId, out _);
    }

    public string? TryGetExternalMinecraftDir(string instanceId) =>
        _externalInstancePaths.TryGetValue(instanceId, out var ext) ? ext.McRoot : null;

    public string GameDirectory(string instanceId) =>
        _externalInstancePaths.TryGetValue(instanceId, out var ext) && !string.IsNullOrWhiteSpace(ext.GameDir)
            ? ext.GameDir
            : Path.Combine(VersionsDir, instanceId);

    public string ResolveResourceDirectory(string instanceId, string subfolder)
    {
        string gameDir = GameDirectory(instanceId);
        if (string.IsNullOrEmpty(subfolder)) return gameDir;
        string isolatedDir = Path.Combine(gameDir, subfolder);
        if (Directory.Exists(isolatedDir)) return isolatedDir;
        if (_externalInstancePaths.TryGetValue(instanceId, out var ext) &&
            !string.IsNullOrWhiteSpace(ext.McRoot) &&
            !Directory.Exists(Path.Combine(gameDir, "mods")) &&
            !File.Exists(Path.Combine(gameDir, "options.txt")))
        {
            string sharedDir = Path.Combine(ext.McRoot, subfolder);
            if (Directory.Exists(sharedDir)) return sharedDir;
        }
        return isolatedDir;
    }

    internal const string ModIconRoute = "launcher-mod-icons/";
    private static readonly string[] AllowedIconExtensions = [".png", ".webp", ".jpg", ".jpeg"];
    private string ModIconDirectory => Path.Combine(CacheDir, "mod-icons");

    internal string CacheModIcon(byte[] data) => CacheIconAsset(data, ".png");

    internal string CacheIconAsset(byte[] data, string extension)
    {
        string ext = AllowedIconExtensions.FirstOrDefault(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)) ?? ".png";
        string name = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + ext;
        string path = Path.Combine(ModIconDirectory, name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(ModIconDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return ModIconRoute + name;
    }

    internal Stream? OpenModIcon(string name)
    {
        string? ext = AllowedIconExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.Ordinal));
        if (ext == null || name.Length != 64 + ext.Length || !name.Take(64).All(Uri.IsHexDigit)) return null;
        string path = Path.Combine(ModIconDirectory, name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DclDir);
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ManifestsCacheDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RuntimeJavaDir);

        Directory.CreateDirectory(MinecraftDir);
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(LibrariesDir);
        Directory.CreateDirectory(Path.Combine(AssetsDir, "indexes"));
        Directory.CreateDirectory(Path.Combine(AssetsDir, "objects"));
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(ResourcePacksDir);
        Directory.CreateDirectory(ShaderPacksDir);
        Directory.CreateDirectory(SavesDir);
    }

    public void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(SessionLogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {RedactLogSecrets(message)}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    private static string RedactLogSecrets(string message) =>
        Regex.Replace(message,
            """(?i)(\b(?:authorization|x-api-key|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret)\b["']?\s*(?:=|:|\s)\s*["']?(?:Bearer\s+)?)[^"'\s,;&]+""",
            "$1[已隐去]");

    public DclConfig LoadConfig()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<DclConfig>(json, JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log($"LoadConfig error: {ex.Message}");
        }
        var fresh = new DclConfig();
        SaveConfig(fresh);
        return fresh;
    }

    public void SaveConfig(DclConfig cfg)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(cfg, JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log($"SaveConfig error: {ex.Message}");
        }
    }

    public AccountsStore LoadAccounts()
    {
        try
        {
            if (File.Exists(AccountsPath))
            {
                byte[] encrypted = File.ReadAllBytes(AccountsPath);
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plain);
                var store = JsonSerializer.Deserialize<AccountsStore>(json, JsonOpts);
                if (store != null) return store;
            }
        }
        catch (Exception ex)
        {
            Log($"LoadAccounts error: {ex.Message}");
        }
        var fallback = new AccountsStore();
        SaveAccounts(fallback);
        return fallback;
    }

    public void SaveAccounts(AccountsStore store)
    {
        try
        {
            var json = JsonSerializer.Serialize(store, JsonOpts);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AccountsPath, encrypted);
        }
        catch (Exception ex)
        {
            Log($"SaveAccounts error: {ex.Message}");
        }
    }

    public Dictionary<string, InstanceMeta> LoadInstanceMetaMap()
    {
        try
        {
            if (File.Exists(FavoritesPath))
            {
                var json = File.ReadAllText(FavoritesPath, Encoding.UTF8);
                var map = JsonSerializer.Deserialize<Dictionary<string, InstanceMeta>>(json, JsonOpts);
                if (map != null) return map;
            }
        }
        catch (Exception ex)
        {
            Log($"LoadInstanceMetaMap error: {ex.Message}");
        }
        return new Dictionary<string, InstanceMeta>(StringComparer.OrdinalIgnoreCase);
    }

    public void SaveInstanceMetaMap(Dictionary<string, InstanceMeta> map)
    {
        try
        {
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(map, JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log($"SaveInstanceMetaMap error: {ex.Message}");
        }
    }
}
