# DummyCrisp 前端视觉与交互设计大纲

> 本大纲融合 **Editorial 社论式线条/色块排版**（参考 `obamaoralhistory.columbia.edu`）与 **3D 视差空间面板**（参考 Uiverse `tricky-lionfish-78`），以 **黄黑高对比工业美学** 作为首发展示配色，并支持通过 CSS 变量无缝扩展多套主题。后续前端开发将按“独立 HTML 模块原型 → 串联组装 → 接入 `bridge.js` 打包”的节奏逐步落地。

---

## 1. 整体视觉语言与核心特征

### 1.1 布局骨架（参考 `obamaoralhistory.columbia.edu`）
1. **秩序感极强的线条系统（Hairlines & Glyphs）**
   - **1px 实线分割**：贯穿视口的细线（Hairline）锚定各个功能区块的顶部标题与页眉/页脚边界。
   - **细密点线分隔（Dotted Rules）**：`1px dotted` 分隔主标题与副描述、版本元数据与操作区。
   - **竖线管道分隔符（Pipe `|`）**：用于版本号、加载器类型、Java 版本等元信息内联排布（如 `1.21.1 | NeoForge | Java 21`）。
   - **标志性括号图腾 `{ }`**：用于主品牌标识、版本时间线包裹边界及大型区块过渡装饰。
2. **零圆角板块化色块切换（Modular Color Blocks）**
   - 主页由纵向呼吸滚动的**锐利矩形板块**组成（`border-radius: 0` 用于大色块边界，与卡片局部圆角形成张力）：
     - **黑曜主画布（Obsidian Canvas）** ↔ **高饱和电光黄全幅色块（Signal Yellow Block）** 交替推进，文字与线条反色自适应。
   - 在大色块右侧边缘嵌入 **90° 旋转竖排衬线文字**（如 `DUMMYCRISP // LAUNCHER`），强化先锋编辑感。
3. **字体对比体系（Typography Contrast）**
   - **展示衬线体（Editorial Display Serif）**：用于主标语、区块大标题、引文式版本说明（营造克制、高级的艺术感）。
   - **超粗无衬线体（Heavy Grotesque Sans）**：全大写/极粗字重，用于功能导航编号、超大数字统计（如当前实例数 `04`、已装 Mod 数 `128`）、按钮主标签。
   - **等宽/几何数据体（Monospace / UI Sans）**：用于路径、Hash、内存数值（`4096 MB`）、下载速度与日志流。
4. **呼吸感交互与微细节**
   - 滚动与切换带有平滑缓动（`cubic-bezier(0.22, 1, 0.36, 1)`），板块交界与卡片展开具备舒缓的呼吸节奏；尊重 `prefers-reduced-motion`。
   - 悬停反馈：导航项悬停时字间距微扩并伴随箭头位移；实例/任务进度条采用 **条形声波/条码式刻度（Barcode Scrubber）+ 红色/亮色游标** 呈现下载或启动进度。
   - 右上角提供极简三道细线菜单按钮，可呼出全屏编号索引抽屉（`01. 启动总览` ~ `06. 系统诊断`），右上角配细线弧角框。

---

## 2. 3D 空间二级面板交互（参考 `tricky-lionfish-78`）

### 2.1 触发逻辑（主页面选项 → 3D 子菜单面板 → 目标功能页）
当用户在主页面点击一级模块入口（例如 **【下载 / DOWNLOAD】**、**【实例管理 / MANAGE】**、**【环境与参数 / RUNTIME】**）时，不直接生硬跳转，而是呼出一个居中的 **3D 空间卡片矩阵面板**：
- 例：点击主页面的 **【下载】** → 展开 3D 面板，包含多张悬浮 3D 卡片：
  - `01 / VERSIONS` **游戏版本**（原版与加载器安装）
  - `02 / MODS` **模组中心**（Modrinth / CurseForge）
  - `03 / PACKS` **资源包**（材质与音效包）
  - `04 / SHADERS` **光影包**（Iris / OptiFine 光影）
  - `05 / MODPACKS` **整合包**（MRPACK / CurseForge ZIP）
- 点击任意 3D 卡片后，面板收敛并切换至对应的子页面，左上角保留面朝主画布的返回索引。

### 2.2 3D 卡片结构与物理视差规范
每张 3D 菜单卡片严格遵循多平面 Z 轴分层（在黄黑配色下重构材质）：
- **透视容器（`.card-stage`）**：`perspective: 1000px`
- **底板（`.card-3d`）**：`transform-style: preserve-3d`，带斜向交叉网格纹理（CSS 双重 `repeating-linear-gradient` + `linear-gradient`）；`hover` 时卡片绕对角轴旋转 `transform: rotate3d(0.5, 1, 0, 24deg)`（桌面端也可叠加轻微鼠标跟随倾角），同时底板网格纹理同步平移产生视差滑移感。
- **前景半透色块（`.content-box`）**：半透明工业黄/暗金玻璃层（支持随主题变量切换）。
- **四层 Z 轴悬浮高度（True Parallax Depth）**：
  1. 底板网格平面：`Z = 0px`
  2. 底部交互按钮（如 `ENTER →`）：`translate3d(0, 0, 20px)`，悬停跃升至 `60px`
  3. 功能说明正文：`translate3d(0, 0, 30px)`，悬停跃升至 `60px`
  4. 卡片大标题（如 `MODS`）：`translate3d(0, 0, 50px)`，悬停跃升至 `60px`
  5. 右上角角标方块（编号/数量徽章，如 `02` / `CF+MR`）：`translate3d(0, 0, 80px)`（最高悬浮层 + 独立投影）

---

## 3. 配色方案架构（多主题引擎 + 首发黄黑方案）

所有颜色均通过 `[data-theme="..."]` 挂载到 `:root` CSS 变量，后续新增配色只需追加一组 Token，无需改动组件代码。

### 3.1 首发展示主题：`obsidian-yellow`（曜石黑 × 电光黄）
| 语义 Token | 色值 | 用途 |
| :--- | :--- | :--- |
| `--bg-primary` | `#0A0A0C` | 主画布黑曜底色 |
| `--bg-elevated` | `#141418` | 卡片底板、悬浮浮层、输入框底色 |
| `--bg-accent-block` | `#F5C518` | 全幅模块化强调色块（电光工业黄） |
| `--accent-primary` | `#FFD21F` | 主操作按钮、高亮边框、3D 面板主视觉色 |
| `--accent-translucent` | `rgba(245, 197, 24, 0.82)` | 3D 卡片内层半透明内容板（`.content-box`） |
| `--accent-marker` | `#FF3B30` | 条码进度游标、报错/警告状态点 |
| `--text-on-dark` | `#F4F3EF` | 黑底主标题与正文（暖白，避免刺眼纯白） |
| `--text-muted-dark` | `#8E8E93` | 黑底元信息、版本号、次要说明 |
| `--text-on-accent` | `#0A0A0C` | 黄底色块与 3D 面板内的深黑文字（高对比度） |
| `--line-solid` | `rgba(245, 197, 24, 0.28)` | 1px 实线分割线 |
| `--line-dotted` | `rgba(244, 243, 239, 0.22)` | 细密点线分隔符 |

### 3.2 预留扩展主题位（后续接入）
- `editorial-cream`：米白纸张底（`#F4F3EF`）+ 深海藏青色块（`#0C3B52`），致敬原参考站经典馆藏感。
- `cyber-cyan`：极夜黑底 + 透亮青蓝（复刻 `tricky-lionfish-78` 原生霓虹青色系）。
- `monolith-ash`：极简灰白建筑感高冷单色系。

---

## 4. 模块化 HTML 原型拆解与组装路线

为了在浏览器中快速预览、逐个打磨手感，再整体打包为 Tauri 桌面应用，前端拆分为以下独立可调试模块：

```text
D:\MC\DummyCrisp\ui-prototype\
├── index.html                  # 主入口外壳与路由视图调度
├── styles\
│   ├── tokens.css              # 主题变量（首发黄黑 + 主题切换器）
│   ├── layout.css              # 线条网格、全幅色块、竖排文字、抽屉导航
│   └── card-3d.css             # 3D 空间卡片与 Z 轴视差动效
├── scripts\
│   ├── bridge.js               # 双端通信层（浏览器 Mock <-> Tauri invoke）
│   └── router.js               # 视图切换与 3D 二级面板状态机
└── modules\                    # 按开发阶段逐个搭建的独立页面模块
    ├── m1-home-hub.html        # 模块 1：主页面（板块化布局、启动区、一级入口、全屏菜单）
    ├── m2-3d-portal.html       # 模块 2：点击一级入口弹出的 3D 卡片矩阵面板演示
    ├── m3-auth-java.html       # 模块 3：微软正版登录（Device Code）与 Java 环境管理
    ├── m4-download-hub.html    # 模块 4：版本 / Mod / 资源包 / 光影 下载中心与镜像切换
    ├── m5-instance-mgr.html    # 模块 5：版本隔离管理与 Mod/材质/光影 可视化开关列表
    └── m6-runtime-diag.html    # 模块 6：内存动态分配、JVM 参数与崩溃报错分析面板
```

### 前端制作与审查原则（Skill 协作策略）
- **视觉与动效实现（`web-design-engineer`）**：严格先定义 Token 与布局骨架，保留手写精致 CSS 动效、细线节奏与 3D `preserve-3d` 分层，避免模板化 AI 渐变与冗余装饰；每个模块完成后先在浏览器验证交互与呼吸感。
- **体验与可用性校验（`ui-ux-pro-max`）**：确保黄黑高对比度下的文本可读性（对比度 ≥ 4.5:1）、键盘焦点可达、动效时长克制不晕眩、窗口缩放响应式适配（适配桌面启动器常见尺寸 `1080×720` 至全屏）。
