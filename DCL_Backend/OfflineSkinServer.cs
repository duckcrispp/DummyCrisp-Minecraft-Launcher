using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCL_Launcher;

internal sealed class OfflineSkinServer : IDisposable
{
    private sealed record SkinEntry(string UndashedUuid, string Username, string SkinPath, string SkinModel, string TextureHash);

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SkinEntry> _byUuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SkinEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _texturePaths = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private RSA? _rsa;
    private string _publicKeyPem = "";
    private string _metadataJson = "";
    private int _port;

    public string BaseUrl => _port > 0 ? $"http://127.0.0.1:{_port}" : "";

    public static (string SkinPath, string SkinModel, string AvatarRoute) ImportSkinPng(string sourcePngPath, string accountId, StorageService storage)
    {
        if (string.IsNullOrWhiteSpace(sourcePngPath) || !File.Exists(sourcePngPath))
            throw new FileNotFoundException("找不到所选皮肤文件。", sourcePngPath);

        byte[] rawBytes = File.ReadAllBytes(sourcePngPath);
        if (rawBytes.Length < 64 || rawBytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("皮肤文件大小不合法（需为有效的 PNG 皮肤贴图）。");

        using var ms = new MemoryStream(rawBytes);
        using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
        int w = img.Width;
        int h = img.Height;
        bool validRatio = w >= 64 && w <= 4096 && (w == h || w == h * 2);
        if (!validRatio)
            throw new InvalidDataException($"皮肤尺寸 {w}×{h} 不符合 Minecraft 皮肤规范（支持 64×64、64×32 及等比高清皮肤）。");

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(img, 0, 0, w, h);
        }

        // Standard Minecraft slim (Alex) detection: in 64x64+ format, column 54..55 at row 20..31 is transparent for 3px arms
        string skinModel = "default";
        if (w == h)
        {
            double scale = w / 64.0;
            int sx1 = Math.Clamp((int)Math.Floor(54 * scale), 0, w - 1);
            int sx2 = Math.Clamp((int)Math.Floor(55 * scale), 0, w - 1);
            int sy = Math.Clamp((int)Math.Floor(24 * scale), 0, h - 1);
            if (bmp.GetPixel(sx1, sy).A < 128 && bmp.GetPixel(sx2, sy).A < 128)
                skinModel = "slim";
        }

        Directory.CreateDirectory(storage.SkinsDir);
        string safeId = new string(accountId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safeId.Length == 0) safeId = Guid.NewGuid().ToString("N")[..10];
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Use unique versioned filenames so WebView2/JVM file handles on previous skins never cause Access Denied
        string skinFile = $"{safeId}-{stamp}.png";
        string skinDest = Path.Combine(storage.SkinsDir, skinFile);
        bmp.Save(skinDest, ImageFormat.Png);

        // Composite 8x8 base head (8,8) + 8x8 hat layer (40,8) onto crisp pixelated 64x64 avatar PNG
        int unit = Math.Max(1, w / 64);
        string headFile = $"{safeId}-{stamp}-head.png";
        string headDest = Path.Combine(storage.SkinsDir, headFile);
        using (var head = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(head))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Color.Transparent);
            g.DrawImage(bmp, new Rectangle(0, 0, 64, 64), new Rectangle(8 * unit, 8 * unit, 8 * unit, 8 * unit), GraphicsUnit.Pixel);
            g.DrawImage(bmp, new Rectangle(0, 0, 64, 64), new Rectangle(40 * unit, 8 * unit, 8 * unit, 8 * unit), GraphicsUnit.Pixel);
            head.Save(headDest, ImageFormat.Png);
        }

        // Best-effort cleanup of stale previous skin files for this account (never fail import if locked)
        try
        {
            foreach (string oldFile in Directory.EnumerateFiles(storage.SkinsDir, $"{safeId}*.png"))
            {
                if (string.Equals(oldFile, skinDest, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oldFile, headDest, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(oldFile); } catch { }
            }
        }
        catch { }

        return (skinDest, skinModel, $"{StorageService.SkinAssetRoute}{headFile}");
    }

    public void RegisterSkin(string uuid, string username, string skinPath, string skinModel)
    {
        if (string.IsNullOrWhiteSpace(skinPath) || !File.Exists(skinPath)) return;
        EnsureStarted();
        string undashed = uuid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(skinPath))).ToLowerInvariant()[..24];
        var entry = new SkinEntry(undashed, username, skinPath, skinModel == "slim" ? "slim" : "default", hash);
        _byUuid[undashed] = entry;
        _byName[username] = entry;
        _texturePaths[hash] = skinPath;
    }

    public void EnsureStarted()
    {
        lock (_lock)
        {
            if (_listener != null && _port > 0) return;
            _rsa = RSA.Create(2048);
            byte[] spki = _rsa.ExportSubjectPublicKeyInfo();
            var sb = new StringBuilder();
            sb.Append("-----BEGIN PUBLIC KEY-----\n");
            string b64 = Convert.ToBase64String(spki);
            for (int i = 0; i < b64.Length; i += 64)
                sb.Append(b64.AsSpan(i, Math.Min(64, b64.Length - i))).Append('\n');
            sb.Append("-----END PUBLIC KEY-----\n");
            _publicKeyPem = sb.ToString();

            _metadataJson = JsonSerializer.Serialize(new
            {
                meta = new
                {
                    serverName = "DCL Offline Skin",
                    implementationName = "dcl-offline-yggdrasil",
                    implementationVersion = "1.0.0",
                    feature_non_email_login = true
                },
                skinDomains = new[] { "127.0.0.1", "localhost" },
                signaturePublickey = _publicKeyPem
            });

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
    }

    public List<string> BuildJvmAgentArgs(string authlibInjectorJarPath)
    {
        EnsureStarted();
        string prefetched = Convert.ToBase64String(Encoding.UTF8.GetBytes(_metadataJson));
        return
        [
            $"-javaagent:{authlibInjectorJarPath}={BaseUrl}",
            "-Dauthlibinjector.side=client",
            $"-Dauthlibinjector.yggdrasil.prefetched={prefetched}"
        ];
    }

    public async Task<string?> EnsureAuthlibInjectorAsync(StorageService storage, DownloadManager dlMgr, CancellationToken ct)
    {
        string jarPath = Path.Combine(storage.CacheDir, "authlib-injector.jar");
        if (File.Exists(jarPath) && new FileInfo(jarPath).Length > 50_000)
            return jarPath;

        string[] metaUrls =
        [
            "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json",
            "https://authlib-injector.yushi.moe/artifact/latest.json"
        ];
        try
        {
            string metaText = await dlMgr.GetDownloadTextAsync(metaUrls, ct);
            using var doc = JsonDocument.Parse(metaText);
            string dlUrl = doc.RootElement.TryGetProperty("download_url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
            string? sha256 = doc.RootElement.TryGetProperty("checksums", out var cEl) && cEl.TryGetProperty("sha256", out var sEl)
                ? sEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(dlUrl)) return null;

            var urls = new List<string>();
            if (dlUrl.StartsWith("https://authlib-injector.yushi.moe/", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(dlUrl.Replace("https://authlib-injector.yushi.moe/", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/", StringComparison.OrdinalIgnoreCase));
            }
            urls.Add(dlUrl);
            await dlMgr.DownloadVerifiedFileAsync(urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), jarPath, null, null, null, null, ct, sha256: sha256);
            return File.Exists(jarPath) ? jarPath : null;
        }
        catch (Exception ex)
        {
            storage.Log($"离线皮肤组件 authlib-injector 获取失败（若当前无网络，联网导入皮肤一次即可离线复用）：{ex.Message}");
            return File.Exists(jarPath) && new FileInfo(jarPath).Length > 50_000 ? jarPath : null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 4000;
            client.SendTimeout = 4000;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(requestLine)) return;
            string[] parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            string method = parts[0].ToUpperInvariant();
            string rawTarget = parts[1];

            int contentLength = 0;
            while (true)
            {
                string? headerLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(headerLine)) break;
                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(headerLine["Content-Length:".Length..].Trim(), out contentLength);
            }

            string bodyText = "";
            if (contentLength > 0 && contentLength <= 65536)
            {
                char[] buf = new char[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await reader.ReadAsync(buf.AsMemory(read, contentLength - read), ct);
                    if (n <= 0) break;
                    read += n;
                }
                bodyText = new string(buf, 0, read);
            }

            int qIdx = rawTarget.IndexOf('?');
            string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
            string query = qIdx >= 0 ? rawTarget[(qIdx + 1)..] : "";

            if (path == "/" || path == "")
            {
                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_metadataJson), ct);
                return;
            }

            if (path.StartsWith("/textures/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string hash = path["/textures/".Length..^4];
                if (_texturePaths.TryGetValue(hash, out var texPath) && File.Exists(texPath))
                {
                    byte[] png = await File.ReadAllBytesAsync(texPath, ct);
                    await WriteResponseAsync(stream, 200, "OK", "image/png", png, ct);
                    return;
                }
                await WriteResponseAsync(stream, 404, "Not Found", "text/plain", [], ct);
                return;
            }

            const string profilePrefix = "/sessionserver/session/minecraft/profile/";
            if (path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string reqUuid = Uri.UnescapeDataString(path[profilePrefix.Length..]).Replace("-", "", StringComparison.Ordinal).Trim('/');
                if (_byUuid.TryGetValue(reqUuid, out var entry))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(BuildSignedProfileJson(entry));
                    await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", payload, ct);
                    return;
                }
                await WriteResponseAsync(stream, 204, "No Content", "application/json", [], ct);
                return;
            }

            if (path.Equals("/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
            {
                string user = ExtractQueryParam(query, "username");
                if (!string.IsNullOrEmpty(user) && _byName.TryGetValue(user, out var entry))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(BuildSignedProfileJson(entry));
                    await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", payload, ct);
                    return;
                }
                await WriteResponseAsync(stream, 204, "No Content", "application/json", [], ct);
                return;
            }

            if (method == "POST" && path.Equals("/api/profiles/minecraft", StringComparison.OrdinalIgnoreCase))
            {
                var found = new List<object>();
                try
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string u = el.GetString() ?? "";
                            if (_byName.TryGetValue(u, out var entry))
                                found.Add(new { id = entry.UndashedUuid, name = entry.Username });
                        }
                    }
                }
                catch { }
                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(found));
                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", payload, ct);
                return;
            }

            await WriteResponseAsync(stream, 404, "Not Found", "application/json", Encoding.UTF8.GetBytes("{}"), ct);
        }
    }

    private string BuildSignedProfileJson(SkinEntry entry)
    {
        var skinObj = entry.SkinModel == "slim"
            ? (object)new { url = $"{BaseUrl}/textures/{entry.TextureHash}.png", metadata = new { model = "slim" } }
            : new { url = $"{BaseUrl}/textures/{entry.TextureHash}.png" };

        string texturesJson = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            profileId = entry.UndashedUuid,
            profileName = entry.Username,
            textures = new { SKIN = skinObj }
        });

        string valueB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(texturesJson));
        string sigB64 = "";
        if (_rsa != null)
        {
            byte[] sig = _rsa.SignData(Encoding.UTF8.GetBytes(valueB64), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            sigB64 = Convert.ToBase64String(sig);
        }

        return JsonSerializer.Serialize(new
        {
            id = entry.UndashedUuid,
            name = entry.Username,
            properties = new[]
            {
                new { name = "textures", value = valueB64, signature = sigB64 }
            }
        });
    }

    private static string ExtractQueryParam(string query, string key)
    {
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return "";
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string contentType, byte[] body, CancellationToken ct)
    {
        string header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _rsa?.Dispose();
    }
}
