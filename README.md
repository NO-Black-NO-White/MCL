# MCL 0.9.2

粉白配色的 Minecraft Java 版启动器，使用 C#、WPF 和 .NET 9 开发。

MCL 是非官方项目，未获 Mojang 或 Microsoft 批准，也与两者无关联。

## 直接运行

下载 Windows x64 发布包，解压后双击 `MCL.exe`。需要 64 位 Windows 和 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)。从源码构建时，程序位于 `outputs/MCL-0.9.2/MCL.exe`。

## 下载一个游戏版本

1. 点击侧栏“下载游戏”，或首页“下载新版本”。
2. 确认顶部的游戏安装位置，可点击“更改位置”选择一个空文件夹或已有的 `.minecraft`。
3. 搜索并选择版本，查看所需 Java 版本和参考内容大小。
4. 点击“下载并安装”，查看进度、速度和已完成文件数量；需要时可取消。
5. 安装完成后，版本会加入“游戏版本”列表并设为当前启动项，返回首页即可启动。

原版下载使用官方源，提供 1.13 及以后的原版正式版。Java 运行环境仍需在“启动设置”里检测或选择，版本需要的 Java 高于已选版本时会显示提示。

下载内容包括客户端、适用于 Windows 64 位的依赖与原生库、资源索引、声音和语言等资源，以及日志配置。按官方清单核对文件大小和 SHA-1。已校验的本地文件会复用，下载失败会自动重试；取消后重新安装会跳过已完成文件，不提供单个文件内部的断点续传。

只有完整安装后才写入正式版本配置；未完成的安装不会显示为可启动版本。遇到已有同名版本且配置与官方不同，会停止以保护原配置，请选其他游戏目录安装。

## 导入整合包

选择侧栏“导入整合包”。把 Modrinth 的 `.mrpack` 或 CurseForge 客户端 ZIP 拖入页面，也可以点击“选择整合包文件”。核对包名、Minecraft 版本、加载器和安装位置后，点击“安装整合包”。清单里标为可选的客户端文件可以勾选一并安装。

Modrinth 支持原版和 Fabric。CurseForge ZIP 支持原版、Fabric 和 Minecraft 1.20.1 的 Forge 47.x，Forge 安装需要 64 位 Java 17 或更高版本。MCL 优先通过 Curse.Tools 查询文件并下载，失败时尝试 [MCIM](https://www.mcimirror.top/guide/platform/curseforge)，再通过 [Modrinth](https://docs.modrinth.com/api/operations/versionsfromhashes/) 匹配内容相同的文件。安装会校验文件大小和 SHA-1，无需配置 CurseForge API 密钥。如果都无法下载，可以从整合包作者或 CurseForge 取得对应版本的文件，放进同一个文件夹，然后在导入页点「选择补充文件夹」并重试。也可以选择已安装整合包的游戏目录，MCL 会在它的 `mods`、`resourcepacks` 和 `shaderpacks` 内查找对应文件。

MCL 会安装或复用校验通过的原版文件，按清单安装所需的加载器，下载并核对模组文件，然后应用客户端配置。安装完成后，整合包会加入“游戏版本”并成为当前选择，回首页即可启动。`versions/pack-…/game` 保存该整合包的模组、配置和存档。如果已有同名版本的配置与官方不同，MCL 会保留该版本，并缓存一份整合包使用的官方版本文件。如果整合包使用版本列表中的基础版本，MCL 会阻止误删该基础版本。

整合包所需的 Minecraft 版本须能通过当前原版安装器安装。遇到 NeoForge、Quilt 或其他暂未支持的组合时，MCL 会显示具体原因。取消或失败时，已经完成的基础游戏文件可复用，整合包实例在全部安装成功后才出现。

## 已有游戏与本地档案

可以在首页选择包含 `versions`、`libraries` 和 `assets` 的游戏文件夹。误选 `versions` 子目录时，MCL 会识别对应根目录。

在“启动设置”里选择 Java 和内存，然后点击右下角“保存设置”。本地档案使用自己填写的游戏名，适用于单人游戏及允许本地玩家的联机环境。

## 编辑本地角色

打开侧栏的“本地角色”，填写 1–16 位英文字母、数字或下划线的玩家名，可选皮肤（64×64 或 64×32 PNG）和披风（64×32 PNG）。选择图片后会显示预览。点击“保存本地角色”，返回首页启动。改名可能让部分存档把你识别为新玩家，从而读取不同的背包或进度。

外观使用 [CustomSkinLoader](https://modrinth.com/mod/customskinloader) 的本地文件模式。首次在对应版本使用时，MCL 会下载并校验所需的外观组件。兼容 Java 版 1.14–1.21.11 与 26.1–26.3 的正式原版，以及相同范围内由 MCL 导入的 Fabric 整合包。原版使用单独的模组目录，游戏仍从原有存档目录启动。整合包已有单独的 CustomSkinLoader 配置时，可以关闭“启动时应用外观”以按整合包原配置启动。

皮肤和披风显示在本机客户端。取消勾选“启动时应用外观”可以暂时关掉它，也可以用图片旁的“清除”恢复默认。游戏内如隐藏了披风，可在 Minecraft 的“皮肤自定义”里打开披风显示。

“本地角色 → 从正版账号导入”可读取你自己的 Minecraft Java 版角色名、UUID、当前皮肤和正在佩戴的披风。填写取得 Minecraft 接口访问许可的应用 ID 后，在微软网页登录。可以单独将昵称、皮肤或披风导入本地角色，再点击“保存本地角色”。启动游戏和好友联机使用本地角色，登录记录以当前 Windows 用户加密保存，可用“清除登录”移除。

## 好友联机

好友联机支持 Minecraft Java 1.20 及以上的原版和由 MCL 导入的整合包。双方需要安装相同的 Minecraft 版本和模组，使用不同的玩家身份。

1. 房主选好游戏版本，在“好友联机 → 创建房间”点“启动并创建房间”。MCL 会使用正在运行的游戏，或启动所选版本。没配置 Java 时会尝试从本机检测。
2. 房主在游戏里进入单人存档，按 Esc 选择“对局域网开放”并启动局域网世界。MCL 识别端口后会启动内置中转，首次使用时从发布包提取并校验 `cloudflared`，随后建立公网隧道并生成邀请码。组件准备无需联网或管理员权限。
3. 好友在“好友联机 → 加入房间”粘贴邀请码，点“加入并启动”。MCL 会匹配已安装的游戏版本。进入 Minecraft 后打开“多人游戏”，在局域网列表中选择“MCL 好友房间”。使用整合包时，双方应先安装同一整合包及对应模组。
4. 房主在联机期间保持存档和 MCL 运行。结束联机、退出游戏或关闭 MCL，会关闭这个房间的临时隧道。下次开房需要新的邀请码。

自动开房使用 [Cloudflare Quick Tunnel](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)，适合临时联测。公网连接取决于房主和好友都能访问 Cloudflare。手动部署的共享中转和旧邀请码仍可通过“手动中转设置”使用。

开发时可用以下命令启动本机中转，再把“手动中转设置”填为 `http://127.0.0.1:28470`，勾选“创建房间时使用这个中转地址”。

```powershell
dotnet run --project src/MCL.Relay/MCL.Relay.csproj
```

共享中转的公网地址使用 HTTPS。服务使用 `/mcl/v1/` 下的 HTTP 和 WebSocket 连接，反向代理要保留 WebSocket 升级。可在 `MCL.exe` 同目录放一个 `relay-url.txt` 来预填手动中转的地址。

本机中转运行时，可用以下命令验证邀请码、两路并发转发和关闭房间。

```powershell
dotnet run --project tests/MultiplayerValidation/MultiplayerValidation.csproj -- http://127.0.0.1:28470
```

## 管理和删除已有版本

在“游戏版本”里选中一个版本，下面会显示“版本管理”。点击“打开文件夹”可查看该版本目录；点击“删除版本”会先显示版本名称、具体路径和独立数据提示，默认操作是取消。

确认后，只将选中的 `versions/<版本名>` 文件夹移入 Windows 回收站。该文件夹中的独立存档、模组和设置也会一起移动；共享资源和游戏根目录的存档不在删除范围。可在 Windows 回收站恢复，恢复后点击“刷新”重新识别。

游戏运行中会禁用删除。存在继承或 JAR 引用的依赖版本、文件被占用、路径包含链接、路径过长或无法安全使用回收站时，会停止并显示原因。删除成功后刷新列表，选择剩余版本并保存；没有剩余版本时清空当前选择。
## 开发和验证

开发环境为 VS2022 的“.NET 桌面开发”工作负载和 .NET SDK 9.0.319。打开 `MCL.sln`，按 F5 运行。

```powershell
dotnet build MCL.sln -c Debug
```

正式版内置一个自带 .NET 10 运行时的中转程序。发布时为脚本提供能运行 .NET 10 SDK 的 `dotnet.exe`。已并行安装 SDK 9 和 10 时，可以省略 `-RelayDotnet`。

```powershell
.\scripts\Publish-MCL.ps1 -RelayDotnet 'C:\path\to\dotnet.exe'
```

发布脚本会校验并打包 Cloudflare 官方版 `cloudflared`。可以用 `-CloudflaredPath` 指定本机的官方 Windows x64 可执行文件。软件发布包请一并包含 `licenses` 目录。

隔离验证入口：

```powershell
dotnet run --project tests/ModpackValidation/ModpackValidation.csproj -c Release
dotnet run --project work/download-validation/DownloadValidation.csproj -c Release
dotnet run --project work/validation/Validation.csproj -c Release
```

下载回归覆盖首次安装、完整文件复用、损坏文件修复、校验失败重试、取消后恢复、保护已有版本及路径边界。已从空目录实际下载并验证官方 1.20.1 的 3641 个文件，随后验证重复安装全部复用。

整合包验证覆盖独立实例、可选文件、覆盖文件优先级、同名版本保护、校验失败和取消。另在隔离目录实际安装并启动了 Minecraft 1.20.1、Fabric Loader 0.16.14 和 Fabric API。

设置和日志保存在 `%LocalAppData%\MCL`。界面预览保存在 `outputs`。游戏和存档位于你选择的游戏文件夹，MCL 的隔离验证使用独立测试目录。
界面安装验证还覆盖实际取消、重试按钮、进度动画、安装后列表刷新与所选版本持久化。验证入口为 work/ui-install-probe。
