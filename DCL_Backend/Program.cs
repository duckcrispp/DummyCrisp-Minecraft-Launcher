using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DCL_Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--self-test")
        {
            string testRoot = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "DCL_snapshpt1_selftest");
            int rc = RunSelfTest(testRoot);
            Environment.Exit(rc);
            return;
        }
        var storage = new StorageService();
        storage.Log($"启动器启动 · {Assembly.GetExecutingAssembly().GetName().Version} · PID {Environment.ProcessId} · {RuntimeInformation.OSDescription}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => storage.Log($"界面线程异常：{e.Exception}");
        Application.ApplicationExit += (_, _) => storage.Log("启动器退出");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => storage.Log($"未处理异常：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            storage.Log($"后台任务异常：{e.Exception}");
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(storage));
    }

    private static int RunSelfTest(string parent)
    {
        string testRoot = Path.Combine(Path.GetFullPath(parent), "dcl-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(testRoot);
            var bridge = new HostBridge(storage, startOnlineServices: false);

            // 1. Verify Embedded UI Resources inside single EXE
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            if (!names.Any(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)) ||
                !names.Any(n => n.EndsWith("app.js", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine("FAIL: Missing embedded UI assets inside single EXE.");
                return 1;
            }

            // 2. Verify Hierarchical PCL-style & DCL directories generated
            string[] reqDirs = {
                storage.DclDir, storage.ConfigDir, storage.CacheDir, storage.LogsDir, storage.RuntimeJavaDir,
                storage.MinecraftDir, storage.VersionsDir, storage.LibrariesDir, storage.AssetsDir,
                storage.ModsDir, storage.ResourcePacksDir, storage.ShaderPacksDir, storage.SavesDir
            };
            foreach (var d in reqDirs)
            {
                if (!Directory.Exists(d))
                {
                    Console.Error.WriteLine($"FAIL: Missing generated directory: {d}");
                    return 2;
                }
            }

            // 3. Verify System RAM + Smart Auto Memory Heuristic + DPAPI Account persistence + Config persistence
            var (tot, avail) = SystemMemoryHelper.GetSystemMemoryMb();
            int vanillaMem = SystemMemoryHelper.ComputeRecommendedMemoryMb(tot, avail, "1.21.4", "Vanilla", 0, false);
            int packMem = SystemMemoryHelper.ComputeRecommendedMemoryMb(tot, avail, "1.21.1", "NeoForge", 220, true);
            if (packMem < vanillaMem)
            {
                Console.Error.WriteLine($"FAIL: Auto RAM did not scale for modpack ({packMem} < {vanillaMem})");
                return 3;
            }

            var addPayload = JsonDocument.Parse("{\"username\":\"SmokeTester\"}").RootElement;
            bridge.HandleCommandAsync("add_account", addPayload).GetAwaiter().GetResult();
            if (!File.Exists(storage.AccountsPath))
            {
                Console.Error.WriteLine("FAIL: DPAPI accounts.dat was not written.");
                return 4;
            }

            var memPayload = JsonDocument.Parse("{\"auto\":false,\"mb\":5120}").RootElement;
            bridge.HandleCommandAsync("set_memory", memPayload).GetAwaiter().GetResult();
            var reloaded = storage.LoadConfig();
            if (reloaded.AutoMemory || reloaded.MemoryMb != 5120)
            {
                Console.Error.WriteLine("FAIL: Config settings.json did not persist manual memory override.");
                return 5;
            }

            // 4. Verify Java detection + crash export
            var javas = new JavaManager(storage, new HttpClient()).ScanLocalJavas();
            var exportRes = bridge.HandleCommandAsync("export_crash_report", default).GetAwaiter().GetResult();
            Console.WriteLine($"SELF-TEST PASSED | EmbeddedRes={names.Length} | SysRAM={tot}MB (Free {avail}MB) | AutoRAM Vanilla={vanillaMem}MB Modpack={packMem}MB | LocalJavas={javas.Count} | CrashExportOK={exportRes != null}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Exception in self-test: {ex}");
            return 9;
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly StorageService _storage;
    private readonly HostBridge _bridge;
    private readonly Dictionary<string, string> _embeddedIndex;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    public MainForm(StorageService storage)
    {
        Text = "DCL_snapshpt1";
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        Padding = new Padding(0);
        Width = 1240;
        Height = 790;
        MinimumSize = new Size(360, 280);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 10, 12);
        try
        {
            using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DCL_Launcher.app.ico");
            if (iconStream != null) Icon = new Icon(iconStream);
        }
        catch { }

        _storage = storage;
        _bridge = new HostBridge(_storage);
        TopMost = _bridge.AlwaysOnTop;
        _embeddedIndex = BuildEmbeddedResourceLookup();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(10, 10, 12)
        };
        Controls.Add(_webView);

        Load += OnFormLoadAsync;
        SizeChanged += (_, _) => UpdateResizePadding();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00C00000 | 0x00040000 | 0x00020000 | 0x00010000 | 0x00080000;
            return cp;
        }
    }

    private void UpdateResizePadding()
    {
        Padding = new Padding(0);
        try
        {
            var stateMsg = JsonSerializer.Serialize(new { @event = "window_state", data = new { maximized = WindowState == FormWindowState.Maximized } }, StorageService.JsonOpts);
            _webView.CoreWebView2?.PostWebMessageAsJson(stateMsg);
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int roundPref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref roundPref, sizeof(int));
            int darkMode = 1;
            DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var nc = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
            if (IsZoomed(Handle))
            {
                var mon = MonitorFromWindow(Handle, 2);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(mon, ref mi))
                {
                    nc.rgrc0 = mi.rcWork;
                    Marshal.StructureToPtr(nc, m.LParam, false);
                }
            }
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            var pt = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            const int edge = 7;
            bool left = pt.X <= edge;
            bool right = pt.X >= ClientSize.Width - edge;
            bool top = pt.Y <= edge;
            bool bottom = pt.Y >= ClientSize.Height - edge;
            if (top && left) { m.Result = (IntPtr)13; return; }
            if (top && right) { m.Result = (IntPtr)14; return; }
            if (bottom && left) { m.Result = (IntPtr)16; return; }
            if (bottom && right) { m.Result = (IntPtr)17; return; }
            if (left) { m.Result = (IntPtr)10; return; }
            if (right) { m.Result = (IntPtr)11; return; }
            if (top) { m.Result = (IntPtr)12; return; }
            if (bottom) { m.Result = (IntPtr)15; return; }
        }
        base.WndProc(ref m);
    }

    private async void OnFormLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            string wvUserData = Path.Combine(_storage.CacheDir, "webview2");
            Directory.CreateDirectory(wvUserData);
            var env = await CoreWebView2Environment.CreateAsync(null, wvUserData);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.AddWebResourceRequestedFilter("https://dcl.local/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += (_, args) => _storage.Log($"WebView2 进程异常：{args.ProcessFailedKind} / {args.Reason} / 退出码 {args.ExitCode}");
            _storage.Log($"WebView2 已就绪 · {core.Environment.BrowserVersionString}");
            core.NavigationStarting += (_, args) => args.Cancel = !IsLauncherPage(args.Uri);
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!args.IsUserInitiated || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps) return;
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { _storage.Log($"Unable to open external website: {ex.Message}"); }
            };

            _bridge.PushEvent += (evtName, data) =>
            {
                if (IsDisposed || Disposing) return;
                BeginInvoke(() =>
                {
                    try
                    {
                        var msg = JsonSerializer.Serialize(new { @event = evtName, data }, StorageService.JsonOpts);
                        _webView.CoreWebView2?.PostWebMessageAsJson(msg);
                    }
                    catch { }
                });
            };

            core.Navigate("https://dcl.local/index.html");
        }
        catch (Exception ex)
        {
            _storage.Log($"WebView2 Init Error: {ex}");
            MessageBox.Show($"WebView2 初始化失败：{ex.Message}", "DCL_snapshpt1", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Dictionary<string, string> BuildEmbeddedResourceLookup()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.StartsWith("ui\\", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ui/", StringComparison.OrdinalIgnoreCase))
            {
                string norm = name[3..].Replace('\\', '/');
                map[norm] = name;
            }
        }
        return map;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            string rel = uri.AbsolutePath.TrimStart('/');
            if (string.IsNullOrEmpty(rel)) rel = "index.html";

            Stream? stream;
            if (_embeddedIndex.TryGetValue(rel, out var resName))
                stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName);
            else if (rel.StartsWith(StorageService.ModIconRoute, StringComparison.Ordinal))
                stream = _storage.OpenModIcon(rel[StorageService.ModIconRoute.Length..]);
            else
                return;

            if (stream != null)
            {
                string mime = GetMimeType(rel);
                string cacheControl = rel.StartsWith(StorageService.ModIconRoute, StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable" : "no-cache";
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    stream, 200, "OK", $"Content-Type: {mime}\r\nCache-Control: {cacheControl}");
            }
        }
        catch (Exception ex)
        {
            _storage.Log($"WebResourceRequested error: {ex.Message}");
        }
    }

    private static bool IsLauncherPage(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "dcl.local" && uri.IsDefaultPort;

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsLauncherPage(e.Source)) return;
        string reqId = "";
        string cmd = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            reqId = root.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
            cmd = root.TryGetProperty("cmd", out var cmdEl) ? (cmdEl.GetString() ?? "") : "";
            JsonElement payload = root.TryGetProperty("payload", out var pEl) ? pEl.Clone() : default;

            var result = cmd switch
            {
                "browse_resource_install_folder" => InvokeUi(() =>
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "选择资源的安装文件夹",
                        UseDescriptionForTitle = true,
                        InitialDirectory = _storage.VersionsDir
                    };
                    return dialog.ShowDialog(this) == DialogResult.OK
                        ? _bridge.SelectResourceInstallFolder(dialog.SelectedPath)
                        : new { cancelled = true };
                }),
                "browse_java_install_folder" => InvokeUi(() =>
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "选择 Java 安装目录",
                        UseDescriptionForTitle = true,
                        InitialDirectory = _storage.RuntimeJavaDir
                    };
                    return dialog.ShowDialog(this) == DialogResult.OK
                        ? _bridge.SelectJavaInstallFolder(dialog.SelectedPath)
                        : new { cancelled = true };
                }),
                "import_instance_folder" => await ImportInstanceFolderFromUiAsync(),
                "window_minimize" => InvokeUi(() => { SendMessage(Handle, 0x0112, 0xF020, 0); return new { ok = true }; }),
                "window_toggle_maximize" => InvokeUi(() =>
                {
                    bool nextMax = WindowState != FormWindowState.Maximized;
                    SendMessage(Handle, 0x0112, nextMax ? 0xF030 : 0xF120, 0);
                    return new { maximized = WindowState == FormWindowState.Maximized };
                }),
                "window_close" => InvokeUi(() => { SendMessage(Handle, 0x0112, 0xF060, 0); return new { ok = true }; }),
                "window_set_topmost" => InvokeUi(() =>
                {
                    bool pinned = payload.ValueKind == JsonValueKind.Object &&
                                  payload.TryGetProperty("pinned", out var pinEl) &&
                                  pinEl.ValueKind == JsonValueKind.True;
                    TopMost = _bridge.SetAlwaysOnTop(pinned);
                    return new { alwaysOnTop = TopMost };
                }),
                "window_start_drag" => InvokeUi(() =>
                {
                    if (WindowState == FormWindowState.Normal)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, 0x00A1, 0x0002, 0);
                    }
                    return new { ok = true };
                }),
                "window_start_resize" => InvokeUi(() =>
                {
                    if (WindowState == FormWindowState.Normal)
                    {
                        string edge = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("edge", out var edgeEl)
                            ? (edgeEl.GetString() ?? "")
                            : "";
                        int ht = edge switch
                        {
                            "left" => 10,
                            "right" => 11,
                            "top" => 12,
                            "topleft" => 13,
                            "topright" => 14,
                            "bottom" => 15,
                            "bottomleft" => 16,
                            "bottomright" => 17,
                            _ => 0
                        };
                        if (ht != 0)
                        {
                            ReleaseCapture();
                            SendMessage(Handle, 0x00A1, ht, 0);
                        }
                    }
                    return new { ok = true };
                }),
                _ => await _bridge.HandleCommandAsync(cmd, payload)
            };
            if (reqId.Length == 0 || IsDisposed) return;
            string reply = JsonSerializer.Serialize(new { id = reqId, ok = true, result }, StorageService.JsonOpts);
            BeginInvoke(() => _webView.CoreWebView2?.PostWebMessageAsJson(reply));
        }
        catch (Exception ex)
        {
            _storage.Log($"启动器操作失败 [{cmd}/{reqId}]：{ex}");
            if (reqId.Length == 0 || IsDisposed) return;
            string errReply = JsonSerializer.Serialize(new { id = reqId, ok = false, error = ex.Message }, StorageService.JsonOpts);
            BeginInvoke(() => _webView.CoreWebView2?.PostWebMessageAsJson(errReply));
        }
    }

    private object InvokeUi(Func<object> fn)
    {
        if (InvokeRequired) return Invoke(fn);
        return fn();
    }

    private async Task<object> ImportInstanceFolderFromUiAsync()
    {
        string? selected = InvokeUi(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择外部 Minecraft 版本文件夹或 .minecraft 目录",
                UseDescriptionForTitle = true,
                InitialDirectory = _storage.VersionsDir
            };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : "";
        }) as string;
        if (string.IsNullOrWhiteSpace(selected))
            return new { cancelled = true };
        return await _bridge.ImportInstanceFolderAsync(selected);
    }

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };
}
