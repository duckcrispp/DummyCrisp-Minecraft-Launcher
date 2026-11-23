# DummyCrisp Minecraft 启动器 — 开发流程与关键技术方案

## 1. 技术选型（贴合“轻量化 + 先 HTML 调试再打包”）

为了同时满足 **低内存/小体积、Windows 高兼容性、先做 HTML 交互调试再打包** 的要求，推荐采用 **Web 前端 + Tauri v2 (Rust)** 架构：

| 层级 | 推荐方案 | 理由 |
| :--- | :--- | :--- |
| **桌面壳与打包** | **Tauri v2** (调用 Windows 自带 WebView2) | Win10/11 默认内置 WebView2，安装包仅 ~5–15 MB，运行内存约 30–60 MB（远低于 Electron 的 150MB+ 内存与 100MB+ 体积）。 |
| **后端核心** | **Rust** (`tokio` + `reqwest` + `zip` + `serde`) | 编译为原生 Win32 二进制，无 GC 停顿；高并发下载、SHA1 校验、ZIP/JAR 解析性能强；直接调用 Win32 API 读取系统内存/注册表与托管子进程。 |
| **前端界面** | **原生 HTML/CSS/ES Module**（或轻量 Preact/Vite） | 无需复杂框架，直接在浏览器打开或本地静态服务即可调试交互与样式。 |
| **双端解耦层** | **`bridge.js` 通信适配层** | 浏览器调试时走 Mock 数据或本地调试 HTTP 服务；打包为桌面端时自动切换为 Tauri `invoke()`，做到**同一套 HTML 代码零重写打包**。 |

> 备选：如果更熟悉 Go，可用 **Wails v2/v3**（同样调用 WebView2，体积与内存略大于 Tauri 但开发门槛更低）。

---

## 2. 建议目录结构（`DummyCrisp/`）

```text
DummyCrisp/
├── docs\                   # 架构设计、协议与接口文档
├── ui-prototype\           # 纯 HTML/CSS/JS 交互原型（可在浏览器直接打开调试）
├── src-tauri\              # 后续打包阶段的 Rust 后端与 Tauri 配置
└── tests\                  # 解析器测试样例（version.json / crash-report / mod 元数据）
```

---

## 3. 分阶段开发流程（每阶段：HTML 调试 -> 核心联调 -> 打包验证）

### 阶段 1：基础框架、账号登录与 Java 管理
- **HTML 交互原型**：主界面框架（侧边栏、版本选择、启动按钮）、微软登录弹窗（Device Code 展示与复制）、Java 扫描列表与自动下载卡片。
- **核心功能**：
  - 微软 OAuth2 Device Code 登录链路（MSA -> Xbox Live -> XSTS -> MC Token -> Profile），使用 Windows DPAPI 加密持久化 Refresh Token。
  - Java 环境扫描（注册表、`PATH`、常见目录、`%APPDATA%\.minecraft` / PCL / HMCL 缓存）及 Adoptium / Azul API 下载与国内镜像加速。
- **打包验证**：打包最小可运行 EXE，验证登录态加密存储与本地 Java 识别。

### 阶段 2：原版下载与游戏启动引擎
- **HTML 交互原型**：版本安装向导（正式版/快照版过滤、镜像源切换）、多任务下载队列与实时进度条、内存与 JVM 参数设置面板。
- **核心功能**：
  - 解析 Mojang `version_manifest_v2.json` 与 `version.json`，实现原版 Client、Libraries（含 `rules` 系统/架构过滤与 `natives` 解压）、Assets 索引多线程并发下载与 SHA1 校验。
  - 拼接完整启动命令（`arguments.jvm` / `arguments.game` / 旧版 `minecraftArguments`），托管游戏子进程与实时日志流。
- **打包验证**：实测从零下载并成功拉起原版 Minecraft（覆盖 1.8.9 / 1.12.2 / 1.20.1 / 1.21+）。

### 阶段 3：模组加载器（Fabric / NeoForge / Forge）安装与管理
- **HTML 交互原型**：安装页集成加载器选择与版本匹配、已安装实例的加载器一键升级/切换/卸载面板。
- **核心功能**：
  - **Fabric**：请求 `meta.fabricmc.net`（及 BMCLAPI 镜像），直接合成或继承版本 JSON 与库下载。
  - **Forge / NeoForge**：解析安装包内的 `install_profile.json`，按依赖下载安装器库，**调用本地 Java 顺序执行 `processors`（如二进制补丁、SRG 重映射）** 并捕获进度；兼容 1.12.2 旧版与 1.13+ 新版结构。
- **打包验证**：实测各加载器安装、启动及同 MC 版本下加载器升级不破坏用户存档与配置。

### 阶段 4：版本隔离与可视化资源管理（Mod / 资源包 / 光影）
- **HTML 交互原型**：左侧实例列表 + 右侧分栏管理（Mod / 资源包 / 光影包），支持搜索筛选、拖拽导入、批量启用/禁用（开关）与图标/描述预览。
- **核心功能**：
  - 兼容 `.minecraft` 标准结构与版本隔离（`versions/<id>/` 下独立 `mods`、`resourcepacks`、`shaderpacks`、`saves`，通过 `--gameDir` 控制），可导入现有 HMCL/PCL 目录。
  - **Mod 元数据解析**：读取 JAR 内 `fabric.mod.json`、`META-INF/mods.toml`、`META-INF/neoforge.mods.toml`、`mcmod.info`，提取名称、版本、图标、前置依赖；通过 `.jar` ↔ `.jar.disabled` 实现无损启停。
  - **资源包/光影解析**：读取 `pack.mcmeta`、`pack.png`、`shaders.properties`，联动读写 `options.txt` / 光影配置文件。

### 阶段 5：CurseForge / Modrinth 资源中心与国内镜像
- **HTML 交互原型**：在线资源搜索页（分类、MC 版本、加载器筛选）、详情页（版本列表、依赖提示、一键安装到指定实例）、批量检查 Mod 更新界面。
- **核心功能**：
  - **Modrinth API v2** 搜索、版本筛选、依赖树自动拉取、通过本地文件 SHA1/SHA512 反查已安装 Mod 的更新版本。
  - **CurseForge API v1** 接入（支持用户自定义 API Key 或中转镜像服务如 MCIM），统一封装为抽象的资源源接口，支持双源与国内镜像（BMCLAPI / MCIM）自动回退与测速优选。

### 阶段 6：智能内存管理、报错分析与体验打磨
- **HTML 交互原型**：启动参数/JVM 预设编辑器、游戏崩溃诊断弹窗（高亮错误原因、问题 Mod 定位、一键导出诊断报告）。
- **核心功能**：
  - **内存管理**：通过 Windows API 读取物理总内存与当前可用内存，提供“动态智能分配”（依据实例 Mod 数量与空闲内存计算推荐 `-Xmx`）与手动滑块。
  - **报错分析引擎**：监听游戏非零退出码，结合实时 `stdout/stderr`、`logs/latest.log` 与 `crash-reports/crash-*.txt`，建立常见崩溃规则库（Java 版本不匹配、缺少前置 Mod、Mixin 冲突、显卡/OpenGL 驱动异常、OOM、重复 Mod、非法 JVM 参数）。
  - 最终体积裁剪（Release LTO / strip）与 Windows 兼容性测试（Win10/Win11、高 DPI 缩放、中文路径与用户名处理）。

---

## 4. 关键需要解决的核心问题

1. **Forge / NeoForge 安装器复杂性（最易踩坑）**
   - Fabric 只需下载 JSON 和 JAR；但 1.13+ Forge / NeoForge 需要执行 `install_profile.json` 中的 `processors`（运行特定主类并传参、生成客户端映射后的 JAR）。
   - **解法**：实现标准的 Processor Runner（解析 `{MINECRAFT_JAR}`、`{SIDE}` 等占位符并调用指定 Java 执行），并支持从 BMCLAPI 下载完整安装器包与依赖库，避免官网限速或校验失败。

2. **国内网络环境与 CurseForge API Key 限制**
   - Mojang 官方源、GitHub（部分 Mod/Java 下载）、CurseForge 在国内直连极慢；且 CurseForge 官方 API 要求绑定开发者 Key，直接硬编码在开源/客户端代码里有泄漏和封禁风险。
   - **解法**：下载器内置**多源调度器**（官方源 + BMCLAPI + MCIM 等镜像源自动测速/失败切换 + 断点续传与 SHA1 强校验）；CurseForge 优先支持接入聚合镜像或可配置的代理/Key。

3. **Windows 路径、编码与系统兼容性**
   - Windows 常见坑点：用户名或游戏目录含**中文/空格**导致旧版 Forge 或 Java 参数解析异常；高 DPI 下 WebView2 缩放模糊；WebView2 运行时极少数精简版 Win10 缺失。
   - **解法**：启动参数强制注入 `-Dfile.encoding=UTF-8`，路径传递规范引用；Tauri 打包时提供引导/在线补全 WebView2 安装器（Bootstrapper 仅 ~1.5MB）；避免依赖管理员权限，配置保存在用户目录或便携模式（同级目录配置检测）。

4. **全版本 MC 启动规则差异（1.7 ~ 1.21+）**
   - 1.12.2 及以下使用旧版 `minecraftArguments` 字符串与 `log4j` 漏洞修复参数；1.13+ 改用结构化 `arguments.jvm` / `arguments.game` 规则数组；1.20.5+ 强制要求 Java 21；Natives 在新版 MC 中逐渐由独立 DLL 解压变为 Maven JAR 内置。
   - **解法**：编写统一的 `VersionJson` 合并器（处理子版本 `inheritsFrom` 父类继承与库去重/版本冲突仲裁），按 MC 版本号与 `rules` 字段精确匹配参数与 Java 版本。

5. **正版账号安全与离线容错**
   - 微软登录 Token 链条长（4 次 API 交换），Access Token 24 小时过期。
   - **解法**：后台静默使用 Refresh Token 续期；利用 Windows 原生 `CryptProtectData` (DPAPI) 加密存储凭据文件，确保明文 Token 不落盘。

6. **HTML 调试与桌面打包的一致性**
   - 纯网页无法直接读写本地磁盘、执行 `java.exe` 或绕过部分接口 CORS。
   - **解法**：从阶段 1 起严格约束前端不直接写死 `fetch` 外部受限接口或浏览器不可用的本地 API，所有数据与操作一律经过 `bridge.js`（浏览器模式由轻量本地调试代理或 Mock JSON 驱动，桌面模式由 Rust Tauri Command 驱动）。
