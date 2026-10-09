# NoSuffering 0.1.4 功能补充测试记录

日期：2026-10-09；设计依据：`docs/DESIGN.md` v1.8。

本版增加个人先古奖励刷新、个人商店刷新和第三幕 Boss 增血。每项结果只适用于所列 DLL、平台、游戏版本和 Mod 组合。构建、加载、单人、原生双客户端和 Computer Use 游玩分别记录。商店续档入场遗物修正已通过原生检查；完整界面对局仍在进行，未完成项目不会记为通过。

## 环境

| 目标 | 实际游戏 | 运行时 | Mod 组合 |
| --- | --- | --- | --- |
| Mac ARM64 beta | v0.111.0 / `41cef1ea` / Steam build `24724944` | .NET 9.0.7、Godot 4.5.1、Harmony 2.4.2 | NoSuffering；25-Mod 组合专项使用独立克隆 |
| Mac ARM64 正式版 | v0.107.1 / `59260271` / Steam build `23811903` | 同上 | NoSuffering；框架专项另加源码构建的 ModConfig 0.2.2 |
| Windows x64 beta | xht-rog，v0.111.0，同上 commit/build | 同上 | 原有 25-Mod 组合，含 NoSuffering |
| Windows x64 正式版 | xht-rog，v0.107.1，同上 commit/build | 同上 | NoSuffering |

所有引擎夹具在 `NoSufferingLab` 隔离用户目录离线运行。多人使用两个实际游戏进程和原生 ENet，监听仅限 `127.0.0.1`。没有改动主 Steam 分支、主存档或其他模组配置。

## 药水输入修正后的候选版本

最终候选在上述功能实现上只增加两处原生药水入口的 Busy 拦截：在饮用设置 IsQueued／触发 BeforeUse、丢弃禁用槽位之前拒绝新操作，操作结束后同一药水仍可正常重试。不阻断已在等待中的玩家选择。

| 目标 | DLL SHA-256 | 构建 | 运行结果 |
| --- | --- | --- | --- |
| Mac beta | `02f63de79fe3d74563c70993c5c81fb0a2f1f6f52735b599d25fff9b849ba7dd` | PASS，0 警告／错误 | 已启动继续 UI 对局；本候选原生双客户端两端各 12/12 PASS，退出码 0 |
| Mac 正式版 | `c1744bb3b49baa9654570e2276b6b4006e59856d9f01b7eee338aa2548ca97e0` | PASS，0 警告／错误 | 本候选原生双客户端两端各 12/12 PASS，退出码 0 |
| Windows beta | `b453c472f989bb67abffb6af2e145fbd477f65f541635b631a01d2d66d54efe8` | PASS，0 警告／错误 | 25-Mod 组合加独立测试插件：原生互操作及药水回归 9/9 PASS，退出码 0；本候选原生双客户端两端各 12/12 PASS，退出码 0 |
| Windows 正式版 | `db382947565b2aef8b5493c980730b50690456c893f206e076dcbcedb4697bb8` | PASS，0 警告／错误 | 本候选原生双客户端两端各 12/12 PASS，退出码 0 |

四个最终候选的双客户端结果见 [Mac 证据](expansion-evidence/macos-potion-boundary.txt)及 [Windows 证据](expansion-evidence/windows-potion-boundary.txt)。独立复查核对 beta／正式版入口签名及已入队药水的续接路径，未发现这两处修正的具体回归。

原生药水回归通过反射人为保持真实 Busy 标志，再调用真实原生入口。旧 DLL 确定复现饮用和丢弃的局部状态卡住；新 DLL 两项均保持原状态，并在释放标志后对同一药水重试成功。这不证明物理鼠标、真实 F07 并发时序或多人药水竞态已验收。完整证据及测试插件源码见[原生互操作记录](expansion-evidence/native-interop.txt)与 `tests/native-interop`。

## 修正前已执行的构建与测试

| 目标／DLL | 构建 | 加载 | 单人 | 双客户端 | 独立进程继续 |
| --- | --- | --- | --- | --- | --- |
| Mac beta `578af2e7…` | PASS，0 警告／错误 | PASS | 同 DLL、25-Mod 组合新增功能 13/13；完整 UI 对局进行中 | 同 DLL 原生 12/12，两端退出码 0 | 同 DLL：购买自动存档、实际进程退出／原生 load/join、库存锁定与下一次种子 PASS；UI 已确认刷新牌序跨进程保留 |
| Mac 正式版 `3e71f533…` | PASS，0 警告／错误 | PASS | 同 DLL 新增功能 13/13；`90a1b604…` ModConfig 专项 PASS | `730ed3b5…` 原生 12/12，两端退出码 0；最终商店钩子修正由单人及新进程检查覆盖 | 最终 DLL：购买自动存档后两进程退出、重新加载／加入 PASS |
| Windows beta `37c24306…` | PASS，0 警告／错误 | PASS，实际加载 25 Mod | 同 DLL 新增功能 13/13；`f90d32cb…` 地图／设置 7/7 | 同 DLL 原生 12/12，两端退出码 0 | 同 DLL：独立进程购买续档与新种子 PASS |
| Windows 正式版 `a466aa23…` | PASS，0 警告／错误 | PASS，实际加载 1 Mod | 同 DLL 新增功能 13/13；`e3cabac3…` 地图／设置 7/7 | 同 DLL 原生 12/12，两端退出码 0 | 同 DLL：购买自动存档、实际进程退出／原生 load/join、库存锁定与下一次种子 PASS |

此阶段 Windows 源码快照为 `24253ce23c77e76205ad68701697144e5d008f7055c83af2ceea7f496d7663dc`。前序 `874ac3ab…` 的地图／设置 7 项在相关代码未变时保留，不重复冒充最终 DLL 运行。此阶段各目标的完整 DLL 摘要、检查内容及退出码见[结构化证据](expansion-evidence/windows-final.txt)、[Mac beta 联机](expansion-evidence/macos-beta-mp.txt)、[Mac beta 新进程](expansion-evidence/macos-beta-fresh.txt)、[Mac 正式版单人](expansion-evidence/macos-stable-sp.txt)、[Mac 正式版新进程](expansion-evidence/macos-stable-fresh.txt)、[Windows 正式版联机及新进程](expansion-evidence/windows-stable-mp.txt)、[Mac beta 25-Mod 功能检查](expansion-evidence/macos-beta-multimod.txt)。

原始运行记录保存在本地忽略目录 `artifacts`：

- Mac beta 25-Mod 最终 13 项：`macos/public-beta-25mod/accepted-profile-expansion/run-and-restore-record.json`；完整树摘要证明原界面对局的 293 份文件已原样恢复。
- Mac beta 双客户端：`macos/public-beta/mp-20261009-174439/run-record.json`。
- Mac beta 新进程：`macos/public-beta/mp-20261009-174529/run-record.json`。
- Mac 正式版双客户端：`macos/public/mp-20261009-173108/run-record.json`。
- Mac 正式版新进程：`macos/public/mp-20261009-174630/run-record.json`。
- Mac 正式版最终 13 项：`macos/public/lab-20261009T094047737570Z/run-record.json`。
- ModConfig 与单人：`macos/public/lab-20261009T092653596135Z/run-record.json`。
- Windows 正式版双客户端／独立进程：`windows/public/mp-20261009-181819/run-record.json`、`windows/public/mp-20261009-181938/run-record.json`。
- Windows 最终汇总：`validation/windows-24253ce2.json`，包含源码、DLL、单人、两次联机运行和完整模组版本；阶段地图／设置结果在 `validation/windows-874ac3ab.json`。

## 功能检查的具体含义

单人新增功能夹具执行真实原生房间、奖励和购买处理器，通过直接设置幕次／进入地图坐标建立场景；不是完整游玩，也不是物理鼠标验证。

- 初始涅奥刷新本人奖励、领取、原生 Proceed 打开可交互地图。
- 整体刷新本人原生库存，房间／地图位置不变，原生 Shops／Rewards RNG 不受污染；会员卡和送货员的价格叠加正确。
- 金币不足的真实失败购买不锁刷新，随后刷新成功。
- 成功购买改变金币与牌组，送货员补货仍锁定刷新。
- 正常商店入场只执行一次餐券治疗 15、Maw Bank 金币 12；刷新后或购买后保存并经原生磁盘加载，已有损伤、金币和购买锁定不变，不重复执行入场收益。此三项新增断言在四个目标通过；Mac beta 使用 25-Mod 组合。
- 第二幕 Boss 和第三幕普通敌人拒绝增血且不改变状态。
- 第三幕 Boss 对每个存活敌人增加当前有效上限的 25%，已有损伤保留，战前基线不变。
- 普通重开、新牌序重开、再次普通重开及原生磁盘续读保持确切增血量，不重复叠加；当前牌序尝试保留。

原生双客户端 12 项检查覆盖：真实建局、伪造他人身份选择被拒绝、房主领取后客户端仍能仅刷新自身且扣自身生命、双方独立领取与 Proceed、客户端个人商店刷新、送货员购买及所有者库存同步、客户端不能执行房主增血、普通重开、新牌序重开、地图回滚、待完成的奖励任务先于刷新收费处理、原生商店磁盘续读。

另一次独立进程检查先刷新商店、让客户端成功购买并自动存档，未追加手动保存或刷新便退出两端；第二组进程通过游戏原生加载大厅继续。Mac 正式版恢复的客户端金币 9961、牌组 11 张、库存及购买锁定一致；下一次刷新使用新的持久化 Revision 输入，Revision 2→3，种子 `363808084864638692`→`885736499033398724`。Mac beta 对应过程恢复金币 9939、牌组 11 张，种子 `14429437140635911785`→`1218682949736837250`。Windows beta 与正式版也各自独立通过同一过程。它们不是 Steam 或互联网多人测试。

## 设置和常用模组

Windows beta 与独立 Mac beta 克隆实际加载组合包含 BaseLib 3.4.7、RitsuLib 0.6.7、UndoAndRestart 0.111.0.4、AncientSL 1.0、Intent Graph 1.6.0、Custom Card Balance 2.9.1、CrystalSphereAlpha 0.0.1，以及当前角色、先古之民、商人皮肤和语音模组。Mac beta 13 项检查使用同一最终 DLL；固定测试目录限制使首次新目录运行未执行，随后在游戏关闭期间临时移开原副环境存档，使用独立测试内容运行，结束后逐文件核对恢复，未修改主存档。每项原生操作通过只证明此组合中的对应场景，未据此宣称对方所有功能或任意组合兼容。

已只读核对安装的 UndoAndRestart 生命周期：原生 `CombatManager.Reset(bool)` 后调用 `UndoRedoManager.Reset()`，清空历史、选择、待执行动作和导航，取消恢复并递增世代；战斗对象引用变化另有清理。最终 beta 单人与联机日志均记录 NoS 重开／回滚之前旧历史 reset、之后新建会话／快照。多人中对方明确禁用单步撤销捕获。该阶段仅证明实际原生重载经过对方清理路径。随后新增独立插件调用真实原生 PlayCardAction 和第三方 HandleUndoKey／HandleQuickRestartKey，验证出牌后撤销恢复手牌和能量、NoS 重开清理旧世代、刷新牌序后交替撤销及第三方重开保持当前尝试。最终 Windows beta 候选重复全部七项互操作检查通过；通过原生处理器调用，不是物理按钮点击。AncientSL 的 `EventRoom.OnEventStateChanged` 回调在个人刷新和替换后仍保留；单人及双客户端领取／Proceed 实际通过。

AncientSL 1.0 本身没有刷新按钮；其补丁保留领取前存档。已通过第三方原生楼层重开加载该存档，再执行 NoS 个人刷新、领取及 Proceed。实际 UndoAndRestart DLL 与 AncientSL DLL 指纹、七项逐项结果以及两项药水回归见[原生互操作证据](expansion-evidence/native-interop.txt)。

ModConfig 专项使用[官方 v0.2.2 标签](https://github.com/xhyrzldf/ModConfig-STS2/tree/v0.2.2)，commit `639eb97fa7824e94a43339913c51433117207d05`。该标签无发布二进制附件；使用未修改源码、官方 Godot.NET.Sdk 4.5.1 与源生成器构建完整 DLL，用官方 Godot 4.5.1 导出对应资源 PCK。DLL SHA-256 为 `8d2dad09f28633520c8a61abae6a4d2fce04af31e5b18c4d71fd8a8425ff48a0`，PCK 为 `205732d402731226c811dd5ed45a8776ff33a5158fb52dee6308c91762ff8fdf`。官方 manifest 为 0.2.2，日志常量仍为 0.2.1，保留原样。

Mac 正式版实际框架运行 PASS：注册 14 个设置，开关／百分比／语言／带修饰键快捷键双向同步，两份配置落盘，真实原生 Mods 页生成并显示 7 个开关、3 个滑块、3 个下拉框和按键控件。通过原生 `ForceTabPressed` 触发页签，不是人工点击验收。结束后恢复原配置并移除测试框架，不改变无框架基线。未测试多人框架组合、框架官方发布二进制或框架 UI 的真人操作。

[Shop Enhancement STS2_0.5.2](https://github.com/moyudamowang/Shop-Enhancement-STS2) 官方发布 DLL 调用当前 beta 已不存在的 `MerchantRoom.get_Inventory()`。已核对真实程序集签名，属于该发布版本的 API 阻断；运行未执行，不能标为兼容。未修改或再分发对方 DLL／资源。

## 修复与失败记录

- 最终 Mac 正式版首次联机夹具与 beta 同时运行，原生端口冲突导致主机 CantCreate、连接超时；该次其余检查未执行。保留失败日志后，单独重跑正式版，两端 12/12 PASS、退出码 0。

- 原动作队列 Busy 拦截发生在药水本地标记之后，拒绝动作会遗留 IsQueued／禁用槽位。旧 DLL 用真实入口确定复现；提前拦截两处入口后，对同一药水释放 Busy 后重试通过。此缺陷与 Computer Use 药水菜单点击无效果的未定因现象分开记录。

- 商店生成的 CardRarityOdds 缓存了 Rewards RNG；仅替换玩家字典会污染原生随机流。同步替换缓存并在 finally 恢复原实例后检查通过。
- 原生商店并不序列化完整库存。补存原生卡牌／商品数据、原价、折扣标记、补货池和删牌状态，在原生工厂恢复所有者库存；不将远端未补货的占位库存覆盖真实所有者库存。
- 购买后的自动同步保存曾缺失，退出继续会回到购买前。增加成功购买后的内部提交，待原生购买／补货完成，再同步所有者库存并保存；独立进程验证通过。内部提交没有新增 UI 操作或收费。
- 操作序号在新进程重置曾导致刷新种子复用，改用持久化 WorldRevision 参与种子输入。
- 原生加载客户端在库存工厂执行前需要收到保存的个人库存；补充真实保存大厅握手。正在执行的有序重载中抑制额外 Hello／State 广播，修复回滚摘要不同；最新 beta／正式版双客户端均通过。
- 移除向原生 RoomId 注入高位数字的旧处理；原生位置网络编码只有 4 位 RoomId，该处理会让奖励／金币消息位置不匹配。保留原生 ID，操作世代由本模组协议处理。
- Windows 路径分隔符曾使多人诊断拒绝启动，已统一规范路径并在非法探针参数下退出，避免继续原生快速建局。该次未完成运行不算多人通过。
- 地图／设置探针曾仍断言 5 个开关，更新为本版 7 个后 Windows 两分支 7/7 通过。
- ModConfig 首次诊断调用受保护 OnRelease 没有发出原生 Released 信号；改用公开 ForceTabPressed 后真实页签检查通过。这是诊断调用问题。
- 静态审查发现商店保存的是入场后状态，但续读会再次触发餐券／Maw Bank 等入场效果。已为实际保存库存恢复的那次新房间重建设置一次性标记，仅跳过它的重复入场玩法钩子；普通入场和入场前回滚不跳过。新增三项原生检查在四个目标通过，补修后的 beta 双客户端和 Mac 双分支新进程续档通过；独立复查未发现具体回归。

## xht-rog 主目录部署

主目录此前已安装 Windows beta DLL `37c24306…`，Steamworks 初始化成功、NoSuffering 0.1.4 初始化、实际加载 25 Mod，启动检查退出码 0。更新前备份 15 份原生运行存档／伴随记录（含一份第三方备份），启动后 15 项摘要均不变；临时计划任务已移除。未进入主存档游玩。Headless Godot 退出时报告资源／RID 尚未释放，保留该日志事实，不把启动结果当作玩法验收。[部署证据](expansion-evidence/primary-deploy.txt)。

## Computer Use 和未测边界

完整 Mac beta 对局见[独立记录](2026-10-09-cua-playthrough.md)，当前进行中，已完成两种战斗重开、刷新牌序跨进程保留、战斗、事件及火堆回滚、自带设置、商店刷新与购买锁定跨进程保留的界面操作；第二／三幕和结局仍未完成。初始全屏模式使 Computer Use 找不到可点击窗口，通过系统 Window→Exit Full Screen 恢复；这属于操作环境问题，不算模组通过项。

未完成的完整对局、Steam／互联网多人、两台实机真人联机、Intel Mac、Linux、未来游戏版本、未列出的 Mod 组合，以及物理点击第三方撤销／刷新按钮，均未标为通过；已执行的原生处理器互操作范围如上。历史版本结果不填补这些边界。
