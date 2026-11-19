using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DCL_Launcher;

public sealed class HostBridge
{
    private readonly StorageService _storage;
    private readonly HttpClient _http;
    private readonly JavaManager _javaMgr;
    private readonly DownloadManager _dlMgr;
    private readonly InstanceManager _instMgr;
    private readonly AccountManager _accMgr;
    private readonly MinecraftApiService _mcApi;
    private readonly GameLauncher _launcher;
    private readonly CurseForgeService _curseForge;
    private readonly ModpackInstaller _packInstaller;
    private readonly object _workspaceRefreshLock = new();
    private Task? _javaScanTask;
    private (string Token, string Path)? _resourceInstallFolder;
    private (string Token, string Path)? _javaInstallFolder;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingPreSpawnLaunches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, Dictionary<string, object?> Project, List<ResourceFile> Files)> _resourceCache = new();

    public bool AlwaysOnTop => _cfg.AlwaysOnTop;

    public bool SetAlwaysOnTop(bool pinned)
    {
        _cfg.AlwaysOnTop = pinned;
        _storage.SaveConfig(_cfg);
        return _cfg.AlwaysOnTop;
    }

    private string DownloadSource => _cfg.AutoMirror ? "最优" : _cfg.MirrorSource;

    // 发行前填入在 Azure / Microsoft Entra 注册并获 Mojang 审批通过的公共客户端 Application (client) ID（UUID）
    private const string DefaultMicrosoftClientId = "";

    private string MicrosoftClientId =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("DCL_MICROSOFT_CLIENT_ID"), _cfg.MicrosoftClientId, DefaultMicrosoftClientId);
    private string CurseForgeKey =>
        FirstNonEmpty(Environment.GetEnvironmentVariable("DCL_CURSEFORGE_API_KEY"), _cfg.CurseForgeApiKey, BuildSecrets.CurseForgeApiKey);
    private string CurseForgeApiRoute => "auto";

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(v => (v ?? "").Trim()).FirstOrDefault(v => v.Length > 0) ?? "";

    private DclConfig _cfg;
    private List<JavaInstallation> _javas = new();
    private List<InstanceMeta> _instances = new();
    private List<ModEntry> _mods = new();
    private List<PackEntry> _packs = new();
    private List<Dictionary<string, object?>> _catalogVersions = new();
    private List<string> _latestCrashLines = new();
    private bool _hasUnseenDownloadBall;
    private bool _microsoftStarting;
    private (string Id, string VerificationUri, CancellationTokenSource Cancellation, Task<AccountEntry> Task)? _microsoftLogin;

    private readonly List<(string Name, string Url, double Avail, int Bw)> _mirrorDefs = new()
    {
        ("BMCLAPI", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", 0.992, 420),
        ("MCIM", "https://mod.mcimirror.top/", 0.985, 310),
        ("官方源", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", 0.995, 140)
    };
    private Dictionary<string, int> _mirrorPings = new()
    {
        ["BMCLAPI"] = 28,
        ["MCIM"] = 42,
        ["官方源"] = 185
    };

    public event Action<string, object?>? PushEvent;

    public HostBridge(StorageService storage, HttpClient? http = null)
    {
        _storage = storage;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3),
            MaxConnectionsPerServer = MinecraftApiService.AssetDownloadConcurrency,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromMinutes(8),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DCL-Launcher/1.0");
        _javaMgr = new JavaManager(_storage, _http);
        _dlMgr = new DownloadManager(_storage, _http);
        _instMgr = new InstanceManager(_storage);
        _accMgr = new AccountManager(_storage, _http);
        _mcApi = new MinecraftApiService(_storage, _http, _dlMgr);
        _launcher = new GameLauncher(_storage, _mcApi);
        _curseForge = new CurseForgeService(_http);
        _packInstaller = new ModpackInstaller(_storage, _dlMgr, _mcApi, _curseForge);

        _cfg = _storage.LoadConfig();
        _dlMgr.TasksChanged += () => PushEvent?.Invoke("download_updated", _dlMgr.GetTasksSnapshot());

        RefreshLocalWorkspace();
        _ = BackgroundSyncOnlineCatalogAsync();
        _ = RunPeriodicMirrorProbeLoopAsync();
    }

    private async Task RunPeriodicMirrorProbeLoopAsync()
    {
        try
        {
            // Initial probe shortly after WebView attaches so live RTT & auto-switch emit cleanly
            await Task.Delay(3500);
            while (true)
            {
                await ProbeMirrorsAndMaybeSwitchAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex)
        {
            _storage.Log($"MirrorProbeLoop stopped: {ex.Message}");
        }
    }

    private async Task<object> ProbeMirrorsAndMaybeSwitchAsync(CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(6));
        string prevMirror = _cfg.MirrorSource;
        var tasks = _mirrorDefs.Select(async m => (m.Name, ms: await _dlMgr.PingHostAsync(m.Url, probeCts.Token)));
        foreach (var res in await Task.WhenAll(tasks))
        {
            _mirrorPings[res.Name] = res.ms;
        }
        bool switched = false;
        if (_cfg.AutoMirror)
        {
            string best = PickOptimalMirror();
            if (!string.Equals(best, _cfg.MirrorSource, StringComparison.OrdinalIgnoreCase))
            {
                int curPing = _mirrorPings.GetValueOrDefault(_cfg.MirrorSource, 9999);
                double curScore = ScoreMirror(_mirrorDefs.First(m => m.Name == _cfg.MirrorSource));
                double bestScore = ScoreMirror(_mirrorDefs.First(m => m.Name == best));
                if (curPing >= 1200 || bestScore + 12.0 < curScore)
                {
                    _cfg.MirrorSource = best;
                    _storage.SaveConfig(_cfg);
                    switched = !string.Equals(prevMirror, best, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        var dto = BuildMirrorSnapshotDto(switched);
        PushEvent?.Invoke("mirror_updated", dto);
        return dto;
    }

    private double ScoreMirror((string Name, string Url, double Avail, int Bw) m)
    {
        int ms = _mirrorPings.GetValueOrDefault(m.Name, 500);
        double penalty = ms >= 2500 ? 3000 : (1 - m.Avail) * 1000;
        return ms + penalty - m.Bw * 0.04;
    }

    private object BuildMirrorSnapshotDto(bool switched)
    {
        return new
        {
            mirrorSource = _cfg.MirrorSource,
            autoMirror = _cfg.AutoMirror,
            switched,
            mirrors = _mirrorDefs.Select(m => new
            {
                name = m.Name,
                pingMs = _mirrorPings.GetValueOrDefault(m.Name, 80),
                availability = m.Avail,
                bandwidthMbps = m.Bw,
                score = Math.Round(ScoreMirror(m), 2)
            }).ToList()
        };
    }

    public void RefreshLocalWorkspace()
    {
        lock (_workspaceRefreshLock)
        {
            var (totalRam, availRam) = SystemMemoryHelper.GetSystemMemoryMb();
            _javas = _javaMgr.ScanLocalJavas(_cfg.ActiveJavaId);
            _instances = _instMgr.ScanVersions(_cfg, _javas, totalRam, availRam);

            if (!_instances.Any(i => i.Id == _cfg.ActiveInstanceId))
                _cfg.ActiveInstanceId = _instances.FirstOrDefault()?.Id ?? "";
            RefreshActiveResources();
            if (_cfg.AutoMemory)
            {
                _cfg.MemoryMb = ComputeAutoMemoryForActive(totalRam, availRam);
            }
            _latestCrashLines = _launcher.AnalyzeCrashLogs(_storage.GameDirectory(_cfg.ActiveInstanceId));
            _storage.SaveConfig(_cfg);
        }
    }

    private int ComputeAutoMemoryForInstance(InstanceMeta? inst, int totalRam, int availRam)
    {
        int mods = inst != null ? inst.ModsCount : _mods.Count(m => m.Enabled);
        int shaders = inst != null ? _instMgr.CountShaders(inst.Id) : 0;
        return SystemMemoryHelper.ComputeRecommendedMemoryMb(
            totalRam,
            availRam,
            inst?.McVersion,
            inst?.Loader,
            mods,
            inst?.IsModpack ?? false,
            shaders);
    }

    private int ComputeAutoMemoryForActive(int totalRam, int availRam)
    {
        var inst = _instances.FirstOrDefault(i => i.Id == _cfg.ActiveInstanceId) ?? _instances.FirstOrDefault();
        return ComputeAutoMemoryForInstance(inst, totalRam, availRam);
    }

    private void SyncAutoMemoryForInstance(InstanceMeta? inst, bool persistInstance = true)
    {
        var (tot, av) = SystemMemoryHelper.GetSystemMemoryMb();
        if (inst != null)
        {
            inst.ModsCount = inst.Loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase)
                ? 0
                : _instMgr.CountEnabledMods(inst.Id);
            if (inst.AutoMemory)
            {
                inst.MemoryMb = ComputeAutoMemoryForInstance(inst, tot, av);
                if (persistInstance) SaveSingleInstance(inst);
            }
        }
        if (_cfg.AutoMemory)
        {
            _cfg.MemoryMb = ComputeAutoMemoryForActive(tot, av);
            _storage.SaveConfig(_cfg);
        }
    }

    private async Task BackgroundSyncOnlineCatalogAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Ping mirrors in parallel
            var pingTasks = _mirrorDefs.Select(async m =>
            {
                int ms = await _dlMgr.PingHostAsync(m.Url, cts.Token);
                return (m.Name, ms);
            });
            foreach (var res in await Task.WhenAll(pingTasks))
            {
                _mirrorPings[res.Name] = res.ms;
            }
            if (_cfg.AutoMirror)
            {
                _cfg.MirrorSource = PickOptimalMirror();
                _storage.SaveConfig(_cfg);
            }

            // Resource catalogs load on demand when their pages are opened.
            _catalogVersions = await _mcApi.FetchVersionManifestAsync(DownloadSource, cts.Token);

            PushEvent?.Invoke("catalog_synced", new { versionsCount = _catalogVersions.Count });
        }
        catch (Exception ex)
        {
            _storage.Log($"BackgroundSync error: {ex.Message}");
        }
    }

    private string PickOptimalMirror()
    {
        return _mirrorDefs
            .OrderBy(ScoreMirror)
            .First().Name;
    }

    public object BuildStateDto()
    {
        var (totalRam, availRam) = SystemMemoryHelper.GetSystemMemoryMb();
        var accounts = _accMgr.GetAccounts().Select(PublicAccount).ToArray();
        var activeAcc = _accMgr.GetActiveAccount();
        var favIds = _instances.Where(i => i.Favorite).Select(i => i.Id).Take(6).ToList();

        return new
        {
            activeInstanceId = _cfg.ActiveInstanceId,
            recentInstanceIds = favIds,
            mirrorSource = _cfg.MirrorSource,
            autoMirror = _cfg.AutoMirror,
            mirrors = _mirrorDefs.Select(m => new
            {
                name = m.Name,
                pingMs = _mirrorPings.GetValueOrDefault(m.Name, 80),
                availability = m.Avail,
                bandwidthMbps = m.Bw,
                score = Math.Round(_mirrorPings.GetValueOrDefault(m.Name, 80) + (1 - m.Avail) * 1000 - m.Bw * 0.04, 2)
            }),
            downloadTasks = _dlMgr.GetTasksSnapshot(),
            hasUnseenDownloadBall = _hasUnseenDownloadBall,
            networkLoading = false,
            autoMemory = _cfg.AutoMemory,
            memoryMb = _cfg.MemoryMb,
            systemRamMb = totalRam,
            availableRamMb = availRam,
            jvmPreset = _cfg.JvmPreset,
            customJvmArgs = _cfg.CustomJvmArgs,
            themeId = _cfg.ThemeId,
            bgPatternId = _cfg.BgPatternId == "festive" ? "festive" : "none",
            alwaysOnTop = _cfg.AlwaysOnTop,
            hasMicrosoftClientId = MicrosoftClientId.Length > 0,
            hasCurseForgeApiKey = CurseForgeKey.Length > 0,
            curseForgeApiRoute = CurseForgeApiRoute,
            officialJavas = new[]
            {
                new { id = "off-25", version = "25", fullVersion = "25.0.0", channel = "Latest", vendor = "Adoptium / Oracle", url = "https://adoptium.net/", size = "184 MB", note = "最新官方特性发布版" },
                new { id = "off-21", version = "21", fullVersion = "21.0.6 LTS", channel = "LTS", vendor = "Eclipse Temurin", url = "https://adoptium.net/temurin/releases/?version=21", size = "194 MB", note = "MC 1.20.5+ 官方长期支持版" },
                new { id = "off-17", version = "17", fullVersion = "17.0.14 LTS", channel = "LTS", vendor = "Eclipse Temurin", url = "https://adoptium.net/temurin/releases/?version=17", size = "186 MB", note = "MC 1.18 - 1.20.4 长期支持版" },
                new { id = "off-11", version = "11", fullVersion = "11.0.26 LTS", channel = "LTS", vendor = "Eclipse Temurin", url = "https://adoptium.net/temurin/releases/?version=11", size = "177 MB", note = "MC 1.17 过渡兼容版本" },
                new { id = "off-8", version = "8", fullVersion = "8u442 LTS", channel = "LTS", vendor = "Eclipse Temurin", url = "https://adoptium.net/temurin/releases/?version=8", size = "99 MB", note = "MC 1.16.5 及以下经典版" }
            },
            accounts,
            account = activeAcc != null
                ? new { loggedIn = true, id = activeAcc.Id, username = activeAcc.Username, uuid = activeAcc.Uuid, type = activeAcc.Type, avatar = activeAcc.Avatar }
                : new { loggedIn = false, id = "", username = "", uuid = "-", type = "未登录", avatar = "" },
            instances = _instances,
            installedMods = _mods,
            installedPacks = _packs,
            javas = _javas,
            crashLines = _latestCrashLines,
            catalog = new
            {
                versions = _catalogVersions,
                mods = System.Array.Empty<object>(),
                packs = System.Array.Empty<object>(),
                shaders = System.Array.Empty<object>(),
                modpacks = System.Array.Empty<object>()
            }
        };
    }

    public async Task<object?> HandleCommandAsync(string cmd, JsonElement payload)
    {
        switch (cmd)
        {
            case "get_state":
                return BuildStateDto();

            case "set_theme":
            {
                _cfg.ThemeId = GetStr(payload, "themeId", _cfg.ThemeId);
                _storage.SaveConfig(_cfg);
                return new { themeId = _cfg.ThemeId };
            }

            case "set_bg_pattern":
            {
                string nextPattern = GetStr(payload, "bgPatternId", "none");
                _cfg.BgPatternId = nextPattern == "festive" ? "festive" : "none";
                _storage.SaveConfig(_cfg);
                return new { bgPatternId = _cfg.BgPatternId };
            }

            case "set_active_instance":
            {
                string id = GetStr(payload, "id");
                var activeInst = _instances.FirstOrDefault(i => i.Id == id)
                    ?? throw new InvalidOperationException("该版本未安装");
                _cfg.ActiveInstanceId = id;
                RefreshActiveResources();
                SyncAutoMemoryForInstance(activeInst);
                _storage.SaveConfig(_cfg);
                return new { activeInstanceId = _cfg.ActiveInstanceId, memoryMb = _cfg.MemoryMb, instanceMemoryMb = activeInst.MemoryMb };
            }

            case "probe_mirrors":
            {
                return await ProbeMirrorsAndMaybeSwitchAsync(CancellationToken.None);
            }

            case "set_mirror":
            {
                bool auto = GetBool(payload, "auto");
                if (auto)
                {
                    _cfg.AutoMirror = true;
                    _storage.SaveConfig(_cfg);
                    await ProbeMirrorsAndMaybeSwitchAsync(CancellationToken.None);
                }
                else
                {
                    var requested = GetStr(payload, "mirror", _cfg.MirrorSource);
                    if (!_mirrorDefs.Any(node => node.Name == requested))
                        throw new ArgumentException("未知下载源");
                    _cfg.MirrorSource = requested;
                    _cfg.AutoMirror = false;
                    _storage.SaveConfig(_cfg);
                    PushEvent?.Invoke("mirror_updated", BuildMirrorSnapshotDto(false));
                }
                _storage.Log($"下载源设置为 {DownloadSource}，应用于后续任务");
                return new { mirror = _cfg.MirrorSource, auto = _cfg.AutoMirror, mirrors = BuildMirrorSnapshotDto(false) };
            }

            case "set_curseforge_route":
            {
                string nextRoute = CurseForgeService.NormalizeRoute(GetStr(payload, "route", "auto"));
                _cfg.CurseForgeApiRoute = nextRoute;
                _storage.SaveConfig(_cfg);
                foreach (var k in _resourceCache.Keys.Where(k => k.Contains(":cf-", StringComparison.Ordinal)).ToArray())
                    _resourceCache.TryRemove(k, out _);
                _storage.Log($"CurseForge 接口线路设置为 {nextRoute}");
                return new { curseForgeApiRoute = _cfg.CurseForgeApiRoute };
            }

            case "set_memory":
            {
                bool auto = GetBool(payload, "auto");
                _cfg.AutoMemory = auto;
                if (auto)
                {
                    var (tot, av) = SystemMemoryHelper.GetSystemMemoryMb();
                    _cfg.MemoryMb = ComputeAutoMemoryForActive(tot, av);
                }
                else
                {
                    _cfg.MemoryMb = Math.Clamp(GetInt(payload, "mb", _cfg.MemoryMb), 1024, 16384);
                }
                _storage.SaveConfig(_cfg);
                return new { auto = _cfg.AutoMemory, mb = _cfg.MemoryMb };
            }

            case "set_jvm_args":
            {
                string nextArgs = GetStr(payload, "jvmArgs");
                _cfg.CustomJvmArgs = nextArgs;
                _cfg.JvmPreset = nextArgs.Contains("UseZGC") ? "Gen-ZGC" : nextArgs.Contains("UseG1GC") ? "G1GC" : "Custom";
                _storage.SaveConfig(_cfg);
                return new { customJvmArgs = _cfg.CustomJvmArgs };
            }

            case "update_instance_settings":
            {
                string id = GetStr(payload, "id");
                var inst = _instances.FirstOrDefault(i => i.Id == id);
                if (inst == null) return null;

                var map = _storage.LoadInstanceMetaMap();
                if (payload.TryGetProperty("useGlobalSettings", out var ugs) && ugs.ValueKind == JsonValueKind.True)
                {
                    inst.UseGlobalSettings = true;
                    inst.AutoMemory = _cfg.AutoMemory;
                    var (tot, av) = SystemMemoryHelper.GetSystemMemoryMb();
                    inst.MemoryMb = _cfg.AutoMemory ? ComputeAutoMemoryForInstance(inst, tot, av) : _cfg.MemoryMb;
                    inst.JvmArgs = _cfg.CustomJvmArgs;
                    var actJava = _javas.FirstOrDefault(j => j.Active) ?? _javas.FirstOrDefault();
                    if (actJava != null)
                    {
                        inst.JavaId = actJava.Id;
                        inst.JavaVersion = InstanceManager.FormatJavaLabel(actJava.Version);
                    }
                }
                else
                {
                    if (payload.TryGetProperty("useGlobalSettings", out var ugs2) && ugs2.ValueKind == JsonValueKind.False)
                        inst.UseGlobalSettings = false;
                    if (payload.TryGetProperty("jvmArgs", out var ja) && ja.ValueKind == JsonValueKind.String)
                    {
                        inst.JvmArgs = ja.GetString() ?? "";
                        inst.UseGlobalSettings = false;
                    }
                    if (payload.TryGetProperty("autoMemory", out var am) && am.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        inst.AutoMemory = am.GetBoolean();
                        inst.UseGlobalSettings = false;
                        if (inst.AutoMemory)
                        {
                            var (tot, av) = SystemMemoryHelper.GetSystemMemoryMb();
                            inst.MemoryMb = ComputeAutoMemoryForInstance(inst, tot, av);
                        }
                    }
                    if (payload.TryGetProperty("memoryMb", out var mm) && mm.ValueKind == JsonValueKind.Number)
                    {
                        inst.MemoryMb = Math.Clamp(mm.GetInt32(), 1024, 16384);
                        inst.AutoMemory = false;
                        inst.UseGlobalSettings = false;
                    }
                    if (payload.TryGetProperty("javaId", out var jid) && jid.ValueKind == JsonValueKind.String)
                    {
                        string targetJavaId = jid.GetString() ?? "";
                        var j = _javas.FirstOrDefault(x => x.MatchesId(targetJavaId));
                        if (j != null)
                        {
                            inst.JavaId = j.Id;
                            inst.JavaVersion = InstanceManager.FormatJavaLabel(j.Version);
                            inst.UseGlobalSettings = false;
                        }
                    }
                }
                map[inst.Id] = inst;
                _storage.SaveInstanceMetaMap(map);
                return inst;
            }

            case "set_instance_icon":
            {
                string id = GetStr(payload, "id");
                var inst = _instances.FirstOrDefault(i => i.Id == id);
                if (inst == null) return null;
                inst.IconId = GetStr(payload, "iconId", "grass");
                inst.CustomIcon = null;
                SaveSingleInstance(inst);
                return inst;
            }

            case "set_instance_color":
            {
                string id = GetStr(payload, "id");
                var inst = _instances.FirstOrDefault(i => i.Id == id);
                if (inst == null) return null;
                inst.FolderColor = GetStr(payload, "colorId", "amber");
                if (payload.TryGetProperty("customHex", out var ch) && ch.ValueKind == JsonValueKind.String)
                    inst.CustomFolderHex = ch.GetString();
                SaveSingleInstance(inst);
                return inst;
            }

            case "toggle_favorite_instance":
            {
                string id = GetStr(payload, "id");
                var inst = _instances.FirstOrDefault(i => i.Id == id);
                if (inst == null) return null;
                inst.Favorite = !inst.Favorite;
                SaveSingleInstance(inst);
                return inst;
            }

            case "delete_instance":
            {
                string id = GetStr(payload, "id");
                int idx = _instances.FindIndex(i => i.Id == id);
                if (idx < 0) return null;
                var removed = _instances[idx];
                _instances.RemoveAt(idx);
                if (!string.IsNullOrWhiteSpace(removed.ExternalPath))
                {
                    // External linked folder: unlink from launcher only, never delete user files on disk
                    _storage.UnregisterExternalInstancePath(id);
                }
                else
                {
                    string verDir = Path.Combine(_storage.VersionsDir, id);
                    if (Directory.Exists(verDir)) Directory.Delete(verDir, true);
                }
                var map = _storage.LoadInstanceMetaMap();
                map.Remove(id);
                _storage.SaveInstanceMetaMap(map);
                if (_cfg.ActiveInstanceId == id)
                {
                    _cfg.ActiveInstanceId = _instances.FirstOrDefault()?.Id ?? "";
                    RefreshActiveResources();
                    SyncAutoMemoryForInstance(_instances.FirstOrDefault(i => i.Id == _cfg.ActiveInstanceId), persistInstance: false);
                    _storage.SaveConfig(_cfg);
                }
                return removed;
            }

            case "toggle_mod":
            {
                if (_cfg.ActiveInstanceId.Length == 0) return null;
                var mod = _instMgr.ToggleMod(_cfg.ActiveInstanceId, GetStr(payload, "id"));
                RefreshActiveResources();
                SyncAutoMemoryForInstance(_instances.FirstOrDefault(i => i.Id == _cfg.ActiveInstanceId));
                return mod;
            }

            case "delete_mod":
            {
                bool ok = _cfg.ActiveInstanceId.Length > 0 && _instMgr.DeleteMod(_cfg.ActiveInstanceId, GetStr(payload, "id"));
                RefreshActiveResources();
                SyncAutoMemoryForInstance(_instances.FirstOrDefault(i => i.Id == _cfg.ActiveInstanceId));
                return new { ok };
            }

            case "toggle_pack":
            {
                if (_cfg.ActiveInstanceId.Length == 0) return null;
                var pack = _instMgr.TogglePack(_cfg.ActiveInstanceId, GetStr(payload, "id"));
                RefreshActiveResources();
                return pack;
            }

            case "set_active_java":
            {
                string id = GetStr(payload, "id");
                var selected = _javas.FirstOrDefault(java => java.MatchesId(id));
                if (selected == null) return null;
                foreach (var java in _javas) java.Active = java == selected;
                _cfg.ActiveJavaId = selected.Id;
                _storage.SaveConfig(_cfg);
                return selected;
            }

            case "scan_local_javas":
            {
                var previousPaths = _javas.Select(java => java.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Task scan;
                lock (_workspaceRefreshLock)
                    scan = _javaScanTask ??= _javaMgr.SearchLocalDrivesAsync();
                try
                {
                    await scan;
                    RefreshLocalWorkspace();
                    return new { added = _javas.Count(java => !previousPaths.Contains(java.Path)), javas = _javas };
                }
                finally
                {
                    lock (_workspaceRefreshLock)
                        if (_javaScanTask == scan) _javaScanTask = null;
                }
            }

            case "install_java_official":
            {
                string offId = GetStr(payload, "id", "off-21");
                string vendorId = GetStr(payload, "vendor", "temurin");
                string vendorLabel = vendorId.ToLowerInvariant() switch
                {
                    "oracle" => "Oracle JDK",
                    "zulu" => "Azul Zulu",
                    "microsoft" => "Microsoft OpenJDK",
                    _ => "Eclipse Temurin"
                };
                string folderToken = GetStr(payload, "folderToken");
                string? targetInstallDir = null;
                if (!string.IsNullOrWhiteSpace(folderToken))
                {
                    if (_javaInstallFolder is not { } jFolder || jFolder.Token != folderToken || !Directory.Exists(jFolder.Path))
                        throw new InvalidOperationException("所选 Java 安装目录失效，请重新选择。");
                    targetInstallDir = jFolder.Path;
                }
                int featureVer = int.TryParse(offId.Replace("off-", ""), out int fv) ? fv : 21;
                var task = _dlMgr.Enqueue(
                    $"Java {featureVer} ({vendorLabel})",
                    targetInstallDir != null ? $"安装至 {targetInstallDir}" : $"{vendorLabel} 安装包",
                    "assets/mc-icons/command.png",
                    vendorLabel,
                    "~190 MB",
                    async (entry, prog, ct) =>
                    {
                        prog.Report((5, $"查询 {vendorLabel} 下载信息..."));
                        var (url, fileName) = await _javaMgr.ResolveVendorJavaDownloadAsync(vendorId, featureVer, ct);
                        string zipPath = Path.Combine(_storage.CacheDir, "java", fileName);
                        await _dlMgr.DownloadFileWithSha1Async(url, zipPath, null, (rec, tot) =>
                        {
                            if (tot > 0) prog.Report((10 + (int)(rec * 75 / tot.Value), $"{(rec / 1048576.0):F1}/{(tot.Value / 1048576.0):F1} MB"));
                        }, ct);
                        prog.Report((90, "解压运行时..."));
                        await _javaMgr.ExtractJavaArchiveAsync(zipPath, featureVer, ct, targetInstallDir);
                        RefreshLocalWorkspace();
                        PushEvent?.Invoke("workspace_refreshed", BuildStateDto());
                    });
                if (!string.IsNullOrWhiteSpace(folderToken) && _javaInstallFolder?.Token == folderToken) _javaInstallFolder = null;
                _hasUnseenDownloadBall = true;
                return new { task, javas = _javas };
            }

            case "switch_account":
            {
                var account = _accMgr.SwitchAccount(GetStr(payload, "id"));
                return account is null ? null : PublicAccount(account);
            }

            case "add_account":
                return PublicAccount(_accMgr.AddOfflineAccount(GetStr(payload, "username")));

            case "delete_account":
            {
                var account = _accMgr.DeleteAccount(GetStr(payload, "id"));
                return account is null ? null : PublicAccount(account);
            }

            case "microsoft_begin":
            {
                if (_microsoftStarting || _microsoftLogin != null)
                    throw new InvalidOperationException("已有微软登录正在进行。");
                _microsoftStarting = true;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    string clientId = MicrosoftClientId;
                    var device = await _accMgr.BeginMicrosoftDeviceCodeAsync(clientId, timeout.Token);
                    var cancellation = new CancellationTokenSource(device.ExpiresAt - DateTimeOffset.UtcNow);
                    var id = Guid.NewGuid().ToString("N");
                    _microsoftLogin = (id, device.VerificationUri, cancellation,
                        _accMgr.CompleteMicrosoftLoginChainAsync(clientId, device, cancellation.Token));
                    return new { id, device.UserCode, device.ExpiresAt };
                }
                finally
                {
                    _microsoftStarting = false;
                }
            }

            case "microsoft_wait":
            {
                if (_microsoftLogin is not { } login || login.Id != GetStr(payload, "id"))
                    throw new InvalidOperationException("未找到该微软登录请求。");
                try
                {
                    return PublicAccount(await login.Task);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                finally
                {
                    if (_microsoftLogin?.Id == login.Id) _microsoftLogin = null;
                    login.Cancellation.Dispose();
                }
            }

            case "microsoft_cancel":
                if (_microsoftLogin is { } pending && pending.Id == GetStr(payload, "id"))
                    pending.Cancellation.Cancel();
                return null;

            case "microsoft_refresh":
            {
                var active = _accMgr.GetActiveAccount();
                if (active == null || !active.Type.Contains("Microsoft"))
                    throw new InvalidOperationException("请先选择 Microsoft 正版账号。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var updated = await _accMgr.EnsureFreshAsync(active, MicrosoftClientId, timeout.Token, force: true);
                return PublicAccount(updated!);
            }

            case "open_microsoft_verification":
            {
                if (_microsoftLogin is not { } login || login.Id != GetStr(payload, "id"))
                    throw new InvalidOperationException("未找到该微软登录请求。");
                Process.Start(new ProcessStartInfo(login.VerificationUri) { UseShellExecute = true });
                return null;
            }

            case "search_catalog":
            case "search_catalog_page":
            {
                string category = GetStr(payload, "catKey", GetStr(payload, "kind", "mods"));
                string query = GetStr(payload, "query");
                string loader = GetStr(payload, "loader", "all");
                string sortBy = GetStr(payload, "sortBy", "downloads");
                string mcVersion = GetStr(payload, "mcVersion", "all");
                string resourceTag = GetStr(payload, "tag", "all");
                string resourceSubTag = GetStr(payload, "subTag", "all");
                int page = Math.Clamp(GetInt(payload, "page", 1), 1, 100000);
                int pageSize = Math.Clamp(GetInt(payload, "pageSize", 50), 10, 50);
                int explicitOffset = GetInt(payload, "offset", -1);
                int offset = explicitOffset >= 0 ? explicitOffset : (page - 1) * pageSize;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string source = GetStr(payload, "source", "all");
                var (items, totalHits) = source switch
                {
                    "CurseForge" => await _curseForge.SearchAsync(category, query, loader, offset, pageSize, CurseForgeKey, timeout.Token, sortBy, mcVersion, CurseForgeApiRoute, resourceTag, resourceSubTag),
                    "Modrinth" => await _mcApi.SearchModrinthPagedAsync(ResourceCatalog.ProjectType(category), query, loader, offset, pageSize, timeout.Token, sortBy, mcVersion, resourceTag, resourceSubTag),
                    _ => await SearchCombinedCatalogAsync(category, query, loader, offset, pageSize, sortBy, mcVersion, resourceTag, resourceSubTag, timeout.Token)
                };
                return new { items, totalHits, page, pageSize, offset };
            }

            case "resolve_mod_project":
            {
                string slug = GetStr(payload, "slug");
                string name = GetStr(payload, "name");
                string fileName = GetStr(payload, "file");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var found = await _mcApi.ResolveModProjectAsync(slug, name, fileName, timeout.Token);
                return found == null ? null : new { catKey = "mods", id = found["id"], preview = found };
            }

            case "get_resource_detail_live":
            {
                string catKey = GetStr(payload, "catKey", "mods");
                string id = GetStr(payload, "id");
                if (catKey == "versions")
                {
                    if (_catalogVersions.Count == 0)
                    {
                        _catalogVersions = await _mcApi.FetchVersionManifestAsync(DownloadSource, CancellationToken.None);
                    }
                    var vItem = _catalogVersions.FirstOrDefault(v => string.Equals(v["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));
                    using var detailTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var trees = await _mcApi.FetchVersionLoaderBuildsAsync(id, detailTimeout.Token);
                    return new Dictionary<string, object?>
                    {
                        ["catKey"] = "versions",
                        ["id"] = id,
                        ["cnName"] = "",
                        ["enName"] = $"Minecraft {id}",
                        ["icon"] = vItem?["icon"] ?? "assets/mc-icons/grass.png",
                        ["isPixel"] = true,
                        ["summary"] = $"Minecraft Java Edition {id}",
                        ["releaseTime"] = vItem?["releaseTime"] ?? "",
                        ["type"] = vItem?["type"] ?? "正式版",
                        ["java"] = vItem?["java"] ?? "Java 21",
                        ["source"] = "Mojang Studios",
                        ["loaders"] = trees.Keys.Count > 0 ? trees.Keys.ToList() : new List<string>(),
                        ["loaderTrees"] = trees
                    };
                }
                var (project, _) = await GetResourceAsync(catKey, id);
                return project;
            }

            case "get_resource_targets":
            {
                string category = GetStr(payload, "catKey");
                _ = ResourceFolder(category);
                var (_, files) = await GetResourceAsync(category, GetStr(payload, "projectId"));
                var builds = DownloadableResourceFiles(files, category, GetStr(payload, "buildId"));
                if (builds.Length == 0) throw new InvalidOperationException("该资源没有可下载的构建，或作者限制了第三方下载。");
                return _instances.Select(instance => new
                    {
                        Instance = instance,
                        Match = builds.Max(file => file.Match(instance, category))
                    })
                    .OrderByDescending(target => target.Match)
                    .ThenByDescending(target => target.Instance.LastPlayedAt)
                    .ThenByDescending(target => target.Instance.Id == _cfg.ActiveInstanceId)
                    .ThenByDescending(target => target.Instance.McVersion, ResourceCatalog.VersionComparer)
                    .ThenBy(target => target.Instance.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(target => new
                    {
                        target.Instance.Id, target.Instance.Name, target.Instance.McVersion, target.Instance.Loader,
                        match = target.Match.ToString()
                    }).ToArray();
            }

            case "add_download_task":
            {
                string name = GetStr(payload, "name", "未命名资源");
                string sub = GetStr(payload, "sub", "package.jar");
                string icon = GetStr(payload, "icon", "assets/mc-icons/grass.png");
                string size = GetStr(payload, "size", "2.4 MB");
                string installType = GetStr(payload, "installType");
                string mcVersion = GetStr(payload, "mcVersion");
                string catKey = GetStr(payload, "catKey");
                string mirror = DownloadSource;
                if (installType == "version" && !string.IsNullOrWhiteSpace(mcVersion))
                {
                    if (ResourceCatalog.Strings(payload, "loaders").Any(loader => loader != "Fabric"))
                        throw new InvalidOperationException("此版本安装器目前支持原版和 Fabric。");
                    string? verJsonUrl = _catalogVersions
                        .FirstOrDefault(v => string.Equals(v["id"]?.ToString(), mcVersion, StringComparison.OrdinalIgnoreCase))
                        ?.GetValueOrDefault("url")?.ToString();
                    if (string.IsNullOrWhiteSpace(verJsonUrl))
                    {
                        var manifest = await _mcApi.FetchVersionManifestAsync(mirror, CancellationToken.None);
                        _catalogVersions = manifest;
                        verJsonUrl = manifest.FirstOrDefault(v => v["id"]?.ToString() == mcVersion)?["url"]?.ToString();
                    }
                    if (string.IsNullOrWhiteSpace(verJsonUrl) || !InstanceManager.IsFileName(mcVersion))
                        throw new InvalidOperationException($"未找到版本 {mcVersion} 的官方清单");

                    string fabricLoader = GetStr(payload, "fabricVersion");
                    if (fabricLoader.Length == 0 && ResourceCatalog.Strings(payload, "loaders").Contains("Fabric"))
                        throw new ArgumentException("请选择一个 Fabric 构建");
                    if (fabricLoader.Length > 0 && !InstanceManager.IsFileName(fabricLoader))
                        throw new ArgumentException("无效的 Fabric 版本");
                    string instFolderName = fabricLoader.Length > 0 ? $"fabric-loader-{fabricLoader}-{mcVersion}" :
                        File.Exists(Path.Combine(_storage.GameDirectory(mcVersion), $"{mcVersion}.fabric.json")) ? $"{mcVersion}-vanilla" : mcVersion;
                    var task = _dlMgr.Enqueue(name, sub, icon, mirror, size, async (_, prog, ct) =>
                    {
                        await _mcApi.InstallVanillaOrFabricAsync(mcVersion, verJsonUrl, instFolderName, fabricLoader, mirror, prog, ct);
                        RefreshLocalWorkspace();
                        PushEvent?.Invoke("workspace_refreshed", BuildStateDto());
                    });
                    _hasUnseenDownloadBall = true;
                    return task;
                }

                if (catKey == "modpacks")
                    return await QueueModpackAsync(GetStr(payload, "projectId"), GetStr(payload, "buildId"), icon, mirror);

                string subfolder = ResourceFolder(catKey);
                string folderToken = GetStr(payload, "folderToken");
                bool usesCustomFolder = folderToken.Length > 0;
                InstanceMeta? target = null;
                string destFolder;
                if (usesCustomFolder)
                {
                    if (_resourceInstallFolder is not { } folder || folder.Token != folderToken || !Directory.Exists(folder.Path))
                        throw new InvalidOperationException("请重新选择安装文件夹。");
                    destFolder = folder.Path;
                }
                else
                {
                    target = _instances.FirstOrDefault(i => i.Id == GetStr(payload, "instanceId"))
                        ?? throw new InvalidOperationException("请选择已安装的目标版本");
                    destFolder = _storage.ResolveResourceDirectory(target.Id, subfolder);
                }

                var resource = await GetResourceAsync(catKey, GetStr(payload, "projectId"));
                var builds = DownloadableResourceFiles(resource.Files, catKey, GetStr(payload, "buildId"));
                var file = target == null
                    ? builds.FirstOrDefault(build => build.GameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase)) ?? builds.FirstOrDefault()
                    : builds.MaxBy(build => build.Match(target, catKey));
                if (file == null) throw new InvalidOperationException("这个构建不可下载，或作者限制了第三方下载。");
                string destPath = usesCustomFolder ? FindCustomDestination(destFolder, file.File) : Path.Combine(destFolder, file.File);
                if (!usesCustomFolder && !File.Exists(destPath) && File.Exists(destPath + ".disabled")) destPath += ".disabled";
                string source = resource.Project["source"]?.ToString() ?? "Modrinth";
                string destinationLabel = target?.Name ?? destFolder;
                var directTask = _dlMgr.Enqueue(name, $"{Path.GetFileName(destPath)} · {destinationLabel}", icon, source, ResourceCatalog.SizeLabel(file.Size), async (_, prog, ct) =>
                {
                    if (target != null && !_instances.Any(i => i.Id == target.Id)) throw new InvalidOperationException("目标实例已移除");
                    if (usesCustomFolder && !Directory.Exists(destFolder)) throw new DirectoryNotFoundException("指定的文件夹已移除。");
                    var speedSw = Stopwatch.StartNew();
                    long prevBytes = 0;
                    double prevSec = 0;
                    await _dlMgr.DownloadVerifiedFileAsync(DownloadRoutes.Files(file.FileUrl, mirror, file.Sha1.Length > 0),
                        destPath, file.Sha1, null, file.Size, (received, total) =>
                    {
                        double sec = speedSw.Elapsed.TotalSeconds;
                        double dt = Math.Max(0.15, sec - prevSec);
                        double bps = Math.Max(0, (received - prevBytes) / dt);
                        if (sec - prevSec >= 0.35)
                        {
                            prevBytes = received;
                            prevSec = sec;
                        }
                        string rate = bps >= 1048576.0 ? $"{bps / 1048576.0:F1} MB/s" : $"{bps / 1024.0:F0} KB/s";
                        if (total > 0) prog.Report(((int)(received * 100 / total.Value), $"{rate} · {(received / 1048576.0):F1}/{(total.Value / 1048576.0):F1} MB"));
                        else prog.Report((0, $"{rate} · {(received / 1048576.0):F1} MB"));
                    }, ct, overwrite: !usesCustomFolder);
                    if (target == null) return;
                    if (_cfg.ActiveInstanceId == target.Id)
                    {
                        RefreshActiveResources();
                    }
                    SyncAutoMemoryForInstance(target);
                    PushEvent?.Invoke("workspace_refreshed", BuildStateDto());
                }, uniqueKey: usesCustomFolder ? "custom-resource:" + destPath.ToUpperInvariant() : null);
                if (usesCustomFolder && _resourceInstallFolder?.Token == folderToken) _resourceInstallFolder = null;
                _hasUnseenDownloadBall = true;
                return directTask;
            }

            case "toggle_download_task":
                return _dlMgr.TogglePauseResume(GetStr(payload, "id"));

            case "delete_download_task":
                return new { ok = _dlMgr.DeleteTask(GetStr(payload, "id")) };

            case "dismiss_download_ball":
                _hasUnseenDownloadBall = false;
                return new { ok = true };

            case "open_folder":
            {
                string id = GetStr(payload, "id", _cfg.ActiveInstanceId);
                string subpath = GetStr(payload, "subpath");
                if (!_instances.Any(i => i.Id == id)) throw new InvalidOperationException("请先选择已安装的版本。");
                if (subpath is not ("" or "mods" or "resourcepacks" or "shaderpacks" or "screenshots" or "saves"))
                    throw new ArgumentException("未知实例目录");
                string target = _storage.ResolveResourceDirectory(id, subpath);
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                return new { ok = true, path = target };
            }

            case "launch_game":
            {
                var inst = _instances.FirstOrDefault(i => i.Id == _cfg.ActiveInstanceId);
                if (inst == null) return new { ok = false, message = "暂无可启动的游戏版本，请先在【下载资源 -> 游戏版本】下载安装至少一个版本。" };
                string preferredJava = inst.UseGlobalSettings ? _cfg.ActiveJavaId : inst.JavaId;
                var java = _javas.FirstOrDefault(j => j.MatchesId(preferredJava));
                if (inst.RequiredJavaMajor > 0)
                {
                    if (java == null || JavaManager.ParseMajor(java.Version) != inst.RequiredJavaMajor)
                        java = _javas.FirstOrDefault(j => JavaManager.ParseMajor(j.Version) == inst.RequiredJavaMajor);
                    if (java == null)
                        return new { ok = false, message = $"此整合包需要 Java {inst.RequiredJavaMajor}，请前往「实例环境」安装。" };
                }
                java ??= InstanceManager.PickJavaForMcVersion(_javas, inst.McVersion, null);
                if (java == null) return new { ok = false, message = "未检测到可用的 Java，请点击【实例环境 -> 安装 Java】。" };

                // Recompute live per-version AutoMemory right before launch when enabled
                bool useAutoMem = inst.UseGlobalSettings ? _cfg.AutoMemory : inst.AutoMemory;
                if (useAutoMem)
                {
                    SyncAutoMemoryForInstance(inst);
                }
                int memMb = useAutoMem
                    ? (inst.UseGlobalSettings ? _cfg.MemoryMb : inst.MemoryMb)
                    : (inst.UseGlobalSettings ? _cfg.MemoryMb : inst.MemoryMb);
                string jvmArgs = InstanceManager.ResolveEffectiveJvmArgs(inst, java, _cfg);
                var acc = _accMgr.GetActiveAccount();

                if (_pendingPreSpawnLaunches.ContainsKey(inst.Id))
                {
                    return new { ok = true, instanceId = inst.Id, message = $"正在准备启动 {inst.Name}..." };
                }

                var preSpawnCts = new CancellationTokenSource();
                _pendingPreSpawnLaunches[inst.Id] = preSpawnCts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(preSpawnCts.Token);
                        authTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var account = await _accMgr.EnsureFreshAsync(acc, MicrosoftClientId, authTimeout.Token);
                        var res = await _launcher.LaunchAsync(inst, java, account, memMb, jvmArgs,
                            pct =>
                            {
                                if (pct >= 100)
                                {
                                    RecordInstancePlayed(inst.Id);
                                    if (_pendingPreSpawnLaunches.TryRemove(inst.Id, out var doneCts)) doneCts.Dispose();
                                    PushEvent?.Invoke("game_process_started", new { instanceId = inst.Id });
                                }
                                PushEvent?.Invoke("launch_progress", new { instanceId = inst.Id, progress = pct });
                            }, CancellationToken.None, preSpawnCts.Token);
                        if (_pendingPreSpawnLaunches.TryRemove(inst.Id, out var remCts)) remCts.Dispose();
                        if (!res.Ok)
                        {
                            _storage.Log($"游戏启动失败 [{inst.Id}]：{res.Message}");
                            _latestCrashLines = res.CrashLines;
                            PushEvent?.Invoke("crash_detected", new { instanceId = inst.Id, message = res.Message, lines = res.CrashLines });
                        }
                        else
                        {
                            PushEvent?.Invoke("toast", res.Message);
                        }
                    }
                    catch (OperationCanceledException) when (preSpawnCts.IsCancellationRequested)
                    {
                        if (_pendingPreSpawnLaunches.TryRemove(inst.Id, out var cancelCts)) cancelCts.Dispose();
                        PushEvent?.Invoke("launch_cancelled", new { instanceId = inst.Id });
                    }
                    catch (Exception ex)
                    {
                        if (_pendingPreSpawnLaunches.TryRemove(inst.Id, out var errCts)) errCts.Dispose();
                        _storage.Log($"游戏启动失败 [{inst.Id}]：{ex}");
                        PushEvent?.Invoke("crash_detected", new { instanceId = inst.Id, message = ex.Message });
                    }
                });
                return new { ok = true, instanceId = inst.Id, message = $"正在启动 {inst.Name} ({memMb} MB)..." };
            }

            case "cancel_launch":
            {
                string id = GetStr(payload, "instanceId", _cfg.ActiveInstanceId);
                if (_pendingPreSpawnLaunches.TryRemove(id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    PushEvent?.Invoke("launch_cancelled", new { instanceId = id });
                    return new { cancelled = true, instanceId = id };
                }
                return new { cancelled = false, instanceId = id };
            }

            case "open_launcher_logs":
                Process.Start(new ProcessStartInfo(_storage.LogsDir) { UseShellExecute = true });
                return new { ok = true, path = _storage.LogsDir };

            case "log_ui_error":
            {
                string message = GetStr(payload, "message");
                _storage.Log($"界面脚本异常：{message[..Math.Min(message.Length, 2000)]}");
                return null;
            }

            case "export_crash_report":
            {
                string reportPath = Path.Combine(_storage.LogsDir, $"crash-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                await File.WriteAllLinesAsync(reportPath, _latestCrashLines.Count > 0 ? _latestCrashLines : _launcher.AnalyzeCrashLogs(), Encoding.UTF8);
                return new { ok = true, path = reportPath };
            }

            default:
                return new { ok = true, cmd };
        }
    }

    private static object PublicAccount(AccountEntry account) => new
    {
        loggedIn = true, account.Id, account.Username, account.Uuid, account.Type, account.Avatar
    };

    private async Task<DownloadTaskEntry> QueueModpackAsync(string reference, string buildId, string icon, string mirror)
    {
        if (buildId.Length == 0) throw new ArgumentException("请选择要安装的整合包构建。");

        var (project, files) = await GetResourceAsync("modpacks", reference);
        string source = project["source"]?.ToString() ?? "Modrinth";
        string extension = source == "CurseForge" ? ".zip" : ".mrpack";
        var build = files.FirstOrDefault(file => file.Id == buildId &&
            file.File.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(file.FileUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            ?? throw new InvalidOperationException("此构建没有可用的客户端整合包下载。");
        string id = ModpackInstaller.InstanceId(reference, buildId);
        if (Directory.Exists(_storage.GameDirectory(id)))
            throw new InvalidOperationException("该整合包构建已安装，可在版本实例列表中找到。");

        string name = project.GetValueOrDefault("cnName")?.ToString() ?? "";
        if (name.Length == 0) name = project["enName"]?.ToString() ?? "";
        string key = CurseForgeKey;
        string cfRoute = CurseForgeApiRoute;
        _hasUnseenDownloadBall = true;
        return _dlMgr.Enqueue(name, build.Title, icon, source, ResourceCatalog.SizeLabel(build.Size), async (_, progress, ct) =>
        {
            await _packInstaller.InstallAsync(id, source, name, build, mirror, _javas, key, progress, ct, cfRoute);
            RefreshLocalWorkspace();
            PushEvent?.Invoke("workspace_refreshed", BuildStateDto());
        }, uniqueKey: id);
    }

    private static string ResourceFolder(string category) => category switch
    {
        "mods" => "mods", "packs" => "resourcepacks", "shaders" => "shaderpacks",
        _ => throw new InvalidOperationException("请选择模组、资源包或光影包。")
    };

    internal object SelectResourceInstallFolder(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string token = Guid.NewGuid().ToString("N");
        _resourceInstallFolder = (token, fullPath);
        return new { token, path = fullPath };
    }

    internal object SelectJavaInstallFolder(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string token = Guid.NewGuid().ToString("N");
        _javaInstallFolder = (token, fullPath);
        return new { token, path = fullPath };
    }

    internal async Task<object> ImportInstanceFolderAsync(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return new { cancelled = true };

        string sourceRoot = Path.GetFullPath(selectedPath.TrimEnd('\\', '/'));
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException("所选文件夹不存在。");

        string versionsRoot = Path.GetFullPath(_storage.VersionsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选文件夹已在启动器版本库中，无需重复导入。");

        return await Task.Run<object>(() =>
        {
            var candidates = new List<(string RawId, string Dir)>();
            string? mcRoot = null;

            string? directJson = InstanceManager.ResolveVersionJsonPath(sourceRoot, Path.GetFileName(sourceRoot));
            if (directJson != null)
            {
                candidates.Add((Path.GetFileName(sourceRoot), sourceRoot));
                string? parentDir = Path.GetDirectoryName(sourceRoot);
                if (parentDir != null && Path.GetFileName(parentDir).Equals("versions", StringComparison.OrdinalIgnoreCase))
                    mcRoot = Path.GetDirectoryName(parentDir);
            }
            else
            {
                string subVersions = Path.Combine(sourceRoot, "versions");
                string searchRoot = Directory.Exists(subVersions) ? subVersions : sourceRoot;
                if (Directory.Exists(subVersions)) mcRoot = sourceRoot;
                foreach (var dir in Directory.GetDirectories(searchRoot))
                {
                    if (InstanceManager.ResolveVersionJsonPath(dir, Path.GetFileName(dir)) != null)
                        candidates.Add((Path.GetFileName(dir), dir));
                }
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException("未识别到有效的 Minecraft 版本文件夹（需包含 <版本名>.json 或 versions/<版本名>/<版本名>.json）。");

            var importedIds = new List<string>();
            lock (_workspaceRefreshLock)
            {
                var metaMap = _storage.LoadInstanceMetaMap();
                var existingPaths = new HashSet<string>(
                    metaMap.Values
                        .Where(m => !string.IsNullOrWhiteSpace(m.ExternalPath))
                        .Select(m => Path.GetFullPath(m.ExternalPath!).TrimEnd('\\', '/')),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var (rawId, srcDir) in candidates)
                {
                    string normalizedSrc = Path.GetFullPath(srcDir).TrimEnd('\\', '/');
                    if (existingPaths.Contains(normalizedSrc)) continue;

                    string baseId = SanitizeInstanceId(rawId);
                    string targetId = baseId;
                    int suffix = 2;
                    while (metaMap.ContainsKey(targetId) || Directory.Exists(Path.Combine(_storage.VersionsDir, targetId)))
                    {
                        targetId = $"{baseId}-ext{(suffix == 2 ? "" : $"-{suffix}")}";
                        suffix++;
                    }

                    _storage.RegisterExternalInstancePath(targetId, normalizedSrc, mcRoot);
                    var resolvedJson = InstanceManager.ResolveVersionJsonPath(normalizedSrc, targetId);
                    var (detMcVer, _, _) = resolvedJson != null
                        ? InstanceManager.DetectVersionMetadata(resolvedJson, Path.GetFileNameWithoutExtension(resolvedJson))
                        : ("", "Vanilla", "");
                    var matchedJava = InstanceManager.PickJavaForMcVersion(_javas, detMcVer, null);
                    string optJvm = InstanceManager.ComputeRecommendedJvmArgs(detMcVer, matchedJava);
                    bool compatWithGlobal = InstanceManager.IsJvmArgsCompatibleWithJava(_cfg.CustomJvmArgs, matchedJava, detMcVer);
                    metaMap[targetId] = new InstanceMeta
                    {
                        Id = targetId,
                        Name = rawId,
                        ExternalPath = normalizedSrc,
                        ExternalMinecraftDir = mcRoot,
                        JavaId = matchedJava?.Id ?? "",
                        JvmArgs = optJvm,
                        Isolated = true,
                        UseGlobalSettings = compatWithGlobal,
                        AutoMemory = true
                    };
                    existingPaths.Add(normalizedSrc);
                    importedIds.Add(targetId);
                }

                if (importedIds.Count == 0)
                    throw new InvalidOperationException("所选版本文件夹已全部识别并导入，无需重复添加。");

                _storage.SaveInstanceMetaMap(metaMap);
            }

            RefreshLocalWorkspace();
            string primaryName = importedIds
                .Select(id => _instances.FirstOrDefault(i => i.Id == id)?.Name ?? id)
                .FirstOrDefault() ?? "";
            _storage.Log($"识别并关联外部版本文件夹（零拷贝）：{sourceRoot} -> {string.Join(", ", importedIds)}");
            PushEvent?.Invoke("workspace_refreshed", BuildStateDto());
            return new
            {
                importedIds,
                count = importedIds.Count,
                firstId = importedIds[0],
                primaryName
            };
        });
    }

    private static string SanitizeInstanceId(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim(' ', '.');
        return InstanceManager.IsFileName(cleaned) ? cleaned : "Imported-Minecraft";
    }

    private static string FindCustomDestination(string folder, string filename)
    {
        string path = Path.Combine(folder, filename);
        string name = Path.GetFileNameWithoutExtension(filename);
        string extension = Path.GetExtension(filename);
        for (int copy = 2; File.Exists(path) || Directory.Exists(path); copy++)
            path = Path.Combine(folder, $"{name} ({copy}){extension}");
        return path;
    }

    private static ResourceFile[] DownloadableResourceFiles(List<ResourceFile> files, string category, string buildId)
    {
        string extension = category == "mods" ? ".jar" : ".zip";
        return files.Where(file =>
            (buildId.Length == 0 || file.Id == buildId) &&
            InstanceManager.IsFileName(file.File) && file.File.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(file.FileUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            .OrderBy(file => file.ReleaseType == "Release" ? 0 : 1)
            .ThenByDescending(file => file.Published).ToArray();
    }

    private async Task<(List<Dictionary<string, object?>> Items, int TotalHits)> SearchCombinedCatalogAsync(
        string category, string query, string loader, int offset, int pageSize, string sortBy, string mcVersion, string resourceTag, string resourceSubTag, CancellationToken ct)
    {
        const int cfHardCap = 10000;
        // Beyond CurseForge's 10,000 API pagination cap, seamlessly continue paging Modrinth
        if (offset >= cfHardCap * 2)
        {
            int mrOnlyOffset = offset - cfHardCap;
            var (mrOnlyItems, mrOnlyTotal) = await SafeSearchAsync(
                () => _mcApi.SearchModrinthPagedAsync(ResourceCatalog.ProjectType(category), query, loader, mrOnlyOffset, pageSize, ct, sortBy, mcVersion, resourceTag, resourceSubTag));
            return (mrOnlyItems.Take(pageSize).ToList(), mrOnlyTotal + cfHardCap);
        }

        int mrOffset = (offset + 1) / 2;
        int cfOffset = offset / 2;
        int mrBase = Math.Max(1, (offset + pageSize + 1) / 2 - mrOffset);
        int cfBase = Math.Max(1, (offset + pageSize) / 2 - cfOffset);
        int mrLimit = Math.Min(50, mrBase + 5);
        int cfLimit = Math.Min(50, cfBase + 5);

        Task<(List<Dictionary<string, object?>> Items, int TotalHits)> mrTask = SafeSearchAsync(
            () => _mcApi.SearchModrinthPagedAsync(ResourceCatalog.ProjectType(category), query, loader, mrOffset, mrLimit, ct, sortBy, mcVersion, resourceTag, resourceSubTag));
        Task<(List<Dictionary<string, object?>> Items, int TotalHits)> cfTask = cfOffset + cfLimit <= cfHardCap
            ? SafeSearchAsync(() => _curseForge.SearchAsync(category, query, loader, cfOffset, cfLimit, CurseForgeKey, ct, sortBy, mcVersion, CurseForgeApiRoute, resourceTag, resourceSubTag))
            : Task.FromResult((new List<Dictionary<string, object?>>(), cfHardCap));

        await Task.WhenAll(mrTask, cfTask);
        var (mrItems, mrTotal) = mrTask.Result;
        var (cfItems, cfTotal) = cfTask.Result;
        int effectiveCfTotal = Math.Min(cfHardCap, cfTotal);
        int combinedTotal = mrTotal + effectiveCfTotal;

        if (mrItems.Count == 0 && cfItems.Count == 0) return ([], 0);
        if (cfItems.Count == 0)
        {
            // When CF ran out earlier than offset, pull full pageSize from Modrinth at exact remainder offset
            if (mrItems.Count < pageSize && offset > effectiveCfTotal)
            {
                int fallbackMrOffset = Math.Max(0, offset - effectiveCfTotal);
                var (extraMr, extraTotal) = await SafeSearchAsync(
                    () => _mcApi.SearchModrinthPagedAsync(ResourceCatalog.ProjectType(category), query, loader, fallbackMrOffset, pageSize, ct, sortBy, mcVersion, resourceTag, resourceSubTag));
                if (extraMr.Count > 0) return (extraMr.Take(pageSize).ToList(), extraTotal + effectiveCfTotal);
            }
            return (mrItems.Take(pageSize).ToList(), Math.Max(mrTotal, combinedTotal));
        }
        if (mrItems.Count == 0) return (cfItems.Take(pageSize).ToList(), Math.Max(effectiveCfTotal, combinedTotal));

        var merged = new List<Dictionary<string, object?>>(mrItems.Count + cfItems.Count);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static string MergeKey(Dictionary<string, object?> item)
        {
            string raw = (item.GetValueOrDefault("slug")?.ToString() ?? item.GetValueOrDefault("enName")?.ToString() ?? "").Trim();
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.Length > 0 ? sb.ToString() : (item.GetValueOrDefault("id")?.ToString() ?? "");
        }

        static double ParseDownloadsScore(Dictionary<string, object?> item)
        {
            string text = (item.GetValueOrDefault("downloads")?.ToString() ?? "").Trim().Replace("次", "").Trim().ToUpperInvariant();
            if (text.Length == 0) return 0;
            double mult = 1;
            if (text.EndsWith('亿')) { mult = 100_000_000; text = text[..^1].Trim(); }
            else if (text.EndsWith('万')) { mult = 10_000; text = text[..^1].Trim(); }
            else if (text.EndsWith('B')) { mult = 1_000_000_000; text = text[..^1].Trim(); }
            else if (text.EndsWith('M')) { mult = 1_000_000; text = text[..^1].Trim(); }
            else if (text.EndsWith('K')) { mult = 1_000; text = text[..^1].Trim(); }
            return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v * mult : 0;
        }

        void AddMerged(Dictionary<string, object?> item)
        {
            string k = MergeKey(item);
            if (seen.TryGetValue(k, out int existingIdx))
            {
                var existing = merged[existingIdx];
                existing["source"] = "CurseForge/Modrinth";
                if (ParseDownloadsScore(item) > ParseDownloadsScore(existing))
                    existing["downloads"] = item.GetValueOrDefault("downloads");
                return;
            }
            seen[k] = merged.Count;
            merged.Add(item);
        }

        int max = Math.Max(mrItems.Count, cfItems.Count);
        for (int i = 0; i < max; i++)
        {
            if (i < mrItems.Count) AddMerged(mrItems[i]);
            if (i < cfItems.Count) AddMerged(cfItems[i]);
        }

        if (!sortBy.Equals("newest", StringComparison.OrdinalIgnoreCase))
        {
            merged.Sort((a, b) => ParseDownloadsScore(b).CompareTo(ParseDownloadsScore(a)));
        }

        return (merged.Take(pageSize).ToList(), combinedTotal);
    }

    private static async Task<(List<Dictionary<string, object?>> Items, int TotalHits)> SafeSearchAsync(
        Func<Task<(List<Dictionary<string, object?>> Items, int TotalHits)>> action)
    {
        try { return await action(); }
        catch { return ([], 0); }
    }

    private async Task<(Dictionary<string, object?> Project, List<ResourceFile> Files)> GetResourceAsync(string category, string reference)
    {
        if (reference.Length < 4 || !(reference.StartsWith("mr-", StringComparison.Ordinal) || reference.StartsWith("cf-", StringComparison.Ordinal)))
            throw new ArgumentException("请选择在线资源目录中的项目。");
        string key = $"{category}:{reference}";
        if (_resourceCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow < cached.Expires)
            return (cached.Project, cached.Files);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (project, files) = reference.StartsWith("cf-", StringComparison.Ordinal)
            ? await _curseForge.GetResourceAsync(category, reference[3..], CurseForgeKey, timeout.Token, CurseForgeApiRoute)
            : await _mcApi.GetModrinthResourceAsync(category, reference[3..], timeout.Token);
        if (_resourceCache.Count >= 24)
        {
            foreach (var existing in _resourceCache.Keys.Take(1)) _resourceCache.TryRemove(existing, out _);
        }
        _resourceCache[key] = (DateTimeOffset.UtcNow.AddMinutes(3), project, files);
        return (project, files);
    }

    private void RefreshActiveResources()
    {
        string id = _cfg.ActiveInstanceId;
        _mods = id.Length > 0 ? _instMgr.ScanMods(id) : [];
        _packs = id.Length > 0 ? _instMgr.ScanPacks(id) : [];
    }

    private void RecordInstancePlayed(string id)
    {
        lock (_workspaceRefreshLock)
        {
            var instance = _instances.FirstOrDefault(item => item.Id == id);
            if (instance == null) return;
            var now = DateTimeOffset.Now;
            instance.LastPlayedAt = now;
            instance.LastPlayed = now.ToString("yyyy-MM-dd HH:mm");
            SaveSingleInstance(instance);
        }
    }

    private void SaveSingleInstance(InstanceMeta inst)
    {
        lock (_workspaceRefreshLock)
        {
            var map = _storage.LoadInstanceMetaMap();
            map[inst.Id] = inst;
            _storage.SaveInstanceMetaMap(map);
        }
    }

    private static string GetStr(JsonElement el, string prop, string def = "") =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? def)
            : def;

    private static bool GetBool(JsonElement el, string prop, bool def = false) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : def;

    private static int GetInt(JsonElement el, string prop, int def = 0) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : def;
}
