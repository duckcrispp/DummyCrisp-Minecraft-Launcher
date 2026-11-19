using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

internal sealed record InstalledPack(string Name, string McVersion, string Loader, string LoaderVersion, int JavaMajor)
{
    internal const string FileName = ".dcl-pack.json";
}

internal sealed class ModpackInstaller(StorageService storage, DownloadManager downloads,
    MinecraftApiService minecraft, CurseForgeService curseForge)
{
    private sealed record PackFile(string Path, string[] Urls, string Sha1, string? Sha512, long Size);
    private sealed record PackPlan(string Name, string Minecraft, string Loader, string LoaderVersion,
        List<PackFile> Files, string Overrides, string? ClientOverrides);

    internal static string InstanceId(string project, string build) =>
        "pack-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{project}:{build}")))[..16].ToLowerInvariant();

    internal async Task InstallAsync(string id, string source, string name, ResourceFile build, string mirror,
        IReadOnlyList<JavaInstallation> javas, string curseForgeKey,
        IProgress<(int Progress, string Speed)> progress, CancellationToken ct, string curseForgeRoute = "auto")
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
            string archivePath = Path.Combine(work, "pack.zip");
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
                plan.Loader == "Fabric" ? plan.LoaderVersion : null, mirror, new CoreProgress(progress), ct, instance);

            using var baseProfile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(instance, $"{id}.json"), ct));
            int javaMajor = baseProfile.RootElement.TryGetProperty("javaVersion", out var java)
                ? (int)Number(java, "majorVersion") : 0;
            if (javaMajor <= 0) javaMajor = InstanceManager.RequiredJavaForMcVersion(plan.Minecraft);

            if (plan.Loader == "Quilt")
                await InstallQuiltAsync(plan, instance, id, ct);
            else if (plan.Loader is "Forge" or "NeoForge")
            {
                progress.Report((50, $"运行官方 {plan.Loader} 安装器"));
                var runtime = javas.FirstOrDefault(item => JavaManager.ParseMajor(item.Version) == javaMajor && File.Exists(item.Path))
                    ?? throw new InvalidOperationException($"此整合包的 {plan.Loader} 安装器需要 Java {javaMajor}。请在「实例环境 → 账号与 Java」安装后重试。");
                await InstallForgeAsync(plan, runtime, work, instance, id, ct);
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
            if (buildName.Length > 0 && !display.Equals(buildName, StringComparison.OrdinalIgnoreCase))
                display += " · " + buildName;
            var metadata = new InstalledPack(display, plan.Minecraft, plan.Loader, plan.LoaderVersion, javaMajor);
            await File.WriteAllTextAsync(Path.Combine(instance, InstalledPack.FileName),
                JsonSerializer.Serialize(metadata, StorageService.JsonOpts), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(instance, final);
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
        var resolved = await curseForge.GetPackFilesAsync(projectsByFile, key, ct, route);
        var files = resolved.Select(item =>
        {
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

    private Task DownloadPackFileAsync(PackFile file, string destination, string mirror, CancellationToken ct)
    {
        bool verified = file.Sha1.Length > 0 || file.Sha512 is { Length: > 0 };
        var routes = file.Urls.SelectMany(url => DownloadRoutes.Files(url, mirror, verified)).Distinct().ToArray();
        return downloads.DownloadVerifiedFileAsync(routes, destination, file.Sha1, file.Sha512, file.Size, null, ct);
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

    private async Task InstallQuiltAsync(PackPlan plan, string directory, string id, CancellationToken ct)
    {
        string game = Uri.EscapeDataString(plan.Minecraft), loader = Uri.EscapeDataString(plan.LoaderVersion);
        string profile = await downloads.GetDownloadTextAsync([$"https://meta.quiltmc.org/v3/versions/loader/{game}/{loader}/profile/json"], ct);
        using var json = JsonDocument.Parse(profile);
        await minecraft.EnsureLoaderLibrariesAsync(json.RootElement, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{id}.loader.json"), profile, ct);
    }

    private async Task InstallForgeAsync(PackPlan plan, JavaInstallation java, string work, string directory, string id,
        CancellationToken ct)
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
        string installer = Path.Combine(work, "loader-installer.jar");
        await downloads.DownloadFileWithSha1Async(url, installer, null, null, ct);

        string loaderId;
        using (var jar = ZipFile.OpenRead(installer))
        using (var manifest = ReadManifest(jar, "version.json"))
            loaderId = Text(manifest.RootElement, "id");
        if (!InstanceManager.IsFileName(loaderId) || loaderId == plan.Minecraft)
            throw new InvalidDataException($"{plan.Loader} 安装器版本标识无效。");

        string root = Path.Combine(work, "forge");
        string baseDir = Path.Combine(root, "versions", plan.Minecraft);
        Directory.CreateDirectory(baseDir);
        File.Copy(Path.Combine(directory, $"{id}.json"), Path.Combine(baseDir, $"{plan.Minecraft}.json"));
        File.Copy(Path.Combine(directory, $"{id}.jar"), Path.Combine(baseDir, $"{plan.Minecraft}.jar"));
        await File.WriteAllTextAsync(Path.Combine(root, "launcher_profiles.json"), """{"profiles":{}}""", ct);

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

