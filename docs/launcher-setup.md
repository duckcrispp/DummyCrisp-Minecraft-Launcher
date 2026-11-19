# DummyCrisp 认证与版本隔离

## Microsoft 正版登录

1. 在 [Microsoft Entra 管理中心](https://entra.microsoft.com/) 的应用注册中创建自己的应用。选择支持个人 Microsoft 帐户的帐户类型，记下「应用程序（客户端）ID」。
2. 在该应用的「身份验证」设置中启用「允许公共客户端流」。DummyCrisp 使用设备授权流和 `/consumers` 端点，桌面端只需 Client ID。 [Microsoft 桌面应用注册说明](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-registration) · [设备授权流](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
3. 用同一 Client ID 提交 [Minecraft Java 游戏服务 API 审核](https://aka.ms/mce-reviewappid)。准备启动器说明、项目主页和可供审核的版本。等待 Mojang 批准后再用它接入 Minecraft 服务。若 Microsoft 和 Xbox 授权成功，但 Minecraft 服务返回 HTTP 403，先核对审批与 Client ID 是否一致。
4. 关闭启动器。在启动器 EXE 同级的 `DCL/config/settings.json` 中填入 `"microsoftClientId": "自己的应用程序 ID"`，或为启动器进程设置环境变量 `DCL_MICROSOFT_CLIENT_ID`。环境变量优先。
5. 重启，进入「运行诊断 → 账号与 Java → 微软正版登录」。复制设备码，打开微软登录页并完成授权。测试账号需要拥有 Java 版游戏权益，并已在官方启动器中建立 Java 玩家资料。

后端按服务器间隔轮询 Microsoft，随后换取 Xbox Live、XSTS、Minecraft 会话和 Java 玩家资料。关闭登录弹窗可取消。会话凭据使用 Windows 当前用户的 DPAPI 保存在 `DCL/config/accounts.dat`，启动游戏时按到期时间刷新。

## CurseForge

1. 从 [CurseForge 官方 API 文档](https://docs.curseforge.com/rest-api/) 的申请入口申请第三方应用访问权限，随后在开发者控制台生成 API Key。
2. 推荐为启动器进程设置 `DCL_CURSEFORGE_API_KEY`。本地开发也可以在 EXE 同级的 `DCL/config/settings.json` 设置 `curseForgeApiKey`。后者是本地明文配置，发布前从分发文件中移除。
3. 重启后进入任意资源分类，在筛选的「来源」里选 CurseForge。后端向官方 API 发送 `x-api-key`，按项目分类请求搜索及文件列表。若作者关闭第三方分发或文件没有公开下载地址，访问项目主页手动下载。
4. 面向用户公开发布时，使用用户自己的 Key，或按 CurseForge 审批条款通过你控制的服务端代理请求。客户端发行包里的共享 Key 可以被提取。HTTP 401/403 时核对密钥和权限。

## 版本目录

- 每个版本实例使用 `.minecraft/versions/<实例 ID>/` 作为游戏目录。选中模组、材质包或光影包下载时，启动器会列出兼容的已安装实例。三个分类依次存入目标实例的 `mods`、`resourcepacks`、`shaderpacks`。
- 既有的 `1.21.1/1.21.1.fabric.json` 会被识别为 Fabric 配置。新安装的 Fabric 版本使用包含加载器版本的实例 ID，因此可与同一 Minecraft 版本的原版共存。
- 旧的共享目录文件保留在原位置。若其中有要继续使用的存档、配置或资源，先关闭游戏，用启动器的「版本文件夹」打开目标实例，再从共享目录复制相应的 `saves`、`options.txt`、`config`、`mods` 或其他所需文件。对于模组，先确认游戏版本、加载器和前置依赖匹配。材质包和光影包按游戏内设置启用，光影包还需要该实例具有可用的光影加载器。
- 游戏版本页支持原版和 Fabric。单个资源下载按用户选定的项目及构建安装，前置模组可通过同一目标实例单独下载。

## 下载源与运行日志

- 「最优」按文件类型使用国内镜像。BMCLAPI 提供 Minecraft 游戏文件与资源，MCIM 提供带平台校验值的 Modrinth 和 CurseForge 文件。手动选源时使用对应镜像；镜像的文件缺失、校验失败或连接停滞时，可回退到原下载地址。[BMCLAPI 接入文档](https://bmclapidoc.bangbang93.com/) · [MCIM 接入文档](https://docs.mcimirror.top/)
- 下载任务创建时记录当前源设置。任务中的网络重试会保留仍可校验的进度，服务器支持 HTTP Range 时可续传。
- 每次启动器运行都会在 EXE 同级的 `DCL/logs` 生成 `launcher-日期时间-进程号.log`，可从「运行诊断 → 打开日志目录」查看启动器异常、下载重试和游戏进程输出。

## 安装整合包

1. 进入「下载资源 → 整合包」，打开项目详情并选择客户端构建。下载列表会显示获取清单、下载依赖和安装配置的进度。
2. 安装完成后，在版本实例列表选择新实例启动。每个构建拥有自己的模组、配置、资源包、光影包和存档目录。
3. 整合包支持原版、Fabric、Quilt、NeoForge 和 Minecraft 1.13 及更新版本的 Forge。Forge／NeoForge 的官方安装器和整合包启动都需要相应的 Java。在「实例环境」安装所需版本后，可从下载列表重试。
4. 默认安装客户端必需文件，并应用整合包附带的覆盖配置。若 CurseForge 未提供必需文件的下载地址，下载列表会标出相关文件，可改用 CurseForge 客户端安装。[Modrinth 整合包格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack) · [CurseForge API](https://docs.curseforge.com/rest-api/#get-files)

## 中文项目名来源

项目中文名来自各项目作者发布的简体中文语言文件，或作者在资源页使用的中文标题。随启动器附带的索引保存了项目标识、英文名、拼音和来源。 [内置译名索引](../ui-prototype/assets/mod-names-zh-cn.json)

- 下载目录支持汉字、全拼、拼音首字母和英文。例如 `机械动力`、`jixiedongli`、`jxdl` 和 `Create`。拼音可带空格、声调符号或数字。部分拼音也能匹配已收录的名字。
- 未收录的关键词由所选平台按原词查询。本地 Mod 管理也会读取 JAR 内声明的中文名，已收录的名称可用拼音检索。
- 可按内置索引的格式，将有上游出处的补充条目放入 EXE 同级的 `DCL/config/resource-names-zh-cn.json`，重启后生效。每个条目填写 `name`、`search`（英文项目名）、`pinyin`（音节以空格分隔）、`ids`（平台的项目 slug）和 `source`（上游 HTTPS 链接）。可选 `category` 为 `mods`、`packs`、`shaders` 或 `modpacks`，缺省为 `mods`。

