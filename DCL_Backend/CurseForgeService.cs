using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

internal sealed record CurseForgeManualFile(
    int ProjectId, int FileId, string ProjectName, string FileName,
    string DownloadPageUrl, string TargetFolder, string Sha1, long FileSize);

internal sealed class CurseForgeManualFilesRequiredException(
    string message, IReadOnlyList<CurseForgeManualFile> missingFiles, int autoResolvedCount)
    : InvalidOperationException(message)
{
    public IReadOnlyList<CurseForgeManualFile> MissingFiles { get; } = missingFiles;
    public int AutoResolvedCount { get; } = autoResolvedCount;
}

internal sealed class CurseForgeService(HttpClient http)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, JsonElement Page)> _filePages = new();
    internal Func<string> SelectSource { get; set; } = () => "最优";
    private const string OfficialBaseUrl = "https://api.curseforge.com/v1/";
    private const string McimBaseUrl = "https://mod.mcimirror.top/curseforge/v1/";

    internal static string NormalizeRoute(string? route) => (route ?? "").Trim().ToLowerInvariant() switch
    {
        "official" => "official",
        "mcim" => "mcim",
        _ => "auto"
    };

    private static int ClassId(string category) => category switch
    {
        "mods" => 6, "packs" => 12, "shaders" => 6552, "modpacks" => 4471,
        _ => throw new ArgumentException("未知资源分类")
    };

    private static int LoaderId(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => 1, "fabric" => 4, "quilt" => 5, "neoforge" => 6, _ => 0
    };

    public async Task<(List<Dictionary<string, object?>> Items, int TotalHits)> SearchAsync(
        string category, string query, string loader, int offset, int limit, string key, CancellationToken ct,
        string sortBy = "downloads", string mcVersion = "", string route = "auto", string resourceTag = "", string resourceSubTag = "")
    {
        if (offset + limit > 10000) return ([], 10000);
        string verFilter = string.IsNullOrWhiteSpace(mcVersion) || mcVersion.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"&gameVersion={Uri.EscapeDataString(mcVersion.Trim())}";
        string loaderFilter = category is "mods" or "modpacks" && LoaderId(loader) is > 0 and var modLoader
            ? $"&modLoaderType={modLoader}" : "";
        int cfCatId = ResolveCurseForgeCategoryId(category, resourceTag);
        int cfSubCatId = ResolveCurseForgeCategoryId(category, resourceSubTag);
        int[] cfCatIds = new[] { cfCatId, cfSubCatId }.Where(id => id > 0).Distinct().ToArray();
        string catFilter = cfCatIds.Length >= 2
            ? $"&categoryIds={Uri.EscapeDataString($"[{string.Join(",", cfCatIds)}]")}"
            : cfCatIds.Length == 1 ? $"&categoryId={cfCatIds[0]}" : "";
        string filter = loaderFilter + verFilter + catFilter;
        // CurseForge ModsSearchSortField: 6 = TotalDownloads, 11 = ReleasedDate (NOT 3 = LastUpdated)
        int sortField = sortBy.Equals("newest", StringComparison.OrdinalIgnoreCase) ? 11 : 6;
        var translated = FindLocalizedNames(category, query);
        if (translated.Length > 0)
        {
            var slugs = translated.SelectMany(item => item.Ids).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            using var requests = new SemaphoreSlim(3);
            var lookups = slugs.Select(async slug =>
            {
                await requests.WaitAsync(ct);
                try
                {
                    using var response = await GetAsync($"mods/search?gameId=432&classId={ClassId(category)}&slug={Uri.EscapeDataString(slug)}&pageSize=1{filter}", key, route, ct);
                    return Array(response.RootElement, "data")
                        .Where(project => Number(project, "classId") == ClassId(category) &&
                            NormalizeId(Text(project, "slug")) == NormalizeId(slug))
                        .Select(project => Project(project, category)).FirstOrDefault();
                }
                finally
                {
                    requests.Release();
                }
            });
            var matches = (await Task.WhenAll(lookups)).OfType<Dictionary<string, object?>>()
                .DistinctBy(item => item["id"]).ToList();
            return (matches.Skip(offset).Take(limit).ToList(), matches.Count);
        }
        using var doc = await GetAsync($"mods/search?gameId=432&classId={ClassId(category)}&index={offset}&pageSize={limit}&sortField={sortField}&sortOrder=desc&searchFilter={Uri.EscapeDataString(query.Trim())}{filter}", key, route, ct);
        var mods = Array(doc.RootElement, "data").Where(mod => Number(mod, "classId") == ClassId(category))
            .Select(mod => Project(mod, category)).ToList();
        return (mods, Math.Min(10000, (int)Number(doc.RootElement.GetProperty("pagination"), "totalCount")));
    }

    public async Task<(Dictionary<string, object?> Project, List<ResourceFile> Files)> GetResourceAsync(
        string category, string projectId, string key, CancellationToken ct, string route = "auto", string? buildId = null)
    {
        if (!int.TryParse(projectId, out int id) || id <= 0) throw new ArgumentException("无效的 CurseForge 项目 ID");

        // 并行请求项目详情与首页文件列表，大幅缩短详情页首开耗时
        var projectTask = GetProjectAsync(category, projectId, key, route, ct);
        bool specific = int.TryParse(buildId, out int fileId) && fileId > 0;
        var firstFilesPageTask = GetAsync(specific ? $"mods/{id}/files/{fileId}" : $"mods/{id}/files?index=0&pageSize=50", key, route, ct);
        await Task.WhenAll(projectTask, firstFilesPageTask);

        using var project = projectTask.Result;
        using var firstPageDoc = firstFilesPageTask.Result;
        var mod = project.RootElement.GetProperty("data");
        bool allowDistribution = !mod.TryGetProperty("allowModDistribution", out var flag) || flag.ValueKind != JsonValueKind.False;
        List<ResourceFile> files;
        if (specific)
        {
            var selected = firstPageDoc.RootElement.GetProperty("data");
            if (Number(selected, "id") != fileId || Number(selected, "modId") != id)
                throw new InvalidDataException("CurseForge 返回的构建与所选项目不符");
            if (category == "modpacks" && selected.TryGetProperty("isServerPack", out var server) && server.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("所选构建是服务端整合包，请选择客户端构建。");
            files = [ParseFile(selected, allowDistribution)];
        }
        else files = await FilesFromFirstPageAsync(id, firstPageDoc, key, category, allowDistribution, route, ct);
        await FillMissingUrlsFromModrinthAsync(files, ct);
        var item = Project(mod, category);
        var links = new Dictionary<string, string>();
        if (item["webUrl"] is string { Length: > 0 } url) links["curseforge"] = url;
        var depIds = files.SelectMany(f => f.Dependencies ?? [])
            .Select(d => int.TryParse(d.ProjectId, out int mid) ? mid : 0)
            .Where(mid => mid > 0).Distinct().Take(30).ToArray();
        Dictionary<string, Dictionary<string, object?>> depMetaMap;
        if (depIds.Length > 0)
        {
            using var depCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            depCts.CancelAfter(TimeSpan.FromSeconds(3));
            depMetaMap = await FetchCurseForgeProjectsBatchAsync(depIds, key, route, depCts.Token);
        }
        else
        {
            depMetaMap = new(StringComparer.OrdinalIgnoreCase);
        }
        AddDetails(item, category, files, links, depMetaMap);
        return (item, files);
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> FetchCurseForgeProjectsBatchAsync(
        int[] modIds, string key, string route, CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        if (modIds.Length == 0) return map;
        if (NormalizeRoute(route) == "official" && string.IsNullOrWhiteSpace(key)) return map;
        try
        {
            foreach (int[] batch in modIds.Chunk(50))
            {
                using var doc = await RequestAsync(HttpMethod.Post, "mods", key, new { modIds = batch }, route, ct);
                foreach (var p in Array(doc.RootElement, "data"))
                {
                    int cid = checked((int)Number(p, "classId"));
                    string cat = cid switch { 12 => "packs", 6552 => "shaders", 4471 => "modpacks", _ => "mods" };
                    var proj = Project(p, cat);
                    proj["catKey"] = cat;
                    map[Number(p, "id").ToString()] = proj;
                }
            }
        }
        catch { }
        return map;
    }

    private async Task<JsonDocument> GetProjectAsync(string category, string projectId, string key, string route, CancellationToken ct)
    {
        if (!int.TryParse(projectId, out int id) || id <= 0) throw new ArgumentException("无效的 CurseForge 项目 ID");
        var doc = await GetAsync($"mods/{id}", key, route, ct);
        var project = doc.RootElement.GetProperty("data");
        if (Number(project, "gameId") == 432 && Number(project, "classId") == ClassId(category)) return doc;
        doc.Dispose();
        throw new InvalidDataException("此项目与当前资源分类不匹配");
    }

    private async Task<List<ResourceFile>> FilesFromFirstPageAsync(
        int projectId, JsonDocument firstPageDoc, string key, string category, bool allowDistribution, string route, CancellationToken ct)
    {
        const int size = 50;
        const int maxResults = 10000;
        const int maxAutoPages = 8; // 首页 50 + 最多 8 页（450 条最新构建），避免 2000+ 构建大型 Mod 详情页卡顿
        var files = new List<ResourceFile>(200);

        void AppendPage(JsonElement root)
        {
            var page = Array(root, "data");
            if (category != "modpacks")
                files.AddRange(page.Select(file => ParseFile(file, allowDistribution)));
            else
                files.AddRange(page.Where(file => !file.TryGetProperty("isServerPack", out var server) ||
                        server.ValueKind != JsonValueKind.True)
                    .Select(file => ParseFile(file, allowDistribution)));
        }

        AppendPage(firstPageDoc.RootElement);
        long totalCount = firstPageDoc.RootElement.TryGetProperty("pagination", out var pag) ? Number(pag, "totalCount") : files.Count;
        if (totalCount > size)
        {
            int totalExtraPages = (int)((Math.Min(maxResults, totalCount) - 1) / size);
            int targetPages = Math.Min(totalExtraPages, maxAutoPages);
            for (int pageStart = 1; pageStart <= totalExtraPages; pageStart += 4)
            {
                if (pageStart > targetPages && files.Count > 0) break;
                int batchEnd = Math.Min(files.Count == 0 ? totalExtraPages : targetPages, pageStart + 3);
                var batch = Enumerable.Range(pageStart, batchEnd - pageStart + 1);
                var results = await Task.WhenAll(batch.Select(async page =>
                {
                    string path = $"mods/{projectId}/files?index={page * size}&pageSize={size}";
                    string cacheKey = NormalizeRoute(route) + ":" + path;
                    if (_filePages.TryGetValue(cacheKey, out var cached) && cached.Time > DateTime.UtcNow.AddMinutes(-5))
                        return cached.Page;
                    using var doc = await GetAsync(path, key, route, ct);
                    var root = doc.RootElement.Clone();
                    if (!root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException($"CurseForge 构建第 {page + 1} 页无效");
                    if (_filePages.Count > 512)
                        foreach (var stale in _filePages.OrderBy(p => p.Value.Time).Take(64)) _filePages.TryRemove(stale.Key, out _);
                    _filePages[cacheKey] = (DateTime.UtcNow, root);
                    return root;
                }));
                foreach (var root in results) AppendPage(root);
            }
        }
        return files;
    }

    private static Dictionary<string, object?> Project(JsonElement mod, string category)
    {
        string id = Number(mod, "id").ToString();
        string slug = Text(mod, "slug");
        string enName = Text(mod, "name", slug);
        string cnName = ChineseName(category, slug, enName);
        return new()
        {
            ["id"] = $"cf-{id}", ["projectId"] = id, ["slug"] = slug,
            ["source"] = ResolveSourceLabel(category, slug, enName, "CurseForge"),
            ["cnName"] = cnName, ["pinyin"] = PinyinForName(cnName), ["enName"] = enName,
            ["summary"] = Text(mod, "summary"),
            ["author"] = string.Join(", ", Array(mod, "authors").Select(author => Text(author, "name"))),
            ["icon"] = mod.TryGetProperty("logo", out var image) && image.ValueKind == JsonValueKind.Object
                ? Text(image, "thumbnailUrl", "assets/mc-icons/cobblestone.png") : "assets/mc-icons/cobblestone.png",
            ["downloads"] = DownloadCount(Number(mod, "downloadCount")),
            ["mcVersions"] = VersionRange(Array(mod, "latestFilesIndexes").Select(index => Text(index, "gameVersion")).Where(v => v.Length > 0)),
            ["loaders"] = Array(mod, "latestFilesIndexes").Select(index => Number(index, "modLoader"))
                .Select(value => value switch { 1 => "Forge", 4 => "Fabric", 5 => "Quilt", 6 => "NeoForge", _ => "" })
                .Where(value => value.Length > 0).Distinct().ToArray(),
            ["webUrl"] = mod.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object ? Text(links, "websiteUrl") : ""
        };
    }

    private static ResourceFile ParseFile(JsonElement file, bool allowDistribution)
    {
        string fileName = Text(file, "fileName");
        string title = Text(file, "displayName", fileName);
        string[] tags = Strings(file, "gameVersions");
        var versions = tags.Where(tag => Regex.IsMatch(tag, @"^(?:\d+\.\d+|\d{2}w\d{2}[a-z])", RegexOptions.IgnoreCase)).ToArray();
        var loaders = tags.Select(Loader).Where(loader => LoaderId(loader) > 0).Distinct().ToArray();
        string sha1 = Array(file, "hashes").Where(hash => Number(hash, "algo") == 1)
            .Select(hash => Text(hash, "value")).FirstOrDefault() ?? "";
        string type = Number(file, "releaseType") switch { 2 => "Beta", 3 => "Alpha", _ => "Release" };
        string versionNumber = Regex.Match(title, @"(?<!\d)\d+\.\d+(?:\.\d+)?(?:[-+][\w.]+)?").Value;
        // CurseForge relationType: 3 = RequiredDependency, 2 = OptionalDependency
        var deps = Array(file, "dependencies")
            .Select(dep => (Id: Number(dep, "modId"), Rel: (int)Number(dep, "relationType")))
            .Where(dep => dep.Id > 0 && dep.Rel is 3 or 2)
            .Select(dep => new ResourceDependency(dep.Id.ToString(), dep.Rel == 3 ? "required" : "optional"))
            .ToArray();
        return new(Number(file, "id").ToString(), title, versionNumber, fileName,
            allowDistribution ? Text(file, "downloadUrl") : "", sha1, versions, loaders,
            Date(Text(file, "fileDate")), Number(file, "fileLength"), type, Number(file, "downloadCount"), deps);
    }

    /// <summary>
    /// Fills empty FileUrl entries on files dual-hosted on Modrinth by querying Modrinth's bulk SHA-1 endpoint.
    /// </summary>
    private async Task<int> FillMissingUrlsFromModrinthAsync(IList<ResourceFile> files, CancellationToken ct)
    {
        var candidates = new List<(int Index, string Sha1)>();
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (f.FileUrl.Length == 0 && f.Sha1.Length == 40 && f.Sha1.All(Uri.IsHexDigit))
                candidates.Add((i, f.Sha1.ToLowerInvariant()));
        }
        if (candidates.Count == 0) return 0;

        var urlBySha1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] batch in candidates.Select(c => c.Sha1).Distinct().Chunk(100))
        {
            foreach (string endpoint in DownloadRoutes.Files("https://api.modrinth.com/v2/version_files", SelectSource(), true))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    req.Content = JsonContent.Create(new { hashes = batch, algorithm = "sha1" });
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(4));
                    using var resp = await http.SendAsync(req, cts.Token);
                    if (!resp.IsSuccessStatusCode) continue;
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        string? chosenUrl = null;
                        foreach (var mrFile in Array(prop.Value, "files"))
                        {
                            string mrUrl = Text(mrFile, "url");
                            if (!Uri.TryCreate(mrUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;
                            string mrSha1 = mrFile.TryGetProperty("hashes", out var hObj) ? Text(hObj, "sha1") : "";
                            if (mrSha1.Equals(prop.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                chosenUrl = mrUrl;
                                break;
                            }
                        }
                        if (!string.IsNullOrEmpty(chosenUrl))
                            urlBySha1[prop.Name] = chosenUrl;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException or JsonException) { }
                if (batch.All(urlBySha1.ContainsKey)) break;
            }
        }

        int resolved = 0;
        foreach (var (index, sha1) in candidates)
        {
            if (urlBySha1.TryGetValue(sha1, out string? url))
            {
                files[index] = files[index] with { FileUrl = url };
                resolved++;
            }
        }
        return resolved;
    }

    private static string? FindVerifiedLocalFile(ResourceFile file, IReadOnlyList<string>? localSearchDirs)
    {
        if (localSearchDirs == null || localSearchDirs.Count == 0 || !InstanceManager.IsFileName(file.File) || file.Sha1.Length != 40) return null;
        string expectedName = file.File;
        string stem = Path.GetFileNameWithoutExtension(expectedName);
        string ext = Path.GetExtension(expectedName);
        foreach (string dir in localSearchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            var candidates = new List<string>();
            string direct = Path.Combine(dir, expectedName);
            if (File.Exists(direct)) candidates.Add(direct);
            try
            {
                // Browsers save duplicates as "<stem> (1)<ext>"
                if (stem.Length > 0 && ext.Length > 0)
                {
                    foreach (string match in Directory.EnumerateFiles(dir, $"{stem}*{ext}", SearchOption.TopDirectoryOnly))
                    {
                        if (!match.Equals(direct, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(match);
                    }
                }
            }
            catch { }

            foreach (string path in candidates)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (file.Size > 0 && info.Length != file.Size) continue;
                    if (file.Sha1.Length == 40)
                    {
                        if (DownloadManager.ComputeSha1Hex(path).Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                            return path;
                    }
                }
                catch { }
            }
        }
        return null;
    }

    internal async Task<List<(string Path, ResourceFile File, string? LocalPath)>> GetPackFilesAsync(
        IReadOnlyDictionary<int, int> projectsByFile, string key, CancellationToken ct,
        string route = "auto", IReadOnlyList<string>? localSearchDirs = null)
    {
        var files = new Dictionary<int, ResourceFile>();
        foreach (var batch in projectsByFile.Keys.Chunk(50))
        {
            using var doc = await RequestAsync(HttpMethod.Post, "mods/files", key, new { fileIds = batch }, route, ct);
            foreach (var file in Array(doc.RootElement, "data"))
            {
                int id = checked((int)Number(file, "id"));
                if (!projectsByFile.TryGetValue(id, out int project) || project != Number(file, "modId")) continue;
                files[id] = ParseFile(file, allowDistribution: true);
            }
        }

        // Cross-platform legal fallback: look up missing files by exact SHA-1 on Modrinth
        var missingUrlEntries = projectsByFile
            .Where(kv => files.TryGetValue(kv.Key, out var f) && f.FileUrl.Length == 0)
            .Select(kv => (FileId: kv.Key, File: files[kv.Key]))
            .ToList();
        int autoResolvedCount = 0;
        if (missingUrlEntries.Count > 0)
        {
            var patchList = missingUrlEntries.Select(e => e.File).ToList();
            autoResolvedCount = await FillMissingUrlsFromModrinthAsync(patchList, ct);
            for (int i = 0; i < missingUrlEntries.Count; i++)
                files[missingUrlEntries[i].FileId] = patchList[i];
        }

        // Match any remaining opt-out files already present in local Downloads / cache directories
        var localPaths = new Dictionary<int, string>();
        var stillMissingIds = new List<int>();
        foreach (var (fileId, _) in projectsByFile)
        {
            if (!files.TryGetValue(fileId, out var f))
            {
                stillMissingIds.Add(fileId);
                continue;
            }
            if (f.FileUrl.Length > 0) continue;
            string? local = FindVerifiedLocalFile(f, localSearchDirs);
            if (local != null) localPaths[fileId] = local;
            else stillMissingIds.Add(fileId);
        }

        var classes = new Dictionary<int, int>();
        var projectMeta = new Dictionary<int, (string Name, string WebUrl)>();
        var metaProjects = files.Where(item => !item.Value.File.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(item => projectsByFile[item.Key])
            .Concat(stillMissingIds.Select(fid => projectsByFile[fid]))
            .Distinct()
            .ToArray();
        foreach (var batch in metaProjects.Chunk(50))
        {
            using var doc = await RequestAsync(HttpMethod.Post, "mods", key, new { modIds = batch }, route, ct);
            foreach (var project in Array(doc.RootElement, "data"))
            {
                int pid = checked((int)Number(project, "id"));
                classes[pid] = checked((int)Number(project, "classId"));
                string webUrl = project.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
                    ? Text(links, "websiteUrl") : "";
                projectMeta[pid] = (Text(project, "name", $"Project {pid}"), webUrl);
            }
        }

        static string TargetDirectoryForClass(string fileName, int classId)
        {
            if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return "mods";
            return classId switch
            {
                6 => "mods",
                12 => "resourcepacks",
                6552 => "shaderpacks",
                _ => "mods"
            };
        }

        if (stillMissingIds.Count > 0)
        {
            var manualList = new List<CurseForgeManualFile>(stillMissingIds.Count);
            foreach (int fileId in stillMissingIds)
            {
                int projectId = projectsByFile[fileId];
                files.TryGetValue(fileId, out var f);
                projectMeta.TryGetValue(projectId, out var meta);
                string fileName = f?.File is { Length: > 0 } fn ? fn : $"file-{fileId}.jar";
                string pName = !string.IsNullOrWhiteSpace(meta.Name) ? meta.Name : $"CurseForge #{projectId}";
                string baseWeb = !string.IsNullOrWhiteSpace(meta.WebUrl)
                    ? meta.WebUrl.TrimEnd('/')
                    : $"https://www.curseforge.com/projects/{projectId}";
                string pageUrl = $"{baseWeb}/download/{fileId}";
                string folder = f != null ? TargetDirectoryForClass(fileName, classes.GetValueOrDefault(projectId)) : "mods";
                manualList.Add(new CurseForgeManualFile(projectId, fileId, pName, fileName, pageUrl, folder, f?.Sha1 ?? "", f?.Size ?? 0));
            }
            string preview = string.Join("、", manualList.Take(3).Select(m => m.FileName));
            string count = manualList.Count > 3 ? $"等 {manualList.Count} 个文件" : "";
            string autoNote = autoResolvedCount > 0 ? $"（已通过 Modrinth 哈希自动补全 {autoResolvedCount} 个）" : "";
            string verifyNote = manualList.Any(f => f.Sha1.Length != 40) ? " 部分文件缺少发布方摘要，暂时无法自动验证。" : "";
            throw new CurseForgeManualFilesRequiredException(
                $"CurseForge 禁止第三方直链下载 {manualList.Count} 个文件{autoNote}：{preview}{count}。请从弹窗打开官方页下载至浏览器「下载」文件夹后点击「▶」重试。{verifyNote}",
                manualList,
                autoResolvedCount);
        }

        var result = new List<(string Path, ResourceFile File, string? LocalPath)>();
        foreach (var (id, file) in files)
        {
            if (!InstanceManager.IsFileName(file.File)) throw new InvalidDataException("CurseForge 文件名无效。");
            int classId = classes.GetValueOrDefault(projectsByFile[id]);
            if (!file.File.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && classId is not (6 or 12 or 6552))
                throw new InvalidDataException($"不支持此 CurseForge 依赖的安装路径：{file.File}");
            string directory = TargetDirectoryForClass(file.File, classId);
            result.Add(($"{directory}/{file.File}", file, localPaths.GetValueOrDefault(id)));
        }
        return result;
    }

    private Task<JsonDocument> GetAsync(string path, string key, string route, CancellationToken ct) =>
        RequestAsync(HttpMethod.Get, path, key, null, route, ct);

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string key, object? body, string route, CancellationToken ct)
    {
        string normalized = NormalizeRoute(route);
        bool hasKey = !string.IsNullOrWhiteSpace(key);

        if (normalized == "mcim")
            return await SendOnceAsync(method, McimBaseUrl, path, null, body, ct);

        if (normalized == "official")
        {
            if (!hasKey)
                throw new InvalidOperationException("请先配置 CurseForge API Key，或将 CurseForge 线路切换为「自动」/「MCIM 镜像」。");
            return await SendOnceAsync(method, OfficialBaseUrl, path, key, body, ct);
        }

        // auto: 有 Key 时优先官源直连，官源失败或无 Key 时自动回退到 MCIM 镜像（绝不向镜像发送 Key）
        if (hasKey)
        {
            try
            {
                using var primary = CancellationTokenSource.CreateLinkedTokenSource(ct);
                primary.CancelAfter(TimeSpan.FromSeconds(5));
                return await SendOnceAsync(method, OfficialBaseUrl, path, key, body, primary.Token);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return await SendOnceAsync(method, McimBaseUrl, path, null, body, ct);
            }
        }
        return await SendOnceAsync(method, McimBaseUrl, path, null, body, ct);
    }

    private async Task<JsonDocument> SendOnceAsync(
        HttpMethod method, string baseUrl, string path, string? apiKey, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        if (body != null) request.Content = JsonContent.Create(body);
        if (baseUrl == OfficialBaseUrl && !string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("x-api-key", apiKey);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await http.SendAsync(request, budget.Token);
        if (baseUrl == OfficialBaseUrl && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("CurseForge 拒绝了 API Key。请检查开发者控制台的密钥与访问权限。");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
    }
}

