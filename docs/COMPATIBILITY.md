# Mac / 正式版适配与副测试环境

本页记录 0.1.1 建立的隔离副测试环境。其历史结果见 [0.1.1 测试记录](testing/2026-10-08-compat.md)；当前版本 0.1.3 的实际验证范围以[本版测试记录](testing/2026-10-09-rollback.md)为准。

## 实际布局

| 目标 | 环境和证据 |
| --- | --- |
| Windows public-beta | xht-rog 保留当前 Steam 分支并已安装 0.1.3；独立副本 `.tools/game-lab/windows-public-beta/game`，v0.111.0 真实程序集 |
| Mac ARM64 public-beta | Steam 正版 depot 2868842，独立下载 `.tools/game-lab/macos-public-beta/game/SlayTheSpire2.app`，v0.111.0 |
| Windows / Mac ARM64 public | Steam 正版 depot 2868841 / 2868842，独立副环境，v0.107.1 真实程序集；验证状态逐项写入测试记录 |

Mac 原生可执行文件为 universal Mach-O（ARM64 与 x86_64），实际包含两个程序集目录。当前在 ARM64 MacBook 上明确选择 `Contents/Resources/data_sts2_macos_arm64`；x86_64 尚未运行验证。Mac 与 Windows 的 sts2.dll 指纹不同，分别编译并输出平台／分支包，GodotSharp 与 Harmony 指纹相同。

正式版使用 `Sts2Branch=public` 编译原生 API 差异：旧版 RNG 的 Seed / Counter、父事件 RNG 快照、加载大厅玩家 ID 和网络服务构造器。beta 保留对应的原生接口。刷新先古奖励时，正式版暂时替换玩家 Rewards RNG 并在同步生成后恢复原实例；不推进普通奖励随机流。没有加入通用反射兼容框架，也不宣称跨未来版本自动兼容。

工程 `.tools`、游戏文件、授权缓存、运行产物全部忽略，发行包仅包含 NoSuffering 自身文件。沿用既有 SSH alias xht。

## 存档隔离

游戏副本旁写入 Godot 官方的 `override.cfg`：

```ini
[application]
config/use_custom_user_dir=true
config/custom_user_dir_name="NoSufferingLab/macos-public-beta"
```

Windows 使用对应的 `NoSufferingLab/windows-public-beta`。真实引擎日志已确认 Windows 路径为 `%APPDATA%/NoSufferingLab/windows-public-beta`，Mac 为 `~/Library/Application Support/NoSufferingLab/macos-public-beta`。正式版使用独立 `*-public` 目录。仅复制游戏文件不足以隔离存档；必须核对实际引擎路径。

`--force-steam=off` 放在原生参数区，`--ns-lab-probe` 放在 `--` 后。前者在这两个真实分支的原生 NGame 中关闭 Steam 初始化、云存储和 Workshop 查询；后者仅记录实际用户目录。使用真实引擎、磁盘存档与原生运行，不用 TestMode 的内存 Mock。初次启动可能因原生 Mod 提示尚未同意而不加载；`--enable-mods` 仅备份并更新已生成的专用副环境设置，保留其他配置。

诊断玩法探针还要求完整用户目录精确匹配本平台的 NoSufferingLab 槽，以及明确的离线和探针参数。普通启动不会执行诊断。先古探针通过原生 API 设置第二幕、全解锁和测试路线；这不是正常完整通关、界面操作或联机验收。

## 可复用命令

安装官方 [DepotDownloader](https://github.com/SteamRE/DepotDownloader)，由游戏所有者扫码或在终端交互登录：

```bash
python3 scripts/acquire-game.py --platform macos --branch public-beta --downloader /path/to/DepotDownloader
# 两个平台的正式版可在一个会话获取；密码仅在下载器终端输入。
python3 scripts/acquire-game.py --platform both --branch public --interactive-login --downloader /path/to/DepotDownloader
```

下载前拒绝链接目标和已经准备的副环境，允许未完成下载继续。不会修改主 Steam 分支。二维码失效／授权拒绝均按真实结果保留，不认为“用户点批准”就下载成功。

Mac 下载完成后的准备、构建、部署与检查：

```bash
python3 scripts/lab.py prepare --game-dir .tools/game-lab/macos-public-beta/game --assembly-dir .tools/game-lab/macos-public-beta/game/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --platform macos --branch public-beta --in-place
python3 scripts/build.py build --game-dir .tools/game-lab/macos-public-beta/game --assembly-dir .tools/game-lab/macos-public-beta/game/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
python3 scripts/build.py deploy --game-dir .tools/game-lab/macos-public-beta/game --assembly-dir .tools/game-lab/macos-public-beta/game/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
python3 scripts/lab.py run --platform macos --headless
# 首次原生运行生成 settings.save 后启用副环境 Mod。
python3 scripts/lab.py run --platform macos --headless --enable-mods --gameplay-probe --quit-after 6000
python3 scripts/lab.py run --platform macos --headless --continue-probe --quit-after 6000
```

Windows 使用相同 Python 脚本，或已有 PowerShell 包装。`prepare` 从真实安装复制时排除主 Mod；下载 Mac 后恢复其主可执行文件的执行位，不关闭系统安全控制。`run --quit-after 0` 可启动人工游玩的独立环境。

## 验证边界

0.1.1 历史记录分别记录了当时的构建、初始化、单人探针与独立进程继续；这些结果不代表 0.1.3 的回滚行为或当前兼容性。历史记录中 Windows 的 25 个 Mod 组合只验证了 0.1.1 启动共存，不构成玩法互操作或“兼容全部 Mod”的依据。Intel Mac、未来游戏版本和联机均没有兼容结论；联机测试按用户安排后续进行。

0.1.3 四目标均构建和加载；Windows beta 的当前 25-Mod 组合通过七项地图／设置回归、十项核心检查，以及结算 Boss 和刷新牌序的新进程继续。Mac beta、Mac stable 和 Windows stable 的实际检查分别记录，不能从一个目标推断其他目标。主目录另行完成 Steam 启动检查和 14 份存档／伴随文件摘要比对；未进入主存档测试玩法。ModConfig 0.2.2 仅核对官方 API，其框架内运行未测试。

0.1.3 使用存档格式 2。仅接受已知的 0.1.1/0.1.2 格式 1 记录，且必须匹配游戏与其他 Mod 指纹及原生存档配对；旧格式只恢复可用的战斗检查点。旧记录没有保存的非战斗入口快照无法补造。详细行为及本版测试状态见[回滚测试记录](testing/2026-10-09-rollback.md)。

依据：[Godot exported override](https://docs.godotengine.org/en/stable/classes/class_projectsettings.html)、[用户数据路径](https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html)、[官方引擎设置加载源码](https://github.com/godotengine/godot/blob/4.5/core/config/project_settings.cpp)，以及下载后的真实引擎日志、原生 NGame / CommandLineHelper / SaveManager / ModManager 与程序集指纹。
