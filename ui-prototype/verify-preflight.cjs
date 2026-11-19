#!/usr/bin/env node
/**
 * Fast Pre-Flight Verifier for DummyCrisp UI Prototype (< 0.5s)
 * Catches JS syntax errors, view rendering errors, nested <button> bugs,
 * orphaned CSS classes between views.js and styles/*.css, and undefined CSS variables
 * BEFORE any browser subagent is launched.
 */

const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { execFileSync } = require("child_process");

const ROOT = __dirname;
const SCRIPTS = ["scripts/bridge.js", "scripts/views.js", "scripts/app.js"];
const STYLES = ["styles/tokens.css", "styles/base.css", "styles/components.css", "styles/folder-3d.css", "styles/layout.css"];

const startMs = Date.now();
const errors = [];
const warnings = [];

// 1. JS Syntax Check
for (const rel of SCRIPTS) {
  const full = path.join(ROOT, rel);
  try {
    execFileSync(process.execPath, ["--check", full], { stdio: "pipe" });
  } catch (err) {
    errors.push(`[Syntax] ${rel}: ${err.stderr ? err.stderr.toString().trim() : err.message}`);
  }
}

// 2. Read CSS & Extract Defined Classes and Custom Properties
let allCss = "";
for (const rel of STYLES) {
  const full = path.join(ROOT, rel);
  if (fs.existsSync(full)) {
    allCss += "\n" + fs.readFileSync(full, "utf8");
  }
}

const cssDefinedClasses = new Set();
const classRegex = /\.([a-zA-Z_][a-zA-Z0-9_-]*)/g;
let match;
while ((match = classRegex.exec(allCss)) !== null) {
  cssDefinedClasses.add(match[1]);
}

const definedVars = new Set();
const varDeclRegex = /(--[a-zA-Z0-9_-]+)\s*:/g;
while ((match = varDeclRegex.exec(allCss)) !== null) {
  definedVars.add(match[1]);
}

const viewsSrc = fs.readFileSync(path.join(ROOT, "scripts/views.js"), "utf8");
const appSrc = fs.readFileSync(path.join(ROOT, "scripts/app.js"), "utf8");
const combinedJs = viewsSrc + "\n" + appSrc;

while ((match = varDeclRegex.exec(combinedJs)) !== null) {
  definedVars.add(match[1]);
}

// Check that every var(--x) used in CSS has a producer in CSS or JS
const varUseRegex = /var\(\s*(--[a-zA-Z0-9_-]+)/g;
while ((match = varUseRegex.exec(allCss)) !== null) {
  const varName = match[1];
  if (!definedVars.has(varName)) {
    errors.push(`[CSS Var] ${varName} is used in styles/*.css via var(${varName}) but never defined in CSS or JS!`);
  }
}

// 3. Execute bridge.js + views.js in VM & Render All Views
const sandbox = {
  window: {},
  console,
  structuredClone: typeof structuredClone === "function" ? structuredClone : (o) => JSON.parse(JSON.stringify(o)),
  setTimeout,
  clearTimeout
};
sandbox.global = sandbox.window;
vm.createContext(sandbox);

try {
  const bridgeSrc = fs.readFileSync(path.join(ROOT, "scripts/bridge.js"), "utf8");
  vm.runInContext(bridgeSrc, sandbox, { filename: "bridge.js" });
  vm.runInContext(viewsSrc, sandbox, { filename: "views.js" });

  const Bridge = sandbox.window.LauncherBridge;
  const Views = sandbox.window.LauncherViews;
  const state = Bridge.getStateSync();
  const active = Views.getActiveInstance(state);

  const renderedSnippets = [
    { name: "HomeView (page 1)", html: Views.renderHomeView(state, 0, null, null, null, "", false, { favPage: 1 }) },
    { name: "HomeView (page 2)", html: Views.renderHomeView(state, 0, null, null, null, "", false, { favPage: 2 }) },
    { name: "QuickPanel:overview", html: Views.renderQuickPanel(state, active, "overview", {}) },
    { name: "QuickPanel:settings", html: Views.renderQuickPanel(state, active, "settings", {}) },
    { name: "QuickPanel:mods", html: Views.renderQuickPanel(state, active, "mods", { modPage: 1, modQuery: "" }) },
    { name: "QuickPanel:folders", html: Views.renderQuickPanel(state, active, "folders", {}) },
    { name: "InstanceGalleryView", html: Views.renderInstanceGalleryView(state) },
    { name: "DownloadQueueView", html: Views.renderDownloadQueueView(state) },
    {
      name: "DownloadView:versions",
      html: Views.renderDownloadView("download-versions", state, "", { filterOpen: true })
    },
    {
      name: "ResourceDetailView:mod",
      html: Views.renderResourceDetailView(state, {
        catKey: "mods",
        id: state.catalog.mods[0].id,
        activeLoader: "Fabric",
        expandedMcVersion: "1.21.1"
      })
    },
    {
      name: "ResourceDetailView:version",
      html: Views.renderResourceDetailView(state, {
        catKey: "versions",
        id: "1.21.4",
        activeLoader: "NeoForge",
        expandedMcVersion: "1.21.4",
        selectedVersionBuilds: { NeoForge: "1.21.4-NeoForge-latest" }
      })
    },
    {
      name: "FloatingDlBall",
      html: Views.renderFloatingDownloadBall({ ...state, hasUnseenDownloadBall: true })
    },
    {
      name: "ManageView:auth-java",
      html: Views.renderManageView("auth-java", state, "", { javaInstallerOpen: true })
    },
    {
      name: "DiagnosticsView:runtime-diag",
      html: Views.renderDiagnosticsView("runtime-diag", state)
    }
  ];

  // Domain contract checks (9 latest user requirements)
  const expectedIconsOrder = [
    "grass", "command", "bedrock", "tnt",
    "cobblestone", "crafting", "redstone", "gold",
    "creeper", "enderman", "zombie", "wither",
    "fox", "wolf", "chicken", "cat"
  ];
  const actualIconsOrder = Object.keys(Views.MC_ICONS || {});
  if (JSON.stringify(actualIconsOrder) !== JSON.stringify(expectedIconsOrder)) {
    errors.push(`[MC_ICONS] Order mismatch: got [${actualIconsOrder.join(",")}]`);
  }
  for (const key of actualIconsOrder) {
    const src = Views.MC_ICONS[key] && Views.MC_ICONS[key].src;
    if (!src || !fs.existsSync(path.join(ROOT, src))) {
      errors.push(`[Assets] Missing MC_ICONS file: ${src}`);
    }
  }
  const vDetail1214 = Bridge.getResourceDetail("versions", "1.21.4");
  if (!vDetail1214 || vDetail1214.loaders.includes("Vanilla")) {
    errors.push(`[VersionDetail] Vanilla must not appear in version detail loaders`);
  }
  for (const ld of vDetail1214.loaders) {
    const iconPath = vDetail1214.loaderTrees[ld] && vDetail1214.loaderTrees[ld][0].builds[0].icon;
    if (!iconPath || !fs.existsSync(path.join(ROOT, iconPath))) {
      errors.push(`[Assets] Missing loader icon for ${ld}: ${iconPath}`);
    }
  }
  const catExpectedIcon = {
    release: "assets/mc-icons/grass.png",
    snapshot: "assets/mc-icons/command.png",
    ancient: "assets/mc-icons/bedrock.png",
    april: "assets/mc-icons/tnt.png"
  };
  for (const v of state.catalog.versions) {
    if (catExpectedIcon[v.category] && v.icon !== catExpectedIcon[v.category]) {
      errors.push(`[Catalog] Version ${v.id} (${v.category}) has wrong icon ${v.icon}, expected ${catExpectedIcon[v.category]}`);
    }
  }
  const emptyStateClone = JSON.parse(JSON.stringify(state));
  emptyStateClone.instances.forEach((i) => { i.favorite = false; });
  emptyStateClone.downloadTasks = [];
  emptyStateClone.accounts = [];
  emptyStateClone.account = { loggedIn: false, id: "", username: "", uuid: "-", type: "未登录", avatar: "" };
  const emptyHomeHtml = Views.renderHomeView(emptyStateClone, 0, null, null, null, "", false, {});
  const emptyDlHtml = Views.renderDownloadQueueView(emptyStateClone);
  const emptyModHtml = Views.renderDownloadView("download-mods", state, "__nonexistent_query__", {});
  if (!emptyHomeHtml.includes("空空如也X_X") || !emptyDlHtml.includes("空空如也X_X") || !emptyModHtml.includes("空空如也X_X")) {
    errors.push(`[EmptyState] Empty lists must render "空空如也X_X"`);
  }
  if (!emptyHomeHtml.includes("is-anonymous")) {
    errors.push(`[Account] Zero-account state must render circular anonymous avatar (.is-anonymous)`);
  }

  const CHECKED_PREFIXES = /^(cobra-|ff-|folder-|vdetail-|mirror-|emu-|moth-|cc-|dl-state-|pcl-|wasp-|filter-popover|floating-dl-|account-|player-|theme-palette|theme-circle|quail-|gooey-|java-|rare-|ram-|crash-|dl-)/;
  const IGNORED_CLASSES = new Set(["is-active", "is-selected", "is-faved", "is-armed", "is-on", "is-open", "is-paused", "is-downloading", "is-completed"]);

  for (const item of renderedSnippets) {
    const html = String(item.html || "");
    if (!html.trim()) {
      errors.push(`[Render] ${item.name} returned empty HTML`);
      continue;
    }

    // Check for illegal nested <button>...<button>
    const tokenRegex = /<\/?button\b[^>]*>/gi;
    let depth = 0;
    let tMatch;
    while ((tMatch = tokenRegex.exec(html)) !== null) {
      if (tMatch[0].startsWith("</")) {
        depth = Math.max(0, depth - 1);
      } else {
        depth += 1;
        if (depth > 1) {
          errors.push(`[HTML Validity] Nested <button> inside <button> detected in ${item.name}`);
          break;
        }
      }
    }

    // Check component classes against CSS definitions
    const classAttrRegex = /class="([^"]+)"/g;
    let cMatch;
    while ((cMatch = classAttrRegex.exec(html)) !== null) {
      const classes = cMatch[1].trim().split(/\s+/);
      for (const cls of classes) {
        if (CHECKED_PREFIXES.test(cls) && !IGNORED_CLASSES.has(cls) && !cssDefinedClasses.has(cls)) {
          errors.push(`[CSS Contract] Class ".${cls}" rendered in ${item.name} is missing from styles/*.css`);
        }
      }
    }
  }
} catch (err) {
  errors.push(`[VM Execution] ${err.stack || err.message}`);
}

const elapsed = Date.now() - startMs;
if (errors.length > 0) {
  console.error(`❌ Pre-flight check FAILED (${errors.length} error(s)) in ${elapsed}ms:`);
  for (const e of [...new Set(errors)]) {
    console.error("  - " + e);
  }
  process.exit(1);
} else {
  console.log(`✅ Pre-flight check PASSED (syntax + VM render + CSS class/var contracts) in ${elapsed}ms.`);
}
