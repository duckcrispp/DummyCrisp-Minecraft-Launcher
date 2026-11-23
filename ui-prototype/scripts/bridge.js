/* ==========================================================================
   DummyCrisp Launcher Bridge
   Browser Prototype Mode <-> Tauri v2 Invoke Adapter (Zero-Rewrite Packaging)
   ========================================================================== */

(function (global) {
  const isTauri = Boolean(global.__TAURI__ && global.__TAURI__.core);
  const isWebView2 = Boolean(global.chrome && global.chrome.webview && typeof global.chrome.webview.postMessage === "function");

  const rpcPending = new Map();
  const hostListeners = new Set();
  let rpcSeq = 0;

  function applyBackendState(dto) {
    if (!dto || typeof dto !== "object") return;
    Object.assign(state, dto);
    trimCompletedDownloadTasks();
  }

  function invokeWebView2(cmd, payload) {
    return new Promise((resolve, reject) => {
      const id = `rpc-${++rpcSeq}`;
      rpcPending.set(id, { resolve, reject });
      global.chrome.webview.postMessage({ id, cmd, payload: payload || {} });
    });
  }

  if (isWebView2 && typeof global.chrome.webview.addEventListener === "function") {
    let remainingErrorReports = 20;
    function reportUiError(message) {
      if (remainingErrorReports-- <= 0) return;
      global.chrome.webview.postMessage({ cmd: "log_ui_error", payload: { message: String(message).slice(0, 2000) } });
    }
    global.addEventListener("error", (event) => reportUiError(event.error?.stack || event.message));
    global.addEventListener("unhandledrejection", (event) => reportUiError(event.reason?.stack || event.reason));
    global.chrome.webview.addEventListener("message", (ev) => {
      const msg = typeof ev.data === "string" ? JSON.parse(ev.data) : ev.data;
      if (!msg) return;
      if (msg.id && rpcPending.has(msg.id)) {
        const entry = rpcPending.get(msg.id);
        rpcPending.delete(msg.id);
        if (msg.ok) entry.resolve(msg.result);
        else entry.reject(new Error(msg.error || "Host RPC failed"));
        return;
      }
      if (msg.event) {
        if (msg.event === "download_updated" && Array.isArray(msg.data)) {
          state.downloadTasks = msg.data;
          trimCompletedDownloadTasks();
        } else if (msg.event === "javas_updated" && Array.isArray(msg.data)) {
          state.javas = msg.data;
        } else if (msg.event === "mirror_updated" && msg.data) {
          if (msg.data.mirrorSource) state.mirrorSource = msg.data.mirrorSource;
          if (typeof msg.data.autoMirror === "boolean") state.autoMirror = msg.data.autoMirror;
          if (Array.isArray(msg.data.mirrors)) state.mirrors = msg.data.mirrors;
        } else if (msg.event === "workspace_refreshed" && msg.data) {
          applyBackendState(msg.data);
        } else if (msg.event === "active_resources_ready" && msg.data) {
          if (Array.isArray(msg.data.installedMods)) state.installedMods = msg.data.installedMods;
          if (Array.isArray(msg.data.installedPacks)) state.installedPacks = msg.data.installedPacks;
          if (Array.isArray(msg.data.crashLines)) state.crashLines = msg.data.crashLines;
        }
        hostListeners.forEach((fn) => {
          try { fn(msg.event, msg.data); } catch {}
        });
      }
    });
  }

  const mirrorNodes = [
    { name: "BMCLAPI", url: "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json", pingMs: 26, availability: 0.992, bandwidthMbps: 420 },
    { name: "MCIM", url: "https://mod.mcimirror.top/", pingMs: 39, availability: 0.985, bandwidthMbps: 310 },
    { name: "官方源", url: "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", pingMs: 184, availability: 0.995, bandwidthMbps: 140 }
  ];

  function scoreMirrorNode(node) {
    // Lower score is better: latency penalized by reliability gap and low throughput
    const penalty = (node.pingMs || 0) >= 2500 ? 3000 : (1 - node.availability) * 1000;
    const throughputBonus = node.bandwidthMbps * 0.04;
    return Number((node.pingMs + penalty - throughputBonus).toFixed(2));
  }

  function pickOptimalMirror() {
    const ranked = [...mirrorNodes].sort((a, b) => scoreMirrorNode(a) - scoreMirrorNode(b));
    return ranked[0];
  }

  async function probeBrowserMirrorsAndSwitch() {
    if (isWebView2) return null;
    await Promise.all(
      mirrorNodes.map(async (m) => {
        const ctrl = typeof AbortController === "function" ? new AbortController() : null;
        const timer = ctrl ? setTimeout(() => ctrl.abort(), 3200) : null;
        const t0 = performance.now();
        try {
          await fetch(m.url, { mode: "no-cors", cache: "no-store", signal: ctrl ? ctrl.signal : undefined });
          m.pingMs = Math.max(8, Math.round(performance.now() - t0));
        } catch {
          m.pingMs = 2800;
        } finally {
          if (timer) clearTimeout(timer);
        }
      })
    );
    state.mirrors = mirrorNodes.map((m) => Object.assign({}, m, { score: scoreMirrorNode(m) }));
    let switched = false;
    if (state.autoMirror) {
      const best = pickOptimalMirror();
      const curNode = mirrorNodes.find((x) => x.name === state.mirrorSource) || best;
      if (best.name !== state.mirrorSource && (curNode.pingMs >= 1200 || scoreMirrorNode(best) + 12 < scoreMirrorNode(curNode))) {
        state.mirrorSource = best.name;
        switched = true;
      }
    }
    const dto = { mirrorSource: state.mirrorSource, autoMirror: state.autoMirror, switched, mirrors: state.mirrors };
    hostListeners.forEach((fn) => {
      try { fn("mirror_updated", dto); } catch {}
    });
    return dto;
  }

  if (!isWebView2 && typeof global.setInterval === "function") {
    global.setTimeout(() => { probeBrowserMirrorsAndSwitch().catch(() => {}); }, 2200);
    global.setInterval(() => { probeBrowserMirrorsAndSwitch().catch(() => {}); }, 20000);
  }

  const optimalMirror = pickOptimalMirror();

  const state = {
    activeInstanceId: "inst-neoforge-21",
    recentInstanceIds: [
      "inst-neoforge-21",
      "inst-fabric-21",
      "inst-forge-20",
      "inst-vanilla-12",
      "inst-atm10",
      "inst-create-20"
    ],
    mirrorSource: optimalMirror.name,
    autoMirror: true,
    curseForgeApiRoute: "auto",
    themeId: "obsidian-yellow",
    bgPatternId: "none",
    alwaysOnTop: false,
    bypassSystemProxyOnDownload: false,
    disableChineseResourceNames: false,
    mirrors: mirrorNodes.map((n) => ({ ...n, score: scoreMirrorNode(n) })),
    downloadTasks: [
      {
        id: "dl-1",
        name: "Sodium 0.6.5",
        sub: "sodium-neoforge-0.6.5+mc1.21.1.jar",
        icon: "assets/mod-icons/sodium.webp",
        source: optimalMirror.name,
        size: "1.4 MB",
        speed: "18.6 MB/s",
        progress: 74,
        status: "downloading"
      },
      {
        id: "dl-2",
        name: "Create 6.0.2",
        sub: "create-1.21.1-6.0.2.jar",
        icon: "assets/mod-icons/create.webp",
        source: optimalMirror.name,
        size: "16.8 MB",
        speed: "已暂停",
        progress: 38,
        status: "paused"
      },
      {
        id: "dl-3",
        name: "Iris Shaders 1.8.1",
        sub: "iris-neoforge-1.8.1.jar",
        icon: "assets/mod-icons/iris.webp",
        source: optimalMirror.name,
        size: "2.7 MB",
        speed: "已完成",
        progress: 100,
        status: "completed"
      }
    ],
    hasUnseenDownloadBall: false,
    networkLoading: false,
    autoMemory: true,
    memoryMb: 4096,
    systemRamMb: 32768,
    jvmPreset: "Gen-ZGC (Java 21)",
    customJvmArgs: "-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8",
    officialJavas: [
      { id: "off-25", version: "25", fullVersion: "25.0.0", channel: "Latest", vendor: "Oracle Official", url: "https://www.oracle.com/java/technologies/downloads/#java25", size: "184 MB", note: "最新官方特性发布版" },
      { id: "off-21", version: "21", fullVersion: "21.0.6 LTS", channel: "LTS", vendor: "Oracle / Eclipse Temurin", url: "https://adoptium.net/temurin/releases/?version=21", size: "194 MB", note: "MC 1.20.5 - 1.21.4 官方推荐长期支持版" },
      { id: "off-17", version: "17", fullVersion: "17.0.14 LTS", channel: "LTS", vendor: "Eclipse Temurin / Oracle", url: "https://adoptium.net/temurin/releases/?version=17", size: "186 MB", note: "MC 1.18 - 1.20.4 长期支持版" },
      { id: "off-11", version: "11", fullVersion: "11.0.26 LTS", channel: "LTS", vendor: "Eclipse Temurin", url: "https://adoptium.net/temurin/releases/?version=11", size: "177 MB", note: "MC 1.17 过渡兼容版本" },
      { id: "off-8", version: "8", fullVersion: "8u442 LTS", channel: "LTS", vendor: "Oracle Java / Azul Zulu", url: "https://www.java.com/zh-CN/download/", size: "99 MB", note: "MC 1.16.5 及经典 1.12.2 / 1.7.10" }
    ],
    accounts: [
      {
        id: "acc-steve",
        username: "Steve_Crisp",
        uuid: "8667ba71-b85a-4004-af54-457a9734eed7",
        type: "Microsoft 正版",
        avatar: "assets/mc-icons/player-head.png"
      },
      {
        id: "acc-alex",
        username: "Alex_Builder",
        uuid: "c06f8906-4c8a-4fd2-9e12-319c7120a11b",
        type: "Microsoft 正版",
        avatar: "assets/mc-icons/fox.png"
      },
      {
        id: "acc-offline",
        username: "Crisp_Local",
        uuid: "341a0092-11b2-3c88-9001-offline00001",
        type: "离线账号",
        avatar: "assets/mc-icons/creeper.png"
      }
    ],
    account: {
      loggedIn: true,
      id: "acc-steve",
      username: "Steve_Crisp",
      uuid: "8667ba71-b85a-4004-af54-457a9734eed7",
      type: "Microsoft 正版",
      avatar: "assets/mc-icons/player-head.png"
    },
    instances: [
      {
        id: "inst-neoforge-21",
        name: "Crisp 1.21.1 工业探索",
        mcVersion: "1.21.1",
        loader: "NeoForge",
        loaderVersion: "21.1.72",
        javaId: "j21",
        javaVersion: "Java 21.0.5",
        jvmArgs: "-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8",
        iconId: "grass",
        folderColor: "amber",
        favorite: true,
        modsCount: 48,
        lastPlayed: "今天 21:40",
        isolated: true,
        tagline: "机械动力与高性能渲染组合，已启用版本隔离。"
      },
      {
        id: "inst-fabric-21",
        name: "Fabric 极致光影纯净端",
        mcVersion: "1.21.4",
        loader: "Fabric",
        loaderVersion: "0.16.9",
        javaId: "j21",
        javaVersion: "Java 21.0.5",
        jvmArgs: "-XX:+UseG1GC -XX:MaxGCPauseMillis=30 -Dfile.encoding=UTF-8",
        iconId: "grass",
        folderColor: "cobalt",
        favorite: true,
        modsCount: 19,
        lastPlayed: "昨天 23:12",
        isolated: true,
        tagline: "Sodium + Iris 光影管线，轻量高帧率调校。"
      },
      {
        id: "inst-forge-20",
        name: "Forge 1.20.1 经典冒险",
        mcVersion: "1.20.1",
        loader: "Forge",
        loaderVersion: "47.3.12",
        javaId: "j17",
        javaVersion: "Java 17.0.12",
        jvmArgs: "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions",
        iconId: "grass",
        folderColor: "moss",
        favorite: true,
        modsCount: 86,
        lastPlayed: "3 天前",
        isolated: true,
        tagline: "暮色森林与魔法拓展，兼容经典模组。"
      },
      {
        id: "inst-vanilla-12",
        name: "1.12.2 怀旧档案库",
        mcVersion: "1.12.2",
        loader: "Vanilla",
        loaderVersion: "原版",
        javaId: "j8",
        javaVersion: "Java 8u422",
        jvmArgs: "-XX:+UseConcMarkSweepGC",
        iconId: "grass",
        favorite: true,
        modsCount: 0,
        lastPlayed: "1 周前",
        isolated: false,
        tagline: "纯净原版留档，自动匹配 Java 8 运行环境。"
      },
      {
        id: "inst-atm10",
        name: "All the Mods 10",
        mcVersion: "1.21.1",
        loader: "NeoForge",
        loaderVersion: "21.1.89",
        javaId: "j21",
        javaVersion: "Java 21.0.5",
        jvmArgs: "-XX:+UseZGC -XX:+ZGenerational",
        iconId: "gold",
        customIcon: "assets/mod-icons/iris.webp",
        isModpack: true,
        folderColor: "slate",
        favorite: true,
        modsCount: 312,
        lastPlayed: "2 周前",
        isolated: true,
        tagline: "大型科技魔法综合整合包。"
      },
      {
        id: "inst-create-20",
        name: "Create 机械工坊",
        mcVersion: "1.20.1",
        loader: "Forge",
        loaderVersion: "47.3.10",
        javaId: "j17",
        javaVersion: "Java 17.0.12",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "crafting",
        customIcon: "assets/mod-icons/create.webp",
        isModpack: true,
        folderColor: "terracotta",
        favorite: true,
        modsCount: 64,
        lastPlayed: "2 周前",
        isolated: true,
        tagline: "蒸汽火车与齿轮自动化。"
      },
      {
        id: "inst-opt-21",
        name: "Fabulously Optimized",
        mcVersion: "1.21.4",
        loader: "Fabric",
        loaderVersion: "0.16.10",
        javaId: "j21",
        javaVersion: "Java 21.0.5",
        jvmArgs: "-XX:+UseZGC",
        iconId: "redstone",
        customIcon: "assets/mod-icons/fabulously-optimized.webp",
        isModpack: true,
        folderColor: "glacier",
        favorite: true,
        modsCount: 52,
        lastPlayed: "3 周前",
        isolated: true,
        tagline: "高帧率轻量优化整合包。"
      },
      {
        id: "inst-yuushya",
        name: "Yuushya 城镇建筑",
        mcVersion: "1.20.1",
        loader: "Fabric",
        loaderVersion: "0.16.8",
        javaId: "j17",
        javaVersion: "Java 17.0.12",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "crafting",
        customIcon: "assets/mod-icons/fresh-animations.webp",
        isModpack: true,
        folderColor: "linen",
        favorite: true,
        modsCount: 31,
        lastPlayed: "1 个月前",
        isolated: true,
        tagline: "细腻建筑方块与光影搭配。"
      },
      {
        id: "inst-rlcraft",
        name: "RLCraft 硬核生存",
        mcVersion: "1.12.2",
        loader: "Forge",
        loaderVersion: "14.23.5",
        javaId: "j8",
        javaVersion: "Java 8u422",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "enderman",
        customIcon: "assets/mod-icons/sodium.webp",
        isModpack: true,
        favorite: false,
        modsCount: 168,
        lastPlayed: "1 个月前",
        isolated: true,
        tagline: "硬核真实系冒险挑战。"
      },
      {
        id: "inst-vault-18",
        name: "Vault Hunters 1.18",
        mcVersion: "1.18.2",
        loader: "Forge",
        loaderVersion: "40.2.21",
        javaId: "j17",
        javaVersion: "Java 17.0.12",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "command",
        customIcon: "assets/mod-icons/jei.webp",
        isModpack: true,
        favorite: false,
        modsCount: 194,
        lastPlayed: "2 个月前",
        isolated: true,
        tagline: "地牢秘宝刷图挑战整合包。"
      },
      {
        id: "inst-twilight-16",
        name: "1.16.5 暮色神域",
        mcVersion: "1.16.5",
        loader: "Forge",
        loaderVersion: "36.2.42",
        javaId: "j8",
        javaVersion: "Java 8u422",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "grass",
        favorite: false,
        modsCount: 95,
        lastPlayed: "3 个月前",
        isolated: true,
        tagline: "经典暮色森林与拔刀剑。"
      },
      {
        id: "inst-pvp-8",
        name: "1.8.9 极速 PvP",
        mcVersion: "1.8.9",
        loader: "Forge",
        loaderVersion: "11.15.1",
        javaId: "j8",
        javaVersion: "Java 8u422",
        jvmArgs: "-XX:+UseG1GC",
        iconId: "grass",
        favorite: false,
        modsCount: 14,
        lastPlayed: "半年前",
        isolated: true,
        tagline: "低延迟竞技服务端专用。"
      }
    ],
    installedMods: [
      { id: "sodium", name: "Sodium", cnName: "钠", icon: "assets/mod-icons/sodium.webp", file: "sodium-neoforge-0.6.3.jar", version: "0.6.3", source: "Modrinth", loader: "NeoForge", enabled: true, hasUpdate: true, latest: "0.6.5" },
      { id: "iris", name: "Iris Shaders", cnName: "", icon: "assets/mod-icons/iris.webp", file: "iris-neoforge-1.8.1.jar", version: "1.8.1", source: "Modrinth", loader: "NeoForge", enabled: true, hasUpdate: false, latest: "1.8.1" },
      { id: "jei", name: "Just Enough Items (JEI)", cnName: "JEI 物品管理器", icon: "assets/mod-icons/jei.webp", file: "jei-1.21.1-neoforge-19.21.0.jar", version: "19.21.0", source: "CurseForge", loader: "NeoForge", enabled: true, hasUpdate: false, latest: "19.21.0" },
      { id: "xaero", name: "Xaero's Minimap", cnName: "Xaero 的小地图", icon: "assets/mod-icons/xaeros-minimap.webp", file: "Xaeros_Minimap_24.6.1.jar", version: "24.6.1", source: "CurseForge/Modrinth", loader: "NeoForge", enabled: true, hasUpdate: true, latest: "24.7.0" },
      { id: "ferrite", name: "FerriteCore", cnName: "铁氧体核心", icon: "assets/mod-icons/ferrite-core.webp", file: "ferritecore-7.0.2-neoforge.jar", version: "7.0.2", source: "CurseForge/Modrinth", loader: "NeoForge", enabled: true, hasUpdate: false, latest: "7.0.2" },
      { id: "create", name: "Create", cnName: "机械动力", icon: "assets/mod-icons/create.webp", file: "create-1.21.1-6.0.2.jar", version: "6.0.2", source: "CurseForge/Modrinth", loader: "NeoForge", enabled: true, hasUpdate: false, latest: "6.0.2" },
      { id: "lithium", name: "Lithium", cnName: "锂", icon: "assets/mod-icons/lithium.webp", file: "lithium-neoforge-0.14.3.jar", version: "0.14.3", source: "Modrinth", loader: "NeoForge", enabled: true, hasUpdate: false, latest: "0.14.3" },
      ...[
        ["苹果皮", "AppleSkin", "3.0.6", "CurseForge/Modrinth", "assets/mod-icons/fresh-animations.webp"],
        ["玉", "Jade", "15.9.2", "Modrinth", "assets/mod-icons/sodium.webp"],
        ["", "Cloth Config API", "15.0.140", "CurseForge/Modrinth", "assets/mod-icons/ferrite-core.webp"],
        ["", "Mouse Tweaks", "2.26.1", "CurseForge", "assets/mod-icons/jei.webp"],
        ["农夫乐事", "Farmer's Delight", "1.2.6", "CurseForge/Modrinth", "assets/mod-icons/create.webp"],
        ["应用能源 2", "Applied Energistics 2", "19.2.1", "CurseForge", "assets/mod-icons/iris.webp"],
        ["传送石碑", "Waystones", "21.1.11", "CurseForge/Modrinth", "assets/mod-icons/xaeros-minimap.webp"],
        ["精妙背包", "Sophisticated Backpacks", "3.23.1", "CurseForge", "assets/mod-icons/create.webp"],
        ["现代修复", "ModernFix", "5.20.2", "Modrinth", "assets/mod-icons/lithium.webp"],
        ["", "ImmediatelyFast", "1.3.4", "Modrinth", "assets/mod-icons/sodium.webp"],
        ["自然指南针", "Nature's Compass", "1.11.7", "CurseForge/Modrinth", "assets/mod-icons/xaeros-minimap.webp"],
        ["", "Entity Culling", "1.7.2", "Modrinth", "assets/mod-icons/ferrite-core.webp"],
        ["Xaero 世界地图", "Xaero's World Map", "1.39.2", "CurseForge/Modrinth", "assets/mod-icons/xaeros-minimap.webp"]
      ].flatMap((b, bi) =>
        Array.from({ length: 3 }, (_, c) => {
          const i = bi * 3 + c;
          const suf = c === 0 ? "" : ` ${c === 1 ? "拓展模块" : "兼容插件"}`;
          const sufEn = c === 0 ? "" : ` ${c === 1 ? "Addon" : "Compat"}`;
          const baseFile = b[1].toLowerCase().replace(/[^a-z0-9]+/g, "-");
          return {
            id: `inst-mod-${i}`,
            name: `${b[1]}${sufEn}`,
            cnName: b[0] ? `${b[0]}${suf}` : "",
            icon: b[4],
            file: `${baseFile}-${b[2]}.jar`,
            version: b[2],
            source: b[3],
            loader: "NeoForge",
            enabled: i % 9 !== 4,
            hasUpdate: i % 7 === 0,
            latest: `${b[2]}.1`
          };
        })
      )
    ],
    installedPacks: [
      { id: "rp-faithful", kind: "资源包", name: "Faithful 32x", icon: "assets/mod-icons/faithful-32x.png", spec: "pack_format 34 | 32x32", enabled: true },
      { id: "rp-fresh-anim", kind: "资源包", name: "Fresh Animations", icon: "assets/mod-icons/fresh-animations.webp", spec: "pack_format 34 | 实体动画", enabled: true },
      { id: "sh-complementary", kind: "光影包", name: "Complementary Unbound", icon: "assets/mod-icons/complementary-reimagined.webp", spec: "r5.3 | Iris 兼容", enabled: true },
      { id: "sh-bsl", kind: "光影包", name: "BSL Shaders", icon: "assets/mod-icons/iris.webp", spec: "v8.4", enabled: false }
    ],
    javas: [
      { id: "j21", version: "21.0.5", vendor: "Eclipse Temurin (Adoptium)", arch: "x64", path: "C:\\Program Files\\Eclipse Adoptium\\jdk-21.0.5\\bin\\javaw.exe", active: true },
      { id: "j17", version: "17.0.12", vendor: "Microsoft OpenJDK", arch: "x64", path: "C:\\Program Files\\Microsoft\\jdk-17.0.12\\bin\\javaw.exe", active: false },
      { id: "j8", version: "1.8.0_422", vendor: "Azul Zulu", arch: "x64", path: "D:\\MC\\DummyCrisp\\runtime\\zulu8\\bin\\javaw.exe", active: false }
    ],
    catalog: {
      versions: [
        { id: "1.21.4", type: "最新正式版", category: "release", releaseTime: "2024/12/03 19:23", icon: "assets/mc-icons/grass.png", date: "2024-12-03", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 21" },
        { id: "24w45a", type: "最新预览版", category: "snapshot", releaseTime: "2024/11/06 22:20", icon: "assets/mc-icons/command.png", date: "2024-11-06", loaders: ["Fabric", "NeoForge"], java: "Java 21" },
        { id: "1.21.3", type: "正式版", category: "release", releaseTime: "2024/10/23 17:10", icon: "assets/mc-icons/grass.png", date: "2024-10-23", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 21" },
        { id: "1.21.1", type: "正式版", category: "release", releaseTime: "2024/08/08 18:05", icon: "assets/mc-icons/grass.png", date: "2024-08-08", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 21" },
        { id: "1.21", type: "正式版", category: "release", releaseTime: "2024/06/13 16:40", icon: "assets/mc-icons/grass.png", date: "2024-06-13", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 21" },
        { id: "1.20.6", type: "正式版", category: "release", releaseTime: "2024/04/29 19:00", icon: "assets/mc-icons/grass.png", date: "2024-04-29", loaders: ["Fabric", "NeoForge", "OptiFine"], java: "Java 21" },
        { id: "1.20.4", type: "正式版", category: "release", releaseTime: "2023/12/07 18:15", icon: "assets/mc-icons/grass.png", date: "2023-12-07", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 17" },
        { id: "1.20.1", type: "正式版", category: "release", releaseTime: "2023/06/12 17:30", icon: "assets/mc-icons/grass.png", date: "2023-06-12", loaders: ["Forge", "NeoForge", "Fabric", "OptiFine"], java: "Java 17" },
        { id: "1.19.4", type: "正式版", category: "release", releaseTime: "2023/03/14 16:20", icon: "assets/mc-icons/grass.png", date: "2023-03-14", loaders: ["Forge", "Fabric", "OptiFine"], java: "Java 17" },
        { id: "1.19.2", type: "正式版", category: "release", releaseTime: "2022/08/05 18:45", icon: "assets/mc-icons/grass.png", date: "2022-08-05", loaders: ["Forge", "Fabric", "OptiFine"], java: "Java 17" },
        { id: "1.18.2", type: "正式版", category: "release", releaseTime: "2022/02/28 17:00", icon: "assets/mc-icons/grass.png", date: "2022-02-28", loaders: ["Forge", "Fabric", "OptiFine"], java: "Java 17" },
        { id: "1.16.5", type: "经典版", category: "release", releaseTime: "2021/01/15 16:50", icon: "assets/mc-icons/grass.png", date: "2021-01-15", loaders: ["Forge", "Fabric", "OptiFine"], java: "Java 8" },
        { id: "1.12.2", type: "经典版", category: "release", releaseTime: "2017/09/18 15:30", icon: "assets/mc-icons/grass.png", date: "2017-09-18", loaders: ["Forge", "OptiFine"], java: "Java 8" },
        { id: "24w14potato", type: "愚人节版 · 毒马铃薯更新", category: "april", releaseTime: "2024/04/01 16:00", icon: "assets/mc-icons/tnt.png", date: "2024-04-01", loaders: [], java: "Java 21" },
        { id: "23w13a_or_b", type: "愚人节版 · 投票更新", category: "april", releaseTime: "2023/04/01 16:00", icon: "assets/mc-icons/tnt.png", date: "2023-04-01", loaders: [], java: "Java 17" },
        { id: "20w14infinite", type: "愚人节版 · 无限维度", category: "april", releaseTime: "2020/04/01 16:00", icon: "assets/mc-icons/tnt.png", date: "2020-04-01", loaders: [], java: "Java 8" },
        { id: "15w14a", type: "愚人节版 · 爱与拥抱", category: "april", releaseTime: "2015/04/01 16:00", icon: "assets/mc-icons/tnt.png", date: "2015-04-01", loaders: [], java: "Java 8" },
        { id: "b1.7.3", type: "远古版 · Beta", category: "ancient", releaseTime: "2011/07/08 12:00", icon: "assets/mc-icons/bedrock.png", date: "2011-07-08", loaders: [], java: "Java 8" },
        { id: "b1.6.6", type: "远古版 · Beta", category: "ancient", releaseTime: "2011/05/31 12:00", icon: "assets/mc-icons/bedrock.png", date: "2011-05-31", loaders: [], java: "Java 8" },
        { id: "a1.2.6", type: "远古版 · Alpha", category: "ancient", releaseTime: "2010/12/03 12:00", icon: "assets/mc-icons/bedrock.png", date: "2010-12-03", loaders: [], java: "Java 8" },
        { id: "c0.30_01c", type: "远古版 · Classic", category: "ancient", releaseTime: "2009/11/10 12:00", icon: "assets/mc-icons/bedrock.png", date: "2009-11-10", loaders: [], java: "Java 8" },
        { id: "rd-132211", type: "远古版 · Pre-Classic", category: "ancient", releaseTime: "2009/05/13 20:11", icon: "assets/mc-icons/bedrock.png", date: "2009-05-13", loaders: [], java: "Java 8" },
        ...Array.from({ length: 44 }, (_, i) => {
          const minors = ["1.21.2", "1.20.5", "1.20.3", "1.20.2", "1.20", "1.19.3", "1.19.1", "1.19", "1.18.1", "1.18", "1.17.1", "1.17", "1.16.4", "1.16.3", "1.16.2", "1.16.1", "1.15.2", "1.14.4", "1.13.2", "1.11.2", "1.10.2", "1.9.4", "1.8.9", "1.7.10"];
          const ver = i < minors.length ? minors[i] : `24w${String(44 - (i - minors.length)).padStart(2, "0")}a`;
          const isSnap = i >= minors.length;
          return {
            id: ver,
            type: isSnap ? "预览版" : "正式版",
            category: isSnap ? "snapshot" : "release",
            releaseTime: isSnap ? "2024/10/30 18:40" : "2021/06/08 17:00",
            icon: isSnap ? "assets/mc-icons/command.png" : "assets/mc-icons/grass.png",
            date: isSnap ? "2024-10-30" : "2021-06-08",
            loaders: isSnap ? ["Fabric", "NeoForge"] : i % 3 === 0 ? ["Forge", "NeoForge", "Fabric", "OptiFine"] : i % 2 === 0 ? ["Forge", "Fabric", "OptiFine"] : ["Forge", "OptiFine"],
            java: isSnap ? "Java 21" : i < 12 ? "Java 17" : "Java 8"
          };
        })
      ],
      mods: [
        { id: "mr-sodium", cnName: "钠", enName: "Sodium", icon: "assets/mod-icons/sodium.webp", downloads: "14820.0 万次", loaders: ["Fabric", "NeoForge"], mcVersions: "1.20.1 - 1.21.4", source: "Modrinth" },
        { id: "mr-iris", cnName: "", enName: "Iris Shaders", icon: "assets/mod-icons/iris.webp", downloads: "7940.0 万次", loaders: ["Fabric", "NeoForge"], mcVersions: "1.20.1 - 1.21.4", source: "Modrinth" },
        { id: "mr-create", cnName: "机械动力", enName: "Create", icon: "assets/mod-icons/create.webp", downloads: "11260.0 万次", loaders: ["Forge", "Fabric", "NeoForge"], mcVersions: "1.20.1 / 1.21.1", source: "CurseForge/Modrinth" },
        { id: "cf-jei", cnName: "JEI 物品管理器", enName: "Just Enough Items (JEI)", icon: "assets/mod-icons/jei.webp", downloads: "29400.0 万次", loaders: ["Forge", "Fabric", "NeoForge"], mcVersions: "1.12.2 - 1.21.4", source: "CurseForge" },
        { id: "cf-ferrite", cnName: "铁氧体核心", enName: "FerriteCore", icon: "assets/mod-icons/ferrite-core.webp", downloads: "8350.0 万次", loaders: ["Forge", "Fabric", "NeoForge"], mcVersions: "1.18.2 - 1.21.4", source: "CurseForge/Modrinth" },
        { id: "mr-lithium", cnName: "锂", enName: "Lithium", icon: "assets/mod-icons/lithium.webp", downloads: "6410.0 万次", loaders: ["Fabric", "NeoForge"], mcVersions: "1.20.1 - 1.21.4", source: "CurseForge/Modrinth" },
        { id: "xaero-map", cnName: "Xaero 的小地图", enName: "Xaero's Minimap", icon: "assets/mod-icons/xaeros-minimap.webp", downloads: "10580.0 万次", loaders: ["Forge", "Fabric", "NeoForge"], mcVersions: "1.12.2 - 1.21.4", source: "CurseForge/Modrinth" },
        ...[
          ["苹果皮", "AppleSkin", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "158.0"],
          ["玉", "Jade", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "92.3"],
          ["模组菜单", "Mod Menu", ["Fabric"], "Modrinth", "74.5"],
          ["", "Cloth Config API", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "121.4"],
          ["", "Entity Texture Features", ["Fabric", "NeoForge"], "Modrinth", "38.2"],
          ["", "Mouse Tweaks", ["Forge", "Fabric", "NeoForge"], "CurseForge", "146.9"],
          ["农夫乐事", "Farmer's Delight", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "89.1"],
          ["应用能源 2", "Applied Energistics 2", ["Forge", "NeoForge"], "CurseForge/Modrinth", "110.4"],
          ["沉浸工程", "Immersive Engineering", ["Forge", "NeoForge"], "CurseForge", "96.2"],
          ["自然指南针", "Nature's Compass", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "68.4"],
          ["传送石碑", "Waystones", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "104.7"],
          ["精妙背包", "Sophisticated Backpacks", ["Forge", "NeoForge"], "CurseForge/Modrinth", "84.3"],
          ["机械动力：齿轮与蒸汽", "Create: Steam 'n' Rails", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "36.1"],
          ["", "LambDynamicLights", ["Fabric", "NeoForge"], "Modrinth", "42.7"],
          ["", "ImmediatelyFast", ["Fabric", "NeoForge"], "Modrinth", "51.9"],
          ["现代修复", "ModernFix", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "61.8"],
          ["Xaero 的世界地图", "Xaero's World Map", ["Forge", "Fabric", "NeoForge"], "CurseForge/Modrinth", "87.6"]
        ].flatMap((base, bIdx) =>
          Array.from({ length: 6 }, (_, copy) => {
            const idx = bIdx * 6 + copy;
            const icons = [
              "assets/mod-icons/sodium.webp",
              "assets/mod-icons/iris.webp",
              "assets/mod-icons/create.webp",
              "assets/mod-icons/jei.webp",
              "assets/mod-icons/ferrite-core.webp",
              "assets/mod-icons/lithium.webp",
              "assets/mod-icons/xaeros-minimap.webp"
            ];
            const suffixCn = copy === 0 ? "" : ` Addon ${copy + 1}`;
            const suffixEn = copy === 0 ? "" : ` Addon ${copy + 1}`;
            const dlWan = (Math.max(1.2, parseFloat(base[4]) / (copy + 1)) * 100).toFixed(1);
            return {
              id: `gen-mod-${idx}`,
              cnName: copy === 0 && base[0] ? base[0] : "",
              enName: `${base[1]}${suffixEn}`,
              icon: icons[idx % icons.length],
              downloads: `${dlWan} 万次`,
              loaders: base[2],
              mcVersions: idx % 3 === 0 ? "1.20.1 - 1.21.4" : idx % 2 === 0 ? "1.21.1 / 1.21.4" : "1.20.1 / 1.21.1",
              source: base[3]
            };
          })
        )
      ],
      packs: [
        { id: "pk-faithful", cnName: "", enName: "Faithful 32x", icon: "assets/mod-icons/faithful-32x.png", downloads: "2480.0 万次", loaders: ["原版材质"], mcVersions: "1.20.1 - 1.21.4", source: "CurseForge/Modrinth" },
        { id: "pk-fresh", cnName: "", enName: "Fresh Animations", icon: "assets/mod-icons/fresh-animations.webp", downloads: "1930.0 万次", loaders: ["Fabric", "NeoForge"], mcVersions: "1.20.1 - 1.21.4", source: "Modrinth" },
        ...Array.from({ length: 58 }, (_, i) => ({
          id: `gen-pack-${i}`,
          cnName: "",
          enName: `Crisp Vanilla Pack ${i + 3}`,
          icon: i % 2 === 0 ? "assets/mod-icons/faithful-32x.png" : "assets/mod-icons/fresh-animations.webp",
          downloads: `${((14.5 - i * 0.18) * 100).toFixed(1)} 万次`,
          loaders: i % 2 === 0 ? ["原版材质"] : ["Forge", "Fabric"],
          mcVersions: "1.20.1 - 1.21.4",
          source: i % 3 === 0 ? "Modrinth" : i % 3 === 1 ? "CurseForge" : "CurseForge/Modrinth"
        }))
      ],
      shaders: [
        { id: "sh-comp", cnName: "", enName: "Complementary Reimagined", icon: "assets/mod-icons/complementary-reimagined.webp", downloads: "3120.0 万次", loaders: ["Iris", "OptiFine"], mcVersions: "1.20.1 - 1.21.4", source: "CurseForge/Modrinth" },
        { id: "sh-iris", cnName: "", enName: "Photon Shader", icon: "assets/mod-icons/iris.webp", downloads: "710.0 万次", loaders: ["Iris"], mcVersions: "1.21.x", source: "Modrinth" },
        ...Array.from({ length: 58 }, (_, i) => ({
          id: `gen-sh-${i}`,
          cnName: "",
          enName: `Atmospheric Shader ${i + 3}`,
          icon: i % 2 === 0 ? "assets/mod-icons/complementary-reimagined.webp" : "assets/mod-icons/iris.webp",
          downloads: `${((12.2 - i * 0.15) * 100).toFixed(1)} 万次`,
          loaders: i % 2 === 0 ? ["Iris", "OptiFine"] : ["Iris"],
          mcVersions: "1.20.1 - 1.21.4",
          source: i % 2 === 0 ? "CurseForge/Modrinth" : "Modrinth"
        }))
      ],
      modpacks: [
        { id: "mp-fabulously", cnName: "", enName: "Fabulously Optimized", icon: "assets/mod-icons/fabulously-optimized.webp", downloads: "1150.0 万次", loaders: ["Fabric"], mcVersions: "1.21.4", source: "Modrinth" },
        { id: "mp-atm10", cnName: "", enName: "All the Mods 10", icon: "assets/mod-icons/create.webp", downloads: "690.0 万次", loaders: ["NeoForge"], mcVersions: "1.21.1", source: "CurseForge" },
        ...Array.from({ length: 58 }, (_, i) => ({
          id: `gen-mp-${i}`,
          cnName: "",
          enName: `Curated Modpack ${i + 3}`,
          icon: i % 2 === 0 ? "assets/mod-icons/fabulously-optimized.webp" : "assets/mod-icons/create.webp",
          downloads: `${((9.4 - i * 0.11) * 100).toFixed(1)} 万次`,
          loaders: i % 3 === 0 ? ["Forge", "NeoForge"] : i % 2 === 0 ? ["Fabric"] : ["Forge", "Fabric", "NeoForge"],
          mcVersions: i % 2 === 0 ? "1.21.1" : "1.20.1",
          source: i % 3 === 0 ? "Modrinth" : i % 3 === 1 ? "CurseForge" : "CurseForge/Modrinth"
        }))
      ]
    }
  };

  function trimCompletedDownloadTasks() {
    let completedSeen = 0;
    state.downloadTasks = (state.downloadTasks || []).filter((t) => {
      if (t.status !== "completed") return true;
      completedSeen += 1;
      return completedSeen <= 10;
    });
  }
  trimCompletedDownloadTasks();

  const commandsWithoutStateRefresh = new Set([
    "dismiss_download_ball", "set_feature_flags", "open_folder", "launch_game", "cancel_launch", "export_crash_report", "open_launcher_logs",
    "search_catalog", "search_catalog_page", "resolve_mod_project", "get_resource_detail_live", "get_resource_targets",
    "browse_resource_install_folder", "browse_java_install_folder", "probe_mirrors",
    "microsoft_begin", "microsoft_cancel", "open_microsoft_verification",
    "window_minimize", "window_toggle_maximize", "window_close", "window_start_drag", "window_start_resize"
  ]);

  async function invoke(cmd, payload) {
    if (isTauri) {
      return global.__TAURI__.core.invoke(cmd, payload);
    }
    if (isWebView2) {
      const res = await invokeWebView2(cmd, payload);
      if (cmd === "get_state") {
        applyBackendState(res);
      } else if (cmd === "dismiss_download_ball") {
        state.hasUnseenDownloadBall = false;
      } else if (cmd === "set_feature_flags" && res && typeof res === "object") {
        if (typeof res.bypassSystemProxyOnDownload === "boolean") state.bypassSystemProxyOnDownload = res.bypassSystemProxyOnDownload;
        if (typeof res.disableChineseResourceNames === "boolean") state.disableChineseResourceNames = res.disableChineseResourceNames;
      } else if (!commandsWithoutStateRefresh.has(cmd)) {
        try {
          const fresh = await invokeWebView2("get_state", {});
          applyBackendState(fresh);
        } catch {}
      }
      return res;
    }
    // Browser prototype fallback
    switch (cmd) {
      case "get_state":
        return structuredClone(state);
      case "get_resource_targets": {
        const detail = getResourceDetail(payload.catKey, payload.projectId);
        const choices = Object.entries(detail?.loaderTrees || {}).flatMap(([loader, groups]) =>
          groups.filter(group => group.builds.some(build => !payload.buildId || build.id === payload.buildId))
            .map(group => ({ loader, mcVersion: group.mcVersion })));
        const recent = id => {
          const index = state.recentInstanceIds.indexOf(id);
          return index < 0 ? 0 : state.recentInstanceIds.length - index;
        };
        const matches = ["VersionMismatch", "LoaderMismatch", "Compatible"];
        return state.instances.map(instance => {
          const candidates = choices.filter(choice => choice.mcVersion === instance.mcVersion);
          const rank = !candidates.length ? 0 : candidates.some(choice =>
            ["default", "Iris", "OptiFine", instance.loader].includes(choice.loader)) ? 2 : 1;
          return { ...instance, match: matches[rank] };
        }).sort((a, b) => matches.indexOf(b.match) - matches.indexOf(a.match) || recent(b.id) - recent(a.id));
      }
      case "browse_java_install_folder":
        return { token: `jdir-${Date.now().toString(36)}`, path: "D:\\MC\\DummyCrisp\\DCL\\runtime-custom" };
      case "cancel_launch":
        return { cancelled: true, instanceId: payload?.instanceId || state.activeInstanceId };
      case "browse_resource_install_folder":
        throw new Error("请在桌面启动器中选择文件夹。");
      case "import_instance_folder": {
        const suffix = state.instances.filter((i) => String(i.id).startsWith("inst-imported-")).length + 1;
        const nextId = `inst-imported-${suffix}`;
        const label = suffix === 1 ? "1.20.4 导入版本" : `1.20.4 导入版本 #${suffix}`;
        const imported = {
          id: nextId,
          name: label,
          mcVersion: "1.20.4",
          loader: "Fabric",
          loaderVersion: "0.16.7",
          javaId: "j21",
          javaVersion: "Java 21.0.5",
          jvmArgs: state.customJvmArgs || "-XX:+UseG1GC",
          iconId: "grass",
          folderColor: "emerald",
          favorite: false,
          modsCount: 12,
          lastPlayed: "刚刚导入",
          isolated: true,
          tagline: "从外部版本文件夹识别并导入"
        };
        state.instances.push(imported);
        return { importedIds: [nextId], count: 1, firstId: nextId, primaryName: label };
      }
      case "set_active_instance": {
        state.activeInstanceId = payload.id;
        if (payload && payload.promoteRecent) {
          state.recentInstanceIds = [
            payload.id,
            ...state.recentInstanceIds.filter((id) => id !== payload.id)
          ].slice(0, 6);
        }
        const inst = state.instances.find((i) => i.id === payload.id);
        if (!state._allMockMods) state._allMockMods = structuredClone(state.installedMods || []);
        if (inst) {
          if (inst.loader === "Vanilla") {
            state.installedMods = [];
          } else {
            const targetCount = Math.max(1, Math.min(state._allMockMods.length, Number(inst.modsCount) || state._allMockMods.length));
            const offset = Math.abs([...String(inst.id)].reduce((a, c) => a + c.charCodeAt(0), 0)) % Math.max(1, state._allMockMods.length);
            const rotated = [...state._allMockMods.slice(offset), ...state._allMockMods.slice(0, offset)];
            state.installedMods = rotated.slice(0, targetCount);
          }
        }
        return { activeInstanceId: state.activeInstanceId, recentInstanceIds: state.recentInstanceIds };
      }
      case "probe_mirrors":
        await probeBrowserMirrorsAndSwitch();
        return { mirrorSource: state.mirrorSource, autoMirror: state.autoMirror, mirrors: state.mirrors };
      case "set_mirror":
        if (payload && payload.auto) {
          await probeBrowserMirrorsAndSwitch();
          const best = pickOptimalMirror();
          state.mirrorSource = best.name;
          state.autoMirror = true;
        } else {
          state.mirrorSource = payload.mirror;
          state.autoMirror = false;
        }
        return { mirror: state.mirrorSource, auto: state.autoMirror };
      case "set_curseforge_route": {
        const next = ["auto", "official", "mcim"].includes(payload?.route) ? payload.route : "auto";
        state.curseForgeApiRoute = next;
        return { curseForgeApiRoute: state.curseForgeApiRoute };
      }
      case "set_bg_pattern": {
        state.bgPatternId = payload?.bgPatternId === "festive" ? "festive" : "none";
        return { bgPatternId: state.bgPatternId };
      }
      case "set_feature_flags": {
        if (typeof payload?.bypassSystemProxyOnDownload === "boolean") {
          state.bypassSystemProxyOnDownload = payload.bypassSystemProxyOnDownload;
        }
        if (typeof payload?.disableChineseResourceNames === "boolean") {
          state.disableChineseResourceNames = payload.disableChineseResourceNames;
        }
        return {
          bypassSystemProxyOnDownload: state.bypassSystemProxyOnDownload,
          disableChineseResourceNames: state.disableChineseResourceNames
        };
      }
      case "window_set_topmost": {
        state.alwaysOnTop = Boolean(payload?.pinned);
        return { alwaysOnTop: state.alwaysOnTop };
      }
      case "open_downloads_folder":
        return { ok: true, path: "Downloads" };
      case "open_external_url":
        if (payload?.url) global.open(payload.url, "_blank", "noopener,noreferrer");
        return { ok: true };
      case "toggle_mod": {
        const mod = state.installedMods.find((m) => m.id === payload.id);
        if (mod) mod.enabled = !mod.enabled;
        return mod;
      }
      case "delete_mod": {
        const idx = state.installedMods.findIndex((m) => m.id === payload.id);
        if (idx === -1) return null;
        const [removed] = state.installedMods.splice(idx, 1);
        const inst = state.instances.find((i) => i.id === state.activeInstanceId);
        if (inst && inst.modsCount > 0) inst.modsCount -= 1;
        return removed;
      }
      case "toggle_pack": {
        const pack = state.installedPacks.find((p) => p.id === payload.id);
        if (pack) pack.enabled = !pack.enabled;
        return pack;
      }
      case "set_memory":
        state.autoMemory = payload.auto;
        state.memoryMb = payload.mb;
        return { auto: state.autoMemory, mb: state.memoryMb };
      case "set_jvm_args": {
        const nextArgs = String((payload && payload.jvmArgs) || "");
        state.customJvmArgs = nextArgs;
        const activeInst = state.instances.find((i) => i.id === state.activeInstanceId);
        if (activeInst && activeInst.useGlobalSettings) activeInst.jvmArgs = nextArgs;
        return { customJvmArgs: state.customJvmArgs };
      }
      case "set_instance_icon": {
        const inst = state.instances.find((i) => i.id === payload.id);
        if (inst) {
          if (payload.iconId === "__pack__" && inst.packIcon) {
            inst.customIcon = inst.packIcon;
          } else {
            inst.iconId = payload.iconId;
            delete inst.customIcon;
          }
        }
        return inst;
      }
      case "rename_instance": {
        const inst = state.instances.find((i) => i.id === payload.id);
        const nextName = String((payload && payload.name) || "").trim().slice(0, 80);
        if (!inst || !nextName) return null;
        inst.name = nextName;
        return inst;
      }
      case "switch_account": {
        const acc = (state.accounts || []).find((a) => a.id === payload.id);
        if (!acc) return null;
        state.account = {
          loggedIn: true,
          id: acc.id,
          username: acc.username,
          uuid: acc.uuid,
          type: acc.type,
          avatar: acc.avatar,
          hasCustomSkin: Boolean(acc.hasCustomSkin),
          skinModel: acc.skinModel || "default",
          skinUrl: acc.skinUrl || ""
        };
        return state.account;
      }
      case "import_offline_skin": {
        const acc = (state.accounts || []).find((a) => a.id === payload.id);
        if (!acc) return null;
        if (payload?.avatarDataUrl) acc.avatar = payload.avatarDataUrl;
        if (payload?.skinDataUrl) acc.skinUrl = payload.skinDataUrl;
        acc.hasCustomSkin = true;
        acc.skinModel = payload?.skinModel || "default";
        if (state.account && state.account.id === acc.id) {
          state.account.avatar = acc.avatar;
          state.account.skinUrl = acc.skinUrl || "";
          state.account.hasCustomSkin = true;
          state.account.skinModel = acc.skinModel;
        }
        return acc;
      }
      case "add_account": {
        const name = String((payload && payload.username) || "").trim() || `Player_${(state.accounts || []).length + 1}`;
        const acc = {
          id: `acc-${Date.now().toString(36)}`,
          username: name,
          uuid: `${Date.now().toString(16).slice(-8)}-4b12-8a90-crisp${String(Math.floor(Math.random() * 9999)).padStart(4, "0")}`,
          type: (payload && payload.type) || "Microsoft 正版",
          avatar: (payload && payload.avatar) || "assets/mc-icons/player-head.png"
        };
        state.accounts = [acc, ...(state.accounts || [])];
        state.account = {
          loggedIn: true,
          id: acc.id,
          username: acc.username,
          uuid: acc.uuid,
          type: acc.type,
          avatar: acc.avatar
        };
        return acc;
      }
      case "delete_account": {
        const list = state.accounts || [];
        const idx = list.findIndex((a) => a.id === payload.id);
        if (idx === -1) return null;
        const [removed] = list.splice(idx, 1);
        if (list.length > 0) {
          const next = list[0];
          state.account = {
            loggedIn: true,
            id: next.id,
            username: next.username,
            uuid: next.uuid,
            type: next.type,
            avatar: next.avatar
          };
        } else {
          state.account = {
            loggedIn: false,
            id: "",
            username: "",
            uuid: "-",
            type: "未登录",
            avatar: ""
          };
        }
        return removed;
      }
      case "dismiss_download_ball":
        state.hasUnseenDownloadBall = false;
        return { ok: true };
      case "set_instance_color": {
        const inst = state.instances.find((i) => i.id === payload.id);
        if (inst) {
          inst.folderColor = payload.colorId || "amber";
          if (payload.customHex) {
            inst.customFolderHex = payload.customHex;
          }
        }
        return inst;
      }
      case "toggle_favorite_instance": {
        const inst = state.instances.find((i) => i.id === payload.id);
        if (inst) {
          inst.favorite = !inst.favorite;
        }
        return inst;
      }
      case "delete_instance": {
        const idx = state.instances.findIndex((i) => i.id === payload.id);
        if (idx === -1 || state.instances.length <= 1) return null;
        const [removed] = state.instances.splice(idx, 1);
        state.recentInstanceIds = state.recentInstanceIds.filter((id) => id !== payload.id);
        if (state.activeInstanceId === payload.id && state.instances[0]) {
          state.activeInstanceId = state.instances[0].id;
        }
        return removed;
      }
      case "update_instance_settings": {
        const inst = state.instances.find((i) => i.id === payload.id);
        if (!inst) return null;
        if (payload.useGlobalSettings === true) {
          const activeJava = state.javas.find((item) => item.active) || state.javas[0];
          inst.useGlobalSettings = true;
          inst.autoMemory = Boolean(state.autoMemory);
          inst.memoryMb = state.memoryMb;
          inst.jvmArgs = state.customJvmArgs;
          if (activeJava) {
            inst.javaId = activeJava.id;
            inst.javaVersion = activeJava.version.startsWith("1.8") ? "Java 8u422" : `Java ${activeJava.version}`;
          }
          return inst;
        }
        if (payload.useGlobalSettings === false) {
          inst.useGlobalSettings = false;
        }
        if (typeof payload.isolated === "boolean") inst.isolated = payload.isolated;
        if (typeof payload.jvmArgs === "string") {
          inst.jvmArgs = payload.jvmArgs;
          inst.useGlobalSettings = false;
        }
        if (typeof payload.autoMemory === "boolean") {
          inst.autoMemory = payload.autoMemory;
          inst.useGlobalSettings = false;
          if (inst.autoMemory) {
            const modsCnt = Number(inst.modsCount) || 0;
            inst.memoryMb = inst.isModpack || modsCnt >= 100 ? 6144 : modsCnt >= 20 || inst.loader !== "Vanilla" ? 4096 : 3072;
          }
        }
        if (typeof payload.memoryMb === "number" && !Number.isNaN(payload.memoryMb)) {
          inst.memoryMb = payload.memoryMb;
          inst.autoMemory = false;
          inst.useGlobalSettings = false;
        }
        if (payload.javaId) {
          inst.javaId = payload.javaId;
          inst.useGlobalSettings = false;
          const j = state.javas.find((item) => item.id === payload.javaId);
          if (j) inst.javaVersion = j.version.startsWith("1.8") ? "Java 8u422" : `Java ${j.version}`;
        }
        return inst;
      }
      case "set_active_java": {
        let selected = null;
        state.javas.forEach((j) => {
          j.active = j.id === payload.id;
          if (j.active) selected = j;
        });
        return selected;
      }
      case "scan_local_javas": {
        const discoveredPool = [
          {
            id: "j25-scan",
            version: "25.0.0",
            vendor: "Oracle JDK 25",
            arch: "x64",
            path: "C:\\Program Files\\Java\\jdk-25\\bin\\javaw.exe",
            active: false
          },
          {
            id: "j11-scan",
            version: "11.0.26",
            vendor: "Microsoft OpenJDK",
            arch: "x64",
            path: "C:\\Program Files\\Microsoft\\jdk-11.0.26\\bin\\javaw.exe",
            active: false
          }
        ];
        let added = 0;
        discoveredPool.forEach((cand) => {
          if (!state.javas.some((j) => j.id === cand.id || j.path === cand.path)) {
            state.javas.push(cand);
            added += 1;
          }
        });
        return { added, javas: state.javas };
      }
      case "install_java_official": {
        const off = (state.officialJavas || []).find((o) => o.id === payload.id) || (state.officialJavas || [])[1];
        if (!off) return null;
        const vendorMap = {
          temurin: "Eclipse Temurin",
          oracle: "Oracle JDK",
          zulu: "Azul Zulu",
          microsoft: "Microsoft OpenJDK"
        };
        const chosenVendor = vendorMap[String(payload?.vendor || "temurin").toLowerCase()] || off.vendor;
        const targetDir = payload?.installPath || `C:\\Program Files\\Java\\jdk-${off.version}`;
        const task = {
          id: `dl-java-${Date.now().toString(36)}`,
          name: `Java ${off.fullVersion} (${chosenVendor})`,
          sub: `${chosenVendor} · ${targetDir}`,
          icon: "assets/mc-icons/command.png",
          source: chosenVendor,
          size: off.size,
          speed: "18.6 MB/s",
          progress: 34,
          status: "downloading"
        };
        state.downloadTasks.unshift(task);
        trimCompletedDownloadTasks();
        state.hasUnseenDownloadBall = true;
        const verKey = off.version === "8" ? "1.8.0_442" : `${off.version}.0.${off.version === "21" ? "6" : "0"}`;
        if (!state.javas.some((j) => (j.version.startsWith(`${off.version}.`) || (off.version === "8" && j.version.startsWith("1.8"))) && j.vendor.includes(chosenVendor))) {
          state.javas.push({
            id: `j${off.version}-${payload?.vendor || "tem"}`,
            version: verKey,
            vendor: chosenVendor,
            arch: "x64",
            path: `${targetDir}\\bin\\javaw.exe`,
            active: false
          });
        }
        return { task, official: Object.assign({}, off, { vendor: chosenVendor }), javas: state.javas };
      }
      case "open_folder": {
        const inst = state.instances.find((i) => i.id === payload.id) || state.instances[0];
        const base = inst.isolated
          ? `D:\\MC\\DummyCrisp\\.minecraft\\versions\\${inst.name}`
          : "D:\\MC\\DummyCrisp\\.minecraft";
        const target = payload.subpath ? `${base}\\${payload.subpath}` : base;
        return { ok: true, path: target };
      }
      case "add_download_task": {
        const p = payload || {};
        const task = {
          id: `dl-${Date.now().toString(36)}-${Math.floor(Math.random() * 1000)}`,
          name: p.name || "未命名资源",
          sub: p.sub || "resource-package.jar",
          icon: p.icon || "assets/mod-icons/sodium.webp",
          source: state.mirrorSource,
          size: p.size || "2.4 MB",
          speed: "14.2 MB/s",
          progress: 26,
          status: "downloading"
        };
        state.downloadTasks.unshift(task);
        trimCompletedDownloadTasks();
        state.hasUnseenDownloadBall = true;
        if (p.installType === "version" && p.mcVersion) {
          const loaderLabel = Array.isArray(p.loaders) && p.loaders.length > 0 ? p.loaders.join(" + ") : "Vanilla";
          const newId = `inst-dl-${p.mcVersion}-${Date.now().toString(36)}`;
          state.instances.unshift({
            id: newId,
            name: p.instanceName || `${p.mcVersion}${loaderLabel !== "Vanilla" ? ` ${loaderLabel}` : ""}`,
            mcVersion: p.mcVersion,
            loader: Array.isArray(p.loaders) && p.loaders.length > 0 ? p.loaders[0] : "Vanilla",
            loaderVersion: p.loaderVersion || (loaderLabel === "Vanilla" ? "原版" : "推荐构建"),
            javaId: "j21",
            javaVersion: "Java 21.0.5",
            jvmArgs: "-XX:+UseZGC -XX:+ZGenerational",
            iconId: "grass",
            favorite: false,
            modsCount: 0,
            lastPlayed: "刚刚安装",
            isolated: true,
            tagline: `Minecraft ${p.mcVersion} 游戏实例`
          });
        } else if (p.installType === "modpack") {
          const newId = `inst-mp-${Date.now().toString(36)}`;
          state.instances.unshift({
            id: newId,
            name: p.name || "已下载整合包",
            mcVersion: p.mcVersion || "1.21.1",
            loader: p.loader || "NeoForge",
            loaderVersion: "整合包内置",
            javaId: "j21",
            javaVersion: "Java 21.0.5",
            jvmArgs: "-XX:+UseZGC -XX:+ZGenerational",
            iconId: "grass",
            customIcon: p.icon || "assets/mod-icons/create.webp",
            isModpack: true,
            favorite: false,
            modsCount: 120,
            lastPlayed: "刚刚安装",
            isolated: true,
            tagline: "来自资源库的整合包实例"
          });
        }
        return task;
      }
      case "toggle_download_task": {
        const t = state.downloadTasks.find((item) => item.id === payload.id);
        if (!t || t.status === "completed") return t;
        t.status = t.status === "downloading" ? "paused" : "downloading";
        t.speed = t.status === "downloading" ? "16.4 MB/s" : "已暂停";
        return t;
      }
      case "delete_download_task": {
        const idx = state.downloadTasks.findIndex((item) => item.id === payload.id);
        if (idx === -1) return null;
        const [removed] = state.downloadTasks.splice(idx, 1);
        return removed;
      }
      case "clear_completed_downloads": {
        state.downloadTasks = state.downloadTasks.filter((t) => t.status !== "completed");
        return state.downloadTasks;
      }
      case "complete_download_task": {
        const t = state.downloadTasks.find((item) => item.id === payload.id);
        if (t) {
          t.progress = 100;
          t.status = "completed";
          t.speed = t.size;
        }
        trimCompletedDownloadTasks();
        return state.downloadTasks;
      }
      default:
        return { ok: true, cmd, payload };
    }
  }

  const LOADER_OFFICIAL_ICONS = {
    Forge: "assets/mc-icons/loader-forge.png",
    NeoForge: "assets/mc-icons/loader-neoforge.png",
    Fabric: "assets/mc-icons/loader-fabric.png",
    OptiFine: "assets/mc-icons/loader-optifine.png"
  };

  const MC_OFFICIAL_SUMMARIES = {
    "1.21.4": "「花园觉醒」主题更新：新增苍白之园生物群系、苍白橡木方块族与夜晚苏醒的嘎枝（Creaking），同时加入树脂砖及 eyeblossom 等生态细节。",
    "24w45a": "「花园觉醒」周期快照：调整苍白橡木林生成密度与嘎枝之心掉落机制，优化树叶粒子与眼花昼夜闭合表现。",
    "1.21.3": "修复「勇敢之捆」更新中鲑鱼尺寸异常与潜影盒染色问题，改善多人联机实体同步表现。",
    "1.21.1": "维护性更新：修复多项联机崩溃、配方书显示问题并完善官方多语言文本。",
    "1.21": "「棘巧试炼」大型更新：加入试炼大厅结构、旋风人与沼骸等新敌对生物、铜与凝灰岩建筑家族、重锤武器及自动合成器方块。",
    "1.20.6": "紧急修复携带狼铠时的物品复制问题，保障驯服狼披甲冒险的稳定性。",
    "1.20.4": "修复饰纹陶罐物品交互细节，包含蝙蝠新模型与重制后的合成器材质。",
    "1.20.1": "「足迹与故事」更新：带来樱花树林生物群系、考古刷扫系统、嗅探兽与骆驼、竹木与樱花木方块组及可悬挂告示牌。",
    "1.19.4": "骑乘手感优化、盔甲纹饰实验性预览及全新造物主主界面全景图。",
    "1.19.2": "修复「荒野更新」深暗之域与监守者音效同步缺陷。",
    "1.18.2": "完善「洞穴与山崖：第二部分」地形生成，增加自定义世界维度与标签拓展。",
    "1.16.5": "「下界更新」长期经典版本，重塑下界生物群系、下界合金装备与猪灵交易系统。",
    "1.12.2": "「多彩世界更新」经典版本，引入混凝土、带釉陶瓦、配方书与进度系统。",
    "24w14potato": "2024 愚人节特别更新「毒马铃薯更新」：以马铃薯为主题打造的荒诞维度、马铃薯生物群系与奇趣合成物品。",
    "23w13a_or_b": "2023 愚人节特别更新「投票更新」：每隔数分钟由全服投票决定一条疯狂的新游戏规则，甚至能够建造通往月球的方块。",
    "20w14infinite": "2020 愚人节特别更新「无限维度」：向地下界传送门投入任意写字成书，即可开启超过二十亿个随机奇异维度。",
    "15w14a": "2015 愚人节特别更新「爱与拥抱」：用爱心取代生命值，苦力怕会微笑并赠送玫瑰，主打和平与善意。",
    "b1.7.3": "早期 Beta 经典快照版，加入活塞、粘性活塞与剪刀，是红石机械与空岛地图的启蒙时代。",
    "b1.6.6": "加入活板门、高草丛与下界多人联机支持，开启早期多人建造时代。",
    "a1.2.6": "「万圣节更新」初期版本，首次引入下界维度、萤石与灵魂沙。",
    "c0.30_01c": "早期 Classic 创意生存纪念版本，保留纯粹的像素放置与地形破坏体验。",
    "rd-132211": "Minecraft 最早可公开游玩的雏形「Cave Game」，仅有圆石与草方块和基础建造功能。"
  };

  const CURATED_DETAIL_META = {
    "mr-sodium": {
      author: "jellysquid3 / IMS212",
      summary: "大幅提升游戏帧率与区块加载速度的性能优化模组，在保持原版画风的前提下消除明显掉帧与卡顿。",
      slug: "sodium",
      mcmodId: "2425",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.6", "1.20.4", "1.20.1"]
    },
    "mr-iris": {
      author: "coderbot / IMS212",
      summary: "高帧率光影加载模组，让你在畅享高帧数流畅画面的同时开启各类光影水反、动态云层与体积光效果。",
      slug: "iris",
      mcmodId: "3378",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.6", "1.20.1"]
    },
    "mr-create": {
      author: "simibubi",
      summary: "以齿轮、风车、水车和蒸汽火车为核心的机械建造模组，通过直观的旋转动力组合出能动起来的立体工坊与自动化产线。",
      slug: "create",
      mcmodId: "2021",
      mcVersionsList: ["1.21.1", "1.20.1", "1.19.2", "1.18.2"]
    },
    "cf-jei": {
      author: "mezz",
      summary: "在背包旁展示全物品索引与合成配方查询栏，随时点击查看工作台、熔炉与各类模组机器的合成公式。",
      slug: "jei",
      mcmodId: "459",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.4", "1.20.1", "1.12.2"]
    },
    "cf-ferrite": {
      author: "malte0811",
      summary: "减少游戏运行时的内存占用，帮助大型整合包在有限内存环境下保持流畅且更少出现内存不足闪退。",
      slug: "ferrite-core",
      mcmodId: "3865",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.6", "1.20.1"]
    },
    "mr-lithium": {
      author: "jellysquid3",
      summary: "全面优化游戏内生物 AI、红石与物理运算效率，在不改动原版任何特性与生电机制的情况下显著提升流畅度。",
      slug: "lithium",
      mcmodId: "2426",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.6", "1.20.1"]
    },
    "xaero-map": {
      author: "xaero96",
      summary: "融入原版界面风格的实时右上角小地图，支持雷达生物显示、自定义路径点标记与死亡点自动记录。",
      slug: "xaeros-minimap",
      mcmodId: "959",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.4", "1.20.1", "1.12.2"]
    },
    "pk-faithful": {
      author: "Faithful Team",
      summary: "将原版 16×16 方块纹理忠实重绘为细腻的 32×32 高清版本，保留原汁原味的同时让每一块砖木更加精致。",
      slug: "faithful-32x",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.4", "1.20.1"]
    },
    "pk-fresh": {
      author: "FreshLX",
      summary: "为村民、僵尸、狐狸、苦力怕等原版生物带来生动的行走摆臂、眨眼与表情动画，让方块世界充满活力。",
      slug: "fresh-animations",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.1"]
    },
    "sh-comp": {
      author: "EminGT",
      summary: "保留 Minecraft 独特像素审美的高画质光影包，提供柔和体积云、通透水面反射与温暖的火把光晕。",
      slug: "complementary-reimagined",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.1"]
    },
    "sh-iris": {
      author: "sixthsurge",
      summary: "注重真实大气散射与自然色彩表现的高性能光影包，兼具惊艳日出日落天色与流畅帧数。",
      slug: "photon-shader",
      mcVersionsList: ["1.21.4", "1.21.1"]
    },
    "mp-fabulously": {
      author: "robotkoer",
      summary: "精选轻量性能优化、光影支持、动态光源与缩放等实用功能的高帧率原版增强整合包。",
      slug: "fabulously-optimized",
      mcVersionsList: ["1.21.4", "1.21.1", "1.20.4", "1.20.1"]
    },
    "mp-atm10": {
      author: "ATM Team",
      summary: "汇集百余款热门科技、魔法与探索冒险模组的大型综合整合包，内置完整任务引导与终极合成目标。",
      slug: "all-the-mods-10",
      mcVersionsList: ["1.21.1"]
    }
  };

  const liveDetailCache = new Map();
  const onlineCatalogCache = new Map();

  function formatDownloadCountCn(n) {
    const num = Number(n) || 0;
    if (num >= 10000) return `${(num / 10000).toFixed(1)} 万次`;
    return `${num} 次`;
  }

  function formatByteSize(bytes) {
    const b = Number(bytes) || 0;
    if (b >= 1048576) return `${(b / 1048576).toFixed(1)} MB`;
    if (b >= 1024) return `${Math.max(1, Math.round(b / 1024))} KB`;
    return `${b} B`;
  }

  function formatRelativeTimeCn(isoStr) {
    if (!isoStr) return "近期";
    const dt = Date.parse(isoStr);
    if (!Number.isFinite(dt)) return "近期";
    const days = Math.max(0, Math.floor((Date.now() - dt) / 86400000));
    if (days < 1) return "今天";
    if (days < 30) return `${days} 天前`;
    if (days < 365) return `${Math.max(1, Math.floor(days / 30))} 个月前`;
    return `${Math.max(1, Math.floor(days / 365))} 年前`;
  }

  function mapCatKeyToModrinthType(catKey) {
    if (catKey === "packs") return "resourcepack";
    if (catKey === "shaders") return "shader";
    if (catKey === "modpacks") return "modpack";
    return "mod";
  }

  let modNames = [];

  function pinyinKey(value) {
    return String(value || "").normalize("NFD").toLowerCase().replace(/[^a-z]/g, "").replaceAll("v", "u");
  }

  function pinyinMatchRank(query, reading) {
    const term = String(query || "");
    if (/[\u3400-\u9fff06-9]/.test(term)) return -1;
    const normalized = pinyinKey(term);
    if (normalized.length < 2) return -1;
    const full = pinyinKey(reading);
    const initials = pinyinKey(String(reading || "").trim().split(/\s+/).map(word => word[0] || "").join(""));
    if (full === normalized || initials === normalized) return 0;
    return normalized.length >= 3 && full.includes(normalized) ? 1 : -1;
  }

  function namePinyinMatches(query, name, reading) {
    const known = reading || modNames.find(item => item.name === name)?.pinyin;
    return Boolean(known) && pinyinMatchRank(query, known) >= 0;
  }

  function localizedNames(category, query) {
    const term = String(query || "").trim();
    if (!term) return [];
    const han = /[\u3400-\u9fff]/.test(term);
    return modNames.filter(item => (item.category || "mods") === category)
      .map(item => ({
        item,
        rank: han ? item.name === term ? 0 : item.name.includes(term) ? 1 : -1 : pinyinMatchRank(term, item.pinyin)
      }))
      .filter(match => match.rank >= 0)
      .sort((a, b) => a.rank - b.rank || a.item.name.length - b.item.name.length)
      .map(match => match.item);
  }

  function chineseName(category, slug) {
    const id = String(slug || "").toLowerCase().replaceAll("_", "-");
    return modNames.find(item => (item.category || "mods") === category && item.ids.includes(id))?.name || "";
  }

  function normalizeModrinthHit(catKey, hit) {
    const pid = hit.project_id || hit.id || hit.slug || "";
    const categories = hit.loaders?.length ? hit.loaders : hit.display_categories?.length ? hit.display_categories : hit.categories || [];
    const loaderSet = [];
    ["fabric", "neoforge", "forge", "quilt", "iris", "optifine", "minecraft"].forEach((k) => {
      if (categories.includes(k)) {
        loaderSet.push(
          k === "neoforge" ? "NeoForge"
            : k === "optifine" ? "OptiFine"
              : k === "minecraft" ? "Vanilla"
                : k.charAt(0).toUpperCase() + k.slice(1)
        );
      }
    });
    if (loaderSet.length === 0) loaderSet.push(catKey === "mods" ? "Fabric" : "Vanilla");
    const vers = hit.game_versions || hit.versions || [];
    const mcVerText = vers.length >= 2 ? `${vers[vers.length - 1]} - ${vers[0]}` : (vers[0] || "1.21.4");
    return {
      id: `mr-${pid}`,
      projectId: pid,
      slug: hit.slug || pid,
      cnName: chineseName(catKey, hit.slug),
      enName: hit.title || hit.slug || "未命名资源",
      author: hit.author || "Modrinth",
      downloads: formatDownloadCountCn(hit.downloads),
      mcVersions: mcVerText,
      summary: hit.description || "",
      loaders: loaderSet,
      source: "Modrinth",
      icon: hit.icon_url || "assets/mod-icons/sodium.webp",
      webUrl: `https://modrinth.com/${mapCatKeyToModrinthType(catKey)}/${hit.slug || pid}`
    };
  }

  function normalizeModrinthVersionsToTrees(catKey, projectId, itemMeta, versions, depProjectsById = {}) {
    const trees = {};
    const depRefsByGroup = {};
    const hideLoaders = catKey === "packs" || catKey === "shaders";
    const capLoader = (l) => {
      const low = String(l || "").toLowerCase();
      if (low === "neoforge") return "NeoForge";
      if (low === "optifine") return "OptiFine";
      if (low === "fabric") return "Fabric";
      if (low === "forge") return "Forge";
      if (low === "iris") return "Iris";
      if (low === "minecraft") return "Vanilla";
      return l ? l.charAt(0).toUpperCase() + l.slice(1) : "Vanilla";
    };

    const sorted = [...(versions || [])].sort((a, b) => Date.parse(b.date_published || 0) - Date.parse(a.date_published || 0));
    sorted.forEach((v) => {
      const loaders = hideLoaders ? ["default"] : (v.loaders || []).map(capLoader);
      const gameVers = v.game_versions || [];
      const primaryFile = (v.files || []).find((f) => f.primary) || (v.files || [])[0] || {};
      const verTypeRaw = String(v.version_type || "release").toLowerCase();
      const releaseType = verTypeRaw === "alpha" ? "Alpha" : verTypeRaw === "beta" ? "Beta" : "Release";
      const deps = (v.dependencies || []).filter((d) => d && d.project_id && (d.dependency_type === "required" || d.dependency_type === "optional"));

      loaders.forEach((ld) => {
        if (!trees[ld]) trees[ld] = {};
        if (!depRefsByGroup[ld]) depRefsByGroup[ld] = {};
        gameVers.forEach((mcVer) => {
          if (!trees[ld][mcVer]) trees[ld][mcVer] = [];
          if (!depRefsByGroup[ld][mcVer]) depRefsByGroup[ld][mcVer] = new Map();
          deps.forEach((d) => {
            const existing = depRefsByGroup[ld][mcVer].get(d.project_id);
            if (!existing || (existing !== "required" && d.dependency_type === "required")) {
              depRefsByGroup[ld][mcVer].set(d.project_id, d.dependency_type);
            }
          });
          trees[ld][mcVer].push({
            id: v.id || `${projectId}-${ld}-${mcVer}-${v.version_number}`,
            title: v.name || v.version_number || primaryFile.filename || "Build",
            versionNumber: v.version_number || "",
            icon: itemMeta.icon || "assets/mod-icons/sodium.webp",
            file: primaryFile.filename || `${itemMeta.slug || projectId}-${v.version_number || "1.0"}.jar`,
            fileUrl: primaryFile.url || "",
            sha1: (primaryFile.hashes && primaryFile.hashes.sha1) || "",
            downloads: formatDownloadCountCn(v.downloads || 0),
            updatedAgo: formatRelativeTimeCn(v.date_published),
            size: formatByteSize(primaryFile.size || 2400000),
            releaseType,
            depCount: deps.length
          });
        });
      });
    });

    const loaderTrees = {};
    Object.keys(trees).forEach((ld) => {
      const mcKeys = Object.keys(trees[ld]).sort((a, b) => b.localeCompare(a, undefined, { numeric: true }));
      loaderTrees[ld] = mcKeys.map((mcVer) => {
        const groupDeps = [];
        const depMap = depRefsByGroup[ld]?.[mcVer];
        if (depMap) {
          depMap.forEach((depType, pid) => {
            const meta = depProjectsById[pid];
            if (meta) {
              groupDeps.push({
                ...meta,
                depType,
                label: depType === "required" ? "前置支持库" : "可选前置"
              });
            }
          });
          groupDeps.sort((a, b) => (a.depType === "required" ? 0 : 1) - (b.depType === "required" ? 0 : 1));
        }
        return {
          mcVersion: mcVer,
          title: hideLoaders ? `Minecraft ${mcVer}` : `${ld} · Minecraft ${mcVer}`,
          dependencies: groupDeps,
          builds: trees[ld][mcVer]
        };
      });
    });
    const loaderKeys = Object.keys(loaderTrees);
    const modrinthUrl = itemMeta.webUrl || `https://modrinth.com/${mapCatKeyToModrinthType(catKey)}/${itemMeta.slug || projectId}`;
    return {
      catKey,
      id: itemMeta.id || `mr-${projectId}`,
      projectId,
      name: itemMeta.enName || itemMeta.name || projectId,
      enName: itemMeta.enName || itemMeta.name || projectId,
      cnName: itemMeta.cnName || "",
      hideLoaders,
      author: itemMeta.author || "Modrinth",
      downloads: itemMeta.downloads || "",
      summary: itemMeta.summary || "",
      icon: itemMeta.icon || "assets/mod-icons/sodium.webp",
      source: "Modrinth",
      webUrl: modrinthUrl,
      links: Object.assign({ modrinth: modrinthUrl }, itemMeta.links || {}),
      loaders: loaderKeys.length ? loaderKeys : (itemMeta.loaders || ["Fabric"]),
      loaderTrees
    };
  }

  const catalogPages = new Map();
  const pendingCatalogPages = new Map();

  async function fetchLiveCatalogPage(catKey, query, loader, page, pageSize, source, explicitOffset, sortBy, mcVersion, tag, subTag) {
    const offVal = typeof explicitOffset === "number" && explicitOffset >= 0 ? explicitOffset : -1;
    const sortVal = sortBy || "downloads";
    const mcVal = mcVersion || "all";
    const tagVal = tag || "all";
    const subTagVal = subTag || "all";
    const key = JSON.stringify([catKey, query?.trim() || "", loader || "all", page, pageSize, source || "all", offVal, sortVal, mcVal, tagVal, subTagVal]);
    const cached = catalogPages.get(key);
    if (cached?.expires > Date.now()) {
      catalogPages.delete(key);
      catalogPages.set(key, cached);
      rememberCatalogItems(catKey, cached.result.items);
      return cached.result;
    }
    if (pendingCatalogPages.has(key)) return pendingCatalogPages.get(key);

    const task = requestCatalogPage(catKey, query, loader, page, pageSize, source, offVal, sortVal, mcVal, tagVal, subTagVal);
    pendingCatalogPages.set(key, task);
    try {
      const result = await task;
      if (result) {
        if (catalogPages.size >= 48) catalogPages.delete(catalogPages.keys().next().value);
        catalogPages.set(key, { result, expires: Date.now() + 180000 });
        rememberCatalogItems(catKey, result.items);
      }
      return result;
    } finally {
      pendingCatalogPages.delete(key);
    }
  }

  function rememberCatalogItems(catKey, items) {
    items.forEach(item => onlineCatalogCache.set(`${catKey}:${item.id}`, item));
    while (onlineCatalogCache.size > 1000) onlineCatalogCache.delete(onlineCatalogCache.keys().next().value);
  }

  async function requestCatalogPage(catKey, query, loader, page, pageSize, source, explicitOffset, sortBy, mcVersion, tag, subTag) {
    const p = Math.max(1, Number(page) || 1);
    const size = Math.max(10, Number(pageSize) || 50);
    const offset = explicitOffset >= 0 ? explicitOffset : (p - 1) * size;
    const sortVal = sortBy || "downloads";
    const mcVal = !mcVersion || mcVersion === "all" ? "" : String(mcVersion).trim();
    const tagVal = !tag || tag === "all" ? "" : String(tag).trim();
    const subTagVal = !subTag || subTag === "all" ? "" : String(subTag).trim();
    const categoryLoader = catKey === "packs" || catKey === "shaders" ? "all" : loader;
    if (isWebView2) {
      const res = await invoke("search_catalog_page", {
        catKey, query: query || "", loader: categoryLoader || "all", page: p, pageSize: size, offset, source, sortBy: sortVal, mcVersion: mcVal || "all", tag: tagVal || "all", subTag: subTagVal || "all"
      });
      return res;
    }
    if (source === "CurseForge") return null;
    if (catKey === "versions") return null;
    try {
      const aliases = localizedNames(catKey, query);
      if (aliases.length) {
        const ids = [...new Set(aliases.flatMap(item => item.ids))];
        const type = mapCatKeyToModrinthType(catKey);
        const response = await fetch(`https://api.modrinth.com/v2/projects?ids=${encodeURIComponent(JSON.stringify(ids))}`);
        if (!response.ok) return null;
        const projects = await response.json();
        const bySlug = new Map(projects.filter(item => item.project_type === type &&
          ["approved", "archived"].includes(item.status) &&
          (!mcVal || (item.game_versions || []).includes(mcVal)) &&
          (!categoryLoader || categoryLoader === "all" || (item.loaders || []).includes(categoryLoader.toLowerCase())))
          .map(item => [item.slug, { item: normalizeModrinthHit(catKey, item), published: Date.parse(item.published || 0) }]));
        let matches = ids.map(id => bySlug.get(id)).filter(Boolean);
        if (sortVal === "newest") matches.sort((a, b) => b.published - a.published);
        const items = [...new Map(matches.map(m => [m.item.id, m.item])).values()];
        return { items: items.slice(offset, offset + size), totalHits: items.length, page: p, pageSize: size, offset };
      }
      const facets = [[`project_type:${mapCatKeyToModrinthType(catKey)}`]];
      if (categoryLoader && categoryLoader !== "all") facets.push([`categories:${categoryLoader.toLowerCase()}`]);
      if (tagVal) facets.push([`categories:${tagVal.toLowerCase()}`]);
      if (subTagVal && subTagVal.toLowerCase() !== tagVal.toLowerCase()) facets.push([`categories:${subTagVal.toLowerCase()}`]);
      if (mcVal) facets.push([`versions:${mcVal}`]);
      const index = sortVal === "newest" ? "newest" : "downloads";
      const url = `https://api.modrinth.com/v2/search?limit=${size}&offset=${offset}&index=${index}&query=${encodeURIComponent(query?.trim() || "")}&facets=${encodeURIComponent(JSON.stringify(facets))}`;
      const resp = await fetch(url);
      if (!resp.ok) return null;
      const data = await resp.json();
      const items = (data.hits || []).map(hit => normalizeModrinthHit(catKey, hit));
      return { items, totalHits: Number(data.total_hits) || items.length, page: p, pageSize: size, offset };
    } catch {
      return null;
    }
  }

  const liveDetailRequests = new Map();

  function getCachedResourceDetail(catKey, itemId) {
    return liveDetailCache.get(`${catKey}:${itemId}`);
  }

  function buildMcWikiUrl(versionId) {
    const raw = String(versionId || "").trim();
    let page = `Java版${raw}`;
    if (/^b\d/i.test(raw)) page = `Java版Beta_${raw.slice(1)}`;
    else if (/^a\d/i.test(raw)) page = `Java版Alpha_v${raw.slice(1)}`;
    else if (/^c\d/i.test(raw)) page = `Java版Classic_${raw.slice(1)}`;
    else if (/^inf-/i.test(raw)) page = `Java版Infdev_${raw.slice(4)}`;
    else if (/^rd-/i.test(raw)) page = `Java版Pre-Classic_${raw}`;
    return `https://zh.minecraft.wiki/w/${encodeURIComponent(page)}`;
  }

  function getResourcePreview(catKey, itemId) {
    const raw = onlineCatalogCache.get(`${catKey}:${itemId}`) ||
      (state.catalog[catKey] || []).find(item => String(item.id) === String(itemId));
    const webUrl = raw?.webUrl || "";
    const links = catKey === "versions"
      ? { mcwiki: buildMcWikiUrl(itemId) }
      : webUrl && raw?.source === "CurseForge" ? { curseforge: webUrl }
        : webUrl && raw?.source === "Modrinth" ? { modrinth: webUrl } : {};
    return {
      ...raw,
      catKey,
      id: itemId,
      enName: raw?.enName || (catKey === "versions" ? `Minecraft ${itemId}` : "资源详情"),
      icon: raw?.icon || "assets/mc-icons/cobblestone.png",
      hideLoaders: catKey === "shaders" || catKey === "packs",
      loaders: raw?.loaders || [],
      loaderTrees: {},
      links
    };
  }

  async function fetchLiveResourceDetail(catKey, itemId) {
    const cacheKey = `${catKey}:${itemId}`;
    const cached = getCachedResourceDetail(catKey, itemId);
    if (cached) return cached;
    const pending = liveDetailRequests.get(cacheKey);
    if (pending) return pending;
    const request = requestLiveResourceDetail(catKey, itemId, cacheKey);
    liveDetailRequests.set(cacheKey, request);
    try {
      return await request;
    } finally {
      liveDetailRequests.delete(cacheKey);
    }
  }

  async function requestLiveResourceDetail(catKey, itemId, cacheKey) {
    const raw = onlineCatalogCache.get(cacheKey) ||
      (state.catalog[catKey] || []).find((it) => String(it.id) === String(itemId));

    if (isWebView2) {
      const live = await invoke("get_resource_detail_live", { catKey, id: itemId });
      if (!live || !live.loaderTrees) return null;
      const detail = {
        ...live,
        id: itemId,
        catKey,
        enName: live.enName || raw?.enName || "未命名资源",
        cnName: live.cnName || raw?.cnName || "",
        author: live.author || raw?.author || ""
      };
      cacheDetail(cacheKey, detail);
      return detail;
    }
    if (catKey !== "versions") {
      try {
        const pid = raw?.projectId || raw?.slug || CURATED_DETAIL_META[itemId]?.slug || String(itemId).replace(/^mr-/, "");
        const [projResp, verResp] = await Promise.all([
          fetch(`https://api.modrinth.com/v2/project/${encodeURIComponent(pid)}`),
          fetch(`https://api.modrinth.com/v2/project/${encodeURIComponent(pid)}/version`)
        ]);
        if (projResp.ok && verResp.ok) {
          const proj = await projResp.json();
          const vers = await verResp.json();
          const depIds = [...new Set(vers.flatMap((v) => (v.dependencies || []).map((d) => d?.project_id).filter(Boolean)))].slice(0, 60);
          const depProjectsById = {};
          if (depIds.length > 0) {
            try {
              const depResp = await fetch(`https://api.modrinth.com/v2/projects?ids=${encodeURIComponent(JSON.stringify(depIds))}`);
              if (depResp.ok) {
                const depList = await depResp.json();
                depList.forEach((dp) => {
                  const dCat = dp.project_type === "resourcepack" ? "packs" : dp.project_type === "shader" ? "shaders" : dp.project_type === "modpack" ? "modpacks" : "mods";
                  const dMeta = {
                    id: `mr-${dp.id}`,
                    projectId: dp.id,
                    slug: dp.slug || dp.id,
                    catKey: dCat,
                    enName: dp.title || dp.slug,
                    cnName: chineseName(dCat, dp.slug),
                    summary: dp.description || "",
                    icon: dp.icon_url || "assets/mc-icons/cobblestone.png",
                    downloads: formatDownloadCountCn(dp.downloads),
                    source: "Modrinth"
                  };
                  depProjectsById[dp.id] = dMeta;
                  onlineCatalogCache.set(`${dCat}:${dMeta.id}`, dMeta);
                });
              }
            } catch {}
          }
          const itemMeta = {
            ...(raw || {}),
            id: itemId,
            slug: proj.slug || pid,
            enName: proj.title || raw?.enName || "未命名资源",
            cnName: chineseName(catKey, proj.slug),
            summary: proj.description || raw?.summary || "",
            downloads: formatDownloadCountCn(proj.downloads),
            icon: proj.icon_url || raw?.icon || "assets/mc-icons/cobblestone.png",
            webUrl: `https://modrinth.com/${proj.project_type || "mod"}/${proj.slug || pid}`
          };
          const detail = normalizeModrinthVersionsToTrees(catKey, pid, itemMeta, vers, depProjectsById);
          cacheDetail(cacheKey, detail);
          return detail;
        }
      } catch {
        // The standalone prototype can still display its local examples.
      }
    }
    return getResourceDetail(catKey, itemId);
  }

  function cacheDetail(key, detail) {
    if (liveDetailCache.size >= 24) liveDetailCache.delete(liveDetailCache.keys().next().value);
    liveDetailCache.set(key, detail);
  }

  function getResourceDetail(catKey, itemId) {
    const cacheKey = `${catKey}:${itemId}`;
    if (liveDetailCache.has(cacheKey)) return liveDetailCache.get(cacheKey);
    const list = state.catalog[catKey] || [];
    const raw =
      list.find((it) => String(it.id) === String(itemId)) ||
      onlineCatalogCache.get(cacheKey);
    if (!raw || isWebView2) return null;

    if (catKey === "versions") {
      const mcVer = raw.id;
      const supported = new Set(raw.loaders || []);
      const order = ["Forge", "NeoForge", "Fabric", "OptiFine"];
      const baseLoaders = order.filter((ld) => supported.has(ld));
      const loaderTrees = {};
      baseLoaders.forEach((ld, lIdx) => {
        const versionTag =
          ld === "Forge"
            ? `${mcVer}-49.1.${12 - lIdx}`
            : ld === "NeoForge"
              ? `21.${mcVer.split(".")[1] || 1}.${72 - lIdx * 4}`
              : ld === "Fabric"
                ? `0.16.${9 - lIdx}`
                : `HD_U_I${8 - lIdx}`;
        const prevTag =
          ld === "Forge"
            ? `${mcVer}-49.0.50`
            : ld === "NeoForge"
              ? `21.${mcVer.split(".")[1] || 1}.64`
              : ld === "Fabric"
                ? `0.16.5`
                : `HD_U_I6`;
        const betaTag =
          ld === "Forge"
            ? `${mcVer}-49.2.0-beta`
            : ld === "NeoForge"
              ? `21.${mcVer.split(".")[1] || 1}.80-beta`
              : ld === "Fabric"
                ? `0.17.0-beta.1`
                : `pre3_HD_U_I9`;
        const officialIcon = LOADER_OFFICIAL_ICONS[ld] || raw.icon;
        loaderTrees[ld] = [
          {
            mcVersion: mcVer,
            title: `${ld} ${mcVer}`,
            builds: [
              {
                id: `${mcVer}-${ld}-latest`,
                title: `${ld} ${versionTag} (推荐)`,
                icon: officialIcon,
                file: `${mcVer}-${ld.toLowerCase()}-${versionTag}.jar`,
                downloads: `${52 - lIdx * 5} 万次`,
                updatedAgo: "2 周前",
                size: "31.2 MB",
                releaseType: "Release"
              },
              {
                id: `${mcVer}-${ld}-stable`,
                title: `${ld} ${prevTag} (稳定版)`,
                icon: officialIcon,
                file: `${mcVer}-${ld.toLowerCase()}-lts.jar`,
                downloads: `${34 - lIdx * 4} 万次`,
                updatedAgo: "1 个月前",
                size: "29.8 MB",
                releaseType: "Release"
              },
              {
                id: `${mcVer}-${ld}-beta`,
                title: `${ld} ${betaTag} (测试版)`,
                icon: officialIcon,
                file: `${mcVer}-${ld.toLowerCase()}-preview.jar`,
                downloads: `${14 - lIdx * 2} 万次`,
                updatedAgo: "4 天前",
                size: "30.5 MB",
                releaseType: "Beta"
              }
            ]
          }
        ];
      });
      const defaultSummary =
        MC_OFFICIAL_SUMMARIES[mcVer] ||
        (raw.category === "snapshot"
          ? `Minecraft Java Edition ${mcVer} 官方实验性快照，抢先带来新特性测试与问题修复。`
          : raw.category === "ancient"
            ? `Minecraft ${mcVer} 早期经典留档版本，保留早期方块手感与复古玩法体验。`
            : `Minecraft Java Edition ${mcVer} 官方正式发行版，包含该周期的新内容实装、稳定性修复与联机改进。`);
      return {
        catKey,
        id: raw.id,
        cnName: `Minecraft ${raw.id}`,
        enName: `${raw.type} · ${raw.java}`,
        icon: raw.icon,
        isPixel: true,
        summary: defaultSummary,
        author: "Mojang Studios",
        downloads: "官方原版核心",
        mcVersions: raw.id,
        source: state.mirrorSource,
        loaders: baseLoaders,
        links: {
          mcwiki: buildMcWikiUrl(raw.id)
        },
        loaderTrees
      };
    }

    const extra = CURATED_DETAIL_META[raw.id] || {};
    const slug = extra.slug || String(raw.enName || raw.id).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    const src = String(raw.source || "");
    const links = {};
    if (src.includes("CurseForge") || src === "两者都有") {
      links.curseforge = `https://www.curseforge.com/minecraft/mc-mods/${slug}`;
    }
    if (src.includes("Modrinth") || src === "两者都有") {
      links.modrinth = `https://modrinth.com/mod/${slug}`;
    }
    links.mcmod = extra.mcmodId
      ? `https://www.mcmod.cn/class/${extra.mcmodId}.html`
      : `https://search.mcmod.cn/s?key=${encodeURIComponent(raw.cnName || raw.enName)}`;

    const mcList = extra.mcVersionsList || ["1.21.4", "1.21.1", "1.20.6", "1.20.1"];
    const baseVerSeed = ((slug.length % 5) + 1) * 0.1;

    if (catKey === "packs") {
      const cleanPackName = String(raw.enName || "").replace(/\s+Addon\s+\d+$/i, "").trim() || raw.cnName || raw.enName;
      const directGroups = mcList.map((mcVer, mIdx) => {
        const b1 = `v${3 - (mIdx % 2)}.${4 + mIdx}.0`;
        const b2 = `v${3 - (mIdx % 2)}.${3 + mIdx}.2`;
        return {
          mcVersion: mcVer,
          title: `Minecraft ${mcVer}`,
          builds: [
            {
              id: `${raw.id}-${mcVer}-1`,
              title: `${cleanPackName} ${b1}`,
              file: `${slug}-${b1}-mc${mcVer}.zip`,
              downloads: `${Math.round(64 + mIdx * 17)} 万次`,
              updatedAgo: mIdx === 0 ? "12 天前" : `${mIdx + 2} 个月前`,
              size: `${(4.2 + baseVerSeed + mIdx * 0.3).toFixed(1)} MB`,
              releaseType: "Release"
            },
            {
              id: `${raw.id}-${mcVer}-2`,
              title: `${cleanPackName} ${b2}`,
              file: `${slug}-${b2}-mc${mcVer}.zip`,
              downloads: `${Math.round(28 + mIdx * 11)} 万次`,
              updatedAgo: mIdx === 0 ? "2 个月前" : `${mIdx + 4} 个月前`,
              size: `${(4.0 + baseVerSeed + mIdx * 0.25).toFixed(1)} MB`,
              releaseType: "Release"
            }
          ]
        };
      });
      return {
        catKey,
        id: raw.id,
        cnName: raw.cnName || "",
        enName: raw.enName,
        icon: raw.icon,
        isPixel: false,
        hideLoaders: true,
        summary: extra.summary || `${raw.enName} 主题材质资源包，重绘游戏内的方块细节与表面质感，打造更具层次感的视觉风格。`,
        author: extra.author || "Community Maintainer",
        downloads: raw.downloads,
        mcVersions: raw.mcVersions,
        source: raw.source,
        loaders: ["default"],
        links,
        loaderTrees: { default: directGroups }
      };
    }

    const loaders = Array.isArray(raw.loaders) && raw.loaders.length > 0 ? [...raw.loaders] : ["Fabric", "NeoForge"];
    const loaderTrees = {};
    loaders.forEach((ld, lIdx) => {
      loaderTrees[ld] = mcList.map((mcVer, mIdx) => {
        const major = Math.max(0, 6 - mIdx - (lIdx % 2));
        const b1 = `${major}.${5 + (mIdx % 3)}.11`;
        const b2 = `${major}.${5 + (mIdx % 3)}.9`;
        const b3 = `${major}.${5 + (mIdx % 3)}.8`;
        const cleanEn = String(raw.enName || "").replace(/\s+Addon\s+\d+$/i, "").trim() || raw.cnName;
        const ldLow = ld.toLowerCase().replace(/[^a-z0-9]/g, "");
        const mockDeps = raw.id === "mr-iris"
          ? [{ id: "mr-sodium", projectId: "mr-sodium", catKey: "mods", cnName: "钠", enName: "Sodium", icon: "assets/mod-icons/sodium.webp", summary: "高性能渲染引擎前置支持库", downloads: "14820.0 万次", source: "Modrinth", depType: "required", label: "前置支持库" }]
          : raw.id === "mr-create"
            ? [{ id: "cf-jei", projectId: "cf-jei", catKey: "mods", cnName: "JEI 物品管理器", enName: "Just Enough Items (JEI)", icon: "assets/mod-icons/jei.webp", summary: "配方与合成查询推荐辅助库", downloads: "29400.0 万次", source: "CurseForge", depType: "optional", label: "可选前置" }]
            : [];
        return {
          mcVersion: mcVer,
          title: `${ld} ${mcVer}`,
          dependencies: mockDeps,
          builds: [
            {
              id: `${raw.id}-${ldLow}-${mcVer}-1`,
              title: `${cleanEn} ${b1}`,
              file: `${slug}-${ldLow}-${b1}+mc${mcVer}.jar`,
              downloads: `${Math.round(78 + mIdx * 19 - lIdx * 7)} 万次`,
              updatedAgo: mIdx === 0 ? "18 天前" : mIdx === 1 ? "4 个月前" : `${mIdx + 1} 年 ${mIdx + 2} 个月前`,
              size: `${(1.2 + baseVerSeed + mIdx * 0.15).toFixed(1)} MB`,
              releaseType: "Release"
            },
            {
              id: `${raw.id}-${ldLow}-${mcVer}-2`,
              title: `${cleanEn} ${b2}`,
              file: `${slug}-${ldLow}-${b2}+mc${mcVer}.jar`,
              downloads: `${Math.round(32 + mIdx * 14)} 万次`,
              updatedAgo: mIdx === 0 ? "2 个月前" : `${mIdx + 1} 年 ${mIdx + 3} 个月前`,
              size: `${(1.1 + baseVerSeed + mIdx * 0.14).toFixed(1)} MB`,
              releaseType: "Release"
            },
            {
              id: `${raw.id}-${ldLow}-${mcVer}-3`,
              title: `${cleanEn} ${b3}`,
              file: `${slug}-${ldLow}-${b3}+mc${mcVer}.jar`,
              downloads: `${Math.round(144 - mIdx * 12)} 万次`,
              updatedAgo: mIdx === 0 ? "5 个月前" : `${mIdx + 1} 年 ${mIdx + 5} 个月前`,
              size: `${(1.0 + baseVerSeed + mIdx * 0.12).toFixed(1)} MB`,
              releaseType: "Beta"
            }
          ]
        };
      });
    });

    const catFallbackSummary =
      catKey === "packs"
        ? `${raw.enName} 主题材质资源包，重绘游戏内的方块细节与表面质感，打造更具层次感的视觉风格。`
        : catKey === "shaders"
          ? `${raw.enName} 光影着色包，为世界带来自然阳光散射、动态水波反射与柔和的夜间光影氛围。`
          : catKey === "modpacks"
            ? `${raw.enName} 主题整合包，预先整合与调校全套玩法内容、任务指引与画面体验，安装即可展开冒险。`
            : `${raw.cnName ? `${raw.cnName} (${raw.enName})` : raw.enName} 社区热门拓展模组，带来丰富的游戏玩法、探索内容与便捷体验。`;

    return {
      catKey,
      id: raw.id,
      cnName: raw.cnName || "",
      enName: raw.enName,
      icon: raw.icon,
      isPixel: false,
      summary: extra.summary || catFallbackSummary,
      author: extra.author || "Community Maintainer",
      downloads: raw.downloads,
      mcVersions: raw.mcVersions,
      source: raw.source,
      loaders,
      links,
      loaderTrees
    };
  }

  let readyPromise;
  if (isWebView2) {
    if (global.__DCL_BOOT_STATE__ && typeof global.__DCL_BOOT_STATE__ === "object") {
      applyBackendState(global.__DCL_BOOT_STATE__);
      readyPromise = Promise.resolve(state);
    } else {
      readyPromise = invoke("get_state", {}).catch(() => state);
    }
  } else {
    readyPromise = typeof global.fetch === "function"
      ? global.fetch("assets/mod-names-zh-cn.json")
          .then(response => response.ok ? response.json() : [])
          .then(names => { modNames = Array.isArray(names) ? names : []; return state; })
          .catch(() => state)
      : Promise.resolve(state);
  }

  async function resolveModProject(modInfo) {
    if (!modInfo) return null;
    if (isWebView2) {
      return await invoke("resolve_mod_project", modInfo);
    }
    const slug = String(modInfo.slug || modInfo.modId || modInfo.id || "").toLowerCase();
    const normName = String(modInfo.name || "").toLowerCase().replace(/\s+\(.*?\)/g, "").trim();
    const hit = (state.catalog.mods || []).find((m) => {
      const mid = String(m.id || "").toLowerCase().replace(/^(mr|cf)-/, "");
      const men = String(m.enName || "").toLowerCase();
      const mcn = String(m.cnName || "").toLowerCase();
      return (slug && (mid === slug || men.includes(slug))) ||
        (normName && (men === normName || mcn === normName || men.includes(normName)));
    });
    if (hit) {
      rememberCatalogItems("mods", [hit]);
      return { catKey: "mods", id: hit.id, preview: hit };
    }
    try {
      const q = slug || normName;
      if (q) {
        const res = await fetchLiveCatalogPage("mods", q, "all", 1, 10, "all", 0, "downloads", "all");
        if (res?.items?.length) {
          rememberCatalogItems("mods", [res.items[0]]);
          return { catKey: "mods", id: res.items[0].id, preview: res.items[0] };
        }
      }
    } catch {}
    return null;
  }

  global.LauncherBridge = {
    isTauri,
    isWebView2,
    ready: () => readyPromise,
    onHostEvent: (fn) => {
      if (typeof fn === "function") hostListeners.add(fn);
      return () => hostListeners.delete(fn);
    },
    invoke,
    getStateSync: () => state,
    getResourceDetail,
    getCachedResourceDetail,
    getResourcePreview,
    rememberCatalogItems,
    resolveModProject,
    namePinyinMatches,
    fetchLiveCatalogPage,
    fetchLiveResourceDetail
  };
})(window);
