using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

public sealed class InstanceManager
{
    private sealed record ModArchive(string ModId, string Name, string Version, string Desc, string ChineseName, string Icon);
    private const string DefaultModIcon = "assets/mc-icons/cobblestone.png";
    private static readonly Dictionary<string, string> BundledModIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iris"] = "iris", ["sodium"] = "sodium", ["lithium"] = "lithium", ["create"] = "create",
        ["ferritecore"] = "ferrite-core", ["jei"] = "jei", ["xaerominimap"] = "xaeros-minimap"
    };

    private readonly StorageService _storage;
    private readonly ConcurrentDictionary<string, (DateTime Modified, long Size, ModArchive Archive)> _modMetadata = new(StringComparer.OrdinalIgnoreCase);

    public InstanceManager(StorageService storage)
    {
        _storage = storage;
    }

    public List<InstanceMeta> ScanVersions(DclConfig cfg, List<JavaInstallation> javas, int totalRamMb, int availRamMb)
    {
        var metaMap = _storage.LoadInstanceMetaMap();
        _storage.SetExternalInstancePaths(metaMap.Values);
        var results = new List<InstanceMeta>();
        var scannedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_storage.VersionsDir))
        {
            foreach (var dir in Directory.GetDirectories(_storage.VersionsDir))
            {
                string id = Path.GetFileName(dir);
                string? jsonPath = ResolveVersionJsonPath(dir, id);
                if (jsonPath == null) continue;

                metaMap.TryGetValue(id, out var saved);
                var inst = BuildScannedInstance(id, dir, jsonPath, saved, cfg, javas, totalRamMb, availRamMb, null, null);
                metaMap[id] = inst;
                results.Add(inst);
                scannedIds.Add(id);
            }
        }

        // Also scan registered external (linked, zero-copy) version folders
        var staleExternalIds = new List<string>();
        foreach (var (id, saved) in metaMap.ToList())
        {
            if (scannedIds.Contains(id) || string.IsNullOrWhiteSpace(saved.ExternalPath)) continue;
            string extDir = saved.ExternalPath;
            string? jsonPath = Directory.Exists(extDir) ? ResolveVersionJsonPath(extDir, id) : null;
            if (jsonPath == null)
            {
                staleExternalIds.Add(id);
                _storage.UnregisterExternalInstancePath(id);
                continue;
            }
            var inst = BuildScannedInstance(id, extDir, jsonPath, saved, cfg, javas, totalRamMb, availRamMb, extDir, saved.ExternalMinecraftDir);
            metaMap[id] = inst;
            results.Add(inst);
            scannedIds.Add(id);
        }
        foreach (var staleId in staleExternalIds) metaMap.Remove(staleId);

        _storage.SetExternalInstancePaths(results);
        _storage.SaveInstanceMetaMap(metaMap);
        return results;
    }

    private InstanceMeta BuildScannedInstance(
        string id,
        string dir,
        string jsonPath,
        InstanceMeta? saved,
        DclConfig cfg,
        List<JavaInstallation> javas,
        int totalRamMb,
        int availRamMb,
        string? externalPath,
        string? externalMcDir)
    {
        string baseName = Path.GetFileNameWithoutExtension(jsonPath);
        var overlay = Path.Combine(dir, $"{baseName}.loader.json");
        if (!File.Exists(overlay)) overlay = Path.Combine(dir, $"{id}.loader.json");
        var fabricProfile = Path.Combine(dir, $"{baseName}.fabric.json");
        if (!File.Exists(fabricProfile)) fabricProfile = Path.Combine(dir, $"{id}.fabric.json");
        var profile = File.Exists(overlay) ? overlay : File.Exists(fabricProfile) ? fabricProfile : jsonPath;
        var (mcVer, loader, loaderVer) = DetectVersionMetadata(profile, baseName);
        var pack = ReadInstalledPack(dir);
        if (pack != null) (mcVer, loader, loaderVer) = (pack.McVersion, pack.Loader, pack.LoaderVersion);
        int modCount = CountEnabledMods(id);
        int shaderCount = CountShaders(id);
        bool isModpack = pack != null || saved?.IsModpack == true;

        var bestJava = pack?.JavaMajor is > 0
            ? javas.FirstOrDefault(java => java.MatchesId(saved?.JavaId) && JavaManager.ParseMajor(java.Version) == pack.JavaMajor)
                ?? javas.FirstOrDefault(java => JavaManager.ParseMajor(java.Version) == pack.JavaMajor)
            : PickJavaForMcVersion(javas, mcVer, saved?.JavaId);
        int autoMem = SystemMemoryHelper.ComputeRecommendedMemoryMb(totalRamMb, availRamMb, mcVer, loader, modCount, isModpack, shaderCount);

        var lastPlayed = ReadLastPlayed(dir, saved?.LastPlayedAt);
        bool useGlobal = saved?.UseGlobalSettings ?? true;
        bool autoMemory = saved?.AutoMemory ?? true;
        string recommendedJvm = ComputeRecommendedJvmArgs(mcVer, bestJava);
        string resolvedJvm = !string.IsNullOrWhiteSpace(saved?.JvmArgs) ? saved!.JvmArgs : recommendedJvm;
        // 若首次识别/导入/下载的新版本最优 GC 与全局默认不兼容（如 Java 17/8 版本遇上全局 Gen-ZGC），自动以最优 GC 落地
        if (saved == null && useGlobal && !IsJvmArgsCompatibleWithJava(cfg.CustomJvmArgs, bestJava, mcVer))
        {
            useGlobal = false;
        }
        return new InstanceMeta
        {
            Id = id,
            Name = saved?.Name is { Length: > 0 } ? saved.Name : pack?.Name ?? id,
            McVersion = mcVer,
            Loader = loader,
            LoaderVersion = loaderVer,
            JavaId = bestJava?.Id ?? "",
            JavaVersion = bestJava != null ? FormatJavaLabel(bestJava.Version) : "未检测到 Java",
            JvmArgs = resolvedJvm,
            IconId = !string.IsNullOrWhiteSpace(saved?.IconId) ? saved!.IconId : "grass",
            CustomIcon = saved?.CustomIcon,
            IsModpack = isModpack,
            RequiredJavaMajor = pack?.JavaMajor ?? 0,
            FolderColor = !string.IsNullOrWhiteSpace(saved?.FolderColor) ? saved!.FolderColor : PickColorForLoader(loader),
            CustomFolderHex = saved?.CustomFolderHex,
            Favorite = saved?.Favorite ?? false,
            ModsCount = loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase) ? 0 : modCount,
            LastPlayedAt = lastPlayed,
            LastPlayed = lastPlayed?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未游玩",
            Isolated = true,
            UseGlobalSettings = useGlobal,
            AutoMemory = autoMemory,
            MemoryMb = (!useGlobal && !autoMemory && saved is { MemoryMb: >= 1024 }) ? saved.MemoryMb : autoMem,
            Tagline = $"Minecraft {mcVer} · {loader}",
            ExternalPath = externalPath,
            ExternalMinecraftDir = externalMcDir
        };
    }

    internal const string PresetGenZgc = "-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8";
    internal const string PresetG1Gc = "-XX:+UseG1GC -XX:MaxGCPauseMillis=30 -Dfile.encoding=UTF-8";
    internal const string PresetShenandoah = "-XX:+UseShenandoahGC -Dfile.encoding=UTF-8";

    internal static int RequiredJavaMajorForMc(string mcVersion)
    {
        var m = Regex.Match(mcVersion ?? "", @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return 21;
        int major = int.Parse(m.Groups[1].Value);
        int minor = int.Parse(m.Groups[2].Value);
        int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (major >= 26) return 21;
        if (major == 1 && (minor >= 21 || (minor == 20 && patch >= 5))) return 21;
        if (major == 1 && minor >= 18) return 17;
        if (major == 1 && minor == 17) return 16;
        return 8;
    }

    internal static string ComputeRecommendedJvmArgs(string mcVersion, JavaInstallation? java)
    {
        int javaMajor = java != null ? JavaManager.ParseMajor(java.Version) : RequiredJavaMajorForMc(mcVersion);
        if (javaMajor <= 0) javaMajor = RequiredJavaMajorForMc(mcVersion);
        return javaMajor >= 21 ? PresetGenZgc : PresetG1Gc;
    }

    internal static bool IsJvmArgsCompatibleWithJava(string? jvmArgs, JavaInstallation? java, string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(jvmArgs)) return true;
        int javaMajor = java != null ? JavaManager.ParseMajor(java.Version) : RequiredJavaMajorForMc(mcVersion);
        if (javaMajor <= 0) javaMajor = RequiredJavaMajorForMc(mcVersion);
        if (javaMajor < 21 && (jvmArgs.Contains("ZGenerational", StringComparison.OrdinalIgnoreCase) || jvmArgs.Contains("UseZGC", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (javaMajor < 12 && jvmArgs.Contains("UseShenandoahGC", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    internal static string ResolveEffectiveJvmArgs(InstanceMeta inst, JavaInstallation? java, DclConfig cfg)
    {
        string raw = inst.UseGlobalSettings ? cfg.CustomJvmArgs : (string.IsNullOrWhiteSpace(inst.JvmArgs) ? cfg.CustomJvmArgs : inst.JvmArgs);
        if (!IsJvmArgsCompatibleWithJava(raw, java, inst.McVersion))
            return ComputeRecommendedJvmArgs(inst.McVersion, java);
        return raw;
    }

    internal static string? ResolveVersionJsonPath(string dir, string id)
    {
        if (!Directory.Exists(dir)) return null;
        string byId = Path.Combine(dir, $"{id}.json");
        if (File.Exists(byId)) return byId;
        string dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string byDir = Path.Combine(dir, $"{dirName}.json");
        if (File.Exists(byDir)) return byDir;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            string file = Path.GetFileName(path);
            if (file.Equals("pack.json", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("instance.json", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("launcher_profiles.json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    (doc.RootElement.TryGetProperty("libraries", out _) ||
                     doc.RootElement.TryGetProperty("inheritsFrom", out _) ||
                     doc.RootElement.TryGetProperty("mainClass", out _) ||
                     doc.RootElement.TryGetProperty("assetIndex", out _)))
                {
                    return path;
                }
            }
            catch { }
        }
        return null;
    }

    public int CountEnabledMods(string instanceId)
    {
        string dir = _storage.ResolveResourceDirectory(instanceId, "mods");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jar").Count() : 0;
    }

    public int CountShaders(string instanceId)
    {
        string dir = _storage.ResolveResourceDirectory(instanceId, "shaderpacks");
        return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Count() : 0;
    }

    public List<ModEntry> ScanMods(string instanceId)
    {
        var list = new List<ModEntry>();
        string dir = _storage.ResolveResourceDirectory(instanceId, "mods");
        if (!Directory.Exists(dir)) return list;

        var files = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            string fileName = Path.GetFileName(file);
            bool enabled = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            string baseFile = enabled ? fileName : fileName[..^".disabled".Length];
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            if (!_modMetadata.TryGetValue(file, out var metadata) || metadata.Modified != info.LastWriteTimeUtc || metadata.Size != info.Length)
            {
                metadata = (info.LastWriteTimeUtc, info.Length, ReadModArchiveInfo(file, baseFile));
                _modMetadata[file] = metadata;
            }

            list.Add(new ModEntry
            {
                Id = baseFile,
                ModId = metadata.Archive.ModId,
                Name = metadata.Archive.Name,
                CnName = metadata.Archive.ChineseName,
                Pinyin = PinyinForName(metadata.Archive.ChineseName),
                Version = metadata.Archive.Version,
                File = baseFile,
                Icon = metadata.Archive.Icon,
                Desc = string.IsNullOrWhiteSpace(metadata.Archive.Desc) ? baseFile : metadata.Archive.Desc,
                Enabled = enabled,
                HasUpdate = false,
                Latest = metadata.Archive.Version
            });
        }
        return list;
    }

    public ModEntry? ToggleMod(string instanceId, string fileName)
    {
        if (!IsFileName(fileName) || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return null;
        string jarPath = Path.Combine(_storage.ResolveResourceDirectory(instanceId, "mods"), fileName);
        string disabledPath = jarPath + ".disabled";
        if (File.Exists(jarPath))
        {
            File.Move(jarPath, disabledPath, true);
        }
        else if (File.Exists(disabledPath))
        {
            File.Move(disabledPath, jarPath, true);
        }
        return ScanMods(instanceId).FirstOrDefault(m => m.Id.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    public bool DeleteMod(string instanceId, string fileName)
    {
        if (!IsFileName(fileName) || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
        string jarPath = Path.Combine(_storage.ResolveResourceDirectory(instanceId, "mods"), fileName);
        string disabledPath = jarPath + ".disabled";
        if (File.Exists(jarPath)) { File.Delete(jarPath); _modMetadata.TryRemove(jarPath, out _); return true; }
        if (File.Exists(disabledPath)) { File.Delete(disabledPath); _modMetadata.TryRemove(disabledPath, out _); return true; }
        return false;
    }

    internal static bool IsFileName(string name) => name.Length > 0 && name != "." && name != ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    public List<PackEntry> ScanPacks(string instanceId)
    {
        var list = new List<PackEntry>();
        ScanPackDir(_storage.ResolveResourceDirectory(instanceId, "resourcepacks"), "资源包", "assets/mc-icons/grass.png", list);
        ScanPackDir(_storage.ResolveResourceDirectory(instanceId, "shaderpacks"), "光影包", "assets/mc-icons/redstone.png", list);
        return list;
    }

    private static void ScanPackDir(string dir, string kind, string icon, List<PackEntry> list)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.GetFileSystemEntries(dir))
        {
            string fileName = Path.GetFileName(entry);
            bool disabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            string baseName = disabled ? fileName[..^".disabled".Length] : fileName;
            if (!Directory.Exists(entry) && !baseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            string displayName = Path.GetFileNameWithoutExtension(baseName);
            list.Add(new PackEntry
            {
                Id = $"{kind}:{baseName}",
                Kind = kind,
                Name = displayName,
                FileName = baseName,
                Icon = icon,
                Spec = Directory.Exists(entry) ? "文件夹" : "ZIP 压缩包",
                Enabled = !disabled
            });
        }
    }

    public PackEntry? TogglePack(string instanceId, string id)
    {
        int idx = id.IndexOf(':');
        if (idx <= 0) return null;
        string kind = id[..idx];
        string baseName = id[(idx + 1)..];
        if (!IsFileName(baseName) || kind is not ("资源包" or "光影包")) return null;
        string dir = _storage.ResolveResourceDirectory(instanceId, kind == "光影包" ? "shaderpacks" : "resourcepacks");
        string enabledPath = Path.Combine(dir, baseName);
        string disabledPath = enabledPath + ".disabled";
        if (File.Exists(enabledPath)) File.Move(enabledPath, disabledPath, true);
        else if (File.Exists(disabledPath)) File.Move(disabledPath, enabledPath, true);
        else if (Directory.Exists(enabledPath)) Directory.Move(enabledPath, disabledPath);
        else if (Directory.Exists(disabledPath)) Directory.Move(disabledPath, enabledPath);
        return ScanPacks(instanceId).FirstOrDefault(p => p.Id == id);
    }

    public static JavaInstallation? PickJavaForMcVersion(List<JavaInstallation> javas, string mcVer, string? preferredId)
    {
        if (javas.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            var found = javas.FirstOrDefault(j => j.MatchesId(preferredId));
            if (found != null) return found;
        }
        int requiredMajor = RequiredJavaForMcVersion(mcVer);
        return javas.OrderBy(java => Math.Abs(JavaManager.ParseMajor(java.Version) - requiredMajor))
            .ThenByDescending(java => JavaManager.ParseMajor(java.Version) >= requiredMajor).FirstOrDefault();
    }

    internal static int RequiredJavaForMcVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], out int minor)) return 21;
        if (minor >= 21 || (minor == 20 && parts.Length >= 3 && int.TryParse(parts[2], out int patch) && patch >= 5)) return 21;
        if (minor >= 18) return 17;
        return minor == 17 ? 16 : 8;
    }

    private static InstalledPack? ReadInstalledPack(string directory)
    {
        var file = Path.Combine(directory, InstalledPack.FileName);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<InstalledPack>(File.ReadAllText(file), StorageService.JsonOpts); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public static string FormatJavaLabel(string version) =>
        version.StartsWith("1.8", StringComparison.Ordinal) ? $"Java 8u{version.Split('_').LastOrDefault()}" : $"Java {version}";

    private static string PickColorForLoader(string loader) => loader switch
    {
        "NeoForge" => "amber",
        "Fabric" => "glacier",
        "Forge" => "emerald",
        _ => "amber"
    };

    private static DateTimeOffset? ReadLastPlayed(string directory, DateTimeOffset? recorded)
    {
        // Earlier launchers did not persist a play timestamp. Use the game's log when available.
        if (recorded.HasValue) return recorded;
        string log = Path.Combine(directory, "logs", "latest.log");
        return File.Exists(log) ? File.GetLastWriteTimeUtc(log) : null;
    }

    internal static (string McVersion, string Loader, string LoaderVersion) DetectVersionMetadata(string jsonPath, string fallbackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            string? ExtractGameArg(string flag)
            {
                if (!root.TryGetProperty("arguments", out var argsEl) || !argsEl.TryGetProperty("game", out var gameEl) ||
                    gameEl.ValueKind != JsonValueKind.Array)
                    return null;
                bool nextIsVal = false;
                foreach (var item in gameEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) { nextIsVal = false; continue; }
                    string val = item.GetString() ?? "";
                    if (nextIsVal && val.Length > 0) return val;
                    nextIsVal = val.Equals(flag, StringComparison.OrdinalIgnoreCase);
                }
                return null;
            }

            string? mcVer = null;
            if (root.TryGetProperty("inheritsFrom", out var inh) && inh.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(inh.GetString()))
                mcVer = inh.GetString();
            else if (root.TryGetProperty("clientVersion", out var cv) && cv.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cv.GetString()))
                mcVer = cv.GetString();
            else if (ExtractGameArg("--fml.mcVersion") is { Length: > 0 } fmlMc)
                mcVer = fmlMc;
            else if (root.TryGetProperty("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in patches.EnumerateArray())
                {
                    if (p.TryGetProperty("id", out var pid) && pid.GetString() == "game" &&
                        p.TryGetProperty("version", out var pv) && !string.IsNullOrWhiteSpace(pv.GetString()))
                    {
                        mcVer = pv.GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(mcVer))
            {
                string rawId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? fallbackId : fallbackId;
                var leadVer = Regex.Match(rawId, @"^(\d+\.\d+(?:\.\d+)?)(?=[-_\s]+(?:forge|neoforge|fabric|quilt))", RegexOptions.IgnoreCase);
                mcVer = leadVer.Success ? leadVer.Groups[1].Value : rawId;
            }

            string rawJson = root.GetRawText();
            if (rawJson.Contains("neoforged", StringComparison.OrdinalIgnoreCase))
            {
                string? nfArg = ExtractGameArg("--fml.neoForgeVersion");
                var m = Regex.Match(rawJson, @"neoforge[-:](\d+\.\d+\.\d+[^\s""]*)");
                return (mcVer, "NeoForge", !string.IsNullOrWhiteSpace(nfArg) ? nfArg : m.Success ? m.Groups[1].Value : "已安装");
            }
            if (rawJson.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            {
                string? fArg = ExtractGameArg("--fml.forgeVersion");
                var m = Regex.Match(rawJson, @"forge-[\d\.]+-(\d+\.\d+\.\d+)");
                return (mcVer, "Forge", !string.IsNullOrWhiteSpace(fArg) ? fArg : m.Success ? m.Groups[1].Value : "已安装");
            }
            if (rawJson.Contains("org.quiltmc:quilt-loader", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(rawJson, @"quilt-loader:([^""\s]+)");
                return (mcVer, "Quilt", m.Success ? m.Groups[1].Value : "已安装");
            }
            if (rawJson.Contains("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(rawJson, @"fabric-loader:([^""\s]+)");
                return (mcVer, "Fabric", m.Success ? m.Groups[1].Value : "已安装");
            }
            return (mcVer, "Vanilla", "原版");
        }
        catch
        {
            return (fallbackId, "Vanilla", "原版");
        }
    }

    private ModArchive ReadModArchiveInfo(string archivePath, string fallbackFile)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(fallbackFile);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.GetEntry("fabric.mod.json") is { } fabric)
            {
                using var content = fabric.Open();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                return DescribeMod(zip, Text(root, "id"), Text(root, "name", fallbackName), Text(root, "version", "1.0"),
                    Text(root, "description"), DeclaredJsonIcon(root));
            }

            if (zip.GetEntry("quilt.mod.json") is { } quilt)
            {
                using var content = quilt.Open();
                using var doc = JsonDocument.Parse(content);
                var loader = doc.RootElement.GetProperty("quilt_loader");
                var meta = loader.GetProperty("metadata");
                return DescribeMod(zip, Text(loader, "id"), Text(meta, "name", fallbackName), Text(loader, "version", "1.0"),
                    Text(meta, "description"), DeclaredJsonIcon(meta));
            }

            var tomlEntry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (tomlEntry != null)
            {
                using var reader = new StreamReader(tomlEntry.Open());
                string toml = reader.ReadToEnd();
                string id = ReadTomlString(toml, "modId");
                string name = ReadTomlString(toml, "displayName");
                string version = ReadTomlString(toml, "version");
                string desc = Regex.Match(toml, @"(?m)^\s*description\s*=\s*'''([\s\S]*?)'''").Groups[1].Value;
                return DescribeMod(zip, id, name.Length > 0 ? name : fallbackName,
                    version.Length > 0 && !version.Contains("${") ? version : "1.0",
                    desc.Trim().Replace('\n', ' '), ReadTomlString(toml, "logoFile"));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // Broken or unsupported archives can still be managed by file name.
        }
        return new(fallbackName, fallbackName, "1.0", fallbackFile, "", DefaultModIcon);
    }

    private static string ReadTomlString(string toml, string key) =>
        Regex.Match(toml, "(?m)^\\s*" + key + "\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']").Groups[1].Value;

    private static string DeclaredJsonIcon(JsonElement meta)
    {
        if (!meta.TryGetProperty("icon", out var icon)) return "";
        if (icon.ValueKind == JsonValueKind.String) return icon.GetString() ?? "";
        if (icon.ValueKind != JsonValueKind.Object) return "";
        return icon.EnumerateObject()
            .Where(entry => entry.Value.ValueKind == JsonValueKind.String && int.TryParse(entry.Name, out _))
            .OrderBy(entry => Math.Abs(int.Parse(entry.Name) - 64L))
            .FirstOrDefault().Value is { ValueKind: JsonValueKind.String } best ? best.GetString() ?? "" : "";
    }

    private ModArchive DescribeMod(ZipArchive zip, string id, string name, string version, string description, string icon)
    {
        if (description.Length > 80) description = description[..80];
        string chineseName = ReadDeclaredChineseName(zip, id);
        if (chineseName.Length == 0) chineseName = ChineseName("mods", id);
        string localIcon = CacheArchiveIcon(zip, icon, id);
        if (localIcon.Length == 0)
            localIcon = BundledModIcons.TryGetValue(id, out var asset) ? $"assets/mod-icons/{asset}.webp" : DefaultModIcon;
        return new(id, name, version, description, chineseName, localIcon);
    }

    private static string ReadDeclaredChineseName(ZipArchive zip, string id)
    {
        if (zip.GetEntry($"assets/{id}/lang/zh_cn.json") is not { Length: > 0 and <= 1048576 } entry) return "";
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            string name = Text(doc.RootElement, $"modmenu.nameTranslation.{id}", Text(doc.RootElement, $"mod.{id}.name"));
            return name.Any(c => c >= '\u3400' && c <= '\u9fff') ? name : "";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return "";
        }
    }

    private string CacheArchiveIcon(ZipArchive zip, string declared, string id)
    {
        var entry = declared.Length > 0 ? zip.GetEntry(declared.TrimStart('/')) : null;
        entry ??= zip.GetEntry($"assets/{id}/icon.png");
        if (entry is not { Length: >= 24 and <= 524288 } || !entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            using var stream = entry.Open();
            var data = new byte[(int)entry.Length];
            stream.ReadExactly(data);
            if (!data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "";
            int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
            if (width is < 1 or > 2048 || height is < 1 or > 2048) return "";
            return _storage.CacheModIcon(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
