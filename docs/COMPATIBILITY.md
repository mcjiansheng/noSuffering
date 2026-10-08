# Mac / 正式版适配与副测试环境

状态：实施方案；2026-10-08 核对。NoSuffering 实现版本仍为 0.1.0，本文不增加兼容性通过项。

## 采用的环境布局

| 环境 | 用途 | 当前状态 |
| --- | --- | --- |
| xht-rog 当前 Windows public-beta | 主开发基线，保留原 Steam 安装 | 已构建、加载；玩法未验收 |
| xht-rog 独立 Windows public 目录 | 正式版程序集检查、构建及后续运行 | 待获取正版正式版文件 |
| MacBook 原生 ARM64 public-beta | Mac 路径、加载、快捷键和存档验证 | 常见路径未发现 Steam / 游戏，dotnet 未在 PATH |
| MacBook 原生 public | 正式版在 Mac 上的最终检查 | 在前两项副环境建立后补齐 |

xht-rog 的 D 盘剩余约 215 GB，当前 Steam 记录游戏安装约 3 GB，磁盘容量不是建立副本的阻碍。副本放在工程忽略的 `.tools/game-lab` 下，不进入 Git 或发行包。Mac 使用实际原生游戏和运行时，Windows 虚拟机不能替代 Mac 验收。

## 获取分支文件

不在主 Steam 安装里反复切换 beta/public。Steam 官方说明分支切换会替换当前已安装分支。

优先利用现有已登录 Steam 客户端下载当前正式版 depot/manifest 到独立目录；若无法可靠指定分支和完整 depot 集合，则使用 DepotDownloader 的 `-app 2868840 -branch public -os windows -dir <副目录>`。Mac 对应 `-os macos`。工具明确支持分支、平台、输出目录和文件过滤；不能假设它直接继承桌面 Steam 的登录状态。如果需要认证，只由用户在交互界面完成登录／扫码，不读取 Steam 的私人凭据或将密码写进脚本。

先获取 `release_info.json`、运行时配置和实际 `sts2.dll` / `GodotSharp.dll` / `0Harmony.dll`，即可开始 API 检查和构建；需要加载游戏时再补齐该构建的可执行文件、PCK 和所有运行时文件。先查询该分支实际 manifest，再记录 build/depot/manifest/hash，不能用复制过来的 appmanifest 或目录名称认定版本。

## 存档和 Mod 隔离

复制可执行文件仅隔离游戏文件。真实游戏的原生存档走 Godot `user://`，并可用 Steam 云存档；NoSuffering 自己的配置和伴随文件也在 `user://`。所以各游戏副本可能仍共享存档和配置。

副环境运行采用专用系统测试用户，以实际启动日志确认 `user://` 指向该用户独立目录。第一轮使用游戏已核实的 `--force-steam=off`，只加载副本可执行文件旁的本地 `mods`，从 NoSuffering 单独加载开始，然后加入对应分支／平台的 ModConfig、BaseLib 和常用 Mod。此模式仍使用真实引擎、原生运行和磁盘保存，但不验证 Steam 联机、云同步或 Workshop 行为。

该参数仅在当前 v0.111.0 程序集确认：`NGame.InitializePlatform` 跳过 Steam 初始化；`SaveManager.ConstructDefault` 因此采用本地存储；`ModManager` 不查询 Workshop。正式版必须先核实相同入口，不能直接假定存在。即使关闭 Steam，`user://` 根仍不会改变，所以仍需独立用户；不把未证实的 `--user-data-dir` 当作隔离方案。

后续 Steam 联机验收使用测试客户端正常 Steam 登录、确认测试用户目录和云设置，并安装固定版本的玩法 Mod 组合。现在暂不启动联机验收，沿用用户“测试在后续考虑”的安排。`TestMode.IsOn` 使用内存 Mock 存储，不能拿它证明退出重进后尝试记录持久化。

## 工程改动

1. 将 `NoSuffering.csproj` 的三个 DLL 引用改为明确的 `Sts2AssemblyDir`。Windows 现有目录保留默认值；Mac 从真实 app bundle 确认目录。社区模板给出的 Mac 路径为 `Contents/Resources/data_sts2_macos_x86_64`、部署位置 `Contents/MacOS/mods`，但本机 ARM64 必须检查实际目录，不能照抄 x86_64 路径。
2. 增加可在 Mac 运行的构建／打包入口，使用与目标游戏匹配的 SDK；继续保持托管 DLL，不加入 Windows 专属原生依赖。每个“平台＋分支”输出独立目录、程序集指纹与状态，避免四次构建相互覆盖。
3. 对正式版只适配已发现的 API／Harmony 插入点差异，集中在现有 GameBridge/Combat 边界。先尝试一份 DLL，只有实际二进制 API 不兼容才产出同版本的 beta/public 分包，不提前建立通用反射兼容框架。
4. 每环境分别记录构建、初始化、玩法和持久化。关键顺序：普通重开→新牌序→再次普通重开→保存退出重进；再做地图和先古路径。Mac 额外检查 app bundle 安装位置、F6/Fn 键行为、UI 缩放、文件权限及架构。Windows/Mac 联机作为后续独立项。

本次没有创建测试账户、下载游戏、切换 Steam 分支、修改云设置或运行玩法测试。先完成方案和可用入口核查；实际获取文件时的唯一可能新增交互是 Steam 授权登录。

## 依据

- 实际 xht-rog v0.111.0 程序集：NGame.InitializePlatform、SaveManager.ConstructDefault、UserDataPathProvider、ModManager.Initialize；此前环境指纹见 testing/environment-beta.json。
- [Steam 分支文档](https://partner.steamgames.com/doc/store/application/branches)：切换分支替换现有安装。
- [DepotDownloader 官方说明](https://github.com/SteamRE/DepotDownloader)：分支／平台／独立目录、文件过滤及交互认证。
- [Godot 用户数据路径](https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html)：user:// 取决于应用设置和系统用户。
- [ModTemplate 路径定义](https://github.com/Alchyr/ModTemplate-StS2/blob/master/content/ModTemplate/Sts2PathDiscovery.props)：Mac bundle 数据及 Mod 位置参考，仍以实际安装为准。
