/* ==========================================================================
   DummyCrisp Application Controller
   Router, 3D Spatial Portal Manager, Breathing Reveal, Theme Switcher
   ========================================================================== */

(function () {
  const PORTAL_CATALOG = {
    download: {
      eyebrow: "// DOWNLOAD",
      title: "下载资源",
      accent: "DOWNLOAD",
      cards: [
        { icon: "cube", title: "游戏版本", sub: "GAME VERSIONS", route: "download-versions" },
        { icon: "puzzle", title: "模组资源", sub: "MOD RESOURCES", route: "download-mods" },
        { icon: "pack", title: "资源包", sub: "RESOURCE PACKS", route: "download-packs" },
        { icon: "sun", title: "光影包", sub: "SHADER PACKS", route: "download-shaders" },
        { icon: "box", title: "整合包", sub: "MODPACKS", route: "download-modpacks" }
      ]
    },
    manage: {
      eyebrow: "// INSTANCE",
      title: "实例环境",
      accent: "MANAGE",
      directRoute: "auth-java",
      cards: []
    },
    diagnostics: {
      eyebrow: "// DIAGNOSTICS",
      title: "运行诊断",
      accent: "RUNTIME LAB",
      directRoute: "runtime-diag",
      cards: []
    }
  };

  function findHubForSubRoute(route) {
    for (const [key, cfg] of Object.entries(PORTAL_CATALOG)) {
      if (cfg.directRoute === route || cfg.cards.some((c) => c.route === route)) return key;
    }
    return null;
  }

  let currentRoute = "home";
  let searchQuery = "";
  let currentPage = 1;
  let favPage = 1;
  let modPage = 1;
  let modQuery = "";
  let loaderFilter = "all";
  let sourceFilter = "all";
  let sortFilter = "downloads";
  let categoryFilter = "all";
  let subCategoryFilter = "all";
  let mcVersionFilter = "all";
  let filterOpen = false;
  let launchProgress = 0;
  let activeQuickPanel = null;
  let activeHubKey = null;
  let activeSubRoute = null;
  let openPclCats = {};
  let accountMenuOpen = false;
  let offlineAddOpen = false;
  let javaInstallerOpen = false;
  let javaScanPending = false;
  let javaVendor = "temurin";
  let javaInstallPath = "";
  let javaInstallToken = "";
  let lastKnownTotalHits = 0;
  const pendingLaunchIds = new Set();
  const pendingSimTimers = new Map();
  let detailState = { catKey: null, id: null, activeLoader: null, expandedMcVersion: null, selectedVersionBuilds: {} };
  let onlineSearch = null;
  let onlineSearchTimer = null;
  let catalogPrefetchTimer = null;
  let onlineSearchSeq = 0;
  let filterClosingTimer = null;
  let homeReturnTimer = null;
  let gallerySettleTimer = null;

  function getViewOpts(extra) {
    return Object.assign(
      {
        page: currentPage,
        favPage,
        modPage,
        modQuery,
        loaderFilter,
        sourceFilter,
        sortFilter,
        categoryFilter,
        subCategoryFilter,
        mcVersionFilter,
        filterOpen,
        openPclCats,
        accountMenuOpen,
        offlineAddOpen,
        javaInstallerOpen,
        javaScanPending,
        javaVendor,
        javaInstallPath,
        lastKnownTotalHits,
        pendingLaunchIds,
        onlineSearch
      },
      extra || {}
    );
  }

  function resetListControls() {
    searchQuery = "";
    currentPage = 1;
    modPage = 1;
    modQuery = "";
    loaderFilter = "all";
    sourceFilter = "all";
    sortFilter = "downloads";
    categoryFilter = "all";
    subCategoryFilter = "all";
    mcVersionFilter = "all";
    filterOpen = false;
    lastKnownTotalHits = 0;
    onlineSearch = null;
    clearTimeout(onlineSearchTimer);
    clearTimeout(catalogPrefetchTimer);
    onlineSearchSeq += 1;
  }

  function routeToCatalogKey(subRoute) {
    if (subRoute === "download-mods") return "mods";
    if (subRoute === "download-packs") return "packs";
    if (subRoute === "download-shaders") return "shaders";
    if (subRoute === "download-modpacks") return "modpacks";
    return null;
  }

  function refreshDownloadResults(subRoute, { direction = 0, animate = false } = {}) {
    const results = document.getElementById("resource-download-results");
    if (!results) return;
    const state = window.LauncherBridge.getStateSync();
    results.innerHTML = window.LauncherViews.renderDownloadResults(subRoute, state, searchQuery, getViewOpts());
    results.removeAttribute("aria-busy");
    if (direction) animateHorizontalPageSlide(results.querySelector(".data-table-list"), direction);
    else if (animate) animateDampedSlide(results.querySelector(".data-table-list") || results, 10);
  }

  let streamRowTimers = [];
  function clearStreamRowTimers() {
    streamRowTimers.forEach((t) => clearTimeout(t));
    streamRowTimers = [];
  }

  function appendStreamedCatalogRows(subRoute, catKey, startIdx, newItems, fullOnlineSearch, keepTailSkeletons, seq) {
    const results = document.getElementById("resource-download-results");
    const listEl = document.getElementById("download-results-list");
    if (!results || !listEl || !Array.isArray(newItems)) {
      refreshDownloadResults(subRoute);
      return;
    }
    const state = window.LauncherBridge.getStateSync();
    const updateMeta = () => {
      if (!keepTailSkeletons) results.removeAttribute("aria-busy");
      const total = Number(fullOnlineSearch?.totalHits) || fullOnlineSearch?.items?.length || 0;
      const totalPages = Math.max(1, Math.ceil(total / 50));
      results.querySelectorAll(".pagination-info").forEach((info) => {
        info.textContent = `共 ${total} 项 · 第 ${currentPage} / ${totalPages} 页`;
      });
    };

    if (newItems.length === 0) {
      if (!keepTailSkeletons) {
        listEl.querySelectorAll(".data-row--skeleton").forEach((el) => el.remove());
      }
      updateMeta();
      return;
    }

    if (prefersReducedMotion() || typeof window.LauncherViews.renderResourceRowHtml !== "function") {
      listEl.querySelectorAll(".data-row--skeleton").forEach((el) => el.remove());
      const fragHtml = newItems.map((it, idx) => window.LauncherViews.renderResourceRowHtml(it, catKey, state, startIdx + idx)).join("");
      listEl.insertAdjacentHTML("beforeend", fragHtml);
      if (keepTailSkeletons && typeof window.LauncherViews.renderSkeletonRowsHtml === "function") {
        listEl.insertAdjacentHTML("beforeend", window.LauncherViews.renderSkeletonRowsHtml(3));
      }
      updateMeta();
      return;
    }

    // 逐条流式插入，避免整批突兀跳出
    const stepMs = 20;
    newItems.forEach((it, i) => {
      const timer = setTimeout(() => {
        if (seq !== onlineSearchSeq || currentRoute === "resource-detail" || (activeSubRoute || currentRoute) !== subRoute) return;
        const curList = document.getElementById("download-results-list");
        if (!curList) return;
        const firstSkel = curList.querySelector(".data-row--skeleton");
        const rowHtml = window.LauncherViews.renderResourceRowHtml(it, catKey, window.LauncherBridge.getStateSync(), startIdx + i);
        if (firstSkel) {
          firstSkel.insertAdjacentHTML("beforebegin", rowHtml);
          if (!keepTailSkeletons && i === newItems.length - 1) {
            curList.querySelectorAll(".data-row--skeleton").forEach((el) => el.remove());
          }
        } else {
          curList.insertAdjacentHTML("beforeend", rowHtml);
        }
        if (i === newItems.length - 1) {
          updateMeta();
        }
      }, i * stepMs);
      streamRowTimers.push(timer);
    });
  }

  async function loadOnlineDownloadPage(subRoute, motion) {
    const catKey = routeToCatalogKey(subRoute);
    if (!catKey) return;
    clearTimeout(onlineSearchTimer);
    clearTimeout(catalogPrefetchTimer);
    clearStreamRowTimers();
    const seq = ++onlineSearchSeq;
    const query = searchQuery;
    const page = currentPage;
    const source = sourceFilter;
    const loader = loaderFilter;
    const sortBy = sortFilter;
    const categoryTag = categoryFilter;
    const subCategoryTag = subCategoryFilter;
    const mcVersion = mcVersionFilter;
    const baseOffset = (page - 1) * 50;
    const chunkSizes = [10, 10, 10, 10, 10];

    // Flip immediately on pagination click with top-down skeleton rows while fetching
    onlineSearch = { catKey, source, sortBy, categoryTag, subCategoryTag, mcVersion, page, items: [], totalHits: lastKnownTotalHits, loading: true, streaming: false };
    refreshDownloadResults(subRoute, motion);
    document.getElementById("resource-download-results")?.setAttribute("aria-busy", "true");

    // 启动首波请求，同时并行发起后续 4 个小块（每块 10 条），按序逐条流式渲染
    let runningOffset = baseOffset;
    const waveSpecs = chunkSizes.map((size) => {
      const spec = { offset: runningOffset, size };
      runningOffset += size;
      return spec;
    });
    const wavePromises = waveSpecs.map((spec, idx) => {
      if (idx > 0 && lastKnownTotalHits > 0 && spec.offset >= lastKnownTotalHits) {
        return Promise.resolve({ items: [] });
      }
      const p = window.LauncherBridge.fetchLiveCatalogPage(
        catKey, query, loader, page, spec.size, source, spec.offset, sortBy, mcVersion, categoryTag, subCategoryTag
      );
      return idx === 0 ? p : p.catch(() => ({ items: [] }));
    });

    let firstChunk;
    try {
      firstChunk = await wavePromises[0];
    } catch (error) {
      firstChunk = { items: [], message: error.message || "暂时无法加载在线目录。" };
    }
    if (seq !== onlineSearchSeq || currentRoute === "resource-detail" || (activeSubRoute || currentRoute) !== subRoute) return;

    if (!firstChunk) {
      onlineSearch = null;
      refreshDownloadResults(subRoute);
      return;
    }

    if (Number(firstChunk?.totalHits) > 0) lastKnownTotalHits = Number(firstChunk.totalHits);
    const seenIds = new Set();
    const allItems = [];
    const firstItems = (Array.isArray(firstChunk?.items) ? firstChunk.items : []).filter((x) => {
      if (!x || seenIds.has(x.id)) return false;
      seenIds.add(x.id);
      return true;
    });
    allItems.push(...firstItems);
    let totalHits = Number(firstChunk?.totalHits) || lastKnownTotalHits || firstItems.length;
    const hasMoreWaves = firstItems.length > 0 && (baseOffset + waveSpecs[0].size) < totalHits;

    // 先渲染框架 + 骨架，再将首批结果流式逐条写入
    onlineSearch = { ...firstChunk, items: [], totalHits, catKey, source, sortBy, categoryTag, subCategoryTag, mcVersion, page, loading: firstItems.length === 0 ? false : true, streaming: hasMoreWaves };
    if (firstItems.length === 0) {
      refreshDownloadResults(subRoute);
    } else {
      onlineSearch = { ...firstChunk, items: allItems.slice(), totalHits, catKey, source, sortBy, categoryTag, subCategoryTag, mcVersion, page, loading: false, streaming: hasMoreWaves };
      // 用骨架占位后逐条流式替换首批 10 项
      const results = document.getElementById("resource-download-results");
      const listEl = document.getElementById("download-results-list");
      if (results && listEl && listEl.querySelector(".data-row--skeleton") && results.querySelector("[data-page]")) {
        appendStreamedCatalogRows(subRoute, catKey, 0, firstItems, onlineSearch, hasMoreWaves, seq);
      } else {
        refreshDownloadResults(subRoute);
      }
    }

    if (hasMoreWaves) {
      for (let w = 1; w < wavePromises.length; w++) {
        if (waveSpecs[w].offset >= totalHits) break;
        const chunk = await wavePromises[w];
        if (seq !== onlineSearchSeq || currentRoute === "resource-detail" || (activeSubRoute || currentRoute) !== subRoute) return;
        const fresh = (Array.isArray(chunk?.items) ? chunk.items : []).filter((x) => {
          if (!x || seenIds.has(x.id)) return false;
          seenIds.add(x.id);
          return true;
        });
        const startIdx = allItems.length;
        allItems.push(...fresh);
        totalHits = Math.max(totalHits, Number(chunk?.totalHits) || 0, allItems.length);
        if (totalHits > 0) lastKnownTotalHits = totalHits;
        const moreAfterThis = w < wavePromises.length - 1 && waveSpecs[w + 1].offset < totalHits && fresh.length > 0;
        onlineSearch = {
          ...firstChunk,
          ...chunk,
          items: allItems.slice(),
          totalHits,
          catKey,
          source,
          sortBy,
          categoryTag,
          subCategoryTag,
          mcVersion,
          page,
          loading: false,
          streaming: moreAfterThis
        };
        appendStreamedCatalogRows(subRoute, catKey, startIdx, fresh, onlineSearch, moreAfterThis, seq);
        if (fresh.length === 0) break;
      }
      // 最终清理残留骨架
      const cleanupTimer = setTimeout(() => {
        if (seq !== onlineSearchSeq) return;
        document.getElementById("download-results-list")?.querySelectorAll(".data-row--skeleton").forEach((el) => el.remove());
        document.getElementById("resource-download-results")?.removeAttribute("aria-busy");
      }, 260);
      streamRowTimers.push(cleanupTimer);
    }

    const totalHitsFinal = Number(onlineSearch?.totalHits) || 0;
    const lastPage = Math.max(1, Math.ceil(totalHitsFinal / 50));
    if (page < lastPage) {
      catalogPrefetchTimer = setTimeout(() => {
        if (seq === onlineSearchSeq && currentRoute !== "resource-detail" && (activeSubRoute || currentRoute) === subRoute) {
          window.LauncherBridge.fetchLiveCatalogPage(catKey, query, loader, page + 1, 10, source, page * 50, sortBy, mcVersion, categoryTag, subCategoryTag).catch(() => {});
        }
      }, 160);
    } else if (catKey === "mods" && page === 1 && lastPage >= 6) {
      catalogPrefetchTimer = setTimeout(() => {
        if (seq === onlineSearchSeq && currentRoute !== "resource-detail" && (activeSubRoute || currentRoute) === subRoute) {
          window.LauncherBridge.fetchLiveCatalogPage(catKey, query, loader, lastPage, 10, source, (lastPage - 1) * 50, sortBy, mcVersion, categoryTag, subCategoryTag).catch(() => {});
        }
      }, 200);
    }
  }

  function applyDownloadFilters(opts) {
    const keepCatTrayOpen = Boolean(opts && opts.keepCatTrayOpen);
    currentPage = 1;
    const route = activeSubRoute || currentRoute;
    const catKey = routeToCatalogKey(route) || "mods";
    document.querySelectorAll("#filter-popover-slot [data-filter-loader], #filter-popover-slot [data-filter-source], #filter-popover-slot [data-filter-sort], #filter-popover-slot [data-filter-cat], #filter-popover-slot [data-filter-subcat], #filter-popover-slot [data-filter-mc]").forEach(button => {
      const active = button.dataset.filterLoader ? button.dataset.filterLoader === loaderFilter
        : button.dataset.filterSource ? button.dataset.filterSource === sourceFilter
        : button.dataset.filterSort ? button.dataset.filterSort === sortFilter
        : button.dataset.filterCat ? button.dataset.filterCat === categoryFilter
        : button.dataset.filterSubcat ? button.dataset.filterSubcat === subCategoryFilter
        : button.dataset.filterMc === mcVersionFilter;
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", String(active));
      if (button.hasAttribute("aria-selected")) button.setAttribute("aria-selected", String(active));
    });
    const catWrap = document.getElementById("cat-starfish-wrap");
    const catTrigger = document.getElementById("cat-starfish-trigger");
    if (catTrigger) {
      const nameEl = catTrigger.querySelector(".mirror-trigger-name");
      const nextCatLabel = window.LauncherViews.getResourceCategoryLabel(catKey, categoryFilter, subCategoryFilter);
      if (nameEl && nameEl.textContent !== nextCatLabel) {
        nameEl.textContent = nextCatLabel;
        catTrigger.classList.remove("is-switching");
        requestAnimationFrame(() => catTrigger.classList.add("is-switching"));
      }
      catTrigger.setAttribute("aria-label", `选择资源类型（${nextCatLabel}）`);
      if (!keepCatTrayOpen) catTrigger.setAttribute("aria-expanded", "false");
    }
    if (!keepCatTrayOpen) catWrap?.classList.remove("is-open");

    const mcWrap = document.getElementById("mcver-starfish-wrap");
    const mcTrigger = document.getElementById("mcver-starfish-trigger");
    if (mcTrigger) {
      const nameEl = mcTrigger.querySelector(".mirror-trigger-name");
      const nextLabel = mcVersionFilter === "all" ? "全部版本" : mcVersionFilter;
      if (nameEl && nameEl.textContent !== nextLabel) {
        nameEl.textContent = nextLabel;
        mcTrigger.classList.remove("is-switching");
        requestAnimationFrame(() => mcTrigger.classList.add("is-switching"));
      }
      mcTrigger.setAttribute("aria-label", `选择 Minecraft 版本（${nextLabel}）`);
      mcTrigger.setAttribute("aria-expanded", "false");
    }
    mcWrap?.classList.remove("is-open");
    const mcInput = document.getElementById("mcver-search-input");
    if (mcInput && mcInput.value) {
      mcInput.value = "";
      mcInput.classList.remove("has-value");
      ensureMcVersionTrayPopulated("");
    }
    const hasFilter = loaderFilter !== "all" || sourceFilter !== "all" || sortFilter !== "downloads" || categoryFilter !== "all" || subCategoryFilter !== "all" || mcVersionFilter !== "all";
    document.getElementById("btn-toggle-filter")?.classList.toggle("is-active", filterOpen || hasFilter);
    if (routeToCatalogKey(route)) loadOnlineDownloadPage(route, { animate: true });
    else refreshDownloadResults(route, { animate: true });
  }
  let navHistory = [];
  let launchTimer = null;
  let launchResetTimer = null;
  let toastTimer = null;

  const appRoot = document.getElementById("app-root");
  const portalBackdrop = document.getElementById("portal-backdrop");
  const drawerOverlay = document.getElementById("drawer-overlay");
  const toastEl = document.getElementById("toast-pill");
  const btnNavBack = document.getElementById("btn-nav-back");
  const btnNavHome = document.getElementById("btn-nav-home");

  const installDialog = document.getElementById("resource-install-dialog");
  const installTargetWrap = document.getElementById("resource-target-wrap");
  const installTargetTrigger = document.getElementById("resource-target-trigger");
  const installTargetTitle = document.getElementById("resource-target-title");
  const installTargetMeta = document.getElementById("resource-target-meta");
  const installOptions = document.getElementById("resource-install-options");
  const installStatus = document.getElementById("resource-install-status");
  const installBrowse = document.getElementById("resource-install-browse");
  const installConfirm = document.getElementById("resource-install-confirm");
  let pendingInstall = null;
  let installReady = false;
  let installBusy = false;
  let installDropdownOpen = false;
  let installTargetItems = [];
  let selectedTargetKey = "";
  let customInstallKey = "";

  function setInstallDropdownOpen(open) {
    installDropdownOpen = Boolean(open);
    installTargetWrap?.classList.toggle("is-open", installDropdownOpen);
    installTargetTrigger?.setAttribute("aria-expanded", String(installDropdownOpen));
  }

  function refreshInstallSelection() {
    const selected = installTargetItems.find((item) => item.key === selectedTargetKey) || null;
    installConfirm.disabled = !installReady || installBusy || !selected;
    installBrowse.disabled = !installReady || installBusy;
    if (installTargetTrigger) installTargetTrigger.disabled = !installReady || installBusy;
    if (installTargetTitle) {
      installTargetTitle.textContent = selected ? selected.title : (installReady ? "选择安装位置" : "正在查找安装位置…");
    }
    if (installTargetMeta) {
      installTargetMeta.textContent = selected ? (selected.subtitle || "") : "";
    }
    if (installReady && !installBusy) {
      installStatus.textContent = selected
        ? selected.warning
        : "暂无已安装实例，可选择指定文件夹。";
    }
  }

  function selectInstallTarget(key, animateSwitch = true) {
    selectedTargetKey = key || "";
    installOptions.querySelectorAll(".install-target-item").forEach((btn) => {
      const isAct = btn.getAttribute("data-target-key") === selectedTargetKey;
      btn.classList.toggle("is-active", isAct);
      btn.setAttribute("aria-selected", String(isAct));
    });
    if (animateSwitch && installTargetTrigger && !prefersReducedMotion()) {
      installTargetTrigger.classList.remove("is-switching");
      requestAnimationFrame(() => installTargetTrigger.classList.add("is-switching"));
    }
    setInstallDropdownOpen(false);
    refreshInstallSelection();
  }

  function appendInstallOption(title, subtitle, { instanceId = "", folderToken = "", warning = "" } = {}) {
    const key = folderToken ? `folder:${folderToken}` : `inst:${instanceId}`;
    const item = { key, title, subtitle, instanceId, folderToken, warning };
    installTargetItems.push(item);

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "install-target-item";
    btn.setAttribute("role", "option");
    btn.setAttribute("data-target-key", key);
    btn.setAttribute("aria-selected", "false");

    const left = document.createElement("span");
    left.className = "install-target-item-main";
    const nameEl = document.createElement("strong");
    nameEl.textContent = title;
    const metaEl = document.createElement("small");
    metaEl.textContent = subtitle || "";
    left.append(nameEl, metaEl);

    btn.append(left);
    if (warning) {
      const warnTag = document.createElement("span");
      warnTag.className = "install-target-warn-tag";
      warnTag.textContent = warning.includes("加载器") ? "加载器不匹配" : "版本不匹配";
      btn.append(warnTag);
    }
    installOptions.append(btn);
    return item;
  }

  async function chooseResourceTarget(task) {
    if (installDialog.open) return;
    pendingInstall = task;
    installReady = installBusy = false;
    installTargetItems = [];
    selectedTargetKey = "";
    customInstallKey = "";
    setInstallDropdownOpen(false);
    document.getElementById("resource-install-name").textContent =
      task.projectName && task.projectName !== task.name ? `${task.projectName} · ${task.name}` : task.name;
    installOptions.replaceChildren();
    if (installTargetWrap) installTargetWrap.hidden = false;
    installStatus.textContent = "正在查找安装位置…";
    refreshInstallSelection();
    installDialog.showModal();
    try {
      const targets = await window.LauncherBridge.invoke("get_resource_targets", task);
      if (!installDialog.open || pendingInstall !== task) return;
      targets.forEach((target) => {
        const mcLabel = `Minecraft ${target.mcVersion}`;
        const issue = target.match === "VersionMismatch" ? "游戏版本不匹配" :
          target.match === "LoaderMismatch" ? "加载器不匹配" : "";
        const details = [target.name === target.mcVersion || target.name === mcLabel ? "" : mcLabel, target.loader]
          .filter(Boolean).join(" · ");
        appendInstallOption(target.name, details, {
          instanceId: target.id,
          warning: issue ? `${issue}，仍可安装，但游戏可能无法加载此资源。` : ""
        });
      });
      installReady = true;
      if (installTargetItems.length > 0) {
        selectInstallTarget(installTargetItems[0].key, false);
      } else {
        refreshInstallSelection();
      }
    } catch (error) {
      if (installDialog.open && pendingInstall === task) installStatus.textContent = error.message;
    }
  }

  function closeDialogWithAnimation(dialog) {
    if (!dialog || !dialog.open || dialog.classList.contains("is-closing")) return;
    setInstallDropdownOpen(false);
    if (prefersReducedMotion()) {
      dialog.close();
      return;
    }
    dialog.classList.add("is-closing");
    setTimeout(() => {
      dialog.classList.remove("is-closing");
      if (dialog.open) dialog.close();
    }, 240);
  }

  installDialog.addEventListener("close", () => {
    installDialog.classList.remove("is-closing");
    setInstallDropdownOpen(false);
    if (!installDialog.open) pendingInstall = null;
  });
  installDialog.addEventListener("cancel", (e) => {
    e.preventDefault();
    closeDialogWithAnimation(installDialog);
  });
  installDialog.querySelector('form[method="dialog"]')?.addEventListener("submit", (e) => {
    e.preventDefault();
    closeDialogWithAnimation(installDialog);
  });
  installDialog.addEventListener("click", (e) => {
    if (e.target === installDialog) {
      closeDialogWithAnimation(installDialog);
      return;
    }
    if (installDropdownOpen && installTargetWrap && !installTargetWrap.contains(e.target)) {
      setInstallDropdownOpen(false);
    }
  });
  installTargetTrigger?.addEventListener("click", (e) => {
    e.stopPropagation();
    if (!installReady || installBusy || installTargetItems.length === 0) return;
    setInstallDropdownOpen(!installDropdownOpen);
  });
  installOptions.addEventListener("click", (e) => {
    const itemBtn = e.target.closest(".install-target-item[data-target-key]");
    if (!itemBtn) return;
    selectInstallTarget(itemBtn.getAttribute("data-target-key"), true);
  });
  installBrowse.addEventListener("click", async () => {
    const task = pendingInstall;
    if (!installReady || installBusy || !task) return;
    installBusy = true;
    refreshInstallSelection();
    try {
      const folder = await window.LauncherBridge.invoke("browse_resource_install_folder", {});
      if (!installDialog.open || pendingInstall !== task) return;
      if (folder?.token && folder.path) {
        if (customInstallKey) {
          installTargetItems = installTargetItems.filter((it) => it.key !== customInstallKey);
          installOptions.querySelector(`.install-target-item[data-target-key="${CSS.escape(customInstallKey)}"]`)?.remove();
        }
        const added = appendInstallOption("指定文件夹", folder.path, { folderToken: folder.token });
        customInstallKey = added.key;
        installBusy = false;
        selectInstallTarget(added.key, true);
      } else {
        installBusy = false;
        refreshInstallSelection();
      }
    } catch (error) {
      if (installDialog.open && pendingInstall === task) {
        installBusy = false;
        refreshInstallSelection();
        installStatus.textContent = error.message;
      }
    }
  });

  installConfirm.addEventListener("click", async () => {
    const task = pendingInstall;
    const selected = installTargetItems.find((item) => item.key === selectedTargetKey);
    if (!installReady || installBusy || !task || !selected) return;
    installBusy = true;
    setInstallDropdownOpen(false);
    refreshInstallSelection();
    installStatus.textContent = "正在加入下载队列…";
    const destination = selected.folderToken ? { folderToken: selected.folderToken } : { instanceId: selected.instanceId };
    try {
      await window.LauncherBridge.invoke("add_download_task", { ...task, ...destination });
      if (pendingInstall === task && installDialog.open) closeDialogWithAnimation(installDialog);
      syncFloatingDownloadBallUi(window.LauncherBridge.getStateSync());
      showToast(`已加入下载队列：${task.name}`);
    } catch (error) {
      if (installDialog.open && pendingInstall === task) {
        installBusy = false;
        refreshInstallSelection();
        installStatus.textContent = error.message;
      }
    }
  });

  const microsoftDialog = document.getElementById("microsoft-login-dialog");
  const microsoftStatus = document.getElementById("microsoft-login-status");
  const microsoftCode = document.getElementById("microsoft-device-code");
  const microsoftCopy = document.getElementById("microsoft-copy-code");
  const microsoftBrowser = document.getElementById("microsoft-open-browser");
  let microsoftFlow = null;

  function setMicrosoftCode(code) {
    microsoftCode.value = code;
    microsoftCode.hidden = microsoftCopy.hidden = microsoftBrowser.hidden = !code;
  }

  async function signInWithMicrosoft() {
    if (!window.LauncherBridge.isWebView2) {
      showToast("请在桌面启动器中完成微软登录。");
      return;
    }
    if (microsoftDialog.open) return;
    const flow = { id: "", cancelled: false };
    microsoftFlow = flow;
    setMicrosoftCode("");
    microsoftStatus.textContent = "正在向 Microsoft 请求登录授权并打开登录页…";
    microsoftDialog.showModal();
    try {
      const challenge = await window.LauncherBridge.invoke("microsoft_begin", {});
      flow.id = challenge.id;
      if (flow.cancelled || microsoftFlow !== flow || !microsoftDialog.open) {
        await window.LauncherBridge.invoke("microsoft_cancel", { id: flow.id });
        await window.LauncherBridge.invoke("microsoft_wait", { id: flow.id });
        return;
      }
      setMicrosoftCode(challenge.userCode);
      navigator.clipboard?.writeText(challenge.userCode).catch(() => {});
      window.LauncherBridge.invoke("open_microsoft_verification", { id: flow.id }).catch(() => {});
      const time = new Date(challenge.expiresAt).toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" });
      microsoftStatus.textContent = `已自动打开微软登录页并尝试复制授权码，请在浏览器粘贴登录（${time} 过期）。`;
      const account = await window.LauncherBridge.invoke("microsoft_wait", { id: flow.id });
      if (microsoftFlow !== flow || !microsoftDialog.open) return;
      setMicrosoftCode("");
      if (!account) {
        microsoftStatus.textContent = "授权码已过期，请关闭窗口后重新登录。";
        return;
      }
      microsoftDialog.close();
      render({ preserveSubLayout: true });
      showToast(`已登录 Microsoft 账号：${account.username}`);
    } catch (error) {
      if (microsoftFlow === flow && microsoftDialog.open) {
        setMicrosoftCode("");
        microsoftStatus.textContent = error.message;
      }
    }
  }

  microsoftDialog.addEventListener("close", () => {
    if (!microsoftFlow) return;
    const flow = microsoftFlow;
    microsoftFlow = null;
    flow.cancelled = true;
    if (flow.id) window.LauncherBridge.invoke("microsoft_cancel", { id: flow.id }).catch(() => {});
  });

  microsoftCopy.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(microsoftCode.value);
      microsoftStatus.textContent = "已复制授权码，请到微软登录页粘贴。";
    } catch {
      microsoftCode.focus();
      microsoftCode.select();
      microsoftStatus.textContent = "请按 Ctrl+C 复制已选中的授权码。";
    }
  });

  microsoftBrowser.addEventListener("click", async () => {
    try {
      await window.LauncherBridge.invoke("open_microsoft_verification", { id: microsoftFlow.id });
    } catch (error) {
      microsoftStatus.textContent = error.message;
    }
  });


  const THEME_PRESETS = [
    { id: "obsidian-yellow", name: "曜石黑 × 电光黄", wipeBg: "#0a0a0c", ring: "#f5c518", body: "#f5c518", dots: ["#0a0a0c", "#1b1b21", "#ffe14d", "#f4f3ef"], isRainbow: false },
    { id: "editorial-cream", name: "馆藏米白 × 深海蓝", wipeBg: "#f4f3ef", ring: "#0c3b52", body: "#0c3b52", dots: ["#f4f3ef", "#eceae3", "#145a7d", "#d93829"], isRainbow: false },
    { id: "sage-forest", name: "苔原松绿", wipeBg: "#dad7cd", ring: "#3a5a40", body: "#3a5a40", dots: ["#dad7cd", "#a3b18a", "#588157", "#344e41"], isRainbow: false },
    { id: "crimson-rose", name: "绯红玫瑰", wipeBg: "#590d22", ring: "#ff758f", body: "#ff4d6d", dots: ["#590d22", "#a4133c", "#ff8fa3", "#fff0f3"], isRainbow: false },
    { id: "violet-nebula", name: "深空紫霓", wipeBg: "#10002b", ring: "#c77dff", body: "#9d4edd", dots: ["#240046", "#5a189a", "#c77dff", "#e0aaff"], isRainbow: false },
    { id: "monochrome-slate", name: "极简铅灰", wipeBg: "#f8f9fa", ring: "#212529", body: "#343a40", dots: ["#f8f9fa", "#dee2e6", "#adb5bd", "#212529"], isRainbow: false },
    { id: "espresso-amber", name: "焦糖可可", wipeBg: "#ffedd8", ring: "#6f4518", body: "#8b5e34", dots: ["#ffedd8", "#e7bc91", "#bc8a5f", "#583101"], isRainbow: false },
    { id: "ice-periwinkle", name: "晴空冰蓝", wipeBg: "#edf2fb", ring: "#3b63c9", body: "#3b63c9", dots: ["#edf2fb", "#d7e3fc", "#b6ccfe", "#182952"], isRainbow: false },
    { id: "rainbow-prism", name: "幻彩光谱", wipeBg: "radial-gradient(circle at center, #38bdf8 0%, #c77dff 28%, #ff4d6d 56%, #f5c518 82%, #0b0b12 100%)", ring: "#38bdf8", body: "#38bdf8", dots: ["#ff4d6d", "#f5c518", "#34d399", "#c77dff"], isRainbow: true }
  ];
  let currentThemeIndex = 0;
  let themeWipeSwapTimer = null;
  let themeWipeFadeTimer = null;
  let themeWipeResetTimer = null;

  function syncPaletteButtonUi(theme) {
    const btn = document.getElementById("btn-cycle-theme");
    if (!btn || !theme) return;
    btn.classList.toggle("is-rainbow", Boolean(theme.isRainbow));
    btn.style.setProperty("--pal-ring", theme.ring);
    btn.style.setProperty("--pal-body", theme.body);
    btn.style.setProperty("--pal-dot-1", theme.dots[0]);
    btn.style.setProperty("--pal-dot-2", theme.dots[1]);
    btn.style.setProperty("--pal-dot-3", theme.dots[2]);
    btn.style.setProperty("--pal-dot-4", theme.dots[3]);
    btn.setAttribute("aria-label", `切换配色主题 (当前：${theme.name})`);
    btn.removeAttribute("title");
    delete btn.dataset.tooltip;
  }

  function cycleThemeWithWipe() {
    currentThemeIndex = (currentThemeIndex + 1) % THEME_PRESETS.length;
    const nextTheme = THEME_PRESETS[currentThemeIndex];
    const btn = document.getElementById("btn-cycle-theme");
    const wipeHost = document.getElementById("theme-circle-wipe");
    const wipeInner = wipeHost ? wipeHost.querySelector(".theme-circle-wipe__inner") : null;

    if (!wipeHost || !wipeInner || prefersReducedMotion()) {
      document.documentElement.setAttribute("data-theme", nextTheme.id);
      syncPaletteButtonUi(nextTheme);
      window.LauncherBridge.invoke("set_theme", { themeId: nextTheme.id }).catch(() => {});
      return;
    }

    clearTimeout(themeWipeSwapTimer);
    clearTimeout(themeWipeFadeTimer);
    clearTimeout(themeWipeResetTimer);

    const rect = btn ? btn.getBoundingClientRect() : { left: window.innerWidth - 96, top: 32, width: 40, height: 40 };
    const cx = Math.round(rect.left + rect.width / 2);
    const cy = Math.round(rect.top + rect.height / 2);
    const diameter = Math.ceil(Math.hypot(window.innerWidth, window.innerHeight) * 2.28);

    wipeHost.style.left = `${cx}px`;
    wipeHost.style.top = `${cy}px`;
    wipeInner.style.setProperty("--wipe-bg", nextTheme.wipeBg);
    wipeInner.style.setProperty("--wipe-ring", nextTheme.ring);
    wipeInner.style.setProperty("--wipe-size", `${diameter}px`);
    wipeHost.classList.remove("is-wiping", "is-fading");
    void wipeInner.offsetWidth;
    wipeHost.classList.add("is-wiping");

    if (btn) {
      btn.classList.remove("is-spinning");
      void btn.offsetWidth;
      btn.classList.add("is-spinning");
    }

    themeWipeSwapTimer = setTimeout(() => {
      document.documentElement.setAttribute("data-theme", nextTheme.id);
      syncPaletteButtonUi(nextTheme);
      window.LauncherBridge.invoke("set_theme", { themeId: nextTheme.id }).catch(() => {});
    }, 240);

    themeWipeFadeTimer = setTimeout(() => {
      wipeHost.classList.add("is-fading");
    }, 480);

    themeWipeResetTimer = setTimeout(() => {
      wipeHost.classList.remove("is-wiping", "is-fading");
    }, 760);
  }

  function showToast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add("is-shown");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastEl.classList.remove("is-shown");
    }, 1800);
  }

  function syncLaunchButtonUi(state) {
    const hasInst = Boolean(state && Array.isArray(state.instances) && state.instances.length > 0);
    const activeId = state?.activeInstanceId || "";
    const isPending = Boolean(hasInst && activeId && pendingLaunchIds.has(activeId));
    const launchBtn = document.getElementById("btn-launch-game");
    if (!launchBtn) return;
    launchBtn.disabled = !hasInst;
    launchBtn.classList.toggle("is-disabled", !hasInst);
    launchBtn.classList.toggle("is-hollow", isPending);
    launchBtn.setAttribute("aria-disabled", String(!hasInst));
    launchBtn.innerHTML = !hasInst
      ? "<span>暂无可启动版本</span><span>→</span>"
      : isPending
        ? "<span>取消</span><span>×</span>"
        : "<span>启动游戏</span><span>→</span>";
    if (isPending) launchBtn.setAttribute("data-cancel-pending-launch", "true");
    else launchBtn.removeAttribute("data-cancel-pending-launch");
  }

  function syncVersionJavaWarningUi(state) {
    const artSlot = document.getElementById("version-art-slot");
    if (!artSlot) return;
    const active = window.LauncherViews.getActiveInstance(state);
    if (!active || !state?.instances?.length) return;
    const metaRow = artSlot.querySelector(".version-art-meta-row");
    if (!metaRow) return;
    const metaEl = metaRow.querySelector(".version-art-meta");
    if (metaEl) {
      metaEl.textContent = `${active.mcVersion} | ${active.loader} ${active.loaderVersion} | ${active.javaVersion} | ${active.modsCount} Mods`;
    }
    const javaWarning = typeof window.LauncherViews.getInstanceJavaMismatch === "function"
      ? window.LauncherViews.getInstanceJavaMismatch(state, active)
      : "";
    const existing = document.getElementById("version-java-warning");
    if (javaWarning) {
      if (existing) {
        const span = existing.querySelector("span") || existing;
        span.textContent = javaWarning;
      } else {
        const warnEl = document.createElement("div");
        warnEl.className = "version-java-warning";
        warnEl.id = "version-java-warning";
        warnEl.setAttribute("role", "alert");
        const span = document.createElement("span");
        span.textContent = javaWarning;
        warnEl.appendChild(span);
        metaRow.appendChild(warnEl);
      }
    } else if (existing) {
      existing.remove();
    }
  }

  function updateTopMeta(state) {
    const diagPill = document.getElementById("hub-diag-mem-pill");
    if (diagPill && state) {
      diagPill.textContent = `${state.memoryMb} MB`;
    }
    const hasInst = Boolean(state && Array.isArray(state.instances) && state.instances.length > 0);
    if (!hasInst && activeQuickPanel) {
      activeQuickPanel = null;
      const qSlot = document.getElementById("quick-panel-slot");
      if (qSlot) qSlot.innerHTML = "";
    }
    syncLaunchButtonUi(state);
    document.querySelectorAll(".emu-btn[data-quick-panel]").forEach((btn) => {
      btn.disabled = !hasInst;
      btn.classList.toggle("is-disabled", !hasInst);
      btn.setAttribute("aria-disabled", String(!hasInst));
      if (!hasInst) btn.classList.remove("is-active");
    });
  }

  /* ---------- Network Loader (yellow-quail-83), RareUI AnimatedCounter & JVM Auto-Grow ---------- */
  let netLoaderTimer = null;
  function showNetworkLoader(label, durationMs) {
    const pill = document.getElementById("network-loader-pill");
    if (!pill) return;
    pill.setAttribute("aria-label", label || "网络加载中");
    pill.setAttribute("aria-hidden", "false");
    pill.classList.add("is-visible");
    clearTimeout(netLoaderTimer);
    netLoaderTimer = setTimeout(() => {
      pill.classList.remove("is-visible");
      pill.setAttribute("aria-hidden", "true");
    }, durationMs || 1150);
  }

  function animateCounterElement(counterId, targetValue) {
    const root = document.getElementById(counterId);
    if (!root) return;
    const digitsBox = root.querySelector(".rare-counter-digits");
    if (!digitsBox) return;
    const num = Math.max(0, Math.round(Number(targetValue) || 0));
    const formatted = num.toLocaleString("en-US");
    root.setAttribute("data-value", String(num));

    const buildStripHtml = () =>
      "0123456789"
        .split("")
        .map((d) => `<span class="rare-digit-cell">${d}</span>`)
        .join("");

    const needRebuild =
      digitsBox.children.length !== formatted.length ||
      Array.from(digitsBox.children).some((el, idx) => {
        const ch = formatted[idx];
        return ch === ","
          ? !el.classList.contains("rare-mark-col")
          : el.classList.contains("rare-mark-col");
      });

    if (needRebuild) {
      const prevRaw = digitsBox.getAttribute("data-prev") || "";
      digitsBox.innerHTML = "";
      const totalLen = formatted.length;
      formatted.split("").forEach((ch, idx) => {
        const staggerIdx = Math.max(0, totalLen - 1 - idx);
        if (ch === ",") {
          const mark = document.createElement("span");
          mark.className = "rare-mark-col";
          mark.setAttribute("data-slot", "animated-counter-mark");
          mark.style.setProperty("--stagger-idx", String(staggerIdx));
          mark.textContent = ",";
          digitsBox.appendChild(mark);
        } else {
          const prevDigit = prevRaw[idx] && /\d/.test(prevRaw[idx]) ? Number(prevRaw[idx]) : 0;
          const col = document.createElement("span");
          col.className = "rare-digit-col";
          col.setAttribute("data-slot", "animated-counter-digit");
          col.style.setProperty("--stagger-idx", String(staggerIdx));
          col.innerHTML = `<span class="rare-digit-strip" style="transform: translateY(-${prevDigit * 10}%);">${buildStripHtml()}</span>`;
          digitsBox.appendChild(col);
        }
      });
    }

    digitsBox.setAttribute("data-prev", formatted);
    const totalLen = formatted.length;
    Array.from(digitsBox.children).forEach((col, i) => {
      const ch = formatted[i];
      if (ch === ",") return;
      col.style.setProperty("--stagger-idx", String(Math.max(0, totalLen - 1 - i)));
      const d = Number(ch) || 0;
      const strip = col.querySelector(".rare-digit-strip");
      if (strip) {
        requestAnimationFrame(() => {
          strip.style.transform = `translateY(-${d * 10}%)`;
        });
      }
    });
  }

  function syncRareTickBars(sliderId, mb) {
    const ticksEl = document.getElementById(`${sliderId}-ticks`);
    if (!ticksEl) return;
    const min = 1024;
    const step = 512;
    const activeIdx = Math.round((Math.max(1024, Math.min(16384, Number(mb) || 4096)) - min) / step);
    ticksEl.querySelectorAll(".rare-tick").forEach((tick, idx) => {
      tick.classList.toggle("is-filled", idx <= activeIdx);
      tick.classList.toggle("is-thumb", idx === activeIdx);
    });
  }

  async function applyRamValue(target, mb) {
    const clamped = Math.max(1024, Math.min(16384, Math.round(mb / 512) * 512));
    if (target === "quick") {
      const st = window.LauncherBridge.getStateSync();
      await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        memoryMb: clamped
      });
      const qSlider = document.getElementById("quick-ram-slider");
      if (qSlider) qSlider.value = clamped;
      syncRareTickBars("quick-ram-slider", clamped);
      animateCounterElement("quick-ram-counter", clamped);
      const qAutoBtn = document.getElementById("btn-toggle-quick-automem");
      if (qAutoBtn) {
        qAutoBtn.classList.remove("is-on");
        qAutoBtn.setAttribute("aria-pressed", "false");
      }
      const qRamBody = document.getElementById("quick-ram-controls");
      if (qRamBody) qRamBody.classList.remove("is-auto-dimmed");
      const toggleBtn = document.getElementById("btn-apply-global-settings");
      if (toggleBtn) {
        toggleBtn.classList.remove("is-on");
        toggleBtn.setAttribute("aria-pressed", "false");
      }
      const qBody = document.getElementById("quick-settings-body");
      if (qBody) qBody.classList.remove("is-global-dimmed");
      updateTopMeta(window.LauncherBridge.getStateSync());
      return;
    }

    await window.LauncherBridge.invoke("set_memory", { auto: false, mb: clamped });
    const st = window.LauncherBridge.getStateSync();
    const dSlider = document.getElementById("ram-slider");
    if (dSlider) dSlider.value = clamped;
    syncRareTickBars("ram-slider", clamped);
    const sw = document.getElementById("btn-toggle-automem");
    if (sw) {
      sw.classList.remove("is-on");
      sw.setAttribute("aria-pressed", "false");
    }
    const wrap = document.getElementById("diag-ram-controls");
    if (wrap) wrap.classList.remove("is-auto-dimmed");
    animateCounterElement("diag-ram-counter", clamped);
    const activeInst = window.LauncherViews.getActiveInstance(st);
    if (activeInst && activeInst.useGlobalSettings) {
      const qSlider = document.getElementById("quick-ram-slider");
      if (qSlider) qSlider.value = clamped;
      syncRareTickBars("quick-ram-slider", clamped);
      animateCounterElement("quick-ram-counter", clamped);
    }
    updateTopMeta(st);
  }

  function syncJvmTextareaHeight(el) {
    if (!el) return;
    const prevHeight = el.getBoundingClientRect().height || 44;
    el.style.height = "auto";
    const nextHeight = Math.max(44, el.scrollHeight + 2);
    if (!prefersReducedMotion() && Math.abs(prevHeight - nextHeight) > 1) {
      el.style.height = `${prevHeight}px`;
      void el.offsetHeight;
      el.style.height = `${nextHeight}px`;
    } else {
      el.style.height = `${nextHeight}px`;
    }
  }

  function syncAllJvmTextareas() {
    document.querySelectorAll(".quick-jvm-textarea").forEach((ta) => {
      syncJvmTextareaHeight(ta);
    });
  }

  /* ---------- ReactBits Gooey Nav Liquid Canvas & Active Clip-Path Engine ---------- */
  const gooeyStates = new Map();

  function getGooeyState(navId) {
    if (!gooeyStates.has(navId)) {
      gooeyStates.set(navId, { rafId: null, currentBox: null, particles: [] });
    }
    return gooeyStates.get(navId);
  }

  function travelEase(t) {
    return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
  }

  function setGooeyActiveClip(nav, box, stageW, stageH) {
    if (!nav) return;
    const activeList = nav.querySelector(".gooey-nav__list--active");
    const fallback = nav.querySelector(".gooey-nav__fallback");
    if (fallback && box) {
      fallback.style.transform = `translate(${box.x}px, ${box.y}px)`;
      fallback.style.width = `${box.w}px`;
      fallback.style.height = `${box.h}px`;
    }
    if (!activeList || !box || stageW <= 0 || stageH <= 0) return;
    const top = Math.max(0, box.y);
    const right = Math.max(0, stageW - (box.x + box.w));
    const bottom = Math.max(0, stageH - (box.y + box.h));
    const left = Math.max(0, box.x);
    activeList.style.visibility = "visible";
    activeList.style.clipPath = `inset(${top}px ${right}px ${bottom}px ${left}px round 999px)`;
  }

  function measureGooeyButtonBox(btn, stage) {
    if (!btn || !stage) return null;
    const br = btn.getBoundingClientRect();
    const sr = stage.getBoundingClientRect();
    return {
      x: br.left - sr.left,
      y: br.top - sr.top,
      w: br.width,
      h: br.height
    };
  }

  function drawGooeyPill(ctx, x, y, w, h) {
    const r = Math.min(h / 2, w / 2);
    ctx.beginPath();
    ctx.roundRect(x, y, w, h, r);
    ctx.fill();
  }

  function syncGooeyNav(navId, withExplosion) {
    const nav = document.getElementById(navId);
    if (!nav) return;
    const list = nav.querySelector(".gooey-nav__list:not(.gooey-nav__list--active)");
    const canvas = nav.querySelector(".gooey-nav__canvas");
    const stage = nav.querySelector(".gooey-nav__stage");
    if (!list || !canvas || !stage) return;
    const activeBtn = list.querySelector(".gooey-nav__link.is-active") || list.querySelector(".gooey-nav__link");
    if (!activeBtn) return;

    const st = getGooeyState(navId);
    const stageRect = stage.getBoundingClientRect();
    const w = Math.max(1, Math.ceil(stageRect.width));
    const h = Math.max(1, Math.ceil(stageRect.height));
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    canvas.style.width = `${w}px`;
    canvas.style.height = `${h}px`;

    const targetBox = measureGooeyButtonBox(activeBtn, stage);
    if (!targetBox || targetBox.w <= 0) return;

    const ctx = canvas.getContext("2d");
    if (!ctx) {
      setGooeyActiveClip(nav, targetBox, w, h);
      return;
    }
    nav.classList.add("has-canvas");
    const accentColor = getComputedStyle(document.documentElement).getPropertyValue("--accent-primary").trim() || "#f5c518";

    if (!withExplosion || !st.currentBox || prefersReducedMotion()) {
      st.currentBox = { ...targetBox };
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = accentColor;
      drawGooeyPill(ctx, targetBox.x, targetBox.y, targetBox.w, targetBox.h);
      setGooeyActiveClip(nav, targetBox, w, h);
      return;
    }

    const fromBox = { ...st.currentBox };
    const startCx = fromBox.x + fromBox.w / 2;
    const startCy = fromBox.y + fromBox.h / 2;
    const endCx = targetBox.x + targetBox.w / 2;
    const endCy = targetBox.y + targetBox.h / 2;
    const dir = endCx >= startCx ? 1 : -1;

    st.particles = Array.from({ length: 14 }, (_, idx) => ({
      t0: (idx % 4) * 0.05,
      dur: 0.55 + Math.random() * 0.3,
      r: 3 + Math.random() * 4.5,
      arcY: (Math.random() - 0.5) * 26,
      spreadX: dir * (Math.random() * 14 - 4)
    }));

    cancelAnimationFrame(st.rafId);
    const startTime = performance.now();
    const duration = 560;

    const tick = (now) => {
      const rawT = Math.min(1, (now - startTime) / duration);
      const ease = travelEase(rawT);
      const curX = fromBox.x + (targetBox.x - fromBox.x) * ease;
      const curY = fromBox.y + (targetBox.y - fromBox.y) * ease;
      const stretch = Math.sin(rawT * Math.PI) * Math.min(26, Math.abs(endCx - startCx) * 0.22);
      const curW = fromBox.w + (targetBox.w - fromBox.w) * ease + stretch;
      const curH = fromBox.h + (targetBox.h - fromBox.h) * ease - Math.sin(rawT * Math.PI) * 3;
      const drawX = curX - stretch * 0.5;
      const drawY = curY + (fromBox.h - curH) * 0.5;

      st.currentBox = { x: drawX, y: drawY, w: curW, h: curH };
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, w, h);
      ctx.fillStyle = accentColor;

      drawGooeyPill(ctx, drawX, drawY, curW, curH);

      st.particles.forEach((p) => {
        const pt = Math.max(0, Math.min(1, (rawT - p.t0) / p.dur));
        if (pt <= 0 || pt >= 1) return;
        const u = 1 - pt;
        const ctrlX = (startCx + endCx) * 0.5 + p.spreadX;
        const ctrlY = (startCy + endCy) * 0.5 + p.arcY;
        const px = u * u * startCx + 2 * u * pt * ctrlX + pt * pt * endCx;
        const py = u * u * startCy + 2 * u * pt * ctrlY + pt * pt * endCy;
        const pr = p.r * Math.sin(pt * Math.PI);
        if (pr > 0.3) {
          ctx.beginPath();
          ctx.arc(px, py, pr, 0, Math.PI * 2);
          ctx.fill();
        }
      });

      setGooeyActiveClip(nav, st.currentBox, w, h);

      if (rawT < 1) {
        st.rafId = requestAnimationFrame(tick);
      } else {
        st.currentBox = { ...targetBox };
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, w, h);
        ctx.fillStyle = accentColor;
        drawGooeyPill(ctx, targetBox.x, targetBox.y, targetBox.w, targetBox.h);
        setGooeyActiveClip(nav, targetBox, w, h);
      }
    };
    st.rafId = requestAnimationFrame(tick);
  }

  function syncAllGooeyNavs(withExplosion) {
    syncGooeyNav("java-gooey-nav", withExplosion);
    syncGooeyNav("quick-gooey-nav", withExplosion);
  }

  /* ---------- Ventriloc-Inspired Standard Damped Scroll Engine (amt = 0.1) ---------- */
  const DAMPING_AMT = 0.1;
  const DAMPING_THRESHOLD = 0.5;
  let scrollCurrentY = window.scrollY || 0;
  let scrollTargetY = scrollCurrentY;
  let scrollRafId = null;
  let scrollLastTime = 0;
  let isWritingScroll = false;
  let scrollSettledCallback = null;

  function getMaxScrollY() {
    return Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
  }

  function prefersReducedMotion() {
    return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  }

  function stepDampedScroll(now) {
    if (!scrollLastTime) scrollLastTime = now;
    const dt = Math.min(64, Math.max(4, now - scrollLastTime));
    scrollLastTime = now;

    const maxY = getMaxScrollY();
    scrollTargetY = Math.max(0, Math.min(maxY, scrollTargetY));
    const delta = scrollTargetY - scrollCurrentY;

    if (Math.abs(delta) <= DAMPING_THRESHOLD) {
      scrollCurrentY = scrollTargetY;
      isWritingScroll = true;
      window.scrollTo(0, Math.round(scrollCurrentY));
      isWritingScroll = false;
      scrollRafId = null;
      scrollLastTime = 0;
      const cb = scrollSettledCallback;
      scrollSettledCallback = null;
      if (cb) cb();
      updateNavControls();
      return;
    }

    // Framerate-independent Ventriloc damping: 1 - (1 - amt)^(dt/16.6667)
    const factor = 1 - Math.pow(1 - DAMPING_AMT, dt / 16.6667);
    scrollCurrentY += delta * factor;
    isWritingScroll = true;
    window.scrollTo(0, Math.round(scrollCurrentY));
    isWritingScroll = false;
    updateNavControls();
    scrollRafId = requestAnimationFrame(stepDampedScroll);
  }

  function dampedScrollTo(targetY, onSettled) {
    const clamped = Math.max(0, Math.min(getMaxScrollY(), targetY));
    scrollSettledCallback = typeof onSettled === "function" ? onSettled : null;

    if (prefersReducedMotion() || Math.abs(window.scrollY - clamped) <= 2) {
      scrollCurrentY = clamped;
      scrollTargetY = clamped;
      window.scrollTo(0, clamped);
      const cb = scrollSettledCallback;
      scrollSettledCallback = null;
      if (cb) cb();
      updateNavControls();
      return;
    }
    scrollCurrentY = window.scrollY;
    scrollTargetY = clamped;
    if (!scrollRafId) {
      scrollLastTime = 0;
      scrollRafId = requestAnimationFrame(stepDampedScroll);
    }
  }

  function stopDampedScroll() {
    if (scrollRafId !== null) cancelAnimationFrame(scrollRafId);
    scrollRafId = null;
    scrollLastTime = 0;
    scrollCurrentY = scrollTargetY = window.scrollY;
    scrollSettledCallback = null;
  }

  function usesNativeListScrolling() {
    const route = activeSubRoute || currentRoute;
    return route.startsWith("download-") || route === "manage-mods" ||
      currentRoute === "resource-detail" || currentRoute === "download-queue";
  }

  function isInsideScrollableBox(el) {
    let cur = el;
    while (cur && cur !== document.body && cur !== document.documentElement) {
      const style = getComputedStyle(cur);
      if (
        (style.overflowY === "auto" || style.overflowY === "scroll") &&
        cur.scrollHeight > cur.clientHeight + 2
      ) {
        return true;
      }
      cur = cur.parentElement;
    }
    return false;
  }

  window.addEventListener(
    "wheel",
    (e) => {
      if (prefersReducedMotion() || installDialog.open || microsoftDialog.open) return;
      if (usesNativeListScrolling()) {
        if (scrollRafId !== null) {
          const onSettled = scrollSettledCallback;
          stopDampedScroll();
          onSettled?.();
        }
        return;
      }
      if (portalBackdrop.classList.contains("is-open") || drawerOverlay.classList.contains("is-open")) return;
      if (isInsideScrollableBox(e.target)) return;
      e.preventDefault();
      scrollTargetY = Math.max(0, Math.min(getMaxScrollY(), scrollTargetY + e.deltaY));
      if (!scrollRafId) {
        scrollCurrentY = window.scrollY;
        scrollLastTime = 0;
        scrollRafId = requestAnimationFrame(stepDampedScroll);
      }
    },
    { passive: false }
  );

  window.addEventListener(
    "scroll",
    () => {
      if (!isWritingScroll && !scrollRafId) {
        scrollCurrentY = window.scrollY;
        scrollTargetY = window.scrollY;
      }
      updateNavControls();
    },
    { passive: true }
  );

  function getStickyTopbarHeight() {
    const bar = document.querySelector(".topbar");
    return bar ? bar.getBoundingClientRect().height : 64;
  }

  function scrollToElementTop(el, gapPx, { immediate = false } = {}) {
    if (!el) return;
    const topbarH = getStickyTopbarHeight();
    const targetY = el.getBoundingClientRect().top + window.scrollY - topbarH - (gapPx || 8);
    if (immediate) {
      stopDampedScroll();
      window.scrollTo({ top: targetY, behavior: "instant" });
      return;
    }
    dampedScrollTo(targetY);
  }

  function observeReveals() {
    const blocks = document.querySelectorAll(".reveal-block");
    if (!("IntersectionObserver" in window)) {
      blocks.forEach((b) => b.classList.add("is-visible"));
    } else {
      const io = new IntersectionObserver(
        (entries) => {
          entries.forEach((entry) => {
            if (entry.isIntersecting) {
              entry.target.classList.add("is-visible");
              io.unobserve(entry.target);
            }
          });
        },
        { threshold: 0.08 }
      );
      blocks.forEach((b) => io.observe(b));
    }

    const vTargets = document.querySelectorAll(".v-reveal, .v-row-reveal");
    if (!("IntersectionObserver" in window)) {
      vTargets.forEach((el) => el.classList.add("is-inview"));
      return;
    }
    const vio = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add("is-inview");
            vio.unobserve(entry.target);
          }
        });
      },
      { rootMargin: "0px 0px -20px 0px", threshold: 0.02 }
    );
    vTargets.forEach((el) => vio.observe(el));
  }

  function updateNavControls() {
    if (btnNavBack) {
      const canGoBack = currentRoute !== "home" || activeHubKey !== null || activeSubRoute !== null || navHistory.length > 0;
      btnNavBack.classList.toggle("is-disabled", !canGoBack);
    }
    if (btnNavHome) {
      const canGoHome = currentRoute !== "home" || activeHubKey !== null || activeSubRoute !== null || window.scrollY > 40;
      btnNavHome.classList.toggle("is-disabled", !canGoHome);
    }
  }

  /* ---------- Zero-Flash Surgical UI Updaters ---------- */
  function syncInstanceUi(state, shouldAnimate, animateStrokeText) {
    updateTopMeta(state);
    const active = window.LauncherViews.getActiveInstance(state);

    const artSlot = document.getElementById("version-art-slot");
    if (artSlot && (animateStrokeText || !artSlot.hasChildNodes())) {
      artSlot.innerHTML = window.LauncherViews.renderVersionArtHero(active, Boolean(animateStrokeText), state);
    } else {
      syncVersionJavaWarningUi(state);
    }

    document.querySelectorAll(".folder-item[data-select-inst], .gallery-folder-card[data-gallery-pick-inst]").forEach((btn) => {
      const instId = btn.getAttribute("data-select-inst") || btn.getAttribute("data-gallery-pick-inst");
      const isAct = instId === state.activeInstanceId;
      btn.classList.toggle("is-active", isAct);
      const instData = state.instances.find((i) => i.id === instId);
      if (instData) {
        const embBox = btn.querySelector(".folder-emblem");
        const embImg = btn.querySelector(".folder-emblem img");
        const im = window.LauncherViews.getIconMeta(instData.iconId);
        if (embImg && embImg.getAttribute("src") !== im.src) {
          embImg.src = im.src;
          embImg.alt = im.name;
        }
        if (embBox && isAct && shouldAnimate) {
          embBox.classList.remove("is-icon-changing");
          void embBox.offsetWidth;
          embBox.classList.add("is-icon-changing");
          setTimeout(() => {
            embBox.classList.remove("is-icon-changing");
          }, 700);
        }
        const sheetCode = btn.querySelector(".sheet-code");
        if (sheetCode) sheetCode.textContent = instData.javaVersion;
      }
    });

    document.querySelectorAll(".icon-choice-btn[data-set-icon]").forEach((btn) => {
      btn.classList.toggle("is-selected", btn.getAttribute("data-set-icon") === (active.iconId || "grass"));
    });

    // Surgically update open quick panel fields in-place so switching folders NEVER flashes/replaces DOM
    const openPanel = document.getElementById("quick-panel");
    if (openPanel && activeQuickPanel === "overview") {
      const kvVals = openPanel.querySelectorAll(".quick-kv-val");
      if (kvVals.length >= 3) {
        kvVals[0].textContent = active.loader;
        kvVals[1].textContent = active.loaderVersion;
        kvVals[2].textContent = active.mcVersion;
      }
      syncInstanceColorUi(state, false);
    } else if (openPanel && activeQuickPanel === "settings") {
      const effectiveMemMb = active.useGlobalSettings ? state.memoryMb : (active.memoryMb || state.memoryMb);
      const effectiveJvmArgs = active.useGlobalSettings ? state.customJvmArgs : (active.jvmArgs || state.customJvmArgs);
      const qNav = document.getElementById("quick-gooey-nav");
      if (qNav) {
        qNav.querySelectorAll(".gooey-nav__link[data-select-quick-java]").forEach((btn) => {
          const isAct = btn.getAttribute("data-select-quick-java") === active.javaId;
          btn.classList.toggle("is-active", isAct);
          btn.setAttribute("aria-selected", String(isAct));
        });
        qNav.querySelectorAll(".gooey-nav__list--active .gooey-nav__link").forEach((sp) => {
          sp.classList.toggle("is-active", sp.getAttribute("data-gooey-mirror") === active.javaId);
        });
        syncGooeyNav("quick-gooey-nav", true);
      }
      const qJavaMeta = document.getElementById("quick-java-meta");
      const selJ = (state.javas || []).find((j) => j.id === active.javaId) || (state.javas || [])[0];
      if (qJavaMeta && selJ) qJavaMeta.textContent = `Java ${selJ.version} · ${selJ.vendor}`;
      const ramSlider = document.getElementById("quick-ram-slider");
      if (ramSlider) ramSlider.value = effectiveMemMb;
      syncRareTickBars("quick-ram-slider", effectiveMemMb);
      animateCounterElement("quick-ram-counter", effectiveMemMb);
      const effectiveAutoMem = active.useGlobalSettings ? Boolean(state.autoMemory) : Boolean(active.autoMemory);
      const qAutoBtn = document.getElementById("btn-toggle-quick-automem");
      if (qAutoBtn) {
        qAutoBtn.classList.toggle("is-on", effectiveAutoMem);
        qAutoBtn.setAttribute("aria-pressed", String(effectiveAutoMem));
      }
      const qRamControls = document.getElementById("quick-ram-controls");
      if (qRamControls) qRamControls.classList.toggle("is-auto-dimmed", effectiveAutoMem);
      const globalBtn = document.getElementById("btn-apply-global-settings");
      if (globalBtn) {
        globalBtn.classList.toggle("is-on", Boolean(active.useGlobalSettings));
        globalBtn.setAttribute("aria-pressed", String(Boolean(active.useGlobalSettings)));
      }
      const qBody = document.getElementById("quick-settings-body");
      if (qBody) qBody.classList.toggle("is-global-dimmed", Boolean(active.useGlobalSettings));
      const jvmInput = document.getElementById("quick-jvm-args");
      if (jvmInput) {
        jvmInput.value = effectiveJvmArgs;
        syncJvmTextareaHeight(jvmInput);
      }
      document.querySelectorAll("[data-quick-jvm-preset]").forEach((b) => {
        const p = b.getAttribute("data-quick-jvm-preset") || "";
        const match = (p.includes("UseZGC") && effectiveJvmArgs.includes("UseZGC"))
          || (p.includes("UseG1GC") && effectiveJvmArgs.includes("UseG1GC"))
          || (p.includes("UseShenandoahGC") && effectiveJvmArgs.includes("UseShenandoahGC"));
        b.classList.toggle("is-active", match);
      });
    } else if (openPanel && activeQuickPanel === "mods") {
      syncQuickPanelUi(state, { quiet: true });
    } else if (openPanel && activeQuickPanel === "folders") {
      const firstSub = openPanel.querySelector(".quick-folder-btn .sub");
      const firstBtn = openPanel.querySelector(".quick-folder-btn");
      const rootLabel = active.externalPath || (active.isolated ? `versions\\${active.id}` : ".minecraft");
      if (firstSub) {
        firstSub.textContent = rootLabel;
      }
      if (firstBtn) {
        firstBtn.dataset.tooltip = rootLabel;
      }
    }

    if (activeSubRoute === "manage-instances" || activeSubRoute === "manage-mods") {
      syncSubWorkspaceSlot(state, { preserveAnimation: true });
    }
  }

  let quickSlotTimer = null;
  let lastDockCaptionText = "";

  function setDockCaption(text) {
    const cap = document.getElementById("quick-dock-caption");
    if (!cap) return;
    const next = String(text || "");
    if (next === lastDockCaptionText && Boolean(cap.firstElementChild) === Boolean(next)) return;
    lastDockCaptionText = next;
    cap.innerHTML = next ? `<strong>${next}</strong>` : "";
  }

  function syncPlayerAccountUi(state) {
    const slot = document.getElementById("player-account-slot");
    if (slot) {
      slot.innerHTML = window.LauncherViews.renderPlayerAccountControl(state, accountMenuOpen, offlineAddOpen);
    }
    if (activeSubRoute === "auth-java") {
      syncSubWorkspaceSlot(state, { preserveAnimation: true });
    }
  }

  function syncFloatingDownloadBallUi(state) {
    const slot = document.getElementById("floating-dl-ball-slot");
    if (!slot) return;
    if (currentRoute === "download-queue") {
      slot.innerHTML = "";
      return;
    }
    slot.innerHTML = window.LauncherViews.renderFloatingDownloadBall(state);
  }

  function syncQuickPanelUi(state, opts) {
    const active = window.LauncherViews.getActiveInstance(state);
    const labels = { overview: "概览", settings: "版本设置", mods: "Mod 管理", folders: "版本文件夹" };
    document.querySelectorAll(".emu-btn[data-quick-panel]").forEach((btn) => {
      btn.classList.toggle("is-active", btn.getAttribute("data-quick-panel") === activeQuickPanel);
    });
    setDockCaption(activeQuickPanel ? labels[activeQuickPanel] || "" : "");
    const slot = document.getElementById("quick-panel-slot");
    if (!slot) return;

    clearTimeout(quickSlotTimer);
    const startH = slot.scrollHeight;
    const newHtml = window.LauncherViews.renderQuickPanel(state, active, activeQuickPanel, getViewOpts());

    if (!activeQuickPanel && opts && opts.animateClose) {
      const existing = slot.querySelector(".quick-panel");
      if (existing) existing.classList.add("is-closing");
      if (!prefersReducedMotion() && startH > 0) {
        slot.style.height = `${startH}px`;
        slot.style.overflow = "hidden";
        void slot.offsetHeight;
        slot.style.height = "0px";
        let settled = false;
        const finishClose = () => {
          if (settled) return;
          settled = true;
          slot.removeEventListener("transitionend", onEnd);
          clearTimeout(quickSlotTimer);
          if (!activeQuickPanel) {
            slot.innerHTML = "";
            slot.style.height = "";
            slot.style.overflow = "";
          }
        };
        const onEnd = (ev) => {
          if (ev.target === slot && ev.propertyName === "height") finishClose();
        };
        slot.addEventListener("transitionend", onEnd);
        quickSlotTimer = setTimeout(finishClose, 305);
      } else {
        slot.innerHTML = "";
        slot.style.height = "";
        slot.style.overflow = "";
      }
      return;
    }

    if (opts && opts.quiet) {
      slot.style.height = "";
      slot.style.overflow = "";
      slot.innerHTML = newHtml;
      const panel = slot.querySelector(".quick-panel");
      if (panel) panel.style.animation = "none";
      slot.querySelectorAll(".v-row-reveal").forEach((r) => {
        r.style.animation = "none";
        r.classList.add("is-inview");
      });
      requestAnimationFrame(() => {
        syncAllJvmTextareas();
        syncAllGooeyNavs(false);
      });
      return;
    }

    slot.style.height = "";
    slot.style.overflow = "hidden";
    slot.innerHTML = newHtml;
    const nextPanel = slot.querySelector(".quick-panel");
    if (nextPanel && startH > 0) {
      nextPanel.style.animation = "none";
    }
    slot.querySelectorAll(".v-row-reveal").forEach((r) => {
      r.style.animation = "none";
      r.classList.add("is-inview");
    });
    syncAllJvmTextareas();
    const targetH = slot.scrollHeight;
    requestAnimationFrame(() => {
      syncAllJvmTextareas();
      syncAllGooeyNavs(false);
    });
    if (!prefersReducedMotion() && startH !== targetH) {
      slot.style.height = `${startH}px`;
      void slot.offsetHeight;
      slot.style.height = `${targetH}px`;
      let settled = false;
      const finishOpen = () => {
        if (settled) return;
        settled = true;
        slot.removeEventListener("transitionend", onOpenEnd);
        clearTimeout(quickSlotTimer);
        slot.style.height = "";
        slot.style.overflow = "";
        syncAllJvmTextareas();
        syncAllGooeyNavs(false);
      };
      const onOpenEnd = (ev) => {
        if (ev.target === slot && ev.propertyName === "height") finishOpen();
      };
      slot.addEventListener("transitionend", onOpenEnd);
      quickSlotTimer = setTimeout(finishOpen, 305);
    } else {
      slot.style.overflow = "";
    }
  }

  /* Update ONLY lower #hub-outlet without refreshing upper hero (Requirement 6) */
  function syncHubOutletUi(opts) {
    const o = opts || {};
    const state = window.LauncherBridge.getStateSync();
    const outlet = document.getElementById("hub-outlet");
    document.querySelectorAll(".micro-trigger[data-portal]").forEach((btn) => {
      btn.classList.toggle("is-active", btn.getAttribute("data-portal") === activeHubKey);
    });
    if (!outlet) {
      render(o);
      return;
    }
    const activeHubConfig =
      activeHubKey && PORTAL_CATALOG[activeHubKey]
        ? { key: activeHubKey, ...PORTAL_CATALOG[activeHubKey] }
        : null;
    outlet.innerHTML = activeHubConfig
      ? window.LauncherViews.renderHubSection(activeHubConfig, state, activeSubRoute, searchQuery, getViewOpts())
      : "";
    observeReveals();
    if (activeHubConfig) {
      initMasonryMotion(!o.animateMasonry);
    }
    requestAnimationFrame(() => {
      syncAllGooeyNavs(false);
      syncAllJvmTextareas();
    });
    updateNavControls();
  }

  function syncLaunchMeterUi() {
    const slot = document.getElementById("redcow-slot");
    if (slot) {
      slot.innerHTML = window.LauncherViews.renderLaunchMeter(launchProgress);
    }
  }

  function syncMirrorChipsUi(state) {
    const mirrors = state.mirrors || [];
    const activeMirror = mirrors.find(mirror => mirror.name === state.mirrorSource);
    const newLabel = state.autoMirror ? `最优 · ${state.mirrorSource}` : state.mirrorSource;
    const updatePing = (element, ms) => {
      const status = window.LauncherViews.mirrorStatus(ms);
      if (element) {
        element.textContent = status.text;
        element.className = `mirror-ping ${status.tone}`;
      }
      return status.text;
    };
    document.querySelectorAll(".mirror-starfish-trigger:not(.mcver-starfish-trigger)").forEach(trigger => {
      const name = trigger.querySelector(".mirror-trigger-name");
      if (name && name.textContent !== newLabel) {
        name.textContent = newLabel;
        trigger.classList.remove("is-switching");
        requestAnimationFrame(() => trigger.classList.add("is-switching"));
      }
      const ping = updatePing(trigger.querySelector(".mirror-ping"), activeMirror?.pingMs);
      trigger.setAttribute("aria-label", `切换下载源（${newLabel} ${ping}）`);
    });
    document.querySelectorAll("[data-auto-mirror]").forEach((btn) => {
      btn.classList.toggle("is-active", Boolean(state.autoMirror));
      if (!btn.classList.contains("mirror-tray-item")) {
        btn.textContent = btn.textContent.includes("最优镜像")
          ? `最优镜像 · ${state.mirrorSource}`
          : `最优: ${state.mirrorSource}`;
      }
    });
    document.querySelectorAll("[data-mirror]").forEach((btn) => {
      const mName = btn.getAttribute("data-mirror");
      btn.classList.toggle("is-active", state.mirrorSource === mName && !state.autoMirror);
      const pingEl = btn.querySelector(".mirror-ping");
      const node = mirrors.find((x) => x.name === mName);
      updatePing(pingEl, node?.pingMs);
    });
    if (activeSubRoute === "runtime-mirrors") {
      syncSubWorkspaceSlot(state, { preserveAnimation: true });
    }
  }

  function syncDownloadQueueEyebrow(state) {
    const tasks = state.downloadTasks || [];
    const activeCount = tasks.filter((t) => t.status === "downloading").length;
    const completedCount = tasks.filter((t) => t.status === "completed").length;
    const eyebrow = document.getElementById("dl-active-eyebrow");
    if (eyebrow) eyebrow.textContent = `DOWNLOAD QUEUE // ACTIVE ${activeCount}`;
    const clearBtn = document.getElementById("btn-clear-completed-dl");
    if (clearBtn && completedCount === 0) clearBtn.remove();
    const listEl = document.getElementById("dl-queue-list");
    if (listEl && tasks.length === 0 && !listEl.querySelector(".data-row")) {
      listEl.innerHTML = '<div class="data-row data-row--empty"><div>空空如也X_X</div></div>';
    }
  }

  let filterSlotTimer = null;
  let filterSlotTransitionFn = null;
  function clearFilterSlotPending(slot) {
    clearTimeout(filterSlotTimer);
    filterSlotTimer = null;
    if (slot && filterSlotTransitionFn) {
      slot.removeEventListener("transitionend", filterSlotTransitionFn);
      filterSlotTransitionFn = null;
    }
  }
  function ensureMcVersionTrayPopulated(searchText) {
    const listEl = document.getElementById("mcver-tray-list");
    if (!listEl) return;
    const isFiltered = typeof searchText === "string";
    if (!isFiltered && listEl.dataset.lazyMcver !== "true") return;
    listEl.dataset.lazyMcver = "false";
    listEl.innerHTML = window.LauncherViews.renderMcVersionTrayListHtml(
      window.LauncherBridge.getStateSync(),
      mcVersionFilter,
      isFiltered ? searchText : ""
    );
  }
  function syncFilterSlotUi(opts) {
    const slot = document.getElementById("filter-popover-slot");
    const subKey = activeSubRoute || currentRoute;
    const hasFilter = loaderFilter !== "all" || sourceFilter !== "all" || sortFilter !== "downloads" || categoryFilter !== "all" || subCategoryFilter !== "all" || mcVersionFilter !== "all";
    const filterBtn = document.getElementById("btn-toggle-filter");
    if (filterBtn) filterBtn.classList.toggle("is-active", filterOpen || hasFilter);
    if (!slot) return;

    clearFilterSlotPending(slot);
    const finishAfterTransition = (done) => {
      const onEnd = (ev) => {
        if (ev.target !== slot || ev.propertyName !== "grid-template-rows") return;
        clearFilterSlotPending(slot);
        done();
      };
      filterSlotTransitionFn = onEnd;
      slot.addEventListener("transitionend", onEnd);
      filterSlotTimer = setTimeout(() => {
        clearFilterSlotPending(slot);
        done();
      }, 260);
    };

    if (!filterOpen) {
      slot.classList.remove("is-settled");
      if (slot.childElementCount > 0 && !prefersReducedMotion()) {
        document.getElementById("cat-starfish-wrap")?.classList.remove("is-open");
        document.getElementById("mcver-starfish-wrap")?.classList.remove("is-open");
        slot.classList.remove("is-open");
        finishAfterTransition(() => {
          if (!filterOpen) {
            slot.innerHTML = "";
          }
        });
      } else {
        slot.classList.remove("is-open");
        slot.innerHTML = "";
      }
      return;
    }

    const html = window.LauncherViews.renderFilterPopoverHtml(subKey, getViewOpts(), window.LauncherBridge.getStateSync());
    slot.classList.remove("is-settled");
    slot.innerHTML = html;
    if (!(opts && opts.quiet) && !prefersReducedMotion()) {
      slot.classList.remove("is-open");
      requestAnimationFrame(() => {
        if (!filterOpen) return;
        slot.classList.add("is-open");
        finishAfterTransition(() => {
          if (filterOpen) slot.classList.add("is-settled");
        });
      });
    } else {
      slot.classList.add("is-open", "is-settled");
    }
  }

  function animateDampedSlide(el, dy) {
    if (!el || prefersReducedMotion() || typeof el.animate !== "function") return;
    el.animate(
      [
        { opacity: 0.32, transform: `translate3d(0, ${dy || 14}px, 0)` },
        { opacity: 1, transform: "translate3d(0, 0, 0)" }
      ],
      { duration: 360, easing: "cubic-bezier(0.215, 0.61, 0.355, 1)" }
    );
  }

  function animateHorizontalPageSlide(el, dir) {
    if (!el || prefersReducedMotion() || typeof el.animate !== "function") return;
    const dx = (dir >= 0 ? 1 : -1) * 28;
    el.animate(
      [
        { opacity: 0.58, transform: `translate3d(${dx}px, 0, 0)` },
        { opacity: 1, transform: "translate3d(0, 0, 0)" }
      ],
      { duration: 440, easing: "cubic-bezier(0.16, 1, 0.30, 1)" }
    );
  }

  function runGridFlip(containerEl, selector, getKey, mutateFn) {
    if (!containerEl) return;
    const reduced = prefersReducedMotion();
    const firstRects = new Map();
    if (!reduced) {
      containerEl.querySelectorAll(selector).forEach((el) => {
        const key = getKey(el);
        if (key) firstRects.set(key, el.getBoundingClientRect());
      });
    }
    mutateFn();
    if (reduced) return;

    const afterEls = Array.from(containerEl.querySelectorAll(selector));
    const moved = [];
    afterEls.forEach((el) => {
      const key = getKey(el);
      const prev = key ? firstRects.get(key) : null;
      const next = el.getBoundingClientRect();
      if (prev) {
        const dx = prev.left - next.left;
        const dy = prev.top - next.top;
        if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
          el.style.transition = "none";
          el.style.transform = `translate3d(${dx}px, ${dy}px, 0)`;
          moved.push({ el, isNew: false });
        }
      } else {
        el.style.transition = "none";
        el.style.opacity = "0";
        el.style.transform = "translate3d(0, 14px, 0) scale(0.92)";
        moved.push({ el, isNew: true });
      }
    });
    if (moved.length === 0) return;

    void containerEl.offsetHeight;
    requestAnimationFrame(() => {
      moved.forEach(({ el, isNew }) => {
        el.style.transition =
          "transform 460ms cubic-bezier(0.215, 0.61, 0.355, 1), opacity 300ms ease";
        el.style.transform = "translate3d(0, 0, 0)";
        if (isNew) el.style.opacity = "1";
        setTimeout(() => {
          el.style.transition = "";
          el.style.transform = "";
          el.style.opacity = "";
        }, 480);
      });
    });
  }

  function applyCardFavoriteBadge(cardEl, instId, isFav, withPop) {
    if (!cardEl) return;
    const favBtn = cardEl.querySelector(`[data-folder-fav="${instId}"]`);
    if (favBtn) {
      favBtn.classList.toggle("is-faved", Boolean(isFav));
      favBtn.setAttribute("aria-pressed", String(Boolean(isFav)));
      favBtn.setAttribute("title", isFav ? "取消收藏" : "收藏此版本");
      const svg = favBtn.querySelector("svg");
      if (svg) svg.setAttribute("fill", isFav ? "currentColor" : "none");
    }
    const verEl = cardEl.querySelector(".folder-front-ver");
    if (verEl) {
      const existingHeart = verEl.querySelector(".folder-fav-heart");
      if (isFav && !existingHeart) {
        const span = document.createElement("span");
        span.className = `folder-fav-heart${withPop && !prefersReducedMotion() ? " is-popping" : ""}`;
        span.setAttribute("aria-label", "已收藏");
        span.textContent = "♥";
        verEl.appendChild(span);
        if (withPop) {
          setTimeout(() => span.classList.remove("is-popping"), 560);
        }
      } else if (!isFav && existingHeart) {
        existingHeart.remove();
      }
    }
  }

  function clearHeroReturningClass() {
    const hero = document.querySelector(".hero-section.is-returning");
    if (hero) hero.classList.remove("is-returning");
  }

  function syncFolderShelfUi(state, opts) {
    clearHeroReturningClass();
    const region = document.getElementById("folder-shelf-region");
    if (!region) return;
    const favs = window.LauncherViews.getFavoriteInstances(state);
    const totalPages = Math.max(1, Math.ceil(favs.length / 6));
    if (favPage > totalPages) favPage = totalPages;
    if (favPage < 1) favPage = 1;

    const tmp = document.createElement("div");
    tmp.innerHTML = window.LauncherViews.renderFolderShelfContent(state, favPage);
    const nextShelf = tmp.querySelector(".folder-shelf");
    const nextPager = tmp.querySelector(".folder-shelf-pager");
    const curShelf = region.querySelector(".folder-shelf");
    const curPager = region.querySelector(".folder-shelf-pager");

    if (opts && opts.flip && curShelf && nextShelf) {
      const nextCards = Array.from(nextShelf.children);
      const nextIds = nextCards.map((c) => c.getAttribute("data-select-inst")).filter(Boolean);
      const nextIdSet = new Set(nextIds);
      runGridFlip(curShelf, ".folder-item", (el) => el.getAttribute("data-select-inst"), () => {
        // Remove only departed cards so surviving nodes stay attached cleanly
        Array.from(curShelf.querySelectorAll(".folder-item")).forEach((el) => {
          const id = el.getAttribute("data-select-inst");
          if (!id || !nextIdSet.has(id)) {
            el.remove();
          } else {
            el.style.animation = "none";
            el.classList.remove("is-folder-removing");
            el.classList.toggle("is-active", id === state.activeInstanceId);
          }
        });
        const existingIds = new Set(
          Array.from(curShelf.querySelectorAll(".folder-item")).map((el) => el.getAttribute("data-select-inst"))
        );
        // Append any newly pulled-in cards
        nextCards.forEach((nextEl) => {
          const id = nextEl.getAttribute("data-select-inst");
          if (id && !existingIds.has(id)) {
            nextEl.style.animation = "none";
            curShelf.appendChild(nextEl);
          }
        });
        // Reorder only if sequence diverges
        const curOrder = Array.from(curShelf.querySelectorAll(".folder-item"));
        for (let i = 0; i < nextIds.length; i++) {
          const id = nextIds[i];
          const targetEl = curOrder.find((el) => el.getAttribute("data-select-inst") === id);
          const refEl = curShelf.children[i];
          if (targetEl && targetEl !== refEl) {
            curShelf.insertBefore(targetEl, refEl || null);
          }
        }
      });
      if (curPager && nextPager) curPager.replaceWith(nextPager);
      return;
    }

    region.innerHTML = tmp.innerHTML;
    if (opts && opts.slideDir) {
      animateHorizontalPageSlide(region.querySelector(".folder-shelf"), opts.slideDir);
    } else if (opts && opts.animateSlide) {
      animateDampedSlide(region.querySelector(".folder-shelf"), 14);
    }
  }

  function syncGalleryGridUi(state, opts) {
    const gallery = document.getElementById("folder-gallery-page");
    if (!gallery) return;
    const tmp = document.createElement("div");
    tmp.innerHTML = window.LauncherViews.renderInstanceGalleryView(state);
    const nextGrid = tmp.querySelector(".folder-gallery-grid");
    const curGrid = gallery.querySelector(".folder-gallery-grid");
    const nextMeta = tmp.querySelector(".breadcrumb-bar [style*='font-mono']");
    const curMeta = gallery.querySelector(".breadcrumb-bar [style*='font-mono']");
    if (curMeta && nextMeta) {
      curMeta.textContent = nextMeta.textContent;
    }
    if (!curGrid || !nextGrid) return;

    curGrid.classList.add("is-settled");

    if (opts && opts.flip) {
      const sortedIds = window.LauncherViews.getSortedInstances(state).map((i) => i.id);
      const validIdSet = new Set(sortedIds);
      runGridFlip(curGrid, ".gallery-folder-card", (el) => el.getAttribute("data-gallery-pick-inst"), () => {
        const existingMap = new Map();
        Array.from(curGrid.querySelectorAll(".gallery-folder-card")).forEach((el) => {
          const id = el.getAttribute("data-gallery-pick-inst");
          if (!id || !validIdSet.has(id)) {
            el.remove();
          } else {
            el.style.animation = "none";
            el.style.clipPath = "none";
            existingMap.set(id, el);
          }
        });
        sortedIds.forEach((id, idx) => {
          const el = existingMap.get(id);
          if (el && curGrid.children[idx] !== el) {
            curGrid.insertBefore(el, curGrid.children[idx] || null);
          }
        });
      });
      return;
    }

    curGrid.innerHTML = nextGrid.innerHTML;
    curGrid.querySelectorAll(".gallery-folder-card").forEach((card) => {
      card.style.animation = "none";
      card.style.clipPath = "none";
    });
  }

  function syncInstanceColorUi(state, animateColor) {
    const active = window.LauncherViews.getActiveInstance(state);
    const colorVars = window.LauncherViews.resolveFolderColorVars(active);

    document
      .querySelectorAll(`.folder-item[data-select-inst="${active.id}"] .mc-folder, .gallery-folder-card[data-gallery-pick-inst="${active.id}"] .mc-folder`)
      .forEach((mc) => {
        if (colorVars.hasCustom) {
          mc.setAttribute("data-has-color", "true");
          mc.setAttribute("style", colorVars.style);
        } else {
          mc.removeAttribute("data-has-color");
          mc.removeAttribute("style");
        }
        if (animateColor && !prefersReducedMotion()) {
          mc.classList.remove("is-color-changing");
          void mc.offsetWidth;
          mc.classList.add("is-color-changing");
          setTimeout(() => {
            mc.classList.remove("is-color-changing");
          }, 660);
        }
      });

    const activeColorId = active.folderColor || "";
    document.querySelectorAll(".cobra-color[data-set-folder-color]").forEach((btn) => {
      const matched = btn.getAttribute("data-set-folder-color") === activeColorId;
      btn.classList.toggle("is-selected", matched);
      btn.setAttribute("aria-selected", String(matched));
    });
    const customBtn = document.querySelector(".cobra-custom-btn");
    if (customBtn) {
      customBtn.classList.toggle("has-custom-color", activeColorId === "custom");
      if (active.customColorHex) {
        customBtn.setAttribute("data-custom-hex", active.customColorHex);
      }
    }
  }

  let dlBtnWidthTimer = null;
  function syncVersionLoaderCompatibilityUi(detail, selectedBuilds) {
    const isVersionDetail = detail && detail.catKey === "versions";
    const selected = selectedBuilds || {};
    document.querySelectorAll(".vdetail-loader-btn[data-detail-loader]").forEach((btn) => {
      const ld = btn.getAttribute("data-detail-loader");
      btn.classList.toggle("has-selected-build", Boolean(selected[ld]));
      const conflictWith = isVersionDetail ? window.LauncherViews.getLoaderIncompatibility(ld, selected) : null;
      btn.classList.toggle("is-incompatible-loader", Boolean(conflictWith));
      btn.setAttribute("data-conflict-with", conflictWith || "");
      const tip = btn.querySelector(".rare-cow-tooltip");
      if (tip) {
        tip.textContent = conflictWith ? `${ld}与${conflictWith}不兼容！` : "";
      }
    });
    const dlBtn = document.getElementById("btn-download-selected-version");
    const dlTextEl = document.getElementById("vdetail-download-btn-text");
    if (dlBtn && dlTextEl && isVersionDetail) {
      const keys = Object.keys(selected).filter((k) => Boolean(selected[k]));
      const nextLabel = keys.length > 0 ? `下载${detail.id} ${keys.join("+")}` : `下载${detail.id} 原版`;
      if (dlTextEl.textContent !== nextLabel) {
        if (!prefersReducedMotion()) {
          clearTimeout(dlBtnWidthTimer);
          const prevW = dlBtn.getBoundingClientRect().width;
          dlTextEl.textContent = nextLabel;
          dlTextEl.classList.remove("is-text-morphing");
          void dlTextEl.offsetWidth;
          dlTextEl.classList.add("is-text-morphing");
          dlBtn.style.width = "auto";
          const nextW = Math.ceil(dlBtn.getBoundingClientRect().width);
          if (prevW > 0 && Math.abs(nextW - prevW) > 0.5) {
            dlBtn.style.width = `${prevW}px`;
            void dlBtn.offsetWidth;
            dlBtn.style.width = `${nextW}px`;
            dlBtnWidthTimer = setTimeout(() => {
              dlBtn.style.width = "";
            }, 390);
          } else {
            dlBtn.style.width = "";
          }
        } else {
          dlTextEl.textContent = nextLabel;
        }
      }
    }
  }

  /* Update ONLY MC versions list & bg motif when switching Loaders (zero full-page refresh) */
  function syncResourceDetailLoaderUi() {
    const state = window.LauncherBridge.getStateSync();
    const detail = window.LauncherBridge.getResourceDetail(detailState.catKey, detailState.id);
    if (!detail) return;
    const activeLoader = detail.loaders.includes(detailState.activeLoader)
      ? detailState.activeLoader
      : detail.loaders[0];
    const selectedBuilds = detailState.selectedVersionBuilds || {};
    document.querySelectorAll(".vdetail-loader-btn[data-detail-loader]").forEach((btn) => {
      const ld = btn.getAttribute("data-detail-loader");
      const isAct = ld === activeLoader;
      btn.classList.toggle("is-active", isAct);
      btn.setAttribute("aria-selected", String(isAct));
    });
    syncVersionLoaderCompatibilityUi(detail, selectedBuilds);
    const motif = document.getElementById("vdetail-bg-motif");
    if (motif) {
      motif.innerHTML = window.LauncherViews.getMotifSvgForLoader(activeLoader);
    }
    const listEl = document.getElementById("vdetail-mc-list");
    if (listEl) {
      listEl.classList.remove("is-switching");
      listEl.innerHTML = window.LauncherViews.renderDetailMcListHtml(
        detail,
        activeLoader,
        detailState.expandedMcVersion,
        state,
        selectedBuilds
      );
      void listEl.offsetWidth;
      listEl.classList.add("is-switching");
    }
  }

  function syncSubWorkspaceSlot(state, opts) {
    const slot = document.getElementById("hub-workspace-slot");
    if (!slot) {
      render();
      return;
    }
    document.querySelectorAll(".card-3d--hub[data-sub-route]").forEach((c) => {
      c.classList.toggle("is-selected", c.getAttribute("data-sub-route") === activeSubRoute);
    });
    document.querySelectorAll(".hub-integrated-bar .chip-btn[data-sub-route]").forEach((c) => {
      c.classList.toggle("is-active", c.getAttribute("data-sub-route") === activeSubRoute);
    });

    if (!activeSubRoute) {
      slot.innerHTML = "";
      return;
    }

    const animClass = opts && opts.preserveAnimation ? "" : " v-reveal is-inview";
    slot.innerHTML = `<div class="hub-workspace${animClass}" id="hub-workspace">${window.LauncherViews.renderSubWorkspace(activeSubRoute, state, searchQuery, getViewOpts())}</div>`;
    if (opts && opts.preserveAnimation) {
      const ws = document.getElementById("hub-workspace");
      if (ws) ws.style.animation = "none";
      slot.querySelectorAll(".v-row-reveal").forEach((r) => {
        r.style.animation = "none";
        r.classList.add("is-inview");
      });
    } else {
      observeReveals();
    }
  }

  function initMasonryMotion(immediateReveal) {
    const row = document.getElementById("masonry-row");
    if (!row) return;
    const cards = Array.from(row.querySelectorAll(".hub-card-stage"));

    cards.forEach((card, idx) => {
      if (immediateReveal) {
        card.classList.add("is-revealed");
      } else {
        setTimeout(() => {
          card.classList.add("is-revealed");
        }, idx * 55 + 24);
      }

      card.addEventListener("pointerenter", () => {
        cards.forEach((other) => {
          if (other !== card) other.setAttribute("data-dimmed", "true");
        });
      });

      card.addEventListener("pointerleave", () => {
        cards.forEach((other) => other.removeAttribute("data-dimmed"));
      });
    });
  }

  function render(options) {
    const opts = options || {};
    const state = window.LauncherBridge.getStateSync();
    updateTopMeta(state);

    if (currentRoute === "home") {
      const activeHubConfig =
        activeHubKey && PORTAL_CATALOG[activeHubKey]
          ? { key: activeHubKey, ...PORTAL_CATALOG[activeHubKey] }
          : null;
      appRoot.innerHTML = window.LauncherViews.renderHomeView(
        state,
        launchProgress,
        activeQuickPanel,
        activeHubConfig,
        activeSubRoute,
        searchQuery,
        false,
        getViewOpts({
          isReturning: Boolean(opts.isReturning),
          animateStroke: Boolean(opts.animateStroke)
        })
      );
      if (opts.preserveSubLayout) {
        const ws = document.getElementById("hub-workspace");
        if (ws) ws.style.animation = "none";
        appRoot.querySelectorAll(".v-reveal, .v-row-reveal").forEach((r) => {
          r.style.animation = "none";
          r.classList.add("is-inview");
        });
      }
      observeReveals();
      syncBgPatternButtonUi();
      if (activeHubConfig) {
        initMasonryMotion(!opts.animateMasonry);
      }
      clearTimeout(homeReturnTimer);
      if (opts.isReturning) {
        homeReturnTimer = setTimeout(clearHeroReturningClass, 950);
      }
    } else if (currentRoute === "instance-gallery") {
      appRoot.innerHTML = window.LauncherViews.renderInstanceGalleryView(state);
      observeReveals();
      clearTimeout(gallerySettleTimer);
      gallerySettleTimer = setTimeout(() => {
        const g = document.querySelector("#folder-gallery-page .folder-gallery-grid");
        if (g) g.classList.add("is-settled");
      }, 1600);
    } else if (currentRoute === "download-queue") {
      appRoot.innerHTML = window.LauncherViews.renderDownloadQueueView(state);
      observeReveals();
    } else if (currentRoute === "resource-detail") {
      appRoot.innerHTML = window.LauncherViews.renderResourceDetailView(state, detailState);
      observeReveals();
    } else if (currentRoute.startsWith("download-")) {
      appRoot.innerHTML = window.LauncherViews.renderDownloadView(currentRoute, state, searchQuery, getViewOpts());
      observeReveals();
    } else if (currentRoute.startsWith("manage-") || currentRoute === "auth-java") {
      appRoot.innerHTML = window.LauncherViews.renderManageView(currentRoute, state, searchQuery, getViewOpts());
      observeReveals();
    } else if (currentRoute.startsWith("runtime-")) {
      appRoot.innerHTML = window.LauncherViews.renderDiagnosticsView(currentRoute, state);
      observeReveals();
    }
    if (typeof opts.restoreScrollY === "number") {
      const y = opts.restoreScrollY;
      window.scrollTo(0, y);
      scrollCurrentY = y;
      scrollTargetY = y;
    }
    syncFloatingDownloadBallUi(state);
    requestAnimationFrame(() => {
      syncAllGooeyNavs(false);
      syncAllJvmTextareas();
      if (typeof opts.restoreScrollY === "number" && opts.restoreScrollY > 0 && Math.abs(window.scrollY - opts.restoreScrollY) > 2) {
        window.scrollTo(0, opts.restoreScrollY);
        scrollCurrentY = opts.restoreScrollY;
        scrollTargetY = opts.restoreScrollY;
      }
    });
    updateNavControls();
  }

  async function openDownloadQueue() {
    closePortal();
    closeDrawer();
    await window.LauncherBridge.invoke("dismiss_download_ball");
    syncFloatingDownloadBallUi(window.LauncherBridge.getStateSync());
    navHistory.push({
      route: currentRoute,
      hub: activeHubKey,
      sub: activeSubRoute,
      scrollY: window.scrollY
    });
    const hero = document.querySelector(".hero-section");
    const outlet = document.getElementById("hub-outlet");
    const proceed = () => {
      currentRoute = "download-queue";
      activeHubKey = null;
      activeSubRoute = null;
      render();
      window.scrollTo(0, 0);
      scrollCurrentY = 0;
      scrollTargetY = 0;
    };
    if (hero && !prefersReducedMotion()) {
      hero.classList.add("is-sliding-up");
      if (outlet) outlet.classList.add("is-sliding-up");
      setTimeout(proceed, 280);
    } else {
      proceed();
    }
  }

  function leaveDownloadQueue(afterFn) {
    const queueEl = document.getElementById("download-queue-page");
    const runAfter = () => {
      if (queueEl && !prefersReducedMotion()) {
        queueEl.classList.add("is-leaving");
        setTimeout(afterFn, 280);
      } else {
        afterFn();
      }
    };
    if (window.scrollY > 24) {
      dampedScrollTo(0, runAfter);
    } else {
      runAfter();
    }
  }

  async function openResourceDetail(catKey, itemId) {
    const bridge = window.LauncherBridge;
    const cached = bridge.getCachedResourceDetail(catKey, itemId);
    closePortal();
    closeDrawer();
    const detailStateSnapshot =
      currentRoute === "resource-detail" && detailState && detailState.id
        ? {
            catKey: detailState.catKey,
            id: detailState.id,
            activeLoader: detailState.activeLoader,
            loading: Boolean(detailState.loading),
            preview: detailState.loading && detailState.preview ? { ...detailState.preview } : null,
            error: detailState.error || null,
            expandedMcVersion: detailState.expandedMcVersion,
            selectedVersionBuilds: { ...(detailState.selectedVersionBuilds || {}) }
          }
        : null;
    navHistory.push({
      route: currentRoute,
      hub: activeHubKey,
      sub: activeSubRoute,
      scrollY: window.scrollY,
      searchQuery,
      currentPage,
      loaderFilter,
      sourceFilter,
      sortFilter,
      categoryFilter,
      subCategoryFilter,
      mcVersionFilter,
      filterOpen,
      activeQuickPanel,
      detailStateSnapshot
    });
    stopDampedScroll();
    currentRoute = "resource-detail";
    const activeDetail = {
      catKey,
      id: itemId,
      activeLoader: cached?.loaders[0] || null,
      loading: !cached,
      preview: cached ? null : bridge.getResourcePreview(catKey, itemId),
      expandedMcVersion: null,
      selectedVersionBuilds: {}
    };
    detailState = activeDetail;
    render({ restoreScrollY: 0 });
    if (cached) return;

    let detail;
    try {
      detail = await bridge.fetchLiveResourceDetail(catKey, itemId);
    } catch (error) {
      activeDetail.error = error.message || "无法加载资源详情。";
    }
    if (detailState !== activeDetail || currentRoute !== "resource-detail") return;
    activeDetail.loading = false;
    if (detail) {
      activeDetail.preview = null;
      activeDetail.activeLoader = detail.loaders[0] || null;
    } else {
      activeDetail.error ||= "暂时无法读取可下载版本，可以返回列表后重试。";
    }

    const target = document.getElementById("resource-detail-page");
    if (!target || target.classList.contains("is-leaving")) return;
    const template = document.createElement("template");
    template.innerHTML = window.LauncherViews.renderResourceDetailView(bridge.getStateSync(), activeDetail);
    const incoming = template.content.querySelector("#resource-detail-page");
    if (incoming) {
      target.setAttribute("aria-label", incoming.getAttribute("aria-label"));
      target.replaceChildren(...incoming.childNodes);
    }
    const motif = document.getElementById("vdetail-bg-motif");
    if (motif) motif.innerHTML = window.LauncherViews.getMotifSvgForLoader(activeDetail.activeLoader);
  }

  function leaveResourceDetail(afterFn) {
    const detailEl = document.getElementById("resource-detail-page");
    if (detailEl && !prefersReducedMotion()) {
      detailEl.classList.add("is-leaving");
      setTimeout(afterFn, 260);
    } else {
      afterFn();
    }
  }

  function openFolderGallery() {
    closePortal();
    closeDrawer();
    navHistory.push({ route: currentRoute, hub: activeHubKey, sub: activeSubRoute });
    const hero = document.querySelector(".hero-section");
    const proceed = () => {
      currentRoute = "instance-gallery";
      activeHubKey = null;
      activeSubRoute = null;
      render();
      window.scrollTo(0, 0);
      scrollCurrentY = 0;
      scrollTargetY = 0;
    };
    if (hero && !prefersReducedMotion()) {
      hero.classList.add("is-extracting");
      setTimeout(proceed, 320);
    } else {
      proceed();
    }
  }

  function leaveFolderGallery(afterFn) {
    const galleryEl = document.getElementById("folder-gallery-page");
    const runAfter = () => {
      if (galleryEl && !prefersReducedMotion()) {
        galleryEl.classList.add("is-leaving");
        setTimeout(afterFn, 340);
      } else {
        afterFn();
      }
    };
    if (window.scrollY > 24) {
      dampedScrollTo(0, runAfter);
    } else {
      runAfter();
    }
  }

  function alignToSearchViewTop({ animate = false } = {}) {
    const target =
      document.getElementById("resource-search-bar") ||
      document.getElementById("hub-workspace-slot") ||
      document.getElementById("hub-workspace");
    if (target) {
      scrollToElementTop(target, 16, { immediate: usesNativeListScrolling() && !animate });
    }
  }

  function navigateTo(route, skipHistory) {
    const matchedHub = findHubForSubRoute(route);
    if (matchedHub && route !== "home") {
      openSubWorkspace(route, matchedHub);
      return;
    }
    if (!skipHistory && (currentRoute !== route || activeHubKey !== null || activeSubRoute !== null)) {
      navHistory.push({ route: currentRoute, hub: activeHubKey, sub: activeSubRoute });
    }
    currentRoute = route;
    resetListControls();
    if (route === "home") {
      activeHubKey = null;
      activeSubRoute = null;
    }
    closePortal();
    closeDrawer();
    render();
    dampedScrollTo(0);
  }

  function openSubWorkspace(subRoute, hubOverride) {
    const targetHub = hubOverride || findHubForSubRoute(subRoute) || activeHubKey;
    closePortal();
    closeDrawer();
    const isSameHub = currentRoute === "home" && activeHubKey === targetHub && Boolean(document.getElementById("hub-workspace-slot"));
    if (isSameHub && activeSubRoute === subRoute) {
      alignToSearchViewTop({ animate: subRoute.startsWith("download-") });
      return;
    }

    navHistory.push({ route: currentRoute, hub: activeHubKey, sub: activeSubRoute });
    const hasHubOutlet = currentRoute === "home" && Boolean(document.getElementById("hub-outlet"));
    currentRoute = "home";
    activeHubKey = targetHub;
    activeSubRoute = subRoute;
    resetListControls();
    animateEmblem = false;

    if (isSameHub) {
      syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: false });
      updateNavControls();
    } else if (hasHubOutlet) {
      syncHubOutletUi({ animateMasonry: true });
    } else {
      render({ animateMasonry: true });
    }

    requestAnimationFrame(() => {
      if (activeSubRoute === subRoute && currentRoute === "home") {
        alignToSearchViewTop({ animate: subRoute.startsWith("download-") });
      }
    });
    loadOnlineDownloadPage(subRoute);
  }

  function openPortal(portalKey) {
    const cfg = PORTAL_CATALOG[portalKey];
    if (!cfg) return;
    closeDrawer();
    const targetSub = cfg.directRoute || null;

    if (currentRoute === "home" && activeHubKey === portalKey && activeSubRoute === targetSub) {
      const hubEl = document.getElementById("hub-section");
      scrollToElementTop(hubEl, 8);
      return;
    }

    navHistory.push({ route: currentRoute, hub: activeHubKey, sub: activeSubRoute });
    const hasHubOutlet = currentRoute === "home" && Boolean(document.getElementById("hub-outlet"));
    currentRoute = "home";
    activeHubKey = portalKey;
    activeSubRoute = targetSub;
    resetListControls();
    animateEmblem = false;

    if (hasHubOutlet) {
      syncHubOutletUi({ animateMasonry: !targetSub });
    } else {
      render({ animateMasonry: !targetSub });
    }

    requestAnimationFrame(() => {
      const hubEl = document.getElementById("hub-section");
      scrollToElementTop(hubEl, 8);
    });
    if (targetSub) loadOnlineDownloadPage(targetSub);
  }

  function animateDampedExit(elId, cb) {
    const el = document.getElementById(elId);
    if (el && !prefersReducedMotion()) {
      el.classList.add("is-collapsing");
      setTimeout(cb, 260);
    } else {
      cb();
    }
  }

  function collapseToTopWithDamping(onSettled) {
    const runCollapse = () => animateDampedExit("hub-section", onSettled);
    if (window.scrollY > 24) {
      dampedScrollTo(0, runCollapse);
    } else {
      runCollapse();
    }
  }

  function goBack() {
    closeDrawer();
    closePortal();
    if (currentRoute === "resource-detail") {
      const prev = navHistory.length > 0 ? navHistory.pop() : { route: "home", hub: "download", sub: "download-mods", scrollY: 0 };
      leaveResourceDetail(() => {
        currentRoute = prev.route || "home";
        activeHubKey = prev.hub || null;
        activeSubRoute = prev.sub || null;
        if (typeof prev.searchQuery === "string") searchQuery = prev.searchQuery;
        if (typeof prev.currentPage === "number") currentPage = prev.currentPage;
        if (typeof prev.loaderFilter === "string") loaderFilter = prev.loaderFilter;
        if (typeof prev.sourceFilter === "string") sourceFilter = prev.sourceFilter;
        if (typeof prev.sortFilter === "string") sortFilter = prev.sortFilter;
        if (typeof prev.categoryFilter === "string") categoryFilter = prev.categoryFilter;
        if (typeof prev.subCategoryFilter === "string") subCategoryFilter = prev.subCategoryFilter;
        if (typeof prev.mcVersionFilter === "string") mcVersionFilter = prev.mcVersionFilter;
        if (typeof prev.filterOpen === "boolean") filterOpen = prev.filterOpen;
        if ("activeQuickPanel" in prev) activeQuickPanel = prev.activeQuickPanel;
        if (prev.detailStateSnapshot) {
          detailState = {
            ...prev.detailStateSnapshot,
            selectedVersionBuilds: { ...(prev.detailStateSnapshot.selectedVersionBuilds || {}) }
          };
        }
        const targetScrollY = typeof prev.scrollY === "number" ? prev.scrollY : 0;
        render({
          animateMasonry: false,
          preserveSubLayout: true,
          isReturning: false,
          restoreScrollY: targetScrollY
        });
        if (currentRoute === "resource-detail" && detailState && detailState.id && !window.LauncherBridge.getCachedResourceDetail(detailState.catKey, detailState.id)) {
          window.LauncherBridge.fetchLiveResourceDetail(detailState.catKey, detailState.id).then((fetched) => {
            if (!fetched || currentRoute !== "resource-detail" || detailState.id !== fetched.id) return;
            render({ preserveSubLayout: true, restoreScrollY: targetScrollY });
          }).catch(() => {});
        }
        const ws = document.getElementById("hub-workspace");
        if (ws && !prefersReducedMotion()) {
          ws.classList.add("is-entering-back");
        }
      });
      return;
    }
    if (currentRoute === "download-queue") {
      const prev = navHistory.length > 0 ? navHistory.pop() : { route: "home", hub: null, sub: null };
      leaveDownloadQueue(() => {
        currentRoute = prev.route || "home";
        activeHubKey = prev.hub || null;
        activeSubRoute = prev.sub || null;
        render({
          animateMasonry: Boolean(prev.hub) && !prev.sub,
          isReturning: currentRoute === "home" && !prev.sub,
          restoreScrollY: typeof prev.scrollY === "number" ? prev.scrollY : 0
        });
      });
      return;
    }
    if (currentRoute === "instance-gallery") {
      const prev = navHistory.length > 0 ? navHistory.pop() : { route: "home", hub: null, sub: null };
      leaveFolderGallery(() => {
        currentRoute = prev.route || "home";
        activeHubKey = prev.hub || null;
        activeSubRoute = prev.sub || null;
        resetListControls();
        render({
          animateMasonry: Boolean(prev.hub) && !prev.sub,
          isReturning: currentRoute === "home"
        });
      });
      return;
    }
    if (navHistory.length > 0) {
      const prev = navHistory.pop();
      const returningToUpperHome = prev.route === "home" && !prev.hub && currentRoute === "home" && (activeHubKey || activeSubRoute);
      if (returningToUpperHome) {
        collapseToTopWithDamping(() => {
          currentRoute = "home";
          activeHubKey = null;
          activeSubRoute = null;
          resetListControls();
          syncHubOutletUi();
        });
        return;
      }

      const returningToHubFromSub =
        prev.route === "home" &&
        prev.hub &&
        !prev.sub &&
        currentRoute === "home" &&
        prev.hub === activeHubKey &&
        activeSubRoute;

      if (returningToHubFromSub) {
        const hubEl = document.getElementById("hub-section");
        const topbarH = getStickyTopbarHeight();
        const hubTargetY = hubEl ? Math.max(0, hubEl.getBoundingClientRect().top + window.scrollY - topbarH - 8) : 0;
        const collapseSub = () =>
          animateDampedExit("hub-workspace", () => {
            activeSubRoute = null;
            resetListControls();
            syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: true });
            updateNavControls();
          });
        if (Math.abs(window.scrollY - hubTargetY) > 12) {
          dampedScrollTo(hubTargetY, collapseSub);
        } else {
          collapseSub();
        }
        return;
      }

      const sameHomeHub =
        prev.route === "home" &&
        currentRoute === "home" &&
        prev.hub === activeHubKey &&
        Boolean(prev.hub) &&
        Boolean(document.getElementById("hub-workspace-slot"));

      const wasNotHome = currentRoute !== "home";
      currentRoute = prev.route;
      activeHubKey = prev.hub;
      activeSubRoute = prev.sub || null;
      resetListControls();

      if (sameHomeHub) {
        syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: false });
        updateNavControls();
      } else {
        render({
          animateMasonry: Boolean(prev.hub) && !prev.sub,
          isReturning: wasNotHome && currentRoute === "home"
        });
      }

      requestAnimationFrame(() => {
        if (prev.sub) {
          alignToSearchViewTop();
        } else if (prev.hub) {
          scrollToElementTop(document.getElementById("hub-section"), 8);
        } else {
          dampedScrollTo(0);
        }
      });
      return;
    }
    if (activeSubRoute !== null) {
      const hubEl = document.getElementById("hub-section");
      const topbarH = getStickyTopbarHeight();
      const hubTargetY = hubEl ? Math.max(0, hubEl.getBoundingClientRect().top + window.scrollY - topbarH - 8) : 0;
      const collapseSub = () =>
        animateDampedExit("hub-workspace", () => {
          activeSubRoute = null;
          resetListControls();
          syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: true });
          updateNavControls();
        });
      if (Math.abs(window.scrollY - hubTargetY) > 12) {
        dampedScrollTo(hubTargetY, collapseSub);
      } else {
        collapseSub();
      }
      return;
    }
    if (activeHubKey !== null) {
      collapseToTopWithDamping(() => {
        activeHubKey = null;
        resetListControls();
        syncHubOutletUi();
      });
      return;
    }
    if (currentRoute !== "home") {
      navigateTo("home", true);
    }
  }

  function goHome() {
    navHistory = [];
    closePortal();
    closeDrawer();
    if (currentRoute === "resource-detail") {
      leaveResourceDetail(() => {
        activeHubKey = null;
        activeSubRoute = null;
        currentRoute = "home";
        resetListControls();
        render({ isReturning: true, restoreScrollY: 0 });
      });
      return;
    }
    if (currentRoute === "download-queue") {
      leaveDownloadQueue(() => {
        activeHubKey = null;
        activeSubRoute = null;
        currentRoute = "home";
        resetListControls();
        render({ isReturning: true, restoreScrollY: 0 });
      });
      return;
    }
    if (currentRoute === "instance-gallery") {
      leaveFolderGallery(() => {
        activeHubKey = null;
        activeSubRoute = null;
        currentRoute = "home";
        resetListControls();
        render({ isReturning: true });
      });
      return;
    }
    const shouldCollapseAfterScroll = currentRoute === "home" && (activeHubKey !== null || activeSubRoute !== null);
    if (shouldCollapseAfterScroll) {
      collapseToTopWithDamping(() => {
        activeHubKey = null;
        activeSubRoute = null;
        currentRoute = "home";
        resetListControls();
        syncHubOutletUi();
      });
      return;
    }
    const wasNotHome = currentRoute !== "home";
    activeHubKey = null;
    activeSubRoute = null;
    currentRoute = "home";
    resetListControls();
    render({ isReturning: wasNotHome });
    dampedScrollTo(0);
  }

  function closePortal() {
    portalBackdrop.classList.remove("is-open");
    portalBackdrop.setAttribute("aria-hidden", "true");
  }

  function openDrawer() {
    drawerOverlay.classList.add("is-open");
    drawerOverlay.setAttribute("aria-hidden", "false");
  }

  function closeDrawer() {
    drawerOverlay.classList.remove("is-open");
    drawerOverlay.setAttribute("aria-hidden", "true");
  }

  function cancelLaunchSimulation(instanceId) {
    const timer = pendingSimTimers.get(instanceId);
    if (timer) {
      clearInterval(timer);
      pendingSimTimers.delete(instanceId);
    }
  }

  function runLaunchSimulation(instanceId) {
    const targetId = instanceId || window.LauncherBridge.getStateSync()?.activeInstanceId || "";
    cancelLaunchSimulation(targetId);
    if (launchResetTimer) clearTimeout(launchResetTimer);
    if (targetId) pendingLaunchIds.add(targetId);
    syncLaunchButtonUi(window.LauncherBridge.getStateSync());
    launchProgress = 4;
    syncLaunchMeterUi();
    let progress = 4;
    const timer = setInterval(() => {
      progress = Math.min(100, progress + 10);
      const st = window.LauncherBridge.getStateSync();
      if (progress >= 100 && targetId && pendingLaunchIds.has(targetId)) {
        // Simulate Minecraft Java GUI window awakening -> restore solid button
        pendingLaunchIds.delete(targetId);
        syncLaunchButtonUi(st);
      }
      if (st?.activeInstanceId === targetId) {
        launchProgress = progress;
        if (currentRoute === "home") syncLaunchMeterUi();
      }
      if (progress >= 100) {
        cancelLaunchSimulation(targetId);
        showToast("已启动当前实例");
        launchResetTimer = setTimeout(() => {
          launchProgress = 0;
          if (currentRoute === "home") syncLaunchMeterUi();
        }, 1200);
      }
    }, 300);
    if (targetId) pendingSimTimers.set(targetId, timer);
  }

  let accountCloseTimer = null;
  function closeAccountPopoverSmoothly() {
    if (!accountMenuOpen) return;
    accountMenuOpen = false;
    offlineAddOpen = false;
    const pop = document.getElementById("account-popover");
    const badge = document.getElementById("btn-toggle-account-menu");
    if (badge) badge.setAttribute("aria-expanded", "false");
    if (pop && !prefersReducedMotion()) {
      clearTimeout(accountCloseTimer);
      pop.classList.add("is-closing");
      pop.classList.remove("is-open");
      accountCloseTimer = setTimeout(() => {
        syncPlayerAccountUi(window.LauncherBridge.getStateSync());
      }, 240);
    } else {
      syncPlayerAccountUi(window.LauncherBridge.getStateSync());
    }
  }

  /* ---------- Global Event Delegation ---------- */
  document.addEventListener("pointerover", (e) => {
    if (e.target.closest("#mcver-starfish-wrap")) ensureMcVersionTrayPopulated();
  });
  document.addEventListener("focusin", (e) => {
    if (e.target.closest("#mcver-starfish-wrap")) ensureMcVersionTrayPopulated();
  });
  document.addEventListener("input", (e) => {
    if (e.target && e.target.id === "mcver-search-input") {
      e.target.classList.toggle("has-value", e.target.value.length > 0);
      document.getElementById("mcver-starfish-wrap")?.classList.add("is-open");
      ensureMcVersionTrayPopulated(e.target.value);
    }
  });
  document.addEventListener("keydown", (e) => {
    if (e.target && e.target.id === "mcver-search-input") {
      if (e.key === "Enter") {
        e.preventDefault();
        const raw = e.target.value.trim();
        const next = raw.length > 0 ? raw : "all";
        e.target.blur();
        if (next !== mcVersionFilter) {
          mcVersionFilter = next;
          applyDownloadFilters();
        } else {
          document.getElementById("mcver-starfish-wrap")?.classList.remove("is-open");
          document.getElementById("mcver-starfish-trigger")?.setAttribute("aria-expanded", "false");
        }
      } else if (e.key === "Escape") {
        e.preventDefault();
        e.target.blur();
        document.getElementById("mcver-starfish-wrap")?.classList.remove("is-open");
        document.getElementById("mcver-starfish-trigger")?.setAttribute("aria-expanded", "false");
      }
    }
  });
  document.addEventListener("click", async (e) => {
    if (accountMenuOpen && !e.target.closest("#player-account-wrap")) {
      closeAccountPopoverSmoothly();
    }

    const catWrapEl = document.getElementById("cat-starfish-wrap");
    if (catWrapEl && !e.target.closest("#cat-starfish-wrap")) {
      catWrapEl.classList.remove("is-open");
      document.getElementById("cat-starfish-trigger")?.setAttribute("aria-expanded", "false");
    }

    const mcWrapEl = document.getElementById("mcver-starfish-wrap");
    if (mcWrapEl && !e.target.closest("#mcver-starfish-wrap")) {
      mcWrapEl.classList.remove("is-open");
      document.getElementById("mcver-starfish-trigger")?.setAttribute("aria-expanded", "false");
    }

    if (e.target.closest("#cat-starfish-trigger")) {
      if (catWrapEl) {
        mcWrapEl?.classList.remove("is-open");
        const willOpen = !catWrapEl.classList.contains("is-open");
        catWrapEl.classList.toggle("is-open", willOpen);
        document.getElementById("cat-starfish-trigger")?.setAttribute("aria-expanded", String(willOpen));
      }
      return;
    }

    if (e.target.closest("#mcver-starfish-trigger")) {
      if (mcWrapEl) {
        catWrapEl?.classList.remove("is-open");
        ensureMcVersionTrayPopulated();
        const willOpen = !mcWrapEl.classList.contains("is-open");
        mcWrapEl.classList.toggle("is-open", willOpen);
        document.getElementById("mcver-starfish-trigger")?.setAttribute("aria-expanded", String(willOpen));
        if (willOpen) {
          requestAnimationFrame(() => document.getElementById("mcver-search-input")?.focus());
        }
      }
      return;
    }

    if (!e.target.closest("[data-delete-mod], [data-delete-dl], [data-folder-del], [data-delete-account]")) {
      document.querySelectorAll(".emu-delete-btn.is-armed").forEach((b) => b.classList.remove("is-armed"));
    }

    if (e.target.closest("#btn-toggle-account-menu")) {
      clearTimeout(accountCloseTimer);
      if (accountMenuOpen) {
        closeAccountPopoverSmoothly();
      } else {
        accountMenuOpen = true;
        offlineAddOpen = false;
        const existingPop = document.getElementById("account-popover");
        const existingBadge = document.getElementById("btn-toggle-account-menu");
        if (existingPop && !prefersReducedMotion()) {
          existingPop.classList.remove("is-closing");
          document.getElementById("account-offline-drawer")?.classList.remove("is-open");
          document.getElementById("btn-show-offline-add")?.classList.remove("is-active");
          if (existingBadge) existingBadge.setAttribute("aria-expanded", "true");
          existingPop.classList.remove("is-open");
          void existingPop.offsetWidth;
          existingPop.classList.add("is-open");
        } else {
          syncPlayerAccountUi(window.LauncherBridge.getStateSync());
        }
      }
      return;
    }

    const switchAccBtn = e.target.closest("[data-switch-account]");
    if (switchAccBtn) {
      const accId = switchAccBtn.getAttribute("data-switch-account");
      const acc = await window.LauncherBridge.invoke("switch_account", { id: accId });
      if (acc) {
        const badgeImg = document.querySelector("#btn-toggle-account-menu .player-head-img");
        const badgeName = document.querySelector("#btn-toggle-account-menu .player-head-name");
        if (badgeImg) {
          badgeImg.src = acc.avatar || "assets/mc-icons/player-head.png";
          badgeImg.alt = acc.username;
        }
        if (badgeName) badgeName.textContent = acc.username;
        document.querySelectorAll("#account-popover .account-row").forEach((r) => {
          r.classList.toggle("is-current", r.getAttribute("data-acc-row") === acc.id);
        });
        showToast(`已切换账号：${acc.username}`);
      }
      closeAccountPopoverSmoothly();
      return;
    }

    const delAccBtn = e.target.closest("[data-delete-account]");
    if (delAccBtn) {
      e.stopPropagation();
      if (!delAccBtn.classList.contains("is-armed")) {
        document.querySelectorAll(".emu-delete-btn.is-armed").forEach((b) => b.classList.remove("is-armed"));
        delAccBtn.classList.add("is-armed");
        return;
      }
      const accId = delAccBtn.getAttribute("data-delete-account");
      const row = delAccBtn.closest(".account-row");
      const finishAccDelete = async () => {
        const removed = await window.LauncherBridge.invoke("delete_account", { id: accId });
        const st = window.LauncherBridge.getStateSync();
        syncPlayerAccountUi(st);
        if (removed) showToast(`已删除账号：${removed.username}`);
      };
      if (row && !prefersReducedMotion()) {
        row.style.height = `${row.getBoundingClientRect().height}px`;
        void row.offsetHeight;
        row.classList.add("is-row-removing");
        setTimeout(finishAccDelete, 210);
      } else {
        await finishAccDelete();
      }
      return;
    }

    if (e.target.closest("#btn-add-ms-account")) {
      accountMenuOpen = false;
      offlineAddOpen = false;
      openSubWorkspace("auth-java", "manage");
      showToast("正在跳转至微软登录界面");
      return;
    }

    if (e.target.closest("#btn-show-offline-add")) {
      offlineAddOpen = !offlineAddOpen;
      const pop = document.getElementById("account-popover");
      const drawer = document.getElementById("account-offline-drawer");
      const btn = document.getElementById("btn-show-offline-add");
      if (pop && drawer && btn) {
        drawer.classList.toggle("is-open", offlineAddOpen);
        btn.classList.toggle("is-active", offlineAddOpen);
        if (offlineAddOpen) {
          const inp = document.getElementById("account-offline-name");
          if (inp) setTimeout(() => inp.focus(), 60);
        }
      } else {
        syncPlayerAccountUi(window.LauncherBridge.getStateSync());
      }
      return;
    }

    if (e.target.closest("#btn-confirm-offline-account")) {
      const input = document.getElementById("account-offline-name");
      const name = input ? input.value.trim() : "";
      const acc = await window.LauncherBridge.invoke("add_account", {
        username: name,
        type: "离线角色",
        avatar: "assets/mc-icons/creeper.png"
      });
      offlineAddOpen = false;
      const st = window.LauncherBridge.getStateSync();
      syncPlayerAccountUi(st);
      if (acc) showToast(`已添加离线角色：${acc.username}`);
      return;
    }

    if (e.target.closest("#floating-dl-ball")) {
      await openDownloadQueue();
      return;
    }

    if (e.target.closest("#btn-download-selected-version")) {
      const st = window.LauncherBridge.getStateSync();
      const detail = window.LauncherBridge.getResourceDetail(detailState.catKey, detailState.id);
      if (!detail) return;
      const selected = detailState.selectedVersionBuilds || {};
      const selectedKeys = Object.keys(selected).filter((k) => Boolean(selected[k]));
      const suffix = selectedKeys.length > 0 ? ` + ${selectedKeys.join(" + ")}` : " (原版)";
      const taskName = `Minecraft ${detail.id}${suffix}`;
      let fabricVersion = "";
      if (selected.Fabric && detail.loaderTrees && detail.loaderTrees.Fabric) {
        const trees = detail.loaderTrees.Fabric || [];
        for (const grp of trees) {
          const b = (grp.builds || []).find((x) => String(x.id) === String(selected.Fabric));
          if (b) {
            fabricVersion = String(b.versionNumber || b.title || "").replace(/^Fabric\s+/i, "").split(/\s+/)[0];
            break;
          }
        }
      }
      try {
        await window.LauncherBridge.invoke("add_download_task", {
          name: taskName,
          sub: `${detail.id}.jar${selectedKeys.length ? ` · ${selectedKeys.join("/")}` : ""}`,
          icon: "assets/mc-icons/grass.png",
          size: selectedKeys.length > 0 ? "32.8 MB" : "26.4 MB",
          installType: "version",
          mcVersion: detail.id,
          fabricVersion,
          loaders: selectedKeys
        });
      } catch (error) {
        showToast(error.message);
        return;
      }
      syncFloatingDownloadBallUi(window.LauncherBridge.getStateSync());
      showToast(`已添加到下载任务：${taskName}`);
      goBack();
      return;
    }

    const favPageBtn = e.target.closest("[data-fav-page]");
    if (favPageBtn && !favPageBtn.disabled) {
      const nextFav = Number(favPageBtn.getAttribute("data-fav-page")) || 1;
      const slideDir = nextFav >= favPage ? 1 : -1;
      favPage = nextFav;
      syncFolderShelfUi(window.LauncherBridge.getStateSync(), { slideDir });
      return;
    }

    const moreVersions = e.target.closest("[data-more-version-category]");
    if (moreVersions) {
      const state = window.LauncherBridge.getStateSync();
      const category = moreVersions.dataset.moreVersionCategory;
      const versions = window.LauncherViews.filterGameVersions(state, searchQuery, loaderFilter)
        .filter(version => version.category === category);
      const offset = Number(moreVersions.dataset.versionOffset);
      moreVersions.insertAdjacentHTML("beforebegin", window.LauncherViews.renderGameVersionGroupPage(versions, state, category, offset));
      const newRow = moreVersions.previousElementSibling;
      moreVersions.remove();
      (newRow?.matches("button") ? newRow : newRow?.querySelector("button"))?.focus({ preventScroll: true });
      observeReveals();
      return;
    }

    const pclCatBtn = e.target.closest("[data-toggle-pcl-cat]");
    if (pclCatBtn) {
      const category = pclCatBtn.dataset.togglePclCat;
      const group = pclCatBtn.closest(".pcl-category-group");
      const drawer = group.querySelector(".pcl-category-drawer");
      const list = group.querySelector(".pcl-category-list");
      const opens = !group.classList.contains("is-open");
      openPclCats[category] = opens;
      if (opens && !list.children.length) {
        const state = window.LauncherBridge.getStateSync();
        const versions = window.LauncherViews.filterGameVersions(state, searchQuery, loaderFilter)
          .filter(version => version.category === category);
        list.innerHTML = window.LauncherViews.renderGameVersionGroupPage(versions, state, category);
        observeReveals();
      }
      drawer.inert = !opens;
      group.classList.toggle("is-open", opens);
      pclCatBtn.setAttribute("aria-expanded", String(opens));
      return;
    }

    const checkVerBuildBtn = e.target.closest("[data-check-version-build]");
    if (checkVerBuildBtn) {
      e.stopPropagation();
      const raw = checkVerBuildBtn.getAttribute("data-check-version-build") || "";
      const splitIdx = raw.indexOf(":");
      if (splitIdx <= 0) return;
      const loader = raw.slice(0, splitIdx);
      const buildId = raw.slice(splitIdx + 1);
      const selected = detailState.selectedVersionBuilds || (detailState.selectedVersionBuilds = {});
      const exclusiveModLoaders = ["Forge", "NeoForge", "Fabric"];
      const wasChecked = selected[loader] === buildId;
      if (wasChecked) {
        delete selected[loader];
        showToast(`已取消选择 ${loader}`);
      } else {
        if (exclusiveModLoaders.includes(loader)) {
          exclusiveModLoaders.forEach((m) => {
            if (m !== loader) delete selected[m];
          });
        }
        if (loader === "NeoForge" || loader === "Fabric") {
          delete selected.OptiFine;
        } else if (loader === "OptiFine") {
          delete selected.NeoForge;
          delete selected.Fabric;
        }
        selected[loader] = buildId;
        const incompatibleTip = loader === "Forge" ? "（可继续兼容勾选 OptiFine）" : "";
        showToast(`已勾选 ${loader}${incompatibleTip}`);
      }
      const selectedIdForActive = selected[loader] || null;
      const detail = window.LauncherBridge.getResourceDetail(detailState.catKey, detailState.id);
      syncVersionLoaderCompatibilityUi(detail, selected);
      document.querySelectorAll(`.data-row--build[data-build-loader="${loader}"]`).forEach((row) => {
        const rId = row.getAttribute("data-build-id");
        const isRowChecked = selectedIdForActive === rId;
        const isRowDimmed = Boolean(selectedIdForActive) && !isRowChecked;
        row.classList.toggle("is-checked-build", isRowChecked);
        row.classList.toggle("is-dimmed-build", isRowDimmed);
        const btn = row.querySelector(".wasp-check-btn[data-check-version-build]");
        if (btn) {
          btn.classList.toggle("is-checked", isRowChecked);
          btn.setAttribute("aria-pressed", String(isRowChecked));
        }
      });
      return;
    }

    const folderOpenBtn = e.target.closest("[data-folder-open]");
    if (folderOpenBtn) {
      e.stopPropagation();
      const id = folderOpenBtn.getAttribute("data-folder-open");
      const res = await window.LauncherBridge.invoke("open_folder", { id, subpath: "" });
      showToast(`已打开版本目录：${res.path}`);
      return;
    }

    const folderFavBtn = e.target.closest("[data-folder-fav]");
    if (folderFavBtn) {
      e.stopPropagation();
      const id = folderFavBtn.getAttribute("data-folder-fav");
      const updated = await window.LauncherBridge.invoke("toggle_favorite_instance", { id });
      if (updated) {
        showToast(updated.favorite ? `已收藏版本：${updated.name}` : `已取消收藏：${updated.name}`);
        const st = window.LauncherBridge.getStateSync();
        if (currentRoute === "instance-gallery") {
          const cardEl = folderFavBtn.closest(".gallery-folder-card");
          applyCardFavoriteBadge(cardEl, id, updated.favorite, updated.favorite);
          syncGalleryGridUi(st, { flip: true });
        } else {
          const homeCard = folderFavBtn.closest(".folder-item");
          applyCardFavoriteBadge(homeCard, id, updated.favorite, updated.favorite);
          if (!updated.favorite && homeCard && !prefersReducedMotion()) {
            homeCard.classList.add("is-folder-removing");
            setTimeout(() => {
              syncFolderShelfUi(window.LauncherBridge.getStateSync(), { flip: true });
            }, 250);
          } else {
            syncFolderShelfUi(st, { flip: true });
          }
        }
        if (activeSubRoute === "manage-instances") syncSubWorkspaceSlot(st, { preserveAnimation: true });
      }
      return;
    }

    const folderSettingsBtn = e.target.closest("[data-folder-settings]");
    if (folderSettingsBtn) {
      e.stopPropagation();
      const id = folderSettingsBtn.getAttribute("data-folder-settings");
      const prevActiveId = window.LauncherBridge.getStateSync().activeInstanceId;
      if (id && id !== prevActiveId) {
        await window.LauncherBridge.invoke("set_active_instance", { id });
      }
      const st = window.LauncherBridge.getStateSync();
      if (currentRoute === "instance-gallery") {
        leaveFolderGallery(() => {
          navHistory = [];
          currentRoute = "home";
          activeHubKey = null;
          activeSubRoute = null;
          activeQuickPanel = "settings";
          resetListControls();
          render({ isReturning: true, animateStroke: id !== prevActiveId });
        });
        return;
      }
      activeQuickPanel = "settings";
      syncInstanceUi(st, true, id !== prevActiveId);
      syncQuickPanelUi(st);
      return;
    }

    const folderDelBtn = e.target.closest("[data-folder-del]");
    if (folderDelBtn) {
      e.stopPropagation();
      const id = folderDelBtn.getAttribute("data-folder-del");
      if (!folderDelBtn.classList.contains("is-armed")) {
        document.querySelectorAll(".emu-delete-btn.is-armed").forEach((b) => b.classList.remove("is-armed"));
        folderDelBtn.classList.add("is-armed");
        return;
      }
      const prevActiveId = window.LauncherBridge.getStateSync().activeInstanceId;
      const removed = await window.LauncherBridge.invoke("delete_instance", { id });
      if (!removed) {
        folderDelBtn.classList.remove("is-armed");
        showToast("至少需保留一个版本实例");
        return;
      }
      showToast(`已删除版本：${removed.name}`);
      const st = window.LauncherBridge.getStateSync();
      const activeChanged = prevActiveId !== st.activeInstanceId;
      const hostCard = folderDelBtn.closest(".folder-item, .gallery-folder-card");
      const finishDeleteSync = () => {
        const latestSt = window.LauncherBridge.getStateSync();
        syncFolderShelfUi(latestSt, { flip: true });
        if (activeChanged) {
          syncInstanceUi(latestSt, false, true);
        }
        if (currentRoute === "instance-gallery") syncGalleryGridUi(latestSt, { flip: true });
        if (activeSubRoute === "manage-instances") syncSubWorkspaceSlot(latestSt, { preserveAnimation: true });
      };
      if (hostCard && !prefersReducedMotion()) {
        hostCard.classList.add("is-folder-removing");
        setTimeout(() => {
          finishDeleteSync();
        }, 250);
      } else {
        finishDeleteSync();
      }
      return;
    }

    if (e.target.closest("#btn-open-download-queue")) {
      openDownloadQueue();
      return;
    }

    const delDlBtn = e.target.closest("[data-delete-dl]");
    if (delDlBtn) {
      const dlId = delDlBtn.getAttribute("data-delete-dl");
      if (!delDlBtn.classList.contains("is-armed")) {
        document.querySelectorAll(".emu-delete-btn.is-armed").forEach((b) => b.classList.remove("is-armed"));
        delDlBtn.classList.add("is-armed");
        return;
      }
      const removed = await window.LauncherBridge.invoke("delete_download_task", { id: dlId });
      if (removed) {
        showToast(`已移除下载任务：${removed.name}`);
        const st = window.LauncherBridge.getStateSync();
        const row = delDlBtn.closest("[data-dl-row]");
        if (row && !prefersReducedMotion()) {
          row.style.height = `${row.getBoundingClientRect().height}px`;
          void row.offsetHeight;
          row.classList.add("is-row-removing");
          setTimeout(() => {
            row.remove();
            syncDownloadQueueEyebrow(st);
          }, 270);
        } else {
          if (row) row.remove();
          syncDownloadQueueEyebrow(st);
        }
      }
      return;
    }

    const toggleDlBtn = e.target.closest("[data-toggle-dl]");
    if (toggleDlBtn) {
      const dlId = toggleDlBtn.getAttribute("data-toggle-dl");
      const task = await window.LauncherBridge.invoke("toggle_download_task", { id: dlId });
      if (task) {
        showToast(`${task.name} ${task.status === "downloading" ? "继续下载" : "已暂停"}`);
        const st = window.LauncherBridge.getStateSync();
        const row = toggleDlBtn.closest("[data-dl-row]");
        if (row) {
          toggleDlBtn.outerHTML = window.LauncherViews.renderPauseResumeBtn(
            task.status === "downloading",
            task.id,
            task.name
          );
          const subEl = row.querySelector(".quick-mod-file");
          if (subEl) subEl.textContent = `${task.sub} · ${task.speed}`;
        }
        syncDownloadQueueEyebrow(st);
      }
      return;
    }

    if (e.target.closest("#btn-clear-completed-dl")) {
      await window.LauncherBridge.invoke("clear_completed_downloads");
      showToast("已清空已完成下载记录");
      const st = window.LauncherBridge.getStateSync();
      const remainingIds = new Set((st.downloadTasks || []).map((t) => t.id));
      document.querySelectorAll("[data-dl-row]").forEach((row) => {
        if (!remainingIds.has(row.getAttribute("data-dl-row"))) {
          if (!prefersReducedMotion()) {
            row.style.height = `${row.getBoundingClientRect().height}px`;
            void row.offsetHeight;
            row.classList.add("is-row-removing");
            setTimeout(() => {
              row.remove();
              syncDownloadQueueEyebrow(st);
            }, 270);
          } else {
            row.remove();
          }
        }
      });
      syncDownloadQueueEyebrow(st);
      return;
    }

    const detailLoaderBtn = e.target.closest("[data-detail-loader]");
    if (detailLoaderBtn) {
      const ld = detailLoaderBtn.getAttribute("data-detail-loader");
      if (detailLoaderBtn.classList.contains("is-incompatible-loader")) {
        const conflict = detailLoaderBtn.getAttribute("data-conflict-with") || "已选加载器";
        showToast(`${ld}与${conflict}不兼容！`);
        return;
      }
      detailState.activeLoader = ld;
      detailState.expandedMcVersion = null;
      syncResourceDetailLoaderUi();
      return;
    }

    const toggleMcGroupBtn = e.target.closest("[data-toggle-mc-group]");
    if (toggleMcGroupBtn) {
      const mcVer = toggleMcGroupBtn.getAttribute("data-toggle-mc-group");
      const groupEl = toggleMcGroupBtn.closest(".vdetail-mc-group");
      const willOpen = !groupEl.classList.contains("is-open");
      document.querySelectorAll(".vdetail-mc-group.is-open").forEach((g) => {
        if (g !== groupEl) {
          g.classList.remove("is-open");
          g.querySelector(".vdetail-mc-drawer").inert = true;
          const trig = g.querySelector(".vdetail-mc-trigger");
          if (trig) trig.setAttribute("aria-expanded", "false");
        }
      });
      if (groupEl) {
        const list = groupEl.querySelector(".vdetail-build-list");
        const state = window.LauncherBridge.getStateSync();
        const detail = window.LauncherBridge.getResourceDetail(detailState.catKey, detailState.id);
        if (willOpen && detail && !list.children.length) {
          list.innerHTML = window.LauncherViews.renderDetailBuildRows(
            detail, detailState.activeLoader, mcVer, state, detailState.selectedVersionBuilds);
        }
        groupEl.querySelector(".vdetail-mc-drawer").inert = !willOpen;
        groupEl.classList.toggle("is-open", willOpen);
        toggleMcGroupBtn.setAttribute("aria-expanded", String(willOpen));
      }
      detailState.expandedMcVersion = willOpen ? mcVer : null;
      return;
    }

    const extSiteBtn = e.target.closest("[data-ext-site]");
    if (extSiteBtn) {
      showToast(`正在打开 ${extSiteBtn.getAttribute("data-ext-site")} 官方页面`);
      return;
    }

    const portalBtn = e.target.closest("[data-portal]");
    if (portalBtn) {
      openPortal(portalBtn.getAttribute("data-portal"));
      return;
    }

    const subRouteBtn = e.target.closest("[data-sub-route]");
    if (subRouteBtn) {
      openSubWorkspace(subRouteBtn.getAttribute("data-sub-route"));
      return;
    }

    const routeBtn = e.target.closest("[data-route]");
    if (routeBtn) {
      navigateTo(routeBtn.getAttribute("data-route"));
      return;
    }

    if (e.target.closest("[data-hub-close]")) {
      collapseToTopWithDamping(() => {
        activeHubKey = null;
        activeSubRoute = null;
        resetListControls();
        syncHubOutletUi();
      });
      return;
    }

    const modPageBtn = e.target.closest("[data-mod-page]");
    if (modPageBtn && !modPageBtn.disabled) {
      const nextModPage = Number(modPageBtn.getAttribute("data-mod-page")) || 1;
      const slideDir = nextModPage >= modPage ? 1 : -1;
      modPage = nextModPage;
      if (modPageBtn.closest("#quick-panel")) {
        syncQuickPanelUi(window.LauncherBridge.getStateSync(), { quiet: true });
        animateHorizontalPageSlide(document.querySelector("#quick-panel .data-table-list"), slideDir);
      } else if (activeSubRoute && document.getElementById("hub-workspace-slot")) {
        syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: true });
        animateHorizontalPageSlide(document.querySelector("#hub-workspace .data-table-list"), slideDir);
      } else {
        render();
        animateHorizontalPageSlide(document.querySelector(".data-table-list"), slideDir);
      }
      return;
    }

    const delModBtn = e.target.closest("[data-delete-mod]");
    if (delModBtn) {
      const modId = delModBtn.getAttribute("data-delete-mod");
      if (!delModBtn.classList.contains("is-armed")) {
        document.querySelectorAll(".emu-delete-btn.is-armed").forEach((b) => b.classList.remove("is-armed"));
        delModBtn.classList.add("is-armed");
        return;
      }
      const removed = await window.LauncherBridge.invoke("delete_mod", { id: modId });
      if (removed) {
        showToast(`已移除 Mod：${removed.cnName || removed.name}`);
        const st = window.LauncherBridge.getStateSync();
        syncInstanceUi(st, false, false);
        const modRow = delModBtn.closest("[data-mod-row]");
        if (modRow && !prefersReducedMotion()) {
          modRow.style.height = `${modRow.getBoundingClientRect().height}px`;
          void modRow.offsetHeight;
          modRow.classList.add("is-row-removing");
          setTimeout(() => {
            if (activeQuickPanel === "mods") syncQuickPanelUi(st, { quiet: true });
            if (activeSubRoute === "manage-mods") syncSubWorkspaceSlot(st, { preserveAnimation: true });
          }, 260);
        } else {
          if (activeQuickPanel === "mods") syncQuickPanelUi(st, { quiet: true });
          if (activeSubRoute === "manage-mods") syncSubWorkspaceSlot(st, { preserveAnimation: true });
        }
      }
      return;
    }

    const pageBtn = e.target.closest("[data-page]");
    if (pageBtn && !pageBtn.disabled) {
      const nextPage = Number(pageBtn.dataset.page) || 1;
      if (nextPage === currentPage) return;
      const direction = Math.sign(nextPage - currentPage);
      currentPage = nextPage;
      const route = activeSubRoute || currentRoute;
      alignToSearchViewTop({ animate: true });
      if (routeToCatalogKey(route)) loadOnlineDownloadPage(route, { direction });
      else refreshDownloadResults(route, { direction });
      return;
    }

    if (e.target.closest("#btn-toggle-filter")) {
      filterOpen = !filterOpen;
      syncFilterSlotUi();
      return;
    }

    const filterLoaderBtn = e.target.closest("[data-filter-loader]");
    if (filterLoaderBtn) {
      const next = filterLoaderBtn.dataset.filterLoader;
      if (next !== loaderFilter) {
        loaderFilter = next;
        applyDownloadFilters();
      }
      return;
    }

    const filterSourceBtn = e.target.closest("[data-filter-source]");
    if (filterSourceBtn) {
      const next = filterSourceBtn.dataset.filterSource;
      if (next !== sourceFilter) {
        sourceFilter = next;
        applyDownloadFilters();
      }
      return;
    }

    const filterSortBtn = e.target.closest("[data-filter-sort]");
    if (filterSortBtn) {
      const next = filterSortBtn.dataset.filterSort;
      if (next && next !== sortFilter) {
        sortFilter = next;
        applyDownloadFilters();
      }
      return;
    }

    const filterCatBtn = e.target.closest("[data-filter-cat]");
    if (filterCatBtn) {
      const next = filterCatBtn.dataset.filterCat;
      const isDualTray = Boolean(document.querySelector("#cat-tray.cat-tray--dual"));
      filterCatBtn.blur();
      if (next && next !== categoryFilter) {
        categoryFilter = next;
        applyDownloadFilters({ keepCatTrayOpen: isDualTray });
      } else if (!isDualTray) {
        document.getElementById("cat-starfish-wrap")?.classList.remove("is-open");
        document.getElementById("cat-starfish-trigger")?.setAttribute("aria-expanded", "false");
      }
      return;
    }

    const filterSubCatBtn = e.target.closest("[data-filter-subcat]");
    if (filterSubCatBtn) {
      const next = filterSubCatBtn.dataset.filterSubcat;
      filterSubCatBtn.blur();
      if (next && next !== subCategoryFilter) {
        subCategoryFilter = next;
        applyDownloadFilters({ keepCatTrayOpen: true });
      }
      return;
    }

    const filterMcBtn = e.target.closest("[data-filter-mc]");
    if (filterMcBtn) {
      const next = filterMcBtn.dataset.filterMc;
      filterMcBtn.blur();
      if (next && next !== mcVersionFilter) {
        mcVersionFilter = next;
        applyDownloadFilters();
      } else {
        document.getElementById("mcver-starfish-wrap")?.classList.remove("is-open");
        document.getElementById("mcver-starfish-trigger")?.setAttribute("aria-expanded", "false");
      }
      return;
    }

    if (e.target.closest("[data-auto-mirror]")) {
      await window.LauncherBridge.invoke("set_mirror", { auto: true });
      syncMirrorChipsUi(window.LauncherBridge.getStateSync());
      return;
    }

    if (e.target.closest("#btn-open-folder-gallery")) {
      openFolderGallery();
      return;
    }

    const importFolderBtn = e.target.closest("#btn-import-instance-folder");
    if (importFolderBtn) {
      if (importFolderBtn.disabled) return;
      importFolderBtn.disabled = true;
      try {
        const res = await window.LauncherBridge.invoke("import_instance_folder", {});
        if (res && !res.cancelled) {
          const latestSt = window.LauncherBridge.getStateSync();
          syncFolderShelfUi(latestSt);
          syncInstanceUi(latestSt, false, false);
          if (currentRoute === "instance-gallery") {
            syncGalleryGridUi(latestSt);
          }
          const label = res.primaryName || res.firstId || "";
          showToast(res.count > 1 ? `已导入 ${res.count} 个版本文件夹（${label} 等）` : `已导入版本文件夹：${label}`);
        }
      } catch (err) {
        showToast(err?.message || "导入版本文件夹失败");
      } finally {
        importFolderBtn.disabled = false;
      }
      return;
    }

    const galleryPickBtn = e.target.closest("[data-gallery-pick-inst]");
    if (galleryPickBtn) {
      const id = galleryPickBtn.getAttribute("data-gallery-pick-inst");
      await window.LauncherBridge.invoke("set_active_instance", { id, promoteRecent: true });
      showToast("已切换当前版本");
      leaveFolderGallery(() => {
        navHistory = [];
        currentRoute = "home";
        activeHubKey = null;
        activeSubRoute = null;
        resetListControls();
        render({ isReturning: true, animateStroke: true });
      });
      return;
    }

    const selectInstBtn = e.target.closest("[data-select-inst]");
    if (selectInstBtn) {
      const id = selectInstBtn.getAttribute("data-select-inst");
      const prevId = window.LauncherBridge.getStateSync()?.activeInstanceId;
      if (id !== prevId) {
        modPage = 1;
        modQuery = "";
      }
      await window.LauncherBridge.invoke("set_active_instance", { id });
      showToast("已切换当前激活实例");
      syncInstanceUi(window.LauncherBridge.getStateSync(), false, true);
      return;
    }

    const mirrorBtn = e.target.closest("[data-mirror]");
    if (mirrorBtn) {
      const mirror = mirrorBtn.getAttribute("data-mirror");
      await window.LauncherBridge.invoke("set_mirror", { mirror });
      syncMirrorChipsUi(window.LauncherBridge.getStateSync());
      return;
    }

    const toggleModBtn = e.target.closest("[data-toggle-mod]");
    if (toggleModBtn) {
      const modId = toggleModBtn.getAttribute("data-toggle-mod");
      const mod = await window.LauncherBridge.invoke("toggle_mod", { id: modId });
      if (mod) {
        showToast(`${mod.name} 已${mod.enabled ? "启用 (.jar)" : "禁用 (.jar.disabled)"}`);
        document.querySelectorAll(`[data-toggle-mod="${modId}"]`).forEach((btn) => {
          btn.classList.toggle("is-on", mod.enabled);
          btn.setAttribute("aria-pressed", String(Boolean(mod.enabled)));
          const row = btn.closest(".data-row");
          if (row) row.classList.toggle("is-disabled-item", !mod.enabled);
          const fileEl = row ? row.querySelector(".quick-mod-file") : null;
          if (fileEl) fileEl.textContent = `v${mod.version} · ${mod.file}${mod.enabled ? "" : ".disabled"}`;
        });
      }
      return;
    }

    const togglePackBtn = e.target.closest("[data-toggle-pack]");
    if (togglePackBtn) {
      const packId = togglePackBtn.getAttribute("data-toggle-pack");
      const pack = await window.LauncherBridge.invoke("toggle_pack", { id: packId });
      if (pack) {
        showToast(`${pack.name} 已${pack.enabled ? "启用" : "停用"}`);
        document.querySelectorAll(`[data-toggle-pack="${packId}"]`).forEach((btn) => {
          btn.classList.toggle("is-on", pack.enabled);
          btn.setAttribute("aria-pressed", String(Boolean(pack.enabled)));
          const row = btn.closest(".data-row");
          if (row) row.classList.toggle("is-disabled-item", !pack.enabled);
        });
      }
      return;
    }

    const openDepBtn = e.target.closest("[data-open-dep]");
    if (openDepBtn) {
      const rawRef = openDepBtn.getAttribute("data-open-dep") || "";
      const splitIdx = rawRef.indexOf(":");
      if (splitIdx > 0) {
        const depCat = rawRef.slice(0, splitIdx);
        const depId = rawRef.slice(splitIdx + 1);
        const bridge = window.LauncherBridge;
        const curDetail = bridge.getCachedResourceDetail(detailState.catKey, detailState.id) || bridge.getResourceDetail(detailState.catKey, detailState.id);
        const depLoader = openDepBtn.getAttribute("data-dep-loader") || detailState.activeLoader;
        const depMc = openDepBtn.getAttribute("data-dep-mc") || detailState.expandedMcVersion;
        const matchedGrp = (curDetail?.loaderTrees?.[depLoader] || []).find((g) => g.mcVersion === depMc);
        const depMeta = (matchedGrp?.dependencies || []).find((d) => String(d.id) === String(depId));
        if (depMeta && typeof bridge.rememberCatalogItems === "function") {
          bridge.rememberCatalogItems(depCat, [depMeta]);
        }
        openResourceDetail(depCat, depId);
      }
      return;
    }

    const directVerRow = e.target.closest("[data-direct-version-download]");
    const isDirectVerBtn = e.target.closest("[data-direct-version-btn]");
    if (isDirectVerBtn || directVerRow) {
      e.stopPropagation();
      const el = isDirectVerBtn || directVerRow;
      const mcVer = el.dataset.mcVersion || el.getAttribute("data-direct-version-download");
      const task = {
        name: el.dataset.queueName || `Minecraft ${mcVer}`,
        sub: el.dataset.queueSub || `${mcVer}.jar`,
        icon: el.dataset.queueIcon || "assets/mc-icons/bedrock.png",
        size: el.dataset.queueSize || "官方客户端",
        installType: "version",
        mcVersion: mcVer,
        loaders: []
      };
      try {
        await window.LauncherBridge.invoke("add_download_task", task);
        syncFloatingDownloadBallUi(window.LauncherBridge.getStateSync());
        showToast(`已开始下载原版 Minecraft ${mcVer}`);
        navigateTo("download-queue");
      } catch (error) {
        showToast(error.message || "无法开始下载");
      }
      return;
    }

    const modManageRow = e.target.closest("[data-open-mod-detail]");
    if (modManageRow && !e.target.closest(".mod-action-group")) {
      const modInfo = {
        id: modManageRow.getAttribute("data-open-mod-detail") || "",
        slug: modManageRow.getAttribute("data-mod-slug") || "",
        name: modManageRow.getAttribute("data-mod-name") || "",
        file: modManageRow.getAttribute("data-mod-file") || ""
      };
      showToast(`正在查找 ${modInfo.name || modInfo.file} 详情...`);
      try {
        const resolved = await window.LauncherBridge.resolveModProject(modInfo);
        if (resolved && resolved.id) {
          if (resolved.preview && typeof window.LauncherBridge.rememberCatalogItems === "function") {
            window.LauncherBridge.rememberCatalogItems(resolved.catKey || "mods", [resolved.preview]);
          }
          openResourceDetail(resolved.catKey || "mods", resolved.id);
        } else {
          showToast("未在 Modrinth/CurseForge 找到该 Mod 在线详情");
        }
      } catch (error) {
        showToast(error.message || "无法查询该 Mod 详情");
      }
      return;
    }

    const installBtn = e.target.closest("[data-install-action]") ||
      e.target.closest(".data-row--build.is-selectable-build")?.querySelector("[data-install-action]");
    if (installBtn) {
      e.stopPropagation();
      const task = {
        name: installBtn.dataset.queueName,
        projectName: installBtn.dataset.queueProject,
        sub: installBtn.dataset.queueSub || "",
        icon: installBtn.dataset.queueIcon || "assets/mc-icons/cobblestone.png",
        size: installBtn.dataset.queueSize || "",
        installType: installBtn.dataset.installType || "",
        mcVersion: installBtn.dataset.mcVersion || "",
        projectId: installBtn.dataset.projectId || "",
        catKey: installBtn.dataset.catKey || "",
        buildId: installBtn.dataset.buildRef || ""
      };
      if (!task.name) return;
      if (task.catKey === "modpacks" && !task.buildId) {
        openResourceDetail("modpacks", task.projectId);
        return;
      }
      if (task.catKey === "modpacks") task.installType = "modpack";
      if (["mods", "packs", "shaders"].includes(task.catKey)) {
        await chooseResourceTarget(task);
        return;
      }
      try {
        await window.LauncherBridge.invoke("add_download_task", task);
        syncFloatingDownloadBallUi(window.LauncherBridge.getStateSync());
        showToast(task.catKey === "modpacks" ? `已加入整合包安装队列：${task.name}` : `已加入下载队列：${task.name}`);
      } catch (error) {
        showToast(error.message);
      }
      return;
    }

    const openResourceRow = e.target.closest("[data-open-resource]");
    if (openResourceRow) {
      const rawRef = openResourceRow.getAttribute("data-open-resource") || "";
      const splitIdx = rawRef.indexOf(":");
      if (splitIdx > 0) {
        openResourceDetail(rawRef.slice(0, splitIdx), rawRef.slice(splitIdx + 1));
      }
      return;
    }

    const toastBtn = e.target.closest("[data-toast]");
    if (toastBtn) {
      showToast(toastBtn.getAttribute("data-toast"));
      return;
    }

    if (e.target.closest("#btn-locate-crash-mod")) {
      const res = await window.LauncherBridge.invoke("open_folder", { id: window.LauncherBridge.getStateSync().activeInstanceId, subpath: "mods" });
      showToast(`已定位 Mods 目录：${res.path || ".minecraft\\mods"}`);
      return;
    }

    if (e.target.closest("#btn-open-launcher-logs")) {
      await window.LauncherBridge.invoke("open_launcher_logs", {});
      return;
    }

    if (e.target.closest("#btn-export-crash-report")) {
      const res = await window.LauncherBridge.invoke("export_crash_report", {});
      showToast(res && res.path ? `诊断报告已导出：${res.path}` : "诊断报告已导出");
      return;
    }

    if (e.target.closest("#btn-launch-game")) {
      const st = window.LauncherBridge.getStateSync();
      if (!st.instances || st.instances.length === 0) {
        showToast("尚未安装任何游戏版本，请先下载安装");
        return;
      }
      const targetId = st.activeInstanceId;
      if (pendingLaunchIds.has(targetId)) {
        pendingLaunchIds.delete(targetId);
        cancelLaunchSimulation(targetId);
        launchProgress = 0;
        syncLaunchMeterUi();
        syncLaunchButtonUi(st);
        if (window.LauncherBridge.isWebView2) {
          await window.LauncherBridge.invoke("cancel_launch", { instanceId: targetId }).catch(() => {});
        }
        showToast("已取消启动当前版本");
        return;
      }
      pendingLaunchIds.add(targetId);
      syncLaunchButtonUi(st);
      if (window.LauncherBridge.isWebView2) {
        const res = await window.LauncherBridge.invoke("launch_game", { instanceId: targetId });
        if (!res || res.ok === false) {
          pendingLaunchIds.delete(targetId);
          syncLaunchButtonUi(window.LauncherBridge.getStateSync());
          showToast((res && res.message) || "启动失败，请检查实例与 Java 配置");
          return;
        }
        launchProgress = 12;
        syncLaunchMeterUi();
        showToast((res && res.message) || "正在准备启动游戏...");
        return;
      }
      runLaunchSimulation(targetId);
      return;
    }

    if (e.target.closest("#btn-toggle-automem")) {
      const st = window.LauncherBridge.getStateSync();
      await window.LauncherBridge.invoke("set_memory", {
        auto: !st.autoMemory,
        mb: !st.autoMemory ? 6144 : st.memoryMb
      });
      const updated = window.LauncherBridge.getStateSync();
      showToast(updated.autoMemory ? `已启用自动配置 (${updated.memoryMb} MB)` : "已切换为手动内存分配");
      const sw = document.getElementById("btn-toggle-automem");
      if (sw) {
        sw.classList.toggle("is-on", updated.autoMemory);
        sw.setAttribute("aria-pressed", String(Boolean(updated.autoMemory)));
      }
      const wrap = document.getElementById("diag-ram-controls");
      if (wrap) wrap.classList.toggle("is-auto-dimmed", Boolean(updated.autoMemory));
      const slider = document.getElementById("ram-slider");
      if (slider) slider.value = updated.memoryMb;
      syncRareTickBars("ram-slider", updated.memoryMb);
      animateCounterElement("diag-ram-counter", updated.memoryMb);
      const activeInst = window.LauncherViews.getActiveInstance(updated);
      if (activeInst && activeInst.useGlobalSettings) {
        const qSlider = document.getElementById("quick-ram-slider");
        if (qSlider) qSlider.value = updated.memoryMb;
        syncRareTickBars("quick-ram-slider", updated.memoryMb);
        animateCounterElement("quick-ram-counter", updated.memoryMb);
        const qAutoBtn = document.getElementById("btn-toggle-quick-automem");
        if (qAutoBtn) {
          qAutoBtn.classList.toggle("is-on", Boolean(updated.autoMemory));
          qAutoBtn.setAttribute("aria-pressed", String(Boolean(updated.autoMemory)));
        }
        const qRamControls = document.getElementById("quick-ram-controls");
        if (qRamControls) qRamControls.classList.toggle("is-auto-dimmed", Boolean(updated.autoMemory));
      }
      updateTopMeta(updated);
      return;
    }

    if (e.target.closest("#btn-toggle-quick-automem")) {
      const st = window.LauncherBridge.getStateSync();
      const activeInst = window.LauncherViews.getActiveInstance(st);
      const curAuto = activeInst.useGlobalSettings ? Boolean(st.autoMemory) : Boolean(activeInst.autoMemory);
      const nextAuto = !curAuto;
      const updated = await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        autoMemory: nextAuto
      });
      if (updated) {
        const after = window.LauncherBridge.getStateSync();
        const actAfter = window.LauncherViews.getActiveInstance(after);
        const nextMb = actAfter.memoryMb || after.memoryMb;
        syncInstanceUi(after, false);
        showToast(nextAuto ? `已开启当前版本自动分配 (${nextMb} MB)` : "已切换为当前版本手动内存");
      }
      return;
    }

    if (e.target.closest("#btn-microsoft-login")) {
      await signInWithMicrosoft();
      return;
    }

    const refreshAccount = e.target.closest("#btn-refresh-token");
    if (refreshAccount) {
      if (!window.LauncherBridge.isWebView2) return showToast("请在桌面启动器中刷新账号会话。");
      refreshAccount.disabled = true;
      try {
        const account = await window.LauncherBridge.invoke("microsoft_refresh", {});
        showToast(`已刷新 ${account.username} 的账号会话`);
      } catch (error) {
        showToast(error.message);
      } finally {
        refreshAccount.disabled = false;
      }
      return;
    }

    const selectQuickJavaBtn = e.target.closest("[data-select-quick-java]");
    if (selectQuickJavaBtn) {
      const javaId = selectQuickJavaBtn.getAttribute("data-select-quick-java");
      const st = window.LauncherBridge.getStateSync();
      const updated = await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        javaId
      });
      if (updated) {
        const after = window.LauncherBridge.getStateSync();
        const j = (after.javas || []).find((item) => item.id === javaId);
        const nav = document.getElementById("quick-gooey-nav");
        if (nav) {
          nav.querySelectorAll(".gooey-nav__link[data-select-quick-java]").forEach((btn) => {
            const isAct = btn.getAttribute("data-select-quick-java") === javaId;
            btn.classList.toggle("is-active", isAct);
            btn.setAttribute("aria-selected", String(isAct));
          });
          nav.querySelectorAll(".gooey-nav__list--active .gooey-nav__link").forEach((sp) => {
            sp.classList.toggle("is-active", sp.getAttribute("data-gooey-mirror") === javaId);
          });
          syncGooeyNav("quick-gooey-nav", true);
        }
        const metaEl = document.getElementById("quick-java-meta");
        if (metaEl && j) metaEl.textContent = `Java ${j.version} · ${j.vendor}`;
        syncVersionJavaWarningUi(after);
        showToast(`已切换当前版本 Java：Java ${j ? j.version : ""}`);
      }
      return;
    }

    const selectJavaBtn = e.target.closest("[data-select-java]");
    if (selectJavaBtn) {
      const javaId = selectJavaBtn.getAttribute("data-select-java");
      const selected = await window.LauncherBridge.invoke("set_active_java", { id: javaId });
      if (selected) {
        const nav = document.getElementById("java-gooey-nav");
        if (nav) {
          nav.querySelectorAll(".gooey-nav__link[data-select-java]").forEach((btn) => {
            const isAct = btn.getAttribute("data-select-java") === selected.id;
            btn.classList.toggle("is-active", isAct);
            btn.setAttribute("aria-selected", String(isAct));
          });
          nav.querySelectorAll(".gooey-nav__list--active .gooey-nav__link").forEach((sp) => {
            sp.classList.toggle("is-active", sp.getAttribute("data-gooey-mirror") === selected.id);
          });
          syncGooeyNav("java-gooey-nav", true);
        }
        const titleEl = document.getElementById("java-selected-title");
        const vendorEl = document.getElementById("java-selected-vendor");
        const pathEl = document.getElementById("java-selected-path");
        const cardEl = document.getElementById("java-selected-card");
        if (titleEl) titleEl.textContent = `Java ${selected.version} (${selected.arch})`;
        if (vendorEl) vendorEl.textContent = selected.vendor;
        if (pathEl) pathEl.textContent = selected.path;
        if (cardEl && !prefersReducedMotion()) {
          cardEl.classList.remove("is-switching");
          void cardEl.offsetWidth;
          cardEl.classList.add("is-switching");
        }
        syncVersionJavaWarningUi(window.LauncherBridge.getStateSync());
        showToast(`已切换全局 Java：Java ${selected.version}`);
      }
      return;
    }

    const scanJavaButton = e.target.closest("#btn-scan-local-java");
    if (scanJavaButton) {
      if (javaScanPending) return;
      javaScanPending = true;
      scanJavaButton.disabled = true;
      scanJavaButton.setAttribute("aria-busy", "true");
      scanJavaButton.textContent = "正在扫描本机磁盘…";
      try {
        const res = await window.LauncherBridge.invoke("scan_local_javas");
        const javas = res?.javas || window.LauncherBridge.getStateSync().javas;
        showToast(res?.added > 0 ? `扫描完成，新发现 ${res.added} 个 Java，共 ${javas.length} 个`
          : `扫描完成，找到 ${javas.length} 个 Java`);
      } catch (error) {
        showToast(`Java 扫描失败：${error.message}`);
      } finally {
        javaScanPending = false;
        if (activeSubRoute === "auth-java" && document.getElementById("hub-outlet")) {
          syncHubOutletUi({ animateMasonry: false });
        } else if (currentRoute === "auth-java") {
          render();
        }
      }
      return;
    }

    if (e.target.closest("#btn-toggle-java-installer")) {
      javaInstallerOpen = !javaInstallerOpen;
      if (javaInstallerOpen) {
        showNetworkLoader("正在获取 Java 官方发行版列表...", 850);
      }
      const btn = document.getElementById("btn-toggle-java-installer");
      const drawer = document.getElementById("java-official-drawer");
      if (btn && drawer) {
        btn.classList.toggle("is-active", javaInstallerOpen);
        btn.setAttribute("aria-expanded", String(javaInstallerOpen));
        drawer.classList.toggle("is-open", javaInstallerOpen);
      }
      return;
    }

    function syncJavaInstallerDrawerContent() {
      const inner = document.querySelector("#java-official-drawer .java-official-drawer-inner");
      if (inner && typeof window.LauncherViews.renderOfficialJavaDrawerInnerHtml === "function") {
        inner.innerHTML = window.LauncherViews.renderOfficialJavaDrawerInnerHtml(window.LauncherBridge.getStateSync(), getViewOpts());
        return true;
      }
      return false;
    }

    const vendorBtn = e.target.closest(".chip-btn[data-java-vendor]");
    if (vendorBtn) {
      const nextVendor = vendorBtn.getAttribute("data-java-vendor") || "temurin";
      if (nextVendor !== javaVendor) {
        javaVendor = nextVendor;
        if (!syncJavaInstallerDrawerContent()) {
          if (activeSubRoute === "auth-java" && document.getElementById("hub-outlet")) {
            syncHubOutletUi({ animateMasonry: false });
          } else if (currentRoute === "auth-java") {
            render();
          }
        }
      }
      return;
    }

    if (e.target.closest("#btn-browse-java-dir")) {
      try {
        const res = await window.LauncherBridge.invoke("browse_java_install_folder", {});
        if (res && res.path) {
          javaInstallPath = res.path;
          javaInstallToken = res.token || "";
          if (!syncJavaInstallerDrawerContent()) {
            if (activeSubRoute === "auth-java" && document.getElementById("hub-outlet")) {
              syncHubOutletUi({ animateMasonry: false });
            } else if (currentRoute === "auth-java") {
              render();
            }
          }
          showToast(`已设置 Java 安装目录：${res.path}`);
        }
      } catch (error) {
        showToast(error.message || "无法选择安装目录");
      }
      return;
    }

    if (e.target.closest("#btn-reset-java-dir")) {
      javaInstallPath = "";
      javaInstallToken = "";
      if (!syncJavaInstallerDrawerContent()) {
        if (activeSubRoute === "auth-java" && document.getElementById("hub-outlet")) {
          syncHubOutletUi({ animateMasonry: false });
        } else if (currentRoute === "auth-java") {
          render();
        }
      }
      showToast("已恢复默认 Java 安装目录");
      return;
    }

    const installOffJavaBtn = e.target.closest("[data-install-official-java]");
    if (installOffJavaBtn) {
      const offId = installOffJavaBtn.getAttribute("data-install-official-java");
      const offVendor = installOffJavaBtn.getAttribute("data-java-vendor") || javaVendor;
      showNetworkLoader("正在连接 Java 官网获取安装包...", 1200);
      const res = await window.LauncherBridge.invoke("install_java_official", {
        id: offId,
        vendor: offVendor,
        folderToken: javaInstallToken,
        installPath: javaInstallPath
      });
      const st = window.LauncherBridge.getStateSync();
      syncFloatingDownloadBallUi(st);
      if (activeSubRoute === "auth-java" && document.getElementById("hub-outlet")) {
        syncHubOutletUi({ animateMasonry: false });
      } else {
        render();
      }
      if (res && res.official) {
        showToast(`已添加官网安装任务：Java ${res.official.fullVersion} (${res.official.vendor})`);
      }
      return;
    }

    if (e.target.closest("#btn-open-crash-window")) {
      const modal = document.getElementById("crash-window-modal");
      const body = document.getElementById("crash-modal-body");
      const st = window.LauncherBridge.getStateSync();
      const inst = window.LauncherViews.getActiveInstance(st);
      const mem = inst.useGlobalSettings ? st.memoryMb : (inst.memoryMb || st.memoryMb);
      if (body) {
        body.innerHTML = `
          <div class="crash-log crash-log--expanded">
            <div>[23:18:04] [main/ERROR]: Mixin apply for mod sodium failed</div>
            <div class="crash-highlight">↳ 置信度 96%：Mixin 注入冲突 (Sodium ↔ Iris)</div>
            <div>· 当前实例：${inst.name} (${inst.mcVersion} · ${inst.loader})</div>
            <div>· 运行时环境：${inst.javaVersion} · -Xmx${mem}M</div>
            <div>· 冲突模组：sodium-neoforge-0.6.3.jar &amp; iris-neoforge-1.8.1.jar</div>
            <div>· 调用栈：net.caffeinemc.mods.sodium.mixin.core.render.world.WorldRendererMixin</div>
            <div>· 建议修复：将 Sodium 更新至 0.6.5+ 或临时禁用不兼容渲染注入插件</div>
          </div>`;
      }
      if (modal) {
        modal.classList.add("is-open");
        modal.setAttribute("aria-hidden", "false");
      }
      return;
    }

    if (e.target.closest("#btn-close-crash-window") || e.target.id === "crash-window-modal") {
      const modal = document.getElementById("crash-window-modal");
      if (modal) {
        modal.classList.remove("is-open");
        modal.setAttribute("aria-hidden", "true");
      }
      return;
    }

    if (e.target.closest("#btn-copy-crash-report")) {
      const body = document.getElementById("crash-modal-body");
      const text = body ? body.innerText : "";
      if (navigator.clipboard && navigator.clipboard.writeText && text) {
        navigator.clipboard.writeText(text).catch(() => {});
      }
      showToast("已复制完整报错分析日志");
      return;
    }

    const quickPanelBtn = e.target.closest("[data-quick-panel]");
    if (quickPanelBtn) {
      const st = window.LauncherBridge.getStateSync();
      if (!st.instances || st.instances.length === 0) {
        showToast("当前没有游戏版本，无法打开该选项");
        return;
      }
      const key = quickPanelBtn.getAttribute("data-quick-panel");
      const closing = activeQuickPanel === key;
      activeQuickPanel = closing ? null : key;
      syncQuickPanelUi(st, { animateClose: closing });
      return;
    }

    if (e.target.closest("[data-quick-close]")) {
      activeQuickPanel = null;
      syncQuickPanelUi(window.LauncherBridge.getStateSync(), { animateClose: true });
      return;
    }

    const setIconBtn = e.target.closest("[data-set-icon]");
    if (setIconBtn) {
      const st = window.LauncherBridge.getStateSync();
      const iconId = setIconBtn.getAttribute("data-set-icon");
      await window.LauncherBridge.invoke("set_instance_icon", {
        id: st.activeInstanceId,
        iconId
      });
      showToast("已更新版本图标");
      syncInstanceUi(window.LauncherBridge.getStateSync(), true);
      return;
    }

    const setFolderColorBtn = e.target.closest("[data-set-folder-color]");
    if (setFolderColorBtn) {
      const st = window.LauncherBridge.getStateSync();
      const colorId = setFolderColorBtn.getAttribute("data-set-folder-color");
      await window.LauncherBridge.invoke("set_instance_color", {
        id: st.activeInstanceId,
        colorId
      });
      showToast("已切换版本文件夹配色");
      syncInstanceColorUi(window.LauncherBridge.getStateSync(), true);
      return;
    }

    const ramStepBtn = e.target.closest("[data-ram-step]");
    if (ramStepBtn) {
      const target = ramStepBtn.getAttribute("data-ram-target") || "diag";
      const step = Number(ramStepBtn.getAttribute("data-ram-step")) || 0;
      const st = window.LauncherBridge.getStateSync();
      const active = window.LauncherViews.getActiveInstance(st);
      const curMb =
        target === "quick"
          ? active.useGlobalSettings
            ? st.memoryMb
            : active.memoryMb || st.memoryMb
          : st.memoryMb;
      await applyRamValue(target, curMb + step);
      return;
    }

    if (e.target.closest("#btn-apply-global-settings")) {
      const st = window.LauncherBridge.getStateSync();
      const active = window.LauncherViews.getActiveInstance(st);
      const nextGlobal = !Boolean(active.useGlobalSettings);
      const updated = await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        useGlobalSettings: nextGlobal
      });
      if (updated) {
        const after = window.LauncherBridge.getStateSync();
        syncInstanceUi(after, false);
        showToast(nextGlobal ? `已采用全局设置 (${after.memoryMb} MB)` : "已切换为当前版本独立配置");
      }
      return;
    }

    const quickJvmPresetBtn = e.target.closest("[data-quick-jvm-preset]");
    if (quickJvmPresetBtn) {
      const st = window.LauncherBridge.getStateSync();
      const nextArgs = quickJvmPresetBtn.getAttribute("data-quick-jvm-preset") || "";
      await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        jvmArgs: nextArgs
      });
      const inp = document.getElementById("quick-jvm-args");
      if (inp) {
        inp.value = nextArgs;
        syncJvmTextareaHeight(inp);
      }
      document.querySelectorAll("[data-quick-jvm-preset]").forEach((b) => b.classList.toggle("is-active", b === quickJvmPresetBtn));
      const toggleBtn = document.getElementById("btn-apply-global-settings");
      if (toggleBtn) {
        toggleBtn.classList.remove("is-on");
        toggleBtn.setAttribute("aria-pressed", "false");
      }
      document.getElementById("quick-settings-body")?.classList.remove("is-global-dimmed");
      showToast("已应用当前版本 JVM 启动预设");
      return;
    }

    const jvmPresetBtn = e.target.closest("[data-jvm-preset]");
    if (jvmPresetBtn) {
      const nextArgs = jvmPresetBtn.getAttribute("data-jvm-preset") || "";
      await window.LauncherBridge.invoke("set_jvm_args", { jvmArgs: nextArgs });
      const inp = document.getElementById("diag-jvm-args-input");
      if (inp) {
        inp.value = nextArgs;
        syncJvmTextareaHeight(inp);
      }
      document.querySelectorAll("[data-jvm-preset]").forEach((b) => b.classList.toggle("is-active", b === jvmPresetBtn));
      showToast("已应用 JVM 参数预设");
      return;
    }

    const openFolderBtn = e.target.closest("[data-open-folder]");
    if (openFolderBtn) {
      const st = window.LauncherBridge.getStateSync();
      const subpath = openFolderBtn.getAttribute("data-open-folder");
      const res = await window.LauncherBridge.invoke("open_folder", {
        id: st.activeInstanceId,
        subpath
      });
      showToast(`已定位目录：${res.path}`);
      return;
    }
  });

  document.addEventListener("change", async (e) => {
    if (e.target.id === "quick-java-select") {
      const st = window.LauncherBridge.getStateSync();
      await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        javaId: e.target.value
      });
      showToast("已切换当前版本 Java 运行时");
      syncInstanceUi(window.LauncherBridge.getStateSync(), false);
    }

  });

  document.addEventListener("pointerover", (e) => {
    const dockBtn = e.target.closest(".emu-btn[data-quick-panel]");
    if (!dockBtn) return;
    const labels = { overview: "概览", settings: "版本设置", mods: "Mod 管理", folders: "版本文件夹" };
    const key = dockBtn.getAttribute("data-quick-panel");
    setDockCaption(labels[key] || "");
  });

  document.addEventListener("pointerout", (e) => {
    const dockBtn = e.target.closest(".emu-btn[data-quick-panel]");
    if (!dockBtn) return;
    if (dockBtn.contains(e.relatedTarget)) return;
    const labels = { overview: "概览", settings: "版本设置", mods: "Mod 管理", folders: "版本文件夹" };
    setDockCaption(activeQuickPanel ? labels[activeQuickPanel] || "" : "");
  });

  function handleSearchBoxUpdate(target, immediate) {
    if (!target) return;
    if (target.id === "subpage-search") {
      searchQuery = target.value;
      currentPage = 1;
      modPage = 1;
      target.classList.toggle("has-value", Boolean(searchQuery));
      const route = activeSubRoute || currentRoute;
      if (routeToCatalogKey(route)) {
        clearTimeout(onlineSearchTimer);
        clearTimeout(catalogPrefetchTimer);
        onlineSearchSeq += 1;
        if (immediate) loadOnlineDownloadPage(route);
        else onlineSearchTimer = setTimeout(() => loadOnlineDownloadPage(route), 240);
        return;
      }
      const pos = target.selectionStart;
      if (activeSubRoute && document.getElementById("hub-workspace-slot")) {
        syncSubWorkspaceSlot(window.LauncherBridge.getStateSync(), { preserveAnimation: true });
      } else {
        render();
      }
      const restored = document.getElementById("subpage-search");
      if (restored) {
        restored.focus();
        restored.setSelectionRange(pos, pos);
      }
      return;
    }

    if (target.id === "quick-mod-search") {
      modQuery = target.value;
      modPage = 1;
      target.classList.toggle("has-value", Boolean(modQuery));
      const pos = target.selectionStart;
      syncQuickPanelUi(window.LauncherBridge.getStateSync(), { quiet: true });
      const restored = document.getElementById("quick-mod-search");
      if (restored) {
        restored.focus();
        restored.setSelectionRange(pos, pos);
      }
    }
  }

  document.addEventListener("compositionend", (e) => {
    if (e.target && ["subpage-search", "quick-mod-search"].includes(e.target.id)) {
      handleSearchBoxUpdate(e.target, false);
    }
  });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.isComposing && e.target && e.target.id === "subpage-search") {
      e.preventDefault();
      handleSearchBoxUpdate(e.target, true);
    }
  });

  document.addEventListener("input", async (e) => {
    if (e.isComposing && ["subpage-search", "quick-mod-search"].includes(e.target.id)) return;
    if (e.target.id === "subpage-search" || e.target.id === "quick-mod-search") {
      handleSearchBoxUpdate(e.target, false);
      return;
    }

    if (e.target.id === "ram-slider") {
      await applyRamValue("diag", Number(e.target.value));
    }

    if (e.target.id === "quick-ram-slider") {
      await applyRamValue("quick", Number(e.target.value));
    }

    if (e.target.id === "diag-jvm-args-input") {
      syncJvmTextareaHeight(e.target);
      const val = e.target.value;
      await window.LauncherBridge.invoke("set_jvm_args", { jvmArgs: val });
      document.querySelectorAll("[data-jvm-preset]").forEach((b) => {
        b.classList.toggle("is-active", (b.getAttribute("data-jvm-preset") || "") === val.trim());
      });
    }

    if (e.target.id === "quick-jvm-args") {
      syncJvmTextareaHeight(e.target);
      const st = window.LauncherBridge.getStateSync();
      const val = e.target.value;
      await window.LauncherBridge.invoke("update_instance_settings", {
        id: st.activeInstanceId,
        jvmArgs: val
      });
      document.querySelectorAll("[data-quick-jvm-preset]").forEach((b) => {
        const p = b.getAttribute("data-quick-jvm-preset") || "";
        const match = (p.includes("UseZGC") && val.includes("UseZGC"))
          || (p.includes("UseG1GC") && val.includes("UseG1GC"))
          || (p.includes("UseShenandoahGC") && val.includes("UseShenandoahGC"));
        b.classList.toggle("is-active", match);
      });
    }
  });

  /* ---------- Static Shell Controls & Native Windows Window Lifecycle ---------- */
  if (window.LauncherBridge && window.LauncherBridge.isWebView2) {
    document.documentElement.classList.add("is-desktop-host");
    const resizeEdges = ["left", "right", "top", "bottom", "topleft", "topright", "bottomleft", "bottomright"];
    resizeEdges.forEach((edge) => {
      const strip = document.createElement("div");
      strip.className = `win-resize-edge win-resize-edge--${edge}`;
      strip.dataset.winResize = edge;
      strip.setAttribute("aria-hidden", "true");
      strip.addEventListener("mousedown", (e) => {
        if (e.button !== 0 || document.documentElement.dataset.windowMaximized === "true") return;
        e.preventDefault();
        e.stopPropagation();
        window.LauncherBridge.invoke("window_start_resize", { edge });
      });
      document.body.appendChild(strip);
    });
  }

  const btnWinMin = document.getElementById("btn-win-min");
  const btnWinMax = document.getElementById("btn-win-max");

  function syncWindowMaximized(maximized) {
    const value = String(Boolean(maximized));
    document.documentElement.dataset.windowMaximized = value;
    if (btnWinMax) btnWinMax.dataset.maximized = value;
  }
  const btnWinClose = document.getElementById("btn-win-close");
  if (btnWinMin) {
    btnWinMin.addEventListener("click", () => {
      window.LauncherBridge.invoke("window_minimize", {});
    });
  }
  if (btnWinMax) {
    btnWinMax.addEventListener("click", () => {
      window.LauncherBridge.invoke("window_toggle_maximize", {});
    });
  }
  if (btnWinClose) {
    btnWinClose.addEventListener("click", () => {
      window.LauncherBridge.invoke("window_close", {});
    });
  }
  const topbarEl = document.querySelector(".topbar");
  if (topbarEl) {
    topbarEl.addEventListener("mousedown", (e) => {
      if (e.button !== 0 || !window.LauncherBridge || !window.LauncherBridge.isWebView2) return;
      if (e.target.closest("button, a, input, select, textarea, .nav-btn, .win-ctrl-btn, .palette-theme-btn, .menu-btn")) return;
      window.LauncherBridge.invoke("window_start_drag", {});
    });
    topbarEl.addEventListener("dblclick", (e) => {
      if (!window.LauncherBridge || !window.LauncherBridge.isWebView2) return;
      if (e.target.closest("button, a, input, select, textarea, .nav-btn, .win-ctrl-btn, .palette-theme-btn, .menu-btn")) return;
      window.LauncherBridge.invoke("window_toggle_maximize", {});
    });
  }

  if (btnNavBack) btnNavBack.addEventListener("click", goBack);
  if (btnNavHome) btnNavHome.addEventListener("click", goHome);

  document.getElementById("portal-close").addEventListener("click", closePortal);
  portalBackdrop.addEventListener("click", (e) => {
    if (e.target === portalBackdrop) closePortal();
  });

  document.getElementById("btn-open-drawer").addEventListener("click", openDrawer);
  document.getElementById("btn-close-drawer").addEventListener("click", closeDrawer);

  const btnCycleTheme = document.getElementById("btn-cycle-theme");
  if (btnCycleTheme) {
    syncPaletteButtonUi(THEME_PRESETS[currentThemeIndex]);
    btnCycleTheme.addEventListener("click", cycleThemeWithWipe);
  }

  document.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && e.target && e.target.id === "account-offline-name") {
      e.preventDefault();
      const confirmBtn = document.getElementById("btn-confirm-offline-account");
      if (confirmBtn) confirmBtn.click();
      return;
    }
    if ((e.key === "Enter" || e.key === " ") && e.target.matches('[role="button"][data-select-inst], [role="button"][data-gallery-pick-inst]')) {
      e.preventDefault();
      e.target.click();
      return;
    }
    if (e.key === "Escape") {
      if (installDialog.open || microsoftDialog.open) return;
      if (accountMenuOpen) {
        closeAccountPopoverSmoothly();
        return;
      }
      const crashModal = document.getElementById("crash-window-modal");
      if (crashModal && crashModal.classList.contains("is-open")) {
        crashModal.classList.remove("is-open");
        crashModal.setAttribute("aria-hidden", "true");
        return;
      }
      closePortal();
      closeDrawer();
      if (currentRoute === "resource-detail" || currentRoute === "download-queue" || currentRoute === "instance-gallery") {
        goBack();
      } else if (activeHubKey !== null && currentRoute === "home") {
        collapseToTopWithDamping(() => {
          activeHubKey = null;
          activeSubRoute = null;
          resetListControls();
          syncHubOutletUi();
        });
      }
    }
  });

  /* ---------- Splash Curtain Auto Dismiss ---------- */
  const splash = document.getElementById("splash-curtain");
  function dismissSplash() {
    splash.classList.add("is-done");
  }
  splash.addEventListener("click", dismissSplash);
  setTimeout(dismissSplash, 720);

  if (typeof window.LauncherBridge.onHostEvent === "function") {
    window.LauncherBridge.onHostEvent((evt, data) => {
      if (evt === "download_updated" && currentRoute === "download-queue") {
        const listEl = document.getElementById("dl-queue-list");
        const st = window.LauncherBridge.getStateSync();
        if (listEl) {
          (st.downloadTasks || []).forEach((t) => {
            const row = listEl.querySelector(`[data-dl-row="${CSS.escape(t.id)}"]`);
            if (!row) return;
            const fill = row.querySelector(".dl-task-progress-fill");
            const pct = row.querySelector(".dl-progress-pct");
            const subEl = row.querySelector(".quick-mod-file");
            if (fill) {
              fill.style.width = `${t.progress}%`;
              fill.classList.toggle("is-done", t.status === "completed");
            }
            if (pct) pct.textContent = `${t.progress}%`;
            fill?.parentElement?.setAttribute("aria-valuenow", String(t.progress));
            if (subEl) {
              subEl.textContent = `${t.sub} · ${t.status === "completed" ? t.size : t.speed}`;
              subEl.title = t.status === "paused" ? t.speed : "";
            }
            if (row.dataset.dlStatus !== t.status) {
              const stateControl = row.querySelector("[data-toggle-dl], .dl-state-spacer");
              if (stateControl) {
                stateControl.outerHTML = t.status === "completed"
                  ? '<span class="dl-state-spacer" aria-hidden="true"></span>'
                  : window.LauncherViews.renderPauseResumeBtn(t.status === "downloading", t.id, t.name);
              }
              row.dataset.dlStatus = t.status;
            }
          });
          syncDownloadQueueEyebrow(st);
        }
      } else if (evt === "mirror_updated" && data) {
        const st = window.LauncherBridge.getStateSync();
        syncMirrorChipsUi(st);
      } else if (evt === "window_state" && data) {
        syncWindowMaximized(data.maximized);
      } else if (evt === "catalog_synced") {
        window.LauncherBridge.invoke("get_state", {}).then(() => {
          if (currentRoute !== "resource-detail" && (activeSubRoute || currentRoute) === "download-versions") {
            refreshDownloadResults("download-versions");
          }
        }).catch(() => {});
      } else if (evt === "workspace_refreshed") {
        if (currentRoute === "home" || currentRoute === "instance-gallery") render({ preserveSubLayout: true });
      } else if (evt === "launch_progress" && data) {
        const nextPct = Number(data.progress) || 0;
        const st = window.LauncherBridge.getStateSync();
        if (!data.instanceId || data.instanceId === st?.activeInstanceId) {
          launchProgress = nextPct;
          if (currentRoute === "home") syncLaunchMeterUi();
        }
        if (nextPct >= 100 && data.instanceId) {
          pendingLaunchIds.delete(data.instanceId);
          syncLaunchButtonUi(st);
        }
        if (nextPct >= 100) {
          setTimeout(() => {
            launchProgress = 0;
            if (currentRoute === "home") syncLaunchMeterUi();
          }, 1200);
        }
      } else if (evt === "game_process_started" && data) {
        if (data.instanceId) pendingLaunchIds.delete(data.instanceId);
        syncLaunchButtonUi(window.LauncherBridge.getStateSync());
      } else if (evt === "launch_cancelled" && data) {
        if (data.instanceId) pendingLaunchIds.delete(data.instanceId);
        launchProgress = 0;
        if (currentRoute === "home") syncLaunchMeterUi();
        syncLaunchButtonUi(window.LauncherBridge.getStateSync());
      } else if (evt === "crash_detected" && data) {
        if (data.instanceId) pendingLaunchIds.delete(data.instanceId);
        launchProgress = 0;
        if (currentRoute === "home") syncLaunchMeterUi();
        syncLaunchButtonUi(window.LauncherBridge.getStateSync());
        showToast(data.message || "游戏异常退出，请在【运行诊断】查看报错分析");
      } else if (evt === "toast" && data) {
        showToast(String(data));
      }
    });
  }

  /* ---------- Unified Uiverse massive-termite-69 Hover Tooltip Controller ---------- */
  function initGlobalTooltips() {
    let tipEl = document.getElementById("dcl-tooltip");
    if (!tipEl) {
      tipEl = document.createElement("div");
      tipEl.id = "dcl-tooltip";
      tipEl.className = "dcl-tooltip";
      tipEl.setAttribute("role", "tooltip");
      tipEl.setAttribute("aria-hidden", "true");
      tipEl.innerHTML = '<span class="dcl-tooltip-text"></span><span class="dcl-tooltip-arrow" aria-hidden="true"></span>';
      document.body.appendChild(tipEl);
    }
    const textEl = tipEl.querySelector(".dcl-tooltip-text");
    let activeTarget = null;
    let showTimer = null;

    const stripNativeTitle = (el) => {
      if (!el || el.nodeType !== 1) return "";
      const nativeTitle = el.getAttribute("title");
      if (nativeTitle != null) {
        if (nativeTitle.trim()) el.dataset.tooltip = nativeTitle.trim();
        else delete el.dataset.tooltip;
        el.removeAttribute("title");
      }
      return (el.dataset.tooltip || "").trim();
    };

    const hideTooltip = () => {
      clearTimeout(showTimer);
      showTimer = null;
      activeTarget = null;
      tipEl.classList.remove("is-visible");
      tipEl.setAttribute("aria-hidden", "true");
    };

    const positionAndShow = (el) => {
      if (!el || !el.isConnected) {
        hideTooltip();
        return;
      }
      const msg = stripNativeTitle(el);
      if (!msg) {
        hideTooltip();
        return;
      }
      textEl.textContent = msg;
      tipEl.classList.remove("is-bottom");
      const rect = el.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) {
        hideTooltip();
        return;
      }
      const tipW = tipEl.offsetWidth || 120;
      const tipH = tipEl.offsetHeight || 34;
      const gap = 12;
      const centerX = rect.left + rect.width / 2;
      let left = centerX - tipW / 2;
      const pad = 10;
      left = Math.max(pad, Math.min(window.innerWidth - tipW - pad, left));

      let top = rect.top - tipH - gap;
      let isBottom = false;
      if (top < 8) {
        top = rect.bottom + gap;
        isBottom = true;
      }
      const arrowX = Math.max(12, Math.min(tipW - 12, centerX - left));
      tipEl.style.setProperty("--tt-x", `${Math.round(left)}px`);
      tipEl.style.setProperty("--tt-y", `${Math.round(top)}px`);
      tipEl.style.setProperty("--tt-arrow-x", `${Math.round(arrowX)}px`);
      tipEl.classList.toggle("is-bottom", isBottom);
      tipEl.setAttribute("aria-hidden", "false");
      requestAnimationFrame(() => {
        if (activeTarget === el) tipEl.classList.add("is-visible");
      });
    };

    document.addEventListener("pointerover", (e) => {
      const candidate = e.target && e.target.closest ? e.target.closest("[title], [data-tooltip]") : null;
      if (!candidate) {
        if (activeTarget) hideTooltip();
        return;
      }
      const msg = stripNativeTitle(candidate);
      if (!msg) {
        if (activeTarget) hideTooltip();
        return;
      }
      if (activeTarget === candidate) return;
      clearTimeout(showTimer);
      activeTarget = candidate;
      showTimer = setTimeout(() => {
        if (activeTarget === candidate) positionAndShow(candidate);
      }, 3000);
    });

    document.addEventListener("pointerout", (e) => {
      if (!activeTarget) return;
      const rel = e.relatedTarget;
      if (rel && activeTarget.contains(rel)) return;
      hideTooltip();
    });

    document.addEventListener("pointerdown", hideTooltip, { passive: true });
    window.addEventListener("scroll", () => {
      if (tipEl.classList.contains("is-visible")) hideTooltip();
    }, { passive: true });
    window.addEventListener("blur", hideTooltip);
    window.addEventListener("keydown", (e) => {
      if (e.key === "Escape") hideTooltip();
    });
  }

  /* ---------- Mod Row Hover Marquee Controller ---------- */
  function initModMarquee() {
    let hoveredRow = null;
    const clearRowMarquee = (row) => {
      if (!row) return;
      row.querySelectorAll("[data-marquee-clip]").forEach((clip) => {
        clip.classList.remove("is-marquee-active");
        const track = clip.querySelector(".marquee-track");
        if (track) {
          track.style.removeProperty("--marquee-shift");
          track.style.removeProperty("--marquee-dur");
        }
      });
    };

    document.addEventListener("pointerover", (e) => {
      const row = e.target && e.target.closest ? e.target.closest(".data-row--modmanage") : null;
      if (row === hoveredRow) return;
      if (hoveredRow) clearRowMarquee(hoveredRow);
      hoveredRow = row;
      if (!row || prefersReducedMotion()) return;
      row.querySelectorAll("[data-marquee-clip]").forEach((clip) => {
        const track = clip.querySelector(".marquee-track");
        if (!track) return;
        clip.classList.add("is-measuring");
        const overflow = Math.ceil(track.scrollWidth - clip.clientWidth);
        clip.classList.remove("is-measuring");
        if (overflow > 3) {
          const shift = -(overflow + 14);
          const dur = Math.max(2.4, Math.min(12, overflow / 46 + 1.2)).toFixed(2);
          track.style.setProperty("--marquee-shift", `${shift}px`);
          track.style.setProperty("--marquee-dur", `${dur}s`);
          clip.classList.add("is-marquee-active");
        }
      });
    });

    document.addEventListener("pointerout", (e) => {
      if (!hoveredRow) return;
      const rel = e.relatedTarget;
      if (rel && hoveredRow.contains(rel)) return;
      clearRowMarquee(hoveredRow);
      hoveredRow = null;
    });
  }

  /* ---------- Theme-Adaptive SVG Background Pattern Cycler ---------- */
  const BG_PATTERN_PRESETS = [
    { id: "none", name: "无", url: "", size: "156px 156px" },
    { id: "festive", name: "冬日礼赞", url: "assets/patterns/festive.svg", size: "156px 156px" }
  ];
  let currentBgPatternIndex = 0;

  function syncBgPatternButtonUi(animateBtn) {
    const preset = BG_PATTERN_PRESETS[currentBgPatternIndex] || BG_PATTERN_PRESETS[0];
    const btn = document.getElementById("btn-cycle-bg-pattern");
    if (!btn) return;
    btn.dataset.bgPattern = preset.id;
    btn.removeAttribute("data-tooltip");
    btn.setAttribute("aria-label", `切换版本背景（当前：${preset.name}）`);
    btn.classList.toggle("is-pattern-on", preset.id !== "none");
    if (animateBtn && !prefersReducedMotion()) {
      btn.classList.remove("is-spinning");
      void btn.offsetWidth;
      btn.classList.add("is-spinning");
    }
  }

  function applyBgPattern(patternId, { persist = false, animateBtn = false } = {}) {
    const idx = BG_PATTERN_PRESETS.findIndex((p) => p.id === patternId);
    currentBgPatternIndex = idx >= 0 ? idx : 0;
    const preset = BG_PATTERN_PRESETS[currentBgPatternIndex];
    const layer = document.getElementById("bg-pattern-layer");
    if (layer) {
      if (preset.url) {
        let resolvedUrl = preset.url;
        try {
          resolvedUrl = new URL(preset.url, document.baseURI).href;
        } catch {}
        const cssUrl = `url("${resolvedUrl}")`;
        const cssSize = preset.size || "156px 156px";
        layer.style.setProperty("--bg-pattern-url", cssUrl);
        layer.style.setProperty("--bg-pattern-size", cssSize);
        layer.style.webkitMaskImage = cssUrl;
        layer.style.maskImage = cssUrl;
        layer.style.webkitMaskSize = cssSize;
        layer.style.maskSize = cssSize;
        if (!layer.classList.contains("is-active")) {
          void layer.offsetWidth;
          layer.classList.add("is-active");
        }
      } else {
        layer.classList.remove("is-active");
      }
    }
    syncBgPatternButtonUi(animateBtn);
    if (persist) {
      window.LauncherBridge.invoke("set_bg_pattern", { bgPatternId: preset.id }).catch(() => {});
    }
  }

  function cycleBgPattern() {
    const next = BG_PATTERN_PRESETS[(currentBgPatternIndex + 1) % BG_PATTERN_PRESETS.length];
    applyBgPattern(next.id, { persist: true, animateBtn: true });
  }

  function syncPinButtonUi(pinned) {
    const btn = document.getElementById("btn-toggle-pin");
    if (!btn) return;
    const isPinned = Boolean(pinned);
    btn.classList.toggle("is-active", isPinned);
    btn.setAttribute("aria-pressed", String(isPinned));
    btn.setAttribute("aria-label", isPinned ? "取消窗口置顶" : "窗口置顶");
  }

  /* ---------- Launcher-Themed Custom Color Picker Controller ---------- */
  function initDclColorPicker() {
    const pop = document.getElementById("dcl-color-picker");
    const svEl = document.getElementById("dcl-cp-sv");
    const svThumb = document.getElementById("dcl-cp-sv-thumb");
    const hueEl = document.getElementById("dcl-cp-hue");
    const hueThumb = document.getElementById("dcl-cp-hue-thumb");
    const previewEl = document.getElementById("dcl-cp-preview");
    const hexInput = document.getElementById("dcl-cp-hex");
    const rInput = document.getElementById("dcl-cp-r");
    const gInput = document.getElementById("dcl-cp-g");
    const bInput = document.getElementById("dcl-cp-b");
    if (!pop || !svEl || !hueEl) return;

    let cpH = 28, cpS = 0.85, cpV = 0.9;
    let cpOpen = false;
    let cpCommitTimer = null;
    let cpCloseTimer = null;

    const clamp = (n, min, max) => Math.max(min, Math.min(max, n));
    function hsvToRgb(h, s, v) {
      const c = v * s;
      const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
      const m = v - c;
      let r = 0, g = 0, b = 0;
      if (h < 60) [r, g, b] = [c, x, 0];
      else if (h < 120) [r, g, b] = [x, c, 0];
      else if (h < 180) [r, g, b] = [0, c, x];
      else if (h < 240) [r, g, b] = [0, x, c];
      else if (h < 300) [r, g, b] = [x, 0, c];
      else [r, g, b] = [c, 0, x];
      return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
    }
    function rgbToHsv(r, g, b) {
      const rn = r / 255, gn = g / 255, bn = b / 255;
      const max = Math.max(rn, gn, bn), min = Math.min(rn, gn, bn);
      const d = max - min;
      let h = 0;
      if (d !== 0) {
        if (max === rn) h = 60 * (((gn - bn) / d) % 6);
        else if (max === gn) h = 60 * ((bn - rn) / d + 2);
        else h = 60 * ((rn - gn) / d + 4);
      }
      if (h < 0) h += 360;
      const s = max === 0 ? 0 : d / max;
      return [h, s, max];
    }
    function rgbToHex(r, g, b) {
      return "#" + [r, g, b].map((x) => clamp(Math.round(x), 0, 255).toString(16).padStart(2, "0")).join("");
    }
    function parseHex(str) {
      const m = String(str || "").trim().match(/^#?([0-9a-f]{6})$/i);
      if (!m) return null;
      const n = parseInt(m[1], 16);
      return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
    }

    function syncPickerVisuals() {
      const [r, g, b] = hsvToRgb(cpH, cpS, cpV);
      const hex = rgbToHex(r, g, b);
      svEl.style.backgroundColor = `hsl(${Math.round(cpH)}, 100%, 50%)`;
      svThumb.style.left = `${(cpS * 100).toFixed(2)}%`;
      svThumb.style.top = `${((1 - cpV) * 100).toFixed(2)}%`;
      hueThumb.style.left = `${((cpH / 360) * 100).toFixed(2)}%`;
      hueThumb.style.background = `hsl(${Math.round(cpH)}, 100%, 50%)`;
      previewEl.style.background = hex;
      if (document.activeElement !== hexInput) hexInput.value = hex.toUpperCase();
      if (document.activeElement !== rInput) rInput.value = String(r);
      if (document.activeElement !== gInput) gInput.value = String(g);
      if (document.activeElement !== bInput) bInput.value = String(b);
      return hex;
    }

    function applyColorToActiveInstance(hex, immediate) {
      const btn = document.getElementById("btn-open-custom-color");
      if (btn) btn.dataset.customHex = hex;
      const send = async () => {
        const st = window.LauncherBridge.getStateSync();
        if (!st?.activeInstanceId) return;
        await window.LauncherBridge.invoke("set_instance_color", {
          id: st.activeInstanceId,
          colorId: "custom",
          customHex: hex
        });
        syncInstanceColorUi(window.LauncherBridge.getStateSync(), false);
      };
      clearTimeout(cpCommitTimer);
      if (immediate) send();
      else cpCommitTimer = setTimeout(send, 45);
    }

    function setFromHex(hex, commit) {
      const rgb = parseHex(hex);
      if (!rgb) return;
      [cpH, cpS, cpV] = rgbToHsv(rgb[0], rgb[1], rgb[2]);
      const outHex = syncPickerVisuals();
      if (commit) applyColorToActiveInstance(outHex, true);
    }

    function openPicker(anchorBtn) {
      clearTimeout(cpCloseTimer);
      const initialHex = anchorBtn?.dataset?.customHex || "#e67e22";
      setFromHex(initialHex, false);
      pop.hidden = false;
      const rect = anchorBtn.getBoundingClientRect();
      const popW = 264;
      const popH = pop.offsetHeight || 296;
      const pad = 12;
      let left = rect.left;
      if (left + popW > window.innerWidth - pad) left = Math.max(pad, window.innerWidth - popW - pad);
      let top = rect.bottom + 8;
      if (top + popH > window.innerHeight - pad) top = Math.max(pad, rect.top - popH - 8);
      pop.style.left = `${Math.round(left)}px`;
      pop.style.top = `${Math.round(top)}px`;
      anchorBtn.classList.add("is-active");
      anchorBtn.setAttribute("aria-expanded", "true");
      cpOpen = true;
      void pop.offsetWidth;
      pop.classList.add("is-open");
    }

    function closePicker() {
      if (!cpOpen) return;
      cpOpen = false;
      pop.classList.remove("is-open");
      const openBtn = document.getElementById("btn-open-custom-color");
      if (openBtn) {
        openBtn.classList.remove("is-active");
        openBtn.setAttribute("aria-expanded", "false");
      }
      clearTimeout(cpCloseTimer);
      cpCloseTimer = setTimeout(() => {
        if (!cpOpen) pop.hidden = true;
      }, 190);
    }

    function bindDrag(el, onMove) {
      let dragging = false;
      const handle = (ev, immediate) => {
        const rect = el.getBoundingClientRect();
        if (!rect.width || !rect.height) return;
        const x = clamp((ev.clientX - rect.left) / rect.width, 0, 1);
        const y = clamp((ev.clientY - rect.top) / rect.height, 0, 1);
        onMove(x, y, immediate);
      };
      el.addEventListener("pointerdown", (ev) => {
        ev.preventDefault();
        dragging = true;
        el.setPointerCapture?.(ev.pointerId);
        handle(ev, false);
      });
      el.addEventListener("pointermove", (ev) => {
        if (!dragging) return;
        handle(ev, false);
      });
      const stop = (ev) => {
        if (!dragging) return;
        dragging = false;
        handle(ev, true);
      };
      el.addEventListener("pointerup", stop);
      el.addEventListener("pointercancel", stop);
    }

    bindDrag(svEl, (x, y, immediate) => {
      cpS = x;
      cpV = 1 - y;
      const hex = syncPickerVisuals();
      applyColorToActiveInstance(hex, immediate);
    });

    bindDrag(hueEl, (x, _y, immediate) => {
      cpH = x * 360;
      const hex = syncPickerVisuals();
      applyColorToActiveInstance(hex, immediate);
    });

    hexInput.addEventListener("input", () => {
      const rgb = parseHex(hexInput.value);
      if (!rgb) return;
      [cpH, cpS, cpV] = rgbToHsv(rgb[0], rgb[1], rgb[2]);
      const hex = syncPickerVisuals();
      applyColorToActiveInstance(hex, true);
    });

    const onRgbInput = () => {
      const r = clamp(Number(rInput.value) || 0, 0, 255);
      const g = clamp(Number(gInput.value) || 0, 0, 255);
      const b = clamp(Number(bInput.value) || 0, 0, 255);
      [cpH, cpS, cpV] = rgbToHsv(r, g, b);
      const hex = syncPickerVisuals();
      applyColorToActiveInstance(hex, true);
    };
    rInput.addEventListener("input", onRgbInput);
    gInput.addEventListener("input", onRgbInput);
    bInput.addEventListener("input", onRgbInput);

    pop.addEventListener("click", (ev) => {
      const sw = ev.target.closest("[data-cp-swatch]");
      if (sw) setFromHex(sw.dataset.cpSwatch, true);
    });

    document.addEventListener("click", (ev) => {
      const openBtn = ev.target.closest("#btn-open-custom-color");
      if (openBtn) {
        ev.stopPropagation();
        if (cpOpen) closePicker();
        else openPicker(openBtn);
        return;
      }
      if (cpOpen && !ev.target.closest("#dcl-color-picker")) {
        closePicker();
      }
    });

    document.addEventListener("keydown", (ev) => {
      if (ev.key === "Escape" && cpOpen) closePicker();
    });

    window.addEventListener("resize", () => { if (cpOpen) closePicker(); });
  }

  initGlobalTooltips();
  initModMarquee();
  initDclColorPicker();

  document.addEventListener("click", (e) => {
    if (e.target.closest("#btn-cycle-bg-pattern")) {
      cycleBgPattern();
      return;
    }
    const pinBtn = e.target.closest("#btn-toggle-pin");
    if (pinBtn) {
      const nextPinned = !pinBtn.classList.contains("is-active");
      syncPinButtonUi(nextPinned);
      window.LauncherBridge.invoke("window_set_topmost", { pinned: nextPinned })
        .then((res) => {
          if (res && typeof res.alwaysOnTop === "boolean") syncPinButtonUi(res.alwaysOnTop);
        })
        .catch(() => {});
    }
  });

  window.LauncherBridge.ready().then(() => {
    const st = window.LauncherBridge.getStateSync();
    if (st.themeId) {
      const tIdx = THEME_PRESETS.findIndex((t) => t.id === st.themeId);
      if (tIdx >= 0) {
        currentThemeIndex = tIdx;
        document.documentElement.setAttribute("data-theme", st.themeId);
        syncPaletteButtonUi(THEME_PRESETS[tIdx]);
      }
    }
    syncPinButtonUi(Boolean(st.alwaysOnTop));
    applyBgPattern(st.bgPatternId || "none", { persist: false, animateBtn: false });
    render();
  });
})();
