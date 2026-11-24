# MCL 0.5

粉白配色的 Minecraft Java 版启动器，使用 C#、WPF 和 .NET 9 开发。

## 直接运行

双击项目内的“启动 MCL”快捷方式，或运行 `outputs/MCL/MCL.exe`。需要 Windows 64 位及 .NET 9 桌面运行时，当前开发电脑已具备。

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

选择侧栏“导入整合包”。把 Modrinth 的 `.mrpack` 拖入页面，或点击“选择整合包文件”。也可选择包含 Modrinth 清单的 ZIP。核对包名、Minecraft 版本、加载器和安装位置后，点击“安装整合包”。清单里标为可选的客户端文件可以勾选一并安装。

MCL 会安装或复用校验通过的原版文件，安装清单指定的 Fabric Loader，下载并核对模组文件，然后应用客户端配置。安装完成后，整合包会加入“游戏版本”并成为当前选择，回首页即可启动。`versions/pack-…/game` 保存该整合包的模组、配置和存档。如果已有同名版本的配置与官方不同，MCL 会保留该版本，并缓存一份整合包使用的官方版本文件。如果整合包使用版本列表中的基础版本，MCL 会阻止误删该基础版本。

当前支持 Minecraft 1.13 及以后的原版正式版与 Fabric 整合包，所选版本还须能通过当前原版安装器安装。CurseForge、Forge、NeoForge、Quilt 和其他格式会显示不支持的原因。取消或失败时，已经完成的基础游戏文件可复用，整合包实例在全部安装成功后才出现。

## 已有游戏与本地档案

可以在首页选择包含 `versions`、`libraries` 和 `assets` 的游戏文件夹。误选 `versions` 子目录时，MCL 会识别对应根目录。

在“启动设置”里填写本地玩家名、选择 Java、调整内存，然后点击右下角“保存设置”。本地档案没有微软账户身份，不能用于要求正版认证的服务器。

## 微软账号登录

侧栏新增“账号管理”。流程为浏览器微软授权、Xbox 与 Minecraft 认证、Java 版游戏资格和角色验证、显示角色名与头像，再使用真实账号身份启动游戏。提供取消、登录续期及退出登录；正版身份启动前会联网验证，验证失败会显示原因，保持所选身份。

本次完成的是接入代码和可配置入口。尚未取得本项目自己的应用编号与 Minecraft 访问许可，未进行真实账号端到端登录验证。当前保留未认证本地启动，按你的选择暂不提交审核；官方当前要求在允许游戏前认证并验证资格，未来申请前需先调整这一行为。

完整步骤见项目根目录“微软登录接入说明.md”，程序中“账号管理 → 申请与接入说明”也能打开。取得自己的 Client ID 后，在“登录接入设置”里填写并保存，再点击“使用微软账号登录”。没有编号时登录按钮不可用；新应用未获准访问 Minecraft 时，网页登录完成后仍可能被服务拒绝。

密码和验证码仅在微软网页输入。登录凭证以 Windows 当前用户 DPAPI 加密保存到 `%LocalAppData%\MCL\account.dat`，启动设置中只保存应用编号和身份选择，日志隐藏凭证。退出登录清除这个启动器保存的记录，浏览器会话由浏览器管理。首次只管理一个微软账号，多账号列表及更换皮肤另行开发。

## 管理和删除已有版本

在“游戏版本”里选中一个版本，下面会显示“版本管理”。点击“打开文件夹”可查看该版本目录；点击“删除版本”会先显示版本名称、具体路径和独立数据提示，默认操作是取消。

确认后，只将选中的 `versions/<版本名>` 文件夹移入 Windows 回收站。该文件夹中的独立存档、模组和设置也会一起移动；共享资源和游戏根目录的存档不在删除范围。可在 Windows 回收站恢复，恢复后点击“刷新”重新识别。

游戏运行中会禁用删除。存在继承或 JAR 引用的依赖版本、文件被占用、路径包含链接、路径过长或无法安全使用回收站时，会停止并显示原因。删除成功后刷新列表，选择剩余版本并保存；没有剩余版本时清空当前选择。
## 开发和验证

开发环境为 VS2022 的“.NET 桌面开发”工作负载和 .NET SDK 9.0.319。打开 `MCL.sln`，按 F5 运行。

```powershell
dotnet build MCL.sln -c Debug
dotnet publish src/MCL/MCL.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o outputs/MCL
```

隔离验证入口：

```powershell
dotnet run --project tests/ModpackValidation/ModpackValidation.csproj -c Release
dotnet run --project work/download-validation/DownloadValidation.csproj -c Release
dotnet run --project work/validation/Validation.csproj -c Release
dotnet run --project work/auth-validation/AuthValidation.csproj -c Release
dotnet run --project work/account-storage-validation/StorageValidation.csproj -c Release
dotnet run --project work/account-ui-validation/AccountUiValidation.csproj -c Release -- .
```

下载回归覆盖首次安装、完整文件复用、损坏文件修复、校验失败重试、取消后恢复、保护已有版本及路径边界。已从空目录实际下载并验证官方 1.20.1 的 3641 个文件，随后验证重复安装全部复用。

整合包验证覆盖独立实例、可选文件、覆盖文件优先级、同名版本保护、校验失败和取消。另在隔离目录实际安装并启动了 Minecraft 1.20.1、Fabric Loader 0.16.14 和 Fabric API。

账号功能已通过 35 项认证与真实本机回调检查、43 项凭证存储和启动参数检查、34 项实际 WPF 窗口及头像检查。认证服务测试使用模拟响应，浏览器测试只在本机回调；启动参数测试使用隔离的假 Java 子进程。这些检查不代表已用真实微软账号登录，也不代表应用已通过审批。

设置和日志保存在 `%LocalAppData%\MCL`。界面预览保存在 `outputs`。游戏和存档位于你选择的游戏文件夹，MCL 的隔离验证使用独立测试目录。
界面安装验证还覆盖实际取消、重试按钮、进度动画、安装后列表刷新与所选版本持久化。验证入口为 work/ui-install-probe。
