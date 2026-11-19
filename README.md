<p align="center">
  <img src="ui-prototype/assets/app-icon.png" alt="DummyCrisp Minecraft Launcher Logo" width="128" height="128" />
</p>

<h1 align="center">DummyCrisp Minecraft Launcher (DCL)</h1>

<p align="center">
  <strong>A modern Windows launcher for Minecraft: Java Edition</strong><br/>
  <em>Status: Work in Progress — Early Development Preview (Snapshot1)</em><br/>
  <a href="https://duckcrispp.github.io/DummyCrisp-Minecraft-Launcher/">Project Homepage</a> ·
  <a href="https://github.com/duckcrispp/DummyCrisp-Minecraft-Launcher">GitHub Repository</a>
</p>

---

> **Development Notice (WIP)**: DummyCrisp Minecraft Launcher is currently under active early development. Features, UI layouts, and APIs are subject to change prior to the initial public release.

> **Disclaimer**: NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

## Overview

**DummyCrisp Minecraft Launcher (DCL)** is a lightweight Windows desktop launcher for *Minecraft: Java Edition* built with **.NET 8 (WinForms + Microsoft Edge WebView2)** and a custom HTML/CSS/JS interface.

### Key Capabilities
- **Version & Instance Management**: Version isolation under `.minecraft/versions/<id>/`, external `.minecraft` / version folder zero-copy recognition & import, multi-loader support (Vanilla, Fabric, Forge, NeoForge, Quilt), Java runtime discovery/installation, and per-instance JVM GC presets (`Gen-ZGC`, `G1GC`, `Shenandoah`) with cancelable game launch before the game window opens.
- **Resource Browser & Downloader**: Unified search (`全部` / Modrinth / CurseForge) with clustered category, loader, dual-column MC version (`全部版本` + `热门版本`), resource pack resolution, and shader performance filters; automatic MCIM / BMCLAPI mirror routing; build-time optional CurseForge API key injection (`DCL_BUILD_CURSEFORGE_KEY`); embedded 33k+ Chinese/Pinyin project name index (`DCL_Backend/Data/mcmod-zh-cn.tsv.gz`); and progressive streaming pagination with SHA-1/SHA-512 verification.
- **Smart Runtime Diagnostics**: Automatic per-version Java version matching, adaptive `-Xmx` RAM allocation based on system memory, loader overhead, and mod count, plus crash log analysis.
- **Customizable UI**: 9 built-in color themes (defaulting to **Obsidian Black × Signal Yellow**, with no background pattern by default and an optional theme-adaptive SVG background overlay), launcher-themed custom HSV/HEX/RGB folder color picker, always-on-top window pin toggle, responsive frameless windowing, custom scrollbars, and keyboard/controller-friendly ergonomics.

## Authentication, Security & Privacy

DummyCrisp Minecraft Launcher uses **Microsoft OAuth 2.0 Public Client Device Code Flow** for *Minecraft: Java Edition* authentication (`AccountManager.cs`):
1. Requests a user device code from Microsoft's `/consumers/oauth2/v2.0/devicecode` endpoint with scope `XboxLive.signin offline_access`.
2. Launches the user's system browser directly to Microsoft's official verification page (`microsoft.com` / `login.microsoftonline.com`) — **the launcher never collects, handles, or stores Microsoft account passwords**.
3. Stores issued session/refresh tokens **only locally** on the user's device, encrypted per-Windows-user via **Windows DPAPI** (`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` inside `DCL/config/accounts.dat`).
4. Does **not** embed any client secret; requires only the public Azure Application (client) ID approved for Minecraft Services API access.

## Repository Structure

```text
DummyCrisp/
├── DCL_Backend/          # .NET 8 desktop host, WebView2 bridge, OAuth2, downloader, game launcher
├── ui-prototype/         # Embedded WebView2 frontend (HTML/CSS/JS, themes, SVG patterns/icons, assets)
└── docs/                 # Project homepage (GitHub Pages), architecture notes, setup guides
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
