# Mac / 正式版适配与副测试环境

本页记录隔离副测试环境。0.1.5 的通过项、DLL 指纹及未执行项目见[本版测试记录](testing/2026-10-11-victory-rewards.md)，以下广泛兼容检查来自 [0.1.4 历史记录](testing/2026-10-09-expansion.md)；物理鼠标／键盘游玩另见 [Mac beta 对局记录](testing/2026-10-09-cua-playthrough.md)。

## 实际布局

| 目标 | 环境和证据 |
| --- | --- |
| Windows public-beta | xht-rog 保留当前 Steam 分支；独立副本 `.tools/game-lab/windows-public-beta/game`，v0.111.0 真实程序集；保留当前 25 个 Mod 组合 |
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

诊断玩法探针还要求完整用户目录精确匹配本平台的 NoSufferingLab 槽，以及明确的离线和探针参数。普通启动不会执行诊断。单人探针通过原生 API 设置幕次、解锁和测试路线；这不是正常完整通关或物理鼠标操作。Computer Use 游玩不带玩法探针参数，已按游戏界面从新开局完成至原生结局；按用户授权添加强力卡牌辅助，候选版本和具体功能结果另行记录。

联机探针使用两个实际原生进程、独立用户目录和原生 ENet 主机／客户端，明确绑定 `127.0.0.1`，不打开公网监听或修改防火墙。Mac 双副本采用不同 bundle ID 和独立可执行文件，资源可硬链接。`--seed-reload` 让首组进程正常结束，再通过原生加载大厅建立第二组进程，检查购买存档、库存、个人锁定和刷新种子。它验证本机原生网络链路与加载路径，不代表 Steam、互联网或两名真人的测试。

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
python3 scripts/lab.py run --platform macos --branch public-beta --enable-mods --expansion-probe --headless --quit-after 0
python3 scripts/mp-lab.py --platform macos --branch public-beta --headless
python3 scripts/mp-lab.py --platform macos --branch public-beta --headless --seed-reload
```

Windows 使用相同 Python 脚本，或已有 PowerShell 包装。`prepare` 从真实安装复制时排除主 Mod；下载 Mac 后恢复其主可执行文件的执行位，不关闭系统安全控制。`run --quit-after 0` 可启动人工游玩的独立环境。

## 验证边界

各版本、平台和 Mod 组合的结果独立记录。双客户端检查在同一平台执行，Windows／Mac 混合联机及跨平台迁移存档未执行，不能从四个分支分别通过推断这两项兼容。Windows beta 当前及 Mac beta 独立克隆的 25-Mod 组合包含 BaseLib、RitsuLib、UndoAndRestart、AncientSL、Intent Graph、Custom Card Balance、角色与商人皮肤等；加载共存、NoSuffering 原生操作通过与交替调用对方功能是不同范围。Windows beta 最终候选另通过真实原生出牌和 UndoAndRestart 撤销／快速重开处理器的交替调用、AncientSL 保留存档加载后个人刷新领取，共七项检查；新增药水 Busy 拒绝及重试两项，总计九项。该专项调用真实处理器，未据此宣称物理按钮或所有第三方功能已测。

ModConfig 为软依赖，独立设置在未安装框架时可用。官方 v0.2.2 标签没有发布二进制附件，测试使用该标签未修改源码、官方 Godot.NET.Sdk 4.5.1 及源生成器构建 DLL，并由官方 Godot 4.5.1 导出对应资源 PCK。在 Mac 正式版真实引擎中完成注册、双向同步、两份配置落盘与原生 Mods 控件检查。该证据不等同于官方发布二进制、真人点击或多人框架测试；不把框架打包为依赖。

已核查 Shop Enhancement `STS2_0.5.2` 官方发布 DLL。其调用的原生 `MerchantRoom.Inventory` 在本次 beta 中不存在，因此不能宣称该版本兼容；未修改或重新发布对方模组。测试记录列出该静态阻断证据，运行仍标为未执行。

0.1.5 使用存档格式 2，接受已知的 0.1.3/0.1.4 格式 2 和 0.1.1/0.1.2 格式 1，仍须匹配游戏、其他 Mod 指纹及原生存档配对。旧记录没有保存的房间状态无法补造；新胜利节点的领取前快照与原奖励 RNG 见设计 v1.9。BaseLib 设置页为可选注册，仍以 NoSuffering 配置为唯一存储。Intel Mac、Linux、未来游戏版本及未列出的组合没有本版兼容结论。历史 0.1.1–0.1.4 记录仅代表当时范围，不用于填补当前未测项。

依据：[Godot exported override](https://docs.godotengine.org/en/stable/classes/class_projectsettings.html)、[用户数据路径](https://docs.godotengine.org/en/stable/tutorials/io/data_paths.html)、[官方引擎设置加载源码](https://github.com/godotengine/godot/blob/4.5/core/config/project_settings.cpp)，以及下载后的真实引擎日志、原生 NGame / CommandLineHelper / SaveManager / ModManager 与程序集指纹。
