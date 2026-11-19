using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DCL_Launcher;

public sealed class GameLauncher
{
    private readonly StorageService _storage;

    private readonly MinecraftApiService _minecraft;

    public GameLauncher(StorageService storage, MinecraftApiService minecraft)
    {
        _storage = storage;
        _minecraft = minecraft;
    }

    public async Task<(bool Ok, string Message, int ExitCode, List<string> CrashLines)> LaunchAsync(
        InstanceMeta inst,
        JavaInstallation java,
        AccountEntry? account,
        int memoryMb,
        string customJvmArgs,
        Action<int>? onProgress,
        CancellationToken ct,
        CancellationToken preSpawnCt = default)
    {
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(ct, preSpawnCt);
        var setupCt = setupCts.Token;
        string verDir = _storage.GameDirectory(inst.Id);
        string dirName = Path.GetFileName(verDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string verJsonPath = Path.Combine(verDir, $"{inst.Id}.json");
        string verBaseName = inst.Id;
        if (!File.Exists(verJsonPath))
        {
            string byDir = Path.Combine(verDir, $"{dirName}.json");
            if (File.Exists(byDir))
            {
                verJsonPath = byDir;
                verBaseName = dirName;
            }
        }
        string verJarPath = Path.Combine(verDir, $"{verBaseName}.jar");
        if (!File.Exists(verJarPath))
        {
            string altJar = Path.Combine(verDir, $"{inst.Id}.jar");
            if (File.Exists(altJar)) verJarPath = altJar;
        }

        if (!File.Exists(verJsonPath))
        {
            return (false, $"找不到版本配置：{verJsonPath}", -1, new());
        }
        if (!File.Exists(java.Path))
        {
            return (false, $"Java 可执行文件不存在：{java.Path}", -1, new());
        }

        onProgress?.Invoke(22);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(verJsonPath, setupCt));
        var root = doc.RootElement;
        JsonDocument? parentDoc = null;
        string? parentJarPath = null;
        if (root.TryGetProperty("inheritsFrom", out var inhEl) && inhEl.ValueKind == JsonValueKind.String)
        {
            string parentId = inhEl.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(parentId))
            {
                string? siblingVersionsDir = Path.GetDirectoryName(verDir);
                string[] parentDirs = siblingVersionsDir != null
                    ? [Path.Combine(siblingVersionsDir, parentId), Path.Combine(_storage.VersionsDir, parentId)]
                    : [Path.Combine(_storage.VersionsDir, parentId)];
                foreach (string pDir in parentDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string pJson = Path.Combine(pDir, $"{parentId}.json");
                    if (File.Exists(pJson))
                    {
                        parentDoc = JsonDocument.Parse(await File.ReadAllTextAsync(pJson, setupCt));
                        string pJar = Path.Combine(pDir, $"{parentId}.jar");
                        if (File.Exists(pJar)) parentJarPath = pJar;
                        break;
                    }
                }
            }
        }
        using var parentScope = parentDoc;
        string fabricPath = Path.Combine(verDir, $"{verBaseName}.fabric.json");
        if (!File.Exists(fabricPath)) fabricPath = Path.Combine(verDir, $"{inst.Id}.fabric.json");
        string loaderPath = Path.Combine(verDir, $"{verBaseName}.loader.json");
        if (!File.Exists(loaderPath)) loaderPath = Path.Combine(verDir, $"{inst.Id}.loader.json");
        string overlayPath = File.Exists(loaderPath) ? loaderPath : fabricPath;
        using var overlay = File.Exists(overlayPath) ? JsonDocument.Parse(await File.ReadAllTextAsync(overlayPath, setupCt)) : null;
        if (overlay != null && overlayPath == fabricPath)
            await _minecraft.EnsureLoaderLibrariesAsync(overlay.RootElement, setupCt);
        var launchProfile = overlay?.RootElement ?? root;

        if (!File.Exists(verJarPath) && !string.IsNullOrWhiteSpace(parentJarPath) && File.Exists(parentJarPath))
            verJarPath = parentJarPath;

        string mainClass = launchProfile.TryGetProperty("mainClass", out var mcEl)
            ? mcEl.GetString() ?? "net.minecraft.client.main.Main"
            : (parentDoc != null && parentDoc.RootElement.TryGetProperty("mainClass", out var pmcEl)
                ? pmcEl.GetString() ?? "net.minecraft.client.main.Main"
                : "net.minecraft.client.main.Main");
        string assetIndex = root.TryGetProperty("assets", out var aEl)
            ? aEl.GetString() ?? inst.McVersion
            : (parentDoc != null && parentDoc.RootElement.TryGetProperty("assets", out var paEl)
                ? paEl.GetString() ?? inst.McVersion
                : inst.McVersion);
        string? extMcDir = _storage.TryGetExternalMinecraftDir(inst.Id);
        string extLibsDir = !string.IsNullOrWhiteSpace(extMcDir) ? Path.Combine(extMcDir, "libraries") : "";
        string extAssetsDir = !string.IsNullOrWhiteSpace(extMcDir) ? Path.Combine(extMcDir, "assets") : "";
        string resolvedAssetsDir = (!string.IsNullOrWhiteSpace(extAssetsDir) &&
            File.Exists(Path.Combine(extAssetsDir, "indexes", $"{assetIndex}.json")))
            ? extAssetsDir
            : _storage.AssetsDir;
        string resolvedLibsDir = (!string.IsNullOrWhiteSpace(extLibsDir) && Directory.Exists(extLibsDir))
            ? extLibsDir
            : _storage.LibrariesDir;
        string pclNativesDir = Path.Combine(verDir, $"{verBaseName}-natives");
        string defaultNativesDir = Path.Combine(verDir, "natives");
        string nativesDir = Directory.Exists(pclNativesDir) && Directory.EnumerateFileSystemEntries(pclNativesDir).Any()
            ? pclNativesDir
            : defaultNativesDir;
        Directory.CreateDirectory(nativesDir);

        // Build ordered profile chain: overlay -> root -> parent (newest/loader first for library precedence)
        var libProfiles = new List<JsonElement>();
        if (overlay != null) libProfiles.Add(overlay.RootElement);
        libProfiles.Add(root);
        if (parentDoc != null) libProfiles.Add(parentDoc.RootElement);

        // Classpath construction (libraries + client jar)
        bool usePackProfile = inst.RequiredJavaMajor > 0;
        bool useStructuredProfile = usePackProfile ||
            libProfiles.Any(p => p.TryGetProperty("arguments", out _) || p.TryGetProperty("minecraftArguments", out _));
        var cpList = new List<string>();
        var seenLibraries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in libProfiles)
        {
            if (!profile.TryGetProperty("libraries", out var libs)) continue;
            foreach (var lib in libs.EnumerateArray())
            {
                if (!MinecraftApiService.IsLibraryAllowedOnWindows(lib)) continue;
                if (lib.TryGetProperty("downloads", out var downloads) && !downloads.TryGetProperty("artifact", out _)) continue;
                string relLib = MinecraftApiService.LibraryArtifactPath(lib);
                string extCandidate = !string.IsNullOrWhiteSpace(extLibsDir) ? Path.Combine(extLibsDir, relLib) : "";
                string full = (!string.IsNullOrWhiteSpace(extCandidate) && File.Exists(extCandidate))
                    ? extCandidate
                    : Path.Combine(_storage.LibrariesDir, relLib);
                if (useStructuredProfile && lib.TryGetProperty("name", out var coordinate))
                {
                    var parts = (coordinate.GetString() ?? "").Split(':');
                    if (parts.Length >= 3 && !seenLibraries.Add(parts[0] + ":" + parts[1] + ":" +
                        (parts.Length > 3 ? parts[3] : ""))) continue;
                }
                if (File.Exists(full)) cpList.Add(full);
                else if (usePackProfile) throw new FileNotFoundException($"整合包缺少游戏库：{Path.GetFileName(full)}");
            }
        }
        if (usePackProfile)
        {
            string overlayJar = Path.Combine(verDir, $"{inst.Id}.loader.jar");
            if (File.Exists(overlayJar)) cpList.Add(overlayJar);
            if (!File.Exists(verJarPath)) throw new FileNotFoundException("整合包缺少 Minecraft 客户端文件。");
        }
        if (File.Exists(verJarPath)) cpList.Add(verJarPath);
        string classpath = string.Join(Path.PathSeparator, cpList.Distinct(StringComparer.OrdinalIgnoreCase));

        onProgress?.Invoke(55);
        string playerName = account?.Username is { Length: > 0 } ? account.Username : "Player";
        string uuid = account?.Uuid is { Length: > 0 } ? account.Uuid : AccountManager.ComputeOfflineUuid(playerName);
        string token = account?.AccessToken is { Length: > 0 } ? account.AccessToken : "00000000000000000000000000000000";
        string userType = (account?.Type.Contains("Microsoft") ?? false) ? "msa" : "legacy";

        var args = new List<string>
        {
            $"-Xmx{memoryMb}M",
            $"-Xms{Math.Min(512, memoryMb)}M",
            "-Dfile.encoding=UTF-8",
            $"-Djava.library.path=\"{nativesDir}\""
        };

        int javaMajor = JavaManager.ParseMajor(java.Version);
        static bool IsSafeJvmArgForJava(string arg, int major)
        {
            if (arg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
                return false;
            if (major > 0 && major < 21 && arg.Equals("-XX:+ZGenerational", StringComparison.OrdinalIgnoreCase))
                return false;
            if (major > 0 && major < 15 && arg.Equals("-XX:+UseZGC", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(customJvmArgs))
        {
            foreach (var tok in customJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsSafeJvmArgForJava(tok, javaMajor))
                {
                    args.Add(tok);
                }
            }
        }

        args.Add("-cp");
        args.Add($"\"{classpath}\"");
        args.Add(mainClass);

        args.AddRange(new[]
        {
            "--username", $"\"{playerName}\"",
            "--version", $"\"{inst.McVersion}\"",
            "--gameDir", $"\"{verDir}\"",
            "--assetsDir", $"\"{resolvedAssetsDir}\"",
            "--assetIndex", $"\"{assetIndex}\"",
            "--uuid", uuid,
            "--accessToken", token,
            "--userType", userType
        });

        onProgress?.Invoke(82);
        string javaExe = Path.Combine(Path.GetDirectoryName(java.Path)!, "java.exe");
        string executable = Path.GetFileName(java.Path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(javaExe) ? javaExe : java.Path;
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = verDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        if (useStructuredProfile)
        {
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth_player_name"] = playerName, ["auth_uuid"] = uuid, ["auth_access_token"] = token,
                ["auth_session"] = token, ["auth_xuid"] = "", ["clientid"] = "", ["user_type"] = userType,
                ["user_properties"] = "{}", ["profile_properties"] = "{}",
                ["version_name"] = ResourceCatalog.Text(launchProfile, "id", inst.McVersion),
                ["version_type"] = ResourceCatalog.Text(root, "type", "release"),
                ["game_directory"] = verDir, ["assets_root"] = resolvedAssetsDir, ["game_assets"] = resolvedAssetsDir,
                ["assets_index_name"] = assetIndex, ["natives_directory"] = nativesDir,
                ["library_directory"] = resolvedLibsDir, ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["classpath"] = classpath, ["launcher_name"] = "DCL", ["launcher_version"] = "1.0"
            };
            var packJvm = new List<string> { $"-Xmx{memoryMb}M", $"-Xms{Math.Min(512, memoryMb)}M", "-Dfile.encoding=UTF-8" };
            if (!string.IsNullOrWhiteSpace(customJvmArgs))
            {
                foreach (string arg in SplitArgs(customJvmArgs))
                {
                    if (IsSafeJvmArgForJava(arg, javaMajor) && !arg.Equals("-Dfile.encoding=UTF-8", StringComparison.OrdinalIgnoreCase))
                        packJvm.Add(arg);
                }
            }
            // Argument merge order: parent -> root -> overlay
            var argProfiles = new List<JsonElement>();
            if (parentDoc != null) argProfiles.Add(parentDoc.RootElement);
            argProfiles.Add(root);
            if (overlay != null) argProfiles.Add(overlay.RootElement);

            packJvm.AddRange(argProfiles.SelectMany(profile => ProfileArguments(profile, "jvm")));
            if (!packJvm.Any(arg => arg.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
                packJvm.Add("-Djava.library.path=${natives_directory}");
            if (!packJvm.Any(arg => arg is "-cp" or "-classpath" or "--class-path"))
                packJvm.AddRange(["-cp", "${classpath}"]);
            var logSource = root.TryGetProperty("logging", out _) ? root : (parentDoc?.RootElement ?? root);
            if (logSource.TryGetProperty("logging", out var log) && log.TryGetProperty("client", out var clientLog) &&
                clientLog.TryGetProperty("file", out var logFile))
            {
                string logId = ResourceCatalog.Text(logFile, "id");
                string extLogPath = Path.Combine(resolvedAssetsDir, "log_configs", logId);
                string defLogPath = Path.Combine(_storage.AssetsDir, "log_configs", logId);
                string path = File.Exists(extLogPath) ? extLogPath : defLogPath;
                if (File.Exists(path)) packJvm.Add(ResourceCatalog.Text(clientLog, "argument").Replace("${path}", path));
            }

            foreach (string arg in packJvm)
            {
                // Forge's ignore list must name the actual client JAR on this instance's classpath.
                string adjusted = arg.StartsWith("-DignoreList=", StringComparison.Ordinal)
                    ? arg.Replace("${version_name}.jar", Path.GetFileName(verJarPath))
                    : arg;
                psi.ArgumentList.Add(Expand(adjusted, tokens));
            }
            psi.ArgumentList.Add(mainClass);
            foreach (string arg in argProfiles.SelectMany(profile => ProfileArguments(profile, "game")))
                psi.ArgumentList.Add(Expand(arg, tokens));
        }
        else psi.Arguments = string.Join(' ', args);

        setupCt.ThrowIfCancellationRequested();
        _storage.Log($"启动游戏 [{inst.Id}] {inst.Name} · Minecraft {inst.McVersion} · {inst.Loader} {inst.LoaderVersion} · Java {java.Version} · {memoryMb} MB");
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return (false, "无法启动 Java 进程", -1, new());
        }

        onProgress?.Invoke(90);
        var logTail = new List<string>();
        void RecordOutput(string? line, string stream)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (account?.AccessToken is { Length: > 0 }) line = line.Replace(token, "[已隐去]", StringComparison.Ordinal);
            lock (logTail)
            {
                logTail.Add(line);
                if (logTail.Count > 120) logTail.RemoveAt(0);
            }
            _storage.Log($"游戏 [{inst.Id}/{stream}] {line}");
        }
        _storage.Log($"游戏 [{inst.Id}] PID {proc.Id}");
        proc.OutputDataReceived += (_, e) => RecordOutput(e.Data, "stdout");
        proc.ErrorDataReceived += (_, e) => RecordOutput(e.Data, "stderr");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 持续监测直到 Java 游戏界面正式被唤醒（MainWindowHandle != 0）；在此之前点击「取消」可直接终止 Java 进程
        bool cancelledBeforeWindow = false;
        using var windowWatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var windowWatchTask = Task.Run(async () =>
        {
            try
            {
                while (!windowWatchCts.Token.IsCancellationRequested)
                {
                    if (preSpawnCt.IsCancellationRequested)
                    {
                        cancelledBeforeWindow = true;
                        _storage.Log($"用户取消启动 [{inst.Id}]，正在终止尚未唤醒界面的 Java 进程 (PID {proc.Id})");
                        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                        return;
                    }
                    try
                    {
                        if (proc.HasExited) return;
                        proc.Refresh();
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            _storage.Log($"游戏窗口已唤醒 [{inst.Id}] (PID {proc.Id}, HWND 0x{proc.MainWindowHandle.ToInt64():X})");
                            onProgress?.Invoke(100);
                            return;
                        }
                    }
                    catch
                    {
                        return;
                    }
                    await Task.Delay(180, windowWatchCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        await proc.WaitForExitAsync(ct);
        windowWatchCts.Cancel();
        try { await windowWatchTask; } catch { }

        if (cancelledBeforeWindow || preSpawnCt.IsCancellationRequested)
        {
            throw new OperationCanceledException(preSpawnCt);
        }

        _storage.Log($"游戏 [{inst.Id}] 已退出，退出码 {proc.ExitCode}");
        if (proc.ExitCode == 0)
        {
            return (true, "游戏已正常退出", 0, logTail);
        }

        var diagnosis = AnalyzeCrashLogs(verDir, logTail);
        return (false, $"游戏异常退出 (Exit {proc.ExitCode})", proc.ExitCode, diagnosis);
    }

    private static IEnumerable<string> ProfileArguments(JsonElement profile, string kind)
    {
        if (!profile.TryGetProperty("arguments", out var arguments) || !arguments.TryGetProperty(kind, out var values))
        {
            if (kind == "game")
                foreach (string token in SplitArgs(ResourceCatalog.Text(profile, "minecraftArguments"))) yield return token;
            yield break;
        }
        foreach (var entry in values.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String) yield return entry.GetString()!;
            else if (MinecraftApiService.IsLibraryAllowedOnWindows(entry) && entry.TryGetProperty("value", out var value))
            {
                if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var arg in value.EnumerateArray()) yield return arg.GetString()!;
            }
        }
    }

    private static IEnumerable<string> SplitArgs(string args) =>
        Regex.Matches(args, """(?:[^\s"]|"[^"]*")+""").Select(match => match.Value.Replace("\"", ""));

    private static string Expand(string value, IReadOnlyDictionary<string, string> tokens) =>
        Regex.Replace(value, @"\$\{([^}]+)\}", match => tokens.TryGetValue(match.Groups[1].Value, out string? replacement)
            ? replacement
            : match.Value);

    public List<string> AnalyzeCrashLogs(string? gameDir = null, List<string>? runtimeLines = null)
    {
        var output = new List<string>();
        gameDir ??= _storage.MinecraftDir;
        string crashDir = Path.Combine(gameDir, "crash-reports");
        string? latestCrash = Directory.Exists(crashDir)
            ? Directory.GetFiles(crashDir, "crash-*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
        string latestLog = Path.Combine(gameDir, "logs", "latest.log");

        string content = "";
        if (latestCrash != null) content = File.ReadAllText(latestCrash, Encoding.UTF8);
        else if (File.Exists(latestLog)) content = File.ReadAllText(latestLog, Encoding.UTF8);
        else if (runtimeLines != null) content = string.Join('\n', runtimeLines);

        if (string.IsNullOrWhiteSpace(content))
        {
            output.Add("[未检测到游戏崩溃日志 — 下次非零退出时将自动诊断]");
            return output;
        }

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var errorLines = lines.Where(l => l.Contains("ERROR") || l.Contains("Exception") || l.Contains("FATAL") || l.Contains("Mixin")).Take(6).ToList();

        if (content.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
            output.Add("↳ 诊断结果：Java 堆内存不足 (OutOfMemoryError) — 建议增大分配内存");
        else if (content.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
            output.Add("↳ 诊断结果：Java 版本与游戏核心不匹配 — MC 1.20.5+ 需使用 Java 21");
        else if (content.Contains("Mixin", StringComparison.OrdinalIgnoreCase))
            output.Add("↳ 诊断结果：检测到 Mixin 注入冲突 — 请检查报错行对应的前置/兼容模组");
        else if (content.Contains("Missing or unsupported mandatory dependencies", StringComparison.OrdinalIgnoreCase) || content.Contains("Requires", StringComparison.OrdinalIgnoreCase))
            output.Add("↳ 诊断结果：缺少前置依赖模组 (Missing Dependency)");
        else
            output.Add("↳ 诊断摘要：请查看下方最近报错堆栈");

        output.AddRange(errorLines.Take(5));
        return output;
    }
}
