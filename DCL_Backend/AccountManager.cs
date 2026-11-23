using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCL_Launcher;

public sealed class AccountManager
{
    private readonly StorageService _storage;
    private readonly HttpClient _http;
    private AccountsStore _store;

    public AccountManager(StorageService storage, HttpClient http)
    {
        _storage = storage;
        _http = http;
        _store = _storage.LoadAccounts();
    }

    public List<AccountEntry> GetAccounts()
    {
        lock (_accountsLock) return _store.Accounts.ToList();
    }

    public AccountEntry? GetActiveAccount()
    {
        lock (_accountsLock) return _store.Accounts.FirstOrDefault(a => a.Id == _store.ActiveAccountId) ?? _store.Accounts.FirstOrDefault();
    }

    public AccountEntry AddOfflineAccount(string username)
    {
        lock (_accountsLock)
        {
            string clean = string.IsNullOrWhiteSpace(username) ? $"Player_{_store.Accounts.Count + 1}" : username.Trim();
            string uuid = ComputeOfflineUuid(clean);
            var entry = new AccountEntry
            {
                Id = $"acc-{Guid.NewGuid():N}"[..12],
                Username = clean,
                Uuid = uuid,
                Type = "离线账号",
                Avatar = "assets/mc-icons/creeper.png",
                AccessToken = "00000000000000000000000000000000"
            };
            _store.Accounts.Insert(0, entry);
            _store.ActiveAccountId = entry.Id;
            _storage.SaveAccounts(_store);
            return entry;
        }
    }

    public AccountEntry? SwitchAccount(string id)
    {
        lock (_accountsLock)
        {
            var acc = _store.Accounts.FirstOrDefault(a => a.Id == id);
            if (acc == null) return null;
            _store.ActiveAccountId = acc.Id;
            _storage.SaveAccounts(_store);
            return acc;
        }
    }

    public AccountEntry? DeleteAccount(string id)
    {
        lock (_accountsLock)
        {
            int idx = _store.Accounts.FindIndex(a => a.Id == id);
            if (idx < 0) return null;
            var removed = _store.Accounts[idx];
            _store.Accounts.RemoveAt(idx);
            if (_store.ActiveAccountId == id)
            {
                _store.ActiveAccountId = _store.Accounts.FirstOrDefault()?.Id ?? "";
            }
            _storage.SaveAccounts(_store);
            return removed;
        }
    }

    public AccountEntry? SetOfflineSkin(string id, string skinPath, string skinModel, string avatarRoute)
    {
        lock (_accountsLock)
        {
            var acc = _store.Accounts.FirstOrDefault(a => a.Id == id);
            if (acc == null) return null;
            if (acc.Type.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("正版账号请在 Minecraft 官网或微软账户中修改皮肤。");
            acc.SkinPath = skinPath;
            acc.SkinModel = skinModel == "slim" ? "slim" : "default";
            acc.Avatar = avatarRoute;
            _storage.SaveAccounts(_store);
            return acc;
        }
    }

    public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int Interval, DateTimeOffset ExpiresAt);

    private const string MicrosoftEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string Scope = "XboxLive.signin offline_access";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _accountsLock = new();

    public async Task<DeviceCode> BeginMicrosoftDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        if (!Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("请先在 DCL/config/settings.json 配置 Microsoft 应用的 Client ID。");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["scope"] = Scope
        });
        using var response = await _http.PostAsync(MicrosoftEndpoint + "devicecode?mkt=zh-CN", form, ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"微软设备授权请求失败（{ResourceCatalog.Text(body.RootElement, "error", ((int)response.StatusCode).ToString())}）。请检查 Client ID 和公共客户端设置。");
        var device = body.RootElement;
        string verificationUri = ResourceCatalog.Text(device, "verification_uri");
        if (!Uri.TryCreate(verificationUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com"))
            throw new InvalidDataException("微软返回的验证地址无效。");

        return new(DeviceCodeValue(device, "device_code"), DeviceCodeValue(device, "user_code"), verificationUri,
            Math.Max(1, (int)ResourceCatalog.Number(device, "interval")),
            DateTimeOffset.UtcNow.AddSeconds(ResourceCatalog.Number(device, "expires_in")));
    }

    private static string DeviceCodeValue(JsonElement device, string key)
    {
        string value = ResourceCatalog.Text(device, key);
        return value.Length > 0 ? value : throw new InvalidDataException("微软设备授权响应缺少预期字段。");
    }

    public async Task<AccountEntry> CompleteMicrosoftLoginChainAsync(string clientId, DeviceCode device, CancellationToken ct)
    {
        int interval = device.Interval;
        while (DateTimeOffset.UtcNow.AddSeconds(interval) < device.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId, ["device_code"] = device.Code
            });
            using var response = await _http.PostAsync(MicrosoftEndpoint + "token", form, ct);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (response.IsSuccessStatusCode)
            {
                var account = await ExchangeMinecraftAccountAsync(body.RootElement, "", ct);
                lock (_accountsLock)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = _store.Accounts.FirstOrDefault(a => a.Type.Contains("Microsoft") && a.Uuid == account.Uuid);
                    if (existing != null) account.Id = existing.Id;
                    _store.Accounts.RemoveAll(a => a.Id == account.Id);
                    _store.Accounts.Insert(0, account);
                    _store.ActiveAccountId = account.Id;
                    _storage.SaveAccounts(_store);
                }
                return account;
            }
            switch (ResourceCatalog.Text(body.RootElement, "error"))
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                case "authorization_declined":
                case "access_denied": throw new InvalidOperationException("微软授权已被拒绝。");
                case "expired_token": throw new InvalidOperationException("授权码已过期，请重新发起登录。");
                default: throw new InvalidOperationException($"微软设备授权失败（HTTP {(int)response.StatusCode}）。");
            }
        }
        throw new InvalidOperationException("授权码已过期，请重新发起登录。");
    }

    public async Task<AccountEntry?> EnsureFreshAsync(AccountEntry? account, string clientId, CancellationToken ct, bool force = false)
    {
        if (account == null || !account.Type.Contains("Microsoft")) return account;
        await _refreshLock.WaitAsync(ct);
        try
        {
            lock (_accountsLock)
                account = _store.Accounts.FirstOrDefault(a => a.Id == account.Id)
                    ?? throw new InvalidOperationException("此微软账号已移除。");
            if (!force && account.AccessToken is { Length: > 0 } && account.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return account;
            if (string.IsNullOrWhiteSpace(account.RefreshToken) || !Guid.TryParse(clientId, out _))
                throw new InvalidOperationException("微软会话需要重新登录，请前往「账号与 Java」。");

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = clientId,
                ["refresh_token"] = account.RefreshToken, ["scope"] = Scope
            });
            using var response = await _http.PostAsync(MicrosoftEndpoint + "token", form, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"微软会话刷新失败（HTTP {(int)response.StatusCode}），请检查网络或重新登录。");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var renewed = await ExchangeMinecraftAccountAsync(body.RootElement, account.RefreshToken, ct);
            if (!string.Equals(renewed.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("刷新后的 Minecraft 账号与当前账号不一致。");
            lock (_accountsLock)
            {
                int index = _store.Accounts.IndexOf(account);
                if (index < 0) throw new InvalidOperationException("此微软会话已变更，请重新启动游戏。");
                renewed.Id = account.Id;
                _store.Accounts[index] = renewed;
                _storage.SaveAccounts(_store);
            }
            return renewed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AccountEntry> ExchangeMinecraftAccountAsync(JsonElement microsoftToken, string previousRefresh, CancellationToken ct)
    {
        string msaAccess = DeviceCodeValue(microsoftToken, "access_token");
        string msaRefresh = ResourceCatalog.Text(microsoftToken, "refresh_token", previousRefresh);

        // 2. Xbox Live Auth
        var xblPayload = new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={msaAccess}" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        using var xblResp = await _http.PostAsJsonAsync("https://user.auth.xboxlive.com/user/authenticate", xblPayload, ct);
        if (!xblResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xbox Live 登录失败（HTTP {(int)xblResp.StatusCode}）。");
        using var xblDoc = JsonDocument.Parse(await xblResp.Content.ReadAsStringAsync(ct));
        string xblToken = xblDoc.RootElement.GetProperty("Token").GetString() ?? "";
        string uhs = xblDoc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString() ?? "";

        // 3. XSTS Token
        var xstsPayload = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        using var xstsResp = await _http.PostAsJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsPayload, ct);
        using var xstsDoc = JsonDocument.Parse(await xstsResp.Content.ReadAsStringAsync(ct));
        if (!xstsResp.IsSuccessStatusCode)
        {
            long error = ResourceCatalog.Number(xstsDoc.RootElement, "XErr");
            throw new InvalidOperationException(error switch
            {
                2148916233 => "该账号尚未创建 Xbox 玩家资料，请先登录 xbox.com 完成设置。",
                2148916238 => "该账号需要在 Microsoft 家庭组中获得 Xbox 使用权限。",
                _ => $"Xbox XSTS 授权失败（HTTP {(int)xstsResp.StatusCode}，XErr {error}）。"
            });
        }
        string xstsToken = xstsDoc.RootElement.GetProperty("Token").GetString() ?? "";

        // 4. Minecraft Access Token
        var mcAuthPayload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        using var mcResp = await _http.PostAsJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", mcAuthPayload, ct);
        if (mcResp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Minecraft 服务拒绝了 Xbox 登录。请检查该 Client ID 的 Java 版 API 访问审批。");
        if (!mcResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Minecraft 登录失败（HTTP {(int)mcResp.StatusCode}）。");
        using var mcDoc = JsonDocument.Parse(await mcResp.Content.ReadAsStringAsync(ct));
        string mcAccessToken = DeviceCodeValue(mcDoc.RootElement, "access_token");
        var expires = DateTimeOffset.UtcNow.AddSeconds(ResourceCatalog.Number(mcDoc.RootElement, "expires_in"));

        // 5. Minecraft Profile
        using var profReq = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccessToken);
        using var profResp = await _http.SendAsync(profReq, ct);
        if (profResp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("此账号没有 Minecraft Java 版玩家资料。请确认游戏权益，并在官方启动器中创建 Java 玩家名称。");
        if (!profResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"无法读取 Minecraft 玩家资料（HTTP {(int)profResp.StatusCode}）。");
        using var profDoc = JsonDocument.Parse(await profResp.Content.ReadAsStringAsync(ct));
        string uuid = DeviceCodeValue(profDoc.RootElement, "id");
        string name = DeviceCodeValue(profDoc.RootElement, "name");

        return new AccountEntry
        {
            Id = $"ms-{uuid}",
            Username = name,
            Uuid = uuid,
            Type = "Microsoft 正版",
            Avatar = "assets/mc-icons/player-head.png",
            AccessToken = mcAccessToken,
            RefreshToken = msaRefresh,
            AccessTokenExpiresAt = expires
        };
    }

    public static string ComputeOfflineUuid(string username)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[0..16].ReverseIfLittleEndian()).ToString("N");
    }
}

internal static class GuidBytesExtensions
{
    public static byte[] ReverseIfLittleEndian(this byte[] b)
    {
        if (!BitConverter.IsLittleEndian) return b;
        var clone = (byte[])b.Clone();
        Array.Reverse(clone, 0, 4);
        Array.Reverse(clone, 4, 2);
        Array.Reverse(clone, 6, 2);
        return clone;
    }
}
