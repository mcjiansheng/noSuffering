# 不吃苦 · NoSuffering

《杀戮尖塔 2》原生 C# Mod，当前版本 **0.1.3**。本版改为原生地图节点回滚，并提供独立设置界面。需求见 [设计文档](docs/DESIGN.md)，实际验证范围见 [测试记录](docs/testing/2026-10-09-rollback.md)；[0.1.1 跨平台记录](docs/testing/2026-10-08-compat.md) 和 [0.1.2 涅奥记录](docs/testing/2026-10-09-neow.md) 仅代表各自版本的历史证据。

F6 打开面板：路线回滚、刷新先古之民、刷新先古之民奖励、普通战斗重开、重开并刷新牌序。联机规则为房主发起、全队同步，实际多人兼容性尚未验收。普通重开保留当前尝试；新尝试写入伴随存档。两种先古刷新独立选择免费或扣房主当前生命，其余永久免费。

## 安装和使用

xht-rog 当前 beta 主目录已安装 0.1.3，Steam 启动检查通过；部署前后核对的 4 份原生单人／多人存档及 10 个伴随记录保持一致。

关闭游戏，将安装包中的 `NoSuffering` 目录放到游戏 `mods` 目录，再通过 Steam 启动游戏。F6 打开／关闭设置与操作面板，Esc 关闭。点击“路线回滚”后直接进入原生地图，点击已记录的访问节点返回；战斗保留胜利结算结果，事件、商店、火堆等恢复入场状态。可切换幕次，不额外弹确认框。联机兼容性尚未验收。

所有设置可直接在 Mod 自带面板中使用，ModConfig 为可选接入；若已安装，会反射注册相同设置并同步到本模组的 `config.json`，未安装时功能和设置不受影响。ModConfig 0.2.2 已核对官方 API，框架内的运行验证尚未执行。可配置五项功能开关、快捷键组合、语言，以及两种先古刷新各自的 Free／Hp 模式和金额。配置位于 Godot `user://NoSuffering/config.json`，伴随存档位于 `user://NoSuffering/runs`。备份／迁移运行时应同时保留原生存档和该目录。

不刷新普通事件、战斗奖励、商店，不在战斗中途洗牌。胜利结算和全队战败页不提供战斗重开。

## 构建和部署

引用自己的真实游戏程序集，不下载或打包游戏 DLL。需要与游戏运行时匹配的 .NET SDK；工程内 `.tools/dotnet/dotnet[.exe]` 存在时优先使用，避免更改系统 SDK。

```powershell
./scripts/inspect-game.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/build.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/deploy.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
```

构建输出 `artifacts/<platform>/<branch>/NoSuffering-0.1.3.zip`，同目录记录实际程序集和构建指纹。平台／分支目标分别构建；发行页文件名标明目标。正式版构建／部署必须传入 `--branch public`，PowerShell 使用 `-Branch public`。部署先备份原 NoSuffering，仅更新本 Mod，游戏运行时拒绝覆盖。

Mac 可直接构建／部署：

```bash
python3 scripts/build.py inspect --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64
python3 scripts/build.py build --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
python3 scripts/build.py deploy --game-dir /path/to/SlayTheSpire2.app --assembly-dir /path/to/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64 --branch public-beta
```

Mac 包安装到 `.app/Contents/MacOS/mods/NoSuffering`。按已验证的目标平台和游戏分支选用对应构建；其他架构和游戏版本的兼容性不作推断。副环境历史记录见 [跨平台说明](docs/COMPATIBILITY.md)。

Mac 远端工作流程：复制 `local.env.example` 为 `local.env`，填写已有 SSH 别名、远端工程目录和游戏路径；运行 `python3 scripts/remote.py --sync`。PowerShell 指令可通过 stdin 交给 `remote.py`。凭据继续使用既有 SSH 配置，不写入项目。

双分支构建使用 `scripts/build-matrix.ps1 -BetaGameDir <beta目录> -StableGameDir <正式版目录>`。不提供某分支目录时明确记录未执行；脚本不切换 Steam 分支。启动检查可用 `scripts/load-check.ps1`，SSH 环境下加 `-InteractiveSession` 复用已登录桌面的 Steam IPC；完成后删除临时计划任务 `NoSuffering-StartupCheck`。

## 当前兼容范围

优先基线：public-beta **v0.111.0 / build 24724944**；正式版基线为 **v0.107.1 / build 23811903**。Windows 与 Mac ARM64 的两分支分别构建并加载。Mac beta 覆盖五项核心操作、刷新牌序持久化，以及地图／设置回归；Mac 与 Windows 正式版覆盖地图／设置回归及结算 Boss 新进程继续。Windows beta 当前 25 个 Mod 组合通过同样的七项回滚／设置回归、十项核心检查和结算 Boss 新进程继续，详见 [回滚测试记录](docs/testing/2026-10-09-rollback.md)。未进行双人实测；其他游戏版本、架构和 Mod 组合的兼容性不作推断。

历史 0.1.1/0.1.2 的测试只覆盖其记录列出的场景。0.1.3 存档格式 2 对已知的 0.1.1/0.1.2 格式 1 做严格配对校验，只能恢复旧战斗检查点；缺少的历史非战斗入口快照不能补造。其他玩法 Mod 互操作与多人玩法须单独验证。未执行测试不会标为通过。技术参考和署名见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
