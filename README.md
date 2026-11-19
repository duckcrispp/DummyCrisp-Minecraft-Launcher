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

1. **报错分析与启动优化功能尚在完善中**：崩溃日志规则诊断与启动性能调优仍在持续迭代。
2. **从 CurseForge 等网站下载资源可能出现卡住情况**：部分第三方源或受限依赖下载在特定网络环境下仍可能停顿，目前正在持续改进。
3. **资源列表页数可能没有正常显示**：部分搜索/筛选与双源聚合分页场景下，页数显示仍有待进一步修正。
4. **目前尚在等待微软的许可，暂时无法正版登录**：正在等待官方审批；**等许可发下来就会正式发行 1.0 Release**。
5. **自定义主题、自定义背景将在以后推出**：当前默认打开为 **曜石黑 × 电光黄 (`obsidian-yellow`)** 主题且默认 **无背景 (`none`)**。

> 查看完整历史变更、已修复 Bug 与遗留问题列表，请阅读 [CHANGELOG.md](CHANGELOG.md)。

---

## 更新报告 / Latest Update Report (2026-11-19 23:25 UTC+8)

### 已修复 (Fixed)
- **多加载器下载与安装**：修复版本下载界面仅显示 Fabric 的问题，恢复 **Fabric / NeoForge / Forge / Quilt / OptiFine** 多加载器列表与安装（含 Forge + OptiFine 组合）。
- **Java 安装修复**：修复点击官方 Java 下载按钮被通用安装事件拦截导致的无响应问题，支持连续多版本安装与错误提示。
- **下载器可靠性**：新增 12 秒分段超时自动切源与仅重传缺失区间、逐段 `Content-Range` 与强 `ETag` 核验、429/503 `Retry-After` 退避、`download-queue.json` 断点恢复、BMCLAPI NeoForge/Forge 加速与依赖预下载、Java 更新原子替换回滚。
- **CurseForge 整合包体验**：支持 Modrinth SHA-1 自动补全部分受限依赖、手动下载校验入库后自动清理「下载」目录源文件、统一手动依赖弹窗按钮尺寸。
- **主页与概览优化**：锁定主页标题框高度避免按钮上下跳动；「概览 → 版本图标」上方新增版本自定义命名框；整合包默认使用官方图标作为文件夹徽标。

### 尚存在的问题 (Known Issues)
- 报错分析与启动优化功能尚在完善中。
- CurseForge 等站点的资源下载仍可能出现卡住情况，持续改进中。
- 资源列表页数可能没有正常显示。
- 等待微软官方许可中，暂不支持正版登录；获批后将发行 1.0 Release。
- 自定义主题与背景将在以后版本推出。

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
