<p align="center">
  <img src="ui-prototype/assets/app-icon.png" alt="DummyCrisp Minecraft Launcher Logo" width="128" height="128" />
</p>

<h1 align="center">DummyCrisp Minecraft Launcher (DCL)</h1>

<p align="center">
  <strong>A modern Windows launcher for Minecraft: Java Edition</strong><br/>
  <em>Status: Work in Progress — Early Development Preview (Snapshot1)</em><br/>
  <a href="https://duckcrispp.github.io/DummyCrisp-Minecraft-Launcher/">Project Homepage</a> ·
  <a href="https://github.com/duckcrispp/DummyCrisp-Minecraft-Launcher">GitHub Repository</a> ·
  <a href="CHANGELOG.md">Update Reports (CHANGELOG)</a>
</p>

---

> **Development Notice (WIP)**: DummyCrisp Minecraft Launcher is currently under active early development. Features, UI layouts, and APIs are subject to change prior to the 1.0 release.

> **Disclaimer**: NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

## 当前状态说明 / Known Status & Notices

1. 报错分析和启动优化还在陆续完善中。
2. 从 CurseForge 等站点下载资源时，部分网络环境下仍可能卡住，目前还在持续改进。
3. 部分筛选条件下，资源列表的页数可能显示不正常。
4. 正版登录仍在等待微软的 API 许可，目前暂不可用；**许可审批通过后会正式发布 1.0 Release**。
5. 本程序默认预制了一些主题与背景，但尚不支持从本地导入图片的功能，自定义主题和背景会在后续版本推出。

> 完整历史变更记录见 [CHANGELOG.md](CHANGELOG.md)。

---

## 更新报告 / Latest Update Report (2026-11-23 21:56 UTC+8)

### 本次更新与修复 (Fixed & Added)
- **纯净主页**：在主页滑到顶后继续上滑（出现放大镜图标后再滑一次）可切换至纯净主页，顶栏与列表向边缘退场，右侧展示当前账号的 3D 皮肤和用户名。支持拖动旋转，进入时会自动挥手打招呼，未运行游戏时每隔 10–60 秒还会随机做几个基岩版风格的小动作；向下滚动即可回到正常主页。
- **离线账号换肤**：账号管理的离线账号旁新增换肤按钮，支持导入本地 PNG 皮肤，头像同步改为方形完整显示，启动游戏时会通过本地回环服务在离线状态下加载自定义皮肤。
- **优化启动**：
  - 恢复框架依赖单文件发布并剔除未使用的依赖、压缩内置图标，主程序体积从约 160 MB 降回约 4 MB。
  - 启动改为两段加载：先读取本地 Java 和实例列表直接进主页，几百个 Mod JAR 解析与崩溃日志扫描挪到后台完成；移除了阻塞首屏的外部字体请求和重复启屏文字，冷启动更快、不再闪跳。
- **内存分配与 JVM 预设**：按 MC 和 Java 版本自动匹配默认 GC（Java 21+ 用 Gen-ZGC，Java 17–20 用 ZGC，Java 8–16 用 G1GC），调整了多 Mod 与整合包的内存策略。
- **界面与交互细节**：
  - 修复版本颜色选择器选中圆角与相邻色块间留缝的问题。
  - 最大化状态下拖动顶栏或抽屉顶栏可自动还原窗口并继续拖动；统一抽屉页顶栏高度与主页对齐，整个顶栏区域均可拖动。
  - 远古版、愚人节版及无加载器版本统一先进入详情页，并为所有版本详情页补上中文 Minecraft Wiki 直达入口；去掉了多余的 `default` 加载器栏和重复的「原版」字样。
  - 统一资源安装弹窗内各版本条块高度，版本不匹配项标红且不再换行；修复 Mod 管理禁用项悬停卡顿并新增「仅查看禁用」筛选；修复悬浮下载球打开后红点未清除的问题。
  - 调整了部分按钮文案与图标（「下载资源」改为「资源下载」、「启动游戏」改为「START」、模组图标改为拼图），点击顶栏或抽屉的 DCL 标志新增小彩蛋。

### 尚存在的问题 (Known Issues)
- 报错分析和启动优化还在持续完善。
- CurseForge 等站点的资源下载偶尔仍可能卡住。
- 部分筛选条件下，资源列表页数可能显示不正常。
- 正版登录仍在等待微软官方许可，获批后会发布 1.0 Release。
- 本程序默认预制了一些主题与背景，但尚不支持从本地导入图片的功能，自定义主题和背景将在后续版本推出。

---

## Overview

**DummyCrisp Minecraft Launcher (DCL)** is a lightweight Windows desktop launcher for *Minecraft: Java Edition* built with **.NET 8 (WinForms + Microsoft Edge WebView2)** and a custom HTML/CSS/JS interface.

### Key Capabilities
- **Version & Instance Management**: Version isolation under `.minecraft/versions/<id>/`, external `.minecraft` / version folder zero-copy recognition & import, multi-loader support (Vanilla, Fabric, Forge, NeoForge, Quilt, OptiFine), Java runtime discovery/installation, modpack official icon & inline instance renaming, and per-instance JVM GC presets (`Gen-ZGC`, `G1GC`, `Shenandoah`) with cancelable pre-spawn game launch.
- **Resource Browser & Downloader**: Unified search (`全部` / Modrinth / CurseForge) with clustered category, loader, dual-column MC version (`全部版本` + `热门版本`), resource pack resolution, and shader performance filters; automatic MCIM / BMCLAPI mirror routing; embedded 33k+ Chinese/Pinyin project name index (`DCL_Backend/Data/mcmod-zh-cn.tsv.gz`); and multi-segment ranged downloading with SHA-1/SHA-512 verification.
- **Smart Runtime Diagnostics**: Automatic per-version Java version matching, adaptive `-Xmx` RAM allocation based on system memory, loader overhead, mod/shader count, and modpack heuristics, plus crash log analysis (in progress).
- **Customizable UI**: 9 built-in color themes (defaulting to **Obsidian Black × Signal Yellow**, with no background pattern by default), launcher-themed custom HSV/HEX/RGB folder color picker, always-on-top window pin toggle, responsive frameless windowing, and fixed-height hero typography.

## Authentication, Security & Privacy

DummyCrisp Minecraft Launcher implements **Microsoft OAuth 2.0 Public Client Device Code Flow** for *Minecraft: Java Edition* authentication (`AccountManager.cs`) (pending Microsoft Services API approval):
1. Requests a user device code from Microsoft's `/consumers/oauth2/v2.0/devicecode` endpoint with scope `XboxLive.signin offline_access`.
2. Launches the user's system browser directly to Microsoft's official verification page (`microsoft.com` / `login.microsoftonline.com`) — **the launcher never collects, handles, or stores Microsoft account passwords**.
3. Stores issued session/refresh tokens **only locally** on the user's device, encrypted per-Windows-user via **Windows DPAPI** (`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` inside `DCL/config/accounts.dat`).
4. Does **not** embed any client secret; requires only the public Azure Application (client) ID approved for Minecraft Services API access.

## Repository Structure

```text
DummyCrisp/
├── DCL_Backend/          # .NET 8 desktop host, WebView2 bridge, OAuth2, downloader, game launcher
├── ui-prototype/         # Embedded WebView2 frontend (HTML/CSS/JS, themes, SVG patterns/icons, assets)
├── docs/                 # Project homepage (GitHub Pages), architecture notes, setup guides
├── CHANGELOG.md          # Timestamped release & update reports (fixed bugs & known issues)
└── README.md             # Project introduction & latest update report
```

## Build from Source (Windows)

Requirements:
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows Desktop)
- Microsoft Edge WebView2 Runtime (pre-installed on Windows 10/11)

```powershell
dotnet publish DCL_Backend/DCL_snapshpt1.csproj -c Release -r win-x64 -o dist
```

Or run `--self-test` against the built executable:

```powershell
.\dist\DCL_snapshpt1.exe --self-test
```

See [docs/launcher-setup.md](docs/launcher-setup.md) and [docs/architecture-and-roadmap.md](docs/architecture-and-roadmap.md) for setup and roadmap details.

## License

Licensed under the [MIT License](LICENSE).
