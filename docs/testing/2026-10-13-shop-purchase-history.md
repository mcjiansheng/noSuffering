# 0.1.12 商店购买历史校验修复

日期：2026-10-13；需求：DESIGN v1.15。

## 现场与复现

xht-rog 0.1.11 客机日志显示，删牌后的前两次提交成功；第 3 次购买藏品、第 4 次房主购买“战略大师”后收到 StateDivergence。用户本轮附件 ZIP 包含截至 10 月 12 日的旧日志，未包含 10 月 13 日房主记录。

使用 0.1.11 生产逻辑，仅将隔离原生 ENet 双进程探针的购买商品改为无色牌／藏品，两次均复现：

- Mac beta mp-20261013-210056：Native 唯一差异为 bought_colorless，房主 null、客机 [CARD.CATASTROPHE]。
- Mac beta mp-20261013-210245：Native 唯一差异为 bought_relics，房主 null、客机 [RELIC.LANTERN]。

两次 Shop、Combat、Ancient、Boss 组件哈希均一致。原生 MerchantCardEntry、MerchantRelicEntry 在购买者侧写入购买来源历史；RewardSynchronizer 远端处理获得奖励时不写入对应购买历史。

## 修复

Digest 在深拷贝的原生存档中清空 BoughtRelics 和 BoughtColorless，仅规范化比较数据。另发现 MerchantRoom.Exit 会用每端本地库存写入未购买商品历史；远端库存未随原生购买清空／补货，离店后又出现 CardChoices 差异（mp-20261013-212609）。仅对含 Shop 房间的历史节点移除未选择的 CardChoices、RelicChoices、PotionChoices 比较注记，保留已选择记录及其他房间历史。运行中的历史与磁盘存档保留原值；牌组、藏品、金币、随机数、库存继续完整校验。0.1.12 接受已知的 0.1.11 配对存档。

定向探针新增房主购买无色牌与客机购买藏品，检查每次独立提交，并断言校验后原生存档中的购买历史仍存在。沿用原生删牌、送货员购买补货、新进程续存以及主动注入真正摘要分歧后的退出与存档保留检查。

## 本轮结果

四目标均以真实程序集构建，0 警告、0 错误；原生双进程 headless ENet 定向联机及重启续存通过。

| 平台 | 游戏版本 | 构建／加载 | 定向联机／续存 | 证据目录 |
|---|---|---|---|---|
| Mac ARM64 beta | v0.111.0 | 通过 | 通过 | mp-20261013-212928 |
| Mac ARM64 正式 | v0.107.1 | 通过 | 通过 | mp-20261013-213017 |
| Windows x64 beta | v0.111.0 | 通过，25 Mod | 通过 | mp-20261013-212950 |
| Windows x64 正式 | v0.107.1 | 通过 | 通过 | mp-20261013-213213 |

验证包括房主无色牌购买、离店再刷新、客机送货员角色牌及藏品购买、原始购买历史保留、新进程原生续存，以及注入真正摘要差异后双方回菜单且配对存档哈希不变。Windows beta 含 BaseLib、RitsuLib、UndoAndRestart、AncientSL 等实际 25 Mod 组合，其余目标只加载 NoSuffering。精确 DLL 指纹、两端结果见 [机器记录](evidence/0.1.12/validation.json)。

本轮单人完整对局、Computer Use 和 Steam 跨机器联机未执行。首次新增探针对 Release 网络通知等待不足，修正探针后再验证；第二轮发现并修复离店时的未购买商品历史差异，最终矩阵使用完成这些修改后的 DLL。
