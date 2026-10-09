# 不吃苦 · NoSuffering

《杀戮尖塔 2》原生 C# Mod，当前版本 **0.1.4**。新增个人先古奖励刷新、个人商店刷新和第三幕 Boss 生命调整。需求见 [设计文档](docs/DESIGN.md)，实际验证范围见 [本版测试记录](docs/testing/2026-10-09-expansion.md)和 [Mac beta 界面对局记录](docs/testing/2026-10-09-cua-playthrough.md)。此前版本的测试仅代表各自版本的历史证据。

F6 打开面板，提供七项操作：

| 操作 | 行为 |
| --- | --- |
| 路线回滚 | 在原生地图选择已访问节点；已结束的战斗恢复胜利结算状态，其他房间恢复入场状态 |
| 刷新先古之民 | 随机更换全队的先古之民；已有玩家提交奖励后锁定 |
| 刷新先古之民奖励 | 每名玩家只刷新自己的选项；本人提交后锁定 |
| 重新开始战斗 | 恢复战前状态，保留当前尝试的牌序 |
| 重开并刷新牌序 | 恢复战前状态并生成新牌序，退出继续仍保留 |
| 刷新商店 | 每名玩家只刷新自己的全部商品；本人首次成功购买或删牌后锁定 |
| 增加 Boss 生命 | 第三幕 Boss 战中按百分比增加当前存活敌人的生命上限，保留已有损伤 |

多人中两种个人刷新由本人点击，其他操作由房主发起，无投票。个人刷新也经过房主排序与同步。更换先古之民可独立设置扣房主当前生命；刷新个人先古奖励可独立设置扣本人当前生命。其余操作始终免费。

## 安装和使用

关闭游戏，将对应平台和分支安装包中的 `NoSuffering` 目录放到游戏 `mods` 目录，再通过 Steam 启动游戏。联机所有玩家需安装同一版本。F6 打开／关闭面板，Esc 关闭。路线回滚直接进入原生地图选择节点，可切换幕次，无额外确认框。

设置可直接在自带面板中使用，不依赖 ModConfig。可配置七项功能开关、快捷键组合、语言、Boss 增血百分比和两种先古刷新各自的 Free／Hp 模式与金额。已安装 ModConfig 时注册相同设置，并与本模组的 `config.json` 双向同步；真实框架的测试范围见测试记录。配置位于 Godot `user://NoSuffering/config.json`，伴随存档位于 `user://NoSuffering/runs`。备份／迁移运行时应同时保留原生存档和该目录。

商店刷新保留送货员、会员卡等原生价格与补货效果；金币不足、取消删牌等未成功交易不会锁定。Boss 百分比默认 25%，可填 1–1000%，每次按当时生命上限计算增量，重开与继续不重复叠加。不刷新普通事件或战斗奖励，不在战斗中途洗牌。胜利结算和全队战败页不提供战斗重开。

## 构建和部署

引用自己的真实游戏程序集，不下载或打包游戏 DLL。需要与游戏运行时匹配的 .NET SDK；工程内 `.tools/dotnet/dotnet[.exe]` 存在时优先使用，避免更改系统 SDK。

```powershell
./scripts/inspect-game.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/build.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
./scripts/deploy.ps1 -GameDir 'D:/steam/steamapps/common/Slay the Spire 2'
```

构建输出 `artifacts/<platform>/<branch>/NoSuffering-0.1.4.zip`，同目录记录实际程序集和构建指纹。平台／分支目标分别构建；发行页文件名标明目标。正式版构建／部署必须传入 `--branch public`，PowerShell 使用 `-Branch public`。部署先备份原 NoSuffering，仅更新本 Mod，游戏运行时拒绝覆盖。

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

优先基线：public-beta **v0.111.0 / build 24724944**；正式版基线：**v0.107.1 / build 23811903**。Windows 与 Mac ARM64 的两分支分别构建，逐项记录加载、单人、原生双客户端联机与独立进程续档。Windows beta 与 Mac beta 专项使用实际 25-Mod 组合；ModConfig 另以官方 v0.2.2 源码构建的完整 DLL/PCK 在 Mac 正式版运行验证。[本版测试记录](docs/testing/2026-10-09-expansion.md)列出对应 DLL、场景、失败与限制；不能将引擎夹具测试当作完整游玩，也不能从一个组合推断所有 Mod 兼容。

0.1.4 接受已知 0.1.3 格式 2 和 0.1.1/0.1.2 格式 1 的配对记录，仍要求游戏及其他玩法 Mod 指纹一致。旧格式只能恢复其实际记录的检查点；缺少的历史房间状态无法补造。Intel Mac、Linux、其他游戏版本、Steam／互联网多人和未列出的 Mod 组合尚无本版验证结论。技术参考和署名见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
