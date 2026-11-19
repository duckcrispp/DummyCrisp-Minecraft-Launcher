using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

internal sealed record InstalledPack(string Name, string McVersion, string Loader, string LoaderVersion, int JavaMajor, string? Icon = null)
{
    internal const string FileName = ".dcl-pack.json";
}

internal sealed class ModpackInstaller(StorageService storage, DownloadManager downloads,
    MinecraftApiService minecraft, CurseForgeService curseForge)
{
    private sealed record PackFile(string Path, string[] Urls, string Sha1, string? Sha512, long Size, string? LocalPath = null);
    private sealed record PackPlan(string Name, string Minecraft, string Loader, string LoaderVersion,
        List<PackFile> Files, string Overrides, string? ClientOverrides);

    internal static string InstanceId(string project, string build) =>
        "pack-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project}:{build}")))[..16].ToLowerInvariant();

    internal async Task InstallStandaloneLoaderAsync(string mcVersion, string versionUrl, string? versionSha1,
        string id, string loaderKind, string loaderVersion, string mirror,
        IReadOnlyList<JavaInstallation> javas, IProgress<(int Progress, string Speed)> progress, CancellationToken ct,
        string? optifineType = null, string? optifinePatch = null, string? optifineFile = null)
    {
        ValidateVersionComponent(mcVersion, "Minecraft 版本");
        bool isStandaloneOptiFine = loaderKind == "OptiFine";
        bool hasOptiFine = !string.IsNullOrWhiteSpace(optifineType) && !string.IsNullOrWhiteSpace(optifinePatch);
        if (isStandaloneOptiFine)
        {
            if (!hasOptiFine) throw new InvalidDataException("缺少 OptiFine 构建参数。");
            ValidateVersionComponent(optifineType!, "OptiFine 类型");
            ValidateVersionComponent(optifinePatch!, "OptiFine 版本");
        }
        else
        {
            ValidateVersionComponent(loaderVersion, "加载器版本");
            ValidateLoaderVersion(new PackPlan(id, mcVersion, loaderKind, loaderVersion, [], "overrides", null));
            if (hasOptiFine)
            {
                ValidateVersionComponent(optifineType!, "OptiFine 类型");
                ValidateVersionComponent(optifinePatch!, "OptiFine 版本");
            }
        }

        string final = storage.GameDirectory(id);
        if (Directory.Exists(final)) throw new InvalidOperationException($"版本 {id} 已存在，可在版本列表中找到。");
        string stagingRoot = Path.GetFullPath(Path.Combine(storage.CacheDir, "loader-install"));
        string work = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(work, "instance");
        Directory.CreateDirectory(instance);
        try
        {
            progress.Report((5, $"准备 Minecraft {mcVersion} 基础核心"));
            await minecraft.InstallVanillaOrFabricAsync(mcVersion, versionUrl, id, null, mirror,
                new CoreProgress(progress), ct, instance, versionSha1);

            using var baseProfile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(instance, $"{id}.json"), ct));
            int javaMajor = baseProfile.RootElement.TryGetProperty("javaVersion", out var java)
                ? (int)Number(java, "majorVersion") : 0;
            if (javaMajor <= 0) javaMajor = InstanceManager.RequiredJavaForMcVersion(mcVersion);

            if (isStandaloneOptiFine)
            {
                var runtime = javas.FirstOrDefault(item => JavaManager.ParseMajor(item.Version) == javaMajor && File.Exists(item.Path))
                    ?? javas.FirstOrDefault(item => File.Exists(item.Path))
                    ?? throw new InvalidOperationException($"OptiFine 安装需要 Java {javaMajor}。请在「实例环境 → 账号与 Java」安装后重试。");
                await InstallOptiFineStandaloneAsync(mcVersion, optifineType!, optifinePatch!, runtime, work, instance, id, progress, ct);
            }
            else if (loaderKind == "Quilt")
            {
                progress.Report((60, $"安装 Quilt {loaderVersion}"));
                await InstallQuiltAsync(new PackPlan(id, mcVersion, loaderKind, loaderVersion, [], "overrides", null), instance, id, mirror, ct);
            }
            else if (loaderKind is "Forge" or "NeoForge")
            {
                progress.Report((50, $"准备 {loaderKind} 安装器"));
                var runtime = javas.FirstOrDefault(item => JavaManager.ParseMajor(item.Version) == javaMajor && File.Exists(item.Path))
                    ?? throw new InvalidOperationException($"{loaderKind} 安装器需要 Java {javaMajor}。请在「实例环境 → 账号与 Java」安装后重试。");
                await InstallForgeAsync(new PackPlan(id, mcVersion, loaderKind, loaderVersion, [], "overrides", null), runtime, work, instance, id, mirror, progress, ct);
                if (loaderKind == "Forge" && hasOptiFine)
                {
                    progress.Report((86, $"下载 OptiFine {optifineType}_{optifinePatch} 模组"));
                    string ofJar = await DownloadOptiFineJarAsync(mcVersion, optifineType!, optifinePatch!, work, ct);
                    string modsDir = Path.Combine(instance, "mods");
                    Directory.CreateDirectory(modsDir);
                    string safeFile = !string.IsNullOrWhiteSpace(optifineFile) && InstanceManager.IsFileName(optifineFile) && optifineFile.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        ? optifineFile
                        : $"OptiFine_{mcVersion}_{optifineType}_{optifinePatch}.jar";
                    File.Copy(ofJar, Path.Combine(modsDir, safeFile), overwrite: true);
                }
            }
            else
            {
                throw new InvalidOperationException($"暂不支持独立安装加载器：{loaderKind}");
            }

            ct.ThrowIfCancellationRequested();
            progress.Report((96, "完成版本目录写入"));
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            Directory.Move(instance, final);
            progress.Report((100, "安装完成"));
        }
        finally
        {
            try
            {
                string fullPath = Path.GetFullPath(work);
                if (fullPath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                storage.Log($"无法清理加载器安装临时目录：{ex.Message}");
            }
        }
    }

    private async Task<string> DownloadOptiFineJarAsync(string mcVersion, string type, string patch, string work, CancellationToken ct)
    {
        string url = $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        string cachedJar = Path.Combine(storage.CacheDir, "loaders", $"optifine-{cacheKey}.jar");
        string dest = Path.Combine(work, "optifine-installer.jar");
        if (File.Exists(cachedJar) && IsValidZipFile(cachedJar))
        {
            File.Copy(cachedJar, dest, overwrite: true);
            return dest;
        }
        await downloads.DownloadVerifiedFileAsync([url], dest, null, null, null, null, ct, overwrite: true);
        if (!IsValidZipFile(dest))
            throw new InvalidDataException("下载的 OptiFine 安装包已损坏或格式无效。");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachedJar)!);
            File.Copy(dest, cachedJar, overwrite: true);
        }
        catch { }
        return dest;
    }

    private static bool IsValidZipFile(string path)
    {
        try { using var zip = ZipFile.OpenRead(path); return zip.Entries.Count > 0; }
        catch { return false; }
    }

    private async Task InstallOptiFineStandaloneAsync(string mcVersion, string type, string patch,
        JavaInstallation java, string work, string directory, string id,
        IProgress<(int Progress, string Speed)> progress, CancellationToken ct)
    {
        progress.Report((54, $"下载 OptiFine {type}_{patch}"));
        string ofJar = await DownloadOptiFineJarAsync(mcVersion, type, patch, work, ct);
        string ofVersion = $"{mcVersion}_{type}_{patch}";
        string ofLibPath = Path.Combine(storage.LibrariesDir, "optifine", "OptiFine", ofVersion, $"OptiFine-{ofVersion}.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(ofLibPath)!);

        var libs = new List<object> { new { name = $"optifine:OptiFine:{ofVersion}" } };
        using (var zip = ZipFile.OpenRead(ofJar))
        {
            var lwTxt = zip.GetEntry("launchwrapper-of.txt");
            if (lwTxt != null)
            {
                using var reader = new StreamReader(lwTxt.Open());
                string lwVer = (await reader.ReadToEndAsync(ct)).Trim();
                var lwEntry = zip.GetEntry($"launchwrapper-of-{lwVer}.jar");
                if (lwVer.Length > 0 && InstanceManager.IsFileName(lwVer) && lwEntry != null)
                {
                    string lwDest = Path.Combine(storage.LibrariesDir, "optifine", "launchwrapper-of", lwVer, $"launchwrapper-of-{lwVer}.jar");
                    Directory.CreateDirectory(Path.GetDirectoryName(lwDest)!);
                    lwEntry.ExtractToFile(lwDest, overwrite: true);
                    libs.Add(new { name = $"optifine:launchwrapper-of:{lwVer}" });
                }
            }
            else
            {
                libs.Add(new { name = "net.minecraft:launchwrapper:1.12" });
            }
        }

        progress.Report((72, $"修补 OptiFine {type}_{patch}"));
        string vanillaJar = Path.Combine(directory, $"{id}.jar");
        string javaConsole = Path.Combine(Path.GetDirectoryName(java.Path)!, "java.exe");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(javaConsole) ? javaConsole : java.Path,
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-Djava.awt.headless=true", "-cp", ofJar, "optifine.Patcher", vanillaJar, ofJar, ofLibPath }
        };
        using var proc = Process.Start(start) ?? throw new IOException("无法启动 OptiFine 修补程序。");
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(TimeSpan.FromMinutes(3));
        var stderrTask = proc.StandardError.ReadToEndAsync(runCts.Token);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(runCts.Token);
        await proc.WaitForExitAsync(runCts.Token);
        string stderr = await stderrTask;
        _ = await stdoutTask;
        if (proc.ExitCode != 0 || !File.Exists(ofLibPath))
        {
            string reason = stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? $"退出码 {proc.ExitCode}";
            throw new InvalidOperationException($"OptiFine 安装失败：{reason}");
        }

        var overlayProfile = new
        {
            id,
            inheritsFrom = mcVersion,
            type = "release",
            mainClass = "net.minecraft.launchwrapper.Launch",
            arguments = new
            {
                game = new[] { "--tweakClass", "optifine.OptiFineTweaker" }
            },
            libraries = libs
        };
        await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.loader.json"),
            JsonSerializer.Serialize(overlayProfile, StorageService.JsonOpts), ct);
    }

    internal async Task InstallAsync(string id, string source, string name, ResourceFile build, string mirror,
        IReadOnlyList<JavaInstallation> javas, string curseForgeKey,
        IProgress<(int Progress, string Speed)> progress, CancellationToken ct, string curseForgeRoute = "auto",
        string? iconUrl = null)
    {
        string final = storage.GameDirectory(id);
        if (Directory.Exists(final)) throw new InvalidOperationException("该整合包构建已安装，可在版本实例列表中找到。");
        string stagingRoot = Path.GetFullPath(Path.Combine(storage.CacheDir, "modpack-install"));
        string work = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(work, "instance");
        Directory.CreateDirectory(instance);
        try
        {
            progress.Report((2, "下载整合包清单"));
            string archivePath = build.Sha1.Length == 40 && build.Sha1.All(Uri.IsHexDigit)
                ? Path.Combine(storage.CacheDir, "pack-files", build.Sha1[..2].ToLowerInvariant(), build.Sha1.ToLowerInvariant())
                : Path.Combine(work, "pack.zip");
            if (!IsHttps(build.FileUrl)) throw new InvalidDataException("整合包下载地址无效。");
            await downloads.DownloadVerifiedFileAsync(DownloadRoutes.Files(build.FileUrl, mirror, build.Sha1.Length > 0),
                archivePath, build.Sha1, null, build.Size,
                (received, total) =>
                {
                    if (total is > 0) progress.Report((2 + (int)(received * 7 / total.Value), "下载整合包清单"));
                }, ct);

            using var archive = ZipFile.OpenRead(archivePath);
            var plan = source == "CurseForge"
                ? await ReadCurseForgePlanAsync(archive, curseForgeKey, curseForgeRoute, ct)
                : ReadModrinthPlan(archive);
            ValidateVersionComponent(plan.Minecraft, "Minecraft 版本");
            if (plan.Loader != "Vanilla") ValidateVersionComponent(plan.LoaderVersion, "加载器版本");
            ValidateLoaderVersion(plan);

            progress.Report((10, "准备 Minecraft 与加载器"));
            var manifest = await minecraft.FetchVersionManifestAsync(mirror, ct);
            var version = manifest.FirstOrDefault(item => Equals(item["id"], plan.Minecraft));
            string versionUrl = version?.GetValueOrDefault("url")?.ToString() ?? "";
            if (versionUrl.Length == 0) throw new InvalidDataException($"找不到整合包要求的 Minecraft {plan.Minecraft}。");

            await minecraft.InstallVanillaOrFabricAsync(plan.Minecraft, versionUrl, id,
                plan.Loader == "Fabric" ? plan.LoaderVersion : null, mirror, new CoreProgress(progress), ct, instance, version?.GetValueOrDefault("sha1")?.ToString());

            using var baseProfile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(instance, $"{id}.json"), ct));
            int javaMajor = baseProfile.RootElement.TryGetProperty("javaVersion", out var java)
                ? (int)Number(java, "majorVersion") : 0;
            if (javaMajor <= 0) javaMajor = InstanceManager.RequiredJavaForMcVersion(plan.Minecraft);

            if (plan.Loader == "Quilt")
                await InstallQuiltAsync(plan, instance, id, mirror, ct);
            else if (plan.Loader is "Forge" or "NeoForge")
            {
                progress.Report((50, $"准备 {plan.Loader} 安装器"));
                var runtime = javas.FirstOrDefault(item => JavaManager.ParseMajor(item.Version) == javaMajor && File.Exists(item.Path))
                    ?? throw new InvalidOperationException($"此整合包的 {plan.Loader} 安装器需要 Java {javaMajor}。请在「实例环境 → 账号与 Java」安装后重试。");
                await InstallForgeAsync(plan, runtime, work, instance, id, mirror, progress, ct);
            }

            progress.Report((64, $"下载客户端文件 0/{plan.Files.Count}"));
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileJobs = plan.Files.Select(file =>
            {
                string target = TargetPath(instance, id, file.Path);
                if (!targets.Add(target)) throw new InvalidDataException($"整合包包含重复的文件路径：{file.Path}");
                return (File: file, Target: target);
            }).ToArray();
            int complete = 0;
            await Parallel.ForEachAsync(fileJobs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (job, token) =>
                {
                    await DownloadPackFileAsync(job.File, job.Target, mirror, token);
                    int count = Interlocked.Increment(ref complete);
                    progress.Report((64 + (int)(count * 30.0 / fileJobs.Length), $"下载客户端文件 {count}/{fileJobs.Length}"));
                });

            progress.Report((95, "安装整合包配置与覆盖文件"));
            await ExtractOverridesAsync(archive, plan.Overrides, instance, id, ct);
            if (plan.ClientOverrides != null)
                await ExtractOverridesAsync(archive, plan.ClientOverrides, instance, id, ct);

            string display = name.Length > 0 ? name : plan.Name;
            string buildName = source == "Modrinth" && build.VersionNumber.Length > 0 ? build.VersionNumber : build.Title;
            if (buildName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) buildName = buildName[..^4];
            else if (buildName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)) buildName = buildName[..^7];
            buildName = buildName.Trim();
            if (buildName.Length > 0 && !display.Equals(buildName, StringComparison.OrdinalIgnoreCase))
            {
                string normDisplay = Regex.Replace(display, @"[\W_]+", "").ToLowerInvariant();
                string normBuild = Regex.Replace(buildName, @"[\W_]+", "").ToLowerInvariant();
                if (normDisplay.Length >= 4 && normBuild.StartsWith(normDisplay, StringComparison.Ordinal))
                {
                    // Avoid redundant "PackName · PackName-2.0.0" duplication
                    string suffix = Regex.Replace(buildName, @"^.*?(?=\bv?\d+(?:\.\d+)+)", "", RegexOptions.IgnoreCase).Trim(' ', '-', '_', '·', '.', ':');
                    display = suffix.Length > 0 ? $"{display} {suffix}" : display;
                }
                else
                {
                    display += " · " + buildName;
                }
            }
            string? cachedIcon = await TryCachePackIconAsync(iconUrl, work, mirror, ct);
            var metadata = new InstalledPack(display, plan.Minecraft, plan.Loader, plan.LoaderVersion, javaMajor, cachedIcon);
            await File.WriteAllTextAsync(Path.Combine(instance, InstalledPack.FileName),
                JsonSerializer.Serialize(metadata, StorageService.JsonOpts), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(instance, final);
            CleanupConsumedLocalFiles(plan.Files);
        }
        finally
        {
            try
            {
                string fullPath = Path.GetFullPath(work);
                if (fullPath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                storage.Log($"无法清理整合包临时目录：{ex.Message}");
            }
        }
    }

    private async Task<string?> TryCachePackIconAsync(string? iconUrl, string workDir, string mirror, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(iconUrl) || !IsHttps(iconUrl)) return null;
        string tempIcon = Path.Combine(workDir, "pack-icon.tmp");
        try
        {
            using var iconCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            iconCts.CancelAfter(TimeSpan.FromSeconds(10));
            await downloads.DownloadVerifiedFileAsync(DownloadRoutes.Files(iconUrl, mirror, false),
                tempIcon, null, null, null, null, iconCts.Token, overwrite: true);
            var info = new FileInfo(tempIcon);
            if (!info.Exists || info.Length < 24 || info.Length > 2 * 1024 * 1024) return null;
            byte[] data = await File.ReadAllBytesAsync(tempIcon, iconCts.Token);
            string? ext = DetectImageExtension(data);
            return ext != null ? storage.CacheIconAsset(data, ext) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            storage.Log($"缓存整合包官方图标失败（已跳过）：{ex.Message}");
            return null;
        }
    }

    private static string? DetectImageExtension(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ".jpg";
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return ".webp";
        return null;
    }

    private void CleanupConsumedLocalFiles(IEnumerable<PackFile> files)
    {
        string[] allowedRoots =
        [
            Path.GetFullPath(Path.Combine(storage.CacheDir, "manual-downloads")) + Path.DirectorySeparatorChar,
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")) + Path.DirectorySeparatorChar
        ];
        int removed = 0;
        foreach (string path in files.Select(f => f.LocalPath).OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!allowedRoots.Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
                if (!File.Exists(full)) continue;
                File.Delete(full);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                storage.Log($"安装后未能清理手动下载文件 {Path.GetFileName(path)}：{ex.Message}");
            }
        }
        if (removed > 0)
            storage.Log($"整合包安装完成，已自动清理 {removed} 个「下载」目录中的手动补件");
    }

    private static PackPlan ReadModrinthPlan(ZipArchive archive)
    {
        using var manifest = ReadManifest(archive, "modrinth.index.json");
        var root = manifest.RootElement;
        if (Text(root, "game") != "minecraft" || Number(root, "formatVersion") != 1)
            throw new InvalidDataException("此文件不是受支持的 Minecraft Modrinth 整合包。");

        var deps = root.GetProperty("dependencies");
        var loaderKeys = new Dictionary<string, string>
        {
            ["forge"] = "Forge", ["neoforge"] = "NeoForge", ["fabric-loader"] = "Fabric", ["quilt-loader"] = "Quilt"
        };
        var declaredLoaders = deps.EnumerateObject().Where(dependency => dependency.Name != "minecraft").ToArray();
        if (declaredLoaders.Length > 1) throw new InvalidDataException("整合包声明了多个加载器。");
        string loader = "Vanilla", loaderVersion = "";
        if (declaredLoaders.Length == 1)
        {
            var entry = declaredLoaders[0];
            if (!loaderKeys.TryGetValue(entry.Name, out loader!))
                throw new InvalidDataException($"整合包要求的加载器暂未支持：{entry.Name}");
            loaderVersion = Text(deps, entry.Name);
        }

        var files = new List<PackFile>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            if (file.TryGetProperty("env", out var env) && Text(env, "client") is "unsupported" or "optional")
                continue;
            string path = Text(file, "path");
            var hashes = file.GetProperty("hashes");
            string sha1 = Text(hashes, "sha1"), sha512 = Text(hashes, "sha512");
            if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit) || sha512.Length != 128 || !sha512.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Modrinth 清单缺少有效的文件校验值：{path}");
            string[] urls = Strings(file, "downloads").Where(IsModrinthFileUrl).ToArray();
            if (urls.Length == 0) throw new InvalidDataException($"Modrinth 清单没有受支持的下载地址：{path}");
            files.Add(new PackFile(path, urls, sha1, sha512, Number(file, "fileSize")));
        }

        return new PackPlan(Text(root, "name", "Modrinth 整合包"), Text(deps, "minecraft"), loader, loaderVersion,
            files, "overrides", "client-overrides");
    }

    private async Task<PackPlan> ReadCurseForgePlanAsync(ZipArchive archive, string key, string route, CancellationToken ct)
    {
        using var manifest = ReadManifest(archive, "manifest.json");
        var root = manifest.RootElement;
        if (Text(root, "manifestType") != "minecraftModpack" || Number(root, "manifestVersion") != 1)
            throw new InvalidDataException("此文件不是受支持的 CurseForge 客户端整合包。");
        var minecraft = root.GetProperty("minecraft");
        var loaders = Array(minecraft, "modLoaders");
        var primary = loaders.FirstOrDefault(loader =>
            loader.TryGetProperty("primary", out var value) && value.ValueKind == JsonValueKind.True);
        if (primary.ValueKind == JsonValueKind.Undefined && loaders.Length > 0) primary = loaders[0];

        string loaderName = "Vanilla", loaderVersion = "";
        if (primary.ValueKind == JsonValueKind.Object)
        {
            string declared = Text(primary, "id");
            int separator = declared.IndexOf('-');
            if (separator <= 0) throw new InvalidDataException("CurseForge 清单中的加载器标识无效。");
            loaderName = Loader(declared[..separator]);
            loaderVersion = declared[(separator + 1)..];
        }

        var projectsByFile = new Dictionary<int, int>();
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            if (file.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.False) continue;
            int projectId = checked((int)Number(file, "projectID"));
            int fileId = checked((int)Number(file, "fileID"));
            if (projectId <= 0 || fileId <= 0 ||
                (projectsByFile.TryGetValue(fileId, out int existing) && projectId != existing))
                throw new InvalidDataException("CurseForge 清单中的依赖文件标识无效。");
            projectsByFile[fileId] = projectId;
        }
        string[] localSearchDirs =
        [
            Path.Combine(storage.CacheDir, "manual-downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        ];
        var resolved = await curseForge.GetPackFilesAsync(projectsByFile, key, ct, route, localSearchDirs);
        var files = resolved.Select(item =>
        {
            if (item.LocalPath != null)
                return new PackFile(item.Path, [], item.File.Sha1, null, item.File.Size, item.LocalPath);
            if (!IsHttps(item.File.FileUrl)) throw new InvalidDataException($"CurseForge 下载地址无效：{item.File.File}");
            return new PackFile(item.Path, [item.File.FileUrl], item.File.Sha1, null, item.File.Size);
        }).ToList();
        string overrides = Text(root, "overrides", "overrides");
        if (!InstanceManager.IsFileName(overrides)) throw new InvalidDataException("CurseForge 覆盖目录无效。");
        return new PackPlan(Text(root, "name", "CurseForge 整合包"), Text(minecraft, "version"), loaderName,
            loaderVersion, files, overrides, null);
    }

    private static JsonDocument ReadManifest(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry == null || entry.Length > 8 * 1024 * 1024)
            throw new InvalidDataException($"整合包缺少有效的 {name} 清单。请选择客户端构建。");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private async Task DownloadPackFileAsync(PackFile file, string destination, string mirror, CancellationToken ct)
    {
        if (file.LocalPath != null)
        {
            if (!File.Exists(file.LocalPath) ||
                !DownloadManager.VerifyFile(file.LocalPath, file.Sha1, file.Sha512, file.Size > 0 ? file.Size : null, ct))
                throw new InvalidDataException($"本地文件校验失败或已被移除：{Path.GetFileName(destination)}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.LocalPath, destination, overwrite: true);
            return;
        }
        bool verified = file.Sha1.Length > 0 || file.Sha512 is { Length: > 0 };
        var routes = file.Urls.SelectMany(url => DownloadRoutes.Files(url, mirror, verified)).Distinct().ToArray();
        var candidates = new RouteList(routes, mirror.StartsWith("最优", StringComparison.Ordinal));
        string digest = file.Sha512 is { Length: 128 } ? file.Sha512 : file.Sha1;
        bool cacheable = digest.Length is 40 or 128 && digest.All(Uri.IsHexDigit);
        string cache = cacheable ? Path.Combine(storage.CacheDir, "pack-files", digest[..2].ToLowerInvariant(), digest.ToLowerInvariant()) : destination;
        await downloads.DownloadVerifiedFileAsync(candidates, cache, file.Sha1, file.Sha512, file.Size, null, ct);
        if (cacheable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(cache);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct);
        }
    }

    private static async Task ExtractOverridesAsync(ZipArchive archive, string prefix, string instance, string id, CancellationToken ct)
    {
        prefix += "/";
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0 || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("整合包覆盖文件包含符号链接。");
            string target = TargetPath(instance, id, entry.FullName[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, ct);
        }
    }

    private static string TargetPath(string root, string id, string relative)
    {
        string[] parts = relative.Split('/');
        if (parts.Any(part => !InstanceManager.IsFileName(part) || part.Contains('\\') || IsWindowsDevice(part)) ||
            parts[0].Equals("natives", StringComparison.OrdinalIgnoreCase) ||
            (parts.Length == 1 && (parts[0].StartsWith(id + ".", StringComparison.OrdinalIgnoreCase) ||
                parts[0].Equals(InstalledPack.FileName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException($"整合包包含无效路径：{relative}");
        string target = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"整合包路径超出实例目录：{relative}");
        return target;
    }

    private static bool IsWindowsDevice(string path)
    {
        string stem = path.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }

    private static void ValidateVersionComponent(string value, string label)
    {
        if (!Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9._+\-]{0,79}$") || value.EndsWith('.'))
            throw new InvalidDataException($"整合包中的{label}无效。");
    }

    private static void ValidateLoaderVersion(PackPlan plan)
    {
        if (plan.Loader is not ("Vanilla" or "Fabric" or "Quilt" or "Forge" or "NeoForge"))
            throw new InvalidDataException($"整合包要求的加载器暂未支持：{plan.Loader}");
        if (plan.Loader == "Forge" && Regex.IsMatch(plan.Minecraft, @"^1\.(?:[0-9]|1[0-2])(?:\.|$)"))
            throw new InvalidOperationException($"此整合包使用旧版 Forge（Minecraft {plan.Minecraft}），当前只支持 1.13 及更新版本的 Forge 安装器。");
    }

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsModrinthFileUrl(string value) => IsHttps(value) &&
        new Uri(value).Host is "cdn.modrinth.com" or "github.com" or "raw.githubusercontent.com" or "gitlab.com";

    private async Task InstallQuiltAsync(PackPlan plan, string directory, string id, string mirror, CancellationToken ct)
    {
        string game = Uri.EscapeDataString(plan.Minecraft), loader = Uri.EscapeDataString(plan.LoaderVersion);
        string profile = await downloads.GetDownloadTextAsync([$"https://meta.quiltmc.org/v3/versions/loader/{game}/{loader}/profile/json"], ct);
        using var json = JsonDocument.Parse(profile);
        await minecraft.EnsureLoaderLibrariesAsync(json.RootElement, ct, mirror);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.loader.json"), profile, ct);
    }

    private static IReadOnlyList<string> BuildForgeInstallerRoutes(PackPlan plan, string coordinates, string officialUrl, string mirror)
    {
        if (mirror == "官方源") return [officialUrl];
        bool auto = mirror.StartsWith("最优", StringComparison.Ordinal);
        bool officialFirst = mirror == "最优/官方源";
        if (plan.Loader == "NeoForge" && plan.Minecraft != "1.20.1")
        {
            string bmclNeo = $"https://bmclapi2.bangbang93.com/neoforge/version/{Uri.EscapeDataString(plan.LoaderVersion)}/download/installer.jar";
            string bmclMaven = $"https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{plan.LoaderVersion}/neoforge-{plan.LoaderVersion}-installer.jar";
            return new RouteList(officialFirst ? [officialUrl, bmclNeo, bmclMaven] : [bmclNeo, bmclMaven, officialUrl], auto);
        }
        return DownloadRoutes.Files(officialUrl, mirror, verified: true);
    }

    private static string? TryReadLoaderId(string jarPath)
    {
        try
        {
            using var jar = ZipFile.OpenRead(jarPath);
            using var manifest = ReadManifest(jar, "version.json");
            return Text(manifest.RootElement, "id");
        }
        catch { return null; }
    }

    private async Task PrepopulateForgeLibrariesAsync(string installerPath, string stagingRoot, string mirror,
        IProgress<(int Progress, string Speed)> progress, CancellationToken ct)
    {
        var specs = new Dictionary<string, (string Url, string Sha1, long Size)>(StringComparer.OrdinalIgnoreCase);
        using (var jar = ZipFile.OpenRead(installerPath))
        {
            foreach (string entryName in new[] { "install_profile.json", "version.json" })
            {
                var entry = jar.GetEntry(entryName);
                if (entry == null || entry.Length > 8 * 1024 * 1024) continue;
                try
                {
                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Array) continue;
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("downloads", out var dl) || !dl.TryGetProperty("artifact", out var art)) continue;
                        string relPath = Text(art, "path");
                        string libUrl = Text(art, "url");
                        string sha1 = Text(art, "sha1");
                        long size = Number(art, "size");
                        if (relPath.Length == 0 || libUrl.Length == 0 || !IsHttps(libUrl)) continue;
                        if (Path.IsPathRooted(relPath) || relPath.Replace('\\', '/').Split('/').Any(part => !InstanceManager.IsFileName(part))) continue;
                        specs.TryAdd(relPath.Replace('\\', '/'), (libUrl, sha1, size));
                    }
                }
                catch { }
            }
        }
        if (specs.Count == 0) return;

        int done = 0;
        int total = specs.Count;
        progress.Report((53, $"加速预载加载器依赖 0/{total}"));
        await Parallel.ForEachAsync(specs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (kv, token) =>
        {
            string relPath = kv.Key;
            var (libUrl, sha1, size) = kv.Value;
            string sharedDest = Path.Combine(storage.LibrariesDir, relPath.Replace('/', Path.DirectorySeparatorChar));
            string stagingDest = Path.Combine(stagingRoot, "libraries", relPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                bool verified = sha1.Length == 40 && sha1.All(Uri.IsHexDigit);
                var routes = DownloadRoutes.Files(libUrl, mirror, verified);
                await downloads.DownloadVerifiedFileAsync(routes, sharedDest, sha1, null, size > 0 ? size : null, null, token);
                if (File.Exists(sharedDest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(stagingDest)!);
                    File.Copy(sharedDest, stagingDest, overwrite: true);
                }
            }
            catch when (!token.IsCancellationRequested) { }
            int cur = Interlocked.Increment(ref done);
            if (cur == total || cur % 4 == 0)
                progress.Report((53 + (int)(cur * 6.0 / total), $"加速预载加载器依赖 {cur}/{total}"));
        });
    }

    private async Task InstallForgeAsync(PackPlan plan, JavaInstallation java, string work, string directory, string id,
        string mirror, IProgress<(int Progress, string Speed)> progress, CancellationToken ct)
    {
        string coordinates = plan.LoaderVersion.StartsWith(plan.Minecraft + "-", StringComparison.Ordinal)
            ? plan.LoaderVersion : $"{plan.Minecraft}-{plan.LoaderVersion}";
        string url = plan.Loader switch
        {
            "Forge" => $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar",
            "NeoForge" when plan.Minecraft == "1.20.1" =>
                $"https://maven.neoforged.net/releases/net/neoforged/forge/{coordinates}/forge-{coordinates}-installer.jar",
            _ => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{plan.LoaderVersion}/neoforge-{plan.LoaderVersion}-installer.jar"
        };
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        string cachedInstaller = Path.Combine(storage.CacheDir, "loaders", $"{plan.Loader.ToLowerInvariant()}-{cacheKey}.jar");
        string installer = Path.Combine(work, "loader-installer.jar");

        string? loaderId = File.Exists(cachedInstaller) ? TryReadLoaderId(cachedInstaller) : null;
        if (!string.IsNullOrEmpty(loaderId))
        {
            File.Copy(cachedInstaller, installer, overwrite: true);
        }
        else
        {
            if (File.Exists(cachedInstaller)) { try { File.Delete(cachedInstaller); } catch { } }
            progress.Report((51, $"下载 {plan.Loader} 安装器"));
            var routes = BuildForgeInstallerRoutes(plan, coordinates, url, mirror);
            await downloads.DownloadVerifiedFileAsync(routes, installer, null, null, null, null, ct);
            loaderId = TryReadLoaderId(installer);
            if (string.IsNullOrEmpty(loaderId))
                throw new InvalidDataException($"下载的 {plan.Loader} 安装器已损坏或格式无效。");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachedInstaller)!);
                File.Copy(installer, cachedInstaller, overwrite: true);
            }
            catch { }
        }

        if (!InstanceManager.IsFileName(loaderId) || loaderId == plan.Minecraft)
            throw new InvalidDataException($"{plan.Loader} 安装器版本标识无效。");

        string root = Path.Combine(work, "forge");
        string baseDir = Path.Combine(root, "versions", plan.Minecraft);
        Directory.CreateDirectory(baseDir);
        File.Copy(Path.Combine(directory, $"{id}.json"), Path.Combine(baseDir, $"{plan.Minecraft}.json"));
        File.Copy(Path.Combine(directory, $"{id}.jar"), Path.Combine(baseDir, $"{plan.Minecraft}.jar"));
        await File.WriteAllTextAsync(Path.Combine(root, "launcher_profiles.json"), """{"profiles":{}}""", ct);

        await PrepopulateForgeLibrariesAsync(installer, root, mirror, progress, ct);
        progress.Report((60, $"运行官方 {plan.Loader} 安装器"));

        string javaConsole = Path.Combine(Path.GetDirectoryName(java.Path)!, "java.exe");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(javaConsole) ? javaConsole : java.Path,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-Djava.awt.headless=true", "-jar", installer, "--installClient", root }
        };
        using var process = Process.Start(start) ?? throw new IOException($"无法启动 {plan.Loader} 安装器。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw;
        }

        string loaderDir = Path.Combine(root, "versions", loaderId);
        string profile = Path.Combine(loaderDir, $"{loaderId}.json");
        if (process.ExitCode != 0 || !File.Exists(profile))
        {
            storage.Log($"{plan.Loader} installer exit {process.ExitCode}\n{await output}\n{await errors}");
            throw new InvalidOperationException($"{plan.Loader} {plan.LoaderVersion} 安装失败，详情见 DCL 日志。");
        }
        await Task.WhenAll(output, errors);
        string downloadedLibraries = Path.Combine(root, "libraries");
        if (Directory.Exists(downloadedLibraries))
        {
            foreach (string file in Directory.EnumerateFiles(downloadedLibraries, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(storage.LibrariesDir, Path.GetRelativePath(downloadedLibraries, file));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Move(file, target); }
                catch (IOException) when (File.Exists(target)) { }
            }
        }

        File.Copy(profile, Path.Combine(directory, $"{id}.loader.json"));
        string overlayJar = Path.Combine(loaderDir, $"{loaderId}.jar");
        if (File.Exists(overlayJar)) File.Copy(overlayJar, Path.Combine(directory, $"{id}.loader.jar"));
    }

    private sealed class CoreProgress(IProgress<(int Progress, string Speed)> parent) : IProgress<(int Progress, string Speed)>
    {
        public void Report((int Progress, string Speed) value) =>
            parent.Report((10 + value.Progress * 38 / 100, value.Speed));
    }
}

