using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DCL_Launcher;

internal enum ResourceMatch { VersionMismatch, LoaderMismatch, Compatible }

internal sealed record ResourceDependency(string ProjectId, string DependencyType);

internal sealed record ResourceFile(
    string Id, string Title, string VersionNumber, string File, string FileUrl, string Sha1,
    string[] GameVersions, string[] Loaders, DateTimeOffset Published, long Size, string ReleaseType, long Downloads,
    ResourceDependency[]? Dependencies = null)
{
    internal ResourceMatch Match(InstanceMeta instance, string category)
    {
        if (!GameVersions.Contains(instance.McVersion, StringComparer.OrdinalIgnoreCase))
            return ResourceMatch.VersionMismatch;

        var requiredLoaders = Loaders.Where(loader => loader is "Fabric" or "Forge" or "NeoForge" or "Quilt" or "LiteLoader" or "Rift").ToArray();
        bool matchesLoader = (category != "mods" || instance.Loader != "Vanilla") &&
            (requiredLoaders.Length == 0 || requiredLoaders.Contains(instance.Loader, StringComparer.OrdinalIgnoreCase));
        return matchesLoader ? ResourceMatch.Compatible : ResourceMatch.LoaderMismatch;
    }
}

internal static class ResourceCatalog
{
    internal sealed record LocalizedResource(string Name, string Search, string[] Ids, string Pinyin, string? Category, string Source)
    {
        internal string Catalog => Category ?? "mods";
        internal string CompactPinyin => PinyinKey(Pinyin);
        internal string Initials => PinyinKey(string.Concat(Pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word[0])));
    }

    private static readonly LocalizedResource[] LocalizedResources = ReadLocalizedResources();
    private static readonly Dictionary<(string Category, string Id), LocalizedResource> Localizations = LocalizedResources
        .SelectMany(item => item.Ids.Select(id => (Key: (item.Catalog, NormalizeId(id)), Item: item)))
        .DistinctBy(entry => entry.Key)
        .ToDictionary(entry => entry.Key, entry => entry.Item);

    private static LocalizedResource[] ReadLocalizedResources()
    {
        var assembly = typeof(ResourceCatalog).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("mod-names-zh-cn.json", StringComparison.Ordinal));
        using var bundled = embedded is null ? null : assembly.GetManifestResourceStream(embedded);
        var names = bundled is null ? [] : JsonSerializer.Deserialize<LocalizedResource[]>(bundled, StorageService.JsonOpts) ?? [];
        string path = Path.Combine(AppContext.BaseDirectory, "DCL", "config", "resource-names-zh-cn.json");
        try
        {
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                names = (JsonSerializer.Deserialize<LocalizedResource[]>(file, StorageService.JsonOpts) ?? []).Concat(names).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A local import is optional. Use the names shipped with the launcher.
        }
        return names.Where(entry => entry != null && entry.Catalog is "mods" or "packs" or "shaders" or "modpacks"
            && !string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Search)
            && !string.IsNullOrWhiteSpace(entry.Pinyin) && entry.Ids is { Length: > 0 }
            && entry.Ids.All(id => !string.IsNullOrWhiteSpace(id))
            && Uri.TryCreate(entry.Source, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps).ToArray();
    }

    internal static string NormalizeId(string id) => id.Replace('_', '-').ToLowerInvariant();

    private static string PinyinKey(string value)
    {
        return string.Concat(value.Normalize(NormalizationForm.FormD).ToLowerInvariant()
            .Where(character => character is >= 'a' and <= 'z')).Replace('v', 'u');
    }

    internal static LocalizedResource[] FindLocalizedNames(string category, string query)
    {
        string term = query.Trim();
        if (term.Length == 0) return [];
        bool han = term.Any(character => character is >= '\u3400' and <= '\u9fff');
        string pinyin = han ? "" : PinyinKey(term);
        if (!han && (pinyin.Length < 2 || term.Any(character => character is >= '0' and <= '9' && character is not (>= '1' and <= '5')))) return [];
        return LocalizedResources.Where(item => item.Catalog == category)
            .Select(item => (Item: item, Rank: han
                ? item.Name == term ? 0 : item.Name.Contains(term, StringComparison.Ordinal) ? 1 : -1
                : item.CompactPinyin == pinyin || item.Initials == pinyin ? 0
                : pinyin.Length >= 3 && item.CompactPinyin.Contains(pinyin, StringComparison.Ordinal) ? 1 : -1))
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Item.Name.Length)
            .Select(match => match.Item)
            .ToArray();
    }

    public static string ChineseName(string category, string slug) =>
        Localizations.GetValueOrDefault((category, NormalizeId(slug)))?.Name ?? "";

    internal static string PinyinForName(string name) =>
        name.Length == 0 ? "" : LocalizedResources.FirstOrDefault(item => item.Name == name)?.Pinyin ?? "";

    public static string ProjectType(string category) => category switch
    {
        "mods" => "mod",
        "packs" => "resourcepack",
        "shaders" => "shader",
        "modpacks" => "modpack",
        _ => throw new ArgumentException("未知资源分类")
    };

    public static string Category(string projectType) => projectType switch
    {
        "mod" => "mods",
        "resourcepack" => "packs",
        "shader" => "shaders",
        "modpack" => "modpacks",
        _ => throw new ArgumentException("未知资源分类")
    };

    public static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : fallback;

    public static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;

    public static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    public static string[] Strings(JsonElement element, string name) =>
        Array(element, name).Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!).Where(value => value.Length > 0).ToArray();

    public static string Loader(string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => "NeoForge", "optifine" => "OptiFine", "fabric" => "Fabric",
        "forge" => "Forge", "iris" => "Iris", "quilt" => "Quilt",
        "minecraft" or "vanilla" => "Vanilla", _ => loader
    };

    public static string DownloadCount(long count) => count >= 10000 ? $"{count / 10000.0:F1} 万次" : $"{count} 次";

    public static string SizeLabel(long size) =>
        size >= 1048576 ? $"{size / 1048576.0:F1} MB" : $"{Math.Max(1, size / 1024)} KB";

    public static DateTimeOffset Date(string value) => DateTimeOffset.TryParse(value, out var date) ? date : default;

    public static string VersionRange(IEnumerable<string> versions)
    {
        var sorted = versions.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v, VersionComparer).ToArray();
        return sorted.Length > 1 ? $"{sorted[0]} - {sorted[^1]}" : sorted.FirstOrDefault() ?? "";
    }

    private static readonly Regex VersionParts = new(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(.*)$", RegexOptions.IgnoreCase);

    public static IComparer<string> VersionComparer { get; } = Comparer<string>.Create((left, right) =>
    {
        var a = VersionParts.Match(left ?? "");
        var b = VersionParts.Match(right ?? "");
        if (!a.Success || !b.Success)
        {
            int order = a.Success.CompareTo(b.Success);
            return order != 0 ? order : string.CompareOrdinal(left, right);
        }
        for (int i = 1; i <= 3; i++)
        {
            _ = int.TryParse(a.Groups[i].Value, out int x);
            _ = int.TryParse(b.Groups[i].Value, out int y);
            if (x != y) return x.CompareTo(y);
        }
        int suffixOrder = string.IsNullOrEmpty(a.Groups[4].Value).CompareTo(string.IsNullOrEmpty(b.Groups[4].Value));
        return suffixOrder != 0 ? suffixOrder : string.CompareOrdinal(left, right);
    });

    public static void AddDetails(
        Dictionary<string, object?> item,
        string category,
        IReadOnlyList<ResourceFile> files,
        Dictionary<string, string> links,
        IReadOnlyDictionary<string, Dictionary<string, object?>>? dependencyProjects = null)
    {
        bool hideLoaders = category is "packs" or "shaders";
        var groups = new Dictionary<string, Dictionary<string, List<(ResourceFile Raw, object Build)>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderByDescending(f => f.VersionNumber, VersionComparer).ThenByDescending(f => f.Published))
        {
            int depCount = file.Dependencies?.Count(d => d.DependencyType is "required" or "optional") ?? 0;
            object build = new
            {
                file.Id, file.Title, file.VersionNumber, file.File,
                available = !string.IsNullOrEmpty(file.FileUrl),
                downloads = DownloadCount(file.Downloads), updatedAgo = file.Published.ToString("yyyy-MM-dd"),
                size = SizeLabel(file.Size), file.ReleaseType, depCount
            };
            IEnumerable<string> loaders = hideLoaders ? new[] { "default" } : file.Loaders.DefaultIfEmpty("default");
            foreach (string loader in loaders)
            {
                if (!groups.TryGetValue(loader, out var versions)) groups[loader] = versions = new();
                foreach (var version in file.GameVersions)
                {
                    if (!versions.TryGetValue(version, out var entries)) versions[version] = entries = new();
                    entries.Add((file, build));
                }
            }
        }
        item["catKey"] = category;
        item["hideLoaders"] = hideLoaders;
        item["links"] = links;
        item["loaders"] = groups.Keys.DefaultIfEmpty("default").ToArray();
        item["mcVersions"] = VersionRange(files.SelectMany(f => f.GameVersions));
        item["loaderTrees"] = groups.ToDictionary(pair => pair.Key, pair => pair.Value
            .OrderByDescending(group => group.Key, VersionComparer).Select(group =>
            {
                var depMap = new Dictionary<string, (string DepType, Dictionary<string, object?>? Meta)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (raw, _) in group.Value)
                {
                    if (raw.Dependencies == null) continue;
                    foreach (var dep in raw.Dependencies)
                    {
                        if (string.IsNullOrWhiteSpace(dep.ProjectId)) continue;
                        if (dep.DependencyType is not ("required" or "optional")) continue;
                        Dictionary<string, object?>? meta = null;
                        dependencyProjects?.TryGetValue(dep.ProjectId, out meta);
                        if (!depMap.TryGetValue(dep.ProjectId, out var existing) || (existing.DepType != "required" && dep.DependencyType == "required"))
                            depMap[dep.ProjectId] = (dep.DependencyType, meta);
                    }
                }
                var groupDeps = depMap
                    .Where(kv => kv.Value.Meta != null)
                    .OrderBy(kv => kv.Value.DepType == "required" ? 0 : 1)
                    .Select(kv =>
                    {
                        var m = kv.Value.Meta!;
                        return new Dictionary<string, object?>
                        {
                            ["id"] = m.GetValueOrDefault("id") ?? kv.Key,
                            ["projectId"] = kv.Key,
                            ["catKey"] = m.GetValueOrDefault("catKey") ?? category,
                            ["enName"] = m.GetValueOrDefault("enName") ?? kv.Key,
                            ["cnName"] = m.GetValueOrDefault("cnName") ?? "",
                            ["summary"] = m.GetValueOrDefault("summary") ?? "",
                            ["icon"] = m.GetValueOrDefault("icon") ?? "assets/mc-icons/cobblestone.png",
                            ["downloads"] = m.GetValueOrDefault("downloads") ?? "",
                            ["source"] = m.GetValueOrDefault("source") ?? item.GetValueOrDefault("source") ?? "Modrinth",
                            ["depType"] = kv.Value.DepType,
                            ["label"] = kv.Value.DepType == "required" ? "前置支持库" : "可选前置"
                        };
                    })
                    .ToArray();

                return new
                {
                    mcVersion = group.Key,
                    title = hideLoaders || pair.Key == "default" ? $"Minecraft {group.Key}" : $"{pair.Key} {group.Key}",
                    dependencies = groupDeps,
                    builds = group.Value.Select(v => v.Build).ToArray()
                };
            }).ToArray());
    }
}

