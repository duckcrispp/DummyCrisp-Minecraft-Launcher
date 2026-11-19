using System.IO.Compression;
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
    internal sealed record LocalizedResource(
        string Name, string Search, string[] Ids, string Pinyin, string? Category, string Source,
        string? Abbr = null, string? McmodId = null, string? CfSlug = null, string? MrSlug = null,
        string? PrecomputedPinyin = null, string? PrecomputedInitials = null)
    {
        internal string Catalog => Category ?? "mods";
        internal string CompactPinyin => !string.IsNullOrEmpty(PrecomputedPinyin) ? PrecomputedPinyin : PinyinKey(Pinyin);
        internal string Initials => !string.IsNullOrEmpty(PrecomputedInitials) ? PrecomputedInitials : PinyinKey(string.Concat(Pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word[0])));
    }

    private sealed record LocalizationIndex(
        LocalizedResource[] Curated,
        LocalizedResource[] All,
        Dictionary<(string Category, string Id), LocalizedResource> BySlug,
        Dictionary<(string Category, string Id), LocalizedResource> ByCompactSlug,
        Dictionary<(string Category, string Title), LocalizedResource> ByEnglishTitle);

    private static readonly LocalizationIndex Index = BuildLocalizationIndex();

    private static LocalizationIndex BuildLocalizationIndex()
    {
        var curated = ReadCuratedJsonResources();
        var mcmod = ReadEmbeddedMcmodResources();
        var all = new List<LocalizedResource>(curated.Length + mcmod.Count);
        all.AddRange(curated);
        all.AddRange(mcmod);

        var bySlug = new Dictionary<(string, string), LocalizedResource>();
        var byCompact = new Dictionary<(string, string), LocalizedResource>();
        var byTitle = new Dictionary<(string, string), LocalizedResource>();

        void Register(LocalizedResource item)
        {
            string cat = item.Catalog;
            foreach (string rawId in item.Ids)
            {
                string norm = NormalizeId(rawId);
                if (norm.Length > 0)
                {
                    if (!bySlug.TryGetValue((cat, norm), out var existing)) bySlug[(cat, norm)] = item;
                    else bySlug[(cat, norm)] = MergeMetadata(existing, item);
                }
                string compact = CompactId(rawId);
                if (compact.Length >= 3)
                {
                    if (!byCompact.TryGetValue((cat, compact), out var existingC)) byCompact[(cat, compact)] = item;
                    else byCompact[(cat, compact)] = MergeMetadata(existingC, item);
                }
            }
            string titleKey = NormalizeTitle(item.Search);
            if (titleKey.Length >= 3)
            {
                if (!byTitle.TryGetValue((cat, titleKey), out var existingT)) byTitle[(cat, titleKey)] = item;
                else byTitle[(cat, titleKey)] = MergeMetadata(existingT, item);
            }
        }

        // Curated JSON overrides take precedence for Name/Pinyin, while MC百科 enriches McmodId/CfSlug/MrSlug
        foreach (var item in curated) Register(item);
        foreach (var item in mcmod) Register(item);

        return new LocalizationIndex(curated, all.ToArray(), bySlug, byCompact, byTitle);
    }

    private static LocalizedResource MergeMetadata(LocalizedResource primary, LocalizedResource extra)
    {
        return primary with
        {
            Name = primary.Name.Length > 0 ? primary.Name : extra.Name,
            Abbr = !string.IsNullOrEmpty(primary.Abbr) ? primary.Abbr : extra.Abbr,
            McmodId = !string.IsNullOrEmpty(primary.McmodId) ? primary.McmodId : extra.McmodId,
            CfSlug = !string.IsNullOrEmpty(primary.CfSlug) ? primary.CfSlug : extra.CfSlug,
            MrSlug = !string.IsNullOrEmpty(primary.MrSlug) ? primary.MrSlug : extra.MrSlug
        };
    }

    private static LocalizedResource[] ReadCuratedJsonResources()
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

    private static List<LocalizedResource> ReadEmbeddedMcmodResources()
    {
        var list = new List<LocalizedResource>(34000);
        var assembly = typeof(ResourceCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("DCL_Launcher.Data.mcmod-zh-cn.tsv.gz");
        if (stream is null) return list;
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 8) continue;
            string cat = parts[0];
            if (cat is not ("mods" or "modpacks" or "packs" or "shaders")) continue;
            string mcmodId = parts[1].Trim();
            string cfSlug = parts[2].Trim();
            string mrSlug = parts[3].Trim();
            string cn = parts[4].Trim();
            string en = parts[5].Trim();
            string abbr = parts[6].Trim();
            string modIdsRaw = parts[7].Trim();
            string pyFull = parts.Length >= 9 ? parts[8].Trim() : "";
            string pyInit = parts.Length >= 10 ? parts[9].Trim() : "";
            var slugList = new List<string>(4);
            if (cfSlug.Length > 0) slugList.Add(cfSlug);
            if (mrSlug.Length > 0 && !mrSlug.Equals(cfSlug, StringComparison.OrdinalIgnoreCase)) slugList.Add(mrSlug);
            if (modIdsRaw.Length > 0)
            {
                foreach (string mid in modIdsRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!mid.Contains(' ') && !slugList.Contains(mid, StringComparer.OrdinalIgnoreCase))
                        slugList.Add(mid);
                }
            }
            if (slugList.Count == 0 && en.Length == 0) continue;
            list.Add(new LocalizedResource(
                cn,
                en.Length > 0 ? en : (slugList.FirstOrDefault() ?? cn),
                slugList.ToArray(),
                "",
                cat,
                "https://www.mcmod.cn/",
                abbr.Length > 0 ? abbr : null,
                mcmodId.Length > 0 ? mcmodId : null,
                cfSlug.Length > 0 ? cfSlug : null,
                mrSlug.Length > 0 ? mrSlug : null,
                pyFull.Length > 0 ? pyFull : null,
                pyInit.Length > 0 ? pyInit : null));
        }
        return list;
    }

    internal static string NormalizeId(string id) => id.Trim().Replace('_', '-').ToLowerInvariant();

    private static string CompactId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char ch in id)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string NormalizeTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

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

        if (han)
        {
            return Index.All
                .Where(item => item.Catalog == category && item.Name.Length > 0 && item.Ids.Length > 0)
                .Select(item => (Item: item, Rank:
                    item.Name.Equals(term, StringComparison.Ordinal) ? 0
                    : item.Name.StartsWith(term, StringComparison.Ordinal) ? 1
                    : item.Name.Contains(term, StringComparison.Ordinal) ? 2 : -1))
                .Where(match => match.Rank >= 0)
                .OrderBy(match => match.Rank)
                .ThenBy(match => match.Item.Name.Length)
                .Select(match => match.Item)
                .Take(24)
                .ToArray();
        }

        // Exact community abbreviation match (e.g. JEI, AE2, IC2, GTNH)
        if (term.Length is >= 2 and <= 8 && !term.Contains(' '))
        {
            var abbrMatches = Index.All
                .Where(item => item.Catalog == category && item.Ids.Length > 0 &&
                    !string.IsNullOrEmpty(item.Abbr) &&
                    item.Abbr.Equals(term, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .ToArray();
            if (abbrMatches.Length > 0) return abbrMatches;
        }

        // Pinyin search across full MC百科 dictionary (skip if term is an exact English mod slug/title)
        string pinyin = PinyinKey(term);
        if (pinyin.Length < 2 || term.Any(character => character is >= '0' and <= '9' && character is not (>= '1' and <= '5')))
            return [];
        string normTerm = NormalizeId(term);
        string compactTerm = CompactId(term);
        if (Index.BySlug.ContainsKey((category, normTerm)) ||
            (compactTerm.Length >= 3 && (Index.ByCompactSlug.ContainsKey((category, compactTerm)) || Index.ByEnglishTitle.ContainsKey((category, compactTerm)))))
            return [];

        return Index.All
            .Where(item => item.Catalog == category && item.Name.Length > 0 && item.Ids.Length > 0 && item.CompactPinyin.Length > 0)
            .Select(item => (Item: item, Rank:
                item.CompactPinyin.Equals(pinyin, StringComparison.Ordinal) ? 0
                : item.Initials.Equals(pinyin, StringComparison.Ordinal) ? 1
                : pinyin.Length >= 3 && item.CompactPinyin.StartsWith(pinyin, StringComparison.Ordinal) ? 2
                : pinyin.Length >= 4 && item.CompactPinyin.Contains(pinyin, StringComparison.Ordinal) ? 3 : -1))
            .Where(match => match.Rank >= 0)
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Item.Name.Length)
            .Select(match => match.Item)
            .Take(24)
            .ToArray();
    }

    private static LocalizedResource? LookupMetadata(string category, string slug, string enName = "")
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            if (Index.BySlug.TryGetValue((category, NormalizeId(slug)), out var hit))
                return hit;
            string compact = CompactId(slug);
            if (compact.Length >= 3 && Index.ByCompactSlug.TryGetValue((category, compact), out var compactHit))
                return compactHit;
        }
        if (!string.IsNullOrWhiteSpace(enName))
        {
            string titleKey = NormalizeTitle(enName);
            if (titleKey.Length >= 3 && Index.ByEnglishTitle.TryGetValue((category, titleKey), out var titleHit))
                return titleHit;
        }
        return null;
    }

    public static string ChineseName(string category, string slug, string enName = "") =>
        LookupMetadata(category, slug, enName)?.Name ?? "";

    public static string ResolveSourceLabel(string category, string slug, string enName, string primarySource)
    {
        var meta = LookupMetadata(category, slug, enName);
        if (meta == null) return primarySource;
        if (primarySource == "CurseForge" && !string.IsNullOrWhiteSpace(meta.MrSlug))
            return "CurseForge/Modrinth";
        if (primarySource == "Modrinth" && !string.IsNullOrWhiteSpace(meta.CfSlug))
            return "CurseForge/Modrinth";
        return primarySource;
    }

    public static void EnrichExternalLinks(string category, string slug, string enName, Dictionary<string, string> links)
    {
        var meta = LookupMetadata(category, slug, enName);
        if (meta == null) return;

        if (!links.ContainsKey("curseforge") && !string.IsNullOrWhiteSpace(meta.CfSlug))
        {
            string cfSection = category switch
            {
                "modpacks" => "modpacks",
                "packs" => "texture-packs",
                "shaders" => "shaders",
                _ => "mc-mods"
            };
            links["curseforge"] = $"https://www.curseforge.com/minecraft/{cfSection}/{Uri.EscapeDataString(meta.CfSlug)}";
        }
        if (!links.ContainsKey("modrinth") && !string.IsNullOrWhiteSpace(meta.MrSlug))
        {
            string mrSection = category switch
            {
                "modpacks" => "modpack",
                "packs" => "resourcepack",
                "shaders" => "shader",
                _ => "mod"
            };
            links["modrinth"] = $"https://modrinth.com/{mrSection}/{Uri.EscapeDataString(meta.MrSlug)}";
        }
        if (!links.ContainsKey("mcmod") && !string.IsNullOrWhiteSpace(meta.McmodId))
        {
            string mcmodPath = category == "modpacks" ? "modpack" : "class";
            links["mcmod"] = $"https://www.mcmod.cn/{mcmodPath}/{Uri.EscapeDataString(meta.McmodId)}.html";
        }
    }

    internal static string PinyinForName(string name) =>
        name.Length == 0 ? "" : Index.Curated.FirstOrDefault(item => item.Name == name)?.Pinyin ?? "";

    /// <summary>Clustered resource type tag -> Modrinth category slug.</summary>
    internal static string ResolveModrinthTag(string category, string? tagId)
    {
        string tag = (tagId ?? "").Trim().ToLowerInvariant();
        if (tag.Length == 0 || tag == "all") return "";
        return category switch
        {
            "mods" => tag switch
            {
                "worldgen" or "biomes" or "dimensions" or "ores" or "structures" => "worldgen",
                "mobs" => "mobs",
                "technology" or "automation" or "energy" or "redstone" or "processing" or "addons" => "technology",
                "logistics" or "transportation" => "transportation",
                "storage" => "storage",
                "magic" => "magic",
                "adventure" => "adventure",
                "equipment" => "equipment",
                "minigame" => "minigame",
                "horror" or "cursed" => "cursed",
                "food" or "farming" => "food",
                "decoration" => "decoration",
                "optimization" or "bugfix" => "optimization",
                "utility" or "map" => "utility",
                "game-mechanics" => "game-mechanics",
                "social" => "social",
                "management" => "management",
                "economy" => "economy",
                "library" => "library",
                _ => ""
            },
            "modpacks" => tag switch
            {
                "multiplayer" => "multiplayer",
                "optimization" => "optimization",
                "lightweight" or "vanilla-plus" => "lightweight",
                "challenging" or "skyblock" or "horror" => "challenging",
                "combat" => "combat",
                "quests" => "quests",
                "technology" or "sci-fi" => "technology",
                "magic" => "magic",
                "adventure" or "exploration" or "minigame" => "adventure",
                "kitchen-sink" => "kitchen-sink",
                _ => ""
            },
            "packs" => tag switch
            {
                "vanilla-like" => "vanilla-like",
                "realistic" => "realistic",
                "modern" or "medieval" or "steampunk" or "themed" => "themed",
                "simplistic" => "simplistic",
                "decoration" => "decoration",
                "combat" => "combat",
                "utility" => "utility",
                "tweaks" => "tweaks",
                "cursed" => "cursed",
                "modded" => "modded",
                "animated" or "models" => "models",
                "gui" => "gui",
                "audio" => "audio",
                "fonts" => "fonts",
                "core-shaders" => "core-shaders",
                "8x-" or "16x" or "32x" or "64x" or "128x" or "256x" or "512x+" => tag,
                _ => ""
            },
            "shaders" => tag switch
            {
                "vanilla-like" or "fantasy" or "realistic" or "semi-realistic" or "cartoon" or "cursed"
                or "colored-lighting" or "path-tracing" or "pbr" or "reflections" or "atmosphere"
                or "bloom" or "foliage" or "shadows"
                or "potato" or "low" or "medium" or "high" or "screenshot" => tag,
                _ => ""
            },
            _ => ""
        };
    }

    /// <summary>Clustered resource type tag -> CurseForge categoryId.</summary>
    internal static int ResolveCurseForgeCategoryId(string category, string? tagId)
    {
        string tag = (tagId ?? "").Trim().ToLowerInvariant();
        if (tag.Length == 0 || tag == "all") return 0;
        return category switch
        {
            "mods" => tag switch
            {
                "worldgen" => 406,
                "biomes" => 407,
                "ores" => 408,
                "structures" => 409,
                "dimensions" => 410,
                "mobs" => 411,
                "technology" => 412,
                "processing" => 413,
                "transportation" => 414,
                "logistics" => 415,
                "farming" => 416,
                "energy" => 417,
                "magic" => 419,
                "storage" => 420,
                "library" => 421,
                "adventure" => 422,
                "map" => 423,
                "decoration" => 424,
                "game-mechanics" or "minigame" or "economy" => 425,
                "addons" => 426,
                "equipment" => 434,
                "social" or "management" => 435,
                "food" => 436,
                "redstone" => 4558,
                "automation" => 4843,
                "utility" => 5191,
                "optimization" => 6814,
                "bugfix" => 6821,
                "horror" or "cursed" => 10775,
                _ => 0
            },
            "modpacks" => tag switch
            {
                "technology" => 4472,
                "magic" => 4473,
                "sci-fi" => 4474,
                "adventure" => 4475,
                "exploration" => 4476,
                "minigame" => 4477,
                "quests" => 4478,
                "challenging" => 4479,
                "optimization" or "lightweight" => 4481,
                "kitchen-sink" => 4482,
                "combat" => 4483,
                "multiplayer" => 4484,
                "skyblock" => 4736,
                "vanilla-plus" => 5128,
                "horror" => 7418,
                _ => 0
            },
            "packs" => tag switch
            {
                "8x-" or "16x" => 393,
                "32x" => 394,
                "64x" => 395,
                "128x" => 396,
                "256x" => 397,
                "512x+" => 398,
                "steampunk" => 399,
                "realistic" => 400,
                "modern" => 401,
                "medieval" => 402,
                "vanilla-like" or "simplistic" => 403,
                "animated" => 404,
                "themed" or "decoration" or "combat" or "utility" or "tweaks" or "cursed" or "gui" or "audio" or "models" or "core-shaders" => 405,
                "modded" => 4465,
                "fonts" => 5244,
                _ => 0
            },
            "shaders" => tag switch
            {
                "realistic" or "semi-realistic" or "path-tracing" or "pbr" or "reflections" or "atmosphere" or "high" or "screenshot" => 6553,
                "fantasy" or "cartoon" or "cursed" or "colored-lighting" or "bloom" or "medium" => 6554,
                "vanilla-like" or "foliage" or "shadows" or "potato" or "low" => 6555,
                _ => 0
            },
            _ => 0
        };
    }

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
        EnrichExternalLinks(category, item.GetValueOrDefault("slug")?.ToString() ?? "", item.GetValueOrDefault("enName")?.ToString() ?? "", links);
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

