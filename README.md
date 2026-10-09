# 不吃苦 · NoSuffering

《杀戮尖塔 2》原生 C# Mod，当前开发发行 **0.1.2**。五项核心路径已实现；尚未完成 v1.0 玩法验收。需求见 [设计文档](docs/DESIGN.md)，本次修复见 [涅奥测试记录](docs/testing/2026-10-09-neow.md)，跨平台基线见 [0.1.1 测试记录](docs/testing/2026-10-08-compat.md)。

F6 打开面板：路线回滚、刷新先古之民、刷新先古之民奖励、普通战斗重开、重开并刷新牌序。联机由房主发起，全队自动同步。普通重开保留当前尝试；新尝试写入伴随存档。两种先古刷新独立选择免费或扣房主当前生命，其余永久免费。

## 安装和使用

关闭游戏，将安装包中的 `NoSuffering` 目录放到游戏 `mods` 目录，再通过 Steam 启动游戏。联机全员安装同一 NoSuffering 版本和兼容的玩法 Mod 组合。Esc 关闭面板。路线回滚默认一次确认。

推荐安装 **ModConfig**：面板“设置”打开其同一配置页，支持改键、语言、费用、功能开关和历史数量。未安装时使用默认设置：F6、中文随游戏语言、最近 10 个决策点、两种刷新免费。配置位于 Godot `user://NoSuffering/config.json`，伴随存档位于 `user://NoSuffering/runs`。备份／迁移运行时应同时保留原生存档和该目录。

不刷新普通事件、战斗奖励、商店，不在战斗中途洗牌。胜利结算和全队战败页不提供战斗重开。

## 构建和部署

引用自己的真实游戏程序集，不下载或打包游戏 DLL。需要与游戏运行时匹配的 .NET SDK；工程内 `.tools/dotnet/dotnet[.exe]` 存在时优先使用，避免更改系统 SDK。

```powershell
./scripts/inspect-game.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/build.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/deploy.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
```

构建输出 `artifacts/<platform>/<branch>/NoSuffering-0.1.2.zip`，同目录记录实际程序集和构建指纹。四个平台／分支目标分别构建；发行页文件名标明目标。正式版构建／部署必须传入 `--branch public`，PowerShell 使用 `-Branch public`。部署先备份原 NoSuffering，仅更新本 Mod，游戏运行时拒绝覆盖。

Mac 可直接构建／部署：

```bash
python3 scripts/build.py inspect --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64
python3 scripts/build.py build --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
python3 scripts/build.py deploy --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
```

Mac 包安装到 `.app/Contents/MacOS/mods/NoSuffering`。实际通用 app 同时带 ARM64 与 x86_64 程序集目录，必须明确目标架构；不要将 Windows 包当作 Mac 构建结果。副环境创建与实测见 [跨平台说明](docs/COMPATIBILITY.md)、[0.1.1 记录](docs/testing/2026-10-08-compat.md)。

Mac 远端工作流程：复制 `local.env.example` 为 `local.env`，填写已有 SSH 别名、远端工程目录和游戏路径；运行 `python3 scripts/remote.py --sync`。PowerShell 指令可通过 stdin 交给 `remote.py`。凭据继续使用既有 SSH 配置，不写入项目。

双分支构建使用 `scripts/build-matrix.ps1 -BetaGameDir <beta目录> -StableGameDir <正式版目录>`。不提供某分支目录时明确记录未执行；脚本不切换 Steam 分支。启动检查可用 `scripts/load-check.ps1`，SSH 环境下加 `-InteractiveSession` 复用已登录桌面的 Steam IPC；完成后删除临时计划任务 `NoSuffering-StartupCheck`。

## 当前兼容范围

优先基线：public-beta **v0.111.0 / build 24724944**；正式版基线为 **v0.107.1 / build 23811903**。Windows 与 Mac ARM64 使用各自真实程序集构建，按平台和分支选包。具体加载、五项核心操作及存档继续结果见测试记录；不将这些结果扩展到未来游戏更新或 Intel Mac。

xht-rog 现有 25 个 Mod 组合包括 BaseLib、RitsuLib、UndoAndRestart、intentgraph2、CustomCardBalance。本次在复制这些 Mod 的 Windows beta 副环境中验证了涅奥奖励刷新、领取及继续；其余组合玩法、撤销历史互操作或 ModConfig 设置页不据此认定全部兼容。

当前原版事件战斗有父事件状态适配；尚无已核实恢复格式的第三方父事件限制该场重开，保持正常游戏路径。没有稳定身份的额外起手卡限制刷新牌序。原生未完成的多步先古奖励流程不安全恢复，报告明确错误。伴随存档格式或玩法 Mod 组合不一致时拒绝混用。0.1.2 明确允许读取同格式的 0.1.1 记录，并继续核对游戏、其他 Mod、运行和原生保存提交；不修改或自动转换旧记录。

联机实测按用户安排后续进行。未执行测试不会标为通过。技术参考和署名见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
