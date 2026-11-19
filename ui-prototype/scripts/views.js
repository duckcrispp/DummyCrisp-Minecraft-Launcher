/* ==========================================================================
   DummyCrisp Modular View Templates
   Renders Homepage + Sub-Pages triggered from 3D Spatial Portal Cards
   ========================================================================== */

(function (global) {
  function esc(str) {
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function getActiveInstance(state) {
    return (
      (state.instances || []).find((i) => i.id === state.activeInstanceId) ||
      (state.instances && state.instances[0]) || {
        id: "",
        name: "尚未安装游戏",
        mcVersion: "1.21.4",
        loader: "Vanilla",
        loaderVersion: "未安装",
        javaId: "",
        javaVersion: (state.javas && state.javas[0] && `Java ${state.javas[0].version}`) || "未检测到 Java",
        jvmArgs: state.customJvmArgs || "",
        iconId: "grass",
        favorite: false,
        modsCount: 0,
        lastPlayed: "未游玩",
        isolated: false,
        useGlobalSettings: true,
        memoryMb: state.memoryMb || 4096,
        tagline: "点击下载游戏版本"
      }
    );
  }

  /* Official Minecraft Wiki Block / Item / Mob Icons (16 Ordered Icons, 4x4 Grid) */
  const MC_ICONS = {
    grass: { name: "草方块", src: "assets/mc-icons/grass.png" },
    command: { name: "命令方块", src: "assets/mc-icons/command.png" },
    bedrock: { name: "基岩", src: "assets/mc-icons/bedrock.png" },
    tnt: { name: "TNT", src: "assets/mc-icons/tnt.png" },
    cobblestone: { name: "圆石", src: "assets/mc-icons/cobblestone.png" },
    crafting: { name: "工作台", src: "assets/mc-icons/crafting.png" },
    redstone: { name: "红石灯", src: "assets/mc-icons/redstone.png" },
    gold: { name: "金块", src: "assets/mc-icons/gold.png" },
    creeper: { name: "苦力怕", src: "assets/mc-icons/creeper.png" },
    enderman: { name: "末影人", src: "assets/mc-icons/enderman.png" },
    zombie: { name: "僵尸", src: "assets/mc-icons/zombie.png" },
    wither: { name: "凋灵", src: "assets/mc-icons/wither.png" },
    fox: { name: "狐狸", src: "assets/mc-icons/fox.png" },
    wolf: { name: "狼", src: "assets/mc-icons/wolf.png" },
    chicken: { name: "鸡", src: "assets/mc-icons/chicken.png" },
    cat: { name: "猫", src: "assets/mc-icons/cat.png" }
  };

  function getIconMeta(iconId) {
    return MC_ICONS[iconId] || MC_ICONS.grass;
  }

  function renderMcIconImg(iconId) {
    const meta = getIconMeta(iconId);
    return `<img src="${esc(meta.src)}" alt="${esc(meta.name)}" loading="eager" />`;
  }

  function renderInstanceEmblemImg(inst) {
    if (inst && inst.customIcon) {
      return `<img class="is-custom-emblem" src="${esc(inst.customIcon)}" alt="${esc(inst.name)}" loading="eager" />`;
    }
    return renderMcIconImg(inst && inst.iconId);
  }

  function renderCollapseBtn(attr, label) {
    const upArrowSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M12 19V5"/>
        <path d="M5 12l7-7 7 7"/>
      </svg>`;
    return `
      <button class="cc-btn cc-btn--collapse" ${attr} aria-label="${esc(label || "收起")}" type="button">
        <div class="cc-box">
          <span class="cc-elem">${upArrowSvg}</span>
          <span class="cc-elem">${upArrowSvg}</span>
        </div>
      </button>`;
  }

  function renderDownloadBtn(actionText, label, extraAttrs) {
    const dlSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M12 4v11"/>
        <path d="M7.5 11.5L12 16l4.5-4.5"/>
        <path d="M5 20h14"/>
      </svg>`;
    return `
      <button class="cc-btn cc-btn--download" data-install-action="${esc(actionText)}" ${extraAttrs || ""} aria-label="${esc(label || "下载")}" type="button">
        <div class="cc-box">
          <span class="cc-elem">${dlSvg}</span>
          <span class="cc-elem">${dlSvg}</span>
        </div>
      </button>`;
  }

  function mirrorStatus(pingMs) {
    if (!Number.isFinite(pingMs) || pingMs < 0) return { text: "测速中", tone: "" };
    return {
      text: pingMs >= 2500 ? "超时" : `${pingMs}ms`,
      tone: pingMs >= 1500 ? "is-down" : pingMs >= 150 ? "is-slow" : "is-fast"
    };
  }

  function renderMirrorStarfishControl(state) {
    const globeSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <circle cx="12" cy="12" r="9"/>
        <path d="M3.6 9h16.8M3.6 15h16.8"/>
        <path d="M12 3a13 13 0 0 1 0 18M12 3a13 13 0 0 0 0 18"/>
      </svg>`;
    const caretSvg = `<svg class="mirror-caret" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>`;
    const mirrors = state.mirrors || [];
    const activeMirrorObj = mirrors.find((m) => m.name === state.mirrorSource);
    const ping = mirrorStatus(activeMirrorObj?.pingMs);
    const triggerLabel = state.autoMirror ? `最优 · ${state.mirrorSource}` : state.mirrorSource;

    return `
      <div class="mirror-starfish-wrap" id="mirror-starfish-wrap" aria-label="下载源选择">
        <button class="mirror-starfish-trigger" type="button" aria-label="切换下载源（${esc(triggerLabel)} ${esc(ping.text)}）">
          ${globeSvg}
          <span class="mirror-trigger-label">
            <span class="mirror-trigger-name">${esc(triggerLabel)}</span>
            <span class="mirror-ping ${ping.tone}">${esc(ping.text)}</span>
          </span>
          ${caretSvg}
        </button>
        <div class="mirror-tray" role="menu" aria-label="可选下载源">
          <div class="mirror-tray-bridge" aria-hidden="true"></div>
          <button class="mirror-tray-item ${state.autoMirror ? "is-active" : ""}" data-auto-mirror type="button" role="menuitem">
            <span>自动最优</span>
            <span class="mirror-ping">AUTO</span>
          </button>
          ${mirrors
            .map(
              (m) => {
                const status = mirrorStatus(m.pingMs);
                return `
            <button
              class="mirror-tray-item ${state.mirrorSource === m.name && !state.autoMirror ? "is-active" : ""}"
              data-mirror="${esc(m.name)}"
              type="button"
              role="menuitem"
            >
              <span>${esc(m.name)}</span>
              <span class="mirror-ping ${status.tone}">${esc(status.text)}</span>
            </button>`;
              }
            )
            .join("")}
        </div>
      </div>`;
  }

  function renderQuailLoader() {
    return `
      <div class="quail-inline-loader" aria-busy="true">
        <div class="quail-loader-stage" aria-hidden="true">
          <div class="quail-loader"></div>
        </div>
      </div>`;
  }

  function renderAnimatedCounterHtml(counterId, value, unit) {
    const num = Math.max(0, Math.round(Number(value) || 0));
    const formatted = num.toLocaleString("en-US");
    const digitsStrip = "0123456789"
      .split("")
      .map((d) => `<span class="rare-digit-cell">${d}</span>`)
      .join("");
    const totalLen = formatted.length;
    const cols = formatted
      .split("")
      .map((ch, idx) => {
        const staggerIdx = Math.max(0, totalLen - 1 - idx);
        if (ch === ",") {
          return `<span class="rare-mark-col" data-slot="animated-counter-mark" style="--stagger-idx:${staggerIdx};">,</span>`;
        }
        const digit = Number(ch);
        return `
          <span class="rare-digit-col" data-slot="animated-counter-digit" style="--stagger-idx:${staggerIdx};">
            <span class="rare-digit-strip" style="transform: translateY(-${digit * 10}%);">${digitsStrip}</span>
          </span>`;
      })
      .join("");
    return `
      <span class="rare-counter" id="${esc(counterId)}" data-slot="animated-counter" data-value="${esc(num)}">
        <span class="rare-counter-digits">${cols}</span>
        ${unit ? `<span class="rare-counter-unit">${esc(unit)}</span>` : ""}
      </span>`;
  }

  function renderGooeyJavaNavHtml(navId, javas, selectedId, dataAttr) {
    const list = javas || [];
    const cur = list.find((j) => j.id === selectedId) || list[0] || { id: "" };
    const attrName = dataAttr || "data-select-java";
    return `
      <div class="gooey-nav gooey-nav--framed" id="${esc(navId)}">
        <div class="gooey-nav__stage">
          <canvas class="gooey-nav__canvas" aria-hidden="true"></canvas>
          <span class="gooey-nav__fallback" aria-hidden="true"></span>
          <ul class="gooey-nav__list" role="tablist" aria-label="选择 Java">
            ${list
              .map((j) => {
                const shortVer = j.version.startsWith("1.8") ? "Java 8" : `Java ${j.version.split(".")[0]}`;
                const isAct = j.id === cur.id;
                return `
              <li class="gooey-nav__item">
                <button
                  class="gooey-nav__link ${isAct ? "is-active" : ""}"
                  ${attrName}="${esc(j.id)}"
                  role="tab"
                  aria-selected="${Boolean(isAct)}"
                  aria-label="${esc(`Java ${j.version} · ${j.vendor} · ${j.arch}`)}"
                  title="${esc(j.path)}"
                  type="button"
                >${esc(shortVer)}</button>
              </li>`;
              })
              .join("")}
          </ul>
          <ul class="gooey-nav__list gooey-nav__list--active" aria-hidden="true">
            ${list
              .map((j) => {
                const shortVer = j.version.startsWith("1.8") ? "Java 8" : `Java ${j.version.split(".")[0]}`;
                const isAct = j.id === cur.id;
                return `
              <li class="gooey-nav__item">
                <span class="gooey-nav__link ${isAct ? "is-active" : ""}" data-gooey-mirror="${esc(j.id)}">${esc(shortVer)}</span>
              </li>`;
              })
              .join("")}
          </ul>
        </div>
      </div>`;
  }

  function renderRareMemoryBlockHtml(opts) {
    const o = opts || {};
    const val = Math.max(1024, Math.min(16384, Number(o.valueMb) || 4096));
    const min = 1024;
    const max = 16384;
    const step = 512;
    const count = Math.floor((max - min) / step) + 1;
    const activeIdx = Math.round((val - min) / step);
    const ticksHtml = Array.from({ length: count }, (_, idx) => {
      const cls =
        idx === activeIdx
          ? "rare-tick is-thumb is-filled"
          : idx < activeIdx
          ? "rare-tick is-filled"
          : "rare-tick";
      return `<span class="${cls}" data-tick-idx="${idx}"></span>`;
    }).join("");

    return `
      <div class="rare-ram-panel">
        <div class="rare-ram-stepper">
          <button
            class="rare-step-btn"
            data-ram-step="-512"
            data-ram-target="${esc(o.target)}"
            type="button"
            aria-label="减少 512 MB"
          >−</button>
          <div class="rare-counter-stage">
            ${renderAnimatedCounterHtml(o.counterId, val, "MB")}
          </div>
          <button
            class="rare-step-btn"
            data-ram-step="512"
            data-ram-target="${esc(o.target)}"
            type="button"
            aria-label="增加 512 MB"
          >+</button>
        </div>
        <div class="rare-tick-slider-wrap">
          <div class="rare-tick-bars" id="${esc(o.sliderId)}-ticks" aria-hidden="true">
            ${ticksHtml}
          </div>
          <input
            id="${esc(o.sliderId)}"
            class="ram-range-slider ram-range-slider--overlay"
            type="range"
            min="${min}"
            max="${max}"
            step="${step}"
            value="${val}"
            aria-label="内存分配滑动条"
          />
        </div>
      </div>`;
  }

  function renderShowInWindowBtn(btnId, title) {
    const winSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <rect x="3" y="4" width="18" height="16" rx="2.5"/>
        <line x1="3" y1="9" x2="21" y2="9"/>
        <polyline points="14 13 17 13 17 16"/>
        <line x1="12" y1="18" x2="17" y2="13"/>
      </svg>`;
    return `
      <button class="cc-btn cc-btn--sm" id="${esc(btnId)}" aria-label="${esc(title)}" title="${esc(title)}" type="button">
        ${winSvg}
      </button>`;
  }

  function renderPauseResumeBtn(isDownloading, taskId, name) {
    const pauseSvg = `<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><rect x="6" y="5" width="4" height="14" rx="1.2"/><rect x="14" y="5" width="4" height="14" rx="1.2"/></svg>`;
    const playSvg = `<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M8 5.2v13.6a.8.8 0 0 0 1.24.67l10.2-6.8a.8.8 0 0 0 0-1.34L9.24 4.53A.8.8 0 0 0 8 5.2z"/></svg>`;
    return `
      <button
        class="dl-state-btn ${isDownloading ? "is-downloading" : "is-paused"}"
        data-toggle-dl="${esc(taskId)}"
        aria-label="${esc(`${isDownloading ? "暂停" : "继续"} ${name}`)}"
        title="${isDownloading ? "暂停下载" : "继续下载"}"
        type="button"
      >
        ${isDownloading ? pauseSvg : playSvg}
      </button>`;
  }

  function renderTitlePair(cnName, enName) {
    const cn = String(cnName || "").trim();
    const en = String(enName || "").trim();
    if (cn && !en.includes(cn)) {
      return `
        <span class="data-row-title">${esc(cn)}</span>
        <span class="row-en-name">${esc(en)}</span>`;
    }
    return `<span class="data-row-title">${esc(en || cn)}</span>`;
  }

  function renderFilterBtn(isActive) {
    const filterSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <polygon points="3 5 21 5 14 13 14 19 10 21 10 13 3 5"/>
      </svg>`;
    return `
      <button class="emu-btn emu-btn--filter ${isActive ? "is-active" : ""}" id="btn-toggle-filter" aria-label="筛选" title="筛选" type="button">
        <span class="filled" aria-hidden="true"></span>
        ${filterSvg}
      </button>`;
  }

  /* Circular Checkmark Toggle (uiverse.io/kyle1dev/afraid-wasp-94) */
  function renderWaspCheckBtn(loaderName, buildId, isChecked, label) {
    return `
      <button
        class="wasp-check-btn ${isChecked ? "is-checked" : ""}"
        data-check-version-build="${esc(loaderName)}:${esc(buildId)}"
        aria-label="${esc(`${isChecked ? "取消选择" : "选择"} ${label}`)}"
        aria-pressed="${isChecked ? "true" : "false"}"
        type="button"
      >
        <span class="wasp-check-wrapper" aria-hidden="true">
          <span class="wasp-check-bg"></span>
          <svg class="wasp-check-icon" fill="none" viewBox="0 0 24 24">
            <path
              class="wasp-check-path"
              stroke-linejoin="round"
              stroke-linecap="round"
              stroke-width="3"
              stroke="currentColor"
              d="M4 12L10 18L20 6"
            ></path>
          </svg>
        </span>
      </button>`;
  }

  /* Circular Checkmark/Cross Toggle (uiverse.io/ElsayedShamsEldeen/cuddly-moth-14) */
  function renderMothToggle(isOn, attrs, ariaLabel, isSmall) {
    return `
      <button class="moth-toggle ${isSmall ? "moth-toggle--sm" : ""} ${isOn ? "is-on" : ""}" ${attrs} aria-label="${esc(ariaLabel)}" aria-pressed="${Boolean(isOn)}" type="button">
        <span class="moth-layer moth-check" aria-hidden="true">
          <svg viewBox="0 0 24 24"><polyline points="5 13 10 18 19 7"/></svg>
        </span>
        <span class="moth-layer moth-cross" aria-hidden="true">
          <svg viewBox="0 0 24 24"><line x1="6" y1="6" x2="18" y2="18"/><line x1="18" y1="6" x2="6" y2="18"/></svg>
        </span>
      </button>`;
  }

  /* Circular Pure-Icon "Open in Mods Folder" Button */
  function renderOpenFolderBtn(subpath, ariaLabel, isSmall) {
    const folderSvg = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"/>
        <path d="M10 14l4-4m0 0h-3m3 0v3"/>
      </svg>`;
    return `
      <button class="cc-btn cc-btn--openfolder ${isSmall ? "moth-toggle--sm" : ""}" data-open-folder="${esc(subpath)}" aria-label="${esc(ariaLabel || "在 Mod 文件夹中打开")}" type="button">
        <div class="cc-box">
          <span class="cc-elem">${folderSvg}</span>
          <span class="cc-elem">${folderSvg}</span>
        </div>
      </button>`;
  }

  /* 2-Click Delete Button (uiverse.io/vinodjangid07/smart-emu-83) */
  function renderDeleteBtn(modId, ariaLabel, isSmall) {
    return `
      <button class="emu-delete-btn ${isSmall ? "emu-delete-btn--sm" : ""}" data-delete-mod="${esc(modId)}" aria-label="${esc(ariaLabel || "删除 Mod")}" type="button">
        <svg class="emu-del-icon" viewBox="0 0 448 512" aria-hidden="true">
          <path d="M135.2 17.7L128 32H32C14.3 32 0 46.3 0 64S14.3 96 32 96H416c17.7 0 32-14.3 32-32s-14.3-32-32-32H320l-7.2-14.3C307.4 6.8 296.3 0 284.2 0H163.8c-12.1 0-23.2 6.8-28.6 17.7zM416 128H32L53.2 467c1.6 25.3 22.6 45 47.9 45H346.9c25.3 0 46.3-19.7 47.9-45L416 128z"/>
        </svg>
      </button>`;
  }

  /* Adaptive Word-Art Algorithm + ReactBits Stroke-Text SVG Renderer */
  function measureVisualUnits(str) {
    let units = 0;
    for (const ch of String(str || "")) {
      units += ch.charCodeAt(0) > 255 ? 1.0 : 0.56;
    }
    return units;
  }

  function splitArtHeadline(name) {
    const raw = String(name || "").trim();
    if (!raw) return { top: "Minecraft", accent: "" };

    const parts = raw.split(/\s+/).filter(Boolean);
    if (parts.length >= 2) {
      const total = measureVisualUnits(raw);
      let bestIdx = 1;
      let bestDiff = Infinity;
      let acc = 0;
      for (let i = 0; i < parts.length - 1; i++) {
        acc += measureVisualUnits(parts[i]) + (i > 0 ? 0.4 : 0);
        const diff = Math.abs(acc - total * 0.5);
        if (diff < bestDiff) {
          bestDiff = diff;
          bestIdx = i + 1;
        }
      }
      return {
        top: parts.slice(0, bestIdx).join(" "),
        accent: parts.slice(bestIdx).join(" ")
      };
    }

    // Pure version numbers / snapshots (e.g. 1.21.1, 25w14a, b1.7.3) must NEVER be sliced mid-number
    const versionOnlyRe = /^(?:b|a|c|inf-|rd-)?\d+(?:\.\d+)+[a-z0-9-]*$|^\d{2}w\d{2}[a-z]$/i;
    if (versionOnlyRe.test(raw)) {
      return { top: "Minecraft", accent: raw };
    }

    // Explicit semantic separators (- _ / · +) when both halves stay readable
    const sepMatch = raw.match(/^(.{2,}?)[\s\-_/·+]+(.{2,})$/);
    if (sepMatch) {
      return { top: sepMatch[1], accent: sepMatch[2] };
    }

    // CJK <-> ASCII/numeric transition boundary (e.g. "工业1.21.1" or "1.12.2怀旧")
    const cjkAsciiMatch =
      raw.match(/^([\u4e00-\u9fff]{2,})([A-Za-z0-9.+-].*)$/) ||
      raw.match(/^([A-Za-z0-9.+-]{2,})([\u4e00-\u9fff].{1,})$/);
    if (cjkAsciiMatch) {
      return { top: cjkAsciiMatch[1], accent: cjkAsciiMatch[2] };
    }

    // Short single word/title stays intact on one crisp line
    if (measureVisualUnits(raw) <= 6.5) {
      return { top: raw, accent: "" };
    }

    // Long unpunctuated token: split near visual middle
    const targetUnits = measureVisualUnits(raw) * 0.5;
    let acc = 0;
    let cut = Math.ceil(raw.length / 2);
    let minDiff = Infinity;
    for (let i = 2; i <= raw.length - 2; i++) {
      acc += raw.charCodeAt(i - 1) > 255 ? 1.0 : 0.56;
      const diff = Math.abs(acc - targetUnits);
      if (diff < minDiff) {
        minDiff = diff;
        cut = i;
      }
    }
    return { top: raw.slice(0, cut), accent: raw.slice(cut) };
  }

  function parseJavaMajor(verStr) {
    const raw = String(verStr || "").trim().replace(/^Java\s+/i, "");
    if (!raw || /未安装|未检测|-/.test(raw)) return 0;
    const clean = raw.startsWith("1.") ? raw.slice(2) : raw;
    const m = clean.match(/^(\d+)/);
    return m ? Number(m[1]) || 0 : 0;
  }

  function getMcRequiredJavaRange(inst) {
    if (!inst) return { min: 21, max: 25, label: "Java 21-25" };
    if (Number(inst.requiredJavaMajor) > 0) {
      const mj = Number(inst.requiredJavaMajor);
      return { min: mj, max: mj, label: `Java ${mj}` };
    }
    const ver = String(inst.mcVersion || "");
    if (/^(?:2[4-9]w|2[6-9]\.)/i.test(ver)) return { min: 21, max: 25, label: "Java 21-25" };
    const m = /^(\d+)\.(\d+)(?:\.(\d+))?/.exec(ver);
    if (!m) return { min: 21, max: 25, label: "Java 21-25" };
    const major = Number(m[1]);
    const minor = Number(m[2]);
    const patch = Number(m[3] || 0);
    if (major > 1 || minor >= 21 || (minor === 20 && patch >= 5)) {
      return { min: 21, max: 25, label: "Java 21-25" };
    }
    if (minor >= 18) return { min: 17, max: 21, label: "Java 17-21" };
    if (minor === 17) return { min: 16, max: 21, label: "Java 16-17" };
    return { min: 8, max: 11, label: "Java 8-11" };
  }

  function getInstanceJavaMismatch(state, inst) {
    if (!state || !inst || !Array.isArray(state.instances) || state.instances.length === 0) return "";
    const req = getMcRequiredJavaRange(inst);
    const javas = state.javas || [];
    let effectiveJava = null;
    if (inst.useGlobalSettings !== false) {
      effectiveJava = javas.find((j) => j.active) || javas[0] || null;
    } else {
      effectiveJava = javas.find((j) => j.id === inst.javaId) || null;
    }
    const verText = effectiveJava ? effectiveJava.version : inst.javaVersion;
    const curMajor = parseJavaMajor(verText);
    if (!curMajor) {
      return `该版本需要 ${req.label}，当前 Java 为未检测到`;
    }
    if (curMajor < req.min || curMajor > req.max) {
      return `该版本需要 ${req.label}，当前 Java 为 ${curMajor}`;
    }
    return "";
  }

  function truncateArtLine(text, maxUnits) {
    const raw = String(text || "").trim();
    if (measureVisualUnits(raw) <= maxUnits) return raw;
    let out = "";
    let acc = 0;
    const limit = Math.max(4, maxUnits - 1.0);
    for (const ch of raw) {
      const w = ch.charCodeAt(0) > 255 ? 1.0 : 0.56;
      if (acc + w > limit) break;
      out += ch;
      acc += w;
    }
    return `${out.trimEnd()}…`;
  }

  function renderVersionArtHero(active, animateStroke, state) {
    const rawLines = splitArtHeadline(active.name);
    const hasAccentLine = Boolean(rawLines.accent);
    // Hard-cap per-line visual width so even very long modpack names stay inside the 580px SVG frame
    const topLine = truncateArtLine(rawLines.top, 20.5);
    const accentLine = hasAccentLine ? truncateArtLine(rawLines.accent, 21.5) : "";
    const maxUnits = Math.max(measureVisualUnits(topLine), measureVisualUnits(accentLine), 5.5);
    // Dynamically fit font size inside 580px box width while locking the SVG height so below-title controls NEVER shift
    const fontSize = Math.round(Math.max(26, Math.min(60, 510 / maxUnits)));
    const FIXED_SVG_H = 140;
    const blockH = hasAccentLine ? Math.round(fontSize * 2.12) : Math.round(fontSize * 1.08);
    const baseY = Math.max(0, Math.round((FIXED_SVG_H - blockH) * 0.56));
    const line1Y = baseY + Math.round(fontSize * 0.92);
    const line2Y = baseY + Math.round(fontSize * 2.00);
    const svgH = FIXED_SVG_H;
    const wipeId = `stroke-wipe-${String(active.id || "v").replace(/[^a-z0-9]/gi, "")}`;
    const javaWarning = getInstanceJavaMismatch(state, active);

    return `
      <div class="version-art-hero">
        <h1 class="version-art-title" aria-label="${esc(active.name)}">
          <svg
            class="version-art-svg ${animateStroke ? "is-stroke-animating" : ""}"
            viewBox="-8 -4 596 ${svgH + 8}"
            role="img"
            aria-label="${esc(active.name)}"
          >
            <defs>
              <clipPath id="${wipeId}">
                <rect class="stroke-wipe-rect" x="-8" y="-4" width="596" height="${svgH + 8}" style="transform-origin:left center; ${animateStroke ? "transform:scaleX(0);" : "transform:scaleX(1);"}"/>
              </clipPath>
            </defs>

            <!-- Outline Layer (ReactBits Stroke Text) -->
            <text class="stroke-text__stroke" x="0" y="${line1Y}" font-size="${fontSize}" font-family="var(--font-serif)" font-weight="400">${esc(topLine)}</text>
            ${hasAccentLine ? `<text class="stroke-text__stroke" x="0" y="${line2Y}" font-size="${Math.round(fontSize * 0.94)}" font-family="var(--font-display)" font-weight="800">${esc(accentLine)}</text>` : ""}

            <!-- Fill Layer (Contrast Serif + Accent Bold Display) -->
            <g class="stroke-text__fill" clip-path="url(#${wipeId})">
              <text x="0" y="${line1Y}" font-size="${fontSize}" font-family="var(--font-serif)" font-weight="400" fill="var(--text-primary)">${esc(topLine)}</text>
              ${hasAccentLine ? `<text x="0" y="${line2Y}" font-size="${Math.round(fontSize * 0.94)}" font-family="var(--font-display)" font-weight="800" fill="var(--accent-primary)">${esc(accentLine)}</text>` : ""}
            </g>
          </svg>
        </h1>
        <div class="version-art-meta-row">
          <div class="version-art-meta">${esc(active.mcVersion)} | ${esc(active.loader)} ${esc(active.loaderVersion)} | ${esc(active.javaVersion)} | ${active.modsCount} Mods</div>
          ${javaWarning ? `<div class="version-java-warning" id="version-java-warning" role="alert"><span>${esc(javaWarning)}</span></div>` : ""}
        </div>
      </div>
    `;
  }

  function getSortedInstances(state) {
    const list = Array.isArray(state.instances) ? [...state.instances] : [];
    return list.sort((a, b) => Number(Boolean(b.favorite)) - Number(Boolean(a.favorite)));
  }

  function getFavoriteInstances(state) {
    return (Array.isArray(state.instances) ? state.instances : []).filter((i) => i.favorite);
  }

  /* 8 Curated Minimalist Folder Palettes + Custom Contrast Solver */
  const FOLDER_PALETTES = [
    { id: "amber", name: "电光琥珀", back: "#c98a06", bottom: "#eab308", top: "#facc15", ink: "#0a0a0c" },
    { id: "cobalt", name: "深海靛蓝", back: "#082a3b", bottom: "#0c3b52", top: "#16597a", ink: "#f4f3ef" },
    { id: "glacier", name: "极夜冰青", back: "#0369a1", bottom: "#0284c7", top: "#38bdf8", ink: "#070b10" },
    { id: "moss", name: "冷杉苔绿", back: "#1e3d2d", bottom: "#2e5a44", top: "#4a7c59", ink: "#f4f3ef" },
    { id: "terracotta", name: "陶土赭石", back: "#7a3221", bottom: "#a34832", top: "#c9644a", ink: "#f4f3ef" },
    { id: "slate", name: "玄武钛灰", back: "#282c34", bottom: "#3f444e", top: "#5b6270", ink: "#f4f3ef" },
    { id: "plum", name: "暮色紫檀", back: "#35233e", bottom: "#4e3559", top: "#704e7e", ink: "#f4f3ef" },
    { id: "linen", name: "馆藏砂白", back: "#b3ab9b", bottom: "#d6d0c4", top: "#ece8df", ink: "#111111" }
  ];

  function shadeHex(hex, delta) {
    const clean = String(hex || "#eab308").replace("#", "");
    const full = clean.length === 3 ? clean.split("").map((c) => c + c).join("") : clean.padEnd(6, "0");
    const num = parseInt(full.slice(0, 6), 16);
    const clamp = (v) => Math.max(0, Math.min(255, v));
    const r = clamp((num >> 16) + delta);
    const g = clamp(((num >> 8) & 255) + delta);
    const b = clamp((num & 255) + delta);
    return `#${((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1)}`;
  }

  function computeContrastInk(hex) {
    const clean = String(hex || "#eab308").replace("#", "");
    const full = clean.length === 3 ? clean.split("").map((c) => c + c).join("") : clean.padEnd(6, "0");
    const num = parseInt(full.slice(0, 6), 16);
    const r = (num >> 16) & 255;
    const g = (num >> 8) & 255;
    const b = num & 255;
    const lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
    return lum > 0.58 ? "#0a0a0c" : "#f4f3ef";
  }

  function resolveFolderColorVars(inst) {
    const colorId = inst && inst.folderColor;
    if (!colorId) return { hasCustom: false, style: "" };
    if (colorId === "custom" && inst.customFolderHex) {
      const hex = inst.customFolderHex;
      const top = shadeHex(hex, 18);
      const bottom = shadeHex(hex, -12);
      const back = shadeHex(hex, -44);
      const ink = computeContrastInk(hex);
      return {
        hasCustom: true,
        style: `--f-back:${back}; --f-front-bottom:${bottom}; --f-front-top:${top}; --f-ink:${ink};`
      };
    }
    const preset = FOLDER_PALETTES.find((p) => p.id === colorId);
    if (!preset) return { hasCustom: false, style: "" };
    return {
      hasCustom: true,
      style: `--f-back:${preset.back}; --f-front-bottom:${preset.bottom}; --f-front-top:${preset.top}; --f-ink:${preset.ink};`
    };
  }

  function renderFolderCard(inst, isActive, extraClass, styleAttr, actionAttr) {
    const colorVars = resolveFolderColorVars(inst);
    const openSvg = `
      <svg class="emu-act-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"/>
        <path d="M10 14l4-4m0 0h-3m3 0v3"/>
      </svg>`;
    const heartSvg = `
      <svg class="emu-act-icon" viewBox="0 0 24 24" fill="${inst.favorite ? "currentColor" : "none"}" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/>
      </svg>`;
    const settingsSvg = `
      <svg class="emu-act-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <line x1="4" y1="7" x2="20" y2="7"/>
        <line x1="4" y1="12" x2="20" y2="12"/>
        <line x1="4" y1="17" x2="20" y2="17"/>
        <circle cx="9" cy="7" r="2" fill="currentColor"/>
        <circle cx="16" cy="12" r="2" fill="currentColor"/>
        <circle cx="11" cy="17" r="2" fill="currentColor"/>
      </svg>`;

    return `
      <div class="${extraClass} ${isActive ? "is-active" : ""}" ${actionAttr} ${styleAttr || ""} role="button" tabindex="0">
        <div class="mc-folder" ${colorVars.hasCustom ? `data-has-color="true" style="${esc(colorVars.style)}"` : ""}>
          <div class="folder-back"></div>
          <div class="folder-sheet sheet-back"></div>
          <div class="folder-sheet sheet-mid"></div>
          <div class="folder-sheet sheet-front">
            <span class="sheet-code">${esc(inst.javaVersion)}</span>
            <span class="sheet-loader">${esc(inst.loader)} · ${inst.modsCount} MODS</span>
          </div>
          <div class="folder-float-actions">
            <div class="folder-float-bridge" aria-hidden="true"></div>
            <button
              class="ff-act ff-emu-circle ff-act--fav ${inst.favorite ? "is-faved" : ""}"
              data-folder-fav="${esc(inst.id)}"
              style="--ff-i:0;"
              type="button"
              title="${inst.favorite ? "取消收藏" : "收藏此版本"}"
              aria-label="${inst.favorite ? "取消收藏" : "收藏此版本"} ${esc(inst.name)}"
              aria-pressed="${inst.favorite ? "true" : "false"}"
            >
              ${heartSvg}
            </button>
            <button
              class="ff-act ff-emu-circle"
              data-folder-open="${esc(inst.id)}"
              style="--ff-i:1;"
              type="button"
              title="打开版本文件夹"
              aria-label="打开版本文件夹 ${esc(inst.name)}"
            >
              ${openSvg}
            </button>
            <button
              class="ff-act ff-emu-circle"
              data-folder-settings="${esc(inst.id)}"
              style="--ff-i:2;"
              type="button"
              title="版本设置"
              aria-label="版本设置 ${esc(inst.name)}"
            >
              ${settingsSvg}
            </button>
            <button
              class="ff-act emu-delete-btn emu-delete-btn--sm ff-del"
              data-folder-del="${esc(inst.id)}"
              style="--ff-i:3;"
              type="button"
              title="删除此版本"
              aria-label="删除版本 ${esc(inst.name)}"
            >
              <svg class="emu-del-icon" viewBox="0 0 448 512" aria-hidden="true">
                <path d="M135.2 17.7L128 32H32C14.3 32 0 46.3 0 64S14.3 96 32 96H416c17.7 0 32-14.3 32-32s-14.3-32-32-32H320l-7.2-14.3C307.4 6.8 296.3 0 284.2 0H163.8c-12.1 0-23.2 6.8-28.6 17.7zM416 128H32L53.2 467c1.6 25.3 22.6 45 47.9 45H346.9c25.3 0 46.3-19.7 47.9-45L416 128z"/>
              </svg>
            </button>
          </div>
          <div class="folder-front">
            <div class="folder-front-fx" aria-hidden="true"><span class="folder-color-sweep"></span></div>
            <div class="folder-front-top">
              <span>${esc(inst.loader)}</span>
              <span class="folder-active-dot"></span>
            </div>
            <div class="folder-front-body">
              <div class="folder-front-ver ${String(inst.mcVersion || "").length > 14 ? "is-long" : String(inst.mcVersion || "").length > 9 ? "is-compact" : ""}" ${String(inst.mcVersion || "").length > 9 ? `data-tooltip="${esc(inst.mcVersion)}"` : ""}>
                <span class="folder-front-ver-text">${esc(inst.mcVersion)}</span>${inst.favorite ? '<span class="folder-fav-heart" aria-label="已收藏">♥</span>' : ""}
              </div>
              <div class="folder-emblem" title="${esc(inst.customIcon ? inst.name : getIconMeta(inst.iconId).name)}">
                ${renderInstanceEmblemImg(inst)}
              </div>
            </div>
          </div>
        </div>
        <div class="folder-caption">
          <div class="folder-name">${esc(inst.name)}</div>
          <div class="folder-sub">${inst.modsCount} Mods · ${esc(inst.lastPlayed)}</div>
        </div>
      </div>
    `;
  }

  function renderFolderShelfContent(state, favPage) {
    const favs = getFavoriteInstances(state);
    const perPage = 6;
    const totalFavs = favs.length;
    const totalPages = Math.max(1, Math.ceil(totalFavs / perPage));
    const safePage = Math.min(Math.max(1, Number(favPage) || 1), totalPages);
    const pageItems = favs.slice((safePage - 1) * perPage, safePage * perPage);
    const pages = Array.from({ length: totalPages }, (_, i) => i + 1);

    return `
      <div class="folder-shelf">
        ${pageItems.length
          ? pageItems
              .map((inst, idx) =>
                renderFolderCard(
                  inst,
                  inst.id === state.activeInstanceId,
                  "folder-item",
                  `style="--ret-idx:${idx};"`,
                  `data-select-inst="${esc(inst.id)}"`
                )
              )
              .join("")
          : '<div class="folder-shelf-empty">空空如也X_X</div>'}
      </div>
      <div class="folder-shelf-pager" aria-label="收藏版本分页">
        <div class="pagination-controls pagination-controls--fav">
          <button class="chip-btn page-pill page-pill--nav" data-fav-page="${Math.max(1, safePage - 1)}" ${safePage <= 1 ? "disabled" : ""} type="button" aria-label="上一页收藏版本">←</button>
          <div class="pagination-pages">
            ${pages
              .map(
                (p) =>
                  `<button class="chip-btn page-pill ${p === safePage ? "is-active" : ""}" data-fav-page="${p}" type="button">${p}</button>`
              )
              .join("")}
          </div>
          <button class="chip-btn page-pill page-pill--nav" data-fav-page="${Math.min(totalPages, safePage + 1)}" ${safePage >= totalPages ? "disabled" : ""} type="button" aria-label="下一页收藏版本">→</button>
        </div>
      </div>
    `;
  }

  const LOADER_ORDER = ["Forge", "Fabric", "NeoForge", "Quilt", "Iris", "OptiFine", "原版材质"];
  function formatLoadersTag(loaders) {
    const arr = Array.isArray(loaders) ? [...loaders] : [String(loaders || "")];
    arr.sort((a, b) => {
      const ia = LOADER_ORDER.indexOf(a);
      const ib = LOADER_ORDER.indexOf(b);
      return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
    });
    return arr.filter(Boolean).join("/");
  }

  function formatSourceTag(source) {
    if (!source) return "";
    if (source === "两者都有" || source === "Modrinth + CurseForge" || source === "Modrinth + CF") {
      return "CurseForge/Modrinth";
    }
    return source;
  }

  function renderWavyChars(text) {
    return Array.from(text)
      .map((ch, i) => `<span style="transition-delay:${i * 28}ms">${ch === " " ? "&nbsp;" : esc(ch)}</span>`)
      .join("");
  }

  const COL_ICONS = {
    loader: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polygon points="12 2 20.5 7 20.5 17 12 22 3.5 17 3.5 7"/><circle cx="12" cy="12" r="2.8"/></svg>`,
    dl: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 4v11m0 0l-4-4m4 4l4-4M5 19h14"/></svg>`,
    ver: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 19V5m0 0l-4 4m4-4l4 4"/><line x1="6" y1="20" x2="18" y2="20"/></svg>`,
    source: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M3.6 9h16.8M3.6 15h16.8"/><path d="M12 3a13 13 0 0 1 0 18M12 3a13 13 0 0 0 0 18"/></svg>`
  };

  /* Crisp category line emblems for 3D sub-module cards */
  const CARD_ICONS = {
    cube: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8.5 4.9v8.2L12 21l-8.5-4.9V7.9L12 3z"/><path d="M12 12l8.5-4.1M12 12v9M12 12L3.5 7.9"/></svg>`,
    puzzle: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M19.4 13a1.65 1.65 0 0 0 .1 2.2l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.65 1.65 0 0 0-2.2.1 1.65 1.65 0 0 0-.5 1.2V20a2 2 0 1 1-4 0v-.2a1.65 1.65 0 0 0-1-1.5 1.65 1.65 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.65 1.65 0 0 0 .3-1.8 1.65 1.65 0 0 0-1.5-1H4a2 2 0 1 1 0-4h.2a1.65 1.65 0 0 0 1.5-1 1.65 1.65 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.65 1.65 0 0 0 1.8.3h.1a1.65 1.65 0 0 0 1-1.5V4a2 2 0 1 1 4 0v.2a1.65 1.65 0 0 0 1 1.5 1.65 1.65 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.65 1.65 0 0 0-.3 1.8v.1a1.65 1.65 0 0 0 1.5 1H20a2 2 0 1 1 0 4h-.2a1.65 1.65 0 0 0-1.4 0z"/></svg>`,
    pack: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2.5"/><circle cx="8.5" cy="9.5" r="1.5"/><path d="M21 15l-5-5-6 6-3-3-4 4"/></svg>`,
    sun: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/></svg>`,
    box: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z"/><path d="M3.3 7l8.7 5 8.7-5M12 22V12M7.5 4.21l9 5.19"/></svg>`,
    layers: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 12l10 5 10-5"/><path d="M2 17l10 5 10-5"/></svg>`,
    toggle: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="6" width="20" height="12" rx="6"/><circle cx="16" cy="12" r="3"/></svg>`,
    palette: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="13.5" cy="6.5" r=".5" fill="currentColor"/><circle cx="17.5" cy="10.5" r=".5" fill="currentColor"/><circle cx="8.5" cy="7.5" r=".5" fill="currentColor"/><circle cx="6.5" cy="12.5" r=".5" fill="currentColor"/><path d="M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10c.92 0 1.75-.74 1.75-1.67 0-.42-.16-.83-.44-1.13-.29-.3-.44-.7-.44-1.14 0-.92.75-1.67 1.67-1.67H16a6 6 0 0 0 6-6c0-4.96-4.5-8.39-10-8.39z"/></svg>`,
    key: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="5.5"/><path d="M21 2l-9.6 9.6M15.5 7.5l3 3L22 7l-3-3"/></svg>`,
    gauge: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 14l4-4"/><path d="M3.34 19a10 10 0 1 1 17.32 0"/></svg>`,
    terminal: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>`,
    globe: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M2 12h20"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>`
  };

  function getCardIconSvg(iconKey) {
    return CARD_ICONS[iconKey] || CARD_ICONS.cube;
  }

  function renderQuickPanel(state, active, activeQuickPanel, viewOpts) {
    if (!activeQuickPanel) return "";

    if (activeQuickPanel === "overview") {
      const hasCustomIcon = Boolean(active.customIcon);
      const packIconSrc = active.packIcon || active.customIcon || "";
      const packIconTile = packIconSrc
        ? `
          <button
            class="icon-choice-btn is-pack-icon ${hasCustomIcon ? "is-selected" : ""}"
            data-set-icon="__pack__"
            type="button"
            title="整合包官方图标"
            aria-label="整合包官方图标"
          >
            <img src="${esc(packIconSrc)}" alt="整合包官方图标" loading="eager" />
          </button>
        `
        : "";
      const iconChoices =
        packIconTile +
        Object.entries(MC_ICONS)
          .map(
            ([key, item]) => `
          <button
            class="icon-choice-btn ${!hasCustomIcon && (active.iconId || "grass") === key ? "is-selected" : ""}"
            data-set-icon="${key}"
            type="button"
            title="${esc(item.name)}"
            aria-label="${esc(item.name)}"
          >
            <img src="${esc(item.src)}" alt="${esc(item.name)}" loading="eager" />
          </button>
        `
          )
          .join("");

      return `<div class="quick-panel-wrap">
        <div class="quick-panel" id="quick-panel" aria-label="概览">
          <div class="quick-panel-head">
            <div class="quick-panel-title"><span>概览</span></div>
            ${renderCollapseBtn("data-quick-close", "收起")}
          </div>
          <div class="quick-kv-grid">
            <div class="quick-kv">
              <div class="quick-kv-label">加载器</div>
              <div class="quick-kv-val">${esc(active.loader)}</div>
            </div>
            <div class="quick-kv">
              <div class="quick-kv-label">加载器版本</div>
              <div class="quick-kv-val">${esc(active.loaderVersion)}</div>
            </div>
            <div class="quick-kv">
              <div class="quick-kv-label">游戏版本</div>
              <div class="quick-kv-val">${esc(active.mcVersion)}</div>
            </div>
          </div>
          <div class="icon-picker-label">版本名称</div>
          <div class="overview-name-row">
            <input
              class="overview-name-input"
              id="quick-instance-name-input"
              type="text"
              maxlength="80"
              value="${esc(active.name)}"
              placeholder="输入自定义版本名称"
              aria-label="自定义版本名称"
            />
            <button
              class="overview-name-save"
              id="btn-save-instance-name"
              type="button"
              title="保存版本名称"
              aria-label="保存版本名称"
            >
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <polyline points="20 6 9 17 4 12"/>
              </svg>
            </button>
          </div>
          <div class="icon-picker-label">版本图标</div>
          <div class="icon-picker-grid">${iconChoices}</div>

          <div class="color-picker-label">文件夹颜色</div>
          ${(() => {
            const activeColorId = active.folderColor || "";
            const isCustomColor = activeColorId === "custom";
            return `
          <div class="folder-color-row">
            <div class="cobra-palette-card">
              <div class="cobra-palette" role="listbox" aria-label="文件夹预设配色">
                ${FOLDER_PALETTES.map(
                  (p) => `
                  <button
                    class="cobra-color ${activeColorId === p.id ? "is-selected" : ""}"
                    data-set-folder-color="${esc(p.id)}"
                    style="--sw-top:${esc(p.top)}; --sw-bot:${esc(p.bottom)}; background:linear-gradient(180deg, ${esc(p.top)}, ${esc(p.bottom)});"
                    type="button"
                    aria-label="${esc(p.name)}"
                    aria-selected="${activeColorId === p.id ? "true" : "false"}"
                  ></button>`
                ).join("")}
              </div>
            </div>
            <button
              class="emu-btn emu-btn--sm cobra-custom-btn ${isCustomColor ? "has-custom-color" : ""}"
              id="btn-open-custom-color"
              data-custom-hex="${esc(active.customFolderHex || "#e67e22")}"
              data-tooltip="自定义文件夹颜色"
              aria-label="自定义文件夹颜色"
              aria-haspopup="dialog"
              aria-expanded="false"
              type="button"
            >
              <span class="filled" aria-hidden="true"></span>
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
                <line x1="5" y1="7" x2="19" y2="7"/>
                <line x1="5" y1="12" x2="19" y2="12"/>
                <line x1="5" y1="17" x2="19" y2="17"/>
              </svg>
            </button>
          </div>`;
          })()}
        </div>
      </div>`;
    }

    if (activeQuickPanel === "settings") {
      const effectiveMemMb = active.useGlobalSettings ? state.memoryMb : (active.memoryMb || state.memoryMb);
      const effectiveAutoMem = active.useGlobalSettings ? Boolean(state.autoMemory) : Boolean(active.autoMemory);
      const effectiveJvmArgs = active.useGlobalSettings ? state.customJvmArgs : (active.jvmArgs || state.customJvmArgs);
      const selectedJava = (state.javas || []).find((j) => j.id === active.javaId) || (state.javas || [])[0] || { version: "21.0.5", vendor: "-", arch: "x64" };

      return `<div class="quick-panel-wrap">
        <div class="quick-panel quick-panel--settings" id="quick-panel" aria-label="版本设置">
          <div class="quick-panel-head">
            <div class="quick-panel-title"><span>版本设置</span></div>
            <div class="quick-panel-head-actions">
              <div class="quick-global-toggle-wrap">
                <span class="quick-global-label">采用全局设置</span>
                ${renderMothToggle(Boolean(active.useGlobalSettings), 'id="btn-apply-global-settings"', "采用全局设置", true)}
              </div>
              ${renderCollapseBtn("data-quick-close", "收起")}
            </div>
          </div>

          <div class="quick-settings-body ${active.useGlobalSettings ? "is-global-dimmed" : ""}" id="quick-settings-body">
            <div class="quick-setting-block">
              <div class="quick-field-row-head">
                <span class="quick-field-label" style="margin-bottom:0;">JAVA</span>
                <span class="quick-java-meta" id="quick-java-meta">Java ${esc(selectedJava.version)} · ${esc(selectedJava.vendor)}</span>
              </div>
              <div class="quick-gooey-wrap">
                ${renderGooeyJavaNavHtml("quick-gooey-nav", state.javas, active.javaId, "data-select-quick-java")}
              </div>
            </div>

            <div class="quick-setting-block">
              <div class="quick-field-row-head">
                <label class="quick-field-label" style="margin-bottom:0;" for="quick-ram-slider">内存分配</label>
                <div class="quick-global-toggle-wrap">
                  <span class="quick-global-label">自动分配</span>
                  ${renderMothToggle(effectiveAutoMem, 'id="btn-toggle-quick-automem"', "切换当前版本自动分配内存", true)}
                </div>
              </div>
              <div class="ram-alloc-body ${effectiveAutoMem ? "is-auto-dimmed" : ""}" id="quick-ram-controls">
                ${renderRareMemoryBlockHtml({
                  counterId: "quick-ram-counter",
                  sliderId: "quick-ram-slider",
                  target: "quick",
                  valueMb: effectiveMemMb
                })}
              </div>
            </div>

            <div class="quick-setting-block" style="margin-bottom:0;">
              <label class="quick-field-label" for="quick-jvm-args">JVM 启动参数</label>
              <div class="quick-jvm-presets" style="display:flex; flex-wrap:wrap; gap:8px; margin-bottom:10px;">
                <button class="chip-btn page-pill ${(effectiveJvmArgs || "").includes("UseZGC") ? "is-active" : ""}" data-quick-jvm-preset="-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8" type="button">Gen-ZGC</button>
                <button class="chip-btn page-pill ${(effectiveJvmArgs || "").includes("UseG1GC") ? "is-active" : ""}" data-quick-jvm-preset="-XX:+UseG1GC -XX:MaxGCPauseMillis=30 -Dfile.encoding=UTF-8" type="button">G1GC</button>
                <button class="chip-btn page-pill ${(effectiveJvmArgs || "").includes("UseShenandoahGC") ? "is-active" : ""}" data-quick-jvm-preset="-XX:+UseShenandoahGC -Dfile.encoding=UTF-8" type="button">Shenandoah</button>
              </div>
              <div class="jvm-textarea-row">
                <textarea class="quick-jvm-textarea" id="quick-jvm-args" placeholder="点击输入文本……" rows="2">${esc(effectiveJvmArgs)}</textarea>
              </div>
            </div>
          </div>
        </div>
      </div>`;
    }

    if (activeQuickPanel === "mods") {
      return `<div class="quick-panel-wrap">
        <div class="quick-panel" id="quick-panel" aria-label="Mod 管理">
          <div class="quick-panel-head">
            <div class="quick-panel-title"><span>Mod 管理</span> · ${state.installedMods.length}</div>
            ${renderCollapseBtn("data-quick-close", "收起")}
          </div>
          ${renderModManagerBody(state.installedMods, viewOpts && viewOpts.modQuery, (viewOpts && viewOpts.modPage) || 1, true)}
        </div>
      </div>`;
    }

    if (activeQuickPanel === "folders") {
      const rootLabel = active.externalPath || (active.isolated ? `versions\\${active.id}` : ".minecraft");
      const folders = [
        { label: "版本文件夹", sub: rootLabel, path: "" },
        { label: "Mod 文件夹", sub: "mods", path: "mods" },
        { label: "资源包文件夹", sub: "resourcepacks", path: "resourcepacks" },
        { label: "光影包文件夹", sub: "shaderpacks", path: "shaderpacks" },
        { label: "截图文件夹", sub: "screenshots", path: "screenshots" }
      ];

      return `<div class="quick-panel-wrap">
        <div class="quick-panel" id="quick-panel" aria-label="版本文件夹">
          <div class="quick-panel-head">
            <div class="quick-panel-title"><span>版本文件夹</span></div>
            ${renderCollapseBtn("data-quick-close", "收起")}
          </div>
          <div class="quick-folder-grid">
            ${folders
              .map(
                (f) => `
              <button class="quick-folder-btn" data-open-folder="${esc(f.path)}" data-tooltip="${esc(f.sub)}" type="button">
                <div>
                  <div class="name">${esc(f.label)}</div>
                  <div class="sub">${esc(f.sub)}</div>
                </div>
                <span class="muted">↗</span>
              </button>
            `
              )
              .join("")}
          </div>
        </div>
      </div>`;
    }

    return "";
  }

  function renderHubSection(hubConfig, state, activeSubRoute, query, viewOpts) {
    if (!hubConfig) return "";

    let integratedControls = "";
    if (hubConfig.key === "manage") {
      const accName = (state.account && state.account.loggedIn && state.account.username) || "匿名玩家";
      integratedControls = `
        <button class="chip-btn ${activeSubRoute === "auth-java" ? "is-active" : ""}" data-sub-route="auth-java" type="button">
          ${esc(accName)}
        </button>
        <button class="chip-btn ${activeSubRoute === "auth-java" ? "is-active" : ""}" data-sub-route="auth-java" type="button">
          Java · ${state.javas.length}
        </button>
      `;
    } else if (hubConfig.key === "diagnostics") {
      integratedControls = `
        <button class="chip-btn is-active" id="hub-diag-mem-pill" data-sub-route="runtime-diag" type="button">
          ${state.memoryMb} MB
        </button>
      `;
    }

    const masonryHtml =
      hubConfig.cards && hubConfig.cards.length > 0
        ? `
        <div class="masonry-row" id="masonry-row" style="--cols: ${hubConfig.cards.length};">
          ${hubConfig.cards
            .map(
              (c, idx) => `
            <div class="hub-card-stage" data-masonry-idx="${idx}">
              <button
                class="card-3d card-3d--hub ${activeSubRoute === c.route ? "is-selected" : ""}"
                data-sub-route="${esc(c.route)}"
                type="button"
              >
                <div class="index-box hub-emblem-box" aria-hidden="true">
                  ${getCardIconSvg(c.icon)}
                </div>
                <div class="content-box">
                  <div>
                    <span class="card-title">${esc(c.title)}</span>
                    <span class="card-title-sub">${esc(c.sub)}</span>
                  </div>
                </div>
              </button>
            </div>
          `
            )
            .join("")}
        </div>`
        : "";

    return `
      <section class="container hub-section" id="hub-section" aria-label="${esc(hubConfig.title)}">
        <div class="hub-header v-reveal">
          <div class="hub-title-wrap">
            <span class="hub-eyebrow">${esc(hubConfig.eyebrow)}</span>
            <h2 class="hub-title">${esc(hubConfig.title)}</h2>
          </div>

          <div class="hub-integrated-bar">
            ${integratedControls}
            ${renderCollapseBtn("data-hub-close", "收起")}
          </div>
        </div>

        ${masonryHtml}

        <div id="hub-workspace-slot">
          ${
            activeSubRoute
              ? `<div class="hub-workspace v-reveal" id="hub-workspace">${renderSubWorkspace(activeSubRoute, state, query, viewOpts)}</div>`
              : ""
          }
        </div>
      </section>
    `;
  }

  function renderLaunchMeter(launchProgress) {
    if (!launchProgress || launchProgress <= 0) return "";
    return `
      <div class="redcow-meter" aria-label="启动进度">
        <div class="redcow-meta">
          <span>${launchProgress >= 100 ? "已启动" : "启动中"}</span>
          <span>${launchProgress}%</span>
        </div>
        <div class="redcow-track" role="progressbar" aria-valuenow="${launchProgress}" aria-valuemin="0" aria-valuemax="100"></div>
      </div>
    `;
  }

  function renderModManagerBody(allMods, modQuery, modPage, isCompact) {
    const q = (modQuery || "").trim().toLowerCase();
    const filtered = (allMods || []).filter(
      (m) =>
        !q ||
        (m.name || "").toLowerCase().includes(q) ||
        (m.cnName || "").toLowerCase().includes(q) ||
        global.LauncherBridge.namePinyinMatches?.(q, m.cnName, m.pinyin) ||
        (m.file || "").toLowerCase().includes(q) ||
        (m.version || "").toLowerCase().includes(q)
    );
    const PAGE_SIZE = 20;
    const totalPages = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE));
    const page = Math.min(Math.max(1, modPage || 1), totalPages);
    const items = filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
    const searchId = isCompact ? "quick-mod-search" : "subpage-search";
    const uniquePages = [...new Set([1, page, totalPages])].sort((a, b) => a - b);
    const modPageTokens = [];
    uniquePages.forEach((p, idx) => {
      if (idx > 0 && p - uniquePages[idx - 1] > 1) modPageTokens.push("...");
      modPageTokens.push(p);
    });

    const pagerHtml =
      filtered.length > 0
        ? `
      <div class="pagination-bar pagination-bar--mod">
        <div class="pagination-info">共 ${filtered.length} 项 · 第 ${page} / ${totalPages} 页</div>
        <div class="pagination-controls pagination-controls--mod">
          <button class="chip-btn page-pill page-pill--nav" data-mod-page="${Math.max(1, page - 1)}" ${page <= 1 ? "disabled" : ""} type="button" aria-label="上一页">←</button>
          <div class="pagination-pages">
            ${modPageTokens
              .map((p) =>
                p === "..."
                  ? '<span class="page-ellipsis" aria-hidden="true">…</span>'
                  : `<button class="chip-btn page-pill ${p === page ? "is-active" : ""}" data-mod-page="${p}" type="button">${p}</button>`
              )
              .join("")}
          </div>
          <button class="chip-btn page-pill page-pill--nav" data-mod-page="${Math.min(totalPages, page + 1)}" ${page >= totalPages ? "disabled" : ""} type="button" aria-label="下一页">→</button>
        </div>
      </div>`
        : "";

    return `
      <div class="filter-toolbar" style="margin-bottom:10px;">
        <div class="warm-search-box" style="max-width:none;">
          <input class="warm-search-input ${modQuery ? "has-value" : ""}" id="${searchId}" type="text" required value="${esc(modQuery || "")}">
          <label class="warm-search-label" for="${searchId}">${renderWavyChars("点击输入文本……")}</label>
        </div>
      </div>

      ${pagerHtml}

      <div class="data-table-list">
        ${
          items
            .map(
              (m, idx) => `
          <div class="data-row data-row--modmanage is-clickable-mod ${isCompact ? "is-compact" : ""} ${m.enabled ? "" : "is-disabled-item"} v-row-reveal" style="--v-idx:${Math.min(idx, 10)};" data-mod-row="${esc(m.id)}" data-open-mod-detail="${esc(m.id)}" data-mod-slug="${esc(m.modId || "")}" data-mod-name="${esc(m.name || "")}" data-mod-file="${esc(m.file || "")}" role="button" tabindex="0" aria-label="查看 ${esc(m.cnName || m.name)} 详情">
            <span class="row-progress-bg" aria-hidden="true"></span>
            <div class="row-left">
              <div class="row-official-icon">
                <img src="${esc(m.icon)}" alt="" loading="lazy" decoding="async" />
              </div>
              <div class="mod-text-col">
                <div class="marquee-clip" data-marquee-clip>
                  <div class="marquee-track row-title-pair">${renderTitlePair(m.cnName, m.name)}</div>
                </div>
                <div class="marquee-clip" data-marquee-clip>
                  <div class="marquee-track data-row-sub quick-mod-file" style="font-family:var(--font-mono); font-size:11px;">v${esc(m.version)} · ${esc(m.file)}${m.enabled ? "" : ".disabled"}</div>
                </div>
              </div>
            </div>
            ${isCompact ? "" : `<div class="col-tag">${COL_ICONS.ver}<span>v${esc(m.version)} · ${esc(formatSourceTag(m.source))}</span></div>`}
            <div class="mod-action-group">
              ${renderOpenFolderBtn("mods", "在 Mod 文件夹中打开", isCompact)}
              ${renderMothToggle(m.enabled, `data-toggle-mod="${esc(m.id)}"`, m.name, isCompact)}
              ${renderDeleteBtn(m.id, `删除 ${m.cnName || m.name}`, isCompact)}
            </div>
          </div>`
            )
            .join("") || '<div class="data-row data-row--empty"><div>空空如也X_X</div></div>'
        }
      </div>

      ${pagerHtml}
    `;
  }

  function renderPlayerAccountControl(state, menuOpen, offlineAddOpen) {
    const accounts = Array.isArray(state.accounts) ? state.accounts : [];
    const acc = state.account || {};
    const hasAccount = Boolean(acc.loggedIn && acc.username && accounts.length > 0);
    const anonSvg = `
      <svg class="player-anon-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M20 21a8 8 0 0 0-16 0"/>
        <circle cx="12" cy="8" r="4"/>
      </svg>`;
    const caretSvg = `
      <svg class="player-caret-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <polyline points="6 9 12 15 18 9"/>
      </svg>`;

    return `
      <div class="player-account-wrap" id="player-account-wrap">
        <button
          class="player-launch-badge ${hasAccount ? "" : "is-anonymous"}"
          id="btn-toggle-account-menu"
          type="button"
          aria-expanded="${Boolean(menuOpen)}"
          aria-label="${hasAccount ? `切换或管理账号 (${esc(acc.username)})` : "匿名状态 — 点击添加或管理账号"}"
        >
          <span class="player-badge-fill" aria-hidden="true"></span>
          ${
            hasAccount
              ? `<img class="player-head-img" src="${esc(acc.avatar || "assets/mc-icons/player-head.png")}" alt="${esc(acc.username)}" loading="eager" />
                 <span class="player-head-name">${esc(acc.username)}</span>
                 ${caretSvg}`
              : anonSvg
          }
        </button>

        <div class="account-popover ${menuOpen ? "is-open" : ""}" id="account-popover" role="dialog" aria-label="账号管理">
          <div class="account-popover-head">
            <span>账号管理</span>
            <span class="account-count-pill">${accounts.length}</span>
          </div>
          <div class="account-list">
            ${
              accounts.length > 0
                ? accounts
                    .map(
                      (item, idx) => `
                <div class="account-row ${acc.id === item.id ? "is-current" : ""}" data-acc-row="${esc(item.id)}" style="--acc-idx:${Math.min(idx, 6)};">
                  <span class="account-row-bg" aria-hidden="true"></span>
                  <button class="account-select-btn" data-switch-account="${esc(item.id)}" type="button">
                    <img class="account-row-avatar" src="${esc(item.avatar || "assets/mc-icons/player-head.png")}" alt="${esc(item.username)}" />
                    <span class="account-row-info">
                      <span class="account-row-name">${esc(item.username)}</span>
                      <span class="account-row-type">${esc(item.type)}</span>
                    </span>
                  </button>
                  <button
                    class="emu-delete-btn emu-delete-btn--sm"
                    data-delete-account="${esc(item.id)}"
                    aria-label="删除账号 ${esc(item.username)}"
                    title="删除账号"
                    type="button"
                  >
                    <svg class="emu-del-icon" viewBox="0 0 448 512" aria-hidden="true">
                      <path d="M135.2 17.7L128 32H32C14.3 32 0 46.3 0 64S14.3 96 32 96H416c17.7 0 32-14.3 32-32s-14.3-32-32-32H320l-7.2-14.3C307.4 6.8 296.3 0 284.2 0H163.8c-12.1 0-23.2 6.8-28.6 17.7zM416 128H32L53.2 467c1.6 25.3 22.6 45 47.9 45H346.9c25.3 0 46.3-19.7 47.9-45L416 128z"/>
                    </svg>
                  </button>
                </div>`
                    )
                    .join("")
                : '<div class="account-empty">空空如也X_X</div>'
            }
          </div>
          <div class="account-add-actions">
            <button class="btn-outline account-type-btn" id="btn-add-ms-account" type="button">正版登录</button>
            <button class="btn-solid account-type-btn ${offlineAddOpen ? "is-active" : ""}" id="btn-show-offline-add" type="button">+ 离线角色</button>
          </div>
          <div class="account-offline-drawer ${offlineAddOpen ? "is-open" : ""}" id="account-offline-drawer">
            <div class="account-offline-drawer-inner">
              <div class="account-offline-form">
                <input class="account-add-input" id="account-offline-name" type="text" placeholder="点击输入文本……" maxlength="24" />
                <button class="chip-btn is-active" id="btn-confirm-offline-account" type="button">添加</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    `;
  }

  /* ------------------------------------------------------------------------
     1. Editorial Homepage View (Option A: Airy Gallery + 3D Interactive Folders)
     ------------------------------------------------------------------------ */
  function renderHomeView(state, launchProgress, activeQuickPanel, activeHubConfig, activeSubRoute, query, animateEmblem, viewOpts) {
    const active = getActiveInstance(state);
    const hasVersion = Array.isArray(state.instances) && state.instances.length > 0;
    const effectiveQuickPanel = hasVersion ? activeQuickPanel : null;
    const opts = viewOpts || {};
    const quickMetaMap = {
      overview: "概览",
      settings: "版本设置",
      mods: "Mod 管理",
      folders: "版本文件夹"
    };

    return `
      <!-- Airy Single-Screen Focal Hero -->
      <section class="hero-section container ${opts.isReturning ? "is-returning" : ""}">
        <div class="hero-grid">
          <!-- Left: Primary Launch Focal Column -->
          <div class="hero-left-col">
            <div id="player-account-slot">
              ${renderPlayerAccountControl(state, Boolean(opts.accountMenuOpen), Boolean(opts.offlineAddOpen))}
            </div>

            <div id="version-art-slot">
              ${renderVersionArtHero(active, Boolean(opts.animateStroke), state)}
            </div>

            ${
              (() => {
                const isPreSpawnPending = Boolean(hasVersion && opts.pendingLaunchIds && opts.pendingLaunchIds.has(state.activeInstanceId));
                return `
            <div class="launch-row">
              <button class="btn-launch ${hasVersion ? "" : "is-disabled"} ${isPreSpawnPending ? "is-hollow" : ""}" id="btn-launch-game" ${hasVersion ? "" : 'disabled aria-disabled="true"'} ${isPreSpawnPending ? 'data-cancel-pending-launch="true"' : ""} type="button">
                <span>${!hasVersion ? "暂无可启动版本" : isPreSpawnPending ? "取消" : "启动游戏"}</span>
                <span>${!hasVersion ? "→" : isPreSpawnPending ? "×" : "→"}</span>
              </button>`;
              })()
            }
              <div class="redcow-slot" id="redcow-slot">
                ${renderLaunchMeter(launchProgress)}
              </div>
            </div>

            <!-- chilly-eagle-55 Yellow-Black Version Quick Action Dock -->
            <div class="emu-dock-row" aria-label="快捷操作">
              <ul class="emu-action-dock">
                <li class="emu-item">
                  <button
                    class="emu-btn ${effectiveQuickPanel === "overview" ? "is-active" : ""} ${hasVersion ? "" : "is-disabled"}"
                    data-quick-panel="overview"
                    aria-label="概览"
                    ${hasVersion ? "" : 'disabled aria-disabled="true"'}
                    type="button"
                  >
                    <span class="filled" aria-hidden="true"></span>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <rect x="3" y="3" width="7" height="7" rx="1"/>
                      <rect x="14" y="3" width="7" height="7" rx="1"/>
                      <rect x="3" y="14" width="7" height="7" rx="1"/>
                      <path d="M14 17.5h7M17.5 14v7"/>
                    </svg>
                  </button>
                </li>

                <li class="emu-item">
                  <button
                    class="emu-btn ${effectiveQuickPanel === "settings" ? "is-active" : ""} ${hasVersion ? "" : "is-disabled"}"
                    data-quick-panel="settings"
                    aria-label="版本设置"
                    ${hasVersion ? "" : 'disabled aria-disabled="true"'}
                    type="button"
                  >
                    <span class="filled" aria-hidden="true"></span>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <line x1="4" y1="21" x2="4" y2="14"/>
                      <line x1="4" y1="10" x2="4" y2="3"/>
                      <line x1="12" y1="21" x2="12" y2="12"/>
                      <line x1="12" y1="8" x2="12" y2="3"/>
                      <line x1="20" y1="21" x2="20" y2="16"/>
                      <line x1="20" y1="12" x2="20" y2="3"/>
                      <line x1="1" y1="14" x2="7" y2="14"/>
                      <line x1="9" y1="8" x2="15" y2="8"/>
                      <line x1="17" y1="16" x2="23" y2="16"/>
                    </svg>
                  </button>
                </li>

                <li class="emu-item">
                  <button
                    class="emu-btn ${effectiveQuickPanel === "mods" ? "is-active" : ""} ${hasVersion ? "" : "is-disabled"}"
                    data-quick-panel="mods"
                    aria-label="Mod 管理"
                    ${hasVersion ? "" : 'disabled aria-disabled="true"'}
                    type="button"
                  >
                    <span class="filled" aria-hidden="true"></span>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <path d="M12 2l8.5 4.9v10.2L12 22l-8.5-4.9V6.9L12 2z"/>
                      <path d="M12 22V12m0 0l8.5-5.1M12 12L3.5 6.9"/>
                    </svg>
                  </button>
                </li>

                <li class="emu-item">
                  <button
                    class="emu-btn ${effectiveQuickPanel === "folders" ? "is-active" : ""} ${hasVersion ? "" : "is-disabled"}"
                    data-quick-panel="folders"
                    aria-label="版本文件夹"
                    ${hasVersion ? "" : 'disabled aria-disabled="true"'}
                    type="button"
                  >
                    <span class="filled" aria-hidden="true"></span>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                      <path d="M3 6a2 2 0 0 1 2-2h4.6l2 2.5H19a2 2 0 0 1 2 2V18a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6z"/>
                    </svg>
                  </button>
                </li>
              </ul>

              <div class="emu-dock-caption" id="quick-dock-caption">${effectiveQuickPanel ? `<strong>${quickMetaMap[effectiveQuickPanel]}</strong>` : ""}</div>

              ${(() => {
                const bgNames = {
                  none: "无",
                  festive: "冬日礼赞"
                };
                const bgId = state.bgPatternId === "festive" ? "festive" : "none";
                const bgLabel = bgNames[bgId];
                return `
              <button
                class="bg-pattern-btn emu-btn--bg-pattern ${bgId !== "none" ? "is-pattern-on" : ""}"
                id="btn-cycle-bg-pattern"
                data-bg-pattern="${esc(bgId)}"
                aria-label="切换版本背景（当前：${esc(bgLabel)}）"
                type="button"
              >
                <span class="bg-pattern-ring" aria-hidden="true"></span>
                <svg class="bg-pattern-svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <rect x="3" y="3" width="18" height="18" rx="3"/>
                  <circle cx="8.5" cy="8.5" r="1.5"/>
                  <path d="M21 15l-4.5-4.5L9 18"/>
                  <path d="M14 18l-2.5-2.5L7 20"/>
                </svg>
              </button>`;
              })()}
            </div>

            <div class="quick-panel-slot" id="quick-panel-slot">
              ${renderQuickPanel(state, active, effectiveQuickPanel, opts)}
            </div>

            <!-- Compact Interactive Portal Triggers -->
            <div class="micro-dock" aria-label="功能板块入口">
              <button class="micro-trigger ${activeHubConfig && activeHubConfig.key === "download" ? "is-active" : ""}" data-portal="download" type="button">
                <span class="micro-icon icon-glide" aria-hidden="true">
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M12 3v12m0 0l-5-5m5 5l5-5M4 20h16"/>
                  </svg>
                </span>
                <span>下载资源</span>
                <span class="micro-arrow">↓</span>
              </button>

              <button class="micro-trigger ${activeHubConfig && activeHubConfig.key === "manage" ? "is-active" : ""}" data-portal="manage" type="button">
                <span class="micro-icon icon-spin" aria-hidden="true">
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <circle cx="12" cy="12" r="3"/>
                    <path d="M12 2v3m0 14v3M2 12h3m14 0h3m-2.9-7.1l-2.1 2.1m-8 8l-2.1 2.1m0-12.2l2.1 2.1m8 8l2.1 2.1"/>
                  </svg>
                </span>
                <span>实例环境</span>
                <span class="micro-arrow">↓</span>
              </button>

              <button class="micro-trigger ${activeHubConfig && activeHubConfig.key === "diagnostics" ? "is-active" : ""}" data-portal="diagnostics" type="button">
                <span class="micro-icon icon-pulse" aria-hidden="true">
                  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M3 12h4l3-7 4 14 3-7h4"/>
                  </svg>
                </span>
                <span>运行诊断</span>
                <span class="micro-arrow">↓</span>
              </button>
            </div>
          </div>

          <!-- Right: Utility Circular Buttons (List & Download Queue) + 6 Interactive 3D Folders -->
          <div class="folder-zone" aria-label="版本实例文件夹">
            <div class="folder-zone-tools">
              <button class="emu-btn emu-btn--sm" id="btn-open-folder-gallery" aria-label="版本列表" title="版本列表" type="button">
                <span class="filled" aria-hidden="true"></span>
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <line x1="8" y1="6" x2="21" y2="6"/>
                  <line x1="8" y1="12" x2="21" y2="12"/>
                  <line x1="8" y1="18" x2="21" y2="18"/>
                  <circle cx="4" cy="6" r="1.2" fill="currentColor" stroke="none"/>
                  <circle cx="4" cy="12" r="1.2" fill="currentColor" stroke="none"/>
                  <circle cx="4" cy="18" r="1.2" fill="currentColor" stroke="none"/>
                </svg>
              </button>

              <button class="emu-btn emu-btn--sm emu-btn--badge" id="btn-open-download-queue" aria-label="查看下载列表" title="查看下载列表" type="button">
                <span class="filled" aria-hidden="true"></span>
                ${(state.downloadTasks || []).some((t) => t.status === "downloading") ? '<span class="emu-queue-dot" aria-hidden="true"></span>' : ""}
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <path d="M12 4v10"/>
                  <path d="M8.5 10.5L12 14l3.5-3.5"/>
                  <path d="M4 17v1.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V17"/>
                </svg>
              </button>
            </div>

            <div id="folder-shelf-region">
              ${renderFolderShelfContent(state, (opts && opts.favPage) || 1)}
            </div>
          </div>
        </div>
      </section>

      <div id="hub-outlet">${activeHubConfig ? renderHubSection(activeHubConfig, state, activeSubRoute, query, opts) : ""}</div>
    `;
  }

  function renderPaginationBar(page, totalPages, totalItems) {
    if (totalItems <= 0) return "";
    const pages = [];
    const start = Math.max(1, page - 1);
    const end = Math.min(totalPages, page + 1);
    if (start > 1) pages.push(1);
    if (start > 2) pages.push("...");
    for (let p = start; p <= end; p++) pages.push(p);
    if (end < totalPages - 1) pages.push("...");
    if (end < totalPages) pages.push(totalPages);

    return `
      <div class="pagination-bar pagination-bar--catalog">
        <div class="pagination-info">共 ${totalItems} 项 · 第 ${page} / ${totalPages} 页</div>
        <div class="pagination-controls pagination-controls--catalog">
          <button class="chip-btn page-pill page-pill--nav" data-page="${Math.max(1, page - 1)}" ${page <= 1 ? "disabled" : ""} type="button" aria-label="上一页">←</button>
          <div class="pagination-pages">
            ${pages
              .map((p) =>
                p === "..."
                  ? `<span class="page-ellipsis" aria-hidden="true">…</span>`
                  : `<button class="chip-btn page-pill ${p === page ? "is-active" : ""}" data-page="${p}" type="button">${p}</button>`
              )
              .join("")}
          </div>
          <button class="chip-btn page-pill page-pill--nav" data-page="${Math.min(totalPages, page + 1)}" ${page >= totalPages ? "disabled" : ""} type="button" aria-label="下一页">→</button>
        </div>
      </div>
    `;
  }

  function renderSubWorkspace(subKey, state, query, viewOpts) {
    if (subKey.startsWith("download-")) {
      return renderDownloadBody(subKey, state, query, viewOpts);
    }
    if (subKey.startsWith("manage-") || subKey === "auth-java") {
      return renderManageBody(subKey, state, query, viewOpts);
    }
    return renderDiagnosticsBody(subKey, state);
  }

  function isVanillaOnlyMcVersion(v) {
    if (!v) return false;
    const loaders = Array.isArray(v.loaders) ? v.loaders.filter((ld) => ld && ld !== "Vanilla") : [];
    return loaders.length === 0 || v.category === "ancient" || v.category === "april";
  }

  function getAllFilterMcVersions(state) {
    const map = new Map();
    const list = Array.isArray(state?.catalog?.versions) ? state.catalog.versions : [];
    list.forEach((v) => {
      if (v && v.id && !map.has(v.id)) {
        const badge = v.category === "ancient" ? "远古" : v.category === "snapshot" ? "快照" : v.category === "april" ? "愚人节" : "正式";
        map.set(v.id, { id: String(v.id), badge });
      }
    });
    const insts = Array.isArray(state?.instances) ? state.instances : [];
    insts.forEach((inst) => {
      if (inst?.mcVersion && !map.has(inst.mcVersion)) {
        map.set(inst.mcVersion, { id: String(inst.mcVersion), badge: "实例" });
      }
    });
    if (map.size === 0) {
      ["1.21.4", "1.21.1", "1.20.4", "1.20.1", "1.19.2", "1.18.2", "1.16.5", "1.12.2"].forEach((id) => {
        map.set(id, { id, badge: "正式" });
      });
    }
    return [...map.values()];
  }

  const HOT_MC_VERSIONS = [
    "26.3", "26.2", "26.1",
    "1.21.11", "1.21.8", "1.21.4", "1.21.1",
    "1.20.1", "1.19.2", "1.18.2", "1.16.5",
    "1.15.2", "1.14.4", "1.13.2", "1.12.2",
    "1.11.2", "1.10.2", "1.9.2", "1.8.9", "1.7.10"
  ];

  const RESOURCE_CATEGORY_TREES = {
    mods: [
      { id: "all", label: "全部" },
      { id: "worldgen", label: "世界元素" },
      { id: "biomes", label: "生物群系", sub: true },
      { id: "dimensions", label: "维度", sub: true },
      { id: "ores", label: "矿物与资源", sub: true },
      { id: "structures", label: "天然结构", sub: true },
      { id: "mobs", label: "生物", sub: true },
      { id: "technology", label: "科技" },
      { id: "logistics", label: "管道与物流", sub: true },
      { id: "automation", label: "自动化", sub: true },
      { id: "energy", label: "能源", sub: true },
      { id: "redstone", label: "红石", sub: true },
      { id: "processing", label: "资源处理", sub: true },
      { id: "storage", label: "仓储", sub: true },
      { id: "magic", label: "魔法" },
      { id: "adventure", label: "冒险与探索" },
      { id: "equipment", label: "装备与战斗", sub: true },
      { id: "transportation", label: "交通与运输", sub: true },
      { id: "minigame", label: "小游戏", sub: true },
      { id: "horror", label: "恐怖", sub: true },
      { id: "food", label: "食物与烹饪" },
      { id: "farming", label: "农业", sub: true },
      { id: "decoration", label: "装饰与建筑" },
      { id: "optimization", label: "性能优化" },
      { id: "utility", label: "实用与界面" },
      { id: "map", label: "地图与信息", sub: true },
      { id: "game-mechanics", label: "游戏机制", sub: true },
      { id: "bugfix", label: "漏洞修复", sub: true },
      { id: "social", label: "多人与社交", sub: true },
      { id: "management", label: "服务器管理", sub: true },
      { id: "economy", label: "经济", sub: true },
      { id: "library", label: "支持库 / API" },
      { id: "addons", label: "附属模组" }
    ],
    modpacks: [
      { id: "all", label: "全部" },
      { id: "multiplayer", label: "多人" },
      { id: "optimization", label: "性能优化" },
      { id: "lightweight", label: "轻量" },
      { id: "challenging", label: "硬核" },
      { id: "combat", label: "战斗" },
      { id: "quests", label: "任务" },
      { id: "technology", label: "科技" },
      { id: "magic", label: "魔法" },
      { id: "adventure", label: "冒险" },
      { id: "kitchen-sink", label: "水槽包" },
      { id: "exploration", label: "探索" },
      { id: "minigame", label: "小游戏" },
      { id: "sci-fi", label: "科幻" },
      { id: "skyblock", label: "空岛" },
      { id: "vanilla-plus", label: "原版改良" },
      { id: "horror", label: "恐怖" }
    ],
    packs: [
      { id: "all", label: "全部" },
      { groupHeader: true, label: "风格" },
      { id: "vanilla-like", label: "原版风", sub: true },
      { id: "realistic", label: "写实风", sub: true },
      { id: "modern", label: "现代风", sub: true },
      { id: "medieval", label: "中世纪", sub: true },
      { id: "steampunk", label: "蒸汽朋克", sub: true },
      { id: "themed", label: "主题化", sub: true },
      { id: "simplistic", label: "简洁", sub: true },
      { id: "decoration", label: "装饰", sub: true },
      { id: "combat", label: "战斗", sub: true },
      { id: "utility", label: "实用", sub: true },
      { id: "tweaks", label: "改良", sub: true },
      { id: "cursed", label: "鬼畜", sub: true },
      { id: "modded", label: "模组支持", sub: true },
      { groupHeader: true, label: "特性" },
      { id: "animated", label: "动态材质", sub: true },
      { id: "gui", label: "界面 GUI", sub: true },
      { id: "audio", label: "音效", sub: true },
      { id: "fonts", label: "字体", sub: true },
      { id: "models", label: "3D 模型", sub: true },
      { id: "core-shaders", label: "核心着色器", sub: true },
      { groupHeader: true, label: "分辨率" },
      { id: "8x-", label: "8x 及以下", sub: true },
      { id: "16x", label: "16x", sub: true },
      { id: "32x", label: "32x", sub: true },
      { id: "64x", label: "64x", sub: true },
      { id: "128x", label: "128x", sub: true },
      { id: "256x", label: "256x", sub: true },
      { id: "512x+", label: "512x 及以上", sub: true }
    ],
    shaders: [
      { id: "all", label: "全部" },
      { groupHeader: true, label: "风格" },
      { id: "vanilla-like", label: "原版风", sub: true },
      { id: "fantasy", label: "幻想风", sub: true },
      { id: "realistic", label: "写实风", sub: true },
      { id: "semi-realistic", label: "半写实风", sub: true },
      { id: "cartoon", label: "卡通风", sub: true },
      { id: "cursed", label: "鬼畜 / 猎奇", sub: true },
      { groupHeader: true, label: "特性" },
      { id: "colored-lighting", label: "彩色光照", sub: true },
      { id: "path-tracing", label: "路径追踪", sub: true },
      { id: "pbr", label: "PBR", sub: true },
      { id: "reflections", label: "反射", sub: true },
      { id: "atmosphere", label: "大气效果", sub: true },
      { id: "bloom", label: "泛光", sub: true },
      { id: "foliage", label: "植被晃动", sub: true },
      { id: "shadows", label: "动态阴影", sub: true },
      { groupHeader: true, label: "性能负荷" },
      { id: "potato", label: "极低", sub: true },
      { id: "low", label: "低", sub: true },
      { id: "medium", label: "中", sub: true },
      { id: "high", label: "高", sub: true },
      { id: "screenshot", label: "截图级", sub: true }
    ]
  };

  const RESOURCE_CATEGORY_SECONDARY = {
    packs: {
      title: "分辨率",
      items: [
        { id: "all", label: "全部" },
        { id: "8x-", label: "8x 及以下" },
        { id: "16x", label: "16x" },
        { id: "32x", label: "32x" },
        { id: "64x", label: "64x" },
        { id: "128x", label: "128x" },
        { id: "256x", label: "256x" },
        { id: "512x+", label: "512x 及以上" }
      ]
    },
    shaders: {
      title: "性能负荷",
      items: [
        { id: "all", label: "全部" },
        { id: "potato", label: "极低" },
        { id: "low", label: "低" },
        { id: "medium", label: "中" },
        { id: "high", label: "高" },
        { id: "screenshot", label: "截图级" }
      ]
    }
  };

  function subKeyToResourceCat(subKey) {
    if (subKey === "download-packs") return "packs";
    if (subKey === "download-shaders") return "shaders";
    if (subKey === "download-modpacks") return "modpacks";
    return "mods";
  }

  function getResourceCategoryLabel(catKey, tagId, subTagId) {
    const id = tagId || "all";
    const subId = subTagId || "all";
    const list = RESOURCE_CATEGORY_TREES[catKey] || RESOURCE_CATEGORY_TREES.mods;
    const hit = id !== "all" ? list.find((item) => item.id === id) : null;
    const sec = RESOURCE_CATEGORY_SECONDARY[catKey];
    const secHit = sec && subId !== "all" ? sec.items.find((item) => item.id === subId) : null;
    if (hit && secHit) return `${hit.label} · ${secHit.label}`;
    if (hit) return hit.label;
    if (secHit) return secHit.label;
    return "全部";
  }

  function renderCategoryOptionListHtml(items, activeTag, attrName = "data-filter-cat") {
    const cur = activeTag || "all";
    return items
      .map((item) => {
        if (item.groupHeader) {
          return `<div class="cat-tray-group-head" role="presentation">${esc(item.label)}</div>`;
        }
        const isActive = cur === item.id;
        return `<button class="mirror-tray-item ${isActive ? "is-active" : ""}" ${attrName}="${esc(item.id)}" aria-selected="${isActive}" type="button" role="option"><span>${esc(item.label)}</span></button>`;
      })
      .join("");
  }

  function renderResourceCategoryTrayHtml(catKey, activeTag, activeSubTag) {
    const rawList = RESOURCE_CATEGORY_TREES[catKey] || RESOURCE_CATEGORY_TREES.mods;
    const secondary = RESOURCE_CATEGORY_SECONDARY[catKey];
    if (!secondary) {
      return `<div class="mcver-tray-list" id="cat-tray-list">${renderCategoryOptionListHtml(rawList, activeTag, "data-filter-cat")}</div>`;
    }
    const secIds = new Set(secondary.items.filter((x) => x.id !== "all").map((x) => x.id));
    const primaryList = rawList.filter((item) => !(item.groupHeader && item.label === secondary.title) && !secIds.has(item.id));
    return `
      <div class="mcver-dual-grid">
        <div class="mcver-dual-col">
          <div class="mcver-col-head">类型</div>
          <div class="mcver-tray-list" id="cat-tray-list">
            ${renderCategoryOptionListHtml(primaryList, activeTag, "data-filter-cat")}
          </div>
        </div>
        <div class="mcver-dual-col mcver-dual-col--hot">
          <div class="mcver-col-head">${esc(secondary.title)}</div>
          <div class="mcver-tray-list" id="cat-tray-secondary-list">
            ${renderCategoryOptionListHtml(secondary.items, activeSubTag, "data-filter-subcat")}
          </div>
        </div>
      </div>`;
  }

  function renderHotMcVersionListHtml(mcVersionFilter) {
    const active = mcVersionFilter || "all";
    return HOT_MC_VERSIONS.map(
      (v) => `
        <button class="mirror-tray-item ${active === v ? "is-active" : ""}" data-filter-mc="${esc(v)}" aria-selected="${active === v}" type="button" role="option">
          <span>${esc(v)}</span>
        </button>`
    ).join("");
  }

  function renderMcVersionTrayListHtml(state, mcVersionFilter, searchText = "") {
    const allMcVersions = getAllFilterMcVersions(state);
    const active = mcVersionFilter || "all";
    const q = String(searchText || "").trim();
    const qLower = q.toLowerCase();
    const filtered = qLower
      ? allMcVersions.filter((v) => v.id.toLowerCase().includes(qLower))
      : allMcVersions;
    const exactHit = qLower && allMcVersions.some((v) => v.id.toLowerCase() === qLower);
    const customRow = q && !exactHit
      ? `<button class="mirror-tray-item ${active === q ? "is-active" : ""}" data-filter-mc="${esc(q)}" aria-selected="${active === q}" type="button" role="option"><span>使用「${esc(q)}」</span><span class="mirror-ping">自定义</span></button>`
      : "";
    const allRow = !q
      ? `<button class="mirror-tray-item ${active === "all" ? "is-active" : ""}" data-filter-mc="all" aria-selected="${active === "all"}" type="button" role="option"><span>全部版本</span></button>`
      : "";
    const rows = filtered
      .map(
        (v) => `
          <button class="mirror-tray-item ${active === v.id ? "is-active" : ""}" data-filter-mc="${esc(v.id)}" aria-selected="${active === v.id}" type="button" role="option">
            <span>${esc(v.id)}</span>
            <span class="mirror-ping">${esc(v.badge)}</span>
          </button>`
      )
      .join("");
    return `${allRow}${customRow}${rows || (customRow ? "" : '<div class="cat-tray-group-head">无匹配版本</div>')}`;
  }

  function renderFilterPopoverHtml(subKey, viewOpts, state) {
    const opts = viewOpts || {};
    const filterOpen = Boolean(opts.filterOpen);
    if (!filterOpen) return "";
    const isVersions = subKey === "download-versions";
    const hideLoaderFilter = subKey === "download-packs" || subKey === "download-shaders";
    const resCatKey = subKeyToResourceCat(subKey);
    const loaderFilter = opts.loaderFilter || "all";
    const sourceFilter = opts.sourceFilter || "all";
    const sortFilter = opts.sortFilter || "downloads";
    const categoryFilter = opts.categoryFilter || "all";
    const subCategoryFilter = opts.subCategoryFilter || "all";
    const categoryLabel = getResourceCategoryLabel(resCatKey, categoryFilter, subCategoryFilter);
    const mcVersionFilter = opts.mcVersionFilter || "all";
    const mcLabel = mcVersionFilter === "all" ? "全部版本" : mcVersionFilter;
    const cubeSvg = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 2l8.5 4.9v10.2L12 22l-8.5-4.9V6.9L12 2z"/><path d="M12 22V12m0 0l8.5-5.1M12 12L3.5 6.9"/></svg>`;
    const tagSvg = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20.59 13.41l-7.17 7.17a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><line x1="7" y1="7" x2="7.01" y2="7"/></svg>`;
    const caretSvg = `<svg class="mirror-caret" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>`;

    return `
      <div class="filter-popover-wrap">
        <div class="filter-popover-row" id="filter-popover-row">
          ${
            !hideLoaderFilter
              ? `
          <div class="filter-group">
            <span class="filter-group-label">加载器</span>
            <div class="filter-group-options">
              ${["all", "Forge", "Fabric", "NeoForge"]
                .map(
                  (ld) =>
                    `<button class="chip-btn page-pill ${loaderFilter === ld ? "is-active" : ""}" data-filter-loader="${ld}" aria-pressed="${loaderFilter === ld}" type="button">${ld === "all" ? "全部" : ld}</button>`
                )
                .join("")}
            </div>
          </div>`
              : ""
          }
          ${
            !isVersions
              ? `
          <div class="filter-group">
            <span class="filter-group-label">来源</span>
            <div class="filter-group-options">
              ${["all", "Modrinth", "CurseForge"]
                .map(
                  (sr) =>
                    `<button class="chip-btn page-pill ${sourceFilter === sr ? "is-active" : ""}" data-filter-source="${sr}" aria-pressed="${sourceFilter === sr}" type="button">${sr === "all" ? "全部" : sr}</button>`
                )
                .join("")}
            </div>
          </div>
          <div class="filter-group">
            <span class="filter-group-label">排序</span>
            <div class="filter-group-options">
              ${[
                { id: "downloads", label: "最多下载" },
                { id: "newest", label: "最新发布" }
              ]
                .map(
                  (s) =>
                    `<button class="chip-btn page-pill ${sortFilter === s.id ? "is-active" : ""}" data-filter-sort="${s.id}" aria-pressed="${sortFilter === s.id}" type="button">${s.label}</button>`
                )
                .join("")}
            </div>
          </div>
          <div class="filter-group filter-group--category">
            <span class="filter-group-label">类型</span>
            <div class="filter-group-options filter-group-options--dual-triggers">
              <div class="mirror-starfish-wrap mcver-starfish-wrap cat-starfish-wrap" id="cat-starfish-wrap" aria-label="资源类型筛选">
                <button class="mirror-starfish-trigger mcver-starfish-trigger" id="cat-starfish-trigger" type="button" aria-expanded="false" aria-haspopup="listbox" aria-label="选择资源类型（${esc(categoryLabel)}）">
                  ${tagSvg}
                  <span class="mirror-trigger-label">
                    <span class="mirror-trigger-name">${esc(categoryLabel)}</span>
                  </span>
                  ${caretSvg}
                </button>
                <div class="mirror-tray mcver-tray cat-tray ${RESOURCE_CATEGORY_SECONDARY[resCatKey] ? "mcver-tray--dual cat-tray--dual" : ""}" id="cat-tray" role="listbox" aria-label="资源类型列表">
                  <div class="mirror-tray-bridge" aria-hidden="true"></div>
                  ${renderResourceCategoryTrayHtml(resCatKey, categoryFilter, subCategoryFilter)}
                </div>
              </div>
              <div class="filter-inline-pair">
                <span class="filter-group-label">Minecraft 版本</span>
                <div class="mirror-starfish-wrap mcver-starfish-wrap" id="mcver-starfish-wrap" aria-label="Minecraft 版本筛选">
                  <button class="mirror-starfish-trigger mcver-starfish-trigger" id="mcver-starfish-trigger" type="button" aria-expanded="false" aria-haspopup="listbox" aria-label="选择 Minecraft 版本（${esc(mcLabel)}）">
                    ${cubeSvg}
                    <span class="mirror-trigger-label">
                      <span class="mirror-trigger-name">${esc(mcLabel)}</span>
                    </span>
                    ${caretSvg}
                  </button>
                  <div class="mirror-tray mcver-tray mcver-tray--dual" id="mcver-tray" role="listbox" aria-label="Minecraft 版本列表">
                    <div class="mirror-tray-bridge" aria-hidden="true"></div>
                    <div class="mcver-dual-grid">
                      <div class="mcver-dual-col">
                        <div class="mcver-col-head">全部版本</div>
                        <div class="warm-search-box warm-search-box--compact">
                          <input class="warm-search-input" id="mcver-search-input" type="text" required value="" autocomplete="off" spellcheck="false" aria-label="输入 Minecraft 版本号" />
                          <label class="warm-search-label" for="mcver-search-input">${renderWavyChars("点击输入文本……")}</label>
                        </div>
                        <div class="mcver-tray-list" id="mcver-tray-list" data-lazy-mcver="true">
                          <button class="mirror-tray-item ${mcVersionFilter === "all" ? "is-active" : ""}" data-filter-mc="all" aria-selected="${mcVersionFilter === "all"}" type="button" role="option">
                            <span>全部版本</span>
                          </button>
                        </div>
                      </div>
                      <div class="mcver-dual-col mcver-dual-col--hot">
                        <div class="mcver-col-head">热门版本</div>
                        <div class="mcver-tray-list" id="mcver-hot-list">
                          ${renderHotMcVersionListHtml(mcVersionFilter)}
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>`
              : ""
          }
        </div>
      </div>`;
  }

  function renderPclVersionRow(v, state, labelPrefix) {
    const timeText = v.releaseTime || v.date || "";
    const subLabel = `${labelPrefix || v.type}，发布于 ${timeText}`;
    const directVanilla = isVanillaOnlyMcVersion(v);
    const rowAttr = directVanilla
      ? `data-direct-version-download="${esc(v.id)}" data-queue-name="Minecraft ${esc(v.id)}" data-queue-sub="${esc(v.id)}.jar" data-queue-icon="${esc(v.icon || "assets/mc-icons/grass.png")}" data-install-type="version" data-mc-version="${esc(v.id)}"`
      : `data-open-resource="versions:${esc(v.id)}"`;
    const btnAttrs = `data-queue-name="Minecraft ${esc(v.id)}" data-queue-sub="${esc(v.id)}.jar" data-queue-icon="${esc(v.icon || "assets/mc-icons/grass.png")}" data-install-type="version" data-mc-version="${esc(v.id)}"${directVanilla ? ' data-direct-version-btn="true"' : ""}`;
    return `
      <div class="data-row data-row--pcl" ${rowAttr}>
        <span class="row-progress-bg" aria-hidden="true"></span>
        <div class="row-left">
          <div class="row-official-icon is-pixel">
            <img src="${esc(v.icon)}" alt="${esc(v.id)}" loading="eager" />
          </div>
          <div style="min-width:0;">
            <div class="data-row-title">${esc(v.id)}</div>
            <div class="data-row-sub" style="font-family:var(--font-mono); font-size:11px;">${esc(subLabel)}</div>
          </div>
        </div>
        ${renderDownloadBtn(`正在通过 ${state.mirrorSource} 安装 Minecraft ${v.id} (${directVanilla ? "原版" : formatLoadersTag(v.loaders)})`, `下载 ${v.id}`, btnAttrs)}
      </div>`;
  }

  const versionCollator = new Intl.Collator("en", { numeric: true });
  const sortedVersionCatalogs = new WeakMap();
  const versionsPerGroupPage = 40;

  function compareVersionsDescending(left, right) {
    const a = String(left || "");
    const b = String(right || "");
    const parse = (version) => /^v?(\d+)\.(\d+)(?:\.(\d+))?(.*)$/i.exec(version);
    const aParts = parse(a);
    const bParts = parse(b);
    if (aParts && bParts) {
      for (let part = 1; part <= 3; part += 1) {
        const difference = Number(bParts[part] || 0) - Number(aParts[part] || 0);
        if (difference) return difference;
      }
      const releaseOrder = Number(!bParts[4]) - Number(!aParts[4]);
      if (releaseOrder) return releaseOrder;
    }
    return Number(Boolean(bParts)) - Number(Boolean(aParts)) || versionCollator.compare(b, a);
  }

  function filterGameVersions(state, query, loader) {
    const catalog = state.catalog.versions;
    if (!sortedVersionCatalogs.has(catalog)) {
      sortedVersionCatalogs.set(catalog, [...catalog].sort((a, b) => compareVersionsDescending(a.id, b.id)));
    }
    const q = (query || "").trim().toLowerCase();
    return sortedVersionCatalogs.get(catalog).filter(v =>
      (loader === "all" || v.loaders.includes(loader)) &&
      (!q || v.id.toLowerCase().includes(q) || v.loaders.join(" ").toLowerCase().includes(q) || v.type.toLowerCase().includes(q)));
  }

  function renderGameVersionGroupPage(items, state, category, offset = 0) {
    if (!items.length) return '<div class="data-row data-row--empty"><div>空空如也X_X</div></div>';
    const end = offset + versionsPerGroupPage;
    return items.slice(offset, end).map(v => renderPclVersionRow(v, state)).join("") +
      (end < items.length ? `<button class="chip-btn version-group-more" type="button"
        data-more-version-category="${esc(category)}" data-version-offset="${end}">加载更多版本（剩余 ${items.length - end}）</button>` : "");
  }

  function renderResourceRowHtml(it, catKey, state, idx) {
    const displayLabel = it.cnName || it.enName;
    const cascadeStyle = typeof idx === "number" ? `style="--row-idx:${idx};"` : "";
    const cascadeCls = typeof idx === "number" ? "data-row--cascade" : "";
    return `
        <div class="data-row data-row--resource ${cascadeCls}" ${cascadeStyle} data-open-resource="${esc(catKey)}:${esc(it.id)}">
          <span class="row-progress-bg" aria-hidden="true"></span>
          <div class="row-left">
            <div class="row-official-icon">
              <img src="${esc(it.icon)}" alt="" loading="lazy" decoding="async" />
            </div>
            <div class="row-title-pair">
              ${renderTitlePair(it.cnName, it.enName)}
            </div>
          </div>
          <div class="resource-row-meta">
            <div class="col-tag" title="${esc(formatLoadersTag(it.loaders))}">${COL_ICONS.loader}<span>${esc(formatLoadersTag(it.loaders))}</span></div>
            <div class="col-tag" title="${esc(it.downloads)}">${COL_ICONS.dl}<span>${esc(it.downloads)}</span></div>
            <div class="col-tag" title="${esc(it.mcVersions)}">${COL_ICONS.ver}<span>${esc(it.mcVersions)}</span></div>
            <div class="col-tag is-source" title="${esc(formatSourceTag(it.source))}">${COL_ICONS.source}<span>${esc(formatSourceTag(it.source))}</span></div>
          </div>
          ${renderDownloadBtn(`已加入下载队列：${displayLabel} (${state.mirrorSource})`, catKey === "modpacks" ? `选择 ${displayLabel} 的整合包构建` : `下载 ${displayLabel}`, `data-queue-name="${esc(displayLabel)}" data-queue-sub="${esc(it.enName)}" data-queue-icon="${esc(it.icon)}" data-project-id="${esc(it.id)}" data-cat-key="${esc(catKey)}"`)}
        </div>`;
  }

  function renderSkeletonRowsHtml(count) {
    return Array.from({ length: count || 8 }, (_, idx) => `
      <div class="data-row data-row--resource data-row--skeleton data-row--cascade" style="--row-idx:${idx};" aria-hidden="true">
        <div class="row-left">
          <div class="row-official-icon is-skeleton-block"></div>
          <div style="flex:1; min-width:0; display:flex; flex-direction:column; gap:6px;">
            <span class="is-skeleton-line" style="width:${42 + (idx % 4) * 11}%;"></span>
            <span class="is-skeleton-line is-short" style="width:${24 + (idx % 3) * 9}%;"></span>
          </div>
        </div>
        <div class="resource-row-meta">
          <span class="is-skeleton-line is-short" style="width:72px;"></span>
          <span class="is-skeleton-line is-short" style="width:54px;"></span>
        </div>
      </div>`).join("");
  }

  function renderDownloadResults(subKey, state, query, viewOpts) {
    const opts = viewOpts || {};
    const q = (query || "").trim().toLowerCase();
    const loaderFilter = opts.loaderFilter || "all";
    const sourceFilter = opts.sourceFilter || "all";
    const sortFilter = opts.sortFilter || "downloads";
    const mcVersionFilter = opts.mcVersionFilter || "all";
    const catKey = subKey.replace("download-", "");
    const isVersions = catKey === "versions";
    const hideLoaderFilter = catKey === "packs" || catKey === "shaders";
    const PAGE_SIZE = 50;

    let filtered = [];
    let pageItems = [];
    let totalItemsCount = 0;
    let totalPages = 1;
    let page = Math.max(1, opts.page || 1);

    const onlinePage = !isVersions && opts.onlineSearch?.catKey === catKey &&
      opts.onlineSearch.source === sourceFilter &&
      (opts.onlineSearch.sortBy || "downloads") === sortFilter &&
      (opts.onlineSearch.mcVersion || "all") === mcVersionFilter &&
      opts.onlineSearch.page === page ? opts.onlineSearch : null;

    if (isVersions) {
      filtered = filterGameVersions(state, query, loaderFilter);
    } else if (onlinePage) {
      pageItems = onlinePage.items || [];
      totalItemsCount = Number(onlinePage.totalHits) || pageItems.length;
      totalPages = Math.max(1, Math.ceil(totalItemsCount / PAGE_SIZE));
      page = Math.min(page, totalPages);
    } else {
      filtered = (state.catalog[catKey] || []).filter((it) => {
        const matchQ =
          !q ||
          (it.cnName || "").toLowerCase().includes(q) ||
          (it.enName || "").toLowerCase().includes(q) ||
          global.LauncherBridge.namePinyinMatches?.(q, it.cnName, it.pinyin) ||
          formatLoadersTag(it.loaders).toLowerCase().includes(q) ||
          formatSourceTag(it.source).toLowerCase().includes(q);
        const matchLoader = hideLoaderFilter || loaderFilter === "all" || (it.loaders || []).includes(loaderFilter);
        const srcText = formatSourceTag(it.source).toLowerCase();
        const matchSource = sourceFilter === "all" || srcText.includes(sourceFilter.toLowerCase());
        const matchMc = mcVersionFilter === "all" || String(it.mcVersions || "").includes(mcVersionFilter) || String(it.mcVersions || "").includes(mcVersionFilter.replace(/\.\d+$/, ""));
        return matchQ && matchLoader && matchSource && matchMc;
      });
      totalItemsCount = filtered.length;
      totalPages = Math.max(1, Math.ceil(totalItemsCount / PAGE_SIZE));
      page = Math.min(page, totalPages);
      pageItems = filtered.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);
    }

    let listBodyHtml = "";
    let paginationHtml = "";
    if (isVersions) {
      const latestRelease = filtered.find(v => v.type === "最新正式版") || filtered.find(v => v.category === "release");
      const latestSnapshot = filtered.find(v => v.type === "最新预览版") || filtered.find(v => v.category === "snapshot");
      const latestItems = [
        latestRelease ? { v: latestRelease, label: "最新正式版" } : null,
        latestSnapshot ? { v: latestSnapshot, label: "最新预览版" } : null
      ].filter(Boolean);
      const chevronSvg = `<svg class="pcl-category-chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>`;
      const openCats = opts.openPclCats || {};
      const autoExpand = Boolean(q || loaderFilter !== "all");
      const pclGroups = [
        { key: "release", title: "正式版", items: filtered.filter((v) => v.category === "release") },
        { key: "snapshot", title: "预览版", items: filtered.filter((v) => v.category === "snapshot") },
        { key: "ancient", title: "远古版", items: filtered.filter((v) => v.category === "ancient") },
        { key: "april", title: "愚人节版", items: filtered.filter((v) => v.category === "april") }
      ];

      listBodyHtml = `
        <div class="pcl-versions-stack">
          ${
            latestItems.length > 0
              ? `
          <div class="pcl-latest-card">
            <div class="pcl-latest-head">最新版本</div>
            <div class="pcl-latest-list">
              ${latestItems.map(({ v, label }) => renderPclVersionRow(v, state, label)).join("")}
            </div>
          </div>`
              : ""
          }
          ${pclGroups
            .map((grp, grpIdx) => {
              const isOpen = openCats[grp.key] ?? (autoExpand && grp.items.length > 0);
              return `
            <div class="pcl-category-group ${isOpen ? "is-open" : ""}" data-pcl-cat="${esc(grp.key)}" style="--v-idx:${grpIdx};">
              <button
                class="pcl-category-trigger"
                data-toggle-pcl-cat="${esc(grp.key)}"
                aria-expanded="${isOpen}"
                type="button"
              >
                <span class="pcl-category-title">${esc(grp.title)}</span>
                <span class="pcl-category-meta">
                  <span>${grp.items.length}</span>
                  ${chevronSvg}
                </span>
              </button>
              <div class="pcl-category-drawer" ${isOpen ? "" : "inert"}>
                <div class="pcl-category-drawer-inner">
                  <div class="pcl-category-list">
                    ${isOpen ? renderGameVersionGroupPage(grp.items, state, grp.key) : ""}
                  </div>
                </div>
              </div>
            </div>`;
            })
            .join("")}
        </div>`;
    } else {
      const isLoadingChunk = Boolean(onlinePage && onlinePage.loading && pageItems.length === 0);
      const isStreamingMore = Boolean(onlinePage && onlinePage.streaming && pageItems.length > 0);
      const rowsHtml = pageItems.map((it, idx) => renderResourceRowHtml(it, catKey, state, idx)).join("") +
        (isStreamingMore ? renderSkeletonRowsHtml(4) : "");

      const displayTotal = totalItemsCount || (opts.lastKnownTotalHits && onlinePage ? opts.lastKnownTotalHits : 0);
      const displayTotalPages = Math.max(page, totalPages, displayTotal > 0 ? Math.ceil(displayTotal / PAGE_SIZE) : 1);
      paginationHtml = displayTotal > 0
        ? renderPaginationBar(page, displayTotalPages, displayTotal)
        : (onlinePage?.loading ? `<div class="pagination-bar"><div class="pagination-info">第 ${page} 页 · 正在加载...</div></div>` : "");
      const emptyMessage = esc(onlinePage?.message || "空空如也X_X");
      const empty = isLoadingChunk
        ? renderSkeletonRowsHtml(8)
        : `<div class="data-row data-row--empty" role="status"><div>${emptyMessage}</div></div>`;
      listBodyHtml = `<div class="data-table-list" id="download-results-list">${rowsHtml || empty}</div>`;
    }

    return `${paginationHtml}${listBodyHtml}${paginationHtml}`;
  }

  function renderDownloadBody(subKey, state, query, viewOpts) {
    const opts = viewOpts || {};
    const titles = {
      "download-versions": { k: "GAME VERSIONS", t: "游戏版本" },
      "download-mods": { k: "MOD RESOURCES", t: "模组资源" },
      "download-packs": { k: "RESOURCE PACKS", t: "资源包" },
      "download-shaders": { k: "SHADER PACKS", t: "光影包" },
      "download-modpacks": { k: "MODPACKS", t: "整合包" }
    };
    const meta = titles[subKey] || titles["download-mods"];
    const hasActiveFilter = (opts.loaderFilter || "all") !== "all" ||
      (opts.sourceFilter || "all") !== "all" ||
      (opts.sortFilter || "downloads") !== "downloads" ||
      (opts.categoryFilter || "all") !== "all" ||
      (opts.subCategoryFilter || "all") !== "all" ||
      (opts.mcVersionFilter || "all") !== "all";
    return `
      <div class="breadcrumb-bar">
        <div>
          <div style="font-family:var(--font-mono); font-size:11px; color:var(--accent-primary);">${meta.k}</div>
          <h3 class="section-title-editorial" style="margin-top:4px;">${meta.t}</h3>
        </div>
      </div>

      <div class="filter-toolbar" id="resource-search-bar">
        <div class="warm-search-box">
          <input class="warm-search-input ${query ? "has-value" : ""}" id="subpage-search" type="text" required value="${esc(query || "")}">
          <label class="warm-search-label" for="subpage-search">${renderWavyChars("点击输入文本……")}</label>
        </div>
        ${renderFilterBtn(opts.filterOpen || hasActiveFilter)}
        ${renderMirrorStarfishControl(state)}
      </div>

      <div class="filter-popover-slot ${opts.filterOpen ? "is-open is-settled" : ""}" id="filter-popover-slot">
        ${renderFilterPopoverHtml(subKey, opts, state)}
      </div>

      <div id="resource-download-results">${renderDownloadResults(subKey, state, query, opts)}</div>
    `;
  }

  /* ------------------------------------------------------------------------
     2. Download Sub-Pages (Versions / Mods / Packs / Shaders / Modpacks)
     ------------------------------------------------------------------------ */
  function renderDownloadView(subKey, state, query, viewOpts) {
    return `<div class="container subpage-wrap">${renderDownloadBody(subKey, state, query, viewOpts)}</div>`;
  }

  /* ------------------------------------------------------------------------
     3. Instance Environment Sub-Page (Auth & Java Only)
     ------------------------------------------------------------------------ */
  function renderOfficialJavaDrawerInnerHtml(state, viewOpts) {
    const opts = viewOpts || {};
    const officialJavas = state.officialJavas || [];
    const JAVA_VENDORS = [
      { id: "temurin", label: "Eclipse Temurin", versions: ["25", "21", "17", "11", "8"] },
      { id: "oracle", label: "Oracle JDK", versions: ["25", "21", "17"] },
      { id: "zulu", label: "Azul Zulu", versions: ["25", "21", "17", "11", "8"] },
      { id: "microsoft", label: "Microsoft OpenJDK", versions: ["21", "17", "11"] }
    ];
    const activeVendorId = JAVA_VENDORS.some((v) => v.id === opts.javaVendor) ? opts.javaVendor : "temurin";
    const vendorMeta = JAVA_VENDORS.find((v) => v.id === activeVendorId) || JAVA_VENDORS[0];
    const filteredJavas = officialJavas.filter((j) => vendorMeta.versions.includes(String(j.version)));
    const customJavaPath = opts.javaInstallPath || "";
    return `
      <div class="java-installer-toolbar">
        <div class="java-vendor-pills" role="tablist" aria-label="Java 安装来源">
          ${JAVA_VENDORS.map((v) => `
            <button class="chip-btn ${v.id === activeVendorId ? "is-active" : ""}" data-java-vendor="${esc(v.id)}" type="button" role="tab" aria-selected="${v.id === activeVendorId}">${esc(v.label)}</button>`).join("")}
        </div>
        <div class="java-install-path-bar">
          <span class="java-install-path-text">安装位置：${esc(customJavaPath || "默认目录 (DCL\\runtime)")}</span>
          <div class="java-install-path-actions">
            <button class="chip-btn" id="btn-browse-java-dir" type="button">选择位置</button>
            ${customJavaPath ? '<button class="chip-btn" id="btn-reset-java-dir" type="button">恢复默认</button>' : ""}
          </div>
        </div>
      </div>
      <div class="java-official-list">
        ${filteredJavas
          .map(
            (item) => `
          <div class="java-official-row">
            <div>
              <div class="data-row-title">Java ${esc(item.fullVersion)} <span class="mod-tag">${esc(item.channel)}</span></div>
              <div class="data-row-sub">${esc(vendorMeta.label)} · ${esc(item.size)}</div>
            </div>
            ${renderDownloadBtn(
              `已添加 Java ${item.fullVersion} (${vendorMeta.label}) 下载任务`,
              `下载安装 Java ${item.fullVersion}`,
              `data-install-official-java="${esc(item.id)}" data-java-vendor="${esc(activeVendorId)}"`
            )}
          </div>`
          )
          .join("")}
      </div>`;
  }

  function renderManageBody(subKey, state, query, viewOpts) {
    const opts = viewOpts || {};
    const javas = state.javas || [];
    const activeJava = javas.find((j) => j.active) || javas[0] || { id: "", version: "21", vendor: "-", arch: "x64", path: "-" };
    const javaInstallerOpen = Boolean(opts.javaInstallerOpen);
    const javaScanPending = Boolean(opts.javaScanPending);

    return `
      <div class="breadcrumb-bar">
        <div>
          <div style="font-family:var(--font-mono); font-size:11px; color:var(--accent-primary);">AUTH &amp; JAVA RUNTIME</div>
          <h3 class="section-title-editorial" style="margin-top:4px;">账号与 Java</h3>
        </div>
      </div>

      <div class="panel-grid-2">
        <div class="spec-box v-reveal" style="--v-idx:0;">
          <h3>Microsoft OAuth2</h3>
          <div style="border-top:1px dotted var(--line-dotted); padding:14px 0; font-family:var(--font-mono); font-size:12px;">
            <div>玩家: <strong style="color:var(--accent-primary);">${esc((state.account && state.account.username) || "未登录")}</strong></div>
            <div style="margin-top:6px;">类型: ${esc((state.account && state.account.type) || "匿名状态")}</div>
            <div style="margin-top:6px; color:var(--text-muted);">UUID: ${esc((state.account && state.account.uuid) || "-")}</div>
          </div>
          <div style="display:flex; gap:10px; margin-top:12px;">
            <button class="chip-btn chip-btn--ms is-active" id="btn-microsoft-login" type="button">
              <svg class="ms-win-logo" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                <rect x="2" y="2" width="9.2" height="9.2" rx="1"/>
                <rect x="12.8" y="2" width="9.2" height="9.2" rx="1"/>
                <rect x="2" y="12.8" width="9.2" height="9.2" rx="1"/>
                <rect x="12.8" y="12.8" width="9.2" height="9.2" rx="1"/>
              </svg>
              <span>微软登录</span>
            </button>
            <button class="chip-btn" id="btn-refresh-token" type="button">刷新账号会话</button>
          </div>
        </div>

        <div class="spec-box v-reveal" style="--v-idx:1;">
          <div class="spec-box-head">
            <h3>本地 Java (<span id="java-count-badge">${javas.length}</span>)</h3>
          </div>

          <!-- ReactBits Gooey Nav Java Selector -->
          ${renderGooeyJavaNavHtml("java-gooey-nav", javas, activeJava.id, "data-select-java")}

          <div class="java-selected-card" id="java-selected-card">
            <div class="java-selected-top">
              <strong id="java-selected-title">Java ${esc(activeJava.version)} (${esc(activeJava.arch)})</strong>
              <span class="spec-priority-badge" id="java-selected-vendor">${esc(activeJava.vendor)}</span>
            </div>
            <div class="java-selected-path" id="java-selected-path">${esc(activeJava.path)}</div>
          </div>

          <div class="java-action-bar">
            <button class="chip-btn" id="btn-scan-local-java" type="button" aria-busy="${javaScanPending}" ${javaScanPending ? "disabled" : ""}>${javaScanPending ? "正在扫描本机磁盘…" : "查找本地 Java"}</button>
            <button class="chip-btn ${javaInstallerOpen ? "is-active" : ""}" id="btn-toggle-java-installer" type="button" aria-expanded="${javaInstallerOpen}">安装 Java</button>
          </div>

          <div class="java-official-drawer ${javaInstallerOpen ? "is-open" : ""}" id="java-official-drawer">
            <div class="java-official-drawer-inner">
              ${renderOfficialJavaDrawerInnerHtml(state, opts)}
            </div>
          </div>
        </div>
      </div>
    `;
  }

  function renderManageView(subKey, state, query, viewOpts) {
    return `<div class="container subpage-wrap">${renderManageBody(subKey, state, query, viewOpts)}</div>`;
  }

  /* ------------------------------------------------------------------------
     4. Runtime, Memory & Crash Diagnostics Sub-Pages
     ------------------------------------------------------------------------ */
  function renderDiagnosticsBody(subKey, state) {
    return `
      <div class="panel-grid-2 panel-grid-2--strict">
        <div class="spec-box v-reveal" style="--v-idx:0;">
          <div class="spec-box-head">
            <h3>内存分配</h3>
            <span class="spec-sub-tag">${(state.systemRamMb / 1024).toFixed(0)} GB</span>
          </div>
          <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:14px;">
            <span style="font-size:14px;">自动配置</span>
            ${renderMothToggle(state.autoMemory, 'id="btn-toggle-automem"', "切换自动配置", false)}
          </div>
          <div class="ram-alloc-body ${state.autoMemory ? "is-auto-dimmed" : ""}" id="diag-ram-controls">
            ${renderRareMemoryBlockHtml({
              counterId: "diag-ram-counter",
              sliderId: "ram-slider",
              target: "diag",
              valueMb: state.memoryMb
            })}
          </div>
        </div>

        <div class="spec-box v-reveal" style="--v-idx:1;">
          <h3>JVM 参数设置</h3>
          <div style="display:flex; flex-wrap:wrap; gap:8px; margin-bottom:12px;">
            <button class="chip-btn page-pill ${(state.customJvmArgs || "").includes("UseZGC") ? "is-active" : ""}" data-jvm-preset="-XX:+UseZGC -XX:+ZGenerational -Dfile.encoding=UTF-8" type="button">Gen-ZGC</button>
            <button class="chip-btn page-pill ${(state.customJvmArgs || "").includes("UseG1GC") ? "is-active" : ""}" data-jvm-preset="-XX:+UseG1GC -XX:MaxGCPauseMillis=30 -Dfile.encoding=UTF-8" type="button">G1GC</button>
            <button class="chip-btn page-pill ${(state.customJvmArgs || "").includes("UseShenandoahGC") ? "is-active" : ""}" data-jvm-preset="-XX:+UseShenandoahGC -Dfile.encoding=UTF-8" type="button">Shenandoah</button>
          </div>
          <label class="quick-field-label" for="diag-jvm-args-input">自定义参数</label>
          <div class="jvm-textarea-row">
            <textarea class="quick-jvm-textarea" id="diag-jvm-args-input" placeholder="点击输入文本……" rows="2">${esc(state.customJvmArgs)}</textarea>
          </div>
        </div>

        <div class="spec-box v-reveal" style="--v-idx:2;">
          <div class="spec-box-head">
            <h3>报错分析</h3>
            ${renderShowInWindowBtn("btn-open-crash-window", "窗口中显示")}
          </div>
          <div class="crash-log" id="diag-crash-log">
            ${
              Array.isArray(state.crashLines) && state.crashLines.length > 0
                ? state.crashLines
                    .map(
                      (line) =>
                        `<div class="${line.startsWith("↳") ? "crash-highlight" : ""}">${esc(line)}</div>`
                    )
                    .join("")
                : `<div>[23:18:04] [main/ERROR]: Mixin apply for mod sodium failed</div>
            <div class="crash-highlight">↳ 置信度 96%：Mixin 注入冲突</div>
            <div>· sodium-neoforge-0.6.3.jar &amp; iris-neoforge-1.8.1.jar</div>
            <div>· 建议更新 Sodium 至 0.6.5+</div>`
            }
          </div>
          <div style="display:flex; gap:10px; margin-top:14px;">
            <button class="chip-btn is-active" id="btn-locate-crash-mod" type="button">定位 Mod</button>
            <button class="chip-btn" id="btn-open-launcher-logs" type="button">打开日志目录</button>
            <button class="chip-btn" id="btn-export-crash-report" type="button">导出报告</button>
          </div>
        </div>
      </div>
    `;
  }

  function renderDiagnosticsView(subKey, state) {
    return `<div class="container subpage-wrap">${renderDiagnosticsBody(subKey, state)}</div>`;
  }

  /* ------------------------------------------------------------------------
     5. Standalone Scrollable Folder Gallery Page (Ventriloc Blue Card Choreography)
     ------------------------------------------------------------------------ */
  function renderInstanceGalleryView(state) {
    return `
      <section class="container folder-gallery-page" id="folder-gallery-page" aria-label="全部版本文件夹">
        <div class="breadcrumb-bar v-reveal is-inview">
          <div>
            <div style="font-family:var(--font-mono); font-size:11px; color:var(--accent-primary);">VERSION ARCHIVE // ALL INSTANCES (${state.instances.length})</div>
            <h1 class="section-title-editorial" style="margin-top:4px;">版本文件夹库</h1>
          </div>
          <button class="cc-btn cc-btn--import-folder" id="btn-import-instance-folder" type="button" title="导入版本文件夹" aria-label="导入版本文件夹">
            <span class="cc-box" aria-hidden="true">
              <span class="cc-elem"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 10v6m-3-3h6"/></svg></span>
              <span class="cc-elem"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 10v6m-3-3h6"/></svg></span>
            </span>
          </button>
        </div>

        <div class="folder-gallery-grid">
          ${getSortedInstances(state)
            .map((inst, idx) =>
              renderFolderCard(
                inst,
                inst.id === state.activeInstanceId,
                "gallery-folder-card",
                `style="--v-idx:${idx};"`,
                `data-gallery-pick-inst="${esc(inst.id)}"`
              )
            )
            .join("")}
        </div>
      </section>
    `;
  }

  /* ------------------------------------------------------------------------
     6. Standalone Download Queue View (Upward Slide Page)
     ------------------------------------------------------------------------ */
  function renderDownloadQueueView(state) {
    const allTasks = state.downloadTasks || [];
    let completedCount = 0;
    const tasks = allTasks.filter((t) => {
      if (t.status !== "completed") return true;
      completedCount += 1;
      return completedCount <= 10;
    });
    const downloadingCount = tasks.filter((t) => t.status === "downloading").length;

    return `
      <section class="container download-queue-page" id="download-queue-page" aria-label="下载列表">
        <div class="breadcrumb-bar v-reveal is-inview">
          <div>
            <div id="dl-active-eyebrow" style="font-family:var(--font-mono); font-size:11px; color:var(--accent-primary);">DOWNLOAD QUEUE // ACTIVE ${downloadingCount}</div>
            <h1 class="section-title-editorial" style="margin-top:4px;">下载列表</h1>
          </div>
        </div>

        <div class="data-table-list" id="dl-queue-list">
          ${
            tasks
              .map(
                (t, idx) => `
            <div class="data-row data-row--modmanage data-row--dlqueue v-row-reveal is-inview" data-dl-row="${esc(t.id)}" data-dl-status="${esc(t.status)}" style="--v-idx:${Math.min(idx, 10)};">
              <span class="row-progress-bg" aria-hidden="true"></span>
              <div class="row-left">
                <div class="row-official-icon">
                  <img src="${esc(t.icon)}" alt="${esc(t.name)}" loading="eager" />
                </div>
                <div style="min-width:0;">
                  <div class="row-title-pair">
                    <span class="data-row-title">${esc(t.name)}</span>
                    <span class="row-en-name">${esc(t.size)} · ${esc(t.source)}</span>
                  </div>
                  <div class="data-row-sub quick-mod-file" title="${t.status === "paused" ? esc(t.speed) : ""}" style="font-family:var(--font-mono); font-size:11px;">${esc(t.sub)} · ${t.status === "completed" ? esc(t.size) : esc(t.speed)}</div>
                </div>
              </div>

              <div class="dl-progress-cell">
                <div class="dl-task-progress" role="progressbar" aria-label="${esc(t.name)} 下载进度" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${t.progress}">
                  <div class="dl-task-progress-fill ${t.status === "completed" ? "is-done" : ""}" style="width:${t.progress}%"></div>
                </div>
                <span class="data-row-meta dl-progress-pct">${t.progress}%</span>
              </div>

              <div class="mod-action-group">
                ${
                  t.status === "completed"
                    ? '<span class="dl-state-spacer" aria-hidden="true"></span>'
                    : renderPauseResumeBtn(t.status === "downloading", t.id, t.name)
                }
                ${renderDeleteBtn(t.id, `移除 ${t.name}`, true).replace("data-delete-mod=", "data-delete-dl=")}
              </div>
            </div>`
              )
              .join("") || '<div class="data-row data-row--empty"><div>空空如也X_X</div></div>'
          }
        </div>
      </section>
    `;
  }

  /* ------------------------------------------------------------------------
     7. Resource Detail View (Rightward Slide + vincentsaisset.com Cutout + Accordion Drawer)
     ------------------------------------------------------------------------ */
  function getMotifSvgForLoader(loaderName) {
    const ld = String(loaderName || "").toLowerCase();
    if (ld.includes("forge") && !ld.includes("neo")) {
      return `<svg viewBox="0 0 200 200" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M35 80h130v26c0 18-24 28-44 28v26h30v20H49v-20h30v-26c-20 0-44-10-44-28V80z"/><path d="M22 62h156v18H22z"/></svg>`;
    }
    if (ld.includes("fabric")) {
      return `<svg viewBox="0 0 200 200" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="34" y="34" width="132" height="132" rx="18"/><path d="M34 78h132M34 122h132M78 34v132M122 34v132"/></svg>`;
    }
    return `<svg viewBox="0 0 200 200" fill="none" stroke="currentColor" stroke-width="1.4"><polygon points="100,18 178,62 178,138 100,182 22,138 22,62"/><circle cx="100" cy="100" r="38"/><line x1="100" y1="18" x2="100" y2="182"/></svg>`;
  }

  function renderCutoutWord(word) {
    const text = String(word || "");
    const parts = text.split(/\s+/);
    if (parts.length >= 2) {
      const first = parts.slice(0, parts.length - 1).join(" ");
      const last = parts[parts.length - 1];
      return `
        <span class="vdetail-cutout-solid">${esc(first)}</span>
        <span class="vdetail-cutout-pair">
          <span class="vdetail-cutout-stroke" aria-hidden="true">${esc(last)}</span>
          <span class="vdetail-cutout-fill">${esc(last)}</span>
        </span>`;
    }
    return `
      <span class="vdetail-cutout-pair">
        <span class="vdetail-cutout-stroke" aria-hidden="true">${esc(text)}</span>
        <span class="vdetail-cutout-fill">${esc(text)}</span>
      </span>`;
  }

  function getLoaderIncompatibility(loader, selectedBuilds) {
    const sel = selectedBuilds || {};
    const selectedLoaders = Object.keys(sel).filter((k) => Boolean(sel[k]));
    if (selectedLoaders.includes(loader)) return null;
    const exclusiveModLoaders = ["Forge", "NeoForge", "Fabric", "Quilt"];
    for (const other of selectedLoaders) {
      if (exclusiveModLoaders.includes(loader) && exclusiveModLoaders.includes(other) && loader !== other) {
        return other;
      }
      if (loader === "OptiFine" && (other === "NeoForge" || other === "Fabric" || other === "Quilt")) {
        return other;
      }
      if ((loader === "NeoForge" || loader === "Fabric" || loader === "Quilt") && other === "OptiFine") {
        return other;
      }
    }
    return null;
  }

  function renderFloatingDownloadBall(state) {
    if (!state || !state.hasUnseenDownloadBall) return "";
    const activeCount = (state.downloadTasks || []).filter((t) => t.status === "downloading").length;
    return `
      <button
        class="floating-dl-ball"
        id="floating-dl-ball"
        type="button"
        aria-label="查看进行中的下载任务"
        title="点击查看下载列表"
      >
        <span class="floating-dl-pulse" aria-hidden="true"></span>
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M12 4v10"/>
          <path d="M8.5 10.5L12 14l3.5-3.5"/>
          <path d="M4 17v1.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V17"/>
        </svg>
        <span class="floating-dl-badge">${Math.max(1, activeCount)}</span>
      </button>
    `;
  }

  function renderDetailMcListHtml(detail, activeLoader, expandedMcVersion, state, selectedVersionBuilds) {
    const mcGroups = [...(detail.loaderTrees[activeLoader] || [])]
      .sort((a, b) => compareVersionsDescending(a.mcVersion, b.mcVersion));
    const isVersionDetail = detail.catKey === "versions";
    const chevronSvg = `<svg class="vdetail-mc-chevron" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>`;
    if (mcGroups.length === 0) {
      return '<div class="data-row data-row--empty"><div>空空如也X_X</div></div>';
    }
    return mcGroups
      .map((grp, idx) => {
        const isOpen = expandedMcVersion === grp.mcVersion || (isVersionDetail && mcGroups.length === 1);
        return `
          <div class="vdetail-mc-group ${isOpen ? "is-open" : ""}" style="--v-idx:${Math.min(idx, 8)};">
            <button
              class="vdetail-mc-trigger"
              data-toggle-mc-group="${esc(grp.mcVersion)}"
              aria-expanded="${isOpen}"
              type="button"
            >
              <div class="vdetail-mc-left">
                <span class="vdetail-mc-title">${esc(grp.title)}</span>
                <span class="vdetail-mc-count">${grp.builds.length} 个可用构建</span>
              </div>
              ${chevronSvg}
            </button>

            <div class="vdetail-mc-drawer" ${isOpen ? "" : "inert"}>
              <div class="vdetail-mc-drawer-inner">
                <div class="vdetail-build-list">
                  ${isOpen ? renderDetailBuildRows(detail, activeLoader, grp.mcVersion, state, selectedVersionBuilds) : ""}
                </div>
              </div>
            </div>
          </div>`;
      })
      .join("");
  }

  function renderDetailBuildRows(detail, activeLoader, mcVersion, state, selectedVersionBuilds) {
    const group = (detail.loaderTrees[activeLoader] || []).find(item => item.mcVersion === mcVersion);
    if (!group) return "";
    const isVersionDetail = detail.catKey === "versions";
    const selectedIdForLoader = isVersionDetail ? selectedVersionBuilds?.[activeLoader] : null;
    const deps = Array.isArray(group.dependencies) ? group.dependencies : [];
    const depsHtml = !isVersionDetail && deps.length > 0
      ? `
      <div class="vdetail-deps-section">
        <div class="vdetail-section-label">前置资源</div>
        <div class="vdetail-deps-grid">
          ${deps
            .map((dep) => {
              const dTitle = dep.cnName ? `${dep.cnName} (${dep.enName})` : (dep.enName || dep.projectId);
              const dCat = dep.catKey || "mods";
              const dId = dep.id || (dep.projectId ? `mr-${dep.projectId}` : "");
              return `
          <button
            class="vdetail-dep-card"
            type="button"
            data-open-dep="${esc(dCat)}:${esc(dId)}"
            data-dep-loader="${esc(activeLoader)}"
            data-dep-mc="${esc(mcVersion)}"
          >
            <span class="vdetail-dep-icon">
              <img src="${esc(dep.icon || "assets/mc-icons/cobblestone.png")}" alt="" loading="lazy" decoding="async" />
            </span>
            <span class="vdetail-dep-main">
              <span class="vdetail-dep-top">
                <strong class="vdetail-dep-title">${esc(dTitle)}</strong>
                <span class="mod-tag ${dep.depType === "required" ? "is-source" : ""}">${esc(dep.label || "前置支持库")}</span>
              </span>
              ${dep.summary ? `<span class="vdetail-dep-desc">${esc(dep.summary)}</span>` : ""}
              <span class="vdetail-dep-meta">
                ${dep.downloads ? `<span>↓ ${esc(dep.downloads)}</span>` : ""}
                <span>${esc(dep.source || detail.source || "Modrinth")}</span>
              </span>
            </span>
          </button>`;
            })
            .join("")}
        </div>
        <div class="vdetail-section-label vdetail-section-label--builds">版本列表</div>
      </div>`
      : "";

    const rowsHtml = [...group.builds]
      .sort((a, b) => compareVersionsDescending(a.versionNumber, b.versionNumber))
      .map((b) => {
      const isChecked = isVersionDetail && selectedIdForLoader === b.id;
      const isDimmed = isVersionDetail && Boolean(selectedIdForLoader) && selectedIdForLoader !== b.id;
      const rowImg = b.icon || detail.icon;
      const isPixelIcon = Boolean(b.icon || detail.isPixel);
      const depTag = Number(b.depCount) > 0 ? `, ${b.depCount} 项前置` : "";
      return `
    <div
      class="data-row data-row--build ${isVersionDetail || b.available !== false ? "is-selectable-build" : ""} ${isChecked ? "is-checked-build" : ""} ${isDimmed ? "is-dimmed-build" : ""}"
      data-build-id="${esc(b.id)}"
      data-build-loader="${esc(activeLoader)}"
      ${isVersionDetail ? `data-check-version-build="${esc(activeLoader)}:${esc(b.id)}"` : ""}
    >
      <span class="row-progress-bg" aria-hidden="true"></span>
      <div class="row-left">
        <div class="row-official-icon ${isPixelIcon ? "is-pixel" : ""}">
          <img src="${esc(rowImg)}" alt="${esc(b.title)}" loading="lazy" decoding="async" />
        </div>
        <div style="min-width:0;">
          <div class="row-title-pair">
            <span class="data-row-title">${esc(b.title)}</span>
            <span class="mod-tag ${b.releaseType === "Release" ? "is-source" : ""}">${esc(b.releaseType)}</span>
          </div>
          <div class="data-row-sub" style="font-family:var(--font-mono); font-size:11px;">
            ${detail.hideLoaders ? "" : `${esc(activeLoader)}, `}${esc(b.file)}${depTag}${b.downloads ? `, 下载 ${esc(b.downloads)}` : ""}, 更新于 ${esc(b.updatedAgo)} · ${esc(b.size)}
          </div>
        </div>
      </div>
      ${
        isVersionDetail
          ? renderWaspCheckBtn(activeLoader, b.id, isChecked, b.title)
          : b.available === false
                ? '<span class="data-row-sub">请前往项目主页下载</span>'
                : renderDownloadBtn(
              `已加入下载队列：${b.title} (${state.mirrorSource})`,
              detail.catKey === "modpacks" ? `安装 ${b.title}` : `下载 ${b.title}`,
              `data-queue-name="${esc(b.title)}" data-queue-project="${esc(detail.cnName || detail.enName)}" data-queue-sub="${esc(b.file)}" data-queue-icon="${esc(detail.icon)}" data-queue-size="${esc(b.size)}" data-project-id="${esc(detail.id)}" data-cat-key="${esc(detail.catKey)}" data-build-ref="${esc(b.id)}" data-mc-version="${esc(mcVersion)}"`
            )
      }
    </div>`;

      })
      .join("");
    return depsHtml + rowsHtml;
  }

  function renderResourceDetailView(state, detailState) {
    const detail = detailState.preview || global.LauncherBridge.getResourceDetail(detailState.catKey, detailState.id);
    if (!detail) {
      return `<section class="container resource-detail-page"><div class="data-row data-row--empty"><div>空空如也X_X</div></div></section>`;
    }

    const activeLoader = detail.loaders.includes(detailState.activeLoader)
      ? detailState.activeLoader
      : detail.loaders[0];
    const selectedBuilds = (detailState && detailState.selectedVersionBuilds) || {};
    const isVersionDetail = detail.catKey === "versions";
    const headlineText = detail.cnName || detail.enName || detail.name || "未命名资源";
    const subEnName = detail.enName && detail.enName !== headlineText ? detail.enName : "";
    const detailLinks = detail.links || {};
    const extArrowSvg = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>`;
    const dlIconSvg = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 4v11"/><path d="M7.5 11.5L12 16l4.5-4.5"/><path d="M5 20h14"/></svg>`;

    const extButtons = [];
    if (detailLinks.curseforge) {
      extButtons.push(`<a class="vdetail-ext-btn" href="${esc(detailLinks.curseforge)}" target="_blank" rel="noopener noreferrer" data-ext-site="CurseForge"><span>CurseForge 官方页</span>${extArrowSvg}</a>`);
    }
    if (detailLinks.modrinth) {
      extButtons.push(`<a class="vdetail-ext-btn" href="${esc(detailLinks.modrinth)}" target="_blank" rel="noopener noreferrer" data-ext-site="Modrinth"><span>Modrinth 官方页</span>${extArrowSvg}</a>`);
    }
    if (detailLinks.mcmod) {
      extButtons.push(`<a class="vdetail-ext-btn" href="${esc(detailLinks.mcmod)}" target="_blank" rel="noopener noreferrer" data-ext-site="MC 百科"><span>MC 百科</span>${extArrowSvg}</a>`);
    }
    if (detailLinks.mcwiki) {
      extButtons.push(`<a class="vdetail-ext-btn" href="${esc(detailLinks.mcwiki)}" target="_blank" rel="noopener noreferrer" data-ext-site="Minecraft Wiki"><span>Minecraft Wiki</span>${extArrowSvg}</a>`);
    }

    const selectedLoaderKeys = Object.keys(selectedBuilds).filter((k) => Boolean(selectedBuilds[k]));
    const downloadBtnLabel =
      selectedLoaderKeys.length > 0
        ? `下载${detail.id} ${selectedLoaderKeys.join("+")}`
        : `下载${detail.id} 原版`;

    return `
      <div class="vdetail-bg-motif" id="vdetail-bg-motif" aria-hidden="true">${getMotifSvgForLoader(activeLoader)}</div>

      <section class="container resource-detail-page" id="resource-detail-page" aria-label="${esc(headlineText)} 详情">
        <div class="vdetail-shell">
          <div class="vdetail-hero is-ready">
            <div class="vdetail-hero-left">
              <div class="vdetail-icon ${detail.isPixel ? "is-pixel" : ""}">
                <img src="${esc(detail.icon)}" alt="${esc(headlineText)}" loading="eager" />
              </div>
              <div style="min-width:0; flex:1;">
                <div style="font-family:var(--font-mono); font-size:11px; color:var(--accent-primary); letter-spacing:0.12em;">
                  ${[subEnName, detail.author].filter(Boolean).map(esc).join(" · ")}
                </div>
                <h1 class="vdetail-cutout-title" style="margin-top:6px;">
                  ${renderCutoutWord(headlineText)}
                </h1>
                <p class="vdetail-summary">${esc(detail.summary)}</p>
              </div>
            </div>

            <div style="display:flex; flex-direction:column; align-items:flex-end; gap:12px;">
              <div class="vdetail-ext-links">${extButtons.join("")}</div>
            </div>
          </div>

          <!-- Level 1: Loader Switcher + Version Download Action (Skipped for Resource Packs) -->
          ${
            detail.hideLoaders || detailState.loading || detailState.error
              ? ""
              : `
          <div class="vdetail-loader-bar">
            <div class="vdetail-loader-list" role="tablist" aria-label="选择加载器分支">
              ${detail.loaders
                .map((ld) => {
                  const isAct = ld === activeLoader;
                  const hasSelected = Boolean(selectedBuilds[ld]);
                  const conflictWith = isVersionDetail ? getLoaderIncompatibility(ld, selectedBuilds) : null;
                  return `
                    <button
                      class="vdetail-loader-btn ${isAct ? "is-active" : ""} ${hasSelected ? "has-selected-build" : ""} ${conflictWith ? "is-incompatible-loader" : ""}"
                      data-detail-loader="${esc(ld)}"
                      data-conflict-with="${esc(conflictWith || "")}"
                      role="tab"
                      aria-selected="${isAct}"
                      type="button"
                    >
                      <span class="vdetail-loader-word">${renderCutoutWord(ld)}</span>
                      <span class="rare-cow-tooltip" role="tooltip">${conflictWith ? `${esc(ld)}与${esc(conflictWith)}不兼容！` : ""}</span>
                    </button>`;
                })
                .join("")}
            </div>
            ${
              isVersionDetail
                ? `
            <button class="btn-solid vdetail-download-btn" id="btn-download-selected-version" type="button">
              ${dlIconSvg}
              <span id="vdetail-download-btn-text">${esc(downloadBtnLabel)}</span>
            </button>`
                : ""
            }
          </div>`
          }

          <!-- Level 2: Game Versions Accordion List -> Level 3: Expandable Build File Drawer -->
          <div class="vdetail-mc-list" id="vdetail-mc-list">
            ${detailState.loading || detailState.error
              ? `<div class="data-row data-row--empty" role="status"><div>${esc(detailState.error || "正在读取可下载版本…")}</div></div>`
              : renderDetailMcListHtml(detail, activeLoader, detailState.expandedMcVersion, state, selectedBuilds)}
          </div>
        </div>
      </section>
    `;
  }

  global.LauncherViews = {
    renderHomeView,
    renderDownloadView,
    renderDownloadResults,
    renderResourceRowHtml,
    renderManageView,
    renderDiagnosticsView,
    renderInstanceGalleryView,
    renderDownloadQueueView,
    renderResourceDetailView,
    renderDetailMcListHtml,
    renderDetailBuildRows,
    filterGameVersions,
    renderGameVersionGroupPage,
    renderMcVersionTrayListHtml,
    getResourceCategoryLabel,
    renderFilterPopoverHtml,
    renderPauseResumeBtn,
    resolveFolderColorVars,
    getMotifSvgForLoader,
    renderMirrorStarfishControl,
    mirrorStatus,
    renderPlayerAccountControl,
    getLoaderIncompatibility,
    getInstanceJavaMismatch,
    renderFloatingDownloadBall,
    renderAnimatedCounterHtml,
    renderOfficialJavaDrawerInnerHtml,
    renderQuailLoader,
    MC_ICONS,
    renderHubSection,
    renderQuickPanel,
    renderModManagerBody,
    renderVersionArtHero,
    renderSubWorkspace,
    renderLaunchMeter,
    renderFolderShelfContent,
    FOLDER_PALETTES,
    getActiveInstance,
    getFavoriteInstances,
    getSortedInstances,
    getRecentInstances: getFavoriteInstances,
    getIconMeta
  };
})(window);
