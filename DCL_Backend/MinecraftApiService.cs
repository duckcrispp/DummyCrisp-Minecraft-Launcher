using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

public sealed partial class MinecraftApiService
{
    internal const int AssetDownloadConcurrency = 16;
    private readonly StorageService _storage;
    private readonly HttpClient _http;
    private readonly DownloadManager _dl;
    private readonly ConcurrentDictionary<(string Category, string ProjectId), (DateTimeOffset Expires, Dictionary<string, object?> Item)> _searchProjects = new();

    public MinecraftApiService(StorageService storage, HttpClient http, DownloadManager dl)
    {
        _storage = storage;
        _http = http;
        _dl = dl;
    }

    private Task DownloadMirroredFileAsync(string url, string destination, string? sha1, string mirror,
        Action<long, long?>? progress, CancellationToken ct, long? size = null) =>
        _dl.DownloadVerifiedFileAsync(DownloadRoutes.Files(url, mirror, !string.IsNullOrWhiteSpace(sha1)),
            destination, sha1, null, size, progress, ct);

    public async Task<List<Dictionary<string, object?>>> FetchVersionManifestAsync(string mirror, CancellationToken ct)
    {
        const string url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        string cachePath = Path.Combine(_storage.ManifestsCacheDir, "version_manifest_v2.json");
        string jsonText;
        try
        {
            jsonText = await _dl.GetDownloadTextAsync(DownloadRoutes.Files(url, mirror, true), ct, text =>
            {
                using var manifest = JsonDocument.Parse(text);
                var root = manifest.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("latest", out var latest) ||
                    latest.ValueKind != JsonValueKind.Object || Text(latest, "release").Length == 0 || Text(latest, "snapshot").Length == 0 ||
                    !root.TryGetProperty("versions", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0 ||
                    entries.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Object || Text(v, "id").Length == 0 ||
                        Text(v, "type").Length == 0 || Text(v, "releaseTime").Length == 0 ||
                        !Uri.TryCreate(Text(v, "url"), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidDataException("版本清单格式无效");
            });
            string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, jsonText, ct);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        catch when (!ct.IsCancellationRequested && File.Exists(cachePath))
        {
            jsonText = await File.ReadAllTextAsync(cachePath, ct);
        }

        return ParseVersions(jsonText);
    }

    internal List<Dictionary<string, object?>> CachedVersions()
    {
        string path = Path.Combine(_storage.ManifestsCacheDir, "version_manifest_v2.json");
        if (!File.Exists(path)) return [];
        try { return ParseVersions(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    private static List<Dictionary<string, object?>> ParseVersions(string jsonText)
    {
        using var doc = JsonDocument.Parse(jsonText);
        string latestRelease = doc.RootElement.GetProperty("latest").GetProperty("release").GetString() ?? "";
        string latestSnapshot = doc.RootElement.GetProperty("latest").GetProperty("snapshot").GetString() ?? "";

        var list = new List<Dictionary<string, object?>>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            string id = v.GetProperty("id").GetString() ?? "";
            string type = v.GetProperty("type").GetString() ?? "release";
            string relTimeRaw = v.GetProperty("releaseTime").GetString() ?? "";
            string formattedTime = DateTimeOffset.TryParse(relTimeRaw, out var dt)
                ? dt.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
                : relTimeRaw;

            string category = type switch
            {
                "release" => "release",
                "snapshot" => id.Contains("w14", StringComparison.OrdinalIgnoreCase) || id.Contains("potato") || id.Contains("infinite") || id.Contains("_or_") ? "april" : "snapshot",
                _ => "ancient"
            };
            string typeLabel = id == latestRelease ? "最新正式版" : id == latestSnapshot ? "最新预览版" : category switch
            {
                "release" => "正式版",
                "snapshot" => "预览版",
                "april" => "愚人节版",
                _ => "远古版"
            };
            string icon = category switch
            {
                "release" => "assets/mc-icons/grass.png",
                "snapshot" => "assets/mc-icons/command.png",
                "april" => "assets/mc-icons/tnt.png",
                _ => "assets/mc-icons/bedrock.png"
            };

            string[] verLoaders = category switch
            {
                "ancient" or "april" => [],
                "snapshot" => DateTimeOffset.TryParse(relTimeRaw, out var sDt) && sDt >= new DateTimeOffset(2018, 10, 24, 0, 0, 0, TimeSpan.Zero)
                    ? (sDt >= new DateTimeOffset(2023, 10, 1, 0, 0, 0, TimeSpan.Zero) ? ["Fabric", "NeoForge"] : ["Fabric"])
                    : [],
                _ => VersionComparer.Compare(id, "1.20.1") >= 0
                    ? ["Fabric", "NeoForge", "Forge", "Quilt", "OptiFine"]
                    : VersionComparer.Compare(id, "1.14") >= 0
                        ? ["Fabric", "Forge", "Quilt", "OptiFine"]
                        : VersionComparer.Compare(id, "1.13") >= 0
                            ? ["Forge", "OptiFine"]
                            : VersionComparer.Compare(id, "1.7.10") >= 0
                                ? ["OptiFine"]
                                : []
            };

            list.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["type"] = typeLabel,
                ["category"] = category,
                ["releaseTime"] = formattedTime,
                ["date"] = formattedTime.Length >= 10 ? formattedTime[..10] : formattedTime,
                ["icon"] = icon,
                ["url"] = v.GetProperty("url").GetString(),
                ["sha1"] = Text(v, "sha1"),
                ["loaders"] = verLoaders,
                ["java"] = "Java 21"
            });
        }
        return list.OrderByDescending(item => item["id"]?.ToString() ?? "", VersionComparer).ToList();
    }

    public async Task InstallVanillaOrFabricAsync(
        string mcVersion,
        string versionJsonUrl,
        string instanceName,
        string? fabricLoaderVer,
        string mirror,
        IProgress<(int Progress, string Speed)> progress,
        CancellationToken ct,
        string? stagingDirectory = null, string? versionSha1 = null)
    {
        string safeName = string.Concat(instanceName.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = mcVersion;

        // Per requirement 1: Every installed version owns its independent versions/<name> folder
        string verDir = stagingDirectory ?? _storage.GameDirectory(safeName);
        Directory.CreateDirectory(verDir);
        string nativesDir = Path.Combine(verDir, "natives");
        Directory.CreateDirectory(nativesDir);

        progress.Report((5, "获取版本清单"));
        string verJsonText = await _dl.GetDownloadTextAsync(DownloadRoutes.Files(versionJsonUrl, mirror, true), ct, text =>
        {
            using var manifest = JsonDocument.Parse(text);
            if (manifest.RootElement.ValueKind != JsonValueKind.Object || Text(manifest.RootElement, "id") != mcVersion)
                throw new InvalidDataException("服务器返回了不同的 Minecraft 版本");
        }, versionSha1);
        string verJsonPath = Path.Combine(verDir, $"{safeName}.json");

        using var doc = JsonDocument.Parse(verJsonText);
        var root = doc.RootElement;

        // 1. Download Client JAR
        if (root.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("client", out var client))
        {
            string cUrl = client.GetProperty("url").GetString() ?? "";
            string? cSha1 = client.TryGetProperty("sha1", out var s) ? s.GetString() : null;
            string clientJar = Path.Combine(verDir, $"{safeName}.jar");
            await DownloadMirroredFileAsync(cUrl, clientJar, cSha1, mirror, (rec, tot) =>
            {
                if (tot > 0)
                {
                    int pct = 8 + (int)(rec * 22 / tot.Value);
                    progress.Report((pct, $"{(rec / 1048576.0):F1} / {(tot.Value / 1048576.0):F1} MB"));
                }
            }, ct, Number(client, "size"));
        }

        // 2. Collect Libraries matching Windows x64 rules
        var libTasks = new List<(string Url, string Path, string? Sha1, long Size, bool IsNative)>();
        if (root.TryGetProperty("libraries", out var libs))
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!IsLibraryAllowedOnWindows(lib)) continue;
                if (lib.TryGetProperty("downloads", out var dlObj))
                {
                    if (dlObj.TryGetProperty("artifact", out var art))
                    {
                        string pathRel = art.GetProperty("path").GetString() ?? "";
                        string url = art.GetProperty("url").GetString() ?? "";
                        string? sha1 = art.TryGetProperty("sha1", out var s) ? s.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(pathRel) && !string.IsNullOrWhiteSpace(url))
                        {
                            libTasks.Add((url, LibraryDestination(pathRel), sha1, Number(art, "size"), false));
                        }
                    }
                    if (lib.TryGetProperty("natives", out var nat) && nat.TryGetProperty("windows", out var winKey)
                        && dlObj.TryGetProperty("classifiers", out var cls)
                        && cls.TryGetProperty((winKey.GetString() ?? "natives-windows").Replace("${arch}", "64"), out var natArt))
                    {
                        string pathRel = natArt.GetProperty("path").GetString() ?? "";
                        string url = natArt.GetProperty("url").GetString() ?? "";
                        string? sha1 = natArt.TryGetProperty("sha1", out var s) ? s.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(pathRel) && !string.IsNullOrWhiteSpace(url))
                        {
                            libTasks.Add((url, LibraryDestination(pathRel), sha1, Number(natArt, "size"), true));
                        }
                    }
                }
            }
        }

        // Parallel library download with bounded concurrency
        using var sem = new SemaphoreSlim(12);
        int doneLibs = 0;
        await Task.WhenAll(libTasks.Select(async lt =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await DownloadMirroredFileAsync(lt.Url, lt.Path, lt.Sha1, mirror, null, ct, lt.Size);
                if (lt.IsNative && File.Exists(lt.Path))
                {
                    ExtractNatives(lt.Path, nativesDir);
                }
                int count = Interlocked.Increment(ref doneLibs);
                int pct = 30 + (int)(count * 35.0 / Math.Max(1, libTasks.Count));
                progress.Report((pct, $"依赖库 {count}/{libTasks.Count}"));
            }
            finally { sem.Release(); }
        }));

        // 3. Asset Index & Objects (parallel)
        if (root.TryGetProperty("assetIndex", out var ai))
        {
            string idxId = ai.GetProperty("id").GetString() ?? mcVersion;
            string idxUrl = ai.GetProperty("url").GetString() ?? "";
            string? idxSha1 = ai.TryGetProperty("sha1", out var s) ? s.GetString() : null;
            string idxPath = Path.Combine(_storage.AssetsDir, "indexes", $"{idxId}.json");
            await DownloadMirroredFileAsync(idxUrl, idxPath, idxSha1, mirror, null, ct, Number(ai, "size"));

            using var idxDoc = JsonDocument.Parse(await File.ReadAllTextAsync(idxPath, ct));
            if (idxDoc.RootElement.TryGetProperty("objects", out var objects))
                await DownloadAssetsAsync(objects, mirror, progress, ct);
        }

        if (stagingDirectory != null && root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var clientLogging) &&
            clientLogging.TryGetProperty("file", out var logFile) &&
            InstanceManager.IsFileName(Text(logFile, "id")))
        {
            await DownloadMirroredFileAsync(Text(logFile, "url"),
                Path.Combine(_storage.AssetsDir, "log_configs", Text(logFile, "id")), Text(logFile, "sha1"), mirror, null, ct, Number(logFile, "size"));
        }

        // 4. Optional Fabric Loader profile overlay via meta.fabricmc.net
        if (!string.IsNullOrWhiteSpace(fabricLoaderVer))
        {
            progress.Report((98, "合成 Fabric 配置"));
            string fabUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(fabricLoaderVer)}/profile/json";
            string fabJson = await CatalogTextAsync(fabUrl, ct, mirror);
            using var profile = JsonDocument.Parse(fabJson);
            await EnsureLoaderLibrariesAsync(profile.RootElement, ct, mirror);
            await File.WriteAllTextAsync(Path.Combine(verDir, $"{safeName}.fabric.json"), fabJson, ct);
        }
        await File.WriteAllTextAsync(verJsonPath, verJsonText, ct);
    }

    private async Task DownloadAssetsAsync(JsonElement objects, string mirror,
        IProgress<(int Progress, string Speed)> progress, CancellationToken ct)
    {
        var assets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in objects.EnumerateObject())
        {
            string hash = Text(property.Value, "hash");
            if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
                assets.TryAdd(hash.ToLowerInvariant(), Number(property.Value, "size"));
        }

        int completed = 0;
        long nextReport = Environment.TickCount64 + 200;
        var reportLock = new object();
        progress.Report((65, $"资源文件 0/{assets.Count}"));

        await Parallel.ForEachAsync(assets,
            new ParallelOptions { MaxDegreeOfParallelism = AssetDownloadConcurrency, CancellationToken = ct },
            async (asset, token) =>
            {
                var (hash, size) = asset;
                string url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
                string destination = Path.Combine(_storage.AssetsDir, "objects", hash[..2], hash);
                await _dl.DownloadVerifiedFileAsync(DownloadRoutes.Files(url, mirror, true),
                    destination, hash, null, size, null, token);

                lock (reportLock)
                {
                    completed++;
                    long now = Environment.TickCount64;
                    if (now < nextReport && completed < assets.Count) return;
                    nextReport = now + 200;
                    int percent = 65 + completed * 33 / assets.Count;
                    progress.Report((percent, $"资源文件 {completed}/{assets.Count}"));
                }
            });
    }

    internal async Task EnsureLoaderLibrariesAsync(JsonElement profile, CancellationToken ct, string? source = null)
    {
        if (!profile.TryGetProperty("libraries", out var libraries)) return;
        await Task.WhenAll(libraries.EnumerateArray().Where(IsLibraryAllowedOnWindows).Select(async library =>
        {
            string path = LibraryArtifactPath(library);
            string destination = LibraryDestination(path);
            string repository = library.TryGetProperty("url", out var url)
                ? url.GetString() ?? "https://libraries.minecraft.net/" : "https://libraries.minecraft.net/";
            if (!Uri.TryCreate(repository, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("加载器库地址无效");
            string artifactUrl = repository.TrimEnd('/') + "/" + path.Replace('\\', '/');
            string sha1 = Text(library, "sha1"), sha256 = Text(library, "sha256"), sha512 = Text(library, "sha512");
            long size = Number(library, "size");
            if (library.TryGetProperty("downloads", out var ds) && ds.TryGetProperty("artifact", out var art))
            {
                artifactUrl = Text(art, "url", artifactUrl);
                sha1 = Text(art, "sha1", sha1);
                sha256 = Text(art, "sha256", sha256);
                sha512 = Text(art, "sha512", sha512);
                if (Number(art, "size") > 0) size = Number(art, "size");
            }
            bool verified = sha1.Length > 0 || sha256.Length > 0 || sha512.Length > 0;
            if (!verified && new Uri(artifactUrl).Host == "maven.fabricmc.net")
            {
                string checksumUrl = artifactUrl + ".sha1";
                string checksum = await CachedTextAsync(checksumUrl, [checksumUrl], text =>
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^[a-fA-F0-9]{40}(?:\s|$)"))
                        throw new InvalidDataException("Fabric Maven SHA-1 无效");
                }, ct);
                sha1 = checksum.Trim()[..40];
                verified = true;
            }
            if (!verified && size == 0 && File.Exists(destination)) return;
            await _dl.DownloadVerifiedFileAsync(DownloadRoutes.Files(artifactUrl, source ?? SelectSource(), verified),
                destination, sha1, sha512, size, null, ct, sha256: sha256);
        }));
    }

    private string LibraryDestination(string path)
    {
        if (Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Any(part => !InstanceManager.IsFileName(part)))
            throw new InvalidDataException("依赖库清单包含无效的文件路径");
        return Path.Combine(_storage.LibrariesDir, path.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static string LibraryArtifactPath(JsonElement library)
    {
        if (library.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("artifact", out var artifact))
            return artifact.GetProperty("path").GetString()!;
        string rawName = library.GetProperty("name").GetString() ?? "";
        string ext = "jar";
        int atIdx = rawName.LastIndexOf('@');
        if (atIdx > 0 && atIdx < rawName.Length - 1)
        {
            ext = rawName[(atIdx + 1)..];
            rawName = rawName[..atIdx];
        }
        string[] parts = rawName.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(part => !InstanceManager.IsFileName(part)) || !InstanceManager.IsFileName(ext))
            throw new InvalidDataException("加载器库坐标无效");
        string classifier = parts.Length == 4 ? "-" + parts[3] : "";
        return $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{classifier}.{ext}";
    }

    public async Task<(List<Dictionary<string, object?>> Items, int TotalHits)> SearchModrinthPagedAsync(
        string projectType,
        string query,
        string loaderFilter,
        int offset,
        int limit,
        CancellationToken ct,
        string sortBy = "downloads",
        string mcVersion = "",
        string resourceTag = "",
        string resourceSubTag = "")
    {
        string category = Category(projectType);
        string verFilter = string.IsNullOrWhiteSpace(mcVersion) || mcVersion.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ""
            : mcVersion.Trim();
        string mrTag = ResolveModrinthTag(category, resourceTag);
        string mrSubTag = ResolveModrinthTag(category, resourceSubTag);
        bool sortNewest = sortBy.Equals("newest", StringComparison.OrdinalIgnoreCase);

        var localized = FindLocalizedNames(category, query);
        if (localized.Length > 0)
        {
            var matched = await FindModrinthLocalizedProjectsAsync(projectType, category, localized, loaderFilter, verFilter, mrTag, mrSubTag, sortNewest, ct);
            var page = matched.Skip(offset).Take(limit).ToList();
            RememberSearchProjects(category, page);
            return (page, matched.Count);
        }

        string term = query.Trim();
        var facetGroups = new List<string[]> { new[] { $"project_type:{projectType}" } };
        if (projectType is "mod" or "modpack" && !loaderFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            facetGroups.Add([$"categories:{loaderFilter.ToLowerInvariant()}"]);
        if (mrTag.Length > 0)
            facetGroups.Add([$"categories:{mrTag}"]);
        if (mrSubTag.Length > 0 && !mrSubTag.Equals(mrTag, StringComparison.OrdinalIgnoreCase))
            facetGroups.Add([$"categories:{mrSubTag}"]);
        if (verFilter.Length > 0)
            facetGroups.Add([$"versions:{verFilter}"]);

        string facets = Uri.EscapeDataString(JsonSerializer.Serialize(facetGroups));
        string index = sortNewest ? "newest" : (term.Length > 0 && sortBy.Equals("relevance", StringComparison.OrdinalIgnoreCase) ? "relevance" : "downloads");
        string url = $"https://api.modrinth.com/v2/search?limit={Math.Clamp(limit, 10, 50)}&offset={Math.Max(0, offset)}&index={index}&facets={facets}&query={Uri.EscapeDataString(term)}";
        using var doc = JsonDocument.Parse(await CatalogTextAsync(url, ct));
        int totalHits = (int)Number(doc.RootElement, "total_hits");
        var list = Array(doc.RootElement, "hits").Select(hit =>
            CreateModrinthCatalogItem(category, projectType, hit, Text(hit, "project_id"), Strings(hit, "categories")
                .Select(Loader).Where(loader => loader is "Fabric" or "Forge" or "NeoForge" or "Quilt" or "Iris" or "OptiFine").Distinct().ToArray(),
                Strings(hit, "versions"))).ToList();
        RememberSearchProjects(category, list);
        return (list, totalHits);
    }

    private async Task<List<Dictionary<string, object?>>> FindModrinthLocalizedProjectsAsync(
        string projectType, string category, LocalizedResource[] localized, string loader, string verFilter, string mrTag, string mrSubTag, bool sortNewest, CancellationToken ct)
    {
        var ids = localized.SelectMany(item => item.Ids).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
        var ranking = ids.Select((id, index) => (Id: NormalizeId(id), Index: index))
            .DistinctBy(entry => entry.Id).ToDictionary(entry => entry.Id, entry => entry.Index);
        var hits = new List<(int Rank, DateTimeOffset Published, Dictionary<string, object?> Item)>();
        foreach (string[] batch in ids.Chunk(50))
        {
            string parameter = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
            using var doc = JsonDocument.Parse(await CatalogTextAsync($"https://api.modrinth.com/v2/projects?ids={parameter}", ct));
            foreach (var project in doc.RootElement.EnumerateArray())
            {
                string id = Text(project, "id");
                string slug = NormalizeId(Text(project, "slug"));
                if (id.Length == 0 || Text(project, "project_type") != projectType ||
                    Text(project, "status") is not ("approved" or "archived")) continue;
                if (!ranking.TryGetValue(slug, out int rank)) continue;
                string[] loaders = Strings(project, "loaders").Select(Loader).Distinct().ToArray();
                string[] gameVers = Strings(project, "game_versions");
                string[] cats = Strings(project, "categories");
                if (category is "mods" or "modpacks" && !loader.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                    !loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;
                if (mrTag.Length > 0 && !cats.Contains(mrTag, StringComparer.OrdinalIgnoreCase)) continue;
                if (mrSubTag.Length > 0 && !cats.Contains(mrSubTag, StringComparer.OrdinalIgnoreCase)) continue;
                if (verFilter.Length > 0 && !gameVers.Contains(verFilter, StringComparer.OrdinalIgnoreCase)) continue;
                DateTimeOffset created = Date(Text(project, "published"));
                hits.Add((rank, created, CreateModrinthCatalogItem(category, projectType, project, id, loaders, gameVers)));
            }
        }
        return (sortNewest
            ? hits.OrderByDescending(hit => hit.Published).ThenBy(hit => hit.Rank)
            : hits.OrderBy(hit => hit.Rank))
            .Select(hit => hit.Item).DistinctBy(item => item["id"]).ToList();
    }

    private static Dictionary<string, object?> CreateModrinthCatalogItem(
        string category, string projectType, JsonElement project, string projectId, string[] loaders, string[] versions)
    {
        string slug = Text(project, "slug", projectId);
        string enName = Text(project, "title", slug);
        string chineseName = ChineseName(category, slug, enName);
        long dlCount = Number(project, "downloads");
        string dlText = DownloadCount(dlCount);
        return new Dictionary<string, object?>
        {
            ["id"] = $"mr-{projectId}", ["projectId"] = projectId, ["slug"] = slug,
            ["cnName"] = chineseName, ["pinyin"] = PinyinForName(chineseName),
            ["enName"] = enName, ["summary"] = Text(project, "description"),
            ["author"] = Text(project, "author"), ["icon"] = Text(project, "icon_url", "assets/mc-icons/cobblestone.png"),
            ["downloads"] = dlText, ["loaders"] = loaders, ["mcVersions"] = VersionRange(versions),
            ["source"] = ResolveSourceLabel(category, slug, enName, "Modrinth"),
            ["webUrl"] = $"https://modrinth.com/{projectType}/{Uri.EscapeDataString(slug)}"
        };
    }

    private void RememberSearchProjects(string category, List<Dictionary<string, object?>> items)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(5);
        foreach (var item in items)
        {
            if (item["projectId"] is string { Length: > 0 } id)
                _searchProjects[(category, id)] = (expires, item);
        }
        int excess = _searchProjects.Count - 500;
        if (excess > 0)
        {
            foreach (var item in _searchProjects.OrderBy(entry => entry.Value.Expires).Take(excess))
                _searchProjects.TryRemove(item);
        }
    }

    internal async Task<(Dictionary<string, object?> Project, List<ResourceFile> Files)> GetModrinthResourceAsync(
        string category, string projectId, CancellationToken ct)
    {
        string path = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}";
        var filesTask = CatalogTextAsync(path + "/version", ct);
        Dictionary<string, object?> item;
        if (_searchProjects.TryGetValue((category, projectId), out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            item = new Dictionary<string, object?>(cached.Item);
        else
        {
            var projectTask = ReadModrinthProjectAsync(category, projectId, path, ct);
            await Task.WhenAll(projectTask, filesTask);
            item = await projectTask;
        }
        string webUrl = (string)item["webUrl"]!;

        using var versions = JsonDocument.Parse(await filesTask);
        var files = new List<ResourceFile>();
        var depIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions.RootElement.EnumerateArray())
        {
            var entries = Array(version, "files");
            if (category == "modpacks")
                entries = entries.Where(file => Text(file, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length == 0) continue;
            var primary = entries.FirstOrDefault(f => f.TryGetProperty("primary", out var flag) && flag.ValueKind == JsonValueKind.True);
            if (primary.ValueKind == JsonValueKind.Undefined) primary = entries[0];
            var loaders = Strings(version, "loaders").Select(Loader).Distinct().ToArray();
            if (category == "mods" && !loaders.Any(loader => loader is "Fabric" or "Quilt" or "Forge" or "NeoForge" or "LiteLoader" or "Rift")) continue;

            var deps = Array(version, "dependencies")
                .Select(dep => new ResourceDependency(Text(dep, "project_id"), Text(dep, "dependency_type").ToLowerInvariant()))
                .Where(dep => dep.ProjectId.Length > 0 && dep.DependencyType is "required" or "optional")
                .ToArray();
            foreach (var dep in deps) depIds.Add(dep.ProjectId);

            string releaseType = Text(version, "version_type", "release");
            files.Add(new ResourceFile(
                Text(version, "id"), Text(version, "name", Text(version, "version_number")),
                Text(version, "version_number"), Text(primary, "filename"), Text(primary, "url"),
                primary.TryGetProperty("hashes", out var hashes) ? Text(hashes, "sha1") : "",
                Strings(version, "game_versions"), loaders, Date(Text(version, "date_published")), Number(primary, "size"),
                char.ToUpperInvariant(releaseType[0]) + releaseType[1..], Number(version, "downloads"), deps));
        }

        var depMetaMap = await FetchModrinthProjectsBatchAsync(depIds, category, ct);
        var links = new Dictionary<string, string> { ["modrinth"] = webUrl };
        AddDetails(item, category, files, links, depMetaMap);
        return (item, files);
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> FetchModrinthProjectsBatchAsync(
        IEnumerable<string> projectIds, string fallbackCategory, CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var ids = projectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToArray();
        if (ids.Length == 0) return map;
        try
        {
            foreach (string[] batch in ids.Chunk(50))
            {
                string param = Uri.EscapeDataString(JsonSerializer.Serialize(batch));
                using var doc = JsonDocument.Parse(await CatalogTextAsync($"https://api.modrinth.com/v2/projects?ids={param}", ct));
                foreach (var proj in doc.RootElement.EnumerateArray())
                {
                    string pid = Text(proj, "id");
                    if (pid.Length == 0) continue;
                    string pType = Text(proj, "project_type", "mod");
                    string cat = pType switch
                    {
                        "resourcepack" => "packs",
                        "shader" => "shaders",
                        "modpack" => "modpacks",
                        _ => "mods"
                    };
                    string slug = Text(proj, "slug", pid);
                    string cn = ChineseName(cat, slug);
                    long dlCount = Number(proj, "downloads");
                    string dlText = dlCount >= 1_000_000 ? $"{dlCount / 1_000_000.0:F1}M" : $"{Math.Max(1, dlCount / 1000)}K";
                    map[pid] = new Dictionary<string, object?>
                    {
                        ["id"] = $"mr-{pid}",
                        ["projectId"] = pid,
                        ["slug"] = slug,
                        ["catKey"] = cat,
                        ["enName"] = Text(proj, "title", slug),
                        ["cnName"] = cn,
                        ["summary"] = Text(proj, "description"),
                        ["icon"] = Text(proj, "icon_url", "assets/mc-icons/cobblestone.png"),
                        ["downloads"] = dlText,
                        ["source"] = "Modrinth"
                    };
                }
            }
        }
        catch
        {
            // Non-fatal: dependency resolution failure does not block resource version list.
        }
        return map;
    }

    internal async Task<Dictionary<string, object?>?> ResolveModProjectAsync(string slug, string name, string fileName, CancellationToken ct)
    {
        var candidates = new List<string>();
        void AddCand(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            string s = val.Trim().ToLowerInvariant();
            if (s.Length >= 2 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            {
                candidates.Add(s);
                if (s.Contains('_')) candidates.Add(s.Replace('_', '-'));
            }
        }
        AddCand(slug);
        AddCand(NormalizeId(name));

        foreach (string cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = await CatalogTextAsync($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(cand)}", ct);
                using var doc = JsonDocument.Parse(json);
                var proj = doc.RootElement;
                if (Text(proj, "project_type") == "mod")
                {
                    string pid = Text(proj, "id");
                    var item = CreateModrinthCatalogItem("mods", "mod", proj, pid,
                        Strings(proj, "loaders").Select(Loader).Distinct().ToArray(), Strings(proj, "game_versions"));
                    RememberSearchProjects("mods", [item]);
                    return item;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException) { }
        }

        string searchTerm = !string.IsNullOrWhiteSpace(name) ? name : Path.GetFileNameWithoutExtension(fileName);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var (items, _) = await SearchModrinthPagedAsync("mods", searchTerm, "all", 0, 5, ct, "relevance");
            if (items.Count > 0) return items[0];
        }
        return null;
    }

    private async Task<Dictionary<string, object?>> ReadModrinthProjectAsync(
        string category, string projectId, string path, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await CatalogTextAsync(path, ct));
        var project = doc.RootElement;
        string type = Text(project, "project_type");
        if (Category(type) != category && !Strings(project, "additional_project_types").Contains(ProjectType(category)))
            throw new InvalidDataException("此项目与当前资源分类不匹配");

        string slug = Text(project, "slug", projectId);
        string enName = Text(project, "title", slug);
        return new Dictionary<string, object?>
        {
            ["id"] = $"mr-{projectId}", ["projectId"] = projectId, ["slug"] = slug, ["source"] = "Modrinth",
            ["enName"] = enName, ["cnName"] = ChineseName(category, slug, enName),
            ["summary"] = Text(project, "description"), ["webUrl"] = $"https://modrinth.com/{type}/{Uri.EscapeDataString(slug)}",
            ["icon"] = Text(project, "icon_url", "assets/mc-icons/cobblestone.png"), ["downloads"] = DownloadCount(Number(project, "downloads"))
        };
    }

    public async Task<Dictionary<string, List<Dictionary<string, object?>>>> FetchVersionLoaderBuildsAsync(string mcVersion, CancellationToken ct)
    {
        string source = SelectSource();
        var fabricTask = FetchFabricBuildsAsync(mcVersion, ct);
        var neoForgeTask = FetchNeoForgeBuildsAsync(mcVersion, source, ct);
        var forgeTask = FetchForgeBuildsAsync(mcVersion, source, ct);
        var quiltTask = FetchQuiltBuildsAsync(mcVersion, ct);
        var optiFineTask = FetchOptiFineBuildsAsync(mcVersion, ct);

        await Task.WhenAll(fabricTask, neoForgeTask, forgeTask, quiltTask, optiFineTask);

        var trees = new Dictionary<string, List<Dictionary<string, object?>>>();
        void AddLoader(string name, List<Dictionary<string, object?>> builds)
        {
            if (builds.Count > 0)
                trees[name] = [new() { ["mcVersion"] = mcVersion, ["title"] = $"{name} {mcVersion}", ["builds"] = builds }];
        }

        AddLoader("Fabric", fabricTask.Result);
        AddLoader("NeoForge", neoForgeTask.Result);
        AddLoader("Forge", forgeTask.Result);
        AddLoader("Quilt", quiltTask.Result);
        AddLoader("OptiFine", optiFineTask.Result);
        return trees;
    }

    private async Task<List<Dictionary<string, object?>>> FetchFabricBuildsAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            string url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            using var doc = JsonDocument.Parse(await CatalogTextAsync(url, ct));
            return doc.RootElement.EnumerateArray().Take(35).Select(el =>
            {
                string ver = el.GetProperty("loader").GetProperty("version").GetString() ?? "";
                bool stable = el.GetProperty("loader").TryGetProperty("stable", out var st) && st.GetBoolean();
                return new Dictionary<string, object?>
                {
                    ["id"] = $"{mcVersion}-Fabric-{ver}",
                    ["title"] = $"Fabric Loader {ver}",
                    ["file"] = $"fabric-loader-{ver}",
                    ["versionNumber"] = ver,
                    ["icon"] = "assets/mc-icons/loader-fabric.png",
                    ["updatedAgo"] = stable ? "推荐稳定版" : "测试构建",
                    ["size"] = "Meta",
                    ["releaseType"] = stable ? "Release" : "Beta"
                };
            }).Where(b => !string.IsNullOrWhiteSpace(b["versionNumber"]?.ToString())).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<List<Dictionary<string, object?>>> FetchNeoForgeBuildsAsync(string mcVersion, string source, CancellationToken ct)
    {
        if (VersionComparer.Compare(mcVersion, "1.20.1") < 0) return [];
        bool officialOnly = source == "官方源";
        string bmclUrl = $"https://bmclapi2.bangbang93.com/neoforge/list/{Uri.EscapeDataString(mcVersion)}";
        if (!officialOnly)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                string json = await CachedTextAsync(bmclUrl, [bmclUrl], text =>
                {
                    using var d = JsonDocument.Parse(text);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("NeoForge 列表无效");
                }, cts.Token);
                using var doc = JsonDocument.Parse(json);
                var builds = doc.RootElement.EnumerateArray()
                    .Select(el => Text(el, "version"))
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(v => v, VersionComparer)
                    .Take(40)
                    .Select(ver => BuildNeoForgeEntry(mcVersion, ver))
                    .ToList();
                if (builds.Count > 0) return builds;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException) { }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            bool is1201 = mcVersion == "1.20.1";
            string officialUrl = is1201
                ? "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/forge"
                : "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
            string json = await CachedTextAsync(officialUrl, [officialUrl], text =>
            {
                using var d = JsonDocument.Parse(text);
                if (!d.RootElement.TryGetProperty("versions", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("NeoForge 官方列表无效");
            }, cts.Token);
            using var doc = JsonDocument.Parse(json);
            var matched = new List<string>();
            string? prefix = NeoForgeVersionPrefix(mcVersion);
            foreach (var el in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                string raw = el.GetString() ?? "";
                if (raw.Length == 0) continue;
                if (is1201)
                {
                    if (raw.StartsWith("1.20.1-", StringComparison.Ordinal))
                        matched.Add(raw["1.20.1-".Length..]);
                }
                else if (prefix != null && raw.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matched.Add(raw);
                }
            }
            return matched
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, VersionComparer)
                .Take(40)
                .Select(ver => BuildNeoForgeEntry(mcVersion, ver))
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    private static string? NeoForgeVersionPrefix(string mcVersion)
    {
        var m = System.Text.RegularExpressions.Regex.Match(mcVersion, @"^(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        int major = int.Parse(m.Groups[1].Value);
        int minor = int.Parse(m.Groups[2].Value);
        int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (major == 1) return $"{minor}.{patch}.";
        return m.Groups[3].Success ? $"{major}.{minor}.{patch}." : $"{major}.{minor}.";
    }

    private static Dictionary<string, object?> BuildNeoForgeEntry(string mcVersion, string ver)
    {
        bool isBeta = ver.Contains("beta", StringComparison.OrdinalIgnoreCase) || ver.Contains("alpha", StringComparison.OrdinalIgnoreCase);
        return new Dictionary<string, object?>
        {
            ["id"] = $"{mcVersion}-NeoForge-{ver}",
            ["title"] = $"NeoForge {ver}",
            ["file"] = $"neoforge-{ver}-installer.jar",
            ["versionNumber"] = ver,
            ["icon"] = "assets/mc-icons/loader-neoforge.png",
            ["updatedAgo"] = isBeta ? "测试构建" : "官方安装器",
            ["size"] = "Installer",
            ["releaseType"] = isBeta ? "Beta" : "Release"
        };
    }

    private async Task<List<Dictionary<string, object?>>> FetchForgeBuildsAsync(string mcVersion, string source, CancellationToken ct)
    {
        if (VersionComparer.Compare(mcVersion, "1.13") < 0) return [];
        bool officialOnly = source == "官方源";
        string bmclUrl = $"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(mcVersion)}";
        if (!officialOnly)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                string json = await CachedTextAsync(bmclUrl, [bmclUrl], text =>
                {
                    using var d = JsonDocument.Parse(text);
                    if (d.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Forge 列表无效");
                }, cts.Token);
                using var doc = JsonDocument.Parse(json);
                var items = new List<(string Ver, string DateLabel)>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string ver = Text(el, "version");
                    if (ver.Length == 0) continue;
                    string modified = Text(el, "modified");
                    string dateLabel = DateTimeOffset.TryParse(modified, out var dt)
                        ? dt.ToLocalTime().ToString("yyyy/MM/dd")
                        : "官方构建";
                    items.Add((ver, dateLabel));
                }
                var builds = items
                    .GroupBy(x => x.Ver, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Last())
                    .OrderByDescending(x => x.Ver, VersionComparer)
                    .Take(40)
                    .Select(x => BuildForgeEntry(mcVersion, x.Ver, x.DateLabel))
                    .ToList();
                if (builds.Count > 0) return builds;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException) { }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            const string officialUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
            string xml = await CachedTextAsync(officialUrl, [officialUrl], text =>
            {
                if (!text.Contains("<versions>", StringComparison.Ordinal))
                    throw new InvalidDataException("Forge 官方元数据无效");
            }, cts.Token);
            string pattern = $@"<version>{System.Text.RegularExpressions.Regex.Escape(mcVersion)}-([^<]+)</version>";
            var builds = System.Text.RegularExpressions.Regex.Matches(xml, pattern)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => v, VersionComparer)
                .Take(40)
                .Select(ver => BuildForgeEntry(mcVersion, ver, "官方安装器"))
                .ToList();
            return builds;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    private static Dictionary<string, object?> BuildForgeEntry(string mcVersion, string ver, string dateLabel) =>
        new()
        {
            ["id"] = $"{mcVersion}-Forge-{ver}",
            ["title"] = $"Forge {ver}",
            ["file"] = $"forge-{mcVersion}-{ver}-installer.jar",
            ["versionNumber"] = ver,
            ["icon"] = "assets/mc-icons/loader-forge.png",
            ["updatedAgo"] = dateLabel,
            ["size"] = "Installer",
            ["releaseType"] = "Release"
        };

    private async Task<List<Dictionary<string, object?>>> FetchQuiltBuildsAsync(string mcVersion, CancellationToken ct)
    {
        if (VersionComparer.Compare(mcVersion, "1.14") < 0) return [];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
            string url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            string json = await CachedTextAsync(url, [url], text =>
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Quilt 列表无效");
            }, cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Take(35).Select(el =>
            {
                string ver = el.TryGetProperty("loader", out var ld) ? Text(ld, "version") : "";
                bool isBeta = ver.Contains("beta", StringComparison.OrdinalIgnoreCase) || ver.Contains("alpha", StringComparison.OrdinalIgnoreCase);
                return new Dictionary<string, object?>
                {
                    ["id"] = $"{mcVersion}-Quilt-{ver}",
                    ["title"] = $"Quilt Loader {ver}",
                    ["file"] = $"quilt-loader-{ver}",
                    ["versionNumber"] = ver,
                    ["icon"] = "assets/mc-icons/loader-fabric.png",
                    ["updatedAgo"] = isBeta ? "测试构建" : "推荐稳定版",
                    ["size"] = "Meta",
                    ["releaseType"] = isBeta ? "Beta" : "Release"
                };
            }).Where(b => !string.IsNullOrWhiteSpace(b["versionNumber"]?.ToString())).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    private async Task<List<Dictionary<string, object?>>> FetchOptiFineBuildsAsync(string mcVersion, CancellationToken ct)
    {
        if (VersionComparer.Compare(mcVersion, "1.7.10") < 0) return [];
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            string url = $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(mcVersion)}";
            string json = await CachedTextAsync(url, [url], text =>
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OptiFine 列表无效");
            }, cts.Token);
            using var doc = JsonDocument.Parse(json);
            var raw = new List<(string Type, string Patch, string File, string Forge, bool IsPreview)>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string type = Text(el, "type");
                string patch = Text(el, "patch");
                if (type.Length == 0 || patch.Length == 0) continue;
                string file = Text(el, "filename", $"OptiFine_{mcVersion}_{type}_{patch}.jar");
                string forge = Text(el, "forge");
                bool isPreview = file.StartsWith("preview_", StringComparison.OrdinalIgnoreCase) ||
                    patch.Contains("pre", StringComparison.OrdinalIgnoreCase);
                raw.Add((type, patch, file, forge, isPreview));
            }
            return raw
                .OrderBy(x => x.IsPreview)
                .ThenByDescending(x => $"{x.Type}_{x.Patch}", VersionComparer)
                .Take(35)
                .Select(x =>
                {
                    string ver = $"{x.Type}_{x.Patch}";
                    string compat = x.Forge.Length > 0 && !x.Forge.Contains("N/A", StringComparison.OrdinalIgnoreCase)
                        ? $"兼容 {x.Forge}"
                        : (x.IsPreview ? "预览构建" : "推荐稳定版");
                    return new Dictionary<string, object?>
                    {
                        ["id"] = $"{mcVersion}-OptiFine-{ver}",
                        ["title"] = $"OptiFine {ver}",
                        ["file"] = x.File,
                        ["versionNumber"] = ver,
                        ["optifineType"] = x.Type,
                        ["optifinePatch"] = x.Patch,
                        ["icon"] = "assets/mc-icons/loader-optifine.png",
                        ["updatedAgo"] = compat,
                        ["size"] = "Installer",
                        ["releaseType"] = x.IsPreview ? "Beta" : "Release"
                    };
                })
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    public static bool IsLibraryAllowedOnWindows(JsonElement lib)
    {
        if (!lib.TryGetProperty("rules", out var rules)) return true;
        bool allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("os", out var os))
            {
                string name = Text(os, "name");
                if (name.Length > 0 && name != "windows") continue;
                string architecture = Text(os, "arch");
                if (architecture.Length > 0 && architecture is not ("x64" or "amd64" or "x86_64")) continue;
                string version = Text(os, "version");
                if (version.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), version)) continue;
            }
            // Demo, custom-resolution and quick-play features are opt-in on the launcher side.
            if (rule.TryGetProperty("features", out var features) &&
                features.EnumerateObject().Any(feature => feature.Value.ValueKind == JsonValueKind.True)) continue;
            allowed = Text(rule, "action") == "allow";
        }
        return allowed;
    }

    private static void ExtractNatives(string jarPath, string destDir)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
                    continue;
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                string outPath = Path.Combine(destDir, entry.Name);
                entry.ExtractToFile(outPath, true);
            }
        }
        catch { }
    }
}
