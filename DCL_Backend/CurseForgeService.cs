using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using static DCL_Launcher.ResourceCatalog;

namespace DCL_Launcher;

internal sealed class CurseForgeService(HttpClient http)
{
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
        string sortBy = "downloads", string mcVersion = "")
    {
        if (offset + limit > 10000) return ([], 10000);
        string verFilter = string.IsNullOrWhiteSpace(mcVersion) || mcVersion.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"&gameVersion={Uri.EscapeDataString(mcVersion.Trim())}";
        string loaderFilter = category is "mods" or "modpacks" && LoaderId(loader) is > 0 and var modLoader
            ? $"&modLoaderType={modLoader}" : "";
        string filter = loaderFilter + verFilter;
        // CurseForge ModsSearchSortField: 6 = TotalDownloads, 11 = ReleasedDate (NOT 3 = LastUpdated)
        int sortField = sortBy.Equals("newest", StringComparison.OrdinalIgnoreCase) ? 11 : 6;
        var translated = FindLocalizedNames(category, query);
        if (translated.Length > 0)
        {
            var slugs = translated.SelectMany(item => item.Ids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            using var requests = new SemaphoreSlim(3);
            var lookups = slugs.Select(async slug =>
            {
                await requests.WaitAsync(ct);
                try
                {
                    using var response = await GetAsync($"mods/search?gameId=432&classId={ClassId(category)}&slug={Uri.EscapeDataString(slug)}&pageSize=1{filter}", key, ct);
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
        using var doc = await GetAsync($"mods/search?gameId=432&classId={ClassId(category)}&index={offset}&pageSize={limit}&sortField={sortField}&sortOrder=desc&searchFilter={Uri.EscapeDataString(query.Trim())}{filter}", key, ct);
        var mods = Array(doc.RootElement, "data").Where(mod => Number(mod, "classId") == ClassId(category))
            .Select(mod => Project(mod, category)).ToList();
        return (mods, Math.Min(10000, (int)Number(doc.RootElement.GetProperty("pagination"), "totalCount")));
    }

    public async Task<(Dictionary<string, object?> Project, List<ResourceFile> Files)> GetResourceAsync(string category, string projectId, string key, CancellationToken ct)
    {
        using var project = await GetProjectAsync(category, projectId, key, ct);
        var mod = project.RootElement.GetProperty("data");
        var files = await FilesAsync(projectId, key, category, !mod.TryGetProperty("allowModDistribution", out var flag) || flag.ValueKind != JsonValueKind.False, ct);
        var item = Project(mod, category);
        var links = new Dictionary<string, string>();
        if (item["webUrl"] is string { Length: > 0 } url) links["curseforge"] = url;
        var depIds = files.SelectMany(f => f.Dependencies ?? [])
            .Select(d => int.TryParse(d.ProjectId, out int mid) ? mid : 0)
            .Where(mid => mid > 0).Distinct().Take(80).ToArray();
        var depMetaMap = await FetchCurseForgeProjectsBatchAsync(depIds, key, ct);
        AddDetails(item, category, files, links, depMetaMap);
        return (item, files);
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> FetchCurseForgeProjectsBatchAsync(
        int[] modIds, string key, CancellationToken ct)
    {
        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        if (modIds.Length == 0 || string.IsNullOrWhiteSpace(key)) return map;
        try
        {
            foreach (int[] batch in modIds.Chunk(50))
            {
                using var doc = await RequestAsync(HttpMethod.Post, "mods", key, new { modIds = batch }, ct);
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

    private async Task<JsonDocument> GetProjectAsync(string category, string projectId, string key, CancellationToken ct)
    {
        if (!int.TryParse(projectId, out int id) || id <= 0) throw new ArgumentException("无效的 CurseForge 项目 ID");
        var doc = await GetAsync($"mods/{id}", key, ct);
        var project = doc.RootElement.GetProperty("data");
        if (Number(project, "gameId") == 432 && Number(project, "classId") == ClassId(category)) return doc;
        doc.Dispose();
        throw new InvalidDataException("此项目与当前资源分类不匹配");
    }

    private async Task<List<ResourceFile>> FilesAsync(
        string projectId, string key, string category, bool allowDistribution, CancellationToken ct)
    {
        var files = new List<ResourceFile>();
        const int size = 50;
        for (int offset = 0; offset < 10000; offset += size)
        {
            using var doc = await GetAsync($"mods/{projectId}/files?index={offset}&pageSize={size}", key, ct);
            var page = Array(doc.RootElement, "data");
            if (category != "modpacks")
                files.AddRange(page.Select(file => ParseFile(file, allowDistribution)));
            else
                files.AddRange(page.Where(file => !file.TryGetProperty("isServerPack", out var server) ||
                        server.ValueKind != JsonValueKind.True)
                    .Select(file => ParseFile(file, allowDistribution)));
            if (page.Length < size || offset + size >= Number(doc.RootElement.GetProperty("pagination"), "totalCount")) break;
        }
        return files;
    }

    private static Dictionary<string, object?> Project(JsonElement mod, string category)
    {
        string id = Number(mod, "id").ToString();
        string slug = Text(mod, "slug");
        return new()
        {
            ["id"] = $"cf-{id}", ["projectId"] = id, ["slug"] = slug, ["source"] = "CurseForge",
            ["cnName"] = ChineseName(category, slug), ["pinyin"] = PinyinForName(ChineseName(category, slug)), ["enName"] = Text(mod, "name", slug),
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

    internal async Task<List<(string Path, ResourceFile File)>> GetPackFilesAsync(
        IReadOnlyDictionary<int, int> projectsByFile, string key, CancellationToken ct)
    {
        var files = new Dictionary<int, ResourceFile>();
        foreach (var batch in projectsByFile.Keys.Chunk(50))
        {
            using var doc = await RequestAsync(HttpMethod.Post, "mods/files", key, new { fileIds = batch }, ct);
            foreach (var file in Array(doc.RootElement, "data"))
            {
                int id = checked((int)Number(file, "id"));
                if (!projectsByFile.TryGetValue(id, out int project) || project != Number(file, "modId")) continue;
                files[id] = ParseFile(file, allowDistribution: true);
            }
        }

        var unavailable = projectsByFile.Where(item => !files.TryGetValue(item.Key, out var file) || file.FileUrl.Length == 0)
            .Select(item => files.TryGetValue(item.Key, out var file) ? file.File : $"项目 {item.Value} / 文件 {item.Key}").ToArray();
        if (unavailable.Length > 0)
        {
            string count = unavailable.Length > 3 ? $"等 {unavailable.Length} 个文件" : "";
            throw new InvalidOperationException($"CurseForge 未向启动器提供必需文件：{string.Join("、", unavailable.Take(3))}{count}。可在 CurseForge 客户端安装此整合包。");
        }

        var classes = new Dictionary<int, int>();
        var archiveProjects = files.Where(item => !item.Value.File.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(item => projectsByFile[item.Key]).Distinct();
        foreach (var batch in archiveProjects.Chunk(50))
        {
            using var doc = await RequestAsync(HttpMethod.Post, "mods", key, new { modIds = batch }, ct);
            foreach (var project in Array(doc.RootElement, "data"))
                classes[checked((int)Number(project, "id"))] = checked((int)Number(project, "classId"));
        }

        var result = new List<(string Path, ResourceFile File)>();
        foreach (var (id, file) in files)
        {
            if (!InstanceManager.IsFileName(file.File)) throw new InvalidDataException("CurseForge 文件名无效。");
            string directory = file.File.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "mods"
                : classes.GetValueOrDefault(projectsByFile[id]) switch
                {
                    6 => "mods",
                    12 => "resourcepacks",
                    6552 => "shaderpacks",
                    _ => throw new InvalidDataException($"不支持此 CurseForge 依赖的安装路径：{file.File}")
                };
            result.Add(($"{directory}/{file.File}", file));
        }
        return result;
    }

    private Task<JsonDocument> GetAsync(string path, string key, CancellationToken ct) =>
        RequestAsync(HttpMethod.Get, path, key, null, ct);

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string key, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先配置 CurseForge API Key。");
        using var request = new HttpRequestMessage(method, $"https://api.curseforge.com/v1/{path}");
        if (body != null) request.Content = JsonContent.Create(body);
        request.Headers.Add("x-api-key", key);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("CurseForge 拒绝了 API Key。请检查开发者控制台的密钥与访问权限。");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

