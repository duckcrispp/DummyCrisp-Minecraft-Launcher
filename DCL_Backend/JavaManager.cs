using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DCL_Launcher;

public sealed class JavaManager
{
    private readonly StorageService _storage;
    private readonly HttpClient _http;
    public Func<HttpClient>? ResolveDownloadHttp { get; set; }
    private HttpClient DownloadHttp => ResolveDownloadHttp?.Invoke() ?? _http;

    public JavaManager(StorageService storage, HttpClient http)
    {
        _storage = storage;
        _http = http;
    }

    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "$SysReset", "$WinREAgent", "System Volume Information", "Recovery", "PerfLogs",
        "MSOCache", "Config.Msi", "Windows", "WinSxS", "Installer", "Package Cache", "ProgramData",
        "node_modules", ".git", ".hg", ".svn", "__pycache__", "venv", ".venv", ".nuget", "packages",
        "SteamLibrary", "steamapps", "Cache", "Code Cache", "GPUCache", "CrashDumps", "OneDriveTemp",
        "AppDataLocalLow", "Temp", "tmp", "lib", "jmods", "include", "legal", "conf", "man", "demo", "sample"
    };
    private static readonly HashSet<string> AllowedHiddenRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jdks", ".gradle", ".jabba", ".minecraft", ".scoop", ".sdkman"
    };
    private static readonly string[] VmFolders = ["server", "client", "j9vm", "default", "compressedrefs", ""];
    private string DiscoveryCachePath => Path.Combine(_storage.ConfigDir, "discovered-javas.json");

    public List<JavaInstallation> ScanLocalJavas(string? preferredActiveId = null)
    {
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddBin(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            string path = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return;
            try
            {
                string bin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                string? exe = FindExecutable(bin);
                if (exe == null) return;
                string resolved = Path.GetDirectoryName(File.ResolveLinkTarget(exe, true)?.FullName ?? exe)!;
                var home = Directory.GetParent(resolved);
                if (home?.Name.Equals("jre", StringComparison.OrdinalIgnoreCase) == true && home.Parent is { } parent)
                {
                    string jdkBin = Path.Combine(parent.FullName, "bin");
                    if (FindExecutable(jdkBin) != null) resolved = jdkBin;
                }
                if (!candidates.TryGetValue(resolved, out var aliases))
                    candidates.Add(resolved, aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                aliases.Add(bin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (string variable in new[] { "JAVA_HOME", "JDK_HOME", "JRE_HOME" })
            {
                string? home = Environment.GetEnvironmentVariable(variable, scope);
                if (!string.IsNullOrWhiteSpace(home))
                    AddBin(Path.Combine(home.Trim().Trim('"'), "bin"));
            }

            string path = Environment.GetEnvironmentVariable("PATH", scope) ?? "";
            foreach (string bin in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddBin(bin);
        }

        foreach (string bin in ReadCachedBins()) AddBin(bin);
        Parallel.ForEach(QuickSearchRoots().Distinct(StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 8) },
            root =>
            {
                foreach (string bin in FindBins(root, 4, CancellationToken.None))
                {
                    lock (candidates) AddBin(bin);
                }
            });

        string[] registryRoots =
        [
            @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE", @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Adoptium\JRE", @"SOFTWARE\AdoptOpenJDK\JDK", @"SOFTWARE\Azul Systems\Zulu",
            @"SOFTWARE\Microsoft\JDK", @"SOFTWARE\BellSoft\Liberica"
        ];
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                foreach (string keyPath in registryRoots)
                {
                    using var key = registry.OpenSubKey(keyPath);
                    if (key != null) ScanRegistryKey(key, 4, AddBin);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        var results = candidates.Select(candidate => ReadInstallation(candidate.Key, candidate.Value)).OfType<JavaInstallation>()
            .OrderByDescending(java => ParseMajor(java.Version))
            .ThenBy(java => java.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(java => java.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var active = results.FirstOrDefault(java => java.MatchesId(preferredActiveId)) ?? results.FirstOrDefault();
        if (active != null) active.Active = true;
        return results;
    }

    internal Task SearchLocalDrivesAsync(CancellationToken ct = default) =>
        SearchRootsAsync(DriveInfo.GetDrives().Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable &&
            drive.IsReady).Select(drive => drive.RootDirectory.FullName).ToArray(), ct);

    internal async Task SearchRootsAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var expandedRoots = new List<string>();
        foreach (string quick in QuickSearchRoots())
        {
            if (Directory.Exists(quick)) expandedRoots.Add(quick);
        }
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string top in Directory.EnumerateDirectories(root, "*", DirectoryOptions))
                {
                    string name = Path.GetFileName(top);
                    if (!ShouldSkipDirectory(name, top)) expandedRoots.Add(top);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                expandedRoots.Add(root);
            }
        }
        var uniqueRoots = expandedRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await Parallel.ForEachAsync(uniqueRoots,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 4, 8) },
            (root, token) =>
            {
                foreach (string bin in FindBins(root, 5, token))
                    if (ReadInstallation(bin, []) != null)
                        found.TryAdd(bin, 0);
                return ValueTask.CompletedTask;
            });
        ct.ThrowIfCancellationRequested();

        await SaveDiscoveryAsync(found.Keys, ct);
        _storage.Log($"本机 Java 扫描完成 · 检查 {roots.Count} 个目录根，发现 {found.Count} 个运行时入口");
    }

    private async Task SaveDiscoveryAsync(IEnumerable<string> bins, CancellationToken ct)
    {
        string tempPath = DiscoveryCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(bins.Order(StringComparer.OrdinalIgnoreCase)), ct);
            File.Move(tempPath, DiscoveryCachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private string[] ReadCachedBins()
    {
        try
        {
            return File.Exists(DiscoveryCachePath)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(DiscoveryCachePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _storage.Log($"读取 Java 扫描缓存失败：{ex.Message}");
            return [];
        }
    }

    private IEnumerable<string> QuickSearchRoots()
    {
        yield return _storage.RuntimeJavaDir;
        yield return Path.Combine(_storage.MinecraftDir, "runtime");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, ".jdks");
        yield return Path.Combine(profile, ".gradle", "jdks");
        yield return Path.Combine(profile, ".jabba", "jdk");

        string[] vendors = ["Java", "Eclipse Adoptium", "AdoptOpenJDK", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru"];
        foreach (string root in new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        }.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (string vendor in vendors)
            yield return Path.Combine(root, vendor);
    }

    private static bool ShouldSkipDirectory(string name, string fullPath)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (SkippedDirectories.Contains(name)) return true;
        if (name.StartsWith('.') && !AllowedHiddenRoots.Contains(name)) return true;
        if (fullPath.Contains(@"\AppData\Local\Google\", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(@"\AppData\Local\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static IEnumerable<string> FindBins(string root, int maxDepth, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((Path.TrimEndingDirectorySeparator(root), 0));
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            string dirName = Path.GetFileName(entry.Directory);
            if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                FindExecutable(entry.Directory) != null)
            {
                yield return entry.Directory;
                continue;
            }
            string directBin = Path.Combine(entry.Directory, "bin");
            if (File.Exists(Path.Combine(entry.Directory, "release")) && FindExecutable(directBin) != null)
            {
                yield return directBin;
                continue;
            }
            if (entry.Depth >= maxDepth) continue;
            try
            {
                foreach (string child in Directory.EnumerateDirectories(entry.Directory, "*", DirectoryOptions))
                {
                    string childName = Path.GetFileName(child);
                    if (ShouldSkipDirectory(childName, child)) continue;
                    pending.Push((child, entry.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ScanRegistryKey(RegistryKey key, int depth, Action<string?> addBin)
    {
        foreach (string valueName in new[] { "JavaHome", "Path", "InstallationPath", "InstallDir" })
        {
            if (key.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
            {
                addBin(path);
                addBin(Path.Combine(path.Trim().Trim('"'), "bin"));
            }
        }
        if (depth == 0) return;
        foreach (string name in key.GetSubKeyNames())
        {
            try
            {
                using var child = key.OpenSubKey(name);
                if (child != null) ScanRegistryKey(child, depth - 1, addBin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private static string? FindExecutable(string bin)
    {
        string java = Path.Combine(bin, "java.exe");
        if (File.Exists(java)) return java;
        string javaw = Path.Combine(bin, "javaw.exe");
        return File.Exists(javaw) ? javaw : null;
    }

    private static JavaInstallation? ReadInstallation(string bin, IEnumerable<string> aliases)
    {
        try
        {
            string? exe = FindExecutable(bin);
            if (exe == null) return null;
            var home = Directory.GetParent(bin);
            if (home == null) return null;
            string legacyBin = Path.Combine(home.FullName, "jre", "bin");
            string? vm = VmFolders.Select(folder => Path.Combine(bin, folder, "jvm.dll")).FirstOrDefault(File.Exists)
                ?? VmFolders.Select(folder => Path.Combine(legacyBin, folder, "jvm.dll")).FirstOrDefault(File.Exists);
            if (vm == null) return null;

            using var stream = File.OpenRead(exe);
            using var pe = new PEReader(stream);
            string? arch = pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => "x64",
                Machine.I386 => "x86",
                Machine.Arm64 => "arm64",
                _ => null
            };
            if (arch == null) return null;

            string releasePath = Path.Combine(home.FullName, "release");
            string? version = null, runtimeVersion = null, implementor = null;
            if (File.Exists(releasePath) && new FileInfo(releasePath).Length <= 65536)
            {
                foreach (string line in File.ReadLines(releasePath))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        version = line["JAVA_VERSION=".Length..].Trim().Trim('"');
                    else if (line.StartsWith("JAVA_RUNTIME_VERSION=", StringComparison.Ordinal))
                        runtimeVersion = line["JAVA_RUNTIME_VERSION=".Length..].Trim().Trim('"');
                    else if (line.StartsWith("IMPLEMENTOR=", StringComparison.Ordinal))
                        implementor = line["IMPLEMENTOR=".Length..].Trim().Trim('"');
                }
            }

            FileVersionInfo? metadata = null;
            if (version == null || ParseMajor(version) <= 0)
            {
                metadata = FileVersionInfo.GetVersionInfo(exe);
                version = new[] { version, runtimeVersion, metadata.ProductVersion, metadata.FileVersion }
                    .FirstOrDefault(value => value != null && ParseMajor(value) > 0);
            }
            if (version == null)
            {
                var vmMetadata = FileVersionInfo.GetVersionInfo(vm);
                version = new[] { vmMetadata.ProductVersion, vmMetadata.FileVersion }
                    .FirstOrDefault(value => value != null && ParseMajor(value) > 0);
            }
            if (version == null) return null;

            string vendor = implementor ?? (metadata ??= FileVersionInfo.GetVersionInfo(exe)).CompanyName ?? "";
            if (vendor.Contains("Adoptium", StringComparison.OrdinalIgnoreCase)) vendor = "Eclipse Temurin";
            else if (vendor.Contains("Azul", StringComparison.OrdinalIgnoreCase)) vendor = "Azul Zulu";
            else if (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) vendor = "Microsoft OpenJDK";
            else if (vendor.Contains("Oracle", StringComparison.OrdinalIgnoreCase)) vendor = "Oracle";
            else if (string.IsNullOrWhiteSpace(vendor)) vendor = "OpenJDK";

            return new JavaInstallation
            {
                Id = JavaId(exe), Version = version, Vendor = vendor, Arch = arch, Path = exe,
                LegacyIds = aliases.Append(bin).SelectMany(path =>
                    new[] { JavaId(Path.Combine(path, "java.exe")), JavaId(Path.Combine(path, "javaw.exe")) }).Distinct().ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string JavaId(string path) =>
        $"java-{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path)))[..8].ToLowerInvariant()}";

    public static int ParseMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var clean = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version;
        var numStr = new string(clean.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(numStr, out int m) ? m : 0;
    }

    public sealed record JavaPackage(string Url, string FileName, string Sha256, long? Size = null);

    private static string Digest(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(?:SHA256\s*\([^)]+\)\s*=\s*)?([0-9a-fA-F]{64})(?:\s|$)");
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException("发布方未提供有效的 SHA-256");
    }

    public async Task<JavaPackage> ResolveAdoptiumDownloadAsync(int featureVersion, CancellationToken ct)
    {
        string api = $"https://api.adoptium.net/v3/assets/latest/{featureVersion}/hotspot?os=windows&architecture=x64&image_type=jdk";
        using var req = new HttpRequestMessage(HttpMethod.Get, api);
        req.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
        using var resp = await DownloadHttp.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        var pkg = first.GetProperty("binary").GetProperty("package");
        return new(pkg.GetProperty("link").GetString()!, pkg.GetProperty("name").GetString()!,
            Digest(pkg.GetProperty("checksum").GetString() ?? ""), pkg.TryGetProperty("size", out var size) ? size.GetInt64() : null);
    }

    public async Task<JavaPackage> ResolveVendorJavaDownloadAsync(string vendorId, int featureVersion, CancellationToken ct)
    {
        string v = (vendorId ?? "temurin").Trim().ToLowerInvariant();
        if (v == "zulu")
        {
            string api = $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={featureVersion}&os=windows&arch=x86&hw_bitness=64&archive_type=zip&java_package_type=jdk&javafx_bundled=false&release_status=ga&availability_types=CA&page=1&page_size=25";
            using var req = new HttpRequestMessage(HttpMethod.Get, api);
            req.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
            using var resp = await DownloadHttp.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var first = doc.RootElement.EnumerateArray().FirstOrDefault(p => p.TryGetProperty("name", out var n) &&
                !(n.GetString() ?? "").Contains("-crac-", StringComparison.OrdinalIgnoreCase));
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("download_url", out var dlEl))
            {
                string dlUrl = dlEl.GetString() ?? "";
                string fileName = first.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? $"zulu-{featureVersion}.zip") : $"zulu-{featureVersion}.zip";
                if (!string.IsNullOrWhiteSpace(dlUrl))
                {
                    string uuid = first.GetProperty("package_uuid").GetString()!;
                    using var detail = JsonDocument.Parse(await DownloadHttp.GetStringAsync("https://api.azul.com/metadata/v1/zulu/packages/" + Guid.Parse(uuid), ct));
                    return new(dlUrl, fileName, Digest(detail.RootElement.GetProperty("sha256_hash").GetString()!));
                }
            }
            throw new InvalidOperationException($"Azul Zulu 未找到 Java {featureVersion} Windows x64 安装包");
        }
        if (v == "microsoft")
        {
            if (featureVersion is not (11 or 17 or 21 or 25))
                throw new InvalidOperationException($"Microsoft OpenJDK 仅提供 Java 11 / 17 / 21 / 25 LTS 版本");
            string page = await DownloadHttp.GetStringAsync("https://learn.microsoft.com/en-us/java/openjdk/download", ct);
            var match = System.Text.RegularExpressions.Regex.Match(page,
                $@"https://aka\.ms/download-jdk/(microsoft-jdk-{featureVersion}(?:\.[0-9]+)+-windows-x64\.zip)\.sha256sum\.txt",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidDataException("未找到 Microsoft 发布的 Windows x64 SHA-256");
            string name = match.Groups[1].Value;
            return new("https://aka.ms/download-jdk/" + name, name, Digest(await DownloadHttp.GetStringAsync(match.Value, ct)));
        }
        if (v == "oracle")
        {
            string url = $"https://download.oracle.com/java/{featureVersion}/latest/jdk-{featureVersion}_windows-x64_bin.zip";
            try
            {
                return new(url, $"oracle-jdk-{featureVersion}_windows-x64_bin.zip", Digest(await DownloadHttp.GetStringAsync(url + ".sha256", ct)));
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException($"Oracle 未公开此 Java {featureVersion} 的免登录校验下载。可在 Oracle 官方安装后扫描本机 Java，或在当前页选择同版本 Temurin。", ex);
            }
        }
        return await ResolveAdoptiumDownloadAsync(featureVersion, ct);
    }

    public async Task<string> ExtractJavaArchiveAsync(string zipPath, int featureVersion, CancellationToken ct, string? baseInstallDir = null)
    {
        string parentRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(baseInstallDir) ? _storage.RuntimeJavaDir : baseInstallDir);
        Directory.CreateDirectory(parentRoot);
        string targetRoot = Path.Combine(parentRoot, $"jdk-{featureVersion}");
        string backup = targetRoot + ".dcl-previous";
        string temp = Path.Combine(parentRoot, ".dcl-java-" + Guid.NewGuid().ToString("N"));
        using var installLock = new FileStream(targetRoot + ".dcl-lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (!Directory.Exists(targetRoot) && Directory.Exists(backup)) Directory.Move(backup, targetRoot);
        Directory.CreateDirectory(temp);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, temp), ct);
            ct.ThrowIfCancellationRequested();
            var roots = Directory.GetDirectories(temp);
            string extracted = Directory.Exists(Path.Combine(temp, "bin")) ? temp : roots.Length == 1 ? roots[0] : temp;
            string bin = Path.Combine(extracted, "bin");
            var installation = ReadInstallation(bin, []);
            if (!File.Exists(Path.Combine(bin, "java.exe")) || installation?.Arch != "x64" || ParseMajor(installation.Version) != featureVersion)
                throw new InvalidDataException($"安装包缺少可用的 Windows x64 Java {featureVersion}");
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (Directory.Exists(targetRoot)) Directory.Move(targetRoot, backup);
            try { Directory.Move(extracted, targetRoot); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(targetRoot)) Directory.Move(backup, targetRoot);
                throw;
            }
            try
            {
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                var bins = ReadCachedBins().Append(Path.Combine(targetRoot, "bin")).Distinct(StringComparer.OrdinalIgnoreCase);
                await SaveDiscoveryAsync(bins, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _storage.Log($"Java 安装后的缓存清理：{ex.Message}"); }
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _storage.Log($"Java 临时目录清理：{ex.Message}"); }
        }
        return FindExecutable(Path.Combine(targetRoot, "bin"))!;
    }
}
