using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCL_Launcher;

public sealed partial class MinecraftApiService
{
    internal Func<string> SelectSource { get; set; } = () => "最优";

    private async Task<string> CatalogTextAsync(string url, CancellationToken ct, string? source = null)
    {
        string path = new Uri(url).AbsolutePath;
        void Validate(string text)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool valid = path.EndsWith("/search") ? root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array && root.TryGetProperty("total_hits", out _) :
                path.EndsWith("/profile/json") ? root.ValueKind == JsonValueKind.Object && root.TryGetProperty("mainClass", out _) &&
                    root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array :
                path.StartsWith("/v2/project/") && !path.EndsWith("/version") ? root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out _) :
                root.ValueKind == JsonValueKind.Array;
            if (!valid) throw new InvalidDataException("目录响应与请求类型不匹配");
        }
        return await CachedTextAsync(url, DownloadRoutes.Files(url, source ?? SelectSource(), true), Validate, ct);
    }

    private async Task<string> CachedTextAsync(string url, IReadOnlyList<string> routes, Action<string> validate, CancellationToken ct)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string path = Path.Combine(_storage.ManifestsCacheDir, "catalog", key + ".json");
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-5))
        {
            string recent = await File.ReadAllTextAsync(path, ct);
            try { validate(recent); return recent; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        }
        string text;
        try { text = await _dl.GetDownloadTextAsync(routes, ct, validate); }
        catch (IOException) when (!ct.IsCancellationRequested && File.Exists(path))
        {
            text = await File.ReadAllTextAsync(path, ct);
            validate(text);
            _storage.Log($"使用上次成功获取的目录 [{new Uri(url).Host}]");
            return text;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N");
        try { await File.WriteAllTextAsync(temp, text, ct); File.Move(temp, path, true); }
        catch (IOException ex) { _storage.Log($"目录缓存写入失败：{ex.Message}"); }
        finally { File.Delete(temp); }
        return text;
    }
}
